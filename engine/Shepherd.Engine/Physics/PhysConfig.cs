namespace Shepherd.Engine.Physics;

/// <summary>Physics stage 0 (physics-plan-v2 section 4 "Stage 0: groundwork"; vita/notes/physics-k6.md): the physics switches, read once.
/// <para><b>SHEPHERD_PHYS</b> = the stage level (Appendix A). <c>none</c> = kit #4's engine exactly: no typed physics records, no
/// PhysInterest bookkeeping, no hooks, no loop slots (Triggers2D.Step called where it always was) -- the switch back for everything
/// Stage 0 adds. <c>0</c> (the default) = Stage 0's groundwork present and inert: typed records built next to the property bags,
/// PhysInterest / PhysDesc kept, hooks and loop slots called with empty bodies, Triggers2D.Step from slot B -- behaviour identical
/// to <c>none</c>. Levels 1..7 belong to later stages: this build runs them as 0 and says so once.</para>
/// <para>Other switches: SHEPHERD_PHYS_H=name=variant,... (hypothesis switches; Stage 0 decides none and only records the defaults it
/// relied on, see <see cref="Hypotheses"/>); SHEPHERD_CHECK_PHYS=sync|tree|hooks|all (check modes: the dirty hook against a full
/// WorldStamp scan, the maintained PhysInterest / PhysDesc against a recount, the hook call counts); SHEPHERD_PHYS_TRACE=&lt;file&gt;
/// (the native trace writer); SHEPHERD_PHYS_POOL_MB (default 4); SHEPHERD_PHYS_LIB (the Mac dylib); SHEPHERD_PHYS_HOST=1 (the Vita
/// host's shepphys_proc).</para></summary>
[ProfileOnly]   // switch names and log text only: no property-bag key is read here (Assets/Diet.cs)
public static class PhysConfig
{
    /// <summary>-1 = none (kit #4), 0.. = the level this build runs.</summary>
    public static readonly int Level;
    /// <summary>The level asked for (SHEPHERD_PHYS), before clamping to what this build implements.</summary>
    public static readonly int Asked;
    /// <summary>The highest level this build implements.</summary>
    public const int Implemented = 6;   // physics Stage 1: the core simulates (bodies, solid shapes, MovePosition, write-back, interpolation); Stage 2: triggers in the core (P1), one dispatcher, queries; Stage 3: combat (forces, collisions + Collision2D, the time fixes, the step rule); Stage 4 step 4a: the path icalls, Unity's chain vertex filter, composite paths in body space, Synchronous composites (4b-4e and Stage 5 are not built: level 5 runs as 4); Stage 6: the 3D island-ground ray (level 6 = Stage 3 + 4a + Stage 6)
    /// <summary>Stage 0's groundwork is on (Level >= 0): typed records, interest bookkeeping, hooks, loop slots.</summary>
    public static readonly bool On;
    public static readonly bool CheckSync, CheckTree, CheckHooks, CheckBrute;
    public static readonly string Raw, Check;
    public static readonly string TracePath = Environment.GetEnvironmentVariable("SHEPHERD_PHYS_TRACE") is { Length: > 0 } t ? t : null;
    /// <summary>The pool sp_init gets, MiB (SHEPHERD_PHYS_POOL_MB). Default 4 -- but 0 under the Vita host (SHEPHERD_PHYS_HOST=1, kit #8): the
    /// library then sizes the pool inside the host's pool block (a quarter of it; B2's arena the rest: vita/shepherd/phys_host.inc.c).</summary>
    public static readonly int PoolMb = int.TryParse(Environment.GetEnvironmentVariable("SHEPHERD_PHYS_POOL_MB"), out var mb) && mb > 0 ? mb
        : Environment.GetEnvironmentVariable("SHEPHERD_PHYS_HOST") == "1" ? 0 : 4;
    public static readonly bool HostProc = Environment.GetEnvironmentVariable("SHEPHERD_PHYS_HOST") == "1";
    /// <summary>SHEPHERD_TRIGGERS2D=nomatrix (Runtime/Triggers2D.cs): the matrix A/B's check mode (independent of SHEPHERD_PHYS).</summary>
    public static bool TriggersNoMatrix => Environment.GetEnvironmentVariable("SHEPHERD_TRIGGERS2D") == "nomatrix";
    /// <summary>Physics Stage 2's swap A/B check modes (Runtime/Triggers2D.cs, Physics/PhysShadow.cs): SHEPHERD_TRIGGERS2D=shadow runs the pair search
    /// beside the core without sending (level 2 only); =parity is its strict variant.</summary>
    public static bool TriggersShadow => Level >= 2 && Environment.GetEnvironmentVariable("SHEPHERD_TRIGGERS2D") is "shadow" or "parity";
    public static bool TriggersParity => Level >= 2 && Environment.GetEnvironmentVariable("SHEPHERD_TRIGGERS2D") == "parity";
    /// <summary>Physics Stage 3 (plan section 3 "Time fixes", Appendix A SHEPHERD_FIXEDTIME): Time.time / unscaledTime / unscaledDeltaTime are the
    /// fixed clock's inside the fixed step and its callbacks, fixedUnscaledDeltaTime = fdt / timeScale and fixedUnscaledTime its own clock -- on
    /// the frame clock too (the wall clock has them since kit #3). Default (-1): on where Stage 3 runs (PhysWorld.Sim3: level 3 with a core), off
    /// elsewhere (kit #4's frame clock exactly); SHEPHERD_FIXEDTIME=1 / 0 forces it at any level (the A/B).</summary>
    public static readonly int FixedTimeEnv;   // -1 unset, 0, 1 (set in the static constructor)
    /// <summary>Physics Stage 3 (plan section 3, D7: "Unity's rule everywhere"): SHEPHERD_PHYS_STEPCAP=unity -- the frame clock takes as many 50 Hz
    /// steps as game time needs (only Time.maximumDeltaTime bounds a frame, as the wall clock and Unity do); =5 keeps the frame clock's old cap of 5
    /// steps a frame. Default: unity at level 3 and above, 5 below. (At the default 1/60 s frame the cap never binds; SHEPHERD_CAPTURE_DT above
    /// 0.1 s does.) Default (0): unity where Stage 3 runs (PhysWorld.Sim3), 5 elsewhere; the switch forces it at any level.</summary>
    public static readonly int StepCapEnv;     // 0 unset, else the cap (int.MaxValue = unity)

