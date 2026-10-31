/* sp_p3.h -- patch P3's job system (physics-plan-v2 section 1.9 "Multicore", Stage 7 item 2 = the Law of the Vita part 2). Ours.
 *
 * Box2D sees only the pointer b2_unityJobs (box2d/Box2D/Dynamics/b2ContactManager.h, row P3 of box2d/PATCHES.md): the shim sets it
 * around one world's b2World::Step while the worker pool runs (shim/sp_jobs.cpp) and resets it to NULL after. The phases it forks
 * (shim/sp_p3.cpp) -- Collide's narrowphase, P1's final trigger pass, the island solve -- compute on several threads and apply their
 * results in Box2D's own serial order on the calling thread, so the step is bit-identical to the serial one (every pose, contact,
 * flag and event); each phase has a fork threshold, below it the stock code runs unchanged. */
#ifndef SP_P3_H
#define SP_P3_H

#include <Box2D/Common/b2Settings.h>
#include <Box2D/Dynamics/b2TimeStep.h>
#include <stdint.h>

class b2StackAllocator;
class b2Contact;
struct b2Manifold;

enum { SP_P3_MAXPARTS = 4 };   /* the calling thread + up to 3 workers */

typedef void (*b2UnityTask)(void *ctx, int32 part, int32 parts);

struct b2UnityStats {
    uint32_t steps;                           /* steps taken with the pool live */
    uint32_t collide_forks, collide_items;    /* Collide: forked steps, contacts evaluated on the fork */
    uint32_t collide_small;                   /* Collide: steps below the threshold (stock loop) */
    uint32_t trigger_forks, trigger_items, trigger_small;
    uint32_t island_forks, islands_forked;    /* Solve: forked steps, islands in them */
    uint32_t islands_big;                     /* islands too big for a worker's stack allocator (solved by the calling thread) */
    uint32_t island_small;                    /* Solve: steps below the threshold (stock Solve) */
    uint32_t island_joints;                   /* Solve: steps not forked because the world has joints */
    uint32_t island_statics;                  /* Solve: steps not forked because a static body in an island is not a fixed point of
                                                 the solve (a -0 coordinate, a non-zero velocity, a non-finite value): stock Solve */
    uint32_t anomalies;                       /* a shared static body's island result differed from its state (non-finite impulses) */
    uint32_t check_runs, check_mismatches;    /* SHEPHERD_CHECK_PHYS=workers: phases compared with their serial run, differences */
    uint32_t handback_sleepers;               /* kit #9 review fix: sleeping bodies an island search reached on a step it then handed back to
                                                 the stock Solve (left asleep: the stock search wakes them in its own order) */
};

struct b2UnityJobs {
    /* the pool: task(ctx, p, parts) for p = 0 (the calling thread) .. parts - 1 (workers 1..), returning after every part ran */
    void (*run)(b2UnityTask task, void *ctx, int32 parts);
    int32 parts;                              /* 1 + the live workers this step (>= 2 to fork) */
    int32 min_collide, min_triggers, min_islands;   /* fork thresholds (contacts to evaluate / sensor contacts / islands); <= 0 never */
    int32 check;                              /* every forked phase also computed serially and compared (SHEPHERD_CHECK_PHYS=workers) */
    b2StackAllocator *stacks[SP_P3_MAXPARTS]; /* [1..] the workers' island allocators (malloc'd on the calling thread at the first island fork) */
    /* scratch for the narrowphase / trigger forks (grown on the calling thread with realloc; never Box2D's arena): cand / man / ov / sensor
     * to cap contacts; man2 / ov2 (the check mode's serial copies) to cap2, only while the check mode is on (kit #9 review fix) */
    b2Contact **cand;
    b2Manifold *man, *man2;
    uint8_t *ov, *ov2, *sensor;
    int32 cap, cap2;
    /* scratch for the island check mode (bodies and contact manifolds saved before the fork) */
    void *chk;
    int32 chk_cap;
    b2UnityStats st;
};

/* sp_jobs.cpp: the job system for one step of a world with `contacts` contacts while the pool runs (NULL: no pool / SP_JOBS_ENABLE 0);
 * wakes the workers when the step may fork */
b2UnityJobs *sp_jobs_for_step(int32_t contacts);
/* sp_jobs.cpp: grow the narrowphase scratch to n contacts, and its check copies while J->check (false: out of memory -> the stock loop) */
bool sp_p3_reserve(b2UnityJobs *J, int32 n);
bool sp_p3_reserve_check(b2UnityJobs *J, int32 bytes);
/* the stack allocator of worker part p (1..), made on first use (NULL: out of memory -> the island solve stays serial) */
b2StackAllocator *sp_p3_stack(b2UnityJobs *J, int32 part);

#endif
