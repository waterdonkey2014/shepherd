/* SHEPHERD-VITA device kit #10, track memory -- review fixes R1 / R4 / R5 (vita/notes/kit10-memory.md "Review fixes"): a Mac unit test of the
 * host's newlib side, vita/shepherd/k10_newlib.inc.c, included as it is (run-tests-k10.sh: its device section enabled, <reent.h> left out) --
 * shepherd_k10_newlib_owns (what k10_vglmem.c's wrappers of vitaGL's frees ask) over the boot block, the pool's newlib side up to its floor,
 * the arena's side, foreign memory, the fallback's second block; kit #9 (no pool: always 0, vitaGL decides); the fallback's failure; the
 * pre-main env read past 6 KiB. Mocks: memblocks carved in increasing addresses (as on the device), sceIo on ./env.txt (333-byte reads), a
 * fake arena-pool API (a floor + the arena's lowest page). Exit 0 = all passed. */
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <stddef.h>
#include <errno.h>
typedef int SceUID;
typedef struct { int w [8]; } SceKernelLwMutexWork;
#define SCE_O_RDONLY 1
#define SCE_KERNEL_MEMBLOCK_TYPE_USER_RW 0
#define DIR_ "."
#define SHP_ENV_NAME "env.txt"
struct _reent { int _errno; };
static int sceKernelCreateLwMutex (SceKernelLwMutexWork *w, const char *n, int a, int b, void *c) { (void) w; (void) n; (void) a; (void) b; (void) c; return 0; }
static int sceKernelLockLwMutex (SceKernelLwMutexWork *w, int n, void *t) { (void) w; (void) n; (void) t; return 0; }
static int sceKernelUnlockLwMutex (SceKernelLwMutexWork *w, int n) { (void) w; (void) n; return 0; }
static int sceKernelDeleteLwMutex (SceKernelLwMutexWork *w) { (void) w; return 0; }
/* memblocks: carved from one big arena in request order (increasing addresses, as on the device), or a forced address */
static char *g_space; static size_t g_used; static char *g_blk [16]; static size_t g_blksz [16]; static int g_nblk; static int g_fail_next;
static SceUID sceKernelAllocMemBlock (const char *n, int t, unsigned sz, void *o) { (void) n; (void) t; (void) o;
	if (g_fail_next) { g_fail_next = 0; return -1; }
	g_used = (g_used + 0xfffff) & ~(size_t) 0xfffff; g_blk [g_nblk] = g_space + g_used; g_blksz [g_nblk] = sz; g_used += sz; return 0x100 + g_nblk++; }
