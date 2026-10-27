/* sp_s1.cpp -- physics Stage 1 self-test of libshepphys's Unity layer (shim/sp_unity.cpp), through the C API only. Ours.
 * Reported the T1 way (records folded into an FNV-1a 64 hash): the Mac (tests/t1_runner `s1`) and the Vita archive on QEMU's
 * Cortex-A9 (qemu/harness.c) must print the same hash -- the Unity layer's floats (MovePosition velocities, the shape affines,
 * the polygon decomposition, slot C's lerp, the render poses) are bit-identical across them (physics-plan-v2 section 1.10).
 * The analytic checks below are this file's own expectations (status); the engine's `physcheck` repeats them through the real
 * Unity API (Stage 1 acceptance #1).
 *
 * Records:
 *   ABI   sizes + version
 *   MOVE  a Lamb-shaped body (circle r 0.3, mass 1, gravity scale 0, rotation frozen, never sleeps, Interpolate) MovePosition'd in free
 *         space for 30 steps: every pose (must arrive within rounding of the target: |p - t| <= 2e-6)
 *   SLDE  the 45 degree push into a static chain edge, friction 0.4 / 0 / 0.01 (Lamb / frictionless / enemy material against the
 *         island's 0.4): the steady slide / free speed ratio (expected cos45 - mu sin45: 0.4243 / 0.7071 / 0.6624, within 1 %)
 *   GHST  a circle sliding along a 50-vertex collinear chain: the largest per-step speed deviation (<= 1e-4: ghost vertices)
 *   DOOR  a circle pushed into a 4 x 0.58 static box: the resting gap (radius + Box2D's skin, within the linear slop)
 *   INTP  slot C (alpha 0.25 / 0.75 and a 0-step tick), slot A, a teleport's history reset
 *   RNDR  the render poses of an Interpolate and a None body at 60 Hz render times (j_rel, alpha) and a clamped request
 *   DCMP  polygon decomposition: an L (6 points) and a 12-point star -> piece counts and the total mass (= area at density 1); a box
 *         under a negative-determinant affine (C16) -> its area
 *   FILT  the Unity filter: two overlapping dynamic circles on layers the matrix separates (no touching contact), then allowed
 *         (touching), then ignored (Physics2D.IgnoreCollision: none)
 *   MASS  Unity's mass (mass 3, auto mass off) vs auto mass (density x area)
 *   WELD  Stage 4a: SP_USF_CHAIN_WELD, Unity 2022.3's chain vertex filter (the E1 oracle's Paths4a2): an open chain drops a point within
 *         sqrt(2.01) x b2_linearSlop of the last one kept (0.0070887 dropped, 0.00708875 kept), a loop within b2_linearSlop (0.0049907 dropped,
 *         0.00500069 kept), measured as world distances (a 2x scale: 0.0036 local = 0.0072 world kept); the last-kept rule (0.006, 0.012: the
 *         second stays); without the flag every point stays. Each case: a ray down beside the first point -- the hit y is 0 on the first
 *         segment when its second point stayed, else on the slanted segment to the next kept point
 *   UPOL  Stage 4b: SP_USF_UNITY_POLY, Unity 2022.3's polygon build of one decomposed piece (the E1 oracle's Paths4b* scenes): the unit
 *         square plus a fifth hull point (1 + d, 0.3 d) -- 0.002 out (welded with the square's corner: Unity's (0.5 b2_linearSlop)^2), 0.01
 *         out (kept: Box2D 2.3.1's own Set would weld it at 0.05, patch P5 keeps it), the flag off (stock: native decomposition and
 *         Box2D's weld, 0.01 welded); the bottom bump (0.5, -e) -- e 0.001 (a 0.004 rad turn: removed as collinear), 0.0012 (0.0048 rad:
 *         kept); a piece that welds below 3 points (no fixture). Each case: the fixtures made, a point test just outside the square
 *         beside the extra point (inside when it stayed)
 *   B2    the B2 arena's stats after the tests (blocks used while the worlds lived, all freed after)
 * status: 0, or -100 - n for the first failed expectation n. */
#include <math.h>
#include <stdint.h>
#include <string.h>

#include "shepphys.h"
#include "sp_t1.h"

