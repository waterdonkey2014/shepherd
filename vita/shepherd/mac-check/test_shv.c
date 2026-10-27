/* device kit #8 boot track: k8shv_check (vita/shepherd/k8_shv.h) -- the launcher's and the engine's only check of a .shv clip before the
 * hardware decoder gets its access units. With a real clip (argv[1], an owner-local .shv made by vita/tools/splash_pack.py): the clip is
 * accepted, its index walks every access unit inside the pictures, the first access unit starts with a 4-byte start code and an SPS; then
 * every single-bit flip of the header + index and every truncation inside them is refused (both carry a checksum; a flip in the reserved
 * words is refused too, since they are inside the header's) -- the decoder never gets an access unit from outside the file.
 * Without argv[1] a synthetic clip is built. Build + run (exit 0 = PASS):
 *   cc -O2 -Wall -I vita/shepherd -o /tmp/test_shv vita/shepherd/mac-check/test_shv.c && /tmp/test_shv [clip.shv] */
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include "k8_shv.h"

static int fails, checks;
#define CHECK(c, ...) do { checks++; if (!(c)) { fails++; printf ("FAIL %s:%d: ", __FILE__, __LINE__); printf (__VA_ARGS__); printf ("\n"); } } while (0)

static unsigned char *synthetic (unsigned *len)
{
	const unsigned frames = 5, au = 40;
	unsigned n = K8SHV_HEADER_SIZE + 8 * frames + frames * au + 64;
	unsigned char *b = calloc (1, n);
	K8ShvHeader *h = (K8ShvHeader *) b;
	h->magic = K8SHV_MAGIC; h->version = K8SHV_VERSION; h->header_size = K8SHV_HEADER_SIZE;
	h->width = 960; h->height = 544; h->rate_num = 30; h->rate_den = 1; h->frames = frames;
	h->index_off = K8SHV_HEADER_SIZE; h->es_off = h->index_off + 8 * frames; h->es_bytes = frames * au; h->max_au = au;
	h->audio_off = h->es_off + h->es_bytes; h->audio_frames = 16; h->audio_bytes = 64; h->audio_rate = 48000; h->audio_channels = 2;
	h->src_frames = frames; h->src_rate_num = 30; h->src_rate_den = 1; h->flags = K8SHV_F_NO_BFRAMES;
	h->content_w = 960; h->content_h = 540; h->content_x = 0; h->content_y = 2;
	strcpy (h->clip, "synthetic");
	unsigned *ix = (unsigned *) (b + h->index_off);
	for (unsigned i = 0; i < frames; i++) {
		ix [2 * i] = i * au; ix [2 * i + 1] = au;
		unsigned char *a = b + h->es_off + i * au;
		a [0] = a [1] = a [2] = 0; a [3] = 1; a [4] = i ? 0x41 : 0x67;
	}
	h->index_fnv = k8shv_fnv32 (ix, frames * 8);
	h->checksum = k8shv_fnv32 (h, (unsigned) ((char *) &h->checksum - (char *) h));
	*len = n;
	return b;
}

/* every index entry the clip claims stays inside [0, len) */
static int inside (const unsigned char *b, unsigned len)
{
	const K8ShvHeader *h = (const K8ShvHeader *) b;
	for (unsigned i = 0; i < h->frames; i++) {
		unsigned sz;
		const unsigned char *a = k8shv_au (b, i, &sz);
		if (a < b || a + sz > b + len) return 0;
	}
	return !h->audio_bytes || (unsigned long long) h->audio_off + h->audio_bytes <= len;
}

int main (int argc, char **argv)
{
	unsigned len;
	unsigned char *b;
	if (argc > 1) {
		FILE *f = fopen (argv [1], "rb");
		if (!f) { printf ("cannot open %s\n", argv [1]); return 2; }
		fseek (f, 0, SEEK_END); len = (unsigned) ftell (f); fseek (f, 0, SEEK_SET);
		b = malloc (len);
		if (fread (b, 1, len, f) != len) { printf ("short read\n"); return 2; }
		fclose (f);
	} else
		b = synthetic (&len);
	const K8ShvHeader *h = (const K8ShvHeader *) b;
	const char *why = k8shv_check (b, len);
	CHECK (!why, "the clip is refused: %s", why ? why : "");
	if (why) { printf ("%d of %d checks failed\n", fails, checks); return 1; }
	CHECK (inside (b, len), "an index entry outside the file");
	unsigned sz;
	const unsigned char *a0 = k8shv_au (b, 0, &sz);
	CHECK (sz > 5 && !a0 [0] && !a0 [1] && !a0 [2] && a0 [3] == 1 && (a0 [4] & 0x1f) == 7, "the first access unit does not start with a 4-byte start code + an SPS");
	unsigned long total = 0, maxau = 0;
	for (unsigned i = 0; i < h->frames; i++) { k8shv_au (b, i, &sz); total += sz; if (sz > maxau) maxau = sz; }
	CHECK (total == h->es_bytes, "the access units add up to %lu, the header says %u", total, h->es_bytes);
	CHECK (maxau == h->max_au, "the largest access unit is %lu, the header says %u", maxau, h->max_au);
	CHECK (h->content_x + h->content_w <= h->width && h->content_y + h->content_h <= h->height, "the picture area is outside the frame");
	printf ("clip '%s': %u frames at %u/%u, %ux%u (picture %ux%u at %u,%u), %u B of pictures (largest %u), audio %u frames, file %u B\n", h->clip, h->frames, h->rate_num,
		h->rate_den, h->width, h->height, h->content_w, h->content_h, h->content_x, h->content_y, h->es_bytes, h->max_au, h->audio_frames, len);
	/* every bit of the header + index flipped: refused, or every claimed access unit still inside the file */
	unsigned span = h->index_off + 8 * h->frames, flips = 0, refused = 0;
	unsigned char *c = malloc (len);
	for (unsigned i = 0; i < span; i++)
		for (int bit = 0; bit < 8; bit++) {
			memcpy (c, b, len);
			c [i] ^= (unsigned char) (1u << bit);
			flips++;
			const char *w = k8shv_check (c, len);
			if (w) { refused++; continue; }
			CHECK (0, "a flip at byte %u bit %d was accepted", i, bit);
			CHECK (inside (c, len), "a flip at byte %u bit %d was accepted with an index outside the file", i, bit);
		}
	/* every truncation inside the header + index: refused */
	unsigned truncs = 0, trefused = 0;
	for (unsigned n = 0; n < span; n++) { truncs++; if (k8shv_check (b, n)) trefused++; }
	CHECK (trefused == truncs, "%u of %u truncations inside the header + index were accepted", truncs - trefused, truncs);
	printf ("bit flips in the header + index: %u, refused %u; truncations inside them %u, refused %u\n", flips, refused, truncs, trefused);
	printf ("%s: %d checks, %d failed\n", fails ? "FAIL" : "PASS", checks, fails);
	return fails ? 1 : 0;
}
