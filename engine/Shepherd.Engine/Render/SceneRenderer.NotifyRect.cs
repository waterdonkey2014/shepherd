using Shepherd.Engine.Native;
namespace Shepherd.Engine.Render;

// menu-speed, stage "top remaining hotspots": NotifyRectChanges over a dirty queue.
//
// The walk visited every transform of the registry (~10,700 at the idle Main Menu: 6,520 rect transforms, 5,863 of them in asset space
// (scene 0, prefab templates), 657 that pass its test) to find the ~4 per frame whose size it must compare. A transform needs the comparison
// only when its test passes and its world stamp moved since its size was last compared (NTransform.SizeSeen). Both change only through:
// - a state change of the transform or of an ancestor, or a parent link (NTransform.Changed: the world stamp is a function of exactly the
//   chain's states and links, and a transform whose parent link changes is itself changed),
// - a new transform (Objects.Bind), IsRect (a state change), its GameObject's scene (NGameObject.Scene) or its GameObject being set (a transform
//   without one stays queued); Destroyed only ever makes the test fail.
// Each of those queues the transform (NTransform.NrcQueue). The walk takes the queue, collects each queued transform and its current subtree,
// and runs the original loop body on them in registry order (ascending Slot: Registry's order). Everything else the original walk visits would
// be skipped by that same body. When the body sends OnRectTransformDimensionsChange (game code, which may change any transform), the snapshot the
// original walk iterates is taken just before (nothing has run since the walk started, so it is the walk-start snapshot) and the rest of the walk
// is the original loop over it, from the transform after that one. What game code changes during the walk is queued for the next walk, where the
// original would also have compared it (an earlier transform) or compares it again idempotently (a later one: seen).
// Falls back to the full walk on the first walk, when the slot mirror is not exact, and when the queue or the collected set is large (960, or a
// quarter of the transforms: about the same cost; the bound keeps the lists small objects through a scene load). SHEPHERD_HOTSPOT_CHECK=1: every queued walk also tests every transform the original would compare and requires it
// to be in the collected set. SHEPHERD_HOTSPOTS=0: the full walk every frame (and nothing is queued).
public static partial class SceneRenderer
{
    static List<NTransform> _nrcWork = new(NTransform.NrcQueueCap);   // (both queues at the cap from the start: they never grow)
    static readonly NTransform[] _nrcCand = new NTransform[NTransform.NrcQueueCap]; static readonly int[] _nrcKey = new int[NTransform.NrcQueueCap]; static int _nrcN;
    static bool _nrcWalked, _nrcFast, _nrcHaveSnap; static Objects.Snap _nrcSnap;
    /// <summary>Walks so far: queued, of them finished by the original loop after game code ran, full; transforms collected by the queued walks.</summary>
    internal static long NrcQueuedWalks, NrcRemainders, NrcFullWalks, NrcCollected;

