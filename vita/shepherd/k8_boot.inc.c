/* ---------------------------------------------------------------- device kit #8, track boot (vita/notes/kit8-boot.md)
 * Included by host.c (right before k7_host.inc.c). The host parts of the kit #8 boot rows, each with an env-file switch back to kit #7:
 *
 *  MODEL + REAL FIRMWARE (the kit #7 follow-up: kit #7's device profile said "PCH-1000 / 3.73" on the owner's PCH-2000 on 3.65 -- a slim
 *   with a memory card inserted does not mount its built-in 1 GB store imc0, which was kit #7's only PCH-2000 sign, and HENkaku's version
 *   spoof makes sceKernelGetSystemSwVersion report 3.73):
 *   - the model from the HARDWARE, not from what is mounted: SceVshBridge's product checks (vshSblAimgrIsDolce / IsVITA: the console's
 *     product type, a PS TV or a handheld) and the syscon's capabilities -- vshSysconIsMCEmuCapable (the built-in flash can stand in for a
 *     memory card: the PCH-2000 and the PS TV have that flash, the PCH-1000 does not) and vshSysconHasWWAN (the 3G radio of the PCH-1100);
 *     the old signals are kept as cross-checks (sceKernelGetModelForCDialog, imc0 mounted) and every raw value is written to the profile
 *     (model.signals), so the owner's run shows whether the rule holds (the decision is an ESTIMATE until a device run confirms it on the
 *     owner's PCH-2000 with its memory card in). Rule: PS TV when the product type or the CDialog model says so; else PCH-2000 when the
 *     syscon reports the memory-card emulation (or imc0 is mounted: only a slim / PS TV has it); else PCH-1100 (3G) with a WWAN radio; else
 *     PCH-1000. A check that does not answer (a negative return) falls back to kit #7's rule for that signal and says so.
 *   - the REAL firmware: _vshSblGetSystemSwVersion ("real system firmware information", vita-headers), next to the reported one
 *     (sceKernelGetSystemSwVersion: what a spoof shows); "firmware=" is the real one, "firmware.reported=" the other, and the line says
 *     "spoofed" when they differ.
 *   - the console ID's product fields only (bytes 4..7 of _vshSblAimgrGetConsoleId: target / product sub code), never the whole ID (it is
 *     unique to the console).
 *   SHEPHERD_K8_MODEL=0 (or SHEPHERD_K8_BOOT=0): kit #7's profile -- the imc0 rule and the reported firmware, no new keys. */

#ifdef SHEPHERD_GL
#include <psp2/vshbridge.h>
#endif

static int k8_off (const char *k)
{
	const char *e = getenv (k);
	if (e && !strcmp (e, "0"))
		return 1;
	const char *all = getenv ("SHEPHERD_K8_BOOT");   /* the whole track (host side) back to kit #7 */
	return all && !strcmp (all, "0");
}

static int g_k8_model = 1;

static void k8_boot_config (void)
{
	g_k8_model = !k8_off ("SHEPHERD_K8_MODEL");
}

#ifdef SHEPHERD_GL   /* (the profile is a GL-app feature: the headless 00011 writes none) */
/* one firmware word (0xMMmmxxxx) as "M.mm" */
static void k8_fw_text (unsigned v, char *out, size_t n) { snprintf (out, n, "%x.%02x", (v >> 24) & 0xff, (v >> 16) & 0xff); }

typedef struct {
	char model [40];
	char signals [256];
	char how [320];
	char fw [16], fw_reported [16];
	unsigned fw_raw, fw_reported_raw;
	int fw_rc, fw_reported_rc, spoofed;
} K8Model;

/* imc_mounted: kit #7's store scan (imc0 answered devctl 0x3001); cdialog: sceKernelGetModelForCDialog () */
static void k8_model_read (K8Model *m, int imc_mounted, int cdialog)
{
	memset (m, 0, sizeof *m);
	int dolce = vshSblAimgrIsDolce (), vita = vshSblAimgrIsVITA ();
	int mcemu = vshSysconIsMCEmuCapable (), wwan = vshSysconHasWWAN ();
	int card = vshMemoryCardGetCardInsertState ();
	char cid [32];
	memset (cid, 0, sizeof cid);
	int cidr = _vshSblAimgrGetConsoleId (cid);
	snprintf (m->signals, sizeof m->signals,
		  "product dolce %d vita %d; syscon mcemu %d wwan %d; memory card inserted %d; cdialog 0x%x; imc0 %s; console id product fields %s%02x %02x %02x %02x",
		  dolce, vita, mcemu, wwan, card, (unsigned) cdialog, imc_mounted ? "mounted" : "not mounted", cidr < 0 ? "(not read) " : "",
		  (unsigned char) cid [4], (unsigned char) cid [5], (unsigned char) cid [6], (unsigned char) cid [7]);
	const char *why;
	if (dolce == 1 || cdialog == 0x20000) {
		snprintf (m->model, sizeof m->model, "PS TV (VTE-1000)");
		why = dolce == 1 ? "the product type is a PS TV (vshSblAimgrIsDolce)" : "sceKernelGetModelForCDialog says PS TV (the product check did not answer 1)";
	} else if (mcemu == 1 || imc_mounted) {
		snprintf (m->model, sizeof m->model, "PCH-2000");
		why = mcemu == 1 ? (imc_mounted ? "the syscon reports the built-in memory-card flash (vshSysconIsMCEmuCapable) and imc0 is mounted"
						: "the syscon reports the built-in memory-card flash (vshSysconIsMCEmuCapable); imc0 is not mounted because a memory card is in")
			      : (mcemu < 0 ? "imc0 is mounted (kit #7's rule: the syscon check did not answer)"
					   : "imc0 is mounted, but the syscon did NOT report the memory-card flash: the rule needs a look (only a slim / PS TV has imc0)");
	} else if (wwan == 1) {
		snprintf (m->model, sizeof m->model, "PCH-1100 (3G)");
		why = "no built-in memory-card flash, a 3G radio (vshSysconHasWWAN)";
	} else {
		snprintf (m->model, sizeof m->model, "PCH-1000");
		why = mcemu < 0 ? "the syscon check did not answer and imc0 is not mounted (kit #7's rule)" : "no built-in memory-card flash, no 3G radio";
	}
	snprintf (m->how, sizeof m->how, "%s (kit #8 hardware check; SHEPHERD_K8_MODEL=0 = kit #7's imc0 rule)", why);
	SceKernelSystemSwVersion sw;
	memset (&sw, 0, sizeof sw);
	sw.size = sizeof sw;
	m->fw_reported_rc = sceKernelGetSystemSwVersion (&sw);
	m->fw_reported_raw = sw.version;
	SceKernelFwInfo real;
	memset (&real, 0, sizeof real);
	real.size = sizeof real;
	m->fw_rc = _vshSblGetSystemSwVersion (&real);
	m->fw_raw = real.version;
	snprintf (m->fw_reported, sizeof m->fw_reported, "unknown");
	if (m->fw_reported_rc >= 0)
		k8_fw_text (m->fw_reported_raw, m->fw_reported, sizeof m->fw_reported);
	if (m->fw_rc >= 0 && m->fw_raw)
		k8_fw_text (m->fw_raw, m->fw, sizeof m->fw);
	else
		snprintf (m->fw, sizeof m->fw, "%s", m->fw_reported);   /* the real one did not answer: the reported one (the line says so) */
	m->spoofed = m->fw_rc >= 0 && m->fw_raw && m->fw_reported_rc >= 0 && ((m->fw_raw ^ m->fw_reported_raw) & 0xffff0000u) != 0;
}
#endif   /* SHEPHERD_GL */

/* ---------------------------------------------------------------- the clean boot screen + the launcher handshake
 *
 *  CLEAN BOOT SCREEN (roadmap "Splash as soon as possible + a clean boot screen", owner 2026-10-19: "release builds never show the debug
 *   text screen or vitaGL's splash -- the debug screen stays in diagnostic builds only, and a failed start shows a short plain message with
 *   where the log is instead of a black screen"). In the release apps -- the play apps 00097 / 00098 / 00095 (SHP_PLAY_APP) -- by default:
 *   - no debug text screen at start (kit #7: the host's log lines on the screen until vitaGL took over, ~1-2 s);
 *   - no vitaGL splash (kit #7: vitaGL's animated logo from its init until the engine's first frame, ~60 s on the device): vitaGL's
 *     invoke_splashscreen is wrapped at link time (-Wl,--wrap=invoke_splashscreen, vita/gltest/shepgl/shepgl_vita.c) and left out when the
 *     host asks (shepgl_config.no_vgl_splash); right after the init the host presents one black frame (shepgl_boot_frame: a clear of the
 *     display to vitaGL's default clear colour + one vglSwapBuffers, outside shepgl_swap's count -- so the END line's "frames shown", the GL
 *     state the engine starts from and vlx's GL record are kit #7's) and the screen stays black until the engine's first frame;
 *   - a start that FAILS before vitaGL has the display (a missing file, no memory for the arena / audio pool, no shader compiler, no Mono
 *     thread) shows a short plain message -- the app's title, "could not start", the FAIL line, where the log is, "press START" -- on the
 *     debug screen's frame buffer (2x font) instead of 15 s of black; a failure after vitaGL has the display keeps kit #7's red screen.
 *   The measurement app 00099 and the headless 00011 are diagnostic builds: their debug text stays (00011 has no other picture).
 *   SHEPHERD_K8_BOOTSCREEN=0 (or SHEPHERD_K8_BOOT=0): kit #7 -- the debug text, vitaGL's splash, no message screen; =1 turns it on in a
 *   diagnostic GL app too (tests). Read from the env file before the screen would start (k8_env_peek: the switch must be known before the
 *   first log line; apply_env_file runs later and logs the env as before).
 *
 *  LAUNCHER HANDSHAKE (a launcher build: this host runs inside app0:module/shepherd.suprx; k8_launch.h, launcher.c, k8_module.c):
 *   g_k8_launch is the launcher's block (NULL in a kit #7-style eboot); the launcher's own lines go into this log as "K8 L ..." lines; the
 *   host calls release () right before its partition (the launcher gives its CDRAM, threads and audio port back once the splash has ended)
 *   and logs one K8 BOOT line: when the launcher started, the splash's first frame (the new TIME TO SPLASH, from the process start), the
 *   module load behind the logo, the splash's end, when host main began and how long it waited for the splash. A failure path releases
 *   the launcher first (the message screen needs its CDRAM). */
#include "k8_launch.h"
const K8Launch *g_k8_launch __attribute__ ((weak)) = 0;   /* k8_module.c's (strong) in a launcher build */

static int g_k8_bootscreen;     /* the clean boot screen is on */

/* the value of KEY in the app's env file, without applying anything (before apply_env_file); 0 = not there. The last line with the key
 * wins, as apply_env_file's setenv. Review fix: the value goes into the CALLER's buffer (it was one static buffer, so a second call
 * overwrote the first answer: SHEPHERD_K8_BOOTSCREEN=0 next to SHEPHERD_K8_BOOT=1 read as "1" and the switch back to kit #7 did nothing) */
