#!/usr/bin/env python3
"""hotunion.py — union of the per-scenario partial-AOT method sets (hotan.py's set-<name>.txt) into owner-local AOT method lists,
per assembly, and Mono .aotprofile files for mono-aot-cross's `profile=<file>,profile-only`.

usage: hotunion.py <out dir> <scenario dir>...      (a scenario dir holds methods/*.tsv and set-*.txt: profiles/idle, profiles/intro/3-gameplay, ...)

For every set name found (PH0, P90, PH90, P95, PH95, P99, PH99) writes <out dir>/<set>/:
  methods.tsv          the union (image, group, IL bytes, generic instance?, the scenarios it came from, the .aotprofile key fields)
  by-assembly.tsv      methods / IL bytes / generic instances per assembly
  aotprofile/<asm>.aotprofile   the set's methods of that assembly in Mono's AOT profile format (mono/profiler/aot.h, version 1.0),
                       NON-generic methods only (a generic instance needs type records for its arguments; listed, not written)
and <out dir>/summary.md. The files name owner assemblies' methods (derived from the owner's game): owner-local, never in git or a VPK."""
import sys, os, re, struct, collections

sys.dont_write_bytecode = True   # no __pycache__ in the repo
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from hotan import group   # noqa: E402

def load_methods(d):
    m = {}
    md = os.path.join(d, "methods")
    for fn in os.listdir(md):
        with open(os.path.join(md, fn)) as f:
            hdr = f.readline().rstrip("\n").split("\t"); ix = {k: i for i, k in enumerate(hdr)}
            for l in f:
                x = l.rstrip("\n").split("\t")
                m[x[ix["full_name"]]] = {k: x[ix[k]] for k in ("image", "namespace", "class", "method", "param_count", "signature", "il_bytes", "inflated", "wrapper", "self_ms_per_frame_main_median", "calls_per_frame_main_exact")}
    return m

class W:
    def __init__(s): s.b = bytearray(b"AOTPROFILE") + struct.pack("<i", (1 << 16) | 0); s.id = 0; s.images = {}; s.classes = {}
    def i32(s, v): s.b += struct.pack("<i", v)
    def string(s, t): e = t.encode("utf-8"); s.i32(len(e)); s.b += e
    def rec(s, t): s.b.append(t); i = s.id; s.id += 1; s.i32(i); return i
    def image(s, name):
        if name not in s.images:
            s.images[name] = s.rec(1); s.string(name); s.string("")      # AOTPROF_RECORD_IMAGE: name, mvid (unused by the loader)
        return s.images[name]
    def klass(s, img, ns, cls):
        k = (img, ns, cls)
        if k not in s.classes:
            ii = s.image(img); s.classes[k] = s.rec(2); s.b.append(0x12)   # AOTPROF_RECORD_TYPE, MONO_TYPE_CLASS
            s.i32(ii); s.i32(-1); s.string(f"{ns}.{cls}")                  # "ns.Name" / "ns.Outer/Inner" as the AOT profiler writes it
        return s.classes[k]
    def method(s, m):
        ci = s.klass(m["image"], m["namespace"], m["class"])
        s.rec(4); s.i32(ci); s.i32(-1); s.i32(int(m["param_count"])); s.string(m["method"]); s.string(m["signature"])
    def done(s): s.b.append(0); s.i32(s.id); return bytes(s.b)    # AOTPROF_RECORD_NONE (+ the id the loader reads before stopping)

def main():
    out, dirs = sys.argv[1], sys.argv[2:]
    meta, sets = {}, collections.defaultdict(lambda: collections.defaultdict(set))
    for d in dirs:
        meta.update(load_methods(d))
        tag = os.path.relpath(d, os.path.commonpath(dirs) if len(dirs) > 1 else os.path.dirname(d))
        for fn in os.listdir(d):
            mm = re.match(r"set-(\w+)\.txt$", fn)
            if not mm: continue
            for l in open(os.path.join(d, fn)):
                f = l.rstrip("\n")
                if f: sets[mm.group(1)][f].add(tag)
    rows = []
    for name in sorted(sets, key=lambda n: (n.startswith("PH"), n)):
        sd = os.path.join(out, name); os.makedirs(os.path.join(sd, "aotprofile"), exist_ok=True)
        U = sets[name]; per = collections.defaultdict(lambda: [0, 0, 0, 0]); writers = collections.defaultdict(W)
        allw = W()
        with open(os.path.join(sd, "methods.tsv"), "w") as w:
            w.write("image\tgroup\til_bytes\tinflated\tscenarios\tnamespace\tclass\tmethod\tparam_count\tsignature\tfull_name\n")
            for f in sorted(U, key=lambda f: (meta.get(f, {}).get("image", "?"), f)):
                m = meta.get(f)
                if not m: continue
                g = group(m["image"]); il = int(m["il_bytes"] or 0); inf = m["inflated"] == "1"
                p = per[m["image"]]; p[0] += 1; p[1] += il; p[2] += inf
                w.write(f"{m['image']}\t{g}\t{il}\t{int(inf)}\t{','.join(sorted(U[f]))}\t{m['namespace']}\t{m['class']}\t{m['method']}\t{m['param_count']}\t{m['signature']}\t{f}\n")
                if not inf and not m["wrapper"] and m["signature"] != "-":
                    writers[m["image"]].method(m); allw.method(m); p[3] += 1
        with open(os.path.join(sd, "by-assembly.tsv"), "w") as w:
            w.write("assembly\tgroup\tmethods\til_bytes\tgeneric_instances\tin_aotprofile\n")
            for img, p in sorted(per.items(), key=lambda t: -t[1][1]):
                w.write(f"{img}\t{group(img)}\t{p[0]}\t{p[1]}\t{p[2]}\t{p[3]}\n")
        for img, wr in writers.items():
            open(os.path.join(sd, "aotprofile", re.sub(r"[^A-Za-z0-9._-]", "_", img) + ".aotprofile"), "wb").write(wr.done())
        open(os.path.join(sd, f"{name}-all.aotprofile"), "wb").write(allw.done())
        rows.append((name, per))
    with open(os.path.join(out, "summary.md"), "w") as w:
        w.write("# Union of the partial-AOT sets over: " + ", ".join(dirs) + "\n\n")
        w.write("| set | methods | IL KiB | generic instances (not in the .aotprofile) | assemblies (methods) |\n|---|---:|---:|---:|---|\n")
        for name, per in rows:
            n = sum(p[0] for p in per.values()); il = sum(p[1] for p in per.values()); gi = sum(p[2] for p in per.values())
            top = sorted(per.items(), key=lambda t: -t[1][0])[:8]
            w.write(f"| {name} | {n} | {il / 1024:.1f} | {gi} | " + ", ".join(f"{k} {v[0]}" for k, v in top) + (" …" if len(per) > 8 else "") + " |\n")
    print(open(os.path.join(out, "summary.md")).read())

if __name__ == "__main__":
    main()
