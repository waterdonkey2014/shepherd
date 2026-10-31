/* SHEPHERD-VITA device kit #10, track memory (vita/notes/kit10-memory.md): newlib's heap -- vitasdk's own (kit #9) or THE SHARED POOL.
 * Included by host.c right after the memory numbers (MemSnap). Replaces newlib's lib_a-sbrk.o (vitasdk libc.a: _init_vita_heap, _sbrk_r,
 * _free_vita_heap, _get_vita_heap_size -- this file defines all four, so that member is never linked; crt0's _init_vita_newlib (an eboot)
 * and k8_module.c's k8_entry (a launcher's engine module) call _init_vita_heap before anything else).
 *
 * KIT #9 (SHEPHERD_K10_POOL unset or 0, the default; or SHEPHERD_K10_MEM=0): EXACTLY vitasdk's code -- one USER_RW memblock "Newlib heap" of
 * _newlib_heap_size_user (host.c: SHP_NEWLIB_HEAP_MB) allocated before main, a lightweight mutex "sbrk mutex", _sbrk_r bumps a pointer inside
 * it and refuses (ENOMEM) when cur + incr >= end (the same comparison, the same failure paths).
 *
 * THE SHARED POOL (SHEPHERD_K10_POOL=1 in the app's env file; needs vita/patches/k10-memory.patch in the runtime): newlib and the Mono
 * arena grow into ONE block from opposite ends, so whichever side needs memory takes what the other does not use (kit #9: a fixed 116 MiB
 * newlib heap that peaked at 95-101 MiB on the device, a separate 10 MiB IL pool, a fixed arena). Before main only a small "Newlib heap"
 * boot block (SHP_K10_BOOT_KB) is allocated -- the env file read here with plain sceIo calls (no newlib: its heap does not exist yet); at
 * main's partition (host.c) one memblock takes the arena's share + newlib's share + the IL pool's (the same total memory as kit #9), the
 * arena gets all of it with policy 3 (every arena request top-down from the high end, never below the FLOOR), and newlib's next sbrk
 * continues at the block's low end (newlib's mallocr takes a higher, non-adjacent break as a "foreign sbrk": it fences the boot block's last
 * chunk off and frees it into its bins; the bytes between the two -- k10_nl_gap -- are counted as sbrk'd by mallocr and subtracted again in
 * every MEM / P / CP line). newlib takes pages from the arena (mono_vita_vm_arena_pool_take) and keeps the floor SHEPHERD_K10_POOL_RESERVE_MB
 * (default 8) above its top -- its guaranteed room (the memory guard's newlib checks see the floor as newlib's cap); the floor rises only
 * over free pages, and every move re-sets the GC heap's limits (k10_mem.inc.c: max-heap-size = the arena's room above the floor - the card
 * table, interpreter stacks and slack, as kit #9's formula). The ends meet when both are full: then newlib's guard (FAIL MEM) or the GC
 * heap's cap (OutOfMemoryException -> the guard) ends the session cleanly, as at kit #9's fixed sizes. Only where memory comes from changes.
 * A runtime without the patch (weak symbols) or a pool block below the boot block (newlib's break must grow upward): the pool is not used,
 * newlib gets a separate block of its full kit #9 share instead (K10 POOL line says why).
 *
 * REVIEW FIX (vita/notes/kit10-memory.md "Review fixes" R1): vitaGL registers ONE newlib block -- vgl_mem_init maps (sceGxmMapMemory) and
 * records as VGL_MEM_EXTERNAL the memblock holding its malloc(1) at vglInit; vgl_free / vgl_realloc / vgl_malloc_usable_size classify a
 * pointer by that address range (anything outside every range: a no-op free, a NULL realloc). Kit #9: that block is the whole 116 MiB heap.
 * In the pool (or the fallback) newlib spans the boot block AND the pool's low end (or a second block), so the GL eboots link
 * vita/shepherd/k10_vglmem.c (-Wl,--wrap of those three): a pointer newlib owns goes to newlib (shepherd_k10_newlib_owns below), and the
 * pool block / the second block is GPU-mapped after vglInit (vitaGL's GPU allocations fall back to newlib memory when its pools are full).
 * Outside the pool (kit #9) shepherd_k10_newlib_owns answers 0: vitaGL's own code path, unchanged. The pool is OFF by default everywhere
 * since this review (host.c DEFAULTS: no gate models vitaGL -- the VLX stand-in has no pools; a device run decides). */
