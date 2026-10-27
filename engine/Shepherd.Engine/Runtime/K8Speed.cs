using System.Reflection;
using Shepherd.Engine.Native;
using UnityEngine;
namespace Shepherd.Engine.Runtime;

/// <summary>Device kit #8, track speed (vita/notes/kit8-speed.md): the switches back to kit #7, read once at start from the environment
/// (on the Vita: the app's env file), and the track's small helpers. <c>SHEPHERD_K8_SPEED=0</c> turns every row of the track off (= kit #7's
/// code paths); each row also has its own switch. None of the default rows changes what the game does or what is drawn: they find the same
/// answers faster, skip calls that do nothing, or write the same log text in fewer card writes. (ProfileOnly: reads no property-bag keys,
/// so the V3 diet's literal scan skips it -- every new literal of the track lives here.)
/// <list type="bullet">
/// <item><c>SHEPHERD_K8_TAGS</c> (default on): GameObject.FindWithTag / FindGameObjectWithTag / FindGameObjectsWithTag answer from a tag index
/// (<see cref="TagIndex"/>) -- the same objects in the same order as the hierarchy walk; <c>=check</c> runs both and compares every answer
/// (the walk's is returned); <c>=0</c> = kit #7's walk.</item>
/// <item><c>SHEPHERD_K8_SKIP_EMPTY</c> (default on): an Update / LateUpdate / FixedUpdate whose IL is only 'ret' is never called (it does
/// nothing either way); <c>=0</c> = kit #7.</item>
/// <item><c>SHEPHERD_K8_LISTS</c> (default on): the deferred-destroy queue in one pass without LINQ (the same objects destroyed in the same
/// order); <c>=0</c> = kit #7.</item>
/// <item><c>SHEPHERD_K8_HEAP</c> (default on, the Vita host): VitaHost's 50-frame heartbeat reports the GC heap sgen measured at its last
/// collection (GC.GetGCMemoryInfo: no lock) instead of GC.GetTotalMemory (a walk of every major-heap block under the GC lock: ~8 ms of vlx
/// time per heartbeat); the host labels it "gc heap@gc"; checkpoints and the end keep GetTotalMemory; <c>=total</c> / <c>=0</c> = kit #7.</item>
/// <item><c>SHEPHERD_K8_MENU_WATCH</c> (default lazy, with SHEPHERD_MENU_WATCH=1): the menu watch reads the selected button only on the frame
/// of a pad press / touch (before the game sees it) and while a press waits for the menu's answer -- the same M1 lines; <c>=check</c> runs the
/// per-frame watch (its lines are logged) and the lazy one side by side and compares; <c>=every</c> / <c>=0</c> = kit #7's per-frame watch.</item>
/// <item><c>SHEPHERD_K8_LOG_DIET</c> (default: a card write per line = kit #7 -- the test kits' engine.log is the crash evidence; review fix):
/// <c>=batch</c> (opt-in) engine.log is written once per frame (and when ~4 KiB are buffered) instead of one card write per line -- the same
/// lines, but a hard crash loses the buffered ones (<see cref="LogDiet"/>); <c>=ring</c> (opt-in, the release behaviour): batch + the game's own
/// Debug.Log / LogWarning lines left out of engine.log (echoed only) and counted; unset / <c>=0</c> / <c>=line</c> = kit #7.</item>
/// <item><c>SHEPHERD_K8_VISIBILITY=1</c> (opt-in, default off): Renderer.isVisible and OnBecameVisible / OnBecameInvisible from the engine's
/// culling (<see cref="Visibility"/>); off = kit #7 (isVisible always true, no messages).</item>
/// </list></summary>
[ProfileOnly]
public static class K8Speed
{
    static string Env(string k) => Environment.GetEnvironmentVariable(k);
    public static readonly bool Track = Env("SHEPHERD_K8_SPEED") != "0";
    static string Mode(string k) => Track ? Env(k) ?? "" : "0";
    static readonly string TagsMode = Mode("SHEPHERD_K8_TAGS");
    public static readonly bool Tags = TagsMode != "0";
    public static readonly bool TagsCheck = TagsMode == "check";
    public static readonly bool SkipEmpty = Mode("SHEPHERD_K8_SKIP_EMPTY") != "0";
    public static readonly bool Lists = Mode("SHEPHERD_K8_LISTS") != "0";
    public static readonly bool ListsCheck = Mode("SHEPHERD_K8_LISTS") == "check";
    public static readonly bool CheapHeap = Mode("SHEPHERD_K8_HEAP") is not ("0" or "total");
    static readonly string WatchMode = Mode("SHEPHERD_K8_MENU_WATCH");
    /// <summary>The lazy watch makes the M1 lines (default); false = kit #7's per-frame watch (also in check mode, which runs both).</summary>
    public static readonly bool LazyWatch = WatchMode is not ("0" or "every" or "check");
    public static readonly bool WatchCheck = WatchMode == "check";
    static readonly string DietMode = Mode("SHEPHERD_K8_LOG_DIET");
    /// <summary>Review fix: batching is opt-in (<c>batch</c> / <c>ring</c>); the default keeps kit #7's flush per line, because a hard crash on the
    /// Vita (no handler runs) loses every buffered line and nothing else on the card holds them reliably (see <see cref="LogDiet"/>).</summary>
    public static readonly bool LogBatch = DietMode is "batch" or "ring";
    public static readonly bool LogRing = DietMode == "ring";
    public static readonly bool VisibilityOn = Track && Env("SHEPHERD_K8_VISIBILITY") == "1";
    /// <summary>Review fix: the eboot's build-time BCL AOT trim (vita/shepherd/build.sh SHEPHERD_BCL_AOT_TRIM; the Vita host sets
    /// SHEPHERD_BUILD_BCL_AOT_TRIM from the build, over any env-file value): the trimmed modules (comma-separated), "0" = kit #7's AOT set, null =
    /// no Vita host / an older host (the Mac: no AOT at all). No env switch can undo it (AOT code is not in the eboot), so the start line says so.</summary>
    static readonly string BclTrim = Env("SHEPHERD_BUILD_BCL_AOT_TRIM");
    static string TrimNote() =>
        BclTrim == null ? "" : BclTrim == "0" || BclTrim == "" ? "; build: BCL AOT trim off (kit #7's AOT set)" :
        // (integration review: the merged numbers -- physics, the JIT and boot use most of the trim's room; vita/notes/kit8-integration.md 5b)
        !Track ? "; NOT undone by SHEPHERD_K8_SPEED=0: this eboot's build-time BCL AOT trim (" + BclTrim + " interpreted: ~2.2-2.7 MiB less eboot, " +
                 "most of it used by kit #8's physics / JIT / boot code -- arena vs kit #7 at the defaults: 00099 +1, 00011 +2, 00098 / 00193 +1, " +
                 "00097 -2 with the JIT on (= with SHEPHERD_JIT=0), 00095 / 00194 -1 at physics level 6 (= with SHEPHERD_PHYS=0); kit8-integration.md 5b). " +
                 "Kit #7's arena: SHEPHERD_JIT=0 / SHEPHERD_PHYS=0 / SHEPHERD_PHYS_BLOCK_MB=0 first, never SHEPHERD_ARENA_MB alone (env_play.txt is shared " +
                 "by 00097 / 00098 / 00095 / 00193 / 00194); kit #7's AOT set: a SHEPHERD_BCL_AOT_TRIM=0 build" :
        "; build: BCL AOT trim (" + BclTrim + " interpreted; build-time: SHEPHERD_K8_SPEED=0 does not undo it)";

