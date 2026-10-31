using System.Diagnostics;
using System.Text;
using Shepherd.Assets;

namespace Shepherd.VitaPack;

/// <summary>
/// tools/VitaPack — bakes the Vita pack (V3 part 2, vita/notes/v3b-pack.md) for the objects a recorded engine boot touched
/// (SHEPHERD_PACK_RECORD) plus their PPtr closure.
///   dotnet bin/VitaPack.dll --data &lt;game Data dir&gt; --record &lt;rec.txt&gt; [--record &lt;rec2.txt&gt;] --out runtime/vitapack/&lt;name&gt;
///          [--language &lt;code&gt; (default en)] [--keep-joystick-maps] [--closure full|none] [--max 2048] [--no-verify] [--no-payload-hash] [-j N]
/// Writes &lt;out&gt;/shepherd.pack and &lt;out&gt;/report.txt. The pack is derived game data: --out must be under runtime/vitapack/.
/// The FMOD bank key never enters a pack: FMODUnity.Settings is baked with an empty EncryptionKey (Trims.cs), and every bake
/// ends with a key scan of its output (KeyScan.cs; a hit or an unreadable key fails the bake).
///   dotnet bin/VitaPack.dll --data &lt;game Data dir&gt; --scan-key &lt;dir or file&gt; [--scan-key ...]   (scan only; exit 1 = key found
///          or the scan proves nothing)
/// </summary>
static class Program
{
    public static string DataDir, OutDir; static readonly List<string> Records = new(), ScanRoots = new();
    public static string Closure = "full"; public static int MaxSide = 2048; static bool Verify = true, Bench; public static int Jobs = 4;
    /// <summary>--no-payload-hash: no payload-hash TOC property (the bake as before kit #10; vita/notes/kit10-io.md "Review fixes").</summary>
    public static bool NoPayloadHash;
    /// <summary>--language &lt;code&gt;: the one I2 language this pack carries (owner decision: single-language Vita builds;
    /// default English; the future builder lets each user pick one). --keep-joystick-maps: keep Rewired's 334 joystick maps.</summary>
    public static string Language = "en"; public static bool KeepJoystickMaps;
    static readonly StringBuilder ReportText = new();
    public static void Say(string s) { Console.WriteLine(s); lock (ReportText) ReportText.AppendLine(s); }

    /// <summary>Plain files (not serialized assets) the boot reads besides the pack, found by booting strict pack mode against a
    /// Data folder holding only these and Managed/: the engine's RuntimeInitializeOnLoads.json and the game's Addressables
    /// settings + catalog. Copied as they are to &lt;out&gt;/data/ (the device's Data folder, next to Managed/).</summary>
    public static readonly string[] RawFiles = { "RuntimeInitializeOnLoads.json", "StreamingAssets/aa/settings.json", "StreamingAssets/aa/catalog.json" };
    static void CopyRawFiles()
    {
        long total = 0;
        foreach (var rel in RawFiles)
        {
            var src = Path.Combine(DataDir, rel); var dst = Path.Combine(OutDir, "data", rel);
            if (!File.Exists(src)) { Say($"  WARNING raw file missing in the game data: {rel}"); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(dst)); File.Copy(src, dst, overwrite: true); total += new FileInfo(dst).Length;
        }
        Say($"raw files copied to data/ (the device's Data folder besides Managed/): {string.Join(", ", RawFiles)} = {total / 1048576.0:F2} MiB");
    }

