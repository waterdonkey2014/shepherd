/* ---------------------------------------------------------------- device kit #7, track probes: the measurement app's device probes
 * (vita/notes/kit7-probes.md; host.c includes this in the measurement build only, SHP_TEST_CLOCK = Shepherd Measure, SHPD00099).
 * Measurement only: nothing the probes find is adopted, no system setting is touched, every block they allocate is freed, every file they
 * open is closed. Two phases, chosen so a probe can never disturb the run it shares the app with:
 *   AT START, before the memory partition (they need the whole process budget; every block is freed again, and a PRB line compares the
 *   free memory before / after so the partition provably gets what it always got):
 *     - memory budget: what this app really gets of the 512 MB main / 128 MB video memory on this firmware -- the free user / CDRAM /
 *       PHYCONT pools, the largest block and the total of each memory type (largest-block search; the types the probe app ran on the
 *       owner's console since 2026-10-07), whether PHYCONT comes out of the user pool, ATTRIBUTE2 in this app's param.sfo (the one known
 *       homebrew setting that raises the budget: 12 = +109 MiB), and the CPU's speed on each type (copy, read, write, a dependent random
 *       read per 32-byte line) -- the answer to "spare video memory used as general memory";
 *     - big memory pages: a heap-walk microbenchmark (a dependent pointer chase, one node per 4 KiB page, random order: every step a
 *       different page, i.e. a TLB miss unless the mapping is bigger than 4 KiB) over 16 MiB of USER_RW, USER_RW aligned to 1 MiB and
 *       PHYCONT (physically contiguous, 1 MiB granular), plus a dense chase (one node per 32-byte line); the walk time per step on each,
 *       and the A9's DTLB miss counter when the PMU library loads (ScePerf; on a retail console it may not: then times only).
 *   AT THE END, after the END line (the run's verdict is decided and logged first; the last picture stays meanwhile, then green / red):
 *     - storage read test: every mounted store (ux0, imc0 -- the PCH-2000's built-in 1 GB --, uma0, grw0, xmc0): a 100-400 MB file read in
 *       1 MiB sequential chunks (ux0: the card's own pack, 186 MB; elsewhere <store>:data/shepherd/readtest.bin if the owner put one
 *       there), each store's fastest read size (4 KiB .. 2 MiB, sequential), its block alignment (64 KiB reads shifted by 512 B / 4 KiB /
 *       32 KiB) and random reads (4 KiB, 128 KiB = the engine's read size); results -> ux0:data/shepherd/probes/storage.txt (the device
 *       profile of every later start carries them) + PRB lines. Never writes a store unless SHEPHERD_K7_STORE_WRITE names it
 *       (<store>:<MiB>[,<store>:<MiB>], e.g. imc0:256; a bare number = imc0 only; never grw0, a game card's writable area) and it has no
 *       readtest.bin: then its own file <store>:data/shepherd/readtest-shepherd.bin, only with the size + max (64 MiB, 10 % of the store)
 *       free, written whole or not used, read, deleted unless SHEPHERD_K7_STORE_KEEP=1 (a leftover one -- a run killed meanwhile -- is
 *       deleted at the next start, or measured again with KEEP=1; it is never taken for the owner's readtest.bin);
 *     - NGS check: does the NGS sound engine mix off the ARM cores? NGS loaded (sysmodule), a system with a master buss and 64 player
 *       voices playing a synthetic buffer (no output port: silent), sceNgsSystemUpdate timed with 0 / 16 / 64 voices playing: the wall
 *       time, the calling thread's own CPU time (runClocks) and three spinner threads on the other cores (their rate with and without the
 *       updates) -- the calling thread's CPU time = the wall time means the ARM core mixes; much less means it waits for another processor;
 *     - GPU transfer unit: (vita/gltest/shepgl sgl_k7_transfer_probe) sceGxmTransferCopy linear / swizzled / tiled, Fill, Downscale against
 *       the CPU and sceDmacMemcpy, swizzle and downscale results compared byte for byte with the CPU's.
 * SHEPHERD_K7_PROBES=0: none of it (kit #6's measurement app); a list (mem,pages,store,ngs,gpu) runs only those. */
#include <psp2/sysmodule.h>
#include <psp2/perf.h>

static int k7_probe_on (const char *what)
{
	const char *e = getenv ("SHEPHERD_K7_PROBES");
	if (!e || !*e || !strcmp (e, "1") || !strcmp (e, "all"))
		return 1;
	if (!strcmp (e, "0"))
		return 0;
	return strstr (e, what) != NULL;
}
/* scratch for the pointer-chase builders: a USER_RW block (freed again), so the probes at start leave the newlib heap's high-water mark
 * exactly as it was (the engine's newlib numbers stay comparable with kit #6's) */
static SceUID g_k7_scratch_uid = -1;
static unsigned *k7_scratch (unsigned bytes)
{
	void *m = NULL;
	g_k7_scratch_uid = sceKernelAllocMemBlock ("shepherd-probe-scratch", SCE_KERNEL_MEMBLOCK_TYPE_USER_RW, (bytes + 4095u) & ~4095u, NULL);
	if (g_k7_scratch_uid < 0 || sceKernelGetMemBlockBase (g_k7_scratch_uid, &m) < 0) { if (g_k7_scratch_uid >= 0) sceKernelFreeMemBlock (g_k7_scratch_uid); g_k7_scratch_uid = -1; return NULL; }
	return (unsigned *) m;
}
static void k7_scratch_free (void) { if (g_k7_scratch_uid >= 0) sceKernelFreeMemBlock (g_k7_scratch_uid); g_k7_scratch_uid = -1; }
static unsigned k7_rng = 0x9e3779b9u;
static unsigned k7_rand (void) { k7_rng ^= k7_rng << 13; k7_rng ^= k7_rng >> 17; k7_rng ^= k7_rng << 5; return k7_rng; }
static double k7_ms (SceUInt64 t0) { return (double) (sceKernelGetProcessTimeWide () - t0) / 1000.0; }

