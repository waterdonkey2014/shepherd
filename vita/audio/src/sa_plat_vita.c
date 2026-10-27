/* Platform layer: PS Vita (vitasdk). SceKernel LwMutex + SceKernel threads: no pthread-embedded on the audio path
 * (its cancellation polling and CLOCK_REALTIME timed waits are avoided; see vita/notes/v5-runtime.md). */
#include "sa_internal.h"
#include <psp2/kernel/threadmgr.h>
#include <psp2/kernel/processmgr.h>

typedef char sa_static_assert_lw[(sizeof(SceKernelLwMutexWork) <= sizeof(((sa_mutex *)0)->u.bytes)) ? 1 : -1];

int sa_mutex_init(sa_mutex *m, const char *name)
{
    /* attr 0 = FIFO, not recursive */
    return sceKernelCreateLwMutex((SceKernelLwMutexWork *)m->u.bytes, name ? name : "shepaudio", 0, 0, NULL) < 0 ? -1 : 0;
}
void sa_mutex_destroy(sa_mutex *m) { sceKernelDeleteLwMutex((SceKernelLwMutexWork *)m->u.bytes); }
void sa_mutex_lock(sa_mutex *m) { sceKernelLockLwMutex((SceKernelLwMutexWork *)m->u.bytes, 1, NULL); }
void sa_mutex_unlock(sa_mutex *m) { sceKernelUnlockLwMutex((SceKernelLwMutexWork *)m->u.bytes, 1); }

/* k4-display: the Vita host's PIN line for each thread made here (host.c shepherd_vita_thread_note: the mask asked for + the kernel's
 * read-back). Weak: shepaudio's own builds (tests, the standalone lib) have no host and log nothing. */
__attribute__((weak)) void shepherd_vita_thread_note(int thid, const char *name, int mask) { (void)thid; (void)name; (void)mask; }

typedef struct { sa_thread_fn fn; void *arg; } sa_tramp;
static int tramp(SceSize args, void *argp)
{
    sa_tramp t = **(sa_tramp **)argp;   /* argp points at a copy of the pointer */
    free(*(sa_tramp **)argp);
    (void)args;
    t.fn(t.arg);
    return sceKernelExitThread(0);
}
int sa_thread_start(sa_thread *t, const char *name, sa_thread_fn fn, void *arg, int priority, int affinity, int stack_bytes)
{
    sa_tramp *tp = (sa_tramp *)malloc(sizeof *tp);
    if (!tp) return -1;
    tp->fn = fn; tp->arg = arg;
    if (priority <= 0) priority = 0x10000100;          /* SCE_KERNEL_DEFAULT_PRIORITY_USER */
    if (stack_bytes <= 0) stack_bytes = 64 * 1024;
    SceUID id = sceKernelCreateThread(name ? name : "shepaudio", tramp, priority, (SceSize)stack_bytes, 0, affinity, NULL);
    if (id < 0) { free(tp); return -1; }
    if (sceKernelStartThread(id, sizeof tp, &tp) < 0) { sceKernelDeleteThread(id); free(tp); return -1; }
    t->id = id; t->running = 1;
    shepherd_vita_thread_note(id, name ? name : "shepaudio", affinity);
    return 0;
}
void sa_thread_join(sa_thread *t)
{
    if (!t->running) return;
    int status = 0;
    sceKernelWaitThreadEnd(t->id, &status, NULL);
    sceKernelDeleteThread(t->id);
    t->running = 0;
}
void sa_sleep_us(unsigned us) { sceKernelDelayThread(us); }
double sa_now(void) { return (double)sceKernelGetProcessTimeWide() * 1e-6; }
