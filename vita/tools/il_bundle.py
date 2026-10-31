#!/usr/bin/env python3
"""Device kit #10, track boot (vita/notes/kit10-boot.md): the card's game DLLs in ONE BUNDLE, in boot order (boot.ilb).

The host (vita/shepherd/k10_ilb.inc.c) reads the bundle in one straight stream on a worker core while Mono boots, instead of looking up,
opening and reading ~109 DLL files one at a time on the boot's own thread. This tool writes it into the card's managed/ folder:
  * the DLLs = the card's own (ux0:data/shepherd/managed/*.dll: the game's and the Unity modules -- the VPK's engine / BCL stay in app0:),
    those a boot loads, in the order it loads them: the order list is a run's runtime/logs/<app>/assemblies.txt (the engine writes it at
    every exit: the loaded assemblies in load order with their locations; any app's, any start's -- the device's own or a vlx run's);
  * byte for byte, each at a 16-byte aligned offset; header (32 B: "ILB1", version, count, index / data offsets, data bytes, the index's
    FNV-1a, the header's FNV-1a) + index (64 B a DLL: name without ".dll" NUL-terminated in 48 B, offset, size, FNV-1a of its bytes);
  * the host uses it only after one start has compared every bundled DLL it loaded with its own file (k10-ilb.ok) and only while the folder
    listing is the same -- a re-staged managed/ folder needs a re-made bundle (stage-owner.sh does both).
  * kit #10 integration (boot x aot): --ilz <card managed-ilz dir> bundles a DLL's COMPRESSED-IL form (the aot track's k10_ilz.py stage
    output) when that folder holds it -- the index entry's last word = 1 (flags bit 0); the host serves such an entry only while compressed
    IL is on and the ILZ file is verified against its original (exactly the bytes the loader would read without the bundle), and keys the
    bundle's freshness on managed-ilz/ too. Without --ilz the bundle is the boot track's, byte for byte.
The output holds the owner's game DLLs: GAME-DERIVED -- the owner folder only, never in git, never in a VPK.
usage: il_bundle.py <card managed dir> <assemblies.txt> [-o <file>, default <managed dir>/boot.ilb] [--ilz <card managed-ilz dir>]
Standard library only; only reads its inputs."""
import argparse, os, struct, sys

GAME_PREFIX = "/ux0:data/shepherd/managed/"

def fnv32(b, h=2166136261):
    for x in b:
        h = ((h ^ x) * 16777619) & 0xffffffff
    return h

def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("managed"); ap.add_argument("order"); ap.add_argument("-o", "--out"); ap.add_argument("--ilz")
    a = ap.parse_args()
    names = []
    for line in open(a.order, encoding="utf-8"):
        if line.startswith("#"): continue
        f = line.rstrip("\n").split("\t")
        if len(f) < 4 or not f[3].startswith(GAME_PREFIX): continue
        fn = f[3][len(GAME_PREFIX):]
        if not fn.endswith(".dll") or "/" in fn: continue
        n = fn[:-4]
        if n.startswith("Shepherd."): continue   # (the build's own Derived DLL: it changes with every build -- never in the bundle)
        if n not in names: names.append(n)
    if not names: raise SystemExit(f"no card DLLs ({GAME_PREFIX}...) in {a.order}")
    blobs, flags = [], []
    for n in names:
        p = os.path.join(a.managed, n + ".dll")
        if not os.path.isfile(p): raise SystemExit(f"{p}: in the order list but not in the card's managed folder")
        if len(n.encode("utf-8")) > 47: raise SystemExit(f"{n}: name longer than 47 bytes")
        z = os.path.join(a.ilz, n + ".dll") if a.ilz else None
        if z and os.path.isfile(z):   # kit #10 integration: the compressed-IL form (the loader reads it while compressed IL is on)
            blobs.append(open(z, "rb").read()); flags.append(1)
        else:
            blobs.append(open(p, "rb").read()); flags.append(0)
    count = len(names)
    data_off = (32 + 64 * count + 15) & ~15
    idx, data = b"", b""
    for n, b, fl in zip(names, blobs, flags):
        off = len(data)
        idx += n.encode("utf-8").ljust(48, b"\x00") + struct.pack("<4I", off, len(b), fnv32(b), fl)
        data += b + b"\x00" * ((-len(b)) % 16)
    hdr = struct.pack("<7I", 0x31424c49, 1, count, 32, data_off, len(data), fnv32(idx))
    hdr += struct.pack("<I", fnv32(hdr))
    out = a.out or os.path.join(a.managed, "boot.ilb")
    with open(out + ".tmp", "wb") as f:
        f.write(hdr + idx + b"\x00" * (data_off - 32 - len(idx)) + data)
    os.replace(out + ".tmp", out)
    print(f"{os.path.basename(out)}: {count} DLLs from {a.managed} in the boot order of {a.order} ({sum(map(len, blobs))} B of DLLs, file {data_off + len(data)} B); "
          f"first {names[:3]}, last {names[-1]}" + (f"; {sum(flags)} in their compressed-IL form from {a.ilz}" if a.ilz else ""))
    return 0

if __name__ == "__main__":
    sys.exit(main())
