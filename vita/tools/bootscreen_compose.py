#!/usr/bin/env python3
"""Kit #12 boot track (vita/notes/kit12-boot.md section 2): the boot-seq emblem set -- the loading screen recordings with the controller notice's
icon in the bottom-right corner, for the owner's boot sequence (SHEPHERD_K12_BOOTSEQ=1).

The owner's layout (2026-10-30): the game's own controller-notice icon (the 'Controller Recommended' screen of LoadMainMenu: the sprite
gamepad-modern-solid_EYE), moved to the BOTTOM-RIGHT corner as the mirror of the loading ring (bottom-left): the same edge distance, size and
baseline -- the SAME size as the ring, never smaller -- in the screen's red, with its USUAL animation (the notice's own entrance: the pop and the
colour sweep, ~1 s), then steady for the whole loading screen; no text ("connect a controller" dropped: a Vita always has its controls). The icon
is composited here, on the Mac, into every recording, so it costs nothing on the Vita:
  * the icon's pictures are the engine's own screenshots of the notice (vita/tools/bootscreen_record.sh's Mac run of level0, frames --icon-from
    .. --icon-to: its entrance, every second frame for the boot screen's 30 pictures a second, then the last one held), cut out around the icon
    (the notice draws it on black: the pixels are the icon over black), scaled by the ring's width over the icon's steady width (Lanczos), and
    recoloured to the screen's red (each channel times red / the icon's own steady colour: its flash stays a flash, in red);
  * placed as the ring's mirror on the owner's terms -- the same edge distance and the same BASELINE: the icon's steady pixels end on the
    same bottom row as the ring's pixels and as far from the right edge as the ring's are from the left (review fix: it was centred on the
    ring's middle, and the controller glyph is wider than tall, so it stood ~11 px above the ring's baseline). The ring's pixels are measured
    in the recordings (inside its box read from the engine, 21.6..108 x 11.2..97.6 px from the bottom-left, + 8 px), the glyph's in the
    icon's steady picture; after composing, the steady frame is measured again and the file is refused unless both edges match exactly
    (--nudge DX,DY, test only, shifts the icon: the negative control). Over the recording's black there (refused when that area is not black
    in every frame); icon-box rows / columns past the screen are dropped only when they are black in every icon picture;
  * the recording's intro is extended by the icon's entrance where the entrance is longer (the first loop pictures with the icon still moving);
    the loop then starts where the icon is steady -- the same 180 pictures, rotated, so it still loops exactly; lossless, with
    bootscreen_pack.py's playback check (no check, no file).
The output is GAME-DERIVED (the owner's copy, drawn): the owner folder only, never in git, never in a VPK.
usage: bootscreen_compose.py <icon shots dir> <in.shb>... -o <out dir> [--icon-from 543 --icon-to 606] [--ring-x0 21.6 --ring-x1 108 --ring-y0 11.2 --ring-y1 97.6]
       [--icon-box 430,223,100,76] [--icon-w 75] [--red 253,29,3] [--nudge DX,DY (test only: must be refused)]
Only reads its inputs; standard library + the local ffmpeg (no download)."""
import argparse, os, struct, subprocess, sys, zlib
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import bootscreen_set as bs
import bootscreen_pack as bp

W, H = 960, 544
ROW, FRAME = W * 4, W * 544 * 4