static const char *k8_env_peek (const char *key, char *val, size_t vsize)
{
	void *data;
	unsigned size;
	if (read_file (DIR_ "/" SHP_ENV_NAME, &data, &size) != 0)
		return NULL;
	const char *res = NULL;
	size_t kl = strlen (key);
	for (char *l = (char *) data; l && *l; ) {
		char *nl = strpbrk (l, "\r\n");
		if (nl)
			*nl = 0;
		if (l [0] != '#' && !strncmp (l, key, kl) && l [kl] == '=') {
			snprintf (val, vsize, "%s", l + kl + 1);
			res = val;
		}
		l = nl ? nl + 1 : NULL;
	}
	free (data);
	return res;
}

static void k8_bootscreen_decide (void)
{
	char bsv [64], allv [64];
	const char *bs = k8_env_peek ("SHEPHERD_K8_BOOTSCREEN", bsv, sizeof bsv), *all = k8_env_peek ("SHEPHERD_K8_BOOT", allv, sizeof allv);
#ifdef SHP_PLAY_APP
	g_k8_bootscreen = !(bs && !strcmp (bs, "0")) && !(all && !strcmp (all, "0"));
#elif defined (SHEPHERD_GL)
	g_k8_bootscreen = bs && !strcmp (bs, "1") && !(all && !strcmp (all, "0"));
#else
	(void) bs; (void) all;
	g_k8_bootscreen = 0;   /* the headless app: the debug screen is its only picture */
#endif
}

/* the engine's environment (after engine_env): a launcher build turns the real video on unless the env file decides (K8Video.cs); one K8
 * line names the track's switches in force */
static void k8_engine_env (void)
{
	const char *all = getenv ("SHEPHERD_K8_BOOT");
	int off = all && !strcmp (all, "0");
#if defined (SHP_K8_MODULE) && defined (SHEPHERD_GL)
	if (!off && !getenv ("SHEPHERD_K8_VIDEO"))
		setenv ("SHEPHERD_K8_VIDEO", "1", 1);   /* the launcher apps: their device run is where the decoder is exercised first */
#endif
	const char *vid = getenv ("SHEPHERD_K8_VIDEO");
	LOG ("K8 boot track%s: model check %s, clean boot screen %s, launcher %s, real video %s (SHEPHERD_K8_VIDEO=%s)", off ? " OFF (SHEPHERD_K8_BOOT=0: kit #7)" : "",
	     g_k8_model ? "on" : "off", g_k8_bootscreen ? "on" : "off", g_k8_launch ? "yes (this host runs in app0:module/shepherd.suprx)" : "no (kit #7's eboot)",
	     !off && vid && !strcmp (vid, "1") ? "on" : "off", vid ? vid : "unset");
	/* device kit #9, track device (vita/notes/kit9-device.md): the track's switches in force, one line */
	{
		const char *e;
		int delay = !((e = getenv ("SHEPHERD_K9_AVC_DELAY")) && !strcmp (e, "0")), reserve = !((e = getenv ("SHEPHERD_K9_VIDEO_RESERVE")) && !strcmp (e, "0"));
		int rtc = !((e = getenv ("SHEPHERD_K9_SUSP_RTC")) && !strcmp (e, "0")), title = !((e = getenv ("SHEPHERD_K9_TITLE_PRESS")) && !strcmp (e, "0"));
		int arm500 = !((e = getenv ("SHEPHERD_K9_ARM500_DEFAULT")) && !strcmp (e, "0"));
		int video = !off && vid && !strcmp (vid, "1");
#ifdef SHP_PLAY_APP
		const int play = 1;
#else
		const int play = 0;
#endif
		LOG ("K9 device track: the decoder's output delay %s, its library before vitaGL's init %s, the P3 suspension watch by the RTC %s, the title press "
		     "as a menu response %s, CPU Speed default %s (switches back: SHEPHERD_K9_AVC_DELAY / _VIDEO_RESERVE / _SUSP_RTC / _TITLE_PRESS / _ARM500_DEFAULT=0)",
		     delay ? "tolerated" : "OFF (kit #8)", !video ? "not needed (no real video)" : reserve ? "on" : "OFF (kit #8)", rtc ? "on" : "OFF (kit #8)",
		     title ? "on" : "OFF (kit #8)", !play ? "444 MHz (not a play app)" : arm500 ? "500 MHz" : "444 MHz (kit #8)");
	}
}

/* kit #8.1: the engine module's late libraries (k8_latelibs.c): bound at first use instead of at the module's load */
#ifdef SHP_K8_MODULE
extern unsigned k8_late_bound_shacccg, k8_late_bound_net, k8_late_unresolved;
extern const char *k8_late_first_unresolved;
static void k8_late_end_line (void)
{
	LOG ("END k8 late libraries (bound at first use, not at the module's load): SceShaccCg %u of 11, SceNet %u of 5 (newlib's fd layer: never "
	     "called), %u unresolved%s%s", k8_late_bound_shacccg, k8_late_bound_net, k8_late_unresolved,
	     k8_late_first_unresolved ? ", first " : "", k8_late_first_unresolved ? k8_late_first_unresolved : "");
}
#else
static void k8_late_end_line (void) {}
#endif

/* the launcher's splash still has the display: the debug screen must wait for release () */
static int k8_splash_holds_display (void) { return g_k8_launch && g_k8_launch->splash_state == 1 && !g_k8_launch->t_released; }

/* the launcher's lines not yet in this log (review fix: release () can add one -- "left running" -- after the first copy; it is copied too) */
static size_t g_k8_log_seen;
static void k8_launcher_lines (void)
{
	if (!g_k8_launch)
		return;
	char buf [sizeof g_k8_launch->log];
	const char *z = (const char *) memchr (g_k8_launch->log, 0, sizeof g_k8_launch->log);
	size_t n = z ? (size_t) (z - g_k8_launch->log) : sizeof g_k8_launch->log - 1;
	if (n <= g_k8_log_seen)
		return;
	snprintf (buf, sizeof buf, "%.*s", (int) (n - g_k8_log_seen), g_k8_launch->log + g_k8_log_seen);
	g_k8_log_seen = n;
	for (char *l = buf; l && *l; ) {
		char *nl = strchr (l, '\n');
		if (nl)
			*nl = 0;
		if (*l)
			LOG ("K8 L %s", l);
		l = nl ? nl + 1 : NULL;
	}
}

static int g_k8_released, g_k8_wait_ms;
/* the K10 line's verdict on the second copy (buf: at least 200 B) -- kit #10 integration review: "checked" only when time stamps came back.
 * Here, not in k8_avc.h: the launcher's lines are printed in every build (00011 has no decoder header) */
static void k10avc_verdict (char *buf, unsigned bufn, int tag, unsigned second, unsigned pts_seen, unsigned pts_bad, unsigned flush_ok, unsigned flushed)
{
	if (second)
		snprintf (buf, bufn, "; A PICTURE OF THE SECOND COPY%s came out (the decoder did not hand back its held pictures at the refed IDR): not shown",
				 pts_bad ? " (or one out of order: its time stamp named another picture)" : "");
	else if (!tag)
		snprintf (buf, bufn, "; the second copy NOT checked (SHEPHERD_K10_AVC_TAG=0)");
	else if (!pts_seen)
		snprintf (buf, bufn, "; the second copy NOT checked (the decoder hands back no time stamps: no picture carried one)");
	else if (flush_ok >= flushed)
		snprintf (buf, bufn, "; no picture of the second copy came out (each of the %u held picture(s) carried its own access unit's time stamp, in order; %u picture(s) checked in all%s)",
				 flushed, pts_seen, pts_bad ? ", some out of order while the clip played" : "");
	else
		snprintf (buf, bufn, "; %u of the %u held picture(s) carried their time stamp (the rest came back WITHOUT one: not checked)", flush_ok, flushed);
}
static void k8_release_launcher (const char *why)
{
	if (!g_k8_launch || g_k8_released || !g_k8_launch->release)
		return;
	g_k8_released = 1;
	SceUInt64 t = sceKernelGetProcessTimeWide ();
	g_k8_wait_ms = g_k8_launch->release ();
	const K8Launch *l = g_k8_launch;
	LOG ("K8 launcher released (%s) after %.3f s of waiting for the splash: %s; %u of %u frames shown (%u late), decode max %.1f ms / mean %.1f ms; sound %s%s%s; %u KiB CDRAM given back",
	     why, (double) (sceKernelGetProcessTimeWide () - t) / 1e6,
	     l->splash_state == 2 ? "the splash ended" : l->splash_state == 0 ? "no splash" : l->splash_state == 1 ? "the splash DID NOT END in time (left running)" : "the splash stopped early",
	     l->frames_shown, l->frames_total, l->frames_late, l->decode_us_max / 1000.0, l->frames_shown ? l->decode_us_total / 1000.0 / l->frames_shown : 0.0,
	     l->audio_state > 0 ? "played" : "off", l->audio_why [0] ? ": " : "", l->audio_why, l->cdram_kib);
	if (l->splash_state < 0 && l->splash_why [0])
		LOG ("K8 the splash: %s", l->splash_why);
	if (l->k9_delay && l->fed)   /* kit #9 (device track): what the decoder's output delay was on this console */
		LOG ("K9 the splash's decoder: output delay %u access unit(s) before its first picture; %u access units given, %u picture(s) returned by the end-of-stream call (sceAvcdecDecodeStop) -- SHEPHERD_K9_AVC_DELAY=0 = kit #8's loop",
		     l->out_delay, l->fed, l->drained);
	if (l->k9_delay && l->fed && l->k10_flush != 0) {   /* kit #10 (boot track): the drain -- k8_avc.h "THE DRAIN" (0 = K10_FLUSH_KIT9) */
		char rc [48] = "";
		if (l->flush_rc)
			snprintf (rc, sizeof rc, "; a flush call refused 0x%08x", (unsigned) l->flush_rc);
		char id [260] = "";   /* review fix (finding 5): the second copy's identity; integration review: "checked" only when time stamps came back */
		if (l->k10_flush == 1 && l->refed)
			k10avc_verdict (id, sizeof id, l->k10_tag, l->second, l->pts_seen, l->pts_bad, l->flush_ok, l->flushed);
		LOG ("K10 the splash's drain (%s%s): %u flush access unit(s) fed (the clip's own again, from its IDR), %u held picture(s) brought out%s%s; %u of %u pictures shown -- SHEPHERD_K10_AVC_FLUSH=0 = kit #9's end-of-stream call",
		     l->k10_flush == 2 ? "stop1" : "refeed", l->k10_fallback == 2 ? ", the refeed's guard is set (runtime/k10-avc-refeed.off) -> stop1" : l->k10_fallback ? ", refused -> stop1" : "",
		     l->refed, l->flushed, rc, id, l->frames_shown, l->frames_total);
	}
	k8_launcher_lines ();   /* (review fix: release ()'s own lines) */
	/* review fix: the free memory with only the module + the launcher loaded -- "MEM start" is taken while the splash still holds its
	 * threads, the decoder library and the audio port, so this line (and H2's) is the one that settles the 314 / 315 MiB question */
	log_mem ("k8-released");
}

