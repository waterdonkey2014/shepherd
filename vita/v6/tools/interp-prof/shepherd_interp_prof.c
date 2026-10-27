/* V6 stage 2 (vita/notes/engine-pass-speed.md): a Mac-only Mono profiler module that times every managed call of the main thread
 * inside a measurement window, for the vita-sim boot on the Mono interpreter (or the JIT, for the interpreter-vs-JIT split).
 *
 *   build:  vita/v6/tools/interp-prof/build.sh       -> out/libmono-profiler-shepherdprof.dylib (Xcode clang, public Mono API only)
 *   use:    DYLD_LIBRARY_PATH=<out dir> MONO_ENV_OPTIONS="--interpreter --profile=shepherdprof:<output.tsv>"
 *           (vita/v6/sim.sh prof <name>); the engine marks the window: Shepherd.Engine.Diag.ProfMarks.WindowStart / WindowEnd
 *           (empty never-inlined methods, called by SimStats at the first / last window frame).
 *
 * How: a call-instrumentation filter asks for enter + leave events on every method (the interpreter emits MINT_PROF_ENTER /
 * MINT_PROF_EXIT in each transformed method's prologue / return; inlined callees raise nothing and count in their caller, as they
 * run). Between the two marks, on the thread that called WindowStart only, a shadow stack gives each method its inclusive and
 * exclusive time (mach_absolute_time ticks), its call count, and each caller -> callee edge. A leave whose method is not on the shadow
 * stack (frames entered before the window) is ignored; frames above a matching one (left by an exception) are closed with it.
 * Recursion: inclusive time counts only the outermost activation.
 * The instrumentation itself costs time (two callbacks per call): absolute numbers are inflated, the split is the point.
 * Not part of any Vita build. */
#include <pthread.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

typedef struct _MonoProfiler MonoProfiler;
typedef struct _MonoProfilerDesc *MonoProfilerHandle;
typedef struct _MonoMethod MonoMethod;
typedef struct _MonoClass MonoClass;
typedef struct _MonoImage MonoImage;
typedef struct _MonoProfilerCallContext MonoProfilerCallContext;
typedef int mono_bool;
enum { INSTR_ENTER = 1 << 1, INSTR_LEAVE = 1 << 3, INSTR_TAIL = 1 << 5, INSTR_EXC_LEAVE = 1 << 6 };   /* profiler-types.h */

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
extern MonoImage *mono_class_get_image (MonoClass *klass);
extern const char *mono_image_get_name (MonoImage *image);
extern void mono_free (void *);
typedef struct _MonoObject MonoObject;
extern mono_bool mono_profiler_enable_allocations (void);
extern void mono_profiler_set_gc_allocation_callback (MonoProfilerHandle h, void (*cb) (MonoProfiler *, MonoObject *));
extern unsigned int mono_object_get_size (MonoObject *o);

struct _MonoProfiler { int unused; };
static MonoProfiler prof;

#include <mach/mach_time.h>
#include <mach/mach.h>
static inline uint64_t ticks (void) { return mach_absolute_time (); }
static uint64_t freq (void) { mach_timebase_info_data_t tb; mach_timebase_info (&tb); return (uint64_t) (1e9 * tb.denom / tb.numer); }

typedef struct { MonoMethod *m; uint64_t calls, incl, excl; uint32_t active; } Stat;
typedef struct { MonoMethod *a, *b; uint64_t calls, incl; } Edge;
static Stat *st; static size_t st_cap, st_used;
static Edge *ed; static size_t ed_cap, ed_used;
typedef struct { MonoMethod *m; uint64_t t0, child; Stat *s; } Fr;
static Fr stack [4096]; static int sp;
static MonoMethod *mark_start, *mark_end;
static pthread_t owner; static int recording, windows, overflow;
static uint64_t win_t0, win_ticks, enters, ignored_leaves, unwound;
static char out_path [2048] = "interp-prof.tsv";
static size_t h1 (const void *p);
/* allocations in the window on the recording thread, by (top-of-stack method, class); the class is read from the object's vtable
 * (MonoObject.vtable -> MonoVTable.klass, both first fields) */
