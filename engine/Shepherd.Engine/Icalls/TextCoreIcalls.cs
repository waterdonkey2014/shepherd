using System.Buffers.Binary;
using System.Collections;
using Shepherd.Engine.Native;
namespace Shepherd.Engine.Impl;

/// <summary>kit3 (sub-menus): the part of Unity's TextCore FontEngine that TextMeshPro's dynamic font assets reach before they rasterize
/// anything: loading a Font's face and mapping a code point to the face's glyph index. TMP calls them when a Dynamic font asset is first
/// read (its synthesized control characters) and for every character its atlas lacks (TMP_FontAsset.TryAddCharacterInternal): on the
/// Main Menu's Settings screen that is the controller-icon font 'Xbox-Filled' (ellipsis, underline, the control characters). Missing, both
/// returned 0 (= FontEngineError.Success, "no glyph"), so every lookup claimed a glyph-less font whatever the font holds.
///
/// Here, as in Unity: LoadFontFace(Font, pointSize) opens the Font's own font file (Font.m_FontData, face 0 of a collection) and makes it
/// the current face; GetGlyphIndex / TryGetGlyphIndex read the face's character map with FreeType's rules (the Unicode charmap FreeType
/// selects when it opens a face: the last UCS-4 one, else the last UCS-2 one; an index past maxp.numGlyphs is 0). A lookup that finds a
/// glyph the atlas lacks then goes on to TryAddGlyphToTexture, which this engine does not rasterize (it stays a missing hook, so TMP
/// leaves the character out, logged once per font here). No property-bag keys are read ([ProfileOnly]): the font file is re-read from
/// the source record.</summary>
[ProfileOnly]
static class TextCoreIcalls
{
    const int Success = 0, InvalidFile = 4, InvalidFileFormat = 2, InvalidTable = 8;
    sealed class Face
    {
        public string Name; public int Error; public byte[] Cmap; public int Format; public int NumGlyphs; public string Charmap;
        public readonly HashSet<uint> Found = new();
    }
    static readonly Dictionary<NObj, Face> Faces = new();
    static Face _current;

    [Icall("UnityEngine.TextCore.LowLevel.FontEngine::LoadFontFace_With_Size_FromFont_Internal")]
    static object LoadFromFont(object[] a)
    {
        var n = Objects.Get(a[0]);
        if (n == null) { _current = null; return InvalidFile; }
        if (!Faces.TryGetValue(n, out var f)) { Faces[n] = f = Open(n); Log.Info($"[textcore] LoadFontFace '{f.Name}': {(f.Error == Success ? $"{f.NumGlyphs} glyphs, charmap {f.Charmap} (format {f.Format})" : $"error {f.Error}")}"); }
        _current = f.Error == Success ? f : null;
        return f.Error;
    }

    [Icall("UnityEngine.TextCore.LowLevel.FontEngine::GetGlyphIndex")]
    static object GetGlyphIndex(object[] a) => Lookup((uint)a[0]);

    [Icall("UnityEngine.TextCore.LowLevel.FontEngine::TryGetGlyphIndex")]
    static object TryGetGlyphIndex(object[] a) { uint g = Lookup((uint)a[0]); a[1] = g; return g != 0; }

    static uint Lookup(uint unicode)
    {
        var f = _current; if (f == null || f.Cmap == null) return 0u;
        uint g = Map(f.Cmap, f.Format, unicode);
        if (g >= (uint)f.NumGlyphs) g = 0;
        if (g != 0 && f.Found.Add(unicode) && f.Found.Count <= 32)
            Log.Info($"[textcore] '{f.Name}' has a glyph for U+{unicode:X4} (index {g}); a character missing from its TMP atlas would need rasterizing, which this engine does not do");
        return g;
    }

    static Face Open(NObj n)
    {
        var f = new Face { Name = n.Name };
        byte[] data = null;
        try { data = Bytes(n.SourceTree(transient: true)?["m_FontData"]); } catch (Exception e) { Log.Warn($"[textcore] '{n.Name}': font data unreadable ({e.Message})"); }
        if (data == null || data.Length < 12) { f.Error = InvalidFile; return f; }
        try { f.Error = ReadCmap(data, f); }
        catch (Exception e) when (e is ArgumentOutOfRangeException or IndexOutOfRangeException) { f.Error = InvalidFileFormat; }
        return f;
    }

    /// <summary>m_FontData as the asset layer decodes a vector&lt;char&gt;: a byte[], or a list of numbers / chars.</summary>
    static byte[] Bytes(object v)
    {
        switch (v)
        {
            case byte[] b: return b;
            case IList l:
            {
                var r = new byte[l.Count];
                for (int i = 0; i < r.Length; i++) r[i] = l[i] switch { byte x => x, sbyte x => unchecked((byte)x), char x => unchecked((byte)x), IConvertible c => unchecked((byte)c.ToInt32(null)), _ => 0 };
                return r;
            }
            default: return null;
        }
    }

