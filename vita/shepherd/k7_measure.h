/* SHEPHERD-VITA device kit #7, track measure (vita/notes/kit7-measure.md): the host's half of the measurement tools. Included by host.c
 * (after logs_dir and the memory numbers); everything here is off or inert unless its env-file switch turns it on, and SHEPHERD_K7=0 turns
 * all of it off (= kit #6's host: no block, no ring, no thread, no callback, no K7 line).
 *
 * BLACK BOX (SHEPHERD_K7_BLACKBOX, default ON; =0 off, =end: no periodic file): the last seconds of the session kept in small rings --
 *   the last 48 host log lines, the last 48 engine lines (all of them: also the ones the host log no longer keeps), the last 256 frames
 *   (frame, wall us, GC pause us, main-thread allocation KiB: the engine writes them into the black-box block, Diag/K7.cs) -- and written
 *   to <logs>/blackbox.txt at a red / FAIL END (end_run_now, main's FAIL END), a STALL? (the watchdog: nothing logged for 120 s), a
 *   runtime fatal error (before its abort) and an exit() outside the host; and every 5 s while new frames or lines arrive to
 *   <logs>/blackbox-live.txt (the power thread), so a hard crash -- which no handler may catch on the Vita (no signal / exception
 *   handlers, the host's rule) -- still leaves the last <= 5 s. Both files of the previous start are kept as *.prev.txt.
 * HOT-PATH PROFILER, host parts (SHEPHERD_K7_PROF, default off; the engine's Diag/K7Prof.cs is the rest):
 *   "calls" -- the FIRST-CALL TRACE: Mono's jit_done profiler event (raised by the AOT loader for each method's first resolution and by
 *     the interpreter for each method's transform) -> one record per method (frame, time, A = AOT code / I = interpreted / W = no jit
 *     info) -> <logs>/prof-calls.tsv when Main returns (and at a FAIL END). Off: no callback is installed (kit #6).
 *   "sample" -- the SAMPLER: a native thread (a latency-tolerant core when pinning is on) reads, ~1000 times a second, the zone / behaviour
 *     id the engine's main thread and render thread write into the profiler block (one store per zone change: no signals -- the Vita
 *     has none for this) and counts it; a sample taken while a collection has the world stopped counts as GC. The engine turns the counts
 *     into per-scene rows of prof.tsv.
 *   GC ring: each collection's start and pause (us, process time) for the engine's Chrome trace (SHEPHERD_K7_TRACE).
 * BOOT: op 4 = the process time (ms) at main (the eboot's own load before main); the engine sends its BOOT line as checkpoint kind 6. */

#define K7_RING_LINES 48
#define K7_RING_LEN 200
#define K7_BB_FRAMES 256
#define K7_BB_HDR 8
#define K7_PROF_IDS (64 + 4096)            /* = Diag/K7Prof.cs MaxIds */
#define K7_PROF_HDR 16
#define K7_PROF_RT (K7_PROF_HDR + K7_PROF_IDS)
#define K7_PROF_GC (K7_PROF_RT + 64)
#define K7_PROF_GCN 256
#define K7_PROF_WORDS (K7_PROF_GC + 2 * K7_PROF_GCN)
#define K7_ZONE_GC 31                       /* = Diag/K7Prof.cs ZGc */
#define K7_DUMP_CAP (40 * 1024)

static int g_k7_on, g_k7_bb, g_k7_bb_live, g_k7_calls, g_k7_sample;
static char (*g_k7_ring) [K7_RING_LEN];     /* [0, 48) host lines, [48, 96) engine lines */
static unsigned g_k7_ring_n [2];
static volatile int *g_k7_bb_blk, *g_k7_prof_blk;
static char *g_k7_dump;
static unsigned g_k7_live_seen;              /* (frames + lines) at the last live write */
static volatile unsigned g_k7_dumps;

/* host.c: log_line_ex (which 0) and the engine's console lines (which 1); under log_lock */
static void k7_ring_put (int which, const char *line)
{
	if (!g_k7_ring)
		return;
	unsigned i = g_k7_ring_n [which]++ % K7_RING_LINES;
	char *d = g_k7_ring [which * K7_RING_LINES + i];
	size_t n = strlen (line);
	if (n > K7_RING_LEN - 1)
		n = K7_RING_LEN - 1;
	memcpy (d, line, n);
	d [n] = 0;
}

