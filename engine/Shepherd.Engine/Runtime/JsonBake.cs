using System.Buffers.Binary;
using System.Collections;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Shepherd.Engine.Native;
using UnityEngine;
using UObject = UnityEngine.Object;
namespace Shepherd.Engine.Runtime;

/// <summary>Device kit #5 B-2 (track boot, vita/notes/kit5-boot.md): the game's Addressables catalog (StreamingAssets/aa/catalog.json,
/// 9,936,747 bytes) without its 19 MiB document string and without the System.Text.Json parse. Today the game's TextDataProvider reads
/// the whole file into one string (File.ReadAllText) and hands it to JsonUtility.FromJson, whose engine side (Icalls/JsonPrefs.cs)
/// parses it with JsonDocument -- System.Text.Json is interpreted on the Vita and rents 2 x 16 MiB -- and fills ContentCatalogData.
/// Those three objects are the boot's biggest, and a stale stack word keeping one of them alive is kit #4's high-memory boot.
///
/// The bake (owner-local: cache/json/catalog.jbake on the card, made from the owner's own catalog) is the document's structure as the
/// engine's FromJson walks it -- every value's kind and its place in the file (offset, length), property names, the few escaped strings
/// decoded -- stamped with the file's length and content hash (IcallManifest.ContentHash, as icalls.bin v2). It is made by the engine
/// itself after a start that parsed the catalog the normal way, and written only when a fill from it is identical to that parse's
/// result: a reflection walk over every field of the two object graphs (<see cref="Diff"/>), no warnings on either side.
/// With a bake on the card (SHEPHERD_JSONBAKE unset = on under the Vita profile):
/// - when the game's Addressables settings arrive through FromJson (just before it loads the catalog), the engine arms a one-shot
///   redirect in the System.Native shim (pal_vita.c "pure redirect"): the game's next read-only open of catalog.json reads a small
///   token file instead, so its File.ReadAllText makes a ~70-char string;
/// - FromJson gets that token: the engine reads catalog.json itself, sequentially in 256 KiB reads, hashing every byte and cutting the
///   value strings out of the bytes as they pass (no document string, no parse buffers), checks length + hash + ASCII against the bake,
///   and fills the game's object with the same Fill/Value rules as the parse (<see cref="Fill"/>, a mirror of JsonIcalls'), from the
///   bake's structure. Anything unexpected (no bake, another catalog, a damaged bake, a read error, an exception) = the engine reads
///   the real file and parses it exactly as before, with a WRN.
/// Where the shim has no redirect (the Mac sim) the bake is used on the document string the game read: still no parse.
/// SHEPHERD_JSONBAKE=0: kit #4 exactly (no file, no redirect, no extra call). =verify: every start parses as before AND fills a second
/// object from the bake, compares the two (the identity check; makes the bake when there is none). =noredirect: the bake on the
/// document string only (measurement). </summary>
[ProfileOnly]
public static class JsonBake
{
    public static readonly string Setting = (Environment.GetEnvironmentVariable("SHEPHERD_JSONBAKE") ?? "").Trim().ToLowerInvariant();
    public static readonly bool On = Profile.Vita && !(Setting is "0" or "off" or "no" or "false");
    static bool VerifyEvery => Setting == "verify";
    static bool NoRedirect => Setting is "noredirect" or "verify";
    const string CatalogType = "UnityEngine.AddressableAssets.ResourceLocators.ContentCatalogData";
    const string SettingsType = "UnityEngine.AddressableAssets.Initialization.ResourceManagerRuntimeData";
    const string TokenHead = "{\"shepherd-jsonbake\":";
    static string CatalogPath => Path.Combine(Program.GameData, "StreamingAssets", "aa", "catalog.json");
    public static string Dir => Path.Combine(Program.Root, "cache", "json");
    static string BakePath => Path.Combine(Dir, "catalog.jbake");
    static string TokenPath => Path.Combine(Dir, "catalog.token.json");

    static Bake _armed, _loaded;
    static string _armedToken;
    static int _warn0;
    static bool _redirectGone;
    static string _summary;

    // ------------------------------------------------------------------ the hooks (Icalls/JsonPrefs.cs FromJson, VitaMemory.AfterBootInit)

    /// <summary>FromJson's first step (<see cref="On"/> only). true = handled: <paramref name="target"/> holds the filled object. It may
    /// replace <paramref name="json"/> (the token -> the real document, when the bake cannot be used after all).</summary>
    public static bool Intercept(ref string json, ref object target, Type type)
    {
        string tn = type?.FullName;
        if (tn == SettingsType)
        {
#if !SHEPHERD_EBOOT
            if (Probe) ProbeRead();   // SHEPHERD_JSONBAKE_PROBE=1: Mac / SHEPHERD_ENGINE_TOOLS=1 builds only (kit #5 review)
#endif
            Arm(); return false;
        }
        if (json.Length < 4096 && json.StartsWith(TokenHead, StringComparison.Ordinal)) return FromToken(ref json, ref target, type);
        if (tn == CatalogType && json.Length >= VitaMemory.BigJsonChars)
        {
            _warn0 = Log.Warnings;
            if (!VerifyEvery && FromDocument(json, ref target, type)) return true;
        }
        return false;
    }

