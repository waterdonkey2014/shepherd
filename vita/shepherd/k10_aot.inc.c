/* SHEPHERD-VITA device kit #10, track aot (vita/notes/kit10-aot.md): the host side of the kit #10 AOT rows. Included by host.c.
 *
 * AOT THAT LEARNS FROM PLAY (vita/patches/k10-aot-learn.patch): the runtime records, once each, every method it looked for in the AOT
 * images and did not find (the interpreter runs it): the non-generic methods of an image that has an AOT module (the game-AOT app's
 * profile-only owner images, the trimmed CoreLib) and the generic instances compiled code needed that no image holds. This file appends
 * the records to <logs>/aot-learn.tsv -- RESULTS ONLY (which methods ran interpreted; never game state), carried back with the device
 * logs (read-logs.sh) and from every vlx run; vita/tools/k10_learn.py merges them into the list the next build compiles (exactly those
 * methods, the same IL: build.sh SHEPHERD_K10_AOTLEARNED). Append-only across starts, each start behind a "# session" line, capped at 4 MiB:
 * a start that finds the file at the cap moves it to aot-learn.prev.tsv (replacing the older one; k10_learn.py reads both) and begins a new
 * one; a file that reaches the cap during a start stops the record for the rest of that start -- the runtime is told to stop recording and
 * what waits in its buffer is dropped (kit #10 review: take (NULL, 0) only counts, so the buffer and the set of recorded methods kept
 * growing to their cap and stayed there for the whole run). The path is made once at k10a_init (no getenv on the power thread).
 * Written by the power thread every 5 s when new records arrived (a few lines: the cache track's idle-window scheduler should own that
 * moment once it exists), at the end checkpoint, at H11 and at a FAIL END. Recording never changes what runs.
 *   SHEPHERD_K10_AOTLEARN (env file): unset / 1 = on; 0 / off = no record, no file (kit #9). A runtime without the patch: no record (H8 says so).
 *
 * COMPRESSED IL (vita/patches/k10-ilz.patch; the files written on the Mac by vita/tools/k10_ilz.py stage): when ux0:data/shepherd/managed-ilz/
 * holds an assembly, the game's assembly is read from there -- its never-run method bodies compressed, unpacked by the runtime on first
 * use (the original bytes) -- instead of from managed/; Assembly.Location stays the managed/ path. Only with a runtime that carries the
 * patch (a kit #9 runtime would fail those methods: never offered one) and never for a SHEPHERD_K9_FLOATPC folder's DLL.
 *   SHEPHERD_K10_ILZ (env file): unset / 1 = on when the folder exists; 0 / off = the original managed/ DLLs only (kit #9).
 * VERIFIED ON FIRST REUSE, EXACT (the hard rule for anything stored on the card): an ILZ file is used only when this start's two files --
 * managed/<name>.dll by size + modification time + the CRC-32 of its first 1 KiB (its PE / COFF headers: the build's TimeDateStamp -- a
 * content hash in deterministic builds --, the section table, the CLI header: another build of the same size copied with its mtime kept
 * differs there; kit #10 review) and managed-ilz/<name>.dll by size + modification time -- match a stamp in cache/k10-ilz-verified.tsv
 * written by an earlier full check; with no matching stamp (the first start after staging, a copied / updated DLL, a Clear Cache) the full
 * check runs first: the ILZ header's layout must be the one this runtime reads (version 2, its virtual range = the .ilz section's, a block
 * table behind the header: kit #10 review -- the runtime marks any other file bad and every cold method would fail), the original's CRC-32
 * must be the one the header records (the ILZ form was made from THIS original) and the ILZ file's own CRC-32 (header word 56, flag word
 * 60 = 1: k10_ilz.py v2 files) must match -- then the stamp is written; any mismatch = the original is read (a WARN line; slower, never
 * different). The check reads both files once (~6 s on the card for every owner DLL, one start only). The stamp file holds one line per
 * assembly, the latest (rewritten on each new stamp: kit #10 review -- it only ever grew, and past its 256 KiB read cap no stamp was read,
 * so every start re-checked and appended). Stamp lines carry a format tag ("k2"): a line of an earlier format never matches (one re-check).
 * The verification's card reads and its lock wait run in a GC-safe region (kit #10 review: a stop-the-world waited for them).
 * The END line: the learning record's counts; the ILZ assemblies read (their bytes vs their originals'), the bodies unpacked and their
 * bytes, the block unpacks, the errors (a block that did not unpack or check: that method failed loudly). */
