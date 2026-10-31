using Shepherd.Engine.Native;
namespace Shepherd.Engine.Runtime;

/// <summary>Device kit #9, track engine (vita/notes/kit9-engine.md): the switches back to kit #8, read once at start from the environment (on
/// the Vita: the app's env file). <c>SHEPHERD_K9_ENGINE=0</c> turns every row of the track off (= kit #8's engine code paths); each row has its
/// own switch, and the rows that answer a lookup a new way have a <c>=check</c> mode that computes the kit #8 answer too, returns IT, and counts
/// any difference (the [k9] end line). None of the rows changes what the game sees or what is drawn: the same answers in the same order, found
/// with less work, less allocation or less memory traffic. (ProfileOnly: reads no property-bag keys, so the V3 diet's literal scan skips it --
/// every new literal of the track lives here or in the track's other ProfileOnly classes.)
/// <list type="bullet">
/// <item><c>SHEPHERD_K9_STAMPS</c> (default on): stamps instead of re-walks / cleared flags -- GameObject.activeInHierarchy is kept per transform
/// as one int (the answer packed with the hierarchy epoch it was computed at; an activeSelf or parent-link change of a transform without
/// children clears only its own stamp, any other change moves the epoch), each ancestor's answer reused once per epoch; the coherent sort's
/// "taken" marks are a pass stamp per item index (never cleared). (Review fix: an earlier version of this comment also promised per-renderer
/// seen / drawn frame stamps in a hot record -- not built: vita/notes/kit9-engine.md section 9.)</item>
/// <item><c>SHEPHERD_K9_MEMO</c> (default on): lookup memos -- the message method of a type (SendMessage / BroadcastMessage, Invoke /
/// InvokeRepeating, StartCoroutine(string), OnRectTransformDimensionsChange) found once per (type, name); icall result boxes shared while the
/// value is unchanged (Transform position / localPosition / rotation / localRotation / localScale / TransformPoint ..., Quaternion.Euler,
/// anchoredPosition / sizeDelta, activeSelf / CompareTag / isActiveAndEnabled and other bool getters, Shader.PropertyToID, unscaledTime,
/// Camera.aspect): the rewritten Unity stub unboxes a result at once, nothing keeps or compares the box. <c>=check</c> (review fix) returns a
/// new box for every result (kit #8's answer) and verifies that no result box handed out is written in place afterwards (K9Memo).</item>
/// <item><c>SHEPHERD_K9_WHEEL</c> (default on): coroutines waiting on WaitForSeconds and Invoke / InvokeRepeating timers sleep in a time-ordered
/// heap and wake into the resume pass in kit #8's order (the order they were started / added); the other waits are kept per kind.</item>
/// <item><c>SHEPHERD_K9_FIND</c> (default on): Object.FindObjectsOfType / FindObjectsByType / Resources.FindObjectsOfTypeAll and Shader.Find
/// answer from per-type lists kept in the registry's slot order (Native/Registry.cs dynamic kinds) instead of scanning every record.</item>
/// <item><c>SHEPHERD_K9_SORT</c> (default on): temporal-coherence draw sorting -- each camera pass starts its sort from the order that camera's
/// last pass gave the same renderers (new ones appended), repaired by insertion; the comparison is the one strict total order of kit #8
/// (Seq breaks every tie), so the result is the full sort's.</item>
/// <item><c>SHEPHERD_K9_HOT</c> (default on): packed hot records -- the world sort's keys (layer, order, distance, seq, group flag: 20 B per item)
/// in a contiguous key array next to the draw items, so the sort's comparisons do not touch the ~200-B draw items.</item>
/// <item><c>SHEPHERD_K9_RECYCLE</c> (default on): record recycling -- a destroyed record's native block and its transform memo go to a free list
/// and serve the next record created (cleared first, as a new one starts).</item>
/// <item><c>SHEPHERD_K9_PHASE</c> (default on): the engine's own housekeeping runs as slices of a fixed 16-frame phase cycle (Runtime/K9House.cs):
/// slot 0 the find lists' aging, slot 4 the per-object memo tables' dead keys (the PHASE row's own work: kit #8 kept them) and the cameras'
/// kept orders, slot 8 the purgeable arena's idle trim, slot 12 the timer wheel's heap compaction; never game logic, never the GC's own
/// triggers. <c>=0</c> (review fix: it used to leave the cycle running): no cycle -- the memo tables keep their dead keys (kit #8) and the
/// other rows' upkeep runs at the end of every frame. (The V3 diet's idle-file release is not part of it: Assets/Diet.cs already runs it
/// every 8th frame, kit #8's own schedule.)</item>
/// <item><c>SHEPHERD_K9_PURGE</c> (default on): one purgeable cache arena -- the engine's rebuildable caches (the waiting native blocks, the
/// transform-memo free list, the registry's pooled snapshot buffers) are dropped right before the black-frame collection of a scene
/// transition (Runtime/BlackFrameGc.cs), and the big snapshot buffers not asked for in 300 frames at slot 8 of the cycle; their owners
/// rebuild on next use. (Review fix: there is no heap-cap trigger -- the GC heap's cap lives in the host C (max-heap-size / soft-heap-limit),
/// the engine has no signal of it; what the arena can hold at most is in vita/notes/kit9-engine.md.)</item>
/// <item><c>SHEPHERD_K9_PREFETCH</c> (default off; needs the runtime track's PLD intrinsic): the hot walks' next-record prefetch hints.</item>
/// </list></summary>
[ProfileOnly]
public static class K9Engine
{
    static string Env(string k) => Environment.GetEnvironmentVariable(k);
    public static readonly bool Track = Env("SHEPHERD_K9_ENGINE") != "0";
    static string Mode(string k) => Track ? Env(k) ?? "" : "0";
    static readonly string StampsMode = Mode("SHEPHERD_K9_STAMPS"), MemoMode = Mode("SHEPHERD_K9_MEMO"), WheelMode = Mode("SHEPHERD_K9_WHEEL"),
        FindMode = Mode("SHEPHERD_K9_FIND"), SortMode = Mode("SHEPHERD_K9_SORT"), HotMode = Mode("SHEPHERD_K9_HOT");
    public static readonly bool Stamps = StampsMode != "0", StampsCheck = StampsMode == "check";
    public static readonly bool Memo = MemoMode != "0", MemoCheck = MemoMode == "check";
    public static readonly bool Wheel = WheelMode != "0", WheelCheck = WheelMode == "check";
    /// <summary>Review fix (SHEPHERD_K9_WHEEL_COMPACT, default on with the wheel; =0 = the gated e2 wheel): the timer wheel's two heaps drop
    /// stopped coroutines and cancelled timers (and timers of destroyed behaviours, which kit #8's next tick cancels) in the housekeeping cycle
    /// (slot 12) and right before the black-frame collection, instead of keeping them until their wake-up time.</summary>
    public static readonly bool WheelCompact = Wheel && Mode("SHEPHERD_K9_WHEEL_COMPACT") != "0";
    public static readonly bool Find = FindMode != "0", FindCheck = FindMode == "check";
    /// <summary>Review fix: the per-type lists classify each frame's records in one joint pass (Native/Registry.cs DrainTypedJoint);
    /// SHEPHERD_K9_FIND_JOINT=0 (test knob) = each list drains itself (c0b7463).</summary>
    public static readonly bool FindJoint = Find && Mode("SHEPHERD_K9_FIND_JOINT") != "0";
    public static readonly bool Sort = SortMode != "0", SortCheck = SortMode == "check";
    public static readonly bool Hot = HotMode != "0", HotCheck = HotMode == "check";
    public static readonly bool Recycle = Mode("SHEPHERD_K9_RECYCLE") != "0";
    public static readonly bool Phase = Mode("SHEPHERD_K9_PHASE") != "0";
    public static readonly bool Purge = Mode("SHEPHERD_K9_PURGE") != "0";
    public static readonly bool Prefetch = Track && Env("SHEPHERD_K9_PREFETCH") == "1";
    /// <summary>A row with upkeep in the housekeeping cycle (Runtime/K9House.cs) is on.</summary>
    internal static readonly bool HouseRows = Find || Sort || Purge || WheelCompact;

