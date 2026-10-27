/* sp_unity.cpp -- physics Stage 1: the Unity layer of libshepphys (physics-plan-v2 sections 1.2 items 2-3, 1.3, 1.4, 1.5, 1.6, 3, 4
 * "Stage 1"). Ours, not Box2D; zero Box2D alterations.
 *
 * What it adds on top of the stage-0a core (shepphys.cpp):
 *  - Unity worlds: a Box2D world with the Unity contact filter (the layer matrix) and a static ground body for the colliders that
 *    have no Rigidbody2D (their shapes live in world space on it).
 *  - Unity bodies with a MIRROR record in the pool (sp_mirror, 128 B): the managed getters (rb.position, rotation, velocity) read it
 *    and the setters (MovePosition, position / rotation / velocity sets) write it, with 0 native calls; the flags say what is pending.
 *  - Shapes built from a collider's own parameters under its collider-to-body affine: circles (radius x the larger axis scale),
 *    boxes and polygons (transformed; re-wound under a negative determinant, C16; concave or > 8 vertices decomposed into convex
 *    pieces), capsules as rounded boxes, edges as open chains with Unity's adjacent points as ghosts, composite outlines as chain
 *    loops (two-sided, H-CHAIN's default).
 *  - The transform sync (H-SYNC), the pending mirror writes (teleport + wake = H-WAKETP, velocity sets), the movement states
 *    (MovePosition / MoveRotation -> one step's velocity (t - p) / h, gravity and drag 0 for that step = H-MOVEDRAG off, the pre-move
 *    velocity restored = H-MOVEVEL restore), the step, the history P[k-2..k], the write-back flags (C6: last written pose + forced
 *    writes), slot C's lerp (H-INTERP) and slot A's restore (H-EARLY), the render-pose snapshot and evaluation (section 1.6, C2).
 * Every float a body's pose depends on is computed here (section 1.10). Every change to Box2D goes through the traced C API, so a
 * trace of an engine run (SHEPHERD_PHYS_TRACE) replays with tests/t1_runner `replay` and no Unity layer at all.
 * Not thread-safe: USER_0 only (plan section 3 "Threads"). Iteration is always in body-slot order (deterministic). */
#include "shepphys.h"
#include "sp_internal.h"

#include <Box2D/Box2D.h>
#include <new>
#include <stdlib.h>
#include <string.h>

static_assert(sizeof(sp_uworld_def) == 144, "sp_uworld_def");
static_assert(sizeof(sp_mirror) == 160, "sp_mirror");   /* Stage 3: + forces, inverse mass, pre-step velocity (128 before) */
static_assert(sizeof(sp_ucontact) == 48, "sp_ucontact");
static_assert(sizeof(sp_ubody_def) == 56, "sp_ubody_def");
static_assert(sizeof(sp_ushape_def) == 120, "sp_ushape_def");
static_assert(sizeof(sp_usync) == 24, "sp_usync");
static_assert(sizeof(sp_ustep_def) == 24, "sp_ustep_def");
static_assert(sizeof(sp_ustep_out) == 32, "sp_ustep_out");
static_assert(sizeof(sp_render_def) == 16, "sp_render_def");
static_assert(sizeof(sp_rpose) == 24, "sp_rpose");

