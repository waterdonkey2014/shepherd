/*
 * Device kit #8.1 (2026-10-22): the engine module's LATE LIBRARIES -- linked into app0:module/shepherd.suprx only (build.sh
 * SHEPHERD_K8_LAUNCHER=1), never into an eboot.
 *
 * Why: on the owner's Vita both launcher apps (SHPD00193 / 00194) showed the Devolver logo at 40 ms, then the launcher's
 * sceKernelLoadStartModule (app0:module/shepherd.suprx) returned 0x8002D003 = SCE_KERNEL_ERROR_MODULEMGR_NO_LIB: a library the module
 * imports was not loaded at that moment. A main eboot may import a library that is loaded later -- its stubs are bound when the
 * library arrives -- so kit #7's eboots never noticed; a module loaded with sceKernelLoadStartModule is refused instead. Two of the
 * module's 28 imported libraries are known to arrive later:
 *   - SceShaccCg (11 functions): Sony's shader compiler, ur0:data/libshacccg.suprx, which vitaShaRK's shark_init loads inside vglInit
 *     (host.c gl_start), long after the module;
 *   - SceNet (5 functions): newlib's fd layer (lib_a-syscalls.o: read / write / close / [gs]etsockopt on a socket fd) imports them;
 *     its user module (libnet) loads only with sceSysmoduleLoadModule (SCE_SYSMODULE_NET), which nothing does. Never called: nothing
 *     can make a socket fd (check_vpk.py).
 * The rest (SceLibKernel, SceGxm, SceAudiodecUser, SceVideodecUser, taihen ...) are there when the launcher runs: kit #7's eboots
 * called them without loading anything first (the list: module-load-libs.txt, which check_vpk.py and vlx enforce).
 *
 * What: this file DEFINES those 16 functions, so the module imports neither library. Each is a 6-instruction trampoline that, on its
 * first call, finds the real function in the loaded library with taiGetModuleExportFunc (module "SceShaccCg" -- the name
 * libSceShaccCgExt itself looks up -- or else every loaded module by name) and jumps to it with the caller's registers and stack
 * untouched (any signature: the parameter-query functions have no vitasdk prototype). Found addresses are kept: vitaGL keeps
 * libshacccg loaded from vglInit to the end. A function that cannot be found answers 0 (SceShaccCg: "no output", which vitaGL
 * reports as a failed compile) or 0x80410109 (SceNet, as vlx's no-network stand-ins), and the END line counts it.
 * The eboot apps are unchanged: they still import both libraries the kit #7 way.
 */
#include <stdint.h>
#include <psp2/kernel/modulemgr.h>
#include <taihen.h>

#define SHACCCG_LIB 0xA05BBEBBu
#define NET_LIB     0x6BF8B2A2u

typedef struct {
	const char *name;      /* the import's function name (for the END line) */
	uint32_t libnid, funcnid;
	uintptr_t fn;          /* the real function once found (Thumb bit as exported) */
} K8Late;

static K8Late g_late [] = {
	{ "sceShaccCgGetFirstParameter",       SHACCCG_LIB, 0x17223BEBu, 0 },   /*  0 */
	{ "sceShaccCgInitializeCompileOptions", SHACCCG_LIB, 0x3B58AFA0u, 0 },  /*  1 */
	{ "sceShaccCgGetParameterName",        SHACCCG_LIB, 0x4595A388u, 0 },   /*  2 */
	{ "sceShaccCgGetNextParameter",        SHACCCG_LIB, 0x46FA0303u, 0 },   /*  3 */
	{ "sceShaccCgCompileProgram",          SHACCCG_LIB, 0x66814F35u, 0 },   /*  4 */
	{ "sceShaccCgSetDefaultAllocator",     SHACCCG_LIB, 0x6F01D573u, 0 },   /*  5 */
	{ "sceShaccCgReleaseCompiler",         SHACCCG_LIB, 0x95F57A23u, 0 },   /*  6 */
	{ "sceShaccCgInitializeCallbackList",  SHACCCG_LIB, 0xA8C2C1C8u, 0 },   /*  7 */
	{ "sceShaccCgDestroyCompileOutput",    SHACCCG_LIB, 0xAA82EF0Cu, 0 },   /*  8 */
	{ "sceShaccCgGetParameterBufferIndex", SHACCCG_LIB, 0xD4378DB1u, 0 },   /*  9 */
	{ "sceShaccCgGetParameterClass",       SHACCCG_LIB, 0xDF3DDCFDu, 0 },   /* 10 */
	{ "sceNetRecv",                        NET_LIB,     0x023643B7u, 0 },   /* 11 */
	{ "sceNetSetsockopt",                  NET_LIB,     0x065505CAu, 0 },   /* 12 */
	{ "sceNetSocketClose",                 NET_LIB,     0x29822B4Du, 0 },   /* 13 */
	{ "sceNetGetsockopt",                  NET_LIB,     0xBA652062u, 0 },   /* 14 */
	{ "sceNetSend",                        NET_LIB,     0xE3DD8CD9u, 0 },   /* 15 */
};
#define K8_LATE_N ((unsigned) (sizeof g_late / sizeof g_late [0]))

