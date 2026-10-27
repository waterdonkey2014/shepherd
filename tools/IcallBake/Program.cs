// kit #4 boot (B-1): icalls.json -> icalls.bin, the engine's flat copy of the hook manifest (engine/Shepherd.Engine/IcallManifest.cs).
// usage: IcallBake <icalls.json> [<icalls.bin>]   (default: icalls.bin next to the json)
//        IcallBake --check <icalls.json> [<icalls.bin>]   exit 0 when the bake exists, matches the json's length and content hash, and
//                                                        decodes to the json's rows field by field
// The json and the bake are derived from the game's DLLs (tools/Rewriter): they live in runtime/ManagedVita or the owner's managed
// folder only, never in git or a VPK. Writes go to a temporary name first, then a rename.
using Shepherd.Engine;

bool check = args.Length > 0 && args[0] == "--check";
var a = check ? args.Skip(1).ToArray() : args;
if (a.Length < 1 || a.Length > 2) { Console.Error.WriteLine("usage: IcallBake [--check] <icalls.json> [<icalls.bin>]"); return 2; }
var jsonPath = a[0]; var binPath = a.Length > 1 ? a[1] : IcallManifest.BakePath(jsonPath);
var json = File.ReadAllBytes(jsonPath);
var rows = IcallManifest.FromJson(json);
if (check)
{
    if (!File.Exists(binPath)) { Console.WriteLine($"FAIL {binPath}: missing"); return 1; }
    var got = IcallManifest.Read(File.ReadAllBytes(binPath), json.Length, out var fnv, out var why);
    if (got == null) { Console.WriteLine($"FAIL {binPath}: {why}"); return 1; }
    if (fnv != IcallManifest.ContentHash(json)) { Console.WriteLine($"FAIL {binPath}: made from another icalls.json of the same length"); return 1; }
    if (IcallManifest.Diff(rows, got) is { } d) { Console.WriteLine($"FAIL {binPath}: {d}"); return 1; }
    Console.WriteLine($"PASS {binPath}: {got.Length} rows, equal to {jsonPath} field by field");
    return 0;
}
var bin = IcallManifest.Write(rows, json);
var back = IcallManifest.Read(bin, json.Length, out _, out var why2);
if (back == null || IcallManifest.Diff(rows, back) is { } d2) { Console.Error.WriteLine($"IcallBake: the bake does not read back: {why2}"); return 1; }
var tmp = binPath + ".tmp"; File.WriteAllBytes(tmp, bin); File.Move(tmp, binPath, true);
Console.WriteLine($"IcallBake: {rows.Length} rows, {json.Length} -> {bin.Length} bytes: {binPath}");
return 0;