namespace {

template <typename T> bool ugrow(T *&v, int32_t &cap, int32_t need)
{
    if (need <= cap) return true;
    int32_t nc = cap ? cap : 16;
    while (nc < need) nc *= 2;
    T *nv = (T *)realloc(v, (size_t)nc * sizeof(T));
    if (!nv) return false;
    memset(nv + cap, 0, (size_t)(nc - cap) * sizeof(T));
    v = nv;
    cap = nc;
    return true;
}

inline int32_t slot_of(int32_t h) { return (int32_t)(h & 0xFFFF) - 1; }

struct UBody {
    int32_t body;           /* 0 = free */
    int32_t mirror;         /* pool offset */
    int32_t type, flags, interp;
    float mass, ldrag, adrag, gscale;
};

struct Snap {               /* one body's render history at the end of a logic tick */
    int32_t body, serial, mode, valid;
    float hx[3], hy[3], ha[3];
    float vx, vy, w;
};

struct Moved { int32_t slot; float v0x, v0y, w0; uint8_t ld, ad, gs; };
/* Stage 3: a Dynamic body with a frozen position axis (its pre-step coordinate), and a body whose damping H-DAMP mapped for the step */
struct Frozen { int32_t slot; float x, y; };
struct Damped { int32_t slot; uint8_t ld, ad; };

struct UWorld {
    int32_t world = 0, ground = 0;
    UBody *b = nullptr;
    int32_t nb = 0, capb = 0;
    Moved *mv = nullptr;
    int32_t capmv = 0;
    Snap *snap = nullptr;
    int32_t nsnap = 0, capsnap = 0;
    int32_t render_clamped = 0;
    Frozen *fz = nullptr; int32_t capfz = 0;    /* Stage 3 */
    Damped *dm = nullptr; int32_t capdm = 0;
};

enum { MAX_UWORLDS = 8 };
UWorld *g_uw[MAX_UWORLDS];   /* by world slot; zero-initialised (no static constructors) */

UWorld *uw(int32_t world)
{
    int32_t s = slot_of(world);
    if (world <= 0 || s < 0 || s >= MAX_UWORLDS) return nullptr;
    UWorld *U = g_uw[s];
    return (U && U->world == world) ? U : nullptr;
}

UBody *ub(UWorld *U, int32_t body)
{
    int32_t s = slot_of(body);
    if (body <= 0 || s < 0 || s >= U->nb) return nullptr;
    return U->b[s].body == body ? &U->b[s] : nullptr;
}

inline sp_mirror *M(int32_t off) { return (sp_mirror *)((unsigned char *)sp_pool_base() + off); }

inline uint32_t fb(float f) { uint32_t u; memcpy(&u, &f, 4); return u; }
inline float fabs_(float f) { return f < 0.0f ? -f : f; }

/* ---------------- geometry ---------------- */
struct Aff { float m0, m1, m2, m3, m4, m5; };   /* x' = m0 x + m1 y + m2, y' = m3 x + m4 y + m5 */

/* body-space affine = R(-theta) * body2 * rel, theta = atan2(body2[2], body2[0]) (the body transform's own angle) */
Aff body_affine(const float *b2x2, const float *rel)
{
    float th = sp_atan2f(b2x2[2], b2x2[0]);
    float c = sp_cosf(th), s = sp_sinf(th);
    float q00 = c * b2x2[0] + s * b2x2[2], q01 = c * b2x2[1] + s * b2x2[3];
    float q10 = -s * b2x2[0] + c * b2x2[2], q11 = -s * b2x2[1] + c * b2x2[3];
    Aff a;
    a.m0 = q00 * rel[0] + q01 * rel[3]; a.m1 = q00 * rel[1] + q01 * rel[4]; a.m2 = q00 * rel[2] + q01 * rel[5];
    a.m3 = q10 * rel[0] + q11 * rel[3]; a.m4 = q10 * rel[1] + q11 * rel[4]; a.m5 = q10 * rel[2] + q11 * rel[5];
    return a;
}
inline void apply(const Aff &a, float x, float y, float &ox, float &oy) { ox = a.m0 * x + a.m1 * y + a.m2; oy = a.m3 * x + a.m4 * y + a.m5; }
inline float det(const Aff &a) { return a.m0 * a.m4 - a.m1 * a.m3; }
inline float axis_scale_max(const Aff &a)
{
    float sx = sqrtf(a.m0 * a.m0 + a.m3 * a.m3), sy = sqrtf(a.m1 * a.m1 + a.m4 * a.m4);
    return sx > sy ? sx : sy;
}

struct Pt { float x, y; };
inline float cross3(const Pt &a, const Pt &b, const Pt &c) { return (b.x - a.x) * (c.y - a.y) - (b.y - a.y) * (c.x - a.x); }

float area2(const Pt *p, int32_t n)
{
    float a = 0.0f;
    for (int32_t i = 0, j = n - 1; i < n; j = i++) a += p[j].x * p[i].y - p[i].x * p[j].y;
    return a;
}

/* welds points closer than Box2D's polygon weld distance and drops collinear ones; returns the new count */
int32_t clean(Pt *p, int32_t n)
{
    const float weld = 0.5f * b2_linearSlop;
    int32_t m = 0;
    for (int32_t i = 0; i < n; ++i) {
        if (m > 0) { float dx = p[i].x - p[m - 1].x, dy = p[i].y - p[m - 1].y; if (dx * dx + dy * dy < weld * weld) continue; }
        p[m++] = p[i];
    }
    while (m > 1) { float dx = p[0].x - p[m - 1].x, dy = p[0].y - p[m - 1].y; if (dx * dx + dy * dy < weld * weld) m--; else break; }
    bool changed = true;
    while (changed && m >= 3) {
        changed = false;
        for (int32_t i = 0; i < m && m >= 3; ++i) {
            const Pt &a = p[(i + m - 1) % m], &b = p[i], &c = p[(i + 1) % m];
            float ex = b.x - a.x, ey = b.y - a.y, fx = c.x - b.x, fy = c.y - b.y;
            float cr = ex * fy - ey * fx;
            float l = sqrtf((ex * ex + ey * ey) * (fx * fx + fy * fy));
            if (fabs_(cr) <= 1e-6f * l && ex * fx + ey * fy > 0.0f) {   /* collinear and going on in the same direction */
                memmove(p + i, p + i + 1, (size_t)(m - i - 1) * sizeof(Pt));
                m--; changed = true; --i;
            }
        }
    }
    return m;
}

bool convex(const Pt *p, int32_t n)
{
    for (int32_t i = 0; i < n; ++i) if (cross3(p[i], p[(i + 1) % n], p[(i + 2) % n]) <= 0.0f) return false;
    return true;
}

bool in_tri(const Pt &a, const Pt &b, const Pt &c, const Pt &q)
{
    return cross3(a, b, q) >= 0.0f && cross3(b, c, q) >= 0.0f && cross3(c, a, q) >= 0.0f;
}

/* ear clipping (CCW input, n >= 3) into triangles of vertex indices; then Hertel-Mehlhorn merging into convex pieces of <= 8.
 * pieces[k * 8 + i] = vertex index, cnt[k] = count. Returns the number of pieces. Deterministic: fixed scan orders. */
int32_t decompose(const Pt *p, int32_t n, int32_t *pieces, int32_t *cnt, int32_t maxp)
{
    int32_t *idx = (int32_t *)malloc((size_t)n * sizeof(int32_t));
    if (!idx) return 0;
    for (int32_t i = 0; i < n; ++i) idx[i] = i;
    int32_t m = n, np = 0;
    while (m > 3 && np < maxp) {
        int32_t ear = -1; float best = -1.0f; int32_t fallback = 0;
        for (int32_t i = 0; i < m; ++i) {
            int32_t ia = idx[(i + m - 1) % m], ib = idx[i], ic = idx[(i + 1) % m];
            float cr = cross3(p[ia], p[ib], p[ic]);
            if (cr > best) { best = cr; fallback = i; }
            if (cr <= 0.0f) continue;
            bool inside = false;
            for (int32_t k = 0; k < m && !inside; ++k) {
                int32_t iq = idx[k];
                if (iq == ia || iq == ib || iq == ic) continue;
                if ((p[iq].x == p[ia].x && p[iq].y == p[ia].y) || (p[iq].x == p[ib].x && p[iq].y == p[ib].y) ||
                    (p[iq].x == p[ic].x && p[iq].y == p[ic].y)) continue;
                inside = in_tri(p[ia], p[ib], p[ic], p[iq]);
            }
            if (!inside) { ear = i; break; }
        }
        if (ear < 0) ear = fallback;   /* numerically stuck: clip the most convex corner */
        int32_t *t = pieces + np * 8;
        t[0] = idx[(ear + m - 1) % m]; t[1] = idx[ear]; t[2] = idx[(ear + 1) % m];
        cnt[np++] = 3;
        memmove(idx + ear, idx + ear + 1, (size_t)(m - ear - 1) * sizeof(int32_t));
        m--;
    }
    if (m == 3 && np < maxp) { int32_t *t = pieces + np * 8; t[0] = idx[0]; t[1] = idx[1]; t[2] = idx[2]; cnt[np++] = 3; }
    free(idx);
    /* merge pieces sharing an edge while the union stays convex and <= 8 vertices */
    bool merged = true;
    while (merged) {
        merged = false;
        for (int32_t a = 0; a < np && !merged; ++a)
            for (int32_t b = a + 1; b < np && !merged; ++b) {
                int32_t *A = pieces + a * 8, *B = pieces + b * 8, na = cnt[a], nb = cnt[b];
                if (na + nb - 2 > 8) continue;
                for (int32_t i = 0; i < na && !merged; ++i) {
                    int32_t u = A[i], v = A[(i + 1) % na];
                    for (int32_t j = 0; j < nb; ++j) {
                        if (B[j] != v || B[(j + 1) % nb] != u) continue;
                        /* A: ... u v ...; B: ... v u ...: the union walks A from v around to u, then B from u around to v */
                        int32_t U[16], nu = 0;
                        for (int32_t k = 0; k < na; ++k) U[nu++] = A[(i + 1 + k) % na];          /* v .. u (all of A) */
                        for (int32_t k = 2; k < nb; ++k) U[nu++] = B[(j + k) % nb];               /* after u in B, up to before v */
                        Pt q[16];
                        for (int32_t k = 0; k < nu; ++k) q[k] = p[U[k]];
                        if (nu <= 8 && convex(q, nu)) {
                            memcpy(A, U, (size_t)nu * sizeof(int32_t)); cnt[a] = nu;
                            memmove(pieces + b * 8, pieces + (b + 1) * 8, (size_t)(np - b - 1) * 8 * sizeof(int32_t));
                            memmove(cnt + b, cnt + b + 1, (size_t)(np - b - 1) * sizeof(int32_t));
                            np--; merged = true;
                        }
                        break;
                    }
                }
            }
    }
    return np;
}

int32_t make_fixture(int32_t world, int32_t body, const sp_ushape_def *d, int32_t kind, int32_t flags, float radius, const float *xy,
                     int32_t n, float cx, float cy)
{
    sp_shape_def s;
    memset(&s, 0, sizeof s);
    s.kind = kind;
    s.flags = flags | ((d->flags & SP_USF_SENSOR) ? SP_SHAPE_SENSOR : 0);
    s.density = d->density; s.friction = d->friction; s.restitution = d->restitution;
    s.radius = radius; s.cx = cx; s.cy = cy;
    s.category_bits = 1; s.mask_bits = 0xFFFF;
    s.vertex_count = n;
    s.user = d->user;
    int32_t h = sp_shape_create(world, body, &s, xy);
    if (h <= 0) return h;
    sp_shape_unity u = {d->layer, d->include_layers, d->exclude_layers, 0};
    sp_shape_set_unity(world, h, &u);
    return h;
}

/* a convex-or-not polygon (body space, any winding) -> fixtures */
int32_t polygon_fixtures(int32_t world, int32_t body, const sp_ushape_def *d, Pt *p, int32_t n, int32_t sflags, float radius,
                         int32_t *out, int32_t max)
{
    n = clean(p, n);
    if (n < 3) return 0;
    float a = area2(p, n);
    if (a < 0.0f) { for (int32_t i = 0, j = n - 1; i < j; ++i, --j) { Pt t = p[i]; p[i] = p[j]; p[j] = t; } a = -a; }
    if (!(a > 2.0e-7f)) return 0;   /* degenerate (b2PolygonShape::Set would turn it into a 2 x 2 box) */
    int32_t made = 0;
    if (n <= b2_maxPolygonVertices && convex(p, n)) {
        float xy[2 * b2_maxPolygonVertices];
        for (int32_t i = 0; i < n; ++i) { xy[2 * i] = p[i].x; xy[2 * i + 1] = p[i].y; }
        int32_t h = make_fixture(world, body, d, SP_SHAPE_POLYGON, sflags, radius, xy, n, 0.0f, 0.0f);
        if (h > 0) { if (made < max && out) out[made] = h; made++; }
        return made;
    }
    int32_t maxp = n;
    int32_t *pieces = (int32_t *)malloc((size_t)maxp * 8 * sizeof(int32_t)), *cnt = (int32_t *)malloc((size_t)maxp * sizeof(int32_t));
    if (!pieces || !cnt) { free(pieces); free(cnt); return SP_ERR_FULL; }
    int32_t np = decompose(p, n, pieces, cnt, maxp);
    for (int32_t k = 0; k < np; ++k) {
        Pt q[8]; int32_t nq = cnt[k];
        for (int32_t i = 0; i < nq; ++i) q[i] = p[pieces[k * 8 + i]];
        if (!(area2(q, nq) > 2.0e-7f)) continue;
        float xy[16];
        for (int32_t i = 0; i < nq; ++i) { xy[2 * i] = q[i].x; xy[2 * i + 1] = q[i].y; }
        int32_t h = make_fixture(world, body, d, SP_SHAPE_POLYGON, sflags, radius, xy, nq, 0.0f, 0.0f);
        if (h > 0) { if (made < max && out) out[made] = h; made++; }
    }
    free(pieces); free(cnt);
    return made;
}

/* Stage 4b: Unity 2022.3's polygon build of one piece of its convex decomposition (shepphys.h SP_USF_UNITY_POLY), the points already in
 * body space. Measured on the E1 oracle (physics-work/e1, scenes Paths4b..Paths4b5; vita/notes/physics-stage4.md section 10): Unity's weld
 * -- a point whose squared distance to a point already kept is below (0.5 b2_linearSlop)^2 is dropped, in order (Box2D 2.4's rule; the
 * vendored 2.3.1 compares the squared distance with 0.5 b2_linearSlop) --, the gift-wrapping hull of b2PolygonShape::Set (Box2D 2.3.1's
 * own loop: the rightmost, then lowest, point first; c < 0 takes the next point, c == 0 the farther), then Unity's removal of collinear
 * hull vertices: in hull order, a vertex whose unit edge vectors (Box2D's Normalize) have a dot product above 0.99999 goes and the vertex
 * that takes its place is tested next, while more than 3 remain. The fixture's own Set then welds with SP_SHAPE_UNITY_WELD (patch P5) and
 * rebuilds the same hull. Fewer than 3 points after the weld or the hull: no fixture. */
int32_t unity_poly_fixture(int32_t world, int32_t body, const sp_ushape_def *d, const float *xy, int32_t n, int32_t *out, int32_t max)
{
    if (!xy || n < 3) return 0;
    if (n > b2_maxPolygonVertices) n = b2_maxPolygonVertices;   /* (Set reads at most b2_maxPolygonVertices points) */
    const float weld = (0.5f * b2_linearSlop) * (0.5f * b2_linearSlop);
    Pt ps[b2_maxPolygonVertices];
    int32_t m = 0;
    for (int32_t i = 0; i < n; ++i) {
        const Pt v = {xy[2 * i], xy[2 * i + 1]};
        bool unique = true;
        for (int32_t j = 0; j < m; ++j) { const float dx = v.x - ps[j].x, dy = v.y - ps[j].y; if (dx * dx + dy * dy < weld) { unique = false; break; } }
        if (unique) ps[m++] = v;
    }
    if (m < 3) return 0;
    int32_t i0 = 0;
    float x0 = ps[0].x;
    for (int32_t i = 1; i < m; ++i) { const float x = ps[i].x; if (x > x0 || (x == x0 && ps[i].y < ps[i0].y)) { i0 = i; x0 = x; } }
    int32_t hull[b2_maxPolygonVertices], hm = 0, ih = i0;
    for (;;) {
        if (hm >= b2_maxPolygonVertices) return 0;   /* (cannot happen for m <= 8 distinct points; a guard) */
        hull[hm] = ih;
        int32_t ie = 0;
        for (int32_t j = 1; j < m; ++j) {
            if (ie == ih) { ie = j; continue; }
            const float rx = ps[ie].x - ps[hull[hm]].x, ry = ps[ie].y - ps[hull[hm]].y;
            const float vx = ps[j].x - ps[hull[hm]].x, vy = ps[j].y - ps[hull[hm]].y;
            const float c = rx * vy - ry * vx;
            if (c < 0.0f) ie = j;
            if (c == 0.0f && vx * vx + vy * vy > rx * rx + ry * ry) ie = j;
        }
        ++hm;
        ih = ie;
        if (ie == i0) break;
    }
    if (hm < 3) return 0;
    Pt hv[b2_maxPolygonVertices];
    for (int32_t i = 0; i < hm; ++i) hv[i] = ps[hull[i]];
    for (int32_t i = 0; i < hm && hm > 3;) {
        const Pt &a = hv[(i + hm - 1) % hm], &b = hv[i], &c = hv[(i + 1) % hm];
        float ex = b.x - a.x, ey = b.y - a.y, fx = c.x - b.x, fy = c.y - b.y;
        const float le = sqrtf(ex * ex + ey * ey), lf = sqrtf(fx * fx + fy * fy);
        if (le < b2_epsilon || lf < b2_epsilon) { ++i; continue; }
        const float ie = 1.0f / le, jf = 1.0f / lf;
        ex *= ie; ey *= ie; fx *= jf; fy *= jf;
        if (ex * fx + ey * fy > 0.99999f) { memmove(hv + i, hv + i + 1, (size_t)(hm - i - 1) * sizeof(Pt)); --hm; }
        else ++i;
    }
    float v2[2 * b2_maxPolygonVertices];
    for (int32_t i = 0; i < hm; ++i) { v2[2 * i] = hv[i].x; v2[2 * i + 1] = hv[i].y; }
    const float rad = d->radius > 0.0f ? b2_polygonRadius + d->radius : 0.0f;
    const int32_t h = make_fixture(world, body, d, SP_SHAPE_POLYGON, SP_SHAPE_UNITY_WELD | (rad > 0.0f ? SP_SHAPE_RADIUS : 0), rad, v2, hm, 0.0f, 0.0f);
    if (h <= 0) return h;
    if (out && max > 0) out[0] = h;
    return 1;
}

void push_hist(sp_mirror *m, float x, float y, float a, int32_t step)
{
    m->hx[0] = m->hx[1]; m->hy[0] = m->hy[1]; m->ha[0] = m->ha[1]; m->hstep[0] = m->hstep[1];
    m->hx[1] = m->hx[2]; m->hy[1] = m->hy[2]; m->ha[1] = m->ha[2]; m->hstep[1] = m->hstep[2];
    m->hx[2] = x; m->hy[2] = y; m->ha[2] = a; m->hstep[2] = step;
}
void reset_hist(sp_mirror *m, float x, float y, float a)
{
    for (int32_t i = 0; i < 3; ++i) { m->hx[i] = x; m->hy[i] = y; m->ha[i] = a; }
}

int32_t body_flags_of(const sp_ubody_def *d, bool awake)
{
    int32_t f = 0;
    if (awake) f |= SP_BODY_AWAKE;
    if (!(d->flags & SP_UB_NEVER_SLEEP)) f |= SP_BODY_ALLOW_SLEEP;
    if (d->flags & SP_UB_FREEZE_ROTATION) f |= SP_BODY_FIXED_ROTATION;
    if (d->flags & SP_UB_SIMULATED) f |= SP_BODY_ACTIVE;
    return f;
}

int32_t mode_of(const sp_ubody_def *d)
{
    return (d->interpolation & SP_MM_INTERP_MASK) | ((d->type & 3) << SP_MM_TYPE_SHIFT) | ((d->flags & SP_UB_SIMULATED) ? SP_MM_SIMULATED : 0);
}

int32_t apply_pending(UWorld *U, int32_t flags)
{
    int32_t tp = 0;
    for (int32_t s = 0; s < U->nb; ++s) {
        UBody &B = U->b[s];
        if (!B.body) continue;
        sp_mirror *m = M(B.mirror);
        if (!(m->flags & (SP_MF_TELEPORT | SP_MF_SET_VEL))) continue;
        if (m->flags & SP_MF_TELEPORT) {
            float xya[3] = {m->x, m->y, m->angle};
            sp_body_set_transform(U->world, B.body, xya);
            if (!(flags & SP_USTEP_WAKETP_STOCK) && B.type != SP_BODY_STATIC) sp_body_set_flag(U->world, B.body, SP_BODY_AWAKE, 1);
            reset_hist(m, m->x, m->y, m->angle);
            m->flags = (m->flags & ~SP_MF_TELEPORT) | SP_MF_FORCE_WRITE;
            tp++;
        }
        if (m->flags & SP_MF_SET_VEL) {
            float v[3] = {m->vx, m->vy, m->w};
            sp_body_set_velocity(U->world, B.body, v);
            m->flags &= ~SP_MF_SET_VEL;
        }
    }
    return tp;
}

} /* namespace */

