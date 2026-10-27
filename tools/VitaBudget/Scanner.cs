using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Text;
using Shepherd.Assets;

namespace VitaBudget;

/// <summary>Per-bundle survey: object bytes by class/category, streamed (.resS/.resource) bytes attributed to their owners,
/// and detail records for textures, meshes, audio, shaders, materials and MonoScripts. READ-ONLY.</summary>
public static class Scanner
{
    public static string CategoryOf(int classId) => classId switch
    {
        28 or 213 or 687078895 or 89 or 117 or 187 or 84 or 214 => "textures",
        83 or 240 or 241 or 243 or 244 or 245 or 272 or 273 => "audio",
        43 => "meshes",
        74 or 91 or 221 or 90 or 319 or 93 or 1101 or 1102 or 1107 or 1109 or 206 => "animation",
        48 or 72 or 200 => "shaders",
        128 => "fonts",
        329 => "video",
        _ => "other",
    };

    static void Add(Dictionary<string, long> d, string k, long v) { d[k] = d.TryGetValue(k, out var o) ? o + v : v; }
    static void Add2(Dictionary<string, long[]> d, string k, long v)
    {
        if (!d.TryGetValue(k, out var a)) d[k] = a = new long[2];
        a[0]++; a[1] += v;
    }

    /// <summary>m_Name of a NamedObject without a type-tree read (int32 length + UTF-8).</summary>
    public static string QuickName(SerializedFile f, ObjectInfo o)
    {
        var d = f.GetObjectBytes(o);
        if (d.Length < 4) return null;
        int len = f.BigEndianData ? BinaryPrimitives.ReadInt32BigEndian(d) : BinaryPrimitives.ReadInt32LittleEndian(d);
        if (len < 0 || len > 4096 || 4 + len > d.Length) return null;
        return Encoding.UTF8.GetString(d.Slice(4, len));
    }

    public static string ExtName(SerializedFile sf, int fid) =>
        fid == 0 ? sf.Name : fid - 1 < sf.Externals.Count && fid > 0 ? sf.Externals[fid - 1].FileName : $"?ext{fid}";

    public static BundleRec SurveyBundle(string path)
    {
        var rec = new BundleRec { Name = Path.GetFileName(path), Disk = new FileInfo(path).Length };
        try
        {
            using var b = BundleFile.Open(path);
            foreach (var kv in b.BlockCompressionHistogram) rec.Comp[kv.Key.ToString()] = kv.Value;
            var names = new HashSet<string>(b.Nodes.Select(n => n.Name), StringComparer.OrdinalIgnoreCase);
            foreach (var n in b.Nodes)
            {
                rec.Uncomp += n.Size;
                if (n.IsSerializedFile) continue;
                if (n.Name.EndsWith(".resS", StringComparison.OrdinalIgnoreCase)) rec.ResS += n.Size;
                else if (n.Name.EndsWith(".resource", StringComparison.OrdinalIgnoreCase)) rec.Resource += n.Size;
                else rec.OtherNodes += n.Size;
            }
            foreach (var n in b.Nodes.Where(n => n.IsSerializedFile))
            {
                bool isScene = names.Contains(n.Name + ".sharedAssets");
                if (isScene) rec.SceneFiles.Add(n.Name);
                rec.Cabs.Add(n.Name);
                rec.SerBytes += n.Size;
                var sf = SerializedFile.Load(n.Name, b.ReadNode(n.Name));
                SurveyFile(sf, isScene, rec);
            }
            long streamNodes = rec.ResS + rec.Resource;
            if (streamNodes > rec.StreamAttrib) Add(rec.Cat, "streamed-unattributed", streamNodes - rec.StreamAttrib);
        }
        catch (Exception e) { rec.Err = e.Message; }
        return rec;
    }

    /// <summary>Survey one serialized file (bundle node or loose file). Streamed bytes are attributed by the owners' m_StreamData.</summary>
    public static void SurveyFile(SerializedFile sf, bool isScene, BundleRec rec)
    {
        foreach (var o in sf.Objects) Account(sf, o, isScene, rec);
    }