static void k8_boot_line (void)
{
	if (!g_k8_launch)
		return;
	const K8Launch *l = g_k8_launch;
	char first [40], end [40];
	if (l->t_first_frame)
		snprintf (first, sizeof first, "%.3f s", (double) l->t_first_frame / 1e6);
	else
		snprintf (first, sizeof first, "none (no splash)");
	if (l->t_splash_end)
		snprintf (end, sizeof end, "%.3f s", (double) l->t_splash_end / 1e6);
	else
		snprintf (end, sizeof end, "none");
	LOG ("K8 BOOT (launcher build, process time) launcher main %.3f s | TIME TO SPLASH %s | module load %.3f s, from %.3f to %.3f s (behind the logo) | splash end %s | host main %.3f s | host waited %.3f s for the splash | released at %.3f s | (the kernel's read of the ~0.1 MiB eboot before the launcher's main is not measurable from the app)",
	     (double) l->t_start / 1e6, first, (double) (l->t_load_end - l->t_load_begin) / 1e6, (double) l->t_load_begin / 1e6, (double) l->t_load_end / 1e6, end,
	     (double) g_t0 / 1e6, g_k8_wait_ms / 1000.0, (double) l->t_released / 1e6);
}

/* a start that failed: the plain message (see above), or nothing new when the boot screen is off / vitaGL holds the display.
 * start_closes: the caller polls START (gl_fail_wait); 0 = it closes by itself after 15 s (fail_and_wait).
 * Review fixes: the message shows the FIRST line of the failure (the one that names it: "FAIL G0 libshacccg.suprx is missing: ...", "FAIL H0
 * missing <file>"; logf_ keeps it, g_k8_last_fail, and counts the lines after it with the same tag) word-wrapped over up to 4 lines (it was the
 * LAST line, cut at 104 characters mid-word), and "Press START" only where START is read. Every line of the message is logged as well. */
#define K8_FAIL_COLS 52
static void k8_fail_screen (int start_closes)
{
	k8_release_launcher ("a failed start");
	if (!g_k8_bootscreen || g_screen)
		return;
#ifdef SHEPHERD_GL
	if (g_gl_state != 0)
		return;   /* vitaGL is up and has the display (its CDRAM): kit #7's red screen paths */
#endif
	if (psvDebugScreenInit () < 0)
		return;
	psvDebugScreenSetFont (psvDebugScreenScaleFont2x (psvDebugScreenGetFont ()));
#ifdef SHP_APP_TITLE
	const char *title = SHP_APP_TITLE;
#else
	const char *title = "Shepherd";
#endif
	char lines [8][K8_FAIL_COLS + 8];
	int nl = 0;
	snprintf (lines [nl++], sizeof lines [0], "%.40s could not start.", title);
	const char *s = g_k8_last_fail [0] ? g_k8_last_fail : "(see the log)";
	for (int w = 0; *s && w < 4; w++) {   /* word wrap: break at the last space that fits (a longer word is cut) */
		size_t n = strlen (s);
		if (n > K8_FAIL_COLS) {
			n = K8_FAIL_COLS;
			while (n > K8_FAIL_COLS / 2 && s [n] != ' ')
				n--;
			if (s [n] != ' ')
				n = K8_FAIL_COLS;
		}
		snprintf (lines [nl++], sizeof lines [0], "%.*s%s", (int) n, s, w == 3 && s [n] && s [n + 1] ? " ..." : "");
		s += n;
		while (*s == ' ')
			s++;
	}
	if (g_k8_fail_more)
		snprintf (lines [nl++], sizeof lines [0], "(%u more FAIL line%s in the log)", g_k8_fail_more, g_k8_fail_more == 1 ? "" : "s");
	char b [768];
	int o = snprintf (b, sizeof b, "\e[2J\e[H\n\n  %s\n\n", lines [0]);
	for (int i = 1; i < nl && o > 0 && o < (int) sizeof b; i++)
		o += snprintf (b + o, sizeof b - (size_t) o, "  %s\n", lines [i]);
	if (o > 0 && o < (int) sizeof b)
		snprintf (b + o, sizeof b - (size_t) o, "\n  The log: " DIR_ "/\n           " SHP_LOG_NAME "\n\n  %s\n",
			  start_closes ? "Press START to close (or wait 60 s)." : "It closes by itself in 15 s.");
	psvDebugScreenPuts (b);
	LOG ("K8 boot screen: the start failed -- the plain message is on the screen:");
	for (int i = 0; i < nl; i++)
		LOG ("K8 boot screen: | %s", lines [i]);
	LOG ("K8 boot screen: | The log: " DIR_ "/" SHP_LOG_NAME " | %s", start_closes ? "Press START to close (or wait 60 s)." : "It closes by itself in 15 s.");
}

#ifdef SHEPHERD_GL
/* ---------------------------------------------------------------- the engine's real video (roadmap "Real video playback")
 * The game's VideoPlayer clips through the Vita's hardware H.264 decoder into the VideoPlayer's picture: the engine (Runtime/K8Video.cs) opens
 * the clip's owner-local .shv (data/video/<clip>.shv, vita/tools/splash_pack.py) when a VideoPlayer starts playing, asks for the clip frame
 * of each engine frame (the VideoPlayer's own clock, kit #6's timing), and draws it where Unity draws a CameraNearPlane video (Fit Inside,
 * the camera's alpha). Here: the clip read into vitaGL's VRAM (shepgl_vram_alloc: physically contiguous, as the decoder's access units must
 * be), the decoder's work area there too, and each picture decoded straight into the texture memory the engine hands over (vitaGL's own
 * texture: shepgl_tex_data). Frames are decoded in order (H.264: each picture needs the previous ones): a frame further on decodes the ones
 * between into the same texture (Unity's skipOnDrop: the picture of the current time is shown); an earlier frame (a restart) re-creates the
 * decoder and starts again at frame 0. One clip at a time (the game plays one). The calls come from the engine's main thread.
 * Crash guard: runtime/k8-video.marker is written before the decoder opens and removed after its first good picture; a start that finds it
 * (the previous start stopped inside the decoder) plays no video (one WARN line) and removes it -- so one bad start costs one black clip,
 * never a loop of failed starts. SHEPHERD_K8_VIDEO decides on the engine side (K8Video.cs); with it off nothing here runs. */
#include "k8_shv.h"
#include "k8_avc.h"
#define K8_VIDEO_MARKER DIR_ "/runtime/k8-video.marker"
typedef struct {
	int open, ready;
	K8Avc avc;
	unsigned char *clip;
	unsigned clip_len;
	void *work;
	int last;
	unsigned decodes, errors, us_max;
	unsigned long long us_total;
	char name [72];
	int shared;           /* kit #9: this clip's decoder uses the library reserved before vitaGL's init (k9_video_reserve) */
	unsigned delay;       /* kit #9: access units the decoder took before its first picture (logged once) */
	/* kit #9: the pictures the decoder handed back after the last access unit beyond the one asked for (the end-of-stream call may return
	 * them all at once), kept in VRAM in display order and copied into the frame's texture when the engine asks for them.
	 * Review fix (finding 7): ONE VRAM block for all of them (shepgl_vram_alloc has 8 slots -- the clip, the decoder's work area and the
	 * preload's store already hold up to 3 -- so a block per picture offered the end-of-stream call at most 5-6 buffers) */
	void *stash_blk;
	unsigned stash_cap, stash_r, stash_n, stash_bytes;
	/* kit #9 review, round 2 (finding 1): where the latest picture is (the engine's texture it went to), and the end-of-stream call's state
	 * for this clip -- off (no_drain: k8_avc.h's shared guard, SHEPHERD_K9_AVC_DRAIN=0, or a call that did not return in time): the pictures
	 * after the last one that came out are that picture again (held), counted for the close line */
	void *last_pic;
	int drain_checked, no_drain;
	const char *no_drain_why;
	unsigned held;
	int refeed_off;       /* kit #10: 1 = the decoder refused a flush access unit -> stop1 (k8_avc.h "THE DRAIN"); review fixes: 2 = the refeed's guard
	                         (runtime/k10-avc-refeed.off) -> stop1; 3 = a picture of the second copy came out -> the clip holds its last picture;
	                         integration review: 4 = a flush access unit did not come back in time (the .off written) -> the clip holds its last picture */
	int refeed_checked, refeed_marked;   /* kit #10 review fix (finding 7): the refeed's guard checked for this clip / its marker on the card */
	int refeed_nostash_logged;           /* kit #10 integration review: the bounded refeed's one stash picture could not be had (logged once) */
	int second_at;        /* kit #10 review fix (finding 5): the clip picture at which a second-copy picture came out (-1: none) */
} K8Vid;
static K8Vid g_k8v = { .avc = { .mem = -1 } };
static int g_k8v_guard_checked, g_k8v_blocked;
static void k10_bootscreen_stop (const char *why, int frame);   /* kit #10 (k10_boot.inc.c): the boot screen's decoder and VRAM before a clip opens */

/* ---------------------------------------------------------------- device kit #9, track device (vita/notes/kit9-device.md)
 *  OUTPUT DELAY (k8_avc.h): shepherd_vita_video_decode keeps feeding access units while the decoder holds pictures and asks for the held
 *   ones after the last access unit; the picture written into the engine's texture is the decoder's OUTPUT number `frame` (pictures come
 *   out in display order), so the texture holds exactly the picture kit #8 meant. SHEPHERD_K9_AVC_DELAY=0: kit #8 (no picture = an error).
 *  DECODER MEMORY BEFORE vitaGL: kit #8's device run -- sceVideodecInitLibrary 0x80024302 NO_FREE_PHYSICAL_PAGE: vitaGL's init had taken
 *   every free PHYCONT (and CDRAM) page, and the library needs its own PHYCONT (6,144 KiB for 960 x 544, MEASURED on the owner's PCH-2000 by
 *   the kit #8.1 launcher's MEM lines). When the engine's real video is on (SHEPHERD_K8_VIDEO=1: the launcher apps' default), the host
 *   initialises the decoder library right before vitaGL's init (gl_start: k9_video_reserve), so vitaGL's PHYCONT pool is that much smaller
 *   and the library's memory exists when the clip opens; the clip's decoder uses it (k8avc_prepare_ex shared) and the library is ended
 *   after the clip (shepherd_vita_video_close: the memory back to the system -- vitaGL's pools are sized once, at its init, so it does not
 *   get it back; the K9 lines say how much it lost). A later clip (SHEPHERD_K8_VIDEO_CLIPS=all) initialises the library itself, as kit #8.
 *   SHEPHERD_K9_VIDEO_RESERVE=0: kit #8 (nothing before vitaGL; the device then refuses the library: black clip). */
