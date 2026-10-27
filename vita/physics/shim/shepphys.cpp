/* shepphys.cpp -- the flat C API over Box2D 2.3.1 (stage 0a). Ours; Box2D is used unmodified (see shepphys.h).
 *
 * Memory: Box2D allocates through b2Alloc -- with B2 (Stage 1, shim/sp_b2glue.cpp) the fixed arena of sp_core.cpp, else malloc; this
 * file uses malloc/realloc/free for its handle tables and placement new for the b2World, so no C++ operator new / exceptions are
 * pulled in. Stage 1 also added the Unity contact filter (UFilter: off until sp_world_set_matrix, then the layer matrix, include /
 * exclude and the IgnoreCollision pairs) and explicit mass data (sp_body_set_mass), both traced.
 * Order: everything a caller can observe is in a deterministic order: handle order (sp_poses), Box2D's own contact-list
 * and broadphase orders (sp_contacts, sp_events, queries), and the stable sort for SP_RAY_SORTED.
 */
#include "shepphys.h"
#include "sp_trace.h"

#include <Box2D/Box2D.h>
#include <Box2D/Dynamics/b2Island.h>   /* E1, patch P2: b2_unityMaxTranslation / b2_unityMaxRotation */
#include <new>
#include <stdlib.h>
#include <string.h>

/* ---- ABI pins: int32 / uint32 / float only, so 32-bit Vita and 64-bit Mac share one layout ---- */
static_assert(sizeof(sp_world_def) == 16, "sp_world_def");
static_assert(sizeof(sp_body_def) == 48, "sp_body_def");
static_assert(sizeof(sp_shape_def) == 64, "sp_shape_def");
static_assert(sizeof(sp_pose) == 36, "sp_pose");
static_assert(sizeof(sp_step_def) == 16, "sp_step_def");
static_assert(sizeof(sp_step_stats) == 36, "sp_step_stats");
static_assert(sizeof(sp_event) == 20, "sp_event");
static_assert(sizeof(sp_contact) == 88, "sp_contact");
static_assert(sizeof(sp_ray_query) == 24, "sp_ray_query");
static_assert(sizeof(sp_ray_hit) == 36, "sp_ray_hit");
static_assert(sizeof(sp_aabb_query) == 24, "sp_aabb_query");
static_assert(sizeof(float) == 4 && sizeof(int32_t) == 4, "32-bit scalars");
static_assert(sizeof(sp_shape_unity) == 16, "sp_shape_unity");   /* Stage 1 */
static_assert(sizeof(sp_event2) == 32, "sp_event2");             /* Stage 2 */
static_assert(sizeof(sp_uquery) == 56, "sp_uquery");
static_assert(sizeof(sp_uhit) == 40, "sp_uhit");

#define SP_SHIM_VERSION 0x00000900   /* 0.9.0: Stage 4 step 4b (patch P5: SP_SHAPE_UNITY_WELD; SP_USF_UNITY_POLY: Unity's polygon build of a decomposed piece); 0.8.0: Stage 4 step 4a (SP_USF_CHAIN_WELD: Unity's chain vertex filter); 0.7.0: E1 (patch P2 + sp_world_set_clamps, SP_USTEP_MOVEGRAV_ON, H-SYNC tolerance, the TOI casts); 0.6.0: Stage 6 (the 3D island-ground ray: shim/sp_mesh3d.cpp); 0.5.0: Stage 3 (forces in the mirror, frozen axes, H-DAMP / H-CHAIN, contact snapshots); 0.4.0: Stage 2
                                        (P1 world flags, contact serials, Unity queries); 0.3.0: Stage 1 (the Unity layer) */

#ifndef SHEPPHYS_FLAGS
#define SHEPPHYS_FLAGS "?"
#endif
#ifdef SHEPPHYS_TRIG_OWN
#define SP_TRIG_MODE "own"
#else
#define SP_TRIG_MODE "libm"
#endif
#ifdef SHEPPHYS_B2_POOL
#define B2_MODE " alloc=B2"
#else
#define B2_MODE " alloc=malloc"
#endif
#if defined(__clang__)
#define SP_CC "clang " __clang_version__
#elif defined(__GNUC__)
#define SP_CC "gcc " __VERSION__
#else
#define SP_CC "cc"
#endif

namespace {

/* ---- handles: (generation << 16) | (slot + 1); generation 1..0x7FFF, so a handle is always > 0 ---- */
inline int32_t mk_handle(int32_t slot, uint16_t gen) { return (int32_t)(((uint32_t)gen << 16) | (uint32_t)(slot + 1)); }
inline int32_t h_slot(int32_t h) { return (int32_t)(h & 0xFFFF) - 1; }
inline uint16_t h_gen(int32_t h) { return (uint16_t)((uint32_t)h >> 16); }
inline uint16_t next_gen(uint16_t g) { return (uint16_t)(g >= 0x7FFF ? 1 : g + 1); }

template <typename T> bool grow(T *&v, int32_t &cap, int32_t need)
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

struct BodySlot { b2Body *body; uint16_t gen; int32_t user; int32_t next_free; };
/* Stage 1: layer / include / exclude for the Unity filter (sp_shape_set_unity; 0 / 0 / 0 until set). Stage 2: the collider
 * (sp_shape_def.user) and the collider's own rounding radius (uradius: circle radius, capsule radius, edge radius -- never Box2D's
 * b2_polygonRadius skin), read by the queries (sp_query.cpp) */
struct ShapeSlot { b2Fixture *fix; uint16_t gen; int32_t body; int32_t next_free; int32_t layer; uint32_t inc, exc; int32_t user; float uradius;
                   int32_t one_sided; /* Stage 3: SP_SHAPE_ONE_SIDED on an edge / chain (H-CHAIN) */
                   float bradius;     /* E1: the radius Unity's Collider2D.bounds include -- a circle's, a capsule's (SP_SHAPE_NO_SKIN); never a
                                         box's / edge's edgeRadius nor the skin (H-BOUNDSSKIN=none) */ };

/* a handle table with a LIFO free list (deterministic reuse) */
template <typename S> struct Table {
    S *v = nullptr;
    int32_t n = 0, cap = 0, free_head = -1, live = 0;
    int32_t alloc()
    {
        int32_t i;
        if (free_head >= 0) { i = free_head; free_head = v[i].next_free; }
        else { if (!grow(v, cap, n + 1)) return -1; i = n++; v[i].gen = 0; }
        v[i].gen = next_gen(v[i].gen);
        v[i].next_free = -1;
        live++;
        return i;
    }
    void release(int32_t i) { v[i].next_free = free_head; free_head = i; live--; }
    void destroy() { free(v); v = nullptr; n = cap = live = 0; free_head = -1; }
};

struct World;

class Listener : public b2ContactListener {
public:
    World *W = nullptr;
    void BeginContact(b2Contact *c) override;
    void EndContact(b2Contact *c) override;
    void PreSolve(b2Contact *c, const b2Manifold *old) override;   /* Stage 3: one-sided edges / chains (H-CHAIN); else a no-op */
};

/* Stage 1 (physics-plan-v2 section 1.8 steps 3-5): the Unity contact filter. Off (no matrix): Box2D's stock category / mask / group
 * filter, exactly as before (T1). On: layer matrix, then include / exclude (exclude wins: H-TIE), then the IgnoreCollision pairs.
 * Box2D's own body rules (at least one dynamic body; joints) run before it, unchanged. */
class UFilter : public b2ContactFilter {
public:
    World *W = nullptr;
    bool on = false;
    uint32_t rows[32];
    uint64_t *ign = nullptr;   /* open addressing, key = (lo handle << 32 | hi handle) + 1; 0 = empty, ~0 = deleted */
    uint32_t ign_cap = 0, ign_n = 0, ign_used = 0;
    bool ShouldCollide(b2Fixture *a, b2Fixture *b) override;
    static uint64_t key(int32_t a, int32_t b) { uint32_t x = (uint32_t)a, y = (uint32_t)b; if (x > y) { uint32_t t = x; x = y; y = t; } return (((uint64_t)x << 32) | y) + 1; }
    static uint32_t slot0(uint64_t k, uint32_t cap) { k ^= k >> 29; k *= 0xBF58476D1CE4E5B9ull; k ^= k >> 32; return (uint32_t)k & (cap - 1); }
    bool ignored(int32_t a, int32_t b) const
    {
        if (!ign_n) return false;
        uint64_t k = key(a, b);
        for (uint32_t i = slot0(k, ign_cap), n = 0; n < ign_cap; i = (i + 1) & (ign_cap - 1), ++n) {
            if (ign[i] == k) return true;
            if (ign[i] == 0) return false;
        }
        return false;
    }
    bool set_ignored(int32_t a, int32_t b, bool on_)
    {
        uint64_t k = key(a, b);
        if (on_) {
            if (ignored(a, b)) return true;
            if ((ign_used + 1) * 2 > ign_cap) {   /* rehash (also clears deleted marks) */
                uint32_t nc = ign_cap ? ign_cap : 16;
                while ((ign_n + 1) * 2 > nc) nc *= 2;
                uint64_t *nt = (uint64_t *)calloc(nc, sizeof(uint64_t));
                if (!nt) return false;
                for (uint32_t i = 0; i < ign_cap; ++i)
                    if (ign[i] && ign[i] != ~0ull) { uint32_t j = slot0(ign[i], nc); while (nt[j]) j = (j + 1) & (nc - 1); nt[j] = ign[i]; }
                free(ign); ign = nt; ign_cap = nc; ign_used = ign_n;
            }
            uint32_t j = slot0(k, ign_cap);
            while (ign[j] && ign[j] != ~0ull) j = (j + 1) & (ign_cap - 1);
            if (ign[j] == 0) ign_used++;
            ign[j] = k; ign_n++;
            return true;
        }
        if (!ign_n) return true;
        for (uint32_t i = slot0(k, ign_cap), n = 0; n < ign_cap; i = (i + 1) & (ign_cap - 1), ++n) {
            if (ign[i] == k) { ign[i] = ~0ull; ign_n--; return true; }
            if (ign[i] == 0) break;
        }
        return true;
    }
};

/* Stage 2: contact creation serials (SP_WORLD_CONTACT_SERIALS). An open-addressing table b2Contact* -> serial, rebuilt after every
 * step from the contact list (two tables, ping-pong: no allocation once grown). The key also holds the fixture handles and children,
 * so a new contact that reuses a freed contact's memory is never mistaken for it. */
struct CRec { b2Contact *c; int32_t serial, fa, fb, ca, cb; };
struct CMap {
    CRec *t = nullptr;
    uint32_t cap = 0, n = 0;
    static uint32_t h(const void *p) { uint64_t k = (uint64_t)(uintptr_t)p; k ^= k >> 33; k *= 0xff51afd7ed558ccdull; k ^= k >> 33; return (uint32_t)k; }
    static void key(b2Contact *c, int32_t *fa, int32_t *fb, int32_t *ca, int32_t *cb)
    {
        *fa = (int32_t)(intptr_t)c->GetFixtureA()->GetUserData(); *fb = (int32_t)(intptr_t)c->GetFixtureB()->GetUserData();
        *ca = c->GetChildIndexA(); *cb = c->GetChildIndexB();
    }
    int32_t find(b2Contact *c) const
    {
        if (!n) return 0;
        int32_t fa, fb, ca, cb; key(c, &fa, &fb, &ca, &cb);
        for (uint32_t i = h(c) & (cap - 1), k = 0; k < cap; i = (i + 1) & (cap - 1), ++k) {
            const CRec &r = t[i];
            if (!r.c) return 0;
            if (r.c == c) return (r.fa == fa && r.fb == fb && r.ca == ca && r.cb == cb) ? r.serial : 0;
        }
        return 0;
    }
    bool reset(uint32_t need)   /* empty, with room for need entries at load <= 1/2 */
    {
        uint32_t nc = cap ? cap : 64;
        while (nc < need * 2) nc *= 2;
        if (nc != cap) { CRec *nt = (CRec *)realloc(t, (size_t)nc * sizeof(CRec)); if (!nt) return false; t = nt; cap = nc; }
        memset(t, 0, (size_t)cap * sizeof(CRec));
        n = 0;
        return true;
    }
    void put(b2Contact *c, int32_t serial)
    {
        uint32_t i = h(c) & (cap - 1);
        while (t[i].c) i = (i + 1) & (cap - 1);
        CRec &r = t[i];
        r.c = c; r.serial = serial; key(c, &r.fa, &r.fb, &r.ca, &r.cb);
        n++;
    }
};

/* an event as queued: the sp_event2 record plus, until its serial is known, the contact (Stage 2) */
struct EvRec { sp_event2 e; b2Contact *c; };

struct World {
    b2World *w = nullptr;
    alignas(b2World) unsigned char w_mem[sizeof(b2World)];
    Listener listener;
    UFilter filter;
    Table<BodySlot> bodies;
    Table<ShapeSlot> shapes;
    EvRec *ev = nullptr;
    int32_t ev_n = 0, ev_cap = 0, ev_head = 0;
    int32_t step = 0;
    uint16_t gen = 0;
    int32_t wflags = 0;                 /* Stage 2: the sp_world_def flags (P1, contact serials) */
    CMap cmap[2]; int32_t cmap_cur = 0; /* Stage 2: contact serials (cmap[cmap_cur] is current) */
    int32_t serial_next = 0;
    b2Contact **cscratch = nullptr; int32_t cscratch_cap = 0;
    uint32_t version = 0;               /* Stage 2: bumped by every step and structural change (the queries' cached world bounds) */
    int32_t one_sided = 0;              /* Stage 3: one-sided shapes ever made in this world (PreSolve returns at once while 0) */
    float max_translation = 0.0f, max_rotation = 0.0f;   /* E1, patch P2: the per-step clamps (0 = Box2D's compiled ones) */

