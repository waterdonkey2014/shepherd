/* sp_mesh3d.cpp -- physics Stage 6 of libshepphys (physics-plan-v2 section 4 "Stage 6: island raycast", section 2 "Stage 6"): the 3D
 * island-ground ray. Ours, not Box2D (no Box2D code is used beyond b2Alloc / b2Free, B2's arena).
 *
 * The game's only 3D physics on the intro -> dungeon -> base path is a ray: UnitObject.LateUpdate's ground lock (every unit, every logic
 * tick: Physics.Raycast from 10 u behind the unit along +z; a MeshCollider hit sets the unit's z to the hit point's z, no hit sets z = 0),
 * OnGround's Island-layer ray, the followers' RaycastNonAlloc every 30 frames, CompanionBaseArea.StickToFloor. Unity answers them with
 * PhysX (Unity 2022.3: PhysX 4.1); this file answers them from the same geometry with PhysX's arithmetic as far as it is known here --
 * from memory of PhysX's sources (DOCUMENTED, not verified; the editor oracle E1 can settle it bit for bit):
 *   - the ray into a mesh's vertex space: PxTransform::transformInv / PxQuat::rotateInv, then the PxMeshScale's inverse (PxMat33's
 *     cofactor inverse of diag(scale)) -- raycast_triangleMesh;
 *   - the triangle test: intersectRayTriangle / BV4's RayTriOverlap -- Moller-Trumbore, backface culling unless the query hits both sides,
 *     the culling epsilon FLT_EPSILON^2 on det, barycentric limits enlarged by the mesh's geomEpsilon (2^-22 x the largest |coordinate| of
 *     its bounds, TriangleMeshBuilder::computeLocalBounds), u / v / t by one reciprocal of det;
 *   - the closest triangle wins (strictly smaller t; PhysX's tree order breaks exact ties, here: the lower triangle index);
 *   - the hit point (H-RAYPOINT, default "bary"): pose.transform(vertex2world x ((1 - u - v) p0 + u p1 + v p2)) -- RayMeshColliderCallback;
 *     "ray" (SP_R3F_POINT_RAY): origin + dir t;
 *   - the normal: pose.rotate(transpose(world2vertex) x ((p1 - p0) x (p2 - p0))), normalised as PxVec3::normalize (one reciprocal);
 *   - Box: rayAABBIntersect2 in the box's frame (RAYAABB_EPSILON 1e-5); Sphere: intersectRaySphere (the origin moved to 10 u from the
 *     surface first, GU_RAY_SURFACE_OFFSET); Capsule: a ray against the swept sphere; a ray starting inside any of the three reports it at
 *     t = 0 in PhysX, which Unity's Physics.Raycast does not report (its documentation) -- dropped here.
 * Memory: b2Alloc (B2's arena; malloc where B2 is off). A ray allocates nothing except a mesh collider's +z grid, built at the first +z
 * ray after the collider or its mesh changed. Not thread-safe (USER_0 only, like every physics call). */
#include "shepphys.h"
#include "sp_trace.h"

#include <Box2D/Common/b2Settings.h>   /* b2Alloc / b2Free (B2: libshepphys's arena) */
#include <float.h>
#include <math.h>
#include <string.h>

static_assert(sizeof(sp_mesh3d_def) == 16, "sp_mesh3d_def");
static_assert(sizeof(sp_col3d_def) == 88, "sp_col3d_def");
static_assert(sizeof(sp_ray3d_query) == 40, "sp_ray3d_query");
static_assert(sizeof(sp_ray3d_hit) == 48, "sp_ray3d_hit");

