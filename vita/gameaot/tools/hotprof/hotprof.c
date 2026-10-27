/* game-aot study, HOT METHODS (vita/gameaot/HOT.md): a Mac-only Mono profiler module for METHOD-LEVEL execution profiles of the vita-sim
 * boot on the Mono interpreter, as input for owner-local AOT of the rewritten Unity modules / game assemblies (whole modules or
 * profile-guided partial AOT). Derived from vita/v6/tools/interp-prof (V6 stage 2) with:
 *   - ALL managed threads (one shadow stack + tables per thread, no locks on the hot path), not only the main thread;
 *   - every caller -> callee edge (no top-N cut), with both images;
 *   - per-method metadata for the AOT decision and for writing Mono .aotprofile files later: image, class namespace / name,
 *     method name, parameter count, mono_signature_full_name (the string mono-aot-cross's `profile=` loader matches), IL size,
 *     method flags / impl flags (virtual, final, static, abstract, noinlining, aggressive inlining, icall/runtime, pinvoke),
 *     generic instance or not.
 *
 *   build:  vita/gameaot/tools/hotprof/build.sh <out dir>  -> <out dir>/libmono-profiler-shepherdhot.dylib
 *   use:    DYLD_LIBRARY_PATH=<out dir> MONO_ENV_OPTIONS="--interpreter [--interp=-inline] --profile=shepherdhot:<output.tsv>"
 *           The window is the engine's own SimStats window (SHEPHERD_SIMSTATS=a:b): Shepherd.Engine.Diag.ProfMarks.WindowStart /
 *           WindowEnd (empty never-inlined methods SimStats calls at the first / last window frame, on the main thread).
 *
 * How: a call-instrumentation filter asks for enter + leave (+ tail call, exception leave) events on every method; the interpreter
 * emits MINT_PROF_ENTER / MINT_PROF_EXIT in each transformed method (callees it inlines raise nothing and count in their caller,
 * so run with --interp=-inline for exact call counts and edges; with inlining on the edges are the calls the interpreter really
 * makes). While the window is open, each thread keeps a shadow stack: inclusive / exclusive time (mach_absolute_time) per method,
 * call count, caller -> callee edges. Frames entered before the window are not on the stack (their leaves are ignored); frames
 * above a matching leave (left by an exception) are closed with it; recursion counts inclusive time once (outermost activation).
 * At WindowEnd the main thread's stack is closed; frames still open on other threads at that moment are not counted (their
 * completed children are): other-thread times are lower bounds for long-running loops.
 * The instrumentation costs time (two callbacks per call): absolute times are inflated; counts are exact.
 * Not part of any Vita build; reads no game data; writes one TSV. */
#include <pthread.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <mach/mach_time.h>

typedef struct _MonoProfiler MonoProfiler;
typedef struct _MonoProfilerDesc *MonoProfilerHandle;
typedef struct _MonoMethod MonoMethod;
typedef struct _MonoClass MonoClass;
typedef struct _MonoImage MonoImage;
typedef struct _MonoMethodSignature MonoMethodSignature;
typedef struct _MonoMethodHeader MonoMethodHeader;
typedef struct _MonoProfilerCallContext MonoProfilerCallContext;
typedef struct _MonoGenericContext MonoGenericContext;
typedef int mono_bool;
enum { INSTR_ENTER = 1 << 1, INSTR_LEAVE = 1 << 3, INSTR_TAIL = 1 << 5, INSTR_EXC_LEAVE = 1 << 6 };   /* profiler-types.h */

