/* SHEPHERD-VITA mac-check: the slice of the vitasdk API that vita/shepherd/host.c uses, implemented on macOS
 * (vita_mac_compat.c), so the unchanged host runs the real boot on the Mac Mono runtime. Device paths are mapped:
 * "app0:..." -> $MAC_APP0/..., "ux0:..." -> $MAC_UX0/... . Memory numbers are the Mac process's (task_info / malloc
 * zones); the arena and the Mono counters of the Vita fork do not exist here and report what they can. */
#pragma once
#include <stddef.h>
#include <stdint.h>
#include <stdio.h>
#include <malloc/malloc.h>

typedef int SceUID;
typedef unsigned int SceSize;
typedef unsigned long long SceUInt64;
typedef int64_t SceOff;
typedef int SceMode;

#define SCE_O_RDONLY 0x0001
#define SCE_O_WRONLY 0x0002
#define SCE_O_RDWR 0x0003
#define SCE_O_APPEND 0x0100
#define SCE_O_CREAT 0x0200
#define SCE_O_TRUNC 0x0400
#define SCE_S_IFDIR 0x1000
#define SCE_S_ISDIR(m) (((m) & 0xF000) == SCE_S_IFDIR)

typedef struct { SceMode st_mode; unsigned st_attr; SceOff st_size; } SceIoStat;
typedef struct { SceSize size; SceSize size_user; SceSize size_cdram; SceSize size_phycont; } SceKernelFreeMemorySizeInfo;
typedef struct { uint64_t timeStamp; unsigned buttons; unsigned char lx, ly, rx, ry; } SceCtrlData;
/* kit 4 (host.c shepherd_vita_pin raises a worker to the main thread's priority): only the fields host.c reads */
typedef struct { SceSize size; int initPriority; int currentPriority; } SceKernelThreadInfo;
#define SCE_CTRL_SELECT 0x0001
#define SCE_CTRL_START 0x0008
#define SCE_CTRL_MODE_ANALOG 1
typedef enum { SCE_KERNEL_POWER_TICK_DEFAULT = 0 } SceKernelPowerTickType;
#define SCE_KERNEL_MEMBLOCK_TYPE_USER_RW 0x0c20d060

SceUID sceIoOpen (const char *file, int flags, SceMode mode);
int sceIoRead (SceUID fd, void *data, SceSize size);
int sceIoWrite (SceUID fd, const void *data, SceSize size);
int sceIoClose (SceUID fd);
int sceIoMkdir (const char *dir, SceMode mode);
int sceIoGetstat (const char *file, SceIoStat *stat);
int sceIoRemove (const char *file);
int sceIoRename (const char *oldname, const char *newname);
int sceClibPrintf (const char *fmt, ...);
SceUInt64 sceKernelGetProcessTimeWide (void);
int sceKernelDelayThread (unsigned usec);
int sceKernelGetFreeMemorySize (SceKernelFreeMemorySizeInfo *info);
SceUID sceKernelAllocMemBlock (const char *name, int type, SceSize size, void *opt);
int sceKernelGetMemBlockBase (SceUID uid, void **base);
SceUID sceKernelCreateSema (const char *name, unsigned attr, int init, int max, void *opt);
int sceKernelWaitSema (SceUID sema, int signal, unsigned *timeout);
int sceKernelSignalSema (SceUID sema, int signal);
SceUID sceKernelGetThreadId (void);
int sceKernelChangeThreadCpuAffinityMask (SceUID thid, int mask);   /* kit 3 core pinning: remembered, nothing pinned on the Mac */
int sceKernelGetThreadCpuAffinityMask (SceUID thid);
int sceKernelGetThreadInfo (SceUID thid, SceKernelThreadInfo *info);   /* kit 4: the Vita's default user priority (160) unless changed */
int sceKernelChangeThreadPriority (SceUID thid, int priority);          /* kit 4: remembered per thread, nothing changes on the Mac */
int sceKernelExitProcess (int res);
int sceKernelPowerTick (SceKernelPowerTickType type);
int sceCtrlPeekBufferPositive (int port, SceCtrlData *pad, int count);
int sceCtrlSetSamplingMode (int mode);
/* psp2/touch.h (kit #5: host.c shepherd_vita_touch_read, the front panel; the layouts of the vitasdk header) */
typedef unsigned int SceUInt32;
typedef struct { uint8_t id, force; int16_t x, y; uint8_t reserved [8]; uint16_t info; } SceTouchReport;
typedef struct { SceUInt64 timeStamp; uint32_t status, reportNum; SceTouchReport report [8]; } SceTouchData;
typedef struct { int16_t minAaX, minAaY, maxAaX, maxAaY, minDispX, minDispY, maxDispX, maxDispY; uint8_t minForce, maxForce, reserved [30]; } SceTouchPanelInfo;
typedef enum { SCE_TOUCH_PORT_FRONT = 0, SCE_TOUCH_PORT_BACK = 1 } SceTouchPortType;
typedef enum { SCE_TOUCH_SAMPLING_STATE_STOP = 0, SCE_TOUCH_SAMPLING_STATE_START = 1 } SceTouchSamplingState;
int sceTouchSetSamplingState (SceUInt32 port, SceTouchSamplingState state);
int sceTouchGetPanelInfo (SceUInt32 port, SceTouchPanelInfo *info);
int sceTouchPeek (SceUInt32 port, SceTouchData *data, SceUInt32 nBufs);
int psvDebugScreenInit (void);
int psvDebugScreenFinish (void);
int psvDebugScreenPuts (const char *text);
/* psp2/power.h (every host build sets the clocks at start since 2026-10-13): the Mac has no clocks; the stand-ins remember what was
 * set (launch values = the device's: 333/222/0/111) */
int scePowerGetArmClockFrequency (void);
int scePowerGetBusClockFrequency (void);
int scePowerGetGpuClockFrequency (void);
int scePowerGetGpuXbarClockFrequency (void);
int scePowerSetArmClockFrequency (int mhz);
int scePowerSetBusClockFrequency (int mhz);
int scePowerSetGpuClockFrequency (int mhz);
int scePowerSetGpuXbarClockFrequency (int mhz);

struct mallinfo { int arena, ordblks, smblks, hblks, hblkhd, usmblks, fsmblks, uordblks, fordblks, keepcost; };
struct mallinfo mallinfo (void);