#ifndef SHP_K10_POOL_DEFAULT
#define SHP_K10_POOL_DEFAULT 0          /* the shared pool's default per app (host.c DEFAULTS may set it; the env file wins) -- 0 everywhere since the review */
#endif
/* (both builds) the memory guard's view of a cap the pool lowered (review fix R3: k10_mem.inc.c sets it, host.c's heap_limit_cb reads it) */
static volatile int k10_cap_drop_pending;   /* the GC heap's cap fell since the last completed major collection */
static unsigned k10_guard_spared;           /* play-guard refusals let through because of such a drop (the K10 MEM end line) */
#if !defined (SHEPHERD_MAC_CHECK)
#include <reent.h>
#include <errno.h>

#ifndef SHP_K10_BOOT_KB
#define SHP_K10_BOOT_KB 2048u          /* the boot block: newlib before main's partition (kit #9's devices: ~0.1 MiB used by then) */
#endif
#define K10_PAGE 4096u
#define K10_FLOOR_STEP (1024u * 1024u)  /* the floor moves in whole MiB (fewer moves, fewer GC-limit updates) */

#ifndef MONO_VITA_ARENA_POOL   /* (vita/patches/k10-memory.patch's mono-vita.h; host.c reads the fork's stock header: kept in step) */
#define MONO_VITA_ARENA_POOL 3
int mono_vita_vm_arena_pool_take (size_t new_top) __attribute__((weak));
int mono_vita_vm_arena_pool_set_floor (size_t floor) __attribute__((weak));
void mono_vita_vm_arena_pool_get (size_t *taken, size_t *floor, size_t *lowest_used) __attribute__((weak));
void *mono_vita_vm_arena_pool_base (void) __attribute__((weak));
void mono_vita_gc_set_heap_limits (size_t max_heap, size_t soft_limit) __attribute__((weak));
void mono_vita_gc_set_size_classes (const int *sizes, int n) __attribute__((weak));
int mono_vita_gc_slot_size (size_t size) __attribute__((weak));
int mono_vita_gc_size_classes (int *out, int max) __attribute__((weak));
#endif

/* vitasdk's state, the same layout of meaning (lib_a-sbrk.o's statics) */
static char k10_sbrk_mutex [32] __attribute__ ((aligned (8)));
static SceUID k10_heap_memblock;
static unsigned k10_heap_size;
static char *k10_heap_base, *k10_heap_end, *k10_heap_cur;
static char *k10_boot_base, *k10_boot_end;   /* review fix R1: the block made before main (the boot block in pool mode; kit #9's whole heap) */
static char *volatile k10_fb_base, *k10_fb_end;   /* review fix R1: the fallback's second "Newlib heap" block (NULL: none) */
/* the shared pool */
static int k10_pool_req;               /* before main: SHEPHERD_K10_POOL was in force (the boot block is small) */
static volatile int k10_pool_live;     /* newlib grows in the pool now (set at main's partition) */
static char *k10_nl_base;              /* the pool's low end: newlib's region (= mono_vita_vm_arena_pool_base ()) */
static size_t k10_nl_top, k10_nl_taken, k10_nl_reserve, k10_pool_bytes, k10_nl_gap, k10_nl_top_peak;
static volatile size_t k10_nl_floor;   /* (only ever rises; read without the mutex by shepherd_k10_newlib_owns) */
static unsigned k10_floor_moves, k10_floor_short, k10_take_fails;
static void k10_pool_limits (void);    /* k10_mem.inc.c: the GC heap's limits after a floor move */

/* before newlib exists: one key of the app's env file (the LAST line "KEY=value" wins, as apply_env_file's setenv; '#' lines skipped) --
 * plain sceIo and byte loops only (no newlib call: neither its heap, nor its reentrancy structure, nor its locks exist yet). Review fix R4:
 * the WHOLE file, read in 4 KiB pieces (before: its first 6,143 bytes only -- a SHEPHERD_K10_POOL=0 further down was not seen); a line
 * longer than the buffer is read from its first 4 KiB (the key and the value's first n - 1 bytes: all the callers compare). */
