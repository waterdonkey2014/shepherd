// Rewrites every Unity native hook (InternalCall) into managed IL that forwards to Shepherd.Hooks.Icalls.Call(id, args).
// Usage: Rewriter <ManagedDir> <outDir> [--vita]   -> rewritten DLLs + icalls.json manifest (id, key, signature).
//   --vita adds pass 3 (FMOD P/Invokes -> engine hooks) for the Vita profile. Emit it into its own dir (runtime/ManagedVita):
//   without --vita the output is byte-for-byte what it always was (runtime/Managed keeps real FMOD on the Mac).
using Mono.Cecil; using Mono.Cecil.Cil; using Mono.Cecil.Rocks; using System.Text.Json;

var managed = args[0]; var outDir = args[1]; Directory.CreateDirectory(outDir);
bool vita = args.Skip(2).Contains("--vita");
var resolver = new DefaultAssemblyResolver(); resolver.AddSearchDirectory(managed);
var rp = new ReaderParameters { AssemblyResolver = resolver, ReadingMode = ReadingMode.Immediate, ReadSymbols = false };
bool IsBcl(string n) => n is "mscorlib" or "netstandard" || n.StartsWith("System") || n.StartsWith("Mono.") || n.StartsWith("Microsoft.");

// Pass 1: collect every icall in a stable order so ids are deterministic.
var mods = new List<(string file, AssemblyDefinition asm)>();
foreach (var f in Directory.GetFiles(managed, "*.dll").OrderBy(x => x, StringComparer.Ordinal)) {
    var n = Path.GetFileNameWithoutExtension(f); if (IsBcl(n)) continue;
    AssemblyDefinition a; try { a = AssemblyDefinition.ReadAssembly(f, rp); } catch { continue; }
    if (a.MainModule.GetTypes().SelectMany(t => t.Methods).Any(m => m.IsInternalCall && !m.HasBody)) mods.Add((f, a));
}
var icalls = new List<(AssemblyDefinition asm, MethodDefinition m)>();
foreach (var (_, a) in mods)
    foreach (var t in a.MainModule.GetTypes().OrderBy(t => t.FullName, StringComparer.Ordinal))
        foreach (var m in t.Methods.Where(m => m.IsInternalCall && !m.HasBody).OrderBy(m => m.Name, StringComparer.Ordinal).ThenBy(m => m.FullName, StringComparer.Ordinal))
            icalls.Add((a, m));
string Base(MethodDefinition m) => m.DeclaringType.FullName + "::" + m.Name;
var dup = icalls.GroupBy(x => Base(x.m)).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();
string Sig(MethodDefinition m) => "(" + string.Join(",", m.Parameters.Select(p => p.ParameterType.FullName)) + ")";
string Key(MethodDefinition m) => dup.Contains(Base(m)) ? Base(m) + Sig(m) : Base(m);

bool IsByRefLike(TypeReference t) {
    if (t is GenericParameter || t is ArrayType || t is PointerType) return false;
    try { var d = t.Resolve(); return d != null && d.IsValueType && d.CustomAttributes.Any(c => c.AttributeType.Name == "IsByRefLikeAttribute"); } catch { return false; }
}
bool NeedsBox(TypeReference t) => t.IsValueType || t is GenericParameter || (t.Resolve()?.IsValueType ?? false);

var manifest = new List<object>(); int unsupported = 0;
// Emits `args = new object[n] {this?, p0, p1...}; res = Icalls.Call(id, args); <write back ref/out>; return Icalls.Ret<T>(res);` as m's body.
// Shared by pass 1 (icalls) and pass 3 (P/Invokes); returns the manifest row.
object Forward(AssemblyDefinition asm, MethodDefinition m, int id, string key, string native) {
    var mod = asm.MainModule; var ts = mod.TypeSystem;
    var hooksRef = mod.AssemblyReferences.FirstOrDefault(r => r.Name == "Shepherd.Hooks");
    if (hooksRef == null) { hooksRef = new AssemblyNameReference("Shepherd.Hooks", new Version(1, 0, 0, 0)); mod.AssemblyReferences.Add(hooksRef); }
    var icallsT = new TypeReference("Shepherd.Hooks", "Icalls", mod, hooksRef);
    var callRef = new MethodReference("Call", ts.Object, icallsT) { HasThis = false };
    callRef.Parameters.Add(new ParameterDefinition(ts.Int32)); callRef.Parameters.Add(new ParameterDefinition(new ArrayType(ts.Object)));
    var retOpen = new MethodReference("Ret", ts.Void, icallsT) { HasThis = false };
    var gp = new GenericParameter("T", retOpen); retOpen.GenericParameters.Add(gp); retOpen.ReturnType = gp; retOpen.Parameters.Add(new ParameterDefinition(ts.Object));
    MethodReference RetOf(TypeReference t) { var g = new GenericInstanceMethod(retOpen); g.GenericArguments.Add(t); return g; }

