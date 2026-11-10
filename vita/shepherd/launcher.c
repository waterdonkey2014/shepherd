/* SHEPHERD-VITA device kit #8, track boot (vita/notes/kit8-boot.md): the TINY LAUNCHER -- the eboot.bin of a launcher build
 * (vita/shepherd/build.sh SHEPHERD_K8_LAUNCHER=1). Kit #7's eboot held everything (~44-61 MiB of code once inflated) and the Vita read all
 * of it before any of our code ran; this eboot is ~100 KiB, so it runs at once and:
 *   1. puts the Devolver splash on the screen through the hardware video decoder (k8_avc.h) -- the owner-local clip
 *      ux0:data/shepherd/data/video/<clip>.shv (vita/tools/splash_pack.py), its pictures straight into two display frame buffers, at the
 *      clip's own rate, with its sound (the game's FMOD event from the owner's audio bake) on an audio port -- on two threads of their own;
 *   2. meanwhile loads the ENGINE MODULE app0:module/shepherd.suprx (host.c + the Mono runtime + every AOT image: the same code as kit
 *      #7's eboot, only loaded later) with sceKernelLoadStartModule on the main thread;
 *   3. calls the module's entry on the main thread (k8_launch.h): host.c's main runs as before; before it sizes its memory it calls
 *      release (), which waits for the clip's end and gives everything back (CDRAM, threads, the audio port, the decoder module).
 * Env file switches (the app's own env file, read here too): SHEPHERD_K8_SPLASH=0 (or SHEPHERD_K8_BOOT=0) = no splash: the module loads
 * at once and the screen stays black until the engine draws (kit #7's picture minus the debug text / vitaGL splash, which the host's own
 * switches decide); SHEPHERD_K8_SPLASH_AUDIO=0 = the splash without its sound; SHEPHERD_K8_SPLASH_FILE=<path> = another .shv.
 * A module that does not load (or a clip that cannot play) never leaves a black screen: the launcher shows a short plain message with where
 * the log is (the host's log file, which the launcher writes itself then) and exits after START or 60 s.
 *
 * No newlib, no pthread: Sce* imports only (two newlib copies would share the main thread's TLS slot 0x89 with the module's newlib), so
 * memset / memcpy are defined here. Built -nostdlib -nostartfiles; _start is the entry the kernel calls on the main thread.
 *
 * Device kit #9, track device (vita/notes/kit9-device.md): the owner's Vita showed NO splash picture in kit #8.1 -- "frame 0: the decoder
 * gave no picture": the hardware decoder returns its pictures some access units after it got them (k8_avc.h, OUTPUT DELAY), and kit #8's
 * video_main stopped at the first access unit without one. Now it keeps feeding while pictures are pending (up to K8_AVC_MAX_DELAY access
 * units before the first one), asks for the held pictures after the last access unit (sceAvcdecDecodeStop), and times the frames by the
 * pictures that come OUT (picture n is due at the first picture's vblank + n / rate; its access unit's index does not matter). The delay the
 * decoder showed is logged ("output delay N"). SHEPHERD_K9_AVC_DELAY=0 = kit #8's loop (no picture for an access unit = the splash stops). */
#include <stdint.h>
#include <stdarg.h>
#include <psp2/kernel/processmgr.h>
#include <psp2/kernel/threadmgr.h>
#include <psp2/kernel/sysmem.h>
#include <psp2/kernel/modulemgr.h>
#include <psp2/kernel/clib.h>
#include <psp2/io/fcntl.h>
#include <psp2/io/stat.h>
#include <psp2/display.h>
#include <psp2/audioout.h>
#include <psp2/ctrl.h>
#include "k8_launch.h"
#include "k8_shv.h"
#include "k8_avc.h"
#include "debugScreen.h"        /* (the PsvDebugScreenFont type only: the font's glyphs for the failure message) */
#include "debugScreenFont.c"

#ifndef SHP_ENV_NAME
#define SHP_ENV_NAME "env_play.txt"
#endif
#ifndef SHP_LOG_NAME
#define SHP_LOG_NAME "play.txt"
#endif
#ifndef SHP_LOG_PREV_NAME
#define SHP_LOG_PREV_NAME "play.prev.txt"
#endif
#ifndef SHP_APP_TITLE
#define SHP_APP_TITLE "Shepherd"
#endif
#ifndef SHP_BUILD_ID
#define SHP_BUILD_ID "unknown"
#endif
#define DIR_ "ux0:data/shepherd"
#define MODULE_PATH "app0:module/shepherd.suprx"
#define CLIP_DEFAULT DIR_ "/data/video/Devolver_Animated_Logo_v001_4k_Audio-_HQ_HBR_MP4.shv"
/* device kit #12, track boot (vita/notes/kit12-boot.md section 2): THE BOOT SEQUENCE THE OWNER'S WAY (owner 2026-10-29 / 30) -- with
 * SHEPHERD_K12_BOOTSEQ=1 the Devolver splash is followed by the Massive Monster clip below (the game's own logo animation from level0, recorded as
 * Shepherd draws it on the Mac from the owner's copy, with its pre-roll sound from the owner's audio bake: vita/tools/mmlogo_record.sh; owner-local),
 * on the same decoder path, both before release () -- the engine then skips the game's own splash (Runtime/K12BootSeq.cs) and the host's loading
 * screen stays up until the Main Menu (k12_boot.inc.c), so nothing plays twice. Cross skips the clip on the screen (owner 2026-10-30: "X skips the
 * current video"; SHEPHERD_K12_XSKIP=0: no skip). A missing second clip = the Devolver splash alone (one line says so). Off (default in this kit,
 * the owner's device A/B first): kit #11's launcher, picture for picture. SHEPHERD_K12_BOOT=0 = every kit #12 boot row off. */
#define CLIP2_DEFAULT DIR_ "/data/video/MassiveMonster.shv"
/* device kit #13 fixlooks (vita/notes/kit13-fixlooks.md section 3): the logo is pure red on black, so 4:2:0 keeps its edges at half resolution (37 dB whatever
 * the bit rate: the owner's "extremely blurry"). MassiveMonster-luma.shv (mmlogo_pack.py --luma-red, owner-local) holds the red channel as a grey picture,
 * every edge in full-resolution luma: when the card has it, it is the second clip and G and B are cleared in the clip's content rectangle before each
 * picture is shown. The RGB clip stays where it was, so a launcher without this code is unchanged. SHEPHERD_K13_LUMARED=0 = the RGB clip, as kit #13 shipped;
 * no file = the same. (SHEPHERD_K12_CLIP2_FILE still wins; a flagged clip is honoured whatever its name.) */
#define CLIP2_LUMA DIR_ "/data/video/MassiveMonster-luma.shv"
#define FB_W 960
#define FB_H 544
#define FB_BYTES (2u * 1024u * 1024u)   /* 960 x 544 x 4 = 2,088,960, in a 2 MiB CDRAM block (256 KiB granules) */

/* the main thread's stack: the size kit #7's eboot gave host main (host.c's sceUserMainThreadStackSize; the module's entry runs on it) */
__attribute__ ((used)) const SceSize sceUserMainThreadStackSize = 512u * 1024u;

/* gcc may emit these for struct copies / zeroing even with -ffreestanding */
/* (weak: vlx links the launcher with the module's newlib, whose own win there) */
__attribute__ ((weak)) void *memset (void *d, int c, unsigned n) { return sceClibMemset (d, c, n); }
__attribute__ ((weak)) void *memcpy (void *d, const void *s, unsigned n) { return sceClibMemcpy (d, s, n); }