static int k7_env_has (const char *v, const char *word)
{
	if (!v)
		return 0;
	size_t w = strlen (word);
	for (const char *p = v; (p = strstr (p, word)); p += w)
		if ((p == v || p [-1] == ',' || p [-1] == ' ') && (p [w] == 0 || p [w] == ',' || p [w] == ' ' || p [w] == ':'))
			return 1;
	return 0;
}

/* main, right after the env file: the switches, the rings and blocks. Newlib with the black box only (the default): 64,288 B = ~63 KiB --
 * the line rings 2 x 48 x 200 = 19,200 B, the frame block (8 + 4 x 256) x 4 = 4,128 B, the dump buffer K7_DUMP_CAP 40,960 B (review fix:
 * this said ~25 KiB); the profiler block adds 19,008 B, the first-call trace 16 B a record (1 MiB at the default cap) */
static void k7_init (void)
{
	const char *m = getenv ("SHEPHERD_K7"), *b = getenv ("SHEPHERD_K7_BLACKBOX"), *p = getenv ("SHEPHERD_K7_PROF");
	g_k7_on = !(m && !strcmp (m, "0"));
	if (!g_k7_on) {
		LOG ("K7 measure track OFF (SHEPHERD_K7=0): no black box, profiler or boot line (kit #6)");
		return;
	}
	g_k7_bb = !(b && !strcmp (b, "0"));
	g_k7_bb_live = g_k7_bb && !(b && !strcmp (b, "end"));
	const char *tr = getenv ("SHEPHERD_K7_TRACE");
	int prof = (p && *p && strcmp (p, "0")) || (tr && *tr && strcmp (tr, "0"));   /* (the trace's GC rows need the block's GC ring) */
	/* (review fix: "1" = "all" = time + sample + calls, as Diag/K7Prof.cs and the note say -- "1" used to leave the first-call trace off) */
	g_k7_calls = prof && (k7_env_has (p, "calls") || k7_env_has (p, "1") || k7_env_has (p, "all"));
	g_k7_sample = prof && (k7_env_has (p, "sample") || k7_env_has (p, "1") || k7_env_has (p, "all"));
	if (g_k7_bb) {
		g_k7_ring = calloc (2 * K7_RING_LINES, K7_RING_LEN);
		g_k7_bb_blk = calloc (K7_BB_HDR + 4 * K7_BB_FRAMES, sizeof (int));
		g_k7_dump = malloc (K7_DUMP_CAP);
		if (!g_k7_ring || !g_k7_bb_blk || !g_k7_dump) {
			free (g_k7_ring); free ((void *) g_k7_bb_blk); free (g_k7_dump);
			g_k7_ring = NULL; g_k7_bb_blk = NULL; g_k7_dump = NULL; g_k7_bb = g_k7_bb_live = 0;
		} else {
			g_k7_bb_blk [0] = 0x4242;
			g_k7_bb_blk [2] = K7_BB_FRAMES;
		}
		/* the previous start's black box files are kept (the next crash's evidence must survive one restart) */
		char a [160], c [168], t [168];
		static const char *const names [2] = { "blackbox", "blackbox-live" };
		for (int i = 0; i < 2; ++i) {
			snprintf (a, sizeof (a), "%s/%s.txt", logs_dir (), names [i]);
			snprintf (c, sizeof (c), "%s/%s.prev.txt", logs_dir (), names [i]);
			if (i == 1) {   /* (review fix, the live file's temp write: no .txt but a .tmp = a crash between the remove and the rename,
					 *  the .tmp complete; a .tmp next to a .txt = a crash during the write, the .txt the last complete one) */
				SceIoStat st;
				snprintf (t, sizeof (t), "%s.tmp", a);
				if (sceIoGetstat (a, &st) < 0 && sceIoGetstat (t, &st) >= 0)
					sceIoRename (t, a);
				sceIoRemove (t);
			}
			sceIoRemove (c);
			sceIoRename (a, c);
		}
	}
	if (prof) {
		g_k7_prof_blk = calloc (K7_PROF_WORDS, sizeof (int));
		if (g_k7_prof_blk)
			g_k7_prof_blk [0] = 0x4b37;
		else
			g_k7_sample = g_k7_calls = 0;
	}
	LOG ("K7 measure track (host main at %.2f s of process time: the eboot's load before it): black box %s; profiler %s (SHEPHERD_K7_PROF=%s): first-call trace %s, sampler %s; SHEPHERD_K7=0 = kit #6",
	     (double) g_t0 / 1e6, !g_k7_bb ? "off (SHEPHERD_K7_BLACKBOX=0)" : g_k7_bb_live ? "ON (rings + blackbox.txt at a FAIL END / STALL / fatal, blackbox-live.txt every 5 s)"
	     : "ON (blackbox.txt at a FAIL END / STALL / fatal; no live file: SHEPHERD_K7_BLACKBOX=end)",
	     prof ? "on" : "off", p ? p : "unset", g_k7_calls ? "ON" : "off", g_k7_sample ? "ON" : "off");
}

