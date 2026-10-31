/* device kit #9, track fidelity (vita/notes/kit9-fidelity.md, roadmap "VFP-only float maths with FZ / DN off"): the floating-point status
 * and control register as each thread sees it, logged -- the measurement before any fix. The PC original's managed float maths (Unity's
 * player Mono on x64: SSE with MXCSR's FTZ / DAZ off) keeps denormals, propagates NaN operands and rounds to nearest; the ARM equivalent is
 * FPSCR FZ = 0 (no flush to zero), DN = 0 (no default NaN) and RMode = 0 (round to nearest). A Vita thread whose FPSCR has FZ or DN set
 * would flush denormal results to zero / replace NaN payloads, unlike the PC (and unlike vlx: qemu-user starts every thread with FPSCR 0).
 * Logged: the host's main thread at start (and after vglInit in the GL apps: vitaGL loads SceShaccCg there), the engine thread (Mono's
 * main managed thread) before Main and after it returns, and -- second review -- EVERY thread Mono starts or attaches (the A* pathfinding
 * threads, the thread pool, the finalizer, ...; ARM threads do not inherit FPSCR) when it starts and when it stops, with one summary line at
 * the end ("FP Mono threads: ..."). Nothing is changed (owner decision first: the device line says what the Vita's default is). One "FP"
 * line each for the fixed points, a line per thread only when it is "WARN FP" (FZ, DN or a rounding mode other than nearest set, or the
 * control bits changed during the thread's life; the first 16). Not seen: a change an SCE library makes inside a call and undoes before it
 * returns (no hook there). No switch needed: log lines only; SHEPHERD_K9_FP=0 silences them and installs no thread hook (kit #8). */
static int g_k9_fp_off = -1;
static unsigned k9_fpscr (void)
{
	unsigned v;
	__asm__ volatile ("vmrs %0, fpscr" : "=r" (v));
	return v;
}
static void k9_fp_line (const char *where)
{
	if (g_k9_fp_off < 0) {
		const char *e = getenv ("SHEPHERD_K9_FP");
		g_k9_fp_off = e && strcmp (e, "0") == 0;
	}
	if (g_k9_fp_off)
		return;
	unsigned v = k9_fpscr ();
	int fz = (v >> 24) & 1, dn = (v >> 25) & 1, rm = (v >> 22) & 3, ahp = (v >> 26) & 1;
	static const char *const rms [] = { "nearest", "+inf", "-inf", "zero" };
	int bad = fz || dn || rm != 0;
	LOG ("%sFP %s: FPSCR 0x%08x -- FZ %d DN %d RMode %d (%s) AHP %d LEN %u STRIDE %u; sticky IDC %u IXC %u UFC %u OFC %u DZC %u IOC %u -- %s",
	     bad ? "WARN " : "", where, v, fz, dn, rm, rms [rm], ahp, (v >> 16) & 7, (v >> 20) & 3,
	     (v >> 7) & 1, (v >> 4) & 1, (v >> 3) & 1, (v >> 2) & 1, (v >> 1) & 1, v & 1,
	     bad ? "NOT the PC's float rules (the PC keeps denormals and NaN payloads, rounds to nearest): kit #9 fidelity, owner decision"
	         : "the PC's float rules (no flush to zero, no default NaN, round to nearest)");
}

/* second review (vita/notes/kit9-fidelity.md 13.3): every Mono thread, at its start and at its stop (the profiler's thread events run on that
 * thread). The control bits (LEN, STRIDE, RMode, FZ, DN, AHP) at the stop are compared with the start's. */
