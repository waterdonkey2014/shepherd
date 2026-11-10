#!/usr/bin/env python3
"""Kit #12 boot track (vita/notes/kit12-boot.md section 3): the recommended plugins' names as pictures of the game's loading text, for the host
to draw over the loading screen (vita/shepherd/k12_boot.inc.c) when a plugin is missing (owner 2026-10-30: names only, in the loading text's own
font and colour; Wi-Fi Turbo labelled "for online play").
vita/tools/plugins_text.sh runs the Mac engine to the loading screen with the game's own loading text (MMTransition.LoadingText: its font, size and
red) showing each name in turn (SHEPHERD_K12_REC_TEXT); this tool cuts each name out of its screenshot -- the pixels that differ from the same
loop picture a ring period (180 frames) earlier, where the text was empty, inside the text's box -- as straight-alpha RGBA: the text is drawn
over black, so alpha = the pixel's red over the text colour's red and the colour is the text's own. Refused when the text's box is not black in
the reference picture. Output: <out>/plugins.shp ("SHP1": magic, version 1, count, FNV-1a 32 of the rest; per line id, w, h, zlib length, zlib
RGBA). The output is GAME-DERIVED (the game's font, drawn): the owner folder only, never in git.
usage: plugins_text.py <shots dir> -o <out dir> --line <id>:<frame> ... [--box 112,468,400,44] [--period 180] [--color 253,29,3]
Only reads its inputs; standard library + the local ffmpeg (no download)."""
import argparse, os, struct, subprocess, sys, zlib

def fnv32(b, h=2166136261):
    for x in b: h = ((h ^ x) * 16777619) & 0xffffffff
    return h

def rgb(path, box):
    x, y, w, h = box
    return subprocess.run(["ffmpeg", "-v", "error", "-i", path, "-vf", f"crop={w}:{h}:{x}:{y}", "-f", "rawvideo", "-pix_fmt", "rgb24", "-"], capture_output=True, check=True).stdout

def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("shots"); ap.add_argument("-o", "--out", required=True); ap.add_argument("--line", action="append", required=True)
    ap.add_argument("--box", default="112,468,400,44"); ap.add_argument("--period", type=int, default=180); ap.add_argument("--color", default="253,29,3")
    a = ap.parse_args()
    box = tuple(int(v) for v in a.box.split(",")); col = [int(v) for v in a.color.split(",")]
    _, _, bw, bh = box
    entries = []
    for spec in a.line:
        lid, fr = (int(v) for v in spec.split(":"))
        t = rgb(os.path.join(a.shots, f"f{fr:05d}.png"), box); r = rgb(os.path.join(a.shots, f"f{fr - a.period:05d}.png"), box)
        if any(r): raise SystemExit(f"line {lid}: the text's box is not black in f{fr - a.period} (the reference): nothing written")
        xs = [i % bw for i in range(bw * bh) if t[3 * i:3 * i + 3] != r[3 * i:3 * i + 3]]
        ys = [i // bw for i in range(bw * bh) if t[3 * i:3 * i + 3] != r[3 * i:3 * i + 3]]
        if not xs: raise SystemExit(f"line {lid}: no text in f{fr}")
        x0, x1, y0, y1 = min(xs), max(xs), min(ys), max(ys)
        w, h = x1 - x0 + 1, y1 - y0 + 1
        px = bytearray()
        for yy in range(y0, y1 + 1):
            for xx in range(x0, x1 + 1):
                i = 3 * (yy * bw + xx); p = t[i:i + 3]
                al = min(255, round(max(p[0] * 255 / max(1, col[0]), p[1] * 255 / max(1, col[1]) if col[1] > 40 else 0, p[2] * 255 / max(1, col[2]) if col[2] > 40 else 0)))
                px += bytes((col[0], col[1], col[2], al))
        z = zlib.compress(bytes(px), 9)
        entries.append(struct.pack("<4I", lid, w, h, len(z)) + z)
        print(f"line {lid}: f{fr}, {w} x {h} px at {box[0] + x0},{box[1] + y0} (the loading text's red {tuple(col)}), {len(z)} B")
    body = b"".join(entries)
    data = struct.pack("<4I", 0x31504853, 1, len(entries), fnv32(body)) + body
    os.makedirs(a.out, exist_ok=True)
    out = os.path.join(a.out, "plugins.shp")
    with open(out + ".tmp", "wb") as f: f.write(data)
    os.replace(out + ".tmp", out)
    print(f"plugins.shp: {len(entries)} line(s), {len(data)} B")

if __name__ == "__main__":
    main()