static K8Launch g_l;
static unsigned g_log_n;
static void llog (const char *fmt, ...)
{
	if (g_log_n + 2 >= sizeof g_l.log) return;
	va_list ap;
	va_start (ap, fmt);
	int n = sceClibVsnprintf (g_l.log + g_log_n, sizeof g_l.log - g_log_n - 1, fmt, ap);
	va_end (ap);
	if (n < 0) return;
	g_log_n += (unsigned) n < sizeof g_l.log - g_log_n - 1 ? (unsigned) n : sizeof g_l.log - g_log_n - 2;
	g_l.log [g_log_n++] = '\n';
	g_l.log [g_log_n] = 0;
}
static unsigned long long now_us (void) { return sceKernelGetProcessTimeWide (); }
static unsigned ms (unsigned long long t) { return (unsigned) (t / 1000u); }   /* (sceClib's printf: no 64-bit or float formats here) */

/* ---------------------------------------------------------------- the env file: KEY=VALUE lines (the host reads the same file)
 * Review fix: read the way the host reads it -- the WHOLE file (it was the first 8 KiB) and the LAST line with a key wins (it was the first;
 * the host's apply_env_file setenv's every line, so a later override won there and not here). The file goes into a memory block of its own
 * size, given back (env_free) before the module loads, so nothing of it is left when the host sizes its memory. */
static char *g_env;
static SceUID g_env_mem = -1;
static const char *env_get (const char *key)
{
	static char val [160];
	const char *res = 0;
	unsigned kl = sceClibStrnlen (key, 64);
	for (char *s = g_env; s && *s; ) {
		char *e = s;
		while (*e && *e != '\n' && *e != '\r') e++;
		if (s [0] != '#' && (unsigned) (e - s) > kl && !sceClibStrncmp (s, key, kl) && s [kl] == '=') {
			unsigned vl = (unsigned) (e - s) - kl - 1;
			if (vl >= sizeof val) vl = sizeof val - 1;
			sceClibMemcpy (val, s + kl + 1, vl);
			val [vl] = 0;
			res = val;
		}
		s = *e ? e + 1 : e;
	}
	return res;
}
static int env_is (const char *key, const char *v) { const char *x = env_get (key); return x && !sceClibStrncmp (x, v, 32) && sceClibStrnlen (x, 32) == sceClibStrnlen (v, 32); }
static void env_read (void)
{
	SceIoStat st;
	sceClibMemset (&st, 0, sizeof st);
	if (sceIoGetstat (DIR_ "/" SHP_ENV_NAME, &st) < 0 || st.st_size <= 0 || st.st_size > 4ll * 1024 * 1024) return;
	unsigned len = (unsigned) st.st_size, sz = (len + 1u + 0xFFFu) & ~0xFFFu;
	g_env_mem = sceKernelAllocMemBlock ("shepherd-launcher-env", SCE_KERNEL_MEMBLOCK_TYPE_USER_RW, sz, NULL);
	void *base = 0;
	if (g_env_mem < 0 || sceKernelGetMemBlockBase (g_env_mem, &base) < 0) { if (g_env_mem >= 0) sceKernelFreeMemBlock (g_env_mem); g_env_mem = -1; return; }
	g_env = (char *) base;
	unsigned got = 0;
	SceUID fd = sceIoOpen (DIR_ "/" SHP_ENV_NAME, SCE_O_RDONLY, 0);
	if (fd >= 0) {
		while (got < len) {
			int n = sceIoRead (fd, g_env + got, len - got);
			if (n <= 0) break;
			got += (unsigned) n;
		}
		sceIoClose (fd);
	}
	g_env [got] = 0;
}
static void env_free (void)
{
	if (g_env_mem >= 0) sceKernelFreeMemBlock (g_env_mem);
	g_env_mem = -1;
	g_env = 0;
}

/* ---------------------------------------------------------------- the splash */
static SceUID g_clip_mem = -1, g_fb_mem [2] = { -1, -1 };
static void *g_clip, *g_fb [2];
static unsigned g_clip_len;
static K8Avc g_avc;
static SceUID g_vthread = -1, g_athread = -1, g_vdone = -1, g_astart = -1;
static volatile int g_audio_go, g_audio_stop;
static int g_audio_port = -1;
static int g_released;
/* kit #12: the boot sequence (the second clip) and Cross's skip */
static int g_k12_seq, g_k12_xskip;
/* device kit #13, track bootc (vita/notes/kit13-bootc.md section 1): the second clip's decoder on the first clip's work area (k8_avc.h "THE WORK
 * AREA": kit #12's fresh decoder for it was refused 0x80620009 on the device, its work area off a 1 MiB boundary). SHEPHERD_K13_AVC=0 (or
 * SHEPHERD_K13_BOOTC=0) = kit #12's second clip exactly (the first clip's decoder, library and module closed, a fresh one opened). */
static int g_k13_avc;
static char g_clip2_path [160];
static int g_xheld;                    /* Cross was down at the last poll (a skip needs a new press: one held through the tap never skips) */
static int k12_cross_edge (void)
{
	SceCtrlData pad;
	sceClibMemset (&pad, 0, sizeof pad);
	if (sceCtrlPeekBufferPositive (0, &pad, 1) < 0) return 0;
	int now = (pad.buttons & SCE_CTRL_CROSS) != 0, edge = now && !g_xheld;
	g_xheld = now;
	return edge;
}

static SceUID cdram (const char *name, unsigned bytes, void **base)
{
	unsigned sz = (bytes + 0x3FFFFu) & ~0x3FFFFu;
	SceUID b = sceKernelAllocMemBlock (name, SCE_KERNEL_MEMBLOCK_TYPE_USER_CDRAM_RW, sz, NULL);
	if (b < 0) return b;
	if (sceKernelGetMemBlockBase (b, base) < 0) { sceKernelFreeMemBlock (b); return -1; }
	g_l.cdram_kib += sz / 1024u;
	return b;
}
static void show (void *fb, SceDisplaySetBufSync sync)
{
	SceDisplayFrameBuf f;
	sceClibMemset (&f, 0, sizeof f);
	f.size = sizeof f; f.base = fb; f.pitch = FB_W; f.pixelformat = SCE_DISPLAY_PIXELFORMAT_A8B8G8R8; f.width = FB_W; f.height = FB_H;
	sceDisplaySetFrameBuf (fb ? &f : 0, sync);
}

static int audio_main (SceSize args, void *argp)
{
	(void) args; (void) argp;
	const K8ShvHeader *h = (const K8ShvHeader *) g_clip;
	const int16_t *pcm = (const int16_t *) ((const char *) g_clip + h->audio_off);
	const unsigned grain = 1024;
	sceKernelWaitSema (g_astart, 1, 0);   /* the first picture is on the screen */
	for (unsigned f = 0; !g_audio_stop && f < h->audio_frames; f += grain) {   /* (the stop flag first: on a failed start the clip may be going away) */
		static int16_t last [1024 * 2];
		const int16_t *p = pcm + 2u * f;
		if (f + grain > h->audio_frames) {   /* the tail padded with silence */
			sceClibMemset (last, 0, sizeof last);
			sceClibMemcpy (last, p, (h->audio_frames - f) * 4u);
			p = last;
		}
		if (sceAudioOutOutput (g_audio_port, p) < 0) break;
	}
	sceAudioOutOutput (g_audio_port, 0);   /* drain */
	sceKernelExitThread (0);
	return 0;
}

/* kit #9: frame buffers for the pictures the decoder still holds after the last access unit (made at the first drain, freed with the splash;
 * the decoder may hand them all back in one call: each needs a buffer that is not on the screen) */
#define K9_XFB (K8_AVC_MAX_DELAY - 1u)
static SceUID g_xfb_mem [K9_XFB] = { [0 ... K9_XFB - 1] = -1 };
static void *g_xfb [K9_XFB];
static unsigned g_xfb_n;
/* kit #9 review fix (finding 3; round 2, finding 1): sceAvcdecDecodeStop has never run on a Vita -- the shared, persistent guard of k8_avc.h
 * (k9_avc_drain_*: the engine's real video uses the same files): a marker around every end-of-stream call; a marker found later (a call
 * that never returned) turns the call off on every start until runtime/k9-avc-drain.off is deleted; skipped, the splash ends at the last
 * picture before the held ones. (Round 1: a marker of the launcher's own, skipped ONCE then deleted -- with the engine's separate guard a
 * call that always crashes crashed 3 starts out of 4.) */