/* ---- memory budget */
typedef struct { const char *name; unsigned type, align; } K7MemType;
static const K7MemType k7_memtypes [] = {
	{ "USER_RW", SCE_KERNEL_MEMBLOCK_TYPE_USER_RW, 4096 },
	{ "USER_CDRAM_RW", SCE_KERNEL_MEMBLOCK_TYPE_USER_CDRAM_RW, 256 * 1024 },
	{ "USER_MAIN_PHYCONT_NC_RW", SCE_KERNEL_MEMBLOCK_TYPE_USER_MAIN_PHYCONT_NC_RW, 1024 * 1024 },
	{ "USER_MAIN_PHYCONT_RW", SCE_KERNEL_MEMBLOCK_TYPE_USER_MAIN_PHYCONT_RW, 1024 * 1024 },
	/* (USER_MAIN_CDIALOG_RW is not searched: the probe app hung in its search on the owner's PCH-2000, 2026-10-07; nothing uses it) */
};
static unsigned k7_largest (const K7MemType *t, unsigned hi)
{
	unsigned lo = 0;
	hi = (hi / t->align + 2) * t->align;
	while (hi - lo > t->align) {
		unsigned mid = ((lo + (hi - lo) / 2) / t->align) * t->align;
		if (mid <= lo) mid = lo + t->align;
		SceUID u = sceKernelAllocMemBlock ("shepherd-probe", t->type, mid, NULL);
		if (u >= 0) { sceKernelFreeMemBlock (u); lo = mid; } else hi = mid;
	}
	return lo;
}
static void k7_free_sizes (SceKernelFreeMemorySizeInfo *fi)
{
	memset (fi, 0, sizeof *fi);
	fi->size = sizeof *fi;
	sceKernelGetFreeMemorySize (fi);
}
/* the CPU on one memory type: copy / read / write of 2 MiB, dependent random reads per 32-byte line over 4 MiB */
static unsigned __attribute__ ((noinline)) k7_sum (const unsigned *p, unsigned n) { unsigned s = 0; for (unsigned i = 0; i < n; i += 8) s += p [i]; return s; }
static unsigned __attribute__ ((noinline)) k7_chase (void *const *p, unsigned steps) { void *const *q = p; while (steps--) q = (void *const *) *q; return (unsigned) (uintptr_t) q; }
static volatile unsigned g_k7_sink;   /* every chase's result lands here: gcc infers k7_chase pure and would drop a call whose result is unused */
/* returns where the cycle starts (a node; with spread the first node is not at base) */
static void *const *k7_chase_build (char *base, unsigned nodes, unsigned stride, unsigned spread, unsigned *idx)
{
	/* one cycle through `nodes` nodes (Sattolo's shuffle): node i at base + i * stride + (spread ? a random 32-byte line within the stride : 0) */
	for (unsigned i = 0; i < nodes; i++) idx [i] = i;
	for (unsigned i = nodes - 1; i > 0; i--) { unsigned j = k7_rand () % i, t = idx [i]; idx [i] = idx [j]; idx [j] = t; }
	unsigned *off = idx + nodes;
	for (unsigned i = 0; i < nodes; i++) off [i] = i * stride + (spread ? (k7_rand () % (stride / 32)) * 32 : 0);
	for (unsigned i = 0; i < nodes; i++) *(void **) (base + off [idx [i]]) = base + off [idx [(i + 1) % nodes]];
	return (void *const *) (base + off [idx [0]]);
}
static void k7_mem_speed (const K7MemType *t, unsigned *scratch)
{
	const unsigned sz = 4u * MiB;
	SceUID u = sceKernelAllocMemBlock ("shepherd-probe-speed", t->type, sz, NULL);
	void *m = NULL;
	if (u < 0 || sceKernelGetMemBlockBase (u, &m) < 0) { LOG ("PRB mem speed %s: no 4 MiB block (0x%08x)", t->name, (unsigned) u); if (u >= 0) sceKernelFreeMemBlock (u); return; }
	char *b = (char *) m;
	memset (b, 1, sz);
	SceUInt64 t0 = sceKernelGetProcessTimeWide ();
	for (int r = 0; r < 4; r++) memcpy (b + sz / 2, b, sz / 2);
	double cp = k7_ms (t0);
	t0 = sceKernelGetProcessTimeWide ();
	unsigned s = 0;
	for (int r = 0; r < 4; r++) s += k7_sum ((const unsigned *) b, sz / 4);
	double rd = k7_ms (t0);
	t0 = sceKernelGetProcessTimeWide ();
	for (int r = 0; r < 4; r++) memset (b, r, sz);
	double wr = k7_ms (t0);
	void *const *start = k7_chase_build (b, sz / 32, 32, 0, scratch);
	g_k7_sink += k7_chase (start, 4096);
	t0 = sceKernelGetProcessTimeWide ();
	s += k7_chase (start, 65536);
	double ch = k7_ms (t0);
	sceKernelFreeMemBlock (u);
	LOG ("PRB mem speed %-24s copy %.0f MiB/s, read %.0f MiB/s (every 32-byte line), write %.0f MiB/s, dependent random read %.0f ns per 32-byte line (4 MiB)%s",
	     t->name, 8.0 / (cp / 1000.0), 16.0 / (rd / 1000.0), 16.0 / (wr / 1000.0), ch * 1e6 / 65536, s == 0x12345678u ? " " : "");
}
static unsigned k7_sfo_attr2 (void)
{
	void *d;
	unsigned n;
	if (read_file ("app0:sce_sys/param.sfo", &d, &n) != 0)
		return ~0u;
	unsigned v = ~0u;
	const unsigned char *p = (const unsigned char *) d;
#define U16(o) ((unsigned) p [o] | (unsigned) p [(o) + 1] << 8)
#define U32(o) (U16 (o) | U16 ((o) + 2) << 16)
	if (n >= 20 && !memcmp (p, "\0PSF", 4)) {
		unsigned kt = U32 (8), dt = U32 (12), ne = U32 (16);
		for (unsigned i = 0; i < ne && 20 + i * 16 + 16 <= n; i++) {
			unsigned e = 20 + i * 16, ko = kt + U16 (e), dof = dt + U32 (e + 12);
			if (ko < n && dof + 4 <= n && !strncmp ((const char *) p + ko, "ATTRIBUTE2", n - ko))
				v = U32 (dof);
		}
	}
#undef U16
#undef U32
	free (d);
	return v;
}
static void k7_probe_memory (void)
{
	SceUInt64 t0 = sceKernelGetProcessTimeWide ();
	SceKernelFreeMemorySizeInfo f0, f1;
	k7_free_sizes (&f0);
	unsigned a2 = k7_sfo_attr2 ();
	LOG ("PRB mem start: free user %u KiB, cdram %u KiB, phycont %u KiB (after this eboot, the %u MiB newlib heap and the 512 KiB main thread); "
	     "app0:sce_sys/param.sfo ATTRIBUTE2=%d%s", (unsigned) f0.size_user / 1024, (unsigned) f0.size_cdram / 1024, (unsigned) f0.size_phycont / 1024,
	     (unsigned) SHP_NEWLIB_HEAP_MB, a2 == ~0u ? -1 : (int) a2, a2 == 12 ? " (the known homebrew setting for a bigger budget: +109 MiB of main memory, in effect)"
	     : a2 == ~0u ? " (not readable)" : " (12 would give +109 MiB of main memory)");
	unsigned tot [4] = { 0, 0, 0, 0 };
	for (unsigned i = 0; i < sizeof k7_memtypes / sizeof k7_memtypes [0]; i++) {
		const K7MemType *t = &k7_memtypes [i];
		unsigned hi = t->type == SCE_KERNEL_MEMBLOCK_TYPE_USER_CDRAM_RW ? (unsigned) f0.size_cdram : t->type == SCE_KERNEL_MEMBLOCK_TYPE_USER_RW
			      ? (unsigned) f0.size_user : (unsigned) f0.size_phycont;
		if (!hi) hi = 32u * MiB;
		SceUInt64 t1 = sceKernelGetProcessTimeWide ();
		unsigned big = k7_largest (t, hi);
		static SceUID held [64];
		int nh = 0;
		while (nh < 64) {
			unsigned bl = k7_largest (t, big ? big : hi);
			if (bl < t->align) break;
			SceUID u = sceKernelAllocMemBlock ("shepherd-probe-tot", t->type, bl, NULL);
			if (u < 0) break;
			held [nh++] = u;
			tot [i] += bl;
		}
		SceKernelFreeMemorySizeInfo fm;
		k7_free_sizes (&fm);   /* (all of this type held: what the OTHER pools have left) */
		for (int k = 0; k < nh; k++) sceKernelFreeMemBlock (held [k]);
		LOG ("PRB mem %-24s largest block %.2f MiB, total %.2f MiB in %d block(s) (%.0f ms)%s; while all of it was held: free user %u KiB, cdram %u KiB, phycont %u KiB",
		     t->name, big / (double) MiB, tot [i] / (double) MiB, nh, k7_ms (t1), nh == 64 ? " (64 blocks: a lower bound)" : "", (unsigned) fm.size_user / 1024,
		     (unsigned) fm.size_cdram / 1024, (unsigned) fm.size_phycont / 1024);
	}
	unsigned *scratch = k7_scratch ((4u * MiB / 32u) * 2u * sizeof (unsigned));
	if (scratch) {
		for (unsigned i = 0; i < sizeof k7_memtypes / sizeof k7_memtypes [0]; i++)
			k7_mem_speed (&k7_memtypes [i], scratch);
		k7_scratch_free ();
	}
	k7_free_sizes (&f1);
	LOG ("PRB mem verdict: this app gets %u KiB of main memory to use after its eboot + newlib heap (the process budget with ATTRIBUTE2=%d (-1 = unread): eboot span + newlib "
	     "%u MiB + 512 KiB main thread + this), %u KiB of video memory (CDRAM: vitaGL takes all of it next) and %u KiB physically contiguous; the other "
	     "known normal homebrew lever is spare CDRAM as general memory -- see the speed lines (CPU on CDRAM vs USER_RW) | free after the probe: user %u, "
	     "cdram %u, phycont %u KiB (%s) | %.0f ms", tot [0] / 1024, a2 == ~0u ? -1 : (int) a2, (unsigned) SHP_NEWLIB_HEAP_MB, tot [1] / 1024, tot [3] / 1024,
	     (unsigned) f1.size_user / 1024, (unsigned) f1.size_cdram / 1024, (unsigned) f1.size_phycont / 1024,
	     f1.size_user == f0.size_user && f1.size_cdram == f0.size_cdram && f1.size_phycont == f0.size_phycont ? "= before: the partition gets what it always got"
	     : "DIFFERS from before (WARN)", k7_ms (t0));
	if (f1.size_user != f0.size_user || f1.size_cdram != f0.size_cdram || f1.size_phycont != f0.size_phycont)
		LOG ("WARN PRB the free memory after the memory probe differs from before it (user %u -> %u KiB): the partition below is computed from what is free now",
		     (unsigned) f0.size_user / 1024, (unsigned) f1.size_user / 1024);
}

