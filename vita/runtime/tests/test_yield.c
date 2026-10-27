/* SHEPHERD-VITA V6 stage 2: Mac unit test of mono-vita-yield.c, the PS Vita's managed Thread.Yield (vita/notes/engine-pass-speed.md).
 *
 * 1. The pure policy (mono-vita-yield.h): run counting and the delay ladder.
 * 2. The real mono_vita_managed_yield, built from the fork with fake psp2 calls on a SIMULATED clock (sceKernelDelayThread
 *    advances it): the game's patterns, old policy (a constant 100 us, mono_threads_platform_yield) against the new one:
 *    - DispatchThread's idle poller (a 20 us loop body, then Thread.Yield) for one simulated second: wake-ups and the share of
 *      the core it keeps busy;
 *    - a completion wait (spine-unity's BaseLateUpdate spinning on Thread.Yield until the worker is done) for jobs of 0.05..20 ms:
 *      how long after the job ends the waiter notices (overshoot);
 *    - a thread that works between yields (gaps above MONO_VITA_YIELD_GAP_US) never backs off;
 *    - two threads keep separate runs.
 * 3. On a REAL Mac thread (fake psp2 calls = usleep + the monotonic clock): an idle poller (~20 us of spinning per poll) and its
 *    thread CPU time over 0.5 s with the new yield vs the old constant 100 us sleep, from the Mach thread's own user + system time.
 * Build: run-tests.sh */
#include <assert.h>
#include <mach/mach.h>
#include <pthread.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>
#include <unistd.h>
#include "mono/utils/mono-vita.h"
#include "mono/utils/mono-vita-yield.h"
#include <psp2/kernel/threadmgr.h>
#include <psp2/kernel/processmgr.h>

static int failures;
#define CHECK(c, ...) do { if (!(c)) { failures++; printf ("FAIL %s:%d: ", __FILE__, __LINE__); printf (__VA_ARGS__); printf ("\n"); } } while (0)

/* ---- fake psp2: simulated or real ---- */
static int real_mode;
static uint64_t sim_now = 1000000;          /* us */
static SceUID sim_tid = 1;
static uint64_t sim_slept, sim_wakeups;
SceUID sceKernelGetThreadId (void) { return real_mode ? (SceUID) (uintptr_t) pthread_mach_thread_np (pthread_self ()) : sim_tid; }
SceUInt64 sceKernelGetProcessTimeWide (void)
{
	if (!real_mode) return sim_now;
	struct timespec ts; clock_gettime (CLOCK_MONOTONIC, &ts); return (uint64_t) ts.tv_sec * 1000000u + (uint64_t) ts.tv_nsec / 1000u;
}
int sceKernelDelayThread (SceUInt delay)
{
	if (real_mode) { usleep (delay); return 0; }
	sim_now += delay; sim_slept += delay; sim_wakeups++; return 0;
}

static void old_yield (void) { sceKernelDelayThread (MONO_VITA_YIELD_BASE_US); }   /* mono_threads_platform_yield on the Vita */

/* ---- 1. policy ---- */
static void test_policy (void)
{
	CHECK (mono_vita_yield_next_run (0, 5000, 7) == 0, "first yield ever starts a run");
	CHECK (mono_vita_yield_next_run (1000, 1000 + MONO_VITA_YIELD_GAP_US - 1, 3) == 4, "back to back continues the run");
	CHECK (mono_vita_yield_next_run (1000, 1000 + MONO_VITA_YIELD_GAP_US, 3) == 0, "a gap of GAP_US starts a new run");
	CHECK (mono_vita_yield_next_run (1000, 999, 3) == 0, "a clock going backwards starts a new run");
	CHECK (mono_vita_yield_next_run (1, 2, 0xffffu) == 0xffffu, "the run saturates");
	for (unsigned r = 0; r < 16; r++) CHECK (mono_vita_yield_delay_us (r) == 100, "run %u: 100 us", r);
	CHECK (mono_vita_yield_delay_us (16) == 250 && mono_vita_yield_delay_us (31) == 250, "16..31: 250 us");
	CHECK (mono_vita_yield_delay_us (32) == 500 && mono_vita_yield_delay_us (63) == 500, "32..63: 500 us");
	CHECK (mono_vita_yield_delay_us (64) == 1000 && mono_vita_yield_delay_us (60000) == 1000, "64+: 1000 us");
	unsigned prev = 0; for (unsigned r = 0; r < 200; r++) { unsigned d = mono_vita_yield_delay_us (r); CHECK (d >= prev && d <= MONO_VITA_YIELD_MAX_US, "monotone, capped"); prev = d; }
}

