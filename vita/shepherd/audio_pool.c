/* SHEPHERD-VITA Stage C: the audio pool (see audio_pool.h). Block = 16-byte header + payload, sizes multiples of 16:
 *   hdr.size  bytes of the block including its header; 0 marks the end sentinel
 *   hdr.prev  size of the previous block (0 for the first)
 *   hdr.used  1 = handed out
 * First fit from the start; a block is split when the rest can hold a header + 16 bytes; free() merges with both neighbours. */
#include "audio_pool.h"
#include <string.h>

typedef struct ap_hdr { uint32_t size, prev, used, magic; } ap_hdr;
#define AP_MAGIC 0x41504f4cu   /* "APOL": a stray free() of a foreign pointer is caught, not followed */
#define AP_H ((uint32_t) sizeof (ap_hdr))

static ap_hdr *at (ap_pool *p, uint32_t off) { return (ap_hdr *) (p->base + off); }
static uint32_t off_of (ap_pool *p, ap_hdr *h) { return (uint32_t) ((uint8_t *) h - p->base); }

int ap_init (ap_pool *p, void *mem, size_t bytes, void (*lock) (void *), void (*unlock) (void *), void *lock_arg)
{
	memset (p, 0, sizeof (*p));
	uintptr_t b = ((uintptr_t) mem + 15u) & ~(uintptr_t) 15u;
	size_t lost = b - (uintptr_t) mem;
	if (!mem || bytes < lost + 4 * AP_H || bytes - lost > 0xF0000000u)
		return -1;
	p->base = (uint8_t *) b;
	p->size = (uint32_t) ((bytes - lost - AP_H) & ~(size_t) 15u);   /* the sentinel header sits at base + size */
	ap_hdr *h = at (p, 0);
	h->size = p->size; h->prev = 0; h->used = 0; h->magic = AP_MAGIC;
	ap_hdr *s = at (p, p->size);
	s->size = 0; s->prev = p->size; s->used = 1; s->magic = AP_MAGIC;
	p->lock = lock; p->unlock = unlock; p->lock_arg = lock_arg;
	return 0;
}

void *ap_alloc (ap_pool *p, size_t bytes, size_t align)
{
	if (align > 16 || bytes > 0xE0000000u) {
		p->failures++;
		return NULL;
	}
	uint32_t need = (uint32_t) ((bytes + AP_H + 15u) & ~(size_t) 15u);
	if (need < 2 * AP_H)
		need = 2 * AP_H;
	if (p->lock) p->lock (p->lock_arg);
	void *r = NULL;
	for (uint32_t o = 0; o < p->size;) {
		ap_hdr *h = at (p, o);
		if (!h->used && h->size >= need) {
			uint32_t rest = h->size - need;
			if (rest >= 2 * AP_H) {   /* split: the rest becomes a free block after this one */
				ap_hdr *n = at (p, o + need);
				n->size = rest; n->prev = need; n->used = 0; n->magic = AP_MAGIC;
				at (p, o + h->size)->prev = rest;
				h->size = need;
			}
			h->used = 1;
			p->used += h->size;
			if (p->used > p->peak)
				p->peak = p->used;
			p->allocs++;
			r = (uint8_t *) h + AP_H;
			break;
		}
		o += h->size;
	}
	if (!r)
		p->failures++;
	if (p->unlock) p->unlock (p->lock_arg);
	return r;
}

void ap_free (ap_pool *p, void *ptr)
{
	if (!ptr)
		return;
	ap_hdr *h = (ap_hdr *) ((uint8_t *) ptr - AP_H);
	if ((uint8_t *) h < p->base || (uint8_t *) h >= p->base + p->size || ((uintptr_t) ptr & 15u))
		return;   /* not ours */
	if (p->lock) p->lock (p->lock_arg);
	if (h->magic != AP_MAGIC || !h->used) {
		p->failures++;   /* double free / corrupted header: refused, counted */
		if (p->unlock) p->unlock (p->lock_arg);
		return;
	}
	h->used = 0;
	p->used -= h->size;
	p->frees++;
	uint32_t o = off_of (p, h);
	ap_hdr *n = at (p, o + h->size);
	if (!n->used) {   /* merge the next block (the sentinel is always "used") */
		h->size += n->size;
		n->magic = 0;
		at (p, o + h->size)->prev = h->size;
	}
	if (h->prev) {
		ap_hdr *pv = at (p, o - h->prev);
		if (!pv->used) {   /* merge into the previous block */
			pv->size += h->size;
			h->magic = 0;
			at (p, o - h->prev + pv->size)->prev = pv->size;
		}
	}
	if (p->unlock) p->unlock (p->lock_arg);
}

int ap_check (ap_pool *p, uint32_t *largest_free, uint32_t *free_blocks)
{
	uint32_t o = 0, prev = 0, lf = 0, nf = 0, used = 0;
	int prev_free = 0;
	if (p->lock) p->lock (p->lock_arg);
	int bad = 0;
	while (o < p->size) {
		ap_hdr *h = at (p, o);
		if (h->magic != AP_MAGIC || h->size < 2 * AP_H || (h->size & 15u) || h->prev != prev || o + h->size > p->size) { bad = 1; break; }
		if (!h->used) {
			if (prev_free) { bad = 2; break; }   /* two free blocks touch: a missed merge */
			nf++;
			if (h->size > lf) lf = h->size;
		} else
			used += h->size;
		prev_free = !h->used;
		prev = h->size;
		o += h->size;
	}
	if (!bad && (o != p->size || at (p, o)->size != 0 || at (p, o)->prev != prev || used != p->used))
		bad = 3;
	if (p->unlock) p->unlock (p->lock_arg);
	if (largest_free) *largest_free = lf;
	if (free_blocks) *free_blocks = nf;
	return bad;
}
