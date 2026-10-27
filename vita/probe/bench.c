/* bench.c -- identical source for the Mac reference run and the Vita probe.
 * Build both with -O2 -ffp-contract=off and auto-vectorisation OFF (scalar per-core
 * calibration; Mono interpreter/AOT code is not auto-vectorised either).
 * Checksums are deterministic and must be bit-identical on both machines. */
#include "bench.h"
#include <math.h>
#include <stdio.h>
#include <string.h>

#define REPS 5

static uint32_t xs32(uint32_t *s) {
  uint32_t x = *s;
  x ^= x << 13; x ^= x >> 17; x ^= x << 5;
  return *s = x;
}
static uint32_t fnv1a(uint32_t h, const void *p, size_t n) {
  const uint8_t *b = (const uint8_t *)p;
  for (size_t i = 0; i < n; i++) { h ^= b[i]; h *= 16777619u; }
  return h;
}
static void sort_d(double *a, int n) {
  for (int i = 1; i < n; i++) { double v = a[i]; int j = i - 1; while (j >= 0 && a[j] > v) { a[j + 1] = a[j]; j--; } a[j + 1] = v; }
}
__attribute__((unused)) static float clampf(float v, float lo, float hi) { return v < lo ? lo : (v > hi ? hi : v); }
__attribute__((unused)) static int clampi(int v, int lo, int hi) { return v < lo ? lo : (v > hi ? hi : v); }

/* ---------------------------------------------------------------- kernels */

/* 1. Stylizer: the dither-LUT maths Shepherd's CPU Stylizer does per pixel, on synthetic data. Owner-local since fid1-fixup D1 (a port of a
 *    third-party effect): engine/Derived/vita-probe/probe_stylizer_cpu.h, included when present (CMakeLists.txt / build.sh add its
 *    folder); without it the probe has no stylizer_960x544 kernel. */
#if defined(__has_include)
#if __has_include("probe_stylizer_cpu.h")
#include "probe_stylizer_cpu.h"
#endif
#endif

/* 2. Dependent-load chase (index-based so 32- and 64-bit hosts walk identical data).
 *    One node per 64 bytes, Sattolo single-cycle permutation. */
#define CHASE_STRIDE 16u /* uint32 words = 64 bytes */
static void chase_setup(uint32_t *arr, uint32_t *tmp, uint32_t nodes) {
  uint32_t s = 0xC0FFEEu;
  for (uint32_t i = 0; i < nodes; i++) tmp[i] = i;
  for (uint32_t i = nodes - 1; i > 0; i--) { uint32_t j = xs32(&s) % i; uint32_t t = tmp[i]; tmp[i] = tmp[j]; tmp[j] = t; }
  for (uint32_t i = 0; i < nodes; i++) arr[tmp[i] * CHASE_STRIDE] = tmp[(i + 1) % nodes] * CHASE_STRIDE;
}
static uint32_t chase_run(const uint32_t *arr, uint32_t loads) {
  uint32_t p = 0;
  for (uint32_t k = 0; k < loads; k++) p = arr[p];
  return p;
}

/* 3. Float: mat4 x vec4 over 64K vertices (sprite/vertex transform shape). 28 flops each. */
#define FV 65536u
#define FREPS 16
static void float_setup(float *in, float *m) {
  uint32_t s = 0xF10A7u;
  for (uint32_t i = 0; i < FV * 4; i++) in[i] = (float)(xs32(&s) % 2001u) / 1000.0f - 1.0f;
  for (int i = 0; i < 16; i++) m[i] = (float)((i * 37) % 17) / 16.0f - 0.5f;
}
static uint32_t float_run(const float *in, float *out, float *m) {
  for (int r = 0; r < FREPS; r++) {
    for (uint32_t i = 0; i < FV; i++) {
      const float *v = in + i * 4; float *o = out + i * 4;
      float x = v[0], y = v[1], z = v[2], w = v[3];
      o[0] = m[0] * x + m[4] * y + m[8] * z + m[12] * w;
      o[1] = m[1] * x + m[5] * y + m[9] * z + m[13] * w;
      o[2] = m[2] * x + m[6] * y + m[10] * z + m[14] * w;
      o[3] = m[3] * x + m[7] * y + m[11] * z + m[15] * w;
    }
    /* feed the result back so reps cannot be folded */
    float t = out[(r * 4099u) % (FV * 4)];
    m[r & 15] = m[r & 15] * 0.5f + t * 0.001f;
  }
  return fnv1a(2166136261u, out, FV * 16);
}