    /// <summary>FromJson's last step for a big document that went through the parse (<see cref="On"/> only): the timing line, then for
    /// the catalog: verify the bake (SHEPHERD_JSONBAKE=verify) or make one when there is none for this document.</summary>
    public static void AfterParse(string json, object result, Type type, long t0)
    {
        double ms = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
        bool catalog = type?.FullName == CatalogType;
        Log.Info("[jsonbake] " + type?.Name + ": a " + json.Length.ToString() + "-char document parsed (System.Text.Json) and filled in " + ms.ToString("F0") + " ms" + (catalog ? "" : " (not the catalog: never baked)"));
        if (!catalog || result == null) return;
        Dump(result, "parse");
        try
        {
            int warns = Log.Warnings - _warn0;
            var sw = Stopwatch.StartNew();
            ulong h = AsciiHash(json, out bool ascii);
            if (!ascii) { Log.Warn("[jsonbake] the catalog is not plain ASCII: not baked (the parse stays)"); return; }
            var have = _loaded ?? Bake.Load(BakePath, out _);
            if (have != null && have.DocBytes == json.Length && have.DocHash == h)
            {
                if (!VerifyEvery) { Log.Info($"[jsonbake] the bake on the card matches this catalog but was not used this start ({(_redirectGone ? "no redirect in this System.Native" : "not armed")})"); return; }
                var b2 = New(result.GetType());
                int w0 = Log.Warnings; _receivers = 0; _dry = true;
                try { Fill(b2, b2.GetType(), have.Root, 0, new StringSource(json)); } finally { _dry = false; }
                if (_receivers > 0) { Log.Warn("[jsonbake] verify: the document's types have OnAfterDeserialize: the bake is dropped"); TryDelete(BakePath); return; }
                long items = 0; string d = Diff(result, b2, "catalog", new Dictionary<object, object>(ReferenceEqualityComparer.Instance), ref items);
                if (d == null && Log.Warnings == w0) Log.Info("[jsonbake] verify: a fill from the bake is identical to the parse's (deep compare of " + items.ToString() + " fields / elements), " + sw.ElapsedMilliseconds.ToString() + " ms (SHEPHERD_JSONBAKE=verify: parsed every start)");
                else Log.Warn("[jsonbake] verify: the bake's fill DIFFERS from the parse (" + (d ?? (Log.Warnings - w0).ToString() + " warning(s)") + "): the bake is dropped");
                if (d != null || Log.Warnings != w0) TryDelete(BakePath);
                return;
            }
            if (warns > 0) { Log.Info("[jsonbake] not baked: the parse logged " + warns.ToString() + " warning(s)"); return; }
            string why = Build(json, result, h, out int nodes, out int strings, out long bytes, out long compared);
            if (why != null) Log.Warn($"[jsonbake] not baked: {why}");
            else Log.Info("[jsonbake] baked catalog.json (" + json.Length.ToString() + " bytes, hash " + h.ToString("x16") + "): " + nodes.ToString() + " nodes, " + strings.ToString() + " strings, " + (bytes / 1024.0).ToString("F1") + " KiB at cache/json/catalog.jbake; a fill from it is identical to this parse's (deep compare of " + compared.ToString() + " fields / elements); " + sw.ElapsedMilliseconds.ToString() + " ms. The next starts read the catalog from it (no document string, no parse)");
        }
        catch (Exception e) { Log.Warn("[jsonbake] after the parse: " + e.GetBaseException().GetType().Name + ": " + e.GetBaseException().Message + " (the parse's result stands)"); }
    }

    /// <summary>VitaMemory.AfterBootInit (before the first frame): a redirect the game never took is disarmed (logged).</summary>
    public static void AtFirstFrame()
    {
        if (!On) return;
        if (_armed != null)
        {
            _armed = null;
            int was = Redirect(null, null);
            Log.Warn($"[jsonbake] the catalog redirect was armed but the game did not read catalog.json before the first frame (disarmed{(was == 1 ? "" : "; it had been taken")})");
        }
        _loaded = null;
    }

    // ------------------------------------------------------------------ arming (the settings document), the token, the document

#if !SHEPHERD_EBOOT
    /// <summary>SHEPHERD_JSONBAKE_PROBE=1 (measurement only; not in the Vita eboot unless built with SHEPHERD_ENGINE_TOOLS=1): the cost of
    /// the game's own read of the catalog, File.ReadAllText, timed once when the settings arrive (an extra read; the string is dropped at once).</summary>
    static readonly bool Probe = Environment.GetEnvironmentVariable("SHEPHERD_JSONBAKE_PROBE") == "1";
    static void ProbeRead()
    {
        try { var sw = Stopwatch.StartNew(); int n = File.ReadAllText(CatalogPath).Length; Log.Info($"[jsonbake] probe: File.ReadAllText(catalog.json) = {n} chars in {sw.ElapsedMilliseconds} ms (the game's read; measurement only)"); }
        catch (Exception e) { Log.Warn($"[jsonbake] probe: {e.Message}"); }
    }
#endif
    static void Arm()
    {
        if (NoRedirect || _armed != null || _redirectGone) return;
        try
        {
            string why = null;
            var b = _loaded ??= Bake.Load(BakePath, out why);
            if (b == null) { if (why != null) Log.Warn($"[jsonbake] cache/json/catalog.jbake refused ({why}): the catalog is parsed as before (and baked again)"); return; }
            long len = new FileInfo(CatalogPath).Length;
            if (len != b.DocBytes) { Log.Info("[jsonbake] catalog.json is " + len.ToString() + " bytes, the bake was made from " + b.DocBytes.ToString() + ": parsed as before (and baked again)"); _loaded = null; return; }
            string token = TokenHead + "\"catalog.json\",\"bytes\":" + b.DocBytes.ToString(CultureInfo.InvariantCulture) + ",\"hash\":\"" + b.DocHash.ToString("x16", CultureInfo.InvariantCulture) + "\"}";
            bool have = false; try { have = File.Exists(TokenPath) && File.ReadAllText(TokenPath) == token; } catch { }
            if (!have && WriteAtomic(TokenPath, Encoding.ASCII.GetBytes(token)) is { } err) { Log.Warn($"[jsonbake] the token file could not be written ({err}): parsed as before"); return; }
            int r = Redirect(CatalogPath, TokenPath);
            if (r != 1) { if (r == -2) { _redirectGone = true; Log.Info("[jsonbake] this System.Native has no read redirect: the bake fills from the document the game reads (no parse)"); } else Log.Warn("[jsonbake] the shim refused the redirect (" + r.ToString() + "): parsed as before"); return; }
            _armed = b; _armedToken = token;
        }
        catch (Exception e) { Log.Warn($"[jsonbake] arming failed ({e.GetBaseException().Message}): parsed as before"); }
    }

