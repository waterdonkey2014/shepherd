using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Shepherd.Engine.Native;
using UnityEngine;
using static Shepherd.Engine.Render.Sdl;
namespace Shepherd.Engine.Render;

// menu-speed, stage "multicore render-prep pipeline" (PLAN V10b; vita/notes/engine-pass-speed.md section 14). SHEPHERD_MT_RENDER=1, default off.
//
// Frame N is recorded on the main thread exactly as before -- layout, NotifyRectChanges, the uGUI rebuild (game code), the world pass's
// scan (registry walk, property reads, culling, sort keys, sort), the FX policy, texture handles, the Stylizer's parameter lookup and the UI
// pass all run where and when they ran -- but the backend calls go into a command list (MtBackend, the proxy SceneRenderer.G points at), and
// each screen-camera world draw is captured as its inputs instead of being built: the sprite's geometry arrays (the engine only ever
// replaces them; a sprite whose arrays game code holds is copied), a copy of the mesh's channel arrays (Spine rewrites them in place every
// frame), the world matrix, view-projection, pixel rect, tint, blend and the texture handle (taken now when taking it has no side effects;
// otherwise the draw is built now, in order). At the end of the frame the list goes to the render thread, which builds those draws (the same
// WorldEmitter code: projection, colour, uv, index filter, near/far clipping) while the main thread runs frame N+1's logic. The next frame's
// render starts by waiting for it (FrameStart).
//
// Who calls the backend (SHEPHERD_MT_SUBMIT, default by backend):
// - worker (the null backend): the render thread also replays the whole list into the backend (its batching, vertex copy, index
//   rebasing, Stylizer pass, end of frame) -- the PLAN's shape, a render thread that owns the GPU API (vitaGL on USER_1, after the device test)
// - main (SDL and GL on the Mac: SDL3 says SDL_GL_MakeCurrent / SDL_GL_SwapWindow / SDL_RenderGeometry "should only be called on the main
//   thread"): the render thread only builds the vertices into the list; the main thread hands the finished list to the API at FrameStart
// Any call that needs the backend's answer or its pixels now (CreateTexture / CreateTarget / the compressed and alpha8 uploads, ReadPixels,
// Screenshot, Finish, SDL's CPU Stylizer) first waits for the render thread and runs everything recorded before it, in order, then itself:
// the backend sees exactly the single-threaded call sequence (SHEPHERD_MT_CHECK=1 proves it per frame, Render/MtCheck.cs).
//
// Invariants: nothing the render thread reads is written by the main thread before FrameStart (the captured arrays are the list's own or
// never written in place -- the first time game code is handed a sprite's arrays, NSprite.Share waits for the render thread; the replay's
// WorldEmitter, the list being replayed and, submit=worker, the backend belong to the render thread until FrameStart); the render thread runs
// no game code and touches no engine record. LastDraws / LastTris / LastClipTris / LastDropTris and the SHEPHERD_STREAM_HASH line of frame N
// are completed by the replay (read them after a sync point: Screenshot does one).
// Exceptions: one when a recorded call runs (a captured build, or the backend) is logged and drops the rest of the SceneRenderer.Frame try
// block it was recorded in (a screen camera's DrawWorld, the Stylizer, the UI pass), as the single-threaded pass stops there; the calls after
// it run. What the main thread did while recording the rest of that block (counters, texture uploads for draws it built itself) has happened.
// The vertex builds have no throwing path for the inputs captured (non-null arrays; every index and channel access is bounds-checked).
// Memory: the commands, the captures' values and the copies of the UI pass's vertices (and of the draws built on the main thread) are native,
// per list (CmdList), outside the GC heap; the managed part is the captures' reference slots, the mesh snapshots (Pool) and the replay's build
// buffers, ~100 KiB at the Main Menu (the [mt] window memory line).
// One frame of latency: frame N reaches the backend while frame N+1's logic runs (submit=worker), or at frame N+1's render (submit=main);
// the sequence of images is unchanged. Unity's own multithreaded rendering has the same one-frame offset.
[ProfileOnly]
public static class MtRender
{
    /// <summary>SHEPHERD_MT_RENDER=1: the pipeline (default off until the owner's device test).</summary>
    public static readonly bool Requested = Environment.GetEnvironmentVariable("SHEPHERD_MT_RENDER") == "1";
    /// <summary>SHEPHERD_MT_CHECK=1 (check runs only; slow): MtCheck compares the calls the backend received with the single-threaded sequence.</summary>
    public static readonly bool CheckOn = Requested && Environment.GetEnvironmentVariable("SHEPHERD_MT_CHECK") == "1";
    /// <summary>SHEPHERD_MT_CPU=1 (macOS): per-thread CPU time over the SimStats window ([mt] lines; also with the pipeline off).</summary>
    public static readonly bool CpuOn = Environment.GetEnvironmentVariable("SHEPHERD_MT_CPU") == "1" && OperatingSystem.IsMacOS();
    /// <summary>The proxy while the pipeline runs (null: single-threaded rendering, the default).</summary>
    internal static MtBackend Cur;

    /// <summary>Boot: the backend SceneRenderer draws through. The pipeline wraps the null, GL and SDL backends (not compare mode).</summary>
    public static IRenderBackend Wrap(IRenderBackend g)
    {
        if (!Requested || g == null) return g;
        if (g is not (NullBackend or GlBackend or Gfx)) { Log.Info($"[mt] SHEPHERD_MT_RENDER=1: not for the '{g.Name}' backend (compare mode); rendering stays single-threaded"); return g; }
        // who calls the backend: the render thread where the API allows it -- the null backend, and GL on the shepgl host (vitaGL: one global
        // context, usable from any thread one at a time; the Mac rehearsal's CGL context is made current on the render thread) -- else the main
        // thread (SDL3's GL context and SDL_Renderer: "should only be called on the main thread")
        var env = Environment.GetEnvironmentVariable("SHEPHERD_MT_SUBMIT");
        bool can = g is NullBackend || (g is GlBackend && ShepGl.On);
        bool worker = can && env != "main";
        if (env == "worker" && !can) Log.Info($"[mt] SHEPHERD_MT_SUBMIT=worker is not available for '{g.Name}' here (SDL3: its GL context and renderer are main-thread only): the main thread submits");
        var m = g is IGpuStylizer ? new MtBackendGpu(g, worker) : new MtBackend(g, worker);
        Cur = m;
        Log.Info($"[mt] multicore render pipeline ON: backend {g.Name}, the render thread builds the world vertices{(worker ? " and runs the backend" : "; the main thread submits to the backend")}; one frame of latency" + (CheckOn ? "; SHEPHERD_MT_CHECK=1" : ""));
        return m;
    }

    /// <summary>The real backend behind <paramref name="g"/> (no sync: for read-only counters on the main thread).</summary>
    public static IRenderBackend Inner(IRenderBackend g) => g is MtBackend m ? m.R : g;
    /// <summary>Before the main thread reads the backend's own state (a heartbeat's texture census, vitaGL's pools): with submit=worker the
    /// render thread may be using it, so wait for it (a sync point).</summary>
    public static void SyncForRead() { if (Cur is { WorkerSubmits: true } m) m.Sync(); }

