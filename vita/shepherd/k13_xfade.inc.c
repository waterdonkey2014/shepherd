/* ---------------------------------------------------------------- device kit #13, track bootc (vita/notes/kit13-bootc.md section 5)
 * THE BOOT LOADING SCREEN'S EMBLEM CROSSFADE -- the owner's design (2026-11-05): ~8 s per emblem, a 1 s crossfade, shuffled with no repeats
 * until all have shown, only the emblems the save allows. Included by k10_boot.inc.c before its drawing thread (zlib, k10bs_draw are above).
 *  THE SET: the emblem set's folder (data/video/boot/, or the boot sequence's boot-seq/) also holds XFADE.txt + one xf-<emblem>.shx per emblem
 *   + xf-check.bin (vita/tools/bootscreen_xfade.py make: owner-local). Kit #12's recordings are frame-locked -- every recording has the same
 *   frames, and at every frame the same pixels outside the emblem's box (x355-604, y147-396), while inside the box the emblem is a still from
 *   the loop start on -- so ONE recording plays as the stream (the screen, the rings, the crown, the corner icon) and the box is drawn here: the
 *   first emblem's own fade-in (its .shx intro boxes) during the stream's intro, then its still, and every `hold` pictures the last `fade` of
 *   them blend the current still into the next one, (256 - w) / 256 of the one and w / 256 of the other per channel. Outside the box nothing
 *   differs from kit #12's screen.
 *  THE STREAM (review fix): the recording this boot DREW (kit #11's draw, the file kit #12 plays) -- not XFADE.txt's 'stream' line, which names
 *   the recording make checked every other one against (make refuses a set whose recordings differ outside the box, or whose loop changes
 *   the box, so any of them serves). So whatever stops the crossfade after the file is chosen (a damaged or stale .shx, no VRAM, CHECK mode's
 *   check file) leaves exactly kit #12's screen: that boot's drawn emblem, whole -- the first build swapped in the reference recording before
 *   the stills were read, and a failure then showed the same emblem every boot.
 *  THE ORDER: the first emblem is kit #11's draw (the console's random number generator, every emblem equally likely), then the rest in a
 *   random order, then again reshuffled (never the same emblem twice in a row) -- only the emblems whose group the save allows: 'base' always
 *   (every emblem recorded so far: the base game's pentagrams and symbols); 'woolhaven' / 'coop' only when SHEPHERD_K13_XFADE_GROUPS names
 *   them (the save-based rule -- Woolhaven owned, co-op played once -- waits for those emblems' recordings; none is on the card yet).
 *  MEMORY: one VRAM block: every allowed emblem's still (compressed), the first emblem's intro boxes (compressed), the two decoded stills
 *   (250 x 250 x 4 B each) and the check list; all given back with the screen. No main RAM (review fix, as kit #10's boot screen: "no main
 *   RAM, no newlib heap"): the first build kept the decoded stills in newlib for the whole boot (VLX 00193: newlib's top peak +496 KiB, the
 *   GC heap cap 157..249 vs 158..250 MiB with the crossfade off -- the shared pool's floor follows newlib's peak in 1 MiB steps).
 *   SHEPHERD_K13_XFADE_STILLS=ram = that first build's placement (cached main RAM from newlib; the VRAM block if newlib has no room).
 *  CPU: a box is drawn only when it must change in that display buffer (the intro, a new emblem, the fade's pictures): a copy of the decoded
 *   still, or a blend (two pixels' channels at a time); the next emblem's still is inflated right after a fade, not at the next one. VRAM is
 *   uncached for the CPU, so its reads go in bursts: a blend copies 256-pixel runs of both stills into the stack (memcpy) and writes the run
 *   back, an inflate writes through a stack run (its CRC-32 is taken there, never read back from VRAM). ESTIMATE (kit #7's device probe: CPU
 *   reads of VRAM 98 MiB/s): ~5-7 ms of the low-priority thread per fade picture (30 per 8 s), ~3 ms per new emblem.
 *  CHECK (SHEPHERD_K10_BOOTSCREEN_CHECK=1, tests / vlx): every shown buffer = the stream's frame outside the box (xf-check.bin: CRC-32 of the
 *   frame with the box left out) AND the box = what the schedule wants (an intro box / a still: its CRC-32 from the .shx; a blend: the CRC-32
 *   of the blend computed again channel by channel -- a second implementation of the fast blend).
 * Switches (env file): SHEPHERD_K13_XFADE=0 (or SHEPHERD_K13_BOOTC=0) = kit #12's screen (one emblem a boot, the whole recording); a forced
 * emblem (SHEPHERD_K11_EMBLEM) is kit #12's too; SHEPHERD_K13_XFADE_HOLD / _FADE = pictures (default XFADE.txt's: 240 / 30 = 8 s / 1 s at
 * 30/s); tests: SHEPHERD_K13_XFADE_FIRST=<emblem>, SHEPHERD_K13_XFADE_SEED=<n> (a fixed order). Without XFADE.txt (or a failed read) the
 * screen is kit #12's, and one K13 line says why. SHEPHERD_K13_XFADE_NEGCTL=1 (TEST ONLY): the check's negative control. */
