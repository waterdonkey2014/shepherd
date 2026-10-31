// SHEPHERD-VITA device kit #9, track runtime (vita/notes/kit9-runtime.md "E2 divfast"): division / remainder edge cases for the AOT tier.
// Mono mini-test format (vita/tests/il/hello-gen.py: each test's return value -- or EXC:<type> -- is logged per tier), so the same IL can
// be compared between the AOT tier built WITHOUT and WITH SHEPHERD_AOT_DIVFAST (the inline no-wrapper fast path in front of every emulated
// division) and against the interpreter tier. The operands come from arrays (never constants) except in the *_const tests, so the
// compiler cannot fold them; each sweep returns a hash of every quotient / remainder and of every exception raised (by type), in order.
// Covers the guard's edges: divisor 0 / -1 / 1, int.MinValue, values at and around 2^31 / 2^32, 64-bit values whose high word is 0,
// non-zero, negative, and divisors whose low word is 0 (2^32: the 64-bit guard must not use the 32-bit routine there).
using System;
using System.Runtime.CompilerServices;

class Tests
{
	static readonly int[] I = { 0, 1, -1, 2, -2, 3, 7, -7, 10, 1000, 65536, -65536, 0x40000000, -0x40000000, int.MaxValue, int.MinValue, int.MinValue + 1 };
	static readonly uint[] U = { 0, 1, 2, 3, 7, 10, 65536, 0x7FFFFFFF, 0x80000000, 0x80000001, 0xFFFFFFFE, 0xFFFFFFFF };
	static readonly long[] L = { 0, 1, -1, 2, -2, 3, 7, -7, 10, 1000000007, -1000000007, 0x7FFFFFFF, 0x80000000, -0x80000000L, 0xFFFFFFFF,
		0x100000000, 0x100000005, -0x100000000L, 0xFFFFFFFFFFFF, long.MaxValue, long.MinValue, long.MinValue + 1 };
	static readonly ulong[] UL = { 0, 1, 2, 3, 7, 10, 1000000007, 0x7FFFFFFF, 0x80000000, 0xFFFFFFFF, 0x100000000, 0x100000001,
		0x8000000000000000, 0xFFFFFFFF00000000, ulong.MaxValue };

	const int DZ = 1111, OV = 2222, AR = 3333;
	static int Mix (int h, long v) { unchecked { return h * 31 + (int) v + (int) (v >> 32) * 17; } }

	[MethodImpl (MethodImplOptions.NoInlining)] static int DivI (int a, int b) { return a / b; }
	[MethodImpl (MethodImplOptions.NoInlining)] static int RemI (int a, int b) { return a % b; }
	[MethodImpl (MethodImplOptions.NoInlining)] static uint DivU (uint a, uint b) { return a / b; }
	[MethodImpl (MethodImplOptions.NoInlining)] static uint RemU (uint a, uint b) { return a % b; }
	[MethodImpl (MethodImplOptions.NoInlining)] static long DivL (long a, long b) { return a / b; }
	[MethodImpl (MethodImplOptions.NoInlining)] static long RemL (long a, long b) { return a % b; }
	[MethodImpl (MethodImplOptions.NoInlining)] static ulong DivUL (ulong a, ulong b) { return a / b; }
	[MethodImpl (MethodImplOptions.NoInlining)] static ulong RemUL (ulong a, ulong b) { return a % b; }

