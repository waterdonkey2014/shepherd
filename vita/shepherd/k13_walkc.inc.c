/* SHEPHERD-VITA device kit #13, track walkc (vita/notes/kit13-walkc.md): memory and the walk -- the host's half. Included by host.c after
 * k12_walkb.inc.c. Every switch is an env-file line; SHEPHERD_K13_WALKC=0 = kit #12's behaviour for every row of this track (the engine's
 * rows read the same master: Runtime/K13Unload.cs).
 *
 *   SHEPHERD_K13_IL_CACHED=1|0  THE IL POOL IN VIDEO MEMORY, CACHED (with SHEPHERD_K10_IL_VRAM_MB=<MiB>, which stays opt-in): kit #10's IL pool
 *                               in CDRAM was mapped USER_CDRAM_RW -- uncached: every metadata / IL read of the runtime went to the bus, and
 *                               the owner's kit #12 X2 run measured +10.9 s of boot for +24 MiB of GC heap cap. On (default whenever the
 *                               IL pool is in video memory): the block is USER_CDRAM_L1WBWA_RW, the CPU's L1 caches it (write-back,
 *                               write-allocate). Only the CPU ever touches this block (the GPU never sees it), so there is nothing to keep
 *                               coherent. A kernel that refuses the type gets kit #12's uncached block (a WARN line), then the newlib heap
 *                               as before. 0 = kit #12 (USER_CDRAM_RW). Device-gated: the X2-style env arm decides it (ESTIMATE only here).
 *   THE "MiB SHORT" METER (every app, a line at the end): what the run's GC side needed at its peak (the arena's peak: the GC heap, its card
 *                               table, the interpreter stacks, sgen's slack -- what the shared pool's arena must hold) against the arena room
 *                               the device partitions of 00098 / 00095 leave at this run's newlib (pool - newlib's top peak - the floor's
 *                               reserve): "K13 SHORT". The pools are the VLX numbers at the device partitions (kit #14's eboots: 303 / 289 MiB at kit #6's
 *                               stack reserve, kit #12's and #13's were 286 / 270); SHEPHERD_K13_SHORT_POOLS=<a>,<b> replaces them. DERIVED: newlib at
 *                               the measuring partition is not exactly the device partition's (the game-AOT app's is lower).
 *                               A READING, NOT THE MARGIN (kit #14 integration): it leaves out the GC cap's overhead (the cap stops the GC heap
 *                               before the arena is full), the memory guard's newlib rule and what video memory's overflow puts into newlib, and it
 *                               reads this run's newlib against both apps' pools. Kit #14's walk read 9.1 MiB spare on 00098 and 2.0 on 00095 where
 *                               taking the pool down showed ~4 MiB on both (VLX): the run's END line decides, the line says so.
 * Lines: "K13 WALKC" (the switches in force), "K13 SHORT" (the meter, at the end). */

#ifndef SHP_K13_IL_CACHED_DEFAULT
#define SHP_K13_IL_CACHED_DEFAULT 1
#endif
#ifndef SCE_KERNEL_MEMBLOCK_TYPE_USER_CDRAM_L1WBWA_RW
#define SCE_KERNEL_MEMBLOCK_TYPE_USER_CDRAM_L1WBWA_RW 0x09404060
#endif

static struct {
	int read;          /* the switches were read */
	int on;            /* SHEPHERD_K13_WALKC != 0 */
	int il_cached;     /* SHEPHERD_K13_IL_CACHED in force */
	int il_type_used;  /* 2 = cached CDRAM, 1 = uncached CDRAM (kit #12), 0 = not in video memory */
	unsigned pools [2];
	int pools_env;     /* SHEPHERD_K13_SHORT_POOLS set them: the meter uses them as they are */
} k13w;

static void k13_walkc_read (void)
{
	if (k13w.read)
		return;
	k13w.read = 1;
	const char *m = getenv ("SHEPHERD_K13_WALKC");
	k13w.on = !(m && !strcmp (m, "0"));
	const char *c = getenv ("SHEPHERD_K13_IL_CACHED");
	k13w.il_cached = k13w.on && (c && *c ? atoi (c) == 1 : SHP_K13_IL_CACHED_DEFAULT);
	k13w.pools [0] = 303;   /* kit #14: the eboots' spans moved (vita/notes/kit14-integration.md): 00098 / 00095's pools at kit #6's stack reserve (kit #13: 286 / 270) */
	k13w.pools [1] = 289;
	const char *p = getenv ("SHEPHERD_K13_SHORT_POOLS");
	if (p && *p) {
		char *e;
		unsigned long a = strtoul (p, &e, 10);
		if (*e == ',') {
			unsigned long b = strtoul (e + 1, &e, 10);
			if (a >= 64 && a <= 512 && b >= 64 && b <= 512) {
				k13w.pools [0] = (unsigned) a;
				k13w.pools [1] = (unsigned) b;
				k13w.pools_env = 1;
			}
		}
	}
}

