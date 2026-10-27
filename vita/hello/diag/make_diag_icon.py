#!/usr/bin/env python3
"""DIAG-ONLY: the LiveArea icon (128x128, 8-bit indexed PNG) of the devfail1 diagnostic apps -- an amber stethoscope-ish ring with a
cross, so "Shepherd Hello Diag" / "Shepherd Menu Diag" are easy to tell from the normal apps. usage: make_diag_icon.py <out.png> <hello|menu>"""
import struct, sys, zlib
W = H = 128
kind = sys.argv[2] if len(sys.argv) > 2 else 'hello'
pal = bytes([24, 18, 12,   255, 176, 32,   238, 232, 211,   92, 170, 255 if kind == 'hello' else 120])   # ink, amber, paper, accent
rows = b''
for y in range(H):
    row = bytearray([0])
    for x in range(W):
        dx, dy = x - 64, y - 64
        r2 = dx * dx + dy * dy
        ring = 38 * 38 <= r2 <= 48 * 48
        cross = (abs(dx) <= 6 and abs(dy) <= 24) or (abs(dy) <= 6 and abs(dx) <= 24)
        dot = (x - 100) ** 2 + (y - 100) ** 2 <= 10 * 10 if kind == 'menu' else (x - 28) ** 2 + (y - 100) ** 2 <= 10 * 10
        row.append(1 if ring else (2 if cross else (3 if dot else 0)))
    rows += bytes(row)
def chunk(t, d): return struct.pack('>I', len(d)) + t + d + struct.pack('>I', zlib.crc32(t + d) & 0xffffffff)
png = b'\x89PNG\r\n\x1a\n' + chunk(b'IHDR', struct.pack('>IIBBBBB', W, H, 8, 3, 0, 0, 0)) + chunk(b'PLTE', pal) \
    + chunk(b'IDAT', zlib.compress(rows, 9)) + chunk(b'IEND', b'')
open(sys.argv[1], 'wb').write(png)
