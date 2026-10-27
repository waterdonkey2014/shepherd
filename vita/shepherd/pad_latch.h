/* SHEPHERD-VITA kit #3 review: short presses between two engine frames (host.c shepherd_vita_pad_read; test: mac-check/test_pad_latch.c).
 *
 * The engine reads the controls once per frame. At the Vita's frame rates (the play app ~13 fps at the idle menu, Credits ~0.5 s a frame)
 * a tap can begin and end between two reads, and the newest sample alone never shows it. sceCtrlPeekBufferPositive(port, buf, 64) hands
 * back up to 64 of the samples SceCtrl took (one per vblank, ~1 s): every sample newer than the previous read's newest is looked at, and a
 * button that was down in one of them, is up in the newest one and was reported up last time is reported DOWN for this read -- one press
 * frame for the game, as Unity at 60 fps would have seen. The next read reports it up again (the newest sample).
 *
 * Order-agnostic: the samples are told apart by timeStamp only (the newest = the largest; ties: the later entry), so it does not matter
 * whether the firmware returns them oldest or newest first. Inert when every sample has the same timeStamp (a stand-in that copies the
 * current state, vlx) or when the time goes backwards (nothing is latched then). Not latched (by design, rare): a second tap of a button
 * that was just reported down, a release-and-press of a held button between two reads. Expects SceCtrlData (psp2/ctrl.h or the mac-check
 * compat header) and <stdint.h>. */
#pragma once
#include <stdint.h>

typedef struct pad_latch {
	uint64_t last_ts;     /* the newest timeStamp of the previous read */
	unsigned reported;    /* the buttons the previous read reported (held | latched) */
	unsigned taps;        /* reads that latched a tap */
	unsigned reads;
} pad_latch;

/* s[0..n): the samples of one read. Returns the buttons to report; *newest = the newest sample's index; *tap = the latched buttons. */
static unsigned pad_latch_step (pad_latch *st, const SceCtrlData *s, int n, int *newest, unsigned *tap)
{
	int ni = 0;
	for (int i = 1; i < n; ++i)
		if (s [i].timeStamp >= s [ni].timeStamp)
			ni = i;
	uint64_t top = s [ni].timeStamp;
	unsigned cur = s [ni].buttons, seen = 0;
	if (st->reads > 0 && top > st->last_ts)
		for (int i = 0; i < n; ++i)
			if (i != ni && s [i].timeStamp > st->last_ts && s [i].timeStamp <= top)
				seen |= s [i].buttons;
	unsigned t = seen & ~cur & ~st->reported;
	st->last_ts = top;
	st->reads++;
	st->reported = cur | t;
	if (t)
		st->taps++;
	*newest = ni;
	*tap = t;
	return st->reported;
}
