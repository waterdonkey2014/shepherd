#!/usr/bin/env python3
"""vita/llvm/cc_consistency.py <file.ll | ->   (kit #5, vita/notes/kit5-llvm.md; e.g. llvm-dis <M>.dll.s.bc -o - | cc_consistency.py -)
In a module's LLVM IR (llvm-dis of <M>.dll.s.bc / .opt.bc), every call whose callee is a function DEFINED in the module
must use that function's calling convention (a mismatch is undefined behaviour: LLVM may turn the call into unreachable).
Kit #5: also the swifterror (r8) argument -- a call to a defined function must pass swifterror at the parameter index where the
definition has it (or neither has one), and every swifterror argument must be an alloca marked swifterror (the verifier's rule).
Also counts definitions / calls per calling convention."""
import re, sys, collections
txt = sys.stdin.read() if sys.argv[1] == "-" else open(sys.argv[1]).read()
defs = {}; def_se = {}
def se_index(params):
    """index of the swifterror parameter / argument in a comma list (top level), or -1"""
    depth = 0; cur = ""; parts = []
    for ch in params:
        if ch in "([{<": depth += 1
        if ch in ")]}>": depth -= 1
        if ch == "," and depth == 0: parts.append(cur); cur = ""
        else: cur += ch
    if cur.strip(): parts.append(cur)
    for i, p in enumerate(parts):
        if re.search(r'\bswifterror\b', p): return i
    return -1
CCRE = re.compile(r'\b(monocc|coldcc|fastcc|swiftcc|cc\d+)\b')
def ccof(prefix):
    m = CCRE.search(prefix); return m.group(1) if m else "c"
for m in re.finditer(r'^define\b([^@\n]*)@("[^"]+"|[-\w.$]+)\((.*)$', txt, re.M):
    defs[m.group(2)] = ccof(m.group(1))
    rest = m.group(3); depth = 1; i = 0
    while i < len(rest) and depth:
        depth += {"(": 1, ")": -1}.get(rest[i], 0); i += 1
    def_se[m.group(2)] = se_index(rest[:i - 1])
decls = {}
for m in re.finditer(r'^declare\b([^@\n]*)@("[^"]+"|[-\w.$]+)\(', txt, re.M):
    decls[m.group(2)] = ccof(m.group(1))
calls = collections.Counter(); bad = []; ncall_def = 0; se_bad = []; se_calls = 0
for m in re.finditer(r'\b(?:call|invoke)\s+([^@%\n(]*?)\s*[@%]("[^"]+"|[-\w.$]+)\(', txt):
    cc = ccof(m.group(1)); callee = m.group(2); calls[cc] += 1
    rest = txt[m.end():m.end() + 4000]; depth = 1; i = 0
    while i < len(rest) and depth:
        depth += {"(": 1, ")": -1}.get(rest[i], 0); i += 1
    csi = se_index(rest[:i - 1]); se_calls += csi >= 0
    if callee in defs:
        ncall_def += 1
        if defs[callee] != cc: bad.append((callee, defs[callee], cc))
        if def_se[callee] != csi: se_bad.append((callee, def_se[callee], csi))
print(f"{sys.argv[1]}: definitions by CC {dict(collections.Counter(defs.values()))}; calls by CC {dict(calls)}; "
      f"calls to defined functions {ncall_def}, CC mismatches {len(bad)}; definitions with a swifterror parameter "
      f"{sum(1 for v in def_se.values() if v >= 0)}, calls passing swifterror {se_calls}, swifterror position mismatches with the "
      f"callee's definition {len(se_bad)}; swifterror allocas {len(re.findall(r'= alloca swifterror ', txt))}")
for b in bad[:20]: print("  MISMATCH", b)
for b in se_bad[:20]: print("  SWIFTERROR MISMATCH (callee, def index, call index)", b)
sys.exit(1 if bad or se_bad else 0)
