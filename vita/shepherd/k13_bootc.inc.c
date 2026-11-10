/* ---------------------------------------------------------------- device kit #13, track bootc (vita/notes/kit13-bootc.md)
 * Included by host.c after k10_cache.inc.c (the compressed-IL helpers of k10_aot.inc.c are above). One export for the engine,
 * shepherd_vita_k13_bootc (op, io, buf, cap) -- GL apps only (build.sh HOST_EXPORTS):
 *
 *  op 1, VERIFY FILES OVER THE COMPRESSED CODE (section 8): the PS Vita tab's Verify Files checked the card's pack only; since kit #12 every VPK
 *   also carries its own DLLs in compressed-IL form (app0:managed-ilz/, walkb's VPKILZ, read by default) and the card may hold the game's
 *   (ux0:data/shepherd/managed-ilz/, kit #10). The load checks each pair once and remembers it by size + date (k12_walkb.inc.c /
 *   k10_aot.inc.c stamps); Verify Files now reads every one of them again in full, ignoring the stamps: the SHPILZ1 header readable and of
 *   the layout the runtime reads, made from an original of this size and CRC-32 (the original next to it in managed/), and the file's own
 *   CRC-32. Reads only (no stamp written, nothing deleted). io [0] = 1: the app's folder, 2: the card's, 3: both (default); review fix:
 *   io [1] = the first file to check (over the folders in order), io [2] = at most this many (both 0: all, in one call);
 *   out: io [0] files checked, io [1] bad, io [2] KiB read, io [3] ms; buf = the first bad file and why ("" none). Returns 0, < 0 = no
 *   compressed-IL runtime in this build (nothing checked).
 *  op 2 (review fix): the same folders' sizes only -- io [0] files, io [2] KiB to read (each form + its original), nothing read: the engine
 *   counts the compressed code into the window's progress from the start, then checks it in batches (progress moving, the stop honoured).
 *   SHEPHERD_K13_VERIFY_ILZ=0 (or SHEPHERD_K13_BOOTC=0) in the env file: the engine does not ask (kit #12's Verify Files).
 *  k13_deccheck_aac (play apps): the AAC half of the decoder checks (section 7; called by k12_boot.inc.c's k12_deccheck). */
#ifdef SHEPHERD_GL
#if defined (SHEPHERD_AUDIO)
/* ---- section 7: THE AAC HALF OF THE DECODER CHECKS (test-only; SHEPHERD_K12_DECCHECK names 'aac', kit #12's switch; the video half is kit #12's
 * k12_deccheck): every take the Mac's reference names (ux0:data/shepherd/audiobake/menu/decref-aac.tsv, vita/tools/deccheck_aac_ref.py: the same
 * driver, vita/audio/tools/sa_deccheck.h, over AudioToolbox) decoded once more through shepaudio's sceAudiodec decoder (sa_platform_aac) -- before
 * the Mono thread exists (shepaudio starts with the engine: its library is initialised and ended here first), so the game's frames and records
 * are untouched; the boot takes the check's time longer. Lines: K13 DEC aac ...; per take <logs>/dec-aac.tsv (device frames, FNV-1a 64, peak --
 * vita/tools/deccheck_aac_ref.py compare reads it); a differing take's first 64 KiB of samples in <logs>/dec-aac-<n>.pcm (results only: AAC is
 * not bit-exact by the standard, so a difference is the answer "how far", for the Mac to measure). Never claims a match it did not see: a take
 * that did not read or decode is counted apart. Under vlx the stand-in decoder hands back zeros (vita/vlx/audio_vlx.c): every take differs. */
