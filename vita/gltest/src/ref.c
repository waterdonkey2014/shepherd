/* CPU reference for the gltest (see ref.h). Built with -ffp-contract=off on both sides. */
#include "ref.h"
#include <math.h>
#include <stdlib.h>
#include <string.h>

img img_new(int w, int h)
{
	img m = { w, h, (uint8_t *)calloc((size_t)w * (size_t)h, 4) };
	return m;
}
void img_free(img *m) { free(m->p); m->p = NULL; }
void img_fill(img *m, const uint8_t c[4])
{
	for (size_t i = 0; i < (size_t)m->w * (size_t)m->h; i++) memcpy(m->p + i * 4, c, 4);
}

uint32_t xs32(uint32_t *s) { uint32_t x = *s; x ^= x << 13; x ^= x >> 17; x ^= x << 5; return *s = x; }

uint8_t ref_u8(double x)
{
	if (!(x > 0)) return 0;
	if (x >= 1) return 255;
	return (uint8_t)floor(x * 255.0 + 0.5);
}

static int wrapc(int i, int n, int repeat)
{
	if (repeat) { i %= n; return i < 0 ? i + n : i; }
	return i < 0 ? 0 : i >= n ? n - 1 : i;
}
static void texel(const rtex *t, int x, int y, double o[4])
{
	const uint8_t *p = t->fmt == RT_A8 ? t->p + (size_t)y * t->w + x : t->p + ((size_t)y * t->w + x) * 4;
	if (t->fmt == RT_A8) { o[0] = o[1] = o[2] = 0; o[3] = p[0] / 255.0; }
	else for (int c = 0; c < 4; c++) o[c] = p[c] / 255.0;
}
void ref_sample(const rtex *t, double u, double v, int linear, int repeat, double out[4])
{
	if (!t) { out[0] = out[1] = out[2] = out[3] = 1; return; }
	double fx = u * t->w, fy = v * t->h;
	if (!linear) {
		texel(t, wrapc((int)floor(fx), t->w, repeat), wrapc((int)floor(fy), t->h, repeat), out);
		return;
	}
	fx -= 0.5; fy -= 0.5;
	int x0 = (int)floor(fx), y0 = (int)floor(fy);
	double ax = fx - x0, ay = fy - y0, a[4], b[4], c[4], d[4];
	texel(t, wrapc(x0, t->w, repeat), wrapc(y0, t->h, repeat), a);
	texel(t, wrapc(x0 + 1, t->w, repeat), wrapc(y0, t->h, repeat), b);
	texel(t, wrapc(x0, t->w, repeat), wrapc(y0 + 1, t->h, repeat), c);
	texel(t, wrapc(x0 + 1, t->w, repeat), wrapc(y0 + 1, t->h, repeat), d);
	for (int k = 0; k < 4; k++) out[k] = (a[k] * (1 - ax) + b[k] * ax) * (1 - ay) + (c[k] * (1 - ax) + d[k] * ax) * ay;
}