    /// <summary>The start line (Boot, once the log is open): which rows are on.</summary>
    public static string Describe() =>
        "[k8] speed track " + (!Track ? "OFF (SHEPHERD_K8_SPEED=0: kit #7's engine code paths)" :
        "tags " + (TagsCheck ? "index+check" : Tags ? "index" : "walk (kit #7)") +
        ", skip empty messages " + (SkipEmpty ? "on" : "off") + ", lists " + (Lists ? "on" : "off") +
        ", heartbeat heap " + (CheapHeap ? "at the last GC" : "GC.GetTotalMemory (kit #7)") +
        ", menu watch " + (WatchCheck ? "per frame + lazy check (the check needs SHEPHERD_K8_LATCH=0)" : LazyWatch ? "lazy (per frame while the late input latch is on)" : "per frame (kit #7)") +
        ", engine.log " + (LogRing ? "batched (opt-in), game Debug.Log/LogWarning echoed only" : LogBatch ? "batched per frame (opt-in)" : "a card write per line (kit #7)") +
        ", visibility " + (VisibilityOn ? "from culling (opt-in)" : "always visible (kit #7)")) + TrimNote();

    // ---------------- skip empty game messages ----------------
    public static int EmptyTypes, EmptyMessages;
    public static long SkippedCalls;
    /// <summary>Lifecycle.For(type): a phase message whose IL is only 'ret' (nop's allowed before it), no exception clauses, an instance method
    /// with IL (not abstract, extern or runtime-implemented), void, no parameters, not generic -- calling it does nothing, so it is dropped
    /// (the message table keeps null: no phase list membership, no call). Returns the bit mask of dropped messages (1 FixedUpdate, 2 Update,
    /// 4 LateUpdate).</summary>
    public static int DropEmpty(ref MethodInfo fixedUpdate, ref MethodInfo update, ref MethodInfo lateUpdate)
    {
        if (!SkipEmpty) return 0;
        int mask = 0;
        if (IsEmptyBody(fixedUpdate)) { fixedUpdate = null; mask |= 1; }
        if (IsEmptyBody(update)) { update = null; mask |= 2; }
        if (IsEmptyBody(lateUpdate)) { lateUpdate = null; mask |= 4; }
        if (mask != 0) { EmptyTypes++; EmptyMessages += (mask & 1) + (mask >> 1 & 1) + (mask >> 2 & 1); }
        return mask;
    }
    public static bool IsEmptyBody(MethodInfo mi)
    {
        if (mi == null || mi.IsStatic || mi.IsAbstract || mi.ReturnType != typeof(void) || mi.ContainsGenericParameters || mi.GetParameters().Length != 0) return false;
        if ((mi.MethodImplementationFlags & (MethodImplAttributes.InternalCall | MethodImplAttributes.Native | MethodImplAttributes.Runtime | MethodImplAttributes.Unmanaged)) != 0) return false;
        if ((mi.Attributes & MethodAttributes.PinvokeImpl) != 0) return false;
        MethodBody b;
        try { b = mi.GetMethodBody(); } catch (Exception) { return false; }
        if (b == null || b.ExceptionHandlingClauses.Count != 0) return false;
        var il = b.GetILAsByteArray();
        if (il == null || il.Length == 0 || il[il.Length - 1] != 0x2A) return false;   // ret
        for (int i = 0; i < il.Length - 1; i++) if (il[i] != 0x00) return false;          // nop
        return true;
    }