    m.ImplAttributes = MethodImplAttributes.IL | MethodImplAttributes.Managed;
    var body = new MethodBody(m) { InitLocals = true }; m.Body = body; var il = body.GetILProcessor();
    var argsLoc = new VariableDefinition(new ArrayType(ts.Object)); var resLoc = new VariableDefinition(ts.Object);
    body.Variables.Add(argsLoc); body.Variables.Add(resLoc);
    int off = m.IsStatic ? 0 : 1, n = off + m.Parameters.Count;
    il.Emit(OpCodes.Ldc_I4, n); il.Emit(OpCodes.Newarr, ts.Object); il.Emit(OpCodes.Stloc, argsLoc);
    if (!m.IsStatic) {
        var self = m.DeclaringType.HasGenericParameters ? (TypeReference)m.DeclaringType.MakeGenericInstanceType(m.DeclaringType.GenericParameters.ToArray()) : m.DeclaringType;
        il.Emit(OpCodes.Ldloc, argsLoc); il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Ldarg_0);
        if (m.DeclaringType.IsValueType) { if (IsByRefLike(m.DeclaringType)) { il.Emit(OpCodes.Pop); il.Emit(OpCodes.Ldnull); } else { il.Emit(OpCodes.Ldobj, self); il.Emit(OpCodes.Box, self); } }
        il.Emit(OpCodes.Stelem_Ref);
    }
    var pinfo = new List<object>(); bool bad = false;
    for (int k = 0; k < m.Parameters.Count; k++) {
        var p = m.Parameters[k]; var pt = p.ParameterType; int slot = off + k;
        il.Emit(OpCodes.Ldloc, argsLoc); il.Emit(OpCodes.Ldc_I4, slot);
        if (pt is ByReferenceType br) {
            var e = br.ElementType;
            if (IsByRefLike(e) || (p.IsOut && !p.IsIn)) il.Emit(OpCodes.Ldnull);
            else { il.Emit(OpCodes.Ldarg, p); il.Emit(OpCodes.Ldobj, e); if (NeedsBox(e)) il.Emit(OpCodes.Box, e); }
        } else if (pt is PointerType || pt is FunctionPointerType) { il.Emit(OpCodes.Ldarg, p); il.Emit(OpCodes.Box, ts.IntPtr); }
        else if (IsByRefLike(pt)) { il.Emit(OpCodes.Ldnull); bad = true; }
        else { il.Emit(OpCodes.Ldarg, p); if (NeedsBox(pt)) il.Emit(OpCodes.Box, pt); }
        il.Emit(OpCodes.Stelem_Ref);
        pinfo.Add(new { name = p.Name, type = pt.FullName, byref = pt is ByReferenceType, @out = p.IsOut, @in = p.IsIn });
    }
    il.Emit(OpCodes.Ldc_I4, id); il.Emit(OpCodes.Ldloc, argsLoc); il.Emit(OpCodes.Call, callRef); il.Emit(OpCodes.Stloc, resLoc);
    for (int k = 0; k < m.Parameters.Count; k++) {   // write back ref/out (not `in`)
        var p = m.Parameters[k]; if (p.ParameterType is not ByReferenceType br || (p.IsIn && !p.IsOut) || IsByRefLike(br.ElementType)) continue;
        if (p.CustomAttributes.Any(c => c.AttributeType.Name == "IsReadOnlyAttribute")) continue;
        il.Emit(OpCodes.Ldarg, p); il.Emit(OpCodes.Ldloc, argsLoc); il.Emit(OpCodes.Ldc_I4, off + k); il.Emit(OpCodes.Ldelem_Ref);
        il.Emit(OpCodes.Call, RetOf(br.ElementType)); il.Emit(OpCodes.Stobj, br.ElementType);
    }
    var rt = m.ReturnType;
    if (rt.MetadataType == MetadataType.Void) il.Emit(OpCodes.Ret);
    else if (rt is ByReferenceType || IsByRefLike(rt)) {
        bad = true; var exT = new TypeReference("System", "NotSupportedException", mod, ts.CoreLibrary);
        il.Emit(OpCodes.Newobj, new MethodReference(".ctor", ts.Void, exT) { HasThis = true }); il.Emit(OpCodes.Throw);
    } else if (rt is PointerType || rt is FunctionPointerType) { il.Emit(OpCodes.Ldloc, resLoc); il.Emit(OpCodes.Call, RetOf(ts.IntPtr)); il.Emit(OpCodes.Ret); }
    else { il.Emit(OpCodes.Ldloc, resLoc); il.Emit(OpCodes.Call, RetOf(rt)); il.Emit(OpCodes.Ret); }
    if (bad) unsupported++;
    if (native != null)   // P/Invoke rows carry the library name; every other field has the icall row's meaning
        return new { id, asm = asm.Name.Name, key, type = m.DeclaringType.FullName, name = m.Name, @static = m.IsStatic,
                     valueTypeThis = false, @params = pinfo, ret = rt.FullName, unsupported = bad, native };
    return new { id, asm = asm.Name.Name, key, type = m.DeclaringType.FullName, name = m.Name, @static = m.IsStatic,
                 valueTypeThis = !m.IsStatic && m.DeclaringType.IsValueType, @params = pinfo, ret = rt.FullName, unsupported = bad };
}