/* ---------------------------------------------------------------- the black box file */

static int k7_put (int o, const char *fmt, ...)
{
	if (o >= K7_DUMP_CAP - 2)
		return o;
	va_list ap;
	va_start (ap, fmt);
	int n = vsnprintf (g_k7_dump + o, (size_t) (K7_DUMP_CAP - o), fmt, ap);
	va_end (ap);
	if (n < 0)
		return o;
	return o + n < K7_DUMP_CAP ? o + n : K7_DUMP_CAP - 1;
}

static void k7_ring_out (int *o, int which, unsigned last, const char *title)
{
	unsigned n = g_k7_ring_n [which], have = n < K7_RING_LINES ? n : K7_RING_LINES;
	if (have > last)
		have = last;
	*o = k7_put (*o, "-- %s (the last %u of %u)\n", title, have, n);
	for (unsigned k = n - have; k < n; ++k)
		*o = k7_put (*o, "%s\n", g_k7_ring [which * K7_RING_LINES + k % K7_RING_LINES]);
}

/* the rings and the numbers to <logs>/<file>. Any thread; reads only lock-free numbers (no mallinfo: newlib's lock may be the stuck one;
 * the rings are copied under log_lock with a trylock, so a thread that died holding it cannot block the dump).
 * Review fixes: returns 1 when the whole file was written, else 0 with the reason in err (the event line says which -- it used to say
 * "written" for a dump that never happened); temp (the periodic live file): written to <file>.tmp, then the old file removed and the
 * temp renamed over it (the Vita's sceIoRename does not replace an existing file), so a hard crash in the middle of the write -- the
 * case the live file exists for -- keeps the previous snapshot instead of an empty or half file; a crash between the remove and the
 * rename leaves the complete <file>.tmp, which k7_init's next start renames back before keeping it as .prev. */