/* read by host.c's END line (k8_boot.inc.c k8_late_end_line) */
unsigned k8_late_bound_shacccg, k8_late_bound_net, k8_late_unresolved;
const char *k8_late_first_unresolved;

static int fail_shacccg (void) { return 0; }
static int fail_net (void) { return (int) 0x80410109; }

static uintptr_t find_export (uint32_t libnid, uint32_t funcnid)
{
	uintptr_t f = 0;
	if (libnid == SHACCCG_LIB && taiGetModuleExportFunc ("SceShaccCg", libnid, funcnid, &f) >= 0 && f)
		return f;
	SceUID ids [96];
	SceSize n = sizeof ids / sizeof ids [0];
	if (sceKernelGetModuleList (0xFF, ids, &n) < 0)
		return 0;
	for (SceSize i = 0; i < n && i < sizeof ids / sizeof ids [0]; i++) {
		SceKernelModuleInfo mi;
		mi.size = sizeof mi;
		if (sceKernelGetModuleInfo (ids [i], &mi) < 0)
			continue;
		f = 0;
		if (taiGetModuleExportFunc (mi.module_name, libnid, funcnid, &f) >= 0 && f)
			return f;
	}
	return 0;
}

/* called by every trampoline with its index; returns where to jump (never 0) */
uintptr_t k8_late_resolve (unsigned idx) __attribute__ ((used));
uintptr_t k8_late_resolve (unsigned idx)
{
	if (idx >= K8_LATE_N)
		return (uintptr_t) fail_shacccg;
	K8Late *e = &g_late [idx];
	if (e->fn)
		return e->fn;
	uintptr_t f = find_export (e->libnid, e->funcnid);
	if (f) {
		e->fn = f;   /* (two threads may both look it up: the same answer, written twice) */
		if (e->libnid == SHACCCG_LIB) k8_late_bound_shacccg++; else k8_late_bound_net++;
		return f;
	}
	k8_late_unresolved++;
	if (!k8_late_first_unresolved)
		k8_late_first_unresolved = e->name;
	return e->libnid == SHACCCG_LIB ? (uintptr_t) fail_shacccg : (uintptr_t) fail_net;
}

/* the trampolines: save the argument registers (r4 keeps the stack 8-byte aligned), ask k8_late_resolve, restore, jump */
#define K8_LATE_TRAMP(name, idx) \
	__asm__ (".syntax unified\n.text\n.balign 4\n.global " #name "\n.type " #name ", %function\n.thumb\n.thumb_func\n" #name ":\n" \
	         "\tpush {r0-r4, lr}\n\tmovs r0, #" #idx "\n\tbl k8_late_resolve\n\tmov r12, r0\n\tpop {r0-r4, lr}\n\tbx r12\n" \
	         ".size " #name ", . - " #name "\n");
K8_LATE_TRAMP (sceShaccCgGetFirstParameter, 0)
K8_LATE_TRAMP (sceShaccCgInitializeCompileOptions, 1)
K8_LATE_TRAMP (sceShaccCgGetParameterName, 2)
K8_LATE_TRAMP (sceShaccCgGetNextParameter, 3)
K8_LATE_TRAMP (sceShaccCgCompileProgram, 4)
K8_LATE_TRAMP (sceShaccCgSetDefaultAllocator, 5)
K8_LATE_TRAMP (sceShaccCgReleaseCompiler, 6)
K8_LATE_TRAMP (sceShaccCgInitializeCallbackList, 7)
K8_LATE_TRAMP (sceShaccCgDestroyCompileOutput, 8)
K8_LATE_TRAMP (sceShaccCgGetParameterBufferIndex, 9)
K8_LATE_TRAMP (sceShaccCgGetParameterClass, 10)
K8_LATE_TRAMP (sceNetRecv, 11)
K8_LATE_TRAMP (sceNetSetsockopt, 12)
K8_LATE_TRAMP (sceNetSocketClose, 13)
K8_LATE_TRAMP (sceNetGetsockopt, 14)
K8_LATE_TRAMP (sceNetSend, 15)
