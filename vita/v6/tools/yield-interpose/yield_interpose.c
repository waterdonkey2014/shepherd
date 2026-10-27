/* V6 stage 2 (vita/notes/engine-pass-speed.md): a Mac-only DYLD interposer that gives sched_yield the PS Vita's managed
 * Thread.Yield back-off (the fork's mono-vita-yield.h, included as is), so the vita-sim can show its effect: on the Mac, Mono's
 * Thread.Yield is sched_yield. Approximation: here every sched_yield of the process backs off (also the runtime's own), on the
 * Vita only managed Thread.Yield does. Use: sim.sh threads <name> SIM_YIELD=vita (DYLD_INSERT_LIBRARIES). Never in a Vita build. */
#include <sched.h>
#include <stdint.h>
#include <time.h>
#include <unistd.h>
#include "mono/utils/mono-vita-yield.h"

static uint64_t now_us (void) { struct timespec ts; clock_gettime (CLOCK_MONOTONIC, &ts); return (uint64_t) ts.tv_sec * 1000000u + (uint64_t) ts.tv_nsec / 1000u; }
static __thread uint64_t last; static __thread unsigned run;

static int vita_sched_yield (void)
{
	run = mono_vita_yield_next_run (last, now_us (), run);
	usleep (mono_vita_yield_delay_us (run));
	last = now_us ();
	return 0;
}

#define DYLD_INTERPOSE(_replacement, _replacee) \
	__attribute__((used)) static struct { const void *replacement; const void *replacee; } _interpose_##_replacee \
	__attribute__ ((section ("__DATA,__interpose"))) = { (const void *) (unsigned long) &_replacement, (const void *) (unsigned long) &_replacee };
DYLD_INTERPOSE (vita_sched_yield, sched_yield)
