#!/usr/bin/env python3
"""V6 stage 1: the leave-one-out table of vita/v6/stage1.sh (runtime/logs/v6/s1m<config>-{mem,census,capgrid.txt})."""
import os, re, sys

HERE = os.path.dirname(os.path.abspath(__file__))
LOGS = os.path.join(HERE, "..", "..", "..", "runtime", "logs", "v6")
CONFIGS = [
    ("base", "all stage-1 parts off, pre-stage-1 pack"),
    ("all", "stage 1: every part on, the --language en pack"),
    ("-saves", "(b) save streams preallocated (16 + 1 MiB)"),
    ("-language", "(a) no language trim: all 21 I2 languages, CJK/Arabic atlases carried, no engine trim"),
    ("-languagepack", "(a) pack keeps all 21 languages + the CJK/Arabic atlases; the engine trims I2 at load (SHEPHERD_LANGUAGE=en)"),
    ("-packtrims", "(a)+(d) pre-stage-1 pack (all languages, joystick maps), engine parts on"),
    ("-joystickmaps", "(d) the 334 HardwareJoystickMaps carried"),
    ("-newtonsoft", "(e) Newtonsoft's XmlNode/DataSet/... converters kept"),
    ("-scans", "(e) BlendModes / PostProcessing / Febucci all-assembly type scans kept"),
    ("-builtin", "(e) Loader.BuiltinType scans every UnityEngine* assembly"),
    ("-boxes", "(f) a fresh box per decoded primitive"),
    ("-keys", "(f) a key list per decoded struct"),
    ("-atlas", "(f) every atlas sprite decodes its whole SpriteAtlas"),
    ("-lazytext", "(f) pack TextAssets keep their bytes"),
    ("-alpha8", "(f) SDF uploads of pack images go through RGBA8"),
]

def read(p):
    try:
        with open(p, errors="replace") as f: return f.read()
    except OSError: return ""

def mem(tag):
    d = os.path.join(LOGS, tag + "-mem")
    log, vm = read(os.path.join(d, "engine.log")), read(os.path.join(d, "vmmap-summary.txt"))
    live = re.search(r"LIVE after = ([\d.]+) MiB", log)
    zone = re.search(r"DefaultMallocZone\S*\s+\S+\s+\S+\s+\S+\s+\S+\s+\d+\s+([\d.]+)([KMG])", vm)
    foot = re.search(r"Physical footprint:\s+([\d.]+)([KMG])", vm)
    asm = read(os.path.join(d, "assemblies.txt")).splitlines()[1:]
    n, b = len(asm), sum(int(l.split("\t")[2]) for l in asm if l.count("\t") >= 3 and l.split("\t")[2].isdigit())
    done = re.findall(r"done: (\d+) errors, (\d+) exceptions", log)
    def mib(m): return None if not m else float(m.group(1)) * {"K": 1 / 1024, "M": 1, "G": 1024}[m.group(2)]
    return dict(live=float(live.group(1)) if live else None, malloc=mib(zone), foot=mib(foot), asm=n, dll=b / 1048576 if n else None,
                errs="/".join(done[-1]) if done else "?")

def census(tag):
    c = read(os.path.join(LOGS, tag + "-census", "console.txt"))
    inf = re.search(r"Inflated types:\s+(\d+)", c); meth = re.findall(r"^Methods:\s+(\d+)", c, re.M)
    interp = re.search(r"\[interp-census\] (\d+) interpreted methods.*?IR ([\d.]+) MiB", c)
    return dict(inflated=int(inf.group(1)) if inf else None, methods=int(meth[0]) if meth else None,
                interp=int(interp.group(1)) if interp else None, ir=float(interp.group(2)) if interp else None)

def cap(tag):
    rows = []
    text = read(os.path.join(LOGS, tag + "-capgrid.txt"))
    if not text.strip() and tag == "s1mbase": text = read(os.path.join(LOGS, "s1-base-capgrid.txt"))   # the pre-stage-1 engine's grid
    for l in text.splitlines():
        m = re.match(r"(\w+)\s+cap\s+(\d+)m", l)
        if m: rows.append((int(m.group(2)), m.group(1)))
    if not rows: return None
    rows.sort()
    clean = None
    for mib, v in reversed(rows):           # the lowest cap from which every larger probed cap passes
        if v != "PASS": break
        clean = mib
    survive = min((m for m, v in rows if v in ("PASS", "OOM")), default=None)
    crash = max((m for m, v in rows if v in ("CRASH", "FAIL")), default=None)
    return dict(clean=clean, survive=survive, crash=crash, probed=f"{rows[0][0]}-{rows[-1][0]}")

def f(v, fmt="{:.1f}"): return "—" if v is None else fmt.format(v)

def main():
    res = {}
    for tag, _ in CONFIGS:
        t = "s1m" + tag
        res[tag] = dict(**mem(t), **census(t), cap=cap(t))
    all_ = res["all"]
    print("| config | what is off | errors/exc | live heap MiB (f1300, after a full GC) | Δ vs all | malloc MiB | Δ | assemblies (MiB of DLLs) | inflated types | interp methods (IR MiB) | heap cap: clean / survives / crash |")
    print("|---|---|---|---|---|---|---|---|---|---|---|")
    for tag, what in CONFIGS:
        r = res[tag]
        dl = None if r["live"] is None or all_["live"] is None else r["live"] - all_["live"]
        dm = None if r["malloc"] is None or all_["malloc"] is None else r["malloc"] - all_["malloc"]
        c = r["cap"]
        cs = "—" if not c else f"{f(c['clean'], '{}m')} / {f(c['survive'], '{}m')} / {f(c['crash'], '{}m')} ({c['probed']})"
        print(f"| {tag} | {what} | {r['errs']} | {f(r['live'])} | {f(dl, '{:+.1f}')} | {f(r['malloc'])} | {f(dm, '{:+.1f}')} | {r['asm']} ({f(r['dll'], '{:.2f}')}) | "
              f"{f(r['inflated'], '{:,}')} | {f(r['interp'], '{:,}')} ({f(r['ir'], '{:.2f}')}) | {cs} |")
    return 0

if __name__ == "__main__":
    sys.exit(main())
