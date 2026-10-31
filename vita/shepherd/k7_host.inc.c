/* ---------------------------------------------------------------- device kit #7, track probes (vita/notes/kit7-probes.md)
 * Included by host.c (right before the power / watchdog thread). Three host parts of kit #7, each with an env-file switch back to kit #6:
 *
 *  CLOCKS (roadmap "500 MHz with PSVshellPlus", kit #6's device run: the start-up 500 request was refused in every app, and a PSVshellPlus
 *  change to 500 mid-session still read 444 -- the host set 444 at launch, which overrides a plugin's higher launch clock):
 *   - a launch clock ABOVE the app's (arm 444 / bus 222 / gpu 222 / xbar 166) is kept, never lowered: a PSVshellPlus profile saved for the
 *     title sets the clocks when the app starts, and the app no longer forces them back to 444 (one CLK keep line says so). "444" asked by
 *     Settings > Graphics > CPU Speed then means the app's normal clock: 444, or the launch clock when a plugin set a higher one;
 *   - a CLK line on every clock change: the ones the app makes (as before), any the watchdog sees that the app did not make (every 5 s:
 *     a plugin or the system), and every change of the CPU Clock readout (Settings > Graphics, once a second while the rows exist);
 *   - the ARM clock MEASURED, not only reported: a chain of 64 dependent adds per loop pass runs at one add per cycle on the Cortex-A9, so
 *     cycles / microseconds = MHz (best of 4 runs of ~0.5 ms; ~1-2 % low from the loop's own branch). A CLK measured line at start, at the
 *     end and whenever the measured clock settles on another of the Vita's clock steps -- so a clock set outside scePower (PSVshell writes
 *     the clock registers itself) shows in the log even when scePowerGetArmClockFrequency still reports 444. Under vlx (qemu) the measured
 *     figure means nothing (the line says the number; nothing depends on it: the readout and every decision use the system's report).
 *   SHEPHERD_K7_CLK=0: kit #6 -- 444/222/222/166 forced at launch, no keep / change / readout / measured lines, "444" = 444.
 *   (The headless 00011 gets the launch-clock rule only: no watchdog lines, no measurement -- its eboot room.)
 *
 *  DEVICE PROFILE (roadmap "Storage read test": "the results are saved as a small device profile ... for the repack app"): every start of
 *   every GL app (the play apps and the measurement app; not the headless 00011, whose eboot has 12-138 KiB of room) writes
 *   ux0:data/shepherd/device-profile.txt -- the model asked from the system (sceKernelGetModelForCDialog: a PS Vita or a
 *   PS TV; PCH-2000 vs PCH-1000 by the built-in 1 GB store imc0, which only the PCH-2000 / PS TV have), the firmware
 *   (sceKernelGetSystemSwVersion), every store with its size, free space and cluster size (sceIoDevctl 0x3001: ux0, imc0, uma0, grw0,
 *   xmc0), the read speeds the measurement app last measured per store (ux0:data/shepherd/probes/storage.txt, kit #7's storage read test),
 *   the clocks (launch, now, measured), whether the Devolver splash table is on the card, and who wrote it (app, build). One DP line in the
 *   host log; a WARN DP line when data/video-lengths.tsv is missing (kit #6's device run had no splash time because of that).
 *   SHEPHERD_K7_PROFILE=0: no file, no DP lines.
 *
 *  SUSPEND / RESUME (roadmap "P3 console-style suspend/resume", GL apps only; the frame-clock apps' game time never sees a suspension):
 *   on the Vita a PS-button suspend or standby stops every thread of the app while the process clock goes on, so the play clock's next frame
 *   saw the whole gap and Unity's clamp turned it into one 1/3 s step of game time (with up to ~16 FixedUpdate catch-up steps). Now the
 *   watchdog ticks every 250 ms; a tick (or an engine frame, op 4) that finds the last tick more than 1.5 s old books the gap as a
 *   suspension (one SUSP line), the engine's play clock (Runtime/Time.cs) leaves the booked time out of the frame delta (realtimeSinceStartup
 *   keeps it, as Unity's does across a pause) -- the game resumes where it stopped, as a console game does -- and the clocks are checked and
 *   raised again where a standby lowered them below the app's (CLK resume line; a clock above the app's -- a plugin's -- is kept, never
 *   lowered). SHEPHERD_K7_SUSPEND=0 (and SHEPHERD_STRICT_ORIGINAL=1): kit #6 (the 5 s watchdog loop; op 4 answers -1, the engine keeps
 *   Unity's clamp). The watchdog's own 5 s work (a card write of the live black box) is outside the watched time (k7_susp_busy).
 *   Device kit #9 (track device, vita/notes/kit9-device.md): kit #7's and kit #8's device runs never showed a SUSP line although the owner
 *   pressed the PS button and POWER (STEP 9): on the Vita the PROCESS clock STOPS while the app is suspended, so there is no gap to book --
 *   and the play clock (process time too) never sees one, so the game already resumes where it stopped. The suspension is now seen with the
 *   RTC: every watchdog tick compares sceRtcGetCurrentTick (the wall clock, which runs on through a suspension) minus the process clock with
 *   the tick before; a jump of more than 1.5 s is a suspension -- one SUSP line ("seen by the RTC"), the clocks raised again where a standby
 *   lowered them (CLK resume) -- and NOTHING is booked for the play clock (it never saw the time). A gap in the process clock itself (kit #7's
 *   model) is still booked as before. SHEPHERD_K9_SUSP_RTC=0: kit #8 (the process-clock watch only). A change of the system clock while the
 *   app runs looks the same (the line says so); it only re-checks the clocks. */

