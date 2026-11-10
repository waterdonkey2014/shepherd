using System.Runtime.InteropServices;
using static Shepherd.Engine.Render.Sdl;
namespace Shepherd.Engine.Render;

/// <summary>SHEPHERD_MT_CHECK=1 with SHEPHERD_MT_RENDER=1 (check runs only; slow): proves, list by list (a frame's calls and what game code
/// issued since the previous frame), that the backend received exactly the single-threaded call sequence. Reference: every backend call in
/// the order the main thread issued it (MtBackend records it when called), and for each world draw the render thread builds later, the draw
/// the single-threaded pass builds from the ORIGINAL inputs at that moment (the live sprite / mesh arrays, before any copy: the same
/// WorldEmitter code, run on the main thread into a recorder). Executed: every call as the real backend receives it (render thread or main
/// thread), recorded just before the call. The two are compared event by event: kind, texture handle, blend, sdf, every vertex's 32 bytes,
/// every index, clip rects, targets, clear colours, the Stylizer pass's arguments, the arguments (and a hash of the pixels) of every texture
/// / target creation, and where ReadPixels / Screenshot / Finish happen. Mismatches: [mtcheck] lines (the first 20); <see cref="Summary"/> at exit.</summary>
[ProfileOnly]
internal static class MtCheck
{
    public const int Begin = 1, Clear = 2, Clip = 3, NoClip = 4, Target = 5, Draw = 6, Destroy = 7, EndWorld = 8, EndWorldFull = 9, Stylize = 10, EndFrame = 11,
        Present = 12, Finish = 13, ReadPixels = 14, Screenshot = 15, CreateTexture = 16, CreateTarget = 17, CreateCompressed = 18, CreateAlpha8 = 19,
        DrawAdd = 20, Lut = 21,   // (integration with fidelity round 1: the owner-local lit draws and grading pass)
        GpuProgram = 22, GpuMesh = 23, GpuRelease = 24;   // (kit #6 gpulight: IGpuMesh)
    static readonly string[] Names = { "?", "Begin", "ClearTarget", "Clip", "NoClip", "SetTarget", "Draw", "DestroyTexture", "EndWorldPass", "EndWorldPass (SDL)", "RunStylizer", "EndFrame",
        "Present", "Finish", "ReadPixels", "Screenshot", "CreateTexture", "CreateTarget", "CreateCompressedTexture", "CreateSdfAlpha8Texture", "DrawAdd", "RunLut", "GpuProgram", "DrawGpuMesh", "ReleaseGpuMesh" };

    internal struct Ev
    {
        public int Kind, A, B, C, D; public IntPtr Tex; public uint Blend; public bool Sdf; public float R, G, Bl, Al; public ulong Bytes;
        public Vertex[] V; public int[] I; public GlBackend.StylizeArgs Sty;
        /// <summary>DrawAdd: the added colours. Lut: the pass's arguments and its answer (issued: what the main thread was told; received:
        /// what the backend returned).</summary>
        public FColor[] Add; public LutArgs Lut; public bool Ok;
        /// <summary>Kit #11 gpu2 (review fix): GpuMesh -- whether it was DrawGpuMeshTex, its textures on units 1 / 2 and their wrap bits.</summary>
        public bool More; public IntPtr Tex1, Tex2; public int Wrap;
    }
    internal sealed class Rec
    {
        public readonly List<Ev> E = new();
        public void Add(int kind, IntPtr tex = default, uint blend = 0, bool sdf = false, int a = 0, int b = 0, int c = 0, int d = 0, (float, float, float, float) col = default,
            Span<Vertex> v = default, Span<int> idx = default, byte[] bytes = null, Span<FColor> add = default)
        {
            bool draw = kind is Draw or DrawAdd;
            E.Add(new Ev { Kind = kind, Tex = tex, Blend = blend, Sdf = sdf, A = a, B = b, C = c, D = d, R = col.Item1, G = col.Item2, Bl = col.Item3, Al = col.Item4,
                V = draw ? v.ToArray() : null, I = draw ? idx.ToArray() : null, Add = kind == DrawAdd ? add.ToArray() : null, Bytes = bytes == null ? 0 : Fnv(bytes) });
        }
        public void AddSty(in GlBackend.StylizeArgs a) => E.Add(new Ev { Kind = Stylize, Sty = a });
        public void AddLut(in LutArgs a, bool ok) => E.Add(new Ev { Kind = Lut, Lut = a, Ok = ok });
        /// <summary>Kit #6 gpulight: a static-mesh draw (program -1: a release) -- its arguments, the data array's identity, the uniforms.
        /// Kit #11 gpu2 (review fix): <paramref name="more"/> = DrawGpuMeshTex's two textures and wrap bits (null: DrawGpuMesh).</summary>
        public void AddGpu(int program, float[] data, int stride, int first, int count, ReadOnlySpan<FColor> uniforms, IntPtr tex, uint blend, IntPtr[] more = null)
            => E.Add(new Ev { Kind = program < 0 ? GpuRelease : GpuMesh, A = program, B = first, C = count, D = stride, Tex = tex, Blend = blend, Add = uniforms.ToArray(),
                              Bytes = data == null ? 0 : (ulong)(uint)System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(data) << 32 | (uint)data.Length,
                              More = more != null, Tex1 = more != null ? more[0] : IntPtr.Zero, Tex2 = more != null ? more[1] : IntPtr.Zero, Wrap = more != null ? (int)more[2] : 0 });
    }
    static ulong Fnv(byte[] b) { ulong h = 14695981039346656037UL; foreach (var x in b) h = (h ^ x) * 1099511628211UL; return h ^ (ulong)b.Length; }

