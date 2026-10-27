using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
namespace Shepherd.Engine.Impl;

/// <summary>Fidelity round 1: native memory, Burst shared statics and the C# job system, enough for Unity's own managed packages
/// that generate data in jobs (the 2D SpriteShape package's SpriteShapeGenerator is an IJob; vita/notes/fidelity-round1.md §2).
/// Before, every `Unity.Jobs.` / `Unity.Burst.` icall was an inert stub: JobHandle.Schedule did nothing, and
/// BurstCompilerService.GetOrCreateSharedMemory returned a dummy pointer that IJobExtensions.JobStruct&lt;T&gt;.Initialize writes into.
/// Unity semantics implemented here:
/// - UnsafeUtility.Malloc/Free/MemCpy/... on the process heap (NativeArray / NativeSlice / Allocator.Temp, TempJob, Persistent)
/// - SharedStatic memory: one zeroed block per (hash, sub-hash) key, alive for the process (Burst's semantics)
/// - CreateJobReflectionData keeps the job's managed ExecuteJobFunction; Schedule / ScheduleParallelFor run the job to completion on
///   the calling thread before returning (a valid schedule: every job is complete before its handle can be waited on; the game's
///   own code schedules no jobs). No worker threads, so JobsUtility.IsExecutingJob stays false outside a job.
/// [ProfileOnly]: reads no property-bag keys (the V3 diet's literal scan skips it).</summary>
[ProfileOnly]
public static unsafe class JobIcalls
{
    // ---------------- UnsafeUtility ----------------
    [Icall("Unity.Collections.LowLevel.Unsafe.UnsafeUtility::Malloc")] static object Malloc(object[] a) => (IntPtr)Alloc(Convert.ToInt64(a[0]), Convert.ToInt32(a[1]));
    [Icall("Unity.Collections.LowLevel.Unsafe.UnsafeUtility::MallocTracked")] static object MallocTracked(object[] a) => (IntPtr)Alloc(Convert.ToInt64(a[0]), Convert.ToInt32(a[1]));
    [Icall("Unity.Collections.LowLevel.Unsafe.UnsafeUtility::Free")] static object Free(object[] a) { Release((IntPtr)a[0]); return null; }
    [Icall("Unity.Collections.LowLevel.Unsafe.UnsafeUtility::FreeTracked")] static object FreeTracked(object[] a) { Release((IntPtr)a[0]); return null; }
    [Icall("Unity.Collections.LowLevel.Unsafe.UnsafeUtility::MemCpy")] static object MemCpy(object[] a) { Buffer.MemoryCopy((void*)(IntPtr)a[1], (void*)(IntPtr)a[0], Convert.ToInt64(a[2]), Convert.ToInt64(a[2])); return null; }
    [Icall("Unity.Collections.LowLevel.Unsafe.UnsafeUtility::MemMove")] static object MemMove(object[] a) { Buffer.MemoryCopy((void*)(IntPtr)a[1], (void*)(IntPtr)a[0], Convert.ToInt64(a[2]), Convert.ToInt64(a[2])); return null; }
    [Icall("Unity.Collections.LowLevel.Unsafe.UnsafeUtility::MemSet")] static object MemSet(object[] a) { NativeMemory.Fill((void*)(IntPtr)a[0], (nuint)Convert.ToInt64(a[2]), Convert.ToByte(a[1])); return null; }
    [Icall("Unity.Collections.LowLevel.Unsafe.UnsafeUtility::MemCmp")] static object MemCmp(object[] a)
    {
        var x = new ReadOnlySpan<byte>((void*)(IntPtr)a[0], checked((int)Convert.ToInt64(a[2]))); var y = new ReadOnlySpan<byte>((void*)(IntPtr)a[1], x.Length);
        return x.SequenceCompareTo(y);
    }
    /// <summary>MemCpyStride(destination, destinationStride, source, sourceStride, elementSize, count).</summary>
    [Icall("Unity.Collections.LowLevel.Unsafe.UnsafeUtility::MemCpyStride")] static object MemCpyStride(object[] a)
    {
        byte* d = (byte*)(IntPtr)a[0]; int ds = Convert.ToInt32(a[1]); byte* s = (byte*)(IntPtr)a[2]; int ss = Convert.ToInt32(a[3]); int es = Convert.ToInt32(a[4]); int n = Convert.ToInt32(a[5]);
        for (int i = 0; i < n; i++) Buffer.MemoryCopy(s + (long)i * ss, d + (long)i * ds, es, es);
        return null;
    }
    /// <summary>MemCpyReplicate(destination, source, size, count): count copies of size bytes, back to back.</summary>
    [Icall("Unity.Collections.LowLevel.Unsafe.UnsafeUtility::MemCpyReplicate")] static object MemCpyReplicate(object[] a)
    {
        byte* d = (byte*)(IntPtr)a[0]; byte* s = (byte*)(IntPtr)a[1]; int sz = Convert.ToInt32(a[2]), n = Convert.ToInt32(a[3]);
        for (int i = 0; i < n; i++) Buffer.MemoryCopy(s, d + (long)i * sz, sz, sz);
        return null;
    }
    [Icall("Unity.Collections.LowLevel.Unsafe.UnsafeUtility::SizeOf")] static object SizeOfType(object[] a) => SizeOf((Type)a[0]);
    [Icall("Unity.Collections.LowLevel.Unsafe.UnsafeUtility::IsBlittable")] static object IsBlittable(object[] a) => a[0] is Type t && Unmanaged(t);
    [Icall("Unity.Collections.LowLevel.Unsafe.UnsafeUtility::IsUnmanaged")] static object IsUnmanaged(object[] a) => a[0] is Type t && Unmanaged(t);
    [Icall("Unity.Collections.LowLevel.Unsafe.UnsafeUtility::IsValidNativeContainerElementType")] static object IsValidElem(object[] a) => a[0] is Type t && Unmanaged(t);
    [Icall("Unity.Collections.LowLevel.Unsafe.UnsafeUtility::LeakRecord")] static object LeakRecord(object[] a) => 0;
    [Icall("Unity.Collections.LowLevel.Unsafe.UnsafeUtility::LeakErase")] static object LeakErase(object[] a) => 0;
    [Icall("Unity.Collections.LowLevel.Unsafe.UnsafeUtility::GetLeakDetectionMode")] static object LeakMode(object[] a) => 0;

