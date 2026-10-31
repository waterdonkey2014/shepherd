#!/usr/bin/env python3
"""Device kit #10, track boot (vita/notes/kit10-boot.md): the game's own LOADING SCREEN, lossless, for the boot screen (.shb).

The boot screen (vita/shepherd/k10_boot.inc.c) shows the loading screen the game shows while it loads a scene -- MMTools.MMTransition: the
pentagram fading in over its black, the crown with its rings turning a fixed step every frame -- during the ~2 minutes the engine boots
behind a black screen. This tool makes it from the ENGINE'S OWN SCREENSHOTS of that screen (vita/tools/bootscreen_record.sh runs the Mac
engine on the owner's own copy -- the New Game route at the Vita profile's 960 x 544 with the GL backend, the Vita's own draw path -- with a
screenshot per frame around the 'Game Biome Intro' load), so every pixel is the game's own art exactly as Shepherd draws it:
  * the LOOP: the smallest frame L whose next P frames come back P frames later (P = 180 by default: the crown's rings turn -2 and +4 degrees
    a frame) -- within --tol differing pixels a pair: the game adds the steps to a float and the rotation by a and by a + 360 degrees differ
    in the last bits of sin / cos, so a few edge pixels of the rings land one step apart a period later (18 pixels at most on the owner's
    copy); the loop is the frames L .. L + P - 1 as drawn;
  * the INTRO: the frames from the first one after the screen was fully black (the pentagram starting to fade in) up to L, every second one
    kept, so the fade (the game's DOFade over 0.5 s of REAL time: 60 frames a second on the Mac's frame clock) takes the same real time at
    the boot screen's 30 pictures a second, while the loop keeps every frame (the rings turn a step per FRAME, as the game's do);
  * LOSSLESS: frame 0 is the whole screen, every other frame only the rectangle that differs from the frame before it (the loop's first frame
    also covers what differs from the last), each rectangle's RGBA zlib-compressed (vita/shepherd/k10_shb.h); the tool plays the file back
    the way the Vita's player does (two display buffers, three times round the loop) and checks that every shown frame equals its screenshot
    byte for byte -- no check, no output. (H.264 was tried first: 4:2:0 chroma blurs the pentagram's 1-pixel red lines to ~36 dB PSNR.)
The output is GAME-DERIVED (the owner's own copy, drawn): the owner folder only, never in git, never in a VPK, never shared.
usage: bootscreen_pack.py <shots dir> -o <out dir> [--period 180] [--tol 64] [--rate 30] [--name BootLoading]
Only reads its inputs; standard library + the local ffmpeg (its PNG decoder; no download)."""
import argparse, os, re, struct, subprocess, sys, zlib

W, H = 960, 544
ROW = W * 4
FRAME = ROW * H
MAGIC = 0x31424853

def fnv32(b, h=2166136261):
    for x in b:
        h = ((h ^ x) * 16777619) & 0xffffffff
    return h

