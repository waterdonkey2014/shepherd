using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using Shepherd.Engine.Native;
using Shepherd.Engine.Runtime;
using UnityEngine;
using UObject = UnityEngine.Object;
namespace Shepherd.Engine.Impl;

/// <summary>JsonUtility with Unity's rules: the same fields Unity serializes; Unity structs as {"x":…}; UnityEngine.Object refs as {"instanceID":n}.</summary>
static class JsonIcalls
{
    [Icall("UnityEngine.JsonUtility::FromJsonInternal")] static object FromJson(object[] a)
    {
        var json = (string)a[0]; var target = a[1]; var type = (Type)a[2];
        if (string.IsNullOrEmpty(json)) return target;
        // device kit #5 B-2 (Runtime/JsonBake.cs): the Addressables catalog from its owner-local bake -- no document string, no parse
        if (JsonBake.On && JsonBake.Intercept(ref json, ref target, type)) return target;
        // Vita profile: a big document's pooled parse buffers (the Addressables catalog: 2 x 16 MiB) leave ArrayPool<byte>.Shared
        // once the document is disposed (VitaMemory.ReleasePooledBytes; vita/notes/engine-pass-speed.md §8); since device kit #2 the
        // big arrays already pooled leave it BEFORE the parse too, with one full GC (VitaMemory.BeforeBigDocument: the boot's peak)
        if (VitaMemory.Pool && json.Length >= VitaMemory.BigJsonChars) VitaMemory.BeforeBigDocument(json);
        long t0 = JsonBake.On && json.Length >= VitaMemory.BigJsonChars ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
        object r;
        try { r = FromJson(json, target, type); }
        finally { if (VitaMemory.Pool && json.Length >= VitaMemory.BigJsonChars) VitaMemory.ReleasePooledBytes(json.Length); }
        if (t0 != 0) JsonBake.AfterParse(json, r, type, t0);   // (B-2: the parse's time; for the catalog, the bake made / verified)
        return r;
    }
    static object FromJson(string json, object target, Type type)
    {
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        if (target == null)
        {
            if (typeof(UObject).IsAssignableFrom(type)) throw new ArgumentException("Cannot deserialize JSON to new instances of type '" + type.Name + ".'");
            target = New(type);
        }
        Fill(target, target.GetType(), doc.RootElement, 0);
        return target;
    }
    static object New(Type t) => t.IsValueType ? Activator.CreateInstance(t) : t.GetConstructor(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, Type.EmptyTypes) is { } c ? c.Invoke(null) : System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(t);
    static void Fill(object target, Type t, JsonElement e, int depth)
    {
        if (e.ValueKind != JsonValueKind.Object) return;
        foreach (var f in SerRules.Fields(t))
            if (e.TryGetProperty(f.Name, out var v)) { try { f.SetValue(target, Value(v, f.FieldType, depth + 1, f.GetValue(target))); } catch (Exception ex) { Log.Warn($"JsonUtility {t.Name}.{f.Name}: {ex.Message}"); } }
        if (target is ISerializationCallbackReceiver r) r.OnAfterDeserialize();
    }
    static object Value(JsonElement v, Type t, int depth, object existing = null)
    {
        if (depth > 32) return null;
        if (v.ValueKind == JsonValueKind.Null) return t.IsValueType ? Activator.CreateInstance(t) : null;
        if (t == typeof(string)) return v.ValueKind == JsonValueKind.String ? v.GetString() : v.GetRawText();
        if (t == typeof(bool)) return v.ValueKind == JsonValueKind.True || (v.ValueKind == JsonValueKind.Number && v.GetDouble() != 0);
        if (t.IsEnum) return Enum.ToObject(t, v.ValueKind == JsonValueKind.Number ? v.GetInt64() : 0);
        if (t.IsPrimitive) return Convert.ChangeType(v.ValueKind == JsonValueKind.Number ? v.GetDouble() : v.ValueKind == JsonValueKind.String ? double.Parse(v.GetString(), CultureInfo.InvariantCulture) : 0, t, CultureInfo.InvariantCulture);
        if (typeof(UObject).IsAssignableFrom(t)) return v.ValueKind == JsonValueKind.Object && v.TryGetProperty("instanceID", out var id) && Objects.ById.TryGetValue(id.GetInt32(), out var n) ? n.Managed : null;
        if (t.IsArray)
        {
            var et = t.GetElementType(); var items = v.ValueKind == JsonValueKind.Array ? v.EnumerateArray().Select(x => Value(x, et, depth + 1)).ToList() : new List<object>();
            var arr = Array.CreateInstance(et, items.Count); for (int i = 0; i < items.Count; i++) arr.SetValue(items[i], i); return arr;
        }
        if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(List<>))
        {
            var et = t.GetGenericArguments()[0]; var list = (IList)Activator.CreateInstance(t);
            if (v.ValueKind == JsonValueKind.Array) foreach (var x in v.EnumerateArray()) list.Add(Value(x, et, depth + 1));
            return list;
        }
        var obj = existing != null && !t.IsValueType ? existing : New(t);
        if (t == typeof(Vector2)) return new Vector2(F(v, "x"), F(v, "y"));
        if (t == typeof(Vector3)) return new Vector3(F(v, "x"), F(v, "y"), F(v, "z"));
        if (t == typeof(Vector4)) return new Vector4(F(v, "x"), F(v, "y"), F(v, "z"), F(v, "w"));
        if (t == typeof(Quaternion)) return new Quaternion(F(v, "x"), F(v, "y"), F(v, "z"), F(v, "w"));
        if (t == typeof(Color)) return new Color(F(v, "r"), F(v, "g"), F(v, "b"), F(v, "a"));
        if (t == typeof(Rect)) return new Rect(F(v, "x"), F(v, "y"), F(v, "width"), F(v, "height"));
        if (t.IsValueType) { object boxed = obj; Fill(boxed, t, v, depth); return boxed; }
        Fill(obj, t, v, depth); return obj;
    }
    static float F(JsonElement e, string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetSingle() : 0f;

    [Icall("UnityEngine.JsonUtility::ToJsonInternal")] static object ToJson(object[] a)
    {
        if (a[0] == null) return "";
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = (bool)a[1] })) Write(w, a[0], a[0].GetType(), 0);
        return System.Text.Encoding.UTF8.GetString(ms.ToArray());
    }
    static void Write(Utf8JsonWriter w, object o, Type t, int depth)
    {
        if (depth > 32 || o == null) { w.WriteNullValue(); return; }
        switch (o)
        {
            case string s: w.WriteStringValue(s); return;
            case bool b: w.WriteBooleanValue(b); return;
            case Enum en: w.WriteNumberValue(Convert.ToInt64(en)); return;
            case float f: w.WriteNumberValue(f); return; case double d: w.WriteNumberValue(d); return;
            case IConvertible c when o.GetType().IsPrimitive: w.WriteNumberValue(Convert.ToDouble(c, CultureInfo.InvariantCulture)); return;
            case UObject u: w.WriteStartObject(); w.WriteNumber("instanceID", u.GetInstanceID()); w.WriteEndObject(); return;
            case Vector2 v2: w.WriteStartObject(); w.WriteNumber("x", v2.x); w.WriteNumber("y", v2.y); w.WriteEndObject(); return;
            case Vector3 v3: w.WriteStartObject(); w.WriteNumber("x", v3.x); w.WriteNumber("y", v3.y); w.WriteNumber("z", v3.z); w.WriteEndObject(); return;
            case Color col: w.WriteStartObject(); w.WriteNumber("r", col.r); w.WriteNumber("g", col.g); w.WriteNumber("b", col.b); w.WriteNumber("a", col.a); w.WriteEndObject(); return;
            case IList l: w.WriteStartArray(); foreach (var x in l) Write(w, x, x?.GetType(), depth + 1); w.WriteEndArray(); return;
        }
        w.WriteStartObject();
        foreach (var f in SerRules.Fields(o.GetType())) { w.WritePropertyName(f.Name); Write(w, f.GetValue(o), f.FieldType, depth + 1); }
        w.WriteEndObject();
    }
}

