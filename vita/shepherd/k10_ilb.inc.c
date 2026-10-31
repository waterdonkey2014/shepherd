/* SHEPHERD-VITA device kit #10, track boot (vita/notes/kit10-boot.md): GAME CODE IN ONE BUNDLE, IN BOOT ORDER (owner 2026-10-19: "one open
 * and one straight read instead of 109 file lookups"). Included by host.c after k10_boot.inc.c; preload_hook asks k10_ilb_take first.
 *
 * Kit #9 on the owner's Vita: a boot reads ~109 assemblies (~43 MB) one file at a time, each a directory lookup (sceIoGetstat), an open, its
 * reads and a close, ON THE BOOT'S OWN THREAD at the moment Mono asks for it -- the card's time (~10 MB/s: ~4 s of reads) adds to the CPU's.
 *  THE BUNDLE (owner-local, staged with the card's managed/ folder: vita/tools/il_bundle.py, stage-owner.sh STAGE_ILBUNDLE=1):
 *   ux0:data/shepherd/managed/boot.ilb -- the card's own DLLs that a boot loads (the game's and the Unity modules; the VPK's engine / BCL stay
 *   in app0:), byte for byte, in the order a boot asks for them (a run's assemblies.txt), each 16-byte aligned, with an index (name, offset,
 *   size, FNV-1a of the bytes) and a header checksum.
 *  THE READ: before the Mono thread starts, one thread (the latency-tolerant workers' core: CPU3 when the 4th core is open, else USER_2)
 *   opens the bundle ONCE and reads it straight through, each DLL into its own buffer exactly as preload_hook would (the IL pool first, then
 *   the newlib heap -- the same memory as kit #9), checking every DLL's FNV-1a as it lands. preload_hook takes a DLL from there: ready =
 *   no file call at all; still on its way = it waits (in a GC-safe region); a DLL the bundle does not hold, or one that failed its check,
 *   loads from its own file exactly as before. So the card is read in one sequential stream WHILE Mono works, not file by file in its way.
 *  FRESHNESS (results only, verified on first reuse): a bundle is used only when cache/il/k10-ilb.ok names this bundle's checksum
 *   and the managed/ folder's listing key (every *.dll's name, size and modification time, from ONE directory listing) -- any DLL replaced,
 *   added or removed changes the key and the boot reads the loose files (kit #9's way) while it VERIFIES: every DLL it loads that the bundle
 *   holds is compared (size + FNV-1a) with the bundle's entry; a start whose every comparison held writes the .ok at its end, the next start
 *   uses the bundle. A stale or damaged bundle can only make a boot slower, never load other bytes.
 * Switches (env file): SHEPHERD_K10_ILBUNDLE=0 = kit #9 (every DLL from its own file; no thread, no listing); =verify = always the loose files
 * + the comparison (and the .ok); _FILE=<path>. SHEPHERD_K9_FLOATPC (another DLL folder; also asked for by SHEPHERD_STRICT_ORIGINAL=1 since
 * kit #10) turns the bundle off -- host.c's main runs k9_floatpc_config right before k10_ilb_start (kit #10 integration review: it ran in
 * the Mono thread, after this start, so the check below never fired and a verified bundle served managed/'s originals under FloatPc).
 * KIT #10 REVIEW FIXES (vita/notes/kit10-boot.md "Review fixes"):
 *  findings 2 + 3 -- VERIFY needs EVERY bundled DLL compared (each once: the entry's state marks it), not "at least one and none different":
 *   a start that loaded only some of them (an early exit, a short mode) proves nothing about the rest and writes no .ok. And the .ok is
 *   written THE MOMENT the last bundled DLL compared equal (inside preload_hook, GC-safe: with the Main Menu -- the last bundled DLL loads
 *   with the Main Menu scene, vlx b4: .ok at 75.6 s, the Main Menu at f1182 75.3 s), not at the Mono
 *   thread's end -- the play apps' endless boot is mostly closed from LiveArea (the process killed), which never reached that end, so every
 *   start stayed in VERIFY (kit #9's loose files + the compare's FNV pass). Bundled names the VPK also has (never served) are not counted.
 *  finding 4 -- k10_ilb_take hands a READY buffer out with a compare-and-swap (READY -> TAKEN): two threads asking for the same DLL at once
 *   can no longer both get it (the loser freed the winner's live image); the loser reads the DLL's own file, as kit #9.
 * KIT #10 INTEGRATION (boot x aot, vita/notes/kit10-integration.md): an entry may hold the DLL's COMPRESSED-IL form (the aot track's
 *  managed-ilz/<name>.dll; il_bundle.py --ilz, flags bit 0). The bundle never changes which bytes load: preload_hook serves an entry only in
 *  the form the loader would read without the bundle THIS start (k10a_ilz_path: SHEPHERD_K10_ILZ on, the ILZ file present and verified
 *  against its original); any other form = the loose file, exactly as without the bundle. VERIFY compares an entry only with a loose file
 *  of its own form (another form this start: no compare, no .ok); the listing key covers managed-ilz/ for the ILZ entries (a bundle without
 *  ILZ entries keys exactly as the boot track's). Before this fix the hook took a bundled ORIGINAL and then switched its path / size to the
 *  ILZ file (an image opened with the wrong length), and VERIFY compared ILZ bytes with original entries (never an .ok). */