/* public Mono embedding API (exported by the osx-arm64 Mono pack's libcoreclr.dylib, 8.0.29) */
extern MonoProfilerHandle mono_profiler_create (MonoProfiler *prof);
extern void mono_profiler_set_call_instrumentation_filter_callback (MonoProfilerHandle h, int (*cb) (MonoProfiler *, MonoMethod *));
extern void mono_profiler_set_method_enter_callback (MonoProfilerHandle h, void (*cb) (MonoProfiler *, MonoMethod *, MonoProfilerCallContext *));
extern void mono_profiler_set_method_leave_callback (MonoProfilerHandle h, void (*cb) (MonoProfiler *, MonoMethod *, MonoProfilerCallContext *));
extern void mono_profiler_set_method_tail_call_callback (MonoProfilerHandle h, void (*cb) (MonoProfiler *, MonoMethod *, MonoMethod *));
extern void mono_profiler_set_method_exception_leave_callback (MonoProfilerHandle h, void (*cb) (MonoProfiler *, MonoMethod *, void *));
extern void mono_profiler_set_runtime_shutdown_begin_callback (MonoProfilerHandle h, void (*cb) (MonoProfiler *));
extern char *mono_method_full_name (MonoMethod *method, mono_bool signature);
extern const char *mono_method_get_name (MonoMethod *method);
extern MonoClass *mono_method_get_class (MonoMethod *method);
extern const char *mono_class_get_name (MonoClass *klass);
extern const char *mono_class_get_namespace (MonoClass *klass);
extern MonoClass *mono_class_get_nesting_type (MonoClass *klass);
extern MonoImage *mono_class_get_image (MonoClass *klass);
extern const char *mono_image_get_name (MonoImage *image);
extern uint32_t mono_method_get_flags (MonoMethod *method, uint32_t *iflags);
extern MonoMethodSignature *mono_method_signature (MonoMethod *method);
extern uint32_t mono_signature_get_param_count (MonoMethodSignature *sig);
extern char *mono_signature_full_name (MonoMethodSignature *sig);
extern MonoMethodHeader *mono_method_get_header (MonoMethod *method);
extern const unsigned char *mono_method_header_get_code (MonoMethodHeader *header, uint32_t *code_size, uint32_t *max_stack);
extern MonoGenericContext *mono_method_get_context (MonoMethod *method);
extern void mono_free (void *);

struct _MonoProfiler { int unused; };
static MonoProfiler prof;

static inline uint64_t ticks (void) { return mach_absolute_time (); }
static double tick_ms (void) { mach_timebase_info_data_t tb; mach_timebase_info (&tb); return (double) tb.numer / tb.denom / 1e6; }

typedef struct { MonoMethod *m; uint64_t calls, incl, excl; uint32_t active; } Stat;
typedef struct { MonoMethod *a, *b; uint64_t calls, incl; } Edge;
typedef struct { MonoMethod *m; uint64_t t0, child; Stat *s; int seg; } Fr;
typedef struct { Stat *st; size_t st_cap, st_used; Edge *ed; size_t ed_cap, ed_used; } Tab;
#define STACK_MAX 8192
#define MAXSEG 16
typedef struct Thr {
	struct Thr *next;
	int idx, is_main, epoch, overflow;
	uint64_t tid, enters, ignored_leaves, unwound;
	char name [64];
	Tab tab [MAXSEG];
	int sp; Fr stack [STACK_MAX];
} Thr;

static __thread Thr *tl;
static Thr *threads; static int nthreads;
static pthread_mutex_t reg_mu = PTHREAD_MUTEX_INITIALIZER;
static volatile int recording, epoch;
static pthread_t main_pt;
static MonoMethod *mark_start, *mark_end, *frame_mark;
static uint64_t win_t0, win_ticks; static int windows;
/* SHEPHERD_HOT_SEGMENTS=<frame>,<frame>,... (absolute frames, with an absolute SHEPHERD_SIMSTATS window): split the window into
 * segments at those frames; a frame is counted at each SimStats.Before call on the main thread while the window is open */
static int nseg_at, seg_at [MAXSEG], win_from, frames_seen; static volatile int cur_seg;
static uint64_t seg_t0 [MAXSEG], seg_ticks [MAXSEG]; static int seg_from [MAXSEG];
static char out_path [2048] = "hotprof.tsv";
static int written;

static size_t h1 (const void *p) { uint64_t x = (uintptr_t) p; x ^= x >> 33; x *= 0xff51afd7ed558ccdULL; x ^= x >> 33; return (size_t) x; }

