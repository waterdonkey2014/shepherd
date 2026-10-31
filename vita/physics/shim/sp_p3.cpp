/* sp_p3.cpp -- patch P3's forked phases (physics-plan-v2 section 1.9 "Multicore", Stage 7 item 2 = the Law of the Vita part 2). Ours.
 *
 * Box2D 2.3.1 hands three phases of b2World::Step to these member functions while b2_unityJobs is set (box2d/PATCHES.md row P3; the
 * declarations are in the altered b2ContactManager.h / b2World.h, the hand-over is the first statement of the stock function). Each
 * returns false -- before changing anything -- when it does not take the step (below its fork threshold, or a condition below), and
 * the stock code then runs unchanged. When it takes the step, the result is bit-identical to the stock function's:
 *
 *  1. CollideP3 (b2ContactManager::Collide): a serial pre-pass lists, in contact-list order, the contacts the stock loop is certain to
 *     update -- not flagged for filtering, one body awake and not static, the broad-phase proxies overlapping, and (P1's trigger phase
 *     on) not a sensor. During Collide nothing clears an awake flag, moves a proxy or flags a contact for filtering, so each listed
 *     contact is updated by the stock loop. The fork evaluates them (b2Contact::Evaluate into a copy of the contact's own manifold --
 *     the same bytes Evaluate would overwrite in place, stale fields included -- or b2TestOverlap for a sensor with P1 off) on the
 *     transforms Collide reads (no transform changes during Collide). Then the stock loop runs on the calling thread, with every
 *     listed contact's Update taking its evaluated result (UpdateP3 = b2Contact::Update with Evaluate / b2TestOverlap replaced by the
 *     result) and every other contact's Update unchanged -- so destroys, wake-ups (which may make an unlisted contact active later in
 *     the loop: it evaluates inline, as stock), listener calls and their order are the stock loop's.
 *  2. UpdateTriggersP3 (P1's b2ContactManager::UpdateTriggers): the sensor contacts with one body awake and not static; nothing in this
 *     pass changes an awake flag or the contact list, so the set is fixed: b2TestOverlap forked, then UpdateSensor's flag / listener
 *     half in list order.
 *  3. SolveP3 (b2World::Solve, only in worlds without joints): the islands found by the stock depth-first search, all of them first (the
 *     search reads nothing an island solve writes: a body is in one island, a solve changes only its own bodies and contacts), then
 *     solved on several threads, each with its own b2Island and b2StackAllocator (the calling thread uses the world's and takes the
 *     islands too big for a worker's 100 KiB, so b2Alloc -- not thread-safe -- is only ever called on the calling thread). STATIC
 *     bodies are the shared state: they stop the search but join every island that touches them. b2Island::m_p3Shared makes an
 *     island read them and write none of them (the altered b2Island.cpp), the contact solver finds their slot in each island through
 *     b2Island::P3IndexOf, and the stock writes to them -- the depth-first search's SetAwake(true), the island index, c0 / a0, the
 *     copy-back and transform, the sleep -- are replayed here afterwards in island order. That is exact when a static body is a fixed
 *     point of the island solve (no -0 coordinate, zero velocity bits, finite: the solve then leaves its state bit for bit as it
 *     found it, whatever island ran first); any static body that is not -> the stock Solve (counted: island_statics). A non-finite
 *     impulse (only from non-finite state, a broken simulation either way) would break the fixed point: counted (anomalies).
 *     Each island's b2Profile is added in island order, as stock. b2Island::Report still calls the listener's PostSolve on the
 *     solving thread (the shim's listener has none: Box2D's empty default).
 *
 * The SHEPHERD_CHECK_PHYS=workers mode (b2UnityJobs::check) recomputes every forked phase serially and compares byte for byte (the
 * island phase: bodies and manifolds saved, the parallel solve + replay, its result saved, the state restored, the islands solved
 * serially by the stock b2Island, compared; the serial result is kept). */
#include "sp_p3.h"

#include <Box2D/Box2D.h>
#include <Box2D/Dynamics/b2Island.h>
#include <Box2D/Dynamics/Contacts/b2Contact.h>
#include <Box2D/Dynamics/Contacts/b2ContactSolver.h>
#include <Box2D/Common/b2StackAllocator.h>
#include <string.h>

