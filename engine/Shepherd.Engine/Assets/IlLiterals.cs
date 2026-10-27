using System.Text;
namespace Shepherd.Engine.Assets;

/// <summary>fid1-fixup (112m edge): the V3 diet's literal scan (Diet.BuildReadable) read straight from the engine's own assembly file
/// (ECMA-335: PE headers, the metadata tables, the method bodies, the #US heap) instead of through reflection.
///
/// Why: the reflection scan called Type.GetMethods / GetConstructors and MethodBase.GetMethodBody on every method of every non-profile
/// engine type. Mono caches every reflection object it hands out for the life of the domain, so that one scan left ~2,400 MethodInfo,
/// MethodBody (with its IL byte[], locals and exception clauses) objects live on the managed heap for good (the sim's heap census at
/// the Main Menu load, frame 805: RuntimeMethodBody 2,385, RuntimeLocalVariableInfo 3,603, ...), and resolving the bodies' local
/// types is also why profile code must stay out of it (ProfileOnlyAttribute). This reader holds nothing after it returns: one
/// transient byte[] of the file.
///
/// It reproduces the reflection scan exactly: the same methods (every MethodDef with a body of every type but the module type, skipping
/// types marked [ProfileOnly] and the types nested in them), the same byte pattern over the same IL bytes (0x72 .. .. .. 0x70: an ldstr
/// of a user-string token, not decoded as instructions), and Mono's ResolveString semantics for the token (offset 0 or past the #US heap
/// is refused; otherwise the blob at that offset, length/2 UTF-16 code units). SHEPHERD_DIET_SCAN=reflect uses the old scan,
/// SHEPHERD_DIET_SCAN=both runs both and logs whether they agree. Any surprise in the file (no CLI header, an unknown table, a body
/// outside the file) returns null and the caller falls back to reflection.</summary>
[ProfileOnly]   // reads no property-bag keys; its literals (stream and attribute names, its log texts) are not engine keys either
public static class IlLiterals
{
    public static readonly string Mode = (Environment.GetEnvironmentVariable("SHEPHERD_DIET_SCAN") ?? "").Trim().ToLowerInvariant();
    /// <summary>SHEPHERD_DIET_SCAN=both: the reflection scan runs too and <see cref="Compare"/> logs the verdict.</summary>
    public static bool Both => Mode == "both";

    /// <summary>The diet's scan of <paramref name="asm"/> (types marked <paramref name="skip"/> left out), or null: SHEPHERD_DIET_SCAN=reflect,
    /// or the file did not read as expected (logged once).</summary>
    public static List<string> ForDiet(System.Reflection.Assembly asm, Type skip)
    {
        if (Mode == "reflect") return null;
        // The Vita host names the engine's image with its device path ("/app0:managed/Shepherd.Engine.dll"): readable on the Vita
        // (pal_vita resolves the device component), not on the Mac (mac-check: the host's file layer is the Mac's), where
        // SHEPHERD_IL_DIR names the folder that really holds the DLL (vita/shepherd/mac-check sets it).
        var path = asm.Location;
        if (!File.Exists(path) && Environment.GetEnvironmentVariable("SHEPHERD_IL_DIR") is { Length: > 0 } dir && File.Exists(Path.Combine(dir, Path.GetFileName(path))))
            path = Path.Combine(dir, Path.GetFileName(path));
        var r = Scan(path, skip.Namespace, skip.Name, out int n);
        if (r == null) Log.Warn($"[diet] literal scan: could not read '{path}' as an ECMA-335 assembly -- the reflection scan is used");
        else if (Both) Log.Info($"[diet] literal scan (file): {n} method bodies, {r.Count} matches");
        return r;
    }

    /// <summary>SHEPHERD_DIET_SCAN=both: the file scan and the reflection scan found the same strings (as sets)?</summary>
    public static void Compare(List<string> file, List<string> refl)
    {
        var a = new HashSet<string>(file, StringComparer.Ordinal); var b = new HashSet<string>(refl, StringComparer.Ordinal);
        var onlyFile = a.Where(s => !b.Contains(s)).ToList(); var onlyRefl = b.Where(s => !a.Contains(s)).ToList();
        string Q(IEnumerable<string> xs) => string.Join(" | ", xs.Take(8).Select(s => s.Length > 40 ? s[..40] + "..." : s));
        if (onlyFile.Count == 0 && onlyRefl.Count == 0) Log.Info($"[diet] literal scan check: file and reflection agree ({a.Count} distinct strings; matches {file.Count} vs {refl.Count})");
        else Log.Warn($"[diet] literal scan check: MISMATCH -- only in the file scan {onlyFile.Count} ({Q(onlyFile)}), only in the reflection scan {onlyRefl.Count} ({Q(onlyRefl)})");
    }

