/* device kit #10, track fidelity (vita/notes/kit10-fidelity.md, "1. The float rules"): the PC's floating-point rules on every thread the game's
 * code runs on -- a SWITCH, default OFF (owner decision pending: default on only if identical to the PC at no visible cost).
 *
 * What kit #9's device run found (logs 20261028-084922, the WARN FP lines of k9_fp.inc.c): every Vita thread starts with FPSCR 0x03000000 =
 * FZ 1 (flush-to-zero: a denormal operand or result becomes 0) + DN 1 (default NaN: every NaN result is 0x7FC00000, NaN payloads are lost),
 * round to nearest. The PC original (Unity's player Mono on x64: SSE, MXCSR FTZ 0 / DAZ 0) keeps denormals and propagates NaN operands; so do
 * the Mac (arm64 FPCR FZ 0 / DN 0) and vlx (qemu-user starts every thread with FPSCR 0) -- every identity record so far was made with the PC's
 * rules, the device ran with the Vita's.
 *
 *   SHEPHERD_K10_FPSCR=pc (or 1)   FZ 0, DN 0, RMode 0 (round to nearest), LEN / STRIDE / AHP 0 -- the PC's rules -- written on: the host's main
 *                                  thread (the env file is read: before the engine, vitaGL or Mono exists), again after vglInit (vitaGL loads
 *                                  SceShaccCg there: a changed control word is logged and re-applied), the engine thread (Mono's main managed
 *                                  thread) before mono_jit_init, and EVERY thread Mono starts or attaches, on that thread, before its first
 *                                  managed instruction (a profiler handle of its own: the thread_started event fires in
 *                                  mono_thread_attach_internal / mono_thread_attach before the start delegate runs) -- the MT render thread
 *                                  (shepherd-render, a managed Thread), the thread pool, the finalizer, the A* path threads, the timer.
 *                                  The physics workers (libshepphys sp_jobs.cpp) take the forking thread's control bits with every fork, so they
 *                                  follow the engine thread (they run only with SHEPHERD_PHYS_WORKERS >= 1; the kit apps' default is 0 = the
 *                                  serial step on the engine thread). Review fix: with the switch on, libshepphys counts the FPSCR every worker
 *                                  part ran under (SP_JOBS_FP_STAT) and the END lines check it against the rules written -- the hand-over is
 *                                  checked, not assumed (SHEPHERD_K10_FP_FAULT=handover, TEST: the hand-over skipped, so the check must WARN).
 *                                  A heartbeat watch on the engine thread re-applies the rules if anything changed them (counted, logged); a
 *                                  Mono thread whose control bits changed during its life is logged at its stop.
 *   unset / 0                      kit #9: nothing is written (each thread keeps the FPSCR the Vita gives it); one log line.
 *   SHEPHERD_STRICT_ORIGINAL=1     = pc when the env file has no SHEPHERD_K10_FPSCR line (the host reads it itself: StrictOriginal.cs sets the
 *                                  managed environment only, after this point). Review fix: an explicit SHEPHERD_K10_FPSCR line (0 included)
 *                                  wins, and SHEPHERD_K10_STRICT=0 (kit #9's strict list) leaves the float rules out of strict entirely.
 *   vita (TEST, for vlx)           FZ 1, DN 1, nearest at the same points: the device's own default reproduced under vlx (qemu honours FZ / DN),
 *                                  so a vlx run shows what the device computes differently from the records (on the device = its default).
 *   SHEPHERD_K10_FP_CENSUS=1 (TEST) at each engine heartbeat, each Mono thread's stop and (with the physics workers on) after each worker part:
 *                                  the sticky IDC and UFC flags read and cleared -> END lines with the first frames where they were set. What
 *                                  the flags can and cannot say (review fix): IDC is set only under FZ 1 (the Vita's rules / =vita): a
 *                                  denormal INPUT was flushed -- each such operand is denormal work for the VFP under the PC's rules. UFC
 *                                  under FZ 0 is set only for a result that is tiny AND inexact: an exact denormal result, or a denormal
 *                                  operand with a normal result, sets NO flag -- so under =pc the census cannot count the denormal operands
 *                                  the VFP processes, and a low UFC count says nothing about the cost. Not sampled: the MT render thread, the
 *                                  thread-pool / A* / finalizer threads still alive at the end (no per-thread hook runs there), host threads.
 *                                  Clears the sticky flags only (no result changes).
 *   SHEPHERD_K10_FP_BENCH=1 (TEST) the cost: at start, on the main thread, float / double multiply-adds on normal and on denormal operands, timed
 *                                  with FZ 1 DN 1 and with FZ 0 DN 0, interleaved, three rounds (medians) -> one "K10 FP bench" line. VLX timings
 *                                  are meaningless (qemu); the device line is the measurement (the kit README step).
 * A self-test line follows the apply after vglInit / at start: 1e-30 * 1e-10 (a denormal under FZ 0, 0 under FZ 1) and a NaN with a payload + 1 (the
 * payload kept under DN 0, 0x7FC00000 under DN 1) -- the proof the CPU (or qemu) obeys the bits.
 * Not covered (listed in the notes): host threads that never attach to Mono (vitaGL's garbage collector, shepaudio's output, the IO thread, the
 * launcher's splash threads) -- none runs game float code; a change an SCE library makes inside a call and undoes before it returns; NEON (always
 * flush-to-zero + default NaN on ARMv7, whatever FPSCR says: kept out of game-visible code by build.sh's NEON gate). */
