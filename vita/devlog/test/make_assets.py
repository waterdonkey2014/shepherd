#!/usr/bin/env python3
"""LiveArea assets of the Shepherd Log Send Test (SHPD00014), generated (no downloads, standard library only).

VitaShell refuses a VPK without sce_sys/livearea/contents/{template.xml,bg.png,startup.png} (0x80AF5001) and PNGs
that are not 8-bit indexed (0x8010113D: bit depth 8, colour type 3; no 1/2/4-bit palettes). So every PNG here is
written as bit depth 8, colour type 3, with a full 256-entry palette -- the same shape as vita/probe/sce_sys/ (the
known-good set from the first device session). Deterministic: the same bytes on every run.

  python3 vita/devlog/test/make_assets.py [--check]     (--check: only verify the files that are there)
"""
import os
import struct
import sys
import zlib

HERE = os.path.dirname(os.path.abspath(__file__))
SYS = os.path.join(HERE, "sce_sys")
LA = os.path.join(SYS, "livearea", "contents")

# palette: 0 background, 1 dark band, 2 red, 3 bone, 4 dim bone, 5 deep red; the rest padded to 256 entries
PAL = [(24, 20, 22), (34, 28, 32), (209, 43, 39), (238, 232, 211), (150, 140, 128), (120, 24, 26)]
PAL += [(0, 0, 0)] * (256 - len(PAL))

TEMPLATE = ('<?xml version="1.0" encoding="utf-8"?>\n\n<livearea style="a1" format-ver="01.00" content-rev="1">\n'
            '\t<livearea-background>\n\t\t<image>bg.png</image>\n\t</livearea-background>\n\t\n'
            '\t<gate>\n\t\t<startup-image>startup.png</startup-image>\n\t</gate>\n</livearea>\n')


def chunk(t, d):
    return struct.pack(">I", len(d)) + t + d + struct.pack(">I", zlib.crc32(t + d) & 0xFFFFFFFF)


def png(w, h, pix):
    rows = b"".join(b"\x00" + bytes(pix(x, y) for x in range(w)) for y in range(h))
    return (b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", w, h, 8, 3, 0, 0, 0))
            + chunk(b"PLTE", b"".join(bytes(c) for c in PAL)) + chunk(b"IDAT", zlib.compress(rows, 9)) + chunk(b"IEND", b""))


def motif(cx, cy, s):
    """an envelope under the cult eye, centred at (cx, cy), scale s (1.0 = the 128 px icon): a pixel function"""
    def f(x, y):
        u, v = (x - cx) / s + 64, (y - cy) / s + 64
        env = 24 <= u <= 103 and 52 <= v <= 103
        edge = env and (u < 27 or u > 100 or v < 55 or v > 100)
        flap = env and v <= 78 and abs((v - 52) - abs(u - 64) * 26 / 40) <= 2.2
        eye = ((u - 64) ** 2) / 500 + ((v - 30) ** 2) / 90 <= 1.0
        pupil = (u - 64) ** 2 + (v - 30) ** 2 <= 40
        if pupil or flap:
            return 2
        if edge:
            return 4
        if eye or env:
            return 3
        return None
    return f


def icon(x, y):
    p = motif(64, 64, 1.0)(x, y)
    return 0 if p is None else p


def bg(x, y):
    p = motif(620, 250, 2.4)(x, y)
    if p is not None:
        return p
    if 470 <= y <= 476 or 24 <= y <= 30:
        return 5
    return 1 if (x // 40 + y // 40) % 2 else 0


def startup(x, y):
    p = motif(140, 79, 1.0)(x, y)
    if p is not None:
        return p
    return 5 if y >= 150 else 0


FILES = [
    (os.path.join(SYS, "icon0.png"), 128, 128, icon),
    (os.path.join(LA, "bg.png"), 840, 500, bg),
    (os.path.join(LA, "startup.png"), 280, 158, startup),
]


def check_png(path, w=None, h=None):
    """-> error text or None: 8-bit indexed (colour type 3), full PLTE, expected size"""
    d = open(path, "rb").read()
    if d[:8] != b"\x89PNG\r\n\x1a\n" or d[12:16] != b"IHDR":
        return "not a PNG"
    pw, ph, bd, ct = struct.unpack(">IIBB", d[16:26])
    if (bd, ct) != (8, 3):
        return "bit depth %d colour type %d (need 8 / 3)" % (bd, ct)
    if w and (pw, ph) != (w, h):
        return "%dx%d (need %dx%d)" % (pw, ph, w, h)
    i, plte = 8, None
    while i < len(d):
        n = struct.unpack(">I", d[i:i + 4])[0]
        if d[i + 4:i + 8] == b"PLTE":
            plte = n
        i += 12 + n
    if plte is None or plte % 3:
        return "no / bad PLTE"
    return None


def main():
    if "--check" not in sys.argv:
        os.makedirs(LA, exist_ok=True)
        for path, w, h, fn in FILES:
            open(path, "wb").write(png(w, h, fn))
        open(os.path.join(LA, "template.xml"), "w").write(TEMPLATE)
    bad = 0
    for path, w, h, _ in FILES:
        err = check_png(path, w, h)
        print("%-34s %s" % (os.path.relpath(path, HERE), err or "%dx%d, 8-bit indexed, 256 colours, %d bytes" % (w, h, os.path.getsize(path))))
        bad += bool(err)
    return 1 if bad else 0


if __name__ == "__main__":
    sys.exit(main())