/* main, before the IL pool's block: the memory type of the IL pool in video memory (k10_il_vram_alloc) */
static unsigned k13_il_vram_type (void)
{
	k13_walkc_read ();
	return k13w.il_cached ? (unsigned) SCE_KERNEL_MEMBLOCK_TYPE_USER_CDRAM_L1WBWA_RW : (unsigned) SCE_KERNEL_MEMBLOCK_TYPE_USER_CDRAM_RW;
}

static void k13_il_vram_used (int t) { k13w.il_type_used = t; }

/* THE CACHED POOL'S CROSS-CORE CHECK (main, right after the cached block is mapped, before any IL image is read into it): the IL images are
 * written by one core (the preload hook on the main thread, the IL bundle's reader on CPU2) and read by others (the interpreter on any thread
 * that runs game code). The ARM MPCore keeps the L1 caches coherent for SHAREABLE cacheable memory; if this mapping were not, a line written
 * on one core could sit in its L1 while another reads memory. So: the main thread writes 16 KiB (inside its L1), a thread on CPU1 reads and
 * checks them and writes the next 16 KiB, the main thread checks those -- any difference = the block is given back and kit #12's uncached
 * type is used (a WARN line). SHEPHERD_K13_IL_COHERENCE_FAKE=1 (TEST ONLY, the negative control of the fallback): the check reports a
 * difference. 0 = coherent. */
#define K13_COH_WORDS (16u * 1024u / 4u)
static volatile unsigned *k13_coh_base;
static volatile int k13_coh_bad;
static unsigned k13_coh_pat (unsigned i, unsigned salt) { return (i * 0x9E3779B1u) ^ salt; }
/* step 0 (CPU0): write the first 16 KiB; step 1 (CPU1): check them, write the next 16 KiB; step 2 (CPU0): check those */
static int k13_coh_step (SceSize args, void *argp)
{
	int step = args >= sizeof (int) ? *(int *) argp : 0, bad = 0;
	if (step == 0)
		for (unsigned i = 0; i < K13_COH_WORDS; i++) k13_coh_base [i] = k13_coh_pat (i, 0xA5A5A5A5u);
	else if (step == 1) {
		for (unsigned i = 0; i < K13_COH_WORDS; i++) if (k13_coh_base [i] != k13_coh_pat (i, 0xA5A5A5A5u)) bad++;
		for (unsigned i = 0; i < K13_COH_WORDS; i++) k13_coh_base [K13_COH_WORDS + i] = k13_coh_pat (i, 0x5A5A5A5Au);
	} else
		for (unsigned i = 0; i < K13_COH_WORDS; i++) if (k13_coh_base [K13_COH_WORDS + i] != k13_coh_pat (i, 0x5A5A5A5Au)) bad++;
	k13_coh_bad += bad;
	return 0;
}
static int k13_il_coherence (void *base)
{
#if !defined (SHEPHERD_MAC_CHECK)
	k13_coh_base = (volatile unsigned *) base;
	k13_coh_bad = 0;
	static const int mask [3] = { 0x10000 /* CPU0 */, 0x20000 /* CPU1 */, 0x10000 };
	for (int step = 0; step < 3; step++) {
		SceUID t = sceKernelCreateThread ("shepherd-k13-coh", k13_coh_step, 0x10000100, 0x4000, 0, mask [step], NULL);
		if (t < 0 || sceKernelStartThread (t, sizeof step, &step) < 0) {
			LOG ("WARN K13 WALKC the cached IL pool's cross-core check could not start its thread (0x%08x): not checked -- kit #12's uncached type", (unsigned) t);
			if (t >= 0) sceKernelDeleteThread (t);
			return -1;
		}
		sceKernelWaitThreadEnd (t, NULL, NULL);
		sceKernelDeleteThread (t);
	}
	int bad = k13_coh_bad;
	const char *fake = getenv ("SHEPHERD_K13_IL_COHERENCE_FAKE");
	int faked = fake && !strcmp (fake, "1") && !bad;
	if (faked)
		bad = 1;
	for (unsigned i = 0; i < 2 * K13_COH_WORDS; i++)
		k13_coh_base [i] = 0;
	if (bad)
		LOG ("WARN K13 WALKC the cached IL pool FAILED its cross-core check (%d of %u words differed%s): kit #12's uncached type instead", bad, 2 * K13_COH_WORDS,
		     faked ? "; SHEPHERD_K13_IL_COHERENCE_FAKE=1, a test" : "");
	else
		LOG ("K13 WALKC the cached IL pool passed its cross-core check (16 KiB written on CPU0, read on CPU1, 16 KiB written on CPU1, read on CPU0: equal)");
	return bad ? -1 : 0;
#else
	(void) base; return 0;
#endif
}