static int g_no_drain;
static const char *g_no_drain_why;
/* round 2: the video thread is inside the end-of-stream call (release ()'s timeout then turns the call off: runtime/k9-avc-drain.off, so a
 * call that returns late and removes the marker is still remembered), and release () walked away from the thread (a decoder call that
 * returns after that touches neither the display nor the K8Launch block: the engine owns both by then) */
static volatile int g_in_drain, g_abandon;
/* device kit #10, track boot: the drain (k8_avc.h "THE DRAIN") -- SHEPHERD_K10_AVC_FLUSH (default refeed: the clip's own access units fed again
 * after its last one bring the held pictures out, one per call into the buffer not on the screen; 0 = kit #9's end-of-stream call) */
static int g_k10_flush = K10_FLUSH_REFEED, g_k10_refeed_off;
/* kit #10 review fixes: the flush access units' own time stamp (finding 5: SHEPHERD_K10_AVC_TAG=0 = none) and the refeed's persistent guard
 * (finding 7, k8_avc.h: the marker from the first flush access unit to the flush's end; release () turns a refeed it walked away from into
 * runtime/k10-avc-refeed.off) */
static int g_k10_tag = 1, g_refeed_marked;
static volatile int g_in_refeed;
static void k9_more_fbs (unsigned want)
{
	while (g_xfb_n < want && g_xfb_n < K9_XFB) {
		SceUID b = cdram ("shepherd-splash-fb-x", FB_BYTES, &g_xfb [g_xfb_n]);
		if (b < 0) break;
		g_xfb_mem [g_xfb_n++] = b;
	}
}

static void audio_end (void);   /* (kit #12: the second clip ends the first one's sound first) */
/* one clip from the decoder to the display (kit #8-#10's loop; kit #12: returns the picture Cross skipped at + 1, 0 = not skipped) */
/* only byte stores: CDRAM is uncached for the CPU and nothing here reads it (G and B of an RGBA8888 picture) */
static unsigned g_k13_red_us, g_k13_dmax_at, g_k13_flush_us;
static void k13_luma_red (void *pic, const K8ShvHeader *h)
{
	unsigned long long t = now_us ();
	unsigned char *row = (unsigned char *) pic + ((size_t) h->content_y * FB_W + h->content_x) * 4u;
	for (unsigned y = 0; y < h->content_h; y++, row += FB_W * 4u) {
		unsigned char *p = row;
		for (unsigned x = 0; x < h->content_w; x++, p += 4) { p [1] = 0; p [2] = 0; }
	}
	unsigned us = (unsigned) (now_us () - t);
	if (us > g_k13_red_us) g_k13_red_us = us;
}

static unsigned clip_loop (void)
{
	const K8ShvHeader *h = (const K8ShvHeader *) g_clip;
	unsigned long long t0 = 0;
	unsigned i = 0;   /* the picture to show next = pictures out so far (kit #9: not the access unit's index) */
	void *on = 0;     /* the buffer on the screen */
	unsigned skipped = 0;
	if (g_k12_xskip) k12_cross_edge ();   /* (kit #12: Cross's state at the clip's start -- a press already down does not skip) */
	while (i < h->frames && !skipped) {
		void *outs [K8_AVC_MAX_DELAY];
		unsigned long long d0 = now_us ();
		int got = 0, r, drain = g_avc.fed >= h->frames;
		if (!drain) {
			unsigned len;
			const unsigned char *au = k8shv_au (g_clip, g_avc.fed, &len);
			outs [0] = g_fb [i & 1];   /* the buffer not on the screen (the one shown last is g_fb [(i - 1) & 1]) */
			r = k8avc_decode (&g_avc, au, len, outs [0], FB_W, &got);
		} else if (g_k10_flush != K10_FLUSH_KIT9) {   /* kit #10: the drain (kit #8's loop never gets here either) */
			outs [0] = g_fb [i & 1];   /* the buffer not on the screen, as for every access unit */
			if (g_k10_flush == K10_FLUSH_REFEED && !g_k10_refeed_off) {
				unsigned len;
				const unsigned char *au = k8shv_au (g_clip, g_avc.refed % h->frames, &len);
				if (!g_refeed_marked) { k10_avc_refeed_begin (); g_refeed_marked = 1; }   /* review fix (finding 7): the refeed's guard */
				g_in_refeed = 1;
				r = k10avc_refeed (&g_avc, au, len, outs [0], FB_W, &got, g_k10_tag);
				g_in_refeed = 0;
				if (g_abandon)   /* (release () walked away from this thread inside the refeed: its .off is written; nothing more here) */
					break;
				if (r < 0) {   /* refused: stop1 once (behind kit #9's shared guard), then the held pictures stay held */
					g_k10_refeed_off = 1; g_l.k10_fallback = 1;
					k10_avc_refeed_end (); g_refeed_marked = 0;   /* (the call came back: refused is not a crash) */
					llog ("launcher: the decoder refused flush access unit %u (0x%08x): the end-of-stream call with one picture per call (stop1) instead", g_avc.refed, (unsigned) r);
					continue;
				}
				if (g_avc.second) {   /* review fix (finding 5): a picture of the clip's SECOND copy -- never shown (it is in the buffer not on the screen) */
					sceClibSnprintf (g_l.splash_why, sizeof g_l.splash_why, "frame %u: a picture of the clip's second copy came out of flush access unit %u (the decoder did not hand "
							 "back its held pictures at the refed IDR): not shown -- %u of %u pictures shown, as kit #9", i, g_avc.refed, i, h->frames);
					break;
				}
				if (got < 1) {
					if (g_avc.refed <= K8_AVC_MAX_DELAY) { g_l.decode_us_total += (unsigned) (now_us () - d0); continue; }
					sceClibSnprintf (g_l.splash_why, sizeof g_l.splash_why, "frame %u: no picture after %u flush access units (the clip's own, fed again: %u of %u pictures)",
							 i, g_avc.refed, i, h->frames);
					break;
				}
			} else {
				if (g_no_drain) {   /* (kit #9's guard covers every end-of-stream call) */
					sceClibSnprintf (g_l.splash_why, sizeof g_l.splash_why, "frame %u: the end-of-stream call skipped (%s); %u of %u pictures shown",
							 i, g_no_drain == 2 ? "SHEPHERD_K9_AVC_DRAIN=0" : "runtime/k9-avc-drain.off", i, h->frames);
					break;
				}
				k9_avc_drain_begin ();
				g_in_drain = 1;
				r = k10avc_stop1 (&g_avc, outs [0], FB_W, &got);
				g_in_drain = 0;
				k9_avc_drain_end ();
				if (!g_abandon && r < 0) {
					sceClibSnprintf (g_l.splash_why, sizeof g_l.splash_why, "frame %u: %s (0x%08x); %u of %u pictures shown", i, g_avc.why, (unsigned) r, i, h->frames);
					break;
				}
				if (!g_abandon && got < 1) {
					sceClibSnprintf (g_l.splash_why, sizeof g_l.splash_why, "frame %u: the end-of-stream call (stop1) returned no more pictures; %u of %u pictures shown", i, i, h->frames);
					break;
				}
			}
		} else {   /* (kit #9 only: kit #8's loop never gets here -- fed == frames means i == frames) */
			if (g_no_drain) {   /* review fix (finding 3; round 2: the shared guard) */
				sceClibSnprintf (g_l.splash_why, sizeof g_l.splash_why, "frame %u: the end-of-stream call skipped (%s); %u of %u pictures shown",
						 i, g_no_drain == 2 ? "SHEPHERD_K9_AVC_DRAIN=0" : "runtime/k9-avc-drain.off", i, h->frames);
				break;
			}
			unsigned want = h->frames - i, n = 0;
			if (want > K8_AVC_MAX_DELAY) want = K8_AVC_MAX_DELAY;
			k9_more_fbs (want - 1u);
			void *all [2 + K9_XFB];
			unsigned na = 0;
			all [na++] = g_fb [0]; all [na++] = g_fb [1];
			for (unsigned k = 0; k < g_xfb_n; k++) all [na++] = g_xfb [k];
			for (unsigned k = 0; k < na && n < want; k++) if (all [k] != on) outs [n++] = all [k];
			k9_avc_drain_begin ();   /* review fix (finding 3; round 2: the shared marker -- left behind if the call never returns) */
			g_in_drain = 1;
			r = k8avc_drain_n (&g_avc, outs, n, FB_W, &got);
			g_in_drain = 0;
			k9_avc_drain_end ();
		}
		if (g_abandon)   /* round 2: release () gave up on this thread while it was in the decoder -- nothing more here */
			break;
		unsigned dus = (unsigned) (now_us () - d0);
		if (r < 0 || got < 1) {
			if (r >= 0 && g_l.k9_delay && !drain && g_avc.fed - i < K8_AVC_MAX_DELAY) {   /* kit #9: the decoder holds it -- feed the next access unit */
				g_l.decode_us_total += dus;
				continue;
			}
			if (r < 0 || !g_l.k9_delay)
				sceClibSnprintf (g_l.splash_why, sizeof g_l.splash_why, "frame %u: %s (0x%08x, %d pictures)", i, r < 0 ? g_avc.why : "the decoder gave no picture",
						 (unsigned) (r < 0 ? r : 0), got);
			else if (drain)
				sceClibSnprintf (g_l.splash_why, sizeof g_l.splash_why, "frame %u: the decoder returned no more pictures after the last access unit (%u of %u pictures; %u returned by the end-of-stream call)",
						 i, i, h->frames, g_avc.drained);
			else
				sceClibSnprintf (g_l.splash_why, sizeof g_l.splash_why, "frame %u: no picture after %u access units (the decoder does not answer)", i, g_avc.fed);
			break;
		}
		if (!i) g_l.out_delay = g_avc.fed - 1u;   /* kit #9: access units it took before its first picture (0 = a picture per access unit) */
		g_l.decode_us_total += dus;
		if (dus > g_l.decode_us_max) { g_l.decode_us_max = dus; g_k13_dmax_at = i; }
		if (drain && dus > g_k13_flush_us) g_k13_flush_us = dus;   /* (kit #13 fixlooks: the clip's end -- is the drain the hitch?) */
		for (int k = 0; k < got && i < h->frames; k++) {
			/* frame i is due at t0 + i / rate (t0 = the first frame's vblank); it reaches the screen at the vblank after the set */
			if (i) {
				unsigned long long due = t0 + (unsigned long long) i * h->rate_den * 1000000ull / h->rate_num, n = now_us ();
				if (due > n + 17000) sceKernelDelayThread ((SceUInt) (due - n - 17000));
				else if (n > due + 17000) g_l.frames_late++;
			}
			if (h->flags & K8SHV_F_LUMA_RED) k13_luma_red (outs [k], h);   /* (kit #13 fixlooks; the RGB clips: this one test) */
			show (outs [k], SCE_DISPLAY_SETBUF_NEXTFRAME);
			sceDisplayWaitVblankStart ();   /* the new buffer is scanned from here: the other one may be decoded into */
			on = outs [k];
			if (!i) {
				t0 = g_l.t_first_frame = now_us ();
				if (g_astart >= 0) sceKernelSignalSema (g_astart, 1);
			}
			g_l.frames_shown++;
			i++;
			if (g_k12_xskip && k12_cross_edge ()) { skipped = i; break; }   /* kit #12: Cross skips the clip on the screen (this picture stays until the next) */
		}
	}
	return skipped;
}