    static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--repack") return Repack.Run(args);   // device kit #10 io: version 1 -> the chunked version 2
        if (args.Length >= 3 && args[0] == "--stress") return Repack.Stress(args[1], args[2], args.Length > 3 ? int.Parse(args[3]) : 120);
        if (args.Length >= 2 && args[0] == "--verify") return Repack.VerifyCli(args[1], args.Length > 2 && args[2] == "deep");
        // kit #10 io review: a shepherd.pk2 against a shepherd.pack (every chunk; stage-owner.sh STAGE_PK2) / a pack folder opened as the engine does
        if (args.Length >= 3 && args[0] == "--made-from") return Repack.MadeFromCli(args[1], args[2]);
        if (args.Length >= 2 && args[0] == "--open") return Repack.OpenCli(args[1]);
        if (args.Length >= 2 && args[0] == "--read-all") return Repack.ReadAllCli(args);
        if (args.Length >= 3 && args[0] == "--replay") return Repack.ReplayCli(args);
        for (int i = 0; i < args.Length; i++)
            switch (args[i])
            {
                case "--data": DataDir = args[++i]; break;
                case "--record": Records.Add(args[++i]); break;
                case "--out": OutDir = args[++i]; break;
                case "--closure": Closure = args[++i]; break;
                case "--max": MaxSide = int.Parse(args[++i]); break;
                case "--no-verify": Verify = false; break;
                case "--bench": Bench = true; break;
                case "-j": Jobs = int.Parse(args[++i]); break;
                case "--language": Language = args[++i]; break;
                case "--keep-joystick-maps": KeepJoystickMaps = true; break;
                case "--no-payload-hash": NoPayloadHash = true; break;
                case "--scan-key": ScanRoots.Add(args[++i]); break;
                default: Console.Error.WriteLine($"unknown argument {args[i]}"); return 2;
            }
        if (ScanRoots.Count > 0 && DataDir != null && OutDir == null && Records.Count == 0)
        {
            var r = KeyScan.Run(DataDir, ScanRoots); KeyScan.Report(r, ScanRoots);
            return r.Clean ? 0 : 1;
        }
        if (DataDir == null || OutDir == null || Records.Count == 0) { Console.Error.WriteLine("usage: VitaPack --data <Data> --record <rec> [--record ..] --out <runtime/vitapack/name> [--language en] [--keep-joystick-maps] [--closure full|none] [--max 2048] [--no-verify] [--no-payload-hash] [-j N]"); return 2; }
        OutDir = Path.GetFullPath(OutDir);
        if (!OutDir.Replace('\\', '/').Contains("/runtime/vitapack/")) { Console.Error.WriteLine($"refusing to write derived game data outside runtime/vitapack/: {OutDir}"); return 2; }
        Directory.CreateDirectory(OutDir);
        var sw = Stopwatch.StartNew();
        try
        {
            var rec = Recording.Load(Records);
            Say($"recording: {string.Join(", ", Records.Select(Path.GetFileName))}: {rec.Objects.Count} objects ({rec.Objects.Values.Count(o => o.Tree)} tree, {rec.Objects.Values.Count(o => o.Raw)} raw, {rec.Objects.Values.Count(o => o.Script)} script-header, {rec.Objects.Values.Count(o => !o.Tree && !o.Raw && !o.Script)} lookup-only) in {rec.Files.Count} files; {rec.Bundles.Count} bundles; {rec.Streams.Count} stream reads; {rec.TexUse.Count} textures used (sdf {rec.TexUse.Count(t => t.Value.Contains("sdf"))}, cpu {rec.TexUse.Count(t => t.Value.Contains("cpu"))}); absent: {rec.AbsentObjects.Count} objects, {rec.AbsentFiles.Count} files");
            var bake = new Baker(rec);
            bake.Run();
            CopyRawFiles();
            Say($"baked in {sw.Elapsed.TotalSeconds:F1} s");
            if (Verify) bake.VerifyPack();
            if (Bench) bake.BenchDecode();
            var scan = KeyScan.Run(DataDir, new[] { OutDir }); KeyScan.Report(scan, new[] { OutDir });
            if (!scan.Clean) Environment.ExitCode = 1;
            Say($"total {sw.Elapsed.TotalSeconds:F1} s");
        }
        finally { File.WriteAllText(Path.Combine(OutDir, "report.txt"), ReportText.ToString()); }
        return Environment.ExitCode;
    }
}

/// <summary>The union of one or more SHEPHERD_PACK_RECORD files (engine Assets/PackRecorder.cs).</summary>
sealed class Recording
{
    public sealed class Obj { public string File; public long PathId; public int ClassId, Frame = int.MaxValue, Seq = int.MaxValue; public bool Tree, Raw, Script, Lookup; }
    public readonly Dictionary<(string, long), Obj> Objects = new();
    public readonly List<string> Files = new();
    public readonly Dictionary<string, (string path, string[] files)> Bundles = new(StringComparer.OrdinalIgnoreCase);
    public readonly List<string> BundleOrder = new();
    public readonly HashSet<(string, long)> AbsentObjects = new();
    public readonly HashSet<string> AbsentFiles = new(StringComparer.OrdinalIgnoreCase);
    public readonly Dictionary<(string, long), HashSet<string>> TexUse = new();
    public readonly List<(string file, string path, long off, long size)> Streams = new();

    public static Recording Load(IEnumerable<string> paths)
    {
        var r = new Recording(); int seq = 0; var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in paths)
            foreach (var line in File.ReadLines(p))
            {
                if (line.Length == 0 || line[0] == '#') continue;
                var f = line.Split('\t');
                switch (f[0])
                {
                    case "O":
                    {
                        var key = (f[1], long.Parse(f[2]));
                        if (!r.Objects.TryGetValue(key, out var o)) r.Objects[key] = o = new Obj { File = f[1], PathId = key.Item2, ClassId = int.Parse(f[3]), Seq = seq++ };
                        if (files.Add(f[1])) r.Files.Add(f[1]);
                        switch (f[4]) { case "T": o.Tree = true; break; case "R": o.Raw = true; break; case "S": o.Script = true; break; default: o.Lookup = true; break; }
                        if (f.Length > 5) o.Frame = Math.Min(o.Frame, int.Parse(f[5]));
                        break;
                    }
                    case "A": r.AbsentObjects.Add((f[1], long.Parse(f[2]))); break;
                    case "X": r.AbsentFiles.Add(f[1]); break;
                    case "B":
                    {
                        var name = Path.GetFileName(f[1]);
                        if (!r.Bundles.ContainsKey(name)) { r.Bundles[name] = (f[1], f.Length > 2 && f[2].Length > 0 ? f[2].Split('|') : Array.Empty<string>()); r.BundleOrder.Add(name); }
                        break;
                    }
                    case "S": r.Streams.Add((f[1], f[2], long.Parse(f[3]), long.Parse(f[4]))); break;
                    case "T":
                    {
                        var key = (f[1], long.Parse(f[2]));
                        if (!r.TexUse.TryGetValue(key, out var s)) r.TexUse[key] = s = new HashSet<string>();
                        s.Add(f[3]); break;
                    }
                }
            }
        return r;
    }
}
