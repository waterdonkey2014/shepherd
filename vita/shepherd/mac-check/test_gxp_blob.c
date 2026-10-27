/* device kit #5 (track boot): the GXP cache's blob handling (vita/gltest/shepgl/shepgl_gxp_blob.h) against a model of this libvitaGL.a's
 * serialize_shader / unserialize_shader / glCompileShader uniform list (custom_shaders.o, disassembled: vita/notes/kit5-boot.md):
 *  - glCompileShader pushes one node per uniform the compiler reports onto the list's head (custom_shaders.o 0x3c5a-0x3c7e);
 *  - serialize_shader writes N, then each node's parameter index from the head (0xde-0x102), then the GXP;
 *  - unserialize_shader reads N and pushes a node per index, in file order, onto the head (0x1aa-0x1ca).
 * Checks: a blob saved and loaded as vitaGL gives it back rebuilds the list REVERSED; after gxpb_reverse it is node for node the compiled
 * list, for 0..40 uniforms; the GXP bytes are untouched; gxpb_split refuses short blobs, impossible index counts and every truncation.
 * Kit #6 integration: the device's blobs carry the GXP padded to 4 bytes (gxpb_gxp_fits / gxpb_zero_pad: the four device sizes, every
 * size / length pair up to 600 bytes, the padding zeroed in place, a padded blob round-tripped).
 * Build + run (exit 0 = PASS):  cc -O2 -Wall -I vita/gltest/shepgl -o /tmp/test_gxp_blob vita/shepherd/mac-check/test_gxp_blob.c && /tmp/test_gxp_blob */
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include "shepgl_gxp_blob.h"

static int fails, checks;
#define CHECK(c, ...) do { checks++; if (!(c)) { fails++; printf ("FAIL %s:%d: ", __FILE__, __LINE__); printf (__VA_ARGS__); printf ("\n"); } } while (0)

typedef struct node { uint32_t param; struct node *next; } node;   /* vitaGL: { const SceGxmProgramParameter *param; next } */

static node *push (node *head, uint32_t param) { node *n = malloc (sizeof *n); n->param = param; n->next = head; return n; }
static void free_list (node *h) { while (h) { node *n = h->next; free (h); h = n; } }

/* the model of serialize_shader (flag 0): u32 N, the indices from the head, the GXP */
static int serialize (const node *list, const unsigned char *gxp, uint32_t glen, unsigned char *out)
{
	uint32_t n = 0; for (const node *p = list; p; p = p->next) n++;
	memcpy (out, &n, 4);
	int pos = 4;
	for (const node *p = list; p; p = p->next) { memcpy (out + pos, &p->param, 4); pos += 4; }
	memcpy (out + pos, gxp, glen);
	return pos + (int) glen;
}
/* the model of unserialize_shader (flag 0): the GXP after the indices; one node per index in file order, each pushed onto the head */
static node *unserialize (const unsigned char *b, int len, unsigned char *gxp_out, uint32_t *glen)
{
	uint32_t n; memcpy (&n, b, 4);
	*glen = (uint32_t) len - 4 - 4 * n;
	memcpy (gxp_out, b + 4 + 4 * n, *glen);
	node *head = NULL;
	for (uint32_t i = 0; i < n; i++) { uint32_t ix; memcpy (&ix, b + 4 + 4 * i, 4); head = push (head, ix); }
	return head;
}

