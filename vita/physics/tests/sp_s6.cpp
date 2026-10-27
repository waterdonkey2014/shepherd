/* sp_s6.cpp -- physics Stage 6 self-test of libshepphys (the 3D island-ground ray: physics-plan-v2 section 4 "Stage 6"), through the C API
 * only. Ours; every mesh here is synthetic (no game data). Reported the T1 way (records folded into an FNV-1a 64 hash): the Mac
 * (tests/t1_runner `s6`) and the Vita archive on QEMU's Cortex-A9 (qemu/harness.c) must print the same hash. The engine's `physcheck unit6`
 * repeats the contract through the real Unity API, `physcheck room3d` on the game's own scenes.
 *
 * Records:
 *   PLAN  Unity's Plane shape (11 x 11 vertices, 200 triangles, +y front) at scale (100, 1, 100) under a -90 degree X rotation whose w is
 *         0.7071068 (a front facing -z, the GroundCollisionPlane's shape): +z rays from z = -10 on a 7 x 7 grid; the hit z bits under both
 *         H-RAYPOINT variants (bary / ray), the distance, the triangle, u, v. Expected: every ray hits, |z| <= 1e-4 (the plane's own 2^-23
 *         tilt: w^2 - 1/2 is not 0 in float), the normal (0, 0, -1) to 1e-6
 *   FLAT  a quad at z = 3 facing -z (identity pose): +z rays hit at z = 3 within 2 ulps
 *   CULL  -z rays at the same quad's back: no hit; with SP_R3F_BACKFACES: a hit whose normal is the triangle's own (0, 0, -1)
 *   PRIM  a box (2 x 2 x 2, rotated 30 degrees about z), a sphere (r 1 x scale 2), a capsule (r 0.5, height 3, Y): +z rays at their
 *         centres hit at the analytic distance (the box face z = -1 at t 9, the sphere at t 8, the capsule's side at t 9.5); rays starting
 *         inside report nothing (Unity's rule)
 *   FILT  a layer mask meeting no collider's layer: 0 hits and the early-out counted; a trigger box only with SP_R3F_TRIGGERS
 *   MODE  two quads at z = 2 (made first) and z = 1: closest = z 1; ALL in collider order (2 then 1), ALL + SORT (1 then 2); ANY: one hit
 *   NEGS  the plane under scale (-100, 1, 100): still hit from -z (vertex-space culling), u / v swapped (PhysX's negative-determinant rule)
 *   FAST  a scene of the tilted plane, six bumpy heightfields (rotated, scaled) and four cubes: 20,000 +z rays (and 2,000 slanted ones
 *         through the general path only) -- the +z fast path, the general path and the brute force agree bit for bit on every hit
 *   STAT  the scene's counters (fast / general rays, early-outs, the grids: at most one per mesh collider, none rebuilt while nothing changes)
 * status: 0, or -600 - n for the first failed expectation n. */
#include <float.h>
#include <math.h>
#include <stdint.h>
#include <string.h>

#include "shepphys.h"
#include "sp_t1.h"

