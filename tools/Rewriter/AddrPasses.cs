// Device kit #14, track small (vita/notes/kit14-small.md): the owner's Unity.Addressables.dll pass --addr-lazy (the game catalog kept out of
// memory until asked for; Shepherd.Hooks AddrLazy; the engine's Runtime/K14Catalog.cs). The game's own DLL on disk is never touched (the
// output goes to the Rewriter's out dir, owner-local like the rest of runtime/ManagedVita); the pass checks the exact IL shape of
// ContentCatalogData.CreateLocator it was written for (the owner-local call sequence below) and refuses any other build; with no engine handler
// installed every hook behaves as the original code (the hooks answer null / do nothing).
// Cecil writes the type and method tables in its own order, so METADATA TOKENS MOVE. The token-keyed lists of the game-AOT apps (kit #10's learned
// list, the ran list) must be re-keyed for the rewritten DLL: Rewriter <orig dir> <new dir> --rekey-tokens <in> <out> [<in> <out> ...] maps every
// Unity.Addressables token by the method's / type's full name (an unmapped token is an error). The new code lives in one new type at the end of
// the module; it needs ResourceLocationMap.locations, ContentCatalogData.m_InternalIdPrefixes and the nested CompactLocation made internal
// (attributes only).
//   Rewriter <dir holding Unity.Addressables.dll> <out dir> --only-addr-lazy   -> <out dir>/Unity.Addressables.dll (this pass alone)
//   Rewriter <ManagedDir> <outDir> [--vita] ... --addr-lazy                    -> the same file, next to the full run's output
using Mono.Cecil; using Mono.Cecil.Cil; using Mono.Cecil.Rocks;

static class AddrPasses
{
    const string MapName = "UnityEngine.AddressableAssets.ResourceLocators.ResourceLocationMap";
    const string CcdName = "UnityEngine.AddressableAssets.ResourceLocators.ContentCatalogData";

    // The pass was written for one build of ContentCatalogData.CreateLocator: the method's exact call sequence is checked below. That sequence comes from a decompile, so it is
    // owner-local (engine/Derived/tools/AddrLocatorCalls.cs, compiled in by Rewriter.csproj when the file exists): a checkout without it refuses every build, the DLL is not written.
#if SHEPHERD_DERIVED_ADDR
    static readonly string[] CreateLocatorCalls = AddrLocatorCalls.Sequence;
#else
    static readonly string[] CreateLocatorCalls = null;
#endif

    public static int Run(string managed, string outDir, ReaderParameters rp)
    {
        var f = Path.Combine(managed, "Unity.Addressables.dll");
        if (!File.Exists(f)) { Console.Error.WriteLine("--addr-lazy: no Unity.Addressables.dll in " + managed); return 1; }
        var a = AssemblyDefinition.ReadAssembly(f, rp); var mod = a.MainModule;
        if (Apply(a) != 0) return 2;
        var old = mod.Mvid; mod.Mvid = Guid.Empty; var ms = new MemoryStream(); a.Write(ms);
        var h2 = System.Security.Cryptography.SHA256.HashData(old.ToByteArray().Concat(ms.ToArray()).ToArray());
        mod.Mvid = new Guid(h2.AsSpan(0, 16));   // a new identity (an owner AOT image is bound to the MVID it was compiled from)
        Directory.CreateDirectory(outDir);
        var o = Path.Combine(outDir, "Unity.Addressables.dll");
        a.Write(o);
        Console.WriteLine($"  Unity.Addressables: --addr-lazy; MVID {old} -> {mod.Mvid}");
        return 0;
    }