static Stat *stat_of (Thr *t, int seg, MonoMethod *m)
{
	Tab *T = &t->tab [seg];
	if ((T->st_used + 1) * 2 > T->st_cap) {
		size_t nc = T->st_cap ? T->st_cap * 2 : 16384; Stat *nt = calloc (nc, sizeof (Stat));
		for (size_t i = 0; i < T->st_cap; i++) if (T->st [i].m) { size_t j = h1 (T->st [i].m) & (nc - 1); while (nt [j].m) j = (j + 1) & (nc - 1); nt [j] = T->st [i]; }
		free (T->st); T->st = nt; T->st_cap = nc;
		/* open frames of this segment point into the old table */
		for (int k = 0; k < t->sp; k++) if (t->stack [k].seg == seg) { size_t j = h1 (t->stack [k].m) & (nc - 1); while (nt [j].m != t->stack [k].m) j = (j + 1) & (nc - 1); t->stack [k].s = &nt [j]; }
	}
	size_t i = h1 (m) & (T->st_cap - 1);
	while (T->st [i].m && T->st [i].m != m) i = (i + 1) & (T->st_cap - 1);
	if (!T->st [i].m) { T->st [i].m = m; T->st_used++; }
	return &T->st [i];
}
static Edge *edge_of (Thr *t, int seg, MonoMethod *a, MonoMethod *b)
{
	Tab *T = &t->tab [seg];
	if ((T->ed_used + 1) * 2 > T->ed_cap) {
		size_t nc = T->ed_cap ? T->ed_cap * 2 : 32768; Edge *nt = calloc (nc, sizeof (Edge));
		for (size_t i = 0; i < T->ed_cap; i++) if (T->ed [i].b) { size_t j = (h1 (T->ed [i].a) ^ (h1 (T->ed [i].b) * 31)) & (nc - 1); while (nt [j].b) j = (j + 1) & (nc - 1); nt [j] = T->ed [i]; }
		free (T->ed); T->ed = nt; T->ed_cap = nc;
	}
	size_t i = (h1 (a) ^ (h1 (b) * 31)) & (T->ed_cap - 1);
	while (T->ed [i].b && !(T->ed [i].a == a && T->ed [i].b == b)) i = (i + 1) & (T->ed_cap - 1);
	if (!T->ed [i].b) { T->ed [i].a = a; T->ed [i].b = b; T->ed_used++; }
	return &T->ed [i];
}

static Thr *me (void)
{
	Thr *t = tl;
	if (!t) {
		t = calloc (1, sizeof (Thr));
		uint64_t tid = 0; pthread_threadid_np (NULL, &tid); t->tid = tid;
		pthread_getname_np (pthread_self (), t->name, sizeof t->name);
		t->is_main = pthread_equal (pthread_self (), main_pt);
		pthread_mutex_lock (&reg_mu); t->idx = nthreads++; t->next = threads; threads = t; pthread_mutex_unlock (&reg_mu);
		t->epoch = epoch; tl = t;
	}
	if (t->epoch != epoch) { t->sp = 0; t->epoch = epoch; }
	return t;
}

static int filter (MonoProfiler *p, MonoMethod *m)
{
	(void) p;
	MonoClass *k = mono_method_get_class (m);
	const char *cn = k ? mono_class_get_name (k) : NULL, *mn = mono_method_get_name (m);
	if (cn && mn && !strcmp (cn, "ProfMarks")) {
		if (!strcmp (mn, "WindowStart")) mark_start = m; else if (!strcmp (mn, "WindowEnd")) mark_end = m;
	}
	if (cn && mn && !strcmp (cn, "SimStats") && !strcmp (mn, "Before")) frame_mark = m;
	return INSTR_ENTER | INSTR_LEAVE | INSTR_TAIL | INSTR_EXC_LEAVE;
}

