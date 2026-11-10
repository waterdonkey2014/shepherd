/* vita/vlx/vramcheck.inc.c (kit #13 fix, vita/notes/kit13-fix.md): VLX_VRAMCHECK=1 in the app's env file -- a CHECKED model of vitaGL's VRAM pool.
 *
 * Why it exists. The recording stand-in (glrec_vlx.c) replaces vitaGL: under vlx there is no VRAM heap at all and no GPU, so a memory bug between
 * vitaGL's pool and the GPU cannot show. The device showed one (kit #13 STEP 1): a render target's block was freed while its GXM scene was still
 * to run, the GPU's pixels landed on the free chunk and SceLibKernel's mspace died on them at a later allocation. This file models what that takes:
 *
 *  - the pool: dlmalloc as SceLibKernel's mspace lays it out on the device (16-byte alignment, 4-byte overhead, the same bins, tree, dv and top
 *    rules; the dump shows those numbers), 96 MiB, the rings and display buffers vitaGL takes at init in vitaGL's order, in-band chunk fields in
 *    VC_M. Addresses are offsets from the device's 0x62000000.
 *  - vitaGL's rules, read from libvitaGL.a (sha256 2b0d4ad5...): gpu_alloc_texture (ALIGN(w, 8) * h * bpp, 16 aligned), glBufferData,
 *    glDeleteBuffers, glDeleteTextures + glDeleteFramebuffers (an attached texture waits for its framebuffer), the free rule "vgl_framecount -
 *    last_frame > 4 (unsigned; last_frame -1 = never sampled): free NOW, else the frame's purge list", the garbage collector's 4 lists.
 *    last_frame moves when a texture is SAMPLED or a buffer DRAWN FROM, never when a texture is rendered INTO.
 *  - the GPU: glBindFramebuffer only stores the framebuffer; a scene ends at the next draw / clear on another framebuffer (scene_reset), at glFinish
 *    and at a swap. An ended scene's pixels reach the target when the GPU has run it: modelled at glFinish or two swaps after the scene's end,
 *    whichever comes first -- or at once when its block is already free (the writes can only be later than that).
 *  - the poison: VC_D holds the pool's bytes as the GPU and the checker see them, out of band so that a late write cannot take the model's own
 *    lists down. A free fills the block with 0xDB, a late GPU write puts 0x5A on its target's range, every allocation and every tree pick verifies
 *    its whole block (only bytes 0x00 = never used and 0xDB may be there). The late write also zeroes the first 24 bytes in VC_M, a transparent
 *    target's first pixels over the free chunk's fd, bk, child, parent, index: the ring then keeps the order the device's kept.
 *
 * What it does NOT model: vitaGL's shader, uniform and small blocks. The LATE finding does not depend on them (it is exact: the model's first target lands
 * on the device's +0x21010); the frame of a FAIL does: read it as "something got the poisoned block", not as the device's frame (vita/notes/kit13-fix.md).
 *
 * Findings, on the console (a driver greps them into its verdict):
 *   VRAMCHECK LATE   the GPU wrote a freed block: the target (GL name, size, what was drawn last), the thread, frame and stack of the free and of the
 *                    scene's end
 *   VRAMCHECK FAIL   an allocation got a block with late-written bytes: the allocation's thread, frame and stack, then the same names -- this is the
 *                    moment the device's mspace dies; VLX_VRAMCHECK=1 ends the process (exit 3) here, VLX_VRAMCHECK=2 goes on
 *   VRAMCHECK END    one line at every 300th swap: the counters (a gate reads the last one: a run with none compared nothing)
 * Stacks are return addresses scanned off the stack (as a core dump's reader does); vita/vlx/vcsym.py names them from the run's ELF (KEEP=1).
 *
 * The pool's size (kit #14 integration, vita/notes/kit14-integration.md): VLX_VRAMCHECK_POOL_KIB=<KiB> runs the model with the pool an IL block in video memory leaves (98304 - the IL pool's KiB; default 96 MiB),
 * VLX_VRAMCHECK_HIDDEN_KIB=<KiB> takes what the model does not hold first (a block at init: vitaGL's shader, uniform and small blocks), VLX_VRAMCHECK_SPILL=<KiB> says what happens to an allocation that
 * does not fit, as vitaGL's GPU allocator does (DISASSEMBLY, k10_vglmem.c's note): the next <KiB> go to its other pools (the RAM and PHYCONT free space of the device's lines), the rest to newlib: a real
 * malloc here, so the walk feels it where the device's GC would, and "VRAMCHECK SPILL" says how much at each summary. Without VLX_VRAMCHECK_SPILL a full pool stays a console line.
 *
 * Off (the default) nothing here runs and no GL record changes. On, no GL record changes either: nothing here is hashed, and the app's own memory
 * budgets are untouched (the two pool views are anonymous mappings outside vlx's budgets, by vlx_host_map). */

extern void *vlx_host_map (unsigned size);
extern int sceKernelExitProcess (int res);
extern int vlx_host_mapped (unsigned page);
extern char __executable_start, etext;

#define VC_POOL (96u << 20)
#define VC_POISON 0xDB
#define VC_GPU 0x5A
#define VC_NAMES (1u << 18)          /* GL names: the stand-in's one counter */
#define VC_PURGE 16384u              /* vitaGL's list size */
#define VC_STK 14

static int g_vc = -1;                /* -1 unread, 0 off, 1 on (first FAIL ends the run), 2 on (goes on) */
static unsigned char *VC_M, *VC_D;
typedef struct { unsigned sp [VC_STK]; int n; unsigned tid, frame; } vc_where;

