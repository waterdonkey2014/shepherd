/* Shepherd Probe (shepherd-probe-4 since k4: every taiHEN config line; -3 from kit #2; the banner said "v2" until device kit #2) -- measures what only real PS Vita hardware can tell the Shepherd port plan (vita/PLAN.md, gate V0).
 * Pure homebrew measurement: synthetic data only, no game code or assets. Writes ux0:data/shepherd/probe.txt
 * (one open/append/close per line so a crash or freeze still leaves the log up to the failing step).
 *   clocks first (444/222/222/166, CLK lines at start and end), then 1 env/memory  3 presence (libshacccg, kubridge/PSVshell
 *   config)  4 storage  5 cpu bench  6 gpu; 7 executable-memory tests are NOT part of this build (logged as SKIP).
 * Card writes: probe.txt (+ probe.prev.txt) and, through vitaGL's init (GPU test), ux0:data/shader_cache/ (vitaGL's own
 * fixed-function shader cache, shared by every vitaGL app).
 */
#include <psp2/kernel/processmgr.h>
#include <psp2/kernel/sysmem.h>
#include <psp2/kernel/threadmgr.h>
#include <psp2/kernel/cpu.h>
#include <psp2/kernel/clib.h>
#include <psp2/io/fcntl.h>
#include <psp2/io/stat.h>
#include <psp2/power.h>
#include <psp2/ctrl.h>
#include <stdarg.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <math.h>
#include <vitaGL.h>
#include "debugScreen.h"
#include "bench.h"
#include "mac_ref.h"
#include <malloc.h>
#ifndef BENCH_SHA256
#define BENCH_SHA256 "unknown"
#endif
#ifndef VITA_BENCH_FLAGS
#define VITA_BENCH_FLAGS "unknown"
#endif
extern unsigned char is_shark_online;   /* vitaGL global: 1 once libshacccg was actually loaded */
extern void *gxm_context, *gxm_shader_patcher, *gxm_render_target;   /* vitaGL globals: set by its init (see test_gpu) */
/* vitaGL keeps a pool's requested size even when its memblock never mapped, and vglMemFree then reads a NULL mspace; vglAlloc
 * has a NULL-mspace guard. So a 4 KiB alloc + free proves a pool before vglMemFree is asked (vitagl-rootcause.md section 5).
 * 1 = usable, 0 = absent (total 0: vglMemFree is safe and returns 0), -1 = sized but unusable. */
static int vgl_pool_alive(vglMemType t) {
  if (vglMemTotal(t) == 0) return 0;
  void *p = vglAlloc(4096, t);
  if (p) vglFree(p);
  return p ? 1 : -1;
}
static double vgl_pool_free_mib(vglMemType t) { return vgl_pool_alive(t) >= 0 ? vglMemFree(t) / (1024.0 * 1024.0) : -1.0; }
/* vitaGL's splash (splashscreen.o): its init starts a thread that draws the animated logo with its own GXM context and queues it
 * to the display every frame until the first GL scene, which ends it (scene_reset: signal splash_mutex[0], wait for [1], clear
 * is_splashscreen_active, clear_splashscreen, delete both semaphores). When the probe gives up after a failed init it draws no
 * scene, so the logo would keep the display and hide the debug-screen fallback (2026-10-09: the logo stayed). vgl_end_splash
 * does what scene_reset does, without a scene. 1 = ended, 0 = not running, -1 = no answer within timeout_ms (left running). */
extern unsigned char is_splashscreen_active;
extern SceUID splash_mutex[2];   /* [0] "end now" (to the splash thread), [1] "ended" (from it) */
extern void clear_splashscreen(void);
static int vgl_end_splash(unsigned timeout_ms) {
  if (!is_splashscreen_active) return 0;
  sceKernelSignalSema(splash_mutex[0], 1);
  SceUInt to = timeout_ms * 1000u;
  if (sceKernelWaitSema(splash_mutex[1], 1, &to) < 0) return -1;
  is_splashscreen_active = 0;
  if (gxm_shader_patcher) clear_splashscreen();
  sceKernelDeleteSema(splash_mutex[0]); sceKernelDeleteSema(splash_mutex[1]);
  sceKernelDelayThread(100 * 1000);   /* let the splash's last queued flip reach the display before the debug screen sets it */
  return 1;
}

#define PROBE_VERSION "shepherd-probe-4"   /* 3: clocks set first + CLK end lines; "vitaGL up" from its own state (clamped=N); 4: every taiHEN config line */
#define NEWLIB_HEAP (16u * 1024u * 1024u)
unsigned int _newlib_heap_size_user = NEWLIB_HEAP;          /* keep SMALL: it is reserved before main() */
const SceSize sceUserMainThreadStackSize = 1024 * 1024;     /* 1 MiB main stack */

#define LOG_DIR "ux0:data/shepherd"
#define LOG_PATH LOG_DIR "/probe.txt"
#define MiB (1024.0 * 1024.0)

static int g_screen = 0;
static double g_summary_user_mib = -1, g_summary_cdram_mib = -1, g_summary_phy_mib = -1;
static int g_summary_arm = -1, g_summary_shaccg = 0, g_cpu_valid = 1, g_gpu_fail = 0;
static int c_arm = -1, c_bus = -1, c_gpu = -1, c_xbar = -1;   /* clocks at launch, restored on exit */
static double g_summary_seq_mbs = -1, g_summary_sty_ratio = -1, g_summary_chase_ratio = -1, g_summary_draw_us = -1, g_summary_sty_gpu_ms = -1;

static uint64_t now_us(void) { return sceKernelGetProcessTimeWide(); }

