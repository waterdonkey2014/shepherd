/* V6 stage 2 review (vita/notes/engine-pass-speed.md §8): a Mac-only Mono profiler module that takes ONE census of the sgen heap
 * at a GC the engine arms, to find what a run retains (the vita-sim's live heap at the menu read 96.1 MiB in most runs and exactly
 * 16 MiB more in some).
 *
 *   build:  vita/v6/tools/heap-walk/build.sh          -> out/libmono-profiler-shepherdheap.dylib (Xcode clang, public Mono API only)
 *   use:    DYLD_LIBRARY_PATH=<out dir> MONO_ENV_OPTIONS="--interpreter --profile=shepherdheap:<prefix>"   (vita/v6/sim.sh heapwalk)
 *           + SHEPHERD_HEAPWALK=1: after its two forced collections at the heap frame, SimStats creates <prefix>.arm, runs a third
 *           GC.Collect() and deletes the marker. The module arms at that collection's PRE_STOP_WORLD (the marker exists), turns
 *           on Mono's GC-root reports for it, and at PRE_START_WORLD (world stopped, marking done: the only point where
 *           mono_gc_walk_heap may run) walks every live object with its references.
 *   output: <prefix>-classes.tsv   class \t objects \t bytes (bytes descending)
 *           <prefix>-big.txt       every object >= 256 KiB: class, size, array length, and the shortest referrer path back to a
 *                                  reported GC root (static field, conservative native-stack pin, finalizer queue, ...) or to an
 *                                  object nothing in the heap references (then: a GC handle, the interpreter's own stack, or a
 *                                  root kind Mono does not report)
 *           <prefix>-summary.txt   totals, roots by source
 *           <prefix>-bigalloc.txt  (SHEPHERD_HEAPWALK_ALLOC_MIN=<bytes>) every allocation of at least that size over the whole run:
 *                                  class, size, thread, the managed stack (Mono's allocation events: slows the run a little)
 * Names are resolved after the world restarts (POST_START_WORLD_UNLOCKED), from pointers recorded while it was stopped.
 * Not part of any Vita build: the Vita runtime has no profiler modules and no dlopen. */
#include <pthread.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <unistd.h>

typedef struct _MonoProfiler MonoProfiler;
typedef struct _MonoProfilerDesc *MonoProfilerHandle;
typedef struct _MonoClass MonoClass;
typedef struct _MonoObject MonoObject;
typedef struct _MonoType MonoType;
typedef struct _MonoVTable MonoVTable;
typedef struct _MonoClassField MonoClassField;
typedef int mono_bool;
typedef unsigned char mono_byte;
typedef int (*MonoGCReferences) (MonoObject *obj, MonoClass *klass, uintptr_t size, uintptr_t num, MonoObject **refs, uintptr_t *offsets, void *data);
enum { EV_START = 0, EV_END = 5, EV_PRE_STOP = 6, EV_POST_STOP = 7, EV_PRE_START = 8, EV_POST_START = 9, EV_PRE_STOP_LOCKED = 10, EV_POST_START_UNLOCKED = 11 };
enum { FIELD_STATIC = 0x10 };

