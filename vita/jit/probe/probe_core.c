/* Shepherd JIT probe (kit #7 JIT stage 1): measures the device facts a JIT on the PS Vita would depend on, the unknowns the read-only
 * study left open ($SHEPHERD_WORK/jitstudy/verdict.md section 8 stage 1; vita/notes/kit7-jit.md) and the questions kit #8's
 * design is parameterised on (vita/notes/kit8-baseline-jit-design.md section 6). The probe is owner-run and gated: on the device it
 * does nothing with code memory until the owner presses X (plat_confirm: all buttons released first, then a fresh press); on the Mac it
 * runs with auto_confirm.
 * Crash safety: each result is one log line (append-then-close), the log keeps every launch (never rotated), and a state file records
 * the test last entered. A launch that finds a test name there (the previous launch stopped inside it: a fault, a hang, a timeout) logs
 * it, adds it to the faults file and skips it from then on, so one bad test cannot hide the others. The tests run safest first; the
 * ones that may fault on some firmware (code run with the domain open, a write by a thread that never called Open) come last.
 * Revision 2 = the kit #7 review fixes (vita/notes/kit7-jit.md "Review fixes"). */
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include "jitp.h"

static char LINE[768];
#define L(...) do { snprintf (LINE, sizeof LINE, __VA_ARGS__); plat_log_line (LINE); } while (0)

typedef int (*fn_i_i) (int);
static int call_at (int isa, void *p, int arg) { return ((fn_i_i) emit_entry (isa, p)) (arg); }

/* pipeline sync on the core that is about to run new code (an unprivileged ISB: what a real JIT does after the cache maintenance) */
static void exec_barrier (void)
{
#if defined(__arm__)
	__asm__ volatile ("dsb\n\tisb" : : : "memory");
#elif defined(__aarch64__)
	__asm__ volatile ("dsb ish\n\tisb" : : : "memory");
#endif
}

/* ---- crash-safe bookkeeping: the state file, the faults list ---- */
static char g_faults[1024];
static int in_faults (const char *name)
{
	size_t n = strlen (name); const char *s = g_faults;
	while (*s) {
		const char *e = strchr (s, '\n'); size_t len = e ? (size_t) (e - s) : strlen (s);
		if (len == n && !memcmp (s, name, n)) return 1;
		if (!e) break;
		s = e + 1;
	}
	return 0;
}
static int enter (const char *name)
{
	if (in_faults (name)) { L("SKIP %s: a previous launch stopped inside it (delete ux0:data/shepherd/jitprobe.faults to try it again)", name); return 0; }
	plat_put_file ("state", name, 0);
	return 1;
}
static void trim (char *s) { size_t n = strlen (s); while (n && (s[n - 1] == '\n' || s[n - 1] == '\r' || s[n - 1] == ' ')) s[--n] = 0; }
static int is_marker (const char *s) { return !*s || !strcmp (s, "start") || !strcmp (s, "done") || !strcmp (s, "declined") || !strcmp (s, "stopped"); }

static int finish (int rc, const char *marker)
{
	plat_put_file ("state", marker, 0);
	plat_wait_exit ();
	plat_exit ();
	return rc;
}
/* a worker did not end in time: no verdict, and it may still be running generated code -- leave the block alone and end the process
 * (the state file keeps the test's name, so the next launch skips it) */
static int stop_timeout (void)
{
	L("STOPPED: a worker thread did not finish in time (see the line above); exiting without freeing the code block. The next launch skips this test.");
	plat_exit ();
	return 3;
}

/* test-only switch (Mac harness): JITP_TEST_HANG=<test>:<ms> makes that test's worker stall (cross-core, nested-open, non-opener-write),
 * to exercise the waited / timeout / skip-on-relaunch paths; unset (always, on the Vita) = no effect */
static int test_hang_ms (const char *test)
{
	const char *e = getenv ("JITP_TEST_HANG"); size_t n = strlen (test);
	return e && !strncmp (e, test, n) && e[n] == ':' ? atoi (e + n + 1) : 0;
}