    /// <summary>Every user string the scan finds in <paramref name="file"/>'s methods (types marked with the attribute
    /// <paramref name="skipNs"/>.<paramref name="skipName"/>, and the types nested in them, skipped); null if the file does not read
    /// as expected.</summary>
    public static List<string> Scan(string file, string skipNs, string skipName, out int methods)
    {
        methods = 0;
        try { return Read(File.ReadAllBytes(file), skipNs, skipName, out methods); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or IndexOutOfRangeException or ArgumentException) { return null; }
    }

    static int U16(byte[] b, int o) => b[o] | b[o + 1] << 8;
    static int I32(byte[] b, int o) => b[o] | b[o + 1] << 8 | b[o + 2] << 16 | b[o + 3] << 24;

    // ECMA-335 II.22: the column layout of every table of an assembly's metadata (0x00..0x2C). A column is a fixed width (1, 2, 4),
    // a heap index (S = #Strings, G = #GUID, B = #Blob), a table index (T + table) or a coded index (C + kind).
    const int S = -1, G = -2, B = -3;
    static int T(int table) => 100 + table;
    static int C(int kind) => 200 + kind;
    enum K { TypeDefOrRef, HasConstant, HasCustomAttribute, HasFieldMarshal, HasDeclSecurity, MemberRefParent, HasSemantics, MethodDefOrRef, MemberForwarded, Implementation, CustomAttributeType, ResolutionScope, TypeOrMethodDef }
    static readonly (int bits, int[] tables)[] Coded =
    {
        (2, new[] { 0x02, 0x01, 0x1B }),
        (2, new[] { 0x04, 0x08, 0x17 }),
        (5, new[] { 0x06, 0x04, 0x01, 0x02, 0x08, 0x09, 0x0A, 0x00, 0x0E, 0x17, 0x14, 0x11, 0x1A, 0x1B, 0x20, 0x23, 0x26, 0x27, 0x28, 0x2A, 0x2C, 0x2B }),
        (1, new[] { 0x04, 0x08 }),
        (2, new[] { 0x02, 0x06, 0x20 }),
        (3, new[] { 0x02, 0x01, 0x1A, 0x06, 0x1B }),
        (1, new[] { 0x14, 0x17 }),
        (1, new[] { 0x06, 0x0A }),
        (1, new[] { 0x04, 0x06 }),
        (2, new[] { 0x26, 0x23, 0x27 }),
        (3, new[] { 0x06, 0x0A }),
        (2, new[] { 0x00, 0x1A, 0x23, 0x01 }),
        (1, new[] { 0x02, 0x06 }),
    };
    static readonly int[][] Cols =
    {
        /*00 Module*/ new[] { 2, S, G, G, G },
        /*01 TypeRef*/ new[] { C((int)K.ResolutionScope), S, S },
        /*02 TypeDef*/ new[] { 4, S, S, C((int)K.TypeDefOrRef), T(0x04), T(0x06) },
        /*03 FieldPtr*/ new[] { T(0x04) },
        /*04 Field*/ new[] { 2, S, B },
        /*05 MethodPtr*/ new[] { T(0x06) },
        /*06 MethodDef*/ new[] { 4, 2, 2, S, B, T(0x08) },
        /*07 ParamPtr*/ new[] { T(0x08) },
        /*08 Param*/ new[] { 2, 2, S },
        /*09 InterfaceImpl*/ new[] { T(0x02), C((int)K.TypeDefOrRef) },
        /*0A MemberRef*/ new[] { C((int)K.MemberRefParent), S, B },
        /*0B Constant*/ new[] { 2, C((int)K.HasConstant), B },
        /*0C CustomAttribute*/ new[] { C((int)K.HasCustomAttribute), C((int)K.CustomAttributeType), B },
        /*0D FieldMarshal*/ new[] { C((int)K.HasFieldMarshal), B },
        /*0E DeclSecurity*/ new[] { 2, C((int)K.HasDeclSecurity), B },
        /*0F ClassLayout*/ new[] { 2, 4, T(0x02) },
        /*10 FieldLayout*/ new[] { 4, T(0x04) },
        /*11 StandAloneSig*/ new[] { B },
        /*12 EventMap*/ new[] { T(0x02), T(0x14) },
        /*13 EventPtr*/ new[] { T(0x14) },
        /*14 Event*/ new[] { 2, S, C((int)K.TypeDefOrRef) },
        /*15 PropertyMap*/ new[] { T(0x02), T(0x17) },
        /*16 PropertyPtr*/ new[] { T(0x17) },
        /*17 Property*/ new[] { 2, S, B },
        /*18 MethodSemantics*/ new[] { 2, T(0x06), C((int)K.HasSemantics) },
        /*19 MethodImpl*/ new[] { T(0x02), C((int)K.MethodDefOrRef), C((int)K.MethodDefOrRef) },
        /*1A ModuleRef*/ new[] { S },
        /*1B TypeSpec*/ new[] { B },
        /*1C ImplMap*/ new[] { 2, C((int)K.MemberForwarded), S, T(0x1A) },
        /*1D FieldRVA*/ new[] { 4, T(0x04) },
        /*1E EncLog*/ new[] { 4, 4 },
        /*1F EncMap*/ new[] { 4 },
        /*20 Assembly*/ new[] { 4, 2, 2, 2, 2, 4, B, S, S },
        /*21 AssemblyProcessor*/ new[] { 4 },
        /*22 AssemblyOS*/ new[] { 4, 4, 4 },
        /*23 AssemblyRef*/ new[] { 2, 2, 2, 2, 4, B, S, S, B },
        /*24 AssemblyRefProcessor*/ new[] { 4, T(0x23) },
        /*25 AssemblyRefOS*/ new[] { 4, 4, 4, T(0x23) },
        /*26 File*/ new[] { 4, S, B },
        /*27 ExportedType*/ new[] { 4, 4, S, S, C((int)K.Implementation) },
        /*28 ManifestResource*/ new[] { 4, 4, S, C((int)K.Implementation) },
        /*29 NestedClass*/ new[] { T(0x02), T(0x02) },
        /*2A GenericParam*/ new[] { 2, 2, C((int)K.TypeOrMethodDef), S },
        /*2B MethodSpec*/ new[] { C((int)K.MethodDefOrRef), B },
        /*2C GenericParamConstraint*/ new[] { T(0x2A), C((int)K.TypeDefOrRef) },
    };

