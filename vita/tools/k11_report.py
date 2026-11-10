#!/usr/bin/env python3
"""Kit #11 measurement report (vita/notes/kit11-measure.md): per-route Markdown tables for the integration note.

  k11_report.py mac <runs dir | run dir> ...     the Mac measurement runs (vita/tools/k11_measure.sh): one sub-folder per route with the
                                                 engine's k11/ files -> heat map, counters, exceptions, census and green-check tables
  k11_report.py vlx <vlx out dir> <glob> ...     vlx / device-log runs (one folder per run + <run>.verdict): RESULT, the END line (frames,
                                                 OOM, heap-cap refusals, menu responses), the GL record hash ("GL record": the first field of
                                                 EVERY glrec line, `cut -d'|' -f1 glrec.txt | shasum` -- the column the kit records compare)
                                                 and the engine stream hash ("stream record": frame + hash columns, `cut -f1,5`), arena /
                                                 newlib peaks, the K7 BOOT total, the SimStats window's frame times, the eboot span -> one row
                                                 per run; also "GL lines 2..N" (an A/B aid, never a record) and, with --gl-range A:B, the
                                                 lines A..B (`sed -n A,Bp`: the loading route's record is lines 2..2947)
  k11_report.py device <device log folder>       a device send-back folder (the apps' host logs + runtime/logs/<step>/): END,
                                                 OOM, arena / newlib peaks, boot total, the measured clock per host log; the SimStats
                                                 window and the boot line per engine log folder
  k11_report.py heat-diff <runs A> <runs B>      two Mac measurement sets side by side (e.g. a cache on / off): the heat map's unchanged
                                                 share and proof violations per route and scene, and (pixel mode) the two runs' images
                                                 frame by frame -- frames compared, frames that differ, the first one: a reuse cache's
                                                 "0 differing pixels" gate

Options: --json <file> also writes every table's rows as JSON; --steady N: the heat table's "steady" columns over each route's last N
compared frames (default 300); --gl-range A:B (vlx): one more GL column over glrec lines A..B. Reads results only (logs and the measurement files); writes nothing but stdout and --json."""
import sys, os, re, glob, json, hashlib, statistics, argparse, collections

def rd_tsv(p):
    if not os.path.exists(p): return []
    rows, head = [], None
    for l in open(p, errors="replace"):
        l = l.rstrip("\n")
        if not l or l.startswith("#"): continue
        f = l.split("\t")
        if head is None: head = f; continue
        rows.append(dict(zip(head, f)))
    return rows

def num(x, d=0.0):
    try: return float(x)
    except (TypeError, ValueError): return d

def md(title, head, rows):
    cell = lambda c: str(c).replace("|", "/")
    out = [f"\n### {title}\n", "| " + " | ".join(cell(h) for h in head) + " |", "|" + "|".join("---" for _ in head) + "|"]
    for r in rows: out.append("| " + " | ".join(cell(c) for c in r) + " |")
    if not rows: out.append("| " + " | ".join(["(none)"] + [""] * (len(head) - 1)) + " |")
    return "\n".join(out)

def route_dirs(paths):
    out = []
    for p in paths:
        if os.path.isdir(os.path.join(p, "k11")): out.append(p)
        else: out += sorted(d for d in glob.glob(os.path.join(p, "*")) if os.path.isdir(os.path.join(d, "k11")))
    return out

