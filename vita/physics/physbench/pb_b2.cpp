/* pb_b2.cpp -- PhysBench's direct-Box2D backend and phase prototypes (ours). See pb_b2.h.
 * Compiled with libshepphys's flags and its trig substitution (-include shim/sp_trig_override.h), against the vendored,
 * unaltered Box2D 2.3.1 objects inside libshepphys(_vita).a. Shapes are built exactly as shim/shepphys.cpp builds them. */
#include "pb_b2.h"

#include <new>
#include <stdlib.h>
#include <string.h>

#include "Box2D/Box2D.h"

struct pb_b2world {
    b2World *w;
    alignas(b2World) unsigned char w_mem[sizeof(b2World)];
    b2Body *bodies[PB_MAX_BODIES];
    int32_t nb;
    /* phase slots */
    b2Contact **c;
    b2Transform *xa, *xb;
    b2Manifold *man;
    uint8_t *ov, *sensor;
    int32_t n, cap;
};

namespace {

b2Body *B(pb_b2world *W, int32_t h) { return (h >= 1 && h <= W->nb) ? W->bodies[h - 1] : nullptr; }

int32_t b2_body(void *u, const sp_body_def *d)
{
    pb_b2world *W = (pb_b2world *)u;
    if (W->nb >= PB_MAX_BODIES) return SP_ERR_FULL;
    b2BodyDef bd;
    bd.type = (b2BodyType)d->type;
    bd.position.Set(d->x, d->y);
    bd.angle = d->angle;
    bd.linearVelocity.Set(d->vx, d->vy);
    bd.angularVelocity = d->w;
    bd.linearDamping = d->linear_damping;
    bd.angularDamping = d->angular_damping;
    bd.gravityScale = d->gravity_scale;
    bd.awake = (d->flags & SP_BODY_AWAKE) != 0;
    bd.allowSleep = (d->flags & SP_BODY_ALLOW_SLEEP) != 0;
    bd.fixedRotation = (d->flags & SP_BODY_FIXED_ROTATION) != 0;
    bd.bullet = (d->flags & SP_BODY_BULLET) != 0;
    bd.active = (d->flags & SP_BODY_ACTIVE) != 0;
    W->bodies[W->nb++] = W->w->CreateBody(&bd);
    return W->nb;
}

int32_t b2_shape(void *u, int32_t body, const sp_shape_def *d, const float *xy)
{
    pb_b2world *W = (pb_b2world *)u;
    b2Body *b = B(W, body);
    if (!b) return SP_ERR_HANDLE;
    b2CircleShape circle;
    b2PolygonShape poly;
    b2ChainShape chain;
    b2Shape *shape = nullptr;
    switch (d->kind) {
    case SP_SHAPE_CIRCLE:
        circle.m_radius = d->radius;
        circle.m_p.Set(d->cx, d->cy);
        shape = &circle;
        break;
    case SP_SHAPE_BOX:
        if (d->cx == 0.0f && d->cy == 0.0f && d->angle == 0.0f) poly.SetAsBox(d->hx, d->hy);
        else poly.SetAsBox(d->hx, d->hy, b2Vec2(d->cx, d->cy), d->angle);
        shape = &poly;
        break;
    case SP_SHAPE_POLYGON: {
        b2Vec2 v[b2_maxPolygonVertices];
        if (d->vertex_count < 3 || d->vertex_count > b2_maxPolygonVertices) return SP_ERR_PARAM;
        for (int32_t i = 0; i < d->vertex_count; ++i) v[i].Set(xy[2 * i], xy[2 * i + 1]);
        poly.Set(v, d->vertex_count);
        shape = &poly;
        break;
    }
    case SP_SHAPE_CHAIN: {
        if (!(d->flags & SP_SHAPE_LOOP)) return SP_ERR_PARAM;   /* the scenes use loops only */
        b2Vec2 *v = (b2Vec2 *)malloc((size_t)d->vertex_count * sizeof(b2Vec2));
        if (!v) return SP_ERR_FULL;
        for (int32_t i = 0; i < d->vertex_count; ++i) v[i].Set(xy[2 * i], xy[2 * i + 1]);
        chain.CreateLoop(v, d->vertex_count);
        free(v);
        shape = &chain;
        break;
    }
    default:
        return SP_ERR_PARAM;
    }
    b2FixtureDef fd;
    fd.shape = shape;
    fd.density = d->density;
    fd.friction = d->friction;
    fd.restitution = d->restitution;
    fd.isSensor = (d->flags & SP_SHAPE_SENSOR) != 0;
    fd.filter.categoryBits = (uint16)d->category_bits;
    fd.filter.maskBits = (uint16)d->mask_bits;
    fd.filter.groupIndex = (int16)d->group_index;
    return b->CreateFixture(&fd) ? 1 : SP_ERR_FULL;
}

void b2_pose(void *u, int32_t body, float *o)
{
    b2Body *b = B((pb_b2world *)u, body);
    if (!b) { memset(o, 0, 6 * sizeof(float)); return; }
    const b2Vec2 &p = b->GetPosition();
    const b2Vec2 &v = b->GetLinearVelocity();
    o[0] = p.x; o[1] = p.y; o[2] = b->GetAngle(); o[3] = v.x; o[4] = v.y; o[5] = b->GetAngularVelocity();
}

void b2_vel(void *u, int32_t body, float vx, float vy)
{
    if (b2Body *b = B((pb_b2world *)u, body)) { b->SetLinearVelocity(b2Vec2(vx, vy)); b->SetAngularVelocity(0.0f); }
}

void b2_xform(void *u, int32_t body, float x, float y, float a)
{
    if (b2Body *b = B((pb_b2world *)u, body)) { b->SetTransform(b2Vec2(x, y), a); b->SetAwake(true); }
}

void b2_active(void *u, int32_t body, int32_t on)
{
    if (b2Body *b = B((pb_b2world *)u, body)) b->SetActive(on != 0);
}

void b2_step(void *u) { ((pb_b2world *)u)->w->Step(PB_DT, 8, 3); }

const pb_ops k_ops = {b2_body, b2_shape, b2_pose, b2_vel, b2_xform, b2_active, b2_step};

template <typename T> bool grow(T *&p, int32_t cap)
{
    T *q = (T *)realloc(p, (size_t)cap * sizeof(T));
    if (!q) return false;
    p = q;
    return true;
}

} /* namespace */

