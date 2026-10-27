#!/usr/bin/env python3
"""imgcmp.py A.png B.png [diff.png]  -- RGB difference: mean abs (levels), % pixels whose max channel diff > 8 / > 24, max, bbox(>8).
Optional --region x0,y0,x1,y1 restricts the stats to a box (e.g. to exclude the animated centre)."""
import sys, numpy as np
from PIL import Image
args = [a for a in sys.argv[1:] if not a.startswith("--")]
reg = next((a.split("=", 1)[1] for a in sys.argv[1:] if a.startswith("--region=")), None)
excl = next((a.split("=", 1)[1] for a in sys.argv[1:] if a.startswith("--exclude=")), None)
a = np.asarray(Image.open(args[0]).convert("RGB")).astype(np.int16)
b = np.asarray(Image.open(args[1]).convert("RGB")).astype(np.int16)
if a.shape != b.shape: print(f"size mismatch {a.shape} vs {b.shape}"); sys.exit(2)
d = np.abs(a - b); m = d.max(axis=2)
mask = np.ones(m.shape, bool)
if reg: x0, y0, x1, y1 = map(int, reg.split(",")); mask[:] = False; mask[y0:y1, x0:x1] = True
if excl: x0, y0, x1, y1 = map(int, excl.split(",")); mask[y0:y1, x0:x1] = False
n = mask.sum(); dm = d[mask]; mm = m[mask]
ys, xs = np.nonzero((m > 8) & mask)
bbox = f"({xs.min()},{ys.min()})-({xs.max()},{ys.max()})" if len(xs) else "none"
name = lambda p: "/".join(p.split("/")[-2:])
print(f"{name(args[0])} vs {name(args[1])}{' region ' + reg if reg else ''}{' excl ' + excl if excl else ''}: meanAbs={dm.mean():.3f} >8={100 * (mm > 8).sum() / n:.3f}% >24={100 * (mm > 24).sum() / n:.3f}% max={mm.max()} bbox(>8)={bbox}")
if len(args) > 2: Image.fromarray(np.clip(d * 8, 0, 255).astype(np.uint8)).save(args[2])