typedef struct { MonoMethod *m; MonoClass *k; uint64_t n, bytes; } Al;
static Al *al; static size_t al_cap, al_used; static uint64_t al_n, al_bytes;
static Al *al_of (MonoMethod *m, MonoClass *k)
{
	if ((al_used + 1) * 2 > al_cap) {
		size_t nc = al_cap ? al_cap * 2 : 16384; Al *nt = calloc (nc, sizeof (Al));
		for (size_t i = 0; i < al_cap; i++) if (al [i].k) { size_t j = (h1 (al [i].m) ^ (h1 (al [i].k) * 31)) & (nc - 1); while (nt [j].k) j = (j + 1) & (nc - 1); nt [j] = al [i]; }
		free (al); al = nt; al_cap = nc;
	}
	size_t i = (h1 (m) ^ (h1 (k) * 31)) & (al_cap - 1);
	while (al [i].k && !(al [i].m == m && al [i].k == k)) i = (i + 1) & (al_cap - 1);
	if (!al [i].k) { al [i].m = m; al [i].k = k; al_used++; }
	return &al [i];
}
static int written;

static size_t h1 (const void *p) { uint64_t x = (uintptr_t) p; x ^= x >> 33; x *= 0xff51afd7ed558ccdULL; x ^= x >> 33; return (size_t) x; }
static Stat *stat_of (MonoMethod *m)
{
	if ((st_used + 1) * 2 > st_cap) {
		size_t nc = st_cap ? st_cap * 2 : 65536; Stat *nt = calloc (nc, sizeof (Stat));
		for (size_t i = 0; i < st_cap; i++) if (st [i].m) { size_t j = h1 (st [i].m) & (nc - 1); while (nt [j].m) j = (j + 1) & (nc - 1); nt [j] = st [i]; }
		free (st); st = nt; st_cap = nc;
		for (int k = 0; k < sp; k++) { size_t j = h1 (stack [k].m) & (st_cap - 1); while (st [j].m != stack [k].m) j = (j + 1) & (st_cap - 1); stack [k].s = &st [j]; }
	}
	size_t i = h1 (m) & (st_cap - 1);
	while (st [i].m && st [i].m != m) i = (i + 1) & (st_cap - 1);
	if (!st [i].m) { st [i].m = m; st_used++; }
	return &st [i];
}
static Edge *edge_of (MonoMethod *a, MonoMethod *b)
{
	if ((ed_used + 1) * 2 > ed_cap) {
		size_t nc = ed_cap ? ed_cap * 2 : 65536; Edge *nt = calloc (nc, sizeof (Edge));
		for (size_t i = 0; i < ed_cap; i++) if (ed [i].b) { size_t j = (h1 (ed [i].a) ^ (h1 (ed [i].b) * 31)) & (nc - 1); while (nt [j].b) j = (j + 1) & (nc - 1); nt [j] = ed [i]; }
		free (ed); ed = nt; ed_cap = nc;
	}
	size_t i = (h1 (a) ^ (h1 (b) * 31)) & (ed_cap - 1);
	while (ed [i].b && !(ed [i].a == a && ed [i].b == b)) i = (i + 1) & (ed_cap - 1);
	if (!ed [i].b) { ed [i].a = a; ed [i].b = b; ed_used++; }
	return &ed [i];
}

static int calls_on = 1;   /* SHEPHERD_PROF_CALLS=0: instrument only the two marks (thread CPU sampling alone, no overhead) */
static int filter (MonoProfiler *p, MonoMethod *m)
{
	(void) p;
	MonoClass *k = mono_method_get_class (m);
	const char *cn = k ? mono_class_get_name (k) : NULL, *mn = mono_method_get_name (m);
	int mark = 0;
	if (cn && mn && !strcmp (cn, "ProfMarks")) {
		if (!strcmp (mn, "WindowStart")) { mark_start = m; mark = 1; } else if (!strcmp (mn, "WindowEnd")) { mark_end = m; mark = 1; }
	}
	if (!calls_on && !mark) return 0;
	return INSTR_ENTER | INSTR_LEAVE | INSTR_TAIL | INSTR_EXC_LEAVE;
}

