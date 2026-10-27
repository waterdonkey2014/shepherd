#!/usr/bin/env python3
"""vita/llvm/cc_alloc_test.py <llc> <triple> <n signatures> <cc: "" = C | cc21> <seed> [agg]   (kit #5, vita/notes/kit5-llvm.md)
Does LLVM 16's C convention (AAPCS-VFP, thumbv7/armv7 -float-abi=hard) place every argument where Mono's armhf
ABI (mini-arm.c get_call_info: add_general / add_float, managed signatures) puts it?  And what does CC 21 do?
Random signatures over {i32, i64, f32, f64, ptr}; for each signature and each parameter k a function returning parameter k; the
register (or stack slot) it is read from is parsed from llc's assembly. Mono's assignment is simulated in Python (a line-by-line
copy of add_general / add_float with eabi_supported=1, i8_align=8; "agg" adds RegTypeStructByVal arguments as [k x i32] / [k x i64],
the way mini-llvm passes them: LLVMArgAsIArgs). Writes cc_alloc.ll in the current directory. Prints one summary line: how many
signatures agree, how many disagree although Mono keeps every float argument in s0-s15 (must be 0 for the C convention), how many
disagree with a float argument on Mono's stack (mini-llvm refuses those), and the float-free signatures' agreement (CC 21 = C there)."""
import random, subprocess, re, sys, collections
LLC = sys.argv[1]; TRIPLE = sys.argv[2]; N = int(sys.argv[3]); CC = sys.argv[4] if len(sys.argv) > 4 else ""   # "" = C, "cc21"
random.seed(int(sys.argv[5]) if len(sys.argv) > 5 else 1)
OPTS6 = sys.argv[6].split(",") if len(sys.argv) > 6 else []
AGG = "agg" in OPTS6
SE = "se" in OPTS6   # mode 2: a leading "ptr swifterror" parameter (the rgctx / IMT argument): must arrive in r8, the others where they were

def mono(sig):
    gr = 0; fpr = 0; spare = -1; st = 0; out = []
    for t in sig:
        if t in ("i32", "ptr"):
            if gr > 3: out.append(("stack", st)); st += 4
            else: out.append(("r", gr))
            gr += 1
        elif t == "i64":
            if gr >= 3:
                st = (st + 7) & ~7; out.append(("stack", st)); st += 8; gr += 1
            else:
                if gr & 1: gr += 1
                out.append(("r", gr)); gr += 1
            gr += 1
        elif t[0] in "sd":
            k = int(t[1:]); align = 8 if t[0] == "d" else 4; nwords = k * (2 if t[0] == "d" else 1)
            if align >= 8 and (gr & 1): gr += 1
            if gr > 3:
                st = (st + align - 1) & ~(align - 1); out.append(("stack", st)); st += nwords * 4
            else:
                n_in = min(nwords, 4 - gr); out.append(("r", gr)); gr += n_in
                st = (st + align - 1) & ~(align - 1); st += (nwords - n_in) * 4
        else:
            dbl = t == "f64"
            if fpr < 16 or (not dbl and spare >= 0):
                if dbl:
                    if fpr % 2: spare = fpr; fpr += 1
                    out.append(("s", fpr)); fpr += 2
                elif spare >= 0:
                    out.append(("s", spare)); spare = -1
                else:
                    out.append(("s", fpr)); fpr += 1
            else:
                if dbl: st = (st + 7) & ~7
                out.append(("stack", st)); st += 8 if dbl else 4
    return out

def gen(nsig):
    sigs = []
    for _ in range(nsig):
        n = random.randint(1, 22)
        w = random.choice([(1,1,4,3,1), (2,1,2,2,1), (0,0,1,1,0), (0,0,5,1,0), (1,1,1,1,1)])
        sigs.append(random.choices(["i32", "i64", "f32", "f64", "ptr"] + (["s1", "s2", "s3", "s4", "d1", "d2"] if AGG else []), weights=list(w) + ([1, 1, 1, 1, 1, 1] if AGG else []), k=n))
    return sigs

sigs = gen(N)
lines = [f'target triple = "{TRIPLE}"']
for si, sig in enumerate(sigs):
    LT = {"f32": "float", "f64": "double", "i32": "i32", "i64": "i64", "ptr": "ptr", "s1": "[1 x i32]", "s2": "[2 x i32]", "s3": "[3 x i32]", "s4": "[4 x i32]", "d1": "[1 x i64]", "d2": "[2 x i64]"}
    RT = {"s1": "i32", "s2": "i32", "s3": "i32", "s4": "i32", "d1": "i64", "d2": "i64"}
    params = ", ".join(f"{LT[t]} %p{i}" for i, t in enumerate(sig))
    if SE:
        params = "ptr swifterror %se, " + params
        lines.append(f"define {CC} ptr @f{si}_se({params}) {{\n  %v = load ptr, ptr %se\n  ret ptr %v\n}}")
    for k, t in enumerate(sig):
        rt = LT[t]
        if t in RT:
            lines.append(f"define {CC} {RT[t]} @f{si}_{k}({params}) {{\n  %e = extractvalue {rt} %p{k}, 0\n  ret {RT[t]} %e\n}}")
        else:
            lines.append(f"define {CC} {rt} @f{si}_{k}({params}) {{\n  ret {rt} %p{k}\n}}")
