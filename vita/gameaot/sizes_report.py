#!/usr/bin/env python3
"""sizes_report.py [GA_OUT]: the per-module table of the game-AOT size study (vita/gameaot/SIZES.md).

Reads (all under GA_OUT, default $SHEPHERD_WORK/gameaot/sizes):
  census/intro-il.tsv          AsmSize sizes of every assembly the menu+intro run loaded (IL bytes, methods, types, location)
  census/menu-assemblies-interp.txt   the menu-only run's assembly list (which owner modules the menu already loads)
  census/managedvita.txt       names of the rewritten Unity modules (runtime/ManagedVita)
  census/interp-methods.tsv    the menu+intro interpreter census (every method the interpreter transformed)
  aot-<variant>[-gsvtfix|-measure]/  aot-module.sh outputs (.log .time .rusage .dll.o -llvm.o .as.log)
Writes GA_OUT/sizes.tsv (one row per module and variant) and prints markdown tables.
Sizes are ELF section sizes of the relocatable objects (arm-vita-eabi-size -A): text = .text*, rodata = .rodata*,
data = .data*, bss = .bss* (Mono part + LLVM part). "gate" = text + rodata, what vita/shepherd/build.sh's 28 MiB check measures.
"""
import os, re, subprocess, sys, collections

GO = sys.argv[1] if len(sys.argv) > 1 else os.path.join(os.environ.get("SHEPHERD_WORK") or os.path.expanduser("~/shepherd-work"), "gameaot/sizes")
SIZE = os.path.expanduser("~/vitasdk/bin/arm-vita-eabi-size")
NM = os.path.expanduser("~/vitasdk/bin/arm-vita-eabi-nm")
MIB = 1048576.0
GATE = 28 * 1024 * 1024

def tsv(path):
    rows = [l.rstrip("\n").split("\t") for l in open(path) if l.strip() and not l.startswith("#")]
    hdr = rows[0]
    return [dict(zip(hdr, r)) for r in rows[1:]]

def sections(path):
    out = collections.Counter()
    if not os.path.exists(path):
        return out
    for l in subprocess.run([SIZE, "-A", path], capture_output=True, text=True).stdout.splitlines():
        p = l.split()
        if len(p) >= 2 and p[0].startswith(".") and p[1].isdigit():
            k = p[0]
            for c in ("text", "rodata", "data", "bss"):
                if k.startswith("." + c) or (c == "bss" and k == ".tbss"):
                    out[c] += int(p[1]); break
    return out

def sym_sizes_mono(path):
    """Mono-backend object: method sizes from address differences of consecutive code symbols (Mono's .s has no .size)."""
    syms = []
    for l in subprocess.run([NM, "-n", path], capture_output=True, text=True).stdout.splitlines():
        p = l.split()
        if len(p) == 3 and p[1] in "tT":
            syms.append((int(p[0], 16), p[2]))
    text = sections(path)["text"]
    out = {}
    for i, (a, n) in enumerate(syms):
        nxt = syms[i + 1][0] if i + 1 < len(syms) else text
        if n.startswith(".") or n.startswith("$"):
            continue
        out[n] = max(0, nxt - a)
    return out

def sym_sizes_llvm(path):
    out = {}
    for l in subprocess.run([NM, "-S", path], capture_output=True, text=True).stdout.splitlines():
        p = l.split()
        if len(p) == 4 and p[2] in "tT":
            out[p[3]] = int(p[1], 16)
    return out

NON_METHOD = re.compile(r'^(\.|\$|plt_|p_\d|mono_aot_|method_addresses|unbox_trampoline|specific_trampoline|static_rgctx|imt_trampoline|'
                        r'gsharedvt_arg|ftnptr_arg|unbox_arbitrary|llvm_|jit_code_|code_|gc\.safepoint|got|ut_|mono_eh_frame|_mono_|'
                        r'mono_|jit_|aot_wrapper)')