    void push(int32_t kind, b2Contact *c)
    {
        if (!grow(ev, ev_cap, ev_n + 1)) return;        /* out of memory: the event is lost (counted nowhere yet) */
        b2Fixture *fa = c->GetFixtureA(), *fb = c->GetFixtureB();
        EvRec &r = ev[ev_n++];
        sp_event2 &e = r.e;
        e.kind = kind;
        e.flags = ((fa->IsSensor() || fb->IsSensor()) ? SP_EVF_SENSOR : 0) | (w->IsLocked() ? SP_EVF_IN_STEP : 0);
        e.shape_a = (int32_t)(intptr_t)fa->GetUserData();
        e.shape_b = (int32_t)(intptr_t)fb->GetUserData();
        e.step = step;
        e.child_a = c->GetChildIndexA(); e.child_b = c->GetChildIndexB();
        e.serial = (wflags & SP_WORLD_CONTACT_SERIALS) ? cmap[cmap_cur].find(c) : 0;
        r.c = (e.serial == 0 && (wflags & SP_WORLD_CONTACT_SERIALS)) ? c : nullptr;   /* a contact this step created: resolved after it */
    }
    /* after a step: the serial table rebuilt from the contact list (new contacts numbered in creation order), then the queued events'
     * pending serials resolved */
    void serials()
    {
        int32_t n = w->GetContactCount();
        if (!grow(cscratch, cscratch_cap, n + 1)) return;
        int32_t k = 0;
        for (b2Contact *c = w->GetContactList(); c && k < n; c = c->GetNext()) cscratch[k++] = c;
        CMap &old = cmap[cmap_cur], &nw = cmap[cmap_cur ^ 1];
        if (!nw.reset((uint32_t)k + 1)) return;
        for (int32_t i = k - 1; i >= 0; --i) {   /* the list is newest first: walk it oldest first */
            b2Contact *c = cscratch[i];
            int32_t s = old.find(c);
            nw.put(c, s ? s : ++serial_next);
        }
        cmap_cur ^= 1;
        for (int32_t i = ev_head; i < ev_n; ++i) if (ev[i].c) { ev[i].e.serial = cmap[cmap_cur].find(ev[i].c); ev[i].c = nullptr; }
    }
};

void Listener::BeginContact(b2Contact *c) { W->push(SP_EV_BEGIN, c); }
void Listener::EndContact(b2Contact *c) { W->push(SP_EV_END, c); }

/* Stage 3, H-CHAIN=one-sided (SP_SHAPE_ONE_SIDED): Box2D 2.3.1's edges and chains are two-sided; a one-sided one disables (for this step:
 * b2Contact::Update re-enables every contact before PreSolve) a contact whose other shape's centroid lies behind the touched edge -- the side
 * the right-hand normal of v1 -> v2 does not face (Box2D 2.4's convention). Sensors never reach PreSolve. No Box2D file is changed. */
static void one_sided_rule(b2Contact *c, b2Fixture *ef, int32 child, b2Fixture *of)
{
    b2EdgeShape e;
    const b2Shape *s = ef->GetShape();
    if (s->GetType() == b2Shape::e_chain) static_cast<const b2ChainShape *>(s)->GetChildEdge(&e, child);
    else if (s->GetType() == b2Shape::e_edge) e = *static_cast<const b2EdgeShape *>(s);
    else return;
    const b2Shape *os = of->GetShape();
    b2Vec2 q;
    if (os->GetType() == b2Shape::e_circle) q = static_cast<const b2CircleShape *>(os)->m_p;
    else if (os->GetType() == b2Shape::e_polygon) q = static_cast<const b2PolygonShape *>(os)->m_centroid;
    else q = of->GetBody()->GetLocalCenter();
    b2Vec2 ql = b2MulT(ef->GetBody()->GetTransform(), b2Mul(of->GetBody()->GetTransform(), q));
    b2Vec2 d = e.m_vertex2 - e.m_vertex1;
    b2Vec2 n(d.y, -d.x);
    if (b2Dot(n, ql - e.m_vertex1) < 0.0f) c->SetEnabled(false);
}
void Listener::PreSolve(b2Contact *c, const b2Manifold *)
{
    if (!W->one_sided) return;
    b2Fixture *fa = c->GetFixtureA(), *fb = c->GetFixtureB();
    if (W->shapes.v[h_slot((int32_t)(intptr_t)fa->GetUserData())].one_sided) one_sided_rule(c, fa, c->GetChildIndexA(), fb);
    else if (W->shapes.v[h_slot((int32_t)(intptr_t)fb->GetUserData())].one_sided) one_sided_rule(c, fb, c->GetChildIndexB(), fa);
}

bool UFilter::ShouldCollide(b2Fixture *a, b2Fixture *b)
{
    if (!on) return b2ContactFilter::ShouldCollide(a, b);
    int32_t ha = (int32_t)(intptr_t)a->GetUserData(), hb = (int32_t)(intptr_t)b->GetUserData();
    const ShapeSlot &sa = W->shapes.v[(ha & 0xFFFF) - 1], &sb = W->shapes.v[(hb & 0xFFFF) - 1];
    uint32_t la = (uint32_t)sa.layer & 31u, lb = (uint32_t)sb.layer & 31u;
    bool hit = ((rows[la] >> lb) & 1u) != 0;
    if (((sa.inc >> lb) & 1u) || ((sb.inc >> la) & 1u)) hit = true;    /* includeLayers */
    if (((sa.exc >> lb) & 1u) || ((sb.exc >> la) & 1u)) hit = false;   /* excludeLayers (H-TIE exclude-wins; all priorities 0) */
    return hit && !ignored(ha, hb);
}

/* plain zero-initialised globals: no static constructors or destructors (the Vita link and bare-metal harnesses need none) */
enum { MAX_WORLDS = 8 };
World *g_worlds[MAX_WORLDS];
uint16_t g_world_gen[MAX_WORLDS];

World *get_world(int32_t h)
{
    int32_t s = h_slot(h);
    if (h <= 0 || s < 0 || s >= MAX_WORLDS) return nullptr;
    World *W = g_worlds[s];
    return (W && W->gen == h_gen(h)) ? W : nullptr;
}

b2Body *get_body(World *W, int32_t h)
{
    int32_t s = h_slot(h);
    if (h <= 0 || s < 0 || s >= W->bodies.n) return nullptr;
    BodySlot &b = W->bodies.v[s];
    return (b.body && b.gen == h_gen(h)) ? b.body : nullptr;
}

b2Fixture *get_fix(World *W, int32_t h)
{
    int32_t s = h_slot(h);
    if (h <= 0 || s < 0 || s >= W->shapes.n) return nullptr;
    ShapeSlot &f = W->shapes.v[s];
    return (f.fix && f.gen == h_gen(h)) ? f.fix : nullptr;
}

inline int32_t body_handle(b2Body *b) { return (int32_t)(intptr_t)b->GetUserData(); }
inline int32_t fix_handle(b2Fixture *f) { return (int32_t)(intptr_t)f->GetUserData(); }

/* Builds the Box2D shape for def/xy into the caller's storage. For CHAIN the vertices are owned by the b2ChainShape
 * (freed by its destructor). Returns the shape or nullptr on a bad definition. */
struct ShapeStore {
    b2CircleShape circle;
    b2PolygonShape poly;
    b2EdgeShape edge;
    b2ChainShape chain;
};

b2Shape *build_shape(const sp_shape_def *d, const float *xy, ShapeStore &st, bool allow_chain)
{
    switch (d->kind) {
    case SP_SHAPE_CIRCLE:
        if (!(d->radius > 0.0f)) return nullptr;
        st.circle.m_radius = d->radius;
        st.circle.m_p.Set(d->cx, d->cy);
        return &st.circle;
    case SP_SHAPE_BOX:
        if (!(d->hx > 0.0f) || !(d->hy > 0.0f)) return nullptr;
        if (d->cx == 0.0f && d->cy == 0.0f && d->angle == 0.0f) st.poly.SetAsBox(d->hx, d->hy);
        else st.poly.SetAsBox(d->hx, d->hy, b2Vec2(d->cx, d->cy), d->angle);
        return &st.poly;
    case SP_SHAPE_POLYGON: {
        if (!xy || d->vertex_count < 3 || d->vertex_count > b2_maxPolygonVertices) return nullptr;
        b2Vec2 v[b2_maxPolygonVertices];
        for (int32_t i = 0; i < d->vertex_count; ++i) v[i].Set(xy[2 * i], xy[2 * i + 1]);
        if (d->flags & SP_SHAPE_UNITY_WELD) b2_unityPolygonWeldSq = (0.5f * b2_linearSlop) * (0.5f * b2_linearSlop);   /* Stage 4b: patch P5 */
        st.poly.Set(v, d->vertex_count);
        b2_unityPolygonWeldSq = 0.0f;
        if ((d->flags & SP_SHAPE_RADIUS) && d->radius > 0.0f) st.poly.m_radius = d->radius;   /* Stage 1: rounded box / edge radius */
        return &st.poly;
    }
    case SP_SHAPE_EDGE:
        if (!xy || d->vertex_count != 4) return nullptr;
        st.edge.Set(b2Vec2(xy[2], xy[3]), b2Vec2(xy[4], xy[5]));
        if (d->flags & SP_SHAPE_GHOST_PREV) { st.edge.m_vertex0.Set(xy[0], xy[1]); st.edge.m_hasVertex0 = true; }
        if (d->flags & SP_SHAPE_GHOST_NEXT) { st.edge.m_vertex3.Set(xy[6], xy[7]); st.edge.m_hasVertex3 = true; }
        if ((d->flags & SP_SHAPE_RADIUS) && d->radius > 0.0f) st.edge.m_radius = d->radius;
        return &st.edge;
    case SP_SHAPE_CHAIN: {
        if (!allow_chain || !xy) return nullptr;
        int32_t n = d->vertex_count;
        bool loop = (d->flags & SP_SHAPE_LOOP) != 0;
        int32_t first = (!loop && (d->flags & SP_SHAPE_GHOST_PREV)) ? 1 : 0;
        int32_t last = (!loop && (d->flags & SP_SHAPE_GHOST_NEXT)) ? n - 1 : n;   /* exclusive */
        if (last - first < (loop ? 3 : 2)) return nullptr;
        b2Vec2 *v = (b2Vec2 *)malloc((size_t)n * sizeof(b2Vec2));
        if (!v) return nullptr;
        for (int32_t i = 0; i < n; ++i) v[i].Set(xy[2 * i], xy[2 * i + 1]);
        if (loop) st.chain.CreateLoop(v, n);
        else {
            st.chain.CreateChain(v + first, last - first);
            if (first) st.chain.SetPrevVertex(v[0]);
            if (last < n) st.chain.SetNextVertex(v[n - 1]);
        }
        free(v);
        if ((d->flags & SP_SHAPE_RADIUS) && d->radius > 0.0f) st.chain.m_radius = d->radius;
        return &st.chain;
    }
    default:
        return nullptr;
    }
}

/* ---- query callbacks ---- */
struct HitBuf {
    sp_ray_hit *v = nullptr;
    int32_t n = 0, cap = 0;
    bool full = false;
    void add(const sp_ray_hit &h)
    {
        if (!grow(v, cap, n + 1)) { full = true; return; }
        v[n++] = h;
    }
    ~HitBuf() { free(v); }
};

class RayCB : public b2RayCastCallback {
public:
    uint32_t mask = 0xFFFF;
    bool all = false, skip_sensors = false, have = false;
    sp_ray_hit best;
    HitBuf hits;
    float32 ReportFixture(b2Fixture *f, const b2Vec2 &p, const b2Vec2 &nrm, float32 fraction) override
    {
        if (skip_sensors && f->IsSensor()) return -1.0f;
        if ((f->GetFilterData().categoryBits & mask) == 0) return -1.0f;
        sp_ray_hit h;
        h.shape = fix_handle(f);
        h.body = body_handle(f->GetBody());
        h.child = -1;               /* Box2D 2.3.1 does not pass the child index to the callback */
        h.reserved = 0;
        h.px = p.x; h.py = p.y; h.nx = nrm.x; h.ny = nrm.y; h.fraction = fraction;
        if (all) { hits.add(h); return 1.0f; }
        best = h;                   /* clipping: Box2D only reports hits at or before the current fraction, the last wins */
        have = true;
        return fraction;
    }
};

struct ShapeList {
    int32_t *out;
    int32_t max, n = 0;
    /* every shape once, in first-report order (a chain has one proxy per edge and can be reported several times) */
    int32_t *seen = nullptr;
    int32_t nseen = 0, capseen = 0;
    bool add_unique(int32_t h)
    {
        for (int32_t i = 0; i < nseen; ++i) if (seen[i] == h) return false;
        if (grow(seen, capseen, nseen + 1)) seen[nseen++] = h;
        if (n < max && out) out[n] = h;
        n++;
        return true;
    }
    bool was_seen(int32_t h) const
    {
        for (int32_t i = 0; i < nseen; ++i) if (seen[i] == h) return true;
        return false;
    }
    void mark(int32_t h) { if (grow(seen, capseen, nseen + 1)) seen[nseen++] = h; }
    ~ShapeList() { free(seen); }
};

class AabbCB : public b2QueryCallback {
public:
    ShapeList *list = nullptr;
    uint32_t mask = 0xFFFF;
    bool skip_sensors = false;
    bool ReportFixture(b2Fixture *f) override
    {
        if (skip_sensors && f->IsSensor()) return true;
        if ((f->GetFilterData().categoryBits & mask) == 0) return true;
        list->add_unique(fix_handle(f));
        return true;
    }
};

class PointCB : public b2QueryCallback {
public:
    ShapeList *list = nullptr;
    uint32_t mask = 0xFFFF;
    b2Vec2 p;
    bool ReportFixture(b2Fixture *f) override
    {
        if ((f->GetFilterData().categoryBits & mask) == 0) return true;
        int32_t h = fix_handle(f);
        if (list->was_seen(h)) return true;
        if (f->TestPoint(p)) list->add_unique(h);
        else list->mark(h);
        return true;
    }
};

class OverlapCB : public b2QueryCallback {
public:
    ShapeList *list = nullptr;
    uint32_t mask = 0xFFFF;
    bool skip_sensors = false;
    const b2Shape *probe = nullptr;
    b2Transform xf;
    bool ReportFixture(b2Fixture *f) override
    {
        if (skip_sensors && f->IsSensor()) return true;
        if ((f->GetFilterData().categoryBits & mask) == 0) return true;
        int32_t h = fix_handle(f);
        if (list->was_seen(h)) return true;
        const b2Shape *s = f->GetShape();
        const b2Transform &bxf = f->GetBody()->GetTransform();
        bool hit = false;
        for (int32_t c = 0; c < s->GetChildCount() && !hit; ++c) hit = b2TestOverlap(probe, 0, s, c, xf, bxf);
        if (hit) list->add_unique(h);
        else list->mark(h);
        return true;
    }
};

} /* namespace */