static void k10_env_line (const char *p, const char *e, const char *key, unsigned kl, char *val, unsigned n, int *found)
{
	unsigned i = 0;
	while (i < kl && p + i < e && p [i] == key [i])
		i++;
	if (p < e && p [0] != '#' && i == kl && p + kl < e && p [kl] == '=') {
		unsigned j = 0;
		for (const char *v = p + kl + 1; v < e && j + 1 < n; v++)
			val [j++] = *v;
		val [j] = 0;
		*found = 1;
	}
}
static int k10_env_premain (const char *key, char *val, unsigned n)
{
	char buf [4096];
	SceUID fd = sceIoOpen (DIR_ "/" SHP_ENV_NAME, SCE_O_RDONLY, 0);
	if (fd < 0)
		return 0;
	unsigned kl = 0;
	while (key [kl])
		kl++;
	int found = 0, skip = 0;   /* skip: the rest of a line longer than the buffer */
	unsigned have = 0;
	for (;;) {
		int r = sceIoRead (fd, buf + have, sizeof (buf) - have);
		int eof = r <= 0;
		if (r > 0)
			have += (unsigned) r;
		const char *p = buf, *end = buf + have;
		for (;;) {   /* every complete line; at the end of the file the last one too */
			const char *e = p;
			while (e < end && *e != '\r' && *e != '\n')
				e++;
			if (e == end && !eof)
				break;
			if (!skip)
				k10_env_line (p, e, key, kl, val, n, &found);
			skip = 0;
			p = e < end ? e + 1 : e;
			if (p >= end)
				break;
		}
		if (eof)
			break;
		unsigned left = (unsigned) (end - p);
		if (left == sizeof (buf)) {   /* one line fills the buffer: its start holds the key and the value's first bytes -- read it, skip the rest */
			if (!skip)
				k10_env_line (buf, buf + sizeof (buf), key, kl, val, n, &found);
			left = 0;
			skip = 1;
		}
		for (unsigned k = 0; k < left; k++)
			buf [k] = p [k];
		have = left;
	}
	sceIoClose (fd);
	return found;
}

static int k10_pool_wanted_premain (void)
{
	char v [16];
	if (k10_env_premain ("SHEPHERD_K10_MEM", v, sizeof (v)) && v [0] == '0' && !v [1])
		return 0;
	if (k10_env_premain ("SHEPHERD_K10_POOL", v, sizeof (v)))
		return v [0] == '1' && !v [1];
	return SHP_K10_POOL_DEFAULT;
}

void _init_vita_heap (void);
void _init_vita_heap (void)
{
	/* = vitasdk's: the mutex first, the block, its base; any failure leaves no heap (every sbrk then refuses) */
	if (sceKernelCreateLwMutex ((SceKernelLwMutexWork *) k10_sbrk_mutex, "sbrk mutex", 0, 0, NULL) < 0) {
		k10_heap_memblock = 0;
		k10_heap_base = NULL;
		k10_heap_cur = NULL;
		return;
	}
	k10_pool_req = k10_pool_wanted_premain ();
	k10_heap_size = k10_pool_req ? SHP_K10_BOOT_KB * 1024u : _newlib_heap_size_user;
	k10_heap_memblock = sceKernelAllocMemBlock ("Newlib heap", SCE_KERNEL_MEMBLOCK_TYPE_USER_RW, k10_heap_size, NULL);
	if (k10_heap_memblock < 0 || sceKernelGetMemBlockBase (k10_heap_memblock, (void **) &k10_heap_base) < 0) {
		k10_heap_base = NULL;
		k10_heap_cur = NULL;
		return;
	}
	k10_heap_end = k10_heap_base + k10_heap_size;
	k10_heap_cur = k10_heap_base;
	k10_boot_base = k10_heap_base;
	k10_boot_end = k10_heap_end;
}

void _free_vita_heap (void);
void _free_vita_heap (void)
{
	sceKernelDeleteLwMutex ((SceKernelLwMutexWork *) k10_sbrk_mutex);
	sceKernelFreeMemBlock (k10_heap_memblock);
	k10_heap_memblock = 0;
	k10_heap_base = NULL;
	k10_heap_cur = NULL;
}

unsigned int _get_vita_heap_size (void);
unsigned int _get_vita_heap_size (void)
{
	return k10_heap_size;
}

/* the pool: newlib's top -> ntop (bytes above k10_nl_base). The floor is kept k10_nl_reserve above the top (whole MiB, only over free
 * pages: when the arena already holds pages there, the floor stops at the lowest of them -- newlib's room shrinks, its guard sees it); pages
 * are taken up to the floor in 64 KiB steps. 0 = refused (newlib: ENOMEM). Under the sbrk mutex. */
