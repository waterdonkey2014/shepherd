#!/usr/bin/env python3
"""coldwrap.py <image.dylib>...: (Mac arm64 Mono LLVM AOT images) is every LLVM helper that precedes the first method of the
method-address table -- the GC-poll cold wrapper `<prefix>_icall_cold_wrapper_N`, emitted first in every LLVM module -- inside the
runtime's LLVM code range? aot-runtime.c compute_llvm_code_range takes [first method, last method + len) from the EH table, so
code before the lowest method address is outside it: a PLT call from there (the GC poll's first call) asserts `plt_entry`
(mono_aot_get_plt_entry finds no module). Prints per image: lowest method address, cold-wrapper address, verdict."""
import struct, subprocess, sys
def check(p):
    syms = []
    for l in subprocess.run(["nm", "-n", p], capture_output=True, text=True).stdout.splitlines():
        x = l.split()
        if len(x) == 3: syms.append((int(x[0], 16), x[2]))
    sd = {n: a for a, n in syms}
    if "method_addresses" not in sd: return None
    a, b = sd["method_addresses"], sd["method_addresses_end"]; d = open(p, "rb").read(); tg = []
    for i in range((b - a) // 4):
        w = struct.unpack_from("<I", d, a + 4 * i)[0]
        if (w >> 26) == 0b100101:
            imm = w & 0x3ffffff
            if imm & (1 << 25): imm -= 1 << 26
            t = a + 4 * i + imm * 4
            if t != a: tg.append(t)
    cw = [ad for ad, n in syms if "_icall_cold_wrapper_" in n]
    lo = min(tg) if tg else None
    bad = [c for c in cw if lo is None or c < lo]
    return lo, cw, bad
for p in sys.argv[1:]:
    r = check(p)
    if r is None: print(f"{p}\tno method table"); continue
    lo, cw, bad = r
    print(f"{p.split('/')[-2]}/{p.split('/')[-1]}\tlowest method {hex(lo) if lo is not None else None}\tcold wrappers {[hex(c) for c in cw]}\t{'OUTSIDE (unsafe)' if bad else 'inside'}")
