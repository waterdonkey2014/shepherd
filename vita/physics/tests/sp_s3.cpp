/* sp_s3.cpp -- physics Stage 3 self-test of libshepphys (combat: physics-plan-v2 section 4 "Stage 3"), through the C API only. Ours.
 * Reported the T1 way (records folded into an FNV-1a 64 hash): the Mac (tests/t1_runner `s3`) and the Vita archive on QEMU's Cortex-A9
 * (qemu/harness.c) must print the same hash. The analytic expectations are this file's own (status); the engine's `physcheck unit3`
 * repeats them through the real Unity API (Stage 3 acceptance #1).
 *
 * Records:
 *   DAMP  velocity 25 u/s, drag 10, no gravity, 400 steps: the distance travelled under H-DAMP pade / exp / linear (expected 2.5 / 2.258 /
 *         2.0: Box2D's Pade factor 1 / (1 + h c), and the two mapped variants)
 *   FORC  AddForce 500 on a 1 kg body: the velocity after the next step (+10 u/s: F h / m) and after the one after (no change: forces are
 *         cleared); two forces in one step add (sp_u_apply's second force); a force held over a step-less tick (accumulators kept) adds
 *   IMPL  an impulse (4, 0) on a 2 kg body: the mirror's velocity at once (+2 u/s), then the body's after the flush; an angular impulse
 *   BNCE  restitution 1 (friction 0): a circle dropped onto a static box at 5 u/s rebounds (> 4.5 u/s); at 0.5 u/s (below
 *         b2_velocityThreshold 1.0) it does not (< 0.1 u/s)
 *   CLMP  velocity 300 u/s, drag 0: the translation of the first step (Box2D's b2_maxTranslation 2.0 per step = H-MAXTRANS per-second) and
 *         the velocity Box2D leaves (100 u/s) -- a world without clamps, stock Box2D
 *   CLM2  E1, patch P2 (sp_world_set_clamps 100 u / 2 pi rad per step, Unity's maxTranslationSpeed / maxRotationSpeed per step): 300 u/s moves
 *         6 u in a step and keeps its velocity (E1's MaxTransRot L0); 6000 u/s is clamped to 100 u (velocity 5000 after Box2D's ratio);
 *         5000 deg/s turns 1.745 rad (beyond stock's pi / 2; E1's L4: 99.9 deg with angular drag 0.05) and 400 rad/s is clamped to 2 pi
 *   MVGR  E1, H-MOVEDRAG=drag-off-gravity-on (SP_USTEP_MOVEGRAV_ON): a gravity-scale-1 body moved +0.03 a step by MovePosition with drag 10
 *         falls h^2 g = 0.003924 a step and its velocity is restored (E1's MoveVel L1 / L2); without the flag it does not fall
 *   FRZX  FreezePositionX: a body pushed with (5, 5) u/s keeps x exactly, moves in y; its x velocity reads 0
 *   ONES  H-CHAIN one-sided: a circle moving into an edge from its back passes, from its front stops; the two-sided control stops both
 *   CONT  sp_u_contacts_between: a circle pressed onto a static box: one point, the normal from the circle to the box (and flipped for the
 *         reversed sets), the separation, the impulse, pre- / post-solve relative velocities
 * status: 0, or -300 - n for the first failed expectation n. */
#include <math.h>
#include <stdint.h>
#include <string.h>

#include "shepphys.h"
#include "sp_t1.h"

