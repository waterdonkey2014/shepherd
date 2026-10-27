using UnityEngine;
namespace Shepherd.Engine.Render;

/// <summary>menu-speed, top-hotspots stage: the switches (like UiCacheCfg; the literals live in [ProfileOnly] types, so the V3 diet's
/// readable-key scan is unchanged). SHEPHERD_HOTSPOTS=0: this stage's caches and shortcuts off -- the passes exactly as they were
/// (the world pass reads every renderer's property bag every frame, WorldStamp re-validates the whole parent chain, ...), for A/B.
/// SHEPHERD_HOTSPOT_CHECK=1: <see cref="HotCheck"/>.</summary>
[ProfileOnly]
public static class HotCfg
{
    public static readonly bool On = Environment.GetEnvironmentVariable("SHEPHERD_HOTSPOTS") != "0";
    public static readonly bool Check = On && Environment.GetEnvironmentVariable("SHEPHERD_HOTSPOT_CHECK") == "1";
    /// <summary>SHEPHERD_HOTSPOT_DELEGATES=0: the phase messages through MethodInfo.Invoke (as before) with the rest of this stage on (A/B).</summary>
    public static readonly bool Delegates = On && Environment.GetEnvironmentVariable("SHEPHERD_HOTSPOT_DELEGATES") != "0";
    /// <summary>A stack-trace line of Lifecycle.CallD, the method a phase message's delegate is called and caught in: the one frame such an
    /// exception's trace has that MethodInfo.Invoke's inner exception does not (it stops at the reflection boundary); Log.Exception leaves it out.</summary>
    public static bool IsCallFrame(string line) => On && line.Contains("Shepherd.Engine.Runtime.Lifecycle.CallD");
}

