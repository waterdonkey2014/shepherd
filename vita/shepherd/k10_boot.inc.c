/* SHEPHERD-VITA device kit #10, track boot (vita/notes/kit10-boot.md): THE BOOT THAT ALWAYS SHOWS SOMETHING (owner decisions 2026-10-19:
 * "the game's own loading screen while the engine finishes"; "when the pentagram moves to the title"; sound option B). Included by host.c
 * right after k8_boot.inc.c.
 *
 * Kit #9 on the owner's Vita: the launcher's logo at once, then BLACK -- the host's partition, vitaGL, Mono and the engine's boot up to the
 * engine's first frame (00193 82.7 s; the eboot apps: from the tap, 62-67 s) -- then level0's own frames. The engine's boot now shows THE
 * GAME'S OWN LOADING SCREEN:
 *  WHAT: the loading screen the game shows while it loads a scene (MMTools.MMTransition: the pentagram fading in, the crown with its rings
 *   turning a fixed step every frame) exactly as Shepherd's engine draws it, LOSSLESS: the engine's own screenshots of it, made at staging
 *   time on the Mac from the owner's own copy (vita/tools/bootscreen_record.sh + bootscreen_pack.py: the fade-in, then one full ring cycle
 *   of 180 frames that loops), stored as rectangles of what changes from frame to frame, zlib-compressed (k10_shb.h). OWNER-LOCAL:
 *   ux0:data/shepherd/data/video/BootLoading.shb, never in git, never in a VPK, never shared.
 *  SHOWN BY THE HOST, NOT BY vitaGL: right after vitaGL's init (gl_start) -- the host's main thread only waits for the Mono thread from then
 *   on -- a thread of its own (USER_1, low priority: the engine boots on USER_0, the render thread's frames win USER_1) inflates each frame's
 *   rectangles straight into one of two display frame buffers and puts it on the display with sceDisplaySetFrameBuf, 30 pictures a second.
 *   Memory: the file and the two frame buffers in vitaGL's VRAM (allocated here before any engine thread exists; ~5.3 MiB for the owner's
 *   recording), zlib's state inside the file's block -- no main RAM, no decoder, no PHYCONT; all given back at the end.
 *  THE ENGINE MEANWHILE: it boots and draws level0's frames exactly as before (its GL stream and every record unchanged: the boot screen makes
 *   no GL call), but while the boot screen holds the display vitaGL's flips do not reach it -- the GL apps link with
 *   -Wl,--wrap=sceDisplaySetFrameBuf (build.sh), so vitaGL's display callback goes through __wrap_sceDisplaySetFrameBuf below, which drops
 *   them until the boot screen ends (only flips before the engine's first frame: the game has drawn nothing yet).
 *  THE END: the game's first frame -- Runtime/K10Boot.cs calls the host after frame 1's update and before its present, so every frame the
 *   game draws reaches the display (its own black lead-in, the Massive Monster logo, the Devolver video ...); fallbacks: the game's first
 *   VideoPlayer clip, the first scene after level0, the end of the boot loop, the engine's real video opening a clip. The game's own
 *   loading-to-scene move: the pentagram and the rings go at once (the display blanked: black until vitaGL's next flip shows the game's own
 *   frame); the VRAM goes back.
 *  NEVER FLASHED (owner 2026-10-19): shown only when this console's previous boot screen (a RESULT in ux0:data/shepherd/cache/boot/,
 *   keyed by the build, the recording and the card's pack; a stale or missing entry can only show it once more or once less, never change
 *   the game) lasted at least SHEPHERD_K10_BOOTSCREEN_MIN_MS (default 1 s + one full ring cycle); a first start shows it (kit #9's boot leaves
 *   60-80 s). Once the game's frames are up it never comes back.
 *  SOUND (owner pick: option B): silent -- the game's own loading screen mutes the game ("snapshot:/mute_on_loading_screen").
 *  THE TITLE RECORDING (the next stage of the owner's design: the title without its prompt, the ring where the prompt goes, the live title
 *   swapped in) is not built in kit #10 (kit10-boot.md "Not done"): the pentagram moves to the game's own first frame, not to a title.
 * Switches (env file): SHEPHERD_K10_BOOTSCREEN=0 = kit #9 (black; nothing here runs, nothing is allocated); _FILE=<path>; _FPS=<n> (default the
 * file's 30); _MIN_MS=<ms> (0 = always shown); _CHECK=1 (tests: every shown buffer compared with the frame's hash in the file). Without the
 * file on the card or with a failed step: black, as kit #9 -- one K10 line says why. The diagnostic apps (00099 / 00011) never show it.
 * KIT #10 REVIEW FIXES (vita/notes/kit10-boot.md "Review fixes"):
 *  finding 6 -- OFF BY DEFAULT until the owner answers: today's recording shows Shepherd's WHITE-SQUARE rings (the ring images use the game's
 *   UI_UberShader, whose texture the engine does not bind yet: kit #11's GPU work), so it is not yet "the game's own loading screen" and the
 *   brief keeps a pending owner decision off: SHEPHERD_K10_BOOTSCREEN=1 shows it as it is (the owner's choice, or the gates); unset = kit #9's
 *   black. A recording with the real rings (re-made after the GPU work) is what can make it the default.
 *  finding 1 -- NEVER FLASHED keeps learning: a start that skips the screen (the last one was shorter than the minimum) still measures how long
 *   it WOULD have shown (state 3: nothing on the display, the engine still reports its first frame) and stores that, so a short value can no
 *   longer switch the screen off for good; and only an engine end (the game's first frame / content, engine frame >= 1) is stored -- a run
 *   that ended before the game drew anything (a failed start, the host's end of run) teaches nothing. The key names the pack by its own
 *   header (the TOC's FNV-1a, its offsets and counts: VitaPackFormat) instead of its size.
 *  finding 9 -- only the engine's BOOT mode ("boot ..." in the engine arguments) gets it (vitatest / gfxtest / uitest never end it), and a
 *   failed start (fail_and_wait) ends it first: the display is kit #9's black, never a loading screen over a failure.
 *  finding 10 -- the stop is answered within one row's inflate (the drawing thread checks the request between rows), whatever the CPU's
 *   uncached reads from CDRAM cost (the file, zlib's window and the frame buffers are all in vitaGL's VRAM: the ~4-9 % CPU figure assumed
 *   cached memory and is NOT a measurement -- the K10 line's draw max is the device's number).
 *  finding 11 -- vitaGL flips that reach the wrapper before the boot screen's FIRST picture (the host's own black boot frame from gl_start,
 *   whose display callback can run late on GXM's display queue thread) are counted apart: "held off" counts only flips after it. */
