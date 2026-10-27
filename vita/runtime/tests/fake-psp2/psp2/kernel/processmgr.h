/* host-side fake of the vitasdk process manager clock (test_yield.c) */
#pragma once
#include <stdint.h>
typedef uint64_t SceUInt64;
SceUInt64 sceKernelGetProcessTimeWide (void);