def run(cmd):
    r = subprocess.run(cmd, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
    if r.returncode:
        sys.stderr.write(r.stderr.decode("utf-8", "replace")[-2000:]); raise SystemExit(f"failed: {' '.join(cmd[:3])} ...")
    return r.stdout

def frames_of(shots):
    fs = sorted(int(m.group(1)) for f in os.listdir(shots) for m in [re.match(r"f(\d+)\.png$", f)] if m)
    if not fs: raise SystemExit(f"no fNNNNN.png screenshots in {shots}")
    best, cur = (0, 1), 0   # the longest run of consecutive frame numbers (the boot loop also shoots frame 1)
    for i in range(1, len(fs) + 1):
        if i == len(fs) or fs[i] != fs[i - 1] + 1:
            if i - cur > best[1] - best[0]: best = (cur, i)
            cur = i
    return fs[best[0]:best[1]]

def raw_frames(shots, nums):
    """the screenshots (consecutive numbers) as raw RGBA -- ffmpeg's PNG decoder, exact -- one bytes object per frame"""
    raw = run(["ffmpeg", "-v", "error", "-f", "image2", "-start_number", str(nums[0]), "-i", os.path.join(shots, "f%05d.png"),
               "-frames:v", str(len(nums)), "-f", "rawvideo", "-pix_fmt", "rgba", "-"])
    if len(raw) != FRAME * len(nums): raise SystemExit(f"the screenshots are not all {W} x {H} RGBA ({len(raw)} B for {len(nums)} frames)")
    return [raw[i * FRAME:(i + 1) * FRAME] for i in range(len(nums))]

def black(fr): return all(fr[c::4].count(0) == W * H for c in range(3))

def ndiff(a, b, cap):
    """pixels that differ (counting stops above cap)"""
    if a == b: return 0
    n = 0
    for y in range(H):
        ra, rb = a[y * ROW:(y + 1) * ROW], b[y * ROW:(y + 1) * ROW]
        if ra == rb: continue
        for x in range(0, ROW, 4):
            if ra[x:x + 4] != rb[x:x + 4]:
                n += 1
                if n > cap: return n
    return n

def find_loop(fr, period, tol):
    for L in range(0, len(fr) - 2 * period + 1):
        if black(fr[L]): continue
        worst = 0
        for k in range(period):
            d = ndiff(fr[L + k], fr[L + period + k], tol)
            if d > tol: break
            worst = max(worst, d)
        else:
            return L, worst
    return -1, 0

def bbox(a, b):
    """the rectangle (x, y, w, h) of the pixels that differ; None = identical"""
    if a == b: return None
    y0 = y1 = None; x0, x1 = W, -1
    for y in range(H):
        ra, rb = a[y * ROW:(y + 1) * ROW], b[y * ROW:(y + 1) * ROW]
        if ra == rb: continue
        if y0 is None: y0 = y
        y1 = y
        i = 0
        while ra[i:i + 4] == rb[i:i + 4]: i += 4
        j = ROW - 4
        while ra[j:j + 4] == rb[j:j + 4]: j -= 4
        x0 = min(x0, i // 4); x1 = max(x1, j // 4)
    return (x0, y0, x1 - x0 + 1, y1 - y0 + 1)

def union(r, s):
    if r is None: return s
    if s is None: return r
    x0, y0 = min(r[0], s[0]), min(r[1], s[1]); x1, y1 = max(r[0] + r[2], s[0] + s[2]), max(r[1] + r[3], s[1] + s[3])
    return (x0, y0, x1 - x0, y1 - y0)

def crop(fr, r):
    x, y, w, h = r
    return b"".join(fr[(y + j) * ROW + x * 4:(y + j) * ROW + (x + w) * 4] for j in range(h))

def paste(buf, r, raw):
    x, y, w, h = r
    for j in range(h): buf[(y + j) * ROW + x * 4:(y + j) * ROW + (x + w) * 4] = raw[j * w * 4:(j + 1) * w * 4]

def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("shots"); ap.add_argument("-o", "--out", required=True)
    ap.add_argument("--period", type=int, default=180); ap.add_argument("--tol", type=int, default=64)
    ap.add_argument("--rate", type=int, default=30); ap.add_argument("--name", default="BootLoading")
    a = ap.parse_args()
    nums = frames_of(a.shots)
    fr = raw_frames(a.shots, nums)
    L, worst = find_loop(fr, a.period, a.tol)
    if L < 0: raise SystemExit(f"no loop of {a.period} frames in the screenshots f{nums[0]}..f{nums[-1]} (the loading screen must show for 2 periods)")
    b = L
    while b > 0 and not black(fr[b - 1]): b -= 1
    if b == 0: raise SystemExit(f"no fully black frame before the loading screen (f{nums[L]}): the shots must start before it")
    sel = list(range(b, L, 2)) + list(range(L, L + a.period))
    k0 = len(range(b, L, 2))                       # the loop's first frame in the file
    frames = [fr[i] for i in sel]
    rects = [(0, 0, W, H)]
    for k in range(1, len(frames)):
        r = bbox(frames[k], frames[k - 1])
        if k == k0: r = union(r, bbox(frames[k], frames[-1]))
        rects.append(r)
    payload = [None if r is None else crop(frames[k], r) for k, r in enumerate(rects)]
    idx, data, max_raw = b"", b"", 0
    for k, r in enumerate(rects):
        ff = fnv32(frames[k])   # the whole frame as shown: the player's check mode (k10_boot.inc.c)
        if r is None: idx += struct.pack("<4H4I", 0, 0, 0, 0, 0, 0, 0, ff); continue
        z = zlib.compress(payload[k], 9)
        idx += struct.pack("<4H4I", r[0], r[1], r[2], r[3], len(data), len(z), fnv32(payload[k]), ff); data += z
        max_raw = max(max_raw, len(payload[k]))
    # the player's check (k10_boot.inc.c does the same): two display buffers that start black; picture n goes into buffer n & 1, which shows
    # the picture before last, by drawing the previous picture's rectangle and then its own; three times round the loop -- every shown frame
    # must be its screenshot byte for byte
    bufs = [bytearray(FRAME), bytearray(FRAME)]
    order = list(range(len(frames))) + [k0 + i for i in range(a.period)] * 2
    prev = None
    for n, k in enumerate(order):
        buf = bufs[n & 1]
        for kk in ([prev] if prev is not None else []) + [k]:
            if rects[kk] is not None: paste(buf, rects[kk], zlib.decompress(zlib.compress(payload[kk], 9)) if n < 2 else payload[kk])
        if bytes(buf) != frames[k]: raise SystemExit(f"the playback check failed at step {n} (frame {k} of the file): nothing written")
        prev = k
    hdr = struct.pack("<15I", MAGIC, 1, 64, W, H, len(frames), k0, a.rate, 1, 64, 64 + len(idx), len(data), max_raw, fnv32(idx), fnv32(fr[L][:1 << 20]))
    hdr += struct.pack("<I", fnv32(hdr))
    assert len(hdr) == 64
    os.makedirs(a.out, exist_ok=True)
    out = os.path.join(a.out, a.name + ".shb")
    with open(out + ".tmp", "wb") as f: f.write(hdr + idx + data)
    os.replace(out + ".tmp", out)
    loop_rects = [r for r in rects[k0:] if r]
    print(f"{a.name}.shb: the loading screen from the engine's screenshots: intro f{nums[b]}..f{nums[L] - 1} ({L - b} frames, every 2nd kept: {k0}), "
          f"loop f{nums[L]}..f{nums[L + a.period - 1]} ({a.period} frames; each comes back a period later within {worst} pixel(s), --tol {a.tol}); "
          f"{len(frames)} frames at {a.rate}/1 ({len(frames) / a.rate:.2f} s, the loop {a.period / a.rate:.2f} s); lossless: frame 0 the whole screen, "
          f"the loop's rectangles up to {max((r[2] * r[3] for r in loop_rects), default=0)} pixels, the largest rectangle {max_raw} B raw; zlib {len(data)} B; "
          f"file {len(hdr) + len(idx) + len(data)} B; playback check: {len(order)} frames shown, every one = its screenshot")
    return 0

if __name__ == "__main__":
    sys.exit(main())