/* ---------------- dlmalloc, the mspace's rules (public domain, Doug Lea; 16-byte alignment, no FOOTERS, no locks: stand-in calls are serialized by vc_lock) ---------------- */
typedef struct vc_chunk { size_t prev_foot, head; struct vc_chunk *fd, *bk; struct vc_chunk *child [2]; struct vc_chunk *parent; unsigned index; } vc_chunk;
#define PINUSE 1u
#define CINUSE 2u
#define VC_ALIGN 16u
#define VC_MINCHUNK 16u
#define VC_OVER 4u
#define VC_SMALL 32u
#define VC_TREES 32u
#define c_size(p) ((p)->head & ~7u)
#define c_at(p, o) ((vc_chunk *) ((char *) (p) + (o)))
#define c_mem(p) ((char *) (p) + 8)
#define c_of(m) ((vc_chunk *) ((char *) (m) - 8))
#define c_cinuse(p) ((p)->head & CINUSE)
#define c_pinuse(p) ((p)->head & PINUSE)
static struct { unsigned smallmap, treemap; size_t dvsize, topsize; vc_chunk *dv, *top; vc_chunk *sb [(VC_SMALL + 1) * 2]; vc_chunk *tb [VC_TREES]; } M;
#define smallbin(i) ((vc_chunk *) &M.sb [(i) << 1])
static size_t pad (size_t r) { return r < 12 ? VC_MINCHUNK : (r + VC_OVER + VC_ALIGN - 1) & ~(size_t) (VC_ALIGN - 1); }
static unsigned tree_index (size_t s)
{
	size_t x = s >> 8;
	if (!x) return 0;
	if (x > 0xFFFF) return VC_TREES - 1;
	unsigned k = 31 - __builtin_clz (x);
	return (k << 1) + ((s >> (k + 7)) & 1);
}
static void set_foot (vc_chunk *p, size_t s) { c_at (p, s)->prev_foot = s; }
static void free_with_pinuse (vc_chunk *p, size_t s, vc_chunk *n) { n->head &= ~PINUSE; p->head = s | PINUSE; set_foot (p, s); }
static void free_size (vc_chunk *p, size_t s) { p->head = s | PINUSE; set_foot (p, s); }
static void inuse (vc_chunk *p, size_t s) { p->head = (p->head & PINUSE) | s | CINUSE; c_at (p, s)->head |= PINUSE; }
static void inuse_pinuse (vc_chunk *p, size_t s) { p->head = s | PINUSE | CINUSE; c_at (p, s)->head |= PINUSE; }
static void size_inuse (vc_chunk *p, size_t s) { p->head = s | PINUSE | CINUSE; }
static int vc_bad_pick (vc_chunk *v);   /* the checker: the mspace is about to take v out of its tree */
static void ins_small (vc_chunk *p, size_t s)
{
	unsigned i = s >> 3; vc_chunk *b = smallbin (i), *f = b;
	if (!(M.smallmap & (1u << i))) M.smallmap |= 1u << i; else f = b->fd;
	b->fd = p; f->bk = p; p->fd = f; p->bk = b;
}
static void unl_small (vc_chunk *p, size_t s)
{
	vc_chunk *f = p->fd, *b = p->bk; unsigned i = s >> 3;
	if (f == b) M.smallmap &= ~(1u << i); else { f->bk = b; b->fd = f; }
}
static void ins_large (vc_chunk *x, size_t s)
{
	unsigned i = tree_index (s); vc_chunk **h = &M.tb [i];
	x->index = i; x->child [0] = x->child [1] = NULL;
	if (!(M.treemap & (1u << i))) { M.treemap |= 1u << i; *h = x; x->parent = (vc_chunk *) h; x->fd = x->bk = x; return; }
	vc_chunk *t = *h; size_t k = s << (i == VC_TREES - 1 ? 0 : 31 - ((i >> 1) + 8 - 2));
	for (;;) {
		if (c_size (t) != s) {
			vc_chunk **c = &t->child [(k >> 31) & 1]; k <<= 1;
			if (*c) t = *c; else { *c = x; x->parent = t; x->fd = x->bk = x; break; }
		} else {
			vc_chunk *f = t->fd;
			if (!f) { vc_bad_pick (t); f = t->fd; }   /* (a ring member's fd the GPU zeroed: the device faults at address 0xc here) */
			t->fd = f->bk = x; x->fd = f; x->bk = t; x->parent = NULL; break;
		}
	}
}
static void unl_large (vc_chunk *x)
{
	vc_chunk *xp = x->parent, *r;
	if (x->bk != x) { vc_chunk *f = x->fd; r = x->bk; f->bk = r; r->fd = f; }
	else {
		vc_chunk **rp;
		if ((r = *(rp = &x->child [1])) || (r = *(rp = &x->child [0]))) {
			vc_chunk **cp;
			while (*(cp = &r->child [1]) || *(cp = &r->child [0])) r = *(rp = cp);
			*rp = NULL;
		}
	}
	if (xp) {
		vc_chunk **h = &M.tb [x->index];
		if (x == *h) { if (!(*h = r)) M.treemap &= ~(1u << x->index); }
		else if (xp->child [0] == x) xp->child [0] = r; else xp->child [1] = r;
		if (r) {
			vc_chunk *c0, *c1;
			r->parent = xp;
			if ((c0 = x->child [0])) { r->child [0] = c0; c0->parent = r; }
			if ((c1 = x->child [1])) { r->child [1] = c1; c1->parent = r; }
		}
	}
}
static void ins_chunk (vc_chunk *p, size_t s) { if ((s >> 3) < VC_SMALL) ins_small (p, s); else ins_large (p, s); }
static void unl_chunk (vc_chunk *p, size_t s) { if ((s >> 3) < VC_SMALL) unl_small (p, s); else unl_large (p); }
static void replace_dv (vc_chunk *p, size_t s) { if (M.dvsize) ins_chunk (M.dv, M.dvsize); M.dvsize = s; M.dv = p; }
static vc_chunk *leftmost (vc_chunk *t) { return t->child [0] ? t->child [0] : t->child [1]; }
static void *tmalloc_large (size_t nb)
{
	vc_chunk *v = NULL, *t; size_t rsize = -nb; unsigned idx = tree_index (nb);
	if ((t = M.tb [idx])) {
		size_t bits = nb << (idx == VC_TREES - 1 ? 0 : 31 - ((idx >> 1) + 8 - 2)); vc_chunk *rst = NULL;
		for (;;) {
			vc_chunk *rt; size_t trem = c_size (t) - nb;
			if (trem < rsize) { v = t; if (!(rsize = trem)) break; }
			rt = t->child [1]; t = t->child [(bits >> 31) & 1];
			if (rt && rt != t) rst = rt;
			if (!t) { t = rst; break; }
			bits <<= 1;
		}
	}
	if (!t && !v) {
		unsigned left = ((1u << idx) << 1 | -((1u << idx) << 1)) & M.treemap;
		if (left) t = M.tb [__builtin_ctz (left)];
	}
	while (t) { size_t trem = c_size (t) - nb; if (trem < rsize) { rsize = trem; v = t; } t = leftmost (t); }
	if (v && rsize < (size_t) (M.dvsize - nb)) {
		vc_chunk *r = c_at (v, nb);
		vc_bad_pick (v);
		unl_large (v);
		if (rsize < VC_MINCHUNK) inuse_pinuse (v, rsize + nb);
		else { size_inuse (v, nb); free_size (r, rsize); ins_chunk (r, rsize); }
		return c_mem (v);
	}
	return NULL;
}
static void *tmalloc_small (size_t nb)
{
	vc_chunk *t, *v; size_t rsize;
	v = t = M.tb [__builtin_ctz (M.treemap)]; rsize = c_size (t) - nb;
	while ((t = leftmost (t))) { size_t trem = c_size (t) - nb; if (trem < rsize) { rsize = trem; v = t; } }
	vc_chunk *r = c_at (v, nb);
	vc_bad_pick (v);
	unl_large (v);
	if (rsize < VC_MINCHUNK) inuse_pinuse (v, rsize + nb);
	else { size_inuse (v, nb); free_size (r, rsize); replace_dv (r, rsize); }
	return c_mem (v);
}
static void *vc_imalloc (size_t bytes)
{
	void *mem; size_t nb;
	if (bytes <= 236) {
		nb = bytes < 12 ? VC_MINCHUNK : pad (bytes);
		unsigned idx = nb >> 3, bits = M.smallmap >> idx;
		if (bits & 3) {
			idx += ~bits & 1;
			vc_chunk *b = smallbin (idx), *p = b->fd; unl_small (p, idx << 3); inuse_pinuse (p, idx << 3); return c_mem (p);
		} else if (nb > M.dvsize) {
			if (bits) {
				unsigned left = ((1u << idx) << 1 | -((1u << idx) << 1)) & (bits << idx), i = __builtin_ctz (left);
				vc_chunk *b = smallbin (i), *p = b->fd; size_t rsize = (i << 3) - nb;
				unl_small (p, i << 3); size_inuse (p, nb);
				vc_chunk *r = c_at (p, nb); free_size (r, rsize); replace_dv (r, rsize);
				return c_mem (p);
			}
			if (M.treemap) return tmalloc_small (nb);
		}
	} else {
		nb = pad (bytes);
		if (M.treemap && (mem = tmalloc_large (nb))) return mem;
	}
	if (nb <= M.dvsize) {
		size_t rsize = M.dvsize - nb; vc_chunk *p = M.dv;
		if (rsize >= VC_MINCHUNK) { vc_chunk *r = M.dv = c_at (p, nb); M.dvsize = rsize; free_size (r, rsize); size_inuse (p, nb); }
		else { size_t dvs = M.dvsize; M.dvsize = 0; M.dv = NULL; inuse_pinuse (p, dvs); }
		return c_mem (p);
	}
	if (nb < M.topsize) {
		size_t rsize = M.topsize -= nb; vc_chunk *p = M.top, *r = M.top = c_at (p, nb);
		r->head = rsize | PINUSE; size_inuse (p, nb);
		return c_mem (p);
	}
	return NULL;
}
static void vc_ifree (void *mem)
{
	vc_chunk *p = c_of (mem); size_t psize = c_size (p); vc_chunk *next = c_at (p, psize);
	if (!c_pinuse (p)) {
		size_t prevsize = p->prev_foot; vc_chunk *prev = (vc_chunk *) ((char *) p - prevsize);
		psize += prevsize; p = prev;
		if (p != M.dv) unl_chunk (p, prevsize);
		else if ((next->head & 3) == 3) { M.dvsize = psize; free_with_pinuse (p, psize, next); return; }
	}
	if (!c_cinuse (next)) {
		if (next == M.top) {
			size_t t = M.topsize += psize; M.top = p; p->head = t | PINUSE;
			if (p == M.dv) { M.dv = NULL; M.dvsize = 0; }
			return;
		}
		if (next == M.dv) { size_t d = M.dvsize += psize; M.dv = p; free_size (p, d); return; }
		size_t nsize = c_size (next);
		psize += nsize; unl_chunk (next, nsize); free_size (p, psize);
		if (p == M.dv) { M.dvsize = psize; return; }
	} else free_with_pinuse (p, psize, next);
	ins_chunk (p, psize);
}
static void *vc_imemalign (size_t align, size_t bytes)
{
	if (align <= VC_ALIGN) return vc_imalloc (bytes);
	size_t nb = pad (bytes), req = nb + align + VC_MINCHUNK - VC_OVER;
	char *mem = vc_imalloc (req);
	if (!mem) return NULL;
	void *leader = NULL, *trailer = NULL; vc_chunk *p = c_of (mem);
	if ((size_t) mem % align) {
		char *br = (char *) c_of ((char *) (((size_t) mem + align - 1) & -align));
		char *pos = (size_t) (br - (char *) p) >= VC_MINCHUNK ? br : br + align;
		vc_chunk *np = (vc_chunk *) pos; size_t lead = pos - (char *) p, nsz = c_size (p) - lead;
		inuse (np, nsz); inuse (p, lead); leader = c_mem (p); p = np;
	}
	size_t sz = c_size (p);
	if (sz > nb + VC_MINCHUNK) { size_t rs = sz - nb; vc_chunk *r = c_at (p, nb); inuse (p, nb); inuse (r, rs); trailer = c_mem (r); }
	if (leader) vc_ifree (leader);
	if (trailer) vc_ifree (trailer);
	return c_mem (p);
}

