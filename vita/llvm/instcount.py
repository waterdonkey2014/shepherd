#!/usr/bin/env python3
"""instcount.py [--module M] [--all] <aot dir>...: static per-method instruction counts of the Vita AOT outputs (no device):
the same methods in each variant (nollvm = Mono's A32 backend, arm = LLVM A32, thumb = LLVM Thumb-2), for the engine's hot paths
(engine-pass-speed.md §1/§2: DrawWorld + vertex build, the transform cache, M.Mul/M.Inverse, the Registry, the UI pass).

How: llvm-objdump -d (Xcode's, ARM/Thumb ELF) of <M>.dll.o (Mono part) and <M>-llvm.o (LLVM part), one block per method symbol.
 * Mono's .s emits machine code as .byte data, so its objects carry $d mapping symbols over code; a temporary copy with every
   "$d" mapping symbol renamed "$a" makes objdump decode it as A32. Mono's inline constants (ldr rX, [pc]; b +0; .word K) are
   recognised and counted as data words, not instructions.
 * LLVM objects disassemble as they are ($a/$t/$d mapping symbols are right); their literal-pool words count as data words.
Categories: calls (bl/blx), branches (b<cc>, cbz/cbnz, tbb/tbh, bx other than bx lr), loads (ldr*/ldm/pop/vldr/vld1/vpop),
stores (str*/stm/push/vstr/vst1/vpush), FP/SIMD (other v* ops), coop safepoint polls (calls to *state_poll* in the Mono part;
in LLVM code the poll is inlined: load + compare + a branch to a cold call of the icall cold wrapper of mono_threads_state_poll,
counted through the call's relocation: llvm-objdump -dr).
Static counts are not time: they say how much code a path executes at most once per instruction, not which paths run.
"""
import os, re, subprocess, sys, tempfile, collections

OBJDUMP = ["xcrun", "llvm-objdump"]   # Apple llvm-objdump (ARM/Thumb ELF): GNU objdump takes >10 min on an LLVM object with ~70k sections
HOT = [  # (label, regex on the normalised method name)
    ("SceneRenderer.DrawWorld", r"Render_SceneRenderer_DrawWorld"),
    ("SceneRenderer.Item", r"Render_SceneRenderer_Item_"),
    ("SceneRenderer.SortWorld", r"Render_SceneRenderer_SortWorld"),
    ("SceneRenderer.EmitSprite", r"Render_SceneRenderer_EmitSprite"),
    ("SceneRenderer.EmitMesh", r"Render_SceneRenderer_EmitMesh"),
    ("SceneRenderer.Project", r"Render_SceneRenderer_Project"),
    ("SceneRenderer.Vertex", r"Render_SceneRenderer_Vertex_"),
    ("SceneRenderer.Submit", r"Render_SceneRenderer_Submit"),
    ("SceneRenderer.Handle", r"Render_SceneRenderer_Handle"),
    ("SceneRenderer.ShaderOf", r"Render_SceneRenderer_ShaderOf"),
    ("SceneRenderer.DrawOverlayCanvases", r"Render_SceneRenderer_DrawOverlayCanvases"),
    ("SceneRenderer.DrawUiTree", r"Render_SceneRenderer_DrawUiTree"),
    ("SceneRenderer.DrawCanvasRenderer", r"Render_SceneRenderer_DrawCanvasRenderer"),
    ("SceneRenderer.NotifyRectChanges", r"Render_SceneRenderer_NotifyRectChanges"),
    ("M.Mul (Matrix4x4)", r"Native_M_Mul_UnityEngine_Matrix4x4_UnityEngine_Matrix4x4$"),
    ("M.Inverse", r"Native_M_Inverse_UnityEngine_Matrix4x4$"),
    ("M.Point", r"Native_M_Point_UnityEngine_Matrix4x4_UnityEngine_Vector3$"),
    ("M.TRS", r"Native_M_TRS_"),
    ("NTransform.get_LocalToWorld", r"Native_NTransform_get_LocalToWorld$"),
    ("NTransform.get_LocalMatrix", r"Native_NTransform_get_LocalMatrix$"),
    ("NTransform.WorldStamp", r"Native_NTransform_WorldStamp$"),
    ("NTransform.get_Rect", r"Native_NTransform_get_Rect$"),
    ("NTransform.get_Position", r"Native_NTransform_get_Position$"),
    ("NTransform.get_WorldToLocal", r"Native_NTransform_get_WorldToLocal$"),
    ("NTransform.set_LocalPosition", r"Native_NTransform_set_LocalPosition_"),
    ("NTransform.MarkChanged", r"Native_NTransform_MarkChanged$"),
    ("Registry.Drain", r"Native_Registry_Drain$"),
    ("Registry.Items", r"Native_Registry_Items$"),
    ("Registry.Insert", r"Native_Registry_Insert_"),
    ("Registry.IsWorldRenderer", r"Native_Registry_IsWorldRenderer_"),
    ("Objects.Bind", r"Native_Objects_Bind_"),
    ("Icalls.UiAlpha", r"RenderIcalls_UiAlpha"),
    ("LazyRef.Resolve", r"Assets_LazyRef_Resolve"),
]
CC = r"(eq|ne|cs|hs|cc|lo|mi|pl|vs|vc|hi|ls|ge|lt|gt|le|al)?"
CAT_RE = [("call", re.compile(r"^blx?" + CC + r"(\.w|\.n)?$")),   # A32 has conditional calls (blne), Thumb-2 does not
          ("load", re.compile(r"^(ldr|ldm|pop|vldr|vld[1-4]|vpop|ldrex)")),
          ("store", re.compile(r"^(str|stm|push|vstr|vst[1-4]|vpush|strex)")),
          ("branch", re.compile(r"^(b(eq|ne|cs|hs|cc|lo|mi|pl|vs|vc|hi|ls|ge|lt|gt|le|al)?(\.w|\.n)?|cbn?z|tbb|tbh)$")),
          ("fp", re.compile(r"^v"))]