namespace {

/* byte comparison (the check mode; no memcmp: the bare-metal QEMU harness's libc has none) */
bool differ(const void *a, const void *b, size_t n)
{
    const unsigned char *x = (const unsigned char *)a, *y = (const unsigned char *)b;
    for (size_t i = 0; i < n; ++i) if (x[i] != y[i]) return true;
    return false;
}

/* ---------------------------------------------------------------- the narrowphase / overlap fork */
struct NPCtx {
    b2Contact **c;
    b2Manifold *m;
    uint8_t *ov, *sensor;
    int32 n;
};

void np_range(const NPCtx *x, b2Manifold *m, uint8_t *ov, int32 i0, int32 i1)
{
    for (int32 i = i0; i < i1; ++i) {
        b2Contact *c = x->c[i];
        b2Fixture *fA = c->GetFixtureA(), *fB = c->GetFixtureB();
        const b2Transform &xfA = fA->GetBody()->GetTransform();
        const b2Transform &xfB = fB->GetBody()->GetTransform();
        if (x->sensor[i])
            ov[i] = b2TestOverlap(fA->GetShape(), c->GetChildIndexA(), fB->GetShape(), c->GetChildIndexB(), xfA, xfB) ? 1 : 0;
        else
            c->Evaluate(&m[i], xfA, xfB);
    }
}

void np_task(void *p, int32 part, int32 parts)
{
    const NPCtx *x = (const NPCtx *)p;
    int32 i0 = (int32)((int64_t)x->n * part / parts), i1 = (int32)((int64_t)x->n * (part + 1) / parts);
    np_range(x, x->m, x->ov, i0, i1);
}

int32 parts_for(const b2UnityJobs *J, int32 items) { return items < J->parts ? (items < 1 ? 1 : items) : J->parts; }

/* SHEPHERD_CHECK_PHYS=workers: the same items serially into the second buffers (begun as the same copies), compared */
void np_check(b2UnityJobs *J, const NPCtx *x, bool manifolds)
{
    if (manifolds)
        for (int32 i = 0; i < x->n; ++i) if (!x->sensor[i]) J->man2[i] = x->c[i]->GetManifold()[0];   /* the pre-fork bytes again */
    np_range(x, J->man2, J->ov2, 0, x->n);
    int32 bad = 0;
    for (int32 i = 0; i < x->n; ++i) {
        if (x->sensor[i] ? (J->ov2[i] != x->ov[i]) : differ(&J->man2[i], &x->m[i], sizeof(b2Manifold))) {
            ++bad;
            if (x->sensor[i]) x->ov[i] = J->ov2[i]; else x->m[i] = J->man2[i];   /* the serial result is kept */
        }
    }
    J->st.check_runs++;
    J->st.check_mismatches += (uint32_t)bad;
}

/* ---------------------------------------------------------------- the island fork */
struct P3Isl {
    int32 body0, bodyCount, contact0, contactCount, static0, staticCount;
    int32 big, slept;
    b2Profile profile;
};
struct P3Out { b2Position p; b2Velocity v; };

struct ISCtx {
    const b2TimeStep *step;
    b2Vec2 gravity;
    bool allowSleep;
    b2ContactListener *listener;
    b2Body **bodies;
    b2Contact **contacts;
    int32 *slots;         /* per island (from static0): the static bodies' slots in the island */
    P3Out *outs;          /* per island (from static0): their results */
    P3Isl *isl;
    int32 *small, nsmall, *bigs, nbig;
    b2StackAllocator *alloc[SP_P3_MAXPARTS];
    int32 next;           /* atomic: the next small island */
};

void solve_one(ISCtx *x, int32 k, b2StackAllocator *alloc)
{
    P3Isl &is = x->isl[k];
    b2Island island(is.bodyCount, is.contactCount, 0, alloc, x->listener);
    island.m_p3Shared = 1;
    island.m_p3Statics = x->slots + is.static0;
    island.m_p3StaticCount = is.staticCount;
    for (int32 i = 0; i < is.bodyCount; ++i) {
        b2Body *b = x->bodies[is.body0 + i];
        if (b->GetType() != b2_staticBody) island.Add(b);                /* (the index = its slot, as stock) */
        else island.m_bodies[island.m_bodyCount++] = b;                  /* shared: no m_islandIndex write (P3IndexOf finds it) */
    }
    for (int32 j = 0; j < is.contactCount; ++j) island.Add(x->contacts[is.contact0 + j]);
    b2Profile profile;
    island.Solve(&profile, *x->step, x->gravity, x->allowSleep);
    is.profile = profile;
    is.slept = island.m_p3Slept;
    for (int32 s = 0; s < is.staticCount; ++s) {
        int32 slot = x->slots[is.static0 + s];
        x->outs[is.static0 + s].p = island.m_positions[slot];
        x->outs[is.static0 + s].v = island.m_velocities[slot];
    }
}

void island_task(void *p, int32 part, int32 parts)
{
    (void)parts;
    ISCtx *x = (ISCtx *)p;
    b2StackAllocator *alloc = x->alloc[part];
    if (part == 0)
        for (int32 i = 0; i < x->nbig; ++i) solve_one(x, x->bigs[i], alloc);
    for (;;) {
        int32 q = __atomic_fetch_add(&x->next, 1, __ATOMIC_RELAXED);
        if (q >= x->nsmall) break;
        solve_one(x, x->small[q], alloc);
    }
}

inline uint32_t fbits(float32 f) { uint32_t u; memcpy(&u, &f, 4); return u; }
/* a value the island solve leaves as it found it: finite and not -0 (c + h * (+0) and c - 0 * P then give c back, bit for bit) */
inline bool fixed_coord(float32 f) { uint32_t u = fbits(f); return u != 0x80000000u && (u & 0x7F800000u) != 0x7F800000u; }

/* stack bytes an island takes from its allocator: b2Island's five arrays + the contact solver's two (b2ContactPositionConstraint is
 * private to b2ContactSolver.cpp: 88 bytes on both builds, bounded here by 128) */
int32 island_bytes(int32 bc, int32 cc)
{
    return bc * (int32)(sizeof(b2Body *) + sizeof(b2Velocity) + sizeof(b2Position)) + cc * (int32)sizeof(b2Contact *)
         + cc * (int32)(sizeof(b2ContactVelocityConstraint) + 128);
}

} /* namespace */

