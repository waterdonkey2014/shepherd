/* sp_jobs.cpp -- the physics worker pool (physics-plan-v2 section 1.9 "Multicore", Stage 7 item 2 = the Law of the Vita part 2). Ours.
 *
 * Native worker threads (never attached to Mono) that patch P3's forked phases run on (shim/sp_p3.cpp). A fork runs part 0 on the
 * calling thread and parts 1.. on the workers, then joins; the workers spin for SP_JOBS_SPIN_US after each fork (and after a wake at
 * the start of a step that may fork) and then sleep on a semaphore, so an idle pool costs nothing. The join (and sp_jobs_start's wait
 * for the workers to start) spins for at most SP_JOBS_SPIN_US too, then sleeps on the pool's own semaphore, which the last worker posts
 * (kit #9 review fix: a spin-only wait never ends on the Vita when a worker is ready on the core the calling thread spins on, at the
 * same priority -- the scheduler does not preempt a running thread for an equal one; sleeping gives the worker that core). Every fork carries the calling
 * thread's FPSCR (ARMv7) / FPCR (arm64) and a worker takes it before its part: the parts compute with exactly the calling thread's
 * floating-point mode (the Vita's is FZ + DN at thread entry; whatever the engine thread runs, the workers run). Kit #10 fidelity review fix:
 * SP_JOBS_FP_STAT counts what each part ran under (the host's check that the hand-over happened; 2 = the negative test, hand-over skipped).
 *
 * Threads: the host's (sp_host_jobs: the Vita eboot creates them on the cores and at the priority it chooses, vita/shepherd/
 * phys_host.inc.c) or, without one, pthreads on the Mac / Linux. A build with neither (the QEMU harness) has no workers:
 * sp_jobs_start says SP_ERR_FULL and every step stays serial. The pool is process-wide and outlives sp_init / sp_shutdown;
 * sp_jobs_stop ends it. Results never depend on the pool (bit-identical by construction, shim/sp_p3.cpp), only the speed does. */
#include "shepphys.h"
#include "sp_p3.h"

#include <Box2D/Common/b2StackAllocator.h>
#include <Box2D/Collision/b2Collision.h>
#include <new>
#include <stdlib.h>
#include <string.h>

#if defined(__APPLE__) || defined(__linux__)
#define SP_JOBS_PTHREADS 1
#include <pthread.h>
#include <time.h>
#if defined(__APPLE__)
#include <dispatch/dispatch.h>
#else
#include <semaphore.h>
#endif
#endif