def patched_copy(obj):
    """copy of obj whose "$d" mapping symbols are renamed "$a" (ELF .strtab patched in place: same length)"""
    d = bytearray(open(obj, "rb").read())
    import struct
    shoff = struct.unpack_from("<I", d, 0x20)[0]; shentsize, shnum, shstrndx = struct.unpack_from("<HHH", d, 0x2e)
    if shnum == 0: shnum = struct.unpack_from("<I", d, shoff + 20)[0]
    for i in range(shnum):
        s = struct.unpack_from("<IIIIIIIIII", d, shoff + i * shentsize)
        if s[1] == 2:  # SYMTAB -> its string table
            st = struct.unpack_from("<IIIIIIIIII", d, shoff + s[6] * shentsize)
            o, n = st[4], st[5]
            blob = bytes(d[o:o + n]).replace(b"\0$d\0", b"\0$a\0").replace(b"\0$d.", b"\0$a.")
            d[o:o + n] = blob
    f = tempfile.NamedTemporaryFile(suffix=".o", delete=False, dir=os.environ.get("TMPDIR"))
    f.write(d); f.close()
    return f.name

def functions(obj, mono_part):
    path = patched_copy(obj) if mono_part else obj
    out = subprocess.run(OBJDUMP + ["-dr", path], capture_output=True, text=True).stdout
    if mono_part: os.unlink(path)
    funcs = {}; cur = None
    for line in out.splitlines():
        m = re.match(r"^[0-9a-f]+ <([^>]+)>:$", line)
        if m:
            name = m.group(1)
            cur = None if name.startswith(("$", ".L")) else funcs.setdefault(name, dict(insns=0, words=0, bytes=0, call=0, load=0, store=0, branch=0, fp=0, ret=0, poll=0, _prev=[]))
            continue
        if cur is None: continue
        mr = re.match(r"^\s+[0-9a-f]+:\s+(R_ARM_\S+)\s+(\S+)", line)
        if mr:   # relocation of the previous instruction: a call's real target (LLVM objects call undefined/global symbols)
            if cur.get("_lastcall") and re.search(r"state_poll|icall_cold_wrapper", mr.group(2)): cur["poll"] += 1
            cur["_lastcall"] = False
            continue
        m = re.match(r"^\s+([0-9a-f]+):\s+((?:[0-9a-f]{2,8} )+)\s*\t(\S+)\s*(.*)$", line)
        if not m: continue
        size = sum(len(g) for g in m.group(2).split()) // 2      # A32 4, Thumb-2 2 or 4, data 1/2/4
        mn = m.group(3); ops = m.group(4)
        if mn in (".word", ".short", ".byte"):
            cur["words"] += 1; cur["bytes"] += size; continue
        p = cur["_prev"]
        # Mono inline constant: ldr rX, [pc] (offset 0) ; b <next+4> ; <word>
        if mono_part and len(p) >= 2 and p[-2][0] == "ldr" and re.search(r"\[pc(, #0)?\]", p[-2][1]) and p[-1][0] == "b":
            cur["words"] += 1; cur["bytes"] += size; cur["_prev"] = []; continue
        cur["insns"] += 1; cur["bytes"] += size
        base = mn.split(".")[0] if not mn.startswith("v") else mn
        if (mn.startswith("bx") and "lr" in ops) or (mn.startswith(("pop", "ldm")) and "pc" in ops):
            cur["ret"] += 1
        cur["_lastcall"] = False
        for cat, rx in CAT_RE:
            if rx.match(mn):
                cur[cat] += 1
                if cat == "call":
                    if re.search(r"state_poll|icall_cold_wrapper", ops): cur["poll"] += 1
                    else: cur["_lastcall"] = True
                break
        p.append((mn.split(".")[0], ops)); del p[:-2]
    for f in funcs.values():
        del f["_prev"]; f.pop("_lastcall", None)
    return funcs

