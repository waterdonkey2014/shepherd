/* kit #5 (track touch): the host's front-panel latch (vita/shepherd/touch_latch.h, host.c shepherd_vita_touch_read) on the Mac, with
 * synthetic SceTouch sample buffers in both orders. Build + run (exit 0 = PASS):
 *   cc -O2 -Wall -o /tmp/test_touch_latch vita/shepherd/mac-check/test_touch_latch.c && /tmp/test_touch_latch */
#include <stdint.h>
#include <stdio.h>
#include <string.h>

typedef struct { uint8_t id, force; int16_t x, y; uint8_t reserved [8]; uint16_t info; } SceTouchReport;   /* psp2/touch.h's layout */
typedef struct { uint64_t timeStamp; uint32_t status, reportNum; SceTouchReport report [8]; } SceTouchData;
#include "../touch_latch.h"

static int fails;
#define CHECK(c, ...) do { if (!(c)) { fails++; printf ("FAIL %s:%d: ", __FILE__, __LINE__); printf (__VA_ARGS__); printf ("\n"); } } while (0)

/* the panel's ring: one sample per 16.7 ms; a read hands back the last n of them, oldest first (asc) or newest first */
static SceTouchData timeline [4096];
static int read_at (touch_latch *st, int now_idx, int n, int asc, int *x, int *y)
{
	SceTouchData buf [64];
	if (n > now_idx + 1)
		n = now_idx + 1;
	for (int i = 0; i < n; ++i)
		buf [i] = timeline [asc ? now_idx - n + 1 + i : now_idx - i];
	int newest;
	*x = *y = -1;
	int r = touch_latch_step (st, buf, n, &newest, x, y);
	CHECK (buf [newest].timeStamp == timeline [now_idx].timeStamp, "newest sample wrong (%d)", newest);
	return r;
}
/* finger <id> at (x0,y0) -> (x1,y1) over samples [from, to) */
static void finger (int from, int to, int id, int x0, int y0, int x1, int y1)
{
	for (int i = from; i < to; ++i) {
		SceTouchData *d = &timeline [i];
		int k = (int) d->reportNum, t = to - from > 1 ? (i - from) * 1000 / (to - from - 1) : 0;
		d->report [k].id = (uint8_t) id;
		d->report [k].x = (int16_t) (x0 + (x1 - x0) * t / 1000);
		d->report [k].y = (int16_t) (y0 + (y1 - y0) * t / 1000);
		d->reportNum = (uint32_t) (k + 1);
	}
}

int main (void)
{
	for (int asc = 0; asc < 2; ++asc) {
		memset (timeline, 0, sizeof (timeline));
		for (int i = 0; i < 4096; ++i)
			timeline [i].timeStamp = 1000000ull + (uint64_t) i * 16667ull;
		touch_latch st = TOUCH_LATCH_INIT;
		int x, y, r;
		/* reads every 30 samples (0.5 s, Credits on the device); a 5-sample tap (83 ms) at (600,500) inside the gap 30..60 */
		finger (40, 45, 3, 600, 500, 604, 502);
		r = read_at (&st, 30, 64, asc, &x, &y);
		CHECK (r == TOUCH_NONE, "read 1: %d", r);
		r = read_at (&st, 60, 64, asc, &x, &y);
		CHECK (r == TOUCH_TAP && x == 604 && y == 502, "asc=%d: the tap between reads must be latched where it left the glass (%d at %d,%d)", asc, r, x, y);
		r = read_at (&st, 90, 64, asc, &x, &y);
		CHECK (r == TOUCH_NONE, "asc=%d: ... and not again at the next read (%d)", asc, r);
		/* a finger held across reads and moved: CONTACT at the newest position, then released between reads: not a tap */
		finger (100, 140, 5, 200, 900, 800, 300);
		r = read_at (&st, 110, 64, asc, &x, &y);
		CHECK (r == TOUCH_CONTACT && x == 200 + 600 * 10 / 39 && y == 900 - 600 * 10 / 39, "asc=%d: held: %d at %d,%d", asc, r, x, y);
		r = read_at (&st, 139, 64, asc, &x, &y);
		CHECK (r == TOUCH_CONTACT && x == 800 && y == 300, "asc=%d: dragged: %d at %d,%d", asc, r, x, y);
		r = read_at (&st, 150, 64, asc, &x, &y);
		CHECK (r == TOUCH_NONE, "asc=%d: released after a reported contact must not come back as a tap (%d)", asc, r);
		CHECK (st.contacts == 1 && st.taps == 1, "asc=%d: counts %u contacts %u taps", asc, st.contacts, st.taps);
		/* two fingers: the first one is followed; when it leaves while the second stays, no finger for that read, then a new contact */
		finger (200, 260, 7, 100, 100, 100, 100);
		finger (210, 300, 8, 1500, 900, 1500, 900);
		r = read_at (&st, 205, 64, asc, &x, &y);
		CHECK (r == TOUCH_CONTACT && x == 100, "asc=%d: first finger: %d at %d", asc, r, x);
		r = read_at (&st, 230, 64, asc, &x, &y);
		CHECK (r == TOUCH_CONTACT && x == 100, "asc=%d: still the first finger with two down: %d at %d", asc, r, x);
		r = read_at (&st, 270, 64, asc, &x, &y);
		CHECK (r == TOUCH_NONE, "asc=%d: the followed finger left: no finger this read (%d)", asc, r);
		r = read_at (&st, 280, 64, asc, &x, &y);
		CHECK (r == TOUCH_CONTACT && x == 1500, "asc=%d: then the second finger as a new contact (%d at %d)", asc, r, x);
		r = read_at (&st, 330, 64, asc, &x, &y);
		CHECK (r == TOUCH_NONE, "asc=%d: all up (%d)", asc, r);
		CHECK (st.contacts == 3, "asc=%d: contacts %u", asc, st.contacts);
		/* one sample per read (no buffer): a tap between reads is lost, a contact is seen */
		finger (400, 402, 9, 50, 60, 50, 60);
		r = read_at (&st, 399, 1, asc, &x, &y);
		r = read_at (&st, 405, 1, asc, &x, &y);
		CHECK (r == TOUCH_NONE, "asc=%d: one sample: no latch possible (%d)", asc, r);
	}
	/* a stand-in that copies the current state into every sample (one timestamp): never latches */
	{
		SceTouchData buf [64];
		memset (buf, 0, sizeof (buf));
		for (int i = 0; i < 64; ++i)
			buf [i].timeStamp = 5000;
		buf [10].reportNum = 1;
		touch_latch st = TOUCH_LATCH_INIT;
		int nw, x, y;
		touch_latch_step (&st, buf, 64, &nw, &x, &y);
		int r = touch_latch_step (&st, buf, 64, &nw, &x, &y);
		CHECK (r == TOUCH_NONE && st.taps == 0, "equal timestamps must stay inert (%d, %u taps)", r, st.taps);
	}
	printf ("%s: touch latch, %d failure(s)\n", fails ? "FAIL" : "PASS", fails);
	return fails != 0;
}
