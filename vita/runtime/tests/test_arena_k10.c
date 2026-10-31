/* SHEPHERD-VITA device kit #10, track memory (vita/notes/kit10-memory.md): Mac unit test of THE SHARED POOL (vita/patches/k10-memory.patch,
 * mono-mmap-vita.c policy 3 + mono_vita_vm_arena_pool_*). Run by run-tests-k10.sh against mono-mmap-vita.c with k9-gc.patch + k10-memory.patch.
 *   test_arena_k10 check   newlib (the host's sbrk model: pool_take below the floor, the floor raised ahead of it) grows from the low end
 *                          while an sgen-shaped workload (16 KiB block runs freed block by block, LOS sections, odd requests) runs top-down;
 *                          a shadow page map checks: no overlap between newlib's pages and the arena's, nothing of the arena below the floor
 *                          at the time it was placed, zero-fill of arena pages, newlib's pages outside used / peak, largest_free counts only
 *                          what lies at or above the floor, a floor raise over an arena page is refused (and changes nothing), hints are
 *                          ignored and fixed requests below the floor refused, every arena request top-down (no free run above it could
 *                          have held it); the two ends meet: newlib stops at the arena's lowest page, the arena at the floor
 *   test_arena_k10 policy0 the same arena with policy 0 = kit #9's first fit (the pool API refuses everything; nothing reached) */
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <stdint.h>
#include <glib.h>
#include "mono/utils/mono-mmap.h"
#include "mono/utils/mono-vita.h"
#include <psp2/kernel/sysmem.h>

void mono_account_mem (MonoMemAccountType type, ssize_t size) { }
gboolean mono_valloc_can_alloc (size_t size) { return TRUE; }
const char *mono_mem_account_type_name (MonoMemAccountType type) { return "test"; }
SceUID sceKernelAllocMemBlock (const char *name, int type, size_t size, void *opt) { return -1; }
int sceKernelGetMemBlockBase (SceUID uid, void **base) { return -1; }
int sceKernelFreeMemBlock (SceUID uid) { return 0; }

#define PAGE 4096u
#define BLK (16u * 1024)
static uint8_t *shadow, *base;   /* shadow: 0 free, 1 arena, 2 newlib */
static size_t npages;
static unsigned seed = 4711;
static unsigned rnd (void) { seed = seed * 1103515245 + 12345; return seed >> 8; }
static size_t nl_top;     /* newlib's top (bytes from base) */
static size_t floor_b;    /* the floor (bytes from base) */
static size_t reserve = 6u << 20;

typedef struct { uint8_t *p; size_t bytes; int type; } A;
#define MAXLIVE 8192
static A live [MAXLIVE];
static int nlive;
static long requests, refused, topdown_checked, newlib_stops, floor_refusals;

static void quiet (const char *event, const MonoVitaArenaStats *stats, size_t request) { }

static int highest_fit (uint8_t *p, size_t bytes, size_t align)
{
	size_t n = bytes / PAGE, fp = floor_b / PAGE;
	for (size_t s = npages; s-- > fp;) {
		uint8_t *at = base + s * PAGE;
		if (at <= p) return 1;
		if (s + n > npages || ((uintptr_t) at & (align - 1))) continue;
		int ok = 1;
		for (size_t i = 0; i < n && ok; ++i) ok = !shadow [s + i];
		if (ok) { printf ("FAIL not top-down: %p for %zu pages, but %p was free above it\n", p, n, at); return 0; }
	}
	return 1;
}

static int take_arena (uint8_t *p, size_t bytes, size_t align)
{
	size_t first = (size_t) (p - base) / PAGE, n = (bytes + PAGE - 1) / PAGE;
	if (p < base || (p - base) % PAGE || first + n > npages) { printf ("FAIL out of range %p\n", p); return 1; }
	if (first * PAGE < floor_b) { printf ("FAIL arena page %zu below the floor %zu\n", first, floor_b / PAGE); return 1; }
	for (size_t i = 0; i < n; ++i) { if (shadow [first + i]) { printf ("FAIL overlap at page %zu (%d)\n", first + i, shadow [first + i]); return 1; } }
	if (!highest_fit (p, n * PAGE, align)) return 1;
	topdown_checked++;
	for (size_t i = 0; i < n; ++i) shadow [first + i] = 1;
	for (size_t i = 0; i < n * PAGE; ++i) if (p [i]) { printf ("FAIL not zeroed\n"); return 1; }
	memset (p, 0xCD, n * PAGE);
	return 0;
}
static void give (uint8_t *p, size_t bytes, int type)
{
	size_t first = (size_t) (p - base) / PAGE, n = (bytes + PAGE - 1) / PAGE;
	mono_vfree (p, bytes, (MonoMemAccountType) type);
	for (size_t i = 0; i < n; ++i) shadow [first + i] = 0;
}

