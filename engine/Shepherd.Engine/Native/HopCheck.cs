using UnityEngine;
namespace Shepherd.Engine.Native;

/// <summary>SHEPHERD_HOPFIX_CHECK=1 (check runs only): every operation the hop fix replaced with our own field arithmetic (<see cref="Um"/>
/// and the inline loops in SceneRenderer, NTransform, NMesh, M and the icalls) is also computed the old way -- through the Unity method
/// it stands for, in the interpreter -- on the same operands, every time it runs, and the two results are compared bit for bit
/// (floats as their 32-bit patterns: NaN payloads and -0 included). The transform memos (rect, local/world matrices, rotation, lossy
/// scale, position) are compared against Unity-operator formulas by RenderCheck (SHEPHERD_RENDER_CHECK=1). Mismatches: [hopfix] lines
/// (the first 20 in full); <see cref="Summary"/> at exit, per site. A difference only in the payload bits of a result that is NaN on both
/// sides is counted apart: IEEE leaves which input NaN's payload a result carries to the hardware and operand order, and a code
/// generator may swap the operands of a commutative add / multiply (CoreCLR's JIT does in the self-test's Vector4 lerp with two NaN
/// inputs; Mono's interpreter and AOT show none). NaN-ness and every other bit must match.</summary>
[ProfileOnly]
public static class HopCheck
{
    public static readonly bool On = Environment.GetEnvironmentVariable("SHEPHERD_HOPFIX_CHECK") == "1";
    public const int Project = 0, MeshMinMax = 1, RecalcMinMax = 2, RecalcBounds = 3, CopyV4 = 4, SortDist = 5, White = 6, Aabb = 7, MPoint = 8,
        MVec = 9, MRotate = 10, MTrs = 11, Quat = 12, NtSize = 13, NtRect = 14, NtElp = 15, NtElpSet = 16, NtLossy = 17, Identity = 18,
        PixelRect = 19, V2Eq = 20, MaskRect = 21, ClipRect = 22, Planes = 23, BoundsIcall = 24, Layout = 25, ViewRect = 26, Vec = 27, Self = 28, Count = 29;
    static readonly string[] Names =
    {
        "SceneRenderer.Project pixel rect (vs Rect.x/y/width/height)", "SceneRenderer.MeshOutside min/max (vs Vector3.Min/Max)",
        "NMesh.Recalc min/max (vs Vector3.Min/Max)", "NMesh.Recalc bounds (vs new Bounds((mn+mx)*0.5f, mx-mn))", "RenderIcalls.CopyChannel uv (vs new Vector4)",
        "sort distance (vs (a-b).sqrMagnitude)", "Color.white", "GateIcalls.TestAABB (vs Dot/normal/distance/center/extents)", "M.Point (vs new Vector3)",
        "M.Vec (vs new Vector3)", "M.Rotate (vs new Vector3)", "M.TRS columns (vs new Vector3)", "M quaternions (vs new Quaternion / identity)",
        "NTransform.Size (vs SizeDelta + Vector2.Scale(AnchorMax - AnchorMin, pr.size))", "NTransform.Rect (vs new Rect(-piv*s, s))",
        "NTransform.EffectiveLocalPosition get (vs LerpUnclamped + Rect getters)", "NTransform.EffectiveLocalPosition set (vs LerpUnclamped + Rect getters)",
        "NTransform.LossyScale (vs Vector3.Scale)", "Matrix4x4.identity", "CameraIcalls.PixelRect (vs new Rect(r.x*W, ...))", "Vector2 == (NotifyRectChanges)",
        "MaskOf rect (vs Rect.xMin/xMax/yMin/yMax)", "CanvasRenderer clip rect (vs Rect.xMin/yMin/xMax/yMax)", "VideoIcalls.Planes (vs GetRow/+/-/magnitude/new Plane)",
        "RendererIcalls.Bounds (vs new Bounds(p, Vector3.one))", "LayoutRootCanvases values (vs Vector2/Vector3 ctors, zero, identity)", "CameraIcalls.ViewRect (vs new Rect(0,0,1,1))",
        "Vector2/Vector3/Vector4 constructors elsewhere", "Um self-test (every helper vs its Unity method over special values: +-0, +-inf, NaN payloads, denormals, extremes, random bits)",
    };
    static readonly long[] _n = new long[Count], _bad = new long[Count], _nanp = new long[Count];
    static int _notes, _nanNotes;
    static uint B(float f) => (uint)BitConverter.SingleToInt32Bits(f);
    /// <summary>0: the same bits; 1: both NaN, different payloads (the IEEE payload of a NaN result is not fixed across code generators:
    /// a JIT may swap the operands of a commutative add / multiply, and with two NaN inputs that picks the other one's payload);
    /// 2: a real difference.</summary>
    static int D(float o, float u) => B(o) == B(u) ? 0 : float.IsNaN(o) && float.IsNaN(u) ? 1 : 2;
    // (menu-speed multicore stage: with SHEPHERD_MT_RENDER=1 the render thread's vertex build checks its sites too -- WorldEmitter's glow
    // centre and near/far clip interpolation -- while the main thread checks others: the counts are interlocked, a mismatch note is locked)
    static readonly object _gate = new();
    static void Res(int site, int k, string what) { lock (_gate) Res0(site, k, what); }
    static void Res0(int site, int k, string what)
    {
        if (k == 2) { _bad[site]++; if (++_notes <= 20) Log.Info($"[hopfix] frame {Runtime.Time.FrameCount}: {Names[site]}: {what}"); }
        else if (k == 1) { _nanp[site]++; if (++_nanNotes <= 5) Log.Info($"[hopfix] frame {Runtime.Time.FrameCount}: {Names[site]}: NaN payload only: {what}"); }
    }
    static string H(float f) => float.IsNaN(f) ? $"NaN:{B(f):X8}" : f.ToString("R");
    static int Mx(int a, int b) => a > b ? a : b;