    static string OnOff(bool on, bool check) => check ? "on+check" : on ? "on" : "off";
    /// <summary>The start line (Boot, once the log is open): which rows are on.</summary>
    public static string Describe() =>
        "[k9] engine track " + (!Track ? "OFF (SHEPHERD_K9_ENGINE=0: kit #8's engine code paths)" :
        "stamps " + OnOff(Stamps, StampsCheck) + ", memos " + OnOff(Memo, MemoCheck) + ", timer wheel " + OnOff(Wheel, WheelCheck) + (Wheel && !WheelCompact ? " (heap compaction off)" : "") +
        ", find indexes " + OnOff(Find, FindCheck) + (Find && !FindJoint ? " (per-list classification)" : "") + ", coherent sort " + OnOff(Sort, SortCheck) + ", hot records " + OnOff(Hot, HotCheck) +
        ", recycling " + OnOff(Recycle, false) + ", housekeeping cycle " + (Phase ? "on" : HouseRows ? "off (no dead-key pruning; the other rows' upkeep every frame)" : "off") + ", purgeable arena " + OnOff(Purge, false) +
        ", prefetch " + (Prefetch ? "requested (no runtime intrinsic in this build: no-op)" : "off"));

    // ---------------- the check modes: differences (the kit #8 answer is returned) ----------------
    static long _diffs; static int _logged;
    /// <summary>A check-mode difference: counted, the first 12 logged.</summary>
    public static void Diff(string what, string detail)
    {
        _diffs++;
        if (++_logged <= 12) Log.Warn("[k9] check: " + what + " differs: " + detail);
    }
    public static long Diffs => _diffs;
    /// <summary>The finds <see cref="CheckFind"/> names: Object.FindObjectsOfType / FindObjectsByType (Icalls/Core.cs), Resources.FindObjectsOfTypeAll
    /// (Icalls/ResourceIcalls.cs FindAllFast).</summary>
    public const int FindOfTypeKind = 0, FindAllKind = 1;
    /// <summary>=check: the find index's answer (b) against the scan's (a), element by element.</summary>
    public static void CheckFind(Type t, List<UnityEngine.Object> a, List<UnityEngine.Object> b, int kind = FindOfTypeKind)
    {
        bool same = a.Count == b.Count; int at = -1; for (int i = 0; same && i < a.Count; i++) if (!(same = ReferenceEquals(a[i], b[i]))) at = i;
        if (!same) Diff((kind == FindAllKind ? "Resources.FindObjectsOfTypeAll(" : "FindObjectsOfType(") + t?.Name + ")", "scan " + a.Count + " object(s), index " + b.Count + (at >= 0 ? ", first difference at " + at : ""));
    }
    /// <summary>=check (review fix): Shader.Find / FindShaderByName / ShaderNamed -- the scan's first match (a) against the shader records' (b).</summary>
    public static void CheckShader(string name, Native.NShader a, Native.NShader b)
    {
        if (!ReferenceEquals(a, b)) Diff("Shader.Find(" + name + ")", "scan " + (a == null ? "no match" : "a match") + ", shader records " + (b == null ? "no match" : a == null ? "a match" : "another record"));
    }
    /// <summary>The searches the wheel's per-behaviour counts skip (Runtime/Lifecycle.cs), for <see cref="DiffCount"/>.</summary>
    public const int SkipStopAll = 0, SkipStopRoutine = 1, SkipStopMethod = 2, SkipCancelInvoke = 3, SkipIsInvoking = 4;
    /// <summary>=check (review fix): a search the count said was empty (NMono.CoN / InvN = 0) found a live coroutine / timer of the behaviour -- the
    /// skip would have answered differently; kit #8's answer is applied.</summary>
    public static void DiffCount(object owner, string method, int what) => Diff(what switch
    {
        SkipStopAll => "StopAllCoroutines skipped (the behaviour's coroutine count was 0)",
        SkipStopRoutine => "StopCoroutine(IEnumerator) skipped (the behaviour's coroutine count was 0)",
        SkipStopMethod => "StopCoroutine(string) skipped (the behaviour's coroutine count was 0)",
        SkipCancelInvoke => "CancelInvoke skipped (the behaviour's timer count was 0)",
        _ => "IsInvoking answered false (the behaviour's timer count was 0)"
    }, (owner?.GetType().Name ?? "?") + "." + method);
    public static void DiffSort(int n, int at) => Diff("world sort", n + " items, first difference at position " + at);
    public static void DiffCoroutine(object owner, string method) => Diff("coroutine resume (not in the wheel's list, or out of order)", (owner?.GetType().Name ?? "?") + "." + method);
    public static void DiffInvoke(object owner, string method) => Diff("Invoke fire (not in the wheel's due list, or out of order)", (owner?.GetType().Name ?? "?") + "." + method);