def normalise(name, llvm_prefix):
    for pre in llvm_prefix:
        if name.startswith(pre): return name[len(pre):]
    return name

def variant(d, module):
    mono = functions(os.path.join(d, module + ".dll.o"), True)
    llvm_o = os.path.join(d, module + "-llvm.o")
    llvm = functions(llvm_o, False) if os.path.exists(llvm_o) else {}
    pre = [module.replace(".", "_") + "_"] if llvm else []
    res = {}
    for n, f in mono.items(): res[normalise(n, pre)] = dict(f, part="mono")
    for n, f in llvm.items(): res[normalise(n, pre)] = dict(f, part="llvm")
    return res

def main(args):
    module = "Shepherd.Engine"; show_all = False
    while args and args[0].startswith("--"):
        if args[0] == "--module": module = args[1]; args = args[2:]
        elif args[0] == "--all": show_all = True; args = args[1:]
    vs = [(os.path.basename(d.rstrip("/")), variant(d, module)) for d in args]
    names = [n for n, _ in vs]
    print(f"## {module}: static per-method counts ({', '.join(names)})\n")
    print("| method | " + " | ".join(f"{n} insns / bytes / calls / ld / st / br / fp" for n in names) + " | " + " | ".join(f"{n}/{names[0]} insns" for n in names[1:]) + " |")
    print("|---|" + "---:|" * (len(names) * 2 - 1))
    totals = collections.defaultdict(lambda: collections.Counter())
    for label, rx in HOT:
        r = re.compile(rx)
        row = []; ins = []
        for n, v in vs:
            hits = [k for k in v if r.search(k) and not k.startswith(("plt_", "p_", "ut_", "wrapper_"))]
            if not hits: row.append("–"); ins.append(None); continue
            agg = collections.Counter()
            for k in hits:
                for c in ("insns", "bytes", "call", "load", "store", "branch", "fp", "poll", "words"): agg[c] += v[k][c]
            totals[n].update(agg)
            parts = "/".join(sorted(set(v[k]["part"] for k in hits)))
            row.append(f"{agg['insns']} / {agg['bytes']} / {agg['call']} / {agg['load']} / {agg['store']} / {agg['branch']} / {agg['fp']}" + (f" ({len(hits)} fns)" if len(hits) > 1 else "") + ("" if parts == "mono" or n.endswith("nollvm") else f" [{parts}]"))
            ins.append(agg["insns"])
        ratios = [f"{i / ins[0]:.2f}" if (i and ins[0]) else "–" for i in ins[1:]]
        print(f"| {label} | " + " | ".join(row) + " | " + " | ".join(ratios) + " |")
    t0 = totals[names[0]]
    print(f"| **hot set total** | " + " | ".join(f"{totals[n]['insns']} / {totals[n]['bytes']} / {totals[n]['call']} / {totals[n]['load']} / {totals[n]['store']} / {totals[n]['branch']} / {totals[n]['fp']}" for n in names)
          + " | " + " | ".join((f"{totals[n]['insns'] / t0['insns']:.2f}" if t0['insns'] else "–") for n in names[1:]) + " |\n")
    print("Whole image (every method symbol; Mono part + LLVM part):\n")
    print("| variant | methods | instructions | code bytes | data words | calls | loads | stores | branches | FP/SIMD | safepoint-poll calls |")
    print("|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|")
    for n, v in vs:
        a = collections.Counter(); k = 0
        for name, f in v.items():
            if name.startswith(("plt_", "p_", "ut_", "mono_aot_", "method_addresses", "jit_code", "llvm_code", "gc.safepoint")): continue
            k += 1
            for c in ("insns", "bytes", "words", "call", "load", "store", "branch", "fp", "poll"): a[c] += f[c]
        print(f"| {n} | {k} | {a['insns']} | {a['bytes']} | {a['words']} | {a['call']} | {a['load']} | {a['store']} | {a['branch']} | {a['fp']} | {a['poll']} |")
    if show_all:
        for n, v in vs:
            for name in sorted(v): print(n, name, v[name])

if __name__ == "__main__":
    main(sys.argv[1:])