static int g_k9_delay = 1, g_k9_reserve_on = 1;
static int g_k10_flush = K10_FLUSH_REFEED;   /* device kit #10, track boot: the drain (k8_avc.h "THE DRAIN"; SHEPHERD_K10_AVC_FLUSH=0 = kit #9's call) */
static int g_k10_tag = 1;                    /* kit #10 review fix (finding 5): the flush access units' own time stamp (SHEPHERD_K10_AVC_TAG=0: none) */
static K8AvcLib g_k9_lib;
static int g_k9_reserved;                 /* 1 = g_k9_lib is up and no clip has used and ended it yet */
static unsigned g_k9_lib_phycont_k, g_k9_lib_user_k, g_k9_lib_cdram_k;   /* what the early init took (free memory before - after) */
static SceUID g_k9_pad = -1;              /* review fix (finding 4): PHYCONT held for the clip's sceAvcdecCreateDecoder (k9_video_reserve) */
static unsigned g_k9_pad_k;
static int k9_off (const char *k) { const char *e = getenv (k); return e && !strcmp (e, "0"); }
static void k9_boot_config (void)
{
	g_k9_delay = !k9_off ("SHEPHERD_K9_AVC_DELAY");
	g_k9_reserve_on = !k9_off ("SHEPHERD_K9_VIDEO_RESERVE");
	g_k10_flush = k10avc_flush_mode (getenv ("SHEPHERD_K10_AVC_FLUSH"));
	g_k10_tag = !k9_off ("SHEPHERD_K10_AVC_TAG");
}
static void k9_free_mem (unsigned *user_k, unsigned *cdram_k, unsigned *phycont_k)
{
	SceKernelFreeMemorySizeInfo fi;
	memset (&fi, 0, sizeof fi);
	fi.size = sizeof fi;
	sceKernelGetFreeMemorySize (&fi);
	*user_k = (unsigned) fi.size_user / 1024; *cdram_k = (unsigned) fi.size_cdram / 1024; *phycont_k = (unsigned) fi.size_phycont / 1024;
}
/* kit #9 review fixes (vita/notes/kit9-device.md "Review fixes"):
 *  finding 2 -- the reserve is taken only when a clip will really open on it: the engine has the real-video code (build.sh SHP_K8_ENGINE:
 *   the launcher apps), K8Video.cs would turn it on (SHEPHERD_VITA_HOST=1, SHEPHERD_GL_HOST=shepgl), kit #8's crash-guard marker is NOT
 *   there (after a decoder crash the next start is "kit #7, no decoder" again: nothing of the decoder runs, not even the library), the
 *   clip list plays the Devolver logo (the only clip a kit route reaches: K8Video.cs CheckedClips) and its .shv is on the card. The
 *   reserve's own library init runs inside the crash guard (the marker written before, removed after it returned). A clip that does not
 *   open after all gives the library back (shepherd_vita_video_open's failure paths).
 *  finding 4 -- the 6,144 KiB the device measured (kit #8.1: MEM start vs MEM k8-released) was the decoder stack with a decoder CREATED:
 *   if sceVideodecInitLibrary alone takes less, the rest (rounded up to 1 MiB) is held in a PHYCONT block until right before the clip's
 *   sceAvcdecCreateDecoder, so that call finds what it had on the device. SHEPHERD_K9_RESERVE_KIB=<KiB> (default 6144; 0 = the library only).
 *  finding 1 -- vitaGL's two per-frame circular pools took 21,846 KiB of its PHYCONT pool at frame 0 on every kit #8 device log; a pool
 *   smaller than that puts one of them (10,923 KiB) into VRAM for the whole run. When that happens the preload's VRAM budget
 *   (Runtime/K8Preload.cs, SHEPHERD_K8_PRELOAD_MB, default 16) is cut by the same 11 MiB unless the env file names one -- the spill and the
 *   preload together keep to kit #8's 16 MiB, so the textures keep kit #8's room (the device's VRAM free reading is not usable for that:
 *   from CP500 on every kit #8 app reads "vram 1733632/98304", more than the total). */
#define K9_CLIP_NAME "Devolver_Animated_Logo_v001_4k_Audio-_HQ_HBR_MP4"   /* = K8Video.cs CheckedClips */
#define K9_CLIP_PATH DIR_ "/data/video/" K9_CLIP_NAME ".shv"
#define K9_VGL_CIRC_KIB 10923u   /* one vitaGL per-frame circular pool (DEVICE: phycont 21.3 of 26.0 MiB used at frame 0 = two of them) */
static int k9_names (const char *list, const char *name)
{
	size_t nl = strlen (name);
	for (const char *s = list; *s; ) {
		while (*s == ' ' || *s == ',') s++;
		const char *e = s;
		while (*e && *e != ',') e++;
		const char *t = e;
		while (t > s && t [-1] == ' ') t--;
		if ((size_t) (t - s) == nl && !strncmp (s, name, nl)) return 1;
		s = e;
	}
	return 0;
}
static const char *k9_reserve_skip (void)
{
#ifndef SHP_K8_ENGINE
	return "this app's engine has no real-video code (SHEPHERD_NO_K8: kit #7's engine code -- no clip can open)";
#else
	const char *e;
	if (!((e = getenv ("SHEPHERD_VITA_HOST")) && !strcmp (e, "1")) || !((e = getenv ("SHEPHERD_GL_HOST")) && !strcmp (e, "shepgl")))
		return "the engine's real video is off in this start (SHEPHERD_VITA_HOST / SHEPHERD_GL_HOST: Runtime/K8Video.cs turns it off)";
	if (file_size (K8_VIDEO_MARKER, NULL) == 0)
		return "the previous start stopped inside the hardware decoder (" K8_VIDEO_MARKER "): kit #8's crash guard -- this start runs no decoder at all (black clips, kit #7); the next start tries again";
	if ((e = getenv ("SHEPHERD_K8_VIDEO_CLIPS")) && *e && strcmp (e, "all") && !k9_names (e, K9_CLIP_NAME))
		return "SHEPHERD_K8_VIDEO_CLIPS does not name the Devolver logo (a clip it names initialises its own library at the clip: kit #8's path)";
	if (file_size (K9_CLIP_PATH, NULL) != 0)
		return "the Devolver logo's clip is not on the card (" K9_CLIP_PATH ": drawn black, kit #7)";
	return NULL;
#endif
}
static void k9_pad_free (const char *why)
{
	if (g_k9_pad < 0)
		return;
	sceKernelFreeMemBlock (g_k9_pad);
	g_k9_pad = -1;
	LOG ("K9 video reserve: the %u KiB PHYCONT block held for the decoder given back (%s)", g_k9_pad_k, why);
	g_k9_pad_k = 0;
}
/* gl_start, right before vitaGL's init (its MEM before-gl line comes after this one): the decoder library, early */
static void k9_video_reserve (void)
{
	const char *vid = getenv ("SHEPHERD_K8_VIDEO"), *all = getenv ("SHEPHERD_K8_BOOT");
	if (!(vid && !strcmp (vid, "1")) || (all && !strcmp (all, "0")))
		return;   /* no real video in this start: nothing to reserve (kit #8's memory exactly) */
	k9_boot_config ();
	if (!g_k9_reserve_on) {
		LOG ("K9 video reserve OFF (SHEPHERD_K9_VIDEO_RESERVE=0: kit #8 -- vitaGL takes every free PHYCONT page and the clip's decoder library will likely be refused, 0x80024302)");
		return;
	}
	const char *skip = k9_reserve_skip ();   /* review fix (finding 2) */
	if (skip) {
		LOG ("K9 video reserve: not taken -- %s; vitaGL keeps kit #8's PHYCONT pool (no VRAM cost in this start)", skip);
		return;
	}
	unsigned u0, c0, p0, u1, c1, p1;
	k9_free_mem (&u0, &c0, &p0);
	SceUInt64 t0 = sceKernelGetProcessTimeWide ();
	SceUID mk = sceIoOpen (K8_VIDEO_MARKER, SCE_O_WRONLY | SCE_O_CREAT | SCE_O_TRUNC, 0777);   /* review fix (finding 2): the crash guard */
	if (mk >= 0) { sceIoWrite (mk, "k9-reserve", 10); sceIoClose (mk); }
	int r = k8avc_lib_init (&g_k9_lib, 960, 544, 3);
	sceIoRemove (K8_VIDEO_MARKER);
	k9_free_mem (&u1, &c1, &p1);
	g_k9_lib_user_k = u0 > u1 ? u0 - u1 : 0; g_k9_lib_cdram_k = c0 > c1 ? c0 - c1 : 0; g_k9_lib_phycont_k = p0 > p1 ? p0 - p1 : 0;
	if (r < 0) {
		LOG ("WARN K9 video reserve: %s (0x%08x) before vitaGL's init: the clip's decoder will try again at the clip (kit #8's path)", g_k9_lib.why, (unsigned) r);
		return;
	}
	g_k9_reserved = 1;
	LOG ("K9 video reserve: the hardware decoder's library is up BEFORE vitaGL's init (SCE_SYSMODULE_AVCDEC + sceVideodecInitLibrary 960x544, 3 refs, %.1f ms): "
	     "it took %u KiB of PHYCONT, %u KiB of CDRAM, %u KiB of user memory -- vitaGL's PHYCONT pool is %u KiB smaller than kit #8's (free before / after: "
	     "phycont %u -> %u, cdram %u -> %u, user %u -> %u KiB); given back after the clip (SHEPHERD_K9_VIDEO_RESERVE=0: kit #8)",
	     (double) (sceKernelGetProcessTimeWide () - t0) / 1000.0, g_k9_lib_phycont_k, g_k9_lib_cdram_k, g_k9_lib_user_k, g_k9_lib_phycont_k, p0, p1, c0, c1, u0, u1);
	/* review fix (finding 4): the rest of the device's 6,144 KiB, held for the clip's sceAvcdecCreateDecoder */
	const char *e = getenv ("SHEPHERD_K9_RESERVE_KIB");
	unsigned total_k = e && *e ? (unsigned) strtoul (e, NULL, 10) : 6144u;
	if (total_k > g_k9_lib_phycont_k) {
		unsigned pad_k = (total_k - g_k9_lib_phycont_k + 1023u) & ~1023u;
		g_k9_pad = sceKernelAllocMemBlock ("shepherd-k9-decoder-pad", SCE_KERNEL_MEMBLOCK_TYPE_USER_MAIN_PHYCONT_NC_RW, pad_k * 1024u, NULL);
		if (g_k9_pad >= 0) {
			g_k9_pad_k = pad_k;
			k9_free_mem (&u1, &c1, &p1);
		}
		LOG ("K9 video reserve: the library took %u of the %u KiB the device's decoder stack took (kit #8.1, with its decoder created): %u KiB of PHYCONT %s until the clip's sceAvcdecCreateDecoder "
		     "(free phycont now %u KiB; SHEPHERD_K9_RESERVE_KIB=0: the library only)", g_k9_lib_phycont_k, total_k, pad_k,
		     g_k9_pad >= 0 ? "held" : "NOT held (refused)", p1);
	}
	/* review fix (finding 1): vitaGL's pool vs its two per-frame circular pools -- the spill's VRAM comes out of the preload's budget */
	if (p1 < 2u * K9_VGL_CIRC_KIB) {
		unsigned spill_k = p1 < K9_VGL_CIRC_KIB ? 2u * K9_VGL_CIRC_KIB : K9_VGL_CIRC_KIB, spill_m = (spill_k + 1023u) / 1024u;
		const char *pm = getenv ("SHEPHERD_K8_PRELOAD_MB");
		char b [8];
		if (!(pm && *pm)) {
			snprintf (b, sizeof b, "%u", spill_m + 2u <= 16u ? 16u - spill_m : 2u);
			setenv ("SHEPHERD_K8_PRELOAD_MB", b, 1);
		}
		LOG ("K9 video reserve: vitaGL's PHYCONT pool will be %u KiB, less than the %u KiB its two per-frame circular pools took on every kit #8 device log: %u KiB of them go to VRAM "
		     "for the whole run -- the preload's VRAM budget %s (SHEPHERD_K8_PRELOAD_MB; kit #8: 16 MiB for the preload, now the spill + the preload together)",
		     p1, 2u * K9_VGL_CIRC_KIB, spill_k, pm && *pm ? "stays as the env file says" : "is cut by the same: SHEPHERD_K8_PRELOAD_MB=");
		if (!(pm && *pm))
			LOG ("K9 video reserve: SHEPHERD_K8_PRELOAD_MB=%s (16 - %u MiB; the env file's own SHEPHERD_K8_PRELOAD_MB wins)", b, spill_m);
	}
}
/* the clip that used the early library has ended (or the start ends): the library and its memory go back */
static void k9_video_unreserve (const char *why)
{
	k9_pad_free (why);
	if (!g_k9_reserved)
		return;
	g_k9_reserved = 0;
	k8avc_lib_term (&g_k9_lib);
	unsigned u, c, p;
	k9_free_mem (&u, &c, &p);
	LOG ("K9 video reserve: the decoder library ended (%s): free phycont %u KiB, cdram %u KiB, user %u KiB now (vitaGL's pools stay as sized at its init)", why, p, c, u);
}

