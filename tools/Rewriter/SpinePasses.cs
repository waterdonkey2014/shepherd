// Kit #7, spine track (vita/notes/kit7-spine.md): the owner's spine-unity.dll passes after kit #6's --spine-share, applied to one loaded module
// and written once. The game's own DLL on disk is never touched (the output goes to the Rewriter's out dir, owner-local like the rest of
// runtime/ManagedVita); every pass checks the exact IL shape it was written for and refuses any other build; with no engine handler
// installed every hook behaves as the original code (the kit #6 behaviour).
using Mono.Cecil; using Mono.Cecil.Cil; using Mono.Cecil.Rocks;

static class SpinePasses
{
    public static int Run(string managed, string outDir, ReaderParameters rp, bool share, bool lazy, bool clip)
    {
        var f = Path.Combine(managed, "spine-unity.dll");
        if (!File.Exists(f)) { Console.Error.WriteLine("spine passes: no spine-unity.dll in " + managed); return 1; }
        if (lazy && !share) { Console.Error.WriteLine("--spine-lazyskin needs --spine-share (a lazy skin is read again from the streamed pack record: kit #6's L1b read)"); return 1; }
        var a = AssemblyDefinition.ReadAssembly(f, rp); var mod = a.MainModule;
        if (share && SpineSharePass.Apply(a) != 0) return 2;
        if (lazy && SpineLazySkinPass.Apply(a, share) != 0) return 2;
        if (clip && SpineClipPass.Apply(a) != 0) return 2;
        // the new MVID: from the original's and the rewritten bytes (written once with an empty MVID), so any change to a pass gives the
        // module a new identity (an owner AOT image is bound to the MVID it was compiled from). kit #6 (--spine-share alone): the same bytes.
        var old = mod.Mvid; mod.Mvid = Guid.Empty; var ms = new MemoryStream(); a.Write(ms);
        var h2 = System.Security.Cryptography.SHA256.HashData(old.ToByteArray().Concat(ms.ToArray()).ToArray());
        mod.Mvid = new Guid(h2.AsSpan(0, 16));
        a.Write(Path.Combine(outDir, "spine-unity.dll"));
        Console.WriteLine($"  spine-unity: passes{(share ? " --spine-share" : "")}{(lazy ? " --spine-lazyskin" : "")}{(clip ? " --spine-clip" : "")}; MVID {old} -> {mod.Mvid}");
        return 0;
    }

    // ---- shared Cecil helpers ----
    internal static TypeReference Corlib(ModuleDefinition mod, string ns, string name, bool valueType = false)
        => new TypeReference(ns, name, mod, mod.TypeSystem.CoreLibrary) { IsValueType = valueType };
    internal static MethodReference Ref(TypeReference declaring, string name, TypeReference ret, bool hasThis, params TypeReference[] ps)
    {
        var r = new MethodReference(name, ret, declaring) { HasThis = hasThis };
        foreach (var p in ps) r.Parameters.Add(new ParameterDefinition(p));
        return r;
    }
    internal static MethodDefinition One(TypeDefinition t, string name, int pc, List<string> errors)
    {
        var ms = t?.Methods.Where(m => m.Name == name && m.Parameters.Count == pc && m.HasBody).ToList();
        if (ms == null || ms.Count != 1) { errors.Add($"{t?.Name}.{name}/{pc}: {ms?.Count ?? 0} bodies"); return null; }
        return ms[0];
    }
    internal static FieldDefinition Field(TypeDefinition t, string name, List<string> errors)
    {
        var fd = t?.Fields.FirstOrDefault(x => x.Name == name && !x.IsStatic);
        if (fd == null) errors.Add($"{t?.Name}.{name} missing");
        return fd;
    }
    internal static bool IsCall(Instruction i, string type, string name, int pc)
        => (i.OpCode == OpCodes.Call || i.OpCode == OpCodes.Callvirt) && i.Operand is MethodReference r && r.Name == name && r.Parameters.Count == pc && (r.DeclaringType.FullName == type || r.DeclaringType.GetElementType().FullName == type);
    internal static TypeReference HooksType(ModuleDefinition mod, string name)
    {
        var hooksRef = mod.AssemblyReferences.FirstOrDefault(r => r.Name == "Shepherd.Hooks");
        if (hooksRef == null) { hooksRef = new AssemblyNameReference("Shepherd.Hooks", new Version(1, 0, 0, 0)); mod.AssemblyReferences.Add(hooksRef); }
        return new TypeReference("Shepherd.Hooks", name, mod, hooksRef);
    }
    internal static int Fail(string pass, List<string> e) { foreach (var x in e) Console.Error.WriteLine($"  spine-unity {pass}: " + x); return 2; }
    internal static MethodDefinition DefaultCtor(ModuleDefinition mod, TypeDefinition t)
    {
        var ctor = new MethodDefinition(".ctor", MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName, mod.TypeSystem.Void);
        var il = ctor.Body.GetILProcessor(); il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Call, Ref(mod.TypeSystem.Object, ".ctor", mod.TypeSystem.Void, true)); il.Emit(OpCodes.Ret);
        t.Methods.Add(ctor); return ctor;
    }
}

