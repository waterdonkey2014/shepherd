/* gltest host services on the PS Vita. */
#include "plat.h"
#include <psp2/kernel/processmgr.h>
#include <psp2/kernel/threadmgr.h>
#include <psp2/kernel/clib.h>
#include <psp2/kernel/sysmem.h>
#include <psp2/io/fcntl.h>
#include <psp2/io/stat.h>
#include <psp2/power.h>
#include <psp2/ctrl.h>
#include <stdio.h>
#include <string.h>
#include "debugScreen.h"

/* newlib heap: the CPU reference keeps a few 960x544 RGBA frames (2 MiB each) + the synthetic textures. Reserved before
 * main() from USER_RW, so vitaGL's RAM pool (free - threshold at vglInitExtended) is what is left after it. */
__attribute__((used)) unsigned int _newlib_heap_size_user = 64u * 1024u * 1024u;
__attribute__((used)) const SceSize sceUserMainThreadStackSize = 1024 * 1024;

#define LOG_DIR "ux0:data/shepherd"
#define LOG_PATH LOG_DIR "/gltest.txt"
static int g_screen;
static int c_arm = -1, c_bus = -1, c_gpu = -1, c_xbar = -1;

const char *plat_log_path(void) { return LOG_PATH; }
static void write_line(const char *line, int trunc)
{
	SceUID fd = sceIoOpen(LOG_PATH, SCE_O_WRONLY | SCE_O_CREAT | (trunc ? SCE_O_TRUNC : SCE_O_APPEND), 0777);
	if (fd >= 0) { sceIoWrite(fd, line, strlen(line)); sceIoWrite(fd, "\n", 1); sceIoClose(fd); }
}
void plat_log_open(void)
{
	sceIoMkdir("ux0:data", 0777);
	sceIoMkdir(LOG_DIR, 0777);
	/* keep the previous run's log (it may be the only record of a crash) before starting a fresh one */
	sceIoRemove(LOG_DIR "/gltest.prev.txt");
	sceIoRename(LOG_PATH, LOG_DIR "/gltest.prev.txt");
	write_line("", 1);
}
void plat_log_line(const char *line)
{
	write_line(line, 0);
	sceClibPrintf("%s\n", line);
	if (g_screen) { psvDebugScreenPuts(line); psvDebugScreenPuts("\n"); }
}
uint64_t plat_us(void) { return sceKernelGetProcessTimeWide(); }
void plat_delay_ms(int ms) { sceKernelDelayThread((SceUInt)ms * 1000u); }
const char *plat_name(void) { return sceKernelIsPSVitaTV() ? "PS TV (vitaGL)" : "PS Vita (vitaGL)"; }
const char *plat_dialect(void) { return "essl100"; }

static long long fsize(const char *p) { SceIoStat st; memset(&st, 0, sizeof st); return sceIoGetstat(p, &st) >= 0 ? (long long)st.st_size : -1; }
int plat_shacccg(char *where, int n)
{
	/* the two paths this vitaGL/vitaShaRK build tries, in its order */
	static const char *const paths[2] = { "ur0:data/external/libshacccg.suprx", "ur0:data/libshacccg.suprx" };
	for (int i = 0; i < 2; i++) {
		long long s = fsize(paths[i]);
		if (s > 0) { snprintf(where, n, "%s (%lld bytes)", paths[i], s); return 1; }
	}
	snprintf(where, n, "ur0:data/libshacccg.suprx and ur0:data/external/libshacccg.suprx are both missing");
	return 0;
}
void plat_screen(int on)
{
	if (on && !g_screen) g_screen = psvDebugScreenInit() >= 0;
	else if (!on && g_screen) { psvDebugScreenFinish(); g_screen = 0; }
}
void plat_clocks_set(void)
{
	c_arm = scePowerGetArmClockFrequency(); c_bus = scePowerGetBusClockFrequency();
	c_gpu = scePowerGetGpuClockFrequency(); c_xbar = scePowerGetGpuXbarClockFrequency();
	scePowerSetArmClockFrequency(444); scePowerSetBusClockFrequency(222);
	scePowerSetGpuClockFrequency(222); scePowerSetGpuXbarClockFrequency(166);
	sceKernelDelayThread(100 * 1000);
}
void plat_clocks_restore(void)
{
	if (c_arm <= 0) return;
	scePowerSetArmClockFrequency(c_arm); scePowerSetBusClockFrequency(c_bus);
	scePowerSetGpuClockFrequency(c_gpu); scePowerSetGpuXbarClockFrequency(c_xbar);
}
void plat_clocks_str(char *buf, int n)
{
	snprintf(buf, n, "arm %d bus %d gpu %d xbar %d MHz (launch: %d/%d/%d/%d)", scePowerGetArmClockFrequency(), scePowerGetBusClockFrequency(),
	         scePowerGetGpuClockFrequency(), scePowerGetGpuXbarClockFrequency(), c_arm, c_bus, c_gpu, c_xbar);
}
int plat_button(void)
{
	SceCtrlData pad; memset(&pad, 0, sizeof pad);
	sceCtrlPeekBufferPositive(0, &pad, 1);
	return (pad.buttons & (SCE_CTRL_START | SCE_CTRL_CROSS | SCE_CTRL_CIRCLE)) != 0;
}
int plat_exit_wait_frames(void) { return 60 * 600; }   /* ~10 minutes, then exit anyway */
void plat_exit(int fails)
{
	(void)fails;
	char cl[200], line[240];
	plat_clocks_str(cl, sizeof cl);
	snprintf(line, sizeof line, "CLK end %s", cl);   /* every Shepherd Vita app logs the clocks at start and at end */
	plat_log_line(line);
	plat_clocks_restore();
	sceKernelExitProcess(0);
}
