#!/usr/bin/env python3
"""prof_rank.py -- ranks the game's systems from Shepherd's hot-path profile (kit #7, track measure: engine Diag/K7Prof.cs writes
<logs>/prof.tsv with SHEPHERD_K7_PROF=time and/or sample; read-logs.sh copies it from the card, vlx / Mac runs write it locally).

For each scene of each file (the active scene at each frame's start; "[window ...]" rows = SHEPHERD_K7_PROF's win= frames):
  - the frame: ms per frame (the boot loop's wall time / frames), the GC's pause per frame, the samples taken
  - THE NAMED SUSPECTS (owner question 2026-10-16, 'what else is CPU-bound?'), ms per frame and % of the frame:
      coroutine scheduler   the engine's coroutine passes' own time (Lifecycle Coroutines.Resume: the scheduler, not the bodies)
      coroutine bodies      every coroutine step's own time (the game's code inside its coroutines)
      DOTween               DG.Tweening.* messages and coroutines (DOTweenComponent's per-frame tween update)
      Rewired               Rewired.* messages and coroutines (the per-frame input processing)
      FixedUpdate scripts   every FixedUpdate message + the fixed step's own loop + Triggers2D.Step
      A* path requests      Pathfinding.* / AstarPath messages and coroutines (the main thread's part; the path threads run elsewhere)
      SkeletonUtilityBones  the Spine skeletons that carry a SkeletonUtility ("[+SkeletonUtility]" keys: their bones' DoUpdate runs inside
                            their update events) + any SkeletonUtilityBone / SkeletonUtility message; the population from the census rows
  - the top systems: the behaviours grouped by type (all their messages and coroutines), and the engine's own zones, by self time
Basis: self ms (SHEPHERD_K7_PROF=time) or samples (=sample: ~1 per ms of the main thread; GC samples are their own row).
--device-frame-ms N: a device ESTIMATE column = each value x (N / this scene's measured ms per frame) -- the method of the kits'
  estimates (a device frame time x the measured share); label it ESTIMATE, never DEVICE.
usage: prof_rank.py PROF.TSV... [--scene TEXT] [--top N] [--device-frame-ms N] [--basis self|samples]"""
import argparse, collections, sys

SUSPECTS = ("coroutine scheduler", "coroutine bodies", "DOTween", "Rewired", "FixedUpdate scripts", "A* path requests", "SkeletonUtilityBones")
CORO_ZONES = ("coroutines (next frame)", "coroutines (end of frame)", "coroutines (WaitForFixedUpdate)")

def load(path):
    """the scene blocks in file order: each starts with its 'scene' row (a scene visited twice -- BufferScene -- is two blocks)"""
    scenes = collections.OrderedDict(); cur = None
    with open(path, encoding="utf-8") as f:
        for l in f:
            if l.startswith("#") or l.startswith("scene\t"): continue
            p = l.rstrip("\n").split("\t")
            if len(p) < 9: continue
            sc, frames, kind, name = p[0], int(p[1]), p[2], p[3]
            calls, incl, self_, mx, smp = int(p[4]), float(p[5]), float(p[6]), float(p[7]), int(p[8])
            if kind == "scene" or cur is None or cur[0] != sc:
                cur = (sc, name if kind == "scene" else "")
                scenes[cur] = {"frames": frames, "rows": [], "counts": {}, "wall": 0.0, "gc": 0.0, "samples": 0, "maxf": 0.0}
            s = scenes[cur]
            if kind == "scene": s.update(frames=frames, wall=incl, gc=self_, maxf=mx, samples=smp, range=name)
            elif kind == "count": s["counts"][name] = (calls, incl)
            else: s["rows"].append((kind, name, calls, incl, self_, mx, smp))
    return scenes

def owner(name):
    """the behaviour type of a message / coroutine key ('Ns.Type.Update', 'Ns.Type.<M>d__3 [coroutine]', '... [+SkeletonUtility]')"""
    n = name.replace(" [+SkeletonUtility]", "").replace(" [coroutine]", "")
    return n.rsplit(".", 1)[0] if "." in n else n

def suspect(kind, name):
    if kind == "zone":
        if name in CORO_ZONES: return "coroutine scheduler"
        if name in ("FixedUpdate loop", "fixed step", "Triggers2D.Step"): return "FixedUpdate scripts"
        return None
    if "[+SkeletonUtility]" in name or name.startswith("Spine.Unity.SkeletonUtility"): return "SkeletonUtilityBones"
    if name.startswith("DG.Tweening."): return "DOTween"
    if name.startswith("Rewired."): return "Rewired"
    if name.startswith("Pathfinding.") or name.startswith("AstarPath"): return "A* path requests"
    if kind == "type" and name.endswith(".FixedUpdate"): return "FixedUpdate scripts"
    if kind == "coro": return "coroutine bodies"
    return None