/* ---- workers ---- */
typedef struct { volatile int rc_open, rc_close, closed, cpu; int hang_ms; } nest_ctx;
static void *nest_worker (void *a)
{
	nest_ctx *c = (nest_ctx *) a;
	c->cpu = plat_current_cpu ();
	if (c->hang_ms) plat_sleep_us ((uint32_t) c->hang_ms * 1000u);
	c->rc_open = plat_vm_open ();                    /* a second Open while the main thread holds the domain */
	if (c->rc_open >= 0) { c->rc_close = plat_vm_close (); c->closed = 1; }
	return 0;
}

typedef struct { jitp_block *blk; int isa; uint8_t *slot; int want, open_own, hang_ms; volatile int rc_sync, cpu; } once_ctx;
static void *write_once_worker (void *a)
{
	once_ctx *c = (once_ctx *) a;
	c->cpu = plat_current_cpu ();
	if (c->hang_ms) plat_sleep_us ((uint32_t) c->hang_ms * 1000u);
	if (c->open_own) plat_vm_open ();               /* the Mac's per-thread W^X only; on the Vita this thread never calls Open */
	int n = emit_ret_imm (c->isa, c->slot, (uint32_t) c->want);
	c->rc_sync = plat_vm_sync (c->blk, c->slot, (uint32_t) n);
	if (c->open_own) plat_vm_close ();
	return 0;
}

/* cross-core: the caller runs the current routine (so its lines sit in that core's I-cache), the writer on another core rewrites it in
 * place and syncs, the caller runs it again -- new value = Sync reached the other core; old value = stale code */
typedef struct {
	jitp_block *blk; int isa, rounds, open_once, pw, hang_ms; uint8_t *slot;
	volatile int phase, abort, rounds_done, fresh, stale, other, pre_bad;
	volatile int cpu_w0, cpu_w1, cpu_c0, cpu_c1, mask_w, mask_c;
} xc_ctx;
static int wait_phase (xc_ctx *x, int want)
{
	uint64_t t0 = plat_us ();
	while (x->phase != want) {
		if (x->abort) return 0;
		if (plat_us () - t0 > 2000000) { x->abort = 1; return 0; }
		plat_sleep_us (50);
	}
	__sync_synchronize ();
	return 1;
}
static void *xc_writer (void *a)
{
	xc_ctx *x = (xc_ctx *) a;
	x->cpu_w0 = plat_current_cpu (); x->mask_w = plat_current_core_mask ();
	if (x->hang_ms) plat_sleep_us ((uint32_t) x->hang_ms * 1000u);
	for (int r = 0; r < x->rounds; r++) {
		if (!wait_phase (x, 2 * r + 1)) break;
		if (!x->open_once) plat_vm_open ();          /* per-write brackets (and the Mac's per-thread window) */
		int n = emit_ret_imm (x->isa, x->slot, (uint32_t) (101 + r));
		plat_vm_sync (x->blk, x->slot, (uint32_t) n);
		if (!x->open_once) plat_vm_close ();
		__sync_synchronize ();
		x->phase = 2 * r + 2;
	}
	x->cpu_w1 = plat_current_cpu ();
	return 0;
}
static void *xc_caller (void *a)
{
	xc_ctx *x = (xc_ctx *) a;
	x->cpu_c0 = plat_current_cpu (); x->mask_c = plat_current_core_mask ();
	if (!x->pw) plat_vm_close ();                    /* Mac: this thread to executable (never on the Vita: Close there is process-wide) */
	for (int r = 0; r < x->rounds; r++) {
		int old = 100 + r, nw = 101 + r;
		for (int k = 0; k < 64; k++) if (call_at (x->isa, x->slot, 0) != old) x->pre_bad++;
		__sync_synchronize ();
		x->phase = 2 * r + 1;
		if (!wait_phase (x, 2 * r + 2)) break;
		exec_barrier ();
		int v = call_at (x->isa, x->slot, 0);
		if (v == nw) x->fresh++; else if (v == old) x->stale++; else x->other++;
		x->rounds_done = r + 1;
	}
	x->cpu_c1 = plat_current_cpu ();
	return 0;
}