/* ---- big memory pages */
static int g_k7_perf = -1;   /* the PMU library: -1 not tried, 0 not available, 1 loaded */
static void k7_perf_load (void)
{
	if (g_k7_perf >= 0) return;
	g_k7_perf = 0;
	const char *e = getenv ("SHEPHERD_K7_PERF");
	if (e && !strcmp (e, "0")) { LOG ("PRB pages: the PMU (ScePerf) not tried (SHEPHERD_K7_PERF=0): walk times only"); return; }
	int r = sceSysmoduleLoadModule (SCE_SYSMODULE_PERF);
	if (r < 0) { LOG ("PRB pages: the PMU library (sysmodule PERF) does not load here (0x%08x): walk times only, no DTLB miss counts", (unsigned) r); return; }
	int rr = scePerfArmPmonReset (SCE_PERF_ARM_PMON_THREAD_ID_SELF);
	int rs = rr >= 0 ? scePerfArmPmonSelectEvent (SCE_PERF_ARM_PMON_THREAD_ID_SELF, 0, SCE_PERF_ARM_PMON_DTLB_MISS) : rr;
	int rc = rs >= 0 ? scePerfArmPmonSelectEvent (SCE_PERF_ARM_PMON_THREAD_ID_SELF, 1, SCE_PERF_ARM_PMON_DATA_MAINTLB_STALL) : rs;
	if (rr < 0 || rs < 0 || rc < 0) {
		LOG ("PRB pages: the PMU library loaded but its counters refused (reset 0x%08x, select 0x%08x / 0x%08x): walk times only", (unsigned) rr, (unsigned) rs, (unsigned) rc);
		sceSysmoduleUnloadModule (SCE_SYSMODULE_PERF);
		return;
	}
	g_k7_perf = 1;
	LOG ("PRB pages: the PMU library loaded: counter 0 = DTLB misses, counter 1 = data main-TLB stall cycles");
}
static void k7_walk (const char *what, unsigned type, unsigned align, int use_opt, unsigned *scratch)
{
	const unsigned sz = 16u * MiB;
	SceKernelAllocMemBlockOpt opt;
	memset (&opt, 0, sizeof opt);
	opt.size = sizeof opt;
	opt.attr = SCE_KERNEL_ALLOC_MEMBLOCK_ATTR_HAS_ALIGNMENT;
	opt.alignment = align;
	SceUID u = sceKernelAllocMemBlock ("shepherd-probe-walk", type, sz, use_opt ? &opt : NULL);
	void *m = NULL;
	if (u < 0 || sceKernelGetMemBlockBase (u, &m) < 0) { LOG ("PRB pages %s: no 16 MiB block (0x%08x)", what, (unsigned) u); if (u >= 0) sceKernelFreeMemBlock (u); return; }
	char *b = (char *) m;
	double t_page = 0, t_line = 0;
	unsigned miss_page = 0, miss_line = 0, stall_page = 0, stall_line = 0;
	for (int pass = 0; pass < 2; pass++) {
		unsigned nodes = pass == 0 ? sz / 4096 : (4u * MiB) / 32, stride = pass == 0 ? 4096 : 32;
		void *const *start = k7_chase_build (b, nodes, stride, pass == 0, scratch);
		const unsigned steps = 65536;
		g_k7_sink += k7_chase (start, nodes < 8192 ? nodes : 8192);   /* (warm up) */
		double best = 1e30;
		unsigned bm = 0, bs = 0;
		for (int r = 0; r < 3; r++) {
			if (g_k7_perf > 0) {
				scePerfArmPmonSetCounterValue (SCE_PERF_ARM_PMON_THREAD_ID_SELF, 0, 0);
				scePerfArmPmonSetCounterValue (SCE_PERF_ARM_PMON_THREAD_ID_SELF, 1, 0);
				scePerfArmPmonStart (SCE_PERF_ARM_PMON_THREAD_ID_SELF);
			}
			SceUInt64 t0 = sceKernelGetProcessTimeWide ();
			g_k7_sink += k7_chase (start, steps);
			double ms = k7_ms (t0);
			unsigned m0 = 0, m1 = 0;
			if (g_k7_perf > 0) {
				scePerfArmPmonStop (SCE_PERF_ARM_PMON_THREAD_ID_SELF);
				scePerfArmPmonGetCounterValue (SCE_PERF_ARM_PMON_THREAD_ID_SELF, 0, &m0);
				scePerfArmPmonGetCounterValue (SCE_PERF_ARM_PMON_THREAD_ID_SELF, 1, &m1);
			}
			if (ms < best) { best = ms; bm = m0; bs = m1; }
		}
		if (pass == 0) { t_page = best * 1e6 / steps; miss_page = bm; stall_page = bs; }
		else { t_line = best * 1e6 / steps; miss_line = bm; stall_line = bs; }
	}
	sceKernelFreeMemBlock (u);
	char pmu [160] = "";
	if (g_k7_perf > 0)
		snprintf (pmu, sizeof pmu, "; DTLB misses per step %.2f / %.2f, main-TLB stall cycles per step %.1f / %.1f", miss_page / 65536.0, miss_line / 65536.0,
			  stall_page / 65536.0, stall_line / 65536.0);
	LOG ("PRB pages %-34s base %p: walk one node per 4 KiB page over 16 MiB %.0f ns per step, one node per 32-byte line over 4 MiB %.0f ns per step%s",
	     what, (void *) b, t_page, t_line, pmu);
}
static void k7_probe_pages (void)
{
	SceUInt64 t0 = sceKernelGetProcessTimeWide ();
	k7_perf_load ();
	unsigned *scratch = k7_scratch ((4u * MiB / 32u) * 2u * sizeof (unsigned));
	if (!scratch) { LOG ("PRB pages: no 1 MiB scratch block"); return; }
	k7_walk ("USER_RW (default)", SCE_KERNEL_MEMBLOCK_TYPE_USER_RW, 4096, 0, scratch);
	k7_walk ("USER_RW aligned to 1 MiB", SCE_KERNEL_MEMBLOCK_TYPE_USER_RW, 1024 * 1024, 1, scratch);
	k7_walk ("USER_MAIN_PHYCONT_RW (contiguous)", SCE_KERNEL_MEMBLOCK_TYPE_USER_MAIN_PHYCONT_RW, 1024 * 1024, 0, scratch);
	k7_scratch_free ();
	if (g_k7_perf > 0) {
		sceSysmoduleUnloadModule (SCE_SYSMODULE_PERF);
		g_k7_perf = 0;
	}
	LOG ("PRB pages verdict: the same walk on each mapping (only how the addresses are mapped differs): a contiguous / 1 MiB-aligned block that walks "
	     "clearly faster per page step than default USER_RW = the kernel maps it with bigger entries (64 KiB pages / 1 MiB sections), so a heap "
	     "placed there would save the page-table lookups; equal times = no difference to gain | %.0f ms", k7_ms (t0));
}

