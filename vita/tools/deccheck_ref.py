#!/usr/bin/env python3
"""Kit #12 boot track (vita/notes/kit12-boot.md section 4; the design: kit11-device.md section 6): the Mac's reference for the decoder checks.

For every .shv clip in <card>/data/video/ (the Devolver splash, the intro, the Massive Monster clip), the clip's own H.264 stream is decoded by
the local ffmpeg (its h264 decoder: H.264 decoding is bit-exact by the standard, so the Vita's hardware decoder must give the same pictures)
to raw YUV 4:2:0, and each picture gets one FNV-1a 64 of its luma plane and of its chroma -- twice: as I420 (the U plane, then the V plane) and
as NV12 (U and V interleaved), so the device's line can name the layout its decoder wrote (the SDK's YUV420_RASTER is not documented) -- in
<card>/data/video/<clip>.decref.tsv, one line per picture: "<n>\\t<Y>\\t<chroma I420>\\t<chroma NV12>" (16 hex digits each). The host's check
(vita/shepherd/k12_boot.inc.c, SHEPHERD_K12_DECCHECK=video) compares the Vita's pictures with these.
The output holds hashes of GAME-DERIVED pictures: owner-local (the card folder), never in git.
--self-test: negative controls -- a device result whose picture 5 is changed, one that stops at picture 7, an empty one, one with a picture
number out of order, and an empty reference must each FAIL; only the whole unchanged result may PASS (review fix: compare() zipped the two
lists, so a short or empty device result -- a decoder that refused partway, a stopped run -- was "0 differ": a PASS).
usage: deccheck_ref.py <card dir> [--clips Name,Name] | --compare <decref.tsv> <dec-video-<clip>.tsv> | --self-test
Only reads its inputs; standard library + the local ffmpeg (no download)."""
import argparse, os, struct, subprocess, sys

def fnv64(b, h=14695981039346656037):
    for x in b:
        h = ((h ^ x) * 1099511628211) & 0xFFFFFFFFFFFFFFFF
    return h

def es_of(shv):
    d = open(shv, "rb").read()
    if struct.unpack_from("<I", d, 0)[0] != 0x31564853: raise SystemExit(f"{shv}: not a .shv")
    w, h, frames, index_off, es_off = struct.unpack_from("<I", d, 12)[0], struct.unpack_from("<I", d, 16)[0], struct.unpack_from("<I", d, 28)[0], struct.unpack_from("<I", d, 32)[0], struct.unpack_from("<I", d, 36)[0]
    es = b"".join(d[es_off + o: es_off + o + s] for o, s in (struct.unpack_from("<II", d, index_off + 8 * i) for i in range(frames)))
    return w, h, frames, es

def reference(shv, tmp):
    w, h, frames, es = es_of(shv)
    p = os.path.join(tmp, "clip.h264"); open(p, "wb").write(es)
    raw = subprocess.run(["ffmpeg", "-v", "error", "-f", "h264", "-i", p, "-f", "rawvideo", "-pix_fmt", "yuv420p", "-"], capture_output=True)
    if raw.returncode: raise SystemExit(raw.stderr.decode()[-500:])
    raw = raw.stdout
    ys, cs = w * h, w * h // 4
    fs = ys + 2 * cs
    n = len(raw) // fs
    rows = []
    for k in range(n):
        f = raw[k * fs:(k + 1) * fs]
        y, u, v = f[:ys], f[ys:ys + cs], f[ys + cs:]
        nv = bytes(b for pair in zip(u, v) for b in pair)
        rows.append((k, fnv64(y), fnv64(u + v), fnv64(nv)))
    return w, h, frames, rows

def write(rows, out, name, w, h, frames):
    with open(out + ".tmp", "w") as f:
        f.write(f"# {name}: {len(rows)} pictures of {frames} ({w}x{h}), ffmpeg's h264 decode of the .shv's own stream; Y, chroma as I420, chroma as NV12 (FNV-1a 64)\n")
        for k, y, ci, cn in rows: f.write(f"{k}\t{y:016x}\t{ci:016x}\t{cn:016x}\n")
    os.replace(out + ".tmp", out)

