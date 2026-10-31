/* harness.c -- runs the T1 scenarios from libshepphys_vita.a (the very archive the Vita links: same gcc, same flags,
 * same objects) on QEMU's emulated Cortex-A9 (vexpress-a9: ARMv7-A, VFPv3-D32 + NEON, IEEE-exact softfloat with ARM
 * FPSCR semantics). Ours. It is the closest offline stand-in for the Vita's CPU; it is NOT the device: the Vita's
 * default FPSCR and any silicon quirk are only settled by the device run (vita/t1_main.c).
 * Bare metal: start.S -> harness_main(); identity MMU map (normal memory, so gcc's unaligned accesses are legal);
 * a bump heap for Box2D's b2Alloc; output and exit code through ARM semihosting (SVC 0xAB in Thumb state).
 * No newlib at all (vitasdk's newlib forwards memcpy & co. to SceLibKernel): the few libc / libm routines Box2D and the
 * shim need are defined below; only libgcc.a (EABI helpers) is linked. sqrtf is the VFP vsqrt (IEEE, as on the device).
 * Prints one line per scenario and pass; on a mismatch in pass A it prints every record as "DUMP <name> TAG a b | hex |"
 * so build.sh can diff it against tests/golden/<name>.txt.
 */
#include <stddef.h>
#include <stdint.h>
#include <string.h>

#include "shepphys.h"
#include "sp_t1.h"

int harness_main(void);

/* ---- semihosting ---- */
static int semihost(int op, const void *arg)
{
    register int r0 __asm__("r0") = op;
    register const void *r1 __asm__("r1") = arg;
    __asm__ volatile("svc 0xab" : "+r"(r0) : "r"(r1) : "memory");
    return r0;
}
static void out(const char *s) { semihost(0x04, s); }                                 /* SYS_WRITE0 */
__attribute__((noreturn)) static void quit(int code)
{
    uint32_t blk[2] = {0x20026u, (uint32_t)code};                                       /* ADP_Stopped_ApplicationExit */
    semihost(0x20, blk);                                                                /* SYS_EXIT_EXTENDED */
    for (;;) {}
}

/* ---- tiny formatting into a line buffer ---- */
typedef struct { char b[512]; int n; } Line;
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

/* ---- heap (bump; free is a no-op: every scenario world is small and the run is short) ---- */
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
void *calloc(size_t a, size_t b)
{
    void *p = malloc(a * b);
    memset(p, 0, a * b);
    return p;
}
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
void *memcpy(void *d, const void *s, size_t n) { char *a = d; const char *b = s; while (n--) *a++ = *b++; return d; }
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
float floorf(float x)          /* used only by Box2D's TOI path (continuous physics is off) */
{
    if (!(x == x) || x >= 8388608.0f || x <= -8388608.0f) return x;
    float t = (float)(int32_t)x;
    return t > x ? t - 1.0f : t;
}
/* referenced by b2Rope / b2Log only (never called here) */
float atan2f(float y, float x) { (void)y; (void)x; out("FAIL atan2f\n"); quit(6); }
float expf(float x) { (void)x; out("FAIL expf\n"); quit(6); }
int vprintf(const char *f, __builtin_va_list ap) { (void)f; (void)ap; return 0; }
int *__errno(void) { static int e; return &e; }   /* for newlib's libm in the libm-trig risk probe (build.sh verify) */
/* Stage 0's trace writer (shim/sp_trace.cpp) uses stdio; bare metal has no files, so sp_trace_begin fails cleanly here (fopen NULL) */
void *fopen(const char *p, const char *m) { (void)p; (void)m; return 0; }
size_t fwrite(const void *p, size_t s, size_t n, void *f) { (void)p; (void)s; (void)n; (void)f; return 0; }
int fclose(void *f) { (void)f; return 0; }
int setvbuf(void *f, char *b, int m, size_t n) { (void)f; (void)b; (void)m; (void)n; return 0; }
char *strncpy(char *d, const char *s, size_t n) { size_t i = 0; for (; i < n && s[i]; ++i) d[i] = s[i]; for (; i < n; ++i) d[i] = 0; return d; }
/* the C++ runtime pieces Box2D's vtables reference (deleting destructors, pure virtuals); never called here */
void _ZdlPv(void *p) { free(p); }
void _ZdlPvj(void *p, unsigned n) { (void)n; free(p); }
void __cxa_pure_virtual(void) { out("FAIL pure virtual\n"); quit(5); }

