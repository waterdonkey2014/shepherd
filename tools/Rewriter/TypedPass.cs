// Kit #9, track runtime (vita/notes/kit9-runtime.md): `Rewriter ... --typed <list>` -- the fast paths in front of chosen icall stubs.
// Each listed stub keeps kit #8's object[] forwarder as its slow path, unchanged, and gets a prefix:
//   lever 10 (a list line `typed <key>`): if Shepherd.Hooks.TypedIcalls.On, call TypedIcalls.<shape>(id, args...) -- the arguments as they
//            are (no object[], no boxes), struct results / arguments as their float fields; it returns false when the engine bound no typed
//            implementation for the id, and the forwarder runs. Shapes are inferred from the signature (Infer below); a signature with no
//            shape is refused.
//   E5       (a line `mirror <key>`): if Shepherd.Hooks.Mirror.On, count the hit in Mirror.Hits[id] and return the engine icall's own
//            formula over Mirror's fields -- the engine keeps that state in those very fields (Shepherd.Engine Runtime/Time.cs) or set the
//            constant there; no call at all.
// Results are given back exactly as the forwarder's Icalls.Ret<T> would (a reference result through isinst T). A module with a rewritten
// stub gets a new MVID derived from its old one (an AOT image or a card made for kit #8's module can never bind to it silently).
// The list refuses unknown keys, keys listed twice and mirrors with no formula here; nothing is written on a refusal.
using Mono.Cecil; using Mono.Cecil.Cil; using System.Security.Cryptography;

static class TypedPass
{
    enum P { Str, Int, OutV3, OutQ, OutRect, RefV3 }
    enum R { Void, F, I, B, Ref }
    record Shape(string Name, R Ret, P[] Params);

    public static int Run(string[] lines, List<(AssemblyDefinition asm, MethodDefinition m, int id, string key)> all)
    {
        var byKey = all.ToDictionary(x => x.key);
        var errors = new List<string>(); var done = new HashSet<string>(); var changed = new HashSet<ModuleDefinition>();
        int nt = 0, nm = 0;
        foreach (var raw in lines)
        {
            var line = raw.Trim(); if (line.Length == 0 || line.StartsWith("#")) continue;
            var sp = line.IndexOf(' '); if (sp < 0) { errors.Add($"bad line '{line}'"); continue; }
            var kind = line[..sp]; var key = line[(sp + 1)..].Trim();
            if (!done.Add(key)) { errors.Add($"{key}: listed twice"); continue; }
            if (!byKey.TryGetValue(key, out var x)) { errors.Add($"{key}: no such icall"); continue; }
            if (kind == "mirror")
            {
                if (!Mirrors.TryGetValue(key, out var emit)) { errors.Add($"{key}: no mirror formula in TypedPass"); continue; }
                Prefix(x.m, (il, slow) =>
                {
                    var mT = MirrorT(x.m.Module); var ts = x.m.Module.TypeSystem;
                    il.Add(Instruction.Create(OpCodes.Ldsfld, F(x.m.Module, mT, "On", ts.Boolean)));
                    il.Add(Instruction.Create(OpCodes.Brfalse, slow));
                    // review fix: the hit counted, as the forwarder's Icalls.Call would -- Mirror.Hits[id]++ (a plain increment, no call:
                    // the path stays zero-hop; Icalls.HitCount adds Mirror.Hits in, so icall-hits.txt / MemReport keep these keys)
                    il.Add(Instruction.Create(OpCodes.Ldsfld, F(x.m.Module, mT, "Hits", new ArrayType(ts.Int64))));
                    il.Add(Instruction.Create(OpCodes.Ldc_I4, x.id));
                    il.Add(Instruction.Create(OpCodes.Ldelema, ts.Int64));
                    il.Add(Instruction.Create(OpCodes.Dup));
                    il.Add(Instruction.Create(OpCodes.Ldind_I8));
                    il.Add(Instruction.Create(OpCodes.Ldc_I4_1));
                    il.Add(Instruction.Create(OpCodes.Conv_I8));
                    il.Add(Instruction.Create(OpCodes.Add));
                    il.Add(Instruction.Create(OpCodes.Stind_I8));
                    emit(x.m.Module, mT, il);
                });
                nm++;
            }
            else if (kind == "typed")
            {
                var sh = Infer(x.m);
                if (sh == null) { errors.Add($"{key}: no typed shape for {x.m.FullName}"); continue; }
                EmitTyped(x.m, x.id, sh);
                nt++;
            }
            else { errors.Add($"{key}: unknown kind '{kind}'"); continue; }
            changed.Add(x.m.Module);
        }
        if (errors.Count > 0) { foreach (var e in errors) Console.Error.WriteLine("  --typed: " + e); return 2; }
        foreach (var mod in changed)
        {
            // (review fix: "v2" -- the mirrored stubs now count their hits; a v1 module's AOT image or card must not bind to a v2 module)
            var h = SHA256.HashData(mod.Mvid.ToByteArray().Concat(System.Text.Encoding.ASCII.GetBytes("shepherd kit #9 typed icall stubs v2 (mirror hits)")).ToArray());
            mod.Mvid = new Guid(h.AsSpan(0, 16));
        }
        Console.WriteLine($"  --typed: {nt} typed stubs, {nm} mirrored getters in {changed.Count} modules ({string.Join(", ", changed.Select(m => m.Assembly.Name.Name).OrderBy(s => s))}); new MVIDs for those modules");
        return 0;
    }