    static PhysConfig()
    {
        Raw = (Environment.GetEnvironmentVariable("SHEPHERD_PHYS") ?? "").Trim().ToLowerInvariant();
        if (Raw is "none" or "off" or "kit4") Asked = -1;
        else if (Raw.Length == 0) Asked = 0;
        else if (!int.TryParse(Raw, out Asked) || Asked < 0 || Asked > 7) Asked = 0;
        Level = Math.Min(Asked, Implemented);
        On = Level >= 0;
        FixedTimeEnv = Environment.GetEnvironmentVariable("SHEPHERD_FIXEDTIME") is { Length: > 0 } ft ? (ft is "1" or "on" ? 1 : 0) : -1;
        StepCapEnv = Environment.GetEnvironmentVariable("SHEPHERD_PHYS_STEPCAP") is { Length: > 0 } sc ? (sc == "unity" ? int.MaxValue : int.TryParse(sc, out var n) && n > 0 ? n : 5) : 0;
        Check = (Environment.GetEnvironmentVariable("SHEPHERD_CHECK_PHYS") ?? "").Trim().ToLowerInvariant();
        bool all = Check == "all";
        CheckSync = On && (all || Check.Split(',').Contains("sync"));
        CheckTree = On && (all || Check.Split(',').Contains("tree"));
        CheckHooks = On && (all || Check.Split(',').Contains("hooks"));
        CheckBrute = On && (all || Check.Split(',').Contains("brute"));   // physics Stage 2: queries and trigger pairs against a brute force without the broadphase
    }