/* ---- 2. simulated ---- */
typedef void (*YieldFn) (void);
/* idle poller: body_us of work, then yield, for total_us of simulated time -> wake-ups per second, busy share */
static void idle_poller (YieldFn y, uint64_t body_us, uint64_t total_us, double *wakeups_per_s, double *busy)
{
	uint64_t start = sim_now, w0 = sim_wakeups, busy_us = 0;
	while (sim_now - start < total_us) { sim_now += body_us; busy_us += body_us; y (); }
	double secs = (sim_now - start) / 1e6;
	*wakeups_per_s = (sim_wakeups - w0) / secs; *busy = (double) busy_us / (sim_now - start);
}
/* completion wait: the job ends job_us after the wait starts; each poll costs check_us; returns the overshoot (us) */
static uint64_t completion_wait (YieldFn y, uint64_t job_us, uint64_t check_us, unsigned *yields)
{
	sim_now += 10000;   /* the thread did other work before waiting: a new run */
	uint64_t start = sim_now, done = start + job_us; unsigned n = 0;
	for (;;) { sim_now += check_us; if (sim_now >= done) break; y (); n++; }
	*yields = n; return sim_now - done;
}

static void test_simulated (void)
{
	double wps_old, busy_old, wps_new, busy_new;
	sim_tid = 101;
	idle_poller (old_yield, 20, 1000000, &wps_old, &busy_old);
	idle_poller (mono_vita_managed_yield, 20, 1000000, &wps_new, &busy_new);
	printf ("  idle poller (20 us body, 1 s): old 100 us yield: %.0f wake-ups/s, core %.1f %% busy; new: %.0f wake-ups/s, %.1f %% busy\n",
	        wps_old, 100 * busy_old, wps_new, 100 * busy_new);
	CHECK (wps_new < 1100 && wps_new > 900, "idle poller settles at ~1000 wake-ups/s (got %.0f)", wps_new);
	CHECK (busy_new < 0.025, "idle poller keeps < 2.5 %% of the core busy (got %.1f %%)", 100 * busy_new);
	CHECK (busy_old > 0.15, "the old policy kept > 15 %% busy (got %.1f %%)", 100 * busy_old);
	/* a slower (interpreted) body on the device: 60 us */
	idle_poller (mono_vita_managed_yield, 60, 1000000, &wps_new, &busy_new);
	printf ("  idle poller (60 us body): new: %.0f wake-ups/s, %.1f %% busy\n", wps_new, 100 * busy_new);
	CHECK (busy_new < 0.07, "60 us body: < 7 %% busy (got %.1f %%)", 100 * busy_new);

	sim_tid = 102;
	uint64_t jobs [] = { 50, 300, 1000, 1500, 2000, 5000, 20000 };
	for (size_t i = 0; i < sizeof jobs / sizeof jobs [0]; i++) {
		unsigned yo, yn;
		uint64_t oo = completion_wait (old_yield, jobs [i], 5, &yo), on = completion_wait (mono_vita_managed_yield, jobs [i], 5, &yn);
		printf ("  completion wait, job %5llu us: old overshoot %3llu us (%u yields), new %4llu us (%u yields)\n",
		        (unsigned long long) jobs [i], (unsigned long long) oo, yo, (unsigned long long) on, yn);
		CHECK (on <= MONO_VITA_YIELD_MAX_US + 5, "overshoot bounded by the longest delay (got %llu)", (unsigned long long) on);
		if (jobs [i] <= 1500) CHECK (on <= MONO_VITA_YIELD_BASE_US + 5, "a short wait (<= 1.5 ms) keeps the 100 us granularity (got %llu)", (unsigned long long) on);
	}

	/* a thread that works between yields: gaps above GAP_US -> never backs off */
	sim_tid = 103; uint64_t s0 = sim_slept;
	for (int i = 0; i < 1000; i++) { sim_now += MONO_VITA_YIELD_GAP_US + 50; mono_vita_managed_yield (); }
	CHECK (sim_slept - s0 == 1000 * MONO_VITA_YIELD_BASE_US, "working thread: every yield 100 us (slept %llu)", (unsigned long long) (sim_slept - s0));

	/* two threads interleaved keep separate runs: A spins (backs off), B works between its yields (does not) */
	uint64_t b_sl = 0;
	for (int i = 0; i < 200; i++) {
		sim_tid = 104; sim_now += 5; mono_vita_managed_yield ();
		sim_tid = 105; uint64_t t = sim_slept; sim_now += 400; mono_vita_managed_yield (); b_sl += sim_slept - t;
	}
	/* (A's gaps include B's turn: 400 + B's sleep > GAP_US, so A starts a new run every time too -- per-thread clocks) */
	CHECK (b_sl == 200 * MONO_VITA_YIELD_BASE_US, "thread B never backs off (slept %llu)", (unsigned long long) b_sl);

	uint64_t calls, delay, backed, noslot; mono_vita_get_yield_stats (&calls, &delay, &backed, &noslot);
	printf ("  stats: %llu managed yields, %.1f ms slept, %llu backed off, %llu without a slot\n",
	        (unsigned long long) calls, delay / 1000.0, (unsigned long long) backed, (unsigned long long) noslot);
	CHECK (calls > 0 && backed > 0 && noslot == 0, "stats count");
}