static void log_raw(const char *line, int truncate) {
  SceUID fd = sceIoOpen(LOG_PATH, SCE_O_WRONLY | SCE_O_CREAT | (truncate ? SCE_O_TRUNC : SCE_O_APPEND), 0777);
  if (fd >= 0) { sceIoWrite(fd, line, strlen(line)); sceIoWrite(fd, "\n", 1); sceIoClose(fd); }
  sceClibPrintf("%s\n", line);
  if (g_screen) { psvDebugScreenPuts(line); psvDebugScreenPuts("\n"); }
}
static void logf_(const char *fmt, ...) {
  char buf[512];
  va_list ap; va_start(ap, fmt); vsnprintf(buf, sizeof buf, fmt, ap); va_end(ap);
  log_raw(buf, 0);
}
#define LOG(...) logf_(__VA_ARGS__)
static void bench_log(const char *line) { log_raw(line, 0); }

/* ------------------------------------------------------------------ 1. memory */
typedef struct { const char *name; SceKernelMemBlockType type; uint32_t align; } memtype;
static const memtype MEMTYPES[] = {
  {"USER_RW", SCE_KERNEL_MEMBLOCK_TYPE_USER_RW, 4096},
  {"USER_CDRAM_RW", SCE_KERNEL_MEMBLOCK_TYPE_USER_CDRAM_RW, 256 * 1024},
  {"USER_MAIN_PHYCONT_NC_RW", SCE_KERNEL_MEMBLOCK_TYPE_USER_MAIN_PHYCONT_NC_RW, 1024 * 1024},
  {"USER_MAIN_PHYCONT_RW", SCE_KERNEL_MEMBLOCK_TYPE_USER_MAIN_PHYCONT_RW, 1024 * 1024},
  /* USER_MAIN_CDIALOG_RW is not measured: on the owner's PCH-2000 (3.74, HENlo) the probe hung in its search, right after
   * PHYCONT_RW (2026-10-07), and the port does not use it. */
};

static void log_free(const char *when) {
  SceKernelFreeMemorySizeInfo fi; memset(&fi, 0, sizeof fi); fi.size = sizeof fi;
  int rc = sceKernelGetFreeMemorySize(&fi);
  LOG("MEM free[%s] rc=0x%08X user=%.2f MiB cdram=%.2f MiB phycont=%.2f MiB", when, (unsigned)rc,
      fi.size_user / MiB, fi.size_cdram / MiB, fi.size_phycont / MiB);
}

/* largest single block of this type, binary search on multiples of align, upper bound hi */
static uint32_t largest_block(const memtype *t, uint32_t hi) {
  uint32_t lo = 0; hi = (hi / t->align + 2) * t->align;
  while (hi - lo > t->align) {
    uint32_t mid = ((lo + (hi - lo) / 2) / t->align) * t->align;
    if (mid <= lo) mid = lo + t->align;
    SceUID uid = sceKernelAllocMemBlock("probe", t->type, mid, NULL);
    if (uid >= 0) { sceKernelFreeMemBlock(uid); lo = mid; } else hi = mid;
  }
  return lo;
}

static void test_memory(void) {
  LOG("BEGIN memory (newlib heap reserved before main = %.1f MiB, main stack = %u KiB)", NEWLIB_HEAP / MiB, (unsigned)(sceUserMainThreadStackSize / 1024));
  log_free("start");
  SceKernelFreeMemorySizeInfo fi; memset(&fi, 0, sizeof fi); fi.size = sizeof fi; sceKernelGetFreeMemorySize(&fi);
  for (unsigned i = 0; i < sizeof MEMTYPES / sizeof MEMTYPES[0]; i++) {
    const memtype *t = &MEMTYPES[i];
    uint32_t hi = (t->type == SCE_KERNEL_MEMBLOCK_TYPE_USER_CDRAM_RW) ? (uint32_t)fi.size_cdram
                : (t->type == SCE_KERNEL_MEMBLOCK_TYPE_USER_RW) ? (uint32_t)fi.size_user
                : (t->type == SCE_KERNEL_MEMBLOCK_TYPE_USER_MAIN_CDIALOG_RW) ? 32u * 1024u * 1024u
                : (uint32_t)fi.size_phycont;
    if (hi == 0) hi = 32u * 1024u * 1024u;
    uint64_t t0 = now_us();
    uint32_t big = largest_block(t, hi);
    /* greedy total: keep grabbing the largest remaining block */
    static SceUID held[256]; int nh = 0; uint64_t total = 0;
    while (nh < 256) {
      uint32_t b = largest_block(t, big ? big : hi);
      if (b < t->align) break;
      SceUID uid = sceKernelAllocMemBlock("probe_tot", t->type, b, NULL);
      if (uid < 0) break;
      held[nh++] = uid; total += b;
    }
    for (int k = 0; k < nh; k++) sceKernelFreeMemBlock(held[k]);
    LOG("MEM %-24s largest=%.2f MiB total=%.2f MiB in %d blocks (%.0f ms)%s", t->name, big / MiB, total / MiB, nh, (now_us() - t0) / 1000.0,
        nh == 256 ? " (CAPPED: lower bound)" : "");
    if (t->type == SCE_KERNEL_MEMBLOCK_TYPE_USER_RW) g_summary_user_mib = total / MiB;
    if (t->type == SCE_KERNEL_MEMBLOCK_TYPE_USER_CDRAM_RW) g_summary_cdram_mib = total / MiB;
    if (t->type == SCE_KERNEL_MEMBLOCK_TYPE_USER_MAIN_PHYCONT_NC_RW) g_summary_phy_mib = total / MiB;
  }
  LOG("SKIP memory USER_MAIN_CDIALOG_RW (hung on device 2026-10-07; not used by the port)");
  log_free("after-free");
  LOG("NOTE USER_RW figures exclude the %.1f MiB newlib heap, the 1 MiB main stack and the eboot; CDRAM excludes debugScreen's 2 MiB framebuffer. Add them back for the process budget.", NEWLIB_HEAP / MiB);
  LOG("PASS memory");
}

