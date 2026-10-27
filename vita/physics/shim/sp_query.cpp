/* sp_query.cpp -- physics Stage 2: Unity-shaped queries of libshepphys (physics-plan-v2 section 4 "Stage 2" (the query shape, PhysBench
 * finding 5), section 2 "Stage 2" (the icalls they serve), Appendix A (H-QSKIN, H-CASTSLOP, H-STARTNORMAL, H-BOUNDSSKIN,
 * H-OVERLAPORDER)). Ours, not Box2D; it reads Box2D through its public API only and changes nothing (queries are not traced).
 *
 *  - Overlap and point queries walk b2BroadPhase directly (b2ContactManager::m_broadPhase is public in 2.3.1): each proxy is one
 *    fixture child, so a chain (a composite's outline) tests only the touched edge -- not every child of the fixture as stage 0a's
 *    sp_overlap_shape does. Results are de-duplicated per collider (the shape's sp_shape_def.user) in a scratch block each world slot
 *    keeps (grown once, no allocation per call); an "any" query stops at its first hits.
 *  - Rays use the broadphase's own ray cast with b2Fixture::RayCast per proxy -- exactly what b2World::RayCast does, with the child
 *    index at hand -- except for shapes with their own rounding (a capsule's radius, an edge radius), which b2PolygonShape::RayCast does
 *    not see: those go through the analytic sweep: conservative advancement (Newton on the convex distance function, from the left) on
 *    b2Distance between the exact shapes -- Box2D's b2_polygonRadius skin excluded, the collider's own rounding kept.
 *  - Shape casts since E1 (H-CASTSLOP=toi, physics-work/e1 results/arm64-r1/Queries.csv): Box2D's b2TimeOfImpact between the probe and
 *    each fixture child, both with their full radius (the b2_polygonRadius skin of polygons and edges included), so the cast stops at
 *    Box2D's TOI target separation r_total - 3 b2_linearSlop; the centroid is origin + dir x (t L), the distance |centroid - origin|, the
 *    point and normal those of Box2D's contact manifold at that pose (the evaluation b2Contact would make for the pair, A / B in Box2D's
 *    registration order; the point the mean of the manifold's points, the normal turned to point from the collider to the probe).
 *  - Overlaps keep the skin (H-QSKIN: b2TestOverlap); point tests use b2Fixture::TestPoint (no skin; rounded shapes by their radius).
 *  - queriesStartInColliders (SP_UQF_START_IN): a ray starting inside a collider reports it at distance 0, the point at the origin, the
 *    normal 0 - dir (H-STARTNORMAL; E1: +0, not -0, for a zero component); a cast whose probe starts overlapping a collider (TOI 0), or a
 *    zero-length cast overlapping it, reports distance 0 and centroid = origin with the manifold's point and normal at the origin, whatever
 *    the direction (E1); without SP_UQF_START_IN such a collider is ignored.
 *  - Order: overlaps in broadphase order (H-OVERLAPORDER=tree order, the dynamic tree's own traversal -- deterministic); *All rays and
 *    casts by distance (stable: ties keep broadphase order).
 * USER_0 only (plan section 3 "Threads"), like the rest of the core. */
#include "shepphys.h"
#include "sp_internal.h"

#include <Box2D/Box2D.h>
#include <math.h>
#include <stdlib.h>
#include <string.h>