/// <summary>SHEPHERD_HOTSPOT_CHECK=1 (check runs only): every value this stage's caches hand out is also computed the way the pass computed it
/// before (the original expression, on the live inputs), every time it is used, and the two are compared (floats bit for bit, references
/// by identity). Mismatches: [hotspots] lines (the first 20 in full); <see cref="Summary"/> at exit, per site. A check run's stream equals the
/// plain run's: the comparisons only read (every original expression they re-run is free of side effects once its cached value exists).</summary>
[ProfileOnly]
public static class HotCheck
{
    public const int WEnabled = 0, WKind = 1, WSprite = 2, WNSprite = 3, WTex = 4, WColor = 5, WFlip = 6, WShared = 7, WMat = 8, WBlend = 9,
        WLayer = 10, WOrder = 11, Position = 12, Stamp = 13, Abs = 14, Nrc = 15, Phase = 16, Find = 17, Cams = 18, Starts = 19, Boxes = 20, Count = 21;
    static readonly string[] Names =
    {
        "world pass: enabled (vs r.Get(\"enabled\") + Conv.To)", "world pass: renderer kind (vs r.Managed is SpriteRenderer / MeshRenderer / a SpriteShapeRenderer)",
        "world pass: sprite (vs r.Get(\"sprite\") as Sprite ?? Conv.To)", "world pass: sprite record (vs Objects.Get<NSprite>)",
        "world pass: texture touch skipped (vs RenderIcalls.Tex: loaded, bound)", "world pass: colour (vs r.Get(\"color\") is Color ? : white)",
        "world pass: flipX / flipY (vs Flag)", "world pass: shared materials (vs RendererIcalls.Shared; sprites and SpriteShapes)", "world pass: material record (vs Objects.Get<NMaterial>)",
        "world pass: blend (vs ShaderOf(name).BlendWorld)", "world pass: sorting layer (vs SortingLayerValue)", "world pass: sorting order (vs SortOrder)",
        "NTransform.Position memo (vs M.Point(parent LocalToWorld, EffectiveLocalPosition))", "NTransform.WorldStamp epoch shortcut (vs the full chain validation)",
        "BoxOutside's own |x| (vs MathF.Abs: the cull result)",
        "NotifyRectChanges queue (every transform the full walk would compare is collected)",
        "Lifecycle phase lists (vs Active filtered by the message, in order)",
        "GameObject.Find name-index miss (vs the walk)",
        "SceneRenderer.Frame cameras (vs AllCameras().ToList())", "Lifecycle Start order, few behaviours (vs OrderBy)",
        "icall result boxes kept per value (lossyScale, Renderer.bounds: vs the value now)",
    };
    static readonly long[] _n = new long[Count], _bad = new long[Count];
    static int _notes;
    static uint B(float f) => (uint)BitConverter.SingleToInt32Bits(f);
    static void Res(int site, bool ok, string what)
    {
        _n[site]++;
        if (ok) return;
        _bad[site]++; if (++_notes <= 20) Log.Info($"[hotspots] frame {Runtime.Time.FrameCount}: {Names[site]}: {what}");
    }
    public static void Bool(int site, bool ours, bool orig) => Res(site, ours == orig, $"{ours} vs {orig}");
    public static void Ref(int site, object ours, object orig) => Res(site, ReferenceEquals(ours, orig), $"{ours} vs {orig}");
    public static void F(int site, float ours, float orig) => Res(site, B(ours) == B(orig), $"{ours:R} vs {orig:R}");
    public static void U(int site, uint ours, uint orig) => Res(site, ours == orig, $"{ours} vs {orig}");
    public static void L(int site, long ours, long orig) => Res(site, ours == orig, $"{ours} vs {orig}");
    public static void Col(int site, Color o, Color u) => Res(site, B(o.r) == B(u.r) && B(o.g) == B(u.g) && B(o.b) == B(u.b) && B(o.a) == B(u.a), $"{o} vs {u}");
    public static void V3(int site, Vector3 o, Vector3 u) => Res(site, B(o.x) == B(u.x) && B(o.y) == B(u.y) && B(o.z) == B(u.z), $"({o.x:R},{o.y:R},{o.z:R}) vs ({u.x:R},{u.y:R},{u.z:R})");
    /// <summary>The phase snapshot equals Active (as the phase starts) filtered by "the type has the message", element for element.</summary>
    public static void PhaseList(List<Native.NMono> active, Native.NMono[] got, int n, Func<Native.NMono, bool> has)
    {
        int j = 0; bool ok = true;
        foreach (var m in active) { if (!has(m)) continue; if (j >= n || !ReferenceEquals(got[j], m)) { ok = false; break; } j++; }
        Res(Phase, ok && j == n, $"{n} listed, {j} matched");
    }
    /// <summary>Two boxed values of the same struct type, byte for byte (Bounds: centre and extents, 6 floats).</summary>
    public static void Same(int site, object ours, object orig)
    {
        bool ok = ours.GetType() == orig.GetType();
        if (ok && ours is Bounds a && orig is Bounds b) ok = B(a.center.x) == B(b.center.x) && B(a.center.y) == B(b.center.y) && B(a.center.z) == B(b.center.z) && B(a.extents.x) == B(b.extents.x) && B(a.extents.y) == B(b.extents.y) && B(a.extents.z) == B(b.extents.z);
        else if (ok) ok = Equals(ours, orig);
        Res(site, ok, $"{ours} vs {orig}");
    }
    /// <summary>Two sequences, element for element (reference identity).</summary>
    public static void Seq<T>(int site, IReadOnlyList<T> ours, IReadOnlyList<T> orig) where T : class
    {
        bool ok = ours.Count == orig.Count; for (int i = 0; ok && i < ours.Count; i++) ok = ReferenceEquals(ours[i], orig[i]);
        Res(site, ok, $"{ours.Count} vs {orig.Count} elements");
    }
    public static string Summary()
    {
        if (!HotCfg.Check) return null;
        long n = 0, bad = 0; for (int i = 0; i < Count; i++) { n += _n[i]; bad += _bad[i]; }
        var sb = new System.Text.StringBuilder($"[hotspots] check: {n} values compared, {bad} mismatches");
        for (int i = 0; i < Count; i++) if (_n[i] > 0 || _bad[i] > 0) sb.Append($"\n      {Names[i]}: {_n[i]} checked, {_bad[i]} differ");
        sb.Append($"\n      NotifyRectChanges walks: {SceneRenderer.NrcQueuedWalks} queued ({SceneRenderer.NrcCollected} transforms collected, {SceneRenderer.NrcRemainders} finished by the full loop after game code ran), {SceneRenderer.NrcFullWalks} full");
        return sb.ToString();
    }
}
