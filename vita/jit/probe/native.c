/* Shepherd JIT probe: the eboot-side code the generated routines are measured against or call into (vita/jit/probe/README.md).
 *   jitp_helper            the target of a call out of the code block (compiled Thumb-2 on the Vita: vitasdk's default, as the runtime's
 *                          own helpers will be; ARM-mode twin jitp_helper_arm for the interworking paths)
 *   jitp_native_countdown  the exec-ratio baseline: the SAME two loop instructions emit_countdown writes (SUBS #1 / BNE back), kept in a
 *                          register by inline asm, in the eboot's .text -- so "block / eboot" compares where the code runs, not two
 *                          different loops (kit #7 review fix: the old C baseline was a volatile load/store loop). */
#include "jitp.h"

int jitp_helper (int x) { return x * 3 + 7; }

#if defined(__arm__)
__attribute__((noinline, target("arm"))) int jitp_helper_arm (int x) { return x * 3 + 7; }
__attribute__((noinline, target("arm"))) static int countdown_a32 (int n)
{
	__asm__ volatile ("1: subs %0, %0, #1\n\tbne 1b" : "+r" (n) : : "cc");
	return n;
}
__attribute__((noinline, target("thumb"))) static int countdown_t32 (int n)
{
	__asm__ volatile ("1: subs %0, %0, #1\n\tbne 1b" : "+l" (n) : : "cc");
	return n;
}
void *jitp_helper_arm_ptr (void) { return (void *) jitp_helper_arm; }
#else
void *jitp_helper_arm_ptr (void) { return 0; }
#endif

#if defined(__aarch64__)
__attribute__((noinline)) static int countdown_a64 (int n)
{
	__asm__ volatile ("1: subs %w0, %w0, #1\n\tb.ne 1b" : "+r" (n) : : "cc");
	return n;
}
#endif

int jitp_native_countdown (int isa, int n)
{
	switch (isa) {
#if defined(__arm__)
	case ISA_A32: return countdown_a32 (n);
	case ISA_T32: return countdown_t32 (n);
#endif
#if defined(__aarch64__)
	case ISA_A64: return countdown_a64 (n);
#endif
	}
	return -1;
}
