namespace Shepherd.Engine.Native;

/// <summary>SHEPHERD_CHECK_REG=1 (V6 stage 2, check runs only): every time a pass uses a <see cref="Registry"/> list, the list is
/// compared with what the old full scan would have produced at that moment — every live record of Objects.All that passes the
/// list's membership test, in Objects.All's order — record by record. Mismatches are logged ([regcheck], the first 20 in full);
/// <see cref="Summary"/> gives the totals at exit. Slow (a full scan per use): never in timing runs.</summary>
[ProfileOnly]
public static class RegistryCheck
{
    public static readonly bool On = Environment.GetEnvironmentVariable("SHEPHERD_CHECK_REG") == "1";
    static long _checks, _items, _bad; static readonly Dictionary<Registry.Kind, long> PerKind = new();
    static readonly List<NObj> Expected = new();

    public static void Compare(Registry reg, List<NObj> items)
    {
        Expected.Clear();
        foreach (var n in Objects.All) if (reg.Member(n)) Expected.Add(n);
        _checks++; _items += items.Count; PerKind[reg.Of] = PerKind.GetValueOrDefault(reg.Of) + 1;
        bool same = Expected.Count == items.Count;
        int at = -1;
        for (int i = 0; same && i < items.Count; i++) if (!ReferenceEquals(Expected[i], items[i])) { same = false; at = i; }
        if (same) return;
        _bad++;
        if (_bad <= 20)
            Log.Info($"[regcheck] MISMATCH {reg.Of} (check #{_checks}, slots exact={Objects.SlotsExact}): list {items.Count} vs scan {Expected.Count}" +
                     (at >= 0 ? $", first difference at #{at}: list {Describe(items[at])} vs scan {Describe(Expected[at])}" : ""));
    }
    static string Describe(NObj n) => n == null ? "null" : $"{n.GetType().Name} '{n.Name}' id={n.InstanceId} slot={n.Slot}";

    public static string Summary() => !On ? null :
        $"[regcheck] {_checks} list uses compared with the full scan ({string.Join(", ", PerKind.Select(kv => $"{kv.Key} {kv.Value}"))}), {_items} records, {_bad} mismatches; slots exact={Objects.SlotsExact}; {LogState()}";
    static string LogState() { var (log, lags) = Registry.LogState(); return $"bind log {log} entries; lag per list: {string.Join(", ", lags.Select(x => $"{x.kind} {x.lag}"))}"; }
}
