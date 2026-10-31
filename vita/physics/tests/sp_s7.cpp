/* sp_s7.cpp -- physics Stage 7 item 2 (kit #9, the Law of the Vita part 2) self-test of libshepphys: worlds built to exercise patch P3's
 * forked phases (box2d/PATCHES.md row P3; shim/sp_p3.cpp), through the C API only. Ours; synthetic scenes (no game data). Reported the T1
 * way (records folded into an FNV-1a 64 hash). The serial run is the reference: `t1_runner s7` and `t1_runner s7 --workers 1|2|3 [--jcheck]`
 * must print the same hash, and the Vita archive on QEMU's Cortex-A9 (qemu/harness.c, serial: no threads there) the same as the Mac.
 *
 * Records (every 20 steps of each world: the step, the bodies, the awake ones, the contacts, the touching ones, a pose hash -- every
 * body's position, angle, velocities and flags, bit for bit -- and an event hash -- kinds, flags, shapes, steps of everything drained):
 *   CRWD  world A, top-down like the game (no gravity; P1's trigger phase, kinematic triggers, contact serials, sleep): 72 dynamic circles
 *         and boxes thrown at each other in six clusters inside a chain-loop arena on the ground body (one static body shared by every
 *         island touching the walls) with four static posts (more shared static bodies), three kinematic movers sweeping through the
 *         crowd (islands through kinematic bodies), sensors on statics, dynamics and kinematics. At step 120 a filter change (the
 *         filtering flag in Collide), at 160 a shape destroyed and at 200 made again; the clusters come to rest and fall asleep
 *         (the static bodies' sleep replay)
 *   PILE  world B: a 15 x 15 pile of boxes under gravity in a static box -- one island far beyond a worker's 100 KiB stack allocator
 *         (patch P3 gives it to the calling thread) -- and loose boxes beside it (small islands)
 *   NEGZ  world C: two separate pairs of boxes resting on a static post at x = -0.0 and on a static body made with a velocity (neither is a
 *         fixed point of the island solve: patch P3 hands that step's islands back to the stock Solve)
 *   STCK  world D: stock flags (no P1): sensors evaluated inside Collide (the narrowphase fork's b2TestOverlap path)
 * status: 0, or -700 - n for the first failed expectation n. */
#include <stdint.h>
#include <string.h>

#include "shepphys.h"
#include "sp_t1.h"

