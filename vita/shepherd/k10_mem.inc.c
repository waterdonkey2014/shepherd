/* SHEPHERD-VITA device kit #10, track memory (vita/notes/kit10-memory.md): the memory track's host side. Included by host.c after
 * k9_gc.inc.c (newlib's heap and the shared pool's sbrk side: k10_newlib.inc.c, included earlier). Every switch is an env-file line;
 * SHEPHERD_K10_MEM=0 turns every one of them off (= kit #9's host, whatever the other lines say).
 *
 *   SHEPHERD_K10_POOL=1|0          THE SHARED POOL (opt-in: off by default everywhere since the review -- R1/R2, host.c DEFAULTS; a device run
 *                                  decides) (k10_newlib.inc.c): newlib's heap and the Mono arena in one block, growing from opposite ends
 *                                  (needs vita/patches/k10-memory.patch in the runtime: build-runtime-k9.sh SHEPHERD_K10_PATCHES). Read before
 *                                  main (newlib's heap is made there). The IL pool joins it (IL images go to newlib's side).
 *   SHEPHERD_K10_POOL_RESERVE_MB=n newlib's guaranteed room above its top (default 8, 4..64): the floor the arena never crosses. The GC heap's cap
 *                                  follows the floor (the arena's room above it - card table - interpreter stacks - slack, kit #9's formula).
 *   SHEPHERD_K10_EVAC=<pct>        sgen's major-block EVACUATION threshold (MONO_GC_PARAMS evacuation-threshold=; sgen's default 66): a block
 *                                  size class whose blocks are less than <pct> % full is compacted by the next major collection (its objects
 *                                  copied into fewer blocks, the emptied blocks freed). Only where objects live changes.
 *   SHEPHERD_K10_SIZES=a,b,...     extra slot sizes for the major heap's blocks (the size-class audit; up to 32, multiples of 8 below 8000),
 *                                  joined to sgen's 26 at its init (k10-memory.patch). Only where objects live changes. Default: the play
 *                                  apps' twelve (host.c DEFAULTS: the walk census's pick), none elsewhere; =0 = sgen's 26 (kit #9).
 *   SHEPHERD_K10_LADDER=1|0        THE MEMORY-PRESSURE LADDER (default ON in the play apps) (Runtime/MemLadder.cs is the engine's half): every heartbeat the host turns the
 *                                  GC heap's headroom (its cap - the heap the last major collection left: review fix R6) and newlib's (its cap -
 *                                  its sbrk'd size) into a level
 *                                  0..5 (SHEPHERD_K10_LADDER_MB=24,18,12,8,6 for the GC heap, SHEPHERD_K10_LADDER_NL_MB=12,10,8,7,6 for newlib:
 *                                  a level as soon as either side falls under its threshold; down again only 4 MiB above it); the engine
 *                                  polls it and gives optional memory back in a fixed order -- 1 preloads, 2 in-RAM learning caches, 3 image
 *                                  caches, 4 the warm-return cache, 5 extra VRAM residency -- each step once per rise, long before the memory
 *                                  guard (which acts at the cap / at newlib's last 4 MiB). Test: SHEPHERD_K10_LADDER_FORCE=<level>@<frame>[,...].
 *   SHEPHERD_K10_MEMLOG=<frames>   (test only) MemReport's memory timeline: a sample every <frames> frames (at the heartbeats) and after every
 *                                  major collection -- arena used / largest free block / free extents, the GC heap (sgen's, and at the last
 *                                  collection with its fragmented part), newlib, free user memory, the pool's ends -- to <logs>/k10-mem.tsv,
 *                                  and the HOLES summary on the K10 MEM end line (the smallest largest-free block, the most extents).
 *   SHEPHERD_K10_CENSUS=1          (test only; slows every major) the SIZE-CLASS CENSUS: a heap walk after each major collection (the world
 *                                  stopped) -- every object's true size, the slot sgen gives it, the bytes lost to the slot's rounding, per
 *                                  8-byte size and per class -> <logs>/k10-sizes.tsv + k10-classes.tsv (the walk with the largest heap) and
 *                                  the K10 SIZES end line (the waste, the engine's own types first, the extra slot sizes that would save most).
 * Lines: "K10 MEM" (the switches, at start), "K10 POOL" (the pool's set-up), "K10 LADDER" (each level change), "K10 MEM end" / "K10 SIZES"
 * (at the end). */

#ifndef SHP_K10_LADDER_DEFAULT
#define SHP_K10_LADDER_DEFAULT 0
#endif
#define K10_SAMPLES 8192
#define K10C_SLOTS 8192
#define K10C_BINS 1001                   /* 8-byte bins: 8 .. 8000 B; bin 0 unused */

static struct {
	int on, pool, census, evac, nsizes, ladder;
	unsigned il_vram_mb, il_main_kit9_mb;   /* SHEPHERD_K10_IL_VRAM_MB (STRETCH: cold CPU data in free video memory) */
	int sizes [32];
	unsigned memlog, reserve_mb, il_kit9_mb, extra_mb;
	unsigned lad_gc [5], lad_nl [5];
	int force_n, force_lvl [8], force_at [8];
	int runtime_up;
	const char *pool_why;
	int fatal;              /* review fix R5: newlib has only its 2 MiB boot block (no pool, no second block): main ends with FAIL (fail_and_wait) */
	unsigned cap_ceiling_mb;   /* review fix R7: the env file's own MONO_GC_PARAMS max-heap-size, honoured in the pool as a ceiling (0: none) */
} k10m;

static unsigned k10_env_u (const char *k, unsigned lo, unsigned hi, unsigned def)
{
	const char *e = getenv (k);
	if (!e || !*e)
		return def;
	unsigned long v = strtoul (e, NULL, 10);
	return v < lo || v > hi ? def : (unsigned) v;
}
static int k10_env_list_def (const char *k, unsigned *out, int max, int lo, int hi, const char *def)
{
	const char *e = getenv (k);
	if ((!e || !*e) && def)
		e = def;
	int n = 0;
	while (e && *e && n < max) {
		char *end;
		long v = strtol (e, &end, 10);
		if (end == e)
			break;
		if (v >= lo && v <= hi)
			out [n++] = (unsigned) v;
		e = *end == ',' ? end + 1 : end;
		if (*end != ',')
			break;
	}
	return n;
}

static int k10_env_list (const char *k, unsigned *out, int max, int lo, int hi) { return k10_env_list_def (k, out, max, lo, hi, NULL); }

