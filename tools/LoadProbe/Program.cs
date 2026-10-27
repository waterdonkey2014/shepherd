// Can CoreCLR (.NET 8) load the game's UNMODIFIED managed assemblies and reflect over every type?
using System.Reflection; using System.Runtime.Loader;
var managed = args[0];
var alc = new AssemblyLoadContext("game");
alc.Resolving += (ctx, name) => {
    var p = Path.Combine(managed, name.Name + ".dll");
    if (File.Exists(p) && name.Name is not ("mscorlib" or "netstandard") && !name.Name.StartsWith("System")) return ctx.LoadFromAssemblyPath(p);
    return null;   // BCL -> .NET 8's own (mscorlib/netstandard facades forward to CoreLib)
};
var asm = alc.LoadFromAssemblyPath(Path.Combine(managed, "Assembly-CSharp.dll"));
Type[] types;
try { types = asm.GetTypes(); Console.WriteLine($"GetTypes OK: {types.Length} types"); }
catch (ReflectionTypeLoadException e) {
    types = e.Types.Where(t => t != null).ToArray();
    Console.WriteLine($"partial: {types.Length} loaded, {e.LoaderExceptions.Length} loader exceptions");
    foreach (var g in e.LoaderExceptions.GroupBy(x => x.Message).OrderByDescending(g => g.Count()).Take(12)) Console.WriteLine($"  x{g.Count()}  {g.Key}");
}
// Try a tiny bit of real execution: a pure-logic static method with no engine dependency.
int ok = 0, fail = 0; var failures = new Dictionary<string,int>();
foreach (var t in types) {
    try { System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(t.TypeHandle); ok++; }
    catch (Exception ex) { fail++; var m = (ex.InnerException ?? ex).GetType().Name + ": " + (ex.InnerException ?? ex).Message; m = m.Length > 110 ? m[..110] : m; failures[m] = failures.GetValueOrDefault(m) + 1; }
}
Console.WriteLine($"static constructors: {ok} ran, {fail} threw");
foreach (var kv in failures.OrderByDescending(k => k.Value).Take(10)) Console.WriteLine($"  x{kv.Value}  {kv.Key}");
Console.WriteLine("loaded assemblies: " + string.Join(", ", alc.Assemblies.Select(a => a.GetName().Name).OrderBy(x => x).Take(80)));
