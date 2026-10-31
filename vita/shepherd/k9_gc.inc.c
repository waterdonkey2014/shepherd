/* SHEPHERD-VITA device kit #9, track gc (vita/notes/kit9-gc.md): the GC's settings and their host side. Included by host.c (before the Mono
 * thread's body). Every switch is an env-file line; with all of them unset (or SHEPHERD_K9_GC=0) MONO_GC_PARAMS, the arena and every
 * runtime call are kit #8's byte for byte: the H4 line shows the same parameter string.
 *
 *   SHEPHERD_GC_CONC=1|0      lever 32 (DEFAULT ON in the play apps, off in 00099 / 00011: see DEFAULTS below): CONCURRENT mark + sweep -- sgen's marksweep-conc collector with concurrent-sweep: a major collection
 *                             starts with a short pause (a nursery collection + the roots), sgen's worker thread marks while the game runs,
 *                             and a finishing pause rescans what changed; the sweep runs on the worker too. The worker is pinned at its start
 *                             (vita/patches/k9-gc.patch's hook) to SHEPHERD_GC_CORE: 2 (default) = USER_2, 3 = CPU3 when the boot probe found
 *                             it open (else USER_2), worker = the latency-tolerant workers' mask (follows the 4th CPU Core row). Its priority
 *                             stays Mono's default unless SHEPHERD_GC_PRIO=<n> (a larger number = lower; shepaudio's output thread is 64).
 *                             SHEPHERD_GC_CONC_SWEEP=0 keeps the sweep serial. GC.Collect calls (the black-frame GC, the pause menu's) stay
 *                             full stop-the-world collections (sgen runs every explicit collection serially). Above soft-heap-limit (the cap
 *                             - 16 MiB) sgen runs its own majors serially too (stock sgen: forced): lever 32 acts only below it (review F1).
 *                             The worker is pinned only when core pinning is on in the app (SHEPHERD_VITA_PIN / SHEPHERD_MT_RENDER: kit #3's
 *                             contract -- pinning off, no thread is touched; review F11). Needs the kit #9 runtime: with kit #8's
 *                             (SHEPHERD_K9_RUNTIMES=0) the default is off (kit #8's GC); an explicit =1 runs stock sgen's concurrent collector
 *                             unpinned and without the cap rule (review F6).
 *   SHEPHERD_GC_CONC_CAP=0    (lever 32) stock sgen at the heap cap. Default 1 = the CAP RULE (vita/patches/k9-gc.patch, review F2): an
 *                             allocation that max-heap-size would refuse gets a forced serial major, its sweep completed before the retry --
 *                             what kit #8's serial collector does there (stock concurrent sgen refuses it during a mark or a concurrent sweep);
 *                             and a concurrent mark still running when the heap passes soft-heap-limit is finished by a forced serial major
 *                             (the serial collector runs one there; stock sgen lets the mark run on into the cap).
 *   SHEPHERD_GC_STATS=0|1     the runtime's kit #9 counters (nursery pinning, the worker / sweep waits, the forced majors) -- default on when any
 *                             switch here is on, off otherwise (00099 / 00011's defaults: kit #8's code paths; review F7).
 *   SHEPHERD_GC_SPLIT=1       M6: the SPLIT nursery (sgen minor=split): a survivor of one nursery collection ages in the nursery's own aging
 *                             space and is promoted only at the next one (SHEPHERD_GC_SPLIT_AGE=<n> promotion-age, SHEPHERD_GC_SPLIT_RATIO=<n>
 *                             alloc-ratio in percent: sgen's defaults otherwise) -- fewer short-lived objects in the major heap, fewer majors.
 *   SHEPHERD_GC_NURSERY=scene per-scene nursery sizing: a dynamic nursery of SHEPHERD_GC_NURSERY_MIN..MAX KiB (default 4096..8192; the whole
 *                             MAX is reserved in the arena at start -- 4 MiB more than kit #8's fixed 4 MiB with the defaults) whose size the
 *                             host sets per scene from the learning cache ux0:data/shepherd/cache/gc-nursery.tsv (each scene's allocation
 *                             history: frames, main-thread allocation, nursery / major collections); a scene never seen yet gets MIN.
 *                             Not with SHEPHERD_GC_SPLIT=1 (sgen's split nursery has a fixed size: a WARN line, the split wins).
 *                             SHEPHERD_GC_NURSERY=<n> (KiB, a power of two) = a FIXED nursery of that size (an experiment; 4096 = kit #8).
 *   SHEPHERD_GC_USED=cache|counter|check|0  the sgen per-frame used-size fix (cache = DEFAULT in the play apps): GC.GetTotalMemory's major-heap figure from the previous walk while
 *                             nothing changed (cache: the same answer by construction), from sgen's own book (counter: no walk), or both
 *                             checked against a walk on every call (check: the proof run; mismatch counts on the K9 GC end line).
 *                             0 = kit #8 (every call walks every major-heap object under the GC lock; 00099 / 00011's default).
 *   SHEPHERD_ARENA_ENDS=2     M8: the TWO-ENDED arena -- sgen's large-object space from the arena's high end (top-down), the major heap's
 *                             blocks and everything else first fit from the bottom as before; SHEPHERD_ARENA_HIGH_KB=<n> also sends every
 *                             other request of at least n KiB (never a major-heap block run) to the high end. =2m: the mirror image (the
 *                             major heap's block runs from the high end, the LOS and the rest from the bottom). 1 / unset = kit #8.
 *   SHEPHERD_GC_CENSUS=1      (research, test only; STRETCH "smaller object headers, count first") at the end of every major collection,
 *                             with the world still stopped, a walk of the whole heap (mono_gc_walk_heap): objects, their true sizes, what a
 *                             4-byte header (no sync word; the vtable word keeps the lock / hash bits) would save at sgen's 8-byte alignment,
 *                             the most numerous classes -- the K9 GC census line at the end (the last walk). Slows every major: never on.
 *   SHEPHERD_K9_GC=0          every switch above off (kit #8), whatever the lines say.
 * A runtime without vita/patches/k9-gc.patch (build.sh SHEPHERD_K9_RUNTIMES=0: kit #8's) has none of the entry points (weak symbols): the
 * K9 GC line says so and the switches that need them stay off; concurrent mark / the split nursery need only MONO_GC_PARAMS (stock sgen) but
 * then run without the worker pin.
 * Lines: "K9 GC" (the configuration, at start), "PIN sgen worker" (the worker's core, at its start), "K9 GC scene" (each scene's learned
 * nursery), "K9 GC end" (pauses by kind, the worst pause, the GC heap's peak, the arena's ends, the used-size counts). */

