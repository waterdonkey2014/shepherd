using System.Collections;
using System.Reflection;
using System.Text;
using Shepherd.Engine.Native;
using Shepherd.Engine.Runtime;
using UnityEngine;
using UObject = UnityEngine.Object;
namespace Shepherd.Engine.Assets;

#if !SHEPHERD_EBOOT

/// <summary>Device kit #10, track load: the loader's output, written down -- every record the loader creates (scene loads, asset loads,
/// Instantiate) in creation order, with its kind, name, scene / active / enabled state and, for a script, every serialized field deep
/// (nested serializable objects, lists and arrays element by element, object references as instance ids, floats as bits, strings
/// exactly), one line per record: "seq id class name state hash" (+ the dump itself with SHEPHERD_K10_LOADDUMP_FULL=1). Two runs with
/// the same data produce the same file iff the loader made the same objects with the same field values in the same order -- the
/// identity test of the E3 rows (defaults vs SHEPHERD_K10_LOAD=0). Read only: FieldInfo.GetValue and the records' own fields (no
/// property getter, no game code). Written when the loader drains its queue at depth 0 (every record created since the last write) and
/// after every Instantiate. SHEPHERD_K10_LOADDUMP=&lt;file&gt; (measurement / test only; off by default). [ProfileOnly]: its literals stay out
/// of the diet's readable-key scan (Assets/Diet.cs), so a Mac / tools build seeds the same bags as an eboot.
///
/// Kit #10 review: (1) the engine records are described too -- a sprite's texture / rects / pivot / border / settings / geometry (what the
/// atlas index picked), a texture's size / format / source / pixels, a shader's name / queue / render state, a material's shader / queue /
/// properties / keywords, a mesh's arrays -- and a CreateShader that hands back an existing shader (the dedupe: no new record) writes an
/// "alias" line naming the record it returned, so a wrong atlas entry or shader pick makes the two dumps differ. (2) The file is capped
/// (SHEPHERD_K10_LOADDUMP_MAX_MB; default 64 on the Vita profile, no cap on the Mac) and any write failure (a full card) stops the dump with
/// a WRN line: the dump never throws into a load.</summary>
[ProfileOnly]
public static class LoadDump
{
    static readonly string File = Environment.GetEnvironmentVariable("SHEPHERD_K10_LOADDUMP") is { Length: > 0 } f ? f : null;
    public static readonly bool On = File != null;
    static readonly bool Full = Environment.GetEnvironmentVariable("SHEPHERD_K10_LOADDUMP_FULL") == "1";
    static readonly long MaxBytes = (long)((Environment.GetEnvironmentVariable("SHEPHERD_K10_LOADDUMP_MAX_MB") is { } mb && double.TryParse(mb, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var m) ? m : Profile.Vita ? 64 : 0) * 1048576);   // 0 = no cap
    static StreamWriter _w; static long _seq, _bytes; static bool _stopped;
    static readonly List<object> _pending = new();   // NObj (a created record) or Alias (an existing record handed back)
    static readonly StringBuilder _sb = new(), _line = new();
    sealed class Alias { public string File; public long PathId; public NObj Rec; }