static int k7_bb_write (const char *file, const char *why, unsigned frames_max, unsigned lines_max, int wait, int temp, char *err, size_t errn)
{
	static volatile int busy;
	if (err && errn)
		err [0] = 0;
	if (!g_k7_bb)
		return 0;
	/* one dump at a time (one buffer): an event dump waits up to 1 s for a periodic write to finish; a periodic write never waits */
	for (int i = 0; __sync_lock_test_and_set (&busy, 1); ++i) {
		if (!wait || i >= 1000) {
			if (err && errn)
				snprintf (err, errn, "skipped: %s", wait ? "a periodic write held the one buffer for more than 1 s (a slow card)" : "another dump is being written");
			return 0;
		}
		sceKernelDelayThread (1000);
	}
	int locked = pthread_mutex_trylock (&log_lock) == 0;
	int o = 0;
	MonoVitaArenaStats as;
	int aok = mono_vita_vm_arena_try_get_stats (&as) == 0;
	SceKernelFreeMemorySizeInfo fi;
	memset (&fi, 0, sizeof (fi));
	fi.size = sizeof (fi);
	sceKernelGetFreeMemorySize (&fi);
	o = k7_put (o, "# Shepherd black box (kit #7): %s\n# build %s, log %s, wall %.1f s, the engine's last heartbeat f%d, last frame written f%d\n", why, SHP_BUILD_ID,
		    SHP_LOG_NAME, wall_s (), g_last_frame, g_k7_bb_blk ? g_k7_bb_blk [3] : -1);
	o = k7_put (o, "# memory: free user %u KiB, cdram %u, phycont %u | arena %s%u KiB used, peak %u of %u | gc heap %u KiB (max-heap-size %u MiB), %u minor + %u major GCs, pauses %.1f + %.1f ms | heap-cap refusals %u, OOM %d, FAIL lines %d | newlib peak %u KiB of %u\n",
		    (unsigned) (fi.size_user / 1024), (unsigned) (fi.size_cdram / 1024), (unsigned) (fi.size_phycont / 1024), aok ? "" : "~",
		    aok ? (unsigned) (as.used / 1024) : 0u, aok ? (unsigned) (as.peak / 1024) : 0u, aok ? (unsigned) (as.arena_size / 1024) : 0u,
		    g_runtime_up ? (unsigned) (mono_gc_get_heap_size () / 1024) : 0u, g_max_heap_mb, gcs.count [0], gcs.count [1], gcs.total_us [0] / 1000.0,
		    gcs.total_us [1] / 1000.0, mono_vita_heap_limit_count (), g_oom_count, g_fail_count, __malloc_max_sbrked_mem / 1024, SHP_NEWLIB_HEAP_MB * 1024);
	o = k7_put (o, "# clocks: arm %d bus %d gpu %d MHz\n", scePowerGetArmClockFrequency (), scePowerGetBusClockFrequency (), scePowerGetGpuClockFrequency ());
	if (g_k7_bb_blk) {
		unsigned head = (unsigned) g_k7_bb_blk [1], have = head < K7_BB_FRAMES ? head : K7_BB_FRAMES;
		if (have > frames_max)
			have = frames_max;
		o = k7_put (o, "-- frames (the last %u of %u): frame\twall_ms\tgc_pause_ms\talloc_kib\n", have, head);
		for (unsigned k = head - have; k < head; ++k) {
			const volatile int *r = g_k7_bb_blk + K7_BB_HDR + 4 * (k % K7_BB_FRAMES);
			o = k7_put (o, "f%d\t%.2f\t%.2f\t%d\n", r [0], r [1] / 1000.0, r [2] / 1000.0, r [3]);
		}
	}
	if (locked) {
		k7_ring_out (&o, 0, lines_max, "host log lines");
		k7_ring_out (&o, 1, lines_max, "engine lines (all of them, also those the host log no longer keeps)");
		pthread_mutex_unlock (&log_lock);
	} else
		o = k7_put (o, "-- (the log lock was held by another thread: the line rings are not copied)\n");
	char path [168], tmp [176];
	snprintf (path, sizeof (path), "%s/%s", logs_dir (), file);
	snprintf (tmp, sizeof (tmp), "%s.tmp", path);
	int ok = 0;
	SceUID fd = sceIoOpen (temp ? tmp : path, SCE_O_WRONLY | SCE_O_CREAT | SCE_O_TRUNC, 0777);
	if (fd >= 0) {
		int w = sceIoWrite (fd, g_k7_dump, (SceSize) o);
		sceIoClose (fd);
		ok = w == o;
		if (!ok && err && errn)
			snprintf (err, errn, "the card took %d of %d bytes", w, o);
		if (ok && temp) {
			sceIoRemove (path);
			int rr = sceIoRename (tmp, path);
			if (rr < 0) {   /* (the complete snapshot stays in the .tmp file) */
				ok = 0;
				if (err && errn)
					snprintf (err, errn, "renaming %s.tmp failed (0x%08x): the snapshot is in the .tmp file", file, (unsigned) rr);
			}
		}
	} else if (err && errn)
		snprintf (err, errn, "sceIoOpen 0x%08x", (unsigned) fd);
	__sync_lock_release (&busy);
	return ok;
}