/* ---- SHEPHERD_PROF_THREADS=1: CPU time of every thread of the process over the window (Mach thread_info, names from
 * THREAD_EXTENDED_INFO), e.g. the game's "Dispatch Thread" polling loop ---- */
typedef struct { uint64_t tid; char name [64]; double cpu0, cpu1; int seen0, seen1; } Th;
static Th th [256]; static int nth, threads_on;
static uint64_t thr_wall0, thr_wall1;
static void sample_threads (int end)
{
	thread_act_array_t list; mach_msg_type_number_t n;
	if (task_threads (mach_task_self (), &list, &n) != KERN_SUCCESS) return;
	for (mach_msg_type_number_t i = 0; i < n; i++) {
		thread_identifier_info_data_t id; mach_msg_type_number_t c = THREAD_IDENTIFIER_INFO_COUNT;
		thread_extended_info_data_t ex; mach_msg_type_number_t c2 = THREAD_EXTENDED_INFO_COUNT;
		if (thread_info (list [i], THREAD_IDENTIFIER_INFO, (thread_info_t) &id, &c) != KERN_SUCCESS) continue;
		if (thread_info (list [i], THREAD_EXTENDED_INFO, (thread_info_t) &ex, &c2) != KERN_SUCCESS) continue;
		double cpu = (ex.pth_user_time + ex.pth_system_time) / 1e9;
		int k = 0; while (k < nth && th [k].tid != id.thread_id) k++;
		if (k == nth) { if (nth == 256) continue; nth++; th [k].tid = id.thread_id; th [k].seen0 = th [k].seen1 = 0; }
		strncpy (th [k].name, ex.pth_name, sizeof th [k].name - 1);
		if (end) { th [k].cpu1 = cpu; th [k].seen1 = 1; } else { th [k].cpu0 = cpu; th [k].seen0 = 1; }
		mach_port_deallocate (mach_task_self (), list [i]);
	}
	vm_deallocate (mach_task_self (), (vm_address_t) list, n * sizeof (thread_act_t));
	if (end) thr_wall1 = mach_absolute_time (); else thr_wall0 = mach_absolute_time ();
}

static void close_top (uint64_t now)
{
	Fr *f = &stack [--sp];
	uint64_t incl = now - f->t0, excl = incl > f->child ? incl - f->child : 0;
	Stat *s = f->s; s->calls++; s->excl += excl;
	if (--s->active == 0) s->incl += incl;
	if (sp > 0) { stack [sp - 1].child += incl; Edge *e = edge_of (stack [sp - 1].m, f->m); e->calls++; e->incl += incl; }
}

