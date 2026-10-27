using System.Reflection;
using System.Runtime.InteropServices;
using Shepherd.Engine.Native;

namespace Shepherd.Engine.Runtime;

/// <summary>kit #7 spine track, "native clipping" (vita/notes/kit7-spine.md; roadmap kit #7 "Spine: ... native clipping"): Spine's SkeletonClipping
/// -- Esoteric's own docs call clipping very expensive, and on the Vita the game's spine-unity runs interpreted on the Dispatch Thread -- done by a
/// native port of the same code (vita/shepherd/spine_clip_abi.h; the port itself is owner-local, engine/Derived/native, compiled into host.c
/// when present) that runs the same scalar float operations in the same order. The owner's spine-unity.dll rewritten by tools/Rewriter
/// --spine-clip hands ClipStart's clipping polygon (made clockwise in place exactly as the managed code does, so its own list ends the same)
/// and every ClipTriangles call to this class; ClipTriangles' three output lists are then written as the managed loop leaves them (cleared,
/// sized, the same values; the triangle list's capacity through the largest size it reached). The clipper's own scratch lists (clipOutput,
/// scratch, the triangulator's pools) are not used: an allocation-only difference.
/// Measured use (MEASURED-M census): the Main Menu's Lamb has a 14-vertex clipping polygon every frame (ClipStart's triangulation and
/// decomposition each frame, no triangle in its range); the intro's Frog and Spider leaders clip 15 triangles a frame.
/// Switches (env files): SHEPHERD_SPINE_CLIP=0 -- the managed code (kit #6); =check -- the managed results are kept and every call's native
/// results compared bit for bit (counts + float bits), one [spineclip] line per mismatch (first 20) and the totals at the end; unset / 1 =
/// native when the port is linked in (shepherd_spine_clip_api() == 1), else the managed code with one line saying so. Mac: SHEPHERD_SPINE_CLIP_LIB
/// = a dylib built from the same port (kit7 spine-work), without it nothing is installed on the Mac.</summary>
[ProfileOnly]
public static class SpineClipNative
{
    static readonly string Setting = (Environment.GetEnvironmentVariable("SHEPHERD_SPINE_CLIP") ?? "").Trim().ToLowerInvariant();
    static bool _native;   // the port answered (Vita: linked into the eboot; Mac: the test dylib)
    static string _why;
    static long _starts, _startsNative, _tris, _trisIn, _checkStarts, _checkTris, _badStarts, _badTris;
    static int _logged;

    sealed class State { public float[] Polys = new float[64]; public int[] Counts = new int[8]; public int[] Ws = new int[128]; public float[] Copy; public int NPolys = -1; }
    [ThreadStatic] static float[] _v, _uv, _scratch; [ThreadStatic] static int[] _t; [ThreadStatic] static Shepherd.Hooks.ClipResult _res; [ThreadStatic] static int[] _r3;

    public static void Install()
    {
        if (!Profile.Vita || Setting is "0" or "off") { _why = Setting.Length > 0 ? "off (SHEPHERD_SPINE_CLIP=" + Setting + ")" : null; return; }
        try { _native = Probe(); } catch (Exception e) { _native = false; _why = "the port did not answer (" + e.GetType().Name + ")"; }
        if (!_native) { _why ??= "the managed code (no native port in this build)"; return; }
        Shepherd.Hooks.SpineClip.OnStart = Start; Shepherd.Hooks.SpineClip.OnPolygons = Polygons; Shepherd.Hooks.SpineClip.OnTris = Tris;
        Shepherd.Hooks.SpineClip.OnCheckStart = CheckStart; Shepherd.Hooks.SpineClip.OnCheckTris = CheckTris;
        Shepherd.Hooks.SpineClip.Mode = Setting == "check" ? 2 : 1;
        _why = Setting == "check" ? "CHECK mode: the managed results are kept, every call compared with the native port's" : "native port (SHEPHERD_SPINE_CLIP=0 = the managed code, kit #6)";
    }
    public static string ModeLine() => _why == null ? null : "[spineclip] Spine clipping: " + _why + "; a line follows only when the owner's spine-unity.dll is the --spine-clip rewrite and a clipping attachment draws";

