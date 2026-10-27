/* pb_plat_vita.c -- PhysBench platform layer for the PS Vita (ours): SceKernel threads with a CPU affinity mask
 * (USER_1 / USER_2: the plan's worker cores; the caller stays on USER_0), kernel semaphores, the process clock. */
#include "pb_plat.h"

#include <psp2/kernel/processmgr.h>
#include <psp2/kernel/threadmgr.h>
#include <psp2/kernel/cpu.h>

#define PB_MAX_THREADS 4

typedef struct { void (*fn)(void *); void *arg; } start_t;
static SceUID g_threads[PB_MAX_THREADS];
static start_t g_start[PB_MAX_THREADS];
static int32_t g_nthreads;

uint64_t pb_now_ns(void) { return (uint64_t)sceKernelGetProcessTimeWide() * 1000u; }
void pb_sleep_us(uint32_t us) { sceKernelDelayThread(us); }

static int entry(SceSize len, void *argp)
{
    start_t *st = *(start_t **)argp;
    (void)len;
    st->fn(st->arg);
    return 0;
}

int32_t pb_thread_start(int32_t core, void (*fn)(void *), void *arg, uint32_t stack_bytes)
{
    if (g_nthreads >= PB_MAX_THREADS) return -1;
    int mask = core == 1 ? SCE_KERNEL_CPU_MASK_USER_1 : core == 2 ? SCE_KERNEL_CPU_MASK_USER_2 : SCE_KERNEL_CPU_MASK_USER_0;
    start_t *st = &g_start[g_nthreads];
    st->fn = fn;
    st->arg = arg;
    SceUID t = sceKernelCreateThread("pb_worker", entry, 0x10000100, stack_bytes, 0, mask, NULL);
    if (t < 0) return t;
    if (sceKernelStartThread(t, sizeof st, &st) < 0) { sceKernelDeleteThread(t); return -2; }
    g_threads[g_nthreads++] = t;
    return 0;
}

void pb_thread_join_all(void)
{
    for (int32_t i = 0; i < g_nthreads; ++i) {
        int stat = 0;
        sceKernelWaitThreadEnd(g_threads[i], &stat, NULL);
        sceKernelDeleteThread(g_threads[i]);
    }
    g_nthreads = 0;
}

void *pb_sem_create(void)
{
    SceUID s = sceKernelCreateSema("pb_sema", 0, 0, 0x7FFFFFFF, NULL);
    return s < 0 ? NULL : (void *)(intptr_t)s;
}
void pb_sem_post(void *s) { sceKernelSignalSema((SceUID)(intptr_t)s, 1); }
void pb_sem_wait(void *s) { sceKernelWaitSema((SceUID)(intptr_t)s, 1, NULL); }
void pb_sem_destroy(void *s) { if (s) sceKernelDeleteSema((SceUID)(intptr_t)s); }

uint32_t pb_fpscr_get(void) { uint32_t v; __asm__ volatile("vmrs %0, fpscr" : "=r"(v)); return v; }
void pb_fpscr_set(uint32_t v) { __asm__ volatile("vmsr fpscr, %0" ::"r"(v) : "memory"); }
const char *pb_plat_name(void) { return "vita (SceKernel threads: caller USER_0, workers USER_1 / USER_2)"; }
