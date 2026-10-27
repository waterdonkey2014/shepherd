/* sp_s0.c -- Stage 0 self-test of libshepphys (physics-plan-v2 section 4 "Stage 0: groundwork"): the runtime + pool and the
 * settings check, through the C API only. Ours. Compiled into both libraries next to the T1 scenarios and reported the T1 way
 * (records folded into an FNV-1a 64 hash), so the Mac (tests/t1_runner `s0`) and the Vita archive on QEMU's Cortex-A9
 * (qemu/harness.c) must print the same hash: the pool's offsets and every settings-check float are bit-identical across them.
 *
 * Records:
 *   ABI   the SP_ABI_N_SIZES struct sizes and the ABI version
 *   POOL  a fixed alloc / free sequence on a 96 KiB pool: every returned offset, LIFO reuse, the stats; overlap check (every live
 *         block filled with its own pattern and verified); exhaustion without SP_INIT_POOL_GROW (SP_ERR_FULL, failures counted);
 *         a grow with it (base_gen bumped, contents and offsets kept); the header block (magic, ABI version)
 *   SETS  sp_settings_check on Unity's own project defaults (Physics2DSettings of a new 2022.3 project: no game data): every row's
 *         verdict and the two float bit patterns; the number of DIFFERS rows (expected: exactly 1, SP_SET_MAX_ROTATION)
 * status: 0, or a negative code naming the first failed expectation (-100 - n). */
#include <stdint.h>
#include <string.h>

#include "shepphys.h"
#include "sp_t1.h"

typedef struct S0 {
    sp_t1_record_fn fn; void *user;
    uint64_t h; int32_t records, words, status;
} S0;

static void rec(S0 *s, uint32_t tag, int32_t a, int32_t b, const uint32_t *w, int32_t n, uint32_t fmask)
{
    uint32_t hd[4] = {tag, (uint32_t)a, (uint32_t)b, (uint32_t)n};
    const uint8_t *p = (const uint8_t *)hd;
    for (int i = 0; i < 16; ++i) s->h = (s->h ^ p[i]) * 1099511628211ull;
    p = (const uint8_t *)w;
    for (int32_t i = 0; i < n * 4; ++i) s->h = (s->h ^ p[i]) * 1099511628211ull;
    s->records++; s->words += n;
    if (s->fn) s->fn(s->user, tag, a, b, w, n, fmask);
}
static void fail(S0 *s, int32_t code) { if (s->status == 0) s->status = -100 - code; }

static uint32_t fbits(float f) { uint32_t u; memcpy(&u, &f, 4); return u; }

static void fill(int32_t off, int32_t bytes, uint32_t seed)
{
    uint8_t *b = (uint8_t *)sp_pool_base() + off;
    for (int32_t i = 0; i < bytes; ++i) b[i] = (uint8_t)(seed * 131u + (uint32_t)i * 7u);
}
static int check(int32_t off, int32_t bytes, uint32_t seed)
{
    const uint8_t *b = (const uint8_t *)sp_pool_base() + off;
    for (int32_t i = 0; i < bytes; ++i) if (b[i] != (uint8_t)(seed * 131u + (uint32_t)i * 7u)) return 0;
    return 1;
}

