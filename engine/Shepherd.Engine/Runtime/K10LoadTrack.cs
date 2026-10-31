namespace Shepherd.Engine.Runtime;

/// <summary>Device kit #10, track load (vita/notes/kit10-load.md): the track's master switch and its start / end lines.
/// SHEPHERD_K10_LOAD=0 turns every row of the track off (= kit #9's loader); each row also has its own switch, independent of the others:
///   SHEPHERD_K10_FIELDPROG   E3 field programs for MonoBehaviour / ScriptableObject fields (Assets/FieldPrograms.cs; =check: + a ToManaged comparison)
///   SHEPHERD_K10_BINPROG     binary field programs for stripped scripts = the asset object snapshots (Assets/BinPrograms.cs)
///   SHEPHERD_K10_CREATEPROG  E3 create programs: key reads by place, per-type / per-script memos, shader places, atlas index (Assets/TreeIdx.cs, Loader)
///   SHEPHERD_K10_SEEDPROG    E3 for the built-in components' property bags (Assets/SeedPrograms.cs)
///   SHEPHERD_K10_CLONEPROG   E3 for Instantiate: the property bag copied in one step, DeepCopy by a per-type plan (Runtime/Cloner.cs)
///   SHEPHERD_K10_ANIMBIND    Animator binding memos + typed clip-array reads (Anim/AnimData.cs, Anim/AnimatorRuntime.cs)
///   SHEPHERD_K10_LOADPROF    the loader's own profile (tools only: Assets/LoadProf.cs);  SHEPHERD_K10_LOADDUMP=&lt;file&gt; the load dump (Assets/LoadDump.cs)
/// Named K10LoadTrack (kit #10 review): the boot track's loading-priority switch class is Runtime/K10Load.cs. The start line is written by
/// Loader.Init (<see cref="Start"/>), the end lines by <see cref="End"/> at the run's end summaries (FieldCopy.Summary's call in Boot: Boot.cs is
/// the boot track's file -- the integration may move both calls into Boot.cs next to the kit #9 lines, see the note's section 6).</summary>
public static class K10LoadTrack
{
    public static readonly bool AllOff = Environment.GetEnvironmentVariable("SHEPHERD_K10_LOAD") is "0" or "off";
    static bool Row(string env) => !AllOff && Environment.GetEnvironmentVariable(env) is not ("0" or "off");
    /// <summary>E3 create programs: the Create* steps read the tree by place (Assets/TreeIdx.cs) and keep the per-type / per-script answers.</summary>
    public static readonly bool CreateProg = Row("SHEPHERD_K10_CREATEPROG");
    /// <summary>E3 seed programs: a built-in component's property bag from its value tree (Assets/SeedPrograms.cs).</summary>
    public static readonly bool SeedProg = Row("SHEPHERD_K10_SEEDPROG");
    /// <summary>E3 for Instantiate (Runtime/Cloner.cs): the property bag copied in one step, DeepCopy's per-type decision kept.</summary>
    public static readonly bool CloneProg = Row("SHEPHERD_K10_CLONEPROG");
    /// <summary>Animator binding results kept in the session (Anim/AnimData.cs Crc, Anim/AnimatorRuntime.cs BuiltinName / FieldOf / CommitOf).</summary>
    public static readonly bool AnimBind = Row("SHEPHERD_K10_ANIMBIND");
    /// <summary>E3 / asset object snapshots: stripped scripts (Unity's binary field data) filled by a binary field program (Assets/BinPrograms.cs).</summary>
    public static readonly bool BinProg = Row("SHEPHERD_K10_BINPROG");
    public static string Describe() => AllOff ? "[k10] load track: OFF (SHEPHERD_K10_LOAD=0: kit #9's loader)"
        : $"[k10] load track: field programs {(Assets.FieldPrograms.On ? (Assets.FieldPrograms.Check ? "CHECK" : "on") : "off")}, create programs {(CreateProg ? "on" : "off")}, seed programs {(SeedProg ? "on" : "off")}, clone programs {(CloneProg ? "on" : "off")}, binary field programs {(BinProg ? "on" : "off")}, animator binding memos {(AnimBind ? "on" : "off")}";
    public static string Summary()
    {
        if (AllOff) return null;
        return string.Join(" | ", new[] { Assets.FieldPrograms.Summary(), Assets.BinPrograms.Summary(), Assets.SeedPrograms.Summary(), Assets.Loader.CreateSummary(), Cloner.Summary(), Anim.AnimatorRuntime.BindSummary() }.Where(x => x != null));
    }
    static bool _started, _ended;
    /// <summary>The start line, once (Loader.Init).</summary>
    public static void Start() { if (_started) return; _started = true; Log.Info(Describe()); }
    /// <summary>The end lines, once: what each row did (+ the check mode's differences), the profile's summary (tools), the dump's close (tools).</summary>
    public static void End()
    {
        if (_ended) return; _ended = true;
        if (Summary() is { } k10) Log.Info(k10);
        if (Assets.LoadProf.Summary() is { } lp) Log.Info(lp);   // SHEPHERD_K10_LOADPROF=1 (measurement; Mac / SHEPHERD_ENGINE_TOOLS=1)
        Assets.LoadDump.Close();   // SHEPHERD_K10_LOADDUMP=<file> (test only; Mac / SHEPHERD_ENGINE_TOOLS=1)
    }
}
