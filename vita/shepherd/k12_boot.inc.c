/* SHEPHERD-VITA device kit #12, track boot (vita/notes/kit12-boot.md): the host half of THE BOOT SEQUENCE THE OWNER'S WAY and of the RECOMMENDED
 * PLUGINS on the loading screen. Included by host.c after k11_device.inc.c and before k10_boot.inc.c (which calls the hooks below).
 * SHEPHERD_K12_BOOT=0 = every kit #12 boot row off (kit #11's host; one K12 line says so). Every row here is OFF by default in this kit (it was
 * built before kit #11's device run: kit12-boot.md "Waits on the kit #11 run").
 *
 * THE BOOT SEQUENCE (SHEPHERD_K12_BOOTSEQ=1; owner decisions 2026-10-29 / 30): the launcher plays the Devolver splash, then the Massive Monster clip
 *  (launcher.c, both with their sound; Cross skips the one on the screen); then THIS host's loading screen (k10_boot.inc.c: the game's own loading
 *  screen, recorded) holds the display until the Main Menu -- from the boot-seq emblem set when the card has one (data/video/boot-seq/: the same
 *  recordings with the controller notice's icon in the bottom-right corner, the ring's mirror: vita/tools/bootscreen_compose.py), else the boot
 *  set -- and the engine skips the game's own splash (Runtime/K12BootSeq.cs), so nothing plays twice. Decided once, when the loading screen starts
 *  (after the launcher released): the launcher must have shown BOTH clips (played or skipped), and SHEPHERD_K10_BOOTSCREEN must not be 0 -- else
 *  this start is kit #11's (the K12 line says why) and the engine plays the game's own splash. In the sequence the loading screen is asked for
 *  (as SHEPHERD_K10_BOOTSCREEN=1) and never "never flashed": it is the sequence's last piece. The eboot apps have no launcher: kit #11's boot.
 *  The strict original (SHEPHERD_STRICT_ORIGINAL=1) keeps the game's own order.
 *
 * RECOMMENDED PLUGINS (SHEPHERD_K12_PLUGINS=1; owner 2026-10-30: names only, only the MISSING ones, Wi-Fi Turbo labelled "for online play"):
 *  detected READ-ONLY at the loading screen's start -- taiHEN's config (ux0:tai/config.txt when it exists, which taiHEN reads first, else
 *  ur0:tai/config.txt: a non-comment line naming the plugin's file), the 4th core (host.c's C3 probe: CPU3 open = CapUnlocker's effect), the ARM
 *  clock (the system's report, or kit #7's measured step once it settled: >= 480 MHz = a 500 MHz profile) -- CapUnlocker (capunlocker in the
 *  config, or CPU3 open), PSVshellPlus (psvshellplus in the config, or a 500 MHz clock), Wi-Fi Turbo (wifiturbo in the config). The names are
 *  drawn bottom centre over the loading screen, between the ring and the corner icon, as pictures of the loading text's own font made on the Mac
 *  (data/video/boot/plugins.shp: vita/tools/plugins_text.sh; owner-local, the game's font) -- nothing is drawn without that file (one line).
 *  shepherd_vita_k12_plugins () answers the same detection, recomputed at the call (menusb's PS Vita Settings info row; kit12-boot.md "For
 *  other tracks"). Nothing is written anywhere. */

static int g_k12_master = 1;          /* SHEPHERD_K12_BOOT */
static int g_k12_seq_asked;           /* SHEPHERD_K12_BOOTSEQ=1 */
static int g_k12_seq;                 /* the sequence holds the loading screen in this start (decided at its start) */
static char g_k12_seq_why [200];
static int g_k12_plugins_asked;       /* SHEPHERD_K12_PLUGINS=1 */

static void k12_boot_config (void)
{
	const char *m = getenv ("SHEPHERD_K12_BOOT");
	g_k12_master = !(m && !strcmp (m, "0"));
	const char *s = getenv ("SHEPHERD_K12_BOOTSEQ"), *p = getenv ("SHEPHERD_K12_PLUGINS");
	g_k12_seq_asked = g_k12_master && s && !strcmp (s, "1");
	g_k12_plugins_asked = g_k12_master && p && !strcmp (p, "1");
	if (!g_k12_master)
		LOG ("K12 boot rows OFF (SHEPHERD_K12_BOOT=0): kit #11's boot -- no boot sequence, no plugin names, the engine's kit #12 boot rows off");
	else if (g_k12_seq_asked || g_k12_plugins_asked || getenv ("SHEPHERD_K12_STENCIL") || getenv ("SHEPHERD_K12_LOOKS"))
		LOG ("K12 boot rows: boot sequence %s; plugin names on the loading screen %s; stencil masks %s; the loading screen's own looks %s (each opt-in in this kit; SHEPHERD_K12_BOOT=0 = kit #11)",
		     g_k12_seq_asked ? "asked (SHEPHERD_K12_BOOTSEQ=1: decided when the loading screen starts)" : "off", g_k12_plugins_asked ? "ON" : "off",
		     getenv ("SHEPHERD_K12_STENCIL") ? getenv ("SHEPHERD_K12_STENCIL") : "off", getenv ("SHEPHERD_K12_LOOKS") ? getenv ("SHEPHERD_K12_LOOKS") : "off");
}