#ifndef SHP_K10_SIZES_DEFAULT
#define SHP_K10_SIZES_DEFAULT ""   /* (the play apps' list: host.c DEFAULTS) */
#endif
/* main, right after the env file (before the partition) */
static void k10_mem_config (void)
{
	const char *m = getenv ("SHEPHERD_K10_MEM");
	k10m.on = !(m && !strcmp (m, "0"));
	k10m.pool = k10m.on && k10_pool_req;
	{
		/* review fix R4: the pool was decided before main (k10_env_premain); the env file as main applied it must agree. A pool the env file
		 * turns off (SHEPHERD_K10_POOL=0 / SHEPHERD_K10_MEM=0) is not used (newlib gets its kit #9 share in a second block: the partition is
		 * kit #9's); a pool asked for after newlib's whole heap was made cannot be used (kit #9 as it is). */
		const char *pe = getenv ("SHEPHERD_K10_POOL");
		int want = k10m.on && (pe ? !strcmp (pe, "1") : SHP_K10_POOL_DEFAULT);
		if (k10_pool_req && !want) {
			k10m.pool = 0;
			k10m.pool_why = k10m.on ? "the env file says SHEPHERD_K10_POOL is not 1, but the read before main saw it on" : "SHEPHERD_K10_MEM=0";
			LOG ("WARN K10 MEM the shared pool was set up before main but the env file turns it off (%s): newlib gets its kit #9 share in a second "
			     "block, the partition is kit #9's", k10m.pool_why);
		} else if (!k10_pool_req && want)
			LOG ("WARN K10 MEM the env file asks for the shared pool but the read before main did not see it: newlib's whole kit #9 heap exists "
			     "already -- kit #9's split");
	}
	k10m.reserve_mb = k10_env_u ("SHEPHERD_K10_POOL_RESERVE_MB", 4, 64, 8);
	k10m.memlog = k10m.on ? k10_env_u ("SHEPHERD_K10_MEMLOG", 1, 1000000, 0) : 0;
	k10m.census = k10m.on && k10_env_u ("SHEPHERD_K10_CENSUS", 0, 1, 0) == 1;
	k10m.evac = k10m.on ? (int) k10_env_u ("SHEPHERD_K10_EVAC", 1, 100, 0) : 0;
	unsigned sz [32];
	/* SHEPHERD_K10_SIZES=0 = sgen's own 26 sizes (kit #9); unset = the app's default list (host.c DEFAULTS: the play apps' twelve, else none) */
	k10m.nsizes = k10m.on ? k10_env_list_def ("SHEPHERD_K10_SIZES", sz, 32, 16, 7999, SHP_K10_SIZES_DEFAULT) : 0;
	for (int i = 0; i < k10m.nsizes; i++)
		k10m.sizes [i] = (int) (sz [i] & ~7u);
	k10m.ladder = k10m.on && k10_env_u ("SHEPHERD_K10_LADDER", 0, 1, SHP_K10_LADDER_DEFAULT) == 1;
#ifdef SHEPHERD_GL
	k10m.il_vram_mb = k10m.on ? k10_env_u ("SHEPHERD_K10_IL_VRAM_MB", 1, 64, 0) : 0;
#endif
	static const unsigned dg [5] = { 24, 18, 12, 8, 6 }, dn [5] = { 12, 10, 8, 7, 6 };
	if (k10_env_list ("SHEPHERD_K10_LADDER_MB", k10m.lad_gc, 5, 1, 256) != 5)
		memcpy (k10m.lad_gc, dg, sizeof dg);
	if (k10_env_list ("SHEPHERD_K10_LADDER_NL_MB", k10m.lad_nl, 5, 1, 256) != 5)
		memcpy (k10m.lad_nl, dn, sizeof dn);
	const char *f = k10m.ladder ? getenv ("SHEPHERD_K10_LADDER_FORCE") : NULL;
	while (f && *f && k10m.force_n < 8) {   /* <level>@<frame>[,...] */
		char *end;
		long lv = strtol (f, &end, 10);
		if (*end != '@')
			break;
		long at = strtol (end + 1, &end, 10);
		if (lv >= 0 && lv <= 5 && at >= 0) {
			k10m.force_lvl [k10m.force_n] = (int) lv;
			k10m.force_at [k10m.force_n++] = (int) at;
		}
		if (*end != ',')
			break;
		f = end + 1;
	}
	char sl [200] = "", ev [40] = "sgen's (66 %)";
	for (int i = 0, o = 0; i < k10m.nsizes && o < (int) sizeof sl - 8; i++)
		o += snprintf (sl + o, sizeof sl - o, "%s%d", i ? "," : "", k10m.sizes [i]);
	if (k10m.evac)
		snprintf (ev, sizeof ev, "%d %%", k10m.evac);
	LOG ("K10 MEM (kit #10 memory track; SHEPHERD_K10_MEM=0 = kit #9): shared pool %s (reserve %u MiB) | evacuation threshold %s | extra size classes %s | "
	     "pressure ladder %s (GC heap headroom %u/%u/%u/%u/%u MiB, newlib %u/%u/%u/%u/%u MiB%s) | memory timeline %s | size-class census %s",
	     !k10m.on ? "off (SHEPHERD_K10_MEM=0)" : k10m.pool ? "ON (SHEPHERD_K10_POOL=1, set up at the partition)" : "off (kit #9: the 116 MiB newlib heap, the IL pool, the arena apart)",
	     k10m.reserve_mb, ev, k10m.nsizes ? sl : "none (sgen's 26)",
	     k10m.ladder ? "ON" : "off", k10m.lad_gc [0], k10m.lad_gc [1], k10m.lad_gc [2], k10m.lad_gc [3], k10m.lad_gc [4],
	     k10m.lad_nl [0], k10m.lad_nl [1], k10m.lad_nl [2], k10m.lad_nl [3], k10m.lad_nl [4], k10m.force_n ? "; FORCED levels (test)" : "",
	     k10m.memlog ? "ON (test)" : "off", k10m.census ? "ON (test: slows every major)" : "off");
	if (k10m.il_vram_mb)
		LOG ("K10 MEM IL images in VIDEO memory first (SHEPHERD_K10_IL_VRAM_MB=%u; STRETCH 'cold CPU data in free VRAM'): a %u MiB CDRAM block (the CPU reads it at "
		     "~1000 MiB/s, a random miss 333 ns vs 255 in main RAM: kit #7's probe) takes the IL pool's place before vitaGL sizes its video memory, so main "
		     "RAM's IL pool (%u MiB) goes to the arena", k10m.il_vram_mb, k10m.il_vram_mb, g_il_pool_mb);
}

/* ---------------------------------------------------------------- the shared pool: the partition and the GC heap's limits */
#if !defined (SHEPHERD_MAC_CHECK)
static int k10_pool_api (void)
{
	return mono_vita_vm_arena_pool_take && mono_vita_vm_arena_pool_set_floor && mono_vita_vm_arena_pool_get && mono_vita_vm_arena_pool_base &&
	       mono_vita_gc_set_heap_limits && mono_vita_vm_arena_set_policy;
}
#endif

