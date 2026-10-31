// FloatPc -- kit #9 fidelity track (vita/notes/kit9-fidelity.md, roadmap kit #9 owner decision (2) "number precision: float maths like the
// PC's Mono ... built behind a switch, proven against the player column with vita/tests/il, its device cost measured, then the owner decides
// the default"). An IL rewrite, so the runtime is untouched (no interpreter opcode, no codegen flag: every tier -- interpreter, AOT, LLVM AOT,
// the JIT -- runs the rewritten IL the same way):
//   The PC's Mono (Unity's player, Mono with float32 off) keeps float intermediates in double precision until a float32 store / argument /
//   return; Shepherd's Mono 8 (and CoreCLR) round every float op to float32. A single IEEE operation on float32 inputs rounded once to float32
//   equals the double operation rounded to float32 (the innocuous double rounding of +, -, *, /), so only CHAINS differ: an R4 arithmetic op
//   whose result feeds another arithmetic op, a comparison, a float->int conversion, conv.r8 or a float64 parameter, or whose operand is such
//   a chain's double. Those ops ("double ops") are rewritten: every float32 operand gets an explicit conv.r8 right after the instruction
//   that produced it (the op then runs in double precision on every tier: R8 op R8 = R8), the comparisons / conversions they feed get
//   both operands widened the same way, and a double op whose result goes to a float32 store / argument / return / box gets an explicit
//   conv.r4 right after it (the PC's rounding at the store). Everything else is left alone. Values that cross a branch / merge point (a
//   ternary inside an expression) are left float32 (counted: "crossing"), as are dup'ed results (counted).
//   FloatPc <in.dll> <out.dll> [--report <file>] [--x64-f2i] [--no-double]   (the MVID changes: an AOT image is bound to it; see Rewrite.Finish)
//   FloatPc --selftest [<dir>]                                            (kit #9 review: calli modelling, the float rule end to end on CoreCLR,
//                                                                          the MVID rule; exit 0 = all pass)
// Output DLLs from game assemblies are game-derived: owner-local, never in git.
using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;

if (args.Length >= 1 && args[0] == "--selftest") return SelfTest.Run(args.Length > 1 ? args[1] : null);   // [<dir>]: also writes the test module there
if (args.Length >= 3 && args[0] == "--dump")
{   // FloatPc --dump <dll> <method-name-substring>: the IL of the matching methods (a check of the rewrite)
    var a0 = AssemblyDefinition.ReadAssembly(args[1]);
    foreach (var t in a0.MainModule.GetTypes()) foreach (var m in t.Methods) if (m.HasBody && m.FullName.Contains(args[2]))
    { Console.WriteLine(m.FullName); foreach (var x in m.Body.Instructions) Console.WriteLine("   " + x); }
    return 0;
}
if (args.Length < 2) { Console.Error.WriteLine("usage: FloatPc <in.dll> <out.dll> [--report <file>] [--x64-f2i] [--no-double]  |  FloatPc --dump <dll> <method>"); return 2; }
string inPath = args[0], outPath = args[1]; string report = null;
for (int i = 2; i + 1 < args.Length; i++) if (args[i] == "--report") report = args[i + 1];
Rewrite.X64F2i = args.Contains("--x64-f2i"); Rewrite.Double = !args.Contains("--no-double");
var resolver = new DefaultAssemblyResolver(); resolver.AddSearchDirectory(Path.GetDirectoryName(Path.GetFullPath(inPath)));
var asm = AssemblyDefinition.ReadAssembly(inPath, new ReaderParameters { AssemblyResolver = resolver, ReadSymbols = false, ReadingMode = ReadingMode.Immediate });
var tot = new Rewrite.Totals();
Rewrite.All(asm, tot);
var old = asm.MainModule.Mvid;
File.WriteAllBytes(outPath, Rewrite.Finish(asm));
var line = $"FloatPc {Path.GetFileName(inPath)}: {tot.Methods:N0} methods rewritten of {tot.MethodsWithR4:N0} with R4 arithmetic; double ops {tot.DoubleOps:N0} of {tot.R4Ops:N0} R4 ops; conv.r8 inserted {tot.Widen:N0}, conv.r4 inserted {tot.Narrow:N0}; double ops across a merge {tot.Crossing:N0}; left float32: dup {tot.Dup:N0}, methods not modelled {tot.Unsafe:N0}{(Rewrite.X64F2i ? $"; x64 float->int helpers at {tot.F2i:N0} conversions (left as they are: native-int {tot.F2iNative:N0}, checked 8-32-bit {tot.F2iOvf:N0} -- the same on both players; in methods not modelled {tot.F2iUnmodelled:N0})" : "")}; calli sites {tot.Calli:N0}; MVID {old} -> {asm.MainModule.Mvid} ({Rewrite.Salt})";
Console.WriteLine(line);
if (report != null) File.AppendAllText(report, line + "\n");
return 0;