#ifdef SHEPHERD_GL
#include <zlib.h>
#include <psp2/display.h>
#include "k10_shb.h"
#define K10_BS_DEFAULT DIR_ "/data/video/BootLoading.shb"
#define K10_BS_CACHE DIR_ "/cache/boot"
#define K10_BS_TIMING K10_BS_CACHE "/k10-bootscreen.txt"
#define K10_FB_BYTES (2u * 1024u * 1024u)   /* 960 x 544 x 4 = 2,088,960 B per display frame buffer */
#define K10_ZMEM (64u * 1024u)              /* zlib's inflate state + its 32 KiB window, inside the file's block */
typedef struct {
	int state;                     /* 0 = off / not started, 1 = running, 2 = ended, -1 = could not start; review fix (finding 1): 3 = NOT shown
	                                  (never flashed) but measuring how long it would have shown, 4 = that measurement ended */
	const char *why;
	char why_buf [120];
	unsigned char *blk;            /* the file + zlib's memory, one VRAM block */
	unsigned len;
	const K10ShbHeader *h;
	const K10ShbRect *r;
	void *fb [2];
	unsigned zused;
	SceUID thread;
	volatile int stop, done, owns;   /* owns: vitaGL's flips are dropped (__wrap_sceDisplaySetFrameBuf) */
	volatile unsigned shown, loops, dropped, draw_us_max, check_bad;
	volatile unsigned dropped_early;   /* review fix (finding 11): flips held before the first picture (the host's own boot frame, never the game's) */
	volatile int thread_rc;
	unsigned long long t_start, t_first, t_end;   /* process time (us): started, first picture on the display, ended */
	unsigned min_ms, learned_ms, fps, check;
	unsigned learned_rec;          /* review fix (finding 1): the recording's checksum, kept for a skipped start's measurement (h is freed) */
	int end_frame;
	char end_why [96];
} K10BootScreen;
static K10BootScreen g_k10bs;