    static bool FromToken(ref string json, ref object target, Type type)
    {
        var b = _armed; string token = _armedToken; _armed = null; _armedToken = null;
        int hits = RedirectHits();
        string why = null; double readMs = 0;
        var sw = Stopwatch.StartNew();
        if (b == null || json != token) why = b == null ? "a bake token arrived but no redirect was armed" : "the token is not the one armed";
        else if (type?.FullName != CatalogType) why = $"the token arrived for {type?.FullName}";
        else
        {
            try
            {
                var vals = b.ReadValues(CatalogPath, out why);
                readMs = sw.Elapsed.TotalMilliseconds;
                if (vals != null)
                {
                    object t = target;
                    if (t == null)
                    {
                        if (typeof(UObject).IsAssignableFrom(type)) throw new ArgumentException("Cannot deserialize JSON to new instances of type '" + type.Name + ".'");
                        t = New(type);
                    }
                    int w0 = Log.Warnings;
                    Fill(t, t.GetType(), b.Root, 0, new ValueSource(vals));
                    if (Log.Warnings != w0) why = "the fill logged " + (Log.Warnings - w0).ToString() + " warning(s)";
                    else
                    {
                        target = t; _loaded = null;
                        _summary = "[jsonbake] the catalog came from the bake: catalog.json read + hashed in " + readMs.ToString("F0") + " ms (" + b.Split + "; " + b.DocBytes.ToString() + " bytes, " + vals.Count(v => v != null).ToString() + " strings cut from it), filled in " + (sw.Elapsed.TotalMilliseconds - readMs).ToString("F0") + " ms; no " + b.DocBytes.ToString() + "-char document string, no System.Text.Json parse, the game's read took the token (" + hits.ToString() + " redirected open); SHEPHERD_JSONBAKE=0 parses it as in kit #4";
                        Log.Info(_summary);
                        Dump(t, "bake");
                        return true;
                    }
                }
            }
            catch (Exception e) { why = $"{e.GetBaseException().GetType().Name}: {e.GetBaseException().Message}"; }
        }
        Log.Warn($"[jsonbake] the bake was not used ({why}): catalog.json is read and parsed as before");
        TryDelete(BakePath);
        _loaded = null;
        json = File.ReadAllText(CatalogPath);   // the redirect is spent: the real document
        _warn0 = Log.Warnings;
        return false;
    }

    /// <summary>No redirect (the Mac sim, or SHEPHERD_JSONBAKE=noredirect): the game read the whole document; when the bake was made from
    /// exactly this text, fill from it with the document's own spans (no parse).</summary>
    static bool FromDocument(string json, ref object target, Type type)
    {
        try
        {
            var b = _loaded ??= Bake.Load(BakePath, out _);
            if (b == null || b.DocBytes != json.Length) return false;
            var sw = Stopwatch.StartNew();
            if (AsciiHash(json, out bool ascii) != b.DocHash || !ascii) return false;
            object t = target;
            if (t == null) { if (typeof(UObject).IsAssignableFrom(type)) return false; t = New(type); }
            int w0 = Log.Warnings;
            Fill(t, t.GetType(), b.Root, 0, new StringSource(json));
            if (Log.Warnings != w0) { Log.Warn("[jsonbake] the fill from the document's spans logged warnings: parsed as before"); return false; }
            target = t; _loaded = null;
            _summary = "[jsonbake] the catalog was filled from the bake using the " + json.Length.ToString() + "-char document the game read (no redirect in this System.Native, or SHEPHERD_JSONBAKE=noredirect): no System.Text.Json parse; hashed + filled in " + sw.ElapsedMilliseconds.ToString() + " ms";
            Log.Info(_summary);
            Dump(t, "bake from the document");
            return true;
        }
        catch (Exception e) { Log.Warn($"[jsonbake] the fill from the document failed ({e.GetBaseException().Message}): parsed as before"); return false; }
    }

    static string Build(string json, object parsed, ulong hash, out int nodes, out int strings, out long bytes, out long compared)
    {
        nodes = strings = 0; bytes = compared = 0;
        var root = Scanner.Parse(json, out string why);
        if (root == null) return $"the engine's scanner refused the document ({why})";
        var b = new Bake { DocBytes = json.Length, DocHash = hash, TypeName = parsed.GetType().FullName, Root = root };
        var mine = New(parsed.GetType());
        int w0 = Log.Warnings; _receivers = 0; _dry = true;
        try { Fill(mine, mine.GetType(), root, 0, new StringSource(json)); } finally { _dry = false; }
        if (_receivers > 0) return "the document's types have OnAfterDeserialize";
        if (Log.Warnings != w0) return "the fill from the bake logged " + (Log.Warnings - w0).ToString() + " warning(s)";
        string d = Diff(parsed, mine, "catalog", new Dictionary<object, object>(ReferenceEqualityComparer.Instance), ref compared);
        if (d != null) return $"a fill from it differs from the parse at {d}";
        var file = b.Encode(out nodes, out strings);
        bytes = file.Length;
        // the file read back as the next start will: the same structure
        var back = Bake.Decode(file, out why);
        if (back == null) return $"the bake does not read back ({why})";
        return WriteAtomic(BakePath, file);
    }

    // ------------------------------------------------------------------ the shim's redirect (vita/runtime/system-native/pal_vita.c)

    /// <summary>1 armed, 0 disarmed, -1 refused, -2 this System.Native has no redirect (the Mac).</summary>
    static unsafe int Redirect(string from, string to)
    {
        try { int was = 0; int r = Native(from, to, &was); return to == null ? was : r; }
        catch (Exception e) when (e is EntryPointNotFoundException or DllNotFoundException or MarshalDirectiveException) { _redirectGone = true; return -2; }
    }
    static int RedirectHits() { try { return NativeHits(); } catch { return -1; } }
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    static unsafe int Native(string from, string to, int* was) => shepherd_vita_engine_redirect(from, to, was);
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    static int NativeHits() => shepherd_vita_engine_redirect_hits();
    [DllImport("__Internal")] static extern unsafe int shepherd_vita_engine_redirect([MarshalAs(UnmanagedType.LPUTF8Str)] string from, [MarshalAs(UnmanagedType.LPUTF8Str)] string to, int* wasArmed);
    [DllImport("__Internal")] static extern int shepherd_vita_engine_redirect_hits();

    // ------------------------------------------------------------------ the mirror of JsonIcalls.Fill / Value (Icalls/JsonPrefs.cs)
    // Same rules, same order, same calls, over the bake's nodes instead of System.Text.Json's elements. Any document the engine bakes
    // was first filled both ways and compared field by field (Build), so a difference between the two can never reach the game.