extern MonoProfilerHandle mono_profiler_create (MonoProfiler *prof);
extern void mono_profiler_set_gc_event_callback (MonoProfilerHandle h, void (*cb) (MonoProfiler *, int, uint32_t, mono_bool));
extern void mono_profiler_set_gc_roots_callback (MonoProfilerHandle h, void (*cb) (MonoProfiler *, uint64_t, const mono_byte *const *, MonoObject *const *));
extern void mono_profiler_set_gc_root_register_callback (MonoProfilerHandle h, void (*cb) (MonoProfiler *, const mono_byte *, uintptr_t, int, const void *, const char *));
extern void mono_profiler_set_gc_root_unregister_callback (MonoProfilerHandle h, void (*cb) (MonoProfiler *, const mono_byte *));
extern int mono_gc_walk_heap (int flags, MonoGCReferences callback, void *data);
extern char *mono_type_get_name (MonoType *type);
extern MonoType *mono_class_get_type (MonoClass *klass);
extern int mono_class_get_rank (MonoClass *klass);
extern int32_t mono_array_element_size (MonoClass *ac);
extern MonoClass *mono_class_get_parent (MonoClass *klass);
extern MonoClassField *mono_class_get_fields (MonoClass *klass, void **iter);
extern const char *mono_field_get_name (MonoClassField *field);
extern uint32_t mono_field_get_offset (MonoClassField *field);
extern uint32_t mono_field_get_flags (MonoClassField *field);
extern MonoClass *mono_vtable_class (MonoVTable *vtable);
extern void mono_free (void *);
typedef struct _MonoMethod MonoMethod;
extern mono_bool mono_profiler_enable_allocations (void);
extern void mono_profiler_set_gc_allocation_callback (MonoProfilerHandle h, void (*cb) (MonoProfiler *, MonoObject *));
extern unsigned int mono_object_get_size (MonoObject *o);
extern MonoClass *mono_object_get_class (MonoObject *o);
extern void mono_stack_walk (mono_bool (*func) (MonoMethod *, int32_t, int32_t, mono_bool, void *), void *user_data);
extern char *mono_method_full_name (MonoMethod *method, mono_bool signature);

struct _MonoProfiler { int unused; };
static MonoProfiler prof;
static MonoProfilerHandle H;
static char prefix [2048] = "heapwalk", marker [2100];
static volatile int armed, walked, written;

typedef struct { uintptr_t a; MonoClass *k; uint64_t size; } Obj;
typedef struct { uintptr_t src, dst; uint32_t off; } Edge;
typedef struct { uintptr_t obj; const mono_byte *addr; } Root;
typedef struct { const mono_byte *start; uintptr_t size; int source; const void *key; char *name; int live; } Reg;
static Obj *objs; static size_t n_obj, cap_obj;
static Edge *edges; static size_t n_edge, cap_edge;
static Root *roots; static size_t n_root, cap_root;
static Reg *regs; static size_t n_reg, cap_reg;
static pthread_mutex_t reg_mu = PTHREAD_MUTEX_INITIALIZER;
static uint64_t walk_ns;

#define GROW(p, n, c, init) do { if ((n) >= (c)) { (c) = (c) ? (c) * 2 : (init); (p) = realloc ((p), (c) * sizeof (*(p))); } } while (0)

static int on_obj (MonoObject *o, MonoClass *k, uintptr_t size, uintptr_t num, MonoObject **refs, uintptr_t *offsets, void *data)
{
	(void) data;
	if (size) { GROW (objs, n_obj, cap_obj, 1 << 20); objs [n_obj++] = (Obj) { (uintptr_t) o, k, size }; }
	for (uintptr_t i = 0; i < num; i++) {
		if (!refs [i]) continue;
		GROW (edges, n_edge, cap_edge, 1 << 22);
		edges [n_edge++] = (Edge) { (uintptr_t) o, (uintptr_t) refs [i], (uint32_t) offsets [i] };
	}
	return 0;
}
static void on_roots (MonoProfiler *p, uint64_t count, const mono_byte *const *addresses, MonoObject *const *objects)
{
	(void) p;
	if (!armed) return;
	for (uint64_t i = 0; i < count; i++) { GROW (roots, n_root, cap_root, 1 << 16); roots [n_root++] = (Root) { (uintptr_t) objects [i], addresses [i] }; }
}
static void on_reg (MonoProfiler *p, const mono_byte *start, uintptr_t size, int source, const void *key, const char *name)
{
	(void) p;
	pthread_mutex_lock (&reg_mu);
	GROW (regs, n_reg, cap_reg, 4096);
	regs [n_reg++] = (Reg) { start, size, source, key, name ? strdup (name) : NULL, 1 };
	pthread_mutex_unlock (&reg_mu);
}
static void on_unreg (MonoProfiler *p, const mono_byte *start)
{
	(void) p;
	pthread_mutex_lock (&reg_mu);
	for (size_t i = n_reg; i-- > 0;) if (regs [i].live && regs [i].start == start) { regs [i].live = 0; break; }
	pthread_mutex_unlock (&reg_mu);
}