int __real_sceDisplaySetFrameBuf (const SceDisplayFrameBuf *p, SceDisplaySetBufSync sync);
int __wrap_sceDisplaySetFrameBuf (const SceDisplayFrameBuf *p, SceDisplaySetBufSync sync);
/* every sceDisplaySetFrameBuf of this module (vitaGL's display callback, the debug screen) comes here; while the boot screen holds the
 * display, vitaGL's flips are dropped (counted) -- lock-free: called on GXM's display queue thread */
int __wrap_sceDisplaySetFrameBuf (const SceDisplayFrameBuf *p, SceDisplaySetBufSync sync)
{
	if (g_k10bs.owns) {
		if (g_k10bs.shown) g_k10bs.dropped++;
		else g_k10bs.dropped_early++;   /* review fix (finding 11): before the first picture -- gl_start's black frame arriving late */
		return 0;
	}
	return __real_sceDisplaySetFrameBuf (p, sync);
}
static void k10bs_show (void *fb, SceDisplaySetBufSync sync)
{
	SceDisplayFrameBuf f;
	memset (&f, 0, sizeof f);
	f.size = sizeof f; f.base = fb; f.pitch = 960; f.pixelformat = SCE_DISPLAY_PIXELFORMAT_A8B8G8R8; f.width = 960; f.height = 544;
	__real_sceDisplaySetFrameBuf (fb ? &f : NULL, sync);
}

/* zlib's memory: a bump allocator over the tail of the file's VRAM block, reset for every rectangle (no main RAM, no newlib heap) */
static voidpf k10bs_zalloc (voidpf opaque, uInt items, uInt size)
{
	K10BootScreen *b = (K10BootScreen *) opaque;
	unsigned n = ((unsigned) items * (unsigned) size + 15u) & ~15u;
	if (b->zused + n > K10_ZMEM) return Z_NULL;
	voidpf p = b->blk + ((b->len + 15u) & ~15u) + b->zused;
	b->zused += n;
	return p;
}
static void k10bs_zfree (voidpf opaque, voidpf p) { (void) opaque; (void) p; }
/* frame k's rectangle into the display buffer fb: zlib's output goes straight into each row (pitch 960); 0 = drawn, 1 = stopped (an end
 * request came: checked between rows -- review fix, finding 10: the reply never waits for a whole rectangle's inflate from uncached CDRAM) */
static int k10bs_draw (K10BootScreen *b, unsigned k, unsigned char *fb)
{
	const K10ShbRect *r = &b->r [k];
	if (!r->w || !r->h)
		return 0;
	z_stream z;
	memset (&z, 0, sizeof z);
	z.zalloc = k10bs_zalloc; z.zfree = k10bs_zfree; z.opaque = b;
	b->zused = 0;
	if (inflateInit (&z) != Z_OK)
		return -1;
	z.next_in = (Bytef *) (b->blk + b->h->data_off + r->off);
	z.avail_in = r->len;
	int rc = Z_OK;
	for (unsigned y = 0; y < r->h && rc == Z_OK; y++) {
		if (b->stop) { inflateEnd (&z); return 1; }   /* (the buffer being drawn is not on the display: dropped half-drawn) */
		z.next_out = fb + ((size_t) (r->y + y) * 960u + r->x) * 4u;
		z.avail_out = (uInt) r->w * 4u;
		while (z.avail_out && rc == Z_OK)
			rc = inflate (&z, Z_NO_FLUSH);
		if (rc == Z_STREAM_END && (z.avail_out || y + 1 != r->h))
			rc = Z_DATA_ERROR;   /* (the stream ended before the rectangle did) */
	}
	if (rc == Z_OK) {   /* every row is full: the stream's end marker may still be unread -- it must end here, with no byte more */
		unsigned char extra;
		z.next_out = &extra; z.avail_out = 1;
		rc = inflate (&z, Z_FINISH);
		if (rc == Z_STREAM_END && z.avail_out != 1)
			rc = Z_DATA_ERROR;
	}
	inflateEnd (&z);
	return rc == Z_STREAM_END ? 0 : -2;
}

/* the timing RESULT: "<key>\t<shown ms>\n" -- key = the build, the recording's checksum, the card's pack. Review fix (finding 1): the pack by
 * its own 64-byte header (magic, version, flags, the TOC's FNV-1a, the TOC's and the payload's offsets and sizes, the entry / file / bundle /
 * string counts: tools/VitaPack PackWriter) + its size, not by its size alone -- one 64-byte read */
