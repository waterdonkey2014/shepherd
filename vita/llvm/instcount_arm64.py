#!/usr/bin/env python3
"""instcount_arm64.py <nollvm dylib> <llvm dylib>: the hot-method static instruction counts of instcount.py, for the Mac
benchmark's osx-arm64 AOT images (Mach-O; Mono's arm64 backend vs LLVM arm64). Used to calibrate the armv7 static ratios against
the measured arm64 frame-time ratio (vita/notes/llvm-aot.md §6): same methods, same regexes (instcount.HOT)."""
import os, re, subprocess, sys, collections
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from instcount import HOT   # noqa: E402

CATS = [("call", re.compile(r"^(bl|blr)$")), ("load", re.compile(r"^(ldr|ldp|ldur|ldrb|ldrh|ldrsw|ldrsb|ldrsh|ldurb|ldurh|ldursw|ldar|ldxr|ld1)")),
        ("store", re.compile(r"^(str|stp|stur|strb|strh|sturb|sturh|stlr|stxr|st1)")),
        ("branch", re.compile(r"^(b\.|cbn?z|tbn?z|b$|br$)")), ("fp", re.compile(r"^f"))]

def funcs(dylib):
    out = subprocess.run(["xcrun", "llvm-objdump", "-d", "--no-show-raw-insn", dylib], capture_output=True, text=True).stdout
    res = {}; cur = None
    for line in out.splitlines():
        m = re.match(r"^[0-9a-f]+ <([^>]+)>:$", line)
        if m:
            n = m.group(1).lstrip("_")
            n = re.sub(r"^Shepherd_Engine_(?=Shepherd_Engine_)", "", n)   # LLVM symbols carry the module prefix
            cur = res.setdefault(n, collections.Counter()); continue
        if cur is None: continue
        m = re.match(r"^\s+[0-9a-f]+:\s+(\S+)", line)
        if not m: continue
        mn = m.group(1); cur["insns"] += 1
        for c, rx in CATS:
            if rx.match(mn): cur[c] += 1; break
    return res

def main(a, b):
    fa, fb = funcs(a), funcs(b)
    print("| method | Mono arm64 insns / calls / ld / st | LLVM arm64 insns / calls / ld / st | LLVM/Mono |")
    print("|---|---:|---:|---:|")
    ta = collections.Counter(); tb = collections.Counter()
    for label, rx in HOT:
        r = re.compile(rx)
        row = []
        for f, t in ((fa, ta), (fb, tb)):
            hits = [k for k in f if r.search(k) and not k.startswith(("plt_", "p_", "ut_", "wrapper_"))]
            c = collections.Counter()
            for k in hits: c.update(f[k])
            t.update(c); row.append(c)
        if not row[0]["insns"] or not row[1]["insns"]: continue
        print(f"| {label} | {row[0]['insns']} / {row[0]['call']} / {row[0]['load']} / {row[0]['store']} | "
              f"{row[1]['insns']} / {row[1]['call']} / {row[1]['load']} / {row[1]['store']} | {row[1]['insns'] / row[0]['insns']:.2f} |")
    print(f"| **hot set total** | {ta['insns']} / {ta['call']} / {ta['load']} / {ta['store']} | {tb['insns']} / {tb['call']} / {tb['load']} / {tb['store']} | {tb['insns'] / ta['insns']:.2f} |")

if __name__ == "__main__":
    main(sys.argv[1], sys.argv[2])
