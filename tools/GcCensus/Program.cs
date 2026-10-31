// GcCensus -- device kit #9, track gc (vita/notes/kit9-gc.md section "GC timing and the game"): does anything the game (or the engine) runs
// depend on WHEN the garbage collector runs? Concurrent marking (lever 32), the split nursery (M6), per-scene nursery sizes and the two-ended
// arena (M8) change only when collections happen and where objects end up; that is invisible to managed code EXCEPT through:
//   1. finalizers (they run on the finalizer thread after a collection found the object dead: their timing follows the GC's),
//   2. weak references (WeakReference / WeakReference<T> / ConditionalWeakTable / weak GCHandles: cleared by a collection),
//   3. the GC's own API (GC.Collect, GC.GetTotalMemory, GC.CollectionCount, GC.GetGeneration, ...: answers that follow the GC's schedule),
//   4. address-based identity hash codes (Mono's object hash = the object's address when it is first hashed, kept in its header after a
//      move): RuntimeHelpers.GetHashCode, Object.GetHashCode on a type that does not override it, and Dictionary / HashSet keys of such
//      types -- an object's address depends on how full the nursery was when it was allocated, so a different nursery size can give it a
//      different hash (the PC's Boehm-GC Mono hashes addresses too, so the original's own values are not reproducible either; a census
//      tells where it can matter). .NET's Dictionary / HashSet enumerate their entries array (insertion order), so a hash moves only a
//      bucket there -- but HASH-ORDERED containers enumerate their buckets: System.Collections.Hashtable (and HybridDictionary above 8
//      entries), ConcurrentDictionary, ImmutableDictionary / ImmutableHashSet. Keyed by identity-hashed objects, their enumeration order is
//      address order (review fix F10: section 4d counts their constructions and their enumeration sites).
// Reads the given assemblies (read only) and writes a report. The report names game types: keep it on the build volume, never in git.
//   dotnet run -- <report.txt> <dir or dll>...
using Mono.Cecil;
using Mono.Cecil.Cil;

var outPath = args[0];
var files = new List<string>();
foreach (var a in args.Skip(1))
{
    if (Directory.Exists(a)) files.AddRange(Directory.GetFiles(a, "*.dll").OrderBy(x => x, StringComparer.Ordinal));
    else if (File.Exists(a)) files.Add(a);
}
var resolver = new DefaultAssemblyResolver();
foreach (var d in files.Select(Path.GetDirectoryName).Distinct()) resolver.AddSearchDirectory(d);
var rp = new ReaderParameters { AssemblyResolver = resolver, ReadSymbols = false };
var asms = new List<AssemblyDefinition>();
var seen = new HashSet<string>();
foreach (var f in files)
{
    var n = Path.GetFileName(f);
    if (n.StartsWith("System.") || n.StartsWith("Microsoft.") || n is "mscorlib.dll" or "netstandard.dll" || !seen.Add(n)) continue;
    try { asms.Add(AssemblyDefinition.ReadAssembly(f, rp)); } catch (Exception e) { Console.Error.WriteLine($"skip {n}: {e.Message}"); }
}
var w = new StreamWriter(outPath);
w.WriteLine($"# GcCensus: {asms.Count} assemblies ({string.Join(", ", files.Select(Path.GetDirectoryName).Distinct())})");

var fin = new List<string>(); var weak = new List<string>(); var gcapi = new List<string>(); var idh = new List<string>(); var keys = new List<string>();
var finByAsm = new Dictionary<string, int>(); var weakByAsm = new Dictionary<string, int>(); var gcByAsm = new Dictionary<string, int>();
var gcByName = new SortedDictionary<string, int>(StringComparer.Ordinal);
var keyTypes = new Dictionary<string, (int n, string verdict)>();
var hashOrd = new List<string>(); var hashOrdEnum = new List<string>(); var hashOrdByType = new SortedDictionary<string, int>(StringComparer.Ordinal);
// review F10: containers whose ENUMERATION follows the keys' hash codes (bucket order), not insertion order
string[] hashOrderedTypes = { "System.Collections.Hashtable", "System.Collections.Specialized.HybridDictionary", "System.Collections.Concurrent.ConcurrentDictionary`2",
    "System.Collections.Immutable.ImmutableDictionary`2", "System.Collections.Immutable.ImmutableHashSet`1", "System.Collections.Immutable.ImmutableDictionary", "System.Collections.Immutable.ImmutableHashSet" };
string[] enumMembers = { "GetEnumerator", "get_Keys", "get_Values", "CopyTo", "ToArray" };