    // ---------------- per-thread CPU (SHEPHERD_MT_CPU=1, macOS) and the window summary ----------------
    [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "clock_gettime_nsec_np")] static extern ulong ClockNs(uint clock);
    const uint CLOCK_THREAD_CPUTIME_ID = 16;   // macOS <time.h>
    [MethodImpl(MethodImplOptions.NoInlining)] internal static long ThreadCpuNs() => CpuOn ? (long)ClockNs(CLOCK_THREAD_CPUTIME_ID) : 0;
    static long _winMainCpu0, _winFrames;
    /// <summary>SimStats: the window's first frame starts.</summary>
    public static void WindowStart() { _winMainCpu0 = ThreadCpuNs(); Cur?.WindowStart(); _winFrames = 0; }
    public static void WindowFrame() => _winFrames++;
    /// <summary>SimStats: the window's last frame ended: [mt] lines.</summary>
    public static void WindowEnd()
    {
        long n = Math.Max(1, _winFrames);
        double mainCpu = CpuOn ? (ThreadCpuNs() - _winMainCpu0) / 1e6 / n : double.NaN;
        if (Cur != null) Cur.WindowEnd(n, mainCpu);
        else if (CpuOn) Log.Info($"[mt] window ({n} frames): single-threaded rendering; CPU main thread {mainCpu:F3} ms/frame");
    }

    /// <summary>The replay's sink: a built world draw goes to the backend (direct) or into the list (submit=main).</summary>
    internal sealed class Sink
    {
        internal MtBackend B; internal CmdList L; internal int I; internal bool Direct;
        /// <summary>SHEPHERD_MT_CHECK=1: the single-threaded reference build of a captured draw, recorded into L.Ref with this texture handle.</summary>
        internal bool Capture; internal IntPtr CapTex;
        public void Draw(Vertex[] v, int nv, int[] ib, int ni, uint blend, bool sdf)
        {
            if (ni - ni % 3 < 3) return;   // SubmitIdx's early-out (whole triangles only)
            ni -= ni % 3;
            var vs = new Span<Vertex>(v, 0, nv); var ix = new Span<int>(ib, 0, ni);
            if (Capture) { L.Ref.Add(MtCheck.Draw, CapTex, blend, sdf, v: vs, idx: ix); return; }
            ref var c = ref L[I];
            if (Direct) B.Call(L, MtCheck.Draw, c.Tex, blend, sdf, vs, ix);
            else { c.Out = true; c.A = L.PutV(vs); c.B = nv; c.C = L.PutI(ix); c.D = ni; }
            SceneRenderer.LastDraws++; SceneRenderer.LastTris += ni / 3;
            if (c.Live >= 0) L.Live.Fill(c.Live, c.Tex, blend, sdf, vs, ix);
        }
    }

    internal enum K : byte { Begin, Clear, Clip, NoClip, Target, Draw, Destroy, EndWorld, Stylize, EndFrame, Present, Sprite, Mesh, Glow, Dropped, DrawAdd, Lut, GpuMesh, GpuRelease }

    /// <summary>One recorded backend call or captured world draw (48 bytes, native). No references: a captured draw's inputs are in
    /// <see cref="Cap"/> and its arrays in <see cref="CmdList.Refs"/>.</summary>
    internal struct Cmd
    {
        public K K; public bool Sdf, Out; public uint Blend; public IntPtr Tex;
        /// <summary>Clip: x, y, w, h. Begin / Clear: the colour (float bits). Draw, and a captured draw built for the main thread to submit
        /// (Out): vertex offset / count, index offset / count in the list's arena. EndFrame: frame, the main thread's clip / drop counters, hash (1).</summary>
        public int A, B, C, D;
        /// <summary>The draw's slot in the frame's stream-hash recorder (-1: none). X: a captured draw's inputs (CmdList.Cap / Refs index).</summary>
        public int Live, X;
        /// <summary>The try block of SceneRenderer.Frame the call was made in (a screen camera's DrawWorld, the Stylizer, the UI pass; 0: none):
        /// an exception in it drops the rest of that block's calls, as the single-threaded pass would have stopped there.</summary>
        public int Scope;
        /// <summary>Integration with fidelity round 1: DrawAdd: the added colours' count (their offset in the list's colour arena is X). Lut: the
        /// answer RunLut gave the main thread (1 = true; the replay checks the backend's own answer against it).</summary>
        public int E;
    }
    /// <summary>A captured world draw's inputs, apart from its arrays (native, with the commands; the arrays are in CmdList's reference
    /// slots: sprite verts, uvs, tris / mesh pos, col, uv, idx / glow pos, nrm, idx).</summary>
    internal struct Cap
    {
        public Matrix4x4 Lw; public Color Tint; public Vector3 Cam, CMin, CMax; public bool Fx, Fy, SkipZero; public int Pass;   // (SkipZero: kit #7 spine, a mesh draw's colour-0 triangles may leave it)
    }
    /// <summary>A capture's arrays (sprite verts, uvs, tris / mesh pos, col, uv, idx / glow pos, nrm, idx), in the order the capture methods
    /// store them (the replay reads them back as those types).</summary>
    internal struct Ref4 { public object A, B, C, D; }
    /// <summary>A camera pass's view-projection, pixel rect and target height (its captured draws hold its index).</summary>
    internal struct PassData { public Matrix4x4 Vp; public RectF Pr; public float Gh; }

    /// <summary>A frame's command list (two of them alternate: one recorded, one replayed) with its arena of copied vertices / indices and
    /// the snapshot arrays it holds.
    /// Memory: the arena (the UI pass's draws and the world draws built on the main thread are copied into it: their buffers are reused at
    /// once) is native memory, outside the GC heap: a screen that hands the backend ~1.3 MB of UI vertices a frame (Credits) grows it, and
    /// growing it, and giving the memory back after the peak, cost no garbage-collector work (managed arrays there were large-object-space
    /// allocations that brought a major collection forward). The commands and the captures' values are native too; the managed part is the
    /// captures' reference slots (small chunks, grown on demand, kept) and the mesh snapshots (Pool): ~100 KiB at the Main Menu.</summary>
    internal sealed unsafe class CmdList
    {
        /// <summary>The commands (native, CLen allocated, N used).</summary>
        Cmd* C; int CLen; public int N, Done, JobFrom;
        public ref Cmd this[int i] => ref C[i];
        /// <summary>The captures (native: WLen allocated, NW used) and their arrays (Ref4, in chunks of 32 captures: 1 KB each, small objects -- a
        /// few long-lived arrays in the large-object space keep its 1 MB sections from being given back), allocated on demand and kept.</summary>
        Cap* W; int WLen; public int NW;
        Ref4[][] _wr = new Ref4[16][]; int _wrN;
        public ref Cap Cap(int x) => ref W[x];
        /// <summary>Capture x's arrays.</summary>
        public ref Ref4 Refs(int x) => ref _wr[x >> 5][x & 31];
        public PassData[] P = new PassData[4]; public int NP;
        public readonly List<GlBackend.StylizeArgs> Sty = new();
        /// <summary>The grading passes recorded (RunLut's arguments; fidelity round 1, owner-local post effects).</summary>
        public readonly List<LutArgs> Lut = new();
        /// <summary>The arena (native): VLen vertices / ILen indices allocated, NV / NI used.</summary>
        public Vertex* V; public int VLen, NV;
        public int* I; public int ILen, NI;
        /// <summary>The added colours of the lit draws (IAddDraw.DrawAdd; fidelity round 1): FLen allocated, NF used (at most one per copied vertex).</summary>
        public FColor* F; public int FLen, NF;
        /// <summary>What a list may hold before it is run up to that point and its arrays reused (MtBackend.Spill): 4096 commands, 65536
        /// copied vertices (2 MiB), 196608 indices -- a bound for pathological frames, above every menu screen (the Credits screen's ~1.3 MB
        /// of UI vertices a frame is ~40K vertices). A spill is a sync point: that frame's draws reach the backend early, in order, and its
        /// overlap is lost (a 4096-vertex cap spilled on most menu-tour frames and made the tour slower than single-threaded; 16384 still
        /// spilled ~2.4 times per Credits frame). After a peak, Shrink gives the arena's memory back.</summary>
        public const int CCap = 4096, VCap = 65536, ICap = 196608;
        const int CMin = 1024, WMin = 256, VMin = 1024, IMin = 4096, FMin = 64;
        /// <summary>The highest NW since the list was last retired (a spill restarts it at 0: Retire clears up to here).</summary>
        public int HighW;
        public RenderCheck.Rec Live;
        public MtCheck.Rec Ref, Exec;
        /// <summary>The snapshot arrays this list holds (returned to their pool when it is retired).</summary>
        public readonly Pool<Vector2>.Held H2 = new(); public readonly Pool<ushort>.Held HU = new(); public readonly Pool<Vector3>.Held H3 = new();
        public readonly Pool<Color32>.Held HC = new(); public readonly Pool<Vector4>.Held H4 = new(); public readonly Pool<int>.Held HI = new();
        public bool Ended;
        public int Frame = -1, Emits;
        /// <summary>An exception dropped the rest of this scope (Cmd.Scope): in the build (captured draws) / in the backend calls.</summary>
        public int BuildFailed, BackFailed;
        /// <summary>The most the arena held since it was last sized, and how many times the list was retired since (Shrink).</summary>
        public int PeakV, PeakI, PeakF, Uses;
        /// <summary>The list's largest native size so far (commands, captures, arena; bytes; the [mt] lines).</summary>
        public long ArenaHigh;

        public CmdList()
        {
            C = (Cmd*)NativeMemory.Alloc((nuint)CMin, (nuint)sizeof(Cmd)); CLen = CMin;
            W = (Cap*)NativeMemory.Alloc((nuint)WMin, (nuint)sizeof(Cap)); WLen = WMin;
            V = (Vertex*)NativeMemory.Alloc((nuint)VMin, (nuint)sizeof(Vertex)); VLen = VMin;
            I = (int*)NativeMemory.Alloc((nuint)IMin, sizeof(int)); ILen = IMin;
            F = (FColor*)NativeMemory.Alloc((nuint)FMin, (nuint)sizeof(FColor)); FLen = FMin;
            ArenaHigh = ArenaBytes;
        }
        /// <summary>Native memory held: commands, captures, arena.</summary>
        public long ArenaBytes => (long)CLen * sizeof(Cmd) + (long)WLen * sizeof(Cap) + (long)VLen * sizeof(Vertex) + (long)ILen * sizeof(int) + (long)FLen * sizeof(FColor);
        /// <summary>MtBackend.Dispose, once the render thread has stopped.</summary>
        public void Free() { NativeMemory.Free(C); NativeMemory.Free(W); NativeMemory.Free(V); NativeMemory.Free(I); NativeMemory.Free(F); C = null; W = null; V = null; I = null; F = null; CLen = WLen = VLen = ILen = FLen = 0; }
        public Span<Vertex> Vs(int at, int n) => new(V + at, n);
        public Span<int> Is(int at, int n) => new(I + at, n);
        public Span<FColor> Fs(int at, int n) => new(F + at, n);
        /// <summary>At a spill and when the list is retired: the arena's peak for Shrink.</summary>
        public void NotePeak() { PeakV = Math.Max(PeakV, NV); PeakI = Math.Max(PeakI, NI); PeakF = Math.Max(PeakF, NF); }
        /// <summary>After a peak (the Credits screen hands the UI pass ~1.3 MB of vertices a frame) the arena shrinks back once it has stayed
        /// under a quarter full for 120 uses, so the pipeline does not keep a screen's worth of copies for the rest of the run.</summary>
        public void Shrink()
        {
            NotePeak();
            if (++Uses < 120) return;
            if (VLen > VMin && PeakV * 4 < VLen) ResizeV(Math.Max(VMin, PeakV * 2));
            if (ILen > IMin && PeakI * 4 < ILen) ResizeI(Math.Max(IMin, PeakI * 2));
            if (FLen > FMin && PeakF * 4 < FLen) ResizeF(Math.Max(FMin, PeakF * 2));
            Uses = PeakV = PeakI = PeakF = 0;
        }
        /// <summary>The arena's next size: a quarter more (not double: the Credits screen's ~40K vertices would take 2 MiB a list), in steps of
        /// 4096 elements.</summary>
        static int Grow(int len, int need) => (int)((Math.Max(need, len + len / 4) + 4095L) & ~4095L);
        void ResizeV(int n) { V = (Vertex*)NativeMemory.Realloc(V, (nuint)n * (nuint)sizeof(Vertex)); VLen = n; ArenaHigh = Math.Max(ArenaHigh, ArenaBytes); }
        void ResizeI(int n) { I = (int*)NativeMemory.Realloc(I, (nuint)n * sizeof(int)); ILen = n; ArenaHigh = Math.Max(ArenaHigh, ArenaBytes); }
        void ResizeF(int n) { F = (FColor*)NativeMemory.Realloc(F, (nuint)n * (nuint)sizeof(FColor)); FLen = n; ArenaHigh = Math.Max(ArenaHigh, ArenaBytes); }
        public ref Cmd Add(K k)
        {
            if (N == CLen) { C = (Cmd*)NativeMemory.Realloc(C, (nuint)CLen * 2 * (nuint)sizeof(Cmd)); CLen *= 2; ArenaHigh = Math.Max(ArenaHigh, ArenaBytes); }
            ref var c = ref C[N++]; c = default; c.K = k; c.Live = -1; return ref c;
        }
        /// <summary>A captured draw's inputs (Cmd.X = its index).</summary>
        public ref Cap AddCap(out int x, object r0, object r1, object r2, object r3)
        {
            if (NW == WLen) { W = (Cap*)NativeMemory.Realloc(W, (nuint)WLen * 2 * (nuint)sizeof(Cap)); WLen *= 2; ArenaHigh = Math.Max(ArenaHigh, ArenaBytes); }
            if (NW == _wrN * 32)
            {
                if (_wrN == _wr.Length) Array.Resize(ref _wr, _wrN * 2);
                _wr[_wrN++] = new Ref4[32]; MtRender.ManagedGrowth += 32L * 4 * IntPtr.Size;
            }
            x = NW++; ref var r = ref _wr[x >> 5][x & 31]; r.A = r0; r.B = r1; r.C = r2; r.D = r3;
            return ref W[x];
        }
        /// <summary>The managed memory this list holds: capture chunks, snapshot arrays (the [mt] lines).</summary>
        public long CapBytes => (long)_wrN * 32 * 4 * IntPtr.Size;
        public long SnapBytes => Pool<Vector2>.Bytes(H2) + Pool<ushort>.Bytes(HU) + Pool<Vector3>.Bytes(H3) + Pool<Color32>.Bytes(HC) + Pool<Vector4>.Bytes(H4) + Pool<int>.Bytes(HI);
        /// <summary>Retire: the captures' references dropped (keep nothing alive).</summary>
        public void ClearCaps() { int n = Math.Max(NW, HighW); for (int k = 0; k * 32 < n; k++) Array.Clear(_wr[k], 0, Math.Min(32, n - k * 32)); HighW = 0; }
        public int PutV(ReadOnlySpan<Vertex> v)
        {
            if (NV + v.Length > VLen) ResizeV(Grow(VLen, NV + v.Length));
            v.CopyTo(new Span<Vertex>(V + NV, v.Length)); int o = NV; NV += v.Length; return o;
        }
        public int PutI(ReadOnlySpan<int> v)
        {
            if (NI + v.Length > ILen) ResizeI(Grow(ILen, NI + v.Length));
            v.CopyTo(new Span<int>(I + NI, v.Length)); int o = NI; NI += v.Length; return o;
        }
        public int PutF(ReadOnlySpan<FColor> v)
        {
            if (NF + v.Length > FLen) ResizeF(Grow(FLen, NF + v.Length));
            v.CopyTo(new Span<FColor>(F + NF, v.Length)); int o = NF; NF += v.Length; return o;
        }
    }

    /// <summary>Main thread counters for the [mt] window lines: bytes of managed arrays the pipeline allocated (capture reference chunks, new
    /// snapshot arrays), bytes copied into snapshot arrays.</summary>
    internal static long ManagedGrowth, SnapBytes;

    // ---------------- snapshot arrays: exact-length arrays, reused from frame to frame (main thread only) ----------------
    internal static Vector2[] Take(Vector2[] a, CmdList L) => Pool<Vector2>.Take(a, L.H2);
    internal static ushort[] Take(ushort[] a, CmdList L) => Pool<ushort>.Take(a, L.HU);
    internal static Vector3[] Take(Vector3[] a, CmdList L) => Pool<Vector3>.Take(a, L.H3);
    internal static Color32[] Take(Color32[] a, CmdList L) => Pool<Color32>.Take(a, L.HC);
    internal static Vector4[] Take(Vector4[] a, CmdList L) => Pool<Vector4>.Take(a, L.H4);
    internal static int[] Take(int[] a, CmdList L) => Pool<int>.Take(a, L.HI);
    internal static void Give(CmdList L) { Pool<Vector2>.Give(L.H2); Pool<ushort>.Give(L.HU); Pool<Vector3>.Give(L.H3); Pool<Color32>.Give(L.HC); Pool<Vector4>.Give(L.H4); Pool<int>.Give(L.HI); }
}

