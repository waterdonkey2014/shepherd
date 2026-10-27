#!/usr/bin/env python3
"""vita/llvm/check_eh_tramps.py (kit #5 llvm review fix A, vita/llvm/fork-eh-vfp.patch): read the throw trampolines a full-AOT CoreLib
object carries (System.Private.CoreLib.dll.o: throw_exception, rethrow_exception, rethrow_preserve_exception, throw_corlib_exception,
llvm_throw_corlib_exception(_abs)_trampoline, llvm_resume_unwind_trampoline -- emitted as raw A32 words by exceptions-arm.c's
get_throw_trampoline) and check the fp_regs pointer they pass to mono_arm_throw_exception*: the saved d0-d15 sit right above the
param area, so `add lr, sp, #K` must have K = the param area (`sub sp, sp, #P` after the d-register store). Stock Mono passes 8
always, which is wrong for the 16-byte param area of the managed throw / rethrow trampolines.
usage: check_eh_tramps.py <System.Private.CoreLib.dll.o>   exit 0 = every trampoline consistent (prints one line each)
       check_eh_tramps.py --expect-stock <o>               exit 0 = the stock offsets (8 everywhere): the switch-off check"""
import subprocess, sys, struct, tempfile, os

NAMES = ["throw_exception", "rethrow_exception", "rethrow_preserve_exception", "throw_corlib_exception",
         "llvm_throw_corlib_exception_trampoline", "llvm_throw_corlib_exception_abs_trampoline", "llvm_resume_unwind_trampoline"]

def main():
    args = sys.argv[1:]
    stock = "--expect-stock" in args
    args = [a for a in args if a != "--expect-stock"]
    o = args[0]
    nm = subprocess.run(["arm-vita-eabi-nm", "-S", o], capture_output=True, text=True, check=True).stdout
    syms = {}
    for line in nm.splitlines():
        p = line.split()
        if len(p) == 4 and p[3] in NAMES and p[2] in "tT":
            syms[p[3]] = (int(p[0], 16), int(p[1], 16))
    with tempfile.TemporaryDirectory(dir=os.environ.get("TMPDIR")) as td:
        b = os.path.join(td, "text.bin")
        subprocess.run(["arm-vita-eabi-objcopy", "-O", "binary", "--only-section=.text", o, b], check=True)
        text = open(b, "rb").read()
    bad = 0
    if not syms:
        print("no throw trampolines in", o); return 2
    for n in NAMES:
        if n not in syms:
            continue
        a, sz = syms[n]
        words = [struct.unpack_from("<I", text, a + i)[0] for i in range(0, sz - sz % 4, 4)]
        subs = [w & 0xff for w in words if (w & 0xfffff000) == 0xe24dd000]          # sub sp, sp, #imm8 (rot 0)
        adds = [w & 0xff for w in words if (w & 0xfffff000) == 0xe28de000]          # add lr, sp, #imm8 (rot 0)
        if len(subs) < 2 or len(adds) != 1:
            print(f"{n}: unexpected shape (sub sp {subs}, add lr {adds})"); bad += 1; continue
        param, fpo = subs[1], adds[0]    # subs[0] = the 128-byte d-register area
        ok = (fpo == 8) if stock else (fpo == param)
        bad += not ok
        print(f"{n}: param area {param} B, fp_regs = sp + {fpo} -> {'ok' if ok else 'WRONG'}{' (stock expected)' if stock else ''}")
    return 1 if bad else 0

sys.exit(main())
