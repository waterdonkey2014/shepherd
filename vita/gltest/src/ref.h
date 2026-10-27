/* CPU reference for the gltest: what GL ES 2.0 semantics + GlBackend's programs and blend factors must produce.
 * Images are RGBA8, top-down (row 0 = first row in memory = pixel y 0 of GlBackend's targets). Math in double; a fragment
 * output is clamped to [0,1] and rounded to the nearest byte, as a UNORM8 render target stores it. */
#ifndef GLTEST_REF_H
#define GLTEST_REF_H
#include <stdint.h>
#include <stddef.h>

typedef struct { int w, h; uint8_t *p; } img;
img img_new(int w, int h);
void img_free(img *m);
void img_fill(img *m, const uint8_t c[4]);
static inline uint8_t *img_px(const img *m, int x, int y) { return m->p + ((size_t)y * (size_t)m->w + (size_t)x) * 4; }

enum { RT_RGBA8, RT_A8 };   /* A8 = GL_ALPHA: samples as (0, 0, 0, a) */
typedef struct { int w, h, fmt; const uint8_t *p; } rtex;
void ref_sample(const rtex *t, double u, double v, int linear, int repeat, double out[4]);

/* BC1/BC3 exactly like engine/Shepherd.Assets/.../Textures/Bcn.cs (Ex5/Ex6 bit replication, (2a+b+1)/3 rounding,
 * BC3 colour always 4-colour, BC1 punch-through when c0 <= c1). Blocks in row-major block order; out is w x h RGBA8. */
void ref_bc1_decode(const uint8_t *blocks, int w, int h, uint8_t *rgba);
void ref_bc3_decode(const uint8_t *blocks, int w, int h, uint8_t *rgba);
/* same, but a BC3 colour block with c0 <= c1 decoded as 3-colour + transparent (what some GPUs do) */
void ref_bc3_decode_3colour(const uint8_t *blocks, int w, int h, uint8_t *rgba);

uint8_t ref_u8(double x);

/* SDL3 blend modes as GlBackend.SetBlend maps them to glBlendFuncSeparate */
enum { BL_NONE, BL_BLEND, BL_ADD, BL_MOD, BL_MUL, BL_PREMUL, BL_ADD_PREMUL, BL_COUNT };
extern const char *const ref_blend_names[BL_COUNT];
void ref_blend(uint8_t dst[4], const double src[4], int mode);

enum { SH_SPRITE, SH_SDF, SH_COPY, SH_TINTBLACK };
typedef struct {
	int x0, y0, x1, y1;            /* pixel rect, end exclusive (top-down) */
	double u0, v0, u1, v1;          /* uv at the (x0,y0) and (x1,y1) corners */
	double tl[4], tr[4], bl[4];     /* vertex colours (affine over the quad: br = tr + bl - tl) */
	double dark[4];                 /* SH_TINTBLACK: the second colour */
	const rtex *tex;                /* NULL = the 1x1 white texture */
	int linear, repeat, shader, blend;
	int clip, cx, cy, cw, ch;       /* scissor, top-down like GlBackend's */
} rquad;
void ref_quad(img *dst, const rquad *q);

/* GlBackend.RunStylizer (the owner-local stylizer.frag) and the owner-local layer map: defined in the owner-local
 * engine/Derived/vita-gltest/stylizer_ref.c, compiled in only when present (GLTEST_HAVE_STYLIZER) */
typedef struct { const img *src, *layers, *lut1, *lut2; int block; double intensity, lerp; } rsty;
void ref_stylize(img *dst, const rsty *a);
void ref_layer_map(const uint8_t *pat_rgba, int pw, int ph, int layers1, int layers2, img *out);

typedef struct {
	int maxd; long over, n; double mean;
	int nex; int ex_x[4], ex_y[4]; uint8_t ex_g[4][4], ex_e[4][4];   /* first mismatches over tol */
} cmpres;
void ref_compare(const img *gpu, const img *exp, int x0, int y0, int w, int h, int tol, cmpres *r);

/* deterministic generators */
uint32_t xs32(uint32_t *s);
#endif
