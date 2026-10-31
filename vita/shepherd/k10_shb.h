/* SHEPHERD-VITA device kit #10, track boot (vita/notes/kit10-boot.md): the .shb BOOT SCREEN file -- the game's own loading screen as the
 * engine draws it, LOSSLESS (the engine's own RGBA screenshots of it, vita/tools/bootscreen_pack.py; owner-local, never in git or a VPK):
 *   header (64 B) | index: frames x K10ShbRect (24 B) | data: one zlib stream per frame
 * Frame k = the pixels of ONE rectangle (x, y, w, h; RGBA, rows top-down, w * 4 bytes a row) -- the bounding box of everything that differs
 * from the frame shown before it (frame 0: the whole 960 x 544 screen; the loop's first frame, loop_start, also covers what differs from the
 * LAST frame, so the player can go from the last frame back to it). A player that keeps two display buffers brings the one that shows frame
 * k - 2 to frame k by drawing the rectangles of k - 1 and then k (outside them frame k = frame k - 1 = frame k - 2). w = 0 = no change.
 * Pure C, no libc: the header and index checks only (the zlib streams carry their own Adler-32, which inflate checks). */
#ifndef SHEPHERD_K10_SHB_H
#define SHEPHERD_K10_SHB_H

#define K10SHB_MAGIC 0x31424853u      /* "SHB1" */
#define K10SHB_HEADER_SIZE 64u

typedef struct K10ShbHeader {
	unsigned magic, version, header_size;
	unsigned width, height;              /* 960 x 544 */
	unsigned frames, loop_start;         /* frames in the file; after the last one the player goes on from loop_start */
	unsigned rate_num, rate_den;         /* pictures a second */
	unsigned index_off, data_off, data_bytes;
	unsigned max_raw;                    /* the largest rectangle's RGBA bytes */
	unsigned index_fnv;                  /* FNV-1a 32 of the index */
	unsigned src_fnv;                    /* FNV-1a 32 of the loop's first screenshot's first MiB (which recording it is) */
	unsigned checksum;                   /* FNV-1a 32 of the header bytes before this field */
} K10ShbHeader;
typedef char k10shb_header_size_check [(sizeof (K10ShbHeader) == K10SHB_HEADER_SIZE) ? 1 : -1];

typedef struct K10ShbRect {
	unsigned short x, y, w, h;
	unsigned off, len;                   /* the zlib stream: data_off + off, len bytes */
	unsigned raw_fnv;                    /* FNV-1a 32 of the rectangle's RGBA bytes (the packer's check; the player relies on zlib's Adler-32) */
	unsigned frame_fnv;                  /* FNV-1a 32 of the WHOLE frame as shown (960 x 544 RGBA): the player's check mode compares each
	                                        display buffer with it (SHEPHERD_K10_BOOTSCREEN_CHECK=1: tests, vlx) */
} K10ShbRect;
typedef char k10shb_rect_size_check [(sizeof (K10ShbRect) == 24u) ? 1 : -1];

static inline unsigned k10shb_fnv32 (const void *p, unsigned n)
{
	const unsigned char *b = (const unsigned char *) p;
	unsigned h = 2166136261u;
	for (unsigned i = 0; i < n; i++) { h ^= b [i]; h *= 16777619u; }
	return h;
}
/* 0 = usable (header, index, every rectangle inside the screen and every stream inside the file); else a short reason (static text) */
static inline const char *k10shb_check (const void *buf, unsigned len)
{
	const K10ShbHeader *h = (const K10ShbHeader *) buf;
	if (len < K10SHB_HEADER_SIZE) return "shorter than the header";
	if (h->magic != K10SHB_MAGIC || h->version != 1u || h->header_size != K10SHB_HEADER_SIZE) return "not a boot screen file (magic / version)";
	if (k10shb_fnv32 (h, (unsigned) ((const char *) &h->checksum - (const char *) h)) != h->checksum) return "header checksum (damaged)";
	if (h->width != 960u || h->height != 544u) return "not 960 x 544";
	if (!h->frames || h->frames > 4096u || h->loop_start >= h->frames || !h->rate_num || !h->rate_den) return "frame count / loop / rate";
	if (h->index_off != K10SHB_HEADER_SIZE || h->frames * 24u > len - h->index_off || h->data_off != h->index_off + h->frames * 24u
	    || h->data_off > len || h->data_bytes > len - h->data_off) return "index / data outside the file";
	if (k10shb_fnv32 ((const char *) buf + h->index_off, h->frames * 24u) != h->index_fnv) return "index checksum (damaged)";
	const K10ShbRect *r = (const K10ShbRect *) ((const char *) buf + h->index_off);
	if (r [0].x || r [0].y || r [0].w != 960u || r [0].h != 544u) return "frame 0 is not the whole screen";
	for (unsigned i = 0; i < h->frames; i++) {
		if (!r [i].w || !r [i].h) { if (r [i].w || r [i].h || r [i].len) return "an empty rectangle with data"; continue; }
		if ((unsigned) r [i].x + r [i].w > 960u || (unsigned) r [i].y + r [i].h > 544u) return "a rectangle outside the screen";
		if ((unsigned) r [i].w * r [i].h * 4u > h->max_raw) return "a rectangle larger than max_raw";
		if (!r [i].len || r [i].off > h->data_bytes || r [i].len > h->data_bytes - r [i].off) return "a stream outside the data";
	}
	return 0;
}
#endif
