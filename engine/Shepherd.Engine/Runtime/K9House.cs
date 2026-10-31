using Shepherd.Engine.Native;
namespace Shepherd.Engine.Runtime;

/// <summary>Kit #9 engine (vita/notes/kit9-engine.md): the engine's own housekeeping on a fixed 16-slot phase cycle (SHEPHERD_K9_PHASE; from
/// SimCity's 16-phase cycle / RollerCoaster Tycoon's tick128) and the one purgeable cache arena (SHEPHERD_K9_PURGE; from Doom's zone
/// allocator). One slice runs at the end of each frame (Lifecycle.Frame, after the render and the registry's end of frame), never game logic,
/// never a GC trigger of the GC's own:
/// <list type="bullet">
/// <item>slot 0: the find indexes' per-type lists nobody asked for in 600 frames leave (Native/Registry.cs; rebuilt from a full scan on their
/// next use);</item>
/// <item>slot 4: the engine's per-object memo tables drop entries of destroyed objects -- NotifyRectChanges' last sizes, the pointer hit test's
/// root memo, the per-frame canvas matrices, the cameras' kept draw orders (a destroyed object is never asked for again; the per-frame memos
/// are recomputed in a later frame anyway);</item>
/// <item>slot 8 (purgeable arena): the engine's rebuildable caches -- the registry's big snapshot buffers not asked for in 300 frames;</item>
/// <item>slot 12 (review fix, SHEPHERD_K9_WHEEL_COMPACT): the timer wheel's heaps drop stopped coroutines and cancelled timers (Runtime/Lifecycle.cs
/// Coroutines / Invokes CompactHeap) -- kept until their wake-up time before, which never comes while Time.timeScale is 0.</item>
/// </list>
/// The purgeable arena also empties itself (every waiting native block back to newlib, the transform memo free list, every pooled snapshot
/// buffer) right before the black-frame collection of a scene transition (Runtime/BlackFrameGc.cs), so that collection reclaims them: what
/// each owner needs it rebuilds on its next use (a new buffer, a new memo, a malloc'd block -- exactly what it made before the caches existed).
/// Review fix (SHEPHERD_K9_PHASE=0 used to leave slots 0 / 4 / 8 / 12 running while the start line said "off"): with PHASE off there is no
/// cycle -- the memo tables' dead keys stay (kit #8's tables) and the find / sort / purge / wheel rows' upkeep runs at the end of every frame.</summary>
[ProfileOnly]
public static class K9House
{
    static readonly bool On = K9Engine.Phase || K9Engine.HouseRows;
    static int _slot;
    /// <summary>Lifecycle.Frame, the end of every frame.</summary>
    public static void EndFrame()
    {
        if (!On) return;
        int f = Time.FrameCount;
        if (K9Engine.Find) K9Engine.NoteTypedFrame(f);   // (review fix: the find lists' classification per frame, for the end line)
        if (!K9Engine.Phase) { EveryFrame(f); return; }
        int s = _slot; _slot = (s + 1) & 15;
        switch (s)
        {
            case 0: if (K9Engine.Find) Registry.AgeTyped(f, 600); break;
            case 4:
                if (K9Engine.Phase) Render.SceneRenderer.PruneDeadKeys();
                if (K9Engine.Sort) Render.SceneRenderer.AgeCamOrders(f);
                break;
            case 8: if (K9Engine.Purge) Note(Objects.PurgeCaches(false, f)); break;
            case 12: if (K9Engine.WheelCompact) CompactWheel(); break;
            default: return;
        }
        K9Engine.PhaseSlices++;
    }
    /// <summary>SHEPHERD_K9_PHASE=0 (review fix): no cycle -- each row's upkeep at the end of every frame; the per-object memo tables' dead keys
    /// are not pruned (kit #8 kept them).</summary>
    static void EveryFrame(int f)
    {
        if (K9Engine.Find) Registry.AgeTyped(f, 600);
        if (K9Engine.Sort) Render.SceneRenderer.AgeCamOrders(f);
        if (K9Engine.Purge) Note(Objects.PurgeCaches(false, f));
        if (K9Engine.WheelCompact) CompactWheel();
    }
    /// <summary>Runtime/BlackFrameGc.cs, right before its collection on a covered frame: the whole purgeable arena is dropped, and the timer
    /// wheel's dead entries are released (review fixes): the heaps compacted -- a sleeping timer whose behaviour was destroyed is cancelled
    /// there, as kit #8's next tick cancels it -- and then the cancelled timers leave Invokes' Items / due list and the finished coroutines the
    /// wheel's group lists now, instead of at the next tick / pass, which runs after this collection: a destroyed behaviour (and all it
    /// references) is not kept alive through it by the wheel's bookkeeping (kit #8's tick had released it if one tick ran since the destroy).</summary>
    public static void BeforeCollect()
    {
        if (K9Engine.Purge) Note(Objects.PurgeCaches(true, Time.FrameCount));
        if (K9Engine.WheelCompact) { CompactWheel(); K9Engine.WheelFlushed += Invokes.ReleaseDead() + Coroutines.ReleaseDead(); }
    }
    /// <summary>Review fix (SHEPHERD_K9_WHEEL_COMPACT): both timer-wheel heaps without their dead entries.</summary>
    static void CompactWheel() => K9Engine.WheelCompacted += Coroutines.CompactHeap() + Invokes.CompactHeap();
    static void Note(long bytes) { if (bytes > 0) { K9Engine.Purged++; K9Engine.PurgedBytes += bytes; } }
}