static void k10bs_key (char *out, size_t n, unsigned rec)
{
	unsigned pack = 0, ph = 0;
	file_size (DIR_ "/pack/shepherd.pack", &pack);
	unsigned char hdr [64];
	SceUID fd = sceIoOpen (DIR_ "/pack/shepherd.pack", SCE_O_RDONLY, 0);
	if (fd >= 0) {
		if (sceIoRead (fd, hdr, sizeof hdr) == (int) sizeof hdr) ph = k10shb_fnv32 (hdr, sizeof hdr);
		sceIoClose (fd);
	}
	snprintf (out, n, "k10bs2 %s rec %08x pack %u %08x", SHP_BUILD_ID, rec, pack, ph);
}
static unsigned k10bs_learned (unsigned rec)
{
	char key [160], buf [256];
	k10bs_key (key, sizeof key, rec);
	SceUID fd = sceIoOpen (K10_BS_TIMING, SCE_O_RDONLY, 0);
	if (fd < 0)
		return 0;   /* first start (or a cleared cache): shown */
	int r = sceIoRead (fd, buf, sizeof buf - 1);
	sceIoClose (fd);
	if (r <= 0)
		return 0;
	buf [r] = 0;
	char *tab = strchr (buf, '\t');
	size_t kl = strlen (key);
	if (!tab || (size_t) (tab - buf) != kl || strncmp (buf, key, kl))
		return 0;   /* another build / recording / pack: as a first start */
	return (unsigned) strtoul (tab + 1, NULL, 10);
}
static void k10bs_learn (unsigned rec, unsigned shown_ms)
{
	char key [160], line [200];
	k10bs_key (key, sizeof key, rec);
	int n = snprintf (line, sizeof line, "%s\t%u\n", key, shown_ms);
	sceIoMkdir (DIR_ "/cache", 0777);
	sceIoMkdir (K10_BS_CACHE, 0777);
	SceUID fd = sceIoOpen (K10_BS_TIMING ".tmp", SCE_O_WRONLY | SCE_O_CREAT | SCE_O_TRUNC, 0777);   /* temp + rename: never half a line */
	if (fd < 0)
		return;
	int w = sceIoWrite (fd, line, (SceSize) n);
	sceIoClose (fd);
	if (w == n) {
		sceIoRemove (K10_BS_TIMING);
		sceIoRename (K10_BS_TIMING ".tmp", K10_BS_TIMING);
	} else
		sceIoRemove (K10_BS_TIMING ".tmp");
}

static int k10bs_main (SceSize args, void *argp)
{
	(void) args; (void) argp;
	K10BootScreen *b = &g_k10bs;
	unsigned n = b->h->frames, L = b->h->loop_start, k = 0;
	int prev = -1;
	unsigned long long t0 = 0;
	while (!b->stop) {
		/* picture number `shown` goes into buffer shown & 1, which shows the picture before last: the previous picture's rectangle, then
		 * this one's (k10_shb.h; bootscreen_pack.py checks the same playback against the screenshots) */
		unsigned char *fb = (unsigned char *) b->fb [b->shown & 1];
		unsigned long long d0 = sceKernelGetProcessTimeWide ();
		int rc = prev >= 0 ? k10bs_draw (b, (unsigned) prev, fb) : 0;
		if (!rc) rc = k10bs_draw (b, k, fb);
		if (rc == 1) break;   /* review fix (finding 10): an end request between two rows */
		if (rc) { b->thread_rc = rc; break; }
		unsigned dus = (unsigned) (sceKernelGetProcessTimeWide () - d0);
		if (dus > b->draw_us_max) b->draw_us_max = dus;
		if (b->check && k10shb_fnv32 (fb, 960u * 544u * 4u) != b->r [k].frame_fnv)
			b->check_bad++;
		if (b->shown) {   /* picture n is due at the first picture's vblank + n / rate */
			unsigned long long due = t0 + (unsigned long long) b->shown * 1000000ull / b->fps, now = sceKernelGetProcessTimeWide ();
			while (!b->stop && due > now + 17000) {   /* (short sleeps: an end request is answered within ~20 ms) */
				unsigned long long w = due - now - 17000;
				sceKernelDelayThread ((SceUInt) (w > 20000 ? 20000 : w));
				now = sceKernelGetProcessTimeWide ();
			}
		}
		if (b->stop)
			break;
		k10bs_show (fb, SCE_DISPLAY_SETBUF_NEXTFRAME);
		sceDisplayWaitVblankStart ();   /* from here this buffer is scanned: the other one may be drawn into */
		if (!b->shown)
			b->t_first = t0 = sceKernelGetProcessTimeWide ();
		b->shown++;
		prev = (int) k;
		if (++k >= n) { k = L; b->loops++; }
	}
	/* the game's own loading-to-scene move: the pentagram and the rings go at once -- black until vitaGL's next flip (the game's frame) */
	k10bs_show (NULL, SCE_DISPLAY_SETBUF_NEXTFRAME);
	sceDisplayWaitVblankStart ();
	b->done = 1;
	sceKernelExitThread (0);
	return 0;
}

