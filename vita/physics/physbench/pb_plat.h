/* pb_plat.h -- the few platform services PhysBench's portable code needs (ours): a clock, threads pinned to a core,
 * counting semaphores, the FP control register, a spin hint. pb_plat_vita.c (SceKernel) / pb_plat_mac.c (pthreads). */
#ifndef PB_PLAT_H
#define PB_PLAT_H

#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

uint64_t pb_now_ns(void);                 /* monotonic; the Vita's process clock has 1 us resolution */
void pb_sleep_us(uint32_t us);
/* starts a worker thread on worker core `core` (1 = USER_1, 2 = USER_2 on the Vita; unpinned elsewhere); 0 on success */
int32_t pb_thread_start(int32_t core, void (*fn)(void *), void *arg, uint32_t stack_bytes);
void pb_thread_join_all(void);            /* waits for every started thread, then forgets them */
void *pb_sem_create(void);                /* counting, initial 0 */
void pb_sem_post(void *s);
void pb_sem_wait(void *s);
void pb_sem_destroy(void *s);
uint32_t pb_fpscr_get(void);              /* ARMv7 FPSCR / arm64 FPCR (low 32 bits) */
void pb_fpscr_set(uint32_t v);
const char *pb_plat_name(void);

#if defined(__arm__) || defined(__aarch64__)
#define pb_cpu_relax() __asm__ volatile("yield" ::: "memory")
#elif defined(__x86_64__)
#define pb_cpu_relax() __asm__ volatile("pause" ::: "memory")
#else
#define pb_cpu_relax() __asm__ volatile("" ::: "memory")
#endif

#ifdef __cplusplus
}
#endif
#endif
