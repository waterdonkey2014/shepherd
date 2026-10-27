#!/usr/bin/env python3
"""Kit #6 engine track (vita/notes/kit6-engine.md, item a): the length table for the engine's VideoPlayer stand-in.

The engine draws no video, but a VideoPlayer still has to take as long as the clip does: the game sequences against it (the intro's crown
-> video -> rebirth chain starts its next transition from loopPointReached and freezes when the clip ends early). This reads each .mp4's
own header (moov / the video track's mdhd + stts: frame count and the constant frame duration) and writes one line per clip:

    <name> TAB <frames> TAB <rate as num/den>

The output is game-derived (lengths of the owner's videos): keep it owner-local (the owner folder, a work dir), never in git. Point the
engine at it with SHEPHERD_VIDEO_LENGTHS=<file>. A clip left out keeps the profile's stand-in length (the Vita profile: 0 s, the clip is
skipped as in kits #3-#5), so --only decides which clips the Vita "plays" (as a black picture for the clip's length).

usage: video_lengths.py <dir with the game's .mp4 files> [--only Name,Name...] [-o out.tsv]
Read-only on the input; standard library only."""
import argparse, os, struct, sys
from fractions import Fraction

def boxes(f, start, end):
    f.seek(start)
    while f.tell() + 8 <= end:
        pos = f.tell(); size, typ = struct.unpack(">I4s", f.read(8)); hdr = 8
        if size == 1: size = struct.unpack(">Q", f.read(8))[0]; hdr = 16
        elif size == 0: size = end - pos
        if size < hdr: break
        yield pos, size, typ.decode("latin1"), hdr
        f.seek(pos + size)

def child(f, pos, size, hdr, name):
    for p, s, t, h in boxes(f, pos + hdr, pos + size):
        if t == name: return p, s, h
    return None

def video_track(path):
    """(frames, Fraction rate) of the first video track, or None."""
    with open(path, "rb") as f:
        end = os.path.getsize(path)
        moov = next(((p, s, h) for p, s, t, h in boxes(f, 0, end) if t == "moov"), None)
        if not moov: return None
        for p, s, t, h in boxes(f, moov[0] + moov[2], moov[0] + moov[1]):
            if t != "trak": continue
            mdia = child(f, p, s, h, "mdia")
            if not mdia: continue
            hdlr = child(f, *mdia, "hdlr")
            f.seek(hdlr[0] + hdlr[2] + 8)
            if f.read(4) != b"vide": continue
            mdhd = child(f, *mdia, "mdhd"); f.seek(mdhd[0] + mdhd[2]); ver = f.read(1)[0]
            f.seek(mdhd[0] + mdhd[2] + 4 + (16 if ver == 1 else 8)); timescale = struct.unpack(">I", f.read(4))[0]   # after version/flags + the two times
            stbl = child(f, *child(f, *mdia, "minf"), "stbl")
            stts = child(f, *stbl, "stts"); f.seek(stts[0] + stts[2] + 4); n = struct.unpack(">I", f.read(4))[0]
            runs = [struct.unpack(">II", f.read(8)) for _ in range(n)]
            frames = sum(c for c, _ in runs)
            deltas = {d for _, d in runs}
            delta = max(deltas, key=lambda d: sum(c for c, dd in runs if dd == d))   # the constant (dominant) frame duration
            return frames, Fraction(timescale, delta)
    return None

def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("dir"); ap.add_argument("--only", default=""); ap.add_argument("-o", "--out")
    a = ap.parse_args()
    only = {x.strip().lower() for x in a.only.split(",") if x.strip()}
    rows = []
    for fn in sorted(os.listdir(a.dir)):
        if not fn.lower().endswith(".mp4"): continue
        name = fn[:-4]
        if only and name.lower() not in only: continue
        vt = video_track(os.path.join(a.dir, fn))
        if not vt: print(f"skip {fn}: no video track found", file=sys.stderr); continue
        frames, rate = vt
        rows.append(f"{name}\t{frames}\t{rate.numerator}/{rate.denominator}\t# {float(frames / rate):.3f} s")
    out = "# Shepherd VideoPlayer stand-in lengths (owner-local: made from the owner's game install by vita/tools/video_lengths.py; never in git)\n" + "\n".join(rows) + "\n"
    if a.out: open(a.out, "w").write(out)
    else: sys.stdout.write(out)
    return 0 if rows else 1

if __name__ == "__main__":
    sys.exit(main())