#ifndef MONO_VITA_ARENA_TWO_ENDED   /* (host.c reads the fork's stock mono-vita.h; vita/patches/k9-gc.patch's declarations, kept in step) */
typedef void (*MonoVitaGcThreadFn) (int index);
typedef struct {
	unsigned calls, walks, cache_hits;
	unsigned cache_checked, cache_mismatches;
	unsigned counter_checked, counter_mismatches;
	int64_t last_walk, last_counter;
	int64_t worst_counter_diff;
} MonoVitaUsedSizeStats;
void mono_vita_vm_arena_set_policy (int policy, size_t high_min) __attribute__((weak));
void mono_vita_vm_arena_get_ends (unsigned *high_allocs, size_t *low_top, size_t *high_bottom) __attribute__((weak));
void mono_vita_set_gc_thread_callback (MonoVitaGcThreadFn fn) __attribute__((weak));
void mono_vita_gc_set_used_size_mode (int mode) __attribute__((weak));
void mono_vita_gc_get_used_size_stats (MonoVitaUsedSizeStats *stats) __attribute__((weak));
void mono_vita_gc_set_nursery_target (size_t bytes) __attribute__((weak));
void mono_vita_gc_get_nursery (size_t *size, size_t *min, size_t *max) __attribute__((weak));
typedef struct {
	unsigned pinnings, with_pinned;
	unsigned objects, max_objects;
	uint64_t bytes;
	unsigned max_bytes;
} MonoVitaPinStats;
void mono_vita_gc_get_pin_stats (MonoVitaPinStats *stats) __attribute__((weak));
/* kit #9 gc review (the patch's mono-vita.h; kept in step) */
typedef struct {
	unsigned finish_waits, finish_wait_max_us;
	uint64_t finish_wait_us;
	unsigned sweep_waits, sweep_wait_max_us;
	uint64_t sweep_wait_us;
	unsigned soft_forced;
	unsigned cap_forced;
	unsigned explicit_collections;
} MonoVitaGcWaitStats;
void mono_vita_gc_set_stats (int on) __attribute__((weak));
void mono_vita_gc_set_conc_cap (int on) __attribute__((weak));
void mono_vita_gc_get_wait_stats (MonoVitaGcWaitStats *stats) __attribute__((weak));
#endif

/* DEFAULTS (the track's gates, vita/notes/kit9-gc.md sections 9-10: identity on every kit route and app with each switch on, PASS END at the
 * device partitions, spans within kit #8's steps): in the PLAY apps (00097 / 00098 / 00095 and the launcher apps 00193 / 00194) lever 32
 * (concurrent mark + sweep) and the used-size cache are ON; the measurement app 00099 and the headless 00011 keep kit #8's GC (their
 * kit-to-kit history, like 00099's 444 MHz). Everything else is opt-in. SHEPHERD_GC_CONC=0 / SHEPHERD_GC_USED=0 / SHEPHERD_K9_GC=0 = kit #8. */
#ifdef SHP_PLAY_APP
#define K9_CONC_DEFAULT 1
#define K9_USED_DEFAULT 1
#else
#define K9_CONC_DEFAULT 0
#define K9_USED_DEFAULT 0
#endif

static struct {
	int on;               /* SHEPHERD_K9_GC != 0 */
	int rt;               /* the runtime has k9-gc.patch's entry points */
	int conc, conc_sweep, core, prio;   /* lever 32 (core: 2 / 3 / 1 = worker mask) */
	int split, split_age, split_ratio;  /* M6 */
	int nursery_scene;    /* per-scene sizing */
	unsigned nursery_fixed_kib, nursery_min_kib, nursery_max_kib;
	int used;             /* 0 kit #8, 1 cache, 2 counter, 3 check */
	int arena_ends;       /* 2 = two-ended */
	unsigned arena_high_kib;
	int workers;          /* sgen worker threads started */
	int census;           /* SHEPHERD_GC_CENSUS=1 (research, test only): the object-header census at every major collection */
	int pin;              /* review F11: core pinning is on in this app (pin_requested, read in main before any thread) */
	int cap;              /* review F2: the concurrent collector's cap rule (SHEPHERD_GC_CONC_CAP, default 1 with lever 32) */
	int stats;            /* review F7: the runtime's kit #9 counters (SHEPHERD_GC_STATS) */
	int base_track;       /* review F5: M2's base follows the heap's floor until the next major (lever 32) */
} k9g;

static int k9g_env_int (const char *name, int dflt)
{
	const char *v = getenv (name);
	return v && *v ? atoi (v) : dflt;
}

