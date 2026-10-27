/* pb_qemu.c -- runs PhysBench's synthetic scenes (libpbscene_vita.a + libshepphys_vita.a: the very objects the PhysBench
 * eboot links) on QEMU's emulated Cortex-A9 and prints each scene's pose hash, so build.sh can check offline that the
 * device's STEP hashes should equal the Mac reference's. Ours. Bare metal like vita/physics/qemu/harness.c (whose start.S
 * and link.ld it reuses unchanged): bump heap, semihosting output and exit, the few libc / libm routines defined here.
 * QEMU timing means nothing; only the hashes are used. */
#include <stddef.h>
#include <stdint.h>
#include <string.h>

#include "pb_scene.h"
#include "shepphys.h"

int harness_main(void);

static int semihost(int op, const void *arg)
{
    register int r0 __asm__("r0") = op;
    register const void *r1 __asm__("r1") = arg;
    __asm__ volatile("svc 0xab" : "+r"(r0) : "r"(r1) : "memory");
    return r0;
}
static void out(const char *s) { semihost(0x04, s); }
__attribute__((noreturn)) static void quit(int code)
{
    uint32_t blk[2] = {0x20026u, (uint32_t)code};
    semihost(0x20, blk);
    for (;;) {}
}

typedef struct { char b[256]; int n; } Line;
static void ls(Line *l, const char *s) { while (*s && l->n < (int)sizeof l->b - 1) l->b[l->n++] = *s++; l->b[l->n] = 0; }
static void lhex(Line *l, uint32_t v)
{
    char t[9];
    for (int i = 7; i >= 0; --i) { t[i] = "0123456789abcdef"[v & 15]; v >>= 4; }
    t[8] = 0;
    ls(l, t);
}
static void ldec(Line *l, int32_t v)
{
    char t[12];
    int i = 11;
    uint32_t u = v < 0 ? (uint32_t)(-(int64_t)v) : (uint32_t)v;
    t[i] = 0;
    do { t[--i] = (char)('0' + u % 10); u /= 10; } while (u);
    if (v < 0) t[--i] = '-';
    ls(l, t + i);
}
static void lend(Line *l) { ls(l, "\n"); out(l->b); l->n = 0; l->b[0] = 0; }

/* ---- libc / libm / C++ runtime pieces (no newlib: vitasdk's forwards to SceLibKernel) ---- */
extern char __heap_start[], __heap_end[];
static char *g_brk;
void *malloc(size_t n)
{
    n = (n + 15u) & ~(size_t)15u;
    if (!g_brk) g_brk = __heap_start;
    if (g_brk + n + 16 > __heap_end) { out("FAIL heap exhausted\n"); quit(3); }
    *(size_t *)g_brk = n;
    void *p = g_brk + 16;
    g_brk += n + 16;
    return p;
}
void free(void *p) { (void)p; }
void *memcpy(void *d, const void *s, size_t n) { char *a = d; const char *b = s; while (n--) *a++ = *b++; return d; }
void *realloc(void *p, size_t n)
{
    if (!p) return malloc(n);
    size_t old = *(size_t *)((char *)p - 16);
    if (n <= old) return p;
    void *q = malloc(n);
    memcpy(q, p, old);
    return q;
}
void abort(void) { out("FAIL abort\n"); quit(4); }
void *memmove(void *d, const void *s, size_t n)
{
    char *a = d;
    const char *b = s;
    if (a < b) while (n--) *a++ = *b++;
    else { a += n; b += n; while (n--) *--a = *--b; }
    return d;
}
void *memset(void *d, int c, size_t n) { unsigned char *a = d; while (n--) *a++ = (unsigned char)c; return d; }
int strcmp(const char *a, const char *b)
{
    while (*a && *a == *b) { ++a; ++b; }
    return (unsigned char)*a - (unsigned char)*b;
}
float sqrtf(float x) { float r; __asm__("vsqrt.f32 %0, %1" : "=t"(r) : "t"(x)); return r; }
float floorf(float x)
{
    if (!(x == x) || x >= 8388608.0f || x <= -8388608.0f) return x;
    float t = (float)(int32_t)x;
    return t > x ? t - 1.0f : t;
}
float atan2f(float y, float x) { (void)y; (void)x; out("FAIL atan2f\n"); quit(6); }
float expf(float x) { (void)x; out("FAIL expf\n"); quit(6); }
int vprintf(const char *f, __builtin_va_list ap) { (void)f; (void)ap; return 0; }
int *__errno(void) { static int e; return &e; }
/* physics Stage 0's trace writer (shim/sp_trace.cpp) uses stdio; bare metal has no files, so sp_trace_begin fails cleanly (fopen NULL) */
void *fopen(const char *p, const char *m) { (void)p; (void)m; return 0; }
size_t fwrite(const void *p, size_t s, size_t n, void *f) { (void)p; (void)s; (void)n; (void)f; return 0; }
int fclose(void *f) { (void)f; return 0; }
int setvbuf(void *f, char *b, int m, size_t n) { (void)f; (void)b; (void)m; (void)n; return 0; }
char *strncpy(char *d, const char *s, size_t n) { size_t i = 0; for (; i < n && s[i]; ++i) d[i] = s[i]; for (; i < n; ++i) d[i] = 0; return d; }
void _ZdlPv(void *p) { free(p); }
void _ZdlPvj(void *p, unsigned n) { (void)n; free(p); }
void __cxa_pure_virtual(void) { out("FAIL pure virtual\n"); quit(5); }