    // ---------------- the end line ----------------
    public static string Summary()
    {
        if (!Track) return null;
        FixedSteps.End();
        var s = "[k8] speed: " + TagIndex.Summary();
        if (SkipEmpty) s += "; empty messages skipped: " + EmptyMessages + " in " + EmptyTypes + " type(s), ~" + SkippedCalls + " calls not made";
        if (Lists) s += "; " + Lookups.Summary();
        if (LogBatch) s += "; engine.log " + LogDiet.Summary();
        if (WatchCheck)   // (integration review: from the watch as resolved -- with the late input latch on, the check never runs)
        {
            int lm = Diag.VitaHost.LazyMode;
            s += "; " + ((lm & 2) != 0 && lm >= 0 ? Diag.MenuWatchCheck.Summary() :
                "menu watch check: NOT RUN (" + (lm < 0 ? "no menu watch in this start" : "the late input latch is on: SHEPHERD_K8_LATCH=0 for the check") + ")");
        }
        if (VisibilityOn) s += "; " + Visibility.Summary();
        return s;
    }
}

/// <summary>Kit #8 speed track, the tag index (roadmap lever 2; the kit #7 profiler found IntroRoomMusicController.Update calling
/// GameObject.FindWithTag("Player") every frame, and the engine's walk -- Scenes.AllGameObjects(): every scene's whole hierarchy, a
/// children copy per node -- took 31 % of the intro frame on the Mac). Every GameObject record whose tag is not "Untagged" sits in its tag's
/// set (NGameObject.Tag's setter moves it; Objects.Kill takes it out). A query looks only at that tag's records: those active in hierarchy
/// whose chain up to a scene root is linked both ways (each transform in its parent's Children, the root in its scene's Roots) are exactly
/// the ones the walk visits (the walk goes down those same lists), and their walk order is the order of their paths: the scene's place in
/// Scenes.All's enumeration, the root's place in Roots, then each child index (pre-order: an ancestor before its descendants). So
/// FindWithTag returns the first of them and FindGameObjectsWithTag all of them in that order -- the walk's answers. The walk stays for
/// "Untagged" and a null tag (every untagged object), and while a hierarchy is being destroyed (Lifecycle.DestroyDepth: killed records are
/// still linked then). SHEPHERD_K8_TAGS=check runs the walk too, returns its answer and counts any difference.</summary>
[ProfileOnly]
public static class TagIndex
{
    const string Untagged = "Untagged";
    static readonly Dictionary<string, HashSet<NGameObject>> ByTag = new(StringComparer.Ordinal);
    static long _one, _many, _walks, _cands, _mismatch;

