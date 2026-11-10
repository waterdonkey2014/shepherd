#!/usr/bin/env python3
"""Kit #13 looks: the PC-capture picture check -- key Mac screens against the owner's PC captures, every kit, as a report with a threshold.

The owner's PC captures (the game on his PC, owner-local: never in git) are 1920x1080 frames, optionally under a 44 px window bar (1920x1124).
A Mac shot is the engine's 960x544 GL frame of the same screen (a scripted route, SHEPHERD_SHOT_FRAMES). The PC frame is brought to the Vita's
picture: the bar dropped, scaled by 544 / 1080 (the cameras keep their vertical field of view, so the world keeps its size per screen row) and
centre-cropped to 960 columns. Both are then box-filtered to 240 x 136 (4 x 4 Vita pixels per cell: the Stylizer's hatching, single-pixel
edges and text anti-aliasing average out) and compared in CIELAB (D65):

  dE     = the CIE76 colour difference per cell
  mean   = the mean dE over the screen (or a region), p90 = its 90th percentile, far = the % of cells with dE > 20 (a different colour)

A screen PASSes when its mean dE is at or under the manifest's threshold AND every region with its own threshold passes AND the picture is
in place. A region named "!name" is EXCLUDED from the whole-screen numbers (what the Vita build leaves out on purpose or cannot match: the PC's
mouse cursor, its keyboard prompts, the online store panels, the version text). A comparison that cannot run (a missing / unreadable file, a
frame of another size) is a FAIL, never a PASS.

In place (the alignment check): the whole-screen mean dE is also computed with the Mac picture moved by every whole number of cells up to
ALIGN_CELLS (4 Vita pixels a cell) in x and y (over the overlapping, non-excluded cells). The picture is MISPLACED -- a FAIL -- when the best
offset is 2 cells (8 px) or more away in either axis and beats the unmoved mean by ALIGN_GAIN dE or more. A mean dE alone does not see a
moved picture: a screen whose content is mostly flat colour keeps a low mean when it is moved (the title moved 24 px: mean 4.7), so a screen
with no region thresholds would PASS a misplaced picture without this check.

Negative control: `piccheck.py selftest` -- with the SAME verdict as `run` (the manifest row's threshold and regions with --manifest /
--screen, else threshold 12 and no regions: the weakest rule a new screen can have): the frame against itself must PASS; the frame moved
24 px across, moved 16 px down, recoloured and blanked must FAIL. `--no-align` turns the alignment check off (the selftest must then FAIL:
the check of the check).

Usage:
  piccheck.py compare PC.png MAC.png [--threshold T] [--region name=x,y,w,h[:T]]... [--out DIR] [--label L] [--no-align]
  piccheck.py run MANIFEST.tsv --pc-dir DIR --runs DIR [--arm NAME=RUNDIR]... [--out DIR] [--report FILE] [--no-align]
  piccheck.py selftest IMAGE.png [--manifest MANIFEST.tsv --screen NAME] [--no-align]

Manifest (TAB): name, pc file (in --pc-dir), the Mac shot (relative to a run dir), threshold, regions ("name=x,y,w,h[:T];..." in Vita
pixels, or -), what the screen is. Each --arm is one Mac run dir (e.g. kit12=<dir> k13=<dir>); every screen is compared for every arm, so
one report shows the kit #12 picture and the kit #13 picture against the same PC frame (before / after).
Output: the report (markdown) and, with --out, per screen and arm a side-by-side PNG (PC | Mac | dE heat map) -- owner-local (game pixels)."""
import sys, os, argparse
import numpy as np
from PIL import Image

W, H = 960, 544
CW, CH = 240, 136
ALIGN_CELLS = 10    # the alignment check's search: +-10 cells = +-40 Vita pixels in x and y
ALIGN_GAIN = 0.4    # dE: measured (kit #13 looks) -- the key screens' own best offsets are 0 or 1 cell with gains <= 0.30; Mac pictures moved
                    # 8-24 px give gains of 0.53-5.8 at the moved offset