int shepherd_vita_video_api (void);
int shepherd_vita_video_api (void) { return 1; }

static void k8v_stash_free (void)
{
	if (g_k8v.stash_blk) shepgl_vram_free (g_k8v.stash_blk);
	g_k8v.stash_blk = NULL;
	g_k8v.stash_cap = g_k8v.stash_r = g_k8v.stash_n = 0;
}
static void *k8v_stash (unsigned k) { return (unsigned char *) g_k8v.stash_blk + (size_t) k * g_k8v.stash_bytes; }
/* kit #9 review, round 2 (finding 1; vita/notes/kit9-device.md "Review fixes"): the end-of-stream call (sceAvcdecDecodeStop, never run on a
 * Vita) through k8_avc.h's ONE guard -- shared with the launcher's splash: a marker around every call; a marker found later (a call that
 * never returned, a crash or a hang, this start or an earlier one) becomes runtime/k9-avc-drain.off, and while that file is on the card
 * neither side calls it again -- and BOUNDED here: the call runs on a thread of its own, into the stash only (VRAM this code owns), and the
 * engine's main thread waits at most SHEPHERD_K9_AVC_DRAIN_MS (default 2000 ms). A call that does not return in time: the .off file is
 * written, the thread, the decoder, its library, work area, clip and stash are LEFT to it for the rest of the process (never freed: the
 * call may still write into them), no more real video in this start, and the clip shows its last decoded picture to the end. (Round 1 had
 * two one-shot guards that did not see each other -- the launcher's own marker and kit #8's video marker armed again around this call -- and
 * the call ran on the main thread with no limit: a call that always crashes crashed 3 starts out of 4, one that hangs froze every second
 * start at the end of the logo.) */
#define K9_DRAIN_MS_DEFAULT 2000u
typedef struct {
	K8Avc avc;                        /* a copy of the clip's decoder: a call that returns late writes only here, never into g_k8v */
	void *outs [K8_AVC_MAX_DELAY];    /* stash pictures only */
	unsigned n, pitch;
	int r, got;
	volatile int done;
	int stop1;                        /* kit #10: SHEPHERD_K10_AVC_FLUSH=stop1 (or the refeed's fallback) -- one picture per call */
} K9DrainJob;
static K9DrainJob g_k9_job;
static int g_k9_hung;                     /* a call did not return in time: its decoder and memory belong to it from now on */
static int k9_drain_main (SceSize args, void *argp)
{
	(void) args; (void) argp;
	K9DrainJob *j = &g_k9_job;
	if (j->stop1)   /* kit #10 */
		j->r = k10avc_stop1 (&j->avc, j->outs [0], j->pitch, &j->got);
	else
		j->r = k8avc_drain_n (&j->avc, j->outs, j->n, j->pitch, &j->got);
	k9_avc_drain_end ();   /* the call returned (a late return after the wait gave up: the .off file written then stays) */
	j->done = 1;
	sceKernelExitThread (0);
	return 0;
}
/* 0 = the call returned (*r, *got, g_k8v.avc updated); 1 = it did not return within the wait (the thread is left in it) */
static int k9_drain_bounded (void *const *outs, unsigned n, unsigned pitch, int *got, int *r)
{
	K9DrainJob *j = &g_k9_job;
	memset (j, 0, sizeof *j);
	j->avc = g_k8v.avc;
	j->stop1 = g_k10_flush == K10_FLUSH_STOP1 || g_k8v.refeed_off;   /* kit #10: one picture per call (n is 1 then) */
	for (unsigned k = 0; k < n && k < K8_AVC_MAX_DELAY; k++) j->outs [k] = outs [k];
	j->n = n; j->pitch = pitch;
	const char *e = getenv ("SHEPHERD_K9_AVC_DRAIN_MS");
	unsigned ms = e && *e ? (unsigned) strtoul (e, NULL, 10) : K9_DRAIN_MS_DEFAULT;
	if (!ms) ms = K9_DRAIN_MS_DEFAULT;
	k9_avc_drain_begin ();
	SceUID th = sceKernelCreateThread ("shepherd-k9-avc-drain", k9_drain_main, 0x10000100, 0x4000, 0, 0, NULL);
	if (th < 0 || sceKernelStartThread (th, 0, NULL) < 0) {   /* no thread: the call on this thread (unbounded, the marker around it) */
		if (th >= 0) sceKernelDeleteThread (th);
		LOG ("WARN K9 video '%s': no thread for the end-of-stream call (0x%08x): called on the engine's main thread, without a time limit", g_k8v.name, (unsigned) th);
		if (j->stop1)   /* kit #10 */
			j->r = k10avc_stop1 (&j->avc, j->outs [0], j->pitch, &j->got);
		else
			j->r = k8avc_drain_n (&j->avc, j->outs, j->n, j->pitch, &j->got);
		k9_avc_drain_end ();
	} else {
		SceUInt to = ms * 1000u;
		int w = sceKernelWaitThreadEndCB (th, NULL, &to);
		if (w < 0 && !j->done) {
			k9_avc_drain_off ("the decoder's end-of-stream call (sceAvcdecDecodeStop) did not return within the engine's wait (SHEPHERD_K9_AVC_DRAIN_MS): a hang. "
					  "Kit #9 skips the call (launcher and engine) while this file is here: delete it to try the call again.\n");
			LOG ("WARN K9 video '%s': the decoder's end-of-stream call did not return within %u ms (0x%08x): runtime/k9-avc-drain.off written -- skipped from now on "
			     "(launcher and engine) until that file is deleted; its thread, the decoder and their memory are left to it, no more real video in this start",
			     g_k8v.name, ms, (unsigned) w);
			return 1;
		}
		if (w < 0) { SceUInt to2 = 1000000; sceKernelWaitThreadEndCB (th, NULL, &to2); }   /* (done was set just before its exit) */
		sceKernelDeleteThread (th);
	}
	g_k8v.avc = j->avc;
	*r = j->r; *got = j->got;
	return 0;
}
/* kit #10 integration review (the engine's refeed had no time limit): every flush access unit of the engine's clip goes through a BOUNDED
 * call, as kit #9's end-of-stream call -- a thread of its own ("shepherd-k10-avc-refeed"), a copy of the clip's decoder, the picture into the
 * stash (VRAM this code owns, never the engine's texture: a call that returns late writes only there), and the main thread waits at most
 * SHEPHERD_K10_AVC_REFEED_MS (default 2000 ms; unset = SHEPHERD_K9_AVC_DRAIN_MS's value when that is set). A call that does not come back in
 * time: runtime/k10-avc-refeed.off is written (the next starts go to stop1 behind kit #9's guard, launcher and engine), the thread, the
 * decoder and its memory are left to it (g_k9_hung: never freed or re-created in this start) and the clip holds its last picture -- the boot
 * goes on (before: the main thread waited forever; only the NEXT start turned the marker into the .off). No thread = stop1 instead (never an
 * unbounded call); no VRAM for one stash picture = the call on this thread into the texture, as the first kit #10 build (a WARN line). */