static void close_top (Thr *t, uint64_t now)
{
	Fr *f = &t->stack [--t->sp];
	uint64_t incl = now - f->t0, excl = incl > f->child ? incl - f->child : 0;
	Stat *s = f->s; s->calls++; s->excl += excl;
	if (--s->active == 0) s->incl += incl;
	if (t->sp > 0) { t->stack [t->sp - 1].child += incl; Edge *e = edge_of (t, f->seg, t->stack [t->sp - 1].m, f->m); e->calls++; e->incl += incl; }
}

static void on_enter (MonoProfiler *p, MonoMethod *m, MonoProfilerCallContext *c)
{
	(void) p; (void) c;
	if (m == mark_start && m) {
		if (!recording) { main_pt = pthread_self (); epoch++; windows++; win_t0 = ticks (); cur_seg = 0; seg_t0 [0] = win_t0; seg_from [0] = win_from; frames_seen = 0; recording = 1; }
		return;
	}
	if (!recording) return;
	Thr *t = me ();
	if (m == mark_end && m && t->is_main) {
		recording = 0; uint64_t now = ticks ();
		while (t->sp > 0) close_top (t, now);
		seg_ticks [cur_seg] += now - seg_t0 [cur_seg];
		win_ticks += now - win_t0; return;
	}
	if (m == frame_mark && m && t->is_main) {
		frames_seen++;
		if (cur_seg + 1 < MAXSEG && cur_seg < nseg_at && win_from + frames_seen >= seg_at [cur_seg]) {
			uint64_t now = ticks (); seg_ticks [cur_seg] += now - seg_t0 [cur_seg];
			cur_seg++; seg_t0 [cur_seg] = now; seg_from [cur_seg] = win_from + frames_seen;
		}
	}
	if (t->sp >= STACK_MAX) { t->overflow++; return; }
	t->enters++;
	if ((t->enters & 0xffff) == 1) pthread_getname_np (pthread_self (), t->name, sizeof t->name);   /* names set later (Thread.Name) */
	int seg = cur_seg;
	Stat *s = stat_of (t, seg, m); s->active++;
	Fr *f = &t->stack [t->sp++]; f->m = m; f->s = s; f->seg = seg; f->child = 0; f->t0 = ticks ();
}
static void on_leave (MonoProfiler *p, MonoMethod *m, MonoProfilerCallContext *c)
{
	(void) p; (void) c;
	if (!recording) return;
	Thr *t = me ();
	uint64_t now = ticks ();
	int i = t->sp - 1; while (i >= 0 && t->stack [i].m != m) i--;
	if (i < 0) { t->ignored_leaves++; return; }
	while (t->sp - 1 > i) { close_top (t, now); t->unwound++; }
	close_top (t, now);
}
static void on_exc_leave (MonoProfiler *p, MonoMethod *m, void *exc) { (void) exc; on_leave (p, m, NULL); }
static void on_tail (MonoProfiler *p, MonoMethod *m, MonoMethod *target) { (void) target; on_leave (p, m, NULL); }

