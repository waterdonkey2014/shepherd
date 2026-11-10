/* SHEPHERD-VITA device kit #12, track walkb (vita/notes/kit12-walkb.md): THE WALK, the C-heap side and the runtime -- the host's half.
 * Included by host.c after k11_walk.inc.c. Every row is an env-file switch back to kit #11; SHEPHERD_K12_WALKB=0 = kit #11 for all of them
 * (read once, where each row is first asked).
 *
 *   SHEPHERD_K12_TIERING=0      THE INTERPRETER WITHOUT TIERING (opt-in; default 1 = kit #11: Mono's interpreter tiering): Mono transforms a
 *                               method first without optimization and again, optimized, after 1,000 calls (tiering.c) -- the first copy
 *                               (its InterpMethod, code, data items, clauses, MonoJitInfo) stays in the load context's mempool for good,
 *                               and every call site naming a tier-0 method is remembered in a list until it tiers up. Off = every method
 *                               transformed once, optimized (Mono's own "-tiering" interpreter option, as Mono does for every wrapper
 *                               already): the same IL, the same results, one copy per method. What changes is CPU: a method's first call
 *                               transforms with the optimizer (slower once), its tier-0 runs are optimized code (faster). Not in the JIT
 *                               app (00097): its baseline JIT puts its entry points in optimized methods only, so tiering is part of its
 *                               design there (the line says so and the switch is ignored).
 *   SHEPHERD_K12_SEQPOINTS=0    THE INTERPRETER WITHOUT IL SEQUENCE POINTS (opt-in; default 1 = kit #11): by default Mono's interpreter records,
 *                               for every method it transforms, a sequence-point table (the IL offset of each statement boundary: a
 *                               compressed MonoSeqPointInfo of its own + an entry in the jit memory manager's seq_points table). Without a
 *                               debugger the only reader is a stack trace's IL offset ("[0x0001a]" in an exception's text, StackFrame's
 *                               GetILOffset). Off = Mono's own debug option "no-compact-seq-points" (MONO_DEBUG, read by mini_init): no table
 *                               per method; the game's code, its results and every frame are the same -- an exception's stack text then
 *                               shows interpreted frames without their IL offset (the engine's and the black box's exception lines lose that
 *                               detail: a diagnostic cost, hence opt-in). Not in the JIT app (as tiering).
 *   SHEPHERD_K12_VPKILZ=1       THE VPK'S OWN DLLs IN COMPRESSED-IL FORM (opt-in since the review; default 0 = kit #11: the originals): a build
 *                               with SHEPHERD_K12_VPKILZ=1 (build.sh) packs every VPK DLL's IL bodies as kit #10's ILZ form
 *                               (vita/tools/k10_ilz.py pack + verify at build time: every body checked) into app0:managed-ilz/ beside the
 *                               originals in app0:managed/; with the env line SHEPHERD_K12_VPKILZ=1 the preload hook reads the ILZ form when
 *                               the VPK has it (Assembly.Location stays app0:managed/<name>.dll), and kit #10's runtime patch (k10-ilz.patch)
 *                               unpacks a body the first time something asks for it -- exactly the original bytes (CRC-checked blocks; a
 *                               bad block fails the method loudly, never runs other IL). AOT-compiled methods' bodies are never read.
 *                               Unset / 0 = the originals (kit #11); a VPK without managed-ilz/ = kit #11 as is. (Review fix: the default
 *                               is OFF -- it trades boot CPU for C heap, and its device A/B waits on the kit #11 run: SHP_K12_VPKILZ_DEFAULT.)
 *                               Each pair is checked as kit #10 checks a card pair (review fix; k10a_ilz_verified): the header's layout,
 *                               the original's size AND CRC-32 as the header records them, the ILZ file's own CRC-32 -- or the stamp of an
 *                               earlier full check of exactly these two files (kit #10's stamps file, results only; the VPK's pairs under
 *                               "app0/<name>"). A stale or damaged ILZ form is never read: the original is (a WARN line; slower, never
 *                               different). The check's app0 / card reads and its lock wait run in a GC-safe region (kit #10 review rule).
 *   SHEPHERD_K12_TOC=0          (read by the engine: engine/Shepherd.Assets ObjectTable.View) THE ASSETS' INDEX ENTRIES AS A VIEW: a serialized
 *                               file's object table is a slice of the pack's TOC (32 bytes per object, kept whole by the pack reader anyway)
 *                               and an index entry (ObjectInfo) is a view over it -- kit #11 made one 40-byte object per entry (148,226 on the
 *                               intro walk, ~5.7 MiB of GC heap); every value read is the same. =0: one object per entry again (kit #11's
 *                               memory). The host only names it on its K12 WALKB line.
 *   SHEPHERD_K12_HDRCENSUS=1    (test only; slows every major) THE OBJECT-HEADER CENSUS: a heap walk after each major collection (the world
 *                               stopped) -- every object's size as sgen places it today (8-byte header: the vtable word + the sync word)
 *                               and as it would with a 4-byte header (the vtable word only; sync words in a side table), at sgen's 8-byte
 *                               alignment and in its size classes; and every object whose sync word is in use now (thin-locked, hashed,
 *                               inflated): the side table's entries. -> the "K12 HDR" end line + <logs>/k12-hdr.tsv (per class; the walk
 *                               with the largest heap).
 * Lines: "K12 WALKB" (at start: the switches), "K12 vpkilz" (end), "K12 HDR" (end, census only). */