static int g_clock_launch [4] = { -1, -1, -1, -1 };   /* arm, bus, gpu, xbar at launch; restored at exit when they were changed */
static int g_clock_set;                                /* 1 = clocks_start changed them */
static int g_clock_target [4] = { 444, 222, 222, 166 };   /* kit #7: what the app keeps the clocks at (a higher launch clock is kept) */
static int g_clock_base_arm = 444;                     /* kit #7: the app's normal ARM clock -- 444, or a higher launch clock (CPU Speed "444") */
static int g_k7_clk = 1, g_k7_profile = 1;
#ifdef SHEPHERD_GL
static int g_k7_susp = 1;
static int g_k9_susp_rtc = 1;   /* kit #9 (device track): the P3 watch also reads the RTC (SHEPHERD_K9_SUSP_RTC=0: kit #8) */
#endif
static int k7_off (const char *k) { const char *e = getenv (k); return e && !strcmp (e, "0"); }
/* device kit #9, track device: 500 MHz BY DEFAULT in the play apps (owner decision 2026-10-22) -- shepherd_vita_k6 op 0's bit 28 tells the
 * engine's CPU Speed row (Runtime/VitaToggles.cs) that this app's default is 500; the measurement app 00099 (SHP_TEST_CLOCK) and the headless
 * 00011 keep 444. The app still cannot raise the clock itself (the stock kernel refuses 500: rc 0x802b0000; no plugin exports a way): the row
 * keeps 500 as the choice and says what is needed. SHEPHERD_K9_ARM500_DEFAULT=0: kit #8 (444 by default). */
static int k9_state_bits (void)
{
#ifdef SHP_PLAY_APP
	return k7_off ("SHEPHERD_K9_ARM500_DEFAULT") ? 0 : (1 << 28);
#else
	return 0;
#endif
}
static void k7_config (void)
{
	g_k7_clk = !k7_off ("SHEPHERD_K7_CLK");
	g_k7_profile = !k7_off ("SHEPHERD_K7_PROFILE");
#ifdef SHEPHERD_GL
	/* (kit #7 integration review: the strict original switch -- Runtime/StrictOriginal.cs, which puts SHEPHERD_K7_SUSPEND=0 among the PC
	 *  behaviours -- sets it in the managed environment only, after this point; so the host reads SHEPHERD_STRICT_ORIGINAL=1 itself and turns
	 *  P3's watch off too: kit #6's 5 s loop, op 4 = -1, no SUSP line that would claim the play clock left a suspension out) */
	const char *so = getenv ("SHEPHERD_STRICT_ORIGINAL");
	g_k7_susp = !k7_off ("SHEPHERD_K7_SUSPEND") && !(so && !strcmp (so, "1"));
	g_k9_susp_rtc = !k7_off ("SHEPHERD_K9_SUSP_RTC");   /* kit #9 device track: the RTC sees a suspension the process clock does not */
#endif
}