TypeDefinition Res(TypeReference t) { try { return t?.Resolve(); } catch { return null; } }
// does t (or a base below System.Object / System.ValueType) override GetHashCode? null = cannot tell (unresolved)
string HashKind(TypeReference tr)
{
    if (tr == null) return "?";
    if (tr.IsGenericParameter) return "generic parameter";
    if (tr.IsValueType) return "value type";
    if (tr.IsArray) return "ARRAY (identity hash)";
    var td = Res(tr);
    if (td == null) return "? unresolved";
    if (td.IsEnum || td.IsValueType) return "value type";
    if (td.IsInterface) return "interface (depends on the object)";
    if (td.FullName == "System.String" || td.FullName == "System.Type" || td.FullName == "System.RuntimeType") return "string/Type (stable)";
    for (var t = td; t != null && t.FullName != "System.Object"; t = Res(t.BaseType))
    {
        var gh = t.Methods.FirstOrDefault(m => m.Name == "GetHashCode" && m.Parameters.Count == 0 && m.IsVirtual);
        if (gh != null)
        {
            // what does it hash? (UnityEngine.Object: the instance id or base.GetHashCode)
            var body = gh.HasBody ? string.Join(" ", gh.Body.Instructions.Where(i => i.OpCode.Code is Code.Call or Code.Callvirt or Code.Ldfld).Select(i => i.Operand is MemberReference mr ? mr.Name : "")) : "";
            if (body.Contains("Object::GetHashCode") || (gh.HasBody && gh.Body.Instructions.Any(i => i.OpCode.Code == Code.Call && i.Operand is MethodReference mr2 && mr2.DeclaringType.FullName == "System.Object" && mr2.Name == "GetHashCode")))
                return $"overridden in {t.FullName} BUT calls base Object.GetHashCode (identity hash)";
            return $"overridden in {t.FullName} ({body})";
        }
    }
    return "IDENTITY HASH (no override)";
}