/* main, right after the arena exists (before the Mono thread): read the switches; set the arena's policy */
static void k9_gc_config (void)
{
	const char *m = getenv ("SHEPHERD_K9_GC");
	k9g.on = !(m && !strcmp (m, "0"));
	k9g.rt = mono_vita_vm_arena_set_policy && mono_vita_set_gc_thread_callback && mono_vita_gc_set_used_size_mode && mono_vita_gc_set_nursery_target
		 && mono_vita_gc_set_stats && mono_vita_gc_set_conc_cap && mono_vita_gc_get_wait_stats;
	k9g.pin = pin_requested ();   /* review F11 (main, before the threads: the power thread's getenv race, k9 gc fix) */
	if (!k9g.on)
		return;
	/* review F6: the play apps' default needs the kit #9 runtime -- with kit #8's (SHEPHERD_K9_RUNTIMES=0) the default is kit #8's GC */
	k9g.conc = k9g_env_int ("SHEPHERD_GC_CONC", k9g.rt ? K9_CONC_DEFAULT : 0) == 1;
	k9g.conc_sweep = k9g.conc && k9g_env_int ("SHEPHERD_GC_CONC_SWEEP", 1) != 0;
	k9g.cap = k9g.conc && k9g.rt && k9g_env_int ("SHEPHERD_GC_CONC_CAP", 1) != 0;
	const char *c = getenv ("SHEPHERD_GC_CORE");
	k9g.core = c && !strcmp (c, "3") ? 3 : c && !strcmp (c, "worker") ? 1 : c && !strcmp (c, "0") ? 0 : 2;
	k9g.prio = k9g_env_int ("SHEPHERD_GC_PRIO", 0);
	k9g.split = k9g_env_int ("SHEPHERD_GC_SPLIT", 0) == 1;
	k9g.split_age = k9g_env_int ("SHEPHERD_GC_SPLIT_AGE", 0);
	k9g.split_ratio = k9g_env_int ("SHEPHERD_GC_SPLIT_RATIO", 0);
	const char *n = getenv ("SHEPHERD_GC_NURSERY");
	if (n && !strcmp (n, "scene")) {
		k9g.nursery_scene = 1;
		k9g.nursery_min_kib = (unsigned) k9g_env_int ("SHEPHERD_GC_NURSERY_MIN", 4096);
		k9g.nursery_max_kib = (unsigned) k9g_env_int ("SHEPHERD_GC_NURSERY_MAX", 8192);
		if (k9g.nursery_min_kib < 512 || (k9g.nursery_min_kib & (k9g.nursery_min_kib - 1))) k9g.nursery_min_kib = 4096;
		if (k9g.nursery_max_kib < k9g.nursery_min_kib || (k9g.nursery_max_kib & (k9g.nursery_max_kib - 1))) k9g.nursery_max_kib = 8192;
		if (k9g.split) {
			LOG ("WARN K9 GC SHEPHERD_GC_NURSERY=scene with SHEPHERD_GC_SPLIT=1: sgen's split nursery has a fixed size -- per-scene sizing is off, the split nursery stays");
			k9g.nursery_scene = 0;
		} else if (!k9g.rt) {
			LOG ("WARN K9 GC SHEPHERD_GC_NURSERY=scene needs the kit #9 runtime (vita/patches/k9-gc.patch): per-scene sizing is off");
			k9g.nursery_scene = 0;
		}
	} else if (n && atoi (n) >= 512 && !(atoi (n) & (atoi (n) - 1)) && !k9g.split)
		k9g.nursery_fixed_kib = (unsigned) atoi (n);
	const char *u = getenv ("SHEPHERD_GC_USED");
	k9g.used = !u || !*u ? (k9g.rt ? K9_USED_DEFAULT : 0) : !strcmp (u, "cache") || !strcmp (u, "1") ? 1 : !strcmp (u, "counter") || !strcmp (u, "2") ? 2 : !strcmp (u, "check") || !strcmp (u, "3") ? 3 : 0;
	const char *ae = getenv ("SHEPHERD_ARENA_ENDS");
	k9g.arena_ends = ae && !strcmp (ae, "2") ? 2 : ae && !strcmp (ae, "2m") ? 3 : 1;
	k9g.arena_high_kib = (unsigned) k9g_env_int ("SHEPHERD_ARENA_HIGH_KB", 0);
	k9g.census = k9g_env_int ("SHEPHERD_GC_CENSUS", 0) == 1;
	if (!k9g.rt && (k9g.used || k9g.arena_ends >= 2)) {
		LOG ("WARN K9 GC SHEPHERD_GC_USED / SHEPHERD_ARENA_ENDS need the kit #9 runtime (vita/patches/k9-gc.patch): off");
		k9g.used = 0; k9g.arena_ends = 1;
	}
	if (k9g.rt && k9g.arena_ends >= 2)
		mono_vita_vm_arena_set_policy (k9g.arena_ends == 3 ? 2 : 1, (size_t) k9g.arena_high_kib * 1024);
	/* review F7: the runtime's counters only when a kit #9 GC switch is on (or SHEPHERD_GC_STATS=1); off = kit #8's code paths */
	int any = k9g.conc || k9g.split || k9g.nursery_scene || k9g.nursery_fixed_kib || k9g.used || k9g.arena_ends >= 2 || k9g.census;
	k9g.stats = k9g.rt && k9g_env_int ("SHEPHERD_GC_STATS", any) == 1;
	k9g.base_track = k9g.conc;
}

/* review F5 (M2's growth gate under lever 32; gc_event_cb and the governor, host.c): kit #8 sets M2's base -- "what the last major left" --
 * at every major pause. Under lever 32 that is wrong twice: a concurrent major's START pause frees nothing (its figure is the heap before
 * the major), and its FINISHING pause's sweep runs afterwards on sgen's worker (the figure is the unswept heap; sgen updates it when the
 * sweep ends). So with lever 32: the start pause leaves the base alone, every other major sets it, and until the next major the base
 * follows the heap's floor (each later collection's figure and the governor's own reading: the swept heap shows there). Returns 0 with
 * lever 32 off (the caller then runs kit #8's line); kind -1 = the governor's reading. */
static int k9_base_floor_on;
static inline int k9_gc_base_update (int major, int kind, long long heap, volatile long long *base)
{
	if (!k9g.base_track)
		return 0;
	if (major && kind != 1) {   /* a serial / finishing / explicit major: its figure, then the floor */
		*base = heap;
		k9_base_floor_on = 1;
	} else if (!major && k9_base_floor_on && heap >= 0 && heap < *base)
		*base = heap;   /* (a minor pause, or the governor's reading: the worker's sweep has freed blocks since) */
	return 1;
}

/* the sgen worker (lever 32), on its own thread before any work: the core, optionally the priority, one PIN line.
 * Review F11: only with core pinning on in the app (kit #3's contract: pinning off, no thread is touched -- 00097 by default);
 * SHEPHERD_GC_CORE=3 / worker = the latency-tolerant workers' mask (CPU3 when the C3 probe found it open AND Settings > 4th CPU Core is
 * On, else USER_2), registered so that row moves it with the others; 2 (default) = USER_2, which the row never moves.
 * Review F8: the line also gives the worker's stack (SHEPHERD_THREAD_STACK_KB: from the thread-stack reserve, taken during mono_jit_init,
 * after vglInit) and the free user memory after it. */