/* ---------------------------------------------------------------- BCn (Bcn.cs rules) */
static uint8_t ex5(int v) { v &= 31; return (uint8_t)((v << 3) | (v >> 2)); }
static uint8_t ex6(int v) { v &= 63; return (uint8_t)((v << 2) | (v >> 4)); }
static void colour_block(const uint8_t *b, uint8_t tile[64], int punch)
{
	int c0 = b[0] | (b[1] << 8), c1 = b[2] | (b[3] << 8);
	uint8_t pal[16];
	pal[0] = ex5(c0 >> 11); pal[1] = ex6(c0 >> 5); pal[2] = ex5(c0); pal[3] = 255;
	pal[4] = ex5(c1 >> 11); pal[5] = ex6(c1 >> 5); pal[6] = ex5(c1); pal[7] = 255;
	if (c0 > c1 || !punch) {
		for (int c = 0; c < 3; c++) {
			pal[8 + c] = (uint8_t)((2 * pal[c] + pal[4 + c] + 1) / 3);
			pal[12 + c] = (uint8_t)((pal[c] + 2 * pal[4 + c] + 1) / 3);
		}
		pal[11] = 255; pal[15] = 255;
	} else {
		for (int c = 0; c < 3; c++) pal[8 + c] = (uint8_t)((pal[c] + pal[4 + c]) / 2);
		pal[11] = 255;
		pal[12] = pal[13] = pal[14] = 0; pal[15] = 0;
	}
	uint32_t idx = (uint32_t)b[4] | ((uint32_t)b[5] << 8) | ((uint32_t)b[6] << 16) | ((uint32_t)b[7] << 24);
	for (int i = 0; i < 16; i++) { int k = (int)((idx >> (2 * i)) & 3) * 4; memcpy(tile + i * 4, pal + k, 4); }
}
static void alpha_block(const uint8_t *b, uint8_t tile[64])
{
	int a[8];
	a[0] = b[0]; a[1] = b[1];
	if (a[0] > a[1]) for (int k = 1; k <= 6; k++) a[k + 1] = ((7 - k) * a[0] + k * a[1] + 3) / 7;
	else { for (int k = 1; k <= 4; k++) a[k + 1] = ((5 - k) * a[0] + k * a[1] + 2) / 5; a[6] = 0; a[7] = 255; }
	uint64_t bits = 0;
	for (int i = 0; i < 6; i++) bits |= (uint64_t)b[2 + i] << (8 * i);
	for (int i = 0; i < 16; i++) tile[i * 4 + 3] = (uint8_t)a[(bits >> (3 * i)) & 7];
}
static void decode(const uint8_t *blocks, int w, int h, uint8_t *rgba, int bc3, int bc3_punch)
{
	int bw = (w + 3) / 4, bh = (h + 3) / 4, bb = bc3 ? 16 : 8;
	uint8_t tile[64];
	for (int by = 0; by < bh; by++)
		for (int bx = 0; bx < bw; bx++) {
			const uint8_t *b = blocks + ((size_t)by * bw + bx) * bb;
			if (bc3) { colour_block(b + 8, tile, bc3_punch); alpha_block(b, tile); }
			else colour_block(b, tile, 1);
			for (int y = 0; y < 4; y++)
				for (int x = 0; x < 4; x++) {
					int px = bx * 4 + x, py = by * 4 + y;
					if (px < w && py < h) {
						uint8_t *d = rgba + ((size_t)py * w + px) * 4;
						if (bc3 && bc3_punch) {
							/* 3-colour interpretation: index 3 is transparent black; keep the BC3 alpha for the others */
							memcpy(d, tile + (y * 4 + x) * 4, 3);
							d[3] = tile[(y * 4 + x) * 4 + 3];
						} else memcpy(d, tile + (y * 4 + x) * 4, 4);
					}
				}
		}
}
void ref_bc1_decode(const uint8_t *blocks, int w, int h, uint8_t *rgba) { decode(blocks, w, h, rgba, 0, 0); }
void ref_bc3_decode(const uint8_t *blocks, int w, int h, uint8_t *rgba) { decode(blocks, w, h, rgba, 1, 0); }
void ref_bc3_decode_3colour(const uint8_t *blocks, int w, int h, uint8_t *rgba) { decode(blocks, w, h, rgba, 1, 1); }

/* ---------------------------------------------------------------- blending */
const char *const ref_blend_names[BL_COUNT] = { "NONE", "BLEND", "ADD", "MOD", "MUL", "BLEND_PREMULTIPLIED", "ADD_PREMULTIPLIED" };

void ref_blend(uint8_t dst[4], const double src_in[4], int mode)
{
	double s[4], d[4], o[4];
	for (int k = 0; k < 4; k++) { s[k] = src_in[k] < 0 ? 0 : src_in[k] > 1 ? 1 : src_in[k]; d[k] = dst[k] / 255.0; }
	switch (mode) {
	case BL_NONE: for (int k = 0; k < 4; k++) o[k] = s[k]; break;
	case BL_BLEND: for (int k = 0; k < 3; k++) o[k] = s[k] * s[3] + d[k] * (1 - s[3]); o[3] = s[3] + d[3] * (1 - s[3]); break;  /* SRC_ALPHA, 1-SRC_ALPHA, ONE, 1-SRC_ALPHA */
	case BL_ADD: for (int k = 0; k < 3; k++) o[k] = s[k] * s[3] + d[k]; o[3] = d[3]; break;                                   /* SRC_ALPHA, ONE, ZERO, ONE */
	case BL_MOD: for (int k = 0; k < 3; k++) o[k] = d[k] * s[k]; o[3] = d[3]; break;                                          /* ZERO, SRC_COLOR, ZERO, ONE */
	case BL_MUL: for (int k = 0; k < 3; k++) o[k] = s[k] * d[k] + d[k] * (1 - s[3]); o[3] = d[3]; break;                       /* DST_COLOR, 1-SRC_ALPHA, ZERO, ONE */
	case BL_PREMUL: for (int k = 0; k < 3; k++) o[k] = s[k] + d[k] * (1 - s[3]); o[3] = s[3] + d[3] * (1 - s[3]); break;     /* ONE, 1-SRC_ALPHA, ONE, 1-SRC_ALPHA */
	case BL_ADD_PREMUL: for (int k = 0; k < 3; k++) o[k] = s[k] + d[k]; o[3] = d[3]; break;                                    /* ONE, ONE, ZERO, ONE */
	default: for (int k = 0; k < 4; k++) o[k] = s[k]; break;
	}
	for (int k = 0; k < 4; k++) dst[k] = ref_u8(o[k]);
}

