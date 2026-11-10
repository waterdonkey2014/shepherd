/*
 * SHEPHERD-VITA device kit #14, track newlib (vita/notes/kit14-newlib.md section 1): THE NEWLIB CENSUS BY OWNER -- a vlx-only object
 * (never in an eboot; TEST ONLY). Linked into a vlx ELF with the --wrap flags vita/vlx/k14_mcensus.sh prints, it records every allocation
 * the program makes from newlib's heap and from the runtime's mempools, keyed by WHO asked for it (the caller's return address), and
 * writes the live picture each time the host prints its census line (the host's "<tag> cheap:" line calls mono_vita_census; this object
 * wraps that call), plus a snapshot taken at newlib's high-water mark.
 *
 *   malloc family   _malloc_r / _calloc_r / _realloc_r / _memalign_r / _free_r are the recording points (every newlib allocation goes
 *                   through them); malloc / calloc / realloc / memalign / free, eglib's monoeg_malloc / malloc0 / realloc / try_*,
 *                   its containers (lists, hash tables, arrays, strings), operator new and the runtime's internal hash tables are
 *                   OWNER FRAMES: an allocation inside one is booked to the caller of the outermost owner frame of its thread (the
 *                   thread is the vlx thread object in TPIDRURO). Live bytes = newlib chunk sizes (malloc_usable_size + 4).
 *   mempool family  mono_mem_manager_alloc / alloc0 / strdup / alloc0_lock_free (kind MM, by memory manager), mono_image_alloc /
 *                   alloc0 / strdup (IMG) and direct mono_mempool_alloc / alloc0 / strdup (MP) -- cumulative bytes (requested and
 *                   8-aligned) by caller; a mempool call nested inside an MM / IMG call is not booked twice, it only maps that
 *                   memory manager / image to its pool. Pools are tracked (mono_mempool_destroy removes one) and reported with
 *                   mono_mempool_get_allocated: allocated - booked = the chunk slack (+ 24-byte chunk headers).
 *
 * Output (raw Linux syscalls; the vlx root is /vlxroot): /vlxroot/k14mc-<n>.txt at the n-th census call, /vlxroot/k14mc-peak.txt (the
 * snapshot at the highest live total seen so far, rewritten at each call). Lines: "M <pc> <live bytes> <live count>", "A <kind> <mm#>
 * <pc> <aligned bytes> <requested bytes> <count>", "P <pool> <allocated> <booked> <count> <how> <owner>", "T ..." totals. vita/vlx/
 * k14_mcensus.py symbolizes and sums them. Nothing here changes what the program computes: every wrapper returns the real function's
 * result; the tables live in mmap'd host memory outside the modelled Vita budget.
 */
#include <stdint.h>
#include <stddef.h>

static inline long ks6 (long n, long a, long b, long c, long d, long e, long f)
{
	register long r0 asm ("r0") = a; register long r1 asm ("r1") = b; register long r2 asm ("r2") = c;
	register long r3 asm ("r3") = d; register long r4 asm ("r4") = e; register long r5 asm ("r5") = f;
	register long r7 asm ("r7") = n;
	asm volatile ("svc #0" : "+r" (r0) : "r" (r1), "r" (r2), "r" (r3), "r" (r4), "r" (r5), "r" (r7) : "memory");
	return r0;
}
enum { K_NR_write = 4, K_NR_open = 5, K_NR_close = 6, K_NR_sched_yield = 158, K_NR_mmap2 = 192 };

/* ------------------------------------------------------------------ the lock and the thread state */
static volatile int k_lock;
static void lk (void) { int n = 0; while (__sync_lock_test_and_set (&k_lock, 1)) { if (++n > 64) { ks6 (K_NR_sched_yield, 0, 0, 0, 0, 0, 0); n = 0; } } }
static void ulk (void) { __sync_lock_release (&k_lock); }