static void k9_gc_worker_started (int index)
{
	SceUID me = sceKernelGetThreadId ();
	k9g.workers++;
	int mask = !k9g.pin ? 0 : k9g.core == 2 ? SHP_USER2_MASK : k9g.core == 3 || k9g.core == 1 ? shepherd_vita_worker_mask () : 0;
	int before = sceKernelGetThreadCpuAffinityMask (me);
	int rc = mask ? sceKernelChangeThreadCpuAffinityMask (me, mask) : 0;
	int after = sceKernelGetThreadCpuAffinityMask (me);
	SceKernelThreadInfo wi;
	memset (&wi, 0, sizeof wi); wi.size = sizeof wi;
	int ti = sceKernelGetThreadInfo (me, &wi);
	int p0 = ti >= 0 ? wi.currentPriority : -1, prc = 0;
#ifndef SHEPHERD_MAC_CHECK
	unsigned stack_kib = ti >= 0 ? (unsigned) (wi.stackSize / 1024) : 0u;
#else
	unsigned stack_kib = 0;   /* (the mac-check's SceKernelThreadInfo stand-in has no stackSize) */
#endif
	if (k9g.prio > 0)
		prc = sceKernelChangeThreadPriority (me, k9g.prio);
	SceKernelFreeMemorySizeInfo fm;
	memset (&fm, 0, sizeof fm); fm.size = sizeof fm;
	int frc = sceKernelGetFreeMemorySize (&fm);
	LOG ("PIN sgen worker %d (SceUID 0x%x; lever 32: concurrent %s): cpu affinity 0x%x -> 0x%x: rc 0x%08x, read back 0x%x%s; priority %d%s; stack %u KiB "
	     "(a thread of its own: SHEPHERD_THREAD_STACK_KB, from the thread-stack reserve), free user memory now %u KiB",
	     index, (unsigned) me, k9g.conc_sweep ? "mark + sweep" : "mark", (unsigned) before, (unsigned) mask, (unsigned) rc, (unsigned) after,
	     !mask ? (!k9g.pin ? " (core pinning is off in this app -- SHEPHERD_VITA_PIN / SHEPHERD_MT_RENDER: not pinned, kit #3's rule)" : " (SHEPHERD_GC_CORE=0: not pinned)")
		   : rc < 0 ? " (NOT PINNED: the kernel refused the mask)" : "", p0,
	     k9g.prio > 0 ? (prc < 0 ? " (SHEPHERD_GC_PRIO refused)" : " -> SHEPHERD_GC_PRIO") : " (Mono's default)",
	     stack_kib, frc >= 0 ? (unsigned) (fm.size_user / 1024) : 0u);
	if (mask && rc >= 0 && (k9g.core == 1 || k9g.core == 3))
		k6_worker_note (me, "sgen worker");   /* the 4th CPU Core row moves it with the other latency-tolerant workers */
}

/* the Mono thread, before mono_jit_init: MONO_GC_PARAMS (kit #8's exactly with every switch off) and the runtime hooks */
static void k9_gc_params (char *gcp, size_t n, unsigned max_heap_mb, int soft)
{
	char major [64], minor [96], nursery [64];
	snprintf (major, sizeof major, "%s", k9g.conc ? (k9g.conc_sweep ? "major=marksweep-conc,concurrent-sweep" : "major=marksweep-conc,no-concurrent-sweep")
		  : "major=marksweep,no-concurrent-sweep");
	minor [0] = 0;
	if (k9g.split) {
		int o = snprintf (minor, sizeof minor, ",minor=split");
		if (k9g.split_age > 0) o += snprintf (minor + o, sizeof minor - o, ",promotion-age=%d", k9g.split_age);
		if (k9g.split_ratio > 0) snprintf (minor + o, sizeof minor - o, ",alloc-ratio=%d", k9g.split_ratio);
	}
	if (k9g.nursery_scene)
		snprintf (nursery, sizeof nursery, "vita-nursery-min=%uk,vita-nursery-max=%uk", k9g.nursery_min_kib, k9g.nursery_max_kib);
	else if (k9g.nursery_fixed_kib)
		snprintf (nursery, sizeof nursery, "nursery-size=%uk", k9g.nursery_fixed_kib);
	else
		snprintf (nursery, sizeof nursery, "nursery-size=4m");
	if (soft)
		snprintf (gcp, n, "%s%s,%s,max-heap-size=%um,soft-heap-limit=%um", major, minor, nursery, max_heap_mb, max_heap_mb - SHP_SOFT_MARGIN_MB);
	else
		snprintf (gcp, n, "%s%s,%s,max-heap-size=%um", major, minor, nursery, max_heap_mb);
	if (!k9g.on)
		return;
	if (k9g.rt) {
		if (k9g.conc)
			mono_vita_set_gc_thread_callback (k9_gc_worker_started);
		if (k9g.used)
			mono_vita_gc_set_used_size_mode (k9g.used);
		if (k9g.nursery_scene)
			mono_vita_gc_set_nursery_target ((size_t) k9g.nursery_min_kib * 1024);   /* (the boot: the minimum until a scene is known) */
		if (k9g.stats)
			mono_vita_gc_set_stats (1);       /* review F7: the counters (default off in the runtime = kit #8's code paths) */
		if (k9g.cap)
			mono_vita_gc_set_conc_cap (1);    /* review F2: the cap rule (default off in the runtime = stock sgen) */
	}
}

static void k9_gc_scene_load (void);
/* the K9 GC line (after the H4 line) */
static void k9_gc_line (void)
{
	if (!k9g.on) {
		LOG ("K9 GC off (SHEPHERD_K9_GC=0): kit #8's GC settings%s", k9g.rt ? "" : "; the runtime has no kit #9 gc entry points");
		return;
	}
	if (k9g.conc && !k9g.rt)
		LOG ("WARN K9 GC SHEPHERD_GC_CONC=1 with kit #8's runtime (SHEPHERD_K9_RUNTIMES=0): stock sgen's concurrent collector -- its worker is NOT "
		     "pinned and it has no cap rule (an allocation at max-heap-size can fail where kit #8 recovered); SHEPHERD_GC_CONC=0 = kit #8's GC");
	LOG ("K9 GC (%s) lever 32 concurrent %s%s%s | M6 split nursery %s | nursery %s | used-size %s | M8 arena %s | runtime %s | counters %s",
	     !K9_CONC_DEFAULT ? "kit #8's GC by default in this app" : k9g.rt ? "play app: lever 32 + the used-size cache by default"
		     : "play app, but kit #8's runtime: kit #8's GC by default",
	     k9g.conc ? (k9g.conc_sweep ? "mark + sweep" : "mark (serial sweep)") : "off (serial mark + sweep: kit #8)",
	     !k9g.conc ? "" : !k9g.rt ? ", worker not pinned (kit #8's runtime)" : !k9g.pin ? ", worker not pinned (core pinning off in this app)"
		     : k9g.core == 2 ? ", worker on USER_2" : k9g.core == 3 || k9g.core == 1 ? (shepherd_vita_worker_mask () == SHP_CORE3_MASK ? ", worker on CPU3 (the 4th CPU Core row moves it)" : ", worker on USER_2 (CPU3 not open or the 4th CPU Core row Off; the row moves it)") : ", worker not pinned",
	     !k9g.conc ? "" : k9g.cap ? ", the cap rule (at the cap and above soft-heap-limit: serial majors, as kit #8)" : ", stock sgen at the cap (SHEPHERD_GC_CONC_CAP=0 or kit #8's runtime)",
	     k9g.split ? "on" : "off", k9g.nursery_scene ? "per scene (learning cache cache/gc-nursery.tsv)" : k9g.nursery_fixed_kib ? "fixed (SHEPHERD_GC_NURSERY)" : "fixed 4 MiB (kit #8)",
	     k9g.used == 1 ? "cache (identical answers)" : k9g.used == 2 ? "counter (sgen's book, no walk)" : k9g.used == 3 ? "CHECK (walk + compare)" : "walk (kit #8)",
	     k9g.arena_ends == 2 ? "two-ended (the LOS from the high end)" : k9g.arena_ends == 3 ? "two-ended (the major heap from the high end)" : "first fit (kit #8)",
	     k9g.rt ? "kit #9 (k9-gc.patch)" : "kit #8 (no k9-gc.patch: the worker is not pinned, no used-size / nursery / arena modes)",
	     k9g.stats ? "on (pinning, worker / sweep waits, forced majors)" : "off (kit #8's code paths)");
	if (k9g.nursery_scene)
		k9_gc_scene_load ();
}