    /// <summary>NGameObject.Tag's setter (only when the value changes).</summary>
    public static void Moved(NGameObject g, string old, string now)
    {
        if (!K8Speed.Tags) return;
        if (old != null && old != Untagged && ByTag.TryGetValue(old, out var s)) s.Remove(g);
        if (now != null && now != Untagged && !g.Destroyed)
        {
            if (!ByTag.TryGetValue(now, out s)) ByTag[now] = s = new HashSet<NGameObject>();
            s.Add(g);
        }
    }
    /// <summary>Objects.Kill of a GameObject record.</summary>
    public static void Killed(NGameObject g)
    {
        if (K8Speed.Tags && g.Tag is { } t && t != Untagged && ByTag.TryGetValue(t, out var s)) s.Remove(g);
    }

    static bool UseIndex(string tag) => K8Speed.Tags && tag != null && tag != Untagged && Lifecycle.DestroyDepth == 0;

    // ---- kit #7's walk (also the check's reference) ----
    static object WalkOne(string tag) => Scenes.AllGameObjects().FirstOrDefault(g => g.Tag == tag && g.ActiveInHierarchy)?.Managed;
    static GameObject[] WalkAll(string tag) => Scenes.AllGameObjects().Where(g => g.Tag == tag && g.ActiveInHierarchy).Select(g => (GameObject)g.Managed).ToArray();

    /// <summary>GameObject.FindGameObjectWithTag (FindWithTag): the first active object with the tag in the walk's order, or null.</summary>
    public static object FindOne(string tag)
    {
        if (!UseIndex(tag)) { _walks++; return WalkOne(tag); }
        _one++;
        int n = Collect(tag);
        NGameObject best = null;
        if (n == 1) best = _cand[0];
        else if (n > 1) { int b = 0; for (int i = 1; i < n; i++) if (Before(i, b)) b = i; best = _cand[b]; }
        object r = best?.Managed;
        Clear(n);
        if (K8Speed.TagsCheck) { var w = WalkOne(tag); if (!ReferenceEquals(w, r)) Mismatch(tag, "FindWithTag", Describe(r), Describe(w)); return w; }
        return r;
    }
    /// <summary>GameObject.FindGameObjectsWithTag: every active object with the tag, in the walk's order.</summary>
    public static GameObject[] FindAll(string tag)
    {
        if (!UseIndex(tag)) { _walks++; return WalkAll(tag); }
        _many++;
        int n = Collect(tag);
        // stable insertion sort by path (candidates are few; distinct objects never compare equal)
        for (int i = 1; i < n; i++)
        {
            var c = _cand[i]; var k = _keys[i]; int kl = _klen[i]; int j = i - 1;
            while (j >= 0 && Less(k, kl, _keys[j], _klen[j])) { _cand[j + 1] = _cand[j]; _keys[j + 1] = _keys[j]; _klen[j + 1] = _klen[j]; j--; }
            _cand[j + 1] = c; _keys[j + 1] = k; _klen[j + 1] = kl;
        }
        var r = new GameObject[n];
        for (int i = 0; i < n; i++) r[i] = (GameObject)_cand[i].Managed;
        Clear(n);
        if (K8Speed.TagsCheck)
        {
            var w = WalkAll(tag);
            bool same = w.Length == r.Length; for (int i = 0; same && i < w.Length; i++) same = ReferenceEquals(w[i], r[i]);
            if (!same) Mismatch(tag, "FindGameObjectsWithTag", r.Length + " object(s): " + string.Join(", ", r.Select(x => Describe(x))), w.Length + " object(s): " + string.Join(", ", w.Select(x => Describe(x))));
            return w;
        }
        return r;
    }

