using System.Text;
using System.Text.RegularExpressions;
using Shepherd.Assets;

namespace Shepherd.FmodBake;

/// <summary>
/// The "menu" subset = what the boot, the Main Menu and the first scene (Game Biome Intro) can play:
///  (1) serialized references: every object of the boot's loose files and of both scenes' bundle closures is scanned for
///      event GUIDs (FMOD.GUID = 4 ints, as Unity serializes EventReference.Guid) and for "event:/" / "snapshot:/" strings;
///  (2) string literals in the boot / menu / intro code of the decomp (AudioManager, MainMenu*, LoadMainMenu, UINavigator,
///      MMTransition, Intro*), never edited, only read;
///  (3) path families those scenes drive from code: ui/, player/, weapon/, material/, music/menu|intro, atmos/intro.
/// Writes runtime/audiobake/subset-menu.txt (path TAB sources).
/// </summary>
static class Subset
{
    static readonly string[] Scenes = { "Assets/Scenes/Main Menu.unity", "Assets/Scenes/Game Biome Intro.unity" };
    static readonly string[] Loose = { "level0", "sharedassets0.assets", "resources.assets", "globalgamemanagers.assets" };
    static readonly Regex CodeFiles = new(@"(^|/)(AudioManager|LoadMainMenu|LoadMenu|MainMenu\w*|UIMainMenu\w*|UINavigator|MMTransition|Intro\w*|UIHeartsIntro|Interaction_Intro\w*|SplashScreen\w*|UIMenuBase|UIManager)\.cs$");
    static readonly string[] Families = { "event:/ui/", "event:/player/", "event:/weapon/", "event:/material/", "event:/music/menu/", "event:/music/intro/", "event:/atmos/intro/" };

    public static int Run(string[] a)
    {
        using var s = Program.OpenSession(out var events, out _, out _);
        var byGuid = new Dictionary<(ulong, ulong), EventInfo>();
        var byPath = events.ToDictionary(e => e.Path, StringComparer.Ordinal);
        foreach (var e in events)
        {
            var g = Guid.Parse(e.Guid).ToByteArray();
            byGuid[(BitConverter.ToUInt64(g, 0), BitConverter.ToUInt64(g, 8))] = e;
        }
        var src = new SortedDictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        void Add(string path, string why) { if (!byPath.ContainsKey(path)) return; if (!src.TryGetValue(path, out var l)) src[path] = l = new SortedSet<string>(); l.Add(why); }

        // (1) serialized references: PPtr reachability from the scenes (what Unity would load with them), every reached
        //     object scanned for event GUIDs and path strings
        using (var db = new AssetDatabase(Program.Data))
        {
            var q = new Queue<(string label, SerializedFile f, ObjectInfo o)>();
            var seen = new HashSet<(string, long)>();
            void Seed(string label, SerializedFile f) { if (f == null) return; foreach (var o in f.Objects) if (seen.Add((f.Name, o.PathId))) q.Enqueue((label, f, o)); }
            Seed("boot", db.GetFile("level0"));
            Seed("boot", db.GetFile("sharedassets0.assets"));
            foreach (var sc in Scenes)
            {
                try
                {
                    var sl = db.LoadScene(sc, eager: true);
                    string label = sc.Contains("Main Menu") ? "mainmenu" : "intro";
                    Seed(label, sl.Scene); Seed(label, sl.SharedAssets);
                }
                catch (Exception e) { Program.Log($"subset: scene {sc}: {e.Message}"); }
            }
            int scanned = 0; long bytes = 0;
            var pp = new List<PPtr>();
            while (q.Count > 0)
            {
                var (label, f, o) = q.Dequeue();
                ReadOnlySpan<byte> d;
                try { d = f.GetObjectBytes(o); } catch { continue; }
                scanned++; bytes += d.Length;
                for (int i = 0; i + 16 <= d.Length; i += 4)
                {
                    var k = (BitConverter.ToUInt64(d.Slice(i, 8)), BitConverter.ToUInt64(d.Slice(i + 8, 8)));
                    if (k.Item1 == 0 && k.Item2 == 0) continue;
                    if (byGuid.TryGetValue(k, out var e)) Add(e.Path, $"{label}:guid");
                }
                foreach (var str in Strings(d)) Add(str, $"{label}:string");
                if (f.GetTypeTree(o) == null) continue;
                TypeTreeObject t;
                try { t = TypeTreeReader.Read(f, o); } catch { continue; }
                pp.Clear(); TypeTreeReader.CollectPPtrs(t, pp);
                foreach (var p in pp)
                {
                    if (p.IsNull || p.PathId == 0) continue;
                    var (tf, to) = db.Resolve(f, p);
                    if (!to.IsNull && seen.Add((tf.Name, to.PathId))) q.Enqueue((label, tf, to));
                }
            }
            Program.Log($"subset: reached + scanned {scanned} objects, {bytes / 1048576.0:F1} MiB");
        }

        // (2) code literals
        string decomp = Path.Combine(Program.Repo, "decomp", "Assembly-CSharp");
        var lit = new Regex("\"((event|snapshot):/[^\"]+)\"");
        int codeFiles = 0;
        foreach (var file in Directory.EnumerateFiles(decomp, "*.cs", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(decomp, file).Replace('\\', '/');
            if (!CodeFiles.IsMatch(rel)) continue;
            codeFiles++;
            foreach (Match m in lit.Matches(File.ReadAllText(file))) Add(m.Groups[1].Value, "code:" + Path.GetFileNameWithoutExtension(rel));
        }
        // (3) families
        foreach (var e in events) foreach (var fam in Families) if (e.Path.StartsWith(fam, StringComparison.Ordinal)) Add(e.Path, "family:" + fam[7..^1]);

        string outp = Path.Combine(Program.OutRoot, "subset-menu.txt");
        Directory.CreateDirectory(Program.OutRoot);
        File.WriteAllLines(outp, src.Select(kv => kv.Key + "\t" + string.Join(",", kv.Value)));
        Program.Log($"subset: {src.Count} paths ({src.Count(kv => kv.Key.StartsWith("snapshot:/"))} snapshots) from {codeFiles} code files -> {outp}");
        foreach (var grp in src.SelectMany(kv => kv.Value.Select(v => v.Split(':')[0])).GroupBy(x => x)) Program.Log($"  source {grp.Key}: {grp.Count()} refs");
        return 0;
    }

    /// <summary>ASCII runs starting with event:/ or snapshot:/ inside a byte span.</summary>
    static IEnumerable<string> Strings(ReadOnlySpan<byte> d)
    {
        var o = new List<string>();
        foreach (var pfx in new[] { "event:/"u8.ToArray(), "snapshot:/"u8.ToArray() })
        {
            int i = 0;
            while (i < d.Length)
            {
                int j = d[i..].IndexOf(pfx);
                if (j < 0) break;
                int st = i + j, e = st;
                int len = st >= 4 ? BitConverter.ToInt32(d.Slice(st - 4, 4)) : -1;   // Unity strings are length-prefixed
                if (len >= pfx.Length && len <= 512 && st + len <= d.Length) e = st + len;
                else while (e < d.Length && d[e] >= 0x20 && d[e] < 0x7f) e++;
                o.Add(Encoding.ASCII.GetString(d[st..e]));
                i = e + 1;
            }
        }
        return o;
    }
}
