using System.Diagnostics;
namespace Shepherd.Engine.Runtime;

/// <summary>kit #4 (M1, black-frame GC; vita/notes/kit4-bootgc.md). Unity's Resources.UnloadUnusedAssets runs a full garbage collection as
/// part of its work; the engine's did none. The game asks for it at its scene transitions -- MMTransition.LoadScene, between the
/// buffer scene and the next scene's load, while the screen is black -- and at room changes, the book, the comic menu, a language
/// change. Here it collects the major heap when the frame on screen is covered (Render/BlackFrame: an opaque full-screen quad, the
/// transition's black hold; the loading icon on top is tolerated), so the collection the game asks for lands where nobody sees it,
/// and the old generation starts the next scene or room with its garbage gone -- fewer majors of sgen's own during visible play,
/// where every one is a 0.8-3 s freeze on the device. A visible call (the transition already fading out, the book) still collects
/// nothing, as before.
/// Growth gate: a black-frame major only when the major heap has grown by at least half of sgen's allowance since the last major
/// (sgen starts a major of its own after about a third of the live heap, at least 16 MiB, of growth: SGEN_DEFAULT_ALLOWANCE_HEAP_SIZE_RATIO,
/// the 4 MiB nursery x 4), so a room change that made little garbage does not pay a ~1-3 s major for nothing. The heap after each
/// major (sgen's own included) is sampled on the first frame after it (<see cref="Track"/>, one GC.CollectionCount per frame).
/// SHEPHERD_BLACK_GC (env file): unset = scene for the Vita profile, off for the Mac default | scene | on | always (every covered call:
/// Unity's rule) | 0 / off (the engine's previous UnloadUnusedAssets: no collection). Only the collection's timing changes: what can differ
/// is when old objects are finalized and weak references cleared -- not deterministic in Unity either.
/// scene (kit #5 integration, vita/notes/kit5-integration.md "The menus gate"): kit #4's rule, plus Unity's rule for the covered call
/// that follows a real scene load -- when the scenes loaded since the previous UnloadUnusedAssets call created at least
/// SceneObjectsMin objects (the Main Menu: 18,084; the game's BufferScene: 4), that call collects whatever the growth gate says. The
/// Main Menu transition's second call then always collects, as kit #4's device runs did (3 of 3); kit #4's gate decided it from where
/// sgen's own majors fell inside the one-frame scene load, and the kit #5 eboots' boot heap moved that knife edge (VLX boot samples).
/// The call before the load (after the BufferScene) keeps the gate: collecting there moved sgen's in-load majors next to the menu's
/// 4 MiB SDF atlas decodes (arena peak ~131 MiB, kit #5 menus review and the integration's `always` probes). on = kit #4's rule exactly.</summary>
public static class BlackFrameGc
{
    static readonly string Mode = (Environment.GetEnvironmentVariable("SHEPHERD_BLACK_GC") ?? (Profile.Vita ? "scene" : "off")).Trim().ToLowerInvariant();
    public static readonly bool On = Mode is "on" or "1" or "always" or "scene";
    static readonly bool Always = Mode == "always";
    static readonly bool AfterScene = Mode == "scene";
    const int SceneObjectsMin = 1000;
    static int _sceneObjects;               // objects the scenes loaded since the last UnloadUnusedAssets call created (scene mode)
    /// <summary>Icalls/BundleIcalls (SceneManager.LoadSceneAsync, the game's scene loads; not the boot's level0): a scene finished loading, creating <paramref name="objects"/> objects.</summary>
    public static void NoteSceneLoaded(int objects) { if (AfterScene) _sceneObjects += objects; }
    const double GrowthFraction = 0.5, AllowanceRatio = 1.0 / 3, MinAllowanceMiB = 16;
    static long _base = -1;                 // the major heap (GCMemoryInfo.HeapSizeBytes) right after the last major collection seen
    static int _majorsSeen = -1;

