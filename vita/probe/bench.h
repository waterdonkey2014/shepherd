/* bench.h -- CPU calibration kernels shared VERBATIM by the Mac reference build
 * (bench_mac.c) and the Vita probe (main.c). Nothing platform-specific lives here
 * or in bench.c: the host passes a clock, a logger and a pre-run hook.
 * All synthetic data; no game code or assets. */
#ifndef SHEPHERD_BENCH_H
#define SHEPHERD_BENCH_H
#include <stddef.h>
#include <stdint.h>

#define BENCH_VERSION "bench-v3"
#define BENCH_MAX_RESULTS 12
/* arena the host must provide (largest kernel: 32 MiB chase + 2 MiB shuffle temp) */
#define BENCH_ARENA_BYTES (40u * 1024u * 1024u)

typedef struct bench_host {
  uint64_t (*now_us)(void);                 /* monotonic microseconds */
  void (*log)(const char *line);            /* one line, no trailing newline needed */
  /* called right before each timed kernel; writes a short note (e.g. clocks) and
     returns nonzero if the run should be marked SUSPECT (e.g. arm clock != 444) */
  int (*pre)(const char *name, char *note, int note_len);
} bench_host;

typedef struct bench_result {
  char name[24];
  double min_ms, med_ms;  /* per run */
  double metric;          /* kernel-specific figure of merit, see unit */
  char unit[12];
  uint32_t checksum;      /* must match between Mac and Vita */
  int reps;
  int suspect;
  char note[64];
} bench_result;

/* Runs every kernel; returns the number of results written (<= max). */
int bench_run_all(const bench_host *h, void *arena, size_t arena_bytes, bench_result *out, int max);

#endif
