using System.Globalization;
using System.Text.RegularExpressions;
using Shepherd.Assets;

/// <summary>
/// kit #10 fidelity, STRETCH: the physics composite oracle re-made for the install as it is (physcheck's CompositeOracle reads its cases --
/// "=== &lt;bundle&gt; &lt;CAB&gt; #&lt;path id&gt; ..." lines -- from SHEPHERD_PHYSCHECK_COMPOSITE and the data from the install; the owner's install
/// moved to 1.5.26 on 2026-10-25 and the old list's bundles / CABs / path ids no longer hold the four composites).
///   CompoScan &lt;game Data dir&gt; &lt;old oracle list&gt; &lt;new list out&gt;
/// Every CompositeCollider2D (class 66) with input paths and Unity's baked output in every bundle gets its signature: geometry type, vertex and
/// offset distance, the point count of every input path (m_ColliderPaths, in order) and of every output path (m_CompositePaths). Each old case's
/// signature (parsed from its === line) is looked up; a case found exactly once is written to the new list as "=== bundle CAB #pathid ..."
/// (physcheck then compares Unity's own output with Shepherd's pipeline on those objects); several with the signature, one of them at the old
/// bundle / CAB / path id = that one; none / several otherwise = reported, not written.
/// </summary>
static class P
{
    static int Main(string[] a)
    {
        if (a.Length < 3) { Console.Error.WriteLine("usage: CompoScan <Data> <old list> <new list>"); return 2; }
        var want = new List<(string line, string sig)>();
        foreach (var l in File.ReadLines(a[1]))
        {
            var m = Regex.Match(l, @"^=== \S+ \S+ #-?\d+ (geom=\S+ vd=\S+ od=\S+ in=\d+ paths \([\d,]+ pts\) unity out=\d+ \([\d,]+ pts\))");
            if (m.Success) want.Add((l, m.Groups[1].Value));
        }
        var db = new AssetDatabase(a[0]);
        var found = new Dictionary<string, List<string>>();
        int composites = 0, bundles = 0;
        foreach (var bp in Directory.GetFiles(db.BundleDir, "*.bundle").OrderBy(x => x, StringComparer.Ordinal))
        {
            bundles++;
            IReadOnlyList<SerializedFile> files;
            try { files = db.LoadBundleFiles(bp); } catch { continue; }
            foreach (var f in files)
                foreach (var o in f.ObjectsOfClass(66))
                {
                    TypeTreeObject t;
                    try { t = TypeTreeReader.Read(f, o); } catch { continue; }
                    var subs = t["m_ColliderPaths"] as List<object>;
                    var outp = (t["m_CompositePaths"] as TypeTreeObject)?["m_Paths"] as List<object>;
                    if (subs == null || outp == null || subs.Count == 0 || outp.Count == 0) continue;
                    var ins = new List<int>();
                    foreach (TypeTreeObject sub in subs) foreach (List<object> path in (List<object>)sub["m_ColliderPaths"]) ins.Add(path.Count);
                    var outs = outp.Cast<List<object>>().Select(p => p.Count).ToList();
                    string sig = string.Create(CultureInfo.InvariantCulture,
                        $"geom={t["m_GeometryType"]} vd={t["m_VertexDistance"]} od={t["m_OffsetDistance"]} in={ins.Count} paths ({string.Join(",", ins)} pts) unity out={outs.Count} ({string.Join(",", outs)} pts)");
                    composites++;
                    string head = $"=== {Path.GetFileName(bp)} {f.Name} #{o.PathId} {sig}";
                    if (!found.TryGetValue(sig, out var lst)) found[sig] = lst = new List<string>();
                    lst.Add(head);
                }
            db.ReleaseIdle(long.MaxValue);
        }
        Console.WriteLine($"scanned {bundles} bundles: {composites} composites with inputs and Unity's output");
        var outLines = new List<string> { $"# kit #10 fidelity CompoScan: the composite oracle re-made by signature for the install at {a[0]} from {a[1]} -- {DateTime.Now:yyyy-MM-dd HH:mm}" };
        int ok = 0;
        foreach (var (line, sig) in want)
        {
            var hits = found.GetValueOrDefault(sig) ?? new List<string>();
            var same = hits.Where(h => h.Split(' ').Take(4).SequenceEqual(line.Split(' ').Take(4))).ToList();
            if (hits.Count > 1 && same.Count == 1) hits = same;   // several with the signature, one of them where the old list had it: that one
            Console.WriteLine($"{(hits.Count == 1 ? "FOUND" : hits.Count == 0 ? "MISSING" : "AMBIGUOUS x" + hits.Count)} {sig}\n  was: {line.Substring(0, Math.Min(line.Length, 140))}" +
                              string.Concat(hits.Take(4).Select(h => "\n  now: " + h.Substring(0, Math.Min(h.Length, 140)))));
            if (hits.Count == 1) { outLines.Add(hits[0]); ok++; }
        }
        outLines.Add($"cases={ok}");
        File.WriteAllLines(a[2], outLines);
        Console.WriteLine($"{ok} of {want.Count} cases found once -> {a[2]}");
        return ok == want.Count ? 0 : 1;
    }
}
