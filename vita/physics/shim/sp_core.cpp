/* sp_core.cpp -- Stage 0 runtime of libshepphys (physics-plan-v2 section 4 "Stage 0: groundwork"). Ours, not Box2D.
 *
 *  - sp_init / sp_shutdown: one process-wide runtime holding the POOL: one base pointer, every block a uint32 offset into it
 *    (plan section 1.3: pointers never cross the ABI). Block 0 is the sp_header the managed fast path reads through the base.
 *    The pool is what Stage 1 builds the SoA mirror and rings in and what B2 (b2Alloc / b2Free by link substitution) routes Box2D's
 *    allocations to. Allocator: 16-byte granules, a 16-byte block header, segregated LIFO free lists (exact 16-byte classes up to
 *    256 B, 256-byte classes up to 4 KiB, powers of two above), carved from a bump pointer. Every result is a pure function of the
 *    call sequence (deterministic offsets). No malloc after sp_init except a Mac grow (SP_INIT_POOL_GROW): a new block twice the
 *    size, the contents copied, base_gen bumped; without it an exhausted pool returns SP_ERR_FULL (the binding's loud
 *    "FAIL phys capacity pool" on the Vita).
 *  - sp_settings_check: Physics2DSettings decoded from the install at run time vs Box2D 2.3.1's compiled constants (plan section 1.2:
 *    every CotL value equals a stock constant except maxRotationSpeed, which only spinning bodies reach: H-MAXROT).
 *  - sp_nop: the managed->native transition floor for the engine's physbench mode.
 * Not thread-safe: USER_0 only, like every physics call (plan section 3 "Threads"); kit #9's worker pool (shim/sp_jobs.cpp) runs only inside
 * b2World::Step's forked phases, which never allocate from the pool or the arena on a worker. */
#include "shepphys.h"

#include <Box2D/Common/b2Settings.h>
#include <stdlib.h>
#include <string.h>

static_assert(sizeof(sp_init_def) == 16, "sp_init_def");
static_assert(sizeof(sp_header) == 32, "sp_header");
static_assert(sizeof(sp_pool_stats) == 40, "sp_pool_stats");
static_assert(sizeof(sp_settings) == 68, "sp_settings");
static_assert(sizeof(sp_settings_row) == 16, "sp_settings_row");

namespace {

enum : uint32_t {
    GRANULE = 16, HDR = 16,
    TAG_LIVE = 0x4C495645u,                 /* 'LIVE' */
    TAG_FREE = 0x46524545u,                 /* 'FREE' */
    N_SMALL = 16,                           /* 16..256 by 16 */
    N_MID = 15,                             /* 512..4096 by 256 */
    N_BIG = 18,                             /* 8 KiB .. 1 GiB by powers of two (bounded by the pool size in practice) */
    N_CLASS = N_SMALL + N_MID + N_BIG,
    NIL = 0xFFFFFFFFu,
    DEFAULT_BYTES = 4u << 20
};

/* block header: size = whole block incl. header (a class size + HDR), tag, next (free list, offset), class */
struct Block { uint32_t size, tag, next, cls; };
static_assert(sizeof(Block) == HDR, "block header");

struct Pool {
    unsigned char *base;
    uint32_t bytes, top, used, high, live, allocs, frees, grows, fails, base_gen;
    uint32_t head[N_CLASS];
    bool grow;
};
Pool g_pool;          /* zero-initialised: no static constructor (the Vita link and bare-metal harnesses need none) */
bool g_up;

/* Stage 1, B2 (physics-plan-v2 section 1.2 item 5): Box2D's own allocations (b2Alloc / b2Free, shim/sp_b2glue.cpp) come from this
 * ARENA -- the pool's allocator (16-byte granules, segregated LIFO free lists, a bump pointer: deterministic) on its own fixed block
 * that NEVER moves (Box2D keeps raw pointers into it, so it cannot grow by copying like the mirror pool). Exhausted, or before sp_init,
 * or with SP_INIT_B2_MALLOC: malloc, counted (`fallback`: the Vita's "FAIL phys capacity b2" line, the Mac's high-water log). */
struct Arena {
    unsigned char *base;
    uint32_t bytes, top, used, high, live, allocs, frees, fallback, fallback_live;
    uint32_t head[N_CLASS];
    bool on;
};
Arena g_b2;

/* Kit #8 (sp_host_memory): the host's block -- the pool first, then B2's arena -- when one is registered; nothing is allocated or freed in
 * it by this file except by carving (calloc / free stay for the no-block path). */
struct HostBlock { unsigned char *base; uint32_t bytes, pool, b2; };
HostBlock g_host;

/* payload bytes -> class; class -> payload bytes */
uint32_t class_of(uint32_t n)
{
    if (n <= 256) return n == 0 ? 0 : (n + 15) / 16 - 1;
    if (n <= 4096) return N_SMALL + (n + 255) / 256 - 2;
    uint32_t c = N_SMALL + N_MID, s = 8192;
    while (s < n && c < N_CLASS - 1) { s <<= 1; ++c; }
    return s >= n ? c : NIL;
}
uint32_t class_size(uint32_t c)
{
    if (c < N_SMALL) return (c + 1) * 16;
    if (c < N_SMALL + N_MID) return (c - N_SMALL + 2) * 256;
    return 8192u << (c - N_SMALL - N_MID);
}

inline Block *blk(uint32_t off) { return (Block *)(g_pool.base + off); }

bool grow_to(uint32_t need)
{
    if (!g_pool.grow) return false;
    uint64_t n = g_pool.bytes;
    while (n < need) n *= 2;
    if (n > 0x7FFFFFF0u) return false;
    unsigned char *nb = (unsigned char *)malloc((size_t)n);
    if (!nb) return false;
    memcpy(nb, g_pool.base, g_pool.top);
    memset(nb + g_pool.top, 0, (size_t)(n - g_pool.top));
    free(g_pool.base);
    g_pool.base = nb;
    g_pool.bytes = (uint32_t)n;
    g_pool.grows++;
    g_pool.base_gen++;
    ((sp_header *)(g_pool.base + HDR))->base_gen = g_pool.base_gen;
    return true;
}

/* the header block is the first allocation (payload at offset HDR = 16) */
uint32_t header_off() { return HDR; }

} /* namespace */