/* ================================================================ 1. Collide */
bool b2ContactManager::CollideP3()
{
    b2UnityJobs *J = b2_unityJobs;
    if (J->parts < 2 || J->min_collide <= 0 || m_contactCount < J->min_collide) { J->st.collide_small++; return false; }
    if (!sp_p3_reserve(J, m_contactCount)) return false;
    int32 n = 0;
    for (b2Contact *c = m_contactList; c; c = c->m_next) {
        if (c->m_flags & b2Contact::e_filterFlag) continue;   /* the stock loop decides (rare: after a filter change) */
        b2Fixture *fixtureA = c->m_fixtureA, *fixtureB = c->m_fixtureB;
        bool sensor = fixtureA->m_isSensor || fixtureB->m_isSensor;
        if (sensor && b2_unityTriggerPhase) continue;          /* P1: Update returns before evaluating a sensor */
        b2Body *bodyA = fixtureA->m_body, *bodyB = fixtureB->m_body;
        bool activeA = bodyA->IsAwake() && bodyA->m_type != b2_staticBody;
        bool activeB = bodyB->IsAwake() && bodyB->m_type != b2_staticBody;
        if (activeA == false && activeB == false) continue;
        if (!m_broadPhase.TestOverlap(fixtureA->m_proxies[c->m_indexA].proxyId, fixtureB->m_proxies[c->m_indexB].proxyId)) continue;
        J->cand[n] = c;
        J->sensor[n] = sensor ? 1 : 0;
        J->ov[n] = 0;
        if (!sensor) J->man[n] = c->m_manifold;   /* Evaluate writes into this copy what it would write into m_manifold */
        ++n;
    }
    if (n < J->min_collide) { J->st.collide_small++; return false; }
    NPCtx x = {J->cand, J->man, J->ov, J->sensor, n};
    J->run(np_task, &x, parts_for(J, n));
    J->st.collide_forks++;
    J->st.collide_items += (uint32_t)n;
    if (J->check) np_check(J, &x, true);

    /* the stock loop (b2ContactManager::Collide with P1's body rule), each listed contact's Update on its evaluated result */
    int32 k = 0;
    b2Contact* c = m_contactList;
    while (c)
    {
        b2Fixture* fixtureA = c->GetFixtureA();
        b2Fixture* fixtureB = c->GetFixtureB();
        int32 indexA = c->GetChildIndexA();
        int32 indexB = c->GetChildIndexB();
        b2Body* bodyA = fixtureA->GetBody();
        b2Body* bodyB = fixtureB->GetBody();

        // Is this contact flagged for filtering?
        if (c->m_flags & b2Contact::e_filterFlag)
        {
            // Should these bodies collide? (Shepherd P1: the Unity body rule when b2_unityKinematicTriggers is set)
            if (UnityShouldCollide(fixtureA, fixtureB) == false)
            {
                b2Contact* cNuke = c;
                c = cNuke->GetNext();
                Destroy(cNuke);
                continue;
            }

            // Check user filtering.
            if (m_contactFilter && m_contactFilter->ShouldCollide(fixtureA, fixtureB) == false)
            {
                b2Contact* cNuke = c;
                c = cNuke->GetNext();
                Destroy(cNuke);
                continue;
            }

            // Clear the filtering flag.
            c->m_flags &= ~b2Contact::e_filterFlag;
        }

        bool activeA = bodyA->IsAwake() && bodyA->m_type != b2_staticBody;
        bool activeB = bodyB->IsAwake() && bodyB->m_type != b2_staticBody;

        // At least one body must be awake and it must be dynamic or kinematic.
        if (activeA == false && activeB == false)
        {
            c = c->GetNext();
            continue;
        }

        int32 proxyIdA = fixtureA->m_proxies[indexA].proxyId;
        int32 proxyIdB = fixtureB->m_proxies[indexB].proxyId;
        bool overlap = m_broadPhase.TestOverlap(proxyIdA, proxyIdB);

        // Here we destroy contacts that cease to overlap in the broad-phase.
        if (overlap == false)
        {
            b2Contact* cNuke = c;
            c = cNuke->GetNext();
            Destroy(cNuke);
            continue;
        }

        // The contact persists.
        if (k < n && J->cand[k] == c)
        {
            UpdateP3(c, &J->man[k], J->ov[k] != 0);   // Shepherd P3: the evaluated result
            ++k;
        }
        else
        {
            c->Update(m_contactListener);
        }
        c = c->GetNext();
    }
    if (k != n) J->st.anomalies++;   /* (never: every listed contact is reached and updated) */
    return true;
}