/* ------------------------------------------------------------------ 2. clocks (run first: every Shepherd Vita app sets
 * 444/222/222/166 at start and logs the clocks read back at start and at end, so every timing in the log is at a known clock) */
static void log_clocks(const char *when) {
  LOG("CLK %s arm=%d bus=%d gpu=%d xbar=%d", when, scePowerGetArmClockFrequency(), scePowerGetBusClockFrequency(),
      scePowerGetGpuClockFrequency(), scePowerGetGpuXbarClockFrequency());
}
static void test_clocks(void) {
  LOG("BEGIN clocks (set at start, before every other test)");
  log_clocks("before");
  c_arm = scePowerGetArmClockFrequency(); c_bus = scePowerGetBusClockFrequency(); c_gpu = scePowerGetGpuClockFrequency(); c_xbar = scePowerGetGpuXbarClockFrequency();
  int a = scePowerSetArmClockFrequency(444), b = scePowerSetBusClockFrequency(222);
  int g = scePowerSetGpuClockFrequency(222), x = scePowerSetGpuXbarClockFrequency(166);
  LOG("CLK set rc arm=0x%08X bus=0x%08X gpu=0x%08X xbar=0x%08X", (unsigned)a, (unsigned)b, (unsigned)g, (unsigned)x);
  sceKernelDelayThread(200 * 1000);
  log_clocks("after");
  g_summary_arm = scePowerGetArmClockFrequency();
  LOG("%s clocks", g_summary_arm == 444 ? "PASS" : "FAIL");
}

/* ------------------------------------------------------------------ 3. presence */
static SceOff file_size(const char *path) {
  SceIoStat st; memset(&st, 0, sizeof st);
  return sceIoGetstat(path, &st) >= 0 ? st.st_size : -1;
}
static void scan_config(const char *path) {
  SceUID fd = sceIoOpen(path, SCE_O_RDONLY, 0);
  if (fd < 0) { LOG("CFG %s not readable (0x%08X)", path, (unsigned)fd); return; }
  static char buf[64 * 1024];
  int n = sceIoRead(fd, buf, sizeof buf - 1); sceIoClose(fd);
  if (n < 0) n = 0;
  buf[n] = 0;
  LOG("CFG %s (%d bytes)", path, n);
  char *line = buf;
  while (line && *line) {
    char *nl = strchr(line, '\n'); if (nl) *nl = 0;
    /* probe-4 (k4 review): every non-comment line (sections and plugin paths), so the log names any core-unlock plugin too -- probe-3
     * printed only the kubridge / PSVshell lines */
    char *t = line; while (*t == ' ' || *t == '\t') t++;
    size_t len = strlen(t); while (len && (t[len - 1] == '\r' || t[len - 1] == ' ' || t[len - 1] == '\t')) t[--len] = 0;
    if (*t && *t != '#')
      LOG("CFG   %s", t);
    line = nl ? nl + 1 : NULL;
  }
}
static void test_presence(void) {
  LOG("BEGIN presence");
  SceOff s1 = file_size("ur0:data/libshacccg.suprx"), s2 = file_size("ur0:data/external/libshacccg.suprx");
  LOG("SHACCCG ur0:data/libshacccg.suprx=%lld bytes, ur0:data/external/libshacccg.suprx=%lld bytes (-1 = missing)", (long long)s1, (long long)s2);
  g_summary_shaccg = (s1 > 0 || s2 > 0);
  scan_config("ur0:tai/config.txt");
  scan_config("ux0:tai/config.txt");
  LOG("NOTE kubridge is only detected via config.txt here (its exports are not called in this build)");
  LOG("%s presence (libshacccg %s)", g_summary_shaccg ? "PASS" : "FAIL", g_summary_shaccg ? "found" : "MISSING -> GPU test will be skipped");
}

/* ------------------------------------------------------------------ 4. storage */
/* Each phase reads a region no earlier phase touched, so no figure benefits from a driver/FS cache:
 *   random 4 KiB over the last 32 MiB  ->  64 KiB chunks over [0, 32 MiB)  ->  1 MiB chunks from 32 MiB to end-of-random-region */