/* ---- B2: called by b2Alloc / b2Free (shim/sp_b2glue.cpp) ---- */
void *sp_b2_alloc(int32_t size)
{
    if (!g_b2.on || size < 0) return nullptr;
    uint32_t c = class_of((uint32_t)size);
    if (c == NIL) { g_b2.fallback++; g_b2.fallback_live++; return nullptr; }
    uint32_t bsize = class_size(c) + HDR, off;
    if (g_b2.head[c] != NIL) {
        off = g_b2.head[c];
        g_b2.head[c] = ((Block *)(g_b2.base + off))->next;
    } else {
        if ((uint64_t)g_b2.top + bsize > g_b2.bytes) { g_b2.fallback++; g_b2.fallback_live++; return nullptr; }
        off = g_b2.top;
        g_b2.top += bsize;
    }
    Block *b = (Block *)(g_b2.base + off);
    b->size = bsize; b->tag = TAG_LIVE; b->next = NIL; b->cls = c;
    memset(g_b2.base + off + HDR, 0, bsize - HDR);
    g_b2.used += bsize;
    if (g_b2.used > g_b2.high) g_b2.high = g_b2.used;
    g_b2.live++; g_b2.allocs++;
    return g_b2.base + off + HDR;
}
/* true: the arena owned it (freed); false: not an arena pointer (the caller frees it with free()) */
bool sp_b2_free(void *mem)
{
    unsigned char *p = (unsigned char *)mem;
    if (!g_b2.base || p < g_b2.base + HDR || p >= g_b2.base + g_b2.top) {
        if (mem && g_b2.fallback_live) g_b2.fallback_live--;   /* (approximate: a malloc'd block of Box2D's or not) */
        return false;
    }
    uint32_t off = (uint32_t)(p - g_b2.base) - HDR;
    Block *b = (Block *)(g_b2.base + off);
    if (b->tag != TAG_LIVE) return true;   /* double free: ignored (Box2D never does it) */
    b->tag = TAG_FREE; b->next = g_b2.head[b->cls]; g_b2.head[b->cls] = off;
    g_b2.used -= b->size; g_b2.live--; g_b2.frees++;
    return true;
}

