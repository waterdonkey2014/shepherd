/* Shepherd JIT probe, PS Vita backend (vita/jit/probe/README.md, vita/notes/kit7-jit.md). Binds jitp.h to the console's own user-mode
 * code-memory API, HENkaku's ForVM (available on the owner's HENlo/taiHEN chain, the same API every Vita dynarec homebrew uses):
 *   plat_vm_alloc  -> sceKernelAllocMemBlockForVM + sceKernelGetMemBlockBase   (<= 16 MiB, 1 MiB granule)
 *   plat_vm_open/close -> sceKernelOpenVMDomain / CloseVMDomain                (process-wide write window: plat_domain_process_wide = 1)
 *   plat_vm_sync   -> sceKernelSyncVMDomain                                     (D-clean + I-invalidate: libgcc __clear_cache is a no-op here)
 *   threads/affinity -> SceKernel threads pinned with sceKernelChangeThreadCpuAffinityMask (CPU3 = the 4th core); sceKernelGetCpuId says
 *                    which core a thread really ran on
 * Generated code: A32 and T32 (Thumb-2, the ISA kit #8's stencils use). It sets the clocks to 444/222/222/166 like every Shepherd app and
 * restores them on exit; it appends to ux0:data/shepherd/jitprobe.txt (one open/append/close per line, crash-safe, every launch kept) and
 * keeps jitprobe.state / .faults / .count next to it for resume-after-restart.
 * IMPORTANT: this backend is BUILT but NOT RUN on a device until the owner confirms (the first exec-memory step). main_vita.c leaves
 * auto_confirm off, so on the console nothing is allocated or generated until the owner releases every button and then presses X. It
 * allocates no executable memory and generates no code on the Mac (that path is plat_mac.c); here the code lives only to be compiled into
 * the owner's VPK for his own run. */
#include <psp2/kernel/processmgr.h>
#include <psp2/kernel/clib.h>
#include <psp2/kernel/sysmem.h>
#include <psp2/kernel/threadmgr.h>
#include <psp2/kernel/cpu.h>
#include <psp2/power.h>
#include <psp2/ctrl.h>
#include <psp2/io/fcntl.h>
#include <psp2/io/stat.h>
#include <stdio.h>
#include <string.h>
#include "jitp.h"
#include "debugScreen.h"

extern int sceKernelFreeMemBlockForVM(SceUID uid);   /* in libSceSysmem_stub.a, not in the vitasdk header (vita/jit/probe/README.md) */

#define LOG_DIR "ux0:data/shepherd"
#define CPU3_MASK 0x80000
#define USER_MASKS { 0x10000, 0x20000, 0x40000, 0x80000 }   /* USER_0..2 + CPU3 */

const SceSize sceUserMainThreadStackSize = 1024 * 1024;
unsigned int _newlib_heap_size_user = 32 * 1024 * 1024;   /* small: the probe needs little; the ForVM block is separate from newlib */

static int g_screen;
static int c_arm = -1, c_bus = -1, c_gpu = -1, c_xbar = -1;

const char *plat_name (void) { return "vita"; }
int plat_isas (int *out, int cap) { int n = 0; if (cap > n) out[n++] = ISA_A32; if (cap > n) out[n++] = ISA_T32; return n; }   /* Mono's ARM32 backend + kit #8's Thumb-2 stencils */
int plat_domain_process_wide (void) { return 1; }

void plat_init (void)
{
	c_arm = scePowerGetArmClockFrequency (); c_bus = scePowerGetBusClockFrequency ();
	c_gpu = scePowerGetGpuClockFrequency (); c_xbar = scePowerGetGpuXbarClockFrequency ();
	scePowerSetArmClockFrequency (444); scePowerSetBusClockFrequency (222);
	scePowerSetGpuClockFrequency (222); scePowerSetGpuXbarClockFrequency (166);
	sceIoMkdir ("ux0:data", 0777); sceIoMkdir (LOG_DIR, 0777);
	g_screen = psvDebugScreenInit () >= 0;
}
void plat_exit (void)
{
	if (c_arm > 0) { scePowerSetArmClockFrequency (c_arm); scePowerSetBusClockFrequency (c_bus); scePowerSetGpuClockFrequency (c_gpu); scePowerSetGpuXbarClockFrequency (c_xbar); }
	sceKernelExitProcess (0);
}
uint64_t plat_us (void) { return sceKernelGetProcessTimeWide (); }
void plat_sleep_us (uint32_t us) { sceKernelDelayThread (us); }