static void test_storage(void) {
  const char *path = LOG_DIR "/readtest.bin";
  LOG("BEGIN storage");
  SceOff sz = file_size(path);
  const SceOff R32 = 32ll * 1024 * 1024;
  if (sz < 3 * R32) { LOG("SKIP storage: put a file of at least 100 MB (ideally ~400 MB) at %s to measure memory-card read speed", path); return; }
  const uint32_t big = 1024 * 1024;
  uint8_t *buf = (uint8_t *)memalign(64, big);
  if (!buf) { LOG("FAIL storage: memalign"); return; }
  uint32_t cks = 0, st = 0x9E3779B9u;
  SceUID fd = sceIoOpen(path, SCE_O_RDONLY, 0);
  if (fd < 0) { LOG("FAIL storage: open 0x%08X", (unsigned)fd); free(buf); return; }
  /* random 4 KiB in the last 32 MiB */
  SceOff rbase = sz - R32; int reads = 0; uint64_t t0 = now_us();
  for (int i = 0; i < 2000; i++) {
    st ^= st << 13; st ^= st >> 17; st ^= st << 5;
    SceOff off = rbase + (SceOff)(st % (uint32_t)(R32 / 4096)) * 4096;
    if (sceIoLseek(fd, off, SCE_SEEK_SET) < 0) break;
    if (sceIoRead(fd, buf, 4096) != 4096) break;
    cks += buf[2]; reads++;
  }
  double s = (now_us() - t0) / 1e6;
  LOG("IO random 4KiB (cold): %d reads in %.2f s = %.0f reads/s (%.3f ms each)", reads, s, reads / (s > 0 ? s : 1), s * 1000.0 / (reads ? reads : 1));
  /* 64 KiB chunks over the first 32 MiB */
  sceIoLseek(fd, 0, SCE_SEEK_SET); t0 = now_us(); SceOff done = 0;
  while (done < R32) { int n = sceIoRead(fd, buf, 64 * 1024); if (n <= 0) break; done += n; cks += buf[1]; }
  s = (now_us() - t0) / 1e6;
  LOG("IO seq 64KiB chunks (cold): %.1f MiB in %.2f s = %.1f MiB/s", done / MiB, s, done / MiB / (s > 0 ? s : 1));
  /* 1 MiB chunks from 32 MiB up to the random region */
  SceOff end = rbase; if (end - R32 > 512ll * 1024 * 1024) end = R32 + 512ll * 1024 * 1024;
  sceIoLseek(fd, R32, SCE_SEEK_SET); t0 = now_us(); done = 0;
  while (R32 + done < end) { int n = sceIoRead(fd, buf, big); if (n <= 0) break; done += n; cks += buf[0]; }
  s = (now_us() - t0) / 1e6; sceIoClose(fd);
  g_summary_seq_mbs = done / MiB / (s > 0 ? s : 1);
  LOG("IO seq 1MiB chunks (cold): %.1f MiB in %.2f s = %.1f MiB/s (cks %u)", done / MiB, s, g_summary_seq_mbs, (unsigned)cks);
  free(buf);
  LOG("PASS storage");
}

/* ------------------------------------------------------------------ 5. cpu */
static int bench_pre(const char *name, char *note, int n) {
  (void)name; int arm = scePowerGetArmClockFrequency(), bus = scePowerGetBusClockFrequency();
  snprintf(note, n, "arm=%d bus=%d", arm, bus);
  return arm != 444 || bus != 222;
}
static void test_cpu(void) {
  LOG("BEGIN cpu (%s, single thread pinned to USER_0)", BENCH_VERSION);
  int same_src = strcmp(BENCH_SHA256, MAC_REF_BENCH_SHA256) == 0, same_ver = strcmp(MAC_REF_VERSION, BENCH_VERSION) == 0;
  LOG("CPU source sha %s vs mac reference %s -> %s; version %s vs %s -> %s", BENCH_SHA256, MAC_REF_BENCH_SHA256, same_src ? "match" : "MISMATCH",
      BENCH_VERSION, MAC_REF_VERSION, same_ver ? "match" : "MISMATCH");
  LOG("CPU flags vita: %s | mac: %s (%s, %s)", VITA_BENCH_FLAGS, MAC_REF_FLAGS, MAC_REF_MACHINE, MAC_REF_WHEN);
  if (!same_src || !same_ver) g_cpu_valid = 0;
  SceUID self = sceKernelGetThreadId();
  int old = sceKernelGetThreadCpuAffinityMask(self);
  int arc = sceKernelChangeThreadCpuAffinityMask(self, SCE_KERNEL_CPU_MASK_USER_0);
  sceKernelDelayThread(1000);
  LOG("CPU affinity rc=0x%08X old=0x%X now=0x%X", (unsigned)arc, (unsigned)old, (unsigned)sceKernelGetThreadCpuAffinityMask(self));
  uint32_t bytes = (BENCH_ARENA_BYTES + 4095u) & ~4095u;
  SceUID uid = sceKernelAllocMemBlock("bench_arena", SCE_KERNEL_MEMBLOCK_TYPE_USER_RW, bytes, NULL);
  if (uid < 0) { LOG("FAIL cpu: arena alloc 0x%08X", (unsigned)uid); g_cpu_valid = 0; return; }
  void *arena = NULL; sceKernelGetMemBlockBase(uid, &arena);
  memset(arena, 0, bytes);
  bench_host h = {now_us, bench_log, bench_pre};
  bench_result r[BENCH_MAX_RESULTS];
  int n = bench_run_all(&h, arena, bytes, r, BENCH_MAX_RESULTS);
  sceKernelFreeMemBlock(uid);
  log_clocks("cpu-end");
  for (int i = 0; i < n; i++)
    for (int k = 0; k < MAC_REF_N; k++)
      if (strcmp(r[i].name, MAC_REF[k].name) == 0) {
        double ratio = MAC_REF[k].min_ms > 0 ? r[i].min_ms / MAC_REF[k].min_ms : -1;
        int ok = r[i].checksum == MAC_REF[k].checksum && !r[i].suspect;
        if (!ok) g_cpu_valid = 0;
        const char *tag = (!strncmp(r[i].name, "memcpy", 6) || !strncmp(r[i].name, "readsum", 7) || !strcmp(r[i].name, "chase_32MiB"))
                          ? "  [mac cache-resident / vita DRAM: use absolute vita figure]" : "";
        LOG("RATIO %-18s vita %.3f ms / mac %.3f ms = %.1fx slower  checksum %s%s%s", r[i].name, r[i].min_ms, MAC_REF[k].min_ms, ratio,
            r[i].checksum == MAC_REF[k].checksum ? "match" : "MISMATCH", r[i].suspect ? " (SUSPECT clock/nondeterministic)" : "", tag);
        if (!strcmp(r[i].name, "stylizer_960x544")) g_summary_sty_ratio = ok ? ratio : -ratio;
        if (!strcmp(r[i].name, "chase_32MiB")) g_summary_chase_ratio = ok ? ratio : -ratio;
      }
  sceKernelChangeThreadCpuAffinityMask(self, old > 0 ? old : SCE_KERNEL_CPU_MASK_USER_ALL);
  LOG("%s cpu (%d kernels, comparison %s)", g_cpu_valid ? "PASS" : "FAIL", n, g_cpu_valid ? "valid" : "INVALID");
}