/* ---- Stage 1: read access for shim/sp_unity.cpp (sp_internal.h); every mutation it makes goes through the traced C API ---- */
b2World *spi_world(int32_t world) { World *W = get_world(world); return W ? W->w : nullptr; }
b2Body *spi_body(int32_t world, int32_t body) { World *W = get_world(world); return W ? get_body(W, body) : nullptr; }
/* ---- Stage 2: read access for shim/sp_query.cpp ---- */
b2Fixture *spi_fixture(int32_t world, int32_t shape) { World *W = get_world(world); return W ? get_fix(W, shape) : nullptr; }
bool spi_shape_meta(int32_t world, int32_t shape, int32_t *user, int32_t *layer, float *uradius)
{
    World *W = get_world(world);
    if (!W || !get_fix(W, shape)) return false;
    const ShapeSlot &ss = W->shapes.v[h_slot(shape)];
    *user = ss.user; *layer = ss.layer; *uradius = ss.uradius;
    return true;
}
uint32_t spi_world_version(int32_t world) { World *W = get_world(world); return W ? W->version : 0; }
float spi_shape_bradius(int32_t world, int32_t shape) { World *W = get_world(world); return W && get_fix(W, shape) ? W->shapes.v[h_slot(shape)].bradius : 0.0f; }
int32_t spi_world_flags(int32_t world) { World *W = get_world(world); return W ? W->wflags : 0; }

