// Gate-0 scorecard: what engine/middleware/BCL surface does the game's IL actually touch, and how often?
// Reads metadata + walks every IL body (no decompile needed). Usage: ApiSurface <ManagedDir> <outDir> <gameAsm>...
using System.Collections.Immutable;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Text.Json;

var managed = args[0]; var outDir = args[1]; var gameAsmsRaw = args.Skip(2).ToArray();
Directory.CreateDirectory(outDir);

// ---- load every managed assembly: type defs, base types, forwarders ----
var readers = new Dictionary<string, MetadataReader>(StringComparer.OrdinalIgnoreCase);
var peOf = new Dictionary<string, PEReader>(StringComparer.OrdinalIgnoreCase);
foreach (var f in Directory.GetFiles(managed, "*.dll")) {
    try {
        var pe = new PEReader(File.OpenRead(f)); if (!pe.HasMetadata) continue;
        var md = pe.GetMetadataReader(); if (!md.IsAssembly) continue;
        var an = md.GetString(md.GetAssemblyDefinition().Name); readers[an] = md; peOf[an] = pe;
    } catch { }
}
var forward = new Dictionary<string, string>();          // [facade]T -> [target]T
var baseOf = new Dictionary<string, string>();           // [asm]T -> [asm]Base
foreach (var (asm, md) in readers) {
    foreach (var eh in md.ExportedTypes) {
        var et = md.GetExportedType(eh);
        if (et.Implementation.Kind == HandleKind.AssemblyReference) {
            var tgt = md.GetString(md.GetAssemblyReference((AssemblyReferenceHandle)et.Implementation).Name);
            var n = Names.Join(md.GetString(et.Namespace), md.GetString(et.Name));
            forward[$"[{asm}]{n}"] = $"[{tgt}]{n}";
        }
    }
    var prov = new Prov(md, asm);
    foreach (var th in md.TypeDefinitions) {
        var td = md.GetTypeDefinition(th);
        if (td.BaseType.IsNil) continue;
        baseOf[prov.Def(th)] = prov.Key(td.BaseType);
    }
}
var gameAsms = gameAsmsRaw.Where(a => { bool ok = readers.ContainsKey(a); if (!ok) Console.Error.WriteLine("skip (not an assembly): " + a); return ok; }).ToArray();
string Canon(string k) { int guard = 0; while (forward.TryGetValue(k, out var t) && guard++ < 8) k = t; return k; }
bool Derives(string k, string target) {
    for (int i = 0; i < 64 && k != null; i++) { k = Canon(k); if (k == target) return true; baseOf.TryGetValue(k, out k); }
    return false;
}
const string MB = "[UnityEngine.CoreModule]UnityEngine.MonoBehaviour";
const string SO = "[UnityEngine.CoreModule]UnityEngine.ScriptableObject";

// ---- opcode operand table ----
var ops = new Dictionary<int, OperandType>();
foreach (var fi in typeof(OpCodes).GetFields()) { var oc = (OpCode)fi.GetValue(null); ops[(ushort)oc.Value] = oc.OperandType; }

var unityMessages = new[] { "Awake","Start","Update","FixedUpdate","LateUpdate","OnEnable","OnDisable","OnDestroy",
    "OnGUI","OnTriggerEnter2D","OnTriggerExit2D","OnTriggerStay2D","OnCollisionEnter2D","OnTriggerEnter","OnCollisionEnter",
    "OnApplicationQuit","OnApplicationPause","OnApplicationFocus","OnValidate","OnBecameVisible","OnBecameInvisible",
    "OnAnimatorMove","OnRenderObject","OnPostRender","OnPreRender","OnRenderImage","OnDrawGizmos","Reset" };

var memberUse = new Dictionary<string, long>();     // "[asm]Type::member" -> call sites
var typeUse = new Dictionary<string, long>();       // "[asm]Type" -> refs
var game = new List<object>();
long totalIl = 0, totalMethods = 0, totalTypes = 0, mbTypes = 0, soTypes = 0, coroutineTypes = 0, serializedFields = 0;
var msgCounts = unityMessages.ToDictionary(m => m, m => 0);