#include <psp2/io/dirent.h>
#define K10_ILB_MAGIC 0x31424c49u      /* "ILB1" */
#define K10_ILB_DEFAULT GAME_MANAGED "/boot.ilb"
#define K10_ILB_OK DIR_ "/cache/il/k10-ilb.ok"
#define K10_ILB_MAX 256u
typedef struct { unsigned magic, version, count, index_off, data_off, data_bytes, index_fnv, checksum; } K10IlbHeader;
typedef struct { char name [48]; unsigned off, size, fnv, reserved; } K10IlbEntry;   /* 64 B; name NUL-terminated, without ".dll"; reserved = flags */
#define K10ILB_F_ILZ 1u   /* kit #10 integration: the entry is managed-ilz/<name>.dll (the compressed-IL form), not managed/<name>.dll */
typedef char k10_ilb_hdr_check [(sizeof (K10IlbHeader) == 32u) ? 1 : -1];
typedef char k10_ilb_ent_check [(sizeof (K10IlbEntry) == 64u) ? 1 : -1];
enum { K10ILB_PENDING = 0, K10ILB_READING, K10ILB_READY, K10ILB_TAKEN, K10ILB_FAILED };
static struct {
	int mode;                      /* 0 off, 1 use (the thread reads), 2 verify (loose files compared) */
	const char *why;
	char why_buf [160];
	char path [128];
	K10IlbHeader h;
	K10IlbEntry *e;                /* the index (newlib) */
	volatile int *state;
	void *volatile *buf;           /* the DLL's buffer once READY */
	unsigned dirkey, okkey_bundle, okkey_dir;
	SceUID thread, sema;
	volatile int stop;
	volatile unsigned read_n, read_bytes, taken, waited, waited_us, misses, bad_fnv, verified, verify_bad, pool_n;
	volatile unsigned ilz_entries, ilz_taken, form_other, form_noverify;   /* kit #10 integration (boot x aot): above */
	unsigned long long t_start, t_end;
	/* review fixes (findings 2 + 3): VERIFY's target (the bundled DLLs the card serves: the count less the VPK's names) and the .ok's moment */
	unsigned vpk_dups, verify_target, lost;
	int ok_written;                /* 1 = written in this start, -1 = the write failed */
	unsigned long long t_ok;
} g_ilb;

static unsigned k10_ilb_fnv (const void *p, unsigned n, unsigned h)
{
	const unsigned char *b = (const unsigned char *) p;
	for (unsigned i = 0; i < n; i++) { h ^= b [i]; h *= 16777619u; }
	return h;
}
static int k10_ilb_find (const char *name);
/* the managed/ folder's key for the bundle: over every file the bundle holds -- its name, size and modification time from ONE directory
 * listing (the sum of each one's FNV-1a: independent of the listing's order) and how many of them the folder has; files the bundle does not
 * hold (the build's own Derived DLL, DLLs no boot loads) do not count */