static uint32_t g_ttb[4096] __attribute__((aligned(16384)));
static void mmu_on(void)
{
    for (uint32_t i = 0; i < 4096; ++i)
        g_ttb[i] = (i << 20) | (1u << 12) | (3u << 10) | (1u << 3) | (1u << 2) | 2u;
    __asm__ volatile("mcr p15, 0, %0, c2, c0, 0\n\t"
                     "mcr p15, 0, %1, c2, c0, 2\n\t"
                     "mcr p15, 0, %2, c3, c0, 0\n\t"
                     "mcr p15, 0, %1, c8, c7, 0\n\t"
                     "dsb\n\tisb" ::"r"(g_ttb), "r"(0), "r"(0xFFFFFFFFu) : "memory");
    uint32_t sctlr;
    __asm__ volatile("mrc p15, 0, %0, c1, c0, 0" : "=r"(sctlr));
    sctlr |= 1u | (1u << 2) | (1u << 12);
    sctlr &= ~(1u << 1);
    __asm__ volatile("mcr p15, 0, %0, c1, c0, 0\n\tisb" ::"r"(sctlr) : "memory");
}
static uint32_t get_fpscr(void) { uint32_t v; __asm__ volatile("vmrs %0, fpscr" : "=r"(v)); return v; }
static void set_fpscr(uint32_t v) { __asm__ volatile("vmsr fpscr, %0" ::"r"(v) : "memory"); }

extern void (*__init_array_start[])(void);
extern void (*__init_array_end[])(void);

int harness_main(void)
{
    mmu_on();
    for (void (**p)(void) = __init_array_start; p < __init_array_end; ++p) (*p)();
    set_fpscr(get_fpscr() & ~((3u << 22) | (1u << 24) | (1u << 25)));   /* IEEE: RN, FZ=0, DN=0 */
    Line l = {{0}, 0};
    ls(&l, "QEMU physbench scenes on emulated Cortex-A9: "); ls(&l, sp_build_info()); lend(&l);
    int fails = 0;
    for (int32_t id = 0; id < PB_SC_N; ++id) {
        uint64_t h = 0;
        sp_step_stats st;
        int32_t r = pb_scene_run_hash(id, PB_WARMUP_STEPS + PB_MEASURE_STEPS, &h, &st);
        fails += r != 0;
        ls(&l, "QEMU SCENE "); ls(&l, pb_scene_name(id)); ls(&l, " hash "); lhex(&l, (uint32_t)(h >> 32)); lhex(&l, (uint32_t)h);
        ls(&l, " status "); ldec(&l, r); ls(&l, " bodies "); ldec(&l, st.bodies); ls(&l, " contacts "); ldec(&l, st.contacts);
        lend(&l);
    }
    out(fails ? "QEMU RESULT FAIL\n" : "QEMU RESULT DONE\n");
    quit(fails ? 1 : 0);
    return 0;
}