/* main, before the partition's numbers: in pool mode the IL pool joins the pool (its memory becomes newlib's side: IL images are read with
 * malloc) and the partition adds newlib's share to the arena's (returned: MiB to add to the sanity cap / SHEPHERD_ARENA_MB); a pool that
 * cannot be used (no patched runtime) gives newlib its kit #9 share now (a second block) and the partition is kit #9's */
static unsigned k10_partition_prepare (void)
{
#if !defined (SHEPHERD_MAC_CHECK)
	if (k10m.il_vram_mb) {   /* the IL pool moves to video memory: main RAM's share of it goes to the arena (or into the shared pool's newlib side) */
		k10m.il_main_kit9_mb = g_il_pool_mb;
		g_il_pool_mb = 0;
	}
	if (!k10_pool_req)
		return 0;
	if (!k10m.pool || !k10_pool_api ()) {
		if (!k10m.pool_why)
			k10m.pool_why = k10m.on ? "the runtime has no shared-pool entry points (vita/patches/k10-memory.patch)" : "SHEPHERD_K10_MEM=0";
		k10m.pool = 0;
		int fb = k10_newlib_fallback ();
		k10m.fatal = fb != 0;   /* review fix R5: Mono's init needs ~6 MiB of newlib -- main stops with the FAIL screen instead of a crash */
		LOG ("%s K10 POOL not used: %s -- newlib %s", fb ? "FAIL" : "WARN", k10m.pool_why,
		     fb ? "has only its boot block (a second newlib block could not be placed above it): set SHEPHERD_K10_POOL=0" : "gets its kit #9 share in a second block (the partition is kit #9's)");
		return 0;
	}
	k10m.il_kit9_mb = k10m.il_vram_mb ? k10m.il_main_kit9_mb : g_il_pool_mb;
	g_il_pool_mb = 0;
	k10m.extra_mb = _newlib_heap_size_user / MiB - SHP_K10_BOOT_KB / 1024 + k10m.il_kit9_mb;
	return k10m.extra_mb;
#else
	return 0;
#endif
}

/* main, where the IL pool is allocated: the IL pool from video memory (SHEPHERD_K10_IL_VRAM_MB) -- 0 when allocated */
static int k10_il_vram_alloc (char **pool, unsigned *size)
{
#if !defined (SHEPHERD_MAC_CHECK) && defined (SHEPHERD_GL)
	if (!k10m.il_vram_mb)
		return -1;
	unsigned bytes = (k10m.il_vram_mb * MiB + 256u * 1024u - 1) & ~(256u * 1024u - 1);
	SceUID b = sceKernelAllocMemBlock ("shepherd-il-pool-vram", SCE_KERNEL_MEMBLOCK_TYPE_USER_CDRAM_RW, bytes, NULL);
	void *base = NULL;
	if (b < 0 || sceKernelGetMemBlockBase (b, &base) < 0) {
		LOG ("WARN K10 MEM the %u MiB IL pool in video memory could not be allocated (0x%08x): the IL images go to the newlib heap", k10m.il_vram_mb, (unsigned) b);
		return -1;
	}
	*pool = (char *) base;
	*size = bytes;
	LOG ("H2 IL pool: %u MiB USER_CDRAM_RW at %p (kit #10: video memory -- IL images read here first, then the newlib heap; SHEPHERD_K10_IL_VRAM_MB unset = kit #9's main-RAM pool)",
	     k10m.il_vram_mb, base);
	return 0;
#else
	(void) pool; (void) size; return -1;
#endif
}

/* the GC heap's cap in pool mode: the arena's room above the floor - card table - interpreter stacks - slack (kit #9's formula) */
static unsigned k10_pool_cap_mb (void)
{
#if !defined (SHEPHERD_MAC_CHECK)
	size_t room = k10_pool_bytes > k10_nl_floor ? k10_pool_bytes - k10_nl_floor : 0;
	unsigned over = SHP_CARD_TABLE_MB + SHP_INTERP_STACKS_MB + SHP_ARENA_SLACK_MB;
	unsigned r = (unsigned) (room / MiB);
	return r > over + 16 ? r - over : 16;
#else
	return g_max_heap_mb;
#endif
}
static unsigned k10_cap_min_mb = ~0u, k10_cap_max_mb, k10_cap_moves, k10_cap_drops;
/* k10_newlib.inc.c, after a floor move (the sbrk mutex held: plain stores only); and once the runtime is up.
 * Review fix R3: a cap that FALLS (newlib took pages) can land below what sgen has allocated -- the next block request is then refused before
 * any major collection has run (a minor's promotions come first), and the play apps' memory guard would end the session on that first
 * refusal although a major would make room (the patch's "the next allocation collects first" holds only for sgen's own retry paths). So a
 * drop marks k10_cap_drop_pending: until the next COMPLETED major (k10_mem_after_major), heap_limit_cb lets such refusals through (counted;
 * sgen pins the refused survivors and runs its overflow major, as it does without the guard); a refusal after that major is the guard's
 * case again, as at kit #9's fixed cap. An OutOfMemoryException still ends the session (exception_throw_cb).
 * Review fix R7: an env-file MONO_GC_PARAMS max-heap-size (a test override) is a ceiling here, not overwritten. */
static void k10_pool_limits (void)
{
#if !defined (SHEPHERD_MAC_CHECK)
	if (!k10_pool_live || !k10m.runtime_up)
		return;
	unsigned cap = k10_pool_cap_mb ();
	if (k10m.cap_ceiling_mb && cap > k10m.cap_ceiling_mb)
		cap = k10m.cap_ceiling_mb;
	size_t soft = cap >= 3 * SHP_SOFT_MARGIN_MB ? (size_t) (cap - SHP_SOFT_MARGIN_MB) * MiB : 0;
	if (cap < g_max_heap_mb) {
		k10_cap_drop_pending = 1;
		k10_cap_drops++;
	}
	mono_vita_gc_set_heap_limits ((size_t) cap * MiB, soft);
	g_max_heap_mb = cap;
	k10_cap_moves++;
	if (cap < k10_cap_min_mb) k10_cap_min_mb = cap;
	if (cap > k10_cap_max_mb) k10_cap_max_mb = cap;
#endif
}

/* main: the pool block exists at base (pool_bytes). Before mono_vita_vm_arena_init: is it above newlib's boot block? (newlib's break must
 * grow upward) -- 0 yes */
