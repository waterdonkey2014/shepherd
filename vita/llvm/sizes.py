#!/usr/bin/env python3
"""sizes.py <aot dir> [<aot dir> ...]: per-module section sizes of the Vita AOT outputs written by vita/llvm/aot-vita.sh.
For each module: the Mono part (<M>.dll.o: tables, PLT, trampolines and the methods Mono's own backend compiled) and, for LLVM
variants, the LLVM part (<M>-llvm.o: the LLVM-compiled methods, the Mono EH table, the LLVM GOT and the AOT data tables).
Columns: .text (code), .rodata, .data+.bss, methods (code symbols that are methods, see method_syms), "text+rodata" (what the
existing 28 MiB gate in vita/shepherd/build.sh measures with `size`). --tsv writes machine-readable rows too.
"""
import os, re, subprocess, sys

SIZE = os.path.expanduser("~/vitasdk/bin/arm-vita-eabi-size")
NM = os.path.expanduser("~/vitasdk/bin/arm-vita-eabi-nm")
GATE = 28 * 1024 * 1024
# non-method code symbols the Mono AOT compiler emits (tables, PLT, trampolines, wrappers' helpers) -- everything else in a code
# section is a compiled method body
NON_METHOD = re.compile(r'^(\.|\$|plt_|p_\d|mono_aot_|method_addresses|unbox_trampoline|specific_trampoline|static_rgctx|imt_trampoline|gsharedvt_arg|ftnptr_arg|unbox_arbitrary|llvm_|jit_code_|code_|gc\.safepoint|got|ut_|mono_eh_frame|_mono_)')

def sections(path):
    out = {}
    for l in subprocess.run([SIZE, "-A", path], capture_output=True, text=True).stdout.splitlines():
        p = l.split()
        if len(p) >= 2 and p[0].startswith(".") and p[1].isdigit():
            k = p[0]
            if k.startswith(".text"): k = ".text"
            elif k.startswith(".rodata"): k = ".rodata"
            elif k.startswith(".data") or k.startswith(".bss") or k == ".tbss": k = ".databss"
            out[k] = out.get(k, 0) + int(p[1])
    return out

def method_syms(path):
    n = 0
    for l in subprocess.run([NM, path], capture_output=True, text=True).stdout.splitlines():
        p = l.split()
        if len(p) == 3 and p[1] in "tT" and not NON_METHOD.match(p[2]):
            n += 1
    return n

def module_rows(d):
    rows = []
    for f in sorted(os.listdir(d)):
        if not f.endswith(".dll.o"): continue
        m = f[:-6]
        mono = os.path.join(d, f); llvm = os.path.join(d, m + "-llvm.o")
        sm = sections(mono); sl = sections(llvm) if os.path.exists(llvm) else {}
        r = dict(module=m,
                 mono_text=sm.get(".text", 0), llvm_text=sl.get(".text", 0),
                 rodata=sm.get(".rodata", 0) + sl.get(".rodata", 0),
                 databss=sm.get(".databss", 0) + sl.get(".databss", 0),
                 mono_methods=method_syms(mono), llvm_methods=method_syms(llvm) if sl else 0)
        r["text"] = r["mono_text"] + r["llvm_text"]
        r["gate"] = r["text"] + r["rodata"]
        rows.append(r)
    return rows

def mib(x): return x / 1048576.0

def main(args):
    tsv = None
    if args and args[0] == "--tsv":
        tsv = open(args[1], "w"); args = args[2:]
        tsv.write("dir\tmodule\tmono_text\tllvm_text\ttext\trodata\tdatabss\tmono_methods\tllvm_methods\n")
    for d in args:
        rows = module_rows(d)
        print(f"## {os.path.basename(d.rstrip('/'))}")
        print("| module | .text Mono part | .text LLVM part | .text total | .rodata | .data+.bss | methods Mono / LLVM | text+rodata (gate) |")
        print("|---|---:|---:|---:|---:|---:|---:|---:|")
        t = dict(mono_text=0, llvm_text=0, text=0, rodata=0, databss=0, mono_methods=0, llvm_methods=0, gate=0)
        for r in rows:
            flag = "" if r["gate"] < GATE else " **over 28 MiB**"
            print(f"| {r['module']} | {mib(r['mono_text']):.2f} | {mib(r['llvm_text']):.2f} | {mib(r['text']):.2f} | {mib(r['rodata']):.2f} | "
                  f"{mib(r['databss']):.2f} | {r['mono_methods']} / {r['llvm_methods']} | {mib(r['gate']):.2f}{flag} |")
            for k in t: t[k] += r[k]
            if tsv: tsv.write("\t".join(str(x) for x in [d, r["module"], r["mono_text"], r["llvm_text"], r["text"], r["rodata"], r["databss"], r["mono_methods"], r["llvm_methods"]]) + "\n")
        print(f"| **total** | {mib(t['mono_text']):.2f} | {mib(t['llvm_text']):.2f} | {mib(t['text']):.2f} | {mib(t['rodata']):.2f} | {mib(t['databss']):.2f} | "
              f"{t['mono_methods']} / {t['llvm_methods']} | {mib(t['gate']):.2f} |\n")

if __name__ == "__main__":
    main(sys.argv[1:])