namespace {

struct S6 {
    sp_t1_record_fn fn; void *user;
    uint64_t h; int32_t records, words, status, steps;
};

void rec(S6 *s, uint32_t tag, int32_t a, int32_t b, const uint32_t *w, int32_t n, uint32_t fmask)
{
    uint32_t hd[4] = {tag, (uint32_t)a, (uint32_t)b, (uint32_t)n};
    const uint8_t *p = (const uint8_t *)hd;
    for (int i = 0; i < 16; ++i) s->h = (s->h ^ p[i]) * 1099511628211ull;
    p = (const uint8_t *)w;
    for (int32_t i = 0; i < n * 4; ++i) s->h = (s->h ^ p[i]) * 1099511628211ull;
    s->records++; s->words += n;
    if (s->fn) s->fn(s->user, tag, a, b, w, n, fmask);
}
void fail(S6 *s, int32_t code) { if (s->status == 0) s->status = -600 - code; }
uint32_t fbits(float f) { uint32_t u; memcpy(&u, &f, 4); return u; }
float fabs_(float f) { return f < 0 ? -f : f; }
void recf(S6 *s, uint32_t tag, int32_t a, int32_t b, const float *f, int32_t n)
{
    uint32_t w[SP_T1_MAX_WORDS];
    for (int32_t i = 0; i < n && i < SP_T1_MAX_WORDS; ++i) w[i] = fbits(f[i]);
    rec(s, tag, a, b, w, n, (1u << n) - 1u);
}
/* a hit as words: point, normal, distance, u, v (floats) + face, user */
void rechit(S6 *s, uint32_t tag, int32_t a, int32_t n, const sp_ray3d_hit *h)
{
    uint32_t w[11];
    const float f[9] = {h->px, h->py, h->pz, h->nx, h->ny, h->nz, h->distance, h->u, h->v};
    for (int i = 0; i < 9; ++i) w[i] = fbits(f[i]);
    w[9] = (uint32_t)h->face; w[10] = (uint32_t)h->user;
    rec(s, tag, a, n, w, 11, 0x1FFu);
}

/* ---- synthetic meshes ---- */
/* Unity's Plane shape: (n + 1)^2 vertices over x, z in [-5, 5] (y 0), 2 n^2 triangles with the +y front */
int32_t plane_mesh(int32_t scene, int32_t n)
{
    static float v[3 * 21 * 21];
    static int32_t t[3 * 2 * 20 * 20];
    int32_t k = 0;
    for (int32_t j = 0; j <= n; ++j)
        for (int32_t i = 0; i <= n; ++i) { v[k++] = -5.0f + 10.0f * (float)i / (float)n; v[k++] = 0.0f; v[k++] = -5.0f + 10.0f * (float)j / (float)n; }
    k = 0;
    for (int32_t j = 0; j < n; ++j)
        for (int32_t i = 0; i < n; ++i) {
            int32_t a = j * (n + 1) + i, b = a + (n + 1), c = a + 1, d = b + 1;
            t[k++] = a; t[k++] = b; t[k++] = c;   /* cross(b - a, c - a) = +y */
            t[k++] = b; t[k++] = d; t[k++] = c;
        }
    sp_mesh3d_def d = {(n + 1) * (n + 1), 2 * n * n, 0, 0};
    return sp_mesh3d_create(scene, &d, v, t);
}
/* a quad in the xy plane at z (front -z: cross = (0, 0, -1)), half size hs */
int32_t quad_mesh(int32_t scene, float z, float hs)
{
    const float v[12] = {-hs, -hs, z, hs, -hs, z, -hs, hs, z, hs, hs, z};
    const int32_t t[6] = {0, 2, 1, 1, 2, 3};   /* (0, 2, 1): cross((-hs..hs in y), (+x)) = (0, 0, -1) */
    sp_mesh3d_def d = {4, 2, 0, 0};
    return sp_mesh3d_create(scene, &d, v, t);
}
uint32_t g_lcg;
float rnd() { g_lcg = g_lcg * 1664525u + 1013904223u; return (float)(g_lcg >> 8) * (1.0f / 16777216.0f); }
/* a 12 x 12 heightfield in [-1, 1]^2 (y up: Unity-style; the collider rotates it), heights in [0, amp] */
int32_t bumpy_mesh(int32_t scene, float amp)
{
    static float v[3 * 13 * 13];
    static int32_t t[3 * 2 * 12 * 12];
    int32_t k = 0;
    for (int32_t j = 0; j <= 12; ++j)
        for (int32_t i = 0; i <= 12; ++i) { v[k++] = -1.0f + (float)i / 6.0f; v[k++] = rnd() * amp; v[k++] = -1.0f + (float)j / 6.0f; }
    k = 0;
    for (int32_t j = 0; j < 12; ++j)
        for (int32_t i = 0; i < 12; ++i) {
            int32_t a = j * 13 + i, b = a + 13, c = a + 1, d = b + 1;
            t[k++] = a; t[k++] = b; t[k++] = c;
            t[k++] = b; t[k++] = d; t[k++] = c;
        }
    sp_mesh3d_def d = {13 * 13, 2 * 12 * 12, 0, 0};
    return sp_mesh3d_create(scene, &d, v, t);
}
/* Unity's Cube shape: 24 vertices (4 per face), 12 triangles, outward fronts, [-0.5, 0.5]^3 */
int32_t cube_mesh(int32_t scene)
{
    static float v[72];
    static int32_t t[36];
    /* per face: normal axis a, sign s; the two in-plane axes u, w chosen so that cross(u, w) = s * e_a */
    int32_t k = 0, m = 0;
    for (int32_t a = 0; a < 3; ++a)
        for (int32_t sgn = -1; sgn <= 1; sgn += 2) {
            int32_t u = (a + 1) % 3, w = (a + 2) % 3;
            if (sgn < 0) { int32_t x = u; u = w; w = x; }
            int32_t base = k / 3;
            for (int32_t c = 0; c < 4; ++c) {
                float p[3]; p[a] = 0.5f * (float)sgn; p[u] = (c & 1) ? 0.5f : -0.5f; p[w] = (c & 2) ? 0.5f : -0.5f;
                v[k++] = p[0]; v[k++] = p[1]; v[k++] = p[2];
            }
            /* corners 0 (-,-) 1 (+,-) 2 (-,+) 3 (+,+) in (u, w): (0, 1, 2) has cross(e_u, e_w) = the outward normal */
            t[m++] = base; t[m++] = base + 1; t[m++] = base + 2;
            t[m++] = base + 1; t[m++] = base + 3; t[m++] = base + 2;
        }
    sp_mesh3d_def d = {24, 12, 0, 0};
    return sp_mesh3d_create(scene, &d, v, t);
}

sp_col3d_def cdef(int32_t kind, int32_t layer, int32_t user)
{
    sp_col3d_def d;
    memset(&d, 0, sizeof d);
    d.kind = kind; d.layer = layer; d.user = user; d.direction = 1;
    d.qw = 1.0f; d.sx = 1.0f; d.sy = 1.0f; d.sz = 1.0f;
    return d;
}
float f_of(uint32_t u) { float f; memcpy(&f, &u, 4); return f; }
/* the -90 degree X rotation with w = x-magnitude 0.7071068 (0x3F3504F4) */
void tilt(sp_col3d_def *d) { d->qx = -f_of(0x3F3504F4u); d->qy = 0.0f; d->qz = 0.0f; d->qw = f_of(0x3F3504F4u); }
sp_ray3d_query zray(float x, float y, float z, int32_t flags)
{
    sp_ray3d_query q;
    memset(&q, 0, sizeof q);
    q.ox = x; q.oy = y; q.oz = z; q.dz = 1.0f; q.max_distance = FLT_MAX; q.layer_mask = 0xFFFFFFFFu; q.flags = flags;
    return q;
}
bool same(const sp_ray3d_hit &a, const sp_ray3d_hit &b)   /* the first 44 bytes (all but the collider handle); no memcmp (bare-metal QEMU) */
{
    const uint8_t *x = (const uint8_t *)&a, *y = (const uint8_t *)&b;
    for (int i = 0; i < 44; ++i) if (x[i] != y[i]) return false;
    return true;
}
/* a value truncated to an integer, one at a time (inlined, GCC pairs x and y into a NEON vcvt: check_syms forbids NEON f32) */
__attribute__((noinline)) float trunc_scalar(float v) { return (float)(int32_t)v; }

const uint32_t T_PLAN = SP_T1_TAG('P', 'L', 'A', 'N'), T_FLAT = SP_T1_TAG('F', 'L', 'A', 'T'), T_CULL = SP_T1_TAG('C', 'U', 'L', 'L'),
               T_PRIM = SP_T1_TAG('P', 'R', 'I', 'M'), T_FILT = SP_T1_TAG('F', 'I', 'L', 'T'), T_MODE = SP_T1_TAG('M', 'O', 'D', 'E'),
               T_NEGS = SP_T1_TAG('N', 'E', 'G', 'S'), T_FAST = SP_T1_TAG('F', 'A', 'S', 'T'), T_STAT = SP_T1_TAG('S', 'T', 'A', 'T');

void t_plane(S6 *s)
{
    int32_t sc = sp_scene3d_create(0), m = plane_mesh(sc, 10);
    sp_col3d_def d = cdef(SP_C3_MESH, 17, 101);
    tilt(&d); d.mesh = m; d.sx = 100.0f; d.sy = 1.0f; d.sz = 100.0f;
    int32_t c = sp_col3d_create(sc, &d);
    if (sc <= 0 || m <= 0 || c <= 0) fail(s, 1);
    int32_t hits = 0;
    float worst = 0.0f;
    for (int32_t j = 0; j < 7; ++j)
        for (int32_t i = 0; i < 7; ++i) {
            float x = -400.0f + 133.33333f * (float)i, y = -400.0f + 133.33333f * (float)j;
            if (i == 3) x = 0.0f;
            if (j == 3) y = 0.0f;
            sp_ray3d_query q = zray(x, y, -10.0f, 0);
            sp_ray3d_hit h, hr;
            int32_t n = sp_ray3d(sc, &q, &h, 1);
            q.flags = SP_R3F_POINT_RAY;
            int32_t nr = sp_ray3d(sc, &q, &hr, 1);
            if (n == 1 && nr == 1) {
                ++hits;
                if (fabs_(h.pz) > worst) worst = fabs_(h.pz);
                if (fabs_(h.nz + 1.0f) > 1e-6f || fabs_(h.nx) > 1e-6f || fabs_(h.ny) > 1e-6f) fail(s, 3);
                rechit(s, T_PLAN, i, j, &h);
                const float f[3] = {hr.px, hr.py, hr.pz};
                recf(s, T_PLAN, i + 100, j, f, 3);
            }
        }
    if (hits != 49) fail(s, 2);
    if (!(worst <= 1e-4f)) fail(s, 4);
    const float w[1] = {worst};
    recf(s, T_PLAN, -1, hits, w, 1);
    sp_scene3d_destroy(sc);
}

void t_flat(S6 *s)
{
    int32_t sc = sp_scene3d_create(0), m = quad_mesh(sc, 3.0f, 2.0f);
    sp_col3d_def d = cdef(SP_C3_MESH, 0, 7);
    d.mesh = m;
    sp_col3d_create(sc, &d);
    float worst = 0.0f;
    for (int32_t i = 0; i < 16; ++i) {
        float x = -1.9f + 0.25f * (float)i, y = 1.7f - 0.21f * (float)i;
        sp_ray3d_query q = zray(x, y, -10.0f, 0);
        sp_ray3d_hit h;
        if (sp_ray3d(sc, &q, &h, 1) != 1) { fail(s, 10); continue; }
        if (fabs_(h.pz - 3.0f) > worst) worst = fabs_(h.pz - 3.0f);
        rechit(s, T_FLAT, i, 1, &h);
    }
    if (!(worst <= 4.8e-7f)) fail(s, 11);
    /* CULL: from the back (z = +10, -z) nothing; with backfaces a hit with the triangle's own normal */
    sp_ray3d_query q = zray(0.3f, 0.2f, 10.0f, 0);
    q.dz = -1.0f;
    sp_ray3d_hit h;
    int32_t n0 = sp_ray3d(sc, &q, &h, 1);
    q.flags = SP_R3F_BACKFACES;
    int32_t n1 = sp_ray3d(sc, &q, &h, 1);
    if (n0 != 0 || n1 != 1 || fabs_(h.nz + 1.0f) > 1e-6f || fabs_(h.pz - 3.0f) > 4.8e-7f) fail(s, 12);
    rechit(s, T_CULL, n0, n1, &h);
    sp_scene3d_destroy(sc);
}

void t_prims(S6 *s)
{
    int32_t sc = sp_scene3d_create(0);
    sp_col3d_def b = cdef(SP_C3_BOX, 0, 21);
    b.px = 5.0f; b.py = 5.0f; b.ex = 2.0f; b.ey = 2.0f; b.ez = 2.0f;
    b.qz = sp_sinf(0.26179939f); b.qw = sp_cosf(0.26179939f);   /* 30 degrees about z */
    sp_col3d_def sp = cdef(SP_C3_SPHERE, 0, 22);
    sp.px = -5.0f; sp.py = 5.0f; sp.ex = 1.0f; sp.sx = 2.0f; sp.sy = 2.0f; sp.sz = 2.0f;
    sp_col3d_def cp = cdef(SP_C3_CAPSULE, 0, 23);
    cp.py = -5.0f; cp.ex = 0.5f; cp.ey = 3.0f; cp.direction = 1;
    sp_col3d_create(sc, &b); sp_col3d_create(sc, &sp); sp_col3d_create(sc, &cp);
    const float px[3] = {5.0f, -5.0f, 0.0f}, py[3] = {5.0f, 5.0f, -5.0f}, want[3] = {9.0f, 8.0f, 9.5f};
    for (int32_t k = 0; k < 3; ++k) {
        sp_ray3d_query q = zray(px[k], py[k], -10.0f, 0);
        sp_ray3d_hit h;
        int32_t n = sp_ray3d(sc, &q, &h, 1);
        if (n != 1 || fabs_(h.distance - want[k]) > 1e-4f || h.user != 21 + k || h.face != -1) fail(s, 20 + k);
        if (n == 1) rechit(s, T_PRIM, k, n, &h);
        /* from inside: nothing (Unity's Physics.Raycast) */
        sp_ray3d_query qi = zray(px[k], py[k], 0.0f, 0);
        int32_t ni = sp_ray3d(sc, &qi, &h, 1);
        if (ni != 0) fail(s, 25 + k);
        rec(s, T_PRIM, k + 10, ni, nullptr, 0, 0);
    }
    /* a slanted ray at the box's side face: the normal is the face's (rotated) */
    sp_ray3d_query q = zray(5.0f - 10.0f, 5.0f, 0.3f, 0);
    q.dx = 1.0f; q.dz = 0.0f;
    sp_ray3d_hit h;
    int32_t n = sp_ray3d(sc, &q, &h, 1);
    if (n != 1 || h.user != 21) fail(s, 28);
    if (n == 1) rechit(s, T_PRIM, 20, n, &h);
    sp_scene3d_destroy(sc);
}

void t_filters(S6 *s)
{
    int32_t sc = sp_scene3d_create(0), m = quad_mesh(sc, 0.0f, 3.0f);
    sp_col3d_def d = cdef(SP_C3_MESH, 17, 31);
    d.mesh = m;
    sp_col3d_create(sc, &d);
    sp_col3d_def t = cdef(SP_C3_BOX, 4, 32);
    t.flags = SP_C3F_TRIGGER; t.pz = -2.0f; t.ex = 1.0f; t.ey = 1.0f; t.ez = 1.0f;
    sp_col3d_create(sc, &t);
    int32_t st0[16], st1[16];
    sp_scene3d_stats(sc, st0);
    sp_ray3d_query q = zray(0.1f, 0.1f, -10.0f, 0);
    q.layer_mask = 1u << 23;   /* Island: no collider on it */
    sp_ray3d_hit h;
    int32_t n0 = sp_ray3d(sc, &q, &h, 1);
    sp_scene3d_stats(sc, st1);
    if (n0 != 0 || st1[4] != st0[4] + 1) fail(s, 30);
    q.layer_mask = 0xFFFFFFFFu;
    int32_t n1 = sp_ray3d(sc, &q, &h, 1);    /* the trigger is skipped: the quad */
    int32_t u1 = n1 == 1 ? h.user : 0;
    q.flags = SP_R3F_TRIGGERS;
    int32_t n2 = sp_ray3d(sc, &q, &h, 1);    /* the trigger box (z -2.5 .. -1.5) comes first */
    int32_t u2 = n2 == 1 ? h.user : 0;
    if (n1 != 1 || u1 != 31 || n2 != 1 || u2 != 32) fail(s, 31);
    const uint32_t w[5] = {(uint32_t)n0, (uint32_t)n1, (uint32_t)u1, (uint32_t)n2, (uint32_t)u2};
    rec(s, T_FILT, st1[4] - st0[4], 0, w, 5, 0);
    sp_scene3d_destroy(sc);
}

void t_modes(S6 *s)
{
    int32_t sc = sp_scene3d_create(0), m2 = quad_mesh(sc, 2.0f, 3.0f), m1 = quad_mesh(sc, 1.0f, 3.0f);
    sp_col3d_def d2 = cdef(SP_C3_MESH, 0, 42), d1 = cdef(SP_C3_MESH, 0, 41);
    d2.mesh = m2; d1.mesh = m1;
    sp_col3d_create(sc, &d2); sp_col3d_create(sc, &d1);
    sp_ray3d_query q = zray(0.5f, -0.25f, -10.0f, 0);
    sp_ray3d_hit h[4];
    int32_t nc = sp_ray3d(sc, &q, h, 4);
    int32_t uc = h[0].user;
    q.flags = SP_R3F_ALL;
    int32_t na = sp_ray3d(sc, &q, h, 4);
    int32_t ua0 = h[0].user, ua1 = h[1].user;
    q.flags = SP_R3F_ALL | SP_R3F_SORT;
    int32_t ns = sp_ray3d(sc, &q, h, 4);
    int32_t us0 = h[0].user, us1 = h[1].user;
    q.flags = SP_R3F_ALL | SP_R3F_SORT;
    int32_t n1 = sp_ray3d(sc, &q, h, 1);   /* a buffer of 1: the total found, the closest written */
    int32_t u1 = h[0].user;
    q.flags = SP_R3F_ANY;
    int32_t ny = sp_ray3d(sc, &q, h, 1);
    if (nc != 1 || uc != 41 || na != 2 || ua0 != 42 || ua1 != 41 || ns != 2 || us0 != 41 || us1 != 42 || n1 != 2 || u1 != 41 || ny != 1) fail(s, 40);
    const uint32_t w[11] = {(uint32_t)nc, (uint32_t)uc, (uint32_t)na, (uint32_t)ua0, (uint32_t)ua1, (uint32_t)ns, (uint32_t)us0, (uint32_t)us1,
                            (uint32_t)n1, (uint32_t)u1, (uint32_t)ny};
    rec(s, T_MODE, 0, 0, w, 11, 0);
    sp_scene3d_destroy(sc);
}

void t_negative(S6 *s)
{
    int32_t sc = sp_scene3d_create(0), m = plane_mesh(sc, 10);
    sp_col3d_def d = cdef(SP_C3_MESH, 0, 51);
    tilt(&d); d.mesh = m; d.sx = -100.0f; d.sy = 1.0f; d.sz = 100.0f;
    sp_col3d_create(sc, &d);
    sp_ray3d_query q = zray(37.5f, -12.25f, -10.0f, 0);
    sp_ray3d_hit h;
    int32_t n = sp_ray3d(sc, &q, &h, 1);
    if (n != 1 || fabs_(h.pz) > 1e-4f) fail(s, 50);
    if (n == 1) rechit(s, T_NEGS, n, 0, &h);
    sp_scene3d_destroy(sc);
}

void t_fast(S6 *s)
{
    int32_t sc = sp_scene3d_create(0);
    int32_t pm = plane_mesh(sc, 10);
    sp_col3d_def d = cdef(SP_C3_MESH, 17, 60);
    tilt(&d); d.mesh = pm; d.sx = 100.0f; d.sy = 1.0f; d.sz = 100.0f;
    sp_col3d_create(sc, &d);
    g_lcg = 12345u;
    for (int32_t k = 0; k < 6; ++k) {   /* bumpy grounds: y up locally, rotated to face -z, then turned about z */
        int32_t bm = bumpy_mesh(sc, 0.4f + 0.2f * (float)k);
        sp_col3d_def b = cdef(SP_C3_MESH, k % 2 == 0 ? 0 : 8, 61 + k);
        float a = 0.3f * (float)k, ca = sp_cosf(a * 0.5f), sa = sp_sinf(a * 0.5f);
        /* q = qz(a) * qx(-90) */
        float qx = -f_of(0x3F3504F4u), qw = f_of(0x3F3504F4u);
        b.qx = ca * qx; b.qy = sa * qx; b.qz = sa * qw; b.qw = ca * qw;
        b.mesh = bm; b.px = -30.0f + 12.0f * (float)k; b.py = 5.0f - 3.0f * (float)k; b.pz = -0.5f * (float)k;
        b.sx = 4.0f + (float)k; b.sy = 1.0f + 0.25f * (float)k; b.sz = 3.0f + 0.5f * (float)k;
        sp_col3d_create(sc, &b);
    }
    int32_t cm = cube_mesh(sc);
    for (int32_t k = 0; k < 4; ++k) {
        sp_col3d_def c = cdef(SP_C3_MESH, 0, 70 + k);
        float a = 0.5f * (float)k + 0.1f;
        c.qz = sp_sinf(a * 0.5f); c.qw = sp_cosf(a * 0.5f);
        c.mesh = cm; c.px = 10.0f * (float)k - 15.0f; c.py = -10.0f; c.pz = -1.0f; c.sx = 3.0f; c.sy = 2.0f + (float)k; c.sz = 1.5f;
        sp_col3d_create(sc, &c);
    }
    int32_t mism = 0, hits = 0, firstbad = -1;
    for (int32_t i = 0; i < 20000; ++i) {
        float x = -60.0f + 120.0f * rnd(), y = -40.0f + 80.0f * rnd();
        if (i % 97 == 0) { x = trunc_scalar(x); y = trunc_scalar(y); }   /* lattice points: triangle edges and vertices */
        sp_ray3d_query q = zray(x, y, -10.0f, 0);
        sp_ray3d_hit hf, hg, hb;
        int32_t nf = sp_ray3d(sc, &q, &hf, 1);
        q.flags = SP_R3F_GENERAL;
        int32_t ng = sp_ray3d(sc, &q, &hg, 1);
        q.flags = SP_R3F_BRUTE;
        int32_t nb = sp_ray3d(sc, &q, &hb, 1);
        bool ok = nf == ng && ng == nb && (nf == 0 || (same(hf, hg) && same(hg, hb)));
        if (!ok) { ++mism; if (firstbad < 0) firstbad = i; }
        if (nf == 1) {
            ++hits;
            s->h = (s->h ^ fbits(hf.pz)) * 1099511628211ull;
            s->h = (s->h ^ (uint32_t)hf.face) * 1099511628211ull;
            s->h = (s->h ^ (uint32_t)hf.user) * 1099511628211ull;
        }
    }
    int32_t smism = 0;
    for (int32_t i = 0; i < 2000; ++i) {   /* slanted rays: the general path against the brute force */
        sp_ray3d_query q = zray(-60.0f + 120.0f * rnd(), -40.0f + 80.0f * rnd(), -10.0f, SP_R3F_GENERAL);
        q.dx = rnd() - 0.5f; q.dy = rnd() - 0.5f;
        float l = sqrtf(q.dx * q.dx + q.dy * q.dy + 1.0f);
        q.dx /= l; q.dy /= l; q.dz = 1.0f / l;
        sp_ray3d_hit hg, hb;
        int32_t ng = sp_ray3d(sc, &q, &hg, 1);
        q.flags = SP_R3F_BRUTE;
        int32_t nb = sp_ray3d(sc, &q, &hb, 1);
        if (!(ng == nb && (ng == 0 || same(hg, hb)))) ++smism;
    }
    if (mism != 0 || smism != 0) fail(s, 60);
    if (hits < 2000) fail(s, 61);
    const uint32_t w[4] = {(uint32_t)mism, (uint32_t)smism, (uint32_t)hits, (uint32_t)firstbad};
    rec(s, T_FAST, 20000, 2000, w, 4, 0);
    int32_t st[16], st2[16];
    sp_scene3d_stats(sc, st);
    for (int32_t i = 0; i < 500; ++i) { sp_ray3d_query q = zray(-60.0f + 120.0f * rnd(), -40.0f + 80.0f * rnd(), -10.0f, 0); sp_ray3d_hit h; sp_ray3d(sc, &q, &h, 1); }
    sp_scene3d_stats(sc, st2);
    /* every +z ray on the fast path; at most one grid per mesh collider (11), none rebuilt while nothing changes */
    if (st[5] != 20000 || st[9] < 1 || st[9] > 11 || st2[9] != st[9]) fail(s, 62);
    rec(s, T_STAT, 0, 0, (const uint32_t *)st, 16, 0);
    s->steps += 44000;
    sp_scene3d_destroy(sc);
}

} /* namespace */