/// <summary>Kit #7, lever L2 (vita/notes/kit4-intro-plan.md 3.3, kit7-spine.md): a skin's attachments are read when the skin is first used.
/// The intro's 'Follower' data holds 624 skins with 84,143 attachments (each of its two copies) and 'Player' 123 skins with 24,793, of which
/// a few are ever shown; SkeletonBinary reads every one of them at load. With this pass a non-default skin of a streamed skeleton file
/// (kit #6 L1b) can be read in two steps: at load its header (name, bones, constraints) as before, and its slot section with a loader that
/// passes every call on to the real loader (so a missing region throws exactly where and how it did) but keeps nothing -- the skin is then
/// "lazy": an empty attachment dictionary plus where its slot section starts (the engine's token) and which slots it fills. The first access
/// to its attachments (every load of the private field Skin.attachments, in Skin's own methods: SetAttachment, AddSkin / CopySkin / AttachAll
/// of another skin, GetAttachment(s), RemoveAttachment, Clear, Attachments) first reads the slot section again from the pack, with the same
/// reader method and the real loader, into the same dictionary in the same order, and resolves the linked meshes it read exactly as the end
/// of ReadSkeletonData does (the same instructions, copied). GetAttachment / GetAttachments of a slot the skin does not fill answer as the
/// empty lookup would without reading. One lock for every load (a load can need another skin's parent mesh; a thread that is loading and
/// re-enters returns at once), and VertexAttachment's own id lock held across the replay (R1); a loaded skin is published after a barrier and a
/// skin that was lazy is read after one (R3; review fixes, kit7-spine.md). Which skins are lazy is the engine's decision
/// (Shepherd.Hooks.SpineLazy.Mark: null = read as before).</summary>
static class SpineLazySkinPass
{
    public static int Apply(AssemblyDefinition a, bool shared)
    {
        var mod = a.MainModule; var ts = mod.TypeSystem; var errors = new List<string>();
        TypeDefinition T(string n) { var t = mod.GetType(n); if (t == null) errors.Add("type " + n + " missing"); return t; }
        var sb = T("Spine.SkeletonBinary"); var skinT = T("Spine.Skin"); var sdT = T("Spine.SkeletonData"); var loaderI = T("Spine.AttachmentLoader");
        var exposedT = T("Spine.ExposedList`1"); var attT = T("Spine.Attachment");
        if (errors.Count > 0) return SpinePasses.Fail("--spine-lazyskin", errors);
        var inputT = sb.NestedTypes.FirstOrDefault(n => n.Name == "SkeletonInput"); if (inputT == null) { errors.Add("SkeletonBinary/SkeletonInput missing"); return SpinePasses.Fail("--spine-lazyskin", errors); }
        if (skinT.Fields.Any(f => f.Name.StartsWith("<Shepherd>"))) { errors.Add("already rewritten"); return SpinePasses.Fail("--spine-lazyskin", errors); }
        var fAttachments = SpinePasses.Field(skinT, "attachments", errors); var fLoader = SpinePasses.Field(sb, "attachmentLoader", errors);
        var fLinked = SpinePasses.Field(sb, "linkedMeshes", errors); var fStrings = SpinePasses.Field(inputT, "strings", errors);
        var fSdSlots = SpinePasses.Field(sdT, "slots", errors);
        var readSkin = SpinePasses.One(sb, "ReadSkin", 4, errors); var readAtt = SpinePasses.One(sb, "ReadAttachment", 6, errors);
        var readInt = inputT.Methods.FirstOrDefault(m => m.Name == "ReadInt" && m.Parameters.Count == 1 && m.Parameters[0].ParameterType.MetadataType == MetadataType.Boolean);
        var readStr = inputT.Methods.FirstOrDefault(m => m.Name == "ReadStringRef" && m.Parameters.Count == 0);
        var inputCtor = inputT.Methods.FirstOrDefault(m => m.IsConstructor && !m.IsStatic && m.Parameters.Count == 1 && m.Parameters[0].ParameterType.FullName == "System.IO.Stream");
        var setAtt = SpinePasses.One(skinT, "SetAttachment", 3, errors); var getAtt = SpinePasses.One(skinT, "GetAttachment", 2, errors); var getAtts = SpinePasses.One(skinT, "GetAttachments", 2, errors);
        var rsd = sb.Methods.Where(m => m.Name == "ReadSkeletonData" && m.Parameters.Count == 1 && m.Parameters[0].ParameterType.FullName == "System.IO.Stream").ToList();
        if (readInt == null || readStr == null || inputCtor == null) errors.Add("SkeletonInput.ReadInt(bool) / ReadStringRef() / .ctor(Stream) not found");
        if (rsd.Count != 1) errors.Add("SkeletonBinary.ReadSkeletonData(Stream) not found once");
        if (errors.Count > 0) return SpinePasses.Fail("--spine-lazyskin", errors);
        var streamT = inputCtor.Parameters[0].ParameterType;   // spine-unity's own reference to Stream
        var lazyHooks = SpinePasses.HooksType(mod, "SpineLazy");
        var hMark = SpinePasses.Ref(lazyHooks, "Mark", ts.Object, false, ts.Int32);
        var hSkip = SpinePasses.Ref(lazyHooks, "Skip", ts.Void, false, ts.Object, ts.Boolean);
        var hOpen = SpinePasses.Ref(lazyHooks, "Open", ts.Object, false, ts.Object);
        var hLoaded = SpinePasses.Ref(lazyHooks, "Loaded", ts.Void, false, ts.Object, ts.Object);
        var hLock = SpinePasses.Ref(lazyHooks, "get_Lock", ts.Object, false);
        var monitorT = SpinePasses.Corlib(mod, "System.Threading", "Monitor");
        var mEnter = SpinePasses.Ref(monitorT, "Enter", ts.Void, false, ts.Object, new ByReferenceType(ts.Boolean));
        var mExit = SpinePasses.Ref(monitorT, "Exit", ts.Void, false, ts.Object);
        var intArr = new ArrayType(ts.Int32);
        var stringsType = fStrings.FieldType;   // ExposedList<string>
        // ExposedList<SlotData>.Count: a field of the generic type, referenced through the instance type
        var sdSlotsCount = new FieldReference("Count", ts.Int32, fSdSlots.FieldType);

        // VertexAttachment ids: every VertexAttachment takes the next value of a static counter at construction (id = (nextID++ & 0xFFFF) << 11;
        // DeformTimeline.PropertyId adds it: AnimationState's property set, wrapping at 65,536 -- equal ids are part of the game's behaviour). A
        // lazy skin's first read lets the real loader build every attachment (the counter moves exactly as in the eager read) and records the
        // ids the vertex attachments got; its later read gives the attachments it builds those ids again, in the same order, and sets the
        // counter back to where it was before that read: every id, and every id taken later, is the eager read's. The later read holds the
        // counter's own lock (nextIdLock, which the constructor takes) from the save to the restore (review fix, kit7-spine.md "Review fixes"
        // R1): a VertexAttachment built on another thread meanwhile (a skeleton read, a Copy) waits instead of taking an id the restore would
        // hand out again. Lock order everywhere: SpineLazy.Lock, then nextIdLock (the constructor takes nextIdLock alone and calls nothing).
        var vaT = T("Spine.VertexAttachment"); if (vaT == null) return SpinePasses.Fail("--spine-lazyskin", errors);
        var fId = SpinePasses.Field(vaT, "id", errors); var fNext = vaT.Fields.FirstOrDefault(f => f.Name == "nextID" && f.IsStatic); var fNextLock = vaT.Fields.FirstOrDefault(f => f.Name == "nextIdLock" && f.IsStatic);
        if (fNext == null || fNextLock == null || errors.Count > 0) { errors.Add("VertexAttachment.nextID / nextIdLock missing"); return SpinePasses.Fail("--spine-lazyskin", errors); }
        fId.IsInitOnly = false;   // (the replay sets it once, right after the constructor)
        var monitorT0 = SpinePasses.Corlib(mod, "System.Threading", "Monitor");
        var mEnter0 = SpinePasses.Ref(monitorT0, "Enter", ts.Void, false, ts.Object, new ByReferenceType(ts.Boolean)); var mExit0 = SpinePasses.Ref(monitorT0, "Exit", ts.Void, false, ts.Object);
        MethodDefinition NextAccessor(bool set)
        {
            var m = new MethodDefinition(set ? "<Shepherd>SetNext" : "<Shepherd>GetNext", MethodAttributes.Assembly | MethodAttributes.Static | MethodAttributes.HideBySig, set ? ts.Void : ts.Int32);
            var pv = new ParameterDefinition("v", ParameterAttributes.None, ts.Int32); if (set) m.Parameters.Add(pv);
            var b = m.Body; b.InitLocals = true; var il = b.GetILProcessor(); var vTaken = new VariableDefinition(ts.Boolean); var vR = new VariableDefinition(ts.Int32); b.Variables.Add(vTaken); b.Variables.Add(vR);
            var tryStart = il.Create(OpCodes.Ldsfld, fNextLock); var done = il.Create(OpCodes.Nop);
            il.Append(tryStart); il.Emit(OpCodes.Ldloca, vTaken); il.Emit(OpCodes.Call, mEnter0);
            if (set) { il.Emit(OpCodes.Ldarg, pv); il.Emit(OpCodes.Stsfld, fNext); } else { il.Emit(OpCodes.Ldsfld, fNext); il.Emit(OpCodes.Stloc, vR); }
            il.Emit(OpCodes.Leave, done);
            var fin = il.Create(OpCodes.Ldloc, vTaken); il.Append(fin); var fe = il.Create(OpCodes.Endfinally); il.Emit(OpCodes.Brfalse, fe); il.Emit(OpCodes.Ldsfld, fNextLock); il.Emit(OpCodes.Call, mExit0); il.Append(fe);
            il.Append(done); if (!set) il.Emit(OpCodes.Ldloc, vR); il.Emit(OpCodes.Ret);
            b.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Finally) { TryStart = tryStart, TryEnd = fin, HandlerStart = fin, HandlerEnd = done });
            vaT.Methods.Add(m); return m;
        }
        var mGetNext = NextAccessor(false); var mSetNext = NextAccessor(true);
        // static object <Shepherd>NextLock() => nextIdLock (the private field read where it is declared)
        var mNextLock = new MethodDefinition("<Shepherd>NextLock", MethodAttributes.Assembly | MethodAttributes.Static | MethodAttributes.HideBySig, ts.Object);
        { var il = mNextLock.Body.GetILProcessor(); il.Emit(OpCodes.Ldsfld, fNextLock); il.Emit(OpCodes.Ret); vaT.Methods.Add(mNextLock); }
        // Thread.MemoryBarrier: a lazy skin is published (lazy = null) after a full barrier, and a skin that was lazy is read after one (R3)
        var mBarrier = SpinePasses.Ref(SpinePasses.Corlib(mod, "System.Threading", "Thread"), "MemoryBarrier", ts.Void, false);
        var intList = new GenericInstanceType(exposedT); intList.GenericArguments.Add(ts.Int32);
        var intListCtor = new MethodReference(".ctor", ts.Void, intList) { HasThis = true };
        var intListAdd = new MethodReference("Add", ts.Void, intList) { HasThis = true }; intListAdd.Parameters.Add(new ParameterDefinition(exposedT.GenericParameters[0]));
        var intListItems = new FieldReference("Items", new ArrayType(exposedT.GenericParameters[0]), intList);

        // (1) <Shepherd>SkipLoader: every call to the real loader, its result dropped (a vertex attachment's id recorded);
        //     <Shepherd>ReplayLoader: every call to the real loader, a vertex attachment given the next recorded id
        TypeDefinition Loader(string name, bool replay, out FieldDefinition fR, out FieldDefinition fIds, out FieldDefinition fK)
        {
            var t = new TypeDefinition("Spine", name, TypeAttributes.NotPublic | TypeAttributes.Sealed | TypeAttributes.BeforeFieldInit | TypeAttributes.Class, ts.Object);
            t.Interfaces.Add(new InterfaceImplementation(loaderI));
            var real = new FieldDefinition("real", FieldAttributes.Assembly, loaderI); t.Fields.Add(real); fR = real;
            var ids = new FieldDefinition("ids", FieldAttributes.Assembly, intList); t.Fields.Add(ids); fIds = ids;
            FieldDefinition k = null; if (replay) { k = new FieldDefinition("k", FieldAttributes.Assembly, ts.Int32); t.Fields.Add(k); } fK = k;
            int n = 0;
            foreach (var im in loaderI.Methods)
            {
                var m = new MethodDefinition(im.Name, MethodAttributes.Public | MethodAttributes.Final | MethodAttributes.HideBySig | MethodAttributes.NewSlot | MethodAttributes.Virtual, im.ReturnType);
                foreach (var p in im.Parameters) m.Parameters.Add(new ParameterDefinition(p.Name, ParameterAttributes.None, p.ParameterType));
                var b = m.Body; b.InitLocals = true; var il = b.GetILProcessor(); var vRes = new VariableDefinition(im.ReturnType); var vVa = new VariableDefinition(vaT); b.Variables.Add(vRes); b.Variables.Add(vVa);
                il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldfld, real);
                for (int i = 0; i < m.Parameters.Count; i++) il.Emit(OpCodes.Ldarg, m.Parameters[i]);
                il.Emit(OpCodes.Callvirt, im); il.Emit(OpCodes.Stloc, vRes);
                var end = il.Create(OpCodes.Nop);
                il.Emit(OpCodes.Ldloc, vRes); il.Emit(OpCodes.Isinst, vaT); il.Emit(OpCodes.Stloc, vVa); il.Emit(OpCodes.Ldloc, vVa); il.Emit(OpCodes.Brfalse, end);
                il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldfld, ids); il.Emit(OpCodes.Brfalse, end);
                if (!replay) { il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldfld, ids); il.Emit(OpCodes.Ldloc, vVa); il.Emit(OpCodes.Ldfld, fId); il.Emit(OpCodes.Callvirt, intListAdd); }
                else
                {
                    il.Emit(OpCodes.Ldloc, vVa); il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldfld, ids); il.Emit(OpCodes.Ldfld, intListItems); il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldfld, k); il.Emit(OpCodes.Ldelem_I4); il.Emit(OpCodes.Stfld, fId);
                    il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldfld, k); il.Emit(OpCodes.Ldc_I4_1); il.Emit(OpCodes.Add); il.Emit(OpCodes.Stfld, k);
                }
                il.Append(end);
                if (replay) il.Emit(OpCodes.Ldloc, vRes); else il.Emit(OpCodes.Ldnull);
                il.Emit(OpCodes.Ret);
                t.Methods.Add(m); n++;
            }
            if (n != 6) errors.Add($"AttachmentLoader has {n} methods, 6 expected");
            mod.Types.Add(t); return t;
        }
        var skipT = Loader("<Shepherd>SkipLoader", false, out var fReal, out var fSkipIds, out _); var skipCtor = SpinePasses.DefaultCtor(mod, skipT);
        var replayT = Loader("<Shepherd>ReplayLoader", true, out var fRReal, out var fRIds, out var fRK); var replayCtor = SpinePasses.DefaultCtor(mod, replayT);
        int loaderMethods = 6;

        // (2) <Shepherd>LazySkin: what a lazy skin needs to be read later
        var lzT = new TypeDefinition("Spine", "<Shepherd>LazySkin", TypeAttributes.NotPublic | TypeAttributes.Sealed | TypeAttributes.BeforeFieldInit | TypeAttributes.Class, ts.Object);
        FieldDefinition LF(string n, TypeReference t) { var fd = new FieldDefinition(n, FieldAttributes.Assembly, t); lzT.Fields.Add(fd); return fd; }
        var lBinary = LF("binary", sb); var lData = LF("data", sdT); var lStrings = LF("strings", stringsType); var lNon = LF("nonessential", ts.Boolean);
        var lCount = LF("count", ts.Int32); var lToken = LF("token", ts.Object); var lSlots = LF("slots", intArr); var lLoading = LF("loading", ts.Boolean);
        var lReal = LF("real", loaderI); var lIds = LF("ids", intList);
        var lzCtor = SpinePasses.DefaultCtor(mod, lzT);
        mod.Types.Add(lzT);
        var fLazy = new FieldDefinition("<Shepherd>lazy", FieldAttributes.Assembly, lzT); skinT.Fields.Add(fLazy);
        // R3: set with lazy (before the SkeletonData is published), never cleared: a skin whose dictionary another thread may have filled after
        // publication; Ensure reads such a skin's attachments only after a barrier (the loading thread clears lazy only after one)
        var fWas = new FieldDefinition("<Shepherd>was", FieldAttributes.Assembly, ts.Boolean); skinT.Fields.Add(fWas);

        // (3) SkeletonBinary.<Shepherd>ReadSlots(input, data, skin, count, nonessential, note): ReadSkin's slot loop (the same calls in the same
        // order); note != null: the slots read are marked (returns false when a slot index is outside the skeleton's slots)
        var mSlots = new MethodDefinition("<Shepherd>ReadSlots", MethodAttributes.Assembly | MethodAttributes.HideBySig, ts.Boolean);
        var pIn = new ParameterDefinition("input", ParameterAttributes.None, inputT); var pData = new ParameterDefinition("skeletonData", ParameterAttributes.None, sdT);
        var pSkin = new ParameterDefinition("skin", ParameterAttributes.None, skinT); var pCount = new ParameterDefinition("count", ParameterAttributes.None, ts.Int32);
        var pNon = new ParameterDefinition("nonessential", ParameterAttributes.None, ts.Boolean); var pNote = new ParameterDefinition("note", ParameterAttributes.None, intArr);
        foreach (var p in new[] { pIn, pData, pSkin, pCount, pNon, pNote }) mSlots.Parameters.Add(p);
        {
            var b = mSlots.Body; b.InitLocals = true; var il = b.GetILProcessor();
            var vM = new VariableDefinition(ts.Int32); var vSlot = new VariableDefinition(ts.Int32); var vK = new VariableDefinition(ts.Int32); var vN = new VariableDefinition(ts.Int32);
            var vName = new VariableDefinition(ts.String); var vAtt = new VariableDefinition(attT); var vOk = new VariableDefinition(ts.Boolean);
            foreach (var v in new[] { vM, vSlot, vK, vN, vName, vAtt, vOk }) b.Variables.Add(v);
            il.Emit(OpCodes.Ldc_I4_1); il.Emit(OpCodes.Stloc, vOk);
            il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Stloc, vM);
            var outerCond = il.Create(OpCodes.Ldloc, vM); il.Emit(OpCodes.Br, outerCond);
            var outerBody = il.Create(OpCodes.Ldarg, pIn); il.Append(outerBody);
            il.Emit(OpCodes.Ldc_I4_1); il.Emit(OpCodes.Callvirt, readInt); il.Emit(OpCodes.Stloc, vSlot);
            il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Stloc, vK);
            il.Emit(OpCodes.Ldarg, pIn); il.Emit(OpCodes.Ldc_I4_1); il.Emit(OpCodes.Callvirt, readInt); il.Emit(OpCodes.Stloc, vN);
            // note: if (note != null && n > 0) { if ((uint)slot < (uint)(note.Length << 5)) note[slot >> 5] |= 1 << (slot & 31); else ok = false; }
            var afterNote = il.Create(OpCodes.Nop);
            il.Emit(OpCodes.Ldarg, pNote); il.Emit(OpCodes.Brfalse, afterNote);
            il.Emit(OpCodes.Ldloc, vN); il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Ble, afterNote);
            var outOfRange = il.Create(OpCodes.Ldc_I4_0);
            il.Emit(OpCodes.Ldloc, vSlot); il.Emit(OpCodes.Ldarg, pNote); il.Emit(OpCodes.Ldlen); il.Emit(OpCodes.Conv_I4); il.Emit(OpCodes.Ldc_I4_5); il.Emit(OpCodes.Shl); il.Emit(OpCodes.Bge_Un, outOfRange);
            il.Emit(OpCodes.Ldarg, pNote); il.Emit(OpCodes.Ldloc, vSlot); il.Emit(OpCodes.Ldc_I4_5); il.Emit(OpCodes.Shr);
            il.Emit(OpCodes.Ldelema, ts.Int32); il.Emit(OpCodes.Dup); il.Emit(OpCodes.Ldind_I4);
            il.Emit(OpCodes.Ldc_I4_1); il.Emit(OpCodes.Ldloc, vSlot); il.Emit(OpCodes.Ldc_I4, 31); il.Emit(OpCodes.And); il.Emit(OpCodes.Shl); il.Emit(OpCodes.Or); il.Emit(OpCodes.Stind_I4);
            il.Emit(OpCodes.Br, afterNote);
            il.Append(outOfRange); il.Emit(OpCodes.Stloc, vOk);
            il.Append(afterNote);
            var innerCond = il.Create(OpCodes.Ldloc, vK); il.Emit(OpCodes.Br, innerCond);
            var innerBody = il.Create(OpCodes.Ldarg, pIn); il.Append(innerBody);
            il.Emit(OpCodes.Callvirt, readStr); il.Emit(OpCodes.Stloc, vName);
            il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldarg, pIn); il.Emit(OpCodes.Ldarg, pData); il.Emit(OpCodes.Ldarg, pSkin); il.Emit(OpCodes.Ldloc, vSlot); il.Emit(OpCodes.Ldloc, vName); il.Emit(OpCodes.Ldarg, pNon);
            il.Emit(OpCodes.Call, readAtt); il.Emit(OpCodes.Stloc, vAtt);
            var next = il.Create(OpCodes.Ldloc, vK);
            il.Emit(OpCodes.Ldloc, vAtt); il.Emit(OpCodes.Brfalse, next);
            il.Emit(OpCodes.Ldarg, pSkin); il.Emit(OpCodes.Ldloc, vSlot); il.Emit(OpCodes.Ldloc, vName); il.Emit(OpCodes.Ldloc, vAtt); il.Emit(OpCodes.Callvirt, setAtt);
            il.Append(next); il.Emit(OpCodes.Ldc_I4_1); il.Emit(OpCodes.Add); il.Emit(OpCodes.Stloc, vK);
            il.Append(innerCond); il.Emit(OpCodes.Ldloc, vN); il.Emit(OpCodes.Blt, innerBody);
            il.Emit(OpCodes.Ldloc, vM); il.Emit(OpCodes.Ldc_I4_1); il.Emit(OpCodes.Add); il.Emit(OpCodes.Stloc, vM);
            il.Append(outerCond); il.Emit(OpCodes.Ldarg, pCount); il.Emit(OpCodes.Blt, outerBody);
            il.Emit(OpCodes.Ldloc, vOk); il.Emit(OpCodes.Ret);
        }
        sb.Methods.Add(mSlots);

        // (4) SkeletonBinary.<Shepherd>Resolve(data, start): ReadSkeletonData's linked-mesh loop body (copied instruction for instruction --
        // with --spine-share its UpdateUVs is already the interning call) over linkedMeshes[start..n), then those entries removed
        var mResolve = new MethodDefinition("<Shepherd>Resolve", MethodAttributes.Assembly | MethodAttributes.HideBySig, ts.Void);
        var rData = new ParameterDefinition("skeletonData", ParameterAttributes.None, sdT); var rStart = new ParameterDefinition("start", ParameterAttributes.None, ts.Int32);
        mResolve.Parameters.Add(rData); mResolve.Parameters.Add(rStart);
        if (!CopyLinkedLoop(rsd[0], mResolve, rData, rStart, fLinked, sdT, shared, errors)) return SpinePasses.Fail("--spine-lazyskin", errors);
        sb.Methods.Add(mResolve);

        // (4b) the string table a lazy skin is read with later: only the entries its slot section uses are kept (the first read marks every
        // ReadStringRef index it takes in <Shepherd>keep; at the end of ReadSkeletonData every other entry is cleared -- they were the read's
        // own references, held by nothing the data keeps). A later read of the same bytes takes exactly the marked entries: the same objects.
        var fKeep = new FieldDefinition("<Shepherd>keep", FieldAttributes.Assembly, intArr); inputT.Fields.Add(fKeep);
        var fTouched = new FieldDefinition("<Shepherd>touched", FieldAttributes.Assembly, intArr); inputT.Fields.Add(fTouched);
        {
            var rsr = readStr; rsr.Body.SimplifyMacros(); var il = rsr.Body.GetILProcessor(); var ins = rsr.Body.Instructions;
            // ldarg.0; ldc.i4.1; call ReadInt; stloc idx; ... : mark (idx - 1) after the store when touched != null and idx != 0
            var st = ins.FirstOrDefault(i => i.OpCode == OpCodes.Stloc && i.Previous != null && SpinePasses.IsCall(i.Previous, inputT.FullName, "ReadInt", 1));
            if (st == null || ins.Count(i => i.OpCode == OpCodes.Ret) != 2) errors.Add("ReadStringRef: shape (ReadInt -> stloc, two returns)");
            else
            {
                var v = (VariableDefinition)st.Operand; var after = st.Next;
                var seq = new List<Instruction> {
                    il.Create(OpCodes.Ldarg_0), il.Create(OpCodes.Ldfld, fTouched), il.Create(OpCodes.Brfalse, after),
                    il.Create(OpCodes.Ldloc, v), il.Create(OpCodes.Brfalse, after),
                    il.Create(OpCodes.Ldarg_0), il.Create(OpCodes.Ldfld, fTouched), il.Create(OpCodes.Ldloc, v), il.Create(OpCodes.Ldc_I4_1), il.Create(OpCodes.Sub), il.Create(OpCodes.Call, MarkBit(mod, inputT, intArr)) };
                var at = st; foreach (var x in seq) { il.InsertAfter(at, x); at = x; }
            }
            rsr.Body.OptimizeMacros();
        }
        {
            // end of ReadSkeletonData: <Shepherd>Prune(input)
            var m = rsd[0]; m.Body.SimplifyMacros(); var il = m.Body.GetILProcessor();
            var inLoc = m.Body.Variables.Where(v => v.VariableType.FullName == inputT.FullName).ToList();
            var rets = m.Body.Instructions.Where(i => i.OpCode == OpCodes.Ret).ToList();
            if (inLoc.Count != 1 || rets.Count != 1) errors.Add($"ReadSkeletonData: {inLoc.Count} SkeletonInput locals, {rets.Count} returns (1 / 1 expected)");
            else
            {
                var prune = PruneMethod(mod, inputT, fKeep, fStrings, intArr); inputT.Methods.Add(prune);
                var r = rets[0]; var call = il.Create(OpCodes.Call, prune); var ld = il.Create(OpCodes.Ldloc, inLoc[0]);
                // the return value is on the stack at 'ret': load the input, call (static, void) -- the stack keeps the value under it
                il.InsertBefore(r, ld); il.InsertBefore(r, call);
                foreach (var i in m.Body.Instructions) if (i.Operand == r) i.Operand = ld;   // a branch to the old ret goes through the prune
                foreach (var h in m.Body.ExceptionHandlers) { if (h.HandlerEnd == r) h.HandlerEnd = ld; if (h.TryEnd == r) h.TryEnd = ld; }
            }
            m.Body.OptimizeMacros();
        }

        // (5) SkeletonBinary.<Shepherd>LazySkip(input, data, skin, count, nonessential) -> true = the slot section was read and dropped, skin is lazy
        var mSkip = new MethodDefinition("<Shepherd>LazySkip", MethodAttributes.Assembly | MethodAttributes.HideBySig, ts.Boolean);
        var kIn = new ParameterDefinition("input", ParameterAttributes.None, inputT); var kData = new ParameterDefinition("skeletonData", ParameterAttributes.None, sdT);
        var kSkin = new ParameterDefinition("skin", ParameterAttributes.None, skinT); var kCount = new ParameterDefinition("count", ParameterAttributes.None, ts.Int32);
        var kNon = new ParameterDefinition("nonessential", ParameterAttributes.None, ts.Boolean);
        foreach (var p in new[] { kIn, kData, kSkin, kCount, kNon }) mSkip.Parameters.Add(p);
        {
            var b = mSkip.Body; b.InitLocals = true; var il = b.GetILProcessor();
            var vTok = new VariableDefinition(ts.Object); var vL = new VariableDefinition(lzT); var vNote = new VariableDefinition(intArr); var vReal = new VariableDefinition(loaderI);
            var vSkipL = new VariableDefinition(skipT); var vOk = new VariableDefinition(ts.Boolean);
            foreach (var v in new[] { vTok, vL, vNote, vReal, vSkipL, vOk }) b.Variables.Add(v);
            var go = il.Create(OpCodes.Nop);
            il.Emit(OpCodes.Ldarg, kCount); il.Emit(OpCodes.Call, hMark); il.Emit(OpCodes.Stloc, vTok);
            il.Emit(OpCodes.Ldloc, vTok); il.Emit(OpCodes.Brtrue, go); il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Ret);
            il.Append(go);
            il.Emit(OpCodes.Newobj, lzCtor); il.Emit(OpCodes.Stloc, vL);
            void SetL(FieldDefinition fd, Action load) { il.Emit(OpCodes.Ldloc, vL); load(); il.Emit(OpCodes.Stfld, fd); }
            SetL(lBinary, () => il.Emit(OpCodes.Ldarg_0)); SetL(lData, () => il.Emit(OpCodes.Ldarg, kData));
            SetL(lStrings, () => { il.Emit(OpCodes.Ldarg, kIn); il.Emit(OpCodes.Ldfld, fStrings); });
            SetL(lNon, () => il.Emit(OpCodes.Ldarg, kNon)); SetL(lCount, () => il.Emit(OpCodes.Ldarg, kCount)); SetL(lToken, () => il.Emit(OpCodes.Ldloc, vTok));
            // note = new int[(data.slots.Count + 31) >> 5]
            il.Emit(OpCodes.Ldarg, kData); il.Emit(OpCodes.Ldfld, fSdSlots); il.Emit(OpCodes.Ldfld, sdSlotsCount); il.Emit(OpCodes.Ldc_I4, 31); il.Emit(OpCodes.Add); il.Emit(OpCodes.Ldc_I4_5); il.Emit(OpCodes.Shr);
            il.Emit(OpCodes.Newarr, ts.Int32); il.Emit(OpCodes.Stloc, vNote);
            il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldfld, fLoader); il.Emit(OpCodes.Stloc, vReal);
            SetL(lReal, () => il.Emit(OpCodes.Ldloc, vReal)); SetL(lIds, () => il.Emit(OpCodes.Newobj, intListCtor));
            il.Emit(OpCodes.Newobj, skipCtor); il.Emit(OpCodes.Stloc, vSkipL); il.Emit(OpCodes.Ldloc, vSkipL); il.Emit(OpCodes.Ldloc, vReal); il.Emit(OpCodes.Stfld, fReal);
            il.Emit(OpCodes.Ldloc, vSkipL); il.Emit(OpCodes.Ldloc, vL); il.Emit(OpCodes.Ldfld, lIds); il.Emit(OpCodes.Stfld, fSkipIds);
            il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldloc, vSkipL); il.Emit(OpCodes.Stfld, fLoader);
            il.Emit(OpCodes.Ldloc, vTok); il.Emit(OpCodes.Ldc_I4_1); il.Emit(OpCodes.Call, hSkip);
            // input.touched = input.keep ?? (input.keep = new int[(input.strings.Count + 31) >> 5])
            var haveKeep = il.Create(OpCodes.Nop);
            il.Emit(OpCodes.Ldarg, kIn); il.Emit(OpCodes.Ldfld, fKeep); il.Emit(OpCodes.Brtrue, haveKeep);
            il.Emit(OpCodes.Ldarg, kIn); il.Emit(OpCodes.Ldarg, kIn); il.Emit(OpCodes.Ldfld, fStrings); il.Emit(OpCodes.Ldfld, new FieldReference("Count", ts.Int32, stringsType));
            il.Emit(OpCodes.Ldc_I4, 31); il.Emit(OpCodes.Add); il.Emit(OpCodes.Ldc_I4_5); il.Emit(OpCodes.Shr); il.Emit(OpCodes.Newarr, ts.Int32); il.Emit(OpCodes.Stfld, fKeep);
            il.Append(haveKeep);
            il.Emit(OpCodes.Ldarg, kIn); il.Emit(OpCodes.Ldarg, kIn); il.Emit(OpCodes.Ldfld, fKeep); il.Emit(OpCodes.Stfld, fTouched);
            // try { ok = ReadSlots(input, data, skin, count, nonessential, note); } finally { attachmentLoader = real; input.touched = null; Skip(tok, false); }
            var tryStart = il.Create(OpCodes.Ldarg_0); il.Append(tryStart);
            il.Emit(OpCodes.Ldarg, kIn); il.Emit(OpCodes.Ldarg, kData); il.Emit(OpCodes.Ldarg, kSkin); il.Emit(OpCodes.Ldarg, kCount); il.Emit(OpCodes.Ldarg, kNon); il.Emit(OpCodes.Ldloc, vNote);
            il.Emit(OpCodes.Call, mSlots); il.Emit(OpCodes.Stloc, vOk);
            var afterTry = il.Create(OpCodes.Nop);
            il.Emit(OpCodes.Leave, afterTry);
            var finStart = il.Create(OpCodes.Ldarg_0); il.Append(finStart);
            il.Emit(OpCodes.Ldloc, vReal); il.Emit(OpCodes.Stfld, fLoader); il.Emit(OpCodes.Ldarg, kIn); il.Emit(OpCodes.Ldnull); il.Emit(OpCodes.Stfld, fTouched);
            il.Emit(OpCodes.Ldloc, vTok); il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Call, hSkip);
            il.Emit(OpCodes.Endfinally);
            il.Append(afterTry);
            b.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Finally) { TryStart = tryStart, TryEnd = finStart, HandlerStart = finStart, HandlerEnd = afterTry });
            // l.slots = ok ? note : null; skin.lazy = l; return true
            var useNull = il.Create(OpCodes.Ldnull); var setSlots = il.Create(OpCodes.Stfld, lSlots);
            il.Emit(OpCodes.Ldloc, vL); il.Emit(OpCodes.Ldloc, vOk); il.Emit(OpCodes.Brfalse, useNull); il.Emit(OpCodes.Ldloc, vNote); il.Emit(OpCodes.Br, setSlots);
            il.Append(useNull); il.Append(setSlots);
            il.Emit(OpCodes.Ldarg, kSkin); il.Emit(OpCodes.Ldc_I4_1); il.Emit(OpCodes.Stfld, fWas);
            il.Emit(OpCodes.Ldarg, kSkin); il.Emit(OpCodes.Ldloc, vL); il.Emit(OpCodes.Stfld, fLazy);
            il.Emit(OpCodes.Ldc_I4_1); il.Emit(OpCodes.Ret);
        }
        sb.Methods.Add(mSkip);

        // (6) SkeletonBinary.<Shepherd>Load(skin, l) (static): under SpineLazy.Lock, the slot section read again into skin with the real loader,
        // its linked meshes resolved (both under nextIdLock: R1), skin.lazy cleared last, after a full barrier (R3)
        var mLoad = new MethodDefinition("<Shepherd>Load", MethodAttributes.Assembly | MethodAttributes.Static | MethodAttributes.HideBySig, ts.Void);
        var dSkin = new ParameterDefinition("skin", ParameterAttributes.None, skinT); var dL = new ParameterDefinition("l", ParameterAttributes.None, lzT);
        mLoad.Parameters.Add(dSkin); mLoad.Parameters.Add(dL);
        {
            var b = mLoad.Body; b.InitLocals = true; var il = b.GetILProcessor();
            var vLock = new VariableDefinition(ts.Object); var vTaken = new VariableDefinition(ts.Boolean); var vS = new VariableDefinition(ts.Object);
            var vIn = new VariableDefinition(inputT); var vB = new VariableDefinition(sb); var vStart = new VariableDefinition(ts.Int32);
            var vSaved = new VariableDefinition(ts.Int32); var vCur = new VariableDefinition(loaderI); var vRp = new VariableDefinition(replayT); var vSwapped = new VariableDefinition(ts.Boolean);
            var vNl = new VariableDefinition(ts.Object); var vNTaken = new VariableDefinition(ts.Boolean);
            foreach (var v in new[] { vLock, vTaken, vS, vIn, vB, vStart, vSaved, vCur, vRp, vSwapped, vNl, vNTaken }) b.Variables.Add(v);
            var ret = il.Create(OpCodes.Ret);
            il.Emit(OpCodes.Call, hLock); il.Emit(OpCodes.Stloc, vLock); il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Stloc, vTaken);
            var outerTry = il.Create(OpCodes.Ldloc, vLock); il.Append(outerTry); il.Emit(OpCodes.Ldloca, vTaken); il.Emit(OpCodes.Call, mEnter);
            var outerLeave = il.Create(OpCodes.Nop);
            // if (skin.lazy == null || l.loading) leave
            il.Emit(OpCodes.Ldarg, dSkin); il.Emit(OpCodes.Ldfld, fLazy); il.Emit(OpCodes.Brfalse, outerLeave);
            il.Emit(OpCodes.Ldarg, dL); il.Emit(OpCodes.Ldfld, lLoading); il.Emit(OpCodes.Brtrue, outerLeave);
            il.Emit(OpCodes.Ldarg, dL); il.Emit(OpCodes.Ldc_I4_1); il.Emit(OpCodes.Stfld, lLoading);
            var innerTry = il.Create(OpCodes.Ldarg, dL); il.Append(innerTry);
            il.Emit(OpCodes.Ldfld, lToken); il.Emit(OpCodes.Call, hOpen); il.Emit(OpCodes.Stloc, vS);
            il.Emit(OpCodes.Ldloc, vS); il.Emit(OpCodes.Castclass, streamT); il.Emit(OpCodes.Newobj, inputCtor); il.Emit(OpCodes.Stloc, vIn);
            il.Emit(OpCodes.Ldloc, vIn); il.Emit(OpCodes.Ldarg, dL); il.Emit(OpCodes.Ldfld, lStrings); il.Emit(OpCodes.Stfld, fStrings);
            il.Emit(OpCodes.Ldarg, dL); il.Emit(OpCodes.Ldfld, lBinary); il.Emit(OpCodes.Stloc, vB);
            il.Emit(OpCodes.Ldloc, vB); il.Emit(OpCodes.Ldfld, fLinked); il.Emit(OpCodes.Callvirt, ListCount(mod, fLinked)); il.Emit(OpCodes.Stloc, vStart);
            // R1: nextIdLock held from here to the restore (nl = <Shepherd>NextLock(); Monitor.Enter(nl, ref ntaken))
            il.Emit(OpCodes.Call, mNextLock); il.Emit(OpCodes.Stloc, vNl); il.Emit(OpCodes.Ldloc, vNl); il.Emit(OpCodes.Ldloca, vNTaken); il.Emit(OpCodes.Call, mEnter);
            // saved = VertexAttachment.nextID; the reader's loader := a replay loader over the true loader and the recorded ids
            il.Emit(OpCodes.Call, mGetNext); il.Emit(OpCodes.Stloc, vSaved);
            il.Emit(OpCodes.Ldloc, vB); il.Emit(OpCodes.Ldfld, fLoader); il.Emit(OpCodes.Stloc, vCur);
            il.Emit(OpCodes.Newobj, replayCtor); il.Emit(OpCodes.Stloc, vRp);
            il.Emit(OpCodes.Ldloc, vRp); il.Emit(OpCodes.Ldarg, dL); il.Emit(OpCodes.Ldfld, lReal); il.Emit(OpCodes.Stfld, fRReal);
            il.Emit(OpCodes.Ldloc, vRp); il.Emit(OpCodes.Ldarg, dL); il.Emit(OpCodes.Ldfld, lIds); il.Emit(OpCodes.Stfld, fRIds);
            il.Emit(OpCodes.Ldloc, vB); il.Emit(OpCodes.Ldloc, vRp); il.Emit(OpCodes.Stfld, fLoader); il.Emit(OpCodes.Ldc_I4_1); il.Emit(OpCodes.Stloc, vSwapped);
            il.Emit(OpCodes.Ldloc, vB); il.Emit(OpCodes.Ldloc, vIn); il.Emit(OpCodes.Ldarg, dL); il.Emit(OpCodes.Ldfld, lData); il.Emit(OpCodes.Ldarg, dSkin);
            il.Emit(OpCodes.Ldarg, dL); il.Emit(OpCodes.Ldfld, lCount); il.Emit(OpCodes.Ldarg, dL); il.Emit(OpCodes.Ldfld, lNon); il.Emit(OpCodes.Ldnull);
            il.Emit(OpCodes.Call, mSlots); il.Emit(OpCodes.Pop);
            il.Emit(OpCodes.Ldloc, vB); il.Emit(OpCodes.Ldloc, vCur); il.Emit(OpCodes.Stfld, fLoader);
            il.Emit(OpCodes.Ldloc, vB); il.Emit(OpCodes.Ldarg, dL); il.Emit(OpCodes.Ldfld, lData); il.Emit(OpCodes.Ldloc, vStart); il.Emit(OpCodes.Call, mResolve);
            il.Emit(OpCodes.Ldloc, vSaved); il.Emit(OpCodes.Call, mSetNext); il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Stloc, vSwapped);
            // R1: the counter is restored: nextIdLock released (Monitor.Exit(nl); ntaken = false)
            il.Emit(OpCodes.Ldloc, vNl); il.Emit(OpCodes.Call, mExit); il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Stloc, vNTaken);
            // R3: every write of the read before the store that publishes it (another thread's Ensure reads lazy without the lock)
            il.Emit(OpCodes.Call, mBarrier);
            il.Emit(OpCodes.Ldarg, dSkin); il.Emit(OpCodes.Ldnull); il.Emit(OpCodes.Stfld, fLazy);
            il.Emit(OpCodes.Ldarg, dL); il.Emit(OpCodes.Ldfld, lToken); il.Emit(OpCodes.Ldloc, vS); il.Emit(OpCodes.Call, hLoaded);
            il.Emit(OpCodes.Leave, outerLeave);
            // finally: l.loading = false; after an exception the reader's loader and the counter as they were (then nextIdLock released)
            var innerFin = il.Create(OpCodes.Ldarg, dL); il.Append(innerFin); il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Stfld, lLoading);
            var finOut = il.Create(OpCodes.Endfinally); var afterSwap = il.Create(OpCodes.Ldloc, vNTaken);
            il.Emit(OpCodes.Ldloc, vSwapped); il.Emit(OpCodes.Brfalse, afterSwap);
            il.Emit(OpCodes.Ldloc, vB); il.Emit(OpCodes.Ldloc, vCur); il.Emit(OpCodes.Stfld, fLoader); il.Emit(OpCodes.Ldloc, vSaved); il.Emit(OpCodes.Call, mSetNext);
            il.Append(afterSwap); il.Emit(OpCodes.Brfalse, finOut);
            il.Emit(OpCodes.Ldloc, vNl); il.Emit(OpCodes.Call, mExit);
            il.Append(finOut);
            il.Append(outerLeave); il.Emit(OpCodes.Leave, ret);
            var outerFin = il.Create(OpCodes.Ldloc, vTaken); il.Append(outerFin);
            var finEnd = il.Create(OpCodes.Endfinally);
            il.Emit(OpCodes.Brfalse, finEnd); il.Emit(OpCodes.Ldloc, vLock); il.Emit(OpCodes.Call, mExit);
            il.Append(finEnd);
            il.Append(ret);
            b.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Finally) { TryStart = innerTry, TryEnd = innerFin, HandlerStart = innerFin, HandlerEnd = outerLeave });
            b.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Finally) { TryStart = outerTry, TryEnd = outerFin, HandlerStart = outerFin, HandlerEnd = ret });
        }
        sb.Methods.Add(mLoad);

        // (7) Skin.<Shepherd>Ensure(skin) (static) and <Shepherd>HasSlot(l, slot) (static)
        var mEnsure = new MethodDefinition("<Shepherd>Ensure", MethodAttributes.Assembly | MethodAttributes.Static | MethodAttributes.HideBySig, ts.Void);
        var eSkin = new ParameterDefinition("skin", ParameterAttributes.None, skinT); mEnsure.Parameters.Add(eSkin);
        {
            var b = mEnsure.Body; b.InitLocals = true; var il = b.GetILProcessor(); var vL = new VariableDefinition(lzT); b.Variables.Add(vL);
            var end = il.Create(OpCodes.Ret); var notLazy = il.Create(OpCodes.Ldarg, eSkin);
            il.Emit(OpCodes.Ldarg, eSkin); il.Emit(OpCodes.Brfalse, end);   // null: the original ldfld throws next, as before
            il.Emit(OpCodes.Ldarg, eSkin); il.Emit(OpCodes.Ldfld, fLazy); il.Emit(OpCodes.Stloc, vL); il.Emit(OpCodes.Ldloc, vL); il.Emit(OpCodes.Brfalse, notLazy);
            il.Emit(OpCodes.Ldarg, eSkin); il.Emit(OpCodes.Ldloc, vL); il.Emit(OpCodes.Call, mLoad); il.Emit(OpCodes.Ret);
            // R3: lazy == null on a skin that was lazy: maybe read by another thread just now; its dictionary is read after a barrier (pairs with
            // the barrier before Load's "lazy = null"). Skins that were never lazy (every skin with SHEPHERD_SPINE_LAZYSKIN=0) take no barrier.
            il.Append(notLazy); il.Emit(OpCodes.Ldfld, fWas); il.Emit(OpCodes.Brfalse, end);
            il.Emit(OpCodes.Call, mBarrier);
            il.Append(end);
        }
        skinT.Methods.Add(mEnsure);
        var mHas = new MethodDefinition("<Shepherd>HasSlot", MethodAttributes.Assembly | MethodAttributes.Static | MethodAttributes.HideBySig, ts.Boolean);
        var hL = new ParameterDefinition("l", ParameterAttributes.None, lzT); var hI = new ParameterDefinition("slotIndex", ParameterAttributes.None, ts.Int32);
        mHas.Parameters.Add(hL); mHas.Parameters.Add(hI);
        {
            var b = mHas.Body; b.InitLocals = true; var il = b.GetILProcessor(); var vB = new VariableDefinition(intArr); b.Variables.Add(vB);
            var notNull = il.Create(OpCodes.Ldarg, hI); var no = il.Create(OpCodes.Ldc_I4_0);
            il.Emit(OpCodes.Ldarg, hL); il.Emit(OpCodes.Ldfld, lSlots); il.Emit(OpCodes.Stloc, vB); il.Emit(OpCodes.Ldloc, vB); il.Emit(OpCodes.Brtrue, notNull);
            il.Emit(OpCodes.Ldc_I4_1); il.Emit(OpCodes.Ret);   // slots unknown: every slot may be filled
            il.Append(notNull); il.Emit(OpCodes.Ldloc, vB); il.Emit(OpCodes.Ldlen); il.Emit(OpCodes.Conv_I4); il.Emit(OpCodes.Ldc_I4_5); il.Emit(OpCodes.Shl); il.Emit(OpCodes.Bge_Un, no);
            il.Emit(OpCodes.Ldloc, vB); il.Emit(OpCodes.Ldarg, hI); il.Emit(OpCodes.Ldc_I4_5); il.Emit(OpCodes.Shr); il.Emit(OpCodes.Ldelem_I4);
            il.Emit(OpCodes.Ldc_I4_1); il.Emit(OpCodes.Ldarg, hI); il.Emit(OpCodes.Ldc_I4, 31); il.Emit(OpCodes.And); il.Emit(OpCodes.Shl); il.Emit(OpCodes.And);
            il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Cgt_Un); il.Emit(OpCodes.Ret);
            il.Append(no); il.Emit(OpCodes.Ret);
        }
        skinT.Methods.Add(mHas);

        // (8) every load of Skin.attachments: the skin it is loaded from read first (dup; call Ensure)
        int sites = 0;
        foreach (var m in mod.GetTypes().SelectMany(t => t.Methods).Where(m => m.HasBody).ToList())
        {
            if (m == mEnsure || m == mLoad || m == mSlots || m == mResolve || m == mSkip || m == mHas || m == mNextLock) continue;
            var ld = m.Body.Instructions.Where(i => i.OpCode == OpCodes.Ldfld && i.Operand is FieldReference fr && fr.Resolve() == fAttachments).ToList();
            if (ld.Count == 0) continue;
            m.Body.SimplifyMacros(); var il = m.Body.GetILProcessor();
            foreach (var i in ld) { il.InsertBefore(i, il.Create(OpCodes.Dup)); il.InsertBefore(i, il.Create(OpCodes.Call, mEnsure)); sites++; }
            m.Body.OptimizeMacros();
        }
        if (sites != 9) errors.Add($"Skin.attachments loaded at {sites} sites, 9 expected");

        // (9) GetAttachment(slot, name) / GetAttachments(slot, list) of a lazy skin that fills no attachment in that slot: what the empty
        // lookup answers (null / nothing added), without a read. (GetAttachment keeps the original path for a null name: the lookup throws
        // there when the dictionary has entries.)
        void Prefix(MethodDefinition m, bool getOne)
        {
            m.Body.SimplifyMacros(); var il = m.Body.GetILProcessor(); var first = m.Body.Instructions[0];
            var vL = new VariableDefinition(lzT); m.Body.Variables.Add(vL); m.Body.InitLocals = true;
            il.InsertBefore(first, il.Create(OpCodes.Ldarg_0)); il.InsertBefore(first, il.Create(OpCodes.Ldfld, fLazy)); il.InsertBefore(first, il.Create(OpCodes.Stloc, vL));
            il.InsertBefore(first, il.Create(OpCodes.Ldloc, vL)); il.InsertBefore(first, il.Create(OpCodes.Brfalse, first));
            if (getOne) { il.InsertBefore(first, il.Create(OpCodes.Ldarg_2)); il.InsertBefore(first, il.Create(OpCodes.Brfalse, first)); }
            il.InsertBefore(first, il.Create(OpCodes.Ldloc, vL)); il.InsertBefore(first, il.Create(OpCodes.Ldarg_1)); il.InsertBefore(first, il.Create(OpCodes.Call, mHas));
            il.InsertBefore(first, il.Create(OpCodes.Brtrue, first));
            if (getOne) il.InsertBefore(first, il.Create(OpCodes.Ldnull));
            il.InsertBefore(first, il.Create(OpCodes.Ret));
            m.Body.OptimizeMacros();
        }
        Prefix(getAtt, true); Prefix(getAtts, false);

        // (10) ReadSkin: after a non-default skin's header (its slot count stored), "if (LazySkip(...)) return skin;"
        {
            var ins = readSkin.Body.Instructions;
            var dflt = ins.FirstOrDefault(i => i.OpCode == OpCodes.Ldstr && (string)i.Operand == "default");
            var br = dflt?.Next?.Next?.Next;   // ldstr "default"; newobj Skin(string); stloc skin; br JOIN
            if (dflt == null || dflt.Next.OpCode != OpCodes.Newobj || br == null || (br.OpCode != OpCodes.Br && br.OpCode != OpCodes.Br_S)) errors.Add("ReadSkin: the default branch's 'br' to the slot loop not found");
            else
            {
                var join = (Instruction)br.Operand; var before = join.Previous;
                var skinLoc = dflt.Next.Next;   // stloc skin
                var countStore = ins.FirstOrDefault(i => i.Offset < dflt.Offset && i.OpCode.Code is Code.Stloc_1 or Code.Stloc or Code.Stloc_S);   // the default branch's count store
                bool ok = before != null && countStore != null && Same(before, countStore) && before.Previous != null && SpinePasses.IsCall(before.Previous, inputT.FullName, "ReadInt", 1)
                    && ins.Count(i => i.Operand == join) == 1;
                // the slot loop (from the join to the end) has exactly these calls in this order: what <Shepherd>ReadSlots does
                var calls = ins.SkipWhile(i => i != join).Where(i => i.OpCode == OpCodes.Call || i.OpCode == OpCodes.Callvirt).Select(i => ((MethodReference)i.Operand).Name).ToList();
                var want = new[] { "ReadInt", "ReadInt", "ReadStringRef", "ReadAttachment", "SetAttachment" };
                if (!calls.SequenceEqual(want)) { ok = false; errors.Add("ReadSkin's slot loop calls [" + string.Join(", ", calls) + "], expected [" + string.Join(", ", want) + "]"); }
                if (!ok) errors.Add("ReadSkin: the non-default branch's slot count store is not right before the slot loop");
                else
                {
                    readSkin.Body.SimplifyMacros(); var il = readSkin.Body.GetILProcessor();
                    var cont = join;
                    var seq = new List<Instruction> {
                        il.Create(OpCodes.Ldarg_0), il.Create(OpCodes.Ldarg_1), il.Create(OpCodes.Ldarg_2), LoadLocal(il, skinLoc), LoadLocal(il, countStore), il.Create(OpCodes.Ldarg, readSkin.Parameters[3]),
                        il.Create(OpCodes.Call, mSkip), il.Create(OpCodes.Brfalse, cont), LoadLocal(il, skinLoc), il.Create(OpCodes.Ret) };
                    var at = before; foreach (var x in seq) { il.InsertAfter(at, x); at = x; }
                    readSkin.Body.OptimizeMacros();
                }
            }
        }
        if (errors.Count > 0) return SpinePasses.Fail("--spine-lazyskin", errors);
        Console.WriteLine($"  spine-unity: --spine-lazyskin: Skin.attachments sites x{sites}; ReadSkin lazy branch x1; linked-mesh loop copied (UpdateUVs {(shared ? "through the --spine-share hook" : "as read")}); AttachmentLoader methods passed on x{loaderMethods}");
        return 0;
    }

    static MethodDefinition _mark;
    /// <summary>static &lt;Shepherd&gt;MarkBit(int[] b, int i): b[i &gt;&gt; 5] |= 1 &lt;&lt; (i &amp; 31) when i is inside b (on SkeletonInput).</summary>
    static MethodDefinition MarkBit(ModuleDefinition mod, TypeDefinition inputT, TypeReference intArr)
    {
        if (_mark != null) return _mark;
        var ts = mod.TypeSystem; var m = new MethodDefinition("<Shepherd>MarkBit", MethodAttributes.Assembly | MethodAttributes.Static | MethodAttributes.HideBySig, ts.Void);
        var pb = new ParameterDefinition("b", ParameterAttributes.None, intArr); var pi = new ParameterDefinition("i", ParameterAttributes.None, ts.Int32); m.Parameters.Add(pb); m.Parameters.Add(pi);
        var il = m.Body.GetILProcessor(); var end = il.Create(OpCodes.Ret);
        il.Emit(OpCodes.Ldarg, pi); il.Emit(OpCodes.Ldarg, pb); il.Emit(OpCodes.Ldlen); il.Emit(OpCodes.Conv_I4); il.Emit(OpCodes.Ldc_I4_5); il.Emit(OpCodes.Shl); il.Emit(OpCodes.Bge_Un, end);
        il.Emit(OpCodes.Ldarg, pb); il.Emit(OpCodes.Ldarg, pi); il.Emit(OpCodes.Ldc_I4_5); il.Emit(OpCodes.Shr); il.Emit(OpCodes.Ldelema, ts.Int32); il.Emit(OpCodes.Dup); il.Emit(OpCodes.Ldind_I4);
        il.Emit(OpCodes.Ldc_I4_1); il.Emit(OpCodes.Ldarg, pi); il.Emit(OpCodes.Ldc_I4, 31); il.Emit(OpCodes.And); il.Emit(OpCodes.Shl); il.Emit(OpCodes.Or); il.Emit(OpCodes.Stind_I4);
        il.Append(end);
        inputT.Methods.Add(m); return _mark = m;
    }
    /// <summary>static &lt;Shepherd&gt;Prune(SkeletonInput input): when lazy skins marked entries (keep != null), every other string-table entry is
    /// cleared (strings.Items[i] = null) and keep dropped.</summary>
    static MethodDefinition PruneMethod(ModuleDefinition mod, TypeDefinition inputT, FieldDefinition fKeep, FieldDefinition fStrings, TypeReference intArr)
    {
        var ts = mod.TypeSystem; var m = new MethodDefinition("<Shepherd>Prune", MethodAttributes.Assembly | MethodAttributes.Static | MethodAttributes.HideBySig, ts.Void);
        var p = new ParameterDefinition("input", ParameterAttributes.None, inputT); m.Parameters.Add(p);
        var b = m.Body; b.InitLocals = true; var il = b.GetILProcessor();
        var vK = new VariableDefinition(intArr); var vItems = new VariableDefinition(new ArrayType(ts.String)); var vN = new VariableDefinition(ts.Int32); var vI = new VariableDefinition(ts.Int32);
        foreach (var v in new[] { vK, vItems, vN, vI }) b.Variables.Add(v);
        var end = il.Create(OpCodes.Ret);
        il.Emit(OpCodes.Ldarg, p); il.Emit(OpCodes.Ldfld, fKeep); il.Emit(OpCodes.Stloc, vK); il.Emit(OpCodes.Ldloc, vK); il.Emit(OpCodes.Brfalse, end);
        il.Emit(OpCodes.Ldarg, p); il.Emit(OpCodes.Ldfld, fStrings); il.Emit(OpCodes.Ldfld, new FieldReference("Items", new ArrayType(((GenericInstanceType)fStrings.FieldType).ElementType.GenericParameters[0]), fStrings.FieldType)); il.Emit(OpCodes.Stloc, vItems);
        il.Emit(OpCodes.Ldarg, p); il.Emit(OpCodes.Ldfld, fStrings); il.Emit(OpCodes.Ldfld, new FieldReference("Count", ts.Int32, fStrings.FieldType)); il.Emit(OpCodes.Stloc, vN);
        il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Stloc, vI);
        var cond = il.Create(OpCodes.Ldloc, vI); il.Emit(OpCodes.Br, cond);
        var body = il.Create(OpCodes.Ldloc, vK); il.Append(body);
        var next = il.Create(OpCodes.Ldloc, vI);
        // if ((k[i >> 5] & (1 << (i & 31))) == 0) items[i] = null
        il.Emit(OpCodes.Ldloc, vI); il.Emit(OpCodes.Ldc_I4_5); il.Emit(OpCodes.Shr); il.Emit(OpCodes.Ldelem_I4);
        il.Emit(OpCodes.Ldc_I4_1); il.Emit(OpCodes.Ldloc, vI); il.Emit(OpCodes.Ldc_I4, 31); il.Emit(OpCodes.And); il.Emit(OpCodes.Shl); il.Emit(OpCodes.And); il.Emit(OpCodes.Brtrue, next);
        il.Emit(OpCodes.Ldloc, vItems); il.Emit(OpCodes.Ldloc, vI); il.Emit(OpCodes.Ldnull); il.Emit(OpCodes.Stelem_Ref);
        il.Append(next); il.Emit(OpCodes.Ldc_I4_1); il.Emit(OpCodes.Add); il.Emit(OpCodes.Stloc, vI);
        il.Append(cond); il.Emit(OpCodes.Ldloc, vN); il.Emit(OpCodes.Blt, body);
        il.Emit(OpCodes.Ldarg, p); il.Emit(OpCodes.Ldnull); il.Emit(OpCodes.Stfld, fKeep);
        il.Append(end);
        return m;
    }
    static bool Same(Instruction a, Instruction b) => a.OpCode == b.OpCode && Equals(a.Operand, b.Operand);
    static Instruction LoadLocal(ILProcessor il, Instruction store)
    {
        var v = store.Operand as VariableDefinition ?? store.OpCode.Code switch { Code.Stloc_0 => il.Body.Variables[0], Code.Stloc_1 => il.Body.Variables[1], Code.Stloc_2 => il.Body.Variables[2], Code.Stloc_3 => il.Body.Variables[3], _ => null };
        return il.Create(OpCodes.Ldloc, v);
    }
    static MethodReference ListCount(ModuleDefinition mod, FieldDefinition list)
    {
        var lt = (GenericInstanceType)list.FieldType; var open = lt.ElementType;
        return new MethodReference("get_Count", mod.TypeSystem.Int32, lt) { HasThis = true };
    }

    /// <summary>The body of ReadSkeletonData's linked-mesh loop ("for (i = 0; i &lt; linkedMeshes.Count captured; i++) { ... }") copied into
    /// target as "n = linkedMeshes.Count; for (i = start; i &lt; n; i++) { the same instructions }; linkedMeshes.RemoveRange(start, Count - start)":
    /// the loop counter, the LinkedMesh / Attachment locals and the SkeletonData local remapped; the shape is checked (the calls it makes).</summary>
    static bool CopyLinkedLoop(MethodDefinition src, MethodDefinition dst, ParameterDefinition pData, ParameterDefinition pStart, FieldDefinition fLinked, TypeDefinition sdT, bool shared, List<string> errors)
    {
        var ins = src.Body.Instructions.ToList();
        // find: ldarg.0; ldfld linkedMeshes; callvirt get_Count; stloc N; ldc.i4.0; stloc I; br COND
        int k = ins.FindIndex(i => i.OpCode == OpCodes.Ldfld && i.Operand is FieldReference fr && fr.Resolve() == fLinked && i.Next != null && i.Next.Operand is MethodReference mr && mr.Name == "get_Count");
        if (k < 1) { errors.Add("linked-mesh loop: linkedMeshes.Count not found"); return false; }
        Instruction getCount = ins[k + 1], stN = ins[k + 2], ldc0 = ins[k + 3], stI = ins[k + 4], brCond = ins[k + 5];
        VariableDefinition Loc(Instruction i) => i.Operand as VariableDefinition ?? (i.OpCode.Code switch { Code.Stloc_0 or Code.Ldloc_0 => src.Body.Variables[0], Code.Stloc_1 or Code.Ldloc_1 => src.Body.Variables[1], Code.Stloc_2 or Code.Ldloc_2 => src.Body.Variables[2], Code.Stloc_3 or Code.Ldloc_3 => src.Body.Variables[3], _ => null });
        if (ldc0.OpCode != OpCodes.Ldc_I4_0 || Loc(stN) == null || Loc(stI) == null || !(brCond.OpCode == OpCodes.Br || brCond.OpCode == OpCodes.Br_S)) { errors.Add("linked-mesh loop: header shape"); return false; }
        var vN = Loc(stN); var vI = Loc(stI); var cond = (Instruction)brCond.Operand;
        int bodyStart = k + 6, condAt = ins.IndexOf(cond);
        // the increment just before COND: ldloc I; ldc.i4.1; add; stloc I
        int incAt = condAt - 4;
        if (incAt <= bodyStart || Loc(ins[incAt]) != vI || ins[incAt + 1].OpCode != OpCodes.Ldc_I4_1 || ins[incAt + 2].OpCode != OpCodes.Add || Loc(ins[incAt + 3]) != vI) { errors.Add("linked-mesh loop: increment shape"); return false; }
        if (Loc(ins[condAt]) != vI || Loc(ins[condAt + 1]) != vN || !(ins[condAt + 2].OpCode == OpCodes.Blt || ins[condAt + 2].OpCode == OpCodes.Blt_S) || ins[condAt + 2].Operand != ins[bodyStart]) { errors.Add("linked-mesh loop: condition shape"); return false; }
        var body = ins.GetRange(bodyStart, incAt - bodyStart);
        var calls = body.Where(i => i.OpCode == OpCodes.Call || i.OpCode == OpCodes.Callvirt).Select(i => ((MethodReference)i.Operand).Name).ToList();
        var want = new[] { "get_Item", "FindSkin", "get_DefaultSkin", "Concat", "GetAttachment", "Concat", "set_DeformAttachment", "set_ParentMesh", shared ? "<Shepherd>UpdateUVs" : "UpdateUVs" };
        if (!calls.SequenceEqual(want)) { errors.Add("linked-mesh loop calls [" + string.Join(", ", calls) + "]"); return false; }
        // locals the body uses: I, the SkeletonData local, and its own temporaries (LinkedMesh, Attachment)
        var dataLoc = src.Body.Variables.FirstOrDefault(v => v.VariableType.FullName == sdT.FullName);
        var used = body.Select(Loc).Where(v => v != null).Distinct().ToList();
        if (dataLoc == null || !used.Contains(dataLoc) || used.Any(v => v != vI && v != dataLoc && v.VariableType.FullName is not ("Spine.SkeletonJson/LinkedMesh" or "Spine.Attachment"))) { errors.Add("linked-mesh loop: locals [" + string.Join(", ", used.Select(v => v.VariableType.Name)) + "]"); return false; }
        if (body.Any(i => i.OpCode.Code is Code.Ldarg or Code.Ldarg_S or Code.Ldarg_1 or Code.Ldarg_2 or Code.Ldarg_3 or Code.Starg or Code.Starg_S or Code.Ldarga or Code.Ldarga_S || (i.OpCode.FlowControl is FlowControl.Branch or FlowControl.Cond_Branch && i.Operand is Instruction t && !body.Contains(t) && t != ins[incAt]))) { errors.Add("linked-mesh loop: argument use or a branch out of the body"); return false; }
        var b = dst.Body; b.InitLocals = true; var il = b.GetILProcessor(); var map = new Dictionary<VariableDefinition, VariableDefinition>();
        var nI = new VariableDefinition(src.Module.TypeSystem.Int32); var nN = new VariableDefinition(src.Module.TypeSystem.Int32); b.Variables.Add(nI); b.Variables.Add(nN); map[vI] = nI;
        foreach (var v in used.Where(v => v != vI && v != dataLoc)) { var nv = new VariableDefinition(v.VariableType); b.Variables.Add(nv); map[v] = nv; }
        var countRef = (MethodReference)getCount.Operand;
        // n = linkedMeshes.Count; i = start; br cond
        il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldfld, fLinked); il.Emit(OpCodes.Callvirt, countRef); il.Emit(OpCodes.Stloc, nN);
        il.Emit(OpCodes.Ldarg, pStart); il.Emit(OpCodes.Stloc, nI);
        var nCond = il.Create(OpCodes.Ldloc, nI); var nInc = il.Create(OpCodes.Ldloc, nI);
        il.Emit(OpCodes.Br, nCond);
        var copies = new Dictionary<Instruction, Instruction>();
        foreach (var i in body)
        {
            Instruction c;
            var v = Loc(i);
            if (v == dataLoc) c = il.Create(OpCodes.Ldarg, pData);   // only ever loaded (checked: no store to it in the body)
            else if (v != null) c = il.Create(i.OpCode.Code is Code.Stloc or Code.Stloc_S or Code.Stloc_0 or Code.Stloc_1 or Code.Stloc_2 or Code.Stloc_3 ? OpCodes.Stloc : i.OpCode.Code is Code.Ldloca or Code.Ldloca_S ? OpCodes.Ldloca : OpCodes.Ldloc, map[v]);
            else c = i.Operand switch { null => il.Create(i.OpCode), Instruction _ => il.Create(OpCodes.Nop), MethodReference mr => il.Create(i.OpCode, mr), FieldReference fr => il.Create(i.OpCode, fr), TypeReference tr => il.Create(i.OpCode, tr), string s => il.Create(i.OpCode, s), int n => il.Create(i.OpCode, n), sbyte sb2 => il.Create(i.OpCode, sb2), _ => null };
            if (c == null) { errors.Add("linked-mesh loop: operand " + i); return false; }
            if (v == dataLoc && i.OpCode.Code is Code.Stloc or Code.Stloc_S or Code.Stloc_0 or Code.Stloc_1 or Code.Stloc_2 or Code.Stloc_3 or Code.Ldloca or Code.Ldloca_S) { errors.Add("linked-mesh loop: the SkeletonData local is stored / addressed"); return false; }
            copies[i] = c; il.Append(c);
        }
        foreach (var i in body.Where(i => i.Operand is Instruction))
        {
            var t = (Instruction)i.Operand; var c = copies[i];
            c.OpCode = i.OpCode.Code switch { Code.Br_S => OpCodes.Br, Code.Brtrue_S => OpCodes.Brtrue, Code.Brfalse_S => OpCodes.Brfalse, Code.Blt_S => OpCodes.Blt, Code.Bne_Un_S => OpCodes.Bne_Un, Code.Beq_S => OpCodes.Beq, _ => i.OpCode };
            c.Operand = t == ins[incAt] ? nInc : copies[t];
        }
        il.Append(nInc); il.Emit(OpCodes.Ldc_I4_1); il.Emit(OpCodes.Add); il.Emit(OpCodes.Stloc, nI);
        il.Append(nCond); il.Emit(OpCodes.Ldloc, nN); il.Emit(OpCodes.Blt, copies[body[0]]);
        // linkedMeshes.RemoveRange(start, linkedMeshes.Count - start)
        var lt = (GenericInstanceType)fLinked.FieldType;
        var removeRange = new MethodReference("RemoveRange", src.Module.TypeSystem.Void, lt) { HasThis = true };
        removeRange.Parameters.Add(new ParameterDefinition(src.Module.TypeSystem.Int32)); removeRange.Parameters.Add(new ParameterDefinition(src.Module.TypeSystem.Int32));
        il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldfld, fLinked); il.Emit(OpCodes.Ldarg, pStart);
        il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldfld, fLinked); il.Emit(OpCodes.Callvirt, countRef); il.Emit(OpCodes.Ldarg, pStart); il.Emit(OpCodes.Sub);
        il.Emit(OpCodes.Callvirt, removeRange);
        il.Emit(OpCodes.Ret);
        return true;
    }
}

