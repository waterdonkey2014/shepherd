using System.Buffers.Binary;
using System.Text;
using Shepherd.Assets;
using Shepherd.FmodBake;

namespace Shepherd.VitaPack;

/// <summary>V6 stage 1 (vita/notes/engine-pass-memory.md): data-only trims of what the Vita pack carries. Nothing here changes a
/// game DLL; the engine reads the trimmed records exactly as it read the originals.
///   --language &lt;code&gt; (default en; "all" keeps every language, for comparison): the I2 localization sources keep that one language (every term's Languages / Flags /
///       Languages_Touch arrays and the source's mLanguages list), and the TMP font atlases of the other scripts' fonts
///       (CJK, Korean, Arabic) are replaced by a 4x4 transparent stub image (the font assets, their glyph tables and every
///       reference stay, so the game's font set-up is unchanged; it simply never draws them in that language)
///   joystick maps (default on; --keep-joystick-maps turns it off): Rewired's ControllerDataFiles keeps its default map, its
///       templates and the hardware map of the Vita's own pad (polish round 1: the engine shows the pad to Rewired as a Unity-input
///       DualShock 4, Impl.VitaPad, which Rewired identifies through that map's macOS fallback entry: <see cref="VitaPadMap"/>);
///       its hardwareJoystickMaps array keeps only that entry, and the other 333 HardwareJoystickMap assets are left out
///       (recorded as absent: a stray reference resolves to null, silently).
///   FMOD bank key (always): FMODUnity.Settings keeps every field but an empty EncryptionKey (TrimFmodSettings; KeyScan proves it).
/// Stripped MonoBehaviours are rewritten in Unity's binary layout (the layout the engine's reflection reader follows); every
/// rewrite first re-serializes the parsed record without changes and must reproduce the original bytes exactly, else the
/// record is kept as it is (with a WARNING in the report).</summary>
sealed partial class Baker
{
    public readonly SortedDictionary<string, string> Properties = new(StringComparer.Ordinal);
    public readonly List<string> TrimReport = new();
    int _jsExcluded; long _jsBytes;

    // ------------------------------------------------------------------ script identity of MonoBehaviours

    readonly Dictionary<(SerializedFile, long), string> _scriptOf = new();
    string ScriptOf(SerializedFile f, ObjectInfo o)
    {
        if (o.ClassId != ClassIds.MonoBehaviour) return null;
        if (_scriptOf.TryGetValue((f, o.PathId), out var s)) return s;
        try { s = _db.GetMonoScript(f, o)?.FullName; } catch { s = null; }
        return _scriptOf[(f, o.PathId)] = s;
    }

    const string JoystickMapScript = "Rewired.Data.Mapping.HardwareJoystickMap", ControllerDataScript = "Rewired.Data.ControllerDataFiles";
    static bool IsI2Source(string script) => script is "I2.Loc.LanguageSourceAsset" or "I2.Loc.LanguageSource";

    // the default HardwareJoystickMap (kept: ControllerDataFiles logs "missing critical data" without it) and the Vita pad's map
    readonly HashSet<(SerializedFile, long)> _keepJoystickMaps = new();
    /// <summary>The HardwareJoystickMap (by its controllerName, the first field after the MonoBehaviour header) that identifies the
    /// engine's virtual Vita pad (Shepherd.Engine Impl.VitaPad.JoystickName matches its fallback_OSX product names).</summary>
    public const string VitaPadMap = "Sony DualShock 4";
    readonly HashSet<(SerializedFile, long)> _vitaPadMaps = new();
    string JoystickMapName(SerializedFile f, ObjectInfo o)
    {
        try
        {
            if (f.HasTree(o)) return TypeTreeReader.Read(f, o)["controllerName"] as string;
            var c = new Cur(f.GetObjectBytes(o).ToArray()); HeaderLength(c); return c.Str();
        }
        catch { return null; }
    }