# ------------------------------------------------------------------------------------------------ mac
def mac(paths, steady, js):
    dirs = route_dirs(paths)
    if not dirs: print("no run folders with a k11/ sub-folder"); return
    T = collections.OrderedDict()
    # heat per route x scene
    rows = []
    for d in dirs:
        r = os.path.basename(d.rstrip("/"))
        for s in rd_tsv(os.path.join(d, "k11", "k11-heat-scenes.tsv")):
            rows.append([r, s["scene"], s["frames"], s["unchanged_stream_pct"], s["unchanged_pixel_pct"], s["violation_tiles"], s["miss_tiles"],
                         s["frames_all_unchanged_stream"], s["global_prefix_pct"], s["tile_prefix_pct"], " ".join(s["changers(cat:tiles)"].split()[:3])])
    T["heat"] = (["route", "scene", "frames", "unchanged % (stream)", "unchanged % (pixel)", "proof violations", "misses", "frames all unchanged",
                  "global prefix %", "tile prefix %", "top changers (cat:tiles)"], rows)
    # heat steady: the last N compared frames of each route
    rows = []
    for d in dirs:
        r = os.path.basename(d.rstrip("/"))
        fr = rd_tsv(os.path.join(d, "k11", "k11-heat.tsv"))
        if not fr: continue
        tail = fr[-steady:]; n = int(num(tail[0]["tiles"], 1))
        cs = [num(x["changed_stream"]) for x in tail]; cp = [num(x["changed_pixel"]) for x in tail if num(x["changed_pixel"], -1) >= 0]
        sc = collections.Counter(x["scene"] for x in tail).most_common(1)[0][0]
        rows.append([r, sc, len(tail), f"{100 - 100 * statistics.mean(cs) / n:.1f}", f"{statistics.median(cs):.0f} / {max(cs):.0f}",
                     f"{100 - 100 * statistics.mean(cp) / n:.1f}" if cp else "-", f"{statistics.median(cp):.0f} / {max(cp):.0f}" if cp else "-",
                     sum(1 for x in tail if num(x["changed_stream"]) == 0)])
    T["heat-steady"] = (["route", "scene (most frames)", "frames", "unchanged % (stream)", "changed tiles med / max (stream)", "unchanged % (pixel)",
                         "changed tiles med / max (pixel)", "frames all unchanged"], rows)
    # counters per route x scene
    rows = []
    for d in dirs:
        r = os.path.basename(d.rstrip("/"))
        by = collections.OrderedDict()
        for x in rd_tsv(os.path.join(d, "k11", "k11-counters.tsv")): by.setdefault(x["scene"], []).append(x)
        for sc, xs in by.items():
            rng = [num(x["rng_draws"]) for x in xs]; br = [num(x["brains"]) for x in xs]; fl = [num(x["flagged_at_frame_end"]) for x in xs]
            tc = [num(x["task_changes"]) for x in xs]; th = [num(x["exc_thrown_main"]) for x in xs]; ca = [num(x["exc_caught"]) for x in xs]
            on = lambda v: v and v[0] >= 0
            rows.append([r, sc, len(xs),
                         f"{sum(rng):.0f} ({statistics.mean(rng):.2f}/f, max {max(rng):.0f}, {sum(1 for v in rng if v > 0)} f)" if on(rng) else "-",
                         f"{max(br):.0f}" if on(br) else "-", f"{sum(1 for v in fl if v > 0)} f (max {max(fl):.0f})" if on(fl) else "-",
                         f"{sum(tc):.0f} in {sum(1 for v in tc if v > 0)} f" if on(tc) else "-",
                         f"{sum(th):.0f} in {sum(1 for v in th if v > 0)} f" if on(th) else "-", f"{sum(ca):.0f}" if on(ca) else "-"])
    T["counters"] = (["route", "scene", "frames", "random draws (per frame, max, frames)", "followers max", "flagged at frame end",
                      "task changes", "exceptions thrown (main)", "caught"], rows)
    # exceptions per route
    rows = []
    for d in dirs:
        r = os.path.basename(d.rstrip("/"))
        for x in rd_tsv(os.path.join(d, "k11", "k11-exceptions.tsv"))[:8]:
            cat = x.get("catchers(method:count)", "")
            short = "; ".join(re.sub(r"^.*?\] ", "", c).split("(")[0] + ":" + c.rsplit(":", 1)[-1] for c in cat.split(" | ") if c)[:160]
            rows.append([r, x["thrower"], x["type"], x["thrown"], x["caught"], f"{x['first_frame']}-{x['last_frame']}", short or "-"])
    T["exceptions"] = (["route", "thrower", "type", "thrown", "caught", "frames", "caught by (method:count)"], rows)
    # census per route x scene
    rows = []
    for d in dirs:
        r = os.path.basename(d.rstrip("/"))
        for s in rd_tsv(os.path.join(d, "k11", "k11-census-sum.tsv")):
            rows.append([r, s["scene"], s["frames"], s["draws"], s["noop"], s["hidden"], s.get("overwritten", "-"), s.get("blended_over", s.get("overdrawn", "-")), s["visible"], s["visible_lt16px"],
                         s["offscreen_target_draws"], " ".join(s["top_noop(why|cat:tex:n)"].split()[:3]) or "-", " ".join(s["top_killers(cat:tex:draws_hidden)"].split()[:2]) or "-"])
    T["census"] = (["route", "scene", "frames", "draws", "noop", "hidden (footprint covered: a skip rule)", "overwritten (this frame only)", "blended over", "visible", "visible < 16 px", "off-screen target draws",
                    "top noop (why|cat:texture:n)", "top killers"], rows)
    # green check per route
    rows = []
    for d in dirs:
        r = os.path.basename(d.rstrip("/"))
        g = rd_tsv(os.path.join(d, "k11", "k11-green.tsv"))
        log = open(os.path.join(d, "engine.log"), errors="replace").read() if os.path.exists(os.path.join(d, "engine.log")) else ""
        st = re.search(r"\[k11\] green self-test: (\w+)[^\n]*", log)
        if g or st or "[k11] green check" in log:
            rows.append([r, len(g), sum(int(num(x["regions"])) for x in g), sum(int(num(x["region_px"])) for x in g), sum(int(num(x["green_px"])) for x in g),
                         sum(1 for x in g if num(x["green_px"]) > 0), st.group(1) if st else "-"])
    T["green"] = (["route", "frames with regions", "regions", "px checked", "green px left", "frames with holes", "self-test"], rows)
    for k, (h, rs) in T.items(): print(md(k, h, rs))
    if js: json.dump({k: [dict(zip(h, r)) for r in rs] for k, (h, rs) in T.items()}, open(js, "w"), indent=1)

