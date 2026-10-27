using Shepherd.Assets;
using Shepherd.Engine.Native;
namespace Shepherd.Engine.Assets;

/// <summary>V3 part 2: <c>SHEPHERD_PACK_RECORD=&lt;file&gt;</c> records every file and object the asset layer touches during a boot
/// (first occurrence of each, in order), so tools/VitaPack can bake exactly that set plus its PPtr closure. One line per event,
/// tab-separated:
///   O file pathId classId access(T tree / R raw / S script header / L lookup only) frame
///   A file pathId                    (a lookup that found nothing: the PPtr target does not exist)
///   X name                           (a file that exists nowhere)
///   B bundlePath file1|file2|...     (a bundle's serialized files were loaded)
///   S file streamPath offset size    (streamed .resS/.resource data was read)
///   T file pathId use(gpu / sdf / cpu) frame   (a texture was uploaded, drawn as a distance field, or read on the CPU)
/// Unset: nothing is installed. The recording is derived data: it stays under runtime/ (gitignored).</summary>
[ProfileOnly]
public static class PackRecorder
{
    public static readonly string PathName = Environment.GetEnvironmentVariable("SHEPHERD_PACK_RECORD");
    public static readonly bool On = !string.IsNullOrEmpty(PathName);
    /// <summary>Set by SceneRenderer while it fetches pixels for a GPU upload (so CPU readers can be told apart).</summary>
    public static bool InUpload;
    static StreamWriter _w;
    static readonly HashSet<string> Seen = new(StringComparer.Ordinal);
    static readonly object Gate = new();
    public static int Lines;

    public static void Install()
    {
        if (!On || _w != null) return;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(PathName)));
        _w = new StreamWriter(PathName, false) { AutoFlush = false };
        _w.WriteLine("# Shepherd boot recording (tools/VitaPack input); derived game metadata, never commit/share");
        AssetTrace.ObjectRead = (f, o, a) => Line($"O\t{f.Name}\t{o.PathId}\t{o.ClassId}\t{(a == AssetTrace.Access.Tree ? 'T' : a == AssetTrace.Access.Script ? 'S' : 'R')}", true);
        AssetTrace.ObjectLookup = (f, o) => Line($"O\t{f.Name}\t{o.PathId}\t{o.ClassId}\tL", true);
        AssetTrace.ObjectAbsent = (f, pid) => Line($"A\t{f.Name}\t{pid}", false);
        AssetTrace.FileAbsent = n => Line($"X\t{n}", false);
        AssetTrace.StreamRead = (f, p, off, size) => Line($"S\t{f?.Name}\t{p}\t{off}\t{size}", false);
        AssetTrace.BundleLoaded = (b, files) => Line($"B\t{b}\t{string.Join("|", files.Select(x => x.Name))}", false);
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Flush();
        Log.Info($"[pack-record] recording asset accesses -> {PathName}");
    }

    /// <summary>Texture use: 0 = GPU upload, 1 = drawn as an SDF atlas, 2 = CPU pixel read (Stylizer LUT/pattern, GetPixels).</summary>
    public static void Texture(NTexture t, int use)
    {
        if (!On || t?.SourceFile is not SerializedFile f) return;
        Line($"T\t{f.Name}\t{t.PathId}\t{(use == 1 ? "sdf" : use == 2 ? "cpu" : "gpu")}", true);
    }

    static void Line(string key, bool frame)
    {
        lock (Gate)
        {
            if (_w == null || !Seen.Add(key)) return;
            _w.Write(key); if (frame) { _w.Write('\t'); _w.Write(Runtime.Time.FrameCount); }
            _w.Write('\n'); Lines++;
        }
    }

    public static void Flush() { lock (Gate) _w?.Flush(); }
    public static void Close() { lock (Gate) { if (_w == null) return; _w.Flush(); _w.Dispose(); _w = null; Log.Info($"[pack-record] {Lines} records -> {PathName}"); } }
}
