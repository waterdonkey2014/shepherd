#!/usr/bin/env python3
"""check_owner_guids.py <packaged dll dir> <owner dll dir> <aot dir> [<aot dir> ...]: the AOT GUID gate for OWNER modules.

vita/hello/check_aot_guids.py (Mono .s) and vita/llvm/check_aot_guids_llvm.py (LLVM objects) look a module's own GUID up in
the packaged (VPK) set only, which is right for the engine-AOT eboot, where every AOT module is packaged. An owner module
(a rewritten Unity module or a game assembly) is in the owner's managed folder instead, so here its own GUID is checked against
the owner DLL and every image_table dependency against the packaged DLL of that name, else the owner DLL ("(owner)").
Both object kinds: <M>-llvm.o when present (the LLVM part holds the tables), else <M>.dll.s. The image_table blob is read as
bytes (names are split on NUL and decoded leniently: the blob also holds binary fields). Exit 1 on any mismatch."""
import glob, os, re, struct, sys
sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "llvm"))
import check_aot_guids_llvm as L          # mvid(), tables(obj) for LLVM objects

GRE = re.compile(r'^[0-9A-F]{8}-[0-9A-F]{4}-')

def tables_s(sfile):
    txt = open(sfile, errors="replace").read()
    out = {}
    i = txt.index("\nassembly_guid:")
    out["__self__"] = re.search(r'\.string "([^"]*)"', txt[i:i + 200]).group(1)
    i = txt.index("\nimage_table:"); j = txt.index("\n.text", i + 5)
    bs = bytes(int(x) for x in re.findall(r'\d+', ' '.join(l.strip()[6:] for l in txt[i:j].splitlines() if l.strip().startswith('.byte'))))
    out["__count__"] = struct.unpack_from("<I", bs, 0)[0]
    parts = [x.decode("latin-1") for x in bs[4:].split(b"\0") if x]
    for k, x in enumerate(parts):
        if GRE.match(x) and k:
            out[parts[k - 1]] = x
    return out

def main(pkg, own, dirs):
    mv = {os.path.basename(f)[:-4]: L.mvid(f) for f in glob.glob(pkg + "/*.dll")}
    ow = {os.path.basename(f)[:-4]: L.mvid(f) for f in glob.glob(own + "/*.dll")}
    bad = 0; n = 0
    for d in dirs:
        for o in sorted(glob.glob(d + "/*.dll.o")):
            a = os.path.basename(o)[:-6]
            lo = os.path.join(d, a + "-llvm.o"); s = os.path.join(d, a + ".dll.s")
            t = L.tables(lo) if os.path.exists(lo) else tables_s(s)
            selfref = ow.get(a) or mv.get(a)
            ok = t["__self__"] == selfref; bad += not ok; n += 1
            deps = []
            for k, v in t.items():
                if k.startswith("__"): continue
                r = mv.get(k); tag = ""
                if r is None and k in ow: r = ow[k]; tag = " (owner)"
                deps.append(f"{k}{tag}:{'ok' if r == v else 'MISMATCH ' + str(r) + ' vs ' + v}"); bad += r != v
            print(f"{a}: self {'ok' if ok else 'MISMATCH'} {t['__self__']} | image_table {t['__count__']}: " + ", ".join(deps))
    print(f"MODULES {n}")
    print("TOTAL MISMATCHES", bad)
    return 1 if bad else 0

if __name__ == "__main__":
    sys.exit(main(sys.argv[1], sys.argv[2], sys.argv[3:]))