/* ---------------------------------------------------------------- per-scene nursery sizing: the learning cache
 * The engine reports each scene load (Diag/VitaHost.cs kind 8, Runtime/K9Gc.cs): "load <scene>" when a load starts; the cache's key is the
 * transition "<previous scene>><scene>" (the first one's previous scene is "(boot)"). The window of a scene runs
 * from its load's start to the next scene's load start (its load included); for it the host keeps the frames, the main thread's allocation
 * (the engine's figure at the two notes), the nursery collections and their pause time, the major collections, the GC heap's peak at the
 * collections. When the window ends, the scene's row in cache/gc-nursery.tsv is updated and written (temp + rename, only when it changed);
 * when a scene's load starts, its row's size goes to sgen (it applies at the end of the next nursery collection).
 * The rule (a scene's next size from its last window): allocation per frame x K9_FRAMES_PER_MINOR (a nursery collection at most every 8
 * frames), rounded up to a power of two in [MIN, MAX] -- a heavy scene gets a bigger nursery (fewer nursery collections, and fewer objects
 * that live a few frames promoted into the major heap, so fewer majors); a light one keeps MIN (a smaller nursery would free no memory:
 * the whole MAX is reserved); a window whose GC heap peaked within 24 MiB of the cap keeps MIN. Only when collections happen changes --
 * never what the game computes: the census (vita/notes/kit9-gc.md) found no game behaviour that depends on the GC's timing. */
#define K9_SCENES 48
#define K9_FRAMES_PER_MINOR 8
static struct { char name [48]; unsigned size_kib, visits, frames; unsigned long long alloc; unsigned minors, majors; double minor_ms; unsigned peak_kib; } k9s [K9_SCENES];
static int k9s_n, k9s_loaded, k9s_dirty;
static struct { int on, idx, frame0; long long alloc0; unsigned minors0, majors0; unsigned long long minor_us0; long long peak; char prev [24]; } k9w;
static unsigned k9_target_changes;
static long long k9_heap_peak_bytes;   /* the GC heap's largest HeapSizeBytes at a collection (gc_event_cb) */

#define K9S_PATH DIR_ "/cache/gc-nursery.tsv"
static void k9_gc_scene_load (void)
{
	if (k9s_loaded) return;
	k9s_loaded = 1;
	void *d; unsigned sz;
	if (read_file (K9S_PATH, &d, &sz) != 0) {
		LOG ("K9 GC scene cache: %s not there yet (every scene starts at the minimum and is learned this session)", K9S_PATH);
		return;
	}
	char *p = (char *) d, *e = p + sz;
	while (p < e && k9s_n < K9_SCENES) {
		char *nl = memchr (p, '\n', (size_t) (e - p)); if (!nl) nl = e;
		if (*p != '#' && nl > p) {
			char line [256]; size_t l = (size_t) (nl - p) < sizeof line - 1 ? (size_t) (nl - p) : sizeof line - 1;
			memcpy (line, p, l); line [l] = 0;
			char *tab = strchr (line, '\t');
			if (tab) {
				*tab = 0;
				unsigned size = 0, visits = 0, frames = 0, minors = 0, majors = 0, peak = 0; unsigned long long alloc = 0; double mms = 0;
				if (sscanf (tab + 1, "%u\t%u\t%u\t%llu\t%u\t%lf\t%u\t%u", &size, &visits, &frames, &alloc, &minors, &mms, &majors, &peak) >= 2) {
					snprintf (k9s [k9s_n].name, sizeof k9s [0].name, "%s", line);
					k9s [k9s_n].size_kib = size; k9s [k9s_n].visits = visits; k9s [k9s_n].frames = frames; k9s [k9s_n].alloc = alloc;
					k9s [k9s_n].minors = minors; k9s [k9s_n].minor_ms = mms; k9s [k9s_n].majors = majors; k9s [k9s_n].peak_kib = peak;
					k9s_n++;
				}
			}
		}
		p = nl + 1;
	}
	free (d);
	LOG ("K9 GC scene cache: %d scene(s) learned before (%s)", k9s_n, K9S_PATH);
}

static void k9_gc_scene_save (void)
{
	if (!k9s_dirty) return;
	char b [K9_SCENES * 160 + 256]; int o = 0;
	o += snprintf (b + o, sizeof b - o, "# kit #9 gc per-scene nursery sizes (host.c k9_gc.inc.c): scene\tnext size KiB\tvisits\tframes\tmain-thread alloc B\tnursery GCs\tnursery GC ms\tmajor GCs\tGC heap peak KiB (the last window)\n");
	for (int i = 0; i < k9s_n && o < (int) sizeof b - 160; i++)
		o += snprintf (b + o, sizeof b - o, "%s\t%u\t%u\t%u\t%llu\t%u\t%.1f\t%u\t%u\n", k9s [i].name, k9s [i].size_kib, k9s [i].visits, k9s [i].frames, k9s [i].alloc,
			       k9s [i].minors, k9s [i].minor_ms, k9s [i].majors, k9s [i].peak_kib);
	sceIoMkdir (DIR_ "/cache", 0777);
	SceUID fd = sceIoOpen (K9S_PATH ".tmp", SCE_O_WRONLY | SCE_O_CREAT | SCE_O_TRUNC, 0777);
	int ok = 0;
	if (fd >= 0) {
		ok = sceIoWrite (fd, b, (SceSize) o) == o;
		sceIoClose (fd);
		if (ok) { sceIoRemove (K9S_PATH); ok = sceIoRename (K9S_PATH ".tmp", K9S_PATH) >= 0; }
	}
	if (ok) k9s_dirty = 0;
	else LOG ("WARN K9 GC scene cache: %s not written", K9S_PATH);
}