/* ---- CPU setup ---- */
static uint32_t g_ttb[4096] __attribute__((aligned(16384)));
static void mmu_on(void)
{
    for (uint32_t i = 0; i < 4096; ++i)   /* 1 MiB sections, identity: TEX=001 C=1 B=1 (normal WB-WA), AP=11 */
        g_ttb[i] = (i << 20) | (1u << 12) | (3u << 10) | (1u << 3) | (1u << 2) | 2u;
    __asm__ volatile("mcr p15, 0, %0, c2, c0, 0\n\t"   /* TTBR0 */
                     "mcr p15, 0, %1, c2, c0, 2\n\t"   /* TTBCR = 0 */
                     "mcr p15, 0, %2, c3, c0, 0\n\t"   /* DACR: all domains manager */
                     "mcr p15, 0, %1, c8, c7, 0\n\t"   /* TLBIALL */
                     "dsb\n\tisb" ::"r"(g_ttb), "r"(0), "r"(0xFFFFFFFFu) : "memory");
    uint32_t sctlr;
    __asm__ volatile("mrc p15, 0, %0, c1, c0, 0" : "=r"(sctlr));
    sctlr |= 1u | (1u << 2) | (1u << 12);              /* M, C, I */
    sctlr &= ~(1u << 1);                               /* A = 0: no alignment faults */
    __asm__ volatile("mcr p15, 0, %0, c1, c0, 0\n\tisb" ::"r"(sctlr) : "memory");
}
static uint32_t get_fpscr(void) { uint32_t v; __asm__ volatile("vmrs %0, fpscr" : "=r"(v)); return v; }
static void set_fpscr(uint32_t v) { __asm__ volatile("vmsr fpscr, %0" ::"r"(v) : "memory"); }

extern void (*__init_array_start[])(void);
extern void (*__init_array_end[])(void);

/* ---- record dump ---- */
static const char *g_dump_name;
static void on_rec(void *user, uint32_t tag, int32_t a, int32_t b, const uint32_t *w, int32_t n, uint32_t fmask)
{
    (void)user; (void)fmask;
    Line l = {{0}, 0};
    char t[5] = {(char)(tag & 0xFF), (char)((tag >> 8) & 0xFF), (char)((tag >> 16) & 0xFF), (char)(tag >> 24), 0};
    ls(&l, "DUMP "); ls(&l, g_dump_name); ls(&l, " "); ls(&l, t); ls(&l, " "); ldec(&l, a); ls(&l, " "); ldec(&l, b); ls(&l, " |");
    for (int32_t i = 0; i < n; ++i) { ls(&l, " "); lhex(&l, w[i]); }
    ls(&l, " |");
    lend(&l);
}

static int run_pass(char pass, int dump)
{
    int32_t n = sp_t1_count(), ok = 0;
    Line l = {{0}, 0};
    for (int32_t i = 0; i < n; ++i) {
        sp_t1_result r, e;
        sp_t1_run(i, NULL, NULL, &r);
        int g = sp_t1_expected(i, &e);
        int match = g && e.hash_lo == r.hash_lo && e.hash_hi == r.hash_hi && r.status == 0;
        ok += match;
        char ps[2] = {pass, 0};
        ls(&l, "QEMU "); ls(&l, ps); ls(&l, " "); ls(&l, sp_t1_name(i));
        ls(&l, match ? " OK hash " : " MISMATCH hash "); lhex(&l, r.hash_hi); lhex(&l, r.hash_lo);
        ls(&l, " golden "); lhex(&l, e.hash_hi); lhex(&l, e.hash_lo);
        ls(&l, " records "); ldec(&l, r.records); ls(&l, " status "); ldec(&l, r.status);
        lend(&l);
        if (!match && dump) { g_dump_name = sp_t1_name(i); sp_t1_run(i, on_rec, NULL, &r); }
    }
    char ps[2] = {pass, 0};
    ls(&l, "QEMU "); ls(&l, ps); ls(&l, " SUMMARY "); ldec(&l, ok); ls(&l, "/"); ldec(&l, n);
    ls(&l, " fpscr "); lhex(&l, get_fpscr());
    lend(&l);
    return ok == n;
}