static int request (size_t bytes, size_t align, int type)
{
	requests++;
	uint8_t *p = mono_valloc_aligned (bytes, align, MONO_MMAP_READ | MONO_MMAP_WRITE, (MonoMemAccountType) type);
	if (!p) { refused++; return 0; }
	if (take_arena (p, bytes, align)) return 1;
	if (nlive < MAXLIVE) live [nlive++] = (A) { p, bytes, type };
	else give (p, bytes, type);
	return 0;
}

/* the host's sbrk model: newlib wants 'grow' more bytes; it raises the floor ahead of it (the reserve), then takes */
static int newlib_grow (size_t grow)
{
	size_t want = nl_top + grow;
	if (want + reserve > floor_b) {
		size_t nf = (want + reserve + PAGE - 1) / PAGE * PAGE;   /* (the host keeps the floor on a page) */
		if (nf > npages * PAGE) nf = npages * PAGE;
		/* try the full raise, else as far as the pages are free (one page at a time down) */
		while (nf > floor_b && mono_vita_vm_arena_pool_set_floor (nf) != 0) { floor_refusals++; nf -= PAGE; }
		if (nf > floor_b) {
			/* verify: [old floor, nf) was free in the shadow */
			for (size_t i = floor_b / PAGE; i < nf / PAGE; ++i) if (shadow [i]) { printf ("FAIL floor raised over arena page %zu\n", i); return 1; }
			floor_b = nf;
		}
	}
	if (want > floor_b) {
		if (mono_vita_vm_arena_pool_take (want) == 0) { printf ("FAIL pool_take above the floor accepted\n"); return 1; }
		newlib_stops++;
		return 0;
	}
	if (mono_vita_vm_arena_pool_take (want) != 0) { printf ("FAIL pool_take below the floor refused (top %zu want %zu floor %zu)\n", nl_top, want, floor_b); return 1; }
	size_t np = (want + PAGE - 1) / PAGE;
	for (size_t i = (nl_top + PAGE - 1) / PAGE; i < np; ++i) { if (shadow [i]) { printf ("FAIL newlib took arena page %zu\n", i); return 1; } shadow [i] = 2; }
	memset (base + nl_top, 0x5A, want - nl_top);   /* newlib writes its pages (no zero-fill promised) */
	nl_top = want;
	return 0;
}

static int cycle (void)
{
	unsigned r = rnd () % 100;
	if (r < 50 && request (32 * BLK, BLK, MONO_MEM_ACCOUNT_SGEN_MARKSWEEP)) return 1;
	if (r >= 50 && r < 62 && request (1u << 20, 1u << 20, MONO_MEM_ACCOUNT_SGEN_LOS)) return 1;
	if (r >= 62 && r < 68 && request ((1 + rnd () % 3) * (1u << 20) + (rnd () % 16) * PAGE, PAGE, MONO_MEM_ACCOUNT_SGEN_LOS)) return 1;
	if (r >= 68 && r < 75 && request ((1 + rnd () % 32) * PAGE, PAGE, MONO_MEM_ACCOUNT_OTHER)) return 1;
	if (r >= 75 && newlib_grow ((rnd () % 64 + 1) * 1024)) return 1;
	for (int k = 0; k < nlive;) {
		A a = live [k];
		if (a.type == MONO_MEM_ACCOUNT_SGEN_MARKSWEEP && rnd () % 100 < 20) {
			size_t nb = a.bytes / BLK;
			live [k] = live [--nlive];
			for (size_t b = 0; b < nb; ++b) {
				if (rnd () % 100 < 40) give (a.p + b * BLK, BLK, a.type);
				else if (nlive < MAXLIVE) live [nlive++] = (A) { a.p + b * BLK, BLK, a.type };
				else give (a.p + b * BLK, BLK, a.type);
			}
			continue;
		}
		if ((a.type == MONO_MEM_ACCOUNT_SGEN_LOS && rnd () % 100 < 15) || (a.type == MONO_MEM_ACCOUNT_OTHER && rnd () % 100 < 10)) {
			give (a.p, a.bytes, a.type);
			live [k] = live [--nlive];
			continue;
		}
		k++;
	}
	return 0;
}