static unsigned k9_rule (unsigned long long alloc, unsigned frames, unsigned peak_kib)
{
	unsigned want = k9g.nursery_min_kib;
	unsigned long long per = frames ? alloc / frames : alloc;
	unsigned long long need_kib = per * K9_FRAMES_PER_MINOR / 1024;
	while (want < need_kib && want < k9g.nursery_max_kib) want <<= 1;
	if (g_max_heap_mb && peak_kib + 24u * 1024u > g_max_heap_mb * 1024u) want = k9g.nursery_min_kib;
	return want;
}

/* kind 8 from the engine: a scene's load starts (note "load <scene>"); alloc = the main thread's allocation so far. The END checkpoint
 * closes the last window too (note "end", host.c) */
static void k9_gc_scene_note (int frame, const char *note, int64_t alloc_main)
{
	if (!k9g.nursery_scene || !note || (strncmp (note, "load ", 5) && strcmp (note, "end"))) return;
	int end = !strcmp (note, "end");
	/* the key is the TRANSITION "<previous scene>><scene>" (as kit #8's read-order maps): a pass-through scene behaves differently coming
	 * from different places -- the BufferScene before the Main Menu's load allocates ~1.4 MiB/frame, the one after it ~80 KiB/frame; keyed
	 * by its name alone, each visit overwrote the other's size (vlx New Game, sessions A / B) */
	char key [sizeof k9s [0].name];
	snprintf (key, sizeof key, "%s>%s", k9w.prev [0] ? k9w.prev : "(boot)", end ? "" : note + 5);
	const char *scene = key;
	/* close the previous window */
	if (k9w.on && k9w.idx >= 0) {
		int i = k9w.idx;
		unsigned frames = frame > k9w.frame0 ? (unsigned) (frame - k9w.frame0) : 1;
		unsigned long long alloc = alloc_main > k9w.alloc0 ? (unsigned long long) (alloc_main - k9w.alloc0) : 0;
		unsigned peak = (unsigned) (k9w.peak / 1024);
		k9s [i].visits++; k9s [i].frames = frames; k9s [i].alloc = alloc; k9s [i].minors = gcs.count [0] - k9w.minors0;
		k9s [i].majors = gcs.count [1] - k9w.majors0; k9s [i].minor_ms = (double) (gcs.total_us [0] - k9w.minor_us0) / 1000.0; k9s [i].peak_kib = peak;
		unsigned next = k9_rule (alloc, frames, peak);
		LOG ("K9 GC scene '%s' window f%d..f%d: %u frames, main-thread allocation %llu KiB (%llu KiB/frame), %u nursery GCs (%.1f ms), %u major GCs, GC heap peak %u KiB -> next visit's nursery %u KiB (was %u)",
		     k9s [i].name, k9w.frame0, frame, frames, alloc / 1024, alloc / 1024 / frames, k9s [i].minors, k9s [i].minor_ms, k9s [i].majors, peak, next, k9s [i].size_kib);
		if (next != k9s [i].size_kib) k9s [i].size_kib = next;
		k9s_dirty = 1;
		k9_gc_scene_save ();
	}
	k9w.on = 0;
	if (end) return;
	/* open the new one */
	int i = 0;
	while (i < k9s_n && strcmp (k9s [i].name, scene)) i++;
	if (i == k9s_n && k9s_n < K9_SCENES) {
		snprintf (k9s [i].name, sizeof k9s [0].name, "%s", scene);
		k9s [i].size_kib = k9g.nursery_min_kib;
		k9s_n++;
	}
	if (i >= K9_SCENES) { k9w.on = 0; return; }
	size_t cur = 0;
	mono_vita_gc_get_nursery (&cur, NULL, NULL);
	size_t want = (size_t) k9s [i].size_kib * 1024;
	if (want != cur) k9_target_changes++;
	mono_vita_gc_set_nursery_target (want);
	LOG ("K9 GC scene '%s' f%d: nursery %u KiB (%s; now %u KiB, applied at the next nursery GC)", scene, frame, k9s [i].size_kib,
	     k9s [i].visits ? "learned" : "first visit: the minimum", (unsigned) (cur / 1024));
	snprintf (k9w.prev, sizeof k9w.prev, "%s", note + 5);
	k9w.on = 1; k9w.idx = i; k9w.frame0 = frame; k9w.alloc0 = alloc_main; k9w.minors0 = gcs.count [0]; k9w.majors0 = gcs.count [1];
	k9w.minor_us0 = gcs.total_us [0]; k9w.peak = 0;
}

/* gc_event_cb (world stopped, after every collection): the GC heap's peak (for the scene window and the end line) */
static inline void k9_gc_after_collection (long long heap)
{
	if (heap > k9_heap_peak_bytes) k9_heap_peak_bytes = heap;
	if (heap > k9w.peak) k9w.peak = heap;
}

/* ---------------------------------------------------------------- the object-header census (STRETCH, research: count first)
 * Mono's object header on the Vita is two words: the vtable pointer and the sync word (thin lock / hash / inflated monitor). Dropping the
 * sync word (Lilliput-style: lock and hash-taken bits in the vtable word's spare low bits, inflated monitors and stored hashes in a side
 * table) saves 4 bytes per object -- but sgen allocates in 8-byte steps (SGEN_ALLOC_ALIGN; 64-bit fields and LDREXD need it), so an
 * object gains only when its true size minus 4 drops into the next lower 8-byte step: ALIGN8 (size) - ALIGN8 (size - 4). This walk counts
 * it on the real heap after a major collection (the world stopped: the profiler's PRE_START_WORLD event, as mono_gc_walk_heap requires). */