namespace {

struct S1 {
    sp_t1_record_fn fn; void *user;
    uint64_t h; int32_t records, words, status, steps;
};

void rec(S1 *s, uint32_t tag, int32_t a, int32_t b, const uint32_t *w, int32_t n, uint32_t fmask)
{
    uint32_t hd[4] = {tag, (uint32_t)a, (uint32_t)b, (uint32_t)n};
    const uint8_t *p = (const uint8_t *)hd;
    for (int i = 0; i < 16; ++i) s->h = (s->h ^ p[i]) * 1099511628211ull;
    p = (const uint8_t *)w;
    for (int32_t i = 0; i < n * 4; ++i) s->h = (s->h ^ p[i]) * 1099511628211ull;
    s->records++; s->words += n;
    if (s->fn) s->fn(s->user, tag, a, b, w, n, fmask);
}
void fail(S1 *s, int32_t code) { if (s->status == 0) s->status = -100 - code; }
uint32_t fbits(float f) { uint32_t u; memcpy(&u, &f, 4); return u; }
float fabs_(float f) { return f < 0 ? -f : f; }
void recf(S1 *s, uint32_t tag, int32_t a, int32_t b, const float *f, int32_t n)
{
    uint32_t w[SP_T1_MAX_WORDS];
    for (int32_t i = 0; i < n && i < SP_T1_MAX_WORDS; ++i) w[i] = fbits(f[i]);
    rec(s, tag, a, b, w, n, (1u << n) - 1u);
}

const float kDt = 0.02f;

int32_t world(bool all_layers)
{
    sp_uworld_def d;
    memset(&d, 0, sizeof d);
    d.gravity_x = 0.0f; d.gravity_y = -9.81f; d.flags = SP_WORLD_DEFAULT_FLAGS; d.use_matrix = 1;
    for (int i = 0; i < 32; ++i) d.matrix[i] = all_layers ? 0xFFFFFFFFu : 0u;
    return sp_uworld_create(&d);
}
sp_ubody_def bdef(int32_t type, float x, float y)
{
    sp_ubody_def b;
    memset(&b, 0, sizeof b);
    b.type = type; b.flags = SP_UB_SIMULATED; b.x = x; b.y = y; b.m00 = 1.0f; b.m10 = 0.0f;
    b.mass = 1.0f; b.linear_drag = 0.0f; b.angular_drag = 0.05f; b.gravity_scale = 1.0f;
    return b;
}
sp_ubody_def lamb(float x, float y)
{
    sp_ubody_def b = bdef(SP_BODY_DYNAMIC, x, y);
    b.flags |= SP_UB_NEVER_SLEEP | SP_UB_FREEZE_ROTATION; b.gravity_scale = 0.0f; b.interpolation = 1;
    return b;
}
sp_ushape_def sdef(int32_t kind)
{
    sp_ushape_def d;
    memset(&d, 0, sizeof d);
    d.kind = kind;
    d.body2[0] = 1.0f; d.body2[3] = 1.0f;
    d.rel[0] = 1.0f; d.rel[4] = 1.0f;
    d.density = 1.0f; d.friction = 0.4f;
    return d;
}
int32_t circle(int32_t w, int32_t b, float r, float fric, int32_t layer)
{
    sp_ushape_def d = sdef(SP_US_CIRCLE);
    d.radius = r; d.friction = fric; d.layer = layer;
    int32_t h = 0;
    sp_ushape_create(w, b, &d, nullptr, &h, 1);
    return h;
}
int32_t mk(int32_t w, sp_ubody_def d) { return sp_ubody_create(w, &d); }
sp_mirror *mir(int32_t w, int32_t b) { return (sp_mirror *)((unsigned char *)sp_pool_base() + sp_ubody_mirror(w, b)); }
void move_to(int32_t w, int32_t b, float x, float y) { sp_mirror *m = mir(w, b); m->tx = x; m->ty = y; m->flags |= SP_MF_MOVE_POS; }
int32_t step(S1 *s, int32_t w, int32_t k, sp_ustep_out *o = nullptr)
{
    sp_ustep_def d = {kDt, 8, 3, k, 0, 0};
    sp_ustep_out t;
    s->steps++;
    return sp_u_step(w, &d, o ? o : &t);
}

void t_move(S1 *s)
{
    int32_t w = world(true);
    int32_t b = mk(w, lamb(1.25f, -3.5f));
    circle(w, b, 0.3f, 0.4f, 14);
    float worst = 0.0f;
    for (int32_t k = 0; k < 30; ++k) {
        sp_mirror *m = mir(w, b);
        float tx = m->x + 2.0f * kDt, ty = m->y + 1.0f * kDt;   /* UnitObject: rb.position + v * dt */
        move_to(w, b, tx, ty);
        step(s, w, k);
        m = mir(w, b);
        float e = fabs_(m->x - tx); if (fabs_(m->y - ty) > e) e = fabs_(m->y - ty);
        if (e > worst) worst = e;
        const float f[5] = {m->x, m->y, m->angle, m->vx, m->vy};
        recf(s, SP_T1_TAG('M', 'O', 'V', 'E'), k, m->flags & (SP_MF_WRITE | SP_MF_ANGLE), f, 5);
        m->flags &= ~(SP_MF_WRITE | SP_MF_ANGLE);
    }
    if (!(worst <= 2e-6f)) fail(s, 1);
    recf(s, SP_T1_TAG('M', 'O', 'V', 'E'), -1, 0, &worst, 1);
    sp_uworld_destroy(w);
}

float slide_ratio(S1 *s, float fric_body, float fric_edge, int32_t id)
{
    int32_t w = world(true);
    sp_ushape_def e = sdef(SP_US_EDGE);
    const float pts[4] = {-60.0f, 0.0f, 60.0f, 0.0f};
    e.vertex_count = 2; e.friction = fric_edge; e.layer = 23;
    int32_t h;
    sp_ushape_create(w, sp_uworld_ground(w), &e, pts, &h, 1);
    int32_t b = mk(w, lamb(-20.0f, 0.31f));
    circle(w, b, 0.3f, fric_body, 14);
    const float speed = 5.0f, c = 0.70710678f;
    float x0 = 0.0f, dxs = 0.0f;
    for (int32_t k = 0; k < 200; ++k) {
        sp_mirror *m = mir(w, b);
        if (k == 100) x0 = m->x;
        move_to(w, b, m->x + speed * c * kDt, m->y - speed * c * kDt);
        step(s, w, k);
    }
    dxs = mir(w, b)->x - x0;
    float ratio = dxs / (100.0f * speed * kDt);
    const float f[3] = {ratio, mir(w, b)->x, mir(w, b)->y};
    recf(s, SP_T1_TAG('S', 'L', 'D', 'E'), id, 0, f, 3);
    sp_uworld_destroy(w);
    return ratio;
}

void t_slide(S1 *s)
{
    const float c = 0.70710678f;
    float r1 = slide_ratio(s, 0.4f, 0.4f, 0), r0 = slide_ratio(s, 0.0f, 0.0f, 1), re = slide_ratio(s, 0.01f, 0.4f, 2);
    float mu_e = sqrtf(0.01f * 0.4f);
    float e1 = c - 0.4f * c, e0 = c, ee = c - mu_e * c;
    if (fabs_(r1 - e1) > 0.01f * e1) fail(s, 2);
    if (fabs_(r0 - e0) > 0.01f * e0) fail(s, 3);
    if (fabs_(re - ee) > 0.01f * ee) fail(s, 4);
}

void t_ghost(S1 *s)
{
    int32_t w = world(true);
    sp_ushape_def e = sdef(SP_US_EDGE);
    float pts[100];
    float px = -12.5f;   /* (no int -> float conversion in a loop: the Vita compiler could vectorise it into NEON) */
    for (int32_t i = 0; i < 50; ++i) { pts[2 * i] = px; pts[2 * i + 1] = 0.0f; px += 0.5f; }
    e.vertex_count = 50; e.friction = 0.0f; e.layer = 23;
    int32_t h;
    sp_ushape_create(w, sp_uworld_ground(w), &e, pts, &h, 1);
    int32_t b = mk(w, lamb(-11.0f, 0.31f));
    circle(w, b, 0.3f, 0.0f, 14);
    float prev = mir(w, b)->x, dmin = 1e9f, dmax = -1e9f;
    for (int32_t k = 0; k < 90; ++k) {
        sp_mirror *m = mir(w, b);
        move_to(w, b, m->x + 3.0f * kDt, m->y - 3.0f * kDt);
        step(s, w, k);
        float x = mir(w, b)->x, dx = x - prev;
        prev = x;
        if (k >= 20) { if (dx < dmin) dmin = dx; if (dx > dmax) dmax = dx; }
    }
    float dev = (dmax - dmin) / kDt;   /* speed spread over the slide, u/s */
    const float f[3] = {dmin, dmax, dev};
    recf(s, SP_T1_TAG('G', 'H', 'S', 'T'), 0, 0, f, 3);
    if (!(dev <= 1e-4f)) fail(s, 5);
    sp_uworld_destroy(w);
}

void t_door(S1 *s)
{
    int32_t w = world(true);
    sp_ushape_def bx = sdef(SP_US_BOX);
    bx.sx = 4.0f; bx.sy = 0.5754f; bx.layer = 21;
    int32_t h;
    sp_ushape_create(w, sp_uworld_ground(w), &bx, nullptr, &h, 1);
    int32_t b = mk(w, lamb(0.3f, 2.0f));
    circle(w, b, 0.3f, 0.4f, 14);
    for (int32_t k = 0; k < 120; ++k) { sp_mirror *m = mir(w, b); move_to(w, b, m->x, m->y - 3.0f * kDt); step(s, w, k); }
    float gap = mir(w, b)->y - 0.5f * 0.5754f;   /* centre above the box top */
    const float f[2] = {gap, mir(w, b)->x};
    recf(s, SP_T1_TAG('D', 'O', 'O', 'R'), 0, 0, f, 2);
    /* resting: radius + skin (b2_polygonRadius 0.01) minus at most the linear slop of penetration */
    if (!(gap >= 0.3f + 0.01f - 0.005f - 1e-4f && gap <= 0.3f + 0.01f + 1e-4f)) fail(s, 6);
    sp_uworld_destroy(w);
}

void t_interp(S1 *s)
{
    int32_t w = world(true);
    int32_t b = mk(w, lamb(0.0f, 0.0f));
    circle(w, b, 0.3f, 0.4f, 14);
    for (int32_t k = 0; k < 3; ++k) { sp_mirror *m = mir(w, b); move_to(w, b, m->x + 0.1f, m->y); step(s, w, k); }
    const float a1[2] = {0.25f, kDt}, a2[2] = {0.75f, kDt};
    sp_u_interp(w, a1, 0);
    sp_mirror *m = mir(w, b);
    float i1 = m->ix, e1 = m->hx[1] + (m->hx[2] - m->hx[1]) * 0.25f;
    sp_u_interp(w, a2, 0);   /* a 0-step tick: the same history, alpha grew */
    float i2 = mir(w, b)->ix;
    sp_u_early(w, 0);
    float wa = mir(w, b)->wx, xtrue = mir(w, b)->x;
    m = mir(w, b);
    m->x = 7.5f; m->y = -2.0f; m->flags |= SP_MF_TELEPORT;   /* rb.position = (7.5, -2) */
    sp_u_flush(w, 0);
    sp_u_interp(w, a1, 0);
    m = mir(w, b);
    const float f[7] = {i1, e1, i2, wa, m->ix, m->iy, m->hx[0]};
    recf(s, SP_T1_TAG('I', 'N', 'T', 'P'), (m->flags & SP_MF_FORCE_WRITE) ? 1 : 0, 0, f, 7);
    if (fbits(i1) != fbits(e1) || !(i2 > i1) || fbits(wa) != fbits(xtrue) || m->ix != 7.5f || m->iy != -2.0f || !(m->flags & SP_MF_FORCE_WRITE))
        fail(s, 7);
    step(s, w, 3);   /* forced write-back after a body-side teleport (C6) though the body rests */
    m = mir(w, b);
    if (!(m->flags & SP_MF_WRITE) || m->x != 7.5f) fail(s, 8);
    sp_uworld_destroy(w);
}

void t_render(S1 *s)
{
    int32_t w = world(true);
    sp_ubody_def li = lamb(0.0f, 0.0f), ln = lamb(0.0f, 5.0f);
    ln.interpolation = 0;
    int32_t bi = sp_ubody_create(w, &li), bn = sp_ubody_create(w, &ln);
    circle(w, bi, 0.3f, 0.4f, 14); circle(w, bn, 0.3f, 0.4f, 14);
    for (int32_t k = 0; k < 4; ++k) {
        sp_mirror *m = mir(w, bi); move_to(w, bi, m->x + 0.05f, m->y);
        m = mir(w, bn); move_to(w, bn, m->x + 0.05f, m->y);
        step(s, w, k);
    }
    sp_u_snapshot(w);
    const sp_render_def rq[4] = {{0, 0.5f, kDt, 0}, {-1, 0.25f, kDt, 0}, {0, 0.0f, kDt, 0}, {1, 0.5f, kDt, 0}};
    for (int32_t i = 0; i < 4; ++i) {
        sp_rpose o[4];
        int32_t n = sp_render_poses(w, &rq[i], o, 4);
        const uint32_t wv[6] = {fbits(o[0].x), fbits(o[0].y), fbits(o[1].x), fbits(o[1].y), (uint32_t)n & 0xFFFFu, ((uint32_t)n >> 30) & 1u};
        rec(s, SP_T1_TAG('R', 'N', 'D', 'R'), i, 0, wv, 6, 0xFu);   /* (ints as ints: no int -> float conversion the compiler could vectorise) */
        if (i == 0) {
            sp_mirror *m = mir(w, bi);
            float want = m->hx[1] + (m->hx[2] - m->hx[1]) * 0.5f;
            if (fbits(o[0].x) != fbits(want) || fbits(o[1].x) != fbits(mir(w, bn)->hx[2])) fail(s, 9);
        }
        if (i == 3 && !((n >> 30) & 1)) fail(s, 10);
    }
    sp_uworld_destroy(w);
}

float area_mass(int32_t w, int32_t b) { float md[4]; sp_body_get_mass(w, b, md); return md[0]; }

void t_decomp(S1 *s)
{
    int32_t w = world(true);
    sp_ubody_def d = bdef(SP_BODY_DYNAMIC, 0.0f, 0.0f);
    d.flags |= SP_UB_AUTO_MASS;
    int32_t bl = sp_ubody_create(w, &d);
    sp_ushape_def p = sdef(SP_US_POLYGON);
    const float L[12] = {0, 0, 3, 0, 3, 1, 1, 1, 1, 3, 0, 3};   /* an L: area 5 */
    p.vertex_count = 6;
    int32_t hs[16];
    int32_t nl = sp_ushape_create(w, bl, &p, L, hs, 16);
    sp_ubody_mass(w, bl);
    float al = area_mass(w, bl);
    int32_t bs = sp_ubody_create(w, &d);
    float star[24];
    float fi = 0.0f;
    for (int32_t i = 0; i < 12; ++i, fi += 1.0f) {
        float r = (i & 1) ? 1.0f : 2.5f, a = fi * (6.2831853f / 12.0f);
        star[2 * i] = r * sp_cosf(a); star[2 * i + 1] = r * sp_sinf(a);
    }
    p.vertex_count = 12;
    int32_t ns = sp_ushape_create(w, bs, &p, star, hs, 16);
    sp_ubody_mass(w, bs);
    float as = area_mass(w, bs);
    int32_t bm = sp_ubody_create(w, &d);   /* a 2 x 1 box mirrored in y (a negative-determinant affine, C16) */
    sp_ushape_def bx = sdef(SP_US_BOX);
    bx.sx = 2.0f; bx.sy = 1.0f; bx.rel[4] = -1.0f;
    int32_t nm = sp_ushape_create(w, bm, &bx, nullptr, hs, 16);
    sp_ubody_mass(w, bm);
    float am = area_mass(w, bm);
    const uint32_t wv[6] = {(uint32_t)nl, fbits(al), (uint32_t)ns, fbits(as), (uint32_t)nm, fbits(am)};
    rec(s, SP_T1_TAG('D', 'C', 'M', 'P'), 0, 0, wv, 6, 0x2Au);
    if (nl < 2 || fabs_(al - 5.0f) > 1e-4f || ns < 2 || nm != 1 || fabs_(am - 2.0f) > 1e-5f) fail(s, 11);
    sp_uworld_destroy(w);
}

void t_filter(S1 *s)
{
    sp_uworld_def wd;
    memset(&wd, 0, sizeof wd);
    wd.gravity_y = -9.81f; wd.flags = SP_WORLD_DEFAULT_FLAGS; wd.use_matrix = 1;
    for (int i = 0; i < 32; ++i) wd.matrix[i] = 0xFFFFFFFFu;
    wd.matrix[9] = 0xFFFFFFFFu & ~(1u << 9);   /* Units (9) x Units off (symmetric: only the diagonal bit) */
    int32_t w = sp_uworld_create(&wd);
    sp_ubody_def a = lamb(0.0f, 0.0f), b = lamb(0.4f, 0.0f);
    int32_t ba = sp_ubody_create(w, &a), bb = sp_ubody_create(w, &b);
    int32_t ha = circle(w, ba, 0.3f, 0.4f, 9), hb = circle(w, bb, 0.3f, 0.4f, 9);
    sp_ustep_out o;
    step(s, w, 0, &o);
    int32_t t0 = o.touching;
    sp_shape_unity u = {10, 0, 0, 0};   /* move b to layer 10: allowed (the pair is found at the end of the next step, touching after) */
    sp_shape_set_unity(w, hb, &u);
    step(s, w, 1, &o);
    step(s, w, 2, &o);
    int32_t t1 = o.touching;
    sp_world_ignore_pair(w, ha, hb, 1);   /* flagged: the next step's collide drops it */
    step(s, w, 3, &o);
    int32_t t2 = o.touching;
    const uint32_t wv[3] = {(uint32_t)t0, (uint32_t)t1, (uint32_t)t2};
    rec(s, SP_T1_TAG('F', 'I', 'L', 'T'), 0, 0, wv, 3, 0);
    if (t0 != 0 || t1 != 1 || t2 != 0) fail(s, 12);
    sp_uworld_destroy(w);
}

void t_mass(S1 *s)
{
    int32_t w = world(true);
    sp_ubody_def d = bdef(SP_BODY_DYNAMIC, 0.0f, 0.0f);
    d.mass = 3.0f;
    int32_t b = sp_ubody_create(w, &d);
    circle(w, b, 0.5f, 0.4f, 0);
    sp_ubody_mass(w, b);
    float m1[4]; sp_body_get_mass(w, b, m1);
    d.flags |= SP_UB_AUTO_MASS;
    sp_ubody_set(w, b, &d);
    float m2[4]; sp_body_get_mass(w, b, m2);
    const float f[8] = {m1[0], m1[1], m1[2], m1[3], m2[0], m2[1], m2[2], m2[3]};
    recf(s, SP_T1_TAG('M', 'A', 'S', 'S'), 0, 0, f, 8);
    if (m1[0] != 3.0f || fabs_(m2[0] - 0.785398f) > 1e-5f || fabs_(m1[3] / m2[3] - 3.0f / m2[0]) > 1e-3f) fail(s, 13);
    sp_uworld_destroy(w);
}

/* Stage 4a: one ground chain (edge or loop) with the weld flag or not, then a ray straight down at x = probe: the hit y */
float weld_hit(int32_t kind, int32_t flags, const float *pts, int32_t n, float scale, float probe)
{
    int32_t w = world(true);
    sp_ushape_def e = sdef(kind);
    e.flags = flags; e.vertex_count = n; e.rel[0] = scale; e.rel[4] = scale;
    int32_t h = 0;
    int32_t made = sp_ushape_create(w, sp_uworld_ground(w), &e, pts, &h, 1);
    float y = made == 1 ? -1.0f : -2.0f;
    sp_ray_query q = {probe, 0.5f, probe, -0.5f, 0xFFFFu, 0};
    sp_ray_hit hit[4];
    if (made == 1 && sp_raycast(w, &q, hit, 4) > 0) y = hit[0].py;
    sp_uworld_destroy(w);
    return y;
}

void t_weld(S1 *s)
{
    /* open chains: (0, 0), (d, 0), (d + 1, 1); the ray at x = d / 2 hits y 0 when (d, 0) stayed, ~d / 2 when it was dropped */
    const float ds[2] = {0.0070887f, 0.00708875f};
    float r[12];
    for (int32_t i = 0; i < 2; ++i) {
        const float d = ds[i], pts[6] = {0.0f, 0.0f, d, 0.0f, d + 1.0f, 1.0f};
        r[i] = weld_hit(SP_US_EDGE, SP_USF_CHAIN_WELD, pts, 3, 1.0f, 0.5f * d);
        r[2 + i] = weld_hit(SP_US_EDGE, 0, pts, 3, 1.0f, 0.5f * d);
    }
    /* loops: (0, 0), (d, 0), (1, 1), (0, 1) counter-clockwise; the ray from inside at x = d / 2 */
    const float dl[2] = {0.0049907f, 0.00500069f};
    for (int32_t i = 0; i < 2; ++i) {
        const float d = dl[i], pts[8] = {0.0f, 0.0f, d, 0.0f, 1.0f, 1.0f, 0.0f, 1.0f};
        r[4 + i] = weld_hit(SP_US_LOOP, SP_USF_CHAIN_WELD, pts, 4, 1.0f, 0.5f * d);
    }
    /* world distances: 0.0036 local under a 2x scale = 0.0072 world (kept); 0.0035 = 0.0070 (dropped) */
    {
        const float a[6] = {0.0f, 0.0f, 0.0036f, 0.0f, 1.0036f, 1.0f}, b[6] = {0.0f, 0.0f, 0.0035f, 0.0f, 1.0035f, 1.0f};
        r[6] = weld_hit(SP_US_EDGE, SP_USF_CHAIN_WELD, a, 3, 2.0f, 0.0036f);
        r[7] = weld_hit(SP_US_EDGE, SP_USF_CHAIN_WELD, b, 3, 2.0f, 0.0035f);
    }
    /* the last-kept rule: (0, 0), (0.006, 0) dropped, (0.012, 0) kept (0.012 from the last kept, though 0.006 from the dropped one), (1.012, 1) */
    {
        const float c[8] = {0.0f, 0.0f, 0.006f, 0.0f, 0.012f, 0.0f, 1.012f, 1.0f};
        r[8] = weld_hit(SP_US_EDGE, SP_USF_CHAIN_WELD, c, 4, 1.0f, 0.009f);
    }
    /* too few left: an edge of two points 0.004 apart, a loop collapsing below 3; and without the flag a 0.006 step stays (a step Box2D's own
     * CreateChain assert accepts: > b2_linearSlop -- the san build runs Box2D's asserts) */
    {
        const float e2[4] = {0.0f, 0.0f, 0.004f, 0.0f}, l3[6] = {0.0f, 0.0f, 0.003f, 0.0f, 0.0f, 0.003f}, e6[6] = {0.0f, 0.0f, 0.006f, 0.0f, 1.006f, 1.0f};
        r[9] = weld_hit(SP_US_EDGE, SP_USF_CHAIN_WELD, e2, 2, 1.0f, 0.002f);
        r[10] = weld_hit(SP_US_LOOP, SP_USF_CHAIN_WELD, l3, 3, 1.0f, 0.001f);
        r[11] = weld_hit(SP_US_EDGE, 0, e6, 3, 1.0f, 0.003f);
    }
    recf(s, SP_T1_TAG('W', 'E', 'L', 'D'), 0, 0, r, 12);
    bool ok = r[0] > 0.003f && r[0] < 0.004f && r[1] == 0.0f && r[2] == 0.0f && r[3] == 0.0f &&
              r[4] > 0.0024f && r[4] < 0.0026f && r[5] == 0.0f && r[6] == 0.0f && r[7] > 0.003f && r[7] < 0.004f &&
              r[8] == 0.0f && r[9] == -2.0f && r[10] == -2.0f && r[11] == 0.0f;
    if (!ok) fail(s, 15);
}

/* Stage 4b: one ground polygon piece with SP_USF_UNITY_POLY (or not); returns fixtures * 10 + (the probe point inside ? 1 : 0) */
int32_t upol(int32_t flags, const float *pts, int32_t n, float px, float py)
{
    int32_t w = world(true);
    sp_ushape_def e = sdef(SP_US_POLYGON);
    e.flags = flags; e.vertex_count = n;
    int32_t h[8] = {0};
    int32_t made = sp_ushape_create(w, sp_uworld_ground(w), &e, pts, h, 8);
    const float p[2] = {px, py};
    int32_t in = made > 0 && sp_u_test_point(w, h, made < 8 ? made : 8, p) > 0 ? 1 : 0;
    sp_uworld_destroy(w);
    return made * 10 + in;
}

void t_upol(S1 *s)
{
    int32_t r[7];
    /* the fifth point (1 + d, 0.3 d): probed at (1 + 0.6 d, 0.3 d), outside the unit square, inside the hull when the point stayed */
    const float d2 = 0.002f, d10 = 0.01f;
    const float a[10] = {0.0f, 0.0f, 1.0f, 0.0f, 1.0f + d2, 0.3f * d2, 1.0f, 1.0f, 0.0f, 1.0f};
    const float b[10] = {0.0f, 0.0f, 1.0f, 0.0f, 1.0f + d10, 0.3f * d10, 1.0f, 1.0f, 0.0f, 1.0f};
    r[0] = upol(SP_USF_UNITY_POLY, a, 5, 1.0f + 0.6f * d2, 0.3f * d2);
    r[1] = upol(SP_USF_UNITY_POLY, b, 5, 1.0f + 0.6f * d10, 0.3f * d10);
    r[2] = upol(0, b, 5, 1.0f + 0.6f * d10, 0.3f * d10);
    /* the bottom bump (0.5, -e), probed at (0.5, -0.5 e) */
    const float e1 = 0.001f, e2 = 0.0012f;
    const float c[10] = {0.0f, 0.0f, 0.5f, -e1, 1.0f, 0.0f, 1.0f, 1.0f, 0.0f, 1.0f};
    const float dd[10] = {0.0f, 0.0f, 0.5f, -e2, 1.0f, 0.0f, 1.0f, 1.0f, 0.0f, 1.0f};
    r[3] = upol(SP_USF_UNITY_POLY, c, 5, 0.5f, -0.5f * e1);
    r[4] = upol(SP_USF_UNITY_POLY, dd, 5, 0.5f, -0.5f * e2);
    /* a piece welding below 3 points: (0, 0), (0.002, 0), (0.001, 0.0015) */
    const float t[6] = {0.0f, 0.0f, 0.002f, 0.0f, 0.001f, 0.0015f};
    r[5] = upol(SP_USF_UNITY_POLY, t, 3, 0.001f, 0.0005f);
    /* the hull of an unordered point set: a square given as (1, 1), (0, 0), (1, 0), (0, 1) (a convex piece in any order: Set's hull) */
    const float u[8] = {1.0f, 1.0f, 0.0f, 0.0f, 1.0f, 0.0f, 0.0f, 1.0f};
    r[6] = upol(SP_USF_UNITY_POLY, u, 4, 0.5f, 0.5f);
    rec(s, SP_T1_TAG('U', 'P', 'O', 'L'), 0, 0, (const uint32_t *)r, 7, 0);
    bool ok = r[0] == 10 && r[1] == 11 && r[2] == 10 && r[3] == 10 && r[4] == 11 && r[5] == 0 && r[6] == 11;
    if (!ok) fail(s, 16);
}

} /* namespace */