static int k10_pool_order_ok (void *base)
{
#if !defined (SHEPHERD_MAC_CHECK)
	return (char *) base >= k10_heap_end ? 0 : -1;
#else
	(void) base; return -1;
#endif
}
/* main, after mono_vita_vm_arena_init on the pool block: policy 3 and newlib moves there */
static int k10_pool_start (size_t pool_bytes)
{
#if !defined (SHEPHERD_MAC_CHECK)
	if (k9g.arena_ends >= 2)   /* review fix R7: say so -- the pool needs policy 3, which replaces M8's two-ended arena */
		LOG ("WARN K10 POOL SHEPHERD_ARENA_ENDS=%s is not used in the shared pool: every arena request goes top-down above newlib's floor (policy 3)",
		     k9g.arena_ends == 3 ? "2m" : "2");
	mono_vita_vm_arena_set_policy (MONO_VITA_ARENA_POOL, 0);
	MonoVitaArenaStats st;
	mono_vita_vm_arena_get_stats (&st);   /* (the arena's allocatable bytes: the block minus its bookkeeping pages) */
	(void) pool_bytes;
	if (k10_pool_activate (st.arena_size, (size_t) k10m.reserve_mb * MiB) != 0)
		return -1;
	LOG ("K10 POOL newlib + IL + the Mono arena in one block of %u KiB (%u KiB allocatable): newlib's boot block %u KiB below it (its break moves up "
	     "%u KiB to the block's low end; mallocr counts that gap as sbrk'd: subtracted in every MEM line), newlib from the low end with %u MiB of "
	     "guaranteed room (the floor), the arena top-down above the floor; the GC heap's cap follows the floor (now %u MiB); kit #9's split: newlib "
	     "%u MiB + IL pool %u MiB + arena %u MiB",
	     (unsigned) (pool_bytes / 1024), (unsigned) (st.arena_size / 1024), SHP_K10_BOOT_KB, (unsigned) (k10_nl_gap / 1024), k10m.reserve_mb,
	     k10_pool_cap_mb (), _newlib_heap_size_user / MiB, k10m.il_kit9_mb, (unsigned) (pool_bytes / MiB) - k10m.extra_mb);
	return 0;
#else
	(void) pool_bytes; return -1;
#endif
}

#ifdef SHEPHERD_GL
/* gl_start, right after vglInit (review fix R1): with newlib split over two blocks (the pool, or the fallback) vitaGL registered only the
 * block of its malloc(1) -- the boot block. The GL eboots' k10_vglmem.c GPU-maps newlib's other block (vitaGL hands newlib memory to the GPU
 * when its own pools are full) and reports what vitaGL registered; its wrappers route vitaGL's frees / reallocs of newlib chunks to newlib.
 * A weak reference: VLX links the recording stand-in (no vitaGL, no k10_vglmem.o). Outside the pool: nothing (kit #9). */
#if !defined (SHEPHERD_MAC_CHECK)
int shepherd_k10_vgl_after_init (const void *pool_block, const void *fb_block, char *out, unsigned n) __attribute__((weak));
#endif
static void k10_gl_after_init (void)
{
#if !defined (SHEPHERD_MAC_CHECK)
	if (!k10_pool_req)
		return;
	const void *pb, *fb;
	k10_newlib_extra_blocks (&pb, &fb);
	if (!shepherd_k10_vgl_after_init) {
		LOG ("K10 POOL vitaGL: not in this link (VLX's recording stand-in has no vitaGL pools) -- a device eboot GPU-maps newlib's %s and hands "
		     "vitaGL's frees of newlib chunks to newlib (k10_vglmem.c)", pb ? "pool block" : fb ? "second block" : "(no other) block");
		return;
	}
	char d [480];
	d [0] = 0;
	if (shepherd_k10_vgl_after_init (pb, fb, d, sizeof d))
		LOG ("K10 POOL vitaGL: %s", d);
	else
		LOG ("WARN K10 POOL vitaGL: %s", d);
#endif
}
#endif

/* the pool's ends for the MEM lines (empty outside pool mode) */
static const char *k10_pool_note (void)
{
	static char b [200];
	b [0] = 0;
#if !defined (SHEPHERD_MAC_CHECK)
	if (!k10_pool_live)
		return b;
	size_t tk = 0, fl = 0, lu = 0;
	mono_vita_vm_arena_pool_get (&tk, &fl, &lu);
	snprintf (b, sizeof b, " | pool: newlib top %u KiB (peak %u), floor %u KiB, arena's lowest page %u KiB, gap %u KiB, GC cap %u MiB",
		  (unsigned) (k10_nl_top / 1024), (unsigned) (k10_nl_top_peak / 1024), (unsigned) (fl / 1024), (unsigned) (lu / 1024),
		  (unsigned) ((lu > fl ? lu - fl : 0) / 1024), g_max_heap_mb);
#endif
	return b;
}

/* mono_thread_body: MONO_GC_PARAMS' kit #10 parts (after k9_gc_params) and the cap in pool mode */
static void k10_gc_params (char *gcp, size_t n)
{
	if (k10_pool_live)
		g_max_heap_mb = k10_pool_cap_mb ();   /* (the cap k9_gc_params writes below: the floor's, now) */
	(void) gcp; (void) n;
}
static void k10_gc_params_tail (char *gcp, size_t n)
{
	size_t l = strlen (gcp);
	if (k10m.evac && l + 32 < n)
		snprintf (gcp + l, n - l, ",evacuation-threshold=%d", k10m.evac);
}
/* mono_thread_body, after the H4 line (review fix R7): in the pool, an env-file MONO_GC_PARAMS that setenv (..., 0) kept in force -- its
 * max-heap-size (g_max_heap_mb as H4 parsed it) becomes the ceiling of the floor's cap instead of being overwritten without a word */
static void k10_gc_params_in_force (const char *ours)
{
#if !defined (SHEPHERD_MAC_CHECK)
	const char *now = getenv ("MONO_GC_PARAMS");
	if (!k10_pool_live || !now || !strcmp (now, ours))
		return;
	k10m.cap_ceiling_mb = g_max_heap_mb;
	LOG ("WARN K10 POOL the env file's own MONO_GC_PARAMS is in force: %s", k10m.cap_ceiling_mb ?
	     "its max-heap-size is the ceiling of the GC heap's cap (the floor's cap, when lower, wins)" :
	     "it has no max-heap-size -- the shared pool still sets the floor's cap once the runtime is up (else sgen could fill newlib's room)");
#else
	(void) ours;
#endif
}
/* mono_thread_body, before the runtime's init: the extra slot sizes */
static void k10_before_runtime (void)
{
#if !defined (SHEPHERD_MAC_CHECK)
	if (k10m.nsizes) {
		if (mono_vita_gc_set_size_classes)
			mono_vita_gc_set_size_classes (k10m.sizes, k10m.nsizes);
		else
			LOG ("WARN K10 SIZES: the runtime has no size-class entry point (vita/patches/k10-memory.patch): sgen's 26 sizes stay");
	}
#endif
}
/* mono_thread_body, right after mono_jit_init: the pool's live GC limits; the slot-size table in force */
static void k10_after_runtime (void)
{
	k10m.runtime_up = 1;
	k10_pool_limits ();
#if !defined (SHEPHERD_MAC_CHECK)
	if (k10m.nsizes && mono_vita_gc_size_classes) {
		int t [64], n = mono_vita_gc_size_classes (t, 64);
		char s [400];
		int o = 0;
		for (int i = 0; i < n && i < 64 && o < (int) sizeof s - 8; i++)
			o += snprintf (s + o, sizeof s - o, "%s%d", i ? " " : "", t [i]);
		LOG ("K10 SIZES sgen's slot sizes in force (%d): %s", n, s);
	}
	if (k10_pool_live)
		LOG ("K10 POOL the runtime is up: GC heap cap %u MiB (soft %u) = the arena's room above the floor (%u KiB) - %u MiB card table, interpreter "
		     "stacks, slack", g_max_heap_mb, g_max_heap_mb > SHP_SOFT_MARGIN_MB ? g_max_heap_mb - SHP_SOFT_MARGIN_MB : 0, (unsigned) (k10_nl_floor / 1024),
		     SHP_CARD_TABLE_MB + SHP_INTERP_STACKS_MB + SHP_ARENA_SLACK_MB);
#endif
}

