// Counts native-bound methods (InternalCall / extern) in Unity's managed engine modules = the "be UnityPlayer" surface.
using System.Reflection; using System.Reflection.Metadata; using System.Reflection.PortableExecutable;
long total = 0;
foreach (var f in Directory.GetFiles(args[0], "UnityEngine*.dll").OrderBy(x => x)) {
    using var pe = new PEReader(File.OpenRead(f)); var md = pe.GetMetadataReader(); int n = 0, all = 0;
    foreach (var h in md.MethodDefinitions) {
        var m = md.GetMethodDefinition(h); all++;
        bool icall = (m.ImplAttributes & MethodImplAttributes.InternalCall) != 0;
        bool pinv = (m.Attributes & MethodAttributes.PinvokeImpl) != 0;
        if (icall || pinv) n++;
    }
    total += n; if (n > 0) Console.WriteLine($"  {Path.GetFileNameWithoutExtension(f),-42} {n,6} native-bound of {all,6} methods");
}
Console.WriteLine($"  {"TOTAL",-42} {total,6}");