#define K9C_TOP 512
typedef struct {
	unsigned walks, at_major, objects, gain_objects;
	unsigned long long bytes, aligned, saving;
	struct { MonoClass *k; unsigned n; unsigned long long b; } top [K9C_TOP];
} K9Census;
static K9Census *k9c_cur, *k9c_prev;   /* (malloc'd at the first walk: 2 x 8 KiB of tables only in a census run, not in every eboot's bss) */
#define k9c (*k9c_cur)
#define k9c_last (*k9c_prev)
static int k9c_ref_cb (MonoObject *obj, MonoClass *klass, uintptr_t size, uintptr_t num, MonoObject **refs, uintptr_t *offsets, void *data)
{
	(void) refs; (void) offsets; (void) data; (void) num;
	if (!size) return 0;   /* (a repeated call for the same object: its references only) */
	unsigned u = (unsigned) mono_object_get_size (obj);
	unsigned a8 = (u + 7u) & ~7u, b8 = u > 4 ? ((u - 4u + 7u) & ~7u) : 8u;
	k9c.objects++; k9c.bytes += u; k9c.aligned += a8;
	if (a8 > b8) { k9c.saving += a8 - b8; k9c.gain_objects++; }
	unsigned h = (unsigned) (((uintptr_t) klass >> 3) * 2654435761u) % K9C_TOP;
	for (unsigned i = 0; i < K9C_TOP; i++, h = (h + 1) % K9C_TOP) {
		if (k9c.top [h].k == klass) { k9c.top [h].n++; k9c.top [h].b += a8; break; }
		if (!k9c.top [h].k) { k9c.top [h].k = klass; k9c.top [h].n = 1; k9c.top [h].b = a8; break; }
	}
	return 0;
}
/* the 8 most numerous classes of the last walk, named DURING the walk: mono_class_get_name / _namespace enter GC-unsafe mode, which needs a
 * thread attached to the runtime -- the collecting thread is, the host's main thread at the END line is not (a first version named them
 * there: SIGSEGV under vlx) */
static char k9c_top_text [640];
/* gc_event_cb, PRE_START_WORLD of a pause that ran a major collection (the world is stopped, this thread is the collecting one) */
static void k9_gc_census_walk (void)
{
	if (!k9g.census) return;
	if (!k9c_cur) {
		k9c_cur = (K9Census *) calloc (1, sizeof (K9Census));
		k9c_prev = (K9Census *) calloc (1, sizeof (K9Census));
		if (!k9c_cur || !k9c_prev) { k9g.census = 0; return; }
	}
	memset (&k9c, 0, sizeof k9c);
	mono_gc_walk_heap (0, k9c_ref_cb, NULL);
	k9c.walks = k9c_last.walks + 1; k9c.at_major = gcs.count [1] + 1;
	int o = 0;
	for (int r = 0; r < 8; r++) {   /* (a selection pass over the table; the counts are zeroed in k9c only, after the copy below) */
		int best = -1;
		for (int i = 0; i < K9C_TOP; i++)
			if (k9c.top [i].k && k9c.top [i].n && (best < 0 || k9c.top [i].n > k9c.top [best].n)) best = i;
		if (best < 0) break;
		const char *ns = mono_class_get_namespace (k9c.top [best].k), *nm = mono_class_get_name (k9c.top [best].k);
		o += snprintf (k9c_top_text + o, sizeof k9c_top_text - o, "%s %s%s%s %u (%llu KiB)", r ? "," : "", ns && *ns ? ns : "", ns && *ns ? "." : "",
			       nm ? nm : "?", k9c.top [best].n, k9c.top [best].b / 1024);
		k9c.top [best].n = 0;
		if (o > (int) sizeof k9c_top_text - 80) break;
	}
	k9c_last = k9c;
}
static void k9_gc_census_line (void)
{
	if (!k9g.census || !k9c_prev || !k9c_last.walks) return;
	LOG ("K9 GC census (research: smaller object headers, count first; the walk after major #%u, %u walks): %u objects, %llu KiB true size, %llu KiB at 8-byte steps (%.1f B mean); a 4-byte header would save %llu KiB (%.1f %% of the heap; %u objects = %.1f %% gain 8 bytes, the rest gain nothing at sgen's 8-byte alignment) | most numerous:%s",
	     k9c_last.at_major, k9c_last.walks, k9c_last.objects, k9c_last.bytes / 1024, k9c_last.aligned / 1024, k9c_last.objects ? (double) k9c_last.aligned / k9c_last.objects : 0.0,
	     k9c_last.saving / 1024, k9c_last.aligned ? 100.0 * k9c_last.saving / k9c_last.aligned : 0.0, k9c_last.gain_objects,
	     k9c_last.objects ? 100.0 * k9c_last.gain_objects / k9c_last.objects : 0.0, k9c_top_text);
}

/* the K9 GC end line: pauses by kind, the worst pause, the GC heap's peak, the arena, the nursery, the used-size counts; before it the
 * majors in order (review F9: the first K9_MAJSEQ_HEAD and the last K9_MAJSEQ_RING, several lines) and the runtime's waits (review F4) */
static void k9_gc_majors_lines (void)
{
	unsigned n = gcs.majseq_n, keep = n < K9_MAJSEQ_HEAD + K9_MAJSEQ_RING ? n : K9_MAJSEQ_HEAD + K9_MAJSEQ_RING;
	if (!n) {
		LOG ("K9 GC majors in order (ms; s = sgen's own serial major, cs / cf = a concurrent major's start / finishing pause, x = an explicit GC.Collect): none");
		return;
	}
	unsigned lines = (keep + 79) / 80;
	char seq [80 * 12 + 96];
	for (unsigned l = 0, k = 0; l < lines; l++) {
		int o = 0;
		for (unsigned c = 0; c < 80 && k < keep; c++, k++) {
			/* entry k of the kept ones: the head in order, then (when more than fit) the ring's last K9_MAJSEQ_RING in order */
			unsigned idx = k < K9_MAJSEQ_HEAD || n <= K9_MAJSEQ_HEAD + K9_MAJSEQ_RING ? k : n - (keep - k);
			unsigned v = gcs.majseq [idx < K9_MAJSEQ_HEAD ? idx : K9_MAJSEQ_HEAD + (idx - K9_MAJSEQ_HEAD) % K9_MAJSEQ_RING], kd = v >> 30;
			if (k == K9_MAJSEQ_HEAD && n > K9_MAJSEQ_HEAD + K9_MAJSEQ_RING)
				o += snprintf (seq + o, sizeof seq - o, " [... %u not kept ...]", n - K9_MAJSEQ_HEAD - K9_MAJSEQ_RING);
			o += snprintf (seq + o, sizeof seq - o, " %s%.0f", kd == 1 ? "cs" : kd == 2 ? "cf" : kd == 3 ? "x" : "s", (v & 0x3fffffffu) / 1000.0);
		}
		LOG ("K9 GC majors in order (ms; s = sgen's own serial major, cs / cf = a concurrent major's start / finishing pause, x = an explicit GC.Collect; "
		     "%u majors%s; part %u of %u):%s", n, n > keep ? ", the first 64 and the last 192 kept" : "", l + 1, lines, seq);
	}
}

