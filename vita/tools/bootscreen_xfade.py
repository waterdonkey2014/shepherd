#!/usr/bin/env python3
"""bootscreen_xfade.py -- SHEPHERD-VITA device kit #13, track bootc (vita/notes/kit13-bootc.md section 5): THE BOOT LOADING SCREEN'S EMBLEM
CROSSFADE, the owner's design (2026-11-05): ~8 s per emblem, a 1 s crossfade, shuffled with no repeats until all have shown, only the emblems
the save allows -- built as ONE shared stream + ONE still per emblem, blending only the emblem's box.

The tool is generic; its INPUTS AND OUTPUTS are the owner's own recordings (an emblem set made by bootscreen_record.sh / bootscreen_set.py from
the owner's copy of the game): owner-local, never in git or a VPK.

What it checks first (any failure: nothing written, exit 1):
  * every recording of the set has the same size, frame count, loop start and rate (the kit #12 recordings: 196 frames at 30/s, loop 16);
  * at every frame, every pixel OUTSIDE the box is the same in every recording (the recordings are frame-locked: one stream serves them all);
  * in every recording the box does not change from the loop start on (no change rectangle of a loop frame touches the box, and the last
    frame's box = the loop start's: the loop is seamless with any still in the box).
What it writes into the set's folder:
  * XFADE.txt -- "xfade 1", the stream (one of the set's own recordings, by file name and its header checksum), the box, the timing, one line
    per emblem: "emblem <name> <file.shx> <group> <checksum>" (group base / woolhaven / coop: the host shows a group only when the save
    allows it; every emblem recorded so far is the base game's);
  * xf-<name>.shx per emblem: the box (x, y, w, h) of the recording's frames 0 .. loop_start (the intro's fade-in, then the still), each a
    zlib stream of the box's RGBA (k10_shb.h's conventions: rows top-down, w * 4 bytes a row), with the CRC-32 (zlib's) of each box;
  * xf-check.bin -- per stream frame the CRC-32 of the frame with the box left out (the host's CHECK mode: SHEPHERD_K10_BOOTSCREEN_CHECK=1).
The host (vita/shepherd/k13_xfade.inc.c) plays the stream as before and draws the box itself: the first emblem's own intro, then its still,
then every hold - fade pictures a blend of the current still into the next.

usage: bootscreen_xfade.py make <set dir> [--stream FILE] [--box X0,Y0,X1,Y1] [--hold PICTURES] [--fade PICTURES] [--group NAME=GROUP ...]
       bootscreen_xfade.py check <set dir>           (re-read what make wrote: every .shx box = its recording's, the check file = the stream's)
       bootscreen_xfade.py --self-test               (synthetic recordings: the checks catch a box change in the loop, a pixel outside the box)
"""
import os, struct, sys, zlib

W, H = 960, 544
BOX_DEFAULT = (355, 147, 604, 396)   # x0, y0, x1, y1 inclusive (kit #12's recordings: the emblem's box, measured on the recordings)
SHX_MAGIC = 0x31584853               # "SHX1"
XFC_MAGIC = 0x31434658               # "XFC1"

def fnv32(b, h=2166136261):
    for x in b:
        h = ((h ^ x) * 16777619) & 0xFFFFFFFF
    return h

class Shb:
    def __init__(self, path):
        b = open(path, 'rb').read()
        hd = struct.unpack('<16I', b[:64])
        (self.magic, ver, hs, self.w, self.h, self.frames, self.loop, self.rn, self.rd, ioff, doff, dbytes, maxraw, ifnv, sfnv, self.checksum) = hd
        if self.magic != 0x31424853 or ver != 1 or hs != 64 or self.w != W or self.h != H:
            raise SystemExit(f"{path}: not a 960 x 544 .shb boot screen file")
        self.rects = [struct.unpack('<4H4I', b[ioff + 24 * i: ioff + 24 * i + 24]) for i in range(self.frames)]
        self.b, self.doff, self.path = b, doff, path
    def frames_rgba(self):
        fb = bytearray(W * H * 4)
        for (x, y, rw, rh, off, ln, rf, ff) in self.rects:
            if rw:
                raw = zlib.decompress(self.b[self.doff + off: self.doff + off + ln])
                for r in range(rh):
                    fb[((y + r) * W + x) * 4: ((y + r) * W + x + rw) * 4] = raw[r * rw * 4:(r + 1) * rw * 4]
            yield bytes(fb)