/* ---------------- what the checker keeps ---------------- */
typedef struct { unsigned off, bytes; unsigned name, frame; vc_where w; } vc_freed;   /* the recent frees, for the report */
#define VC_HIST 8192u
static vc_freed *g_hist; static unsigned g_nhist;
typedef struct { unsigned off, bytes, name, w, h, src; vc_where free_w, end_w; } vc_late;
#define VC_LATE 64u
static vc_late g_late [VC_LATE]; static unsigned g_nlate;
typedef struct {
	unsigned char kind;              /* 1 texture, 2 buffer, 3 framebuffer */
	unsigned char attached, pending; /* texture: framebuffers it is attached to; deleted, waiting for them */
	unsigned off, bytes;             /* its block (off 0 = none) */
	unsigned last_frame;             /* texture / buffer: -1 = never used by a draw */
	unsigned w, h, tex, lastsrc;     /* texture: size, what was drawn into it last; framebuffer: its colour texture */
	void *spill_mem; unsigned spill_other;   /* kit #14 integration: an allocation that did not fit: its newlib block, or its bytes in vitaGL's other pools */
} vc_obj;
static vc_obj *g_o;
#define VC_OBJ(n) (&g_o [(n) & (VC_NAMES - 1)])
static unsigned g_vfc;               /* vitaGL's vgl_framecount: every vglSwapBuffers, the boot frames included */
static unsigned *g_purge [4], g_pidx, g_pelem, g_pclean = 1;
static unsigned g_scene_fb, g_scene_tex;   /* the open scene: its framebuffer (0 = the display, or none), the texture it renders into */
static int g_scene_drawn;
typedef struct { unsigned tex, due; vc_where end_w; } vc_job;
#define VC_JOBS 256u
static vc_job g_job [VC_JOBS]; static unsigned g_njob;
static unsigned long long g_stat_alloc, g_stat_free, g_stat_late, g_stat_fail, g_stat_ext;
static unsigned g_vc_inuse = 0x288, g_vc_peak;   /* bytes in use, as the mspace's own counters (the dump's mstate keeps both); starts with sceClibMspaceCreate's mstate + fencepost */
static SceKernelLwMutexWork g_vc_lw;
static unsigned g_attr [8];          /* the buffer behind each vertex attribute */