/* ---- storage read test (at the end) */
#define K7_BUF (2u * MiB)
typedef struct { char dev [8], path [96]; int mounted, wrote; SceOff size; unsigned cluster; double free_gib, total_gib; } K7Store;
static double k7_read_seq (SceUID fd, char *buf, SceOff off, SceOff bytes, unsigned rs)
{
	SceUInt64 t0 = sceKernelGetProcessTimeWide ();
	SceOff done = 0;
	while (done < bytes) {
		unsigned n = (SceOff) rs < bytes - done ? rs : (unsigned) (bytes - done);
		int r = sceIoPread (fd, buf, n, off + done);
		if (r <= 0) break;
		done += r;
	}
	double s = (double) (sceKernelGetProcessTimeWide () - t0) / 1e6;
	return done > 0 && s > 0 ? done / (double) MiB / s : 0;
}
static int k7_store_keep (void) { const char *e = getenv ("SHEPHERD_K7_STORE_KEEP"); return e && !strcmp (e, "1"); }
/* (kit #7 integration review) SHEPHERD_K7_STORE_WRITE names the stores the app may write its own test file to: <store>:<MiB>[,<store>:<MiB>]
 * (64..1024 MiB); a bare number = imc0 only (the PCH-2000's built-in store, the one with no file on it). grw0 (a game card's writable area)
 * never, named or not. 0 = this store is not named. */
