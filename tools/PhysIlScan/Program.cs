// PhysIlScan -- physics Stage 3 (physics-plan-v2 section 4 "Stage 3": "the IL scan for Collision2D retention, and the list of AddForce / AddTorque
// sites outside FixedUpdate", section 3 "Forces", section 1.8 "Collision2D: pooling only if an IL scan proves no handler retains it").
// Reads the game's managed assemblies (read only) and writes a report (game-derived names: keep it on the build volume, never in git).
//   dotnet run -- <game Managed dir> <report.txt>
// 1. Collision2D retention: every field whose type mentions Collision2D (or ContactPoint2D arrays), every method taking a Collision2D, and in
//    those methods every place the Collision2D argument (or a local holding it) escapes: stored to a field / static / array element / a
//    closure's display class, boxed to object, added to a collection, passed to a call other than Collision2D's own members, or returned.
//    A call into another method that also takes a Collision2D is followed (that method is scanned on its own); a delegate Invoke is an escape
//    (its targets are the methods taking Collision2D, scanned too).
// 2. Force sites: every call to Rigidbody2D.AddForce / AddTorque / AddForceAtPosition / AddRelativeForce, classified by where it runs:
//    FixedUpdate (the force acts on the step after it), a coroutine (its owner method), Update / LateUpdate / an event handler / other.
using Mono.Cecil;
using Mono.Cecil.Cil;