foreach (var ga in gameAsms) {
    var md = readers[ga]; var prov = new Prov(md, ga);
    var pe = peOf[ga];
    int types = 0, methods = 0; long il = 0, mb = 0, so = 0, co = 0, sf = 0;
    foreach (var th in md.TypeDefinitions) {
        var td = md.GetTypeDefinition(th); var name = md.GetString(td.Name);
        if (name == "<Module>") continue;
        var key = prov.Def(th);
        bool compilerGen = name.StartsWith("<") || name.Contains("<");
        if (!compilerGen) types++;
        bool isMb = Derives(key, MB), isSo = Derives(key, SO);
        if (isMb && !compilerGen) mb++;
        if (isSo && !compilerGen) so++;
        if (compilerGen && name.Contains(">d__")) co++;   // iterator state machines (coroutines + LINQ-style yields)
        if (isMb || isSo) {
            foreach (var fh in td.GetFields()) {
                var fd = md.GetFieldDefinition(fh); var a = fd.Attributes;
                if ((a & System.Reflection.FieldAttributes.Static) != 0) continue;
                bool pub = (a & System.Reflection.FieldAttributes.FieldAccessMask) == System.Reflection.FieldAttributes.Public;
                bool serAttr = fd.GetCustomAttributes().Any(ch => prov.AttrName(ch).EndsWith("SerializeField"));
                bool nonSer = (a & System.Reflection.FieldAttributes.NotSerialized) != 0;
                if ((pub || serAttr) && !nonSer) sf++;
            }
            foreach (var mh in td.GetMethods()) { var mn = md.GetString(md.GetMethodDefinition(mh).Name); if (msgCounts.ContainsKey(mn)) msgCounts[mn]++; }
        }
        foreach (var mh in td.GetMethods()) {
            var mdef = md.GetMethodDefinition(mh); methods++;
            if (mdef.RelativeVirtualAddress == 0) continue;
            var body = pe.GetMethodBody(mdef.RelativeVirtualAddress); var r = body.GetILReader(); il += r.Length;
            while (r.RemainingBytes > 0) {
                int op = r.ReadByte(); if (op == 0xFE) op = 0xFE00 | r.ReadByte();
                if (!ops.TryGetValue(op, out var ot)) break;
                switch (ot) {
                    case OperandType.InlineNone: break;
                    case OperandType.ShortInlineBrTarget: case OperandType.ShortInlineI: case OperandType.ShortInlineVar: r.ReadByte(); break;
                    case OperandType.InlineVar: r.ReadInt16(); break;
                    case OperandType.InlineI: case OperandType.InlineBrTarget: case OperandType.ShortInlineR: case OperandType.InlineString: case OperandType.InlineSig: r.ReadInt32(); break;
                    case OperandType.InlineI8: case OperandType.InlineR: r.ReadInt64(); break;
                    case OperandType.InlineSwitch: { int n = r.ReadInt32(); for (int i = 0; i < n; i++) r.ReadInt32(); break; }
                    case OperandType.InlineField: case OperandType.InlineMethod: case OperandType.InlineTok: case OperandType.InlineType:
                        prov.Count(MetadataTokens.EntityHandle(r.ReadInt32()), memberUse, typeUse); break;
                    default: r.ReadInt32(); break;
                }
            }
        }
    }
    totalIl += il; totalMethods += methods; totalTypes += types; mbTypes += mb; soTypes += so; coroutineTypes += co; serializedFields += sf;
    game.Add(new { assembly = ga, types, methods, ilBytes = il, monoBehaviours = mb, scriptableObjects = so, iteratorStateMachines = co, serializedFields = sf });
}

