/* Stage C: the audio pool allocator (vita/shepherd/audio_pool.c) on the Mac -- random alloc/free against a shadow model:
 * no overlap, contents intact, 16-byte alignment, coalescing (all freed -> one block of the whole pool), failure counting,
 * double/foreign free refused, and a 4-thread run through the lock hooks. Exit 0 = PASS. */
#include "../audio_pool.h"
#include <pthread.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

static int fails;
#define CHECK(c, ...) do { if (!(c)) { fails++; printf ("FAIL %s:%d: ", __FILE__, __LINE__); printf (__VA_ARGS__); printf ("\n"); } } while (0)

static uint32_t rng = 12345;
static uint32_t rnd (void) { rng ^= rng << 13; rng ^= rng >> 17; rng ^= rng << 5; return rng; }

typedef struct { uint8_t *p; size_t n; uint8_t tag; } slot;

static void stress (ap_pool *ap, slot *s, int nslots, int iters, uint32_t *fail_allocs)
{
	for (int it = 0; it < iters; it++) {
		int k = (int) (rnd () % (uint32_t) nslots);
		if (s[k].p) {
			for (size_t i = 0; i < s[k].n; i++)
				if (s[k].p[i] != (uint8_t) (s[k].tag + i)) { CHECK (0, "contents of block %d corrupted at %zu", k, i); break; }
			ap_free (ap, s[k].p);
			s[k].p = NULL;
		} else {
			size_t n = (rnd () % 4 == 0) ? 1 + rnd () % 200000 : 1 + rnd () % 4096;   /* takes and rings: small and large */
			uint8_t *p = (uint8_t *) ap_alloc (ap, n, 16);
			if (!p) { (*fail_allocs)++; continue; }
			CHECK (((uintptr_t) p & 15u) == 0, "alignment");
			s[k].p = p; s[k].n = n; s[k].tag = (uint8_t) rnd ();
			for (size_t i = 0; i < n; i++) p[i] = (uint8_t) (s[k].tag + i);
		}
	}
}

typedef struct { ap_pool *ap; uint32_t seed; int iters; uint32_t fails; } targ;
static pthread_mutex_t mu = PTHREAD_MUTEX_INITIALIZER;
static void lk (void *a) { pthread_mutex_lock ((pthread_mutex_t *) a); }
static void ulk (void *a) { pthread_mutex_unlock ((pthread_mutex_t *) a); }
static void *worker (void *a)
{
	targ *t = (targ *) a;
	slot s[64]; memset (s, 0, sizeof (s));
	uint32_t local = t->seed;
	for (int it = 0; it < t->iters; it++) {
		local ^= local << 13; local ^= local >> 17; local ^= local << 5;
		int k = (int) (local % 64);
		if (s[k].p) {
			for (size_t i = 0; i < s[k].n; i++) if (s[k].p[i] != (uint8_t) (s[k].tag + i)) { t->fails++; break; }
			ap_free (t->ap, s[k].p); s[k].p = NULL;
		} else {
			size_t n = 1 + local % 20000;
			uint8_t *p = (uint8_t *) ap_alloc (t->ap, n, 16);
			if (!p) continue;
			s[k].p = p; s[k].n = n; s[k].tag = (uint8_t) local;
			for (size_t i = 0; i < n; i++) p[i] = (uint8_t) (s[k].tag + i);
		}
	}
	for (int k = 0; k < 64; k++) if (s[k].p) ap_free (t->ap, s[k].p);
	return NULL;
}

int main (void)
{
	size_t bytes = 8u << 20;
	void *mem = malloc (bytes + 7);
	ap_pool ap;
	CHECK (ap_init (&ap, (uint8_t *) mem + 7, bytes, NULL, NULL, NULL) == 0, "init (unaligned base)");
	uint32_t lf, nf;
	CHECK (ap_check (&ap, &lf, &nf) == 0 && nf == 1 && lf == ap.size, "fresh pool: one free block of %u bytes", ap.size);
	slot s[512]; memset (s, 0, sizeof (s));
	uint32_t fa = 0;
	stress (&ap, s, 512, 200000, &fa);
	int c = ap_check (&ap, &lf, &nf);
	CHECK (c == 0, "consistent after 200k random ops (code %d)", c);
	printf ("single-threaded: 200000 ops, %u allocs / %u frees, %u failed allocations (full pool), peak %u of %u bytes, now %u free blocks (largest %u)\n",
	        ap.allocs, ap.frees, fa, ap.peak, ap.size, nf, lf);
	CHECK (fa == ap.failures, "failures counted (%u vs %u)", fa, ap.failures);
	for (int k = 0; k < 512; k++) if (s[k].p) { ap_free (&ap, s[k].p); s[k].p = NULL; }
	CHECK (ap_check (&ap, &lf, &nf) == 0 && nf == 1 && lf == ap.size && ap.used == 0, "everything freed -> one free block of the whole pool (%u blocks, largest %u)", nf, lf);
	void *x = ap_alloc (&ap, 100, 16);
	uint32_t f0 = ap.failures;
	ap_free (&ap, x); ap_free (&ap, x);
	CHECK (ap.failures == f0 + 1 && ap_check (&ap, NULL, NULL) == 0, "double free refused and counted");
	int foreign;
	ap_free (&ap, &foreign);
	CHECK (ap_check (&ap, NULL, NULL) == 0, "foreign pointer ignored");
	CHECK (ap_alloc (&ap, 64, 32) == NULL, "align > 16 refused");
	CHECK (ap_alloc (&ap, bytes, 16) == NULL && ap_alloc (&ap, ap.size - 16, 16) != NULL, "too big refused; the whole pool in one block fits");
	free (mem);

	/* 4 threads through the lock hooks */
	mem = malloc (bytes);
	ap_init (&ap, mem, bytes, lk, ulk, &mu);
	pthread_t th[4]; targ ta[4];
	for (int i = 0; i < 4; i++) { ta[i] = (targ) { &ap, 0x9E3779B9u * (uint32_t) (i + 1), 100000, 0 }; pthread_create (&th[i], NULL, worker, &ta[i]); }
	uint32_t tf = 0;
	for (int i = 0; i < 4; i++) { pthread_join (th[i], NULL); tf += ta[i].fails; }
	CHECK (tf == 0 && ap_check (&ap, &lf, &nf) == 0 && nf == 1 && ap.used == 0, "4 threads x 100k ops: contents intact (%u), all freed, one block", tf);
	printf ("4 threads: %u allocs, %u frees, peak %u bytes\n", ap.allocs, ap.frees, ap.peak);
	free (mem);
	printf ("%s test_audio_pool (%d failure(s))\n", fails ? "FAIL" : "PASS", fails);
	return fails ? 1 : 0;
}
