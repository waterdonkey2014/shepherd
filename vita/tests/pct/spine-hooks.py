#!/usr/bin/env python3
"""Kit #7 tests track (vita/notes/kit7-tests.md 4.2 + "Review fixes"): puts SHEPHERD_TEST_SPINE's hook lines into ANOTHER engine tree -- a
Mac-only copy such as the combat drive's read-only copy of k6-physics (vita/tests/pct/astar-combat.sh setup) or a workflow adopting the
switch before k7-tests is merged. k7-tests' own tree has them already (each wrapped in #if !SHEPHERD_EBOOT; a tree that builds eboots
must wrap them the same way -- or take k7-tests' engine edits). Diag/TestSpine.cs itself is copied by the caller.
  spine-hooks.py <engine dir (the one holding Shepherd.Engine/)>
The sites: the game's Debug.Log reaching TestSpine.OnGameLog (the bind waits for the game's own 'Dispatch Thread Created'), Awake / Start /
the phase messages (Call, CallD; a callback that threw too), every coroutine step (one that threw too), the synchronization context's
tasks, Invoke / InvokeRepeating, AsyncOperation completions, SendMessage / Broadcast, AnimationEvents, OnDidApplyAnimationProperties, 2D
trigger messages (main's Triggers2D, k6-physics' PhysDispatch), native messages, OnWillRenderObject, Canvas.willRenderCanvases,
OnRectTransformDimensionsChange, VideoPlayer events, the scene events. A site whose anchor is not in the tree is reported (that file differs
from the layouts this knows: main b3bc3ba, k6-physics d1f9608); the four critical ones (log signal, Call, CallD, coroutine step) missing
-> exit 1. A site already hooked is left alone. Prints one line per site."""
import os, re, sys

HOOK = "if (Diag.TestSpine.On) Diag.TestSpine.After();"
root = os.path.join(sys.argv[1], "Shepherd.Engine")
report, missing_critical = [], []

def load(rel):
    p = os.path.join(root, rel)
    return (p, open(p).read()) if os.path.exists(p) else (p, None)

def after_line(rel, pattern, name, critical=False, all_matches=False, hook=HOOK):
    """Insert the hook on its own line after the line(s) matching pattern (same indentation), unless the next line already holds it."""
    p, s = load(rel)
    if s is None: report.append(f"  {name}: {rel} not in this tree"); (missing_critical.append(name) if critical else None); return
    lines = s.split("\n"); out = []; n = 0; already = 0
    for i, l in enumerate(lines):
        out.append(l)
        if re.search(pattern, l) and (all_matches or n + already == 0):
            nxt = lines[i + 1] if i + 1 < len(lines) else ""
            if "Diag.TestSpine." in nxt: already += 1; continue
            ind = re.match(r"\s*", l).group(0)
            if l.rstrip().endswith("{"): ind += "    "
            out.append(ind + hook); n += 1
    if n: open(p, "w").write("\n".join(out))
    if n == 0 and already == 0: report.append(f"  {name}: anchor NOT FOUND in {rel}"); (missing_critical.append(name) if critical else None)
    else: report.append(f"  {name}: {rel} {n} hooked" + (f", {already} already" if already else ""))

def inline(rel, old, new, name, critical=False):
    """Replace one exact text (a one-line site: the hook goes inside it)."""
    p, s = load(rel)
    if s is None: report.append(f"  {name}: {rel} not in this tree"); (missing_critical.append(name) if critical else None); return
    if HOOK in new and new in s: report.append(f"  {name}: {rel} already"); return
    c = s.count(old)
    if c == 0: report.append(f"  {name}: anchor NOT FOUND in {rel}"); (missing_critical.append(name) if critical else None); return
    open(p, "w").write(s.replace(old, new)); report.append(f"  {name}: {rel} {c} hooked (inline)")

L = "Runtime/Lifecycle.cs"
after_line("Icalls/AppIcalls.cs", r"SceneStop\.OnGameLog\(msg\);", "log signal (bind after the game's own DispatchThread init)", True,
           hook="if (Diag.TestSpine.On) Diag.TestSpine.OnGameLog(msg);")