    /// <summary>--rekey-tokens: a token-keyed list (kit #10's learned list, a ran list) for the rewritten Unity.Addressables.dll: every
    /// `Unity.Addressables!<token>` and every <token> column after a `Unity.Addressables` column, mapped by full name. An unmapped token = rc 2.</summary>
    public static int Rekey(string origDir, string newDir, ReaderParameters rp, string[] pairs)
    {
        var o = AssemblyDefinition.ReadAssembly(Path.Combine(origDir, "Unity.Addressables.dll"), rp).MainModule;
        var n = AssemblyDefinition.ReadAssembly(Path.Combine(newDir, "Unity.Addressables.dll"), rp).MainModule;
        Dictionary<string, uint> Tokens(ModuleDefinition mod)
        {
            var d = new Dictionary<string, uint>();
            foreach (var t in mod.GetTypes())
            {
                d["T " + t.FullName] = t.MetadataToken.ToUInt32();
                foreach (var m in t.Methods) d["M " + m.FullName] = m.MetadataToken.ToUInt32();
            }
            return d;
        }
        var a = Tokens(o); var b = Tokens(n); var map = new Dictionary<string, string>();
        foreach (var kv in a) { if (!b.TryGetValue(kv.Key, out var t)) { Console.Error.WriteLine("--rekey-tokens: " + kv.Key + " is not in the new DLL"); return 2; } map[kv.Value.ToString("x8")] = t.ToString("x8"); }
        for (int i = 0; i + 1 < pairs.Length; i += 2)
        {
            int lines = 0, moved = 0, bad = 0; var res = new List<string>();
            string Map(string tok) { if (!map.TryGetValue(tok, out var x)) { bad++; return tok; } if (x != tok) moved++; return x; }
            foreach (var l in File.ReadAllLines(pairs[i]))
            {
                if (l.StartsWith("#") || !l.Contains("Unity.Addressables")) { res.Add(l); continue; }
                lines++;
                var c = l.Split('\t');
                for (int k = 0; k + 1 < c.Length; k++) if (c[k] == "Unity.Addressables" && c[k + 1].Length == 8) c[k + 1] = Map(c[k + 1]);
                for (int k = 0; k < c.Length; k++)
                    c[k] = System.Text.RegularExpressions.Regex.Replace(c[k], "Unity\\.Addressables!([0-9a-f]{8})", m => "Unity.Addressables!" + Map(m.Groups[1].Value));
                res.Add(string.Join('\t', c));
            }
            if (bad > 0) { Console.Error.WriteLine($"--rekey-tokens: {pairs[i]}: {bad} token(s) not in the original DLL"); return 2; }
            File.WriteAllLines(pairs[i + 1], res);
            Console.WriteLine($"--rekey-tokens: {pairs[i]} -> {pairs[i + 1]}: {lines} lines name Unity.Addressables, {moved} tokens moved");
        }
        return 0;
    }

    static string Short(MethodReference r)
    {
        var t = r.DeclaringType; string tn = t.Namespace == "System" ? t.FullName : t.Name;
        return tn + "::" + r.Name;
    }