/* ---- output ---- */
typedef struct { MonoMethod *m; int id; } Id;
static Id *ids; static size_t id_cap; static int nid;
static int id_of (MonoMethod *m, int *fresh)
{
	if ((size_t) (nid + 1) * 2 > id_cap) {
		size_t nc = id_cap ? id_cap * 2 : 65536; Id *nt = calloc (nc, sizeof (Id));
		for (size_t i = 0; i < id_cap; i++) if (ids [i].m) { size_t j = h1 (ids [i].m) & (nc - 1); while (nt [j].m) j = (j + 1) & (nc - 1); nt [j] = ids [i]; }
		free (ids); ids = nt; id_cap = nc;
	}
	size_t i = h1 (m) & (id_cap - 1);
	while (ids [i].m && ids [i].m != m) i = (i + 1) & (id_cap - 1);
	*fresh = !ids [i].m;
	if (!ids [i].m) { ids [i].m = m; ids [i].id = ++nid; }
	return ids [i].id;
}
static const char *s_or (const char *s, const char *d) { return s && *s ? s : d; }
static MonoClass *outermost (MonoClass *k) { MonoClass *x; int n = 0; while ((x = mono_class_get_nesting_type (k)) && n++ < 16) k = x; return k; }
static void meta (FILE *f, MonoMethod *m, int id)
{
	MonoClass *k = mono_method_get_class (m);
	const char *img = k ? mono_image_get_name (mono_class_get_image (k)) : NULL;
	char *full = mono_method_full_name (m, 1);
	int wrapper = full && !strncmp (full, "(wrapper", 8);
	uint32_t iflags = 0, flags = mono_method_get_flags (m, &iflags);
	/* nested types as Outer/Inner; the namespace is the outermost type's */
	char cls [1024]; cls [0] = 0;
	if (k) {
		MonoClass *chain [16]; int n = 0; MonoClass *x = k;
		while (x && n < 16) { chain [n++] = x; x = mono_class_get_nesting_type (x); }
		for (int i = n - 1; i >= 0; i--) { strncat (cls, s_or (mono_class_get_name (chain [i]), "?"), sizeof cls - strlen (cls) - 1); if (i) strncat (cls, "/", sizeof cls - strlen (cls) - 1); }
	}
	uint32_t il = 0, maxst = 0; int has_il = 0;
	/* IL size only for ordinary IL methods: not wrappers, abstract (0x400), pinvoke (0x2000), icall (impl 0x1000) or runtime (impl code type 3) */
	if (!wrapper && !(flags & 0x400) && !(flags & 0x2000) && !(iflags & 0x1000) && (iflags & 3) == 0) {
		MonoMethodHeader *h = mono_method_get_header (m);
		if (h) { mono_method_header_get_code (h, &il, &maxst); has_il = 1; }
	}
	MonoMethodSignature *sig = wrapper ? NULL : mono_method_signature (m);
	char *sigs = sig ? mono_signature_full_name (sig) : NULL;
	int inflated = !wrapper && mono_method_get_context (m) != NULL;
	fprintf (f, "M\t%d\t%s\t%s\t%s\t%s\t%u\t%s\t%d\t%u\t0x%x\t0x%x\t%d\t%s\n", id, s_or (img, "?"),
	         k ? s_or (mono_class_get_namespace (outermost (k)), "") : "", cls, s_or (mono_method_get_name (m), "?"),
	         sig ? mono_signature_get_param_count (sig) : 0, s_or (sigs, "-"), has_il, il, flags, iflags, inflated, s_or (full, "?"));
	if (sigs) mono_free (sigs);
	if (full) mono_free (full);
}