# ------------------------------------------------------------------------------------------------ vlx
def lines_hash(p, first=1, last=None, field_sep="|"):
    """sha1 of the first field of lines first..last (1-based, inclusive; last None = to the end) = `cut -d'|' -f1 | sed -n A,Bp | shasum`."""
    if not os.path.exists(p): return "none", 0
    ls = [l.rstrip("\n").split(field_sep)[0] for l in open(p, errors="replace")]
    ls = ls[first - 1:last]
    return hashlib.sha1("".join(l + "\n" for l in ls).encode()).hexdigest()[:12], len(ls)

def stream_hash(p):
    """The kit records' convention: frame + hash columns of every line (cut -f1,5 stream-hash.txt | shasum)."""
    if not os.path.exists(p): return "none", 0
    ls = [l.rstrip("\n").split("\t") for l in open(p, errors="replace") if l.strip()]
    return hashlib.sha1("".join(f"{f[0]}\t{f[4] if len(f) > 4 else ''}\n" for f in ls).encode()).hexdigest()[:12], len(ls)

def vlx(root, globs, js, gl_range=None):
    rows = []
    for g in globs:
        for d in sorted(x for x in glob.glob(os.path.join(root, g)) if os.path.isdir(x)):
            V = open(d + ".verdict", errors="replace").read() if os.path.exists(d + ".verdict") else ""
            logs = [p for p in glob.glob(os.path.join(d, "*.txt")) if os.path.basename(p) not in ("glrec.txt", "stream-hash.txt", "phys-hash.txt", "icall-hits.txt", "device-profile.txt", "vita-settings.txt", "missing-icalls.txt", "assemblies.txt", "simstats.txt")]
            L = "".join(open(p, errors="replace").read() for p in logs)
            res = (re.search(r"^RESULT: (\w+)", V, re.M) or [None, "?"])[1]
            end = re.search(r"(PASS|FAIL) END frame (\d+)[^\n]*?(\d+) OOM, (\d+) heap-cap refusal\(s\)[^\n]*?(\d+) menu response\(s\)", L)
            ends = f"{end[1]} END {end[2]}" if end else ("no END" if not re.search(r"(PASS|FAIL) END frame \d+", L) else re.search(r"(PASS|FAIL) END frame \d+", L)[0])
            oom = f"{end[3]} / {end[4]}" if end else "-"; resp = end[5] if end else "-"
            gla, gan = lines_hash(os.path.join(d, "glrec.txt")); gl2, gn = lines_hash(os.path.join(d, "glrec.txt"), 2)
            glr = lines_hash(os.path.join(d, "glrec.txt"), gl_range[0], gl_range[1]) if gl_range else None
            st, sn = stream_hash(os.path.join(d, "stream-hash.txt"))
            m = re.search(r"MEM end[^\n]*?newlib used=\d+ KiB \(sbrk \d+, peak (\d+)\)[^\n]*?arena used=\d+ KiB peak=(\d+) of (\d+)", L)
            mem = f"{int(m[2]) / 1024:.1f} of {int(m[3]) / 1024:.0f} / {int(m[1]) / 1024:.1f}" if m else "-"
            b = re.search(r"K7 BOOT .*?TOTAL ([\d.]+) s", L); sp = re.search(r"eboot span: (\d+)", V)
            S = open(os.path.join(d, "simstats.txt"), errors="replace").read() if os.path.exists(os.path.join(d, "simstats.txt")) else ""
            win = re.search(r"window frames (\d+)\.\.(\d+).*?\n\s+frame wall\s+mean ([\d.]+) p50 ([\d.]+) p95 ([\d.]+)", S, re.S)
            rows.append([os.path.basename(d), res, ends, oom, resp, f"{gla} ({gan})", f"{st} ({sn})"] + ([f"{glr[0]} ({glr[1]})"] if gl_range else []) +
                        [mem, b.group(1) if b else "-", f"{win[3]} / {win[4]} / {win[5]} (f{win[1]}-{win[2]})" if win else "-",
                         f"{int(sp.group(1)) / 1048576:.4f}" if sp else "-", f"{gl2} ({gn})"])
    head = ["run", "RESULT", "END", "OOM / refusals", "menu responses", "GL record (all lines)", "stream record (frames)"] + \
           ([f"GL lines {gl_range[0]}..{gl_range[1]}"] if gl_range else []) + \
           ["arena peak of arena / newlib peak MiB", "boot total s", "window frame ms mean / p50 / p95", "eboot span MiB", "GL lines 2..N (A/B aid, not a record)"]
    print(md("vlx runs", head, rows))
    if js: json.dump([dict(zip(head, r)) for r in rows], open(js, "w"), indent=1)