static void pool_test(S0 *s)
{
    enum { N = 48 };
    static const int32_t sizes[N] = {
        16, 24, 100, 256, 257, 4096, 4097, 9000, 1, 0, 33, 640, 1024, 2048, 300, 17,
        48, 64, 128, 512, 768, 1500, 3000, 5000, 15, 31, 47, 63, 200, 400, 800, 1600,
        8, 12, 20, 40, 80, 160, 320, 700, 1300, 2600, 70, 140, 280, 560, 1120, 2240};
    int32_t off[N], live[N];
    sp_init_def d = {96 * 1024, 0, 0, 0};
    if (sp_pool_base() != 0) sp_shutdown();
    if (sp_init(&d) != SP_OK) { fail(s, 1); return; }
    const sp_header *hd = (const sp_header *)((const uint8_t *)sp_pool_base() + 16);
    uint32_t w[24];
    w[0] = hd->magic; w[1] = hd->abi_version; w[2] = hd->base_gen;
    rec(s, SP_T1_TAG('P', 'O', 'O', 'L'), 0, 0, w, 3, 0);
    if (hd->magic != SP_HEADER_MAGIC || hd->abi_version != SHEPPHYS_ABI_VERSION) fail(s, 2);
    for (int32_t i = 0; i < N; ++i) {
        off[i] = sp_pool_alloc(sizes[i]); live[i] = off[i] > 0;
        if (off[i] <= 0 || (off[i] & 15) != 0) fail(s, 3);
        else fill(off[i], sizes[i], (uint32_t)i);
    }
    for (int32_t i = 0; i < N; i += 8) {
        int32_t n = 0;
        for (int32_t k = i; k < i + 8 && k < N; ++k) w[n++] = (uint32_t)off[k];
        rec(s, SP_T1_TAG('P', 'O', 'O', 'L'), 1, i, w, n, 0);
    }
    for (int32_t i = 0; i < N; i += 3) { if (sp_pool_free(off[i]) != SP_OK) fail(s, 4); live[i] = 0; }
    if (sp_pool_free(off[0]) != SP_ERR_PARAM) fail(s, 5);          /* double free refused */
    if (sp_pool_free(16) != SP_ERR_PARAM) fail(s, 6);              /* the header block stays */
    if (sp_pool_free(off[1] + 16) != SP_ERR_PARAM) fail(s, 7);     /* not a block start */
    /* LIFO reuse: the same sizes again come back at the freed offsets, newest first */
    for (int32_t i = 0; i < N; i += 3) {
        int32_t o = sp_pool_alloc(sizes[i]);
        w[0] = (uint32_t)o; rec(s, SP_T1_TAG('P', 'O', 'O', 'L'), 2, i, w, 1, 0);
        off[i] = o; live[i] = o > 0;
        if (o > 0) fill(o, sizes[i], (uint32_t)i + 1000u); else fail(s, 8);
    }
    for (int32_t i = 0; i < N; ++i)
        if (live[i] && !check(off[i], sizes[i], (uint32_t)i + (i % 3 == 0 ? 1000u : 0u))) fail(s, 9);   /* no overlap */
    sp_pool_stats st;
    sp_pool_get_stats(&st);
    w[0] = st.bytes; w[1] = st.used; w[2] = st.high_water; w[3] = st.live_blocks; w[4] = st.allocs; w[5] = st.frees;
    w[6] = st.grows; w[7] = st.failures; w[8] = st.base_gen; w[9] = st.top;
    rec(s, SP_T1_TAG('P', 'O', 'O', 'L'), 3, 0, w, 10, 0);
    /* exhaustion without the grow flag */
    int32_t got = 0, r;
    while ((r = sp_pool_alloc(4096)) > 0) ++got;
    sp_pool_get_stats(&st);
    w[0] = (uint32_t)got; w[1] = (uint32_t)r; w[2] = st.failures; w[3] = st.top;
    rec(s, SP_T1_TAG('P', 'O', 'O', 'L'), 4, 0, w, 4, 0);
    if (r != SP_ERR_FULL || st.failures != 1 || st.grows != 0) fail(s, 10);
    sp_shutdown();
    if (sp_pool_base() != 0) fail(s, 11);
    /* the Mac's grow: offsets and contents survive, base_gen counts the moves */
    sp_init_def g = {4096, 0, SP_INIT_POOL_GROW, 0};
    if (sp_init(&g) != SP_OK) { fail(s, 12); return; }
    int32_t a = sp_pool_alloc(1000); fill(a, 1000, 7);
    int32_t b = sp_pool_alloc(20000); fill(b, 20000, 8);
    int32_t c = sp_pool_alloc(100000); fill(c, 100000, 9);
    sp_pool_get_stats(&st);
    hd = (const sp_header *)((const uint8_t *)sp_pool_base() + 16);
    w[0] = (uint32_t)a; w[1] = (uint32_t)b; w[2] = (uint32_t)c; w[3] = st.bytes; w[4] = st.grows; w[5] = st.base_gen; w[6] = hd->base_gen;
    w[7] = (uint32_t)(check(a, 1000, 7) && check(b, 20000, 8) && check(c, 100000, 9));
    rec(s, SP_T1_TAG('P', 'O', 'O', 'L'), 5, 0, w, 8, 0);
    if (!w[7] || st.grows == 0 || st.base_gen != st.grows || hd->base_gen != st.base_gen || hd->magic != SP_HEADER_MAGIC) fail(s, 13);
    sp_shutdown();
    if (sp_init(0) != SP_OK || sp_pool_get_stats(&st) != SP_OK || st.bytes != 4u << 20) fail(s, 14);   /* the default: 4 MiB */
    sp_shutdown();
    sp_init_def bad = {0, 2, 0, 0};
    if (sp_init(&bad) != SP_ERR_PARAM) fail(s, 15);   /* workers come with Stage 7 */
}