#define K10_FP_FZ (1u << 24)
#define K10_FP_DN (1u << 25)
#define K10_FP_CTRL 0x07F70000u   /* AHP DN FZ RMode STRIDE LEN (= K9_FP_CTRL) */
#define K10_FP_STICKY 0x0000009Fu /* IDC IXC UFC OFC DZC IOC */
static int g_k10_fp_mode = -1;     /* 0 off (kit #9), 1 pc, 2 vita (test) */
static unsigned g_k10_fp_want;     /* the control bits written */
static const char *g_k10_fp_src = "";
static int g_k10_fp_census, g_k10_fp_fault;
static unsigned g_k10_fp_threads, g_k10_fp_threads_changed, g_k10_fp_life_changed, g_k10_fp_resets, g_k10_fp_warned;
static unsigned g_k10_fp_hb, g_k10_fp_hb_idc, g_k10_fp_hb_ufc, g_k10_fp_stop_idc, g_k10_fp_stop_ufc;
static int g_k10_fp_first_idc = -1, g_k10_fp_first_ufc = -1, g_k10_fp_last_idc = -1, g_k10_fp_last_ufc = -1;
static pthread_mutex_t g_k10_fp_lock = PTHREAD_MUTEX_INITIALIZER;
static struct _MonoProfiler g_k10_fp_prof;

static unsigned k10_fp_get (void)
{
#if defined (__arm__)
	unsigned v;
	__asm__ volatile ("vmrs %0, fpscr" : "=r" (v));
	return v;
#elif defined (__aarch64__)
	unsigned long v;
	__asm__ volatile ("mrs %0, fpcr" : "=r" (v));
	return (unsigned) v;   /* (the Mac check: FPCR has FZ / DN / RMode at the same bits, no sticky flags) */
#else
	return 0;
#endif
}
static void k10_fp_put (unsigned v)
{
#if defined (__arm__)
	__asm__ volatile ("vmsr fpscr, %0" : : "r" (v) : "memory");
#elif defined (__aarch64__)
	unsigned long w = v & K10_FP_CTRL;
	__asm__ volatile ("msr fpcr, %0" : : "r" (w) : "memory");
#else
	(void) v;
#endif
}
/* the wanted control bits on this thread; returns the FPSCR found */
static unsigned k10_fp_set (void)
{
	unsigned v = k10_fp_get ();
	unsigned nv = (v & ~K10_FP_CTRL) | g_k10_fp_want;
	if (nv != v)
		k10_fp_put (nv);
	return v;
}
static const char *k10_fp_rules (unsigned v)
{
	unsigned c = v & K10_FP_CTRL;
	return c == 0 ? "the PC's (FZ 0 DN 0 nearest)" : c == (K10_FP_FZ | K10_FP_DN) ? "the Vita's (FZ 1 DN 1 nearest)" : "other";
}