    public static void F(int site, float ours, float unity) { Interlocked.Increment(ref _n[site]); int k = D(ours, unity); if (k > 0) Res(site, k, $"{H(ours)} vs {H(unity)}"); }
    public static void Bool(int site, bool ours, bool unity) { Interlocked.Increment(ref _n[site]); int k = ours == unity ? 0 : 2; if (k > 0) Res(site, k, $"{ours} vs {unity}"); }
    public static void V2(int site, Vector2 o, Vector2 u) { Interlocked.Increment(ref _n[site]); int k = Mx(D(o.x, u.x), D(o.y, u.y)); if (k > 0) Res(site, k, $"({H(o.x)},{H(o.y)}) vs ({H(u.x)},{H(u.y)})"); }
    public static void V3(int site, Vector3 o, Vector3 u) { Interlocked.Increment(ref _n[site]); int k = Mx(Mx(D(o.x, u.x), D(o.y, u.y)), D(o.z, u.z)); if (k > 0) Res(site, k, $"({H(o.x)},{H(o.y)},{H(o.z)}) vs ({H(u.x)},{H(u.y)},{H(u.z)})"); }
    public static void V4(int site, Vector4 o, Vector4 u) { Interlocked.Increment(ref _n[site]); int k = Mx(Mx(D(o.x, u.x), D(o.y, u.y)), Mx(D(o.z, u.z), D(o.w, u.w))); if (k > 0) Res(site, k, $"({H(o.x)},{H(o.y)},{H(o.z)},{H(o.w)}) vs ({H(u.x)},{H(u.y)},{H(u.z)},{H(u.w)})"); }
    public static void Q(int site, Quaternion o, Quaternion u) { Interlocked.Increment(ref _n[site]); int k = Mx(Mx(D(o.x, u.x), D(o.y, u.y)), Mx(D(o.z, u.z), D(o.w, u.w))); if (k > 0) Res(site, k, $"({H(o.x)},{H(o.y)},{H(o.z)},{H(o.w)}) vs ({H(u.x)},{H(u.y)},{H(u.z)},{H(u.w)})"); }
    public static void Col(int site, Color o, Color u) { Interlocked.Increment(ref _n[site]); int k = Mx(Mx(D(o.r, u.r), D(o.g, u.g)), Mx(D(o.b, u.b), D(o.a, u.a))); if (k > 0) Res(site, k, $"({H(o.r)},{H(o.g)},{H(o.b)},{H(o.a)}) vs ({H(u.r)},{H(u.g)},{H(u.b)},{H(u.a)})"); }
    public static unsafe void M(int site, Matrix4x4 o, Matrix4x4 u)
    {
        Interlocked.Increment(ref _n[site]); float* a = &o.m00, b = &u.m00; int k = 0, at = -1;
        for (int i = 0; i < 16; i++) { int d = D(a[i], b[i]); if (d > k) { k = d; at = i; } }
        if (k > 0) Res(site, k, $"element {at}: {H(a[at])} vs {H(b[at])}");
    }
    /// <summary>Our rect fields vs a Unity Rect read through its getters.</summary>
    public static void R(int site, in RectF o, Rect u)
    {
        Interlocked.Increment(ref _n[site]); float ox = o.X, oy = o.Y, ow = o.W, oh = o.H, ux = u.x, uy = u.y, uw = u.width, uh = u.height;
        int k = Mx(Mx(D(ox, ux), D(oy, uy)), Mx(D(ow, uw), D(oh, uh))); if (k > 0) Res(site, k, $"({H(ox)},{H(oy)},{H(ow)},{H(oh)}) vs ({H(ux)},{H(uy)},{H(uw)},{H(uh)})");
    }
    /// <summary>Two Unity Rects, both read through Unity's getters.</summary>
    public static void RR(int site, Rect o, Rect u) => R(site, new RectF(o.x, o.y, o.width, o.height), u);
    /// <summary>Two Unity Bounds, both read through Unity's getters.</summary>
    public static void Bnd(int site, Bounds o, Bounds u) { V3(site, o.center, u.center); V3(site, o.extents, u.extents); }
    /// <summary>Two Unity Planes, both read through Unity's getters.</summary>
    public static void Pl(int site, Plane o, Plane u) { V3(site, o.normal, u.normal); F(site, o.distance, u.distance); }