#ifdef SHEPHERD_GL   /* (the loading screen, the launcher and the plugin names are the GL apps': the headless 00011 has none of them) */
#include <zlib.h>
#include <psp2/power.h>
/* ---------------------------------------------------------------- recommended plugins: detection (read-only) */
enum { K12P_CAPUNLOCKER, K12P_PSVSHELLPLUS, K12P_WIFITURBO, K12P_N };
static const char *const k12p_name [K12P_N] = { "CapUnlocker", "PSVshellPlus", "Wi-Fi Turbo" };
static const char *const k12p_key [K12P_N] = { "capunlocker", "psvshellplus", "wifiturbo" };
static int g_k12p_cfg [K12P_N];      /* named in taiHEN's config */
static char g_k12p_cfgfile [32];     /* the config taiHEN reads ("" = none readable) */
static int g_k12p_read;
static int k8_clk_shown (int reported);   /* (k8_play.inc.c, later in host.c: the measured clock once settled, else the report) */
static int k12_ci_has (const char *line, const char *key)   /* case-insensitive substring */
{
	size_t kl = strlen (key);
	for (const char *p = line; *p; p++) {
		size_t i = 0;
		while (i < kl && p [i] && (p [i] | 0x20) == key [i]) i++;
		if (i == kl) return 1;
	}
	return 0;
}
#define K12P_CFG_MAX (16 * 1024)
static void k12_plugins_read (void)
{
	memset (g_k12p_cfg, 0, sizeof g_k12p_cfg);
	g_k12p_cfgfile [0] = 0;
	static const char *const cfgs [] = { "ux0:tai/config.txt", "ur0:tai/config.txt" };   /* (taiHEN reads ux0's when it exists) */
	char *buf = NULL;   /* (heap, only while reading: a static buffer cost 16 KiB of every GL eboot's span) */
	for (unsigned c = 0; c < 2; c++) {
		SceUID fd = sceIoOpen (cfgs [c], SCE_O_RDONLY, 0);
		if (fd < 0) continue;
		if (!buf && !(buf = (char *) malloc (K12P_CFG_MAX))) { sceIoClose (fd); break; }
		int n = sceIoRead (fd, buf, K12P_CFG_MAX - 1);
		sceIoClose (fd);
		if (n < 0) n = 0;
		buf [n] = 0;
		snprintf (g_k12p_cfgfile, sizeof g_k12p_cfgfile, "%s", cfgs [c]);
		for (char *line = buf; line && *line; ) {
			char *nl = strchr (line, '\n');
			if (nl) *nl = 0;
			while (*line == ' ' || *line == '\t') line++;
			if (*line && *line != '#' && *line != '*')
				for (int k = 0; k < K12P_N; k++) if (k12_ci_has (line, k12p_key [k])) g_k12p_cfg [k] = 1;
			line = nl ? nl + 1 : NULL;
		}
		break;   /* (the first readable one is taiHEN's) */
	}
	free (buf);
	g_k12p_read = 1;   /* (set last: a second caller meanwhile reads again, the same answer) */
}
/* per plugin: 1 = present, 0 = missing; *how: what decided it (static text) */
static int k12_plugin_present (int k, const char **how)
{
	if (!g_k12p_read) k12_plugins_read ();
	int rep = scePowerGetArmClockFrequency (), mhz = k8_clk_shown (rep);
	switch (k) {
	case K12P_CAPUNLOCKER:
		if (g_k12p_cfg [k]) { *how = "named in taiHEN's config"; return 1; }
		if (g_core3 > 0) { *how = "the 4th core (CPU3) is open to this app"; return 1; }
		*how = g_core3 < 0 ? "not in taiHEN's config; the 4th core not probed in this app" : "not in taiHEN's config and the 4th core is closed"; return 0;
	case K12P_PSVSHELLPLUS:
		if (g_k12p_cfg [k]) { *how = "named in taiHEN's config"; return 1; }
		if (mhz >= 480) { *how = "the ARM clock runs at a 500 MHz step"; return 1; }
		*how = "not in taiHEN's config and the ARM clock is below 480 MHz"; return 0;
	default:
		if (g_k12p_cfg [k]) { *how = "named in taiHEN's config"; return 1; }
		*how = "not in taiHEN's config"; return 0;
	}
}
/* the engine (Runtime/K12PluginDetect.cs -> menusb's PS Vita Settings info row, K12Plugins.Set): out [k] = 1 present / 0 missing for k = CapUnlocker, PSVshellPlus,
 * Wi-Fi Turbo; out [3] = the measured / reported ARM clock (MHz), out [4] = the 4th core (1 open, 0 closed, -1 not probed), out [5] = 1 when a
 * taiHEN config was readable. Recomputed at every call (the config is read once a start). Returns the count written. */
