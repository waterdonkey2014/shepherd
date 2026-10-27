/* Platform layer: POSIX (macOS test build). */
#include "sa_internal.h"
#include <pthread.h>
#include <time.h>
#include <errno.h>

typedef char sa_static_assert_mutex[(sizeof(pthread_mutex_t) <= sizeof(((sa_mutex *)0)->u.bytes)) ? 1 : -1];

int sa_mutex_init(sa_mutex *m, const char *name)
{
    (void)name;
    return pthread_mutex_init((pthread_mutex_t *)m->u.bytes, NULL) == 0 ? 0 : -1;
}
void sa_mutex_destroy(sa_mutex *m) { pthread_mutex_destroy((pthread_mutex_t *)m->u.bytes); }
void sa_mutex_lock(sa_mutex *m) { pthread_mutex_lock((pthread_mutex_t *)m->u.bytes); }
void sa_mutex_unlock(sa_mutex *m) { pthread_mutex_unlock((pthread_mutex_t *)m->u.bytes); }

typedef struct { sa_thread_fn fn; void *arg; } sa_tramp;
static void *tramp(void *p)
{
    sa_tramp t = *(sa_tramp *)p;
    free(p);
    t.fn(t.arg);
    return NULL;
}
int sa_thread_start(sa_thread *t, const char *name, sa_thread_fn fn, void *arg, int priority, int affinity, int stack_bytes)
{
    (void)name; (void)priority; (void)affinity;
    pthread_attr_t a; pthread_t th;
    sa_tramp *tp = (sa_tramp *)malloc(sizeof *tp);
    if (!tp) return -1;
    tp->fn = fn; tp->arg = arg;
    pthread_attr_init(&a);
    if (stack_bytes > 0) pthread_attr_setstacksize(&a, (size_t)stack_bytes);
    if (pthread_create(&th, &a, tramp, tp) != 0) { pthread_attr_destroy(&a); free(tp); return -1; }
    pthread_attr_destroy(&a);
    t->h = (void *)th; t->running = 1;
    return 0;
}
void sa_thread_join(sa_thread *t)
{
    if (!t->running) return;
    pthread_join((pthread_t)t->h, NULL);
    t->running = 0;
}
void sa_sleep_us(unsigned us)
{
    struct timespec ts; ts.tv_sec = us / 1000000u; ts.tv_nsec = (long)(us % 1000000u) * 1000L;
    while (nanosleep(&ts, &ts) != 0 && errno == EINTR) {}
}
double sa_now(void)
{
    struct timespec ts; clock_gettime(CLOCK_MONOTONIC, &ts);
    return (double)ts.tv_sec + ts.tv_nsec * 1e-9;
}
