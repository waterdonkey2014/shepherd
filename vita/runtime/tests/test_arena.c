/* SHEPHERD-VITA: Mac unit test of mono-mmap-vita.c (the Vita mono_valloc arena): randomized alloc/free with
 * alignments and partial frees against a shadow page map; checks zero-fill, alignment, no overlap, coalescing,
 * hints, EXEC refusal and the lazy self-allocated arena. Then the fatal paths (double free, free outside the
 * arena): each must raise its g_error only after releasing the arena lock (the host's fatal log handler reads the
 * stats; with the lock still held it would deadlock), and must leave the arena unchanged. Build: see run-tests.sh */
#include <setjmp.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <stdint.h>
#include <glib.h>
#include "mono/utils/mono-mmap.h"
#include "mono/utils/mono-vita.h"
#include <psp2/kernel/sysmem.h>

/* stubs for the generic parts of mono-mmap.c */
void mono_account_mem (MonoMemAccountType type, ssize_t size) { }
gboolean mono_valloc_can_alloc (size_t size) { return TRUE; }
const char *mono_mem_account_type_name (MonoMemAccountType type) { return "test"; }

static int lazy_allocs;
SceUID sceKernelAllocMemBlock (const char *name, int type, size_t size, void *opt) { lazy_allocs++; return 1; }
static void *lazy_base;
int sceKernelGetMemBlockBase (SceUID uid, void **base) { *base = lazy_base; return 0; }
int sceKernelFreeMemBlock (SceUID uid) { return 0; }

#define PAGE 4096u
static uint8_t *shadow; /* 1 = allocated */
static uint8_t *base;
static size_t npages;

typedef struct { uint8_t *p; size_t pages; } A;

static unsigned rnd (void) { static unsigned s = 12345; s = s * 1103515245 + 12345; return s >> 8; }

/* stands in for the host's fatal log handler: g_error must not return, so jump back into the test */
static jmp_buf fatal_jmp;
static int fatal_count, fatal_lock_held;
static void fatal_handler (const gchar *domain, GLogLevelFlags level, const gchar *message, gpointer data)
{
	if (!(level & G_LOG_LEVEL_ERROR)) {
		printf ("     (log: %s)\n", message);
		return;
	}
	MonoVitaArenaStats s;
	int busy = mono_vita_vm_arena_try_get_stats (&s) != 0;   /* what host.c's log_mem_ex ("fatal", 1) does */
	fatal_count++;
	fatal_lock_held += busy;
	printf ("     fatal: %s [arena lock %s]\n", message, busy ? "HELD" : "free");
	longjmp (fatal_jmp, 1);
}

/* runs op, which must raise exactly one fatal error with the arena lock released */
#define EXPECT_FATAL(what, op) do { \
	int before = fatal_count, held = fatal_lock_held; \
	if (!setjmp (fatal_jmp)) { op; } \
	if (fatal_count != before + 1 || fatal_lock_held != held) { printf ("FAIL %s: fatal=%d lock-held=%d\n", what, fatal_count - before, fatal_lock_held - held); return 1; } \
} while (0)

