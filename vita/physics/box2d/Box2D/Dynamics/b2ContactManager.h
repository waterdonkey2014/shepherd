// ALTERED for Shepherd: physics-plan-v2 patch P3 (multicore, physics Stage 7 item 2): CollideP3 / UpdateTriggersP3 / UpdateP3 and the
// b2_unityJobs pointer declared (NULL = stock); listed in vita/physics/box2d/PATCHES.md (row P3).
// ALTERED for Shepherd: physics-plan-v2 patch P1 (Unity trigger phase + kinematic triggers, physics Stage 2), behind the
// runtime flags b2_unityTriggerPhase / b2_unityKinematicTriggers (b2Contact.h); with both 0 this file behaves exactly as
// stock Box2D 2.3.1. Listed in vita/physics/box2d/PATCHES.md (row P1). The original notice follows unchanged.
/*
* Copyright (c) 2006-2009 Erin Catto http://www.box2d.org
*
* This software is provided 'as-is', without any express or implied
* warranty.  In no event will the authors be held liable for any damages
* arising from the use of this software.
* Permission is granted to anyone to use this software for any purpose,
* including commercial applications, and to alter it and redistribute it
* freely, subject to the following restrictions:
* 1. The origin of this software must not be misrepresented; you must not
* claim that you wrote the original software. If you use this software
* in a product, an acknowledgment in the product documentation would be
* appreciated but is not required.
* 2. Altered source versions must be plainly marked as such, and must not be
* misrepresented as being the original software.
* 3. This notice may not be removed or altered from any source distribution.
*/

#ifndef B2_CONTACT_MANAGER_H
#define B2_CONTACT_MANAGER_H

#include <Box2D/Collision/b2BroadPhase.h>

class b2Contact;
class b2ContactFilter;
class b2ContactListener;
class b2BlockAllocator;
class b2Fixture;   // Shepherd P1: UnityShouldCollide
struct b2Manifold;   // Shepherd P3: UpdateP3

// Shepherd P3 (multicore, physics Stage 7): the job system of one world's step, owned by the Shepherd shim (vita/physics/shim/sp_p3.h,
// sp_jobs.cpp). The shim sets b2_unityJobs around one world's b2World::Step (as P1's flags) and resets it to NULL after; NULL = stock.
// With it set, Collide's narrowphase, P1's trigger pass and the island solve may run on several threads -- each fork's results are
// applied in Box2D's own serial order, so poses, contacts and events are bit-identical to the serial step (shim/sp_p3.cpp).
struct b2UnityJobs;
extern b2UnityJobs* b2_unityJobs;

// Delegate of b2World.
class b2ContactManager
{
public:
	b2ContactManager();

	// Broad-phase callback.
	void AddPair(void* proxyUserDataA, void* proxyUserDataB);

	void FindNewContacts();

	void Destroy(b2Contact* c);

	void Collide();
	void UpdateTriggers();   // Shepherd P1: the final trigger pass (b2_unityTriggerPhase)
	bool UnityShouldCollide(b2Fixture* fixtureA, b2Fixture* fixtureB);   // Shepherd P1: the pair's body rule (b2_unityKinematicTriggers)
	bool CollideP3();          // Shepherd P3 (shim/sp_p3.cpp): Collide with the narrowphase forked; false = not taken (the stock loop runs)
	bool UpdateTriggersP3();   // Shepherd P3 (shim/sp_p3.cpp): UpdateTriggers with the overlap tests forked; false = not taken
	void UpdateP3(b2Contact* c, const b2Manifold* evaluated, bool overlap);   // Shepherd P3: b2Contact::Update on a precomputed result
            
	b2BroadPhase m_broadPhase;
	b2Contact* m_contactList;
	int32 m_contactCount;
	b2ContactFilter* m_contactFilter;
	b2ContactListener* m_contactListener;
	b2BlockAllocator* m_allocator;
};

#endif