/// <summary>A command list's snapshot arrays of one element type, reused by position: the k-th copy a list takes reuses the k-th array it took
/// the last time it was recorded when the length is the same (the same meshes are captured in the same order frame after frame), so the
/// steady state allocates nothing and looks nothing up. A list's arrays are only written while it is recorded (main thread), after the render
/// thread is done with it.</summary>
[ProfileOnly]
internal static class Pool<T>
{
    internal sealed class Held { public readonly List<T[]> A = new(); public int N; }
    public static T[] Take(T[] src, Held held)
    {
        int n = src.Length, k = held.N++; var l = held.A; T[] a;
        if (k < l.Count && l[k].Length == n) a = l[k];
        else { a = new T[n]; if (k < l.Count) l[k] = a; else l.Add(a); MtRender.ManagedGrowth += (long)n * Unsafe.SizeOf<T>(); }
        Array.Copy(src, a, n); MtRender.SnapBytes += (long)n * Unsafe.SizeOf<T>(); return a;
    }
    public static long Bytes(Held held) { long b = 0; foreach (var a in held.A) b += (long)a.Length * Unsafe.SizeOf<T>(); return b; }
    public static void Give(Held held)
    {
        var l = held.A;
        if (l.Count > held.N * 2 + 16) l.RemoveRange(held.N * 2 + 16, l.Count - (held.N * 2 + 16));   // (the scene shrank: let the extra arrays go)
        held.N = 0;
    }
}

/// <summary>The proxy backend of the multicore pipeline (see the notes at the top of this file).</summary>
[ProfileOnly]
internal class MtBackend : IRenderBackend
{
    internal readonly IRenderBackend R;
    /// <summary>True: the render thread calls the backend. False: it builds the world vertices only; the main thread calls the backend.</summary>
    internal readonly bool WorkerSubmits;
    readonly IWorldPassSplit _split;
    MtRender.CmdList _rec = new(), _busy, _spare = new();
    readonly WorldEmitter _rep = new(128, 512, 64);   // (small to start: under the large-object threshold; they grow to the largest world draw)
    readonly MtRender.Sink _sink;
    readonly Thread _thread;
    readonly ManualResetEventSlim _go = new(false, 1), _done = new(true);
    volatile bool _quit;
    Exception _jobError, _callError;
    Action _call;   // a call the render thread makes for the main thread (submit=worker: it is the only thread that calls the backend)
    readonly RenderStats _stats = new();
    readonly Queue<(int frame, int din, int dout)> _frameStats = new();
    int _pass, _scope, _scopes;
    // window counters (main thread; the worker's are written before _done is set and read after the wait)
    long _waitTicks, _waitCount, _syncs, _workerTicks, _workerCpuNs, _jobs, _deferred, _declined, _copiedSprites, _spills, _copiedV, _copiedI, _arenaHigh;
    /// <summary>Kit #7 (Render/K7Overlay.cs): the render thread's busy time and the main thread's waits for it so far, Stopwatch ticks.</summary>
    internal long WorkerTicksK7 => _workerTicks; internal long WaitTicksK7 => _waitTicks;
    long _w0Wait, _w0Count, _w0Syncs, _w0WorkerTicks, _w0WorkerCpu, _w0Jobs, _w0Deferred, _w0Declined, _w0Copied, _w0Spills, _w0CopiedV, _w0CopiedI, _w0Growth, _w0Snap;

    // integration with fidelity round 1: RunLut (the owner-local grading pass) must answer when it is recorded; the backend refuses it only
    // without its program (GL: a constant from construction) or when a LUT handle is not one of its textures. The proxy sees every handle
    // the backend hands out and every DestroyTexture, in call order, so it keeps that set itself and answers exactly; the replay compares the
    // backend's own answer with it (a difference: an [mt] line, and a mismatch under SHEPHERD_MT_CHECK=1).
    readonly HashSet<IntPtr> _handles = new();
    readonly bool _lutProg;
    long _lutWrong;
    IntPtr Handed(IntPtr h) { if (h != IntPtr.Zero) _handles.Add(h); return h; }