extern "C" SP_API int32_t sp_s1_run(sp_t1_record_fn fn, void *user, sp_t1_result *out)
{
    S1 s;
    memset(&s, 0, sizeof s);
    s.fn = fn; s.user = user; s.h = 14695981039346656037ull;
    bool was_up = sp_pool_base() != nullptr;
    if (!was_up) { sp_init_def d = {0, 0, 0, 0}; sp_init(&d); }
    int32_t sizes[SP_ABI_N_SIZES];
    float probe = 0;
    int32_t v = sp_abi(sizes, SP_ABI_N_SIZES, &probe);
    rec(&s, SP_T1_TAG('A', 'B', 'I', ' '), v, SP_ABI_N_SIZES, (const uint32_t *)sizes, SP_ABI_N_SIZES, 0);
    if (v != SHEPPHYS_ABI_VERSION) fail(&s, 0);
    t_move(&s);
    t_slide(&s);
    t_ghost(&s);
    t_door(&s);
    t_interp(&s);
    t_render(&s);
    t_decomp(&s);
    t_filter(&s);
    t_mass(&s);
    t_weld(&s);
    t_upol(&s);
    sp_pool_stats st;
    sp_b2_get_stats(&st);
    const uint32_t bw[2] = {st.live_blocks, st.allocs == st.frees ? 1u : 0u};   /* (the same whether the arena is on or not) */
    rec(&s, SP_T1_TAG('B', '2', ' ', ' '), 0, 0, bw, 2, 0);
    if (st.grows && (st.live_blocks != 0 || st.allocs != st.frees)) fail(&s, 14);   /* the arena is on: every block given back */
    if (!was_up) sp_shutdown();
    if (out) {
        out->hash_lo = (uint32_t)s.h; out->hash_hi = (uint32_t)(s.h >> 32);
        out->records = s.records; out->words = s.words; out->steps = s.steps; out->status = s.status;
    }
    return s.status;
}