    // ---- the port: Vita = linked into the eboot (host.c), Mac = the test dylib ----
    static bool Probe()
    {
        if (Diag.VitaHost.On) return VitaApi() == 1;
#if !SHEPHERD_EBOOT
        var lib = Environment.GetEnvironmentVariable("SHEPHERD_SPINE_CLIP_LIB");
        if (string.IsNullOrEmpty(lib)) { _why = "the managed code (Mac: no SHEPHERD_SPINE_CLIP_LIB)"; return false; }
        var h = NativeLibrary.Load(lib);
        unsafe
        {
            _macApi = (delegate* unmanaged[Cdecl]<int>)NativeLibrary.GetExport(h, "shepherd_spine_clip_api");
            _macStart = (delegate* unmanaged[Cdecl]<float*, int, float*, int, int*, int, int*, int, int>)NativeLibrary.GetExport(h, "shepherd_spine_clip_start");
            _macTris = (delegate* unmanaged[Cdecl]<float*, int, int*, int, float*, float*, int*, int, float*, float*, int, int*, int, float*, int, int*, int>)NativeLibrary.GetExport(h, "shepherd_spine_clip_tris");
            return _macApi() == 1;
        }
#else
        return false;
#endif
    }
#if !SHEPHERD_EBOOT
    static unsafe delegate* unmanaged[Cdecl]<int> _macApi;
    static unsafe delegate* unmanaged[Cdecl]<float*, int, float*, int, int*, int, int*, int, int> _macStart;
    static unsafe delegate* unmanaged[Cdecl]<float*, int, int*, int, float*, float*, int*, int, float*, float*, int, int*, int, float*, int, int*, int> _macTris;
#endif
    [DllImport("__Internal", EntryPoint = "shepherd_spine_clip_api")] static extern int VitaApi();
    [DllImport("__Internal", EntryPoint = "shepherd_spine_clip_start")] static extern unsafe int VitaStart(float* poly, int n, float* outp, int outCap, int* counts, int countsCap, int* ws, int wsCap);
    [DllImport("__Internal", EntryPoint = "shepherd_spine_clip_tris")] static extern unsafe int VitaTris(float* verts, int vlen, int* tris, int tlen, float* uvs, float* polys, int* counts, int npolys, float* outV, float* outUV, int capV, int* outT, int capT, float* scratch, int scratchCap, int* res);
    static unsafe int CStart(float* poly, int n, float* outp, int outCap, int* counts, int countsCap, int* ws, int wsCap)
    {
#if !SHEPHERD_EBOOT
        if (!Diag.VitaHost.On) return _macStart(poly, n, outp, outCap, counts, countsCap, ws, wsCap);
#endif
        return VitaStart(poly, n, outp, outCap, counts, countsCap, ws, wsCap);
    }
    static unsafe int CTris(float* verts, int vlen, int* tris, int tlen, float* uvs, float* polys, int* counts, int npolys, float* outV, float* outUV, int capV, int* outT, int capT, float* scratch, int scratchCap, int* res)
    {
#if !SHEPHERD_EBOOT
        if (!Diag.VitaHost.On) return _macTris(verts, vlen, tris, tlen, uvs, polys, counts, npolys, outV, outUV, capV, outT, capT, scratch, scratchCap, res);
#endif
        return VitaTris(verts, vlen, tris, tlen, uvs, polys, counts, npolys, outV, outUV, capV, outT, capT, scratch, scratchCap, res);
    }

    // ---- ClipStart ----
    static object Start(object state, float[] poly, int n)
    {
        var s = state as State ?? new State();
        Interlocked.Increment(ref _starts);
        s.NPolys = -1;
        if (poly == null || n < 2 || n > poly.Length) return s;   // the managed code (it throws for n < 2, as before)
        int v = n >> 1, slots = Math.Max(v, 1);
        int needOut = slots * (2 * v + 2), needCounts = slots, needWs = 8 * v + slots * (v + 1) + 8;
        if (s.Polys.Length < needOut) s.Polys = new float[Math.Max(needOut, s.Polys.Length * 2)];
        if (s.Counts.Length < needCounts) s.Counts = new int[Math.Max(needCounts, s.Counts.Length * 2)];
        if (s.Ws.Length < needWs) s.Ws = new int[Math.Max(needWs, s.Ws.Length * 2)];
        bool check = Shepherd.Hooks.SpineClip.Mode == 2;
        float[] src = poly;
        if (check) { if (s.Copy == null || s.Copy.Length < n) s.Copy = new float[n]; Array.Copy(poly, s.Copy, n); src = s.Copy; }   // check: the managed code makes the list clockwise itself
        int np;
        unsafe { fixed (float* p = src, o = s.Polys) fixed (int* c = s.Counts, w = s.Ws) np = CStart(p, n, o, s.Polys.Length, c, s.Counts.Length, w, s.Ws.Length); }
        if (np < 0) return s;   // declined: the managed code
        s.NPolys = np;
        if (!check) Interlocked.Increment(ref _startsNative);
        return s;
    }
    static int Polygons(object state) => Shepherd.Hooks.SpineClip.Mode == 1 && state is State s && s.NPolys >= 0 ? s.NPolys : -1;