    static readonly Dictionary<IntPtr, long> _live = new();
    /// <summary>Bytes allocated through UnsafeUtility and not yet freed (diagnostics).</summary>
    public static long LiveBytes;
    static void* Alloc(long size, int align)
    {
        if (size <= 0) size = 1;
        void* p = NativeMemory.AlignedAlloc((nuint)size, (nuint)Math.Max(16, align));
        lock (_live) { _live[(IntPtr)p] = size; LiveBytes += size; }
        return p;
    }
    static void Release(IntPtr p)
    {
        if (p == IntPtr.Zero) return;
        lock (_live) { if (!_live.Remove(p, out var n)) return; LiveBytes -= n; }   // never free what this heap did not hand out
        NativeMemory.AlignedFree((void*)p);
    }
    static readonly Dictionary<Type, int> _sizes = new();
    static int SizeOf(Type t)
    {
        lock (_sizes)
        {
            if (_sizes.TryGetValue(t, out var n)) return n;
            n = (int)typeof(Unsafe).GetMethod(nameof(Unsafe.SizeOf))!.MakeGenericMethod(t).Invoke(null, null)!;
            _sizes[t] = n; return n;
        }
    }
    static bool Unmanaged(Type t)
    {
        if (t.IsPrimitive || t.IsPointer || t.IsEnum) return true;
        if (!t.IsValueType) return false;
        foreach (var f in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)) if (!Unmanaged(f.FieldType)) return false;
        return true;
    }

    // ---------------- Burst SharedStatic ----------------
    static readonly Dictionary<(ulong, ulong), IntPtr> _shared = new();
    /// <summary>GetOrCreateSharedMemory(ref Hash128 key, uint size_of, uint alignment): the same zeroed block for the same key, forever.</summary>
    [Icall("Unity.Burst.LowLevel.BurstCompilerService::GetOrCreateSharedMemory")] static object Shared(object[] a)
    {
        var h = (UnityEngine.Hash128)a[0]; var raw = new byte[16]; MemoryMarshal.Write(raw, in h);
        var key = (BitConverter.ToUInt64(raw, 0), BitConverter.ToUInt64(raw, 8));
        lock (_shared)
        {
            if (_shared.TryGetValue(key, out var p)) return p;
            long size = Math.Max(1, Convert.ToInt64(a[1])); int align = Math.Max(16, Convert.ToInt32(a[2]));
            p = (IntPtr)NativeMemory.AlignedAlloc((nuint)size, (nuint)align); NativeMemory.Clear((void*)p, (nuint)size);
            _shared[key] = p; return p;
        }
    }

    // ---------------- jobs ----------------
    sealed class JobType { public Type Wrapper, User; public Delegate Exec; public MethodInfo Runner; }
    static readonly List<JobType> _jobTypes = new();
    /// <summary>Jobs run to completion so far (diagnostics: the [render] census line).</summary>
    public static int JobsRun;
    [Icall("Unity.Jobs.LowLevel.Unsafe.JobsUtility::CreateJobReflectionData")] static object CreateReflection(object[] a)
    {
        var jt = new JobType { Wrapper = (Type)a[0], User = (Type)a[1], Exec = a[2] as Delegate };
        if (jt.Exec == null) { Log.Warn($"[jobs] CreateJobReflectionData({jt.Wrapper}): no managed execute function"); return IntPtr.Zero; }
        var pt = jt.Exec.Method.GetParameters()[0].ParameterType;   // ref T: the job data as it lies at JobDataPtr
        jt.Runner = typeof(JobIcalls).GetMethod(nameof(Run), BindingFlags.Static | BindingFlags.NonPublic)!.MakeGenericMethod(pt.IsByRef ? pt.GetElementType()! : pt);
        lock (_jobTypes) { _jobTypes.Add(jt); return (IntPtr)(_jobTypes.Count * 16); }
    }
    [Icall("Unity.Jobs.LowLevel.Unsafe.JobsUtility::Schedule_Injected")] static object Schedule(object[] a) { Execute(a[0], 1); a[1] = default(Unity.Jobs.JobHandle); return null; }
    [Icall("Unity.Jobs.LowLevel.Unsafe.JobsUtility::ScheduleParallelFor_Injected")] static object ScheduleFor(object[] a) { Execute(a[0], Convert.ToInt32(a[1])); a[3] = default(Unity.Jobs.JobHandle); return null; }
    [Icall("Unity.Jobs.LowLevel.Unsafe.JobsUtility::get_IsExecutingJob")] static object Executing(object[] a) => false;
    [Icall("Unity.Jobs.LowLevel.Unsafe.JobsUtility::get_JobWorkerMaximumCount")] static object Workers(object[] a) => 0;
    [Icall("Unity.Jobs.LowLevel.Unsafe.JobsUtility::get_ThreadIndexCount")] static object ThreadCount(object[] a) => 1;
    [Icall("Unity.Jobs.LowLevel.Unsafe.JobsUtility::get_ThreadIndex")] static object ThreadIndex(object[] a) => 0;
    /// <summary>GetWorkStealingRange(ref JobRanges ranges, int jobIndex, out int begin, out int end): the single worker takes the whole
    /// range once ([0, TotalIterationCount)), then reports nothing left.</summary>
    [Icall("Unity.Jobs.LowLevel.Unsafe.JobsUtility::GetWorkStealingRange")] static object Steal(object[] a)
    {
        if (_rangeTaken) { a[2] = 0; a[3] = 0; return false; }
        _rangeTaken = true; a[2] = 0; a[3] = _rangeCount; return true;
    }
    static bool _rangeTaken; static int _rangeCount;
    static void Execute(object boxedParams, int iterations)
    {
        var t = boxedParams.GetType();
        var data = (IntPtr)t.GetField("JobDataPtr")!.GetValue(boxedParams)!; var refl = (IntPtr)t.GetField("ReflectionData")!.GetValue(boxedParams)!;
        int i = (int)(refl / 16) - 1; JobType jt;
        lock (_jobTypes) jt = i >= 0 && i < _jobTypes.Count ? _jobTypes[i] : null;
        if (jt == null || data == IntPtr.Zero) { Log.Warn("[jobs] Schedule: unknown job reflection data"); return; }
        bool taken = _rangeTaken; int count = _rangeCount;
        _rangeTaken = false; _rangeCount = iterations;
        try { jt.Runner.Invoke(null, new object[] { data, jt.Exec }); JobsRun++; }
        catch (TargetInvocationException e) { Log.Exception(e.InnerException ?? e, $"job {jt.User?.Name}"); }
        finally { _rangeTaken = taken; _rangeCount = count; }
    }
    /// <summary>ExecuteJobFunction(ref T data, IntPtr additionalPtr, IntPtr bufferRangePatchData, ref JobRanges ranges, int jobIndex) on the
    /// job data at p; the struct is written back (jobs may keep state in their own fields).</summary>
    static void Run<T>(IntPtr p, Delegate exec) where T : struct
    {
        var ps = exec.Method.GetParameters();
        var ranges = Activator.CreateInstance(ps[3].ParameterType.GetElementType()!);
        var f = ranges.GetType().GetField("TotalIterationCount"); f?.SetValue(ranges, _rangeCount);
        var args = new object[] { Unsafe.Read<T>((void*)p), IntPtr.Zero, IntPtr.Zero, ranges, 0 };
        exec.DynamicInvoke(args);
        Unsafe.Write((void*)p, (T)args[0]);
    }
}