    /// <summary>Every frame (Render/BlackFrame.EndFrame): after a major collection, the heap it left is the growth gate's base.</summary>
    public static void Track()
    {
        if (!On) return;
        int majors = GC.CollectionCount(GC.MaxGeneration);
        if (majors != _majorsSeen) { _majorsSeen = majors; _base = HeapBytes(); }
    }
    public static int Calls, Collections, Skipped, Visible;
    public static double CollectMs;

    static long HeapBytes() => GC.GetGCMemoryInfo().HeapSizeBytes;   // sgen: major blocks + large objects at the last collection

    /// <summary>Resources.UnloadUnusedAssets (Icalls/ResourceIcalls.cs), on the main thread.</summary>
    public static void OnUnloadUnusedAssets()
    {
        if (!On) return;
        Calls++;
        int frame = Time.FrameCount;
        int sceneObjects = _sceneObjects; _sceneObjects = 0;
        bool afterScene = AfterScene && sceneObjects >= SceneObjectsMin;
        bool recent = Render.BlackFrame.LastFrame >= frame - 1;
        bool covered = Render.BlackFrame.LastCovered && recent;
        // kit #11 walk (SHEPHERD_K11_COVER=1, opt-in): a black hold with 15-20 % on top counts as covered for the growth gate only (no scene-load
        // override: Render/BlackFrame.WideCover)
        bool wide = !covered && Render.BlackFrame.LastCoveredWide && recent;
        if (wide) { covered = true; afterScene = false; }
        if (!covered) { Visible++; if (Visible <= 3) Log.Info($"[blackgc] f{frame} UnloadUnusedAssets on a visible frame: no collection (as before)"); return; }
        Track();   // (a major since this frame began: its heap is the base)
        long heap = HeapBytes();
        double allowance = Math.Max(_base * AllowanceRatio, MinAllowanceMiB * 1048576.0), grown = _base < 0 ? double.PositiveInfinity : heap - _base;
        if (!Always && !afterScene && grown < GrowthFraction * allowance)
        {
            Skipped++;
            Log.Info($"[blackgc] f{frame} UnloadUnusedAssets on a covered frame ({Render.BlackFrame.LastNote}): no collection -- the major heap grew {grown / 1048576.0:F1} MiB since the last major, under half of sgen's allowance ({allowance / 1048576.0:F1} MiB)");
            return;
        }
        long inUse0 = GC.GetTotalMemory(false);
        K9House.BeforeCollect();   // kit #9 engine (SHEPHERD_K9_PURGE): the purgeable arena is dropped so this collection reclaims it
        var sw = Stopwatch.StartNew();
#if !SHEPHERD_NO_K13WALKC
        var k13n = K13Unload.Collect();   // kit #13 walkc: a full collection; with SHEPHERD_K13_UNLOAD on, Unity's release of unreached assets inside it
#else
        GC.Collect();
#endif
        sw.Stop();
        Collections++; CollectMs += sw.Elapsed.TotalMilliseconds;
        _base = HeapBytes(); _majorsSeen = GC.CollectionCount(GC.MaxGeneration);
        Log.Info($"[blackgc] f{frame} UnloadUnusedAssets on a covered frame ({Render.BlackFrame.LastNote}): full collection {sw.Elapsed.TotalMilliseconds:F0} ms, GC heap in use {inUse0 / 1048576.0:F1} -> {GC.GetTotalMemory(false) / 1048576.0:F1} MiB" +
                 (double.IsInfinity(grown) ? " (no major seen before it)" : $" (the major heap grew {grown / 1048576.0:F1} MiB since the last major; allowance {allowance / 1048576.0:F1} MiB)") +
                 (afterScene && grown < GrowthFraction * allowance ? $"; after a scene load of {sceneObjects} objects (mode scene: Unity's rule for this call)" : "")
#if !SHEPHERD_NO_K13WALKC
                 + k13n
#endif
                 );
    }

    public static string Summary() => !On ? null : $"[blackgc] UnloadUnusedAssets: {Calls} calls, {Collections} full collections on covered frames ({CollectMs:F0} ms), {Skipped} covered calls skipped by the growth gate, {Visible} on visible frames (mode {Mode})";
}
