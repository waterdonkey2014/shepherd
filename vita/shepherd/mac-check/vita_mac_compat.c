/* SHEPHERD-VITA mac-check: macOS implementations of the vitasdk calls host.c makes, and of the Vita fork's
 * mono-vita.h API (absent from Microsoft's osx-arm64 Mono), so vita/shepherd/host.c runs unchanged on the Mac. */
#include "vita_mac_compat.h"
#include <mono/utils/mono-vita.h>

#include <dispatch/dispatch.h>
#include <errno.h>
#include <fcntl.h>
#include <mach/mach.h>
#include <pthread.h>
#include <stdarg.h>
#include <stdlib.h>
#include <string.h>
#include <sys/stat.h>
#include <time.h>
#include <unistd.h>

static const char *map_path (const char *in, char *out, size_t n)
{
	const char *app0 = getenv ("MAC_APP0"), *ux0 = getenv ("MAC_UX0"), *ur0 = getenv ("MAC_UR0");
	if (!strncmp (in, "app0:", 5) && app0)
		snprintf (out, n, "%s/%s", app0, in [5] == '/' ? in + 6 : in + 5);
	else if (!strncmp (in, "ux0:", 4) && ux0)
		snprintf (out, n, "%s/%s", ux0, in [4] == '/' ? in + 5 : in + 4);
	else if (!strncmp (in, "ur0:", 4) && ur0)   /* Stage B GL host: the libshacccg presence check */
		snprintf (out, n, "%s/%s", ur0, in [4] == '/' ? in + 5 : in + 4);
	else
		snprintf (out, n, "%s", in);
	return out;
}

SceUID sceIoOpen (const char *file, int flags, SceMode mode)
{
	char p [1024];
	int f = (flags & 3) == SCE_O_RDONLY ? O_RDONLY : (flags & 3) == SCE_O_WRONLY ? O_WRONLY : O_RDWR;
	if (flags & SCE_O_APPEND) f |= O_APPEND;
	if (flags & SCE_O_CREAT) f |= O_CREAT;
	if (flags & SCE_O_TRUNC) f |= O_TRUNC;
	int fd = open (map_path (file, p, sizeof p), f, 0644);
	return fd < 0 ? (SceUID) (0x80010000u | (unsigned) errno) : fd;
}
int sceIoRead (SceUID fd, void *data, SceSize size) { return (int) read (fd, data, size); }
int sceIoWrite (SceUID fd, const void *data, SceSize size) { return (int) write (fd, data, size); }
int sceIoClose (SceUID fd) { return close (fd); }
int sceIoMkdir (const char *dir, SceMode mode) { char p [1024]; return mkdir (map_path (dir, p, sizeof p), 0755) ? (int) (0x80010000u | (unsigned) errno) : 0; }
int sceIoRemove (const char *file) { char p [1024]; return unlink (map_path (file, p, sizeof p)) ? (int) (0x80010000u | (unsigned) errno) : 0; }
int sceIoRename (const char *oldname, const char *newname)
{
	char a [1024], b [1024];
	return rename (map_path (oldname, a, sizeof a), map_path (newname, b, sizeof b)) ? (int) (0x80010000u | (unsigned) errno) : 0;
}
int sceIoGetstat (const char *file, SceIoStat *st)
{
	char p [1024];
	struct stat s;
	if (stat (map_path (file, p, sizeof p), &s) != 0)
		return (int) (0x80010000u | (unsigned) errno);
	st->st_mode = S_ISDIR (s.st_mode) ? SCE_S_IFDIR : 0x2000;
	st->st_attr = 0;
	st->st_size = s.st_size;
	return 0;
}
int sceClibPrintf (const char *fmt, ...)
{
	if (!getenv ("MAC_ECHO"))
		return 0;
	va_list ap;
	va_start (ap, fmt);
	int r = vfprintf (stderr, fmt, ap);
	va_end (ap);
	return r;
}
SceUInt64 sceKernelGetProcessTimeWide (void)
{
	struct timespec ts;
	clock_gettime (CLOCK_MONOTONIC, &ts);
	return (SceUInt64) ts.tv_sec * 1000000u + (SceUInt64) ts.tv_nsec / 1000u;
}
int sceKernelDelayThread (unsigned usec) { usleep (usec); return 0; }

/* the Vita's ~365 MiB budget minus the eboot and the newlib heap, so the partition line computes what the device would */
int sceKernelGetFreeMemorySize (SceKernelFreeMemorySizeInfo *info)
{
	unsigned newlib = 0;
	extern unsigned int _newlib_heap_size_user;
	newlib = _newlib_heap_size_user;
	info->size_user = (SceSize) (365u * 1024 * 1024 - 40u * 1024 * 1024 - newlib);
	info->size_cdram = 112u * 1024 * 1024;
	info->size_phycont = 26u * 1024 * 1024;
	return 0;
}

static char fake_block [1 << 16] __attribute__((aligned (4096)));
SceUID sceKernelAllocMemBlock (const char *name, int type, SceSize size, void *opt) { return 0x4000; }
int sceKernelGetMemBlockBase (SceUID uid, void **base) { *base = fake_block; return 0; }

