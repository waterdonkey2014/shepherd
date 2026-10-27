/* sp_t1.cpp -- T1 determinism scenarios (ours). See sp_t1.h. Uses only the shepphys C API.
 * Scenario maths: float + - * /, sqrtf (IEEE correctly rounded on every target), sp_sinf / sp_cosf (shim/sp_trig.c).
 * Compiled with the library's flags (-ffp-contract=off), so the driving code is as deterministic as the core.
 */
#include "sp_t1.h"

#include <math.h>
#include <string.h>

#include "golden/t1_golden.h"

namespace {

const float DT = 0.02f;   /* CotL's fixedDeltaTime */
const float TWO_PI = 6.28318531f;

enum : uint32_t {
    TAG_POSE = SP_T1_TAG('P', 'O', 'S', 'E'),
    TAG_EVNT = SP_T1_TAG('E', 'V', 'N', 'T'),
    TAG_EVSM = SP_T1_TAG('E', 'V', 'S', 'M'),
    TAG_STAT = SP_T1_TAG('S', 'T', 'A', 'T'),
    TAG_CONT = SP_T1_TAG('C', 'O', 'N', 'T'),
    TAG_CNTN = SP_T1_TAG('C', 'N', 'T', 'N'),
    TAG_RAYH = SP_T1_TAG('R', 'A', 'Y', 'H'),
    TAG_QURY = SP_T1_TAG('Q', 'U', 'R', 'Y'),
    TAG_META = SP_T1_TAG('M', 'E', 'T', 'A'),
    TAG_ERR = SP_T1_TAG('E', 'R', 'R', '!'),
};

struct Ctx {
    sp_t1_record_fn fn;
    void *user;
    uint64_t h;
    int32_t records, words, steps, status;
    int32_t W;
    sp_step_stats last;
};

inline uint32_t fb(float f) { uint32_t u; memcpy(&u, &f, 4); return u; }
inline int32_t slot(int32_t h) { return h > 0 ? (h & 0xFFFF) : h; }

void mix(uint64_t &h, uint32_t w)
{
    for (int i = 0; i < 4; ++i) {
        h ^= (w >> (8 * i)) & 0xFFu;
        h *= 0x100000001b3ULL;
    }
}

void rec(Ctx &c, uint32_t tag, int32_t a, int32_t b, const uint32_t *w, int32_t n, uint32_t fmask)
{
    if (n > SP_T1_MAX_WORDS) n = SP_T1_MAX_WORDS;
    mix(c.h, tag); mix(c.h, (uint32_t)a); mix(c.h, (uint32_t)b); mix(c.h, (uint32_t)n);
    for (int32_t i = 0; i < n; ++i) mix(c.h, w[i]);
    c.records++;
    c.words += n;
    if (c.fn) c.fn(c.user, tag, a, b, w, n, fmask);
}

bool ok(Ctx &c, int32_t r)
{
    if (r < 0 && c.status == 0) {
        c.status = r;
        uint32_t w = (uint32_t)r;
        rec(c, TAG_ERR, c.steps, 0, &w, 1, 0);
    }
    return r >= 0;
}

/* ---- builders ---- */
void world(Ctx &c, float gx, float gy)
{
    sp_world_def d;
    d.gravity_x = gx; d.gravity_y = gy; d.flags = SP_WORLD_DEFAULT_FLAGS; d.reserved = 0;
    c.W = sp_world_create(&d);
    ok(c, c.W);
}

sp_body_def bdef(int32_t type, float x, float y, float angle = 0.0f)
{
    sp_body_def d;
    memset(&d, 0, sizeof d);
    d.type = type;
    d.flags = SP_BODY_AWAKE | SP_BODY_ALLOW_SLEEP | SP_BODY_ACTIVE;
    d.x = x; d.y = y; d.angle = angle;
    d.gravity_scale = 1.0f;
    return d;
}

int32_t body(Ctx &c, const sp_body_def &d)
{
    int32_t h = sp_body_create(c.W, &d);
    ok(c, h);
    return h;
}

sp_shape_def sdef(int32_t kind, float density, float friction, int32_t flags)
{
    sp_shape_def d;
    memset(&d, 0, sizeof d);
    d.kind = kind; d.flags = flags;
    d.density = density; d.friction = friction;
    d.category_bits = 1; d.mask_bits = 0xFFFF;
    return d;
}

int32_t shape(Ctx &c, int32_t b, const sp_shape_def &d, const float *xy)
{
    int32_t h = sp_shape_create(c.W, b, &d, xy);
    ok(c, h);
    return h;
}

int32_t circle(Ctx &c, int32_t b, float r, float cx, float cy, float density, float friction, float rest, int32_t flags)
{
    sp_shape_def d = sdef(SP_SHAPE_CIRCLE, density, friction, flags);
    d.radius = r; d.cx = cx; d.cy = cy; d.restitution = rest;
    return shape(c, b, d, nullptr);
}

int32_t box(Ctx &c, int32_t b, float hx, float hy, float cx, float cy, float angle, float density, float friction,
            int32_t flags)
{
    sp_shape_def d = sdef(SP_SHAPE_BOX, density, friction, flags);
    d.hx = hx; d.hy = hy; d.cx = cx; d.cy = cy; d.angle = angle;
    return shape(c, b, d, nullptr);
}

int32_t poly(Ctx &c, int32_t b, const float *xy, int32_t n, float density, float friction, int32_t flags)
{
    sp_shape_def d = sdef(SP_SHAPE_POLYGON, density, friction, flags);
    d.vertex_count = n;
    return shape(c, b, d, xy);
}

int32_t edge(Ctx &c, int32_t b, const float *xy8, int32_t flags, float friction)
{
    sp_shape_def d = sdef(SP_SHAPE_EDGE, 0.0f, friction, flags);
    d.vertex_count = 4;
    return shape(c, b, d, xy8);
}

int32_t chain(Ctx &c, int32_t b, const float *xy, int32_t n, int32_t flags, float friction)
{
    sp_shape_def d = sdef(SP_SHAPE_CHAIN, 0.0f, friction, flags);
    d.vertex_count = n;
    return shape(c, b, d, xy);
}

void regular(float *xy, int32_t n, float r, float cx, float cy, float phase)
{
    for (int32_t i = 0; i < n; ++i) {
        float a = phase + (float)i * (TWO_PI / (float)n);
        xy[2 * i] = cx + r * sp_cosf(a);
        xy[2 * i + 1] = cy + r * sp_sinf(a);
    }
}

sp_pose pose(Ctx &c, int32_t b)
{
    sp_pose p;
    memset(&p, 0, sizeof p);
    ok(c, sp_body_get_pose(c.W, b, &p));
    return p;
}

/* Unity-style MovePosition / MoveRotation for one step: velocity = (target - current) / dt (stock Box2D; the movement
 * state with no gravity/drag and velocity restore is a later Unity-layer patch) */
void move_to(Ctx &c, int32_t b, float tx, float ty, float ta, bool rot)
{
    sp_pose p = pose(c, b);
    float v[3] = {(tx - p.x) / DT, (ty - p.y) / DT, rot ? (ta - p.angle) / DT : 0.0f};
    ok(c, sp_body_set_velocity(c.W, b, v));
}

void set_vel(Ctx &c, int32_t b, float vx, float vy, float w)
{
    float v[3] = {vx, vy, w};
    ok(c, sp_body_set_velocity(c.W, b, v));
}

/* ---- recorders ---- */
void step(Ctx &c)
{
    sp_step_def d = {DT, 8, 3, 0};
    ok(c, sp_world_step(c.W, &d, &c.last));
    c.steps++;
}

void rec_poses(Ctx &c)
{
    sp_pose p[160];
    int32_t n = sp_poses(c.W, p, 160);
    if (!ok(c, n)) return;
    for (int32_t i = 0; i < n && i < 160; ++i) {
        uint32_t w[7] = {fb(p[i].x), fb(p[i].y), fb(p[i].angle), fb(p[i].vx), fb(p[i].vy), fb(p[i].w), (uint32_t)p[i].flags};
        rec(c, TAG_POSE, c.steps, slot(p[i].body), w, 7, 0x3F);
    }
}

void rec_events(Ctx &c)
{
    sp_event e[64];
    int32_t n;
    while ((n = sp_events(c.W, e, 64)) > 0)
        for (int32_t i = 0; i < n; ++i) {
            uint32_t w[3] = {(uint32_t)e[i].flags, (uint32_t)slot(e[i].shape_a), (uint32_t)slot(e[i].shape_b)};
            rec(c, TAG_EVNT, e[i].step, e[i].kind, w, 3, 0);
        }
    ok(c, n);
}

/* events folded into a running window summary instead of one record each (the crowd scenario has thousands) */
struct EvSum { uint64_t h = 0xcbf29ce484222325ULL; int32_t begin = 0, end = 0; };
void sum_events(Ctx &c, EvSum &s)
{
    sp_event e[64];
    int32_t n;
    while ((n = sp_events(c.W, e, 64)) > 0)
        for (int32_t i = 0; i < n; ++i) {
            if (e[i].kind == SP_EV_BEGIN) s.begin++; else s.end++;
            mix(s.h, (uint32_t)e[i].kind); mix(s.h, (uint32_t)e[i].flags);
            mix(s.h, (uint32_t)e[i].shape_a); mix(s.h, (uint32_t)e[i].shape_b); mix(s.h, (uint32_t)e[i].step);
        }
    ok(c, n);
}
void rec_evsum(Ctx &c, EvSum &s)
{
    uint32_t w[4] = {(uint32_t)s.begin, (uint32_t)s.end, (uint32_t)s.h, (uint32_t)(s.h >> 32)};
    rec(c, TAG_EVSM, c.steps, 0, w, 4, 0);
    s = EvSum();
}

void rec_stats(Ctx &c)
{
    const sp_step_stats &s = c.last;
    uint32_t w[8] = {(uint32_t)s.bodies, (uint32_t)s.awake_bodies, (uint32_t)s.shapes, (uint32_t)s.contacts,
                     (uint32_t)s.touching, (uint32_t)s.proxies, (uint32_t)s.tree_height, (uint32_t)s.events_queued};
    rec(c, TAG_STAT, c.steps, 0, w, 8, 0);
}

void rec_contacts(Ctx &c, int32_t flags)
{
    sp_contact k[128];
    int32_t n = sp_contacts(c.W, k, 128, flags);
    if (!ok(c, n)) return;
    rec(c, TAG_CNTN, c.steps, n, nullptr, 0, 0);
    for (int32_t i = 0; i < n && i < 128; ++i) {
        const sp_contact &o = k[i];
        uint32_t w[20] = {(uint32_t)slot(o.shape_a), (uint32_t)slot(o.shape_b), (uint32_t)o.child_a, (uint32_t)o.child_b,
                          (uint32_t)o.flags, (uint32_t)o.point_count,
                          fb(o.normal_x), fb(o.normal_y), fb(o.px[0]), fb(o.py[0]), fb(o.px[1]), fb(o.py[1]),
                          fb(o.separation[0]), fb(o.separation[1]), fb(o.normal_impulse[0]), fb(o.normal_impulse[1]),
                          fb(o.tangent_impulse[0]), fb(o.tangent_impulse[1]), fb(o.friction), fb(o.restitution)};
        rec(c, TAG_CONT, c.steps, i, w, 20, 0xFFFC0u);
    }
}

/* ================================================================ scenarios */

/* 0: mixed boxes, circles and polygons fall on a ground box and a sloped edge (gravity, rotation -> trig, sleep) */
void sc_fall_boxes(Ctx &c)
{
    world(c, 0.0f, -10.0f);
    int32_t g = body(c, bdef(SP_BODY_STATIC, 0.0f, 0.0f));
    box(c, g, 20.0f, 0.5f, 0.0f, -0.5f, 0.0f, 0.0f, 0.6f, 0);
    const float slope[8] = {-14.0f, 6.0f, -12.0f, 4.0f, -4.0f, 0.0f, -2.0f, 0.0f};
    edge(c, g, slope, SP_SHAPE_GHOST_PREV | SP_SHAPE_GHOST_NEXT, 0.4f);
    float pent[10];
    regular(pent, 5, 0.45f, 0.0f, 0.0f, 0.3f);
    for (int32_t i = 0; i < 12; ++i) {
        float x = -9.0f + 1.4f * (float)i, y = 3.0f + 1.7f * (float)(i % 4), a = 0.13f * (float)i - 0.4f;
        int32_t b = body(c, bdef(SP_BODY_DYNAMIC, x, y, a));
        switch (i % 4) {
        case 0: box(c, b, 0.4f, 0.3f, 0.0f, 0.0f, 0.0f, 1.0f, 0.3f, 0); break;
        case 1: circle(c, b, 0.35f, 0.0f, 0.0f, 1.0f, 0.3f, 0.1f, 0); break;
        case 2: poly(c, b, pent, 5, 1.0f, 0.3f, 0); break;
        default: box(c, b, 0.5f, 0.25f, 0.1f, 0.0f, 0.2f, 2.0f, 0.3f, 0); break;
        }
    }
    rec_poses(c);
    for (int32_t s = 1; s <= 300; ++s) {
        step(c);
        rec_events(c);
        if (s % 20 == 0) { rec_poses(c); rec_stats(c); }
    }
    rec_contacts(c, SP_CONTACTS_TOUCHING_ONLY);
}

/* 1: a 6-high box pyramid and an 8-box column settle and sleep (block solver, warm starting, islands, sleep) */
void sc_stack_boxes(Ctx &c)
{
    world(c, 0.0f, -10.0f);
    int32_t g = body(c, bdef(SP_BODY_STATIC, 0.0f, -0.5f));
    box(c, g, 20.0f, 0.5f, 0.0f, 0.0f, 0.0f, 0.0f, 0.6f, 0);
    for (int32_t r = 0; r < 6; ++r) {
        int32_t count = 6 - r;
        for (int32_t i = 0; i < count; ++i) {
            float x = ((float)i - (float)(count - 1) * 0.5f) * 1.05f - 4.0f, y = 0.5f + (float)r;
            int32_t b = body(c, bdef(SP_BODY_DYNAMIC, x, y));
            box(c, b, 0.5f, 0.5f, 0.0f, 0.0f, 0.0f, 1.0f, 0.6f, 0);
        }
    }
    for (int32_t k = 0; k < 8; ++k) {
        int32_t b = body(c, bdef(SP_BODY_DYNAMIC, 5.0f + 0.02f * (float)k, 0.5f + (float)k));
        box(c, b, 0.5f, 0.5f, 0.0f, 0.0f, 0.0f, 1.0f, 0.6f, 0);
    }
    rec_poses(c);
    for (int32_t s = 1; s <= 500; ++s) {
        step(c);
        rec_events(c);
        if (s % 50 == 0) { rec_poses(c); rec_stats(c); }
    }
    rec_contacts(c, SP_CONTACTS_TOUCHING_ONLY);
}

/* 2: chain loop bowl, open chain with ghost vertices, ghosted edges (edge/chain manifolds, two-sided chains) */
void sc_chain_bowl(Ctx &c)
{
    world(c, 0.0f, -10.0f);
    float bowl[48];
    regular(bowl, 24, 6.0f, 0.0f, 0.0f, 0.0f);
    int32_t gb = body(c, bdef(SP_BODY_STATIC, 0.0f, 0.0f));
    chain(c, gb, bowl, 24, SP_SHAPE_LOOP, 0.5f);
    const float zig[20] = {-9.0f, 3.0f, -8.0f, 2.0f, -6.0f, 0.5f, -4.0f, 1.5f, -2.0f, 0.0f,
                           0.0f, 1.0f, 2.0f, -0.5f, 4.0f, 0.5f, 6.0f, 0.0f, 7.0f, 1.0f};
    int32_t gz = body(c, bdef(SP_BODY_STATIC, 20.0f, 0.0f));
    chain(c, gz, zig, 10, SP_SHAPE_GHOST_PREV | SP_SHAPE_GHOST_NEXT, 0.4f);
    int32_t ge = body(c, bdef(SP_BODY_STATIC, 40.0f, 0.0f));
    for (int32_t k = 0; k < 4; ++k) {
        float x0 = -8.0f + 4.0f * (float)k;
        float e[8] = {x0 - 4.0f, 0.0f, x0, 0.0f, x0 + 4.0f, 0.0f, x0 + 8.0f, 0.0f};
        edge(c, ge, e, SP_SHAPE_GHOST_PREV | SP_SHAPE_GHOST_NEXT, 0.1f);
    }
    for (int32_t i = 0; i < 5; ++i) {
        int32_t b = body(c, bdef(SP_BODY_DYNAMIC, -2.0f + (float)i, 2.0f + 0.3f * (float)i));
        circle(c, b, 0.3f + 0.07f * (float)i, 0.0f, 0.0f, 1.0f, 0.3f, 0.0f, 0);
        set_vel(c, b, 1.0f - 0.5f * (float)i, 0.0f, 0.0f);
    }
    for (int32_t k = 0; k < 3; ++k) {
        int32_t b = body(c, bdef(SP_BODY_DYNAMIC, -1.5f + 1.5f * (float)k, 4.0f, 0.3f * (float)k));
        box(c, b, 0.4f, 0.4f, 0.0f, 0.0f, 0.0f, 1.0f, 0.3f, 0);
    }
    float pent[10];
    regular(pent, 5, 0.5f, 0.0f, 0.0f, 0.0f);
    int32_t pb = body(c, bdef(SP_BODY_DYNAMIC, 0.5f, 5.5f, 0.1f));
    poly(c, pb, pent, 5, 1.0f, 0.3f, 0);
    for (int32_t k = 0; k < 3; ++k) {
        int32_t b = body(c, bdef(SP_BODY_DYNAMIC, 13.0f + 2.0f * (float)k, 4.0f));
        circle(c, b, 0.4f, 0.0f, 0.0f, 1.0f, 0.4f, 0.0f, 0);
    }
    sp_body_def sd = bdef(SP_BODY_DYNAMIC, 33.0f, 0.31f);
    sd.flags |= SP_BODY_FIXED_ROTATION;
    sd.vx = 6.0f;
    int32_t sl = body(c, sd);
    box(c, sl, 0.5f, 0.3f, 0.0f, 0.0f, 0.0f, 1.0f, 0.1f, 0);
    rec_poses(c);
    for (int32_t s = 1; s <= 400; ++s) {
        step(c);
        rec_events(c);
        if (s % 25 == 0) { rec_poses(c); rec_stats(c); }
    }
    rec_contacts(c, SP_CONTACTS_TOUCHING_ONLY);
}

/* 3: a kinematic body driven MovePosition-style across static walls, pushing dynamic circles; its sensor meets
 *    dynamic bodies only (stock 2.3: no kinematic-static contacts at all) */
void sc_kinematic_sweep(Ctx &c)
{
    world(c, 0.0f, 0.0f);
    int32_t w1 = body(c, bdef(SP_BODY_STATIC, 3.0f, 0.0f));
    box(c, w1, 0.2f, 2.0f, 0.0f, 0.0f, 0.0f, 0.0f, 0.4f, 0);
    int32_t w2 = body(c, bdef(SP_BODY_STATIC, 6.0f, 0.0f));
    box(c, w2, 0.2f, 2.0f, 0.0f, 0.0f, 0.0f, 0.0f, 0.4f, 0);
    int32_t ws = body(c, bdef(SP_BODY_STATIC, 8.0f, 0.0f));
    box(c, ws, 0.5f, 0.5f, 0.0f, 0.0f, 0.0f, 0.0f, 0.4f, SP_SHAPE_SENSOR);
    int32_t k = body(c, bdef(SP_BODY_KINEMATIC, 0.0f, 0.0f));
    box(c, k, 0.5f, 0.25f, 0.0f, 0.0f, 0.0f, 0.0f, 0.4f, 0);
    circle(c, k, 0.8f, 0.0f, 0.0f, 0.0f, 0.4f, 0.0f, SP_SHAPE_SENSOR);
    const float cx[4] = {1.5f, 4.5f, 7.0f, 9.5f}, cy[4] = {0.1f, -0.2f, 0.3f, 0.0f};
    for (int32_t i = 0; i < 4; ++i) {
        sp_body_def d = bdef(SP_BODY_DYNAMIC, cx[i], cy[i]);
        d.linear_damping = 0.5f;
        int32_t b = body(c, d);
        circle(c, b, 0.3f, 0.0f, 0.0f, 1.0f, 0.3f, 0.0f, 0);
    }
    rec_poses(c);
    for (int32_t s = 1; s <= 250; ++s) {
        float ph = (float)s * (1.0f / 125.0f);
        float tx = ph <= 1.0f ? 12.0f * ph : 12.0f * (2.0f - ph);
        float ty = 0.5f * sp_sinf((float)s * 0.05f);
        float ta = 0.3f * sp_sinf((float)s * 0.03f);
        move_to(c, k, tx, ty, ta, true);
        step(c);
        rec_events(c);
        if (s % 5 == 0) rec_poses(c);
        if (s % 50 == 0) rec_stats(c);
    }
    rec_contacts(c, 0);
}

/* 4: CotL-like top-down movement (no gravity): MovePosition at 45 degrees into a wall chain (friction slide ratio), and a
 *    box pressed along a 50-segment collinear chain (ghost vertices) vs 50 separate edges (internal-vertex snags) */
void sc_topdown_slide(Ctx &c)
{
    world(c, 0.0f, 0.0f);
    int32_t gw = body(c, bdef(SP_BODY_STATIC, 0.0f, 0.0f));
    const float wall[4] = {5.0f, -30.0f, 5.0f, 30.0f};
    chain(c, gw, wall, 2, 0, 0.4f);
    sp_body_def ld = bdef(SP_BODY_DYNAMIC, 3.0f, 0.0f);
    ld.flags |= SP_BODY_FIXED_ROTATION;
    int32_t lamb = body(c, ld);
    circle(c, lamb, 0.3f, 0.0f, 0.0f, 1.0f, 0.4f, 0.0f, 0);

    float line[102];
    for (int32_t i = 0; i <= 50; ++i) { line[2 * i] = (float)i; line[2 * i + 1] = 0.0f; }
    int32_t gc = body(c, bdef(SP_BODY_STATIC, 0.0f, 100.0f));
    chain(c, gc, line, 51, 0, 0.0f);
    int32_t ge = body(c, bdef(SP_BODY_STATIC, 0.0f, 200.0f));
    for (int32_t i = 0; i < 50; ++i) {
        float e[8] = {0.0f, 0.0f, (float)i, 0.0f, (float)(i + 1), 0.0f, 0.0f, 0.0f};
        edge(c, ge, e, 0, 0.0f);
    }
    sp_body_def bd = bdef(SP_BODY_DYNAMIC, 1.0f, 100.4f);
    bd.flags |= SP_BODY_FIXED_ROTATION;
    int32_t bb = body(c, bd);
    box(c, bb, 0.4f, 0.4f, 0.0f, 0.0f, 0.0f, 1.0f, 0.0f, 0);
    bd.y = 200.4f;
    int32_t be = body(c, bd);
    box(c, be, 0.4f, 0.4f, 0.0f, 0.0f, 0.0f, 1.0f, 0.0f, 0);
    bd.x = 30.0f; bd.y = 100.4f;                         /* slow twins (0.5 u/s): more steps near each vertex */
    int32_t bbs = body(c, bd);
    box(c, bbs, 0.4f, 0.4f, 0.0f, 0.0f, 0.0f, 1.0f, 0.0f, 0);
    bd.y = 200.4f;
    int32_t bes = body(c, bd);
    box(c, bes, 0.4f, 0.4f, 0.0f, 0.0f, 0.0f, 1.0f, 0.0f, 0);

    const float speed = 5.5f, d45 = 0.70710678f;
    float y0 = pose(c, lamb).y, y_free = 0.0f, y_slide0 = 0.0f, max_pen = 0.0f;
    float minvx[4] = {1e9f, 1e9f, 1e9f, 1e9f}, maxvy[4] = {0.0f, 0.0f, 0.0f, 0.0f};
    rec_poses(c);
    for (int32_t s = 1; s <= 150; ++s) {
        sp_pose p = pose(c, lamb);
        move_to(c, lamb, p.x + speed * d45 * DT, p.y + speed * d45 * DT, 0.0f, false);
        sp_pose q = pose(c, bb);
        move_to(c, bb, q.x + 5.0f * DT, q.y - 1.0f * DT, 0.0f, false);
        q = pose(c, be);
        move_to(c, be, q.x + 5.0f * DT, q.y - 1.0f * DT, 0.0f, false);
        q = pose(c, bbs);
        move_to(c, bbs, q.x + 0.5f * DT, q.y - 0.5f * DT, 0.0f, false);
        q = pose(c, bes);
        move_to(c, bes, q.x + 0.5f * DT, q.y - 0.5f * DT, 0.0f, false);
        step(c);
        rec_events(c);
        p = pose(c, lamb);
        if (s == 5) y_free = (p.y - y0) / (5.0f * DT);
        if (s == 100) y_slide0 = p.y;
        float pen = (p.x + 0.3f) - 5.0f;
        if (pen > max_pen) max_pen = pen;
        if (s >= 10) {
            const int32_t bs[4] = {bb, be, bbs, bes};
            for (int32_t j = 0; j < 4; ++j) {
                sp_pose r = pose(c, bs[j]);
                if (r.vx < minvx[j]) minvx[j] = r.vx;
                float avy = r.vy < 0.0f ? -r.vy : r.vy;
                if (avy > maxvy[j]) maxvy[j] = avy;
            }
        }
        if (s % 5 == 0) rec_poses(c);
    }
    float slide = (pose(c, lamb).y - y_slide0) / (50.0f * DT);
    uint32_t m0[5] = {fb(slide / speed), fb(slide), fb(y_free), fb(max_pen), fb(0.4243f)};
    rec(c, TAG_META, 0, 0, m0, 5, 0x1F);   /* slide/free ratio (plan: cos45 - 0.4 sin45 = 0.4243), speeds, penetration */
    uint32_t m1[8] = {fb(minvx[0]), fb(maxvy[0]), fb(minvx[1]), fb(maxvy[1]),
                      fb(minvx[2]), fb(maxvy[2]), fb(minvx[3]), fb(maxvy[3])};
    rec(c, TAG_META, 1, 0, m1, 8, 0xFF);   /* chain vs separate edges at 5 and 0.5 u/s: min vx (no snag = the speed),
                                              max |vy| */
}

/* 5: Enter/Exit sequences: movers through static circle / box / polygon / edge / chain-loop sensors; a dynamic sensor
 *    (sensor-sensor pairs), a kinematic sensor (stock: never meets statics); destroy inside a sensor; refilter */
void sc_sensor_sequence(Ctx &c)
{
    world(c, 0.0f, 0.0f);
    int32_t s1b = body(c, bdef(SP_BODY_STATIC, 2.0f, 0.0f));
    int32_t s1 = circle(c, s1b, 1.0f, 0.0f, 0.0f, 0.0f, 0.2f, 0.0f, SP_SHAPE_SENSOR);
    int32_t s2b = body(c, bdef(SP_BODY_STATIC, 5.0f, 0.0f));
    box(c, s2b, 1.0f, 0.5f, 0.0f, 0.0f, 0.0f, 0.0f, 0.2f, SP_SHAPE_SENSOR);
    const float tri[6] = {-0.8f, -0.8f, 0.8f, -0.8f, 0.0f, 0.9f};
    int32_t s3b = body(c, bdef(SP_BODY_STATIC, 8.0f, 0.0f));
    poly(c, s3b, tri, 3, 0.0f, 0.2f, SP_SHAPE_SENSOR);
    const float e4[8] = {0.0f, 0.0f, 0.0f, -1.0f, 0.0f, 1.0f, 0.0f, 0.0f};
    int32_t s4b = body(c, bdef(SP_BODY_STATIC, 11.0f, 0.0f));
    int32_t s4 = edge(c, s4b, e4, SP_SHAPE_SENSOR, 0.2f);
    const float sq[8] = {-1.0f, -1.0f, 1.0f, -1.0f, 1.0f, 1.0f, -1.0f, 1.0f};
    int32_t s5b = body(c, bdef(SP_BODY_STATIC, 14.0f, 0.0f));
    chain(c, s5b, sq, 4, SP_SHAPE_LOOP | SP_SHAPE_SENSOR, 0.2f);

    sp_body_def d = bdef(SP_BODY_DYNAMIC, -1.0f, 0.0f);
    d.vx = 4.0f;
    int32_t m1 = body(c, d);
    circle(c, m1, 0.25f, 0.0f, 0.0f, 1.0f, 0.2f, 0.0f, 0);
    d = bdef(SP_BODY_DYNAMIC, -2.0f, 0.3f);
    d.vx = 3.0f; d.w = 2.0f;
    int32_t m2 = body(c, d);
    box(c, m2, 0.3f, 0.2f, 0.0f, 0.0f, 0.0f, 1.0f, 0.2f, 0);
    d = bdef(SP_BODY_DYNAMIC, -1.5f, -0.3f);
    d.vx = 3.5f;
    int32_t m3 = body(c, d);
    circle(c, m3, 0.2f, 0.0f, 0.0f, 1.0f, 0.2f, 0.0f, SP_SHAPE_SENSOR);
    d = bdef(SP_BODY_KINEMATIC, -1.0f, 0.6f);
    d.vx = 2.0f;
    int32_t m4 = body(c, d);
    circle(c, m4, 0.3f, 0.0f, 0.0f, 0.0f, 0.2f, 0.0f, SP_SHAPE_SENSOR);
    (void)m3; (void)m4;
    rec_poses(c);
    for (int32_t s = 1; s <= 250; ++s) {
        if (s == 100) { ok(c, sp_body_destroy(c.W, m2)); rec_events(c); }
        if (s == 152) { const uint32_t f[3] = {1, 0, 0}; ok(c, sp_shape_set_filter(c.W, s4, f)); rec_events(c); }
        if (s == 200) ok(c, sp_shape_set_sensor(c.W, s1, 0));
        step(c);
        rec_events(c);
        if (s % 25 == 0) { rec_poses(c); rec_stats(c); }
    }
    rec_contacts(c, 0);
}

/* 6: ray casts (closest / all sorted / all unsorted), AABB, point and exact-overlap queries over every shape kind */
void rec_rays(Ctx &c, int32_t base, float ox, float oy, int32_t nrays)
{
    sp_ray_hit hits[16];
    for (int32_t i = 0; i < nrays; ++i) {
        float a = (float)i * (TWO_PI / (float)nrays) + 0.01f;
        sp_ray_query q = {ox, oy, ox + 12.0f * sp_cosf(a), oy + 12.0f * sp_sinf(a), 0xFFFF, 0};
        const int32_t modes[3] = {SP_RAY_CLOSEST | SP_RAY_SKIP_SENSORS, SP_RAY_ALL | SP_RAY_SORTED, SP_RAY_ALL};
        for (int32_t m = 0; m < 3; ++m) {
            q.flags = modes[m];
            int32_t n = sp_raycast(c.W, &q, hits, 16);
            if (!ok(c, n)) return;
            for (int32_t j = 0; j < n && j < 16; ++j) {
                uint32_t w[7] = {(uint32_t)slot(hits[j].shape), (uint32_t)slot(hits[j].body), fb(hits[j].fraction),
                                 fb(hits[j].px), fb(hits[j].py), fb(hits[j].nx), fb(hits[j].ny)};
                rec(c, TAG_RAYH, base + i, m * 100 + j, w, 7, 0x7C);
            }
        }
    }
}

void rec_list(Ctx &c, int32_t a, int32_t b, const int32_t *v, int32_t n)
{
    uint32_t w[SP_T1_MAX_WORDS];
    int32_t k = 0;
    w[k++] = (uint32_t)n;
    for (int32_t i = 0; i < n && k < SP_T1_MAX_WORDS; ++i) w[k++] = (uint32_t)slot(v[i]);
    rec(c, TAG_QURY, a, b, w, k, 0);
}

void sc_raycasts(Ctx &c)
{
    world(c, 0.0f, 0.0f);
    int32_t b;
    b = body(c, bdef(SP_BODY_STATIC, 5.0f, 0.0f));
    circle(c, b, 1.0f, 0.0f, 0.0f, 0.0f, 0.2f, 0.0f, 0);
    b = body(c, bdef(SP_BODY_STATIC, 0.0f, 5.0f, 0.3f));
    box(c, b, 1.0f, 0.5f, 0.0f, 0.0f, 0.0f, 0.0f, 0.2f, 0);
    float pent[10];
    regular(pent, 5, 1.2f, 0.0f, 0.0f, 0.2f);
    b = body(c, bdef(SP_BODY_STATIC, -5.0f, 0.0f));
    poly(c, b, pent, 5, 0.0f, 0.2f, 0);
    const float e[8] = {0.0f, 0.0f, -2.0f, -5.0f, 2.0f, -4.0f, 0.0f, 0.0f};
    b = body(c, bdef(SP_BODY_STATIC, 0.0f, 0.0f));
    edge(c, b, e, 0, 0.2f);
    const float zz[10] = {3.0f, -6.0f, 4.5f, -4.0f, 6.0f, -6.0f, 7.5f, -4.0f, 9.0f, -6.0f};
    chain(c, b, zz, 5, 0, 0.2f);
    b = body(c, bdef(SP_BODY_STATIC, 3.0f, 3.0f));
    box(c, b, 0.5f, 0.5f, 0.0f, 0.0f, 0.0f, 0.0f, 0.2f, SP_SHAPE_SENSOR);
    int32_t dyn = body(c, bdef(SP_BODY_DYNAMIC, -3.0f, 4.0f));
    circle(c, dyn, 0.6f, 0.0f, 0.0f, 1.0f, 0.2f, 0.0f, 0);
    float sq[8];
    regular(sq, 4, 0.7f, 0.0f, 0.0f, 0.785398163f);
    int32_t dyn2 = body(c, bdef(SP_BODY_DYNAMIC, -6.0f, -4.0f, 0.4f));
    poly(c, dyn2, sq, 4, 1.0f, 0.2f, 0);

    rec_rays(c, 0, 0.0f, 0.0f, 32);
    rec_rays(c, 100, 4.0f, -2.0f, 12);
    int32_t out[32], n;
    const sp_aabb_query aq[3] = {{-1, -1, 1, 1, 0xFFFF, 0}, {2, -7, 10, -3, 0xFFFF, 0}, {-10, -10, 10, 10, 0xFFFF, SP_QUERY_SKIP_SENSORS}};
    for (int32_t i = 0; i < 3; ++i) { n = sp_query_aabb(c.W, &aq[i], out, 32); if (ok(c, n)) rec_list(c, 0, i, out, n); }
    const float pts[6][2] = {{5.0f, 0.5f}, {0.0f, 5.0f}, {-5.0f, 0.0f}, {3.2f, 3.2f}, {0.0f, -4.5f}, {-3.0f, 4.0f}};
    for (int32_t i = 0; i < 6; ++i) { n = sp_query_point(c.W, pts[i], 0xFFFF, out, 32); if (ok(c, n)) rec_list(c, 1, i, out, n); }
    sp_shape_def pc = sdef(SP_SHAPE_CIRCLE, 0.0f, 0.0f, 0);
    pc.radius = 0.5f;
    sp_shape_def pbx = sdef(SP_SHAPE_BOX, 0.0f, 0.0f, 0);
    pbx.hx = 1.5f; pbx.hy = 0.2f;
    sp_shape_def pe = sdef(SP_SHAPE_EDGE, 0.0f, 0.0f, 0);
    pe.vertex_count = 4;
    const float pexy[8] = {0, 0, -3.0f, 0.0f, 3.0f, 0.0f, 0, 0};
    sp_shape_def pp = sdef(SP_SHAPE_POLYGON, 0.0f, 0.0f, 0);
    pp.vertex_count = 3;
    const float ptri[6] = {-0.5f, -0.5f, 0.5f, -0.5f, 0.0f, 0.6f};
    for (int32_t i = 0; i < 16; ++i) {
        float a = (float)i * (TWO_PI / 16.0f);
        float xf[3] = {5.5f * sp_cosf(a), 5.5f * sp_sinf(a), a};
        n = sp_overlap_shape(c.W, &pc, nullptr, xf, 0xFFFF, 0, out, 32); if (ok(c, n)) rec_list(c, 2, i, out, n);
        n = sp_overlap_shape(c.W, &pbx, nullptr, xf, 0xFFFF, 0, out, 32); if (ok(c, n)) rec_list(c, 3, i, out, n);
        n = sp_overlap_shape(c.W, &pe, pexy, xf, 0xFFFF, 0, out, 32); if (ok(c, n)) rec_list(c, 4, i, out, n);
        n = sp_overlap_shape(c.W, &pp, ptri, xf, 0xFFFF, SP_QUERY_SKIP_SENSORS, out, 32); if (ok(c, n)) rec_list(c, 5, i, out, n);
    }
    set_vel(c, dyn, 2.0f, -1.0f, 0.0f);
    set_vel(c, dyn2, 1.0f, 1.5f, 3.0f);
    for (int32_t s = 0; s < 10; ++s) { step(c); rec_events(c); }
    rec_poses(c);
    rec_rays(c, 200, -1.0f, 1.0f, 16);
}

/* 7: the crowd bench: a 64-gon arena, 48 MovePosition units (circles) chasing / orbiting a moving player, 24 kinematic
 *    sensor hitboxes following units one step late, 40 static grass sensors. Also the Mac/Vita timing scenario. */
void sc_crowd(Ctx &c)
{
    world(c, 0.0f, 0.0f);
    float ring[128];
    regular(ring, 64, 12.0f, 0.0f, 0.0f, 0.0f);
    int32_t ga = body(c, bdef(SP_BODY_STATIC, 0.0f, 0.0f));
    chain(c, ga, ring, 64, SP_SHAPE_LOOP, 0.4f);
    for (int32_t i = 0; i < 40; ++i) {
        float x = -7.5f + 2.5f * (float)(i % 8) + 0.3f * (float)(i / 8), y = -6.0f + 3.0f * (float)(i / 8);
        int32_t g = body(c, bdef(SP_BODY_STATIC, x, y));
        box(c, g, 0.3f, 0.3f, 0.0f, 0.0f, 0.0f, 0.0f, 0.4f, SP_SHAPE_SENSOR);
    }
    sp_body_def pd = bdef(SP_BODY_DYNAMIC, 0.0f, 0.0f);
    pd.flags |= SP_BODY_FIXED_ROTATION;
    int32_t player = body(c, pd);
    circle(c, player, 0.3f, 0.0f, 0.0f, 1.0f, 0.4f, 0.0f, 0);
    int32_t units[48], hits[24];
    for (int32_t i = 0; i < 48; ++i) {
        float a = (float)i * (TWO_PI / 48.0f);
        sp_body_def d = bdef(SP_BODY_DYNAMIC, 7.0f * sp_cosf(a), 7.0f * sp_sinf(a));
        d.flags |= SP_BODY_FIXED_ROTATION;
        units[i] = body(c, d);
        circle(c, units[i], 0.3f + 0.05f * (float)(i % 4), 0.0f, 0.0f, 1.0f, 0.4f, 0.0f, 0);
    }
    for (int32_t i = 0; i < 24; ++i) {
        sp_pose u = pose(c, units[i]);
        hits[i] = body(c, bdef(SP_BODY_KINEMATIC, u.x, u.y));
        circle(c, hits[i], 0.5f, 0.0f, 0.0f, 0.0f, 0.4f, 0.0f, SP_SHAPE_SENSOR);
    }
    EvSum es;
    rec_poses(c);
    for (int32_t s = 1; s <= 500; ++s) {
        float t = (float)s;
        float px = 4.0f * sp_cosf(t * 0.01f), py = 4.0f * sp_sinf(t * 0.013f);
        move_to(c, player, px, py, 0.0f, false);
        for (int32_t i = 0; i < 24; ++i) {               /* hitboxes follow their unit's pose of the previous step */
            sp_pose u = pose(c, units[i]);
            move_to(c, hits[i], u.x, u.y, 0.0f, false);
        }
        for (int32_t i = 0; i < 48; ++i) {
            sp_pose u = pose(c, units[i]);
            float tx, ty;
            if (i % 2 == 0) { tx = px; ty = py; }
            else {
                float a = (float)i * (TWO_PI / 48.0f) + t * 0.004f;
                tx = 6.0f * sp_cosf(a); ty = 6.0f * sp_sinf(a);
            }
            float dx = tx - u.x, dy = ty - u.y, len = sqrtf(dx * dx + dy * dy);
            float vx = 0.0f, vy = 0.0f;
            if (len > 0.01f) { vx = dx / len * 3.0f; vy = dy / len * 3.0f; }
            move_to(c, units[i], u.x + vx * DT, u.y + vy * DT, 0.0f, false);
        }
        step(c);
        sum_events(c, es);
        if (s % 50 == 0) { rec_evsum(c, es); rec_stats(c); }
        if (s % 100 == 0) rec_poses(c);
    }
}

struct Scenario { const char *name; void (*fn)(Ctx &); };
const Scenario k_scenarios[] = {
    {"fall_boxes", sc_fall_boxes},
    {"stack_boxes", sc_stack_boxes},
    {"chain_bowl", sc_chain_bowl},
    {"kinematic_sweep", sc_kinematic_sweep},
    {"topdown_slide", sc_topdown_slide},
    {"sensor_sequence", sc_sensor_sequence},
    {"raycasts", sc_raycasts},
    {"crowd", sc_crowd},
};
const int32_t k_n = (int32_t)(sizeof(k_scenarios) / sizeof(k_scenarios[0]));

} /* namespace */

