// SHEPHERD-VITA device kit #14, track small (vita/notes/kit14-small.md, "unused library code trimmed"): the generic twin of K9Unbox.cs / K9UnboxPool.cs. The keep
// list now rules generic instances (SHEPHERD_AOT_KEEP_GENERIC=1), so a struct method of a BCL generic definition can be missing from EVERY image. The runtime
// then finds no AOT method for it and takes mono_aot_get_unbox_trampoline's FIRST branch, which kit #9's fallback (cached per method, with a jit info naming
// the method) did not cover: AOT code's ldvirtftn + delegate constructor stopped on g_assert (method || del->interp_method), and repeated lookups emptied
// CoreLib's pool of 1024 arbitrary unbox trampolines (the fatal "Ran out of trampolines"). vita/patches/k14-unbox-first.patch sends that branch through the
// same fallback (SHEPHERD_K14_UNBOX_FIRST=0 = the old branch).
// Run it with CoreLib AOT-compiled WITHOUT KeyValuePair`2.ToString and List`1.Enumerator's MoveNext / get_Current (SHEPHERD_AOT_KEEP = every other CoreLib token
// + SHEPHERD_AOT_KEEP_GENERIC=1, vita/tests/il/vlx.sh as K9Unbox.cs): three instance shapes (concrete, partially shared, fully shared), ldvirtftn delegates,
// reflection delegates and an interface call on a boxed struct. With a complete CoreLib every test passes as well and the pool is not touched.
// Mono mini-test format (vita/tests/il/hello-gen.py logs each test's return value per tier).
using System;
using System.Collections;
using System.Collections.Generic;

class Tests
{
	public static int test_0_ldvirtftn_concrete_3000 () {
		object o = new KeyValuePair<int, int> (1, 2);
		int n = 0;
		for (int i = 0; i < 3000; i++) {
			Func<string> f = o.ToString;   // a new virtual delegate each time: a lookup at ldvirtftn and at its first call
			n += f ().Length;
		}
		return n == 18000 ? 0 : n;
	}

	public static int test_0_ldvirtftn_partial_3000 () {
		object o = new KeyValuePair<string, int> ("a", 2);
		int n = 0;
		for (int i = 0; i < 3000; i++) {
			Func<string> f = o.ToString;
			n += f ().Length;
		}
		return n == 18000 ? 0 : n;
	}

	public static int test_0_ldvirtftn_shared_3000 () {
		object o = new KeyValuePair<string, string> ("a", "b");
		int n = 0;
		for (int i = 0; i < 3000; i++) {
			Func<string> f = o.ToString;
			n += f ().Length;
		}
		return n == 18000 ? 0 : n;
	}

	public static int test_0_create_delegate_3000 () {
		object o = new KeyValuePair<int, long> (1, 2);
		int n = 0;
		for (int i = 0; i < 3000; i++) {
			var f = (Func<string>) Delegate.CreateDelegate (typeof (Func<string>), o, "ToString");
			n += f ().Length;
		}
		return n == 18000 ? 0 : n;
	}

	public static int test_0_interface_boxed_enumerator () {
		var l = new List<int> { 1, 2, 3 };
		int n = 0;
		for (int i = 0; i < 3000; i++) {
			IEnumerator<int> e = l.GetEnumerator ();   // boxed List<int>.Enumerator: vtable / IMT dispatch into the dropped MoveNext
			while (e.MoveNext ())
				n += e.Current;
		}
		return n == 18000 ? 0 : n;
	}

	public static int test_0_ldvirtftn_interface_enumerator () {
		var l = new List<short> { 1, 2, 3 };
		int n = 0;
		for (int i = 0; i < 1500; i++) {
			IEnumerator e = l.GetEnumerator ();
			Func<bool> step = e.MoveNext;   // ldvirtftn on the interface: the boxed struct's dropped method
			while (step ())
				n++;
		}
		return n == 4500 ? 0 : n;
	}
}