#ifdef SHEPHERD_GL   /* (the headless engine-AOT app, 00011, has 12-138 KiB of eboot room left: it gets the launch-clock rule only) */
#include <psp2/rtc.h>   /* kit #9: sceRtcGetCurrentTick (the P3 watch) */
/* ---- the ARM clock, measured */
static unsigned __attribute__ ((noinline)) k7_spin (unsigned n)
{
#if defined (__arm__)
	unsigned x = 0;
	__asm__ volatile ("1:\n\t"
			  ".rept 64\n\t"
			  "adds %0, %0, #1\n\t"
			  ".endr\n\t"
			  "subs %1, %1, #1\n\t"
			  "bne 1b\n\t"
			  : "+l" (x), "+l" (n) : : "cc");
	return x;
#else
	volatile unsigned x = 0;
	while (n--)
		for (int i = 0; i < 64; i++)
			x++;
	return x;
#endif
}
/* MHz = cycles per microsecond: 4096 passes x 64 dependent adds = 262,144 cycles (~0.5 ms at 500 MHz, ~0.6 ms at 444), best of 4 runs
 * (an interrupt or a preemption only ever adds time). 0 = no measurement. */
static int k7_measure_mhz (void)
{
	const unsigned n = 4096;
	SceUInt64 best = ~0ull;
	for (int r = 0; r < 4; r++) {
		SceUInt64 t0 = sceKernelGetProcessTimeWide ();
		k7_spin (n);
		SceUInt64 dt = sceKernelGetProcessTimeWide () - t0;
		if (dt && dt < best)
			best = dt;
	}
	return best == ~0ull ? 0 : (int) ((64ull * n + best / 2) / best);
}
/* the Vita clock step a measurement is (-8 % .. +6 %: the loop's branch makes it ~1-2 % low), or 0 */
static int k7_clk_step (int mhz)
{
	static const int steps [] = { 41, 83, 111, 166, 222, 333, 444, 500 };
	for (unsigned i = 0; i < sizeof steps / sizeof steps [0]; i++)
		if (mhz * 100 >= steps [i] * 92 && mhz * 100 <= steps [i] * 106)
			return steps [i];
	return 0;
}

