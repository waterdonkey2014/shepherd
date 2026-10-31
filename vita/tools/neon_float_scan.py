#!/usr/bin/env python3
"""Kit #9 fidelity (vita/notes/kit9-fidelity.md, roadmap "VFP-only float maths with FZ / DN off (never NEON for floats)"): find NEON
floating-point instructions in ARM objects / archives / linked ELFs. The A9's NEON unit always flushes denormals to zero and uses the default
NaN (it ignores FPSCR.FZ / DN), so float maths on NEON can differ from the PC's x64 SSE maths, which keeps denormals and NaN payloads; VFP
honours FPSCR. An instruction is NEON float when its mnemonic has a .f32 / .f16 type and none of its register operands is an S register (VFP
single precision uses S registers; NEON uses D / Q). VFP double precision (.f64 on D registers) is fine.
Kit #9 review: NEON float instructions are split into
  - EXACT ones, the same bits on NEON and VFP: constant moves (vmov.f32 d/q, #imm -- materialises a constant, no arithmetic) and float <->
    integer conversions (vcvt between .f32 and .s32 / .u32, fixed point too: truncation toward zero; a denormal input truncates to 0 on both
    units, NaN gives 0 on both, out-of-range saturates on both; integer -> float rounds to nearest on NEON, by FPSCR.RMode on VFP -- round to
    nearest, the PC's, which the FP lines check);
  - ARITHMETIC (everything else): the gate.
--map <ld map>: attributes each instruction of the first file (a linked ELF) to its input object (archive member) through the link map's .text input sections;
--allow <regex>: NEON arithmetic in objects whose path matches is allowed (the build allows vitaGL / math-neon -- the GPU's matrices and
uniforms -- and shepaudio's mixer -- audio samples: output the game's code never reads back).
  neon_float_scan.py [--fail] [--per-func N] [--map FILE --allow REGEX] <file.o | lib.a | elf> ...
Prints per file: NEON float arithmetic (outside / inside the allowed objects), the exact ones, by mnemonic, and the top functions; the last
line is the verdict. --fail: exit 1 when NEON float arithmetic is found outside the allowed objects."""
import sys, subprocess, re, collections, os, argparse, bisect
ap = argparse.ArgumentParser(); ap.add_argument("files", nargs="+"); ap.add_argument("--fail", action="store_true"); ap.add_argument("--per-func", type=int, default=8)
ap.add_argument("--map", help="the ld map of the (one) ELF given"); ap.add_argument("--allow", help="regex over input object paths where NEON float arithmetic is allowed")
ap.add_argument("--objdump", default=os.path.expanduser("~/vitasdk/bin/arm-vita-eabi-objdump")); o = ap.parse_args()
FN = re.compile(r"^[0-9a-f]+ <(.+)>:$")
REG = re.compile(r"\b([sdq])(\d+)\b")
LDST = re.compile(r"^v(ld|st)[1-4m]|^vldr|^vstr|^vpush|^vpop")
CVT_INT = re.compile(r"^vcvt\.(f32\.[su]32|[su]32\.f32)$")
ALLOW = re.compile(o.allow) if o.allow else None

def load_map(path):
    """the .text input sections of a GNU ld map: sorted (start, end, object) intervals"""
    iv = []; started = False; sec = None
    full = re.compile(r"^ (\.\S+)?\s+0x([0-9a-f]+)\s+0x([0-9a-f]+)\s+(\S.*)$")
    alone = re.compile(r"^ (\.\S+)\s*$")
    for line in open(path, errors="replace"):
        line = line.rstrip("\n")
        if not started:
            started = line.startswith("Linker script and memory map"); continue
        m = alone.match(line)
        if m: sec = m.group(1); continue
        m = full.match(line)
        if m:
            name = m.group(1) or sec; sec = None
            start, size, src = int(m.group(2), 16), int(m.group(3), 16), m.group(4).strip()
            if name and name.startswith(".text") and size and start and not src.startswith("*"):
                iv.append((start, start + size, src))
            continue
        if not line.startswith("  "): sec = None
    iv.sort(); return iv

intervals = load_map(o.map) if o.map else None
starts = [i[0] for i in intervals] if intervals else None
def where(addr, f):
    if not intervals or f != o.files[0]: return f   # (the map belongs to the first file, the linked ELF; objects after it are their own source)
    k = bisect.bisect_right(starts, addr) - 1
    if k >= 0 and intervals[k][0] <= addr < intervals[k][1]: return intervals[k][2]
    return "?"

bad_total = 0; allowed_total = 0; exact_total = 0
for f in o.files:
    try: out = subprocess.run([o.objdump, "-d", "--no-show-raw-insn", f], capture_output=True, text=True, errors="replace").stdout
    except FileNotFoundError: print("no objdump at", o.objdump); sys.exit(2)
    fn = "?"; bad_m = collections.Counter(); bad_f = collections.Counter(); ok_src = collections.Counter(); exact_m = collections.Counter()
    nbad = nok = nexact = nvfp = 0
    for line in out.splitlines():
        m = FN.match(line)
        if m: fn = m.group(1); continue
        p = line.split("\t")
        if len(p) < 2: continue
        mn = p[1].strip(); ops = p[2] if len(p) > 2 else ""
        if not mn.startswith("v") or LDST.match(mn): continue
        if ".f32" not in mn and ".f16" not in mn: continue
        regs = REG.findall(ops.split(";")[0].split("@")[0])
        if not regs or any(r[0] == "s" for r in regs): nvfp += 1; continue
        if (mn == "vmov.f32" and "#" in ops) or CVT_INT.match(mn): nexact += 1; exact_m[mn] += 1; continue
        try: addr = int(p[0].strip().rstrip(":"), 16)
        except ValueError: addr = -1
        src = where(addr, f)
        if ALLOW and ALLOW.search(src): nok += 1; ok_src[os.path.basename(src.split("(")[0]) if "(" in src else os.path.basename(src)] += 1; continue
        nbad += 1; bad_m[mn] += 1; bad_f[f"{fn}  [{os.path.basename(src)}]"] += 1
    bad_total += nbad; allowed_total += nok; exact_total += nexact
    print(f"{f}: {nbad} NEON float arithmetic instruction(s){' (outside the allowed objects)' if ALLOW else ''}"
          f"{' -- ' + ', '.join(f'{k} {v}' for k, v in bad_m.most_common(8)) if nbad else ''}"
          f"{'; allowed: ' + str(nok) + ' (' + ', '.join(f'{k} {v}' for k, v in ok_src.most_common(6)) + ')' if ALLOW else ''}"
          f"; exact NEON constant moves / int conversions: {nexact}{' (' + ', '.join(f'{k} {v}' for k, v in exact_m.most_common(4)) + ')' if nexact else ''}"
          f"; VFP single-precision: {nvfp}")
    for k, v in bad_f.most_common(o.per_func if nbad else 0): print(f"    {v:6d}  {k[:170]}")
verdict = "FAIL" if bad_total else "PASS"
print(f"TOTAL NEON float arithmetic instructions: {bad_total}{' outside the allowed objects (allowed: ' + str(allowed_total) + ')' if ALLOW else ''}; exact ones {exact_total} -- {verdict}")
sys.exit(1 if o.fail and bad_total else 0)