    // ---- candidates: the tag's records the walk visits and finds (active in hierarchy, linked to a scene root both ways), with their paths ----
    static NGameObject[] _cand = new NGameObject[8];
    static int[][] _keys = new int[8][];
    static int[] _klen = new int[8];
    static int[] _sceneH = new int[8];
    static int _scenes;
    static int Collect(string tag)
    {
        if (!ByTag.TryGetValue(tag, out var set) || set.Count == 0) return 0;
        _scenes = 0;
        foreach (var sc in Scenes.All.Values) { if (_scenes == _sceneH.Length) Array.Resize(ref _sceneH, _scenes * 2); _sceneH[_scenes++] = sc.Handle; }   // (Scenes.All's enumeration order = the walk's; Handle = its key)
        int n = 0;
        foreach (var g in set)
        {
            _cands++;
            if (g.Destroyed || !g.ActiveInHierarchy) continue;
            if (n == _cand.Length) { Array.Resize(ref _cand, n * 2); Array.Resize(ref _keys, n * 2); Array.Resize(ref _klen, n * 2); }
            if (!PathKey(g, ref _keys[n], out _klen[n])) continue;
            _cand[n++] = g;
        }
        return n;
    }
    static void Clear(int n) { Array.Clear(_cand, 0, n); }
    /// <summary>[scene place, root place, child index at each level down to g]; false when the walk cannot reach g.</summary>
    static bool PathKey(NGameObject g, ref int[] key, out int len)
    {
        len = 0;
        int depth = 1; for (var t = g.Transform.Parent; t != null; t = t.Parent) depth++;
        int need = depth + 1;
        if (key == null || key.Length < need) key = new int[Math.Max(16, need)];
        int k = need - 1;
        var tr = g.Transform;
        for (; tr.Parent != null; tr = tr.Parent)
        {
            int i = tr.Parent.Children.IndexOf(tr);
            if (i < 0) return false;
            key[k--] = i;
        }
        var root = tr.Go;
        if (!Scenes.All.TryGetValue(root.Scene, out var s)) return false;
        int ri = s.Roots.IndexOf(root);
        if (ri < 0) return false;
        int si = -1; for (int i = 0; i < _scenes; i++) if (_sceneH[i] == root.Scene) { si = i; break; }
        if (si < 0) return false;
        key[0] = si; key[1] = ri;
        len = need;
        return true;
    }
    static bool Before(int a, int b) => Less(_keys[a], _klen[a], _keys[b], _klen[b]);
    /// <summary>Pre-order: the first differing index decides; a path that is a prefix of the other (an ancestor) comes first.</summary>
    static bool Less(int[] a, int al, int[] b, int bl)
    {
        int n = Math.Min(al, bl);
        for (int i = 0; i < n; i++) if (a[i] != b[i]) return a[i] < b[i];
        return al < bl;
    }

    static string Describe(object o) => o is UnityEngine.Object u && Objects.Get(u) is { } r ? "'" + r.Name + "' (" + r.InstanceId + ")" : o == null ? "null" : "?";
    static void Mismatch(string tag, string what, string index, string walk)
    {
        if (++_mismatch <= 10) Log.Warn("[k8] tags check: " + what + "('" + tag + "') differs: index " + index + ", walk " + walk);
    }

    public static string Summary() =>
        !K8Speed.Tags ? "tags: the walk (kit #7)" :
        "tags: " + _one + " FindWithTag + " + _many + " FindGameObjectsWithTag from the index (" + _cands + " candidate looks), " + _walks + " by the walk (Untagged / null / inside a destroy)" +
        (K8Speed.TagsCheck ? ", check: " + _mismatch + " difference(s)" : "");
}