namespace {

inline int32_t slot_of(int32_t h) { return (int32_t)(h & 0xFFFF) - 1; }

/* ---- per-world scratch: the hits found so far and a user -> hit index table ---- */
struct Scratch {
    sp_uhit *hit = nullptr; uint8_t *start = nullptr;
    int32_t n = 0, cap = 0;
    int32_t *tab = nullptr; uint32_t tcap = 0;    /* open addressing: user -> index + 1 (0 = empty) */
    int32_t *skip = nullptr; int32_t nskip = 0, capskip = 0;   /* colliders ignored as a whole (overlapping the start without START_IN) */
    uint32_t bounds_version = 0xFFFFFFFFu; int32_t bounds_world = 0; bool bounds_valid = false; b2AABB bounds;
};
enum { MAX_W = 8 };
Scratch g_scr[MAX_W];

template <typename T> bool qgrow(T *&v, int32_t &cap, int32_t need)
{
    if (need <= cap) return true;
    int32_t nc = cap ? cap : 64;
    while (nc < need) nc *= 2;
    T *nv = (T *)realloc(v, (size_t)nc * sizeof(T));
    if (!nv) return false;
    v = nv; cap = nc;
    return true;
}

inline uint32_t uh(int32_t u) { uint32_t x = (uint32_t)u * 2654435761u; return x ^ (x >> 15); }

void scr_reset(Scratch &S)
{
    S.n = 0; S.nskip = 0;
    if (!S.tcap) { S.tcap = 256; S.tab = (int32_t *)calloc(S.tcap, sizeof(int32_t)); }
    else memset(S.tab, 0, S.tcap * sizeof(int32_t));
}
/* the index of user's hit, or -1 */
int32_t scr_find(const Scratch &S, int32_t user)
{
    if (!S.tab) return -1;
    for (uint32_t i = uh(user) & (S.tcap - 1), k = 0; k < S.tcap; i = (i + 1) & (S.tcap - 1), ++k) {
        int32_t v = S.tab[i];
        if (!v) return -1;
        if (S.hit[v - 1].user == user) return v - 1;
    }
    return -1;
}
bool scr_skipped(const Scratch &S, int32_t user) { for (int32_t i = 0; i < S.nskip; ++i) if (S.skip[i] == user) return true; return false; }
/* appends a hit for a user not seen yet; returns its index or -1 (out of memory) */
int32_t scr_add(Scratch &S, const sp_uhit &h, bool start)
{
    if (!qgrow(S.hit, S.cap, S.n + 1)) return -1;
    if (S.cap > 0 && !S.start) S.start = (uint8_t *)malloc((size_t)S.cap);
    else if (S.start) { uint8_t *ns = (uint8_t *)realloc(S.start, (size_t)S.cap); if (!ns) return -1; S.start = ns; }
    if ((uint32_t)(S.n + 1) * 2 > S.tcap) {   /* rehash */
        uint32_t nc = S.tcap * 2;
        while ((uint32_t)(S.n + 1) * 2 > nc) nc *= 2;
        int32_t *nt = (int32_t *)calloc(nc, sizeof(int32_t));
        if (!nt) return -1;
        for (int32_t k = 0; k < S.n; ++k) { uint32_t i = uh(S.hit[k].user) & (nc - 1); while (nt[i]) i = (i + 1) & (nc - 1); nt[i] = k + 1; }
        free(S.tab); S.tab = nt; S.tcap = nc;
    }
    S.hit[S.n] = h; S.start[S.n] = start ? 1 : 0;
    uint32_t i = uh(h.user) & (S.tcap - 1);
    while (S.tab[i]) i = (i + 1) & (S.tcap - 1);
    S.tab[i] = S.n + 1;
    return S.n++;
}

struct Meta { int32_t shape, user, layer; float ur; };

/* the proxy's fixture child passes the query's filter */
bool pass(int32_t world, const sp_uquery *q, b2Fixture *f, Meta *m)
{
    if (f->IsSensor() && !(q->flags & SP_UQF_TRIGGERS)) return false;
    m->shape = (int32_t)(intptr_t)f->GetUserData();
    if (!spi_shape_meta(world, m->shape, &m->user, &m->layer, &m->ur)) return false;
    if (!((q->layer_mask >> ((uint32_t)m->layer & 31u)) & 1u)) return false;
    if (q->exclude_user != 0 && m->user == q->exclude_user) return false;
    return true;
}

/* a b2DistanceProxy of the fixture child with the collider's exact rounding (Box2D's polygon skin removed) */
void exact_proxy(b2DistanceProxy &p, const b2Fixture *f, int32_t child, float ur)
{
    p.Set(f->GetShape(), child);
    p.m_radius = ur;
}

/* does the fixture child contain p (the collider's exact shape: TestPoint; rounded polygons / edges by their radius)? */
bool contains(const b2Fixture *f, int32_t child, float ur, const b2Vec2 &p)
{
    b2Shape::Type t = f->GetType();
    if (t == b2Shape::e_circle || (t == b2Shape::e_polygon && ur <= 0.0f)) return f->TestPoint(p);
    if (ur <= 0.0f) return false;   /* an edge / chain without radius has no inside */
    b2DistanceProxy a, b;
    b2CircleShape pt; pt.m_radius = 0.0f; pt.m_p.SetZero();
    a.Set(&pt, 0); a.m_radius = 0.0f;
    exact_proxy(b, f, child, 0.0f);
    b2DistanceInput in; in.proxyA = a; in.proxyB = b; in.transformA.Set(p, 0.0f); in.transformB = f->GetBody()->GetTransform(); in.useRadii = false;
    b2SimplexCache c; c.count = 0;
    b2DistanceOutput o; b2Distance(&o, &c, &in);
    if (t == b2Shape::e_polygon && o.distance <= 0.0f) return true;   /* inside the core polygon */
    return o.distance <= ur;
}

/* Conservative advancement along dir (unit length) from origin over [0, L]: the first t at which probe A (radius ra, at origin + t dir,
 * angle aa) touches the fixture child (its exact radius ur). The distance between the moving convex probe and the fixed convex shape is
 * a convex function of t, so Newton's steps from t = 0 approach its first root from the left without passing it. Returns 1 (a hit at
 * *t, the point on the shape, the normal from the shape toward the probe), 0 (no hit), 2 (overlapping at t = 0). */
const float kTol = 5e-5f;
int32_t sweep(const b2DistanceProxy &A0, float ra, const b2Vec2 &origin, float aa, const b2Vec2 &dir, float L, const b2Fixture *f, int32_t child,
              float ur, float *t_out, b2Vec2 *p_out, b2Vec2 *n_out)
{
    b2DistanceProxy B;
    exact_proxy(B, f, child, 0.0f);
    b2DistanceInput in; in.proxyA = A0; in.proxyA.m_radius = 0.0f; in.proxyB = B; in.transformB = f->GetBody()->GetTransform(); in.useRadii = false;
    b2SimplexCache cache; cache.count = 0;
    b2DistanceOutput o;
    const float rr = ra + ur;
    float t = 0.0f;
    b2Vec2 u(0.0f, 0.0f);   /* unit, from the probe's core toward the shape's core (the last well-defined one) */
    for (int32_t it = 0; it < 48; ++it) {
        in.transformA.Set(origin + t * dir, aa);
        b2Distance(&o, &cache, &in);
        float d = o.distance - rr;
        b2Vec2 w = o.pointB - o.pointA;
        float wl = w.Length();
        if (wl > b2_epsilon) u = (1.0f / wl) * w;
        if (d <= kTol) {
            if (it == 0) return 2;
            if (wl <= b2_epsilon && u.x == 0.0f && u.y == 0.0f) return 0;
            *t_out = t;
            *p_out = o.pointB - ur * u;
            *n_out = -u;
            return 1;
        }
        if (wl <= b2_epsilon) return 0;
        float closing = b2Dot(dir, u);
        if (closing <= 1e-6f) return 0;      /* moving away, or along the surface */
        t += d / closing;
        if (t > L) return 0;
    }
    return 0;
}

/* E1 (H-CASTSLOP=toi, H-STARTNORMAL): the contact manifold of the probe (at xq) and a fixture child, evaluated as b2Contact would for the
 * pair -- Box2D's collide functions take the polygon / edge first, and Unity passes the probe as A when the types allow either order (E1:
 * circle-circle and polygon-polygon normals); the point = the mean of the world manifold's points, the normal from the collider toward the
 * probe. Pairs Box2D has no function for (an edge probe -- a capsule -- against an edge or a chain) and an empty manifold fall back to the
 * surfaces' midpoint along b2Distance's axis. */
void cast_manifold(const b2Shape *sq, const b2Transform &xq, const b2Fixture *f, int32_t child, b2Vec2 *p_out, b2Vec2 *n_out)
{
    const b2Shape *sc = f->GetShape();
    const b2Transform &xc = f->GetBody()->GetTransform();
    b2EdgeShape ce;
    if (sc->GetType() == b2Shape::e_chain) { ((const b2ChainShape *)sc)->GetChildEdge(&ce, child); sc = &ce; }
    b2Shape::Type tq = sq->GetType(), tc = sc->GetType();
    b2Manifold m; m.pointCount = 0;
    bool qa = true;   /* the probe is A */
    const b2Shape *sa = sq, *sb = sc; b2Transform xa = xq, xb = xc;
    auto swap = [&]() { qa = false; sa = sc; sb = sq; xa = xc; xb = xq; };
    if (tq == b2Shape::e_circle && tc == b2Shape::e_circle) b2CollideCircles(&m, (const b2CircleShape *)sa, xa, (const b2CircleShape *)sb, xb);
    else if (tq == b2Shape::e_polygon && tc == b2Shape::e_polygon) b2CollidePolygons(&m, (const b2PolygonShape *)sa, xa, (const b2PolygonShape *)sb, xb);
    else if (tq == b2Shape::e_polygon && tc == b2Shape::e_circle) b2CollidePolygonAndCircle(&m, (const b2PolygonShape *)sa, xa, (const b2CircleShape *)sb, xb);
    else if (tq == b2Shape::e_circle && tc == b2Shape::e_polygon) { swap(); b2CollidePolygonAndCircle(&m, (const b2PolygonShape *)sa, xa, (const b2CircleShape *)sb, xb); }
    else if (tq == b2Shape::e_edge && tc == b2Shape::e_circle) b2CollideEdgeAndCircle(&m, (const b2EdgeShape *)sa, xa, (const b2CircleShape *)sb, xb);
    else if (tq == b2Shape::e_edge && tc == b2Shape::e_polygon) {
        /* a capsule probe (an edge with its radius) against a polygon: E1 (capsulecast-inside / -face) has the polygon as A and the capsule
         * as B, like Box2D's polygon pairs -- the probe as a two-vertex polygon (its segment and radius; Box2D's own normals) */
        const b2EdgeShape *e = (const b2EdgeShape *)sq;
        b2PolygonShape cp;
        cp.m_count = 2; cp.m_vertices[0] = e->m_vertex1; cp.m_vertices[1] = e->m_vertex2;
        cp.m_normals[0] = b2Cross(e->m_vertex2 - e->m_vertex1, 1.0f); cp.m_normals[0].Normalize();
        cp.m_normals[1] = b2Cross(e->m_vertex1 - e->m_vertex2, 1.0f); cp.m_normals[1].Normalize();
        cp.m_centroid = 0.5f * (e->m_vertex1 + e->m_vertex2); cp.m_radius = e->m_radius;
        qa = false;
        b2CollidePolygons(&m, (const b2PolygonShape *)sc, xc, &cp, xq);
        if (m.pointCount > 0) {
            b2WorldManifold wm;
            wm.Initialize(&m, xc, sc->m_radius, xq, cp.m_radius);
            b2Vec2 p = wm.points[0];
            if (m.pointCount == 2) p = 0.5f * (wm.points[0] + wm.points[1]);
            *p_out = p; *n_out = wm.normal;
            return;
        }
    }
    else if (tq == b2Shape::e_circle && tc == b2Shape::e_edge) { swap(); b2CollideEdgeAndCircle(&m, (const b2EdgeShape *)sa, xa, (const b2CircleShape *)sb, xb); }
    else if (tq == b2Shape::e_polygon && tc == b2Shape::e_edge) { swap(); b2CollideEdgeAndPolygon(&m, (const b2EdgeShape *)sa, xa, (const b2PolygonShape *)sb, xb); }
    if (m.pointCount > 0) {
        b2WorldManifold wm;
        wm.Initialize(&m, xa, sa->m_radius, xb, sb->m_radius);
        b2Vec2 p = wm.points[0];
        if (m.pointCount == 2) p = 0.5f * (wm.points[0] + wm.points[1]);
        *p_out = p;
        *n_out = qa ? -wm.normal : wm.normal;   /* the manifold's normal runs from A to B */
        return;
    }
    /* fallback: b2Distance between the cores, the surfaces' midpoint */
    b2DistanceInput in; in.proxyA.Set(sq, 0); in.proxyB.Set(f->GetShape(), child); in.transformA = xq; in.transformB = xc; in.useRadii = false;
    b2SimplexCache cache; cache.count = 0;
    b2DistanceOutput o; b2Distance(&o, &cache, &in);
    b2Vec2 n = o.pointB - o.pointA;
    if (n.Normalize() < b2_epsilon) n.Set(1.0f, 0.0f);
    b2Vec2 cA = o.pointA + in.proxyA.m_radius * n, cB = o.pointB - in.proxyB.m_radius * n;
    *p_out = 0.5f * (cA + cB);
    *n_out = -n;
}

/* E1 (H-CASTSLOP=toi): the probe sq (its own radius: a circle's r, a box's b2_polygonRadius skin, a capsule's r) swept from origin along dir
 * (unit) over L against a fixture child (its full radius) with Box2D's b2TimeOfImpact: the probe moves (sweep A, origin -> origin + L dir),
 * the collider's body stands still (sweep B). Returns 1 (a hit: *t_out = t L, the centroid, point and normal), 0 (none), 2 (the probe starts
 * overlapping: TOI 0, or with zero = a zero-length cast an overlap at the origin; the point and normal are filled for the origin). */
int32_t cast_toi(const b2Shape *sq, const b2Vec2 &origin, float aa, const b2Vec2 &dir, float L, bool zero, const b2Fixture *f, int32_t child,
                 float *t_out, b2Vec2 *c_out, b2Vec2 *p_out, b2Vec2 *n_out)
{
    const b2Body *body = f->GetBody();
    b2Transform xq0; xq0.Set(origin, aa);
    if (zero) {
        if (!b2TestOverlap(sq, 0, f->GetShape(), child, xq0, body->GetTransform())) return 0;
        *t_out = 0.0f; *c_out = origin;
        cast_manifold(sq, xq0, f, child, p_out, n_out);
        return 2;
    }
    b2TOIInput in;
    in.proxyA.Set(sq, 0);
    in.proxyB.Set(f->GetShape(), child);
    in.sweepA.localCenter.SetZero(); in.sweepA.c0 = origin; in.sweepA.c = origin + L * dir; in.sweepA.a0 = aa; in.sweepA.a = aa; in.sweepA.alpha0 = 0.0f;
    in.sweepB.localCenter = body->GetLocalCenter(); in.sweepB.c0 = body->GetWorldCenter(); in.sweepB.c = in.sweepB.c0;
    in.sweepB.a0 = body->GetAngle(); in.sweepB.a = in.sweepB.a0; in.sweepB.alpha0 = 0.0f;
    in.tMax = 1.0f;
    b2TOIOutput out;
    b2TimeOfImpact(&out, &in);
    if (out.state == b2TOIOutput::e_separated) return 0;
    if (out.state == b2TOIOutput::e_overlapped || !(out.t > 0.0f)) {
        *t_out = 0.0f; *c_out = origin;
        cast_manifold(sq, xq0, f, child, p_out, n_out);
        return 2;
    }
    b2Transform xq;
    in.sweepA.GetTransform(&xq, out.t);   /* E1: the centroid is the sweep's pose at t, (1 - t) c0 + t c1 (circlecast-face-oblique's bits) */
    *t_out = out.t;
    *c_out = xq.p;
    cast_manifold(sq, xq, f, child, p_out, n_out);
    return 1;
}

/* the world's bounds (every proxy's fat AABB), cached per world version: a non-finite or huge ray / cast length is clamped to them */
bool world_bounds(int32_t world, b2World *w, Scratch &S, b2AABB *out)
{
    uint32_t v = spi_world_version(world);
    if (S.bounds_world != world || S.bounds_version != v) {
        S.bounds_world = world; S.bounds_version = v; S.bounds_valid = false;
        for (b2Body *b = w->GetBodyList(); b; b = b->GetNext()) {
            if (!b->IsActive()) continue;
            for (b2Fixture *f = b->GetFixtureList(); f; f = f->GetNext())
                for (int32_t c = 0; c < f->GetShape()->GetChildCount(); ++c) {
                    const b2AABB &a = f->GetAABB(c);
                    if (!S.bounds_valid) { S.bounds = a; S.bounds_valid = true; } else S.bounds.Combine(a);
                }
        }
    }
    *out = S.bounds;
    return S.bounds_valid;
}
float clamp_length(int32_t world, b2World *w, Scratch &S, const b2Vec2 &o, float L, float probe_extent)
{
    if (L > 0.0f && L < 1e5f) return L;
    b2AABB a;
    if (!world_bounds(world, w, S, &a)) return L > 0.0f && L < 1e5f ? L : 1.0f;
    float dx = b2Max(b2Abs(o.x - a.lowerBound.x), b2Abs(o.x - a.upperBound.x));
    float dy = b2Max(b2Abs(o.y - a.lowerBound.y), b2Abs(o.y - a.upperBound.y));
    float far = sqrtf(dx * dx + dy * dy) + probe_extent + 1.0f;
    return (L > 0.0f && L < far) ? L : far;
}

/* the probe shape of an overlap / cast query, its proxy and its AABB at the origin */
struct Probe {
    b2CircleShape circle; b2PolygonShape poly; b2EdgeShape edge;
    const b2Shape *shape = nullptr;
    float ra = 0.0f;        /* the probe's exact radius (sweeps) */
    float angle = 0.0f;
    float extent = 0.0f;    /* a bound of its size (length clamping) */
};
bool make_probe(const sp_uquery *q, Probe &P)
{
    switch (q->kind) {
    case SP_UQ_RAY: case SP_UQ_POINT:
        P.circle.m_radius = 0.0f; P.circle.m_p.SetZero(); P.shape = &P.circle; P.ra = 0.0f; return true;
    case SP_UQ_CIRCLE: case SP_UQ_CIRCLE_CAST:
        if (!(q->radius >= 0.0f)) return false;
        P.circle.m_radius = q->radius; P.circle.m_p.SetZero(); P.shape = &P.circle; P.ra = q->radius; P.extent = q->radius; return true;
    case SP_UQ_BOX: case SP_UQ_BOX_CAST: {
        float hx = 0.5f * q->sx, hy = 0.5f * q->sy;
        if (!(hx > 0.0f) || !(hy > 0.0f)) return false;
        P.poly.SetAsBox(hx, hy);                     /* m_radius = b2_polygonRadius: the overlap test's skin (H-QSKIN) */
        P.shape = &P.poly; P.ra = 0.0f; P.angle = q->angle; P.extent = sqrtf(hx * hx + hy * hy); return true;
    }
    case SP_UQ_CAPSULE: case SP_UQ_CAPSULE_CAST: {
        bool vert = q->direction == 0;
        float w = q->sx, h = q->sy;
        float r = q->radius > 0.0f ? q->radius : 0.5f * (vert ? w : h);
        float inner = 0.5f * (vert ? h : w) - r;
        if (!(r > 0.0f)) return false;
        if (inner <= 0.0f) { P.circle.m_radius = r; P.circle.m_p.SetZero(); P.shape = &P.circle; P.ra = r; P.extent = r; return true; }
        if (vert) P.edge.Set(b2Vec2(0.0f, -inner), b2Vec2(0.0f, inner)); else P.edge.Set(b2Vec2(-inner, 0.0f), b2Vec2(inner, 0.0f));
        P.edge.m_radius = r;                          /* an exact capsule: a segment with a radius */
        P.shape = &P.edge; P.ra = r; P.angle = q->angle; P.extent = inner + r; return true;
    }
    default: return false;
    }
}

/* ---- broadphase walkers ---- */
struct OverlapWalk {
    int32_t world; const sp_uquery *q; const b2BroadPhase *bp; Scratch *S; int32_t max;
    const Probe *P; b2Transform xf; b2Vec2 point; bool is_point; bool stop = false;
    bool QueryCallback(int32_t proxyId) { b2FixtureProxy *fp = (b2FixtureProxy *)bp->GetUserData(proxyId); return Visit(fp->fixture, fp->childIndex); }
    bool Visit(b2Fixture *f, int32_t child)
    {
        Meta m;
        if (!pass(world, q, f, &m)) return true;
        if (scr_find(*S, m.user) >= 0) return true;   /* this collider is in already */
        bool hit;
        if (is_point) hit = contains(f, child, m.ur, point);
        else hit = b2TestOverlap(P->shape, 0, f->GetShape(), child, xf, f->GetBody()->GetTransform());
        if (!hit) return true;
        sp_uhit h; memset(&h, 0, sizeof h);
        h.shape = m.shape; h.user = m.user; h.cx = xf.p.x; h.cy = xf.p.y; h.px = xf.p.x; h.py = xf.p.y;
        if (scr_add(*S, h, false) < 0) { stop = true; return false; }
        if ((q->flags & SP_UQF_ANY) && S->n >= max) { stop = true; return false; }
        return true;
    }
};

struct SweepWalk {
    int32_t world; const sp_uquery *q; const b2BroadPhase *bp; Scratch *S;
    const Probe *P; b2DistanceProxy A; b2Vec2 o, dir; float L; bool zero;
    bool QueryCallback(int32_t proxyId) { b2FixtureProxy *fp = (b2FixtureProxy *)bp->GetUserData(proxyId); return Visit(fp->fixture, fp->childIndex); }
    bool Visit(b2Fixture *f, int32_t child)
    {
        Meta m;
        if (!pass(world, q, f, &m)) return true;
        if (scr_skipped(*S, m.user)) return true;
        int32_t at = scr_find(*S, m.user);
        if (at >= 0 && S->start[at]) return true;    /* already a start-inside hit: nothing can be closer */
        float t = 0.0f; b2Vec2 c, p, n;
        int32_t r = cast_toi(P->shape, o, P->angle, dir, L, zero, f, child, &t, &c, &p, &n);   /* E1: H-CASTSLOP=toi */
        if (r == 0) return true;
        sp_uhit h; memset(&h, 0, sizeof h);
        h.shape = m.shape; h.user = m.user;
        if (r == 2) {
            if (!(q->flags & SP_UQF_START_IN)) {   /* the collider overlaps the start: ignored as a whole */
                if (at >= 0) { S->hit[at].distance = -1.0f; }   /* drop it (compacted below) */
                if (qgrow(S->skip, S->capskip, S->nskip + 1)) S->skip[S->nskip++] = m.user;
                return true;
            }
            h.cx = o.x; h.cy = o.y; h.px = p.x; h.py = p.y; h.nx = n.x; h.ny = n.y; h.distance = 0.0f;   /* E1: the manifold at the origin */
            h.fraction = 0.0f;
            if (at >= 0) { S->hit[at] = h; S->start[at] = 1; } else scr_add(*S, h, true);
            return true;
        }
        b2Vec2 dv = c - o;
        h.cx = c.x; h.cy = c.y; h.px = p.x; h.py = p.y; h.nx = n.x; h.ny = n.y; h.distance = dv.Length(); h.fraction = t;   /* (the fraction: TOI's t) */
        if (at >= 0) { if (h.distance < S->hit[at].distance) S->hit[at] = h; }
        else scr_add(*S, h, false);
        return true;
    }
};

struct RayWalk {
    int32_t world; const sp_uquery *q; const b2BroadPhase *bp; Scratch *S; b2Vec2 o, dir; float L;
    bool closest; float best = 2.0f;               /* closest mode: the current clip fraction of L */
    float RayCastCallback(const b2RayCastInput &input, int32_t proxyId) { b2FixtureProxy *fp = (b2FixtureProxy *)bp->GetUserData(proxyId); return Visit(input, fp->fixture, fp->childIndex); }
    float Visit(const b2RayCastInput &input, b2Fixture *f, int32_t child)
    {
        Meta m;
        if (!pass(world, q, f, &m)) return input.maxFraction;
        if (scr_skipped(*S, m.user)) return input.maxFraction;
        float t; b2Vec2 p, n;
        bool rounded = m.ur > 0.0f && f->GetType() != b2Shape::e_circle;
        if (rounded) {
            b2CircleShape pt; pt.m_radius = 0.0f; pt.m_p.SetZero();
            b2DistanceProxy A; A.Set(&pt, 0);
            if (sweep(A, 0.0f, o, 0.0f, dir, input.maxFraction * L, f, child, m.ur, &t, &p, &n) != 1) return input.maxFraction;
        } else {
            b2RayCastOutput out;
            if (!f->RayCast(&out, input, child)) return input.maxFraction;
            t = out.fraction * L; p = input.p1 + out.fraction * (input.p2 - input.p1); n = out.normal;
        }
        int32_t at = scr_find(*S, m.user);
        sp_uhit h; memset(&h, 0, sizeof h);
        h.shape = m.shape; h.user = m.user; h.cx = p.x; h.cy = p.y; h.px = p.x; h.py = p.y; h.nx = n.x; h.ny = n.y; h.distance = t;
        if (at >= 0) { if (!S->start[at] && t < S->hit[at].distance) S->hit[at] = h; }
        else scr_add(*S, h, false);
        if (closest) { float fr = t / L; if (fr < best) best = fr; return best; }
        return input.maxFraction;
    }
};

/* ray start-inside: the colliders containing the origin */
struct StartWalk {
    int32_t world; const sp_uquery *q; const b2BroadPhase *bp; Scratch *S; b2Vec2 o, dir; bool start_in;
    bool QueryCallback(int32_t proxyId) { b2FixtureProxy *fp = (b2FixtureProxy *)bp->GetUserData(proxyId); return Visit(fp->fixture, fp->childIndex); }
    bool Visit(b2Fixture *f, int32_t child)
    {
        Meta m;
        if (!pass(world, q, f, &m)) return true;
        if (scr_find(*S, m.user) >= 0 || scr_skipped(*S, m.user)) return true;
        if (!contains(f, child, m.ur, o)) return true;
        if (!start_in) { if (qgrow(S->skip, S->capskip, S->nskip + 1)) S->skip[S->nskip++] = m.user; return true; }
        sp_uhit h; memset(&h, 0, sizeof h);
        h.shape = m.shape; h.user = m.user; h.cx = o.x; h.cy = o.y; h.px = o.x; h.py = o.y; h.nx = 0.0f - dir.x; h.ny = 0.0f - dir.y; h.distance = 0.0f;   /* E1: +0 */
        scr_add(*S, h, true);
        return true;
    }
};

/* SP_UQF_BRUTE (SHEPHERD_CHECK_PHYS=brute): the same walkers over every fixture child in body-list order, no broadphase -- the
 * check that the broadphase-direct shape loses nothing (the plan's "brute oracle"). The tight AABB of each child against the query's. */
template <typename W> void brute_query(b2World *w, W &walker, const b2AABB &a)
{
    for (b2Body *b = w->GetBodyList(); b; b = b->GetNext()) {
        if (!b->IsActive()) continue;
        for (b2Fixture *f = b->GetFixtureList(); f; f = f->GetNext())
            for (int32_t c = 0; c < f->GetShape()->GetChildCount(); ++c) {
                b2AABB fa; f->GetShape()->ComputeAABB(&fa, b->GetTransform(), c);
                const float r = f->GetShape()->m_radius;   /* b2ChainShape::ComputeAABB leaves its skin out: grow every child by its radius */
                fa.lowerBound -= b2Vec2(r, r); fa.upperBound += b2Vec2(r, r);
                if (!b2TestOverlap(fa, a)) continue;
                if (!walker.Visit(f, c)) return;
            }
    }
}
template <typename W> void brute_ray(b2World *w, W &walker, const b2RayCastInput &in)
{
    b2RayCastInput cur = in;
    for (b2Body *b = w->GetBodyList(); b; b = b->GetNext()) {
        if (!b->IsActive()) continue;
        for (b2Fixture *f = b->GetFixtureList(); f; f = f->GetNext())
            for (int32_t c = 0; c < f->GetShape()->GetChildCount(); ++c) {
                float v = walker.Visit(cur, f, c);
                if (v == 0.0f) return;
                if (v > 0.0f && v < cur.maxFraction) cur.maxFraction = v;
            }
    }
}

/* stable insertion sort of the hits by distance (ties keep broadphase / report order) */
void sort_hits(Scratch &S)
{
    for (int32_t i = 1; i < S.n; ++i) {
        sp_uhit h = S.hit[i]; uint8_t st = S.start[i];
        int32_t j = i - 1;
        while (j >= 0 && S.hit[j].distance > h.distance) { S.hit[j + 1] = S.hit[j]; S.start[j + 1] = S.start[j]; --j; }
        S.hit[j + 1] = h; S.start[j + 1] = st;
    }
}

} /* namespace */