namespace {
bool g_b2_host;   /* kit #8: the arena is a part of the host's block (never freed) */
/* host: NULL = calloc the arena (as before); else the arena is [host, host + host_bytes) of the host's block */
void b2_arena_init(const sp_init_def *def, unsigned char *host, uint32_t host_bytes)
{
    if (g_b2.on) return;
    if (g_b2.base && g_b2.live) return;   /* a leaked arena from an earlier runtime still has Box2D blocks: keep using malloc */
    if (def && (def->flags & SP_INIT_B2_MALLOC)) return;
    uint32_t bytes = def && def->b2_bytes > 0 ? (uint32_t)def->b2_bytes : (uint32_t)DEFAULT_BYTES;
    bytes = (bytes + GRANULE - 1) & ~(GRANULE - 1);
    if (!g_b2_host) free(g_b2.base);
    memset(&g_b2, 0, sizeof g_b2);
    g_b2_host = host != nullptr;
    if (host) { bytes = host_bytes & ~(GRANULE - 1); memset(host, 0, bytes); g_b2.base = host; }
    else g_b2.base = (unsigned char *)calloc(1, bytes);
    if (!g_b2.base || bytes < 2 * GRANULE) { g_b2.base = nullptr; g_b2_host = false; return; }
    g_b2.bytes = bytes;
    for (uint32_t i = 0; i < N_CLASS; ++i) g_b2.head[i] = NIL;
    g_b2.top = GRANULE;   /* offset 0 is never a block (a NULL-like offset) */
    g_b2.on = true;
}
} /* namespace */