/* ------------------------------------------------------------------ 6. gpu (vitaGL) */
#define NQ 550
#define NT 16
static float q_xy[NQ][8], q_uv[8] = {0, 0, 1, 0, 0, 1, 1, 1};
static float b_xy[NQ * 12], b_uv[NQ * 12];
static GLuint g_tex[NT];

__attribute__((unused)) static GLuint compile(GLenum kind, const char *src, const char *what) {
  GLuint s = glCreateShader(kind);
  glShaderSource(s, 1, &src, NULL);
  glCompileShader(s);
  GLint ok = 0; glGetShaderiv(s, GL_COMPILE_STATUS, &ok);
  if (!ok) { char info[512] = {0}; glGetShaderInfoLog(s, sizeof info, NULL, info); LOG("GPU shader %s compile FAILED: %s", what, info); glDeleteShader(s); return 0; }
  return s;
}

__attribute__((unused)) static const char *VS = "attribute vec2 aPos;\nattribute vec2 aUv;\nvarying vec2 vUv;\nvoid main() { vUv = aUv; gl_Position = vec4(aPos, 0.0, 1.0); }\n";
/* the GPU version of the Stylizer pass (its fragment shader FS) is owner-local since fid1-fixup D1 (a port of a third-party effect):
   engine/Derived/vita-probe/probe_stylizer_gpu.h, included when present; without it the gpu test has no stylizer pass. */
#if defined(__has_include)
#if __has_include("probe_stylizer_gpu.h")
#include "probe_stylizer_gpu.h"
#endif
#endif

static void draw_quads_individual(void) {
  glEnableClientState(GL_VERTEX_ARRAY); glEnableClientState(GL_TEXTURE_COORD_ARRAY);
  for (int i = 0; i < NQ; i++) {
    glBindTexture(GL_TEXTURE_2D, g_tex[i % NT]);
    glVertexPointer(2, GL_FLOAT, 0, q_xy[i]);
    glTexCoordPointer(2, GL_FLOAT, 0, q_uv);
    glDrawArrays(GL_TRIANGLE_STRIP, 0, 4);
  }
  glDisableClientState(GL_VERTEX_ARRAY); glDisableClientState(GL_TEXTURE_COORD_ARRAY);
}
static void draw_quads_batched(void) {
  glEnableClientState(GL_VERTEX_ARRAY); glEnableClientState(GL_TEXTURE_COORD_ARRAY);
  glBindTexture(GL_TEXTURE_2D, g_tex[0]);
  glVertexPointer(2, GL_FLOAT, 0, b_xy);
  glTexCoordPointer(2, GL_FLOAT, 0, b_uv);
  glDrawArrays(GL_TRIANGLES, 0, NQ * 6);
  glDisableClientState(GL_VERTEX_ARRAY); glDisableClientState(GL_TEXTURE_COORD_ARRAY);
}

typedef struct { double submit_us, swap_us, frame_us; } frame_stats;   /* frame_us = serialized CPU submit + swap + GPU finish */
typedef struct { GLuint fbo, prog, fbo_tex, lut, pat; GLint uSrc, uLut, uPat, uScreen; } sty_gl;
static frame_stats run_frames(int mode, int frames, const sty_gl *g) {
  static const float fs_xy[8] = {-1, -1, 1, -1, -1, 1, 1, 1}, fs_uv[8] = {0, 0, 1, 0, 0, 1, 1, 1};
  frame_stats acc = {0, 0, 0}; int counted = 0;
  for (int f = 0; f < frames; f++) {
    uint64_t t0 = now_us();
    if (mode == 2) glBindFramebuffer(GL_FRAMEBUFFER, g->fbo);
    glClearColor(0.1f, 0.1f, 0.12f, 1.0f); glClear(GL_COLOR_BUFFER_BIT);
    glEnable(GL_TEXTURE_2D);
    if (mode == 1) draw_quads_batched(); else draw_quads_individual();
    if (mode == 2) {
      glBindFramebuffer(GL_FRAMEBUFFER, 0);
      glDisable(GL_BLEND);                       /* the real Stylizer pass is opaque */
      glUseProgram(g->prog);
      glActiveTexture(GL_TEXTURE0); glBindTexture(GL_TEXTURE_2D, g->fbo_tex);
      glActiveTexture(GL_TEXTURE1); glBindTexture(GL_TEXTURE_2D, g->lut);
      glActiveTexture(GL_TEXTURE2); glBindTexture(GL_TEXTURE_2D, g->pat);
      glUniform1i(g->uSrc, 0); glUniform1i(g->uLut, 1); glUniform1i(g->uPat, 2); glUniform2f(g->uScreen, 960.0f, 544.0f);
      glEnableVertexAttribArray(0); glEnableVertexAttribArray(1);
      glVertexAttribPointer(0, 2, GL_FLOAT, GL_FALSE, 0, fs_xy); glVertexAttribPointer(1, 2, GL_FLOAT, GL_FALSE, 0, fs_uv);
      glDrawArrays(GL_TRIANGLE_STRIP, 0, 4);
      glDisableVertexAttribArray(0); glDisableVertexAttribArray(1);
      glUseProgram(0); glActiveTexture(GL_TEXTURE0);
      glEnable(GL_BLEND);
    }
    uint64_t t1 = now_us();
    vglSwapBuffers(GL_FALSE);   /* ends the real scene */
    uint64_t t2 = now_us();
    glFinish();                 /* wait for the GPU: t3 - t0 is a serialized frame */
    uint64_t t3 = now_us();
    if (f >= 10) { acc.submit_us += t1 - t0; acc.swap_us += t2 - t1; acc.frame_us += t3 - t0; counted++; }
  }
  if (counted) { acc.submit_us /= counted; acc.swap_us /= counted; acc.frame_us /= counted; }
  return acc;
}