static int check_stats (void)
{
	MonoVitaArenaStats st;
	mono_vita_vm_arena_get_stats (&st);
	size_t used = 0, nl = 0, largest = 0, run = 0;
	for (size_t i = 0; i < npages; ++i) {
		used += shadow [i] == 1; nl += shadow [i] == 2;
		if (!shadow [i] && i * PAGE >= floor_b) { run++; if (run > largest) largest = run; } else run = 0;
	}
	if (used * PAGE != st.used) { printf ("FAIL accounting: shadow arena %zu KiB, the arena says %zu KiB\n", used * 4, st.used / 1024); return 1; }
	if (largest * PAGE != st.largest_free) { printf ("FAIL largest_free: shadow %zu KiB (at or above the floor), the arena says %zu KiB\n", largest * 4, st.largest_free / 1024); return 1; }
	size_t tk = 0, fl = 0, lu = 0;
	mono_vita_vm_arena_pool_get (&tk, &fl, &lu);
	if (tk != (nl_top + PAGE - 1) / PAGE * PAGE || tk != nl * PAGE) { printf ("FAIL pool taken %zu vs newlib top %zu (%zu pages in the shadow)\n", tk, nl_top, nl); return 1; }
	if (fl != floor_b) { printf ("FAIL pool floor %zu vs %zu\n", fl, floor_b); return 1; }
	size_t lowest = npages * PAGE;
	for (size_t i = floor_b / PAGE; i < npages; ++i) if (shadow [i] == 1) { lowest = i * PAGE; break; }
	if (lu != lowest) { printf ("FAIL lowest_used %zu vs the shadow's %zu\n", lu, lowest); return 1; }
	return 0;
}