    /// <summary>Before the recorded set is added: find ControllerDataFiles and the map it names as its default.</summary>
    void PrepareTrims(IEnumerable<(SerializedFile f, ObjectInfo o)> recorded)
    {
        Properties["language"] = Program.Language;
        if (!Program.KeepJoystickMaps) Properties["joystick-maps"] = "excluded";
        if (Program.KeepJoystickMaps) return;
        foreach (var (f, o) in recorded)
        {
            if (ScriptOf(f, o) != ControllerDataScript) continue;
            PPtr def;
            if (f.HasTree(o) && TypeTreeReader.Read(f, o)["defaultHardwareJoystickMap"] is PPtr tp) def = tp;
            else if (ControllerData.Parse(f.GetObjectBytes(o).ToArray()) is { } cd) def = cd.Default;
            else { TrimReport.Add($"  WARNING {f.Name} #{o.PathId}: ControllerDataFiles not readable: joystick maps kept"); Program.KeepJoystickMaps = true; Properties.Remove("joystick-maps"); return; }
            var (rf, ro) = _db.Resolve(f, def);
            if (ro != null) _keepJoystickMaps.Add((rf, ro.PathId));
            TrimReport.Add($"  {f.Name} #{o.PathId}: ControllerDataFiles; its default map {(ro != null ? $"{rf.Name} #{ro.PathId}" : "(none)")} is kept");
            var maps = f.HasTree(o) ? (TypeTreeReader.Read(f, o)["hardwareJoystickMaps"] as List<object>)?.OfType<PPtr>().ToList()
                : ControllerData.Parse(f.GetObjectBytes(o).ToArray())?.Maps.Select(PPtrOf).ToList();
            foreach (var mp in maps ?? new List<PPtr>())
            {
                var (mf, mo) = _db.Resolve(f, mp);
                if (mo == null || JoystickMapName(mf, mo) != VitaPadMap) continue;
                _keepJoystickMaps.Add((mf, mo.PathId)); _vitaPadMaps.Add((mf, mo.PathId));
                TrimReport.Add($"  {f.Name} #{o.PathId}: the Vita pad's hardware map '{VitaPadMap}' ({mf.Name} #{mo.PathId}) is kept");
            }
        }
    }

    /// <summary>Objects the pack leaves out on purpose (recorded as absent: references to them resolve to null, silently).</summary>
    bool Excluded(SerializedFile f, ObjectInfo o)
    {
        if (Program.KeepJoystickMaps || o.ClassId != ClassIds.MonoBehaviour) return false;
        if (ScriptOf(f, o) != JoystickMapScript || _keepJoystickMaps.Contains((f, o.PathId))) return false;
        if (_absentObjects.Add((f.Name, o.PathId))) { _jsExcluded++; _jsBytes += o.ByteSize; }
        return true;
    }

    /// <summary>After Build: rewrite the records the trims touch.</summary>
    void TrimEntry(Entry e)
    {
        if (e.Obj.ClassId != ClassIds.MonoBehaviour) return;
        var script = ScriptOf(e.File, e.Obj);
        if (script == ControllerDataScript && !Program.KeepJoystickMaps && e.Tree != null)
        {
            if (e.Tree["hardwareJoystickMaps"] is not List<object> maps) { TrimReport.Add($"  WARNING {e}: ControllerDataFiles tree has no hardwareJoystickMaps list"); return; }
            var keep = maps.Where(m => m is PPtr p && KeptPadMap(e.File, p)).ToList();
            int n = maps.Count; e.Tree.Set("hardwareJoystickMaps", keep);
            TrimReport.Add($"  {e}: ControllerDataFiles (type tree) hardwareJoystickMaps {n} -> {keep.Count} (default map and {(e.Tree["joystickTemplates"] as List<object>)?.Count} templates kept)");
        }
        else if (script == ControllerDataScript && !Program.KeepJoystickMaps && e.Raw != null)
        {
            var cd = ControllerData.Parse(e.Raw);
            if (cd == null || !cd.Write().AsSpan().SequenceEqual(e.Raw)) { TrimReport.Add($"  WARNING {e}: ControllerDataFiles layout not recognised (round trip failed): kept as it is"); return; }
            int n = cd.Maps.Count; cd.Maps.RemoveAll(m => !KeptPadMap(e.File, PPtrOf(m))); var w = cd.Write();
            TrimReport.Add($"  {e}: ControllerDataFiles hardwareJoystickMaps {n} -> {cd.Maps.Count} (default map and {cd.Templates.Count} templates kept); record {e.Raw.Length} -> {w.Length} B");
            e.Raw = w;
        }
        else if (IsI2Source(script) && Program.Language != "all")
        {
            if (e.Raw != null) TrimI2Raw(e);
            else if (e.Tree != null) TrimI2Tree(e);
        }
        else if (script == FmodSettingsScript) TrimFmodSettings(e);
    }

    // ------------------------------------------------------------------ FMOD bank key (never in a pack)

    const string FmodSettingsScript = "FMODUnity.Settings";
    public int FmodSettingsSeen, FmodKeysBlanked, FmodSettingsUnreadable;

    /// <summary>The Vita has no FMOD runtime (the Vita profile answers FMOD with stubs, vita/notes/v4-profile.md) and needs no
    /// bank key: the baked FMODUnity.Settings record ("FMODStudioSettings") gets an EMPTY EncryptionKey. The record keeps its
    /// layout, so the engine's reflection reader still parses it; with an empty key RuntimeManager skips setAdvancedSettings
    /// (decomp/FMODUnity/FMODUnity/RuntimeManager.cs). The key is never printed or logged here either (no length, no value).
    /// A record the baker cannot place the field in fails the bake (exit code 1); the post-bake key scan backs this up.</summary>
    void TrimFmodSettings(Entry e)
    {
        FmodSettingsSeen++;
        if (e.Tree != null && e.Tree.Has("EncryptionKey"))
        {
            if (e.Tree["EncryptionKey"] is string k && k.Length > 0) { e.Tree.Set("EncryptionKey", ""); FmodKeysBlanked++; TrimReport.Add($"  {e} '{FmodSettingsScript}': EncryptionKey blanked (type tree)"); }
            else TrimReport.Add($"  {e} '{FmodSettingsScript}': EncryptionKey already empty (type tree)");
        }
        if (e.Raw != null)
        {
            if (!GameKey.Locate(e.Raw, out int lenPos, out int end, out bool empty))
            { FmodSettingsUnreadable++; TrimReport.Add($"  ERROR {e}: FMODUnity.Settings layout not recognised: cannot blank its EncryptionKey"); Environment.ExitCode = 1; return; }
            if (empty) { TrimReport.Add($"  {e} '{FmodSettingsScript}': EncryptionKey already empty (raw)"); return; }
            // [0, lenPos) verbatim + int32 0 (an empty, already aligned string) + [end, ..) verbatim: every later field keeps its bytes
            var w = new byte[lenPos + 4 + (e.Raw.Length - end)];
            e.Raw.AsSpan(0, lenPos).CopyTo(w);
            e.Raw.AsSpan(end).CopyTo(w.AsSpan(lenPos + 4));
            if (!GameKey.Locate(w, out int lp2, out int end2, out bool empty2) || !empty2 || lp2 != lenPos || end2 != lenPos + 4 || !w.AsSpan(end2).SequenceEqual(e.Raw.AsSpan(end)))
            { FmodSettingsUnreadable++; TrimReport.Add($"  ERROR {e}: the blanked FMODUnity.Settings record does not re-validate"); Environment.ExitCode = 1; return; }
            e.Raw = w; FmodKeysBlanked++;
            TrimReport.Add($"  {e} '{FmodSettingsScript}': EncryptionKey blanked (raw layout; every other field byte-identical)");
        }
        else if (e.Tree == null) { FmodSettingsUnreadable++; TrimReport.Add($"  ERROR {e}: FMODUnity.Settings has neither a tree nor raw bytes"); Environment.ExitCode = 1; }
    }

    // ------------------------------------------------------------------ I2 (stripped: binary layout)

    void TrimI2Raw(Entry e)
    {
        var src = I2Source.Parse(e.Raw);
        if (src == null || !src.Write().AsSpan().SequenceEqual(e.Raw)) { TrimReport.Add($"  WARNING {e}: I2 LanguageSource layout not recognised (round trip failed): all languages kept"); return; }
        int keep = PickLanguage(src.Languages.Select(l => (l.Name, l.Code)).ToList(), e.ToString());
        if (keep < 0) return;
        int n = src.Languages.Count, shortTerms = 0;
        foreach (var t in src.Terms)
        {
            if (keep >= t.Languages.Count) shortTerms++;
            t.Languages = new List<string> { keep < t.Languages.Count ? t.Languages[keep] : "" };
            t.Flags = new[] { keep < t.Flags.Length ? t.Flags[keep] : (byte)0 };
            if (t.Touch.Count > 0) t.Touch = new List<string> { keep < t.Touch.Count ? t.Touch[keep] : "" };
        }
        var kept = src.Languages[keep]; src.Languages.Clear(); src.Languages.Add(kept);
        var w = src.Write(); long before = e.Raw.Length; e.Raw = w;
        TrimReport.Add($"  {e} '{src.Name}': I2 {n} languages -> 1 ({kept.Name}, code {kept.Code}), {src.Terms.Count} terms ({shortTerms} had no entry for it); record {before / 1048576.0:F2} -> {w.Length / 1048576.0:F2} MiB");
    }

    void TrimI2Tree(Entry e)
    {
        if (e.Tree["mSource"] is not TypeTreeObject s || s["mLanguages"] is not List<object> langs || s["mTerms"] is not List<object> terms) return;
        int keep = PickLanguage(langs.OfType<TypeTreeObject>().Select(l => (l.Str("Name"), l.Str("Code"))).ToList(), e.ToString());
        if (keep < 0) return;
        int n = langs.Count;
        foreach (var t in terms.OfType<TypeTreeObject>())
        {
            if (t["Languages"] is List<object> ls) t.Set("Languages", new List<object> { keep < ls.Count ? ls[keep] : "" });
            if (t["Flags"] is byte[] fb) t.Set("Flags", new[] { keep < fb.Length ? fb[keep] : (byte)0 });
            else if (t["Flags"] is List<object> fl) t.Set("Flags", new List<object> { keep < fl.Count ? fl[keep] : (byte)0 });
            if (t["Languages_Touch"] is List<object> tl && tl.Count > 0) t.Set("Languages_Touch", new List<object> { keep < tl.Count ? tl[keep] : "" });
        }
        var kept = langs[keep]; langs.Clear(); langs.Add(kept);
        TrimReport.Add($"  {e}: I2 (type tree) {n} languages -> 1, {terms.Count} terms");
    }

    /// <summary>The index of the build language among a source's languages: the I2 Code (exact, then the part before '-'),
    /// then the Name. -1 (and a WARNING) when the source does not have it.</summary>
    int PickLanguage(List<(string name, string code)> langs, string what)
    {
        var want = Program.Language;
        int Find(Func<(string name, string code), bool> p) { for (int i = 0; i < langs.Count; i++) if (p(langs[i])) return i; return -1; }
        int k = Find(l => string.Equals(l.code, want, StringComparison.OrdinalIgnoreCase));
        if (k < 0) k = Find(l => string.Equals((l.code ?? "").Split('-')[0], want, StringComparison.OrdinalIgnoreCase));
        if (k < 0) k = Find(l => string.Equals(l.name, want, StringComparison.OrdinalIgnoreCase));
        if (k < 0) { TrimReport.Add($"  WARNING {what}: language '{want}' not among {string.Join(", ", langs.Select(l => $"{l.name} ({l.code})"))}: all languages kept"); return -1; }
        Properties["language-name"] = langs[k].name ?? "";
        Properties["language-code"] = langs[k].code ?? "";
        return k;
    }

    // ------------------------------------------------------------------ font atlases of other scripts

    /// <summary>TMP font assets that only one script needs (CotL's LocalizationManager.SetupFonts loads all of them into the
    /// TMP fallback list): language code prefix -> font name pattern. Fonts not listed (Lapture, FiraSans, notoSans, Font
    /// Awesome, the controller glyph fonts, OpenDyslexic) are needed by every language and always kept.</summary>
    static readonly (string lang, string pattern)[] ScriptFonts =
    {
        ("zh", "NotoSansSC"), ("zh", "NotoSansTC"), ("ko", "NotoSansKR"), ("ja", "NotoSansJP"), ("ar", "NotoSansArabic"),
    };
    public static string StubReason(string fontName)
    {
        if (Program.Language == "all") return null;
        var want = Program.Language.Split('-')[0].ToLowerInvariant();
        foreach (var (lang, pat) in ScriptFonts)
            if (fontName.Contains(pat, StringComparison.OrdinalIgnoreCase) && lang != want) return $"atlas of '{fontName}' (a {lang} font; this pack is {Program.Language})";
        return null;
    }

    // ------------------------------------------------------------------ Unity binary layout (little-endian, 4-byte aligned)

    sealed class Cur
    {
        readonly byte[] _b; public int P;
        public Cur(byte[] b) { _b = b; }
        public bool End => P == _b.Length;
        void Need(int n) { if (n < 0 || P + n > _b.Length) throw new EndOfStreamException(); }
        public void Align() => P = (P + 3) & ~3;
        public int I32() { Need(4); var v = BinaryPrimitives.ReadInt32LittleEndian(_b.AsSpan(P)); P += 4; return v; }
        public byte U8() { Need(1); return _b[P++]; }
        public byte[] Bytes(int n) { Need(n); var r = _b.AsSpan(P, n).ToArray(); P += n; return r; }
        public string Str() { int n = I32(); var s = Encoding.UTF8.GetString(Bytes(n)); Align(); return s; }
        public bool Bool() { var v = U8() != 0; Align(); return v; }
        public byte[] Pptr() => Bytes(12);
        public byte[] ByteArray() { int n = I32(); var r = Bytes(n); Align(); return r; }
        public List<string> Strs() { int n = I32(); if (n < 0 || n > 1_000_000) throw new InvalidDataException(); var l = new List<string>(n); for (int i = 0; i < n; i++) l.Add(Str()); return l; }
    }
    sealed class Out
    {
        readonly MemoryStream _m = new();
        public void Align() { while ((_m.Length & 3) != 0) _m.WriteByte(0); }
        public void I32(int v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteInt32LittleEndian(b, v); _m.Write(b); }
        public void U8(byte v) => _m.WriteByte(v);
        public void Bytes(byte[] b) => _m.Write(b);
        public void Str(string s) { var u = Encoding.UTF8.GetBytes(s ?? ""); I32(u.Length); _m.Write(u); Align(); }
        public void Bool(bool v) { U8(v ? (byte)1 : (byte)0); Align(); }
        public void ByteArray(byte[] b) { I32(b.Length); _m.Write(b); Align(); }
        public void Strs(List<string> l) { I32(l.Count); foreach (var s in l) Str(s); }
        public byte[] ToArray() => _m.ToArray();
    }

    bool KeptPadMap(SerializedFile from, PPtr p) { var (rf, ro) = _db.Resolve(from, p); return ro != null && _vitaPadMaps.Contains((rf, ro.PathId)); }
    static PPtr PPtrOf(byte[] raw) => new(BinaryPrimitives.ReadInt32LittleEndian(raw), BinaryPrimitives.ReadInt64LittleEndian(raw.AsSpan(4)));
    /// <summary>A stripped MonoBehaviour's header: m_GameObject, m_Enabled, m_Script, m_Name (kept verbatim). Its length.</summary>
    static int HeaderLength(Cur c) { int p0 = c.P; c.Pptr(); c.U8(); c.Align(); c.Pptr(); c.Str(); return c.P - p0; }

    /// <summary>Rewired.Data.ControllerDataFiles: defaultHardwareJoystickMap, hardwareJoystickMaps[], joystickTemplates[].</summary>
    sealed class ControllerData
    {
        public byte[] Head; public byte[] DefaultRaw; public PPtr Default; public List<byte[]> Maps = new(), Templates = new();
        public static ControllerData Parse(byte[] raw)
        {
            try
            {
                var c = new Cur(raw); var d = new ControllerData();
                int h = HeaderLength(c); d.Head = raw.AsSpan(0, h).ToArray();
                d.DefaultRaw = c.Pptr(); d.Default = new PPtr(BinaryPrimitives.ReadInt32LittleEndian(d.DefaultRaw), BinaryPrimitives.ReadInt64LittleEndian(d.DefaultRaw.AsSpan(4)));
                int n = c.I32(); if (n < 0 || n > 100_000) return null; for (int i = 0; i < n; i++) d.Maps.Add(c.Pptr());
                n = c.I32(); if (n < 0 || n > 100_000) return null; for (int i = 0; i < n; i++) d.Templates.Add(c.Pptr());
                return c.End ? d : null;
            }
            catch { return null; }
        }
        public byte[] Write()
        {
            var o = new Out(); o.Bytes(Head); o.Bytes(DefaultRaw);
            o.I32(Maps.Count); foreach (var m in Maps) o.Bytes(m);
            o.I32(Templates.Count); foreach (var t in Templates) o.Bytes(t);
            return o.ToArray();
        }
    }

    /// <summary>I2.Loc.LanguageSourceAsset / LanguageSource: the header, then mSource (LanguageSourceData) in declaration order
    /// (decomp/Assembly-CSharp/I2/Loc/LanguageSourceData.cs, TermData.cs, LanguageData.cs; [NonSerialized] fields skipped).</summary>
    sealed class I2Source
    {
        public sealed class Term { public string Name; public int Type; public List<string> Languages; public byte[] Flags; public List<string> Touch; }
        public sealed class Lang { public string Name, Code; public byte Flags; }
        public byte[] Head; public string Name;
        public bool UserAgreesScene, UserAgreesPlugins, GoogleLiveSync, CaseInsensitive, IgnoreDeviceLanguage;
        public List<Term> Terms = new(); public int OnMissing; public string AppName; public List<Lang> Languages = new();
        public int AllowUnload; public string GUrl, GKey, GName, GVersion; public int GFreq, GEditorFreq, GSync; public byte[] GDelay;
        public List<byte[]> Assets = new();

        public static I2Source Parse(byte[] raw)
        {
            try
            {
                var c = new Cur(raw); var s = new I2Source();
                int p0 = c.P; c.Pptr(); c.U8(); c.Align(); c.Pptr(); s.Name = c.Str(); s.Head = raw.AsSpan(p0, c.P - p0).ToArray();
                s.UserAgreesScene = c.Bool(); s.UserAgreesPlugins = c.Bool(); s.GoogleLiveSync = c.Bool();
                int n = c.I32(); if (n < 0 || n > 1_000_000) return null;
                for (int i = 0; i < n; i++) s.Terms.Add(new Term { Name = c.Str(), Type = c.I32(), Languages = c.Strs(), Flags = c.ByteArray(), Touch = c.Strs() });
                s.CaseInsensitive = c.Bool(); s.OnMissing = c.I32(); s.AppName = c.Str();
                n = c.I32(); if (n < 0 || n > 1000) return null;
                for (int i = 0; i < n; i++) { var l = new Lang { Name = c.Str(), Code = c.Str(), Flags = c.U8() }; c.Align(); s.Languages.Add(l); }
                s.IgnoreDeviceLanguage = c.Bool(); s.AllowUnload = c.I32();
                s.GUrl = c.Str(); s.GKey = c.Str(); s.GName = c.Str(); s.GVersion = c.Str();
                s.GFreq = c.I32(); s.GEditorFreq = c.I32(); s.GSync = c.I32(); s.GDelay = c.Bytes(4);
                n = c.I32(); if (n < 0 || n > 1_000_000) return null; for (int i = 0; i < n; i++) s.Assets.Add(c.Pptr());
                return c.End ? s : null;
            }
            catch { return null; }
        }
        public byte[] Write()
        {
            var o = new Out(); o.Bytes(Head);
            o.Bool(UserAgreesScene); o.Bool(UserAgreesPlugins); o.Bool(GoogleLiveSync);
            o.I32(Terms.Count);
            foreach (var t in Terms) { o.Str(t.Name); o.I32(t.Type); o.Strs(t.Languages); o.ByteArray(t.Flags); o.Strs(t.Touch); }
            o.Bool(CaseInsensitive); o.I32(OnMissing); o.Str(AppName);
            o.I32(Languages.Count); foreach (var l in Languages) { o.Str(l.Name); o.Str(l.Code); o.U8(l.Flags); o.Align(); }
            o.Bool(IgnoreDeviceLanguage); o.I32(AllowUnload);
            o.Str(GUrl); o.Str(GKey); o.Str(GName); o.Str(GVersion);
            o.I32(GFreq); o.I32(GEditorFreq); o.I32(GSync); o.Bytes(GDelay);
            o.I32(Assets.Count); foreach (var a in Assets) o.Bytes(a);
            return o.ToArray();
        }
    }

    void ReportTrims()
    {
        Say($"trims: language {Program.Language}{(Properties.TryGetValue("language-name", out var ln) ? $" (I2 '{ln}', code {Properties["language-code"]})" : "")}; " +
            $"joystick maps {(Program.KeepJoystickMaps ? "kept" : $"excluded: {_jsExcluded} HardwareJoystickMap assets ({_jsBytes / 1048576.0:F2} MiB of source records); the Vita pad's '{VitaPadMap}' kept: {_vitaPadMaps.Count}")}; " +
            $"FMODUnity.Settings records {FmodSettingsSeen}, bank key blanked in {FmodKeysBlanked}{(FmodSettingsUnreadable > 0 ? $", {FmodSettingsUnreadable} NOT READABLE" : "")}; " +
            $"font atlases stubbed: {_tex.Count(t => t.Stub != null)} ({_tex.Where(t => t.Stub != null).Sum(t => t.SourceBytesIfExact) / 1048576.0:F2} MiB of exact atlas images not carried); " +
            $"pack properties: {string.Join(", ", Properties.Select(kv => kv.Key + "=" + kv.Value))}");
        foreach (var l in TrimReport) Say(l);
        foreach (var t in _tex.Where(t => t.Stub != null))
        {
            bool drawn = _rec.TexUse.TryGetValue((t.E.File.Name, t.E.Obj.PathId), out var use);
            Say($"  stub image: '{t.Name}' {(TextureFormat)t.Fmt} {t.W}x{t.H}: {t.Stub}{(drawn ? $" -- WARNING: the recorded boot used it ({string.Join("/", use)})" : "")}");
        }
    }
}