static void path_of (const char *which, char *out, int cap) { snprintf (out, cap, "%s/jitprobe.%s", LOG_DIR, !strcmp (which, "log") ? "txt" : which); }
static void write_text (const char *which, const char *text, int append)
{
	char path[128]; path_of (which, path, sizeof path);
	SceUID fd = sceIoOpen (path, SCE_O_WRONLY | SCE_O_CREAT | (append ? SCE_O_APPEND : SCE_O_TRUNC), 0777);
	if (fd >= 0) { sceIoWrite (fd, text, strlen (text)); sceIoWrite (fd, "\n", 1); sceIoClose (fd); }
}
void plat_log_line (const char *line)
{
	write_text ("log", line, 1);      /* never truncated or rotated: a crashed launch's lines stay for the owner to send back */
	sceClibPrintf ("%s\n", line);
	if (g_screen) { psvDebugScreenPuts (line); psvDebugScreenPuts ("\n"); }
}
int plat_read_file (const char *which, char *buf, int cap)
{
	char path[128]; path_of (which, path, sizeof path);
	SceUID fd = sceIoOpen (path, SCE_O_RDONLY, 0);
	if (fd < 0) return -1;
	int n = sceIoRead (fd, buf, cap - 1); sceIoClose (fd); if (n >= 0) buf[n] = 0; return n;
}
int plat_put_file (const char *which, const char *text, int append) { write_text (which, text, append); return 0; }
void plat_describe (char *buf, int cap)
{
	snprintf (buf, cap, "console: PS Vita, PSTV=%d; clocks arm %d bus %d gpu %d xbar %d MHz (set to 444/222/222/166)",
	          sceKernelIsPSVitaTV (), scePowerGetArmClockFrequency (), scePowerGetBusClockFrequency (),
	          scePowerGetGpuClockFrequency (), scePowerGetGpuXbarClockFrequency ());
}
#define PAD_BUTTONS 0xFFFFu     /* SELECT .. SQUARE: every digital button */
static int pad_now (void) { SceCtrlData pad; memset (&pad, 0, sizeof pad); sceCtrlPeekBufferPositive (0, &pad, 1); return (int) (pad.buttons & PAD_BUTTONS); }
/* every button up for ~100 ms (6 polls in a row); 0 if something stays held for the whole wait */
static int wait_all_released (int polls)
{
	int up = 0;
	for (int i = 0; i < polls; i++) { if (pad_now () == 0) { if (++up >= 6) return 1; } else up = 0; sceKernelDelayThread (16 * 1000); }
	return 0;
}
/* the owner's go: every button released first (an X held from the launch, or a stuck button, never counts), then a FRESH press of X
 * seen on two polls in a row (O / START = exit); 10 minutes without a decision = exit */
int plat_confirm (void)
{
	if (!wait_all_released (60 * 60)) return 0;
	int x_polls = 0;
	for (int i = 0; i < 60 * 600; i++) {
		int b = pad_now ();
		if (b & (SCE_CTRL_CIRCLE | SCE_CTRL_START)) return 0;
		if (b & SCE_CTRL_CROSS) { if (++x_polls >= 2) return 1; } else x_polls = 0;
		sceKernelDelayThread (16 * 1000);
	}
	return 0;
}
void plat_wait_exit (void)
{
	wait_all_released (60 * 60);
	for (int i = 0; i < 60 * 600; i++) { if (pad_now () & (SCE_CTRL_CROSS | SCE_CTRL_CIRCLE | SCE_CTRL_START)) break; sceKernelDelayThread (16 * 1000); }
}
int plat_free_user_kib (void)
{
	SceKernelFreeMemorySizeInfo fi; memset (&fi, 0, sizeof fi); fi.size = sizeof fi;
	if (sceKernelGetFreeMemorySize (&fi) < 0) return -1;
	return (int) (fi.size_user / 1024);
}
int plat_alloc_user (uint32_t bytes, int *id)
{
	bytes = (bytes + 0xfff) & ~0xfffu;
	SceUID u = sceKernelAllocMemBlock ("jitp-user", SCE_KERNEL_MEMBLOCK_TYPE_USER_RW, bytes, NULL);
	*id = u; return u < 0 ? u : 0;
}
void plat_free_user (int id) { if (id >= 0) sceKernelFreeMemBlock (id); }