static void k10bs_free (void)
{
	K10BootScreen *b = &g_k10bs;
	for (int i = 0; i < 2; i++) if (b->fb [i]) { shepgl_vram_free (b->fb [i]); b->fb [i] = NULL; }
	if (b->blk) { shepgl_vram_free (b->blk); b->blk = NULL; }
	b->h = NULL; b->r = NULL;
}
static void k10bs_fail (const char *why, int rc)
{
	snprintf (g_k10bs.why_buf, sizeof g_k10bs.why_buf, "%s (0x%08x)", why, (unsigned) rc);
	g_k10bs.why = g_k10bs.why_buf;
	g_k10bs.state = -1;
	k10bs_free ();
	LOG ("K10 boot screen: NOT shown -- %s: black until the game's first frame, as kit #9", g_k10bs.why);
}

/* main, right after gl_start succeeded and before the Mono thread exists (VRAM is allocated with no engine thread running) */
static void k10_bootscreen_start (void)
{
	K10BootScreen *b = &g_k10bs;
	memset (b, 0, sizeof *b);
	b->thread = -1; b->end_frame = -1;
	const char *e = getenv ("SHEPHERD_K10_BOOTSCREEN");
	if (e && !strcmp (e, "0")) {
		b->why = "SHEPHERD_K10_BOOTSCREEN=0 (kit #9: black)";
		LOG ("K10 boot screen OFF (SHEPHERD_K10_BOOTSCREEN=0): black until the game's first frame, as kit #9");
		return;
	}
	if (!g_k8_bootscreen) {
		b->why = "a diagnostic app (the debug text shows: no clean boot screen)";
		LOG ("K10 boot screen: not used -- %s", b->why);
		return;
	}
	if (!(e && !strcmp (e, "1"))) {   /* review fix (finding 6): a pending owner decision stays off */
		b->why = "off by default until the owner answers (the recording shows Shepherd's white-square rings: UI_UberShader's texture is not bound yet)";
		LOG ("K10 boot screen OFF by default: the recording of the game's loading screen shows Shepherd's WHITE-SQUARE rings (the ring images use the game's "
		     "UI_UberShader, whose texture the engine does not bind yet), not the game's own -- the owner's answer is pending: SHEPHERD_K10_BOOTSCREEN=1 in the env file "
		     "shows it as it is; black until the game's first frame, as kit #9");
		return;
	}
	{   /* kit #10 integration (fidelity x boot, kit10-fidelity.md section 3): the strict original switch -- the PC shows no loading screen during
	     * its start; the host decides before Main, so it reads SHEPHERD_STRICT_ORIGINAL=1 itself (StrictOriginal.cs lists the entry too);
	     * SHEPHERD_K10_STRICT=0 = kit #9's strict list (no boot screen existed: the env file's line stands) */
		const char *so = getenv ("SHEPHERD_STRICT_ORIGINAL"), *ks = getenv ("SHEPHERD_K10_STRICT");
		if (so && !strcmp (so, "1") && !(ks && !strcmp (ks, "0"))) {
			b->why = "SHEPHERD_STRICT_ORIGINAL=1 (the PC shows no loading screen during its start)";
			LOG ("K10 boot screen: not shown -- SHEPHERD_STRICT_ORIGINAL=1: the PC original shows no loading screen during its start (SHEPHERD_K10_STRICT=0 = kit #9's strict list): black until the game's first frame, as kit #9");
			return;
		}
	}
	{   /* review fix (finding 9): only the engine's boot (the game) ends it -- another engine mode (vitatest, gfxtest, uitest ...) never would */
		const char *a = g_args_buf;
		while (*a == ' ' || *a == '\t' || *a == '\r' || *a == '\n') a++;
		if (strncmp (a, "boot", 4) || (a [4] && a [4] != ' ' && a [4] != '\t' && a [4] != '\r' && a [4] != '\n')) {
			b->why = "the engine does not run the game's boot in this start (its arguments)";
			int al = 0;
			while (a [al] && a [al] != '\r' && a [al] != '\n') al++;   /* (the args file's line, without its line end) */
			LOG ("K10 boot screen: not used -- the engine's arguments '%.*s' are not the game's boot (only BootGame ends the boot screen): black, as kit #9", al, a);
			return;
		}
	}
	const char *path = getenv ("SHEPHERD_K10_BOOTSCREEN_FILE");
	if (!path || !*path) path = K10_BS_DEFAULT;
	unsigned len = 0;
	if (file_size (path, &len) != 0 || len <= K10SHB_HEADER_SIZE) {
		b->why = "no recording of the game's loading screen on the card (data/video/BootLoading.shb: stage-owner.sh STAGE_BOOTSCREEN=1 makes it from your own copy)";
		LOG ("K10 boot screen: not shown -- %s (%s): black until the game's first frame, as kit #9", b->why, path);
		return;
	}
	SceUInt64 t0 = sceKernelGetProcessTimeWide ();
	b->len = len;
	if (!(b->blk = shepgl_vram_alloc (((len + 15u) & ~15u) + K10_ZMEM, 256))) { k10bs_fail ("no VRAM for the recording", (int) len); return; }
	SceUID fd = sceIoOpen (path, SCE_O_RDONLY, 0);
	unsigned got = 0;
	if (fd >= 0) {
		while (got < len) {
			unsigned want = len - got > 1048576u ? 1048576u : len - got;
			int r = sceIoRead (fd, b->blk + got, want);
			if (r <= 0) break;
			got += (unsigned) r;
		}
		sceIoClose (fd);
	}
	if (got != len) { k10bs_fail ("the recording did not read whole", (int) got); return; }
	const char *bad = k10shb_check (b->blk, len);
	if (bad) { k10bs_fail (bad, 0); return; }
	b->h = (const K10ShbHeader *) b->blk;
	b->r = (const K10ShbRect *) (b->blk + b->h->index_off);
	e = getenv ("SHEPHERD_K10_BOOTSCREEN_FPS");
	b->fps = e && *e ? (unsigned) strtoul (e, NULL, 10) : 0;
	if (!b->fps) b->fps = b->h->rate_num / b->h->rate_den;
	if (b->fps < 1) b->fps = 1;
	if (b->fps > 60) b->fps = 60;
	e = getenv ("SHEPHERD_K10_BOOTSCREEN_CHECK");
	b->check = e && !strcmp (e, "1");
	/* never flashed: the previous start's boot screen (a result) */
	unsigned loop_ms = (b->h->frames - b->h->loop_start) * 1000u / b->fps;
	e = getenv ("SHEPHERD_K10_BOOTSCREEN_MIN_MS");
	b->min_ms = e && *e ? (unsigned) strtoul (e, NULL, 10) : 1000u + loop_ms;
	b->learned_rec = b->h->checksum;
	b->learned_ms = k10bs_learned (b->h->checksum);
	if (b->min_ms && b->learned_ms && b->learned_ms < b->min_ms) {
		snprintf (b->why_buf, sizeof b->why_buf, "this console's last boot screen lasted %u ms, less than %u ms (never flashed)", b->learned_ms, b->min_ms);
		b->why = b->why_buf;
		k10bs_free ();
		/* review fix (finding 1): not shown, but this start still measures how long it WOULD have shown (from here to the game's first frame,
		 * which the engine reports as for a shown screen) and stores that -- a short value is replaced as soon as the boot is long again */
		b->state = 3;
		b->t_start = sceKernelGetProcessTimeWide ();
		LOG ("K10 boot screen: not shown -- %s: straight to the game's own frames, as the original; this start measures how long it would have shown "
		     "(the next start decides with that)", b->why);
		return;
	}
	for (int i = 0; i < 2; i++) {
		if (!(b->fb [i] = shepgl_vram_alloc (K10_FB_BYTES, 1u << 16))) { k10bs_fail ("no VRAM for the display frame buffers", i); return; }
		memset (b->fb [i], 0, K10_FB_BYTES);
	}
	b->owns = 1;   /* from now on vitaGL's flips do not reach the display */
	b->state = 1;
	b->t_start = sceKernelGetProcessTimeWide ();
	b->thread = sceKernelCreateThread ("shepherd-k10-boot", k10bs_main, 0xB0, 0x4000, 0, 0x20000 /* USER_1 */, NULL);
	if (b->thread < 0 || sceKernelStartThread (b->thread, 0, NULL) < 0) {
		int tr = b->thread;
		if (b->thread >= 0) sceKernelDeleteThread (b->thread);
		b->thread = -1; b->owns = 0;
		{ k10bs_fail ("no thread for the boot screen", tr); return; }
	}
	LOG ("K10 boot screen ON at %.3f s: the game's own loading screen, lossless (the engine's own screenshots of it, from your own copy: %u frames at %u/s, the loop "
	     "from frame %u = %.1f s; %u KiB), shown by the host on USER_1 until the game's first frame; %u KiB of VRAM (the recording + zlib %u + 2 frame "
	     "buffers %u), given back at its end; vitaGL's flips held off the display meanwhile; silent (the game's loading screen mutes); never flashed: %s%s "
	     "(%.0f ms to set up; SHEPHERD_K10_BOOTSCREEN=1: shown as recorded -- its rings are Shepherd's white squares until the GPU work; unset / 0 = kit #9's black)",
	     (double) b->t_start / 1e6, b->h->frames, b->fps, b->h->loop_start, loop_ms / 1000.0, len / 1024u, (((len + 15u) & ~15u) + K10_ZMEM + 2u * K10_FB_BYTES) / 1024u,
	     (len + K10_ZMEM) / 1024u, 2u * K10_FB_BYTES / 1024u,
	     !b->min_ms ? "off (SHEPHERD_K10_BOOTSCREEN_MIN_MS=0)" : b->learned_ms ? "this console's last one lasted long enough" : "a first start (no timing learned yet): shown",
	     b->check ? "; CHECK mode: every shown buffer compared with the frame's hash" : "", (double) (b->t_start - t0) / 1000.0);
}