#ifndef SHP_K12_VPKILZ_DEFAULT
#define SHP_K12_VPKILZ_DEFAULT 0    /* kit #11: the VPK's original DLLs even when it carries managed-ilz/ (SHEPHERD_K12_VPKILZ=1 = their ILZ forms:
                                       opt-in until the kit #11 run's boot numbers + a device A/B decide -- review fix) */
#endif
#ifndef SHP_K12_TIERING_DEFAULT
#define SHP_K12_TIERING_DEFAULT 1   /* kit #11: Mono's interpreter tiering (SHEPHERD_K12_TIERING=0 = every method optimized once: opt-in) */
#endif

static int k12w_master = -1;   /* SHEPHERD_K12_WALKB: -1 not read yet */
static int k12w_on (void)
{
	if (k12w_master < 0) {
		const char *e = getenv ("SHEPHERD_K12_WALKB");
		k12w_master = !(e && !strcmp (e, "0"));
	}
	return k12w_master;
}
static int k12w_env_bool (const char *k, int def)
{
	const char *e = getenv (k);
	if (!e || !*e)
		return def;
	return strcmp (e, "0") != 0;
}

/* ---------------------------------------------------------------- the interpreter's tiering (before mono_jit_init_version) */
#if !defined (SHEPHERD_MAC_CHECK)
extern const char *mono_interp_opts_string __attribute__ ((weak));   /* mini-runtime.c: the interpreter's options (driver.c's --interp=) */
#endif
static int k12w_tiering_off;
static void k12_walkb_pre_init (void)
{
	int want_off = k12w_on () && !k12w_env_bool ("SHEPHERD_K12_TIERING", SHP_K12_TIERING_DEFAULT);
	const char *why = "";
#if defined (SHEPHERD_JIT)
	if (want_off) {
		want_off = 0;
		why = " -- SHEPHERD_K12_TIERING=0 IGNORED in the JIT app (its baseline JIT tier puts its entry points in optimized methods: tiering is part of its design)";
	}
#endif
#if !defined (SHEPHERD_MAC_CHECK)
	if (want_off && !&mono_interp_opts_string) {
		want_off = 0;
		why = " -- SHEPHERD_K12_TIERING=0 IGNORED: this runtime has no mono_interp_opts_string";
	}
	if (want_off)
		mono_interp_opts_string = "-tiering";   /* read by mini_init -> mono_ee_interp_init (interp_parse_options): INTERP_OPT_TIERING cleared */
#else
	want_off = 0;
#endif
	k12w_tiering_off = want_off;
	/* SHEPHERD_K12_SEQPOINTS=0: MONO_DEBUG += no-compact-seq-points (mini_init -> mini_parse_debug_options; an env file's own MONO_DEBUG kept) */
	int seq_off = k12w_on () && !k12w_env_bool ("SHEPHERD_K12_SEQPOINTS", 1);
	const char *seq_why = "";
#if defined (SHEPHERD_JIT)
	if (seq_off) { seq_off = 0; seq_why = " -- SHEPHERD_K12_SEQPOINTS=0 IGNORED in the JIT app"; }
#endif
	if (seq_off) {
		const char *md = getenv ("MONO_DEBUG");
		char buf [256];
		if (md && *md && strstr (md, "no-compact-seq-points") == NULL)
			snprintf (buf, sizeof buf, "%s,no-compact-seq-points", md);
		else
			snprintf (buf, sizeof buf, "%s", md && *md ? md : "no-compact-seq-points");
		setenv ("MONO_DEBUG", buf, 1);
	}
	const char *toc = getenv ("SHEPHERD_K12_TOC");
	LOG ("K12 WALKB (kit #12 walkb; SHEPHERD_K12_WALKB=0 = kit #11): interpreter tiering %s%s | IL sequence points %s%s | VPK compressed IL %s | the assets' index entries %s | object-header census %s",
	     !k12w_on () ? "Mono's (kit #11; SHEPHERD_K12_WALKB=0)" : want_off ? "OFF (SHEPHERD_K12_TIERING=0: every method transformed once, optimized -- Mono's \"-tiering\")"
	     : "Mono's (kit #11: tier 0, then optimized after 1,000 calls)", why,
	     seq_off ? "OFF (SHEPHERD_K12_SEQPOINTS=0: MONO_DEBUG no-compact-seq-points -- no table per interpreted method; stack traces without IL offsets)"
	     : "kept (kit #11: a table per interpreted method)", seq_why,
	     !k12w_on () ? "off (SHEPHERD_K12_WALKB=0)" : k12w_env_bool ("SHEPHERD_K12_VPKILZ", SHP_K12_VPKILZ_DEFAULT) ? "ON when the VPK has app0:managed-ilz/ (SHEPHERD_K12_VPKILZ=1; each pair CRC-checked)"
		     : "off (kit #11's originals; SHEPHERD_K12_VPKILZ=1 = a VPK's app0:managed-ilz/)",
	     !k12w_on () || (toc && !strcmp (toc, "0")) ? "one object per entry (kit #11's; the engine reads SHEPHERD_K12_TOC / SHEPHERD_K12_WALKB)" : "a view over the object table (the engine's Shepherd.Assets ObjectTable)",
	     k12w_on () && k12w_env_bool ("SHEPHERD_K12_HDRCENSUS", 0) ? "ON (test: slows every major)" : "off");
}