def box_of(fb, box):
    x0, y0, x1, y1 = box
    return b''.join(fb[(y * W + x0) * 4:(y * W + x1 + 1) * 4] for y in range(y0, y1 + 1))

def outside_crc(fb, box):
    """CRC-32 (zlib's) of the frame in row-major order with the box's pixels left out (the host's check: zlib's crc32 over the same bytes)"""
    x0, y0, x1, y1 = box
    c = zlib.crc32(fb[:y0 * W * 4])
    for y in range(y0, y1 + 1):
        c = zlib.crc32(fb[y * W * 4:(y * W + x0) * 4], c)
        c = zlib.crc32(fb[(y * W + x1 + 1) * 4:(y + 1) * W * 4], c)
    return zlib.crc32(fb[(y1 + 1) * W * 4:], c)

def touches(rect, box):
    x, y, w, h = rect[:4]
    return w and h and not (x > box[2] or x + w - 1 < box[0] or y > box[3] or y + h - 1 < box[1])

def read_set(d):
    t = open(os.path.join(d, 'SET.txt')).read().splitlines()
    em = []
    for line in t:
        p = line.split()
        if len(p) == 5 and p[0] == 'emblem':
            em.append((p[2], p[1]))   # (name, file)
    if not em:
        raise SystemExit(f"{d}/SET.txt: no emblem lines")
    return em

def make(d, stream=None, box=BOX_DEFAULT, hold=240, fade=30, groups=None):
    em = read_set(d)
    recs = {n: Shb(os.path.join(d, f)) for n, f in em}
    files = dict(em)
    names = [n for n, _ in em]
    a = recs[names[0]]
    for n in names[1:]:
        r = recs[n]
        if (r.frames, r.loop, r.rn, r.rd) != (a.frames, a.loop, a.rn, a.rd):
            raise SystemExit(f"{files[n]}: {r.frames} frames loop {r.loop} rate {r.rn}/{r.rd} -- not {a.frames} / {a.loop} / {a.rn}/{a.rd} as {files[names[0]]}")
    sname = stream or files[names[0]]
    sn = next((n for n, f in em if f == sname), None)
    if sn is None:
        raise SystemExit(f"--stream {sname} is not one of the set's recordings")
    for n in names:   # the box is static in the loop: no loop rectangle touches it
        bad = [k for k in range(a.loop, a.frames) if touches(recs[n].rects[k], box)]
        if bad:
            raise SystemExit(f"{files[n]}: loop frame(s) {bad[:5]} change the box -- the emblem is not a still in the loop (no crossfade set written)")
    gens = {n: recs[n].frames_rgba() for n in names}
    boxes = {n: [] for n in names}
    check = []
    for k in range(a.frames):
        fr = {n: next(gens[n]) for n in names}
        ref = outside_crc(fr[sn], box)
        for n in names:
            if n != sn and outside_crc(fr[n], box) != ref:
                raise SystemExit(f"{files[n]} frame {k}: pixels outside the box differ from the stream's ({sname}) -- not frame-locked (no crossfade set written)")
            if k <= a.loop:
                boxes[n].append(box_of(fr[n], box))
            elif k == a.frames - 1 and box_of(fr[n], box) != boxes[n][a.loop]:
                raise SystemExit(f"{files[n]}: the last frame's box differs from the loop start's (no crossfade set written)")
        check.append(ref)
    bw, bh = box[2] - box[0] + 1, box[3] - box[1] + 1
    out = []
    for n in names:
        idx, data = [], b''
        for bx in boxes[n]:
            z = zlib.compress(bx, 9)
            idx.append((len(data), len(z), zlib.crc32(bx)))
            data += z
        nf = len(boxes[n])
        ioff = 64; doff = ioff + nf * 12
        hdr = struct.pack('<12I', SHX_MAGIC, 1, 64, box[0], box[1], bw, bh, nf, ioff, doff, len(data), recs[n].checksum)
        hdr += struct.pack('<I', idx[-1][2])                 # the still's CRC-32
        hdr += struct.pack('<2I', 0, 0)
        hdr += struct.pack('<I', fnv32(hdr))                 # checksum of the 60 bytes before it
        assert len(hdr) == 64
        body = hdr + b''.join(struct.pack('<3I', *e) for e in idx) + data
        fn = f"xf-{n}.shx"
        open(os.path.join(d, fn), 'wb').write(body)
        out.append((n, fn, (groups or {}).get(n, 'base'), fnv32(body)))
    xc = struct.pack('<4I', XFC_MAGIC, 1, a.frames, fnv32(struct.pack(f'<{len(check)}I', *check))) + struct.pack(f'<{len(check)}I', *check)
    open(os.path.join(d, 'xf-check.bin'), 'wb').write(xc)
    lines = ["# the boot screen's emblem crossfade (vita/tools/bootscreen_xfade.py, device kit #13): one shared stream + one still per emblem, the box blended",
             "xfade 1", f"stream {sname} {recs[sn].checksum:08x}", f"box {box[0]} {box[1]} {bw} {bh}", f"frames {a.frames} loop {a.loop} rate {a.rn} {a.rd}",
             f"hold {hold}", f"fade {fade}", f"check xf-check.bin {fnv32(xc):08x}"] + [f"emblem {n} {fn} {g} {ck:08x}" for n, fn, g, ck in out]
    open(os.path.join(d, 'XFADE.txt'), 'w', newline='\n').write('\n'.join(lines) + '\n')
    tot = sum(os.path.getsize(os.path.join(d, fn)) for _, fn, _, _ in out)
    print(f"XFADE: {len(out)} emblems, stream {sname} ({os.path.getsize(os.path.join(d, sname))} B), stills + intros {tot} B ({', '.join(f'{n} {os.path.getsize(os.path.join(d, fn))}' for n, fn, _, _ in out)}); "
          f"box {box} {bw}x{bh}; frames {a.frames} loop {a.loop}; every frame outside the box = the stream's in every recording; the box static in the loop")
    return 0