extern "C" {

pb_b2world *pb_b2_create(void)
{
    void *mem = malloc(sizeof(pb_b2world));
    if (!mem) return nullptr;
    memset(mem, 0, sizeof(pb_b2world));
    pb_b2world *W = (pb_b2world *)mem;
    W->w = new (W->w_mem) b2World(b2Vec2(0.0f, -9.81f));
    W->w->SetAllowSleeping(true);
    W->w->SetContinuousPhysics(false);
    W->w->SetWarmStarting(true);
    W->w->SetSubStepping(false);
    return W;
}

void pb_b2_destroy(pb_b2world *W)
{
    if (!W) return;
    W->w->~b2World();
    free(W->c); free(W->xa); free(W->xb); free(W->man); free(W->ov); free(W->sensor);
    free(W);
}

const pb_ops *pb_b2_ops(void) { return &k_ops; }

int32_t pb_np_prepare(pb_b2world *W, int32_t *n_solid, int32_t *n_sensor)
{
    int32_t total = W->w->GetContactCount();
    if (total > W->cap) {
        int32_t cap = total + 64;
        if (!grow(W->c, cap) || !grow(W->xa, cap) || !grow(W->xb, cap) || !grow(W->man, cap) || !grow(W->ov, cap) ||
            !grow(W->sensor, cap))
            return SP_ERR_FULL;
        W->cap = cap;
    }
    int32_t n = 0, ns = 0, nso = 0;
    for (b2Contact *c = W->w->GetContactList(); c; c = c->GetNext()) {
        b2Fixture *fa = c->GetFixtureA(), *fb = c->GetFixtureB();
        b2Body *ba = fa->GetBody(), *bb = fb->GetBody();
        bool activeA = ba->IsAwake() && ba->GetType() != b2_staticBody;
        bool activeB = bb->IsAwake() && bb->GetType() != b2_staticBody;
        if (!activeA && !activeB) continue;   /* b2ContactManager::Collide skips these */
        if (!b2TestOverlap(fa->GetAABB(c->GetChildIndexA()), fb->GetAABB(c->GetChildIndexB()))) continue;   /* destroyed */
        W->c[n] = c;
        W->xa[n] = ba->GetTransform();
        W->xb[n] = bb->GetTransform();
        W->sensor[n] = (fa->IsSensor() || fb->IsSensor()) ? 1 : 0;
        if (W->sensor[n]) ++ns; else ++nso;
        ++n;
    }
    W->n = n;
    if (n_solid) *n_solid = nso;
    if (n_sensor) *n_sensor = ns;
    return n;
}

void pb_np_part(pb_b2world *W, int32_t part, int32_t nparts)
{
    int32_t i0 = (int32_t)((int64_t)W->n * part / nparts), i1 = (int32_t)((int64_t)W->n * (part + 1) / nparts);
    for (int32_t i = i0; i < i1; ++i) {
        b2Contact *c = W->c[i];
        if (W->sensor[i]) {
            b2Fixture *fa = c->GetFixtureA(), *fb = c->GetFixtureB();
            W->ov[i] = b2TestOverlap(fa->GetShape(), c->GetChildIndexA(), fb->GetShape(), c->GetChildIndexB(), W->xa[i], W->xb[i])
                           ? 1 : 0;
        } else {
            c->Evaluate(&W->man[i], W->xa[i], W->xb[i]);
        }
    }
}

void pb_np_clear(pb_b2world *W)
{
    if (W->n <= 0) return;
    memset((void *)W->man, 0, (size_t)W->n * sizeof(b2Manifold));
    memset(W->ov, 0, (size_t)W->n);
}

uint64_t pb_np_digest(const pb_b2world *W)
{
    uint64_t h = 0xcbf29ce484222325ULL;
    const unsigned char *m = (const unsigned char *)W->man;
    for (size_t i = 0; i < (size_t)W->n * sizeof(b2Manifold); ++i) { h ^= m[i]; h *= 0x100000001b3ULL; }
    for (int32_t i = 0; i < W->n; ++i) { h ^= W->ov[i]; h *= 0x100000001b3ULL; }
    return h;
}

void pb_b2_profile(const pb_b2world *W, float *o)
{
    const b2Profile &p = W->w->GetProfile();
    o[0] = p.step; o[1] = p.collide; o[2] = p.solve; o[3] = p.solveInit;
    o[4] = p.solveVelocity; o[5] = p.solvePosition; o[6] = p.broadphase; o[7] = p.solveTOI;
}

int32_t pb_b2_contact_count(const pb_b2world *W) { return W->w->GetContactCount(); }

} /* extern "C" */