#if !defined (SHEPHERD_MAC_CHECK)
#include <psp2/io/dirent.h>   /* sceIoDopen / sceIoDread / sceIoDclose (SceIofilemgr, as sceIoOpen) */
extern int mono_shepherd_k10_learn_mode __attribute__ ((weak));
extern volatile int mono_shepherd_k10_learn_records __attribute__ ((weak));
extern volatile int mono_shepherd_k10_learn_wrappers __attribute__ ((weak));
extern volatile int mono_shepherd_k10_learn_dropped __attribute__ ((weak));
extern int mono_shepherd_k10_learn_take (char *dst, int cap) __attribute__ ((weak));
extern volatile int mono_shepherd_k10_ilz_images __attribute__ ((weak));
extern volatile int mono_shepherd_k10_ilz_bodies __attribute__ ((weak));
extern volatile int mono_shepherd_k10_ilz_body_bytes __attribute__ ((weak));
extern volatile int mono_shepherd_k10_ilz_inflates __attribute__ ((weak));
extern volatile int mono_shepherd_k10_ilz_inflate_bytes __attribute__ ((weak));
extern volatile int mono_shepherd_k10_ilz_errors __attribute__ ((weak));

#ifdef SHP_APP_ID
#define K10A_APP_ID SHP_APP_ID
#else
#define K10A_APP_ID "?"
#endif
#define K10A_ILZ_DIR GAME_MANAGED "-ilz"
#define K10A_LEARN_FILE_CAP (4u * 1024u * 1024u)
static int g_k10a_init, g_k10a_learn_on, g_k10a_ilz_on, g_k10a_ilz_dir_files;
static unsigned g_k10a_ilz_files, g_k10a_ilz_orig_kib, g_k10a_ilz_kib, g_k10a_ilz_stale;
static unsigned g_k10a_learn_written, g_k10a_learn_flushes, g_k10a_learn_capped, g_k10a_learn_rotated_kib;
static int g_k10a_learn_session;   /* the "# session" line written this start */
static char g_k10a_learn_path [200], g_k10a_learn_prev [208];   /* <logs>/aot-learn.tsv, <logs>/aot-learn.prev.tsv (made at k10a_init) */
#define K10A_ILZ_STAMPS DIR_ "/cache/k10-ilz-verified.tsv"
#define K10A_STAMP_TAG "k2\t"   /* the stamp format: k2 = layout + both CRCs checked, the original's first 1 KiB in the key (kit #10 review) */
#define K10A_STAMP_READ_CAP (256u * 1024u)
static unsigned g_k10a_ilz_verified, g_k10a_ilz_stamped, g_k10a_ilz_refused, g_k10a_ilz_verify_ms;
static char *g_k10a_stamps; static unsigned g_k10a_stamps_len, g_k10a_stamps_cap; static int g_k10a_stamps_read;
static pthread_mutex_t g_k10a_ilz_lock = PTHREAD_MUTEX_INITIALIZER;
static unsigned g_k10a_crc_tab [256];
static pthread_mutex_t g_k10a_learn_lock = PTHREAD_MUTEX_INITIALIZER;
/* kit #10 integration (the cache track's idle window, k10_cache.inc.c -- included after this file): the power thread's quiet passes write the
 * learning record inside the engine's idle window (logos, quotes, menus, the open Settings menu: never a scene load or gameplay), or when 60 s
 * passed since the last write, or when the runtime's buffer holds >= 128 KiB (its cap is 256 KiB: nothing dropped that the 5 s pace kept);
 * the tagged flushes (H11, the end, FAIL END) write at once as before. SHEPHERD_K10_IDLE=0 or SHEPHERD_K10_CACHE=0 (no window ever opens) =
 * the aot track's 5 s pace. */