// ---- bucket by referenced assembly ----
string AsmOf(string k) => k.StartsWith("[") ? k[1..k.IndexOf(']')] : "?";
string Bucket(string a) =>
    gameAsms.Contains(a) ? "game" :
    a.StartsWith("UnityEngine") ? "unity-engine" :
    a.StartsWith("Unity.") ? "unity-package" :
    a is "mscorlib" or "netstandard" || a.StartsWith("System") || a.StartsWith("Microsoft") || a.StartsWith("Mono.") ? "dotnet-bcl" : "third-party";
var perAsm = memberUse.Select(kv => (key: Canon(kv.Key.Split("::")[0]) + "::" + kv.Key.Split("::")[1], n: kv.Value))
    .GroupBy(x => AsmOf(x.key)).Where(g => Bucket(g.Key) != "game")
    .Select(g => new {
        assembly = g.Key, bucket = Bucket(g.Key),
        distinctMembers = g.Select(x => x.key).Distinct().Count(),
        distinctTypes = g.Select(x => x.key.Split("::")[0]).Distinct().Count(),
        callSites = g.Sum(x => x.n),
        top = g.GroupBy(x => x.key).Select(h => new { member = h.Key[(h.Key.IndexOf(']') + 1)..], n = h.Sum(z => z.n) }).OrderByDescending(h => h.n).Take(40).ToList()
    }).OrderByDescending(x => x.callSites).ToList();
var buckets = perAsm.GroupBy(x => x.bucket).Select(g => new { bucket = g.Key, assemblies = g.Count(), distinctTypes = g.Sum(x => x.distinctTypes), distinctMembers = g.Sum(x => x.distinctMembers), callSites = g.Sum(x => x.callSites) }).OrderByDescending(b => b.callSites).ToList();

var result = new { game, totals = new { types = totalTypes, methods = totalMethods, ilBytes = totalIl, monoBehaviours = mbTypes, scriptableObjects = soTypes, iteratorStateMachines = coroutineTypes, serializedFields },
                   unityMessageImplementations = msgCounts.Where(kv => kv.Value > 0).OrderByDescending(kv => kv.Value).ToDictionary(kv => kv.Key, kv => kv.Value),
                   buckets, perAssembly = perAsm };