enum S : byte { I4, I8, R4, R8, Ref, Other }
static class Rewrite
{
    public sealed class Totals { public long Methods, MethodsWithR4, R4Ops, DoubleOps, Widen, Narrow, Crossing, Dup, F2i, F2iNative, F2iOvf, Unsafe, Calli, F2iUnmodelled; }
    public static bool X64F2i, Double = true;
    /// <summary>The tool revision: bump it when the rewrite rules change (it is part of the MVID salt, with the flags).</summary>
    public const string Version = "floatpc-2";
    public static string Salt => $"{Version}; double {(Double ? "on" : "off")}; x64-f2i {(X64F2i ? "on" : "off")}";
    /// <summary>The written module, with a new MVID derived from what was written. kit #9 review fix: the MVID was SHA256(old MVID +
    /// "shepherd-floatpc-1"), the same for every rewrite of one DLL whatever the flags or the tool revision -- a double-only set, an --x64-f2i
    /// set and a later FloatPc's set shared one MVID, so an AOT image built against one (the engine image for rewritten Unity modules, 00095's
    /// owner AOT) was accepted against another and ran its token-bound code on renumbered metadata. Now: SHA256 of the module as rewritten
    /// (written once with the input's MVID still in it, so two inputs never meet) + the tool revision + the flags (<see cref="Salt"/>). Every
    /// staleness gate checks MVIDs only (Mono's AOT image table, build.sh's / the hello's GUID gate, gameaot/check_owner_guids.py), so a different
    /// rewrite is now a different MVID and Mono stops at a stale image (aot-only mode: a fatal error naming the module, as for the original DLLs) instead
    /// of running its token-bound code. Deterministic: the same input + flags + revision give
    /// the same bytes. The second write must differ from the first in the MVID only (checked: anything else throws).
    /// Why the MVID must change at all: an AOT image that references this assembly (the engine's image references the UnityEngine.* modules,
    /// UnityEngine.UI and Unity.Jobs; the Derived module UnityEngine.CoreModule) encodes its members by metadata token, and Cecil's rewrite
    /// renumbers them -- keeping the MVID makes Mono trust a stale image (kit #9: a vlx test with kept MVIDs crashed in the interpreter at frame 1).
    /// Such an assembly is rewritten only together with a rebuild of the images that reference it; the game's own DLLs (referenced by name only)
    /// can be swapped on the card for the interpreted apps (00097 / 00098 / 00099 / 00011), or put beside them (host.c SHEPHERD_K9_FLOATPC, which
    /// also leaves 00095's owner modules unregistered).</summary>
    /// <summary>Every method of the module (the --x64-f2i helpers first).</summary>
    public static void All(AssemblyDefinition asm, Totals tot)
    {
        HelperType = null;
        if (X64F2i) AddHelpers(asm.MainModule);
        foreach (var t in asm.MainModule.GetTypes().ToList()) { if (t == HelperType) continue; foreach (var m in t.Methods) if (m.HasBody) Method(m, tot); }
    }
    public static byte[] Finish(AssemblyDefinition asm)
    {
        var mod = asm.MainModule; var old = mod.Mvid;
        var ms1 = new MemoryStream(); asm.Write(ms1); var first = ms1.ToArray();
        var hb = System.Security.Cryptography.SHA256.HashData(first.Concat(System.Text.Encoding.ASCII.GetBytes("\nshepherd " + Salt)).ToArray());
        mod.Mvid = new Guid(hb.AsSpan(0, 16));
        var ms2 = new MemoryStream(); asm.Write(ms2); var final = ms2.ToArray();
        // the check: the final bytes with the old MVID put back are the first write's, byte for byte
        var o = old.ToByteArray(); var nw = mod.Mvid.ToByteArray(); int at = -1, hits = 0;
        if (final.Length == first.Length)
            for (int i = 0; i + 16 <= final.Length; i++) if (final[i] == nw[0] && final.AsSpan(i, 16).SequenceEqual(nw)) { at = i; hits++; }
        if (hits != 1) throw new InvalidOperationException($"FloatPc: the second write is not the first with a new MVID (length {first.Length} -> {final.Length}, MVID found {hits} times)");
        var back = (byte[])final.Clone(); o.CopyTo(back, at);
        if (!back.AsSpan().SequenceEqual(first)) throw new InvalidOperationException("FloatPc: the second write differs from the first beyond the MVID");
        return final;
    }
    public static TypeDefinition HelperType; static readonly Dictionary<Code, MethodDefinition> Helper = new();
    /// <summary>--x64-f2i: the PC's float -> integer rules (Unity's player Mono on x64, measured with vita/tests/il/F2iMode.cs on the oracle's
    /// x64 player: 992 / 992 probes): cvt32(v) = NaN or v outside [-2^31, 2^31) ? int.MinValue : trunc(v) (SSE cvttsd2si); cvt64 the same at
    /// 2^63; conv.i1 / i2 / u1 / u2 / i4 = narrowing of cvt32, conv.u4 = the low 32 bits of cvt64, conv.i8 = cvt64, conv.u8 = Mono's
    /// mono_fconv_u8 (v &lt; 2^63 ? cvt64(v) : cvt64(v - 2^63) + 2^63). ARM tiers saturate / wrap differently (129 of the 992 probes differ
    /// between the x64 and arm64 players). The helpers are injected into the rewritten module (no new assembly).</summary>
    public static void AddHelpers(ModuleDefinition mod)
    {
        var ts = mod.TypeSystem;
        HelperType = new TypeDefinition("", "<ShepherdF2i>", TypeAttributes.NotPublic | TypeAttributes.Abstract | TypeAttributes.Sealed | TypeAttributes.BeforeFieldInit, ts.Object);
        mod.Types.Add(HelperType);
        MethodDefinition New(string name, TypeReference ret) { var md = new MethodDefinition(name, MethodAttributes.Assembly | MethodAttributes.Static | MethodAttributes.HideBySig, ret); md.Parameters.Add(new ParameterDefinition("v", ParameterAttributes.None, ts.Double)); HelperType.Methods.Add(md); return md; }
        MethodDefinition Cvt(string name, TypeReference ret, double lo, double hi, bool is64)
        {
            var md = New(name, ret); var il = md.Body.GetILProcessor(); var bad = is64 ? il.Create(OpCodes.Ldc_I8, long.MinValue) : il.Create(OpCodes.Ldc_I4, int.MinValue);
            il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldc_R8, hi); il.Emit(OpCodes.Bge_Un, bad);   // v >= hi or NaN
            il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldc_R8, lo); il.Emit(OpCodes.Blt_Un, bad);    // v < lo or NaN
            il.Emit(OpCodes.Ldarg_0); il.Emit(is64 ? OpCodes.Conv_I8 : OpCodes.Conv_I4); il.Emit(OpCodes.Ret);
            il.Append(bad); il.Emit(OpCodes.Ret);
            return md;
        }
        var c32 = Cvt("Cvt32", ts.Int32, -2147483648.0, 2147483648.0, false);
        var c64 = Cvt("Cvt64", ts.Int64, -9223372036854775808.0, 9223372036854775808.0, true);
        MethodDefinition Wrap(string name, TypeReference ret, MethodDefinition inner, OpCode? narrow)
        {
            var md = New(name, ret); var il = md.Body.GetILProcessor();
            il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Call, inner); if (narrow is { } n) il.Emit(n); il.Emit(OpCodes.Ret); return md;
        }
        Helper[Code.Conv_I4] = Wrap("I4", ts.Int32, c32, null);
        Helper[Code.Conv_I1] = Wrap("I1", ts.Int32, c32, OpCodes.Conv_I1);
        Helper[Code.Conv_I2] = Wrap("I2", ts.Int32, c32, OpCodes.Conv_I2);
        Helper[Code.Conv_U1] = Wrap("U1", ts.Int32, c32, OpCodes.Conv_U1);
        Helper[Code.Conv_U2] = Wrap("U2", ts.Int32, c32, OpCodes.Conv_U2);
        Helper[Code.Conv_U4] = Wrap("U4", ts.Int32, c64, OpCodes.Conv_U4);
        Helper[Code.Conv_I8] = Wrap("I8", ts.Int64, c64, null);
        {
            var md = New("U8", ts.Int64); var il = md.Body.GetILProcessor(); var high = il.Create(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldc_R8, 9223372036854775808.0); il.Emit(OpCodes.Clt); il.Emit(OpCodes.Brfalse, high);
            il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Call, c64); il.Emit(OpCodes.Ret);
            il.Append(high); il.Emit(OpCodes.Ldc_R8, 9223372036854775808.0); il.Emit(OpCodes.Sub); il.Emit(OpCodes.Call, c64); il.Emit(OpCodes.Ldc_I8, long.MinValue); il.Emit(OpCodes.Add); il.Emit(OpCodes.Ret);
            Helper[Code.Conv_U8] = md;
        }
        // the checked 64-bit forms: x64 throws at exactly 2^63 (conv.ovf.i8) / 2^64 (conv.ovf.u8); ARM Mono's range checks let those through
        var ovfCtor = new MethodReference(".ctor", ts.Void, new TypeReference("System", "OverflowException", mod, ts.CoreLibrary)) { HasThis = true };
        MethodDefinition Ovf(string name, TypeReference ret, double lo, bool loInclusive, double hi, OpCode conv)
        {
            var md = New(name, ret); var il = md.Body.GetILProcessor(); var thr = il.Create(OpCodes.Newobj, ovfCtor);
            il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldc_R8, lo); il.Emit(loInclusive ? OpCodes.Blt_Un : OpCodes.Ble_Un, thr);   // below the range or NaN
            il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldc_R8, hi); il.Emit(OpCodes.Bge_Un, thr);                                   // at / above the top or NaN
            il.Emit(OpCodes.Ldarg_0); il.Emit(conv); il.Emit(OpCodes.Ret);
            il.Append(thr); il.Emit(OpCodes.Throw);
            return md;
        }
        Helper[Code.Conv_Ovf_I8] = Ovf("OvfI8", ts.Int64, -9223372036854775808.0, true, 9223372036854775808.0, OpCodes.Conv_I8);
        Helper[Code.Conv_Ovf_U8] = Ovf("OvfU8", ts.Int64, -1.0, false, 18446744073709551616.0, OpCodes.Conv_U8);
    }
    struct V { public S T; public int Prod; public bool Crossed; public bool Duped; public List<int> Alt; }   // Alt: the other producers of a merged slot
    enum K : byte { None, Arith, Cmp, F2I, R8, F64Arg, R4Sink, Other }
    static S Of(TypeReference t)
    {
        if (t == null) return S.Other;
        switch (t.MetadataType)
        {
            case MetadataType.Single: return S.R4; case MetadataType.Double: return S.R8;
            case MetadataType.Boolean: case MetadataType.Char: case MetadataType.SByte: case MetadataType.Byte: case MetadataType.Int16: case MetadataType.UInt16: case MetadataType.Int32: case MetadataType.UInt32: case MetadataType.IntPtr: case MetadataType.UIntPtr: return S.I4;
            case MetadataType.Int64: case MetadataType.UInt64: return S.I8;
            case MetadataType.Void: return S.Other;
            default: return t.IsValueType ? S.Other : S.Ref;
        }
    }
    static bool IsArith(Code c) => c is Code.Add or Code.Sub or Code.Mul or Code.Div or Code.Rem;

    public static void Method(MethodDefinition m, Totals tot)
    {
        var body = m.Body; body.SimplifyMacros();
        var ins = body.Instructions; int n = ins.Count; var orig = ins.ToArray();
        var idx = new Dictionary<Instruction, int>(); for (int i = 0; i < n; i++) idx[ins[i]] = i;
        var targets = new HashSet<int>();
        foreach (var x in ins) { if (x.Operand is Instruction ti) targets.Add(idx[ti]); else if (x.Operand is Instruction[] tis) foreach (var tt in tis) targets.Add(idx[tt]); }
        foreach (var h in body.ExceptionHandlers) { if (h.HandlerStart != null) targets.Add(idx[h.HandlerStart]); if (h.FilterStart != null) targets.Add(idx[h.FilterStart]); }
        // ---- the linear stack pass: each instruction's popped values (producer, type, crossed) and its own result type ----
        var inputs = new V[n][]; var outT = new S[n]; var isR4Op = new bool[n]; var f2i = new List<int>();
        var consumerKind = new K[n]; var consumerAt = new int[n]; for (int i = 0; i < n; i++) consumerAt[i] = -1;
        var saved = new Dictionary<int, List<V>>(); var st = new List<V>(); bool unsafeMethod = false; var backSeen = new HashSet<int>(); var dupedProd = new bool[n];
        void Save(int target, List<V> cur)
        {
            if (backSeen.Contains(target) || target <= 0) { if (cur.Count > 0) unsafeMethod = true; return; }   // a backward branch with a stack: not modelled
            if (!saved.TryGetValue(target, out var have)) { saved[target] = cur.Select(v => { v.Alt = v.Alt == null ? null : new List<int>(v.Alt); return v; }).ToList(); return; }
            if (have.Count != cur.Count) { unsafeMethod = true; return; }
            for (int k = 0; k < have.Count; k++) { var v = have[k]; v.Alt ??= new List<int>(); v.Alt.Add(cur[k].Prod); if (cur[k].Alt != null) v.Alt.AddRange(cur[k].Alt); v.Duped |= cur[k].Duped; have[k] = v; }
        }
        S LocT(int k) => Of(body.Variables[k].VariableType);
        S ArgT(ParameterDefinition p) => p == null ? S.Ref : Of(p.ParameterType);
        V Pop() { if (st.Count == 0) return new V { T = S.Other, Prod = -1 }; var v = st[^1]; st.RemoveAt(st.Count - 1); return v; }
        void Push(S t, int i) => st.Add(new V { T = t, Prod = i });
        void Use1(int p, int at, K k) { if (p < 0) return; if (consumerAt[p] < 0) { consumerAt[p] = at; consumerKind[p] = k; } else if (consumerAt[p] != at) consumerKind[p] = K.Other; }
        void Use(V v, int at, K k) { Use1(v.Prod, at, k); if (v.Alt != null) foreach (var p in v.Alt) Use1(p, at, k); }
        for (int i = 0; i < n; i++)
        {
            var x = ins[i]; var c = x.OpCode.Code;
            if (targets.Contains(i))
            {
                bool fallsIn = i > 0 && !(ins[i - 1].OpCode.FlowControl is FlowControl.Branch or FlowControl.Return or FlowControl.Throw);
                if (saved.TryGetValue(i, out var sv))
                {
                    if (!fallsIn) st = sv.Select(v => { v.Alt = v.Alt == null ? null : new List<int>(v.Alt); return v; }).ToList();
                    else if (sv.Count != st.Count) unsafeMethod = true;
                    else for (int k = 0; k < st.Count; k++) { var v = st[k]; v.Alt = v.Alt == null ? new List<int>() : new List<int>(v.Alt); v.Alt.Add(sv[k].Prod); if (sv[k].Alt != null) v.Alt.AddRange(sv[k].Alt); v.Duped |= sv[k].Duped; st[k] = v; }
                }
                else if (!fallsIn) st = new List<V>();
                for (int k = 0; k < st.Count; k++) { var v = st[k]; v.Crossed = true; st[k] = v; }
                backSeen.Add(i);
            }
            V[] pops; S push = S.Other; bool hasPush = false; K kind = K.Other;
            switch (c)
            {
                case Code.Ldc_R4: pops = new V[0]; push = S.R4; hasPush = true; break;
                case Code.Ldc_R8: pops = new V[0]; push = S.R8; hasPush = true; break;
                case Code.Ldc_I4: pops = new V[0]; push = S.I4; hasPush = true; break;
                case Code.Ldc_I8: pops = new V[0]; push = S.I8; hasPush = true; break;
                case Code.Ldloc: pops = new V[0]; push = LocT(((VariableDefinition)x.Operand).Index); hasPush = true; break;
                case Code.Ldarg: pops = new V[0]; push = ArgT((ParameterDefinition)x.Operand); hasPush = true; break;
                case Code.Stloc: { var t = LocT(((VariableDefinition)x.Operand).Index); pops = new[] { Pop() }; kind = t == S.R4 ? K.R4Sink : t == S.R8 ? K.R8 : K.Other; break; }
                case Code.Starg: { var t = ArgT((ParameterDefinition)x.Operand); pops = new[] { Pop() }; kind = t == S.R4 ? K.R4Sink : t == S.R8 ? K.R8 : K.Other; break; }
                case Code.Ldfld: pops = new[] { Pop() }; push = Of(((FieldReference)x.Operand).FieldType); hasPush = true; break;
                case Code.Ldsfld: pops = new V[0]; push = Of(((FieldReference)x.Operand).FieldType); hasPush = true; break;
                case Code.Stfld: { var t = Of(((FieldReference)x.Operand).FieldType); var v = Pop(); var o = Pop(); pops = new[] { o, v }; kind = t == S.R4 ? K.R4Sink : t == S.R8 ? K.R8 : K.Other; break; }
                case Code.Stsfld: { var t = Of(((FieldReference)x.Operand).FieldType); pops = new[] { Pop() }; kind = t == S.R4 ? K.R4Sink : t == S.R8 ? K.R8 : K.Other; break; }
                case Code.Ldelem_R4: pops = Pops(2); push = S.R4; hasPush = true; break;
                case Code.Ldelem_R8: pops = Pops(2); push = S.R8; hasPush = true; break;
                case Code.Ldelem_Any: pops = Pops(2); push = Of((TypeReference)x.Operand); hasPush = true; break;
                case Code.Stelem_R4: pops = Pops(3); kind = K.R4Sink; break;
                case Code.Stelem_R8: pops = Pops(3); kind = K.R8; break;
                case Code.Stelem_Any: { var t = Of((TypeReference)x.Operand); pops = Pops(3); kind = t == S.R4 ? K.R4Sink : t == S.R8 ? K.R8 : K.Other; break; }
                case Code.Ldind_R4: pops = Pops(1); push = S.R4; hasPush = true; break;
                case Code.Ldind_R8: pops = Pops(1); push = S.R8; hasPush = true; break;
                case Code.Stind_R4: pops = Pops(2); kind = K.R4Sink; break;
                case Code.Stind_R8: pops = Pops(2); kind = K.R8; break;
                case Code.Ldobj: pops = Pops(1); push = Of((TypeReference)x.Operand); hasPush = true; break;
                case Code.Stobj: { var t = Of((TypeReference)x.Operand); pops = Pops(2); kind = t == S.R4 ? K.R4Sink : t == S.R8 ? K.R8 : K.Other; break; }
                case Code.Unbox_Any: pops = Pops(1); push = Of((TypeReference)x.Operand); hasPush = true; break;
                case Code.Dup:
                {
                    var v = Pop(); v.Duped = true; st.Add(v); st.Add(v); inputs[i] = new V[0]; outT[i] = v.T;
                    if (v.Prod >= 0) { consumerKind[v.Prod] = K.Other; dupedProd[v.Prod] = true; } if (v.Alt != null) foreach (var p2 in v.Alt) if (p2 >= 0) { consumerKind[p2] = K.Other; dupedProd[p2] = true; }   // (a dup'ed value is never rewritten)
                    continue;
                }
                case Code.Add: case Code.Sub: case Code.Mul: case Code.Div: case Code.Rem:
                {
                    var b = Pop(); var a = Pop(); pops = new[] { a, b };
                    bool fl = a.T is S.R4 or S.R8 || b.T is S.R4 or S.R8;
                    bool r4 = fl && a.T != S.R8 && b.T != S.R8;
                    push = fl ? (r4 ? S.R4 : S.R8) : (a.T == S.I8 || b.T == S.I8 ? S.I8 : S.I4); hasPush = true; kind = fl ? K.Arith : K.Other; isR4Op[i] = r4;
                    break;
                }
                case Code.Neg: { var a = Pop(); pops = new[] { a }; push = a.T; hasPush = true; kind = a.T is S.R4 or S.R8 ? K.Arith : K.Other; isR4Op[i] = a.T == S.R4; break; }
                case Code.Ceq: case Code.Cgt: case Code.Cgt_Un: case Code.Clt: case Code.Clt_Un: pops = Pops(2); push = S.I4; hasPush = true; kind = K.Cmp; break;
                case Code.Beq: case Code.Bne_Un: case Code.Bge: case Code.Bge_Un: case Code.Bgt: case Code.Bgt_Un: case Code.Ble: case Code.Ble_Un: case Code.Blt: case Code.Blt_Un:
                    pops = Pops(2); kind = K.Cmp; Save(idx[(Instruction)x.Operand], st); break;
                case Code.Brtrue: case Code.Brfalse: pops = Pops(1); Save(idx[(Instruction)x.Operand], st); break;
                case Code.Br: case Code.Leave: pops = new V[0]; Save(idx[(Instruction)x.Operand], st); if (c == Code.Leave) st.Clear(); break;
                case Code.Switch: pops = Pops(1); foreach (var tt in (Instruction[])x.Operand) Save(idx[tt], st); break;
                case Code.Conv_R4: pops = Pops(1); push = S.R4; hasPush = true; kind = K.R4Sink; break;   // (explicit narrowing: the PC rounds here too)
                case Code.Conv_R8: case Code.Conv_R_Un: pops = Pops(1); push = S.R8; hasPush = true; kind = K.R8; break;
                case Code.Ckfinite: { var a = Pop(); pops = new[] { a }; push = a.T; hasPush = true; kind = K.Cmp; break; }
                case Code.Box: { var t = Of((TypeReference)x.Operand); pops = Pops(1); push = S.Ref; hasPush = true; kind = t == S.R4 ? K.R4Sink : t == S.R8 ? K.R8 : K.Other; break; }
                case Code.Ret: { pops = m.ReturnType.MetadataType != MetadataType.Void ? Pops(1) : new V[0]; var t = Of(m.ReturnType); kind = t == S.R4 ? K.R4Sink : t == S.R8 ? K.R8 : K.Other; st.Clear(); break; }
                case Code.Call: case Code.Callvirt: case Code.Newobj:
                {
                    var mr = (MethodReference)x.Operand; var ps = mr.Parameters; var vals = new V[ps.Count];
                    for (int k = ps.Count - 1; k >= 0; k--) vals[k] = Pop();
                    V self = default; bool hasSelf = mr.HasThis && c != Code.Newobj; if (hasSelf) self = Pop();
                    inputs[i] = hasSelf ? new[] { self }.Concat(vals).ToArray() : vals;
                    for (int k = 0; k < ps.Count; k++) { var t = Of(ps[k].ParameterType); Use(vals[k], i, t == S.R4 ? K.R4Sink : t == S.R8 ? K.F64Arg : K.Other); }
                    if (hasSelf) Use(self, i, K.Other);
                    if (c == Code.Newobj) { Push(S.Ref, i); outT[i] = S.Ref; } else if (mr.ReturnType.MetadataType != MetadataType.Void) { var rt = Of(mr.ReturnType); Push(rt, i); outT[i] = rt; }
                    continue;
                }
                case Code.Calli:
                {   // kit #9 review fix: calli pops its signature's arguments (+ 'this' for an instance signature without an explicit this) and the
                    // function pointer on top, and pushes its return type -- before, it fell into the generic path (Varpop -> 0 pops, Varpush -> one
                    // int32), the arguments and the pointer stayed on the modelled stack, and every later pop in the method paired the wrong
                    // producers with no "not modelled" count
                    var cs = (CallSite)x.Operand; var ps = cs.Parameters; var fp = Pop(); var vals = new V[ps.Count];
                    for (int k = ps.Count - 1; k >= 0; k--) vals[k] = Pop();
                    V self = default; bool hasSelf = cs.HasThis && !cs.ExplicitThis; if (hasSelf) self = Pop();
                    inputs[i] = (hasSelf ? new[] { self } : new V[0]).Concat(vals).Append(fp).ToArray();
                    for (int k = 0; k < ps.Count; k++) { var t = Of(ps[k].ParameterType); Use(vals[k], i, t == S.R4 ? K.R4Sink : t == S.R8 ? K.F64Arg : K.Other); }
                    if (hasSelf) Use(self, i, K.Other); Use(fp, i, K.Other);
                    if (cs.ReturnType.MetadataType != MetadataType.Void) { var rt = Of(cs.ReturnType); Push(rt, i); outT[i] = rt; }
                    tot.Calli++;
                    continue;
                }
                default:
                {
                    var op = x.OpCode;
                    if (c is Code.Conv_I1 or Code.Conv_I2 or Code.Conv_I4 or Code.Conv_U1 or Code.Conv_U2 or Code.Conv_U4 or Code.Conv_I8 or Code.Conv_U8 or Code.Conv_I or Code.Conv_U
                        or Code.Conv_Ovf_I1 or Code.Conv_Ovf_I2 or Code.Conv_Ovf_I4 or Code.Conv_Ovf_U1 or Code.Conv_Ovf_U2 or Code.Conv_Ovf_U4 or Code.Conv_Ovf_I8 or Code.Conv_Ovf_U8 or Code.Conv_Ovf_I or Code.Conv_Ovf_U
                        or Code.Conv_Ovf_I1_Un or Code.Conv_Ovf_I2_Un or Code.Conv_Ovf_I4_Un or Code.Conv_Ovf_U1_Un or Code.Conv_Ovf_U2_Un or Code.Conv_Ovf_U4_Un or Code.Conv_Ovf_I8_Un or Code.Conv_Ovf_U8_Un or Code.Conv_Ovf_I_Un or Code.Conv_Ovf_U_Un)
                    {
                        pops = Pops(1); push = c is Code.Conv_I8 or Code.Conv_U8 or Code.Conv_Ovf_I8 or Code.Conv_Ovf_U8 or Code.Conv_Ovf_I8_Un or Code.Conv_Ovf_U8_Un ? S.I8 : S.I4; hasPush = true; kind = K.F2I;
                        if (pops[0].T is S.R4 or S.R8) { if (c is Code.Conv_I or Code.Conv_U) tot.F2iNative++; else if (c is Code.Conv_Ovf_I8 or Code.Conv_Ovf_U8) f2i.Add(i); else if (c.ToString().StartsWith("Conv_Ovf")) tot.F2iOvf++; else f2i.Add(i); }
                        break;
                    }
                    int np = op.StackBehaviourPop switch { StackBehaviour.Pop0 => 0, StackBehaviour.Pop1 or StackBehaviour.Popi or StackBehaviour.Popref => 1, StackBehaviour.Pop1_pop1 or StackBehaviour.Popi_pop1 or StackBehaviour.Popi_popi or StackBehaviour.Popi_popi8 or StackBehaviour.Popi_popr4 or StackBehaviour.Popi_popr8 or StackBehaviour.Popref_pop1 or StackBehaviour.Popref_popi => 2, StackBehaviour.Popi_popi_popi or StackBehaviour.Popref_popi_popi or StackBehaviour.Popref_popi_popi8 or StackBehaviour.Popref_popi_popr4 or StackBehaviour.Popref_popi_popr8 or StackBehaviour.Popref_popi_popref => 3, StackBehaviour.PopAll => st.Count, _ => -1 };
                    if (np < 0) { unsafeMethod = true; np = 0; }   // (kit #9 review: a pop behaviour this pass does not model -- the method is left as it is, counted)
                    pops = Pops(np);
                    int npush = op.StackBehaviourPush switch { StackBehaviour.Push0 => 0, StackBehaviour.Push1_push1 => 2, _ => 1 };
                    S pt = op.StackBehaviourPush switch { StackBehaviour.Pushr4 => S.R4, StackBehaviour.Pushr8 => S.R8, StackBehaviour.Pushi8 => S.I8, StackBehaviour.Pushref => S.Ref, _ => S.I4 };
                    for (int k = 0; k < npush; k++) Push(pt, i);
                    inputs[i] = pops; outT[i] = pt; foreach (var v in pops) Use(v, i, K.Other);
                    if (op.FlowControl is FlowControl.Throw) st.Clear();
                    continue;
                }
            }
            inputs[i] = pops; foreach (var v in pops) Use(v, i, kind);
            if (hasPush) { Push(push, i); outT[i] = push; }
        }
        V[] Pops(int k) { var r = new V[k]; for (int j = k - 1; j >= 0; j--) r[j] = Pop(); return r; }

        // ---- the double ops (fixpoint) ----
        // ---- --x64-f2i: each float -> integer conversion becomes conv.r8 + a call of the PC's rule (the conv instruction keeps its place: branch
        //      targets stay valid) ----
        if (X64F2i && f2i.Count > 0 && unsafeMethod) tot.F2iUnmodelled += f2i.Count;   // (kit #9 review: no helper where the stack model is not trusted -- an int
                                                                                        //  conversion mistaken for a float one would go through double)
        else if (X64F2i && f2i.Count > 0)
        {
            var il0 = body.GetILProcessor();
            foreach (var i in f2i) { var x = orig[i]; var h = Helper[x.OpCode.Code]; x.OpCode = OpCodes.Conv_R8; x.Operand = null; il0.InsertAfter(x, il0.Create(OpCodes.Call, h)); tot.F2i++; }
        }
        int r4ops = isR4Op.Count(b => b); if (r4ops == 0 || !Double) { body.OptimizeMacros(); return; }
        tot.MethodsWithR4++; tot.R4Ops += r4ops;
        if (unsafeMethod) { tot.Unsafe++; body.OptimizeMacros(); return; }   // (a stack across a backward branch, or merges of unequal depth: left as it is)
        IEnumerable<int> P(V v) { if (v.Prod >= 0) yield return v.Prod; if (v.Alt != null) foreach (var p in v.Alt) if (p >= 0) yield return p; }
        bool Ok(int i) => isR4Op[i] && !dupedProd[i] && !inputs[i].Any(v => v.Duped);
        var D = new bool[n];
        for (int i = 0; i < n; i++)
            if (isR4Op[i] && consumerKind[i] is K.Arith or K.Cmp or K.F2I or K.R8 or K.F64Arg)
            { if (Ok(i)) { D[i] = true; if (inputs[i].Any(v => v.Crossed)) tot.Crossing++; } else tot.Dup++; }
        for (bool changed = true; changed;)
        {
            changed = false;
            for (int i = 0; i < n; i++)
                if (!D[i] && isR4Op[i] && inputs[i].Any(v => P(v).Any(p => D[p])))
                { if (Ok(i)) { D[i] = true; changed = true; } }
        }
        // ---- the edits: widen float32 operands of double ops and of the comparisons / conversions they feed; narrow at float32 sinks ----
        var widenAfter = new HashSet<int>(); var narrowAfter = new HashSet<int>();
        for (int i = 0; i < n; i++)
        {
            if (D[i]) { foreach (var v in inputs[i]) if (v.T is S.R4 or S.R8) foreach (var p in P(v)) if (!D[p] && outT[p] == S.R4) widenAfter.Add(p); if (consumerKind[i] == K.R4Sink && !(consumerAt[i] >= 0 && orig[consumerAt[i]].OpCode.Code == Code.Conv_R4)) narrowAfter.Add(i); continue; }
            // a comparison / branch / ckfinite fed by a double op: its other float32 operand is widened too (the PC compares two doubles)
            if (inputs[i] != null && inputs[i].Any(v => P(v).Any(p => D[p])) && !(isR4Op[i]))
                foreach (var v in inputs[i]) if (v.T is S.R4 or S.R8 && !v.Duped) foreach (var p in P(v)) if (!D[p] && outT[p] == S.R4) widenAfter.Add(p);
        }
        int dops = D.Count(b => b);
        if (dops > 0)
        {
            var il = body.GetILProcessor();
            foreach (var p in widenAfter) il.InsertAfter(orig[p], il.Create(OpCodes.Conv_R8));
            foreach (var p in narrowAfter) il.InsertAfter(orig[p], il.Create(OpCodes.Conv_R4));
            tot.Methods++; tot.DoubleOps += dops; tot.Widen += widenAfter.Count; tot.Narrow += narrowAfter.Count;
        }
        body.OptimizeMacros();
    }
}