static pthread_mutex_t g_clk_mu = PTHREAD_MUTEX_INITIALIZER;
static int g_clk_seen [4] = { -1, -1, -1, -1 };   /* the clocks as last logged (the app's own sets, the watchdog, the readout) */
static int g_clk_meas, g_clk_meas_step, g_clk_meas_pend;
static void k7_clk_read (int c [4])
{
	c [0] = scePowerGetArmClockFrequency ();
	c [1] = scePowerGetBusClockFrequency ();
	c [2] = scePowerGetGpuClockFrequency ();
	c [3] = scePowerGetGpuXbarClockFrequency ();
}
/* after the app itself set a clock: the read-back is what the watchdog compares with next (so it never reports the app's own change) */
static void k7_clk_note_set (void)
{
	int c [4];
	k7_clk_read (c);
	pthread_mutex_lock (&g_clk_mu);
	memcpy (g_clk_seen, c, sizeof c);
	pthread_mutex_unlock (&g_clk_mu);
}
static void k7_clk_measured_line (const char *when, int reported)
{
	int m = k7_measure_mhz (), s = k7_clk_step (m);
	g_clk_meas = m;
	if (s)
		g_clk_meas_step = s;
	LOG ("CLK measured %s: ~%d MHz (%d MHz timed: 64 dependent adds per pass = 64 cycles on the Cortex-A9; the system reports arm %d MHz)%s", when,
	     s ? s : m, m, reported, !s ? " -- not one of the Vita's clock steps (under vlx this figure is the Mac's, meaningless)"
	     : s != reported ? " -- they DIFFER: the clock was set outside scePower (e.g. by PSVshell), or the report is stale" : "");
}
static void k7_clk_start (void)
{
	if (!g_k7_clk)
		return;
	k7_clk_note_set ();
	k7_clk_measured_line ("at start", g_clk_seen [0]);
}
/* the watchdog (power thread, every 5 s): a clock change the app did not make, and the measured clock settling on another step */
static void k7_clk_watch (void)
{
	if (!g_k7_clk)
		return;
	int c [4], was [4];
	k7_clk_read (c);
	pthread_mutex_lock (&g_clk_mu);
	memcpy (was, g_clk_seen, sizeof was);
	int changed = memcmp (c, g_clk_seen, sizeof c) != 0;
	memcpy (g_clk_seen, c, sizeof c);
	pthread_mutex_unlock (&g_clk_mu);
	if (changed && was [0] >= 0)
		LOG ("CLK change at %.1f s, seen by the watchdog (not made by the app: a plugin such as PSVshell, or the system): arm %d -> %d, bus %d -> %d, "
		     "gpu %d -> %d, xbar %d -> %d MHz (the app keeps it)", wall_s (), was [0], c [0], was [1], c [1], was [2], c [2], was [3], c [3]);
	int m = k7_measure_mhz (), s = k7_clk_step (m);
	g_clk_meas = m;
	if (s && s != g_clk_meas_step) {
		if (s == g_clk_meas_pend) {   /* two readings in a row: settled */
			LOG ("CLK measured at %.1f s: the ARM cores now run at ~%d MHz (was ~%d; %d MHz timed) while the system reports arm %d MHz%s", wall_s (), s,
			     g_clk_meas_step, m, c [0], s != c [0] ? " -- they DIFFER: the clock was set outside scePower (e.g. by PSVshell)" : "");
			g_clk_meas_step = s;
			g_clk_meas_pend = 0;
		} else
			g_clk_meas_pend = s;
	} else
		g_clk_meas_pend = 0;
}
/* shepherd_vita_k6 op 3, the CPU Clock readout row (about once a second while the rows exist): the system's report as before, plus a CLK
 * line whenever it differs from the clock last logged */
static int k7_clk_readout (void)
{
	int a = scePowerGetArmClockFrequency ();
	if (!g_k7_clk)
		return a;
	pthread_mutex_lock (&g_clk_mu);
	int was = g_clk_seen [0];
	g_clk_seen [0] = a;
	pthread_mutex_unlock (&g_clk_mu);
	if (a != was)
		LOG ("CLK readout (Settings > Graphics > CPU Clock) at %.1f s: arm %d MHz (was %d; last measured ~%d MHz)", wall_s (), a, was,
		     g_clk_meas_step ? g_clk_meas_step : g_clk_meas);
	return a;
}
/* after a suspension (P3): a standby may leave the clocks at the system's own; raise the app's again where they are BELOW them (CLK resume
 * line). A clock ABOVE the app's is kept, never lowered (kit #7 review): a plugin's (PSVshellPlus) -- the launch rule's "kept, never lowered"
 * for a change made mid-session, which the watchdog only logs -- and it becomes the clock the app keeps from then on. */
static void k7_clk_reapply (void)
{
	if (!g_clock_set)
		return;
	int c [4], up = 0, kept = 0;
	k7_clk_read (c);
	for (int i = 0; i < 4; i++) {
		if (c [i] > g_clock_target [i]) {
			g_clock_target [i] = c [i];
			kept++;
		} else if (c [i] < g_clock_target [i])
			up++;
	}
	if (!up) {
		LOG ("CLK resume: arm %d bus %d gpu %d xbar %d MHz, as the app had them%s: nothing to set", c [0], c [1], c [2], c [3],
		     kept ? " or above (a plugin's clock: kept, never lowered)" : "");
		k7_clk_note_set ();
		return;
	}
	int r [4] = { 0, 0, 0, 0 };
	if (c [0] < g_clock_target [0]) r [0] = scePowerSetArmClockFrequency (g_clock_target [0]);
	if (c [1] < g_clock_target [1]) r [1] = scePowerSetBusClockFrequency (g_clock_target [1]);
	if (c [2] < g_clock_target [2]) r [2] = scePowerSetGpuClockFrequency (g_clock_target [2]);
	if (c [3] < g_clock_target [3]) r [3] = scePowerSetGpuXbarClockFrequency (g_clock_target [3]);
	int b [4];
	k7_clk_read (b);
	LOG ("CLK resume: the clocks were arm %d bus %d gpu %d xbar %d MHz after the suspension; raised to %d/%d/%d/%d where below%s: rc 0x%08x 0x%08x 0x%08x 0x%08x, read back %d/%d/%d/%d",
	     c [0], c [1], c [2], c [3], g_clock_target [0], g_clock_target [1], g_clock_target [2], g_clock_target [3],
	     kept ? " (one above the app's: a plugin's, kept)" : "", (unsigned) r [0], (unsigned) r [1], (unsigned) r [2], (unsigned) r [3], b [0], b [1], b [2], b [3]);
	k7_clk_note_set ();
}

