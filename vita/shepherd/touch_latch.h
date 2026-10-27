/* SHEPHERD-VITA kit #5 (track touch): the FRONT touch panel between two engine frames (host.c shepherd_vita_touch_read; test:
 * mac-check/test_touch_latch.c). The engine reads the panel once per frame, like the pad (pad_latch.h). At the Vita's frame rates (the
 * play apps ~15 fps at the idle menu, Credits ~0.5 s a frame) a quick tap can touch and leave the glass between two reads: the newest
 * sample alone never shows it. sceTouchPeek (port, buf, 64) hands back the samples SceTouch buffered; every sample newer than the previous
 * read's newest is looked at, and when the newest one has no finger, the previous read reported none and some sample in between had one,
 * the read reports a TAP at the position where that finger left the glass (the latest such sample) -- the engine turns it into a mouse
 * click there (Icalls/VitaTouch.cs: move, button down, button up). A finger on the glass in the newest sample is reported as such
 * (CONTACT), at the position of the finger followed since it touched (its report id); when that finger leaves while another stays, the
 * read reports no finger (the mouse button comes up), and the next read starts a new contact at the other finger.
 *
 * Order-agnostic: the samples are told apart by timeStamp only (the newest = the largest; ties: the later entry). Inert when every sample
 * has the same timeStamp (vlx's stand-in without a script) or when time goes backwards (nothing is latched then). Not latched (rare): a
 * second tap in the same interval (one tap per read), a tap right after a reported contact ended in the same interval. Rear panel: never
 * read. Expects SceTouchData (psp2/touch.h or the mac-check test's own struct) and <stdint.h>. */
#pragma once
#include <stdint.h>

typedef struct touch_latch {
	uint64_t last_ts;     /* the newest timeStamp of the previous read */
	int follow;           /* the report id followed while a finger stays on the glass; -1: none */
	int reported;         /* the previous read reported a finger on the glass */
	unsigned taps, contacts, reads;
} touch_latch;

#define TOUCH_LATCH_INIT { 0, -1, 0, 0, 0, 0 }
#define TOUCH_NONE 0
#define TOUCH_CONTACT 1
#define TOUCH_TAP 2

static int touch_latch_reports (const SceTouchData *d) { return d->reportNum > 8u ? 8 : (int) d->reportNum; }

/* s[0..n): the samples of one read. Returns TOUCH_CONTACT (*x, *y = the followed finger in the newest sample), TOUCH_TAP (*x, *y = where
 * the latched finger left the glass) or TOUCH_NONE; *newest = the newest sample's index. Coordinates in the panel's report space. */
static int touch_latch_step (touch_latch *st, const SceTouchData *s, int n, int *newest, int *x, int *y)
{
	int ni = 0;
	for (int i = 1; i < n; ++i)
		if (s [i].timeStamp >= s [ni].timeStamp)
			ni = i;
	uint64_t top = s [ni].timeStamp;
	int r = TOUCH_NONE, k = touch_latch_reports (&s [ni]);
	if (k > 0) {
		int j = 0;
		if (st->reported && st->follow >= 0) {
			for (j = 0; j < k && s [ni].report [j].id != st->follow; ++j)
				;
			if (j == k)
				j = -1;   /* the followed finger left; another one stays: no finger this read, a new contact at the next */
		} else {
			st->follow = s [ni].report [0].id;
			st->contacts++;
		}
		if (j >= 0) {
			*x = s [ni].report [j].x;
			*y = s [ni].report [j].y;
			r = TOUCH_CONTACT;
		} else
			st->follow = -1;
	} else {
		st->follow = -1;
		if (st->reads > 0 && top > st->last_ts && !st->reported) {
			int li = -1;
			for (int i = 0; i < n; ++i)
				if (i != ni && touch_latch_reports (&s [i]) > 0 && s [i].timeStamp > st->last_ts && s [i].timeStamp <= top
				    && (li < 0 || s [i].timeStamp >= s [li].timeStamp))
					li = i;
			if (li >= 0) {
				*x = s [li].report [0].x;
				*y = s [li].report [0].y;
				r = TOUCH_TAP;
				st->taps++;
			}
		}
	}
	st->reported = r == TOUCH_CONTACT;
	st->last_ts = top;
	st->reads++;
	*newest = ni;
	return r;
}