uint64_t sp_tr_hash(const void *p, uint32_t n);    /* shim/sp_trace.cpp (exported) */
uint64_t sp_tr_ray3d_hash(const sp_ray3d_hit *h, int32_t n);   /* shim/sp_mesh3d.cpp (exported): Stage 6's RAY3D record hash */
uint64_t sp_tr_pose_hash(int32_t world);

/* ---- Stage 1: an engine run's trace (SHEPHERD_PHYS_TRACE) replayed on the emulated Cortex-A9 (build.sh qemu-replay): the file
 * "replay.sptr" in QEMU's working directory, read through semihosting; every recorded core call re-issued against this archive, every
 * returned handle and result, every step's pose hash and every event drain's hash compared. Run first, in a fresh process, so the
 * handles come out as recorded (allocation is deterministic). The plan's section 1.10: "Mac hash must equal Vita hash ... recorded
 * traces from Stage 1". Record layout: shim/sp_trace.h. ---- */
static int replay_trace(void)
{
    static const char name[] = "replay.sptr";
    uint32_t op[3] = {(uint32_t)(uintptr_t)name, 0 /* "r" */, sizeof name - 1};
    int fh = semihost(0x01, op);                                                      /* SYS_OPEN */
    if (fh < 0) return -1;
    uint32_t fl[1] = {(uint32_t)fh};
    int len = semihost(0x0C, fl);                                                      /* SYS_FLEN */
    uint8_t *buf = (uint8_t *)malloc((size_t)len + 16);
    uint32_t rd[3] = {(uint32_t)fh, (uint32_t)(uintptr_t)buf, (uint32_t)len};
    int left = semihost(0x06, rd);                                                     /* SYS_READ: bytes NOT read */
    semihost(0x02, fl);                                                                /* SYS_CLOSE */
    Line l = {{0}, 0};
    const uint32_t *h = (const uint32_t *)buf;
    if (left != 0 || len < 128 || h[0] != 0x52545053u || h[2] != SHEPPHYS_ABI_VERSION) {
        ls(&l, "QEMU REPLAY FAILED: not a readable ABI-"); ldec(&l, SHEPPHYS_ABI_VERSION); ls(&l, " trace"); lend(&l); return 1;
    }
    long recs = 0, steps = 0, notes = 0, mism = 0;
    long first = -1; const char *what = "";
    int at = 128;
    sp_event ev[256];
    static sp_event2 ev2[128];
    static sp_ray3d_hit rh[64];
    long rays = 0;
#define MIS(w) do { if (first < 0) { first = recs; what = (w); } mism++; } while (0)
    while (at + 8 <= len) {
        uint32_t opc = *(const uint32_t *)(buf + at), n = *(const uint32_t *)(buf + at + 4);
        const int32_t *a = (const int32_t *)(buf + at + 8);
        at += 8 + (int)n;
        recs++;
        switch (opc) {
        case 1: { int32_t r = sp_world_create(a[1] ? (const sp_world_def *)(a + 2) : 0); if (r != a[0]) MIS("world_create"); break; }
        case 2: { if (sp_world_destroy(a[0]) != a[1]) MIS("world_destroy"); break; }
        case 3: {
            int32_t r = sp_world_step(a[0], a[2] ? (const sp_step_def *)(a + 5) : 0, 0);
            if (r != a[1]) MIS("world_step");
            uint64_t want = ((uint64_t)(uint32_t)a[4] << 32) | (uint32_t)a[3], got = r == SP_OK ? sp_tr_pose_hash(a[0]) : 0;
            if (want != got) MIS("pose hash");
            steps++;
            break; }
        case 4: { if (sp_world_set_gravity(a[0], a[2] ? (const float *)(a + 3) : 0) != a[1]) MIS("set_gravity"); break; }
        case 32: { if (sp_world_set_clamps(a[0], a[2] ? (const float *)(a + 3) : 0) != a[1]) MIS("set_clamps"); break; }   /* E1: patch P2 */
        case 5: { if (sp_body_create(a[0], a[2] ? (const sp_body_def *)(a + 3) : 0) != a[1]) MIS("body_create"); break; }
        case 6: { if (sp_body_destroy(a[0], a[1]) != a[2]) MIS("body_destroy"); break; }
        case 7: { if (sp_body_set_transform(a[0], a[1], a[3] ? (const float *)(a + 4) : 0) != a[2]) MIS("set_transform"); break; }
        case 8: { if (sp_body_set_velocity(a[0], a[1], a[3] ? (const float *)(a + 4) : 0) != a[2]) MIS("set_velocity"); break; }
        case 9: { if (sp_body_apply(a[0], a[1], a[2], a[5] ? (const float *)(a + 6) : 0, a[3]) != a[4]) MIS("body_apply"); break; }
        case 10: { if (sp_body_set_type(a[0], a[1], a[2]) != a[3]) MIS("set_type"); break; }
        case 11: { if (sp_body_set_flag(a[0], a[1], a[2], a[3]) != a[4]) MIS("set_flag"); break; }
        case 12: { if (sp_body_set_float(a[0], a[1], a[2], a[4] ? (const float *)(a + 5) : 0) != a[3]) MIS("set_float"); break; }
        case 13: {
            const sp_shape_def *d = a[3] ? (const sp_shape_def *)(a + 6) : 0;
            const float *xy = a[4] ? (const float *)((const uint8_t *)(a + 6) + sizeof(sp_shape_def)) : 0;
            if (sp_shape_create(a[0], a[1], d, xy) != a[2]) MIS("shape_create");
            break; }
        case 14: { if (sp_shape_destroy(a[0], a[1]) != a[2]) MIS("shape_destroy"); break; }
        case 15: { if (sp_shape_set_sensor(a[0], a[1], a[2]) != a[3]) MIS("shape_set_sensor"); break; }
        case 16: { if (sp_shape_set_filter(a[0], a[1], a[3] ? (const uint32_t *)(a + 4) : 0) != a[2]) MIS("shape_set_filter"); break; }
        case 17: {
            int32_t mx = a[1] > 256 ? 256 : a[1];
            int32_t r = sp_events(a[0], a[3] ? ev : 0, mx);
            if (r != a[2]) MIS("events");
            uint64_t want = ((uint64_t)(uint32_t)a[5] << 32) | (uint32_t)a[4], got = sp_tr_hash(ev, r > 0 ? (uint32_t)r * (uint32_t)sizeof(sp_event) : 0);
            if (want != got) MIS("event hash");
            break; }
        case 18: notes++; break;
        case 19: { if (sp_world_set_matrix(a[0], a[2] ? (const uint32_t *)(a + 3) : 0) != a[1]) MIS("set_matrix"); break; }
        case 20: { if (sp_shape_set_unity(a[0], a[1], a[3] ? (const sp_shape_unity *)(a + 4) : 0) != a[2]) MIS("shape_set_unity"); break; }
        case 21: { if (sp_world_ignore_pair(a[0], a[1], a[2], a[3]) != a[4]) MIS("ignore_pair"); break; }
        case 22: { if (sp_body_set_mass(a[0], a[1], a[3] ? (const float *)(a + 4) : 0) != a[2]) MIS("set_mass"); break; }
        case 23: {   /* Stage 2: the events drain with contact serials */
            int32_t mx = a[1] > 128 ? 128 : a[1];
            int32_t r = sp_events2(a[0], a[3] ? ev2 : 0, mx);
            if (r != a[2]) MIS("events2");
            uint64_t want = ((uint64_t)(uint32_t)a[5] << 32) | (uint32_t)a[4], got = sp_tr_hash(ev2, r > 0 ? (uint32_t)r * (uint32_t)sizeof(sp_event2) : 0);
            if (want != got) MIS("event2 hash");
            break; }
        /* Stage 6: the 3D scene (handles as recorded: a fresh process allocates them identically) */
        case 24: { if (sp_scene3d_create(a[1]) != a[0]) MIS("scene3d_create"); break; }
        case 25: { if (sp_scene3d_destroy(a[0]) != a[1]) MIS("scene3d_destroy"); break; }
        case 26: {
            const uint8_t *p = (const uint8_t *)(a + 5);
            const sp_mesh3d_def *d = a[2] ? (const sp_mesh3d_def *)p : 0;
            if (d) p += sizeof *d;
            const float *xyz = a[3] ? (const float *)p : 0;
            if (a[3] && d) p += (uint32_t)d->vertex_count * 12u;
            const int32_t *tri = a[4] ? (const int32_t *)p : 0;
            if (sp_mesh3d_create(a[0], d, xyz, tri) != a[1]) MIS("mesh3d_create");
            break; }
        case 27: { if (sp_mesh3d_destroy(a[0], a[1]) != a[2]) MIS("mesh3d_destroy"); break; }
        case 28: { if (sp_col3d_create(a[0], a[2] ? (const sp_col3d_def *)(a + 3) : 0) != a[1]) MIS("col3d_create"); break; }
        case 29: { if (sp_col3d_set(a[0], a[1], a[3] ? (const sp_col3d_def *)(a + 4) : 0) != a[2]) MIS("col3d_set"); break; }
        case 30: { if (sp_col3d_destroy(a[0], a[1]) != a[2]) MIS("col3d_destroy"); break; }
        case 31: {
            int32_t mx = a[1] > 64 ? 64 : a[1];
            int32_t r = sp_ray3d(a[0], a[3] ? (const sp_ray3d_query *)(a + 6) : 0, rh, mx);
            if (r != a[2]) MIS("ray3d");
            int32_t w = r > 0 ? (r < a[1] ? r : a[1]) : 0;
            if (w > mx) w = mx;
            uint64_t want = ((uint64_t)(uint32_t)a[5] << 32) | (uint32_t)a[4], got = sp_tr_ray3d_hash(rh, w);
            if (a[1] <= 64 && want != got) MIS("ray3d hit hash");
            rays++;
            break; }
        default: MIS("unknown op"); break;
        }
    }
#undef MIS
    ls(&l, mism == 0 ? "QEMU REPLAY OK: " : "QEMU REPLAY FAILED: "); ldec(&l, (int32_t)recs); ls(&l, " records, "); ldec(&l, (int32_t)steps);
    ls(&l, " steps (pose hash after every one), "); ldec(&l, (int32_t)notes); ls(&l, " notes, "); ldec(&l, (int32_t)rays); ls(&l, " 3D rays, ");
    ldec(&l, (int32_t)mism); ls(&l, " mismatches");
    if (mism) { ls(&l, "; first at record "); ldec(&l, (int32_t)first); ls(&l, ": "); ls(&l, what); }
    lend(&l);
    return mism ? 1 : 0;
}