static dispatch_semaphore_t semas [16];
static int nsemas;
SceUID sceKernelCreateSema (const char *name, unsigned attr, int init, int max, void *opt)
{
	semas [nsemas] = dispatch_semaphore_create (init);
	return nsemas++;
}
int sceKernelWaitSema (SceUID sema, int signal, unsigned *timeout)
{
	dispatch_semaphore_wait (semas [sema], DISPATCH_TIME_FOREVER);
	return 0;
}
int sceKernelSignalSema (SceUID sema, int signal)
{
	dispatch_semaphore_signal (semas [sema]);
	return 0;
}
SceUID sceKernelGetThreadId (void) { uint64_t t; pthread_threadid_np (NULL, &t); return (SceUID) t; }
/* kit 3 core pinning (host.c shepherd_vita_pin): the Mac pins nothing; the last mask set on a thread is read back as the Vita would */
static __thread int g_affinity;
int sceKernelChangeThreadCpuAffinityMask (SceUID thid, int mask) { (void) thid; g_affinity = mask; return 0; }
int sceKernelGetThreadCpuAffinityMask (SceUID thid) { (void) thid; return g_affinity; }
/* kit 4 (host.c shepherd_vita_pin: a worker pinned to CPU3 / USER_2 runs at least at the main thread's priority): the Mac has no thread
 * priorities; every thread reads the Vita's default user priority (160) until a change is remembered for it */
static struct { SceUID t; int p; } g_prio [64];
static int g_nprio;
static pthread_mutex_t g_prio_mu = PTHREAD_MUTEX_INITIALIZER;
int sceKernelGetThreadInfo (SceUID thid, SceKernelThreadInfo *info)
{
	int p = 160;
	if (!thid) thid = sceKernelGetThreadId ();
	pthread_mutex_lock (&g_prio_mu);
	for (int i = 0; i < g_nprio; i++) if (g_prio [i].t == thid) p = g_prio [i].p;
	pthread_mutex_unlock (&g_prio_mu);
	info->initPriority = 160;
	info->currentPriority = p;
	return 0;
}
int sceKernelChangeThreadPriority (SceUID thid, int priority)
{
	if (!thid) thid = sceKernelGetThreadId ();
	pthread_mutex_lock (&g_prio_mu);
	int i = 0;
	while (i < g_nprio && g_prio [i].t != thid) i++;
	if (i == g_nprio && g_nprio < 64) g_nprio++;
	if (i < 64) { g_prio [i].t = thid; g_prio [i].p = priority; }
	pthread_mutex_unlock (&g_prio_mu);
	return 0;
}
int sceKernelExitProcess (int res) { fflush (NULL); _exit (res); }
int sceKernelPowerTick (SceKernelPowerTickType type) { return 0; }
/* The controls. V6 host: nothing pressed (0 samples). Stage B GL host: MAC_CTRL="call:hexbuttons[:holdCalls],..." scripts the
 * Vita's controls by call count (the engine reads the pad once per frame, so a call number is a frame number after the first
 * frame), with the sticks centred (128) -- the rehearsal's test of the native pad path (VitaPad SHEPHERD_VITA_PAD=1). */
int sceCtrlPeekBufferPositive (int port, SceCtrlData *pad, int count)
{
	static unsigned calls;
	memset (pad, 0, sizeof (*pad));
	const char *script = getenv ("MAC_CTRL");
	if (!script)
		return 0;
	pad->lx = pad->ly = pad->rx = pad->ry = 128;
	unsigned now = ++calls;
	for (const char *p = script; p && *p;) {
		unsigned at = 0, buttons = 0, hold = 6;
		int n = sscanf (p, "%u:%x:%u", &at, &buttons, &hold);
		if (n >= 2 && now >= at && now < at + (n == 3 ? hold : 6))
			pad->buttons |= buttons;
		p = strchr (p, ',');
		if (p)
			p++;
	}
	pad->timeStamp = sceKernelGetProcessTimeWide ();
	return 1;
}
int sceCtrlSetSamplingMode (int mode) { return 0; }
/* kit #5: the front touch panel. Nothing touched (one sample, no finger) unless MAC_TOUCH="call:x,y[:holdCalls],..." scripts a finger in
 * the panel's report space (1920 x 1088) by call count, like MAC_CTRL (one call per engine frame). */