    internal MtBackend(IRenderBackend r, bool workerSubmits)
    {
        R = r; WorkerSubmits = workerSubmits; _split = r as IWorldPassSplit;
        _lutProg = r is GlBackend gl ? gl.HasLutProgram : r is IGpuPost;
        _sink = new MtRender.Sink { B = this };
        _rep.Rx = _sink;
        if (MtRender.CheckOn) { _rec.Ref = new(); _rec.Exec = new(); _spare.Ref = new(); _spare.Exec = new(); }
        _thread = new Thread(Loop, RenderStackBytes()) { IsBackground = true, Name = "shepherd-render" };
#if !SHEPHERD_EBOOT
        Shepherd.Assets.Sched.Spawn("shepherd-render");   // (kit #7 tests: the seeded-schedule hooks, Mac only; Shepherd.Assets/Sched.cs)
#endif
        _thread.Start();
        if (workerSubmits && r is GlBackend && ShepGl.On) OnApi(AttachGl);   // the GL context moves to the render thread (vitaGL: any thread, one at a time; the Mac rehearsal's CGL context: current per thread)
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    static void AttachGl() { if (!ShepGl.Attach()) Log.Warn("[mt] shepgl_attach failed on the render thread"); }
    /// <summary>kit #5 review (track mem): under the Vita host this thread asks for kit #4's 1 MiB stack itself. Kit #5's M3 made 256 KiB the
    /// size of Mono's threads that ask for none; this one runs all of real vitaGL / GXM in the MT apps (draws, texture uploads, the GXM
    /// shader patcher building fragment programs, swaps), a depth vlx has measured only with its GL recorder standing in. The host's STK
    /// lines report its deepest use on the device. SHEPHERD_RENDER_STACK_KB=&lt;KiB&gt; (64..8192; 0 = the runtime's default) overrides; the
    /// Mac keeps the runtime's default (0), as before.</summary>
    /// Kit #6 integration (host.c "M3 stack shrink from the device STK lines"): kit #5's device run measured this thread's deepest use at
    /// 9 KiB of its 1 MiB with real vitaGL (STK end, 4 MT starts); it now asks for 512 KiB (9.7x the 53 KiB SceShaccCg took on the Mono
    /// thread, should a GL program -- the gpulight sphere program, built on first use -- compile here). SHEPHERD_STACKS=kit5 / kit4 =
    /// kit #5's / kit #4's 1 MiB.
    static int RenderStackBytes()
    {
        if (!Diag.VitaHost.On) return 0;
        var e = Environment.GetEnvironmentVariable("SHEPHERD_RENDER_STACK_KB");
        if (e is { Length: > 0 } && int.TryParse(e, out var kb) && (kb == 0 || kb is >= 64 and <= 8192)) return kb * 1024;
        var st = Environment.GetEnvironmentVariable("SHEPHERD_STACKS");
        return st == "kit5" || st == "kit4" ? 1 << 20 : 512 << 10;
    }

    // ---------------- IRenderBackend: recorded calls ----------------
    public string Name => R.Name;
    public int W => R.W;
    public int H => R.H;
    /// <summary>The counters of the last frame the backend completed, as of the last sync point (main thread).</summary>
    public RenderStats Stats => _stats;
    public void Begin(float r, float g, float b, float a) { Colour(ref Rec(MtRender.K.Begin), r, g, b, a); Ref(MtCheck.Begin, colour: (r, g, b, a)); }
    public void ClearTarget(float r, float g, float b, float a) { Colour(ref Rec(MtRender.K.Clear), r, g, b, a); Ref(MtCheck.Clear, colour: (r, g, b, a)); }
    /// <summary>Begin / ClearTarget: the colour in the command's A..D (float bits, exact).</summary>
    static void Colour(ref MtRender.Cmd c, float r, float g, float b, float a) { c.A = BitConverter.SingleToInt32Bits(r); c.B = BitConverter.SingleToInt32Bits(g); c.C = BitConverter.SingleToInt32Bits(b); c.D = BitConverter.SingleToInt32Bits(a); }
    static (float, float, float, float) Colour(in MtRender.Cmd c) => (BitConverter.Int32BitsToSingle(c.A), BitConverter.Int32BitsToSingle(c.B), BitConverter.Int32BitsToSingle(c.C), BitConverter.Int32BitsToSingle(c.D));
    public void Clip(int x, int y, int w, int h) { ref var c = ref Rec(MtRender.K.Clip); c.A = x; c.B = y; c.C = w; c.D = h; Ref(MtCheck.Clip, x, y, w, h); }
    public void NoClip() { Rec(MtRender.K.NoClip); Ref(MtCheck.NoClip); }
    public void SetTarget(IntPtr target) { ref var c = ref Rec(MtRender.K.Target); c.Tex = target; Ref(MtCheck.Target, tex: target); }
    public void DestroyTexture(IntPtr tex) { ref var c = ref Rec(MtRender.K.Destroy); c.Tex = tex; Ref(MtCheck.Destroy, tex: tex); _handles.Remove(tex); }
    public void Draw(IntPtr tex, uint blend, bool sdf, Span<Vertex> v, Span<int> idx)
    {
        if ((_rec.NV + v.Length > MtRender.CmdList.VCap || _rec.NI + idx.Length > MtRender.CmdList.ICap) && _rec.NV + _rec.NI > 0) Spill();
        ref var c = ref Rec(MtRender.K.Draw); var L = _rec; c.Tex = tex; c.Blend = blend; c.Sdf = sdf;
        c.A = L.PutV(v); c.B = v.Length; c.C = L.PutI(idx); c.D = idx.Length;
        _copiedV += v.Length; _copiedI += idx.Length;
        if (L.Ref != null) L.Ref.Add(MtCheck.Draw, tex, blend, sdf, v: v, idx: idx);
    }
    public void EndWorldPass()
    {
        if (_split != null && this is IGpuStylizer gs)
        {   // the flush and the counters are recorded; the owner-local post effects' and Stylizer's lookups (live component fields, LUT uploads)
            // run now, their passes (RunLut, RunStylizer) are recorded -- the order of the backends' own EndWorldPass
            Rec(MtRender.K.EndWorld); Ref(MtCheck.EndWorld);
            if (this is IGpuPost gp) DerivedRender.Post?.ApplyGpu(gp);
            DerivedRender.Stylizer?.ApplyGpu(gs);
        }
        else { Ref(MtCheck.EndWorldFull); Sync(); Exec(MtCheck.EndWorldFull); R.EndWorldPass(); }   // SDL: the CPU Stylizer reads the world pass back now
    }
    /// <summary>IAddDraw (MtBackendGpu): a lit draw, recorded with copies of its vertices, indices and added colours.</summary>
    protected void DrawAddRec(IntPtr tex, uint blend, Span<Vertex> v, Span<int> idx, Span<FColor> add)
    {
        if ((_rec.NV + v.Length > MtRender.CmdList.VCap || _rec.NI + idx.Length > MtRender.CmdList.ICap) && _rec.NV + _rec.NI > 0) Spill();
        ref var c = ref Rec(MtRender.K.DrawAdd); var L = _rec; c.Tex = tex; c.Blend = blend;
        c.A = L.PutV(v); c.B = v.Length; c.C = L.PutI(idx); c.D = idx.Length; c.X = L.PutF(add); c.E = add.Length;
        _copiedV += v.Length; _copiedI += idx.Length;
        if (L.Ref != null) L.Ref.Add(MtCheck.DrawAdd, tex, blend, false, v: v, idx: idx, add: add);
    }
    /// <summary>IGpuPost (MtBackendGpu): the grading pass, recorded; the answer is the backend's own rule on the handles it holds at this
    /// point of the call sequence (see _handles).</summary>
    protected bool RunLutRec(in LutArgs a)
    {
        bool ok = _lutProg && _handles.Contains(a.Lut1) && _handles.Contains(a.Lut2);
        var L = _rec; ref var c = ref Rec(MtRender.K.Lut); c.A = L.Lut.Count; c.E = ok ? 1 : 0; L.Lut.Add(a);
        if (L.Ref != null) L.Ref.AddLut(a, ok);
        return ok;
    }
    /// <summary>Kit #6 gpulight (IGpuMesh, MtBackendGpu): the program is built where the backend runs (a sync point, once per program; the answer
    /// is remembered here); a draw is recorded with its arguments, the uniforms copied (the colour arena: one FColor per vec4) and the data
    /// array held (a capture slot: the caller never writes it); a release is recorded in order.</summary>
    List<string> _gpuNames; List<int> _gpuIds;   // (made on the first call)
    protected int GpuProgramRec(string name, string vs, string fs)
    {
        _gpuNames ??= new(); _gpuIds ??= new();
        int k = _gpuNames.IndexOf(name); if (k >= 0) return _gpuIds[k];
        Ref(MtCheck.GpuProgram); Sync(); Exec(MtCheck.GpuProgram);
        int id = WorkerSubmits ? ApiGpuProgram(name, vs, fs) : ((IGpuMesh)R).GpuProgram(name, vs, fs);
        _gpuNames.Add(name); _gpuIds.Add(id); return id;
    }
    int ApiGpuProgram(string name, string vs, string fs) { int r = 0; OnApi(() => r = ((IGpuMesh)R).GpuProgram(name, vs, fs)); return r; }
    protected void GpuMeshRec(int program, float[] data, int stride, int first, int count, ReadOnlySpan<FColor> uniforms, IntPtr tex, uint blend)
    {
        ref var c = ref Rec(MtRender.K.GpuMesh); var L = _rec; c.Tex = tex; c.Blend = blend; c.A = program; c.B = first; c.C = count; c.D = stride;
        c.E = uniforms.Length; c.Live = L.PutF(uniforms);   // (Live: the uniforms' offset in the colour arena; this command has no stream-hash slot)
        L.AddCap(out c.X, data, null, null, null);
        if (L.Ref != null) L.Ref.AddGpu(program, data, stride, first, count, uniforms, tex, blend);
    }
    protected void GpuReleaseRec(float[] data)
    {
        ref var c = ref Rec(MtRender.K.GpuRelease); var L = _rec; L.AddCap(out c.X, data, null, null, null); c.Live = -1;
        if (L.Ref != null) L.Ref.AddGpu(-1, data, 0, 0, 0, default, IntPtr.Zero, 0);
    }
    public void EndFrame() { ref var c = ref Rec(MtRender.K.EndFrame); c.A = -1; Ref(MtCheck.EndFrame); }
    public void Present() { Rec(MtRender.K.Present); Ref(MtCheck.Present); }
    protected void Stylize(in GlBackend.StylizeArgs a) { var L = _rec; ref var c = ref Rec(MtRender.K.Stylize); c.A = L.Sty.Count; L.Sty.Add(a); if (L.Ref != null) L.Ref.AddSty(a); }

    // ---------------- calls that need the backend now: sync, then call ----------------
    // (submit=worker: the call itself is made by the render thread, the main thread waits for its answer -- the lambdas live in their own
    //  methods, so nothing is allocated for them in the submit=main case)
    public IntPtr CreateTexture(int w, int h, byte[] rgba, bool linear, bool sdf = false, bool owned = false)
    {
        Ref(MtCheck.CreateTexture, w, h, linear ? 1 : 0, (sdf ? 2 : 0) | (owned ? 4 : 0), bytes: rgba); Sync(); Exec(MtCheck.CreateTexture, w, h, linear ? 1 : 0, (sdf ? 2 : 0) | (owned ? 4 : 0), rgba);
        return Handed(WorkerSubmits ? ApiCreateTexture(w, h, rgba, linear, sdf, owned) : R.CreateTexture(w, h, rgba, linear, sdf, owned));
    }
    IntPtr ApiCreateTexture(int w, int h, byte[] rgba, bool linear, bool sdf, bool owned) { IntPtr r = default; OnApi(() => r = R.CreateTexture(w, h, rgba, linear, sdf, owned)); return r; }
    public IntPtr CreateTarget(int w, int h) { Ref(MtCheck.CreateTarget, w, h); Sync(); Exec(MtCheck.CreateTarget, w, h); return Handed(WorkerSubmits ? ApiCreateTarget(w, h) : R.CreateTarget(w, h)); }
    IntPtr ApiCreateTarget(int w, int h) { IntPtr r = default; OnApi(() => r = R.CreateTarget(w, h)); return r; }
    public void Finish() { Ref(MtCheck.Finish); Sync(); Exec(MtCheck.Finish); if (WorkerSubmits) OnApi(R.Finish); else R.Finish(); }
    public byte[] ReadPixels(out int w, out int h) { Ref(MtCheck.ReadPixels); Sync(); Exec(MtCheck.ReadPixels); if (WorkerSubmits) return ApiReadPixels(out w, out h); return R.ReadPixels(out w, out h); }
    byte[] ApiReadPixels(out int w, out int h) { byte[] r = null; int rw = 0, rh = 0; OnApi(() => r = R.ReadPixels(out rw, out rh)); w = rw; h = rh; return r; }
    public void Screenshot(string path) { Ref(MtCheck.Screenshot); Sync(); Exec(MtCheck.Screenshot); if (WorkerSubmits) ApiScreenshot(path); else R.Screenshot(path); }
    void ApiScreenshot(string path) => OnApi(() => R.Screenshot(path));
    protected IntPtr CreateCompressed(int w, int h, int fmt, byte[] blocks, bool linear, Func<byte[]> rgba)
    {
        Ref(MtCheck.CreateCompressed, w, h, fmt, linear ? 1 : 0, bytes: blocks); Sync(); Exec(MtCheck.CreateCompressed, w, h, fmt, linear ? 1 : 0, blocks);
        return Handed(WorkerSubmits ? ApiCreateCompressed(w, h, fmt, blocks, linear, rgba) : ((ICompressedUpload)R).CreateCompressedTexture(w, h, fmt, blocks, linear, rgba));
    }
    IntPtr ApiCreateCompressed(int w, int h, int fmt, byte[] blocks, bool linear, Func<byte[]> rgba) { IntPtr r = default; OnApi(() => r = ((ICompressedUpload)R).CreateCompressedTexture(w, h, fmt, blocks, linear, rgba)); return r; }
    protected IntPtr CreateAlpha8(int w, int h, byte[] a8, bool linear)
    {
        Ref(MtCheck.CreateAlpha8, w, h, linear ? 1 : 0, bytes: a8); Sync(); Exec(MtCheck.CreateAlpha8, w, h, linear ? 1 : 0, 0, a8);
        return Handed(WorkerSubmits ? ApiCreateAlpha8(w, h, a8, linear) : ((IAlphaUpload)R).CreateSdfAlpha8Texture(w, h, a8, linear));
    }
    IntPtr ApiCreateAlpha8(int w, int h, byte[] a8, bool linear) { IntPtr r = default; OnApi(() => r = ((IAlphaUpload)R).CreateSdfAlpha8Texture(w, h, a8, linear)); return r; }
#if !SHEPHERD_NO_K8
    /// <summary>Kit #8 (IVideoTexture): the video texture, in order like the other creations (SHEPHERD_MT_CHECK=1 records it as a creation
    /// with no pixels).</summary>
    protected IntPtr CreateVideoRec(int w, int h, out IntPtr data)
    {
        Ref(MtCheck.CreateTexture, w, h, 1, 8); Sync(); Exec(MtCheck.CreateTexture, w, h, 1, 8);
        IntPtr d = IntPtr.Zero, t;
        if (WorkerSubmits) { IntPtr r = default; OnApi(() => r = ((IVideoTexture)R).CreateVideoTexture(w, h, out d)); t = r; }
        else t = ((IVideoTexture)R).CreateVideoTexture(w, h, out d);
        data = d; return Handed(t);
    }
#endif
    /// <summary>Kit #6 (c): the native-memory compressed upload, in order like the array one (the caller frees the blocks after this
    /// returns). SHEPHERD_MT_CHECK=1 records a managed copy of the blocks (check runs only).</summary>
    protected unsafe IntPtr CreateCompressedPtr(int w, int h, int fmt, IntPtr blocks, int length, bool linear)
    {
        var chk = _rec.Ref != null || _rec.Exec != null ? new ReadOnlySpan<byte>((void*)blocks, length).ToArray() : null;
        Ref(MtCheck.CreateCompressed, w, h, fmt, linear ? 1 : 0, bytes: chk); Sync(); Exec(MtCheck.CreateCompressed, w, h, fmt, linear ? 1 : 0, chk);
        if (!WorkerSubmits) return Handed(((ICompressedUploadPtr)R).CreateCompressedTexture(w, h, fmt, blocks, length, linear));
        IntPtr r = default; OnApi(() => r = ((ICompressedUploadPtr)R).CreateCompressedTexture(w, h, fmt, blocks, length, linear)); return Handed(r);
    }
    /// <summary>Kit #6 (c): the native-memory alpha8 upload, in order like the array one (the caller frees the bytes after this returns:
    /// the call runs before it does). SHEPHERD_MT_CHECK=1 records a managed copy of the bytes (check runs only).</summary>
    protected unsafe IntPtr CreateAlpha8Ptr(int w, int h, IntPtr a8, bool linear)
    {
        var chk = _rec.Ref != null || _rec.Exec != null ? new ReadOnlySpan<byte>((void*)a8, w * h).ToArray() : null;
        Ref(MtCheck.CreateAlpha8, w, h, linear ? 1 : 0, bytes: chk); Sync(); Exec(MtCheck.CreateAlpha8, w, h, linear ? 1 : 0, 0, chk);
        if (!WorkerSubmits) return Handed(((IAlphaUploadPtr)R).CreateSdfAlpha8Texture(w, h, a8, linear));
        IntPtr r = default; OnApi(() => r = ((IAlphaUploadPtr)R).CreateSdfAlpha8Texture(w, h, a8, linear)); return Handed(r);
    }
    public void Dispose()
    {
        Sync();
        if (WorkerSubmits) OnApi(R.Dispose);
#if SHEPHERD_EBOOT
        _quit = true; _done.Reset(); _go.Set();
        bool stopped = _done.Wait(1000);
#else
        _quit = true; Shepherd.Assets.Sched.EvReset(_done); Shepherd.Assets.Sched.EvSet(_go);
        bool stopped = Shepherd.Assets.Sched.EvWait(_done, 1000);
#endif
        if (!stopped)
        {
            // kit #3 review: Sync() above already waited for the last job, so this is a render thread that does not answer; give it 9 s more,
            // then tell the Vita host, which then makes no vitaGL call after Main (its END pool line and the green/red screen would share
            // vitaGL's one context with a thread that may still be inside it)
            Log.Warn("[mt] the render thread did not stop within 1 s of the end: waiting up to 9 s more");
#if SHEPHERD_EBOOT
            stopped = _done.Wait(9000);
#else
            stopped = Shepherd.Assets.Sched.EvWait(_done, 9000);
#endif
            if (!stopped) { Log.Error("[mt] the render thread did not stop within 10 s: it may still be inside the backend"); Diag.VitaHost.RenderThreadAlive("render thread did not stop within 10 s"); }
            else Log.Info("[mt] the render thread stopped late");
        }
        if (MtRender.Cur == this) MtRender.Cur = null;
        if (!WorkerSubmits) R.Dispose();
        if (stopped) { _rec?.Free(); _spare?.Free(); }   // the arenas (native); a render thread that did not stop may still read them
    }
    /// <summary>submit=worker: the render thread makes the call (it is idle: Sync has joined it); exceptions come back to the caller.</summary>
    void OnApi(Action a)
    {
        long t0 = Diag.K7.Master ? Stopwatch.GetTimestamp() : 0;   // kit #7 review fix (the overlay; SHEPHERD_K7=0: no clock read, kit #6)
#if SHEPHERD_EBOOT
        _call = a; _done.Reset(); _go.Set(); _done.Wait();
#else
        _call = a; Shepherd.Assets.Sched.EvReset(_done); Shepherd.Assets.Sched.EvSet(_go); Shepherd.Assets.Sched.EvWait(_done);   // kit #7 tests: the PCT hooks (Mac builds only)
#endif
        if (t0 != 0) _apiTicks += Stopwatch.GetTimestamp() - t0;
        if (_callError is { } e) { _callError = null; System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e).Throw(); }
    }
    long _apiTicks;
    /// <summary>Kit #7 review fix (Render/K7Overlay.cs): the main thread's time blocked in <see cref="OnApi"/> so far, Stopwatch ticks -- the
    /// render thread making a call for it outside a frame job (a sync point's pending commands, among them a recorded Present: the swap;
    /// texture uploads during loads). Not main-thread work; render-thread time (its swaps are in the GPU column).</summary>
    internal long ApiTicksK7 => _apiTicks;