/* kit #12: the second clip of the boot sequence on the video thread, after the first (the main thread is loading the module meanwhile): the first
 * clip's decoder, sound and memory go, the second clip is read into CDRAM, its decoder and sound start, the same loop plays it -- its counters in
 * the k12 fields (the first clip's stay where the host reads them) */
static void clip2_play (void)
{
	/* review fix (kit #12 finding 5): kit #9's round-2 rule -- once release () gave up on this thread (g_abandon: it already ended the sound and
	 * the host runs), the thread touches nothing more: no new sound port or thread, no decoder, no CDRAM, no write to the K8Launch block */
	if (g_abandon) return;
	unsigned keep_shown = g_l.frames_shown, keep_late = g_l.frames_late, keep_dmax = g_l.decode_us_max, keep_dtot = g_l.decode_us_total, keep_cdram = g_l.cdram_kib;
	unsigned long long keep_first = g_l.t_first_frame;
	char keep_why [sizeof g_l.splash_why];
	sceClibMemcpy (keep_why, g_l.splash_why, sizeof keep_why);
	audio_end ();
	if (!g_k13_avc) k8avc_close (&g_avc);   /* (kit #13: the decoder, its work area, the library and the module stay for the second clip) */
	if (g_clip_mem >= 0) { sceKernelFreeMemBlock (g_clip_mem); g_clip_mem = -1; g_clip = 0; }
	const char *why = 0;
	int rc = 0;
	SceIoStat st;
	sceClibMemset (&st, 0, sizeof st);
	rc = sceIoGetstat (g_clip2_path, &st);
	if (rc < 0 || st.st_size <= K8SHV_HEADER_SIZE || st.st_size > 64ll * 1024 * 1024) why = "the clip file is missing (vita/tools/mmlogo_record.sh makes data/video/MassiveMonster.shv)";
	else {
		g_clip_len = (unsigned) st.st_size;
		if ((g_clip_mem = cdram ("shepherd-splash-clip2", g_clip_len, &g_clip)) < 0) { why = "no CDRAM for the clip"; rc = g_clip_mem; }
	}
	if (!why) {
		SceUID fd = sceIoOpen (g_clip2_path, SCE_O_RDONLY, 0);
		unsigned got = 0;
		if (fd >= 0) {
			while (got < g_clip_len) {
				unsigned want = g_clip_len - got > 1048576u ? 1048576u : g_clip_len - got;
				int n = sceIoRead (fd, (char *) g_clip + got, want);
				if (n <= 0) break;
				got += (unsigned) n;
			}
			sceIoClose (fd);
		}
		const char *bad = got == g_clip_len ? k8shv_check (g_clip, g_clip_len) : "the clip file read short";
		if (bad) { why = bad; rc = (int) got; }
		else if (((const K8ShvHeader *) g_clip)->width != FB_W || ((const K8ShvHeader *) g_clip)->height != FB_H) why = "the clip is not 960 x 544";
	}
	if (g_abandon) {   /* (review fix: release () gave up while the file was read -- its own CDRAM block goes back, nothing else is touched) */
		if (g_clip_mem >= 0) { sceKernelFreeMemBlock (g_clip_mem); g_clip_mem = -1; g_clip = 0; }
		if (g_k13_avc) k8avc_close (&g_avc);   /* kit #13 bootc review fix: the first clip's decoder was kept for this clip -- this thread owns it (not inside a
		                                        * decoder call) and nobody else ever would give it back: its 7 MiB work area, the library and the module go */
		return;
	}
	if (!why) {
		const K8ShvHeader *h = (const K8ShvHeader *) g_clip;
		if (g_k13_avc) {   /* kit #13: the first clip's decoder created again on its own work area (the pointer the device took); refused -> a fresh one on a 1 MiB boundary */
			void *w1 = g_avc.ctrl.frameBuf.pBuf;
			rc = h->width == g_avc.w && h->height == g_avc.h ? k13avc_reopen (&g_avc) : k8avc_fail (&g_avc, "the clip's size is not the first clip's", -1);
			if (rc < 0) {
				int r1 = rc;
				const char *w1why = g_avc.why;
				k8avc_close (&g_avc);
				rc = k13avc_open (&g_avc, h->width, h->height, 3, SCE_KERNEL_MEMBLOCK_TYPE_USER_CDRAM_RW);
				llog ("launcher: K13 clip 2: the first clip's decoder could not be created again on its work area 0x%08x (%s, 0x%08x): a fresh decoder, work area 0x%08x (%s)%s",
				      (unsigned) w1, w1why ? w1why : "?", (unsigned) r1, (unsigned) g_avc.ctrl.frameBuf.pBuf, g_avc.k13_how == 1 ? "the memblock's 1 MiB alignment" : "1 MiB more, aligned inside",
				      rc < 0 ? " -- REFUSED too" : "");
			} else
				llog ("launcher: K13 clip 2: the first clip's decoder created again on its own work area 0x%08x (%u KiB; the library and the module kept: nothing allocated)",
				      (unsigned) w1, g_avc.ctrl.frameBuf.size / 1024u);
		} else
		rc = k8avc_open (&g_avc, h->width, h->height, 3, SCE_KERNEL_MEMBLOCK_TYPE_USER_CDRAM_RW);
		if (g_abandon) {   /* (kit #12 fix + kit, review finding: release () gave up while the decoder opened -- it goes again with the clip's block;
		                    * no sound port, no thread, no write to the K8Launch block. vlx cannot reach this path: checked by construction) */
			if (rc >= 0 || g_k13_avc) k8avc_close (&g_avc);   /* (kit #13 bootc review fix: also a refused re-create / fresh open -- its kept work area, library and module) */
			if (g_clip_mem >= 0) { sceKernelFreeMemBlock (g_clip_mem); g_clip_mem = -1; g_clip = 0; }
			return;
		}
		if (rc < 0) why = g_avc.why ? g_avc.why : "the decoder did not open";
		else {
			g_avc.tag_clip = g_l.k9_delay && g_k10_flush == K10_FLUSH_REFEED && g_k10_tag;
			sceClibSnprintf (g_l.k12_clip2, sizeof g_l.k12_clip2, "%s", h->clip);
			g_l.k12_clip2_total = h->frames;
			g_audio_stop = 0;
			if (g_astart >= 0) { sceKernelDeleteSema (g_astart); g_astart = -1; }
			g_astart = sceKernelCreateSema ("shepherd-splash-audio2", 0, 0, 1, NULL);
			if (h->audio_bytes && g_astart >= 0 && (g_audio_port = sceAudioOutOpenPort (SCE_AUDIO_OUT_PORT_TYPE_BGM, 1024, (int) h->audio_rate, SCE_AUDIO_OUT_MODE_STEREO)) >= 0) {
				int vol [2] = { SCE_AUDIO_OUT_MAX_VOL, SCE_AUDIO_OUT_MAX_VOL };
				sceAudioOutSetVolume (g_audio_port, (SceAudioOutChannelFlag) (SCE_AUDIO_VOLUME_FLAG_L_CH | SCE_AUDIO_VOLUME_FLAG_R_CH), vol);
				g_athread = sceKernelCreateThread ("shepherd-splash-audio2", audio_main, 70, 0x4000, 0, 0x40000 /* USER_2 */, NULL);
				if (g_athread >= 0 && sceKernelStartThread (g_athread, 0, NULL) < 0) { sceKernelDeleteThread (g_athread); g_athread = -1; }
			}
			/* the second clip through the same loop and counters (then moved to the k12 fields; the first clip's put back). Review fix: marked
			 * as playing first, so a release () that gives up during the clip leaves the host a block that says so (its first-clip counters then
			 * hold this clip's partial counts) */
			g_l.k12_clip2_state = 1;
			sceClibSnprintf (g_l.k12_clip2_why, sizeof g_l.k12_clip2_why, "still playing when the launcher's wait ended (the first clip's counters hold its partial counts)");
			g_l.frames_shown = 0; g_l.frames_late = 0; g_l.t_first_frame = 0; g_l.splash_why [0] = 0;
			unsigned sk = clip_loop ();
			if (g_abandon) {   /* (review fix: release () gave up during the clip -- nothing more here, as video_main after the first clip.
			                    * kit #12 fix + kit, review finding: release () sets g_abandon only after its own audio_end () returned, so this
			                    * thread alone owns the second clip's sound now -- end it (signal, join, release the BGM port the engine's
			                    * audio needs), and give the decoder and the clip's block back: nobody else ever would) */
				audio_end ();
				k8avc_close (&g_avc);
				if (g_clip_mem >= 0) { sceKernelFreeMemBlock (g_clip_mem); g_clip_mem = -1; g_clip = 0; }
				return;
			}
			if (g_refeed_marked) { k10_avc_refeed_end (); g_refeed_marked = 0; }   /* (the second clip's flush is over: kit #10's refeed guard file goes) */
			g_l.k12_clip2_shown = g_l.frames_shown; g_l.k12_clip2_late = g_l.frames_late; g_l.k12_t_clip2_first = g_l.t_first_frame;
			if (h->flags & K8SHV_F_LUMA_RED)
				llog ("launcher: K13 clip 2: luma-red (%u x %u at %u,%u: G and B cleared before each picture, at most %u us; SHEPHERD_K13_LUMARED=0 = the RGB clip)",
				      h->content_w, h->content_h, h->content_x, h->content_y, g_k13_red_us);
			g_l.k12_t_clip2_end = now_us ();
			g_l.k12_skip2_at = sk;
			g_l.k12_clip2_state = sk ? 3 : g_l.frames_shown == h->frames ? 2 : -1;
			if (g_l.k12_clip2_state < 0) sceClibSnprintf (g_l.k12_clip2_why, sizeof g_l.k12_clip2_why, "%s", g_l.splash_why [0] ? g_l.splash_why : "stopped early");
			if (sk) g_audio_stop = 1;   /* (the sound goes with the picture) */
			if (g_astart >= 0 && !g_l.t_first_frame) sceKernelSignalSema (g_astart, 1);
		}
	}
	if (why) { g_l.k12_clip2_state = -1; sceClibSnprintf (g_l.k12_clip2_why, sizeof g_l.k12_clip2_why, "%s (0x%08x)", why, (unsigned) rc); }
	g_l.frames_shown = keep_shown; g_l.frames_late = keep_late; g_l.t_first_frame = keep_first;   /* (the first clip's counters, where the host reads them) */
	g_l.decode_us_max = keep_dmax; g_l.decode_us_total = keep_dtot;
	sceClibMemcpy (g_l.splash_why, keep_why, sizeof g_l.splash_why);
	if (keep_cdram > g_l.cdram_kib) g_l.cdram_kib = keep_cdram;
}