namespace {

/* ================================================================ PhysX-shaped arithmetic (PxVec3 / PxQuat / PxMat33 / PxTransform)
 * Every expression keeps PhysX's operation order (the build has -ffp-contract=off: no fused multiply-add on either platform). */
struct V3 { float x, y, z; };
inline V3 v3(float x, float y, float z) { V3 r; r.x = x; r.y = y; r.z = z; return r; }
inline V3 add(V3 a, V3 b) { return v3(a.x + b.x, a.y + b.y, a.z + b.z); }
inline V3 sub(V3 a, V3 b) { return v3(a.x - b.x, a.y - b.y, a.z - b.z); }
inline V3 mulf(V3 a, float f) { return v3(a.x * f, a.y * f, a.z * f); }   /* PxVec3 * f and f * PxVec3 */
inline float dot(V3 a, V3 b) { return a.x * b.x + a.y * b.y + a.z * b.z; }
inline V3 cross(V3 a, V3 b) { return v3(a.y * b.z - a.z * b.y, a.z * b.x - a.x * b.z, a.x * b.y - a.y * b.x); }
inline V3 neg(V3 a) { return v3(-a.x, -a.y, -a.z); }
inline float at(V3 a, int i) { return i == 0 ? a.x : i == 1 ? a.y : a.z; }
inline void setat(V3 *a, int i, float f) { if (i == 0) a->x = f; else if (i == 1) a->y = f; else a->z = f; }
/* PxVec3::normalize: m = |v|^2; if m > 0: v /= sqrt(m) (PxVec3::operator/= multiplies by the reciprocal) */
inline V3 normalize(V3 v)
{
    float m = dot(v, v);
    if (m > 0.0f) { float f = 1.0f / sqrtf(m); v = v3(v.x * f, v.y * f, v.z * f); }
    return v;
}

struct Q { float x, y, z, w; };
/* PxQuat::rotate */
inline V3 qrot(Q q, V3 v)
{
    const float vx = 2.0f * v.x, vy = 2.0f * v.y, vz = 2.0f * v.z;
    const float w2 = q.w * q.w - 0.5f;
    const float dot2 = (q.x * vx + q.y * vy + q.z * vz);
    return v3((vx * w2 + (q.y * vz - q.z * vy) * q.w + q.x * dot2), (vy * w2 + (q.z * vx - q.x * vz) * q.w + q.y * dot2),
              (vz * w2 + (q.x * vy - q.y * vx) * q.w + q.z * dot2));
}
/* PxQuat::rotateInv */
inline V3 qrotinv(Q q, V3 v)
{
    const float vx = 2.0f * v.x, vy = 2.0f * v.y, vz = 2.0f * v.z;
    const float w2 = q.w * q.w - 0.5f;
    const float dot2 = (q.x * vx + q.y * vy + q.z * vz);
    return v3((vx * w2 - (q.y * vz - q.z * vy) * q.w + q.x * dot2), (vy * w2 - (q.z * vx - q.x * vz) * q.w + q.y * dot2),
              (vz * w2 - (q.x * vy - q.y * vx) * q.w + q.z * dot2));
}
/* PxQuat * PxQuat */
inline Q qmul(Q a, Q q)
{
    const float tx = a.w * q.x + q.w * a.x + a.y * q.z - q.y * a.z;
    const float ty = a.w * q.y + q.w * a.y + a.z * q.x - q.z * a.x;
    const float tz = a.w * q.z + q.w * a.z + a.x * q.y - q.x * a.y;
    Q r; r.w = a.w * q.w - q.x * a.x - a.y * q.y - q.z * a.z; r.x = tx; r.y = ty; r.z = tz;
    return r;
}

/* PxMat33 (column-major: c0, c1, c2) */
struct M3 { V3 c0, c1, c2; };
inline V3 mtransform(const M3 &m, V3 v) { return add(add(mulf(m.c0, v.x), mulf(m.c1, v.y)), mulf(m.c2, v.z)); }
inline V3 mtransformT(const M3 &m, V3 v) { return v3(dot(m.c0, v), dot(m.c1, v), dot(m.c2, v)); }
inline M3 mmul(const M3 &a, const M3 &b) { M3 r; r.c0 = mtransform(a, b.c0); r.c1 = mtransform(a, b.c1); r.c2 = mtransform(a, b.c2); return r; }
inline M3 mtranspose(const M3 &m) { M3 r; r.c0 = v3(m.c0.x, m.c1.x, m.c2.x); r.c1 = v3(m.c0.y, m.c1.y, m.c2.y); r.c2 = v3(m.c0.z, m.c1.z, m.c2.z); return r; }
/* PxMat33(const PxQuat&) */
inline M3 mfromq(Q q)
{
    const float x = q.x, y = q.y, z = q.z, w = q.w;
    const float x2 = x + x, y2 = y + y, z2 = z + z;
    const float xx = x2 * x, yy = y2 * y, zz = z2 * z, xy = x2 * y, xz = x2 * z, xw = x2 * w, yz = y2 * z, yw = y2 * w, zw = z2 * w;
    M3 r;
    r.c0 = v3(1.0f - yy - zz, xy + zw, xz - yw);
    r.c1 = v3(xy - zw, 1.0f - xx - zz, yz + xw);
    r.c2 = v3(xz + yw, yz - xw, 1.0f - xx - yy);
    return r;
}
/* PxMat33::getInverse (the cofactor form; identity when det == 0) */
inline M3 minverse(const M3 &m)
{
    const float det = dot(m.c0, cross(m.c1, m.c2));
    M3 r;
    if (det != 0) {
        const float invDet = 1.0f / det;
        r.c0.x = invDet * (m.c1.y * m.c2.z - m.c2.y * m.c1.z);
        r.c0.y = invDet * -(m.c0.y * m.c2.z - m.c2.y * m.c0.z);
        r.c0.z = invDet * (m.c0.y * m.c1.z - m.c0.z * m.c1.y);
        r.c1.x = invDet * -(m.c1.x * m.c2.z - m.c1.z * m.c2.x);
        r.c1.y = invDet * (m.c0.x * m.c2.z - m.c0.z * m.c2.x);
        r.c1.z = invDet * -(m.c0.x * m.c1.z - m.c0.z * m.c1.x);
        r.c2.x = invDet * (m.c1.x * m.c2.y - m.c1.y * m.c2.x);
        r.c2.y = invDet * -(m.c0.x * m.c2.y - m.c0.y * m.c2.x);
        r.c2.z = invDet * (m.c0.x * m.c1.y - m.c1.x * m.c0.y);
        return r;
    }
    r.c0 = v3(1, 0, 0); r.c1 = v3(0, 1, 0); r.c2 = v3(0, 0, 1);
    return r;
}
/* PxMeshScale(scale, identity rotation).toMat33(): rot^T with its columns scaled, times rot */
inline M3 meshscale_mat(V3 s)
{
    Q id; id.x = 0.0f; id.y = 0.0f; id.z = 0.0f; id.w = 1.0f;
    M3 rot = mfromq(id), tr = mtranspose(rot);
    tr.c0 = mulf(tr.c0, s.x); tr.c1 = mulf(tr.c1, s.y); tr.c2 = mulf(tr.c2, s.z);
    return mmul(tr, rot);
}

/* ================================================================ objects */
const float kCullEps = FLT_EPSILON * FLT_EPSILON;   /* BV4's GU_CULLING_EPSILON_RAY_TRIANGLE (from memory) */

struct Node { float mn[3], mx[3]; int32_t a, n; };   /* leaf: n > 0 triangles order[a .. a + n); inner: n == 0, children a, a + 1 */

struct Mesh {
    int32_t nv, nt;
    float *v;            /* 3 nv */
    int32_t *t;          /* 3 nt */
    Node *nodes; int32_t nn;
    int32_t *order;      /* nt: triangle indices in BVH leaf order */
    float mn[3], mx[3];  /* bounds (vertex space) */
    float geps;          /* PhysX's mGeomEpsilon: 2^-22 x the largest |coordinate| of the bounds */
    uint16_t gen; bool live;
};

struct Col {
    sp_col3d_def d;
    int32_t serial;      /* creation order (the tie order across colliders) */
    uint16_t gen; bool live;
    /* derived */
    Q q; V3 p;           /* the shape's pose (primitives: the transform pose with the scaled centre applied) */
    M3 s2w, w2s;         /* mesh: vertex2worldSkew = PxMeshScale.toMat33(), world2vertex = its getInverse() */
    bool ident;          /* mesh: PxMeshScale::isIdentity (scale exactly (1, 1, 1)) */
    V3 he;               /* box: half extents; sphere: (r, 0, 0); capsule: (r, half height of the segment, 0) */
    int32_t axis;        /* capsule: the segment's axis in the shape frame */
    float wmn[3], wmx[3];/* world AABB, inflated (both paths' cull) */
    bool aabb_ok;
    /* mesh: the +z fast path's grid -- this collider's triangles a +z ray can hit, binned by their world xy boxes (built at the first +z
     * ray after the collider or its mesh changed) */
    bool grid_ok;
    float gx0, gy0, ginv; int32_t gnx, gny;
    int32_t *gstart;     /* gnx * gny + 1 */
    int32_t *gitems;     /* triangle indices */
    int32_t gcap_start, gcap_items, gentries;
};

struct Scene {
    Col *cols; int32_t ncol, capcol;
    Mesh *meshes; int32_t nmesh, capmesh;
    int32_t serial;
    uint32_t layers;     /* OR of 1 << layer over the live colliders */
    bool layers_ok;
    int32_t prims;       /* live primitive colliders */
    /* stats */
    int32_t rays, early, fast, general, tritests, primtests, gridbuilds, hits, triangles, bvhnodes;
    uint16_t gen; bool live;
};

const int kMaxScenes = 8;
Scene g_scenes[kMaxScenes];   /* zero-initialised */

inline int32_t mk(int32_t slot, uint16_t gen) { return (int32_t)(((uint32_t)gen << 16) | (uint32_t)(slot + 1)); }
inline int32_t slot_of(int32_t h) { return (int32_t)(h & 0xFFFF) - 1; }
inline uint16_t gen_of(int32_t h) { return (uint16_t)((uint32_t)h >> 16); }

Scene *get_scene(int32_t h)
{
    if (h <= 0) return nullptr;
    int32_t s = slot_of(h);
    if (s < 0 || s >= kMaxScenes) return nullptr;
    Scene *S = &g_scenes[s];
    return S->live && S->gen == gen_of(h) ? S : nullptr;
}
Mesh *get_mesh(Scene *S, int32_t h)
{
    if (!S || h <= 0) return nullptr;
    int32_t s = slot_of(h);
    if (s < 0 || s >= S->nmesh) return nullptr;
    Mesh *M = &S->meshes[s];
    return M->live && M->gen == gen_of(h) ? M : nullptr;
}
Col *get_col(Scene *S, int32_t h)
{
    if (!S || h <= 0) return nullptr;
    int32_t s = slot_of(h);
    if (s < 0 || s >= S->ncol) return nullptr;
    Col *C = &S->cols[s];
    return C->live && C->gen == gen_of(h) ? C : nullptr;
}

void *alloc(size_t n) { return n ? b2Alloc((int32)n) : nullptr; }
void dealloc(void *p) { if (p) b2Free(p); }
template <class T> bool grow(T **arr, int32_t *cap, int32_t need)
{
    if (need <= *cap) return true;
    int32_t nc = *cap ? *cap : 8;
    while (nc < need) nc *= 2;
    T *na = (T *)alloc((size_t)nc * sizeof(T));
    if (!na) return false;
    memset(na, 0, (size_t)nc * sizeof(T));
    if (*arr) { memcpy(na, *arr, (size_t)*cap * sizeof(T)); dealloc(*arr); }
    *arr = na; *cap = nc;
    return true;
}

void free_mesh(Mesh *M) { dealloc(M->v); dealloc(M->t); dealloc(M->nodes); dealloc(M->order); M->v = nullptr; M->t = nullptr; M->nodes = nullptr; M->order = nullptr; }
void free_grid(Col *C) { dealloc(C->gstart); dealloc(C->gitems); C->gstart = nullptr; C->gitems = nullptr; C->gcap_start = C->gcap_items = 0; C->gentries = 0; C->gnx = C->gny = 0; C->grid_ok = false; }


/* ================================================================ the BVH (vertex space; median split on the longest centroid axis) */
struct Build { Mesh *M; const float *cen; int32_t nn; int32_t *tmp; };

inline bool cen_less(const Build *B, int32_t a, int32_t b, int ax)
{
    float ca = B->cen[3 * a + ax], cb = B->cen[3 * b + ax];
    return ca < cb || (ca == cb && a < b);   /* a total order: the result is unique, so the build is deterministic */
}
/* bottom-up merge sort of o[0..n) by (centroid[ax], index) */
void sort_axis(Build *B, int32_t *o, int32_t n, int ax)
{
    int32_t *src = o, *dst = B->tmp;
    for (int32_t w = 1; w < n; w *= 2) {
        for (int32_t lo = 0; lo < n; lo += 2 * w) {
            int32_t mid = lo + w < n ? lo + w : n, hi = lo + 2 * w < n ? lo + 2 * w : n, i = lo, j = mid, k = lo;
            while (i < mid && j < hi) dst[k++] = cen_less(B, src[j], src[i], ax) ? src[j++] : src[i++];
            while (i < mid) dst[k++] = src[i++];
            while (j < hi) dst[k++] = src[j++];
        }
        int32_t *t = src; src = dst; dst = t;
    }
    if (src != o) memcpy(o, src, (size_t)n * sizeof(int32_t));
}
void tri_bounds(const Mesh *M, int32_t ti, float mn[3], float mx[3])
{
    const int32_t *t = M->t + 3 * ti;
    for (int k = 0; k < 3; ++k) { mn[k] = FLT_MAX; mx[k] = -FLT_MAX; }
    for (int j = 0; j < 3; ++j)
        for (int k = 0; k < 3; ++k) { float c = M->v[3 * t[j] + k]; if (c < mn[k]) mn[k] = c; if (c > mx[k]) mx[k] = c; }
}
/* a node box's inflation: generous against the triangle test's rounding and geomEpsilon's barycentric enlargement (the BVH only prunes;
 * a box too large costs tests, never a result) */
inline float infl(const float mn[3], const float mx[3], float geps)
{
    float e = 0.0f;
    for (int k = 0; k < 3; ++k) { float a = fabsf(mn[k]), b = fabsf(mx[k]); if (a > e) e = a; if (b > e) e = b; }
    return e * 1e-5f + geps * (e + 1.0f) * 4.0f + 1e-6f;
}
void build_into(Build *B, int32_t id, int32_t a, int32_t n, int32_t depth)
{
    Mesh *M = B->M;
    float mn[3] = {FLT_MAX, FLT_MAX, FLT_MAX}, mx[3] = {-FLT_MAX, -FLT_MAX, -FLT_MAX};
    float cmn[3] = {FLT_MAX, FLT_MAX, FLT_MAX}, cmx[3] = {-FLT_MAX, -FLT_MAX, -FLT_MAX};
    for (int32_t i = a; i < a + n; ++i) {
        float tm[3], tx[3];
        tri_bounds(M, M->order[i], tm, tx);
        for (int k = 0; k < 3; ++k) {
            if (tm[k] < mn[k]) mn[k] = tm[k];
            if (tx[k] > mx[k]) mx[k] = tx[k];
            float c = B->cen[3 * M->order[i] + k];
            if (c < cmn[k]) cmn[k] = c;
            if (c > cmx[k]) cmx[k] = c;
        }
    }
    Node *N = &M->nodes[id];
    float e = infl(mn, mx, M->geps);
    for (int k = 0; k < 3; ++k) { N->mn[k] = mn[k] - e; N->mx[k] = mx[k] + e; }
    if (n <= 4 || depth >= 40) { N->a = a; N->n = n; return; }
    int ax = 0;
    float best = cmx[0] - cmn[0];
    for (int k = 1; k < 3; ++k) if (cmx[k] - cmn[k] > best) { best = cmx[k] - cmn[k]; ax = k; }
    sort_axis(B, M->order + a, n, ax);
    int32_t h = n / 2, l = B->nn;
    B->nn += 2;
    N->a = l; N->n = 0;
    build_into(B, l, a, h, depth + 1);
    build_into(B, l + 1, a + h, n - h, depth + 1);
}

/* ================================================================ colliders */
inline V3 cv(const float *f) { return v3(f[0], f[1], f[2]); }
inline float absmax3(float a, float b, float c) { a = fabsf(a); b = fabsf(b); c = fabsf(c); return a > b ? (a > c ? a : c) : (b > c ? b : c); }

void aabb_add(float mn[3], float mx[3], V3 p)
{
    for (int k = 0; k < 3; ++k) { float c = at(p, k); if (c < mn[k]) mn[k] = c; if (c > mx[k]) mx[k] = c; }
}
void aabb_inflate(float mn[3], float mx[3], float extra)
{
    float e = 0.0f;
    for (int k = 0; k < 3; ++k) { float a = fabsf(mn[k]), b = fabsf(mx[k]); if (a > e) e = a; if (b > e) e = b; }
    e = e * 1e-5f + extra + 1e-5f;
    for (int k = 0; k < 3; ++k) { mn[k] -= e; mx[k] += e; }
}

/* the shape's pose and scale terms from the collider's transform (Unity: the actor at the transform's world pose; a primitive's centre
 * scaled by the lossy scale is the shape's local pose; a mesh's scale is its PxMeshScale) */
void derive(Scene *S, Col *C)
{
    const sp_col3d_def &d = C->d;
    Q q; q.x = d.qx; q.y = d.qy; q.z = d.qz; q.w = d.qw;
    V3 p = v3(d.px, d.py, d.pz), s = v3(d.sx, d.sy, d.sz);
    float mn[3] = {FLT_MAX, FLT_MAX, FLT_MAX}, mx[3] = {-FLT_MAX, -FLT_MAX, -FLT_MAX};
    C->aabb_ok = false;
    C->grid_ok = false;   /* (rebuilt at the next +z ray) */
    if (d.kind == SP_C3_MESH) {
        C->q = q; C->p = p;
        C->ident = s.x == 1.0f && s.y == 1.0f && s.z == 1.0f;
        C->s2w = meshscale_mat(s);
        C->w2s = minverse(C->s2w);
        Mesh *M = get_mesh(S, d.mesh);
        if (M && M->nt > 0) {
            for (int i = 0; i < 8; ++i) {
                V3 c = v3((i & 1) ? M->mx[0] : M->mn[0], (i & 2) ? M->mx[1] : M->mn[1], (i & 4) ? M->mx[2] : M->mn[2]);
                V3 w = add(qrot(q, C->ident ? c : mtransform(C->s2w, c)), p);
                aabb_add(mn, mx, w);
            }
            aabb_inflate(mn, mx, M->geps * (absmax3(s.x, s.y, s.z) + 1.0f) * 4.0f * (absmax3(M->mx[0] - M->mn[0], M->mx[1] - M->mn[1], M->mx[2] - M->mn[2]) + 1.0f));
            C->aabb_ok = true;
        }
    } else {
        /* actorPose * PxTransform(centre x scale): q' = q * identity, p' = q.rotate(c) + p */
        Q id; id.x = 0.0f; id.y = 0.0f; id.z = 0.0f; id.w = 1.0f;
        V3 c = v3(d.cx * d.sx, d.cy * d.sy, d.cz * d.sz);
        C->q = qmul(q, id);
        C->p = add(qrot(q, c), p);
        float ax = fabsf(d.sx), ay = fabsf(d.sy), az = fabsf(d.sz);
        V3 ext;
        if (d.kind == SP_C3_BOX) {
            C->he = v3(fabsf(d.ex * d.sx) * 0.5f, fabsf(d.ey * d.sy) * 0.5f, fabsf(d.ez * d.sz) * 0.5f);
            /* |R| he */
            M3 R = mfromq(C->q);
            ext = v3(fabsf(R.c0.x) * C->he.x + fabsf(R.c1.x) * C->he.y + fabsf(R.c2.x) * C->he.z,
                     fabsf(R.c0.y) * C->he.x + fabsf(R.c1.y) * C->he.y + fabsf(R.c2.y) * C->he.z,
                     fabsf(R.c0.z) * C->he.x + fabsf(R.c1.z) * C->he.y + fabsf(R.c2.z) * C->he.z);
        } else if (d.kind == SP_C3_SPHERE) {
            float r = fabsf(d.ex) * absmax3(ax, ay, az);
            C->he = v3(r, 0.0f, 0.0f);
            ext = v3(r, r, r);
        } else {   /* capsule: the radius scales with the larger of the two cross-section scales, the height with the axis' */
            int a = d.direction < 0 || d.direction > 2 ? 1 : d.direction;
            float sa = a == 0 ? ax : a == 1 ? ay : az, s1 = a == 0 ? ay : ax, s2 = a == 2 ? ay : az;
            float r = fabsf(d.ex) * (s1 > s2 ? s1 : s2), hh = fabsf(d.ey) * sa * 0.5f - r;
            if (hh < 0.0f) hh = 0.0f;
            C->he = v3(r, hh, 0.0f); C->axis = a;
            V3 u = v3(a == 0 ? 1.0f : 0.0f, a == 1 ? 1.0f : 0.0f, a == 2 ? 1.0f : 0.0f), w = qrot(C->q, u);
            ext = v3(fabsf(w.x) * hh + r, fabsf(w.y) * hh + r, fabsf(w.z) * hh + r);
        }
        aabb_add(mn, mx, sub(C->p, ext)); aabb_add(mn, mx, add(C->p, ext));
        aabb_inflate(mn, mx, 0.0f);
        C->aabb_ok = true;
    }
    for (int k = 0; k < 3; ++k) { C->wmn[k] = mn[k]; C->wmx[k] = mx[k]; }
}

/* ================================================================ ray tests */
/* slab test of the ray (o, d) against [mn, mx] (conservative: the boxes are inflated); the entry distance in *tmin */
inline bool slab(V3 o, V3 d, const float mn[3], const float mx[3], float maxd, float *tmin)
{
    float t0 = 0.0f, t1 = maxd;
    for (int k = 0; k < 3; ++k) {
        float ok = at(o, k), dk = at(d, k);
        if (dk == 0.0f) { if (ok < mn[k] || ok > mx[k]) return false; continue; }
        float a = (mn[k] - ok) / dk, b = (mx[k] - ok) / dk;
        if (a > b) { float t = a; a = b; b = t; }
        if (a > t0) t0 = a;
        if (b < t1) t1 = b;
        if (t0 > t1) return false;
    }
    *tmin = t0;
    return true;
}

/* BV4's RayTriOverlap / intersectRayTriangle (from memory): Moller-Trumbore in vertex space */
inline bool tri_test(V3 lo, V3 ld, V3 v0, V3 v1, V3 v2, bool cull, float geps, float *ot, float *ou, float *ov)
{
    const V3 edge1 = sub(v1, v0), edge2 = sub(v2, v0);
    const V3 pvec = cross(ld, edge2);
    const float det = dot(edge1, pvec);
    if (cull) {
        if (det < kCullEps) return false;
        const V3 tvec = sub(lo, v0);
        const float u = dot(tvec, pvec);
        const float enlargeCoeff = geps * det;
        const float uvlimit = -enlargeCoeff, uvlimit2 = det + enlargeCoeff;
        if (u < uvlimit || u > uvlimit2) return false;
        const V3 qvec = cross(tvec, edge1);
        const float v = dot(ld, qvec);
        if (v < uvlimit || (u + v) > uvlimit2) return false;
        const float d = dot(edge2, qvec);
        if (d < 0.0f) return false;
        const float oneOverDet = 1.0f / det;
        *ot = d * oneOverDet; *ou = u * oneOverDet; *ov = v * oneOverDet;
        return true;
    }
    if (fabsf(det) < kCullEps) return false;
    const float oneOverDet = 1.0f / det;
    const V3 tvec = sub(lo, v0);
    const float u = dot(tvec, pvec) * oneOverDet;
    if (u < -geps || u > 1.0f + geps) return false;
    const V3 qvec = cross(tvec, edge1);
    const float v = dot(ld, qvec) * oneOverDet;
    if (v < -geps || (u + v) > 1.0f + geps) return false;
    const float d = dot(edge2, qvec) * oneOverDet;
    if (d < 0.0f) return false;
    *ot = d; *ou = u; *ov = v;
    return true;
}

/* the ray in a mesh collider's vertex space (raycast_triangleMesh): q.rotateInv(o - p), q.rotateInv(d), then world2vertex */
inline void to_vertex(const Col *C, V3 o, V3 d, V3 *lo, V3 *ld)
{
    V3 a = qrotinv(C->q, sub(o, C->p)), b = qrotinv(C->q, d);
    if (!C->ident) { a = mtransform(C->w2s, a); b = mtransform(C->w2s, b); }
    *lo = a; *ld = b;
}

/* PhysX's rayAABBIntersect2 (box frame): 0 = miss; 1 = the origin inside (t 0); 1 + plane otherwise */
uint32_t ray_aabb2(V3 mn, V3 mx, V3 ro, V3 rd, V3 *coord, float *t)
{
    bool inside = true;
    float maxT[3] = {-1.0f, -1.0f, -1.0f};
    V3 c = v3(0, 0, 0);
    for (int i = 0; i < 3; ++i) {
        if (at(ro, i) < at(mn, i)) {
            setat(&c, i, at(mn, i)); inside = false;
            if (at(rd, i) != 0.0f) maxT[i] = (at(mn, i) - at(ro, i)) / at(rd, i);
        } else if (at(ro, i) > at(mx, i)) {
            setat(&c, i, at(mx, i)); inside = false;
            if (at(rd, i) != 0.0f) maxT[i] = (at(mx, i) - at(ro, i)) / at(rd, i);
        }
    }
    if (inside) { *coord = ro; *t = 0.0f; return 1; }
    int which = 0;
    if (maxT[1] > maxT[which]) which = 1;
    if (maxT[2] > maxT[which]) which = 2;
    if (maxT[which] < 0.0f) return 0;
    for (int i = 0; i < 3; ++i) {
        if (i == which) continue;
        float ci = at(ro, i) + maxT[which] * at(rd, i);
        setat(&c, i, ci);
        if (ci < at(mn, i) - 0.00001f || ci > at(mx, i) + 0.00001f) return 0;   /* RAYAABB_EPSILON */
    }
    *coord = c; *t = maxT[which];
    return 1 + (uint32_t)which;
}

/* intersectRaySphereBasic / intersectRaySphere (the origin moved to GU_RAY_SURFACE_OFFSET = 10 u from the surface first) */
bool ray_sphere_basic(V3 origin, V3 dir, float length, V3 center, float radius, float *dist, V3 *hit)
{
    const V3 offset = sub(center, origin);
    const float ray_dist = dot(dir, offset);
    const float off2 = dot(offset, offset);
    const float rad_2 = radius * radius;
    if (off2 <= rad_2) { *hit = origin; *dist = 0.0f; return true; }
    if (ray_dist <= 0 || (ray_dist - length) > radius) return false;
    const float d = rad_2 - (off2 - ray_dist * ray_dist);
    if (d < 0.0f) return false;
    *dist = ray_dist - sqrtf(d);
    if (*dist > length) return false;
    *hit = add(origin, mulf(dir, *dist));
    return true;
}
bool ray_sphere(V3 origin, V3 dir, float length, V3 center, float radius, float *dist, V3 *hit)
{
    const V3 x = sub(origin, center);
    float l = sqrtf(dot(x, x)) - radius - 10.0f;
    if (l < 0.0f) l = 0.0f;
    bool st = ray_sphere_basic(add(origin, mulf(dir, l)), dir, length - l, center, radius, dist, hit);
    if (st) *dist += l;
    return st;
}
/* a ray against the capsule (segment p0..p1, radius r): the smallest t >= 0 on the swept sphere; 0 when the origin is inside */
bool ray_capsule(V3 o, V3 d, V3 p0, V3 p1, float r, float *t)
{
    V3 ab = sub(p1, p0), ao = sub(o, p0);
    float ab2 = dot(ab, ab);
    /* the origin inside? (distance to the segment <= r) */
    float s = ab2 > 0.0f ? dot(ao, ab) / ab2 : 0.0f;
    if (s < 0.0f) s = 0.0f; else if (s > 1.0f) s = 1.0f;
    V3 cl = sub(o, add(p0, mulf(ab, s)));
    if (dot(cl, cl) <= r * r) { *t = 0.0f; return true; }
    float best = FLT_MAX; bool hit = false;
    if (ab2 > 0.0f) {   /* the infinite cylinder, then the segment's range */
        V3 n = mulf(ab, 1.0f / sqrtf(ab2));
        V3 dp = sub(d, mulf(n, dot(d, n))), op = sub(ao, mulf(n, dot(ao, n)));
        float A = dot(dp, dp), B = 2.0f * dot(dp, op), Cc = dot(op, op) - r * r;
        if (A > 0.0f) {
            float disc = B * B - 4.0f * A * Cc;
            if (disc >= 0.0f) {
                float tt = (-B - sqrtf(disc)) / (2.0f * A);
                if (tt >= 0.0f) {
                    float h = dot(add(ao, mulf(d, tt)), n);
                    if (h >= 0.0f && h * h <= ab2) { best = tt; hit = true; }
                }
            }
        }
    }
    float dist; V3 hp;
    if (ray_sphere(o, d, FLT_MAX, p0, r, &dist, &hp) && dist < best) { best = dist; hit = true; }
    if (ray_sphere(o, d, FLT_MAX, p1, r, &dist, &hp) && dist < best) { best = dist; hit = true; }
    if (hit) *t = best;
    return hit;
}

/* ================================================================ one query */
struct Best { float t, u, v; int32_t tri, serial; int32_t col; bool any; };
/* is (t, serial, tri) before the current best? (the order both traversals share) */
inline bool before(const Best &b, float t, int32_t serial, int32_t tri)
{
    if (!b.any) return true;
    if (t != b.t) return t < b.t;
    if (serial != b.serial) return serial < b.serial;
    return tri < b.tri;
}

struct Query {
    Scene *S;
    V3 o, d;
    float maxd;
    uint32_t mask;
    int32_t flags;
    bool cull, all, anyq;
    Best best;                       /* closest / any */
    Best *per; int32_t nper;         /* SP_R3F_ALL: per collider slot (stamped) */
    bool done;                       /* SP_R3F_ANY: found one */
};

inline bool col_eligible(const Query &Q, const Col *C)
{
    if (!C->live) return false;
    if (((Q.mask >> (C->d.layer & 31)) & 1u) == 0) return false;
    if ((C->d.flags & SP_C3F_TRIGGER) && !(Q.flags & SP_R3F_TRIGGERS)) return false;
    return true;
}
inline void offer(Query &Q, int32_t slot, const Col *C, float t, float u, float v, int32_t tri)
{
    if (Q.anyq) {
        if (!Q.best.any) { Q.best.any = true; Q.best.t = t; Q.best.u = u; Q.best.v = v; Q.best.tri = tri; Q.best.serial = C->serial; Q.best.col = slot; }
        Q.done = true;
        return;
    }
    if (Q.all) {
        Best &b = Q.per[slot];
        if (before(b, t, C->serial, tri)) { b.any = true; b.t = t; b.u = u; b.v = v; b.tri = tri; b.serial = C->serial; b.col = slot; }
        return;
    }
    if (before(Q.best, t, C->serial, tri)) { Q.best.any = true; Q.best.t = t; Q.best.u = u; Q.best.v = v; Q.best.tri = tri; Q.best.serial = C->serial; Q.best.col = slot; }
}
/* the current bound a node / collider must beat (FLT_MAX: none) */
inline float bound_of(const Query &Q, int32_t slot)
{
    if (Q.anyq) return Q.maxd;
    const Best &b = Q.all ? Q.per[slot] : Q.best;
    if (!b.any) return Q.maxd;
    return b.t + fabsf(b.t) * 1e-5f + 1e-6f;
}

void test_tri(Query &Q, int32_t slot, const Col *C, const Mesh *M, int32_t tri, V3 lo, V3 ld)
{
    Q.S->tritests++;
    const int32_t *t = M->t + 3 * tri;
    V3 p0 = cv(M->v + 3 * t[0]), p1 = cv(M->v + 3 * t[1]), p2 = cv(M->v + 3 * t[2]);
    float tt, u, v;
    if (!tri_test(lo, ld, p0, p1, p2, Q.cull, M->geps, &tt, &u, &v)) return;
    if (tt > Q.maxd) return;
    offer(Q, slot, C, tt, u, v, tri);
}

void mesh_general(Query &Q, int32_t slot, const Col *C, const Mesh *M)
{
    V3 lo, ld;
    to_vertex(C, Q.o, Q.d, &lo, &ld);
    if (Q.flags & SP_R3F_BRUTE) {
        for (int32_t i = 0; i < M->nt && !Q.done; ++i) test_tri(Q, slot, C, M, i, lo, ld);
        return;
    }
    int32_t stack[96], sp = 0;
    stack[sp++] = 0;
    while (sp > 0 && !Q.done) {
        const Node *N = &M->nodes[stack[--sp]];
        float tmin;
        if (!slab(lo, ld, N->mn, N->mx, Q.maxd, &tmin)) continue;
        if (tmin > bound_of(Q, slot)) continue;
        if (N->n > 0) {
            for (int32_t i = N->a; i < N->a + N->n && !Q.done; ++i) test_tri(Q, slot, C, M, M->order[i], lo, ld);
        } else if (sp + 2 <= 96) {
            stack[sp++] = N->a + 1;
            stack[sp++] = N->a;
        }
    }
}

void prim_test(Query &Q, int32_t slot, const Col *C)
{
    Q.S->primtests++;
    float t = 0.0f;
    bool hit = false;
    if (C->d.kind == SP_C3_BOX) {
        V3 lo = qrotinv(C->q, sub(Q.o, C->p)), ld = qrotinv(C->q, Q.d), coord;
        uint32_t r = ray_aabb2(neg(C->he), C->he, lo, ld, &coord, &t);
        hit = r > 0 && t <= Q.maxd;
    } else if (C->d.kind == SP_C3_SPHERE) {
        V3 hp;
        hit = ray_sphere(Q.o, Q.d, Q.maxd, C->p, C->he.x, &t, &hp);
    } else if (C->d.kind == SP_C3_CAPSULE) {
        V3 u = v3(C->axis == 0 ? 1.0f : 0.0f, C->axis == 1 ? 1.0f : 0.0f, C->axis == 2 ? 1.0f : 0.0f), w = mulf(qrot(C->q, u), C->he.y);
        hit = ray_capsule(Q.o, Q.d, sub(C->p, w), add(C->p, w), C->he.x, &t) && t <= Q.maxd;
    }
    /* t == 0: the ray starts inside (or on) the primitive; PhysX reports it, Unity's Physics.Raycast does not */
    if (hit && t > 0.0f) offer(Q, slot, C, t, 0.0f, 0.0f, -1);
}

/* ---- the +z fast path's grid (per mesh collider) ---- */
/* the cell of x (clamped): monotone in x, the same expression for a triangle's box and for a ray, so a ray inside a box lands in one of
 * the box's cells; truncation is floor for f >= 0 (no libm: the bare-metal QEMU harness has none) */
inline int32_t cellf(float x, float x0, float inv, int32_t n)
{
    float f = (x - x0) * inv;
    if (!(f >= 0.0f)) return 0;
    if (f >= (float)n) return n - 1;
    return (int32_t)f;
}
inline int32_t ceil_pos(float q) { int32_t i = q < 2147483520.0f ? (int32_t)q : 2147483520; return (float)i < q ? i + 1 : i; }
/* the triangles of C's mesh a +z ray can hit with culling (world normal z not clearly > 0: det > 0 in vertex space is -n_world . (0, 0, 1) > 0
 * in exact arithmetic), each in every cell its inflated world xy box touches */
bool build_grid(Scene *S, Col *C)
{
    free_grid(C);
    S->gridbuilds++;
    const Mesh *M = get_mesh(S, C->d.mesh);
    if (!M || M->nt == 0) { C->grid_ok = true; return true; }
    float *box = (float *)alloc((size_t)M->nt * 4 * sizeof(float));
    int32_t *tri = (int32_t *)alloc((size_t)M->nt * sizeof(int32_t));
    if (!box || !tri) { dealloc(box); dealloc(tri); return false; }
    float sm = absmax3(C->d.sx, C->d.sy, C->d.sz);
    int32_t ne = 0;
    float x0 = FLT_MAX, y0 = FLT_MAX, x1 = -FLT_MAX, y1 = -FLT_MAX;
    for (int32_t ti = 0; ti < M->nt; ++ti) {
        const int32_t *t = M->t + 3 * ti;
        V3 p[3];
        float mn[3] = {FLT_MAX, FLT_MAX, FLT_MAX}, mx[3] = {-FLT_MAX, -FLT_MAX, -FLT_MAX};
        for (int j = 0; j < 3; ++j) {
            V3 lv = cv(M->v + 3 * t[j]);
            p[j] = lv;
            aabb_add(mn, mx, add(qrot(C->q, C->ident ? lv : mtransform(C->s2w, lv)), C->p));
        }
        V3 nl = cross(sub(p[1], p[0]), sub(p[2], p[0]));
        V3 nw = qrot(C->q, C->ident ? nl : mtransformT(C->w2s, nl));
        float len = sqrtf(dot(nw, nw));
        if (!(nw.z <= 1e-3f * len)) continue;   /* clearly facing +z: a +z ray with culling never hits it */
        float edge = absmax3(mx[0] - mn[0], mx[1] - mn[1], mx[2] - mn[2]);
        float e = absmax3(mn[0], mx[0], absmax3(mn[1], mx[1], 1.0f)) * 1e-5f + M->geps * (sm + 1.0f) * (edge + 1.0f) * 8.0f + 1e-5f;
        float *b = box + 4 * ne;
        b[0] = mn[0] - e; b[1] = mn[1] - e; b[2] = mx[0] + e; b[3] = mx[1] + e;
        if (b[0] < x0) x0 = b[0];
        if (b[1] < y0) y0 = b[1];
        if (b[2] > x1) x1 = b[2];
        if (b[3] > y1) y1 = b[3];
        tri[ne++] = ti;
    }
    C->gentries = ne;
    if (ne == 0) { C->gnx = C->gny = 0; C->grid_ok = true; dealloc(box); dealloc(tri); return true; }
    float W = x1 - x0, H = y1 - y0;
    float cs = sqrtf((W * H > 0.0f ? W * H : (W > H ? W : H)) / (float)ne);
    if (!(cs > 1e-3f)) cs = 1e-3f;
    for (int attempt = 0; attempt < 24; ++attempt) {
        int32_t nx = ceil_pos(W / cs), ny = ceil_pos(H / cs);
        if (nx < 1) nx = 1;
        if (ny < 1) ny = 1;
        if (nx > 128 || ny > 128) { cs *= 2.0f; continue; }
        float inv = 1.0f / cs;
        int64_t items = 0;
        for (int32_t e = 0; e < ne; ++e) {
            const float *b = box + 4 * e;
            items += (int64_t)(cellf(b[2], x0, inv, nx) - cellf(b[0], x0, inv, nx) + 1) * (cellf(b[3], y0, inv, ny) - cellf(b[1], y0, inv, ny) + 1);
        }
        if (items > (int64_t)ne * 16 + 4096 && attempt < 23) { cs *= 2.0f; continue; }
        int32_t ncell = nx * ny;
        if (!grow(&C->gstart, &C->gcap_start, ncell + 1) || !grow(&C->gitems, &C->gcap_items, (int32_t)(items > 0 ? items : 1))) break;
        memset(C->gstart, 0, (size_t)(ncell + 1) * sizeof(int32_t));
        for (int32_t e = 0; e < ne; ++e) {
            const float *b = box + 4 * e;
            int32_t ax0 = cellf(b[0], x0, inv, nx), ax1 = cellf(b[2], x0, inv, nx), ay0 = cellf(b[1], y0, inv, ny), ay1 = cellf(b[3], y0, inv, ny);
            for (int32_t iy = ay0; iy <= ay1; ++iy) for (int32_t ix = ax0; ix <= ax1; ++ix) C->gstart[iy * nx + ix + 1]++;
        }
        for (int32_t c = 0; c < ncell; ++c) C->gstart[c + 1] += C->gstart[c];
        int32_t *fill = (int32_t *)alloc((size_t)ncell * sizeof(int32_t));
        if (!fill) break;
        memcpy(fill, C->gstart, (size_t)ncell * sizeof(int32_t));
        for (int32_t e = 0; e < ne; ++e) {   /* each cell's list in triangle order */
            const float *b = box + 4 * e;
            int32_t ax0 = cellf(b[0], x0, inv, nx), ax1 = cellf(b[2], x0, inv, nx), ay0 = cellf(b[1], y0, inv, ny), ay1 = cellf(b[3], y0, inv, ny);
            for (int32_t iy = ay0; iy <= ay1; ++iy) for (int32_t ix = ax0; ix <= ax1; ++ix) C->gitems[fill[iy * nx + ix]++] = tri[e];
        }
        dealloc(fill);
        C->gx0 = x0; C->gy0 = y0; C->ginv = inv; C->gnx = nx; C->gny = ny;
        C->grid_ok = true;
        dealloc(box); dealloc(tri);
        return true;
    }
    free_grid(C);
    dealloc(box); dealloc(tri);
    return false;
}

/* the +z fast path through one mesh collider: its grid cell's triangles, the same per-triangle test as the general path */
void mesh_fast(Query &Q, int32_t slot, Col *C, const Mesh *M)
{
    if (!C->grid_ok && !build_grid(Q.S, C)) { mesh_general(Q, slot, C, M); return; }   /* (no memory for a grid: the general path, same answer) */
    if (C->gnx == 0) return;
    float cs = 1.0f / C->ginv;
    float x = Q.o.x, y = Q.o.y;
    if (!(x >= C->gx0) || !(y >= C->gy0) || !(x <= C->gx0 + cs * (float)C->gnx * 1.0001f + 1.0f) || !(y <= C->gy0 + cs * (float)C->gny * 1.0001f + 1.0f)) return;
    int32_t c = cellf(y, C->gy0, C->ginv, C->gny) * C->gnx + cellf(x, C->gx0, C->ginv, C->gnx);
    V3 lo, ld;
    to_vertex(C, Q.o, Q.d, &lo, &ld);
    for (int32_t k = C->gstart[c]; k < C->gstart[c + 1] && !Q.done; ++k) test_tri(Q, slot, C, M, C->gitems[k], lo, ld);
}

Best *g_per; int32_t g_percap;   /* SP_R3F_ALL's per-collider results (one query at a time) */

void fill_hit(const Query &Q, const Best &b, sp_ray3d_hit *h)
{
    const Scene *S = Q.S;
    const Col *C = &S->cols[b.col];
    memset(h, 0, sizeof *h);
    h->distance = b.t; h->user = C->d.user; h->col = mk(b.col, C->gen); h->face = b.tri;
    if (C->d.kind == SP_C3_MESH) {
        const Mesh *M = get_mesh((Scene *)S, C->d.mesh);
        const int32_t *t = M->t + 3 * b.tri;
        V3 lp0 = cv(M->v + 3 * t[0]), lp1 = cv(M->v + 3 * t[1]), lp2 = cv(M->v + 3 * t[2]);
        V3 pos;
        if (Q.flags & SP_R3F_POINT_RAY) pos = add(Q.o, mulf(Q.d, b.t));
        else {
            const V3 localImpact = add(add(mulf(lp0, 1.0f - b.u - b.v), mulf(lp1, b.u)), mulf(lp2, b.v));
            pos = add(qrot(C->q, C->ident ? localImpact : mtransform(C->s2w, localImpact)), C->p);
        }
        const V3 localNormal = cross(sub(lp1, lp0), sub(lp2, lp0));
        V3 n = C->ident ? qrot(C->q, localNormal) : qrot(C->q, mtransformT(C->w2s, localNormal));
        n = normalize(n);
        float u = b.u, v = b.v;
        if (!C->ident && dot(C->s2w.c0, cross(C->s2w.c1, C->s2w.c2)) < 0.0f) { float x = u; u = v; v = x; }   /* PhysX: a negative-determinant scale swaps u / v */
        h->px = pos.x; h->py = pos.y; h->pz = pos.z; h->nx = n.x; h->ny = n.y; h->nz = n.z; h->u = u; h->v = v;
        return;
    }
    h->face = -1;
    if (C->d.kind == SP_C3_BOX) {
        V3 lo = qrotinv(C->q, sub(Q.o, C->p)), ld = qrotinv(C->q, Q.d), coord; float t;
        uint32_t r = ray_aabb2(neg(C->he), C->he, lo, ld, &coord, &t);
        V3 pos = add(qrot(C->q, coord), C->p), n = v3(0, 0, 0);
        if (r > 1) setat(&n, (int)r - 2, at(coord, (int)r - 2) > 0.0f ? 1.0f : -1.0f);
        n = qrot(C->q, n);
        h->px = pos.x; h->py = pos.y; h->pz = pos.z; h->nx = n.x; h->ny = n.y; h->nz = n.z;
    } else if (C->d.kind == SP_C3_SPHERE) {
        float t; V3 hp;
        ray_sphere(Q.o, Q.d, Q.maxd, C->p, C->he.x, &t, &hp);
        V3 n = normalize(sub(hp, C->p));
        h->px = hp.x; h->py = hp.y; h->pz = hp.z; h->nx = n.x; h->ny = n.y; h->nz = n.z;
    } else {
        V3 pos = add(Q.o, mulf(Q.d, b.t));
        V3 u = v3(C->axis == 0 ? 1.0f : 0.0f, C->axis == 1 ? 1.0f : 0.0f, C->axis == 2 ? 1.0f : 0.0f), w = mulf(qrot(C->q, u), C->he.y);
        V3 p0 = sub(C->p, w), ab = mulf(w, 2.0f);
        float ab2 = dot(ab, ab), s = ab2 > 0.0f ? dot(sub(pos, p0), ab) / ab2 : 0.0f;
        if (s < 0.0f) s = 0.0f; else if (s > 1.0f) s = 1.0f;
        V3 n = normalize(sub(pos, add(p0, mulf(ab, s))));
        h->px = pos.x; h->py = pos.y; h->pz = pos.z; h->nx = n.x; h->ny = n.y; h->nz = n.z;
    }
}

int32_t impl_ray3d(int32_t scene, const sp_ray3d_query *q, sp_ray3d_hit *out, int32_t max)
{
    Scene *S = get_scene(scene);
    if (!S) return SP_ERR_HANDLE;
    if (!q || max < 0 || (max > 0 && !out)) return SP_ERR_PARAM;
    S->rays++;
    Query Q;
    memset(&Q, 0, sizeof Q);
    Q.S = S; Q.o = v3(q->ox, q->oy, q->oz); Q.d = v3(q->dx, q->dy, q->dz);
    Q.maxd = q->max_distance < FLT_MAX ? q->max_distance : FLT_MAX;   /* +inf and NaN: FLT_MAX */
    if (q->max_distance != q->max_distance) Q.maxd = FLT_MAX;
    if (Q.maxd < 0.0f) return 0;
    Q.mask = q->layer_mask; Q.flags = q->flags;
    Q.cull = !(q->flags & SP_R3F_BACKFACES);
    Q.anyq = (q->flags & SP_R3F_ANY) != 0;
    Q.all = !Q.anyq && (q->flags & SP_R3F_ALL) != 0;
    if (!S->layers_ok) {
        S->layers = 0; S->prims = 0;
        for (int32_t i = 0; i < S->ncol; ++i) if (S->cols[i].live) { S->layers |= 1u << (S->cols[i].d.layer & 31); if (S->cols[i].d.kind != SP_C3_MESH) S->prims++; }
        S->layers_ok = true;
    }
    if ((Q.mask & S->layers) == 0) { S->early++; return 0; }
    if (Q.all) {
        if (g_percap < S->ncol) {
            dealloc(g_per);
            g_percap = S->capcol;
            g_per = (Best *)alloc((size_t)g_percap * sizeof(Best));
            if (!g_per) { g_percap = 0; return SP_ERR_FULL; }
        }
        memset(g_per, 0, (size_t)S->ncol * sizeof(Best));
        Q.per = g_per; Q.nper = S->ncol;
    }
    /* the +z fast path: a mesh collider's own grid instead of its BVH; everything else (eligibility, primitives, the world-box cull) is the
     * general path's, so the two give one answer bit for bit (SP_R3F_GENERAL / SP_R3F_BRUTE force the others: the check modes) */
    bool fast = Q.d.x == 0.0f && Q.d.y == 0.0f && Q.d.z > 0.0f && Q.cull && !(q->flags & (SP_R3F_GENERAL | SP_R3F_BRUTE));
    if (fast) S->fast++; else S->general++;
    for (int32_t i = 0; i < S->ncol && !Q.done; ++i) {
        Col *C = &S->cols[i];
        if (!col_eligible(Q, C)) continue;
        if (C->d.kind != SP_C3_MESH) { prim_test(Q, i, C); continue; }
        const Mesh *M = get_mesh(S, C->d.mesh);
        if (!M || M->nt == 0) continue;
        if (!(q->flags & SP_R3F_BRUTE)) {
            float tmin;
            if (!C->aabb_ok || !slab(Q.o, Q.d, C->wmn, C->wmx, Q.maxd, &tmin)) continue;
            if (tmin > bound_of(Q, i)) continue;
        }
        if (fast) mesh_fast(Q, i, C, M); else mesh_general(Q, i, C, M);
    }
    if (!Q.all) {
        if (!Q.best.any) return 0;
        S->hits++;
        if (max > 0) fill_hit(Q, Q.best, out);
        return 1;
    }
    /* SP_R3F_ALL: the colliders with a hit, in slot order; SP_R3F_SORT: by (distance, creation order) */
    int32_t n = 0;
    for (int32_t i = 0; i < Q.nper; ++i) if (Q.per[i].any) Q.per[n++] = Q.per[i];
    if (q->flags & SP_R3F_SORT)
        for (int32_t i = 1; i < n; ++i) {   /* insertion sort (a handful of hits) */
            Best x = Q.per[i]; int32_t j = i - 1;
            while (j >= 0 && (Q.per[j].t > x.t || (Q.per[j].t == x.t && Q.per[j].serial > x.serial))) { Q.per[j + 1] = Q.per[j]; --j; }
            Q.per[j + 1] = x;
        }
    for (int32_t i = 0; i < n && i < max; ++i) fill_hit(Q, Q.per[i], out + i);
    S->hits += n;
    return n;
}

/* ================================================================ scene / mesh / collider management */
int32_t impl_scene_create(int32_t flags)
{
    for (int32_t s = 0; s < kMaxScenes; ++s) {
        Scene *S = &g_scenes[s];
        if (S->live) continue;
        uint16_t g = (uint16_t)(S->gen + 1); if (g == 0 || g > 0x7FFF) g = 1;
        memset(S, 0, sizeof *S);
        S->gen = g; S->live = true;
        (void)flags;
        return mk(s, g);
    }
    return SP_ERR_FULL;
}
int32_t impl_scene_destroy(int32_t scene)
{
    Scene *S = get_scene(scene);
    if (!S) return SP_ERR_HANDLE;
    for (int32_t i = 0; i < S->nmesh; ++i) if (S->meshes[i].live) free_mesh(&S->meshes[i]);
    for (int32_t i = 0; i < S->ncol; ++i) free_grid(&S->cols[i]);
    dealloc(S->meshes); dealloc(S->cols);
    uint16_t g = S->gen;
    memset(S, 0, sizeof *S);
    S->gen = g;
    return SP_OK;
}
void invalidate(Scene *S) { S->layers_ok = false; }

int32_t impl_mesh_create(int32_t scene, const sp_mesh3d_def *d, const float *xyz, const int32_t *tri)
{
    Scene *S = get_scene(scene);
    if (!S) return SP_ERR_HANDLE;
    if (!d || d->vertex_count < 0 || d->triangle_count < 0 || (d->vertex_count > 0 && !xyz) || (d->triangle_count > 0 && !tri)) return SP_ERR_PARAM;
    for (int32_t i = 0; i < 3 * d->triangle_count; ++i) if (tri[i] < 0 || tri[i] >= d->vertex_count) return SP_ERR_PARAM;
    for (int32_t i = 0; i < 3 * d->vertex_count; ++i) if (!(xyz[i] == xyz[i]) || xyz[i] > FLT_MAX || xyz[i] < -FLT_MAX) return SP_ERR_PARAM;
    int32_t slot = -1;
    for (int32_t i = 0; i < S->nmesh; ++i) if (!S->meshes[i].live) { slot = i; break; }
    if (slot < 0) {
        if (!grow(&S->meshes, &S->capmesh, S->nmesh + 1)) return SP_ERR_FULL;
        slot = S->nmesh++;
    }
    Mesh *M = &S->meshes[slot];
    uint16_t g = (uint16_t)(M->gen + 1); if (g == 0 || g > 0x7FFF) g = 1;
    memset(M, 0, sizeof *M);
    M->gen = g;
    M->nv = d->vertex_count; M->nt = d->triangle_count;
    M->v = (float *)alloc((size_t)M->nv * 3 * sizeof(float));
    M->t = (int32_t *)alloc((size_t)M->nt * 3 * sizeof(int32_t));
    M->order = (int32_t *)alloc((size_t)M->nt * sizeof(int32_t));
    M->nodes = (Node *)alloc((size_t)(2 * M->nt + 1) * sizeof(Node));
    float *cen = (float *)alloc((size_t)M->nt * 3 * sizeof(float));
    int32_t *tmp = (int32_t *)alloc((size_t)M->nt * sizeof(int32_t));
    if ((M->nv && !M->v) || (M->nt && (!M->t || !M->order || !cen || !tmp)) || !M->nodes) { free_mesh(M); dealloc(cen); dealloc(tmp); return SP_ERR_FULL; }
    if (M->nv) memcpy(M->v, xyz, (size_t)M->nv * 3 * sizeof(float));
    if (M->nt) memcpy(M->t, tri, (size_t)M->nt * 3 * sizeof(int32_t));
    for (int k = 0; k < 3; ++k) { M->mn[k] = M->nv ? FLT_MAX : 0.0f; M->mx[k] = M->nv ? -FLT_MAX : 0.0f; }
    for (int32_t i = 0; i < M->nv; ++i) for (int k = 0; k < 3; ++k) { float c = M->v[3 * i + k]; if (c < M->mn[k]) M->mn[k] = c; if (c > M->mx[k]) M->mx[k] = c; }
    float ge = 0.0f;
    for (int k = 0; k < 3; ++k) { float a = fabsf(M->mx[k]) > fabsf(M->mn[k]) ? fabsf(M->mx[k]) : fabsf(M->mn[k]); if (a > ge) ge = a; }
    M->geps = ge * 2.384185791015625e-07f;   /* 2^-22 (TriangleMeshBuilder::computeLocalBounds) */
    for (int32_t i = 0; i < M->nt; ++i) {
        M->order[i] = i;
        const int32_t *t = M->t + 3 * i;
        for (int k = 0; k < 3; ++k) cen[3 * i + k] = (M->v[3 * t[0] + k] + M->v[3 * t[1] + k] + M->v[3 * t[2] + k]) * (1.0f / 3.0f);
    }
    if (M->nt > 0) {
        Build B; B.M = M; B.cen = cen; B.nn = 1; B.tmp = tmp;
        build_into(&B, 0, 0, M->nt, 0);
        M->nn = B.nn;
    } else {
        M->nn = 1; Node *N = &M->nodes[0];
        for (int k = 0; k < 3; ++k) { N->mn[k] = 1.0f; N->mx[k] = -1.0f; }
        N->a = 0; N->n = 0;
    }
    dealloc(cen); dealloc(tmp);
    M->live = true;
    S->triangles += M->nt; S->bvhnodes += M->nn;
    return mk(slot, g);
}
int32_t impl_mesh_destroy(int32_t scene, int32_t mesh)
{
    Scene *S = get_scene(scene);
    Mesh *M = get_mesh(S, mesh);
    if (!M) return SP_ERR_HANDLE;
    S->triangles -= M->nt; S->bvhnodes -= M->nn;
    free_mesh(M);
    M->live = false;
    for (int32_t i = 0; i < S->ncol; ++i) if (S->cols[i].live && S->cols[i].d.kind == SP_C3_MESH && S->cols[i].d.mesh == mesh) derive(S, &S->cols[i]);
    invalidate(S);
    return SP_OK;
}
bool valid_def(const sp_col3d_def *d)
{
    if (!d || d->kind < SP_C3_BOX || d->kind > SP_C3_MESH || d->layer < 0 || d->layer > 31) return false;
    const float *f = &d->px;
    for (int i = 0; i < 16; ++i) if (!(f[i] == f[i]) || f[i] > FLT_MAX || f[i] < -FLT_MAX) return false;
    return true;
}
int32_t impl_col_create(int32_t scene, const sp_col3d_def *d)
{
    Scene *S = get_scene(scene);
    if (!S) return SP_ERR_HANDLE;
    if (!valid_def(d)) return SP_ERR_PARAM;
    int32_t slot = -1;
    for (int32_t i = 0; i < S->ncol; ++i) if (!S->cols[i].live) { slot = i; break; }
    if (slot < 0) {
        if (!grow(&S->cols, &S->capcol, S->ncol + 1)) return SP_ERR_FULL;
        slot = S->ncol++;
    }
    Col *C = &S->cols[slot];
    free_grid(C);   /* (a reused slot) */
    uint16_t g = (uint16_t)(C->gen + 1); if (g == 0 || g > 0x7FFF) g = 1;
    memset(C, 0, sizeof *C);
    C->gen = g; C->live = true; C->d = *d; C->serial = ++S->serial;
    derive(S, C);
    invalidate(S);
    return mk(slot, g);
}
int32_t impl_col_set(int32_t scene, int32_t col, const sp_col3d_def *d)
{
    Scene *S = get_scene(scene);
    Col *C = get_col(S, col);
    if (!C) return SP_ERR_HANDLE;
    if (!valid_def(d)) return SP_ERR_PARAM;
    C->d = *d;
    derive(S, C);   /* (its grid is rebuilt at the next +z ray) */
    invalidate(S);
    return SP_OK;
}
int32_t impl_col_destroy(int32_t scene, int32_t col)
{
    Scene *S = get_scene(scene);
    Col *C = get_col(S, col);
    if (!C) return SP_ERR_HANDLE;
    C->live = false;
    free_grid(C);
    invalidate(S);
    return SP_OK;
}

} /* namespace */