int shepherd_vita_k12_plugins (int *out, int n);
int shepherd_vita_k12_plugins (int *out, int n)
{
	const char *how;
	int v [6];
	for (int k = 0; k < K12P_N; k++) v [k] = k12_plugin_present (k, &how);
	int rep = scePowerGetArmClockFrequency ();
	v [3] = k8_clk_shown (rep); v [4] = g_core3 > 0 ? 1 : g_core3 == 0 ? 0 : -1; v [5] = g_k12p_cfgfile [0] != 0;
	for (int i = 0; i < n && i < 6; i++) out [i] = v [i];
	return n < 6 ? n : 6;
}

/* ---------------------------------------------------------------- the plugin names drawn over the loading screen */
/* data/video/boot/plugins.shp (vita/tools/plugins_text.sh, owner-local: the game's font): "SHP1" header {magic, version 1, count, checksum = FNV-1a
 * 32 of the rest}, then per line {id (K12P_*), w, h, zlib length} + the zlib'd RGBA (straight alpha) of the line as the loading text draws it */
#define K12_SHP_FILE DIR_ "/data/video/boot/plugins.shp"
#define K12_SHP_MAX (512u * 1024u)
typedef struct { int on; unsigned w [K12P_N], h [K12P_N]; unsigned char *px [K12P_N]; int show [K12P_N]; int x [K12P_N], y [K12P_N]; char why [120];
                 unsigned char *under [2] [K12P_N]; int saved [2]; } K12Names;   /* under: each buffer's pixels beneath the names (restored before its next picture) */
