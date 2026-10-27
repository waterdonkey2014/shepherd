/* host-side fake of the vitasdk memblock API, for unit-testing the Vita runtime code on the Mac */
#pragma once
#include <stddef.h>
typedef int SceUID;
#define SCE_KERNEL_MEMBLOCK_TYPE_USER_RW 0x0c20d060
SceUID sceKernelAllocMemBlock (const char *name, int type, size_t size, void *opt);
int sceKernelGetMemBlockBase (SceUID uid, void **base);
int sceKernelFreeMemBlock (SceUID uid);