/* main, after the H2 lines: the switches in force, once */
static void k13_walkc_line (void)
{
	k13_walkc_read ();
	LOG ("K13 WALKC (kit #13 walkc: memory and the walk; SHEPHERD_K13_WALKC=0 = kit #12): the IL pool in video memory %s | the asset release "
	     "(Unity's UnloadUnusedAssets) is the engine's row: its [k13] lines",
	     !k13w.on ? "kit #12's (SHEPHERD_K13_WALKC=0)" :
	     k13w.il_type_used == 2 ? "USER_CDRAM_L1WBWA_RW: cached by the CPU's L1 (SHEPHERD_K13_IL_CACHED=0: kit #12's uncached CDRAM)" :
	     k13w.il_type_used == 1 ? (k13w.il_cached ? "USER_CDRAM_RW: the cached type was REFUSED, kit #12's uncached block instead" : "USER_CDRAM_RW, uncached (SHEPHERD_K13_IL_CACHED=0: kit #12)") :
	     k13w.il_cached ? "not asked for (SHEPHERD_K10_IL_VRAM_MB unset); when asked for, cached (USER_CDRAM_L1WBWA_RW)" : "not asked for (SHEPHERD_K10_IL_VRAM_MB unset)");
}

/* main, at the end: the "MiB short" meter */
static void k13_short_line (void)
{
	k13_walkc_read ();   /* (a log line only: printed whatever SHEPHERD_K13_WALKC says, so the OFF arm of an A/B carries it too) */
	MemSnap m;
	mem_snap (&m, 0);
	if (!m.arena_ok)
		return;
	unsigned need_k = (unsigned) (m.arena.peak / 1024);
	unsigned nl_k = k10_pool_live ? (unsigned) (k10_nl_top_peak / 1024) : m.newlib_peak_k;
	unsigned floor_k = nl_k + (k10_pool_live ? k10m.reserve_mb * 1024u : 0u);
	char out [2] [96];
	unsigned pool [2];
	for (int i = 0; i < 2; i++) {
		pool [i] = k13w.pools [i] + (k13w.pools_env ? 0 : (unsigned) k14_pool_more ());   /* (kit #14 newlib: the default pools follow the stack reserve in force) */
		unsigned pool_k = pool [i] * 1024u, room_k = pool_k > floor_k ? pool_k - floor_k : 0;
		int short_k = (int) need_k - (int) room_k;
		snprintf (out [i], sizeof out [i], "room %.1f MiB -> %s %.1f MiB", room_k / 1024.0, short_k > 0 ? "SHORT" : "spare", (short_k > 0 ? short_k : -short_k) / 1024.0);
	}
	LOG ("K13 SHORT (DERIVED; kit #13 walkc's meter; a reading, not the margin: the GC cap's overhead, the memory guard and video memory's overflow are not in it, "
	     "the END line decides): the GC side needed %.1f MiB at its peak (the arena's peak), newlib's top peak %.1f MiB (+%u MiB floor reserve)%s | "
	     "at 00098's device pool (%u MiB): %s | at 00095's (%u MiB): %s (both at this run's newlib; SHEPHERD_K13_SHORT_POOLS=<a>,<b> sets the pools)",
	     need_k / 1024.0, nl_k / 1024.0, k10_pool_live ? k10m.reserve_mb : 0u, k10_pool_live ? "" : " (not the shared pool: newlib's peak)",
	     pool [0], out [0], pool [1], out [1]);
}