static int video_main (SceSize args, void *argp)
{
	(void) args; (void) argp;
	const K8ShvHeader *h = (const K8ShvHeader *) g_clip;
	unsigned sk1 = clip_loop ();
	llog ("launcher: K13 clip 1: the slowest picture took %u us (picture %u of %u, %u late), the slowest of the held pictures at the clip's end %u us", g_l.decode_us_max, g_k13_dmax_at, h->frames, g_l.frames_late, g_k13_flush_us);
	if (g_abandon) {   /* round 2: the host runs (it never waits for g_vdone again): exit without touching its block */
		sceKernelExitThread (0);
		return 0;
	}

	if (g_refeed_marked) { k10_avc_refeed_end (); g_refeed_marked = 0; }   /* kit #10 review fix (finding 7): the flush is over, the refeed came back */
	g_l.fed = g_avc.fed; g_l.drained = g_avc.drained;
	g_l.k10_flush = g_k10_flush; g_l.refed = g_avc.refed; g_l.flushed = g_avc.flushed; g_l.flush_rc = g_avc.flush_rc;   /* kit #10 */
	g_l.k10_tag = g_k10_tag; g_l.second = g_avc.second;   /* kit #10 review fix (finding 5) */
	g_l.pts_seen = g_avc.pts_seen; g_l.pts_bad = g_avc.pts_bad; g_l.flush_ok = g_avc.flush_ok;   /* kit #10 integration review */
	g_l.t_splash_end = now_us ();
	g_l.splash_state = g_l.frames_shown == h->frames || sk1 ? 2 : -1;   /* (kit #12: a clip Cross skipped ended as asked) */
	if (sk1) { g_l.k12_skip1 = 1; g_l.k12_skip1_at = sk1; g_audio_stop = 1; }
	if (g_astart >= 0 && !g_l.t_first_frame) sceKernelSignalSema (g_astart, 1);   /* (no picture: let the audio thread end) */
	if (g_k12_seq && g_l.splash_state == 2) clip2_play ();   /* kit #12: the Massive Monster clip, then the host */
	if (g_abandon) {   /* (review fix: release () gave up during the second clip -- exit without touching anything, as after the first) */
		sceKernelExitThread (0);
		return 0;
	}
	sceKernelSignalSema (g_vdone, 1);
	sceKernelExitThread (0);
	return 0;
}

