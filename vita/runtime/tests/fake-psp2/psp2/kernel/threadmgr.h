/* host-side fake of the vitasdk thread manager calls used by the Vita runtime code under test (test_yield.c) */
#pragma once
#include <stdint.h>
typedef int SceUID;
typedef unsigned int SceUInt;
SceUID sceKernelGetThreadId (void);
int sceKernelDelayThread (SceUInt delay);