# ------------------------------------------------------------------------------------------------ device
def device(root, js):
    """A device log folder (the README's send-back: the apps' host logs at the top, the engine logs in runtime/logs/<step>/)."""
    rows = []
    for p in sorted(glob.glob(os.path.join(root, "*.txt"))):
        if p.endswith(".prev.txt"): continue
        L = open(p, errors="replace").read()
        end = re.search(r"(PASS|FAIL) END frame (\d+), ([\d.]+) s[^\n]*?(\d+) OOM, (\d+) heap-cap refusal\(s\)", L)
        if not end and "K7 BOOT" not in L: continue
        m = re.search(r"MEM end[^\n]*?newlib used=\d+ KiB \(sbrk \d+, peak (\d+)\)[^\n]*?arena used=\d+ KiB peak=(\d+) of (\d+)", L)
        b = re.search(r"K7 BOOT .*?TOTAL ([\d.]+) s", L); clk = re.search(r"CLK measured at start: ~(\d+) MHz", L)
        rows.append([os.path.basename(p), f"{end[1]} END {end[2]} ({end[3]} s)" if end else "no END", f"{end[4]} / {end[5]}" if end else "-",
                     f"{int(m[2]) / 1024:.1f} of {int(m[3]) / 1024:.0f} / {int(m[1]) / 1024:.1f}" if m else "-", b.group(1) if b else "-", clk.group(1) if clk else "-"])
    head = ["host log", "END", "OOM / refusals", "arena peak of arena / newlib peak MiB", "boot total s", "clock measured MHz"]
    print(md("device host logs", head, rows))
    rows2 = []
    for d in sorted(glob.glob(os.path.join(root, "runtime", "logs", "*")) + [os.path.join(root, "runtime", "logs")]):
        S = os.path.join(d, "simstats.txt")
        if not os.path.isdir(d) or not os.path.exists(S): continue
        t = open(S, errors="replace").read()
        win = re.search(r"window frames (\d+)\.\.(\d+).*?\n\s+frame wall\s+mean ([\d.]+) p50 ([\d.]+) p95 ([\d.]+)", t, re.S)
        E = open(os.path.join(d, "engine.log"), errors="replace").read() if os.path.exists(os.path.join(d, "engine.log")) else ""
        bt = re.search(r"\[k7\] BOOT .*?TOTAL ([\d.]+) s", E); last = E.strip().splitlines()[-1][-90:] if E.strip() else "-"
        rows2.append([os.path.relpath(d, root), f"{win[3]} / {win[4]} / {win[5]} (f{win[1]}-{win[2]})" if win else "-", bt.group(1) if bt else "-", last])
    head2 = ["engine log folder", "window frame ms mean / p50 / p95", "boot total s", "engine.log last line"]
    print(md("device engine logs", head2, rows2))
    if js: json.dump({"host": [dict(zip(head, r)) for r in rows], "engine": [dict(zip(head2, r)) for r in rows2]}, open(js, "w"), indent=1)