int plat_vm_alloc (const char *name, uint32_t bytes, jitp_block *out)
{
	bytes = (bytes + 0xfffff) & ~0xfffffu;        /* 1 MiB granule (the field rule) */
	SceUID u = sceKernelAllocMemBlockForVM (name, bytes);
	if (u < 0) return u;
	void *base = NULL;
	int rc = sceKernelGetMemBlockBase (u, &base);
	if (rc < 0 || !base) { sceKernelFreeMemBlockForVM (u); return rc < 0 ? rc : -1; }
	out->id = u; out->base = (uint8_t *) base; out->size = bytes; return 0;
}
int plat_vm_free (jitp_block *b) { int rc = b->id > 0 ? sceKernelFreeMemBlockForVM (b->id) : 0; b->base = 0; b->id = 0; return rc; }
int plat_vm_open (void) { return sceKernelOpenVMDomain (); }
int plat_vm_close (void) { return sceKernelCloseVMDomain (); }
int plat_vm_sync (jitp_block *b, void *addr, uint32_t len) { return sceKernelSyncVMDomain (b->id, addr, len); }

typedef struct { void *(*fn) (void *); void *arg; int core; SceUID done_evf; } tctx;
static int thread_entry (SceSize args, void *argp)
{
	(void) args; tctx *c = *(tctx **) argp;
	c->fn (c->arg);
	return 0;
}
int plat_pin_self (int core)
{
	int masks[] = USER_MASKS;
	if (core < 0 || core > 3) return 0;
	SceUID me = sceKernelGetThreadId ();
	return sceKernelChangeThreadCpuAffinityMask (me, masks[core]);
}
int plat_current_core_mask (void) { return sceKernelGetThreadCpuAffinityMask (sceKernelGetThreadId ()); }
int plat_current_cpu (void) { return sceKernelGetCpuId (); }

int plat_thread_start (jitp_thread *t, void *(*fn) (void *), void *arg, int core)
{
	static tctx ctxs[8]; static int ci; tctx *c = &ctxs[ci++ & 7];
	c->fn = fn; c->arg = arg; c->core = core;
	SceUID th = sceKernelCreateThread ("jitp-worker", thread_entry, 0x40, 64 * 1024, 0, core >= 0 ? ((int[])USER_MASKS)[core] : 0, NULL);
	if (th < 0) return th;
	t->h = (void *) (intptr_t) th;
	tctx *cp = c;
	return sceKernelStartThread (th, sizeof cp, &cp);
}
int plat_thread_join (jitp_thread *t, uint32_t timeout_ms)
{
	SceUID th = (SceUID) (intptr_t) t->h;
	SceUInt to = timeout_ms * 1000u;
	int rc = sceKernelWaitThreadEnd (th, NULL, timeout_ms ? &to : NULL);
	if (rc >= 0) sceKernelDeleteThread (th);
	return rc;
}
int plat_core3_probe (void)
{
	SceUID me = sceKernelGetThreadId ();
	int old = sceKernelGetThreadCpuAffinityMask (me);
	int rc = sceKernelChangeThreadCpuAffinityMask (me, CPU3_MASK);
	int ok = rc >= 0;
	sceKernelChangeThreadCpuAffinityMask (me, old ? old : 0x70000);   /* back to the user cores */
	return ok;
}