    static ushort U16(byte[] d, int o) => BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(o, 2));
    static short S16(byte[] d, int o) => BinaryPrimitives.ReadInt16BigEndian(d.AsSpan(o, 2));
    static uint U32(byte[] d, int o) => BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(o, 4));

    /// <summary>Face 0's tables: maxp.numGlyphs and the Unicode charmap FreeType selects by default (find_unicode_charmap: scanning the
    /// encoding records backwards, the first UCS-4 one -- (3,10), (0,6), (0,4) -- else the first Unicode one -- platform 0 or (3,1)/(3,10);
    /// (0,5) is the variation-selector table, never a charmap). No Unicode charmap: FreeType leaves the face without one and every lookup is 0.</summary>
    static int ReadCmap(byte[] d, Face f)
    {
        int sfnt = 0;
        if (U32(d, 0) == 0x74746366u) sfnt = (int)U32(d, 12);   // 'ttcf': a collection, face 0
        int numTables = U16(d, sfnt + 4), cmap = -1, maxp = -1;
        for (int i = 0; i < numTables; i++)
        {
            int rec = sfnt + 12 + 16 * i; uint tag = U32(d, rec);
            if (tag == 0x636D6170u) cmap = (int)U32(d, rec + 8);        // 'cmap'
            else if (tag == 0x6D617870u) maxp = (int)U32(d, rec + 8);   // 'maxp'
        }
        if (maxp < 0) return InvalidTable;
        f.NumGlyphs = U16(d, maxp + 4);
        if (cmap < 0) return Success;   // a face without a cmap opens; it maps nothing
        int n = U16(d, cmap + 2), pick = -1; bool ucs4 = false;
        for (int i = n - 1; i >= 0; i--)
        {
            int rec = cmap + 4 + 8 * i; int pid = U16(d, rec), eid = U16(d, rec + 2);
            bool unicode = pid == 0 && eid != 5 || pid == 3 && (eid == 1 || eid == 10);
            if (!unicode) continue;
            bool wide = pid == 3 && eid == 10 || pid == 0 && (eid == 4 || eid == 6);
            if (wide) { pick = i; ucs4 = true; break; }
            if (pick < 0) pick = i;
        }
        if (pick < 0) { f.Charmap = "none (no Unicode charmap)"; return Success; }
        int prec = cmap + 4 + 8 * pick, sub = cmap + (int)U32(d, prec + 4);
        f.Charmap = $"({U16(d, prec)},{U16(d, prec + 2)}){(ucs4 ? " UCS-4" : "")}";
        int format = U16(d, sub);
        int len = format switch { 0 or 2 or 4 or 6 => U16(d, sub + 2), 8 or 10 or 12 or 13 => (int)U32(d, sub + 4), _ => 0 };
        if (len <= 0 || sub + len > d.Length || format is not (0 or 4 or 6 or 10 or 12 or 13)) { f.Charmap += $" format {format} not read"; return Success; }
        f.Format = format; f.Cmap = d.AsSpan(sub, len).ToArray();
        return Success;
    }

    /// <summary>One character map subtable lookup (TrueType / OpenType cmap formats 0, 4, 6, 10, 12, 13; FreeType's tt_cmapN_char_index).</summary>
    static uint Map(byte[] c, int format, uint u)
    {
        switch (format)
        {
            case 0: return u < 256 && 6 + u < c.Length ? c[6 + u] : 0u;
            case 4:
            {
                if (u > 0xFFFF) return 0;
                int segX2 = U16(c, 6), ends = 14, starts = ends + segX2 + 2, deltas = starts + segX2, offs = deltas + segX2;
                for (int s = 0; s < segX2; s += 2)
                {
                    int end = U16(c, ends + s); if (u > end) continue;
                    int start = U16(c, starts + s); if (u < start) return 0;
                    int delta = S16(c, deltas + s), ro = U16(c, offs + s);
                    if (ro == 0) return (uint)((u + delta) & 0xFFFF);
                    int at = offs + s + ro + 2 * (int)(u - start);
                    if (at + 2 > c.Length) return 0;
                    uint g = U16(c, at);
                    return g == 0 ? 0u : (uint)((g + delta) & 0xFFFF);
                }
                return 0;
            }
            case 6:
            {
                uint first = U16(c, 6), count = U16(c, 8);
                return u >= first && u - first < count ? U16(c, 10 + 2 * (int)(u - first)) : 0u;
            }
            case 10:
            {
                uint first = U32(c, 12), count = U32(c, 16);
                return u >= first && u - first < count ? U16(c, 20 + 2 * (int)(u - first)) : 0u;
            }
            case 12: case 13:
            {
                uint groups = U32(c, 12);
                for (int i = 0; i < groups; i++)
                {
                    int g = 16 + 12 * i; uint start = U32(c, g), end = U32(c, g + 4), id = U32(c, g + 8);
                    if (u < start) return 0;
                    if (u <= end) return format == 12 ? id + (u - start) : id;
                }
                return 0;
            }
        }
        return 0;
    }
}
