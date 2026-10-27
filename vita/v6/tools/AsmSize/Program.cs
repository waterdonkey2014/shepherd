// V6 vita-sim (vita/notes/v6-mac-sim.md): offline metadata measurements with System.Reflection.Metadata only (nothing is loaded
// or executed).
//   AsmSize sizes <assemblies.txt> <out.tsv>   per loaded assembly (the run's runtime/logs/assemblies.txt): file bytes, metadata
//                                             bytes, IL bytes (method bodies incl. headers + EH), methods, types; totals by group
//   AsmSize refs <out.tsv> <dll|dir>...        which methods of the non-BCL assemblies reference crypto / compression / network /
//                                             serializer types (IL operand tokens), to say what the game uses those natives for
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

static class P
{
    static bool IsBcl(string n) => n is "mscorlib" or "netstandard" || n.StartsWith("System") || n.StartsWith("Microsoft.");
    static string Group(string name, string loc) =>
        name.StartsWith("Shepherd.") ? "engine" : IsBcl(name) && !loc.Contains("/Data/Managed/") && !loc.Contains("/Managed/") ? "BCL (.NET 8)" :
        name.StartsWith("UnityEngine") || name.StartsWith("Unity.") ? "Unity modules + packages" : "game + middleware";

    static int Main(string[] a)
    {
        if (a.Length >= 3 && a[0] == "sizes") return Sizes(a[1], a[2]);
        if (a.Length >= 3 && a[0] == "refs") return Refs(a[1], a.Skip(2).ToArray());
        if (a.Length >= 3 && a[0] == "calls") return Calls(a[1], a[2]);
        if (a.Length >= 5 && a[0] == "reach") return Reach(a[1], a[2], a[3], a[4], a.Length > 5 ? a[5] : null, a.Length > 6 ? a[6] : null);
        if (a.Length >= 3 && a[0] == "pinvokes") return PInvokes(a[1], a.Skip(2).ToArray());
        if (a.Length >= 2 && a[0] == "typenames") return TypeNames(a.Skip(1).ToArray());
        if (a.Length >= 2 && a[0] == "literals") return Literals(a[1]);
        Console.Error.WriteLine("usage: AsmSize sizes <assemblies.txt> <out.tsv> | AsmSize refs <out.tsv> <dll|dir>... | AsmSize pinvokes <out.tsv> <dll|dir>..."); return 2;
    }

