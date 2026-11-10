using System.Text;

namespace Shepherd.Assets;

/// <summary>Kit #13 basec (vita/notes/kit13-basec.md "One copy of repeated strings"): the strings the asset reader makes from the game's own files
/// (TypeTree string fields: object names, the game's serialized string fields, I2's translations; the engine's binary field programs; the
/// Addressables catalog's key strings) are handed out as one shared copy per distinct value while a frame's data is read. The base scene alone
/// makes ~250,000 strings whose value was already made (16 MiB at the Vita's sizes, MEASURED-M census). Strings are immutable: sharing one copy
/// changes no value any reader sees (only object identity -- ReferenceEquals or a lock on a string -- could tell, which neither the game's
/// serialized fields nor the engine use). The pool lives only while data is read: the engine clears it at every frame's end
/// (<see cref="Clear"/>), so it never holds memory between loads; strings longer than <see cref="MaxLen"/> characters are not pooled.
/// The pack decoder's inline strings are NOT pooled: the pack baker already keeps every value used twice or more (up to 256 chars) in the
/// pack's shared string table, so inside one pack an inline string is the only one of its value (tools/VitaPack/PackWriter.cs Write, step 1).
/// By default the boot frame is not pooled (pooling starts at the first frame's end): on the pack route the boot frame's reads (the binary
/// field programs of I2's sources and Rewired's data) held 23,104 values for 1,264 shared hits (Mac fixtures, MEASURED-M), a pool built at
/// the boot for ~0.08 MiB; the intro's load then shares 8,931 strings with a pool of ~600 values.
/// Off unless the engine turns it on (<see cref="Enable"/>; the engine's switch: SHEPHERD_K13_STRPOOL=0 or SHEPHERD_K13_BASEC=0 = every read
/// makes its own string, kit #12; SHEPHERD_K13_STRPOOL=boot = the boot frame pooled too): the tools that compile these sources (the pack
/// baker, CompoScan, ...) never pool and never hold strings.</summary>
public static class StringPool
{
    /// <summary>Pooling on (set by the engine: at start, or at the first frame's end; false in every other process).</summary>
    public static bool On { get; private set; }
    /// <summary>Enabled, waiting for the first frame's end (the boot frame not pooled).</summary>
    public static bool Pending { get; private set; }
    public const int MaxLen = 256;
    /// <summary>Where a pooled string came from (the end-of-run line's per-site counts).</summary>
    public enum Site { Tree, Bin, Catalog }
    static readonly object _lock = new();
    static HashSet<string> _set;
    /// <summary>Strings handed out from the pool instead of a new copy / strings added / non-empty pools dropped (since start; for the log).</summary>
    public static long Shared, Added, Clears;
    /// <summary>The characters of the shared hits (the copies not kept: ~ Shared x 14 + 2 x SharedChars bytes at the Vita's 32-bit sizes).</summary>
    public static long SharedChars;
    /// <summary>The largest pool dropped (values held at once) and the clear that dropped it (1 = the first non-empty clear).</summary>
    public static int MaxHeld; public static long MaxHeldAt;
    public static readonly long[] SharedBy = new long[3], AddedBy = new long[3];

    /// <summary>The engine's start (before any asset is read): pooling on or off for this process; afterBoot = from the first <see cref="Clear"/>
    /// (the end of the first frame) on.</summary>
    public static void Enable(bool on, bool afterBoot) { On = on && !afterBoot; Pending = on && afterBoot; }

    /// <summary>A string for these UTF-8 bytes: the pooled copy of that value when there is one.</summary>
    public static string Get(ReadOnlySpan<byte> utf8, Site site)
    {
        var s = Encoding.UTF8.GetString(utf8);
        return On && s.Length <= MaxLen ? Intern(s, site) : s;
    }

    /// <summary>The pooled copy of s (s itself when it is the first of its value).</summary>
    public static string Intern(string s, Site site)
    {
        if (!On || s == null || s.Length > MaxLen) return s;
        if (s.Length == 0) return string.Empty;
        lock (_lock)
        {
            _set ??= new HashSet<string>(StringComparer.Ordinal);
            if (_set.TryGetValue(s, out var have)) { Shared++; SharedBy[(int)site]++; SharedChars += s.Length; return have; }
            _set.Add(s); Added++; AddedBy[(int)site]++;
            return s;
        }
    }

    /// <summary>Drops the pool (the strings handed out stay with their readers). Returns the number of values it held.</summary>
    public static int Clear()
    {
        if (Pending) { Pending = false; On = true; return 0; }   // the boot frame's end: pooling starts
        if (!On) return 0;
        lock (_lock)
        {
            int n = _set?.Count ?? 0;
            _set = null;
            if (n > 0) { Clears++; if (n > MaxHeld) { MaxHeld = n; MaxHeldAt = Clears; } }
            return n;
        }
    }

    /// <summary>The end-of-run numbers (null when off).</summary>
    public static string Summary() => !On ? (Pending ? "string pool: enabled, no frame ended" : null) :
        $"string pool: {Shared:N0} shared / {Added:N0} added (tree {SharedBy[0]:N0} / {AddedBy[0]:N0}, binary programs {SharedBy[1]:N0} / {AddedBy[1]:N0}, " +
        $"catalog {SharedBy[2]:N0} / {AddedBy[2]:N0}), ~{(Shared * 14 + SharedChars * 2) / 1048576.0:F2} MiB of copies not made (DERIVED, 32-bit sizes), " +
        $"largest pool {MaxHeld:N0} values (clear #{MaxHeldAt} of {Clears})";
}