after_line(L, r"var r = m\.Invoke\(n\.Managed, null\);", "Call (Awake / Start / phase messages by Invoke)", True)
# the Call / CallD catch line: CallD's join (after its try / catch) and Call's exception path (after a throw; a second check after a
# normal return is a no-op)
after_line(L, r"catch \(Exception e\) \{ Log\.Exception\(e, \$\"\{what\} \{n\.Managed\?\.GetType\(\)\.FullName\} '\{n\.Name\}'\"\); \}\s*$", "CallD + Call (threw)", True, all_matches=True)
inline(L, '{s.Method}"); s.Done = true; s.Kind = Wait.Done; return; }', f'{{s.Method}}"); s.Done = true; s.Kind = Wait.Done; {HOOK} return; }}', "coroutine step (threw)")
after_line(L, r"catch \(Exception e\) \{ Log\.Exception\(e, \$\"coroutine \{s\.Owner", "coroutine step", True)
after_line(L, r"SyncExec\?\.Invoke\(null, null\); \} catch", "synchronization context tasks")
after_line(L, r"mi\?\.Invoke\(i\.Owner\.Managed, null\); \} catch", "Invoke / InvokeRepeating")
after_line(L, r"InvokeCompletionEvent\(o\.Managed\); \} catch", "AsyncOperation completions", all_matches=True)
inline("Icalls/Core.cs", "var r = mi.Invoke(m.Managed, mi.GetParameters().Length == 1 ? new[] { value } : null); if (r is IEnumerator e)",
       f"var r = mi.Invoke(m.Managed, mi.GetParameters().Length == 1 ? new[] {{ value }} : null); {HOOK} if (r is IEnumerator e)", "SendMessage / Broadcast")
after_line("Icalls/Core.cs", r'catch \(Exception e\) \{ Log\.Exception\(e, \$"SendMessage \{method\}"\); \}', "SendMessage (threw)")
after_line("Anim/AnimatorRuntime.cs", r'Log\.Exception\(ex, \$"AnimationEvent \{e\.fn\}"\)', "AnimationEvents")
inline("Anim/AnimatorRuntime.cs", 'catch (Exception e) { Log.Exception(e, "OnDidApplyAnimationProperties"); } };',
       f'catch (Exception e) {{ Log.Exception(e, "OnDidApplyAnimationProperties"); }} {HOOK} }};', "OnDidApplyAnimationProperties")
after_line("Runtime/Triggers2D.cs", r"r = h\.Mi\.Invoke\(m\.Managed, null\);", "2D trigger messages (Triggers2D)")
after_line("Runtime/Triggers2D.cs", r"catch \(TargetInvocationException e\) \{ _arg\[0\] = null; Log\.Exception", "2D trigger messages (threw)")
after_line("Physics/PhysDispatch.cs", r"else \{ var r = Mi\.Invoke\(m\.Managed, OneArg", "2D trigger / collision messages (PhysDispatch)")
after_line("Physics/PhysDispatch.cs", r"catch \(Exception e\) \{ HandlerExceptions\+\+;", "2D trigger / collision messages (threw)")
after_line("Runtime/NativeMessages.cs", r"Log\.Exception\(e, \$\"\{msg\} \{m\.Managed\.GetType\(\)\.Name\} '\{go\.Name\}'\"\)", "native messages")
after_line("Render/SpriteShapes.cs", r'\$"OnWillRenderObject \{t\.Name\}"', "OnWillRenderObject")
after_line("Render/SceneRenderer.cs", r'"Canvas\.SendWillRenderCanvases"', "Canvas.willRenderCanvases")
after_line("Render/SceneRenderer.cs", r'catch \(Exception e\) \{ Log\.Exception\(e, "OnRectTransformDimensionsChange"\); \}', "OnRectTransformDimensionsChange")
after_line("Icalls/VideoMiscIcalls.cs", r'"loopPointReached"\); \}', "VideoPlayer loopPointReached")
after_line("Icalls/VideoMiscIcalls.cs", r'"video started"\); \}', "VideoPlayer prepared / started")
inline("Icalls/VideoMiscIcalls.cs", "try { Prepared?.Invoke(null, new[] { a[0] }); } catch { } return null; }",
       f"try {{ Prepared?.Invoke(null, new[] {{ a[0] }}); }} catch {{ }} {HOOK} return null; }}", "VideoPlayer prepare")
inline("Icalls/VideoMiscIcalls.cs", "static void Fire(MethodInfo m, NObj v, string what) { try { m?.Invoke(null, new[] { v.Managed }); } catch (Exception e) { Log.Exception(e, what); } }",
       f"static void Fire(MethodInfo m, NObj v, string what) {{ try {{ m?.Invoke(null, new[] {{ v.Managed }}); }} catch (Exception e) {{ Log.Exception(e, what); }} {HOOK} }}", "VideoPlayer events (kit #6 video)")
inline("Icalls/BundleIcalls.cs", "static void Fire(MethodInfo m, params object[] args) { try { m?.Invoke(null, args); } catch (Exception e) { Log.Exception(e, m?.Name); } }",
       f"static void Fire(MethodInfo m, params object[] args) {{ try {{ m?.Invoke(null, args); }} catch (Exception e) {{ Log.Exception(e, m?.Name); }} {HOOK} }}", "scene events")
print("spine-hooks: " + root); print("\n".join(report))
if missing_critical: print("CRITICAL sites missing: " + ", ".join(missing_critical)); sys.exit(1)
