/* SHEPHERD-VITA device kit #8, track boot (vita/notes/kit8-boot.md): the Vita's hardware H.264 decoder (SceAvcdec through SceVideodec,
 * loaded with sceSysmoduleLoadModule (SCE_SYSMODULE_AVCDEC)) for the .shv clips (k8_shv.h): one access unit in, one RGBA picture out,
 * written by the decoder straight into the caller's buffer (a display frame buffer for the launcher's splash; a texture for the engine).
 * No libc (the launcher has none): Sce* calls only. Every buffer the decoder touches lives in physically contiguous memory (CDRAM): the
 * decoder's work memory (sized by sceAvcdecQueryDecoderMemSize), the access units (the .shv read into CDRAM), the output pictures.
 * The colour conversion is the decoder's own (cscCoefficient 0); splash_pack.py converts the clips to BT.601 limited range for it.
 *
 * Device kit #9, track device (vita/notes/kit9-device.md) -- two device findings of kit #8 (owner run 2026-10-22):
 *  OUTPUT DELAY: the Vita's decoder gave NO picture for the first access unit ("frame 0: the decoder gave no picture") although the clips
 *   have no B-frames and say max_num_reorder_frames 0 -- most likely the hardware returns pictures some access units after it got them (its
 *   DPB / pipeline; a HYPOTHESIS: kit #8.1 stopped at that first access unit, so a decoder that never outputs looks the same in its log --
 *   kit #9 then logs "no picture after 16 access units (the decoder does not answer)", a different failure, not a tolerated delay).
 *   A caller that wants every picture therefore keeps feeding access units while pictures are pending (k8avc_decode reports 0 or
 *   1 picture per call; fed / out count them), and after the last access unit asks for the held ones with sceAvcdecDecodeStop
 *   (k8avc_drain_n: a buffer offered for each picture still expected, 0 = none left). Pictures still come out in display order (no B-frames), each written into the
 *   buffer passed with the call that returns it -- so the picture index is the OUTPUT count, never the access unit's. K8_AVC_MAX_DELAY caps
 *   how many access units may go in before the first picture (the H.264 DPB maximum, 16) -- more = the decoder is not answering.
 *   (sceAvcdecDecodeStop is not in vita-headers' videodec.h: declared here as Vita3K's HLE has it, (ctrl, array of pictures).)
 *  DECODER MEMORY: sceVideodecInitLibrary takes the library's own physically contiguous memory (MEASURED on the owner's PCH-2000: 6,144 KiB
 *   of PHYCONT for 960 x 544 / 3 references -- the kit #8.1 launcher's MEM start vs MEM k8-released lines), and vitaGL's init takes all
 *   free PHYCONT and CDRAM, so the engine's real video found none (0x80024302 NO_FREE_PHYSICAL_PAGE). K8AvcLib initialises the library
 *   once, early (the host: right before vitaGL's init), and a decoder created later uses it (k8avc_prepare_ex shared = 1: no sysmodule load,
 *   no library init, and k8avc_close leaves both to their owner, k8avc_lib_term). */
#ifndef SHEPHERD_K8_AVC_H
#define SHEPHERD_K8_AVC_H

#include <psp2/videodec.h>
#include <psp2/sysmodule.h>
#include <psp2/kernel/sysmem.h>
#include <psp2/kernel/clib.h>
#include <psp2/io/fcntl.h>
#include <psp2/io/stat.h>

/* kit #9: the decoder's end-of-stream call (SceVideodecUser; not declared by vita-headers): the pictures it still holds, one per element */
int sceAvcdecDecodeStop (const SceAvcdecCtrl *decoder, SceAvcdecArrayPicture *array_picture);

/* kit #9 review, round 2 (finding 1; vita/notes/kit9-device.md "Review fixes"): ONE guard for the end-of-stream call -- the kit's one call
 * never run on a Vita -- shared by the launcher's splash and the engine's real video (one process, one card). Round 1 had two one-shot
 * guards that did not see each other (the launcher's own marker, skipped once then deleted; the engine's kit #8 marker, a black clip
 * once): a call that always crashes then crashed 3 starts out of 4, forever. Now:
 *   K9_AVC_DRAIN_MARKER is written right before EVERY end-of-stream call (launcher or engine) and removed right after it returned;
 *   a marker found by any later check (a call that never returned: a crash, or a hang the launcher's 15 s release cap or the engine's
 *   bounded wait walked away from -- this start or an earlier one) is turned into K9_AVC_DRAIN_OFF, and while that file is on the card
 *   NEITHER side calls it again, on every start, until the file is deleted. The cost of a skipped call: the held pictures at the clip's
 *   end (the decoder's delay: 1 picture if the device's delay is 1) -- the launcher's splash ends at the picture before them, the engine
 *   keeps showing that picture. SHEPHERD_K9_AVC_DRAIN=0 in the env file = always skipped (no file needed). SHEPHERD_K9_AVC_DELAY=0 (kit
 *   #8) never reaches any of this. */
