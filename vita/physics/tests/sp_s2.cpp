/* sp_s2.cpp -- physics Stage 2 self-test of libshepphys: patch P1 (box2d/PATCHES.md), contact serials and the Unity queries
 * (shim/sp_query.cpp), through the C API only. Ours. Reported the T1 way (records folded into an FNV-1a 64 hash): the Mac (tests/t1_runner
 * `s2`) and the Vita archive on QEMU's Cortex-A9 (qemu/harness.c) must print the same hash.
 *
 * Records:
 *   P1PR  the plan's four P1 probes (physics-plan-v2 Appendix C.2), each under the four flag settings (stock / trigger phase / kinematic
 *         triggers / both): the step of the first Begin and the step the kinematic body fell asleep. Expected, from the scratch
 *         prototype: motion into a static sensor 15 / 14 / 15 / 14; a new kinematic sensor fixture -1 / -1 / 10 / 10; a kinematic sensor
 *         teleported -1 / -1 / 11 / 10; a kinematic sensor at rest over a static sensor -1 / -1 / 0 / 0 and asleep after step 24
 *   SERL  contact creation serials (SP_WORLD_CONTACT_SERIALS): three sensors overlapping one static sensor -> three Begins with distinct
 *         serials; one shape destroyed -> its End carries its Begin's serial; recreated -> a new, larger serial
 *   RAY   rays: a box face (exact distance, normal), start inside (with and without queriesStartInColliders), from inside a chain loop
 *         (the edge), *All sorted and de-duplicated per collider, a layer mask, triggers on / off, an excluded collider
 *   CAST  circle / box / capsule casts (analytic: the exact shapes, no Box2D skin), a zero-length cast as an overlap test
 *   OVLP  overlap circle with Box2D's skin (H-QSKIN) and point tests without it; an overlap of everything (broadphase order)
 *   BNDS  bounds (no skin), closest points (outside -- with Box2D's skin since E1 -- / inside), the touching contacts of a sensor after a step
 * status: 0, or -200 - n for the first failed expectation n. */
#include <math.h>
#include <stdint.h>
#include <string.h>

#include "shepphys.h"
#include "sp_t1.h"