static void show_color(float r, float g, float b, int frames) {
  for (int i = 0; i < frames; i++) { glClearColor(r, g, b, 1); glClear(GL_COLOR_BUFFER_BIT); vglSwapBuffers(GL_FALSE); }
}

static int test_gpu(void) {   /* returns 1 if vitaGL owns the display afterwards */
  LOG("BEGIN gpu");
  log_clocks("gpu");
  if (!g_summary_shaccg) { LOG("SKIP gpu: libshacccg.suprx not found in ur0:data (extract it on your own console with ShaRKF00D)"); return 0; }
  LOG("GPU handing the display to vitaGL; progress continues in the log file only (screen turns GREEN when done, RED on failure)");
  LOG("GPU note: vitaGL's init creates ux0:data/shader_cache/ (its fixed-function shader cache, shared by every vitaGL app)");
  psvDebugScreenFinish(); g_screen = 0;
  /* the fallback screen's CDRAM: vitaGL's VRAM pool takes ALL free CDRAM at init, so without this block the debug screen (a 2 MiB
   * CDRAM framebuffer) could never come back after a failed init. Kept out of the pool, freed again right after the init. */
  SceUID fb_keep = sceKernelAllocMemBlock("probe-fallback-fb", SCE_KERNEL_MEMBLOCK_TYPE_USER_CDRAM_RW, 2u * 1024u * 1024u, NULL);
  if (fb_keep >= 0) LOG("GPU kept 2 MiB CDRAM out of vitaGL's VRAM pool for the fallback debug screen: the pool is ~2 MiB smaller than the 2026-10-09 run's 96");
  else LOG("GPU could not keep 2 MiB CDRAM for the fallback debug screen (0x%08X): after a failed init the screen may stay dark", (unsigned)fb_keep);
  vglSetupRuntimeShaderCompiler(SHARK_OPT_DEFAULT, 1, 0, 0);
  LOG("GPU shader compiler: SHARK_OPT_DEFAULT fastmath=1 fastprecision=0 fastint=0");
  /* this vitaGL's return value is not a success flag: 1 = the size was clamped to the display maximum, else 0 (also for a
   * working 960x544 init on a handheld). vitaGL is up when its context, shader patcher and render target exist and its VRAM
   * pool really allocates (a 4 KiB vglAlloc); the compiler (is_shark_online) is checked right after. */
  uint64_t t_init = now_us();
  GLboolean clamped = vglInitExtended(0, 960, 544, 16 * 1024 * 1024, SCE_GXM_MULTISAMPLE_NONE);
  double init_ms = (now_us() - t_init) / 1000.0;
  int vram = vgl_pool_alive(VGL_MEM_VRAM);
  int ok = gxm_context && gxm_shader_patcher && gxm_render_target && vram == 1;
  if (fb_keep >= 0) sceKernelFreeMemBlock(fb_keep);
  LOG("GPU vglInitExtended returned clamped=%d (%.0f ms); context %s %p, shader patcher %s %p, render target %s %p, VRAM pool 4 KiB vglAlloc %s, libshacccg %d -> vitaGL up %d",
      clamped, init_ms, gxm_context ? "up" : "MISSING", gxm_context, gxm_shader_patcher ? "up" : "MISSING", gxm_shader_patcher,
      gxm_render_target ? "up" : "MISSING", gxm_render_target, vram == 1 ? "ok" : vram == 0 ? "ABSENT (pool total 0)" : "FAILED",
      (int)is_shark_online, ok);
  LOG("GPU pools MiB free/total (-1 = sized but unusable): vram %.1f/%.1f ram %.1f/%.1f phycont %.1f/%.1f cdlg %.1f/%.1f",
      vgl_pool_free_mib(VGL_MEM_VRAM), vglMemTotal(VGL_MEM_VRAM) / MiB, vgl_pool_free_mib(VGL_MEM_RAM), vglMemTotal(VGL_MEM_RAM) / MiB,
      vgl_pool_free_mib(VGL_MEM_SLOW), vglMemTotal(VGL_MEM_SLOW) / MiB, vgl_pool_free_mib(VGL_MEM_BUDGET), vglMemTotal(VGL_MEM_BUDGET) / MiB);
  if (!ok) {
    /* no GL scene will follow: end vitaGL's splash first (or its logo keeps the display), then bring the debug screen back in
     * the CDRAM kept above, so the FAIL line and the SUMMARY show on screen */
    int sp = vgl_end_splash(5000);
    g_screen = psvDebugScreenInit() >= 0;
    g_gpu_fail = 1;
    LOG("GPU fallback: vitaGL splash %s; debug screen %s", sp > 0 ? "ended" : sp == 0 ? "was not running" : "did NOT end within 5 s (it may keep the display)",
        g_screen ? "back" : "could not start (log file only)");
    LOG("FAIL gpu: vitaGL is not up after vglInitExtended (see the state line above; clamped=%d is not a failure)", clamped);
    return 0;
  }
  LOG("GPU shacccg loaded by vitaGL: %d", (int)is_shark_online);
  if (!is_shark_online) { LOG("FAIL gpu: vitaGL could not load libshacccg (file present but unusable?)"); g_gpu_fail = 1; return 1; }
  vglWaitVblankStart(GL_FALSE);
  glViewport(0, 0, 960, 544);
  glMatrixMode(GL_PROJECTION); glLoadIdentity(); glOrthof(0, 960, 544, 0, -1, 1);
  glMatrixMode(GL_MODELVIEW); glLoadIdentity();
  glEnable(GL_BLEND); glBlendFunc(GL_SRC_ALPHA, GL_ONE_MINUS_SRC_ALPHA);

  /* 16 synthetic 256x256 DXT5 textures (any 16-byte block is valid DXT5) */
  static uint8_t dxt[256 / 4 * 256 / 4 * 16];
  uint32_t s = 0xD5D5D5u;
  glGenTextures(NT, g_tex);
  int dxt_ok = 1;
  for (int t = 0; t < NT; t++) {
    for (unsigned i = 0; i < sizeof dxt; i++) { s ^= s << 13; s ^= s >> 17; s ^= s << 5; dxt[i] = (uint8_t)(s >> 24); }
    glBindTexture(GL_TEXTURE_2D, g_tex[t]);
    glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MIN_FILTER, GL_LINEAR); glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MAG_FILTER, GL_LINEAR);
    glCompressedTexImage2D(GL_TEXTURE_2D, 0, GL_COMPRESSED_RGBA_S3TC_DXT5_EXT, 256, 256, 0, sizeof dxt, dxt);
    if (glGetError() != GL_NO_ERROR) dxt_ok = 0;
  }
  LOG("GPU 16x 256x256 DXT5 textures uploaded: %s; vram free now %.1f MiB", dxt_ok ? "ok" : "GL error", vglMemFree(VGL_MEM_VRAM) / MiB);
  for (int i = 0; i < NQ; i++) {
    s ^= s << 13; s ^= s >> 17; s ^= s << 5;
    float w = 64 + (s % 97), h = 64 + ((s >> 8) % 97), x = (float)((s >> 16) % (960 - 160)), y = (float)((s >> 4) % (544 - 160));
    float v[8] = {x, y, x + w, y, x, y + h, x + w, y + h}; memcpy(q_xy[i], v, sizeof v);
    float tri[12] = {x, y, x + w, y, x, y + h, x + w, y, x + w, y + h, x, y + h}; memcpy(&b_xy[i * 12], tri, sizeof tri);
    float tuv[12] = {0, 0, 1, 0, 0, 1, 1, 0, 1, 1, 0, 1}; memcpy(&b_uv[i * 12], tuv, sizeof tuv);
  }
  show_color(0, 0, 0.3f, 2);
  LOG("GPU warm-up (first draws compile fixed-function shaders; first 10 frames of each run are excluded)");
  frame_stats a = run_frames(0, 130, NULL);
  g_summary_draw_us = a.submit_us / NQ;
  LOG("GPU individual: %d draws, %d textures: submit %.2f ms (%.1f us/draw), swap %.2f ms, serialized frame %.2f ms", NQ, NT, a.submit_us / 1000, a.submit_us / NQ, a.swap_us / 1000, a.frame_us / 1000);
  frame_stats b = run_frames(1, 130, NULL);
  LOG("GPU batched: 1 draw, %d quads: submit %.2f ms, swap %.2f ms, serialized frame %.2f ms", NQ, b.submit_us / 1000, b.swap_us / 1000, b.frame_us / 1000);