/* ---------------------------------------------------------------- MemReport: the memory timeline (test only) */
typedef struct {
	int frame;
	unsigned char kind;   /* 'h' heartbeat, 'm' after a major collection */
	unsigned arena_used_k, arena_largest_k, extents, gc_heap_k, gc_at_k, gc_frag_k, nl_used_k, nl_sbrk_k, free_user_k, cap_mb, floor_k, lowest_k;
} K10Sample;
static K10Sample *k10_samples;
static unsigned k10_ns, k10_last_frame_sampled = ~0u;
static struct { unsigned min_largest_k, min_largest_frame, max_extents, max_frag_k, max_arena_k, samples; } k10h = { ~0u, 0, 0, 0, 0, 0 };
static volatile int k10_frame_now;

static void k10_sample (int kind, int nonblocking)
{
	if (!k10m.memlog)
		return;
	if (!k10_samples) {
		k10_samples = (K10Sample *) calloc (K10_SAMPLES, sizeof (K10Sample));
		if (!k10_samples) { k10m.memlog = 0; return; }
	}
	if (k10_ns >= K10_SAMPLES)
		return;
	MonoVitaArenaStats as;
	if (nonblocking ? mono_vita_vm_arena_try_get_stats (&as) != 0 : (mono_vita_vm_arena_get_stats (&as), 0))
		return;
	K10Sample *s = &k10_samples [k10_ns++];
	s->frame = k10_frame_now;
	s->kind = (unsigned char) kind;
	s->arena_used_k = (unsigned) (as.used / 1024);
	s->arena_largest_k = (unsigned) (as.largest_free / 1024);
	s->extents = as.free_extents;
	s->gc_heap_k = g_runtime_up ? (unsigned) (mono_gc_get_heap_size () / 1024) : 0;
#if !defined (SHEPHERD_MAC_CHECK)
	if (g_runtime_up) {
		int64_t a, b, c, d, h, f;
		mono_gc_get_gcmemoryinfo (&a, &b, &c, &d, &h, &f);
		s->gc_at_k = (unsigned) (h / 1024);
		s->gc_frag_k = f > 0 ? (unsigned) (f / 1024) : 0;   /* (sgen's figure can dip below 0 between a collection's two updates) */
	}
#endif
	if (!nonblocking) {
		struct mallinfo mi = mallinfo ();
		s->nl_used_k = k10_nl_fix_k ((unsigned long long) (unsigned) mi.uordblks);
		s->nl_sbrk_k = k10_nl_fix_k ((unsigned long long) mi.arena);
		SceKernelFreeMemorySizeInfo fi;
		memset (&fi, 0, sizeof (fi));
		fi.size = sizeof (fi);
		sceKernelGetFreeMemorySize (&fi);
		s->free_user_k = fi.size_user / 1024;
	}
	s->cap_mb = g_max_heap_mb;
#if !defined (SHEPHERD_MAC_CHECK)
	if (k10_pool_live) {
		size_t tk = 0, fl = 0, lu = 0;
		mono_vita_vm_arena_pool_get (&tk, &fl, &lu);
		s->floor_k = (unsigned) (fl / 1024);
		s->lowest_k = (unsigned) (lu / 1024);
	}
#endif
	k10h.samples++;
	if (s->arena_used_k > k10h.max_arena_k) k10h.max_arena_k = s->arena_used_k;
	if (s->gc_frag_k > k10h.max_frag_k) k10h.max_frag_k = s->gc_frag_k;
	if (s->extents > k10h.max_extents) k10h.max_extents = s->extents;
	if (s->arena_largest_k < k10h.min_largest_k) { k10h.min_largest_k = s->arena_largest_k; k10h.min_largest_frame = (unsigned) s->frame; }
}

static void k10_write_file (const char *name, const char *data, size_t len)
{
	char path [160];
	snprintf (path, sizeof path, "%s/%s", logs_dir (), name);
	SceUID fd = sceIoOpen (path, SCE_O_WRONLY | SCE_O_CREAT | SCE_O_TRUNC, 0666);
	if (fd < 0)
		return;
	sceIoWrite (fd, data, len);
	sceIoClose (fd);
}

static void k10_memlog_write (void)
{
	if (!k10_samples || !k10_ns)
		return;
	size_t cap = (size_t) k10_ns * 120 + 256, o = 0;
	char *b = (char *) malloc (cap);
	if (!b)
		return;
	o += snprintf (b + o, cap - o, "frame\tkind\tarena_used_kib\tarena_largest_free_kib\tfree_extents\tgc_heap_kib\tgc_heap_at_gc_kib\tgc_fragmented_kib\tnewlib_used_kib\tnewlib_sbrk_kib\tfree_user_kib\tgc_cap_mib\tpool_floor_kib\tpool_arena_lowest_kib\n");
	for (unsigned i = 0; i < k10_ns && o + 128 < cap; i++) {
		const K10Sample *s = &k10_samples [i];
		o += snprintf (b + o, cap - o, "%d\t%c\t%u\t%u\t%u\t%u\t%u\t%u\t%u\t%u\t%u\t%u\t%u\t%u\n", s->frame, s->kind, s->arena_used_k, s->arena_largest_k,
			       s->extents, s->gc_heap_k, s->gc_at_k, s->gc_frag_k, s->nl_used_k, s->nl_sbrk_k, s->free_user_k, s->cap_mb, s->floor_k, s->lowest_k);
	}
	k10_write_file ("k10-mem.tsv", b, o);
	free (b);
}