#define K13XF_MAX 16
#define K13XF_SEQ 128
#define K13XF_RUN 256   /* pixels per burst through the stack (VRAM is uncached for the CPU) */
typedef struct {
	int on;
	char why [160];
	char stream [64], dir [96];
	unsigned stream_ck, bx, by, bw, bh, frames, loop, hold, fade, check_ck;
	unsigned play_ck;          /* review fix: the checksum of the recording this boot plays (the drawn emblem's own: kit #12's file) */
	char play [48];            /* (its file name, for the lines) */
	int n, first;
	char name [K13XF_MAX][32], file [K13XF_MAX][48];
	unsigned set_ck [K13XF_MAX];   /* SET.txt's checksum of each emblem's recording (the .shx must be made from it) */
	unsigned char *blk; unsigned blk_len;
	unsigned char *still_z [K13XF_MAX]; unsigned still_len [K13XF_MAX], still_crc [K13XF_MAX];
	unsigned char *intro_z; unsigned intro_n, intro_off [64], intro_len [64], intro_crc [64];
	unsigned char *scratch [2]; int scratch_em [2], scratch_heap;
	unsigned *check_crc;
	int seq [K13XF_SEQ];
	int state [2][3];          /* per display buffer: the box it holds: a, b, w (state [i][0] = -1: unknown) */
	int negctl;                /* SHEPHERD_K13_XFADE_NEGCTL=1 (TEST ONLY): one byte of every blended box flipped -- the check must FAIL */
	unsigned seeded, segs_shown, fades, box_draws, blends, decodes, decode_us_max, box_us_max, check_out_bad, check_box_bad, bad_decode;
} K13Xfade;
static K13Xfade g_k13xf;

static int k13xf_env_off (void)
{
	const char *a = getenv ("SHEPHERD_K13_XFADE"), *m = getenv ("SHEPHERD_K13_BOOTC");
	return (a && !strcmp (a, "0")) || (m && !strcmp (m, "0"));
}
static unsigned k13xf_rand (void)
{
	if (g_k13xf.seeded) { g_k13xf.seeded = g_k13xf.seeded * 1103515245u + 12345u; return g_k13xf.seeded >> 8; }   /* (tests: SHEPHERD_K13_XFADE_SEED) */
	unsigned r = 0;
	if (sceKernelGetRandomNumber (&r, sizeof r) < 0) r = (unsigned) sceKernelGetProcessTimeWide () * 2654435761u;
	return r;
}
/* XFADE.txt of the set's folder (after kit #11's draw): 0 = the crossfade is on (the screen still plays the drawn recording: s->path), else
 * x->why */
