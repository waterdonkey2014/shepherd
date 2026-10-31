// SHEPHERD-VITA device kit #9, track runtime (vita/notes/kit9-runtime.md "Review fixes", finding 5): fix A's fallback -- a value-type
// method that a trimmed / partial AOT image did not compile enters the interpreter through the arbitrary unbox trampoline -- must be cached
// per method: every arbitrary trampoline comes from CoreLib's fixed pool (nunbox-arbitrary-trampolines=1024; an empty pool is the fatal
// g_error "Ran out of trampolines"), and the runtime asks again at every lookup.
// Run it with CoreLib AOT-compiled WITHOUT System.Int32's ToString overloads (SHEPHERD_AOT_KEEP = every other CoreLib method token;
// vita/tests/il/vlx.sh with SHEPHERD_AOT_KEEP in the hello build's environment, or IL_LLVM_ENV for the LLVM tier): a delegate over a boxed
// int's virtual ToString (`dup; ldvirtftn object::ToString; newobj` -> a virtual delegate) resolves Int32.ToString -- a value-type method --
// at each new delegate's first invocation (mono_delegate_trampoline: virtual delegates are never cached) and asks for its unbox trampoline:
// 3000 delegates = 3000 lookups. Uncached (the kit #9 runtimes before the review fix, and the gameaot fork's own fix A), the pool runs out
// and the run dies; cached, one trampoline serves them all. With a complete CoreLib the method's own unbox trampoline is found (no pool use).
// Mono mini-test format (vita/tests/il/hello-gen.py logs each test's return value per tier).
using System;

class Tests
{
	public static int test_0_virtual_delegate_boxed_int_3000 () {
		object o = 42;
		int n = 0;
		for (int i = 0; i < 3000; i++) {
			Func<string> f = o.ToString;   // a new virtual delegate each time: one unbox-trampoline lookup at its first call
			n += f ().Length;
		}
		return n == 6000 ? 0 : n;
	}

	public static int test_0_virtual_delegate_boxed_int_formats () {
		object o = -7;
		int n = 0;
		for (int i = 0; i < 1500; i++) {
			Func<string> f = o.ToString;
			n += f () == "-7" ? 1 : 1000;
		}
		return n == 1500 ? 0 : n;
	}
}