/* ---------------------------------------------------------------- the size-class census (test only) */
typedef struct { MonoClass *k; const char *ns, *nm; unsigned n, minsz, maxsz; unsigned long long b, a8, slot; } K10Class;   /* b true bytes, a8 at sgen's 8-byte alignment, slot the slots' bytes */
typedef struct {
	unsigned walks, at_major, objects, los_objects;
	unsigned long long bytes, slot_bytes, los_bytes, los_rounded, a8_small;
	unsigned bin_n [K10C_BINS];
	unsigned long long bin_b [K10C_BINS];
	K10Class cls [K10C_SLOTS];
	unsigned ncls, cls_full;
} K10Census;
static K10Census *k10c_cur, *k10c_best;   /* the walk in progress; the walk with the largest heap */
static int k10_stock_slots [64], k10_stock_n;   /* sgen's table, when the runtime cannot be asked (the computed 32-bit Vita table) */

static unsigned k10_slot_of (unsigned u)
{
#if !defined (SHEPHERD_MAC_CHECK)
	if (mono_vita_gc_slot_size)
		return (unsigned) mono_vita_gc_slot_size (u);
#endif
	if (u > 8000)
		return 0;
	if (!k10_stock_n) {   /* ms_calculate_block_obj_sizes on 16 KiB blocks (MS_BLOCK_SKIP 288: a 32-bit MSBlockInfo with 64 mark words) */
		static const int t [] = { 8, 16, 24, 32, 40, 48, 64, 80, 96, 128, 160, 200, 256, 328, 408, 512, 664, 840, 1072, 1336, 1784, 2296, 2680, 4024, 5360, 8000 };
		k10_stock_n = (int) (sizeof t / sizeof t [0]);
		memcpy (k10_stock_slots, t, sizeof t);
	}
	for (int i = 0; i < k10_stock_n; i++)
		if ((unsigned) k10_stock_slots [i] >= u)
			return (unsigned) k10_stock_slots [i];
	return 0;
}

static int k10c_ref_cb (MonoObject *obj, MonoClass *klass, uintptr_t size, uintptr_t num, MonoObject **refs, uintptr_t *offsets, void *data)
{
	(void) refs; (void) offsets; (void) data; (void) num;
	if (!size)
		return 0;   /* (a repeated call for the same object: its references only) */
	K10Census *c = k10c_cur;
	unsigned u = (unsigned) mono_object_get_size (obj);
	unsigned a8 = (u + 7u) & ~7u;
	unsigned slot = k10_slot_of (a8);
	c->objects++;
	c->bytes += u;
	if (!slot) {   /* the large-object space: whole 4 KiB pages (an estimate of its rounding: sgen's LOS sections hand out page-rounded chunks) */
		c->los_objects++;
		c->los_bytes += u;
		c->los_rounded += (u + 4095u) & ~4095u;
		slot = (u + 4095u) & ~4095u;
	} else {
		c->slot_bytes += slot;
		c->a8_small += a8;
		unsigned bin = a8 / 8u;
		if (bin < K10C_BINS) { c->bin_n [bin]++; c->bin_b [bin] += u; }
	}
	unsigned h = (unsigned) (((uintptr_t) klass >> 3) * 2654435761u) % K10C_SLOTS;
	for (unsigned i = 0; i < K10C_SLOTS; i++, h = (h + 1) % K10C_SLOTS) {
		K10Class *e = &c->cls [h];
		if (e->k == klass) {
			e->n++; e->b += u; e->a8 += a8; e->slot += slot;
			if (u < e->minsz) e->minsz = u;
			if (u > e->maxsz) e->maxsz = u;
			return 0;
		}
		if (!e->k) {
			if (c->ncls >= K10C_SLOTS * 3 / 4) { c->cls_full++; return 0; }
			e->k = klass; e->n = 1; e->b = u; e->a8 = a8; e->slot = slot; e->minsz = e->maxsz = u;
			e->ns = mono_class_get_namespace (klass);   /* (named during the walk: the collecting thread is attached; metadata strings live on) */
			e->nm = mono_class_get_name (klass);
			c->ncls++;
			return 0;
		}
	}
	return 0;
}

/* gc_event_cb, PRE_START_WORLD of a pause that ran a major collection (the world stopped, this thread the collecting one) */
static void k10_mem_major_walk (void)
{
	if (!k10m.census)
		return;
	if (!k10c_cur) {
		k10c_cur = (K10Census *) calloc (1, sizeof (K10Census));
		k10c_best = (K10Census *) calloc (1, sizeof (K10Census));
		if (!k10c_cur || !k10c_best) { k10m.census = 0; return; }
	}
	unsigned walks = k10c_best->walks;
	memset (k10c_cur, 0, sizeof *k10c_cur);
	mono_gc_walk_heap (0, k10c_ref_cb, NULL);
	k10c_cur->walks = walks + 1;
	k10c_cur->at_major = gcs.count [1] + 1;
	if (k10c_cur->slot_bytes + k10c_cur->los_rounded >= k10c_best->slot_bytes + k10c_best->los_rounded) {
		K10Census *t = k10c_best; k10c_best = k10c_cur; k10c_cur = t;   /* (the bigger walk kept) */
	} else
		k10c_best->walks = walks + 1;
}

static int k10_cmp_waste (const void *a, const void *b)
{
	const K10Class *x = (const K10Class *) a, *y = (const K10Class *) b;
	unsigned long long wx = x->slot - x->a8, wy = y->slot - y->a8;   /* (lost to the size classes; the 8-byte alignment is not a size class's doing) */
	return wx < wy ? 1 : wx > wy ? -1 : 0;
}