    // ---- the fast path prefix: everything before the forwarder's first instruction (the forwarder itself is not touched) ----
    static void Prefix(MethodDefinition m, Action<List<Instruction>, Instruction> build)
    {
        var body = m.Body; var slow = body.Instructions[0]; var pre = new List<Instruction>();
        build(pre, slow);
        var il = body.GetILProcessor();
        foreach (var i in pre) il.InsertBefore(slow, i);
    }

    static TypeReference HooksT(ModuleDefinition mod, string name)
    {
        var hooksRef = mod.AssemblyReferences.First(r => r.Name == "Shepherd.Hooks");   // (pass 1 added it to every module with an icall)
        return new TypeReference("Shepherd.Hooks", name, mod, hooksRef);
    }
    static TypeReference MirrorT(ModuleDefinition mod) => HooksT(mod, "Mirror");
    static FieldReference F(ModuleDefinition mod, TypeReference t, string name, TypeReference ft) => new FieldReference(name, ft, t);

    // ---- lever 10: shape inference and the typed call ----
    static bool Is(TypeReference t, string full) => t.FullName == full;
    static bool IsIntLike(TypeReference t)
    {
        if (Is(t, "System.Int32")) return true;
        try { var d = t.Resolve(); return d != null && d.IsEnum && d.Fields.First(f => !f.IsStatic).FieldType.FullName == "System.Int32"; } catch { return false; }
    }
    static bool IsRefType(TypeReference t)
    {
        if (t is GenericParameter || t is ByReferenceType || t is PointerType) return false;
        if (t is ArrayType) return true;
        try { var d = t.Resolve(); return d != null && !d.IsValueType; } catch { return false; }
    }
    static Shape Infer(MethodDefinition m)
    {
        if (!m.IsStatic && m.DeclaringType.IsValueType) return null;
        if (m.HasGenericParameters || m.DeclaringType.HasGenericParameters) return null;
        R ret = m.ReturnType.MetadataType switch
        {
            MetadataType.Void => R.Void, MetadataType.Single => R.F, MetadataType.Int32 => R.I, MetadataType.Boolean => R.B,
            _ => IsRefType(m.ReturnType) ? R.Ref : (R)(-1)
        };
        if ((int)ret < 0) return null;
        var ps = new List<P>();
        foreach (var p in m.Parameters)
        {
            var t = p.ParameterType;
            if (t is ByReferenceType br)
            {
                var e = br.ElementType.FullName;
                if (p.IsOut && e == "UnityEngine.Vector3") ps.Add(P.OutV3);
                else if (p.IsOut && e == "UnityEngine.Quaternion") ps.Add(P.OutQ);
                else if (p.IsOut && e == "UnityEngine.Rect") ps.Add(P.OutRect);
                else if (!p.IsOut && e == "UnityEngine.Vector3" && !p.CustomAttributes.Any(c => c.AttributeType.Name == "IsReadOnlyAttribute")) ps.Add(P.RefV3);
                else return null;
            }
            else if (Is(t, "System.String")) ps.Add(P.Str);
            else if (IsIntLike(t)) ps.Add(P.Int);
            else return null;
        }
        string name = (m.IsStatic, ps.Count, ret) switch
        {
            (true, 0, R.F) => "F_V", (true, 0, R.I) => "I_V", (true, 0, R.B) => "B_V",
            (true, 1, R.F) when ps[0] == P.Str => "F_S", (true, 1, R.B) when ps[0] == P.Str => "B_S",
            (true, 1, R.B) when ps[0] == P.Int => "B_I", (true, 1, R.Ref) when ps[0] == P.Int => "O_I",
            (false, 0, R.Ref) => "O_O", (false, 0, R.B) => "B_O", (false, 0, R.I) => "I_O",
            (false, 1, R.F) when ps[0] == P.Int => "F_OI",
            (false, 1, R.Void) when ps[0] == P.OutV3 => "V3_O",
            (false, 1, R.Void) when ps[0] is P.OutQ or P.OutRect => "V4_O",
            (false, 1, R.Void) when ps[0] == P.RefV3 => "S_OV3",
            _ => null
        };
        // an int-like (enum) RESULT is refused above (Ret<TEnum> of a boxed int would be default); int results are plain Int32 here
        return name == null ? null : new Shape(name, ret, ps.ToArray());
    }

