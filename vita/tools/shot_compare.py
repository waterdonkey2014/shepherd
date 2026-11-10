#!/usr/bin/env python3
"""Kit #13 fixlooks (vita/notes/kit13-fixlooks.md section 1): a device screenshot (SHEPHERD_SHOT_FRAMES, ux0:data/shepherd/shots/fNNNNN.png) against the Mac's picture of the
same screen: the exact-pixel answer a phone photo cannot give.

The device's PNG comes from vitaGL's glReadPixels, which hands rows back in the opposite order to a desktop GL for at least the display (its FBO path copies memory row 0 into the
LAST row): whether a device shot comes out upside down has never been seen, so both orders are tried and the better one is named. The screen animates (the title's crowd wobbles),
so a few percent of differing pixels is normal: the numbers that decide are the colours. After the Stylizer every world pixel is a PALETTE colour, so the two pictures' colour sets
must agree: a colour the device shows in bulk that the Mac never shows is a real difference, and the bulk colours' own levels are read directly.

usage: shot_compare.py <device.png> <mac.png> [--top N] [--box name=x,y,w,h ...]    (default boxes: the sky, the near crowd, the logo's two halves)
Reads only; standard library + the local ffmpeg (no download). Exit 0 = the same colours (the bulk colours within 2 levels), 1 = they differ, 2 = could not compare."""
import collections, subprocess, sys

W, H = 960, 544
BOXES = {"sky": (40, 20, 880, 120), "near crowd": (225, 485, 95, 50), "logo top": (340, 240, 90, 40), "logo bottom": (340, 295, 90, 20)}


def rgb(path):
    r = subprocess.run(["ffmpeg", "-v", "error", "-i", path, "-f", "rawvideo", "-pix_fmt", "rgb24", "-"], stdout=subprocess.PIPE, stderr=subprocess.PIPE)
    if r.returncode or len(r.stdout) != W * H * 3:
        raise SystemExit(f"{path}: not a {W} x {H} picture ({r.stderr.decode('utf-8', 'replace')[-200:]})")
    return r.stdout


def flip(b):
    return b"".join(b[(H - 1 - y) * W * 3:(H - y) * W * 3] for y in range(H))


def colours(b):
    return collections.Counter(b[i:i + 3] for i in range(0, len(b), 3))


def box_mean(b, box):
    x0, y0, w, h = box
    s = [0, 0, 0]
    for y in range(y0, y0 + h):
        row = b[(y * W + x0) * 3:(y * W + x0 + w) * 3]
        for c in range(3):
            s[c] += sum(row[c::3])
    return tuple(round(v / (w * h), 1) for v in s)


def differ(a, b):
    return sum(1 for i in range(0, len(a), 3) if a[i:i + 3] != b[i:i + 3])


def main():
    args = sys.argv[1:]
    top = 12
    boxes = dict(BOXES)
    pos = []
    while args:
        x = args.pop(0)
        if x == "--top":
            top = int(args.pop(0))
        elif x == "--box":
            n, v = args.pop(0).split("=")
            boxes[n] = tuple(int(k) for k in v.split(","))
        else:
            pos.append(x)
    if len(pos) != 2:
        print(__doc__)
        return 2
    dev, mac = rgb(pos[0]), rgb(pos[1])
    dup = flip(dev)
    d0, d1 = differ(dev, mac), differ(dup, mac)
    upside = d1 < d0
    if upside:
        dev = dup
    n = W * H
    print(f"rows: {'BOTTOM-FIRST (the device shot was upside down: compared flipped)' if upside else 'top-first'} ({min(d0, d1)} vs {max(d0, d1)} pixels differ)")
    print(f"pixels that differ: {min(d0, d1)} of {n} ({100.0 * min(d0, d1) / n:.2f} %; the title's crowd moves, so this is not the verdict)")
    cd, cm = colours(dev), colours(mac)
    print(f"{'colour':>17} {'device':>8} {'Mac':>8}")
    for c, k in cd.most_common(top):
        print(f"{tuple(c)!s:>17} {k:>8} {cm.get(c, 0):>8}")
    only = [(c, k) for c, k in cd.most_common(top) if not cm.get(c) and k > n // 1000]
    for name, box in boxes.items():
        print(f"mean {name:<12} device {box_mean(dev, box)}  Mac {box_mean(mac, box)}")
    near = 0
    for c, k in only:
        if any(max(abs(c[i] - m[i]) for i in range(3)) <= 2 for m, _ in cm.most_common(top)):
            near += 1
    if only and near < len(only):
        print(f"RESULT DIFFER: {len(only) - near} bulk colour(s) the Mac does not show: a real difference on the device")
        return 1
    print("RESULT SAME COLOURS: every bulk colour of the device shot is the Mac's (within 2 levels): the screen's colours are the picture's; a photo that looks different is the camera or the panel")
    return 0


if __name__ == "__main__":
    sys.exit(main())