static int sceKernelGetMemBlockBase (SceUID b, void **base) { *base = g_blk [b - 0x100]; return 0; }
static int sceKernelFreeMemBlock (SceUID b) { (void) b; return 0; }
static FILE *g_f;
static SceUID sceIoOpen (const char *p, int f, int m) { (void) p; (void) f; (void) m; g_f = fopen ("env.txt", "rb"); return g_f ? 3 : -1; }
static int sceIoRead (SceUID fd, void *b, unsigned n) { (void) fd; return (int) fread (b, 1, n < 333 ? n : 333, g_f); }
static int sceIoClose (SceUID fd) { (void) fd; fclose (g_f); return 0; }
unsigned int _newlib_heap_size_user = 116u << 20;
/* the fake arena pool (policy 3): base, size, floor, the arena's lowest used page */
static char *g_pb; static size_t g_psz, g_floor, g_lowest, g_taken;
#define MONO_VITA_ARENA_POOL 3
static int mono_vita_vm_arena_pool_take (size_t t) { if (t > g_floor) return -1; g_taken = t; return 0; }
static int mono_vita_vm_arena_pool_set_floor (size_t f) { if (f > g_lowest) return -1; g_floor = f; return 0; }
static void mono_vita_vm_arena_pool_get (size_t *t, size_t *f, size_t *l) { *t = g_taken; *f = g_floor; *l = g_lowest; }
static void *mono_vita_vm_arena_pool_base (void) { return g_pb; }
static int g_limits_calls; static void k10_pool_limits (void) { g_limits_calls++; }
#include "k10_newlib.inc.c"
static int bad;
#define CHECK(c, msg) do { if (!(c)) { printf ("FAIL %s\n", msg); bad++; } else printf ("ok   %s\n", msg); } while (0)
int main (void)
{
	g_space = malloc (1u << 30);
	/* 1: kit #9 (no env file key): one 116 MiB block; owns() always 0 (vitaGL's own path) */
	remove ("env.txt");
	_init_vita_heap ();
	CHECK (!k10_pool_req && k10_heap_size == 116u << 20, "kit #9: no pool, the whole 116 MiB block before main");
	char *p = _sbrk_r (&(struct _reent) { 0 }, 4096);
	CHECK (p == k10_boot_base && !shepherd_k10_newlib_owns (p) && !shepherd_k10_newlib_owns (p + (100u << 20)), "kit #9: owns() = 0 everywhere (vitaGL decides)");
	/* 2: the pool asked for in a big env file, past 6 KiB */
	FILE *f = fopen ("env.txt", "w");
	for (int i = 0; i < 200; i++) fprintf (f, "# padding line %d ..........................................\n", i);
	fprintf (f, "SHEPHERD_K10_POOL=1\n"); fclose (f);
	g_used = 0; g_nblk = 0;
	_init_vita_heap ();
	CHECK (k10_pool_req && k10_heap_size == 2048u * 1024, "pool past 6 KiB of env file: seen before main (boot block 2 MiB)");
	struct _reent re = { 0 };
	char *a = _sbrk_r (&re, 64 * 1024);
	CHECK (shepherd_k10_newlib_owns (a) && shepherd_k10_newlib_owns (k10_boot_end - 1) && !shepherd_k10_newlib_owns (k10_boot_end), "boot block: owned, its end not");
	/* the pool block above it */
	SceUID pbk = sceKernelAllocMemBlock ("pool", 0, 284u << 20, NULL); sceKernelGetMemBlockBase (pbk, (void **) &g_pb);
	g_psz = 284u << 20; g_lowest = g_psz - (100u << 20); g_floor = 0;
	CHECK (k10_pool_activate (g_psz, 8u << 20) == 0 && k10_pool_live, "pool activated");
	CHECK (k10_nl_floor == (8u << 20), "floor = 8 MiB reserve");
	char *q = _sbrk_r (&re, 3u << 20);   /* the first break in the pool: at its low end */
	CHECK (q == g_pb, "newlib's next break at the pool's low end");
	CHECK (shepherd_k10_newlib_owns (q) && shepherd_k10_newlib_owns (q + (3u << 20)), "pool newlib side (below the floor): owned");
	CHECK (!shepherd_k10_newlib_owns (g_pb + k10_nl_floor) && !shepherd_k10_newlib_owns (g_pb + g_lowest), "the arena's side (at / above the floor): not owned");
	char *r = _sbrk_r (&re, 20u << 20);
	CHECK (r == q + (3u << 20) && k10_nl_floor >= (31u << 20) && shepherd_k10_newlib_owns (r + (19u << 20)), "growth: the floor rises, the new chunks owned");
	char foreign [16];
	CHECK (!shepherd_k10_newlib_owns (foreign) && !shepherd_k10_newlib_owns (NULL), "foreign memory / NULL: not owned (vitaGL decides)");
	const void *pb, *fb; k10_newlib_extra_blocks (&pb, &fb);
	CHECK (pb == g_pb && fb == NULL, "extra blocks: the pool block, no fallback");
	/* 3: the fallback (a second block above the boot block) */
	g_used = 0; g_nblk = 0; k10_pool_live = 0; k10_nl_floor = 0; k10_nl_gap = 0; k10_fb_base = NULL;
	_init_vita_heap ();
	_sbrk_r (&re, 64 * 1024);
	CHECK (k10_newlib_fallback () == 0 && k10_fb_base, "fallback: a second block");
	char *s = _sbrk_r (&re, 1u << 20);
	CHECK (s == k10_fb_base && shepherd_k10_newlib_owns (s) && shepherd_k10_newlib_owns (k10_boot_base) && !shepherd_k10_newlib_owns (k10_fb_end), "fallback: both blocks owned, nothing past them");
	k10_newlib_extra_blocks (&pb, &fb);
	CHECK (pb == NULL && fb == k10_fb_base, "extra blocks: the fallback's block");
	/* 4: the fallback failing (no second block) */
	g_used = 0; g_nblk = 0; k10_fb_base = NULL;
	_init_vita_heap ();
	g_fail_next = 1;
	CHECK (k10_newlib_fallback () == -1 && !k10_fb_base, "fallback failure reported (-1): host.c stops with FAIL (R5)");
	/* 5: SHEPHERD_K10_MEM=0 later in the file wins over an early POOL=1 */
	f = fopen ("env.txt", "w"); fprintf (f, "SHEPHERD_K10_POOL=1\n"); for (int i = 0; i < 300; i++) fprintf (f, "X%d=%080d\n", i, i); fprintf (f, "SHEPHERD_K10_MEM=0\n"); fclose (f);
	g_used = 0; g_nblk = 0;
	_init_vita_heap ();
	CHECK (!k10_pool_req && k10_heap_size == 116u << 20, "SHEPHERD_K10_MEM=0 past 24 KiB: kit #9's whole heap");
	printf ("%s\n", bad ? "SOME FAILED" : "ALL PASSED");
	return bad != 0;
}