    static void EmitTyped(MethodDefinition m, int id, Shape sh)
    {
        var mod = m.Module; var ts = mod.TypeSystem; var body = m.Body;
        var typedT = HooksT(mod, "TypedIcalls");
        var call = new MethodReference(sh.Name, ts.Boolean, typedT) { HasThis = false };
        call.Parameters.Add(new ParameterDefinition(ts.Int32));
        if (!m.IsStatic) call.Parameters.Add(new ParameterDefinition(ts.Object));
        VariableDefinition Loc(TypeReference t) { var v = new VariableDefinition(t); body.Variables.Add(v); return v; }
        var outs = new List<VariableDefinition>();
        Prefix(m, (il, slow) =>
        {
            il.Add(Instruction.Create(OpCodes.Ldsfld, new FieldReference("On", ts.Boolean, typedT)));
            il.Add(Instruction.Create(OpCodes.Brfalse, slow));
            il.Add(Instruction.Create(OpCodes.Ldc_I4, id));
            if (!m.IsStatic) il.Add(Instruction.Create(OpCodes.Ldarg_0));
            ParameterDefinition structParam = null; TypeDefinition structType = null;
            for (int k = 0; k < sh.Params.Length; k++)
            {
                var p = m.Parameters[k];
                switch (sh.Params[k])
                {
                    case P.Str: il.Add(Instruction.Create(OpCodes.Ldarg, p)); call.Parameters.Add(new ParameterDefinition(ts.String)); break;
                    case P.Int: il.Add(Instruction.Create(OpCodes.Ldarg, p)); call.Parameters.Add(new ParameterDefinition(ts.Int32)); break;
                    case P.RefV3:
                        structType = ((ByReferenceType)p.ParameterType).ElementType.Resolve();
                        foreach (var fn in new[] { "x", "y", "z" })
                        {
                            il.Add(Instruction.Create(OpCodes.Ldarg, p));
                            il.Add(Instruction.Create(OpCodes.Ldfld, mod.ImportReference(structType.Fields.First(f => f.Name == fn))));
                            call.Parameters.Add(new ParameterDefinition(ts.Single));
                        }
                        break;
                    case P.OutV3: case P.OutQ: case P.OutRect:
                        structParam = p; structType = ((ByReferenceType)p.ParameterType).ElementType.Resolve();
                        int n = sh.Params[k] == P.OutV3 ? 3 : 4;
                        for (int j = 0; j < n; j++)
                        {
                            var v = Loc(ts.Single); outs.Add(v);
                            il.Add(Instruction.Create(OpCodes.Ldloca, v));
                            call.Parameters.Add(new ParameterDefinition(new ByReferenceType(ts.Single)) { Attributes = ParameterAttributes.Out });
                        }
                        break;
                }
            }
            VariableDefinition r = null;
            if (sh.Ret != R.Void)
            {
                var rt = sh.Ret switch { R.F => ts.Single, R.I => ts.Int32, R.B => ts.Boolean, _ => ts.Object };
                r = Loc(rt);
                il.Add(Instruction.Create(OpCodes.Ldloca, r));
                call.Parameters.Add(new ParameterDefinition(new ByReferenceType(rt)) { Attributes = ParameterAttributes.Out });
            }
            il.Add(Instruction.Create(OpCodes.Call, call));
            il.Add(Instruction.Create(OpCodes.Brfalse, slow));
            if (r != null)
            {
                il.Add(Instruction.Create(OpCodes.Ldloc, r));
                if (sh.Ret == R.Ref) il.Add(Instruction.Create(OpCodes.Isinst, m.ReturnType));   // Icalls.Ret<T>: o is T v ? v : default
            }
            if (structParam != null)
            {
                if (sh.Params.Contains(P.OutRect))
                {
                    // Rect(x, y, width, height): m_XMin = x, m_YMin = y, m_Width = width, m_Height = height -- the struct the forwarder stores
                    var ctor = structType.Methods.First(c => c.IsConstructor && !c.IsStatic && c.Parameters.Count == 4 && c.Parameters.All(q => q.ParameterType.FullName == "System.Single"));
                    il.Add(Instruction.Create(OpCodes.Ldarg, structParam));
                    foreach (var v in outs) il.Add(Instruction.Create(OpCodes.Ldloc, v));
                    il.Add(Instruction.Create(OpCodes.Call, mod.ImportReference(ctor)));
                }
                else
                {
                    var names = sh.Params.Contains(P.OutQ) ? new[] { "x", "y", "z", "w" } : new[] { "x", "y", "z" };
                    for (int j = 0; j < names.Length; j++)
                    {
                        il.Add(Instruction.Create(OpCodes.Ldarg, structParam));
                        il.Add(Instruction.Create(OpCodes.Ldloc, outs[j]));
                        il.Add(Instruction.Create(OpCodes.Stfld, mod.ImportReference(structType.Fields.First(f => f.Name == names[j]))));
                    }
                }
            }
            il.Add(Instruction.Create(OpCodes.Ret));
        });
    }