#define K9_AVC_DRAIN_MARKER "ux0:data/shepherd/runtime/k9-avc-drain.marker"
#define K9_AVC_DRAIN_OFF "ux0:data/shepherd/runtime/k9-avc-drain.off"
static inline int k9_avc_file_there (const char *p)
{
	SceIoStat st;
	sceClibMemset (&st, 0, sizeof st);
	return sceIoGetstat (p, &st) >= 0;
}
static inline int k9_avc_file_write (const char *p, const char *text)
{
	SceUID fd = sceIoOpen (p, SCE_O_WRONLY | SCE_O_CREAT | SCE_O_TRUNC, 0777);
	if (fd < 0) return -1;
	SceSize n = 0;
	while (text [n]) n++;
	int w = sceIoWrite (fd, text, n);
	sceIoClose (fd);
	return w == (int) n ? 0 : -1;
}
/* the end-of-stream call is off from now on (why: one line for the file -- the owner deletes the file to try the call again) */
static inline void k9_avc_drain_off (const char *why) { k9_avc_file_write (K9_AVC_DRAIN_OFF, why); }
/* 0 = the end-of-stream call may run; 1 = skip it (*why: static text for the log) */
static inline int k9_avc_drain_blocked (const char **why)
{
	int off = k9_avc_file_there (K9_AVC_DRAIN_OFF);
	if (k9_avc_file_there (K9_AVC_DRAIN_MARKER)) {
		/* (an .off already there -- e.g. the launcher's release () found its thread in the call -- keeps its own text; the marker goes only
		 * once the off file is there: a card that refuses the write keeps the marker, found again next time) */
		if (off || k9_avc_file_write (K9_AVC_DRAIN_OFF, "the decoder's end-of-stream call (sceAvcdecDecodeStop) never returned once: runtime/k9-avc-drain.marker was found "
					      "(a crash or a hang). Kit #9 skips the call while this file is here: delete it to try the call again.\n") == 0)
			sceIoRemove (K9_AVC_DRAIN_MARKER);
		*why = "runtime/k9-avc-drain.marker found -- an earlier end-of-stream call never returned (a crash or a hang): runtime/k9-avc-drain.off "
		       "written, the call is skipped on every start until that file is deleted";
		return 1;
	}
	if (off) {
		*why = "runtime/k9-avc-drain.off is on the card (an earlier end-of-stream call never returned): skipped until that file is deleted";
		return 1;
	}
	return 0;
}
static inline void k9_avc_drain_begin (void) { k9_avc_file_write (K9_AVC_DRAIN_MARKER, "drain"); }
static inline void k9_avc_drain_end (void) { sceIoRemove (K9_AVC_DRAIN_MARKER); }

/* kit #10 review fix (finding 7; vita/notes/kit10-boot.md "Review fixes"): the refeed (THE DRAIN, below) -- feeding the clip's own SPS / PPS /
 * IDR again after its last access unit -- has never run on a Vita either, and it runs on the engine's main thread (the launcher: its video
 * thread, under release ()'s 15 s cap). The same persistent guard as kit #9's end-of-stream call, with files of its own (a refeed that crashed
 * says nothing about sceAvcdecDecodeStop): K10_AVC_REFEED_MARKER is written before a clip's FIRST flush access unit and removed once the
 * flush is over (the clip's last picture out, a refusal, an error or the clip's close) -- one write and one remove per clip, at the end of
 * the logo; a marker found by a later start (the refeed never came back: a crash, or a hang the owner closed the app on) is turned into
 * K10_AVC_REFEED_OFF, and while that file is on the card neither side refeeds: the flush goes straight to stop1 (behind kit #9's guard),
 * whose refusal on the device leaves the held pictures held (kit #9's 184 / 188) -- automatically, no env-file change. Delete the .off file
 * to try the refeed again. */