int shepherd_k10_window (void);
static int g_k10a_learn_windowed;
static SceUInt64 g_k10a_learn_last_us;

static int k10a_env_off (const char *name)
{
	const char *e = getenv (name);
	return e && (!strcmp (e, "0") || !strcmp (e, "off"));
}

static void k10a_init (void)
{
	if (g_k10a_init)
		return;
	g_k10a_learn_on = &mono_shepherd_k10_learn_take && !k10a_env_off ("SHEPHERD_K10_AOTLEARN");
	g_k10a_learn_windowed = !k10a_env_off ("SHEPHERD_K10_CACHE") && !k10a_env_off ("SHEPHERD_K10_IDLE");   /* (kit #10 integration: above) */
	snprintf (g_k10a_learn_path, sizeof (g_k10a_learn_path), "%s/aot-learn.tsv", logs_dir ());
	snprintf (g_k10a_learn_prev, sizeof (g_k10a_learn_prev), "%s/aot-learn.prev.tsv", logs_dir ());
	if (g_k10a_learn_on) {
		/* (kit #10 review) a record at its cap is moved aside before this start records anything: the older .prev goes, k10_learn.py
		 * merges both names -- the learning loop never stalls on a full file */
		SceIoStat st;
		if (sceIoGetstat (g_k10a_learn_path, &st) >= 0 && (unsigned) st.st_size >= K10A_LEARN_FILE_CAP) {
			sceIoRemove (g_k10a_learn_prev);
			if (sceIoRename (g_k10a_learn_path, g_k10a_learn_prev) >= 0)
				g_k10a_learn_rotated_kib = (unsigned) st.st_size / 1024u;
		}
	}
	g_k10a_ilz_dir_files = 0;
	if (&mono_shepherd_k10_ilz_images && !k10a_env_off ("SHEPHERD_K10_ILZ")) {
		SceUID d = sceIoDopen (K10A_ILZ_DIR);
		if (d >= 0) {
			SceIoDirent e;
			while (sceIoDread (d, &e) > 0) {
				size_t l = strlen (e.d_name);
				if (l > 4 && !strcmp (e.d_name + l - 4, ".dll"))
					g_k10a_ilz_dir_files++;
			}
			sceIoDclose (d);
		}
		g_k10a_ilz_on = g_k10a_ilz_dir_files > 0;
	}
	g_k10a_init = 1;
}

/* the SHPILZ1 header of an ILZ file: its file offset, the original's size + CRC-32, the file's own CRC-32 and its flag (v2 files: 1), and
 * whether its layout is the one the runtime reads (k10-ilz.patch k10_ilz_state: version 2, the header's virtual range = the .ilz section's,
 * the block table behind the 64-byte header -- anything else the runtime marks bad and every cold method fails: kit #10 review);
 * 0 = unreadable / not an ILZ file */
