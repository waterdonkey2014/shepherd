/* sp_t1.h -- T1 determinism scenarios for the Shepherd physics core (ours).
 *
 * Each scenario builds a small synthetic world through the shepphys C API only, steps it at the game's fixed step
 * (0.02 s, 8 velocity / 3 position iterations) and emits records: tag, two ints, and up to 24 32-bit words (float bit
 * patterns or ints). Every record is folded into an FNV-1a 64-bit hash, so two machines agree on a scenario iff their
 * hashes agree. The same code is compiled into the Mac dylib and the Vita static library; the goldens
 * (tests/golden/<name>.txt, tests/golden/t1_golden.h) are recorded on the Mac and compiled into both, so a device run can
 * check itself bit for bit (sp_t1_expected) and dump its records for a diff when it does not match.
 * No libc formatting, no libm: scenario maths uses + - * / sqrtf (correctly rounded everywhere) and sp_sinf / sp_cosf.
 */
#ifndef SP_T1_H
#define SP_T1_H

#include <stdint.h>
#include "shepphys.h"

#ifdef __cplusplus
extern "C" {
#endif

#define SP_T1_TAG(a, b, c, d) ((uint32_t)(a) | ((uint32_t)(b) << 8) | ((uint32_t)(c) << 16) | ((uint32_t)(d) << 24))
#define SP_T1_MAX_WORDS 24

/* one record: float_mask bit i set = words[i] is a float bit pattern (for printing only; the hash is over raw words) */
typedef void (*sp_t1_record_fn)(void *user, uint32_t tag, int32_t a, int32_t b, const uint32_t *words, int32_t n,
                                uint32_t float_mask);

typedef struct sp_t1_result {
    uint32_t hash_lo, hash_hi;      /* FNV-1a 64 over every record (tag, a, b, n, words), little-endian bytes */
    int32_t records, words, steps;
    int32_t status;                 /* 0, or the first negative shepphys error a scenario hit */
} sp_t1_result;

SP_API int32_t sp_t1_count(void);
SP_API const char *sp_t1_name(int32_t i);
SP_API int32_t sp_t1_run(int32_t i, sp_t1_record_fn fn, void *user, sp_t1_result *out);   /* returns out->status */
/* golden values compiled in from tests/golden/t1_golden.h: 1 if scenario i has a golden, else 0 */
SP_API int32_t sp_t1_expected(int32_t i, sp_t1_result *out);
SP_API const char *sp_t1_golden_info(void);   /* how and where the compiled-in goldens were recorded */
/* Stage 0 self-test (tests/sp_s0.c): runtime + pool and the settings check, reported the T1 way (the Mac and QEMU must agree) */
SP_API int32_t sp_s0_run(sp_t1_record_fn fn, void *user, sp_t1_result *out);
/* Stage 1 self-test (tests/sp_s1.cpp): the Unity layer, reported the same way (the Mac and QEMU must agree) */
SP_API int32_t sp_s1_run(sp_t1_record_fn fn, void *user, sp_t1_result *out);
/* Stage 2 self-test (tests/sp_s2.cpp): patch P1's probes, contact serials, the Unity queries (the Mac and QEMU must agree) */
SP_API int32_t sp_s2_run(sp_t1_record_fn fn, void *user, sp_t1_result *out);
/* Stage 3 self-test (tests/sp_s3.cpp): forces, impulses, damping variants, restitution, the clamp, frozen axes, one-sided chains, contact
 * snapshots (the Mac and QEMU must agree) */
SP_API int32_t sp_s3_run(sp_t1_record_fn fn, void *user, sp_t1_result *out);
/* Stage 6 self-test (tests/sp_s6.cpp): the 3D island-ground ray -- planes, the tilted Unity plane, culling, primitives, filters, the
 * closest / all / any modes, the +z fast path against the general path and the brute force (the Mac and QEMU must agree) */
SP_API int32_t sp_s6_run(sp_t1_record_fn fn, void *user, sp_t1_result *out);
SP_API void *shepphys_t1_proc(const char *name);

#ifdef __cplusplus
}
#endif
#endif