/* main thread, after the env file (the k7 / k8 configs) */
static void k10_fp_config (void)
{
	const char *e = getenv ("SHEPHERD_K10_FPSCR"), *so = getenv ("SHEPHERD_STRICT_ORIGINAL"), *c = getenv ("SHEPHERD_K10_FP_CENSUS");
	const char *ks = getenv ("SHEPHERD_K10_STRICT"), *fault = getenv ("SHEPHERD_K10_FP_FAULT");
	/* review fix: strict asks for the PC's rules only where the env file says nothing -- an explicit SHEPHERD_K10_FPSCR line (0 included) wins, and
	 * SHEPHERD_K10_STRICT=0 = kit #9's strict list (no float rules) */
	int strict = so && !strcmp (so, "1") && !(ks && !strcmp (ks, "0")), explicit_line = e && *e;
	g_k10_fp_mode = 0;
	if (strict && !explicit_line) {
		g_k10_fp_mode = 1;
		g_k10_fp_src = "SHEPHERD_STRICT_ORIGINAL=1";
	} else if (e && (!strcmp (e, "pc") || !strcmp (e, "1"))) {
		g_k10_fp_mode = 1;
		g_k10_fp_src = "SHEPHERD_K10_FPSCR=pc";
	} else if (e && !strcmp (e, "vita")) {
		g_k10_fp_mode = 2;
		g_k10_fp_src = "SHEPHERD_K10_FPSCR=vita (TEST: the device's default reproduced)";
	} else if (e && *e && strcmp (e, "0"))
		LOG ("WARN K10 FP: SHEPHERD_K10_FPSCR=%s is not pc, 1, vita or 0 -- off (kit #9)", e);
	g_k10_fp_want = g_k10_fp_mode == 2 ? (K10_FP_FZ | K10_FP_DN) : 0u;
	g_k10_fp_census = g_k10_fp_mode && c && !strcmp (c, "1");
	g_k10_fp_fault = g_k10_fp_mode && fault && !strcmp (fault, "handover");
#ifdef SHP_PHYS_LINK
	if (g_k10_fp_mode)   /* review fix: libshepphys counts the FPSCR each physics worker part ran under (the hand-over checked at the END lines) */
		sp_jobs_set (SP_JOBS_FP_STAT, g_k10_fp_fault ? 2 : 1);
#endif
	if (!g_k10_fp_mode)
		LOG ("K10 FP rules: off (SHEPHERD_K10_FPSCR unset / 0 = kit #9: every thread keeps the FPSCR the Vita gives it -- FZ 1 DN 1 on the device; "
		     "SHEPHERD_K10_FPSCR=pc = the PC's rules)%s", so && !strcmp (so, "1") ? (strict ? " -- the env file's SHEPHERD_K10_FPSCR line wins over "
		     "SHEPHERD_STRICT_ORIGINAL=1" : " -- SHEPHERD_K10_STRICT=0: kit #9's strict list, no float rules") : "");
	else
		LOG ("K10 FP rules: %s on the main thread, after vglInit, on the engine thread and on every thread Mono starts or attaches (the render "
		     "thread, the thread pool, the finalizer, A*), the physics workers through their fork hand-over (checked: SP_JOBS_FP_STAT); a heartbeat "
		     "watch re-applies them (%s)%s%s", g_k10_fp_mode == 1 ? "the PC's -- FZ 0 DN 0, round to nearest --" :
		     "the VITA's -- FZ 1 DN 1, round to nearest (test) --", g_k10_fp_src, g_k10_fp_census ? "; census of denormal flags on "
		     "(SHEPHERD_K10_FP_CENSUS=1, test)" : "", g_k10_fp_fault ? "; WARN TEST FAULT SHEPHERD_K10_FP_FAULT=handover: the physics workers SKIP "
		     "the fork's hand-over (the END check must report it)" : "");
}