/// <summary>Kit #8 speed track, the release logging diet (roadmap "Release logging diet": "in normal builds ... debug builds keep full
/// logs"; kit #5 found one repeated line costing ~13 ms of card write on the device). OPT-IN since the review (the default is kit #7's
/// AutoFlush per line: the test kits' engine.log is the evidence trail, and kit #8 brings the first native physics / JIT code to the device).
/// <c>SHEPHERD_K8_LOG_DIET=batch</c>: engine.log keeps every line, but the writer is no longer flushed per line -- the Boot loop flushes it once
/// at the end of each frame that logged (<see cref="EndFrame"/>), the writer itself when its buffers fill (~1 K chars / 4 KiB), Log.Flush after
/// the "done" line, and on a managed unhandled exception and at a managed process exit. What a HARD crash loses (a native fault, an
/// exec-memory fault, sgen's exit after "arena exhausted" -- no managed hook runs): every line written since the last flush -- the crashing
/// frame's lines, or up to ~4 KiB of a long frame (boot, a scene load). They exist nowhere else reliably: the black box's engine-line ring
/// (vita/shepherd/k7_measure.h) is RAM; it reaches the card as blackbox.txt only at a FAIL END / STALL / runtime fatal error (48 lines cut to
/// 200 chars) and as blackbox-live.txt every 5 s (its last 24 lines), and not at all with SHEPHERD_K7=0 / SHEPHERD_K7_BLACKBOX=0; the host
/// log keeps only the first 40 echoed lines plus WRN / ERR / EXC / [gl] / [audio] / [vitamem]. <c>ring</c> (opt-in: the release behaviour):
/// batch + the game's own Debug.Log and LogWarning lines (ULG / UWR) not written to engine.log at all -- only echoed and counted; errors,
/// asserts, exceptions and the engine's lines stay. Unset / <c>0</c> / <c>line</c>: AutoFlush per line (kit #7).</summary>
[ProfileOnly]
public static class LogDiet
{
    public static readonly bool Batch = K8Speed.LogBatch;
    public static bool Dirty;
    public static long Flushes, RingLines;
    static bool _hooked;
    /// <summary>Log.Open: the exit hooks (once).</summary>
    public static void Opened()
    {
        if (!Batch || _hooked) return;
        _hooked = true;
        AppDomain.CurrentDomain.UnhandledException += (_, _) => Log.Flush();
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Log.Flush();
    }
    /// <summary>The Boot loop, end of every frame.</summary>
    public static void EndFrame() { if (Batch && Dirty) Log.Flush(); }
    /// <summary>A game Debug.Log line of this type (0 error, 1 assert, 2 warning, 3 log, 4 exception) is kept out of engine.log.</summary>
    public static bool RingOnly(int type)
    {
        if (!K8Speed.LogRing || (type != 2 && type != 3)) return false;
        RingLines++;
        return true;
    }
    public static string Summary() =>
        "batched: " + Flushes + " flush(es)" + (K8Speed.LogRing ? ", " + RingLines + " game Debug.Log / LogWarning line(s) in the black box only (SHEPHERD_K8_LOG_DIET=ring)" : "");
}

/// <summary>Kit #8 speed track, exact Unity visibility signals -- OPT-IN (<c>SHEPHERD_K8_VISIBILITY=1</c>; default off = kit #7: Renderer.isVisible
/// always true, OnBecameVisible / OnBecameInvisible never sent). The game uses both (decomp: SpineRandomAnimationPicker disables an off-screen
/// skeleton's SkeletonAnimation, CritterBaseBird stops its LOD manager's updates, Follower / Task_Bat / SimpleSpineAnimator / DeadBodySliding /
/// SpawnParticlesOnHit / BruteRock / SpiderWebBomb / HubManager / GenerateRoom read isVisible), so the original's own savings switch on. Rules
/// (Unity's documented ones; the timing and the edge cases wait for the E1 editor check, hence opt-in): a renderer the world pass draws
/// (SpriteRenderer, MeshRenderer, SpriteShapeRenderer) is visible in a frame when any camera's pass keeps it after its culling-mask, enabled /
/// active and frustum tests (the same per-renderer test that culls the draw); a renderer that became visible gets OnBecameVisible on the
/// scripts of its GameObject right after that camera's culling (before the camera's draws); one visible in the previous frame and in no camera
/// this frame gets OnBecameInvisible after the last camera; isVisible = the state after the last frame's cameras (false before it was first
/// seen). Renderers the world pass never draws (particles, lines, trails, UI) keep kit #7's answer (true). Known differences to check: Unity
/// tests the world AABB of the bounds (this tests the transformed box: equal for unrotated 2D renderers), Unity also counts shadow-caster
/// culling, and render-texture cameras whose target nothing samples are skipped here. <c>SHEPHERD_K8_VISIBILITY_LOG=1</c>: the first 200
/// messages as log lines (the Mac message log).</summary>
[ProfileOnly]
public static class Visibility
{
    public static readonly bool On = K8Speed.VisibilityOn;
    static readonly bool LogOn = On && Environment.GetEnvironmentVariable("SHEPHERD_K8_VISIBILITY_LOG") == "1";
    sealed class St { public int Seen; public bool Visible; }
    static readonly Dictionary<NComponent, St> States = new();
    static readonly List<NComponent> _became = new(), _visible = new(), _gone = new();
    static int _frame;
    static long _becameN, _goneN, _logged;
    static readonly object[] _none = Array.Empty<object>();
    static readonly Dictionary<Type, MethodInfo> MsgOn = new(), MsgOff = new();   // (two maps: no value-tuple key instantiation in the full-AOT eboots)

