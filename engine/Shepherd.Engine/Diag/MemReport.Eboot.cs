#if SHEPHERD_EBOOT
namespace Shepherd.Engine.Diag;

/// <summary>Device kit #7, track measure (vita/notes/kit7-measure.md, "Room"): MemReport (SHEPHERD_MEMREPORT, Diag/MemReport*.cs) is the Mac's
/// budget-planning tool -- macOS CPU clocks, execution-order probe behaviours, micro-benchmarks, the heap census -- and was ~0.39 MiB of AOT
/// code in every engine-AOT eboot (the largest diagnostic there; every byte of AOT code is eboot span, and the device's free user memory
/// drops 1 MiB at a time with it). The eboots leave it out like the other Mac-only tools (kit #5 review rule: -p:ShepherdEboot=1); its two
/// entry points stay as no-ops, so Boot is unchanged. With SHEPHERD_MEMREPORT unset -- every gate, every device run -- nothing differs;
/// build with SHEPHERD_ENGINE_TOOLS=1 for an eboot that has it (= kit #6's).</summary>
public static class MemReport
{
    public static void Before(int frame) { }
    public static void After(int frame) { }
}
#endif
