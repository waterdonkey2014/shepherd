/* pb_pool.h -- a minimal fork-join pool for PhysBench (ours): the shape of the plan's native workers (physics-plan.md
 * section 1.8: 2 workers on USER_1 / USER_2, never attached to Mono, "spin briefly, then wait on an event flag",
 * FPSCR set explicitly on each worker). The caller runs part 0 itself and spins for the join (phases are short).
 *   PB_POOL_SPIN    workers spin on the generation counter (lowest latency, burns both cores while the pool lives)
 *   PB_POOL_BLOCK   workers sleep on a semaphore between forks (every fork pays a kernel wake-up)
 *   PB_POOL_HYBRID  workers spin for hybrid_us after each job, then sleep (the plan's choice)
 */
#ifndef PB_POOL_H
#define PB_POOL_H

#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

enum { PB_POOL_SPIN = 0, PB_POOL_BLOCK = 1, PB_POOL_HYBRID = 2 };
typedef void (*pb_task)(void *ctx, int32_t part, int32_t nparts);

/* starts nworkers (1..2) on worker cores 1..nworkers; each worker records its FPSCR at entry, then sets fpscr_set */
int32_t pb_pool_init(int32_t nworkers, int32_t mode, uint32_t fpscr_set, uint32_t hybrid_us);
/* runs fn(ctx, p, nparts) for p = 0 (caller) .. nparts-1 (workers 1..); nparts <= 1 + nworkers */
void pb_pool_run(pb_task fn, void *ctx, int32_t nparts);
void pb_pool_shutdown(void);
uint32_t pb_pool_worker_fpscr0(int32_t worker);   /* worker 1..2: the FPSCR its thread started with */
const char *pb_pool_mode_name(int32_t mode);

#ifdef __cplusplus
}
#endif
#endif
