/* ---------------------------------------------------------------- device kit #8, track play (vita/notes/kit8-play.md)
 * Included by host.c right after k7_host.inc.c (the pad / touch statics and the kit #7 clock measurement are above). Every part has an
 * env-file switch back to kit #7; SHEPHERD_K8_PLAY=0 turns the whole track off (the engine reads the same switches: Runtime/K8Play.cs).
 *
 *  MEASURED CLOCK (kit #7 device run, extras X2: with a PSVshellPlus profile the ARM cores really ran at ~497 MHz while
 *   scePowerGetArmClockFrequency still reported 444 -- PSVshellPlus sets the clock outside scePower): the CPU Clock readout row
 *   (shepherd_vita_k6 op 3) and the performance overlay (shepherd_vita_k8 op 1) show the clock kit #7's watchdog MEASURED (the timed add
 *   chain, k7_host.inc.c) once that measurement settled on one of the Vita's clock steps; until then, and wherever nothing is measured (the
 *   headless 00011; under vlx, where qemu's timing is the Mac's and never lands on a Vita step), the system's report as before. One CLK
 *   readout-measured line whenever the readout shows a measured clock that differs from the report.
 *   SHEPHERD_K8_CLK_MEASURED=0 (or SHEPHERD_K7_CLK=0, which measures nothing): the system's report, kit #7.
 *
 *  RIGHT STICK + REAR TOUCH (GL apps; shepherd_vita_k8_pad, once per frame after shepherd_vita_pad_read): the right stick of the same
 *   newest SceCtrl sample (centred at 0, -128..127, y down) -- the game's DualShock 4 map reads it (Horizontal / Vertical_Secondary, the
 *   photo mode's camera tilt) and kit #7 never passed it on -- and the REAR touch panel as the four buttons the Vita lacks: its corners
 *   (the panel split in a left 40 %, a dead middle 20 % and a right 40 %, each side in a top and a bottom half): top-left L2, top-right R2,
 *   bottom-left L3, bottom-right R3 (the standard Vita-port layout; PS4 Remote Play's), bit 1 L2, 2 R2, 4 L3, 8 R3. A corner touched and
 *   released between two engine frames is reported down for one read, like the pad (pad_latch.h over the samples' corner bits). The rear
 *   panel is sampled only from the engine's first call (the engine calls only with its Rear Touch row on); arg 0 = rear touch off for
 *   now (the row switched Off): no SceTouch call, nothing reported. SHEPHERD_K8_PAD=0 (or SHEPHERD_K8_PLAY=0): never called (kit #7).
 *
 *  FRAME PACING (G3, GL apps; shepherd_vita_k8 op 3, the engine's frame start on the play clock): the frame starts on the display's grid of
 *   N vblanks (N = 2 / 3 / 4: 30 / 20 / 15 fps, chosen per area by the engine: Runtime/K8Play.cs) -- the call waits for the vblank N after
 *   the previous frame's start (sceDisplayWaitVblankStartMulti) unless the frame already took longer (then the grid restarts there: a late
 *   frame is never made later). N <= 1: no wait (the grid follows). Returns the vblanks waited, -1 off. Never called on the frame clock or
 *   with SHEPHERD_K8_PACING=0 / the Frame Pacing row Off (kit #7). op 4: the vblank counter (a P line / test aid). */

#include <psp2/display.h>
static int g_k8 = 1, g_k8_meas = 1;
static void k8_play_config (void)   /* (kit #8 integration: named k8_config in the track -- the boot track has one too) */
{
	g_k8 = !k7_off ("SHEPHERD_K8_PLAY");
	g_k8_meas = g_k8 && !k7_off ("SHEPHERD_K8_CLK_MEASURED");
}

/* vlx only (test): the measurement a device would make, modelled (VLX_MEASURED_ARM=<MHz>; vita/vlx/vlx.c) -- qemu's own timing is the
 * Mac's and never a Vita clock step. A weak reference: the device eboot has no such symbol (NULL, never called). */
