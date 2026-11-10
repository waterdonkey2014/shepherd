namespace Shepherd.Engine.Runtime;

/// <summary>Kit #13 basec (vita/notes/kit13-basec.md): the base / dungeon groundwork rows' env-file switches. SHEPHERD_K13_BASEC=0 turns every
/// row of this track off (= kit #12); each row also has its own switch (0 = off). Read once at start; <see cref="Describe"/> logs which arm runs
/// (frame 0) and <see cref="Summary"/> what the rows did (end of run).</summary>
[ProfileOnly]
public static class K13Basec
{
    static readonly bool Master = Environment.GetEnvironmentVariable("SHEPHERD_K13_BASEC") != "0";
    static bool Row(string name, bool dflt = true)
    {
        if (!Master) return false;
        var v = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrEmpty(v) ? dflt : v is not ("0" or "off");
    }

    /// <summary>SHEPHERD_K13_POLYPATHS: the PolygonCollider2D / EdgeCollider2D path icalls served from the typed collider records (the serialized
    /// m_Points) below physics level 4, by the same code that serves them at level 4 and above (Physics/Physics2DPaths.cs). Default: physics
    /// level 0 only (no core: the hooks read and write the record and nothing else) and not under the Vita host (no device app runs the base at
    /// level 0, and the binder's scan would only add boot work to the level-0 Measure app). "1" / "on" = level 0 under the Vita host too;
    /// "all" = levels 0-3 (with a core at 1-3 a path set queues the collider and its shapes are rebuilt -- a physics change, gated only by the
    /// level-2 room counts); "0" / "off" = unbound below level 4 (kit #12: pathCount 0, GetPath(0) throws "Path 0 does not exist").</summary>
    static readonly string PolyRaw = Master ? Environment.GetEnvironmentVariable("SHEPHERD_K13_POLYPATHS") ?? "" : "0";
    public static bool PolyPathsAt(int level) => level >= 0 && level < 4 && PolyRaw switch
    {
        "0" or "off" => false,
        "all" => true,
        "1" or "on" => level == 0,
        _ => level == 0 && !Diag.VitaHost.On,
    };
    /// <summary>The hooks bound below level 4 (IcallRegistry; for the log).</summary>
    public static int PolyPathsBound;

    /// <summary>SHEPHERD_K13_CAMCANVAS (default on): a Screen Space - Camera root canvas whose camera is disabled (or inactive) is not drawn --
    /// Unity's documented rule (the canvas is rendered by its Render Camera; only with no camera set is it drawn as an overlay); no PC capture
    /// of the screen it fixes exists (Render/SceneRenderer.cs CameraCanvasHidden). Off: drawn as an overlay (kit #12: the first dungeon's room
    /// under a white screen).</summary>
    public static readonly bool CamCanvas = Row("SHEPHERD_K13_CAMCANVAS");
    /// <summary>Distinct root canvases the rule kept off screen (for the log).</summary>
    public static int CamHidden;

    /// <summary>SHEPHERD_K13_STRPOOL (default on, from the first frame's end): one copy per value of the strings the asset readers make while a
    /// frame's data is read (Shepherd.Assets.StringPool; the pack's inline strings are never pooled). "boot" = the boot frame pooled too (the
    /// first version of this row); off: every read makes its own string (kit #12).</summary>
    public static readonly bool StrPool = Row("SHEPHERD_K13_STRPOOL");
    public static readonly bool StrPoolBoot = StrPool && Environment.GetEnvironmentVariable("SHEPHERD_K13_STRPOOL") == "boot";

    /// <summary>The track's line for the log (frame 0): which arm of every row runs.</summary>
    public static string Describe() => !Master ? "[k13 basec] every row off (SHEPHERD_K13_BASEC=0 = kit #12)"
        : $"[k13 basec] polygon / edge paths below physics level 4: {(PolyPathsAt(0) || PolyPathsAt(1) ? (PolyPathsAt(1) ? "levels 0-3" : "level 0") : "unbound")} " +
          $"(SHEPHERD_K13_POLYPATHS={(PolyRaw.Length > 0 ? PolyRaw : "default")}{(Diag.VitaHost.On ? ", Vita host" : "")}); " +
          $"camera-mode canvases with their camera off {(CamCanvas ? "not drawn" : "drawn as overlays (SHEPHERD_K13_CAMCANVAS=0)")}; " +
          $"string pool {(StrPool ? (StrPoolBoot ? "on from the boot (SHEPHERD_K13_STRPOOL=boot)" : "on from the first frame's end") : "off (SHEPHERD_K13_STRPOOL=0)")}; lazy skins over resident skeleton files {(SpineLazySkins.Resident ? "on" : SpineLazySkins.On ? "off (SHEPHERD_K13_LAZYSKINS=0)" : "off (L2 off)")}";

    /// <summary>The track's end-of-run line (null with every row off).</summary>
    public static string Summary() => !Master ? null
        : $"[k13 basec] end: {Shepherd.Assets.StringPool.Summary() ?? "string pool off"}; camera-mode canvases kept off screen {CamHidden}; " +
          $"polygon / edge path hooks bound below level 4: {PolyPathsBound}";
}