/* b2Contact::Update (stock, with P1's sensor return) on a result computed by the fork: `evaluated` is what Evaluate wrote into a copy
 * of m_manifold taken after the contact's previous update (so the same bytes it would have written in place), `overlap` what
 * b2TestOverlap returned for a sensor. Everything else is the stock function, statement for statement. */
void b2ContactManager::UpdateP3(b2Contact* c, const b2Manifold* evaluated, bool overlap)
{
    b2ContactListener* listener = m_contactListener;
    b2Manifold oldManifold = c->m_manifold;

    // Re-enable this contact.
    c->m_flags |= b2Contact::e_enabledFlag;

    bool touching = false;
    bool wasTouching = (c->m_flags & b2Contact::e_touchingFlag) == b2Contact::e_touchingFlag;

    bool sensorA = c->m_fixtureA->IsSensor();
    bool sensorB = c->m_fixtureB->IsSensor();
    bool sensor = sensorA || sensorB;
    if (sensor && b2_unityTriggerPhase)
    {
        return;   // Shepherd P1 (never listed in that mode)
    }

    b2Body* bodyA = c->m_fixtureA->GetBody();
    b2Body* bodyB = c->m_fixtureB->GetBody();

    // Is this contact a sensor?
    if (sensor)
    {
        touching = overlap;   // Shepherd P3: = b2TestOverlap(shapeA, m_indexA, shapeB, m_indexB, xfA, xfB)

        // Sensors don't generate manifolds.
        c->m_manifold.pointCount = 0;
    }
    else
    {
        c->m_manifold = *evaluated;   // Shepherd P3: = Evaluate(&m_manifold, xfA, xfB)
        touching = c->m_manifold.pointCount > 0;

        // Match old contact ids to new contact ids and copy the
        // stored impulses to warm start the solver.
        for (int32 i = 0; i < c->m_manifold.pointCount; ++i)
        {
            b2ManifoldPoint* mp2 = c->m_manifold.points + i;
            mp2->normalImpulse = 0.0f;
            mp2->tangentImpulse = 0.0f;
            b2ContactID id2 = mp2->id;

            for (int32 j = 0; j < oldManifold.pointCount; ++j)
            {
                b2ManifoldPoint* mp1 = oldManifold.points + j;

                if (mp1->id.key == id2.key)
                {
                    mp2->normalImpulse = mp1->normalImpulse;
                    mp2->tangentImpulse = mp1->tangentImpulse;
                    break;
                }
            }
        }

        if (touching != wasTouching)
        {
            bodyA->SetAwake(true);
            bodyB->SetAwake(true);
        }
    }

    if (touching)
    {
        c->m_flags |= b2Contact::e_touchingFlag;
    }
    else
    {
        c->m_flags &= ~b2Contact::e_touchingFlag;
    }

    if (wasTouching == false && touching == true && listener)
    {
        listener->BeginContact(c);
    }

    if (wasTouching == true && touching == false && listener)
    {
        listener->EndContact(c);
    }

    if (sensor == false && touching && listener)
    {
        listener->PreSolve(c, &oldManifold);
    }
}