static void on_enter (MonoProfiler *p, MonoMethod *m, MonoProfilerCallContext *c)
{
	(void) p; (void) c;
	if (m == mark_start && m) {
		if (!recording) { if (threads_on) sample_threads (0); owner = pthread_self (); recording = 1; windows++; sp = 0; win_t0 = ticks (); }
		return;
	}
	if (!recording || !pthread_equal (pthread_self (), owner)) return;
	if (m == mark_end && m) { uint64_t now = ticks (); while (sp > 0) close_top (now); win_ticks += now - win_t0; recording = 0; if (threads_on) sample_threads (1); return; }
	if (sp >= (int) (sizeof stack / sizeof stack [0])) { overflow++; return; }
	enters++;
	Stat *s = stat_of (m); s->active++;
	stack [sp].m = m; stack [sp].s = s; stack [sp].child = 0; stack [sp].t0 = ticks (); sp++;
}
static void on_leave (MonoProfiler *p, MonoMethod *m, MonoProfilerCallContext *c)
{
	(void) p; (void) c;
	if (!recording || !pthread_equal (pthread_self (), owner)) return;
	uint64_t now = ticks ();
	int i = sp - 1; while (i >= 0 && stack [i].m != m) i--;
	if (i < 0) { ignored_leaves++; return; }
	while (sp - 1 > i) { close_top (now); unwound++; }
	close_top (now);
}
static void on_alloc (MonoProfiler *p, MonoObject *o)
{
	(void) p;
	if (!recording || !pthread_equal (pthread_self (), owner) || !o) return;
	MonoClass *k = **(MonoClass ***) o;
	unsigned int sz = mono_object_get_size (o);
	Al *a = al_of (sp > 0 ? stack [sp - 1].m : NULL, k); a->n++; a->bytes += sz; al_n++; al_bytes += sz;
}
static int cmp_al (const void *a, const void *b) { const Al *x = a, *y = b; return x->bytes < y->bytes ? 1 : x->bytes > y->bytes ? -1 : 0; }
static void on_exc_leave (MonoProfiler *p, MonoMethod *m, void *exc) { (void) exc; on_leave (p, m, NULL); }
static void on_tail (MonoProfiler *p, MonoMethod *m, MonoMethod *target) { (void) target; on_leave (p, m, NULL); }

static int cmp_excl (const void *a, const void *b) { const Stat *x = a, *y = b; return x->excl < y->excl ? 1 : x->excl > y->excl ? -1 : 0; }
static int cmp_edge (const void *a, const void *b) { const Edge *x = a, *y = b; return x->incl < y->incl ? 1 : x->incl > y->incl ? -1 : 0; }
static const char *img_of (MonoMethod *m) { MonoClass *k = mono_method_get_class (m); const char *n = k ? mono_image_get_name (mono_class_get_image (k)) : NULL; return n ? n : "?"; }

