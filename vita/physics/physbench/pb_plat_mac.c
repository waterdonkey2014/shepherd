/* pb_plat_mac.c -- PhysBench platform layer for macOS (ours): pthreads at user-interactive QoS (macOS cannot pin a
 * thread to a core, so "USER_1 / USER_2" are just two more threads), libdispatch semaphores, CLOCK_UPTIME_RAW. */
#include "pb_plat.h"

#include <dispatch/dispatch.h>
#include <pthread.h>
#include <time.h>
#include <unistd.h>

#define PB_MAX_THREADS 4

typedef struct { void (*fn)(void *); void *arg; } start_t;
static pthread_t g_threads[PB_MAX_THREADS];
static start_t g_start[PB_MAX_THREADS];
static int32_t g_nthreads;

uint64_t pb_now_ns(void) { return clock_gettime_nsec_np(CLOCK_UPTIME_RAW); }
void pb_sleep_us(uint32_t us) { usleep(us); }

static void *entry(void *p)
{
    start_t *st = (start_t *)p;
    pthread_set_qos_class_self_np(QOS_CLASS_USER_INTERACTIVE, 0);
    st->fn(st->arg);
    return NULL;
}

int32_t pb_thread_start(int32_t core, void (*fn)(void *), void *arg, uint32_t stack_bytes)
{
    (void)core;
    if (g_nthreads >= PB_MAX_THREADS) return -1;
    start_t *st = &g_start[g_nthreads];
    st->fn = fn;
    st->arg = arg;
    pthread_attr_t a;
    pthread_attr_init(&a);
    pthread_attr_setstacksize(&a, stack_bytes < 65536u ? 65536u : stack_bytes);
    int r = pthread_create(&g_threads[g_nthreads], &a, entry, st);
    pthread_attr_destroy(&a);
    if (r != 0) return -2;
    g_nthreads++;
    return 0;
}

void pb_thread_join_all(void)
{
    for (int32_t i = 0; i < g_nthreads; ++i) pthread_join(g_threads[i], NULL);
    g_nthreads = 0;
}

void *pb_sem_create(void) { return (void *)dispatch_semaphore_create(0); }
void pb_sem_post(void *s) { dispatch_semaphore_signal((dispatch_semaphore_t)s); }
void pb_sem_wait(void *s) { dispatch_semaphore_wait((dispatch_semaphore_t)s, DISPATCH_TIME_FOREVER); }
void pb_sem_destroy(void *s) { if (s) dispatch_release((dispatch_semaphore_t)s); }

uint32_t pb_fpscr_get(void)
{
#if defined(__aarch64__)
    uint64_t v;
    __asm__ volatile("mrs %0, fpcr" : "=r"(v));
    return (uint32_t)v;
#else
    return 0;
#endif
}
void pb_fpscr_set(uint32_t v)
{
#if defined(__aarch64__)
    uint64_t w = v;
    __asm__ volatile("msr fpcr, %0" ::"r"(w) : "memory");
#else
    (void)v;
#endif
}
const char *pb_plat_name(void) { return "mac (pthreads, QoS user-interactive, unpinned)"; }