for (int id = 0; id < icalls.Count; id++) {
    var (asm, m) = icalls[id];
    m.IsInternalCall = false;
    manifest.Add(Forward(asm, m, id, Key(m), null));
}
foreach (var (file, a) in mods) a.Write(Path.Combine(outDir, Path.GetFileName(file)));

// Pass 2: platform SDKs the engine must never touch (Steam, GOG Galaxy): every P/Invoke becomes a managed stub that
// returns default (and zeroes out-params) — "SDK not available", without loading any native library.
foreach (var name in new[] { "com.steamworks.net", "GalaxyCSharp" })
{
    var f = Path.Combine(managed, name + ".dll"); if (!File.Exists(f)) continue;
    var a = AssemblyDefinition.ReadAssembly(f, rp); int n = 0;
    foreach (var m in a.MainModule.GetTypes().SelectMany(t => t.Methods).Where(m => m.IsPInvokeImpl))
    {
        m.PInvokeInfo = null; m.IsPInvokeImpl = false; /* order matters: Cecil's PInvokeInfo setter re-sets the flag */ m.ImplAttributes = MethodImplAttributes.IL | MethodImplAttributes.Managed; m.IsPreserveSig = false;
        var body = new MethodBody(m) { InitLocals = true }; m.Body = body; var il = body.GetILProcessor();
        foreach (var p in m.Parameters)
            if (p.ParameterType is ByReferenceType br && p.IsOut) { il.Emit(OpCodes.Ldarg, p); il.Emit(OpCodes.Initobj, br.ElementType); }
        if (m.ReturnType.MetadataType != MetadataType.Void) { var loc = new VariableDefinition(m.ReturnType); body.Variables.Add(loc); il.Emit(OpCodes.Ldloca, loc); il.Emit(OpCodes.Initobj, m.ReturnType); il.Emit(OpCodes.Ldloc, loc); }
        il.Emit(OpCodes.Ret); n++;
    }
    // Steamworks.NET's "is the SDK up?" guards throw InvalidOperationException; game code calls Steam APIs unguarded
    // (the shipping Mac build assumes Steam), so make the guards no-ops: every call then just returns "not available".
    foreach (var m in a.MainModule.GetTypes().SelectMany(t => t.Methods).Where(m => m.HasBody && m.Name.StartsWith("TestIfAvailable") && m.ReturnType.MetadataType == MetadataType.Void))
    {
        m.Body = new MethodBody(m); m.Body.GetILProcessor().Emit(OpCodes.Ret); n++;
    }
    // Polish round 1: an explicit-layout struct that overlaps an object reference with other fields (Steamworks.NET's
    // SteamDatagramRelayAuthTicket.ExtraField.OptionValue: a byte[] ByValArray at offset 0 under two int64s) loads on Mono -- Unity's
    // runtime, and the Vita's -- but CoreCLR's type loader rejects it, so Assembly.GetTypes() on the SDK throws
    // ReflectionTypeLoadException and every all-assembly scan breaks (Febucci TextAnimator's effect database: no 'size'/'wiggle').
    // Such a struct only describes native marshalling, and nothing here reaches native Steam (every P/Invoke above is a managed
    // stub), so it becomes sequential: the same fields, no overlap, loadable everywhere.
    int relaid = 0;
    foreach (var t in a.MainModule.GetTypes().Where(t => t.IsValueType && t.IsExplicitLayout))
    {
        var refs = t.Fields.Where(fd => !fd.IsStatic && (fd.FieldType.IsArray || fd.FieldType.MetadataType is MetadataType.String or MetadataType.Object or MetadataType.Class)).ToList();
        if (refs.Count == 0) continue;
        var inst = t.Fields.Where(fd => !fd.IsStatic).ToList();
        if (!refs.Any(r => inst.Any(o => o != r && o.Offset == r.Offset))) continue;
        t.IsExplicitLayout = false; t.IsSequentialLayout = true; t.ClassSize = -1; t.PackingSize = -1;
        foreach (var fd in inst) fd.Offset = -1;
        relaid++; Console.WriteLine($"  {name}: {t.FullName}: explicit layout with an overlapped object field -> sequential (CoreCLR type load)");
    }
    a.Write(Path.Combine(outDir, name + ".dll"));
    Console.WriteLine($"  {name}: {n} P/Invokes -> managed 'not available' stubs");
}