    // ---------------- the world draws (SceneRenderer's emit loop, main thread) ----------------
    /// <summary>Capture a sprite draw (EmitSprite's inputs). False: build it now (its texture handle needs work with side effects).</summary>
    internal bool Sprite(Vector2[] verts, Vector2[] uvs, ushort[] tris, bool fx, bool fy, in Matrix4x4 lw, in Matrix4x4 vp, in RectF pr, float gh, NTexture tex, Color tint, uint blend, bool shared)
    {
        if (verts == null || uvs == null || tris == null) return false;   // (EmitSprite would throw: let it, in order)
        if (verts.Length == 0 || tris.Length < 3) return true;            // EmitSprite draws nothing
        if (!SceneRenderer.TryHandle(tex, false, out var h)) { _declined++; return false; }
        ref var c = ref Rec(MtRender.K.Sprite); var L = _rec;
        Vector2[] sv = verts, su = uvs; ushort[] st = tris;
        if (shared) { sv = MtRender.Take(verts, L); su = MtRender.Take(uvs, L); st = MtRender.Take(tris, L); _copiedSprites++; }
        ref var w = ref L.AddCap(out c.X, sv, su, st, null);
        w.Fx = fx; w.Fy = fy; w.Lw = lw; c.Tex = h; w.Tint = tint; c.Blend = blend; w.Pass = _pass;   // (vp, pr, gh: the pass's, NextPass)
        Captured(L, L.N - 1);
        if (L.Ref != null) { MtCheck.HandleAgrees(tex, h); RefSprite(L, h, verts, uvs, tris, fx, fy, lw, vp, pr, gh, tint, blend); }
        return true;
    }
    /// <summary>Capture a mesh draw (EmitMesh's inputs; the channel arrays are copied: Spine rewrites them in place).</summary>
    internal bool Mesh(Vector3[] pos, Color32[] col, Vector4[] uv, int[] idx, in Matrix4x4 lw, in Matrix4x4 vp, in RectF pr, float gh, NTexture tex, Color tint, uint blend, bool skipZero = false)
    {
        if (pos == null || idx == null || pos.Length == 0 || idx.Length < 3) return true;   // EmitMesh draws nothing
        if (!SceneRenderer.TryHandle(tex, false, out var h)) { _declined++; return false; }
        ref var c = ref Rec(MtRender.K.Mesh); var L = _rec;
        ref var w = ref L.AddCap(out c.X, MtRender.Take(pos, L), col == null ? null : MtRender.Take(col, L), uv == null ? null : MtRender.Take(uv, L), MtRender.Take(idx, L));
        w.Lw = lw; c.Tex = h; w.Tint = tint; c.Blend = blend; w.Pass = _pass; w.SkipZero = skipZero;
        Captured(L, L.N - 1);
        if (L.Ref != null) { MtCheck.HandleAgrees(tex, h); RefMesh(L, h, pos, col, uv, idx, lw, vp, pr, gh, tint, blend, skipZero); }
        return true;
    }
    /// <summary>Capture a glow draw (EmitGlow's inputs; pos / nrm / idx copied).</summary>
    internal bool Glow(Vector3[] pos, Vector3[] nrm, Vector3 cmn, Vector3 cmx, int[] idx, in Matrix4x4 lw, in Matrix4x4 vp, in RectF pr, float gh, Color tint, Vector3 cam, uint blend, NTexture white)
    {
        if (!SceneRenderer.TryHandle(white, false, out var h)) { _declined++; return false; }
        ref var c = ref Rec(MtRender.K.Glow); var L = _rec;
        ref var w = ref L.AddCap(out c.X, MtRender.Take(pos, L), nrm == null ? null : MtRender.Take(nrm, L), MtRender.Take(idx, L), null); w.CMin = cmn; w.CMax = cmx;
        w.Lw = lw; c.Tex = h; w.Tint = tint; w.Cam = cam; c.Blend = blend; w.Pass = _pass;
        Captured(L, L.N - 1);
        if (L.Ref != null) { MtCheck.HandleAgrees(white, h); RefGlow(L, h, pos, nrm, cmn, cmx, idx, lw, vp, pr, gh, tint, cam, blend); }
        return true;
    }
    // SHEPHERD_MT_CHECK=1: the single-threaded builds from the original inputs (own methods: a lambda in the capture methods would allocate
    // its closure on every call, check or not)
    static void RefSprite(MtRender.CmdList L, IntPtr h, Vector2[] verts, Vector2[] uvs, ushort[] tris, bool fx, bool fy, Matrix4x4 lw, Matrix4x4 vp, RectF pr, float gh, Color tint, uint blend)
        => MtCheck.Reference(L, h, e => e.EmitSprite(verts, uvs, tris, fx, fy, in lw, in vp, in pr, gh, null, tint, blend));
    static void RefMesh(MtRender.CmdList L, IntPtr h, Vector3[] pos, Color32[] col, Vector4[] uv, int[] idx, Matrix4x4 lw, Matrix4x4 vp, RectF pr, float gh, Color tint, uint blend, bool skipZero)
        => MtCheck.Reference(L, h, e => e.EmitMesh(pos, col, uv, idx, in lw, in vp, in pr, gh, null, tint, blend, skipZero));
    static void RefGlow(MtRender.CmdList L, IntPtr h, Vector3[] pos, Vector3[] nrm, Vector3 cmn, Vector3 cmx, int[] idx, Matrix4x4 lw, Matrix4x4 vp, RectF pr, float gh, Color tint, Vector3 cam, uint blend)
        => MtCheck.Reference(L, h, e => e.EmitGlow(pos, nrm, cmn, cmx, idx, in lw, in vp, in pr, gh, tint, cam, blend, null));
    /// <summary>A captured draw: its stream-hash slot.</summary>
    void Captured(MtRender.CmdList L, int i)
    {
        _deferred++; L.Emits++;
        if (RenderCheck.Live != null) { L.Live = RenderCheck.Live; L[i].Live = L.Live.Placeholder(); }
    }
    /// <summary>SceneRenderer: a new camera pass (its captured draws hold its index).</summary>
    internal void NextPass(in Matrix4x4 vp, in RectF pr, float gh)
    {
        var L = _rec; if (L.NP == L.P.Length) Array.Resize(ref L.P, L.NP * 2);
        ref var p = ref L.P[L.NP]; p.Vp = vp; p.Pr = pr; p.Gh = gh; _pass = L.NP++;
    }
    /// <summary>SceneRenderer.Frame: the calls recorded until <see cref="EndScope"/> belong to one of its try blocks (a screen camera's DrawWorld,
    /// the Stylizer, the UI pass). An exception when one of them runs (a captured build on the render thread, or the backend) drops the rest
    /// of that block's calls, as the single-threaded pass stops at the exception; the calls after the block run (EndFrame, Present).</summary>
    internal void BeginScope() => _scope = ++_scopes;
    internal void EndScope() => _scope = 0;