static void write_out (void)
{
	if (written) return; written = 1;
	FILE *f = fopen (out_path, "w");
	if (!f) { fprintf (stderr, "[interp-prof] cannot write %s\n", out_path); return; }
	double ms = 1000.0 / (double) freq ();
	size_t n = 0; Stat *all = malloc (st_used * sizeof (Stat) + 1);
	for (size_t i = 0; i < st_cap; i++) if (st [i].m) all [n++] = st [i];
	qsort (all, n, sizeof (Stat), cmp_excl);
	/* per image: exclusive time */
	typedef struct { const char *img; double excl; uint64_t calls; } Img; Img imgs [512]; int ni = 0; double total = 0;
	for (size_t i = 0; i < n; i++) {
		const char *im = img_of (all [i].m); double e = all [i].excl * ms; total += e; int k = 0;
		while (k < ni && strcmp (imgs [k].img, im)) k++;
		if (k == ni && ni < 512) { imgs [ni].img = im; imgs [ni].excl = 0; imgs [ni].calls = 0; ni++; }
		if (k < ni) { imgs [k].excl += e; imgs [k].calls += all [i].calls; }
	}
	for (int a = 0; a < ni; a++) for (int b = a + 1; b < ni; b++) if (imgs [b].excl > imgs [a].excl) { Img t = imgs [a]; imgs [a] = imgs [b]; imgs [b] = t; }
	fprintf (f, "# shepherd interp-prof: %d window(s), window wall %.3f ms, attributed %.3f ms; %llu enters, %llu ignored leaves, %llu unwound, %d stack overflows; methods %zu, edges %zu\n",
	         windows, win_ticks * ms, total, (unsigned long long) enters, (unsigned long long) ignored_leaves, (unsigned long long) unwound, overflow, n, ed_used);
	if (threads_on && thr_wall1 > thr_wall0) {
		double wall = (thr_wall1 - thr_wall0) * ms;
		fprintf (f, "## threads: CPU over the window (wall %.1f ms): cpu_ms\tshare_of_wall\tname\n", wall);
		for (int k = 0; k < nth; k++) if (th [k].seen0 && th [k].seen1)
			fprintf (f, "t\t%.1f\t%.3f\t%s\n", (th [k].cpu1 - th [k].cpu0) * 1000.0, (th [k].cpu1 - th [k].cpu0) * 1000.0 / wall, th [k].name [0] ? th [k].name : "(unnamed)");
	}
	fprintf (f, "## images (exclusive ms, calls)\n");
	for (int a = 0; a < ni; a++) fprintf (f, "image\t%.3f\t%llu\t%s\n", imgs [a].excl, (unsigned long long) imgs [a].calls, imgs [a].img);
	fprintf (f, "## methods: excl_ms\tincl_ms\tcalls\timage\tmethod\n");
	for (size_t i = 0; i < n; i++) {
		char *name = mono_method_full_name (all [i].m, 1);
		fprintf (f, "m\t%.3f\t%.3f\t%llu\t%s\t%s\n", all [i].excl * ms, all [i].incl * ms, (unsigned long long) all [i].calls, img_of (all [i].m), name ? name : "?");
		if (name) mono_free (name);
	}
	size_t ne = 0; Edge *es = malloc (ed_used * sizeof (Edge) + 1);
	for (size_t i = 0; i < ed_cap; i++) if (ed [i].b) es [ne++] = ed [i];
	qsort (es, ne, sizeof (Edge), cmp_edge);
	fprintf (f, "## edges (top 3000 by inclusive ms): incl_ms\tcalls\tcaller\tcallee\n");
	for (size_t i = 0; i < ne && i < 3000; i++) {
		char *a = mono_method_full_name (es [i].a, 0), *b = mono_method_full_name (es [i].b, 0);
		fprintf (f, "e\t%.3f\t%llu\t%s\t%s\n", es [i].incl * ms, (unsigned long long) es [i].calls, a ? a : "?", b ? b : "?");
		if (a) mono_free (a); if (b) mono_free (b);
	}
	size_t na = 0; Al *as = malloc (al_used * sizeof (Al) + 1);
	for (size_t i = 0; i < al_cap; i++) if (al [i].k) as [na++] = al [i];
	qsort (as, na, sizeof (Al), cmp_al);
	fprintf (f, "## allocations (window, recording thread): %llu objects, %llu bytes; top 400 by bytes: bytes\tcount\tclass\tallocating method\n", (unsigned long long) al_n, (unsigned long long) al_bytes);
	for (size_t i = 0; i < na && i < 400; i++) {
		char *mn = as [i].m ? mono_method_full_name (as [i].m, 0) : NULL;
		const char *ns = mono_class_get_namespace (as [i].k), *cn = mono_class_get_name (as [i].k);
		fprintf (f, "a\t%llu\t%llu\t%s%s%s\t%s\n", (unsigned long long) as [i].bytes, (unsigned long long) as [i].n, ns && *ns ? ns : "", ns && *ns ? "." : "", cn ? cn : "?", mn ? mn : "(no managed frame)");
		if (mn) mono_free (mn);
	}
	free (as);
	fclose (f); free (all); free (es);
	fprintf (stderr, "[interp-prof] %d window(s), %.1f ms, %zu methods -> %s\n", windows, win_ticks * ms, n, out_path);
}
static void on_shutdown (MonoProfiler *p) { (void) p; write_out (); }

__attribute__((visibility ("default"))) void mono_profiler_init_shepherdprof (const char *desc)
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
	if (getenv ("SHEPHERD_PROF_CALLS") && !strcmp (getenv ("SHEPHERD_PROF_CALLS"), "0")) calls_on = 0;
	if (getenv ("SHEPHERD_PROF_THREADS")) threads_on = 1;
	if (getenv ("SHEPHERD_PROF_ALLOC")) { mono_profiler_enable_allocations (); mono_profiler_set_gc_allocation_callback (h, on_alloc); }
	atexit (write_out);
	fprintf (stderr, "[interp-prof] loaded, output %s\n", out_path);
}