// Pass 3 (--vita only): FMOD without the native library. Every P/Invoke in the FMOD Unity integration ("fmodstudio",
// "fmod", ...) becomes the same forwarder as pass 1, appended to the manifest after the icalls (pass-1 ids unchanged) with
// key "<lib>!<entry point>" (+ "(param types)" for overloads). The engine implements them in C# (Shepherd.Audio.Fmod):
// silent but stateful — handles, bank state, instance playback state, STOPPED callbacks. Unimplemented entries return
// default (FMOD_OK, zeroed outs) and show up in missing-icalls.txt, like any other unimplemented hook.
if (vita)
{
    var natives = new List<(AssemblyDefinition asm, MethodDefinition m, string lib, string entry)>();
    var nmods = new List<(string file, AssemblyDefinition asm)>();
    foreach (var name in new[] { "FMODUnity", "FMODUnityResonance" })
    {
        var f = Path.Combine(managed, name + ".dll"); if (!File.Exists(f)) continue;
        var a = AssemblyDefinition.ReadAssembly(f, rp); nmods.Add((f, a));
        foreach (var t in a.MainModule.GetTypes().OrderBy(t => t.FullName, StringComparer.Ordinal))
            foreach (var m in t.Methods.Where(m => m.IsPInvokeImpl && m.PInvokeInfo != null).OrderBy(m => m.Name, StringComparer.Ordinal).ThenBy(m => m.FullName, StringComparer.Ordinal))
                natives.Add((a, m, m.PInvokeInfo.Module.Name, string.IsNullOrEmpty(m.PInvokeInfo.EntryPoint) ? m.Name : m.PInvokeInfo.EntryPoint));
    }
    string NBase((AssemblyDefinition asm, MethodDefinition m, string lib, string entry) x) => x.lib + "!" + x.entry;
    var ndup = natives.GroupBy(NBase).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();
    int nid = icalls.Count;
    foreach (var x in natives)
    {
        var m = x.m;
        m.PInvokeInfo = null; m.IsPInvokeImpl = false; /* order matters, see pass 2 */ m.IsPreserveSig = false;
        manifest.Add(Forward(x.asm, m, nid++, ndup.Contains(NBase(x)) ? NBase(x) + Sig(m) : NBase(x), x.lib));
    }
    foreach (var (file, a) in nmods) if (natives.Any(x => x.asm == a)) a.Write(Path.Combine(outDir, Path.GetFileName(file)));   // only assemblies that changed
    foreach (var g in natives.GroupBy(x => (x.asm.Name.Name, x.lib))) Console.WriteLine($"  {g.Key.Item1}: {g.Count()} '{g.Key.lib}' P/Invokes -> engine hooks (Shepherd.Audio)");
}
// Pass 4 (--spine-share, kit6 mem L1; opt-in, its own flag): the owner's spine-unity.dll with the skeleton reader's finished arrays routed
// through Shepherd.Hooks.SpineShare (SpineSharePass below). Without the flag nothing here runs and every output is what it always was.
// Kit #7 (spine track, vita/notes/kit7-spine.md): more opt-in passes over the same spine-unity.dll, each its own flag -- --spine-lazyskin (L2:
// a skin's attachments read when the skin is first used; needs --spine-share's streamed read) and --spine-clip (SkeletonClipping.ClipTriangles
// through Shepherd.Hooks.SpineClip: the engine's native port). The passes edit one module, then it is written once (SpinePasses.Run); with
// --spine-share alone the output is byte-for-byte kit #6's.
bool spShare = args.Skip(2).Contains("--spine-share"), spLazy = args.Skip(2).Contains("--spine-lazyskin"), spClip = args.Skip(2).Contains("--spine-clip");
if ((spShare || spLazy || spClip) && SpinePasses.Run(managed, outDir, rp, spShare, spLazy, spClip) != 0) { Console.Error.WriteLine("spine passes: refused (see above); spine-unity.dll not written"); Environment.Exit(3); }
File.WriteAllText(Path.Combine(outDir, "icalls.json"), JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = false }));
Console.WriteLine($"rewrote {icalls.Count} icalls in {mods.Count} assemblies ({unsupported} with span/byref-return parts stubbed) -> {outDir}");
foreach (var (file, a) in mods) Console.WriteLine($"  {a.Name.Name}: {a.MainModule.GetTypes().SelectMany(t => t.Methods).Count(m => m.HasBody && m.Body.Instructions.Any(i => i.Operand is MethodReference r && r.Name == "Call" && r.DeclaringType.Name == "Icalls"))}");