    // ---------------- the frame boundary (main thread) ----------------
    /// <summary>SceneRenderer.Frame's start: the render thread is done with the previous frame (submit=main: the main thread hands that frame,
    /// then what was recorded since -- Present, calls game code made -- to the backend). Submit=worker: what was recorded since stays in the
    /// list, in order, ahead of this frame.</summary>
    internal void FrameStart() { Join(); if (!WorkerSubmits) RunPending(); }
    /// <summary>The end of SceneRenderer.Frame: G.EndFrame, recorded with what the replay completes; the list goes to the render thread.</summary>
    internal void FrameEnd(int frame, int mainClip, int mainDrop, bool hash)
    {
        ref var c = ref Rec(MtRender.K.EndFrame); var L = _rec; c.A = frame; c.B = mainClip; c.C = mainDrop; c.D = hash ? 1 : 0; Ref(MtCheck.EndFrame);
        if (hash) { L.Live = RenderCheck.Live; RenderCheck.Live = new RenderCheck.Rec(); }   // the frame's stream; what game code issues next counts into the next frame
        L.Frame = frame; L.Ended = true;
        L.JobFrom = L.Done; _busy = L; _rec = _spare; _spare = null;   // (EndFrame was just recorded: the list always has something to run)
#if SHEPHERD_EBOOT
        _done.Reset(); _go.Set();
#else
        Shepherd.Assets.Sched.EvReset(_done); Shepherd.Assets.Sched.EvSet(_go);
#endif
    }