#if !defined (SHEPHERD_MAC_CHECK)
/* ---------------------------------------------------------------- the VPK's own DLLs in compressed-IL form (the preload hook) */
#define K12_VPK_ILZ "app0:managed-ilz"
static int k12z_state = -1;   /* -1 not decided; 0 off; 1 on (the VPK has managed-ilz/, the runtime unpacks ILZ bodies, the switch on) -- under g_k10a_ilz_lock */
static unsigned k12z_files, k12z_orig_kib, k12z_kib, k12z_refused, k12z_verified, k12z_stamped, k12z_verify_ms;
/* (review fix) may app0:managed-ilz/<name>.dll stand in for app0:managed/<name>.dll (its size `osize`)? -- kit #10's check of a card pair
 * (k10a_ilz_verified), for the VPK's: the stamp of an earlier full check of exactly these two files (kit #10's stamps file, under the name
 * "app0/<name>": a card DLL of the same name never takes the VPK's line), else the full check now -- the header's layout and file-CRC flag,
 * the original's size AND CRC-32 as the header records them, the ILZ file's own CRC-32 -- then a stamp. The size alone let a stale pair
 * through: Roslyn pads PE files to 512 bytes, so two builds of a Shepherd.* DLL are often the same size (b1 / b2 / b3's Shepherd.Engine.dll:
 * three hashes, one size), and a stale ILZ form would run the old IL -- its MVID no longer the eboot AOT image's, so that whole image
 * interpreted, silently. NULL = use it; else why not. Under g_k10a_ilz_lock (the stamps), in a GC-safe region (the caller). */