static K12Names g_k12n;
/* the loading screen's start (k10_bootscreen_start): which names are missing, their pictures inflated into main RAM (a few KiB) */
static void k12_names_prepare (void)
{
	K12Names *n = &g_k12n;
	memset (n, 0, sizeof *n);
	if (!g_k12_plugins_asked) return;
	const char *how [K12P_N];
	int missing = 0;
	for (int k = 0; k < K12P_N; k++) { n->show [k] = !k12_plugin_present (k, &how [k]); missing += n->show [k]; }
	LOG ("K12 plugins (read-only: %s; the 4th core %s; ARM clock %d MHz): CapUnlocker %s (%s), PSVshellPlus %s (%s), Wi-Fi Turbo %s (%s) -- %d name(s) for the loading screen",
	     g_k12p_cfgfile [0] ? g_k12p_cfgfile : "no taiHEN config readable", g_core3 > 0 ? "open" : g_core3 == 0 ? "closed" : "not probed", k8_clk_shown (scePowerGetArmClockFrequency ()),
	     n->show [0] ? "MISSING" : "present", how [0], n->show [1] ? "MISSING" : "present", how [1], n->show [2] ? "MISSING (for online play)" : "present", how [2], missing);
	if (!missing) return;
	void *data = NULL;
	unsigned size = 0;
	if (read_file (K12_SHP_FILE, &data, &size) != 0 || size < 16 || size > K12_SHP_MAX) {
		snprintf (n->why, sizeof n->why, "no plugin-name pictures on the card (" K12_SHP_FILE ": vita/tools/plugins_text.sh)");
		LOG ("K12 plugins: names NOT drawn -- %s", n->why);
		free (data);
		return;
	}
	const unsigned *h = (const unsigned *) data;
	if (h [0] != 0x31504853u || h [1] != 1u || h [3] != k10shb_fnv32 ((const unsigned char *) data + 16, size - 16)) {
		snprintf (n->why, sizeof n->why, "the plugin-name pictures are damaged or another version");
		LOG ("K12 plugins: names NOT drawn -- %s", n->why);
		free (data);
		return;
	}
	unsigned off = 16, total_h = 0;
	for (unsigned i = 0; i < h [2] && off + 16 <= size; i++) {
		const unsigned *e = (const unsigned *) ((const unsigned char *) data + off);
		unsigned id = e [0], w = e [1], hh = e [2], zl = e [3];
		off += 16;
		if (off + zl > size || id >= K12P_N || !w || !hh || w > 960 || hh > 64) break;
		if (n->show [id] && !n->px [id]) {
			uLongf raw = (uLongf) w * hh * 4u;
			unsigned char *px = (unsigned char *) malloc (raw), *u0 = (unsigned char *) malloc (raw), *u1 = (unsigned char *) malloc (raw);
			if (px && u0 && u1 && uncompress (px, &raw, (const Bytef *) data + off, zl) == Z_OK && raw == (uLongf) w * hh * 4u) {
				n->px [id] = px; n->under [0] [id] = u0; n->under [1] [id] = u1; n->w [id] = w; n->h [id] = hh; total_h += hh;
			} else { free (px); free (u0); free (u1); }
		}
		off += zl;
	}
	free (data);
	/* bottom centre, between the ring (bottom-left) and the corner icon (bottom-right): one line each, stacked up from the ring's baseline */
	int y = 544 - 11 - (int) total_h;
	for (int k = 0; k < K12P_N; k++) {
		if (!n->show [k] || !n->px [k]) continue;
		n->x [k] = (960 - (int) n->w [k]) / 2; n->y [k] = y; y += (int) n->h [k];
		n->on = 1;
	}
	LOG ("K12 plugins: %s", n->on ? "the missing names drawn bottom centre over the loading screen (the loading text's own font)" : "no picture for the missing names in the card's file");
}
/* the boot screen's thread, before the next picture's rectangles go into buffer bi: the pixels the names covered in it put back, so the buffer
 * holds the recording's own pixels again (the rectangles redraw only what changed; the CHECK mode's hash then compares the recording exactly) */
static void k12_names_restore (unsigned char *fb, unsigned bi)
{
	K12Names *n = &g_k12n;
	if (!n->on || !n->saved [bi & 1]) return;
	for (int k = 0; k < K12P_N; k++) {
		if (!n->show [k] || !n->px [k]) continue;
		for (unsigned yy = 0; yy < n->h [k]; yy++)
			memcpy (fb + ((size_t) (n->y [k] + (int) yy) * 960u + (unsigned) n->x [k]) * 4u, n->under [bi & 1] [k] + (size_t) yy * n->w [k] * 4u, n->w [k] * 4u);
	}
	n->saved [bi & 1] = 0;
}
/* one picture of the loading screen (the boot screen's thread, after its rectangles went into buffer bi and the check): the pixels beneath kept,
 * then the names over them */
static void k12_names_draw (unsigned char *fb, unsigned bi)
{
	K12Names *n = &g_k12n;
	if (!n->on) return;
	for (int k = 0; k < K12P_N; k++) {
		if (!n->show [k] || !n->px [k]) continue;
		for (unsigned yy = 0; yy < n->h [k]; yy++)
			memcpy (n->under [bi & 1] [k] + (size_t) yy * n->w [k] * 4u, fb + ((size_t) (n->y [k] + (int) yy) * 960u + (unsigned) n->x [k]) * 4u, n->w [k] * 4u);
	}
	n->saved [bi & 1] = 1;
	for (int k = 0; k < K12P_N; k++) {
		if (!n->show [k] || !n->px [k]) continue;
		const unsigned char *s = n->px [k];
		for (unsigned yy = 0; yy < n->h [k]; yy++) {
			unsigned char *d = fb + ((size_t) (n->y [k] + (int) yy) * 960u + (unsigned) n->x [k]) * 4u;
			for (unsigned xx = 0; xx < n->w [k]; xx++, s += 4, d += 4) {
				/* over the screen's black (vita/tools/plugins_text.sh checks the text's box is black in its reference picture): the name over black;
				 * the pixels beneath are put back before the buffer's next picture (k12_names_restore), so nothing builds up */
				unsigned a = s [3];
				d [0] = (unsigned char) ((s [0] * a + 127u) / 255u);
				d [1] = (unsigned char) ((s [1] * a + 127u) / 255u);
				d [2] = (unsigned char) ((s [2] * a + 127u) / 255u);
			}
		}
	}
}