/* ---- 3. real Mac thread ---- */
static volatile int stop;
static double thread_cpu_s (thread_t th)
{
	thread_basic_info_data_t bi; mach_msg_type_number_t n = THREAD_BASIC_INFO_COUNT;
	if (thread_info (th, THREAD_BASIC_INFO, (thread_info_t) &bi, &n) != KERN_SUCCESS) return -1;
	return bi.user_time.seconds + bi.user_time.microseconds / 1e6 + bi.system_time.seconds + bi.system_time.microseconds / 1e6;
}
static volatile int queue_empty = 1;
static void *poller (void *arg)
{
	YieldFn y = (YieldFn) arg;
	/* ~20 us of work per poll, like the interpreted IsEmpty check + loop on the device */
	while (!stop) { uint64_t t0 = sceKernelGetProcessTimeWide (); while (sceKernelGetProcessTimeWide () - t0 < 20) { } if (!queue_empty) break; y (); }
	return NULL;
}
static double measure (YieldFn y, double secs, double *wall)
{
	pthread_t t; stop = 0; pthread_create (&t, NULL, poller, (void *) y);
	thread_t th = pthread_mach_thread_np (t);
	usleep (20000); double c0 = thread_cpu_s (th); struct timespec a, b; clock_gettime (CLOCK_MONOTONIC, &a);
	usleep ((useconds_t) (secs * 1e6));
	double c1 = thread_cpu_s (th); clock_gettime (CLOCK_MONOTONIC, &b);
	stop = 1; pthread_join (t, NULL);
	*wall = (b.tv_sec - a.tv_sec) + (b.tv_nsec - a.tv_nsec) / 1e9;
	return (c1 - c0) / *wall;
}
static void test_real (void)
{
	real_mode = 1; double w1, w2;
	double old_share = measure (old_yield, 0.5, &w1), new_share = measure (mono_vita_managed_yield, 0.5, &w2);
	printf ("  real Mac thread, idle poller 0.5 s: old 100 us yield %.1f %% of a core, new %.1f %%\n", 100 * old_share, 100 * new_share);
	CHECK (new_share >= 0 && new_share < old_share, "the new yield uses less CPU than the old one");
	CHECK (new_share < 0.05, "the new yield keeps the idle poller under 5 %% of a core (got %.1f %%)", 100 * new_share);
	CHECK (old_share > 2 * new_share, "at least halved (old %.1f %%, new %.1f %%)", 100 * old_share, 100 * new_share);
	real_mode = 0;
}

int main (void)
{
	test_policy (); test_simulated (); test_real ();
	if (failures) { printf ("FAIL test_yield: %d check(s)\n", failures); return 1; }
	printf ("PASS test_yield (policy, simulated poller / completion waits / working threads, real-thread CPU)\n");
	return 0;
}
