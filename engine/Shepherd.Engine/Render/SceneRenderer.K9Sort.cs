using Shepherd.Engine.Native;
namespace Shepherd.Engine.Render;

// Device kit #9, track engine (vita/notes/kit9-engine.md): the world pass's sort, rows SHEPHERD_K9_SORT and SHEPHERD_K9_HOT.
//
// The order a camera pass draws its items in is the one permutation the comparison WCmp allows: (sorting layer, order) ascending, camera
// distance descending, scan index (Seq, unique in the pass) ascending, with the SortingGroup units of GCmp -- a strict total order, so EVERY
// correct sort of the same items gives the same permutation, whatever order it starts from (kit #8 already started from last frame's
// permutation when the item count was unchanged). Two changes, neither touching the order:
// - SORT (temporal coherence): each camera keeps the renderers of its last pass in their sorted order; the next pass starts from them (the
//   ones still in this pass, in that order, then this pass's new ones in scan order) and repairs that start by insertion (a few shifts when a
//   few items moved), falling back to the merge sort when the repair would be long. Kit #8 only reused the previous pass's permutation of
//   item INDICES, and only when the count was equal -- so a renderer appearing or vanishing (every gameplay frame) or a second camera pass
//   (the counts alternate) meant a full sort every pass.
// - HOT (packed hot records): the comparison reads a packed key per item (layer, order, distance, seq, group flag: 20 bytes, contiguous)
//   instead of the draw item (~200 bytes with its references and matrix): the sort's working set is a tenth -- on the A9 (no hardware
//   prefetch, ~270 ns per missed line) the comparisons stop missing the cache once per item. Items with SortingGroups compare through GCmp
//   on the draw items as before.
// The renderer's pass index (validated by the pass's item at that index) says which of last pass's renderers are in this one; a pass stamp per
// item index (the frame-number-stamp idea: never cleared) marks the ones taken. =check sorts the kit #8 way too and counts differing permutations (the kit #8 one is drawn).
public static partial class SceneRenderer
{
    static readonly bool K9Sort = Runtime.K9Engine.Sort || Runtime.K9Engine.Hot, K9Seed = Runtime.K9Engine.Sort, K9Hot = Runtime.K9Engine.Hot,
        K9Check = Runtime.K9Engine.SortCheck || Runtime.K9Engine.HotCheck;
    struct SKey { public float Layer, Order, Dist; public int Seq; public bool G; }
    static SKey[] _sk = new SKey[256];
    static bool _skOn;
    /// <summary>A camera's last pass: its renderers in sorted order.</summary>
    sealed class CamOrder { public NComponent[] R = new NComponent[64]; public int N; public int Frame; }
    static readonly Dictionary<NBehaviour, CamOrder> _camOrder = new();
    static int _passStamp;
    static int[] _wChk = new int[256];

    static int[] SortWorldK9(int n, NBehaviour cam)
    {
        if (_wOrder.Length < n) { _wOrder = new int[n * 2]; _wTmp = new int[n * 2]; _wPrevN = -1; }
        if (K9Hot)
        {
            if (_sk.Length < n) _sk = new SKey[Math.Max(n, _sk.Length * 2)];
            for (int k = 0; k < n; k++) { ref var it = ref _wi[k]; ref var q = ref _sk[k]; q.Layer = it.Layer; q.Order = it.Order; q.Dist = it.Dist; q.Seq = it.Seq; q.G = it.Groups != null; }
            _skOn = true;
        }
        try
        {
            var o = _wOrder;
            if (K9Seed && Seed(o, n, cam))
            {
                int budget = 8 * n + 64;
                for (int i = 1; i < n && budget >= 0; i++) { int x = o[i], j = i - 1; while (j >= 0 && WCmp(o[j], x) > 0) { o[j + 1] = o[j]; j--; budget--; } o[j + 1] = x; }
                if (budget >= 0) Runtime.K9Engine.SortSeeded++;
                else { for (int k = 0; k < n; k++) o[k] = k; MergeSort(o, _wTmp, 0, n); Runtime.K9Engine.SortFull++; Runtime.K9Engine.SortRepairLong++; }
            }
            else if (!K9Seed && n == _wPrevN)
            {   // (HOT alone: kit #8's start -- last pass's permutation when the count is the same)
                int budget = 8 * n + 64;
                for (int i = 1; i < n && budget >= 0; i++) { int x = o[i], j = i - 1; while (j >= 0 && WCmp(o[j], x) > 0) { o[j + 1] = o[j]; j--; budget--; } o[j + 1] = x; }
                if (budget < 0) { for (int k = 0; k < n; k++) o[k] = k; MergeSort(o, _wTmp, 0, n); }
            }
            else { for (int k = 0; k < n; k++) o[k] = k; MergeSort(o, _wTmp, 0, n); if (n > 1) Runtime.K9Engine.SortFull++; }
            _wPrevN = n;
            if (K9Check) CheckOrder(o, n);
            if (K9Seed) Keep(o, n, cam);
            return o;
        }
        finally { _skOn = false; }
    }