extern "C" {

SP_API int32_t sp_b2_get_stats(sp_pool_stats *out)
{
    if (!out) return SP_ERR_PARAM;
    memset(out, 0, sizeof *out);
    out->bytes = g_b2.bytes; out->used = g_b2.used; out->high_water = g_b2.high; out->live_blocks = g_b2.live;
    out->allocs = g_b2.allocs; out->frees = g_b2.frees; out->grows = g_b2.on ? 1u : 0u; out->failures = g_b2.fallback;
    out->base_gen = g_b2.fallback_live; out->top = g_b2.top;
    return SP_OK;
}

SP_API int32_t sp_init(const sp_init_def *def)
{
    if (g_up) return SP_OK;
    if (def && (def->workers < 0 || def->workers > 3)) return SP_ERR_PARAM;   /* Stage 7 item 2 (kit #9): 0..3 workers (patch P3) */
    uint32_t bytes = def && def->pool_bytes > 0 ? (uint32_t)def->pool_bytes : (uint32_t)DEFAULT_BYTES;
    unsigned char *b2host = nullptr; uint32_t b2host_bytes = 0;
    if (g_host.base) {   /* kit #8: the pool and B2's arena from the host's block (sp_host_memory) */
        uint32_t quarter = (g_host.bytes / 4) & ~(GRANULE - 1);
        bytes = def && def->pool_bytes > 0 ? (uint32_t)def->pool_bytes : (quarter < (256u << 10) ? (256u << 10) : quarter);
    }
    bytes = (bytes + GRANULE - 1) & ~(GRANULE - 1);
    if (bytes < 4096) bytes = 4096;
    memset(&g_pool, 0, sizeof g_pool);
    if (g_host.base) {
        if (bytes > g_host.bytes) return SP_ERR_FULL;
        uint32_t rest = g_host.bytes - bytes;
        uint32_t b2 = def && (def->flags & SP_INIT_B2_MALLOC) ? 0u : def && def->b2_bytes > 0 ? (((uint32_t)def->b2_bytes + GRANULE - 1) & ~(GRANULE - 1)) : (rest & ~(GRANULE - 1));
        if (b2 > rest) return SP_ERR_FULL;
        g_pool.base = g_host.base;
        memset(g_pool.base, 0, bytes);
        g_host.pool = bytes; g_host.b2 = b2;
        if (b2) { b2host = g_host.base + bytes; b2host_bytes = b2; }
    } else g_pool.base = (unsigned char *)calloc(1, bytes);
    if (!g_pool.base) return SP_ERR_FULL;
    g_pool.bytes = bytes;
    g_pool.grow = !g_host.base && def && (def->flags & SP_INIT_POOL_GROW) != 0;   /* (a host block never grows: the Vita fails loudly) */
    for (uint32_t i = 0; i < N_CLASS; ++i) g_pool.head[i] = NIL;
    g_up = true;
    int32_t h = sp_pool_alloc((int32_t)sizeof(sp_header));
    if (h != (int32_t)header_off()) { sp_shutdown(); return SP_ERR_FULL; }
    sp_header *hd = (sp_header *)(g_pool.base + h);
    hd->magic = SP_HEADER_MAGIC;
    hd->abi_version = SHEPPHYS_ABI_VERSION;
    hd->base_gen = 0;
    if (!g_host.base || b2host) b2_arena_init(def, b2host, b2host_bytes);   /* Stage 1, B2 (kit #8: in the host's block when one is registered;
                                                                               no room left in it: Box2D's malloc, counted -- never a calloc) */
    if (def && def->workers > 0) sp_jobs_start(def->workers);   /* Stage 7 item 2 (kit #9): the worker pool (a build without threads stays
                                                                   serial: sp_jobs_get(SP_JOBS_WORKERS) = 0) */
    return SP_OK;
}

SP_API int32_t sp_shutdown(void)
{
    if (!g_up) return SP_ERR_PARAM;
    if (g_pool.base != g_host.base) free(g_pool.base);   /* kit #8: a host block is the host's (kept for the next sp_init) */
    memset(&g_pool, 0, sizeof g_pool);
    g_up = false;
    if (g_b2.on) {   /* B2: with Box2D blocks still live (a world not destroyed) the arena is kept (leaked) so they stay valid */
        g_b2.on = false;
        if (!g_b2.live) { if (!g_b2_host) free(g_b2.base); memset(&g_b2, 0, sizeof g_b2); g_b2_host = false; }
    }
    return SP_OK;
}

SP_API int32_t sp_host_memory(void *base, uint32_t bytes)
{
    if (g_up) return SP_ERR_PARAM;
    if (!base || bytes == 0) { memset(&g_host, 0, sizeof g_host); return SP_OK; }
    uintptr_t a = (uintptr_t)base, e = a + bytes;
    a = (a + GRANULE - 1) & ~(uintptr_t)(GRANULE - 1);
    if (e <= a + 4096) return SP_ERR_PARAM;
    g_host.base = (unsigned char *)a; g_host.bytes = (uint32_t)((e - a) & ~(uintptr_t)(GRANULE - 1)); g_host.pool = g_host.b2 = 0;
    return SP_OK;
}

SP_API int32_t sp_host_memory_info(void **base, uint32_t *bytes, uint32_t *pool_bytes, uint32_t *b2_bytes)
{
    if (base) *base = g_host.base;
    if (bytes) *bytes = g_host.bytes;
    if (pool_bytes) *pool_bytes = g_host.pool;
    if (b2_bytes) *b2_bytes = g_host.b2;
    return g_host.base ? SP_OK : SP_ERR_PARAM;
}

SP_API void *sp_pool_base(void) { return g_up ? g_pool.base : nullptr; }

SP_API int32_t sp_pool_alloc(int32_t bytes)
{
    if (!g_up || bytes < 0) return SP_ERR_PARAM;
    uint32_t c = class_of((uint32_t)bytes);
    if (c == NIL) { g_pool.fails++; return SP_ERR_FULL; }
    uint32_t size = class_size(c) + HDR, off;
    if (g_pool.head[c] != NIL) {
        off = g_pool.head[c];
        g_pool.head[c] = blk(off)->next;
    } else {
        if ((uint64_t)g_pool.top + size > g_pool.bytes && !grow_to(g_pool.top + size)) { g_pool.fails++; return SP_ERR_FULL; }
        off = g_pool.top;
        g_pool.top += size;
    }
    Block *b = blk(off);
    b->size = size; b->tag = TAG_LIVE; b->next = NIL; b->cls = c;
    memset(g_pool.base + off + HDR, 0, size - HDR);   /* zeroed like the 32-byte NObj blocks: no stale data leaks into a mirror */
    g_pool.used += size;
    if (g_pool.used > g_pool.high) g_pool.high = g_pool.used;
    g_pool.live++;
    g_pool.allocs++;
    return (int32_t)(off + HDR);
}

SP_API int32_t sp_pool_free(int32_t offset)
{
    if (!g_up || offset < (int32_t)HDR || (uint32_t)offset >= g_pool.top || (offset & (GRANULE - 1)) != 0) return SP_ERR_PARAM;
    uint32_t off = (uint32_t)offset - HDR;
    Block *b = blk(off);
    if (b->tag != TAG_LIVE || b->cls >= N_CLASS || b->size != class_size(b->cls) + HDR) return SP_ERR_PARAM;
    if (off == 0) return SP_ERR_PARAM;   /* the header block lives as long as the runtime */
    b->tag = TAG_FREE;
    b->next = g_pool.head[b->cls];
    g_pool.head[b->cls] = off;
    g_pool.used -= b->size;
    g_pool.live--;
    g_pool.frees++;
    return SP_OK;
}

SP_API int32_t sp_pool_get_stats(sp_pool_stats *out)
{
    if (!out) return SP_ERR_PARAM;
    memset(out, 0, sizeof *out);
    if (!g_up) return SP_ERR_PARAM;
    out->bytes = g_pool.bytes; out->used = g_pool.used; out->high_water = g_pool.high; out->live_blocks = g_pool.live;
    out->allocs = g_pool.allocs; out->frees = g_pool.frees; out->grows = g_pool.grows; out->failures = g_pool.fails;
    out->base_gen = g_pool.base_gen; out->top = g_pool.top;
    return SP_OK;
}

SP_API int32_t sp_nop(void) { return 0; }

/* the Unity value in Box2D's units against the compiled constant. The conversions use the constants' own expression forms
 * (x / 180.0f * b2_pi), so a value equal to Unity's default gives the constant's exact bits (no fused ops: -ffp-contract=off). */
SP_API int32_t sp_settings_check(const sp_settings *s, sp_settings_row *rows, int32_t max)
{
    if (!s) return SP_ERR_PARAM;
    volatile float fdt = s->fixed_delta_time;   /* volatile: computed at run time exactly as written, never folded differently */
    float u[SP_SET_COUNT], b[SP_SET_COUNT];
    u[SP_SET_VELOCITY_THRESHOLD] = s->velocity_threshold;            b[SP_SET_VELOCITY_THRESHOLD] = b2_velocityThreshold;
    u[SP_SET_MAX_LINEAR_CORRECTION] = s->max_linear_correction;      b[SP_SET_MAX_LINEAR_CORRECTION] = b2_maxLinearCorrection;
    u[SP_SET_MAX_ANGULAR_CORRECTION] = s->max_angular_correction_deg / 180.0f * b2_pi;
                                                                     b[SP_SET_MAX_ANGULAR_CORRECTION] = b2_maxAngularCorrection;
    u[SP_SET_BAUMGARTE] = s->baumgarte_scale;                        b[SP_SET_BAUMGARTE] = b2_baumgarte;
    u[SP_SET_TOI_BAUMGARTE] = s->baumgarte_toi_scale;                b[SP_SET_TOI_BAUMGARTE] = b2_toiBaugarte;
    u[SP_SET_TIME_TO_SLEEP] = s->time_to_sleep;                      b[SP_SET_TIME_TO_SLEEP] = b2_timeToSleep;
    u[SP_SET_LINEAR_SLEEP_TOLERANCE] = s->linear_sleep_tolerance;    b[SP_SET_LINEAR_SLEEP_TOLERANCE] = b2_linearSleepTolerance;
    u[SP_SET_ANGULAR_SLEEP_TOLERANCE] = s->angular_sleep_tolerance_deg / 180.0f * b2_pi;
                                                                     b[SP_SET_ANGULAR_SLEEP_TOLERANCE] = b2_angularSleepTolerance;
    u[SP_SET_POLYGON_RADIUS] = s->default_contact_offset;            b[SP_SET_POLYGON_RADIUS] = b2_polygonRadius;
    u[SP_SET_MAX_TRANSLATION] = s->max_translation_speed * fdt;      b[SP_SET_MAX_TRANSLATION] = b2_maxTranslation;
    u[SP_SET_MAX_ROTATION] = s->max_rotation_speed_deg * fdt / 180.0f * b2_pi;
                                                                     b[SP_SET_MAX_ROTATION] = b2_maxRotation;
    int32_t differs = 0;
    for (int32_t i = 0; i < SP_SET_COUNT; ++i) {
        uint32_t ub, bb;
        memcpy(&ub, &u[i], 4); memcpy(&bb, &b[i], 4);
        float d = u[i] - b[i]; if (d < 0) d = -d;
        float m = u[i] < 0 ? -u[i] : u[i], m2 = b[i] < 0 ? -b[i] : b[i]; if (m2 > m) m = m2;
        int32_t v = ub == bb ? SP_SET_EXACT : (d <= 1e-6f * m ? SP_SET_CLOSE : SP_SET_DIFFERS);
        differs += v == SP_SET_DIFFERS;
        if (rows && i < max) { rows[i].id = i; rows[i].verdict = v; rows[i].unity = u[i]; rows[i].box2d = b[i]; }
    }
    return differs;
}

} /* extern "C" */