/// <summary>Kit #7, "native clipping" (vita/notes/kit7-spine.md): SkeletonClipping.ClipTriangles through Shepherd.Hooks.SpineClip -- a handler the
/// engine installs runs the same scalar operations in the same order natively (vita/shepherd/spine_clip.h) and writes the three output lists
/// exactly as the managed loop leaves them; no handler (or the handler declining) = the original body. Filled in below.</summary>
static class SpineClipPass
{
    public static int Apply(AssemblyDefinition a)
    {
        var mod = a.MainModule; var ts = mod.TypeSystem; var errors = new List<string>();
        var sc = mod.GetType("Spine.SkeletonClipping"); var exposedT = mod.GetType("Spine.ExposedList`1");
        if (sc == null || exposedT == null) { errors.Add("Spine.SkeletonClipping / ExposedList`1 missing"); return SpinePasses.Fail("--spine-clip", errors); }
        if (sc.Fields.Any(f => f.Name.StartsWith("<Shepherd>"))) { errors.Add("already rewritten"); return SpinePasses.Fail("--spine-clip", errors); }
        var start = SpinePasses.One(sc, "ClipStart", 2, errors); var tris = SpinePasses.One(sc, "ClipTriangles", 5, errors);
        var fPoly = SpinePasses.Field(sc, "clippingPolygon", errors); var fPolys = SpinePasses.Field(sc, "clippingPolygons", errors);
        var fOutV = SpinePasses.Field(sc, "clippedVertices", errors); var fOutUV = SpinePasses.Field(sc, "clippedUVs", errors); var fOutT = SpinePasses.Field(sc, "clippedTriangles", errors);
        if (errors.Count > 0) return SpinePasses.Fail("--spine-clip", errors);
        var fNative = new FieldDefinition("<Shepherd>native", FieldAttributes.Assembly, ts.Object); sc.Fields.Add(fNative);
        var hooks = SpinePasses.HooksType(mod, "SpineClip"); var resT = SpinePasses.HooksType(mod, "ClipResult");
        var floatArr = new ArrayType(ts.Single); var intArr = new ArrayType(ts.Int32);
        var fMode = new FieldReference("Mode", ts.Int32, hooks);
        var hStart = SpinePasses.Ref(hooks, "Start", ts.Object, false, ts.Object, floatArr, ts.Int32);
        var hPolys = SpinePasses.Ref(hooks, "Polygons", ts.Int32, false, ts.Object);
        var hTris = SpinePasses.Ref(hooks, "Tris", resT, false, ts.Object, floatArr, ts.Int32, intArr, ts.Int32, floatArr);
        var hCheckStart = SpinePasses.Ref(hooks, "CheckStart", ts.Void, false, ts.Object, ts.Object);
        var hCheckTris = SpinePasses.Ref(hooks, "CheckTris", ts.Void, false, ts.Object, floatArr, ts.Int32, intArr, ts.Int32, floatArr, ts.Object, ts.Object, ts.Object);
        FieldReference R(string n, TypeReference t) => new FieldReference(n, t, resT);
        var rV = R("V", floatArr); var rUV = R("UV", floatArr); var rT = R("T", intArr); var rNV = R("NV", ts.Int32); var rNT = R("NT", ts.Int32); var rMaxT = R("MaxT", ts.Int32);
        // ExposedList<float> / <int> members through their instance types
        MethodReference Clear(TypeReference lt) { var m = new MethodReference("Clear", ts.Void, lt) { HasThis = true }; m.Parameters.Add(new ParameterDefinition(ts.Boolean)); return m; }
        MethodReference Resize(TypeReference lt) { var m = new MethodReference("Resize", lt.GetElementType() == exposedT ? (TypeReference)lt : lt, lt) { HasThis = true }; m.ReturnType = new GenericInstanceType(exposedT) { GenericArguments = { exposedT.GenericParameters[0] } }; m.Parameters.Add(new ParameterDefinition(ts.Int32)); return m; }
        FieldReference Items(TypeReference lt) => new FieldReference("Items", new ArrayType(exposedT.GenericParameters[0]), lt);
        var arrayCopy = SpinePasses.Ref(SpinePasses.Corlib(mod, "System", "Array"), "Copy", ts.Void, false, SpinePasses.Corlib(mod, "System", "Array"), ts.Int32, SpinePasses.Corlib(mod, "System", "Array"), ts.Int32, ts.Int32);

        // (1) ClipStart: right after the clipping polygon's world vertices -- if (Mode != 0) { native = Start(native, polygon.Items, n);
        //     int k = Polygons(native); if (k >= 0) return k; }; and in check mode, before its last return, CheckStart(native, clippingPolygons)
        {
            var b = start.Body; b.SimplifyMacros(); var il = b.GetILProcessor(); var ins = b.Instructions;
            var cwv = ins.Where(i => SpinePasses.IsCall(i, "Spine.VertexAttachment", "ComputeWorldVertices", 6)).ToList();
            var locals = b.Variables; var nLoc = locals.FirstOrDefault(v => v.Index == 0 && v.VariableType.MetadataType == MetadataType.Int32); var itemsLoc = locals.FirstOrDefault(v => v.Index == 1 && v.VariableType.FullName == "System.Single[]");
            var rets = ins.Where(i => i.OpCode == OpCodes.Ret).ToList();
            if (cwv.Count != 1 || nLoc == null || itemsLoc == null || rets.Count != 2) errors.Add($"ClipStart: shape ({cwv.Count} ComputeWorldVertices, locals n / items {(nLoc != null)} / {(itemsLoc != null)}, {rets.Count} returns)");
            else
            {
                var cont = cwv[0].Next; var popCont = il.Create(OpCodes.Pop);
                var seq = new List<Instruction> {
                    il.Create(OpCodes.Ldsfld, fMode), il.Create(OpCodes.Brfalse, cont),
                    il.Create(OpCodes.Ldarg_0), il.Create(OpCodes.Ldarg_0), il.Create(OpCodes.Ldfld, fNative), il.Create(OpCodes.Ldloc, itemsLoc), il.Create(OpCodes.Ldloc, nLoc), il.Create(OpCodes.Call, hStart), il.Create(OpCodes.Stfld, fNative),
                    il.Create(OpCodes.Ldarg_0), il.Create(OpCodes.Ldfld, fNative), il.Create(OpCodes.Call, hPolys),
                    il.Create(OpCodes.Dup), il.Create(OpCodes.Ldc_I4_0), il.Create(OpCodes.Blt, popCont), il.Create(OpCodes.Ret), popCont };
                var at = cwv[0]; foreach (var x in seq) { il.InsertAfter(at, x); at = x; }
                var last = rets[1];   // the end: the polygon count on the stack
                var skip = last;
                var chk = new List<Instruction> {
                    il.Create(OpCodes.Ldsfld, fMode), il.Create(OpCodes.Ldc_I4_2), il.Create(OpCodes.Bne_Un, skip),
                    il.Create(OpCodes.Ldarg_0), il.Create(OpCodes.Ldfld, fNative), il.Create(OpCodes.Ldarg_0), il.Create(OpCodes.Ldfld, fPolys), il.Create(OpCodes.Call, hCheckStart) };
                foreach (var x in chk) il.InsertBefore(last, x);
                foreach (var i in ins) if (i.Operand == last && !chk.Contains(i)) i.Operand = chk[0];
            }
            b.OptimizeMacros();
        }
        // (2) ClipTriangles: if (clippingPolygons == null && native != null) { r = Tris(...); clippedVertices.Clear(); clippedUVs.Clear(); clippedTriangles.Clear();
        //     clippedVertices.Resize(r.NV); clippedUVs.Resize(r.NV); clippedTriangles.Resize(r.MaxT); clippedTriangles.Resize(r.NT); Array.Copy x3; return; }
        //     and in check mode, before its return, CheckTris(native, the inputs, the three lists)
        {
            var b = tris.Body; b.SimplifyMacros(); var il = b.GetILProcessor(); var ins = b.Instructions; var first = ins[0];
            var rets = ins.Where(i => i.OpCode == OpCodes.Ret).ToList();
            if (rets.Count != 1) errors.Add($"ClipTriangles: {rets.Count} returns, 1 expected");
            else
            {
                var vR = new VariableDefinition(resT); b.Variables.Add(vR); b.InitLocals = true;
                var fl = fOutV.FieldType; var ul = fOutUV.FieldType; var tl = fOutT.FieldType;
                var seq = new List<Instruction> {
                    il.Create(OpCodes.Ldarg_0), il.Create(OpCodes.Ldfld, fPolys), il.Create(OpCodes.Brtrue, first),
                    il.Create(OpCodes.Ldarg_0), il.Create(OpCodes.Ldfld, fNative), il.Create(OpCodes.Brfalse, first),
                    il.Create(OpCodes.Ldarg_0), il.Create(OpCodes.Ldfld, fNative), il.Create(OpCodes.Ldarg_1), il.Create(OpCodes.Ldarg_2), il.Create(OpCodes.Ldarg_3), il.Create(OpCodes.Ldarg, tris.Parameters[3]), il.Create(OpCodes.Ldarg, tris.Parameters[4]),
                    il.Create(OpCodes.Call, hTris), il.Create(OpCodes.Stloc, vR) };
                foreach (var (f, t) in new[] { (fOutV, fl), (fOutUV, ul), (fOutT, tl) }) { seq.Add(il.Create(OpCodes.Ldarg_0)); seq.Add(il.Create(OpCodes.Ldfld, f)); seq.Add(il.Create(OpCodes.Ldc_I4_1)); seq.Add(il.Create(OpCodes.Callvirt, Clear(t))); }
                foreach (var (f, t, n) in new[] { (fOutV, fl, rNV), (fOutUV, ul, rNV), (fOutT, tl, rMaxT), (fOutT, tl, rNT) })
                { seq.Add(il.Create(OpCodes.Ldarg_0)); seq.Add(il.Create(OpCodes.Ldfld, f)); seq.Add(il.Create(OpCodes.Ldloc, vR)); seq.Add(il.Create(OpCodes.Ldfld, n)); seq.Add(il.Create(OpCodes.Callvirt, Resize(t))); seq.Add(il.Create(OpCodes.Pop)); }
                foreach (var (src, f, t, n) in new[] { (rV, fOutV, fl, rNV), (rUV, fOutUV, ul, rNV), (rT, fOutT, tl, rNT) })
                { seq.Add(il.Create(OpCodes.Ldloc, vR)); seq.Add(il.Create(OpCodes.Ldfld, src)); seq.Add(il.Create(OpCodes.Ldc_I4_0)); seq.Add(il.Create(OpCodes.Ldarg_0)); seq.Add(il.Create(OpCodes.Ldfld, f)); seq.Add(il.Create(OpCodes.Ldfld, Items(t))); seq.Add(il.Create(OpCodes.Ldc_I4_0)); seq.Add(il.Create(OpCodes.Ldloc, vR)); seq.Add(il.Create(OpCodes.Ldfld, n)); seq.Add(il.Create(OpCodes.Call, arrayCopy)); }
                seq.Add(il.Create(OpCodes.Ret));
                foreach (var x in seq) il.InsertBefore(first, x);
                var last = rets[0];
                var chk = new List<Instruction> {
                    il.Create(OpCodes.Ldsfld, fMode), il.Create(OpCodes.Ldc_I4_2), il.Create(OpCodes.Bne_Un, last),
                    il.Create(OpCodes.Ldarg_0), il.Create(OpCodes.Ldfld, fNative), il.Create(OpCodes.Ldarg_1), il.Create(OpCodes.Ldarg_2), il.Create(OpCodes.Ldarg_3), il.Create(OpCodes.Ldarg, tris.Parameters[3]), il.Create(OpCodes.Ldarg, tris.Parameters[4]),
                    il.Create(OpCodes.Ldarg_0), il.Create(OpCodes.Ldfld, fOutV), il.Create(OpCodes.Ldarg_0), il.Create(OpCodes.Ldfld, fOutUV), il.Create(OpCodes.Ldarg_0), il.Create(OpCodes.Ldfld, fOutT), il.Create(OpCodes.Call, hCheckTris) };
                foreach (var x in chk) il.InsertBefore(last, x);
                foreach (var i in ins) if (i.Operand == last && !chk.Contains(i)) i.Operand = chk[0];
            }
            b.OptimizeMacros();
        }
        if (errors.Count > 0) return SpinePasses.Fail("--spine-clip", errors);
        Console.WriteLine("  spine-unity: --spine-clip: ClipStart hook after ComputeWorldVertices x1 (+ check at its end); ClipTriangles native prefix x1 (+ check at its end)");
        return 0;
    }
}