static int k10_pool_grow (size_t ntop)
{
	size_t want = (ntop + k10_nl_reserve + K10_FLOOR_STEP - 1) / K10_FLOOR_STEP * K10_FLOOR_STEP;
	if (want > k10_pool_bytes)
		want = k10_pool_bytes;
	if (want > k10_nl_floor) {
		size_t f = want;
		if (mono_vita_vm_arena_pool_set_floor (f) != 0) {
			size_t tk = 0, fl = 0, lu = 0;
			mono_vita_vm_arena_pool_get (&tk, &fl, &lu);
			f = lu & ~(size_t) (K10_PAGE - 1);   /* as high as the free run that holds the floor reaches */
			if (f > want)
				f = want;
			if (f <= k10_nl_floor || mono_vita_vm_arena_pool_set_floor (f) != 0)
				f = k10_nl_floor;
			k10_floor_short++;
		}
		if (f != k10_nl_floor) {
			k10_nl_floor = f;
			k10_floor_moves++;
			k10_pool_limits ();
		}
	}
	if (ntop > k10_nl_floor) {
		k10_take_fails++;
		return 0;
	}
	size_t t = (ntop + 65535u) & ~(size_t) 65535u;
	if (t > k10_nl_floor)
		t = k10_nl_floor;
	if (mono_vita_vm_arena_pool_take (t) != 0) {
		k10_take_fails++;
		return 0;
	}
	k10_nl_taken = t;
	return 1;
}

void *_sbrk_r (struct _reent *reent, ptrdiff_t incr);
void *_sbrk_r (struct _reent *reent, ptrdiff_t incr)
{
	if (sceKernelLockLwMutex ((SceKernelLwMutexWork *) k10_sbrk_mutex, 1, NULL) < 0)
		goto fail;
	if (k10_pool_live) {
		ptrdiff_t ntop = (ptrdiff_t) k10_nl_top + incr;
		if (ntop < 0 || ((size_t) ntop > k10_nl_taken && !k10_pool_grow ((size_t) ntop))) {
			sceKernelUnlockLwMutex ((SceKernelLwMutexWork *) k10_sbrk_mutex, 1);
			goto fail;
		}
		char *prev = k10_nl_base + k10_nl_top;
		k10_nl_top = (size_t) ntop;
		if (k10_nl_top > k10_nl_top_peak)
			k10_nl_top_peak = k10_nl_top;
		sceKernelUnlockLwMutex ((SceKernelLwMutexWork *) k10_sbrk_mutex, 1);
		return prev;
	}
	/* vitasdk's: no heap, or the new break at or past the end -> ENOMEM (the boot block before the pool, or kit #9's whole heap) */
	if (!k10_heap_base || k10_heap_cur + incr >= k10_heap_end) {
		sceKernelUnlockLwMutex ((SceKernelLwMutexWork *) k10_sbrk_mutex, 1);
		goto fail;
	}
	char *prev = k10_heap_cur;
	k10_heap_cur += incr;
	sceKernelUnlockLwMutex ((SceKernelLwMutexWork *) k10_sbrk_mutex, 1);
	return prev;
fail:
	reent->_errno = ENOMEM;
	return (void *) -1;
}

/* main's partition (host.c), pool mode, right after the pool block exists and the arena has it with policy 3: newlib moves there.
 * 0 = done; -1 = not possible (the block lies below the boot block: newlib's break must grow upward) -- the caller falls back. */
static int k10_pool_activate (size_t pool_bytes, size_t reserve)
{
	char *nb = (char *) mono_vita_vm_arena_pool_base ();
	if (!nb || nb < k10_heap_end)
		return -1;
	sceKernelLockLwMutex ((SceKernelLwMutexWork *) k10_sbrk_mutex, 1, NULL);
	k10_nl_base = nb;
	k10_pool_bytes = pool_bytes;
	k10_nl_reserve = reserve;
	k10_nl_top = k10_nl_taken = 0;
	k10_nl_floor = 0;
	k10_nl_gap = (size_t) (nb - k10_heap_cur);   /* mallocr adds it to its sbrk'd count at the first break here (a "foreign sbrk") */
	int ok = k10_pool_grow (0);
	if (ok)
		k10_pool_live = 1;
	sceKernelUnlockLwMutex ((SceKernelLwMutexWork *) k10_sbrk_mutex, 1);
	return ok ? 0 : -1;
}

