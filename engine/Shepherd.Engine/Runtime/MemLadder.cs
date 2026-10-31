using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
namespace Shepherd.Engine.Runtime;

/// <summary>Device kit #10, track memory (vita/notes/kit10-memory.md): THE MEMORY-PRESSURE LADDER, the engine's half (the host's is
/// vita/shepherd/k10_mem.inc.c). When free memory falls, optional memory is given back in a FIXED ORDER long before the memory guard would end
/// the session (the guard acts at the GC heap's cap and at newlib's last 4 MiB):
/// <list type="number">
/// <item><see cref="Preloads"/> -- data read ahead for a scene that has not been asked for yet (the cache track's K8Preload store);</item>
/// <item><see cref="LearningCaches"/> -- in-RAM copies of the learning caches;</item>
/// <item><see cref="ImageCaches"/> -- the engine's re-readable buffers: the V3 diet's whole-file buffers nothing read this frame (re-read on
///   demand, Assets/Diet.cs), the per-frame sprite-atlas trees, the kit #9 purgeable arena (Native/Objects.cs PurgeCaches: rebuilt on use);</item>
/// <item><see cref="WarmReturn"/> -- the warm-return cache (scenes kept for a quick way back);</item>
/// <item><see cref="VramResidency"/> -- video memory kept beyond what is drawn.</item>
/// </list>
/// Every 16 frames (Assets/Diet.cs EndFrame, the end of the frame) the engine asks the host for the pressure level 0..5 (the host turns the GC
/// heap's and newlib's headroom into it at each heartbeat); when the level rises, the steps above the old level up to the new one run once,
/// in order (each registered piece: <see cref="IRelease.Release"/>), and the host counts what they gave back (the K10 MEM end line). A piece
/// may only drop what is made again -- re-read, re-decoded, rebuilt -- exactly as it was: the ladder changes when memory is used, never a result.
/// Other tracks register their caches with <see cref="Register"/> (a static constructor, a first use). Off (the host answers -1: SHEPHERD_K10_LADDER
/// unset or 0, SHEPHERD_K10_MEM=0, no Vita host) = kit #9: no call after the first.</summary>
[ProfileOnly]
public static class MemLadder
{
    public const int Preloads = 1, LearningCaches = 2, ImageCaches = 3, WarmReturn = 4, VramResidency = 5;
    /// <summary>One piece of optional memory. <see cref="Release"/> gives it back now and returns the bytes freed (an estimate is fine); its owner
    /// makes it again on its next use, the same as before.</summary>
    public interface IRelease { string Name { get; } long Release(); }

    static readonly bool On = Profile.Vita && Diag.VitaHost.On;
    static IRelease[][] _steps;   // (made at the first use: with the ladder off nothing is allocated -- kit #9's heap exactly)
    static int _level, _tick;
    static bool _off, _builtins;
    public static int Level => _level;
    public static int StepsRun, Released;

    /// <summary>Adds a piece of optional memory to a step (1..5).</summary>
    public static void Register(int step, IRelease piece)
    {
        if (step < 1 || step > 5 || piece == null) return;
        _steps ??= new IRelease[6][];
        var a = _steps[step];
        int n = a?.Length ?? 0;
        var b = new IRelease[n + 1];
        if (a != null) Array.Copy(a, b, n);
        b[n] = piece;
        _steps[step] = b;
    }

    static void Builtins()
    {
        _builtins = true;
        Register(ImageCaches, new FileBuffers());
        Register(ImageCaches, new AtlasTrees());
        Register(ImageCaches, new PurgeableArena());
    }

    /// <summary>Assets/Diet.cs EndFrame: every 16th frame, the host's level; a rise runs the steps it crossed.</summary>
    public static void Tick()
    {
        if (GeoCensusAt > 0 && Time.FrameCount == GeoCensusAt) GeoCensus();   // (test only: SHEPHERD_K10_GEO_CENSUS=<frame>)
        if (!On || _off || (++_tick & 15) != 0) return;
        int lv = Host(0, 0);
        if (lv < 0) { _off = true; return; }
        if (!_builtins) Builtins();
        if (lv > 5) lv = 5;
        for (int s = _level + 1; s <= lv; s++) Run(s);
        _level = lv;
    }

    static void Run(int step)
    {
        var a = _steps?[step];
        long total = 0;
        string names = "";
        if (a != null)
            foreach (var p in a)
            {
                long b = 0;
                try { b = p.Release(); } catch (Exception e) { Log.Exception(e, "MemLadder " + p.Name); }
                total += b > 0 ? b : 0;
                names += (names.Length > 0 ? ", " : "") + p.Name + " " + (b / 1024) + " KiB";
            }
        StepsRun++; Released += (int)(total / 1024);
        Host(1, step); Host(2, (int)Math.Min(total / 1024, int.MaxValue));
        Log.Info($"[k10] memory ladder step {step} ({StepName(step)}) at frame {Time.FrameCount}: {(names.Length > 0 ? names : "nothing registered")} -- {total / 1024} KiB given back");
    }

    static string StepName(int s) => s switch { 1 => "preloads", 2 => "in-RAM learning caches", 3 => "image caches", 4 => "the warm-return cache", _ => "extra VRAM residency" };