	// the sweeps: every pair, the operator inline in the loop (one emulated site each)
	public static int test_0_sweep_int () {
		int h = 0;
		foreach (int a in I) foreach (int b in I) {
			try { h = Mix (h, a / b); } catch (DivideByZeroException) { h = Mix (h, DZ); } catch (OverflowException) { h = Mix (h, OV); } catch (ArithmeticException) { h = Mix (h, AR); }
			try { h = Mix (h, a % b); } catch (DivideByZeroException) { h = Mix (h, DZ); } catch (OverflowException) { h = Mix (h, OV); } catch (ArithmeticException) { h = Mix (h, AR); }
		}
		return h;
	}
	public static int test_0_sweep_uint () {
		int h = 0;
		foreach (uint a in U) foreach (uint b in U) {
			try { h = Mix (h, a / b); } catch (DivideByZeroException) { h = Mix (h, DZ); } catch (ArithmeticException) { h = Mix (h, AR); }
			try { h = Mix (h, a % b); } catch (DivideByZeroException) { h = Mix (h, DZ); } catch (ArithmeticException) { h = Mix (h, AR); }
		}
		return h;
	}
	public static int test_0_sweep_long () {
		int h = 0;
		foreach (long a in L) foreach (long b in L) {
			try { h = Mix (h, a / b); } catch (DivideByZeroException) { h = Mix (h, DZ); } catch (OverflowException) { h = Mix (h, OV); } catch (ArithmeticException) { h = Mix (h, AR); }
			try { h = Mix (h, a % b); } catch (DivideByZeroException) { h = Mix (h, DZ); } catch (OverflowException) { h = Mix (h, OV); } catch (ArithmeticException) { h = Mix (h, AR); }
		}
		return h;
	}
	public static int test_0_sweep_ulong () {
		int h = 0;
		foreach (ulong a in UL) foreach (ulong b in UL) {
			try { h = Mix (h, (long) (a / b)); } catch (DivideByZeroException) { h = Mix (h, DZ); } catch (ArithmeticException) { h = Mix (h, AR); }
			try { h = Mix (h, (long) (a % b)); } catch (DivideByZeroException) { h = Mix (h, DZ); } catch (ArithmeticException) { h = Mix (h, AR); }
		}
		return h;
	}
	// the same through non-inlined helpers (the site is the helper's only division: its argument registers, no loop context)
	public static int test_0_sweep_helpers () {
		int h = 0;
		foreach (int a in I) foreach (int b in I) { try { h = Mix (h, DivI (a, b)); } catch (Exception e) { h = Mix (h, e.GetType ().Name.Length); } try { h = Mix (h, RemI (a, b)); } catch (Exception e) { h = Mix (h, e.GetType ().Name.Length + 100); } }
		foreach (uint a in U) foreach (uint b in U) { try { h = Mix (h, DivU (a, b)); } catch (Exception e) { h = Mix (h, e.GetType ().Name.Length); } try { h = Mix (h, RemU (a, b)); } catch (Exception e) { h = Mix (h, e.GetType ().Name.Length + 100); } }
		foreach (long a in L) foreach (long b in L) { try { h = Mix (h, DivL (a, b)); } catch (Exception e) { h = Mix (h, e.GetType ().Name.Length); } try { h = Mix (h, RemL (a, b)); } catch (Exception e) { h = Mix (h, e.GetType ().Name.Length + 100); } }
		foreach (ulong a in UL) foreach (ulong b in UL) { try { h = Mix (h, (long) DivUL (a, b)); } catch (Exception e) { h = Mix (h, e.GetType ().Name.Length); } try { h = Mix (h, (long) RemUL (a, b)); } catch (Exception e) { h = Mix (h, e.GetType ().Name.Length + 100); } }
		return h;
	}
	// constant divisors (the 32-bit *_IMM forms: a constant that cannot raise is called directly; -1 keeps the wrapper)
	public static int test_0_const () {
		int h = 0;
		foreach (int a in I) { h = Mix (h, a / 7); h = Mix (h, a % 7); h = Mix (h, a / -7); h = Mix (h, a % 10); h = Mix (h, a / 1000003); }
		foreach (uint a in U) { h = Mix (h, a / 7u); h = Mix (h, a % 10u); h = Mix (h, a / 0xFFFFFFFFu); h = Mix (h, a % 0x80000001u); }
		foreach (long a in L) { h = Mix (h, a / 10L); h = Mix (h, a % 7L); h = Mix (h, a / 0x100000000L); h = Mix (h, a % 0x100000005L); }
		foreach (ulong a in UL) { h = Mix (h, (long) (a / 10UL)); h = Mix (h, (long) (a % 0xFFFFFFFFUL)); h = Mix (h, (long) (a / 0x100000000UL)); }
		return h;
	}
	public static int test_0_const_minus_one () {
		int h = 0;
		foreach (int a in I) { try { h = Mix (h, a / -1); } catch (OverflowException) { h = Mix (h, OV); } try { h = Mix (h, a % -1); } catch (OverflowException) { h = Mix (h, OV); } }
		foreach (long a in L) { try { h = Mix (h, a / -1L); } catch (OverflowException) { h = Mix (h, OV); } try { h = Mix (h, a % -1L); } catch (OverflowException) { h = Mix (h, OV); } }
		return h;
	}
	// single cases, logged one by one (an exception shows as EXC:<type> in the table)
	public static int test_0_int_min_div_m1 () { return DivI (I [15], I [2]); }
	public static int test_0_int_min_rem_m1 () { return RemI (I [15], I [2]); }
	public static int test_0_int_div_0 () { return DivI (I [6], I [0]); }
	public static int test_0_uint_rem_0 () { return (int) RemU (U [5], U [0]); }
	public static int test_0_long_min_div_m1 () { return (int) DivL (L [20], L [2]); }
	public static int test_0_long_rem_0 () { return (int) RemL (L [9], L [0]); }
	public static int test_0_ulong_div_2p32 () { return (int) DivUL (UL [14], UL [10]); }
	public static int test_0_ulong_div_0 () { return (int) DivUL (UL [5], UL [0]); }
	public static int test_0_long_lowword_zero_divisor () { return (int) (DivL (L [9], L [15]) * 1000 + RemL (L [16], L [15])); }
}
