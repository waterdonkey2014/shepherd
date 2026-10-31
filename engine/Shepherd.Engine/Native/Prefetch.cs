using System.Runtime.CompilerServices;
namespace Shepherd.Engine.Native;

/// <summary>Kit #9 engine, Memory-stall pack (a): the software-prefetch hint of the engine's hot walks. The Vita's A9 has effectively no
/// hardware prefetch (~270 ns per missed 32-byte line, misses do not overlap), so a walk over records scattered on the heap waits on RAM once
/// per record; a PLD of the NEXT record issued while the current one is worked on hides that wait. The runtime track provides the intrinsic
/// (the AOT / LLVM compiler turns a call of <see cref="Line"/> into one `pld [r0]` of the object's first line; the interpreter and the JIT a
/// no-op): until a build carries it, Line is an empty method and SHEPHERD_K9_PREFETCH=1 only costs the calls. The walks that ask (Lifecycle's
/// phase loops, the coroutine wheel's pass) test <see cref="On"/> once per walk: off (the default), nothing is called. A prefetch changes
/// where data waits, never what it is.</summary>
public static class Prefetch
{
    public static readonly bool On = Runtime.K9Engine.Prefetch;
    /// <summary>The runtime intrinsic's call site: one PLD of o's first cache line (empty without the intrinsic).</summary>
    [MethodImpl(MethodImplOptions.NoInlining)] public static void Line(object o) { }
}