    /// <summary>The queue so far (NTransform.NrcQueue swapped for an empty one, flags cleared): what was dirtied before this walk.</summary>
    static List<NTransform> NrcTakeQueue()
    {
        var q = NTransform.NrcQueue; NTransform.NrcQueue = _nrcWork; _nrcWork = q;
        foreach (var t in q) t.NrcQueued = false;
        NTransform.NrcOverflow = false;   // (taken with the queue: a full walk follows, or the queued walk had nothing to miss)
        return q;
    }
    /// <summary>The queued walk; false = run the full walk (nothing done yet).</summary>
    static bool NotifyRectChangesQueued()
    {
        if (!_nrcWalked || !Objects.SlotsExact || NTransform.NrcOverflow) { _nrcWalked = true; NrcFullWalks++; return false; }
        var items = Registry.Transforms.Items();   // (the Drain the original's Snapshot made here; RegistryCheck compares it in check runs)
        var q = NrcTakeQueue();
        _nrcN = 0; int cap = Math.Min(NTransform.NrcQueueCap, Math.Max(512, items.Count / 4));
        bool ok = true;
        foreach (var t in q) if (!NrcCollect(t, cap)) { ok = false; break; }
        q.Clear();
        if (!ok) { NrcClear(); NrcFullWalks++; return false; }   // (the queue is taken: the full walk covers it)
        if (_nrcN > 1) { NrcSort(_nrcN); NrcUnique(); }   // (registry order; a transform collected twice -- queued twice, or under two queued ancestors -- once)
        NrcQueuedWalks++; NrcCollected += _nrcN;
        if (HotCfg.Check) NrcCheck(items);
        _nrcFast = true; _nrcHaveSnap = false;
        try
        {
            for (int i = 0; i < _nrcN; i++)
            {
                var t = _nrcCand[i];
                if (!NotifyRect(t) || !_nrcHaveSnap) continue;
                _nrcFast = false; NrcRemainders++;   // game code ran: the rest is the original walk over the walk-start snapshot, after t
                bool after = false;
                // (the flag is cleared before the loop: its enumerator hands the buffer back when the loop ends, an exception included,
                // so the finally below must not drain the same snapshot a second time)
                var snap = _nrcSnap; _nrcHaveSnap = false;
                foreach (var n in snap) { if (after) NotifyRect(n); else after = ReferenceEquals(n, t); }
                break;
            }
        }
        finally { _nrcFast = false; if (_nrcHaveSnap) { foreach (var _ in _nrcSnap) { } _nrcHaveSnap = false; } NrcClear(); }   // (an unused snapshot's buffer goes back)
        return true;
    }
    static readonly NTransform[] _nrcTmpC = new NTransform[NTransform.NrcQueueCap]; static readonly int[] _nrcTmpK = new int[NTransform.NrcQueueCap];
    /// <summary>_nrcCand[0..n) by ascending _nrcKey (slots: unique, so the order is the registry's whatever sort finds it): insertion for short
    /// runs, then a bottom-up merge (no allocation: Array.Sort's generic pair sort allocates in the full-AOT build).</summary>
    static void NrcSort(int n)
    {
        var k = _nrcKey; var c = _nrcCand;
        const int Run = 16;
        for (int lo = 0; lo < n; lo += Run)
        {
            int hi = Math.Min(n, lo + Run);
            for (int i = lo + 1; i < hi; i++) { int x = k[i]; var y = c[i]; int j = i - 1; while (j >= lo && k[j] > x) { k[j + 1] = k[j]; c[j + 1] = c[j]; j--; } k[j + 1] = x; c[j + 1] = y; }
        }
        if (n <= Run) return;
        var tk = _nrcTmpK; var tc = _nrcTmpC;
        for (int w = Run; w < n; w *= 2)
        {
            for (int lo = 0; lo < n; lo += 2 * w)
            {
                int mid = Math.Min(n, lo + w), hi = Math.Min(n, lo + 2 * w), a = lo, b = mid, o = lo;
                while (a < mid && b < hi) { if (k[a] <= k[b]) { tk[o] = k[a]; tc[o++] = c[a++]; } else { tk[o] = k[b]; tc[o++] = c[b++]; } }
                while (a < mid) { tk[o] = k[a]; tc[o++] = c[a++]; }
                while (b < hi) { tk[o] = k[b]; tc[o++] = c[b++]; }
            }
            (k, tk) = (tk, k); (c, tc) = (tc, c);
        }
        if (!ReferenceEquals(k, _nrcKey)) { Array.Copy(k, _nrcKey, n); Array.Copy(c, _nrcCand, n); }
        Array.Clear(_nrcTmpC, 0, Math.Min(n, _nrcTmpC.Length)); if (!ReferenceEquals(c, _nrcCand)) Array.Clear(c, 0, n);
    }
    /// <summary>_nrcCand[0..n), sorted by slot, without repeats (a slot is one record: live members have distinct slots).</summary>
    static void NrcUnique()
    {
        int w = 1;
        for (int i = 1; i < _nrcN; i++) if (_nrcKey[i] != _nrcKey[w - 1]) { _nrcKey[w] = _nrcKey[i]; _nrcCand[w++] = _nrcCand[i]; }
        Array.Clear(_nrcCand, w, _nrcN - w); _nrcN = w;
    }
    static void NrcSnap() { _nrcSnap = Registry.Transforms.Snapshot(); _nrcHaveSnap = true; }
    static void NrcClear() { Array.Clear(_nrcCand, 0, _nrcN); _nrcN = 0; }
    /// <summary>t and its subtree, the registry members among them (repeats allowed: NrcUnique); false = more than cap.</summary>
    static bool NrcCollect(NTransform t, int cap)
    {
        if (!t.Destroyed && Registry.Transforms.Has(t))
        {
            if (_nrcN >= cap) return false;
            _nrcCand[_nrcN] = t; _nrcKey[_nrcN] = t.Slot; _nrcN++;
        }
        var ch = t.Children;
        for (int i = 0; i < ch.Count; i++) if (!NrcCollect(ch[i], cap)) return false;
        return true;
    }
    /// <summary>SHEPHERD_HOTSPOT_CHECK=1: every transform the original walk would compare now (its test passes and its size is not seen) is in the
    /// collected set.</summary>
    static void NrcCheck(List<NObj> items)
    {
        foreach (var n in items)
        {
            if (n is not NTransform t || !t.IsRect || t.Destroyed || t.Go == null || t.Go.Scene == 0) continue;
            bool need = !t.SizeSeen();
            HotCheck.Bool(HotCheck.Nrc, true, !need || Array.BinarySearch(_nrcKey, 0, _nrcN, t.Slot) >= 0 && ReferenceEquals(_nrcCand[Array.BinarySearch(_nrcKey, 0, _nrcN, t.Slot)], t));
        }
    }
}