var dir = args[0]; var outPath = args[1];
var resolver = new DefaultAssemblyResolver(); resolver.AddSearchDirectory(dir);
var rp = new ReaderParameters { AssemblyResolver = resolver, ReadSymbols = false };
var asms = new List<AssemblyDefinition>();
foreach (var f in Directory.GetFiles(dir, "*.dll").OrderBy(x => x, StringComparer.Ordinal))
{
    var n = Path.GetFileName(f);
    if (n.StartsWith("UnityEngine") || n.StartsWith("System") || n.StartsWith("Mono.") || n.StartsWith("mscorlib") || n.StartsWith("netstandard") || n.StartsWith("Unity.")) continue;
    try { asms.Add(AssemblyDefinition.ReadAssembly(f, rp)); } catch { }
}
var w = new StreamWriter(outPath);
bool IsCol(TypeReference t) => t != null && (t.FullName == "UnityEngine.Collision2D" || t.FullName.Contains("UnityEngine.Collision2D"));
bool IsCpArr(TypeReference t) => t != null && t.FullName.Contains("UnityEngine.ContactPoint2D");
int fieldsCol = 0, methodsCol = 0, handlers = 0, escapes = 0, followCalls = 0, memberUses = 0;
var escList = new List<string>(); var fieldList = new List<string>();
foreach (var a in asms)
    foreach (var t in a.MainModule.GetTypes())
    {
        foreach (var fd in t.Fields)
            if (IsCol(fd.FieldType) || (IsCpArr(fd.FieldType) && fd.FieldType.IsArray)) { fieldsCol++; fieldList.Add($"FIELD {a.Name.Name} {t.FullName}::{fd.Name} : {fd.FieldType.FullName}"); }
        foreach (var m in t.Methods)
        {
            if (!m.HasBody) continue;
            var cp = m.Parameters.Where(p => IsCol(p.ParameterType)).ToList();
            if (cp.Count == 0) continue;
            methodsCol++;
            if (m.Name is "OnCollisionEnter2D" or "OnCollisionStay2D" or "OnCollisionExit2D") handlers++;
            // the argument's slots: its parameter index (+1 for an instance method) and the locals it is copied into
            var argIdx = new HashSet<int>(cp.Select(p => p.Index + (m.HasThis ? 1 : 0)));
            var locals = new HashSet<int>();
            var ins = m.Body.Instructions;
            bool LoadsArg(Instruction i)
            {
                switch (i.OpCode.Code)
                {
                    case Code.Ldarg_0: return argIdx.Contains(0);
                    case Code.Ldarg_1: return argIdx.Contains(1);
                    case Code.Ldarg_2: return argIdx.Contains(2);
                    case Code.Ldarg_3: return argIdx.Contains(3);
                    case Code.Ldarg_S: case Code.Ldarg: return argIdx.Contains(((ParameterDefinition)i.Operand).Index + (m.HasThis ? 1 : 0));
                    case Code.Ldloc_0: return locals.Contains(0);
                    case Code.Ldloc_1: return locals.Contains(1);
                    case Code.Ldloc_2: return locals.Contains(2);
                    case Code.Ldloc_3: return locals.Contains(3);
                    case Code.Ldloc_S: case Code.Ldloc: return locals.Contains(((VariableDefinition)i.Operand).Index);
                }
                return false;
            }
            int LocalOf(Instruction i) => i.OpCode.Code switch
            {
                Code.Stloc_0 => 0, Code.Stloc_1 => 1, Code.Stloc_2 => 2, Code.Stloc_3 => 3,
                Code.Stloc_S or Code.Stloc => ((VariableDefinition)i.Operand).Index, _ => -1
            };
            foreach (var i in ins)
            {
                if (!LoadsArg(i) || i.Next == null) continue;
                var nx = i.Next;
                string what = null;
                int li = LocalOf(nx);
                if (li >= 0) { locals.Add(li); continue; }
                switch (nx.OpCode.Code)
                {
                    case Code.Stfld: case Code.Stsfld: what = $"stored to field {((FieldReference)nx.Operand).DeclaringType.Name}::{((FieldReference)nx.Operand).Name}"; break;
                    case Code.Stelem_Ref: case Code.Stelem_Any: what = "stored to an array element"; break;
                    case Code.Box: what = "boxed"; break;
                    case Code.Ret: what = "returned"; break;
                    case Code.Call: case Code.Callvirt: case Code.Newobj:
                        var mr = (MethodReference)nx.Operand;
                        if (mr.DeclaringType.FullName == "UnityEngine.Collision2D") { memberUses++; break; }
                        if (mr.Name == "op_Inequality" || mr.Name == "op_Equality" || mr.Name == "op_Implicit") { memberUses++; break; }   // (null checks)
                        if (mr.Parameters.Any(p => IsCol(p.ParameterType)) && mr.Name != "Invoke" && !mr.DeclaringType.FullName.StartsWith("System.Collections")) { followCalls++; break; }   // scanned on its own
                        what = $"passed to {mr.DeclaringType.FullName}::{mr.Name}"; break;
                }
                // (an argument consumed later in the instruction stream -- e.g. the second of two call arguments -- is reported by its call when the
                //  next instruction is that call; anything else is conservatively an escape)
                if (what == null && nx.OpCode.Code is not (Code.Call or Code.Callvirt or Code.Newobj))
                {   // a member call a few instructions on (ldarg; ldc.i4.0; call Collision2D::GetContact): consumed by Collision2D itself
                    var q = nx; for (int k = 0; k < 3 && q != null && q.OpCode.Code is not (Code.Call or Code.Callvirt); k++) q = q.Next;
                    if (q != null && q.Operand is MethodReference qm && qm.DeclaringType.FullName == "UnityEngine.Collision2D" && nx.OpCode.Code is Code.Ldc_I4_0 or Code.Ldc_I4_1 or Code.Ldc_I4 or Code.Ldc_I4_S or Code.Ldloc_0 or Code.Ldloc_1 or Code.Ldloc_S) { memberUses++; continue; }
                    what = $"used by {nx.OpCode.Code} (unclassified: treated as an escape)";
                }
                if (what == null) continue;
                escapes++;
                escList.Add($"ESCAPE {a.Name.Name} {t.FullName}::{m.Name} IL_{nx.Offset:x4}: the Collision2D {what}");
            }
        }
    }
// the delegate types whose Invoke takes a Collision2D (an escape through them reaches only their subscribers): who subscribes?
var colDelegates = new HashSet<string>();
foreach (var a in asms) foreach (var t in a.MainModule.GetTypes())
    if (t.BaseType?.FullName == "System.MulticastDelegate" && t.Methods.FirstOrDefault(x => x.Name == "Invoke") is { } inv && inv.Parameters.Any(p => IsCol(p.ParameterType))) colDelegates.Add(t.FullName);