    /// <summary>The boot line (Lifecycle's first frame) -- one place that says which physics this run has.</summary>
    public static string Describe()
    {
        string lvl = Level < 0 ? "none (kit #4: no Stage 0 groundwork)" : Level == 0 ? "0 (groundwork on, inert: Triggers2D from loop slot B)"
            : Level == 1 ? "1 (Stage 1: the core simulates bodies and solid shapes -- MovePosition, write-back, interpolation; triggers from Triggers2D after the step)"
            : Level == 2 ? "2 (Stage 2: + triggers in the core (patch P1), the one dispatcher (Physics/PhysDispatch.cs), the Unity queries)"
            : Level == 3 ? $"{Level} (Stage 3: + combat -- forces, velocity / drag / constraints setters, collision callbacks with Collision2D, IgnoreCollision; time fixes and the step rule: see [phys] time)"
            : Level <= 5 ? $"{Level} (Stage 3's combat + Stage 4 step 4a: the PolygonCollider2D / EdgeCollider2D / CompositeCollider2D path icalls, Unity's chain vertex filter, composite paths in body space, " +
                           $"Synchronous composites, usedByComposite's refusal{(Level == 5 ? "; Stage 5 is not built: level 5 runs as 4" : "")}; Stage 4 steps 4b-4e are not built)"
            : $"{Level} (Stage 3's combat + Stage 4 step 4a (the path icalls, the chain vertex filter, composite paths in body space, Synchronous composites) + Stage 6: the 3D island-ground ray -- Physics.Raycast / RaycastNonAlloc / RaycastTest against the MeshColliders (BVH, +z fast path) and Box / Sphere / Capsule, the 3D collider icalls; Stage 5 and Stage 4 steps 4b-4e are not built)";
        string asked = Asked != Level ? $"; SHEPHERD_PHYS={Raw} asked for level {Asked}, this build implements up to {Implemented}" : "";
        string bad = Raw.Length > 0 && Asked == 0 && Raw != "0" ? $"; SHEPHERD_PHYS='{Raw}' not understood: 0" : "";
        string chk = Check.Length > 0 ? $"; checks {Check}" : "";
        return $"[phys] level {lvl}{asked}{bad}{chk}; hypotheses: {Hypotheses.Describe()}";
    }
}