/* the end (the engine: the game's first content; the host: the end of the run) -- frame = the engine frame, -1 = the host's own */
static void k10_bootscreen_stop (const char *why, int frame)
{
	K10BootScreen *b = &g_k10bs;
	if (b->state == 3) {   /* review fix (finding 1): not shown (never flashed) -- the measurement ends */
		b->state = 4;
		b->t_end = sceKernelGetProcessTimeWide ();
		unsigned would_ms = (unsigned) ((b->t_end - b->t_start) / 1000u);
		int learn = frame >= 1;   /* (only an engine end: the game's first frame / content) */
		snprintf (b->end_why, sizeof b->end_why, "%s", why && *why ? why : "?");
		b->end_frame = frame;
		if (learn) k10bs_learn (b->learned_rec, would_ms);
		LOG ("K10 boot screen (not shown: never flashed): the game's first content came at %.3f s (%s%s%d): it would have shown for %.1f s -- %s", (double) b->t_end / 1e6,
		     b->end_why, frame >= 0 ? ", engine frame " : "", frame >= 0 ? frame : 0, would_ms / 1000.0,
		     learn ? (would_ms >= b->min_ms ? "learned: the next start shows it" : "learned: the next start skips it again") : "NOT learned (the run ended before the game's first frame)");
		return;
	}
	if (b->state != 1)
		return;
	b->state = 2;
	snprintf (b->end_why, sizeof b->end_why, "%s", why && *why ? why : "?");
	b->end_frame = frame;
	b->stop = 1;
	SceUInt to = 2000000;   /* the thread answers within ~20 ms */
	int hung = 0;
	if (b->thread >= 0) {
		if (sceKernelWaitThreadEndCB (b->thread, NULL, &to) < 0 && !b->done)
			hung = 1;
		else
			sceKernelDeleteThread (b->thread);
		b->thread = -1;
	}
	b->owns = 0;   /* vitaGL's next flip reaches the display (black until then) */
	b->t_end = sceKernelGetProcessTimeWide ();
	unsigned shown_ms = b->t_first ? (unsigned) ((b->t_end - b->t_first) / 1000u) : 0;
	unsigned rec = b->h ? b->h->checksum : 0, frames = b->h ? b->h->frames : 0;
	if (hung) {   /* its VRAM is left to the thread (never freed in this start) */
		k10bs_show (NULL, SCE_DISPLAY_SETBUF_IMMEDIATE);
		LOG ("WARN K10 boot screen: its thread did not stop within 2 s: its VRAM is left to it; the game goes on");
	} else {
		k10bs_free ();
		if (shown_ms && frame >= 1) k10bs_learn (rec, shown_ms);   /* review fix (finding 1): only an engine end teaches the length */
	}
	if (b->thread_rc)
		LOG ("WARN K10 boot screen: a frame did not inflate (%d) after %u pictures: the screen was black from there", b->thread_rc, b->shown);
	LOG ("K10 boot screen ended at %.3f s (%s%s%d): shown from %.3f s for %.1f s -- %u pictures (%u time(s) round the %u-frame recording's loop), draw max %.1f ms; "
	     "%u vitaGL flips held off the display (+%u before its first picture: the host's own boot frame); its VRAM given back%s%s; the length %s",
	     (double) b->t_end / 1e6, b->end_why, frame >= 0 ? ", engine frame " : "", frame >= 0 ? frame : 0, (double) b->t_first / 1e6, shown_ms / 1000.0, b->shown, b->loops,
	     frames, b->draw_us_max / 1000.0, b->dropped, b->dropped_early, hung ? " (NOT: left to the hung thread)" : "",
	     !b->check ? "" : b->check_bad ? "; CHECK: SOME SHOWN BUFFERS DIFFERED FROM THE RECORDING" : "; CHECK: every shown buffer = its recorded frame",
	     hung ? "not learned (the thread hung)" : !shown_ms ? "not learned (no picture shown)" : frame >= 1 ? "learned (never flashed)" : "NOT learned (the run ended before the game's first frame)");
	if (b->check && b->check_bad)
		LOG ("FAIL K10 boot screen check: %u of %u shown buffers differed from their recorded frame's hash", b->check_bad, b->shown);
}
/* the engine (Runtime/K10Boot.cs): 1 = this host has the boot screen (| 2 while it shows; | 4 while a skipped one is measured -- review fix,
 * finding 1: the engine reports the game's first frame then too) */
