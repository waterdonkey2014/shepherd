/* sp_trig.c -- deterministic sinf / cosf for the Shepherd physics core (ours; not Box2D code).
 *
 * Why: Box2D 2.3.1 calls sinf/cosf in b2Rot::Set (every rotating body, every step). Apple's libm and the Vita's newlib give
 * different last bits for some inputs, which would break the Mac == Vita bit-exact contract (physics-plan.md section 1.9,
 * patch 5). These versions use only IEEE double + - * and conversions, in a fixed order, so every IEEE-conforming FPU gives
 * the same result: arm64 (Mac), x86_64 SSE2, and the Vita's ARMv7 VFPv3 (scalar double; no NEON, no FMA with
 * -ffp-contract=off). They are not libm: no errno, no tables.
 *
 * Method: x is widened to double; k = round(x * 2/pi); r = (x - k*PIO2_1) - k*PIO2_1T (Cody-Waite, PIO2_1 has 33 bits so
 * k*PIO2_1 is exact for |k| < 2^20, i.e. |x| < 1.6e6); then Taylor polynomials of degree 15 (sin) / 16 (cos) on |r| <= pi/4
 * in double (truncation < 5e-17), and one final rounding to float. Results are within 0.5 ulp + 1e-16 relative of the true
 * value, so they equal the correctly rounded float except on rare near-ties; that is fine: the point is determinism.
 * Larger |x| keeps the same arithmetic (deterministic, less accurate); non-finite x gives NaN; |x| >= 2^62 gives NaN.
 * Must be compiled with -ffp-contract=off (CMakeLists.txt does) so no compiler fuses a*b+c.
 */
#include <stdint.h>
#include "shepphys.h"

/* constants as exact hex literals (C99), so no decimal-to-binary conversion can differ between compilers */
#define INV_PIO2  0x1.45f306dc9c883p-1     /* 2/pi */
#define PIO2_1    0x1.921fb54400000p+0     /* first 33 bits of pi/2 */
#define PIO2_1T   0x1.0b4611a626331p-34    /* pi/2 - PIO2_1 */

#define S1 (-0x1.5555555555555p-3)         /* -1/3! */
#define S2 ( 0x1.1111111111111p-7)         /*  1/5! */
#define S3 (-0x1.a01a01a01a01ap-13)        /* -1/7! */
#define S4 ( 0x1.71de3a556c734p-19)        /*  1/9! */
#define S5 (-0x1.ae64567f544e4p-26)        /* -1/11! */
#define S6 ( 0x1.6124613a86d09p-33)        /*  1/13! */
#define S7 (-0x1.ae7f3e733b81fp-41)        /* -1/15! */

#define C1 (-0x1.0000000000000p-1)         /* -1/2! */
#define C2 ( 0x1.5555555555555p-5)         /*  1/4! */
#define C3 (-0x1.6c16c16c16c17p-10)        /* -1/6! */
#define C4 ( 0x1.a01a01a01a01ap-16)        /*  1/8! */
#define C5 (-0x1.27e4fb7789f5cp-22)        /* -1/10! */
#define C6 ( 0x1.1eed8eff8d898p-29)        /*  1/12! */
#define C7 (-0x1.93974a8c07c9dp-37)        /* -1/14! */
#define C8 ( 0x1.ae7f3e733b81fp-45)        /*  1/16! */

static double k_sin(double r)
{
    double z = r * r;
    double p = S7;
    p = p * z + S6;
    p = p * z + S5;
    p = p * z + S4;
    p = p * z + S3;
    p = p * z + S2;
    p = p * z + S1;
    return r + (r * z) * p;
}

static double k_cos(double r)
{
    double z = r * r;
    double p = C8;
    p = p * z + C7;
    p = p * z + C6;
    p = p * z + C5;
    p = p * z + C4;
    p = p * z + C3;
    p = p * z + C2;
    p = p * z + C1;
    return 1.0 + z * p;
}

/* returns the quadrant (k mod 4) and the reduced argument, or -1 for NaN / infinite / huge input */
static int reduce(float x, double *r)
{
    double d = (double)x;
    double ad = d < 0.0 ? -d : d;
    if (!(ad < 0x1p62)) return -1;                  /* NaN, +-inf, absurdly large */
    if (ad <= 0x1.921fb54442d18p-1) {               /* |x| <= pi/4: no reduction (keeps -0.0 and tiny x exact) */
        *r = d;
        return 0;
    }
    double t = d * INV_PIO2;
    int64_t k = (int64_t)(t >= 0.0 ? t + 0.5 : t - 0.5);
    double kd = (double)k;
    *r = (d - kd * PIO2_1) - kd * PIO2_1T;
    return (int)(k & 3);
}

float sp_sinf(float x)
{
    double r;
    switch (reduce(x, &r)) {
    case 0: return (float)k_sin(r);
    case 1: return (float)k_cos(r);
    case 2: return (float)-k_sin(r);
    case 3: return (float)-k_cos(r);
    default: return (x - x) / (x - x);              /* NaN */
    }
}

float sp_cosf(float x)
{
    double r;
    switch (reduce(x, &r)) {
    case 0: return (float)k_cos(r);
    case 1: return (float)-k_sin(r);
    case 2: return (float)-k_cos(r);
    case 3: return (float)k_sin(r);
    default: return (x - x) / (x - x);
    }
}

/* ---- Stage 1: sp_atan2f, for the 2D angle of a transform (atan2(m10, m00) of its world matrix; shim/sp_unity.cpp). Same rules as
 * above: IEEE double + - * / and sqrt (correctly rounded everywhere), a fixed order, one final rounding to float.
 * Method: t = min(|y|, |x|) / max(|y|, |x|) in [0, 1]; one half-angle step v = t / (1 + sqrt(1 + t*t)) (|v| <= tan(pi/8) = 0.4142),
 * atan(v) by its series to v^47 (truncation < 1e-18 relative), doubled; then the octant / quadrant fold and the sign of y
 * (copysign semantics: atan2(-0, +x) = -0). atan2(0, x > 0) = +0 exactly; atan2(0, 0) = 0; NaN in -> NaN. */
#define PI_D   0x1.921fb54442d18p+1
#define PIO2_D 0x1.921fb54442d18p+0
static double k_atan01(double t)   /* 0 <= t <= 1 */
{
    double v = t / (1.0 + __builtin_sqrt(1.0 + t * t));
    double z = v * v;
    double p = 1.0 / 47.0;
    for (int k = 22; k >= 0; --k) p = 1.0 / (double)(2 * k + 1) - z * p;   /* p = sum_k (-z)^k / (2k+1), Horner in -z */
    return 2.0 * (v * p);
}
float sp_atan2f(float y, float x)
{
    if (y != y || x != x) return y + x;   /* NaN */
    union { float f; uint32_t u; } sx = {x}, sy = {y}, fy, fx;
    fy.u = sy.u & 0x7FFFFFFFu; fx.u = sx.u & 0x7FFFFFFFu;   /* |y|, |x| (+0 for -0) */
    double ay = (double)fy.f, ax = (double)fx.f;
    double r;
    if (ay == 0.0 && ax == 0.0) r = 0.0;
    else if (ay <= ax) r = k_atan01(ay / ax);
    else r = PIO2_D - k_atan01(ax / ay);
    if (sx.u >> 31) r = PI_D - r;
    float f = (float)r;
    return (sy.u >> 31) ? -f : f;
}