    static long _lists, _events, _draws, _verts, _builds, _bad, _adds, _luts;
    static int _notes;
    static readonly MtRender.Sink _cap = new() { Capture = true };

    /// <summary>The single-threaded build of a captured world draw, on the main thread, from the original inputs, into the list's reference.
    /// (SceneRenderer.Emit0: the frame's own emitter; between two world draws nothing is pending in its buffers. Its clip counters are
    /// left as they were.)</summary>
    public static void Reference(MtRender.CmdList L, IntPtr tex, Action<WorldEmitter> build)
    {
        var e = SceneRenderer.Emit0; int ct = e.ClipTris, dt = e.DropTris;
        _cap.L = L; _cap.CapTex = tex; e.Rx = _cap; _builds++;
        try { build(e); }
        finally { e.Rx = null; e.ClipTris = ct; e.DropTris = dt; _cap.L = null; }
    }

    static long _handles, _handleBad;
    /// <summary>SceneRenderer.TryHandle said the texture's handle was ready without side effects: Handle itself must give the same handle (a
    /// drift between the two copies of those tests would show here).</summary>
    public static void HandleAgrees(Shepherd.Engine.Native.NTexture tex, IntPtr h)
    {
        _handles++;
        var r = SceneRenderer.Handle(tex, false);
        if (r != h) { _handleBad++; _bad++; Note($"frame {Runtime.Time.FrameCount}: TryHandle gave {h} for '{tex?.Name}', Handle gives {r}"); }
    }