    interface ISource { string Str(JNode n); string Raw(JNode n); }
    sealed class StringSource : ISource
    {
        readonly string _doc; public StringSource(string doc) => _doc = doc;
        public string Str(JNode n) => n.Lit ?? _doc.Substring(n.Off + 1, n.Len - 2);
        public string Raw(JNode n) => _doc.Substring(n.Off, n.Len);
    }
    sealed class ValueSource : ISource
    {
        readonly string[] _v; public ValueSource(string[] v) => _v = v;
        public string Str(JNode n) => n.Lit ?? _v[n.Id] ?? throw new InvalidOperationException("no value cut for node " + n.Id.ToString());
        public string Raw(JNode n) => n.Kind is K.Array or K.Object || n.Lit != null ? throw new InvalidOperationException("the raw text of an object / array / escaped string is not in the bake's cut")
            : n.Kind == K.String ? "\"" + Str(n) + "\"" : _v[n.Id] ?? throw new InvalidOperationException("no raw text for node " + n.Id.ToString());
    }

    static object New(Type t) => t.IsValueType ? Activator.CreateInstance(t) : t.GetConstructor(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, Type.EmptyTypes) is { } c ? c.Invoke(null) : System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(t);
    static void Fill(object target, Type t, JNode e, int depth, ISource src)
    {
        if (e.Kind != K.Object) return;
        foreach (var f in SerRules.Fields(t))
            if (e.TryGet(f.Name, out var v)) { try { f.SetValue(target, Value(v, f.FieldType, depth + 1, src, f.GetValue(target))); } catch (Exception ex) { Log.Warn("JsonUtility " + t.Name + "." + f.Name + ": " + ex.Message); } }
        if (target is ISerializationCallbackReceiver r) { if (_dry) _receivers++; else r.OnAfterDeserialize(); }
    }
    /// <summary>While a bake is made or verified the second object must not run OnAfterDeserialize (it could touch the game's state):
    /// a document whose types have one is not baked (the fill then also differs from the parse and the compare refuses it).</summary>
    static bool _dry; static int _receivers;
    static object Value(JNode v, Type t, int depth, ISource src, object existing = null)
    {
        if (depth > 32) return null;
        if (v.Kind == K.Null) return t.IsValueType ? Activator.CreateInstance(t) : null;
        if (t == typeof(string)) return v.Kind == K.String ? src.Str(v) : src.Raw(v);
        if (t == typeof(bool)) return v.Kind == K.True || (v.Kind == K.Number && Double(v, src) != 0);
        if (t.IsEnum) return Enum.ToObject(t, v.Kind == K.Number ? Int64(v, src) : 0);
        if (t.IsPrimitive) return Convert.ChangeType(v.Kind == K.Number ? Double(v, src) : v.Kind == K.String ? double.Parse(src.Str(v), CultureInfo.InvariantCulture) : 0, t, CultureInfo.InvariantCulture);
        if (typeof(UObject).IsAssignableFrom(t)) return v.Kind == K.Object && v.TryGet("instanceID", out var id) && Objects.ById.TryGetValue(Int32(id, src), out var n) ? n.Managed : null;
        if (t.IsArray)
        {
            var et = t.GetElementType(); var items = v.Kind == K.Array ? v.Items.Select(x => Value(x, et, depth + 1, src)).ToList() : new List<object>();
            var arr = Array.CreateInstance(et, items.Count); for (int i = 0; i < items.Count; i++) arr.SetValue(items[i], i); return arr;
        }
        if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(List<>))
        {
            var et = t.GetGenericArguments()[0]; var list = (IList)Activator.CreateInstance(t);
            if (v.Kind == K.Array) foreach (var x in v.Items) list.Add(Value(x, et, depth + 1, src));
            return list;
        }
        var obj = existing != null && !t.IsValueType ? existing : New(t);
        if (t == typeof(Vector2)) return new Vector2(F(v, "x", src), F(v, "y", src));
        if (t == typeof(Vector3)) return new Vector3(F(v, "x", src), F(v, "y", src), F(v, "z", src));
        if (t == typeof(Vector4)) return new Vector4(F(v, "x", src), F(v, "y", src), F(v, "z", src), F(v, "w", src));
        if (t == typeof(Quaternion)) return new Quaternion(F(v, "x", src), F(v, "y", src), F(v, "z", src), F(v, "w", src));
        if (t == typeof(Color)) return new Color(F(v, "r", src), F(v, "g", src), F(v, "b", src), F(v, "a", src));
        if (t == typeof(Rect)) return new Rect(F(v, "x", src), F(v, "y", src), F(v, "width", src), F(v, "height", src));
        if (t.IsValueType) { object boxed = obj; Fill(boxed, t, v, depth, src); return boxed; }
        Fill(obj, t, v, depth, src); return obj;
    }
    static float F(JNode e, string k, ISource src) => e.TryGet(k, out var v) && v.Kind == K.Number ? Single(v, src) : 0f;
    // JsonElement.GetDouble / GetInt64 / GetInt32 / GetSingle: the number's text in full, a finite value, else FormatException
    static double Double(JNode v, ISource src) { var s = src.Raw(v); return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && double.IsFinite(d) ? d : throw new FormatException("not a finite double: " + s); }
    static float Single(JNode v, ISource src) { var s = src.Raw(v); return float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && float.IsFinite(d) ? d : throw new FormatException("not a finite float: " + s); }
    static long Int64(JNode v, ISource src) { var s = src.Raw(v); return long.TryParse(s, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var d) ? d : throw new FormatException("not an Int64: " + s); }
    static int Int32(JNode v, ISource src) { var s = src.Raw(v); return int.TryParse(s, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var d) ? d : throw new FormatException("not an Int32: " + s); }

#if SHEPHERD_EBOOT
    /// <summary>SHEPHERD_JSONBAKE_DUMP: not in the Vita eboot (kit #5 review); a SHEPHERD_ENGINE_TOOLS=1 build (or the Mac) has it.</summary>
    static void Dump(object o, string how) { }