/* the splash's sound stopped and its thread + port given back (review fix: shared by release () -- also on its timeout path, so a hung
 * decoder no longer keeps the BGM port the engine's shepaudio opens later -- and by the failed start, which freed the clip while the
 * already started audio thread could still read it) */
static void audio_end (void)
{
	g_audio_stop = 1;
	if (g_astart >= 0) sceKernelSignalSema (g_astart, 1);   /* (a thread still waiting for the first picture wakes and sees the flag) */
	if (g_athread >= 0) {
		SceUInt to = 2000000;
		sceKernelWaitThreadEndCB (g_athread, NULL, &to);
		sceKernelDeleteThread (g_athread);
		g_athread = -1;
	}
	if (g_audio_port >= 0) { sceAudioOutReleasePort (g_audio_port); g_audio_port = -1; }
}

static void splash_free (void)
{
	k8avc_close (&g_avc);
	for (int i = 0; i < 2; i++) if (g_fb_mem [i] >= 0) { sceKernelFreeMemBlock (g_fb_mem [i]); g_fb_mem [i] = -1; g_fb [i] = 0; }
	for (unsigned i = 0; i < g_xfb_n; i++) if (g_xfb_mem [i] >= 0) { sceKernelFreeMemBlock (g_xfb_mem [i]); g_xfb_mem [i] = -1; g_xfb [i] = 0; }   /* kit #9 */
	g_xfb_n = 0;
	if (g_clip_mem >= 0) { sceKernelFreeMemBlock (g_clip_mem); g_clip_mem = -1; g_clip = 0; }
	if (g_vdone >= 0) { sceKernelDeleteSema (g_vdone); g_vdone = -1; }
	if (g_astart >= 0) { sceKernelDeleteSema (g_astart); g_astart = -1; }
}

static int splash_fail (const char *why, int rc)
{
	sceClibSnprintf (g_l.splash_why, sizeof g_l.splash_why, "%s (0x%08x)", why, (unsigned) rc);
	g_l.splash_state = -1;
	splash_free ();
	return -1;
}

static int splash_start (const char *path)
{
	SceIoStat st;
	sceClibMemset (&st, 0, sizeof st);
	int r = sceIoGetstat (path, &st);
	if (r < 0 || st.st_size <= K8SHV_HEADER_SIZE || st.st_size > 64ll * 1024 * 1024) return splash_fail ("the clip file is missing or has no pictures", r);
	g_clip_len = (unsigned) st.st_size;
	if ((g_clip_mem = cdram ("shepherd-splash-clip", g_clip_len, &g_clip)) < 0) return splash_fail ("no CDRAM for the clip", g_clip_mem);
	SceUID fd = sceIoOpen (path, SCE_O_RDONLY, 0);
	if (fd < 0) return splash_fail ("the clip file does not open", fd);
	unsigned got = 0;
	while (got < g_clip_len) {   /* 1 MiB reads (the card's best size is 128 KiB+, kit #7's storage test) */
		unsigned want = g_clip_len - got > 1048576u ? 1048576u : g_clip_len - got;
		int n = sceIoRead (fd, (char *) g_clip + got, want);
		if (n <= 0) break;
		got += (unsigned) n;
	}
	sceIoClose (fd);
	if (got != g_clip_len) return splash_fail ("the clip file read short", (int) got);
	const char *bad = k8shv_check (g_clip, g_clip_len);
	if (bad) return splash_fail (bad, 0);
	const K8ShvHeader *h = (const K8ShvHeader *) g_clip;
	sceClibSnprintf (g_l.clip, sizeof g_l.clip, "%s", h->clip);
	g_l.frames_total = h->frames;
	if (h->width != FB_W || h->height != FB_H) return splash_fail ("the clip is not 960 x 544", (int) h->width);
	r = k8avc_open (&g_avc, h->width, h->height, 3, SCE_KERNEL_MEMBLOCK_TYPE_USER_CDRAM_RW);
	if (r < 0) return splash_fail (g_avc.why ? g_avc.why : "the decoder did not open", g_avc.rc);
	g_avc.tag_clip = g_l.k9_delay && g_k10_flush == K10_FLUSH_REFEED && g_k10_tag;   /* kit #10 integration review: the clip's own access units time-stamped too */
	g_l.cdram_kib += g_avc.mem_bytes / 1024u;
	for (int i = 0; i < 2; i++) {
		if ((g_fb_mem [i] = cdram ("shepherd-splash-fb", FB_BYTES, &g_fb [i])) < 0) return splash_fail ("no CDRAM for the frame buffers", g_fb_mem [i]);
		sceClibMemset (g_fb [i], 0, FB_BYTES);
	}
	g_vdone = sceKernelCreateSema ("shepherd-splash-done", 0, 0, 1, NULL);
	g_astart = sceKernelCreateSema ("shepherd-splash-audio", 0, 0, 1, NULL);
	if (g_vdone < 0 || g_astart < 0) return splash_fail ("no semaphores", g_vdone < 0 ? g_vdone : g_astart);
	/* the sound: an audio port of its own (released at release (), long before the engine's shepaudio opens its own) */
	if (h->audio_bytes && !env_is ("SHEPHERD_K8_SPLASH_AUDIO", "0")) {
		g_audio_port = sceAudioOutOpenPort (SCE_AUDIO_OUT_PORT_TYPE_BGM, 1024, (int) h->audio_rate, SCE_AUDIO_OUT_MODE_STEREO);
		if (g_audio_port >= 0) {
			int vol [2] = { SCE_AUDIO_OUT_MAX_VOL, SCE_AUDIO_OUT_MAX_VOL };
			sceAudioOutSetVolume (g_audio_port, (SceAudioOutChannelFlag) (SCE_AUDIO_VOLUME_FLAG_L_CH | SCE_AUDIO_VOLUME_FLAG_R_CH), vol);
			g_athread = sceKernelCreateThread ("shepherd-splash-audio", audio_main, 70, 0x4000, 0, 0x40000 /* USER_2 */, NULL);
			if (g_athread >= 0 && sceKernelStartThread (g_athread, 0, NULL) >= 0) g_l.audio_state = 1;
			else { sceClibSnprintf (g_l.audio_why, sizeof g_l.audio_why, "no audio thread (0x%08x)", (unsigned) g_athread); g_l.audio_state = -1; }
		} else {
			sceClibSnprintf (g_l.audio_why, sizeof g_l.audio_why, "the audio port did not open (0x%08x)", (unsigned) g_audio_port);
			g_l.audio_state = -1;
		}
	} else if (!h->audio_bytes)
		sceClibSnprintf (g_l.audio_why, sizeof g_l.audio_why, "the clip has no sound");
	else
		sceClibSnprintf (g_l.audio_why, sizeof g_l.audio_why, "SHEPHERD_K8_SPLASH_AUDIO=0");
	g_vthread = sceKernelCreateThread ("shepherd-splash-video", video_main, 80, 0x4000, 0, 0x20000 /* USER_1 */, NULL);
	if (g_vthread < 0 || sceKernelStartThread (g_vthread, 0, NULL) < 0) {
		int vr = g_vthread;
		if (g_vthread >= 0) { sceKernelDeleteThread (g_vthread); g_vthread = -1; }   /* (created, not started: dormant) */
		audio_end ();   /* review fix: the audio thread has ended before splash_fail frees the clip it reads */
		return splash_fail ("no video thread", vr);
	}
	g_l.splash_state = 1;
	return 0;
}