static int k13xf_read (const K11Set *s, const char *dir)
{
	K13Xfade *x = &g_k13xf;
	memset (x, 0, sizeof *x);
	x->first = -1; x->scratch_em [0] = x->scratch_em [1] = -1;
	x->state [0][0] = x->state [1][0] = -1;
	snprintf (x->dir, sizeof x->dir, "%s", dir);
	if (k13xf_env_off ()) { snprintf (x->why, sizeof x->why, "SHEPHERD_K13_XFADE=0 / SHEPHERD_K13_BOOTC=0 (kit #12's screen)"); return -1; }
	const char *force = getenv ("SHEPHERD_K11_EMBLEM");
	if (force && *force) { snprintf (x->why, sizeof x->why, "SHEPHERD_K11_EMBLEM forces one emblem (kit #12's screen)"); return -1; }
	char p [128];
	snprintf (p, sizeof p, "%s/XFADE.txt", dir);
	void *data = NULL;
	unsigned size = 0;
	if (read_file (p, &data, &size) != 0 || !size || size > 8192) {
		if (data) free (data);
		snprintf (x->why, sizeof x->why, "no crossfade set in %s (vita/tools/bootscreen_xfade.py make writes XFADE.txt)", dir);
		return -1;
	}
	const char *groups = getenv ("SHEPHERD_K13_XFADE_GROUPS");
	int ver = 0, skipped = 0;
	for (char *line = (char *) data, *nl; line && *line; line = nl) {
		nl = strchr (line, '\n');
		if (nl) *nl++ = 0;
		if (*line == '#' || !*line) continue;
		char a [64], b [48], g [16];
		unsigned u1, u2, u3, u4, ck;
		if (sscanf (line, "xfade %d", &ver) == 1) continue;
		if (sscanf (line, "stream %63s %x", a, &ck) == 2) { snprintf (x->stream, sizeof x->stream, "%s", a); x->stream_ck = ck; continue; }
		if (sscanf (line, "box %u %u %u %u", &u1, &u2, &u3, &u4) == 4) { x->bx = u1; x->by = u2; x->bw = u3; x->bh = u4; continue; }
		if (sscanf (line, "frames %u loop %u", &u1, &u2) == 2) { x->frames = u1; x->loop = u2; continue; }
		if (sscanf (line, "hold %u", &u1) == 1) { x->hold = u1; continue; }
		if (sscanf (line, "fade %u", &u1) == 1) { x->fade = u1; continue; }
		if (sscanf (line, "check %47s %x", b, &ck) == 2) { x->check_ck = ck; continue; }
		if (sscanf (line, "emblem %31s %47s %15s %x", a, b, g, &ck) == 4 && x->n < K13XF_MAX) {
			if (strchr (b, '/') || strstr (b, "..")) continue;
			int allowed = !strcmp (g, "base") || (groups && strstr (groups, g));
			int si = -1;
			for (int i = 0; i < s->n; i++) if (!strcmp (s->name [i], a)) si = i;
			if (!allowed || si < 0) { skipped++; continue; }
			snprintf (x->name [x->n], sizeof x->name [0], "%s", a);
			snprintf (x->file [x->n], sizeof x->file [0], "%s", b);
			x->set_ck [x->n] = s->checksum [si];
			x->n++;
		}
	}
	free (data);
	if (ver != 1 || !x->stream [0] || !x->bw || !x->bh || x->bx + x->bw > 960u || x->by + x->bh > 544u || !x->frames || x->loop >= x->frames || x->loop > 63u || x->n < 1) {
		snprintf (x->why, sizeof x->why, "XFADE.txt is not usable (version %d, %d emblem(s) allowed, box %ux%u, loop %u)", ver, x->n, x->bw, x->bh, x->loop);
		return -1;
	}
	if (x->n < 2) { snprintf (x->why, sizeof x->why, "only one emblem allowed for this save (%d skipped): kit #12's screen", skipped); return -1; }
	int si = -1;
	for (int i = 0; i < s->n; i++) if (!strcmp (s->file [i], x->stream)) si = i;
	if (si < 0 || s->checksum [si] != x->stream_ck) { snprintf (x->why, sizeof x->why, "the crossfade set is stale (its stream %s is not the set's recording: re-run bootscreen_xfade.py make)", x->stream); return -1; }
	if (s->pick < 0 || s->pick >= s->n) { snprintf (x->why, sizeof x->why, "no emblem drawn for this boot"); return -1; }
	x->play_ck = s->checksum [s->pick];   /* review fix: the drawn recording is the stream (any of the set's serves: make checked them all) */
	snprintf (x->play, sizeof x->play, "%s", s->file [s->pick]);
	const char *e = getenv ("SHEPHERD_K13_XFADE_HOLD");
	if (e && *e) x->hold = (unsigned) strtoul (e, NULL, 10);
	e = getenv ("SHEPHERD_K13_XFADE_FADE");
	if (e && *e) x->fade = (unsigned) strtoul (e, NULL, 10);
	if (!x->hold) x->hold = 240;
	if (x->fade >= x->hold) x->fade = x->hold / 2;
	e = getenv ("SHEPHERD_K13_XFADE_NEGCTL");
	x->negctl = e && !strcmp (e, "1");
	e = getenv ("SHEPHERD_K13_XFADE_SEED");
	if (e && *e) x->seeded = (unsigned) strtoul (e, NULL, 10) | 1u;
	/* the first emblem: kit #11's draw (or the test's), then shuffled blocks of every allowed emblem, never the same one twice in a row */
	e = getenv ("SHEPHERD_K13_XFADE_FIRST");
	const char *want = e && *e ? e : s->pick >= 0 ? s->name [s->pick] : NULL;
	for (int i = 0; i < x->n; i++) if (want && !strcmp (x->name [i], want)) x->first = i;
	if (x->first < 0) x->first = (int) (k13xf_rand () % (unsigned) x->n);
	int k = 0, last = -1;
	while (k < K13XF_SEQ) {
		int blk [K13XF_MAX];
		for (int i = 0; i < x->n; i++) blk [i] = i;
		for (int i = x->n - 1; i > 0; i--) { int j = (int) (k13xf_rand () % (unsigned) (i + 1)), t = blk [i]; blk [i] = blk [j]; blk [j] = t; }
		if (!k) { for (int i = 0; i < x->n; i++) if (blk [i] == x->first) { blk [i] = blk [0]; blk [0] = x->first; } }
		else if (blk [0] == last) { int t = blk [0]; blk [0] = blk [x->n - 1]; blk [x->n - 1] = t; }
		for (int i = 0; i < x->n && k < K13XF_SEQ; i++) x->seq [k++] = blk [i];
		last = x->seq [k - 1];
	}
	x->on = 1;
	return 0;
}
static int k13xf_readat (const char *p, unsigned off, void *dst, unsigned n)
{
	SceUID fd = sceIoOpen (p, SCE_O_RDONLY, 0);
	if (fd < 0) return -1;
	int ok = sceIoLseek (fd, off, SCE_SEEK_SET) == (SceOff) off;
	unsigned got = 0;
	while (ok && got < n) {
		int r = sceIoRead (fd, (char *) dst + got, n - got > 1048576u ? 1048576u : n - got);
		if (r <= 0) break;
		got += (unsigned) r;
	}
	sceIoClose (fd);
	return ok && got == n ? 0 : -1;
}
/* after the screen's frame buffers exist: every allowed emblem's still + the first emblem's intro into one VRAM block; 0 = ready */
static int k13xf_load (const K10ShbHeader *h)
{
	K13Xfade *x = &g_k13xf;
	if (!x->on) return -1;
	if (h->frames != x->frames || h->loop_start != x->loop || h->checksum != x->play_ck) { snprintf (x->why, sizeof x->why, "the drawn recording does not match XFADE.txt (frames / loop)"); x->on = 0; return -1; }
	unsigned hdr [16], idx [64 * 3], need = 0, raw = x->bw * x->bh * 4u, lens [K13XF_MAX], offs [K13XF_MAX], nfs [K13XF_MAX], doffs [K13XF_MAX];
	for (int i = 0; i < x->n; i++) {   /* pass 1: the headers and index entries -> the sizes */
		char p [160];
		snprintf (p, sizeof p, "%s/%s", x->dir, x->file [i]);
		if (k13xf_readat (p, 0, hdr, 64) || hdr [0] != 0x31584853u || hdr [1] != 1u || hdr [2] != 64u || k10shb_fnv32 (hdr, 60) != hdr [15]
		    || hdr [3] != x->bx || hdr [4] != x->by || hdr [5] != x->bw || hdr [6] != x->bh || hdr [7] != x->loop + 1u || hdr [11] != x->set_ck [i]) {
			snprintf (x->why, sizeof x->why, "%s is not usable (header, box, frames or a recording it was not made from: re-run bootscreen_xfade.py make)", x->file [i]);
			x->on = 0; return -1;
		}
		nfs [i] = hdr [7]; doffs [i] = hdr [9];
		if (k13xf_readat (p, hdr [8], idx, nfs [i] * 12u)) { snprintf (x->why, sizeof x->why, "%s: its index did not read", x->file [i]); x->on = 0; return -1; }
		unsigned sl = nfs [i] - 1u;
		offs [i] = idx [sl * 3]; lens [i] = idx [sl * 3 + 1]; x->still_crc [i] = idx [sl * 3 + 2];
		need += (lens [i] + 15u) & ~15u;
		if (i == x->first) {
			x->intro_n = x->loop;
			for (unsigned k = 0; k < x->intro_n; k++) { x->intro_off [k] = idx [k * 3]; x->intro_len [k] = idx [k * 3 + 1]; x->intro_crc [k] = idx [k * 3 + 2]; }
			need += (idx [sl * 3] + 15u) & ~15u;   /* (the intro boxes lie before the still in the file) */
		}
	}
	/* the decoded stills: in the VRAM block (review fix: no main RAM -- newlib's peak sets the shared pool's floor); SHEPHERD_K13_XFADE_STILLS=ram
	 * = the first build's cached main RAM from newlib (the VRAM block if newlib has no room) */
	const char *sp = getenv ("SHEPHERD_K13_XFADE_STILLS");
	unsigned char *heap = sp && !strcmp (sp, "ram") ? malloc (2u * raw) : NULL;
	x->scratch_heap = heap != NULL;
	unsigned blk = need + (heap ? 0u : 2u * raw) + x->frames * 4u + 64u;
	if (!(x->blk = shepgl_vram_alloc (blk, 256))) { if (heap) free (heap); snprintf (x->why, sizeof x->why, "no VRAM for the crossfade (%u KiB)", blk / 1024u); x->on = 0; return -1; }
	x->blk_len = blk;
	unsigned at = 0;
	for (int i = 0; i < x->n; i++) {   /* pass 2: the bytes */
		char p [160];
		snprintf (p, sizeof p, "%s/%s", x->dir, x->file [i]);
		if (i == x->first) {
			x->intro_z = x->blk + at;
			if (k13xf_readat (p, doffs [i], x->intro_z, offs [i])) goto bad;
			at += (offs [i] + 15u) & ~15u;
		}
		x->still_z [i] = x->blk + at; x->still_len [i] = lens [i];
		if (k13xf_readat (p, doffs [i] + offs [i], x->still_z [i], lens [i])) goto bad;
		at += (lens [i] + 15u) & ~15u;
	}
	if (heap) { x->scratch [0] = heap; x->scratch [1] = heap + raw; }
	else { x->scratch [0] = x->blk + at; x->scratch [1] = x->blk + at + raw; at += 2u * raw; }
	x->check_crc = (unsigned *) (x->blk + at);
	if (g_k10bs.check) {   /* (tests) the stream's outside-the-box CRCs */
		char p [160];
		unsigned xh [4];
		snprintf (p, sizeof p, "%s/xf-check.bin", x->dir);
		if (k13xf_readat (p, 0, xh, 16) || xh [0] != 0x31434658u || xh [2] != x->frames || k13xf_readat (p, 16, x->check_crc, x->frames * 4u)
		    || k10shb_fnv32 (x->check_crc, x->frames * 4u) != xh [3]) { snprintf (x->why, sizeof x->why, "xf-check.bin is not usable (CHECK mode)"); goto bad0; }
	}
	return 0;
bad:
	snprintf (x->why, sizeof x->why, "an emblem file did not read whole");
bad0:
	shepgl_vram_free (x->blk); x->blk = NULL; x->on = 0;
	if (heap) free (heap);
	x->scratch [0] = x->scratch [1] = NULL;
	return -1;
}
static void k13xf_free (void)
{
	if (g_k13xf.scratch_heap && g_k13xf.scratch [0]) free (g_k13xf.scratch [0]);
	g_k13xf.scratch [0] = g_k13xf.scratch [1] = NULL; g_k13xf.scratch_heap = 0;
	if (g_k13xf.blk) { shepgl_vram_free (g_k13xf.blk); g_k13xf.blk = NULL; }
}
/* one zlib stream of the box's RGBA into dst (pitch = its row stride in bytes; the box's own rows: bw * 4); 0 = ok (zlib's Adler-32) and, unless
 * crc is ~0u, its CRC-32 is crc. Review fix: zlib writes into a run on the stack, the run is copied out (dst may be VRAM: uncached for the CPU)
 * and its CRC-32 taken from the stack -- nothing is read back from dst (the same bytes as the first build's inflate straight into dst) */