    static int Apply(AssemblyDefinition a)
    {
        var mod = a.MainModule; var ts = mod.TypeSystem; var errors = new List<string>();
        var map = mod.GetType(MapName); var ccd = mod.GetType(CcdName);
        if (map == null || ccd == null) { Console.Error.WriteLine("  Unity.Addressables --addr-lazy: ResourceLocationMap / ContentCatalogData missing"); return 2; }
        var dict = SpinePasses.Field(map, "locations", errors);
        var locate = SpinePasses.One(map, "Locate", 3, errors);
        var keys = SpinePasses.One(map, "get_Keys", 0, errors);
        var locs = SpinePasses.One(map, "get_Locations", 0, errors);
        var adds = map.Methods.Where(m => m.Name == "Add" && m.Parameters.Count == 2 && m.HasBody).ToList();
        if (adds.Count != 2) errors.Add($"ResourceLocationMap.Add/2: {adds.Count} bodies");
        var ctor = map.Methods.SingleOrDefault(m => m.IsConstructor && !m.IsStatic && m.Parameters.Count == 2 && m.Parameters[0].ParameterType.FullName == "System.String" && m.Parameters[1].ParameterType.FullName == "System.Int32");
        if (ctor == null) errors.Add("ResourceLocationMap(string, int) missing");
        var create = SpinePasses.One(ccd, "CreateLocator", 1, errors);
        var fLocId = SpinePasses.Field(ccd, "m_LocatorId", errors); var fBucket = SpinePasses.Field(ccd, "m_BucketDataString", errors); var fKey = SpinePasses.Field(ccd, "m_KeyDataString", errors);
        var fEntry = SpinePasses.Field(ccd, "m_EntryDataString", errors); var fExtra = SpinePasses.Field(ccd, "m_ExtraDataString", errors); var fIds = SpinePasses.Field(ccd, "m_InternalIds", errors);
        var fPre = SpinePasses.Field(ccd, "m_InternalIdPrefixes", errors); var fProv = SpinePasses.Field(ccd, "m_ProviderIds", errors); var fTypes = SpinePasses.Field(ccd, "m_resourceTypes", errors);
        var compact = ccd.NestedTypes.SingleOrDefault(t => t.Name == "CompactLocation");
        var compactCtor = compact?.Methods.SingleOrDefault(m => m.IsConstructor && !m.IsStatic && m.Parameters.Count == 8);
        if (compactCtor == null) errors.Add("CompactLocation's constructor missing");
        var expand = ccd.Methods.SingleOrDefault(m => m.Name == "ExpandInternalId" && m.IsStatic && m.Parameters.Count == 2);
        if (expand == null) errors.Add("ExpandInternalId missing");
        MethodReference readObj = null, resolve = null, typeValue = null; TypeReference locIface = null;
        if (create != null)
        {
            var ins = create.Body.Instructions.Where(i => (i.OpCode == OpCodes.Call || i.OpCode == OpCodes.Callvirt || i.OpCode == OpCodes.Newobj) && i.Operand is MethodReference).ToList();
            var calls = ins.Select(i => Short((MethodReference)i.Operand)).ToList();
            if (CreateLocatorCalls == null) errors.Add("the owner-local call sequence is not here (engine/Derived/tools/AddrLocatorCalls.cs): the pass cannot check CreateLocator, refused");
            else if (!calls.SequenceEqual(CreateLocatorCalls)) errors.Add("CreateLocator is not the catalog reader this pass was written for: its calls are " + string.Join(", ", calls));
            else
            {
                MethodReference Called(string name) => ins.Select(i => (MethodReference)i.Operand).First(r => r.Name == name);
                readObj = Called("ReadObjectFromByteArray"); resolve = Called("ResolveInternalId"); typeValue = Called("get_Value");
                locIface = create.Body.Instructions.Where(i => i.OpCode == OpCodes.Newarr).Select(i => (TypeReference)i.Operand).FirstOrDefault(t => t.Name == "IResourceLocation");
                if (locIface == null) errors.Add("CreateLocator makes no IResourceLocation[]");
            }
            if (create.ReturnType.FullName != MapName || create.Parameters[0].ParameterType.FullName != "System.String") errors.Add("CreateLocator's signature differs");
        }
        if (errors.Count > 0) { foreach (var x in errors) Console.Error.WriteLine("  Unity.Addressables --addr-lazy: " + x); return 2; }

        var obj = ts.Object; var str = ts.String; var strArr = new ArrayType(str); var intp = ts.IntPtr; var objArr = new ArrayType(obj);
        var typeT = SpinePasses.Corlib(mod, "System", "Type");
        var hooks = SpinePasses.HooksType(mod, "AddrLazy");
        TypeReference Del(string n) => new TypeReference("Shepherd.Hooks", n, mod, hooks.Scope);
        MethodReference DelCtor(TypeReference d) => SpinePasses.Ref(d, ".ctor", ts.Void, true, obj, intp);
        var dRead = Del("AddrRead"); var dExpand = Del("AddrExpand"); var dResolve = Del("AddrResolve"); var dTypeV = Del("AddrTypeValue"); var dNewArr = Del("AddrNewArray"); var dNewLoc = Del("AddrNewLocation");
        var hBind = SpinePasses.Ref(hooks, "Bind", ts.Void, false, dRead, dExpand, dResolve, dTypeV, dNewArr, dNewLoc);
        var hCreate = SpinePasses.Ref(hooks, "Create", obj, false, obj, str, str, str, str, strArr, strArr, strArr, obj, ts.Int32, str, str);
        var hAttached = SpinePasses.Ref(hooks, "Attached", ts.Void, false, obj, obj, obj);
        var hEnsure = SpinePasses.Ref(hooks, "Ensure", ts.Void, false, obj, obj);
        var hFull = SpinePasses.Ref(hooks, "Full", ts.Void, false, obj);

        // attributes only: what the new type needs to reach
        dict.IsPrivate = false; dict.IsAssembly = true;
        fPre.IsPrivate = false; fPre.IsAssembly = true;
        compact.IsNestedPrivate = false; compact.IsNestedAssembly = true;
        foreach (var f in new[] { fBucket, fKey, fEntry, fExtra, fIds, fProv, fTypes, fLocId }) if (f.IsPrivate) { f.IsPrivate = false; f.IsAssembly = true; }

        // the state field (a field: FieldDef tokens are not in any token-keyed list)
        var fLazy = new FieldDefinition("<Shepherd>lazy", FieldAttributes.Assembly, obj); map.Fields.Add(fLazy);

        // the new type, last in the module: every original method and type keeps its token
        var h = new TypeDefinition("", "<Shepherd>AddrLazy", TypeAttributes.NotPublic | TypeAttributes.Abstract | TypeAttributes.Sealed | TypeAttributes.BeforeFieldInit, obj);
        mod.Types.Add(h);
        MethodDefinition S(string name, TypeReference ret, params TypeReference[] ps)
        {
            var m = new MethodDefinition(name, MethodAttributes.Assembly | MethodAttributes.Static | MethodAttributes.HideBySig, ret);
            foreach (var p in ps) m.Parameters.Add(new ParameterDefinition(p));
            h.Methods.Add(m); return m;
        }
        // Type TypeValue(object a, int i) => ((SerializedType[]) a)[i].Value   (the original's in-place call: its cache lands in the array)
        var mTypeV = S("TypeValue", typeT, obj, ts.Int32);
        {
            var il = mTypeV.Body.GetILProcessor(); var st = typeValue.DeclaringType;
            il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Castclass, fTypes.FieldType); il.Emit(OpCodes.Ldarg_1); il.Emit(OpCodes.Ldelema, st); il.Emit(OpCodes.Call, typeValue); il.Emit(OpCodes.Ret);
        }
        // object[] NewArray(int n) => new IResourceLocation[n]
        var mNewArr = S("NewArray", objArr, ts.Int32);
        { var il = mNewArr.Body.GetILProcessor(); il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Newarr, locIface); il.Emit(OpCodes.Ret); }
        // object NewLocation(object map, id, provider, dep, data, depHash, primaryKey, type) => new CompactLocation((ResourceLocationMap) map, ...)
        var mNewLoc = S("NewLocation", obj, obj, str, str, obj, obj, ts.Int32, str, typeT);
        {
            var il = mNewLoc.Body.GetILProcessor();
            il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Castclass, map);
            for (int k = 1; k < 8; k++) il.Emit(OpCodes.Ldarg, (short)k);
            il.Emit(OpCodes.Newobj, compactCtor); il.Emit(OpCodes.Ret);
        }
        // void Attach(ResourceLocationMap m, object state) { m.<Shepherd>lazy = state; AddrLazy.Attached(state, m, m.locations); }
        var mAttach = S("Attach", ts.Void, map, obj);
        {
            var il = mAttach.Body.GetILProcessor();
            il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldarg_1); il.Emit(OpCodes.Stfld, fLazy);
            il.Emit(OpCodes.Ldarg_1); il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldfld, dict); il.Emit(OpCodes.Call, hAttached);
            il.Emit(OpCodes.Ret);
        }
        // void Bind() { AddrLazy.Bind(new AddrRead(ReadObjectFromByteArray), new AddrExpand(ExpandInternalId), new AddrResolve(ResolveInternalId), ...); }
        var mBind = S("Bind", ts.Void);
        {
            var il = mBind.Body.GetILProcessor();
            void D(MethodReference target, TypeReference d) { il.Emit(OpCodes.Ldnull); il.Emit(OpCodes.Ldftn, target); il.Emit(OpCodes.Newobj, DelCtor(d)); }
            D(readObj, dRead); D(expand, dExpand); D(resolve, dResolve); D(mTypeV, dTypeV); D(mNewArr, dNewArr); D(mNewLoc, dNewLoc);
            il.Emit(OpCodes.Call, hBind); il.Emit(OpCodes.Ret);
        }

        // Locate: `if (<Shepherd>lazy != null) AddrLazy.Ensure(<Shepherd>lazy, key);` then the original body
        Prepend(locate, il => new[]
        {
            il.Create(OpCodes.Ldarg_0), il.Create(OpCodes.Ldfld, fLazy), null /* brfalse -> the original first instruction */,
            il.Create(OpCodes.Ldarg_0), il.Create(OpCodes.Ldfld, fLazy), il.Create(OpCodes.Ldarg_1), il.Create(OpCodes.Call, hEnsure),
        });
        // Keys / Locations / Add x2: `if (<Shepherd>lazy != null) { AddrLazy.Full(<Shepherd>lazy); <Shepherd>lazy = null; }` then the original body
        foreach (var m in new[] { keys, locs }.Concat(adds))
            Prepend(m, il => new[]
            {
                il.Create(OpCodes.Ldarg_0), il.Create(OpCodes.Ldfld, fLazy), null,
                il.Create(OpCodes.Ldarg_0), il.Create(OpCodes.Ldfld, fLazy), il.Create(OpCodes.Call, hFull),
                il.Create(OpCodes.Ldarg_0), il.Create(OpCodes.Ldnull), il.Create(OpCodes.Stfld, fLazy),
            });
        // CreateLocator: `Bind(); object s = AddrLazy.Create(this, <the catalog's fields>, providerSuffix);
        //                 if (s != null) { var m = new ResourceLocationMap(m_LocatorId, 0); Attach(m, s); return m; }`
        var sLoc = new VariableDefinition(obj); create.Body.Variables.Add(sLoc);
        Prepend(create, il =>
        {
            var seq = new List<Instruction> { il.Create(OpCodes.Call, mBind), il.Create(OpCodes.Ldarg_0) };
            foreach (var f in new[] { fBucket, fKey, fEntry, fExtra, fIds, fPre, fProv, fTypes }) { seq.Add(il.Create(OpCodes.Ldarg_0)); seq.Add(il.Create(OpCodes.Ldfld, f)); }
            // the types' count (0 for a null array: the engine refuses it)
            var noTypes = il.Create(OpCodes.Ldc_I4_0); var haveCount = il.Create(OpCodes.Nop);
            seq.Add(il.Create(OpCodes.Ldarg_0)); seq.Add(il.Create(OpCodes.Ldfld, fTypes)); seq.Add(il.Create(OpCodes.Brfalse, noTypes));
            seq.Add(il.Create(OpCodes.Ldarg_0)); seq.Add(il.Create(OpCodes.Ldfld, fTypes)); seq.Add(il.Create(OpCodes.Ldlen)); seq.Add(il.Create(OpCodes.Conv_I4)); seq.Add(il.Create(OpCodes.Br, haveCount));
            seq.Add(noTypes); seq.Add(haveCount);
            seq.Add(il.Create(OpCodes.Ldarg_0)); seq.Add(il.Create(OpCodes.Ldfld, fLocId)); seq.Add(il.Create(OpCodes.Ldarg_1));
            seq.Add(il.Create(OpCodes.Call, hCreate)); seq.Add(il.Create(OpCodes.Stloc, sLoc));
            seq.Add(il.Create(OpCodes.Ldloc, sLoc)); seq.Add(null);
            seq.Add(il.Create(OpCodes.Ldarg_0)); seq.Add(il.Create(OpCodes.Ldfld, fLocId)); seq.Add(il.Create(OpCodes.Ldc_I4_0)); seq.Add(il.Create(OpCodes.Newobj, ctor));
            seq.Add(il.Create(OpCodes.Dup)); seq.Add(il.Create(OpCodes.Ldloc, sLoc)); seq.Add(il.Create(OpCodes.Call, mAttach)); seq.Add(il.Create(OpCodes.Ret));
            return seq.ToArray();
        });
        Console.WriteLine("  Unity.Addressables --addr-lazy: CreateLocator -> AddrLazy.Bind + Create; ResourceLocationMap.Locate -> AddrLazy.Ensure; Keys / Locations / Add x2 -> AddrLazy.Full; the new code in <Shepherd>AddrLazy, last in the module");
        return 0;
    }

    /// <summary>Inserts a prologue before m's first instruction; the null slot becomes `brfalse <the original first instruction>`.</summary>
    static void Prepend(MethodDefinition m, Func<ILProcessor, Instruction[]> make)
    {
        var body = m.Body; body.SimplifyMacros();
        var il = body.GetILProcessor(); var first = body.Instructions[0];
        var seq = make(il);
        for (int k = 0; k < seq.Length; k++) if (seq[k] == null) seq[k] = il.Create(OpCodes.Brfalse, first);
        foreach (var ins in seq) il.InsertBefore(first, ins);
        body.MaxStackSize = Math.Max(body.MaxStackSize, 16);
        body.OptimizeMacros();
    }
}