typedef struct { volatile uintptr_t key; int depth; uintptr_t owner; int mdepth; int mkind; uintptr_t mobj; int quiet; } kts;
#define KTS_N 1024
static kts k_ts [KTS_N];
static kts *ts (void)
{
	uintptr_t t; asm volatile ("mrc p15, 0, %0, c13, c0, 3" : "=r" (t));
	t |= 1;   /* (never 0: 0 = a free slot) */
	unsigned h = (unsigned) ((t >> 4) * 2654435761u) & (KTS_N - 1);
	for (int i = 0; i < KTS_N; i++, h = (h + 1) & (KTS_N - 1)) {
		uintptr_t k = k_ts [h].key;
		if (k == t) return &k_ts [h];
		if (!k && __sync_bool_compare_and_swap (&k_ts [h].key, 0, t)) return &k_ts [h];
	}
	return &k_ts [0];   /* (full: shared, approximate) */
}

/* ------------------------------------------------------------------ host memory for the tables */
static void *kmap (size_t n)
{
	long r = ks6 (K_NR_mmap2, 0, (long) n, 3, 0x22, -1, 0);
	return (unsigned long) r > 0xfffff000ul ? 0 : (void *) r;
}

/* live newlib chunks: pointer -> owner, chunk size (linear probing, backward-shift deletion) */
typedef struct { uintptr_t p; uintptr_t owner; uint32_t sz; } kptr;
#define KP_BITS 23
#define KP_N (1u << KP_BITS)
static kptr *k_ptr;
static uint32_t k_ptr_used, k_ptr_full;
/* owners of live chunks: pc -> live bytes, count */
typedef struct { uintptr_t pc; int64_t live; int32_t cnt; uint32_t pad; } kown;
#define KO_N (1u << 16)
static kown *k_own, *k_own_peak;
/* mempool bookings: (kind, mm#, pc) -> aligned, requested, count */
typedef struct { uintptr_t pc; uint32_t kind; uint32_t mm; int64_t al; int64_t rq; int32_t cnt; uint32_t pad; } kmp;
#define KM_N (1u << 16)
static kmp *k_mp, *k_mp_peak;
/* pools: pool -> booked aligned bytes, count, how (1 MM, 2 IMG, 3 direct), its owner object (mm / image) */
typedef struct { uintptr_t pool; int64_t booked; int32_t cnt; uint32_t how; uintptr_t obj; uint32_t pad; } kpool;
#define KL_N (1u << 15)
static kpool *k_pool;
/* memory managers seen (index = the mm# above) */
#define KMM_N 256
static uintptr_t k_mm [KMM_N]; static int k_mm_n;

static int64_t k_live, k_live_peak, k_snap_at, k_untracked_free, k_mallocs, k_frees;
static int k_ready, k_failed, k_seq;

static void kinit (void)   /* (under the lock) */
{
	if (k_ready || k_failed) return;
	k_ptr = kmap ((size_t) KP_N * sizeof (kptr));
	k_own = kmap ((size_t) KO_N * sizeof (kown)); k_own_peak = kmap ((size_t) KO_N * sizeof (kown));
	k_mp = kmap ((size_t) KM_N * sizeof (kmp)); k_mp_peak = kmap ((size_t) KM_N * sizeof (kmp));
	k_pool = kmap ((size_t) KL_N * sizeof (kpool));
	if (!k_ptr || !k_own || !k_own_peak || !k_mp || !k_mp_peak || !k_pool) { k_failed = 1; return; }
	k_ready = 1;
}
static inline unsigned hp (uintptr_t p, unsigned bits) { return (unsigned) ((p >> 3) * 2654435761u) >> (32 - bits); }