#include <mach/mach_time.h>
static uint64_t now_ns (void) { static mach_timebase_info_data_t tb; if (!tb.denom) mach_timebase_info (&tb); return mach_absolute_time () * tb.numer / tb.denom; }
static void write_out (void);

static void on_gc (MonoProfiler *p, int ev, uint32_t gen, mono_bool serial)
{
	(void) p; (void) gen; (void) serial;
	if (ev == EV_PRE_STOP && !walked && !armed && access (marker, F_OK) == 0) {
		armed = 1;
		mono_profiler_set_gc_roots_callback (H, on_roots);   /* sgen reports roots (and records pins) only while this is set */
	} else if (ev == EV_PRE_START && armed && !walked) {
		uint64_t t0 = now_ns ();
		mono_gc_walk_heap (0, on_obj, NULL);
		walk_ns = now_ns () - t0;
		walked = 1;
	} else if (ev == EV_POST_START_UNLOCKED && walked && !written) {
		mono_profiler_set_gc_roots_callback (H, NULL);
		armed = 0; written = 1;
		write_out ();
	}
}

/* ---- analysis (world running again; the recorded addresses are only compared, never dereferenced) ---- */
static int cmp_obj (const void *a, const void *b) { uintptr_t x = ((const Obj *) a)->a, y = ((const Obj *) b)->a; return x < y ? -1 : x > y; }
static int cmp_edge_dst (const void *a, const void *b) { uintptr_t x = ((const Edge *) a)->dst, y = ((const Edge *) b)->dst; return x < y ? -1 : x > y; }
static int cmp_root (const void *a, const void *b) { uintptr_t x = ((const Root *) a)->obj, y = ((const Root *) b)->obj; return x < y ? -1 : x > y; }
static long find_obj (uintptr_t a)
{
	size_t lo = 0, hi = n_obj;
	while (lo < hi) { size_t m = (lo + hi) / 2; if (objs [m].a < a) lo = m + 1; else hi = m; }
	return lo < n_obj && objs [lo].a == a ? (long) lo : -1;
}
static size_t first_edge_to (uintptr_t dst)
{
	size_t lo = 0, hi = n_edge;
	while (lo < hi) { size_t m = (lo + hi) / 2; if (edges [m].dst < dst) lo = m + 1; else hi = m; }
	return lo;
}
static long root_of (uintptr_t obj)
{
	size_t lo = 0, hi = n_root;
	while (lo < hi) { size_t m = (lo + hi) / 2; if (roots [m].obj < obj) lo = m + 1; else hi = m; }
	return lo < n_root && roots [lo].obj == obj ? (long) lo : -1;
}

/* class names, cached per MonoClass* */
typedef struct { MonoClass *k; char *name; uint64_t n, bytes; } Cls;
static Cls *cls; static size_t cls_cap, cls_used;
static size_t hk (const void *p) { uint64_t x = (uintptr_t) p; x ^= x >> 33; x *= 0xff51afd7ed558ccdULL; x ^= x >> 33; return (size_t) x; }
static Cls *cls_of (MonoClass *k)
{
	if ((cls_used + 1) * 2 > cls_cap) {
		size_t nc = cls_cap ? cls_cap * 2 : 16384; Cls *nt = calloc (nc, sizeof (Cls));
		for (size_t i = 0; i < cls_cap; i++) if (cls [i].k) { size_t j = hk (cls [i].k) & (nc - 1); while (nt [j].k) j = (j + 1) & (nc - 1); nt [j] = cls [i]; }
		free (cls); cls = nt; cls_cap = nc;
	}
	size_t j = hk (k) & (cls_cap - 1);
	while (cls [j].k && cls [j].k != k) j = (j + 1) & (cls_cap - 1);
	if (!cls [j].k) { cls [j].k = k; char *s = mono_type_get_name (mono_class_get_type (k)); cls [j].name = strdup (s ? s : "?"); if (s) mono_free (s); cls_used++; }
	return &cls [j];
}
static const char *cname (MonoClass *k) { return k ? cls_of (k)->name : "?"; }