/* the host calls this once, before its partition (k8_launch.h) */
static int release (void)
{
	if (g_released) return 0;
	g_released = 1;
	unsigned long long t = now_us ();
	if (g_l.splash_state == 1 && g_vdone >= 0) {
		SceUInt to = (g_l.wait_ms_max ? g_l.wait_ms_max : 15000u) * 1000u;
		if (sceKernelWaitSema (g_vdone, 1, &to) < 0) {
			/* a decoder that hangs must not stop the boot: the video thread, the decoder and the clip are left (the thread may be inside the
			 * decoder; their memory too: the partition then has less, and the engine's real video finds the decoder library taken: black
			 * clips); the sound ends and its port is given back (review fix: the engine's shepaudio opens one later) */
			audio_end ();
			g_abandon = 1;   /* round 2 (finding 1): a decoder call that returns late touches nothing more */
			if (g_in_drain) {   /* round 2 (finding 1): the end-of-stream call hangs -- off from now on, for the engine's real video too */
				k9_avc_drain_off ("the decoder's end-of-stream call (sceAvcdecDecodeStop) did not return within the launcher's splash wait (15 s): a hang. "
						  "Kit #9 skips the call (launcher and engine) while this file is here: delete it to try the call again.\n");
				llog ("release: the splash's video thread is inside the decoder's end-of-stream call (sceAvcdecDecodeStop): runtime/k9-avc-drain.off written -- the call is skipped from now on (the engine's real video too) until that file is deleted");
			}
			if (g_in_refeed) {   /* kit #10 review fix (finding 7): a flush access unit that does not come back -- the refeed is off from now on */
				k9_avc_file_write (K10_AVC_REFEED_OFF, "the decoder did not come back from a flush access unit (kit #10's refeed) within the launcher's splash wait (15 s): a hang. "
						   "Kit #10 skips the refeed (launcher and engine) while this file is here: delete it to try it again.\n");
				llog ("release: the splash's video thread is inside a flush access unit (the refeed): runtime/k10-avc-refeed.off written -- the refeed is skipped from now on (the engine's real video too) until that file is deleted");
			}
			llog ("release: the splash did not end within %u ms: the video thread, the decoder and the clip are left running; the sound stopped and its port given back; the host goes on",
			      g_l.wait_ms_max ? g_l.wait_ms_max : 15000u);
			g_l.t_released = now_us ();
			return (int) ((g_l.t_released - t) / 1000u);
		}
	}
	int waited = (int) ((now_us () - t) / 1000u);
	show (0, SCE_DISPLAY_SETBUF_IMMEDIATE);   /* nothing scans the frame buffers any more: black until the engine draws */
	if (g_vthread >= 0) {
		/* review fix: g_vdone is signalled just BEFORE the thread exits -- wait for its end, or the delete fails (not dormant) and leaks it */
		SceUInt to = 1000000;
		sceKernelWaitThreadEndCB (g_vthread, NULL, &to);
		sceKernelDeleteThread (g_vthread);
		g_vthread = -1;
	}
	audio_end ();
	splash_free ();
	g_l.t_released = now_us ();
	return waited;
}

/* ---------------------------------------------------------------- the failure message (a module that does not load) */
static void text_at (uint32_t *fb, int x, int y, const char *s, uint32_t color)
{
	const PsvDebugScreenFont *f = &psvDebugScreenFont;
	for (; *s; s++, x += 16) {
		unsigned char c = (unsigned char) *s;
		if (x + 16 > FB_W) break;
		if (c < f->first || c > f->last) continue;
		const unsigned char *g = f->glyphs + (unsigned) (c - f->first) * 8u;
		for (int r = 0; r < 16; r++)
			for (int col = 0; col < 16; col++)
				if (g [r >> 1] & (0x80u >> (col >> 1))) fb [(y + r) * FB_W + x + col] = color;
	}
}
static void log_file_lines (const char *first)
{
	/* the host never ran: the launcher writes the app's log itself (the previous one kept as .prev, as the host does) */
	sceIoRemove (DIR_ "/" SHP_LOG_PREV_NAME);
	sceIoRename (DIR_ "/" SHP_LOG_NAME, DIR_ "/" SHP_LOG_PREV_NAME);
	SceUID fd = sceIoOpen (DIR_ "/" SHP_LOG_NAME, SCE_O_WRONLY | SCE_O_CREAT | SCE_O_TRUNC, 0777);
	if (fd < 0) return;
	char b [256];
	int n = sceClibSnprintf (b, sizeof b, "BEGIN %s launcher (kit #8) build %s\n", SHP_APP_TITLE, SHP_BUILD_ID);
	sceIoWrite (fd, b, (SceSize) n);
	sceIoWrite (fd, g_l.log, g_log_n);
	n = sceClibSnprintf (b, sizeof b, "%s\n", first);
	sceIoWrite (fd, b, (SceSize) n);
	sceIoClose (fd);
}
static void fail_screen (const char *line1, const char *line2)
{
	release ();   /* the splash ends first (its memory comes back) */
	void *fb = 0;
	SceUID b = cdram ("shepherd-fail-fb", FB_BYTES, &fb);
	if (b >= 0) {
		uint32_t *p = (uint32_t *) fb;
		for (unsigned i = 0; i < FB_W * FB_H; i++) p [i] = 0xFF000000u;
		text_at (p, 32, 160, SHP_APP_TITLE " could not start.", 0xFFFFFFFFu);
		text_at (p, 32, 208, line1, 0xFFC0C0C0u);
		text_at (p, 32, 240, line2, 0xFFC0C0C0u);
		text_at (p, 32, 304, "The log: " DIR_, 0xFFC0C0C0u);
		text_at (p, 32, 336, "  /" SHP_LOG_NAME, 0xFFC0C0C0u);
		text_at (p, 32, 400, "Press START to close (or wait 60 s).", 0xFF808080u);
		show (fb, SCE_DISPLAY_SETBUF_NEXTFRAME);
	}
	SceCtrlData pad;
	for (int i = 0; i < 600; i++) {
		sceClibMemset (&pad, 0, sizeof pad);
		sceCtrlPeekBufferPositive (0, &pad, 1);
		if (pad.buttons & SCE_CTRL_START) break;
		sceKernelDelayThread (100 * 1000);
	}
	sceKernelExitProcess (1);
}

/* ---------------------------------------------------------------- the entry */
typedef struct { K8Launch *launch; } K8ModArg;   /* what sceKernelLoadStartModule hands module_start (copied or not: the pointer survives) */