int main (void)
{
	size_t size = 64u << 20;
	void *mem;
	if (posix_memalign (&mem, 1 << 22, size)) return 1;
	memset (mem, 0xAB, size);       /* the arena must zero what it hands out */
	if (mono_vita_vm_arena_init (mem, size) != 0) { printf ("FAIL init\n"); return 1; }
	MonoVitaArenaStats st;
	mono_vita_vm_arena_get_stats (&st);
	base = (uint8_t *) mem + (size - st.arena_size);
	npages = st.arena_size / PAGE;
	shadow = calloc (npages, 1);
	printf ("arena %zu KiB usable, %u extents\n", st.arena_size / 1024, st.free_extents);
	if (st.free_extents != 1) { printf ("FAIL fresh arena not one extent\n"); return 1; }

	if (mono_valloc (NULL, PAGE, MONO_MMAP_READ | MONO_MMAP_WRITE | MONO_MMAP_EXEC, MONO_MEM_ACCOUNT_CODE) != NULL) { printf ("FAIL exec allowed\n"); return 1; }
	if (mono_mprotect (base, PAGE, MONO_MMAP_READ | MONO_MMAP_EXEC) == 0) { printf ("FAIL mprotect exec allowed\n"); return 1; }

	A live [4096]; int nlive = 0;
	size_t aligns [] = { PAGE, 16384, 65536, 1u << 20, 4u << 20 };
	long ops = 0, fails = 0;
	for (int it = 0; it < 200000; ++it) {
		int r = rnd () % 100;
		if ((r < 55 && nlive < 4096) || nlive == 0) {
			size_t pages = 1 + rnd () % ((rnd () % 10) ? 8 : 300);
			size_t al = aligns [rnd () % 5];
			uint8_t *p = (rnd () % 3) ? mono_valloc_aligned (pages * PAGE, al, MONO_MMAP_READ | MONO_MMAP_WRITE, MONO_MEM_ACCOUNT_OTHER)
			                          : mono_valloc (NULL, pages * PAGE - (rnd () % PAGE), MONO_MMAP_READ | MONO_MMAP_WRITE, MONO_MEM_ACCOUNT_OTHER);
			if (!p) { fails++; continue; }
			size_t first = (size_t)(p - base) / PAGE;
			if (p < base || (p - base) % PAGE || first + pages > npages) { printf ("FAIL out of range %p\n", p); return 1; }
			for (size_t i = 0; i < pages; ++i) {
				if (shadow [first + i]) { printf ("FAIL overlap at page %zu\n", first + i); return 1; }
				shadow [first + i] = 1;
			}
			for (size_t i = 0; i < pages * PAGE; ++i) if (p [i]) { printf ("FAIL not zeroed\n"); return 1; }
			memset (p, 0xCD, pages * PAGE);
			live [nlive].p = p; live [nlive].pages = pages; nlive++;
			ops++;
		} else {
			int k = rnd () % nlive;
			A a = live [k];
			/* free a random sub-range (prefix, suffix, middle or all), keep the rest live */
			size_t off = (rnd () % 4 == 0) ? rnd () % a.pages : 0;
			size_t cnt = 1 + rnd () % (a.pages - off);
			mono_vfree (a.p + off * PAGE, cnt * PAGE, MONO_MEM_ACCOUNT_OTHER);
			size_t first = (size_t)(a.p - base) / PAGE;
			for (size_t i = off; i < off + cnt; ++i) shadow [first + i] = 0;
			live [k] = live [--nlive];
			if (off > 0 && nlive < 4096) { live [nlive].p = a.p; live [nlive].pages = off; nlive++; }
			if (off + cnt < a.pages && nlive < 4096) { live [nlive].p = a.p + (off + cnt) * PAGE; live [nlive].pages = a.pages - off - cnt; nlive++; }
			else if (off + cnt < a.pages) { mono_vfree (a.p + (off + cnt) * PAGE, (a.pages - off - cnt) * PAGE, MONO_MEM_ACCOUNT_OTHER);
				for (size_t i = off + cnt; i < a.pages; ++i) shadow [first + i] = 0; }
			ops++;
		}
	}
	mono_vita_vm_arena_get_stats (&st);
	size_t used = 0; for (size_t i = 0; i < npages; ++i) used += shadow [i];
	printf ("%ld ops, %ld failed allocs (arena full), live %d, used %zu KiB (arena says %zu), peak %zu KiB, %u extents\n",
		ops, fails, nlive, used * 4, st.used / 1024, st.peak / 1024, st.free_extents);
	if (used * PAGE != st.used) { printf ("FAIL accounting\n"); return 1; }
	for (int k = 0; k < nlive; ++k) mono_vfree (live [k].p, live [k].pages * PAGE, MONO_MEM_ACCOUNT_OTHER);
	mono_vita_vm_arena_get_stats (&st);
	if (st.used != 0 || st.free_extents != 1 || st.largest_free != npages * PAGE) { printf ("FAIL no full coalesce: used %zu extents %u\n", st.used, st.free_extents); return 1; }
	/* hint: allocate, free, re-allocate at the same address */
	uint8_t *h = mono_valloc (NULL, 8 * PAGE, MONO_MMAP_READ | MONO_MMAP_WRITE, MONO_MEM_ACCOUNT_OTHER);
	mono_vfree (h, 8 * PAGE, MONO_MEM_ACCOUNT_OTHER);
	uint8_t *h2 = mono_valloc (h + 2 * PAGE, 2 * PAGE, MONO_MMAP_READ | MONO_MMAP_WRITE, MONO_MEM_ACCOUNT_OTHER);
	if (h2 != h + 2 * PAGE) { printf ("FAIL hint %p != %p\n", h2, h + 2 * PAGE); return 1; }
	mono_vfree (h2, 2 * PAGE, MONO_MEM_ACCOUNT_OTHER);

	/* fatal paths */
	g_log_set_default_handler (fatal_handler, NULL);
	if (mono_vita_vm_arena_try_get_stats (&st) != 0 || st.used != 0) { printf ("FAIL try_get_stats on an idle arena\n"); return 1; }
	uint8_t *d = mono_valloc (NULL, 4 * PAGE, MONO_MMAP_READ | MONO_MMAP_WRITE, MONO_MEM_ACCOUNT_OTHER);
	mono_vfree (d + PAGE, 2 * PAGE, MONO_MEM_ACCOUNT_OTHER);                    /* middle two pages */
	EXPECT_FATAL ("double free (whole range over a freed middle)", mono_vfree (d, 4 * PAGE, MONO_MEM_ACCOUNT_OTHER));
	EXPECT_FATAL ("double free (same range again)", mono_vfree (d + PAGE, 2 * PAGE, MONO_MEM_ACCOUNT_OTHER));
	EXPECT_FATAL ("double free (overlapping the previous free extent)", mono_vfree (d + 2 * PAGE, 2 * PAGE, MONO_MEM_ACCOUNT_OTHER));
	EXPECT_FATAL ("free below the arena", mono_vfree ((uint8_t *) mem - 16 * PAGE, PAGE, MONO_MEM_ACCOUNT_OTHER));
	EXPECT_FATAL ("free past the arena", mono_vfree (base + npages * PAGE - PAGE, 2 * PAGE, MONO_MEM_ACCOUNT_OTHER));
	EXPECT_FATAL ("unaligned free", mono_vfree (d + 1, PAGE, MONO_MEM_ACCOUNT_OTHER));
	/* nothing changed: pages 0 and 3 of d are still live (8 KiB), the middle is free */
	mono_vita_vm_arena_get_stats (&st);
	if (st.used != 2 * PAGE || st.free_extents != 2) { printf ("FAIL a rejected free changed the arena: used %zu, %u extents\n", st.used, st.free_extents); return 1; }
	mono_vfree (d, PAGE, MONO_MEM_ACCOUNT_OTHER);
	mono_vfree (d + 3 * PAGE, PAGE, MONO_MEM_ACCOUNT_OTHER);
	mono_vita_vm_arena_get_stats (&st);
	if (st.used != 0 || st.free_extents != 1) { printf ("FAIL no coalesce after the fatal tests: used %zu, %u extents\n", st.used, st.free_extents); return 1; }
	printf ("     %d fatal errors, all raised with the arena lock released\n", fatal_count);
	printf ("PASS arena\n");
	return 0;
}