extern "C" {

SP_API int32_t sp_t1_count(void) { return k_n; }

SP_API const char *sp_t1_name(int32_t i) { return (i >= 0 && i < k_n) ? k_scenarios[i].name : nullptr; }

SP_API int32_t sp_t1_run(int32_t i, sp_t1_record_fn fn, void *user, sp_t1_result *out)
{
    if (i < 0 || i >= k_n) return SP_ERR_PARAM;
    Ctx c;
    memset(&c, 0, sizeof c);
    c.fn = fn; c.user = user;
    c.h = 0xcbf29ce484222325ULL;
    k_scenarios[i].fn(c);
    if (c.W > 0) ok(c, sp_world_destroy(c.W));
    if (out) {
        out->hash_lo = (uint32_t)c.h;
        out->hash_hi = (uint32_t)(c.h >> 32);
        out->records = c.records; out->words = c.words; out->steps = c.steps; out->status = c.status;
    }
    return c.status;
}

SP_API int32_t sp_t1_expected(int32_t i, sp_t1_result *out)
{
    const char *name = sp_t1_name(i);
    if (!name) return 0;
    for (int32_t k = 0; k < SP_T1_GOLDEN_N; ++k) {
        if (strcmp(k_t1_golden[k].name, name) != 0) continue;
        if (out) {
            out->hash_lo = k_t1_golden[k].hash_lo; out->hash_hi = k_t1_golden[k].hash_hi;
            out->records = k_t1_golden[k].records; out->words = k_t1_golden[k].words; out->steps = k_t1_golden[k].steps;
            out->status = 0;
        }
        return 1;
    }
    return 0;
}

SP_API const char *sp_t1_golden_info(void) { return SP_T1_GOLDEN_INFO; }

#define T1_FN(f) { #f, (void *)f }
static const struct { const char *name; void *fn; } k_t1_procs[] = {
    T1_FN(sp_t1_count), T1_FN(sp_t1_name), T1_FN(sp_t1_run), T1_FN(sp_t1_expected), T1_FN(sp_t1_golden_info), T1_FN(sp_s0_run),
};

SP_API void *shepphys_t1_proc(const char *name)
{
    for (size_t i = 0; name && i < sizeof(k_t1_procs) / sizeof(k_t1_procs[0]); ++i)
        if (!strcmp(k_t1_procs[i].name, name)) return k_t1_procs[i].fn;
    return nullptr;
}

} /* extern "C" */