# ------------------------------------------------------------------------------------------------ heat-diff
def heat_diff(a, b, js):
    def load(p):
        out = {}
        for d in route_dirs([p]):
            seen = collections.Counter()
            for s in rd_tsv(os.path.join(d, "k11", "k11-heat-scenes.tsv")):
                seen[s["scene"]] += 1   # (a scene visited twice is two blocks)
                out[(os.path.basename(d.rstrip("/")), s["scene"] if seen[s["scene"]] == 1 else f"{s['scene']} #{seen[s['scene']]}")] = s
        return out
    A, B = load(a), load(b); rows = []
    for k in sorted(set(A) | set(B)):
        x, y = A.get(k, {}), B.get(k, {})
        rows.append([k[0], k[1], x.get("frames", "-"), y.get("frames", "-"), x.get("unchanged_stream_pct", "-"), y.get("unchanged_stream_pct", "-"),
                     x.get("unchanged_pixel_pct", "-"), y.get("unchanged_pixel_pct", "-"), x.get("violation_tiles", "-"), y.get("violation_tiles", "-")])
    head = ["route", "scene", "frames A", "frames B", "unchanged % stream A", "B", "unchanged % pixel A", "B", "violations A", "B"]
    print(md("heat map A / B", head, rows))
    # the images, frame by frame (pixel mode's image_hash column): a reuse cache's "0 differing pixels" gate
    def images(p):
        out = {}
        for d in route_dirs([p]):
            out[os.path.basename(d.rstrip("/"))] = {x["frame"]: x["image_hash"] for x in rd_tsv(os.path.join(d, "k11", "k11-heat.tsv"))
                                                     if x.get("image_hash", "-") not in ("-", "", None)}
        return out
    IA, IB = images(a), images(b); rows2 = []
    for r in sorted(set(IA) | set(IB)):
        x, y = IA.get(r, {}), IB.get(r, {}); both = sorted(set(x) & set(y), key=int); diff = [f for f in both if x[f] != y[f]]
        rows2.append([r, len(x), len(y), len(both), len(diff), diff[0] if diff else "-", "SAME" if both and not diff else "DIFFER" if diff else "not compared"])
    head2 = ["route", "image frames A", "B", "compared", "differ", "first differing frame", "verdict"]
    print(md("images A / B (pixel mode, frame by frame)", head2, rows2))
    if js: json.dump({"heat": [dict(zip(head, r)) for r in rows], "images": [dict(zip(head2, r)) for r in rows2]}, open(js, "w"), indent=1)

if __name__ == "__main__":
    ap = argparse.ArgumentParser(add_help=False); ap.add_argument("--json"); ap.add_argument("--steady", type=int, default=300); ap.add_argument("--gl-range"); ap.add_argument("-h", "--help", action="store_true")
    o, rest = ap.parse_known_args()
    if o.help or not rest: print(__doc__); sys.exit(0 if o.help else 2)
    mode, args = rest[0], rest[1:]
    if mode == "mac": mac(args, o.steady, o.json)
    elif mode == "vlx" and len(args) >= 2: vlx(args[0], args[1:], o.json, tuple(int(x) for x in o.gl_range.split(":")) if o.gl_range else None)
    elif mode == "heat-diff" and len(args) == 2: heat_diff(args[0], args[1], o.json)
    elif mode == "device" and len(args) == 1: device(args[0], o.json)
    else: print(__doc__); sys.exit(2)
