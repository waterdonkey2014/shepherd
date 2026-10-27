/* Shepherd Phys T1 (SHPD00020) -- runs the T1 determinism scenarios of libshepphys_vita.a on the device and checks them
 * against the golden hashes recorded on the Mac (compiled in, tests/golden/t1_golden.h). Ours; synthetic scenes only.
 * Writes ux0:data/shepherd/physics-t1.txt (one open/append/close per line) and, for each scenario whose hash differs,
 * ux0:data/shepherd/physics-t1-<pass>-<name>.txt with every record ("TAG a b | hex words", the Mac runner's format
 * minus the decimals) so the owner can bring it back for `diff` against vita/physics/tests/golden/<name>.txt.
 * Passes: A = the FPSCR the OS gives this thread (printed; UNVERIFIED until a device run), B = IEEE (FZ=0 DN=0 RN),
 * C = flush-to-zero (FZ=1, informational: what if the default were FZ). Timing per scenario with the process clock.
 * Exits by itself after 20 s or on START.
 * Clocks (2026-10-13, every Shepherd Vita app): 444/222/222/166 (ARM/bus/GPU/crossbar) set at start, logged on CLK lines (rc +
 * read-back) at start and at the end; the launch values are restored before exit.
 */
#include <psp2/ctrl.h>
#include <psp2/io/fcntl.h>
#include <psp2/io/stat.h>
#include <psp2/kernel/clib.h>
#include <psp2/kernel/processmgr.h>
#include <psp2/kernel/threadmgr.h>
#include <psp2/power.h>
#include <stdarg.h>
#include <stdio.h>
#include <string.h>

#include "debugScreen.h"
#include "shepphys.h"
#include "sp_t1.h"

unsigned int _newlib_heap_size_user = 32u * 1024u * 1024u;
const SceSize sceUserMainThreadStackSize = 1024 * 1024;

#define OUT_DIR "ux0:data/shepherd"
#define LOG_PATH OUT_DIR "/physics-t1.txt"

static int g_screen;

static void log_line(const char *line, int truncate)
{
    SceUID fd = sceIoOpen(LOG_PATH, SCE_O_WRONLY | SCE_O_CREAT | (truncate ? SCE_O_TRUNC : SCE_O_APPEND), 0777);
    if (fd >= 0) { sceIoWrite(fd, line, strlen(line)); sceIoWrite(fd, "\n", 1); sceIoClose(fd); }
    sceClibPrintf("%s\n", line);
    if (g_screen) { psvDebugScreenPuts(line); psvDebugScreenPuts("\n"); }
}

static void logf_(const char *fmt, ...)
{
    char buf[512];
    va_list ap;
    va_start(ap, fmt);
    vsnprintf(buf, sizeof buf, fmt, ap);
    va_end(ap);
    log_line(buf, 0);
}

static void log_clocks(const char *when)
{
    logf_("CLK %s: arm %d bus %d gpu %d xbar %d MHz", when, scePowerGetArmClockFrequency(), scePowerGetBusClockFrequency(),
          scePowerGetGpuClockFrequency(), scePowerGetGpuXbarClockFrequency());
}

static uint32_t get_fpscr(void) { uint32_t v; __asm__ volatile("vmrs %0, fpscr" : "=r"(v)); return v; }
static void set_fpscr(uint32_t v) { __asm__ volatile("vmsr fpscr, %0" ::"r"(v) : "memory"); }

/* the dump sink: buffered writes of "TAG a b | hex words" */
typedef struct { SceUID fd; char buf[8192]; int n; } Dump;
static void dump_flush(Dump *d) { if (d->n && d->fd >= 0) sceIoWrite(d->fd, d->buf, d->n); d->n = 0; }
static void on_rec(void *user, uint32_t tag, int32_t a, int32_t b, const uint32_t *w, int32_t n, uint32_t fmask)
{
    Dump *d = (Dump *)user;
    char line[300];
    int k = snprintf(line, sizeof line, "%c%c%c%c %ld %ld |", (int)(tag & 0xFF), (int)((tag >> 8) & 0xFF),
                     (int)((tag >> 16) & 0xFF), (int)(tag >> 24), (long)a, (long)b);
    for (int32_t i = 0; i < n && k < (int)sizeof line - 12; ++i) k += snprintf(line + k, sizeof line - k, " %08lx", (unsigned long)w[i]);
    k += snprintf(line + k, sizeof line - k, " |\n");
    if (d->n + k > (int)sizeof d->buf) dump_flush(d);
    memcpy(d->buf + d->n, line, k);
    d->n += k;
}