/* the proof the bits act: a denormal product and a NaN payload, computed at run time on this thread */
static void k10_fp_selftest (const char *where)
{
	volatile float a = 1.0e-30f, b = 1.0e-10f, one = 1.0f;   /* normal operands, a denormal product (1e-40) */
	volatile unsigned nb = 0x7FC00123u;   /* a quiet NaN with a payload */
	float n;
	memcpy (&n, (const void *) &nb, 4);
	volatile float nv = n;
	float p = a * b, q = nv + one;
	unsigned pb, qb;
	memcpy (&pb, &p, 4);
	memcpy (&qb, &q, 4);
	int pc = pb != 0 && qb == 0x7FC00123u, vita = pb == 0 && qb == 0x7FC00000u;
	LOG ("%sK10 FP self-test (%s): 1e-30 * 1e-10 = 0x%08x (%s), NaN(0x123) + 1 = 0x%08x (%s) -- %s", (g_k10_fp_mode == 1 && !pc) || (g_k10_fp_mode == 2 && !vita) ? "WARN " : "",
	     where, pb, pb ? "a denormal: kept" : "flushed to zero", qb, qb == 0x7FC00123u ? "payload kept" : qb == 0x7FC00000u ? "the default NaN" : "other",
	     pc ? "the PC's rules act" : vita ? "the Vita's rules act" : "neither rule set (mixed)");
}

/* main / engine thread: write the rules, one line with what was there (+ the self-test on the main thread) */
static void k10_fp_apply (const char *where, int selftest)
{
	if (g_k10_fp_mode <= 0)
		return;
	unsigned was = k10_fp_set ();
	LOG ("K10 FP %s: FPSCR 0x%08x (%s) -> 0x%08x", where, was, k10_fp_rules (was), k10_fp_get ());
	if (selftest)
		k10_fp_selftest (where);
}
/* main thread after vglInit: was anything reset? */
static void k10_fp_after (const char *where)
{
	if (g_k10_fp_mode <= 0)
		return;
	unsigned was = k10_fp_set ();
	if ((was & K10_FP_CTRL) != g_k10_fp_want) {
		__atomic_add_fetch (&g_k10_fp_resets, 1, __ATOMIC_RELAXED);
		LOG ("WARN K10 FP %s: the control bits had changed to 0x%08x (%s) -- re-applied", where, was & K10_FP_CTRL, k10_fp_rules (was));
	} else
		LOG ("K10 FP %s: control bits unchanged (0x%08x)", where, was & K10_FP_CTRL);
	k10_fp_selftest (where);
}

/* the census (test): read and clear this thread's sticky denormal flags */
static unsigned k10_fp_take_sticky (void)
{
	unsigned v = k10_fp_get ();
	if (v & (0x80u | 0x08u))
		k10_fp_put (v & ~(0x80u | 0x08u));
	return v;
}

/* every Mono thread, on that thread, before its first managed instruction (Mono calls the newest profiler handle first: this one runs before
 * kit #9's check, whose line then shows the rules written here) */
static void k10_fp_thread_started (MonoProfiler *prof, uintptr_t tid)
{
	(void) prof; (void) tid;
	unsigned was = k10_fp_set ();
	__atomic_add_fetch (&g_k10_fp_threads, 1, __ATOMIC_RELAXED);
	if ((was & K10_FP_CTRL) != g_k10_fp_want)
		__atomic_add_fetch (&g_k10_fp_threads_changed, 1, __ATOMIC_RELAXED);
}
static void k10_fp_thread_stopped (MonoProfiler *prof, uintptr_t tid)
{
	(void) prof;
	unsigned v = g_k10_fp_census ? k10_fp_take_sticky () : k10_fp_get ();
	if (g_k10_fp_census) {
		if (v & 0x80u) __atomic_add_fetch (&g_k10_fp_stop_idc, 1, __ATOMIC_RELAXED);
		if (v & 0x08u) __atomic_add_fetch (&g_k10_fp_stop_ufc, 1, __ATOMIC_RELAXED);
	}
	if ((v & K10_FP_CTRL) != g_k10_fp_want) {
		__atomic_add_fetch (&g_k10_fp_life_changed, 1, __ATOMIC_RELAXED);
		if (__atomic_add_fetch (&g_k10_fp_warned, 1, __ATOMIC_RELAXED) <= 8)
			LOG ("WARN K10 FP Mono thread %#lx (uid %#x) at its stop: control bits 0x%08x (%s), not the ones written at its start", (unsigned long) tid,
			     (unsigned) sceKernelGetThreadId (), v & K10_FP_CTRL, k10_fp_rules (v));
	}
}
/* before mono_jit_init (after kit #9's handle: Mono calls the newest first) */
static void k10_fp_profiler (void)
{
	if (g_k10_fp_mode <= 0)
		return;
	MonoProfilerHandle h = mono_profiler_create (&g_k10_fp_prof);
	mono_profiler_set_thread_started_callback (h, k10_fp_thread_started);
	mono_profiler_set_thread_stopped_callback (h, k10_fp_thread_stopped);
}

