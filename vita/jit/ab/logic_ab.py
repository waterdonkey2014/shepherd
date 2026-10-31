#!/usr/bin/env python3
"""logic_ab.py -- SHEPHERD-VITA kit #9 (track jit, vita/notes/kit9-jit.md section "the device A/B"): the JIT A/B that isolates GAME LOGIC.

Kit #8's device A/B compared the idle Main Menu's "[sim] logic part" (= frame minus render): JIT on 23.81 vs off 23.50 ms while the render
part moved 3 ms between starts of the same app -- most likely run variance (the first JIT-on start's render part equals the JIT-off start's). This tool reads the hot-path profiler's window rows
(SHEPHERD_K7_PROF=time,zones,win=<from>:<to>: the engine's zones timed with ~60 timestamps a frame, no per-behaviour keys) of several
starts per arm and splits each frame into
  GAME LOGIC   the zones where the game's own (interpreted) code runs: the Update / LateUpdate / FixedUpdate loops, Start calls, the
               coroutine passes, Invoke calls, the SynchronizationContext, async operations -- what the JIT can change
  ANIMATOR     the Animator update zone: the engine's (AOT) animator evaluation AND the game's StateMachineBehaviour / animation-event
               code inside it -- shown on its own (the JIT can move only its game part)
  LAYOUT       the renderer's "render: layout + canvases" zone, shown on its own (second review): the engine's layout passes AND
               Canvas.SendPreWillRenderCanvases / SendWillRenderCanvases -- uGUI's CanvasUpdateRegistry and TextMeshPro's rebuilds, the
               game's own UnityEngine.UI / TMP assemblies, interpreted in 00097 (vita/notes/engine-pass-speed.md: "Layout itself ... is game
               code"). The JIT can move it, so it is NOT part of the control
  RENDER       the renderer's other zones (SceneRenderer.Frame's own time, world pass, post + Stylizer, UI canvases, end / handoff, the wait
               for the render thread) -- engine AOT code, the CONTROL: it must not move with the JIT; when it does, the run differs in
               something else (clock, heat, card cache) and the start is suspect
  REST         everything else of the frame (input, physics' fixed step outside the scripts, diagnostics, present, GC)
(The render thread's own zone, "render thread: frame job" (MT apps), is another thread's time: left out of the main thread's split.)
and prints per arm the median and range over its starts, then each arm against the first ("A", the JIT off).
usage: logic_ab.py A=<prof.tsv>[,<prof.tsv>...] B=<...> [C=<...>] [--scene TEXT]
       (each file = one start's ux0:data/shepherd/runtime/logs/play/prof.tsv as read-logs.sh copies it; the arm's label is free text)"""
import sys, statistics, collections

LOGIC = ("Update loop", "LateUpdate loop", "FixedUpdate loop", "Start calls", "coroutines (next frame)", "coroutines (end of frame)",
         "coroutines (WaitForFixedUpdate)", "Invoke calls", "SynchronizationContext", "async operations")
RENDER_PREFIX = "render"
LAYOUT = "render: layout + canvases"            # (second review) mixed engine + game (uGUI / TMP) code: its own group, not the control
OTHER_THREAD_PREFIX = "render thread"           # (the render thread's zone in MT apps: not main-thread frame time)
BEHAVIOUR_KINDS = ("type", "coro", "key", "msg")   # K7Prof's per-behaviour rows ("coro" = a coroutine's type; second review: was "coroutine")

def window(path, scene_filter):
    """the '[window ...]' block's rows (kind, name, self ms) and its frame count; else the longest scene block"""
    blocks = collections.OrderedDict(); cur = None
    for l in open(path, encoding="utf-8", errors="replace"):
        if l.startswith("#") or l.startswith("scene\t"): continue
        p = l.rstrip("\n").split("\t")
        if len(p) < 9: continue
        sc, frames, kind, name = p[0], int(p[1]), p[2], p[3]
        incl, self_ = float(p[5]), float(p[6])
        if kind == "scene" or cur is None or cur[0] != sc:
            cur = (sc, name if kind == "scene" else ""); blocks[cur] = {"frames": frames, "rows": [], "wall": 0.0}
        b = blocks[cur]
        if kind == "scene": b.update(frames=frames, wall=incl)
        else: b["rows"].append((kind, name, self_))
    pick = [k for k in blocks if "[window" in k[0] and (not scene_filter or scene_filter in k[0])]
    if not pick:
        pick = sorted((k for k in blocks if not scene_filter or scene_filter in k[0]), key=lambda k: -blocks[k]["frames"])[:1]
    if not pick: return None
    return pick[0][0], blocks[pick[0]]