foreach (var a in asms)
{
    var an = a.Name.Name;
    foreach (var t in a.MainModule.GetTypes())
    {
        // 1. finalizers
        var f = t.Methods.FirstOrDefault(m => m.Name == "Finalize" && m.Parameters.Count == 0 && m.IsVirtual && m.HasBody && t.FullName != "System.Object");
        if (f != null)
        {
            finByAsm[an] = finByAsm.GetValueOrDefault(an) + 1;
            var calls = f.Body.Instructions.Where(i => i.Operand is MethodReference).Select(i => ((MethodReference)i.Operand).DeclaringType.Name + "::" + ((MethodReference)i.Operand).Name).Distinct().Take(8);
            fin.Add($"FINALIZER {an} {t.FullName}: calls {string.Join(", ", calls)}");
        }
        foreach (var m in t.Methods)
        {
            if (!m.HasBody) continue;
            var ins = m.Body.Instructions;
            for (int k = 0; k < ins.Count; k++)
            {
                var i = ins[k];
                if (i.Operand is not MethodReference mr) continue;
                var dt = mr.DeclaringType; var dn = dt.IsGenericInstance ? ((GenericInstanceType)dt).ElementType.FullName : dt.FullName;
                string where = $"{an} {t.FullName}::{m.Name}";
                // 2. weak references
                if (dn is "System.WeakReference" or "System.WeakReference`1" or "System.Runtime.CompilerServices.ConditionalWeakTable`2")
                {
                    weakByAsm[an] = weakByAsm.GetValueOrDefault(an) + 1;
                    weak.Add($"WEAK {where}: {i.OpCode} {dn}::{mr.Name}");
                }
                if (dn == "System.Runtime.InteropServices.GCHandle" && mr.Name == "Alloc" && mr.Parameters.Count == 2)
                {
                    var prev = k > 0 ? ins[k - 1] : null;
                    int? kind = prev?.OpCode.Code switch { Code.Ldc_I4_0 => 0, Code.Ldc_I4_1 => 1, Code.Ldc_I4_2 => 2, Code.Ldc_I4_3 => 3, _ => null };
                    if (kind is 0 or 1 or null) { weakByAsm[an] = weakByAsm.GetValueOrDefault(an) + 1; weak.Add($"WEAK {where}: GCHandle.Alloc(type {(kind?.ToString() ?? "not a constant")}{(kind is 0 or 1 ? " = weak" : "")})"); }
                }
                // 3. the GC's API
                if (dn == "System.GC")
                {
                    gcByAsm[an] = gcByAsm.GetValueOrDefault(an) + 1; gcByName[mr.Name] = gcByName.GetValueOrDefault(mr.Name) + 1;
                    gcapi.Add($"GCAPI {where}: GC.{mr.Name}");
                }
                // 4. identity hashes
                if (dn == "System.Runtime.CompilerServices.RuntimeHelpers" && mr.Name == "GetHashCode") idh.Add($"IDHASH {where}: RuntimeHelpers.GetHashCode");
                if (dn == "System.Object" && mr.Name == "GetHashCode" && i.OpCode.Code == Code.Callvirt)
                {
                    // the static receiver type: the nearest constrained. prefix, or unknown
                    var recv = k > 0 && ins[k - 1].OpCode.Code == Code.Constrained ? (TypeReference)ins[k - 1].Operand : null;
                    if (recv == null || !recv.IsValueType) idh.Add($"OBJHASH {where}: callvirt Object.GetHashCode{(recv != null ? " on " + recv.FullName : "")}");
                }
                // review F10: hash-ordered containers -- constructions (newobj, or the Immutable factories) and enumeration sites
                if (hashOrderedTypes.Contains(dn))
                {
                    bool isNew = i.OpCode.Code == Code.Newobj || (dn.StartsWith("System.Collections.Immutable.") && (mr.Name.StartsWith("Create") || mr.Name.StartsWith("To")));
                    if (isNew)
                    {
                        bool cmp = mr.Parameters.Any(p => p.ParameterType.Name.StartsWith("IEqualityComparer") || p.ParameterType.Name.StartsWith("IHashCodeProvider"));
                        string hk;
                        if (cmp) hk = "custom comparer";
                        else if (dt is GenericInstanceType hg) hk = HashKind(hg.GenericArguments[0]);
                        else if (mr is GenericInstanceMethod gm && gm.GenericArguments.Count > 0) hk = HashKind(gm.GenericArguments[0]);
                        else hk = "object keys (non-generic: depends on what is stored)";
                        hashOrdByType[dn] = hashOrdByType.GetValueOrDefault(dn) + 1;
                        hashOrd.Add($"HASHORD {where}: {(i.OpCode.Code == Code.Newobj ? "new" : mr.Name)} {dt.Name} ({hk})");
                    }
                    else if (enumMembers.Contains(mr.Name))
                        hashOrdEnum.Add($"HASHENUM {where}: {dt.Name}::{mr.Name}");
                }
                // Dictionary / HashSet keys
                if (i.OpCode.Code == Code.Newobj && dt is GenericInstanceType git && (dn.StartsWith("System.Collections.Generic.Dictionary`2") || dn.StartsWith("System.Collections.Generic.HashSet`1") || dn.StartsWith("System.Collections.Concurrent.ConcurrentDictionary`2")))
                {
                    // a comparer argument changes the hash: note it
                    bool cmp = mr.Parameters.Any(p => p.ParameterType.Name.StartsWith("IEqualityComparer"));
                    var key = git.GenericArguments[0];
                    var hk = cmp ? "custom comparer" : HashKind(key);
                    var kn = key.FullName + " -> " + hk;
                    var e = keyTypes.GetValueOrDefault(kn); keyTypes[kn] = (e.n + 1, hk);
                    if (hk.Contains("IDENTITY") || hk.Contains("ARRAY") || hk.StartsWith("?") || hk.StartsWith("interface")) keys.Add($"KEY {where}: new {dt.Name}<{key.FullName}> ({hk})");
                }
            }
        }
    }
}
void Sec(string title, List<string> l, Dictionary<string, int> by = null)
{
    w.WriteLine(); w.WriteLine($"## {title}: {l.Count}" + (by != null ? " (" + string.Join(", ", by.OrderByDescending(x => x.Value).Select(x => $"{x.Key} {x.Value}")) + ")" : ""));
    foreach (var s in l) w.WriteLine(s);
}
w.WriteLine();
w.WriteLine($"SUMMARY finalizers {fin.Count} | weak-reference sites {weak.Count} | GC API calls {gcapi.Count} ({string.Join(", ", gcByName.Select(x => $"{x.Key} {x.Value}"))}) | RuntimeHelpers/Object.GetHashCode sites {idh.Count} | Dictionary/HashSet constructions with an identity-hashed or unknown key {keys.Count} | hash-ordered containers constructed {hashOrd.Count} ({string.Join(", ", hashOrdByType.Select(x => $"{x.Key} {x.Value}"))}), their enumeration call sites {hashOrdEnum.Count}");
Sec("1. finalizers", fin, finByAsm);
Sec("2. weak references", weak, weakByAsm);
Sec("3. the GC's API", gcapi, gcByAsm);
Sec("4a. identity hash calls", idh);
Sec("4b. Dictionary / HashSet keys hashed by identity (or unknown)", keys);
Sec("4d. hash-ordered containers (enumeration in bucket order = hash order): constructions", hashOrd);
Sec("4e. their enumeration call sites (a direct call on the container type; through an interface it is not seen)", hashOrdEnum);
w.WriteLine(); w.WriteLine("## 4c. every Dictionary / HashSet key type constructed (count, hash)");
foreach (var kv in keyTypes.OrderByDescending(x => x.Value.n)) w.WriteLine($"KEYTYPE {kv.Value.n,5} {kv.Key}");
w.Close();
Console.WriteLine($"finalizers {fin.Count}, weak {weak.Count}, GC API {gcapi.Count}, id-hash calls {idh.Count}, identity/unknown keys {keys.Count}, hash-ordered constructions {hashOrd.Count} / enumeration sites {hashOrdEnum.Count} -> {outPath}");