namespace {

struct S7 {
    sp_t1_record_fn fn; void *user;
    uint64_t h; int32_t records, words, status, steps;
};

void rec(S7 *s, uint32_t tag, int32_t a, int32_t b, const uint32_t *w, int32_t n, uint32_t fmask)
{
    uint32_t hd[4] = {tag, (uint32_t)a, (uint32_t)b, (uint32_t)n};
    const uint8_t *p = (const uint8_t *)hd;
    for (int i = 0; i < 16; ++i) s->h = (s->h ^ p[i]) * 1099511628211ull;
    p = (const uint8_t *)w;
    for (int32_t i = 0; i < n * 4; ++i) s->h = (s->h ^ p[i]) * 1099511628211ull;
    s->records++; s->words += n;
    if (s->fn) s->fn(s->user, tag, a, b, w, n, fmask);
}
void fail(S7 *s, int32_t code) { if (s->status == 0) s->status = -700 - code; }
uint64_t fnv(uint64_t h, const void *d, int32_t n)
{
    const uint8_t *p = (const uint8_t *)d;
    for (int32_t i = 0; i < n; ++i) h = (h ^ p[i]) * 1099511628211ull;
    return h;
}

struct Lcg { uint32_t x; float next() { x = x * 1664525u + 1013904223u; return (float)(x >> 8) * (1.0f / 16777216.0f); } };

enum { MAXB = 320 };
sp_pose g_poses[MAXB];
sp_event g_ev[512];

/* one world's checkpoint: counts + the pose hash + the event hash since the last one */
struct Track { uint64_t evh; int32_t evn; };
void drain(int32_t w, Track *t)
{
    for (;;) {
        int32_t n = sp_events(w, g_ev, 512);
        if (n <= 0) break;
        t->evh = fnv(t->evh, g_ev, n * (int32_t)sizeof(sp_event));
        t->evn += n;
        if (n < 512) break;
    }
}
void checkpoint(S7 *s, uint32_t tag, int32_t w, int32_t step, const sp_step_stats *st, Track *t)
{
    drain(w, t);
    int32_t n = sp_poses(w, g_poses, MAXB);
    uint64_t ph = fnv(14695981039346656037ull, g_poses, (n > 0 ? n : 0) * (int32_t)sizeof(sp_pose));
    uint32_t r[9] = {(uint32_t)n, (uint32_t)st->awake_bodies, (uint32_t)st->contacts, (uint32_t)st->touching, (uint32_t)ph, (uint32_t)(ph >> 32),
                     (uint32_t)t->evh, (uint32_t)(t->evh >> 32), (uint32_t)t->evn};
    rec(s, tag, step, 0, r, 9, 0);
    t->evh = 14695981039346656037ull; t->evn = 0;
}

sp_body_def body(int32_t type, float x, float y)
{
    sp_body_def bd; memset(&bd, 0, sizeof bd);
    bd.type = type; bd.flags = SP_BODY_ACTIVE | SP_BODY_ALLOW_SLEEP | SP_BODY_AWAKE; bd.x = x; bd.y = y; bd.gravity_scale = 1.0f;
    return bd;
}
sp_shape_def shape(int32_t kind, float a, float b)
{
    sp_shape_def sd; memset(&sd, 0, sizeof sd);
    sd.kind = kind; sd.density = 1.0f; sd.friction = 0.4f; sd.category_bits = 1; sd.mask_bits = 0xFFFF;
    if (kind == SP_SHAPE_CIRCLE) sd.radius = a; else { sd.hx = a; sd.hy = b; }
    return sd;
}
int32_t step(int32_t w, sp_step_stats *st)
{
    sp_step_def d = {0.02f, 8, 3, 1};
    return sp_world_step(w, &d, st);
}

/* ---------------- CRWD ---------------- */
void crowd(S7 *s)
{
    sp_world_def wd = {0.0f, 0.0f, SP_WORLD_ALLOW_SLEEP | SP_WORLD_WARM_START | SP_WORLD_TRIGGER_PHASE | SP_WORLD_KINEMATIC_TRIGGERS | SP_WORLD_CONTACT_SERIALS, 0};
    int32_t w = sp_world_create(&wd);
    if (w <= 0) { fail(s, 1); return; }
    Lcg rng = {7u};
    /* the arena: a chain loop on one static body, four static posts, two static sensors */
    sp_body_def gb = body(SP_BODY_STATIC, 0.0f, 0.0f);
    int32_t ground = sp_body_create(w, &gb);
    float loop[16] = {-12, -9, 12, -9, 12, 9, -12, 9};
    sp_shape_def chain = shape(SP_SHAPE_CHAIN, 0, 0); chain.flags = SP_SHAPE_LOOP; chain.vertex_count = 4; chain.density = 0.0f;
    if (sp_shape_create(w, ground, &chain, loop) <= 0) fail(s, 2);
    for (int32_t i = 0; i < 4; ++i) {
        sp_body_def pb = body(SP_BODY_STATIC, (i & 1) ? 5.0f : -5.0f, (i & 2) ? 3.5f : -3.5f);
        int32_t post = sp_body_create(w, &pb);
        sp_shape_def ps = shape(SP_SHAPE_BOX, 0.6f, 0.6f); ps.density = 0.0f;
        sp_shape_create(w, post, &ps, nullptr);
        sp_shape_def sens = shape(SP_SHAPE_CIRCLE, 1.6f, 0); sens.flags = SP_SHAPE_SENSOR; sens.density = 0.0f;
        sp_shape_create(w, post, &sens, nullptr);
    }
    /* six clusters of twelve, thrown toward the middle */
    int32_t dyn[72], dynShape[72], nd = 0;
    for (int32_t c = 0; c < 6; ++c) {
        float cx = -8.0f + 3.2f * (float)c, cy = (c & 1) ? 5.5f : -5.5f;
        for (int32_t k = 0; k < 12; ++k) {
            sp_body_def bd = body(SP_BODY_DYNAMIC, cx + 0.9f * (float)(k % 3) - 0.9f, cy + 0.9f * (float)(k / 3) * ((c & 1) ? -1.0f : 1.0f) * 0.5f);
            bd.vx = (rng.next() - 0.5f) * 3.0f - cx * 0.35f;
            bd.vy = -cy * (0.3f + 0.2f * rng.next());
            bd.w = (rng.next() - 0.5f) * 2.0f;
            bd.linear_damping = 0.8f; bd.angular_damping = 0.8f; bd.user = nd;
            int32_t b = sp_body_create(w, &bd);
            sp_shape_def sd = (k & 1) ? shape(SP_SHAPE_BOX, 0.32f, 0.26f) : shape(SP_SHAPE_CIRCLE, 0.34f, 0);
            dyn[nd] = b; dynShape[nd] = sp_shape_create(w, b, &sd, nullptr);
            if (k % 4 == 0) { sp_shape_def ss = shape(SP_SHAPE_CIRCLE, 0.9f, 0); ss.flags = SP_SHAPE_SENSOR; ss.density = 0.0f; sp_shape_create(w, b, &ss, nullptr); }
            nd++;
        }
    }
    /* three kinematic movers with sensors, sweeping */
    int32_t kin[3];
    for (int32_t i = 0; i < 3; ++i) {
        sp_body_def kb = body(SP_BODY_KINEMATIC, -10.0f + 10.0f * (float)i, -7.5f + 7.5f * (float)i);
        kb.vx = (i == 1) ? -1.5f : 1.5f; kb.vy = (i == 0) ? 1.0f : -1.0f;
        kin[i] = sp_body_create(w, &kb);
        sp_shape_def ks = shape(SP_SHAPE_BOX, 1.2f, 0.25f); ks.density = 0.0f;
        sp_shape_create(w, kin[i], &ks, nullptr);
        sp_shape_def kss = shape(SP_SHAPE_CIRCLE, 1.5f, 0); kss.flags = SP_SHAPE_SENSOR; kss.density = 0.0f;
        sp_shape_create(w, kin[i], &kss, nullptr);
    }
    Track t = {14695981039346656037ull, 0};
    sp_step_stats st; memset(&st, 0, sizeof st);
    for (int32_t i = 1; i <= 400; ++i) {
        if (i == 60 || i == 100) for (int32_t k = 0; k < 3; ++k) { float v[3] = {k == 1 ? 1.5f : -1.5f, k == 0 ? -1.0f : 1.0f, 0.0f}; sp_body_set_velocity(w, kin[k], v); }
        if (i == 140) for (int32_t k = 0; k < 3; ++k) { float v[3] = {0, 0, 0}; sp_body_set_velocity(w, kin[k], v); }   /* the movers stop: rest, sleep */
        if (i == 120) { uint32_t f[3] = {1, 0xFFFD, 0}; for (int32_t k = 0; k < nd; k += 5) sp_shape_set_filter(w, dynShape[k], f); }   /* the filtering flag */
        if (i == 160) for (int32_t k = 3; k < nd; k += 9) { sp_shape_destroy(w, dynShape[k]); dynShape[k] = 0; }
        if (i == 200) for (int32_t k = 3; k < nd; k += 9) { sp_shape_def sd = shape(SP_SHAPE_CIRCLE, 0.3f, 0); dynShape[k] = sp_shape_create(w, dyn[k], &sd, nullptr); }
        if (i == 330) { float v[3] = {6.0f, 2.0f, 0.0f}; sp_body_set_velocity(w, dyn[17], v); }   /* a sleeper woken through its island */
        if (step(w, &st) != SP_OK) fail(s, 3);
        s->steps++;
        if (i % 20 == 0) checkpoint(s, SP_T1_TAG('C', 'R', 'W', 'D'), w, i, &st, &t);
    }
    if (st.awake_bodies >= st.bodies) fail(s, 4);   /* (something fell asleep) */
    sp_world_destroy(w);
}

/* ---------------- PILE ---------------- */
void pile(S7 *s)
{
    sp_world_def wd = {0.0f, -10.0f, SP_WORLD_ALLOW_SLEEP | SP_WORLD_WARM_START | SP_WORLD_CONTACT_SERIALS, 0};
    int32_t w = sp_world_create(&wd);
    if (w <= 0) { fail(s, 10); return; }
    sp_body_def gb = body(SP_BODY_STATIC, 0.0f, 0.0f);
    int32_t ground = sp_body_create(w, &gb);
    sp_shape_def wall = shape(SP_SHAPE_BOX, 9.0f, 0.5f); wall.density = 0.0f;
    sp_shape_create(w, ground, &wall, nullptr);
    sp_shape_def side = shape(SP_SHAPE_BOX, 0.5f, 10.0f); side.density = 0.0f; side.cx = -8.0f; side.cy = 10.0f;
    sp_shape_create(w, ground, &side, nullptr);
    side.cx = 8.0f; sp_shape_create(w, ground, &side, nullptr);
    for (int32_t y = 0; y < 15; ++y)
        for (int32_t x = 0; x < 15; ++x) {
            sp_body_def bd = body(SP_BODY_DYNAMIC, -7.0f + 1.0f * (float)x + ((y & 1) ? 0.25f : 0.0f), 1.0f + 0.98f * (float)y);
            int32_t b = sp_body_create(w, &bd);
            sp_shape_def sd = shape(SP_SHAPE_BOX, 0.48f, 0.48f);
            sp_shape_create(w, b, &sd, nullptr);
        }
    /* loose boxes on a ledge beside the pile (their own islands, on their own static body) */
    sp_body_def lb = body(SP_BODY_STATIC, 14.0f, 2.0f);
    int32_t ledge = sp_body_create(w, &lb);
    sp_shape_def ls = shape(SP_SHAPE_BOX, 4.0f, 0.25f); ls.density = 0.0f;
    sp_shape_create(w, ledge, &ls, nullptr);
    for (int32_t i = 0; i < 6; ++i) {
        sp_body_def bd = body(SP_BODY_DYNAMIC, 11.0f + 1.2f * (float)i, 3.0f + 0.3f * (float)i);
        int32_t b = sp_body_create(w, &bd);
        sp_shape_def sd = shape(SP_SHAPE_BOX, 0.4f, 0.3f);
        sp_shape_create(w, b, &sd, nullptr);
    }
    Track t = {14695981039346656037ull, 0};
    sp_step_stats st; memset(&st, 0, sizeof st);
    int32_t maxTouch = 0;
    for (int32_t i = 1; i <= 160; ++i) {
        if (step(w, &st) != SP_OK) fail(s, 11);
        s->steps++;
        if (st.touching > maxTouch) maxTouch = st.touching;
        if (i % 20 == 0) checkpoint(s, SP_T1_TAG('P', 'I', 'L', 'E'), w, i, &st, &t);
    }
    if (maxTouch < 380) fail(s, 12);   /* (one island with more contacts than a worker's 100 KiB holds) */
    sp_world_destroy(w);
}

/* ---------------- NEGZ ---------------- */
void negz(S7 *s)
{
    sp_world_def wd = {0.0f, -10.0f, SP_WORLD_ALLOW_SLEEP | SP_WORLD_WARM_START, 0};
    int32_t w = sp_world_create(&wd);
    if (w <= 0) { fail(s, 20); return; }
    sp_body_def pb = body(SP_BODY_STATIC, -0.0f, 0.0f);   /* x = -0.0: not a fixed point of the island solve (c + h * 0 = +0) */
    int32_t post = sp_body_create(w, &pb);
    sp_shape_def ps = shape(SP_SHAPE_BOX, 3.0f, 0.5f); ps.density = 0.0f;
    sp_shape_create(w, post, &ps, nullptr);
    sp_body_def mb = body(SP_BODY_STATIC, 10.0f, 0.0f);   /* a static body made with a velocity: the stock island solve moves it */
    mb.vx = 0.25f;
    int32_t mover = sp_body_create(w, &mb);
    sp_shape_create(w, mover, &ps, nullptr);
    for (int32_t i = 0; i < 4; ++i) {
        sp_body_def bd = body(SP_BODY_DYNAMIC, (i < 2 ? -1.5f : 8.5f) + 3.0f * (float)(i & 1), 1.2f);
        int32_t b = sp_body_create(w, &bd);
        sp_shape_def sd = shape(SP_SHAPE_BOX, 0.5f, 0.5f);
        sp_shape_create(w, b, &sd, nullptr);
    }
    Track t = {14695981039346656037ull, 0};
    sp_step_stats st; memset(&st, 0, sizeof st);
    for (int32_t i = 1; i <= 120; ++i) {
        if (step(w, &st) != SP_OK) fail(s, 21);
        s->steps++;
        if (i % 20 == 0) checkpoint(s, SP_T1_TAG('N', 'E', 'G', 'Z'), w, i, &st, &t);
    }
    sp_world_destroy(w);
}

/* ---------------- WAKE (kit #9 review fix) ----------------
 * A script wakes ONE body (a velocity) that rests on bodies still asleep, and patch P3 then hands the step back to the stock Solve: scene A
 * through the island count (three seeds -- the stack's top and a stacked pair -- in two islands: with --jmin 3 the islands are below the
 * threshold), scene B through a static body that is not a fixed point of the island solve (its ground teleported to angle -0.0 in the same
 * step: the solve's a + h * 0 makes it +0; any threshold -- a static's velocity would not do: a sleeping island's SetAwake(false) zeroes
 * it, and -0 positions never reach m_sweep.c, which is p + R * 0). Each scene is a stack of three boxes made top first, so the bottom box comes first in Box2D's
 * body list: a hand-back that leaves the lower boxes awake makes the stock Solve start that island from the bottom box instead of the top
 * one -- the contacts top / middle and middle / bottom then solve in the other order (Gauss-Seidel: other bits). The review found SolveP3's
 * search did exactly that (its SetAwake(true) ran before the hand-back decision): with the old code, workers + --jmin 3 or any workers run
 * gave another hash than the serial run. */
void wakeup(S7 *s)
{
    sp_world_def wd = {0.0f, -10.0f, SP_WORLD_ALLOW_SLEEP | SP_WORLD_WARM_START, 0};
    int32_t w = sp_world_create(&wd);
    if (w <= 0) { fail(s, 40); return; }
    int32_t top[2], pair[2] = {0, 0}, groundB = 0;
    for (int32_t sc = 0; sc < 2; ++sc) {
        float gx = sc ? 2.0f : 20.0f;
        sp_body_def gb = body(SP_BODY_STATIC, gx, 0.0f);
        int32_t ground = sp_body_create(w, &gb);
        if (sc) groundB = ground;
        sp_shape_def gs = shape(SP_SHAPE_BOX, 8.0f, 0.5f); gs.density = 0.0f;
        sp_shape_create(w, ground, &gs, nullptr);
        for (int32_t k = 2; k >= 0; --k) {   /* the stack, top first (k = 2 .. 0) */
            sp_body_def bb = body(SP_BODY_DYNAMIC, gx + 0.15f * (float)k, 1.0f + 1.01f * (float)k);
            int32_t b = sp_body_create(w, &bb);
            sp_shape_def bs = shape(SP_SHAPE_BOX, 0.9f - 0.15f * (float)k, 0.5f);
            sp_shape_create(w, b, &bs, nullptr);
            if (k == 2) top[sc] = b;
        }
        if (sc == 0)
            for (int32_t k = 0; k < 2; ++k) {   /* scene A: a stacked pair (the third seed, one island with the second) */
                sp_body_def db = body(SP_BODY_DYNAMIC, gx + 5.0f, 1.0f + 1.02f * (float)k);
                pair[k] = sp_body_create(w, &db);
                sp_shape_def ds = shape(SP_SHAPE_BOX, 0.5f, 0.5f);
                sp_shape_create(w, pair[k], &ds, nullptr);
            }
    }
    Track t = {14695981039346656037ull, 0};
    sp_step_stats st; memset(&st, 0, sizeof st);
    int32_t awake130 = -1, awake200 = -1;
    for (int32_t i = 1; i <= 300; ++i) {
        if (i == 130) {   /* scene A: the stack's top and the pair woken, the stack's lower boxes still asleep */
            awake130 = st.awake_bodies;
            float v[3] = {0.8f, 0.0f, 0.0f}; sp_body_set_velocity(w, top[0], v);
            float u[3] = {-0.5f, 0.0f, 0.0f}; sp_body_set_velocity(w, pair[0], u); sp_body_set_velocity(w, pair[1], u);
        }
        if (i == 200) {   /* scene B: its ground teleported to angle -0 (no wake), the stack's top woken, its lower boxes still asleep */
            awake200 = st.awake_bodies;
            float xa[3] = {2.0f, 0.0f, -0.0f}; sp_body_set_transform(w, groundB, xa);
            float v[3] = {-0.8f, 0.0f, 0.0f}; sp_body_set_velocity(w, top[1], v);
        }
        if (step(w, &st) != SP_OK) fail(s, 41);
        s->steps++;
        if (i % 20 == 0 || i == 131 || i == 201) checkpoint(s, SP_T1_TAG('W', 'A', 'K', 'E'), w, i, &st, &t);
    }
    if (awake130 != 0 || awake200 > 5) fail(s, 42);   /* (every body asleep when scene A is woken; scene B's asleep at 200) */
    sp_world_destroy(w);
}

/* ---------------- STCK ---------------- */
void stock(S7 *s)
{
    sp_world_def wd = {0.0f, 0.0f, SP_WORLD_ALLOW_SLEEP | SP_WORLD_WARM_START, 0};
    int32_t w = sp_world_create(&wd);
    if (w <= 0) { fail(s, 30); return; }
    Lcg rng = {11u};
    sp_body_def gb = body(SP_BODY_STATIC, 0.0f, 0.0f);
    int32_t ground = sp_body_create(w, &gb);
    for (int32_t i = 0; i < 12; ++i) {
        sp_shape_def ss = shape(SP_SHAPE_BOX, 0.8f, 0.8f); ss.flags = SP_SHAPE_SENSOR; ss.density = 0.0f;
        ss.cx = -6.0f + 1.1f * (float)i; ss.cy = (i & 1) ? 1.0f : -1.0f;
        sp_shape_create(w, ground, &ss, nullptr);
    }
    for (int32_t i = 0; i < 30; ++i) {
        sp_body_def bd = body(SP_BODY_DYNAMIC, -7.0f + 0.5f * (float)i, 3.0f * (rng.next() - 0.5f));
        bd.vx = 2.0f * (rng.next() - 0.5f); bd.vy = 2.0f * (rng.next() - 0.5f);
        int32_t b = sp_body_create(w, &bd);
        sp_shape_def sd = shape(SP_SHAPE_CIRCLE, 0.2f, 0);
        sp_shape_create(w, b, &sd, nullptr);
    }
    Track t = {14695981039346656037ull, 0};
    sp_step_stats st; memset(&st, 0, sizeof st);
    for (int32_t i = 1; i <= 100; ++i) {
        if (step(w, &st) != SP_OK) fail(s, 31);
        s->steps++;
        if (i % 20 == 0) checkpoint(s, SP_T1_TAG('S', 'T', 'C', 'K'), w, i, &st, &t);
    }
    sp_world_destroy(w);
}

} /* namespace */

extern "C" SP_API int32_t sp_s7_run(sp_t1_record_fn fn, void *user, sp_t1_result *out)
{
    S7 s;
    memset(&s, 0, sizeof s);
    s.fn = fn; s.user = user; s.h = 14695981039346656037ull;
    crowd(&s);
    pile(&s);
    negz(&s);
    stock(&s);
    wakeup(&s);   /* (kit #9 review fix; last, so the four worlds above keep their records) */
    if (out) { out->hash_lo = (uint32_t)s.h; out->hash_hi = (uint32_t)(s.h >> 32); out->records = s.records; out->words = s.words; out->steps = s.steps; out->status = s.status; }
    return s.status;
}