int jitp_main (int auto_confirm)
{
	plat_init ();
	char cnt[32] = {0}; int launch = 1;
	if (plat_read_file ("count", cnt, sizeof cnt) > 0) launch = atoi (cnt) + 1;
	snprintf (cnt, sizeof cnt, "%d", launch); plat_put_file ("count", cnt, 0);
	L("=== %s on %s: launch %d (this file keeps every launch -- send all of it) ===", JITP_VERSION, plat_name (), launch);
	char desc[256]; plat_describe (desc, sizeof desc); L("%s", desc);
	int isas[4]; int nisa = plat_isas (isas, 4);
	int pw = plat_domain_process_wide ();
	L("generated code: %s%s%s; Open/Close: %s", isa_name (isas[0]), nisa > 1 ? " + " : "", nisa > 1 ? isa_name (isas[1]) : "",
	  pw ? "one process-wide domain (the Vita's ForVM)" : "per-thread W^X (Mac MAP_JIT): the Vita-only questions are marked n/a");

	/* the previous launch's last test (device crash / hang evidence) -> the faults list */
	char prev[64] = {0};
	if (plat_read_file ("state", prev, sizeof prev) > 0) trim (prev); else prev[0] = 0;
	if (!is_marker (prev)) {
		L("NOTE the previous launch stopped inside '%s' (no result line after it): added to jitprobe.faults, skipped from now on", prev);
		plat_put_file ("faults", prev, 1);
	}
	plat_put_file ("state", "start", 0);
	if (plat_read_file ("faults", g_faults, sizeof g_faults) <= 0) g_faults[0] = 0;
	if (g_faults[0]) { char fl[256]; snprintf (fl, sizeof fl, "%.250s", g_faults); for (char *c = fl; *c; c++) if (*c == '\n') *c = ' '; trim (fl); L("skipped this launch (a previous launch stopped inside them): %s", fl); }

	int core3 = plat_core3_probe ();
	L("4th core (CPU3): %s", core3 ? "a thread was accepted there (a core-unlock plugin is active)" : "refused (workers would share USER_2), as on a stock console");

	if (!auto_confirm) {
		L("WAITING for the owner's go: this step allocates code memory (ForVM) and runs generated code. Release every button, then press X to run, O to exit.");
		if (!plat_confirm ()) { L("owner declined (or no fresh X press): no code memory was allocated, nothing generated. Exiting."); return finish (0, "declined"); }
	}
	L("owner confirmed (or auto on the Mac): starting the code-memory tests");

	/* 1. the block: free user memory before, the alloc, where it lands vs the eboot, free memory after */
	if (!enter ("block")) { L("every other test needs the block: nothing more to run"); return finish (1, "stopped"); }
	int free_before = plat_free_user_kib ();
	jitp_block b; memset (&b, 0, sizeof b);
	int rc = plat_vm_alloc ("shepherd-jit", 4u * 1024 * 1024, &b);
	int free_after = plat_free_user_kib ();
	if (rc < 0 || !b.base) { L("FAIL block: alloc rc 0x%08x (free user before %d KiB). No code memory; stopping.", (unsigned) rc, free_before); return finish (1, "stopped"); }
	int64_t to_helper = (int64_t) (uintptr_t) b.base - (int64_t) ((uintptr_t) &jitp_helper & ~(uintptr_t) 1);
	int64_t ad = to_helper < 0 ? -to_helper : to_helper;
	L("block: 4 MiB at %p, free user %d -> %d KiB (cost %d KiB = out of user RAM: %s)",
	  b.base, free_before, free_after, free_before - free_after, free_before >= 0 && free_before - free_after >= 3072 ? "yes" : "unclear");
	L("block: ~%lld MiB from THIS probe's code -> A32 BL (+-32 MiB) %s, Thumb-2 BL (+-16 MiB) %s. %s",
	  (long long) (to_helper / (1024 * 1024)), ad < (32ll << 20) ? "in reach" : "OUT of reach", ad < (16ll << 20) ? "in reach" : "OUT of reach",
	  pw ? "The probe's eboot is tiny: Shepherd's (45-62 MiB, then newlib and the arena) lays memory out differently, so kit #8 re-measures this inside Shepherd."
	     : "Mac harness layout (MAP_JIT vs the Mac binary): not a Vita distance.");

	/* 2. the basic path per ISA: Open, write one routine, Sync, Close, call (the homebrew bracket pattern; __clear_cache is a no-op here) */
	int isa_ok[4] = {0}, any_ok = 0, pi = -1;
	for (int k = 0; k < nisa; k++) {
		int isa = isas[k]; char nm[32]; snprintf (nm, sizeof nm, "basic-%s", isa_name (isa));
		if (!enter (nm)) continue;
		uint8_t *p = b.base + 0x100 * (size_t) k;
		int ro = plat_vm_open ();
		int n = emit_ret_imm (isa, p, 12345);
		int rs = plat_vm_sync (&b, p, (uint32_t) n);
		int rcl = plat_vm_close ();
		int r = call_at (isa, p, 0);
		L("[%s] basic: Open rc 0x%08x, wrote ret-12345 (%d B), Sync rc 0x%08x, Close rc 0x%08x; call -> %d (%s)",
		  isa_name (isa), (unsigned) ro, n, (unsigned) rs, (unsigned) rcl, r, r == 12345 ? "PASS" : "WRONG");
		if (r == 12345) { isa_ok[k] = 1; any_ok = 1; if (pi < 0) pi = isa; }
	}
	if (!any_ok) { L("FAIL basic: no ISA's generated code returned the expected value; stopping before the rest."); plat_vm_free (&b); return finish (1, "stopped"); }

	/* 3. Sync cost: many small writes under one Open, each routine synced (the per-write kernel cost a JIT pays) */
	double sync_each = -1;
	if (enter ("sync-cost")) {
		plat_vm_open ();
		uint8_t *q = b.base + 0x1000;
		int count = 2000, step = 32, ok = 1;
		uint64_t t0 = plat_us ();
		for (int i = 0; i < count; i++) { int m = emit_ret_imm (pi, q + (size_t) i * step, (uint32_t) (i & 0xFFFF)); plat_vm_sync (&b, q + (size_t) i * step, (uint32_t) m); }
		uint64_t t_sync = plat_us () - t0;
		plat_vm_close ();
		for (int i = 0; i < count; i++) if (call_at (pi, q + (size_t) i * step, 0) != (i & 0xFFFF)) { ok = 0; break; }
		sync_each = (double) t_sync / count;
		L("[%s] sync cost: %d routines written+synced under one Open in %llu us (%.2f us each); all correct: %d", isa_name (pi), count, (unsigned long long) t_sync, sync_each, ok);
	}

	/* 4. Open / Close cost: bare pairs, and one Open / write / Sync / Close bracket per routine (the per-batch alternative to open-once) */
	if (enter ("open-close-cost")) {
		const int K = 1000, M = 500; int bad = 0, ok = 1;
		uint64_t t0 = plat_us ();
		for (int k = 0; k < K; k++) { if (plat_vm_open () < 0) bad++; if (plat_vm_close () < 0) bad++; }
		uint64_t t_oc = plat_us () - t0;
		uint8_t *q = b.base + 0x20000;
		t0 = plat_us ();
		for (int i = 0; i < M; i++) { plat_vm_open (); int m = emit_ret_imm (pi, q + (size_t) i * 32, (uint32_t) (20000 + i)); plat_vm_sync (&b, q + (size_t) i * 32, (uint32_t) m); plat_vm_close (); }
		uint64_t t_br = plat_us () - t0;
		for (int i = 0; i < M; i++) if (call_at (pi, q + (size_t) i * 32, 0) != 20000 + i) { ok = 0; break; }
		L("[%s] open/close cost: %d Open+Close pairs in %llu us (%.2f us a pair, %d error rc); %d routines each in its own Open/write/Sync/Close in %llu us (%.2f us each; under one Open: %.2f us each); all correct: %d",
		  isa_name (pi), K, (unsigned long long) t_oc, (double) t_oc / K, bad, M, (unsigned long long) t_br, (double) t_br / M, sync_each, ok);
	}

	/* 5. call out of the block into the eboot per ISA: through a register (always reachable) and by a patched direct BL (reach-dependent),
	 *    to the Thumb-2 helper (how the runtime's C helpers are compiled) and, on ARM, to an ARM-mode one (the interworking forms) */
	for (int k = 0; k < nisa; k++) {
		int isa = isas[k]; char nm[32]; snprintf (nm, sizeof nm, "call-out-%s", isa_name (isa));
		if (!isa_ok[k]) { L("[%s] call out: skipped (its basic test did not pass this launch)", isa_name (isa)); continue; }
		if (!enter (nm)) continue;
		uint8_t *c1 = b.base + 0x200000 + 0x400 * (size_t) k, *c2 = c1 + 0x100, *c3 = c1 + 0x200;
		void *harm = jitp_helper_arm_ptr (); int64_t d2 = 0, d3 = 0;
		plat_vm_open ();
		int nc = emit_call_helper (isa, c1, (void *) jitp_helper, 11);
		plat_vm_sync (&b, c1, (uint32_t) nc);
		int nb = emit_bl_helper (isa, c2, (void *) jitp_helper, 11, &d2);
		if (nb) plat_vm_sync (&b, c2, (uint32_t) nb);
		int nb3 = harm ? emit_bl_helper (isa, c3, harm, 12, &d3) : 0;
		if (nb3) plat_vm_sync (&b, c3, (uint32_t) nb3);
		plat_vm_close ();
		int reach = isa == ISA_T32 ? 16 : isa == ISA_A32 ? 32 : 128;
		int v = call_at (isa, c1, 0);
		L("[%s] call out (register: MOVW/MOVT + BLX): helper(11) -> %d (%s)", isa_name (isa), v, v == jitp_helper (11) ? "PASS" : "WRONG");
		if (nb) { v = call_at (isa, c2, 0); L("[%s] call out (patched direct BL to the eboot helper%s, %lld bytes): -> %d (%s)", isa_name (isa), harm ? " (Thumb-2)" : "", (long long) d2, v, v == jitp_helper (11) ? "PASS" : "WRONG"); }
		else L("[%s] call out (direct BL): the helper is %lld bytes away, outside the +-%d MiB BL reach -> a veneer / thunk is needed", isa_name (isa), (long long) d2, reach);
		if (harm) {
			if (nb3) { v = call_at (isa, c3, 0); L("[%s] call out (patched direct %s to the ARM-mode helper, %lld bytes): -> %d (%s)", isa_name (isa), isa == ISA_T32 ? "BLX" : "BL", (long long) d3, v, v == jitp_helper (12) ? "PASS" : "WRONG"); }
			else L("[%s] call out (direct to the ARM-mode helper): %lld bytes away, out of reach", isa_name (isa), (long long) d3);
		}
	}

	/* 6. exec ratio per ISA: the same two-instruction loop in the code block and in the eboot's .text (native.c), best of 3 interleaved --
	 *    ~1.00 means code in the ForVM block runs at the eboot's speed (cacheable, same fetch path) */
	uint8_t *loop_slot[4] = {0}; uint64_t loop_us[4] = {0};
	const int N = 8000000;
	for (int k = 0; k < nisa; k++) {
		int isa = isas[k]; char nm[32]; snprintf (nm, sizeof nm, "exec-ratio-%s", isa_name (isa));
		if (!isa_ok[k]) { L("[%s] exec ratio: skipped (its basic test did not pass this launch)", isa_name (isa)); continue; }
		if (!enter (nm)) continue;
		uint8_t *cl = b.base + 0x300000 + 0x100 * (size_t) k;
		plat_vm_open ();
		int ncl = emit_countdown (isa, cl);
		if (ncl) plat_vm_sync (&b, cl, (uint32_t) ncl);
		plat_vm_close ();
		if (!ncl || jitp_native_countdown (isa, 1000) != 0) { L("[%s] exec ratio: no countdown pair for this ISA (skipped)", isa_name (isa)); continue; }
		call_at (isa, cl, 1000);
		uint64_t bg = ~0ull, bn = ~0ull; int rg = 0, rn = 0;
		for (int rep = 0; rep < 3; rep++) {
			uint64_t t = plat_us (); rg |= call_at (isa, cl, N); uint64_t g = plat_us () - t;
			t = plat_us (); rn |= jitp_native_countdown (isa, N); uint64_t n = plat_us () - t;
			if (g < bg) bg = g;
			if (n < bn) bn = n;
		}
		loop_slot[k] = cl; loop_us[k] = bg;
		L("[%s] exec ratio: the same 2-instruction loop (SUBS #1 / BNE) x %d, best of 3: code block %llu us, eboot .text %llu us (block / eboot = %.2fx; ~1.00 = native speed); results %d / %d",
		  isa_name (isa), N, (unsigned long long) bg, (unsigned long long) bn, bn ? (double) bg / bn : 0.0, rg, rn);
	}

	/* 7. second block: the per-process total (a 16 MiB block after this 4 MiB one would exceed the quota the field reports) */
	if (enter ("second-block")) {
		jitp_block b2; memset (&b2, 0, sizeof b2);
		int rc2 = plat_vm_alloc ("shepherd-jit-2", 16u * 1024 * 1024, &b2);
		L("second block: a 16 MiB ForVM alloc after the 4 MiB one -> rc 0x%08x (%s; 0x80024B0B = per-process VM quota reached, as the field reports)",
		  (unsigned) rc2, rc2 >= 0 && b2.base ? "GRANTED" : "refused");
		if (rc2 >= 0 && b2.base) plat_vm_free (&b2);
	}

	/* 8. nested Open: on one thread, then a second thread's Open while the main thread holds the domain (the azaharvita race: 0x80010058).
	 *    No code is written between the calls, so whatever the answer, nothing can fault; the domain ends closed under every semantics. */
	if (enter ("nested-open")) {
		int r1 = plat_vm_open (), r2 = plat_vm_open (), r3 = plat_vm_close (), r4 = r2 >= 0 ? plat_vm_close () : 0;
		L("nested open (one thread): Open rc 0x%08x, a second Open rc 0x%08x (%s), Close rc 0x%08x%s",
		  (unsigned) r1, (unsigned) r2, r2 < 0 ? "refused" : "accepted", (unsigned) r3, r2 >= 0 ? (r4 < 0 ? ", second Close refused (one state, not counted)" : ", second Close accepted") : "");
		nest_ctx nc; memset (&nc, 0, sizeof nc); nc.rc_close = -1; nc.hang_ms = test_hang_ms ("nested-open");
		int m1 = plat_vm_open ();
		jitp_thread th; int cr = plat_thread_start (&th, nest_worker, &nc, -1);
		if (cr == 0) {
			/* if the second Open WAITS for the holder (a lock-like domain) instead of erroring, the main thread's Close releases it */
			int blocked = 0, m3;
			if (plat_thread_join (&th, 2000) < 0) {
				blocked = 1; m3 = plat_vm_close ();
				if (plat_thread_join (&th, 2000) < 0) { L("nested open (two threads): TIMEOUT -- the second thread's Open did not return in 2 s, nor 2 s after the main thread's Close (no verdict)"); return stop_timeout (); }
			} else m3 = plat_vm_close ();
			L("nested open (two threads): main Open rc 0x%08x (held); a second thread's Open while held -> rc 0x%08x (%s)%s; the main thread's Close rc 0x%08x%s%s",
			  (unsigned) m1, (unsigned) nc.rc_open,
			  blocked ? "it WAITED until the main thread closed: a lock-like domain, one holder at a time"
			          : nc.rc_open < 0 ? "refused: not nestable across threads -- one owner of Open/Close, or a process-wide lock (the azaharvita fix)" : "accepted",
			  nc.closed ? (nc.rc_close < 0 ? "; its Close refused" : "; its Close accepted") : "",
			  (unsigned) m3, !blocked && nc.closed && nc.rc_close >= 0 && m3 < 0 ? " (refused: the other thread's Close had already closed the main thread's window = one shared state)" : "",
			  pw ? "" : " [Mac: per-thread W^X, a harness check only]");
		} else { plat_vm_close (); L("nested open (two threads): could not start the thread (rc %d)", cr); }
	}

	/* 9. code run while the domain is open (the open-once-never-close model needs it; the study's PCSX/Daedalus note says it works):
	 *    write, sync, call with the domain still open, and time the countdown loop open vs closed */
	int ewo_ok = 0;
	if (!pw) L("exec while open: n/a on the Mac (per-thread W^X: a thread cannot write and run the same page at once) -- the device answers it");
	else if (enter ("exec-while-open")) {
		uint8_t *s = b.base + 0x100000;
		int ro = plat_vm_open ();
		int n = emit_ret_imm (pi, s, 4242);
		int rs = plat_vm_sync (&b, s, (uint32_t) n);
		exec_barrier ();
		int r = call_at (pi, s, 0);
		int kl = -1; for (int k = 0; k < nisa; k++) if (isas[k] == pi && loop_slot[k]) kl = k;
		uint64_t tl = 0;
		if (kl >= 0) { uint64_t t = plat_us (); call_at (pi, loop_slot[kl], N); tl = plat_us () - t; }
		int rcl = plat_vm_close ();
		ewo_ok = r == 4242;
		if (kl >= 0) L("[%s] exec while open: Open rc 0x%08x, wrote ret-4242, Sync rc 0x%08x, called with the domain still open -> %d (%s); the countdown loop while open %llu us vs closed %llu us; Close rc 0x%08x",
		               isa_name (pi), (unsigned) ro, (unsigned) rs, r, ewo_ok ? "PASS: code runs while open" : "WRONG", (unsigned long long) tl, (unsigned long long) loop_us[kl], (unsigned) rcl);
		else L("[%s] exec while open: Open rc 0x%08x, wrote ret-4242, Sync rc 0x%08x, called with the domain still open -> %d (%s); Close rc 0x%08x",
		       isa_name (pi), (unsigned) ro, (unsigned) rs, r, ewo_ok ? "PASS: code runs while open" : "WRONG", (unsigned) rcl);
	}

	/* 10. a write by a thread that never called Open, while the main thread holds the domain open (open-once with a compiler thread) */
	int now_ok = 0;
	if (enter ("non-opener-write")) {
		uint8_t *s = b.base + 0x101000;
		once_ctx oc; memset (&oc, 0, sizeof oc); oc.blk = &b; oc.isa = pi; oc.slot = s; oc.want = 777; oc.open_own = !pw; oc.cpu = -1; oc.hang_ms = test_hang_ms ("non-opener-write");
		int mo = pw ? plat_vm_open () : 0;
		jitp_thread th; int cr = plat_thread_start (&th, write_once_worker, &oc, -1);
		if (cr == 0) {
			/* if the writer's Sync waits while another thread holds the domain, the main thread's Close releases it */
			int got = -1, mc = 0;
			if (plat_thread_join (&th, 2000) < 0) {
				mc = pw ? plat_vm_close () : 0;
				if (plat_thread_join (&th, 2000) < 0) { L("non-opener write: TIMEOUT -- the writer did not finish in 2 s, nor 2 s after the main thread's Close (no verdict)"); return stop_timeout (); }
				got = call_at (pi, s, 0);
				L("[%s] non-opener write: the writer finished only after the main thread closed (its Sync waited for the holder; Sync rc 0x%08x, main Close rc 0x%08x) -> %d: no writing while another thread holds the domain",
				  isa_name (pi), (unsigned) oc.rc_sync, (unsigned) mc, got);
			} else {
				if (pw && ewo_ok) { exec_barrier (); got = call_at (pi, s, 0); mc = plat_vm_close (); }
				else { mc = plat_vm_close (); got = call_at (pi, s, 0); }
				now_ok = pw && got == 777;
				if (pw) L("[%s] non-opener write: with the main thread holding Open (rc 0x%08x), a thread that never called Open wrote + synced a routine (Sync rc 0x%08x, CPU %d); called %s -> %d (%s); main Close rc 0x%08x",
				          isa_name (pi), (unsigned) mo, (unsigned) oc.rc_sync, oc.cpu, ewo_ok ? "with the domain still open" : "after Close", got, got == 777 ? "PASS: any thread can write while one holds it open" : "WRONG", (unsigned) mc);
				else L("[%s] non-opener write: Mac harness (per-thread W^X: the worker opened its own window -- a handshake check, not the Vita's answer); read back -> %d (%s)",
				       isa_name (pi), got, got == 777 ? "PASS" : "WRONG");
			}
		} else { if (pw) plat_vm_close (); L("non-opener write: could not start the writer thread (rc %d)", cr); }
	}

	/* 11. cross-core: code another core has ALREADY RUN, rewritten in place and synced by this core's writer -- does the other core see it?
	 *     Run in the model this launch showed works: open-once (the main thread holds Open, the writer never opens) when tests 9 and 10
	 *     passed, else per-write brackets. Both threads report the core they actually ran on (sceKernelGetCpuId). */
	if (enter ("cross-core")) {
		xc_ctx x; memset (&x, 0, sizeof x);
		x.blk = &b; x.isa = pi; x.slot = b.base + 0x102000; x.rounds = 200; x.pw = pw; x.open_once = pw && ewo_ok && now_ok; x.hang_ms = test_hang_ms ("cross-core");
		x.cpu_w0 = x.cpu_w1 = x.cpu_c0 = x.cpu_c1 = -1;
		plat_vm_open ();
		int n = emit_ret_imm (pi, x.slot, 100);
		plat_vm_sync (&b, x.slot, (uint32_t) n);
		if (!x.open_once) plat_vm_close ();
		jitp_thread tw, tc;
		int cw = plat_thread_start (&tw, xc_writer, &x, 1);
		int cc = cw == 0 ? plat_thread_start (&tc, xc_caller, &x, 2) : -1;
		if (cw == 0 && cc == 0) {
			int jw = plat_thread_join (&tw, 15000), jc = plat_thread_join (&tc, 15000);
			if (jw < 0 || jc < 0) { x.abort = 1; L("cross-core: TIMEOUT (writer %s, caller %s, %d of %d rounds done) -- no verdict", jw < 0 ? "still running" : "ended", jc < 0 ? "still running" : "ended", x.rounds_done, x.rounds); return stop_timeout (); }
			if (x.open_once) plat_vm_close ();
			const char *v;
			if (x.rounds_done < x.rounds) v = "INCOMPLETE (a handshake timed out; see the counts)";
			else if (x.cpu_w0 >= 0 && (x.cpu_w0 == x.cpu_c0 || x.cpu_w1 == x.cpu_c1)) v = "INCONCLUSIVE (both threads ran on one core)";
			else if (x.other || x.pre_bad) v = "ODD (unexpected values; see the counts)";
			else if (x.stale) v = "STALE: Sync did not reach the other core's I-cache -- install code on the core that runs it, or invalidate there";
			else v = pw ? "PASS: Sync makes rewritten code visible to another core that had already run it" : "PASS (Mac harness; cores unknown)";
			L("[%s] cross-core (%s): writer CPU %d->%d (mask 0x%x), caller CPU %d->%d (mask 0x%x); %d of %d rounds: fresh %d, stale %d, other %d, warm-run mismatches %d -> %s",
			  isa_name (pi), x.open_once ? "open-once model: main holds Open, the writer never opens" : "per-write Open/Sync/Close brackets",
			  x.cpu_w0, x.cpu_w1, (unsigned) x.mask_w, x.cpu_c0, x.cpu_c1, (unsigned) x.mask_c, x.rounds_done, x.rounds, x.fresh, x.stale, x.other, x.pre_bad, v);
		} else {
			x.abort = 1;
			if (cw == 0 && plat_thread_join (&tw, 4000) < 0) { L("cross-core: the writer did not end after the caller failed to start"); return stop_timeout (); }
			if (x.open_once) plat_vm_close ();
			L("cross-core: could not start both worker threads (writer %d, caller %d)", cw, cc);
		}
	}

	plat_vm_free (&b);
	L("=== done (launch %d): see the lines above; this probe generated and ran code only after the confirm step ===", launch);
	return finish (0, "done");
}