namespace {
/* an A* node test straight on the broadphase: the proxy's child index is known (b2FixtureProxy), so a chain costs one
 * b2TestOverlap per overlapping edge proxy, and the first hit ends the query (A* needs only "blocked or not") */
struct NodeCB {
    const b2BroadPhase *bp;
    const b2Shape *probe;
    b2Transform xf;
    uint32_t mask;
    bool hit;
    bool QueryCallback(int32 id)
    {
        const b2FixtureProxy *p = (const b2FixtureProxy *)bp->GetUserData(id);
        const b2Fixture *f = p->fixture;
        if (f->IsSensor() || (f->GetFilterData().categoryBits & mask) == 0) return true;
        if (b2TestOverlap(probe, 0, f->GetShape(), p->childIndex, xf, f->GetBody()->GetTransform())) { hit = true; return false; }
        return true;
    }
};
} /* namespace */

extern "C" {

int32_t pb_b2_astar(pb_b2world *W, const pb_scene *s, int32_t *blocked)
{
    const b2BroadPhase &bp = W->w->GetContactManager().m_broadPhase;
    b2CircleShape c;
    c.m_radius = s->gr;
    int32_t nb = 0;
    for (int32_t y = 0; y < s->gh; ++y)
        for (int32_t x = 0; x < s->gw; ++x) {
            NodeCB cb;
            cb.bp = &bp;
            cb.probe = &c;
            cb.xf.Set(b2Vec2(s->gx0 + s->gstep * (float)x, s->gy0 + s->gstep * (float)y), 0.0f);
            cb.mask = s->gmask;
            cb.hit = false;
            b2AABB a;
            c.ComputeAABB(&a, cb.xf, 0);
            bp.Query(&cb, a);
            nb += cb.hit ? 1 : 0;
        }
    *blocked = nb;
    return s->gw * s->gh;
}

} /* extern "C" */
