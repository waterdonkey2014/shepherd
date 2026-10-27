// ALTERED for Shepherd: physics-plan-v2 patch P2 (the speed clamps as runtime values, physics E1), behind the runtime values
// b2_unityMaxTranslation / b2_unityMaxRotation (b2Island.h); with both 0 this file behaves exactly as stock Box2D 2.3.1.
// Listed in vita/physics/box2d/PATCHES.md (row P2). The original notice follows unchanged.
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

#ifndef B2_ISLAND_H
#define B2_ISLAND_H

#include <Box2D/Common/b2Math.h>
#include <Box2D/Dynamics/b2Body.h>
#include <Box2D/Dynamics/b2TimeStep.h>

// Shepherd P2 (ALTERED): the per-step speed clamps as runtime values; 0 = stock Box2D 2.3.1's compiled b2_maxTranslation /
// b2_maxRotation (the default). The Shepherd shim sets them around each b2World::Step from the world's own clamps
// (sp_world_set_clamps, shim/shepphys.cpp) and resets them to 0 after: Unity 2022.3 applies Physics2DSettings' maxTranslationSpeed
// (units) and maxRotationSpeed (here in radians) per step (the E1 oracle's MaxTransRot scene).
extern float32 b2_unityMaxTranslation;   // > 0: the largest translation of a body in one step (else b2_maxTranslation)
extern float32 b2_unityMaxRotation;      // > 0: the largest rotation of a body in one step, radians (else b2_maxRotation)

class b2Contact;
class b2Joint;
class b2StackAllocator;
class b2ContactListener;
struct b2ContactVelocityConstraint;
struct b2Profile;

/// This is an internal class.
class b2Island
{
public:
	b2Island(int32 bodyCapacity, int32 contactCapacity, int32 jointCapacity,
			b2StackAllocator* allocator, b2ContactListener* listener);
	~b2Island();

	void Clear()
	{
		m_bodyCount = 0;
		m_contactCount = 0;
		m_jointCount = 0;
	}

	void Solve(b2Profile* profile, const b2TimeStep& step, const b2Vec2& gravity, bool allowSleep);

	void SolveTOI(const b2TimeStep& subStep, int32 toiIndexA, int32 toiIndexB);

	void Add(b2Body* body)
	{
		b2Assert(m_bodyCount < m_bodyCapacity);
		body->m_islandIndex = m_bodyCount;
		m_bodies[m_bodyCount] = body;
		++m_bodyCount;
	}

	void Add(b2Contact* contact)
	{
		b2Assert(m_contactCount < m_contactCapacity);
		m_contacts[m_contactCount++] = contact;
	}

	void Add(b2Joint* joint)
	{
		b2Assert(m_jointCount < m_jointCapacity);
		m_joints[m_jointCount++] = joint;
	}

	void Report(const b2ContactVelocityConstraint* constraints);

	b2StackAllocator* m_allocator;
	b2ContactListener* m_listener;

	b2Body** m_bodies;
	b2Contact** m_contacts;
	b2Joint** m_joints;

	b2Position* m_positions;
	b2Velocity* m_velocities;

	int32 m_bodyCount;
	int32 m_jointCount;
	int32 m_contactCount;

	int32 m_bodyCapacity;
	int32 m_contactCapacity;
	int32 m_jointCapacity;
};

#endif
