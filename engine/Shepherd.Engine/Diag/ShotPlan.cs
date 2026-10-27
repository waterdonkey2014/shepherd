namespace Shepherd.Engine.Diag;

/// <summary>Stage-2 review (vita/notes/engine-pass-speed.md §6): extra boot screenshots at listed frames, on top of the regular
/// "boot &lt;frames&gt; &lt;every&gt;" interval, so identity checks can look densely at the frames around input presses (where a press handled
/// one frame later shows up). SHEPHERD_SHOT_FRAMES="a-b,c,d-e" (inclusive ranges, absolute frames). No-op when unset.</summary>
[ProfileOnly]
public static class ShotPlan
{
    static readonly HashSet<int> Frames = Parse(Environment.GetEnvironmentVariable("SHEPHERD_SHOT_FRAMES"));
    static HashSet<int> Parse(string s)
    {
        var set = new HashSet<int>();
        foreach (var part in (s ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var ab = part.Split('-');
            if (ab.Length == 1 && int.TryParse(ab[0], out var f)) set.Add(f);
            else if (ab.Length == 2 && int.TryParse(ab[0], out var a) && int.TryParse(ab[1], out var b)) for (int i = a; i <= b; i++) set.Add(i);
        }
        return set;
    }
    public static bool Extra(int frame) => Frames.Count > 0 && Frames.Contains(frame);
    /// <summary>Stage B: SHEPHERD_SHOT_DIR=&lt;dir&gt; puts the boot screenshots there instead of &lt;root&gt;/runtime/shots/boot (the Vita GL host:
    /// ux0:data/shepherd/shots/, written on request only: SHEPHERD_SHOT_FRAMES or a "boot N every" interval in args.txt).</summary>
    public static string Dir(string fallback) => Environment.GetEnvironmentVariable("SHEPHERD_SHOT_DIR") is { Length: > 0 } d ? d : fallback;
}
