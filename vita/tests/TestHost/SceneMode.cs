using System.Runtime.CompilerServices;
using System.Text;
namespace Shepherd.TestHost;

/// <summary>Kit #7 tests track: `scene &lt;names|all&gt; &lt;out dir&gt; [unity dir]` -- the one-feature render scenes (probes/Scenes.cs) on
/// Shepherd's engine into the recording backend (RefRaster.cs): per scene &lt;tag&gt;.draws.tsv (the sorted draw list) and
/// &lt;tag&gt;.shepherd.rgba (the reference renderer's frame); with a Unity dir, each frame compared with &lt;tag&gt;.unity.rgba pixel for pixel.</summary>
public static class SceneMode
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static int Run(string[] args)
    {
        string names = args[1], outDir = args[2], unity = args.Length > 3 ? args[3] : null;
        Directory.CreateDirectory(outDir);
        var list = new List<string>();
        foreach (var n in (names == "all" ? string.Join(",", ShepherdOracle.Scenes.All) : names).Split(','))
            if (n.Contains(':')) list.Add(n); else for (int s = 1; s <= 4; s++) list.Add(n + ":" + s);
        Shepherd.Engine.Runtime.Time.CaptureDeltaTime = 1f / 60f;
        var rec = new RecordingBackend(ShepherdOracle.Scenes.W, ShepherdOracle.Scenes.H);
        Shepherd.Engine.Render.SceneRenderer.G = rec;
        Shepherd.Engine.Runtime.Lifecycle.Render = Shepherd.Engine.Render.SceneRenderer.Frame;
        int bad = 0, compared = 0; var summary = new StringBuilder();
        foreach (var spec in list)
        {
            var p = spec.Split(':'); var made = new List<UnityEngine.GameObject>(); var slog = new StringBuilder();
            ShepherdOracle.Scenes.Build(p[0], int.Parse(p[1]), made, slog);
            for (int i = 0; i < 3; i++) Shepherd.Engine.Runtime.Lifecycle.Frame();
            string tag = p[0] + "-" + p[1];
            File.WriteAllText(Path.Combine(outDir, tag + ".draws.tsv"), rec.DrawList(rec.LastFrame));
            File.WriteAllText(Path.Combine(outDir, tag + ".scene.shepherd.txt"), slog.ToString());
            var fb = rec.Raster(rec.LastFrame); File.WriteAllBytes(Path.Combine(outDir, tag + ".shepherd.rgba"), fb);
            string verdict = $"{tag}: {rec.LastFrame.Count} draws";
            var up = unity == null ? null : Path.Combine(unity, tag + ".unity.rgba");
            if (up != null && File.Exists(up))
            {
                var u = File.ReadAllBytes(up); compared++;
                int diff = 0, first = -1; for (int i = 0; i + 3 < Math.Min(u.Length, fb.Length); i += 4) if (u[i] != fb[i] || u[i + 1] != fb[i + 1] || u[i + 2] != fb[i + 2]) { diff++; if (first < 0) first = i / 4; }
                if (diff > 0) { bad++; int x = first % ShepherdOracle.Scenes.W, y = first / ShepherdOracle.Scenes.W; verdict += $"; {diff} of {fb.Length / 4} pixels differ from Unity (first at {x},{y}: Shepherd {fb[first * 4]},{fb[first * 4 + 1]},{fb[first * 4 + 2]} Unity {u[first * 4]},{u[first * 4 + 1]},{u[first * 4 + 2]})"; }
                else verdict += "; every pixel = Unity's";
                if (File.Exists(Path.Combine(unity, tag + ".scene.txt")) && File.ReadAllText(Path.Combine(unity, tag + ".scene.txt")) != slog.ToString()) verdict += " (WARNING: the two sides built different scenes)";
            }
            summary.Append(verdict).Append('\n'); Console.WriteLine(verdict);
            foreach (var g in made) if (g != null) UnityEngine.Object.DestroyImmediate(g);
            Shepherd.Engine.Runtime.Lifecycle.Frame();
        }
        summary.Append($"RESULT: {compared} scenes compared with Unity, {bad} differ\n");
        File.WriteAllText(Path.Combine(outDir, "scenes.txt"), summary.ToString());
        return bad == 0 ? 0 : 1;
    }
}