int subs = 0;
foreach (var a in asms) foreach (var t in a.MainModule.GetTypes()) foreach (var m in t.Methods)
{
    if (!m.HasBody) continue;
    foreach (var i in m.Body.Instructions)
        if (i.Operand is MethodReference mr && (mr.Name.StartsWith("add_") || mr.Name == "Combine") && mr.Parameters.Any(p => colDelegates.Contains(p.ParameterType.FullName)))
        { subs++; escList.Add($"SUBSCRIBE {a.Name.Name} {t.FullName}::{m.Name} -> {mr.DeclaringType.Name}::{mr.Name}"); }
        else if (i.Operand is MethodReference ctor && ctor.Name == ".ctor" && colDelegates.Contains(ctor.DeclaringType.FullName))
        { subs++; escList.Add($"SUBSCRIBE {a.Name.Name} {t.FullName}::{m.Name} -> new {ctor.DeclaringType.Name}"); }
}
w.WriteLine($"# delegate types taking a Collision2D: {string.Join(", ", colDelegates)}; subscriptions / delegate constructions of them: {subs}");
Console.WriteLine($"Collision2D delegate types: {colDelegates.Count} ({string.Join(", ", colDelegates)}); subscriptions: {subs}");
w.WriteLine($"# 1. Collision2D retention: {methodsCol} methods take a Collision2D ({handlers} OnCollision*2D handlers); {fieldsCol} fields of Collision2D / ContactPoint2D[] type; " +
            $"{escapes} escapes; {followCalls} calls into other Collision2D-taking methods (scanned on their own); {memberUses} uses of its own members");
foreach (var l in fieldList) w.WriteLine(l);
foreach (var l in escList) w.WriteLine(l);
Console.WriteLine($"Collision2D: {methodsCol} methods take one ({handlers} OnCollision*2D handlers), {fieldsCol} fields of Collision2D / ContactPoint2D[] type, {escapes} escapes, {followCalls} followed calls, {memberUses} member uses");

// ---- 2. force sites ----
var cats = new Dictionary<string, int>();
var siteList = new List<string>();
string Owner(MethodDefinition m)
{   // a coroutine's MoveNext / a lambda: the method that made it
    var t = m.DeclaringType;
    if (m.Name == "MoveNext" && t.Name.StartsWith("<") && t.Name.Contains(">d__")) return "coroutine " + t.Name[1..t.Name.IndexOf('>')];
    if (m.Name.StartsWith("<") && m.Name.Contains(">b__")) return "lambda in " + m.Name[1..m.Name.IndexOf('>')];
    if (t.Name.StartsWith("<>c")) return "lambda in " + (m.Name.Contains('>') ? m.Name[1..m.Name.IndexOf('>')] : m.Name);
    return m.Name;
}
foreach (var a in asms)
    foreach (var t in a.MainModule.GetTypes())
        foreach (var m in t.Methods)
        {
            if (!m.HasBody) continue;
            foreach (var i in m.Body.Instructions)
            {
                if (i.OpCode.Code is not (Code.Call or Code.Callvirt) || i.Operand is not MethodReference mr || mr.DeclaringType.FullName != "UnityEngine.Rigidbody2D") continue;
                if (mr.Name is not ("AddForce" or "AddTorque" or "AddForceAtPosition" or "AddRelativeForce")) continue;
                var own = Owner(m);
                var outer = t; while (outer.DeclaringType != null && outer.Name.StartsWith("<")) outer = outer.DeclaringType;
                string cat = own == "FixedUpdate" ? "FixedUpdate" : own.StartsWith("coroutine") ? "coroutine" : own.StartsWith("lambda") ? "lambda / delegate" :
                             own is "Update" or "LateUpdate" ? own : own.StartsWith("On") ? "event / message handler" : "other method";
                cats[cat] = cats.GetValueOrDefault(cat) + 1;
                bool impulse = false;
                for (var p = i.Previous; p != null && p.Offset >= i.Offset - 8; p = p.Previous) if (p.OpCode.Code == Code.Ldc_I4_1 && mr.Parameters.Count >= 2) impulse = true;
                siteList.Add($"SITE {cat,-24} {a.Name.Name} {outer.FullName}::{own} -> Rigidbody2D.{mr.Name}({string.Join(", ", mr.Parameters.Select(p => p.ParameterType.Name))}){(impulse ? " [mode 1 = Impulse]" : "")}");
            }
        }
w.WriteLine($"# 2. force sites: {siteList.Count} calls; by where they run: {string.Join(", ", cats.OrderByDescending(k => k.Value).Select(k => $"{k.Key} {k.Value}"))}");
foreach (var l in siteList.OrderBy(x => x, StringComparer.Ordinal)) w.WriteLine(l);
w.Close();
Console.WriteLine($"force sites: {siteList.Count}; {string.Join(", ", cats.OrderByDescending(k => k.Value).Select(k => $"{k.Key} {k.Value}"))}");