/* ---------------------------------------------------------------- the boot sequence: decided when the loading screen starts */
#define K12_SEQ_SET_DIR DIR_ "/data/video/boot-seq"
/* k10_bootscreen_start, first: 1 = the sequence holds the loading screen in this start (the screen is asked for, from the boot-seq set if any) */
static int k12_seq_decide (void)
{
	g_k12_seq = 0;
	if (!g_k12_seq_asked) return 0;
	const K8Launch *l = g_k8_launch;
	const char *bs = getenv ("SHEPHERD_K10_BOOTSCREEN"), *so = getenv ("SHEPHERD_STRICT_ORIGINAL");
	if (so && !strcmp (so, "1")) snprintf (g_k12_seq_why, sizeof g_k12_seq_why, "SHEPHERD_STRICT_ORIGINAL=1 keeps the game's own order");
	else if (!l) snprintf (g_k12_seq_why, sizeof g_k12_seq_why, "no launcher in this app (an eboot app: kit #11's boot)");
	else if (!l->k12_seq) snprintf (g_k12_seq_why, sizeof g_k12_seq_why, "the launcher did not run the sequence (its K8 L lines say why)");
	else if (l->k12_clip2_state != 2 && l->k12_clip2_state != 3) snprintf (g_k12_seq_why, sizeof g_k12_seq_why, "the Massive Monster clip did not play (%s)", l->k12_clip2_why [0] ? l->k12_clip2_why : "?");
	else if (bs && !strcmp (bs, "0")) snprintf (g_k12_seq_why, sizeof g_k12_seq_why, "SHEPHERD_K10_BOOTSCREEN=0: no loading screen to hold");
	else if (!g_k8_bootscreen) snprintf (g_k12_seq_why, sizeof g_k12_seq_why, "a diagnostic app (no clean loading screen)");
	else g_k12_seq = 1;
	/* review fix (kit #12 finding 2): the sequence holds the screen only from a set whose every recording passed the rings check ('rings drawn':
	 * the boot-seq set, else the boot emblem set) -- never kit #11's 'rings white' set or kit #10's single recording (Shepherd's white-square
	 * rings, opt-in since kit #10); SHEPHERD_K10_BOOTSCREEN=1 in the env file still shows the card's screen as it is (asked for). Else this start
	 * is kit #11's boot (the engine plays the game's own splash) */
	int seqset = 0;
	if (g_k12_seq && !(bs && !strcmp (bs, "1"))) {
		const char *bf = getenv ("SHEPHERD_K10_BOOTSCREEN_FILE");
		if (bf && *bf) { g_k12_seq = 0; snprintf (g_k12_seq_why, sizeof g_k12_seq_why, "SHEPHERD_K10_BOOTSCREEN_FILE names one recording (no set, no rings check): shown only with SHEPHERD_K10_BOOTSCREEN=1"); }
		else {   /* (g_k11set as the probe: k10_bootscreen_start reads it again from the folder chosen here -- no new memory) */
			const char *why = NULL;
			g_k11_set_dir = K12_SEQ_SET_DIR;
			if (k11_set_read (&g_k11set) == 0 && g_k11set.rings_drawn) seqset = 1;
			else {
				if (g_k11set.present) why = "boot-seq set 'rings white'";
				g_k11_set_dir = K11_SET_DIR;
				if (k11_set_read (&g_k11set) != 0) why = why ? why : g_k11set.why;
				else if (!g_k11set.rings_drawn) why = "boot set 'rings white': white-square rings";
				else why = NULL;
				if (why) {   /* (the line fits g_k12_seq_why's 200 bytes: the review's vlx run cut the first wording) */
					g_k12_seq = 0;
					snprintf (g_k12_seq_why, sizeof g_k12_seq_why, "no set marked 'rings drawn' on the card (%.70s); SHEPHERD_K10_BOOTSCREEN=1 shows the screen as it is", why);
				}
			}
		}
	} else if (g_k12_seq) {   /* (SHEPHERD_K10_BOOTSCREEN=1: the boot-seq set when the card has one, as before) */
		void *d = NULL; unsigned sz = 0;
		seqset = read_file (K12_SEQ_SET_DIR "/SET.txt", &d, &sz) == 0 && sz > 0;
		free (d);
		if (seqset) g_k11_set_dir = K12_SEQ_SET_DIR;
	}
	if (g_k12_seq) {
		LOG ("K12 boot sequence ON: the launcher showed the Devolver splash (%u of %u pictures%s) and the Massive Monster clip (%u of %u%s); the loading screen holds the display "
		     "until the Main Menu from %s; the engine skips the game's own splash (Runtime/K12BootSeq.cs)",
		     l->frames_shown, l->frames_total, l->k12_skip1 ? ", Cross skipped it" : "", l->k12_clip2_shown, l->k12_clip2_total, l->k12_clip2_state == 3 ? ", Cross skipped it" : "",
		     seqset ? "the boot-seq emblem set (the controller icon in the corner)" : bs && !strcmp (bs, "1") ? "the card's boot screen (SHEPHERD_K10_BOOTSCREEN=1: as it is)"
		     : "the boot emblem set, rings drawn (no usable data/video/boot-seq/ on the card: no corner icon)");
	} else
		LOG ("K12 boot sequence NOT in this start: %s -- kit #11's boot (the game's own splash)", g_k12_seq_why);
	return g_k12_seq;
}
/* (shepherd_vita_k12_api, the engine's question, is at the end of k10_boot.inc.c: it reads the loading screen's state) */