static unsigned k10_ilb_dirkey (unsigned *count)
{
	unsigned sum = 0, n = 0;
	SceUID d = sceIoDopen (GAME_MANAGED);
	if (d < 0) return 0;
	SceIoDirent de;
	char nm [64];
	for (;;) {
		memset (&de, 0, sizeof de);
		if (sceIoDread (d, &de) <= 0) break;
		size_t l = strlen (de.d_name);
		if (l < 5 || l - 4 >= sizeof nm || strcmp (de.d_name + l - 4, ".dll")) continue;
		memcpy (nm, de.d_name, l - 4); nm [l - 4] = 0;
		if (k10_ilb_find (nm) < 0) continue;
		unsigned h = k10_ilb_fnv (nm, (unsigned) (l - 4), 2166136261u);
		unsigned long long sz = (unsigned long long) de.d_stat.st_size;
		h = k10_ilb_fnv (&sz, sizeof sz, h);
		h = k10_ilb_fnv (&de.d_stat.st_mtime, sizeof de.d_stat.st_mtime, h);
		sum += h; n++;
	}
	sceIoDclose (d);
	if (g_ilb.ilz_entries) {   /* kit #10 integration: the ILZ entries' own files (name, size, modification time) in managed-ilz/ */
		unsigned nz = 0;
		d = sceIoDopen (GAME_MANAGED "-ilz");
		for (; d >= 0;) {
			memset (&de, 0, sizeof de);
			if (sceIoDread (d, &de) <= 0) break;
			size_t l = strlen (de.d_name);
			if (l < 5 || l - 4 >= sizeof nm || strcmp (de.d_name + l - 4, ".dll")) continue;
			memcpy (nm, de.d_name, l - 4); nm [l - 4] = 0;
			int i = k10_ilb_find (nm);
			if (i < 0 || !(g_ilb.e [i].reserved & K10ILB_F_ILZ)) continue;
			unsigned h = k10_ilb_fnv ("ilz/", 4u, 2166136261u);
			h = k10_ilb_fnv (nm, (unsigned) (l - 4), h);
			unsigned long long sz = (unsigned long long) de.d_stat.st_size;
			h = k10_ilb_fnv (&sz, sizeof sz, h);
			h = k10_ilb_fnv (&de.d_stat.st_mtime, sizeof de.d_stat.st_mtime, h);
			sum += h; nz++;
		}
		if (d >= 0) sceIoDclose (d);
		if (nz != g_ilb.ilz_entries) n = 0;   /* (an ILZ entry's file is missing: the caller's count check refuses the bundle) */
		sum = k10_ilb_fnv (&nz, sizeof nz, sum);
	}
	if (count) *count = n;
	sum = k10_ilb_fnv (&n, sizeof n, sum);
	return sum ? sum : 1u;
}
static int k10_ilb_find (const char *name)
{
	for (unsigned i = 0; i < g_ilb.h.count; i++)
		if (!strcmp (g_ilb.e [i].name, name)) return (int) i;
	return -1;
}
/* a buffer for an IL image, as read_file_ex takes one: the IL pool first (lock-free: il_pool_alloc), then the newlib heap */
static void *k10_ilb_alloc (unsigned n)
{
	char *b = (char *) il_pool_alloc (n + 1);
	if (b) { __atomic_add_fetch (&g_il_pool_files, 1, __ATOMIC_RELAXED); g_ilb.pool_n++; }
	else b = (char *) malloc (n + 1);
	return b;
}
static void k10_ilb_release (void *b, unsigned n)
{
	if (in_il_pool (b)) {   /* rewound when it is the pool's last allocation; else the space stays used */
		il_pool_rewind ((char *) b, n + 1);
		__atomic_sub_fetch (&g_il_pool_files, 1, __ATOMIC_RELAXED);
	} else
		free (b);
}
static int k10_ilb_main (SceSize args, void *argp)
{
	(void) args; (void) argp;
	SceUID fd = sceIoOpen (g_ilb.path, SCE_O_RDONLY, 0);
	for (unsigned i = 0; i < g_ilb.h.count && !g_ilb.stop; i++) {
		if (!__sync_bool_compare_and_swap (&g_ilb.state [i], K10ILB_PENDING, K10ILB_READING))
			continue;   /* (preload_hook took it first: it reads that one itself) */
		const K10IlbEntry *e = &g_ilb.e [i];
		char *b = fd >= 0 ? (char *) k10_ilb_alloc (e->size) : NULL;
		unsigned done = 0;
		if (b && sceIoLseek (fd, (SceOff) g_ilb.h.data_off + e->off, SCE_SEEK_SET) >= 0)
			while (done < e->size) {
				int r = sceIoRead (fd, b + done, e->size - done > 1048576u ? 1048576u : e->size - done);
				if (r <= 0) break;
				done += (unsigned) r;
			}
		int ok = b && done == e->size && k10_ilb_fnv (b, e->size, 2166136261u) == e->fnv;
		if (ok) {
			b [e->size] = 0;
			g_ilb.buf [i] = b;
			g_ilb.read_n++; g_ilb.read_bytes += e->size;
			__sync_synchronize ();
			g_ilb.state [i] = K10ILB_READY;
		} else {
			if (b) { k10_ilb_release (b, e->size); if (done == e->size) g_ilb.bad_fnv++; }
			g_ilb.state [i] = K10ILB_FAILED;   /* preload_hook reads the DLL's own file */
		}
		sceKernelSignalSema (g_ilb.sema, 1);
	}
	if (fd >= 0) sceIoClose (fd);
	g_ilb.t_end = sceKernelGetProcessTimeWide ();
	sceKernelSignalSema (g_ilb.sema, 1);
	sceKernelExitThread (0);
	return 0;
}