static const char *k12z_verified_why (const char *name, const char *orig, unsigned osize, const char *p)
{
	SceIoStat so, sz;
	unsigned hcrc1k = 0, h1k = 0;
	if (sceIoGetstat (orig, &so) < 0 || sceIoGetstat (p, &sz) < 0 || !k10a_crc_file (orig, ~0u, 1024u, &hcrc1k, &h1k))
		return "unreadable";
	char sname [200], dto [32], dtz [32], key [320];
	snprintf (sname, sizeof (sname), "app0/%s", name);
	k10a_dt (dto, sizeof (dto), &so.st_mtime);
	k10a_dt (dtz, sizeof (dtz), &sz.st_mtime);
	snprintf (key, sizeof (key), K10A_STAMP_TAG "%s\t%u\t%s\t%08x\t%u\t%s", sname, (unsigned) so.st_size, dto, hcrc1k, (unsigned) sz.st_size, dtz);
	if (k10a_stamp_has (key)) {
		k12z_stamped++;
		return NULL;
	}
	unsigned hoff = 0, hsize = 0, hcrc = 0, fcrc = 0, flag = 0, ocrc = 0, ozs = 0, zcrc = 0, zs = 0;
	int layout = 0;
	const char *why = NULL;
	SceUInt64 t0 = sceKernelGetProcessTimeWide ();
	if (!k10a_ilz_hdr (p, &hoff, &hsize, &hcrc, &fcrc, &flag, &layout))
		why = "no readable SHPILZ1 header";
	else if (flag != 1)
		why = "no file CRC-32 in its header";
	else if (!layout)
		why = "a header layout this runtime does not read";
	else if (hsize != osize)
		why = "made from an original of another size";
	else if (!k10a_crc_file (orig, ~0u, 0, &ocrc, &ozs) || ozs != osize || ocrc != hcrc)
		why = "made from another original (CRC-32): a stale pair";
	else if (!k10a_crc_file (p, hoff + 56, 0, &zcrc, &zs) || zcrc != fcrc)
		why = "damaged (its own CRC-32)";
	k12z_verify_ms += (unsigned) ((sceKernelGetProcessTimeWide () - t0) / 1000u);
	if (why)
		return why;
	char line [400];
	snprintf (line, sizeof (line), "%s\t%08x\t%08x\n", key, ocrc, zcrc);
	k10a_stamp_add (sname, line);
	k12z_verified++;
	return NULL;
}
/* preload_hook, after the path is settled: an app0:managed/<name>.dll is read from app0:managed-ilz/<name>.dll when the switch is on, the VPK
 * carries one and the pair passes k12z_verified_why (Assembly.Location stays the managed/ path: the caller keeps `rooted`) */
static void k12_vpk_ilz_path (const char *name, char *path, size_t cap, unsigned *size)
{
	if (k12z_state == 0 || strncmp (path, VPK_MANAGED "/", sizeof (VPK_MANAGED)) != 0)
		return;   /* (off: kit #11 -- decided once; a racy read of -1 only takes the slow path) */
	char p [300];
	unsigned zs = 0;
	const char *why = NULL;
	int use = 0;
	if ((size_t) snprintf (p, sizeof (p), K12_VPK_ILZ "/%s.dll", name) >= sizeof (p) || strlen (p) >= cap)
		return;
	{
		/* (review fix) in a GC-safe region, as kit #10's hook (k10a_ilz_path, kit #10 review): the decision and the check read app0 and the card
		 * and may wait for another thread's check -- a stop-the-world must not wait for them; nothing in here touches managed memory */
		SAFE_BEGIN ();
		pthread_mutex_lock (&g_k10a_ilz_lock);
		if (k12z_state < 0) {
			int on = k12w_on () && k12w_env_bool ("SHEPHERD_K12_VPKILZ", SHP_K12_VPKILZ_DEFAULT) && &mono_shepherd_k10_ilz_images != NULL;
			if (on) {
				unsigned s = 0;
				on = file_size (K12_VPK_ILZ "/System.Private.CoreLib.dll", &s) == 0;   /* (a VPK built with SHEPHERD_K12_VPKILZ=1 has CoreLib's) */
			}
			k12z_state = on;
		}
		if (k12z_state > 0 && file_size (p, &zs) == 0) {   /* (none: nothing to cut in it, or not a VPK DLL -- the original) */
			why = k12z_verified_why (name, path, *size, p);
			if (why)
				k12z_refused++;
			else {
				use = 1;
				k12z_files++;
				k12z_orig_kib += *size / 1024u;
				k12z_kib += zs / 1024u;
			}
		}
		pthread_mutex_unlock (&g_k10a_ilz_lock);
		SAFE_END ();
	}
	if (why)
		LOG ("WARN K12 vpkilz: %s not used (%s): the original %s is read", p, why, path);
	if (!use)
		return;
	snprintf (path, cap, "%s", p);
	*size = zs;
}
#else
static int k12z_state = 0;
static unsigned k12z_refused;
static void k12_vpk_ilz_path (const char *name, char *path, size_t cap, unsigned *size) { (void) name; (void) path; (void) cap; (void) size; }
#endif