static int k10a_ilz_hdr (const char *path, unsigned *hoff, unsigned *orig_size, unsigned *orig_crc, unsigned *file_crc, unsigned *flag, int *layout_ok)
{
	unsigned char h [1024];
	int ok = 0;
	SceUID fd = sceIoOpen (path, SCE_O_RDONLY, 0);
	if (fd < 0)
		return 0;
	if (sceIoRead (fd, h, sizeof (h)) == (int) sizeof (h)) {
		unsigned pe = h [0x3c] | h [0x3d] << 8, ns, opt, so;
		if (pe + 24 < sizeof (h) && h [pe] == 'P' && h [pe + 1] == 'E') {
			ns = h [pe + 6] | h [pe + 7] << 8;
			opt = h [pe + 20] | h [pe + 21] << 8;
			so = pe + 24 + opt;
			for (unsigned i = 0; i < ns && so + 40 * (i + 1) <= sizeof (h); i++) {
				unsigned char *e = h + so + 40 * i;
				if (!memcmp (e, ".ilz\0\0\0\0", 8)) {
#define K10A_LE32(b, o) ((b) [o] | (b) [(o) + 1] << 8 | (b) [(o) + 2] << 16 | (unsigned) (b) [(o) + 3] << 24)
					unsigned hp = K10A_LE32 (e, 20), svs = K10A_LE32 (e, 8), sva = K10A_LE32 (e, 12);
					unsigned char t [64];
					if (sceIoLseek (fd, hp, SCE_SEEK_SET) == (SceOff) hp && sceIoRead (fd, t, sizeof (t)) == (int) sizeof (t) && !memcmp (t, "SHPILZ1", 8)) {
						*hoff = hp; *orig_size = K10A_LE32 (t, 32); *orig_crc = K10A_LE32 (t, 36); *file_crc = K10A_LE32 (t, 56); *flag = K10A_LE32 (t, 60);
						*layout_ok = K10A_LE32 (t, 8) == 2 && K10A_LE32 (t, 16) == sva && K10A_LE32 (t, 20) == svs && K10A_LE32 (t, 48) >= 64;
						ok = 1;
					}
#undef K10A_LE32
					break;
				}
			}
		}
	}
	sceIoClose (fd);
	return ok;
}

/* CRC-32 (IEEE 802.3, zlib's crc32: the value k10_ilz.py writes) of a whole file -- or of its first `limit` bytes (0: all) --, the 4 bytes at
 * file offset `zero_at` read as zero (~0u: none); 0 = unreadable */
static int k10a_crc_file (const char *path, unsigned zero_at, unsigned limit, unsigned *crc_out, unsigned *size_out)
{
	if (!g_k10a_crc_tab [1])
		for (unsigned i = 0; i < 256; i++) {
			unsigned c = i;
			for (int k = 0; k < 8; k++)
				c = c & 1 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
			g_k10a_crc_tab [i] = c;
		}
	SceUID fd = sceIoOpen (path, SCE_O_RDONLY, 0);
	if (fd < 0)
		return 0;
	unsigned bs = limit && limit < 65536u ? limit : 65536u;
	unsigned char *buf = malloc (bs);
	if (!buf) {
		sceIoClose (fd);
		return 0;
	}
	unsigned crc = 0xFFFFFFFFu, at = 0;
	int n;
	while ((!limit || at < limit) && (n = sceIoRead (fd, buf, limit && limit - at < bs ? limit - at : bs)) > 0) {
		for (int i = 0; i < n; i++) {
			unsigned char c = (zero_at != ~0u && at + i >= zero_at && at + i < zero_at + 4) ? 0 : buf [i];
			crc = g_k10a_crc_tab [(crc ^ c) & 0xff] ^ (crc >> 8);
		}
		at += (unsigned) n;
	}
	free (buf);
	sceIoClose (fd);
	if (n < 0)
		return 0;
	*crc_out = crc ^ 0xFFFFFFFFu;
	*size_out = at;
	return 1;
}

static void k10a_dt (char *o, size_t cap, const SceDateTime *t)
{
	snprintf (o, cap, "%04u%02u%02u%02u%02u%02u.%06u", t->year, t->month, t->day, t->hour, t->minute, t->second, t->microsecond);
}

/* the stamps of earlier full checks (cache/k10-ilz-verified.tsv: results only -- which file pairs were checked, by size + mtime (+ the
 * original's first 1 KiB) + CRC-32), read once */
static void k10a_stamps_read (void)
{
	if (g_k10a_stamps_read)
		return;
	g_k10a_stamps_read = 1;
	void *d = NULL;
	unsigned n = 0;
	if (file_size (K10A_ILZ_STAMPS, &n) == 0 && n > 0 && n < K10A_STAMP_READ_CAP && (d = malloc (n + 1)) != NULL) {
		SceUID fd = sceIoOpen (K10A_ILZ_STAMPS, SCE_O_RDONLY, 0);
		int r = fd >= 0 ? sceIoRead (fd, d, n) : -1;
		if (fd >= 0)
			sceIoClose (fd);
		if (r == (int) n) {
			((char *) d) [n] = 0;
			g_k10a_stamps = d; g_k10a_stamps_len = n; g_k10a_stamps_cap = n + 1;
		} else
			free (d);
	}
}