static int k13xf_inflate (const unsigned char *z, unsigned zlen, unsigned char *dst, unsigned pitch, unsigned crc)
{
	K13Xfade *x = &g_k13xf;
	unsigned char run [K13XF_RUN * 4u];
	z_stream s;
	memset (&s, 0, sizeof s);
	s.zalloc = k10bs_zalloc; s.zfree = k10bs_zfree; s.opaque = &g_k10bs;
	g_k10bs.zused = 0;
	if (inflateInit (&s) != Z_OK) return -1;
	s.next_in = (Bytef *) z; s.avail_in = zlen;
	int rc = Z_OK;
	uLong c = crc32 (0L, Z_NULL, 0);
	const unsigned rowb = x->bw * 4u;
	for (unsigned y = 0; y < x->bh && rc == Z_OK; y++) {
		unsigned char *row = dst + (size_t) y * pitch;
		for (unsigned off = 0; off < rowb && rc == Z_OK;) {
			unsigned n = rowb - off < sizeof run ? rowb - off : (unsigned) sizeof run;
			s.next_out = run; s.avail_out = n;
			while (s.avail_out && rc == Z_OK) rc = inflate (&s, Z_NO_FLUSH);
			unsigned got = n - s.avail_out;
			memcpy (row + off, run, got);
			if (crc != ~0u) c = crc32 (c, run, got);
			off += got;
			if (rc == Z_STREAM_END && (off < rowb || y + 1 != x->bh)) rc = Z_DATA_ERROR;
		}
	}
	inflateEnd (&s);
	return (rc == Z_STREAM_END || rc == Z_OK) && (crc == ~0u || (unsigned) c == crc) ? 0 : -2;
}
/* the still of emblem e decoded into scratch slot `slot` */
static int k13xf_decode (int e, int slot)
{
	K13Xfade *x = &g_k13xf;
	if (x->scratch_em [slot] == e) return 0;
	SceUInt64 t0 = sceKernelGetProcessTimeWide ();
	int r = k13xf_inflate (x->still_z [e], x->still_len [e], x->scratch [slot], x->bw * 4u, x->still_crc [e]);
	unsigned us = (unsigned) (sceKernelGetProcessTimeWide () - t0);
	if (us > x->decode_us_max) x->decode_us_max = us;
	x->decodes++;
	x->scratch_em [slot] = r ? -1 : e;
	if (r) x->bad_decode++;
	return r;
}
static int k13xf_slot (int e) { return g_k13xf.scratch_em [0] == e ? 0 : g_k13xf.scratch_em [1] == e ? 1 : -1; }
static int k13xf_touch (const K10ShbRect *r)
{
	const K13Xfade *x = &g_k13xf;
	return r->w && r->h && !(r->x >= x->bx + x->bw || r->x + r->w <= x->bx || r->y >= x->by + x->bh || r->y + r->h <= x->by);
}
/* the blend of two stills (w / 256 of b) into the box of fb: two channels at a time. Review fix: the stills may be in VRAM (uncached for the
 * CPU) -- each row goes in 256-pixel runs: both runs copied to the stack (memcpy: burst reads), blended there, the run copied back out */