def check(d):
    t = dict()
    em = []
    for line in open(os.path.join(d, 'XFADE.txt')).read().splitlines():
        p = line.split()
        if not p or p[0].startswith('#'):
            continue
        if p[0] == 'emblem':
            em.append(p[1:])
        else:
            t[p[0]] = p[1:]
    sets = dict(read_set(d))
    box = tuple(int(x) for x in t['box']); box = (box[0], box[1], box[0] + box[2] - 1, box[1] + box[3] - 1)
    s = Shb(os.path.join(d, t['stream'][0]))
    xc = open(os.path.join(d, 'xf-check.bin'), 'rb').read()
    ref = struct.unpack(f'<{s.frames}I', xc[16:16 + 4 * s.frames])
    bad = 0
    for k, fb in enumerate(s.frames_rgba()):
        if outside_crc(fb, box) != ref[k]:
            bad += 1
    for n, fn, g, ck in em:
        body = open(os.path.join(d, fn), 'rb').read()
        if fnv32(body) != int(ck, 16):
            print(f"FAIL {fn}: its checksum differs from XFADE.txt"); bad += 1; continue
        hd = struct.unpack('<16I', body[:64]); nf, ioff, doff = hd[7], hd[8], hd[9]
        r = Shb(os.path.join(d, sets[n]))
        for k, fb in enumerate(r.frames_rgba()):
            if k >= nf: break
            off, ln, f = struct.unpack('<3I', body[ioff + 12 * k: ioff + 12 * k + 12])
            if zlib.decompress(body[doff + off: doff + off + ln]) != box_of(fb, box) or zlib.crc32(box_of(fb, box)) != f:
                print(f"FAIL {fn}: box {k} differs from {sets[n]}"); bad += 1
    print("XFADE CHECK " + ("OK" if not bad else f"FAIL ({bad})"))
    return 1 if bad else 0