/* ---------------------------------------------------------------- quads */
void ref_quad(img *dst, const rquad *q)
{
	int x0 = q->x0 < 0 ? 0 : q->x0, y0 = q->y0 < 0 ? 0 : q->y0, x1 = q->x1 > dst->w ? dst->w : q->x1, y1 = q->y1 > dst->h ? dst->h : q->y1;
	if (q->clip) {
		if (x0 < q->cx) x0 = q->cx;
		if (y0 < q->cy) y0 = q->cy;
		if (x1 > q->cx + q->cw) x1 = q->cx + q->cw;
		if (y1 > q->cy + q->ch) y1 = q->cy + q->ch;
	}
	double W = q->x1 - q->x0, H = q->y1 - q->y0;
	for (int y = y0; y < y1; y++)
		for (int x = x0; x < x1; x++) {
			double s = (x + 0.5 - q->x0) / W, t = (y + 0.5 - q->y0) / H, c[4], tx[4], o[4];
			for (int k = 0; k < 4; k++) c[k] = q->tl[k] + (q->tr[k] - q->tl[k]) * s + (q->bl[k] - q->tl[k]) * t;
			ref_sample(q->tex, q->u0 + (q->u1 - q->u0) * s, q->v0 + (q->v1 - q->v0) * t, q->linear, q->repeat, tx);
			switch (q->shader) {
			case SH_SDF: {
				double a = (tx[3] - 0.44) / 0.12; a = a < 0 ? 0 : a > 1 ? 1 : a;
				o[0] = c[0]; o[1] = c[1]; o[2] = c[2]; o[3] = a * a * (3 - 2 * a) * c[3];
				break;
			}
			case SH_COPY: for (int k = 0; k < 4; k++) o[k] = tx[k]; break;
			case SH_TINTBLACK:
				for (int k = 0; k < 3; k++) o[k] = ((tx[3] - 1) * q->dark[3] + 1 - tx[k]) * q->dark[k] + tx[k] * c[k];
				o[3] = tx[3] * c[3];
				break;
			default: for (int k = 0; k < 4; k++) o[k] = tx[k] * c[k]; break;
			}
			ref_blend(img_px(dst, x, y), o, q->blend);
		}
}

/* ---------------------------------------------------------------- Stylizer: ref_stylize / ref_layer_map (ref.h) are owner-local --
 * engine/Derived/vita-gltest/stylizer_ref.c (a port of a third-party effect's maths; fid1-fixup D1). CMakeLists.txt / build.sh compile
 * it in when present; without it the gltest builds no stylizer programs and skips the Stylizer checks (GLTEST_HAVE_STYLIZER 0). */

/* ---------------------------------------------------------------- compare */
void ref_compare(const img *g, const img *e, int x0, int y0, int w, int h, int tol, cmpres *r)
{
	memset(r, 0, sizeof *r);
	double sum = 0;
	for (int y = y0; y < y0 + h; y++)
		for (int x = x0; x < x0 + w; x++) {
			const uint8_t *a = img_px(g, x, y), *b = img_px(e, x, y);
			int m = 0;
			for (int k = 0; k < 4; k++) { int d = abs((int)a[k] - (int)b[k]); if (d > m) m = d; sum += d; }
			if (m > r->maxd) r->maxd = m;
			if (m > tol) {
				if (r->nex < 4) { r->ex_x[r->nex] = x; r->ex_y[r->nex] = y; memcpy(r->ex_g[r->nex], a, 4); memcpy(r->ex_e[r->nex], b, 4); r->nex++; }
				r->over++;
			}
			r->n++;
		}
	r->mean = r->n ? sum / (4.0 * r->n) : 0;
}