static int k10a_stamp_has (const char *key)
{
	k10a_stamps_read ();
	if (!g_k10a_stamps)
		return 0;
	size_t kl = strlen (key);
	for (const char *q = g_k10a_stamps; (q = strstr (q, key)) != NULL; q += kl)
		if ((q == g_k10a_stamps || q [-1] == '\n') && q [kl] == '\t')
			return 1;   /* (a whole key: line start, then the CRC columns) */
	return 0;
}

/* a new stamp for assembly `name`: the in-memory lines of the same assembly (any format) and every line of another format dropped, the
 * new line added, the file rewritten from them -- one line per assembly, so it never grows past the assemblies staged (kit #10 review: it
 * only grew, and past its read cap no stamp was read). A cut-off write can only lose stamps (= a re-check), never add one. */
static void k10a_stamp_add (const char *name, const char *line)
{
	k10a_stamps_read ();
	size_t nl = strlen (name), tl = strlen (K10A_STAMP_TAG), l = strlen (line);
	char *nb = malloc (g_k10a_stamps_len + l + 1);
	if (!nb)
		return;
	unsigned o = 0;
	for (unsigned i = 0; i < g_k10a_stamps_len; ) {
		const char *s = g_k10a_stamps + i, *e = memchr (s, '\n', g_k10a_stamps_len - i);
		unsigned ll = e ? (unsigned) (e - s) + 1 : g_k10a_stamps_len - i;
		if (e && ll > tl + nl + 1 && !memcmp (s, K10A_STAMP_TAG, tl) && !(!memcmp (s + tl, name, nl) && s [tl + nl] == '\t')) {
			memcpy (nb + o, s, ll);
			o += ll;
		}
		i += ll;
	}
	memcpy (nb + o, line, l + 1);
	o += (unsigned) l;
	free (g_k10a_stamps);
	g_k10a_stamps = nb; g_k10a_stamps_len = o; g_k10a_stamps_cap = o + 1;
	sceIoMkdir (DIR_ "/cache", 0777);   /* (exists on every staged card; harmless when it does) */
	SceUID fd = sceIoOpen (K10A_ILZ_STAMPS, SCE_O_WRONLY | SCE_O_CREAT | SCE_O_TRUNC, 0777);
	if (fd >= 0) {
		sceIoWrite (fd, g_k10a_stamps, (SceSize) o);
		sceIoClose (fd);
	}
}

/* may the ILZ file `p` stand in for the original `orig` (its size `osize`)? -- a stamp of an earlier full check of exactly these two files,
 * else the full check now (then a stamp). Under g_k10a_ilz_lock. */