/* ================================================================ 2. P1's final trigger pass */
bool b2ContactManager::UpdateTriggersP3()
{
    b2UnityJobs *J = b2_unityJobs;
    if (J->parts < 2 || J->min_triggers <= 0 || m_contactCount < J->min_triggers) { J->st.trigger_small++; return false; }
    if (!sp_p3_reserve(J, m_contactCount)) return false;
    int32 n = 0;
    for (b2Contact *c = m_contactList; c; c = c->GetNext()) {
        b2Fixture *fixtureA = c->GetFixtureA(), *fixtureB = c->GetFixtureB();
        if (fixtureA->IsSensor() == false && fixtureB->IsSensor() == false) continue;
        b2Body *bodyA = fixtureA->GetBody(), *bodyB = fixtureB->GetBody();
        bool activeA = bodyA->IsAwake() && bodyA->GetType() != b2_staticBody;
        bool activeB = bodyB->IsAwake() && bodyB->GetType() != b2_staticBody;
        if (activeA == false && activeB == false) continue;
        J->cand[n] = c;
        J->sensor[n] = 1;
        J->ov[n] = 0;
        ++n;
    }
    if (n < J->min_triggers) { J->st.trigger_small++; return false; }
    NPCtx x = {J->cand, J->man, J->ov, J->sensor, n};
    J->run(np_task, &x, parts_for(J, n));
    J->st.trigger_forks++;
    J->st.trigger_items += (uint32_t)n;
    if (J->check) np_check(J, &x, false);
    /* b2Contact::UpdateSensor (P1) on each overlap result, in list order (no awake flag or list change in this pass) */
    b2ContactListener *listener = m_contactListener;
    for (int32 k = 0; k < n; ++k) {
        b2Contact *c = J->cand[k];
        c->m_flags |= b2Contact::e_enabledFlag;
        bool wasTouching = (c->m_flags & b2Contact::e_touchingFlag) == b2Contact::e_touchingFlag;
        bool touching = J->ov[k] != 0;   // Shepherd P3: = b2TestOverlap(...) on the post-solve transforms
        c->m_manifold.pointCount = 0;
        if (touching)
        {
            c->m_flags |= b2Contact::e_touchingFlag;
        }
        else
        {
            c->m_flags &= ~b2Contact::e_touchingFlag;
        }
        if (wasTouching == false && touching == true && listener)
        {
            listener->BeginContact(c);
        }
        if (wasTouching == true && touching == false && listener)
        {
            listener->EndContact(c);
        }
    }
    return true;
}

