namespace Shepherd.Engine.Runtime;

/// <summary>Device kit #9, track fidelity (vita/notes/kit9-fidelity.md): the owner's fidelity decisions of 2026-10-21 -- Unity-exact random
/// numbers ON by default, and the stand-in differences the kit #7 tests found (kit7-tests.md section 5) fixed one by one, each proven
/// against the Unity 2022.3.62f2 oracle players (vita/tests/oracle). Every part has an env-file switch back to kit #8:
/// <list type="bullet">
/// <item><c>SHEPHERD_K9_FIDELITY=0</c> -- the whole track off (= kit #8: System.Random, every stand-in as kit #8 had it);</item>
/// <item><c>SHEPHERD_UNITY_RANDOM=0</c> -- kit #8's System.Random (the kit #7 switch; <c>=1</c> still forces Unity's generator, also with the
/// track off; Icalls/UnityRandom.cs);</item>
/// <item><c>SHEPHERD_K9_STANDIN=0</c> -- every stand-in fix off; <c>SHEPHERD_K9_STANDIN_OFF=&lt;name&gt;[,...]</c> -- the named fixes off (names:
/// <see cref="Names"/>).</item>
/// </list>
/// Vita profile only (the Mac's non-Vita runs and their recorded baselines keep kit #8's behaviour). Read once at first use; the switch
/// names stay out of the V3 diet's readable-key scan ([ProfileOnly]).</summary>
[ProfileOnly]
public static class K9Fidelity
{
    /// <summary>The track's master switch (on by default under the Vita profile).</summary>
    public static readonly bool On = Profile.Vita && Environment.GetEnvironmentVariable("SHEPHERD_K9_FIDELITY") != "0";
    /// <summary>Unity's own xorshift128 generator for UnityEngine.Random: forced by SHEPHERD_UNITY_RANDOM=1, refused by =0, else the track's default.</summary>
    public static readonly bool UnityRandom = Environment.GetEnvironmentVariable("SHEPHERD_UNITY_RANDOM") is var ur && (ur == "1" || (ur != "0" && On));
    static readonly bool Standin = On && Environment.GetEnvironmentVariable("SHEPHERD_K9_STANDIN") != "0";
    static readonly string[] Off = (Environment.GetEnvironmentVariable("SHEPHERD_K9_STANDIN_OFF") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    /// <summary>The stand-in fixes by name (each one's switch-off name); the summary line lists them.</summary>
    public static readonly string[] Names =
    {
        "canvasgroup", "canvas", "propblock", "mesh", "pow2", "layer", "find", "destroyedname", "siblingindex", "curve",
    };
    // each fix's flag, read once (after Names / Off / Standin above: static initialisers run in text order); the literals live here, in a
    // [ProfileOnly] class, so they never join the V3 diet's readable keys
    public static readonly bool FixCanvasGroup = Fix("canvasgroup"), FixCanvas = Fix("canvas"), FixPropBlock = Fix("propblock"), FixMesh = Fix("mesh"),
        FixPow2 = Fix("pow2"), FixLayer = Fix("layer"), FixFind = Fix("find"), FixDestroyedName = Fix("destroyedname"), FixSiblingIndex = Fix("siblingindex"),
        FixCurve = Fix("curve");
    /// <summary>Is the named stand-in fix on?</summary>
    public static bool Fix(string name) { if (!Standin) return false; foreach (var o in Off) if (o == name) return false; return true; }

    /// <summary>Boot.Run's start line: what this track turned on.</summary>
    public static string Describe()
    {
        var sb = new System.Text.StringBuilder("[k9] fidelity: ");
        if (!Profile.Vita) return sb.Append("off (not the Vita profile: kit #8's behaviour)").ToString();
        sb.Append(On ? "on" : "OFF (SHEPHERD_K9_FIDELITY=0: kit #8)").Append("; UnityEngine.Random = ")
          .Append(UnityRandom ? "Unity's xorshift128 (SHEPHERD_UNITY_RANDOM=0: kit #8's System.Random)" : "kit #8's System.Random").Append("; stand-in fixes: ");
        int n = 0;
        foreach (var name in Names) if (Fix(name)) { sb.Append(n++ == 0 ? "" : ",").Append(name); }
        if (n == 0) sb.Append("none");
        else sb.Append(" (SHEPHERD_K9_STANDIN=0 / SHEPHERD_K9_STANDIN_OFF=<name>,...: kit #8's)");
        return sb.ToString();
    }
}
