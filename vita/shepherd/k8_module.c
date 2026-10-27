/* SHEPHERD-VITA device kit #8, track boot (vita/notes/kit8-boot.md): the ENGINE MODULE's entry (a launcher build's app0:module/shepherd.suprx:
 * host.c + the Mono runtime + every AOT image, linked as a user module instead of the eboot; vita/shepherd/launcher.c loads it).
 *
 * module_start only hands its entry point back to the launcher (k8_launch.h): the kernel may run it on a thread of its own with a small
 * stack, so nothing else happens there. The launcher then calls k8_entry on its main thread, which does what vitasdk's crt0 _start does for
 * an eboot -- newlib's heap / reentrancy / malloc lock / file descriptors (_init_vita_newlib's four steps: crt0.o is not linked into a module)
 * and the constructors (__libc_init_array) -- and runs host.c's main exactly as kit #7's eboot did. g_k8_launch tells the host it runs under
 * the launcher (NULL in a kit #7-style eboot). The newlib heap is this module's _newlib_heap_size_user (host.c), allocated here, after the
 * module itself was loaded: the same order of memory as kit #7's eboot start (the eboot's segments, then the heap). */
#include <psp2/kernel/modulemgr.h>
#include "k8_launch.h"

extern int main (int argc, char *argv []);
extern void _init_vita_heap (void), _init_vita_reent (void), _init_vita_malloc (void), _init_vita_io (void);
extern void _free_vita_heap (void), _free_vita_reent (void), _free_vita_malloc (void), _free_vita_io (void);
extern void __libc_init_array (void);

/* crt0.o's two newlib helpers (not linked into a module): the same four steps in the same order; newlib's exit path calls the second */
void _init_vita_newlib (void);
void _init_vita_newlib (void) { _init_vita_heap (); _init_vita_reent (); _init_vita_malloc (); _init_vita_io (); }
void _free_vita_newlib (void);
void _free_vita_newlib (void) { _free_vita_io (); _free_vita_malloc (); _free_vita_reent (); _free_vita_heap (); }

const K8Launch *g_k8_launch;

static int k8_entry (const K8Launch *l)
{
	g_k8_launch = l;
	_init_vita_newlib ();
	__libc_init_array ();
	static char arg0 [] = "shepherd";
	static char *argv [] = { arg0, 0 };
	return main (1, argv);
}

typedef struct { K8Launch *launch; } K8ModArg;   /* = launcher.c's */

int module_start (SceSize args, void *argp) __attribute__ ((used));
int module_start (SceSize args, void *argp)
{
	if (args >= sizeof (K8ModArg) && argp) {
		K8Launch *l = ((K8ModArg *) argp)->launch;
		if (l && l->magic == K8_LAUNCH_MAGIC && l->version == K8_LAUNCH_VERSION && l->size == sizeof (K8Launch))
			l->entry = k8_entry;
	}
	return SCE_KERNEL_START_SUCCESS;
}
int module_stop (SceSize args, void *argp) __attribute__ ((used));
int module_stop (SceSize args, void *argp) { (void) args; (void) argp; return SCE_KERNEL_STOP_SUCCESS; }
