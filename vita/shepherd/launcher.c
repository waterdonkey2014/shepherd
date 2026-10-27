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
 * memset / memcpy are defined here. Built -nostdlib -nostartfiles; _start is the entry the kernel calls on the main thread. */
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

static int video_main (SceSize args, void *argp)
{
	(void) args; (void) argp;
	const K8ShvHeader *h = (const K8ShvHeader *) g_clip;
	unsigned long long t0 = 0;
	for (unsigned i = 0; i < h->frames; i++) {
		unsigned len;
		const unsigned char *au = k8shv_au (g_clip, i, &len);
		void *fb = g_fb [i & 1];
		unsigned long long d0 = now_us ();
		int got = 0, r = k8avc_decode (&g_avc, au, len, fb, FB_W, &got);
		unsigned dus = (unsigned) (now_us () - d0);
		if (r < 0 || got != 1) {
			sceClibSnprintf (g_l.splash_why, sizeof g_l.splash_why, "frame %u: %s (0x%08x, %d pictures)", i, r < 0 ? g_avc.why : "the decoder gave no picture",
					 (unsigned) (r < 0 ? r : 0), got);
			break;
		}
		g_l.decode_us_total += dus;
		if (dus > g_l.decode_us_max) g_l.decode_us_max = dus;
		/* frame i is due at t0 + i / rate (t0 = the first frame's vblank); it reaches the screen at the vblank after the set */
		if (i) {
			unsigned long long due = t0 + (unsigned long long) i * h->rate_den * 1000000ull / h->rate_num, n = now_us ();
			if (due > n + 17000) sceKernelDelayThread ((SceUInt) (due - n - 17000));
			else if (n > due + 17000) g_l.frames_late++;
		}
		show (fb, SCE_DISPLAY_SETBUF_NEXTFRAME);
		sceDisplayWaitVblankStart ();   /* the new buffer is scanned from here: the other one may be decoded into */
		if (!i) {
			t0 = g_l.t_first_frame = now_us ();
			if (g_astart >= 0) sceKernelSignalSema (g_astart, 1);
		}
		g_l.frames_shown++;
	}
	g_l.t_splash_end = now_us ();
	g_l.splash_state = g_l.frames_shown == h->frames ? 2 : -1;
	if (g_astart >= 0 && !g_l.t_first_frame) sceKernelSignalSema (g_astart, 1);   /* (no picture: let the audio thread end) */
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
	int off = env_is ("SHEPHERD_K8_SPLASH", "0") || env_is ("SHEPHERD_K8_BOOT", "0");
	const char *clip = env_get ("SHEPHERD_K8_SPLASH_FILE");
	static char clip_path [160];
	sceClibSnprintf (clip_path, sizeof clip_path, "%s", clip && *clip ? clip : CLIP_DEFAULT);
	if (off) {
		sceClibSnprintf (g_l.splash_why, sizeof g_l.splash_why, "SHEPHERD_K8_%s=0 (the module loads at once; no splash)", env_is ("SHEPHERD_K8_BOOT", "0") ? "BOOT" : "SPLASH");
		llog ("launcher at %u ms: splash OFF (%s)", ms (g_l.t_start), g_l.splash_why);
	} else if (splash_start (clip_path) == 0)
		llog ("launcher at %u ms: splash '%s' started (%u frames, %u KiB CDRAM: the clip %u KiB + the decoder %u KiB + 2 frame buffers; sound %s)", ms (g_l.t_start), g_l.clip,
		      g_l.frames_total, g_l.cdram_kib, ((g_clip_len + 0x3FFFFu) & ~0x3FFFFu) / 1024u, g_avc.mem_bytes / 1024u, g_l.audio_state > 0 ? "on" : g_l.audio_why);
	else
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