static kown *own_get (uintptr_t pc)
{
	unsigned h = hp (pc, 16);
	for (unsigned i = 0; i < KO_N; i++, h = (h + 1) & (KO_N - 1)) {
		if (k_own [h].pc == pc) return &k_own [h];
		if (!k_own [h].pc) { k_own [h].pc = pc; return &k_own [h]; }
	}
	return &k_own [0];
}
static void ptr_put (uintptr_t p, uintptr_t owner, uint32_t sz)
{
	if (k_ptr_used >= KP_N - KP_N / 8) { k_ptr_full++; return; }
	unsigned h = hp (p, KP_BITS);
	while (k_ptr [h].p && k_ptr [h].p != p) h = (h + 1) & (KP_N - 1);
	if (k_ptr [h].p == p) {   /* (a chunk we never saw freed: replace) */
		kown *o = own_get (k_ptr [h].owner); o->live -= k_ptr [h].sz; o->cnt--; k_live -= k_ptr [h].sz;
	} else k_ptr_used++;
	k_ptr [h].p = p; k_ptr [h].owner = owner; k_ptr [h].sz = sz;
	kown *o = own_get (owner); o->live += sz; o->cnt++; k_live += sz;
}
static int ptr_del (uintptr_t p)
{
	unsigned h = hp (p, KP_BITS);
	while (k_ptr [h].p && k_ptr [h].p != p) h = (h + 1) & (KP_N - 1);
	if (!k_ptr [h].p) return 0;
	kown *o = own_get (k_ptr [h].owner); o->live -= k_ptr [h].sz; o->cnt--; k_live -= k_ptr [h].sz;
	k_ptr_used--;
	/* backward-shift deletion */
	unsigned i = h, j = h;
	for (;;) {
		j = (j + 1) & (KP_N - 1);
		if (!k_ptr [j].p) break;
		unsigned home = hp (k_ptr [j].p, KP_BITS);
		if ((i <= j) ? (i < home && home <= j) : (i < home || home <= j)) continue;
		k_ptr [i] = k_ptr [j]; i = j;
	}
	k_ptr [i].p = 0;
	return 1;
}
static kmp *mp_get (uint32_t kind, uint32_t mm, uintptr_t pc)
{
	unsigned h = hp (pc ^ (kind << 28) ^ (mm << 20), 16);
	for (unsigned i = 0; i < KM_N; i++, h = (h + 1) & (KM_N - 1)) {
		kmp *e = &k_mp [h];
		if (e->pc == pc && e->kind == kind && e->mm == mm) return e;
		if (!e->pc) { e->pc = pc; e->kind = kind; e->mm = mm; return e; }
	}
	return &k_mp [0];
}
static kpool *pool_get (uintptr_t pool, int create)
{
	unsigned h = hp (pool, 15);
	for (unsigned i = 0; i < KL_N; i++, h = (h + 1) & (KL_N - 1)) {
		if (k_pool [h].pool == pool) return &k_pool [h];
		if (!k_pool [h].pool) { if (!create) return 0; k_pool [h].pool = pool; return &k_pool [h]; }
	}
	return 0;
}
static void pool_del (uintptr_t pool)
{
	unsigned h = hp (pool, 15);
	while (k_pool [h].pool && k_pool [h].pool != pool) h = (h + 1) & (KL_N - 1);
	if (!k_pool [h].pool) return;
	unsigned i = h, j = h;
	for (;;) {
		j = (j + 1) & (KL_N - 1);
		if (!k_pool [j].pool) break;
		unsigned home = hp (k_pool [j].pool, 15);
		if ((i <= j) ? (i < home && home <= j) : (i < home || home <= j)) continue;
		k_pool [i] = k_pool [j]; i = j;
	}
	k_pool [i].pool = 0; k_pool [i].booked = 0; k_pool [i].cnt = 0; k_pool [i].how = 0; k_pool [i].obj = 0;
}
static int mm_index (uintptr_t mm)
{
	for (int i = 0; i < k_mm_n; i++) if (k_mm [i] == mm) return i;
	if (k_mm_n < KMM_N) { k_mm [k_mm_n] = mm; return k_mm_n++; }
	return KMM_N - 1;
}
static void snap_maybe (void)   /* (under the lock) the high-water snapshot, every 256 KiB of new peak */
{
	if (k_live > k_live_peak) k_live_peak = k_live;
	if (k_live >= k_snap_at + 262144) {
		k_snap_at = k_live;
		__builtin_memcpy (k_own_peak, k_own, (size_t) KO_N * sizeof (kown));
		__builtin_memcpy (k_mp_peak, k_mp, (size_t) KM_N * sizeof (kmp));
	}
}