    static List<string> Read(byte[] f, string skipNs, string skipName, out int methods)
    {
        methods = 0;
        // ---- PE: the sections (RVA -> file offset) and the CLI header (data directory 14)
        int pe = I32(f, 0x3C);
        if (I32(f, pe) != 0x00004550) return null;
        int coff = pe + 4, nsec = U16(f, coff + 2), optSize = U16(f, coff + 16), opt = coff + 20, magic = U16(f, opt);
        if (magic != 0x10B && magic != 0x20B) return null;
        int dd = opt + (magic == 0x20B ? 112 : 96), sec = opt + optSize;
        int Off(int rva)
        {
            for (int i = 0; i < nsec; i++)
            {
                int s = sec + 40 * i, vsize = I32(f, s + 8), va = I32(f, s + 12), rawSize = I32(f, s + 16), raw = I32(f, s + 20);
                if (rva >= va && rva < va + Math.Max(vsize, rawSize)) { int o = raw + (rva - va); return o >= 0 && o < f.Length ? o : -1; }
            }
            return -1;
        }
        int cli = Off(I32(f, dd + 14 * 8)); if (cli < 0) return null;
        int md = Off(I32(f, cli + 8)); if (md < 0 || I32(f, md) != 0x424A5342) return null;
        // ---- metadata root: the streams
        int p = md + 16 + I32(f, md + 12), nstreams = U16(f, p + 2); p += 4;
        int tbl = -1, str = -1, us = -1, usSize = 0;
        for (int i = 0; i < nstreams; i++)
        {
            int off = I32(f, p), size = I32(f, p + 4), n = p + 8, e = n; while (f[e] != 0) e++;
            var name = Encoding.ASCII.GetString(f, n, e - n);
            if (name == "#~") tbl = md + off; else if (name == "#Strings") str = md + off; else if (name == "#US") { us = md + off; usSize = size; }
            p = n + ((e - n + 1 + 3) & ~3);
        }
        if (tbl < 0 || str < 0 || us < 0) return null;
        // ---- the table stream: row counts, index widths, where each table starts
        int heaps = f[tbl + 6];
        long valid = (long)(uint)I32(f, tbl + 8) | (long)I32(f, tbl + 12) << 32;
        if ((valid >> Cols.Length) != 0) return null;   // a table this reader does not know (an edit-and-continue or PDB image)
        var rows = new int[Cols.Length]; p = tbl + 24;
        for (int t = 0; t < Cols.Length; t++) if ((valid >> t & 1) != 0) { rows[t] = I32(f, p); p += 4; }
        if ((heaps & 0x40) != 0) p += 4;
        int strW = (heaps & 1) != 0 ? 4 : 2, guidW = (heaps & 2) != 0 ? 4 : 2, blobW = (heaps & 4) != 0 ? 4 : 2;
        int Width(int col)
        {
            if (col > 0 && col < 100) return col;
            if (col == S) return strW; if (col == G) return guidW; if (col == B) return blobW;
            if (col < 200) return rows[col - 100] < 65536 ? 2 : 4;
            var (bits, tables) = Coded[col - 200]; int max = 0; foreach (var t in tables) max = Math.Max(max, rows[t]);
            return max < (1 << (16 - bits)) ? 2 : 4;
        }
        var start = new int[Cols.Length]; var rowSize = new int[Cols.Length]; var colOff = new int[Cols.Length][];
        for (int t = 0; t < Cols.Length; t++)
        {
            start[t] = p; colOff[t] = new int[Cols[t].Length];
            for (int c = 0; c < Cols[t].Length; c++) { colOff[t][c] = rowSize[t]; rowSize[t] += Width(Cols[t][c]); }
            p += rowSize[t] * rows[t];
        }
        int Cell(int t, int row, int c)   // row is 1-based
        {
            int o = start[t] + (row - 1) * rowSize[t] + colOff[t][c];
            return Width(Cols[t][c]) == 2 ? U16(f, o) : I32(f, o);
        }
        string Str(int idx) { int o = str + idx, e = o; while (f[e] != 0) e++; return Encoding.UTF8.GetString(f, o, e - o); }

        // ---- types: method ranges, the skip attribute's constructors, the types marked with it, nesting
        int ntypes = rows[0x02], nmethods = rows[0x06];
        int First(int type) => Cell(0x02, type, 5);
        int End(int type) => type < ntypes ? First(type + 1) : nmethods + 1;
        int attrType = 0;
        for (int t = 1; t <= ntypes && attrType == 0; t++) if (Str(Cell(0x02, t, 1)) == skipName && Str(Cell(0x02, t, 2)) == skipNs) attrType = t;
        var marked = new bool[ntypes + 1];
        if (attrType != 0)
            for (int r = 1; r <= rows[0x0C]; r++)
            {
                int parent = Cell(0x0C, r, 0), ctor = Cell(0x0C, r, 1);
                if ((parent & 31) != 3) continue;                    // HasCustomAttribute tag 3 = TypeDef
                int tag = ctor & 7, idx = ctor >> 3; bool ours = false;
                if (tag == 2) ours = idx >= First(attrType) && idx < End(attrType);              // CustomAttributeType 2 = MethodDef
                else if (tag == 3) { int cls = Cell(0x0A, idx, 0); ours = (cls & 7) == 0 && cls >> 3 == attrType; }   // 3 = MemberRef on the TypeDef
                if (ours) marked[parent >> 5] = true;
            }
        var enclosing = new int[ntypes + 1];
        for (int r = 1; r <= rows[0x29]; r++) enclosing[Cell(0x29, r, 0)] = Cell(0x29, r, 1);
        bool Skipped(int t) { for (int guard = 0; t != 0 && guard < 64; t = enclosing[t], guard++) if (marked[t]) return true; return false; }

        // ---- the scan
        var found = new List<string>();
        for (int t = 2; t <= ntypes; t++)   // row 1 is the module type (Assembly.GetTypes does not return it)
        {
            if (Skipped(t)) continue;
            for (int m = First(t), e = End(t); m < e; m++)
            {
                int rva = Cell(0x06, m, 0); if (rva == 0) continue;   // abstract, extern, runtime-implemented: no body
                int b = Off(rva); if (b < 0) return null;
                int code, size;
                if ((f[b] & 3) == 2) { code = b + 1; size = f[b] >> 2; }                  // tiny header
                else if ((f[b] & 3) == 3) { code = b + 4 * (f[b + 1] >> 4); size = I32(f, b + 4); }   // fat header
                else return null;
                if (size < 0 || code + size > f.Length) return null;
                methods++;
                for (int i = 0; i + 4 < size; i++)
                    if (f[code + i] == 0x72 && f[code + i + 4] == 0x70 && UserString(f, us, usSize, f[code + i + 1] | f[code + i + 2] << 8 | f[code + i + 3] << 16) is { } s)
                        found.Add(s);
            }
        }
        return found;
    }

    /// <summary>Mono's ResolveString for a user-string token's offset: 0 or past the heap is refused; otherwise the blob there.</summary>
    static string UserString(byte[] f, int us, int usSize, int off)
    {
        if (off <= 0 || off >= usSize) return null;
        int o = us + off, b0 = f[o], len, hdr;
        if ((b0 & 0x80) == 0) { len = b0; hdr = 1; }
        else if ((b0 & 0xC0) == 0x80) { len = (b0 & 0x3F) << 8 | f[o + 1]; hdr = 2; }
        else if ((b0 & 0xE0) == 0xC0) { len = (b0 & 0x1F) << 24 | f[o + 1] << 16 | f[o + 2] << 8 | f[o + 3]; hdr = 4; }
        else return null;
        int chars = len >> 1;
        if (o + hdr + 2 * chars > f.Length) return null;
        return Encoding.Unicode.GetString(f, o + hdr, 2 * chars);
    }
}
