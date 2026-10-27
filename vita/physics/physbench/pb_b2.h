/* pb_b2.h -- PhysBench's direct-Box2D backend and the Stage 7 phase prototypes (ours).
 *
 * The same synthetic scenes (pb_scene.c) built straight into a b2World (no shim), so the bench can reach what the shim
 * hides: the contact list, for a prototype of the plan's multicore phases (physics-plan.md section 1.8 / v2 section 1.9):
 *   - collide: b2Contact::Evaluate into per-contact manifold slots, split across threads (the serial pass that follows --
 *     touching transitions, listener calls, SetAwake -- is not prototyped here; it stays serial in the plan);
 *   - the final trigger pass: b2TestOverlap per sensor contact into per-contact flags, split across threads.
 * Island-parallel solving is NOT prototyped: it needs b2World::Solve's private state (the Stage 7 restructure of an
 * altered Box2D); this stage alters no Box2D file.
 * b2Profile (collide / solve / broadphase ms) is filled only where Box2D's b2Timer works (the Mac); on the Vita its timer
 * is a stub (b2Timer.cpp's #else branch) and returns 0.
 */
#ifndef PB_B2_H
#define PB_B2_H

#include <stdint.h>
#include "pb_scene.h"

#ifdef __cplusplus
extern "C" {
#endif

typedef struct pb_b2world pb_b2world;

pb_b2world *pb_b2_create(void);       /* gravity (0, -9.81), sleep + warm start on, continuous off (as the shim world) */
void pb_b2_destroy(pb_b2world *w);
const pb_ops *pb_b2_ops(void);        /* u = the pb_b2world */

/* phase prototypes: snapshot the contacts the next step's collide would update (an awake non-static body on either side,
 * fat AABBs overlapping), in contact-list order; returns the count, and the solid / sensor split */
int32_t pb_np_prepare(pb_b2world *w, int32_t *n_solid, int32_t *n_sensor);
/* evaluates partition part of nparts (contiguous ranges by count) into the slots; sensor contacts -> b2TestOverlap flag,
 * solid contacts -> b2Contact::Evaluate manifold. Thread-safe across distinct parts. */
void pb_np_part(pb_b2world *w, int32_t part, int32_t nparts);
void pb_np_clear(pb_b2world *w);      /* zero the slots (so unwritten bytes compare equal) */
uint64_t pb_np_digest(const pb_b2world *w);   /* FNV-1a 64 over every slot */

/* b2World::GetProfile of the last step: step, collide, solve, solveInit, solveVelocity, solvePosition, broadphase, solveTOI */
void pb_b2_profile(const pb_b2world *w, float *out8);
int32_t pb_b2_contact_count(const pb_b2world *w);
/* a Stage 2 / 5 query prototype: the scene's A* grid scanned straight on the broadphase (child-aware, first hit ends the
 * node); returns the node count, *blocked = nodes that overlap a non-sensor shape on s->gmask (must equal the shim scan) */
int32_t pb_b2_astar(pb_b2world *w, const pb_scene *s, int32_t *blocked);

#ifdef __cplusplus
}
#endif
#endif