/* the engine thread's heartbeat / checkpoint (shepherd_vita_engine_checkpoint): nothing may have changed the rules since; the census */
static void k10_fp_watch (int frame)
{
	if (g_k10_fp_mode <= 0)
		return;
	unsigned v = g_k10_fp_census ? k10_fp_take_sticky () : k10_fp_get ();
	if ((v & K10_FP_CTRL) != g_k10_fp_want) {
		k10_fp_set ();
		__atomic_add_fetch (&g_k10_fp_resets, 1, __ATOMIC_RELAXED);
		if (__atomic_add_fetch (&g_k10_fp_warned, 1, __ATOMIC_RELAXED) <= 8)
			LOG ("WARN K10 FP f=%d engine thread: the control bits had changed to 0x%08x (%s) -- re-applied", frame, v & K10_FP_CTRL, k10_fp_rules (v));
	}
	if (g_k10_fp_census) {
		pthread_mutex_lock (&g_k10_fp_lock);
		g_k10_fp_hb++;
		if (v & 0x80u) { g_k10_fp_hb_idc++; if (g_k10_fp_first_idc < 0) g_k10_fp_first_idc = frame; g_k10_fp_last_idc = frame; }
		if (v & 0x08u) { g_k10_fp_hb_ufc++; if (g_k10_fp_first_ufc < 0) g_k10_fp_first_ufc = frame; g_k10_fp_last_ufc = frame; }
		pthread_mutex_unlock (&g_k10_fp_lock);
	}
}