/* main, before the Mono thread exists */
static void k10_ilb_start (void)
{
	memset (&g_ilb, 0, sizeof g_ilb);
	g_ilb.thread = g_ilb.sema = -1;
	const char *m = getenv ("SHEPHERD_K10_ILBUNDLE");
	if (m && !strcmp (m, "0")) { LOG ("K10 IL bundle OFF (SHEPHERD_K10_ILBUNDLE=0): every DLL from its own file, as kit #9"); return; }
	if (g_k9_fpc_dir [0]) { LOG ("K10 IL bundle: not used -- SHEPHERD_K9_FLOATPC names another DLL folder"); return; }
	const char *p = getenv ("SHEPHERD_K10_ILBUNDLE_FILE");
	snprintf (g_ilb.path, sizeof g_ilb.path, "%s", p && *p ? p : K10_ILB_DEFAULT);
	SceUInt64 t0 = sceKernelGetProcessTimeWide ();
	SceUID fd = sceIoOpen (g_ilb.path, SCE_O_RDONLY, 0);
	if (fd < 0) { LOG ("K10 IL bundle: none on the card (%s: stage-owner.sh STAGE_ILBUNDLE=1 makes it): every DLL from its own file, as kit #9", g_ilb.path); return; }
	const char *bad = NULL;
	if (sceIoRead (fd, &g_ilb.h, sizeof g_ilb.h) != (int) sizeof g_ilb.h || g_ilb.h.magic != K10_ILB_MAGIC || g_ilb.h.version != 1u)
		bad = "not an IL bundle (magic / version)";
	else if (k10_ilb_fnv (&g_ilb.h, 28u, 2166136261u) != g_ilb.h.checksum)
		bad = "header checksum (damaged)";
	else if (!g_ilb.h.count || g_ilb.h.count > K10_ILB_MAX || g_ilb.h.index_off != 32u || g_ilb.h.data_off < 32u + g_ilb.h.count * 64u)
		bad = "index outside the file";
	if (!bad) {
		g_ilb.e = (K10IlbEntry *) malloc (g_ilb.h.count * sizeof (K10IlbEntry));
		g_ilb.state = (volatile int *) calloc (g_ilb.h.count, sizeof (int));
		g_ilb.buf = (void *volatile *) calloc (g_ilb.h.count, sizeof (void *));
		if (!g_ilb.e || !g_ilb.state || !g_ilb.buf) bad = "no memory for the index";
		else if (sceIoRead (fd, g_ilb.e, g_ilb.h.count * 64u) != (int) (g_ilb.h.count * 64u) || k10_ilb_fnv (g_ilb.e, g_ilb.h.count * 64u, 2166136261u) != g_ilb.h.index_fnv)
			bad = "index checksum (damaged)";
		else
			for (unsigned i = 0; i < g_ilb.h.count && !bad; i++)
				if (g_ilb.e [i].name [47] || !g_ilb.e [i].size || g_ilb.e [i].off > g_ilb.h.data_bytes || g_ilb.e [i].size > g_ilb.h.data_bytes - g_ilb.e [i].off)
					bad = "an entry outside the data";
				else if (g_ilb.e [i].reserved & ~K10ILB_F_ILZ)
					bad = "an entry with flags this host does not read";
				else if (g_ilb.e [i].reserved & K10ILB_F_ILZ)
					g_ilb.ilz_entries++;   /* (kit #10 integration: the compressed-IL form) */
	}
	sceIoClose (fd);
	if (bad) {
		LOG ("K10 IL bundle: %s refused (%s): every DLL from its own file, as kit #9", g_ilb.path, bad);
		free (g_ilb.e); free ((void *) g_ilb.state); free ((void *) g_ilb.buf); g_ilb.e = NULL; g_ilb.state = NULL; g_ilb.buf = NULL;
		return;
	}
	unsigned ndll = 0;
	g_ilb.dirkey = k10_ilb_dirkey (&ndll);
	if (ndll != g_ilb.h.count) {   /* (a bundled DLL is missing from the folder: the bundle is for another card) */
		LOG ("K10 IL bundle: %s holds %u DLLs (%u in their compressed-IL form), managed/ has %u of them%s: not used (re-stage it); every DLL from its own file, as kit #9", g_ilb.path, g_ilb.h.count, g_ilb.ilz_entries, ndll, g_ilb.ilz_entries ? " or managed-ilz/ lacks one of the ILZ forms" : "");
		free (g_ilb.e); free ((void *) g_ilb.state); free ((void *) g_ilb.buf); g_ilb.e = NULL; g_ilb.state = NULL; g_ilb.buf = NULL;
		return;
	}
	/* preload_hook looks in app0:managed (the VPK) FIRST: a bundled name the VPK also has must keep coming from the VPK -- one listing of
	 * app0:managed marks such entries as never served (none today: the bundle holds only DLLs a boot loaded from the card). Review fix
	 * (findings 2 + 3): listed before the mode is chosen -- VERIFY does not count those entries either */
	unsigned vpk_dups = 0;
	{
		SceUID d = sceIoDopen (VPK_MANAGED);
		SceIoDirent de;
		char nm [64];
		for (; d >= 0;) {
			memset (&de, 0, sizeof de);
			if (sceIoDread (d, &de) <= 0) break;
			size_t l = strlen (de.d_name);
			if (l < 5 || l - 4 >= sizeof nm || strcmp (de.d_name + l - 4, ".dll")) continue;
			memcpy (nm, de.d_name, l - 4); nm [l - 4] = 0;
			int i = k10_ilb_find (nm);
			if (i >= 0) { g_ilb.state [i] = K10ILB_FAILED; vpk_dups++; }
		}
		if (d >= 0) sceIoDclose (d);
		if (vpk_dups) LOG ("K10 IL bundle: %u bundled DLL(s) are also in the VPK (app0:managed): those keep coming from the VPK, as kit #9", vpk_dups);
	}
	g_ilb.vpk_dups = vpk_dups;
	g_ilb.verify_target = g_ilb.h.count - vpk_dups;
	char ok [64] = "";
	{
		SceUID o = sceIoOpen (K10_ILB_OK, SCE_O_RDONLY, 0);
		if (o >= 0) { int r = sceIoRead (o, ok, sizeof ok - 1); sceIoClose (o); if (r > 0) ok [r] = 0; }
	}
	unsigned ob = 0, od = 0;
	int have_ok = sscanf (ok, "%x %x", &ob, &od) == 2 && ob == g_ilb.h.checksum && od == g_ilb.dirkey;
	int verify = m && !strcmp (m, "verify");
	if (!have_ok || verify || !g_ilb.verify_target) {
		g_ilb.mode = g_ilb.verify_target ? 2 : 0;
		if (!g_ilb.mode) {
			LOG ("K10 IL bundle: every bundled DLL is also in the VPK: not used; every DLL as kit #9");
			free (g_ilb.e); free ((void *) g_ilb.state); free ((void *) g_ilb.buf); g_ilb.e = NULL; g_ilb.state = NULL; g_ilb.buf = NULL;
			return;
		}
		LOG ("K10 IL bundle: VERIFY -- %s (%u DLLs, %u KiB) %s: this start reads every DLL from its own file and compares each the bundle holds; ALL %u equal = "
		     "cache/il/k10-ilb.ok, written the moment the last one compared (then the next start reads the bundle) -- a start that loads only some of them writes "
		     "nothing (managed/ holds all %u, key %08x; %.0f ms)", g_ilb.path, g_ilb.h.count, g_ilb.h.data_bytes / 1024u,
		     verify ? "(SHEPHERD_K10_ILBUNDLE=verify)" : ok [0] ? "is not the verified one (the .ok names another bundle or folder listing)" : "is not verified yet on this card",
		     g_ilb.verify_target, ndll, g_ilb.dirkey, (double) (sceKernelGetProcessTimeWide () - t0) / 1000.0);
		return;
	}
	/* kit #10 integration (boot x aot): USE reads only the entries whose form the loader reads this start -- an ILZ entry while compressed IL
	 * is off (SHEPHERD_K10_ILZ=0, no managed-ilz/, a runtime without it), an original while its ILZ form will be read instead (compressed IL on
	 * and managed-ilz/ holds it) -- the others are never read (no pool space spent on bytes the hook would give back) and load from their files */
	{
		int ilz_on = k10a_ilz_on_now ();
		unsigned char *has = ilz_on ? (unsigned char *) calloc (g_ilb.h.count, 1) : NULL;
		if (has) {
			SceUID d = sceIoDopen (GAME_MANAGED "-ilz");
			SceIoDirent de;
			char nm [64];
			for (; d >= 0;) {
				memset (&de, 0, sizeof de);
				if (sceIoDread (d, &de) <= 0) break;
				size_t l = strlen (de.d_name);
				if (l < 5 || l - 4 >= sizeof nm || strcmp (de.d_name + l - 4, ".dll")) continue;
				memcpy (nm, de.d_name, l - 4); nm [l - 4] = 0;
				int i = k10_ilb_find (nm);
				if (i >= 0 && strncmp (nm, "Shepherd.", 9)) has [i] = 1;
			}
			if (d >= 0) sceIoDclose (d);
		}
		unsigned other = 0, usable = 0;
		for (unsigned i = 0; i < g_ilb.h.count; i++) {
			if (g_ilb.state [i] == K10ILB_FAILED) continue;   /* (a VPK name) */
			int want_ilz = has && has [i];
			if (((g_ilb.e [i].reserved & K10ILB_F_ILZ) ? 1 : 0) != want_ilz) { g_ilb.state [i] = K10ILB_FAILED; other++; }
			else usable++;
		}
		free (has);
		if (other)
			LOG ("K10 IL bundle: %u bundled DLL(s) are the other form than the loader reads this start (compressed IL %s; the bundle holds %u compressed-IL entries): those load from their own files",
			     other, ilz_on ? "ON" : "off", g_ilb.ilz_entries);
		if (!usable) {
			LOG ("K10 IL bundle: no bundled DLL is in the form this start reads: not used; every DLL from its own file, as kit #9 (re-make it with the card's managed-ilz/: stage-owner.sh STAGE_ILBUNDLE)");
			free (g_ilb.e); free ((void *) g_ilb.state); free ((void *) g_ilb.buf); g_ilb.e = NULL; g_ilb.state = NULL; g_ilb.buf = NULL;
			return;
		}
	}
	g_ilb.sema = sceKernelCreateSema ("shepherd-k10-ilb", 0, 0, 1 << 20, NULL);
	g_ilb.mode = 1;
	g_ilb.t_start = sceKernelGetProcessTimeWide ();
	int mask = shepherd_vita_worker_mask ();
	g_ilb.thread = sceKernelCreateThread ("shepherd-k10-ilb", k10_ilb_main, 0x10000100, 0x4000, 0, mask ? mask : 0x40000, NULL);
	if (g_ilb.sema < 0 || g_ilb.thread < 0 || sceKernelStartThread (g_ilb.thread, 0, NULL) < 0) {
		g_ilb.mode = 0;
		LOG ("K10 IL bundle: no reader thread (0x%08x): every DLL from its own file, as kit #9", (unsigned) g_ilb.thread);
		return;
	}
	LOG ("K10 IL bundle ON: %s -- %u DLLs, %u KiB in boot order, read in ONE stream by a thread on %s while Mono works (verified on this card: the .ok matches the "
	     "bundle %08x and managed/'s listing %08x, %u DLLs; %.0f ms; SHEPHERD_K10_ILBUNDLE=0 = kit #9's file per DLL)", g_ilb.path, g_ilb.h.count, g_ilb.h.data_bytes / 1024u,
	     mask == 0x80000 ? "CPU3" : "USER_2", g_ilb.h.checksum, g_ilb.dirkey, ndll, (double) (sceKernelGetProcessTimeWide () - t0) / 1000.0);
}