    // ---- E5: the mirrored getters' formulas -- each is the engine hook's own expression (Shepherd.Engine Icalls/AppIcalls.cs, RenderIcalls.cs,
    // InputIcalls.cs) over the Mirror fields that hold that state ----
    static Dictionary<string, Action<ModuleDefinition, TypeReference, List<Instruction>>> Mirrors = new()
    {
        // Dt: UTime.InFixedStep ? UTime.FixedDeltaTime : UTime.DeltaTime
        ["UnityEngine.Time::get_deltaTime"] = (mod, t, il) => { var ts = mod.TypeSystem; var l1 = Instruction.Create(OpCodes.Ldsfld, F(mod, t, "DeltaTime", ts.Single));
            il.Add(Instruction.Create(OpCodes.Ldsfld, F(mod, t, "InFixedStep", ts.Boolean))); il.Add(Instruction.Create(OpCodes.Brfalse, l1));
            il.Add(Instruction.Create(OpCodes.Ldsfld, F(mod, t, "FixedDeltaTime", ts.Single))); il.Add(Instruction.Create(OpCodes.Ret));
            il.Add(l1); il.Add(Instruction.Create(OpCodes.Ret)); },
        // UDt: WallFixed ? FixedUnscaledDeltaTime : UnscaledDeltaTime, WallFixed = InFixedStep && (Wall || FrameFixed)
        ["UnityEngine.Time::get_unscaledDeltaTime"] = (mod, t, il) => { var ts = mod.TypeSystem;
            var lu = Instruction.Create(OpCodes.Ldsfld, F(mod, t, "UnscaledDeltaTime", ts.Single)); var lf = Instruction.Create(OpCodes.Ldsfld, F(mod, t, "FixedUnscaledDeltaTime", ts.Single));
            il.Add(Instruction.Create(OpCodes.Ldsfld, F(mod, t, "InFixedStep", ts.Boolean))); il.Add(Instruction.Create(OpCodes.Brfalse, lu));
            il.Add(Instruction.Create(OpCodes.Ldsfld, F(mod, t, "Wall", ts.Boolean))); il.Add(Instruction.Create(OpCodes.Brtrue, lf));
            il.Add(Instruction.Create(OpCodes.Ldsfld, F(mod, t, "FrameFixed", ts.Boolean))); il.Add(Instruction.Create(OpCodes.Brfalse, lu));
            il.Add(lf); il.Add(Instruction.Create(OpCodes.Ret)); il.Add(lu); il.Add(Instruction.Create(OpCodes.Ret)); },
        // T_: (float)(WallFixed ? UTime.FixedNow : UTime.Now)
        ["UnityEngine.Time::get_time"] = (mod, t, il) => { var ts = mod.TypeSystem;
            var ln = Instruction.Create(OpCodes.Ldsfld, F(mod, t, "Now", ts.Double)); var lf = Instruction.Create(OpCodes.Ldsfld, F(mod, t, "FixedNow", ts.Double));
            il.Add(Instruction.Create(OpCodes.Ldsfld, F(mod, t, "InFixedStep", ts.Boolean))); il.Add(Instruction.Create(OpCodes.Brfalse, ln));
            il.Add(Instruction.Create(OpCodes.Ldsfld, F(mod, t, "Wall", ts.Boolean))); il.Add(Instruction.Create(OpCodes.Brtrue, lf));
            il.Add(Instruction.Create(OpCodes.Ldsfld, F(mod, t, "FrameFixed", ts.Boolean))); il.Add(Instruction.Create(OpCodes.Brfalse, ln));
            il.Add(lf); il.Add(Instruction.Create(OpCodes.Conv_R4)); il.Add(Instruction.Create(OpCodes.Ret));
            il.Add(ln); il.Add(Instruction.Create(OpCodes.Conv_R4)); il.Add(Instruction.Create(OpCodes.Ret)); },
        ["UnityEngine.Time::get_timeScale"] = (mod, t, il) => Load(mod, t, il, "TimeScale", mod.TypeSystem.Single),
        ["UnityEngine.Time::get_frameCount"] = (mod, t, il) => Load(mod, t, il, "FrameCount", mod.TypeSystem.Int32),
        ["UnityEngine.Time::get_fixedDeltaTime"] = (mod, t, il) => Load(mod, t, il, "FixedDeltaTime", mod.TypeSystem.Single),
        ["UnityEngine.Time::get_inFixedTimeStep"] = (mod, t, il) => Load(mod, t, il, "InFixedStep", mod.TypeSystem.Boolean),
        // constants the hooks return (the engine sets the field next to Mirror.On)
        ["UnityEngine.Mesh::get_canAccess"] = (mod, t, il) => Load(mod, t, il, "MeshCanAccess", mod.TypeSystem.Boolean),
        ["UnityEngine.Material::HasProperty"] = (mod, t, il) => Load(mod, t, il, "MaterialHasProperty", mod.TypeSystem.Boolean),
        ["UnityEngine.Input::get_mousePresent"] = (mod, t, il) => Load(mod, t, il, "MousePresent", mod.TypeSystem.Boolean),
        ["UnityEngine.Application::get_runInBackground"] = (mod, t, il) => Load(mod, t, il, "RunInBackground", mod.TypeSystem.Boolean),
        ["UnityEngine.Input::get_touchCount"] = (mod, t, il) => Load(mod, t, il, "TouchCount", mod.TypeSystem.Int32),
        // no-ops (void hooks that do nothing)
        ["UnityEngine.UISystemProfilerApi::BeginSample"] = (mod, t, il) => il.Add(Instruction.Create(OpCodes.Ret)),
        ["UnityEngine.UISystemProfilerApi::EndSample"] = (mod, t, il) => il.Add(Instruction.Create(OpCodes.Ret)),
    };
    static void Load(ModuleDefinition mod, TypeReference t, List<Instruction> il, string field, TypeReference ft)
    {
        il.Add(Instruction.Create(OpCodes.Ldsfld, F(mod, t, field, ft))); il.Add(Instruction.Create(OpCodes.Ret));
    }
    /// <summary>The mirrored keys (the engine checks each one's binding before it sets Mirror.On).</summary>
    public static IEnumerable<string> MirrorKeys => Mirrors.Keys;
}