    /// <summary>The start: the camera's last renderers still in this pass, in their kept order, then this pass's others in scan order. False when
    /// there is nothing to start from (no world cache, a first pass, or nothing kept in common).</summary>
    static bool Seed(int[] o, int n, NBehaviour cam)
    {
        if (!HotCfg.On || cam == null || !_camOrder.TryGetValue(cam, out var co) || co.N == 0) return false;
        int st = ++_passStamp; if (st == int.MaxValue) { _passStamp = 0; Array.Clear(_seedAt); return false; }
        if (_seedAt.Length < n) _seedAt = new int[Math.Max(n, _seedAt.Length * 2)];   // (new entries 0: never a pass stamp)
        for (int k = 0; k < n; k++) { var wc = (_wi[k].R as NRenderer)?.WC; if (wc == null) return false; wc.PassIdx = k; }
        int m = 0;
        var r = co.R;
        for (int i = 0; i < co.N; i++)
        {   // in this pass: the item at its index is this renderer (a renderer is at most one item of a pass, and every item's index was just
            // written, so a stale index from an earlier pass never finds its renderer there); taken once (_seedAt: this pass's stamp by item index)
            var wc = (r[i] as NRenderer)?.WC; if (wc == null) continue;
            int k = wc.PassIdx;
            if ((uint)k < (uint)n && ReferenceEquals(_wi[k].R, r[i]) && _seedAt[k] != st && m < n) { o[m++] = k; _seedAt[k] = st; }
        }
        if (m == 0) return false;
        for (int k = 0; k < n; k++) if (_seedAt[k] != st) { if (m == n) return false; o[m++] = k; }
        return m == n;
    }
    /// <summary>The seed's "taken" marks by item index: the pass stamp, never cleared (the frame-number-stamp idea; contiguous, 4 bytes an item --
    /// the renderer records stay at their size class: review round 2 moved the stamp out of WCache).</summary>
    static int[] _seedAt = new int[256];
    /// <summary>The camera's sorted renderers, kept for its next pass.</summary>
    static void Keep(int[] o, int n, NBehaviour cam)
    {
        if (cam == null) return;
        if (!_camOrder.TryGetValue(cam, out var co)) _camOrder[cam] = co = new CamOrder();
        if (co.R.Length < n) co.R = new NComponent[Math.Max(n, co.R.Length * 2)];
        for (int k = 0; k < n; k++) co.R[k] = _wi[o[k]].R;
        if (co.N > n) Array.Clear(co.R, n, co.N - n);
        co.N = n; co.Frame = Runtime.Time.FrameCount;
    }
    /// <summary>The housekeeping cycle: the kept orders of cameras gone or idle (no pass in 300 frames) are dropped (a next pass sorts fully).</summary>
    internal static void AgeCamOrders(int frame)
    {
        if (_camOrder.Count == 0) return;
        List<NBehaviour> old = null;
        foreach (var kv in _camOrder) if (kv.Key.Destroyed || frame - kv.Value.Frame > 300) (old ??= new()).Add(kv.Key);
        if (old != null) foreach (var c in old) _camOrder.Remove(c);
    }

    /// <summary>The housekeeping cycle (Runtime/K9House.cs, SHEPHERD_K9_PHASE): entries of destroyed objects leave the per-object memo tables --
    /// NotifyRect's last sizes (only live transforms are compared), the pointer hit test's per-frame root memo and the per-frame canvas
    /// matrices (both recomputed in any later frame; this runs at the end of a frame).</summary>
    internal static void PruneDeadKeys()
    {
        _deadT.Clear();
        foreach (var t in LastSize.Keys) if (t.Destroyed) _deadT.Add(t);
        foreach (var t in _deadT) LastSize.Remove(t);
        _deadT.Clear();
        foreach (var t in _uiRootOf.Keys) if (t.Destroyed) _deadT.Add(t);
        foreach (var t in _deadT) _uiRootOf.Remove(t);
        _deadT.Clear(); _deadC.Clear();
        foreach (var c in _canvasScreen.Keys) if (c.Destroyed) _deadC.Add(c);
        foreach (var c in _deadC) _canvasScreen.Remove(c);
        _deadC.Clear();
    }
    static readonly List<NTransform> _deadT = new();
    static readonly List<NComponent> _deadC = new();

    /// <summary>WCmp on the packed keys (HOT): the same tests in the same order; items with SortingGroups go to GCmp on the draw items.</summary>
    static int KCmp(int x, int y)
    {
        ref var a = ref _sk[x]; ref var b = ref _sk[y];
        if (a.G || b.G) return GCmp(ref _wi[x], ref _wi[y]);
        int c = FCmp(a.Layer, b.Layer); if (c != 0) return c;
        c = FCmp(a.Order, b.Order); if (c != 0) return c;
        c = FCmp(b.Dist, a.Dist); if (c != 0) return c;   // descending
        if (K11World.Tie && _k11Cre[x] != _k11Cre[y]) return _k11Cre[x] < _k11Cre[y] ? -1 : 1;   // kit #11 gpu1 (WCmp's; the keys in their own array)
        return a.Seq < b.Seq ? -1 : a.Seq > b.Seq ? 1 : 0;
    }

    /// <summary>=check: kit #8's sort of the same items (index start, merge sort, the draw items' WCmp); a different permutation is a difference.</summary>
    static void CheckOrder(int[] o, int n)
    {
        bool sk = _skOn; _skOn = false;
        if (_wChk.Length < n) _wChk = new int[n * 2];
        var tmp = new int[n];
        for (int k = 0; k < n; k++) _wChk[k] = k;
        MergeSort(_wChk, tmp, 0, n);
        _skOn = sk;
        for (int k = 0; k < n; k++) if (_wChk[k] != o[k]) { Runtime.K9Engine.DiffSort(n, k); Array.Copy(_wChk, o, n); return; }
    }
}
