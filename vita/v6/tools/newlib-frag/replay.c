/* SHEPHERD-VITA V6 (vita/notes/v6-vita-build.md section 4): replays an interpose.c malloc trace through Doug Lea's
 * dlmalloc 2.8.3 (the copy in the runtime fork, src/mono/mono/utils/dlmalloc.c, public domain; run.sh generates
 * dlmalloc-sbrk.c from it with Mono's changes undone: MORECORE on, mmap off, realloc/calloc back in) on an sbrk-only heap
 * -- the shape of newlib's malloc on the Vita (dlmalloc 2.6.x, sbrk from one fixed memblock, no mmap, nothing returned
 * from the middle) -- and prints the high-water mark of the sbrk'd size against the bytes in use: the fragmentation term
 * of the newlib heap, on this workload, at 64-bit sizes.
 * usage: replay <trace> [label]
 */
#include <errno.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/mman.h>

/* ---- the simulated sbrk: one contiguous reservation, like newlib's memblock */
#define SIM_RESERVE ((size_t) 4 << 30)
static char *sim_base, *sim_brk;
static size_t sim_hw;
static void *sim_sbrk (intptr_t inc)
{
	if (!sim_base) {
		sim_base = mmap (NULL, SIM_RESERVE, PROT_READ | PROT_WRITE, MAP_PRIVATE | MAP_ANON | MAP_NORESERVE, -1, 0);
		if (sim_base == MAP_FAILED) { perror ("mmap"); exit (2); }
		sim_brk = sim_base;
	}
	char *old = sim_brk;
	if (inc > 0 && (size_t) (sim_brk - sim_base) + (size_t) inc > SIM_RESERVE)
		return (void *) -1;
	if (inc < 0 && (size_t) (-inc) > (size_t) (sim_brk - sim_base))
		return (void *) -1;
	sim_brk += inc;
	if ((size_t) (sim_brk - sim_base) > sim_hw)
		sim_hw = (size_t) (sim_brk - sim_base);
	return old;
}

#include "dlmalloc-sbrk.c"

/* ---- trace pointer -> replayed pointer (open addressing, linear probing, backward-shift delete) */
typedef struct { uint32_t op, pad; uint64_t p, n, q; } Rec;
typedef struct { uint64_t key; void *mem; uint64_t req; } Slot;
#define TBITS 23
#define TSIZE ((size_t) 1 << TBITS)
static Slot *tab;
static size_t live;
static inline size_t hsh (uint64_t k) { k ^= k >> 33; k *= 0xff51afd7ed558ccdULL; k ^= k >> 33; return (size_t) k & (TSIZE - 1); }
static Slot *find (uint64_t k)
{
	for (size_t i = hsh (k);; i = (i + 1) & (TSIZE - 1)) {
		if (!tab [i].key) return NULL;
		if (tab [i].key == k) return &tab [i];
	}
}
static void insert (uint64_t k, void *mem, uint64_t req)
{
	if (live * 2 > TSIZE) { fprintf (stderr, "table full\n"); exit (2); }
	for (size_t i = hsh (k);; i = (i + 1) & (TSIZE - 1)) {
		if (!tab [i].key || tab [i].key == k) {
			if (!tab [i].key) live++;
			tab [i].key = k; tab [i].mem = mem; tab [i].req = req;
			return;
		}
	}
}
static void erase (Slot *s)
{
	size_t i = (size_t) (s - tab), j = i;
	tab [i].key = 0;
	live--;
	for (;;) {
		j = (j + 1) & (TSIZE - 1);
		if (!tab [j].key) return;
		size_t h = hsh (tab [j].key);
		if ((j > i && (h <= i || h > j)) || (j < i && (h <= i && h > j))) {
			tab [i] = tab [j];
			tab [j].key = 0;
			i = j;
		}
	}
}

static uint64_t inuse_req, inuse_chunk, peak_req, peak_chunk, at_peak_fp_req, at_peak_fp_chunk, peak_fp;
static uint64_t n_events, n_unknown_free, n_oom, n_big;
static inline size_t csize (void *mem) { return chunksize (mem2chunk (mem)); }

static void account_add (void *mem, uint64_t req)
{
	inuse_req += req;
	inuse_chunk += csize (mem);
	if (inuse_req > peak_req) peak_req = inuse_req;
	if (inuse_chunk > peak_chunk) peak_chunk = inuse_chunk;
}
static void account_sub (void *mem, uint64_t req)
{
	inuse_req -= req;
	inuse_chunk -= csize (mem);
}

