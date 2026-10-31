/* SHEPHERD-VITA device kit #9, track gc (vita/notes/kit9-gc.md): Mac unit test of M8, the TWO-ENDED arena (vita/patches/k9-gc.patch,
 * mono-mmap-vita.c mono_vita_vm_arena_set_policy). Run by run-tests-k9.sh against the patched mono-mmap-vita.c.
 *   test_arena_k9 check     policy 1: a randomized sgen-shaped workload (512 KiB runs of 16 KiB major-heap blocks freed block by block,
 *                           1 MiB LOS sections and big LOS objects, odd other requests) against a shadow page map -- no overlap, zero-fill,
 *                           accounting, full coalescing at the end; every LOS request lands at the HIGHEST fitting address (no free extent
 *                           above it could have held it); hinted / fixed requests keep their address; policy 0 = first fit exactly
 *                           (the same requests give the same addresses as before the patch: the kit #8 behaviour)
 *   test_arena_k9 frag <0|1> <seed>   the same workload under one policy: prints the requests that FAILED although the arena had enough
 *                           free pages in total (fragmentation failures), the mean / minimum largest free extent and the mean extent count */
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
static uint8_t *shadow, *base;
static size_t npages;
static unsigned seed = 12345;
static unsigned rnd (void) { seed = seed * 1103515245 + 12345; return seed >> 8; }

typedef struct { uint8_t *p; size_t bytes; int type; } A;
#define MAXLIVE 8192
static A live [MAXLIVE];
static int nlive;

