// Kit #7 tests track (vita/notes/kit7-tests.md "Compiler + instruction differential tests"): the recording driver of the IL conformance
// suite. Mono's own mini regression tests (basic.cs, basic-float.cs, basic-long.cs, basic-math.cs, basic-calls.cs, objects.cs, arrays.cs,
// generics.cs, exceptions.cs, ... from the local dotnet/runtime 8.0.31 tree the Vita runtime is built from; compiled ONCE into IL by
// build.sh, never committed) and this track's FpMode.cs are run on every tier -- Unity's Mono (the editor's runtime and the oracle
// players: the game's own runtime, arm64 and x64), CoreCLR (the Mac test host) and, under vlx, Shepherd's Vita runtime -- and every
// test's RETURNED VALUE is recorded (not just pass / fail), so two tiers agree only if every test computes the same number.
// Output: one line per test "<file>\t<class>.<method>\t<returned or EXC:type>\t<expected from the name or ->". Written for .NET 4.x
// (mcs) and .NET 8 alike: no float formatting (System.Native-free), plain reflection.
using System;
using System.IO;
using System.Reflection;
using System.Text;

public static class ShepherdConformance
{
    public static int Main(string[] args)
    {
        string dir = args.Length > 0 ? args[0] : Path.GetDirectoryName(typeof(ShepherdConformance).Assembly.Location);
        string outFile = args.Length > 1 ? args[1] : null;
        var sb = new StringBuilder();
        int n = Run(dir, sb);
        if (outFile != null) File.WriteAllText(outFile, sb.ToString()); else Console.Write(sb.ToString());
        return n > 0 ? 0 : 1;
    }

    /// <summary>Loads every conf-*.dll in dir and runs its tests (each static int method named test_* or fp_* without parameters).</summary>
    public static int Run(string dir, StringBuilder sb)
    {
        var files = Directory.GetFiles(dir, "conf-*.dll"); Array.Sort(files, StringComparer.Ordinal);
        int total = 0;
        foreach (var f in files)
        {
            Assembly a;
            try { a = Assembly.LoadFrom(f); } catch (Exception e) { sb.Append(Path.GetFileName(f)).Append("\t-\tLOADFAIL:").Append(e.GetType().Name).Append("\t-\n"); continue; }
            Type[] types; try { types = a.GetTypes(); } catch (ReflectionTypeLoadException e) { types = e.Types; }
            var list = new System.Collections.Generic.List<MethodInfo>();
            foreach (var t in types)
            {
                if (t == null) continue;
                foreach (var m in t.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    if ((m.Name.StartsWith("test_") || m.Name.StartsWith("fp_")) && m.ReturnType == typeof(int) && m.GetParameters().Length == 0 && !m.ContainsGenericParameters) list.Add(m);
            }
            list.Sort((x, y) => string.CompareOrdinal(x.DeclaringType.FullName + "." + x.Name, y.DeclaringType.FullName + "." + y.Name));
            foreach (var m in list)
            {
                string val;
                try { val = ((int)m.Invoke(null, null)).ToString(System.Globalization.CultureInfo.InvariantCulture); }
                catch (TargetInvocationException e) { val = "EXC:" + (e.InnerException != null ? e.InnerException.GetType().Name : "?"); }
                catch (Exception e) { val = "EXC:" + e.GetType().Name; }
                string exp = "-";
                if (m.Name.StartsWith("test_")) { var p = m.Name.Split('_'); if (p.Length > 2) exp = p[1]; }
                sb.Append(Path.GetFileName(f)).Append('\t').Append(m.DeclaringType.FullName).Append('.').Append(m.Name).Append('\t').Append(val).Append('\t').Append(exp).Append('\n');
                total++;
            }
        }
        return total;
    }
}
