using System.Runtime.CompilerServices;
namespace Shepherd.Engine.Diag;

/// <summary>V6 stage 2 (vita/notes/engine-pass-speed.md): the measurement window's edges for the Mac-only call-timing profiler
/// (vita/v6/tools/interp-prof, <c>vita/v6/sim.sh prof</c>). The profiler recognises these two methods by name and times every
/// managed call on the calling thread between them. Empty and never inlined, so the profiler sees their enter events; SimStats
/// calls them at the first and last frame of its window. Without the profiler they cost two empty calls per run.</summary>
[ProfileOnly]
public static class ProfMarks
{
    [MethodImpl(MethodImplOptions.NoInlining)] public static void WindowStart() { }
    [MethodImpl(MethodImplOptions.NoInlining)] public static void WindowEnd() { }
}