    // ---------------- counters (the end line) ----------------
    public static long SortRepairLong, AhRefLinks;
    public static long AhHits, AhWalks, MemoMethodHits, MemoMethodMisses, BoxShared, WheelSleeps, WheelWakes, WheelCompacted, WheelRenumbers, FindIndexed, FindScans, SortSeeded, SortFull, Recycled, PhaseSlices, Purged, PurgedBytes;
    /// <summary>Review fix (the find lists' cost): records the per-type lists classified as they were bound (TypedSeen), those past the class
    /// prefilter that took the type test (TypedTests), the Mac-only time of EndFrame's per-type drains (TypedTicks, Stopwatch ticks; 0 in the
    /// eboots), the busiest frame of that; and apart, the lists' builds (TypedBuilds: each one scan of every record, TypedBuilt records).</summary>
    public static long TypedSeen, TypedTests, TypedTicks, TypedBuilds, TypedBuilt, BoxChecked, WheelFlushed;
    static long _tsSeen, _tsTests, _tsTicks, _maxSeen, _maxTests, _maxTicks; static int _maxFrame, _maxLists;
    /// <summary>Runtime/K9House.cs, the end of every frame (find row on): the frame's per-type classification, kept if it is the busiest so far.</summary>
    internal static void NoteTypedFrame(int frame)
    {
        long seen = TypedSeen - _tsSeen; _tsSeen = TypedSeen;
        long tests = TypedTests - _tsTests; _tsTests = TypedTests;
        long ticks = TypedTicks - _tsTicks; _tsTicks = TypedTicks;
        if (seen > _maxSeen) { _maxSeen = seen; _maxTests = tests; _maxTicks = ticks; _maxFrame = frame; _maxLists = Registry.TypedLists; }
    }