#define K10_REFEED_MS_DEFAULT 2000u
typedef struct {
	K8Avc avc;                        /* a copy of the clip's decoder (as K9DrainJob) */
	const void *au;
	unsigned len, pitch;
	void *out;                        /* the stash picture */
	int tag, r, got;
	volatile int done;
} K10RefeedJob;
static K10RefeedJob g_k10_rjob;
static unsigned g_k10_rjob_calls, g_k10_rjob_us_max;
static int k10_refeed_main (SceSize args, void *argp)
{
	(void) args; (void) argp;
	K10RefeedJob *j = &g_k10_rjob;
	j->r = k10avc_refeed (&j->avc, j->au, j->len, j->out, j->pitch, &j->got, j->tag);
	j->done = 1;
	sceKernelExitThread (0);
	return 0;
}
/* 0 = the call returned (*r, *got, g_k8v.avc updated); 1 = it did not return in time (the .off written, the thread left in it); -1 = no thread */
static int k10_refeed_bounded (const void *au, unsigned len, void *out, unsigned pitch, int *got, int *r)
{
	K10RefeedJob *j = &g_k10_rjob;
	memset (j, 0, sizeof *j);
	j->avc = g_k8v.avc; j->au = au; j->len = len; j->out = out; j->pitch = pitch; j->tag = g_k10_tag;
	const char *e = getenv ("SHEPHERD_K10_AVC_REFEED_MS");
	if (!(e && *e)) e = getenv ("SHEPHERD_K9_AVC_DRAIN_MS");
	unsigned ms = e && *e ? (unsigned) strtoul (e, NULL, 10) : K10_REFEED_MS_DEFAULT;
	if (!ms) ms = K10_REFEED_MS_DEFAULT;
	SceUInt64 t0 = sceKernelGetProcessTimeWide ();
	SceUID th = sceKernelCreateThread ("shepherd-k10-avc-refeed", k10_refeed_main, 0x10000100, 0x4000, 0, 0, NULL);
	if (th < 0 || sceKernelStartThread (th, 0, NULL) < 0) {
		if (th >= 0) sceKernelDeleteThread (th);
		LOG ("WARN K10 video '%s': no thread for the refeed (0x%08x): the end-of-stream call with one picture per call (stop1) instead -- never an unbounded call", g_k8v.name, (unsigned) th);
		return -1;
	}
	SceUInt to = ms * 1000u;
	int w = sceKernelWaitThreadEndCB (th, NULL, &to);
	if (w < 0 && !j->done) {
		int wrote = k9_avc_file_write (K10_AVC_REFEED_OFF, "the decoder did not come back from a flush access unit (kit #10's refeed) within the engine's wait (SHEPHERD_K10_AVC_REFEED_MS): "
					       "a hang. Kit #10 skips the refeed (launcher and engine) while this file is here: delete it to try it again.\n");
		if (wrote == 0) sceIoRemove (K10_AVC_REFEED_MARKER);   /* (the .off is the record now; a card that refused the write keeps the marker: found next start) */
		LOG ("WARN K10 video '%s': flush access unit %u did not come back within %u ms (0x%08x): runtime/k10-avc-refeed.off %s -- the refeed is skipped from now on "
		     "(launcher and engine: stop1) until that file is deleted; its thread, the decoder and their memory are left to it, the clip holds its last picture", g_k8v.name,
		     g_k8v.avc.refed, ms, (unsigned) w, wrote == 0 ? "written" : "NOT written (the marker stays: the next start writes it)");
		return 1;
	}
	if (w < 0) { SceUInt to2 = 1000000; sceKernelWaitThreadEndCB (th, NULL, &to2); }   /* (done was set just before its exit) */
	sceKernelDeleteThread (th);
	unsigned us = (unsigned) (sceKernelGetProcessTimeWide () - t0);
	g_k10_rjob_calls++;
	if (us > g_k10_rjob_us_max) g_k10_rjob_us_max = us;
	g_k8v.avc = j->avc;
	*r = j->r; *got = j->got;
	return 0;
}
/* one stash picture (VRAM) for the bounded refeed; 0 = none */
static int k10_refeed_stash (int pitch, const K8ShvHeader *h)
{
	if (g_k8v.stash_blk) return 1;
	g_k8v.stash_bytes = (unsigned) pitch * h->height * 4u;
	if ((g_k8v.stash_blk = shepgl_vram_alloc (g_k8v.stash_bytes, 256)))
		g_k8v.stash_cap = 1;
	return g_k8v.stash_blk != NULL;
}

/* picture i = the latest picture again (the end-of-stream call off); 0 = done, < 0 = no picture came out yet */
static int k9_hold (int i, void *dst, int pitch)
{
	const K8ShvHeader *h = (const K8ShvHeader *) g_k8v.clip;
	if (!g_k8v.last_pic)
		return -1;
	if (g_k8v.last_pic != dst)
		memcpy (dst, g_k8v.last_pic, (size_t) pitch * h->height * 4u);
	g_k8v.last_pic = dst;
	g_k8v.held++;
	g_k8v.last = i;
	return 0;
}
static void k8v_free (void)
{
	if (g_k9_hung) {   /* round 2 (finding 1): the call that did not return may still write into all of it -- left, never freed */
		if (g_k8v.clip || g_k8v.work || g_k8v.stash_blk)
			LOG ("K9 video '%s': its decoder, library, clip, work area and stash are left to the end-of-stream call that did not return (not freed in this start)", g_k8v.name);
		g_k8v.stash_blk = NULL; g_k8v.work = NULL; g_k8v.clip = NULL; g_k8v.open = g_k8v.ready = 0; g_k8v.shared = 0;
		g_k8v.stash_cap = g_k8v.stash_r = g_k8v.stash_n = 0;
		g_k8v.avc.created = g_k8v.avc.lib = g_k8v.avc.sysmod = 0; g_k8v.avc.mem = -1;
		g_k9_reserved = 0;   /* (the reserved library is the one the call is using: never ended) */
		return;
	}
	k8v_stash_free ();
	k8avc_close (&g_k8v.avc);
	if (g_k8v.work) shepgl_vram_free (g_k8v.work);
	if (g_k8v.clip) shepgl_vram_free (g_k8v.clip);
	g_k8v.work = NULL; g_k8v.clip = NULL; g_k8v.open = g_k8v.ready = 0;
	if (g_k8v.shared) {   /* kit #9: the early library is ended with the clip that used it */
		g_k8v.shared = 0;
		k9_video_unreserve ("its clip closed");
	}
}
void shepherd_vita_video_close (int id);
void shepherd_vita_video_close (int id)
{
	(void) id;
	if (!g_k8v.open)
		return;
	LOG ("K8 video '%s' closed: %u pictures decoded (%u errors), decode mean %.2f ms / max %.2f ms", g_k8v.name, g_k8v.decodes, g_k8v.errors,
	     g_k8v.decodes ? (double) g_k8v.us_total / 1000.0 / g_k8v.decodes : 0.0, g_k8v.us_max / 1000.0);
	if (g_k9_delay)
		LOG ("K9 video '%s': the decoder's output delay %u access unit(s); %u access units given, %u picture(s) from the end-of-stream call%s", g_k8v.name, g_k8v.delay,
		     g_k8v.avc.fed, g_k8v.avc.drained, g_k8v.no_drain ? " (the call OFF)" : "");
	if (g_k8v.refeed_marked) { k10_avc_refeed_end (); g_k8v.refeed_marked = 0; }   /* kit #10 review fix (finding 7): the refeed came back */
	if (g_k9_delay && g_k10_flush != K10_FLUSH_KIT9 && (g_k8v.avc.refed || g_k8v.avc.flushed || g_k8v.avc.flush_rc || g_k8v.refeed_off == 2 || g_k8v.refeed_off >= 4)) {   /* kit #10 */
		char id [260] = "";   /* review fix (finding 5): the second copy's identity; integration review: "checked" only when time stamps came back */
		if (g_k8v.avc.second)
			snprintf (id, sizeof id, "; A PICTURE OF THE SECOND COPY%s came out at picture %d (the decoder did not hand back its held pictures at the refed IDR): not shown -- the clip held its last picture from there, as kit #9",
				  g_k8v.avc.pts_bad ? " (or one out of order)" : "", g_k8v.second_at);
		else if (g_k10_flush == K10_FLUSH_REFEED && g_k8v.avc.refed)
			k10avc_verdict (id, sizeof id, g_k10_tag, 0, g_k8v.avc.pts_seen, g_k8v.avc.pts_bad, g_k8v.avc.flush_ok, g_k8v.avc.flushed);
		char bd [96] = "";   /* integration review: the bounded refeed */
		if (g_k10_rjob_calls)
			snprintf (bd, sizeof bd, "; bounded: %u call(s) on their own thread, the longest %.1f ms", g_k10_rjob_calls, g_k10_rjob_us_max / 1000.0);
		LOG ("K10 video '%s': the drain (%s%s): %u flush access unit(s) fed (the clip's own again, from its IDR), %u held picture(s) brought out%s%s%s; %d of the clip's %u pictures out -- SHEPHERD_K10_AVC_FLUSH=0 = kit #9's end-of-stream call",
		     g_k8v.name, k10avc_flush_name (g_k10_flush), g_k8v.refeed_off == 2 ? ", the refeed's guard is set (runtime/k10-avc-refeed.off) -> stop1" : g_k8v.refeed_off == 1 ? ", refused -> stop1"
		     : g_k8v.refeed_off == 4 ? ", a flush access unit DID NOT COME BACK in time (runtime/k10-avc-refeed.off written) -> the clip held its last picture"
		     : g_k8v.refeed_off == 5 ? ", no thread for the bounded call -> stop1" : "",
		     g_k8v.avc.refed, g_k8v.avc.flushed, g_k8v.avc.flush_rc ? "; a flush call refused" : "", bd, id, g_k8v.last + 1 - (int) g_k8v.held, ((const K8ShvHeader *) g_k8v.clip)->frames);
	}
	if (g_k8v.held)   /* kit #9 review, round 2 (finding 1) */
		LOG ("K9 video '%s': the end-of-stream call was OFF (%s): the last %u picture(s) showed the last decoded picture again", g_k8v.name,
		     g_k8v.no_drain_why ? g_k8v.no_drain_why : "?", g_k8v.held);
	k8v_free ();
	sceIoRemove (K8_VIDEO_MARKER);
}
/* the decoder (re-)created on the open clip; 0 = ready */
static int k8v_decoder (void)
{
	const K8ShvHeader *h = (const K8ShvHeader *) g_k8v.clip;
	/* kit #9 integration review fix (backstop): after an end-of-stream call that did not return, its thread is still inside THIS decoder,
	 * its work area and its stash (the job holds a copy of g_k8v.avc with the same handles) -- never delete or re-create it under that call */
	if (g_k9_hung) {
		g_k8v.avc.why = "an end-of-stream call did not return: its decoder is still in use (never re-created in this start)";
		return -100;
	}
	k8avc_close (&g_k8v.avc);
	/* kit #9: the library reserved before vitaGL's init, when its size is this clip's (else kit #8's own init) */
	int shared = g_k9_reserved && g_k9_lib.w == h->width && g_k9_lib.h == h->height;
	if (g_k9_reserved && !shared)
		k9_video_unreserve ("the clip's size is not the reserved library's: its decoder initialises its own (kit #8)");
	g_k8v.shared = shared;
	g_k8v.stash_r = g_k8v.stash_n = 0;   /* (kit #9: a re-created decoder starts again at picture 0: nothing held) */
	g_k8v.last_pic = NULL;
	int need = k8avc_prepare_ex (&g_k8v.avc, h->width, h->height, 3, shared);
	if (need < 0)
		return need;
	g_k8v.avc.tag_clip = g_k9_delay && g_k10_flush == K10_FLUSH_REFEED && g_k10_tag;   /* kit #10 integration review: the clip's own access units time-stamped too */
	if (!g_k8v.work && !(g_k8v.work = shepgl_vram_alloc ((unsigned) need, 1u << 20))) {
		g_k8v.avc.why = "no VRAM for the decoder's work area";
		return -1;
	}
	g_k8v.last = -1;
	if (shared)
		k9_pad_free ("right before the clip's sceAvcdecCreateDecoder");   /* review fix (finding 4) */
	return k8avc_create (&g_k8v.avc, g_k8v.work, (unsigned) need);
}
/* Review fix: the engine passes the clip's path as managed code builds it -- ROOTED ("/ux0:data/shepherd/data/video/<clip>.shv": Program.Root
 * is SHEPHERD_ROOT, pal_vita.c "Paths") -- and sceIo takes device paths only ("ux0:..."): the PAL's file calls run resolve_path, this entry
 * did not, so on the Vita every clip would have been "missing" (vlx's path map accepted the leading '/'; VLX_STRICT_PATHS=1 now refuses it
 * as the device does). The same rule as resolve_path: the LAST "<device>:" component wins. */