static unsigned k7_store_write_mib (const char *dev)
{
	const char *e = getenv ("SHEPHERD_K7_STORE_WRITE");
	if (!e || !*e || !strcmp (dev, "grw0"))
		return 0;
	unsigned long w = 0;
	size_t dl = strlen (dev);
	if (!strchr (e, ':'))
		w = strcmp (dev, "imc0") ? 0 : strtoul (e, NULL, 10);
	else
		for (const char *p = e; p && *p; p = strchr (p, ','), p = p ? p + 1 : NULL) {
			while (*p == ' ')
				p++;
			if (!strncmp (p, dev, dl) && p [dl] == ':') { w = strtoul (p + dl + 1, NULL, 10); break; }
		}
	return !w ? 0 : w < 64 ? 64 : w > 1024 ? 1024 : (unsigned) w;
}
static int k7_store_file (K7Store *st, char *buf)
{
	unsigned sz = 0;
	if (!strcmp (st->dev, "ux0") && file_size (DIR_ "/pack/shepherd.pack", &sz) == 0 && sz >= 32u * MiB) {
		snprintf (st->path, sizeof st->path, DIR_ "/pack/shepherd.pack");
		st->size = sz;
		return 1;
	}
	/* (kit #7 integration review) the app's own test file has its own name, so a leftover (a run stopped during its test, or one with
	 * SHEPHERD_K7_STORE_KEEP=1) is never taken for the owner's readtest.bin: deleted here unless KEEP=1 (then measured again) */
	char own [96];
	snprintf (own, sizeof own, "%s:data/shepherd/readtest-shepherd.bin", st->dev);
	unsigned osz = 0;
	int have_own = strcmp (st->dev, "grw0") && file_size (own, &osz) == 0;
	if (have_own && !(k7_store_keep () && osz >= 32u * MiB)) {
		sceIoRemove (own);
		LOG ("PRB store %s: a leftover %s (%u MiB) deleted", st->dev, own, osz / MiB);
		have_own = 0;
	}
	snprintf (st->path, sizeof st->path, "%s:data/shepherd/readtest.bin", st->dev);
	if (file_size (st->path, &sz) == 0 && sz >= 32u * MiB) { st->size = sz; return 1; }
	snprintf (st->path, sizeof st->path, "%s", own);
	if (have_own) {
		LOG ("PRB store %s: its own test file from an earlier run, %u MiB, measured again (SHEPHERD_K7_STORE_KEEP=1)", st->dev, osz / MiB);
		st->size = osz; return 1;
	}
	unsigned w = k7_store_write_mib (st->dev);
	if (!w) return 0;
	double margin = st->total_gib * 1024.0 * 0.10 > 64.0 ? st->total_gib * 1024.0 * 0.10 : 64.0;
	if (st->free_gib * 1024.0 < w + margin) {
		LOG ("PRB store %s: its own %u MiB test file NOT written: %.0f MiB free, %.0f MiB wanted (the file + a margin of max (64 MiB, 10 %% of the store))",
		     st->dev, w, st->free_gib * 1024.0, w + margin);
		return 0;
	}
	char d [40];
	snprintf (d, sizeof d, "%s:data", st->dev); sceIoMkdir (d, 0777);
	snprintf (d, sizeof d, "%s:data/shepherd", st->dev); sceIoMkdir (d, 0777);
	SceUID fd = sceIoOpen (st->path, SCE_O_WRONLY | SCE_O_CREAT | SCE_O_TRUNC, 0777);
	if (fd < 0) { LOG ("PRB store %s: cannot create %s (0x%08x)", st->dev, st->path, (unsigned) fd); return 0; }
	SceUInt64 t0 = sceKernelGetProcessTimeWide ();
	unsigned *u = (unsigned *) buf;
	unsigned done = 0;
	for (unsigned m = 0; m < w; m += K7_BUF / MiB) {
		for (unsigned i = 0; i < K7_BUF / 4; i++) u [i] = k7_rand ();
		int r = sceIoWrite (fd, buf, K7_BUF);
		if (r != (int) K7_BUF) break;
		done += K7_BUF / MiB;
	}
	sceIoClose (fd);
	double s = (double) (sceKernelGetProcessTimeWide () - t0) / 1e6;
	LOG ("PRB store %s: wrote its own test file %s, %u of %u MiB in %.1f s (%.1f MiB/s written; SHEPHERD_K7_STORE_WRITE)", st->dev, st->path, done, w, s, s > 0 ? done / s : 0);
	if (done < w) { sceIoRemove (st->path); LOG ("PRB store %s: the write stopped early: file deleted, not measured", st->dev); return 0; }
	st->size = (SceOff) done * MiB;
	st->wrote = 1;
	return 1;
}
static void k7_probe_store_one (K7Store *st, char *buf, SceUID out)
{
	SceUInt64 t0 = sceKernelGetProcessTimeWide ();
	SceUID fd = sceIoOpen (st->path, SCE_O_RDONLY, 0);
	if (fd < 0) { LOG ("PRB store %s: cannot open %s (0x%08x)", st->dev, st->path, (unsigned) fd); return; }
	SceOff seqb = st->size > (SceOff) 400 * MiB ? (SceOff) 400 * MiB : st->size;
	double seq = k7_read_seq (fd, buf, 0, seqb, MiB);
	/* read sizes: 4 KiB .. 2 MiB, sequential, each from its own region of the file (no region read twice in a row) */
	static const unsigned sizes [] = { 4096, 16384, 65536, 131072, 262144, 524288, 1048576, 2097152 };
	double sp [8], best = 0;
	SceOff region = st->size / 9 > (SceOff) 8 * MiB ? (SceOff) 8 * MiB : (st->size / 9) & ~(SceOff) 0xfffff;
	char sweep [320] = "";
	for (unsigned i = 0; i < 8; i++) {
		SceOff bytes = sizes [i] <= 16384 ? (region > (SceOff) 2 * MiB ? (SceOff) 2 * MiB : region) : region;
		sp [i] = k7_read_seq (fd, buf, (SceOff) i * region, bytes, sizes [i]);
		if (sp [i] > best) best = sp [i];
		size_t sn = strlen (sweep);
		snprintf (sweep + sn, sizeof sweep - sn, "%s%u KiB %.1f", i ? ", " : "", sizes [i] / 1024, sp [i]);
	}
	unsigned fastest = sizes [7];
	for (unsigned i = 0; i < 8; i++) if (sp [i] >= best * 0.95) { fastest = sizes [i]; break; }
	/* block alignment: 64 KiB reads at 1 MiB-aligned offsets + 0 / 512 B / 4 KiB / 32 KiB, 4 MiB each, from the last region */
	static const unsigned shifts [] = { 0, 512, 4096, 32768 };
	double al [4];
	SceOff abase = (SceOff) 8 * region;
	if (abase + 5 * (SceOff) MiB > st->size) abase = 0;
	for (unsigned i = 0; i < 4; i++) {
		SceUInt64 t1 = sceKernelGetProcessTimeWide ();
		SceOff done = 0;
		for (unsigned k = 0; k < 64; k++) {
			int r = sceIoPread (fd, buf, 65536, abase + (SceOff) k * 65536 + shifts [i]);
			if (r <= 0) break;
			done += r;
		}
		double s = (double) (sceKernelGetProcessTimeWide () - t1) / 1e6;
		al [i] = s > 0 ? done / (double) MiB / s : 0;
	}
	unsigned align = 512;
	for (int i = 3; i >= 1; i--) if (al [i] < al [0] * 0.95) { align = i == 3 ? 65536 : shifts [i + 1]; break; }
	/* random reads: 4 KiB x 256 and 128 KiB x 64 at random aligned offsets over the whole file */
	double rnd [2];
	static const unsigned rsz [2] = { 4096, 131072 }, rcount [2] = { 256, 64 };
	for (unsigned i = 0; i < 2; i++) {
		SceOff span = st->size / rsz [i] - 1;
		SceUInt64 t1 = sceKernelGetProcessTimeWide ();
		for (unsigned k = 0; k < rcount [i]; k++)
			sceIoPread (fd, buf, rsz [i], (SceOff) (((unsigned long long) k7_rand () << 16 ^ k7_rand ()) % (unsigned long long) span) * rsz [i]);
		rnd [i] = (double) (sceKernelGetProcessTimeWide () - t1) / 1000.0 / rcount [i];
	}
	sceIoClose (fd);
	LOG ("PRB store %s: %s (%.0f MiB): sequential 1 MiB reads %.1f MiB/s over %.0f MiB | read sizes (MiB/s): %s -> fastest %u KiB | 64 KiB reads shifted "
	     "by 0 / 512 B / 4 KiB / 32 KiB: %.1f / %.1f / %.1f / %.1f MiB/s -> block alignment %u B (devctl cluster %u B) | random 4 KiB %.2f ms, 128 KiB %.2f ms "
	     "per read (%.1f MiB/s) | %.1f s", st->dev, st->path, st->size / (double) MiB, seq, seqb / (double) MiB, sweep, fastest / 1024, al [0], al [1], al [2],
	     al [3], align, st->cluster, rnd [0], rnd [1], rnd [1] > 0 ? 0.125 / (rnd [1] / 1000.0) : 0, k7_ms (t0) / 1000.0);
	if (out >= 0) {
		char l [512];
		int n = snprintf (l, sizeof l, "speed.%s=seq1M %.1f MiB/s; fastest %u KiB (%.1f MiB/s); align %u B; cluster %u B; rand4K %.2f ms; rand128K %.2f ms; "
				  "file %s %.0f MiB; free %.2f of %.2f GiB\n", st->dev, seq, fastest / 1024, best, align, st->cluster, rnd [0], rnd [1], st->path,
				  st->size / (double) MiB, st->free_gib, st->total_gib);
		if (n > 0) sceIoWrite (out, l, (unsigned) n < sizeof l ? (unsigned) n : sizeof l - 1);
	}
	if (st->wrote && !k7_store_keep ()) {
		sceIoRemove (st->path);
		LOG ("PRB store %s: its test file deleted again (SHEPHERD_K7_STORE_KEEP=1 keeps it)", st->dev);
	}
}
static void k7_probe_storage (void)
{
	SceUInt64 t0 = sceKernelGetProcessTimeWide ();
	SceUID mb = sceKernelAllocMemBlock ("shepherd-probe-io", SCE_KERNEL_MEMBLOCK_TYPE_USER_RW, K7_BUF, NULL);
	void *m = NULL;
	char *buf = NULL, *heap = NULL;
	if (mb >= 0 && sceKernelGetMemBlockBase (mb, &m) >= 0) buf = (char *) m;
	else { if (mb >= 0) sceKernelFreeMemBlock (mb); mb = -1; buf = heap = (char *) memalign (65536, K7_BUF); }
	if (!buf) { LOG ("PRB store: no 2 MiB read buffer (free user / newlib): skipped"); return; }
	sceIoMkdir (DIR_ "/probes", 0777);
	SceUID out = sceIoOpen (DIR_ "/probes/storage.tmp", SCE_O_WRONLY | SCE_O_CREAT | SCE_O_TRUNC, 0777);
	if (out >= 0) {
		char h [200];
		int n = snprintf (h, sizeof h, "# Shepherd storage read test (kit #7): written by Shepherd Measure at the end of its run; the device profile copies these lines\n"
				  "measured.by=%s build %s\n", SHP_LOG_NAME, SHP_BUILD_ID);
		if (n > 0) sceIoWrite (out, h, (unsigned) n);
	}
	static const char *const devs [] = { "ux0", "imc0", "uma0", "grw0", "xmc0" };
	int tested = 0;
	for (unsigned i = 0; i < sizeof devs / sizeof devs [0]; i++) {
		K7Store st;
		memset (&st, 0, sizeof st);
		snprintf (st.dev, sizeof st.dev, "%s", devs [i]);
		char dv [8];
		snprintf (dv, sizeof dv, "%s:", devs [i]);
		SceIoDevInfo di;
		memset (&di, 0, sizeof di);
		int r = sceIoDevctl (dv, 0x3001, NULL, 0, &di, sizeof di);
		if (r < 0 || di.max_size <= 0) { LOG ("PRB store %s: not mounted (devctl 0x%08x)", st.dev, (unsigned) r); continue; }
		st.mounted = 1; st.cluster = (unsigned) di.cluster_size; st.free_gib = di.free_size / 1073741824.0; st.total_gib = di.max_size / 1073741824.0;
		if (!k7_store_file (&st, buf)) {
			LOG ("PRB store %s: mounted (%.2f of %.2f GiB free, cluster %u B) but no test file: copy a 100-400 MB file to %s:data/shepherd/readtest.bin "
			     "(e.g. made on the Mac with: dd if=/dev/urandom of=readtest.bin bs=1m count=256), or SHEPHERD_K7_STORE_WRITE=%s:256 in "
			     SHP_ENV_NAME " lets the app write its own there (with room to spare; deleted after; never on grw0)", st.dev, st.free_gib, st.total_gib,
			     st.cluster, st.dev, st.dev);
			if (out >= 0) {
				char l [160];
				int n = snprintf (l, sizeof l, "speed.%s=not measured (no test file); free %.2f of %.2f GiB; cluster %u B\n", st.dev, st.free_gib, st.total_gib, st.cluster);
				if (n > 0) sceIoWrite (out, l, (unsigned) n);
			}
			continue;
		}
		k7_probe_store_one (&st, buf, out);
		tested++;
	}
	if (out >= 0) {
		sceIoClose (out);
		sceIoRemove (DIR_ "/probes/storage.txt");
		sceIoRename (DIR_ "/probes/storage.tmp", DIR_ "/probes/storage.txt");
	}
	if (mb >= 0) sceKernelFreeMemBlock (mb);
	free (heap);
	LOG ("PRB store verdict: %d store(s) measured -> " DIR_ "/probes/storage.txt (the next device profile carries it) | %.1f s", tested, k7_ms (t0) / 1000.0);
}