/* H11 / FAIL END: what the switch did */
static void k10_fp_end_line (const char *where)
{
	if (g_k10_fp_mode <= 0)
		return;
	unsigned v = k10_fp_get ();
	LOG ("%sK10 FP rules (%s): %s -- written on the main thread, after vglInit, on the engine thread and on %u Mono thread(s) at their start (%u of "
	     "them came with other control bits); %u reset(s) found and re-applied; %u Mono thread(s) with other control bits at their stop; this thread "
	     "now 0x%08x (%s)", g_k10_fp_resets || g_k10_fp_life_changed ? "WARN " : "", where, g_k10_fp_mode == 1 ? "the PC's" : "the Vita's (test)",
	     g_k10_fp_threads, g_k10_fp_threads_changed, g_k10_fp_resets, g_k10_fp_life_changed, v, k10_fp_rules (v));
	if (g_k10_fp_census) {
		pthread_mutex_lock (&g_k10_fp_lock);
		LOG ("K10 FP census (%s): engine heartbeats %u -- with a denormal INPUT flushed (IDC) %u (first f=%d, last f=%d), with a TINY result (UFC) %u "
		     "(first f=%d, last f=%d); Mono threads at their stop with IDC %u, UFC %u (IDC is set only under FZ 1: a denormal operand the Vita's rule "
		     "flushed = denormal work for the VFP under the PC's rules; UFC under FZ 0 only for a TINY AND INEXACT result -- an exact denormal result "
		     "or a denormal operand sets no flag, so under the PC's rules the denormal operands are NOT counted; not sampled: the render thread and the "
		     "pool / A* threads alive at the end)", where, g_k10_fp_hb, g_k10_fp_hb_idc, g_k10_fp_first_idc, g_k10_fp_last_idc,
		     g_k10_fp_hb_ufc, g_k10_fp_first_ufc, g_k10_fp_last_ufc, g_k10_fp_stop_idc, g_k10_fp_stop_ufc);
		pthread_mutex_unlock (&g_k10_fp_lock);
	}
#ifdef SHP_PHYS_LINK
	/* review fix: the physics workers' parts -- the fork hand-over checked (libshepphys SP_JOBS_FP_STAT, set in k10_fp_config) */
	{
		unsigned parts = (unsigned) sp_jobs_get (SP_JOBS_ST_FP_PARTS), por = (unsigned) sp_jobs_get (SP_JOBS_ST_FP_CTRL_OR);
		unsigned pand = (unsigned) sp_jobs_get (SP_JOBS_ST_FP_CTRL_AND);
		int ok = !parts || (por == g_k10_fp_want && pand == g_k10_fp_want);
		if (!parts)
			LOG ("K10 FP physics workers (%s): no part ran on a worker (%d live; SHEPHERD_PHYS_WORKERS=%d -- 0, the kit apps' default, = the serial "
			     "step on the engine thread: nothing to hand over)", where, (int) sp_jobs_get (SP_JOBS_WORKERS), g_phys_workers);
		else
			LOG ("%sK10 FP physics workers (%s): %u part(s) ran on the workers under FPSCR control bits OR 0x%08x / AND 0x%08x -- %s%s", ok ? "" : "WARN ",
			     where, parts, por, pand, ok ? "every part ran under the rules written (the fork hand-over gave the workers the engine thread's)" :
			     "NOT the rules written on the engine thread: the fork hand-over did not happen for every part", g_k10_fp_fault ?
			     " (TEST FAULT SHEPHERD_K10_FP_FAULT=handover: expected)" : "");
		if (parts && g_k10_fp_census)
			LOG ("K10 FP census (%s): physics worker parts %u -- with a denormal INPUT flushed (IDC) %u, with a TINY inexact result (UFC) %u", where, parts,
			     (unsigned) sp_jobs_get (SP_JOBS_ST_FP_IDC), (unsigned) sp_jobs_get (SP_JOBS_ST_FP_UFC));
	}
#endif
}

/* ---- the cost probe (SHEPHERD_K10_FP_BENCH=1, test): scalar VFP only (the asm barrier keeps each value in a VFP register: no NEON, no
 * vectorising -- build.sh's NEON gate would refuse the build otherwise) ---- */