#else
    /// <summary>SHEPHERD_JSONBAKE_DUMP=&lt;file&gt; (identity checks across runs): the catalog object the game received, one line per value --
    /// every instance field of every object, arrays element by element (strings as length + FNV-1a 64 of their chars, floats by their
    /// bits, a shared object as a back reference) -- so a parse start and a bake start can be compared with cmp.</summary>
    static void Dump(object o, string how)
    {
        var path = Environment.GetEnvironmentVariable("SHEPHERD_JSONBAKE_DUMP");
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            var sb = new StringBuilder(); var ids = new Dictionary<object, int>(ReferenceEqualityComparer.Instance); long n = 0;
            void W(object v, string p)
            {
                n++;
                if (v == null) { sb.Append(p).Append("\tnull\n"); return; }
                var t = v.GetType();
                if (v is string str) { var f = Fnv64.Start; foreach (char c in str) f.Word(c); sb.Append(p).Append("\tstring\t").Append(str.Length).Append('\t').Append(f.Value.ToString("x16")).Append('\n'); return; }
                if (v is float fl) { sb.Append(p).Append("\tfloat\t").Append(BitConverter.SingleToInt32Bits(fl)).Append('\n'); return; }
                if (v is double db) { sb.Append(p).Append("\tdouble\t").Append(BitConverter.DoubleToInt64Bits(db)).Append('\n'); return; }
                if (t.IsPrimitive || t.IsEnum || v is decimal) { sb.Append(p).Append('\t').Append(t.FullName).Append('\t').Append(Convert.ToString(v, CultureInfo.InvariantCulture)).Append('\n'); return; }
                if (v is MemberInfo || v is Delegate || v is UObject || v is Assembly || v is Module) { sb.Append(p).Append('\t').Append(t.FullName).Append("\t(reference)\n"); return; }
                if (!t.IsValueType) { if (ids.TryGetValue(v, out var id)) { sb.Append(p).Append("\t-> #").Append(id).Append('\n'); return; } ids[v] = ids.Count; }
                sb.Append(p).Append('\t').Append(t.FullName).Append(t.IsValueType ? "" : " #" + (ids.Count - 1)).Append('\n');
                if (v is Array a) { for (int i = 0; i < a.Length; i++) W(a.GetValue(i), p + "[" + i + "]"); return; }
                for (var c = t; c != null && c != typeof(object); c = c.BaseType)
                    foreach (var f in c.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                        W(f.GetValue(v), p + "." + f.Name);
            }
            W(o, "catalog");
            File.WriteAllText(path, sb.ToString());
            Log.Info($"[jsonbake] dump of the catalog object ({how}): {n} values -> {path}");
        }
        catch (Exception e) { Log.Warn($"[jsonbake] dump failed: {e.Message}"); }
    }