extern "C" {

SP_API int32_t sp_u_query(int32_t world, const sp_uquery *q, sp_uhit *out, int32_t max)
{
    b2World *w = spi_world(world);
    int32_t s = slot_of(world);
    if (!w || s < 0 || s >= MAX_W) return SP_ERR_HANDLE;
    if (!q || (max > 0 && !out)) return SP_ERR_PARAM;
    if (w->IsLocked()) return SP_ERR_LOCKED;
    Scratch &S = g_scr[s];
    scr_reset(S);
    Probe P;
    if (!make_probe(q, P)) return SP_ERR_PARAM;
    const b2BroadPhase &bp = w->GetContactManager().m_broadPhase;
    b2Vec2 o(q->ox, q->oy);
    b2Vec2 dir(q->dx, q->dy);
    float dl = dir.Length();
    bool zero = !(dl > b2_epsilon) || !(q->distance > 0.0f);
    if (dl > b2_epsilon) dir *= 1.0f / dl; else dir.SetZero();

    if (q->kind == SP_UQ_POINT || q->kind == SP_UQ_CIRCLE || q->kind == SP_UQ_BOX || q->kind == SP_UQ_CAPSULE ||
        (q->kind == SP_UQ_RAY && zero)) {
        OverlapWalk W; W.world = world; W.q = q; W.bp = &bp; W.S = &S; W.max = max > 0 ? max : 1; W.P = &P;
        W.xf.Set(o, P.angle); W.point = o; W.is_point = (q->kind == SP_UQ_POINT || q->kind == SP_UQ_RAY);
        b2AABB a;
        if (W.is_point) { a.lowerBound = o; a.upperBound = o; }
        else P.shape->ComputeAABB(&a, W.xf, 0);
        if (q->flags & SP_UQF_BRUTE) brute_query(w, W, a); else bp.Query(&W, a);
        if (q->kind == SP_UQ_RAY) for (int32_t i = 0; i < S.n; ++i) { S.hit[i].nx = 0.0f - dir.x; S.hit[i].ny = 0.0f - dir.y; }
    } else if (q->kind == SP_UQ_RAY) {
        float L = clamp_length(world, w, S, o, q->distance, 0.0f);
        bool closest = !(q->flags & SP_UQF_ALL);
        StartWalk SW; SW.world = world; SW.q = q; SW.bp = &bp; SW.S = &S; SW.o = o; SW.dir = dir; SW.start_in = (q->flags & SP_UQF_START_IN) != 0;
        b2AABB pa; pa.lowerBound = o; pa.upperBound = o;
        if (q->flags & SP_UQF_BRUTE) brute_query(w, SW, pa); else bp.Query(&SW, pa);   /* start-inside first: those hits are at distance 0 */
        if (!(closest && S.n > 0)) {
            RayWalk RW; RW.world = world; RW.q = q; RW.bp = &bp; RW.S = &S; RW.o = o; RW.dir = dir; RW.L = L; RW.closest = closest;
            b2RayCastInput in; in.p1 = o; in.p2 = o + L * dir; in.maxFraction = 1.0f;
            if (q->flags & SP_UQF_BRUTE) brute_ray(w, RW, in); else bp.RayCast(&RW, in);
        }
        sort_hits(S);
        float fl = q->distance > 0.0f && q->distance < 1e30f ? q->distance : L;
        for (int32_t i = 0; i < S.n; ++i) S.hit[i].fraction = S.hit[i].distance / fl;
        if (closest && S.n > 1) S.n = 1;
    } else {   /* shape casts */
        float L = zero ? 0.0f : clamp_length(world, w, S, o, q->distance, P.extent);
        SweepWalk SW; SW.world = world; SW.q = q; SW.bp = &bp; SW.S = &S; SW.P = &P; SW.o = o; SW.dir = dir; SW.L = L; SW.zero = zero;
        SW.A.Set(P.shape, 0);
        b2Transform x0; x0.Set(o, P.angle);
        b2AABB a0, a1; P.shape->ComputeAABB(&a0, x0, 0);
        if (!zero) { b2Transform x1; x1.Set(o + L * dir, P.angle); P.shape->ComputeAABB(&a1, x1, 0); a0.Combine(a1); }
        if (q->flags & SP_UQF_BRUTE) brute_query(w, SW, a0); else bp.Query(&SW, a0);
        int32_t k = 0;   /* drop the hits of colliders found to overlap the start later (without START_IN) */
        for (int32_t i = 0; i < S.n; ++i) if (S.hit[i].distance >= 0.0f && !scr_skipped(S, S.hit[i].user)) { S.hit[k] = S.hit[i]; S.start[k] = S.start[i]; k++; }
        S.n = k;
        sort_hits(S);   /* (E1: each hit's fraction is the TOI's t, set by the walk) */
        if (!(q->flags & SP_UQF_ALL) && S.n > 1) S.n = 1;
    }
    for (int32_t i = 0; i < S.n && i < max; ++i) out[i] = S.hit[i];
    return S.n;
}

SP_API int32_t sp_u_bounds(int32_t world, const int32_t *shapes, int32_t n, float *out)
{
    if (!out || (n > 0 && !shapes)) return SP_ERR_PARAM;
    int32_t live = 0;
    b2AABB all; all.lowerBound.SetZero(); all.upperBound.SetZero();
    for (int32_t i = 0; i < n; ++i) {
        b2Fixture *f = spi_fixture(world, shapes[i]);
        int32_t user, layer; float ur;
        if (!f || !spi_shape_meta(world, shapes[i], &user, &layer, &ur)) continue;
        const b2Shape *sh = f->GetShape();
        const b2Transform &xf = f->GetBody()->GetTransform();
        /* the exact outline from the vertices (Box2D's ComputeAABB would add its skin), grown by the collider's own rounding */
        const float br = spi_shape_bradius(world, shapes[i]);   /* E1: a box's / edge's edgeRadius is not in Unity's bounds (RBox 1 | 1) */
        (void)ur;
        for (int32_t c = 0; c < sh->GetChildCount(); ++c) {
            b2Vec2 v[b2_maxPolygonVertices]; int32_t nv = 0; float r = br;
            switch (sh->GetType()) {
            case b2Shape::e_circle: { const b2CircleShape *cs = (const b2CircleShape *)sh; v[0] = cs->m_p; nv = 1; r = cs->m_radius; break; }
            case b2Shape::e_polygon: { const b2PolygonShape *ps = (const b2PolygonShape *)sh; for (int32_t k = 0; k < ps->m_count; ++k) v[k] = ps->m_vertices[k]; nv = ps->m_count; break; }
            case b2Shape::e_edge: { const b2EdgeShape *es = (const b2EdgeShape *)sh; v[0] = es->m_vertex1; v[1] = es->m_vertex2; nv = 2; break; }
            case b2Shape::e_chain: { b2EdgeShape e; ((const b2ChainShape *)sh)->GetChildEdge(&e, c); v[0] = e.m_vertex1; v[1] = e.m_vertex2; nv = 2; break; }
            default: break;
            }
            for (int32_t k = 0; k < nv; ++k) {
                b2Vec2 w = b2Mul(xf, v[k]);
                b2AABB a; a.lowerBound.Set(w.x - r, w.y - r); a.upperBound.Set(w.x + r, w.y + r);
                if (!live) all = a; else all.Combine(a);
                live++;
            }
        }
    }
    out[0] = all.lowerBound.x; out[1] = all.lowerBound.y; out[2] = all.upperBound.x; out[3] = all.upperBound.y;
    return live > 0 ? 1 : 0;
}

SP_API int32_t sp_u_test_point(int32_t world, const int32_t *shapes, int32_t n, const float *p)
{
    if (!p || (n > 0 && !shapes)) return SP_ERR_PARAM;
    b2Vec2 q(p[0], p[1]);
    for (int32_t i = 0; i < n; ++i) {
        b2Fixture *f = spi_fixture(world, shapes[i]);
        int32_t user, layer; float ur;
        if (!f || !spi_shape_meta(world, shapes[i], &user, &layer, &ur)) continue;
        for (int32_t c = 0; c < f->GetShape()->GetChildCount(); ++c) if (contains(f, c, ur, q)) return 1;
    }
    return 0;
}

SP_API int32_t sp_u_closest(int32_t world, const int32_t *shapes, int32_t n, const float *p, float *out)
{
    if (!p || !out || (n > 0 && !shapes)) return SP_ERR_PARAM;
    b2Vec2 q(p[0], p[1]);
    bool have = false; float best = 0.0f; b2Vec2 bp(0.0f, 0.0f);
    b2CircleShape pt; pt.m_radius = 0.0f; pt.m_p.SetZero();
    for (int32_t i = 0; i < n; ++i) {
        b2Fixture *f = spi_fixture(world, shapes[i]);
        int32_t user, layer; float ur;
        if (!f || !spi_shape_meta(world, shapes[i], &user, &layer, &ur)) continue;
        /* E1 (results/arm64-r1/Queries.csv closest / distance rows): Unity's ClosestPoint includes Box2D's skin -- (3, 0.5) against a 2 x 2 box
         * gives (1.01, 0.5): the shape's full radius (polygon / edge skin + its own rounding; a circle's radius) around its core */
        const float fr = f->GetShape()->m_radius;
        for (int32_t c = 0; c < f->GetShape()->GetChildCount(); ++c) {
            b2DistanceProxy A, B;
            A.Set(&pt, 0); A.m_radius = 0.0f;
            exact_proxy(B, f, c, 0.0f);
            b2DistanceInput in; in.proxyA = A; in.proxyB = B; in.transformA.Set(q, 0.0f); in.transformB = f->GetBody()->GetTransform(); in.useRadii = false;
            b2SimplexCache cache; cache.count = 0;
            b2DistanceOutput o; b2Distance(&o, &cache, &in);
            bool core_in = f->GetType() == b2Shape::e_polygon && f->TestPoint(q);
            if (core_in || o.distance <= fr) { out[0] = q.x; out[1] = q.y; out[2] = 0.0f; return 1; }
            float d = o.distance - fr;
            b2Vec2 cp = o.pointB;
            if (fr > 0.0f && o.distance > b2_epsilon) cp = o.pointB + (fr / o.distance) * (q - o.pointB);
            if (!have || d < best) { have = true; best = d; bp = cp; }
        }
    }
    if (!have) return 0;
    out[0] = bp.x; out[1] = bp.y; out[2] = best;
    return 1;
}

/* the exact distance between two colliders' shapes (no Box2D skin; each collider's own rounding kept): the minimum over their
 * children; negative when their rounded parts overlap (circles: the exact signed gap; overlapping cores give -(ra + rb)). The physics
 * Stage 2 swap A/B's classifier (Physics/PhysShadow.cs: "polygon skin" pairs, exact-touch ties). */
SP_API int32_t sp_u_distance(int32_t world, const int32_t *a, int32_t na, const int32_t *b, int32_t nb, float *out)
{
    if (!out || (na > 0 && !a) || (nb > 0 && !b)) return SP_ERR_PARAM;
    bool have = false; float best = 0.0f;
    for (int32_t i = 0; i < na; ++i) {
        b2Fixture *fa = spi_fixture(world, a[i]); int32_t ua, la; float ra;
        if (!fa || !spi_shape_meta(world, a[i], &ua, &la, &ra)) continue;
        for (int32_t j = 0; j < nb; ++j) {
            b2Fixture *fb = spi_fixture(world, b[j]); int32_t ub, lb; float rb;
            if (!fb || !spi_shape_meta(world, b[j], &ub, &lb, &rb)) continue;
            for (int32_t ci = 0; ci < fa->GetShape()->GetChildCount(); ++ci)
                for (int32_t cj = 0; cj < fb->GetShape()->GetChildCount(); ++cj) {
                    b2DistanceProxy A, B;
                    exact_proxy(A, fa, ci, 0.0f); exact_proxy(B, fb, cj, 0.0f);
                    b2DistanceInput in; in.proxyA = A; in.proxyB = B; in.transformA = fa->GetBody()->GetTransform(); in.transformB = fb->GetBody()->GetTransform(); in.useRadii = false;
                    b2SimplexCache c; c.count = 0;
                    b2DistanceOutput o; b2Distance(&o, &c, &in);
                    float d = o.distance - ra - rb;   /* signed: < 0 when the rounded parts overlap (overlapping cores: -(ra + rb)) */
                    if (!have || d < best) { have = true; best = d; }
                }
        }
    }
    if (!have) return 0;
    out[0] = best;
    return 1;
}

/* the colliders overlapping a collider's shapes (Physics2D.OverlapCollider): the broadphase walked with each shape child's AABB and
 * b2TestOverlap per proxy (skin kept, H-QSKIN); the query's filter (layer mask, triggers, exclude_user = the collider itself) */
struct ShapeWalk {
    int32_t world; const sp_uquery *q; const b2BroadPhase *bp; Scratch *S; const b2Fixture *f; int32_t own;
    bool QueryCallback(int32_t proxyId) { b2FixtureProxy *fp = (b2FixtureProxy *)bp->GetUserData(proxyId); return Visit(fp->fixture, fp->childIndex); }
    bool Visit(b2Fixture *o, int32_t child)
    {
        if (o == f) return true;
        Meta m;
        if (!pass(world, q, o, &m)) return true;
        if (scr_find(*S, m.user) >= 0) return true;
        if (!b2TestOverlap(f->GetShape(), own, o->GetShape(), child, f->GetBody()->GetTransform(), o->GetBody()->GetTransform())) return true;
        sp_uhit h; memset(&h, 0, sizeof h);
        h.shape = m.shape; h.user = m.user;
        scr_add(*S, h, false);
        return true;
    }
};
SP_API int32_t sp_u_overlap_shapes(int32_t world, const int32_t *shapes, int32_t n, const sp_uquery *q, sp_uhit *out, int32_t max)
{
    b2World *w = spi_world(world);
    int32_t s = slot_of(world);
    if (!w || s < 0 || s >= MAX_W) return SP_ERR_HANDLE;
    if (!q || (n > 0 && !shapes) || (max > 0 && !out)) return SP_ERR_PARAM;
    Scratch &S = g_scr[s];
    scr_reset(S);
    const b2BroadPhase &bp = w->GetContactManager().m_broadPhase;
    for (int32_t i = 0; i < n; ++i) {
        b2Fixture *f = spi_fixture(world, shapes[i]);
        if (!f) continue;
        for (int32_t c = 0; c < f->GetShape()->GetChildCount(); ++c) {
            ShapeWalk W; W.world = world; W.q = q; W.bp = &bp; W.S = &S; W.f = f; W.own = c;
            b2AABB a; f->GetShape()->ComputeAABB(&a, f->GetBody()->GetTransform(), c);
            bp.Query(&W, a);
        }
    }
    for (int32_t i = 0; i < S.n && i < max; ++i) out[i] = S.hit[i];
    return S.n;
}

/* SHEPHERD_CHECK_PHYS=brute: the touching sensor pairs the core holds after a step against a brute-force evaluation of every fixture-child
 * pair (no broadphase): the body rule (P1's: one non-static body with kinematic triggers, else one dynamic), the world's contact filter
 * (the Unity filter), Box2D's awake rule (one awake non-static body: pairs it does not update are left out of both), b2TestOverlap.
 * out[0..5] = {core pairs, brute pairs, missing in the core, extra in the core, first difference's shape a, shape b}. Returns the
 * number of differences. Solid pairs are not compared (their touching is the solver's manifold, not an overlap test). */
struct PKey { int32_t a, ca, b, cb; };
static bool pk_less(const PKey &x, const PKey &y) { return x.a != y.a ? x.a < y.a : x.ca != y.ca ? x.ca < y.ca : x.b != y.b ? x.b < y.b : x.cb < y.cb; }
static PKey pk(int32_t a, int32_t ca, int32_t b, int32_t cb) { PKey k; if (a < b || (a == b && ca <= cb)) { k.a = a; k.ca = ca; k.b = b; k.cb = cb; } else { k.a = b; k.ca = cb; k.b = a; k.cb = ca; } return k; }
static void pk_sort(PKey *v, int32_t n)   /* insertion sort is enough for the counts a check meets; heap-free */
{
    for (int32_t i = 1; i < n; ++i) { PKey t = v[i]; int32_t j = i - 1; while (j >= 0 && pk_less(t, v[j])) { v[j + 1] = v[j]; --j; } v[j + 1] = t; }
}
SP_API int32_t sp_u_brute_pairs(int32_t world, int32_t *out)
{
    b2World *w = spi_world(world);
    if (!w || !out) return SP_ERR_PARAM;
    const bool kt = (spi_world_flags(world) & SP_WORLD_KINEMATIC_TRIGGERS) != 0;
    b2ContactFilter *filter = w->GetContactManager().m_contactFilter;
    struct Ch { b2Fixture *f; int32_t c; b2AABB a; };
    int32_t nch = 0, cap = 0; Ch *ch = nullptr;
    for (b2Body *b = w->GetBodyList(); b; b = b->GetNext()) {
        if (!b->IsActive()) continue;
        for (b2Fixture *f = b->GetFixtureList(); f; f = f->GetNext())
            for (int32_t c = 0; c < f->GetShape()->GetChildCount(); ++c) {
                if (!qgrow(ch, cap, nch + 1)) { free(ch); return SP_ERR_FULL; }
                ch[nch].f = f; ch[nch].c = c; f->GetShape()->ComputeAABB(&ch[nch].a, b->GetTransform(), c);
                const float r = f->GetShape()->m_radius;   /* (chain children's AABBs leave the skin out) */
                ch[nch].a.lowerBound -= b2Vec2(r, r); ch[nch].a.upperBound += b2Vec2(r, r);
                nch++;
            }
    }
    int32_t ncore = 0, capc = 0, nbr = 0, capb = 0; PKey *core = nullptr, *br = nullptr;
    auto active = [](const b2Body *b) { return b->IsAwake() && b->GetType() != b2_staticBody; };
    for (b2Contact *c = w->GetContactList(); c; c = c->GetNext()) {
        b2Fixture *fa = c->GetFixtureA(), *fb = c->GetFixtureB();
        if (!(fa->IsSensor() || fb->IsSensor()) || !c->IsTouching()) continue;
        if (!active(fa->GetBody()) && !active(fb->GetBody())) continue;
        if (qgrow(core, capc, ncore + 1)) core[ncore++] = pk((int32_t)(intptr_t)fa->GetUserData(), c->GetChildIndexA(), (int32_t)(intptr_t)fb->GetUserData(), c->GetChildIndexB());
    }
    for (int32_t i = 0; i < nch; ++i)
        for (int32_t j = i + 1; j < nch; ++j) {
            b2Fixture *fa = ch[i].f, *fb = ch[j].f;
            if (!(fa->IsSensor() || fb->IsSensor())) continue;
            b2Body *ba = fa->GetBody(), *bb = fb->GetBody();
            if (ba == bb) continue;
            if (kt) { if (ba->GetType() == b2_staticBody && bb->GetType() == b2_staticBody) continue; }
            else if (ba->GetType() != b2_dynamicBody && bb->GetType() != b2_dynamicBody) continue;
            if (!active(ba) && !active(bb)) continue;
            if (!b2TestOverlap(ch[i].a, ch[j].a)) continue;
            if (filter && !filter->ShouldCollide(fa, fb)) continue;
            if (!b2TestOverlap(fa->GetShape(), ch[i].c, fb->GetShape(), ch[j].c, ba->GetTransform(), bb->GetTransform())) continue;
            if (qgrow(br, capb, nbr + 1)) br[nbr++] = pk((int32_t)(intptr_t)fa->GetUserData(), ch[i].c, (int32_t)(intptr_t)fb->GetUserData(), ch[j].c);
        }
    pk_sort(core, ncore); pk_sort(br, nbr);
    int32_t miss = 0, extra = 0, i = 0, j = 0; bool first = true;
    out[4] = out[5] = 0;
    while (i < ncore || j < nbr) {
        if (j >= nbr || (i < ncore && pk_less(core[i], br[j]))) { extra++; if (first) { out[4] = core[i].a; out[5] = core[i].b; first = false; } ++i; }
        else if (i >= ncore || pk_less(br[j], core[i])) { miss++; if (first) { out[4] = br[j].a; out[5] = br[j].b; first = false; } ++j; }
        else { ++i; ++j; }
    }
    out[0] = ncore; out[1] = nbr; out[2] = miss; out[3] = extra;
    free(ch); free(core); free(br);
    return miss + extra;
}

SP_API int32_t sp_u_contacts_of(int32_t world, const int32_t *shapes, int32_t n, int32_t flags, int32_t *other, int32_t max)
{
    if (n > 0 && !shapes) return SP_ERR_PARAM;
    int32_t k = 0;
    for (int32_t i = 0; i < n; ++i) {
        b2Fixture *f = spi_fixture(world, shapes[i]);
        if (!f) continue;
        for (b2ContactEdge *e = f->GetBody()->GetContactList(); e; e = e->next) {
            b2Contact *c = e->contact;
            if (!c->IsTouching()) continue;
            b2Fixture *fa = c->GetFixtureA(), *fb = c->GetFixtureB(), *o;
            if (fa == f) o = fb; else if (fb == f) o = fa; else continue;
            if ((flags & SP_CONTACTS_NO_SENSORS) && (fa->IsSensor() || fb->IsSensor())) continue;
            if (other && k < max) other[k] = (int32_t)(intptr_t)o->GetUserData();
            k++;
        }
    }
    return k < max ? k : max;
}

} /* extern "C" */