def debug_sym(full):
    """aot-compiler.c get_debug_sym () (no prefix, no collision suffix) applied to mono_method_full_name (m, TRUE)."""
    o = []; i = 0; n = len(full)
    while i < n:
        c = full[i]
        if i == 0 and c.isdigit():
            o.append("_")
        elif c.isascii() and c.isalnum():
            o.append(c)
        elif c == " " and full[i + 1:i + 3] == "()":
            i += 2
        elif c == "," and full[i + 1:i + 2] == " ":
            o.append("_"); i += 1
        elif c in "()>":
            pass
        else:
            o.append("_")
        i += 1
    return "".join(o)

def asm_sym(name):
    return re.sub(r"[^A-Za-z0-9_]", "_", name)

def parse_log(path):
    r = dict(compiled=0, total=0, skips=collections.Counter(), unable=collections.Counter())
    if not os.path.exists(path):
        return r
    for l in open(path, errors="replace"):
        m = re.match(r"Compiled: (\d+)/(\d+)", l)
        if m:
            r["compiled"], r["total"] = int(m.group(1)), int(m.group(2))
        m = re.match(r"Skip \(([^)]*)\):", l)
        if m:
            r["skips"][m.group(1)] += 1
        m = re.search(r"Unable to compile method .* due to: '([^']*)'", l)
        if m:
            r["unable"][re.sub(r"\d+", "N", m.group(1))[:90]] += 1
    return r

def parse_stats(path):
    """the 'Method stats:' block of the stats option: Normal / Instance / GSharedvt / Wrapper"""
    out = {}
    for l in open(path, errors="replace"):
        m = re.match(r"\s+(Normal|Instance|GSharedvt|Wrapper):\s+(\d+)", l)
        if m:
            out[m.group(1)] = int(m.group(2))
    return out

def kv(path):
    d = {}
    if os.path.exists(path):
        for t in open(path).read().split():
            if "=" in t:
                k, v = t.split("=", 1); d[k] = v
    return d