/* an event dump (red / FAIL END, STALL?, fatal, exit outside the host): blackbox.txt + one host line */
static void k7_bb_event (const char *why)
{
	if (!g_k7_bb)
		return;
	char err [160], line [400];
	if (k7_bb_write ("blackbox.txt", why, K7_BB_FRAMES, K7_RING_LINES, 1, 0, err, sizeof (err))) {
		g_k7_dumps++;
		snprintf (line, sizeof (line), "K7 black box written: %s/blackbox.txt (%s; dump %u)", logs_dir (), why, g_k7_dumps);
	} else   /* (review fix: a skipped dump said "written"; blackbox.txt is then missing or an earlier dump of this start) */
		snprintf (line, sizeof (line), "K7 black box NOT written: %s/blackbox.txt (%s): %s -- blackbox.txt is missing or an earlier dump (%u written this start); blackbox-live.txt has the last periodic state",
			  logs_dir (), why, err [0] ? err : "?", g_k7_dumps);
	log_line_ex (line, 0);
}

/* the power thread, every 5 s: blackbox-live.txt when something new arrived since the last write (a hard crash keeps the last <= 5 s) */
static void k7_bb_live (void)
{
	if (!g_k7_bb_live || !g_k7_bb_blk)
		return;
	unsigned seen = (unsigned) g_k7_bb_blk [1] + g_k7_ring_n [0] + g_k7_ring_n [1];
	if (seen == g_k7_live_seen)
		return;
	g_k7_live_seen = seen;
	k7_bb_write ("blackbox-live.txt", "periodic (every 5 s while the session runs; after a crash this is the last state written)", 120, 24, 0, 1, NULL, 0);
}

/* ---------------------------------------------------------------- the first-call trace (SHEPHERD_K7_PROF=...calls...) */

typedef struct { MonoMethod *m; int frame; unsigned ms; char kind; } K7Call;
static K7Call *g_k7_call;
static unsigned g_k7_call_cap;
static volatile unsigned g_k7_call_n;
#if !defined (SHEPHERD_MAC_CHECK)
extern char __executable_start [], _etext [];   /* the eboot's text (vitasdk's ld script): static AOT code lives there */
#endif

static void k7_jit_done (MonoProfiler *prof, MonoMethod *method, MonoJitInfo *jinfo)
{
	(void) prof;
	unsigned i = __sync_fetch_and_add (&g_k7_call_n, 1u);
	if (i >= g_k7_call_cap)
		return;
	char kind = 'W';
	if (jinfo) {
		kind = 'I';
#if !defined (SHEPHERD_MAC_CHECK)
		char *code = (char *) mono_jit_info_get_code_start (jinfo);
		if (code >= __executable_start && code < _etext)
			kind = 'A';
#endif
	}
	K7Call *c = &g_k7_call [i];
	c->frame = g_k7_bb_blk ? g_k7_bb_blk [3] : g_k7_prof_blk ? g_k7_prof_blk [1] : -1;
	c->ms = (unsigned) ((sceKernelGetProcessTimeWide () - g_t0) / 1000u);
	c->kind = kind;
	c->m = method;   /* (last) */
}

/* the Mono thread, before mono_jit_init_version: the callback (only when asked for: an installed jit_done makes the AOT loader look each
 * method up -- mono_get_method + the jit info table -- at its first call) */
static void k7_profiler (MonoProfilerHandle ph)
{
	if (!g_k7_calls)
		return;
	const char *c = getenv ("SHEPHERD_K7_CALLS_CAP");
	g_k7_call_cap = c && atoi (c) > 0 ? (unsigned) atoi (c) : 65536u;
	g_k7_call = calloc (g_k7_call_cap, sizeof (K7Call));
	if (!g_k7_call) {
		g_k7_call_cap = 0;
		LOG ("WARN K7 first-call trace: no memory for %u records: off", g_k7_call_cap);
		return;
	}
	mono_profiler_set_jit_done_callback (ph, k7_jit_done);
	LOG ("K7 first-call trace ON: up to %u methods (%u KiB of newlib), one record per method's first AOT resolution / interpreter transform", g_k7_call_cap,
	     (unsigned) (g_k7_call_cap * sizeof (K7Call) / 1024));
}