#define K9_FP_CTRL 0x07F70000u
#define K9_FP_SLOTS 64
static struct { SceUID uid; unsigned start; } g_k9_fp_slot [K9_FP_SLOTS];
static unsigned g_k9_fp_started, g_k9_fp_stopped, g_k9_fp_bad_start, g_k9_fp_bad_stop, g_k9_fp_changed, g_k9_fp_untracked, g_k9_fp_warned;
static unsigned g_k9_fp_first = 0xFFFFFFFFu;   /* the first thread's control bits */
static pthread_mutex_t g_k9_fp_lock = PTHREAD_MUTEX_INITIALIZER;
static struct _MonoProfiler g_k9_fp_prof;
static int k9_fp_pc_rules (unsigned v) { return !((v >> 24) & 1) && !((v >> 25) & 1) && !((v >> 22) & 3); }
static void k9_fp_thread_warn (const char *when, uintptr_t tid, unsigned v, unsigned was)
{
	if (__atomic_add_fetch (&g_k9_fp_warned, 1, __ATOMIC_RELAXED) > 16)
		return;
	LOG ("WARN FP Mono thread %#lx (uid %#x) at %s: FPSCR 0x%08x -- FZ %u DN %u RMode %u%s%s", (unsigned long) tid, (unsigned) sceKernelGetThreadId (), when,
	     v, (v >> 24) & 1, (v >> 25) & 1, (v >> 22) & 3, k9_fp_pc_rules (v) ? "" : " -- NOT the PC's float rules",
	     was != 0xFFFFFFFFu && ((was ^ v) & K9_FP_CTRL) ? " -- the control bits changed during the thread's life" : "");
}
static void k9_fp_thread_started (MonoProfiler *prof, uintptr_t tid)
{
	(void) prof;
	unsigned v = k9_fpscr ();
	SceUID me = sceKernelGetThreadId ();
	pthread_mutex_lock (&g_k9_fp_lock);
	g_k9_fp_started++;
	if (g_k9_fp_first == 0xFFFFFFFFu)
		g_k9_fp_first = v & K9_FP_CTRL;
	int k = -1;
	for (int i = 0; i < K9_FP_SLOTS && k < 0; i++)
		if (!g_k9_fp_slot [i].uid || g_k9_fp_slot [i].uid == me)
			k = i;
	if (k >= 0) {
		g_k9_fp_slot [k].uid = me;
		g_k9_fp_slot [k].start = v;
	} else
		g_k9_fp_untracked++;
	int bad = !k9_fp_pc_rules (v);
	if (bad)
		g_k9_fp_bad_start++;
	pthread_mutex_unlock (&g_k9_fp_lock);
	if (bad)
		k9_fp_thread_warn ("start", tid, v, 0xFFFFFFFFu);
}
static void k9_fp_thread_stopped (MonoProfiler *prof, uintptr_t tid)
{
	(void) prof;
	unsigned v = k9_fpscr (), was = 0xFFFFFFFFu;
	SceUID me = sceKernelGetThreadId ();
	pthread_mutex_lock (&g_k9_fp_lock);
	g_k9_fp_stopped++;
	for (int i = 0; i < K9_FP_SLOTS; i++)
		if (g_k9_fp_slot [i].uid == me) {
			was = g_k9_fp_slot [i].start;
			g_k9_fp_slot [i].uid = 0;
			break;
		}
	int changed = was != 0xFFFFFFFFu && ((was ^ v) & K9_FP_CTRL), bad = !k9_fp_pc_rules (v);
	g_k9_fp_changed += changed;
	g_k9_fp_bad_stop += bad;
	pthread_mutex_unlock (&g_k9_fp_lock);
	if (bad || changed)
		k9_fp_thread_warn ("stop", tid, v, was);
}
/* after stack_watch_profiler / k7_profiler: a handle of its own (the thread events of the other handles stay theirs) */
static void k9_fp_profiler (void)
{
	if (g_k9_fp_off < 0) {
		const char *e = getenv ("SHEPHERD_K9_FP");
		g_k9_fp_off = e && strcmp (e, "0") == 0;
	}
	if (g_k9_fp_off)
		return;
	MonoProfilerHandle h = mono_profiler_create (&g_k9_fp_prof);
	mono_profiler_set_thread_started_callback (h, k9_fp_thread_started);
	mono_profiler_set_thread_stopped_callback (h, k9_fp_thread_stopped);
}
static void k9_fp_threads_line (const char *where)
{
	if (g_k9_fp_off)
		return;
	pthread_mutex_lock (&g_k9_fp_lock);
	unsigned st = g_k9_fp_started, sp = g_k9_fp_stopped, bs = g_k9_fp_bad_start, bp = g_k9_fp_bad_stop, ch = g_k9_fp_changed, un = g_k9_fp_untracked;
	unsigned first = g_k9_fp_first;
	pthread_mutex_unlock (&g_k9_fp_lock);
	int bad = bs || bp || ch;
	LOG ("%sFP Mono threads (%s): %u started (%u not the PC's float rules at their start), %u stopped (%u not the PC's rules at their stop, %u with "
	     "control bits changed during their life)%s; first thread's control bits 0x%08x -- %s", bad ? "WARN " : "", where, st, bs, sp, bp, ch,
	     un ? " (some not tracked: the table full)" : "", first == 0xFFFFFFFFu ? 0u : first,
	     bad ? "NOT all of them on the PC's float rules: kit #9 fidelity, owner decision" : "every one checked on the PC's float rules (FZ 0, DN 0, nearest)");
}