    /// <summary>A sync point (a call that needs the backend now): the render thread is done, and everything recorded before has reached the
    /// backend, in order.</summary>
    internal void Sync() { _syncs++; Join(); RunPending(); }
    /// <summary>A new command in the list being recorded (a full list spills first: CmdList.CCap).</summary>
    ref MtRender.Cmd Rec(MtRender.K k)
    {
        if (_rec.N >= MtRender.CmdList.CCap) Spill();
        ref var c = ref _rec.Add(k); c.Scope = _scope; return ref c;
    }
    /// <summary>The list is full (CmdList.CCap / VCap / ICap): run what is recorded so far (a sync point), then reuse its commands and arena
    /// from the start (nothing in them is needed once they have run; the camera passes, Stylizer arguments and snapshot arrays stay).</summary>
    void Spill()
    {
        _spills++; Sync();
        var L = _rec;
        if (L.Done == L.N) { L.NotePeak(); L.HighW = Math.Max(L.HighW, L.NW); L.N = L.Done = 0; L.NV = 0; L.NI = 0; L.NF = 0; L.NW = 0; }
    }
    /// <summary>Wait for the render thread's frame; submit=main: hand it to the backend.</summary>
    void Join()
    {
        if (_busy == null) return;
#if SHEPHERD_EBOOT
        if (!_done.IsSet) { long w0 = Stopwatch.GetTimestamp(); _done.Wait(); _waitTicks += Stopwatch.GetTimestamp() - w0; _waitCount++; }
#else
        Shepherd.Assets.Sched.Point(Shepherd.Assets.Sched.MtJoin);
        if (!_done.IsSet) { long w0 = Stopwatch.GetTimestamp(); Shepherd.Assets.Sched.EvWait(_done); _waitTicks += Stopwatch.GetTimestamp() - w0; _waitCount++; }
#endif
        var L = _busy; _busy = null;
        if (_jobError is { } e) { _jobError = null; Log.Exception(e, "render thread"); }
        if (!WorkerSubmits) RunLogged(L, L.JobFrom, L.N, backend: true, build: false);
        L.Done = L.N;
        Retire(L); _spare = L;
    }
    /// <summary>The commands recorded since the last kick / sync point (submit=worker: run by the render thread).</summary>
    void RunPending()
    {
        var L = _rec; int from = L.Done, to = L.N;
        if (from >= to) return;
        if (WorkerSubmits) RunOnApi(L, from, to); else RunLogged(L, from, to, backend: true, build: true);
        L.Done = to;
    }
    void RunOnApi(MtRender.CmdList L, int from, int to) => OnApi(() => RunLogged(L, from, to, backend: true, build: true));
    /// <summary>A replay outside the render thread's job: a backend exception is logged (as the job's are), not thrown into whatever call
    /// happened to need the sync point.</summary>
    void RunLogged(MtRender.CmdList L, int from, int to, bool backend, bool build)
    {
        try { Run(L, from, to, backend, build); } catch (Exception e) { Log.Exception(e, "render thread"); }
    }

    /// <summary>A completed list: counters, the check, its arrays back to the pool, ready for reuse.</summary>
    void Retire(MtRender.CmdList L)
    {
        if (L.Ended) { R.Stats.CopyTo(_stats); _frameStats.Enqueue((L.Frame, _stats.DrawsIn, _stats.DrawsOut)); while (_frameStats.Count > 64) _frameStats.Dequeue(); }
        if (L.Ref != null) MtCheck.Compare(L);
        L.ClearCaps();
        _arenaHigh = Math.Max(_arenaHigh, L.ArenaHigh); L.ArenaHigh = L.ArenaBytes;
        L.Shrink();
        L.N = L.Done = L.JobFrom = L.NV = L.NI = L.NF = L.Emits = L.NP = L.NW = L.BuildFailed = L.BackFailed = 0; L.Sty.Clear(); L.Lut.Clear(); L.Live = null; L.Ended = false; L.Frame = -1;
        MtRender.Give(L);
    }
    /// <summary>FrameTimes: (frame, draws in, draws out) of the frames the backend completed since the last call.</summary>
    internal bool TryTakeFrameStats(out (int frame, int din, int dout) s) => _frameStats.TryDequeue(out s);

    // ---------------- the render thread ----------------
    void Loop()
    {
        Diag.VitaHost.PinRender();   // the Vita: USER_1 when pinning is on (SHEPHERD_VITA_PIN, default = this pipeline on); else nothing
        while (true)
        {
#if SHEPHERD_EBOOT
            _go.Wait(); _go.Reset();
            if (_quit) { _done.Set(); return; }
            if (_call != null) CallOnce(); else Job();
            _done.Set();
#else
            Shepherd.Assets.Sched.EvWait(_go); Shepherd.Assets.Sched.EvReset(_go);
            if (_quit) { Shepherd.Assets.Sched.EvSet(_done); Shepherd.Assets.Sched.Leave(); return; }
            if (_call != null) CallOnce(); else Job();
            Shepherd.Assets.Sched.EvSet(_done);
#endif
        }
    }
    // (own methods: what they referenced does not stay in the render thread's frame while it waits -- the last call's closure kept there
    //  kept that texture upload's pixels alive until the next call)
    [MethodImpl(MethodImplOptions.NoInlining)]
    void CallOnce() { var a = _call; _call = null; try { a(); } catch (Exception e) { _callError = e; } }
    [MethodImpl(MethodImplOptions.NoInlining)]
    void Job()
    {
        var L = _busy;
        long t0 = Stopwatch.GetTimestamp(), c0 = MtRender.ThreadCpuNs();
        Diag.K7Prof.RtBegin();   // kit #7: the render thread's zone (the sampler / the Chrome trace); off: one bool test
        try { Run(L, L.JobFrom, L.N, backend: WorkerSubmits, build: true); }
        catch (Exception e) { _jobError = e; }
        Diag.K7Prof.RtEnd();
        _workerTicks += Stopwatch.GetTimestamp() - t0; _workerCpuNs += MtRender.ThreadCpuNs() - c0; _jobs++;
    }

    /// <summary>Commands [from, to) of L: build the captured draws (build) and / or make the backend calls (backend). An exception is logged
    /// and drops the rest of its scope (Cmd.Scope: the try block of SceneRenderer.Frame it was recorded in), as the single-threaded pass would
    /// have stopped there; outside a scope only the call itself is lost. A build failure marks the scope's later commands Dropped, so the
    /// main thread's submit pass (submit=main) skips them too; the calls after the scope always run (EndFrame, Present, the stream hash).</summary>
    void Run(MtRender.CmdList L, int from, int to, bool backend, bool build)
    {
        for (int i = from; i < to; i++)
        {
            ref var c = ref L[i];
            if (c.K == MtRender.K.Dropped) continue;
            if (c.Scope != 0)
            {
                if (build && c.Scope == L.BuildFailed) { c.K = MtRender.K.Dropped; continue; }
                if (backend && !build && c.Scope == L.BackFailed) continue;
            }
            try
            {
                switch (c.K)
                {
                    case MtRender.K.Sprite: case MtRender.K.Mesh: case MtRender.K.Glow:
                        if (build)
                        {
                            _sink.L = L; _sink.I = i; _sink.Direct = backend;
                            ref var w = ref L.Cap(c.X); ref var p = ref L.P[w.Pass]; ref var r = ref L.Refs(c.X);   // (the types the capture methods stored)
                            if (c.K == MtRender.K.Sprite) _rep.EmitSprite(Unsafe.As<Vector2[]>(r.A), Unsafe.As<Vector2[]>(r.B), Unsafe.As<ushort[]>(r.C), w.Fx, w.Fy, in w.Lw, in p.Vp, in p.Pr, p.Gh, null, w.Tint, c.Blend);
                            else if (c.K == MtRender.K.Mesh) _rep.EmitMesh(Unsafe.As<Vector3[]>(r.A), Unsafe.As<Color32[]>(r.B), Unsafe.As<Vector4[]>(r.C), Unsafe.As<int[]>(r.D), in w.Lw, in p.Vp, in p.Pr, p.Gh, null, w.Tint, c.Blend, w.SkipZero);
                            else _rep.EmitGlow(Unsafe.As<Vector3[]>(r.A), Unsafe.As<Vector3[]>(r.B), w.CMin, w.CMax, Unsafe.As<int[]>(r.C), in w.Lw, in p.Vp, in p.Pr, p.Gh, w.Tint, w.Cam, c.Blend, null);
                        }
                        else if (backend && c.Out) Call(L, MtCheck.Draw, c.Tex, c.Blend, c.Sdf, L.Vs(c.A, c.B), L.Is(c.C, c.D));
                        break;
                    default:
                        if (backend) Do(L, ref c);
                        break;
                }
            }
            catch (Exception e)
            {
                bool built = build && c.K is MtRender.K.Sprite or MtRender.K.Mesh or MtRender.K.Glow;
                Log.Exception(e, built ? "DrawWorld (render thread)" : $"render backend ({c.K}, {(build ? "build + submit" : "submit")})");
                if (c.Scope != 0) { if (build) L.BuildFailed = c.Scope; else L.BackFailed = c.Scope; }
            }
        }
    }