/* preload_hook, before anything else for a name: 1 = *data / *size hold the DLL from the bundle (path = its loose file's, as Mono would
 * name it); 0 = not from the bundle (load it as kit #9 does). Called WITHOUT load_lock. */
static int k10_ilb_take (const char *name, void **data, unsigned *size, char *path, size_t pathn, int *ilz)
{
	if (g_ilb.mode != 1 || g_k9_fpc_dir [0])   /* (integration review backstop: never with another DLL folder -- main decides it before the start) */
		return 0;
	int i = k10_ilb_find (name);
	if (i < 0) { g_ilb.misses++; return 0; }
	if (__sync_bool_compare_and_swap (&g_ilb.state [i], K10ILB_PENDING, K10ILB_TAKEN))
		return 0;   /* the thread has not come to it: this load reads the file itself (the thread skips it) */
	if (g_ilb.state [i] == K10ILB_READING) {
		SceUInt64 w0 = sceKernelGetProcessTimeWide ();
		SAFE_BEGIN ();
		while (g_ilb.state [i] == K10ILB_READING) {
			SceUInt to = 100000;
			sceKernelWaitSema (g_ilb.sema, 1, &to);
		}
		SAFE_END ();
		g_ilb.waited++; g_ilb.waited_us += (unsigned) (sceKernelGetProcessTimeWide () - w0);
	}
	/* review fix (finding 4): READY -> TAKEN in one compare-and-swap -- the one thread that wins owns the buffer; another asking for the same
	 * DLL at the same moment (FAILED, TAKEN, or lost the swap) reads its own file, as kit #9 (load_lock's mono_assembly_loaded check then
	 * frees that copy) */
	if (!__sync_bool_compare_and_swap (&g_ilb.state [i], K10ILB_READY, K10ILB_TAKEN)) {
		if (g_ilb.state [i] == K10ILB_TAKEN) __sync_fetch_and_add (&g_ilb.lost, 1u);
		return 0;
	}
	__sync_synchronize ();
	*data = g_ilb.buf [i];
	*size = g_ilb.e [i].size;
	g_ilb.buf [i] = NULL;
	__sync_fetch_and_add (&g_ilb.taken, 1u);
	snprintf (path, pathn, GAME_MANAGED "/%s.dll", name);   /* (the ORIGINAL's path, as Mono names it: Assembly.Location; preload_hook checks the form) */
	*ilz = (g_ilb.e [i].reserved & K10ILB_F_ILZ) ? 1 : 0;
	return 1;
}
static void k10_ilb_write_ok (void);
/* kit #10 integration (boot x aot): preload_hook gave a taken entry back -- it is the other form than the loader reads this start */
static void k10_ilb_other_form (void) { __sync_fetch_and_add (&g_ilb.form_other, 1u); __sync_fetch_and_sub (&g_ilb.taken, 1u); }
static void k10_ilb_ilz_taken (void) { __sync_fetch_and_add (&g_ilb.ilz_taken, 1u); }
/* preload_hook in VERIFY mode, after a loose file loaded (under load_lock): compare it with the bundle's entry -- each entry once (its state:
 * PENDING -> READY = equal, -> FAILED = different; the VPK's names are FAILED from the start and never counted). Review fixes (findings 2 +
 * 3): when the LAST of the verify_target entries compares equal (and none differed), the .ok is written right here */