/* ------------------------------------------------------------------ owner frames */
#define OWNER_IN(t, ra) do { if ((t)->depth++ == 0) (t)->owner = (uintptr_t) (ra); } while (0)
#define OWNER_OUT(t) do { (t)->depth--; } while (0)
static inline uintptr_t owner_of (kts *t, void *ra) { return t->depth ? t->owner : (uintptr_t) ra; }

/* ------------------------------------------------------------------ newlib: the recording points */
struct _reent;
extern void *__real__malloc_r (struct _reent *, size_t);
extern void *__real__calloc_r (struct _reent *, size_t, size_t);
extern void *__real__realloc_r (struct _reent *, void *, size_t);
extern void *__real__memalign_r (struct _reent *, size_t, size_t);
extern void __real__free_r (struct _reent *, void *);
extern size_t _malloc_usable_size_r (struct _reent *, void *);

static void rec_new (struct _reent *r, void *p, uintptr_t owner)
{
	if (!p) return;
	uint32_t sz = (uint32_t) _malloc_usable_size_r (r, p) + 4u;
	lk (); kinit (); if (k_ready) { k_mallocs++; ptr_put ((uintptr_t) p, owner, sz); snap_maybe (); } ulk ();
}
static void rec_del (void *p)
{
	if (!p) return;
	lk (); if (k_ready) { k_frees++; if (!ptr_del ((uintptr_t) p)) k_untracked_free++; } ulk ();
}
void *__wrap__malloc_r (struct _reent *r, size_t n)
{
	kts *t = ts (); uintptr_t o = owner_of (t, __builtin_return_address (0));
	void *p = __real__malloc_r (r, n); if (!t->quiet) rec_new (r, p, o); return p;
}
void *__wrap__calloc_r (struct _reent *r, size_t a, size_t b)
{
	kts *t = ts (); uintptr_t o = owner_of (t, __builtin_return_address (0));
	OWNER_IN (t, (void *) o);   /* (newlib's _calloc_r calls _malloc_r: booked there) */
	void *p = __real__calloc_r (r, a, b);
	OWNER_OUT (t);
	return p;
}
/* memalign / realloc: what they do inside (a bigger chunk taken, its slack given back, a move) is not booked chunk by chunk ("quiet");
 * the result is booked once afterwards */
void *__wrap__memalign_r (struct _reent *r, size_t al, size_t n)
{
	kts *t = ts (); uintptr_t o = owner_of (t, __builtin_return_address (0));
	t->quiet++; void *p = __real__memalign_r (r, al, n); t->quiet--;
	if (p && !t->quiet) rec_new (r, p, o);
	return p;
}
void *__wrap__realloc_r (struct _reent *r, void *old, size_t n)
{
	kts *t = ts (); uintptr_t o = owner_of (t, __builtin_return_address (0));
	t->quiet++; void *p = __real__realloc_r (r, old, n); t->quiet--;
	if (!t->quiet && (p || !n)) {
		lk (); if (k_ready) { if (old) { k_frees++; ptr_del ((uintptr_t) old); } if (p) ptr_del ((uintptr_t) p); } ulk ();
		if (p) rec_new (r, p, o);
	}
	return p;
}
void __wrap__free_r (struct _reent *r, void *p) { kts *t = ts (); if (!t->quiet) rec_del (p); __real__free_r (r, p); }

/* owner frames over the plain C entry points (their callers own what they get; operator new is booked to itself: libsupc++ may not be linked) */
#define OWNER_WRAP1(R, name, T1) extern R __real_##name (T1); \
	R __wrap_##name (T1 a) { kts *t = ts (); OWNER_IN (t, __builtin_return_address (0)); R r = __real_##name (a); OWNER_OUT (t); return r; }