#define K10_AVC_REFEED_MARKER "ux0:data/shepherd/runtime/k10-avc-refeed.marker"
#define K10_AVC_REFEED_OFF "ux0:data/shepherd/runtime/k10-avc-refeed.off"
/* 0 = the refeed may run; 1 = not (*why: static text for the log) */
static inline int k10_avc_refeed_blocked (const char **why)
{
	int off = k9_avc_file_there (K10_AVC_REFEED_OFF);
	if (k9_avc_file_there (K10_AVC_REFEED_MARKER)) {
		if (off || k9_avc_file_write (K10_AVC_REFEED_OFF, "the decoder never came back from a flush access unit (the clip's own, fed again after its last: kit #10's refeed) -- "
					      "runtime/k10-avc-refeed.marker was found (a crash, or a hang the app was closed on). Kit #10 skips the refeed while this file is here: delete it to try it again.\n") == 0)
			sceIoRemove (K10_AVC_REFEED_MARKER);
		*why = "runtime/k10-avc-refeed.marker found -- an earlier refeed never came back (a crash or a hang): runtime/k10-avc-refeed.off written, the refeed "
		       "is skipped on every start until that file is deleted";
		return 1;
	}
	if (off) {
		*why = "runtime/k10-avc-refeed.off is on the card (an earlier refeed never came back): skipped until that file is deleted";
		return 1;
	}
	return 0;
}
static inline void k10_avc_refeed_begin (void) { k9_avc_file_write (K10_AVC_REFEED_MARKER, "refeed"); }
static inline void k10_avc_refeed_end (void) { sceIoRemove (K10_AVC_REFEED_MARKER); }

#define K8_AVC_MAX_DELAY 16u   /* access units in before the first picture, at most (the H.264 DPB's maximum): more = no answer */

typedef struct K8Avc {
	SceAvcdecCtrl ctrl;
	SceUID mem;
	unsigned mem_bytes, w, h, refs;
	int sysmod, lib, created;
	int rc;               /* the failing call's return code */
	const char *why;      /* static text: which step failed (0 = none) */
	/* kit #9 (appended: kit #8's fields keep their places) */
	int shared;           /* 1 = the library belongs to a K8AvcLib (k8avc_prepare_ex): this decoder neither loads nor ends it */
	unsigned fed, out, drained;   /* access units given, pictures returned (decode + drain), pictures returned by the drain */
	/* kit #10 (appended): the flush after the last access unit (k10avc_*, below) */
	unsigned refed, flushed;      /* flush access units fed (the clip's own again, from its first), pictures they brought out */
	int flush_rc;                 /* the first refused flush call's return code (0 = none refused) */
	/* kit #10 review fix (finding 5, appended): a picture of the clip's SECOND copy came out of a flush access unit (its own time stamp
	 * tag, k10avc_refeed): never counted as the clip's, never shown -- the caller ends the clip there (kit #9's "held") */
	unsigned second;
	/* kit #10 integration review (appended): the time-stamp check made provable. tag_clip = the clip's OWN access units carry a time stamp too
	 * (K10_AVC_CLIP_LO | the access unit's number; set by the caller with the refeed + SHEPHERD_K10_AVC_TAG on): a picture handed back with
	 * it shows the decoder passes time stamps through, and which picture it is (no B-frames in the .shv clips: picture n = access unit n). */
	int tag_clip;
	unsigned pts_seen;   /* pictures (any call) that came back carrying a clip time stamp: 0 = the decoder hands back none (the check is blind) */
	unsigned pts_bad;    /* pictures whose clip time stamp named another picture than the next one (out of order) */
	unsigned flush_ok;   /* flush pictures vouched for by their clip time stamp (the clip's held pictures, in order) */
	/* device kit #13, track bootc (appended): how the work area was made (k13avc_*, below): 0 = kit #8's k8avc_open, 1 = the memblock's own
	 * 1 MiB alignment option, 2 = a block 1 MiB larger with the area at its first 1 MiB boundary, 3 = the previous decoder's area again */
	unsigned k13_how;
} K8Avc;

static inline int k8avc_fail (K8Avc *d, const char *why, int rc) { d->why = why; d->rc = rc; return rc < 0 ? rc : -1; }