/* the instance (or, for statics, static) field at a byte offset; arrays: the element index */
static void field_at (MonoClass *k, uint32_t off, int want_static, char *buf, size_t n)
{
	if (!want_static && mono_class_get_rank (k) > 0) {
		int es = mono_array_element_size (k); uint32_t hdr = 4 * sizeof (void *);   /* MonoArray: vtable, sync, bounds, max_length */
		snprintf (buf, n, "[%u]", es > 0 && off >= hdr ? (unsigned) ((off - hdr) / (uint32_t) es) : 0u); return;
	}
	for (MonoClass *c = k; c; c = want_static ? NULL : mono_class_get_parent (c)) {
		void *it = NULL; MonoClassField *f; MonoClassField *best = NULL; uint32_t bo = 0;
		while ((f = mono_class_get_fields (c, &it))) {
			int st = (mono_field_get_flags (f) & FIELD_STATIC) != 0;
			if (st != want_static) continue;
			uint32_t fo = mono_field_get_offset (f);
			if (fo <= off && (!best || fo > bo)) { best = f; bo = fo; }   /* a field of a struct: the ref is inside it */
		}
		if (best) { if (bo == off) snprintf (buf, n, ".%s", mono_field_get_name (best)); else snprintf (buf, n, ".%s+%u", mono_field_get_name (best), off - bo); return; }
	}
	snprintf (buf, n, "+%u", off);
}

static const char *src_name (int s)
{
	static const char *n [] = { "external", "stack", "finalizer-queue", "static", "thread-static", "context-static", "gc-handle", "jit", "threading",
		"domain", "reflection", "marshal", "thread-pool", "debugger", "handle", "ephemeron", "toggleref" };
	return s >= 0 && s < (int) (sizeof n / sizeof *n) ? n [s] : "?";
}
static void describe_root (const Root *r, char *buf, size_t n)
{
	const mono_byte *a = r->addr;
	pthread_mutex_lock (&reg_mu);
	for (size_t i = n_reg; i-- > 0;) {
		const Reg *g = &regs [i];
		if (!g->live || a < g->start || a >= g->start + (g->size ? g->size : 1)) continue;
		if (g->source == 3 && g->key) {   /* static fields: key = MonoVTable */
			MonoClass *k = mono_vtable_class ((MonoVTable *) g->key); char f [512];
			field_at (k, (uint32_t) (a - g->start), 1, f, sizeof f);
			snprintf (buf, n, "root %s %s%s", src_name (g->source), cname (k), f);
		} else snprintf (buf, n, "root %s '%s' (+%ld)", src_name (g->source), g->name ? g->name : "", (long) (a - g->start));
		pthread_mutex_unlock (&reg_mu); return;
	}
	pthread_mutex_unlock (&reg_mu);
	snprintf (buf, n, "root at %p: not a registered range = a thread's native stack / saved registers (conservative pin)", (void *) a);
}