/* ---- P3: suspend / resume (GL apps) */
#define K7_SUSP_TICK_US 250000u
#define K7_SUSP_GAP_US 1500000u
static pthread_mutex_t g_susp_mu = PTHREAD_MUTEX_INITIALIZER;
static SceUInt64 g_susp_tick;          /* the last time the watchdog or an engine frame saw the app running (process clock, us) */
static volatile unsigned g_susp_ms;    /* suspended time booked so far, ms */
static unsigned g_susp_n;
static volatile int g_susp_reapply;
static int g_susp_hold;                /* kit #7 review: 1 while the measurement app's NGS probe keeps every other core busy on purpose (its spinner
                                        * threads may starve this watchdog for over 1.5 s): nothing is booked meanwhile */
static int g_susp_busy;                /* kit #7 integration review: 1 while the watchdog does its own 5 s work (the live black box's card writes,
                                        * a STALL? line, a clock line): that time is not a suspension -- nothing is booked meanwhile */
/* the watchdog's tick and every engine frame on the play clock (op 4): a gap of more than 1.5 s since the last one = the app was suspended
 * (no thread ran); booked once, minus one tick (the part the watchdog would have slept anyway). Returns the total booked, ms. */
/* kit #9: the RTC minus the process clock at the watchdog's last tick (us); 0 = not read yet */
static long long g_susp_rtc_off;
static int g_susp_rtc_have;
static unsigned g_susp_rtc_n, g_susp_rtc_ms;
static unsigned k7_susp_book (int engine)
{
	SceRtcTick rt;
	int rtc_ok = !engine && g_k9_susp_rtc && sceRtcGetCurrentTick (&rt) >= 0;   /* (the watchdog only: the engine's op 4 stays kit #8's) */
	pthread_mutex_lock (&g_susp_mu);
	SceUInt64 now = sceKernelGetProcessTimeWide (), last = g_susp_tick;
	unsigned gap_ms = 0, rtc_gap_ms = 0;
	if (last && now > last + K7_SUSP_GAP_US && !g_susp_hold && !g_susp_busy) {
		gap_ms = (unsigned) ((now - last - K7_SUSP_TICK_US) / 1000u);
		g_susp_ms += gap_ms;
		g_susp_n++;
		g_susp_reapply = 1;
	}
	if (rtc_ok) {   /* kit #9: the wall clock ran on while the process clock stood still = a suspension the play clock never saw */
		long long off = (long long) rt.tick - (long long) now;
		if (g_susp_rtc_have && off - g_susp_rtc_off > (long long) K7_SUSP_GAP_US && !gap_ms) {
			rtc_gap_ms = (unsigned) ((off - g_susp_rtc_off) / 1000);
			g_susp_rtc_ms += rtc_gap_ms;
			g_susp_rtc_n++;
			g_susp_n++;
			g_susp_reapply = 1;
		}
		g_susp_rtc_off = off;
		g_susp_rtc_have = 1;
	}
	g_susp_tick = now;
	unsigned total = g_susp_ms, n = g_susp_n, rtc_total = g_susp_rtc_ms;
	pthread_mutex_unlock (&g_susp_mu);
	if (rtc_gap_ms)
		LOG ("SUSP #%u at %.1f s: the app was suspended for ~%.1f s (the PS button or standby -- seen by the RTC: the process clock stops while an app is "
		     "suspended, so it never saw the gap; or the system clock was changed): the play clock never saw it either -- nothing to leave out, the game "
		     "resumes where it stopped; %.1f s seen this way in all; the clocks are checked again (kit #9; SHEPHERD_K9_SUSP_RTC=0: kit #8's process-clock watch only)",
		     n, wall_s (), rtc_gap_ms / 1000.0, rtc_total / 1000.0);
	if (gap_ms)
		LOG ("SUSP #%u at %.1f s: the app was suspended for ~%.1f s (nothing ran from %.1f s on: the PS button or standby; booked by %s): the play clock "
		     "leaves it out -- the game resumes where it stopped, as a console game does (kit #7 P3; SHEPHERD_K7_SUSPEND=0 = kit #6: Unity's one "
		     "frame clamped to 1/3 s); %.1f s booked in all; the clocks are checked again", n, wall_s (), gap_ms / 1000.0, (double) (last - g_t0) / 1e6,
		     engine ? "the engine's next frame" : "the watchdog", total / 1000.0);
	return total;
}