    /// <summary>Account one object: class/category bytes + detail record (+ attributed streamed bytes).</summary>
    public static void Account(SerializedFile sf, ObjectInfo o, bool isScene, BundleRec rec)
    {
        {
            string cls = ClassIds.Name(o.ClassId);
            Add2(rec.Cls, cls, o.ByteSize);
            if (isScene) Add(rec.Cat, "scenes", o.ByteSize);
            else if (o.ClassId == ClassIds.MonoBehaviour)
            {
                var sp = AssetDatabase.ReadMonoBehaviourScriptPPtr(sf, o);
                Add2(rec.Mb, $"{ExtName(sf, sp.FileId)}:{sp.PathId}", o.ByteSize);
            }
            else if (o.ClassId == 49) // TextAsset: Spine skeleton/atlas text counts as animation
            {
                string nm = QuickName(sf, o) ?? "";
                bool spine = nm.EndsWith(".skel", StringComparison.OrdinalIgnoreCase) || nm.EndsWith(".atlas", StringComparison.OrdinalIgnoreCase)
                             || nm.EndsWith("_Atlas", StringComparison.Ordinal) || nm.Contains(".skel.", StringComparison.OrdinalIgnoreCase);
                Add(rec.Cat, spine ? "animation" : "other", o.ByteSize);
            }
            else Add(rec.Cat, CategoryOf(o.ClassId), o.ByteSize);

            try { Detail(sf, o, rec); }
            catch { rec.DetailErrors++; }
        }
    }

    static void Detail(SerializedFile sf, ObjectInfo o, BundleRec rec)
    {
        switch (o.ClassId)
        {
            case 28: case 89: case 117: case 187:
            {
                if (sf.GetTypeTree(o) == null) return;
                var t = TypeTreeReader.Read(sf, o);
                long st = t.Obj("m_StreamData")?.Long("size") ?? 0;
                var tr = new TexRec
                {
                    B = rec.Name, Cab = sf.Name, Pid = o.PathId, Cls = o.ClassId, Name = t.Str("m_Name"),
                    W = t.Int("m_Width"), H = t.Int("m_Height"), Fmt = t.Int("m_TextureFormat"), Mips = t.Int("m_MipCount"),
                    Dim = t.Int("m_TextureDimension"), Images = Math.Max(1, t.Int("m_ImageCount")),
                    Inline = t.Bytes("image data")?.Length ?? 0, Stream = st, Obj = o.ByteSize, Readable = t.Bool("m_IsReadable"),
                };
                if (o.ClassId == 117) tr.Images = Math.Max(1, t.Int("m_Depth"));
                rec.Tex.Add(tr);
                if (st > 0) { Add(rec.Cat, "textures", st); rec.StreamAttrib += st; }
                break;
            }
            case 43:
            {
                if (sf.GetTypeTree(o) == null) return;
                var t = TypeTreeReader.Read(sf, o);
                long st = t.Obj("m_StreamData")?.Long("size") ?? 0;
                rec.Mesh.Add(new MeshRec
                {
                    B = rec.Name, Cab = sf.Name, Pid = o.PathId, Name = t.Str("m_Name"),
                    Verts = t.Obj("m_VertexData")?.Int("m_VertexCount") ?? 0, Obj = o.ByteSize, Stream = st,
                });
                if (st > 0) { Add(rec.Cat, "meshes", st); rec.StreamAttrib += st; }
                break;
            }
            case 83:
            {
                if (sf.GetTypeTree(o) == null) return;
                var t = TypeTreeReader.Read(sf, o);
                long rs = t.Obj("m_Resource")?.Long("m_Size") ?? 0;
                rec.Audio.Add(new AudioRec
                {
                    B = rec.Name, Cab = sf.Name, Pid = o.PathId, Name = t.Str("m_Name"), Fmt = t.Int("m_CompressionFormat"),
                    Ch = t.Int("m_Channels"), Freq = t.Int("m_Frequency"), LoadType = t.Int("m_LoadType"), Len = t.Float("m_Length"),
                    Obj = o.ByteSize, Res = rs,
                });
                if (rs > 0) { Add(rec.Cat, "audio", rs); rec.StreamAttrib += rs; }
                break;
            }
            case 329:
            {
                if (sf.GetTypeTree(o) == null) return;
                var t = TypeTreeReader.Read(sf, o);
                long rs = t.Obj("m_ExternalResources")?.Long("m_Size") ?? 0;
                if (rs > 0) { Add(rec.Cat, "video", rs); rec.StreamAttrib += rs; }
                break;
            }
            case 48:
            {
                var sr = new ShaderRec { B = rec.Name, Cab = sf.Name, Pid = o.PathId, Obj = o.ByteSize };
                rec.Shaders.Add(sr);
                if (sf.GetTypeTree(o) == null) { sr.Err = "no type tree"; return; }
                try
                {
                    var t = TypeTreeReader.Read(sf, o);
                    var pf = t.Obj("m_ParsedForm");
                    sr.Name = pf?.Str("m_Name") ?? t.Str("m_Name");
                    var subs = pf?.List("m_SubShaders") ?? new List<object>();
                    sr.Sub = subs.Count;
                    sr.PassNames = new List<string>();
                    foreach (var s in subs.OfType<TypeTreeObject>())
                        foreach (var p in (s.List("m_Passes") ?? new List<object>()).OfType<TypeTreeObject>())
                        {
                            sr.Passes++;
                            var st = p.Obj("m_State");
                            string pn = st?.Str("m_Name") ?? p.Str("m_Name");
                            if (sr.PassNames.Count < 12) sr.PassNames.Add(string.IsNullOrEmpty(pn) ? "(unnamed)" : pn);
                        }
                    var props = pf?.Obj("m_PropInfo")?.List("m_Props") ?? new List<object>();
                    sr.Props = props.Count;
                    sr.PropNames = props.OfType<TypeTreeObject>().Select(p => p.Str("m_Name")).Where(x => x != null).Take(40).ToList();
                    sr.Kw = (pf?.List("m_KeywordNames") ?? t.List("m_KeywordNames"))?.Count ?? 0;
                }
                catch (Exception e)
                {
                    sr.Err = e.Message.Length > 160 ? e.Message[..160] : e.Message;
                    sr.Name ??= QuickParsedName(sf, o);
                }
                break;
            }
            case 21:
            {
                if (sf.GetTypeTree(o) == null) return;
                var t = TypeTreeReader.Read(sf, o);
                var sp = t.PPtr("m_Shader");
                rec.Mats.Add(new MatRec { B = rec.Name, Cab = sf.Name, Pid = o.PathId, Name = t.Str("m_Name"), ShCab = sp.IsNull ? null : ExtName(sf, sp.FileId), ShPid = sp.PathId });
                break;
            }
            case 115:
            {
                if (sf.GetTypeTree(o) == null) return;
                var t = TypeTreeReader.Read(sf, o);
                rec.Scripts.Add(new ScriptRec { Cab = sf.Name, Pid = o.PathId, Cls = t.Str("m_ClassName"), Ns = t.Str("m_Namespace"), Asm = t.Str("m_AssemblyName") });
                break;
            }
        }
    }

