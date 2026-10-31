/* device kit #10, track io (vita/notes/kit10-io.md): the host side of loading I/O -- included by host.c in every app.
 *
 * shepherd_vita_k10 (op, a, an, b, bn, ctx), the engine's one export for it (Shepherd.Engine Runtime/K10Io.cs):
 *   op 0  capabilities: 1 = LZ4, 2 = zstd (a SHEPHERD_K10_ZSTD build: vitasdk's libzstd.a linked), 4 = DMA (sceDmacMemcpy: the GL apps)
 *   op 1  LZ4 block decode: b[0..bn) -> a[0..an); returns the bytes written, which a valid chunk makes exactly an, or < 0 (malformed input:
 *         every length and offset is checked against both buffers -- a damaged card can never make it write outside a or read outside b)
 *   op 2  zstd frame decode (one decoder context per ctx 0..3: the reading thread, the decode-ahead worker, the preload, Verify files),
 *         or -2 when the build has no zstd
 *   op 3  copy b -> a (an bytes) by sceDmacMemcpy (1 = done by DMA, 0 = not available: the caller copies); only ever asked for copies INTO
 *         video memory (no CPU cache to invalidate on the destination; the kernel writes the source's cache lines back, as for vitaGL's
 *         texture uploads)
 * The decoders run on whatever thread calls (the decode-ahead worker on the 4th core / USER_2); they allocate nothing but the zstd contexts
 * (once per ctx, malloc: newlib). LZ4 is the format of lz4 1.10's LZ4_compress_HC blocks (no frame): the pack builder's encoder. */
#if defined (SHEPHERD_GL) && !defined (SHEPHERD_VLX_NO_DMAC)
#include <psp2/kernel/dmac.h>
#define K10_DMA 1
#endif
#ifdef SHEPHERD_K10_ZSTD
#include <zstd.h>
static ZSTD_DCtx *g_k10_zctx [4];
/* review: a context is taken for the call (an atomic flag per ctx); a second caller with the same ctx at the same time (two Verify files runs)
 * gets a context of its own for that call (created and freed: newlib) instead of racing inside libzstd */
static volatile int g_k10_zbusy [4];
#endif

/* LZ4 block format: a token (high nibble literal length, low nibble match length - 4; 15 = more length bytes, each 255 = keep going),
 * the literals, a 2-byte little-endian offset back into the output, the match; the last sequence is literals only. */
static int k10_lz4 (unsigned char *dst, int dstLen, const unsigned char *src, int srcLen)
{
	const unsigned char *s = src, *se = src + srcLen;
	unsigned char *d = dst, *de = dst + dstLen;
	if (srcLen <= 0 || dstLen < 0) return -1;
	for (;;) {
		if (s >= se) return -1;                  /* (a valid block ends in its literals-only sequence, below) */
		unsigned t = *s++;
		size_t lit = t >> 4;
		if (lit == 15) { unsigned x; do { if (s >= se) return -1; x = *s++; lit += x; } while (x == 255); }
		if (lit > (size_t) (se - s) || lit > (size_t) (de - d)) return -1;
		memcpy (d, s, lit); d += lit; s += lit;
		if (s == se) break;                      /* the last sequence: literals only */
		if (se - s < 2) return -1;
		size_t off = (size_t) s [0] | ((size_t) s [1] << 8); s += 2;
		if (off == 0 || off > (size_t) (d - dst)) return -1;
		size_t ml = t & 15;
		if (ml == 15) { unsigned x; do { if (s >= se) return -1; x = *s++; ml += x; } while (x == 255); }
		ml += 4;
		if (ml > (size_t) (de - d)) return -1;
		const unsigned char *m = d - off;
		if (off >= 8) {
			/* 8 bytes at a time while both ends stay inside the output (off >= 8: the copy never reads what it is writing) */
			while (ml >= 8) { memcpy (d, m, 8); d += 8; m += 8; ml -= 8; }
			while (ml--) *d++ = *m++;
		} else {
			while (ml--) *d++ = *m++;            /* overlapping (a run): byte by byte, as the format defines it */
		}
	}
	return (int) (d - dst);
}

static int g_k10_dma_refused;
int shepherd_vita_k10 (int op, void *a, int an, void *b, int bn, int ctx)
{
	switch (op) {
	case 0:
		return 1
#ifdef SHEPHERD_K10_ZSTD
			| 2
#endif
#ifdef K10_DMA
			| 4
#endif
			;
	case 1:
		return k10_lz4 ((unsigned char *) a, an, (const unsigned char *) b, bn);
	case 2:
#ifdef SHEPHERD_K10_ZSTD
	{
		if (ctx < 0 || ctx > 3) return -1;
		if (__sync_lock_test_and_set (&g_k10_zbusy [ctx], 1)) {
			ZSTD_DCtx *own = ZSTD_createDCtx (); if (!own) return -1;
			size_t r2 = ZSTD_decompressDCtx (own, a, (size_t) an, b, (size_t) bn);
			ZSTD_freeDCtx (own);
			return ZSTD_isError (r2) ? -1 : (int) r2;
		}
		if (!g_k10_zctx [ctx] && !(g_k10_zctx [ctx] = ZSTD_createDCtx ())) { __sync_lock_release (&g_k10_zbusy [ctx]); return -1; }
		size_t r = ZSTD_decompressDCtx (g_k10_zctx [ctx], a, (size_t) an, b, (size_t) bn);
		__sync_lock_release (&g_k10_zbusy [ctx]);
		return ZSTD_isError (r) ? -1 : (int) r;
	}
#else
		return -2;
#endif
	case 3:
#ifdef K10_DMA
		if (g_k10_dma_refused || an <= 0) return 0;
		if (sceDmacMemcpy (a, b, (SceSize) an) < 0) { g_k10_dma_refused = 1; LOG ("WARN K10 sceDmacMemcpy refused: copies by the CPU from now on"); return 0; }
		return 1;
#else
		return 0;
#endif
	}
	return -1;
}