extern int vlx_model_measured_mhz (void) __attribute__ ((weak));
/* the ARM clock to SHOW (the readout row, the overlay): the settled measured step, else the system's report */
static int k8_clk_shown (int reported)
{
#ifdef SHEPHERD_GL
	if (g_k8_meas && g_k7_clk && vlx_model_measured_mhz && vlx_model_measured_mhz () > 0)
		return vlx_model_measured_mhz ();
	if (g_k8_meas && g_k7_clk && g_clk_meas_step > 0)
		return g_clk_meas_step;
#endif
	return reported;
}
/* shepherd_vita_k6 op 3 (kit #8): kit #7's readout (its CLK readout line on a change of the report) and the clock shown */
static int k8_clk_readout (void)
{
	int a = k7_clk_readout (), s = k8_clk_shown (a);
	static int last_shown = -1;
	if (s != a && s != last_shown)
		LOG ("CLK readout at %.1f s shows the MEASURED ~%d MHz (the system reports %d: set outside scePower)", wall_s (), s, a);
	last_shown = s;
	return s;
}

#ifdef SHEPHERD_GL
/* ---- the right stick and the rear touch panel */
/* (eboot room: the rear read REUSES the front panel's sample buffer g_touch and the pad's g_pads -- all three reads run on the engine's main
 *  thread one after the other, and none keeps its samples past its own call: the pad's are read before this, the front panel's at the frame's
 *  start; so the rear panel costs no new 9 KiB + 4 KiB of bss) */
#define g_rear g_touch
#define g_rear_bits g_pads
static pad_latch g_rlatch;
static int g_rear_state;   /* 0 not set up, 1 on, -1 refused */
static int g_rear_x0, g_rear_y0, g_rear_w = 1920, g_rear_h = 890;
static unsigned g_rear_reads, g_rear_presses, g_rear_logged;
static unsigned k8_rear_corner (const SceTouchData *d)
{
	unsigned bits = 0;
	int n = d->reportNum > 8u ? 8 : (int) d->reportNum;
	for (int i = 0; i < n; i++) {
		int x = d->report [i].x - g_rear_x0, y = d->report [i].y - g_rear_y0;
		int left = x * 10 < g_rear_w * 4, right = x * 10 >= g_rear_w * 6, top = y * 2 < g_rear_h;
		if (left)
			bits |= top ? 1u : 4u;
		else if (right)
			bits |= top ? 2u : 8u;
	}
	return bits;
}
static void k8_rear_setup (void)
{
	int rs = sceTouchSetSamplingState (SCE_TOUCH_PORT_BACK, SCE_TOUCH_SAMPLING_STATE_START);
	SceTouchPanelInfo pi;
	memset (&pi, 0, sizeof (pi));
	int rp = sceTouchGetPanelInfo (SCE_TOUCH_PORT_BACK, &pi);
	if (rp >= 0 && pi.maxAaX > pi.minAaX && pi.maxAaY > pi.minAaY) {
		g_rear_x0 = pi.minAaX;
		g_rear_y0 = pi.minAaY;
		g_rear_w = pi.maxAaX - pi.minAaX + 1;
		g_rear_h = pi.maxAaY - pi.minAaY + 1;
	}
	g_rear_state = rs < 0 ? -1 : 1;
	LOG ("TOUCH rear panel as L2/R2/L3/R3: sampling rc 0x%08x, info rc 0x%08x, area %d,%d..%d,%d: L x<%d, R x>=%d, top y<%d", (unsigned) rs, (unsigned) rp,
	     pi.minAaX, pi.minAaY, pi.maxAaX, pi.maxAaY, g_rear_x0 + g_rear_w * 4 / 10, g_rear_x0 + g_rear_w * 6 / 10, g_rear_y0 + g_rear_h / 2);
}
/* Once per frame after the pad read: *rx, *ry = the right stick (the pad read's newest sample); *rear = the rear corners (bit 1 L2, 2 R2,
 * 4 L3, 8 R3; held | latched). arg (rear_on) 0: the rear panel is not read. Returns 1 (2 = a rear sample was read). */