namespace {

enum { MAXW = SP_P3_MAXPARTS - 1 };

inline uint32_t fp_get()
{
#if defined(__aarch64__)
    uint64_t v;
    __asm__ volatile("mrs %0, fpcr" : "=r"(v));
    return (uint32_t)v;
#elif defined(__arm__) && defined(__ARM_FP)
    uint32_t v;
    __asm__ volatile("vmrs %0, fpscr" : "=r"(v));
    return v;
#else
    return 0;
#endif
}
inline void fp_set(uint32_t v)
{
#if defined(__aarch64__)
    uint64_t w = v;
    __asm__ volatile("msr fpcr, %0" ::"r"(w) : "memory");
#elif defined(__arm__) && defined(__ARM_FP)
    __asm__ volatile("vmsr fpscr, %0" ::"r"(v) : "memory");
#else
    (void)v;
#endif
}
/* the FPSCR bits a fork hands over: the control bits (ARMv7: AHP DN FZ RMode Stride Len; arm64 FPCR: the same positions), never the
 * cumulative exception flags, which the calling thread's arithmetic sets as it goes */
inline uint32_t fp_mode(uint32_t v) { return v & 0x07FF0000u; }

#if defined(__arm__) || defined(__aarch64__)
#define cpu_relax() __asm__ volatile("yield" ::: "memory")
#elif defined(__x86_64__)
#define cpu_relax() __asm__ volatile("pause" ::: "memory")
#else
#define cpu_relax() __asm__ volatile("" ::: "memory")
#endif

struct Worker {
    int32_t idx;              /* 1..MAXW */
    void *sem;
    int32_t asleep;           /* atomic: waiting (or about to wait) on sem */
    int32_t started;          /* atomic */
    uint32_t fp0;             /* its FPSCR at thread entry (diagnostics) */
#ifdef SP_JOBS_PTHREADS
    pthread_t th;
#endif
};

struct Pool {
    Worker w[MAXW];
    int32_t n;                /* live workers */
    int32_t enabled;          /* SP_JOBS_ENABLE: 0 = the workers stay, every step serial */
    b2UnityTask fn;
    void *ctx;
    int32_t parts;
    uint32_t fp;
    uint32_t gen;             /* atomic: the fork generation */
    int32_t done;             /* atomic */
    int32_t quit;             /* atomic */
    uint64_t spin_ns;
    sp_jobs_host host;
    int32_t have_host;
    int32_t prewake_min;      /* wake the workers at a step start when the world has at least this many contacts (0 = the smallest threshold) */
    uint32_t forks, wakes, sleeps;
    /* kit #9 review fixes */
    void *jsem;               /* the calling thread sleeps here in a join / start wait once its spin is over; a worker posts it */
    int32_t jwait;            /* atomic: the calling thread waits (or is about to wait) on jsem */
    uint32_t jsleeps;         /* atomic: how often it did */
    int32_t dead;             /* sp_jobs_stop without a host thread_join: the workers were told to end, never joined -- no restart */
    /* kit #10 fidelity review fix (SP_JOBS_FP_STAT): 0 = off (kit #9), 1 = the parts' FPSCR counted, 2 = TEST, the hand-over skipped */
    int32_t fpstat;
    uint32_t fp_parts, fp_or, fp_and, fp_idc, fp_ufc;   /* atomic */
} P;                         /* zero-initialised: no static constructor */

b2UnityJobs J;                /* what Box2D sees through b2_unityJobs */
int32_t g_defaults_set;

void set_defaults()
{
    if (g_defaults_set) return;
    g_defaults_set = 1;
    P.enabled = 1;
    P.spin_ns = 200u * 1000u;
    /* the fork thresholds (physics-plan-v2 section 1.9 "per-phase fork thresholds", set from PhysBench's device run 2026-10-09 at 444 MHz:
     * a spinning fork + join 1.35 us with 2 threads / 1.50 us with 3, ~1.3 us per contact evaluated, the intro-sized phase 52 -> 23 us
     * on 3 threads): Collide and the trigger pass from 16 items, the islands from 4 */
    J.min_collide = 16;
    J.min_triggers = 16;
    J.min_islands = 4;
}

inline uint32_t load_u(const uint32_t *p) { return __atomic_load_n(p, __ATOMIC_SEQ_CST); }
inline int32_t load_i(const int32_t *p) { return __atomic_load_n(p, __ATOMIC_SEQ_CST); }

/* ---------------------------------------------------------------- platform: the host's, else pthreads */
#ifdef SP_JOBS_PTHREADS
uint64_t px_now_us()
{
#if defined(__APPLE__)
    return clock_gettime_nsec_np(CLOCK_UPTIME_RAW) / 1000u;
#else
    struct timespec ts;
    clock_gettime(CLOCK_MONOTONIC, &ts);
    return (uint64_t)ts.tv_sec * 1000000u + (uint64_t)ts.tv_nsec / 1000u;
#endif
}
void *px_sem_create()
{
#if defined(__APPLE__)
    return (void *)dispatch_semaphore_create(0);
#else
    sem_t *s = (sem_t *)malloc(sizeof(sem_t));
    if (s && sem_init(s, 0, 0) != 0) { free(s); s = nullptr; }
    return s;
#endif
}
void px_sem_post(void *s)
{
#if defined(__APPLE__)
    dispatch_semaphore_signal((dispatch_semaphore_t)s);
#else
    sem_post((sem_t *)s);
#endif
}
void px_sem_wait(void *s)
{
#if defined(__APPLE__)
    dispatch_semaphore_wait((dispatch_semaphore_t)s, DISPATCH_TIME_FOREVER);
#else
    while (sem_wait((sem_t *)s) != 0) {}
#endif
}
void px_sem_destroy(void *s)
{
#if defined(__APPLE__)
    if (s) dispatch_release((dispatch_semaphore_t)s);
#else
    if (s) { sem_destroy((sem_t *)s); free(s); }
#endif
}
#endif

uint64_t now_us() { return P.have_host ? P.host.now_us() :
#ifdef SP_JOBS_PTHREADS
    px_now_us();
#else
    0;
#endif
}
void *js_create() { return P.have_host ? P.host.sem_create() :
#ifdef SP_JOBS_PTHREADS
    px_sem_create();
#else
    nullptr;
#endif
}
void js_post(void *s) { if (P.have_host) P.host.sem_post(s);
#ifdef SP_JOBS_PTHREADS
    else px_sem_post(s);
#endif
}
void js_wait(void *s) { if (P.have_host) P.host.sem_wait(s);
#ifdef SP_JOBS_PTHREADS
    else px_sem_wait(s);
#endif
}
void js_destroy(void *s) { if (P.have_host) P.host.sem_destroy(s);
#ifdef SP_JOBS_PTHREADS
    else px_sem_destroy(s);
#endif
}

void wake(Worker *w)
{
    if (__atomic_exchange_n(&w->asleep, 0, __ATOMIC_SEQ_CST)) { js_post(w->sem); P.wakes++; }
}

/* kit #9 review fix: a worker's store the calling thread may wait for (done, started) is followed by this -- it posts jsem when the calling
 * thread said it sleeps (jwait). The store and the jwait load (both seq_cst) pair with the waiter's jwait store and condition load: either the
 * worker sees jwait set, or the waiter sees the store; a token posted for a waiter that saw the store anyway makes a later wait return at once
 * (the waiter just looks again) */
void signal_waiter()
{
    if (load_i(&P.jwait) && __atomic_exchange_n(&P.jwait, 0, __ATOMIC_SEQ_CST)) js_post(P.jsem);
}

/* the calling thread until cond(): spinning for at most the pool's spin time (the usual join is a few microseconds), then sleeping on jsem.
 * Sleeping lets a worker that waits for this core run (kit #9 review: a spin-only join could wait forever on the Vita) */
template <class Cond> void wait_until(Cond cond)
{
    if (cond()) return;
    uint64_t t0 = now_us(), spin_us = P.spin_ns / 1000u;
    for (uint32_t k = 1;; ++k) {
        cpu_relax();
        if (cond()) return;
        if ((k & 63) == 0 && now_us() - t0 >= spin_us) break;
    }
    for (;;) {
        __atomic_store_n(&P.jwait, 1, __ATOMIC_SEQ_CST);
        if (cond()) { __atomic_store_n(&P.jwait, 0, __ATOMIC_SEQ_CST); return; }
        __atomic_add_fetch(&P.jsleeps, 1, __ATOMIC_RELAXED);
        js_wait(P.jsem);
        __atomic_store_n(&P.jwait, 0, __ATOMIC_SEQ_CST);
        if (cond()) return;
    }
}

/* kit #10 fidelity review fix (SP_JOBS_FP_STAT on): after a part, on the worker -- the control bits it ran under and the sticky denormal flags it
 * left (ARMv7 FPSCR IDC bit 7 = a denormal input flushed under FZ 1, UFC bit 3 = a tiny inexact result; read and cleared, flags only). arm64
 * keeps its flags in FPSR: the Mac counts the control bits only */
void fp_note()
{
    uint32_t v = fp_get();
    __atomic_add_fetch(&P.fp_parts, 1u, __ATOMIC_RELAXED);
    __atomic_or_fetch(&P.fp_or, v & 0x07F70000u, __ATOMIC_RELAXED);
    __atomic_and_fetch(&P.fp_and, v & 0x07F70000u, __ATOMIC_RELAXED);
#if defined(__arm__)
    if (v & 0x80u) __atomic_add_fetch(&P.fp_idc, 1u, __ATOMIC_RELAXED);
    if (v & 0x08u) __atomic_add_fetch(&P.fp_ufc, 1u, __ATOMIC_RELAXED);
    if (v & 0x88u) fp_set(v & ~0x88u);
#endif
}

void worker_main(void *arg)
{
    Worker *me = (Worker *)arg;
    me->fp0 = fp_get();
    uint32_t seen = load_u(&P.gen);
    __atomic_store_n(&me->started, 1, __ATOMIC_SEQ_CST);
    signal_waiter();   /* (sp_jobs_start may wait for it) */
    for (;;) {
        uint32_t g;
        uint64_t t0 = now_us(), spin_us = P.spin_ns / 1000u;
        uint32_t k = 0;
        while ((g = load_u(&P.gen)) == seen) {
            cpu_relax();
            if ((++k & 63) == 0 && now_us() - t0 >= spin_us) {
                /* sleep: say so, look once more (a fork that missed the flag is seen here), then wait; a token posted for a fork we saw
                 * anyway makes a later wait return at once -- the loop just looks again */
                __atomic_store_n(&me->asleep, 1, __ATOMIC_SEQ_CST);
                if (load_u(&P.gen) != seen) { __atomic_store_n(&me->asleep, 0, __ATOMIC_SEQ_CST); continue; }
                __atomic_add_fetch(&P.sleeps, 1, __ATOMIC_RELAXED);
                js_wait(me->sem);
                __atomic_store_n(&me->asleep, 0, __ATOMIC_SEQ_CST);
                t0 = now_us();
                k = 0;
            }
        }
        seen = g;
        if (load_i(&P.quit)) break;
        if (me->idx < P.parts) {
            uint32_t cur = fp_get();
            int32_t st = __atomic_load_n(&P.fpstat, __ATOMIC_RELAXED);   /* (kit #10 fidelity review fix: 0 = kit #9's code path) */
            if (st != 2 && fp_mode(cur) != fp_mode(P.fp)) fp_set((cur & ~0x07FF0000u) | fp_mode(P.fp));
            P.fn(P.ctx, me->idx, P.parts);
            if (st) fp_note();
        }
        __atomic_add_fetch(&P.done, 1, __ATOMIC_SEQ_CST);
        signal_waiter();   /* (the join may sleep) */
    }
}

#ifdef SP_JOBS_PTHREADS
void *px_entry(void *arg)
{
#if defined(__APPLE__)
    pthread_set_qos_class_self_np(QOS_CLASS_USER_INTERACTIVE, 0);
#endif
    worker_main(arg);
    return nullptr;
}
#endif

/* the fork: part 0 here, 1.. on the workers, then the join */
void run(b2UnityTask fn, void *ctx, int32 parts)
{
    if (parts <= 1 || P.n == 0) { fn(ctx, 0, 1); return; }
    if (parts > P.n + 1) parts = P.n + 1;
    P.fn = fn;
    P.ctx = ctx;
    P.parts = parts;
    P.fp = fp_get();
    __atomic_store_n(&P.done, 0, __ATOMIC_SEQ_CST);
    __atomic_add_fetch(&P.gen, 1, __ATOMIC_SEQ_CST);
    for (int32_t i = 0; i < P.n; ++i) wake(&P.w[i]);
    P.forks++;
    fn(ctx, 0, parts);
    wait_until([] { return load_i(&P.done) >= P.n; });   /* (kit #9 review fix: spin, then sleep -- was a spin without end) */
}

/* (between steps only: no fork is in flight, every worker spins or sleeps outside P.fn) */
void stop_threads()
{
    __atomic_store_n(&P.quit, 1, __ATOMIC_SEQ_CST);
    __atomic_add_fetch(&P.gen, 1, __ATOMIC_SEQ_CST);
    for (int32_t i = 0; i < P.n; ++i) { __atomic_store_n(&P.w[i].asleep, 0, __ATOMIC_SEQ_CST); js_post(P.w[i].sem); }
    if (P.have_host && !P.host.thread_join) {
        /* kit #9 review fix: a host without a join cannot say when its workers have left. They are told to end (quit stays 1, so a worker
         * that has seen the generation change but not yet read quit still ends instead of running the last fork's task on a dead frame)
         * and their semaphores stay alive (a late wait must not land on a deleted one); the pool stays down for good */
        P.n = 0;
        P.dead = 1;
        return;
    }
    for (int32_t i = 0; i < P.n; ++i) {
        if (P.have_host) P.host.thread_join(P.w[i].idx);
#ifdef SP_JOBS_PTHREADS
        else pthread_join(P.w[i].th, nullptr);
#endif
    }
    for (int32_t i = 0; i < P.n; ++i) { js_destroy(P.w[i].sem); P.w[i].sem = nullptr; }
    if (P.jsem) { js_destroy(P.jsem); P.jsem = nullptr; }
    __atomic_store_n(&P.jwait, 0, __ATOMIC_SEQ_CST);
    P.n = 0;
    __atomic_store_n(&P.quit, 0, __ATOMIC_SEQ_CST);   /* (every worker has ended: joined) */
}

/* the pool's scratch now, in bytes (kit #9 review fix: reported, SP_JOBS_ST_SCRATCH_BYTES -- the host's END line and t1_runner print it) */
int32_t scratch_bytes()
{
    size_t b = (size_t)J.cap * (sizeof(b2Contact *) + sizeof(b2Manifold) + 2) + (size_t)J.cap2 * (sizeof(b2Manifold) + 1) + (size_t)J.chk_cap;
    for (int32_t p = 1; p < SP_P3_MAXPARTS; ++p) if (J.stacks[p]) b += sizeof(b2StackAllocator);
    return b > 0x7FFFFFFF ? 0x7FFFFFFF : (int32_t)b;
}

} /* namespace */