static int k10a_ilz_verified (const char *name, const char *orig, unsigned osize, const char *p)
{
	SceIoStat so, sz;
	unsigned hcrc1k = 0, h1k = 0;
	if (sceIoGetstat (orig, &so) < 0 || sceIoGetstat (p, &sz) < 0 || !k10a_crc_file (orig, ~0u, 1024u, &hcrc1k, &h1k))
		return 0;
	char dto [32], dtz [32], key [288];
	k10a_dt (dto, sizeof (dto), &so.st_mtime);
	k10a_dt (dtz, sizeof (dtz), &sz.st_mtime);
	snprintf (key, sizeof (key), K10A_STAMP_TAG "%s\t%u\t%s\t%08x\t%u\t%s", name, (unsigned) so.st_size, dto, hcrc1k, (unsigned) sz.st_size, dtz);
	if (k10a_stamp_has (key)) {
		g_k10a_ilz_stamped++;
		return 1;
	}
	unsigned hoff = 0, hsize = 0, hcrc = 0, fcrc = 0, flag = 0, ocrc = 0, ozs = 0, zcrc = 0, zs = 0;
	int layout = 0;
	SceUInt64 t0 = sceKernelGetProcessTimeWide ();
	const char *why = NULL;
	if (!k10a_ilz_hdr (p, &hoff, &hsize, &hcrc, &fcrc, &flag, &layout))
		why = "no readable SHPILZ1 header";
	else if (flag != 1)
		why = "no file CRC-32 in its header (a k10_ilz.py v1 file: restage managed-ilz/)";
	else if (!layout)
		why = "a header layout this runtime does not read (version / range / block table: restage with this kit's k10_ilz.py)";
	else if (hsize != osize)
		why = "made from an original of another size";
	else if (!k10a_crc_file (orig, ~0u, 0, &ocrc, &ozs) || ozs != osize || ocrc != hcrc)
		why = "made from another original (CRC-32)";
	else if (!k10a_crc_file (p, hoff + 56, 0, &zcrc, &zs) || zcrc != fcrc)
		why = "damaged (its own CRC-32)";
	g_k10a_ilz_verify_ms += (unsigned) ((sceKernelGetProcessTimeWide () - t0) / 1000u);
	if (why) {
		g_k10a_ilz_refused++;
		LOG ("WARN K10 ilz: %s not used (%s): the original " GAME_MANAGED "/%s.dll is read -- restage managed-ilz/ (k10_ilz.py stage)", p, why, name);
		return 0;
	}
	char line [360];
	snprintf (line, sizeof (line), "%s\t%08x\t%08x\n", key, ocrc, zcrc);
	k10a_stamp_add (name, line);
	g_k10a_ilz_verified++;
	return 1;
}

/* kit #10 integration (boot x aot, k10_ilb.inc.c): is compressed IL on for this start (the switch, the folder, the runtime)? */
static int k10a_ilz_on_now (void) { k10a_init (); return g_k10a_ilz_on; }

/* the preload hook, for an assembly about to be read from GAME_MANAGED (not a FloatPc folder's): its ILZ form instead, when there is one */
static void k10a_ilz_path (const char *name, char *path, size_t cap, unsigned *size)
{
	k10a_init ();
	if (!g_k10a_ilz_on || strncmp (path, GAME_MANAGED "/", sizeof (GAME_MANAGED)) != 0)
		return;
	char p [300];
	unsigned n = 0;
	if (!strncmp (name, "Shepherd.", 9))
		return;   /* (the build's own DLLs -- the derived module is rebuilt with every eboot: never from managed-ilz/) */
	snprintf (p, sizeof (p), K10A_ILZ_DIR "/%s.dll", name);
	if (file_size (p, &n) != 0 || strlen (p) >= cap)
		return;
	/* a result of ITS original, verified on first reuse (exact): the stamp of an earlier full check of exactly these two files, else the full
	 * check now (the header's layout, the original's CRC-32 = the one the ILZ header records, the ILZ file's own CRC-32); anything else -- a
	 * game update, a replaced or damaged file -- reads the original (a stale ILZ form would run the old IL; k10_ilz.py restages it).
	 * (kit #10 review: in a GC-safe region, as the hook's own lock wait and reads -- the check reads the card and may wait for another
	 * thread's check; nothing in it touches managed memory) */
	int ok;
	{
		SAFE_BEGIN ();
		pthread_mutex_lock (&g_k10a_ilz_lock);
		ok = k10a_ilz_verified (name, path, *size, p);
		pthread_mutex_unlock (&g_k10a_ilz_lock);
		SAFE_END ();
	}
	if (!ok) {
		g_k10a_ilz_stale++;
		return;
	}
	g_k10a_ilz_files++;
	g_k10a_ilz_orig_kib += *size / 1024u;
	g_k10a_ilz_kib += n / 1024u;
	snprintf (path, cap, "%s", p);
	*size = n;
}