/* the fallback (the pool block below the boot block, or no patched runtime): a second "Newlib heap" block for the rest of newlib's kit #9
 * share, used once the boot block is full (it must lie above the boot block too; else newlib stays in its boot block: the K10 POOL line
 * says so and the app's FAIL line asks for SHEPHERD_K10_POOL=0). 0 = done. */
static int k10_newlib_fallback (void)
{
	unsigned rest = _newlib_heap_size_user > k10_heap_size ? _newlib_heap_size_user - k10_heap_size : 0;
	SceUID b = rest ? sceKernelAllocMemBlock ("Newlib heap", SCE_KERNEL_MEMBLOCK_TYPE_USER_RW, rest, NULL) : -1;
	char *base = NULL;
	if (b < 0 || sceKernelGetMemBlockBase (b, (void **) &base) < 0 || base < k10_heap_end) {
		if (b >= 0)
			sceKernelFreeMemBlock (b);
		return -1;
	}
	/* newlib continues in it as in the pool, without an arena: a plain bump region whose "floor" is its end */
	sceKernelLockLwMutex ((SceKernelLwMutexWork *) k10_sbrk_mutex, 1, NULL);
	k10_nl_gap = (size_t) (base - k10_heap_cur);
	k10_heap_base = base;
	k10_heap_cur = base;
	k10_heap_end = base + rest;
	k10_heap_size += rest;
	k10_fb_end = base + rest;
	k10_fb_base = base;   /* (review fix R1: after its end -- shepherd_k10_newlib_owns reads the base first) */
	sceKernelUnlockLwMutex ((SceKernelLwMutexWork *) k10_sbrk_mutex, 1);
	return 0;
}

/* review fix R1 (k10_vglmem.c's wrappers of vitaGL's vgl_free / vgl_realloc / vgl_malloc_usable_size): 1 = p is a chunk of newlib's heap
 * that vitaGL's own address check may not know (the pool's or the fallback's newlib side, or the boot block) -- the wrapper hands it to
 * newlib's free / realloc / malloc_usable_size, exactly what vitaGL does for a pointer in its own newlib range. 0 = vitaGL decides (always
 * outside the pool: kit #9's single heap is vitaGL's own range). No lock: the floor only rises and a chunk lies below the top it was cut
 * from; vitaGL's other pools (VRAM / RAM / PHYCONT / CDLG blocks) never overlap newlib's blocks. Any thread. */
int shepherd_k10_newlib_owns (const void *p);
int shepherd_k10_newlib_owns (const void *p)
{
	if (!k10_pool_req)
		return 0;
	const char *c = (const char *) p;
	if (c >= k10_boot_base && c < k10_boot_end)
		return 1;
	if (k10_pool_live)
		return c >= k10_nl_base && c < k10_nl_base + k10_nl_floor;
	const char *fb = k10_fb_base;
	return fb && c >= fb && c < k10_fb_end;
}
/* review fix R1: newlib's blocks besides vitaGL's own (gl_start, after vglInit: k10_vglmem.c GPU-maps them) -- the pool's low end and the
 * fallback's second block (NULL each when absent) */
static __attribute__ ((unused)) void k10_newlib_extra_blocks (const void **pool, const void **fb)
{
	*pool = k10_pool_live ? k10_nl_base : NULL;
	*fb = k10_fb_base;
}

/* the MEM / P / CP lines: newlib's sbrk'd size and peak without the gap mallocr counted; its cap = what it may grow to now */
static inline unsigned k10_nl_fix_k (unsigned long long sbrked)
{
	return (unsigned) ((sbrked > k10_nl_gap ? sbrked - k10_nl_gap : sbrked) / 1024);
}
static inline unsigned k10_newlib_cap_k (void)
{
	if (k10_pool_live)
		return (unsigned) ((SHP_K10_BOOT_KB * 1024ull + k10_nl_floor) / 1024);
	return k10_heap_size / 1024;
}
#else   /* the Mac check: the system's malloc */
static inline unsigned k10_nl_fix_k (unsigned long long sbrked) { return (unsigned) (sbrked / 1024); }
static inline unsigned k10_newlib_cap_k (void) { return SHP_NEWLIB_HEAP_MB * 1024; }
static int k10_pool_req, k10_pool_live;
static int k10_newlib_fallback (void) { return -1; }
#endif