/* ---- NGS check (at the end). The public NGS ABI as the console implements it (the vitasdk headers declare only the Internal names, without
 * structs); our own declarations, checked against every return code -- a refusal ends the probe, it never guesses on. */
typedef struct { int32_t max_racks, max_voices, granularity, sample_rate, max_modules; } K7NgsSys;
typedef struct { void *data; uint32_t size; } K7NgsBuf;
typedef struct { const void *def; int32_t voices, channels, max_patches_per_input, patches_per_output; void *user; } K7NgsRack;
typedef struct { uint32_t src; int32_t out_index, out_sub; uint32_t dst; int32_t in_index; } K7NgsPatch;
typedef struct { int32_t module_id, channel; } K7NgsHdr;
typedef struct { const void *buffer; int32_t bytes; int16_t loops, next; } K7NgsPlayerBuf;
typedef struct {
	K7NgsHdr hdr;
	uint32_t desc_id, desc_size;
	K7NgsPlayerBuf buf [4];
	float freq, scalar;
	int32_t lead_in, limit, start_bytes;
	int8_t channels, map [2], type, res, start_buffer, pad [2];
} K7NgsPlayer;
int sceNgsSystemGetRequiredMemorySize (const K7NgsSys *p, uint32_t *size);
int sceNgsSystemInit (void *mem, uint32_t size, const K7NgsSys *p, uint32_t *sys);
int sceNgsSystemUpdate (uint32_t sys);
int sceNgsSystemRelease (uint32_t sys);
int sceNgsRackGetRequiredMemorySize (uint32_t sys, const K7NgsRack *d, uint32_t *size);
int sceNgsRackInit (uint32_t sys, K7NgsBuf *buf, const K7NgsRack *d, uint32_t *rack);
int sceNgsRackGetVoiceHandle (uint32_t rack, uint32_t index, uint32_t *voice);
int sceNgsRackRelease (uint32_t rack, void *cb);
const void *sceNgsVoiceDefGetMasterBuss (void);
const void *sceNgsVoiceDefGetSimpleVoice (void);
int sceNgsPatchCreateRouting (const K7NgsPatch *info, uint32_t *patch);
int sceNgsVoiceSetParamsBlock (uint32_t voice, const K7NgsHdr *hdr, uint32_t size, int32_t *nerr);
int sceNgsVoicePlay (uint32_t voice);
int sceNgsVoiceKill (uint32_t voice);