    /// <summary>Fallback for shaders whose full read fails: Shader's m_ParsedForm starts with m_PropInfo; we only need the name,
    /// so scan for a plausible "Name/Path" string near the start. Best-effort.</summary>
    static string QuickParsedName(SerializedFile sf, ObjectInfo o)
    {
        var d = sf.GetObjectBytes(o);
        int lim = Math.Min(d.Length - 4, 1 << 16);
        for (int i = 0; i < lim; i += 4)
        {
            int len = BinaryPrimitives.ReadInt32LittleEndian(d[i..]);
            if (len < 4 || len > 120 || i + 4 + len > d.Length) continue;
            var s = d.Slice(i + 4, len);
            bool ok = true; bool slash = false;
            foreach (var c in s) { if (c < 0x20 || c > 0x7e) { ok = false; break; } if (c == '/') slash = true; }
            if (ok && slash) return Encoding.ASCII.GetString(s);
        }
        return null;
    }

    public static Survey Run(string dataDir, string bundleDir, int dop)
    {
        var paths = Directory.GetFiles(bundleDir, "*.bundle");
        Array.Sort(paths, StringComparer.Ordinal);
        var results = new BundleRec[paths.Length];
        int done = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        // largest first so the tail is short
        var order = paths.Select((p, i) => (p, i, len: new FileInfo(p).Length)).OrderByDescending(x => x.len).ToArray();
        Parallel.ForEach(order, new ParallelOptions { MaxDegreeOfParallelism = dop }, x =>
        {
            results[x.i] = SurveyBundle(x.p);
            int d = Interlocked.Increment(ref done);
            if (d % 250 == 0) Console.Error.WriteLine($"  survey {d}/{paths.Length} ({sw.Elapsed.TotalSeconds:F0}s, ws {Environment.WorkingSet >> 20} MiB)");
        });
        return new Survey { DataDir = dataDir, When = DateTime.Now, Bundles = results.ToList() };
    }
}