static void path_to_root (FILE *f, size_t target)
{
	/* BFS over reversed edges from the target to the nearest reported root or referrer-less object */
	static long *parent; static uint32_t *poff; static size_t pcap;
	if (pcap < n_obj) { free (parent); free (poff); pcap = n_obj; parent = malloc (pcap * sizeof (long)); poff = malloc (pcap * sizeof (uint32_t)); }
	for (size_t i = 0; i < n_obj; i++) parent [i] = -2;
	size_t *q = malloc (n_obj * sizeof (size_t)); size_t qh = 0, qt = 0;
	q [qt++] = target; parent [target] = -1;
	long hit = -1; int hit_root = 0; size_t visited = 1;
	while (qh < qt) {
		size_t cur = q [qh++];
		if (root_of (objs [cur].a) >= 0) { hit = (long) cur; hit_root = 1; break; }
		size_t e = first_edge_to (objs [cur].a); int any = 0;
		for (; e < n_edge && edges [e].dst == objs [cur].a; e++) {
			long s = find_obj (edges [e].src); if (s < 0) continue;
			any = 1;
			if (parent [s] != -2) continue;
			parent [s] = (long) cur; poff [s] = edges [e].off; q [qt++] = (size_t) s; visited++;
		}
		if (!any && hit < 0) hit = (long) cur;   /* remember the first referrer-less object; keep looking for a reported root */
	}
	free (q);
	if (hit < 0) { fprintf (f, "    (no path: %zu objects reachable backwards)\n", visited); return; }
	char buf [1024];
	if (hit_root) { describe_root (&roots [root_of (objs [hit].a)], buf, sizeof buf); fprintf (f, "    %s\n", buf); }
	else fprintf (f, "    no heap referrer and not a reported root (a GC handle, the interpreter stack, or an unreported root kind); %zu objects searched\n", visited);
	for (long c = hit; c >= 0; c = parent [c]) {
		if (parent [c] >= 0) { field_at (objs [c].k, poff [c], 0, buf, sizeof buf); }
		else buf [0] = 0;
		fprintf (f, "      %s @%p (%llu B)%s%s\n", cname (objs [c].k), (void *) objs [c].a, (unsigned long long) objs [c].size, buf [0] ? "  via " : "", buf);
	}
}

static int cmp_cls_bytes (const void *a, const void *b) { uint64_t x = ((const Cls *) a)->bytes, y = ((const Cls *) b)->bytes; return x < y ? 1 : x > y ? -1 : 0; }

static void write_out (void)
{
	qsort (objs, n_obj, sizeof (Obj), cmp_obj);
	qsort (edges, n_edge, sizeof (Edge), cmp_edge_dst);
	qsort (roots, n_root, sizeof (Root), cmp_root);
	uint64_t total = 0;
	for (size_t i = 0; i < n_obj; i++) { Cls *c = cls_of (objs [i].k); c->n++; c->bytes += objs [i].size; total += objs [i].size; }
	char path [2200];
	snprintf (path, sizeof path, "%s-classes.tsv", prefix);
	FILE *f = fopen (path, "w");
	if (f) {
		Cls *v = malloc (cls_used * sizeof (Cls)); size_t m = 0;
		for (size_t i = 0; i < cls_cap; i++) if (cls [i].k) v [m++] = cls [i];
		qsort (v, m, sizeof (Cls), cmp_cls_bytes);
		fprintf (f, "class\tobjects\tbytes\n");
		for (size_t i = 0; i < m; i++) fprintf (f, "%s\t%llu\t%llu\n", v [i].name, (unsigned long long) v [i].n, (unsigned long long) v [i].bytes);
		fclose (f); free (v);
	}
	snprintf (path, sizeof path, "%s-big.txt", prefix);
	f = fopen (path, "w");
	if (f) {
		fprintf (f, "# objects >= 256 KiB at the armed GC, with the shortest referrer path to a root\n");
		/* kit3: SHEPHERD_HEAPWALK_PATHS="<class>[,<class>...]" also explains the first SHEPHERD_HEAPWALK_PATHS_N (default 3) objects
		 * of each named class (exact class name, as in -classes.tsv), whatever their size: what retains them */
		const char *pc = getenv ("SHEPHERD_HEAPWALK_PATHS"); const char *pn = getenv ("SHEPHERD_HEAPWALK_PATHS_N");
		int per = pn ? atoi (pn) : 3; char want [64][256]; int nw = 0, seen [64] = { 0 };
		for (const char *s = pc; s && *s && nw < 64; ) {
			const char *e = strchr (s, ','); size_t l = e ? (size_t) (e - s) : strlen (s);
			if (l > 0 && l < sizeof want [0]) { memcpy (want [nw], s, l); want [nw][l] = 0; nw++; }
			s = e ? e + 1 : s + l;
		}
		for (size_t i = 0; i < n_obj; i++) {
			int w = -1;
			for (int j = 0; j < nw; j++) if (seen [j] < per && strcmp (cname (objs [i].k), want [j]) == 0) { w = j; break; }
			if (objs [i].size < 256 * 1024 && w < 0) continue;
			if (w >= 0) seen [w]++;
			fprintf (f, "%s @%p %llu B (%.2f MiB)\n", cname (objs [i].k), (void *) objs [i].a, (unsigned long long) objs [i].size, objs [i].size / 1048576.0);
			path_to_root (f, i);
		}
		fclose (f);
	}
	snprintf (path, sizeof path, "%s-summary.txt", prefix);
	f = fopen (path, "w");
	if (f) {
		fprintf (f, "objects %zu, bytes %llu (%.2f MiB), references %zu, classes %zu, reported roots %zu, walk %.1f ms\n",
			n_obj, (unsigned long long) total, total / 1048576.0, n_edge, cls_used, n_root, walk_ns / 1e6);
		uint64_t big = 0; size_t nbig = 0;
		for (size_t i = 0; i < n_obj; i++) if (objs [i].size > 8000) { big += objs [i].size; nbig++; }
		fprintf (f, "objects > 8000 B (LOS-sized): %zu, %.2f MiB\n", nbig, big / 1048576.0);
		fclose (f);
	}
	fprintf (stderr, "[heap-walk] %zu objects, %.2f MiB, %zu refs, %zu roots -> %s-*\n", n_obj, total / 1048576.0, n_edge, n_root, prefix);
}

