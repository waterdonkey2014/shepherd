/* k10_cache.inc.c: SHEPHERD-VITA device kit #10, track cache (vita/notes/kit10-cache.md) -- the host side of the learning cache framework
 * (engine Runtime/K10Cache.cs): the card's free space for the store's free-space margin (owner 2026-10-16: the cache never lets the card drop
 * below a safety margin of free space). Included by host.c in every app; one export, shepherd_vita_k10_cache (a P/Invoke, GC-safe: one devctl).
 *   op 0: ux0's free space in MiB (sceIoDevctl 0x3001, the call kit #7's device profile makes), -1 when the card does not answer;
 *   op 1: ux0's size in MiB, -1 likewise;
 *   op 2: the engine's idle window (arg 1 = open, 0 = closed; the engine calls it once per change): kept for the host's / runtime's own
 *         writers -- shepherd_k10_window () answers 1 while a window is open (the boot's logos, the quotes, the menus, the open Settings menu;
 *         never a scene load or gameplay), 0 otherwise and in every app whose engine never opened one (SHEPHERD_K10_CACHE=0: always 0).
 * Under vlx the devctl stand-in answers 64 GiB / 32 GiB free (vita/vlx/vlx.c; fixed numbers, so a run never depends on the Mac's disk).
 * Nothing here runs unless the engine asks (the Settings rows, a cache write): with SHEPHERD_K10_CACHE=0 the host is kit #9's. */
#include <psp2/io/devctl.h>

static volatile int g_k10_window;
/* 1 while the engine's idle window is open (a card write there cannot stall a frame the player sees); any host / runtime code may ask */
int shepherd_k10_window (void) { return __atomic_load_n (&g_k10_window, __ATOMIC_ACQUIRE); }

int shepherd_vita_k10_cache (int op, int arg)
{
	if (op == 2) {
		__atomic_store_n (&g_k10_window, arg ? 1 : 0, __ATOMIC_RELEASE);
		return 0;
	}
	if (op == 0 || op == 1) {
		SceIoDevInfo di;
		memset (&di, 0, sizeof di);
		int r = sceIoDevctl ("ux0:", 0x3001, NULL, 0, &di, sizeof di);
		if (r < 0 || di.max_size <= 0)
			return -1;
		long long v = (op == 0 ? (long long) di.free_size : (long long) di.max_size) >> 20;
		return v > 0x7fffffffLL ? 0x7fffffff : (int) v;
	}
	return -1;
}
