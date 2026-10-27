/* kit #3 review: the host's short-press latch (vita/shepherd/pad_latch.h, host.c shepherd_vita_pad_read) on the Mac, with synthetic
 * SceCtrl sample buffers in both orders. Build + run (exit 0 = PASS):
 *   cc -O2 -Wall -o /tmp/test_pad_latch vita/shepherd/mac-check/test_pad_latch.c && /tmp/test_pad_latch */
#include <stdint.h>
#include <stdio.h>
#include <string.h>

typedef struct { uint64_t timeStamp; unsigned buttons; unsigned char lx, ly, rx, ry; } SceCtrlData;   /* (the fields the latch reads) */
#include "../pad_latch.h"

static int fails;
#define CHECK(c, ...) do { if (!(c)) { fails++; printf ("FAIL %s:%d: ", __FILE__, __LINE__); printf (__VA_ARGS__); printf ("\n"); } } while (0)

enum { CROSS = 0x4000, CIRCLE = 0x2000, DOWN = 0x0040 };

/* the device's ring: one sample per 16.7 ms vblank; a read hands back the last n of them, oldest first (asc) or newest first */
static SceCtrlData timeline [4096];
static int read_at (pad_latch *st, int now_idx, int n, int asc, unsigned *tap)
{
	SceCtrlData buf [64];
	if (n > now_idx + 1)
		n = now_idx + 1;
	for (int i = 0; i < n; ++i)
		buf [i] = timeline [asc ? now_idx - n + 1 + i : now_idx - i];
	int newest;
	unsigned r = pad_latch_step (st, buf, n, &newest, tap);
	CHECK (buf [newest].timeStamp == timeline [now_idx].timeStamp, "newest sample wrong (%d)", newest);
	return (int) r;
}
static void set_timeline (int from, int to, unsigned buttons)
{
	for (int i = from; i < to; ++i)
		timeline [i].buttons = buttons;
}

int main (void)
{
	for (int asc = 0; asc < 2; ++asc) {
		for (int i = 0; i < 4096; ++i) {
			memset (&timeline [i], 0, sizeof (timeline [i]));
			timeline [i].timeStamp = 1000000ull + (uint64_t) i * 16667ull;
		}
		/* frames every 30 vblanks (0.5 s, Credits on the device); a cross tap of 5 vblanks (83 ms) inside the gap 30..60 */
		set_timeline (40, 45, CROSS);
		pad_latch st = { 0 };
		unsigned tap;
		int r0 = read_at (&st, 30, 64, asc, &tap);
		CHECK (r0 == 0 && tap == 0, "read 1: %x %x", r0, tap);
		int r1 = read_at (&st, 60, 64, asc, &tap);
		CHECK (r1 == CROSS && tap == CROSS, "asc=%d: the tap between frames must be reported down once (%x, tap %x)", asc, r1, tap);
		int r2 = read_at (&st, 90, 64, asc, &tap);
		CHECK (r2 == 0 && tap == 0, "asc=%d: ... and up at the next read (%x)", asc, r2);
		/* a hold across reads: down at the read, no latch (cur covers it), then released between reads: not re-reported */
		set_timeline (100, 140, DOWN);
		int r3 = read_at (&st, 120, 64, asc, &tap);
		CHECK (r3 == DOWN && tap == 0, "asc=%d: held: %x tap %x", asc, r3, tap);
		int r4 = read_at (&st, 150, 64, asc, &tap);
		CHECK (r4 == 0 && tap == 0, "asc=%d: released after a reported hold must not come back as a tap (%x tap %x)", asc, r4, tap);
		/* a tap while another button is held: only the tapped one is latched */
		set_timeline (160, 200, DOWN);
		set_timeline (165, 170, DOWN | CIRCLE);
		int r5 = read_at (&st, 180, 64, asc, &tap);
		CHECK (r5 == (DOWN | CIRCLE) && tap == CIRCLE, "asc=%d: tap during a hold: %x tap %x", asc, r5, tap);
		/* reads faster than the samples (the same newest sample twice): nothing new, nothing latched */
		int r6 = read_at (&st, 180, 64, asc, &tap);
		CHECK (r6 == DOWN && tap == 0, "asc=%d: no new sample: %x tap %x", asc, r6, tap);
		/* one sample per read (SHEPHERD_PAD_LATCH=0's shape, or a firmware that returns 1): the newest only */
		set_timeline (205, 207, CROSS);
		int r7 = read_at (&st, 210, 1, asc, &tap);
		CHECK (r7 == 0 && tap == 0, "asc=%d: a single sample cannot latch: %x", asc, r7);
		/* a tap older than the ring (64 samples back): gone, as on the device */
		set_timeline (300, 303, CROSS);
		pad_latch st2 = { 0 };
		read_at (&st2, 290, 64, asc, &tap);
		int r8 = read_at (&st2, 400, 64, asc, &tap);
		CHECK (r8 == 0 && tap == 0, "asc=%d: a tap beyond the ring must not appear: %x", asc, r8);
		CHECK (st.taps == 2, "asc=%d: taps counted %u, want 2", asc, st.taps);
	}
	/* every sample with the same timeStamp (a stand-in copying the current state: vlx): inert */
	{
		SceCtrlData b [64];
		memset (b, 0, sizeof (b));
		for (int i = 0; i < 64; ++i) { b [i].timeStamp = 5000; b [i].buttons = i == 10 ? CROSS : 0; }
		pad_latch st = { 0 };
		int nw; unsigned tap;
		pad_latch_step (&st, b, 64, &nw, &tap);
		for (int i = 0; i < 64; ++i) b [i].timeStamp = 5000;
		unsigned r = pad_latch_step (&st, b, 64, &nw, &tap);
		CHECK (tap == 0, "equal timestamps must not latch (%x tap %x)", r, tap);
		/* time going backwards: nothing latched, the next read starts again from there */
		for (int i = 0; i < 64; ++i) { b [i].timeStamp = 100 + i; b [i].buttons = i == 5 ? CROSS : 0; }
		r = pad_latch_step (&st, b, 64, &nw, &tap);
		CHECK (tap == 0 && r == 0, "time going backwards must not latch (%x tap %x)", r, tap);
	}
	printf ("%s: pad latch, %d failure(s)\n", fails ? "FAIL" : "PASS", fails);
	return fails ? 1 : 0;
}
