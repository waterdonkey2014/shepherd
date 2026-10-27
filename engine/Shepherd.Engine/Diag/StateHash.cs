using Shepherd.Engine.Native;
namespace Shepherd.Engine.Diag;

#if !SHEPHERD_EBOOT
/// <summary>Kit #7 tests track (vita/notes/kit7-tests.md): per-frame STATE checksums, the replay fixtures' and the lockstep rig's
/// signal (vita/tests/fixtures, vita/tests/lockstep). SHEPHERD_STATE_HASH=&lt;file&gt;: after every frame one line
///   frame  live  active  transforms  objects  behaviours  rng  time  total
/// -- live = live records (Objects.ById), active = enabled behaviours (Lifecycle.Active), then 64-bit hashes of: every live transform's
/// stored state (raw float bits of local position / rotation / scale, the RectTransform fields, its parent and its children in order),
/// every GameObject's active / layer / scene, every behaviour's enabled + lifecycle flags (Awoken, Started, OnEnable delivered), the
/// game's random number calls (UnityEngine.Random: call count and seed), the clocks (frame count, time, unscaled, fixed, deltaTime,
/// timeScale: raw bits); total = all of them. The per-record parts are sums of per-record hashes keyed by instance id, so they do not
/// depend on any enumeration order: two runs agree on a frame iff the same records hold the same state. Read-only: it reads stored
/// fields (no lazily computed world matrix is built, no icall, no game code), so a run with it on draws the same frames (checked:
/// kit7-tests.md). SHEPHERD_STATE_DUMP=&lt;frame&gt;[,...]: at those frames every record's hashed fields also go to
/// &lt;file&gt;.f&lt;frame&gt;.tsv (one line per record, sorted by instance id), so the first differing frame of two runs can be diffed
/// record by record. Mac only (not in the eboots): the device leg of the P2 lockstep rig is a later kit's step.</summary>
[ProfileOnly]
public static class StateHash
{
    public static readonly string File = Environment.GetEnvironmentVariable("SHEPHERD_STATE_HASH") is { Length: > 0 } f ? f : null;
    public static readonly bool On = File != null;
    static readonly HashSet<int> DumpAt = Parse(Environment.GetEnvironmentVariable("SHEPHERD_STATE_DUMP"));
    static StreamWriter _w;
    static HashSet<int> Parse(string s) { var h = new HashSet<int>(); foreach (var p in (s ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries)) if (int.TryParse(p.Trim(), out var v)) h.Add(v); return h; }

    public static void After(int frame) { if (On) Write(frame); }