/* prof-calls.tsv: on the Mono thread when Main returned (names are plain field reads: no lock, no allocation in the runtime) */
static void k7_calls_write (const char *tag)
{
	if (!g_k7_call)
		return;
	unsigned n = g_k7_call_n < g_k7_call_cap ? g_k7_call_n : g_k7_call_cap, a = 0, in = 0;
	char path [168];
	snprintf (path, sizeof (path), "%s/prof-calls.tsv", logs_dir ());
	SceUID fd = sceIoOpen (path, SCE_O_WRONLY | SCE_O_CREAT | SCE_O_TRUNC, 0777);
	if (fd < 0)
		return;
	char *buf = malloc (8192);   /* (its own: the black box's buffer may be in use by the power thread's periodic write) */
	if (!buf) {
		sceIoClose (fd);
		return;
	}
	const int cap = 8192;
	int o = snprintf (buf, (size_t) cap, "# Shepherd first-call trace (kit #7): one row per method, in first-call order; kind A = static AOT code, I = interpreted (transformed), W = no jit info\nseq\tframe\tms\tkind\timage\ttoken\tclass\tmethod\n");
	for (unsigned i = 0; i < n; ++i) {
		K7Call *c = &g_k7_call [i];
		if (!c->m)
			continue;
		MonoClass *k = mono_method_get_class (c->m);
		const char *ns = k ? mono_class_get_namespace (k) : "", *cn = k ? mono_class_get_name (k) : "?";
		MonoImage *im = k ? mono_class_get_image (k) : NULL;
		a += c->kind == 'A';
		in += c->kind == 'I';
		if (o > cap - 600) {
			sceIoWrite (fd, buf, (SceSize) o);
			o = 0;
		}
		o += snprintf (buf + o, (size_t) (cap - o), "%u\t%d\t%u\t%c\t%s\t%08x\t%s%s%s\t%s\n", i, c->frame, c->ms, c->kind, im ? mono_image_get_name (im) : "?",
			       mono_method_get_token (c->m), ns && *ns ? ns : "", ns && *ns ? "." : "", cn ? cn : "?", mono_method_get_name (c->m));
	}
	sceIoWrite (fd, buf, (SceSize) o);
	sceIoClose (fd);
	free (buf);
	/* (review fix: the line starts with "K7", the tag in brackets -- "FAIL END K7 ..." at a FAIL END was counted as a FAIL line and taken for
	 *  the run's END line by read-logs.sh's /^(PASS|FAIL) END/; now read-logs.sh's "K7 (...|first-call trace|...)" lines list it) */
	LOG ("K7 first-call trace (%s): %u methods (%u AOT, %u interpreted, %u without jit info)%s -> %s", tag, n, a, in, n - a - in,
	     g_k7_call_n > g_k7_call_cap ? " -- the record cap was reached (SHEPHERD_K7_CALLS_CAP)" : "", path);
}

/* ---------------------------------------------------------------- CPU time per thread (H11): the Mono threads the stack watch knows */

static void k7_cpu_line (const char *tag)
{
#if defined (SHEPHERD_MAC_CHECK)
	(void) tag;   /* (the stack watch is the Vita's) */
#else
	if (!g_k7_on)
		return;
	char line [700];
	int o = snprintf (line, sizeof (line), "%s K7 CPU time per thread (the kernel's run clocks / 1e6 -- us if the unit is the PSP's; %.1f s of process time):", tag, wall_s ());
	unsigned long long any = 0;
	pthread_mutex_lock (&g_sw_lock);
	for (int i = 0; i < SW_SLOTS && o < (int) sizeof (line) - 60; ++i) {
		if (!g_sw [i].lo)
			continue;
		SceKernelThreadInfo ti;
		memset (&ti, 0, sizeof (ti));
		ti.size = sizeof (ti);
		if (sceKernelGetThreadInfo (g_sw [i].uid, &ti) < 0)
			continue;
		any += ti.runClocks;
		o += snprintf (line + o, sizeof (line) - o, " %s %.1f;", g_sw [i].name [0] ? g_sw [i].name : ti.name, (double) ti.runClocks / 1e6);
	}
	pthread_mutex_unlock (&g_sw_lock);
	if (any)   /* (vlx's thread info has no run clocks: no line there) */
		LOG ("%s", line);
#endif
}

/* ---------------------------------------------------------------- the sampler (SHEPHERD_K7_PROF=...sample...) */