static void k9_gc_end_line (void)
{
	MonoVitaArenaStats as;
	mono_vita_vm_arena_get_stats (&as);
	unsigned worst = gcs.max_us [0] > gcs.max_us [1] ? gcs.max_us [0] : gcs.max_us [1];
	char ends [160] = "", nur [128] = "", used [256] = "", pin [200] = "", waits [800] = "";
	if (k9g.stats && mono_vita_gc_get_pin_stats) {   /* (STRETCH "pinned-object area", count first: plain counters; review F7: only with the counters on) */
		MonoVitaPinStats ps;
		mono_vita_gc_get_pin_stats (&ps);
		snprintf (pin, sizeof pin, " | nursery pinning: %u of %u passes kept objects (%u objects, %llu KiB; at most %u objects / %u KiB in one)",
			  ps.with_pinned, ps.pinnings, ps.objects, (unsigned long long) (ps.bytes / 1024), ps.max_objects, ps.max_bytes / 1024);
	}
	if (k9g.stats && mono_vita_gc_get_wait_stats) {   /* review F1 / F2 / F4: what vlx cannot show -- the device's waits and the forced majors */
		MonoVitaGcWaitStats w;
		mono_vita_gc_get_wait_stats (&w);
		snprintf (waits, sizeof waits, "K9 GC waits (the device's scheduler decides them; vlx cannot model it): %u concurrent finishing pauses waited for sgen's worker "
			  "to stop %.1f ms in all (max %.1f); %u waits for the concurrent sweep %.1f ms (max %.1f) | sgen's own majors run SERIALLY by the concurrent "
			  "collector: %u with the heap above soft-heap-limit (the cap - %u MiB: lever 32 does not act there; a running mark finished by the cap rule included), "
			  "%u by the cap rule at max-heap-size (an allocation that did not fit) | "
			  "explicit full collections %u, %u of them through kit #4's GC.Collect wrapper (M2: it governs only what passes it)",
			  w.finish_waits, w.finish_wait_us / 1000.0, w.finish_wait_max_us / 1000.0, w.sweep_waits, w.sweep_wait_us / 1000.0, w.sweep_wait_max_us / 1000.0,
			  w.soft_forced, (unsigned) SHP_SOFT_MARGIN_MB, w.cap_forced, w.explicit_collections, g_gcgov_full_calls);
	}
	if (k9g.rt && k9g.arena_ends >= 2 && mono_vita_vm_arena_get_ends) {
		unsigned hn = 0; size_t lt = 0, hb = 0;
		mono_vita_vm_arena_get_ends (&hn, &lt, &hb);
		snprintf (ends, sizeof ends, "; two-ended: %u high-end allocations, low end reached %u KiB, high end reached down to %u KiB", hn, (unsigned) (lt / 1024), (unsigned) (hb / 1024));
	}
	if (k9g.rt && mono_vita_gc_get_nursery) {
		size_t sz = 0, mn = 0, mx = 0;
		mono_vita_gc_get_nursery (&sz, &mn, &mx);
		snprintf (nur, sizeof nur, " | nursery %u KiB (range %u..%u KiB)", (unsigned) (sz / 1024), (unsigned) (mn / 1024), (unsigned) (mx / 1024));
		if (k9g.nursery_scene) {
			size_t l = strlen (nur);
			snprintf (nur + l, sizeof nur - l, ", %u per-scene changes, %d scene(s) in the cache", k9_target_changes, k9s_n);
		}
	}
	if (k9g.rt && k9g.used && mono_vita_gc_get_used_size_stats) {
		MonoVitaUsedSizeStats u;
		mono_vita_gc_get_used_size_stats (&u);
		snprintf (used, sizeof used, " | used-size (%s): %u calls, %u walks, %u reused", k9g.used == 1 ? "cache" : k9g.used == 2 ? "counter" : "check", u.calls, u.walks, u.cache_hits);
		if (k9g.used == 3) {
			size_t l = strlen (used);
			snprintf (used + l, sizeof used - l, "; CHECK cache %u of %u reuses differed, counter %u of %u differed (worst %lld B)", u.cache_mismatches, u.cache_checked,
				  u.counter_mismatches, u.counter_checked, (long long) u.worst_counter_diff);
		}
	}
	k9_gc_majors_lines ();
	if (waits [0])
		LOG ("%s", waits);
	k9_gc_census_line ();
	LOG ("K9 GC end: pauses minor %u (max %.1f, total %.1f ms), major %u (max %.1f, total %.1f ms) [serial %u max %.1f | concurrent start %u max %.1f | concurrent finish %u max %.1f | explicit GC.Collect %u max %.1f] | WORST PAUSE %.1f ms | GC heap peak at collections %u KiB | arena peak %u of %u KiB, largest free %u KiB in %u extents%s%s%s | sgen workers %d%s",
	     gcs.count [0], gcs.max_us [0] / 1000.0, gcs.total_us [0] / 1000.0, gcs.count [1], gcs.max_us [1] / 1000.0, gcs.total_us [1] / 1000.0,
	     gcs.kind_n [0], gcs.kind_max [0] / 1000.0, gcs.kind_n [1], gcs.kind_max [1] / 1000.0, gcs.kind_n [2], gcs.kind_max [2] / 1000.0, gcs.kind_n [3], gcs.kind_max [3] / 1000.0,
	     worst / 1000.0, (unsigned) (k9_heap_peak_bytes / 1024), (unsigned) (as.peak / 1024), (unsigned) (as.arena_size / 1024), (unsigned) (as.largest_free / 1024),
	     as.free_extents, ends, nur, used, k9g.workers, pin);
}