#define OWNER_WRAP2(R, name, T1, T2) extern R __real_##name (T1, T2); \
	R __wrap_##name (T1 a, T2 b) { kts *t = ts (); OWNER_IN (t, __builtin_return_address (0)); R r = __real_##name (a, b); OWNER_OUT (t); return r; }
#define OWNER_WRAP3(R, name, T1, T2, T3) extern R __real_##name (T1, T2, T3); \
	R __wrap_##name (T1 a, T2 b, T3 c) { kts *t = ts (); OWNER_IN (t, __builtin_return_address (0)); R r = __real_##name (a, b, c); OWNER_OUT (t); return r; }
#define OWNER_WRAP4(R, name, T1, T2, T3, T4) extern R __real_##name (T1, T2, T3, T4); \
	R __wrap_##name (T1 a, T2 b, T3 c, T4 d) { kts *t = ts (); OWNER_IN (t, __builtin_return_address (0)); R r = __real_##name (a, b, c, d); OWNER_OUT (t); return r; }
#define OWNER_WRAP0(R, name) extern R __real_##name (void); \
	R __wrap_##name (void) { kts *t = ts (); OWNER_IN (t, __builtin_return_address (0)); R r = __real_##name (); OWNER_OUT (t); return r; }
#define OWNER_WRAPV2(name, T1, T2) extern void __real_##name (T1, T2); \
	void __wrap_##name (T1 a, T2 b) { kts *t = ts (); OWNER_IN (t, __builtin_return_address (0)); __real_##name (a, b); OWNER_OUT (t); }
#define OWNER_WRAPV3(name, T1, T2, T3) extern void __real_##name (T1, T2, T3); \
	void __wrap_##name (T1 a, T2 b, T3 c) { kts *t = ts (); OWNER_IN (t, __builtin_return_address (0)); __real_##name (a, b, c); OWNER_OUT (t); }
#define OWNER_WRAPV4(name, T1, T2, T3, T4) extern void __real_##name (T1, T2, T3, T4); \
	void __wrap_##name (T1 a, T2 b, T3 c, T4 d) { kts *t = ts (); OWNER_IN (t, __builtin_return_address (0)); __real_##name (a, b, c, d); OWNER_OUT (t); }