/* the learning record to <logs>/aot-learn.tsv (append); tag NULL = the power thread's quiet 5-s pass */
static void k10a_learn_flush (const char *tag)
{
	k10a_init ();
	if (!g_k10a_learn_on || !&mono_shepherd_k10_learn_take || g_k10a_learn_capped)
		return;   /* (capped: the runtime records nothing more this start -- below) */
	int pending = mono_shepherd_k10_learn_take (NULL, 0);
	if (pending <= 0 && (g_k10a_learn_session || !tag))
		return;   /* nothing new (the session line is written with the first records, or at the first tagged flush) */
	if (!tag && g_k10a_learn_windowed && !shepherd_k10_window () && pending < 128 * 1024
	    && sceKernelGetProcessTimeWide () - g_k10a_learn_last_us < 60000000ULL)
		return;   /* (kit #10 integration) a quiet pass outside the idle window: the next window, 60 s, or a half-full buffer writes it */
	if (pthread_mutex_trylock (&g_k10a_learn_lock) != 0)
		return;   /* (another flush is writing: its pass takes these too, or the next one does) */
	const char *path = g_k10a_learn_path;
	SceIoStat st;
	unsigned have = sceIoGetstat (path, &st) >= 0 ? (unsigned) st.st_size : 0;
	char *buf = malloc (16384);
	if (have >= K10A_LEARN_FILE_CAP) {
		/* (kit #10 review) the file reached its cap during this start: the runtime stops recording for the rest of it (its buffer and its set
		 * of recorded methods stop growing) and what waits in the buffer is dropped -- take (NULL, 0) only counts. The next start moves the
		 * file to aot-learn.prev.tsv (k10a_init) and records again. */
		g_k10a_learn_capped = 1;
		if (&mono_shepherd_k10_learn_mode)
			mono_shepherd_k10_learn_mode = 0;
		unsigned dropped = 0;
		int n;
		while (buf && (n = mono_shepherd_k10_learn_take (buf, 16384)) > 0)
			dropped += (unsigned) n;
		LOG ("WARN K10 learn: %s holds %u KiB (the cap: %u KiB): the record is off for the rest of this start (%u B waiting dropped; the next start moves the file to aot-learn.prev.tsv) -- read-logs.sh takes both, k10_learn.py merges both", path, have / 1024, K10A_LEARN_FILE_CAP / 1024, dropped);
		free (buf);
		pthread_mutex_unlock (&g_k10a_learn_lock);
		return;
	}
	SceUID fd = sceIoOpen (path, SCE_O_WRONLY | SCE_O_CREAT | SCE_O_APPEND, 0777);
	if (fd < 0) {
		free (buf);
		pthread_mutex_unlock (&g_k10a_learn_lock);
		return;
	}
	if (buf) {
		if (!g_k10a_learn_session) {
			g_k10a_learn_session = 1;
			int o = snprintf (buf, 16384, "# Shepherd AOT learning record (kit #10, vita/notes/kit10-aot.md): flag image token desc name -- m = a method of an AOT image the build left out, "
					  "g / h = a generic instance compiled code asked for that no image holds (its definition's image AOT / interpreted)\n"
					  "# session %s build %s at %.1f s of process time\n", K10A_APP_ID, SHP_BUILD_ID, wall_s ());
			sceIoWrite (fd, buf, (SceSize) o);
		}
		int n;
		while ((n = mono_shepherd_k10_learn_take (buf, 16384)) > 0) {
			sceIoWrite (fd, buf, (SceSize) n);
			g_k10a_learn_written += (unsigned) n;
		}
		free (buf);
	}
	sceIoClose (fd);
	g_k10a_learn_flushes++;
	g_k10a_learn_last_us = sceKernelGetProcessTimeWide ();
	pthread_mutex_unlock (&g_k10a_learn_lock);
}