static void k10_census_end (void)
{
	if (!k10m.census || !k10c_best || !k10c_best->objects)
		return;
	K10Census *c = k10c_best;
	/* per class, sorted by bytes lost to the slots (+ the LOS's page rounding) */
	K10Class *v = (K10Class *) malloc (sizeof (K10Class) * c->ncls);
	unsigned nv = 0;
	if (v) {
		for (unsigned i = 0; i < K10C_SLOTS; i++)
			if (c->cls [i].k) v [nv++] = c->cls [i];
		qsort (v, nv, sizeof (K10Class), k10_cmp_waste);
	}
	size_t cap = 256 + (size_t) nv * 160 + K10C_BINS * 64, o = 0;
	char *b = (char *) malloc (cap);
	if (b && v) {
		o += snprintf (b + o, cap - o, "class\tcount\tmin_size\tmax_size\ttrue_kib\taligned8_kib\tslot_kib\tclass_waste_kib\n");
		for (unsigned i = 0; i < nv && o + 200 < cap; i++)
			o += snprintf (b + o, cap - o, "%s%s%s\t%u\t%u\t%u\t%llu\t%llu\t%llu\t%llu\n", v [i].ns && *v [i].ns ? v [i].ns : "", v [i].ns && *v [i].ns ? "." : "",
				       v [i].nm ? v [i].nm : "?", v [i].n, v [i].minsz, v [i].maxsz, v [i].b / 1024, v [i].a8 / 1024, v [i].slot / 1024, (v [i].slot - v [i].a8) / 1024);
		k10_write_file ("k10-classes.tsv", b, o);
	}
	/* per 8-byte size: count, true bytes, slot, waste */
	unsigned long long waste_small = c->slot_bytes - (c->bytes - c->los_bytes);
	if (b) {
		o = 0;
		o += snprintf (b + o, cap - o, "size_aligned8\tcount\ttrue_kib\tslot\tclass_waste_kib\n");
		for (unsigned i = 1; i < K10C_BINS && o + 64 < cap; i++)
			if (c->bin_n [i]) {
				unsigned s = k10_slot_of (i * 8);
				o += snprintf (b + o, cap - o, "%u\t%u\t%llu\t%u\t%llu\n", i * 8, c->bin_n [i], c->bin_b [i] / 1024, s,
					       ((unsigned long long) (s - i * 8) * c->bin_n [i]) / 1024);   /* (the size-class part: slot - the 8-byte-aligned size) */
			}
		k10_write_file ("k10-sizes.tsv", b, o);
	}
	/* what extra slot sizes would save (greedy, up to 8; each new class costs ~8 KiB: half a 16 KiB block left partly empty) */
	char sug [400] = "";
	{
		static unsigned slot_now [K10C_BINS];
		for (unsigned i = 1; i < K10C_BINS; i++) slot_now [i] = k10_slot_of (i * 8);
		int so = 0;
		for (int r = 0; r < 8; r++) {
			unsigned best_x = 0; long long best_gain = 0;
			for (unsigned x = 2; x < K10C_BINS; x++) {   /* candidate slot size x*8 */
				long long gain = 0;
				for (unsigned i = 1; i <= x; i++)
					if (c->bin_n [i] && slot_now [i] > x * 8)
						gain += (long long) c->bin_n [i] * (long long) (slot_now [i] - x * 8);
				gain -= 8192;
				if (gain > best_gain) { best_gain = gain; best_x = x; }
			}
			if (!best_x)
				break;
			for (unsigned i = 1; i <= best_x; i++)
				if (slot_now [i] > best_x * 8) slot_now [i] = best_x * 8;
			so += snprintf (sug + so, sizeof sug - so, "%s%u (%lld KiB)", r ? ", " : "", best_x * 8, best_gain / 1024);
			if (so > (int) sizeof sug - 40) break;
		}
	}
	/* the engine's own types (Shepherd.*) with the most waste, and the overall top */
	char eng [700] = "", top [700] = "";
	int eo = 0, to = 0, en = 0;
	for (unsigned i = 0; v && i < nv; i++) {
		unsigned long long w = v [i].slot - v [i].a8;
		int is_eng = v [i].ns && !strncmp (v [i].ns, "Shepherd", 8);
		if (is_eng && en < 10 && eo < (int) sizeof eng - 120)
			eo += snprintf (eng + eo, sizeof eng - eo, "%s %s %u x %u..%u B -> %llu KiB lost", en++ ? "," : "", v [i].nm ? v [i].nm : "?", v [i].n, v [i].minsz, v [i].maxsz, w / 1024);
		if (i < 8 && to < (int) sizeof top - 120)
			to += snprintf (top + to, sizeof top - to, "%s %s%s%s %u x %u..%u B -> %llu KiB", i ? "," : "", v [i].ns && *v [i].ns ? v [i].ns : "", v [i].ns && *v [i].ns ? "." : "",
					v [i].nm ? v [i].nm : "?", v [i].n, v [i].minsz, v [i].maxsz, w / 1024);
	}
	LOG ("K10 SIZES census (the walk with the largest heap: after major #%u, %u walks): %u objects, %llu KiB true size; small objects in slots %llu KiB "
	     "(%llu KiB = %.1f %% more than their true size: %llu KiB the 8-byte alignment, %llu KiB the size classes); %u large objects %llu KiB (+%llu KiB page "
	     "rounding)%s | <logs>/k10-sizes.tsv, k10-classes.tsv",
	     c->at_major, c->walks, c->objects, c->bytes / 1024, c->slot_bytes / 1024, waste_small / 1024,
	     c->slot_bytes ? 100.0 * waste_small / c->slot_bytes : 0.0, (c->a8_small - (c->bytes - c->los_bytes)) / 1024, (c->slot_bytes - c->a8_small) / 1024,
	     c->los_objects, c->los_bytes / 1024, (c->los_rounded - c->los_bytes) / 1024,
	     c->cls_full ? " (class table full: some objects not by class)" : "");
	LOG ("K10 SIZES the engine's types with the most lost:%s", eng [0] ? eng : " none");
	LOG ("K10 SIZES all types, the most lost:%s", top);
	LOG ("K10 SIZES extra slot sizes that would save the most (greedy, net of ~8 KiB each): %s", sug [0] ? sug : "none");
	free (b);
	free (v);
}

/* ---------------------------------------------------------------- the memory-pressure ladder (host half) */
static volatile long long k10_live_settled = -1;   /* review fix R6: g_gc_major_base as the previous major cycle left it (after its sweep); -1 none yet */
static volatile int k10_level;           /* 0..5: what the engine reads (shepherd_vita_engine_mem op 0) */
static unsigned k10_level_max, k10_level_changes, k10_steps_done [6];
static unsigned long long k10_released_k [6];
static void k10_ladder_heartbeat (int frame, const MemSnap *m)
{
	if (!k10m.ladder)
		return;
	int forced = -1;
	for (int i = 0; i < k10m.force_n; i++)
		if (frame >= k10m.force_at [i])
			forced = k10m.force_lvl [i];   /* (the last one reached wins) */
	unsigned gc_room = 999, nl_room = 999;
	if (g_runtime_up && g_max_heap_mb) {
		/* review fix R6: what the collections LEFT, not the heap now -- that includes the garbage of sgen's allowance and climbs to the soft
		 * limit before every major near the cap, so the ladder rose and fell with each GC cycle and re-released its caches every time. M2's
		 * g_gc_major_base alone is not enough: under lever 32 a finishing pause resets it to the heap BEFORE the worker's sweep, and minors
		 * lower it again as the sweep frees (VLX k10c, 00098 idle: 92 -> 72 MiB within one cycle; the level still flipped 0 <-> 2 with
		 * thresholds in that band). So: the lower of the base now and the base the previous cycle settled to (k10_live_settled, taken just
		 * before a completed major resets it) -- the heap after the last finished sweep; frees show at once, growth one cycle later (the
		 * soft limit and the guard still act at once). Before the first major: the heap now. */
		long long base = -1, settled = k10_live_settled, heap;
#if !defined (SHEPHERD_MAC_CHECK)
		base = g_gc_major_base;
#endif
		if (base > 0 && settled > 0)
			heap = settled < base ? settled : base;
		else
			heap = base > 0 ? base : (long long) mono_gc_get_heap_size ();
		long long cap = (long long) g_max_heap_mb * MiB;
		gc_room = heap < cap ? (unsigned) ((cap - heap) / MiB) : 0;
	}
	unsigned nlcap = k10_newlib_cap_k (), nlsb = m->newlib_sbrk_k > m->newlib_used_k ? m->newlib_sbrk_k : m->newlib_used_k;
	nl_room = nlcap > nlsb ? (nlcap - nlsb) / 1024 : 0;
#if !defined (SHEPHERD_MAC_CHECK)
	if (k10_pool_live) {   /* the shared pool: newlib may grow into every free page up to the arena's lowest one (its floor rises over free pages) */
		size_t tk = 0, fl = 0, lu = 0;
		mono_vita_vm_arena_pool_get (&tk, &fl, &lu);
		nl_room = lu > k10_nl_top ? (unsigned) ((lu - k10_nl_top) / MiB) : 0;
		nlcap = (unsigned) ((SHP_K10_BOOT_KB * 1024ull + lu) / 1024);
	}
#endif
	int lv = 0;
	for (int i = 0; i < 5; i++)
		if (gc_room < k10m.lad_gc [i] || nl_room < k10m.lad_nl [i])
			lv = i + 1;
	int cur = k10_level;
	if (lv < cur) {   /* down only with 4 MiB to spare above the current level's thresholds */
		int keep = 0;
		for (int i = 0; i < cur; i++)
			if (gc_room < k10m.lad_gc [i] + 4 || nl_room < k10m.lad_nl [i] + 4)
				keep = i + 1;
		lv = keep;
	}
	if (forced >= 0)
		lv = forced;
	if (lv != cur) {
		k10_level = lv;
		k10_level_changes++;
		if ((unsigned) lv > k10_level_max) k10_level_max = (unsigned) lv;
		LOG ("K10 LADDER f=%d level %d -> %d (GC heap headroom %u MiB of a %u MiB cap, newlib headroom %u MiB of %u KiB%s): the engine gives back steps 1..%d "
		     "(1 preloads, 2 in-RAM learning caches, 3 image caches, 4 the warm-return cache, 5 extra VRAM residency)",
		     frame, cur, lv, gc_room, g_max_heap_mb, nl_room, nlcap, forced >= 0 ? "; FORCED (test)" : "", lv);
	}
}