#if defined (__arm__)
#define K10_KEEP_F(x) __asm__ volatile ("" : "+t" (x))
#define K10_KEEP_D(x) __asm__ volatile ("" : "+w" (x))
#else
#define K10_KEEP_F(x) __asm__ volatile ("" : "+w" (x))
#define K10_KEEP_D(x) __asm__ volatile ("" : "+w" (x))
#endif
#define K10_BENCH_N 1000000
static float g_k10_bf [64];
static double g_k10_bd [64];
static unsigned k10_bench_f (int denormal, float *out)
{
	float k = 0.5f, acc = 0.0f;
	const float *in = g_k10_bf + (denormal ? 32 : 0);
	SceUInt64 t0 = sceKernelGetProcessTimeWide ();
	for (int i = 0; i < K10_BENCH_N; i++) {
		float x = in [i & 31];
		K10_KEEP_F (x);
		acc = acc * k + x;   /* denormal: every operand and result stays denormal (acc -> ~2x) under FZ 0, all zeros under FZ 1 */
		K10_KEEP_F (acc);
	}
	*out = acc;
	return (unsigned) (sceKernelGetProcessTimeWide () - t0);
}
static unsigned k10_bench_d (int denormal, double *out)
{
	double k = 0.5, acc = 0.0;
	const double *in = g_k10_bd + (denormal ? 32 : 0);
	SceUInt64 t0 = sceKernelGetProcessTimeWide ();
	for (int i = 0; i < K10_BENCH_N; i++) {
		double x = in [i & 31];
		K10_KEEP_D (x);
		acc = acc * k + x;
		K10_KEEP_D (acc);
	}
	*out = acc;
	return (unsigned) (sceKernelGetProcessTimeWide () - t0);
}
static unsigned k10_med3 (unsigned a, unsigned b, unsigned c)
{
	return a > b ? (b > c ? b : a > c ? c : a) : (a > c ? a : b > c ? c : b);
}
static void k10_fp_bench (void)
{
	const char *e = getenv ("SHEPHERD_K10_FP_BENCH");
	if (!e || strcmp (e, "1"))
		return;
	for (int i = 0; i < 32; i++) {   /* the denormal inputs as bit patterns (computing them under the Vita's FZ 1 would give zeros) */
		unsigned fb = 0x00010000u * (unsigned) (i + 1);             /* exponent 0: denormal floats ~9e-41 .. 3e-39 (FLT_MIN 1.18e-38) */
		unsigned long long db = 0x0000100000000000ull * (unsigned long long) (i + 1);   /* denormal doubles (DBL_MIN 2.2e-308) */
		g_k10_bf [i] = 1.0f + (float) i / 64.0f;                  /* normal: 1 .. 1.5 */
		memcpy (&g_k10_bf [32 + i], &fb, 4);
		g_k10_bd [i] = 1.0 + (double) i / 64.0;
		memcpy (&g_k10_bd [32 + i], &db, 8);
	}
	unsigned t [2][4][3];   /* [FZ0, FZ1][float normal, float denormal, double normal, double denormal][round] */
	float rf [2] = { 0, 0 };
	double rd [2] = { 0, 0 };
	unsigned save = k10_fp_get ();
	for (int r = 0; r < 3; r++)
		for (int m0 = 0; m0 < 2; m0++) {
			int m = (r & 1) ? 1 - m0 : m0;   /* interleaved: FZ0 first in rounds 0 / 2, FZ1 first in round 1 */
			k10_fp_put ((save & ~K10_FP_CTRL) | (m ? (K10_FP_FZ | K10_FP_DN) : 0u));
			float f;
			double d;
			t [m][0][r] = k10_bench_f (0, &f);
			t [m][1][r] = k10_bench_f (1, &f);
			rf [m] = f;
			t [m][2][r] = k10_bench_d (0, &d);
			t [m][3][r] = k10_bench_d (1, &d);
			rd [m] = d;
		}
	k10_fp_put (save);
	unsigned md [2][4];
	for (int m = 0; m < 2; m++)
		for (int k = 0; k < 4; k++)
			md [m][k] = k10_med3 (t [m][k][0], t [m][k][1], t [m][k][2]);
	unsigned fb0, fb1;
	memcpy (&fb0, &rf [0], 4);
	memcpy (&fb1, &rf [1], 4);
	LOG ("K10 FP bench (main thread, %d multiply-adds per cell, the median of 3 interleaved rounds, ns per op; clock as CLK says): float normal "
	     "FZ1 %.2f / FZ0 %.2f, float DENORMAL FZ1 %.2f / FZ0 %.2f (x%.2f); double normal FZ1 %.2f / FZ0 %.2f, double DENORMAL FZ1 %.2f / FZ0 %.2f "
	     "(x%.2f); denormal results FZ0 0x%08x (kept) / FZ1 0x%08x (flushed), double %s -- the PC's rules cost only on maths that meets denormals "
	     "(VLX timings are meaningless: qemu)", K10_BENCH_N,
	     md [1][0] * 1000.0 / K10_BENCH_N, md [0][0] * 1000.0 / K10_BENCH_N, md [1][1] * 1000.0 / K10_BENCH_N, md [0][1] * 1000.0 / K10_BENCH_N,
	     md [1][1] ? (double) md [0][1] / md [1][1] : 0.0,
	     md [1][2] * 1000.0 / K10_BENCH_N, md [0][2] * 1000.0 / K10_BENCH_N, md [1][3] * 1000.0 / K10_BENCH_N, md [0][3] * 1000.0 / K10_BENCH_N,
	     md [1][3] ? (double) md [0][3] / md [1][3] : 0.0, fb0, fb1, rd [0] != 0.0 && rd [1] == 0.0 ? "kept / flushed" : "NOT as expected");
}