/* the names here must match vita/vlx/k14_mcensus.sh's --wrap list (one per wrapper) */
typedef void *P; typedef unsigned U; typedef size_t Z; typedef const char *S;
OWNER_WRAP1 (P, malloc, Z)
OWNER_WRAP2 (P, calloc, Z, Z)
OWNER_WRAP2 (P, realloc, P, Z)
OWNER_WRAP2 (P, memalign, Z, Z)
OWNER_WRAP1 (P, strdup, S)
OWNER_WRAP2 (P, strndup, S, Z)
/* eglib */
OWNER_WRAP1 (P, monoeg_malloc, Z)
OWNER_WRAP1 (P, monoeg_malloc0, Z)
OWNER_WRAP2 (P, monoeg_realloc, P, Z)
OWNER_WRAP1 (P, monoeg_try_malloc, Z)
OWNER_WRAP2 (P, monoeg_try_realloc, P, Z)
OWNER_WRAP2 (P, monoeg_g_calloc, Z, Z)
OWNER_WRAP2 (P, monoeg_g_memdup, const void *, U)
OWNER_WRAP2 (P, monoeg_g_slist_prepend, P, P)
OWNER_WRAP2 (P, monoeg_g_slist_append, P, P)
OWNER_WRAP1 (P, monoeg_g_slist_copy, P)
OWNER_WRAP3 (P, monoeg_g_slist_insert_sorted, P, P, P)
OWNER_WRAP3 (P, monoeg_g_slist_insert_before, P, P, P)
OWNER_WRAP2 (P, monoeg_g_list_prepend, P, P)
OWNER_WRAP2 (P, monoeg_g_list_append, P, P)
OWNER_WRAP1 (P, monoeg_g_list_copy, P)
OWNER_WRAP3 (P, monoeg_g_list_insert_sorted, P, P, P)
OWNER_WRAP3 (P, monoeg_g_list_insert_before, P, P, P)
OWNER_WRAP2 (P, monoeg_g_hash_table_new, P, P)
OWNER_WRAP4 (P, monoeg_g_hash_table_new_full, P, P, P, P)
OWNER_WRAPV4 (monoeg_g_hash_table_insert_replace, P, P, P, int)
OWNER_WRAPV2 (monoeg_g_ptr_array_add, P, P)
OWNER_WRAP1 (P, monoeg_g_ptr_array_sized_new, U)
OWNER_WRAPV2 (monoeg_g_ptr_array_set_size, P, int)
OWNER_WRAP0 (P, monoeg_g_ptr_array_new)
OWNER_WRAP3 (P, monoeg_g_array_append_vals, P, const void *, U)
OWNER_WRAP4 (P, monoeg_g_array_insert_vals, P, U, const void *, U)
OWNER_WRAP3 (P, monoeg_g_array_new, int, int, U)
OWNER_WRAP4 (P, monoeg_g_array_sized_new, int, int, U, U)
OWNER_WRAPV2 (monoeg_g_array_set_size, P, int)
OWNER_WRAP3 (P, monoeg_g_byte_array_append, P, const void *, U)
OWNER_WRAPV2 (monoeg_g_byte_array_set_size, P, int)
OWNER_WRAP2 (P, monoeg_g_strndup, S, Z)
OWNER_WRAP2 (P, monoeg_g_strdup_vprintf, S, __builtin_va_list)
OWNER_WRAP3 (P, monoeg_g_strsplit, S, S, int)
OWNER_WRAP1 (P, monoeg_g_string_new, S)
OWNER_WRAP1 (P, monoeg_g_string_sized_new, Z)
OWNER_WRAP2 (P, monoeg_g_string_append, P, S)
OWNER_WRAP3 (P, monoeg_g_string_append_len, P, S, int)
OWNER_WRAP2 (P, monoeg_g_string_append_c, P, int)
OWNER_WRAP0 (P, monoeg_g_queue_new)
/* the runtime's own hash tables */
OWNER_WRAPV3 (mono_internal_hash_table_insert, P, P, P)
OWNER_WRAP3 (P, mono_conc_hashtable_insert, P, P, P)
OWNER_WRAP2 (P, mono_conc_hashtable_new, P, P)
OWNER_WRAP4 (P, mono_conc_hashtable_new_full, P, P, P, P)
OWNER_WRAPV3 (mono_g_hash_table_insert_internal, P, P, P)
OWNER_WRAPV4 (mono_property_hash_insert, P, P, U, P)

/* ------------------------------------------------------------------ the mempool family */
extern void *__real_mono_mem_manager_alloc (P, U);
extern void *__real_mono_mem_manager_alloc0 (P, U);
extern void *__real_mono_mem_manager_alloc0_lock_free (P, U);
extern char *__real_mono_mem_manager_strdup (P, S);
extern void *__real_mono_image_alloc (P, U);
extern void *__real_mono_image_alloc0 (P, U);
extern char *__real_mono_image_strdup (P, S);
extern void *__real_mono_mempool_alloc (P, U);
extern void *__real_mono_mempool_alloc0 (P, U);
extern char *__real_mono_mempool_strdup (P, S);
extern void __real_mono_mempool_destroy (P);
extern uint32_t mono_mempool_get_allocated (P);