/* after mono_jit_init_version (the H8 lines) */
static void k10a_log (void)
{
	k10a_init ();
	const char *l = getenv ("SHEPHERD_K10_AOTLEARN"), *z = getenv ("SHEPHERD_K10_ILZ");
	if (!&mono_shepherd_k10_learn_take)
		LOG ("H8 K10 aot: no learning record in this runtime (vita/patches/k10-aot-learn.patch; SHEPHERD_K10_AOTLEARN=%s ignored)", l ? l : "unset");
	else
		LOG ("H8 K10 aot: learning record %s (SHEPHERD_K10_AOTLEARN=%s; 0 = none, kit #9): every method the AOT images lacked that ran interpreted, once each -> %s (append; for vita/tools/k10_learn.py)%s",
		     g_k10a_learn_on ? "ON" : "OFF", l ? l : "unset", g_k10a_learn_path, g_k10a_learn_rotated_kib ? " -- the file was at its cap: moved to aot-learn.prev.tsv, a new one begun" : "");
	if (!&mono_shepherd_k10_ilz_images)
		LOG ("H8 K10 aot: no compressed-IL support in this runtime (vita/patches/k10-ilz.patch): the game's assemblies from " GAME_MANAGED " (SHEPHERD_K10_ILZ=%s ignored)", z ? z : "unset");
	else
		LOG ("H8 K10 aot: compressed IL %s (SHEPHERD_K10_ILZ=%s; 0 = the original DLLs, kit #9): %d ILZ assemblies in " K10A_ILZ_DIR "/%s",
		     g_k10a_ilz_on ? "ON" : "OFF", z ? z : "unset", g_k10a_ilz_dir_files, g_k10a_ilz_on ? " -- read instead of their " GAME_MANAGED " copies, never-run bodies unpacked on first use" : "");
}

/* the end checkpoint (kind 2), H11 and FAIL END */
static void k10a_end (const char *tag)
{
	k10a_learn_flush (tag);
	if (&mono_shepherd_k10_learn_take)
		LOG ("K10 learn (%s): %d method(s) the AOT images lacked ran interpreted (recorded once each), %d wrapper miss(es) counted, %d record(s) dropped (buffer full) -> %s (%u B appended this start, %u write(s)%s%s)",
		     tag, &mono_shepherd_k10_learn_records ? mono_shepherd_k10_learn_records : -1, &mono_shepherd_k10_learn_wrappers ? mono_shepherd_k10_learn_wrappers : -1,
		     &mono_shepherd_k10_learn_dropped ? mono_shepherd_k10_learn_dropped : -1, g_k10a_learn_path, g_k10a_learn_written, g_k10a_learn_flushes,
		     g_k10a_learn_on ? "" : "; SHEPHERD_K10_AOTLEARN=0: no file", g_k10a_learn_capped ? "; WARN: the file reached its cap -- the record stopped" : (g_k10a_learn_rotated_kib ? "; the file at its cap was moved to aot-learn.prev.tsv at the start" : ""));
	if (&mono_shepherd_k10_ilz_images)
		LOG ("K10 ilz (%s): %u assemblies read in their ILZ form (%u KiB instead of %u KiB: -%u KiB; %u ILZ file(s) not used), verified on first reuse: %u by a stamp, %u checked now (%u ms), %u refused; %d with compressed bodies opened; %d bodies unpacked (%d KiB), %d block unpack(s) (%d KiB), %d error(s)%s",
		     tag, g_k10a_ilz_files, g_k10a_ilz_kib, g_k10a_ilz_orig_kib, g_k10a_ilz_orig_kib - g_k10a_ilz_kib, g_k10a_ilz_stale, g_k10a_ilz_stamped, g_k10a_ilz_verified, g_k10a_ilz_verify_ms, g_k10a_ilz_refused, mono_shepherd_k10_ilz_images, mono_shepherd_k10_ilz_bodies,
		     mono_shepherd_k10_ilz_body_bytes / 1024, mono_shepherd_k10_ilz_inflates, mono_shepherd_k10_ilz_inflate_bytes / 1024, mono_shepherd_k10_ilz_errors,
		     mono_shepherd_k10_ilz_errors ? " -- WARN: a block did not unpack / check (a damaged managed-ilz/ file: SHEPHERD_K10_ILZ=0, then restage it)" : "");
}
#else
static void k10a_init (void) { }
static void k10a_ilz_path (const char *name, char *path, size_t cap, unsigned *size) { (void) name; (void) path; (void) cap; (void) size; }
static int k10a_ilz_on_now (void) { return 0; }
static void k10a_learn_flush (const char *tag) { (void) tag; }
static void k10a_log (void) { }
static void k10a_end (const char *tag) { (void) tag; }
#endif