int harness_main(void)
{
    uint32_t fpscr0 = get_fpscr();
    mmu_on();
    for (void (**p)(void) = __init_array_start; p < __init_array_end; ++p) (*p)();
    {   /* Stage 1: a trace to replay first (fresh state), then nothing else */
        set_fpscr(fpscr0 & ~((3u << 22) | (1u << 24) | (1u << 25)));   /* IEEE: RN, FZ=0, DN=0 (as sp_init sets it on the device) */
        int rr = replay_trace();
        if (rr >= 0) quit(rr);
        set_fpscr(fpscr0);
    }
    Line l = {{0}, 0};
    ls(&l, "QEMU shepphys T1 on emulated Cortex-A9 (vexpress-a9): "); ls(&l, sp_build_info()); lend(&l);
    ls(&l, "QEMU golden: "); ls(&l, sp_t1_golden_info()); lend(&l);
    int32_t sizes[SP_ABI_N_SIZES];
    float probe = 0.0f;
    sp_abi(sizes, SP_ABI_N_SIZES, &probe);
    uint32_t pb;
    memcpy(&pb, &probe, 4);
    ls(&l, "QEMU abi sizes");
    for (int i = 0; i < SP_ABI_N_SIZES; ++i) { ls(&l, " "); ldec(&l, sizes[i]); }
    ls(&l, " probe "); lhex(&l, pb); ls(&l, pb == 0x424f0000u ? " ok" : " BAD"); lend(&l);
    float s1 = sp_sinf(1.0f), c1 = sp_cosf(1.0f);
    uint32_t sb, cb;
    memcpy(&sb, &s1, 4);
    memcpy(&cb, &c1, 4);
    ls(&l, "QEMU fpscr at entry "); lhex(&l, fpscr0); ls(&l, " sp_sinf(1) "); lhex(&l, sb); ls(&l, " sp_cosf(1) "); lhex(&l, cb);
    lend(&l);
    set_fpscr(fpscr0 & ~((3u << 22) | (1u << 24) | (1u << 25)));       /* IEEE: RN, FZ=0, DN=0 */
    int a = run_pass('A', 1);
    {   /* Stage 0 self-test (tests/sp_s0.c): build.sh qemu compares this hash with the Mac's t1_runner s0 */
        sp_t1_result r;
        int32_t st = sp_s0_run(NULL, NULL, &r);
        ls(&l, st == 0 ? "QEMU S0 RESULT PASS hash " : "QEMU S0 RESULT FAIL hash "); lhex(&l, r.hash_hi); lhex(&l, r.hash_lo);
        ls(&l, " records "); ldec(&l, r.records); ls(&l, " status "); ldec(&l, st); lend(&l);
        if (st != 0) a = 0;
    }
    {   /* Stage 1 self-test (tests/sp_s1.cpp, the Unity layer): build.sh qemu compares this hash with the Mac's t1_runner s1 */
        sp_t1_result r;
        int32_t st = sp_s1_run(NULL, NULL, &r);
        ls(&l, st == 0 ? "QEMU S1 RESULT PASS hash " : "QEMU S1 RESULT FAIL hash "); lhex(&l, r.hash_hi); lhex(&l, r.hash_lo);
        ls(&l, " records "); ldec(&l, r.records); ls(&l, " status "); ldec(&l, st); lend(&l);
        if (st != 0) a = 0;
    }
    {   /* Stage 2 self-test (tests/sp_s2.cpp: P1, contact serials, the Unity queries): build.sh qemu compares this hash with the Mac's t1_runner s2 */
        sp_t1_result r;
        int32_t st = sp_s2_run(NULL, NULL, &r);
        ls(&l, st == 0 ? "QEMU S2 RESULT PASS hash " : "QEMU S2 RESULT FAIL hash "); lhex(&l, r.hash_hi); lhex(&l, r.hash_lo);
        ls(&l, " records "); ldec(&l, r.records); ls(&l, " status "); ldec(&l, st); lend(&l);
        if (st != 0) a = 0;
    }
    {   /* Stage 3 self-test (tests/sp_s3.cpp: forces, damping, restitution, clamp, frozen axes, one-sided chains, contacts): vs the Mac's t1_runner s3 */
        sp_t1_result r;
        int32_t st = sp_s3_run(NULL, NULL, &r);
        ls(&l, st == 0 ? "QEMU S3 RESULT PASS hash " : "QEMU S3 RESULT FAIL hash "); lhex(&l, r.hash_hi); lhex(&l, r.hash_lo);
        ls(&l, " records "); ldec(&l, r.records); ls(&l, " status "); ldec(&l, st); lend(&l);
        if (st != 0) a = 0;
    }
    {   /* Stage 6 self-test (tests/sp_s6.cpp: the 3D island-ground ray, the +z fast path = the general path): vs the Mac's t1_runner s6 */
        sp_t1_result r;
        int32_t st = sp_s6_run(NULL, NULL, &r);
        ls(&l, st == 0 ? "QEMU S6 RESULT PASS hash " : "QEMU S6 RESULT FAIL hash "); lhex(&l, r.hash_hi); lhex(&l, r.hash_lo);
        ls(&l, " records "); ldec(&l, r.records); ls(&l, " status "); ldec(&l, st); lend(&l);
        if (st != 0) a = 0;
    }
    {   /* Stage 7 item 2 self-test (tests/sp_s7.cpp, kit #9: patch P3's worlds, serial here -- no threads): vs the Mac's t1_runner s7 */
        sp_t1_result r;
        int32_t st = sp_s7_run(NULL, NULL, &r);
        ls(&l, st == 0 ? "QEMU S7 RESULT PASS hash " : "QEMU S7 RESULT FAIL hash "); lhex(&l, r.hash_hi); lhex(&l, r.hash_lo);
        ls(&l, " records "); ldec(&l, r.records); ls(&l, " status "); ldec(&l, st); lend(&l);
        if (st != 0) a = 0;
    }
    set_fpscr(get_fpscr() | (1u << 24));                                 /* flush-to-zero what-if */
    run_pass('C', 0);
    out(a ? "QEMU RESULT PASS\n" : "QEMU RESULT FAIL\n");
    quit(a ? 0 : 1);
    return 0;
}