/// <summary>PlayerPrefs persisted to the engine sandbox (never the real game's prefs).</summary>
static class PrefsIcalls
{
    static Dictionary<string, JsonElement> _raw; static readonly Dictionary<string, object> P = new();
    static string File_ => Path.Combine(AppIcalls.Persistent, "playerprefs.json");
    static void Load()
    {
        if (_raw != null) return; _raw = new();
        try { if (File.Exists(File_)) foreach (var p in JsonDocument.Parse(File.ReadAllText(File_)).RootElement.EnumerateObject()) P[p.Name] = p.Value.ValueKind switch { JsonValueKind.String => p.Value.GetString(), JsonValueKind.Number => p.Value.TryGetInt32(out var i) ? i : p.Value.GetSingle(), _ => null }; }
        catch (Exception e) { Log.Warn("playerprefs: " + e.Message); }
    }
    static void Save() { Directory.CreateDirectory(AppIcalls.Persistent); File.WriteAllText(File_, JsonSerializer.Serialize(P)); }
    [Icall("UnityEngine.PlayerPrefs::GetString")] static object GetS(object[] a) { Load(); return P.TryGetValue((string)a[0], out var v) && v is string s ? s : (string)a[1]; }
    [Icall("UnityEngine.PlayerPrefs::GetInt")] static object GetI(object[] a) { Load(); return P.TryGetValue((string)a[0], out var v) && v is int i ? i : (int)a[1]; }
    [Icall("UnityEngine.PlayerPrefs::GetFloat")] static object GetF(object[] a) { Load(); return P.TryGetValue((string)a[0], out var v) ? v is float f ? f : v is int i ? (float)i : (float)a[1] : (float)a[1]; }
    [Icall("UnityEngine.PlayerPrefs::TrySetSetString")] static object SetS(object[] a) { Load(); P[(string)a[0]] = (string)a[1]; return true; }
    [Icall("UnityEngine.PlayerPrefs::TrySetInt")] static object SetI(object[] a) { Load(); P[(string)a[0]] = (int)a[1]; return true; }
    [Icall("UnityEngine.PlayerPrefs::TrySetFloat")] static object SetF(object[] a) { Load(); P[(string)a[0]] = (float)a[1]; return true; }
    [Icall("UnityEngine.PlayerPrefs::HasKey")] static object Has(object[] a) { Load(); return P.ContainsKey((string)a[0]); }
    [Icall("UnityEngine.PlayerPrefs::DeleteKey")] static object Del(object[] a) { Load(); P.Remove((string)a[0]); return null; }
    [Icall("UnityEngine.PlayerPrefs::DeleteAll")] static object DelAll(object[] a) { Load(); P.Clear(); return null; }
    [Icall("UnityEngine.PlayerPrefs::Save")] static object SaveI(object[] a) { Save(); return null; }
}