def main(argv):
    ap = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    ap.add_argument("files", nargs="+"); ap.add_argument("--scene"); ap.add_argument("--top", type=int, default=15)
    ap.add_argument("--device-frame-ms", type=float); ap.add_argument("--basis", choices=("self", "samples"))
    a = ap.parse_args(argv)
    for path in a.files:
        for (sc, _rng), s in load(path).items():
            if a.scene and a.scene not in sc: continue
            fr = max(1, s["frames"]); fms = s["wall"] / fr
            timed = any(r[4] > 0 for r in s["rows"])
            basis = a.basis or ("self" if timed else "samples")
            # samples -> ms: the sampler ticks at ~1 kHz nominal (the device; vlx and the Mac tick slower), so each sample is this scene's
            # wall time / its samples (the frame zone covers the whole boot loop: every tick lands on some id)
            mps = (s["wall"] / s["samples"]) if s["samples"] else 1.0
            val = (lambda r: r[4]) if basis == "self" else (lambda r: float(r[6]) * mps)
            per = lambda v: v / fr
            scale = (a.device_frame_ms / fms) if a.device_frame_ms and fms > 0 else None
            def cell(v):
                pf = per(v)
                out = f"{pf:8.3f} ms  {100 * pf / fms if fms else 0:5.1f} %"
                if scale: out += f"  ~{pf * scale:7.2f} ms ESTIMATE"
                return out
            print(f"== {path} :: {sc} ({s.get('range', '')}, {s['frames']} frames): {fms:.2f} ms/frame (longest {s['maxf']:.1f} ms), "
                  f"GC pause {s['gc'] / fr:.2f} ms/frame, {s['samples']} samples; basis {basis}"
                  + (f"; device estimate scale x{scale:.2f} (device {a.device_frame_ms} ms/frame)" if scale else ""))
            if basis == "samples" and s["samples"]:
                gc = next((r[6] for r in s["rows"] if r[0] == "sample" and r[1].startswith("GC")), 0)
                print(f"   GC (world stopped) samples: {gc} ({100.0 * gc / s['samples']:.1f} % of the ticks; {mps:.2f} ms per sample here)")
            sus = collections.Counter(); calls = collections.Counter()
            for r in s["rows"]:
                k = suspect(r[0], r[1])
                if k: sus[k] += val(r); calls[k] += r[2]
            print("   the named suspects:")
            for k in SUSPECTS:
                print(f"     {k:22s} {cell(sus[k])}   ({calls[k]} calls)")
            cnt = s["counts"]
            pop = [f"{n.split(' (')[0]} {v[0]}" for n, v in cnt.items() if any(w in n for w in ("SkeletonUtility", "Pathfinding", "DG.Tweening", "Rewired", "coroutines running", "active behaviours"))]
            if pop: print("     populations at the scene's end: " + ", ".join(pop))
            grp = collections.Counter(); gcalls = collections.Counter()
            for r in s["rows"]:
                if r[0] in ("type", "coro"): grp[owner(r[1])] += val(r); gcalls[owner(r[1])] += r[2]
            print(f"   the top {a.top} behaviour types (all their messages + coroutines, self):")
            for n, v in grp.most_common(a.top): print(f"     {n[:70]:70s} {cell(v)}   ({gcalls[n]} calls)")
            zs = sorted((r for r in s["rows"] if r[0] == "zone"), key=lambda r: -val(r))[:a.top]
            print(f"   the engine's zones (self):")
            for r in zs: print(f"     {r[1][:70]:70s} {cell(val(r))}   ({r[2]} calls, max {r[5]:.2f} ms)")
            st = [r for r in s["rows"] if r[0] == "stall"]
            if st:
                print("   stall share (SHEPHERD_K7_STALL: 1 - warm / cold, the medians of the frames' times; cold = the caches evicted right before"
                      " the loop (every other frame); a large share = memory-bound: keep on one core with prefetch; ~0 = compute-bound: a"
                      " candidate for another core):")
                for r in st:
                    cold, warm = r[3], r[4]
                    print(f"     {r[1][:50]:50s} cold {cold:9.3f} ms  warm {warm:9.3f} ms  share {1 - warm / cold if cold > 0 else 0:5.2f}  ({r[2]} frames)")
            print()
    return 0

if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