static void k13xf_blend (unsigned char *fb, const unsigned *a, const unsigned *b, unsigned w)
{
	const K13Xfade *x = &g_k13xf;
	unsigned iw = 256u - w;
	unsigned ra [K13XF_RUN], rb2 [K13XF_RUN];
	for (unsigned y = 0; y < x->bh; y++) {
		unsigned char *o = fb + ((size_t) (x->by + y) * 960u + x->bx) * 4u;
		const unsigned *pa = a + (size_t) y * x->bw, *pb = b + (size_t) y * x->bw;
		for (unsigned i0 = 0; i0 < x->bw; i0 += K13XF_RUN) {
			unsigned n = x->bw - i0 < K13XF_RUN ? x->bw - i0 : K13XF_RUN;
			memcpy (ra, pa + i0, n * 4u); memcpy (rb2, pb + i0, n * 4u);
			for (unsigned i = 0; i < n; i++) {
				unsigned va = ra [i], vb = rb2 [i];
				unsigned rb = (((va & 0x00FF00FFu) * iw + (vb & 0x00FF00FFu) * w) >> 8) & 0x00FF00FFu;
				unsigned ga = ((((va >> 8) & 0x00FF00FFu) * iw + ((vb >> 8) & 0x00FF00FFu) * w)) & 0xFF00FF00u;
				ra [i] = rb | ga;
			}
			memcpy (o + i0 * 4u, ra, n * 4u);
		}
	}
}
static void k13xf_copy (unsigned char *fb, const unsigned char *s)
{
	const K13Xfade *x = &g_k13xf;
	for (unsigned y = 0; y < x->bh; y++) memcpy (fb + ((size_t) (x->by + y) * 960u + x->bx) * 4u, s + (size_t) y * x->bw * 4u, x->bw * 4u);
}
/* the CRC-32 of the box in fb (check mode) */
static unsigned k13xf_box_crc (const unsigned char *fb)
{
	const K13Xfade *x = &g_k13xf;
	uLong c = crc32 (0L, Z_NULL, 0);
	for (unsigned y = 0; y < x->bh; y++) c = crc32 (c, fb + ((size_t) (x->by + y) * 960u + x->bx) * 4u, x->bw * 4u);
	return (unsigned) c;
}
/* the CRC-32 of fb with the box left out (check mode; bootscreen_xfade.py's outside_crc) */
static unsigned k13xf_out_crc (const unsigned char *fb)
{
	const K13Xfade *x = &g_k13xf;
	uLong c = crc32 (0L, Z_NULL, 0);
	c = crc32 (c, fb, x->by * 960u * 4u);
	for (unsigned y = x->by; y < x->by + x->bh; y++) {
		c = crc32 (c, fb + (size_t) y * 3840u, x->bx * 4u);
		c = crc32 (c, fb + (size_t) y * 3840u + (x->bx + x->bw) * 4u, (960u - x->bx - x->bw) * 4u);
	}
	c = crc32 (c, fb + (size_t) (x->by + x->bh) * 3840u, (544u - x->by - x->bh) * 3840u);
	return (unsigned) c;
}
/* the CRC-32 a blend must have, computed channel by channel (check mode: a second implementation of k13xf_blend) */
static unsigned k13xf_blend_crc (const unsigned char *a, const unsigned char *b, unsigned w)
{
	const K13Xfade *x = &g_k13xf;
	unsigned char row [1024 * 4];
	uLong c = crc32 (0L, Z_NULL, 0);
	for (unsigned y = 0; y < x->bh; y++) {
		for (unsigned i = 0; i < x->bw * 4u; i++) {
			unsigned v = a [(size_t) y * x->bw * 4u + i] * (256u - w) + b [(size_t) y * x->bw * 4u + i] * w;
			row [i] = (unsigned char) (v >> 8);
		}
		c = crc32 (c, row, x->bw * 4u);
	}
	return (unsigned) c;
}
/* one shown picture: the box of display buffer `buf` (which now holds stream frame k, drawn over picture `shown` - 2) made what the schedule
 * wants; called after the stream's rectangles, before the check and the plugin names */