static volatile int g_k7_spin_run, g_k7_spin_ok [3];
static volatile unsigned g_k7_spin_count [3];
static void *k7_spinner (void *arg)
{
	int k = (int) (intptr_t) arg;
	g_k7_spin_ok [k] = sceKernelChangeThreadCpuAffinityMask (sceKernelGetThreadId (), 0x20000 << k) >= 0;   /* USER_1, USER_2, CPU3 */
	if (!g_k7_spin_ok [k])
		return NULL;   /* (CPU3 without the core-unlock plugin: refused) */
	unsigned c = 0;
	while (g_k7_spin_run) { c++; if (!(c & 0xfff)) g_k7_spin_count [k] = c; }
	g_k7_spin_count [k] = c;
	return NULL;
}
static SceUInt64 k7_runclocks (void)
{
	SceKernelThreadInfo ti;
	memset (&ti, 0, sizeof ti);
	ti.size = sizeof ti;
	return sceKernelGetThreadInfo (sceKernelGetThreadId (), &ti) >= 0 ? (SceUInt64) ti.runClocks : 0;
}
static void k7_probe_ngs (void)
{
	SceUInt64 t0 = sceKernelGetProcessTimeWide ();
	int r = sceSysmoduleLoadModule (SCE_SYSMODULE_NGS);
	if (r < 0) { LOG ("PRB ngs: the NGS module does not load (sysmodule 0x%08x): skipped", (unsigned) r); return; }
	K7NgsSys sp = { 4, 80, 512, 48000, 14 };
	uint32_t ssz = 0, sys = 0, rack_m = 0, rack_v = 0, master = 0;
	void *smem = NULL, *mmem = NULL, *vmem = NULL;
	short *pcm = NULL;
	const char *stop = NULL;
	int rc = 0, nplay = 0;
	enum { NV = 64 };
	uint32_t voice [NV];
	if ((rc = sceNgsSystemGetRequiredMemorySize (&sp, &ssz)) < 0) { stop = "system memory size"; goto done; }
	if (!(smem = memalign (256, ssz))) { stop = "system memory"; goto done; }
	memset (smem, 0, ssz);
	if ((rc = sceNgsSystemInit (smem, ssz, &sp, &sys)) < 0) { stop = "sceNgsSystemInit"; goto done; }
	{
		K7NgsRack rd = { sceNgsVoiceDefGetMasterBuss (), 1, 2, NV, 0, NULL };
		uint32_t msz = 0;
		if (!rd.def) { stop = "master buss definition"; goto done; }
		if ((rc = sceNgsRackGetRequiredMemorySize (sys, &rd, &msz)) < 0) { stop = "master rack size"; goto done; }
		if (!(mmem = memalign (256, msz))) { stop = "master rack memory"; goto done; }
		memset (mmem, 0, msz);
		K7NgsBuf bi = { mmem, msz };
		if ((rc = sceNgsRackInit (sys, &bi, &rd, &rack_m)) < 0) { stop = "master sceNgsRackInit"; goto done; }
		if ((rc = sceNgsRackGetVoiceHandle (rack_m, 0, &master)) < 0) { stop = "master voice handle"; goto done; }
		K7NgsRack vd = { sceNgsVoiceDefGetSimpleVoice (), NV, 1, 0, 1, NULL };
		uint32_t vsz = 0;
		if (!vd.def) { stop = "simple voice definition"; goto done; }
		if ((rc = sceNgsRackGetRequiredMemorySize (sys, &vd, &vsz)) < 0) { stop = "voice rack size"; goto done; }
		if (!(vmem = memalign (256, vsz))) { stop = "voice rack memory"; goto done; }
		memset (vmem, 0, vsz);
		K7NgsBuf bv = { vmem, vsz };
		if ((rc = sceNgsRackInit (sys, &bv, &vd, &rack_v)) < 0) { stop = "voice sceNgsRackInit"; goto done; }
		LOG ("PRB ngs: up -- system %u B, master rack %u B, %d-voice rack %u B (granularity 512 samples = %.2f ms at 48 kHz)", (unsigned) ssz, (unsigned) msz, NV,
		     (unsigned) vsz, 512 * 1000.0 / 48000);
	}
	/* a synthetic 1 s mono PCM16 buffer (a quiet saw), looped; no output port is opened: nothing is heard */
	if (!(pcm = (short *) malloc (48000 * sizeof (short)))) { stop = "pcm"; goto done; }
	for (int i = 0; i < 48000; i++) pcm [i] = (short) ((i * 37 % 2000) - 1000);
	for (int v = 0; v < NV; v++) {
		if ((rc = sceNgsRackGetVoiceHandle (rack_v, (uint32_t) v, &voice [v])) < 0) { stop = "voice handle"; goto done; }
		K7NgsPatch pi = { voice [v], 0, 0, master, 0 };
		uint32_t patch = 0;
		if ((rc = sceNgsPatchCreateRouting (&pi, &patch)) < 0) { stop = "sceNgsPatchCreateRouting"; goto done; }
		K7NgsPlayer pp;
		memset (&pp, 0, sizeof pp);
		pp.hdr.module_id = 0; pp.hdr.channel = 0;
		pp.desc_id = 0x01015CE6u; pp.desc_size = sizeof pp - sizeof pp.hdr;
		pp.buf [0].buffer = pcm; pp.buf [0].bytes = 48000 * (int32_t) sizeof (short); pp.buf [0].loops = -1; pp.buf [0].next = 0;
		pp.freq = 48000.0f; pp.scalar = 1.0f; pp.channels = 1; pp.map [0] = 0; pp.map [1] = 0; pp.type = 0;
		int32_t nerr = 0;
		if ((rc = sceNgsVoiceSetParamsBlock (voice [v], &pp.hdr, sizeof pp, &nerr)) < 0) { stop = "sceNgsVoiceSetParamsBlock (player)"; goto done; }
	}
	{
		static const int counts [3] = { 0, 16, NV };
		const int updates = 150;
		for (int ci = 0; ci < 3; ci++) {
			while (nplay < counts [ci]) {
				if ((rc = sceNgsVoicePlay (voice [nplay])) < 0) { stop = "sceNgsVoicePlay"; goto done; }
				nplay++;
			}
			for (int w = 0; w < 4; w++) sceNgsSystemUpdate (sys);   /* (warm up) */
			/* spinners on the other cores; first a quiet window as long as the update loop (the probe thread asleep), then the updates.
			 * (kit #7 review: they can starve the P3 watchdog on its core for over 1.5 s -- it books nothing until they stop) */
			k7_susp_hold (1);
			g_k7_spin_run = 1;
			pthread_t th [3];
			int nth = 0;
			for (int k = 0; k < 3; k++) {
				g_k7_spin_count [k] = 0; g_k7_spin_ok [k] = 0;
				pthread_attr_t a;
				pthread_attr_init (&a);
				pthread_attr_setstacksize (&a, 16 * 1024);
				if (pthread_create (&th [nth], &a, k7_spinner, (void *) (intptr_t) k) == 0) nth++;
				pthread_attr_destroy (&a);
			}
			int mask0 = sceKernelGetThreadCpuAffinityMask (sceKernelGetThreadId ());
			sceKernelChangeThreadCpuAffinityMask (sceKernelGetThreadId (), 0x10000);   /* the probe thread on USER_0 */
			sceKernelDelayThread (50 * 1000);
			unsigned q0 [3], q1 [3], u1 [3];
			for (int k = 0; k < 3; k++) q0 [k] = g_k7_spin_count [k];
			SceUInt64 tq = sceKernelGetProcessTimeWide ();
			sceKernelDelayThread (updates * 2000);
			double quiet = k7_ms (tq);
			for (int k = 0; k < 3; k++) q1 [k] = g_k7_spin_count [k];
			SceUInt64 rc0 = k7_runclocks (), tu = sceKernelGetProcessTimeWide ();
			int bad = 0;
			for (int u = 0; u < updates; u++) if (sceNgsSystemUpdate (sys) < 0) bad++;
			double wall = k7_ms (tu);
			SceUInt64 rc1 = k7_runclocks ();
			for (int k = 0; k < 3; k++) u1 [k] = g_k7_spin_count [k];
			g_k7_spin_run = 0;
			for (int k = 0; k < nth; k++) pthread_join (th [k], NULL);
			k7_susp_hold (0);
			sceKernelChangeThreadCpuAffinityMask (sceKernelGetThreadId (), mask0 > 0 ? mask0 : 0);   /* (back to what it had) */
			char sp3 [160] = "";
			for (int k = 0; k < nth; k++) {
				double rq = (q1 [k] - q0 [k]) / (quiet > 0 ? quiet : 1), ru = (u1 [k] - q1 [k]) / (wall > 0 ? wall : 1);
				size_t sn = strlen (sp3);
				if (!g_k7_spin_ok [k]) snprintf (sp3 + sn, sizeof sp3 - sn, "%s%s n/a (not open)", k ? ", " : "", k == 0 ? "USER_1" : k == 1 ? "USER_2" : "CPU3");
				else snprintf (sp3 + sn, sizeof sp3 - sn, "%s%s %.0f%%", k ? ", " : "", k == 0 ? "USER_1" : k == 1 ? "USER_2" : "CPU3", rq > 0 ? 100.0 * ru / rq : 0);
			}
			double cpu = (double) (rc1 - rc0) / 1000.0;
			LOG ("PRB ngs %2d voices playing: %d updates of 512 samples in %.1f ms = %.3f ms each (%.1f %% of the 10.67 ms they make)%s; the calling thread's own "
			     "CPU time %.1f ms (%.0f %% of the wall time); spinner rate on the other cores vs a quiet window: %s", counts [ci], updates, wall, wall / updates,
			     100.0 * (wall / updates) / (512 * 1000.0 / 48000), bad ? " (some updates refused)" : "", cpu, wall > 0 ? 100.0 * cpu / wall : 0, sp3);
		}
	}
done:
	if (stop)
		LOG ("PRB ngs: stopped at %s (0x%08x): no further NGS calls", stop, (unsigned) rc);
	for (int v = 0; v < nplay; v++) sceNgsVoiceKill (voice [v]);
	if (rack_v) sceNgsRackRelease (rack_v, NULL);
	if (rack_m) sceNgsRackRelease (rack_m, NULL);
	if (sys) sceNgsSystemRelease (sys);
	free (vmem); free (mmem); free (smem); free (pcm);
	sceSysmoduleUnloadModule (SCE_SYSMODULE_NGS);
	if (!stop)
		LOG ("PRB ngs verdict: the calling thread's CPU time close to the update's wall time = NGS mixes on the ARM core that calls sceNgsSystemUpdate "
		     "(no offload: the kit #9 audio work stays on our cores); far below it = it waits while another processor mixes; a spinner rate well "
		     "under 100 %% = it also took an ARM core of ours | %.0f ms", k7_ms (t0));
}