int main (int argc, char **argv)
{
	size_t size = 96u << 20;
	void *mem;
	if (posix_memalign (&mem, 1 << 22, size)) return 1;
	memset (mem, 0xAB, size);
	if (mono_vita_vm_arena_init (mem, size) != 0) { printf ("FAIL init\n"); return 1; }
	mono_vita_vm_arena_set_log (quiet, 1u << 30);
	MonoVitaArenaStats st;
	mono_vita_vm_arena_get_stats (&st);
	base = (uint8_t *) mono_vita_vm_arena_pool_base ();
	if (base != (uint8_t *) mem + (size - st.arena_size)) { printf ("FAIL pool_base %p\n", base); return 1; }
	npages = st.arena_size / PAGE;
	shadow = calloc (npages, 1);
	if (argc >= 2 && !strcmp (argv [1], "policy0")) {
		/* kit #9's policy: the pool API refuses everything, valloc is first fit from the bottom */
		if (mono_vita_vm_arena_pool_take (PAGE) == 0 || mono_vita_vm_arena_pool_set_floor (PAGE) == 0) { printf ("FAIL the pool API acted under policy 0\n"); return 1; }
		uint8_t *a = mono_valloc (NULL, 4 * PAGE, MONO_MMAP_READ | MONO_MMAP_WRITE, MONO_MEM_ACCOUNT_OTHER);
		if (a != base) { printf ("FAIL policy 0 is not first fit (%p vs %p)\n", a, base); return 1; }
		mono_vita_vm_arena_get_stats (&st);
		if (st.largest_free != st.arena_size - 4 * PAGE) { printf ("FAIL policy 0 largest_free %zu\n", st.largest_free); return 1; }
		printf ("policy 0 (kit #9): the pool API refused, first fit kept, largest_free unclipped: ok\n");
		return 0;
	}
	mono_vita_vm_arena_set_policy (MONO_VITA_ARENA_POOL, 0);
	/* the host's start: newlib's first pages (its pre-pool heap lives elsewhere), the floor a reserve above */
	if (newlib_grow (64 * 1024)) return 1;
	/* sgen's start: the card table, the nursery -- from the top */
	if (request (8u << 20, PAGE, MONO_MEM_ACCOUNT_SGEN_CARD_TABLE) || request (4u << 20, 4u << 20, MONO_MEM_ACCOUNT_SGEN_NURSERY)) return 1;
	/* hints are ignored, fixed requests below the floor refused */
	uint8_t *h = mono_valloc (base + floor_b + PAGE, PAGE, MONO_MMAP_READ | MONO_MMAP_WRITE, MONO_MEM_ACCOUNT_OTHER);
	if (!h || take_arena (h, PAGE, PAGE)) { printf ("FAIL hinted request\n"); return 1; }
	give (h, PAGE, MONO_MEM_ACCOUNT_OTHER);
	if (mono_valloc (base + PAGE, PAGE, MONO_MMAP_READ | MONO_MMAP_WRITE | MONO_MMAP_FIXED, MONO_MEM_ACCOUNT_OTHER)) { printf ("FAIL fixed request below the floor served\n"); return 1; }
	for (int it = 0; it < 6000; ++it) {
		if (cycle ()) return 1;
		if (it % 50 == 0 && check_stats ()) return 1;
	}
	if (check_stats ()) return 1;
	/* contention: newlib grows hard while the arena keeps its blocks -- the floor stops at the arena's lowest page, newlib at the floor */
	for (int it = 0; it < 40000 && newlib_stops < 20; ++it) {
		if (cycle ()) return 1;
		if (newlib_grow ((rnd () % 256 + 1) * 1024)) return 1;
		if (it % 50 == 0 && check_stats ()) return 1;
	}
	if (!newlib_stops) { printf ("FAIL the contention phase never stopped newlib at the floor\n"); return 1; }
	if (check_stats ()) return 1;
	/* a floor raise over an arena page is refused and changes nothing */
	size_t tk, fl, lu;
	mono_vita_vm_arena_pool_get (&tk, &fl, &lu);
	if (lu < npages * PAGE) {
		if (mono_vita_vm_arena_pool_set_floor (lu + PAGE) == 0) { printf ("FAIL a floor raise over an arena page was accepted\n"); return 1; }
		if (check_stats ()) return 1;
	}
	/* lowering the floor to newlib's top gives the reserve back to the arena */
	if (mono_vita_vm_arena_pool_set_floor (tk) != 0) { printf ("FAIL floor lowered to newlib's top refused\n"); return 1; }
	floor_b = tk;
	if (mono_vita_vm_arena_pool_set_floor (tk - PAGE) == 0) { printf ("FAIL floor below newlib's top accepted\n"); return 1; }
	if (check_stats ()) return 1;
	/* the ends meet: fill the arena with single pages until it refuses; then newlib can take nothing more */
	size_t filled = 0;
	for (;;) { uint8_t *p = mono_valloc (NULL, PAGE, MONO_MMAP_READ | MONO_MMAP_WRITE, MONO_MEM_ACCOUNT_OTHER); if (!p) break; if (take_arena (p, PAGE, PAGE)) return 1; filled++; }
	mono_vita_vm_arena_get_stats (&st);
	if (st.largest_free) { printf ("FAIL the arena refused with %zu KiB free above the floor\n", st.largest_free / 1024); return 1; }
	if (mono_vita_vm_arena_pool_set_floor (floor_b + PAGE) == 0 || mono_vita_vm_arena_pool_take (nl_top + PAGE * 2) == 0) { printf ("FAIL newlib grew into the full arena\n"); return 1; }
	if (check_stats ()) return 1;
	printf ("shared pool: %ld arena requests (%ld refused, %ld placements checked top-down above the floor), newlib grew to %zu KiB (%ld stops at the floor, %ld floor raises refused over arena pages), the ends met after %zu more pages: ok\n",
		requests, refused, topdown_checked, nl_top / 1024, newlib_stops, floor_refusals, filled);
	return 0;
}
