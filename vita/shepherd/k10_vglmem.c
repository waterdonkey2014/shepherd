/* SHEPHERD-VITA device kit #10, track memory -- review fix R1 (vita/notes/kit10-memory.md "Review fixes"): vitaGL and newlib's heap when
 * newlib spans two blocks (the shared pool, SHEPHERD_K10_POOL=1, or its fallback's second block). Linked into the GL eboots and engine
 * modules only (vita/shepherd/build.sh: this object + -Wl,--wrap=vgl_free,--wrap=vgl_realloc,--wrap=vgl_malloc_usable_size); a link
 * without the --wrap flags fails on the __real_ references below (never silently unwrapped). VLX links its recording stand-in for vitaGL
 * instead (vita/vlx/glrec_vlx.c: no vitaGL, no pools) -- no gate under VLX can see what this file is for; the device run decides.
 *
 * Why. vitaGL's vgl_mem_init (libvitaGL.a mem_utils.o) calls malloc (1), looks the pointer's memblock up (sceKernelGetMemBlockInfoByAddr),
 * GPU-maps that block (sceGxmMapMemory) and records it as its VGL_MEM_EXTERNAL ("newlib") range. vgl_free / vgl_realloc /
 * vgl_malloc_usable_size pick the pool by the pointer's address: in that range -> newlib's free / realloc / malloc_usable_size; outside every
 * range -> mempool_mspace [255], a word past the array (zero in the k10b maps: a free that does nothing, a realloc that answers NULL).
 * vglMalloc / vglCalloc / vglMemalign / vglRealloc try newlib FIRST (shader sources, the compiler's work, texture staging, ...), and the GPU
 * allocators (gpu_alloc_mapped_aligned_*) fall back to newlib memory (use_extra_mem, on by default) when the VRAM / RAM / PHYCONT / CDLG pools
 * are full. Kit #9: newlib is one 116 MiB block = vitaGL's range (device G1 line "newlib 0/118784"). The pool: malloc (1) at vglInit comes
 * from the 2 MiB boot block (VLX "MEM before-gl ... pool: newlib top 0 KiB"), so every later newlib chunk vitaGL freed would have leaked and
 * any newlib chunk it gave the GPU would have been unmapped.
 *
 * The fix. (1) The three wrappers hand a pointer newlib owns (host.c's shepherd_k10_newlib_owns: the boot block, the pool's newlib side up to
 * its floor, the second block) to newlib -- what vitaGL itself does for its EXTERNAL range -- and everything else to vitaGL. Outside the pool
 * shepherd_k10_newlib_owns answers 0: vitaGL's own code path (kit #9). (2) After vglInit, shepherd_k10_vgl_after_init GPU-maps newlib's other
 * block (its whole memblock, as vitaGL maps its own newlib block); if that map fails, vitaGL's newlib fallback for GPU memory is turned off
 * (vglUseExtraMem (GL_FALSE): a full pool then fails that allocation instead of handing the GPU unmapped memory) and the line is a WARN. */
#include <stdarg.h>
#include <stdlib.h>
#include <malloc.h>
#include <stdio.h>
#include <string.h>
#include <psp2/kernel/sysmem.h>
#include <psp2/gxm.h>
#include <vitaGL.h>

int shepherd_k10_newlib_owns (const void *p);   /* host.c (k10_newlib.inc.c) */

/* libvitaGL.a mem_utils.o (not in vitaGL.h) */
void __real_vgl_free (void *p);
void *__real_vgl_realloc (void *p, size_t size);
size_t __real_vgl_malloc_usable_size (void *p);
int vgl_mem_get_type_by_addr (void *p);

void __wrap_vgl_free (void *p);
void __wrap_vgl_free (void *p)
{
	if (shepherd_k10_newlib_owns (p))
		free (p);
	else
		__real_vgl_free (p);
}

void *__wrap_vgl_realloc (void *p, size_t size);
void *__wrap_vgl_realloc (void *p, size_t size)
{
	return shepherd_k10_newlib_owns (p) ? realloc (p, size) : __real_vgl_realloc (p, size);
}

size_t __wrap_vgl_malloc_usable_size (void *p);
size_t __wrap_vgl_malloc_usable_size (void *p)
{
	return shepherd_k10_newlib_owns (p) ? malloc_usable_size (p) : __real_vgl_malloc_usable_size (p);
}

static unsigned put (char *out, unsigned n, unsigned o, const char *fmt, ...) __attribute__ ((format (printf, 4, 5)));
static unsigned put (char *out, unsigned n, unsigned o, const char *fmt, ...)
{
	if (o + 1 >= n)
		return o;
	va_list ap;
	va_start (ap, fmt);
	int r = vsnprintf (out + o, n - o, fmt, ap);
	va_end (ap);
	if (r < 0)
		return o;
	return o + (unsigned) r < n ? o + (unsigned) r : n - 1;
}

/* host.c's gl_start (k10_gl_after_init), right after vglInit, only when newlib spans two blocks: pool_block / fb_block = an address inside
 * newlib's other block (NULL: none). 1 = every such block GPU-mapped (or vitaGL's own already); 0 = a map failed (vitaGL's newlib fallback
 * for GPU memory is off now). out: the K10 POOL vitaGL line. */
int shepherd_k10_vgl_after_init (const void *pool_block, const void *fb_block, char *out, unsigned n);
int shepherd_k10_vgl_after_init (const void *pool_block, const void *fb_block, char *out, unsigned n)
{
	int ok = 1;
	unsigned o = put (out, n, 0, "vitaGL's own newlib range %u KiB (the memblock of its malloc (1) at vglInit; kit #9: the whole heap)",
			  (unsigned) (vglMemTotal (VGL_MEM_EXTERNAL) / 1024));
	const void *b [2] = { pool_block, fb_block };
	static const char *const nm [2] = { "the pool block", "the second newlib block" };
	for (int i = 0; i < 2; i++) {
		if (!b [i])
			continue;
		if (vgl_mem_get_type_by_addr ((void *) b [i]) == VGL_MEM_EXTERNAL) {
			o = put (out, n, o, "; %s is vitaGL's own range (vitaGL mapped it)", nm [i]);
			continue;
		}
		SceKernelMemBlockInfo info;
		memset (&info, 0, sizeof (info));
		info.size = sizeof (info);
		int r = sceKernelGetMemBlockInfoByAddr ((void *) b [i], &info);
		if (r >= 0)
			r = sceGxmMapMemory (info.mappedBase, info.mappedSize, SCE_GXM_MEMORY_ATTRIB_RW);
		o = put (out, n, o, "; %s %p (%u KiB) GPU-mapped: %s (0x%08x)", nm [i], info.mappedBase, (unsigned) (info.mappedSize / 1024),
			 r < 0 ? "FAILED" : "ok", (unsigned) r);
		if (r < 0)
			ok = 0;
	}
	o = put (out, n, o, "; vitaGL's frees / reallocs of newlib chunks outside its range go to newlib (the host's --wrap of vgl_free / vgl_realloc / "
		 "vgl_malloc_usable_size)");
	if (!ok) {
		vglUseExtraMem (GL_FALSE);
		o = put (out, n, o, "; vitaGL's newlib fallback for GPU memory is OFF now (vglUseExtraMem): a full VRAM / RAM / PHYCONT pool fails that "
			 "allocation instead of handing the GPU unmapped memory");
	}
	return ok;
}