#ifndef PROBE_HAVE_STYLIZER_GPU
  LOG("SKIP gpu stylizer pass: the owner-local shader (engine/Derived/vita-probe) is not built in");
#else
  /* Stylizer pass: quads -> 960x544 FBO -> full-screen LUT shader to the backbuffer */
  vglSetSemanticBindingMode(VGL_MODE_SHADER_PAIR);   /* compile eagerly so errors surface in glCompileShader, not as a crash in glLinkProgram */
  GLuint vs = compile(GL_VERTEX_SHADER, VS, "stylizer.vs"), fs = compile(GL_FRAGMENT_SHADER, FS, "stylizer.fs");
  sty_gl g; memset(&g, 0, sizeof g);
  if (vs && fs) {
    g.prog = glCreateProgram(); glAttachShader(g.prog, vs); glAttachShader(g.prog, fs);
    glBindAttribLocation(g.prog, 0, "aPos"); glBindAttribLocation(g.prog, 1, "aUv");
    glLinkProgram(g.prog);
    GLint linked = 0; glGetProgramiv(g.prog, GL_LINK_STATUS, &linked);
    if (!linked) { char il[512] = {0}; glGetProgramInfoLog(g.prog, sizeof il, NULL, il); LOG("GPU stylizer program link FAILED: %s", il); g.prog = 0; }
  }
  if (g.prog) {
    g.uSrc = glGetUniformLocation(g.prog, "uSrc"); g.uLut = glGetUniformLocation(g.prog, "uLut");
    g.uPat = glGetUniformLocation(g.prog, "uPat"); g.uScreen = glGetUniformLocation(g.prog, "uScreen");
    glGenTextures(1, &g.fbo_tex); glBindTexture(GL_TEXTURE_2D, g.fbo_tex);
    glTexImage2D(GL_TEXTURE_2D, 0, GL_RGBA, 960, 544, 0, GL_RGBA, GL_UNSIGNED_BYTE, NULL);
    glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MIN_FILTER, GL_NEAREST); glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MAG_FILTER, GL_NEAREST);
    glGenFramebuffers(1, &g.fbo); glBindFramebuffer(GL_FRAMEBUFFER, g.fbo);
    glFramebufferTexture2D(GL_FRAMEBUFFER, GL_COLOR_ATTACHMENT0, GL_TEXTURE_2D, g.fbo_tex, 0);
    GLenum fbs = glCheckFramebufferStatus(GL_FRAMEBUFFER);
    LOG("GPU FBO status 0x%04X err 0x%04X", (unsigned)fbs, (unsigned)glGetError());
    glBindFramebuffer(GL_FRAMEBUFFER, 0);
    static uint8_t lutpx[256 * 32 * 4], patpx[64 * 64 * 4];
    for (int i = 0; i < 256 * 32; i++) { lutpx[i * 4] = (uint8_t)(i * 7); lutpx[i * 4 + 1] = (uint8_t)(i * 13 + 5); lutpx[i * 4 + 2] = (uint8_t)(i * 29 + 11); lutpx[i * 4 + 3] = 255; }
    for (int y = 0; y < 64; y++) for (int x = 0; x < 64; x++) { uint8_t v = (uint8_t)((((x + y) & 7) < 4) ? 200 : 40); uint8_t *p = &patpx[(y * 64 + x) * 4]; p[0] = p[1] = p[2] = v; p[3] = 255; }
    glGenTextures(1, &g.lut); glBindTexture(GL_TEXTURE_2D, g.lut);
    glTexImage2D(GL_TEXTURE_2D, 0, GL_RGBA, 256, 32, 0, GL_RGBA, GL_UNSIGNED_BYTE, lutpx);
    glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MIN_FILTER, GL_NEAREST); glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MAG_FILTER, GL_NEAREST);
    glGenTextures(1, &g.pat); glBindTexture(GL_TEXTURE_2D, g.pat);
    glTexImage2D(GL_TEXTURE_2D, 0, GL_RGBA, 64, 64, 0, GL_RGBA, GL_UNSIGNED_BYTE, patpx);
    glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MIN_FILTER, GL_NEAREST); glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MAG_FILTER, GL_NEAREST);
    glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_S, GL_REPEAT); glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_T, GL_REPEAT);
    if (fbs == GL_FRAMEBUFFER_COMPLETE) {
      frame_stats c = run_frames(2, 130, &g);
      double gpu_a = (a.frame_us - a.submit_us) / 1000.0, gpu_c = (c.frame_us - c.submit_us) / 1000.0;
      g_summary_sty_gpu_ms = gpu_c - gpu_a;
      LOG("GPU individual + FBO + stylizer pass: submit %.2f ms, swap %.2f ms, serialized frame %.2f ms", c.submit_us / 1000, c.swap_us / 1000, c.frame_us / 1000);
      LOG("GPU stylizer+FBO cost: GPU-side %.2f ms, CPU-side %.2f ms", g_summary_sty_gpu_ms, (c.submit_us - a.submit_us) / 1000.0);
    } else { LOG("FAIL gpu: FBO incomplete; stylizer pass skipped"); g_gpu_fail = 1; }
  } else { LOG("FAIL gpu: stylizer shader/program failed (see above)"); g_gpu_fail = 1; }
