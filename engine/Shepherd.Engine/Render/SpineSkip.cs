using System.Runtime.CompilerServices;
using Shepherd.Engine.Native;
using static Shepherd.Engine.Render.Sdl;
namespace Shepherd.Engine.Render;

/// <summary>kit #7 spine track, "Spine: skip provably invisible pieces" (vita/notes/kit7-spine.md; roadmap kit #7): in a Spine skeleton's mesh
/// draw, the triangles whose three vertices end with colour (0, 0, 0, 0) -- an attachment faded out by its slot / attachment / skeleton alpha
/// (MeshGenerator writes one colour per attachment; with premultiplied vertex colours an alpha of 0 makes every byte 0) -- leave the GPU
/// draw, nothing else. Why this is provably a no-op, per program and blend mode (Render/Shaders/sprite.frag: gl_FragColor = texel x the
/// interpolated vertex colour; three zero colours interpolate to exactly 0 everywhere, so every fragment is (0, 0, 0, 0)): BLEND (src x srcA +
/// dst x (1 - srcA); alpha ONE, 1 - srcA), BLEND_PREMULTIPLIED (ONE, 1 - srcA), ADD (srcA, ONE; alpha ZERO, ONE), ADD_PREMULTIPLIED (ONE, ONE;
/// ZERO, ONE) and MUL (DST_COLOR, 1 - srcA; ZERO, ONE) all give dst exactly (a factor of exactly 1 on dst, 0 from src); the backends use no
/// depth or stencil. Never skipped: the lit program (the owner-local lit model adds fog / fill / emission whatever the vertex colour:
/// not a no-op -- the draw goes the lit way, see SceneRenderer.LitBegin), SDF text, FX draws (ShaderPolicy), NONE / MOD and composed blends.
/// What stays: the mesh, its vertices and bounds (culling, isVisible, the black-frame watch -- it still sees the draw's vertices), the draw's
/// place in the order. Only the backend's draw call loses those triangles (all of them: the call is not made). The draw stream (glrec / the
/// stream hash) changes only where such triangles exist -- none on the kit routes or the intro's first room (SpineCensus, MEASURED-M).
/// Switch (env files): SHEPHERD_SPINE_SKIP0=0 -- every triangle drawn (kit #6). Off by itself while SHEPHERD_RENDER_CHECK runs (that check
/// compares the draw stream with the pre-stage-2 renderer's).</summary>
[ProfileOnly]
public static class SpineSkip
{
    public static readonly bool On = Environment.GetEnvironmentVariable("SHEPHERD_SPINE_SKIP0") is not ("0" or "off");
    static readonly ConditionalWeakTable<NComponent, object> _spine = new();
    static readonly object Yes = new(), No = new();
    /// <summary>Triangles / whole draws left out so far (the [spineskip] end line).</summary>
    internal static long Tris, Draws; static long _totalTris, _totalDraws;

    /// <summary>Is this renderer a Spine skeleton's (a component of its GameObject derives from a type named SkeletonRenderer)? Cached per
    /// renderer record; a type check by name, as the engine reads every game type (no reference to spine-unity).</summary>
    internal static bool IsSpine(NComponent renderer)
    {
        if (renderer?.Go == null) return false;
        if (_spine.TryGetValue(renderer, out var v)) return v == Yes;
        bool s = false;
        foreach (var c in renderer.Go.Components)
            for (var t = c.Managed?.GetType(); t != null && !s; t = t.BaseType) if (t.Name == "SkeletonRenderer") s = true;
        _spine.AddOrUpdate(renderer, s ? Yes : No);
        return s;
    }
    /// <summary>The blend modes whose result is the destination for a (0, 0, 0, 0) source (see the class summary).</summary>
    internal static bool NoOpBlend(uint blend) => blend is BLENDMODE_BLEND or BLENDMODE_BLEND_PREMULTIPLIED or BLENDMODE_ADD or BLENDMODE_ADD_PREMULTIPLIED or BLENDMODE_MUL;

    /// <summary>ib[0..ni) without the triangles whose three vertices are colour (0, 0, 0, 0), compacted in place, order kept; returns the new count.</summary>
    internal static int Filter(Vertex[] v, int[] ib, int ni)
    {
        int w = 0; ni -= ni % 3;
        for (int k = 0; k < ni; k += 3)
        {
            int a = ib[k], b = ib[k + 1], c = ib[k + 2];
            if (Zero(in v[a]) && Zero(in v[b]) && Zero(in v[c])) continue;
            if (w != k) { ib[w] = a; ib[w + 1] = b; ib[w + 2] = c; }
            w += 3;
        }
        if (w != ni) { Interlocked.Add(ref Tris, (ni - w) / 3); if (w == 0) Interlocked.Increment(ref Draws); }
        return w;
    }
    static bool Zero(in Vertex x) => x.color.r == 0f && x.color.g == 0f && x.color.b == 0f && x.color.a == 0f;

    public static string Summary()
    {
        long t = _totalTris + Interlocked.Exchange(ref Tris, 0), d = _totalDraws + Interlocked.Exchange(ref Draws, 0);
        return t == 0 ? null : $"[spineskip] Spine triangles of colour 0 left out of the GPU draws: {t:N0} ({d:N0} whole draws)";
    }
}