/// <summary>FloatPc --selftest (kit #9 review): (1) a calli inside a float chain is modelled (its arguments and pointer popped) and the chain is
/// rewritten; run on CoreCLR (which rounds every float op to float32), the rewritten method returns the PC's double-intermediate answer on every
/// case and the original the float32 chain's; (2) the MVID rule: the same input + flags give the same bytes, different flags or inputs a different
/// MVID, never the input's.</summary>
static class SelfTest
{
    static int _fail;
    static void Check(bool ok, string what) { Console.WriteLine((ok ? "PASS  " : "FAIL  ") + what); if (!ok) _fail++; }
    /// <summary>T.CalliMix(a, b, c, d) = (a * b + Id(c)) * d with Id called through ldftn + calli; T.CalliArgs(a, b, c) = Id2(a * b, c) * c
    /// (a float32 argument: stored, so single-rounded either way, then a chain).</summary>
    static byte[] Module(string name)
    {
        var asm = AssemblyDefinition.CreateAssembly(new AssemblyNameDefinition(name, new Version(1, 0)), name, ModuleKind.Dll);
        var mod = asm.MainModule; var ts = mod.TypeSystem;
        var t = new TypeDefinition("", "T", TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed, ts.Object); mod.Types.Add(t);
        MethodDefinition M(string n, int np) { var md = new MethodDefinition(n, MethodAttributes.Public | MethodAttributes.Static, ts.Single); for (int k = 0; k < np; k++) md.Parameters.Add(new ParameterDefinition(ts.Single)); t.Methods.Add(md); return md; }
        var id = M("Id", 1); { var il = id.Body.GetILProcessor(); il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ret); }
        var id2 = M("Id2", 2); { var il = id2.Body.GetILProcessor(); il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ret); }
        var cs1 = new CallSite(ts.Single); cs1.Parameters.Add(new ParameterDefinition(ts.Single));
        var cs2 = new CallSite(ts.Single); cs2.Parameters.Add(new ParameterDefinition(ts.Single)); cs2.Parameters.Add(new ParameterDefinition(ts.Single));
        var mix = M("CalliMix", 4);
        {
            var il = mix.Body.GetILProcessor();
            il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldarg_1); il.Emit(OpCodes.Mul); il.Emit(OpCodes.Ldarg_2); il.Emit(OpCodes.Ldftn, id); il.Emit(OpCodes.Calli, cs1);
            il.Emit(OpCodes.Add); il.Emit(OpCodes.Ldarg_3); il.Emit(OpCodes.Mul); il.Emit(OpCodes.Ret);
        }
        var args2 = M("CalliArgs", 3);
        {
            var il = args2.Body.GetILProcessor();
            il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldarg_1); il.Emit(OpCodes.Mul); il.Emit(OpCodes.Ldarg_2); il.Emit(OpCodes.Ldftn, id2); il.Emit(OpCodes.Calli, cs2);
            il.Emit(OpCodes.Ldarg_2); il.Emit(OpCodes.Mul); il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Add); il.Emit(OpCodes.Ret);
        }
        var ms = new MemoryStream(); asm.Write(ms); return ms.ToArray();
    }
    static byte[] Rw(byte[] input, bool dbl, bool f2i, out Rewrite.Totals tot)
    {
        Rewrite.Double = dbl; Rewrite.X64F2i = f2i;
        var asm = AssemblyDefinition.ReadAssembly(new MemoryStream(input), new ReaderParameters { ReadingMode = ReadingMode.Immediate });
        tot = new Rewrite.Totals(); Rewrite.All(asm, tot); return Rewrite.Finish(asm);
    }
    static Guid Mvid(byte[] b) => ModuleDefinition.ReadModule(new MemoryStream(b)).Mvid;
    static System.Reflection.MethodInfo Load(byte[] b, string n) =>
        new System.Runtime.Loader.AssemblyLoadContext(null, true).LoadFromStream(new MemoryStream(b)).GetType("T").GetMethod(n);
    public static int Run(string dir)
    {
        var src = Module("FloatPcSelfTest");
        if (dir != null) { Directory.CreateDirectory(dir); File.WriteAllBytes(Path.Combine(dir, "FloatPcSelfTest.dll"), src); }
        var rw = Rw(src, true, false, out var tot);
        var mixIl = ModuleDefinition.ReadModule(new MemoryStream(rw)).GetType("T").Methods.First(m => m.Name == "CalliMix").Body.Instructions.Select(x => x.OpCode.Code).ToArray();
        Console.WriteLine("CalliMix rewritten: " + string.Join(" ", mixIl));
        Check(tot.Calli == 2 && tot.Unsafe == 0, $"calli modelled: {tot.Calli} calli sites, {tot.Unsafe} methods not modelled");
        var want = new[] { Code.Ldarg_0, Code.Conv_R8, Code.Ldarg_1, Code.Conv_R8, Code.Mul, Code.Ldarg_2, Code.Ldftn, Code.Calli, Code.Conv_R8, Code.Add, Code.Ldarg_3, Code.Conv_R8, Code.Mul, Code.Conv_R4, Code.Ret };
        Check(mixIl.SequenceEqual(want), "CalliMix: a, b, Id(c), d widened after their producers, the chain in double, conv.r4 at the return");
        // run both on CoreCLR: the original = the float32 chain, the rewrite = the PC's (double intermediates, one rounding at the return)
        var o1 = Load(src, "CalliMix"); var r1 = Load(rw, "CalliMix"); var o2 = Load(src, "CalliArgs"); var r2 = Load(rw, "CalliArgs");
        var rng = new Random(9); int n = 20000, okR = 0, okO = 0, distinct = 0, okR2 = 0, okO2 = 0, distinct2 = 0;
        float F() => (float)((rng.NextDouble() * 2 - 1) * Math.Pow(2, rng.Next(-8, 9)));
        for (int k = 0; k < n; k++)
        {
            float a = F(), b = F(), c = F(), d = F();
            float pc = (float)(((double)a * b + c) * d), f32 = (a * b + c) * d;
            float gotR = (float)r1.Invoke(null, new object[] { a, b, c, d }), gotO = (float)o1.Invoke(null, new object[] { a, b, c, d });
            if (BitConverter.SingleToInt32Bits(gotR) == BitConverter.SingleToInt32Bits(pc)) okR++;
            if (BitConverter.SingleToInt32Bits(gotO) == BitConverter.SingleToInt32Bits(f32)) okO++;
            if (pc != f32) distinct++;
            float ab = a * b;   // the float32 argument (a store: single rounding on the PC too)
            float pc2 = (float)((double)ab * c + a), f322 = ab * c + a;
            float gotR2 = (float)r2.Invoke(null, new object[] { a, b, c }), gotO2 = (float)o2.Invoke(null, new object[] { a, b, c });
            if (BitConverter.SingleToInt32Bits(gotR2) == BitConverter.SingleToInt32Bits(pc2)) okR2++;
            if (BitConverter.SingleToInt32Bits(gotO2) == BitConverter.SingleToInt32Bits(f322)) okO2++;
            if (pc2 != f322) distinct2++;
        }
        Check(okR == n && okO == n && distinct > 0, $"CalliMix on CoreCLR: rewrite = the PC's rule {okR} / {n}, original = float32 {okO} / {n} ({distinct} cases where the two rules differ)");
        Check(okR2 == n && okO2 == n && distinct2 > 0, $"CalliArgs on CoreCLR (float32 argument, then a chain): rewrite = the PC's rule {okR2} / {n}, original = float32 {okO2} / {n} ({distinct2} differ)");
        // the MVID rule
        var rwB = Rw(src, true, false, out _); var f2i = Rw(src, true, true, out _); var nod = Rw(src, false, true, out _);
        var src2 = Module("FloatPcSelfTest2"); var rwOther = Rw(src2, true, false, out _);
        Check(rw.AsSpan().SequenceEqual(rwB), "the same input + flags: the same bytes (deterministic)");
        Check(Mvid(rw) != Mvid(src), $"the MVID changes ({Mvid(src)} -> {Mvid(rw)})");
        Check(new[] { Mvid(rw), Mvid(f2i), Mvid(nod), Mvid(rwOther) }.Distinct().Count() == 4, $"different flags / inputs, different MVIDs: double {Mvid(rw)}, double + --x64-f2i {Mvid(f2i)}, --no-double --x64-f2i {Mvid(nod)}, another input {Mvid(rwOther)}");
        Console.WriteLine(_fail == 0 ? "selftest: all PASS" : $"selftest: {_fail} FAIL");
        return _fail == 0 ? 0 : 1;
    }
}