static Z kstrlen (S s) { Z n = 0; if (s) while (s [n]) n++; return n; }
static void book (uint32_t kind, uintptr_t obj, uintptr_t pc, U size)
{
	lk (); kinit ();
	if (k_ready) {
		uint32_t mm = kind == 1 ? (uint32_t) mm_index (obj) : 0;
		kmp *e = mp_get (kind, mm, pc); e->al += (size + 7u) & ~7u; e->rq += size; e->cnt++;
	}
	ulk ();
}
/* an outer MM / IMG frame: the nested mempool call maps it to its pool; (kind 1 MM, 2 IMG) */
#define MEM_IN(t, k, o) int _outer = (t)->mdepth++ == 0; if (_outer) { (t)->mkind = (k); (t)->mobj = (uintptr_t) (o); }
#define MEM_OUT(t) (t)->mdepth--
#define MMW(R, name, kind, T2, sz) R __wrap_##name (P o, T2 a) { \
	kts *t = ts (); uintptr_t pc = (uintptr_t) __builtin_return_address (0); MEM_IN (t, kind, o); \
	R r = __real_##name (o, a); MEM_OUT (t); if (_outer) book (kind, (uintptr_t) o, pc, (U) (sz)); return r; }
MMW (void *, mono_mem_manager_alloc, 1, U, a)
MMW (void *, mono_mem_manager_alloc0, 1, U, a)
MMW (void *, mono_mem_manager_alloc0_lock_free, 1, U, a)
MMW (char *, mono_mem_manager_strdup, 1, S, kstrlen (a) + 1)
MMW (void *, mono_image_alloc, 2, U, a)
MMW (void *, mono_image_alloc0, 2, U, a)
MMW (char *, mono_image_strdup, 2, S, kstrlen (a) + 1)
static void mp_booked (kts *t, P pool, uintptr_t pc, U size)
{
	lk (); kinit ();
	if (k_ready) {
		kpool *pl = pool_get ((uintptr_t) pool, 1);
		if (pl) {
			pl->booked += (size + 7u) & ~7u; pl->cnt++;
			if (t->mdepth) { if (!pl->how) { pl->how = (uint32_t) t->mkind; pl->obj = t->mobj; } }
			else if (!pl->how) pl->how = 3;
		}
		if (!t->mdepth) { kmp *e = mp_get (3, 0, pc); e->al += (size + 7u) & ~7u; e->rq += size; e->cnt++; }
	}
	ulk ();
}
void *__wrap_mono_mempool_alloc (P pool, U n) { kts *t = ts (); void *r = __real_mono_mempool_alloc (pool, n); mp_booked (t, pool, (uintptr_t) __builtin_return_address (0), n); return r; }
void *__wrap_mono_mempool_alloc0 (P pool, U n) { kts *t = ts (); void *r = __real_mono_mempool_alloc0 (pool, n); mp_booked (t, pool, (uintptr_t) __builtin_return_address (0), n); return r; }
char *__wrap_mono_mempool_strdup (P pool, S s) { kts *t = ts (); char *r = __real_mono_mempool_strdup (pool, s); mp_booked (t, pool, (uintptr_t) __builtin_return_address (0), (U) kstrlen (s) + 1); return r; }
void __wrap_mono_mempool_destroy (P pool) { lk (); if (k_ready) pool_del ((uintptr_t) pool); ulk (); __real_mono_mempool_destroy (pool); }

/* ------------------------------------------------------------------ the dump (at every census call of the host) */
static char k_buf [65536]; static int k_bn, k_fd;
static void wflush (void) { int o = 0; while (o < k_bn) { long r = ks6 (K_NR_write, k_fd, (long) (k_buf + o), k_bn - o, 0, 0, 0); if (r <= 0) break; o += (int) r; } k_bn = 0; }
static void wc (char c) { if (k_bn >= (int) sizeof k_buf) wflush (); k_buf [k_bn++] = c; }
static void ws (S s) { while (*s) wc (*s++); }
static void wu (uint64_t v) { char t [24]; int n = 0; do { t [n++] = (char) ('0' + v % 10); v /= 10; } while (v); while (n) wc (t [--n]); }
static void wi (int64_t v) { if (v < 0) { wc ('-'); wu ((uint64_t) -v); } else wu ((uint64_t) v); }
static void wx (uintptr_t v) { ws ("0x"); for (int i = 28; i >= 0; i -= 4) wc ("0123456789abcdef" [(v >> i) & 15]); }
static int wopen (S path) { return (int) ks6 (K_NR_open, (long) path, 01 | 0100 | 01000, 0644, 0, 0, 0); }

