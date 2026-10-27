/* SHEPHERD-VITA (devfail1, 2026-10-08): a corrected sem_post for vitasdk's pthread-embedded (libpthread.a).
 *
 * The bug. pthread-embedded keeps a semaphore as { int value; pthread_mutex_t lock; SceUID kernel semaphore }: sem_wait /
 * sem_timedwait decrement value and block on the kernel semaphore only when it went negative (a waiter must sleep). Its sem_post
 * (sem_post.o, disassembled: pte_osSemaphorePost is called BEFORE the "++value <= 0" test, i.e. always) signals the kernel
 * semaphore on EVERY post, also when nobody is waiting. Every "post first, wait later" pair therefore leaves one stale count in the
 * kernel semaphore, and a later wait that really has to block consumes it and returns at once, without any post. Checked in vlx
 * with the stock libpthread.a: post, wait (no block), then a 200 ms sem_timedwait returns 0 after 0 ms with value -1.
 * (pthreads-win32, which pthread-embedded comes from, posts only when ++value <= 0; pthread-embedded's own sem_post_multiple does.)
 *
 * What it broke. Mono's coop stop-the-world waits on a semaphore that suspended threads post (suspend_semaphore): when a thread
 * parked before the GC got to waiting, the next collection's wait returned early and the GC read a thread that had not parked yet
 * -> "Cannot read suspend state when target ... is in the ASYNC_SUSPEND_REQUESTED state" (the V5 hello's M10 in vlx 7 of 7 runs,
 * the menu after the Main Menu; the diag runtime's SUSPEND-STATE line: posts 4, waits 5, semaphore value -1). The same stale count
 * hits the per-thread resume semaphores and pthread-embedded's condition variables (cond_wait uses sem_post on its internal
 * block-lock semaphore), and so Monitor.Wait, the thread pool and everything built on them.
 *
 * The fix: this sem_post, linked into the app ahead of libpthread.a (the System.Native shim archive defines it, so the archive's
 * sem_post.o is never pulled in), signals the kernel semaphore only when ++value <= 0 -- the pthreads-win32 rule -- which matches
 * what pthread-embedded's waits, its timed-wait cleanup and sem_trywait already assume. The struct layout below is
 * pthread-embedded's (sem_post.o / sem_timedwait.o: value +0, lock +4, kernel semaphore +8). pthread-embedded keeps sem_post in an
 * object of its own, so nothing else is replaced. */
#include <errno.h>
#include <pthread.h>
#include <semaphore.h>

struct shepherd_pte_sem {
	int value;              /* > 0: free count; < 0: -(threads blocked on the kernel semaphore) */
	pthread_mutex_t lock;
	int kernel_sema;        /* pte_osSemaphoreHandle (SceUID), created with max 32767 */
};

/* pthread-embedded's OS layer (vita_osal.o): sceKernelSignalSema (handle, count); returns 0 (PTE_OS_OK) */
extern int pte_osSemaphorePost (int handle, int count);

#define SHEPHERD_PTE_SEM_VALUE_MAX 32767   /* the kernel semaphore's maximum (pte_osSemaphoreCreate) */

int sem_post (sem_t *sem)
{
	struct shepherd_pte_sem *s = sem ? *(struct shepherd_pte_sem **) sem : NULL;
	int result;
	if (s == NULL) {
		errno = EINVAL;
		return -1;
	}
	if ((result = pthread_mutex_lock (&s->lock)) != 0) {
		errno = result;
		return -1;
	}
	if (*(struct shepherd_pte_sem **) sem == NULL)   /* destroyed meanwhile (sem_destroy.c) */
		result = EINVAL;
	else if (s->value >= SHEPHERD_PTE_SEM_VALUE_MAX)
		result = ERANGE;
	else if (++s->value <= 0 && pte_osSemaphorePost (s->kernel_sema, 1) != 0) {   /* wake ONE blocked waiter, only if there is one */
		s->value--;
		result = EINVAL;
	}
	pthread_mutex_unlock (&s->lock);
	if (result != 0) {
		errno = result;
		return -1;
	}
	return 0;
}