int main (void)
{
	unsigned char gxp [200], blob [4 + 4 * 64 + 200], back [200];
	for (int i = 0; i < 200; i++) gxp [i] = (unsigned char) (i * 37 + 11);
	memcpy (gxp, "GXP\0", 4);
	for (uint32_t count = 0; count <= 40; count++) {
		/* the compile: the compiler reports uniforms with parameter indices 3, 7, 11, ... in its order; each is pushed onto the head */
		node *compiled = NULL;
		for (uint32_t i = 0; i < count; i++) compiled = push (compiled, 3 + 4 * i);
		int len = serialize (compiled, gxp, sizeof gxp, blob);
		uint32_t n, glen;
		CHECK (gxpb_split (blob, len, &n, &glen) == NULL && n == count && glen == sizeof gxp, "split of a %u-index blob", count);

		/* loaded as vitaGL gives it back: the list comes out reversed */
		uint32_t g2;
		node *raw = unserialize (blob, len, back, &g2);
		int same = 1, rev = 1;
		{ const node *a = raw, *b = compiled; for (; a && b; a = a->next, b = b->next) if (a->param != b->param) same = 0; if (a || b) same = 0; }
		{ uint32_t want [64]; uint32_t k = 0; for (const node *p = compiled; p; p = p->next) want [k++] = p->param;
		  const node *a = raw; for (uint32_t i = 0; i < k; i++, a = a->next) if (!a || a->param != want [k - 1 - i]) rev = 0; }
		CHECK (rev && (count < 2 ? same : !same), "%u uniforms: unshuffled blob -> the list comes back reversed", count);
		free_list (raw);

		/* shepgl_gxp_save's blob: the index run reversed once -> the load rebuilds the compiled list node for node */
		gxpb_reverse (blob, n);
		node *loaded = unserialize (blob, len, back, &g2);
		const node *a = loaded, *b = compiled; same = 1;
		for (; a && b; a = a->next, b = b->next) if (a->param != b->param) same = 0;
		if (a || b) same = 0;
		CHECK (same, "%u uniforms: after gxpb_reverse the loaded list is the compiled one", count);
		CHECK (g2 == sizeof gxp && memcmp (back, gxp, sizeof gxp) == 0, "%u uniforms: the GXP bytes are untouched", count);
		gxpb_reverse (blob, n);   /* twice = the original serialization */
		node *again = unserialize (blob, len, back, &g2);
		int rev2 = 1; { uint32_t want [64]; uint32_t k = 0; for (const node *p = compiled; p; p = p->next) want [k++] = p->param;
		  const node *x = again; for (uint32_t i = 0; i < k; i++, x = x->next) if (!x || x->param != want [k - 1 - i]) rev2 = 0; }
		CHECK (rev2, "%u uniforms: reversing twice gives vitaGL's own serialization back", count);
		free_list (again); free_list (loaded); free_list (compiled);

		/* every truncation that cuts into the minimum GXP or the indices is refused by the frame check */
		int refused_all = 1;
		for (int cut = 0; cut < (int) (4 + 4 * count + GXPB_MIN_GXP); cut++) if (gxpb_split (blob, cut, &n, &glen) == NULL) refused_all = 0;
		CHECK (refused_all, "%u uniforms: every blob shorter than 4 + 4N + %u bytes is refused", count, GXPB_MIN_GXP);
	}
	uint32_t n, glen, big = GXPB_MAX_INDICES + 1;
	unsigned char hdr [64] = { 0 };
	memcpy (hdr, &big, 4);
	CHECK (gxpb_split (hdr, sizeof hdr, &n, &glen) != NULL, "an index count above %u is refused", GXPB_MAX_INDICES);
	uint32_t huge = 0x40000001u; memcpy (hdr, &huge, 4);   /* 4 + 4N overflows 32 bits */
	CHECK (gxpb_split (hdr, sizeof hdr, &n, &glen) != NULL, "an index count whose byte size wraps 32 bits is refused");
	CHECK (gxpb_split (NULL, 100, &n, &glen) != NULL && gxpb_split (hdr, -1, &n, &glen) != NULL, "no buffer / a negative length is refused");
	/* kit #6 integration: the device's padded blobs (kit #5's DEVICE logs: the GXP part rounded up to 4 bytes) */
	{
		static const uint32_t dev [][2] = { { 339, 340 }, { 315, 316 }, { 363, 364 }, { 369, 372 } };   /* GXP size, the blob's GXP part */
		for (int i = 0; i < 4; i++) {
			CHECK (gxpb_gxp_fits (dev [i][0], dev [i][1], 1), "the device's %u-byte GXP in a %u-byte part is taken (pad on)", dev [i][0], dev [i][1]);
			CHECK (!gxpb_gxp_fits (dev [i][0], dev [i][1], 0), "... and refused with SHEPHERD_K6_GXP_PAD=0 (kit #5's exact check)");
		}
		int bad = 0, good = 0;
		for (uint32_t sz = GXPB_MIN_GXP; sz < 600; sz++)
			for (uint32_t gl = sz > 8 ? sz - 8 : 0; gl < sz + 9; gl++) {
				int want = gl == sz || gl == ((sz + 3u) & ~3u);
				if (gxpb_gxp_fits (sz, gl, 1) != want) bad++; else good++;
				if (gxpb_gxp_fits (sz, gl, 0) != (gl == sz)) bad++;
			}
		CHECK (bad == 0, "gxpb_gxp_fits: only the exact size or the size rounded up to 4 (%d of %d cases wrong)", bad, bad + good);
		CHECK (!gxpb_gxp_fits (GXPB_MIN_GXP - 1, GXPB_MIN_GXP, 1), "a GXP below the minimum size is never padded");
		/* the padding is zeroed in place, nothing else changes */
		unsigned char b [4 + 8 + 344]; memset (b, 0xEE, sizeof b);
		uint32_t two = 2; memcpy (b, &two, 4);
		unsigned char ref [sizeof b]; memcpy (ref, b, sizeof b);
		gxpb_zero_pad (b, 2, 339, 340);
		int changed = 0, padz = b [4 + 8 + 339] == 0;
		for (size_t k = 0; k < sizeof b; k++) if (k != 4 + 8 + 339 && b [k] != ref [k]) changed++;
		CHECK (padz && changed == 0, "gxpb_zero_pad zeroes exactly the padding byte (changed elsewhere: %d)", changed);
		memcpy (b, ref, sizeof b); gxpb_zero_pad (b, 2, 340, 340);
		CHECK (memcmp (b, ref, sizeof b) == 0, "gxpb_zero_pad leaves an unpadded blob alone");
		/* a padded blob saved and loaded the way vitaGL does: the indices and the GXP bytes survive, the length is the padded one */
		unsigned char g [339]; for (int k = 0; k < 339; k++) g [k] = (unsigned char) (k * 13 + 1); memcpy (g, "GXP\0", 4);
		unsigned char pb [4 + 4 * 3 + 340]; uint32_t three = 3, ix [3] = { 9, 5, 1 };
		memcpy (pb, &three, 4); memcpy (pb + 4, ix, 12); memcpy (pb + 16, g, 339); pb [16 + 339] = 0x7f;   /* whatever the compiler left */
		uint32_t pn, pgl;
		CHECK (gxpb_split (pb, sizeof pb, &pn, &pgl) == NULL && pn == 3 && pgl == 340 && gxpb_gxp_fits (339, pgl, 1), "a padded blob splits into 3 indices + a 340-byte part that fits");
		gxpb_zero_pad (pb, pn, 339, pgl);
		unsigned char gb [340]; uint32_t gl2; node *l = unserialize (pb, sizeof pb, gb, &gl2);
		CHECK (gl2 == 340 && memcmp (gb, g, 339) == 0 && gb [339] == 0, "unserialized: the 339 GXP bytes intact, the padding zero, vitaGL's size field 340 (as compiled)");
		free_list (l);
	}
	printf ("test_gxp_blob: %d checks, %d FAIL\n", checks, fails);
	return fails ? 1 : 0;
}