    // ---- ClipTriangles ----
    static Shepherd.Hooks.ClipResult Tris(object state, float[] vertices, int verticesLength, int[] triangles, int trianglesLength, float[] uvs)
    {
        var s = (State)state;
        Interlocked.Increment(ref _tris); Interlocked.Add(ref _trisIn, trianglesLength / 3);
        var r = Run(s, vertices, triangles, trianglesLength, uvs, out _);
        return r;
    }
    /// <summary>The port over this call's inputs into this thread's buffers (grown and run again when the port asks); the managed code's
    /// IndexOutOfRangeException for an index outside the arrays, thrown here before the port runs (it reads no bounds).</summary>
    static Shepherd.Hooks.ClipResult Run(State s, float[] vertices, int[] triangles, int trianglesLength, float[] uvs, out bool ok)
    {
        ok = false;
        if (vertices == null || triangles == null || uvs == null) throw new NullReferenceException();
        int tl = trianglesLength;
        if (tl > 0)
        {
            // the managed loop reads triangles[i..i+2] for i < trianglesLength (step 3) and vertices / uvs at 2 * index (+1)
            int last = (tl + 2) / 3 * 3; if (last > triangles.Length) throw new IndexOutOfRangeException();
            int lim = Math.Min(vertices.Length, uvs.Length);
            for (int i = 0; i < last; i++) { int k = triangles[i]; if (k < 0 || 2 * (long)k + 1 >= lim) throw new IndexOutOfRangeException(); }
            tl = last;
        }
        var res = _res ??= new Shepherd.Hooks.ClipResult(); var r3 = _r3 ??= new int[3];
        _v ??= new float[256]; _uv ??= new float[256]; _t ??= new int[384]; _scratch ??= new float[256];
        for (int attempt = 0; attempt < 4; attempt++)
        {
            int rc;
            unsafe
            {
                fixed (float* pv = vertices, pu = uvs, pp = s.Polys, ov = _v, ou = _uv, sc = _scratch) fixed (int* pt = triangles, pc = s.Counts, ot = _t, rr = r3)
                    rc = CTris(pv, vertices.Length, pt, trianglesLength, pu, pp, pc, s.NPolys, ov, ou, _v.Length, ot, _t.Length, sc, _scratch.Length, rr);
            }
            if (rc == 0) { res.V = _v; res.UV = _uv; res.T = _t; res.NV = r3[0]; res.NT = r3[1]; res.MaxT = r3[2]; ok = true; return res; }
            if (r3[0] > _v.Length) { _v = new float[r3[0]]; _uv = new float[r3[0]]; }
            if (r3[1] > _t.Length) _t = new int[r3[1]];
            if (r3[2] > _scratch.Length) _scratch = new float[r3[2]];
        }
        throw new InvalidOperationException("spine clip: the native port's buffers did not settle");
    }

