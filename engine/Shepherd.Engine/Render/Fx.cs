namespace Shepherd.Engine.Render;

/// <summary>Fidelity round 1: switches for the fidelity features, for A/B runs and a battery-saver profile. SHEPHERD_FX is a list of
/// tokens separated by commas or spaces: <c>no&lt;feature&gt;</c> turns one feature off (nospriteshape, nolighting, nofog, nolut, nomask, novignette, noglow),
/// <c>off</c> turns them all off. Unset: every verified feature on.</summary>
[ProfileOnly]
public static class Fx
{
    static readonly HashSet<string> Tokens = new((Environment.GetEnvironmentVariable("SHEPHERD_FX") ?? "").ToLowerInvariant()
        .Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    /// <summary>True when SHEPHERD_FX turns <paramref name="feature"/> off ("no" + feature, or "off").</summary>
    public static bool Off(string feature) => Tokens.Contains("off") || Tokens.Contains("no" + feature);
    public static string Describe() => Tokens.Count == 0 ? "all on" : string.Join(",", Tokens);
}