/* ---------------------------------------------------------------- scratch (sp_p3.h) */
bool sp_p3_reserve(b2UnityJobs *j, int32 n)
{
    if (n > j->cap) {
        int32 cap = j->cap ? j->cap : 64;
        while (cap < n) cap *= 2;
        void *a = realloc(j->cand, (size_t)cap * sizeof(b2Contact *));
        if (!a) return false;
        j->cand = (b2Contact **)a;
        if (!(a = realloc(j->man, (size_t)cap * sizeof(b2Manifold)))) return false;
        j->man = (b2Manifold *)a;
        if (!(a = realloc(j->ov, (size_t)cap))) return false;
        j->ov = (uint8_t *)a;
        if (!(a = realloc(j->sensor, (size_t)cap))) return false;
        j->sensor = (uint8_t *)a;
        j->cap = cap;
    }
    if (j->check && j->cap2 < j->cap) {   /* kit #9 review fix: the check mode's serial copies only while it is on (they were always made) */
        void *a = realloc(j->man2, (size_t)j->cap * sizeof(b2Manifold));
        if (!a) return false;
        j->man2 = (b2Manifold *)a;
        if (!(a = realloc(j->ov2, (size_t)j->cap))) return false;
        j->ov2 = (uint8_t *)a;
        j->cap2 = j->cap;
    }
    return true;
}

