/* Shepherd JIT probe, qemu encoding check (vita/jit/probe/qemu/run.sh; kit #7 review fix for finding 8). The Mac harness runs A64, so
 * it can never execute the probe's A32 / Thumb-2 routines; this bare-metal program runs the SAME emit.c + native.c on an emulated
 * Cortex-A9 (qemu-system-arm, vexpress-a9) and checks every routine the device probe generates: return-immediate, add-to-argument,
 * the countdown loop, the register call (MOVW/MOVT + BLX) and the patched direct BL / BLX to a Thumb-2 and an ARM-mode helper --
 * near, at the edge of the +-32 MiB (A32) / +-16 MiB (T32) reach on both sides, and just past it (must refuse). It proves the
 * ENCODINGS (and interworking) only: qemu keeps its translated code coherent by itself, so cache maintenance, ForVM and the Open /
 * Close model are the device's questions, not this one's. Semihosting prints the results; exit status 0 = every check passed. */
#include <stdint.h>
#include <stddef.h>
#include "../jitp.h"

void *memcpy (void *d, const void *s, size_t n) { uint8_t *dd = d; const uint8_t *ss = s; while (n--) *dd++ = *ss++; return d; }
void *memset (void *d, int c, size_t n) { uint8_t *dd = d; while (n--) *dd++ = (uint8_t) c; return d; }
size_t strlen (const char *s) { size_t n = 0; while (s[n]) n++; return n; }

__attribute__((noinline, target("arm"))) static int semi (int op, void *arg)
{
	register int r0 __asm__ ("r0") = op; register void *r1 __asm__ ("r1") = arg;
	__asm__ volatile ("svc 0x123456" : "+r" (r0) : "r" (r1) : "memory");
	return r0;
}
static void puts_ (const char *s) { semi (0x04, (void *) s); }
static void putd (int v)
{
	char b[16]; int i = 15; unsigned u = v < 0 ? (unsigned) -v : (unsigned) v; b[i] = 0;
	do { b[--i] = (char) ('0' + u % 10); u /= 10; } while (u);
	if (v < 0) b[--i] = '-';
	puts_ (b + i);
}

typedef int (*fn_i_i) (int);
static int fails, checks;
static void check (const char *isa, const char *what, int got, int want)
{
	checks++; if (got != want) fails++;
	puts_ (got == want ? "PASS " : "FAIL "); puts_ (isa); puts_ (" "); puts_ (what); puts_ (" -> "); putd (got); puts_ (" (want "); putd (want); puts_ (")\n");
}
static int run (int isa, uint8_t *p, int arg) { return ((fn_i_i) emit_entry (isa, p)) (arg); }

static void one_isa (int isa)
{
	const char *nm = isa_name (isa);
	static uint8_t near[4096] __attribute__((aligned (16)));
	void *th = (void *) jitp_helper, *ah = jitp_helper_arm_ptr ();
	uintptr_t hth = (uintptr_t) th & ~(uintptr_t) 1;
	int64_t d;
	emit_ret_imm (isa, near, 12345); check (nm, "ret-imm 12345", run (isa, near, 0), 12345);
	emit_ret_imm (isa, near, 0xFFFF); check (nm, "ret-imm 65535", run (isa, near, 0), 0xFFFF);
	emit_ret_arg_plus (isa, near, 5); check (nm, "arg+5 (10)", run (isa, near, 10), 15);
	emit_countdown (isa, near); check (nm, "countdown 1000", run (isa, near, 1000), 0);
	check (nm, "eboot countdown 1000", jitp_native_countdown (isa, 1000), 0);
	emit_call_helper (isa, near, th, 11); check (nm, "register call -> Thumb helper(11)", run (isa, near, 0), 40);
	emit_call_helper (isa, near, ah, 12); check (nm, "register call -> ARM helper(12)", run (isa, near, 0), 43);
	check (nm, "direct BL -> Thumb helper(11), near: emitted", emit_bl_helper (isa, near, th, 11, &d) > 0, 1);
	check (nm, "direct BL -> Thumb helper(11), near", run (isa, near, 0), 40);
	check (nm, "direct BL -> ARM helper(12), near: emitted", emit_bl_helper (isa, near, ah, 12, &d) > 0, 1);
	check (nm, "direct BL -> ARM helper(12), near", run (isa, near, 0), 43);
	/* the reach edges: a routine placed so its BL is (reach - 64 KiB) after / before the helper, and one placed 1 MiB past the reach */
	int64_t reach = isa == ISA_T32 ? (16ll << 20) : (32ll << 20);
	int64_t offs[2] = { reach - (64 << 10), -(reach - (64 << 10)) };
	for (int s = 0; s < 2; s++) {
		uint8_t *far = (uint8_t *) (uintptr_t) ((hth + (uintptr_t) offs[s]) & ~(uintptr_t) 15);
		int ok = emit_bl_helper (isa, far, th, 11, &d) > 0;
		check (nm, s ? "direct BL -> Thumb helper, -reach+64KiB: emitted" : "direct BL -> Thumb helper, +reach-64KiB: emitted", ok, 1);
		if (ok) check (nm, s ? "direct BL -> Thumb helper, -reach+64KiB" : "direct BL -> Thumb helper, +reach-64KiB", run (isa, far, 0), 40);
		ok = emit_bl_helper (isa, far + 64, ah, 12, &d) > 0;
		check (nm, s ? "direct BL -> ARM helper, -reach+64KiB: emitted" : "direct BL -> ARM helper, +reach-64KiB: emitted", ok, 1);
		if (ok) check (nm, s ? "direct BL -> ARM helper, -reach+64KiB" : "direct BL -> ARM helper, +reach-64KiB", run (isa, far + 64, 0), 43);
		uint8_t *past = (uint8_t *) (uintptr_t) ((hth + (uintptr_t) (s ? -(reach + (1 << 20)) : (reach + (1 << 20)))) & ~(uintptr_t) 15);
		check (nm, s ? "direct BL, 1 MiB past -reach: refused" : "direct BL, 1 MiB past +reach: refused", emit_bl_helper (isa, past, th, 11, &d), 0);
	}
}

void qmain (void)
{
	puts_ ("shepherd jit probe qemu encoding check (cortex-a9): emit.c + native.c\n");
	one_isa (ISA_A32);
	one_isa (ISA_T32);
	puts_ ("checks "); putd (checks); puts_ (", failures "); putd (fails); puts_ (fails ? " -> FAIL\n" : " -> PASS\n");
	semi (0x18, (void *) (uintptr_t) (fails ? 0x20023 : 0x20026));   /* SYS_EXIT: ApplicationExit = qemu exit 0 */
	for (;;) ;
}
