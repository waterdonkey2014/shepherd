/* pb_pool.c -- PhysBench's fork-join pool (ours). See pb_pool.h. GCC/clang __atomic builtins (sequentially consistent:
 * LDREX/STREX + DMB on the Cortex-A9), platform threads and semaphores from pb_plat. */
#include "pb_pool.h"

#include "pb_plat.h"

#define MAXW 2

typedef struct {
    int32_t idx;              /* 1..MAXW */
    void *sem;
    uint32_t fpscr0;
    volatile int32_t started;
} worker_t;

static struct {
    worker_t w[MAXW];
    int32_t nworkers, mode;
    uint32_t fpscr_set;
    uint64_t hybrid_ns;
    pb_task fn;
    void *ctx;
    int32_t nparts;
    uint32_t gen;             /* atomic */
    int32_t done;             /* atomic */
    int32_t quit;             /* atomic */
} P;

static uint32_t load_gen(void) { return __atomic_load_n(&P.gen, __ATOMIC_SEQ_CST); }

static void worker_main(void *arg)
{
    worker_t *me = (worker_t *)arg;
    me->fpscr0 = pb_fpscr_get();
    pb_fpscr_set(P.fpscr_set);
    uint32_t seen = 0;
    __atomic_store_n(&me->started, 1, __ATOMIC_SEQ_CST);
    for (;;) {
        uint32_t g = load_gen();
        if (P.mode == PB_POOL_SPIN) {
            while ((g = load_gen()) == seen) pb_cpu_relax();
        } else if (P.mode == PB_POOL_HYBRID) {
            uint64_t t0 = pb_now_ns();
            uint32_t k = 0;
            while ((g = load_gen()) == seen) {
                pb_cpu_relax();
                if ((++k & 63) == 0 && pb_now_ns() - t0 > P.hybrid_ns) break;
            }
            while (g == seen) { pb_sem_wait(me->sem); g = load_gen(); }   /* stale tokens: re-check and wait again */
        } else {
            while (g == seen) { pb_sem_wait(me->sem); g = load_gen(); }
        }
        seen = g;
        if (__atomic_load_n(&P.quit, __ATOMIC_SEQ_CST)) break;
        if (me->idx < P.nparts) P.fn(P.ctx, me->idx, P.nparts);
        __atomic_add_fetch(&P.done, 1, __ATOMIC_SEQ_CST);
    }
}

int32_t pb_pool_init(int32_t nworkers, int32_t mode, uint32_t fpscr_set, uint32_t hybrid_us)
{
    if (nworkers < 1 || nworkers > MAXW) return -1;
    P.nworkers = nworkers;
    P.mode = mode;
    P.fpscr_set = fpscr_set;
    P.hybrid_ns = (uint64_t)hybrid_us * 1000u;
    __atomic_store_n(&P.gen, 0, __ATOMIC_SEQ_CST);
    __atomic_store_n(&P.done, 0, __ATOMIC_SEQ_CST);
    __atomic_store_n(&P.quit, 0, __ATOMIC_SEQ_CST);
    for (int32_t i = 0; i < nworkers; ++i) {
        worker_t *w = &P.w[i];
        w->idx = i + 1;
        w->sem = pb_sem_create();
        w->fpscr0 = 0;
        w->started = 0;
        if (!w->sem || pb_thread_start(i + 1, worker_main, w, 256u * 1024u) != 0) return -2 - i;
    }
    for (int32_t i = 0; i < nworkers; ++i)
        while (!__atomic_load_n(&P.w[i].started, __ATOMIC_SEQ_CST)) pb_sleep_us(100);
    return 0;
}

void pb_pool_run(pb_task fn, void *ctx, int32_t nparts)
{
    P.fn = fn;
    P.ctx = ctx;
    P.nparts = nparts;
    __atomic_store_n(&P.done, 0, __ATOMIC_SEQ_CST);
    __atomic_add_fetch(&P.gen, 1, __ATOMIC_SEQ_CST);
    if (P.mode != PB_POOL_SPIN)
        for (int32_t i = 0; i < P.nworkers; ++i) pb_sem_post(P.w[i].sem);
    fn(ctx, 0, nparts);
    while (__atomic_load_n(&P.done, __ATOMIC_SEQ_CST) < P.nworkers) pb_cpu_relax();
}

void pb_pool_shutdown(void)
{
    __atomic_store_n(&P.quit, 1, __ATOMIC_SEQ_CST);
    __atomic_add_fetch(&P.gen, 1, __ATOMIC_SEQ_CST);
    for (int32_t i = 0; i < P.nworkers; ++i) pb_sem_post(P.w[i].sem);
    pb_thread_join_all();
    for (int32_t i = 0; i < P.nworkers; ++i) { pb_sem_destroy(P.w[i].sem); P.w[i].sem = 0; }
    P.nworkers = 0;
}

uint32_t pb_pool_worker_fpscr0(int32_t worker)
{
    return (worker >= 1 && worker <= MAXW) ? P.w[worker - 1].fpscr0 : 0;
}

const char *pb_pool_mode_name(int32_t mode)
{
    return mode == PB_POOL_SPIN ? "spin" : mode == PB_POOL_BLOCK ? "block" : "hybrid";
}