bool sp_p3_reserve_check(b2UnityJobs *j, int32 bytes)
{
    if (bytes <= j->chk_cap) return true;
    void *a = realloc(j->chk, (size_t)bytes);
    if (!a) return false;
    j->chk = a;
    j->chk_cap = bytes;
    return true;
}

b2StackAllocator *sp_p3_stack(b2UnityJobs *j, int32 part)
{
    if (part < 1 || part >= SP_P3_MAXPARTS) return nullptr;
    if (!j->stacks[part]) {
        void *m = malloc(sizeof(b2StackAllocator));   /* (100 KiB each, only once an island phase forks; never Box2D's arena) */
        if (!m) return nullptr;
        j->stacks[part] = new (m) b2StackAllocator();
    }
    return j->stacks[part];
}

/* ---------------------------------------------------------------- the shim's step hook (shepphys.cpp impl_world_step) */
b2UnityJobs *sp_jobs_for_step(int32_t contacts)
{
    if (P.n == 0 || !P.enabled) return nullptr;
    J.run = run;
    J.parts = P.n + 1;
    J.st.steps++;
    int32_t pm = P.prewake_min;
    if (pm <= 0) {
        pm = 0x7FFFFFFF;
        if (J.min_collide > 0 && J.min_collide < pm) pm = J.min_collide;
        if (J.min_triggers > 0 && J.min_triggers < pm) pm = J.min_triggers;
    }
    if (contacts >= pm)   /* a step that may fork: wake the workers now, they spin through the step's serial parts */
        for (int32_t i = 0; i < P.n; ++i) wake(&P.w[i]);
    return &J;
}

