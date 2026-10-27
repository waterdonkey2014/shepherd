/* SHEPHERD-VITA device kit #8, track boot (vita/notes/kit8-boot.md): the Vita's hardware H.264 decoder (SceAvcdec through SceVideodec,
 * loaded with sceSysmoduleLoadModule (SCE_SYSMODULE_AVCDEC)) for the .shv clips (k8_shv.h): one access unit in, one RGBA picture out,
 * written by the decoder straight into the caller's buffer (a display frame buffer for the launcher's splash; a texture for the engine).
 * No libc (the launcher has none): Sce* calls only. Every buffer the decoder touches lives in physically contiguous memory (CDRAM): the
 * decoder's work memory (sized by sceAvcdecQueryDecoderMemSize), the access units (the .shv read into CDRAM), the output pictures.
 * The colour conversion is the decoder's own (cscCoefficient 0); splash_pack.py converts the clips to BT.601 limited range for it. */
#ifndef SHEPHERD_K8_AVC_H
#define SHEPHERD_K8_AVC_H

#include <psp2/videodec.h>
#include <psp2/sysmodule.h>
#include <psp2/kernel/sysmem.h>
#include <psp2/kernel/clib.h>

typedef struct K8Avc {
	SceAvcdecCtrl ctrl;
	SceUID mem;
	unsigned mem_bytes, w, h, refs;
	int sysmod, lib, created;
	int rc;               /* the failing call's return code */
	const char *why;      /* static text: which step failed (0 = none) */
} K8Avc;

static inline int k8avc_fail (K8Avc *d, const char *why, int rc) { d->why = why; d->rc = rc; return rc < 0 ? rc : -1; }

/* step 1: the decoder module + the library; returns the work memory the decoder needs (bytes, > 0) or < 0 (d->why / d->rc) */
static inline int k8avc_prepare (K8Avc *d, unsigned w, unsigned h, unsigned refs)
{
	sceClibMemset (d, 0, sizeof *d);
	d->mem = -1; d->w = w; d->h = h; d->refs = refs;
	int r = sceSysmoduleLoadModule (SCE_SYSMODULE_AVCDEC);
	if (r < 0) return k8avc_fail (d, "the decoder module (SCE_SYSMODULE_AVCDEC) did not load", r);
	d->sysmod = 1;
	SceVideodecQueryInitInfoHwAvcdec init;
	sceClibMemset (&init, 0, sizeof init);
	init.size = sizeof init; init.horizontal = w; init.vertical = h; init.numOfRefFrames = refs; init.numOfStreams = 1;
	r = sceVideodecInitLibrary (SCE_VIDEODEC_TYPE_HW_AVCDEC, &init);
	if (r < 0) return k8avc_fail (d, "sceVideodecInitLibrary refused", r);
	d->lib = 1;
	SceAvcdecQueryDecoderInfo q;
	sceClibMemset (&q, 0, sizeof q);
	q.horizontal = w; q.vertical = h; q.numOfRefFrames = refs;
	SceAvcdecDecoderInfo di;
	sceClibMemset (&di, 0, sizeof di);
	r = sceAvcdecQueryDecoderMemSize (SCE_VIDEODEC_TYPE_HW_AVCDEC, &q, &di);
	if (r < 0 || !di.frameMemSize) return k8avc_fail (d, "sceAvcdecQueryDecoderMemSize refused", r);
	d->mem_bytes = (di.frameMemSize + 0xFFFFFu) & ~0xFFFFFu;
	return (int) d->mem_bytes;
}
/* step 2: create the decoder on work memory the caller owns (work: physically contiguous, >= the prepared size), 0 = ready */
static inline int k8avc_create (K8Avc *d, void *work, unsigned bytes)
{
	if (!work || bytes < d->mem_bytes) return k8avc_fail (d, "no work memory for the decoder", -1);
	d->ctrl.frameBuf.pBuf = work; d->ctrl.frameBuf.size = bytes;
	SceAvcdecQueryDecoderInfo q;
	sceClibMemset (&q, 0, sizeof q);
	q.horizontal = d->w; q.vertical = d->h; q.numOfRefFrames = d->refs;
	int r = sceAvcdecCreateDecoder (SCE_VIDEODEC_TYPE_HW_AVCDEC, &d->ctrl, &q);
	if (r < 0) return k8avc_fail (d, "sceAvcdecCreateDecoder refused", r);
	d->created = 1;
	return 0;
}
/* both steps, the work memory a memblock of memtype (the launcher: CDRAM, before vitaGL takes it all); 0 = ready, < 0 = failed (whatever was
 * set up is undone by k8avc_close) */
static inline int k8avc_open (K8Avc *d, unsigned w, unsigned h, unsigned refs, SceKernelMemBlockType memtype)
{
	int need = k8avc_prepare (d, w, h, refs);
	if (need < 0) return need;
	d->mem = sceKernelAllocMemBlock ("shepherd-avcdec", memtype, d->mem_bytes, NULL);
	if (d->mem < 0) return k8avc_fail (d, "no memory for the decoder's work area", d->mem);
	void *base = 0;
	int r = sceKernelGetMemBlockBase (d->mem, &base);
	if (r < 0) return k8avc_fail (d, "the decoder's work area has no base", r);
	return k8avc_create (d, base, d->mem_bytes);
}

/* one access unit -> *got pictures (0 or 1) written to out (RGBA8888, pitch in pixels, the coded size); < 0 = the decoder refused */
static inline int k8avc_decode (K8Avc *d, const void *au, unsigned len, void *out, unsigned pitch, int *got)
{
	SceAvcdecAu a;
	sceClibMemset (&a, 0, sizeof a);
	a.es.pBuf = (void *) au; a.es.size = len;
	a.dts.upper = a.dts.lower = a.pts.upper = a.pts.lower = 0xFFFFFFFFu;
	SceAvcdecPicture pic;
	sceClibMemset (&pic, 0, sizeof pic);
	SceAvcdecPicture *pp = &pic;
	SceAvcdecArrayPicture arr;
	sceClibMemset (&arr, 0, sizeof arr);
	arr.numOfElm = 1; arr.pPicture = &pp;
	pic.size = sizeof pic;
	pic.frame.pixelType = SCE_AVCDEC_PIXELFORMAT_RGBA8888;
	pic.frame.framePitch = pitch; pic.frame.frameWidth = d->w; pic.frame.frameHeight = d->h;
	pic.frame.opt.rgba.alpha = 0xFF;
	pic.frame.pPicture [0] = out;
	int r = sceAvcdecDecode (&d->ctrl, &a, &arr);
	*got = r < 0 ? 0 : (int) arr.numOfOutput;
	if (r < 0) { d->why = "sceAvcdecDecode refused an access unit"; d->rc = r; }
	return r;
}

static inline void k8avc_close (K8Avc *d)
{
	if (d->created) sceAvcdecDeleteDecoder (&d->ctrl);
	if (d->mem >= 0) sceKernelFreeMemBlock (d->mem);
	if (d->lib) sceVideodecTermLibrary (SCE_VIDEODEC_TYPE_HW_AVCDEC);
	if (d->sysmod) sceSysmoduleUnloadModule (SCE_SYSMODULE_AVCDEC);
	d->created = d->lib = d->sysmod = 0; d->mem = -1;
}
#endif