/* ================================================================ C API (traced wrappers) */
extern "C" {

SP_API uint64_t sp_tr_ray3d_hash(const sp_ray3d_hit *h, int32_t n)
{
    uint64_t x = 14695981039346656037ull;
    for (int32_t i = 0; h && i < n; ++i) {
        const uint8_t *b = (const uint8_t *)(h + i);
        for (uint32_t k = 0; k < 44; ++k) x = (x ^ b[k]) * 1099511628211ull;
    }
    return x;
}

SP_API int32_t sp_scene3d_create(int32_t flags)
{
    int32_t r = impl_scene_create(flags);
    if (sp_tr_on) { int32_t a[2] = {r, flags}; sp_tr_rec(SP_TR_SCENE3D_CREATE, a, sizeof a, nullptr, 0); }
    return r;
}
SP_API int32_t sp_scene3d_destroy(int32_t scene)
{
    int32_t r = impl_scene_destroy(scene);
    if (sp_tr_on) { int32_t a[2] = {scene, r}; sp_tr_rec(SP_TR_SCENE3D_DESTROY, a, sizeof a, nullptr, 0); }
    return r;
}
SP_API int32_t sp_mesh3d_create(int32_t scene, const sp_mesh3d_def *def, const float *xyz, const int32_t *tri)
{
    int32_t r = impl_mesh_create(scene, def, xyz, tri);
    if (sp_tr_on) {
        int32_t nv = def && def->vertex_count > 0 && xyz ? def->vertex_count : 0, nt = def && def->triangle_count > 0 && tri ? def->triangle_count : 0;
        uint32_t nb = (uint32_t)(def ? sizeof *def : 0) + (uint32_t)nv * 12u + (uint32_t)nt * 12u;
        uint8_t *b = (uint8_t *)alloc(nb ? nb : 4);
        if (b) {
            uint32_t at_ = 0;
            if (def) { memcpy(b, def, sizeof *def); at_ += sizeof *def; }
            if (nv) { memcpy(b + at_, xyz, (size_t)nv * 12u); at_ += (uint32_t)nv * 12u; }
            if (nt) { memcpy(b + at_, tri, (size_t)nt * 12u); at_ += (uint32_t)nt * 12u; }
            int32_t a[5] = {scene, r, def != nullptr, nv != 0, nt != 0};
            sp_tr_rec(SP_TR_MESH3D_CREATE, a, sizeof a, b, at_);
            dealloc(b);
        }
    }
    return r;
}
SP_API int32_t sp_mesh3d_destroy(int32_t scene, int32_t mesh)
{
    int32_t r = impl_mesh_destroy(scene, mesh);
    if (sp_tr_on) { int32_t a[3] = {scene, mesh, r}; sp_tr_rec(SP_TR_MESH3D_DESTROY, a, sizeof a, nullptr, 0); }
    return r;
}
SP_API int32_t sp_col3d_create(int32_t scene, const sp_col3d_def *def)
{
    int32_t r = impl_col_create(scene, def);
    if (sp_tr_on) { int32_t a[3] = {scene, r, def != nullptr}; sp_tr_rec(SP_TR_COL3D_CREATE, a, sizeof a, def, def ? sizeof *def : 0); }
    return r;
}
SP_API int32_t sp_col3d_set(int32_t scene, int32_t col, const sp_col3d_def *def)
{
    int32_t r = impl_col_set(scene, col, def);
    if (sp_tr_on) { int32_t a[4] = {scene, col, r, def != nullptr}; sp_tr_rec(SP_TR_COL3D_SET, a, sizeof a, def, def ? sizeof *def : 0); }
    return r;
}
SP_API int32_t sp_col3d_destroy(int32_t scene, int32_t col)
{
    int32_t r = impl_col_destroy(scene, col);
    if (sp_tr_on) { int32_t a[3] = {scene, col, r}; sp_tr_rec(SP_TR_COL3D_DESTROY, a, sizeof a, nullptr, 0); }
    return r;
}
SP_API int32_t sp_ray3d(int32_t scene, const sp_ray3d_query *q, sp_ray3d_hit *out, int32_t max)
{
    int32_t r = impl_ray3d(scene, q, out, max);
    if (sp_tr_on) {
        int32_t w = r > 0 ? (r < max ? r : max) : 0;
        uint64_t h = sp_tr_ray3d_hash(out, w);
        int32_t a[6] = {scene, max, r, q != nullptr, (int32_t)(uint32_t)h, (int32_t)(uint32_t)(h >> 32)};
        sp_tr_rec(SP_TR_RAY3D, a, sizeof a, q, q ? sizeof *q : 0);
    }
    return r;
}
SP_API int32_t sp_scene3d_stats(int32_t scene, int32_t *o)
{
    Scene *S = get_scene(scene);
    if (!S) return SP_ERR_HANDLE;
    if (!o) return SP_ERR_PARAM;
    int32_t live = 0, meshes = 0, cells = 0, items = 0;
    for (int32_t i = 0; i < S->ncol; ++i) {
        const Col *C = &S->cols[i];
        live += C->live;
        if (C->live && C->grid_ok && C->gstart) { cells += C->gnx * C->gny; items += C->gstart[C->gnx * C->gny]; }
    }
    for (int32_t i = 0; i < S->nmesh; ++i) meshes += S->meshes[i].live;
    int32_t v[16] = {live, meshes, S->triangles, S->rays, S->early, S->fast, S->general, S->tritests, S->primtests, S->gridbuilds,
                     cells, items, (int32_t)S->layers, S->bvhnodes, S->hits, 0};
    memcpy(o, v, sizeof v);
    return SP_OK;
}

} /* extern "C" */