static volatile int g_k7_sampler_run;
static void *k7_sampler (void *arg)
{
	(void) arg;
	int old;
	pthread_setcancelstate (PTHREAD_CANCEL_DISABLE, &old);
	if (pin_requested ())
		shepherd_vita_pin (shepherd_vita_worker_mask (), "k7-sampler");
	volatile int *p = g_k7_prof_blk;
	while (g_k7_sampler_run && !g_host_exiting) {
		sceKernelDelayThread (1000);
		int z = gcs.stop_t ? K7_ZONE_GC : p [2], r = p [3];
		if ((unsigned) z < K7_PROF_IDS)
			p [K7_PROF_HDR + z]++;
		if ((unsigned) r < 64)
			p [K7_PROF_RT + r]++;
		p [5]++;
		if (gcs.stop_t)
			p [6]++;
	}
	return NULL;
}
static void k7_sampler_start (void)
{
	if (!g_k7_sample || !g_k7_prof_blk || g_k7_sampler_run)
		return;
	g_k7_sampler_run = 1;
	g_k7_prof_blk [4] = 1;
	pthread_t t;
	pthread_attr_t a;
	pthread_attr_init (&a);
	pthread_attr_setstacksize (&a, 32 * 1024);
	int rc = pthread_create (&t, &a, k7_sampler, NULL);
	pthread_attr_destroy (&a);
	if (rc != 0) {
		g_k7_sampler_run = 0;
		g_k7_prof_blk [4] = 0;
	}
	LOG ("K7 sampler %s (~1000 samples/s of the engine's zone / behaviour ids; GC = the world stopped)", rc == 0 ? "started" : "NOT started");
}

/* gc_event_cb (POST_START_WORLD, world stopped: stores only) -- the Chrome trace's GC rows */
static inline void k7_gc_note (SceUInt64 start, unsigned us)
{
	volatile int *p = g_k7_prof_blk;
	if (!p)
		return;
	unsigned i = (unsigned) p [7]++ % K7_PROF_GCN;
	p [K7_PROF_GC + 2 * i] = (int) (unsigned) start;
	p [K7_PROF_GC + 2 * i + 1] = (int) us;
}

/* ---------------------------------------------------------------- the engine's export (Diag/K7.cs; a P/Invoke: GC-safe, no runtime lock) */

void *shepherd_vita_k7_ptr (int which);
void *shepherd_vita_k7_ptr (int which) { return which == 0 ? (void *) g_k7_bb_blk : which == 1 ? (void *) g_k7_prof_blk : NULL; }

int shepherd_vita_k7 (int op, int arg);
int shepherd_vita_k7 (int op, int arg)
{
	switch (op) {
	case 0:   /* state: 1 on, 2 black box, 4 live file, 8 first-call trace, 16 sampler wanted */
		return (g_k7_on ? 1 : 0) | (g_k7_bb ? 2 : 0) | (g_k7_bb_live ? 4 : 0) | (g_k7_calls ? 8 : 0) | (g_k7_sample ? 16 : 0);
	case 1:   /* the engine has the profiler block: start the sampler */
		k7_sampler_start ();
		return g_k7_sampler_run;
	case 2: { /* free user memory now, KiB (the overlay, twice a second) */
		SceKernelFreeMemorySizeInfo fi;
		memset (&fi, 0, sizeof (fi));
		fi.size = sizeof (fi);
		sceKernelGetFreeMemorySize (&fi);
		return (int) (fi.size_user / 1024);
	}
	case 3:   /* the newlib heap's sbrk'd size, KiB */
		return (int) (mallinfo ().arena / 1024);
	case 4:   /* the process time at main, ms (the eboot's load before main) */
		return (int) (g_t0 / 1000u);
	case 5:   /* a black box event from the engine (arg: 1 = its own FAIL test) */
		k7_bb_event (arg == 1 ? "the engine's k7 test (SHEPHERD_K7_BB_TEST)" : "the engine asked for it");
		return 0;
	case 90:  /* SHEPHERD_K7_BB_TEST=crash@N: a real crash (a write to address 0) -- no handler runs; blackbox-live.txt is the evidence */
		LOG ("K7 TEST: crashing on purpose now (SHEPHERD_K7_BB_TEST=crash): the system's crash dialog follows; blackbox-live.txt holds the last <= 5 s");
		k7_bb_live ();
		*(volatile int *) (uintptr_t) arg = 0;
		return -1;
	}
	(void) arg;
	return -1;
}