/* kit #9: the decoder library initialised once by its owner (the host, before vitaGL's init) and shared by the decoders created later */
typedef struct K8AvcLib {
	int sysmod, lib, rc;
	unsigned w, h, refs;
	const char *why;
} K8AvcLib;
static inline int k8avc_lib_init (K8AvcLib *L, unsigned w, unsigned h, unsigned refs)
{
	sceClibMemset (L, 0, sizeof *L);
	L->w = w; L->h = h; L->refs = refs;
	int r = sceSysmoduleLoadModule (SCE_SYSMODULE_AVCDEC);
	if (r < 0) { L->why = "the decoder module (SCE_SYSMODULE_AVCDEC) did not load"; L->rc = r; return r; }
	L->sysmod = 1;
	SceVideodecQueryInitInfoHwAvcdec init;
	sceClibMemset (&init, 0, sizeof init);
	init.size = sizeof init; init.horizontal = w; init.vertical = h; init.numOfRefFrames = refs; init.numOfStreams = 1;
	r = sceVideodecInitLibrary (SCE_VIDEODEC_TYPE_HW_AVCDEC, &init);
	if (r < 0) { L->why = "sceVideodecInitLibrary refused"; L->rc = r; sceSysmoduleUnloadModule (SCE_SYSMODULE_AVCDEC); L->sysmod = 0; return r; }
	L->lib = 1;
	return 0;
}
static inline void k8avc_lib_term (K8AvcLib *L)
{
	if (L->lib) sceVideodecTermLibrary (SCE_VIDEODEC_TYPE_HW_AVCDEC);
	if (L->sysmod) sceSysmoduleUnloadModule (SCE_SYSMODULE_AVCDEC);
	L->lib = L->sysmod = 0;
}

/* step 1: the decoder module + the library (shared = 1: both already up -- a K8AvcLib of the same size); returns the work memory the
 * decoder needs (bytes, > 0) or < 0 (d->why / d->rc) */