/* 4/5. Bandwidth: memcpy and a summing read over 8 MiB. */
#define BW_BYTES (8u * 1024u * 1024u)
#define BW_REPS 8
static uint32_t copy_run(uint8_t *dst, const uint8_t *src) {
  for (int r = 0; r < BW_REPS; r++) memcpy(dst, src, BW_BYTES);
  return fnv1a(2166136261u, dst, 4096);
}
static uint32_t readsum_run(const uint32_t *src) {
  uint32_t a = 0, b = 0, c = 0, d = 0;
  for (int r = 0; r < BW_REPS; r++)
    for (uint32_t i = 0; i < BW_BYTES / 4; i += 4) { a += src[i]; b ^= src[i + 1]; c += src[i + 2]; d ^= src[i + 3]; }
  return a ^ (b * 3u) ^ (c * 5u) ^ (d * 7u);
}

/* 6. Interpreter dispatch: tiny switch-dispatched register VM (shape of an IL interpreter loop). */
enum { OP_LDI, OP_MUL, OP_ADD, OP_XOR, OP_SHR, OP_MOV, OP_DEC, OP_JNZ, OP_HALT };
typedef struct { uint8_t op, a, b, c; int32_t imm; } vm_ins;
static int vm_build(vm_ins *code, uint32_t iters) {
  int n = 0;
  /* r0 = acc, r1 = counter, r2 = tmp, r3 = const 31, r4 = 7 */
  code[n++] = (vm_ins){OP_LDI, 0, 0, 0, 17};
  code[n++] = (vm_ins){OP_LDI, 1, 0, 0, (int32_t)iters};
  code[n++] = (vm_ins){OP_LDI, 3, 0, 0, 31};
  code[n++] = (vm_ins){OP_LDI, 4, 0, 0, 7};
  int loop = n;
  code[n++] = (vm_ins){OP_MUL, 2, 0, 3, 0};   /* r2 = acc*31     */
  code[n++] = (vm_ins){OP_ADD, 2, 2, 1, 0};   /* r2 += i         */
  code[n++] = (vm_ins){OP_SHR, 5, 0, 4, 0};   /* r5 = acc >> 7   */
  code[n++] = (vm_ins){OP_XOR, 0, 2, 5, 0};   /* acc = r2 ^ r5   */
  code[n++] = (vm_ins){OP_DEC, 1, 0, 0, 0};   /* i--             */
  code[n++] = (vm_ins){OP_JNZ, 1, 0, 0, loop};
  code[n++] = (vm_ins){OP_HALT, 0, 0, 0, 0};
  return n;
}
static uint32_t vm_run(const vm_ins *code, uint64_t *ops_out) {
  uint32_t r[8] = {0};
  uint64_t ops = 0;
  int pc = 0;
  for (;;) {
    const vm_ins *in = &code[pc++];
    ops++;
    switch (in->op) {
    case OP_LDI: r[in->a] = (uint32_t)in->imm; break;
    case OP_MUL: r[in->a] = r[in->b] * r[in->c]; break;
    case OP_ADD: r[in->a] = r[in->b] + r[in->c]; break;
    case OP_XOR: r[in->a] = r[in->b] ^ r[in->c]; break;
    case OP_SHR: r[in->a] = r[in->b] >> (r[in->c] & 31); break;
    case OP_MOV: r[in->a] = r[in->b]; break;
    case OP_DEC: r[in->a]--; break;
    case OP_JNZ: if (r[in->a]) pc = in->imm; break;
    case OP_HALT: *ops_out = ops; return r[0];
    default: *ops_out = ops; return 0xDEADu;
    }
  }
}

/* ---------------------------------------------------------------- driver */

typedef struct { double t[REPS]; uint32_t sum; int ok; } runs;
enum { M_RATE, M_NS_PER }; /* RATE: amount / best-run seconds.  NS_PER: best-run ns / count */
static void finish(const bench_host *h, bench_result *r, const char *name, runs *rs, double amount,
                   const char *unit, int mode, int suspect, const char *note) {
  double t[REPS];
  memcpy(t, rs->t, sizeof t);
  sort_d(t, REPS);
  memset(r, 0, sizeof *r);
  snprintf(r->name, sizeof r->name, "%s", name);
  r->min_ms = t[0] / 1000.0;
  r->med_ms = t[REPS / 2] / 1000.0;
  r->reps = REPS;
  r->checksum = rs->sum;
  r->suspect = suspect || !rs->ok;
  snprintf(r->note, sizeof r->note, "%s%s", note, rs->ok ? "" : " NONDETERMINISTIC");
  snprintf(r->unit, sizeof r->unit, "%s", unit);
  if (r->min_ms <= 0.0) r->metric = 0.0;
  else if (mode == M_RATE) r->metric = amount / (r->min_ms / 1000.0);
  else r->metric = (r->min_ms * 1e6) / amount;
  char line[220];
  snprintf(line, sizeof line, "BENCH %-18s min=%9.3f ms med=%9.3f ms  %10.3f %-7s sum=%08X%s  [%s]", r->name, r->min_ms,
           r->med_ms, r->metric, r->unit, (unsigned)r->checksum, r->suspect ? " SUSPECT" : "", r->note);
  h->log(line);
}

