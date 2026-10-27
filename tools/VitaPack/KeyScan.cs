using System.Text;
using Shepherd.FmodBake;

namespace Shepherd.VitaPack;

/// <summary>The FMOD bank key never enters a pack (vita/PLAN.md "Hygiene"; the Vita has no FMOD runtime and needs no key).
/// Baker.TrimFmodSettings blanks the EncryptionKey of the baked FMODUnity.Settings record; this proves it: the key is read
/// IN MEMORY from the game's resources.assets with the same code tools/FmodBake uses (FmodBake/GameKey.cs, linked into this
/// assembly), and every file under the given roots is searched for its bytes (UTF-8 and UTF-16LE). The key is never printed,
/// logged, hashed or written; the report names files and hit counts only. A positive control (the same search over
/// resources.assets itself) shows that the matcher finds the key where it is.
///   dotnet bin/VitaPack.dll --data &lt;game Data dir&gt; --scan-key &lt;dir or file&gt; [--scan-key ...]
/// Every bake also scans its own output (Program: a hit fails the bake).</summary>
static class KeyScan
{
    public sealed record Outcome(int Files, long Bytes, List<(string path, int hits)> Found, string Error)
    {
        public bool Clean => Error == null && Found.Count == 0;
    }

    /// <summary>Reads the key (in memory), runs the positive control, then scans the roots. Never throws for a scan problem:
    /// an unreadable key or a failed control is an Outcome.Error (a scan that proves nothing must not read as clean).</summary>
    public static Outcome Run(string dataDir, IReadOnlyList<string> roots)
    {
        byte[][] needles;
        string how;
        try
        {
            var k = GameKey.Read(dataDir);
            if (string.IsNullOrEmpty(k.Key)) return new Outcome(0, 0, new(), "the game's FMODUnity.Settings has an empty EncryptionKey: nothing to scan for");
            needles = new[] { Encoding.UTF8.GetBytes(k.Key), Encoding.Unicode.GetBytes(k.Key) };
            how = k.How;
        }
        catch (Exception e) { return new Outcome(0, 0, new(), "the FMOD bank key could not be read from resources.assets (" + e.GetType().Name + "): the scan proves nothing"); }

        int control = Hits(Path.Combine(dataDir, "resources.assets"), needles);
        if (control <= 0) return new Outcome(0, 0, new(), "positive control failed: the key's bytes were not found in resources.assets, so the matcher cannot be trusted");
        Program.Say($"key scan: the FMOD bank key read in memory from resources.assets ({how}; never printed or stored); positive control: resources.assets holds it ({control} hit{(control == 1 ? "" : "s")})");

        var found = new List<(string, int)>(); int files = 0; long bytes = 0;
        var opt = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0, IgnoreInaccessible = false, ReturnSpecialDirectories = false };
        foreach (var root in roots)
        {
            var full = Path.GetFullPath(root);
            IEnumerable<string> list = File.Exists(full) ? new[] { full } : Directory.Exists(full) ? Directory.EnumerateFiles(full, "*", opt) : null;
            if (list == null) return new Outcome(files, bytes, found, $"scan root does not exist: {full}");
            foreach (var f in list.OrderBy(x => x, StringComparer.Ordinal))
            {
                files++; bytes += new FileInfo(f).Length;
                int h = Hits(f, needles);
                if (h > 0) found.Add((f, h));
            }
        }
        return new Outcome(files, bytes, found, null);
    }

    public static void Report(Outcome r, IReadOnlyList<string> roots)
    {
        if (r.Error != null) { Program.Say($"key scan: ERROR {r.Error}"); return; }
        Program.Say($"key scan: {r.Files} files ({r.Bytes / 1048576.0:F1} MiB) under {string.Join(", ", roots.Select(Path.GetFullPath))}: " +
            (r.Found.Count == 0 ? "0 files contain the FMOD bank key (UTF-8 or UTF-16LE) -- CLEAN" : $"{r.Found.Count} FILES CONTAIN THE FMOD BANK KEY"));
        foreach (var (p, h) in r.Found) Program.Say($"  KEY FOUND: {p} ({h} hit{(h == 1 ? "" : "s")})");
    }

    /// <summary>Occurrences of any needle in a file, streamed in 16 MiB windows that overlap by the longest needle - 1.</summary>
    static int Hits(string path, byte[][] needles)
    {
        int over = needles.Max(n => n.Length) - 1, hits = 0;
        var buf = new byte[16 << 20];
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan);
        int have = 0;
        while (true)
        {
            int n = fs.Read(buf, have, buf.Length - have);
            int len = have + n;
            bool last = n == 0;
            var span = buf.AsSpan(0, len);
            foreach (var nd in needles)
            {
                // count only matches that start before the carried-over tail (those are counted in the next window), except at the end
                int limit = last ? len : len - over, i = 0;
                while (i < limit)
                {
                    int j = span[i..].IndexOf(nd);
                    if (j < 0 || i + j >= limit) break;
                    hits++; i += j + 1;
                }
            }
            if (last) break;
            int keep = Math.Min(over, len);
            buf.AsSpan(len - keep, keep).CopyTo(buf);
            have = keep;
        }
        return hits;
    }
}
