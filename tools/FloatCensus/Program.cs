// FloatCensus -- kit #9 fidelity track (vita/notes/kit9-fidelity.md, roadmap kit #9 "number precision like the PC's Mono ... measured first",
// "NaN bit patterns checked first, fixed only if the game reads raw bits", "one shared libm, no build-host constant folding", "copy the PC's
// exact float-to-int edge cases"). Reads managed assemblies (read only) and counts, per assembly:
//  1. EXPOSURE to the float evaluation rule: float32 (R4) arithmetic ops (add / sub / mul / div / rem) whose result goes straight into a
//     consumer where the PC's Mono (double intermediates until a store) and a float32 runtime (Shepherd's Mono 8, CoreCLR) can give different
//     answers: another float arithmetic op, a comparison / compare-branch, a float->int conversion, conv.r8, a float64 parameter. A result
//     that goes straight to a float32 store / argument / return gives the same bits either way (one IEEE operation rounded once to float32
//     equals the double operation rounded to float32 -- the innocuous double rounding of +, -, *, /), so those are counted apart.
//  2. NaN / raw-bit readers: BitConverter Single/Double <-> bits / bytes, BinaryWriter.Write(float/double), BinaryReader.ReadSingle/Double,
//     Buffer.BlockCopy, Marshal.Copy of float arrays, Single/Double.GetHashCode, float locals reinterpreted through a pointer (ldloca; conv.u;
//     ldind.i4 / u4 / i8), explicit-layout structs overlapping a float field with another field.
//  3. Constant-argument maths: Math / MathF / Mathf calls whose float arguments are all literals (what a build-host compiler could fold with
//     ITS libm instead of the target's).
//  4. float -> int conversion sites (conv.i1 / i2 / i4 / i8 / u1 / u2 / u4 / u8, the .ovf forms) on a float operand, and Mathf.FloorToInt /
//     CeilToInt / RoundToInt calls (where x64 gives INT_MIN for NaN / out of range and ARM saturates).
// The stack types come from a linear pass per method (reset at a branch target whose incoming stack is unknown): a census, not a verifier.
//   dotnet run -- <report.txt> <dir-or-dll> [...]     (game-derived names in the report: keep it on the build volume, never in git)
using Mono.Cecil;
using Mono.Cecil.Cil;

if (args.Length < 2) { Console.Error.WriteLine("usage: FloatCensus <report.txt> <dir-or-dll> ..."); return 2; }
var files = new List<string>();
foreach (var a in args.Skip(1)) { if (Directory.Exists(a)) files.AddRange(Directory.GetFiles(a, "*.dll").OrderBy(x => x, StringComparer.Ordinal)); else files.Add(a); }
var w = new StreamWriter(args[0]);
var tot = new Dictionary<string, long>();
void Add(string k, long n = 1) => tot[k] = tot.GetValueOrDefault(k) + n;
foreach (var f in files)
{
    AssemblyDefinition asm;
    try { asm = AssemblyDefinition.ReadAssembly(f, new ReaderParameters { ReadSymbols = false }); } catch (Exception e) { w.WriteLine($"# {Path.GetFileName(f)}: not read ({e.GetType().Name})"); continue; }
    var c = new Census(asm.Name.Name);
    foreach (var t in asm.MainModule.GetTypes())
    {
        if (t.IsExplicitLayout) c.ExplicitLayout(t);
        foreach (var m in t.Methods) if (m.HasBody) c.Method(m);
    }
    c.Write(w);
    foreach (var kv in c.Counts) Add(kv.Key, kv.Value);
}
w.WriteLine("== TOTAL"); foreach (var kv in tot.OrderBy(x => x.Key, StringComparer.Ordinal)) w.WriteLine($"   {kv.Key}: {kv.Value:N0}");
w.Close();
Console.WriteLine($"FloatCensus: {files.Count} files -> {args[0]}; exposed float ops {tot.GetValueOrDefault("1 exposed R4 ops"):N0} of {tot.GetValueOrDefault("1 R4 arithmetic ops"):N0} R4 arithmetic ops in {tot.GetValueOrDefault("1 methods with exposed ops"):N0} methods; raw-bit readers {tot.Where(x => x.Key.StartsWith("2 ")).Sum(x => x.Value):N0}; const-arg maths {tot.GetValueOrDefault("3 const-arg maths calls"):N0}; float->int sites {tot.GetValueOrDefault("4 float->int conv sites"):N0}");
return 0;

