#!/usr/bin/env python3
"""Writes the LiveArea icon (128x128, 8-bit indexed PNG) for Shepherd Hello. Generated, no downloads."""
import struct, sys, zlib
W = H = 128
pal = bytes([18, 24, 30,   92, 170, 255,   238, 232, 211])      # ink, blue, paper
rows = b''
for y in range(H):
    row = bytearray([0])
    for x in range(W):
        dx, dy = x - 64, y - 64
        ring = 34 * 34 <= dx * dx + dy * dy <= 46 * 46             # a halo
        core = dx * dx + dy * dy <= 14 * 14
        row.append(1 if ring else (2 if core else 0))
    rows += bytes(row)
def chunk(t, d): return struct.pack('>I', len(d)) + t + d + struct.pack('>I', zlib.crc32(t + d) & 0xffffffff)
png = b'\x89PNG\r\n\x1a\n' + chunk(b'IHDR', struct.pack('>IIBBBBB', W, H, 8, 3, 0, 0, 0)) + chunk(b'PLTE', pal) \
    + chunk(b'IDAT', zlib.compress(rows, 9)) + chunk(b'IEND', b'')
open(sys.argv[1], 'wb').write(png)