#endif

    /// <summary>null when the two object graphs are the same field for field (every instance field of every type, arrays element by
    /// element, strings ordinal, floats by their bits, shared references shared alike); else the path of the first difference.</summary>
    static string Diff(object a, object b, string path, Dictionary<object, object> seen, ref long items)
    {
        items++;
        if (a == null || b == null) return a == null && b == null ? null : path + ": " + (a == null ? "null" : "set") + " vs " + (b == null ? "null" : "set");
        var t = a.GetType();
        if (t != b.GetType()) return path + ": " + t.Name + " vs " + b.GetType().Name;
        if (a is string sa) return string.Equals(sa, (string)b, StringComparison.Ordinal) ? null : path + ": strings differ (" + sa.Length.ToString() + " vs " + ((string)b).Length.ToString() + " chars)";
        if (a is float fa) return BitConverter.SingleToInt32Bits(fa) == BitConverter.SingleToInt32Bits((float)b) ? null : path + ": " + fa.ToString() + " vs " + b.ToString();
        if (a is double da) return BitConverter.DoubleToInt64Bits(da) == BitConverter.DoubleToInt64Bits((double)b) ? null : path + ": " + da.ToString() + " vs " + b.ToString();
        if (t.IsPrimitive || t.IsEnum || a is decimal) return a.Equals(b) ? null : path + ": " + a.ToString() + " vs " + b.ToString();
        if (a is MemberInfo || a is Delegate || a is UObject || a is Assembly || a is Module) return ReferenceEquals(a, b) ? null : $"{path}: other {t.Name} reference";
        if (!t.IsValueType)
        {
            if (seen.TryGetValue(a, out var pb)) return ReferenceEquals(pb, b) ? null : $"{path}: a shared object is not shared alike";
            seen[a] = b;
        }
        if (a is Array aa)
        {
            var ab = (Array)b;
            if (aa.Rank != 1 || aa.Length != ab.Length) return path + ": " + aa.Length.ToString() + " vs " + ab.Length.ToString() + " elements";
            for (int i = 0; i < aa.Length; i++) if (Diff(aa.GetValue(i), ab.GetValue(i), path + "[" + i + "]", seen, ref items) is { } d) return d;
            return null;
        }
        for (var c = t; c != null && c != typeof(object); c = c.BaseType)
            foreach (var f in c.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                if (Diff(f.GetValue(a), f.GetValue(b), path + "." + f.Name, seen, ref items) is { } d) return d;
        return null;
    }

    // ------------------------------------------------------------------ the document's structure

    static class K { public const byte Null = 0, False = 1, True = 2, Number = 3, String = 4, Array = 5, Object = 6; }
    sealed class JNode
    {
        public byte Kind; public int Off, Len;   // the raw text span in the document (a string's includes its quotes)
        public string Lit;                        // a string with escapes: its decoded value (else its content is Off + 1 .. Len - 2)
        public JNode[] Items;                     // an array's elements
        public string[] Names; public JNode[] Values;   // an object's members, in document order
        public int Id;                            // pre-order index
        /// <summary>System.Text.Json's TryGetProperty: the LAST member of that name (it searches from the object's end), ordinal.</summary>
        public bool TryGet(string name, out JNode v)
        {
            if (Names != null) for (int i = Names.Length - 1; i >= 0; i--) if (string.Equals(Names[i], name, StringComparison.Ordinal)) { v = Values[i]; return true; }
            v = null; return false;
        }
    }

    /// <summary>A JSON reader that keeps every value's span (no decoding but the escaped strings). It reads what JsonIcalls' parse
    /// accepts for a document like the catalog (comments and trailing commas too); anything else is refused (no bake).</summary>
    static class Scanner
    {
        public static JNode Parse(string s, out string why)
        {
            why = null; int i = 0, id = 0;
            try
            {
                var root = Val(s, ref i, ref id, 0);
                Ws(s, ref i);
                if (i != s.Length) { why = "text after the document at " + i.ToString(); return null; }
                return root;
            }
            catch (FormatException e) { why = e.Message; return null; }
        }
        static void Ws(string s, ref int i)
        {
            while (i < s.Length)
            {
                char c = s[i];
                if (c is ' ' or '\t' or '\n' or '\r') { i++; continue; }
                if (c == '/' && i + 1 < s.Length && s[i + 1] == '/') { i += 2; while (i < s.Length && s[i] != '\n') i++; continue; }
                if (c == '/' && i + 1 < s.Length && s[i + 1] == '*') { int e = s.IndexOf("*/", i + 2, StringComparison.Ordinal); if (e < 0) throw new FormatException("unterminated comment"); i = e + 2; continue; }
                break;
            }
        }
        static JNode Val(string s, ref int i, ref int id, int depth)
        {
            if (depth > 64) throw new FormatException("deeper than 64");
            Ws(s, ref i);
            if (i >= s.Length) throw new FormatException("unexpected end");
            var n = new JNode { Off = i, Id = id++ };
            char c = s[i];
            if (c == '"') { n.Kind = K.String; n.Lit = Str(s, ref i); }
            else if (c == '{')
            {
                n.Kind = K.Object; i++; var names = new List<string>(); var vals = new List<JNode>();
                while (true)
                {
                    Ws(s, ref i);
                    if (i < s.Length && s[i] == '}') { i++; break; }
                    if (i >= s.Length || s[i] != '"') throw new FormatException("a member name expected at " + i.ToString());
                    int ns = i; string lit = Str(s, ref i); names.Add(lit ?? s.Substring(ns + 1, i - ns - 2));
                    Ws(s, ref i);
                    if (i >= s.Length || s[i] != ':') throw new FormatException("':' expected at " + i.ToString());
                    i++;
                    vals.Add(Val(s, ref i, ref id, depth + 1));
                    Ws(s, ref i);
                    if (i < s.Length && s[i] == ',') { i++; continue; }
                    if (i < s.Length && s[i] == '}') { i++; break; }
                    throw new FormatException("',' or '}' expected at " + i.ToString());
                }
                n.Names = names.ToArray(); n.Values = vals.ToArray();
            }
            else if (c == '[')
            {
                n.Kind = K.Array; i++; var items = new List<JNode>();
                while (true)
                {
                    Ws(s, ref i);
                    if (i < s.Length && s[i] == ']') { i++; break; }
                    items.Add(Val(s, ref i, ref id, depth + 1));
                    Ws(s, ref i);
                    if (i < s.Length && s[i] == ',') { i++; continue; }
                    if (i < s.Length && s[i] == ']') { i++; break; }
                    throw new FormatException("',' or ']' expected at " + i.ToString());
                }
                n.Items = items.ToArray();
            }
            else if (string.CompareOrdinal(s, i, "true", 0, 4) == 0) { n.Kind = K.True; i += 4; }
            else if (string.CompareOrdinal(s, i, "false", 0, 5) == 0) { n.Kind = K.False; i += 5; }
            else if (string.CompareOrdinal(s, i, "null", 0, 4) == 0) { n.Kind = K.Null; i += 4; }
            else if (c == '-' || (c >= '0' && c <= '9'))
            {
                n.Kind = K.Number; int st = i;
                if (s[i] == '-') i++;
                if (i < s.Length && s[i] == '0') i++; else { if (i >= s.Length || s[i] < '1' || s[i] > '9') throw BadNumber(st); while (i < s.Length && char.IsAsciiDigit(s[i])) i++; }
                if (i < s.Length && s[i] == '.') { i++; if (i >= s.Length || !char.IsAsciiDigit(s[i])) throw BadNumber(st); while (i < s.Length && char.IsAsciiDigit(s[i])) i++; }
                if (i < s.Length && (s[i] == 'e' || s[i] == 'E')) { i++; if (i < s.Length && (s[i] == '+' || s[i] == '-')) i++; if (i >= s.Length || !char.IsAsciiDigit(s[i])) throw BadNumber(st); while (i < s.Length && char.IsAsciiDigit(s[i])) i++; }
            }
            else throw new FormatException("unexpected '" + c.ToString() + "' at " + i.ToString());
            n.Len = i - n.Off;
            return n;
        }
        static FormatException BadNumber(int at) => new FormatException("bad number at " + at.ToString());
        /// <summary>A string token at s[i] == '"': moves past it; returns its decoded value when it has escapes, else null (the span is the value).</summary>
        static string Str(string s, ref int i)
        {
            int st = ++i; StringBuilder sb = null;
            while (true)
            {
                if (i >= s.Length) throw new FormatException("unterminated string");
                char c = s[i];
                if (c == '"') { i++; return sb?.ToString(); }
                if (c < 0x20) throw new FormatException("a control character in a string at " + i.ToString());
                if (c != '\\') { sb?.Append(c); i++; continue; }
                sb ??= new StringBuilder().Append(s, st, i - st);
                if (i + 1 >= s.Length) throw new FormatException("unterminated escape");
                char e = s[i + 1]; i += 2;
                switch (e)
                {
                    case '"': sb.Append('"'); break; case '\\': sb.Append('\\'); break; case '/': sb.Append('/'); break;
                    case 'b': sb.Append('\b'); break; case 'f': sb.Append('\f'); break; case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break; case 't': sb.Append('\t'); break;
                    case 'u':
                        if (i + 4 > s.Length || !ushort.TryParse(s.AsSpan(i, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var u)) throw new FormatException("bad \\u escape at " + i.ToString());
                        sb.Append((char)u); i += 4; break;
                    default: throw new FormatException("bad escape at " + (i - 2).ToString());
                }
            }
        }
    }

    /// <summary>The bake file and the value cut. Layout (little-endian): "SHPDJBAK" | u32 version | u64 document bytes | u64 document
    /// content hash | u32 + UTF-8 type name | u32 names, each u32 + UTF-8 | u32 nodes | the nodes in pre-order (u8 kind, u32 offset,
    /// u32 length; an escaped string + u32 + UTF-8 value; an array + u32 count; an object + u32 count + count x u32 name index; the
    /// children follow) | u64 checksum (IcallManifest.ContentHash of every byte before it).</summary>
    sealed class Bake
    {
        const string Magic = "SHPDJBAK"; const int Version = 1; const byte KEscaped = 0x80;
        public long DocBytes; public ulong DocHash; public string TypeName; public JNode Root; public List<JNode> Nodes;

        public byte[] Encode(out int nodes, out int strings)
        {
            var names = new List<string>(); var index = new Dictionary<string, int>(StringComparer.Ordinal);
            var ms = new MemoryStream(); int n = 0, sc = 0;
            void U32(int v) { Span<byte> t = stackalloc byte[4]; BinaryPrimitives.WriteInt32LittleEndian(t, v); ms.Write(t); }
            void U64(ulong v) { Span<byte> t = stackalloc byte[8]; BinaryPrimitives.WriteUInt64LittleEndian(t, v); ms.Write(t); }
            void Str(string s) { var b = Encoding.UTF8.GetBytes(s); U32(b.Length); ms.Write(b); }
            void Collect(JNode x) { if (x.Names != null) foreach (var nm in x.Names) if (!index.ContainsKey(nm)) { index[nm] = names.Count; names.Add(nm); } foreach (var c in (IEnumerable<JNode>)x.Items ?? x.Values ?? System.Array.Empty<JNode>()) Collect(c); }
            void Node(JNode x)
            {
                n++; if (x.Kind == K.String) sc++;
                ms.WriteByte((byte)(x.Kind | (x.Lit != null ? KEscaped : 0))); U32(x.Off); U32(x.Len);
                if (x.Lit != null) Str(x.Lit);
                if (x.Kind == K.Array) { U32(x.Items.Length); foreach (var c in x.Items) Node(c); }
                if (x.Kind == K.Object) { U32(x.Names.Length); foreach (var nm in x.Names) U32(index[nm]); foreach (var c in x.Values) Node(c); }
            }
            Collect(Root);
            int count = 0; void Count(JNode x) { count++; foreach (var c in (IEnumerable<JNode>)x.Items ?? x.Values ?? System.Array.Empty<JNode>()) Count(c); } Count(Root);
            ms.Write(Encoding.ASCII.GetBytes(Magic)); U32(Version); U64((ulong)DocBytes); U64(DocHash); Str(TypeName);
            U32(names.Count); foreach (var nm in names) Str(nm);
            U32(count); Node(Root);
            U64(IcallManifest.ContentHash(ms.GetBuffer().AsSpan(0, (int)ms.Length)));
            nodes = n; strings = sc;
            return ms.ToArray();
        }

        public static Bake Load(string path, out string why)
        {
            why = null;
            try { if (!File.Exists(path)) return null; return Decode(File.ReadAllBytes(path), out why); }
            catch (Exception e) { why = e.GetBaseException().Message; return null; }
        }
        public static Bake Decode(byte[] b, out string why)
        {
            why = null;
            if (b.Length < 8 + 4 + 16 + 8 || Encoding.ASCII.GetString(b, 0, 8) != Magic) { why = "not a bake file"; return null; }
            if (IcallManifest.ContentHash(b.AsSpan(0, b.Length - 8)) != BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(b.Length - 8))) { why = "its checksum does not match (damaged)"; return null; }
            int pos = 8, end = b.Length - 8;
            int U32() { if (pos + 4 > end) throw new FormatException("truncated"); int v = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(pos)); pos += 4; return v; }
            ulong U64() { if (pos + 8 > end) throw new FormatException("truncated"); ulong v = BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(pos)); pos += 8; return v; }
            string Str() { int n = U32(); if (n < 0 || n > end - pos) throw new FormatException("truncated string"); var s = Encoding.UTF8.GetString(b, pos, n); pos += n; return s; }
            try
            {
                if (U32() != Version) { why = "another bake version"; return null; }
                var r = new Bake { DocBytes = (long)U64(), DocHash = U64(), TypeName = Str(), Nodes = new List<JNode>() };
                int nn = U32(); if (nn < 0 || nn > 1 << 20) throw new FormatException("name count");
                var names = new string[nn]; for (int i = 0; i < nn; i++) names[i] = Str();
                int count = U32(); if (count < 1 || count > end) throw new FormatException("node count");
                int id = 0;
                JNode Node(int depth)
                {
                    if (depth > 64 || pos >= end) throw new FormatException("node tree");
                    byte k = b[pos++]; var x = new JNode { Kind = (byte)(k & 0x7F), Off = U32(), Len = U32(), Id = id++ };
                    if (x.Kind > K.Object || x.Off < 0 || x.Len < 1 || x.Off + (long)x.Len > r.DocBytes) throw new FormatException("node " + x.Id.ToString());
                    if ((k & KEscaped) != 0) { if (x.Kind != K.String) throw new FormatException("escaped non-string"); x.Lit = Str(); }
                    else if (x.Kind == K.String && x.Len < 2) throw new FormatException("string node " + x.Id.ToString());
                    r.Nodes.Add(x);
                    if (x.Kind == K.Array) { int c = U32(); if (c < 0 || c > count) throw new FormatException("array size"); x.Items = new JNode[c]; for (int i = 0; i < c; i++) x.Items[i] = Node(depth + 1); }
                    if (x.Kind == K.Object)
                    {
                        int c = U32(); if (c < 0 || c > count) throw new FormatException("object size");
                        x.Names = new string[c]; for (int i = 0; i < c; i++) { int ni = U32(); if (ni < 0 || ni >= nn) throw new FormatException("name index"); x.Names[i] = names[ni]; }
                        x.Values = new JNode[c]; for (int i = 0; i < c; i++) x.Values[i] = Node(depth + 1);
                    }
                    return x;
                }
                r.Root = Node(0);
                if (id != count || pos != end) { why = "the node tree is not what the header says (" + id.ToString() + " of " + count.ToString() + " nodes, " + (end - pos).ToString() + " bytes left)"; return null; }
                return r;
            }
            catch (FormatException e) { why = e.Message; return null; }
        }

        /// <summary>The token path's read of the document: one sequential pass in 256 KiB reads, every byte hashed, each value's text cut out
        /// as it passes (strings without escapes: their content; numbers / true / false / null: their text). null + why when the file is not
        /// the one the bake was made from (length, content hash, plain ASCII) or cannot be read.</summary>
        /// <summary>The last <see cref="ReadValues"/>' split: card reads, hashing, the rest (cutting the strings), in ms.</summary>
        public string Split = "";
        public string[] ReadValues(string path, out string why)
        {
            why = null;
            var vals = new string[Nodes.Count];
            long t0 = Stopwatch.GetTimestamp();
            using var r = new Cutter(path);
            try { return ReadValues(r, vals, out why); }
            finally
            {
                double f = 1000.0 / Stopwatch.Frequency, all = (Stopwatch.GetTimestamp() - t0) * f;
                Split = "reads " + (r.ReadTicks * f).ToString("F0") + " ms, hash " + (r.HashTicks * f).ToString("F0") + " ms, cutting the strings " + (all - (r.ReadTicks + r.HashTicks) * f).ToString("F0") + " ms";
            }
        }
        string[] ReadValues(Cutter r, string[] vals, out string why)
        {
            why = null;
            r.Next();
            foreach (var x in Nodes)
            {
                if (x.Kind is K.Array or K.Object || x.Lit != null) continue;
                long off = x.Kind == K.String ? x.Off + 1 : x.Off; int n = x.Kind == K.String ? x.Len - 2 : x.Len;
                if (n == 0) { vals[x.Id] = ""; continue; }
                while (off >= r.Start + r.Len) if (r.Eof || !r.Next()) { why = "catalog.json ends before byte " + off.ToString(); return null; }
                if (off < r.Start) { why = "the bake's values are not in file order"; return null; }
                if ((vals[x.Id] = r.Cut(off, n)) == null) { why = "catalog.json ended inside a value"; return null; }
            }
            while (!r.Eof) r.Next();
            long total = r.Start + r.Len;
            if (total != DocBytes) { why = "catalog.json is " + total.ToString() + " bytes, the bake was made from " + DocBytes.ToString(); return null; }
            var f = r.Hash; f.Word((uint)total, (uint)((ulong)total >> 32)); ulong h = f.Value;
            if (h != DocHash) { why = "catalog.json's content hash " + h.ToString("x16") + " is not the bake's " + DocHash.ToString("x16") + " (another catalog)"; return null; }
            if ((r.High & 0x80808080u) != 0) { why = "catalog.json is not plain ASCII"; return null; }
            return vals;
        }
    }

    /// <summary>The sequential read under <see cref="Bake.ReadValues"/>: 256 KiB unbuffered reads, each hashed as it arrives (IcallManifest.ContentHash's words, <see cref="Fnv64"/>;
    /// the length is mixed in by the caller) with the OR of every byte (the ASCII check), and the value strings
    /// cut from the bytes (a value across reads is copied into its string piece by piece).</summary>
    sealed unsafe class Cutter : IDisposable
    {
        const int Chunk = 256 << 10;   // a multiple of 4: only the file's last read can leave a tail of 1-3 bytes
        readonly FileStream _fs; readonly byte[] _buf = new byte[Chunk];
        public long Start; public int Len; public bool Eof; public Fnv64 Hash = Fnv64.Start; public uint High;
        public Cutter(string path) => _fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.None);
        public void Dispose() => _fs.Dispose();
        /// <summary>Stopwatch ticks spent reading (the card) and hashing, for the log line.</summary>
        public long ReadTicks, HashTicks;
        public bool Next()
        {
            Start += Len; Len = 0;
            long t0 = Stopwatch.GetTimestamp();
            while (Len < Chunk) { int r = _fs.Read(_buf, Len, Chunk - Len); if (r <= 0) { Eof = true; break; } Len += r; }
            long t1 = Stopwatch.GetTimestamp();
            var s = _buf.AsSpan(0, Len); int n4 = Len & ~3;
            Hash.Words(MemoryMarshal.Cast<byte, uint>(s[..n4]), ref High);
            for (int i = n4; i < Len; i++) { High |= s[i]; Hash.Word(s[i]); }
            ReadTicks += t1 - t0; HashTicks += Stopwatch.GetTimestamp() - t1;
            return Len > 0;
        }
        /// <summary>The n bytes at file offset off (>= Start) as a string, reading on as needed; null if the file ends first. The bytes are
        /// widened one by one (<see cref="Widen"/>): Encoding.Latin1 did the same work ~15x slower in the Vita's AOT code (vlx 1,629 ms vs
        /// 110 ms for this file's 17,305 strings; CoreLib's widening goes through vector helpers, software loops without hardware SIMD there).</summary>
        public string Cut(long off, int n)
        {
            var str = new string('\0', n);   // a fresh string (n > 0), filled below before anyone sees it
            fixed (char* p = str)
            fixed (byte* b = _buf)
            {
                int done = 0;
                while (done < n)
                {
                    long at = off + done;
                    if (at >= Start + Len && (Eof || !Next())) return null;
                    int k = (int)Math.Min(n - done, Start + Len - at);
                    Widen(b + (at - Start), p + done, k);
                    done += k;
                }
            }
            return str;
        }
        static void Widen(byte* s, char* d, int n)
        {
            int i = 0;
            for (; i + 4 <= n; i += 4) { d[i] = (char)s[i]; d[i + 1] = (char)s[i + 1]; d[i + 2] = (char)s[i + 2]; d[i + 3] = (char)s[i + 3]; }
            for (; i < n; i++) d[i] = (char)s[i];
        }
    }

    /// <summary>IcallManifest.ContentHash of the document's bytes, computed from its string: the same value when every char is ASCII (a byte
    /// each, <paramref name="ascii"/>), which is when the bake can cut values from the file by the same offsets.</summary>
    static ulong AsciiHash(string s, out bool ascii)
    {
        var f = Fnv64.Start; int n4 = s.Length & ~3; int or = 0;
        for (int i = 0; i < n4; i += 4)
        {
            int c0 = s[i], c1 = s[i + 1], c2 = s[i + 2], c3 = s[i + 3];
            or |= c0 | c1 | c2 | c3;
            f.Word((uint)(c0 | c1 << 8 | c2 << 16 | c3 << 24));
        }
        for (int i = n4; i < s.Length; i++) { or |= s[i]; f.Word(s[i]); }
        f.Word((uint)s.Length);
        ascii = or < 0x80;
        return f.Value;
    }

    static void TryDelete(string p) { try { if (File.Exists(p)) File.Delete(p); } catch { } }
    static string WriteAtomic(string path, byte[] b) => Render.GxpCache.WriteAtomic(path, b);
}