static void vc_where_here (vc_where *w)
{
	volatile unsigned mark = 0;
	unsigned here = (unsigned) (uintptr_t) &mark, lo = (unsigned) (uintptr_t) &__executable_start, hi = (unsigned) (uintptr_t) &etext;
	w->n = 0; w->tid = (unsigned) sceKernelGetThreadId (); w->frame = g_swaps;
	if ((uintptr_t) w > here && (uintptr_t) w - here < 0x1800) here = (unsigned) (uintptr_t) (w + 1);   /* (a caller's own record: not scanned) */
	for (unsigned a = here & ~3u, end = here + 0x1800; a < end && w->n < VC_STK; a += 4) {
		if (!(a & 0xFFFu) && !vlx_host_mapped (a)) break;   /* a shallow stack's end */
		unsigned v = *(volatile unsigned *) (uintptr_t) a;
		if (v >= lo && v < hi && (v & 1)) w->sp [w->n++] = v;
	}
}
static void vc_print_where (const char *what, const vc_where *w)
{
	char l [220]; int n = snprintf (l, sizeof l, "VRAMCHECK   %s: thread 0x%x, frame %u, stack", what, w->tid, w->frame);
	for (int i = 0; i < w->n && n < (int) sizeof l - 12; i++) n += snprintf (l + n, sizeof l - n, " %x", w->sp [i]);
	sceClibPrintf ("%s\n", l);
}
static unsigned g_spill_on, g_spill_budget, g_spill_other, g_spill_other_peak, g_spill_nl, g_spill_nl_peak, g_spill_n;   /* kit #14 integration: VLX_VRAMCHECK_SPILL */
static void vc_summary (const char *when)
{
	if (g_spill_on) sceClibPrintf ("VRAMCHECK SPILL (%s, f%u; VLX on ESTIMATE inputs: the hidden block and the budget are device projections): %u allocations did not fit the pool; "
	                               "in vitaGL's other pools now %u KiB (peak %u of a %u KiB budget), in newlib now %u KiB (peak %u)\n", when, g_swaps, g_spill_n,
	                               g_spill_other >> 10, g_spill_other_peak >> 10, g_spill_budget >> 10, g_spill_nl >> 10, g_spill_nl_peak >> 10);
	sceClibPrintf ("VRAMCHECK END (%s, f%u): %llu allocations + %llu frees checked, %llu host blocks, %llu late GPU write(s), %llu FAIL(s); pool top +0x%x, %u bytes; in use %u KiB, peak %u KiB\n", when, g_swaps,
	               g_stat_alloc, g_stat_free, g_stat_ext, g_stat_late, g_stat_fail, (unsigned) ((char *) M.top - (char *) VC_M), (unsigned) M.topsize, g_vc_inuse >> 10, g_vc_peak >> 10);
}
static int vc_on (void);
static void vc_lock (void) { sceKernelLockLwMutex (&g_vc_lw, 1, NULL); }
static void vc_unlock (void) { sceKernelUnlockLwMutex (&g_vc_lw, 1); }