static void k10_ilb_verify (const char *name, const void *data, unsigned size, int ilz)
{
	if (g_ilb.mode != 2 || g_k9_fpc_dir [0])   /* (integration review backstop: a FloatPc DLL is never compared with the bundle's original) */
		return;
	int i = k10_ilb_find (name);
	if (i < 0 || g_ilb.state [i] != K10ILB_PENDING) return;
	if (((g_ilb.e [i].reserved & K10ILB_F_ILZ) ? 1 : 0) != ilz) {   /* kit #10 integration: the loose file is the other form this start -- this
		g_ilb.state [i] = K10ILB_FAILED;                                 * start cannot vouch for the entry (no compare, so no .ok) */
		g_ilb.form_noverify++;
		return;
	}
	if (g_ilb.e [i].size == size && k10_ilb_fnv (data, size, 2166136261u) == g_ilb.e [i].fnv) { g_ilb.state [i] = K10ILB_READY; g_ilb.verified++; }
	else { g_ilb.state [i] = K10ILB_FAILED; g_ilb.verify_bad++; }
	if (!g_ilb.ok_written && !g_ilb.verify_bad && g_ilb.verified == g_ilb.verify_target)
		k10_ilb_write_ok ();
}
/* the .ok (results only: this bundle's checksum + managed/'s listing key), temp + rename; in a GC-safe region (card writes) */
static void k10_ilb_write_ok (void)
{
	char line [40];
	int n = snprintf (line, sizeof line, "%08x %08x\n", g_ilb.h.checksum, g_ilb.dirkey), good = 0;
	SAFE_BEGIN ();
	sceIoMkdir (DIR_ "/cache", 0777);
	sceIoMkdir (DIR_ "/cache/il", 0777);
	SceUID o = sceIoOpen (K10_ILB_OK ".tmp", SCE_O_WRONLY | SCE_O_CREAT | SCE_O_TRUNC, 0777);
	if (o >= 0) {
		int w = sceIoWrite (o, line, (SceSize) n);
		sceIoClose (o);
		if (w == n) { sceIoRemove (K10_ILB_OK); good = sceIoRename (K10_ILB_OK ".tmp", K10_ILB_OK) >= 0; } else sceIoRemove (K10_ILB_OK ".tmp");
	}
	SAFE_END ();
	g_ilb.ok_written = good ? 1 : -1;
	g_ilb.t_ok = sceKernelGetProcessTimeWide ();
	LOG ("K10 IL bundle VERIFY at %.3f s: all %u bundled DLLs this card serves compared equal -- %s", (double) g_ilb.t_ok / 1e6, g_ilb.verify_target,
	     good ? "cache/il/k10-ilb.ok written: the next start reads the bundle" : "cache/il/k10-ilb.ok could NOT be written: the next start verifies again");
}
/* the end of the run: the line; VERIFY: the .ok (results only) when every comparison held */
static void k10_ilb_end (void)
{
	if (g_ilb.mode == 1) {
		g_ilb.stop = 1;
		unsigned left = 0;
		for (unsigned i = 0; i < g_ilb.h.count; i++) if (g_ilb.state [i] == K10ILB_READY) left++;
		LOG ("K10 IL bundle: %u of %u DLLs read from the bundle (%u KiB, %.2f s of the thread's time from %.3f s), %u taken by Mono (%u waited for, %.2f s in all), "
		     "%u read from their own files (%u not in the bundle), %u failed their check, %u read but never asked for (their memory stays); %u asked for again while taken "
		     "(read from their own files)", g_ilb.read_n, g_ilb.h.count,
		     g_ilb.read_bytes / 1024u, g_ilb.t_end ? (double) (g_ilb.t_end - g_ilb.t_start) / 1e6 : -1.0, (double) g_ilb.t_start / 1e6, g_ilb.taken, g_ilb.waited,
		     g_ilb.waited_us / 1e6, g_ilb.misses, g_ilb.misses, g_ilb.bad_fnv, left, g_ilb.lost);
		if (g_ilb.ilz_entries || g_ilb.form_other)   /* kit #10 integration (boot x aot) */
			LOG ("K10 IL bundle: %u of its entries are the compressed-IL form (managed-ilz/), %u of them taken; %u taken entries given back -- the loader reads the other form this start (SHEPHERD_K10_ILZ / a stale ILZ file): their own files, as without the bundle",
			     g_ilb.ilz_entries, g_ilb.ilz_taken, g_ilb.form_other);
	} else if (g_ilb.mode == 2) {   /* review fixes (findings 2 + 3): the .ok was written (or not) when the last DLL compared -- here only the line */
		LOG ("K10 IL bundle VERIFY: %u of the %u bundled DLLs this card serves loaded in this start and compared: %u equal, %u DIFFERENT -- %s", g_ilb.verified + g_ilb.verify_bad,
		     g_ilb.verify_target, g_ilb.verified, g_ilb.verify_bad,
		     g_ilb.verify_bad ? "the bundle is NOT used (re-stage it: stage-owner.sh STAGE_ILBUNDLE=1)"
		     : g_ilb.ok_written > 0 ? "cache/il/k10-ilb.ok was written when the last one compared: the next start reads the bundle"
		     : g_ilb.ok_written < 0 ? "every one equal, but cache/il/k10-ilb.ok could not be written: the next start verifies again"
		     : g_ilb.form_noverify ? "some bundled DLLs loaded in their other form (compressed IL on / off: SHEPHERD_K10_ILZ, managed-ilz/): those cannot be compared -- no .ok"
		     : "NOT every bundled DLL was loaded in this start: no .ok (the next start verifies again)");
		if (g_ilb.form_noverify)   /* kit #10 integration (boot x aot) */
			LOG ("K10 IL bundle VERIFY: %u bundled DLL(s) loaded in the other form (the bundle holds %u compressed-IL entries; the loader read %s): not compared -- re-make the bundle with the card's managed-ilz/ (stage-owner.sh STAGE_ILBUNDLE) or keep SHEPHERD_K10_ILZ as when it was made",
			     g_ilb.form_noverify, g_ilb.ilz_entries, g_ilb.ilz_entries ? "originals" : "compressed-IL forms");
	}
}