/* ---- the GPU transfer unit (at the end; vitaGL's GXM): vita/gltest/shepgl/shepgl_vita.c */
extern int sgl_k7_transfer_probe (void (*line) (const char *));

static void k7_probes_start (void)
{
	if (!k7_probe_on ("mem") && !k7_probe_on ("pages")) {
		LOG ("PRB (kit #7 device probes) off at start (SHEPHERD_K7_PROBES=%s)", getenv ("SHEPHERD_K7_PROBES") ? getenv ("SHEPHERD_K7_PROBES") : "");
		return;
	}
	LOG ("PRB kit #7 device probes, part 1 of 2 (before the memory partition; every block is freed again; SHEPHERD_K7_PROBES=0 in " SHP_ENV_NAME
	     " skips them): memory budget, big memory pages");
	SceKernelFreeMemorySizeInfo f0, f1;
	k7_free_sizes (&f0);
	if (k7_probe_on ("mem")) k7_probe_memory ();
	if (k7_probe_on ("pages")) k7_probe_pages ();
	k7_free_sizes (&f1);   /* (the PMU library's load / unload included) */
	if (f1.size_user != f0.size_user || f1.size_cdram != f0.size_cdram || f1.size_phycont != f0.size_phycont)
		LOG ("WARN PRB free memory after part 1: user %u, cdram %u, phycont %u KiB -- before it %u / %u / %u KiB: the partition below gets what is free now",
		     (unsigned) f1.size_user / 1024, (unsigned) f1.size_cdram / 1024, (unsigned) f1.size_phycont / 1024, (unsigned) f0.size_user / 1024,
		     (unsigned) f0.size_cdram / 1024, (unsigned) f0.size_phycont / 1024);
	else
		LOG ("PRB part 1 done in %.1f s: the free memory is what it was before it (user %u, cdram %u, phycont %u KiB): the partition is unchanged",
		     wall_s (), (unsigned) f1.size_user / 1024, (unsigned) f1.size_cdram / 1024, (unsigned) f1.size_phycont / 1024);
}
static void k7_probes_end (int gl_ok_to_draw)
{
	int st = k7_probe_on ("store"), ng = k7_probe_on ("ngs"), gp = k7_probe_on ("gpu");
	if (!st && !ng && !gp)
		return;
	/* (no GL call here: the screen keeps the run's last frame until the green / red one -- so the measurement app's GL record stays the
	 *  same call for call, the probes included) */
	LOG ("PRB kit #7 device probes, part 2 of 2 (after the END line; the last picture stays on screen meanwhile, about 1-2 minutes, then green / "
	     "red as before): %s%s%s", st ? "storage read test " : "", ng ? "NGS check " : "", gp ? "GPU transfer unit" : "");
	if (st) k7_probe_storage ();
	if (ng) k7_probe_ngs ();
	if (gp) {
		if (gl_ok_to_draw) {
			SceUInt64 t0 = sceKernelGetProcessTimeWide ();
			int r = sgl_k7_transfer_probe (log_line);
			LOG ("PRB gpu verdict: %s | %.0f ms", r > 0 ? "the lines above (GXM transfer vs CPU vs sceDmacMemcpy; swizzle / downscale compared byte for byte)"
			     : r == 0 ? "not run (no vitaGL here: vlx's GL stand-in)" : "stopped (see above)", k7_ms (t0));
		} else
			LOG ("PRB gpu: skipped (vitaGL is not safe to use now: the render thread did not stop, or vitaGL is not up)");
	}
	LOG ("PRB kit #7 device probes done at %.1f s", wall_s ());
}