static const char *k8_device_path (const char *in)
{
	const char *root = in;
	for (const char *p = in; *p; ++p)
		if (*p == ':' && (p == in || p [-1] != '/')) {
			const char *s = p;
			while (s > in && s [-1] != '/')
				--s;
			root = s;
		}
	return root;
}
/* info: frames, rate_num, rate_den, width, height, content x, y, w, h; returns 1 (the stream) or < 0 */
int shepherd_vita_video_open (const char *mpath, int *info, int ninfo);
int shepherd_vita_video_open (const char *mpath, int *info, int ninfo)
{
	const char *path = mpath ? k8_device_path (mpath) : "";
	k10_bootscreen_stop ("the engine's real video opens a clip", -1);   /* kit #10: normally ended already by Runtime/K10Boot.cs (the same moment) */
	shepherd_vita_video_close (1);
	memset (&g_k8v, 0, sizeof g_k8v);
	g_k8v.avc.mem = -1;
	if (!g_k8v_guard_checked) {
		g_k8v_guard_checked = 1;
		if (file_size (K8_VIDEO_MARKER, NULL) == 0) {
			sceIoRemove (K8_VIDEO_MARKER);
			g_k8v_blocked = 1;
			LOG ("WARN K8 video: the previous start stopped while the hardware video decoder was working (" K8_VIDEO_MARKER "): no real video in this start (black clips, as kit #7); the next start tries again");
		}
	}
	if (g_k8v_blocked || g_k9_hung) {
		if (g_k9_hung)   /* kit #9 review, round 2 (finding 1) */
			LOG ("K8 video: no real video in this start -- an end-of-stream call did not return (its decoder is still in use): %s drawn black (kit #7)", path);
		else
			k9_video_unreserve ("no real video in this start (kit #8's crash guard)");   /* review fix (finding 2) */
		return -100;
	}
	k9_boot_config ();   /* kit #9: SHEPHERD_K9_AVC_DELAY (and the reserve switch, read again: harmless) */
	SceUInt64 t0 = sceKernelGetProcessTimeWide ();
	unsigned len = 0;
	if (file_size (path, &len) != 0 || len <= K8SHV_HEADER_SIZE) {
		LOG ("K8 video: %s is missing: the clip is drawn black (kit #7)", path);
		if (strstr (path, K9_CLIP_NAME)) k9_video_unreserve ("the clip it was for is missing");   /* review fix (finding 2) */
		return -1;
	}
	SceUID fd = sceIoOpen (path, SCE_O_RDONLY, 0);
	/* review fix: only header + index + pictures go to VRAM -- the sound after them (the launcher's: ~1.2 MB for the Devolver logo) is never
	 * used here; k8shv_check still checks every field against the whole file (it reads only the header and the index) */
	unsigned need = len;
	if (fd >= 0) {
		K8ShvHeader hh;
		memset (&hh, 0, sizeof hh);
		if (sceIoRead (fd, &hh, sizeof hh) == (int) sizeof hh && hh.magic == K8SHV_MAGIC && hh.header_size == K8SHV_HEADER_SIZE && hh.audio_bytes
		    && hh.frames <= 100000 && hh.index_off <= len && hh.es_off <= len && hh.es_bytes <= len - hh.es_off) {
			unsigned ix_end = hh.index_off + hh.frames * 8u, es_end = hh.es_off + hh.es_bytes, end = ix_end > es_end ? ix_end : es_end;
			if (ix_end >= hh.index_off && ix_end <= len && end <= hh.audio_off && end > K8SHV_HEADER_SIZE)
				need = end;
		}
		sceIoLseek (fd, 0, SCE_SEEK_SET);
	}
	if (fd < 0 || !(g_k8v.clip = shepgl_vram_alloc (need, 256))) {
		if (fd >= 0) {
			sceIoClose (fd);
			LOG ("WARN K8 video: no VRAM for %s (%u KiB): the clip is drawn black (kit #7)", path, need / 1024);
		} else
			LOG ("WARN K8 video: %s does not open (0x%08x): the clip is drawn black (kit #7)", path, (unsigned) fd);
		if (strstr (path, K9_CLIP_NAME)) k9_video_unreserve ("the clip it was for did not open");   /* review fix (finding 2) */
		return -2;
	}
	g_k8v.clip_len = need;
	unsigned got = 0;
	if (fd >= 0) {
		while (got < need) {
			unsigned want = need - got > 1048576u ? 1048576u : need - got;
			int n = sceIoRead (fd, g_k8v.clip + got, want);
			if (n <= 0)
				break;
			got += (unsigned) n;
		}
		sceIoClose (fd);
	}
	const char *bad = got != need ? "read short" : k8shv_check (g_k8v.clip, len);   /* (len: the file's; the buffer holds `need` of it) */
	if (bad) {
		LOG ("WARN K8 video: %s refused (%s): the clip is drawn black (kit #7)", path, bad);
		k8v_free ();
		if (strstr (path, K9_CLIP_NAME)) k9_video_unreserve ("the clip it was for was refused");   /* review fix (finding 2) */
		return -3;
	}
	const K8ShvHeader *h = (const K8ShvHeader *) g_k8v.clip;
	snprintf (g_k8v.name, sizeof g_k8v.name, "%s", h->clip);
	SceUID mk = sceIoOpen (K8_VIDEO_MARKER, SCE_O_WRONLY | SCE_O_CREAT | SCE_O_TRUNC, 0777);   /* the crash guard */
	if (mk >= 0) { sceIoWrite (mk, h->clip, strlen (h->clip)); sceIoClose (mk); }
	int r = k8v_decoder ();
	if (r < 0) {
		LOG ("WARN K8 video '%s': %s (0x%08x): the clip is drawn black (kit #7)", g_k8v.name, g_k8v.avc.why ? g_k8v.avc.why : "the decoder did not open", (unsigned) g_k8v.avc.rc);
		k8v_free ();
		sceIoRemove (K8_VIDEO_MARKER);
		return -4;
	}
	int v [9] = { (int) h->frames, (int) h->rate_num, (int) h->rate_den, (int) h->width, (int) h->height, (int) h->content_x, (int) h->content_y,
		      (int) h->content_w, (int) h->content_h };
	for (int i = 0; i < ninfo && i < 9; i++)
		info [i] = v [i];
	g_k8v.open = 1;
	LOG ("K8 video '%s' open (%s): %u frames at %u/%u, %ux%u, %u KiB of pictures + %u KiB decoder work in VRAM (%.0f ms; the file %u KiB)%s", g_k8v.name, path, h->frames,
	     h->rate_num, h->rate_den, h->width, h->height, need / 1024, g_k8v.avc.mem_bytes / 1024, (double) (sceKernelGetProcessTimeWide () - t0) / 1000.0, len / 1024,
	     g_k8v.shared ? "; kit #9: the decoder library reserved before vitaGL's init" : "");
	return 1;
}
/* decode up to `frame` into dst (RGBA, pitch in pixels); returns the frame now in dst, or < 0 */
int shepherd_vita_video_decode (int id, int frame, void *dst, int pitch);
int shepherd_vita_video_decode (int id, int frame, void *dst, int pitch)
{
	(void) id;
	if (!g_k8v.open || !dst)
		return -1;
	const K8ShvHeader *h = (const K8ShvHeader *) g_k8v.clip;
	if (frame < 0)
		return -1;
	if ((unsigned) frame >= h->frames)
		frame = (int) h->frames - 1;
	void *shown = g_k8v.last_pic;   /* kit #10 review fix (finding 5): the picture on the display now (the previous call's texture) */
	if (frame <= g_k8v.last) {
		if (frame == g_k8v.last)
			return frame;   /* (the caller asked for the picture it has) */
		/* kit #9 integration review fix: a rewind (a looping clip wrapping to 0 right after its end-of-stream drain, which may be the call
		 * that hung) must not delete and re-create the decoder the abandoned thread is still using -- the clip keeps its last picture
		 * (the hang handling's contract: "skipped = the clip holds its last picture"), or the engine draws it black if none came out */
		if (g_k9_hung) {
			if (k9_hold (frame, dst, pitch) == 0)
				return frame;
			g_k8v.errors++;
			return -100;
		}
		if (k8v_decoder () < 0) {   /* a restart: H.264 starts again at the first picture */
			g_k8v.errors++;
			return -2;
		}
	}
	/* kit #9: pictures come OUT in display order, possibly some access units after they went in: feed (and, after the last access unit,
	 * drain) until picture `frame` has come out into dst; the pictures in between go into dst too (Unity's skipOnDrop: the current time's
	 * picture is shown). With SHEPHERD_K9_AVC_DELAY=0 an access unit without a picture is an error (kit #8: the same calls, AU i -> picture i) */
	while (g_k8v.last < frame) {
		int i = g_k8v.last + 1;
		int drain = g_k8v.avc.fed >= h->frames, got = 0, r;
		SceUInt64 t0 = sceKernelGetProcessTimeWide ();
		if (g_k8v.stash_r < g_k8v.stash_n) {   /* kit #9: a picture the end-of-stream call already returned */
			memcpy (dst, k8v_stash (g_k8v.stash_r++), g_k8v.stash_bytes);
			g_k8v.decodes++;
			g_k8v.last = i;
			g_k8v.last_pic = dst;
			continue;
		}
		if (drain && g_k10_flush == K10_FLUSH_REFEED && !g_k8v.refeed_off && !g_k8v.refeed_checked) {   /* kit #10 review fix (finding 7): the refeed's guard */
			const char *why = NULL;
			g_k8v.refeed_checked = 1;
			if (k10_avc_refeed_blocked (&why)) {
				g_k8v.refeed_off = 2;
				LOG ("K10 video '%s': the refeed is OFF (%s): the end-of-stream call with one picture per call (stop1) instead", g_k8v.name, why);
			}
		}
		if (drain && g_k10_flush == K10_FLUSH_REFEED && !g_k8v.refeed_off) {   /* kit #10: the drain -- the clip's own access units fed again (k8_avc.h) */
			unsigned len;
			const unsigned char *au = k8shv_au (g_k8v.clip, g_k8v.avc.refed % h->frames, &len);
			if (!g_k8v.refeed_marked) { k10_avc_refeed_begin (); g_k8v.refeed_marked = 1; }   /* review fix (finding 7): one marker for the clip's flush */
			/* integration review: bounded (k10_refeed_bounded), into the stash, then copied into dst; SHEPHERD_K10_AVC_REFEED_MS=off = the first
			 * kit #10 build's unbounded call into dst (a test switch only) */
			int bounded = -2;
			{
				const char *rm = getenv ("SHEPHERD_K10_AVC_REFEED_MS");
				if (!(rm && !strcmp (rm, "off"))) {
					if (k10_refeed_stash (pitch, h))
						bounded = k10_refeed_bounded (au, len, k8v_stash (0), (unsigned) pitch, &got, &r);
					else if (!g_k8v.refeed_nostash_logged) {
						g_k8v.refeed_nostash_logged = 1;
						LOG ("WARN K10 video '%s': no VRAM for one stash picture (%u KiB): the refeed is called on the main thread into the engine's texture, without a time limit (the marker around it)",
						     g_k8v.name, (unsigned) pitch * h->height * 4u / 1024u);
					}
				}
			}
			if (bounded == 1) {   /* the call did not come back in time: everything it holds is left to it; the clip holds its last picture */
				g_k9_hung = 1;
				g_k8v.refeed_marked = 0;
				g_k8v.refeed_off = 4;
				g_k8v.drain_checked = 1; g_k8v.no_drain = 1;
				g_k8v.no_drain_why = "a flush access unit did not come back in time (kit #10's refeed; runtime/k10-avc-refeed.off written)";
				if (k9_hold (i, dst, pitch) == 0)
					continue;
				g_k8v.errors++;
				sceIoRemove (K8_VIDEO_MARKER);
				return -3;
			}
			if (bounded == -1) {   /* no thread: stop1 (behind kit #9's guard and its bounded wait), never an unbounded refeed */
				g_k8v.refeed_off = 5;
				k10_avc_refeed_end (); g_k8v.refeed_marked = 0;
				continue;
			}
			if (bounded == -2)
				r = k10avc_refeed (&g_k8v.avc, au, len, dst, (unsigned) pitch, &got, g_k10_tag);
			else if (r >= 0 && got >= 1 && !g_k8v.avc.second)
				memcpy (dst, k8v_stash (0), g_k8v.stash_bytes);   /* the clip's picture: from the stash into the engine's texture */
			unsigned us = (unsigned) (sceKernelGetProcessTimeWide () - t0);
			g_k8v.us_total += us;
			if (us > g_k8v.us_max) g_k8v.us_max = us;
			if (r < 0) {   /* refused: stop1 (behind kit #9's guard and its bounded wait), then the held pictures stay held */
				g_k8v.refeed_off = 1;
				k10_avc_refeed_end (); g_k8v.refeed_marked = 0;   /* (the call came back: a refusal is not a crash) */
				LOG ("K10 video '%s': the decoder refused flush access unit %u (0x%08x): the end-of-stream call with one picture per call (stop1) instead", g_k8v.name,
				     g_k8v.avc.refed, (unsigned) r);
				continue;
			}
			if (g_k8v.avc.second) {
				/* review fix (finding 5): a picture of the clip's SECOND copy came out -- it is in dst now: never shown. dst gets back the picture the
				 * display shows (the previous call's: another texture of the engine's ring), and the clip holds it to its end (kit #9's "held") */
				k10_avc_refeed_end (); g_k8v.refeed_marked = 0;
				g_k8v.refeed_off = 3; g_k8v.second_at = i;
				g_k8v.drain_checked = 1; g_k8v.no_drain = 1;
				g_k8v.no_drain_why = "a picture of the clip's second copy came out of a flush access unit (kit #10's refeed): not shown";
				LOG ("WARN K10 video '%s' frame %d: a picture of the clip's SECOND copy came out of flush access unit %u (the decoder did not hand back its held pictures at the "
				     "refed IDR): not shown -- the clip holds the picture on the display from here, as kit #9", g_k8v.name, i, g_k8v.avc.refed);
				if (shown && shown != dst)
					memcpy (dst, shown, (size_t) pitch * h->height * 4u);
				else {   /* (no other picture to put back: black, never the wrong picture) */
					g_k8v.errors++;
					g_k8v.last_pic = NULL;
					sceIoRemove (K8_VIDEO_MARKER);
					return -3;
				}
				g_k8v.last_pic = dst;
				if (k9_hold (i, dst, pitch) == 0)
					continue;
				g_k8v.errors++;
				return -3;
			}
			if (got < 1) {
				if (g_k8v.avc.refed <= K8_AVC_MAX_DELAY)
					continue;   /* the next flush access unit */
				g_k8v.errors++;
				k10_avc_refeed_end (); g_k8v.refeed_marked = 0;
				if (g_k8v.errors <= 3)
					LOG ("WARN K10 video '%s' frame %d: no picture after %u flush access units (the clip's own, fed again)", g_k8v.name, i, g_k8v.avc.refed);
				sceIoRemove (K8_VIDEO_MARKER);
				return -3;
			}
			g_k8v.decodes++;
			g_k8v.last = i;
			g_k8v.last_pic = dst;
			if ((unsigned) i + 1u >= h->frames && g_k8v.refeed_marked) { k10_avc_refeed_end (); g_k8v.refeed_marked = 0; }   /* the clip's last picture: the flush is over */
			continue;
		}
		if (drain && !g_k8v.drain_checked) {   /* kit #9 review, round 2 (finding 1): may the end-of-stream call run? (once per clip) */
			const char *why = NULL;
			g_k8v.drain_checked = 1;
			if (k9_off ("SHEPHERD_K9_AVC_DRAIN"))
				why = "SHEPHERD_K9_AVC_DRAIN=0";
			else
				k9_avc_drain_blocked (&why);
			if (why) {
				g_k8v.no_drain = 1; g_k8v.no_drain_why = why;
				LOG ("K9 video '%s': the decoder's end-of-stream call is OFF (%s): from picture %d on the clip shows its last decoded picture", g_k8v.name, why, i);
			}
		}
		if (drain && g_k8v.no_drain) {   /* (round 2) the held pictures are not asked for */
			if (k9_hold (i, dst, pitch) == 0)
				continue;
			g_k8v.errors++;
			LOG ("WARN K8 video '%s' frame %d: the end-of-stream call is off and no picture came out yet", g_k8v.name, i);
			sceIoRemove (K8_VIDEO_MARKER);
			return -3;
		}
		if (!drain) {
			unsigned len;
			const unsigned char *au = k8shv_au (g_k8v.clip, g_k8v.avc.fed, &len);
			r = k8avc_decode (&g_k8v.avc, au, len, dst, (unsigned) pitch, &got);
		} else {
			/* kit #9: a buffer for each picture still held -- the VRAM stash (review fix, finding 7: one block of as many pictures as fit, made at
			 * the first drain; round 2, finding 1: the one asked for goes there too and is copied into dst after the call returned, so a call
			 * that returns late never writes into the engine's texture) */
			unsigned want = h->frames - (unsigned) i, n = 0;
			if (want > K8_AVC_MAX_DELAY) want = K8_AVC_MAX_DELAY;
			if (g_k10_flush != K10_FLUSH_KIT9) want = 1;   /* kit #10 stop1: one picture per call */
			if (!g_k8v.stash_blk) {
				g_k8v.stash_bytes = (unsigned) pitch * h->height * 4u;
				for (unsigned k = want; k >= 1 && !g_k8v.stash_blk; k--)
					if ((g_k8v.stash_blk = shepgl_vram_alloc (k * g_k8v.stash_bytes, 256)))
						g_k8v.stash_cap = k;
				if (g_k8v.stash_cap < want)
					LOG ("WARN K9 video '%s': VRAM for %u of the %u pictures the decoder may still hold after the last access unit (one block of %u KiB)", g_k8v.name,
					     g_k8v.stash_cap ? g_k8v.stash_cap : 1u, want, g_k8v.stash_cap * (g_k8v.stash_bytes / 1024u));
			}
			if (g_k8v.stash_blk) {
				void *outs [K8_AVC_MAX_DELAY];
				for (unsigned k = 0; k < g_k8v.stash_cap && n < want; k++) outs [n++] = k8v_stash (k);
				if (k9_drain_bounded (outs, n, (unsigned) pitch, &got, &r)) {   /* the call did not return in time */
					g_k9_hung = 1;
					g_k8v.no_drain = 1; g_k8v.no_drain_why = "the call did not return in time (runtime/k9-avc-drain.off written)";
					if (k9_hold (i, dst, pitch) == 0)
						continue;
					g_k8v.errors++;
					return -3;
				}
				if (r >= 0 && got >= 1) {   /* pictures i .. i+got-1, in order, in the stash: picture i into dst now */
					memcpy (dst, k8v_stash (0), g_k8v.stash_bytes);
					g_k8v.stash_r = 1; g_k8v.stash_n = (unsigned) got;
					got = 1;
				}
			} else {   /* no VRAM for one picture: the call on this thread into dst (round 1's way: no time limit; the shared marker around it) */
				void *o1 [1] = { dst };
				k9_avc_drain_begin ();
				r = k8avc_drain_n (&g_k8v.avc, o1, 1, (unsigned) pitch, &got);
				k9_avc_drain_end ();
			}
		}
		unsigned us = (unsigned) (sceKernelGetProcessTimeWide () - t0);
		g_k8v.us_total += us;
		if (us > g_k8v.us_max) g_k8v.us_max = us;
		if (r < 0 || got != 1) {
			if (r >= 0 && g_k9_delay && !drain && g_k8v.avc.fed - (unsigned) i < K8_AVC_MAX_DELAY)
				continue;   /* kit #9: the decoder holds it -- the next access unit */
			g_k8v.errors++;
			if (g_k8v.errors <= 3)
				LOG ("WARN K8 video '%s' frame %d: %s (0x%08x, %d pictures; picture buffer %p%s)", g_k8v.name, i,
				     r < 0 ? g_k8v.avc.why : !g_k9_delay ? "no picture" : drain ? "no more pictures after the last access unit" : "no picture after K8_AVC_MAX_DELAY access units",
				     (unsigned) r, got, dst, g_k9_delay ? "; kit #9 output delay tolerated" : "");
			sceIoRemove (K8_VIDEO_MARKER);
			return -3;
		}
		g_k8v.decodes++;
		g_k8v.last = i;
		g_k8v.last_pic = dst;
		if (!g_k8v.ready) {
			g_k8v.ready = 1;
			g_k8v.delay = g_k8v.avc.fed - 1u;
			sceIoRemove (K8_VIDEO_MARKER);   /* the decoder works on this console: the guard is done */
			LOG ("K8 video '%s': first picture decoded in %.2f ms into the texture at %p", g_k8v.name, us / 1000.0, dst);
			if (g_k9_delay && g_k8v.delay)
				LOG ("K9 video '%s': the decoder's first picture came after %u access unit(s) (its output delay: tolerated)", g_k8v.name, g_k8v.avc.fed);
		}
	}
	return frame;
}
#endif