    public static void Created(NObj n) { if (On && !_stopped) _pending.Add(n); }
    /// <summary>CreateShader's dedupe: (file, path id) now names an existing record (no new record, no Created).</summary>
    public static void Aliased(Shepherd.Assets.SerializedFile f, Shepherd.Assets.ObjectInfo o, NObj rec) { if (On && !_stopped) _pending.Add(new Alias { File = f?.Name, PathId = o.PathId, Rec = rec }); }
    /// <summary>Writes every record created since the last call (the point: a drain at depth 0, an Instantiate's end). Never throws.</summary>
    public static void Point(string where)
    {
        if (!On || _stopped || _pending.Count == 0) return;
        try
        {
            _w ??= new StreamWriter(File, false, new UTF8Encoding(false)) { AutoFlush = false };
            _line.Clear().Append("@ ").Append(where).Append(' ').Append(_pending.Count);
            if (!Emit()) return;
            foreach (var item in _pending)
            {
                _sb.Clear(); _line.Clear();
                if (item is Alias a)
                {
                    _sb.Append("alias ").Append(a.File).Append(':').Append(a.PathId).Append(" -> ").Append(a.Rec?.InstanceId ?? 0).Append(' ').Append(a.Rec?.GetType().Name);
                    _line.Append(_seq++).Append('\t').Append(a.Rec?.InstanceId ?? 0).Append("\talias\t").Append(a.Rec?.Name).Append('\t').Append(Fnv(_sb).ToString("x16"));
                }
                else
                {
                    var n = (NObj)item;
                    Describe(n);
                    _line.Append(_seq++).Append('\t').Append(n.InstanceId).Append('\t').Append(n.Managed?.GetType().FullName ?? n.GetType().Name).Append('\t')
                         .Append(n.Name).Append('\t').Append(Fnv(_sb).ToString("x16"));
                }
                if (Full) _line.Append('\t').Append(_sb);
                if (!Emit()) return;
            }
            _pending.Clear();
            _w.Flush();
        }
        catch (Exception e) { Stop($"{e.GetType().Name}: {e.Message}"); }
    }
    /// <summary>One line, inside the cap (false: the dump has stopped).</summary>
    static bool Emit()
    {
        var text = _line.ToString();
        long n = Encoding.UTF8.GetByteCount(text) + 1;
        if (MaxBytes > 0 && _bytes + n > MaxBytes) { _w.WriteLine($"@ truncated: SHEPHERD_K10_LOADDUMP_MAX_MB cap ({MaxBytes / 1048576.0:F0} MiB) reached at seq {_seq}"); Stop($"the {MaxBytes / 1048576.0:F0} MiB cap reached (SHEPHERD_K10_LOADDUMP_MAX_MB)"); return false; }
        _w.WriteLine(text); _bytes += n;
        return true;
    }
    static void Stop(string why)
    {
        _stopped = true; _pending.Clear();
        Log.Warn($"[k10] load dump stopped at seq {_seq}, {_bytes / 1048576.0:F1} MiB: {why} (the run goes on)");
        try { _w?.Flush(); _w?.Dispose(); } catch { }
        _w = null;
    }
    static ulong Fnv(StringBuilder sb) { ulong h = 14695981039346656037UL; for (int i = 0; i < sb.Length; i++) { h ^= sb[i]; h *= 1099511628211UL; } return h; }