static int take (uint8_t *p, size_t bytes)
{
	size_t first = (size_t) (p - base) / PAGE, n = (bytes + PAGE - 1) / PAGE;
	if (p < base || (p - base) % PAGE || first + n > npages) { printf ("FAIL out of range %p\n", p); return 1; }
	for (size_t i = 0; i < n; ++i) { if (shadow [first + i]) { printf ("FAIL overlap at page %zu\n", first + i); return 1; } shadow [first + i] = 1; }
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
static size_t free_pages (void) { size_t f = 0; for (size_t i = 0; i < npages; ++i) f += !shadow [i]; return f; }

/* no free run of the shadow map ABOVE p could hold a request of 'bytes' aligned to 'align' */
static int highest_fit (uint8_t *p, size_t bytes, size_t align)
{
	size_t n = bytes / PAGE;
	for (size_t s = npages; s-- > 0;) {
		uint8_t *at = base + s * PAGE;
		if (at <= p) break;
		if ((uintptr_t) at % align || s + n > npages) continue;
		size_t i = 0; while (i < n && !shadow [s + i]) i++;
		if (i == n) { printf ("FAIL a LOS request at %p, but %p above it was free\n", p, at); return 0; }
	}
	return 1;
}

static long fragfails, requests, fails, losfrag, majfrag;
static double sum_largest, sum_ext; static size_t min_largest = (size_t) -1; static long samples;

/* one request; check = verify the placement */
static int request (int policy, int check, size_t bytes, size_t align, int type)
{
	requests++;
	uint8_t *p = mono_valloc_aligned (bytes, align, MONO_MMAP_READ | MONO_MMAP_WRITE, (MonoMemAccountType) type);
	/* as sgen's ms_get_empty_block: a major-heap run that does not fit is asked for again at half the blocks, down to one block */
	while (!p && type == MONO_MEM_ACCOUNT_SGEN_MARKSWEEP && bytes > BLK) {
		bytes /= 2;
		p = mono_valloc_aligned (bytes, align, MONO_MMAP_READ | MONO_MMAP_WRITE, (MonoMemAccountType) type);
	}
	if (!p) {
		fails++;
		if (free_pages () * PAGE >= bytes + align) {
			fragfails++;
			if (type == MONO_MEM_ACCOUNT_SGEN_LOS) losfrag++;
			if (type == MONO_MEM_ACCOUNT_SGEN_MARKSWEEP) majfrag++;
		}
		return 0;
	}
	if (nlive >= MAXLIVE) { give (p, bytes, type); return 0; }
	if (check && take (p, bytes)) return 1;
	if (!check) { size_t first = (size_t) (p - base) / PAGE, n = (bytes + PAGE - 1) / PAGE; for (size_t i = 0; i < n; ++i) shadow [first + i] = 1; }
	if (check && ((policy == 1 && type == MONO_MEM_ACCOUNT_SGEN_LOS) || (policy == 2 && type == MONO_MEM_ACCOUNT_SGEN_MARKSWEEP)) && !highest_fit (p, (bytes + PAGE - 1) / PAGE * PAGE, align)) return 1;
	live [nlive++] = (A) { p, bytes, type };
	return 0;
}

/* one GC CYCLE of the sgen-shaped workload: the major heap grows by 512 KiB runs of 16 KiB blocks (as marksweep asks when its own free
 * blocks run out), the LOS takes 1 MiB sections and big objects, a few odd requests; then a "major collection" frees a random share of the
 * major heap's blocks ONE BY ONE (scattered over the runs, as sgen frees empty blocks) and of the LOS objects */
static int cycle (int policy, int check)
{
	/* grow until the arena is 75-95 % used (the device's heap runs near its cap: kit #2-#8 logs), at most 200 requests a cycle */
	unsigned target = 75 + rnd () % 21;
	for (int g = 0; g < 200 && (npages - free_pages ()) * 100 < (size_t) target * npages; ++g) {
		if (request (policy, check, 32 * BLK, BLK, MONO_MEM_ACCOUNT_SGEN_MARKSWEEP)) return 1;
		int r = rnd () % 100;
		if (r < 25 && request (policy, check, 1u << 20, 1u << 20, MONO_MEM_ACCOUNT_SGEN_LOS)) return 1;
		if (r >= 25 && r < 33 && request (policy, check, (1 + rnd () % 5) * (1u << 20) + (rnd () % 16) * PAGE, PAGE, MONO_MEM_ACCOUNT_SGEN_LOS)) return 1;
		if (r >= 33 && r < 40 && request (policy, check, (1 + rnd () % 32) * PAGE, PAGE, MONO_MEM_ACCOUNT_OTHER)) return 1;
	}
	/* the collection: each major block dies with probability d, each LOS object with probability l, other requests rarely */
	unsigned d = 15 + rnd () % 35, l = 20 + rnd () % 50;
	for (int k = 0; k < nlive; ) {
		A a = live [k];
		if (a.type == MONO_MEM_ACCOUNT_SGEN_MARKSWEEP) {
			size_t nb = a.bytes / BLK, b = 0;
			/* walk the run's blocks; free the dead ones; split the run into live pieces */
			live [k] = live [--nlive];
			size_t runstart = 0;
			for (b = 0; b <= nb; ++b) {
				int dead = b < nb && rnd () % 100 < d;
				if (b == nb || dead) {
					if (b > runstart && nlive < MAXLIVE) live [nlive++] = (A) { a.p + runstart * BLK, (b - runstart) * BLK, a.type };
					else if (b > runstart) give (a.p + runstart * BLK, (b - runstart) * BLK, a.type);
					if (dead) give (a.p + b * BLK, BLK, a.type);
					runstart = b + 1;
				}
			}
			continue;   /* (live [k] is now another entry, or a piece of this run appended at the end: both are visited once more at most) */
		}
		if ((a.type == MONO_MEM_ACCOUNT_SGEN_LOS && rnd () % 100 < l) || (a.type == MONO_MEM_ACCOUNT_OTHER && rnd () % 100 < 10)) {
			give (a.p, a.bytes, a.type);
			live [k] = live [--nlive];
			continue;
		}
		k++;
	}
	return 0;
}

static void quiet (const char *event, const MonoVitaArenaStats *stats, size_t request) { }

static int setup (int policy, size_t size)
{
	void *mem;
	if (posix_memalign (&mem, 1 << 22, size)) return 1;
	memset (mem, 0xAB, size);
	if (mono_vita_vm_arena_init (mem, size) != 0) { printf ("FAIL init\n"); return 1; }
	mono_vita_vm_arena_set_policy (policy, 0);
	mono_vita_vm_arena_set_log (quiet, 1u << 30);
	MonoVitaArenaStats st;
	mono_vita_vm_arena_get_stats (&st);
	base = (uint8_t *) mem + (size - st.arena_size);
	npages = st.arena_size / PAGE;
	shadow = calloc (npages, 1);
	/* what sgen takes first: the card table, the nursery */
	uint8_t *ct = mono_valloc_aligned (8u << 20, PAGE, MONO_MMAP_READ | MONO_MMAP_WRITE, MONO_MEM_ACCOUNT_SGEN_CARD_TABLE);
	uint8_t *nu = mono_valloc_aligned (4u << 20, 4u << 20, MONO_MMAP_READ | MONO_MMAP_WRITE, MONO_MEM_ACCOUNT_SGEN_NURSERY);
	if (!ct || !nu || take (ct, 8u << 20) || take (nu, 4u << 20)) { printf ("FAIL setup\n"); return 1; }
	return 0;
}

int main (int argc, char **argv)
{
	if (argc >= 2 && !strcmp (argv [1], "frag")) {
		int policy = argc > 2 ? atoi (argv [2]) : 0;
		unsigned s0 = seed = argc > 3 ? (unsigned) atoi (argv [3]) : 12345;
		if (setup (policy, 160u << 20)) return 1;
		for (int it = 0; it < 1500; ++it) {
			cycle (policy, 0);
			MonoVitaArenaStats st; mono_vita_vm_arena_get_stats (&st);
			sum_largest += st.largest_free; sum_ext += st.free_extents; samples++;
			if (it > 100 && st.largest_free < min_largest) min_largest = st.largest_free;
		}
		printf ("policy %d seed %u: %ld requests, %ld refused, %ld of them FRAGMENTATION FAILURES (enough free pages in total: LOS %ld, major heap %ld), largest free after a cycle mean %.1f MiB min %.1f MiB, extents mean %.1f\n",
			policy, s0, requests, fails, fragfails, losfrag, majfrag, sum_largest / samples / 1048576.0, min_largest / 1048576.0, sum_ext / samples);
		return 0;
	}
	/* check: policy 1 with every placement verified, then policy 0's first fit on a fresh arena (in a child: the arena is one per process) */
	if (setup (1, 96u << 20)) return 1;
	for (int it = 0; it < 400; ++it) if (cycle (1, 1)) return 1;
	MonoVitaArenaStats st; mono_vita_vm_arena_get_stats (&st);
	size_t used = 0; for (size_t i = 0; i < npages; ++i) used += shadow [i];
	if (used * PAGE != st.used) { printf ("FAIL accounting: shadow %zu KiB, arena %zu KiB\n", used * 4, st.used / 1024); return 1; }
	unsigned hn = 0; size_t lt = 0, hb = 0;
	mono_vita_vm_arena_get_ends (&hn, &lt, &hb);
	printf ("policy 1: %ld requests, %d live, used %zu KiB, %u extents, largest free %zu KiB; %u high-end allocations, low end reached %zu KiB, high end down to %zu KiB\n",
		requests, nlive, st.used / 1024, st.free_extents, st.largest_free / 1024, hn, lt / 1024, hb / 1024);
	if (hn == 0) { printf ("FAIL no allocation went to the high end\n"); return 1; }
	/* a hinted request keeps its address under policy 1 */
	for (int k = 0; k < nlive; ++k) give (live [k].p, live [k].bytes, live [k].type);
	nlive = 0;
	uint8_t *h = mono_valloc (NULL, 8 * PAGE, MONO_MMAP_READ | MONO_MMAP_WRITE, MONO_MEM_ACCOUNT_SGEN_LOS);
	mono_vfree (h, 8 * PAGE, MONO_MEM_ACCOUNT_SGEN_LOS);
	uint8_t *h2 = mono_valloc (h + 2 * PAGE, 2 * PAGE, MONO_MMAP_READ | MONO_MMAP_WRITE, MONO_MEM_ACCOUNT_SGEN_LOS);
	if (h2 != h + 2 * PAGE) { printf ("FAIL hint under policy 1: %p != %p\n", h2, h + 2 * PAGE); return 1; }
	mono_vfree (h2, 2 * PAGE, MONO_MEM_ACCOUNT_SGEN_LOS);
	/* back to policy 0: a LOS request is first fit again (the lowest free address that fits) */
	mono_vita_vm_arena_set_policy (0, 0);
	uint8_t *f = mono_valloc_aligned (1u << 20, 1u << 20, MONO_MMAP_READ | MONO_MMAP_WRITE, MONO_MEM_ACCOUNT_SGEN_LOS);
	for (size_t s = 0; base + s * PAGE < f; ++s) {
		if ((uintptr_t) (base + s * PAGE) % (1u << 20)) continue;
		size_t i = 0; while (i < 256 && s + i < npages && !shadow [s + i]) i++;
		if (i == 256) { printf ("FAIL policy 0 is not first fit: %p free below %p\n", base + s * PAGE, f); return 1; }
	}
	mono_vfree (f, 1u << 20, MONO_MEM_ACCOUNT_SGEN_LOS);
	mono_vfree (base + 0, 0, MONO_MEM_ACCOUNT_OTHER);
	printf ("PASS arena (kit #9 two-ended)\n");
	return 0;
}
