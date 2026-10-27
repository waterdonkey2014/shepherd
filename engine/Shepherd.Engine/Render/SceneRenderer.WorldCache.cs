using Shepherd.Engine.Impl;
using Shepherd.Engine.Native;
using Shepherd.Engine.Runtime;
using UnityEngine;
namespace Shepherd.Engine.Render;

// menu-speed, stage "top remaining hotspots": the world pass's property-bag reads, kept per renderer.
//
// Every frame DrawWorld read ~8 string-keyed property-bag values per visible SpriteRenderer (enabled, sprite, color, flipX, flipY,
// sharedMaterials, sortingLayerID, sortingOrder), resolved the sprite and the material to their records through the pointer dictionary
// and looked the shader name up: ~5,300 dictionary lookups per idle Main Menu frame, ~13 % of the engine-AOT frame (native samples),
// for values that change a few times per scene. Now each renderer's record (NRenderer) keeps what those reads gave, field by field:
// - a bag-derived value is re-read -- with the original expression, at the point of the pass where the original read it -- whenever
//   the record's PropsVer differs from the one it was read at. Every write to a bag bumps it (NObj.Set / RemoveProp / ReplaceProps are
//   the only writers; the values are immutable boxes and object references, replaced, never written in place), so a kept value is what
//   the read would return now. The original expressions are pure given the bag (Conv.To to bool / float / Sprite, SortingLayerValue,
//   SortOrder) except RendererIcalls.Shared's first conversion, which stores its result in the bag: it runs where it ran, and the
//   version is taken after it. A LazyRef sprite (its resolution reads the loader's live set) is resolved every frame, as before.
// - a record looked up from a managed object (the sprite's NSprite, the first material's NMaterial) is reused while it is alive and the
//   managed object still points at it (m_CachedPtr == its Ptr: exactly when Objects.Get would return it).
// - the texture touch (RenderIcalls.Tex) is skipped when it is a no-op: the sprite's texture is loaded, alive and still bound.
// - the blend is kept per shader-name string (ShaderOf is a function of the name).
// SHEPHERD_HOTSPOTS=0 runs the reads as they were; SHEPHERD_HOTSPOT_CHECK=1 recomputes every kept value with the original expression
// each time it is used and compares (Render/HotCheck.cs).
public static partial class SceneRenderer
{
    /// <summary>One world renderer's kept property-bag reads (see the file comment): each value is read where the pass reads it, the first
    /// time after the bag changed (a bit of _valid per value; all clear when the record's PropsVer differs from _ver). The only bag write that
    /// can happen while the pass reads one renderer is RendererIcalls.Shared's first conversion, which stores the converted sharedMaterials
    /// (no game code runs in the pass): the version is taken after it, and the values read before it -- other keys -- stay valid.</summary>
    internal sealed class WCache
    {
        int _ver = int.MinValue; ushort _valid;
        const ushort VEn = 1, VSp = 2, VCol = 4, VFl = 8, VSh = 16, VOrd = 32, VLay = 64;
        byte _kind; bool _en, _spLive, _hasCol, _fx, _fy;
        object _managed, _sv; Sprite _sp; NSprite _ns; Color _col;
        Material[] _shared; Material _m0; NMaterial _mat; bool _matSet;
        string _shName; uint _blend;
        float _order, _layer; Func<NComponent, float> _layFn; int _layN;