    static void Describe(NObj n)
    {
        _sb.Append(n.GetType().Name).Append(" hs=").Append(n.HasSource ? 1 : 0).Append(" pid=").Append(n.PathId);
        switch (n)
        {
            case NGameObject g: _sb.Append(" go a=").Append(g.ActiveSelf ? 1 : 0).Append(" l=").Append(g.Layer).Append(" t=").Append(g.Tag).Append(" s=").Append(g.Scene).Append(" c=").Append(g.Components.Count); foreach (var c in g.Components) _sb.Append(',').Append(c.InstanceId); break;
            case NTransform t:
                _sb.Append(" tr p=").Append(t.Parent?.InstanceId ?? 0).Append(" go=").Append(t.Go?.InstanceId ?? 0).Append(" k=").Append(t.Children.Count);
                foreach (var c in t.Children) _sb.Append(',').Append(c.InstanceId);
                V3(t.LocalPosition); V4(t.LocalRotation.x, t.LocalRotation.y, t.LocalRotation.z, t.LocalRotation.w); V3(t.LocalScale);
                if (t.IsRect) { V2(t.AnchorMin); V2(t.AnchorMax); V2(t.AnchoredPosition); V2(t.SizeDelta); V2(t.Pivot); }
                break;
            case NBehaviour b: _sb.Append(" en=").Append(b.Enabled ? 1 : 0).Append(" go=").Append(b.Go?.InstanceId ?? 0); break;
            case NComponent c: _sb.Append(" go=").Append(c.Go?.InstanceId ?? 0); break;
        }
        if (n.HideFlags != 0) _sb.Append(" hf=").Append(n.HideFlags);
        switch (n)
        {   // (kit #10 review: the engine records' own fields -- read as fields, nothing computed: EnsureGeometry / decodes never called)
            case NSprite sp:
                _sb.Append(" sprite tex=").Append(sp.Texture?.InstanceId ?? 0); R4(sp.Rect.x, sp.Rect.y, sp.Rect.width, sp.Rect.height); R4(sp.TextureRect.x, sp.TextureRect.y, sp.TextureRect.width, sp.TextureRect.height);
                V2(sp.Pivot); _sb.Append(B(sp.PixelsPerUnit)); V4(sp.Border.x, sp.Border.y, sp.Border.z, sp.Border.w); V2(sp.TextureRectOffset);
                _sb.Append(" pk=").Append(sp.Packed ? 1 : 0).Append(" rot=").Append(sp.PackingRotation);
                _sb.Append(" geo="); Arr2(sp.Vertices); Arr2(sp.Uv); if (sp.Triangles == null) _sb.Append('-'); else { ulong h = Seed; foreach (var t in sp.Triangles) h = Mix(h, t); _sb.Append(sp.Triangles.Length).Append(':').Append(h.ToString("x16")); }
                break;
            case NTexture tx:
                _sb.Append(" tex ").Append(tx.Width).Append('x').Append(tx.Height).Append(" f").Append(tx.Format).Append(" fm").Append(tx.FilterMode).Append(" wm").Append(tx.WrapMode)
                   .Append(" rt").Append(tx.IsRenderTexture ? 1 : 0).Append(" ld").Append(tx.Loaded ? 1 : 0).Append(" px="); Bytes(tx.Rgba);
                if (tx.Src is { } src) { _sb.Append(" src ").Append(src.Width).Append('x').Append(src.Height).Append(" f").Append(src.Format).Append(" in="); Bytes(src.Inline); _sb.Append(" st=").Append(src.StreamPath).Append('@').Append(src.StreamOffset).Append('+').Append(src.StreamSize); }
                break;
            case NShader sh: _sb.Append(" shader \"").Append(sh.ShaderName).Append("\" q=").Append(sh.RenderQueue).Append(" st=").Append(sh.State?.ToString() ?? "-"); break;
            case NMaterial mt:
                _sb.Append(" mat sh=").Append(mt.Shader?.InstanceId ?? 0).Append(" q=").Append(mt.RenderQueue).Append(" p{");
                foreach (var kv in mt.P) { _sb.Append(kv.Key).Append('='); Val(kv.Value, 0); _sb.Append(';'); }
                _sb.Append("} kw{"); foreach (var k in mt.Keywords) _sb.Append(k).Append(';'); _sb.Append('}');
                break;
            case NMesh me:
                _sb.Append(" mesh v="); Arr3(me.Pos); _sb.Append(" c="); if (me.Col == null) _sb.Append('-'); else { ulong h = Seed; foreach (var c in me.Col) h = Mix(h, (uint)(c.r | c.g << 8 | c.b << 16 | c.a << 24)); _sb.Append(me.Col.Length).Append(':').Append(h.ToString("x16")); }
                _sb.Append(" uv="); Arr4(me.Uv0); _sb.Append(" n="); Arr3(me.Normals); _sb.Append(" t="); Arr4(me.Tan);
                _sb.Append(" sub=").Append(me.Sub.Count); for (int i = 0; i < me.Sub.Count; i++) { ulong h = Seed; foreach (var x in me.Sub[i]) h = Mix(h, (uint)x); _sb.Append(',').Append(me.Sub[i].Length).Append(':').Append(h.ToString("x16")).Append('/').Append(i < me.SubTopology.Count ? me.SubTopology[i] : -1); }
                V3(me.Bounds.center); V3(me.Bounds.extents); _sb.Append(" bs=").Append(me.BoundsSet ? 1 : 0).Append(" ver=").Append(me.Version);
                break;
        }
        if (n.Props != null) { _sb.Append(" props{"); foreach (var kv in PropBag.Entries(n.Props)) { _sb.Append(kv.Key).Append('='); Val(kv.Value, 0); _sb.Append(';'); } _sb.Append('}'); }
        if (n.Managed is MonoBehaviour or ScriptableObject) { _sb.Append(" fields{"); Fields(n.Managed, n.Managed.GetType(), 0); _sb.Append('}'); }
    }
    static void Fields(object o, Type t, int depth)
    {
        foreach (var fi in SerRules.Fields(t))
        {
            _sb.Append(fi.Name).Append('=');
            object v; try { v = fi.GetValue(o); } catch (Exception e) { _sb.Append("!").Append(e.GetType().Name).Append(';'); continue; }
            Val(v, depth); _sb.Append(';');
        }
    }
    static void Val(object v, int depth)
    {
        switch (v)
        {
            case null: _sb.Append("null"); return;
            case UObject u: { var r = Objects.Get(u); _sb.Append("ref:").Append(r != null ? r.InstanceId.ToString() : (object)u == null ? "dead" : "unbound"); return; }
            case NObj rec: _sb.Append("rec:").Append(rec.InstanceId); return;   // (an engine record held as a value: a material's texture)
            case Shepherd.Engine.Assets.LazyRef lr: _sb.Append("lazy:").Append(lr.File?.Name).Append(':').Append(lr.P.FileId).Append(':').Append(lr.P.PathId); return;
            case string s: _sb.Append('"').Append(s.Length).Append(':').Append(s).Append('"'); return;
            case float x: _sb.Append("f").Append(BitConverter.SingleToInt32Bits(x).ToString("x8")); return;
            case double d: _sb.Append("d").Append(BitConverter.DoubleToInt64Bits(d).ToString("x16")); return;
            case bool b: _sb.Append(b ? "T" : "F"); return;
            case byte[] bs: _sb.Append("b[").Append(bs.Length).Append(']').Append(Convert.ToHexString(bs.AsSpan(0, Math.Min(64, bs.Length)))); return;
            case Vector2 a: V2(a); return;
            case Vector3 a: V3(a); return;
            case Vector4 a: V4(a.x, a.y, a.z, a.w); return;
            case Quaternion a: V4(a.x, a.y, a.z, a.w); return;
            case Color a: V4(a.r, a.g, a.b, a.a); return;
            case Rect a: V4(a.x, a.y, a.width, a.height); return;
        }
        var t = v.GetType();
        if (t.IsPrimitive || t.IsEnum || v is decimal) { _sb.Append(t.Name).Append(':').Append(Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture)); return; }
        if (depth > 20) { _sb.Append("deep"); return; }
        if (v is IList l) { _sb.Append(t.Name).Append('[').Append(l.Count).Append("]("); for (int i = 0; i < l.Count; i++) { if (i > 0) _sb.Append(','); Val(l[i], depth + 1); } _sb.Append(')'); return; }
        if (v is AnimationCurve ac) { _sb.Append("curve["); foreach (var k in ac.keys) { V4(k.time, k.value, k.inTangent, k.outTangent); V4(k.inWeight, k.outWeight, (int)k.weightedMode, 0); } _sb.Append(']').Append((int)ac.preWrapMode).Append(',').Append((int)ac.postWrapMode); return; }
        if (t.IsValueType && t.Namespace == "UnityEngine") { _sb.Append(t.Name).Append(':'); foreach (var fi in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)) { _sb.Append(fi.Name).Append('='); Val(fi.GetValue(v), depth + 1); _sb.Append(','); } return; }
        _sb.Append(t.Name).Append('{'); Fields(v, t, depth + 1); _sb.Append('}');
    }
    static void V2(Vector2 a) => _sb.Append('(').Append(B(a.x)).Append(',').Append(B(a.y)).Append(')');
    static void V3(Vector3 a) => _sb.Append('(').Append(B(a.x)).Append(',').Append(B(a.y)).Append(',').Append(B(a.z)).Append(')');
    static void V4(float a, float b, float c, float d) => _sb.Append('(').Append(B(a)).Append(',').Append(B(b)).Append(',').Append(B(c)).Append(',').Append(B(d)).Append(')');
    static string B(float x) => BitConverter.SingleToInt32Bits(x).ToString("x8");
    static void R4(float a, float b, float c, float d) => V4(a, b, c, d);
    const ulong Seed = 14695981039346656037UL;
    static ulong Mix(ulong h, uint v) { for (int i = 0; i < 4; i++) { h ^= (byte)(v >> (8 * i)); h *= 1099511628211UL; } return h; }
    static ulong MixF(ulong h, float f) => Mix(h, (uint)BitConverter.SingleToInt32Bits(f));
    static void Bytes(byte[] b) { if (b == null) { _sb.Append('-'); return; } ulong h = Seed; foreach (var x in b) { h ^= x; h *= 1099511628211UL; } _sb.Append(b.Length).Append(':').Append(h.ToString("x16")); }
    static void Arr2(Vector2[] a) { if (a == null) { _sb.Append("-,"); return; } ulong h = Seed; foreach (var v in a) { h = MixF(h, v.x); h = MixF(h, v.y); } _sb.Append(a.Length).Append(':').Append(h.ToString("x16")).Append(','); }
    static void Arr3(Vector3[] a) { if (a == null) { _sb.Append('-'); return; } ulong h = Seed; foreach (var v in a) { h = MixF(h, v.x); h = MixF(h, v.y); h = MixF(h, v.z); } _sb.Append(a.Length).Append(':').Append(h.ToString("x16")); }
    static void Arr4(Vector4[] a) { if (a == null) { _sb.Append('-'); return; } ulong h = Seed; foreach (var v in a) { h = MixF(h, v.x); h = MixF(h, v.y); h = MixF(h, v.z); h = MixF(h, v.w); } _sb.Append(a.Length).Append(':').Append(h.ToString("x16")); }
    public static void Close()
    {
        if (!On) return;
        Point("end");
        try { _w?.Flush(); _w?.Dispose(); } catch (Exception e) { Log.Warn($"[k10] load dump close: {e.GetType().Name}: {e.Message}"); }
        _w = null;
    }
}
#else
/// <summary>The eboot's stand-in (kit #10: the load dump is a test tool -- Mac builds and SHEPHERD_ENGINE_TOOLS=1 eboots carry it; AOT code is span).</summary>
public static class LoadDump
{
    public const bool On = false;
    public static void Created(NObj n) { }
    public static void Aliased(Shepherd.Assets.SerializedFile f, Shepherd.Assets.ObjectInfo o, NObj rec) { }
    public static void Point(string where) { }
    public static void Close() { }
}
#endif