/* ---------------------------------------------------------------- the object-header census (test only) */
#define K12H_SLOTS 8192
typedef struct { MonoClass *k; const char *ns, *nm; unsigned n, sz; unsigned long long now_slot, h4_slot, now_a8, h4_a8; } K12HClass;
typedef struct {
	unsigned at_major, objects, los_objects, sync_flat, sync_hash, sync_infl, ncls, cls_full;
	unsigned long long now_slot, h4_slot, now_a8, h4_a8, true_bytes, los_bytes;
	K12HClass cls [K12H_SLOTS];
} K12HCensus;
static K12HCensus *k12h_cur, *k12h_best;
static unsigned k12h_walks;
static int k12h_on = -1;
static int k12h_cb (MonoObject *obj, MonoClass *klass, uintptr_t size, uintptr_t num, MonoObject **refs, uintptr_t *offsets, void *data)
{
	(void) refs; (void) offsets; (void) data; (void) num;
	if (!size)
		return 0;
	K12HCensus *c = k12h_cur;
	unsigned u = (unsigned) mono_object_get_size (obj);          /* with today's 8-byte header */
	unsigned a8 = (u + 7u) & ~7u, b8 = (u - 4u + 7u) & ~7u;      /* the same object with a 4-byte header */
	unsigned s = k10_slot_of (a8), s4 = k10_slot_of (b8);
	if (!s) {   /* the large-object space (page-rounded chunks) */
		c->los_objects++;
		c->los_bytes += u;
		s = (u + 4095u) & ~4095u;
		s4 = (u - 4u + 4095u) & ~4095u;
	} else if (!s4)
		s4 = s;
	c->objects++;
	c->true_bytes += u;
	c->now_slot += s; c->h4_slot += s4; c->now_a8 += a8; c->h4_a8 += b8;
	uintptr_t sw = ((volatile uintptr_t *) obj) [1];   /* MonoObject.synchronisation: 0 = never locked / hashed (or unlocked again) */
	if (sw) {
		unsigned st = (unsigned) (sw & 3u);   /* monitor.h: 0 flat (a thin lock held), 1 has hash, 2 inflated */
		if (st == 0) c->sync_flat++; else if (st == 1) c->sync_hash++; else c->sync_infl++;
	}
	unsigned h = (unsigned) (((uintptr_t) klass >> 3) * 2654435761u) % K12H_SLOTS;
	for (unsigned i = 0; i < K12H_SLOTS; i++, h = (h + 1) % K12H_SLOTS) {
		K12HClass *e = &c->cls [h];
		if (e->k == klass) {
			e->n++; e->now_slot += s; e->h4_slot += s4; e->now_a8 += a8; e->h4_a8 += b8;
			return 0;
		}
		if (!e->k) {
			if (c->ncls >= K12H_SLOTS * 3 / 4) { c->cls_full++; return 0; }
			e->k = klass; e->n = 1; e->sz = u; e->now_slot = s; e->h4_slot = s4; e->now_a8 = a8; e->h4_a8 = b8;
			e->ns = mono_class_get_namespace (klass);
			e->nm = mono_class_get_name (klass);
			c->ncls++;
			return 0;
		}
	}
	return 0;
}
/* gc_event_cb, PRE_START_WORLD of a pause that ran a major collection (the world stopped, this thread the collecting one) */
static void k12_hdr_walk (void)
{
	if (k12h_on < 0)
		k12h_on = k12w_on () && k12w_env_bool ("SHEPHERD_K12_HDRCENSUS", 0);
	if (!k12h_on)
		return;
	if (!k12h_cur) {
		k12h_cur = (K12HCensus *) calloc (1, sizeof (K12HCensus));
		k12h_best = (K12HCensus *) calloc (1, sizeof (K12HCensus));
		if (!k12h_cur || !k12h_best) { k12h_on = 0; return; }
	}
	memset (k12h_cur, 0, sizeof *k12h_cur);
	mono_gc_walk_heap (0, k12h_cb, NULL);
	k12h_walks++;
	k12h_cur->at_major = gcs.count [1] + 1;
	if (k12h_cur->now_slot >= k12h_best->now_slot) {
		K12HCensus *t = k12h_best; k12h_best = k12h_cur; k12h_cur = t;
	}
}
static int k12h_cmp (const void *a, const void *b)
{
	const K12HClass *x = (const K12HClass *) a, *y = (const K12HClass *) b;
	unsigned long long gx = x->now_slot - x->h4_slot, gy = y->now_slot - y->h4_slot;
	return gx < gy ? 1 : gx > gy ? -1 : 0;
}
static void k12_hdr_end (void)
{
	if (k12h_on <= 0 || !k12h_best || !k12h_best->objects)
		return;
	K12HCensus *c = k12h_best;
	K12HClass *v = (K12HClass *) malloc (sizeof (K12HClass) * c->ncls);
	unsigned nv = 0;
	if (v) {
		for (unsigned i = 0; i < K12H_SLOTS; i++)
			if (c->cls [i].k) v [nv++] = c->cls [i];
		qsort (v, nv, sizeof (K12HClass), k12h_cmp);
	}
	size_t cap = 256 + (size_t) nv * 160, o = 0;
	char *b = (char *) malloc (cap);
	char top [600] = "";
	int to = 0;
	if (b && v) {
		o += snprintf (b + o, cap - o, "class\tcount\tfirst_size\tslot_kib_now\tslot_kib_4byte\tsaved_kib\taligned8_kib_now\taligned8_kib_4byte\n");
		for (unsigned i = 0; i < nv && o + 200 < cap; i++) {
			o += snprintf (b + o, cap - o, "%s%s%s\t%u\t%u\t%llu\t%llu\t%llu\t%llu\t%llu\n", v [i].ns && *v [i].ns ? v [i].ns : "", v [i].ns && *v [i].ns ? "." : "",
				       v [i].nm ? v [i].nm : "?", v [i].n, v [i].sz, v [i].now_slot / 1024, v [i].h4_slot / 1024, (v [i].now_slot - v [i].h4_slot) / 1024,
				       v [i].now_a8 / 1024, v [i].h4_a8 / 1024);
			if (i < 8 && to < (int) sizeof top - 120)
				to += snprintf (top + to, sizeof top - to, "%s %s%s%s %u x -> %llu KiB", i ? "," : "", v [i].ns && *v [i].ns ? v [i].ns : "", v [i].ns && *v [i].ns ? "." : "",
						v [i].nm ? v [i].nm : "?", v [i].n, (v [i].now_slot - v [i].h4_slot) / 1024);
		}
		k10_write_file ("k12-hdr.tsv", b, o);
	}
	unsigned sync = c->sync_flat + c->sync_hash + c->sync_infl;
	LOG ("K12 HDR census (the walk with the largest heap: after major #%u, %u walks): %u objects (%u large), %llu KiB with 8-byte headers -> in sgen's slots "
	     "%llu KiB now, %llu KiB with 4-byte headers (-%llu KiB = -%.1f %%; at 8-byte alignment alone %llu -> %llu KiB, -%llu); sync words in use at that moment: "
	     "%u (thin-locked %u, hashed %u, inflated %u) = the side table's entries a 4-byte header needs | <logs>/k12-hdr.tsv",
	     c->at_major, k12h_walks, c->objects, c->los_objects, c->true_bytes / 1024, c->now_slot / 1024, c->h4_slot / 1024, (c->now_slot - c->h4_slot) / 1024,
	     c->now_slot ? 100.0 * (double) (c->now_slot - c->h4_slot) / (double) c->now_slot : 0.0, c->now_a8 / 1024, c->h4_a8 / 1024, (c->now_a8 - c->h4_a8) / 1024,
	     sync, c->sync_flat, c->sync_hash, c->sync_infl);
	LOG ("K12 HDR the types that save the most:%s%s", top [0] ? top : " none", c->cls_full ? " (class table full: some objects not by class)" : "");
	free (b);
	free (v);
}