static void settings_test(S0 *s)
{
    /* Unity 2022.3's Physics2DSettings defaults for a new project (Unity's documentation; not game data) and its default fixed step */
    sp_settings u;
    memset(&u, 0, sizeof u);
    u.gravity_x = 0.0f; u.gravity_y = -9.81f; u.velocity_iterations = 8; u.position_iterations = 3;
    u.velocity_threshold = 1.0f; u.max_linear_correction = 0.2f; u.max_angular_correction_deg = 8.0f;
    u.max_translation_speed = 100.0f; u.max_rotation_speed_deg = 360.0f; u.baumgarte_scale = 0.2f; u.baumgarte_toi_scale = 0.75f;
    u.time_to_sleep = 0.5f; u.linear_sleep_tolerance = 0.01f; u.angular_sleep_tolerance_deg = 2.0f; u.default_contact_offset = 0.01f;
    u.fixed_delta_time = 0.02f;
    sp_settings_row rows[SP_SET_COUNT];
    int32_t differs = sp_settings_check(&u, rows, SP_SET_COUNT);
    for (int32_t i = 0; i < SP_SET_COUNT; ++i) {
        uint32_t w[4] = {(uint32_t)rows[i].id, (uint32_t)rows[i].verdict, fbits(rows[i].unity), fbits(rows[i].box2d)};
        rec(s, SP_T1_TAG('S', 'E', 'T', 'S'), i, rows[i].verdict, w, 4, 0xC);
    }
    uint32_t w0 = (uint32_t)differs;
    rec(s, SP_T1_TAG('S', 'E', 'T', 'S'), -1, differs, &w0, 1, 0);
    if (differs != 1 || rows[SP_SET_MAX_ROTATION].verdict != SP_SET_DIFFERS) fail(s, 20);
    for (int32_t i = 0; i < SP_SET_COUNT; ++i) if (i != SP_SET_MAX_ROTATION && rows[i].verdict != SP_SET_EXACT) fail(s, 21);
    if (sp_settings_check(0, rows, SP_SET_COUNT) != SP_ERR_PARAM) fail(s, 22);
}

SP_API int32_t sp_s0_run(sp_t1_record_fn fn, void *user, sp_t1_result *out)
{
    S0 s;
    memset(&s, 0, sizeof s);
    s.fn = fn; s.user = user; s.h = 14695981039346656037ull;
    int32_t sizes[SP_ABI_N_SIZES];
    float probe = 0;
    int32_t v = sp_abi(sizes, SP_ABI_N_SIZES, &probe);
    rec(&s, SP_T1_TAG('A', 'B', 'I', ' '), v, SP_ABI_N_SIZES, (const uint32_t *)sizes, SP_ABI_N_SIZES, 0);
    if (v != SHEPPHYS_ABI_VERSION || probe != SHEPPHYS_ABI_PROBE) fail(&s, 0);
    pool_test(&s);
    settings_test(&s);
    if (out) {
        out->hash_lo = (uint32_t)s.h; out->hash_hi = (uint32_t)(s.h >> 32);
        out->records = s.records; out->words = s.words; out->steps = 0; out->status = s.status;
    }
    return s.status;
}