int shepherd_vita_k10_api (void);
int shepherd_vita_k10_api (void) { return 1 | (g_k10bs.state == 1 ? 2 : 0) | (g_k10bs.state == 3 ? 4 : 0); }
void shepherd_vita_k10_bootscreen_end (const char *why, int frame);
void shepherd_vita_k10_bootscreen_end (const char *why, int frame) { k10_bootscreen_stop (why, frame); }
/* [0] state, [1] start ms, [2] first picture ms, [3] end ms, [4] pictures, [5] loops, [6] vitaGL flips held, [7] learned ms, [8] the launcher's
 * logo: first frame ms, [9] its end ms, [10] the launcher released ms, [11] host main ms (process time; -1 = none) */
int shepherd_vita_k10_bootscreen_info (int *out, int n);
int shepherd_vita_k10_bootscreen_info (int *out, int n)
{
	const K10BootScreen *b = &g_k10bs;
	const K8Launch *l = g_k8_launch;
	int v [12] = { b->state, (int) (b->t_start / 1000u), (int) (b->t_first / 1000u), (int) (b->t_end / 1000u), (int) b->shown, (int) b->loops, (int) b->dropped, (int) b->learned_ms,
		       l && l->t_first_frame ? (int) (l->t_first_frame / 1000u) : -1, l && l->t_splash_end ? (int) (l->t_splash_end / 1000u) : -1,
		       l && l->t_released ? (int) (l->t_released / 1000u) : -1, (int) (g_t0 / 1000u) };
	for (int i = 0; i < n && i < 12; i++) out [i] = v [i];
	return n < 12 ? n : 12;
}
#else
static void k10_bootscreen_start (void) {}
static void k10_bootscreen_stop (const char *why, int frame) { (void) why; (void) frame; }
#endif