def compare(ref_rows, dev_rows):
    """-> (same, differ, missing, first difference or None); dev rows: (n, Y, chroma). Every reference picture must be in the device result with
    its own number: a picture the device result lacks (shorter result, a number out of place) counts as missing, an extra one as a difference."""
    same = diff = 0; first = None
    dev = {}
    for k2, dy, dc in dev_rows:
        if k2 in dev or k2 < 0 or k2 >= len(ref_rows):   # (a repeated or out-of-range picture number: a difference)
            diff += 1
            if first is None: first = (k2, "N")
            continue
        dev[k2] = (dy, dc)
    missing = 0
    for k, y, ci, cn in ref_rows:
        d = dev.get(k)
        if d is None:
            missing += 1
            if first is None: first = (k, "missing")
            continue
        if d[0] == y and d[1] in (ci, cn): same += 1
        else:
            diff += 1
            if first is None: first = (k, "Y" if d[0] != y else "C")
    return same, diff, missing, first

def verdict(ref_rows, res):
    """PASS only when every reference picture came back equal: a comparison that could not run (no reference, no device rows) is a FAIL"""
    same, diff, missing, _ = res
    return bool(ref_rows) and same == len(ref_rows) and not diff and not missing

def read_ref(p):
    return [tuple([int(a)] + [int(x, 16) for x in rest]) for a, *rest in (l.rstrip("\n").split("\t")[:4] for l in open(p) if not l.startswith("#"))]

def read_dev(p):
    return [(int(a), int(b, 16), int(c, 16)) for a, b, c, *_ in (l.rstrip("\n").split("\t") for l in open(p) if not l.startswith("#"))]

def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("card", nargs="?"); ap.add_argument("--clips", default="")
    ap.add_argument("--compare", nargs=2); ap.add_argument("--self-test", action="store_true")
    a = ap.parse_args()
    if a.self_test:
        rows = [(k, fnv64(bytes([k])), fnv64(bytes([k, 1])), fnv64(bytes([k, 2]))) for k in range(10)]
        dev_ok = [(k, y, ci) for k, y, ci, cn in rows]
        dev_nv = [(k, y, cn) for k, y, ci, cn in rows]
        cases = [   # (name, reference, device rows, must pass)
            ("unchanged (I420 chroma)", rows, dev_ok, True),
            ("unchanged (NV12 chroma)", rows, dev_nv, True),
            ("picture 5 changed", rows, [(k, y if k != 5 else y ^ 1, ci) for k, y, ci in dev_ok], False),
            ("stops at picture 7", rows, dev_ok[:7], False),
            ("empty device result", rows, [], False),
            ("picture numbers out of place", rows, [(k + 1 if k == 3 else k, y, ci) for k, y, ci in dev_ok], False),
            ("an extra picture", rows, dev_ok + [(10, 1, 1)], False),
            ("empty reference", [], dev_ok, False),
        ]
        ok = True
        for name, ref, dev, want in cases:
            res = compare(ref, dev); got = verdict(ref, res)
            print(f"self-test: {name}: {res[0]} equal, {res[1]} differ, {res[2]} missing, first {res[3]} -> {'PASS' if got else 'FAIL'} (wanted {'PASS' if want else 'FAIL'})")
            ok &= got == want
        print(f"self-test -> {'PASS' if ok else 'FAIL'}")
        return 0 if ok else 1
    if a.compare:
        ref = read_ref(a.compare[0]); res = compare(ref, read_dev(a.compare[1]))
        same, diff, missing, first = res
        ok = verdict(ref, res)
        print(f"{same} equal, {diff} differ, {missing} missing of {len(ref)} reference pictures" + (f"; first difference picture {first[0]} ({first[1]})" if first else "")
              + f" -> {'PASS' if ok else 'FAIL'}" + ("" if ref else " (no reference rows: not compared)"))
        return 0 if ok else 1
    vd = os.path.join(a.card, "data", "video")
    names = [n for n in a.clips.split(",") if n] or [f[:-4] for f in sorted(os.listdir(vd)) if f.endswith(".shv")]
    import tempfile
    with tempfile.TemporaryDirectory(dir=os.environ.get("TMPDIR")) as tmp:
        for name in names:
            shv = os.path.join(vd, name + ".shv")
            if not os.path.exists(shv): print(f"{name}: no .shv"); continue
            w, h, frames, rows = reference(shv, tmp)
            write(rows, os.path.join(vd, name + ".decref.tsv"), name, w, h, frames)
            print(f"{name}.decref.tsv: {len(rows)} of {frames} pictures ({w}x{h}){'' if len(rows) == frames else ' -- ffmpeg gave a different count'}")
    return 0

if __name__ == "__main__":
    sys.exit(main())