int sceTouchSetSamplingState (SceUInt32 port, SceTouchSamplingState state) { return port <= 1 ? 0 : (int) 0x80350001; }
int sceTouchGetPanelInfo (SceUInt32 port, SceTouchPanelInfo *info)
{
	if (!info || port > 1) return (int) 0x80350001;
	memset (info, 0, sizeof (*info));
	info->maxAaX = info->maxDispX = 1919; info->maxAaY = info->maxDispY = port ? 889 : 1087; info->minForce = 1; info->maxForce = 128;
	return 0;
}
int sceTouchPeek (SceUInt32 port, SceTouchData *data, SceUInt32 nBufs)
{
	static unsigned calls;
	if (!data || nBufs < 1 || port > 1) return (int) 0x80350001;
	memset (data, 0, sizeof (*data));
	data->timeStamp = sceKernelGetProcessTimeWide ();
	const char *script = port == 0 ? getenv ("MAC_TOUCH") : NULL;
	unsigned now = ++calls;
	for (const char *p = script; p && *p;) {
		unsigned at = 0, x = 0, y = 0, hold = 1;
		int n = sscanf (p, "%u:%u,%u:%u", &at, &x, &y, &hold);
		if (n >= 3 && now >= at && now < at + (n == 4 ? hold : 1)) {
			data->reportNum = 1; data->report [0].id = 1; data->report [0].force = 64; data->report [0].x = (int16_t) x; data->report [0].y = (int16_t) y;
		}
		p = strchr (p, ';');
		if (p)
			p++;
	}
	return 1;
}
int psvDebugScreenInit (void) { return -1; }   /* no screen: log lines go to menu.txt (and stderr with MAC_ECHO) */
int psvDebugScreenFinish (void) { return 0; }
int psvDebugScreenPuts (const char *text) { return 0; }
/* clocks: nothing to set on the Mac; a stand-in that remembers what was set, so the host's CLK lines read back what it asked for.
 * Launch values = what the PCH-2000 reported at launch (probe/physbench 2026-10-09: arm 333, bus 222, gpu 0, xbar 111). */
static int g_clk [4] = { 333, 222, 0, 111 };
int scePowerGetArmClockFrequency (void) { return g_clk [0]; }
int scePowerGetBusClockFrequency (void) { return g_clk [1]; }
int scePowerGetGpuClockFrequency (void) { return g_clk [2]; }
int scePowerGetGpuXbarClockFrequency (void) { return g_clk [3]; }
int scePowerSetArmClockFrequency (int mhz) { g_clk [0] = mhz; return 0; }
int scePowerSetBusClockFrequency (int mhz) { g_clk [1] = mhz; return 0; }
int scePowerSetGpuClockFrequency (int mhz) { g_clk [2] = mhz; return 0; }
int scePowerSetGpuXbarClockFrequency (int mhz) { g_clk [3] = mhz; return 0; }

unsigned int __malloc_max_sbrked_mem;
struct mallinfo mallinfo (void)
{
	malloc_statistics_t st;
	malloc_zone_statistics (NULL, &st);
	struct mallinfo m = { 0 };
	m.uordblks = (int) st.size_in_use;
	m.arena = (int) st.size_allocated;
	/* newlib's __malloc_max_sbrked_mem is the high-water mark of the sbrk'd size; the Mac analogue is the zone's
	 * size_allocated (max_size_in_use stays 0 here), sampled whenever the host asks */
	if ((unsigned) st.size_allocated > __malloc_max_sbrked_mem)
		__malloc_max_sbrked_mem = (unsigned) st.size_allocated;
	return m;
}

/* ---- the fork's mono-vita.h API: not in Microsoft's Mono */
static size_t arena_size;
void mono_vita_register_static_symbols (const MonoVitaStaticSymbol *symbols, int count) { }
void *mono_vita_lookup_static_symbol (const char *name) { return NULL; }   /* -> Mono's normal native-library probing */
int mono_vita_vm_arena_init (void *base, size_t size) { arena_size = size; return 0; }
void mono_vita_vm_arena_set_budget (size_t size) { }
static void stats (MonoVitaArenaStats *s)
{
	memset (s, 0, sizeof (*s));
	s->arena_size = arena_size;
	task_vm_info_data_t vm;
	mach_msg_type_number_t n = TASK_VM_INFO_COUNT;
	if (task_info (mach_task_self (), TASK_VM_INFO, (task_info_t) &vm, &n) == KERN_SUCCESS)
		s->used = s->peak = (size_t) vm.phys_footprint;   /* the whole process: there is no arena on the Mac */
}
void mono_vita_vm_arena_get_stats (MonoVitaArenaStats *s) { stats (s); }
int mono_vita_vm_arena_try_get_stats (MonoVitaArenaStats *s) { stats (s); return 0; }
void mono_vita_vm_arena_set_log (MonoVitaArenaLogFn fn, size_t step) { }
void mono_vita_set_default_thread_stack_size (size_t size) { }
void mono_vita_set_heap_limit_callback (MonoVitaHeapLimitFn fn) { }
unsigned mono_vita_heap_limit_count (void) { return 0; }
void mono_vita_get_runtime_stats (MonoVitaRuntimeStats *s) { memset (s, 0, sizeof (*s)); }
void mono_vita_get_yield_stats (uint64_t *c, uint64_t *d, uint64_t *b, uint64_t *n) { if (c) *c = 0; if (d) *d = 0; if (b) *b = 0; if (n) *n = 0; }   /* (Mac Mono: Thread.Yield is sched_yield) */

/* ---- build.sh's generated tables: no static AOT modules, no static symbols on the Mac */
void shepherd_register_aot_modules (void) { }
const char *const shepherd_aot_module_names [] = { NULL };
const MonoVitaStaticSymbol shepherd_static_symbols [] = { { NULL, NULL } };
const int shepherd_static_symbols_count = 0;