enum S : byte { I4, I8, R4, R8, F, Ref, Other }
sealed class Census
{
    readonly string _asm; public readonly Dictionary<string, long> Counts = new();
    readonly List<string> _rawSites = new(), _constSites = new(), _ovSites = new(), _fmtSites = new(); readonly Dictionary<string, int> _exposedByMethod = new();
    public Census(string asm) { _asm = asm; }
    void C(string k, long n = 1) => Counts[k] = Counts.GetValueOrDefault(k) + n;
    static S Of(TypeReference t)
    {
        if (t == null) return S.Other;
        switch (t.MetadataType)
        {
            case MetadataType.Single: return S.R4; case MetadataType.Double: return S.R8;
            case MetadataType.Boolean: case MetadataType.Char: case MetadataType.SByte: case MetadataType.Byte: case MetadataType.Int16: case MetadataType.UInt16: case MetadataType.Int32: case MetadataType.UInt32: return S.I4;
            case MetadataType.Int64: case MetadataType.UInt64: return S.I8;
            case MetadataType.IntPtr: case MetadataType.UIntPtr: return S.I4;
            case MetadataType.Void: return S.Other;
            default: return t.IsValueType ? S.Other : S.Ref;
        }
    }
    struct V { public S T; public int Prod; public bool Arith; }   // Prod: the producing instruction's index; Arith: produced by an R4 arithmetic op
    public void ExplicitLayout(TypeDefinition t)
    {
        var byOff = t.Fields.Where(f => !f.IsStatic && f.HasLayoutInfo).GroupBy(f => f.Offset);
        foreach (var g in byOff) if (g.Count() > 1 && g.Any(f => Of(f.FieldType) is S.R4 or S.R8)) { C("2 explicit-layout float overlaps"); _rawSites.Add($"overlap {t.FullName} @{g.Key}: {string.Join(", ", g.Select(f => f.Name + ":" + f.FieldType.Name))}"); }
    }
    public void Method(MethodDefinition m)
    {
        var body = m.Body; var ins = body.Instructions; int n = ins.Count; if (n == 0) return;
        var idx = new Dictionary<Instruction, int>(); for (int i = 0; i < n; i++) idx[ins[i]] = i;
        var targets = new HashSet<int>();
        foreach (var x in ins) { if (x.Operand is Instruction ti) targets.Add(idx[ti]); else if (x.Operand is Instruction[] tis) foreach (var tt in tis) targets.Add(idx[tt]); }
        foreach (var h in body.ExceptionHandlers) { if (h.HandlerStart != null) targets.Add(idx[h.HandlerStart]); if (h.FilterStart != null) targets.Add(idx[h.FilterStart]); }
        var saved = new Dictionary<int, List<V>>();
        var st = new List<V>(); var exposed = new HashSet<int>(); var r4ops = new List<int>(); var sinkSafe = new HashSet<int>();
        S LocT(int k) => k < body.Variables.Count ? Of(body.Variables[k].VariableType) : S.Other;
        S ArgT(int k) { if (m.HasThis) { if (k == 0) return m.DeclaringType.IsValueType ? S.Ref : S.Ref; k--; } return k < m.Parameters.Count ? Of(m.Parameters[k].ParameterType) : S.Other; }
        V Pop() { if (st.Count == 0) return new V { T = S.Other, Prod = -1 }; var v = st[^1]; st.RemoveAt(st.Count - 1); return v; }
        void Push(S t, int i, bool arith = false) => st.Add(new V { T = t, Prod = i, Arith = arith });
        void Consume(V v, string kind)   // a value consumed by kind: "arith", "cmp", "f2i", "r8", "f64arg" (sensitive) or "r4sink" (safe)
        {
            if (!v.Arith || v.Prod < 0) return;
            if (kind == "r4sink") sinkSafe.Add(v.Prod); else exposed.Add(v.Prod);
        }
        for (int i = 0; i < n; i++)
        {
            var x = ins[i]; var op = x.OpCode;
            if (targets.Contains(i)) { if (saved.TryGetValue(i, out var sv)) st = new List<V>(sv); else if (i > 0 && (ins[i - 1].OpCode.FlowControl is FlowControl.Branch or FlowControl.Return or FlowControl.Throw)) st = new List<V>(); }
            switch (op.Code)
            {
                case Code.Ldc_R4: Push(S.R4, i); break;
                case Code.Ldc_R8: Push(S.R8, i); break;
                case Code.Ldc_I4: case Code.Ldc_I4_S: case Code.Ldc_I4_0: case Code.Ldc_I4_1: case Code.Ldc_I4_2: case Code.Ldc_I4_3: case Code.Ldc_I4_4: case Code.Ldc_I4_5: case Code.Ldc_I4_6: case Code.Ldc_I4_7: case Code.Ldc_I4_8: case Code.Ldc_I4_M1: Push(S.I4, i); break;
                case Code.Ldc_I8: Push(S.I8, i); break;
                case Code.Ldloc_0: Push(LocT(0), i); break; case Code.Ldloc_1: Push(LocT(1), i); break; case Code.Ldloc_2: Push(LocT(2), i); break; case Code.Ldloc_3: Push(LocT(3), i); break;
                case Code.Ldloc: case Code.Ldloc_S: Push(LocT(((VariableDefinition)x.Operand).Index), i); break;
                case Code.Ldarg_0: Push(ArgT(0), i); break; case Code.Ldarg_1: Push(ArgT(1), i); break; case Code.Ldarg_2: Push(ArgT(2), i); break; case Code.Ldarg_3: Push(ArgT(3), i); break;
                case Code.Ldarg: case Code.Ldarg_S: { var p = (ParameterDefinition)x.Operand; Push(Of(p.ParameterType), i); break; }
                case Code.Ldloca: case Code.Ldloca_S:
                {
                    var vd = (VariableDefinition)x.Operand; Push(S.Ref, i);
                    if (Of(vd.VariableType) is S.R4 or S.R8 && i + 2 < n && ins[i + 1].OpCode.Code == Code.Conv_U && ins[i + 2].OpCode.Code is Code.Ldind_I4 or Code.Ldind_U4 or Code.Ldind_I8 or Code.Ldind_I)
                    { C("2 float local read through an int pointer"); _rawSites.Add($"reinterpret {m.FullName}"); }
                    break;
                }
                case Code.Ldarga: case Code.Ldarga_S: case Code.Ldflda: case Code.Ldsflda: case Code.Ldelema: case Code.Ldstr: case Code.Ldnull: case Code.Ldtoken: case Code.Ldftn: Push(S.Ref, i); if (op.Code is Code.Ldflda or Code.Ldelema) { Pop(); Push(S.Ref, i); } break;
                case Code.Stloc_0: case Code.Stloc_1: case Code.Stloc_2: case Code.Stloc_3: case Code.Stloc: case Code.Stloc_S:
                {
                    int k = op.Code switch { Code.Stloc_0 => 0, Code.Stloc_1 => 1, Code.Stloc_2 => 2, Code.Stloc_3 => 3, _ => ((VariableDefinition)x.Operand).Index };
                    var v = Pop(); Consume(v, LocT(k) == S.R4 ? "r4sink" : LocT(k) == S.R8 ? "r8" : "other"); break;
                }
                case Code.Starg: case Code.Starg_S: { var v = Pop(); Consume(v, Of(((ParameterDefinition)x.Operand).ParameterType) == S.R4 ? "r4sink" : "r8"); break; }
                case Code.Ldfld: Pop(); Push(Of(((FieldReference)x.Operand).FieldType), i); break;
                case Code.Ldsfld: Push(Of(((FieldReference)x.Operand).FieldType), i); break;
                case Code.Stfld: { var v = Pop(); Pop(); Consume(v, Of(((FieldReference)x.Operand).FieldType) == S.R4 ? "r4sink" : "r8"); break; }
                case Code.Stsfld: { var v = Pop(); Consume(v, Of(((FieldReference)x.Operand).FieldType) == S.R4 ? "r4sink" : "r8"); break; }
                case Code.Ldelem_R4: Pop(); Pop(); Push(S.R4, i); break;
                case Code.Ldelem_R8: Pop(); Pop(); Push(S.R8, i); break;
                case Code.Ldelem_Any: Pop(); Pop(); Push(Of((TypeReference)x.Operand), i); break;
                case Code.Ldelem_I1: case Code.Ldelem_I2: case Code.Ldelem_I4: case Code.Ldelem_U1: case Code.Ldelem_U2: case Code.Ldelem_U4: case Code.Ldelem_I: Pop(); Pop(); Push(S.I4, i); break;
                case Code.Ldelem_I8: Pop(); Pop(); Push(S.I8, i); break;
                case Code.Ldelem_Ref: Pop(); Pop(); Push(S.Ref, i); break;
                case Code.Stelem_R4: { var v = Pop(); Pop(); Pop(); Consume(v, "r4sink"); break; }
                case Code.Stelem_Any: { var v = Pop(); Pop(); Pop(); Consume(v, Of((TypeReference)x.Operand) == S.R4 ? "r4sink" : "r8"); break; }
                case Code.Ldind_R4: Pop(); Push(S.R4, i); break; case Code.Ldind_R8: Pop(); Push(S.R8, i); break;
                case Code.Stind_R4: { var v = Pop(); Pop(); Consume(v, "r4sink"); break; }
                case Code.Ldobj: Pop(); Push(Of((TypeReference)x.Operand), i); break;
                case Code.Stobj: { var v = Pop(); Pop(); Consume(v, Of((TypeReference)x.Operand) == S.R4 ? "r4sink" : "r8"); break; }
                case Code.Dup: { var v = Pop(); st.Add(v); st.Add(new V { T = v.T, Prod = v.Prod, Arith = v.Arith }); break; }
                case Code.Pop: Pop(); break;
                case Code.Add: case Code.Sub: case Code.Mul: case Code.Div: case Code.Rem:
                {
                    var b = Pop(); var a = Pop();
                    bool fl = a.T is S.R4 or S.R8 or S.F || b.T is S.R4 or S.R8 or S.F;
                    if (fl) { Consume(a, "arith"); Consume(b, "arith"); }
                    bool r4 = fl && a.T != S.R8 && b.T != S.R8;
                    if (r4) { C("1 R4 arithmetic ops"); r4ops.Add(i); }
                    Push(fl ? (r4 ? S.R4 : S.R8) : (a.T == S.I8 || b.T == S.I8 ? S.I8 : S.I4), i, r4);
                    break;
                }
                case Code.Neg: { var a = Pop(); Push(a.T, i, a.Arith); break; }   // (exact: passes the producer through)
                case Code.Ceq: case Code.Cgt: case Code.Cgt_Un: case Code.Clt: case Code.Clt_Un: { var b = Pop(); var a = Pop(); Consume(a, "cmp"); Consume(b, "cmp"); Push(S.I4, i); break; }
                case Code.Beq: case Code.Beq_S: case Code.Bne_Un: case Code.Bne_Un_S: case Code.Bge: case Code.Bge_S: case Code.Bge_Un: case Code.Bge_Un_S: case Code.Bgt: case Code.Bgt_S: case Code.Bgt_Un: case Code.Bgt_Un_S: case Code.Ble: case Code.Ble_S: case Code.Ble_Un: case Code.Ble_Un_S: case Code.Blt: case Code.Blt_S: case Code.Blt_Un: case Code.Blt_Un_S:
                { var b = Pop(); var a = Pop(); Consume(a, "cmp"); Consume(b, "cmp"); Save(saved, idx[(Instruction)x.Operand], st); break; }
                case Code.Brtrue: case Code.Brtrue_S: case Code.Brfalse: case Code.Brfalse_S: Pop(); Save(saved, idx[(Instruction)x.Operand], st); break;
                case Code.Br: case Code.Br_S: case Code.Leave: case Code.Leave_S: Save(saved, idx[(Instruction)x.Operand], st); break;
                case Code.Switch: Pop(); foreach (var tt in (Instruction[])x.Operand) Save(saved, idx[tt], st); break;
                case Code.Conv_R4: { var a = Pop(); Consume(a, "r4sink"); Push(S.R4, i); break; }
                case Code.Conv_R8: case Code.Conv_R_Un: { var a = Pop(); Consume(a, "r8"); Push(S.R8, i); break; }
                case Code.Conv_I1: case Code.Conv_I2: case Code.Conv_I4: case Code.Conv_U1: case Code.Conv_U2: case Code.Conv_U4: case Code.Conv_I8: case Code.Conv_U8: case Code.Conv_I: case Code.Conv_U:
                case Code.Conv_Ovf_I1: case Code.Conv_Ovf_I2: case Code.Conv_Ovf_I4: case Code.Conv_Ovf_U1: case Code.Conv_Ovf_U2: case Code.Conv_Ovf_U4: case Code.Conv_Ovf_I8: case Code.Conv_Ovf_U8: case Code.Conv_Ovf_I: case Code.Conv_Ovf_U:
                case Code.Conv_Ovf_I1_Un: case Code.Conv_Ovf_I2_Un: case Code.Conv_Ovf_I4_Un: case Code.Conv_Ovf_U1_Un: case Code.Conv_Ovf_U2_Un: case Code.Conv_Ovf_U4_Un: case Code.Conv_Ovf_I8_Un: case Code.Conv_Ovf_U8_Un: case Code.Conv_Ovf_I_Un: case Code.Conv_Ovf_U_Un:
                {
                    var a = Pop();
                    if (a.T is S.R4 or S.R8) { C("4 float->int conv sites"); C("4 float->int " + op.Code.ToString().ToLowerInvariant()); Consume(a, "f2i"); }
                    Push(op.Code is Code.Conv_I8 or Code.Conv_U8 or Code.Conv_Ovf_I8 or Code.Conv_Ovf_U8 or Code.Conv_Ovf_I8_Un or Code.Conv_Ovf_U8_Un ? S.I8 : S.I4, i);
                    break;
                }
                case Code.Ckfinite: { var a = Pop(); Consume(a, "cmp"); Push(a.T, i); break; }
                case Code.Box: { var v = Pop(); Consume(v, Of((TypeReference)x.Operand) == S.R4 ? "r4sink" : "r8"); Push(S.Ref, i); break; }
                case Code.Ret: { if (m.ReturnType.MetadataType != MetadataType.Void) { var v = Pop(); Consume(v, Of(m.ReturnType) == S.R4 ? "r4sink" : "r8"); } st.Clear(); break; }
                case Code.Call: case Code.Callvirt: case Code.Newobj:
                {
                    var mr = (MethodReference)x.Operand; Call(m, mr, i, ins);
                    var ps = mr.Parameters; var vals = new V[ps.Count];
                    for (int k = ps.Count - 1; k >= 0; k--) vals[k] = Pop();
                    for (int k = 0; k < ps.Count; k++) Consume(vals[k], Of(ps[k].ParameterType) == S.R4 ? "r4sink" : Of(ps[k].ParameterType) == S.R8 ? "f64arg" : "other");
                    if (mr.HasThis && op.Code != Code.Newobj) Pop();
                    if (op.Code == Code.Newobj) Push(S.Ref, i); else if (mr.ReturnType.MetadataType != MetadataType.Void) Push(Of(mr.ReturnType), i);
                    break;
                }
                default:
                {
                    // generic stack effect for everything else (no float typing needed)
                    int pops = op.StackBehaviourPop switch { StackBehaviour.Pop0 => 0, StackBehaviour.Pop1 or StackBehaviour.Popi or StackBehaviour.Popref => 1, StackBehaviour.Pop1_pop1 or StackBehaviour.Popi_pop1 or StackBehaviour.Popi_popi or StackBehaviour.Popi_popi8 or StackBehaviour.Popi_popr4 or StackBehaviour.Popi_popr8 or StackBehaviour.Popref_pop1 or StackBehaviour.Popref_popi => 2, StackBehaviour.Popi_popi_popi or StackBehaviour.Popref_popi_popi or StackBehaviour.Popref_popi_popi8 or StackBehaviour.Popref_popi_popr4 or StackBehaviour.Popref_popi_popr8 or StackBehaviour.Popref_popi_popref => 3, StackBehaviour.PopAll => st.Count, _ => 0 };
                    for (int k = 0; k < pops; k++) Pop();
                    int pushes = op.StackBehaviourPush switch { StackBehaviour.Push0 => 0, StackBehaviour.Push1_push1 => 2, _ => 1 };
                    for (int k = 0; k < pushes; k++) Push(op.StackBehaviourPush == StackBehaviour.Pushr4 ? S.R4 : op.StackBehaviourPush == StackBehaviour.Pushr8 ? S.R8 : op.StackBehaviourPush == StackBehaviour.Pushi8 ? S.I8 : op.StackBehaviourPush == StackBehaviour.Pushref ? S.Ref : S.I4, i);
                    if (op.FlowControl is FlowControl.Throw) st.Clear();
                    break;
                }
            }
        }
        int ex = r4ops.Count(o => exposed.Contains(o));
        C("1 exposed R4 ops", ex); C("1 R4 ops only stored (same bits)", r4ops.Count(o => !exposed.Contains(o)));
        if (ex > 0) { C("1 methods with exposed ops"); _exposedByMethod[m.FullName] = ex; }
        if (r4ops.Count > 0) C("1 methods with R4 arithmetic");
    }
    static void Save(Dictionary<int, List<V>> saved, int target, List<V> st) { if (!saved.ContainsKey(target)) saved[target] = new List<V>(st); }
    static readonly HashSet<string> MathTypes = new() { "System.Math", "System.MathF", "UnityEngine.Mathf" };
    static readonly HashSet<string> MathFns = new() { "Sin", "Cos", "Tan", "Asin", "Acos", "Atan", "Atan2", "Sinh", "Cosh", "Tanh", "Exp", "Log", "Log10", "Log2", "Pow", "Sqrt", "Cbrt", "Floor", "Ceiling", "Ceil", "Round", "Truncate", "FusedMultiplyAdd", "IEEERemainder" };
    void Call(MethodDefinition m, MethodReference mr, int i, Mono.Collections.Generic.Collection<Instruction> ins)
    {
        string dt = mr.DeclaringType.FullName, nm = mr.Name; string sig = mr.Parameters.Count > 0 ? mr.Parameters[0].ParameterType.FullName : "";
        if (dt == "System.BitConverter" && (nm is "SingleToInt32Bits" or "Int32BitsToSingle" or "DoubleToInt64Bits" or "Int64BitsToDouble" or "SingleToUInt32Bits" or "UInt32BitsToSingle" or "DoubleToUInt64Bits" or "ToSingle" or "ToDouble" || nm == "GetBytes" && sig is "System.Single" or "System.Double"))
        { C("2 BitConverter float bits / bytes"); _rawSites.Add($"BitConverter.{nm}({sig}) in {m.FullName}"); }
        else if (dt == "System.IO.BinaryWriter" && nm == "Write" && sig is "System.Single" or "System.Double") { C("2 BinaryWriter.Write(float/double)"); _rawSites.Add($"BinaryWriter.Write({sig}) in {m.FullName}"); }
        else if (dt == "System.IO.BinaryReader" && nm is "ReadSingle" or "ReadDouble") { C("2 BinaryReader.ReadSingle/Double"); _rawSites.Add($"BinaryReader.{nm} in {m.FullName}"); }
        else if (dt == "System.Buffer" && nm is "BlockCopy" or "MemoryCopy") { C("2 Buffer.BlockCopy / MemoryCopy (any array)"); _rawSites.Add($"Buffer.{nm} in {m.FullName}"); }
        else if (dt == "System.Runtime.InteropServices.Marshal" && nm == "Copy" && mr.Parameters.Any(p => p.ParameterType.FullName is "System.Single[]" or "System.Double[]")) { C("2 Marshal.Copy float[]"); _rawSites.Add($"Marshal.Copy(float[]) in {m.FullName}"); }
        else if (dt is "System.Single" or "System.Double" && nm == "GetHashCode") { C("2 Single/Double.GetHashCode"); _rawSites.Add($"{dt}.GetHashCode in {m.FullName}"); }
        if (MathTypes.Contains(dt) && MathFns.Contains(nm) && mr.Parameters.Count > 0 && mr.Parameters.All(p => p.ParameterType.MetadataType is MetadataType.Single or MetadataType.Double))
        {
            bool allConst = true; int k = i - 1;
            for (int a = 0; a < mr.Parameters.Count; a++, k--) { if (k < 0 || ins[k].OpCode.Code is not (Code.Ldc_R4 or Code.Ldc_R8)) { allConst = false; break; } }
            C("3 maths calls (" + dt.Split('.').Last() + ")");
            if (allConst) { C("3 const-arg maths calls"); _constSites.Add($"{dt}.{nm}({string.Join(", ", Enumerable.Range(0, mr.Parameters.Count).Select(a => ins[i - mr.Parameters.Count + a].Operand))}) in {m.FullName}"); }
        }
        if (dt == "UnityEngine.Mathf" && nm is "FloorToInt" or "CeilToInt" or "RoundToInt") C("4 Mathf." + nm + " calls");
        // 5. number formatting (kit7-tests.md finding 6: .NET 8's float.ToString() is the shortest round trip, Unity's Mono class libraries print
        //    7 / 15 digits): float / double formatted with the DEFAULT format ("G" / no format string), and boxed floats fed to string.Concat
        if (dt is "System.Single" or "System.Double" && nm == "ToString")
        {
            bool fmt = mr.Parameters.Any(p => p.ParameterType.FullName == "System.String");
            bool constG = fmt && i > 0 && ins[i - 1].OpCode.Code == Code.Ldstr && ins[i - 1].Operand is string fs && (fs == "" || fs == "G" || fs == "g" || fs == "R" || fs == "r");
            if (!fmt || constG) { C("5 " + dt.Split('.').Last() + ".ToString() default format"); _fmtSites.Add($"{dt}.ToString({(fmt ? "\"" + ins[i - 1].Operand + "\"" : "")}) in {m.FullName}"); }
            else C("5 " + dt.Split('.').Last() + ".ToString(format)");
        }
        if (dt == "System.String" && nm is "Concat" or "Format" && i > 0 && ins[i - 1].OpCode.Code == Code.Box && ins[i - 1].Operand is TypeReference bt && bt.MetadataType is MetadataType.Single or MetadataType.Double)
        { C("5 boxed float into string." + nm); _fmtSites.Add($"string.{nm}(boxed {bt.Name}) in {m.FullName}"); }
    }
    public void Write(StreamWriter w)
    {
        w.WriteLine($"== {_asm}");
        foreach (var kv in Counts.OrderBy(x => x.Key, StringComparer.Ordinal)) w.WriteLine($"   {kv.Key}: {kv.Value:N0}");
        foreach (var kv in _exposedByMethod.OrderByDescending(x => x.Value).Take(15)) w.WriteLine($"   exposed {kv.Value,5}  {kv.Key}");
        foreach (var s in _rawSites.Take(40)) w.WriteLine("   raw-bits: " + s);
        if (_rawSites.Count > 40) w.WriteLine($"   raw-bits: ... {_rawSites.Count - 40} more");
        foreach (var s in _constSites.Take(20)) w.WriteLine("   const-arg: " + s);
        if (_constSites.Count > 20) w.WriteLine($"   const-arg: ... {_constSites.Count - 20} more");
        foreach (var s in _fmtSites.Take(15)) w.WriteLine("   format: " + s);
        if (_fmtSites.Count > 15) w.WriteLine($"   format: ... {_fmtSites.Count - 15} more");
    }
}