    // ---- the mesh / vertex diet's measurement (test only): SHEPHERD_K10_GEO_CENSUS=<frame> -> one [k10] geometry line at that frame ----
    static readonly int GeoCensusAt = int.TryParse(Environment.GetEnvironmentVariable("SHEPHERD_K10_GEO_CENSUS"), out var gc) ? gc : 0;
    /// <summary>What the engine's geometry records hold, and what a mobile-style diet would save exactly: a UV channel whose z / w are all 0 kept
    /// as Vector2 (8 B a vertex less), normals / tangents (kept only where something reads them), 16-bit indices where every index of the record is
    /// below 65,536 (2 B an index less).</summary>
    static void GeoCensus()
    {
        long mN = 0, mV = 0, mPos = 0, mCol = 0, mUv = 0, mNrm = 0, mTan = 0, mIdx = 0, uvZw0V = 0, idx16 = 0, nrmN = 0, tanN = 0;
        long cN = 0, cV = 0, cB = 0, cUvZw0V = 0, cIdx16 = 0, sN = 0, sB = 0;
        foreach (var n in Native.Objects.All)
        {
            if (n is Native.NMesh m)
            {
                mN++; mV += m.Pos.Length; mPos += m.Pos.LongLength * 12; mCol += (m.Col?.LongLength ?? 0) * 4; mUv += (m.Uv0?.LongLength ?? 0) * 16;
                if (m.Normals != null) { nrmN++; mNrm += m.Normals.LongLength * 12; }
                if (m.Tan != null) { tanN++; mTan += m.Tan.LongLength * 16; }
                if (m.Uv0 != null && ZwZero(m.Uv0)) uvZw0V += m.Uv0.Length;
                foreach (var sub in m.Sub) { if (sub == null) continue; mIdx += sub.LongLength * 4; if (Max(sub) < 65536) idx16 += sub.Length; }
            }
            else if (n is Native.NCanvasRenderer c)
            {
                cN++; cV += c.Pos.Length; cB += c.Pos.LongLength * 12 + (c.Col?.LongLength ?? 0) * 4 + (c.Uv0?.LongLength ?? 0) * 16 + c.Idx.LongLength * 4 + (c.Tan?.LongLength ?? 0) * 16;
                if (c.Uv0 != null && ZwZero(c.Uv0)) cUvZw0V += c.Uv0.Length;
                if (Max(c.Idx) < 65536) cIdx16 += c.Idx.Length;
            }
            else if (n is Native.NSprite sp) { sN++; sB += (sp.Vertices?.LongLength ?? 0) * 8 + (sp.Uv?.LongLength ?? 0) * 8 + (sp.Triangles?.LongLength ?? 0) * 2; }
        }
        long save = uvZw0V * 8 + idx16 * 2 + cUvZw0V * 8 + cIdx16 * 2;
        Log.Info($"[k10] geometry at frame {Time.FrameCount}: NMesh {mN} ({mV} vertices) = {(mPos + mCol + mUv + mNrm + mTan + mIdx) / 1024} KiB (positions {mPos / 1024}, colours {mCol / 1024}, " +
                 $"uv0 {mUv / 1024} as Vector4 -- {uvZw0V} vertices with z = w = 0, normals {mNrm / 1024} in {nrmN} records, tangents {mTan / 1024} in {tanN}, indices {mIdx / 1024} -- " +
                 $"{idx16} of them below 65,536); CanvasRenderer {cN} ({cV} vertices) = {cB / 1024} KiB ({cUvZw0V} vertices with uv z = w = 0, {cIdx16} indices below 65,536); " +
                 $"NSprite {sN} = {sB / 1024} KiB (already Vector2 + 16-bit) -- a mobile-style diet would save {save / 1024} KiB of managed heap (uv0 as Vector2 where z = w = 0, 16-bit indices)");
    }
    static bool ZwZero(UnityEngine.Vector4[] a) { foreach (var v in a) if (BitConverter.SingleToInt32Bits(v.z) != 0 || BitConverter.SingleToInt32Bits(v.w) != 0) return false; return true; }   // (+0.0 bit for bit: a -0 would not come back)
    static int Max(int[] a) { int x = 0; foreach (var v in a) if (v > x) x = v; return x; }

    // ---- the built-in pieces (step 3) ----
    sealed class FileBuffers : IRelease
    {
        public string Name => "whole-file buffers";
        public long Release()
        {
            if (Assets.Diet.KeepFiles || Assets.Loader.Db == null) return 0;
            var (n, bytes) = Assets.Loader.Db.ReleaseIdle(Shepherd.Assets.SerializedFile.Clock);   // every buffer nothing read this frame
            Assets.Diet.FilesReleased += n; Assets.Diet.FileBytesReleased += bytes;
            return bytes;
        }
    }
    sealed class AtlasTrees : IRelease
    {
        public string Name => "sprite-atlas trees";
        public long Release() { Assets.Loader.DropTransientCaches(); return 0; }
    }
    sealed class PurgeableArena : IRelease
    {
        public string Name => "purgeable arena";
        public long Release() => K9Engine.Purge ? Native.Objects.PurgeCaches(true, Time.FrameCount) : 0;
    }

    // Not inlined: the interpreter binds a method's P/Invokes when it transforms it (the Mac never calls this).
    [MethodImpl(MethodImplOptions.NoInlining)] static int Host(int op, int arg) => shepherd_vita_engine_mem(op, arg);
    [DllImport("__Internal")] static extern int shepherd_vita_engine_mem(int op, int arg);
}
