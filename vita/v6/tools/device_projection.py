#!/usr/bin/env python3
"""device_projection.py <interp-prof run> <real interp ms/frame> <real JIT ms/frame> [--aot-engine] [--rule 43] [--frames 100]

V6 stage 2 (vita/notes/engine-pass-speed.md): the PS Vita menu-frame projection by the "~43x rule for interpreter vs AOT portions".
- The interpreted Mac frame is split by image from the call-timing profile (sim.sh prof: exclusive ms per image over the window).
  The profile's own cost (two callbacks per call) is removed per call: (instrumented - real) / calls, subtracted from each image
  in proportion to its call count.
- Images the Vita build AOT-compiles (vita/shepherd/build.sh's AOT list; --aot-engine adds Shepherd.Engine) run at Mac JIT speed:
  their interpreted time is divided by k = real interp / real JIT (both measured on the same boot, uninstrumented).
- device ms/frame = rule x (interpreted part + AOT part / k). The A9 is ~43x the M4 per core (CoreMark; up to ~65x memory-bound).
Arithmetic on Mac measurements, not a device result: the device run replaces it."""
import re, sys, os

AOT = {"System.Private.CoreLib", "System.Linq", "System.Collections", "System.Collections.Concurrent", "System.Console", "System.Memory",
       "System.ObjectModel", "System.Runtime.InteropServices", "System.Threading", "Shepherd.Assets", "Shepherd.Hooks",
       "Microsoft.Bcl.AsyncInterfaces"}

def main(a):
    run, real, jit = a[0], float(a[1]), float(a[2])
    aot_engine = "--aot-engine" in a
    rule = float(a[a.index("--rule") + 1]) if "--rule" in a else 43.0
    frames = int(a[a.index("--frames") + 1]) if "--frames" in a else 100
    logs = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", "..", "runtime", "logs", "v6")
    p = run if os.path.isfile(run) else os.path.join(logs, run, "interp-prof.tsv")
    head = open(p).readline()
    win = float(re.search(r"window wall ([\d.]+) ms", head).group(1)) / frames
    imgs = []
    for l in open(p):
        if l.startswith("image\t"):
            _, ms, calls, name = l.rstrip("\n").split("\t"); imgs.append((name, float(ms) / frames, int(calls) / frames))
    calls = sum(c for _, _, c in imgs)
    per_call = (win - real) / calls if calls else 0.0
    aot = AOT | ({"Shepherd.Engine"} if aot_engine else set())
    k = real / jit
    rows, interp_part, aot_part = [], 0.0, 0.0
    for name, ms, c in imgs:
        t = max(0.0, ms - c * per_call)
        if name in aot: aot_part += t
        else: interp_part += t
        rows.append((t, name, c))
    rows.sort(reverse=True)
    print(f"{run}: instrumented {win:.2f} ms/frame, real {real:.2f}, JIT {jit:.2f} (k = {k:.2f}); profile cost {per_call * 1e6:.1f} ns/call over {calls:,.0f} calls/frame")
    for t, name, c in rows[:10]:
        print(f"   {t:7.3f} ms  {c:9,.0f} calls  {'AOT ' if name in aot else 'interp'}  {name}")
    dev = rule * (interp_part + aot_part / k)
    print(f"   interpreted on the Vita: {interp_part:.2f} ms Mac; AOT on the Vita: {aot_part:.2f} ms Mac interp = {aot_part / k:.2f} ms at JIT speed")
    print(f"   device ~ {rule:.0f} x ({interp_part:.2f} + {aot_part / k:.2f}) = {dev:.0f} ms/frame ({1000 / dev:.1f} fps){' [Shepherd.Engine AOT]' if aot_engine else ''}")

if __name__ == "__main__":
    if len(sys.argv) < 4: print(__doc__); sys.exit(2)
    main(sys.argv[1:])