static int run_pass(char pass, uint32_t fpscr_set, int use_set)
{
    uint32_t saved = get_fpscr();
    if (use_set) set_fpscr(fpscr_set);
    int32_t n = sp_t1_count(), ok = 0, have = 0;
    for (int32_t i = 0; i < n; ++i) {
        sp_t1_result r, e;
        SceUInt64 t0 = sceKernelGetProcessTimeWide();
        sp_t1_run(i, NULL, NULL, &r);
        SceUInt64 us = sceKernelGetProcessTimeWide() - t0;
        int g = sp_t1_expected(i, &e);
        int match = g && e.hash_lo == r.hash_lo && e.hash_hi == r.hash_hi && r.status == 0;
        have += g;
        ok += match;
        logf_("%c %-16s %s hash %08lx%08lx golden %08lx%08lx recs %ld steps %ld %8lu us %7lu us/step", pass, sp_t1_name(i),
              match ? "OK      " : "MISMATCH", (unsigned long)r.hash_hi, (unsigned long)r.hash_lo, (unsigned long)e.hash_hi,
              (unsigned long)e.hash_lo, (long)r.records, (long)r.steps, (unsigned long)us,
              (unsigned long)(r.steps ? us / (SceUInt64)r.steps : 0));
        if (!match && pass != 'C') {
            char path[160];
            snprintf(path, sizeof path, OUT_DIR "/physics-t1-%c-%s.txt", pass, sp_t1_name(i));
            static Dump d;
            d.fd = sceIoOpen(path, SCE_O_WRONLY | SCE_O_CREAT | SCE_O_TRUNC, 0777);
            d.n = 0;
            sp_t1_run(i, on_rec, &d, &r);
            dump_flush(&d);
            if (d.fd >= 0) sceIoClose(d.fd);
        }
    }
    logf_("%c SUMMARY %ld/%ld match (fpscr during pass 0x%08lx)", pass, (long)ok, (long)have, (unsigned long)get_fpscr());
    set_fpscr(saved);
    return ok == have && have == n;
}

int main(void)
{
    uint32_t fpscr0 = get_fpscr();   /* read before anything else touches the VFP state */
    psvDebugScreenInit();
    g_screen = 1;
    sceIoMkdir(OUT_DIR, 0777);
    log_line("Shepherd Phys T1 -- libshepphys determinism check (Box2D 2.3.1 + shim, synthetic scenes)", 1);
    int c_arm = scePowerGetArmClockFrequency(), c_bus = scePowerGetBusClockFrequency();
    int c_gpu = scePowerGetGpuClockFrequency(), c_xbar = scePowerGetGpuXbarClockFrequency();
    log_clocks("at launch");
    {
        int ra = scePowerSetArmClockFrequency(444), rb = scePowerSetBusClockFrequency(222);
        int rg = scePowerSetGpuClockFrequency(222), rx = scePowerSetGpuXbarClockFrequency(166);
        sceKernelDelayThread(100 * 1000);
        logf_("CLK set 444/222/222/166: rc arm 0x%08lx bus 0x%08lx gpu 0x%08lx xbar 0x%08lx", (unsigned long)ra, (unsigned long)rb,
              (unsigned long)rg, (unsigned long)rx);
        log_clocks("start");
    }
    logf_("build: %s", sp_build_info());
    logf_("golden: %s", sp_t1_golden_info());
    int32_t sizes[SP_ABI_N_SIZES];
    float probe = 0.0f;
    int32_t abi = sp_abi(sizes, SP_ABI_N_SIZES, &probe);
    logf_("abi %ld probe %s sizes %ld %ld %ld %ld %ld %ld %ld %ld %ld %ld %ld", (long)abi, probe == SHEPPHYS_ABI_PROBE ? "ok" : "BAD",
          (long)sizes[0], (long)sizes[1], (long)sizes[2], (long)sizes[3], (long)sizes[4], (long)sizes[5], (long)sizes[6],
          (long)sizes[7], (long)sizes[8], (long)sizes[9], (long)sizes[10]);
    logf_("FPSCR at entry 0x%08lx: FZ=%lu DN=%lu RMode=%lu AHP=%lu", (unsigned long)fpscr0, (unsigned long)((fpscr0 >> 24) & 1),
          (unsigned long)((fpscr0 >> 25) & 1), (unsigned long)((fpscr0 >> 22) & 3), (unsigned long)((fpscr0 >> 26) & 1));
    float s1 = sp_sinf(1.0f), c1 = sp_cosf(1.0f);
    uint32_t sb, cb;
    memcpy(&sb, &s1, 4);
    memcpy(&cb, &c1, 4);
    logf_("sp_sinf(1) %08lx (mac 3f576aa4) sp_cosf(1) %08lx (mac 3f0a5140)", (unsigned long)sb, (unsigned long)cb);
    const uint32_t ieee = fpscr0 & ~((3u << 22) | (1u << 24) | (1u << 25));
    int a = run_pass('A', 0, 0);
    int b = run_pass('B', ieee, 1);
    run_pass('C', ieee | (1u << 24), 1);
    log_clocks("end");
    logf_("T1 RESULT %s (A default FPSCR %s, B IEEE %s)", a && b ? "PASS" : "FAIL", a ? "ok" : "mismatch", b ? "ok" : "mismatch");
    log_line("press START to exit (auto exit in 20 s)", 0);
    SceCtrlData pad;
    for (int i = 0; i < 200; ++i) {
        memset(&pad, 0, sizeof pad);
        sceCtrlPeekBufferPositive(0, &pad, 1);
        if (pad.buttons & SCE_CTRL_START) break;
        sceKernelDelayThread(100 * 1000);
    }
    scePowerSetArmClockFrequency(c_arm); scePowerSetBusClockFrequency(c_bus);
    scePowerSetGpuClockFrequency(c_gpu); scePowerSetGpuXbarClockFrequency(c_xbar);
    sceKernelExitProcess(0);
    return 0;
}