    /// <summary>Our constants and the literal initial values (NTransform's fields, NCanvasRenderer's colour) against Unity's statics / constructors (once).</summary>
    static void Constants()
    {
        V2(Vec, Um.Zero2, Vector2.zero); V3(Vec, Um.Zero3, Vector3.zero); V3(Vec, Um.One3, Vector3.one); Q(Quat, Um.QIdentity, Quaternion.identity);
        Col(White, Um.White, Color.white); M(Identity, Um.Identity, Matrix4x4.identity);
        V2(Vec, Um.V2(0.5f, 0.5f), new Vector2(0.5f, 0.5f)); V2(Vec, Um.V2(100f, 100f), new Vector2(100, 100));
    }
    /// <summary>Once per check run: every Um helper against the Unity method it stands for -- including the ones the menu never runs
    /// (glow centre, near/far clip interpolation, clip-rect corners) -- over special values (+-0, +-inf, quiet / signalling NaNs with
    /// payloads, denormals, extremes) and random bit patterns, bit for bit.</summary>
    static void SelfTest()
    {
        var v = new List<float> { 0f, -0f, 1f, -1f, 0.5f, -0.5f, 2f, 3f, 0.1f, 1f / 3f, 1e-5f, -1e-5f, 9.9999994E-11f, 1e-8f, 1e-20f, 1e20f, 100f, 1280f, 544f,
            float.MaxValue, -float.MaxValue, float.Epsilon, -float.Epsilon, 1.17549435e-38f, -1.17549435e-38f, 5.877472e-39f, float.PositiveInfinity, float.NegativeInfinity };
        foreach (uint bits in new uint[] { 0x7FC00000, 0xFFC00000, 0x7FC12345, 0xFFC54321, 0x7F800001, 0xFF800123, 0x7FFFFFFF, 0xFFFFFFFF }) v.Add(BitConverter.Int32BitsToSingle((int)bits));
        var rnd = new System.Random(20261007);
        for (int i = 0; i < 48; i++) v.Add(BitConverter.Int32BitsToSingle(rnd.Next(int.MinValue, int.MaxValue)));
        for (int i = 0; i < 48; i++) v.Add((float)(rnd.NextDouble() * 2000 - 1000));
        int n = v.Count;
        float X(int i) => v[((i % n) + n) % n];
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
            {
                float a = v[i], b = v[j], t = X(i * 7 + j);
                F(Self, Um.Min(a, b), Mathf.Min(a, b)); F(Self, Um.Max(a, b), Mathf.Max(a, b)); F(Self, Um.LerpUnclamped(a, b, t), Mathf.LerpUnclamped(a, b, t));
                var p = Um.V3(a, b, t); var q = Um.V3(X(i + 3 * j + 1), X(5 * i + j + 2), X(i * j + 3));
                V3(Self, p, new Vector3(a, b, t)); V3(Self, Um.Sub(p, q), p - q); V3(Self, Um.Mid(p, q), (p + q) * 0.5f); V3(Self, Um.Scale(p, q), Vector3.Scale(p, q));
                F(Self, Um.SqrDist(p, q), (p - q).sqrMagnitude);
                var p4 = Um.V4(a, b, t, X(i + j)); var q4 = Um.V4(X(i + 2 * j + 5), X(3 * i + j + 7), X(i + j + 11), X(2 * i + 3 * j)); float u = X(i * 11 + j * 3);
                V4(Self, p4, new Vector4(a, b, t, X(i + j))); V4(Self, Um.Lerp4(p4, q4, u), p4 + (q4 - p4) * u);
                var p2 = Um.V2(a, b); var q2 = Um.V2(X(i + j + 1), X(i * 3 + j)); V2(Self, p2, new Vector2(a, b)); Bool(Self, Um.Eq(p2, q2), p2 == q2); Bool(Self, Um.Eq(p2, p2), p2 == p2);
                Q(Self, Um.Q(a, b, t, u), new Quaternion(a, b, t, u)); Col(Self, Um.C(a, b, t, u), new Color(a, b, t, u));
                var r = new Rect(a, b, t, u); var rf = Um.Of(r);
                R(Self, rf, r); F(Self, rf.XMax, r.xMax); F(Self, rf.YMax, r.yMax); RR(Self, Um.ToRect(new RectF(a, b, t, u)), new Rect(a, b, t, u));
                Bnd(Self, Um.ToBounds(p, q), new Bounds(p, q)); var bf = Um.Of(new Bounds(p, q)); var ub = new Bounds(p, q); V3(Self, bf.C, ub.center); V3(Self, bf.E, ub.extents);
                var pl = new Plane(q, a); var pf = Um.Of(pl); V3(Self, pf.N, pl.normal); F(Self, pf.D, pl.distance);
                if (UmLayout.Ok) { var raw = Um.ToPlaneRaw(p, u); V3(Self, raw.normal, p); F(Self, raw.distance, u); }
            }
    }
    public static string Summary()
    {
        if (!On) return null;
        Constants(); SelfTest();
        long n = 0, bad = 0, nanp = 0; var per = new System.Text.StringBuilder();
        for (int i = 0; i < Count; i++)
        {
            n += _n[i]; bad += _bad[i]; nanp += _nanp[i];
            if (_n[i] > 0) per.Append($"; {Names[i]}: {_n[i]}{(_bad[i] > 0 ? $" ({_bad[i]} DIFFER)" : "")}{(_nanp[i] > 0 ? $" ({_nanp[i]} NaN payload only)" : "")}");
        }
        return $"[hopfix] check: {n} replaced operations compared with the Unity methods over {Runtime.Time.FrameCount} frames, {bad} mismatches, {nanp} NaN-payload-only differences (struct layout mirrors {(UmLayout.Ok ? "ok" : "OFF: " + UmLayout.Why)}){per}";
    }
}