#define TIMED(rs, expr)                                                     \
  do {                                                                      \
    uint32_t _first = 0;                                                    \
    (rs).ok = 1;                                                            \
    (void)(expr); /* warm-up */                                             \
    for (int _i = 0; _i < REPS; _i++) {                                     \
      uint64_t _t0 = h->now_us();                                           \
      uint32_t _s = (expr);                                                 \
      (rs).t[_i] = (double)(h->now_us() - _t0);                             \
      if (_i == 0) _first = _s; else if (_s != _first) (rs).ok = 0;         \
    }                                                                       \
    (rs).sum = _first;                                                      \
  } while (0)

int bench_run_all(const bench_host *h, void *arena, size_t arena_bytes, bench_result *out, int max) {
  int n = 0;
  char note[64];
  int sus;
  runs rs;
  uint8_t *mem = (uint8_t *)arena;
  if (arena_bytes < BENCH_ARENA_BYTES || max < 10) { h->log("BENCH arena too small / out[] too small"); return 0; }

#ifdef PROBE_HAVE_STYLIZER_CPU
  { /* stylizer */
    sty_ctx c; sty_setup(&c, mem);
    sus = h->pre("stylizer", note, sizeof note);
    TIMED(rs, sty_run(&c));
    finish(h, &out[n++], "stylizer_960x544", &rs, (double)SW * SH / 1e6, "Mpix/s", M_RATE, sus, note);
  }
#else
  h->log("BENCH stylizer_960x544: the owner-local kernel (engine/Derived/vita-probe) is not built in -- skipped");
#endif
  { /* chase: L1-ish, L2-ish, DRAM */
    static const struct { const char *name; uint32_t bytes; uint32_t loads; } cs[3] = {
        {"chase_16KiB", 16u * 1024u, 4u << 20}, {"chase_256KiB", 256u * 1024u, 4u << 20}, {"chase_32MiB", 32u << 20, 2u << 20}};
    for (int k = 0; k < 3; k++) {
      uint32_t nodes = cs[k].bytes / (CHASE_STRIDE * 4u);
      uint32_t *arr = (uint32_t *)mem, *tmp = (uint32_t *)(mem + (32u << 20));
      chase_setup(arr, tmp, nodes);
      sus = h->pre(cs[k].name, note, sizeof note);
      /* +12345: load counts that are multiples of the cycle length always end at node 0 (checksum 0) */
      uint32_t loads = cs[k].loads + 12345u;
      TIMED(rs, chase_run(arr, loads));
      finish(h, &out[n++], cs[k].name, &rs, (double)loads, "ns/load", M_NS_PER, sus, note);
    }
  }
  { /* float */
    float *in = (float *)mem, *o = (float *)(mem + FV * 16), *m = (float *)(mem + FV * 32);
    float_setup(in, m);
    float m0[16]; memcpy(m0, m, sizeof m0);
    sus = h->pre("float_mat4", note, sizeof note);
    /* reset the matrix before every run so each run is identical */
    TIMED(rs, (memcpy(m, m0, sizeof m0), float_run(in, o, m)));
    finish(h, &out[n++], "float_mat4x64K", &rs, (double)FV * FREPS * 28.0 / 1e6, "Mflop/s", M_RATE, sus, note);
  }
  { /* bandwidth */
    uint8_t *src = mem, *dst = mem + BW_BYTES;
    uint32_t s = 99u;
    for (uint32_t i = 0; i < BW_BYTES / 4; i++) ((uint32_t *)src)[i] = xs32(&s);
    sus = h->pre("memcpy", note, sizeof note);
    TIMED(rs, copy_run(dst, src));
    finish(h, &out[n++], "memcpy_8MiB", &rs, (double)BW_BYTES * BW_REPS / 1048576.0, "MiB/s", M_RATE, sus, note);
    sus = h->pre("readsum", note, sizeof note);
    TIMED(rs, readsum_run((const uint32_t *)src));
    finish(h, &out[n++], "readsum_8MiB", &rs, (double)BW_BYTES * BW_REPS / 1048576.0, "MiB/s", M_RATE, sus, note);
  }
  { /* interpreter */
    vm_ins *code = (vm_ins *)mem;
    uint32_t iters = 2u << 20; /* 6 dispatches/iter -> ~12.6M dispatches */
    vm_build(code, iters);
    uint64_t ops = 0;
    vm_run(code, &ops);
    sus = h->pre("interp", note, sizeof note);
    TIMED(rs, vm_run(code, &ops));
    finish(h, &out[n++], "interp_switch", &rs, (double)ops, "ns/op", M_NS_PER, sus, note);
  }
  return n;
}
