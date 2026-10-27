/* shepgl_gxp_blob.h (device kit #5, track boot): the layout of a serialized shader in this libvitaGL.a and the one change shepgl makes
 * to it, host-independent so vita/shepherd/mac-check/test_gxp_blob.c can check it on the Mac against a model of vitaGL's own code.
 *
 * vitaGL (custom_shaders.o, disassembled; vita/notes/kit5-boot.md):
 *   serialize_shader   (vglGetShaderBinary):  u32 N | the N uniform nodes' parameter indices, from the list's HEAD to its tail | GXP
 *   unserialize_shader (glShaderBinary):      reads N, copies + registers the GXP, then for each index in file order allocates a node
 *                                             and pushes it onto the list's HEAD -- so the list it rebuilds is the file's order reversed.
 * glCompileShader builds that list by pushing one node per uniform the compiler reports, so a blob saved from a compiled shader and
 * loaded as it is would give the loaded shader the compiled list in reverse. shepgl_gxp_save reverses the index run once
 * (gxpb_reverse), so the list a load rebuilds is node for node the compiled one. (vitaGL reads the list only for GL_ACTIVE_UNIFORMS /
 * glGetActiveUniform and the binary length, none of which GlBackend asks for; the order is kept anyway.) */
#ifndef SHEPGL_GXP_BLOB_H
#define SHEPGL_GXP_BLOB_H
#include <stdint.h>
#include <string.h>

#define GXPB_MAX_INDICES 4096u
#define GXPB_MIN_GXP 32u

/* NULL = the blob's frame is sound: *n indices, then *glen bytes of GXP at buf + 4 + 4 * *n; else why not */
static inline const char *gxpb_split(const void *buf, int len, uint32_t *n, uint32_t *glen)
{
	if (!buf || len < (int)(4 + GXPB_MIN_GXP))
		return "the blob is too short";
	uint32_t k;
	memcpy(&k, buf, 4);
	if (k > GXPB_MAX_INDICES || 4ull + 4ull * k + GXPB_MIN_GXP > (unsigned long long)len)
		return "its uniform index count does not fit its length";
	*n = k;
	*glen = (uint32_t)len - 4u - 4u * k;
	return NULL;
}

/* device kit #6 (integration; kit #5's DEVICE logs, devicelogs/20261015-kit5-phone): this vitaGL serializes a shader with the GXP part
 * PADDED TO 4 BYTES -- serialize_shader copies the shader object's program size field (+0x320), which holds the compiler's output size,
 * and that is the GXP's own size (sceGxmProgramGetSize) rounded up to a multiple of 4: on the device all 8 programs' vertex shaders came
 * back 1..3 bytes longer (339 -> 340, 315 -> 316, 363 -> 364, 369 -> 372), so kit #5's exact-size check refused every one and the cache
 * was never written (8 starts: "NOT written: ... the GXP says 339 bytes, the blob holds 340"). gxpb_gxp_fits: the blob's GXP part is the
 * GXP exactly, or (pad) exactly the GXP rounded up to 4 -- never anything longer or shorter. pad 0 = kit #5's exact check. */
static inline int gxpb_gxp_fits(uint32_t gxp_size, uint32_t glen, int pad)
{
	if (glen == gxp_size)
		return 1;
	return pad && gxp_size >= GXPB_MIN_GXP && gxp_size < glen && glen == ((gxp_size + 3u) & ~3u);
}

/* the padding after the GXP of a blob of n indices set to zero, so a stored / compared blob does not depend on what the compiler left in
 * those 0..3 bytes (nothing reads them: sceGxm takes the GXP's own size; vitaGL keeps the padded length as its size field, as compiled) */
static inline void gxpb_zero_pad(void *buf, uint32_t n, uint32_t gxp_size, uint32_t glen)
{
	if (glen > gxp_size)
		memset((unsigned char *)buf + 4 + 4 * n + gxp_size, 0, glen - gxp_size);
}

/* the uniform index run of a blob of n indices, reversed in place */
static inline void gxpb_reverse(void *buf, uint32_t n)
{
	unsigned char *ix = (unsigned char *)buf + 4;
	if (n < 2)
		return;
	for (uint32_t i = 0, j = n - 1; i < j; i++, j--) {
		uint32_t a, b;
		memcpy(&a, ix + 4 * i, 4);
		memcpy(&b, ix + 4 * j, 4);
		memcpy(ix + 4 * i, &b, 4);
		memcpy(ix + 4 * j, &a, 4);
	}
}
#endif