#include "../audio/tools/sa_deccheck.h"
typedef struct { int16_t *keep; unsigned kept, cap; } K13DecKeep;
static void k13_dec_keep (const int16_t *p, int n, void *ctx)
{
	K13DecKeep *k = (K13DecKeep *) ctx;
	unsigned room = k->cap - k->kept, m = (unsigned) n < room ? (unsigned) n : room;
	if (m) { memcpy (k->keep + k->kept, p, m * 2u); k->kept += m; }
}
static void k13_deccheck_aac (void)
{
	void *d = NULL;
	unsigned sz = 0;
	if (read_file (DIR_ "/audiobake/menu/decref-aac.tsv", &d, &sz) != 0 || !sz) {
		free (d);
		LOG ("K13 DEC aac: no reference on the card (audiobake/menu/decref-aac.tsv: vita/tools/deccheck_aac_ref.py ref <card>) -- not run");
		return;
	}
	const sa_aac_vtbl *v = sa_platform_aac ();
	if (!v || (v->lib_init && v->lib_init (1) != 0)) { free (d); LOG ("WARN K13 DEC aac: the AAC decoder library did not start -- not run (the players are unaffected)"); return; }
	char out [300];
	snprintf (out, sizeof out, "%s/dec-aac.tsv", logs_dir ());
	FILE *o = fopen (out, "w");
	if (o) fprintf (o, "# the AAC decoder check's device side (host.c k13_bootc.inc.c, sceAudiodec through shepaudio)\n# file\tchannels\trate\tframes\tfnv64\tpeak\taccess units\trefused\n");
	int16_t *pcm = malloc (8192u * 2u * 2u);
	K13DecKeep keep = { malloc (65536u), 0, 32768u };
	unsigned n = 0, same = 0, diff = 0, bad = 0;
	unsigned long long frames = 0;
	SceUInt64 t0 = sceKernelGetProcessTimeWide ();
	for (char *line = (char *) d, *nl; line && *line && pcm && keep.keep; line = nl) {
		nl = strchr (line, '\n'); if (nl) *nl++ = 0;
		char file [200]; int ch = 0, rate = 0; unsigned rfr = 0, rpk = 0; unsigned long long rfnv = 0;
		if (*line == '#' || sscanf (line, "%199[^\t]\t%d\t%d\t%u\t%llx\t%u", file, &ch, &rate, &rfr, &rfnv, &rpk) != 6 || strstr (file, "..")) continue;
		n++;
		char p [300];
		snprintf (p, sizeof p, DIR_ "/audiobake/menu/%s", file);
		void *b = NULL; unsigned bl = 0;
		sa_dec_res r;
		keep.kept = 0;
		if (read_file (p, &b, &bl) != 0 || !bl || sa_deccheck_buf (v, (const unsigned char *) b, (int) bl, ch, rate, &r, pcm, 8192, k13_dec_keep, &keep) < 0) {
			free (b); bad++;
			LOG ("WARN K13 DEC aac: %s did not read / its decoder did not open -- not compared", file);
			continue;
		}
		free (b);
		frames += r.frames;
		int eq = r.fnv == rfnv && r.frames == rfr;
		if (eq) same++; else diff++;
		if (o) fprintf (o, "%s\t%d\t%d\t%u\t%016llx\t%u\t%u\t%u\n", file, ch, rate, r.frames, r.fnv, r.peak, r.aus, r.refused);
		if (!eq && keep.kept) {
			char q [300];
			snprintf (q, sizeof q, "%s/dec-aac-%u.pcm", logs_dir (), n);
			FILE *f = fopen (q, "wb");
			if (f) { fwrite (keep.keep, 2, keep.kept, f); fclose (f); }
		}
	}
	if (o) fclose (o);
	free (pcm); free (keep.keep); free (d);
	if (v->lib_term) v->lib_term ();   /* (shepaudio initialises it again when the engine starts it) */
	LOG ("K13 DEC aac: %u takes decoded by sceAudiodec (%llu frames) in %.1f s: %u of %u = the Mac's (AudioToolbox)%s; %u differ%s%s -> %s",
	     n - bad, frames, (double) (sceKernelGetProcessTimeWide () - t0) / 1e6, same, n, same == n && n ? " -- the device plays what the Mac's analyses heard" : "",
	     diff, diff ? " (their first 64 KiB of samples in dec-aac-<n>.pcm: vita/tools/deccheck_aac_ref.py compare gives how far)" : "",
	     bad ? " -- SOME TAKES NOT COMPARED (unreadable / no decoder)" : "", out);
}
#endif
#define K13_VPK_ILZ "app0:managed-ilz"
#define K13_VPK_MANAGED "app0:managed"
/* review fix (the window sat at the pack's 100 % for the whole compressed-code part, ~72 % of the run's bytes, and could not be stopped): the
 * engine now asks in batches -- win->skip files passed over first, then at most win->left checked (left < 0: all) -- and between batches moves
 * the window's progress and honours its stop; count_only = op 2's sizes (each form's size + its original's, nothing read) */