def run(cmd):
    r = subprocess.run(cmd, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
    if r.returncode: sys.stderr.write(r.stderr.decode("utf-8", "replace")[-2000:]); raise SystemExit(f"failed: {' '.join(cmd[:3])} ...")
    return r.stdout

def icon_frames(shots, f0, f1, box, scale, red):
    """the icon's pictures: cut, scaled, recoloured -- (w, h, [rgb bytes per picture])"""
    x, y, w, h = box
    ow, oh = round(w * scale), round(h * scale)
    nums = list(range(f0, f1 + 1, 2))
    if nums[-1] != f1: nums.append(f1)
    pics = []
    for n in nums:
        p = os.path.join(shots, f"f{n:05d}.png")
        if not os.path.exists(p): raise SystemExit(f"icon screenshot missing: {p}")
        pics.append(run(["ffmpeg", "-v", "error", "-i", p, "-vf", f"crop={w}:{h}:{x}:{y},scale={ow}:{oh}:flags=lanczos", "-f", "rawvideo", "-pix_fmt", "rgb24", "-"]))
    # the icon's own steady colour: the brightest pixels of the last (steady) picture
    last = pics[-1]
    px = sorted((last[i] + last[i + 1] + last[i + 2], i) for i in range(0, len(last), 3))
    top = [i for _, i in px[-max(1, len(px) // 50):]]
    sc = [max(1.0, sum(last[i + c] for i in top) / len(top)) for c in range(3)]
    gain = [red[c] / sc[c] for c in range(3)]
    out = []
    for p in pics:
        b = bytearray(len(p))
        for i in range(0, len(p), 3):
            for c in range(3): b[i + c] = min(255, int(p[i + c] * gain[c] + 0.5))
        out.append(bytes(b))
    return ow, oh, out, sc

def compose(fr, icon, at):
    """fr: whole-screen RGBA; icon: (w, h, rgb); at: (x, y) top-left -- the icon's pixels replace the (black) screen there"""
    w, h, rgb = icon
    x0, y0 = at
    b = bytearray(fr)
    for j in range(h):
        o = ((y0 + j) * W + x0) * 4
        for i in range(w):
            r, g, bb = rgb[(j * w + i) * 3:(j * w + i) * 3 + 3]
            b[o + 4 * i] = r; b[o + 4 * i + 1] = g; b[o + 4 * i + 2] = bb; b[o + 4 * i + 3] = 255
    return bytes(b)

LIT = 8   # a pixel of the glyph / the ring: some channel >= 8 (both are drawn on black)

def rgb_box(rgb, w, h):
    """the lit pixels' box in an rgb picture: (x0, x1, y0, y1) inclusive, or None"""
    xs, ys = [], []
    for j in range(h):
        row = rgb[j * w * 3:(j + 1) * w * 3]
        for i in range(w):
            if max(row[3 * i], row[3 * i + 1], row[3 * i + 2]) >= LIT: xs.append(i); ys.append(j)
    return (min(xs), max(xs), min(ys), max(ys)) if xs else None

def screen_box(frames, x0, y0, x1, y1):
    """the lit pixels' box over these whole-screen RGBA frames inside x0..x1-1, y0..y1-1: (x0, x1, y0, y1) inclusive, or None"""
    bx = None
    for fr in frames:
        for y in range(y0, y1):
            o = y * ROW
            for x in range(x0, x1):
                p = o + 4 * x
                if max(fr[p], fr[p + 1], fr[p + 2]) >= LIT:
                    bx = (x, x, y, y) if bx is None else (min(bx[0], x), max(bx[1], x), min(bx[2], y), max(bx[3], y))
    return bx

def clip_icon(pics, iw, ih, at):
    """drop the icon-box rows / columns that fall off the screen -- only when black in every icon picture (else refuse)"""
    x0, y0 = at
    cl, ct, cr, cb = max(0, -x0), max(0, -y0), max(0, x0 + iw - W), max(0, y0 + ih - H)
    if not (cl or ct or cr or cb): return pics, iw, ih, at
    nw, nh = iw - cl - cr, ih - ct - cb
    out = []
    for p in pics:
        for j in range(ih):
            for i in range(iw):
                if (i < cl or i >= iw - cr or j < ct or j >= ih - cb) and max(p[(j * iw + i) * 3:(j * iw + i) * 3 + 3]) >= LIT:
                    raise SystemExit(f"the icon's lit pixels fall off the screen at ({x0 + i},{y0 + j}): nothing written")
        out.append(b"".join(p[(j * iw + cl) * 3:(j * iw + cl + nw) * 3] for j in range(ct, ct + nh)))
    return out, nw, nh, (x0 + cl, y0 + ct)

def area_black(fr, x0, y0, w, h):
    for j in range(h):
        o = ((y0 + j) * W + x0) * 4
        row = fr[o:o + 4 * w]
        if any(row[k] or row[k + 1] or row[k + 2] for k in range(0, len(row), 4)): return False
    return True

def encode(frames, k0, rate, src_fnv):
    """bootscreen_pack.py's encoding and playback check, on whole frames (k0 = the loop's first frame)"""
    period = len(frames) - k0
    rects = [(0, 0, W, H)]
    for k in range(1, len(frames)):
        r = bp.bbox(frames[k], frames[k - 1])
        if k == k0: r = bp.union(r, bp.bbox(frames[k], frames[-1]))
        rects.append(r)
    payload = [None if r is None else bp.crop(frames[k], r) for k, r in enumerate(rects)]
    idx, data, max_raw = b"", b"", 0
    for k, r in enumerate(rects):
        ff = bp.fnv32(frames[k])
        if r is None: idx += struct.pack("<4H4I", 0, 0, 0, 0, 0, 0, 0, ff); continue
        z = zlib.compress(payload[k], 9)
        idx += struct.pack("<4H4I", r[0], r[1], r[2], r[3], len(data), len(z), bp.fnv32(payload[k]), ff); data += z
        max_raw = max(max_raw, len(payload[k]))
    bufs = [bytearray(FRAME), bytearray(FRAME)]
    order = list(range(len(frames))) + [k0 + i for i in range(period)] * 2
    prev = None
    for n, k in enumerate(order):
        buf = bufs[n & 1]
        for kk in ([prev] if prev is not None else []) + [k]:
            if rects[kk] is not None: bp.paste(buf, rects[kk], payload[kk])
        if bytes(buf) != frames[k]: raise SystemExit(f"the playback check failed at step {n} (frame {k}): nothing written")
        prev = k
    hdr = struct.pack("<15I", bp.MAGIC, 1, 64, W, H, len(frames), k0, rate, 1, 64, 64 + len(idx), len(data), max_raw, bp.fnv32(idx), src_fnv)
    hdr += struct.pack("<I", bp.fnv32(hdr))
    return hdr + idx + data, len(order)

def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("icon_shots"); ap.add_argument("shb", nargs="+"); ap.add_argument("-o", "--out", required=True)
    ap.add_argument("--icon-from", type=int, default=543); ap.add_argument("--icon-to", type=int, default=606)
    ap.add_argument("--icon-box", default="430,223,100,76"); ap.add_argument("--icon-w", type=float, default=75.0)
    ap.add_argument("--ring-x0", type=float, default=21.6); ap.add_argument("--ring-x1", type=float, default=108.0)
    ap.add_argument("--ring-y0", type=float, default=11.2); ap.add_argument("--ring-y1", type=float, default=97.6)
    ap.add_argument("--red", default="253,29,3")
    ap.add_argument("--nudge", default="0,0", help="TEST ONLY: shift the icon by DX,DY px -- the edge check must refuse it (negative control)")
    a = ap.parse_args()
    nudge = tuple(int(v) for v in a.nudge.split(","))
    box = tuple(int(v) for v in a.icon_box.split(","))
    red = [int(v) for v in a.red.split(",")]
    ring_w = a.ring_x1 - a.ring_x0
    scale = ring_w / a.icon_w                     # the same size as the ring, never smaller
    iw, ih, pics, steady = icon_frames(a.icon_shots, a.icon_from, a.icon_to, box, scale, red)
    g = rgb_box(pics[-1], iw, ih)                 # the glyph's steady pixels in the icon picture
    if g is None: raise SystemExit("the icon's steady picture has no lit pixel")
    # the ring's pixels: its box from the engine (bottom-left, + 8 px), measured in the first recording's loop (the ring rotates: the union)
    rx0, rx1 = max(0, int(a.ring_x0) - 8), min(W, int(a.ring_x1 + 0.999) + 8)
    ry0, ry1 = max(0, H - int(a.ring_y1 + 0.999) - 8), min(H, H - int(a.ring_y0) + 8)
    ring = None
    os.makedirs(a.out, exist_ok=True)
    E = len(pics)                                 # the entrance's pictures (the last one = steady)
    for path in a.shb:
        hd, rects = bs.read_shb(path)
        fr = list(bs.frames(hd, rects))
        k0 = hd["loop_start"]; period = len(fr) - k0
        rb = screen_box(fr[k0:], rx0, ry0, rx1, ry1)
        if rb is None: raise SystemExit(f"{path}: no ring pixels inside {rx0}..{rx1 - 1} x {ry0}..{ry1 - 1}: nothing written")
        if ring is None:
            ring = rb
            # the same edge distance and baseline: the glyph's last row = the ring's last row, its last column as far from the right edge as
            # the ring's first column is from the left
            want_r, want_b = (W - 1) - ring[0], ring[3]
            at = (want_r - g[1] + nudge[0], want_b - g[3] + nudge[1])
            pics, iw, ih, at = clip_icon(pics, iw, ih, at)
            g = rgb_box(pics[-1], iw, ih)
            if at[0] < 0 or at[1] < 0 or at[0] + iw > W or at[1] + ih > H: raise SystemExit(f"the icon ({iw} x {ih} at {at}) does not fit the screen")
        elif rb != ring: raise SystemExit(f"{path}: the ring's pixels {rb} differ from the first recording's {ring}: nothing written")
        for f in fr:
            if not area_black(f, at[0], at[1], iw, ih): raise SystemExit(f"{path}: the corner is not black in every frame: nothing written")
        intro = fr[:k0]; loop = fr[k0:]
        extra = max(0, E - 1 - len(intro))        # loop pictures still carrying the entrance
        out = [compose(f, (iw, ih, pics[min(i, E - 1)]), at) for i, f in enumerate(intro)]
        out += [compose(loop[j % period], (iw, ih, pics[min(len(intro) + j, E - 1)]), at) for j in range(extra)]
        k1 = len(out)
        out += [compose(loop[(extra + m) % period], (iw, ih, pics[E - 1]), at) for m in range(period)]
        # THE EDGE CHECK: in the composed steady frame, the icon's lit pixels (inside its box: black in the recording, area_black above) end on
        # the ring's bottom row and as far from the right edge as the ring's begin from the left -- exactly, or nothing is written
        ib = screen_box([out[-1]], at[0], at[1], at[0] + iw, at[1] + ih)
        if ib is None or ib[3] != ring[3] or (W - 1) - ib[1] != ring[0]:
            raise SystemExit(f"{path}: THE EDGE CHECK failed -- icon pixels {ib} vs the ring's {ring} (wanted the last row {ring[3]}, the last column {(W - 1) - ring[0]}): nothing written")
        body, shown = encode(out, k1, hd["rate_num"] // max(1, hd["rate_den"]), hd["src_fnv"])
        dst = os.path.join(a.out, os.path.basename(path))
        with open(dst + ".tmp", "wb") as f: f.write(body)
        os.replace(dst + ".tmp", dst)
        print(f"{os.path.basename(path)}: + the controller icon ({iw} x {ih} at {at[0]},{at[1]}: the ring's mirror -- edge check: its pixels end on row {ib[3]} = the ring's "
              f"baseline, {(W - 1) - ib[1]} px from the right = the ring's {ring[0]} from the left; x {scale:.3f} = the ring's width; the notice's frames "
              f"f{a.icon_from}..f{a.icon_to} every 2nd = {E} pictures, then held; recoloured from its own {tuple(round(c) for c in steady)} to {tuple(red)}); "
              f"intro {len(intro)} -> {k1} pictures, loop {period} (rotated by {extra}); {len(body)} B; playback check: {shown} frames shown, every one = its composed frame")

if __name__ == "__main__":
    main()