/* the NGS probe (k7_probes.inc.c), around its spinner threads: on = book nothing (the watchdog may be starved on purpose); off = the tick
 * restarts now, so a watchdog that wakes late after the window does not see the window as a suspension */
static void k7_susp_hold (int on)
{
	pthread_mutex_lock (&g_susp_mu);
	g_susp_hold = on;
	g_susp_tick = sceKernelGetProcessTimeWide ();
	pthread_mutex_unlock (&g_susp_mu);
}

/* the watchdog around its own 5 s work (kit #7 integration review: a slow card write of blackbox-live.txt inside a long frame -- a load --
 * was booked as a suspension, the gap being the watchdog's own I/O time): on = book nothing meanwhile (nor at an engine frame that ends
 * during it); off = the tick restarts now. The gap before the work is booked first (power_thread). Cost: a real suspension that falls inside
 * those few milliseconds is not booked -- its frame is clamped as in kit #6. */
static void k7_susp_busy (int on)
{
	pthread_mutex_lock (&g_susp_mu);
	g_susp_busy = on;
	g_susp_tick = sceKernelGetProcessTimeWide ();
	pthread_mutex_unlock (&g_susp_mu);
}

/* ---- the device profile (GL apps: the measurement app and the play apps) */
#include <psp2/io/devctl.h>
#include <psp2/kernel/modulemgr.h>
static void k7_profile (void)
{
	if (!g_k7_profile)
		return;
	SceUInt64 t0 = sceKernelGetProcessTimeWide ();
	const unsigned cap = 6144;
	char *b = (char *) malloc (cap);
	if (!b)
		return;
	unsigned n = 0;
#define PF(...) do { int w_ = snprintf (b + n, n < cap ? cap - n : 0, __VA_ARGS__); if (w_ > 0) n += (unsigned) w_; if (n >= cap) n = cap - 1; } while (0)
	PF ("# Shepherd device profile (kit #7) -- written by the app at every start; read by the repack app. One key=value per line.\n");
	char model [96] = "unknown", fw [48] = "unknown", stores [192] = "";
	int nst = 0;
	int mk = sceKernelGetModelForCDialog ();
	SceKernelSystemSwVersion sw;
	memset (&sw, 0, sizeof sw);
	sw.size = sizeof sw;
	int swr = sceKernelGetSystemSwVersion (&sw);
	static const char *const devs [] = { "ux0:", "imc0:", "uma0:", "grw0:", "xmc0:" };
	int imc = 0;
	char sl [5][128];
	for (unsigned i = 0; i < sizeof devs / sizeof devs [0]; i++) {
		SceIoDevInfo di;
		memset (&di, 0, sizeof di);
		int r = sceIoDevctl (devs [i], 0x3001, NULL, 0, &di, sizeof di);
		char d [8];
		snprintf (d, sizeof d, "%.*s", (int) strlen (devs [i]) - 1, devs [i]);
		if (r >= 0 && di.max_size > 0) {
			snprintf (sl [i], sizeof sl [i], "store.%s=mounted, total %.2f GiB, free %.2f GiB, cluster %u B", d, di.max_size / 1073741824.0,
				  di.free_size / 1073741824.0, (unsigned) di.cluster_size);
			if (i == 1)
				imc = 1;
			nst++;
			size_t sn = strlen (stores);
			snprintf (stores + sn, sizeof stores - sn, "%s%s %.1f/%.1f GiB free", sn ? ", " : "", d, di.free_size / 1073741824.0, di.max_size / 1073741824.0);
		} else
			snprintf (sl [i], sizeof sl [i], "store.%s=not mounted (devctl 0x%08x)", d, (unsigned) r);
	}
	if (mk == 0x20000)
		snprintf (model, sizeof model, "PS TV (VTE-1000)");
	else if (mk == 0x10000)
		snprintf (model, sizeof model, imc ? "PCH-2000" : "PCH-1000");
	else
		snprintf (model, sizeof model, "unknown (0x%x)", (unsigned) mk);
	if (swr >= 0)
		snprintf (fw, sizeof fw, "%x.%02x", (unsigned) (sw.version >> 24) & 0xff, (unsigned) (sw.version >> 16) & 0xff);
	if (g_k8_model) {
		/* kit #8 boot track: the model from the hardware and the real firmware (k8_boot.inc.c); kit #7's answers kept as model.kit7 */
		K8Model km;
		k8_model_read (&km, imc, mk);
		PF ("model=%s\n", km.model);
		PF ("model.how=%s\n", km.how);
		PF ("model.signals=%s\n", km.signals);
		PF ("model.kit7=%s (kit #7's rule: sceKernelGetModelForCDialog 0x%x + imc0 %s)\n", model, (unsigned) mk, imc ? "mounted" : "not mounted");
		PF ("firmware=%s\nfirmware.raw=0x%08x (_vshSblGetSystemSwVersion rc 0x%08x: the real firmware%s)\n", km.fw, km.fw_raw, (unsigned) km.fw_rc,
		    km.fw_rc < 0 || !km.fw_raw ? "; it did not answer, so firmware= is the reported one" : "");
		PF ("firmware.reported=%s\nfirmware.reported.raw=0x%08x (sceKernelGetSystemSwVersion rc 0x%08x, '%.28s'; %s)\n", km.fw_reported, km.fw_reported_raw,
		    (unsigned) km.fw_reported_rc, sw.versionString, km.spoofed ? "SPOOFED: it differs from the real firmware (HENkaku's version spoof)" : "= the real firmware");
		snprintf (model, sizeof model, "%s", km.model);
		snprintf (fw, sizeof fw, "%s%s%s", km.fw, km.spoofed ? ", reported as " : "", km.spoofed ? km.fw_reported : "");
	} else {
	PF ("model=%s\n", model);
	PF ("model.how=sceKernelGetModelForCDialog 0x%x (0x10000 = PS Vita handheld, 0x20000 = PS TV); %s\n", (unsigned) mk,
	    mk == 0x10000 ? (imc ? "imc0 (the built-in 1 GB store) is mounted: only the PCH-2000 has one" : "no imc0 mounted: a PCH-1000 (a PCH-2000 whose built-in store is not mounted would look the same)")
			  : "");
	PF ("firmware=%s\nfirmware.raw=0x%08x (sceKernelGetSystemSwVersion rc 0x%08x, '%.28s'; a spoofed version shows the spoofed one)\n", fw, (unsigned) sw.version,
	    (unsigned) swr, sw.versionString);
	}
	for (unsigned i = 0; i < sizeof devs / sizeof devs [0]; i++)
		PF ("%s\n", sl [i]);
	{
		/* the speeds the measurement app last measured (kit #7 storage read test: its lines are copied as they are) */
		void *sd;
		unsigned ss;
		if (read_file (DIR_ "/probes/storage.txt", &sd, &ss) == 0) {
			int lines = 0;
			for (char *l = (char *) sd; l && *l; ) {
				char *nl = strchr (l, '\n');
				if (nl) *nl = 0;
				if (!strncmp (l, "speed.", 6) || !strncmp (l, "measured.", 9)) { PF ("%s\n", l); lines++; }
				l = nl ? nl + 1 : NULL;
			}
			free (sd);
			if (!lines)
				PF ("speed=none in " DIR_ "/probes/storage.txt\n");
		} else
			PF ("speed=not measured yet (Shepherd Measure, SHPD00099, measures every mounted store at the end of its run: " DIR_ "/probes/storage.txt)\n");
	}
	int c [4];
	k7_clk_read (c);
	PF ("clock.launch=%d/%d/%d/%d MHz (arm/bus/gpu/xbar)\nclock.now=%d/%d/%d/%d MHz\nclock.measured=%d MHz\n", g_clock_launch [0], g_clock_launch [1],
	    g_clock_launch [2], g_clock_launch [3], c [0], c [1], c [2], c [3], g_clk_meas_step ? g_clk_meas_step : g_clk_meas);
	SceKernelFreeMemorySizeInfo fi;
	memset (&fi, 0, sizeof fi);
	fi.size = sizeof fi;
	sceKernelGetFreeMemorySize (&fi);
	PF ("memory.free_at_start=user %u KiB, cdram %u KiB, phycont %u KiB (after this app's eboot and %u MiB newlib heap)\n", (unsigned) fi.size_user / 1024,
	    (unsigned) fi.size_cdram / 1024, (unsigned) fi.size_phycont / 1024, (unsigned) SHP_NEWLIB_HEAP_MB);
	int splash = 0, named = 0;
	{
		void *td;
		unsigned ts;
		if (read_file (DIR_ "/data/video-lengths.tsv", &td, &ts) == 0) {
			splash = splash_rows_scan ((char *) td, &named) ? 1 : -1;
			free (td);
		}
	}
	PF ("splash.table=%s\n", splash > 0 ? "present (data/video-lengths.tsv lists the Devolver clip: its 6.27 s are kept)"
				 : splash < 0 ? "present but without a Devolver row the engine takes" : "MISSING (data/video-lengths.tsv: the Devolver logo takes 0 s)");
	PF ("written.by=%s build %s, %.1f s after its start\n", SHP_LOG_NAME, SHP_BUILD_ID, wall_s ());
#undef PF
	int ok = -1;
	SceUID fd = sceIoOpen (DIR_ "/device-profile.tmp", SCE_O_WRONLY | SCE_O_CREAT | SCE_O_TRUNC, 0777);
	if (fd >= 0) {
		ok = sceIoWrite (fd, b, n) == (int) n ? 0 : -2;
		sceIoClose (fd);
		if (ok == 0) {
			sceIoRemove (DIR_ "/device-profile.txt");
			ok = sceIoRename (DIR_ "/device-profile.tmp", DIR_ "/device-profile.txt") >= 0 ? 0 : -3;
		}
	}
	free (b);
	LOG ("DP device profile: %s, firmware %s, %d store(s) mounted (%s) -> %s (%u bytes, %.0f ms; SHEPHERD_K7_PROFILE=0: none)", model, fw, nst, stores,
	     ok == 0 ? DIR_ "/device-profile.txt" : "NOT written", n, (double) (sceKernelGetProcessTimeWide () - t0) / 1000.0);
	const char *vl = getenv ("SHEPHERD_VIDEO_LENGTHS"), *k6v = getenv ("SHEPHERD_K6_VIDEO"), *k6e = getenv ("SHEPHERD_K6_ENGINE");
	int splash_wanted = !(vl && *vl) && !(k6v && !strcmp (k6v, "0")) && !(k6e && !strcmp (k6e, "0"));
	if (splash_wanted && splash == 0)
		LOG ("WARN DP " DIR_ "/data/video-lengths.tsv is MISSING: the Devolver logo takes 0 s instead of the PC game's 6.27 s (kit #6's device run "
		     "had this). push-kit.sh copies it; by hand: the kit's data/shepherd/data/video-lengths.tsv into " DIR_ "/data/");
}
#else   /* headless (00011): the launch-clock rule only (clocks_start / k6_arm); no watchdog lines, no measurement, no profile */
static void k7_clk_note_set (void) { }
static void k7_clk_start (void) { }
static void k7_clk_watch (void) { }
static void k7_clk_measured_line (const char *when, int reported) { (void) when; (void) reported; }
static int k7_clk_readout (void) { return scePowerGetArmClockFrequency (); }
static void k7_profile (void) { }
#endif