static void k13xf_picture (K10BootScreen *b, unsigned char *fb, int buf, unsigned k, int prev)
{
	K13Xfade *x = &g_k13xf;
	SceUInt64 t0 = sceKernelGetProcessTimeWide ();
	if (k13xf_touch (&b->r [k]) || (prev >= 0 && k13xf_touch (&b->r [prev]))) x->state [buf][0] = -1;   /* (the stream drew into the box) */
	unsigned want_crc = 0;
	int have_want = 0;
	if (!b->loops && k < x->loop) {   /* the stream's intro: the FIRST emblem's own fade-in (its intro box k) */
		if (k13xf_inflate (x->intro_z + x->intro_off [k], x->intro_len [k], fb + ((size_t) x->by * 960u + x->bx) * 4u, 3840u, b->check ? x->intro_crc [k] : ~0u)) x->bad_decode++;
		x->state [buf][0] = -1; x->box_draws++;
		want_crc = x->intro_crc [k]; have_want = 1;
	} else {
		unsigned t = b->shown - x->loop, seg = t / x->hold, u = t % x->hold;
		int ea = x->seq [seg % K13XF_SEQ], eb = x->seq [(seg + 1) % K13XF_SEQ];
		unsigned w = u < x->hold - x->fade ? 0u : (u - (x->hold - x->fade) + 1u) * 256u / (x->fade + 1u);
		if (u == 0) { x->segs_shown++; if (seg) x->fades++; }
		if (k13xf_slot (ea) < 0) k13xf_decode (ea, k13xf_slot (eb) == 0 ? 1 : 0);
		if (w && k13xf_slot (eb) < 0) k13xf_decode (eb, k13xf_slot (ea) == 0 ? 1 : 0);
		if (!w && k13xf_slot (eb) < 0 && u >= 2u) k13xf_decode (eb, k13xf_slot (ea) == 0 ? 1 : 0);   /* (the next still early: right after a fade, never at the next one) */
		int sa = k13xf_slot (ea), sb = k13xf_slot (eb);
		if (sa >= 0 && (!w || sb >= 0)) {
			if (x->state [buf][0] != ea || x->state [buf][1] != (w ? eb : -1) || x->state [buf][2] != (int) w) {
				if (!w) k13xf_copy (fb, x->scratch [sa]);
				else {
					k13xf_blend (fb, (const unsigned *) x->scratch [sa], (const unsigned *) x->scratch [sb], w); x->blends++;
					if (x->negctl) fb [((size_t) x->by * 960u + x->bx + x->bw / 2u) * 4u] ^= 1u;   /* (TEST ONLY: the check's negative control) */
				}
				x->state [buf][0] = ea; x->state [buf][1] = w ? eb : -1; x->state [buf][2] = (int) w;
				x->box_draws++;
			}
			if (b->check) { want_crc = !w ? x->still_crc [ea] : k13xf_blend_crc (x->scratch [sa], x->scratch [sb], w); have_want = 1; }
		}
	}
	unsigned us = (unsigned) (sceKernelGetProcessTimeWide () - t0);
	if (us > x->box_us_max) x->box_us_max = us;
	if (b->check) {
		if (k13xf_out_crc (fb) != x->check_crc [k]) x->check_out_bad++;
		if (!have_want || k13xf_box_crc (fb) != want_crc) x->check_box_bad++;
	}
}
static void k13xf_start_line (void)
{
	K13Xfade *x = &g_k13xf;
	char order [200];
	int o = 0;
	for (int i = 0; i < x->n && i < 8 && o < (int) sizeof order - 40; i++) o += snprintf (order + o, sizeof order - (size_t) o, "%s%s", i ? ", " : "", x->name [x->seq [i]]);
	LOG ("K13 boot screen crossfade ON: one stream (the drawn recording %s; the set checked against %s) + %d emblems' stills, the box %ux%u at %u,%u blended; first '%s' (%s), then %s ...; %u pictures each "
	     "(%.1f s), the last %u (%.1f s) a crossfade; %u KiB of VRAM (stills + the first's intro%s); SHEPHERD_K13_XFADE=0 = kit #12's one emblem",
	     x->play, x->stream, x->n, x->bw, x->bh, x->bx, x->by, x->name [x->first], getenv ("SHEPHERD_K13_XFADE_FIRST") ? "SHEPHERD_K13_XFADE_FIRST" : "kit #11's draw", order,
	     x->hold, x->hold / (double) g_k10bs.fps, x->fade, x->fade / (double) g_k10bs.fps, x->blk_len / 1024u, g_k10bs.check ? " + the check list" : "");
	const char *sp = getenv ("SHEPHERD_K13_XFADE_STILLS");
	LOG ("K13 boot screen crossfade: the two decoded stills in %s (%u KiB)", x->scratch_heap ? "main RAM (cached; SHEPHERD_K13_XFADE_STILLS=ram)"
	     : sp && !strcmp (sp, "ram") ? "the VRAM block (SHEPHERD_K13_XFADE_STILLS=ram, but newlib had no room)" : "the VRAM block (no main RAM; SHEPHERD_K13_XFADE_STILLS=ram = cached main RAM)",
	     2u * x->bw * x->bh * 4u / 1024u);
}
static void k13xf_end_line (void)
{
	K13Xfade *x = &g_k13xf;
	if (!x->on) return;
	LOG ("K13 boot screen crossfade: %u emblem(s) shown, %u crossfade(s) begun; %u box draws (%u blends), the box max %.1f ms; %u still(s) inflated, max %.1f ms%s%s",
	     x->segs_shown, x->fades, x->box_draws, x->blends, x->box_us_max / 1000.0, x->decodes, x->decode_us_max / 1000.0,
	     x->bad_decode ? " -- WARN: a box did not inflate / check (a damaged .shx: re-run bootscreen_xfade.py make)" : "",
	     !g_k10bs.check ? "" : x->check_out_bad || x->check_box_bad ? "; CHECK: SOME BUFFERS DIFFERED" : "; CHECK: every shown buffer = the stream's frame outside the box and the scheduled box inside");
	if (g_k10bs.check && (x->check_out_bad || x->check_box_bad))
		LOG ("FAIL K13 boot screen crossfade check: %u buffer(s) differed outside the box from the stream's frame, %u inside it from the scheduled box", x->check_out_bad, x->check_box_bad);
}
