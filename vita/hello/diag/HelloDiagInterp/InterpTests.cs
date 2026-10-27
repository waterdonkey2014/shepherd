// SHEPHERD-VITA DIAG (devfail1): runs in the Mono INTERPRETER (this assembly has no AOT image). Each method raises one exception the
// interpreter itself creates (by name: mono_get_exception_* -> exception.c mono_exception_new_by_name), or crosses interp <-> AOT
// while an exception is in flight. HelloDiag (AOT) calls these one at a time and logs before each call.
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace ShepherdVita.Diag.Interp
{
    public static class InterpTests
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        static T Opaque<T>(T x) => x;

        /// <summary>interpreter null check on a virtual call</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static int NullDeref(object o) => o.GetHashCode();

        /// <summary>interpreter null check on a field load</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static int NullField(int[] a) => a.Length;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void ThrowNull() => throw null;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static int IndexOutOfRange(int[] a, int i) => a[i];

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static string InvalidCast(object o) => (string)o;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static int DivideByZero(int a, int b) => a / b;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static int Overflow(int a) => checked(a + int.MaxValue);

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static int OverflowMinDivMinusOne(int a, int b) => a / b;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void ArrayTypeMismatch(object[] a) => a[0] = 42;

        /// <summary>the first managed new of an exception from interpreted code (parameterless ctor: a resource lookup)</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static string NewNullReference() => new NullReferenceException().Message;

        /// <summary>AOT CoreLib (List.Sort) calls back into an interpreted comparer that throws; caught here, in the interpreter</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static string ThrowThroughAotSort()
        {
            var list = new List<int> { 3, 1, 2 };
            try
            {
                list.Sort((a, b) => throw new InvalidOperationException("thrown by an interpreted comparer"));
                return "no exception";
            }
            catch (InvalidOperationException e)
            {
                return "caught in the interpreter: " + e.Message;
            }
        }

        /// <summary>throws from the interpreter into an AOT caller</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void ThrowToAotCaller(string msg) => throw new ArgumentException(msg);
    }
}