/* ================================================================ 3. the island solve */
bool b2World::SolveP3(const b2TimeStep& step)
{
    b2UnityJobs *J = b2_unityJobs;
    if (J->parts < 2 || J->min_islands <= 0) { J->st.island_small++; return false; }
    if (m_jointCount > 0) { J->st.island_joints++; return false; }   /* joints read m_islandIndex of static bodies: stock */
    /* an upper bound on the islands: the awake, active, non-static bodies (each island has one as its seed) */
    int32 seeds = 0;
    for (b2Body* b = m_bodyList; b; b = b->m_next)
        if (b->m_type != b2_staticBody && b->IsAwake() && b->IsActive()) ++seeds;
    if (seeds < J->min_islands) { J->st.island_small++; return false; }

    // Clear all the island flags. (stock)
    for (b2Body* b = m_bodyList; b; b = b->m_next)
    {
        b->m_flags &= ~b2Body::e_islandFlag;
    }
    for (b2Contact* c = m_contactManager.m_contactList; c; c = c->m_next)
    {
        c->m_flags &= ~b2Contact::e_islandFlag;
    }

    /* the islands, in the stock search's order (the world's stack allocator, freed in reverse at the end) */
    const int32 bodyCap = m_bodyCount + m_contactManager.m_contactCount;   /* a static body joins one island per touching contact at most */
    const int32 contactCap = m_contactManager.m_contactCount;
    b2Body** ibodies = (b2Body**)m_stackAllocator.Allocate(bodyCap * sizeof(b2Body*));
    b2Contact** icontacts = (b2Contact**)m_stackAllocator.Allocate(contactCap * sizeof(b2Contact*));
    P3Isl* isl = (P3Isl*)m_stackAllocator.Allocate(seeds * sizeof(P3Isl));
    int32* slots = (int32*)m_stackAllocator.Allocate(bodyCap * sizeof(int32));
    int32 stackSize = m_bodyCount;
    b2Body** stack = (b2Body**)m_stackAllocator.Allocate(stackSize * sizeof(b2Body*));
    int32 ni = 0, nb = 0, nc = 0, ns = 0;
    bool fixedPoints = true;
    for (b2Body* seed = m_bodyList; seed; seed = seed->m_next)
    {
        if (seed->m_flags & b2Body::e_islandFlag)
        {
            continue;
        }

        if (seed->IsAwake() == false || seed->IsActive() == false)
        {
            continue;
        }

        // The seed can be dynamic or kinematic.
        if (seed->GetType() == b2_staticBody)
        {
            continue;
        }

        P3Isl& is = isl[ni++];
        is.body0 = nb; is.contact0 = nc; is.static0 = ns;

        // Reset island and stack.
        int32 stackCount = 0;
        stack[stackCount++] = seed;
        seed->m_flags |= b2Body::e_islandFlag;

        // Perform a depth first search (DFS) on the constraint graph.
        while (stackCount > 0)
        {
            // Grab the next body off the stack and add it to the island.
            b2Body* b = stack[--stackCount];
            b2Assert(b->IsActive() == true);
            ibodies[nb++] = b;   // (= island.Add: the index is set when the island is solved)

            // Make sure the body is awake. -- Shepherd P3 (kit #9 review fix): NOT here. The stock search's SetAwake(true) is made after the
            // hand-back decision below (taken: the dynamic and kinematic bodies before the solve, the static ones in the replay), so a step
            // handed back to the stock Solve leaves every awake flag as it found it: a sleeping body woken here would make the stock seed
            // loop start that island from it instead of the body the stock search starts from (another body and contact order). Nothing
            // in this search reads an awake flag of a body it reaches (the seed loop's IsAwake skips them on the island flag first).

            // To keep islands as small as possible, we don't
            // propagate islands across static bodies.
            if (b->GetType() == b2_staticBody)
            {
                slots[ns++] = nb - 1 - is.body0;
                fixedPoints = fixedPoints && fixed_coord(b->m_sweep.c.x) && fixed_coord(b->m_sweep.c.y) && fixed_coord(b->m_sweep.a)
                              && fbits(b->m_linearVelocity.x) == 0 && fbits(b->m_linearVelocity.y) == 0 && fbits(b->m_angularVelocity) == 0;
                continue;
            }

            // Search all contacts connected to this body.
            for (b2ContactEdge* ce = b->m_contactList; ce; ce = ce->next)
            {
                b2Contact* contact = ce->contact;

                // Has this contact already been added to an island?
                if (contact->m_flags & b2Contact::e_islandFlag)
                {
                    continue;
                }

                // Is this contact solid and touching?
                if (contact->IsEnabled() == false ||
                    contact->IsTouching() == false)
                {
                    continue;
                }

                // Skip sensors.
                bool sensorA = contact->m_fixtureA->m_isSensor;
                bool sensorB = contact->m_fixtureB->m_isSensor;
                if (sensorA || sensorB)
                {
                    continue;
                }

                icontacts[nc++] = contact;
                contact->m_flags |= b2Contact::e_islandFlag;

                b2Body* other = ce->other;

                // Was the other body already added to this island?
                if (other->m_flags & b2Body::e_islandFlag)
                {
                    continue;
                }

                b2Assert(stackCount < stackSize);
                stack[stackCount++] = other;
                other->m_flags |= b2Body::e_islandFlag;
            }
            // (no joints: m_jointCount == 0)
        }
        is.bodyCount = nb - is.body0;
        is.contactCount = nc - is.contact0;
        is.staticCount = ns - is.static0;
        is.slept = 0;

        // Post solve cleanup (stock, after the island's solve): allow static bodies to participate in other islands.
        for (int32 i = 0; i < is.staticCount; ++i)
        {
            ibodies[is.body0 + slots[is.static0 + i]]->m_flags &= ~b2Body::e_islandFlag;
        }
    }
    m_stackAllocator.Free(stack);

    /* not taken: fewer islands than the threshold, or a static body that is not a fixed point of the solve. The search above changed only
     * the island flags, which the stock Solve clears first (kit #9 review fix: it no longer wakes anything -- the SetAwake(true) calls come
     * after this decision) */
    int32 nbig = 0;
    b2StackAllocator* alloc[SP_P3_MAXPARTS] = {&m_stackAllocator, 0, 0, 0};
    int32 parts = J->parts;
    for (int32 p = 1; p < parts; ++p)
        if (!(alloc[p] = sp_p3_stack(J, p))) { parts = p; break; }
    for (int32 k = 0; k < ni; ++k) {
        isl[k].big = island_bytes(isl[k].bodyCount, isl[k].contactCount) > b2_stackSize ? 1 : 0;
        nbig += isl[k].big;
    }
    if (ni < J->min_islands || !fixedPoints || parts < 2) {
        if (fixedPoints) J->st.island_small++; else J->st.island_statics++;
        for (int32 i = 0; i < nb; ++i)   /* (counted: the sleeping bodies this hand-back leaves asleep for the stock search to wake) */
            if (ibodies[i]->GetType() != b2_staticBody && !ibodies[i]->IsAwake()) J->st.handback_sleepers++;
        m_stackAllocator.Free(slots);
        m_stackAllocator.Free(isl);
        m_stackAllocator.Free(icontacts);
        m_stackAllocator.Free(ibodies);
        return false;
    }

    /* taken: the search's SetAwake(true) on the islands' dynamic and kinematic bodies, before anything reads them (each is in one island,
     * whose solve is the first thing to read its awake state and sleep time -- as the stock search's call just before that island's
     * solve); the static bodies' calls are replayed in island order below, as before */
    for (int32 i = 0; i < nb; ++i)
        if (ibodies[i]->GetType() != b2_staticBody) ibodies[i]->SetAwake(true);

    m_profile.solveInit = 0.0f;
    m_profile.solveVelocity = 0.0f;
    m_profile.solvePosition = 0.0f;

    P3Out* outs = (P3Out*)m_stackAllocator.Allocate((ns > 0 ? ns : 1) * sizeof(P3Out));
    int32* order = (int32*)m_stackAllocator.Allocate(ni * sizeof(int32));   /* the big islands first, then the small ones */
    ISCtx x;
    x.step = &step; x.gravity = m_gravity; x.allowSleep = m_allowSleep; x.listener = m_contactManager.m_contactListener;
    x.bodies = ibodies; x.contacts = icontacts; x.slots = slots; x.outs = outs; x.isl = isl;
    x.bigs = order; x.nbig = 0; x.small = order + nbig; x.nsmall = 0;
    for (int32 k = 0; k < ni; ++k) { if (isl[k].big) x.bigs[x.nbig++] = k; else x.small[x.nsmall++] = k; }
    for (int32 p = 0; p < SP_P3_MAXPARTS; ++p) x.alloc[p] = alloc[p];
    x.next = 0;

    /* SHEPHERD_CHECK_PHYS=workers: the islands' bodies and touching contacts saved, to solve them again serially below */
    unsigned char* save = 0;
    if (J->check) {
        int32 bytes = nb * (int32)sizeof(b2Body) + nc * (int32)(sizeof(b2Manifold) + sizeof(uint32)) + nb * (int32)sizeof(b2Body) + nc * (int32)sizeof(b2Manifold);
        if (sp_p3_reserve_check(J, bytes)) {
            save = (unsigned char*)J->chk;
            for (int32 i = 0; i < nb; ++i) memcpy(save + i * sizeof(b2Body), (const void*)ibodies[i], sizeof(b2Body));
            unsigned char* cm = save + nb * sizeof(b2Body);
            for (int32 j = 0; j < nc; ++j) { memcpy(cm + j * sizeof(b2Manifold), &icontacts[j]->m_manifold, sizeof(b2Manifold)); }
            uint32* cf = (uint32*)(cm + nc * sizeof(b2Manifold));
            for (int32 j = 0; j < nc; ++j) cf[j] = icontacts[j]->m_flags;
        }
    }

    J->run(island_task, &x, parts);
    J->st.island_forks++;
    J->st.islands_forked += (uint32_t)ni;
    J->st.islands_big += (uint32_t)nbig;

    /* the stock writes to the shared static bodies, in island order (each island: its search's SetAwake(true) and island index, then
     * c0 / a0, the copy-back and transform, then the sleep), and the islands' profiles in island order */
    for (int32 k = 0; k < ni; ++k)
    {
        const P3Isl& is = isl[k];
        m_profile.solveInit += is.profile.solveInit;
        m_profile.solveVelocity += is.profile.solveVelocity;
        m_profile.solvePosition += is.profile.solvePosition;
        for (int32 s = 0; s < is.staticCount; ++s)
        {
            int32 slot = slots[is.static0 + s];
            b2Body* b = ibodies[is.body0 + slot];
            const P3Out& o = outs[is.static0 + s];
            b->SetAwake(true);
            b->m_islandIndex = slot;
            if (fbits(o.p.c.x) != fbits(b->m_sweep.c.x) || fbits(o.p.c.y) != fbits(b->m_sweep.c.y) || fbits(o.p.a) != fbits(b->m_sweep.a)
                || fbits(o.v.v.x) != fbits(b->m_linearVelocity.x) || fbits(o.v.v.y) != fbits(b->m_linearVelocity.y) || fbits(o.v.w) != fbits(b->m_angularVelocity))
                J->st.anomalies++;
            b->m_sweep.c0 = b->m_sweep.c;
            b->m_sweep.a0 = b->m_sweep.a;
            b->m_sweep.c = o.p.c;
            b->m_sweep.a = o.p.a;
            b->m_linearVelocity = o.v.v;
            b->m_angularVelocity = o.v.w;
            b->SynchronizeTransform();
        }
        if (is.slept)
        {
            for (int32 s = 0; s < is.staticCount; ++s)
            {
                ibodies[is.body0 + slots[is.static0 + s]]->SetAwake(false);
            }
        }
    }

    if (save) {
        /* the parallel result saved, the saved state back, the islands solved serially by the stock b2Island (the search's SetAwake(true)
         * on each island's static bodies first, as the interleaved stock search does), the two results compared; the serial one is kept */
        unsigned char* cm = save + nb * sizeof(b2Body);
        uint32* cf = (uint32*)(cm + nc * sizeof(b2Manifold));
        unsigned char* pb = (unsigned char*)(cf + nc);
        unsigned char* pm = pb + nb * sizeof(b2Body);
        for (int32 i = 0; i < nb; ++i) memcpy(pb + i * sizeof(b2Body), (const void*)ibodies[i], sizeof(b2Body));
        for (int32 j = 0; j < nc; ++j) memcpy(pm + j * sizeof(b2Manifold), &icontacts[j]->m_manifold, sizeof(b2Manifold));
        for (int32 i = nb - 1; i >= 0; --i) memcpy((void*)ibodies[i], save + i * sizeof(b2Body), sizeof(b2Body));   /* (statics repeat: first copy last) */
        for (int32 j = 0; j < nc; ++j) { memcpy(&icontacts[j]->m_manifold, cm + j * sizeof(b2Manifold), sizeof(b2Manifold)); icontacts[j]->m_flags = cf[j]; }
        b2Profile sum; sum.solveInit = sum.solveVelocity = sum.solvePosition = 0.0f;
        for (int32 k = 0; k < ni; ++k)
        {
            const P3Isl& is = isl[k];
            for (int32 s = 0; s < is.staticCount; ++s) ibodies[is.body0 + slots[is.static0 + s]]->SetAwake(true);
            b2Island island(is.bodyCount, is.contactCount, 0, &m_stackAllocator, m_contactManager.m_contactListener);
            for (int32 i = 0; i < is.bodyCount; ++i) island.Add(ibodies[is.body0 + i]);
            for (int32 j = 0; j < is.contactCount; ++j) island.Add(icontacts[is.contact0 + j]);
            b2Profile profile;
            island.Solve(&profile, step, m_gravity, m_allowSleep);
            sum.solveInit += profile.solveInit; sum.solveVelocity += profile.solveVelocity; sum.solvePosition += profile.solvePosition;
        }
        int32 bad = 0;
        for (int32 i = 0; i < nb; ++i) bad += differ(pb + i * sizeof(b2Body), (const void*)ibodies[i], sizeof(b2Body));
        for (int32 j = 0; j < nc; ++j) bad += differ(pm + j * sizeof(b2Manifold), &icontacts[j]->m_manifold, sizeof(b2Manifold));
        J->st.check_runs++;
        J->st.check_mismatches += (uint32_t)bad;
        m_profile.solveInit = sum.solveInit; m_profile.solveVelocity = sum.solveVelocity; m_profile.solvePosition = sum.solvePosition;
    }

    m_stackAllocator.Free(order);
    m_stackAllocator.Free(outs);
    m_stackAllocator.Free(slots);
    m_stackAllocator.Free(isl);
    m_stackAllocator.Free(icontacts);
    m_stackAllocator.Free(ibodies);

    // (stock from here)
    {
        b2Timer timer;
        // Synchronize fixtures, check for out of range bodies.
        for (b2Body* b = m_bodyList; b; b = b->GetNext())
        {
            // If a body was not in an island then it did not move.
            if ((b->m_flags & b2Body::e_islandFlag) == 0)
            {
                continue;
            }

            if (b->GetType() == b2_staticBody)
            {
                continue;
            }

            // Update fixtures (for broad-phase).
            b->SynchronizeFixtures();
        }

        // Look for new contacts.
        m_contactManager.FindNewContacts();
        m_profile.broadphase = timer.GetMilliseconds();
    }
    return true;
}