/// <summary>SHEPHERD_PHYS_H=name=variant,... (plan Appendix A). Stage 0 made the parser; the stages read their rows through <see cref="Get"/>.
/// <para><b>Since E1</b> (physics-plan-v2 section 5 / T6: the Unity 2022.3.62f2 oracle, physics-work/e1, decisions in
/// vita/notes/physics-ledger.md): every switch E1 decided has exactly one variant left -- Unity's -- and the losing variants are deleted from
/// the engine (the ledger lists what was removed); naming a deleted variant logs "unknown variant" and runs Unity's. Only the rows E1 left
/// open keep their alternatives: H-CBORDER (doubtful: E1 measured a new `list` order, not implemented; `serial` stays) and H-NONALLOC
/// (undecided: PhysX's pruner order). <see cref="Pending"/> names the E1 decisions the engine does not implement yet.</para></summary>
[ProfileOnly]   // switch names and log text only
public static class Hypotheses
{
    /// <summary>(name, variants -- the first is the one in effect by default, decided = E1 settled it: one variant only).</summary>
    static readonly (string name, string[] variants, bool decided)[] Rows =
    {
        ("TRIGPHASE", new[] { "final" }, true), ("KINTRIG", new[] { "all-but-static" }, true),
        ("KINSLEEP", new[] { "sleep" }, true), ("SLEEPSTAY", new[] { "stop" }, true),
        ("KINISLAND", new[] { "propagate" }, true), ("WAKETP", new[] { "wake" }, true), ("SYNC", new[] { "A" }, true),
        ("EARLY", new[] { "none" }, true), ("INTERP", new[] { "lerp" }, true), ("MOVEVEL", new[] { "restore" }, true),
        ("MOVEDRAG", new[] { "drag-off-gravity-on" }, true), ("DAMP", new[] { "pade" }, true), ("CHAIN", new[] { "two-sided" }, true),
        ("TILT", new[] { "matrix" }, true), ("CHILDSYNC", new[] { "follow" }, true), ("EXITSYNC", new[] { "sync" }, true),
        ("REENABLE", new[] { "exit+enter" }, true), ("KINDORDER", new[] { "grouped" }, true),
        ("CBORDER", new[] { "serial", "state", "b2list", "ids" }, false),   // E1: DOUBTFUL (Unity's order is a persistent touching list, `list`: not implemented)
        ("BODYMSG", new[] { "collider+body" }, true),
        ("RECREATE", new[] { "silent" }, true), ("DROPQUEUED", new[] { "deliver" }, true),
        ("CAPTURE", new[] { "report-only" }, true), ("TIE", new[] { "exclude-wins" }, true),
        ("RELVEL", new[] { "pre-solve" }, true), ("MAXTRANS", new[] { "per-step" }, true),
        ("MAXROT", new[] { "per-step" }, true), ("SLOP", new[] { "half-offset" }, true), ("AUTOFIT", new[] { "on" }, true),
        ("COMPORIENT", new[] { "normalize" }, true), ("CLEAN", new[] { "spikes" }, true),
        ("RELVELSIGN", new[] { "other-minus-self" }, true), ("NORMAL", new[] { "toward-self" }, true),
        ("STARTNORMAL", new[] { "unity" }, true),   // rays from inside: distance 0, the origin, -dir; casts starting overlapped: the separating normal, the overlap midpoint
        ("CASTSLOP", new[] { "toi" }, true), ("QSKIN", new[] { "overlaps" }, true), ("BOUNDSSKIN", new[] { "none" }, true), ("OVERLAPORDER", new[] { "tree" }, true),
        ("RAYPOINT", new[] { "bary" }, true),
        ("NONALLOC", new[] { "closest", "found" }, false),   // E1: UNDECIDED (neither closest nor creation order: PhysX's scene pruner)
    };
    /// <summary>E1 decisions the engine does not implement yet (it runs the behaviour in brackets): the ledger's "pending" rows.</summary>
    public const string Pending = "H-MTSLEEP=unity-jobs (runs Box2D's per-island sleep), FreezePosition as a solver constraint (runs the post-step hold), " +
                                  "the awake rule's wakes for static-collider moves (runs Box2D's rule), AUTOFIT for PolygonCollider2D (no sprite outline: Stage 4c), " +
                                  "PhysX's exact mesh-ray arithmetic + the box normal / capsule / flat-box fixes (runs Stage 6's), H-CBORDER=list (runs serial), " +
                                  "contactCaptureLayers in GetContacts / IsTouching (callbacks only)";
    static readonly Dictionary<string, string> _set = Parse();
    static Dictionary<string, string> Parse()
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in (Environment.GetEnvironmentVariable("SHEPHERD_PHYS_H") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var p = kv.Split('=', 2); if (p.Length != 2) continue;
            var name = p[0].Trim().ToUpperInvariant(); if (name.StartsWith("H-")) name = name[2..];
            d[name] = p[1].Trim().ToLowerInvariant();
        }
        return d;
    }
    /// <summary>The selected variant of a switch (its default unless SHEPHERD_PHYS_H names a known variant).</summary>
    public static string Get(string name)
    {
        foreach (var (n, v, _) in Rows)
            if (n == name) return _set.TryGetValue(n, out var s) && v.Contains(s, StringComparer.OrdinalIgnoreCase) ? s : v[0];
        return null;
    }
    /// <summary>"E1's decisions" or the non-default / unknown settings.</summary>
    public static string Describe()
    {
        var parts = new List<string>();
        foreach (var kv in _set)
        {
            var row = Rows.FirstOrDefault(r => r.name == kv.Key);
            if (row.name == null) parts.Add($"H-{kv.Key}={kv.Value} (unknown switch, ignored)");
            else if (!row.variants.Contains(kv.Value, StringComparer.OrdinalIgnoreCase))
                parts.Add($"H-{kv.Key}={kv.Value} ({(row.decided ? "deleted by E1" : "unknown variant")}: {row.variants[0]})");
            else if (!string.Equals(kv.Value, row.variants[0], StringComparison.OrdinalIgnoreCase)) parts.Add($"H-{kv.Key}={kv.Value}");
        }
        int decided = Rows.Count(r => r.decided);
        string open = string.Join(", ", Rows.Where(r => !r.decided).Select(r => $"H-{r.name}={Get(r.name)}"));
        string head = parts.Count == 0 ? $"{decided} decided by E1, open: {open}" : string.Join(", ", parts) + $" ({decided} decided by E1, open: {open})";
        return PhysConfig.Level >= 1 ? head + "; E1 decisions not implemented yet: " + Pending : head;
    }
    /// <summary>For the exit summary: every switch with its value.</summary>
    public static string Table() => string.Join(" ", Rows.Select(r => $"H-{r.name}={Get(r.name)}"));
}