static void write_out (void)
{
	if (written) return; written = 1;
	recording = 0;
	FILE *f = fopen (out_path, "w");
	if (!f) { fprintf (stderr, "[hotprof] cannot write %s\n", out_path); return; }
	double ms = tick_ms ();
	pthread_mutex_lock (&reg_mu);
	uint64_t tot_enters = 0; for (Thr *t = threads; t; t = t->next) tot_enters += t->enters;
	fprintf (f, "# shepherd hotprof v1: %d window(s), main-thread window wall %.3f ms, %d threads, %llu enters\n", windows, win_ticks * ms, nthreads, (unsigned long long) tot_enters);
	fprintf (f, "# S\tseg\tfrom_frame\twall_ms (main thread)\n");
	for (int k = 0; k <= cur_seg; k++) fprintf (f, "S\t%d\t%d\t%.3f\n", k, seg_from [k], seg_ticks [k] * ms);
	fprintf (f, "# T\tidx\tis_main\ttid\tname\tenters\tignored_leaves\tunwound\toverflow\tmethods\tedges\n");
	fprintf (f, "# M\tid\timage\tnamespace\tclass\tmethod\tparam_count\tsignature\thas_il\til_bytes\tflags\tiflags\tinflated\tfull_name\n");
	fprintf (f, "# m\tthread\tseg\tid\tcalls\tincl_ms\texcl_ms\n");
	fprintf (f, "# e\tthread\tseg\tcaller_id\tcallee_id\tcalls\tincl_ms\n");
	for (Thr *t = threads; t; t = t->next) {
		size_t sm = 0, se = 0; for (int k = 0; k < MAXSEG; k++) { sm += t->tab [k].st_used; se += t->tab [k].ed_used; }
		fprintf (f, "T\t%d\t%d\t%llu\t%s\t%llu\t%llu\t%llu\t%d\t%zu\t%zu\n", t->idx, t->is_main, (unsigned long long) t->tid, s_or (t->name, "(unnamed)"),
		         (unsigned long long) t->enters, (unsigned long long) t->ignored_leaves, (unsigned long long) t->unwound, t->overflow, sm, se);
	}
	int fresh;
	for (Thr *t = threads; t; t = t->next) for (int k = 0; k < MAXSEG; k++) {
		Tab *T = &t->tab [k];
		for (size_t i = 0; i < T->st_cap; i++) if (T->st [i].m) {
			int id = id_of (T->st [i].m, &fresh); if (fresh) meta (f, T->st [i].m, id);
			fprintf (f, "m\t%d\t%d\t%d\t%llu\t%.4f\t%.4f\n", t->idx, k, id, (unsigned long long) T->st [i].calls, T->st [i].incl * ms, T->st [i].excl * ms);
		}
	}
	for (Thr *t = threads; t; t = t->next) for (int k = 0; k < MAXSEG; k++) {
		Tab *T = &t->tab [k];
		for (size_t i = 0; i < T->ed_cap; i++) if (T->ed [i].b) {
			int a = id_of (T->ed [i].a, &fresh); if (fresh) meta (f, T->ed [i].a, a);
			int b = id_of (T->ed [i].b, &fresh); if (fresh) meta (f, T->ed [i].b, b);
			fprintf (f, "e\t%d\t%d\t%d\t%d\t%llu\t%.4f\n", t->idx, k, a, b, (unsigned long long) T->ed [i].calls, T->ed [i].incl * ms);
		}
	}
	pthread_mutex_unlock (&reg_mu);
	fclose (f);
	fprintf (stderr, "[hotprof] %d window(s), %.1f ms, %d threads, %d methods -> %s\n", windows, win_ticks * ms, nthreads, nid, out_path);
}
static void on_shutdown (MonoProfiler *p) { (void) p; write_out (); }

__attribute__((visibility ("default"))) void mono_profiler_init_shepherdhot (const char *desc)
{
	const char *c = desc ? strchr (desc, ':') : NULL;
	if (c && c [1]) strncpy (out_path, c + 1, sizeof (out_path) - 1);
	MonoProfilerHandle h = mono_profiler_create (&prof);
	mono_profiler_set_call_instrumentation_filter_callback (h, filter);
	mono_profiler_set_method_enter_callback (h, on_enter);
	mono_profiler_set_method_leave_callback (h, on_leave);
	mono_profiler_set_method_tail_call_callback (h, on_tail);
	mono_profiler_set_method_exception_leave_callback (h, on_exc_leave);
	mono_profiler_set_runtime_shutdown_begin_callback (h, on_shutdown);
	const char *ss = getenv ("SHEPHERD_SIMSTATS"); if (ss && ss [0] != 'm') win_from = atoi (ss);
	const char *sg = getenv ("SHEPHERD_HOT_SEGMENTS");
	if (sg && win_from > 0) { const char *q = sg; while (*q && nseg_at < MAXSEG - 1) { seg_at [nseg_at++] = atoi (q); q = strchr (q, ','); if (!q) break; q++; } }
	else if (sg) fprintf (stderr, "[hotprof] SHEPHERD_HOT_SEGMENTS needs an absolute SHEPHERD_SIMSTATS window: ignored\n");
	atexit (write_out);
	fprintf (stderr, "[hotprof] loaded, output %s, window from %d, %d segment boundaries\n", out_path, win_from, nseg_at);
}
