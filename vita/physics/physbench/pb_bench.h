/* pb_bench.h -- PhysBench's measurement cases (ours), shared VERBATIM by the Vita eboot (vita_main.c) and the Mac
 * reference (mac_main.c); the host passes a logger, an optional Mac-reference lookup and a stop check.
 *
 *   T1    the 8 T1 determinism scenarios against the Mac goldens: bit-exact, or max ulp / first differing record
 *   STEP  libshepphys step cost of the plan's representative scenes (pb_scene.h), 50 warm-up + 500 measured steps
 *   B2    the same scenes on Box2D directly (no shim): step cost, and on the Mac the b2Profile phase split
 *   CALL  C-side cost per API call / query (the floor under the managed glue, which a pure C app cannot measure)
 *   ASTAR A*-shaped OverlapCircle grid scans (intro room 51 x 77, base 150 x 150) through the shim and child-aware
 *   FJ    fork-join overhead of the worker pool (spin / block / hybrid, 2 and 3 threads)
 *   NP    the plan's parallel phases prototyped: Evaluate / TestOverlap per contact split over 1-3 threads, bit-checked
 *   MW    1-3 independent intro worlds stepped concurrently: per-core slowdown from sharing L2 / memory
 */
#ifndef PB_BENCH_H
#define PB_BENCH_H

#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

#define PB_BENCH_VERSION "physbench-1"

typedef struct pb_res {
    char key[40];
    double v;               /* the value (unit below); 0 for pure checks */
    char unit[4];           /* "us", "ns", "ms", "x" or "" */
    uint64_t check;         /* hash / count that must match the Mac reference when has_check */
    int32_t has_check;
} pb_res;

#define PB_MAX_RESULTS 320
typedef struct pb_results {
    pb_res r[PB_MAX_RESULTS];
    int32_t n;
} pb_results;

typedef struct pb_host {
    void (*log)(const char *line);
    /* Mac reference lookup (the Vita host); NULL on the Mac. Returns 1 if key is in the reference. */
    int32_t (*ref)(const char *key, double *v, uint64_t *check, int32_t *has_check);
    int32_t (*stop)(void);  /* nonzero: skip what is left (the owner pressed START) */
    const char *tag;        /* prefix of every result line, e.g. "444" (MHz) or "mac" */
    int32_t workers;        /* worker threads beside the caller (2) */
    uint32_t fpscr;         /* the FP control value workers set (the caller's) */
    int32_t quick;          /* 1: fewer repetitions (smoke test) */
} pb_host;

void pb_bench_header(const pb_host *h);
int32_t pb_bench_t1(const pb_host *h, pb_results *R);         /* returns scenarios bit-exact */
void pb_bench_scenes(const pb_host *h, pb_results *R);
void pb_bench_b2(const pb_host *h, pb_results *R);
void pb_bench_calls(const pb_host *h, pb_results *R);
void pb_bench_astar(const pb_host *h, pb_results *R);
void pb_bench_threads(const pb_host *h, pb_results *R);
void pb_bench_summary(const pb_host *h, const pb_results *R);
const pb_res *pb_find(const pb_results *R, const char *key);

#ifdef __cplusplus
}
#endif
#endif