/* ---- big allocations, with their managed stacks ---- */
static unsigned alloc_min; static FILE *alloc_f; static pthread_mutex_t alloc_mu = PTHREAD_MUTEX_INITIALIZER; static uint64_t t_start;
static mono_bool on_frame (MonoMethod *m, int32_t no, int32_t il, mono_bool managed, void *data)
{
	int *depth = data; (void) no;
	if (!m || !managed) return 0;
	char *n = mono_method_full_name (m, 1);
	fprintf (alloc_f, "      %s IL_%04x\n", n ? n : "?", (unsigned) il); if (n) mono_free (n);
	return ++*depth >= 40;
}
static void on_alloc (MonoProfiler *p, MonoObject *o)
{
	(void) p;
	unsigned size = mono_object_get_size (o);
	if (size < alloc_min) return;
	pthread_mutex_lock (&alloc_mu);
	char *cn = mono_type_get_name (mono_class_get_type (mono_object_get_class (o)));
	uint64_t tid; pthread_threadid_np (NULL, &tid);
	fprintf (alloc_f, "%s %u B (%.2f MiB) @%p thread %llu at +%.3f s\n", cn ? cn : "?", size, size / 1048576.0, (void *) o, (unsigned long long) tid, (now_ns () - t_start) / 1e9);
	if (cn) mono_free (cn);
	int depth = 0; mono_stack_walk (on_frame, &depth);
	fflush (alloc_f);
	pthread_mutex_unlock (&alloc_mu);
}

__attribute__((visibility ("default"))) void mono_profiler_init_shepherdheap (const char *desc)
{
	const char *c = desc ? strchr (desc, ':') : NULL;
	if (c && c [1]) strncpy (prefix, c + 1, sizeof (prefix) - 1);
	snprintf (marker, sizeof marker, "%s.arm", prefix);
	H = mono_profiler_create (&prof);
	mono_profiler_set_gc_event_callback (H, on_gc);
	mono_profiler_set_gc_root_register_callback (H, on_reg);
	mono_profiler_set_gc_root_unregister_callback (H, on_unreg);
	const char *am = getenv ("SHEPHERD_HEAPWALK_ALLOC_MIN");
	if (am && atoi (am) > 0) {
		char path [2200]; snprintf (path, sizeof path, "%s-bigalloc.txt", prefix);
		alloc_f = fopen (path, "w"); alloc_min = (unsigned) atoi (am); t_start = now_ns ();
		if (alloc_f) { mono_profiler_enable_allocations (); mono_profiler_set_gc_allocation_callback (H, on_alloc); }
	}
	fprintf (stderr, "[heap-walk] loaded, marker %s%s\n", marker, alloc_f ? ", logging big allocations" : "");
}
