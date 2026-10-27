namespace Shepherd.Engine.Runtime;

/// <summary>Device kit #5, track "menus" (vita/notes/kit5-menus.md): the switches back to kit #4's behaviour, read once at start from the
/// environment (on the Vita: the app's env file), and the track's log helpers. <c>SHEPHERD_K5_MENUS=0</c> turns every item of the track
/// off; each item also has its own switch. None of them changes what the game does or what is drawn: they change how the engine logs and
/// how fast it finds the same answers. (ProfileOnly: reads no property-bag keys, so the diet's literal scan skips it.)</summary>
[ProfileOnly]
public static class MenusCfg
{
    static readonly bool Track = Environment.GetEnvironmentVariable("SHEPHERD_K5_MENUS") != "0";
    /// <summary>A Resources.Load (or GetBuiltinResource) that finds nothing is logged once per path and type, then counted; the counts
    /// are one line at the end of the run (<see cref="MissSummary"/>). <c>SHEPHERD_LOG_REPEATS=all</c> logs every one, as kit #4 did.
    /// Why: I2 Localization asks Resources.Load for its font overrides ('Fonts/Headings/LaptureRegular', 'Fonts/Body/FiraSans-Regular SDF')
    /// once per localized text, and the game moved those fonts out of Resources into Addressables ("Resources_moved"): the original game
    /// gets null there too, silently (Unity logs nothing for a Resources.Load miss). Logging each one cost a line on the memory card per
    /// text: closing Settings re-localizes ~1,530 texts in one frame, and on the device that frame took 22.2 s.</summary>
    public static readonly bool LogRepeatsOnce = Track && Environment.GetEnvironmentVariable("SHEPHERD_LOG_REPEATS") != "all";
    /// <summary>Resources.FindObjectsOfTypeAll: one pass over the records in the same order, without LINQ, with a cheap type test first;
    /// <c>SHEPHERD_FINDALL_FAST=0</c> = kit #4's query.</summary>
    public static readonly bool FindAllFast = Track && Environment.GetEnvironmentVariable("SHEPHERD_FINDALL_FAST") != "0";
    /// <summary>Instantiate copies serialized fields by the runtime's field offsets instead of FieldInfo.GetValue / SetValue
    /// (Runtime/FieldCopy.cs); <c>SHEPHERD_FAST_CLONE=0</c> = kit #4's reflection copy.</summary>
    public static readonly bool FastClone = Track && Environment.GetEnvironmentVariable("SHEPHERD_FAST_CLONE") != "0";
    /// <summary>Per-type answers kept instead of recomputed (the same answers): VitaPad.BeforeAwake's Rewired base-type walk, the
    /// constructor Objects.RunCtor runs, DeepCopy's built-in-value test of a value's type. <c>SHEPHERD_TYPE_MEMOS=0</c> = kit #4's lookups.</summary>
    public static readonly bool TypeMemos = Track && Environment.GetEnvironmentVariable("SHEPHERD_TYPE_MEMOS") != "0";
    /// <summary>GetComponents(Type, List&lt;Component&gt;) on one object fills the caller's list directly (Icalls/Core.cs Collect).
    /// OFF unless <c>SHEPHERD_FAST_COMPS=1</c>: it saves 5-10 % of the Credits frames (Mac) but it is the item that moved the vlx
    /// device-clock boot most often to "one major collection fewer before the Main Menu" (the menu loads on ~17 MiB of uncollected
    /// garbage, the arena peaks ~4 MiB higher; vita/notes/kit5-menus.md, "The boot's GC schedule").</summary>
    public static readonly bool FastComps = Track && Environment.GetEnvironmentVariable("SHEPHERD_FAST_COMPS") == "1";

    // ---- the once-per-key miss log ----
    static readonly object Gate = new();
    static readonly Dictionary<(string, Type), int> LoadMisses = new();
    static readonly Dictionary<(string, Type), int> BuiltinMisses = new();
    static long _repeats;

    /// <summary>Resources.Load(path, type) returned null: the first one per (path, type) is the kit #4 line, word for word; later ones are
    /// counted (Log.Warnings counts each, as before).</summary>
    public static void ResourceMiss(string path, Type type)
    {
        if (!Count(LoadMisses, (path, type))) Log.Warn($"Resources.Load('{path}', {type?.Name}) -> null");
    }
    /// <summary>Resources.GetBuiltinResource(type, name) returned null (the same rule).</summary>
    public static void BuiltinMiss(Type type, string name)
    {
        if (!Count(BuiltinMisses, (name, type))) Log.Warn($"GetBuiltinResource({type?.Name}, '{name}') -> null");
    }
    static bool Count(Dictionary<(string, Type), int> d, (string, Type) key)
    {
        int n;
        lock (Gate) { d.TryGetValue(key, out n); d[key] = n + 1; if (n > 0) _repeats++; }
        if (n > 0) Log.Warnings++;   // (logged once; still counted as a warning)
        return n > 0;
    }
    /// <summary>The end-of-run line: every (path, type) that missed, with its count. Null when nothing was held back.</summary>
    public static string MissSummary()
    {
        lock (Gate)
        {
            if (_repeats == 0) return null;
            static string Row(KeyValuePair<(string, Type), int> e) => $"'{e.Key.Item1}' {e.Key.Item2?.Name} x{e.Value}";
            var load = LoadMisses.OrderByDescending(e => e.Value).Select(Row);
            var builtin = BuiltinMisses.OrderByDescending(e => e.Value).Select(Row);
            return $"[k5] Resources misses (each logged once above, {_repeats} repeats counted, not logged; SHEPHERD_LOG_REPEATS=all logs every one): " +
                   $"Resources.Load {LoadMisses.Values.Sum()} calls: {string.Join(", ", load)}" +
                   (BuiltinMisses.Count > 0 ? $"; GetBuiltinResource {BuiltinMisses.Values.Sum()} calls: {string.Join(", ", builtin)}" : "");
        }
    }
}