    /// <summary>A retired list: its reference calls vs the calls the backend received.</summary>
    public static void Compare(MtRender.CmdList L)
    {
        var a = L.Ref.E; var b = L.Exec.E; _lists++; _events += a.Count;
        try
        {
            int n = Math.Min(a.Count, b.Count);
            for (int i = 0; i < n; i++)
            {
                var x = a[i]; var y = b[i];
                if (x.Kind is Draw or DrawAdd) { _draws++; _verts += x.V.Length; }
                if (x.Kind == DrawAdd) _adds++; else if (x.Kind == Lut) _luts++;
                string why = x.Kind != y.Kind ? $"{Names[x.Kind]} vs {Names[y.Kind]}"
                    : x.Tex != y.Tex ? "texture / target handle" : x.Blend != y.Blend ? "blend" : x.Sdf != y.Sdf ? "sdf"
                    : x.A != y.A || x.B != y.B || x.C != y.C || x.D != y.D ? "arguments"
                    : Bits(x.R) != Bits(y.R) || Bits(x.G) != Bits(y.G) || Bits(x.Bl) != Bits(y.Bl) || Bits(x.Al) != Bits(y.Al) ? "colour"
                    : x.Bytes != y.Bytes ? "uploaded bytes"
                    : x.Kind is Draw or DrawAdd && !MemoryMarshal.AsBytes(x.V.AsSpan()).SequenceEqual(MemoryMarshal.AsBytes(y.V.AsSpan())) ? $"vertices ({x.V.Length} vs {y.V.Length})"
                    : x.Kind is Draw or DrawAdd && !x.I.AsSpan().SequenceEqual(y.I) ? $"indices ({x.I.Length} vs {y.I.Length})"
                    : x.Kind == DrawAdd && !MemoryMarshal.AsBytes(x.Add.AsSpan()).SequenceEqual(MemoryMarshal.AsBytes(y.Add.AsSpan())) ? $"added colours ({x.Add.Length} vs {y.Add.Length})"
                    : x.Kind == GpuMesh && !MemoryMarshal.AsBytes(x.Add.AsSpan()).SequenceEqual(MemoryMarshal.AsBytes(y.Add.AsSpan())) ? $"uniforms ({x.Add.Length} vs {y.Add.Length})"
                    : x.Kind == GpuMesh && (x.More != y.More || x.Tex1 != y.Tex1 || x.Tex2 != y.Tex2 || x.Wrap != y.Wrap) ? "DrawGpuMeshTex's extra textures / wrap bits"
                    : x.Kind == Stylize && !StyEq(x.Sty, y.Sty) ? "Stylizer arguments"
                    : x.Kind == Lut && !LutEq(x.Lut, y.Lut) ? "grading-pass arguments"
                    : x.Kind == Lut && x.Ok != y.Ok ? $"RunLut's answer (told {x.Ok}, the backend's {y.Ok})" : null;
                if (why != null) { _bad++; Note($"list of frame {L.Frame}: call #{i} of {a.Count}/{b.Count} ({Names[x.Kind]}) differs: {why}"); return; }
            }
            if (a.Count != b.Count) { _bad++; Note($"list of frame {L.Frame}: {a.Count} calls issued vs {b.Count} received" + (b.Count > n ? $" (first extra received: {Names[b[n].Kind]})" : $" (first missing: {Names[a[n].Kind]})")); }
        }
        finally { a.Clear(); b.Clear(); }
    }
    static uint Bits(float f) => (uint)BitConverter.SingleToInt32Bits(f);
    static bool StyEq(in GlBackend.StylizeArgs x, in GlBackend.StylizeArgs y) =>
        x.Lut1 == y.Lut1 && x.Lut1W == y.Lut1W && x.Lut1H == y.Lut1H && x.Lut2 == y.Lut2 && x.Lut2W == y.Lut2W && x.Lut2H == y.Lut2H && Bits(x.PaletteLerp) == Bits(y.PaletteLerp)
        && x.Layers == y.Layers && x.LayersW == y.LayersW && x.LayersH == y.LayersH && x.Block == y.Block && Bits(x.Intensity) == Bits(y.Intensity);
    static bool LutEq(in LutArgs x, in LutArgs y) =>
        x.Lut1 == y.Lut1 && x.Lut2 == y.Lut2 && x.Mask == y.Mask && Bits(x.Exposure) == Bits(y.Exposure) && Bits(x.MaskScaleX) == Bits(y.MaskScaleX) && Bits(x.MaskScaleY) == Bits(y.MaskScaleY)
        && x.Hdr == y.Hdr && x.Linear == y.Linear && x.Vignette == y.Vignette
        && Bits(x.VigSettings.x) == Bits(y.VigSettings.x) && Bits(x.VigSettings.y) == Bits(y.VigSettings.y) && Bits(x.VigSettings.z) == Bits(y.VigSettings.z) && Bits(x.VigSettings.w) == Bits(y.VigSettings.w)
        && Bits(x.VigColor.x) == Bits(y.VigColor.x) && Bits(x.VigColor.y) == Bits(y.VigColor.y) && Bits(x.VigColor.z) == Bits(y.VigColor.z) && Bits(x.VigColor.w) == Bits(y.VigColor.w)
        && Bits(x.VigCenter.x) == Bits(y.VigCenter.x) && Bits(x.VigCenter.y) == Bits(y.VigCenter.y)
        && x.Blend == y.Blend && x.Lut1Blend == y.Lut1Blend && x.Lut2Blend == y.Lut2Blend && x.Lut2BlendFrom == y.Lut2BlendFrom && Bits(x.BlendAmount) == Bits(y.BlendAmount);   // (kit #11 gpu2 review fix)
    static void Note(string s) { if (++_notes <= 20) Log.Info("[mtcheck] " + s); }

    public static string Summary() => !MtRender.CheckOn ? null :
        $"[mtcheck] {_lists} command lists: the calls the backend received vs the single-threaded sequence: {_events} calls ({_draws} draws, {_verts} vertices; {_adds} lit draws (DrawAdd), {_luts} grading passes; " +
        $"{_builds} world draws built on the render thread and compared with the main thread's build from the original inputs; {_handles} texture handles taken early, {_handleBad} differ from Handle's); {_bad} mismatches";
}