    // ---- check mode ----
    static readonly Dictionary<(Type, string), FieldInfo> _f = new();
    static object F(object o, string n)
    {
        var t = o.GetType(); FieldInfo fi;
        lock (_f) if (!_f.TryGetValue((t, n), out fi)) _f[(t, n)] = fi = t.GetField(n, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        return fi.GetValue(o);
    }
    static void CheckStart(object state, object polygons)
    {
        Interlocked.Increment(ref _checkStarts);
        var s = state as State;
        int count = (int)F(polygons, "Count"); var items = (Array)F(polygons, "Items");
        string bad = null;
        if (s == null || s.NPolys < 0) bad = "the port declined or did not run";
        else if (count != s.NPolys) bad = $"{count} managed polygons, {s.NPolys} native";
        else
        {
            int off = 0;
            for (int p = 0; p < count && bad == null; p++)
            {
                var pl = items.GetValue(p); int c = (int)F(pl, "Count"); var fi = (float[])F(pl, "Items");
                if (c != s.Counts[p]) { bad = $"polygon {p}: {c} managed floats, {s.Counts[p]} native"; break; }
                for (int i = 0; i < c; i++) if (BitConverter.SingleToInt32Bits(fi[i]) != BitConverter.SingleToInt32Bits(s.Polys[off + i])) { bad = $"polygon {p} float {i}: managed {fi[i]:R} native {s.Polys[off + i]:R}"; break; }
                off += c;
            }
        }
        if (bad != null) { Interlocked.Increment(ref _badStarts); Note("ClipStart", bad); }
    }
    static void CheckTris(object state, float[] vertices, int verticesLength, int[] triangles, int trianglesLength, float[] uvs, object outV, object outUV, object outT)
    {
        Interlocked.Increment(ref _checkTris); Interlocked.Increment(ref _tris); Interlocked.Add(ref _trisIn, trianglesLength / 3);
        var s = state as State; if (s == null || s.NPolys < 0) { Interlocked.Increment(ref _badTris); Note("ClipTriangles", "no native polygons"); return; }
        Shepherd.Hooks.ClipResult r;
        try { r = Run(s, vertices, triangles, trianglesLength, uvs, out _); }
        catch (Exception e) { Interlocked.Increment(ref _badTris); Note("ClipTriangles", "the native path threw " + e.GetType().Name); return; }
        string bad = Cmp("vertices", outV, r.V, r.NV) ?? Cmp("uvs", outUV, r.UV, r.NV) ?? CmpI("triangles", outT, r.T, r.NT, r.MaxT);
        if (bad != null) { Interlocked.Increment(ref _badTris); Note("ClipTriangles", bad); }
    }
    static string Cmp(string what, object list, float[] nat, int n)
    {
        int c = (int)F(list, "Count"); var a = (float[])F(list, "Items");
        if (c != n) return $"{what}: managed Count {c}, native {n}";
        for (int i = 0; i < n; i++) if (BitConverter.SingleToInt32Bits(a[i]) != BitConverter.SingleToInt32Bits(nat[i])) return $"{what}[{i}]: managed {a[i]:R} native {nat[i]:R}";
        for (int i = n; i < a.Length; i++) if (a[i] != 0f) return $"{what}: managed tail [{i}] = {a[i]:R} (expected 0)";
        return null;
    }
    static string CmpI(string what, object list, int[] nat, int n, int maxT)
    {
        int c = (int)F(list, "Count"); var a = (int[])F(list, "Items");
        if (c != n) return $"{what}: managed Count {c}, native {n}";
        for (int i = 0; i < n; i++) if (a[i] != nat[i]) return $"{what}[{i}]: managed {a[i]} native {nat[i]}";
        if (a.Length < maxT) return $"{what}: managed capacity {a.Length} < the native largest count {maxT}";
        return null;
    }
    static void Note(string where, string what)
    {
        if (Interlocked.Increment(ref _logged) <= 20) Log.Warn($"[spineclip] f{Time.FrameCount} {where} MISMATCH: {what}");
    }

#if !SHEPHERD_EBOOT
    // ---- Mac test access (Diag/SpineClipFuzz.cs) ----
    internal static bool TestLoad(out string why) { _why = null; bool ok; try { ok = Probe(); } catch (Exception e) { ok = false; _why = e.Message; } why = _why; return ok; }
    internal static int TestStart(float[] poly, int n, out float[] polys, out int[] counts)
    {
        var s = (State)Start(null, poly, n); polys = s.Polys; counts = s.Counts; _keep = s; return s.NPolys;
    }
    static State _keep;
    internal static Shepherd.Hooks.ClipResult TestTris(float[] verts, int[] idx, float[] uvs, float[] polys, int[] counts, int np)
    {
        var s = _keep ?? new State(); s.Polys = polys; s.Counts = counts; s.NPolys = np;
        var r = Run(s, verts, idx, idx.Length, uvs, out _);
        return new Shepherd.Hooks.ClipResult { V = (float[])r.V.Clone(), UV = (float[])r.UV.Clone(), T = (int[])r.T.Clone(), NV = r.NV, NT = r.NT, MaxT = r.MaxT };
    }
#endif

    public static void Summary()
    {
        if (_starts == 0 && _checkStarts == 0) return;
        Log.Info($"[spineclip] totals: {_starts} ClipStart ({_startsNative} with the native polygons), {_tris} ClipTriangles calls ({_trisIn} triangles in)" +
                 (Shepherd.Hooks.SpineClip.Mode == 2 ? $"; CHECK: {_checkStarts} ClipStart compared, {_badStarts} differ; {_checkTris} ClipTriangles compared, {_badTris} differ" : ""));
    }
}

#if !SHEPHERD_EBOOT
/// <summary>Mac test access to the port (Diag/SpineClipFuzz.cs): the same Mac binding and buffer logic as the engine's handlers.</summary>
[ProfileOnly]
public static class SpineClipNativeTest
{
    public static bool Load(out string why) => SpineClipNative.TestLoad(out why);
    public static int Start(float[] poly, int n, out float[] polys, out int[] counts) => SpineClipNative.TestStart(poly, n, out polys, out counts);
    public static Shepherd.Hooks.ClipResult Tris(float[] verts, int[] idx, float[] uvs, float[] polys, int[] counts, int np) => SpineClipNative.TestTris(verts, idx, uvs, polys, counts, np);
}
#endif