    const ulong K = 0x9E3779B97F4A7C15UL;
    static ulong Mix(ulong h, ulong v) { h ^= v + K + (h << 6) + (h >> 2); h *= 0xBF58476D1CE4E5B9UL; return h ^ (h >> 31); }
    static ulong F(float x) => (ulong)BitConverter.SingleToUInt32Bits(x);
    static ulong D(double x) => (ulong)BitConverter.DoubleToInt64Bits(x);

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    static void Write(int frame)
    {
        bool dump = DumpAt.Contains(frame);
        var lines = dump ? new List<(int, string)>() : null;
        ulong tr = 0, go = 0, bh = 0;
        foreach (var n in Objects.ById.Values)
        {
            if (n.Destroyed) continue;
            ulong id = (ulong)(uint)n.InstanceId;
            switch (n)
            {
                case NTransform t:
                {
                    ulong h = Mix(id, (ulong)(uint)(t.Parent?.InstanceId ?? 0));
                    var lp = t.LocalPosition; var lr = t.LocalRotation; var ls = t.LocalScale;
                    h = Mix(h, F(lp.x)); h = Mix(h, F(lp.y)); h = Mix(h, F(lp.z));
                    h = Mix(h, F(lr.x)); h = Mix(h, F(lr.y)); h = Mix(h, F(lr.z)); h = Mix(h, F(lr.w));
                    h = Mix(h, F(ls.x)); h = Mix(h, F(ls.y)); h = Mix(h, F(ls.z));
                    if (t.IsRect)
                    {
                        h = Mix(h, 1); h = Mix(h, F(t.AnchorMin.x)); h = Mix(h, F(t.AnchorMin.y)); h = Mix(h, F(t.AnchorMax.x)); h = Mix(h, F(t.AnchorMax.y));
                        h = Mix(h, F(t.AnchoredPosition.x)); h = Mix(h, F(t.AnchoredPosition.y)); h = Mix(h, F(t.SizeDelta.x)); h = Mix(h, F(t.SizeDelta.y));
                        h = Mix(h, F(t.Pivot.x)); h = Mix(h, F(t.Pivot.y));
                    }
                    foreach (var c in t.Children) h = Mix(h, (ulong)(uint)c.InstanceId);   // (the children's order = the sibling indices)
                    tr += h;
                    if (dump) lines.Add((n.InstanceId, $"T\t{n.InstanceId}\t{n.Name}\tp={t.Parent?.InstanceId ?? 0}\tlp={F(lp.x):x8},{F(lp.y):x8},{F(lp.z):x8}\tlr={F(lr.x):x8},{F(lr.y):x8},{F(lr.z):x8},{F(lr.w):x8}\tls={F(ls.x):x8},{F(ls.y):x8},{F(ls.z):x8}\trect={(t.IsRect ? $"{F(t.AnchoredPosition.x):x8},{F(t.AnchoredPosition.y):x8},{F(t.SizeDelta.x):x8},{F(t.SizeDelta.y):x8}" : "-")}\tkids={t.Children.Count}\t{h:x16}"));
                    break;
                }
                case NGameObject g:
                {
                    ulong h = Mix(Mix(Mix(Mix(id, 2), g.ActiveSelf ? 1UL : 0UL), (ulong)(uint)g.Layer), (ulong)(uint)g.Scene);
                    go += h;
                    if (dump) lines.Add((n.InstanceId, $"G\t{n.InstanceId}\t{n.Name}\tactive={g.ActiveSelf}\tlayer={g.Layer}\tscene={g.Scene}\t{h:x16}"));
                    break;
                }
                case NBehaviour b:
                {
                    ulong h = Mix(Mix(id, 3), b.Enabled ? 1UL : 0UL);
                    if (b is NMono m) h = Mix(h, (m.Awoken ? 1UL : 0) | (m.Started ? 2UL : 0) | (m.IsEnabledCalled ? 4UL : 0));
                    bh += h;
                    if (dump) lines.Add((n.InstanceId, $"B\t{n.InstanceId}\t{n.Managed?.GetType().FullName}\t{n.Name}\ten={b.Enabled}\t{(b is NMono mm ? $"aw={mm.Awoken} st={mm.Started} oe={mm.IsEnabledCalled}" : "-")}\t{h:x16}"));
                    break;
                }
            }
        }
        ulong rng = Mix(Mix(4, (ulong)Impl.AppIcalls.RngCalls), (ulong)(uint)Impl.AppIcalls.RngSeed);
        ulong time = Mix(Mix(Mix(Mix(Mix(Mix(Mix(5, (ulong)(uint)Runtime.Time.FrameCount), D(Runtime.Time.Now)), D(Runtime.Time.UnscaledNow)), D(Runtime.Time.FixedNow)),
            F(Runtime.Time.DeltaTime)), F(Runtime.Time.TimeScale)), D(Runtime.Time.Realtime));
        int live = Objects.LiveCount, active = Runtime.Lifecycle.Active.Count;
        ulong total = Mix(Mix(Mix(Mix(Mix(Mix(Mix((ulong)(uint)live, (ulong)(uint)active), tr), go), bh), rng), time), 6);
        _w ??= new StreamWriter(File, false) { AutoFlush = true };
        _w.WriteLine($"{frame}\t{live}\t{active}\t{tr:x16}\t{go:x16}\t{bh:x16}\t{Impl.AppIcalls.RngCalls}\t{time:x16}\t{total:x16}");
        if (dump)
        {
            lines.Sort((a, b) => a.Item1.CompareTo(b.Item1));
            using var d = new StreamWriter($"{File}.f{frame}.tsv", false);
            d.WriteLine($"# frame {frame}: live {live} active {active} rng calls {Impl.AppIcalls.RngCalls} seed {Impl.AppIcalls.RngSeed} time {Runtime.Time.FrameCount} {D(Runtime.Time.Now):x16} {D(Runtime.Time.FixedNow):x16} {F(Runtime.Time.DeltaTime):x8} {F(Runtime.Time.TimeScale):x8}");
            foreach (var (_, l) in lines) d.WriteLine(l);
        }
    }
}
#endif
