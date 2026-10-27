using System.Reflection;
using System.Security.Cryptography;
using Shepherd.Engine.Native;

namespace Shepherd.Engine.Diag;

#if !SHEPHERD_EBOOT
/// <summary>kit5 mem, lever L0 (diagnostic only, off unless set): SHEPHERD_SPINEDUP=&lt;frame&gt;[,&lt;frame&gt;...] -- after those frames, every live
/// Spine SkeletonDataAsset (found by type short name, read by reflection: no library-specific code is compiled in) is listed with its source
/// file / path id, whether it has built its SkeletonData (IsLoaded), its skeleton file (TextAsset: source, length, SHA-256), scale, atlas
/// assets and modifiers; assets with the same skeleton bytes and scale are grouped. It answers "why are there two 'Follower' SkeletonData
/// copies" (vita/notes/kit4-intro-plan.md section 0, L0): two assets of one name in two files, both built by the game, or one asset built twice.</summary>
[ProfileOnly]
public static class SpineDup
{
    static readonly int[] At = (Environment.GetEnvironmentVariable("SHEPHERD_SPINEDUP") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries)
        .Select(x => int.TryParse(x, out var v) ? v : -1).Where(v => v > 0).ToArray();

    public static void After(int frame)
    {
        if (At.Length == 0 || Array.IndexOf(At, frame) < 0) return;
        try { Run(frame); } catch (Exception e) { Log.Exception(e, "SpineDup"); }
    }

    static string Src(object u) => Objects.Get(u) is { } r ? $"{(r.SourceFile as Shepherd.Assets.SerializedFile)?.Name ?? "(runtime)"}#{r.PathId}" : "(no record)";
    static object F(object o, string name)
    {
        for (var t = o.GetType(); t != null; t = t.BaseType)
            if (t.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly) is { } fi) return fi.GetValue(o);
        return null;
    }

    static void Run(int frame)
    {
        var rows = new List<(string key, string line, bool loaded, long len)>();
        foreach (var n in Objects.All.ToArray())
        {
            var m = n.Managed; if (m == null || m.GetType().Name != "SkeletonDataAsset") continue;
            bool loaded = m.GetType().GetProperty("IsLoaded")?.GetValue(m) is true;
            var json = F(m, "skeletonJSON") as UnityEngine.TextAsset;
            string jsrc = "null", hash = "-"; long len = 0;
            if (json != null)
            {
                var b = json.bytes; len = b.Length;
                hash = Convert.ToHexString(SHA256.HashData(b))[..16].ToLowerInvariant();
                jsrc = $"'{json.name}'@{Src(json)} {len:N0} B sha256 {hash}";
            }
            var scale = F(m, "scale");
            var atlases = F(m, "atlasAssets") is Array aa ? string.Join(",", aa.Cast<object>().Select(a => a is UnityEngine.Object ua ? $"'{ua.name}'@{Src(ua)}" : "null")) : "-";
            var mods = F(m, "skeletonDataModifiers") is System.Collections.ICollection mc ? mc.Count : -1;
            rows.Add(($"{hash}|{scale}", $"'{n.Name}'@{Src(m)} loaded={loaded} json={jsrc} scale={scale} atlases=[{atlases}] modifiers={mods}", loaded, len));
        }
        Log.Info($"[spinedup] frame {frame}: {rows.Count} SkeletonDataAsset objects, {rows.Count(r => r.loaded)} with a built SkeletonData");
        foreach (var g in rows.GroupBy(r => r.key).OrderByDescending(g => g.Count()).ThenByDescending(g => g.First().len))
        {
            if (g.Count() < 2) continue;
            Log.Info($"[spinedup]   {g.Count()} assets share skeleton bytes + scale ({g.Count(r => r.loaded)} built, {g.First().len:N0} B each):");
            foreach (var r in g) Log.Info($"[spinedup]     {r.line}");
        }
        foreach (var r in rows.Where(r => r.loaded).OrderByDescending(r => r.len).Take(60)) Log.Info($"[spinedup]   built: {r.line}");
    }
}
#else
/// <summary>kit5 mem, lever L0 diagnostic (off by default). kit #5 integration (vita/notes/kit5-integration.md "Eboot room"): not in the eboot (SHEPHERD_EBOOT); a build with SHEPHERD_ENGINE_TOOLS=1 keeps it. The Mac and vita-sim builds are unchanged.</summary>
[ProfileOnly]
public static class SpineDup
{
    public static void After(int frame)
    {
        if (frame == 1 && Environment.GetEnvironmentVariable("SHEPHERD_SPINEDUP") is { Length: > 0 }) Log.Warn("[spinedup] SHEPHERD_SPINEDUP ignored: the L0 diagnostic is not in this eboot (SHEPHERD_ENGINE_TOOLS=1 builds keep it)");
    }
}
#endif