    // ---- literals (V6 stage 2) ----
    /// <summary>AsmSize literals &lt;Shepherd.Engine.dll&gt;: the distinct ldstr literals of every method of every type that is not
    /// [ProfileOnly] (nor nested in one), sorted, one per line (escaped): exactly the set the V3 diet's "readable property keys" scan
    /// (Assets/Diet.cs BuildReadable) adds, so two engine builds can be compared literal by literal, not only by count.</summary>
    static int Literals(string dll)
    {
        using var pe = new PEReader(File.OpenRead(dll)); var md = pe.GetMetadataReader();
        bool Profile(TypeDefinitionHandle th)
        {
            for (var h = th; !h.IsNil; h = md.GetTypeDefinition(h).GetDeclaringType())
                foreach (var ca in md.GetTypeDefinition(h).GetCustomAttributes())
                {
                    var c = md.GetCustomAttribute(ca); string name = c.Constructor.Kind == HandleKind.MemberReference
                        ? md.GetMemberReference((MemberReferenceHandle)c.Constructor).Parent is var p && p.Kind == HandleKind.TypeReference ? md.GetString(md.GetTypeReference((TypeReferenceHandle)p).Name) : ""
                        : md.GetString(md.GetTypeDefinition(md.GetMethodDefinition((MethodDefinitionHandle)c.Constructor).GetDeclaringType()).Name);
                    if (name == "ProfileOnlyAttribute") return true;
                }
            return false;
        }
        var set = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var th in md.TypeDefinitions)
        {
            if (Profile(th)) continue;
            foreach (var mh in md.GetTypeDefinition(th).GetMethods())
            {
                var m = md.GetMethodDefinition(mh); if (m.RelativeVirtualAddress == 0) continue;
                var il = pe.GetMethodBody(m.RelativeVirtualAddress).GetILBytes();
                for (int i = 0; i + 4 < il.Length; i++)
                    if (il[i] == 0x72 && il[i + 4] == 0x70) { try { set.Add(md.GetUserString(MetadataTokens.UserStringHandle(BitConverter.ToInt32(il, i + 1) & 0xFFFFFF))); } catch { } }
            }
        }
        foreach (var x in set) Console.WriteLine(x.Replace("\\", "\\\\").Replace("\n", "\\n").Replace("\r", "\\r"));
        Console.Error.WriteLine($"{set.Count} distinct literals in non-[ProfileOnly] code");
        return 0;
    }

    // ---- typenames (V6 stage 2) ----
    /// <summary>AsmSize typenames &lt;dll&gt;...: the distinct simple names of every type defined in the given assemblies (nested types
    /// included), one per line: the class names Mono's interpreter option --interp=jit=&lt;name&gt; matches (the mixed-mode estimate of
    /// what AOT-compiling those assemblies would buy, vita/notes/engine-pass-speed.md).</summary>
    static int TypeNames(string[] dlls)
    {
        var names = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var f in dlls)
        {
            using var pe = new PEReader(File.OpenRead(f)); var md = pe.GetMetadataReader();
            foreach (var th in md.TypeDefinitions) { var n = md.GetString(md.GetTypeDefinition(th).Name); if (n.Length > 0 && n != "<Module>") names.Add(n); }
        }
        foreach (var n in names) Console.WriteLine(n);
        return 0;
    }

    // ---- pinvokes (V6 stage B) ----
    /// <summary>AsmSize pinvokes &lt;out.tsv&gt; &lt;dll|dir&gt;...: every P/Invoke (ImplMap) declared in the given assemblies:
    /// assembly, library, entry point, managed signature. Used by vita/shepherd to check that the Vita's static table / shim
    /// answers every native entry point the packaged BCL declares (vita/notes/v6-vita-build.md).</summary>
    static int PInvokes(string outPath, string[] inputs)
    {
        var files = inputs.SelectMany(x => Directory.Exists(x) ? Directory.GetFiles(x, "*.dll") : new[] { x }).Distinct().OrderBy(x => x, StringComparer.Ordinal).ToList();
        var o = new List<string> { "assembly\tlibrary\tentrypoint\tsignature" };
        var prov = new SigNames();
        foreach (var f in files)
        {
            PEReader pe; try { pe = new PEReader(File.OpenRead(f)); if (!pe.HasMetadata) continue; } catch { continue; }
            using var _ = pe; var md = pe.GetMetadataReader(); if (!md.IsAssembly) continue;
            var asm = md.GetString(md.GetAssemblyDefinition().Name);
            foreach (var mh in md.MethodDefinitions)
            {
                var m = md.GetMethodDefinition(mh); if ((m.Attributes & MethodAttributes.PinvokeImpl) == 0) continue;
                var imp = m.GetImport(); if (imp.Module.IsNil) continue;
                var lib = md.GetString(md.GetModuleReference(imp.Module).Name); var ep = imp.Name.IsNil ? md.GetString(m.Name) : md.GetString(imp.Name);
                string sig; try { var s = m.DecodeSignature(prov, null); sig = $"{s.ReturnType} ({string.Join(", ", s.ParameterTypes)})"; } catch (Exception e) { sig = "? " + e.GetType().Name; }
                o.Add($"{asm}\t{lib}\t{ep}\t{sig}");
            }
        }
        File.WriteAllLines(outPath, o);
        foreach (var g in o.Skip(1).Select(l => l.Split('\t')).GroupBy(x => x[1]).OrderBy(g => g.Key, StringComparer.Ordinal))
            Console.WriteLine($"{g.Key,-56} {g.Select(x => x[2]).Distinct().Count(),4} entry points ({g.Count()} declarations)");
        Console.WriteLine($"{o.Count - 1} P/Invoke declarations -> {outPath}");
        return 0;
    }
    sealed class SigNames : ISignatureTypeProvider<string, object>
    {
        public string GetPrimitiveType(PrimitiveTypeCode t) => t.ToString();
        public string GetTypeFromDefinition(MetadataReader md, TypeDefinitionHandle h, byte k) => Full(md, h);
        public string GetTypeFromReference(MetadataReader md, TypeReferenceHandle h, byte k) { var tr = md.GetTypeReference(h); var ns = md.GetString(tr.Namespace); return (ns.Length > 0 ? ns + "." : "") + md.GetString(tr.Name); }
        public string GetTypeFromSpecification(MetadataReader md, object c, TypeSpecificationHandle h, byte k) => "spec";
        public string GetSZArrayType(string e) => e + "[]";
        public string GetArrayType(string e, ArrayShape s) => e + "[,]";
        public string GetByReferenceType(string e) => e + "&";
        public string GetPointerType(string e) => e + "*";
        public string GetGenericInstantiation(string g, System.Collections.Immutable.ImmutableArray<string> a) => $"{g}<{string.Join(",", a)}>";
        public string GetGenericTypeParameter(object c, int i) => "!" + i;
        public string GetGenericMethodParameter(object c, int i) => "!!" + i;
        public string GetFunctionPointerType(MethodSignature<string> s) => "fnptr";
        public string GetModifiedType(string m, string u, bool r) => u;
        public string GetPinnedType(string e) => e;
    }

    static int Sizes(string list, string outPath)
    {
        var rows = new List<(string name, string group, long file, long meta, long il, int methods, int bodies, int types, string loc)>();
        foreach (var line in File.ReadAllLines(list).Where(l => l.Length > 0 && l[0] != '#'))
        {
            var f = line.Split('\t'); if (f.Length < 4 || !File.Exists(f[3])) continue;
            using var fs = File.OpenRead(f[3]); using var pe = new PEReader(fs);
            if (!pe.HasMetadata) continue;
            var md = pe.GetMetadataReader(); long il = 0; int methods = 0, bodies = 0;
            foreach (var mh in md.MethodDefinitions)
            {
                methods++; var m = md.GetMethodDefinition(mh); if (m.RelativeVirtualAddress == 0) continue;
                try { il += pe.GetMethodBody(m.RelativeVirtualAddress).Size; bodies++; } catch { }
            }
            rows.Add((f[0], Group(f[0], f[3]), new FileInfo(f[3]).Length, pe.PEHeaders.MetadataSize, il, methods, bodies, md.TypeDefinitions.Count, f[3]));
        }
        var o = new List<string> { "assembly\tgroup\tfile_bytes\tmetadata_bytes\til_bytes\tmethods\tmethods_with_body\ttypes\tlocation" };
        o.AddRange(rows.OrderByDescending(r => r.il).Select(r => $"{r.name}\t{r.group}\t{r.file}\t{r.meta}\t{r.il}\t{r.methods}\t{r.bodies}\t{r.types}\t{r.loc}"));
        File.WriteAllLines(outPath, o);
        string MiB(long b) => $"{b / 1048576.0,7:F2} MiB";
        foreach (var g in rows.GroupBy(r => r.group).OrderByDescending(g => g.Sum(r => r.il)))
            Console.WriteLine($"{g.Key,-28} {g.Count(),4} assemblies  file {MiB(g.Sum(r => r.file))}  metadata {MiB(g.Sum(r => r.meta))}  IL {MiB(g.Sum(r => r.il))}  methods {g.Sum(r => r.methods),7}  types {g.Sum(r => r.types),6}");
        Console.WriteLine($"{"TOTAL",-28} {rows.Count,4} assemblies  file {MiB(rows.Sum(r => r.file))}  metadata {MiB(rows.Sum(r => r.meta))}  IL {MiB(rows.Sum(r => r.il))}  methods {rows.Sum(r => r.methods),7}  types {rows.Sum(r => r.types),6}  -> {outPath}");
        return 0;
    }

    // ---- refs ----
    static readonly Dictionary<short, OperandType> Ops = typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static).Select(f => (OpCode)f.GetValue(null)).ToDictionary(o => o.Value, o => o.OperandType);
    static readonly (string prefix, string what)[] Watch =
    {
        ("System.Security.Cryptography", "crypto"), ("System.IO.Compression", "compression (zlib)"), ("System.Net", "network"),
        ("MessagePack", "MessagePack"), ("Newtonsoft.Json.Bson", "BSON"),
    };
    static string Cat(string ns) => Watch.FirstOrDefault(w => ns == w.prefix || ns.StartsWith(w.prefix + ".")).what;

    static int Refs(string outPath, string[] inputs)
    {
        var files = inputs.SelectMany(x => Directory.Exists(x) ? Directory.GetFiles(x, "*.dll") : new[] { x }).Distinct().OrderBy(x => x).ToList();
        var o = new List<string> { "assembly\tcategory\tmethod\treferences" };
        var summary = new SortedDictionary<string, SortedSet<string>>();
        foreach (var f in files)
        {
            PEReader pe; try { pe = new PEReader(File.OpenRead(f)); if (!pe.HasMetadata) continue; } catch { continue; }
            using var _ = pe; var md = pe.GetMetadataReader(); if (!md.IsAssembly) continue;
            var asm = md.GetString(md.GetAssemblyDefinition().Name);
            if (IsBcl(asm)) continue;
            foreach (var th in md.TypeDefinitions)
            {
                var t = md.GetTypeDefinition(th); var tn = Full(md, th);
                foreach (var mh in t.GetMethods())
                {
                    var m = md.GetMethodDefinition(mh); if (m.RelativeVirtualAddress == 0) continue;
                    byte[] il; try { il = pe.GetMethodBody(m.RelativeVirtualAddress).GetILBytes(); } catch { continue; }
                    var hits = new SortedSet<string>();
                    foreach (var tok in Tokens(il))
                    {
                        var (ns, member) = Resolve(md, tok); if (ns == null) continue;
                        var c = Cat(ns); if (c != null) hits.Add($"{c}: {member}");
                    }
                    if (hits.Count == 0) continue;
                    o.Add($"{asm}\t{string.Join(",", hits.Select(h => h.Split(':')[0]).Distinct())}\t{tn}::{md.GetString(m.Name)}\t{string.Join("; ", hits)}");
                    foreach (var h in hits) { var k = $"{asm} | {h.Split(':')[0]}"; if (!summary.TryGetValue(k, out var s)) summary[k] = s = new(); s.Add(h[(h.IndexOf(':') + 2)..]); }
                }
            }
        }
        File.WriteAllLines(outPath, o);
        foreach (var kv in summary) Console.WriteLine($"{kv.Key}: {string.Join(", ", kv.Value.Take(14))}{(kv.Value.Count > 14 ? $" (+{kv.Value.Count - 14})" : "")}");
        Console.WriteLine($"{o.Count - 1} methods -> {outPath}");
        return 0;
    }

    /// <summary>AsmSize calls &lt;dll&gt; &lt;substring&gt;: every method whose IL references a member or string containing the substring.</summary>
    static int Calls(string dll, string sub)
    {
        using var pe = new PEReader(File.OpenRead(dll)); var md = pe.GetMetadataReader();
        foreach (var th in md.TypeDefinitions)
            foreach (var mh in md.GetTypeDefinition(th).GetMethods())
            {
                var m = md.GetMethodDefinition(mh); if (m.RelativeVirtualAddress == 0) continue;
                var il = pe.GetMethodBody(m.RelativeVirtualAddress).GetILBytes(); var hits = new SortedSet<string>();
                foreach (var tok in Tokens(il)) { var (ns, mem) = Resolve(md, tok); if (mem != null && (ns + "." + mem).Contains(sub)) hits.Add(ns + "." + mem); }
                for (int i = 0; i + 4 < il.Length; i++) if (il[i] == 0x72 && il[i + 4] == 0x70) { try { var str = md.GetUserString(MetadataTokens.UserStringHandle(BitConverter.ToInt32(il, i + 1) & 0xFFFFFF)); if (str.Contains(sub)) hits.Add("\"" + str + "\""); } catch { } }
                if (hits.Count > 0) Console.WriteLine($"{Full(md, th)}::{md.GetString(m.Name)}  ->  {string.Join(", ", hits)}");
            }
        return 0;
    }

    // ---- reach ----
    /// <summary>AsmSize reach &lt;out.tsv&gt; &lt;interp-methods.tsv&gt; &lt;image&gt; &lt;other build of that dll&gt; [follow-regex]
    /// Which P/Invokes would the SAME managed work bind in another build of a platform-specific assembly (here: the linux-arm
    /// BCL the Vita runs, vs the osx-arm64 one the Mac sim ran)? Start = every method of &lt;image&gt; the Mac run's interpreter
    /// transformed (matched by type + name, all overloads). Walk: calls/newobj/ldftn into this assembly's own methods when the
    /// target is a start method or its type matches follow-regex (the platform-specific implementation types); a type that is
    /// constructed (newobj) contributes all its methods (its interface/virtual methods are what gets called); every visited
    /// type contributes its .cctor. Collect the DllImports reached. A static over-approximation, labelled as such.</summary>
    static int Reach(string outPath, string tsv, string image, string dll, string macDll, string follow)
    {
        // macDll: the build the Mac ran. A callee that does not exist there (type::name) is platform-specific code of the other
        // build (e.g. AesImplementation.GetAlgorithm on Linux): followed too.
        var macKeys = new HashSet<string>();
        if (macDll != null) { using var mpe = new PEReader(File.OpenRead(macDll)); var mmd = mpe.GetMetadataReader(); foreach (var th in mmd.TypeDefinitions) foreach (var mh in mmd.GetTypeDefinition(th).GetMethods()) macKeys.Add(Full(mmd, th) + "::" + mmd.GetString(mmd.GetMethodDefinition(mh).Name)); }
        var rx = new System.Text.RegularExpressions.Regex(follow ?? "OpenSsl|Interop|Evp|LiteHash|HashProviderDispenser|CryptoInitializer|RandomNumberGeneratorImplementation|SafeEvp|SafeHmac|SafeBignum");
        var want = new HashSet<string>();
        foreach (var l in File.ReadLines(tsv))
        {
            var f = l.Split('\t'); if (f.Length < 6 || f[0] != "interp" || f[1] != image) continue;
            var m = f[5]; if (m.StartsWith("(wrapper")) continue;
            int c = m.IndexOf(':'); if (c < 0) continue; int sp = m.IndexOf(" (", c); if (sp < 0) sp = m.Length;
            want.Add(StripGen(m[..c]) + "::" + m[(c + 1)..sp]);
        }
        using var pe = new PEReader(File.OpenRead(dll)); var md = pe.GetMetadataReader();
        var byName = new Dictionary<string, List<MethodDefinitionHandle>>();
        var typeOf = new Dictionary<MethodDefinitionHandle, TypeDefinitionHandle>();
        foreach (var th in md.TypeDefinitions) foreach (var mh in md.GetTypeDefinition(th).GetMethods())
        { var k = Full(md, th) + "::" + md.GetString(md.GetMethodDefinition(mh).Name); (byName.TryGetValue(k, out var lst) ? lst : byName[k] = new()).Add(mh); typeOf[mh] = th; }
        var q = new Queue<MethodDefinitionHandle>(); var seen = new HashSet<MethodDefinitionHandle>(); var typesSeen = new HashSet<TypeDefinitionHandle>(); var built = new HashSet<TypeDefinitionHandle>();
        int matched = 0;
        foreach (var w in want) if (byName.TryGetValue(w, out var l)) { matched++; foreach (var h in l) if (seen.Add(h)) q.Enqueue(h); }
        var hits = new SortedDictionary<string, SortedSet<string>>();
        void Visit(MethodDefinitionHandle h, bool forced) { if (seen.Contains(h)) return; var tn = Full(md, typeOf[h]); var k = tn + "::" + md.GetString(md.GetMethodDefinition(h).Name); if (forced || want.Contains(k) || rx.IsMatch(tn) || (macKeys.Count > 0 && !macKeys.Contains(k)) || (md.GetMethodDefinition(h).Attributes & MethodAttributes.PinvokeImpl) != 0) { seen.Add(h); q.Enqueue(h); } }
        void TypeAll(TypeDefinitionHandle th) { if (!built.Add(th)) return; foreach (var mh in md.GetTypeDefinition(th).GetMethods()) Visit(mh, true); }
        while (q.Count > 0)
        {
            var h = q.Dequeue(); var m = md.GetMethodDefinition(h); var th = typeOf[h];
            if (typesSeen.Add(th))
            {
                foreach (var mh in md.GetTypeDefinition(th).GetMethods()) if (md.GetString(md.GetMethodDefinition(mh).Name) == ".cctor") Visit(mh, true);
                if (System.Text.RegularExpressions.Regex.IsMatch(Full(md, th), @"Safe\w*Handle$")) foreach (var mh in md.GetTypeDefinition(th).GetMethods()) if (md.GetString(md.GetMethodDefinition(mh).Name) == "ReleaseHandle") Visit(mh, true);
            }
            if ((m.Attributes & MethodAttributes.PinvokeImpl) != 0)
            {
                var imp = m.GetImport(); var lib = md.GetString(md.GetModuleReference(imp.Module).Name); var ep = imp.Name.IsNil ? md.GetString(m.Name) : md.GetString(imp.Name);
                try
                {   // return type CLASS <TypeDef>: a SafeHandle subclass whose ReleaseHandle will run
                    var sb = md.GetBlobReader(m.Signature); sb.ReadSignatureHeader(); sb.ReadCompressedInteger();
                    var rt = sb.ReadSignatureTypeCode(); if (rt == SignatureTypeCode.TypeHandle) { var rth = sb.ReadTypeHandle(); if (rth.Kind == HandleKind.TypeDefinition) { var td = (TypeDefinitionHandle)rth; foreach (var mh in md.GetTypeDefinition(td).GetMethods()) if (md.GetString(md.GetMethodDefinition(mh).Name) is "ReleaseHandle" or ".cctor") Visit(mh, true); } }
                }
                catch { }
                (hits.TryGetValue(lib, out var set) ? set : hits[lib] = new()).Add(ep); continue;
            }
            if (m.RelativeVirtualAddress == 0) continue;
            byte[] il; try { il = pe.GetMethodBody(m.RelativeVirtualAddress).GetILBytes(); } catch { continue; }
            foreach (var (tok, isNew) in TokensOp(il))
            {
                var eh = MetadataTokens.EntityHandle(tok);
                if (eh.Kind == HandleKind.MethodSpecification) eh = md.GetMethodSpecification((MethodSpecificationHandle)eh).Method;
                if (eh.Kind == HandleKind.MethodDefinition) { var mh = (MethodDefinitionHandle)eh; Visit(mh, false); if (isNew && rx.IsMatch(Full(md, typeOf[mh]))) TypeAll(typeOf[mh]); }
                else if (eh.Kind == HandleKind.MemberReference)
                {   // a member of a generic instantiation of one of this assembly's types: all overloads by name
                    var mr = md.GetMemberReference((MemberReferenceHandle)eh);
                    if (mr.Parent.Kind != HandleKind.TypeSpecification) continue;
                    var br = md.GetBlobReader(md.GetTypeSpecification((TypeSpecificationHandle)mr.Parent).Signature);
                    if (br.ReadByte() != 0x15) continue; br.ReadByte(); var gt = br.ReadTypeHandle(); if (gt.Kind != HandleKind.TypeDefinition) continue;
                    var tdh = (TypeDefinitionHandle)gt; var nm = md.GetString(mr.Name);
                    foreach (var mh in md.GetTypeDefinition(tdh).GetMethods()) if (md.GetString(md.GetMethodDefinition(mh).Name) == nm) { Visit(mh, false); if (isNew && rx.IsMatch(Full(md, tdh))) TypeAll(tdh); }
                }
            }
        }
        var o = new List<string> { "library\tentrypoint" }; foreach (var kv in hits) foreach (var e in kv.Value) o.Add($"{kv.Key}\t{e}");
        File.WriteAllLines(outPath, o);
        Console.WriteLine($"{image}: {want.Count} transformed methods on the Mac, {matched} matched in {Path.GetFileName(dll)}; {seen.Count} methods visited; follow /{rx}/");
        foreach (var kv in hits) Console.WriteLine($"  {kv.Key} ({kv.Value.Count}): {string.Join(" ", kv.Value)}");
        return 0;
    }
    static string StripGen(string t) { var sb = new System.Text.StringBuilder(); int d = 0; foreach (var ch in t) { if (ch == '<') d++; else if (ch == '>') d--; else if (d == 0) sb.Append(ch); } return sb.ToString(); }
    static IEnumerable<(int tok, bool newobj)> TokensOp(byte[] il)
    {
        for (int i = 0; i < il.Length;)
        {
            short v = il[i++]; if (v == 0xFE && i < il.Length) v = (short)(0xFE00 | il[i++]);
            if (!Ops.TryGetValue(v, out var ot)) yield break;
            switch (ot)
            {
                case OperandType.InlineNone: break;
                case OperandType.ShortInlineBrTarget: case OperandType.ShortInlineI: case OperandType.ShortInlineVar: i += 1; break;
                case OperandType.InlineVar: i += 2; break;
                case OperandType.InlineI8: case OperandType.InlineR: i += 8; break;
                case OperandType.InlineSwitch: { int n = BitConverter.ToInt32(il, i); i += 4 + 4 * n; break; }
                case OperandType.InlineMethod: if (i + 4 <= il.Length) yield return (BitConverter.ToInt32(il, i), v == 0x73); i += 4; break;
                default: i += 4; break;
            }
        }
    }

    static IEnumerable<int> Tokens(byte[] il)
    {
        for (int i = 0; i < il.Length;)
        {
            short v = il[i++]; if (v == 0xFE && i < il.Length) v = (short)(0xFE00 | il[i++]);
            if (!Ops.TryGetValue(v, out var ot)) yield break;
            switch (ot)
            {
                case OperandType.InlineNone: break;
                case OperandType.ShortInlineBrTarget: case OperandType.ShortInlineI: case OperandType.ShortInlineVar: i += 1; break;
                case OperandType.InlineVar: i += 2; break;
                case OperandType.InlineI8: case OperandType.InlineR: i += 8; break;
                case OperandType.InlineSwitch: { int n = BitConverter.ToInt32(il, i); i += 4 + 4 * n; break; }
                case OperandType.InlineMethod: case OperandType.InlineField: case OperandType.InlineType: case OperandType.InlineTok:
                    if (i + 4 <= il.Length) yield return BitConverter.ToInt32(il, i); i += 4; break;
                default: i += 4; break;   // InlineBrTarget, InlineI, InlineSig, InlineString, ShortInlineR
            }
        }
    }

    static (string ns, string member) Resolve(MetadataReader md, int token)
    {
        try
        {
            var h = MetadataTokens.EntityHandle(token);
            switch (h.Kind)
            {
                case HandleKind.MemberReference: { var mr = md.GetMemberReference((MemberReferenceHandle)h); var (ns, tn) = TypeOf(md, mr.Parent); return (ns, ns == null ? null : $"{tn}.{md.GetString(mr.Name)}"); }
                case HandleKind.MethodSpecification: return Resolve(md, MetadataTokens.GetToken(md.GetMethodSpecification((MethodSpecificationHandle)h).Method));
                case HandleKind.TypeReference: case HandleKind.TypeSpecification: { var (ns, tn) = TypeOf(md, h); return (ns, tn); }
            }
        }
        catch { }
        return (null, null);
    }
    static (string ns, string name) TypeOf(MetadataReader md, EntityHandle h)
    {
        switch (h.Kind)
        {
            case HandleKind.TypeReference: { var tr = md.GetTypeReference((TypeReferenceHandle)h); var ns = md.GetString(tr.Namespace); if (ns.Length == 0 && tr.ResolutionScope.Kind == HandleKind.TypeReference) ns = TypeOf(md, (EntityHandle)tr.ResolutionScope).ns; return (ns, md.GetString(tr.Name)); }
            case HandleKind.TypeSpecification:
            {   // GENERICINST (0x15) CLASS|VALUETYPE <TypeDefOrRef coded token> ...
                var br = md.GetBlobReader(md.GetTypeSpecification((TypeSpecificationHandle)h).Signature);
                if (br.ReadByte() != 0x15) return (null, null); br.ReadByte(); var gt = br.ReadTypeHandle();
                return gt.Kind == HandleKind.TypeReference ? TypeOf(md, (EntityHandle)gt) : (null, null);
            }
        }
        return (null, null);
    }
    static string Full(MetadataReader md, TypeDefinitionHandle th)
    {
        var t = md.GetTypeDefinition(th); var n = md.GetString(t.Name); var ns = md.GetString(t.Namespace);
        var decl = t.GetDeclaringType(); if (!decl.IsNil) return Full(md, decl) + "/" + n;
        return ns.Length > 0 ? ns + "." + n : n;
    }
}