typedef struct { int skip, left, count_only; } K13IlzWin;
static int k13_ilz_dir (const char *dir, const char *orig_dir, int *files, int *bad, unsigned long long *bytes, char *first, int cap, K13IlzWin *win)
{
	SceUID d = sceIoDopen (dir);
	if (d < 0)
		return 0;   /* (no such folder: nothing to check there) */
	SceIoDirent e;
	for (;;) {
		if (win->left == 0)
			break;
		memset (&e, 0, sizeof e);
		if (sceIoDread (d, &e) <= 0)
			break;
		size_t n = strlen (e.d_name);
		if (n < 5 || strcmp (e.d_name + n - 4, ".dll") || SCE_S_ISDIR (e.d_stat.st_mode))
			continue;
		if (win->skip > 0) { win->skip--; continue; }
		if (win->left > 0) win->left--;
		char p [320], o [320];
		snprintf (p, sizeof p, "%s/%s", dir, e.d_name);
		snprintf (o, sizeof o, "%s/%s", orig_dir, e.d_name);
		if (win->count_only) {
			SceIoStat os;
			memset (&os, 0, sizeof os);
			(*files)++;
			*bytes += (unsigned long long) e.d_stat.st_size + (sceIoGetstat (o, &os) >= 0 ? (unsigned long long) os.st_size : 0ull);
			continue;
		}
		unsigned hoff = 0, hsize = 0, hcrc = 0, fcrc = 0, flag = 0, ocrc = 0, ozs = 0, zcrc = 0, zs = 0;
		int layout = 0;
		const char *why = NULL;
		if (!k10a_ilz_hdr (p, &hoff, &hsize, &hcrc, &fcrc, &flag, &layout))
			why = "no readable SHPILZ1 header";
		else if (flag != 1)
			why = "no file CRC-32 in its header";
		else if (!layout)
			why = "a header layout this runtime does not read";
		else if (!k10a_crc_file (o, ~0u, 0, &ocrc, &ozs))
			why = "its original (managed/) is missing or unreadable";
		else if (ozs != hsize || ocrc != hcrc)
			why = "made from another original (size / CRC-32): a stale pair";
		else if (!k10a_crc_file (p, hoff + 56, 0, &zcrc, &zs) || zcrc != fcrc)
			why = "damaged (its own CRC-32)";
		(*files)++;
		*bytes += (unsigned long long) ozs + zs;
		if (why) {
			(*bad)++;
			if (first && !first [0])
				snprintf (first, (size_t) cap, "%s: %s", p, why);
			LOG ("WARN K13 verify files: %s -- %s", p, why);
		}
	}
	sceIoDclose (d);
	return 1;
}
int shepherd_vita_k13_bootc (int op, int *io, char *buf, int cap);
int shepherd_vita_k13_bootc (int op, int *io, char *buf, int cap)
{
	if ((op == 1 || op == 2) && io) {
		if (!&mono_shepherd_k10_ilz_images)
			return -1;   /* (a runtime without compressed IL: no managed-ilz/ is ever read, nothing to check) */
		/* review fix: io [1] = the first file (over the folders in order), io [2] = at most this many (0 = all: the first build's one call);
		 * op 2 = the sizes only (io [0] files, io [2] KiB to read: each form + its original), for the window's progress */
		int which = io [0] ? io [0] : 3, files = 0, bad = 0;
		K13IlzWin win = { io [1] > 0 ? io [1] : 0, io [2] > 0 ? io [2] : -1, op == 2 };
		const int batch = io [1] > 0 || io [2] > 0;
		unsigned long long bytes = 0;
		if (buf && cap > 0)
			buf [0] = 0;
		SceUInt64 t0 = sceKernelGetProcessTimeWide ();
		if (which & 1)
			k13_ilz_dir (K13_VPK_ILZ, K13_VPK_MANAGED, &files, &bad, &bytes, buf, cap, &win);
		if (which & 2)
			k13_ilz_dir (K10A_ILZ_DIR, GAME_MANAGED, &files, &bad, &bytes, buf, cap, &win);
		io [0] = files; io [1] = bad; io [2] = (int) (bytes / 1024u); io [3] = (int) ((sceKernelGetProcessTimeWide () - t0) / 1000u);
		if (op == 2)
			LOG ("K13 verify files: %d compressed code file(s) to read (app0:managed-ilz/ + managed-ilz/: %llu KiB with their originals)", files, bytes / 1024u);
		else if (!batch || bad)
			LOG ("K13 verify files: %d compressed code file(s) read in full (app0:managed-ilz/ + managed-ilz/: %llu KiB with their originals, %d ms): %d bad%s%s",
			     files, bytes / 1024u, io [3], bad, bad && buf ? " -- first: " : "", bad && buf ? buf : "");
		return 0;
	}
	return -2;   /* (an op this host does not know) */
}
#endif
