// SHEPHERD-VITA device kit #9, track runtime (vita/notes/kit9-runtime.md "Review fixes", finding 5): the arbitrary unbox trampoline pool.
// A companion of K9Unbox.cs, run the same way (CoreLib AOT-compiled WITHOUT System.Int32's ToString overloads: SHEPHERD_AOT_KEEP = every
// other CoreLib token). Delegates made by reflection over a boxed int's ToString (Delegate.CreateDelegate: the target method is known, no
// ldvirtftn) are bound at their first invocation by the delegate trampoline, which asks for the value-type method's unbox trampoline for
// each new delegate (no per-class cache for these). Int32.ToString was not compiled, so each ask is fix A's fallback: uncached, every
// delegate takes another arbitrary trampoline from CoreLib's pool of 1024 and the 1025th ends the run (g_error "Ran out of trampolines");
// cached (the review fix), one trampoline serves all 3000. With a complete CoreLib the image's own unbox trampoline is found every time.
// Mono mini-test format (vita/tests/il/hello-gen.py logs each test's return value per tier).
using System;

class Tests
{
	public static int test_0_create_delegate_boxed_int_3000 () {
		object o = 42;
		int n = 0;
		for (int i = 0; i < 3000; i++) {
			var f = (Func<string>) Delegate.CreateDelegate (typeof (Func<string>), o, "ToString");
			n += f ().Length;
		}
		return n == 6000 ? 0 : n;
	}
}
