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
} K8Vid;
static K8Vid g_k8v = { .avc = { .mem = -1 } };
static int g_k8v_guard_checked, g_k8v_blocked;

int shepherd_vita_video_api (void);
int shepherd_vita_video_api (void) { return 1; }

static void k8v_free (void)
{
	k8avc_close (&g_k8v.avc);
	if (g_k8v.work) shepgl_vram_free (g_k8v.work);
	if (g_k8v.clip) shepgl_vram_free (g_k8v.clip);
	g_k8v.work = NULL; g_k8v.clip = NULL; g_k8v.open = g_k8v.ready = 0;
}
void shepherd_vita_video_close (int id);
void shepherd_vita_video_close (int id)
{
	(void) id;
	if (!g_k8v.open)
		return;
	LOG ("K8 video '%s' closed: %u pictures decoded (%u errors), decode mean %.2f ms / max %.2f ms", g_k8v.name, g_k8v.decodes, g_k8v.errors,
	     g_k8v.decodes ? (double) g_k8v.us_total / 1000.0 / g_k8v.decodes : 0.0, g_k8v.us_max / 1000.0);
	k8v_free ();
	sceIoRemove (K8_VIDEO_MARKER);
}
/* the decoder (re-)created on the open clip; 0 = ready */
static int k8v_decoder (void)
{
	const K8ShvHeader *h = (const K8ShvHeader *) g_k8v.clip;
	k8avc_close (&g_k8v.avc);
	int need = k8avc_prepare (&g_k8v.avc, h->width, h->height, 3);
	if (need < 0)
		return need;
	if (!g_k8v.work && !(g_k8v.work = shepgl_vram_alloc ((unsigned) need, 1u << 20))) {
		g_k8v.avc.why = "no VRAM for the decoder's work area";
		return -1;
	}
	g_k8v.last = -1;
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
	if (g_k8v_blocked)
		return -100;
	SceUInt64 t0 = sceKernelGetProcessTimeWide ();
	unsigned len = 0;
	if (file_size (path, &len) != 0 || len <= K8SHV_HEADER_SIZE) {
		LOG ("K8 video: %s is missing: the clip is drawn black (kit #7)", path);
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
	LOG ("K8 video '%s' open (%s): %u frames at %u/%u, %ux%u, %u KiB of pictures + %u KiB decoder work in VRAM (%.0f ms; the file %u KiB)", g_k8v.name, path, h->frames,
	     h->rate_num, h->rate_den, h->width, h->height, need / 1024, g_k8v.avc.mem_bytes / 1024, (double) (sceKernelGetProcessTimeWide () - t0) / 1000.0, len / 1024);
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
	if (frame <= g_k8v.last) {
		if (frame == g_k8v.last)
			return frame;   /* (the caller asked for the picture it has) */
		if (k8v_decoder () < 0) {   /* a restart: H.264 starts again at the first picture */
			g_k8v.errors++;
			return -2;
		}
	}
	for (int i = g_k8v.last + 1; i <= frame; i++) {
		unsigned len;
		const unsigned char *au = k8shv_au (g_k8v.clip, (unsigned) i, &len);
		SceUInt64 t0 = sceKernelGetProcessTimeWide ();
		int got = 0, r = k8avc_decode (&g_k8v.avc, au, len, dst, (unsigned) pitch, &got);
		unsigned us = (unsigned) (sceKernelGetProcessTimeWide () - t0);
		g_k8v.us_total += us;
		if (us > g_k8v.us_max) g_k8v.us_max = us;
		if (r < 0 || got != 1) {
			g_k8v.errors++;
			if (g_k8v.errors <= 3)
				LOG ("WARN K8 video '%s' frame %d: %s (0x%08x, %d pictures; picture buffer %p)", g_k8v.name, i, r < 0 ? g_k8v.avc.why : "no picture", (unsigned) r, got, dst);
			sceIoRemove (K8_VIDEO_MARKER);
			return -3;
		}
		g_k8v.decodes++;
		g_k8v.last = i;
		if (!g_k8v.ready) {
			g_k8v.ready = 1;
			sceIoRemove (K8_VIDEO_MARKER);   /* the decoder works on this console: the guard is done */
			LOG ("K8 video '%s': first picture decoded in %.2f ms into the texture at %p", g_k8v.name, us / 1000.0, dst);
		}
	}
	return frame;
}
#endif