def load_pc(path):
    im = Image.open(path).convert('RGB')
    w, h = im.size
    if (w, h) == (1920, 1124):
        im = im.crop((0, 44, 1920, 1124)); w, h = 1920, 1080
    if (w, h) == (W, H):
        return np.asarray(im)
    if h <= 0 or abs(w / h - 16 / 9) > 0.02:
        raise ValueError(f"{path}: {w}x{h} is not a 16:9 frame (or 1920x1124 with the window bar)")
    s = H / h; sw = round(w * s)
    im = im.resize((sw, H), Image.LANCZOS)
    x0 = (sw - W) // 2
    return np.asarray(im.crop((x0, 0, x0 + W, H)))


def load_mac(path):
    im = Image.open(path).convert('RGB')
    if im.size != (W, H):
        raise ValueError(f"{path}: {im.size[0]}x{im.size[1]}, not a {W}x{H} Mac shot")
    return np.asarray(im)


def cells_of(rgb):
    a = rgb.astype(np.float64).reshape(CH, H // CH, CW, W // CW, 3).mean(axis=(1, 3))
    return a / 255.0


def lab(c):
    c = np.where(c <= 0.04045, c / 12.92, ((c + 0.055) / 1.055) ** 2.4)
    m = np.array([[0.4124564, 0.3575761, 0.1804375], [0.2126729, 0.7151522, 0.0721750], [0.0193339, 0.1191920, 0.9503041]])
    xyz = c @ m.T / np.array([0.95047, 1.0, 1.08883])
    f = np.where(xyz > (6 / 29) ** 3, np.cbrt(xyz), xyz / (3 * (6 / 29) ** 2) + 4 / 29)
    return np.stack([116 * f[..., 1] - 16, 500 * (f[..., 0] - f[..., 1]), 200 * (f[..., 1] - f[..., 2])], axis=-1)


def de_map(pc, mac):
    return np.sqrt(((lab(cells_of(pc)) - lab(cells_of(mac))) ** 2).sum(axis=-1))


def stats(d):
    return dict(mean=float(d.mean()), p90=float(np.percentile(d, 90)), far=float((d > 20).mean() * 100))


def region_of(d, x, y, w, h):
    sx, sy = CW / W, CH / H
    x0, y0 = int(x * sx), int(y * sy); x1, y1 = max(x0 + 1, int(round((x + w) * sx))), max(y0 + 1, int(round((y + h) * sy)))
    return d[y0:y1, x0:x1]


def parse_regions(spec):
    out = []
    if not spec or spec == '-':
        return out
    for part in spec.replace(';', ' ').split():
        name, rest = part.split('=', 1)
        t = None
        if ':' in rest:
            rest, t = rest.split(':', 1); t = float(t)
        x, y, w, h = (int(v) for v in rest.split(','))
        out.append((name, x, y, w, h, t))
    return out


def keep_mask(regions):
    keep = np.ones((CH, CW), bool)
    sx, sy = CW / W, CH / H
    for (name, x, y, w, h, t) in regions:
        if name.startswith('!'):
            keep[int(y * sy):max(int(y * sy) + 1, int(round((y + h) * sy))), int(x * sx):max(int(x * sx) + 1, int(round((x + w) * sx)))] = False
    return keep


def align(pc, mac, keep, cells=ALIGN_CELLS):
    """The whole-screen mean dE with the Mac picture moved by (dx, dy) cells, every offset up to `cells`: (best dx px, best dy px, gain over the
    unmoved mean, unmoved mean, best mean, misplaced)."""
    a, b = lab(cells_of(pc)), lab(cells_of(mac))
    best, zero = None, None
    for dy in range(-cells, cells + 1):
        for dx in range(-cells, cells + 1):
            ys, xs = slice(max(0, dy), CH + min(0, dy)), slice(max(0, dx), CW + min(0, dx))
            yb, xb = slice(max(0, -dy), CH + min(0, -dy)), slice(max(0, -dx), CW + min(0, -dx))
            k = keep[ys, xs]
            if not k.any():
                continue
            m = float(np.sqrt(((a[ys, xs] - b[yb, xb]) ** 2).sum(axis=-1))[k].mean())
            if dx == 0 and dy == 0:
                zero = m
            if best is None or m < best[2] - 1e-9 or (abs(m - best[2]) <= 1e-9 and max(abs(dx), abs(dy)) < max(abs(best[0]), abs(best[1]))):
                best = (dx, dy, m)
    gain = zero - best[2]
    misplaced = max(abs(best[0]), abs(best[1])) >= 2 and gain >= ALIGN_GAIN
    return dict(dx=best[0] * (W // CW), dy=best[1] * (H // CH), gain=gain, zero=zero, best=best[2], misplaced=misplaced)


def side_by_side(pc, mac, d, path):
    heat = np.clip(d / 40.0, 0, 1)
    hm = np.zeros((CH, CW, 3)); hm[..., 0] = heat * 255; hm[..., 1] = (1 - np.abs(heat - 0.5) * 2) * 160
    hm = np.asarray(Image.fromarray(hm.astype('uint8')).resize((W, H), Image.NEAREST))
    Image.fromarray(np.concatenate([pc, mac, hm], axis=1)).save(path)


def evaluate(pc, mac, threshold, regions, use_align=True):
    """The verdict of one comparison (both frames as 960x544 RGB arrays): (ok, whole-screen stats, region stats, alignment or None, dE map)."""
    d = de_map(pc, mac)
    keep = keep_mask(regions)
    s = stats(d[keep])
    rs = []
    ok = s['mean'] <= threshold
    for (name, x, y, w, h, t) in regions:
        if name.startswith('!'):
            continue
        r = stats(region_of(d, x, y, w, h)); r['name'] = name; r['t'] = t
        r['ok'] = t is None or r['mean'] <= t
        ok = ok and r['ok']
        rs.append(r)
    al = align(pc, mac, keep) if use_align else None
    if al is not None and al['misplaced']:
        ok = False
    return ok, s, rs, al, d


def compare(pc_path, mac_path, threshold, regions, out=None, label='mac', use_align=True):
    pc, mac = load_pc(pc_path), load_mac(mac_path)
    ok, s, rs, al, d = evaluate(pc, mac, threshold, regions, use_align)
    if out:
        os.makedirs(out, exist_ok=True)
        side_by_side(pc, mac, d, os.path.join(out, f"{label}.png"))
    return ok, s, rs, al


def fmt_align(al):
    if al is None:
        return "not checked"
    where = f"best offset {al['dx']:+d},{al['dy']:+d} px (mean {al['best']:.1f} vs {al['zero']:.1f}, gain {al['gain']:.2f})"
    return ("MISPLACED: " if al['misplaced'] else "in place: ") + where


def fmt(s):
    return f"mean {s['mean']:.1f} / p90 {s['p90']:.1f} / far {s['far']:.1f} %"


def cmd_compare(a):
    try:
        ok, s, rs, al = compare(a.pc, a.mac, a.threshold, parse_regions(';'.join(a.region or [])), a.out, a.label, not a.no_align)
    except Exception as e:
        print(f"NOT COMPARED: {e}"); return 2
    print(f"{'PASS' if ok else 'FAIL'}: {fmt(s)} (threshold {a.threshold}); {fmt_align(al)}")
    for r in rs:
        print(f"  region {r['name']}: {fmt(r)}" + (f" (threshold {r['t']}: {'PASS' if r['ok'] else 'FAIL'})" if r['t'] is not None else ""))
    return 0 if ok else 1


def cmd_run(a):
    rows = []
    for line in open(a.manifest, encoding='utf-8'):
        line = line.rstrip('\n')
        if not line or line.startswith('#'):
            continue
        f = line.split('\t')
        if len(f) < 5:
            print(f"NOT COMPARED: bad manifest row: {line}"); return 2
        rows.append(f)
    if not rows:
        print("NOT COMPARED: the manifest lists no screen"); return 2
    arms = [x.split('=', 1) for x in (a.arm or [])] or [['mac', a.runs]]
    lines = ["| screen | arm | whole screen (dE) | verdict | regions | alignment |", "|---|---|---|---|---|---|"]
    bad = 0
    for f in rows:
        name, pcf, shot, thr, reg = f[0], f[1], f[2], float(f[3]), f[4]
        for arm, rundir in arms:
            try:
                ok, s, rs, al = compare(os.path.join(a.pc_dir, pcf), os.path.join(rundir, shot), thr, parse_regions(reg), os.path.join(a.out, name) if a.out else None, arm, not a.no_align)
            except Exception as e:
                lines.append(f"| {name} | {arm} | NOT COMPARED: {e} | FAIL | | |"); bad += 1; continue
            bad += 0 if ok else 1
            rt = "; ".join(f"{r['name']} {r['mean']:.1f}" + (f" (<= {r['t']:g}: {'ok' if r['ok'] else 'FAIL'})" if r['t'] is not None else "") for r in rs)
            lines.append(f"| {name} | {arm} | {fmt(s)} (<= {thr:g}) | {'PASS' if ok else 'FAIL'} | {rt} | {fmt_align(al)} |")
    rep = "\n".join(lines) + f"\n\nRESULT: {'PASS' if bad == 0 else f'FAIL ({bad})'}\n"
    if a.report:
        open(a.report, 'w', encoding='utf-8').write(rep)
    print(rep)
    return 0 if bad == 0 else 1


def shifted(img, dx, dy):
    """The picture moved by (dx, dy) pixels; the uncovered edge repeats the nearest row / column (no wrap-around)."""
    h, w = img.shape[:2]
    p = np.pad(img, ((abs(dy), abs(dy)), (abs(dx), abs(dx)), (0, 0)), mode='edge')
    return p[abs(dy) - dy:abs(dy) - dy + h, abs(dx) - dx:abs(dx) - dx + w].copy()


def cmd_selftest(a):
    base = load_mac(a.image) if Image.open(a.image).size == (W, H) else load_pc(a.image)
    thr, regions, rule = 12.0, [], "threshold 12, no regions (the weakest rule a new screen can have)"
    if a.manifest:
        if not a.screen:
            print("NOT COMPARED: --manifest needs --screen"); return 2
        rows = [l.rstrip('\n').split('\t') for l in open(a.manifest, encoding='utf-8') if l.strip() and not l.startswith('#')]
        row = next((f for f in rows if f[0] == a.screen), None)
        if row is None or len(row) < 5:
            print(f"NOT COMPARED: no screen '{a.screen}' in {a.manifest}"); return 2
        thr, regions, rule = float(row[3]), parse_regions(row[4]), f"the manifest's '{a.screen}' row (threshold {row[3]}, regions {row[4]})"
    use_align = not a.no_align
    print(f"verdict: {rule}; alignment check {'on' if use_align else 'OFF'}")
    fails = 0
    ok, s, rs, al, _ = evaluate(base, base, thr, regions, use_align)
    good = ok and s['mean'] == 0 and s['far'] == 0
    print(f"identity: {fmt(s)}; {fmt_align(al)} -> {'PASS' if good else 'FAIL'} (must PASS with 0)"); fails += 0 if good else 1
    for name, img in (("moved 24 px across", shifted(base, 24, 0)), ("moved 16 px down", shifted(base, 0, 16)), ("hue-swapped", base[..., ::-1].copy()), ("blank", np.zeros_like(base))):
        ok, s, rs, al, _ = evaluate(base, img, thr, regions, use_align)
        why = [] if ok else ([f"mean {s['mean']:.1f} > {thr:g}"] if s['mean'] > thr else []) + [f"region {r['name']}" for r in rs if not r['ok']] + (["misplaced"] if al is not None and al['misplaced'] else [])
        print(f"negative control, {name}: {fmt(s)}; {fmt_align(al)} -> {'FAILS as it must (' + ', '.join(why) + ')' if not ok else 'PASSED: the check is too weak'}"); fails += 0 if not ok else 1
    print(f"RESULT: {'PASS' if fails == 0 else f'FAIL ({fails})'}")
    return 0 if fails == 0 else 1


def main():
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sp = p.add_subparsers(dest='cmd', required=True)
    c = sp.add_parser('compare'); c.add_argument('pc'); c.add_argument('mac'); c.add_argument('--threshold', type=float, default=10.0)
    c.add_argument('--region', action='append'); c.add_argument('--out'); c.add_argument('--label', default='mac'); c.add_argument('--no-align', action='store_true')
    r = sp.add_parser('run'); r.add_argument('manifest'); r.add_argument('--pc-dir', required=True); r.add_argument('--runs', default='.')
    r.add_argument('--arm', action='append'); r.add_argument('--out'); r.add_argument('--report'); r.add_argument('--no-align', action='store_true')
    t = sp.add_parser('selftest'); t.add_argument('image'); t.add_argument('--manifest'); t.add_argument('--screen'); t.add_argument('--no-align', action='store_true')
    a = p.parse_args()
    return {'compare': cmd_compare, 'run': cmd_run, 'selftest': cmd_selftest}[a.cmd](a)


if __name__ == '__main__':
    sys.exit(main())