/* ---------------------------------------------------------------- the decoder checks, device side (kit11-device.md section 6's design)
 * SHEPHERD_K12_DECCHECK=video (test-only, the kit's optional step D; unset: nothing runs, nothing is allocated): right after the loading screen
 * starts and BEFORE the Mono thread exists (the game's frames and records are untouched; the boot takes the check's time longer), every clip of
 * data/video/ (the Devolver splash, the intro, the Massive Monster clip when staged) is decoded once more by the hardware decoder in its YUV
 * 4:2:0 output (SCE_AVCDEC_PIXELFORMAT_YUV420_RASTER: the standard's own pictures -- H.264 decoding is bit-exact by the standard; the players'
 * RGBA is the decoder's colour conversion, not part of it), each picture hashed (FNV-1a 64 of the luma plane, and of the chroma that follows it)
 * and compared with the Mac's reference, data/video/<clip>.decref.tsv (vita/tools/deccheck_ref.py: ffmpeg's decode of the same .shv, the chroma
 * hashed both as I420 planes and as NV12 -- the line names the layout the Vita wrote). The drain: the clip's own access units fed again after
 * its last (kit #10's refeed) bring out the held pictures; skipped when kit #10's refeed guard file is on the card. Lines: K12 DEC ...;
 * per-picture results in <logs>/dec-video-<clip>.tsv (results only). The AAC half (sceAudiodec through shepaudio) is not built here: shepaudio is
 * outside this track (kit12-boot.md "Needs the owner"). The decoder's library: the one reserved before vitaGL (kit #9) when it is up, else its
 * own (initialised and ended here). */