namespace {

struct S2 {
    sp_t1_record_fn fn; void *user;
    uint64_t h; int32_t records, words, status, steps;
};

void rec(S2 *s, uint32_t tag, int32_t a, int32_t b, const uint32_t *w, int32_t n, uint32_t fmask)
{
    uint32_t hd[4] = {tag, (uint32_t)a, (uint32_t)b, (uint32_t)n};
    const uint8_t *p = (const uint8_t *)hd;
    for (int i = 0; i < 16; ++i) s->h = (s->h ^ p[i]) * 1099511628211ull;
    p = (const uint8_t *)w;
    for (int32_t i = 0; i < n * 4; ++i) s->h = (s->h ^ p[i]) * 1099511628211ull;
    s->records++; s->words += n;
    if (s->fn) s->fn(s->user, tag, a, b, w, n, fmask);
}
void fail(S2 *s, int32_t code) { if (s->status == 0) s->status = -200 - code; }
uint32_t fbits(float f) { uint32_t u; memcpy(&u, &f, 4); return u; }
float fabs_(float f) { return f < 0 ? -f : f; }
void reci(S2 *s, uint32_t tag, int32_t a, int32_t b, const int32_t *v, int32_t n)
{
    rec(s, tag, a, b, (const uint32_t *)v, n, 0);
}
void rechit(S2 *s, uint32_t tag, int32_t id, int32_t n, const sp_uhit *h, int32_t k)
{
    for (int32_t i = 0; i < k && i < n; ++i) {
        uint32_t w[10] = {(uint32_t)h[i].user, fbits(h[i].cx), fbits(h[i].cy), fbits(h[i].px), fbits(h[i].py), fbits(h[i].nx), fbits(h[i].ny),
                          fbits(h[i].distance), fbits(h[i].fraction), (uint32_t)i};
        rec(s, tag, id, n, w, 10, 0x1FEu);
    }
    if (n == 0) { uint32_t w[1] = {0}; rec(s, tag, id, 0, w, 1, 0); }
}

const float kDt = 0.02f;

/* ---------------- P1: the plan's probes (Appendix C.2) through the core API ---------------- */
int32_t probe(S2 *s, int32_t mode, int32_t pflags, int32_t *asleep)
{
    sp_world_def wd = {0.0f, 0.0f, SP_WORLD_ALLOW_SLEEP | SP_WORLD_WARM_START | pflags | SP_WORLD_CONTACT_SERIALS, 0};
    int32_t w = sp_world_create(&wd);
    sp_body_def bd; memset(&bd, 0, sizeof bd);
    bd.type = SP_BODY_STATIC; bd.flags = SP_BODY_ACTIVE | SP_BODY_ALLOW_SLEEP | SP_BODY_AWAKE; bd.x = 1.0f; bd.gravity_scale = 1.0f;
    int32_t sb = sp_body_create(w, &bd);
    sp_shape_def box; memset(&box, 0, sizeof box);
    box.kind = SP_SHAPE_BOX; box.flags = SP_SHAPE_SENSOR; box.density = 1.0f; box.hx = 0.25f; box.hy = 1.0f; box.category_bits = 1; box.mask_bits = 0xFFFF;
    int32_t trig = sp_shape_create(w, sb, &box, nullptr);
    sp_shape_def circ; memset(&circ, 0, sizeof circ);
    circ.kind = SP_SHAPE_CIRCLE; circ.density = 1.0f; circ.radius = 0.25f; circ.category_bits = 1; circ.mask_bits = 0xFFFF;
    int32_t d = 0, k = 0;
    if (mode == 0) {
        bd.type = SP_BODY_DYNAMIC; bd.flags = SP_BODY_ACTIVE | SP_BODY_ALLOW_SLEEP | SP_BODY_AWAKE | SP_BODY_FIXED_ROTATION; bd.x = -1.0f; bd.gravity_scale = 0.0f;
        d = sp_body_create(w, &bd);
        sp_shape_create(w, d, &circ, nullptr);
    } else {
        bd.type = SP_BODY_KINEMATIC; bd.flags = SP_BODY_ACTIVE | SP_BODY_ALLOW_SLEEP | SP_BODY_AWAKE; bd.x = mode == 3 ? 1.0f : -3.0f;
        k = sp_body_create(w, &bd);
        if (mode != 1) { circ.flags = SP_SHAPE_SENSOR; sp_shape_create(w, k, &circ, nullptr); }
    }
    int32_t first = -1; *asleep = -1;
    sp_event2 ev[16];
    for (int32_t st = 0; st < 60; ++st) {
        if (mode == 0) { const float v[3] = {5.0f, 0.0f, 0.0f}; sp_body_set_velocity(w, d, v); }
        if (mode == 1 && st == 10) { const float xa[3] = {1.0f, 0.0f, 0.0f}; sp_body_set_transform(w, k, xa); circ.flags = SP_SHAPE_SENSOR; sp_shape_create(w, k, &circ, nullptr); }
        if (mode == 2 && st == 10) { const float xa[3] = {1.0f, 0.0f, 0.0f}; sp_body_set_transform(w, k, xa); sp_body_set_flag(w, k, SP_BODY_AWAKE, 1); }
        sp_step_def sd = {kDt, 8, 3, 0};
        sp_world_step(w, &sd, nullptr);
        s->steps++;
        int32_t n = sp_events2(w, ev, 16);
        for (int32_t i = 0; i < n; ++i)
            if (ev[i].kind == SP_EV_BEGIN && (ev[i].shape_a == trig || ev[i].shape_b == trig) && first < 0) first = st;
        if (mode == 3) { sp_pose p; sp_body_get_pose(w, k, &p); if (!(p.flags & 1) && *asleep < 0) *asleep = st; }
    }
    sp_world_destroy(w);
    return first;
}

void t_p1(S2 *s)
{
    static const int32_t want[4][4] = {{15, 14, 15, 14}, {-1, -1, 10, 10}, {-1, -1, 11, 10}, {-1, -1, 0, 0}};
    const int32_t fl[4] = {0, SP_WORLD_TRIGGER_PHASE, SP_WORLD_KINEMATIC_TRIGGERS, SP_WORLD_TRIGGER_PHASE | SP_WORLD_KINEMATIC_TRIGGERS};
    for (int32_t m = 0; m < 4; ++m)
        for (int32_t v = 0; v < 4; ++v) {
            int32_t asleep;
            int32_t f = probe(s, m, fl[v], &asleep);
            int32_t r[3] = {f, asleep, want[m][v]};
            reci(s, SP_T1_TAG('P', '1', 'P', 'R'), m, v, r, 3);
            if (f != want[m][v]) fail(s, 1 + m);
            if (m == 3 && asleep != 24) fail(s, 5);
        }
}

/* ---------------- contact serials ---------------- */
void t_serial(S2 *s)
{
    sp_world_def wd = {0.0f, 0.0f, SP_WORLD_ALLOW_SLEEP | SP_WORLD_WARM_START | SP_WORLD_TRIGGER_PHASE | SP_WORLD_KINEMATIC_TRIGGERS | SP_WORLD_CONTACT_SERIALS, 0};
    int32_t w = sp_world_create(&wd);
    sp_body_def bd; memset(&bd, 0, sizeof bd);
    bd.type = SP_BODY_STATIC; bd.flags = SP_BODY_ACTIVE | SP_BODY_ALLOW_SLEEP | SP_BODY_AWAKE; bd.gravity_scale = 1.0f;
    int32_t sb = sp_body_create(w, &bd);
    sp_shape_def box; memset(&box, 0, sizeof box);
    box.kind = SP_SHAPE_BOX; box.flags = SP_SHAPE_SENSOR; box.density = 1.0f; box.hx = 2.0f; box.hy = 2.0f; box.category_bits = 1; box.mask_bits = 0xFFFF;
    int32_t trig = sp_shape_create(w, sb, &box, nullptr);
    sp_shape_def circ; memset(&circ, 0, sizeof circ);
    circ.kind = SP_SHAPE_CIRCLE; circ.density = 1.0f; circ.radius = 0.25f; circ.category_bits = 1; circ.mask_bits = 0xFFFF;
    int32_t kb[3], ks[3];
    for (int32_t i = 0; i < 3; ++i) {
        bd.type = SP_BODY_KINEMATIC; bd.flags = SP_BODY_ACTIVE | SP_BODY_AWAKE; bd.x = -1.0f + (float)i; bd.y = 0.5f * (float)i;
        kb[i] = sp_body_create(w, &bd);
        ks[i] = sp_shape_create(w, kb[i], &circ, nullptr);
    }
    sp_step_def sd = {kDt, 8, 3, 0};
    sp_event2 ev[16];
    int32_t ser[3] = {0, 0, 0};
    sp_world_step(w, &sd, nullptr); s->steps++;
    int32_t n = sp_events2(w, ev, 16);
    for (int32_t i = 0; i < n; ++i) {
        int32_t r[6] = {ev[i].kind, ev[i].serial, ev[i].shape_a == trig ? ev[i].shape_b : ev[i].shape_a, ev[i].step, ev[i].child_a, ev[i].child_b};
        reci(s, SP_T1_TAG('S', 'E', 'R', 'L'), 0, i, r, 6);
        for (int32_t j = 0; j < 3; ++j) if (r[2] == ks[j] && ev[i].kind == SP_EV_BEGIN) ser[j] = ev[i].serial;
    }
    if (n != 3 || ser[0] <= 0 || ser[1] <= 0 || ser[2] <= 0 || ser[0] == ser[1] || ser[1] == ser[2] || ser[0] == ser[2]) fail(s, 10);
    int32_t mx = ser[0] > ser[1] ? ser[0] : ser[1]; if (ser[2] > mx) mx = ser[2];
    /* the middle one's shape destroyed between steps: its End carries its serial */
    sp_shape_destroy(w, ks[1]);
    n = sp_events2(w, ev, 16);
    { int32_t r[3] = {n, n > 0 ? ev[0].kind : 0, n > 0 ? ev[0].serial : 0}; reci(s, SP_T1_TAG('S', 'E', 'R', 'L'), 1, 0, r, 3); }
    if (n != 1 || ev[0].kind != SP_EV_END || ev[0].serial != ser[1]) fail(s, 11);
    ks[1] = sp_shape_create(w, kb[1], &circ, nullptr);
    sp_world_step(w, &sd, nullptr); s->steps++;
    n = sp_events2(w, ev, 16);
    { int32_t r[3] = {n, n > 0 ? ev[0].kind : 0, n > 0 ? ev[0].serial : 0}; reci(s, SP_T1_TAG('S', 'E', 'R', 'L'), 2, 0, r, 3); }
    if (n != 1 || ev[0].kind != SP_EV_BEGIN || !(ev[0].serial > mx)) fail(s, 12);
    sp_world_destroy(w);
}

/* ---------------- queries ---------------- */
sp_ushape_def sdef(int32_t kind, float x, float y, int32_t user, int32_t layer)
{
    sp_ushape_def d;
    memset(&d, 0, sizeof d);
    d.kind = kind;
    d.body2[0] = 1.0f; d.body2[3] = 1.0f;
    d.rel[0] = 1.0f; d.rel[2] = x; d.rel[4] = 1.0f; d.rel[5] = y;
    d.density = 1.0f; d.friction = 0.4f; d.layer = layer; d.user = user;
    return d;
}
sp_uquery qdef(int32_t kind, int32_t flags, float ox, float oy, float dx, float dy, float dist)
{
    sp_uquery q;
    memset(&q, 0, sizeof q);
    q.kind = kind; q.flags = flags; q.layer_mask = 0xFFFFFFFFu; q.ox = ox; q.oy = oy; q.dx = dx; q.dy = dy; q.distance = dist;
    return q;
}
bool near(float a, float b, float tol) { return fabs_(a - b) <= tol; }

void t_query(S2 *s)
{
    sp_uworld_def wd;
    memset(&wd, 0, sizeof wd);
    wd.flags = SP_WORLD_DEFAULT_FLAGS | SP_WORLD_TRIGGER_PHASE | SP_WORLD_KINEMATIC_TRIGGERS | SP_WORLD_CONTACT_SERIALS;
    wd.use_matrix = 1;
    for (int i = 0; i < 32; ++i) wd.matrix[i] = 0xFFFFFFFFu;
    int32_t w = sp_uworld_create(&wd), g = sp_uworld_ground(w);
    int32_t hB, hC, hL, hK, hT;
    sp_ushape_def d = sdef(SP_US_BOX, 5.0f, 0.0f, 101, 0); d.sx = 2.0f; d.sy = 1.0f; sp_ushape_create(w, g, &d, nullptr, &hB, 1);
    d = sdef(SP_US_CIRCLE, 0.0f, 5.0f, 102, 0); d.radius = 0.5f; sp_ushape_create(w, g, &d, nullptr, &hC, 1);
    const float loop[8] = {-12.0f, -2.0f, -8.0f, -2.0f, -8.0f, 2.0f, -12.0f, 2.0f};
    d = sdef(SP_US_LOOP, 0.0f, 0.0f, 103, 0); d.vertex_count = 4; sp_ushape_create(w, g, &d, loop, &hL, 1);
    d = sdef(SP_US_CAPSULE, 0.0f, -5.0f, 104, 0); d.sx = 1.0f; d.sy = 3.0f; d.direction = 0; sp_ushape_create(w, g, &d, nullptr, &hK, 1);
    d = sdef(SP_US_CIRCLE, 10.0f, 10.0f, 105, 3); d.radius = 1.0f; d.flags = SP_USF_SENSOR; sp_ushape_create(w, g, &d, nullptr, &hT, 1);
    sp_uhit h[8];
    int32_t n;
    /* RAY 1: a box face */
    sp_uquery q = qdef(SP_UQ_RAY, SP_UQF_START_IN, 0.0f, 0.0f, 1.0f, 0.0f, 20.0f);
    n = sp_u_query(w, &q, h, 8); rechit(s, SP_T1_TAG('R', 'A', 'Y', ' '), 1, n, h, 8);
    if (n != 1 || h[0].user != 101 || !near(h[0].distance, 4.0f, 1e-5f) || !near(h[0].px, 4.0f, 1e-5f) || h[0].nx != -1.0f || h[0].ny != 0.0f) fail(s, 20);
    /* RAY 2: start inside, with and without queriesStartInColliders */
    q = qdef(SP_UQ_RAY, SP_UQF_START_IN, 5.0f, 0.0f, 1.0f, 0.0f, 20.0f);
    n = sp_u_query(w, &q, h, 8); rechit(s, SP_T1_TAG('R', 'A', 'Y', ' '), 2, n, h, 8);
    if (n != 1 || h[0].user != 101 || h[0].distance != 0.0f || h[0].nx != -1.0f || h[0].px != 5.0f) fail(s, 21);
    q.flags = 0;
    n = sp_u_query(w, &q, h, 8); rechit(s, SP_T1_TAG('R', 'A', 'Y', ' '), 3, n, h, 8);
    if (n != 0) fail(s, 22);
    /* RAY 3: from inside a chain loop: its edge */
    q = qdef(SP_UQ_RAY, SP_UQF_START_IN, -10.0f, 0.0f, 1.0f, 0.0f, 20.0f);
    n = sp_u_query(w, &q, h, 8); rechit(s, SP_T1_TAG('R', 'A', 'Y', ' '), 4, n, h, 8);
    if (n != 1 || h[0].user != 103 || !near(h[0].distance, 2.0f, 1e-5f) || h[0].nx != -1.0f) fail(s, 23);
    /* RAY 4: *All, sorted, one hit per collider (the loop's first edge) */
    q = qdef(SP_UQ_RAY, SP_UQF_START_IN | SP_UQF_ALL | SP_UQF_SORT, -20.0f, 0.0f, 1.0f, 0.0f, 40.0f);
    n = sp_u_query(w, &q, h, 8); rechit(s, SP_T1_TAG('R', 'A', 'Y', ' '), 5, n, h, 8);
    if (n != 2 || h[0].user != 103 || !near(h[0].distance, 8.0f, 1e-5f) || h[1].user != 101 || !near(h[1].distance, 24.0f, 2e-5f)) fail(s, 24);
    /* RAY 5: a sensor on layer 3: triggers on / off, a layer mask without layer 3, an excluded collider */
    q = qdef(SP_UQ_RAY, SP_UQF_START_IN | SP_UQF_TRIGGERS, 10.0f, 5.0f, 0.0f, 1.0f, 10.0f);
    n = sp_u_query(w, &q, h, 8); rechit(s, SP_T1_TAG('R', 'A', 'Y', ' '), 6, n, h, 8);
    if (n != 1 || h[0].user != 105 || !near(h[0].distance, 4.0f, 1e-5f)) fail(s, 25);
    q.flags = SP_UQF_START_IN; n = sp_u_query(w, &q, h, 8); if (n != 0) fail(s, 26);
    q.flags = SP_UQF_START_IN | SP_UQF_TRIGGERS; q.layer_mask = ~(1u << 3); n = sp_u_query(w, &q, h, 8); if (n != 0) fail(s, 27);
    q = qdef(SP_UQ_RAY, SP_UQF_START_IN, 0.0f, 0.0f, 1.0f, 0.0f, 20.0f); q.exclude_user = 101; n = sp_u_query(w, &q, h, 8); if (n != 0) fail(s, 28);
    /* RAY 6: an infinite length is clamped to the world (still the exact face) */
    q = qdef(SP_UQ_RAY, SP_UQF_START_IN, 0.0f, 0.0f, 1.0f, 0.0f, INFINITY);
    n = sp_u_query(w, &q, h, 8); rechit(s, SP_T1_TAG('R', 'A', 'Y', ' '), 7, n, h, 8);
    if (n != 1 || h[0].user != 101 || !near(h[0].distance, 4.0f, 1e-5f)) fail(s, 29);

    /* CAST 1: a circle into the box's left face: since E1 (H-CASTSLOP=toi) the centre stops at Box2D's TOI target, 4 - (0.5 + 0.01 skin - 3 x 0.005) =
     * 3.505; the point is the surfaces' midpoint ((4.005 + 3.99) / 2) */
    q = qdef(SP_UQ_CIRCLE_CAST, SP_UQF_START_IN, 0.0f, 0.0f, 1.0f, 0.0f, 20.0f); q.radius = 0.5f;
    n = sp_u_query(w, &q, h, 8); rechit(s, SP_T1_TAG('C', 'A', 'S', 'T'), 1, n, h, 8);
    if (n != 1 || h[0].user != 101 || !near(h[0].distance, 3.505f, 1e-5f) || !near(h[0].cx, 3.505f, 1e-5f) || !near(h[0].px, 3.9975f, 1e-5f) || !near(h[0].nx, -1.0f, 1e-6f)) fail(s, 30);
    /* CAST 2: a 1 x 1 box (both skins: 4 - 0.5 - (0.02 - 0.015)) */
    q = qdef(SP_UQ_BOX_CAST, SP_UQF_START_IN, 0.0f, 0.0f, 1.0f, 0.0f, 20.0f); q.sx = 1.0f; q.sy = 1.0f;
    n = sp_u_query(w, &q, h, 8); rechit(s, SP_T1_TAG('C', 'A', 'S', 'T'), 2, n, h, 8);
    if (n != 1 || h[0].user != 101 || !near(h[0].distance, 3.495f, 1e-5f) || !near(h[0].nx, -1.0f, 1e-6f)) fail(s, 31);
    /* CAST 3: a circle r 0.25 into the capsule's right side (radius 0.5 around x = 0, no skin): 3 - 0.75 + 0.015 */
    q = qdef(SP_UQ_CIRCLE_CAST, SP_UQF_START_IN, 3.0f, -5.0f, -1.0f, 0.0f, 20.0f); q.radius = 0.25f;
    n = sp_u_query(w, &q, h, 8); rechit(s, SP_T1_TAG('C', 'A', 'S', 'T'), 3, n, h, 8);
    if (n != 1 || h[0].user != 104 || !near(h[0].distance, 3.0f - 0.25f - 0.5f + 0.015f, 1e-4f) || !near(h[0].nx, 1.0f, 1e-4f)) fail(s, 32);
    /* CAST 4: a capsule cast (vertical 0.5 x 2) down onto the circle at (0, 5) from (0, 9): the bottom cap (y - 0.75 - 0.25) meets y = 5.5, less the TOI target's 0.015 */
    q = qdef(SP_UQ_CAPSULE_CAST, SP_UQF_START_IN, 0.0f, 9.0f, 0.0f, -1.0f, 20.0f); q.sx = 0.5f; q.sy = 2.0f; q.direction = 0;
    n = sp_u_query(w, &q, h, 8); rechit(s, SP_T1_TAG('C', 'A', 'S', 'T'), 4, n, h, 8);
    if (n != 1 || h[0].user != 102 || !near(h[0].distance, 9.0f - 1.0f - 5.5f + 0.015f, 1e-4f) || !near(h[0].ny, 1.0f, 1e-4f)) fail(s, 33);
    /* CAST 5: zero length = an overlap test at the origin (FollowerTask_ExistentialDread's CircleCast(pos, r, zero, 0)) */
    q = qdef(SP_UQ_CIRCLE_CAST, SP_UQF_START_IN, 5.0f, 0.7f, 0.0f, 0.0f, 0.0f); q.radius = 0.3f;
    n = sp_u_query(w, &q, h, 8); rechit(s, SP_T1_TAG('C', 'A', 'S', 'T'), 5, n, h, 8);
    if (n != 1 || h[0].user != 101 || h[0].distance != 0.0f) fail(s, 34);
    q.oy = 0.85f; n = sp_u_query(w, &q, h, 8); if (n != 0) fail(s, 35);

    /* OVLP: Box2D's skin in overlaps (H-QSKIN): a circle 0.005 off the box's right face overlaps, 0.015 off does not */
    q = qdef(SP_UQ_CIRCLE, 0, 6.205f, 0.0f, 0.0f, 0.0f, 0.0f); q.radius = 0.2f;
    n = sp_u_query(w, &q, h, 8); { int32_t r[2] = {n, n ? h[0].user : 0}; reci(s, SP_T1_TAG('O', 'V', 'L', 'P'), 1, 0, r, 2); }
    if (n != 1 || h[0].user != 101) fail(s, 40);
    q.ox = 6.215f; n = sp_u_query(w, &q, h, 8); if (n != 0) fail(s, 41);
    /* points: no skin */
    q = qdef(SP_UQ_POINT, 0, 5.995f, 0.0f, 0.0f, 0.0f, 0.0f); n = sp_u_query(w, &q, h, 8); if (n != 1 || h[0].user != 101) fail(s, 42);
    q.ox = 6.005f; n = sp_u_query(w, &q, h, 8); if (n != 0) fail(s, 43);
    q.ox = -10.0f; n = sp_u_query(w, &q, h, 8); if (n != 0) fail(s, 44);   /* inside a chain loop: no collider there */
    q.ox = 0.0f; q.oy = -6.4f; n = sp_u_query(w, &q, h, 8); if (n != 1 || h[0].user != 104) fail(s, 45);   /* the capsule's round end */
    /* everything, in broadphase order; ANY stops at the first */
    q = qdef(SP_UQ_BOX, SP_UQF_ALL | SP_UQF_TRIGGERS, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f); q.sx = 60.0f; q.sy = 60.0f;
    n = sp_u_query(w, &q, h, 8);
    { int32_t r[6] = {n, n > 0 ? h[0].user : 0, n > 1 ? h[1].user : 0, n > 2 ? h[2].user : 0, n > 3 ? h[3].user : 0, n > 4 ? h[4].user : 0}; reci(s, SP_T1_TAG('O', 'V', 'L', 'P'), 2, 0, r, 6); }
    if (n != 5) fail(s, 46);
    q.flags = SP_UQF_ANY | SP_UQF_TRIGGERS; n = sp_u_query(w, &q, h, 1); if (n != 1) fail(s, 47);

    /* BNDS: bounds without skin, closest points */
    float b[4], c[3];
    int32_t nb = sp_u_bounds(w, &hB, 1, b);
    { const uint32_t r[4] = {fbits(b[0]), fbits(b[1]), fbits(b[2]), fbits(b[3])}; rec(s, SP_T1_TAG('B', 'N', 'D', 'S'), 1, nb, r, 4, 15); }
    if (nb != 1 || b[0] != 4.0f || b[1] != -0.5f || b[2] != 6.0f || b[3] != 0.5f) fail(s, 50);
    nb = sp_u_bounds(w, &hK, 1, b);
    { const uint32_t r[4] = {fbits(b[0]), fbits(b[1]), fbits(b[2]), fbits(b[3])}; rec(s, SP_T1_TAG('B', 'N', 'D', 'S'), 2, nb, r, 4, 15); }
    if (nb != 1 || !near(b[1], -6.5f, 1e-5f) || !near(b[3], -3.5f, 1e-5f)) fail(s, 51);
    const float p1[2] = {8.0f, 0.0f}, p2[2] = {5.0f, 0.2f}, p3[2] = {0.0f, 7.0f};
    int32_t k1 = sp_u_closest(w, &hB, 1, p1, c);
    { const uint32_t r[3] = {fbits(c[0]), fbits(c[1]), fbits(c[2])}; rec(s, SP_T1_TAG('B', 'N', 'D', 'S'), 3, k1, r, 3, 7); }
    if (k1 != 1 || !near(c[0], 6.01f, 1e-5f) || !near(c[1], 0.0f, 1e-5f) || !near(c[2], 1.99f, 1e-5f)) fail(s, 52);   /* E1: the skin included (1.01 off a face at 1) */
    k1 = sp_u_closest(w, &hB, 1, p2, c); if (k1 != 1 || c[0] != 5.0f || c[1] != 0.2f) fail(s, 53);
    k1 = sp_u_closest(w, &hC, 1, p3, c);
    { const uint32_t r[3] = {fbits(c[0]), fbits(c[1]), fbits(c[2])}; rec(s, SP_T1_TAG('B', 'N', 'D', 'S'), 4, k1, r, 3, 7); }
    if (k1 != 1 || !near(c[1], 5.5f, 1e-5f) || !near(c[2], 1.5f, 1e-5f)) fail(s, 54);
    if (sp_u_test_point(w, &hB, 1, p2) != 1 || sp_u_test_point(w, &hB, 1, p1) != 0) fail(s, 55);
    /* contacts: a dynamic circle resting inside the sensor, after a step */
    sp_ubody_def bd; memset(&bd, 0, sizeof bd);
    bd.type = SP_BODY_DYNAMIC; bd.flags = SP_UB_SIMULATED | SP_UB_NEVER_SLEEP; bd.x = 10.0f; bd.y = 10.5f; bd.m00 = 1.0f; bd.mass = 1.0f; bd.angular_drag = 0.05f;
    int32_t db = sp_ubody_create(w, &bd), hD;
    d = sdef(SP_US_CIRCLE, 0.0f, 0.0f, 106, 0); d.radius = 0.3f; sp_ushape_create(w, db, &d, nullptr, &hD, 1);
    sp_ustep_def sd = {kDt, 8, 3, 0, 0, 0};
    sp_ustep_out so;
    sp_u_step(w, &sd, &so); s->steps++;
    int32_t oth[4];
    int32_t nc = sp_u_contacts_of(w, &hT, 1, 0, oth, 4);
    int32_t ncs = sp_u_contacts_of(w, &hT, 1, SP_CONTACTS_NO_SENSORS, oth + 2, 2);
    { int32_t r[3] = {nc, nc > 0 ? (oth[0] == hD) : 0, ncs}; reci(s, SP_T1_TAG('B', 'N', 'D', 'S'), 5, 0, r, 3); }
    if (nc != 1 || oth[0] != hD || ncs != 0) fail(s, 56);
    /* the exact distance between two colliders (the swap A/B's classifier), the colliders overlapping a collider (OverlapCollider) */
    float dist = -1.0f;
    int32_t kd = sp_u_distance(w, &hB, 1, &hC, 1, &dist);
    sp_uquery qo = qdef(SP_UQ_POINT, SP_UQF_TRIGGERS, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f);
    sp_uhit ho[4];
    int32_t no = sp_u_overlap_shapes(w, &hT, 1, &qo, ho, 4);
    { const uint32_t r[4] = {(uint32_t)kd, fbits(dist), (uint32_t)no, no > 0 ? (uint32_t)ho[0].user : 0u}; rec(s, SP_T1_TAG('B', 'N', 'D', 'S'), 6, 0, r, 4, 2); }
    if (kd != 1 || !near(dist, sqrtf(16.0f + 20.25f) - 0.5f, 1e-5f) || no != 1 || ho[0].user != 106) fail(s, 58);
    /* the brute oracle (SHEPHERD_CHECK_PHYS=brute): the touching sensor pairs and a query without the broadphase agree */
    int32_t bo[6];
    int32_t bd2 = sp_u_brute_pairs(w, bo);
    sp_uquery qb = qdef(SP_UQ_RAY, SP_UQF_START_IN | SP_UQF_ALL | SP_UQF_SORT, -20.0f, 0.0f, 1.0f, 0.0f, 40.0f);
    sp_uhit h1[8], h2[8];
    int32_t n1 = sp_u_query(w, &qb, h1, 8); qb.flags |= SP_UQF_BRUTE; int32_t n2 = sp_u_query(w, &qb, h2, 8);
    { int32_t r[6] = {bd2, bo[0], bo[1], n1, n2, n1 == n2 && n1 > 1 && h1[0].user == h2[0].user && h1[1].user == h2[1].user && h1[1].distance == h2[1].distance}; reci(s, SP_T1_TAG('B', 'N', 'D', 'S'), 7, 0, r, 6); }
    if (bd2 != 0 || bo[0] != 1 || bo[1] != 1 || n1 != n2 || n1 < 2 || h1[0].user != h2[0].user || h1[1].distance != h2[1].distance) fail(s, 59);
    sp_event2 ev[8];
    int32_t ne = sp_events2(w, ev, 8);
    if (ne != 1 || ev[0].kind != SP_EV_BEGIN || !(ev[0].flags & SP_EVF_SENSOR) || ev[0].serial <= 0) fail(s, 57);
    sp_uworld_destroy(w);
}

} /* namespace */