namespace {

struct S3 {
    sp_t1_record_fn fn; void *user;
    uint64_t h; int32_t records, words, status, steps;
};

void rec(S3 *s, uint32_t tag, int32_t a, int32_t b, const uint32_t *w, int32_t n, uint32_t fmask)
{
    uint32_t hd[4] = {tag, (uint32_t)a, (uint32_t)b, (uint32_t)n};
    const uint8_t *p = (const uint8_t *)hd;
    for (int i = 0; i < 16; ++i) s->h = (s->h ^ p[i]) * 1099511628211ull;
    p = (const uint8_t *)w;
    for (int32_t i = 0; i < n * 4; ++i) s->h = (s->h ^ p[i]) * 1099511628211ull;
    s->records++; s->words += n;
    if (s->fn) s->fn(s->user, tag, a, b, w, n, fmask);
}
void fail(S3 *s, int32_t code) { if (s->status == 0) s->status = -300 - code; }
uint32_t fbits(float f) { uint32_t u; memcpy(&u, &f, 4); return u; }
float fabs_(float f) { return f < 0 ? -f : f; }
void recf(S3 *s, uint32_t tag, int32_t a, int32_t b, const float *f, int32_t n)
{
    uint32_t w[SP_T1_MAX_WORDS];
    for (int32_t i = 0; i < n && i < SP_T1_MAX_WORDS; ++i) w[i] = fbits(f[i]);
    rec(s, tag, a, b, w, n, (1u << n) - 1u);
}

const float kDt = 0.02f;

int32_t world(float gy)
{
    sp_uworld_def d;
    memset(&d, 0, sizeof d);
    d.gravity_x = 0.0f; d.gravity_y = gy; d.flags = SP_WORLD_DEFAULT_FLAGS; d.use_matrix = 1;
    for (int i = 0; i < 32; ++i) d.matrix[i] = 0xFFFFFFFFu;
    return sp_uworld_create(&d);
}
sp_ubody_def bdef(int32_t type, float x, float y)
{
    sp_ubody_def b;
    memset(&b, 0, sizeof b);
    b.type = type; b.flags = SP_UB_SIMULATED | SP_UB_NEVER_SLEEP | SP_UB_FREEZE_ROTATION; b.x = x; b.y = y; b.m00 = 1.0f; b.m10 = 0.0f;
    b.mass = 1.0f; b.linear_drag = 0.0f; b.angular_drag = 0.05f; b.gravity_scale = 0.0f;
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
int32_t circle(int32_t w, int32_t b, float r, float fric, float rest)
{
    sp_ushape_def d = sdef(SP_US_CIRCLE);
    d.radius = r; d.friction = fric; d.restitution = rest;
    int32_t h = 0;
    sp_ushape_create(w, b, &d, nullptr, &h, 1);
    return h;
}
int32_t box(int32_t w, int32_t b, float sx, float sy, float fric, float rest)
{
    sp_ushape_def d = sdef(SP_US_BOX);
    d.sx = sx; d.sy = sy; d.friction = fric; d.restitution = rest;
    int32_t h = 0;
    sp_ushape_create(w, b, &d, nullptr, &h, 1);
    return h;
}
int32_t mk(int32_t w, sp_ubody_def d) { return sp_ubody_create(w, &d); }
sp_mirror *mir(int32_t w, int32_t b) { return (sp_mirror *)((unsigned char *)sp_pool_base() + sp_ubody_mirror(w, b)); }
void set_vel(int32_t w, int32_t b, float vx, float vy) { sp_mirror *m = mir(w, b); m->vx = vx; m->vy = vy; m->flags |= SP_MF_SET_VEL; }
int32_t step(S3 *s, int32_t w, int32_t k, int32_t flags = 0)
{
    sp_ustep_def d = {kDt, 8, 3, k, flags, 0};
    sp_ustep_out t;
    s->steps++;
    return sp_u_step(w, &d, &t);
}

/* ---------------- DAMP: 25 u/s, drag 10 ---------------- */
void t_damp(S3 *s)
{
    const int32_t fl[3] = {0, SP_USTEP_DAMP_EXP, SP_USTEP_DAMP_LINEAR};
    const float want[3] = {2.5f, 2.25832f, 2.0f};
    for (int32_t v = 0; v < 3; ++v) {
        int32_t w = world(0.0f);
        sp_ubody_def d = bdef(SP_BODY_DYNAMIC, 0.0f, 0.0f);
        d.linear_drag = 10.0f;
        int32_t b = mk(w, d);
        circle(w, b, 0.3f, 0.4f, 0.0f);
        set_vel(w, b, 25.0f, 0.0f);
        float v1 = 0.0f;
        for (int32_t k = 0; k < 400; ++k) { step(s, w, k, fl[v]); if (k == 0) v1 = mir(w, b)->vx; }
        sp_mirror *m = mir(w, b);
        const float f[4] = {m->x, v1, m->vx, want[v]};
        recf(s, SP_T1_TAG('D', 'A', 'M', 'P'), v, 0, f, 4);
        if (fabs_(m->x - want[v]) > 1e-3f) fail(s, 1 + v);
        sp_uworld_destroy(w);
    }
}

/* ---------------- FORC: AddForce 500 on 1 kg ---------------- */
void t_force(S3 *s)
{
    int32_t w = world(0.0f);
    int32_t b = mk(w, bdef(SP_BODY_DYNAMIC, 0.0f, 0.0f));
    circle(w, b, 0.3f, 0.4f, 0.0f);
    sp_ubody_mass(w, b);   /* Unity's mass once the shapes exist (the engine's flush does the same) */
    step(s, w, 0);
    float f0[2] = {500.0f, 0.0f};
    sp_u_apply(w, b, SP_APPLY_FORCE, f0);   /* the binding's first force of a step: a copy; through the native add here */
    step(s, w, 1);
    float va = mir(w, b)->vx;
    step(s, w, 2);
    float vb = mir(w, b)->vx;
    float f1[2] = {250.0f, 0.0f};
    sp_u_apply(w, b, SP_APPLY_FORCE, f1);
    sp_u_apply(w, b, SP_APPLY_FORCE, f1);   /* two forces in one step add */
    step(s, w, 3);
    float vc = mir(w, b)->vx;
    sp_u_apply(w, b, SP_APPLY_FORCE, f1);   /* a tick without a step: the accumulators carry it ... */
    sp_u_flush(w, 0);
    sp_u_apply(w, b, SP_APPLY_FORCE, f1);   /* ... and the next tick's force adds */
    step(s, w, 4);
    float vd = mir(w, b)->vx;
    float t0[1] = {3.0f};
    sp_u_apply(w, b, SP_APPLY_TORQUE, t0);   /* rotation frozen: no effect, and cleared */
    step(s, w, 5);
    sp_mirror *m = mir(w, b);
    const float f[6] = {va, vb, vc, vd, m->w, m->torque};
    recf(s, SP_T1_TAG('F', 'O', 'R', 'C'), 0, m->flags & SP_MF_FORCE, f, 6);
    if (va != 10.0f || vb != 10.0f || vc != 20.0f || vd != 30.0f || m->w != 0.0f || (m->flags & SP_MF_FORCE)) fail(s, 4);
    sp_uworld_destroy(w);
}

/* ---------------- IMPL: impulses ---------------- */
void t_impulse(S3 *s)
{
    int32_t w = world(0.0f);
    sp_ubody_def d = bdef(SP_BODY_DYNAMIC, 0.0f, 0.0f);
    d.mass = 2.0f; d.flags &= ~SP_UB_FREEZE_ROTATION;
    int32_t b = mk(w, d);
    circle(w, b, 0.5f, 0.4f, 0.0f);
    sp_ubody_mass(w, b);
    float j[2] = {4.0f, 0.0f};
    sp_u_apply(w, b, SP_APPLY_IMPULSE, j);
    float now = mir(w, b)->vx;
    float aj[1] = {1.0f};
    sp_u_apply(w, b, SP_APPLY_ANGULAR_IMPULSE, aj);
    float wnow = mir(w, b)->w;
    sp_u_flush(w, 0);
    sp_pose p;
    sp_body_get_pose(w, b, &p);
    sp_mirror *m = mir(w, b);
    const float f[6] = {now, p.vx, wnow, p.w, m->inv_mass, m->inv_i};
    recf(s, SP_T1_TAG('I', 'M', 'P', 'L'), 0, 0, f, 6);
    /* mass 2 (Unity's), I = mass / auto-mass x the disc's 0.5 m r^2 about the centre: 0.25 */
    if (now != 2.0f || p.vx != 2.0f || m->inv_mass != 0.5f || fabs_(wnow - 4.0f) > 1e-5f || fabs_(p.w - wnow) > 0.0f) fail(s, 5);
    sp_uworld_destroy(w);
}

/* ---------------- BNCE: restitution and the velocity threshold ---------------- */
void t_bounce(S3 *s)
{
    const float speeds[2] = {5.0f, 0.5f};
    for (int32_t i = 0; i < 2; ++i) {
        int32_t w = world(0.0f);
        int32_t g = mk(w, bdef(SP_BODY_STATIC, 0.0f, 0.0f));
        box(w, g, 4.0f, 1.0f, 0.0f, 1.0f);
        int32_t b = mk(w, bdef(SP_BODY_DYNAMIC, 0.0f, 1.2f));
        circle(w, b, 0.5f, 0.0f, 1.0f);
        set_vel(w, b, 0.0f, -speeds[i]);
        float vy = 0.0f, ymin = 10.0f;
        for (int32_t k = 0; k < 60; ++k) { step(s, w, k); sp_mirror *m = mir(w, b); vy = m->vy; if (m->y < ymin) ymin = m->y; }
        const float f[3] = {speeds[i], vy, ymin};
        recf(s, SP_T1_TAG('B', 'N', 'C', 'E'), i, 0, f, 3);
        if (i == 0 && !(vy > 4.5f)) fail(s, 6);
        if (i == 1 && !(fabs_(vy) < 0.1f)) fail(s, 7);
        sp_uworld_destroy(w);
    }
}

/* ---------------- CLMP: b2_maxTranslation ---------------- */
void t_clamp(S3 *s)
{
    int32_t w = world(0.0f);
    int32_t b = mk(w, bdef(SP_BODY_DYNAMIC, 0.0f, 0.0f));
    circle(w, b, 0.3f, 0.4f, 0.0f);
    set_vel(w, b, 300.0f, 0.0f);
    step(s, w, 0);
    sp_mirror *m = mir(w, b);
    const float f[2] = {m->x, m->vx};
    recf(s, SP_T1_TAG('C', 'L', 'M', 'P'), 0, 0, f, 2);
    if (fabs_(m->x - 2.0f) > 1e-5f || fabs_(m->vx - 100.0f) > 1e-3f) fail(s, 8);
    sp_uworld_destroy(w);
}

/* ---------------- CLM2: patch P2's per-step clamps (E1) ---------------- */
void t_clamp2(S3 *s)
{
    const float tr[2] = {100.0f, 360.0f * (3.14159265359f / 180.0f)};
    const float vx[2] = {300.0f, 6000.0f};
    for (int32_t i = 0; i < 2; ++i) {
        int32_t w = world(0.0f);
        if (sp_world_set_clamps(w, tr) != SP_OK) fail(s, 30);
        int32_t b = mk(w, bdef(SP_BODY_DYNAMIC, 0.0f, 0.0f));
        circle(w, b, 0.3f, 0.4f, 0.0f);
        set_vel(w, b, vx[i], 0.0f);
        step(s, w, 0);
        sp_mirror *m = mir(w, b);
        const float f[3] = {vx[i], m->x, m->vx};
        recf(s, SP_T1_TAG('C', 'L', 'M', '2'), i, 0, f, 3);
        if (i == 0 && (m->x != 6.0f || m->vx != 300.0f)) fail(s, 31);
        if (i == 1 && (fabs_(m->x - 100.0f) > 1e-3f || fabs_(m->vx - 5000.0f) > 0.05f)) fail(s, 32);
        sp_uworld_destroy(w);
    }
    const float wv[2] = {5000.0f * (3.14159265359f / 180.0f), 400.0f};
    for (int32_t i = 0; i < 2; ++i) {
        int32_t w = world(0.0f);
        sp_world_set_clamps(w, tr);
        sp_ubody_def d = bdef(SP_BODY_DYNAMIC, 0.0f, 0.0f);
        d.flags &= ~SP_UB_FREEZE_ROTATION; d.angular_drag = 0.0f;
        int32_t b = mk(w, d);
        circle(w, b, 0.3f, 0.4f, 0.0f);
        sp_mirror *m = mir(w, b); m->w = wv[i]; m->flags |= SP_MF_SET_VEL;
        step(s, w, 0);
        m = mir(w, b);
        const float f[3] = {wv[i], m->angle, m->w};
        recf(s, SP_T1_TAG('C', 'L', 'M', '2'), 2 + i, 0, f, 3);
        if (i == 0 && !(m->angle > 1.7f && m->angle < 1.75f)) fail(s, 33);
        if (i == 1 && fabs_(m->angle - tr[1]) > 1e-5f) fail(s, 34);
        sp_uworld_destroy(w);
    }
}

/* ---------------- MVGR: a move step keeps gravity (E1's H-MOVEDRAG) ---------------- */
void t_move_gravity(S3 *s)
{
    for (int32_t v = 0; v < 2; ++v) {
        int32_t w = world(-9.81f);
        sp_ubody_def d = bdef(SP_BODY_DYNAMIC, 0.0f, 20.0f);
        d.gravity_scale = 1.0f; d.linear_drag = 10.0f;
        int32_t b = mk(w, d);
        circle(w, b, 0.5f, 0.4f, 0.0f);
        float y1 = 0.0f;
        for (int32_t k = 0; k < 2; ++k) {
            sp_mirror *m = mir(w, b);
            m->tx = m->x + 0.03f; m->ty = m->y; m->flags |= SP_MF_MOVE_POS;
            step(s, w, k, v ? SP_USTEP_MOVEGRAV_ON : 0);
            if (k == 0) y1 = mir(w, b)->y;
        }
        sp_mirror *m = mir(w, b);
        const float f[5] = {m->x, y1, m->y, m->vx, m->vy};
        recf(s, SP_T1_TAG('M', 'V', 'G', 'R'), v, 0, f, 5);
        if (fabs_(m->x - 0.06f) > 1e-6f || m->vx != 0.0f || m->vy != 0.0f) fail(s, 35 + v);
        if (v == 0 && m->y != 20.0f) fail(s, 37);
        if (v == 1 && (fabs_((20.0f - y1) - 0.003924f) > 2e-6f || fabs_((y1 - m->y) - 0.003924f) > 2e-6f)) fail(s, 38);
        sp_uworld_destroy(w);
    }
}

/* ---------------- FRZX: FreezePositionX ---------------- */
void t_freeze(S3 *s)
{
    int32_t w = world(0.0f);
    sp_ubody_def d = bdef(SP_BODY_DYNAMIC, 1.5f, 0.0f);
    d.flags |= SP_UB_FREEZE_X;
    int32_t b = mk(w, d);
    circle(w, b, 0.3f, 0.4f, 0.0f);
    set_vel(w, b, 5.0f, 5.0f);
    for (int32_t k = 0; k < 10; ++k) step(s, w, k);
    sp_mirror *m = mir(w, b);
    const float f[4] = {m->x, m->y, m->vx, m->vy};
    recf(s, SP_T1_TAG('F', 'R', 'Z', 'X'), 0, 0, f, 4);
    if (m->x != 1.5f || fabs_(m->y - 1.0f) > 1e-5f || m->vx != 0.0f || m->vy != 5.0f) fail(s, 9);
    sp_uworld_destroy(w);
}

/* ---------------- ONES: one-sided chains (H-CHAIN) ---------------- */
float one_sided_run(S3 *s, bool one_sided, float y0, float vy)
{
    int32_t w = world(0.0f);
    int32_t g = mk(w, bdef(SP_BODY_STATIC, 0.0f, 0.0f));
    sp_ushape_def e = sdef(SP_US_EDGE);
    const float pts[4] = {-2.0f, 0.0f, 2.0f, 0.0f};   /* v1 -> v2 along +x: the right-hand normal (0, -1) faces down, the front */
    e.vertex_count = 2; e.flags = one_sided ? SP_USF_ONE_SIDED : 0;
    int32_t h = 0;
    sp_ushape_create(w, g, &e, pts, &h, 1);
    int32_t b = mk(w, bdef(SP_BODY_DYNAMIC, 0.0f, y0));
    circle(w, b, 0.25f, 0.0f, 0.0f);
    for (int32_t k = 0; k < 30; ++k) { set_vel(w, b, 0.0f, vy); step(s, w, k); }
    float y = mir(w, b)->y;
    sp_uworld_destroy(w);
    return y;
}
void t_one_sided(S3 *s)
{
    const float r[4] = {one_sided_run(s, true, 1.0f, -5.0f), one_sided_run(s, true, -1.0f, 5.0f),
                        one_sided_run(s, false, 1.0f, -5.0f), one_sided_run(s, false, -1.0f, 5.0f)};
    recf(s, SP_T1_TAG('O', 'N', 'E', 'S'), 0, 0, r, 4);
    /* from the back (above): through, far below; from the front (below): stopped under the edge; two-sided: both stopped */
    if (!(r[0] < -1.0f) || !(r[1] < 0.0f && r[1] > -0.3f) || !(r[2] > 0.0f && r[2] < 0.3f) || !(r[3] < 0.0f && r[3] > -0.3f)) fail(s, 10);
}

/* ---------------- CONT: the contact snapshot ---------------- */
void t_contacts(S3 *s)
{
    int32_t w = world(0.0f);
    int32_t g = mk(w, bdef(SP_BODY_STATIC, 0.0f, 0.0f));
    int32_t bx = box(w, g, 4.0f, 1.0f, 0.4f, 0.0f);
    int32_t b = mk(w, bdef(SP_BODY_DYNAMIC, 0.25f, 0.9f));
    int32_t c = circle(w, b, 0.5f, 0.4f, 0.0f);
    sp_ucontact uc[4];
    float rel[2];
    int32_t n0 = 0, n1 = 0, n2 = 0;
    for (int32_t k = 0; k < 6; ++k) { set_vel(w, b, 0.0f, -3.0f); step(s, w, k); }
    const int32_t sa[1] = {c}, sb[1] = {bx};
    n0 = sp_u_contacts_between(w, sa, 1, sb, 1, 0, uc, 4, rel);
    const float fpre[12] = {uc[0].px, uc[0].py, uc[0].nx, uc[0].ny, uc[0].separation, uc[0].normal_impulse, uc[0].tangent_impulse, uc[0].rvx, uc[0].rvy,
                            rel[0], rel[1], (float)uc[0].flags};
    recf(s, SP_T1_TAG('C', 'O', 'N', 'T'), 0, n0, fpre, 12);
    n1 = sp_u_contacts_between(w, sa, 1, sb, 1, SP_UCF_POST_SOLVE, uc, 4, rel);
    const float fpost[4] = {uc[0].rvx, uc[0].rvy, rel[0], rel[1]};
    recf(s, SP_T1_TAG('C', 'O', 'N', 'T'), 1, n1, fpost, 4);
    n2 = sp_u_contacts_between(w, sb, 1, sa, 1, 0, uc, 4, rel);
    const float frev[5] = {uc[0].nx, uc[0].ny, uc[0].rvy, rel[1], (float)(uc[0].shape_a == bx && uc[0].shape_b == c)};
    recf(s, SP_T1_TAG('C', 'O', 'N', 'T'), 2, n2, frev, 5);
    /* the circle (a) pressed down onto the box (b): the normal from a to b points down; b's velocity minus a's: +3 up before the solve */
    if (n0 != 1 || n1 != 1 || n2 != 1 || fabs_(fpre[2]) > 1e-6f || fabs_(fpre[3] + 1.0f) > 1e-6f || fabs_(fpre[8] - 3.0f) > 1e-5f ||
        fabs_(fpre[10] - 3.0f) > 1e-5f || !(fpre[5] > 0.0f) || fabs_(frev[1] - 1.0f) > 1e-6f || fabs_(frev[2] + 3.0f) > 1e-5f || frev[4] != 1.0f ||
        fabs_(fpost[1]) > 1e-3f)
        fail(s, 11);
    sp_uworld_destroy(w);
}

} /* namespace */

extern "C" SP_API int32_t sp_s3_run(sp_t1_record_fn fn, void *user, sp_t1_result *out)
{
    S3 s;
    memset(&s, 0, sizeof s);
    s.fn = fn; s.user = user; s.h = 14695981039346656037ull;
    bool was_up = sp_pool_base() != nullptr;
    if (!was_up) { sp_init_def d = {0, 0, 0, 0}; sp_init(&d); }
    int32_t sizes[SP_ABI_N_SIZES];
    float probe = 0;
    int32_t v = sp_abi(sizes, SP_ABI_N_SIZES, &probe);
    rec(&s, SP_T1_TAG('A', 'B', 'I', ' '), v, SP_ABI_N_SIZES, (const uint32_t *)sizes, SP_ABI_N_SIZES, 0);
    if (v != SHEPPHYS_ABI_VERSION) fail(&s, 0);
    t_damp(&s);
    t_force(&s);
    t_impulse(&s);
    t_bounce(&s);
    t_clamp(&s);
    t_clamp2(&s);
    t_move_gravity(&s);
    t_freeze(&s);
    t_one_sided(&s);
    t_contacts(&s);
    if (!was_up) sp_shutdown();
    if (out) {
        out->hash_lo = (uint32_t)s.h; out->hash_hi = (uint32_t)(s.h >> 32);
        out->records = s.records; out->words = s.words; out->steps = s.steps; out->status = s.status;
    }
    return s.status;
}
