/* SHEPHERD-VITA kit #4 (B-3): Mac unit test of the System.Native shim's read-ahead (pal_vita.c, the "pure read-ahead" block, copied
 * verbatim by run-tests.sh). Every read through ra_pread must return exactly what a direct pread returns -- the same count (never a
 * shorter read than the file allows) and the same bytes -- for sequential readers (4 KiB like a StreamReader, odd sizes), random
 * readers, several fds at once, files whose size is not a multiple of anything, reads at and past the end, a file changed and
 * invalidated in between, and with the read-ahead off. It also checks that sequential small reads become ~size/128 KiB fills. */
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <pthread.h>
#include "readahead.inc"

#define NFILES 4
static unsigned char *files [NFILES]; static int64_t sizes [NFILES]; static unsigned real_calls;
static int capped;   /* > 0: the card serves at most this many bytes per request (a short read that is not the end of the file) */
static int fake_pread (intptr_t fd, void *buf, int32_t size, int64_t off)
{
	int f = (int) fd - 100; real_calls++;
	if (f < 0 || f >= NFILES || off < 0) return -1;
	if (off >= sizes [f]) return 0;
	int64_t n = sizes [f] - off < size ? sizes [f] - off : size;
	if (capped > 0 && n > capped) n = capped;
	memcpy (buf, files [f] + off, (size_t) n);
	return (int) n;
}
static unsigned rng = 12345; static unsigned rnd (void) { rng = rng * 1103515245u + 12345u; return rng >> 8; }
static int fails;
static void check_read (int f, int32_t size, int64_t off, const char *what)
{
	static unsigned char a [70000], b [70000];
	memset (a, 0xAA, sizeof a); memset (b, 0x55, sizeof b);
	int ra = ra_pread (100 + f, a, size, off, fake_pread);
	int d = fake_pread (100 + f, b, size, off);
	if (ra != d || (d > 0 && memcmp (a, b, (size_t) d))) {
		if (fails++ < 10) printf ("FAIL %s: file %d size %d off %lld: read-ahead %d, direct %d\n", what, f, size, (long long) off, ra, d);
	}
}
int main (void)
{
	int64_t want [NFILES] = { 10000000 + 123, 4096 * 7, 1, 300000 };
	for (int f = 0; f < NFILES; ++f) { sizes [f] = want [f]; files [f] = malloc ((size_t) sizes [f]); for (int64_t i = 0; i < sizes [f]; ++i) files [f][i] = (unsigned char) (rnd () ^ i); }
	ra_configure (NULL);
	for (int f = 0; f < NFILES; ++f) ra_track (100 + f, 1, f + 1);
	/* 1: a StreamReader-like sequential reader of the 10 MB file: 4 KiB at a time to the end and past it */
	unsigned c0 = real_calls, calls0 = ra_calls;
	for (int64_t off = 0; off <= sizes [0] + 8192; off += 4096) check_read (0, 4096, off, "sequential 4 KiB");
	unsigned sequential_reads = ra_calls - calls0, card = ra_fills + ra_direct;
	printf ("sequential: %u small reads of a %lld-byte file -> %u fills + %u direct reads (%.1f MiB filled)\n", sequential_reads,
		(long long) sizes [0], ra_fills, ra_direct, ra_fill_bytes / 1048576.0);
	if (card > sequential_reads / 25) { printf ("FAIL sequential reads were not batched: %u card reads for %u small reads\n", card, sequential_reads); fails++; }
	(void) c0;
	/* 2: odd sizes, interleaved fds, random jumps, reads at / past the end */
	int64_t pos [NFILES] = { 0 };
	for (int i = 0; i < 200000; ++i) {
		int f = (int) (rnd () % NFILES);
		int32_t size = (int32_t) (1 + rnd () % (rnd () % 4 ? 5000 : 20000));
		int64_t off = rnd () % 10 ? pos [f] : (int64_t) (rnd () % (unsigned) (sizes [f] + 10));
		check_read (f, size, off, "mixed");
		int64_t n = sizes [f] - off < size ? sizes [f] - off : size; pos [f] = off + (n > 0 ? n : 0);
		if (pos [f] >= sizes [f]) pos [f] = 0;
		if (rnd () % 5000 == 0) {   /* the file changes: a writer elsewhere, then the shim's invalidation */
			for (int k = 0; k < 64; ++k) files [f][rnd () % sizes [f]] ^= 0xFF;
			ra_invalidate (1, f + 1);
		}
	}
	/* 2b (k4 review): a card that serves at most 64 KiB per request -- a short read that is not the end of the file. A sequential
	 * 4 KiB reader (and odd sizes) must still get every byte up to the real end, then 0 */
	ra_close (103); ra_track (103, 1, 4);
	capped = 65536;
	for (int pass = 0; pass < 2; ++pass) {
		int32_t step = pass ? 3001 : 4096;
		int64_t off = 0; int got_end = 0;
		for (int k = 0; k < 400 && !got_end; ++k) {
			check_read (3, step, off, "short card reads (64 KiB cap)");
			int64_t n = sizes [3] - off < step ? sizes [3] - off : step;
			if (n <= 0) got_end = 1; else off += n;
		}
		if (!got_end || off != sizes [3]) { printf ("FAIL short card reads: the reader stopped at %lld of %lld\n", (long long) off, (long long) sizes [3]); fails++; }
		ra_close (103); ra_track (103, 1, 4);
	}
	capped = 0;
	/* 3: close and reopen (the slot is reused), then the read-ahead off */
	ra_close (101); ra_track (101, 1, 2);
	for (int64_t off = 0; off < sizes [1]; off += 1000) check_read (1, 1000, off, "after reopen");
	ra_configure ("0");
	for (int64_t off = 0; off < sizes [3]; off += 4096) check_read (3, 4096, off, "read-ahead off");
	printf ("totals: %u calls, %u served from buffers, %u fills (%.1f MiB), %u direct, %u invalidated\n", ra_calls, ra_served, ra_fills,
		ra_fill_bytes / 1048576.0, ra_direct, ra_invalidated);
	printf ("%s test_readahead (%d failures)\n", fails ? "FAIL" : "PASS", fails);
	return fails ? 1 : 0;
}