extern "C" SP_API int32_t sp_s2_run(sp_t1_record_fn fn, void *user, sp_t1_result *out)
{
    S2 s;
    memset(&s, 0, sizeof s);
    s.fn = fn; s.user = user; s.h = 1469598103934665603ull;
    bool was_up = sp_pool_base() != nullptr;   /* the Unity layer's mirrors live in the pool (and B2's arena with it) */
    if (!was_up) { sp_init_def d = {0, 0, 0, 0}; sp_init(&d); }
    int32_t sizes[SP_ABI_N_SIZES]; float probe_f = 0.0f;
    int32_t v = sp_abi(sizes, SP_ABI_N_SIZES, &probe_f);
    { int32_t r[4] = {v, sizes[26], sizes[27], sizes[28]}; reci(&s, SP_T1_TAG('A', 'B', 'I', ' '), 0, 0, r, 4); }
    if (v != SHEPPHYS_ABI_VERSION || sizes[26] != 32 || sizes[27] != 56 || sizes[28] != 40) fail(&s, 0);
    t_p1(&s);
    t_serial(&s);
    t_query(&s);
    if (!was_up) sp_shutdown();
    if (out) {
        out->hash_lo = (uint32_t)s.h; out->hash_hi = (uint32_t)(s.h >> 32);
        out->records = s.records; out->words = s.words; out->steps = s.steps; out->status = s.status;
    }
    return s.status;
}