/* ---------------- the pool as vitaGL uses it ---------------- */
static const vc_late *vc_late_of (unsigned off) { for (unsigned i = g_nlate; i-- > 0; ) if (off >= g_late [i].off && off < g_late [i].off + g_late [i].bytes) return &g_late [i]; return NULL; }
static const char *g_ctx_who; static unsigned g_ctx_name, g_ctx_bytes;
static void vc_fail (unsigned off, unsigned chunk, unsigned bad, const char *when)
{
	const unsigned char *d = VC_D + off;
	g_stat_fail++;
	const vc_late *l = vc_late_of (off + bad);
	sceClibPrintf ("VRAMCHECK FAIL f%u: %s (GL name %u, %u bytes) %s pool +0x%x (a chunk of %u bytes) whose byte +0x%x was written after its free (0x%02x, not the poison): the device's mspace dies here\n",
	               g_swaps, g_ctx_who ? g_ctx_who : "?", g_ctx_name, g_ctx_bytes, when, off, chunk, bad, d [bad]);
	if (l) {
		sceClibPrintf ("VRAMCHECK   the writer: the GPU, rendering target texture %u (%ux%u RGBA, %u bytes at pool +0x%x), last drawn from texture %u\n", l->name, l->w, l->h, l->bytes, l->off, l->src);
		vc_print_where ("its block freed", &l->free_w); vc_print_where ("its scene ended", &l->end_w);
	}
	vc_where w; vc_where_here (&w); vc_print_where ("this allocation", &w);
	if (g_vc == 1) { vc_summary ("FAIL"); sceKernelExitProcess (3); }
}
static long vc_dirty (unsigned off, size_t usable)
{
	const unsigned char *d = VC_D + off;
	for (size_t i = 0; i < usable; i++) if (d [i] != 0 && d [i] != VC_POISON) return (long) i;
	return -1;
}
/* the tree pick the device's mspace dies in (treebin_at (m, x->index) on the pixel that took the index's place). VLX_VRAMCHECK=2 mends the fields and goes on */
static int vc_bad_pick (vc_chunk *v)
{
	unsigned off = (unsigned) ((char *) c_mem (v) - (char *) VC_M); size_t usable = c_size (v) - VC_OVER;
	long bad = vc_dirty (off, usable);
	if (bad < 0) return 0;
	vc_fail (off, (unsigned) c_size (v), (unsigned) bad, "picked");
	memset (VC_D + off, VC_POISON, usable);
	v->index = tree_index (c_size (v)); if (!v->fd || !v->bk) v->fd = v->bk = v;
	return 1;
}
static unsigned vc_alloc (size_t align, size_t bytes, const char *who, unsigned name)
{
	g_ctx_who = who; g_ctx_name = name; g_ctx_bytes = (unsigned) bytes;
	char *mem = vc_imemalign (align, bytes);
	if (!mem) { if (!g_spill_on) sceClibPrintf ("VRAMCHECK pool full: %u bytes (%s) at f%u\n", (unsigned) bytes, who, g_swaps); return 0; }
	unsigned off = (unsigned) (mem - (char *) VC_M); size_t usable = c_size (c_of (mem)) - VC_OVER;
	g_stat_alloc++;
	g_vc_inuse += c_size (c_of (mem)); if (g_vc_inuse > g_vc_peak) g_vc_peak = g_vc_inuse;
	long bad = vc_dirty (off, usable);   /* (a block off the small bins, dv or top: no tree pick saw it) */
	if (bad >= 0) { vc_fail (off, (unsigned) c_size (c_of (mem)), (unsigned) bad, "got"); memset (VC_D + off, VC_POISON, usable); }
	return off;
}
static void vc_poison (unsigned off, unsigned name)
{
	vc_freed *h = &g_hist [g_nhist++ % VC_HIST];
	h->off = off; h->bytes = c_size (c_of (VC_M + off)) - VC_OVER; h->name = name; h->frame = g_swaps; vc_where_here (&h->w);
	memset (VC_D + off, VC_POISON, h->bytes);
}
static void vc_free_now (unsigned off, unsigned name) { g_stat_free++; g_vc_inuse -= c_size (c_of (VC_M + off)); vc_poison (off, name); vc_ifree (VC_M + off); }
static void vc_purge_push (unsigned off) { unsigned i = g_pelem++; if (i < VC_PURGE) g_purge [g_pidx][i] = off; else sceClibPrintf ("VRAMCHECK purge list over vitaGL's %u entries at f%u\n", VC_PURGE, g_swaps); }
static void vc_free_rule (unsigned off, unsigned last_frame, unsigned name)
{
	if (!off) return;
	if (g_vfc - last_frame > 4u) vc_free_now (off, name); else vc_purge_push (off);
}
static void vc_gc (void)
{
	unsigned *l = g_purge [g_pclean];
	for (unsigned i = 0; i < VC_PURGE && l [i]; i++) { vc_free_now (l [i], 0); l [i] = 0; }
	g_pclean = (g_pclean + 1) & 3; g_pidx = (g_pidx + 1) & 3; g_pelem = 0;
}
/* a GPU job's pixels: into a live block they change nothing the checker reads; into a freed one they are the bug */
static void vc_job_run (const vc_job *j)
{
	const vc_obj *t = VC_OBJ (j->tex);
	if (t->kind == 1 && t->off) return;
	const vc_freed *h = NULL;
	unsigned off = 0, bytes = 0, w = 0, h2 = 0, src = 0;
	for (unsigned i = g_nhist; i-- > 0 && g_nhist - i <= VC_HIST; ) if (g_hist [i % VC_HIST].name == j->tex) { h = &g_hist [i % VC_HIST]; break; }
	if (!h) return;
	off = h->off; bytes = t->bytes ? t->bytes : h->bytes; w = t->w; h2 = t->h; src = t->lastsrc;
	memset (VC_D + off, VC_GPU, bytes);
	{	/* and as on the device the pixels land on the free chunk's own fields: a transparent target's first pixels are zero (fd, bk, child, parent, index) -- so the
		 * ring keeps the order the device's kept (a zeroed ring member comes up last), which decides WHEN the chunk is picked */
		vc_chunk *c = c_of (VC_M + off);
		if (!c_cinuse (c) && c_size (c) >= bytes) memset (VC_M + off, 0, 24);
	}
	g_stat_late++;
	sceClibPrintf ("VRAMCHECK LATE f%u: the GPU wrote target texture %u (%ux%u, %u bytes at pool +0x%x) after its block was freed at f%u (last drawn from texture %u)\n", g_swaps, j->tex, w, h2, bytes, off, h->frame, src);
	vc_print_where ("its block freed", &h->w); vc_print_where ("its scene ended", &j->end_w);
	if (g_nlate < VC_LATE) { vc_late *l = &g_late [g_nlate++]; l->off = off; l->bytes = bytes; l->name = j->tex; l->w = w; l->h = h2; l->src = src; l->free_w = h->w; l->end_w = j->end_w; }
}
static void vc_jobs_due (int all)
{
	unsigned k = 0;
	for (unsigned i = 0; i < g_njob; i++) { if (all || g_vfc >= g_job [i].due) vc_job_run (&g_job [i]); else g_job [k++] = g_job [i]; }
	g_njob = k;
}
/* the open scene ends: its target's pixels are the GPU's to write from now on */
static void vc_scene_end (void)
{
	if (g_scene_drawn && g_scene_tex) {
		const vc_obj *t = VC_OBJ (g_scene_tex);
		int late = !(t->kind == 1 && t->off);   /* its block is gone already: the pixels land on a free block whatever the GPU's delay */
		if (g_njob == VC_JOBS) vc_jobs_due (1);
		vc_job *j = &g_job [g_njob++]; j->tex = g_scene_tex; j->due = g_vfc + 2; vc_where_here (&j->end_w);
		if (late) { vc_job_run (j); g_njob--; }
	}
	g_scene_drawn = 0;
}
static void vc_scene_touch (unsigned fb)
{
	if (fb != g_scene_fb) {
		vc_scene_end (); g_scene_fb = fb;
		const vc_obj *f = VC_OBJ (fb);
		g_scene_tex = fb && f->kind == 3 ? f->tex : 0;   /* what the scene renders into, whatever happens to its framebuffer's name before the scene ends */
	}
	g_scene_drawn = 1;
}