def split(b):
    f = max(b["frames"], 1); logic = render = anim = layout = 0.0
    for kind, name, self_ in b["rows"]:
        if kind not in ("zone", "z"):
            if kind in BEHAVIOUR_KINDS: logic += self_   # (time mode with keys: behaviour rows are logic)
            continue
        if name in LOGIC: logic += self_
        elif name == "Animator update": anim += self_
        elif name == LAYOUT: layout += self_
        elif name.startswith(OTHER_THREAD_PREFIX): pass
        elif name.startswith(RENDER_PREFIX): render += self_
    wall = b["wall"]
    return logic / f, render / f, (wall - logic - render - anim - layout) / f, wall / f, anim / f, layout / f

def main():
    arms = collections.OrderedDict(); scene = None; args = sys.argv[1:]
    if "--scene" in args:
        i = args.index("--scene"); scene = args[i + 1]; del args[i:i + 2]
    for a in args:
        if "=" not in a: sys.exit(__doc__)
        lab, files = a.split("=", 1); arms[lab] = [f for f in files.split(",") if f]
    if not arms: sys.exit(__doc__)
    res = collections.OrderedDict()
    for lab, files in arms.items():
        vals = []
        for f in files:
            w = window(f, scene)
            if not w: print(f"{lab}: {f}: no window / scene rows"); continue
            name, b = w; lg, rd, rest, frame, an, ly = split(b)
            vals.append((lg, rd, rest, frame, an, ly)); print(f"{lab}: {f}: {name} ({b['frames']} frames): frame {frame:.2f} ms = logic {lg:.2f} + animator {an:.2f} + layout {ly:.2f} + render {rd:.2f} + rest {rest:.2f}")
        res[lab] = vals
    print("\narm      starts  GAME LOGIC ms/frame (median [range])   ANIMATOR                  LAYOUT (engine + uGUI/TMP)  RENDER (control)          REST                    FRAME")
    def m(v): return f"{statistics.median(v):6.2f} [{min(v):.2f}-{max(v):.2f}]" if v else "     -"
    for lab, vals in res.items():
        print(f"{lab:8} {len(vals):6}  {m([v[0] for v in vals]):34} {m([v[4] for v in vals]):25} {m([v[5] for v in vals]):27} {m([v[1] for v in vals]):25} {m([v[2] for v in vals]):23} {m([v[3] for v in vals])}")
    labs = list(res)
    if len(labs) > 1 and res[labs[0]]:
        base = statistics.median(v[0] for v in res[labs[0]]); rbase = statistics.median(v[1] for v in res[labs[0]])
        abase = statistics.median(v[4] for v in res[labs[0]]); lbase = statistics.median(v[5] for v in res[labs[0]])
        for lab in labs[1:]:
            if not res[lab]: continue
            lg = statistics.median(v[0] for v in res[lab]); rd = statistics.median(v[1] for v in res[lab])
            an = statistics.median(v[4] for v in res[lab]); ly = statistics.median(v[5] for v in res[lab])
            print(f"{lab} vs {labs[0]}: game logic {lg - base:+.2f} ms/frame ({(lg / base - 1) * 100 if base else 0:+.1f} %); animator {an - abase:+.2f}; layout {ly - lbase:+.2f}; render (control) {rd - rbase:+.2f} ms"
                  + ("  <- the control moved > 0.5 ms: suspect run variance, add starts" if abs(rd - rbase) > 0.5 else ""))

if __name__ == "__main__":
    main()