File.WriteAllText(Path.Combine(outDir, "api_surface.json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
// full flat list for shim planning
var sb = new StringBuilder("assembly\tbucket\tmember\tcallSites\n");
foreach (var kv in memberUse.Select(kv => (k: Canon(kv.Key.Split("::")[0]) + "::" + kv.Key.Split("::")[1], kv.Value)).GroupBy(x => x.k).Select(g => (g.Key, n: g.Sum(z => z.Value))).OrderByDescending(x => x.n)) {
    var a = AsmOf(kv.Key); if (Bucket(a) == "game") continue;
    sb.Append(a).Append('\t').Append(Bucket(a)).Append('\t').Append(kv.Key[(kv.Key.IndexOf(']') + 1)..]).Append('\t').Append(kv.n).Append('\n');
}
File.WriteAllText(Path.Combine(outDir, "api_members.tsv"), sb.ToString());
Console.WriteLine(JsonSerializer.Serialize(new { result.totals, buckets }, new JsonSerializerOptions { WriteIndented = true }));

static class Names { public static string Join(string ns, string n) => string.IsNullOrEmpty(ns) ? n : ns + "." + n; }

// Turns handles into stable "[assembly]Namespace.Type" keys (generic instantiations collapse to their definition).
sealed class Prov : ISignatureTypeProvider<string, object> {
    readonly MetadataReader md; readonly string self;
    public Prov(MetadataReader md, string self) { this.md = md; this.self = self; }
    public string Def(TypeDefinitionHandle h) {
        var td = md.GetTypeDefinition(h); var n = md.GetString(td.Name);
        var decl = td.GetDeclaringType();
        if (!decl.IsNil) return Def(decl) + "/" + n;
        return $"[{self}]{Names.Join(md.GetString(td.Namespace), n)}";
    }
    public string Ref(TypeReferenceHandle h) {
        var tr = md.GetTypeReference(h); var n = Names.Join(md.GetString(tr.Namespace), md.GetString(tr.Name));
        switch (tr.ResolutionScope.Kind) {
            case HandleKind.TypeReference: return Ref((TypeReferenceHandle)tr.ResolutionScope) + "/" + md.GetString(tr.Name);
            case HandleKind.AssemblyReference: return $"[{md.GetString(md.GetAssemblyReference((AssemblyReferenceHandle)tr.ResolutionScope).Name)}]{n}";
            default: return $"[{self}]{n}";
        }
    }
    public string Key(EntityHandle h) => h.Kind switch {
        HandleKind.TypeDefinition => Def((TypeDefinitionHandle)h),
        HandleKind.TypeReference => Ref((TypeReferenceHandle)h),
        HandleKind.TypeSpecification => md.GetTypeSpecification((TypeSpecificationHandle)h).DecodeSignature(this, null),
        _ => "?"
    };
    public string AttrName(CustomAttributeHandle ch) {
        var ca = md.GetCustomAttribute(ch);
        return ca.Constructor.Kind switch {
            HandleKind.MemberReference => Key(md.GetMemberReference((MemberReferenceHandle)ca.Constructor).Parent),
            HandleKind.MethodDefinition => Def(md.GetMethodDefinition((MethodDefinitionHandle)ca.Constructor).GetDeclaringType()),
            _ => "" };
    }
    public void Count(EntityHandle h, Dictionary<string, long> members, Dictionary<string, long> types) {
        string owner = null, member = null;
        switch (h.Kind) {
            case HandleKind.MemberReference: {
                var mr = md.GetMemberReference((MemberReferenceHandle)h);
                owner = mr.Parent.Kind switch {
                    HandleKind.TypeReference or HandleKind.TypeSpecification or HandleKind.TypeDefinition => Key((EntityHandle)mr.Parent),
                    _ => "?" };
                member = md.GetString(mr.Name); break; }
            case HandleKind.MethodSpecification: {
                var ms = md.GetMethodSpecification((MethodSpecificationHandle)h);
                Count(ms.Method, members, types); return; }
            case HandleKind.TypeReference: case HandleKind.TypeSpecification:
                owner = Key(h); member = "<type>"; break;
            default: return;   // MethodDef/FieldDef/TypeDef = the game's own code
        }
        if (owner == null || owner.StartsWith($"[{self}]")) return;
        var k = owner + "::" + member; members[k] = members.GetValueOrDefault(k) + 1; types[owner] = types.GetValueOrDefault(owner) + 1;
    }
    // ISignatureTypeProvider — only the outermost definition matters for ownership.
    public string GetPrimitiveType(PrimitiveTypeCode c) => "[mscorlib]System." + c;
    public string GetTypeFromDefinition(MetadataReader r, TypeDefinitionHandle h, byte k) => Def(h);
    public string GetTypeFromReference(MetadataReader r, TypeReferenceHandle h, byte k) => Ref(h);
    public string GetTypeFromSpecification(MetadataReader r, object c, TypeSpecificationHandle h, byte k) => md.GetTypeSpecification(h).DecodeSignature(this, c);
    public string GetSZArrayType(string e) => "[mscorlib]System.Array";
    public string GetArrayType(string e, ArrayShape s) => "[mscorlib]System.Array";
    public string GetByReferenceType(string e) => e;
    public string GetPointerType(string e) => e;
    public string GetPinnedType(string e) => e;
    public string GetModifiedType(string m, string u, bool req) => u;
    public string GetGenericInstantiation(string g, ImmutableArray<string> a) => g;
    public string GetGenericMethodParameter(object c, int i) => "[?]!!" + i;
    public string GetGenericTypeParameter(object c, int i) => "[?]!" + i;
    public string GetFunctionPointerType(MethodSignature<string> s) => "[?]fnptr";
}