/* ---------------- start ---------------- */
static int g_vc_ready;               /* shepgl_init ran: the env file is in, and vitaGL's init is what the pool starts from */
static int vc_on (void)
{
	if (g_vc >= 0) return g_vc;
	if (!g_vc_ready) return 0;
	const char *e = getenv ("VLX_VRAMCHECK");
	g_vc = e && *e && *e != '0' ? (*e == '2' ? 2 : 1) : 0;
	if (!g_vc) return 0;
	VC_M = vlx_host_map (VC_POOL); VC_D = vlx_host_map (VC_POOL);
	g_hist = vlx_host_map (VC_HIST * sizeof (vc_freed));
	g_o = vlx_host_map (VC_NAMES * sizeof (vc_obj));
	for (int i = 0; i < 4; i++) g_purge [i] = vlx_host_map (VC_PURGE * 4);
	if (!VC_M || !VC_D || !g_hist || !g_o || !g_purge [3]) { sceClibPrintf ("VRAMCHECK: no room for the pool's views: OFF\n"); g_vc = 0; return 0; }
	sceKernelCreateLwMutex (&g_vc_lw, "vramcheck", 2, 0, NULL);
	for (unsigned i = 0; i < VC_SMALL + 1; i++) { vc_chunk *b = smallbin (i); b->fd = b->bk = b; }
	/* sceClibMspaceCreate: the mstate's chunk at +8 (0x240 bytes), top after it (the dump's first chunks) */
	vc_chunk *p = (vc_chunk *) (VC_M + 0x248);
	const char *ps = getenv ("VLX_VRAMCHECK_POOL_KIB"), *hs = getenv ("VLX_VRAMCHECK_HIDDEN_KIB"), *ss = getenv ("VLX_VRAMCHECK_SPILL");
	unsigned pool = ps && *ps ? (unsigned) strtoul (ps, NULL, 10) << 10 : VC_POOL; if (pool > VC_POOL || pool < (32u << 20)) pool = VC_POOL;
	M.top = p; M.topsize = pool - 0x248 - 0x40; p->head = M.topsize | PINUSE;
	if (ss && *ss) { g_spill_on = 1; g_spill_budget = (unsigned) strtoul (ss, NULL, 10) << 10; }
	/* vitaGL's init, its order (gxm.o, vgl.o): the VDM, vertex and fragment rings, 3 display buffers, depth + stencil */
	static const struct { unsigned al, sz; } k_init [] = {
		{ 4096, 0x20000 }, { 4096, 0x200000 }, { 4096, 0x80000 },
		{ 4096, 0x200000 }, { 4096, 0x200000 }, { 4096, 0x200000 }, { 16, 960 * 544 * 4 }, { 16, 960 * 544 },
	};
	for (unsigned i = 0; i < sizeof k_init / sizeof k_init [0]; i++) vc_alloc (k_init [i].al, k_init [i].sz, "init", 0);
	if (hs && *hs) { unsigned h = (unsigned) strtoul (hs, NULL, 10) << 10; if (h) vc_alloc (16, h, "what the model does not hold", 0); }
	if (ps || hs || ss) sceClibPrintf ("VRAMCHECK pool %u KiB (what an IL block in video memory leaves), hidden %s KiB taken first (ESTIMATE: what the model does not hold), "
	                                   "spill %s%s (ESTIMATE: KiB of vitaGL's other pools, RAM + PHYCONT free, before newlib)\n", pool >> 10, hs ? hs : "0", ss ? "budget " : "", ss ? ss : "off");
	sceClibPrintf ("VRAMCHECK ON (%s): the model of vitaGL's VRAM pool, 96 MiB; a free poisons, an allocation verifies; the GPU's late writes modelled (vita/vlx/vramcheck.inc.c)\n", g_vc == 1 ? "the first FAIL ends the run" : "goes on after a FAIL");
	return 1;
}