/* the engine (Runtime/MemLadder.cs): op 0 = the level now; op 1 = step arg done (op 2 follows with the KiB it gave back) */
int shepherd_vita_engine_mem (int op, int arg);
int shepherd_vita_engine_mem (int op, int arg)
{
	static int last_step;
	switch (op) {
	case 0: return k10m.ladder ? k10_level : -1;
	case 1: if (arg >= 1 && arg <= 5) { k10_steps_done [arg]++; last_step = arg; } return 0;
	case 2: if (last_step) k10_released_k [last_step] += (unsigned) arg; return 0;
	default: return -1;
	}
}

/* ---------------------------------------------------------------- hooks */
/* every heartbeat (shepherd_vita_engine_checkpoint kind 0 / 7) */
static void k10_mem_heartbeat (int frame, const MemSnap *m)
{
	k10_frame_now = frame;
	if (k10m.memlog && (k10_last_frame_sampled == ~0u || (unsigned) frame >= k10_last_frame_sampled + k10m.memlog)) {
		k10_last_frame_sampled = (unsigned) frame;
		k10_sample ('h', 0);
	}
	k10_ladder_heartbeat (frame, m);
}
/* gc_event_cb, POST_START_WORLD of a major (the world running again; this thread still the collector's): a timeline sample; review fix R3:
 * a pause that completed a major (serial, a concurrent finish or an explicit GC.Collect -- not a concurrent mark's START pause) ends the
 * guard's leniency after a pool cap drop */
static void k10_mem_after_major (int conc)
{
	if (conc != 1) {
		k10_cap_drop_pending = 0;
#if !defined (SHEPHERD_MAC_CHECK)
		k10_live_settled = g_gc_major_base;   /* review fix R6: the previous cycle's settled base (gc_event_cb calls this BEFORE M2 / lever 32 reset it) */
#endif
	}
	if (k10m.memlog)
		k10_sample ('m', 1);
}
/* heap_limit_cb (the GC lock held: a flag and a count only), play apps: 1 = this refusal follows a pool cap drop that no major has answered
 * yet -- the memory guard lets it through (review fix R3) */
static int k10_guard_spare (void)
{
	if (!k10_cap_drop_pending)
		return 0;
	k10_guard_spared++;
	return 1;
}

/* main, at the end (after the K9 GC end line) */
static void k10_mem_end_line (void)
{
	if (!k10m.on)
		return;
	k10_census_end ();
	k10_memlog_write ();
	char pool [512] = "", holes [300] = "", lad [300] = "";
#if !defined (SHEPHERD_MAC_CHECK)
	if (k10_pool_live)
		snprintf (pool, sizeof pool, " | shared pool: newlib's top peak %u KiB (+%u KiB boot block), floor %u KiB, %u floor moves (%u stopped short by the arena), %u "
			  "takes refused; GC heap cap %u..%u MiB (%u changes, %u drops; %u guard refusals let through after a drop)", (unsigned) (k10_nl_top_peak / 1024),
			  SHP_K10_BOOT_KB, (unsigned) (k10_nl_floor / 1024), k10_floor_moves, k10_floor_short, k10_take_fails,
			  k10_cap_min_mb == ~0u ? g_max_heap_mb : k10_cap_min_mb, k10_cap_max_mb ? k10_cap_max_mb : g_max_heap_mb, k10_cap_moves, k10_cap_drops,
			  k10_guard_spared);
	else if (k10_pool_req)
		snprintf (pool, sizeof pool, " | shared pool requested, NOT used: %s", k10m.pool_why ? k10m.pool_why : "?");
#endif
	if (k10h.samples)
		snprintf (holes, sizeof holes, " | holes (%u samples): the arena's largest free block at least %u KiB (f%u), at most %u free extents, sgen's fragmented "
			  "bytes at most %u KiB, arena used at most %u KiB -> <logs>/k10-mem.tsv", k10h.samples, k10h.min_largest_k, k10h.min_largest_frame,
			  k10h.max_extents, k10h.max_frag_k, k10h.max_arena_k);
	if (k10m.ladder)
		snprintf (lad, sizeof lad, " | ladder: highest level %u, %u changes; steps done 1:%u 2:%u 3:%u 4:%u 5:%u, KiB given back 1:%llu 2:%llu 3:%llu 4:%llu 5:%llu",
			  k10_level_max, k10_level_changes, k10_steps_done [1], k10_steps_done [2], k10_steps_done [3], k10_steps_done [4], k10_steps_done [5],
			  k10_released_k [1], k10_released_k [2], k10_released_k [3], k10_released_k [4], k10_released_k [5]);
	LOG ("K10 MEM end:%s%s%s%s", pool, holes, lad, pool [0] || holes [0] || lad [0] ? "" : " (no kit #10 memory switch on)");
}