/// <summary>kit6 mem, lever L1 (vita/notes/kit6-mem.md): read-time sharing of Spine's skeleton arrays. The reader (Spine.SkeletonBinary)
/// builds every vertex / UV / triangle / weight array, every timeline's key and curve arrays, the deform and draw-order frames and every
/// region's offset / UVs fresh; in the game's skeletons most of them repeat (the intro's two 'Follower' SkeletonDataAssets read the same
/// 20.5 MB file). This pass makes each such array pass through Shepherd.Hooks.SpineShare.F / I at the point where the reader has finished
/// writing it, so the engine can hand back an identical array it already keeps (Runtime/SpineReadShare.cs); the fresh one dies young.
/// Sites (each the last write of that array in the reader -- nothing in spine-unity or the game's assemblies writes these arrays after
/// the read, except into its own copies: Attachment.Copy / NewLinkedMesh / GetRemappedClone / GetRepackedAttachments allocate new arrays):
///   ReadFloatArray / ReadShortArray            the returned array
///   ReadVertices                               the weighted vertices / bones (ExposedList.ToArray results)
///   ReadAttachment, region                     RegionAttachment.UpdateOffset() -> &lt;Shepherd&gt;UpdateOffset: + offset, uvs
///   ReadAttachment, mesh / ReadSkeletonData    MeshAttachment.UpdateUVs() -> &lt;Shepherd&gt;UpdateUVs: + uvs (linked meshes too)
///   ReadAnimation                              each timeline as it is added (&lt;Shepherd&gt;Timeline: every float[] / int[] field of the
///                                              timeline classes: frames, curves), each deform frame's vertices and draw order at SetFrame
///   SkeletonDataAsset.GetSkeletonData          Begin(this) before the parse, End(result) after it (also after a parse that threw);
///                                              the binary read takes a stream from Shepherd.Hooks.SpineShare.Open(skeletonJSON) when the
///                                              engine gives one (L1b: no array of the whole file), else TextAsset.bytes as before
/// The pass checks the exact shape it was written for (every site found the expected number of times, the helpers' fields present) and
/// refuses otherwise. The rewritten module gets a new MVID derived from the original's (an AOT image of the game's own spine-unity can never
/// bind to it). With no engine handler installed every hook returns its argument: the game's own arrays, as before.</summary>
static class SpineSharePass
{
    /// <summary>The edits on the loaded module (kit #7: SpinePasses.Run reads spine-unity.dll, applies the passes asked for and writes it once).</summary>
    public static int Apply(AssemblyDefinition a)
    {
        var mod = a.MainModule; var ts = mod.TypeSystem;
        var errors = new List<string>(); var log = new List<string>();
        TypeDefinition T(string n) { var t = mod.GetType(n); if (t == null) errors.Add("type " + n + " missing"); return t; }
        MethodDefinition M(TypeDefinition t, string n, int pc) { var ms = t?.Methods.Where(m => m.Name == n && m.Parameters.Count == pc && m.HasBody).ToList(); if (ms == null || ms.Count != 1) { errors.Add($"{t?.Name}.{n}/{pc}: {ms?.Count ?? 0} bodies"); return null; } return ms[0]; }
        var sb = T("Spine.SkeletonBinary"); var sda = T("Spine.Unity.SkeletonDataAsset"); var ra = T("Spine.RegionAttachment"); var ma = T("Spine.MeshAttachment");
        var tl = T("Spine.Timeline"); var sdata = T("Spine.SkeletonData");
        if (errors.Count > 0) return Fail(errors);
        if (sb.Methods.Any(m => m.Name.StartsWith("<Shepherd>"))) { errors.Add("already rewritten"); return Fail(errors); }

        // the hooks (Shepherd.Hooks.SpineShare)
        var hooksRef = mod.AssemblyReferences.FirstOrDefault(r => r.Name == "Shepherd.Hooks");
        if (hooksRef == null) { hooksRef = new AssemblyNameReference("Shepherd.Hooks", new Version(1, 0, 0, 0)); mod.AssemblyReferences.Add(hooksRef); }
        var hookT = new TypeReference("Shepherd.Hooks", "SpineShare", mod, hooksRef);
        var floatArr = new ArrayType(ts.Single); var intArr = new ArrayType(ts.Int32);
        MethodReference Hook(string n, TypeReference ret, TypeReference p) { var r = new MethodReference(n, ret, hookT) { HasThis = false }; r.Parameters.Add(new ParameterDefinition(p)); return r; }
        var hF = Hook("F", floatArr, floatArr); var hI = Hook("I", intArr, intArr); var hBegin = Hook("Begin", ts.Void, ts.Object); var hEnd = Hook("End", ts.Object, ts.Object);
        var hOpen = Hook("Open", ts.Object, ts.Object);
        MethodReference HookFor(TypeReference arr) => ((ArrayType)arr).ElementType.MetadataType == MetadataType.Single ? hF : hI;
        bool IsShareable(TypeReference t) => t is ArrayType at && at.Rank == 1 && at.ElementType.MetadataType is MetadataType.Single or MetadataType.Int32;

        // helper 1: <Shepherd>Timeline(Timeline) -- every internal float[] / int[] instance field declared by a class implementing Spine.Timeline
        // (or a base class of one), interned once the reader has added the timeline (all its SetFrame / SetCurve writes done)
        var tlTypes = mod.Types.Where(t => t.Namespace == "Spine" && t.IsClass && Implements(t, tl)).ToList();
        var tlFields = new List<FieldDefinition>();
        foreach (var t in tlTypes) for (var c = t; c != null && c.Namespace == "Spine"; c = c.BaseType?.Resolve())
            foreach (var fd in c.Fields.Where(x => !x.IsStatic && IsShareable(x.FieldType) && (x.IsAssembly || x.IsPublic || x.IsFamilyOrAssembly)))
                if (!tlFields.Contains(fd)) tlFields.Add(fd);
        foreach (var t in tlTypes) foreach (var fd in t.Fields.Where(x => !x.IsStatic && IsShareable(x.FieldType) && x.IsPrivate)) errors.Add($"{t.Name}.{fd.Name} is private (not reachable from the helper)");
        if (tlFields.Count == 0) errors.Add("no timeline array fields");
        var mT = new MethodDefinition("<Shepherd>Timeline", MethodAttributes.Private | MethodAttributes.Static | MethodAttributes.HideBySig, tl);
        mT.Parameters.Add(new ParameterDefinition("t", ParameterAttributes.None, tl));
        { var il = mT.Body.GetILProcessor(); mT.Body.InitLocals = true;
          foreach (var g in tlFields.GroupBy(x => x.DeclaringType))
          {
              var loc = new VariableDefinition(g.Key); mT.Body.Variables.Add(loc); var next = il.Create(OpCodes.Nop);
              il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Isinst, g.Key); il.Emit(OpCodes.Stloc, loc); il.Emit(OpCodes.Ldloc, loc); il.Emit(OpCodes.Brfalse, next);
              foreach (var fd in g) { il.Emit(OpCodes.Ldloc, loc); il.Emit(OpCodes.Ldloc, loc); il.Emit(OpCodes.Ldfld, fd); il.Emit(OpCodes.Call, HookFor(fd.FieldType)); il.Emit(OpCodes.Stfld, fd); }
              il.Append(next);
          }
          il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ret); }
        // helper 2 / 3: the region / mesh calls that finish an attachment's arrays, then those arrays through the hook
        FieldDefinition Fd(TypeDefinition t, string n) { var x = t.Fields.FirstOrDefault(y => y.Name == n && !y.IsStatic && IsShareable(y.FieldType) && !y.IsPrivate); if (x == null) errors.Add($"{t.Name}.{n} missing / not reachable"); return x; }
        MethodDefinition Wrap(string name, TypeDefinition t, string call, params FieldDefinition[] fields)
        {
            var target = t.Methods.FirstOrDefault(m => m.Name == call && m.Parameters.Count == 0 && !m.IsStatic);
            if (target == null) { errors.Add($"{t.Name}.{call}() missing"); return null; }
            var m = new MethodDefinition(name, MethodAttributes.Private | MethodAttributes.Static | MethodAttributes.HideBySig, ts.Void);
            m.Parameters.Add(new ParameterDefinition("a", ParameterAttributes.None, t));
            var il = m.Body.GetILProcessor();
            il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Callvirt, target);   // as the reader called it (a null instance throws as before)
            foreach (var fd in fields) { il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldfld, fd); il.Emit(OpCodes.Call, HookFor(fd.FieldType)); il.Emit(OpCodes.Stfld, fd); }
            il.Emit(OpCodes.Ret); return m;
        }
        var mR = Wrap("<Shepherd>UpdateOffset", ra, "UpdateOffset", Fd(ra, "offset"), Fd(ra, "uvs"));
        var mM = Wrap("<Shepherd>UpdateUVs", ma, "UpdateUVs", Fd(ma, "uvs"));
        if (errors.Count > 0) return Fail(errors);
        sb.Methods.Add(mT); sb.Methods.Add(mR); sb.Methods.Add(mM);

        // the sites (counts are the build this pass was written for; any other count refuses)
        var counts = new Dictionary<string, int>();
        void Count(string k) => counts[k] = counts.GetValueOrDefault(k) + 1;
        void Expect(string k, int n) { if (counts.GetValueOrDefault(k) != n) errors.Add($"site '{k}': {counts.GetValueOrDefault(k)} found, {n} expected"); }
        bool IsCall(Instruction i, string type, string name, int pc) => (i.OpCode == OpCodes.Call || i.OpCode == OpCodes.Callvirt) && i.Operand is MethodReference r && r.Name == name && r.Parameters.Count == pc && (r.DeclaringType.FullName == type || r.DeclaringType.GetElementType().FullName == type);
        void Edit(MethodDefinition m, Action<Mono.Cecil.Cil.MethodBody, ILProcessor> e) { if (m == null) return; m.Body.SimplifyMacros(); e(m.Body, m.Body.GetILProcessor()); m.Body.OptimizeMacros(); }

        Edit(M(sb, "ReadFloatArray", 3), (b, il) => { foreach (var r in b.Instructions.Where(i => i.OpCode == OpCodes.Ret).ToList()) { il.InsertBefore(r, il.Create(OpCodes.Call, hF)); Count("ReadFloatArray ret"); } });
        Edit(M(sb, "ReadShortArray", 1), (b, il) => { foreach (var r in b.Instructions.Where(i => i.OpCode == OpCodes.Ret).ToList()) { il.InsertBefore(r, il.Create(OpCodes.Call, hI)); Count("ReadShortArray ret"); } });
        Edit(M(sb, "ReadVertices", 2), (b, il) =>
        {
            foreach (var c in b.Instructions.Where(i => IsCall(i, "Spine.ExposedList`1", "ToArray", 0)).ToList())
            {
                var et = ((GenericInstanceType)((MethodReference)c.Operand).DeclaringType).GenericArguments[0].MetadataType;
                if (et == MetadataType.Single) { il.InsertAfter(c, il.Create(OpCodes.Call, hF)); Count("ReadVertices float ToArray"); }
                else if (et == MetadataType.Int32) { il.InsertAfter(c, il.Create(OpCodes.Call, hI)); Count("ReadVertices int ToArray"); }
            }
        });
        void Replace(MethodDefinition m, string type, string name, MethodDefinition with, string key) =>
            Edit(m, (b, il) => { foreach (var c in b.Instructions.Where(i => IsCall(i, type, name, 0)).ToList()) { il.Replace(c, il.Create(OpCodes.Call, with)); Count(key); } });
        var readAttachment = M(sb, "ReadAttachment", 6); var readSkeletonData = sb.Methods.Where(m => m.Name == "ReadSkeletonData" && m.Parameters.Count == 1 && m.Parameters[0].ParameterType.FullName == "System.IO.Stream").ToList();
        if (readSkeletonData.Count != 1) errors.Add("SkeletonBinary.ReadSkeletonData(Stream) not found once");
        Replace(readAttachment, "Spine.RegionAttachment", "UpdateOffset", mR, "ReadAttachment UpdateOffset");
        Replace(readAttachment, "Spine.MeshAttachment", "UpdateUVs", mM, "ReadAttachment UpdateUVs");
        if (readSkeletonData.Count == 1) Replace(readSkeletonData[0], "Spine.MeshAttachment", "UpdateUVs", mM, "ReadSkeletonData UpdateUVs (linked meshes)");
        Edit(M(sb, "ReadAnimation", 3), (b, il) =>
        {
            foreach (var c in b.Instructions.ToList())
            {
                if (IsCall(c, "Spine.ExposedList`1", "Add", 1) && ((GenericInstanceType)((MethodReference)c.Operand).DeclaringType).GenericArguments[0].FullName == "Spine.Timeline")
                { il.InsertBefore(c, il.Create(OpCodes.Call, mT)); Count("ReadAnimation timelines.Add"); }
                else if (IsCall(c, "Spine.DeformTimeline", "SetFrame", 3) && IsShareable(((MethodReference)c.Operand).Parameters[2].ParameterType))
                { il.InsertBefore(c, il.Create(OpCodes.Call, hF)); Count("ReadAnimation DeformTimeline.SetFrame"); }
                else if (IsCall(c, "Spine.DrawOrderTimeline", "SetFrame", 3) && IsShareable(((MethodReference)c.Operand).Parameters[2].ParameterType))
                { il.InsertBefore(c, il.Create(OpCodes.Call, hI)); Count("ReadAnimation DrawOrderTimeline.SetFrame"); }
            }
        });
        // GetSkeletonData's binary read (L1b): `ReadSkeletonData(skeletonJSON.bytes, loader, scale)` becomes `<Shepherd>ReadSkeletonData(skeletonJSON,
        // loader, scale)`: a stream from Shepherd.Hooks.SpineShare.Open(textAsset) when the engine gives one (the same bytes in the same order,
        // never one array of the whole file), else exactly the original call on TextAsset.bytes
        Edit(M(sda, "GetSkeletonData", 1), (b, il) =>
        {
            var rsdBytes = sda.Methods.Where(m => m.Name == "ReadSkeletonData" && m.IsStatic && m.Parameters.Count == 3 && m.Parameters[0].ParameterType is ArrayType at && at.ElementType.MetadataType == MetadataType.Byte).ToList();
            var calls = b.Instructions.Where(i => i.OpCode == OpCodes.Call && i.Operand is MethodReference r && rsdBytes.Count == 1 && r.Resolve() == rsdBytes[0]).ToList();
            if (rsdBytes.Count != 1 || calls.Count != 1) { errors.Add($"GetSkeletonData: {calls.Count} binary parse calls"); return; }
            var c = calls[0]; var getBytes = c.Previous?.Previous?.Previous;
            if (getBytes == null || !IsCall(getBytes, "UnityEngine.TextAsset", "get_bytes", 0) || c.Previous.OpCode != OpCodes.Ldloc || c.Previous.Previous.OpCode != OpCodes.Ldloc
                || b.Instructions.Any(i => i.Operand == getBytes || i.Operand == c.Previous || i.Operand == c.Previous.Previous) || b.ExceptionHandlers.Any(h => h.TryStart == getBytes || h.HandlerStart == getBytes || h.TryEnd == getBytes || h.HandlerEnd == getBytes))
            { errors.Add("GetSkeletonData: the binary parse is not `skeletonJSON.bytes, loader, scale`"); return; }
            var sbCtor = sb.Methods.FirstOrDefault(m => m.IsConstructor && !m.IsStatic && m.Parameters.Count == 1 && m.Parameters[0].ParameterType.FullName == "Spine.AttachmentLoader");
            var setScale = sb.Properties.FirstOrDefault(x => x.Name == "Scale")?.SetMethod;
            var rsdStream = sb.Methods.Where(m => m.Name == "ReadSkeletonData" && m.Parameters.Count == 1 && m.Parameters[0].ParameterType.FullName == "System.IO.Stream").ToList();
            if (sbCtor == null || setScale == null || rsdStream.Count != 1) { errors.Add("SkeletonBinary(AttachmentLoader) / Scale / ReadSkeletonData(Stream) not found"); return; }
            var streamT = rsdStream[0].Parameters[0].ParameterType;   // spine-unity's own reference to Stream
            var m2 = new MethodDefinition("<Shepherd>ReadSkeletonData", MethodAttributes.Private | MethodAttributes.Static | MethodAttributes.HideBySig, rsdBytes[0].ReturnType);
            foreach (var pd in rsdBytes[0].Parameters) m2.Parameters.Add(new ParameterDefinition(pd.Name, ParameterAttributes.None, pd.ParameterType));
            m2.Parameters[0].ParameterType = ((MethodReference)getBytes.Operand).DeclaringType; m2.Parameters[0].Name = "textAsset";
            var st = new VariableDefinition(streamT); m2.Body.Variables.Add(st); m2.Body.InitLocals = true;
            var p2 = m2.Body.GetILProcessor(); var streamPath = p2.Create(OpCodes.Ldarg_1);
            p2.Emit(OpCodes.Ldarg_0); p2.Emit(OpCodes.Call, hOpen); p2.Emit(OpCodes.Isinst, streamT); p2.Emit(OpCodes.Stloc, st);
            p2.Emit(OpCodes.Ldloc, st); p2.Emit(OpCodes.Brtrue, streamPath);
            p2.Emit(OpCodes.Ldarg_0); p2.Emit(OpCodes.Callvirt, (MethodReference)getBytes.Operand); p2.Emit(OpCodes.Ldarg_1); p2.Emit(OpCodes.Ldarg_2); p2.Emit(OpCodes.Call, rsdBytes[0]); p2.Emit(OpCodes.Ret);
            p2.Append(streamPath); p2.Emit(OpCodes.Newobj, sbCtor); p2.Emit(OpCodes.Dup); p2.Emit(OpCodes.Ldarg_2); p2.Emit(OpCodes.Callvirt, setScale);
            p2.Emit(OpCodes.Ldloc, st); p2.Emit(OpCodes.Callvirt, rsdStream[0]); p2.Emit(OpCodes.Ret);
            sda.Methods.Add(m2);
            il.Remove(getBytes); il.Replace(c, il.Create(OpCodes.Call, m2));
            Count("GetSkeletonData streamed binary read");
        });
        // GetSkeletonData: Begin(this) just before the try that parses, End(result) at the try/catch's join (the load of the result local)
        Edit(M(sda, "GetSkeletonData", 1), (b, il) =>
        {
            var reads = b.Instructions.Where(i => i.OpCode == OpCodes.Call && i.Operand is MethodReference r && r.Name is "ReadSkeletonData" or "<Shepherd>ReadSkeletonData" && r.DeclaringType.FullName == sda.FullName).ToList();
            var eh = b.ExceptionHandlers.Where(h => h.HandlerType == ExceptionHandlerType.Catch && reads.All(rd => rd.Offset >= h.TryStart.Offset && rd.Offset < h.TryEnd.Offset)).ToList();
            if (reads.Count != 2 || eh.Count != 1) { errors.Add($"GetSkeletonData: {reads.Count} parse calls, {eh.Count} catch blocks around them"); return; }
            var h = eh[0]; var join = h.HandlerEnd;
            var resLocals = reads.Select(rd => rd.Next).Where(n => n.OpCode == OpCodes.Stloc).Select(n => n.Operand).Distinct().ToList();
            if (resLocals.Count != 1 || join.OpCode != OpCodes.Ldloc || join.Operand != resLocals[0]) { errors.Add("GetSkeletonData: the result local is not what the try/catch join loads"); return; }
            if (b.Instructions.Any(i => i.Operand == h.TryStart && i.OpCode.FlowControl is FlowControl.Branch or FlowControl.Cond_Branch) || b.ExceptionHandlers.Any(x => x != h && (x.HandlerEnd == h.TryStart || x.TryEnd == h.TryStart))) { errors.Add("GetSkeletonData: something jumps to the try start"); return; }
            il.InsertBefore(h.TryStart, il.Create(OpCodes.Ldarg_0)); il.InsertBefore(h.TryStart, il.Create(OpCodes.Call, hBegin));   // straight-line code before the try
            var cast = il.Create(OpCodes.Castclass, sdata); il.InsertAfter(join, cast); il.InsertBefore(cast, il.Create(OpCodes.Call, hEnd));
            Count("GetSkeletonData Begin/End");
        });
        Expect("ReadFloatArray ret", 1); Expect("ReadShortArray ret", 1); Expect("ReadVertices float ToArray", 1); Expect("ReadVertices int ToArray", 1);
        Expect("ReadAttachment UpdateOffset", 1); Expect("ReadAttachment UpdateUVs", 1); Expect("ReadSkeletonData UpdateUVs (linked meshes)", 1);
        Expect("ReadAnimation timelines.Add", 12); Expect("ReadAnimation DeformTimeline.SetFrame", 1); Expect("ReadAnimation DrawOrderTimeline.SetFrame", 1); Expect("GetSkeletonData Begin/End", 1); Expect("GetSkeletonData streamed binary read", 1);
        if (errors.Count > 0) return Fail(errors);

        Console.WriteLine($"  spine-unity: --spine-share: {string.Join("; ", counts.Select(kv => kv.Key + " x" + kv.Value))}; timeline fields: {string.Join(", ", tlFields.Select(x => x.DeclaringType.Name + "." + x.Name))}");
        return 0;
    }
    static bool Implements(TypeDefinition t, TypeDefinition itf)
    {
        for (var c = t; c != null; c = c.BaseType?.Resolve()) if (c.Interfaces.Any(i => i.InterfaceType.FullName == itf.FullName)) return true;
        return false;
    }
    static int Fail(List<string> e) { foreach (var x in e) Console.Error.WriteLine("  spine-unity --spine-share: " + x); return 2; }
}