    void Do(MtRender.CmdList L, ref MtRender.Cmd c)
    {
        var X = L.Exec;
        switch (c.K)
        {
            case MtRender.K.Begin: { var (r, g, b, a) = Colour(in c); X?.Add(MtCheck.Begin, IntPtr.Zero, 0, false, 0, 0, 0, 0, (r, g, b, a)); R.Begin(r, g, b, a); break; }
            case MtRender.K.Clear: { var (r, g, b, a) = Colour(in c); X?.Add(MtCheck.Clear, IntPtr.Zero, 0, false, 0, 0, 0, 0, (r, g, b, a)); R.ClearTarget(r, g, b, a); break; }
            case MtRender.K.Clip: X?.Add(MtCheck.Clip, IntPtr.Zero, 0, false, c.A, c.B, c.C, c.D); R.Clip(c.A, c.B, c.C, c.D); break;
            case MtRender.K.NoClip: X?.Add(MtCheck.NoClip); R.NoClip(); break;
            case MtRender.K.Target: X?.Add(MtCheck.Target, c.Tex); R.SetTarget(c.Tex); break;
            case MtRender.K.Destroy: X?.Add(MtCheck.Destroy, c.Tex); R.DestroyTexture(c.Tex); break;
            case MtRender.K.Draw: Call(L, MtCheck.Draw, c.Tex, c.Blend, c.Sdf, L.Vs(c.A, c.B), L.Is(c.C, c.D)); break;
            case MtRender.K.EndWorld: X?.Add(MtCheck.EndWorld); _split.EndWorldPassBase(); break;
            case MtRender.K.Stylize: { var a = L.Sty[c.A]; X?.AddSty(a); ((IGpuStylizer)R).RunStylizer(in a); break; }
            case MtRender.K.DrawAdd:
            {
                var v = L.Vs(c.A, c.B); var ix = L.Is(c.C, c.D); var add = L.Fs(c.X, c.E);
                X?.Add(MtCheck.DrawAdd, c.Tex, c.Blend, false, v: v, idx: ix, add: add);
                ((IAddDraw)R).DrawAdd(c.Tex, c.Blend, v, ix, add);
                break;
            }
            case MtRender.K.Lut:
            {
                var a = L.Lut[c.A]; bool ok = ((IGpuPost)R).RunLut(in a);
                X?.AddLut(a, ok);
                if (ok != (c.E == 1) && ++_lutWrong <= 5) Log.Warn($"[mt] RunLut answered {ok} where the main thread was told {c.E == 1} (frame {L.Frame}): only FrameTimes' lut= counter differs");
                break;
            }
            case MtRender.K.GpuMesh:
            {
                var data = Unsafe.As<float[]>(L.Refs(c.X).A); var u = L.Fs(c.Live, c.E);
                X?.AddGpu(c.A, data, c.D, c.B, c.C, u, c.Tex, c.Blend);
                ((IGpuMesh)R).DrawGpuMesh(c.A, data, c.D, c.B, c.C, u, c.Tex, c.Blend);
                break;
            }
            case MtRender.K.GpuRelease: { var data = Unsafe.As<float[]>(L.Refs(c.X).A); X?.AddGpu(-1, data, 0, 0, 0, default, IntPtr.Zero, 0); ((IGpuMesh)R).ReleaseGpuMesh(data); break; }
            case MtRender.K.Present: X?.Add(MtCheck.Present); R.Present(); break;
            case MtRender.K.EndFrame:
                X?.Add(MtCheck.EndFrame);
                if (c.A >= 0 && c.D == 1 && L.Live != null) RenderCheck.HashRec(c.A, L.Live);   // the frame's stream hash (single-threaded: hashed before G.EndFrame)
                R.EndFrame();
                if (c.A >= 0)
                {   // SceneRenderer.Frame's end, completed: the counters it left for the replay (single-threaded: set after G.EndFrame)
                    SceneRenderer.LastClipTris = c.B + _rep.ClipTris; SceneRenderer.LastDropTris = c.C + _rep.DropTris; _rep.ClipTris = _rep.DropTris = 0;
                }
                break;
        }
    }
    internal void Call(MtRender.CmdList L, int kind, IntPtr tex, uint blend, bool sdf, Span<Vertex> v, Span<int> idx)
    {
        L.Exec?.Add(kind, tex, blend, sdf, v: v, idx: idx);
        R.Draw(tex, blend, sdf, v, idx);
    }

    // ---------------- SHEPHERD_MT_CHECK=1 recording ----------------
    void Ref(int kind, int a = 0, int b = 0, int c = 0, int d = 0, IntPtr tex = default, (float, float, float, float) colour = default, byte[] bytes = null)
    { if (_rec.Ref != null) _rec.Ref.Add(kind, tex, 0, false, a, b, c, d, colour, bytes: bytes); }
    void Exec(int kind, int a = 0, int b = 0, int c = 0, int d = 0, byte[] bytes = null)
    { if (_rec.Exec != null) _rec.Exec.Add(kind, IntPtr.Zero, 0, false, a, b, c, d, bytes: bytes); }

    // ---------------- window summary (SimStats) ----------------
    internal void WindowStart()
    {
        _w0Wait = _waitTicks; _w0Count = _waitCount; _w0Syncs = _syncs; _w0WorkerTicks = _workerTicks; _w0WorkerCpu = _workerCpuNs; _w0Jobs = _jobs;
        _w0Deferred = _deferred; _w0Declined = _declined; _w0Copied = _copiedSprites; _w0Spills = _spills;
        _w0CopiedV = _copiedV; _w0CopiedI = _copiedI; _w0Growth = MtRender.ManagedGrowth; _w0Snap = MtRender.SnapBytes;
        _arenaHigh = Math.Max(_rec.ArenaBytes, _spare?.ArenaBytes ?? 0);
    }
    internal void WindowEnd(long n, double mainCpu)
    {
        // the worker's counters are complete for every job joined so far (the window's last frame is still in flight: it is not counted)
        double ms(long t) => t * 1000.0 / Stopwatch.Frequency / n;
        Log.Info($"[mt] window ({n} frames): backend {R.Name}, {(WorkerSubmits ? "render thread builds + submits" : "render thread builds, main thread submits")}; " +
                 $"captured world draws {(double)(_deferred - _w0Deferred) / n:F1}/frame (built on the main thread: {(double)(_declined - _w0Declined) / n:F1}, sprites copied {(double)(_copiedSprites - _w0Copied) / n:F1}); " +
                 $"render thread busy {ms(_workerTicks - _w0WorkerTicks):F3} ms/frame over {_jobs - _w0Jobs} jobs; main thread waited {ms(_waitTicks - _w0Wait):F3} ms/frame ({_waitCount - _w0Count} waits, {_syncs - _w0Syncs} sync points, {_spills - _w0Spills} arena spills)");
        Log.Info($"[mt] window memory: copied into the arena {(double)(_copiedV - _w0CopiedV) / n:F0} vertices + {(double)(_copiedI - _w0CopiedI) / n:F0} indices/frame " +
                 $"({((_copiedV - _w0CopiedV) * 32.0 + (_copiedI - _w0CopiedI) * 4.0) / 1024 / n:F1} KiB/frame; the UI pass and the draws built on the main thread), world snapshots {(MtRender.SnapBytes - _w0Snap) / 1024.0 / n:F1} KiB/frame; " +
                 $"native memory (commands, captures, arena) high-water {_arenaHigh / 1024.0:F0} KiB a list, now {(_rec.ArenaBytes + (_spare?.ArenaBytes ?? 0) + (_busy?.ArenaBytes ?? 0)) / 1024.0:F0} KiB in all; " +
                 $"the pipeline's managed allocations {(MtRender.ManagedGrowth - _w0Growth) / 1024.0 / n:F2} KiB/frame (capture chunks, new snapshot arrays); " +
                 $"managed memory held: captures {(_rec.CapBytes + (_spare?.CapBytes ?? 0) + (_busy?.CapBytes ?? 0)) / 1024.0:F0} KiB, snapshots {(_rec.SnapBytes + (_spare?.SnapBytes ?? 0) + (_busy?.SnapBytes ?? 0)) / 1024.0:F0} KiB, " +
                 $"the render thread's build buffers {_rep.Bytes / 1024.0:F0} KiB");
        if (MtRender.CpuOn) Log.Info($"[mt] window CPU: main thread {mainCpu:F3} ms/frame, render thread {(_workerCpuNs - _w0WorkerCpu) / 1e6 / n:F3} ms/frame, total {mainCpu + (_workerCpuNs - _w0WorkerCpu) / 1e6 / n:F3} ms/frame");
    }
}

/// <summary>The proxy for the backends with the GPU Stylizer and the compressed / alpha8 uploads (null, GL).</summary>
[ProfileOnly]
internal sealed class MtBackendGpu : MtBackend, ICompressedUpload, ICompressedUploadPtr, IGpuStylizer, IAlphaUpload, IAlphaUploadPtr, IGpuPost, IAddDraw, IGpuMesh
#if !SHEPHERD_NO_K8
    , IVideoTexture
#endif
{
#if !SHEPHERD_NO_K8
    /// <summary>Kit #8 (Runtime/K8Video.cs): made where the backend runs, at a sync point like every texture creation (the decoder then
    /// writes the texture's memory from the main thread, a ring of textures apart from what the render thread draws).</summary>
    public IntPtr CreateVideoTexture(int w, int h, out IntPtr data) { data = IntPtr.Zero; return R is IVideoTexture ? CreateVideoRec(w, h, out data) : IntPtr.Zero; }
#endif
    internal MtBackendGpu(IRenderBackend r, bool workerSubmits) : base(r, workerSubmits) { }
    /// <summary>Fidelity round 1 (owner-local lit model): the backend's own answer (a constant: its program set is built with the backend).</summary>
    public bool SupportsAdd => R is IAddDraw a && a.SupportsAdd;
    public void DrawAdd(IntPtr tex, uint blend, Span<Vertex> v, Span<int> idx, Span<FColor> add) => DrawAddRec(tex, blend, v, idx, add);
    public bool RunLut(in LutArgs a) => RunLutRec(in a);
    public bool Dxt => R is ICompressedUpload c && c.Dxt;
    public IntPtr CreateCompressedTexture(int w, int h, int unityFormat, byte[] topDownBlocks, bool linear, Func<byte[]> rgba) => CreateCompressed(w, h, unityFormat, topDownBlocks, linear, rgba);
    public IntPtr CreateSdfAlpha8Texture(int w, int h, byte[] a8, bool linear) => CreateAlpha8(w, h, a8, linear);
    public IntPtr CreateSdfAlpha8Texture(int w, int h, IntPtr a8, bool linear) => R is IAlphaUploadPtr ? CreateAlpha8Ptr(w, h, a8, linear) : IntPtr.Zero;
    public IntPtr CreateCompressedTexture(int w, int h, int unityFormat, IntPtr blocks, int length, bool linear) => R is ICompressedUploadPtr ? CreateCompressedPtr(w, h, unityFormat, blocks, length, linear) : IntPtr.Zero;
    public void RunStylizer(in GlBackend.StylizeArgs a) => Stylize(in a);
    /// <summary>Kit #6 gpulight: only when the backend behind it has static-mesh programs (GL, null).</summary>
    public int GpuProgram(string name, string vertexFile, string fragmentFile) => R is IGpuMesh ? GpuProgramRec(name, vertexFile, fragmentFile) : 0;
    public void DrawGpuMesh(int program, float[] data, int stride, int first, int count, ReadOnlySpan<FColor> uniforms, IntPtr tex, uint blend) => GpuMeshRec(program, data, stride, first, count, uniforms, tex, blend);
    public void ReleaseGpuMesh(float[] data) => GpuReleaseRec(data);
}