/* kit #14 integration: an allocation the pool could not take (VLX_VRAMCHECK_SPILL) and its way back */
static void vc_spill (vc_obj *o, unsigned bytes)
{
	if (!g_spill_on || !bytes) return;
	g_spill_n++;
	if (g_spill_other + bytes <= g_spill_budget) { o->spill_other = bytes; g_spill_other += bytes; if (g_spill_other > g_spill_other_peak) g_spill_other_peak = g_spill_other; return; }
	o->spill_mem = malloc (bytes);
	if (!o->spill_mem) { sceClibPrintf ("VRAMCHECK SPILL: newlib refused %u bytes at f%u\n", bytes, g_swaps); return; }
	o->spill_other = 0; g_spill_nl += bytes; if (g_spill_nl > g_spill_nl_peak) g_spill_nl_peak = g_spill_nl;
}
static void vc_unspill (vc_obj *o)
{
	if (o->spill_mem) { free (o->spill_mem); o->spill_mem = NULL; g_spill_nl -= o->bytes; }
	if (o->spill_other) { g_spill_other -= o->spill_other; o->spill_other = 0; }
}

/* VLX_VRAM_FREE_KIB=model:<KiB> (glrec_vlx.c): the SANE reading, moving with the pool: 96 MiB less the model's bytes in use less <KiB>, what the model does not hold (vitaGL's shader,
 * uniform and small blocks, a spilled circular pool). Calibrate <KiB> on the app's device G1 line. 0 until the model is up. */
static unsigned vc_free_kib (unsigned hidden_kib)
{
	if (!vc_on ()) return 0;
	unsigned used = (g_vc_inuse >> 10) + hidden_kib;
	return used < (VC_POOL >> 10) ? (VC_POOL >> 10) - used : 0;
}