    /// <summary>The end line (Boot): what the rows did; null with the track off.</summary>
    public static string Summary()
    {
        if (!Track) return null;
        if (MemoCheck) K9Memo.VerifyRing();   // (review fix: the result boxes still in the check's ring)
        var s = "[k9] engine: ";
        if (Stamps) s += "activeInHierarchy " + AhHits + " from the stamp, " + AhWalks + " computed" + (StampsCheck ? " (the check's reference walks: " + AhRefLinks + " links)" : "") + "; ";
        if (Memo) s += "message methods " + MemoMethodHits + " memo hits / " + MemoMethodMisses + " looked up; boxes shared " + BoxShared + (MemoCheck ? " (check: " + BoxChecked + " result boxes verified unwritten)" : "") + "; ";
        if (Wheel) s += "timer wheel: " + WheelSleeps + " sleeps, " + WheelWakes + " wakes" + (WheelCompact ? ", " + WheelCompacted + " dead heap entries compacted, " + WheelFlushed + " dead entries released before a black-frame collection" : "") + (WheelRenumbers > 0 ? ", " + WheelRenumbers + " order renumberings" : "") + "; ";
        if (Find) s += "find: " + FindIndexed + " answered from a type list, " + FindScans + " scans, " + TypedBuilds + " type-list builds (" + TypedBuilt + " records scanned), records classified as bound " + TypedSeen + " (" + TypedTests + " type tests), busiest frame f" + _maxFrame + " " + _maxSeen + " records / " + _maxTests + " tests / " + _maxLists + " lists" +
            (TypedTicks > 0 ? " / " + (_maxTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency).ToString("F2", System.Globalization.CultureInfo.InvariantCulture) + " ms" : "") + "; ";
        if (Sort) s += "sort: " + SortSeeded + " passes seeded from the camera's last order, " + SortFull + " full sorts of 2+ items (" + SortRepairLong + " after a too-long repair); ";
        if (Recycle) s += "recycled records " + Recycled + "; ";
        if (Phase) s += "housekeeping slices " + PhaseSlices + "; ";
        if (Purge) s += "purged " + Purged + " block(s), " + (PurgedBytes / 1024) + " KiB; ";
        if (StampsCheck || MemoCheck || WheelCheck || FindCheck || SortCheck || HotCheck) s += "check: " + _diffs + " difference(s)";
        return s.TrimEnd(' ', ';');
    }
}