void _start (unsigned args, void *argp) __attribute__ ((noreturn));
void _start (unsigned args, void *argp)
{
	(void) args; (void) argp;
	g_l.magic = K8_LAUNCH_MAGIC; g_l.version = K8_LAUNCH_VERSION; g_l.size = sizeof g_l;
	g_l.t_start = now_us ();
	g_l.release = release;
	g_l.wait_ms_max = 15000;
	g_l.module_uid = -1;
	env_read ();
	g_l.k9_delay = !env_is ("SHEPHERD_K9_AVC_DELAY", "0");   /* kit #9 (read before env_free) */
	g_k10_flush = k10avc_flush_mode (env_get ("SHEPHERD_K10_AVC_FLUSH"));   /* kit #10: the drain (k8_avc.h) */
	g_l.k10_flush = g_k10_flush;
	g_k10_tag = !env_is ("SHEPHERD_K10_AVC_TAG", "0");   /* kit #10 review fix (finding 5) */
	g_l.k10_tag = g_k10_tag;
	if (g_l.k9_delay && g_k10_flush == K10_FLUSH_REFEED) {   /* kit #10 review fix (finding 7): the refeed's persistent guard (k8_avc.h) */
		const char *why = 0;
		if (k10_avc_refeed_blocked (&why)) {
			g_k10_refeed_off = 1; g_l.k10_fallback = 2;
			llog ("launcher: the refeed is OFF (%s): the end-of-stream call with one picture per call (stop1) instead", why);
		}
	}
	if (g_l.k9_delay) {   /* kit #9 review fix (finding 3; round 2, finding 1: the shared, persistent guard of k8_avc.h) */
		if (env_is ("SHEPHERD_K9_AVC_DRAIN", "0"))
			g_no_drain = 2, g_no_drain_why = "SHEPHERD_K9_AVC_DRAIN=0";
		else if (k9_avc_drain_blocked (&g_no_drain_why))
			g_no_drain = 1;
		if (g_no_drain)
			llog ("launcher: the decoder's end-of-stream call is OFF (%s): the splash ends at the last picture before the held ones", g_no_drain_why);
	}
	int off = env_is ("SHEPHERD_K8_SPLASH", "0") || env_is ("SHEPHERD_K8_BOOT", "0");
	/* kit #12: the owner's boot sequence */
	/* kit #12 integration (the kit #12 defaults table, host.c k12_defaults.inc.c): ON by default in the launcher apps -- an env file line wins
	 * (SHEPHERD_K12_BOOTSEQ=0 = kit #11's boot); SHEPHERD_K12_DEFAULTS=0 = the c2 default (off). The host's table makes the same choice. */
	g_k12_seq = (env_get ("SHEPHERD_K12_BOOTSEQ") ? env_is ("SHEPHERD_K12_BOOTSEQ", "1") : !env_is ("SHEPHERD_K12_DEFAULTS", "0"))
	            && !env_is ("SHEPHERD_K12_BOOT", "0") && !env_is ("SHEPHERD_STRICT_ORIGINAL", "1");
	g_k12_xskip = g_k12_seq && !env_is ("SHEPHERD_K12_XSKIP", "0");
	g_l.k12_seq = g_k12_seq;
	g_k13_avc = g_k12_seq && !env_is ("SHEPHERD_K13_AVC", "0") && !env_is ("SHEPHERD_K13_BOOTC", "0");   /* kit #13 bootc: the second clip's decoder (above) */
	{
		const char *c2 = env_get ("SHEPHERD_K12_CLIP2_FILE");
		SceIoStat lst;
		sceClibMemset (&lst, 0, sizeof lst);
		int luma = !(c2 && *c2) && !env_is ("SHEPHERD_K13_LUMARED", "0") && sceIoGetstat (CLIP2_LUMA, &lst) >= 0;   /* kit #13 fixlooks */
		sceClibSnprintf (g_clip2_path, sizeof g_clip2_path, "%s", c2 && *c2 ? c2 : luma ? CLIP2_LUMA : CLIP2_DEFAULT);
	}
	if (g_k12_seq) {
		g_l.wait_ms_max = 30000;
		llog ("launcher: kit #12 boot sequence ON (SHEPHERD_K12_BOOTSEQ=1): the Devolver splash, then %s; Cross skips the clip on the screen%s; release () waits up to 30 s",
		      g_clip2_path, g_k12_xskip ? "" : " -- OFF (SHEPHERD_K12_XSKIP=0)");
	}
	const char *clip = env_get ("SHEPHERD_K8_SPLASH_FILE");
	static char clip_path [160];
	sceClibSnprintf (clip_path, sizeof clip_path, "%s", clip && *clip ? clip : CLIP_DEFAULT);
	if (off) {
		sceClibSnprintf (g_l.splash_why, sizeof g_l.splash_why, "SHEPHERD_K8_%s=0 (the module loads at once; no splash)", env_is ("SHEPHERD_K8_BOOT", "0") ? "BOOT" : "SPLASH");
		llog ("launcher at %u ms: splash OFF (%s)", ms (g_l.t_start), g_l.splash_why);
	} else if (splash_start (clip_path) == 0) {
		llog ("launcher at %u ms: splash '%s' started (%u frames, %u KiB CDRAM: the clip %u KiB + the decoder %u KiB + 2 frame buffers; sound %s; %s)", ms (g_l.t_start), g_l.clip,
		      g_l.frames_total, g_l.cdram_kib, ((g_clip_len + 0x3FFFFu) & ~0x3FFFFu) / 1024u, g_avc.mem_bytes / 1024u, g_l.audio_state > 0 ? "on" : g_l.audio_why,
		      g_l.k9_delay ? "kit #9: the decoder's output delay tolerated" : "SHEPHERD_K9_AVC_DELAY=0: kit #8's loop");
		if (g_k13_avc)   /* kit #13 bootc: device evidence for k8_avc.h's work-area rule (the clip block's and the area's addresses) */
			llog ("launcher: K13 clip 1: clip block 0x%08x, decoder work area 0x%08x (%s a 1 MiB boundary)", (unsigned) g_clip, (unsigned) g_avc.ctrl.frameBuf.pBuf,
			      ((unsigned) g_avc.ctrl.frameBuf.pBuf & (K13_AVC_ALIGN - 1u)) ? "NOT on" : "on");
	} else
		llog ("launcher at %u ms: NO splash: %s (%s): the module loads with a black screen", ms (g_l.t_start), g_l.splash_why, clip_path);
	env_free ();   /* (every env lookup is done: the file's memory goes back before the module and the host) */
	K8ModArg arg = { &g_l };
	g_l.t_load_begin = now_us ();
	int status = 0;
	g_l.module_uid = sceKernelLoadStartModule (MODULE_PATH, sizeof arg, &arg, 0, NULL, &status);
	g_l.module_status = status;
	g_l.t_load_end = now_us ();
	if (g_l.module_uid < 0)   /* kit #8.1: an error code is not a uid */
		llog ("launcher: " MODULE_PATH " REFUSED after %u ms (rc 0x%08x%s) -- behind the splash (its first frame at %u ms of process time)",
		      ms (g_l.t_load_end - g_l.t_load_begin), (unsigned) g_l.module_uid,
		      (unsigned) g_l.module_uid == 0x8002D003u ? " = MODULEMGR_NO_LIB: the module imports a library that is not loaded yet" : "", ms (g_l.t_first_frame));
	else
		llog ("launcher: " MODULE_PATH " loaded in %u ms (uid 0x%08x, module_start %d) -- behind the splash (its first frame at %u ms of process time)",
		      ms (g_l.t_load_end - g_l.t_load_begin), (unsigned) g_l.module_uid, status, ms (g_l.t_first_frame));
	if (g_l.module_uid < 0 || status != SCE_KERNEL_START_SUCCESS || !g_l.entry) {
		char l1 [80], l2 [80];
		sceClibSnprintf (l1, sizeof l1, "The engine module did not load (0x%08x, %d).", (unsigned) g_l.module_uid, status);
		if ((unsigned) g_l.module_uid == 0x8002D003u)   /* kit #8.1: a missing library is a build problem, not a damaged install */
			sceClibSnprintf (l2, sizeof l2, "A library it needs is not loaded (0x8002D003): send the logs.");
		else
			sceClibSnprintf (l2, sizeof l2, "Reinstall the app's VPK (" MODULE_PATH ").");
		char line [200];
		sceClibSnprintf (line, sizeof line, "FAIL K8 %s %s", l1, l2);
		log_file_lines (line);
		fail_screen (l1, l2);
	}
	int rc = g_l.entry (&g_l);   /* host.c's main: it ends the process itself (exit_process) */
	sceKernelExitProcess (rc);
	for (;;) sceKernelDelayThread (1000000);
}