int shepherd_vita_k8_pad (int rear_on, int32_t *rx, int32_t *ry, uint32_t *rear)
{
	*rx = g_k8_rx;
	*ry = g_k8_ry;
	*rear = 0;
	if (!rear_on)
		return 1;
	if (!g_rear_state)
		k8_rear_setup ();
	if (g_rear_state < 0)
		return 1;
	int n = sceTouchPeek (SCE_TOUCH_PORT_BACK, g_rear, SHP_TOUCH_SAMPLES);
	if (n < 1)
		return 1;
	if (n > SHP_TOUCH_SAMPLES)
		n = SHP_TOUCH_SAMPLES;
	for (int i = 0; i < n; i++) {
		g_rear_bits [i].timeStamp = g_rear [i].timeStamp;
		g_rear_bits [i].buttons = k8_rear_corner (&g_rear [i]);
	}
	int newest = 0;
	unsigned tap = 0, before = g_rlatch.reported;
	unsigned held = pad_latch_step (&g_rlatch, g_rear_bits, n, &newest, &tap);
	g_rear_reads++;
	unsigned down = held & ~before;
	if (down) {
		g_rear_presses++;
		if (g_rear_logged < 20) {
			g_rear_logged++;
			LOG ("TOUCH rear %s%s%s%s down at read %u%s", down & 1 ? "L2 " : "", down & 2 ? "R2 " : "", down & 4 ? "L3 " : "", down & 8 ? "R3 " : "", g_rear_reads,
			     tap ? " (a tap between frames)" : "");
		}
	}
	*rear = held;
	return 3;
}
static void k8_end_line (void)
{
	if (g_rear_state > 0)
		LOG ("TOUCH rear end: %u reads, %u corner presses, %u taps latched", g_rear_reads, g_rear_presses, g_rlatch.taps);
}

/* ---- G3 frame pacing: the frame start on the display's grid of N vblanks */
static int g_pace_last = -1;
static unsigned g_pace_frames, g_pace_waits, g_pace_late;
static int k8_pace (int n)
{
	int now = sceDisplayGetVcount ();
	g_pace_frames++;
	if (g_pace_last < 0 || n <= 1) {
		g_pace_last = now;
		return 0;
	}
	int ahead = g_pace_last + n - now;   /* vblanks still to wait (wraps safely: the counter is an int) */
	int waited = 0;
	if (ahead > 0 && ahead <= n) {
		sceDisplayWaitVblankStartMulti ((unsigned int) ahead);
		waited = ahead;
		g_pace_waits++;
		now = g_pace_last + n;
	} else if (ahead <= 0)
		g_pace_late++;   /* the frame took longer than N vblanks: the grid restarts now (a late frame is never made later) */
	g_pace_last = now;
	return waited;
}
#endif

/* the engine's kit #8 calls (Runtime/K8Play.cs; a P/Invoke, GC-safe: kernel calls and the log's lock only). op 0: state (1 = on, 2 = the
 * measured clock shown); 1: the ARM clock to show (the overlay, twice a second; no log line); 3: frame pacing, arg = N vblanks (GL apps),
 * returns the vblanks waited; 4: the vblank counter (GL apps). -1: not here. */
int shepherd_vita_k8 (int op, int arg)
{
	switch (op) {
	case 0:
		return (g_k8 ? 1 : 0) | (g_k8_meas ? 2 : 0);
	case 1:
		return k8_clk_shown (scePowerGetArmClockFrequency ());
#ifdef SHEPHERD_GL
	case 3:
		return k8_pace (arg);
	case 4:
		return sceDisplayGetVcount ();
#endif
	}
	(void) arg;
	return -1;
}