extern "C" SP_API int32_t sp_s6_run(sp_t1_record_fn fn, void *user, sp_t1_result *out)
{
    S6 s;
    memset(&s, 0, sizeof s);
    s.fn = fn; s.user = user; s.h = 14695981039346656037ull;
    bool was_up = sp_pool_base() != nullptr;
    if (!was_up) { sp_init_def d = {0, 0, 0, 0}; sp_init(&d); }
    int32_t sizes[SP_ABI_N_SIZES];
    float probe = 0;
    int32_t v = sp_abi(sizes, SP_ABI_N_SIZES, &probe);
    rec(&s, SP_T1_TAG('A', 'B', 'I', ' '), v, SP_ABI_N_SIZES, (const uint32_t *)sizes, SP_ABI_N_SIZES, 0);
    if (v != SHEPPHYS_ABI_VERSION) fail(&s, 0);
    t_plane(&s);
    t_flat(&s);
    t_prims(&s);
    t_filters(&s);
    t_modes(&s);
    t_negative(&s);
    t_fast(&s);
    if (!was_up) sp_shutdown();
    if (out) {
        out->hash_lo = (uint32_t)s.h; out->hash_hi = (uint32_t)(s.h >> 32);
        out->records = s.records; out->words = s.words; out->steps = s.steps; out->status = s.status;
    }
    return s.status;
}