#endif
  vglWaitVblankStart(GL_TRUE);
  LOG("%s gpu", g_gpu_fail ? "FAIL" : "PASS");
  return 1;
}

/* ------------------------------------------------------------------ main */
static void wait_button(int vgl) {
  SceCtrlData pad; memset(&pad, 0, sizeof pad);
  for (int i = 0; i < 60 * 600; i++) {   /* up to ~10 minutes, then exit anyway */
    sceCtrlPeekBufferPositive(0, &pad, 1);
    if (pad.buttons & (SCE_CTRL_START | SCE_CTRL_CROSS | SCE_CTRL_CIRCLE)) break;
    if (vgl) show_color(g_gpu_fail ? 0.6f : 0.0f, g_gpu_fail ? 0.0f : 0.55f, g_gpu_fail ? 0.0f : 0.15f, 1); else sceKernelDelayThread(16 * 1000);
  }
}

int main(void) {
  sceIoMkdir("ux0:data", 0777); sceIoMkdir(LOG_DIR, 0777);
  sceIoRemove(LOG_DIR "/probe.prev.txt");            /* keep the previous run's log: it may be the only record of a crash */
  sceIoRename(LOG_PATH, LOG_DIR "/probe.prev.txt");
  log_raw("=== " PROBE_VERSION " ===", 1);
  log_free("pre-screen");          /* before debugScreen takes its 2 MiB CDRAM framebuffer */
  g_screen = psvDebugScreenInit() >= 0;
  if (!g_screen) LOG("WARN debugScreen init failed; log file only");
  LOG("Shepherd Probe 3 (" PROBE_VERSION ") -- measuring this Vita for the CotL port. Do not close the app; it ends on its own.");
  LOG("Log: " LOG_PATH);
  LOG("PSTV=%d", sceKernelIsPSVitaTV());
  test_clocks();
  test_memory();
  test_presence();
  test_storage();
  test_cpu();
  int vgl = test_gpu();
  LOG("SKIP execmem: executable-memory / JIT tests are not part of this build");
  LOG("SUMMARY user_rw=%.1fMiB(+%.0f newlib +1 stack) cdram=%.1fMiB(+2 debugScreen) phycont=%.1fMiB arm=%d shacccg=%d seq=%.1fMiB/s cpu_valid=%d stylizer_ratio=%.1fx chase32M_ratio=%.1fx draw=%.1fus stylizer_gpu=%.2fms gpu_fail=%d",
      g_summary_user_mib, NEWLIB_HEAP / MiB, g_summary_cdram_mib, g_summary_phy_mib, g_summary_arm, g_summary_shaccg, g_summary_seq_mbs,
      g_cpu_valid, g_summary_sty_ratio, g_summary_chase_ratio, g_summary_draw_us, g_summary_sty_gpu_ms, g_gpu_fail);
  LOG("NOTE negative ratios = that kernel's comparison was invalid (checksum mismatch or clock not 444/222)");
  log_clocks("end");
  LOG("DONE -- send " LOG_PATH " back. Press X, O or START to exit.");
  wait_button(vgl);
  if (c_arm > 0) { scePowerSetArmClockFrequency(c_arm); scePowerSetBusClockFrequency(c_bus); scePowerSetGpuClockFrequency(c_gpu); scePowerSetGpuXbarClockFrequency(c_xbar); }
  log_clocks("exit (launch clocks restored)");
  sceKernelExitProcess(0);
  return 0;
}