extern "C" {

SP_API int32_t sp_uworld_create(const sp_uworld_def *def)
{
    if (!def) return SP_ERR_PARAM;
    sp_world_def wd = {def->gravity_x, def->gravity_y, def->flags ? def->flags : SP_WORLD_DEFAULT_FLAGS, 0};
    int32_t w = sp_world_create(&wd);
    if (w <= 0) return w;
    int32_t s = slot_of(w);
    if (s < 0 || s >= MAX_UWORLDS) { sp_world_destroy(w); return SP_ERR_FULL; }
    if (def->use_matrix) sp_world_set_matrix(w, def->matrix);
    void *mem = calloc(1, sizeof(UWorld));
    if (!mem) { sp_world_destroy(w); return SP_ERR_FULL; }
    UWorld *U = new (mem) UWorld();
    U->world = w;
    sp_body_def gd;
    memset(&gd, 0, sizeof gd);
    gd.type = SP_BODY_STATIC; gd.flags = SP_BODY_ACTIVE | SP_BODY_ALLOW_SLEEP; gd.gravity_scale = 1.0f;
    U->ground = sp_body_create(w, &gd);
    g_uw[s] = U;
    return w;
}

SP_API int32_t sp_uworld_destroy(int32_t world)
{
    UWorld *U = uw(world);
    if (!U) return SP_ERR_HANDLE;
    for (int32_t s = 0; s < U->nb; ++s) if (U->b[s].body && U->b[s].mirror) sp_pool_free(U->b[s].mirror);
    int32_t r = sp_world_destroy(world);
    free(U->b); free(U->mv); free(U->snap); free(U->fz); free(U->dm);
    g_uw[slot_of(world)] = nullptr;
    U->~UWorld();
    free(U);
    return r;
}

SP_API int32_t sp_uworld_ground(int32_t world) { UWorld *U = uw(world); return U ? U->ground : SP_ERR_HANDLE; }

SP_API int32_t sp_ubody_create(int32_t world, const sp_ubody_def *def)
{
    UWorld *U = uw(world);
    if (!U) return SP_ERR_HANDLE;
    if (!def || def->type < SP_BODY_STATIC || def->type > SP_BODY_DYNAMIC) return SP_ERR_PARAM;
    float angle = sp_atan2f(def->m10, def->m00);
    sp_body_def bd;
    memset(&bd, 0, sizeof bd);
    bd.type = def->type;
    bd.flags = body_flags_of(def, !(def->flags & SP_UB_START_ASLEEP));
    bd.x = def->x; bd.y = def->y; bd.angle = angle;
    bd.linear_damping = def->linear_drag; bd.angular_damping = def->angular_drag; bd.gravity_scale = def->gravity_scale;
    bd.user = def->user;
    int32_t off = sp_pool_alloc((int32_t)sizeof(sp_mirror));
    if (off < 0) return off;
    int32_t h = sp_body_create(world, &bd);
    if (h <= 0) { sp_pool_free(off); return h; }
    int32_t s = slot_of(h);
    if (!ugrow(U->b, U->capb, s + 1)) { sp_body_destroy(world, h); sp_pool_free(off); return SP_ERR_FULL; }
    if (s >= U->nb) U->nb = s + 1;
    UBody &B = U->b[s];
    B.body = h; B.mirror = off; B.type = def->type; B.flags = def->flags; B.interp = def->interpolation;
    B.mass = def->mass; B.ldrag = def->linear_drag; B.adrag = def->angular_drag; B.gscale = def->gravity_scale;
    b2Body *b = spi_body(world, h);
    sp_mirror *m = M(off);
    m->x = b->GetPosition().x; m->y = b->GetPosition().y; m->angle = b->GetAngle();
    m->wx = m->x; m->wy = m->y; m->wa = m->angle;
    reset_hist(m, m->x, m->y, m->angle);
    m->hstep[0] = m->hstep[1] = m->hstep[2] = -1;
    m->body = h; m->serial = def->serial; m->mode = mode_of(def); m->user = def->user;
    sp_ubody_mass(world, h);
    return h;
}

SP_API int32_t sp_ubody_destroy(int32_t world, int32_t body)
{
    UWorld *U = uw(world);
    if (!U) return SP_ERR_HANDLE;
    UBody *B = ub(U, body);
    if (!B) return SP_ERR_HANDLE;
    int32_t r = sp_body_destroy(world, body);
    sp_pool_free(B->mirror);
    memset(B, 0, sizeof *B);
    return r;
}

SP_API int32_t sp_ubody_mirror(int32_t world, int32_t body)
{
    UWorld *U = uw(world);
    if (!U) return SP_ERR_HANDLE;
    UBody *B = ub(U, body);
    return B ? B->mirror : SP_ERR_HANDLE;
}

SP_API int32_t sp_ubody_set(int32_t world, int32_t body, const sp_ubody_def *def)
{
    UWorld *U = uw(world);
    if (!U) return SP_ERR_HANDLE;
    UBody *B = ub(U, body);
    if (!B || !def) return B ? SP_ERR_PARAM : SP_ERR_HANDLE;
    if (def->type < SP_BODY_STATIC || def->type > SP_BODY_DYNAMIC) return SP_ERR_PARAM;
    if (def->type != B->type) sp_body_set_type(world, body, def->type);
    int32_t ch = def->flags ^ B->flags;
    if (ch & SP_UB_SIMULATED) sp_body_set_flag(world, body, SP_BODY_ACTIVE, (def->flags & SP_UB_SIMULATED) != 0);
    if (ch & SP_UB_NEVER_SLEEP) sp_body_set_flag(world, body, SP_BODY_ALLOW_SLEEP, !(def->flags & SP_UB_NEVER_SLEEP));
    if (ch & SP_UB_FREEZE_ROTATION) sp_body_set_flag(world, body, SP_BODY_FIXED_ROTATION, (def->flags & SP_UB_FREEZE_ROTATION) != 0);
    if (fb(def->linear_drag) != fb(B->ldrag)) sp_body_set_float(world, body, SP_PROP_LINEAR_DAMPING, &def->linear_drag);
    if (fb(def->angular_drag) != fb(B->adrag)) sp_body_set_float(world, body, SP_PROP_ANGULAR_DAMPING, &def->angular_drag);
    if (fb(def->gravity_scale) != fb(B->gscale)) sp_body_set_float(world, body, SP_PROP_GRAVITY_SCALE, &def->gravity_scale);
    bool mass_ch = def->type != B->type || fb(def->mass) != fb(B->mass) || ((ch & (SP_UB_AUTO_MASS | SP_UB_FREEZE_ROTATION)) != 0);
    B->type = def->type; B->flags = def->flags; B->interp = def->interpolation;
    B->mass = def->mass; B->ldrag = def->linear_drag; B->adrag = def->angular_drag; B->gscale = def->gravity_scale;
    sp_mirror *m = M(B->mirror);
    m->mode = mode_of(def);
    if (mass_ch) sp_ubody_mass(world, body);
    return SP_OK;
}

/* Unity's mass data: useAutoMass -> Box2D's from the densities; else the record's mass, the centre from the densities and the
 * rotational inertia scaled with the mass (Box2D gives 1 kg to a dynamic body without density: then I = 0) */
SP_API int32_t sp_ubody_mass(int32_t world, int32_t body)
{
    UWorld *U = uw(world);
    if (!U) return SP_ERR_HANDLE;
    UBody *B = ub(U, body);
    if (!B) return SP_ERR_HANDLE;
    sp_mirror *mi = M(B->mirror);
    mi->inv_mass = mi->inv_i = 0.0f;   /* Stage 3: the impulse modes' inverse mass (0 unless Dynamic) */
    if (B->type != SP_BODY_DYNAMIC) return SP_OK;
    sp_body_set_mass(world, body, nullptr);
    b2Body *b = spi_body(world, body);
    int32_t r = SP_OK;
    if (!(B->flags & SP_UB_AUTO_MASS)) {
        float md[4];
        sp_body_get_mass(world, body, md);
        float has_density = 0.0f;
        for (b2Fixture *f = b->GetFixtureList(); f; f = f->GetNext()) if (f->GetDensity() > 0.0f) has_density = 1.0f;
        float m[4] = {B->mass, md[1], md[2], has_density > 0.0f && md[0] > 0.0f ? md[3] * (B->mass / md[0]) : 0.0f};
        if (!(m[0] > 0.0f)) m[0] = 1.0f;
        r = sp_body_set_mass(world, body, m);
    }
    float md2[4];
    sp_body_get_mass(world, body, md2);   /* {mass, centre, I about the centre}: Box2D's own after SetMassData / ResetMassData */
    mi->inv_mass = md2[0] > 0.0f ? 1.0f / md2[0] : 0.0f;
    mi->inv_i = (!(B->flags & SP_UB_FREEZE_ROTATION) && md2[3] > 0.0f) ? 1.0f / md2[3] : 0.0f;
    return r;
}

SP_API int32_t sp_ushape_create(int32_t world, int32_t body, const sp_ushape_def *d, const float *xy, int32_t *out, int32_t max)
{
    UWorld *U = uw(world);
    if (!U) return SP_ERR_HANDLE;
    if (!d) return SP_ERR_PARAM;
    if (body != U->ground && !ub(U, body)) return SP_ERR_HANDLE;
    Aff a = body_affine(d->body2, d->rel);
    bool flip = det(a) < 0.0f;
    float smax = axis_scale_max(a);
    switch (d->kind) {
    case SP_US_CIRCLE: {
        if (!(d->radius > 0.0f)) return 0;
        float cx, cy;
        apply(a, d->ox, d->oy, cx, cy);
        float r = d->radius * smax;
        if (!(r > 0.0f)) return 0;
        int32_t h = make_fixture(world, body, d, SP_SHAPE_CIRCLE, 0, r, nullptr, 0, cx, cy);
        if (h <= 0) return h;
        if (out && max > 0) out[0] = h;
        return 1;
    }
    case SP_US_BOX: {
        float hx = 0.5f * d->sx, hy = 0.5f * d->sy;
        Pt p[4];
        const float cxs[4] = {-hx, hx, hx, -hx}, cys[4] = {-hy, -hy, hy, hy};
        for (int32_t i = 0; i < 4; ++i) apply(a, d->ox + cxs[i], d->oy + cys[i], p[i].x, p[i].y);
        if (flip) { Pt t = p[1]; p[1] = p[3]; p[3] = t; }
        float rad = d->radius > 0.0f ? b2_polygonRadius + d->radius * smax : 0.0f;
        return polygon_fixtures(world, body, d, p, 4, rad > 0.0f ? SP_SHAPE_RADIUS : 0, rad, out, max);
    }
    case SP_US_CAPSULE: {
        /* a capsule as a rounded box (plan section 4 Stage 1). Stage 2 fix: Box2D's b2PolygonShape::Set welds points closer than
         * sqrt(0.5 * b2_linearSlop) = 0.05, so Stage 1's core box of width 2 x b2_linearSlop collapsed into Box2D's degenerate fallback
         * (a 2 x 2 box at the body origin). The core is now w0 = 0.03 wide on its narrow axis and w0 longer at each end, with the radius
         * r - w0: the sides and the ends meet the capsule exactly, the rounded corners bulge by at most w0 (sqrt 2 - 1) = 0.012 at 45
         * degrees. A capsule narrower than 2 w0 is a box of its size; one no longer than wide is a circle. */
        bool vert = d->direction == 0;
        float w = d->sx, h = d->sy;
        float r = 0.5f * (vert ? w : h);
        float inner = 0.5f * (vert ? h : w) - r;
        if (inner <= 0.0f) {   /* a circle */
            float cx, cy;
            apply(a, d->ox, d->oy, cx, cy);
            int32_t hh = make_fixture(world, body, d, SP_SHAPE_CIRCLE, 0, r * smax, nullptr, 0, cx, cy);
            if (hh <= 0) return hh;
            if (out && max > 0) out[0] = hh;
            return 1;
        }
        const float w0 = 0.03f;
        float rr = r - w0, ex, ey;
        if (rr > 0.0f) { ex = vert ? w0 : inner + w0; ey = vert ? inner + w0 : w0; }
        else { rr = 0.0f; ex = 0.5f * w; ey = 0.5f * h; }   /* too thin to round: its box */
        Pt p[4];
        const float cxs[4] = {-ex, ex, ex, -ex}, cys[4] = {-ey, -ey, ey, ey};
        for (int32_t i = 0; i < 4; ++i) apply(a, d->ox + cxs[i], d->oy + cys[i], p[i].x, p[i].y);
        if (flip) { Pt t = p[1]; p[1] = p[3]; p[3] = t; }
        if (rr <= 0.0f) return polygon_fixtures(world, body, d, p, 4, 0, 0.0f, out, max);
        return polygon_fixtures(world, body, d, p, 4, SP_SHAPE_RADIUS | SP_SHAPE_NO_SKIN, rr * smax, out, max);   /* Stage 2: the radius is all the capsule's (queries) */
    }
    case SP_US_POLYGON: {
        int32_t n = d->vertex_count;
        if (!xy || n < 3) return 0;
        if (d->flags & SP_USF_UNITY_POLY) return unity_poly_fixture(world, body, d, xy, n, out, max);   /* Stage 4b: body space, one piece */
        Pt *p = (Pt *)malloc((size_t)n * sizeof(Pt));
        if (!p) return SP_ERR_FULL;
        for (int32_t i = 0; i < n; ++i) apply(a, d->ox + xy[2 * i], d->oy + xy[2 * i + 1], p[i].x, p[i].y);
        float rad = d->radius > 0.0f ? b2_polygonRadius + d->radius * smax : 0.0f;
        int32_t r = polygon_fixtures(world, body, d, p, n, rad > 0.0f ? SP_SHAPE_RADIUS : 0, rad, out, max);
        free(p);
        return r;
    }
    case SP_US_EDGE:
    case SP_US_LOOP: {
        bool loop = d->kind == SP_US_LOOP;
        int32_t n = d->vertex_count;
        if (!xy || n < (loop ? 3 : 2)) return 0;
        bool gp = !loop && (d->flags & SP_USF_ADJ_START), gn = !loop && (d->flags & SP_USF_ADJ_END);
        int32_t tot = n + (gp ? 1 : 0) + (gn ? 1 : 0);
        float *v = (float *)malloc((size_t)tot * 2 * sizeof(float));
        if (!v) return SP_ERR_FULL;
        int32_t k = 0;
        if (gp) { apply(a, d->ox + d->adj[0], d->oy + d->adj[1], v[0], v[1]); k = 1; }
        for (int32_t i = 0; i < n; ++i) apply(a, d->ox + xy[2 * i], d->oy + xy[2 * i + 1], v[2 * (k + i)], v[2 * (k + i) + 1]);
        if (gn) apply(a, d->ox + d->adj[2], d->oy + d->adj[3], v[2 * (tot - 1)], v[2 * (tot - 1) + 1]);
        if (d->flags & SP_USF_CHAIN_WELD) {   /* Stage 4a: Unity's chain vertex filter (shepphys.h, SP_USF_CHAIN_WELD) */
            const float t2 = loop ? b2_linearSlop * b2_linearSlop : 2.01f * (b2_linearSlop * b2_linearSlop);
            int32_t m = 1;
            for (int32_t i = 1; i < n; ++i) {
                const float px = v[2 * (k + i)], py = v[2 * (k + i) + 1];
                const float dx = px - v[2 * (k + m - 1)], dy = py - v[2 * (k + m - 1) + 1];
                if (dx * dx + dy * dy > t2) { v[2 * (k + m)] = px; v[2 * (k + m) + 1] = py; m++; }
            }
            if (m < (loop ? 3 : 2)) { free(v); return 0; }
            if (gn) { v[2 * (k + m)] = v[2 * (tot - 1)]; v[2 * (k + m) + 1] = v[2 * (tot - 1) + 1]; }
            n = m; tot = n + (gp ? 1 : 0) + (gn ? 1 : 0);
        }
        if (flip && loop) for (int32_t i = 0, j = tot - 1; i < j; ++i, --j) { float tx = v[2 * i], ty = v[2 * i + 1]; v[2 * i] = v[2 * j]; v[2 * i + 1] = v[2 * j + 1]; v[2 * j] = tx; v[2 * j + 1] = ty; }
        int32_t fl = (loop ? SP_SHAPE_LOOP : 0) | (gp ? SP_SHAPE_GHOST_PREV : 0) | (gn ? SP_SHAPE_GHOST_NEXT : 0) |
                     ((d->flags & SP_USF_ONE_SIDED) ? SP_SHAPE_ONE_SIDED : 0);   /* Stage 3: H-CHAIN */
        float rad = d->radius > 0.0f ? b2_polygonRadius + d->radius * smax : 0.0f;
        if (rad > 0.0f) fl |= SP_SHAPE_RADIUS;
        int32_t h = make_fixture(world, body, d, SP_SHAPE_CHAIN, fl, rad, v, tot, 0.0f, 0.0f);
        free(v);
        if (h <= 0) return h;
        if (out && max > 0) out[0] = h;
        return 1;
    }
    default:
        return SP_ERR_PARAM;
    }
}

/* H-SYNC=A's tolerance (E1, results/probe2/SyncDelta__fine.csv: a game write 9e-5 off the pose physics last wrote is no teleport in Unity
 * 2022.3, 1e-4 is): the midpoint of that bracket, per axis (it was 1e-5 before E1). The engine's child-body check uses the same value
 * (PhysWorld.SyncTol). The exact tolerance, per axis vs distance, and the angle's (kept at 1e-6 rad) are open. */
#define SP_SYNC_TOL 9.5e-5f
#define SP_SYNC_TOL_ANGLE 1e-6f

SP_API int32_t sp_u_sync(int32_t world, const sp_usync *recs, int32_t n, int32_t mode)
{
    UWorld *U = uw(world);
    if (!U) return SP_ERR_HANDLE;
    if (!recs && n > 0) return SP_ERR_PARAM;
    int32_t tp = 0;
    for (int32_t i = 0; i < n; ++i) {
        UBody *B = ub(U, recs[i].body);
        if (!B) continue;
        sp_mirror *m = M(B->mirror);
        float x = recs[i].x, y = recs[i].y, a = sp_atan2f(recs[i].m10, recs[i].m00);
        if (mode == 0 && fabs_(x - m->wx) <= SP_SYNC_TOL && fabs_(y - m->wy) <= SP_SYNC_TOL && fabs_(a - m->wa) <= SP_SYNC_TOL_ANGLE) continue;   /* H-SYNC A */
        if (mode != 0 && fb(x) == fb(m->x) && fb(y) == fb(m->y) && fb(a) == fb(m->angle)) continue;                    /* B / C: no change */
        float xya[3] = {x, y, a};
        sp_body_set_transform(world, B->body, xya);
        if (!(recs[i].flags & SP_USTEP_WAKETP_STOCK) && B->type != SP_BODY_STATIC) sp_body_set_flag(world, B->body, SP_BODY_AWAKE, 1);
        m->x = x; m->y = y; m->angle = a;
        m->wx = x; m->wy = y; m->wa = a;
        m->flags &= ~SP_MF_TELEPORT;
        reset_hist(m, x, y, a);
        tp++;
    }
    return tp;
}

SP_API int32_t sp_u_flush(int32_t world, int32_t flags)
{
    UWorld *U = uw(world);
    if (!U) return SP_ERR_HANDLE;
    return apply_pending(U, flags);
}

/* Stage 3, H-DAMP: the Box2D damping c' whose Pade factor 1 / (1 + h c') equals the variant's factor for Unity's drag c (exp: exp(-h c);
 * linear: max(0, 1 - h c), a factor of 0 being c' = +inf); double inside, one rounding to float (deterministic: IEEE double exp is not used --
 * expm1 by its series to 1e-17 relative, since libm's exp may differ between the Mac and the Vita) */
static double sp_expm1_series(double x)
{
    /* x = h c >= 0, small in practice (drag 10 at h 0.02: 0.2); range-reduce by halving, then square back: e^x - 1 = (e^(x/2) - 1)(e^(x/2) + 1) */
    int k = 0;
    while (x > 0.03125 && k < 60) { x *= 0.5; ++k; }
    double t = x, sum = x;
    for (int i = 2; i < 18; ++i) { t *= x / i; sum += t; }
    while (k-- > 0) sum = sum * (sum + 2.0);
    return sum;
}
static float damp_mapped(float c, float h, int32_t flags)
{
    if (!(c > 0.0f)) return c;
    double hc = (double)h * (double)c;
    if (flags & SP_USTEP_DAMP_EXP) return (float)(sp_expm1_series(hc) / (double)h);   /* 1 / (1 + h c') = e^-hc  <=>  h c' = e^hc - 1 */
    if (flags & SP_USTEP_DAMP_LINEAR) {
        if (hc >= 1.0) return __builtin_inff();
        return (float)(hc / (1.0 - hc) / (double)h);                                  /* 1 / (1 + h c') = 1 - hc  <=>  h c' = hc / (1 - hc) */
    }
    return c;
}

SP_API int32_t sp_u_step(int32_t world, const sp_ustep_def *def, sp_ustep_out *out)
{
    UWorld *U = uw(world);
    if (!U) return SP_ERR_HANDLE;
    if (!def || !(def->dt > 0.0f)) return SP_ERR_PARAM;
    int32_t tp = apply_pending(U, def->flags);
    const float h = def->dt;
    const bool drag_on = (def->flags & SP_USTEP_MOVEDRAG_ON) != 0;
    const bool grav_on = (def->flags & SP_USTEP_MOVEGRAV_ON) != 0;   /* E1: H-MOVEDRAG=drag-off-gravity-on (the engine's setting since E1) */
    const bool damp_var = (def->flags & (SP_USTEP_DAMP_EXP | SP_USTEP_DAMP_LINEAR)) != 0;
    if (!ugrow(U->mv, U->capmv, U->nb + 1)) return SP_ERR_FULL;
    /* Stage 3: forces (plan section 3 "Forces"): the accumulators AddForce / AddTorque filled since the last step act in this one (Box2D's
     * ApplyForceToCenter / ApplyTorque with a wake, Unity's AddForce wakes), then Box2D clears them after the step (auto clear). Only a
     * simulated Dynamic body takes them (Box2D ignores the others); they are emptied either way. */
    for (int32_t s = 0; s < U->nb; ++s) {
        UBody &B = U->b[s];
        if (!B.body) continue;
        sp_mirror *m = M(B.mirror);
        if (!(m->flags & SP_MF_FORCE)) continue;
        if (B.type == SP_BODY_DYNAMIC && (B.flags & SP_UB_SIMULATED)) {
            if (fb(m->fx) != 0 || fb(m->fy) != 0) { float f[2] = {m->fx, m->fy}; sp_body_apply(world, B.body, SP_APPLY_FORCE, f, 1); }
            if (fb(m->torque) != 0) { float t[1] = {m->torque}; sp_body_apply(world, B.body, SP_APPLY_TORQUE, t, 1); }
        }
        m->fx = m->fy = m->torque = 0.0f;
        m->flags &= ~SP_MF_FORCE;
    }
    int32_t nmv = 0;
    for (int32_t s = 0; s < U->nb; ++s) {
        UBody &B = U->b[s];
        if (!B.body) continue;
        sp_mirror *m = M(B.mirror);
        if (!(m->flags & (SP_MF_MOVE_POS | SP_MF_MOVE_ROT))) continue;
        int32_t mf = m->flags;
        m->flags &= ~(SP_MF_MOVE_POS | SP_MF_MOVE_ROT);
        if (B.type == SP_BODY_STATIC || !(B.flags & SP_UB_SIMULATED)) continue;
        b2Body *b = spi_body(world, B.body);
        const b2Vec2 &p = b->GetPosition();
        b2Vec2 v0 = b->GetLinearVelocity();
        float w0 = b->GetAngularVelocity();
        float v[3] = {v0.x, v0.y, w0};
        if (mf & SP_MF_MOVE_POS) { v[0] = (m->tx - p.x) / h; v[1] = (m->ty - p.y) / h; }
        if (mf & SP_MF_MOVE_ROT) v[2] = (m->ta - b->GetAngle()) / h;
        Moved &mv = U->mv[nmv++];
        mv.slot = s; mv.v0x = v0.x; mv.v0y = v0.y; mv.w0 = w0; mv.ld = mv.ad = mv.gs = 0;
        if (!drag_on && B.type == SP_BODY_DYNAMIC) {   /* H-MOVEDRAG: no drag during a move; gravity too unless MOVEGRAV_ON (E1: Unity keeps it) */
            const float zero = 0.0f;
            if (fb(B.ldrag) != 0) { sp_body_set_float(world, B.body, SP_PROP_LINEAR_DAMPING, &zero); mv.ld = 1; }
            if (fb(B.adrag) != 0) { sp_body_set_float(world, B.body, SP_PROP_ANGULAR_DAMPING, &zero); mv.ad = 1; }
            if (!grav_on && fb(B.gscale) != 0) { sp_body_set_float(world, B.body, SP_PROP_GRAVITY_SCALE, &zero); mv.gs = 1; }
        }
        sp_body_set_velocity(world, B.body, v);
        sp_body_set_flag(world, B.body, SP_BODY_AWAKE, 1);   /* a MovePosition wakes, also with a zero delta (section 1.2 item 2) */
    }
    /* Stage 3: frozen position axes (RigidbodyConstraints2D.FreezePositionX / Y) of simulated Dynamic bodies: the axis' velocity is zeroed
     * before the step and the coordinate restored after it (with the velocity component zeroed again) */
    int32_t nfz = 0, ndm = 0;
    for (int32_t s = 0; s < U->nb; ++s) {
        UBody &B = U->b[s];
        if (!B.body || B.type != SP_BODY_DYNAMIC || !(B.flags & SP_UB_SIMULATED) || !(B.flags & (SP_UB_FREEZE_X | SP_UB_FREEZE_Y))) continue;
        if (!ugrow(U->fz, U->capfz, nfz + 1)) return SP_ERR_FULL;
        b2Body *b = spi_body(world, B.body);
        Frozen &z = U->fz[nfz++];
        z.slot = s; z.x = b->GetPosition().x; z.y = b->GetPosition().y;
        b2Vec2 v = b->GetLinearVelocity();
        if (((B.flags & SP_UB_FREEZE_X) && fb(v.x) != 0) || ((B.flags & SP_UB_FREEZE_Y) && fb(v.y) != 0)) {
            float nv[3] = {(B.flags & SP_UB_FREEZE_X) ? 0.0f : v.x, (B.flags & SP_UB_FREEZE_Y) ? 0.0f : v.y, b->GetAngularVelocity()};
            sp_body_set_velocity(world, B.body, nv);
        }
    }
    /* Stage 3, H-DAMP exp / linear: each simulated Dynamic body's damping mapped for this step (not a moving one H-MOVEDRAG zeroed) */
    if (damp_var) {
        for (int32_t s = 0; s < U->nb; ++s) {
            UBody &B = U->b[s];
            if (!B.body || B.type != SP_BODY_DYNAMIC || !(B.flags & SP_UB_SIMULATED)) continue;
            bool moved_zeroed = false;
            for (int32_t i = 0; i < nmv; ++i) if (U->mv[i].slot == s && (U->mv[i].ld || U->mv[i].ad)) moved_zeroed = true;
            if (moved_zeroed) continue;
            float ld = damp_mapped(B.ldrag, h, def->flags), ad = damp_mapped(B.adrag, h, def->flags);
            uint8_t cl = fb(ld) != fb(B.ldrag), ca = fb(ad) != fb(B.adrag);
            if (!cl && !ca) continue;
            if (!ugrow(U->dm, U->capdm, ndm + 1)) return SP_ERR_FULL;
            Damped &d = U->dm[ndm++];
            d.slot = s; d.ld = cl; d.ad = ca;
            if (cl) sp_body_set_float(world, B.body, SP_PROP_LINEAR_DAMPING, &ld);
            if (ca) sp_body_set_float(world, B.body, SP_PROP_ANGULAR_DAMPING, &ad);
        }
    }
    /* Stage 3: the velocities the step starts from (H-RELVEL=pre-solve: Box2D's PreSolve sees these) */
    for (int32_t s = 0; s < U->nb; ++s) {
        UBody &B = U->b[s];
        if (!B.body) continue;
        b2Body *b = spi_body(world, B.body);
        sp_mirror *m = M(B.mirror);
        m->pvx = b->GetLinearVelocity().x; m->pvy = b->GetLinearVelocity().y; m->pw = b->GetAngularVelocity();
    }
    sp_step_def sd = {h, def->velocity_iterations, def->position_iterations, 0};
    sp_step_stats st;
    int32_t r = sp_world_step(world, &sd, &st);
    if (r != SP_OK) return r;
    for (int32_t i = 0; i < nmv; ++i) {
        Moved &mv = U->mv[i];
        UBody &B = U->b[mv.slot];
        if (def->flags & SP_USTEP_MOVEVEL_ZERO) { const float z[3] = {0, 0, 0}; sp_body_set_velocity(world, B.body, z); }
        else if (!(def->flags & SP_USTEP_MOVEVEL_KEEP)) { const float v0[3] = {mv.v0x, mv.v0y, mv.w0}; sp_body_set_velocity(world, B.body, v0); }
        if (mv.ld) sp_body_set_float(world, B.body, SP_PROP_LINEAR_DAMPING, &B.ldrag);
        if (mv.ad) sp_body_set_float(world, B.body, SP_PROP_ANGULAR_DAMPING, &B.adrag);
        if (mv.gs) sp_body_set_float(world, B.body, SP_PROP_GRAVITY_SCALE, &B.gscale);
    }
    for (int32_t i = 0; i < ndm; ++i) {
        Damped &d = U->dm[i];
        UBody &B = U->b[d.slot];
        if (d.ld) sp_body_set_float(world, B.body, SP_PROP_LINEAR_DAMPING, &B.ldrag);
        if (d.ad) sp_body_set_float(world, B.body, SP_PROP_ANGULAR_DAMPING, &B.adrag);
    }
    for (int32_t i = 0; i < nfz; ++i) {
        Frozen &z = U->fz[i];
        UBody &B = U->b[z.slot];
        if (!B.body) continue;
        b2Body *b = spi_body(world, B.body);
        b2Vec2 p = b->GetPosition(), v = b->GetLinearVelocity();
        bool fx = (B.flags & SP_UB_FREEZE_X) != 0, fy = (B.flags & SP_UB_FREEZE_Y) != 0;
        if ((fx && fb(p.x) != fb(z.x)) || (fy && fb(p.y) != fb(z.y))) {
            float xya[3] = {fx ? z.x : p.x, fy ? z.y : p.y, b->GetAngle()};
            sp_body_set_transform(world, B.body, xya);
        }
        if ((fx && fb(v.x) != 0) || (fy && fb(v.y) != 0)) {
            float nv[3] = {fx ? 0.0f : v.x, fy ? 0.0f : v.y, b->GetAngularVelocity()};
            sp_body_set_velocity(world, B.body, nv);
        }
    }
    int32_t written = 0, awake = 0, live = 0;
    for (int32_t s = 0; s < U->nb; ++s) {
        UBody &B = U->b[s];
        if (!B.body) continue;
        live++;
        b2Body *b = spi_body(world, B.body);
        sp_mirror *m = M(B.mirror);
        const b2Vec2 &p = b->GetPosition();
        const b2Vec2 &v = b->GetLinearVelocity();
        float a = b->GetAngle();
        m->x = p.x; m->y = p.y; m->angle = a;
        m->vx = v.x; m->vy = v.y; m->w = b->GetAngularVelocity();
        if (b->IsAwake()) { m->flags |= SP_MF_AWAKE; awake++; } else m->flags &= ~SP_MF_AWAKE;
        if (B.type == SP_BODY_STATIC) continue;
        push_hist(m, p.x, p.y, a, def->step_index);
        bool moved = fb(p.x) != fb(m->wx) || fb(p.y) != fb(m->wy) || fb(a) != fb(m->wa);
        if (moved || (m->flags & SP_MF_FORCE_WRITE)) {
            if (fb(a) != fb(m->wa)) m->flags |= SP_MF_ANGLE;
            m->flags = (m->flags | SP_MF_WRITE) & ~SP_MF_FORCE_WRITE;
            m->wx = p.x; m->wy = p.y; m->wa = a;
            written++;
        }
    }
    if (out) {
        out->step = st.step; out->moved = nmv; out->teleported = tp; out->written = written; out->awake = awake; out->bodies = live;
        out->contacts = st.contacts; out->touching = st.touching;
    }
    return SP_OK;
}

SP_API int32_t sp_u_interp(int32_t world, const float *ad, int32_t flags)
{
    UWorld *U = uw(world);
    if (!U) return SP_ERR_HANDLE;
    if (!ad) return SP_ERR_PARAM;
    const float t = ad[0], fdt = ad[1];
    int32_t n = 0;
    for (int32_t s = 0; s < U->nb; ++s) {
        UBody &B = U->b[s];
        if (!B.body || B.type == SP_BODY_STATIC || !(B.flags & SP_UB_SIMULATED)) continue;
        sp_mirror *m = M(B.mirror);
        int32_t im = m->mode & SP_MM_INTERP_MASK;
        if (im == SP_MM_INTERPOLATE) {
            /* Unity's own formula: lerp(P[k-1], P[k], (time - fixedTime) / fdt) -- a + (b - a) * t */
            m->ix = m->hx[1] + (m->hx[2] - m->hx[1]) * t;
            m->iy = m->hy[1] + (m->hy[2] - m->hy[1]) * t;
            m->ia = m->ha[1] + (m->ha[2] - m->ha[1]) * t;
        } else if (im == SP_MM_EXTRAPOLATE) {
            float dt = t * fdt;
            m->ix = m->x + m->vx * dt; m->iy = m->y + m->vy * dt; m->ia = m->angle + m->w * dt;
        } else continue;
        m->wx = m->ix; m->wy = m->iy; m->wa = m->ia;
        m->flags |= SP_MF_POSE;
        n++;
    }
    return n;
}

SP_API int32_t sp_u_early(int32_t world, int32_t flags)
{
    UWorld *U = uw(world);
    if (!U) return SP_ERR_HANDLE;
    int32_t n = 0;
    for (int32_t s = 0; s < U->nb; ++s) {
        UBody &B = U->b[s];
        if (!B.body || B.type == SP_BODY_STATIC || !(B.flags & SP_UB_SIMULATED)) continue;
        sp_mirror *m = M(B.mirror);
        if ((m->mode & SP_MM_INTERP_MASK) == SP_MM_NONE) continue;
        m->wx = m->x; m->wy = m->y; m->wa = m->angle;
        m->flags |= SP_MF_POSE;
        n++;
    }
    return n;
}

SP_API int32_t sp_u_snapshot(int32_t world)
{
    UWorld *U = uw(world);
    if (!U) return SP_ERR_HANDLE;
    if (!ugrow(U->snap, U->capsnap, U->nb + 1)) return SP_ERR_FULL;
    int32_t n = 0;
    for (int32_t s = 0; s < U->nb; ++s) {
        UBody &B = U->b[s];
        if (!B.body || B.type == SP_BODY_STATIC) continue;
        sp_mirror *m = M(B.mirror);
        Snap &q = U->snap[n++];
        q.body = B.body; q.serial = m->serial; q.mode = m->mode; q.valid = (B.flags & SP_UB_SIMULATED) ? 1 : 0;
        memcpy(q.hx, m->hx, sizeof q.hx); memcpy(q.hy, m->hy, sizeof q.hy); memcpy(q.ha, m->ha, sizeof q.ha);
        q.vx = m->vx; q.vy = m->vy; q.w = m->w;
    }
    U->nsnap = n;
    return n;
}

SP_API int32_t sp_render_poses(int32_t world, const sp_render_def *def, sp_rpose *out, int32_t max)
{
    UWorld *U = uw(world);
    if (!U) return SP_ERR_HANDLE;
    if (!def) return SP_ERR_PARAM;
    int32_t jrel = def->j_rel;
    float al = def->alpha;
    bool clamped = false;
    if (jrel > 0) { jrel = 0; al = 1.0f; clamped = true; }
    if (jrel < -1) { jrel = -1; al = 0.0f; clamped = true; }
    if (!(al >= 0.0f)) { al = 0.0f; clamped = true; }
    if (al > 1.0f) { al = 1.0f; clamped = true; }
    if (clamped) U->render_clamped++;
    int32_t j = 2 + jrel;   /* history index of P[j] */
    int32_t n = 0;
    for (int32_t i = 0; i < U->nsnap; ++i) {
        const Snap &q = U->snap[i];
        if (!q.valid) continue;
        if (n < max && out) {
            sp_rpose &o = out[n];
            o.body = q.body; o.serial = q.serial; o.mode = q.mode;
            int32_t im = q.mode & SP_MM_INTERP_MASK;
            if (im == SP_MM_INTERPOLATE) {
                o.x = q.hx[j - 1] + (q.hx[j] - q.hx[j - 1]) * al;
                o.y = q.hy[j - 1] + (q.hy[j] - q.hy[j - 1]) * al;
                o.angle = q.ha[j - 1] + (q.ha[j] - q.ha[j - 1]) * al;
            } else if (im == SP_MM_EXTRAPOLATE) {
                float dt = al * def->fdt;
                o.x = q.hx[j] + q.vx * dt; o.y = q.hy[j] + q.vy * dt; o.angle = q.ha[j] + q.w * dt;
            } else { o.x = q.hx[j]; o.y = q.hy[j]; o.angle = q.ha[j]; }
        }
        n++;
    }
    return clamped ? (int32_t)((uint32_t)n | 0x40000000u) : n;   /* bit 30: the request was outside the served window */
}

} /* extern "C" */