static unsigned long long k12_fnv64 (const unsigned char *p, unsigned n)
{
	unsigned long long h = 14695981039346656037ull;
	for (unsigned i = 0; i < n; i++) { h ^= p [i]; h *= 1099511628211ull; }
	return h;
}
typedef struct { unsigned n; unsigned long long *y, *ci, *cn; } K12DecRef;
/* <clip>.decref.tsv: "# ..." lines, then per picture "<n>\t<Y 16 hex>\t<chroma I420 16 hex>\t<chroma NV12 16 hex>" */
static int k12_decref_read (const char *path, K12DecRef *r)
{
	memset (r, 0, sizeof *r);
	void *d = NULL; unsigned sz = 0;
	if (read_file (path, &d, &sz) != 0 || !sz) { free (d); return -1; }
	unsigned cap = 0;
	for (unsigned i = 0; i < sz; i++) if (((char *) d) [i] == '\n') cap++;
	r->y = calloc (cap + 1, 8); r->ci = calloc (cap + 1, 8); r->cn = calloc (cap + 1, 8);
	if (!r->y || !r->ci || !r->cn) { free (d); return -1; }
	for (char *line = (char *) d, *nl; line && *line; line = nl) {
		nl = strchr (line, '\n'); if (nl) *nl++ = 0;
		unsigned k; unsigned long long a, b, c;
		if (*line == '#' || sscanf (line, "%u\t%llx\t%llx\t%llx", &k, &a, &b, &c) != 4 || k != r->n || r->n >= cap + 1) continue;
		r->y [r->n] = a; r->ci [r->n] = b; r->cn [r->n] = c; r->n++;
	}
	free (d);
	return 0;
}
static void k12_deccheck_clip (const char *name)
{
	char path [160], refp [176], outp [192];
	snprintf (path, sizeof path, DIR_ "/data/video/%s.shv", name);
	snprintf (refp, sizeof refp, DIR_ "/data/video/%s.decref.tsv", name);
	snprintf (outp, sizeof outp, "%s/dec-video-%s.tsv", logs_dir (), name);
	unsigned len = 0;
	if (file_size (path, &len) != 0 || len <= K8SHV_HEADER_SIZE) return;   /* (not on the card: nothing to check) */
	unsigned char *clip = shepgl_vram_alloc ((len + 15u) & ~15u, 256);
	if (!clip) { LOG ("WARN K12 DEC video '%s': no VRAM for the clip (%u KiB) -- the check did not run (the players are unaffected)", name, len / 1024u); return; }
	void *buf = NULL; unsigned got = 0;
	if (read_file (path, &buf, &got) == 0 && got == len) memcpy (clip, buf, len);
	free (buf);
	const char *bad = got == len ? k8shv_check (clip, len) : "the clip did not read whole";
	if (bad) { LOG ("WARN K12 DEC video '%s': %s -- not checked", name, bad); shepgl_vram_free (clip); return; }
	const K8ShvHeader *h = (const K8ShvHeader *) clip;
	unsigned W = h->width, H = h->height, ysz = W * H, csz = ysz / 2;
	K12DecRef ref; int have_ref = k12_decref_read (refp, &ref) == 0 && ref.n;
	K8Avc d;
	int shared = g_k9_reserved && g_k9_lib.w == W && g_k9_lib.h == H;
	int need = k8avc_prepare_ex (&d, W, H, 3, shared);
	void *work = need > 0 ? shepgl_vram_alloc ((unsigned) need, 1u << 20) : NULL;
	unsigned char *pic = shepgl_vram_alloc ((ysz + csz + 0xFFFu) & ~0xFFFu, 256);
	if (need > 0 && work && pic && shared) k9_pad_free ("the decoder check's sceAvcdecCreateDecoder (kit #12 step D)");
	int rc = need > 0 && work && pic ? k8avc_create (&d, work, (unsigned) need) : need;
	if (rc < 0 || !work || !pic) {
		LOG ("WARN K12 DEC video '%s': %s (0x%08x) -- the check did not run (the players are unaffected)", name, d.why ? d.why : !work || !pic ? "no VRAM for the decoder" : "?", (unsigned) rc);
		k8avc_close (&d);
		if (work) shepgl_vram_free (work);
		if (pic) shepgl_vram_free (pic);
		shepgl_vram_free (clip); free (ref.y); free (ref.ci); free (ref.cn);
		return;
	}
	SceUID fo = sceIoOpen (outp, SCE_O_WRONLY | SCE_O_CREAT | SCE_O_TRUNC, 0777);
	char line [160];
	int n0 = snprintf (line, sizeof line, "# picture\tY\tchroma\tresult (kit #12 step D: %s, %ux%u YUV 4:2:0 raster)\n", name, W, H);
	if (fo >= 0) sceIoWrite (fo, line, (SceSize) n0);
	const char *refeed_why = NULL;
	int refeed = !k10_avc_refeed_blocked (&refeed_why);
	unsigned out = 0, fed = 0, refed = 0, same = 0, diff = 0, first_diff = 0xFFFFFFFFu, nv12 = 0, i420 = 0;
	char first_plane = '?';
	SceUInt64 t0 = sceKernelGetProcessTimeWide ();
	while (out < h->frames && fed + refed < 2u * h->frames + K8_AVC_MAX_DELAY) {
		unsigned l2; const unsigned char *au = k8shv_au (clip, fed < h->frames ? fed : (refed % h->frames), &l2);
		if (fed >= h->frames && !refeed) break;
		if (fed >= h->frames && !refed) k10_avc_refeed_begin ();
		SceAvcdecAu a; memset (&a, 0, sizeof a);
		a.es.pBuf = (void *) au; a.es.size = l2;
		a.dts.upper = a.dts.lower = a.pts.upper = a.pts.lower = 0xFFFFFFFFu;
		SceAvcdecPicture p; memset (&p, 0, sizeof p);
		SceAvcdecPicture *pp = &p;
		SceAvcdecArrayPicture arr; memset (&arr, 0, sizeof arr);
		arr.numOfElm = 1; arr.pPicture = &pp;
		p.size = sizeof p;
		p.frame.pixelType = SCE_AVCDEC_PIXELFORMAT_YUV420_RASTER;
		p.frame.framePitch = W; p.frame.frameWidth = W; p.frame.frameHeight = H;
		p.frame.pPicture [0] = pic; p.frame.pPicture [1] = pic + ysz;
		int r = sceAvcdecDecode (&d.ctrl, &a, &arr);
		if (fed < h->frames) fed++; else refed++;
		if (r < 0) { LOG ("WARN K12 DEC video '%s': sceAvcdecDecode refused access unit %u (0x%08x) after %u pictures", name, fed + refed - 1, (unsigned) r, out); break; }
		if (!arr.numOfOutput) continue;
		unsigned long long hy = k12_fnv64 (pic, ysz), hc = k12_fnv64 (pic + ysz, csz);
		const char *res = "no reference";
		if (have_ref && out < ref.n) {
			int okY = hy == ref.y [out], okI = hc == ref.ci [out], okN = hc == ref.cn [out];
			if (okY && (okI || okN)) { same++; i420 += okI; nv12 += okN; res = okI ? "= (I420)" : "= (NV12)"; }
			else { diff++; res = "DIFFERS"; if (first_diff == 0xFFFFFFFFu) { first_diff = out; first_plane = okY ? 'C' : 'Y'; } }
		}
		int n = snprintf (line, sizeof line, "%u\t%016llx\t%016llx\t%s\n", out, hy, hc, res);
		if (fo >= 0) sceIoWrite (fo, line, (SceSize) n);
		out++;
	}
	if (refed) k10_avc_refeed_end ();
	if (fo >= 0) sceIoClose (fo);
	double secs = (double) (sceKernelGetProcessTimeWide () - t0) / 1e6;
	if (!have_ref)
		LOG ("K12 DEC video '%s': %u of %u pictures decoded by sceAvcdec (YUV 4:2:0, %u x %u) in %.1f s; no reference on the card (data/video/%s.decref.tsv: vita/tools/deccheck_ref.py) -- "
		     "hashes written to %s, compare on the Mac", name, out, h->frames, W, H, secs, name, outp);
	else if (!diff && out == h->frames && out == ref.n)
		LOG ("K12 DEC video '%s': %u pictures decoded by sceAvcdec (YUV 4:2:0, %u x %u) in %.1f s; %u of %u = the Mac's reference (ffmpeg h264, the same .shv; chroma as %s) -- the hardware decoder is exact on this stream",
		     name, out, W, H, secs, same, ref.n, i420 && !nv12 ? "I420 planes" : nv12 && !i420 ? "NV12" : "mixed");
	else
		LOG ("K12 DEC video '%s': %u of %u = the reference (%u decoded of %u%s); %s -> %s", name, same, ref.n, out, h->frames,
		     out < h->frames ? (refeed ? ", the rest held by the decoder" : refeed_why) : "", diff ? "FIRST DIFFERENCE" : "pictures missing", outp);
	if (diff) LOG ("K12 DEC video '%s': FIRST DIFFERENCE picture %u plane %c", name, first_diff, first_plane);
	k8avc_close (&d);
	shepgl_vram_free (work); shepgl_vram_free (pic); shepgl_vram_free (clip);
	free (ref.y); free (ref.ci); free (ref.cn);
}
#ifdef SHEPHERD_AUDIO
static void k13_deccheck_aac (void);   /* device kit #13 bootc (k13_bootc.inc.c): the AAC half (SHEPHERD_K12_DECCHECK=...aac...) */
#endif
/* main, right after the loading screen started and before the Mono thread exists (SHEPHERD_K12_DECCHECK=video and / or aac; off: nothing) */
static void k12_deccheck (void)
{
	const char *e = getenv ("SHEPHERD_K12_DECCHECK");
	if (!g_k12_master || !e || !*e) return;
	if (strstr (e, "aac")) {   /* device kit #13 bootc (k13_bootc.inc.c): the AAC half, in the apps that carry shepaudio */
#ifdef SHEPHERD_AUDIO
		k13_deccheck_aac ();
#else
		LOG ("K13 DEC aac: this app has no AAC decoder (no shepaudio: the play apps carry it) -- not run");
#endif
	}
	if (!strstr (e, "video")) return;
	static const char *const clips [] = { "Devolver_Animated_Logo_v001_4k_Audio-_HQ_HBR_MP4", "Intro", "MassiveMonster" };
	SceUInt64 t0 = sceKernelGetProcessTimeWide ();
	for (unsigned i = 0; i < sizeof clips / sizeof clips [0]; i++) k12_deccheck_clip (clips [i]);
	LOG ("K12 DEC: the decoder checks took %.1f s before the engine started (the game's frames and records are untouched)", (double) (sceKernelGetProcessTimeWide () - t0) / 1e6);
}
#endif /* SHEPHERD_GL */