static void do_alloc (uint64_t key, uint64_t n, uint64_t align)
{
	if (!key) return;
	Slot *old = find (key);
	if (old) {   /* a pointer we think is live came back: an unrecorded free (before the constructor); drop the old one */
		account_sub (old->mem, old->req);
		dlfree (old->mem);
		erase (old);
	}
	void *m = align > 8 ? dlmemalign (align, n ? n : 1) : dlmalloc (n ? n : 1);
	if (!m) { n_oom++; return; }
	insert (key, m, n);
	account_add (m, n);
}

static void do_free (uint64_t key)
{
	Slot *s = find (key);
	if (!s) { n_unknown_free++; return; }
	account_sub (s->mem, s->req);
	dlfree (s->mem);
	erase (s);
}

int main (int argc, char **argv)
{
	if (argc < 2) { fprintf (stderr, "usage: replay <trace> [label]\n"); return 2; }
	FILE *f = fopen (argv [1], "rb");
	if (!f) { perror (argv [1]); return 2; }
	tab = calloc (TSIZE, sizeof (Slot));
	static Rec rb [65536];
	size_t k;
	uint64_t next_mark = 16ull << 20;
	printf ("# replay of %s (%s): dlmalloc 2.8.3, sbrk-only, 64-bit sizes\n", argv [1], argc > 2 ? argv [2] : "");
	printf ("# event\tin-use(req) KiB\tin-use(chunks) KiB\tsbrk KiB\tsbrk high-water KiB\n");
	while ((k = fread (rb, sizeof (Rec), 65536, f)) > 0) {
		for (size_t i = 0; i < k; i++) {
			Rec *r = &rb [i];
			n_events++;
			switch (r->op) {
			case 1: do_alloc (r->p, r->n, 0); break;
			case 4: do_alloc (r->p, r->n, r->q); break;
			case 2: do_free (r->p); break;
			case 3:
				if (!r->p) { do_alloc (r->q, r->n, 0); break; }
				if (!r->q) { if (!r->n) do_free (r->p); break; }   /* realloc(p, 0) freed it; a failed realloc keeps p */
				{
					Slot *s = find (r->p);
					if (!s) { do_alloc (r->q, r->n, 0); n_unknown_free++; break; }
					void *mem = s->mem; uint64_t req = s->req;
					account_sub (mem, req);
					erase (s);
					void *m = dlrealloc (mem, r->n ? r->n : 1);
					if (!m) { n_oom++; break; }
					Slot *clash = find (r->q);
					if (clash) { account_sub (clash->mem, clash->req); dlfree (clash->mem); erase (clash); }
					insert (r->q, m, r->n);
					account_add (m, r->n);
				}
				break;
			}
			if (r->n >= (1u << 20)) n_big++;
			size_t fp = gm->footprint;
			if (fp > peak_fp) { peak_fp = fp; at_peak_fp_req = inuse_req; at_peak_fp_chunk = inuse_chunk; }
			if (inuse_req >= next_mark) {
				printf ("%llu\t%llu\t%llu\t%zu\t%zu\n", (unsigned long long) n_events, (unsigned long long) (inuse_req >> 10),
				        (unsigned long long) (inuse_chunk >> 10), (size_t) (sim_brk - sim_base) >> 10, sim_hw >> 10);
				next_mark += 16ull << 20;
			}
		}
	}
	fclose (f);
	printf ("events %llu (allocations >= 1 MiB: %llu), frees of unseen pointers %llu, replay OOM %llu, live at end %zu\n",
	        (unsigned long long) n_events, (unsigned long long) n_big, (unsigned long long) n_unknown_free, (unsigned long long) n_oom, live);
	printf ("peak in use: %.1f MiB requested, %.1f MiB in chunks (headers + alignment)\n", peak_req / 1048576.0, peak_chunk / 1048576.0);
	printf ("sbrk high-water: %.1f MiB (dlmalloc max_footprint %.1f MiB); in use at that moment %.1f MiB requested / %.1f MiB chunks\n",
	        sim_hw / 1048576.0, gm->max_footprint / 1048576.0, at_peak_fp_req / 1048576.0, at_peak_fp_chunk / 1048576.0);
	printf ("end: in use %.1f MiB requested / %.1f MiB chunks, sbrk %.1f MiB\n", inuse_req / 1048576.0, inuse_chunk / 1048576.0,
	        (sim_brk - sim_base) / 1048576.0);
	printf ("RATIO sbrk-high-water / peak-in-use(requested) = %.3f ; / peak-in-use(chunks) = %.3f\n",
	        (double) sim_hw / peak_req, (double) sim_hw / peak_chunk);
	return 0;
}
