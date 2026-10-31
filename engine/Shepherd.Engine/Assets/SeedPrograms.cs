using Shepherd.Assets;
using Shepherd.Engine.Native;
namespace Shepherd.Engine.Assets;

/// <summary>Device kit #10, track load, row E3 for the built-in components (SpriteRenderer, Canvas, CanvasRenderer, Camera, ...): Loader.SeedProps
/// -- the record's property bag seeded from its value tree -- by a program compiled once per pack shape. SeedProps walks the tree's keys in
/// order, skips the identity keys, renames each kept key to its scripting name (the Renames table, else "m_Xyz" -> "xyz": two new strings per
/// key per object), drops a bulky value under a key nothing reads (the diet: Diet.IsReadableKey), counts, converts the value (Loader.Plain)
/// and finally sets the kept keys as one bag (NObj.SetMany: the shape the Set sequence would reach, a repeated key in its first place with the
/// last value). The program keeps, per key place, the skip decision and the scripting name (the same strings), asks the diet the same
/// question at the same first moment (per key it remembers the answer: Diet.IsReadableKey is a set lookup once built), converts every value
/// with Plain in the same order, and builds the bag at once: the shape of the last kept-key pattern is kept with each key's slot (a shape is
/// the one PropShape reached by With from Empty -- the same instance), PropsVer counted per key as SetMany does. Only for pack trees with
/// shared keys, a shaped bag (PropBag.Shaped) and a record without a bag yet (every new record); anything else runs SeedProps.
/// SHEPHERD_K10_SEEDPROG=0: SeedProps. Independent of the create programs' switch (kit #10 review: it used TreeIdx.Use, so
/// SHEPHERD_K10_CREATEPROG=0 turned the seed programs off too while the start line said "on").</summary>
static class SeedPrograms
{
    public static readonly bool On = Runtime.K10LoadTrack.SeedProg;
    public static long Objects, Programs, Keys;
    sealed class Prog
    {
        public string[] Names; public bool[] Skip; public sbyte[] Readable;   // per key place (-1 = not asked yet)
        public bool[] LastKept; public PropShape LastShape; public int[] LastSlots; public int LastCount = -1;
    }
    static readonly Dictionary<object, Prog> Progs = new(TreeIdx.ByRef);
    static bool[] _kept = new bool[64]; static object[] _vals = new object[64];

    public static bool TrySeed(NObj rec, TypeTreeObject d, SerializedFile f)
    {
        if (!On || rec.Props != null || !PropBag.Shaped || f?.Pack == null || !PackReader.ShareKeys) return false;   // (pack trees with shared shape keys)
        var keys = d.Keys;
        if (!Progs.TryGetValue(keys, out var p)) { Progs[keys] = p = Compile(keys); Programs++; }
        int n = keys.Count;
        if (_kept.Length < n) { _kept = new bool[n * 2]; _vals = new object[n * 2]; }
        var kept = _kept; var vals = _vals; var src = d.Values;
        int count = 0;
        Objects++;
        Array.Clear(kept, 0, n);   // (kit #10 review: every place's flag cleared first -- a throw at key i must not leave an earlier record's flags at i+1..n-1 for the finally's Apply)
        try
        {
            for (int i = 0; i < n; i++)
            {
                if (p.Skip[i]) continue;
                var v = src[i];
                if (!Diet.KeepProps && Loader.IsBulky(v) && !Readable(p, i)) { Diet.PropDropped(rec, p.Names[i]); continue; }
                Diet.PropsKeysKept++;
                vals[i] = Loader.Plain(v, f); kept[i] = true; count++;
            }
        }
        finally
        {
            if (count > 0) Apply(rec, p, kept, vals, n, count);   // (SeedProps' finally: SetMany of what was kept so far)
            Array.Clear(vals, 0, n);
        }
        Keys += count;
        return true;
    }
    static bool Readable(Prog p, int i)
    {
        if (p.Readable[i] < 0) p.Readable[i] = (sbyte)(Diet.IsReadableKey(p.Names[i]) ? 1 : 0);
        return p.Readable[i] == 1;
    }
    static void Apply(NObj rec, Prog p, bool[] kept, object[] vals, int n, int count)
    {
        bool same = p.LastCount == count && p.LastKept != null;
        if (same) for (int i = 0; i < n; i++) if (p.LastKept[i] != kept[i]) { same = false; break; }
        if (!same)
        {
            var sh = PropShape.Empty;
            for (int i = 0; i < n; i++) if (kept[i] && sh.IndexOf(p.Names[i]) < 0) sh = sh.With(p.Names[i]);   // (PropBag.SetMany on an empty bag)
            var slots = new int[n];
            for (int i = 0; i < n; i++) slots[i] = kept[i] ? sh.IndexOf(p.Names[i]) : 0;
            p.LastKept = (bool[])kept.AsSpan(0, n).ToArray(); p.LastShape = sh; p.LastSlots = slots; p.LastCount = count;
        }
        var shape = p.LastShape; var sl = p.LastSlots;
        var t = new object[shape.Keys.Length + 1]; t[0] = shape;
        for (int i = 0; i < n; i++) if (kept[i]) t[sl[i]] = vals[i];   // (in key order: a repeated key ends with its last value)
        rec.Props = t;
        if (rec is NRenderer r) r.PropsVer += count;   // (NObj.SetMany: PropsVer per key)
    }
    static Prog Compile(IReadOnlyList<string> keys)
    {
        int n = keys.Count; var p = new Prog { Names = new string[n], Skip = new bool[n], Readable = new sbyte[n] };
        for (int i = 0; i < n; i++)
        {
            var k = keys[i];
            p.Readable[i] = -1;
            if (k is "m_GameObject" or "m_Name" or "m_Script" or "m_ObjectHideFlags" or "m_CorrespondingSourceObject" or "m_PrefabInstance" or "m_PrefabAsset") { p.Skip[i] = true; continue; }
            p.Names[i] = Loader.PropName(k);
        }
        return p;
    }
    public static string Summary() => !On ? null : $"[k10] seed programs: {Objects} built-in records' bags seeded by {Programs} programs, {Keys} keys";
}