def self_test():
    import tempfile, shutil
    def shb(path, frames_rgba, loop):
        # a tiny .shb writer (whole-frame rectangles where anything changed; enough for the checks)
        idx, data, prev = [], b'', None
        for i, f in enumerate(frames_rgba):
            if prev is None:
                rect = (0, 0, W, H); raw = f
            elif f == prev:
                rect = (0, 0, 0, 0); raw = b''
            else:
                rows = [y for y in range(H) if f[y * W * 4:(y + 1) * W * 4] != prev[y * W * 4:(y + 1) * W * 4]]
                y0, y1 = rows[0], rows[-1]; rect = (0, y0, W, y1 - y0 + 1); raw = f[y0 * W * 4:(y1 + 1) * W * 4]
            z = zlib.compress(raw, 1) if raw else b''
            idx.append(struct.pack('<4H4I', *rect, len(data), len(z), 0, 0)); data += z; prev = f
        ix = b''.join(idx); n = len(frames_rgba)
        hd = struct.pack('<15I', 0x31424853, 1, 64, W, H, n, loop, 30, 1, 64, 64 + 24 * n, len(data), W * H * 4, fnv32(ix), 0)
        hd += struct.pack('<I', fnv32(hd))
        open(path, 'wb').write(hd + ix + data)
    def frame(k, emblem, fade_in=3, outside_bug=False):
        f = bytearray(W * H * 4)
        for y in range(0, H, 8):   # a moving band outside the box (the rings' stand-in)
            pass
        y = 20 + (k % 6); f[(y * W) * 4:(y * W + 40) * 4] = b'\x80\x10\x10\xff' * 40
        a = min(k, fade_in) / fade_in
        for yy in range(200, 210):
            for xx in range(400, 410):
                f[(yy * W + xx) * 4:(yy * W + xx) * 4 + 4] = bytes((int(emblem * a) & 255, 0, 0, 255))
        if outside_bug and k == 7: f[0:4] = b'\x01\x02\x03\xff'
        return bytes(f)
    tmp = tempfile.mkdtemp(prefix='xfade-selftest-')
    try:
        ok = True
        for case, want in (('good', 0), ('outside', 1), ('loopbox', 1)):
            d = os.path.join(tmp, case); os.makedirs(d)
            lines = ["shb-set 1", "rings drawn"]
            for e, val in (('A', 200), ('B', 120)):
                fr = [frame(k, val, outside_bug=(case == 'outside' and e == 'B')) for k in range(12)]
                if case == 'loopbox' and e == 'A': fr[9] = frame(9, 90)
                shb(os.path.join(d, f'rec-{e}.shb'), fr, 4)
                lines.append(f"emblem rec-{e}.shb {e} 1 {Shb(os.path.join(d, f'rec-{e}.shb')).checksum:08x}")
            open(os.path.join(d, 'SET.txt'), 'w').write('\n'.join(lines) + '\n')
            try:
                rc = make(d, box=(395, 195, 414, 214))
                rc = check(d) if rc == 0 else rc
            except SystemExit as e:
                print(f"  ({case}: {e})"); rc = 1
            got = 1 if rc else 0
            print(f"self-test {case}: {'refused' if got else 'made + checked'} -- {'as wanted' if got == want else 'WRONG'}")
            ok &= got == want
        print("SELF-TEST " + ("PASS" if ok else "FAIL"))
        return 0 if ok else 1
    finally:
        shutil.rmtree(tmp)

def main(a):
    if a[:1] == ['--self-test']:
        return self_test()
    if len(a) >= 2 and a[0] == 'make':
        kw = {}; groups = {}
        i = 2
        while i < len(a):
            if a[i] == '--stream': kw['stream'] = a[i + 1]; i += 2
            elif a[i] == '--box': kw['box'] = tuple(int(x) for x in a[i + 1].split(',')); i += 2
            elif a[i] == '--hold': kw['hold'] = int(a[i + 1]); i += 2
            elif a[i] == '--fade': kw['fade'] = int(a[i + 1]); i += 2
            elif a[i] == '--group': k, v = a[i + 1].split('='); groups[k] = v; i += 2
            else: raise SystemExit(f"unknown option {a[i]}")
        return make(a[1], groups=groups, **kw)
    if len(a) == 2 and a[0] == 'check':
        return check(a[1])
    print(__doc__); return 2

if __name__ == '__main__':
    sys.exit(main(sys.argv[1:]))