static inline int k8avc_prepare_ex (K8Avc *d, unsigned w, unsigned h, unsigned refs, int shared)
{
	sceClibMemset (d, 0, sizeof *d);
	d->mem = -1; d->w = w; d->h = h; d->refs = refs; d->shared = shared;
	int r;
	if (!shared) {
		r = sceSysmoduleLoadModule (SCE_SYSMODULE_AVCDEC);
		if (r < 0) return k8avc_fail (d, "the decoder module (SCE_SYSMODULE_AVCDEC) did not load", r);
		d->sysmod = 1;
		SceVideodecQueryInitInfoHwAvcdec init;
		sceClibMemset (&init, 0, sizeof init);
		init.size = sizeof init; init.horizontal = w; init.vertical = h; init.numOfRefFrames = refs; init.numOfStreams = 1;
		r = sceVideodecInitLibrary (SCE_VIDEODEC_TYPE_HW_AVCDEC, &init);
		if (r < 0) return k8avc_fail (d, "sceVideodecInitLibrary refused", r);
		d->lib = 1;
	}
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
static inline int k8avc_prepare (K8Avc *d, unsigned w, unsigned h, unsigned refs) { return k8avc_prepare_ex (d, w, h, refs, 0); }
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

/* the one-picture array both calls below hand the decoder: the picture goes to out (RGBA8888, pitch in pixels, the coded size) */
static inline void k8avc_pic (K8Avc *d, SceAvcdecPicture *pic, SceAvcdecPicture **pp, SceAvcdecArrayPicture *arr, void *out, unsigned pitch)
{
	sceClibMemset (arr, 0, sizeof *arr);
	sceClibMemset (pic, 0, sizeof *pic);
	*pp = pic;
	arr->numOfElm = 1; arr->pPicture = pp;
	pic->size = sizeof *pic;
	pic->frame.pixelType = SCE_AVCDEC_PIXELFORMAT_RGBA8888;
	pic->frame.framePitch = pitch; pic->frame.frameWidth = d->w; pic->frame.frameHeight = d->h;
	pic->frame.opt.rgba.alpha = 0xFF;
	pic->frame.pPicture [0] = out;
}

/* one access unit -> *got pictures (0 or 1) written to out (RGBA8888, pitch in pixels, the coded size); < 0 = the decoder refused.
 * kit #9: a picture that comes out is the oldest one the decoder holds (display order), not necessarily this access unit's (output delay) */
#define K10_AVC_CLIP_LO 0x4B300000u   /* kit #10 integration review: the clip's own access units' time stamp tag (+ the access unit's number) */
static inline int k10avc_cliptag (const SceAvcdecPicture *p) { return p->info.pts.upper == 0u && (p->info.pts.lower & 0xFFFF0000u) == K10_AVC_CLIP_LO; }
static inline int k8avc_decode (K8Avc *d, const void *au, unsigned len, void *out, unsigned pitch, int *got)
{
	SceAvcdecAu a;
	sceClibMemset (&a, 0, sizeof a);
	a.es.pBuf = (void *) au; a.es.size = len;
	a.dts.upper = a.dts.lower = a.pts.upper = a.pts.lower = 0xFFFFFFFFu;
	if (d->tag_clip) { a.pts.upper = 0u; a.pts.lower = K10_AVC_CLIP_LO | (d->fed & 0xFFFFu); }   /* (kit #10 integration review; else kit #9's) */
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
	pic.info.pts.upper = pic.info.pts.lower = 0xFFFFFFFFu;   /* (a decoder that leaves the field alone: never read as a tag) */
	int r = sceAvcdecDecode (&d->ctrl, &a, &arr);
	*got = r < 0 ? 0 : (int) arr.numOfOutput;
	if (r < 0) { d->why = "sceAvcdecDecode refused an access unit"; d->rc = r; }
	else {
		if (*got && d->tag_clip && k10avc_cliptag (&pic)) {   /* counted only: the picture is shown as before */
			d->pts_seen++;
			if ((pic.info.pts.lower & 0xFFFFu) != (d->out & 0xFFFFu)) d->pts_bad++;
		}
		d->fed++; d->out += (unsigned) *got;
	}
	return r;
}

/* kit #9: after the last access unit -- the pictures the decoder still holds, in display order, into outs [0 .. *got - 1] (n buffers offered,
 * n <= K8_AVC_MAX_DELAY): *got 0 = none left; < 0 = refused. Whether sceAvcdecDecodeStop hands back every held picture at once or one per
 * call is not documented, so the caller offers a buffer for each picture it still expects and calls again while pictures come out (both
 * ways then end with every picture) */
static inline int k8avc_drain_n (K8Avc *d, void *const *outs, unsigned n, unsigned pitch, int *got)
{
	SceAvcdecPicture pic [K8_AVC_MAX_DELAY], *pp [K8_AVC_MAX_DELAY];
	SceAvcdecArrayPicture arr;
	if (n < 1) n = 1;
	if (n > K8_AVC_MAX_DELAY) n = K8_AVC_MAX_DELAY;
	for (unsigned k = 0; k < n; k++)
		k8avc_pic (d, &pic [k], &pp [k], &arr, outs [k], pitch);   /* (each call rewrites arr for one element: set to n below) */
	for (unsigned k = 0; k < n; k++) pp [k] = &pic [k];
	arr.numOfOutput = 0; arr.numOfElm = n; arr.pPicture = pp;
	int r = sceAvcdecDecodeStop (&d->ctrl, &arr);
	*got = r < 0 ? 0 : (int) (arr.numOfOutput > n ? n : arr.numOfOutput);
	if (r < 0) { d->why = "sceAvcdecDecodeStop refused (the decoder's held pictures)"; d->rc = r; }
	else if (*got > 0) { d->out += (unsigned) *got; d->drained += (unsigned) *got; }
	return r;
}
static inline int k8avc_drain (K8Avc *d, void *out, unsigned pitch, int *got) { void *o [1] = { out }; return k8avc_drain_n (d, o, 1, pitch, got); }

/* ---------------------------------------------------------------- device kit #10, track boot (vita/notes/kit10-boot.md): THE DRAIN
 * Kit #9's device run (owner, 2026-10-27/28): sceAvcdecDecodeStop came back 0x80620002 (SCE_AVCDEC_ERROR_INVALID_PARAM) on BOTH sides
 * with the pictures still held -- the launcher offered 4 buffers, the engine up to 12 -- and returned 0 pictures, so each logo lost the
 * pictures its decoder still held: the launcher 184 of 188, the engine 176 of 179 (the decoder's output delay on the device: 4 access
 * units). Kit #10 never needs that call: the held pictures come out by FEEDING MORE ACCESS UNITS -- the clip's own access units again,
 * from its first one (every .shv starts with SPS + PPS + an IDR picture whose no_output_of_prior_pics_flag is 0: checked on both of the
 * owner's clips with ffmpeg's trace_headers). "The clip, then the clip again" is a valid H.264 stream, so any decoder hands every held
 * picture back in output order: an IDR picture empties the decoded picture buffer through the bumping process (H.264 C.4.4: prior
 * pictures are output, not dropped, when the flag is 0), and a decoder that simply returns its pictures a fixed number of access units
 * late pushes one held picture out per access unit fed. Either way picture n of the clip is the n-th picture out, each into the buffer of
 * the call that returns it -- one buffer per call, as every other decode: no extra buffers and no end-of-stream call. The second copy's
 * pictures are never shown (the caller stops at the clip's last picture; at most K8_AVC_MAX_DELAY + 1 flush access units are fed).
 * SHEPHERD_K10_AVC_FLUSH (env file; the launcher and the engine read the same file):
 *   unset / refeed = the above (default);
 *   stop1  = sceAvcdecDecodeStop with ONE picture per call, repeated (the other reading of the refusal: an array of more than one);
 *   0 / kit9 = kit #9's end-of-stream call exactly (a buffer per held picture in one array; the device refused it).
 * A refused flush access unit (refeed) falls back to stop1 once, behind kit #9's shared guard (k9_avc_drain_*); a refused stop1 ends it
 * (the pictures not out are not shown: kit #9's "held" behaviour). */
#define K10_FLUSH_KIT9 0
#define K10_FLUSH_REFEED 1
#define K10_FLUSH_STOP1 2
static inline int k10avc_flush_mode (const char *v)
{
	if (!v || !v [0]) return K10_FLUSH_REFEED;
	if ((v [0] == '0' && !v [1]) || (v [0] == 'k' && v [1] == 'i' && v [2] == 't' && v [3] == '9' && !v [4])) return K10_FLUSH_KIT9;
	if (v [0] == 's' && v [1] == 't' && v [2] == 'o' && v [3] == 'p' && v [4] == '1' && !v [5]) return K10_FLUSH_STOP1;
	return K10_FLUSH_REFEED;
}
static inline const char *k10avc_flush_name (int m) { return m == K10_FLUSH_KIT9 ? "kit #9's end-of-stream call" : m == K10_FLUSH_STOP1 ? "stop1" : "refeed"; }
/* kit #10 review fix (finding 5): THE SECOND COPY'S IDENTITY. The clip's own access units carry no time stamp (0xFFFFFFFF: kit #9's,
 * unchanged), so a picture out of a flush call was counted as the clip's next one whatever it was. If a decoder dropped (or output only one
 * of) its held pictures at the refed IDR instead of bumping them all out, later flush calls would return pictures of the SECOND copy -- the
 * logo's first frames shown as its last -- while the K10 line still said "188 of 188". Each flush access unit now carries a time stamp of
 * its own (K10_AVC_TAG: pts upper 0, lower 0x4B310000 | its number; a picture's SceAvcdecInfo.pts is its access unit's), and a picture that
 * comes out with that tag is never counted or shown: d->second is set and the caller ends the clip there, as kit #9 did when its pictures
 * stayed held. A decoder that does not hand time stamps back cannot be checked this way (no picture ever carries the tag: the line says so).
 * SHEPHERD_K10_AVC_TAG=0 (env file) = the flush access units without a time stamp (the first kit #10 build's, no check).
 * KIT #10 INTEGRATION REVIEW: that check was blind on a decoder that hands back no time stamps -- the clip's own access units carried none,
 * so a picture without the tag proved nothing, yet the K10 line said "no picture of the second copy came out". Now (refeed + the tag on)
 * the clip's own access units carry K10_AVC_CLIP_LO | their number too (k8avc_decode, d->tag_clip): every picture the decoder hands back
 * with a clip tag proves the pass-through (d->pts_seen), and a FLUSH picture is shown only when its clip tag names exactly the next picture
 * of the clip (d->flush_ok); one naming another picture (out of order) is treated as the second copy -- never shown, the clip holds its last
 * picture (kit #9's "held"). No clip tag ever seen = the line says the second copy was NOT checked (the decoder hands back no time stamps),
 * never that it passed. SHEPHERD_K10_AVC_TAG=0 = every access unit without a time stamp, exactly kit #9's (and the first kit #10 build's). */
#define K10_AVC_TAG_HI 0u
#define K10_AVC_TAG_LO 0x4B310000u
static inline int k10avc_tagged (const SceAvcdecPicture *p) { return p->info.pts.upper == K10_AVC_TAG_HI && (p->info.pts.lower & 0xFFFF0000u) == K10_AVC_TAG_LO; }
/* one flush access unit: the clip's access unit (au, len) -- the caller passes access unit d->refed % frames -- fed after the last one;
 * *got 0 or 1 pictures written to out (a picture of the second copy: *got 0, d->second set -- out then holds that picture: never show it).
 * fed / out stay the clip's (out counts the picture too: it is a picture of the clip). tag: 1 = the flush access unit's own time stamp */
static inline int k10avc_refeed (K8Avc *d, const void *au, unsigned len, void *out, unsigned pitch, int *got, int tag)
{
	SceAvcdecAu a;
	sceClibMemset (&a, 0, sizeof a);
	a.es.pBuf = (void *) au; a.es.size = len;
	a.dts.upper = a.dts.lower = a.pts.upper = a.pts.lower = 0xFFFFFFFFu;
	if (tag) { a.pts.upper = K10_AVC_TAG_HI; a.pts.lower = K10_AVC_TAG_LO | (d->refed & 0xFFFFu); }
	SceAvcdecPicture pic, *pp;
	SceAvcdecArrayPicture arr;
	k8avc_pic (d, &pic, &pp, &arr, out, pitch);
	pic.info.pts.upper = pic.info.pts.lower = 0xFFFFFFFFu;   /* (a decoder that leaves the field alone: never read as a tag) */
	int r = sceAvcdecDecode (&d->ctrl, &a, &arr);
	*got = r < 0 ? 0 : (int) (arr.numOfOutput > 1 ? 1 : arr.numOfOutput);
	if (r < 0) { d->why = "sceAvcdecDecode refused a flush access unit (the clip's own, fed again after its last)"; d->rc = r; if (!d->flush_rc) d->flush_rc = r; return r; }
	d->refed++;
	if (*got && tag && k10avc_tagged (&pic)) {   /* a picture of the second copy: not the clip's */
		*got = 0;
		d->second++;
		return r;
	}
	if (*got && tag && k10avc_cliptag (&pic)) {   /* integration review: a held picture of the clip -- the right one? */
		d->pts_seen++;
		if ((pic.info.pts.lower & 0xFFFFu) != (d->out & 0xFFFFu)) {   /* another picture than the clip's next: never shown (as the second copy) */
			d->pts_bad++;
			*got = 0;
			d->second++;
			return r;
		}
		d->flush_ok++;
	}
	d->out += (unsigned) *got; d->flushed += (unsigned) *got;
	return r;
}
/* stop1: the end-of-stream call with a ONE-picture array; *got 0 = none left; < 0 = refused */
static inline int k10avc_stop1 (K8Avc *d, void *out, unsigned pitch, int *got)
{
	SceAvcdecPicture pic, *pp;
	SceAvcdecArrayPicture arr;
	k8avc_pic (d, &pic, &pp, &arr, out, pitch);
	int r = sceAvcdecDecodeStop (&d->ctrl, &arr);
	*got = r < 0 ? 0 : (int) (arr.numOfOutput > 1 ? 1 : arr.numOfOutput);
	if (r < 0) { d->why = "sceAvcdecDecodeStop refused (one picture per call)"; d->rc = r; if (!d->flush_rc) d->flush_rc = r; }
	else if (*got > 0) { d->out++; d->drained++; d->flushed++; }
	return r;
}

/* ---------------------------------------------------------------- device kit #13, track bootc (vita/notes/kit13-bootc.md section 1): THE WORK AREA
 * Kit #12's device run (owner, 2026-11-05): the launcher's second clip (the Massive Monster logo) was refused by sceAvcdecCreateDecoder with
 * 0x80620009 (SCE_AVCDEC_ERROR_INVALID_POINTER) on both launchers, every start, after the first clip had played on a decoder made the same way.
 * What differed was WHERE its work area landed. The device's user CDRAM blocks are placed first-fit from 0x61000000 in 256 KiB granules (its
 * logs: the first CDRAM block of a start at 0x61000000, a 256 KiB block after a 2 MiB one at 0x61200000), so the first clip's 7 MiB area sat
 * right after its 5 MiB clip block (0x61500000: on a 1 MiB boundary), while the second clip's area sat right after the second clip's 1.5 MiB
 * block in the space the first clip gave back (0x61180000: on a 512 KiB boundary). The engine's own clip has always had its area on a 1 MiB
 * boundary (k8_boot.inc.c: shepgl_vram_alloc (need, 1 MiB); it decodes on the device) and every sample sizes the area in whole MiB. So the
 * decoder wants its work area on a 1 MiB boundary (INFERRED from those facts, not documented; vlx now refuses an area that is not, as the
 * device did: vita/vlx/vlx.c). Two answers, both used by the launcher's second clip:
 *   k13avc_reopen: the next clip's decoder on the SAME work area, library and module (only the decoder is deleted and created again): the
 *     exact pointer and size the device already accepted for the first clip -- nothing is allocated;
 *   k13avc_open: a fresh decoder whose work area is on a 1 MiB boundary whatever the blocks before it (the memblock's alignment option, else
 *     a block 1 MiB larger with the area at its first boundary) -- the fallback when the re-create is refused. */
#define K13_AVC_ALIGN 0x100000u
static inline int k13avc_alloc_work (K8Avc *d, SceKernelMemBlockType memtype, void **work)
{
	SceKernelAllocMemBlockOpt opt;
	sceClibMemset (&opt, 0, sizeof opt);
	opt.size = sizeof opt; opt.attr = SCE_KERNEL_ALLOC_MEMBLOCK_ATTR_HAS_ALIGNMENT; opt.alignment = K13_AVC_ALIGN;
	void *base = 0;
	d->mem = sceKernelAllocMemBlock ("shepherd-avcdec", memtype, d->mem_bytes, &opt);
	if (d->mem >= 0 && sceKernelGetMemBlockBase (d->mem, &base) >= 0 && !((unsigned) base & (K13_AVC_ALIGN - 1u))) { d->k13_how = 1; *work = base; return 0; }
	if (d->mem >= 0) sceKernelFreeMemBlock (d->mem);
	d->mem = sceKernelAllocMemBlock ("shepherd-avcdec", memtype, d->mem_bytes + K13_AVC_ALIGN, NULL);   /* (the option refused or not honoured) */
	if (d->mem < 0) return k8avc_fail (d, "no memory for the decoder's work area", d->mem);
	if (sceKernelGetMemBlockBase (d->mem, &base) < 0) return k8avc_fail (d, "the decoder's work area has no base", -1);
	*work = (void *) (((unsigned) base + K13_AVC_ALIGN - 1u) & ~(K13_AVC_ALIGN - 1u));
	d->k13_how = 2;
	return 0;
}
static inline int k13avc_open (K8Avc *d, unsigned w, unsigned h, unsigned refs, SceKernelMemBlockType memtype)
{
	int need = k8avc_prepare (d, w, h, refs);
	if (need < 0) return need;
	void *work = 0;
	int r = k13avc_alloc_work (d, memtype, &work);
	if (r < 0) return r;
	return k8avc_create (d, work, d->mem_bytes);
}
/* the decoder deleted and created again on its own work area (the library, the module and the area stay); the per-clip state starts as a new
 * decoder's (k8avc_prepare_ex zeroes it for one); 0 = ready, < 0 = refused (d->why / d->rc; d->created 0: the caller closes the rest) */
static inline int k13avc_reopen (K8Avc *d)
{
	if (!d->created || !d->ctrl.frameBuf.pBuf) return k8avc_fail (d, "no decoder to create again", -1);
	void *work = d->ctrl.frameBuf.pBuf;
	unsigned bytes = d->ctrl.frameBuf.size;
	int r = sceAvcdecDeleteDecoder (&d->ctrl);
	d->created = 0;
	if (r < 0) return k8avc_fail (d, "sceAvcdecDeleteDecoder refused (the previous clip's decoder)", r);
	d->fed = d->out = d->drained = d->refed = d->flushed = d->second = d->pts_seen = d->pts_bad = d->flush_ok = 0;
	d->flush_rc = 0; d->tag_clip = 0; d->rc = 0; d->why = 0;
	sceClibMemset (&d->ctrl, 0, sizeof d->ctrl);
	r = k8avc_create (d, work, bytes);
	if (r >= 0) d->k13_how = 3;
	return r;
}

static inline void k8avc_close (K8Avc *d)
{
	if (d->created) sceAvcdecDeleteDecoder (&d->ctrl);
	if (d->mem >= 0) sceKernelFreeMemBlock (d->mem);
	if (d->lib) sceVideodecTermLibrary (SCE_VIDEODEC_TYPE_HW_AVCDEC);   /* (a shared decoder never set these two: its K8AvcLib's owner ends them) */
	if (d->sysmod) sceSysmoduleUnloadModule (SCE_SYSMODULE_AVCDEC);
	d->created = d->lib = d->sysmod = 0; d->mem = -1;
}
#endif