    /// <summary>SceneRenderer.Frame, before the cameras.</summary>
    public static void FrameStart() { if (On) _frame++; }
    /// <summary>DrawWorld: r passed this camera's tests.</summary>
    public static void Seen(NComponent r)
    {
        if (!States.TryGetValue(r, out var s)) States[r] = s = new St();
        if (s.Seen == _frame) return;
        s.Seen = _frame;
        if (!s.Visible) { s.Visible = true; _became.Add(r); _visible.Add(r); }
    }
    /// <summary>DrawWorld, after the camera's renderer loop: OnBecameVisible for the ones that became visible in it.</summary>
    public static void AfterCameraCull()
    {
        if (_became.Count == 0) return;
        for (int i = 0; i < _became.Count; i++) { _becameN++; Send(_became[i], true); }
        _became.Clear();
    }
    /// <summary>SceneRenderer.Frame, after the last camera: OnBecameInvisible for those visible before and seen by no camera this frame.
    /// Review fix: a renderer that goes invisible leaves States too (it held the record -- and through it the managed renderer and its
    /// GameObject -- for good once it was destroyed while off-screen); States = exactly the visible renderers (_visible). The same answers: a
    /// missing entry reads isVisible false, as the kept Visible = false did, and a renderer seen again gets a fresh entry that becomes visible
    /// (OnBecameVisible) exactly as the old entry did (its Seen was an earlier frame either way).</summary>
    public static void FrameEnd()
    {
        if (!On) return;
        int w = 0;
        for (int i = 0; i < _visible.Count; i++)
        {
            var r = _visible[i];
            if (!States.TryGetValue(r, out var s)) continue;
            if (r.Destroyed) { States.Remove(r); _dropped++; continue; }
            if (s.Seen == _frame) { _visible[w++] = r; continue; }
            s.Visible = false; States.Remove(r); _gone.Add(r);
        }
        _visible.RemoveRange(w, _visible.Count - w);
        if (States.Count > _peak) _peak = States.Count;
        for (int i = 0; i < _gone.Count; i++) { _goneN++; Send(_gone[i], false); }
        _gone.Clear();
    }
    static int _peak; static long _dropped;
    /// <summary>Renderer.isVisible (Icalls/RendererIcalls.cs): null = not a world-pass renderer (the caller keeps kit #7's true).</summary>
    public static bool? IsVisible(NComponent r)
    {
        if (r == null || r.Destroyed || !(r.Managed is SpriteRenderer || r.Managed is MeshRenderer || Registry.IsSpriteShapeRenderer(r))) return null;
        return States.TryGetValue(r, out var s) && s.Visible;
    }
    static void Send(NComponent r, bool visible)
    {
        var go = r.Go; if (go == null || !go.ActiveInHierarchy) return;
        if (LogOn && ++_logged <= 200) Log.Info("[k8] visibility f" + Time.FrameCount + ": " + (visible ? "OnBecameVisible" : "OnBecameInvisible") + " '" + go.Name + "' (" + r.Managed?.GetType().Name + ")");
        var cs = Pooled<NComponent>.Copy(go.Components, out int n);
        try
        {
            for (int i = 0; i < n; i++)
            {
                if (cs[i] is not NMono m || m.Destroyed || !m.Awoken || m.Managed == null) continue;
                var t = m.Managed.GetType();
                var map = visible ? MsgOn : MsgOff;
                if (!map.TryGetValue(t, out var mi))
                {
                    for (var bt = t; bt != null && bt != typeof(MonoBehaviour) && mi == null; bt = bt.BaseType)
                        mi = bt.GetMethod(visible ? "OnBecameVisible" : "OnBecameInvisible", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly, null, Type.EmptyTypes, null);
                    map[t] = mi;
                }
                if (mi == null) continue;
                try { mi.Invoke(m.Managed, _none); } catch (TargetInvocationException e) { Log.Exception(e.InnerException ?? e, (visible ? "OnBecameVisible " : "OnBecameInvisible ") + t.Name); }
            }
        }
        finally { Pooled<NComponent>.Back(cs, n); }
    }
    public static string Summary() => "visibility (opt-in): " + _becameN + " OnBecameVisible, " + _goneN + " OnBecameInvisible, " + States.Count + " renderer(s) visible at the end (peak " + _peak + "), " + _dropped + " destroyed while visible";
}