/* ---------------------------------------------------------------- the end line */
static void k12_walkb_end (void)
{
#if !defined (SHEPHERD_MAC_CHECK)
	if (k12z_state > 0 || k12z_refused)
		LOG ("K12 vpkilz: %u VPK assemblies read in their ILZ form (%u KiB instead of %u KiB: -%u KiB), %u refused (stale / damaged / unreadable: the "
		     "original read); pairs checked in full %u (%u ms), by stamp %u; the bodies unpacked are on the K10 ilz line (the runtime counts the "
		     "owner's and the VPK's together)",
		     k12z_files, k12z_kib, k12z_orig_kib, k12z_orig_kib > k12z_kib ? k12z_orig_kib - k12z_kib : 0, k12z_refused, k12z_verified, k12z_verify_ms, k12z_stamped);
	else
		LOG ("K12 vpkilz: off (%s)", !k12w_on () ? "SHEPHERD_K12_WALKB=0" : !k12w_env_bool ("SHEPHERD_K12_VPKILZ", SHP_K12_VPKILZ_DEFAULT)
		     ? "kit #11's originals: SHEPHERD_K12_VPKILZ=1 reads a VPK's app0:managed-ilz/" : &mono_shepherd_k10_ilz_images == NULL
		     ? "the runtime has no compressed-IL support" : "the VPK has no app0:managed-ilz/: a build without SHEPHERD_K12_VPKILZ=1");
#endif
	k12_hdr_end ();
}