/* ---------------- the stand-in's GL calls ---------------- */
static void vc_gl_tex_image (unsigned name, int w, int h, unsigned bpp, unsigned bytes_override)
{
	if (!vc_on ()) return;
	vc_lock ();
	vc_obj *t = VC_OBJ (name);
	if (t->kind == 1 && t->off) { vc_free_rule (t->off, t->last_frame, name); t->off = 0; }
	vc_unspill (t);
	unsigned bytes = bytes_override ? bytes_override : (((unsigned) w + 7) & ~7u) * h * bpp;
	t->kind = 1; t->w = w; t->h = h; t->bytes = bytes; t->last_frame = ~0u; t->lastsrc = 0;
	t->off = vc_alloc (16, bytes, "a texture", name);
	if (!t->off) vc_spill (t, bytes);
	vc_unlock ();
}
static void vc_gl_del_tex (unsigned name)
{
	if (!vc_on ()) return;
	vc_lock ();
	vc_obj *t = VC_OBJ (name);
	if (t->kind == 1 && t->off) {
		if (t->attached) t->pending = 1;   /* the attached case when its framebuffer is not the active one: glDeleteFramebuffers frees it */
		else { vc_free_rule (t->off, t->last_frame, name); t->off = 0; }
	}
	if (t->kind == 1) vc_unspill (t);
	vc_unlock ();
}
static void vc_gl_attach (unsigned fb, unsigned tex)
{
	if (!vc_on ()) return;
	vc_lock ();
	vc_obj *f = VC_OBJ (fb), *t = VC_OBJ (tex);
	f->kind = 3; f->tex = tex; if (t->kind == 1) t->attached++;
	vc_unlock ();
}
static void vc_gl_del_fb (unsigned fb)
{
	if (!vc_on ()) return;
	vc_lock ();
	vc_obj *f = VC_OBJ (fb);
	if (f->kind == 3 && f->tex) {
		vc_obj *t = VC_OBJ (f->tex);
		if (t->kind == 1) {
			if (t->attached) t->attached--;
			if (t->pending && !t->attached) {
				t->pending = 0;
				if (t->off) {
					sceClibPrintf ("VRAMCHECK target %u (%ux%u, pool +0x%x) released at f%u: vgl_framecount %u, last_frame %d -> %s\n", f->tex, t->w, t->h, t->off, g_swaps, g_vfc, (int) t->last_frame, g_vfc - t->last_frame > 4u ? "freed NOW" : "the purge list");
					vc_free_rule (t->off, t->last_frame, f->tex); t->off = 0;   /* <- the rule: last_frame -1 (never sampled) frees NOW */
				}
			}
		}
		f->tex = 0;
	}
	vc_unlock ();
}
static void vc_gl_buffer_data (unsigned name, int size)
{
	if (!vc_on ()) return;
	vc_lock ();
	vc_obj *b = VC_OBJ (name);
	if (b->kind == 2 && b->off) {
		if (b->last_frame == ~0u || g_vfc - b->last_frame > 4u) vc_free_now (b->off, name); else vc_purge_push (b->off);
	}
	if (b->kind == 2) vc_unspill (b);
	b->kind = 2; b->bytes = size; b->last_frame = ~0u;
	b->off = size > 0 ? vc_alloc (16, size, "a buffer (glBufferData)", name) : 0;
	if (size > 0 && !b->off) vc_spill (b, (unsigned) size);
	vc_unlock ();
}
static void vc_gl_del_buffer (unsigned name)
{
	if (!vc_on ()) return;
	vc_lock ();
	vc_obj *b = VC_OBJ (name);
	if (b->kind == 2 && b->off) {
		if (b->last_frame == ~0u || g_vfc - b->last_frame > 4u) vc_free_now (b->off, name); else vc_purge_push (b->off);
		b->off = 0;
	}
	if (b->kind == 2) vc_unspill (b);
	vc_unlock ();
}
static void vc_gl_draw (unsigned fb, const GLuint *units, unsigned elems)
{
	if (!vc_on ()) return;
	vc_lock ();
	vc_scene_touch (fb);
	for (int i = 0; i < 3; i++) if (units [i]) { vc_obj *t = VC_OBJ (units [i]); if (t->kind == 1) t->last_frame = g_vfc; }
	if (fb && units [0]) { vc_obj *f = VC_OBJ (fb); if (f->kind == 3 && f->tex) VC_OBJ (f->tex)->lastsrc = units [0]; }
	for (int i = 0; i < 8; i++) if (g_attr [i]) { vc_obj *b = VC_OBJ (g_attr [i]); if (b->kind == 2) b->last_frame = g_vfc; }
	if (elems) { vc_obj *b = VC_OBJ (elems); if (b->kind == 2) b->last_frame = g_vfc; }
	vc_unlock ();
}
static void vc_gl_attr (unsigned index, unsigned buffer) { if (index < 8) g_attr [index] = buffer; }
static void vc_gl_clear (unsigned fb) { if (vc_on ()) { vc_lock (); vc_scene_touch (fb); vc_unlock (); } }
static void vc_gl_finish (void)
{
	if (!vc_on ()) return;
	vc_lock ();
	vc_scene_end (); g_scene_fb = 0;
	vc_jobs_due (1);
	vc_unlock ();
}
static void vc_swap (void)
{
	if (!vc_on ()) return;
	vc_lock ();
	vc_scene_end (); g_scene_fb = 0;
	g_vfc++;
	vc_jobs_due (0);
	vc_gc ();
	if (g_swaps % 300 == 0) vc_summary ("so far");
	vc_unlock ();
}
static void vc_boot_frame (void) { if (vc_on ()) { vc_lock (); g_vfc++; vc_unlock (); } }
/* shepgl_vram_alloc / free (the host's blocks: vglAlloc on the device): they take their place in the pool, their bytes stay the stand-in's own */
static void *vc_host_blocks [32];
static void vc_host_alloc (void *key, unsigned bytes, unsigned align)
{
	if (!vc_on ()) return;
	vc_lock ();
	g_stat_ext++;
	unsigned off = vc_alloc (16, bytes + align, "a host block (shepgl_vram_alloc)", 0);
	for (int i = 0; i < 16; i++) if (!vc_host_blocks [2 * i]) { vc_host_blocks [2 * i] = key; vc_host_blocks [2 * i + 1] = (void *) (uintptr_t) off; break; }
	vc_unlock ();
}
static void vc_host_free (void *key)
{
	if (!vc_on ()) return;
	vc_lock ();
	for (int i = 0; i < 16; i++) if (vc_host_blocks [2 * i] == key && key) { vc_free_now ((unsigned) (uintptr_t) vc_host_blocks [2 * i + 1], 0); vc_host_blocks [2 * i] = NULL; break; }
	vc_unlock ();
}