/* ================================================================ C API */
extern "C" {

SP_API int32_t sp_abi(int32_t *sizes, int32_t max, float *probe)
{
    const int32_t s[SP_ABI_N_SIZES] = {
        (int32_t)sizeof(sp_world_def), (int32_t)sizeof(sp_body_def), (int32_t)sizeof(sp_shape_def), (int32_t)sizeof(sp_pose),
        (int32_t)sizeof(sp_step_def), (int32_t)sizeof(sp_step_stats), (int32_t)sizeof(sp_event), (int32_t)sizeof(sp_contact),
        (int32_t)sizeof(sp_ray_query), (int32_t)sizeof(sp_ray_hit), (int32_t)sizeof(sp_aabb_query),
        (int32_t)sizeof(sp_init_def), (int32_t)sizeof(sp_header), (int32_t)sizeof(sp_pool_stats), (int32_t)sizeof(sp_settings),
        (int32_t)sizeof(sp_settings_row),
        (int32_t)sizeof(sp_shape_unity), (int32_t)sizeof(sp_uworld_def), (int32_t)sizeof(sp_mirror), (int32_t)sizeof(sp_ubody_def),
        (int32_t)sizeof(sp_ushape_def), (int32_t)sizeof(sp_usync), (int32_t)sizeof(sp_ustep_def), (int32_t)sizeof(sp_ustep_out),
        (int32_t)sizeof(sp_render_def), (int32_t)sizeof(sp_rpose),
        (int32_t)sizeof(sp_event2), (int32_t)sizeof(sp_uquery), (int32_t)sizeof(sp_uhit),
        (int32_t)sizeof(sp_ucontact),
        (int32_t)sizeof(sp_mesh3d_def), (int32_t)sizeof(sp_col3d_def), (int32_t)sizeof(sp_ray3d_query), (int32_t)sizeof(sp_ray3d_hit)};
    for (int32_t i = 0; sizes && i < max && i < SP_ABI_N_SIZES; ++i) sizes[i] = s[i];
    if (probe) {
        volatile float a = 1.5f, b = 0.25f, c = 2.0f, d = 24.75f;
        *probe = a + b * 3.0f + c * d;
    }
    return SHEPPHYS_ABI_VERSION;
}

SP_API int32_t sp_version(void) { return SP_SHIM_VERSION; }

SP_API const char *sp_build_info(void)
{
    return "shepphys " "0.9.0" " box2d 2.3.1+P1+P2+P5 trig=" SP_TRIG_MODE B2_MODE " cc=" SP_CC " flags=" SHEPPHYS_FLAGS;
}

static int32_t impl_world_create(const sp_world_def *def)
{
    if (!def) return SP_ERR_PARAM;
    int32_t s = -1;
    for (int32_t i = 0; i < MAX_WORLDS; ++i) if (!g_worlds[i]) { s = i; break; }
    if (s < 0) return SP_ERR_FULL;
    void *mem = malloc(sizeof(World));
    if (!mem) return SP_ERR_FULL;
    World *W = new (mem) World();
    W->w = new (W->w_mem) b2World(b2Vec2(def->gravity_x, def->gravity_y));
    W->w->SetAllowSleeping((def->flags & SP_WORLD_ALLOW_SLEEP) != 0);
    W->w->SetContinuousPhysics((def->flags & SP_WORLD_CONTINUOUS) != 0);
    W->w->SetWarmStarting((def->flags & SP_WORLD_WARM_START) != 0);
    W->w->SetSubStepping((def->flags & SP_WORLD_SUB_STEP) != 0);
    W->listener.W = W;
    W->w->SetContactListener(&W->listener);
    W->filter.W = W;
    W->w->SetContactFilter(&W->filter);   /* off until sp_world_set_matrix: then exactly b2ContactFilter's stock rule */
    W->wflags = def->flags;               /* Stage 2: P1 (set around this world's steps only) and contact serials */
    g_world_gen[s] = next_gen(g_world_gen[s]);
    W->gen = g_world_gen[s];
    g_worlds[s] = W;
    return mk_handle(s, W->gen);
}

static int32_t impl_world_destroy(int32_t world)
{
    World *W = get_world(world);
    if (!W) return SP_ERR_HANDLE;
    if (W->w->IsLocked()) return SP_ERR_LOCKED;
    W->w->SetContactListener(nullptr);   /* stock ~b2World frees everything without EndContact callbacks */
    W->w->~b2World();
    W->w = nullptr;
    W->bodies.destroy();
    W->shapes.destroy();
    free(W->ev);
    free(W->cmap[0].t); free(W->cmap[1].t); free(W->cscratch);
    free(W->filter.ign);
    g_worlds[h_slot(world)] = nullptr;
    W->~World();
    free(W);
    return SP_OK;
}

static int32_t impl_world_step(int32_t world, const sp_step_def *step, sp_step_stats *stats)
{
    World *W = get_world(world);
    if (!W) return SP_ERR_HANDLE;
    if (!step) return SP_ERR_PARAM;
    if (W->w->IsLocked()) return SP_ERR_LOCKED;
    /* Stage 2, patch P1 (box2d/PATCHES.md): Box2D's two runtime flags hold this world's setting for this step only (0 = stock) */
    b2_unityTriggerPhase = (W->wflags & SP_WORLD_TRIGGER_PHASE) ? 1 : 0;
    b2_unityKinematicTriggers = (W->wflags & SP_WORLD_KINEMATIC_TRIGGERS) ? 1 : 0;
    /* E1, patch P2: Box2D's speed clamps hold this world's values for this step only (0 = stock) */
    b2_unityMaxTranslation = W->max_translation;
    b2_unityMaxRotation = W->max_rotation;
    W->w->Step(step->dt, step->velocity_iterations, step->position_iterations);
    b2_unityTriggerPhase = 0;
    b2_unityKinematicTriggers = 0;
    b2_unityMaxTranslation = 0.0f;
    b2_unityMaxRotation = 0.0f;
    W->version++;
    if (W->wflags & SP_WORLD_CONTACT_SERIALS) W->serials();
    int32_t taken = W->step++;
    if (stats) {
        stats->step = taken;
        stats->bodies = W->w->GetBodyCount();
        int32_t awake = 0;
        for (b2Body *b = W->w->GetBodyList(); b; b = b->GetNext()) awake += b->IsAwake() ? 1 : 0;
        stats->awake_bodies = awake;
        stats->shapes = W->shapes.live;
        stats->contacts = W->w->GetContactCount();
        int32_t touching = 0;
        for (b2Contact *c = W->w->GetContactList(); c; c = c->GetNext()) touching += c->IsTouching() ? 1 : 0;
        stats->touching = touching;
        stats->proxies = W->w->GetProxyCount();
        stats->tree_height = W->w->GetTreeHeight();
        stats->events_queued = W->ev_n - W->ev_head;
    }
    return SP_OK;
}

/* E1, patch P2 (box2d/PATCHES.md): Unity's per-step clamps for this world's steps: tr[0] the largest translation in one step (units),
 * tr[1] the largest rotation in one step (radians); a value <= 0 (or not finite) keeps Box2D's compiled one */
static int32_t impl_world_set_clamps(int32_t world, const float *tr)
{
    World *W = get_world(world);
    if (!W) return SP_ERR_HANDLE;
    if (!tr) return SP_ERR_PARAM;
    if (W->w->IsLocked()) return SP_ERR_LOCKED;
    W->max_translation = tr[0] > 0.0f && tr[0] < 3.0e38f ? tr[0] : 0.0f;
    W->max_rotation = tr[1] > 0.0f && tr[1] < 3.0e38f ? tr[1] : 0.0f;
    return SP_OK;
}

static int32_t impl_world_set_gravity(int32_t world, const float *gxy)
{
    World *W = get_world(world);
    if (!W) return SP_ERR_HANDLE;
    if (!gxy) return SP_ERR_PARAM;
    W->w->SetGravity(b2Vec2(gxy[0], gxy[1]));
    return SP_OK;
}

static int32_t impl_body_create(int32_t world, const sp_body_def *def)
{
    World *W = get_world(world);
    if (!W) return SP_ERR_HANDLE;
    if (!def || def->type < SP_BODY_STATIC || def->type > SP_BODY_DYNAMIC) return SP_ERR_PARAM;
    if (W->w->IsLocked()) return SP_ERR_LOCKED;
    int32_t s = W->bodies.alloc();
    if (s < 0) return SP_ERR_FULL;
    int32_t h = mk_handle(s, W->bodies.v[s].gen);
    b2BodyDef bd;
    bd.type = (b2BodyType)def->type;
    bd.position.Set(def->x, def->y);
    bd.angle = def->angle;
    bd.linearVelocity.Set(def->vx, def->vy);
    bd.angularVelocity = def->w;
    bd.linearDamping = def->linear_damping;
    bd.angularDamping = def->angular_damping;
    bd.gravityScale = def->gravity_scale;
    bd.awake = (def->flags & SP_BODY_AWAKE) != 0;
    bd.allowSleep = (def->flags & SP_BODY_ALLOW_SLEEP) != 0;
    bd.fixedRotation = (def->flags & SP_BODY_FIXED_ROTATION) != 0;
    bd.bullet = (def->flags & SP_BODY_BULLET) != 0;
    bd.active = (def->flags & SP_BODY_ACTIVE) != 0;
    bd.userData = (void *)(intptr_t)h;
    b2Body *b = W->w->CreateBody(&bd);
    if (!b) { W->bodies.release(s); return SP_ERR_FULL; }
    W->bodies.v[s].body = b;
    W->bodies.v[s].user = def->user;
    return h;
}

static int32_t impl_body_destroy(int32_t world, int32_t body)
{
    World *W = get_world(world);
    if (!W) return SP_ERR_HANDLE;
    b2Body *b = get_body(W, body);
    if (!b) return SP_ERR_HANDLE;
    if (W->w->IsLocked()) return SP_ERR_LOCKED;
    /* the fixtures' slots stay valid during DestroyBody so its EndContact events carry live handles */
    int32_t victims[64], nv = 0;
    int32_t *heap = nullptr, cap = 0;
    for (b2Fixture *f = b->GetFixtureList(); f; f = f->GetNext()) {
        if (nv < 64) victims[nv++] = fix_handle(f);
        else { if (grow(heap, cap, nv - 64 + 1)) heap[nv - 64] = fix_handle(f); nv++; }
    }
    W->w->DestroyBody(b);
    for (int32_t i = 0; i < nv; ++i) {
        int32_t fh = i < 64 ? victims[i] : heap[i - 64];
        int32_t s = h_slot(fh);
        W->shapes.v[s].fix = nullptr;
        W->shapes.release(s);
    }
    free(heap);
    int32_t s = h_slot(body);
    W->bodies.v[s].body = nullptr;
    W->bodies.release(s);
    W->version++;
    return SP_OK;
}

static void fill_pose(b2Body *b, int32_t h, int32_t user, sp_pose *o)
{
    const b2Vec2 &p = b->GetPosition();
    const b2Vec2 &v = b->GetLinearVelocity();
    o->x = p.x; o->y = p.y; o->angle = b->GetAngle();
    o->vx = v.x; o->vy = v.y; o->w = b->GetAngularVelocity();
    o->flags = (b->IsAwake() ? 1 : 0) | ((int32_t)b->GetType() << 8);
    o->body = h;
    o->user = user;
}

SP_API int32_t sp_body_get_pose(int32_t world, int32_t body, sp_pose *out)
{
    World *W = get_world(world);
    if (!W) return SP_ERR_HANDLE;
    b2Body *b = get_body(W, body);
    if (!b) return SP_ERR_HANDLE;
    if (!out) return SP_ERR_PARAM;
    fill_pose(b, body, W->bodies.v[h_slot(body)].user, out);
    return SP_OK;
}

SP_API int32_t sp_poses(int32_t world, sp_pose *out, int32_t max)
{
    World *W = get_world(world);
    if (!W) return SP_ERR_HANDLE;
    int32_t n = 0;
    for (int32_t s = 0; s < W->bodies.n; ++s) {
        BodySlot &bs = W->bodies.v[s];
        if (!bs.body) continue;
        if (out && n < max) fill_pose(bs.body, mk_handle(s, bs.gen), bs.user, &out[n]);
        n++;
    }
    return n;
}

static int32_t impl_body_set_transform(int32_t world, int32_t body, const float *xya)
{
    World *W = get_world(world);
    if (!W) return SP_ERR_HANDLE;
    b2Body *b = get_body(W, body);
    if (!b) return SP_ERR_HANDLE;
    if (!xya) return SP_ERR_PARAM;
    if (W->w->IsLocked()) return SP_ERR_LOCKED;
    b->SetTransform(b2Vec2(xya[0], xya[1]), xya[2]);
    W->version++;
    return SP_OK;
}

static int32_t impl_body_set_velocity(int32_t world, int32_t body, const float *vxvyw)
{
    World *W = get_world(world);
    if (!W) return SP_ERR_HANDLE;
    b2Body *b = get_body(W, body);
    if (!b) return SP_ERR_HANDLE;
    if (!vxvyw) return SP_ERR_PARAM;
    b->SetLinearVelocity(b2Vec2(vxvyw[0], vxvyw[1]));
    b->SetAngularVelocity(vxvyw[2]);
    return SP_OK;
}

static int32_t impl_body_apply(int32_t world, int32_t body, int32_t kind, const float *args, int32_t wake)
{
    World *W = get_world(world);
    if (!W) return SP_ERR_HANDLE;
    b2Body *b = get_body(W, body);
    if (!b) return SP_ERR_HANDLE;
    if (!args) return SP_ERR_PARAM;
    bool wk = wake != 0;
    switch (kind) {
    case SP_APPLY_FORCE: b->ApplyForceToCenter(b2Vec2(args[0], args[1]), wk); break;
    case SP_APPLY_IMPULSE: b->ApplyLinearImpulse(b2Vec2(args[0], args[1]), b->GetWorldCenter(), wk); break;
    case SP_APPLY_TORQUE: b->ApplyTorque(args[0], wk); break;
    case SP_APPLY_ANGULAR_IMPULSE: b->ApplyAngularImpulse(args[0], wk); break;
    default: return SP_ERR_PARAM;
    }
    return SP_OK;
}

static int32_t impl_body_set_type(int32_t world, int32_t body, int32_t type)
{
    World *W = get_world(world);
    if (!W) return SP_ERR_HANDLE;
    b2Body *b = get_body(W, body);
    if (!b) return SP_ERR_HANDLE;
    if (type < SP_BODY_STATIC || type > SP_BODY_DYNAMIC) return SP_ERR_PARAM;
    if (W->w->IsLocked()) return SP_ERR_LOCKED;
    b->SetType((b2BodyType)type);
    W->version++;
    return SP_OK;
}

static int32_t impl_body_set_flag(int32_t world, int32_t body, int32_t flag, int32_t on)
{
    World *W = get_world(world);
    if (!W) return SP_ERR_HANDLE;
    b2Body *b = get_body(W, body);
    if (!b) return SP_ERR_HANDLE;
    bool v = on != 0;
    switch (flag) {
    case SP_BODY_AWAKE: b->SetAwake(v); break;
    case SP_BODY_ALLOW_SLEEP: b->SetSleepingAllowed(v); break;
    case SP_BODY_FIXED_ROTATION: b->SetFixedRotation(v); break;
    case SP_BODY_BULLET: b->SetBullet(v); break;
    case SP_BODY_ACTIVE:
        if (W->w->IsLocked()) return SP_ERR_LOCKED;
        b->SetActive(v);
        W->version++;
        break;
    default: return SP_ERR_PARAM;
    }
    return SP_OK;
}

static int32_t impl_body_set_float(int32_t world, int32_t body, int32_t prop, const float *value)
{
    World *W = get_world(world);
    if (!W) return SP_ERR_HANDLE;
    b2Body *b = get_body(W, body);
    if (!b) return SP_ERR_HANDLE;
    if (!value) return SP_ERR_PARAM;
    switch (prop) {
    case SP_PROP_LINEAR_DAMPING: b->SetLinearDamping(*value); break;
    case SP_PROP_ANGULAR_DAMPING: b->SetAngularDamping(*value); break;
    case SP_PROP_GRAVITY_SCALE: b->SetGravityScale(*value); break;
    default: return SP_ERR_PARAM;
    }
    return SP_OK;
}

static int32_t impl_shape_create(int32_t world, int32_t body, const sp_shape_def *def, const float *xy)
{
    World *W = get_world(world);
    if (!W) return SP_ERR_HANDLE;
    b2Body *b = get_body(W, body);
    if (!b) return SP_ERR_HANDLE;
    if (!def) return SP_ERR_PARAM;
    if (W->w->IsLocked()) return SP_ERR_LOCKED;
    ShapeStore st;
    b2Shape *shape = build_shape(def, xy, st, true);
    if (!shape) return SP_ERR_PARAM;
    int32_t s = W->shapes.alloc();
    if (s < 0) return SP_ERR_FULL;
    int32_t h = mk_handle(s, W->shapes.v[s].gen);
    W->shapes.v[s].layer = 0; W->shapes.v[s].inc = 0; W->shapes.v[s].exc = 0;   /* (CreateFixture below may already filter pairs) */
    b2FixtureDef fd;
    fd.shape = shape;
    fd.density = def->density;
    fd.friction = def->friction;
    fd.restitution = def->restitution;
    fd.isSensor = (def->flags & SP_SHAPE_SENSOR) != 0;
    fd.filter.categoryBits = (uint16)def->category_bits;
    fd.filter.maskBits = (uint16)def->mask_bits;
    fd.filter.groupIndex = (int16)def->group_index;
    fd.userData = (void *)(intptr_t)h;
    b2Fixture *f = b->CreateFixture(&fd);
    if (!f) { W->shapes.release(s); return SP_ERR_FULL; }
    W->shapes.v[s].fix = f;
    W->shapes.v[s].body = body;
    W->shapes.v[s].layer = 0; W->shapes.v[s].inc = 0; W->shapes.v[s].exc = 0;   /* Stage 1: the Unity filter's data, until set */
    /* Stage 2: the collider and its own rounding (queries) */
    W->shapes.v[s].user = def->user;
    float ur = 0.0f;
    if (def->kind == SP_SHAPE_CIRCLE) ur = def->radius;
    else if (def->kind != SP_SHAPE_BOX && (def->flags & SP_SHAPE_RADIUS) && def->radius > 0.0f)
        ur = (def->flags & SP_SHAPE_NO_SKIN) ? def->radius : (def->radius > b2_polygonRadius ? def->radius - b2_polygonRadius : 0.0f);
    W->shapes.v[s].uradius = ur;
    W->shapes.v[s].bradius = def->kind == SP_SHAPE_CIRCLE ? def->radius : ((def->flags & SP_SHAPE_NO_SKIN) && (def->flags & SP_SHAPE_RADIUS)) ? def->radius : 0.0f;
    /* Stage 3: H-CHAIN=one-sided (edges and chains only) */
    W->shapes.v[s].one_sided = ((def->flags & SP_SHAPE_ONE_SIDED) && (def->kind == SP_SHAPE_EDGE || def->kind == SP_SHAPE_CHAIN)) ? 1 : 0;
    if (W->shapes.v[s].one_sided) W->one_sided++;
    W->version++;
    return h;
}

static int32_t impl_shape_destroy(int32_t world, int32_t shape)
{
    World *W = get_world(world);
    if (!W) return SP_ERR_HANDLE;
    b2Fixture *f = get_fix(W, shape);
    if (!f) return SP_ERR_HANDLE;
    if (W->w->IsLocked()) return SP_ERR_LOCKED;
    f->GetBody()->DestroyFixture(f);
    int32_t s = h_slot(shape);
    W->shapes.v[s].fix = nullptr;
    W->shapes.release(s);
    W->version++;
    return SP_OK;
}

static int32_t impl_shape_set_sensor(int32_t world, int32_t shape, int32_t on)
{
    World *W = get_world(world);
    if (!W) return SP_ERR_HANDLE;
    b2Fixture *f = get_fix(W, shape);
    if (!f) return SP_ERR_HANDLE;
    f->SetSensor(on != 0);
    return SP_OK;
}

static int32_t impl_shape_set_filter(int32_t world, int32_t shape, const uint32_t *cmg)
{
    World *W = get_world(world);
    if (!W) return SP_ERR_HANDLE;
    b2Fixture *f = get_fix(W, shape);
    if (!f) return SP_ERR_HANDLE;
    if (!cmg) return SP_ERR_PARAM;
    b2Filter fl;
    fl.categoryBits = (uint16)cmg[0];
    fl.maskBits = (uint16)cmg[1];
    fl.groupIndex = (int16)(int32_t)cmg[2];
    f->SetFilterData(fl);
    return SP_OK;
}

/* ---- Stage 1: the Unity filter and mass data (traced below) ---- */
static int32_t impl_world_set_matrix(int32_t world, const uint32_t *rows32)
{
    World *W = get_world(world);
    if (!W) return SP_ERR_HANDLE;
    if (W->w->IsLocked()) return SP_ERR_LOCKED;
    if (rows32) { memcpy(W->filter.rows, rows32, sizeof W->filter.rows); W->filter.on = true; }
    else W->filter.on = false;
    for (b2Body *b = W->w->GetBodyList(); b; b = b->GetNext())
        for (b2Fixture *f = b->GetFixtureList(); f; f = f->GetNext()) f->Refilter();
    W->version++;
    return SP_OK;
}

static int32_t impl_shape_set_unity(int32_t world, int32_t shape, const sp_shape_unity *u)
{
    World *W = get_world(world);
    if (!W) return SP_ERR_HANDLE;
    b2Fixture *f = get_fix(W, shape);
    if (!f) return SP_ERR_HANDLE;
    if (!u || u->layer < 0 || u->layer > 31) return SP_ERR_PARAM;
    if (W->w->IsLocked()) return SP_ERR_LOCKED;
    ShapeSlot &ss = W->shapes.v[h_slot(shape)];
    bool same = ss.layer == u->layer && ss.inc == u->include_layers && ss.exc == u->exclude_layers;
    ss.layer = u->layer; ss.inc = u->include_layers; ss.exc = u->exclude_layers;
    if (!same) { f->Refilter(); W->version++; }
    return SP_OK;
}

static int32_t impl_world_ignore_pair(int32_t world, int32_t a, int32_t b, int32_t ignore)
{
    World *W = get_world(world);
    if (!W) return SP_ERR_HANDLE;
    b2Fixture *fa = get_fix(W, a), *fb = get_fix(W, b);
    if (!fa || !fb) return SP_ERR_HANDLE;
    if (W->w->IsLocked()) return SP_ERR_LOCKED;
    if (!W->filter.set_ignored(a, b, ignore != 0)) return SP_ERR_FULL;
    fa->Refilter();   /* flags fa's contacts (incl. the pair) for filtering; a new pair is re-tested when the broadphase finds it */
    return SP_OK;
}

static int32_t impl_body_set_mass(int32_t world, int32_t body, const float *m)
{
    World *W = get_world(world);
    if (!W) return SP_ERR_HANDLE;
    b2Body *b = get_body(W, body);
    if (!b) return SP_ERR_HANDLE;
    if (W->w->IsLocked()) return SP_ERR_LOCKED;
    if (!m) { b->ResetMassData(); return SP_OK; }
    b2MassData md;
    md.mass = m[0]; md.center.Set(m[1], m[2]);
    md.I = m[3] + m[0] * b2Dot(md.center, md.center);   /* b2MassData.I is about the body origin */
    b->SetMassData(&md);
    return SP_OK;
}

SP_API int32_t sp_body_get_mass(int32_t world, int32_t body, float *m)
{
    World *W = get_world(world);
    if (!W) return SP_ERR_HANDLE;
    b2Body *b = get_body(W, body);
    if (!b) return SP_ERR_HANDLE;
    if (!m) return SP_ERR_PARAM;
    b2MassData md;
    b->GetMassData(&md);   /* I about the origin -> about the centre */
    m[0] = md.mass; m[1] = md.center.x; m[2] = md.center.y; m[3] = md.I - md.mass * b2Dot(md.center, md.center);
    return SP_OK;
}

SP_API int32_t sp_shape_body(int32_t world, int32_t shape)
{
    World *W = get_world(world);
    if (!W) return SP_ERR_HANDLE;
    b2Fixture *f = get_fix(W, shape);
    if (!f) return SP_ERR_HANDLE;
    return body_handle(f->GetBody());
}

SP_API int32_t sp_shape_get_polygon(int32_t world, int32_t shape, float *xy, int32_t max)
{
    World *W = get_world(world);
    if (!W) return SP_ERR_HANDLE;
    b2Fixture *f = get_fix(W, shape);
    if (!f) return SP_ERR_HANDLE;
    if (f->GetType() != b2Shape::e_polygon) return 0;
    const b2PolygonShape *p = (const b2PolygonShape *)f->GetShape();
    for (int32_t i = 0; xy && i < p->m_count && i < max; ++i) { xy[2 * i] = p->m_vertices[i].x; xy[2 * i + 1] = p->m_vertices[i].y; }
    return p->m_count;
}

static int32_t impl_events(int32_t world, sp_event *out, int32_t max)
{
    World *W = get_world(world);
    if (!W) return SP_ERR_HANDLE;
    if (!out && max > 0) return SP_ERR_PARAM;
    int32_t n = 0;
    while (W->ev_head < W->ev_n && n < max) {
        const sp_event2 &e = W->ev[W->ev_head++].e;
        sp_event &o = out[n++];
        o.kind = e.kind; o.flags = e.flags; o.shape_a = e.shape_a; o.shape_b = e.shape_b; o.step = e.step;
    }
    if (W->ev_head == W->ev_n) W->ev_head = W->ev_n = 0;
    return n;
}

static int32_t impl_events2(int32_t world, sp_event2 *out, int32_t max)
{
    World *W = get_world(world);
    if (!W) return SP_ERR_HANDLE;
    if (!out && max > 0) return SP_ERR_PARAM;
    int32_t n = 0;
    while (W->ev_head < W->ev_n && n < max) out[n++] = W->ev[W->ev_head++].e;
    if (W->ev_head == W->ev_n) W->ev_head = W->ev_n = 0;
    return n;
}

SP_API int32_t sp_contacts(int32_t world, sp_contact *out, int32_t max, int32_t flags)
{
    World *W = get_world(world);
    if (!W) return SP_ERR_HANDLE;
    int32_t n = 0;
    for (b2Contact *c = W->w->GetContactList(); c; c = c->GetNext()) {
        b2Fixture *fa = c->GetFixtureA(), *fb = c->GetFixtureB();
        bool sensor = fa->IsSensor() || fb->IsSensor();
        if ((flags & SP_CONTACTS_TOUCHING_ONLY) && !c->IsTouching()) continue;
        if ((flags & SP_CONTACTS_NO_SENSORS) && sensor) continue;
        if (out && n < max) {
            sp_contact &o = out[n];
            memset(&o, 0, sizeof o);
            o.shape_a = fix_handle(fa); o.shape_b = fix_handle(fb);
            o.body_a = body_handle(fa->GetBody()); o.body_b = body_handle(fb->GetBody());
            o.child_a = c->GetChildIndexA(); o.child_b = c->GetChildIndexB();
            o.flags = (c->IsTouching() ? SP_CF_TOUCHING : 0) | (sensor ? SP_CF_SENSOR : 0) | (c->IsEnabled() ? SP_CF_ENABLED : 0);
            const b2Manifold *m = c->GetManifold();
            o.point_count = m->pointCount;
            if (m->pointCount > 0) {
                b2WorldManifold wm;
                c->GetWorldManifold(&wm);
                o.normal_x = wm.normal.x; o.normal_y = wm.normal.y;
                for (int32_t i = 0; i < m->pointCount && i < 2; ++i) {
                    o.px[i] = wm.points[i].x; o.py[i] = wm.points[i].y;
                    o.separation[i] = wm.separations[i];
                    o.normal_impulse[i] = m->points[i].normalImpulse;
                    o.tangent_impulse[i] = m->points[i].tangentImpulse;
                }
            }
            o.friction = c->GetFriction();
            o.restitution = c->GetRestitution();
        }
        n++;
    }
    return n;
}

SP_API int32_t sp_raycast(int32_t world, const sp_ray_query *q, sp_ray_hit *out, int32_t max)
{
    World *W = get_world(world);
    if (!W) return SP_ERR_HANDLE;
    if (!q) return SP_ERR_PARAM;
    RayCB cb;
    cb.mask = q->mask_bits;
    cb.all = (q->flags & SP_RAY_ALL) != 0;
    cb.skip_sensors = (q->flags & SP_RAY_SKIP_SENSORS) != 0;
    b2Vec2 p1(q->x1, q->y1), p2(q->x2, q->y2);
    if (b2DistanceSquared(p1, p2) <= 0.0f) return 0;   /* Box2D asserts on a zero-length ray */
    W->w->RayCast(&cb, p1, p2);
    if (!cb.all) {
        if (!cb.have) return 0;
        if (out && max > 0) out[0] = cb.best;
        return 1;
    }
    if (cb.hits.full) return SP_ERR_FULL;
    if (q->flags & SP_RAY_SORTED) {                     /* stable insertion sort by fraction (ties keep report order) */
        for (int32_t i = 1; i < cb.hits.n; ++i) {
            sp_ray_hit h = cb.hits.v[i];
            int32_t j = i - 1;
            while (j >= 0 && cb.hits.v[j].fraction > h.fraction) { cb.hits.v[j + 1] = cb.hits.v[j]; --j; }
            cb.hits.v[j + 1] = h;
        }
    }
    for (int32_t i = 0; out && i < cb.hits.n && i < max; ++i) out[i] = cb.hits.v[i];
    return cb.hits.n;
}

SP_API int32_t sp_query_aabb(int32_t world, const sp_aabb_query *q, int32_t *shapes, int32_t max)
{
    World *W = get_world(world);
    if (!W) return SP_ERR_HANDLE;
    if (!q) return SP_ERR_PARAM;
    ShapeList list;
    list.out = shapes; list.max = max;
    AabbCB cb;
    cb.list = &list; cb.mask = q->mask_bits; cb.skip_sensors = (q->flags & SP_QUERY_SKIP_SENSORS) != 0;
    b2AABB a;
    a.lowerBound.Set(q->lower_x, q->lower_y);
    a.upperBound.Set(q->upper_x, q->upper_y);
    W->w->QueryAABB(&cb, a);
    return list.n;
}

SP_API int32_t sp_query_point(int32_t world, const float *xy, uint32_t mask_bits, int32_t *shapes, int32_t max)
{
    World *W = get_world(world);
    if (!W) return SP_ERR_HANDLE;
    if (!xy) return SP_ERR_PARAM;
    ShapeList list;
    list.out = shapes; list.max = max;
    PointCB cb;
    cb.list = &list; cb.mask = mask_bits; cb.p.Set(xy[0], xy[1]);
    b2AABB a;
    a.lowerBound = cb.p;          /* a degenerate box: the tree's fat AABBs still contain the point */
    a.upperBound = cb.p;
    W->w->QueryAABB(&cb, a);
    return list.n;
}

SP_API int32_t sp_overlap_shape(int32_t world, const sp_shape_def *def, const float *xy, const float *xf,
                                uint32_t mask_bits, int32_t flags, int32_t *shapes, int32_t max)
{
    World *W = get_world(world);
    if (!W) return SP_ERR_HANDLE;
    if (!def || !xf) return SP_ERR_PARAM;
    ShapeStore st;
    b2Shape *probe = build_shape(def, xy, st, false);
    if (!probe) return SP_ERR_PARAM;
    ShapeList list;
    list.out = shapes; list.max = max;
    OverlapCB cb;
    cb.list = &list; cb.mask = mask_bits; cb.skip_sensors = (flags & SP_QUERY_SKIP_SENSORS) != 0;
    cb.probe = probe;
    cb.xf.Set(b2Vec2(xf[0], xf[1]), xf[2]);
    b2AABB a;
    probe->ComputeAABB(&a, cb.xf, 0);
    W->w->QueryAABB(&cb, a);
    return list.n;
}

/* ---- Stage 0: the mutating entry points, traced (shim/sp_trace.cpp). Each one is the stage-0a body (impl_*) plus, while a trace
 * is open, one record of its arguments and result; queries are not recorded (they change nothing). With no trace open the cost
 * is one predictable branch. ---- */
SP_API int32_t sp_world_create(const sp_world_def *def)
{
    int32_t r = impl_world_create(def);
    if (sp_tr_on) { int32_t a[2] = {r, def != nullptr}; sp_tr_rec(SP_TR_WORLD_CREATE, a, sizeof a, def, def ? sizeof *def : 0); }
    return r;
}
SP_API int32_t sp_world_destroy(int32_t world)
{
    int32_t r = impl_world_destroy(world);
    if (sp_tr_on) { int32_t a[2] = {world, r}; sp_tr_rec(SP_TR_WORLD_DESTROY, a, sizeof a, nullptr, 0); }
    return r;
}
SP_API int32_t sp_world_step(int32_t world, const sp_step_def *step, sp_step_stats *stats)
{
    int32_t r = impl_world_step(world, step, stats);
    if (sp_tr_on) {
        struct { int32_t world, r, has; uint32_t hash_lo, hash_hi; sp_step_def d; } a;
        uint64_t h = r == SP_OK ? sp_tr_pose_hash(world) : 0;
        a.world = world; a.r = r; a.has = step != nullptr; a.hash_lo = (uint32_t)h; a.hash_hi = (uint32_t)(h >> 32);
        if (step) a.d = *step; else memset(&a.d, 0, sizeof a.d);
        sp_tr_rec(SP_TR_WORLD_STEP, &a, sizeof a, nullptr, 0);
    }
    return r;
}
SP_API int32_t sp_world_set_clamps(int32_t world, const float *trans_rot)
{
    int32_t r = impl_world_set_clamps(world, trans_rot);
    if (sp_tr_on) { int32_t a[3] = {world, r, trans_rot != nullptr}; sp_tr_rec(SP_TR_WORLD_SET_CLAMPS, a, sizeof a, trans_rot, trans_rot ? 8 : 0); }
    return r;
}
SP_API int32_t sp_world_set_gravity(int32_t world, const float *gxy)
{
    int32_t r = impl_world_set_gravity(world, gxy);
    if (sp_tr_on) { int32_t a[3] = {world, r, gxy != nullptr}; sp_tr_rec(SP_TR_WORLD_SET_GRAVITY, a, sizeof a, gxy, gxy ? 8 : 0); }
    return r;
}
SP_API int32_t sp_body_create(int32_t world, const sp_body_def *def)
{
    int32_t r = impl_body_create(world, def);
    if (sp_tr_on) { int32_t a[3] = {world, r, def != nullptr}; sp_tr_rec(SP_TR_BODY_CREATE, a, sizeof a, def, def ? sizeof *def : 0); }
    return r;
}
SP_API int32_t sp_body_destroy(int32_t world, int32_t body)
{
    int32_t r = impl_body_destroy(world, body);
    if (sp_tr_on) { int32_t a[3] = {world, body, r}; sp_tr_rec(SP_TR_BODY_DESTROY, a, sizeof a, nullptr, 0); }
    return r;
}
SP_API int32_t sp_body_set_transform(int32_t world, int32_t body, const float *xya)
{
    int32_t r = impl_body_set_transform(world, body, xya);
    if (sp_tr_on) { int32_t a[4] = {world, body, r, xya != nullptr}; sp_tr_rec(SP_TR_BODY_SET_TRANSFORM, a, sizeof a, xya, xya ? 12 : 0); }
    return r;
}
SP_API int32_t sp_body_set_velocity(int32_t world, int32_t body, const float *vxvyw)
{
    int32_t r = impl_body_set_velocity(world, body, vxvyw);
    if (sp_tr_on) { int32_t a[4] = {world, body, r, vxvyw != nullptr}; sp_tr_rec(SP_TR_BODY_SET_VELOCITY, a, sizeof a, vxvyw, vxvyw ? 12 : 0); }
    return r;
}
SP_API int32_t sp_body_apply(int32_t world, int32_t body, int32_t kind, const float *args, int32_t wake)
{
    int32_t r = impl_body_apply(world, body, kind, args, wake);
    if (sp_tr_on) {
        /* the linear kinds read {x, y}, the angular ones {t} */
        uint32_t n = !args ? 0 : (kind == SP_APPLY_FORCE || kind == SP_APPLY_IMPULSE) ? 8 : (kind == SP_APPLY_TORQUE || kind == SP_APPLY_ANGULAR_IMPULSE) ? 4 : 0;
        int32_t a[6] = {world, body, kind, wake, r, args != nullptr};
        sp_tr_rec(SP_TR_BODY_APPLY, a, sizeof a, args, n);
    }
    return r;
}
SP_API int32_t sp_body_set_type(int32_t world, int32_t body, int32_t type)
{
    int32_t r = impl_body_set_type(world, body, type);
    if (sp_tr_on) { int32_t a[4] = {world, body, type, r}; sp_tr_rec(SP_TR_BODY_SET_TYPE, a, sizeof a, nullptr, 0); }
    return r;
}
SP_API int32_t sp_body_set_flag(int32_t world, int32_t body, int32_t flag, int32_t on)
{
    int32_t r = impl_body_set_flag(world, body, flag, on);
    if (sp_tr_on) { int32_t a[5] = {world, body, flag, on, r}; sp_tr_rec(SP_TR_BODY_SET_FLAG, a, sizeof a, nullptr, 0); }
    return r;
}
SP_API int32_t sp_body_set_float(int32_t world, int32_t body, int32_t prop, const float *value)
{
    int32_t r = impl_body_set_float(world, body, prop, value);
    if (sp_tr_on) { int32_t a[5] = {world, body, prop, r, value != nullptr}; sp_tr_rec(SP_TR_BODY_SET_FLOAT, a, sizeof a, value, value ? 4 : 0); }
    return r;
}
SP_API int32_t sp_shape_create(int32_t world, int32_t body, const sp_shape_def *def, const float *xy)
{
    int32_t r = impl_shape_create(world, body, def, xy);
    if (sp_tr_on) {
        /* the points build_shape reads: POLYGON / CHAIN vertex_count of them, EDGE 4, CIRCLE / BOX none */
        int32_t pts = 0;
        if (def && xy) pts = def->kind == SP_SHAPE_EDGE ? 4 : (def->kind == SP_SHAPE_POLYGON || def->kind == SP_SHAPE_CHAIN) ? def->vertex_count : 0;
        if (pts < 0 || pts > 65536) pts = 0;
        struct { int32_t world, body, r, has_def, has_xy, pts; sp_shape_def d; } a;
        a.world = world; a.body = body; a.r = r; a.has_def = def != nullptr; a.has_xy = xy != nullptr; a.pts = pts;
        if (def) a.d = *def; else memset(&a.d, 0, sizeof a.d);
        sp_tr_rec(SP_TR_SHAPE_CREATE, &a, sizeof a, xy, (uint32_t)pts * 8u);
    }
    return r;
}
SP_API int32_t sp_shape_destroy(int32_t world, int32_t shape)
{
    int32_t r = impl_shape_destroy(world, shape);
    if (sp_tr_on) { int32_t a[3] = {world, shape, r}; sp_tr_rec(SP_TR_SHAPE_DESTROY, a, sizeof a, nullptr, 0); }
    return r;
}
SP_API int32_t sp_shape_set_sensor(int32_t world, int32_t shape, int32_t on)
{
    int32_t r = impl_shape_set_sensor(world, shape, on);
    if (sp_tr_on) { int32_t a[4] = {world, shape, on, r}; sp_tr_rec(SP_TR_SHAPE_SET_SENSOR, a, sizeof a, nullptr, 0); }
    return r;
}
SP_API int32_t sp_shape_set_filter(int32_t world, int32_t shape, const uint32_t *cmg)
{
    int32_t r = impl_shape_set_filter(world, shape, cmg);
    if (sp_tr_on) { int32_t a[4] = {world, shape, r, cmg != nullptr}; sp_tr_rec(SP_TR_SHAPE_SET_FILTER, a, sizeof a, cmg, cmg ? 12 : 0); }
    return r;
}
SP_API int32_t sp_events(int32_t world, sp_event *out, int32_t max)
{
    int32_t r = impl_events(world, out, max);
    if (sp_tr_on) {
        uint64_t h = sp_tr_hash(out, r > 0 ? (uint32_t)r * (uint32_t)sizeof(sp_event) : 0);
        int32_t a[6] = {world, max, r, out != nullptr, (int32_t)(uint32_t)h, (int32_t)(uint32_t)(h >> 32)};
        sp_tr_rec(SP_TR_EVENTS, a, sizeof a, nullptr, 0);
    }
    return r;
}

SP_API int32_t sp_events2(int32_t world, sp_event2 *out, int32_t max)
{
    int32_t r = impl_events2(world, out, max);
    if (sp_tr_on) {
        uint64_t h = sp_tr_hash(out, r > 0 ? (uint32_t)r * (uint32_t)sizeof(sp_event2) : 0);
        int32_t a[6] = {world, max, r, out != nullptr, (int32_t)(uint32_t)h, (int32_t)(uint32_t)(h >> 32)};
        sp_tr_rec(SP_TR_EVENTS2, a, sizeof a, nullptr, 0);
    }
    return r;
}

SP_API int32_t sp_world_set_matrix(int32_t world, const uint32_t *rows32)
{
    int32_t r = impl_world_set_matrix(world, rows32);
    if (sp_tr_on) { int32_t a[3] = {world, r, rows32 != nullptr}; sp_tr_rec(SP_TR_WORLD_SET_MATRIX, a, sizeof a, rows32, rows32 ? 128 : 0); }
    return r;
}
SP_API int32_t sp_shape_set_unity(int32_t world, int32_t shape, const sp_shape_unity *u)
{
    int32_t r = impl_shape_set_unity(world, shape, u);
    if (sp_tr_on) { int32_t a[4] = {world, shape, r, u != nullptr}; sp_tr_rec(SP_TR_SHAPE_SET_UNITY, a, sizeof a, u, u ? sizeof *u : 0); }
    return r;
}
SP_API int32_t sp_world_ignore_pair(int32_t world, int32_t shape_a, int32_t shape_b, int32_t ignore)
{
    int32_t r = impl_world_ignore_pair(world, shape_a, shape_b, ignore);
    if (sp_tr_on) { int32_t a[5] = {world, shape_a, shape_b, ignore, r}; sp_tr_rec(SP_TR_WORLD_IGNORE_PAIR, a, sizeof a, nullptr, 0); }
    return r;
}
SP_API int32_t sp_body_set_mass(int32_t world, int32_t body, const float *mass_cx_cy_i)
{
    int32_t r = impl_body_set_mass(world, body, mass_cx_cy_i);
    if (sp_tr_on) { int32_t a[4] = {world, body, r, mass_cx_cy_i != nullptr}; sp_tr_rec(SP_TR_BODY_SET_MASS, a, sizeof a, mass_cx_cy_i, mass_cx_cy_i ? 16 : 0); }
    return r;
}

#define SP_FN(f) { #f, (void *)f }
static const struct { const char *name; void *fn; } k_procs[] = {
    SP_FN(sp_abi), SP_FN(sp_version), SP_FN(sp_build_info),
    SP_FN(sp_world_create), SP_FN(sp_world_destroy), SP_FN(sp_world_step), SP_FN(sp_world_set_gravity), SP_FN(sp_world_set_clamps),
    SP_FN(sp_body_create), SP_FN(sp_body_destroy), SP_FN(sp_body_get_pose), SP_FN(sp_body_set_transform),
    SP_FN(sp_body_set_velocity), SP_FN(sp_body_apply), SP_FN(sp_body_set_type), SP_FN(sp_body_set_flag),
    SP_FN(sp_body_set_float), SP_FN(sp_poses),
    SP_FN(sp_shape_create), SP_FN(sp_shape_destroy), SP_FN(sp_shape_set_sensor), SP_FN(sp_shape_set_filter),
    SP_FN(sp_shape_body), SP_FN(sp_shape_get_polygon),
    SP_FN(sp_events), SP_FN(sp_contacts),
    SP_FN(sp_raycast), SP_FN(sp_query_aabb), SP_FN(sp_query_point), SP_FN(sp_overlap_shape),
    SP_FN(sp_sinf), SP_FN(sp_cosf),
    SP_FN(sp_init), SP_FN(sp_shutdown), SP_FN(sp_pool_base), SP_FN(sp_pool_alloc), SP_FN(sp_pool_free), SP_FN(sp_pool_get_stats),
    SP_FN(sp_nop), SP_FN(sp_settings_check), SP_FN(sp_trace_begin), SP_FN(sp_trace_end), SP_FN(sp_trace_note),
    /* Stage 1 */
    SP_FN(sp_world_set_matrix), SP_FN(sp_shape_set_unity), SP_FN(sp_world_ignore_pair), SP_FN(sp_body_set_mass), SP_FN(sp_body_get_mass),
    SP_FN(sp_uworld_create), SP_FN(sp_uworld_destroy), SP_FN(sp_uworld_ground),
    SP_FN(sp_ubody_create), SP_FN(sp_ubody_destroy), SP_FN(sp_ubody_mirror), SP_FN(sp_ubody_set), SP_FN(sp_ubody_mass),
    SP_FN(sp_ushape_create), SP_FN(sp_u_sync), SP_FN(sp_u_flush), SP_FN(sp_u_step), SP_FN(sp_u_interp), SP_FN(sp_u_early),
    SP_FN(sp_u_snapshot), SP_FN(sp_render_poses), SP_FN(sp_b2_get_stats), SP_FN(sp_atan2f),
    /* Stage 2 */
    SP_FN(sp_events2), SP_FN(sp_u_query), SP_FN(sp_u_bounds), SP_FN(sp_u_closest), SP_FN(sp_u_test_point), SP_FN(sp_u_contacts_of),
    SP_FN(sp_u_distance), SP_FN(sp_u_overlap_shapes), SP_FN(sp_u_brute_pairs),
    /* Stage 3 */
    SP_FN(sp_u_contacts_between), SP_FN(sp_u_apply),
    /* Stage 6 */
    SP_FN(sp_scene3d_create), SP_FN(sp_scene3d_destroy), SP_FN(sp_mesh3d_create), SP_FN(sp_mesh3d_destroy), SP_FN(sp_col3d_create),
    SP_FN(sp_col3d_set), SP_FN(sp_col3d_destroy), SP_FN(sp_ray3d), SP_FN(sp_scene3d_stats),
};

SP_API void *shepphys_proc(const char *name)
{
    for (size_t i = 0; name && i < sizeof(k_procs) / sizeof(k_procs[0]); ++i)
        if (!strcmp(k_procs[i].name, name)) return k_procs[i].fn;
    return nullptr;
}

} /* extern "C" */
