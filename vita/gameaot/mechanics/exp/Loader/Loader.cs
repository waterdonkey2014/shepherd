// GAME-AOT mechanics: loads the assemblies named on the command line (no code of theirs runs) so that the gameaotprof module's
// closure= option can write the value-type closure of each one. Synthetic tool code.
using System;
using System.Reflection;
static class Loader
{
    static int Main(string[] args)
    {
        foreach (var p in args) { var a = Assembly.LoadFrom(p); Console.WriteLine("LOADED " + a.GetName().Name + " " + a.ManifestModule.ModuleVersionId); }
        return 0;
    }
}