extern "C" {

SP_API int32_t sp_host_jobs(const sp_jobs_host *ops)
{
    if (P.n) return SP_ERR_LOCKED;
    if (!ops) { memset(&P.host, 0, sizeof P.host); P.have_host = 0; return SP_OK; }
    if (!ops->thread_start || !ops->sem_create || !ops->sem_post || !ops->sem_wait || !ops->sem_destroy || !ops->now_us) return SP_ERR_PARAM;
    P.host = *ops;
    P.have_host = 1;
    return SP_OK;
}

SP_API int32_t sp_jobs_start(int32_t workers)
{
    set_defaults();
    if (workers < 0 || workers > MAXW) return SP_ERR_PARAM;
    if (P.n) return workers == P.n ? SP_OK : SP_ERR_LOCKED;
    if (workers == 0) return SP_OK;
    if (P.dead) return SP_ERR_LOCKED;   /* (kit #9 review fix: stopped without a join -- see stop_threads) */
#ifndef SP_JOBS_PTHREADS
    if (!P.have_host) return SP_ERR_FULL;   /* no threads in this build (the QEMU harness): every step stays serial */
#endif
    __atomic_store_n(&P.quit, 0, __ATOMIC_SEQ_CST);
    if (!P.jsem && !(P.jsem = js_create())) return SP_ERR_FULL;   /* (kit #9 review fix: the join's sleep) */
    __atomic_store_n(&P.jwait, 0, __ATOMIC_SEQ_CST);
    int32_t made = 0;
    for (int32_t i = 0; i < workers; ++i) {
        Worker *w = &P.w[i];
        memset((void *)w, 0, sizeof *w);
        w->idx = i + 1;
        w->sem = js_create();
        if (!w->sem) break;
        int32_t r;
        if (P.have_host) r = P.host.thread_start(w->idx, worker_main, w);
#ifdef SP_JOBS_PTHREADS
        else {
            pthread_attr_t a;
            pthread_attr_init(&a);
            pthread_attr_setstacksize(&a, 256u * 1024u);
            r = pthread_create(&w->th, &a, px_entry, w) == 0 ? 0 : -1;
            pthread_attr_destroy(&a);
        }
#else
        else r = -1;
#endif
        if (r != 0) { js_destroy(w->sem); w->sem = nullptr; break; }
        made++;
        P.n = made;   /* (each started worker counts from now: stop_threads joins exactly these) */
    }
    wait_until([made] { for (int32_t i = 0; i < made; ++i) if (!load_i(&P.w[i].started)) return false; return true; });   /* (review fix: spin, then sleep) */
    if (!made) { js_destroy(P.jsem); P.jsem = nullptr; }
    return made == workers ? SP_OK : (made ? SP_OK : SP_ERR_FULL);
}

SP_API int32_t sp_jobs_stop(void)
{
    if (P.n) stop_threads();
    for (int32_t p = 1; p < SP_P3_MAXPARTS; ++p)
        if (J.stacks[p]) { J.stacks[p]->~b2StackAllocator(); free(J.stacks[p]); J.stacks[p] = nullptr; }
    free(J.cand); free(J.man); free(J.man2); free(J.ov); free(J.ov2); free(J.sensor); free(J.chk);
    J.cand = nullptr; J.man = J.man2 = nullptr; J.ov = J.ov2 = J.sensor = nullptr; J.cap = J.cap2 = 0; J.chk = nullptr; J.chk_cap = 0;
    return SP_OK;
}

SP_API int32_t sp_jobs_set(int32_t key, int32_t value)
{
    set_defaults();
    switch (key) {
    case SP_JOBS_ENABLE: P.enabled = value ? 1 : 0; return SP_OK;
    case SP_JOBS_MIN_COLLIDE: J.min_collide = value; return SP_OK;
    case SP_JOBS_MIN_TRIGGERS: J.min_triggers = value; return SP_OK;
    case SP_JOBS_MIN_ISLANDS: J.min_islands = value; return SP_OK;
    case SP_JOBS_SPIN_US: P.spin_ns = (uint64_t)(value < 0 ? 0 : value) * 1000u; return SP_OK;
    case SP_JOBS_CHECK: J.check = value ? 1 : 0; return SP_OK;
    case SP_JOBS_PREWAKE: P.prewake_min = value; return SP_OK;
    case SP_JOBS_STATS_RESET: memset(&J.st, 0, sizeof J.st); P.forks = P.wakes = 0; __atomic_store_n(&P.sleeps, 0, __ATOMIC_RELAXED);
        __atomic_store_n(&P.jsleeps, 0, __ATOMIC_RELAXED); return SP_OK;
    case SP_JOBS_FP_STAT:   /* (kit #10 fidelity review fix: before the workers start; the counters start over) */
        if (value < 0 || value > 2) return SP_ERR_PARAM;
        __atomic_store_n(&P.fp_parts, 0u, __ATOMIC_RELAXED); __atomic_store_n(&P.fp_or, 0u, __ATOMIC_RELAXED);
        __atomic_store_n(&P.fp_and, 0xFFFFFFFFu, __ATOMIC_RELAXED); __atomic_store_n(&P.fp_idc, 0u, __ATOMIC_RELAXED);
        __atomic_store_n(&P.fp_ufc, 0u, __ATOMIC_RELAXED);
        __atomic_store_n(&P.fpstat, value, __ATOMIC_SEQ_CST);
        return SP_OK;
    default: return SP_ERR_PARAM;
    }
}

SP_API int32_t sp_jobs_get(int32_t key)
{
    set_defaults();
    switch (key) {
    case SP_JOBS_WORKERS: return P.n;
    case SP_JOBS_ENABLE: return P.enabled;
    case SP_JOBS_MIN_COLLIDE: return J.min_collide;
    case SP_JOBS_MIN_TRIGGERS: return J.min_triggers;
    case SP_JOBS_MIN_ISLANDS: return J.min_islands;
    case SP_JOBS_SPIN_US: return (int32_t)(P.spin_ns / 1000u);
    case SP_JOBS_CHECK: return J.check;
    case SP_JOBS_PREWAKE: return P.prewake_min;
    case SP_JOBS_HOST: return P.have_host;
    case SP_JOBS_ST_STEPS: return (int32_t)J.st.steps;
    case SP_JOBS_ST_FORKS: return (int32_t)P.forks;
    case SP_JOBS_ST_WAKES: return (int32_t)P.wakes;
    case SP_JOBS_ST_SLEEPS: return (int32_t)__atomic_load_n(&P.sleeps, __ATOMIC_RELAXED);
    case SP_JOBS_ST_COLLIDE_FORKS: return (int32_t)J.st.collide_forks;
    case SP_JOBS_ST_COLLIDE_ITEMS: return (int32_t)J.st.collide_items;
    case SP_JOBS_ST_COLLIDE_SMALL: return (int32_t)J.st.collide_small;
    case SP_JOBS_ST_TRIGGER_FORKS: return (int32_t)J.st.trigger_forks;
    case SP_JOBS_ST_TRIGGER_ITEMS: return (int32_t)J.st.trigger_items;
    case SP_JOBS_ST_TRIGGER_SMALL: return (int32_t)J.st.trigger_small;
    case SP_JOBS_ST_ISLAND_FORKS: return (int32_t)J.st.island_forks;
    case SP_JOBS_ST_ISLANDS: return (int32_t)J.st.islands_forked;
    case SP_JOBS_ST_ISLANDS_BIG: return (int32_t)J.st.islands_big;
    case SP_JOBS_ST_ISLAND_SMALL: return (int32_t)J.st.island_small;
    case SP_JOBS_ST_ISLAND_JOINTS: return (int32_t)J.st.island_joints;
    case SP_JOBS_ST_ISLAND_STATICS: return (int32_t)J.st.island_statics;
    case SP_JOBS_ST_ANOMALIES: return (int32_t)J.st.anomalies;
    case SP_JOBS_ST_CHECK_RUNS: return (int32_t)J.st.check_runs;
    case SP_JOBS_ST_CHECK_MISMATCHES: return (int32_t)J.st.check_mismatches;
    case SP_JOBS_ST_WORKER_FP0: return P.n ? (int32_t)P.w[0].fp0 : 0;
    case SP_JOBS_ST_HANDBACK_SLEEPERS: return (int32_t)J.st.handback_sleepers;
    case SP_JOBS_ST_SCRATCH_BYTES: return scratch_bytes();
    case SP_JOBS_ST_JOIN_SLEEPS: return (int32_t)__atomic_load_n(&P.jsleeps, __ATOMIC_RELAXED);
    case SP_JOBS_FP_STAT: return P.fpstat;   /* (kit #10 fidelity review fix) */
    case SP_JOBS_ST_FP_PARTS: return (int32_t)__atomic_load_n(&P.fp_parts, __ATOMIC_RELAXED);
    case SP_JOBS_ST_FP_CTRL_OR: return (int32_t)__atomic_load_n(&P.fp_or, __ATOMIC_RELAXED);
    case SP_JOBS_ST_FP_CTRL_AND: return __atomic_load_n(&P.fp_parts, __ATOMIC_RELAXED) ? (int32_t)__atomic_load_n(&P.fp_and, __ATOMIC_RELAXED) : 0;
    case SP_JOBS_ST_FP_IDC: return (int32_t)__atomic_load_n(&P.fp_idc, __ATOMIC_RELAXED);
    case SP_JOBS_ST_FP_UFC: return (int32_t)__atomic_load_n(&P.fp_ufc, __ATOMIC_RELAXED);
    default: return SP_ERR_PARAM;
    }
}

} /* extern "C" */