static void dump_tables (S path, kown *own, kmp *mp, int with_pools)
{
	k_fd = wopen (path); if (k_fd < 0) return; k_bn = 0;
	ws ("T seq "); wi (k_seq); ws (" live "); wi (k_live); ws (" peak "); wi (k_live_peak); ws (" snap_at "); wi (k_snap_at);
	ws (" chunks "); wu (k_ptr_used); ws (" mallocs "); wi (k_mallocs); ws (" frees "); wi (k_frees); ws (" untracked_frees "); wi (k_untracked_free);
	ws (" table_full "); wu (k_ptr_full); ws ("\n");
	for (int i = 0; i < k_mm_n; i++) { ws ("MM "); wi (i); wc (' '); wx (k_mm [i]); ws ("\n"); }
	{ extern volatile unsigned mono_vita_k14_ihw [32] __attribute__ ((weak)); if (mono_vita_k14_ihw) { for (int i = 0; i < 32; i++) if (mono_vita_k14_ihw [i]) { ws ("I "); wu (mono_vita_k14_ihw [i]); ws ("\n"); } } }   /* (a test runtime only) */
	for (unsigned i = 0; i < KO_N; i++) if (own [i].pc && (own [i].live || own [i].cnt)) { ws ("M "); wx (own [i].pc); wc (' '); wi (own [i].live); wc (' '); wi (own [i].cnt); ws ("\n"); }
	if (with_pools) {   /* the live chunks' sizes (the current state only: 8-byte steps up to 512, then one bucket) */
		static uint32_t h [66]; __builtin_memset (h, 0, sizeof h);
		for (unsigned i = 0; i < KP_N; i++) if (k_ptr [i].p) { uint32_t z = k_ptr [i].sz / 8u; h [z > 64 ? 65 : z]++; }
		for (int i = 0; i <= 65; i++) if (h [i]) { ws ("H "); wi (i * 8); wc (' '); wu (h [i]); ws ("\n"); }
	}
	for (unsigned i = 0; i < KM_N; i++) if (mp [i].pc) { ws ("A "); wu (mp [i].kind); wc (' '); wu (mp [i].mm); wc (' '); wx (mp [i].pc); wc (' '); wi (mp [i].al); wc (' '); wi (mp [i].rq); wc (' '); wi (mp [i].cnt); ws ("\n"); }
	if (with_pools)
		for (unsigned i = 0; i < KL_N; i++) if (k_pool [i].pool) {
			ws ("P "); wx (k_pool [i].pool); wc (' '); wu (mono_mempool_get_allocated ((P) k_pool [i].pool)); wc (' '); wi (k_pool [i].booked); wc (' ');
			wi (k_pool [i].cnt); wc (' '); wu (k_pool [i].how); wc (' '); wx (k_pool [i].obj); ws ("\n");
		}
	wflush (); ks6 (K_NR_close, k_fd, 0, 0, 0, 0, 0);
}
extern int __real_mono_vita_census (int64_t *out, int n);
int __wrap_mono_vita_census (int64_t *out, int n)
{
	int r = __real_mono_vita_census (out, n);
	lk ();
	if (k_ready) {
		char path [64] = "/vlxroot/k14mc-"; int pn = 15; char t [12]; int tn = 0; int s = ++k_seq;
		do { t [tn++] = (char) ('0' + s % 10); s /= 10; } while (s);
		while (tn) path [pn++] = t [--tn];
		path [pn++] = '.'; path [pn++] = 't'; path [pn++] = 'x'; path [pn++] = 't'; path [pn] = 0;
		dump_tables (path, k_own, k_mp, 1);
		dump_tables ("/vlxroot/k14mc-peak.txt", k_own_peak, k_mp_peak, 0);
	}
	ulk ();
	return r;
}
