/* SHEPHERD-VITA device kit #11, track walk (vita/notes/kit11-walk.md): the walk track's host side. Included by host.c after k10_mem.inc.c.
 *
 * THE C HEAP'S CENSUS (MemReport, test-free: one line per checkpoint): where the newlib heap goes -- the runtime's mempools split by
 * owner (every loaded image's own pool; the default load context's memory manager: generic instances, the interpreter's methods and code,
 * vtables), the code manager, the IL images, the runtime's own size counters (MonoClass, inflated classes / methods, MonoMethod,
 * signatures), newlib's free chunks inside its sbrk'd size, and the rest (the host's, the engine's native parts, vitaGL's, hash tables).
 * Needs vita/patches/k11-walk.patch in the runtime (mono_vita_census, a weak symbol here: a kit #10 runtime prints "no census" and
 * nothing else changes). Read only: it changes nothing the game or the GC sees. SHEPHERD_K11_CHEAP=0 (env file) = no line (kit #10).
 * (Integration review: this switch was SHEPHERD_K11_CENSUS=0, a name measure's pixel census reads as its frame list -- renamed so the two
 * never meet; the census itself now reads "0" as off.)
 * Line: "<tag> cheap: ..." after each "<tag> mono:" line (the engine's checkpoints, CPend, H11).
 *
 * The kit #11 walk defaults themselves (the shared pool ON in the play apps; evacuation at kit #10's sgen 66 %, 90 % opt-in since the review)
 * are host.c's DEFAULTS block, read by k10_newlib.inc.c (before main) and k10_mem.inc.c (k10_mem_config); SHEPHERD_K11_WALK=0 = kit #10's
 * defaults for all of them. */

extern int mono_vita_census (int64_t *out, int n) __attribute__ ((weak));   /* vita/patches/k11-walk.patch (mini-vita.c) */

static int k11_census_off = -1;   /* -1: not read yet */

static void k11_cheap_line (const char *tag)
{
	if (k11_census_off < 0) {
		const char *e = getenv ("SHEPHERD_K11_CHEAP"), *w = getenv ("SHEPHERD_K11_WALK");
		k11_census_off = (e && !strcmp (e, "0")) || (w && !strcmp (w, "0"));
	}
	if (k11_census_off)
		return;
	struct mallinfo mi = mallinfo ();
	/* review fix: newlib's used size without the shared pool's gap (mallocr books the gap below the pool as in use) -- k10_nl_fix_k, as the
	 * MEM / ram lines (host.c mem_snap); = mi.uordblks outside the pool */
	unsigned used_k = k10_nl_fix_k ((unsigned long long) (unsigned) mi.uordblks), free_k = (unsigned) mi.fordblks / 1024, chunks = (unsigned) mi.ordblks;
	unsigned long long il_pool_k = g_il_pool ? g_il_pool_used / 1024 : 0, il_k = g_il_bytes / 1024;
	unsigned long long il_nl_k = il_k > il_pool_k ? il_k - il_pool_k : 0;
#if !defined (SHEPHERD_MAC_CHECK)
	if (!mono_vita_census) {
		LOG ("%s cheap: newlib used %u KiB (free inside %u KiB in %u chunks) | IL images %llu KiB (%llu in the IL pool) | no census (the runtime has no "
		     "vita/patches/k11-walk.patch)", tag, used_k, free_k, chunks, il_k, il_pool_k);
		return;
	}
	int64_t v [20];
	memset (v, 0, sizeof v);
	int n = mono_vita_census (v, 20);
	if (n < 20) {
		LOG ("%s cheap: the runtime's census returned %d of 20 values", tag, n);
		return;
	}
	long long mp_k = v [19] / 1024, img_k = v [0] / 1024, alc_k = v [3] / 1024, cm_k = v [4] / 1024;
	long long other_mp_k = mp_k - img_k - alc_k;
	/* the rest of newlib: what is neither a mempool, the code manager, nor an IL image read into newlib (pool mode puts every IL image there) */
	long long rest_k = (long long) used_k - mp_k - cm_k - (long long) il_nl_k;
	LOG ("%s cheap: newlib used %u KiB (free inside %u KiB in %u chunks) = mempools %lld KiB [images' own %lld (%lld images) + the load context's %lld "
	     "(generic instances, interpreter methods + code, vtables) + other %lld] + code manager %lld KiB (%lld used) + IL images in newlib %llu KiB "
	     "(of %llu; %llu in the IL pool; the images' raw bytes %lld KiB) + the rest %lld KiB (host, engine native, vitaGL, hash tables) | counters: "
	     "MonoClass %lld KiB, inflated classes %lld KiB, inflated methods %lld KiB, MonoMethod %lld KiB, signatures %lld KiB (+ inflated %lld, memberref cache %lld) | "
	     "classes def %lld, gtd %lld, ginst %lld, gparam %lld, array %lld, pointer %lld",
	     tag, used_k, free_k, chunks, mp_k, img_k, (long long) v [1], alc_k, other_mp_k, cm_k, (long long) v [5] / 1024, il_nl_k, il_k, il_pool_k,
	     (long long) v [2] / 1024, rest_k, (long long) v [6] / 1024, (long long) v [7] / 1024, (long long) v [8] / 1024, (long long) v [15] / 1024,
	     (long long) v [16] / 1024, (long long) v [17] / 1024, (long long) v [18] / 1024, (long long) v [9], (long long) v [10], (long long) v [11],
	     (long long) v [12], (long long) v [13], (long long) v [14]);
#else
	(void) used_k; (void) free_k; (void) chunks; (void) il_nl_k;
#endif
}