/// <summary>Kit #8 speed track, the fixed-step multiplier (roadmap: "Unity runs physics and FixedUpdate at a fixed rate ... every frame-rate
/// gain also cuts physics work per frame"): the steps each frame ran and their time, per scene (the active scene when the frame's steps
/// ran; a scene visited twice is two rows), for the device logs -- the rate itself never changes (fidelity: 30 Hz physics is in the dropped
/// list); each step is made cheaper instead (the empty FixedUpdates skipped by this track, the native physics path, compiled FixedUpdate
/// scripts). One "[k8] fixed steps" line per scene at its end and in the end summary. Counted and timed only (one clock pair per frame);
/// <c>SHEPHERD_K8_FIXED_STEPS=0</c>: none.</summary>
[ProfileOnly]
public static class FixedSteps
{
    public static readonly bool On = K8Speed.Track && Environment.GetEnvironmentVariable("SHEPHERD_K8_FIXED_STEPS") != "0";
    sealed class Row { public string Scene; public int From, To, Frames, MaxSteps; public long Steps, Ticks; }
    static readonly List<Row> Rows = new();
    static Row _cur; static int _handle = -1;
    public static void Note(int steps, long ticks)
    {
        int h = Scenes.ActiveHandle;
        if (_cur == null || h != _handle)
        {
            if (_cur != null) Log.Info(Line(_cur));
            _handle = h; _cur = new Row { Scene = Scenes.All.TryGetValue(h, out var s) ? s.Name : "?", From = Time.FrameCount };
            if (Rows.Count < 64) Rows.Add(_cur);
        }
        _cur.Frames++; _cur.To = Time.FrameCount; _cur.Steps += steps; _cur.Ticks += ticks; if (steps > _cur.MaxSteps) _cur.MaxSteps = steps;
    }
    static string Line(Row r) =>
        "[k8] fixed steps: scene '" + r.Scene + "' f" + r.From + "-f" + r.To + ": " + r.Frames + " frames, " + r.Steps + " steps = " +
        (r.Steps / (double)Math.Max(1, r.Frames)).ToString("F2") + " steps/frame (max " + r.MaxSteps + "), " +
        (r.Ticks * 1000.0 / System.Diagnostics.Stopwatch.Frequency / Math.Max(1, r.Frames)).ToString("F3") + " ms/frame in the fixed steps (" +
        (r.Steps > 0 ? (r.Ticks * 1000.0 / System.Diagnostics.Stopwatch.Frequency / r.Steps).ToString("F3") : "-") + " ms/step)";
    public static void End() { if (On && _cur != null) Log.Info(Line(_cur)); }
}

/// <summary>Kit #8 speed track (lever 2: lists, no LINQ re-sort), Camera.main: the game reads it in 136 places (decomp), and the engine's answer
/// was a LINQ query per read -- OfType + Where over the camera registry, an OrderBy on each camera's depth (a sort buffer and a closure per
/// call), FirstOrDefault(tag == "MainCamera"). The same answer without them: among the registry's cameras that pass the same filter (alive, a
/// Camera, enabled, active in hierarchy, in a scene) and are tagged "MainCamera", the one with the lowest depth under float.CompareTo, the
/// earliest in registry order on a tie (OrderBy is stable). <c>SHEPHERD_K8_LISTS=check</c> also runs the query and counts any difference
/// (its answer is returned); <c>=0</c> = the query (kit #7).</summary>
[ProfileOnly]
public static class Lookups
{
    const string MainTag = "MainCamera";
    static long _main, _mainDiff;
    public static object MainCamera()
    {
        _main++;
        NBehaviour best = null; float bd = 0f;
        var items = Registry.Cameras.Items();
        for (int i = 0; i < items.Count; i++)
        {
            if (items[i] is not NBehaviour b || b.Destroyed || b.Managed is not Camera || !b.Enabled || b.Go == null || !b.Go.ActiveInHierarchy || b.Go.Scene == 0) continue;
            if (b.Go.Tag != MainTag) continue;
            float d = Conv.To(b.Get("depth"), typeof(float)) is float f ? f : 0f;
            if (best == null || d.CompareTo(bd) < 0) { best = b; bd = d; }
        }
        object r = best?.Managed;
        if (K8Speed.ListsCheck)
        {
            var q = Impl.CameraIcalls.AllCameras().FirstOrDefault(c => c.Go.Tag == MainTag)?.Managed;
            if (!ReferenceEquals(q, r) && ++_mainDiff <= 10) Log.Warn("[k8] lists check: Camera.main differs: fast '" + (best?.Go?.Name ?? "null") + "', query '" + (Objects.Get(q)?.Name ?? "null") + "'");
            return q;
        }
        return r;
    }
    public static string Summary() => "lists: " + _main + " Camera.main without the query" + (K8Speed.ListsCheck ? " (check: " + _mainDiff + " difference(s))" : "");
}