def main():
    il = {r["assembly"]: r for r in tsv(f"{GO}/census/intro-il.tsv")}
    owner = sorted(n for n, r in il.items() if "/intro/Data/Managed/" in r["location"])
    vita = set(open(f"{GO}/census/managedvita.txt").read().split())
    menu = set(l.split("\t")[0] for l in open(f"{GO}/census/menu-assemblies-interp.txt") if not l.startswith("#"))
    census = collections.defaultdict(list)
    for l in open(f"{GO}/census/interp-methods.tsv", errors="replace"):
        p = l.rstrip("\n").split("\t")
        if len(p) == 6 and p[0] in ("interp", "jit"):
            census[p[1]].append((p[5], int(p[3])))
    rows = []
    for m in owner:
        for var, tag in (("nollvm", ""), ("thumb", ""), ("nollvm", "partialni"), ("thumb", "partialni")):
            d = f"{GO}/aot-{var}" + (f"-{tag}" if tag else ""); status = "ok"
            if tag and not os.path.isdir(d):
                continue
            log = parse_log(f"{d}/{m}.log"); t = kv(f"{d}/{m}.time")
            o, lo = f"{d}/{m}.dll.o", f"{d}/{m}-llvm.o"
            if not os.path.exists(o):
                if t.get("failed") == "as" and os.path.exists(f"{d}-measure/{m}.dll.o"):
                    status = "as: branch out of range (size from the measure-only object)"; o = f"{d}-measure/{m}.dll.o"
                elif not tag and os.path.exists(f"{d}-gsvtfix/{m}.dll.o"):
                    status = "compiler crash (SIGBUS); built with the gsharedvt-unop fix"
                    log = parse_log(f"{d}-gsvtfix/{m}.log"); t = kv(f"{d}-gsvtfix/{m}.time")
                    o, lo = f"{d}-gsvtfix/{m}.dll.o", f"{d}-gsvtfix/{m}-llvm.o"
                else:
                    status = "FAILED"
            sm, sl = sections(o), sections(lo)
            ms = parse_stats(f"{d}/{m}.log") if os.path.exists(f"{d}/{m}.log") else {}
            if m == "UnityEngine.CoreModule" and not tag and status.startswith("compiler crash"):
                ms = parse_stats(f"{d}-gsvtfix/{m}.log")
            r = dict(module=m, variant=var + (f"-{tag}" if tag else ""), mstats=ms, kind="rewritten" if m in vita else ("derived-engine" if m.startswith("Shepherd.") else "game/third-party"),
                     menu="menu" if m in menu else "intro", file_bytes=int(il[m]["file_bytes"]), il_bytes=int(il[m]["il_bytes"]),
                     il_methods=int(il[m]["methods_with_body"]), compiled=log["compiled"], total=log["total"], status=status,
                     mono_text=sm["text"], llvm_text=sl["text"], rodata=sm["rodata"] + sl["rodata"], data=sm["data"] + sl["data"],
                     bss=sm["bss"] + sl["bss"], aot_s=int(t.get("aot_s", 0)), as_s=int(t.get("as_s", 0)), rss=int(t.get("rss", 0) or 0),
                     skips=dict(log["skips"]), unable=dict(log["unable"]))
            r["text"] = r["mono_text"] + r["llvm_text"]; r["gate"] = r["text"] + r["rodata"]
            # executed methods (menu+intro interpreter census) -> their compiled bodies in this module
            ex = census.get(m, [])
            sizes = sym_sizes_mono(o) if os.path.exists(o) else {}
            bodies_mono = {k: v for k, v in sizes.items() if not NON_METHOD.match(k)}
            pre = asm_sym(m) + "_"
            bodies_llvm = {k[len(pre):]: v for k, v in sym_sizes_llvm(lo).items() if k.startswith(pre)} if os.path.exists(lo) else {}
            bodies = dict(bodies_mono); bodies.update(bodies_llvm)
            matched = [bodies[debug_sym(n)] for n, _ in ex if debug_sym(n) in bodies]
            body_total = sum(bodies.values())
            avg = (sum(matched) / len(matched)) if matched else ((body_total / len(bodies)) if bodies else 0)
            est_bodies = sum(matched) + avg * (len(ex) - len(matched))
            scale = (r["gate"] / body_total) if body_total else 0      # tables, PLT, wrappers, EH data ~ proportional to code
            r.update(exec_methods=len(ex), exec_ir=sum(b for _, b in ex), exec_matched=len(matched), body_total=body_total,
                     exec_bodies=int(est_bodies), exec_gate_est=int(est_bodies * scale))
            rows.append(r)
    cols = ["module", "variant", "mstats", "kind", "menu", "file_bytes", "il_bytes", "il_methods", "compiled", "total", "status", "mono_text",
            "llvm_text", "text", "rodata", "gate", "data", "bss", "aot_s", "as_s", "rss", "exec_methods", "exec_ir", "exec_matched",
            "body_total", "exec_bodies", "exec_gate_est", "skips", "unable"]
    with open(f"{GO}/sizes.tsv", "w") as f:
        f.write("\t".join(cols) + "\n")
        for r in rows:
            f.write("\t".join(str(r[c]) for c in cols) + "\n")

    def M(x): return f"{x / MIB:.2f}"
    for var in [v for v in ("nollvm", "thumb", "nollvm-partialni", "thumb-partialni") if any(r["variant"] == v for r in rows)]:
        print(f"\n### {var}\n")
        print("| module | kind | loaded by | IL KiB | methods (IL) | compiled | text (Mono / LLVM) | rodata | gate (text+rodata) | data / bss | status / not compiled | AOT s | peak RSS MiB |")
        print("|---|---|---|---:|---:|---:|---:|---:|---:|---:|---|---:|---:|")
        vr = sorted([r for r in rows if r["variant"] == var], key=lambda r: -r["gate"])
        for r in vr:
            nc = r["total"] - r["compiled"]
            why = "; ".join(f"{k} {v}" for k, v in sorted(r["skips"].items(), key=lambda x: -x[1])) or ""
            why2 = "; ".join(f"{k} {v}" for k, v in sorted(r["unable"].items(), key=lambda x: -x[1]))
            st = r["status"] if r["status"] != "ok" else ""
            flag = " **> 28 MiB**" if r["gate"] >= GATE else ""
            txt = M(r["text"]) if var.startswith("nollvm") else f"{M(r['mono_text'])} / {M(r['llvm_text'])}"
            if "partial" in var:
                ms = r["mstats"]; why = f"normal {ms.get('Normal', 0)}, instances {ms.get('Instance', 0)}, gsharedvt {ms.get('GSharedvt', 0)}, wrappers {ms.get('Wrapper', 0)}"
            print(f"| {r['module']} | {r['kind']} | {r['menu']} | {r['il_bytes'] / 1024:.0f} | {r['il_methods']} | {r['compiled']}/{r['total']} | {txt} | "
                  f"{M(r['rodata'])} | {M(r['gate'])}{flag} | {r['data'] / 1024:.0f} / {r['bss'] / 1024:.0f} KiB | "
                  f"{'; '.join(x for x in ([st, why] if 'partial' in var else [st, (f'{nc}: ' + why) if nc and why else (str(nc) if nc else ''), why2]) if x)} | {r['aot_s']} | {r['rss'] / MIB:.0f} |")
        for grp, sel in (("rewritten Unity modules", lambda r: r["kind"] == "rewritten"), ("game / third-party", lambda r: r["kind"] == "game/third-party"),
                         ("derived engine", lambda r: r["kind"] == "derived-engine"), ("menu set", lambda r: r["menu"] == "menu"),
                         ("ALL (menu+intro)", lambda r: True), ("ALL minus Assembly-CSharp", lambda r: r["module"] != "Assembly-CSharp")):
            g = [r for r in vr if sel(r)]
            s = lambda k: sum(r[k] for r in g)
            print(f"| **{grp}** ({len(g)}) | | | {s('il_bytes') / 1024:.0f} | {s('il_methods')} | {s('compiled')}/{s('total')} | "
                  f"{M(s('text')) if var.startswith('nollvm') else M(s('mono_text')) + ' / ' + M(s('llvm_text'))} | {M(s('rodata'))} | **{M(s('gate'))}** | "
                  f"{s('data') / 1024:.0f} / {s('bss') / 1024:.0f} KiB | executed: {s('exec_methods')} methods, est. {M(s('exec_gate_est'))} MiB | {s('aot_s')} | |")


    # one row per module: the full A32 and Thumb-2 builds, the partial (profile-only, no-instances) builds, the executed set
    by = collections.defaultdict(dict)
    for r in rows:
        by[r["module"]][r["variant"]] = r
    print("\n### per module (full AOT: Mono A32 / LLVM Thumb-2; partial = profile-only + no-instances; sizes MiB, data/bss KiB)\n")
    print("| module | kind | first loaded | IL KiB | methods with IL | A32 compiled | A32 text | A32 data/bss | Thumb text Mono / LLVM | Thumb rodata | Thumb gate | Thumb data/bss | partial A32 | partial Thumb gate | executed (menu+intro) | not compiled (A32) |")
    print("|---|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|")
    for m in sorted(by, key=lambda m: -by[m]["nollvm"]["gate"]):
        a, t = by[m]["nollvm"], by[m]["thumb"]; pa, pt = by[m].get("nollvm-partialni"), by[m].get("thumb-partialni")
        nc = a["total"] - a["compiled"]
        note = a["status"] if a["status"] != "ok" else ""
        why = "; ".join(f"{k} {v}" for k, v in sorted(a["skips"].items(), key=lambda x: -x[1]))
        flag = lambda r: " **>28**" if r["gate"] >= GATE else ""
        print(f"| {m} | {a['kind']} | {a['menu']} | {a['il_bytes'] / 1024:.0f} | {a['il_methods']} | {a['compiled']}/{a['total']} | {M(a['text'])}{flag(a)} | "
              f"{a['data'] / 1024:.1f} / {a['bss'] / 1024:.0f} | {M(t['mono_text'])} / {M(t['llvm_text'])} | {M(t['rodata'])} | {M(t['gate'])}{flag(t)} | "
              f"{t['data'] / 1024:.1f} / {t['bss'] / 1024:.0f} | {M(pa['gate']) if pa else '-'} | {M(pt['gate']) if pt else '-'} | {a['exec_methods']} | "
              f"{'; '.join(x for x in [note, (f'{nc} ({why})' if why else str(nc)) if nc else ''] if x)} |")

if __name__ == "__main__":
    main()
