/* device kit #10, track io (vita/notes/kit10-io.md): the DEVICE's LZ4 decoder (vita/shepherd/k10_io.inc.c, compiled here for the Mac
 * unchanged) over every chunk of a real chunked pack (shepherd.pk2), each decoded chunk's XXH32 against the pack's table; then a fuzz pass
 * (every LZ4 chunk truncated, bit-flipped and fed garbage lengths, with the output buffer exactly the chunk's size: the decoder must answer
 * -1 or a length, never read or write outside its buffers -- run under -fsanitize=address,undefined by run.sh).
 *   lz4check <shepherd.pk2> [fuzz rounds per chunk, default 3]
 * The pack is owner-local derived data: this reads it and prints counts only. */
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <stdint.h>
#define LOG(...) ((void) 0)
typedef unsigned int SceSize;
#include "../../shepherd/k10_io.inc.c"

static uint32_t rd32 (const unsigned char *p) { return p [0] | p [1] << 8 | p [2] << 16 | (uint32_t) p [3] << 24; }
static uint64_t rd64 (const unsigned char *p) { return rd32 (p) | (uint64_t) rd32 (p + 4) << 32; }
static uint32_t rotl (uint32_t x, int r) { return x << r | x >> (32 - r); }
static uint32_t xxh32 (const unsigned char *p, size_t n)
{
	const uint32_t P1 = 2654435761u, P2 = 2246822519u, P3 = 3266489917u, P4 = 668265263u, P5 = 374761393u;
	size_t i = 0; uint32_t h;
	if (n >= 16) {
		uint32_t v1 = P1 + P2, v2 = P2, v3 = 0, v4 = 0 - P1;
		do {
			v1 = rotl (v1 + rd32 (p + i) * P2, 13) * P1; i += 4; v2 = rotl (v2 + rd32 (p + i) * P2, 13) * P1; i += 4;
			v3 = rotl (v3 + rd32 (p + i) * P2, 13) * P1; i += 4; v4 = rotl (v4 + rd32 (p + i) * P2, 13) * P1; i += 4;
		} while (i <= n - 16);
		h = rotl (v1, 1) + rotl (v2, 7) + rotl (v3, 12) + rotl (v4, 18);
	} else h = P5;
	h += (uint32_t) n;
	while (i + 4 <= n) { h = rotl (h + rd32 (p + i) * P3, 17) * P4; i += 4; }
	while (i < n) { h = rotl (h + p [i] * P5, 11) * P1; i++; }
	h ^= h >> 15; h *= P2; h ^= h >> 13; h *= P3; h ^= h >> 16;
	return h;
}

int main (int argc, char **argv)
{
	if (argc < 2) { fprintf (stderr, "usage: lz4check <shepherd.pk2> [fuzz rounds]\n"); return 2; }
	int rounds = argc > 2 ? atoi (argv [2]) : 3;
	FILE *f = fopen (argv [1], "rb"); if (!f) { perror (argv [1]); return 2; }
	fseek (f, 0, SEEK_END); long len = ftell (f); fseek (f, 0, SEEK_SET);
	unsigned char *file = malloc (len); if (fread (file, 1, len, f) != (size_t) len) { fprintf (stderr, "short read\n"); return 2; } fclose (f);
	if (rd32 (file) != 0x4B504853u || (file [4] | file [5] << 8) != 2) { fprintf (stderr, "not a version-2 pack\n"); return 2; }
	int n = (int) rd32 (file + 64); uint64_t tAt = rd64 (file + 72);
	long ok = 0, bad = 0, lz4 = 0, stored = 0, other = 0, fz = 0, fzbad = 0; double bytes = 0;
	srand (12345);
	for (int i = 0; i < n; i++) {
		const unsigned char *e = file + tAt + (uint64_t) i * 40;
		int ll = (int) rd32 (e + 8), sl = (int) rd32 (e + 12); uint64_t at = rd64 (e + 16); int codec = e [24];
		uint32_t lh = rd32 (e + 32);
		if (at + sl > (uint64_t) len) { bad++; continue; }
		if (codec == 0) { stored++; if (xxh32 (file + at, ll) == lh) ok++; else bad++; continue; }
		if (codec != 1) { other++; continue; }
		lz4++;
		unsigned char *out = malloc (ll);   /* exactly the chunk's size: ASan sees any write past it */
		int r = k10_lz4 (out, ll, file + at, sl);
		if (r == ll && xxh32 (out, ll) == lh) { ok++; bytes += ll; } else { bad++; if (bad < 5) fprintf (stderr, "chunk %d: decoded %d of %d, hash %s\n", i, r, ll, r == ll ? "differs" : "-"); }
		/* fuzz: truncated, flipped, garbage -- -1 or a length, no out-of-bounds access */
		unsigned char *in = malloc (sl);
		for (int k = 0; k < rounds; k++) {
			memcpy (in, file + at, sl);
			int cut = sl;
			switch (k % 3) {
			case 0: cut = 1 + rand () % sl; break;                                     /* truncated */
			case 1: for (int z = 0; z < 4; z++) in [rand () % sl] ^= 1 << (rand () % 8); break;   /* bit flips */
			default: for (int z = 0; z < 16; z++) in [rand () % sl] = 0xFF; break;          /* runs of 255 length bytes */
			}
			unsigned char *in2 = malloc (cut); memcpy (in2, in, cut);
			int q = k10_lz4 (out, ll, in2, cut); fz++;
			if (q > ll) fzbad++;
			free (in2);
		}
		free (in); free (out);
	}
	printf ("lz4check %s: %d chunks -- lz4 %ld, stored %ld, other codecs %ld (not this decoder's); decoded + hashed OK %ld, BAD %ld; %.1f MiB through the device's LZ4 decoder; fuzz %ld decodes of damaged chunks, %ld answers past the buffer\n",
	        argv [1], n, lz4, stored, other, ok, bad, bytes / 1048576.0, fz, fzbad);
	return bad || fzbad ? 1 : 0;
}