open("cc_alloc.ll", "w").write("\n".join(lines) + "\n")
asm = subprocess.run([LLC, f"-mtriple={TRIPLE}", "-mcpu=cortex-a9", "-mattr=+vfp2,-neon,+d32", "-float-abi=hard", "-O2",
                      "-frame-pointer=none", "cc_alloc.ll", "-o", "-"], capture_output=True, text=True, check=True).stdout
body = collections.defaultdict(list); cur = None
for l in asm.split("\n"):
    m = re.match(r"^(f\d+_(?:\d+|se)):", l)
    if m: cur = m.group(1); continue
    if cur and l.startswith("\t") and not l.startswith("\t.") : body[cur].append(l.strip())
def where(name, t):
    ins = [x for x in body[name] if not x.startswith("@")]
    ins = [x for x in ins if not re.match(r"^(bx\s+lr|pop|push|sub\s+sp|add\s+sp)", x)]
    if t in ("f32",):
        for x in ins:
            m = re.match(r"vmov(?:\.f32)?\s+s0,\s*s(\d+)", x)
            if m: return ("s", int(m.group(1)))
            m = re.match(r"vmov\s+s0,\s*r(\d+)", x)
            if m: return ("r", int(m.group(1)))
            m = re.match(r"vldr\s+s0,\s*\[sp(?:,\s*#(\d+))?\]", x)
            if m: return ("stack", int(m.group(1) or 0))
        if not ins: return ("s", 0)
    elif t == "f64":
        for x in ins:
            m = re.match(r"vmov(?:\.f64)?\s+d0,\s*d(\d+)", x)
            if m: return ("s", 2 * int(m.group(1)))
            m = re.match(r"vmov\s+d0,\s*r(\d+),\s*r(\d+)", x)
            if m: return ("r", int(m.group(1)))
            m = re.match(r"vldr\s+d0,\s*\[sp(?:,\s*#(\d+))?\]", x)
            if m: return ("stack", int(m.group(1) or 0))
        if not ins: return ("s", 0)
    elif t in ("i32", "ptr", "s1", "s2", "s3", "s4"):
        for x in ins:
            m = re.match(r"movs?\s+r0,\s*r(\d+)$", x)
            if m: return ("r", int(m.group(1)))
            m = re.match(r"ldr(?:\.w)?\s+r0,\s*\[sp(?:,\s*#(\d+))?\]", x)
            if m: return ("stack", int(m.group(1) or 0))
        if not ins: return ("r", 0)
    elif t in ("i64", "d1", "d2"):
        lo = None
        for x in ins:
            m = re.match(r"movs?\s+r0,\s*r(\d+)$", x)
            if m: return ("r", int(m.group(1)))
            if re.match(r"ldm(?:\.w)?\s+sp,\s*\{r0,\s*r1\}", x): return ("stack", 0)
            m = re.match(r"ldrd\s+r0,\s*r1,\s*\[sp(?:,\s*#(\d+))?\]", x)
            if m: return ("stack", int(m.group(1) or 0))
            m = re.match(r"ldr(?:\.w)?\s+r0,\s*\[sp(?:,\s*#(\d+))?\]", x)
            if m: return ("stack", int(m.group(1) or 0))
        if not ins: return ("r", 0)
    return ("?", " ; ".join(ins))

agree = disagree_fp_in_regs = disagree_other = unknown = 0; fp_in_regs_sigs = 0; ex = []; nofp = nofp_agree = 0
for si, sig in enumerate(sigs):
    m = mono(sig)
    l = [where(f"f{si}_{k}", t) for k, t in enumerate(sig)]
    if SE:
        se_ok = any(re.match(r"mov\s+r0,\s*r8$", x) for x in body[f"f{si}_se"])
        if not se_ok: unknown += 1; ex.append(("swifterror NOT read from r8", sig, body[f"f{si}_se"], l)); continue
    if any(w[0] == "?" for w in l): unknown += 1; ex.append(("unknown", sig, m, l)); continue
    fp_all_regs = all(m[k][0] == "s" and m[k][1] + (2 if t == "f64" else 1) <= 16 for k, t in enumerate(sig) if t in ("f32", "f64"))
    if fp_all_regs: fp_in_regs_sigs += 1
    if not any(t in ("f32", "f64") for t in sig): nofp += 1; nofp_agree += (m == l)
    if m == l: agree += 1
    elif fp_all_regs: disagree_fp_in_regs += 1; ex.append(("DISAGREE (Mono: every FP arg in s0-s15)", sig, m, l))
    else: disagree_other += 1; ex.append(("disagree (Mono puts an FP arg on the stack)", sig, m, l))
print(f"{TRIPLE} CC={CC or 'C'}: {len(sigs)} signatures; agree {agree}; disagree with every FP arg in registers {disagree_fp_in_regs}; "
      f"disagree with an FP arg on Mono's stack {disagree_other}; unparsed {unknown}; signatures with every FP arg in registers {fp_in_regs_sigs} "
      f"(float-carrying {fp_in_regs_sigs - nofp}); signatures without FP args {nofp}, of which agree {nofp_agree}")
for e in ex[:6]: print(" ", e[0], "\n    sig ", e[1], "\n    mono", e[2], "\n    llvm", e[3])