/* ---------------- Stage 3: the contact snapshot of a collider pair (Collision2D) ---------------- */
namespace {
/* a body's velocity at world point p: post-solve from Box2D, pre-solve from the mirror's pv* (the start of the last step); bodies without a
 * mirror (the ground body) are at rest */
b2Vec2 point_vel(UWorld *U, b2Body *b, const b2Vec2 &p, bool post, b2Vec2 *lin)
{
    if (post) { if (lin) *lin = b->GetLinearVelocity(); return b->GetLinearVelocityFromWorldPoint(p); }
    UBody *B = ub(U, (int32_t)(intptr_t)b->GetUserData());
    if (!B) { if (lin) lin->SetZero(); return b2Vec2(0.0f, 0.0f); }
    const sp_mirror *m = M(B->mirror);
    b2Vec2 v(m->pvx, m->pvy);
    if (lin) *lin = v;
    return v + b2Cross(m->pw, p - b->GetWorldCenter());
}
bool in_set(const int32_t *set, int32_t n, int32_t h) { for (int32_t i = 0; i < n; ++i) if (set[i] == h) return true; return false; }
} /* namespace */

extern "C" SP_API int32_t sp_u_contacts_between(int32_t world, const int32_t *a, int32_t na, const int32_t *b, int32_t nb, int32_t flags,
                                                sp_ucontact *out, int32_t max, float *rel2)
{
    UWorld *U = uw(world);
    if (!U) return SP_ERR_HANDLE;
    if ((!a && na > 0) || (!b && nb > 0) || (!out && max > 0)) return SP_ERR_PARAM;
    const bool post = (flags & SP_UCF_POST_SOLVE) != 0;
    int32_t n = 0;
    bool have_rel = false;
    for (int32_t i = 0; i < na; ++i) {
        b2Fixture *fa = spi_fixture(world, a[i]);
        if (!fa) continue;
        b2Body *ba = fa->GetBody();
        for (b2ContactEdge *ce = ba->GetContactList(); ce; ce = ce->next) {
            b2Contact *c = ce->contact;
            b2Fixture *f1 = c->GetFixtureA(), *f2 = c->GetFixtureB();
            if (f1 != fa && f2 != fa) continue;
            if (!c->IsTouching() || f1->IsSensor() || f2->IsSensor()) continue;
            b2Fixture *fo = f1 == fa ? f2 : f1;
            int32_t ho = (int32_t)(intptr_t)fo->GetUserData();
            if (!in_set(b, nb, ho)) continue;
            const b2Manifold *mf = c->GetManifold();
            if (mf->pointCount <= 0) continue;
            b2WorldManifold wm;
            c->GetWorldManifold(&wm);
            float sgn = f1 == fa ? 1.0f : -1.0f;   /* Box2D's normal points from its fixture A to B: from set a to set b */
            b2Body *bo = fo->GetBody();
            for (int32_t k = 0; k < mf->pointCount && k < b2_maxManifoldPoints; ++k) {
                if (out && n < max) {
                    sp_ucontact &o = out[n];
                    b2Vec2 la, lb;
                    b2Vec2 va = point_vel(U, ba, wm.points[k], post, &la), vb = point_vel(U, bo, wm.points[k], post, &lb);
                    o.shape_a = a[i]; o.shape_b = ho;
                    o.px = wm.points[k].x; o.py = wm.points[k].y;
                    o.nx = sgn * wm.normal.x; o.ny = sgn * wm.normal.y;
                    o.separation = wm.separations[k];
                    o.normal_impulse = mf->points[k].normalImpulse; o.tangent_impulse = mf->points[k].tangentImpulse;
                    o.rvx = vb.x - va.x; o.rvy = vb.y - va.y;
                    o.flags = c->IsEnabled() ? SP_UCPF_ENABLED : 0;
                    if (rel2 && !have_rel) { rel2[0] = lb.x - la.x; rel2[1] = lb.y - la.y; have_rel = true; }
                }
                n++;
            }
        }
    }
    if (rel2 && !have_rel) {   /* no touching point (an Exit): the linear velocities of the two sets' first live fixtures' bodies */
        b2Fixture *fa = nullptr, *fo = nullptr;
        for (int32_t i = 0; i < na && !fa; ++i) fa = spi_fixture(world, a[i]);
        for (int32_t i = 0; i < nb && !fo; ++i) fo = spi_fixture(world, b[i]);
        b2Vec2 la(0.0f, 0.0f), lb(0.0f, 0.0f);
        if (fa) point_vel(U, fa->GetBody(), fa->GetBody()->GetWorldCenter(), post, &la);
        if (fo) point_vel(U, fo->GetBody(), fo->GetBody()->GetWorldCenter(), post, &lb);
        rel2[0] = lb.x - la.x; rel2[1] = lb.y - la.y;
    }
    return n;
}

extern "C" SP_API int32_t sp_u_apply(int32_t world, int32_t body, int32_t kind, const float *args)
{
    UWorld *U = uw(world);
    if (!U) return SP_ERR_HANDLE;
    UBody *B = ub(U, body);
    if (!B) return SP_ERR_HANDLE;
    if (!args) return SP_ERR_PARAM;
    sp_mirror *m = M(B->mirror);
    switch (kind) {
    case SP_APPLY_FORCE: m->fx += args[0]; m->fy += args[1]; m->flags |= SP_MF_FORCE; return SP_OK;
    case SP_APPLY_TORQUE: m->torque += args[0]; m->flags |= SP_MF_FORCE; return SP_OK;
    case SP_APPLY_IMPULSE:
    case SP_APPLY_ANGULAR_IMPULSE:
        if (B->type != SP_BODY_DYNAMIC || !(B->flags & SP_UB_SIMULATED)) return SP_OK;
        if (kind == SP_APPLY_IMPULSE) { m->vx += m->inv_mass * args[0]; m->vy += m->inv_mass * args[1]; }
        else m->w += m->inv_i * args[0];
        m->flags |= SP_MF_SET_VEL;
        return SP_OK;
    default: return SP_ERR_PARAM;
    }
}