        bool Fresh(NRenderer r, ushort bit) { if (_ver != r.PropsVer) { _ver = r.PropsVer; _valid = 0; } return (_valid & bit) != 0; }
        /// <summary>1 = SpriteRenderer, 2 = MeshRenderer, 3 = SpriteShapeRenderer (fidelity round 1: Registry's by-name type test), 0 = none of
        /// them (the type tests DrawWorld made, per managed object).</summary>
        public int Kind(NRenderer r)
        {
            if (!ReferenceEquals(_managed, r.Managed)) { _managed = r.Managed; _kind = (byte)KindOrig(r); }
            if (HotCfg.Check) HotCheck.U(HotCheck.WKind, _kind, (uint)KindOrig(r));
            return _kind;
        }
        static int KindOrig(NRenderer r) => r.Managed is SpriteRenderer ? 1 : r.Managed is MeshRenderer ? 2 : Registry.IsSpriteShapeRenderer(r) ? 3 : 0;
        /// <summary>RendererIcalls.Shared(r) (a SpriteShapeRenderer's materials: SpriteShapes.Draw picks one per segment). Kept like
        /// <see cref="Material"/>'s read of the same key (the version taken after Shared's first conversion).</summary>
        public Material[] Shared(NRenderer r)
        {
            if (!Fresh(r, VSh)) { _shared = RendererIcalls.Shared(r); _ver = r.PropsVer; _valid |= VSh; }
            if (HotCfg.Check) HotCheck.Ref(HotCheck.WShared, _shared, RendererIcalls.Shared(r));
            return _shared;
        }
        /// <summary>!(r.Get("enabled") is { } en &amp;&amp; !(en is bool eb ? eb : (bool)Conv.To(en, typeof(bool)))): false = the pass skips it.</summary>
        public bool Enabled(NRenderer r)
        {
            if (!Fresh(r, VEn)) { _en = EnabledOrig(r); _valid |= VEn; }
            if (HotCfg.Check) HotCheck.Bool(HotCheck.WEnabled, _en, EnabledOrig(r));
            return _en;
        }
        /// <summary>The pass's sprite: sv as Sprite ?? (sv is LazyRef ? RefTo&lt;Sprite&gt;(sv) : Conv.To(sv, typeof(Sprite)) as Sprite) of sv = r.Get("sprite").</summary>
        public Sprite Sprite(NRenderer r)
        {
            if (!Fresh(r, VSp))
            {
                var sv = r.Get("sprite"); _spLive = sv is Assets.LazyRef; _sv = _spLive ? sv : null;
                _sp = _spLive ? null : sv as Sprite ?? Conv.To(sv, typeof(Sprite)) as Sprite;
                _valid |= VSp;
            }
            if (_spLive) return RefTo<Sprite>(_sv);   // (resolved every frame: a LazyRef's target depends on the loader's live set)
            if (HotCfg.Check) { var sv = r.Get("sprite"); HotCheck.Ref(HotCheck.WSprite, _sp, sv as Sprite ?? (sv is Assets.LazyRef ? RefTo<Sprite>(sv) : Conv.To(sv, typeof(Sprite)) as Sprite)); }
            return _sp;
        }
        /// <summary>Objects.Get&lt;NSprite&gt;(sp) (a bound record's Managed is the object it is bound to).</summary>
        public NSprite NSpriteOf(Sprite sp)
        {
            if (!(_ns != null && ReferenceEquals(sp, _ns.Managed) && Bound(_ns, sp))) _ns = Objects.Get<NSprite>(sp);
            if (HotCfg.Check) HotCheck.Ref(HotCheck.WNSprite, _ns, Objects.Get<NSprite>(sp));
            return _ns;
        }
        /// <summary>r.Get("color") is Color c ? c : White().</summary>
        public Color Color(NRenderer r)
        {
            if (!Fresh(r, VCol)) { var cv = r.Get("color"); _hasCol = cv is Color; _col = cv is Color c ? c : default; _valid |= VCol; }
            var col = _hasCol ? _col : White();
            if (HotCfg.Check) HotCheck.Col(HotCheck.WColor, col, r.Get("color") is Color c2 ? c2 : Um.White);
            return col;
        }
        /// <summary>Flag(r, "flipX"), Flag(r, "flipY").</summary>
        public void Flips(NRenderer r, out bool fx, out bool fy)
        {
            if (!Fresh(r, VFl)) { _fx = Flag(r, "flipX"); _fy = Flag(r, "flipY"); _valid |= VFl; }
            fx = _fx; fy = _fy;
            if (HotCfg.Check) { HotCheck.Bool(HotCheck.WFlip, fx, Flag(r, "flipX")); HotCheck.Bool(HotCheck.WFlip, fy, Flag(r, "flipY")); }
        }
        /// <summary>RendererIcalls.Shared(r) and Objects.Get&lt;NMaterial&gt;(shared.Length &gt; 0 ? shared[0] : null).</summary>
        public NMaterial Material(NRenderer r)
        {
            if (!Fresh(r, VSh)) { _shared = RendererIcalls.Shared(r); _ver = r.PropsVer; _valid |= VSh; }   // (Shared's first call may store its conversion: its own key; the version after it)
            var sh = _shared; var m0 = sh.Length > 0 ? sh[0] : null;
            if (!(_matSet && ReferenceEquals(m0, _m0) && (m0 is null || _mat != null && Bound(_mat, m0)))) { _mat = Objects.Get<NMaterial>(m0); _m0 = m0; _matSet = true; }
            if (HotCfg.Check)
            {
                var osh = RendererIcalls.Shared(r); HotCheck.Ref(HotCheck.WShared, sh, osh);
                HotCheck.Ref(HotCheck.WMat, _mat, Objects.Get<NMaterial>(osh.Length > 0 ? osh[0] : null));
            }
            return _mat;
        }
        /// <summary>ShaderOf(mat?.ShaderName ?? "Sprites/Default").BlendWorld (a function of the name string).</summary>
        public uint Blend(NMaterial mat)
        {
            var sn = mat?.ShaderName ?? "Sprites/Default";
            if (!ReferenceEquals(sn, _shName)) { _blend = ShaderOf(sn).BlendWorld; _shName = sn; }
            if (HotCfg.Check) HotCheck.U(HotCheck.WBlend, _blend, ShaderOf(sn).BlendWorld);
            return _blend;
        }
        /// <summary>SortingLayerValue(r) (the loader's: the sortingLayerID's index among the project's sorting layers, which are only ever appended at load).</summary>
        public float Layer(NRenderer r)
        {
            var fn = SortingLayerValue; int nl = Assets.Loader.SortingLayers.Count;
            if (!Fresh(r, VLay) || !ReferenceEquals(fn, _layFn) || nl != _layN) { _layer = fn(r); _layFn = fn; _layN = nl; _valid |= VLay; }
            if (HotCfg.Check) HotCheck.F(HotCheck.WLayer, _layer, SortingLayerValue(r));
            return _layer;
        }
        /// <summary>SortOrder(r).</summary>
        public float Order(NRenderer r)
        {
            if (!Fresh(r, VOrd)) { _order = SortOrder(r); _valid |= VOrd; }
            if (HotCfg.Check) HotCheck.F(HotCheck.WOrder, _order, SortOrder(r));
            return _order;
        }
        static bool EnabledOrig(NRenderer r) => !(r.Get("enabled") is { } en && !(en is bool eb ? eb : (bool)Conv.To(en, typeof(bool))));
    }

    /// <summary>Objects.Get(u) would return n: n is alive and u's m_CachedPtr is n's block (Bind sets both, Kill clears both; a live record's
    /// block is never reused, so the pointer dictionary maps it to n).</summary>
    static bool Bound(NObj n, UnityEngine.Object u) => !n.Destroyed && n.Ptr != IntPtr.Zero && Objects.PtrOf(u) == n.Ptr;

    /// <summary>RenderIcalls.Tex(t?.Managed), skipped when it would do nothing: t is loaded, alive and its managed object still points at it
    /// (Tex looks the record up and touches it only when it is not loaded).</summary>
    static void TouchTex(NTexture t)
    {
        bool noop = t != null && t.Loaded && t.Managed is { } m && Bound(t, m);
        if (HotCfg.Check && noop) HotCheck.Bool(HotCheck.WTex, true, RenderIcalls.Tex(t.Managed) == t);   // (Tex of a loaded, bound texture: no touch)
        if (!noop) RenderIcalls.Tex(t?.Managed);
    }
}
