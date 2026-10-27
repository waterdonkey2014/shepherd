/* Shepherd GL Test (vita/gltest) -- does vitaGL run GlBackend's shaders and GL usage correctly, and how fast?
 *
 * Every GL call goes through the shepgl table exactly as the C# backend will (function pointers from shepgl_proc, Gl.cs order),
 * and the drawing code mirrors engine/Shepherd.Engine/Render/GlBackend.cs (batching, 32-byte vertices, 4-deep VBO/IBO ring,
 * attribute pointers, blend factors, scissor, targets stored top-down, Stylizer pass, present copy). Each test renders into an
 * FBO, reads it back with glReadPixels and compares every pixel with a CPU reference (ref.c): that proves vitaGL's GLSL->Cg
 * translation computes the right thing, not just that it compiles. Synthetic data only: no game code or files.
 * Log: ux0:data/shepherd/gltest.txt (one open/append/close per line). The Mac build (plat_mac.c + shepgl_mac.c) runs the same
 * code on desktop GL to check this program itself. */
#include "shepgl.h"
#include "plat.h"
#include "ref.h"
#include "shaders_gen.h"
#include <limits.h>
#include <math.h>
#include <stdarg.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#define GLTEST_VERSION "shepherd-gltest-1"
#ifndef GLTEST_BUILD
#define GLTEST_BUILD "dev"
#endif

#ifndef GL_ALPHA
#define GL_ALPHA 0x1906
#endif
#ifndef GL_DITHER
#define GL_DITHER 0x0BD0
#endif
#ifndef GL_COMPRESSED_RGBA_S3TC_DXT1_EXT
#define GL_COMPRESSED_RGBA_S3TC_DXT1_EXT 0x83F1
#endif
#ifndef GL_COMPRESSED_RGBA_S3TC_DXT5_EXT
#define GL_COMPRESSED_RGBA_S3TC_DXT5_EXT 0x83F3
#endif
#ifndef GL_MAX_VARYING_VECTORS
#define GL_MAX_VARYING_VECTORS 0x8DFC
#endif
#ifndef GL_MAX_FRAGMENT_UNIFORM_VECTORS
#define GL_MAX_FRAGMENT_UNIFORM_VECTORS 0x8DFD
#endif
#ifndef GL_UNPACK_ALIGNMENT
#define GL_UNPACK_ALIGNMENT 0x0CF5
#endif
#ifndef GL_PACK_ALIGNMENT
#define GL_PACK_ALIGNMENT 0x0D05
#endif

#define SW 960
#define SH 544
#define MiB (1024.0 * 1024.0)

/* ================================================================== log + verdicts */
static void LOG(const char *fmt, ...) __attribute__((format(printf, 1, 2)));
static void LOG(const char *fmt, ...)
{
	char buf[1200];
	va_list ap;
	va_start(ap, fmt);
	vsnprintf(buf, sizeof buf, fmt, ap);
	va_end(ap);
	plat_log_line(buf);
}
static void LOG_LINES(const char *prefix, const char *text)
{
	char line[600];
	const char *p = text;
	while (p && *p) {
		const char *nl = strchr(p, '\n');
		int n = nl ? (int)(nl - p) : (int)strlen(p);
		for (int off = 0; off < n || (n == 0 && off == 0); off += 480) {   /* long lines (the extension string) in chunks */
			int k = n - off > 480 ? 480 : n - off;
			snprintf(line, sizeof line, "%s%.*s", prefix, k, p + off);
			plat_log_line(line);
			if (n == 0) break;
		}
		p = nl ? nl + 1 : NULL;
	}
}

enum { V_CORE, V_ALT, V_CAND, V_INFO };
enum { L_PASS, L_WARN, L_FAIL };
static int n_pass, n_warn, n_fail, n_alt_fail, n_alt_pass, n_cand_fail, n_cand_pass, n_info_diff, n_skip;
static const char *g_tag = "primary";
static int g_cls = V_CORE;   /* class of the suite currently running (primary = CORE, reruns = ALT) */

static void verdict(int cls, int lvl, const char *name, const char *detail)
{
	static const char *const core[3] = { "PASS", "WARN", "FAIL" }, *const alt[3] = { "ALT-PASS", "ALT-WARN", "ALT-FAIL" },
	                         *const cand[3] = { "CAND-PASS", "CAND-WARN", "CAND-FAIL" }, *const info[3] = { "INFO-OK", "INFO-DIFF", "INFO-DIFF" };
	const char *w = cls == V_CORE ? core[lvl] : cls == V_ALT ? alt[lvl] : cls == V_CAND ? cand[lvl] : info[lvl];
	LOG("%s [%s] %s: %s", w, g_tag, name, detail);
	if (cls == V_CORE) { if (lvl == L_PASS) n_pass++; else if (lvl == L_WARN) n_warn++; else n_fail++; }
	else if (cls == V_ALT) { if (lvl == L_FAIL) n_alt_fail++; else n_alt_pass++; }
	else if (cls == V_CAND) { if (lvl == L_FAIL) n_cand_fail++; else n_cand_pass++; }
	else if (lvl != L_PASS) n_info_diff++;
}
static void skip(const char *name, const char *why) { LOG("SKIP [%s] %s: %s", g_tag, name, why); n_skip++; }

/* compare a GPU readback with the CPU reference over the whole image; tol = pass bound, warn_tol = warn bound (max |diff| per channel).
 * g_bg: the cell's clear colour, to report how many reference pixels the draws actually changed (coverage) */
static int g_bg_valid;
static uint8_t g_bg[4];
static int check_img(int cls, const char *name, const img *gpu, const img *exp, int tol, int warn_tol)
{
	cmpres r;
	ref_compare(gpu, exp, 0, 0, gpu->w, gpu->h, tol, &r);
	int lvl = r.maxd <= tol ? L_PASS : r.maxd <= warn_tol ? L_WARN : L_FAIL;
	long drawn = -1;
	if (g_bg_valid) { drawn = 0; for (int i = 0; i < exp->w * exp->h; i++) drawn += memcmp(exp->p + (size_t)i * 4, g_bg, 4) != 0; }
	char d[900], cov[48] = "";
	if (drawn >= 0) snprintf(cov, sizeof cov, ", %ld px drawn", drawn);
	int o = snprintf(d, sizeof d, "%dx%d%s, max diff %d (tol %d, warn %d), mean %.3f, %ld px over tol", gpu->w, gpu->h, cov, r.maxd, tol, warn_tol, r.mean, r.over);
	static const int sx[3] = { 1, 2, 3 };
	for (int k = 0; k < 3 && o < (int)sizeof d - 80; k++) {
		int x = gpu->w * sx[k] / 4, y = gpu->h * sx[k] / 4;
		const uint8_t *g = img_px(gpu, x, y), *e = img_px(exp, x, y);
		o += snprintf(d + o, sizeof d - o, " | (%d,%d) gpu %d,%d,%d,%d exp %d,%d,%d,%d", x, y, g[0], g[1], g[2], g[3], e[0], e[1], e[2], e[3]);
	}
	for (int k = 0; k < r.nex && o < (int)sizeof d - 80; k++)
		o += snprintf(d + o, sizeof d - o, " | MISMATCH (%d,%d) gpu %d,%d,%d,%d exp %d,%d,%d,%d", r.ex_x[k], r.ex_y[k], r.ex_g[k][0], r.ex_g[k][1],
		              r.ex_g[k][2], r.ex_g[k][3], r.ex_e[k][0], r.ex_e[k][1], r.ex_e[k][2], r.ex_e[k][3]);
	verdict(cls, lvl, name, d);
	return lvl;
}

/* ================================================================== the GL table (what Gl.Load builds on the C# side) */
static struct {
#define SHEPGL_E(i, field, sym, how) __typeof__(&sym) field;
#include "shepgl_entries.h"
#undef SHEPGL_E
} G;

static const char *errs(void)   /* Gl.Errors(): drain glGetError */
{
	static char s[96];
	int o = 0;
	s[0] = 0;
	for (int i = 0; i < 8; i++) {
		GLenum e = G.GetError();
		if (e == GL_NO_ERROR) break;
		o += snprintf(s + o, sizeof s - o, " 0x%04X", (unsigned)e);
	}
	return s;
}

static int load_table(void)
{
	int missing = 0;
	LOG("BEGIN table");
#define SHEPGL_E(i, field, sym, how)                                                    \
	G.field = (__typeof__(&sym))shepgl_proc(#sym);                                      \
	if (!G.field) { LOG("FAIL table: %s (Gl.%s) unresolved", #sym, #field); missing++; } \
	if (shepgl_fn(i) != (void *)G.field || strcmp(shepgl_name(i), #sym)) { LOG("FAIL table: index %d is %s, expected %s", i, shepgl_name(i), #sym); missing++; }
#include "shepgl_entries.h"
#undef SHEPGL_E
	char w[400];
	int o = 0, nw = 0;
	w[0] = 0;
	for (int i = 0; i < shepgl_count(); i++)
		if (shepgl_is_wrapped(i)) { o += snprintf(w + o, sizeof w - o, "%s%s", nw ? ", " : "", shepgl_name(i)); nw++; }
	float (*probe)(int32_t, float, float, float, float, uint8_t) = (float (*)(int32_t, float, float, float, float, uint8_t))shepgl_proc("shepgl_abi_probe");
	float pv = probe ? probe(1, 0.5f, 0.25f, 2.0f, 4.0f, 1) : -1.0f;   /* 1 + 1 + 0.75 + 10 + 28 + 11 */
	char d[600];
	snprintf(d, sizeof d, "%d/%d entry points resolved by name through shepgl_proc (Gl.Load's getProc), Gl.cs order; %d are shepgl fix-up wrappers (%s); "
	         "\"glGenFramebuffersEXT\" -> %s; abi probe = %.2f (expect 51.75)",
	         shepgl_count() - missing, SHEPGL_COUNT, nw, nw ? w : "none", shepgl_proc("glGenFramebuffersEXT") ? "non-NULL" : "NULL", pv);
	verdict(V_CORE, missing == 0 && shepgl_count() == SHEPGL_COUNT && pv == 51.75f ? L_PASS : L_FAIL, "table", d);
	return missing == 0;
}

/* ================================================================== programs */
typedef struct prog {
	char name[48];
	GLuint id;
	int ok;
	GLint u_xform, u_screen, u_pat, u_lut1, u_lut2;
	int tw, th;
	double vs_ms, fs_ms, link_ms;
	int gxp_vs, gxp_fs;
} prog;
typedef struct { prog sprite, sdf, copy, sty1, sty2; const char *tag; int ok; } progset;

/* the dialect step: on the Vita the text is handed over unchanged; the Mac legacy context takes #version 120 (no precision
 * statement), exactly GlBackend's glsl120 prelude in front of the same body. precision: NULL or "mediump" for the experiment. */
static char *dialect(const char *src, const char *precision)
{
	const char *body = src;
	if (!strncmp(body, "#version 100\n", 13)) body += 13;
	const char *prec_line = body;
	if (!strncmp(body, "precision ", 10)) { const char *nl = strchr(body, '\n'); body = nl ? nl + 1 : body; }
	size_t n = strlen(src) + 64;
	char *out = (char *)malloc(n);
	if (!strcmp(plat_dialect(), "glsl120")) snprintf(out, n, "#version 120\n%s", body);
	else if (precision) snprintf(out, n, "#version 100\nprecision %s float;\n%s", precision, body);
	else { (void)prec_line; snprintf(out, n, "%s", src); }
	return out;
}

static GLuint compile_one(GLenum kind, const char *src, const char *what, double *ms, int *ok_out)
{
	GLuint s = G.CreateShader(kind);
	const GLchar *p = src;
	GLint len = (GLint)strlen(src);
	G.ShaderSource(s, 1, &p, &len);
	uint64_t t0 = plat_us();
	G.CompileShader(s);
	*ms = (plat_us() - t0) / 1000.0;
	GLint ok = 0, n = 0;
	G.GetShaderiv(s, GL_COMPILE_STATUS, &ok);
	G.GetShaderiv(s, GL_INFO_LOG_LENGTH, &n);
	*ok_out = ok != 0;
	if (n > 1) {
		char *buf = (char *)calloc(1, (size_t)n + 1);
		G.GetShaderInfoLog(s, n, NULL, buf);
		char pre[96];
		snprintf(pre, sizeof pre, "  %s log: ", what);
		LOG_LINES(pre, buf);
		free(buf);
	}
	return s;
}

static int build_program(prog *p, const char *name, const char *vs_src, const char *fs_src, int tintblack, const char *precision)
{
	memset(p, 0, sizeof *p);
	snprintf(p->name, sizeof p->name, "%s", name);
	p->tw = p->th = -1;
	char *vs = dialect(vs_src, NULL), *fs = dialect(fs_src, precision);
	char what[96];
	errs();
	LOG("STEP [%s] %s: glCompileShader(vertex)", g_tag, name);
	int vok = 0, fok = 0;
	snprintf(what, sizeof what, "%s vs", name);
	GLuint v = compile_one(GL_VERTEX_SHADER, vs, what, &p->vs_ms, &vok);
	LOG("STEP [%s] %s: glCompileShader(fragment)", g_tag, name);
	snprintf(what, sizeof what, "%s fs", name);
	GLuint f = compile_one(GL_FRAGMENT_SHADER, fs, what, &p->fs_ms, &fok);
	GLint vok_after = 0;
	G.GetShaderiv(v, GL_COMPILE_STATUS, &vok_after);
	char d[600];
	if (!vok || !fok) {
		snprintf(d, sizeof d, "compile failed: vertex %s (%.1f ms; status re-read after the fragment compile: %d), fragment %s (%.1f ms) -- GlBackend would throw here",
		         vok ? "ok" : "FAILED", p->vs_ms, (int)vok_after, fok ? "ok" : "FAILED", p->fs_ms);
		verdict(g_cls, L_FAIL, name, d);
		G.DeleteShader(v); G.DeleteShader(f);
		free(vs); free(fs);
		return 0;
	}
	p->id = G.CreateProgram();
	G.AttachShader(p->id, v);
	G.AttachShader(p->id, f);
	G.BindAttribLocation(p->id, 0, "a_pos");
	G.BindAttribLocation(p->id, 1, "a_color");
	G.BindAttribLocation(p->id, 2, "a_uv");
	if (tintblack) G.BindAttribLocation(p->id, 3, "a_color2");
	LOG("STEP [%s] %s: glLinkProgram", g_tag, name);
	uint64_t t0 = plat_us();
	G.LinkProgram(p->id);
	p->link_ms = (plat_us() - t0) / 1000.0;
	GLint lok = 0, n = 0;
	G.GetProgramiv(p->id, GL_LINK_STATUS, &lok);
	G.GetProgramiv(p->id, GL_INFO_LOG_LENGTH, &n);
	if (n > 1) {
		char *buf = (char *)calloc(1, (size_t)n + 1);
		G.GetProgramInfoLog(p->id, n, NULL, buf);
		char pre[96];
		snprintf(pre, sizeof pre, "  %s link log: ", name);
		LOG_LINES(pre, buf);
		free(buf);
	}
	p->gxp_vs = shepgl_shader_binary_size(v);   /* after the link: POSTPONED mode compiles there */
	p->gxp_fs = shepgl_shader_binary_size(f);
	G.DeleteShader(v);
	G.DeleteShader(f);
	free(vs); free(fs);
	if (!lok) {
		snprintf(d, sizeof d, "link FAILED (%.1f ms; compile %.1f + %.1f ms)%s", p->link_ms, p->vs_ms, p->fs_ms, n > 1 ? " -- info log above" : " -- empty info log");
		verdict(g_cls, L_FAIL, name, d);
		return 0;
	}
	p->u_xform = G.GetUniformLocation(p->id, "u_xform");
	p->u_screen = G.GetUniformLocation(p->id, "u_screen");
	p->u_pat = G.GetUniformLocation(p->id, "u_pat_info");
	p->u_lut1 = G.GetUniformLocation(p->id, "u_lut1_info");
	p->u_lut2 = G.GetUniformLocation(p->id, "u_lut2_info");
	G.UseProgram(p->id);
	static const char *const su[5] = { "u_tex", "u_src", "u_layers", "u_lut1", "u_lut2" };
	static const int unit[5] = { 0, 0, 1, 2, 3 };
	int samplers = 0;
	for (int k = 0; k < 5; k++) { GLint l = G.GetUniformLocation(p->id, su[k]); if (l >= 0) { G.Uniform1i(l, unit[k]); samplers++; } }
	G.UseProgram(0);
	const char *e = errs();
	p->ok = 1;
	snprintf(d, sizeof d, "compiled + linked: vs %.1f ms, fs %.1f ms, link %.1f ms; serialized GXP vs %d B, fs %d B; uniforms xform %d screen %d pat %d lut1 %d lut2 %d, %d samplers; GL errors:%s",
	         p->vs_ms, p->fs_ms, p->link_ms, p->gxp_vs, p->gxp_fs, (int)p->u_xform, (int)p->u_screen, (int)p->u_pat, (int)p->u_lut1, (int)p->u_lut2,
	         samplers, *e ? e : " none");
	verdict(g_cls, p->u_xform >= 0 ? L_PASS : L_FAIL, name, d);
	return 1;
}

static int build_progset(progset *ps, const char *tag)
{
	g_tag = tag;
	ps->tag = tag;
	uint64_t t0 = plat_us();
	int ok = 1;
	prog *dst[5] = { &ps->sprite, &ps->sdf, &ps->copy, &ps->sty1, &ps->sty2 };   /* the stylizer pair only with the owner-local shader */
	for (int i = 0; i < GLTEST_NPROGRAMS; i++) ok &= build_program(dst[i], k_backend_programs[i].name, k_backend_programs[i].vs, k_backend_programs[i].fs, 0, NULL);
	LOG("INFO [%s] %d programs built in %.1f ms (GlBackend's tracked + owner-local programs of these kinds%s)", tag, GLTEST_NPROGRAMS, (plat_us() - t0) / 1000.0,
	    GLTEST_HAVE_STYLIZER ? "" : "; no owner-local stylizer here: its two programs are not built");
	ps->ok = ok;
	return ok;
}

/* ================================================================== textures, targets (GlBackend.Upload / Target) */
typedef struct tex { GLuint id, fbo; int w, h, linear, wrap, npot; } tex;
static tex *cur_tex, *target, *white;
static void bind_tex0(tex *t) { if (cur_tex == t) return; G.BindTexture(GL_TEXTURE_2D, t->id); cur_tex = t; }
static int pot(int v) { return v > 0 && (v & (v - 1)) == 0; }
static GLuint display_fbo(void) { return shepgl_display_fbo(); }

static tex *tex_new(int w, int h, int linear)
{
	tex *t = (tex *)calloc(1, sizeof *t);
	G.GenTextures(1, &t->id);
	t->w = w; t->h = h; t->linear = linear; t->npot = !pot(w) || !pot(h); t->wrap = 0;
	bind_tex0(t);
	G.TexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MIN_FILTER, linear ? GL_LINEAR : GL_NEAREST);
	G.TexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MAG_FILTER, linear ? GL_LINEAR : GL_NEAREST);
	G.TexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_S, GL_CLAMP_TO_EDGE);
	G.TexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_T, GL_CLAMP_TO_EDGE);
	return t;
}
static tex *up_rgba(int w, int h, const uint8_t *px, int linear, const char *what)
{
	tex *t = tex_new(w, h, linear);
	G.TexImage2D(GL_TEXTURE_2D, 0, GL_RGBA, w, h, 0, GL_RGBA, GL_UNSIGNED_BYTE, px);
	const char *e = errs();
	if (*e) LOG("WARN upload %s RGBA8 %dx%d: GL errors%s", what, w, h, e);
	return t;
}
static tex *up_alpha(int w, int h, const uint8_t *a8, int linear, const char *what)
{
	tex *t = tex_new(w, h, linear);
	G.PixelStorei(GL_UNPACK_ALIGNMENT, 1);     /* GlBackend.Upload for bpp 1 */
	G.TexImage2D(GL_TEXTURE_2D, 0, GL_ALPHA, w, h, 0, GL_ALPHA, GL_UNSIGNED_BYTE, a8);
	G.PixelStorei(GL_UNPACK_ALIGNMENT, 4);
	const char *e = errs();
	if (*e) LOG("WARN upload %s ALPHA8 %dx%d: GL errors%s", what, w, h, e);
	return t;
}
static tex *up_dxt(int w, int h, int dxt5, const uint8_t *blocks, int linear, const char *what)
{
	tex *t = tex_new(w, h, linear);
	int size = (w + 3) / 4 * ((h + 3) / 4) * (dxt5 ? 16 : 8);
	G.CompressedTexImage2D(GL_TEXTURE_2D, 0, dxt5 ? GL_COMPRESSED_RGBA_S3TC_DXT5_EXT : GL_COMPRESSED_RGBA_S3TC_DXT1_EXT, w, h, 0, size, blocks);
	const char *e = errs();
	if (*e) LOG("WARN upload %s DXT%d %dx%d: GL errors%s (GlBackend would fall back to RGBA)", what, dxt5 ? 5 : 1, w, h, e);
	return t;
}
static tex *target_new(int w, int h, int linear, const char *what)
{
	tex *t = up_rgba(w, h, NULL, linear, what);
	G.GenFramebuffers(1, &t->fbo);
	G.BindFramebuffer(GL_FRAMEBUFFER, t->fbo);
	G.FramebufferTexture2D(GL_FRAMEBUFFER, GL_COLOR_ATTACHMENT0, GL_TEXTURE_2D, t->id, 0);
	GLenum st = G.CheckFramebufferStatus(GL_FRAMEBUFFER);
	G.BindFramebuffer(GL_FRAMEBUFFER, target ? target->fbo : display_fbo());
	const char *e = errs();
	if (st != GL_FRAMEBUFFER_COMPLETE || *e) {
		char d[200];
		snprintf(d, sizeof d, "FBO %s %dx%d status 0x%04X (complete = 0x%04X), GL errors:%s", what, w, h, (unsigned)st, (unsigned)GL_FRAMEBUFFER_COMPLETE, *e ? e : " none");
		verdict(V_CORE, L_FAIL, "fbo", d);
		t->fbo = 0;
	}
	return t;
}

/* ================================================================== the batcher (GlBackend.Draw / Flush) */
#define STRIDE 32
typedef struct { float x, y, r, g, b, a, u, v; } vtx;   /* Sdl.Vertex: pos 2f @0, colour 4f @8, uv 2f @24 */
_Static_assert(sizeof(vtx) == STRIDE, "32-byte vertex");
typedef struct { tex *t; int blend; prog *p; int wrap, clip, cx, cy, cw, ch, base, i0, ni; } batch;
static vtx *V; static uint16_t *I; static batch *Bt;
static int nv, ni, nb;
#define MAXV 16384
#define MAXI 49152
#define MAXB 1024
#define RING 4
static GLuint vbo[RING], ibo[RING], quad_px, quad_ndc, quad_idx;
static int ring;
static prog *cur_prog;
static int cur_blend = -1, scissor_on, sx_ = -1, sy_ = -1, sw_ = -1, sh_ = -1, clip_dirty = 1, attr_base = INT_MIN, tw_, th_;
static struct { long draws, flushes, bufbytes, quads; } stt;

static void set_blend(int b)
{
	if (b == cur_blend) return;
	if (b == BL_NONE) G.Disable(GL_BLEND);
	else {
		if (cur_blend == BL_NONE || cur_blend == -1) G.Enable(GL_BLEND);
		switch (b) {   /* GlBackend.SetBlend: SDL3's definitions, colour and alpha factors separately */
		case BL_PREMUL: G.BlendFuncSeparate(GL_ONE, GL_ONE_MINUS_SRC_ALPHA, GL_ONE, GL_ONE_MINUS_SRC_ALPHA); break;
		case BL_ADD: G.BlendFuncSeparate(GL_SRC_ALPHA, GL_ONE, GL_ZERO, GL_ONE); break;
		case BL_ADD_PREMUL: G.BlendFuncSeparate(GL_ONE, GL_ONE, GL_ZERO, GL_ONE); break;
		case BL_MOD: G.BlendFuncSeparate(GL_ZERO, GL_SRC_COLOR, GL_ZERO, GL_ONE); break;
		case BL_MUL: G.BlendFuncSeparate(GL_DST_COLOR, GL_ONE_MINUS_SRC_ALPHA, GL_ZERO, GL_ONE); break;
		default: G.BlendFuncSeparate(GL_SRC_ALPHA, GL_ONE_MINUS_SRC_ALPHA, GL_ONE, GL_ONE_MINUS_SRC_ALPHA); break;
		}
	}
	cur_blend = b;
}
static void set_clip(int on, int x, int y, int w, int h)
{
	if (!on) { if (scissor_on || clip_dirty) { G.Disable(GL_SCISSOR_TEST); scissor_on = 0; } clip_dirty = 0; return; }
	if (w < 0) w = 0;
	if (h < 0) h = 0;
	if (!scissor_on || clip_dirty) { G.Enable(GL_SCISSOR_TEST); scissor_on = 1; }
	if (clip_dirty || x != sx_ || y != sy_ || w != sw_ || h != sh_) { G.Scissor(x, y, w, h); sx_ = x; sy_ = y; sw_ = w; sh_ = h; }   /* targets are top-down: y as given */
	clip_dirty = 0;
}
static void use_prog(prog *p)
{
	if (cur_prog != p) { G.UseProgram(p->id); cur_prog = p; }
	if (p->tw != tw_ || p->th != th_) { G.Uniform4f(p->u_xform, 2.0f / tw_, 2.0f / th_, -1.0f, -1.0f); p->tw = tw_; p->th = th_; }
}
static void attribs(int base)
{
	char *o = (char *)(intptr_t)(base * STRIDE);
	G.EnableVertexAttribArray(0); G.EnableVertexAttribArray(1); G.EnableVertexAttribArray(2);
	G.VertexAttribPointer(0, 2, GL_FLOAT, 0, STRIDE, o);
	G.VertexAttribPointer(1, 4, GL_FLOAT, 0, STRIDE, o + 8);
	G.VertexAttribPointer(2, 2, GL_FLOAT, 0, STRIDE, o + 24);
	attr_base = base;
}
static void flush(void)
{
	if (nb == 0) { nv = ni = 0; return; }
	stt.flushes++;
	stt.bufbytes += (long)nv * STRIDE + (long)ni * 2;
	GLuint vb = vbo[ring], ib = ibo[ring];
	ring = (ring + 1) % RING;
	G.BindBuffer(GL_ARRAY_BUFFER, vb);
	G.BufferData(GL_ARRAY_BUFFER, nv * STRIDE, V, GL_STREAM_DRAW);
	G.BindBuffer(GL_ELEMENT_ARRAY_BUFFER, ib);
	G.BufferData(GL_ELEMENT_ARRAY_BUFFER, ni * 2, I, GL_STREAM_DRAW);
	attr_base = INT_MIN;
	for (int k = 0; k < nb; k++) {
		batch *b = &Bt[k];
		use_prog(b->p);
		bind_tex0(b->t);
		if (b->t->wrap != b->wrap) {
			int m = b->wrap ? GL_REPEAT : GL_CLAMP_TO_EDGE;
			G.TexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_S, m);
			G.TexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_T, m);
			b->t->wrap = b->wrap;
		}
		set_blend(b->blend);
		set_clip(b->clip, b->cx, b->cy, b->cw, b->ch);
		if (b->base != attr_base) attribs(b->base);
		G.DrawElements(GL_TRIANGLES, b->ni, GL_UNSIGNED_SHORT, (void *)(intptr_t)(b->i0 * 2));
	}
	stt.draws += nb;
	nb = 0; nv = ni = 0;
}
static void bind_target(tex *t)
{
	if (target == t) return;
	flush();
	G.BindFramebuffer(GL_FRAMEBUFFER, t->fbo);
	G.Viewport(0, 0, t->w, t->h);
	tw_ = t->w; th_ = t->h; target = t; clip_dirty = 1;
}
static void begin(tex *t, float r, float g, float b, float a)
{
	flush();
	bind_target(t);
	if (scissor_on) { G.Disable(GL_SCISSOR_TEST); scissor_on = 0; }
	G.ClearColor(r, g, b, a);
	G.Clear(GL_COLOR_BUFFER_BIT);
	clip_dirty = 1;
}

typedef struct { int on, x, y, w, h; } clipr;
static const clipr NOCLIP = { 0, 0, 0, 0, 0 };
/* one quad = GlBackend.Draw with 4 vertices (TL, TR, BR, BL) and indices 0,1,2 0,2,3; c = colours TL, TR, BR, BL */
static void quad(tex *t, int blend, prog *p, float x0, float y0, float x1, float y1, float u0, float v0, float u1, float v1, const float c[4][4], clipr cl)
{
	if (nv + 4 > MAXV || ni + 6 > MAXI || nb >= MAXB) flush();
	int wrap = u0 < 0 || v0 < 0 || u0 > 1 || v0 > 1 || u1 < 0 || v1 < 0 || u1 > 1 || v1 > 1;   /* SDL_TEXTURE_ADDRESS_AUTO */
	int merged = 0;
	if (nb > 0) {
		batch *l = &Bt[nb - 1];
		if (l->t == t && l->blend == blend && l->p == p && l->wrap == wrap && l->clip == cl.on &&
		    (!cl.on || (l->cx == cl.x && l->cy == cl.y && l->cw == cl.w && l->ch == cl.h))) { l->ni += 6; merged = 1; }
	}
	if (!merged) { Bt[nb] = (batch){ t, blend, p, wrap, cl.on, cl.x, cl.y, cl.w, cl.h, 0, ni, 6 }; nb++; }
	float xs[4] = { x0, x1, x1, x0 }, ys[4] = { y0, y0, y1, y1 }, us[4] = { u0, u1, u1, u0 }, vs[4] = { v0, v0, v1, v1 };
	for (int k = 0; k < 4; k++) V[nv + k] = (vtx){ xs[k], ys[k], c[k][0], c[k][1], c[k][2], c[k][3], us[k], vs[k] };
	static const int q[6] = { 0, 1, 2, 0, 2, 3 };
	for (int k = 0; k < 6; k++) I[ni + k] = (uint16_t)(nv + q[k]);
	nv += 4; ni += 6; stt.quads++;
}
static void solid4(float c[4][4], float r, float g, float b, float a) { for (int k = 0; k < 4; k++) { c[k][0] = r; c[k][1] = g; c[k][2] = b; c[k][3] = a; } }

static void build_quads(void)   /* GlBackend.BuildQuads, for the 960x544 main targets */
{
	vtx px[4], ndc[4];
	float qx[4] = { 0, SW, SW, 0 }, qy[4] = { 0, 0, SH, SH };
	float nx[4] = { -1, 1, 1, -1 }, ny[4] = { -1, -1, 1, 1 }, tu[4] = { 0, 1, 1, 0 }, tv[4] = { 1, 1, 0, 0 };
	for (int i = 0; i < 4; i++) {
		px[i] = (vtx){ qx[i], qy[i], 1, 1, 1, 1, qx[i], qy[i] };
		ndc[i] = (vtx){ nx[i], ny[i], 1, 1, 1, 1, tu[i], tv[i] };
	}
	uint16_t q[6] = { 0, 1, 2, 0, 2, 3 };
	G.GenBuffers(1, &quad_px); G.GenBuffers(1, &quad_ndc); G.GenBuffers(1, &quad_idx);
	G.BindBuffer(GL_ARRAY_BUFFER, quad_px); G.BufferData(GL_ARRAY_BUFFER, 4 * STRIDE, px, GL_STATIC_DRAW);
	G.BindBuffer(GL_ARRAY_BUFFER, quad_ndc); G.BufferData(GL_ARRAY_BUFFER, 4 * STRIDE, ndc, GL_STATIC_DRAW);
	G.BindBuffer(GL_ELEMENT_ARRAY_BUFFER, quad_idx); G.BufferData(GL_ELEMENT_ARRAY_BUFFER, 12, q, GL_STATIC_DRAW);
}
static void draw_fullquad(GLuint vb)   /* GlBackend.DrawQuad */
{
	G.BindBuffer(GL_ARRAY_BUFFER, vb);
	G.BindBuffer(GL_ELEMENT_ARRAY_BUFFER, quad_idx);
	attribs(0);
	attr_base = INT_MIN;
	G.DrawElements(GL_TRIANGLES, 6, GL_UNSIGNED_SHORT, NULL);
	stt.draws++;
}

#if GLTEST_HAVE_STYLIZER
typedef struct { tex *lut1, *lut2, *layers; int block; float intensity, lerp; } sty_args;
/* GlBackend.RunStylizer: src (world) -> dst, one full-screen pass */
static void stylize(progset *ps, tex *src, tex *dst, const sty_args *a)
{
	flush();
	bind_target(dst);
	set_blend(BL_NONE);
	set_clip(0, 0, 0, 0, 0);
	clip_dirty = 1;
	prog *p = a->lut2 ? &ps->sty2 : &ps->sty1;
	use_prog(p);
	G.Uniform4f(p->u_screen, SW, SH, (float)(a->block < 1 ? 1 : a->block), a->intensity);
	G.Uniform4f(p->u_pat, (float)a->layers->w, (float)a->layers->h, 0, 0);
	G.Uniform4f(p->u_lut1, (float)a->lut1->w, (float)a->lut1->h, 0, 0);
	if (a->lut2) G.Uniform4f(p->u_lut2, (float)a->lut2->w, (float)a->lut2->h, 0, a->lerp);
	G.ActiveTexture(GL_TEXTURE0 + 3); G.BindTexture(GL_TEXTURE_2D, (a->lut2 ? a->lut2 : a->lut1)->id);
	G.ActiveTexture(GL_TEXTURE0 + 2); G.BindTexture(GL_TEXTURE_2D, a->lut1->id);
	G.ActiveTexture(GL_TEXTURE0 + 1); G.BindTexture(GL_TEXTURE_2D, a->layers->id);
	G.ActiveTexture(GL_TEXTURE0); cur_tex = NULL; bind_tex0(src);
	draw_fullquad(quad_px);
	bind_tex0(white);
}
#endif
/* GlBackend.PresentCopy into the display (Vita: framebuffer 0; Mac: the stand-in FBO), letterbox = 1:1 at 960x544 */
static void present_to(progset *ps, tex *src, GLuint fb)
{
	flush();
	G.BindFramebuffer(GL_FRAMEBUFFER, fb);
	target = NULL;
	set_clip(0, 0, 0, 0, 0);
	clip_dirty = 1;
	G.Viewport(0, 0, SW, SH);
	G.ClearColor(0, 0, 0, 1);
	G.Clear(GL_COLOR_BUFFER_BIT);
	set_blend(BL_NONE);
	tw_ = th_ = -1;
	if (cur_prog != &ps->copy) { G.UseProgram(ps->copy.id); cur_prog = &ps->copy; }
	G.Uniform4f(ps->copy.u_xform, 1, 1, 0, 0);
	ps->copy.tw = ps->copy.th = -2;
	bind_tex0(src);
	draw_fullquad(quad_ndc);
	bind_tex0(white);
}
static void present(progset *ps, tex *src) { present_to(ps, src, display_fbo()); }

static int g_rb_flip;   /* set by the orientation test if glReadPixels returns rows in the opposite order to GL */
static void flip_rows(img *m)
{
	size_t row = (size_t)m->w * 4;
	uint8_t *tmp = (uint8_t *)malloc(row);
	for (int y = 0; y < m->h / 2; y++) {
		memcpy(tmp, img_px(m, 0, y), row);
		memcpy(img_px(m, 0, y), img_px(m, 0, m->h - 1 - y), row);
		memcpy(img_px(m, 0, m->h - 1 - y), tmp, row);
	}
	free(tmp);
}
static void readback_fb(GLuint fb, int w, int h, img *out, int compensate)
{
	flush();
	G.BindFramebuffer(GL_FRAMEBUFFER, fb);
	G.ReadPixels(0, 0, w, h, GL_RGBA, GL_UNSIGNED_BYTE, out->p);
	G.BindFramebuffer(GL_FRAMEBUFFER, target ? target->fbo : display_fbo());
	const char *e = errs();
	if (*e) LOG("WARN glReadPixels %dx%d: GL errors%s", w, h, e);
	if (compensate && g_rb_flip) flip_rows(out);
}
static img readback(tex *t) { img m = img_new(t->w, t->h); readback_fb(t->fbo, t->w, t->h, &m, 1); return m; }

/* ================================================================== synthetic data */
static void synth_rgba(uint8_t *p, int w, int h, uint32_t seed, int varied_alpha)
{
	uint32_t s = seed | 1;
	for (int i = 0; i < w * h; i++) {
		uint32_t r = xs32(&s);
		p[i * 4] = (uint8_t)r; p[i * 4 + 1] = (uint8_t)(r >> 8); p[i * 4 + 2] = (uint8_t)(r >> 16);
		p[i * 4 + 3] = varied_alpha ? (uint8_t)(r >> 24) : 255;
	}
}
static void premultiply(uint8_t *p, int n)
{
	for (int i = 0; i < n; i++) for (int k = 0; k < 3; k++) p[i * 4 + k] = (uint8_t)((p[i * 4 + k] * p[i * 4 + 3] + 127) / 255);
}
static uint16_t rnd565(uint32_t *s) { return (uint16_t)xs32(s); }
static void synth_bc1(uint8_t *b, int nblk, uint32_t seed, int punch)
{
	uint32_t s = seed | 1;
	for (int i = 0; i < nblk; i++, b += 8) {
		uint16_t c0 = rnd565(&s), c1 = rnd565(&s);
		if (!punch) { if (c0 == c1) c1 = (uint16_t)(c0 == 0 ? 1 : c0 - 1); if (c0 < c1) { uint16_t t = c0; c0 = c1; c1 = t; } }
		else if (c0 > c1) { uint16_t t = c0; c0 = c1; c1 = t; }
		uint32_t idx = xs32(&s);
		b[0] = (uint8_t)c0; b[1] = (uint8_t)(c0 >> 8); b[2] = (uint8_t)c1; b[3] = (uint8_t)(c1 >> 8);
		b[4] = (uint8_t)idx; b[5] = (uint8_t)(idx >> 8); b[6] = (uint8_t)(idx >> 16); b[7] = (uint8_t)(idx >> 24);
	}
}
static void synth_bc3(uint8_t *b, int nblk, uint32_t seed, int colour_le)
{
	uint32_t s = seed | 1;
	for (int i = 0; i < nblk; i++, b += 16) {
		uint32_t r = xs32(&s);
		uint8_t a0 = (uint8_t)r, a1 = (uint8_t)(r >> 8);
		if ((i & 1) == 0) { if (a0 == a1) a1 = (uint8_t)(a0 ^ 0x40); if (a0 < a1) { uint8_t t = a0; a0 = a1; a1 = t; } }   /* 8-value mode */
		else if (a0 > a1) { uint8_t t = a0; a0 = a1; a1 = t; }                                                          /* 6-value mode */
		b[0] = a0; b[1] = a1;
		uint32_t i0 = xs32(&s), i1 = xs32(&s);
		b[2] = (uint8_t)i0; b[3] = (uint8_t)(i0 >> 8); b[4] = (uint8_t)(i0 >> 16); b[5] = (uint8_t)(i0 >> 24); b[6] = (uint8_t)i1; b[7] = (uint8_t)(i1 >> 8);
		synth_bc1(b + 8, 1, xs32(&s), colour_le);
	}
}
/* an SDF-like alpha atlas: a few discs and bars, distance mapped like TMP's (0.5 = edge, 12 px spread) */
static void synth_sdf(uint8_t *a, int w, int h, int cell)
{
	for (int y = 0; y < h; y++)
		for (int x = 0; x < w; x++) {
			int cx = (x / cell) * cell + cell / 2, cy = (y / cell) * cell + cell / 2, kind = ((x / cell) * 7 + (y / cell) * 3) % 3;
			double dx = x + 0.5 - cx, dy = y + 0.5 - cy, d;
			if (kind == 0) d = sqrt(dx * dx + dy * dy) - cell * 0.30;
			else if (kind == 1) d = fmax(fabs(dx) - cell * 0.12, fabs(dy) - cell * 0.34);
			else d = fabs(sqrt(dx * dx + dy * dy) - cell * 0.25) - cell * 0.08;
			double v = 0.5 - d / 24.0;
			a[(size_t)y * w + x] = ref_u8(v);
		}
}
static void synth_pattern(uint8_t *p, int w, int h)
{
	for (int i = 0; i < w * h; i++) { uint8_t v = (uint8_t)((i * 37 + (i >> 6) * 11) & 255); p[i * 4] = p[i * 4 + 1] = p[i * 4 + 2] = v; p[i * 4 + 3] = 255; }
}

/* ================================================================== shared fixtures */
static tex *cell64, *cell128, *cell2, *cell3, *cellx, *fA, *fB, *fC, *fD;
static img g_src;                         /* 960x544 Stylizer source (synthetic) */
static tex *t_src, *t_lut1, *t_lut2, *t_lm1, *t_lm2;
static img g_lut1, g_lut2, g_lm1, g_lm2, g_pat;

static void make_fixtures(void)
{
	cell64 = target_new(64, 64, 0, "cell64");
	cell128 = target_new(128, 128, 0, "cell128");
	cell2 = target_new(128, 128, 0, "cell128b");
	cell3 = target_new(128, 128, 0, "cell128c");
	cellx = target_new(100, 60, 0, "cell100x60");
	fA = target_new(SW, SH, 0, "main0");      /* GlBackend's two main targets are NEAREST */
	fB = target_new(SW, SH, 0, "main1");
	fC = target_new(SW, SH, 0, "scratch");
	fD = target_new(SW, SH, 0, "present-shape");
	g_src = img_new(SW, SH);
	for (int y = 0; y < SH; y++)
		for (int x = 0; x < SW; x++) {
			uint8_t *p = img_px(&g_src, x, y);
			p[0] = (uint8_t)(x + 3 * y); p[1] = (uint8_t)((x * 7) ^ (y * 13)); p[2] = (uint8_t)(x * 11 + y * 5 + ((x * y) >> 6)); p[3] = 255;
		}
	t_src = up_rgba(SW, SH, g_src.p, 0, "stylizer-source");
	g_lut1 = img_new(256, 32); synth_rgba(g_lut1.p, 256, 32, 0x1u, 0); for (int i = 0; i < 256 * 32; i++) g_lut1.p[i * 4 + 3] = 255;
	g_lut2 = img_new(256, 48); synth_rgba(g_lut2.p, 256, 48, 0x2u, 0); for (int i = 0; i < 256 * 48; i++) g_lut2.p[i * 4 + 3] = 255;
	g_pat = img_new(64, 64); synth_pattern(g_pat.p, 64, 64);
#if GLTEST_HAVE_STYLIZER
	ref_layer_map(g_pat.p, 64, 64, 2, 0, &g_lm1);   /* one palette, 2 layers (LUT 256x32) */
	ref_layer_map(g_pat.p, 64, 64, 2, 3, &g_lm2);   /* two palettes: 2 and 3 layers (LUT 256x48) */
#else
	g_lm1 = img_new(64, 64); g_lm2 = img_new(64, 64);   /* (no owner-local layer map: unused, the Stylizer checks are skipped) */
#endif
	t_lut1 = up_rgba(256, 32, g_lut1.p, 0, "lut1");
	t_lut2 = up_rgba(256, 48, g_lut2.p, 0, "lut2");
	t_lm1 = up_rgba(64, 64, g_lm1.p, 0, "layer-map-1");
	t_lm2 = up_rgba(64, 64, g_lm2.p, 0, "layer-map-2");
}

/* CPU model of a target + the GPU draw, from one description */
typedef struct { const rtex *rt; tex *t; } texpair;
static void dq(img *model, texpair tp, int blend, prog *p, int shader, int x0, int y0, int x1, int y1, double u0, double v0, double u1, double v1,
               const float c[4][4], clipr cl)
{
	quad(tp.t, blend, p, (float)x0, (float)y0, (float)x1, (float)y1, (float)u0, (float)v0, (float)u1, (float)v1, c, cl);
	if (!model) return;
	rquad q;
	memset(&q, 0, sizeof q);
	q.x0 = x0; q.y0 = y0; q.x1 = x1; q.y1 = y1; q.u0 = u0; q.v0 = v0; q.u1 = u1; q.v1 = v1;
	for (int k = 0; k < 4; k++) { q.tl[k] = (float)c[0][k]; q.tr[k] = (float)c[1][k]; q.bl[k] = (float)c[3][k]; }   /* float like the vertex */
	q.tex = tp.rt;
	q.linear = tp.t->linear;
	q.repeat = u0 < 0 || v0 < 0 || u0 > 1 || v0 > 1 || u1 < 0 || v1 < 0 || u1 > 1 || v1 > 1;
	q.shader = shader;
	q.blend = blend;
	q.clip = cl.on; q.cx = cl.x; q.cy = cl.y; q.cw = cl.w; q.ch = cl.h;
	ref_quad(model, &q);
}
static void model_clear(img *m, float r, float g, float b, float a) { uint8_t c[4] = { ref_u8(r), ref_u8(g), ref_u8(b), ref_u8(a) }; img_fill(m, c); }
static texpair WHITE;
static rtex rt_white_1x1;

/* ================================================================== tests */
static int rows_are(const img *m, int y0, int y1, const uint8_t c[4], int tol)
{
	for (int y = y0; y < y1; y++)
		for (int x = 0; x < m->w; x++) {
			const uint8_t *p = img_px(m, x, y);
			for (int k = 0; k < 4; k++) if (abs((int)p[k] - (int)c[k]) > tol) return 0;
		}
	return 1;
}

static void test_orientation(progset *ps)
{
	LOG("BEGIN orientation (GlBackend stores every target top-down and relies on GL's window-y convention)");
	float red[4][4], wht[4][4];
	solid4(red, 1, 0, 0, 1);
	solid4(wht, 1, 1, 1, 1);
	static const uint8_t RED[4] = { 255, 0, 0, 255 }, GRN[4] = { 0, 255, 0, 255 }, WHT[4] = { 255, 255, 255, 255 };
	/* R: geometry at pixel rows 0..15 -> glReadPixels rows 0..15 under GL */
	begin(cell128, 0, 0, 0, 0);
	quad(white, BL_NONE, &ps->sprite, 0, 0, 128, 16, 0, 0, 1, 1, red, NOCLIP);
	img X = img_new(128, 128);
	readback_fb(cell128->fbo, 128, 128, &X, 0);
	int top = rows_are(&X, 0, 16, RED, 1), bot = rows_are(&X, 112, 128, RED, 1);
	const char *R = top && !bot ? "GL (row 0 first)" : bot && !top ? "FLIPPED (last row first)" : "NEITHER";
	g_rb_flip = bot && !top;
	if (g_rb_flip) flip_rows(&X);
	/* U: an uploaded texture's first row lands at pixel row 0 */
	uint8_t *up = (uint8_t *)malloc(128 * 128 * 4);
	for (int y = 0; y < 128; y++) for (int x = 0; x < 128; x++) memcpy(up + (y * 128 + x) * 4, y < 16 ? GRN : (const uint8_t[4]){ 0, 0, 255, 255 }, 4);
	tex *tu = up_rgba(128, 128, up, 0, "orientation");
	free(up);
	begin(cell2, 0, 0, 0, 0);
	quad(tu, BL_NONE, &ps->sprite, 0, 0, 128, 128, 0, 0, 1, 1, wht, NOCLIP);
	img Y = readback(cell2);
	int U = rows_are(&Y, 0, 16, GRN, 1);
	/* S: sampling a render target 1:1 into another keeps its orientation (the Stylizer and present rely on it) */
	begin(cell3, 0, 0, 0, 0);
	quad(cell128, BL_NONE, &ps->copy, 0, 0, 128, 128, 0, 0, 1, 1, wht, NOCLIP);
	img Z = readback(cell3);
	cmpres cr;
	ref_compare(&Z, &X, 0, 0, 128, 128, 0, &cr);
	int S = cr.maxd == 0;
	/* D: the scissor takes GlBackend's top-down y unchanged */
	begin(cell2, 0, 0, 0, 1);
	quad(white, BL_NONE, &ps->sprite, 0, 0, 128, 128, 0, 0, 1, 1, wht, (clipr){ 1, 0, 0, 128, 16 });
	img Dm = readback(cell2);
	static const uint8_t BLK[4] = { 0, 0, 0, 255 };
	int D = rows_are(&Dm, 0, 16, WHT, 1) && rows_are(&Dm, 16, 128, BLK, 1);
	set_clip(0, 0, 0, 0, 0);
	char d[400];
	snprintf(d, sizeof d, "readback row order %s", R);
	verdict(V_CORE, g_rb_flip ? L_WARN : top ? L_PASS : L_FAIL, "orientation.readback", g_rb_flip ?
	        "glReadPixels on an FBO returns the LAST row first (not GL): GlBackend's screenshots/ReadPixels would come out upside down; every later check compensates" : d);
	snprintf(d, sizeof d, "texture row 0 -> pixel row 0: %s", U ? "yes" : "NO (uploads and geometry disagree: every sprite would be upside down)");
	verdict(V_CORE, U ? L_PASS : L_FAIL, "orientation.upload", d);
	snprintf(d, sizeof d, "render target sampled 1:1 keeps orientation: %s (max diff %d)", S ? "yes" : "NO (render-to-texture flips: the Stylizer pass and present would flip)", cr.maxd);
	verdict(V_CORE, S ? L_PASS : L_FAIL, "orientation.rtt", d);
	snprintf(d, sizeof d, "glScissor(0,0,128,16) hits the rows the geometry y 0..16 hits: %s", D ? "yes" : "NO (clip rects would land mirrored)");
	verdict(V_CORE, D ? L_PASS : L_FAIL, "orientation.scissor", d);
	img_free(&X); img_free(&Y); img_free(&Z); img_free(&Dm);
}

static void cell_begin(tex *t, img *m, float r, float g, float b, float a)
{
	begin(t, r, g, b, a);
	model_clear(m, r, g, b, a);
	memcpy(g_bg, m->p, 4);
	g_bg_valid = 1;
}
static void cell_end(int cls, const char *name, tex *t, img *m, int tol, int warn)
{
	flush();
	img gpu = readback(t);
	char nm[96];
	snprintf(nm, sizeof nm, "%s", name);
	check_img(cls, nm, &gpu, m, tol, warn);
	g_bg_valid = 0;
	img_free(&gpu);
}

static void test_sprite(progset *ps)
{
	LOG("BEGIN sprite (sprite program: texture x vertex colour; blends, wrap, filter, clip)");
	float c[4][4];
	uint8_t *px = (uint8_t *)malloc(64 * 64 * 4);
	synth_rgba(px, 64, 64, 0x51u, 1);
	rtex r64 = { 64, 64, RT_RGBA8, px };
	tex *t64 = up_rgba(64, 64, px, 0, "rgba64");
	img m = img_new(64, 64);
	/* s1: nearest, 1:1, constant tint, opaque output of the product (NONE) */
	cell_begin(cell64, &m, 0, 0, 0, 0);
	solid4(c, 0.75f, 0.5f, 1.0f, 0.6f);
	dq(&m, (texpair){ &r64, t64 }, BL_NONE, &ps->sprite, SH_SPRITE, 0, 0, 64, 64, 0, 0, 1, 1, c, NOCLIP);
	cell_end(g_cls, "sprite.rgba8-nearest-tint", cell64, &m, 1, 3);
	if (g_cls == V_CORE) {
		/* negative control: the same GPU image against a reference with a 0.70 tint instead of 0.75 must be caught */
		img gpu = readback(cell64), wrong = img_new(64, 64);
		model_clear(&wrong, 0, 0, 0, 0);
		rquad q;
		memset(&q, 0, sizeof q);
		q.x1 = 64; q.y1 = 64; q.u1 = 1; q.v1 = 1;
		for (int k = 0; k < 4; k++) q.tl[k] = q.tr[k] = q.bl[k] = c[0][k];
		q.tl[0] = q.tr[0] = q.bl[0] = 0.70;
		q.tex = &r64; q.shader = SH_SPRITE; q.blend = BL_NONE;
		ref_quad(&wrong, &q);
		cmpres cr;
		ref_compare(&gpu, &wrong, 0, 0, 64, 64, 1, &cr);
		char d[160];
		snprintf(d, sizeof d, "a deliberately wrong reference (tint r 0.70, not 0.75) is caught: max diff %d, %ld px over tol", cr.maxd, cr.over);
		verdict(V_CORE, cr.maxd > 1 && cr.over > 100 ? L_PASS : L_FAIL, "selftest.negative-control", d);
		img_free(&gpu); img_free(&wrong);
	}
	/* s2: vertex colour gradient (affine: left red -> right blue, top alpha 1 -> bottom 0.2), white texture */
	cell_begin(cell64, &m, 0, 0, 0, 0);
	float g4[4][4] = { { 1, 0, 0, 1 }, { 0, 0, 1, 1 }, { 0, 0, 1, 0.2f }, { 1, 0, 0, 0.2f } };
	dq(&m, WHITE, BL_NONE, &ps->sprite, SH_SPRITE, 0, 0, 64, 64, 0, 0, 1, 1, g4, NOCLIP);
	cell_end(g_cls, "sprite.vertex-gradient", cell64, &m, 2, 4);
	/* s3: REPEAT on a POT texture (uv 0..3) */
	uint8_t *p16 = (uint8_t *)malloc(16 * 16 * 4);
	synth_rgba(p16, 16, 16, 0x16u, 0);
	rtex r16 = { 16, 16, RT_RGBA8, p16 };
	tex *t16 = up_rgba(16, 16, p16, 0, "rgba16");
	cell_begin(cell64, &m, 0, 0, 0, 0);
	solid4(c, 1, 1, 1, 1);
	dq(&m, (texpair){ &r16, t16 }, BL_NONE, &ps->sprite, SH_SPRITE, 0, 0, 48, 48, 0, 0, 3, 3, c, NOCLIP);
	cell_end(g_cls, "sprite.repeat-pot", cell64, &m, 1, 3);
	/* s4 (INFO): REPEAT on NPOT 12x12 -- outside GLES2; GlBackend counts such draws (WrapNpotDraws, 0 at the menu) */
	uint8_t *p12 = (uint8_t *)malloc(12 * 12 * 4);
	synth_rgba(p12, 12, 12, 0x12u, 0);
	rtex r12 = { 12, 12, RT_RGBA8, p12 };
	tex *t12 = up_rgba(12, 12, p12, 0, "rgba12-npot");
	cell_begin(cell64, &m, 0, 0, 0, 0);
	dq(&m, (texpair){ &r12, t12 }, BL_NONE, &ps->sprite, SH_SPRITE, 0, 0, 36, 36, 0, 0, 3, 3, c, NOCLIP);
	cell_end(V_INFO, "sprite.repeat-npot (outside GLES2)", cell64, &m, 1, 3);
	/* s5: LINEAR, 2x magnification of a 32x32 texture into 64x64 (weights 1/4, 3/4) */
	uint8_t *p32 = (uint8_t *)malloc(32 * 32 * 4);
	synth_rgba(p32, 32, 32, 0x32u, 1);
	rtex r32 = { 32, 32, RT_RGBA8, p32 };
	tex *t32 = up_rgba(32, 32, p32, 1, "rgba32-linear");
	cell_begin(cell64, &m, 0, 0, 0, 0);
	dq(&m, (texpair){ &r32, t32 }, BL_NONE, &ps->sprite, SH_SPRITE, 0, 0, 64, 64, 0, 0, 1, 1, c, NOCLIP);
	cell_end(g_cls, "sprite.linear-2x", cell64, &m, 2, 6);
	/* s6: clip (scissor) */
	cell_begin(cell64, &m, 0.2f, 0.4f, 0.6f, 1);
	dq(&m, (texpair){ &r64, t64 }, BL_NONE, &ps->sprite, SH_SPRITE, 0, 0, 64, 64, 0, 0, 1, 1, c, (clipr){ 1, 10, 20, 30, 12 });
	cell_end(g_cls, "sprite.clip", cell64, &m, 1, 3);
	set_clip(0, 0, 0, 0, 0);
	/* blends: dst prefilled, then the hash texture x tint with each mode; premultiplied modes get PMA data + premultiplied tint
	 * (SceneRenderer.Emit premultiplies the vertex colour for Spine) */
	uint8_t *pma = (uint8_t *)malloc(64 * 64 * 4);
	memcpy(pma, px, 64 * 64 * 4);
	premultiply(pma, 64 * 64);
	rtex rp = { 64, 64, RT_RGBA8, pma };
	tex *tp = up_rgba(64, 64, pma, 0, "rgba64-pma");
	for (int mode = BL_NONE; mode < BL_COUNT; mode++) {
		int pm = mode == BL_PREMUL || mode == BL_ADD_PREMUL;
		cell_begin(cell64, &m, 0, 0, 0, 0);
		float base[4][4];
		solid4(base, 0.2f, 0.6f, 0.8f, 0.4f);
		dq(&m, WHITE, BL_NONE, &ps->sprite, SH_SPRITE, 0, 0, 64, 64, 0, 0, 1, 1, base, NOCLIP);
		float a = 0.8f;
		solid4(c, pm ? 0.9f * a : 0.9f, pm ? 0.8f * a : 0.8f, pm ? 0.6f * a : 0.6f, a);
		dq(&m, pm ? (texpair){ &rp, tp } : (texpair){ &r64, t64 }, mode, &ps->sprite, SH_SPRITE, 4, 4, 60, 60, 0.0625, 0.0625, 0.9375, 0.9375, c, NOCLIP);
		char nm[64];
		snprintf(nm, sizeof nm, "blend.%s%s", ref_blend_names[mode], mode == BL_PREMUL ? " (Spine premultiplied)" : "");
		cell_end(g_cls, nm, cell64, &m, 2, 4);
	}
	/* GL_ALPHA through the sprite program: GLES says (0,0,0,a); the SDF program reads .a only, so this is INFO */
	solid4(c, 1, 1, 1, 1);
	uint8_t *a8 = (uint8_t *)malloc(64 * 64);
	for (int i = 0; i < 64 * 64; i++) a8[i] = (uint8_t)(i * 7 + (i >> 6));
	rtex ra = { 64, 64, RT_A8, a8 };
	tex *ta = up_alpha(64, 64, a8, 0, "alpha64");
	cell_begin(cell64, &m, 0, 0, 0, 0);
	dq(&m, (texpair){ &ra, ta }, BL_NONE, &ps->sprite, SH_SPRITE, 0, 0, 64, 64, 0, 0, 1, 1, c, NOCLIP);
	cell_end(V_INFO, "alpha8.sampled-rgb (GLES: 0,0,0,a)", cell64, &m, 1, 3);
	img_free(&m);
	free(px); free(p16); free(p12); free(p32); free(pma); free(a8);
}

static void test_dxt(progset *ps)
{
	LOG("BEGIN dxt (S3TC upload: engine Bcn.cs decoder is the reference)");
	float c[4][4];
	solid4(c, 1, 1, 1, 1);
	struct { const char *name; int w, h, dxt5, mode; int cls; } cases[] = {
		{ "dxt1.4-colour 64x64", 64, 64, 0, 0, V_CORE },
		{ "dxt1.punch-through 64x64 (RGBA_DXT1)", 64, 64, 0, 1, V_CORE },
		{ "dxt5 64x64", 64, 64, 1, 0, V_CORE },
		{ "dxt5.npot 100x60", 100, 60, 1, 0, V_CORE },
		{ "dxt5.colour-c0<=c1 (engine: 4-colour)", 64, 64, 1, 1, V_INFO },
	};
	for (unsigned i = 0; i < sizeof cases / sizeof cases[0]; i++) {
		int w = cases[i].w, h = cases[i].h, nblk = (w / 4) * (h / 4);
		uint8_t *blk = (uint8_t *)malloc((size_t)nblk * 16), *rgba = (uint8_t *)malloc((size_t)w * h * 4);
		if (cases[i].dxt5) { synth_bc3(blk, nblk, 0xD5u + i, cases[i].mode); ref_bc3_decode(blk, w, h, rgba); }
		else { synth_bc1(blk, nblk, 0xD1u + i, cases[i].mode); ref_bc1_decode(blk, w, h, rgba); }
		tex *t = up_dxt(w, h, cases[i].dxt5, blk, 0, cases[i].name);
		rtex rt = { w, h, RT_RGBA8, rgba };
		tex *cell = w == 100 ? cellx : cell64;
		img m = img_new(cell->w, cell->h);
		int cls = cases[i].cls == V_CORE ? g_cls : V_INFO;
		cell_begin(cell, &m, 0, 0, 0, 0);
		dq(&m, (texpair){ &rt, t }, BL_NONE, &ps->sprite, SH_SPRITE, 0, 0, w, h, 0, 0, 1, 1, c, NOCLIP);
		if (cases[i].mode == 1 && cases[i].dxt5) {
			/* which rule does the GPU use for a BC3 colour block with c0 <= c1? */
			flush();
			img gpu = readback(cell);
			uint8_t *alt = (uint8_t *)malloc((size_t)w * h * 4);
			ref_bc3_decode_3colour(blk, w, h, alt);
			img m3 = img_new(cell->w, cell->h);
			model_clear(&m3, 0, 0, 0, 0);
			rtex r3 = { w, h, RT_RGBA8, alt };
			rquad q;
			memset(&q, 0, sizeof q);
			q.x1 = w; q.y1 = h; q.u1 = 1; q.v1 = 1;
			for (int k = 0; k < 4; k++) q.tl[k] = q.tr[k] = q.bl[k] = 1;
			q.tex = &r3; q.shader = SH_SPRITE; q.blend = BL_NONE;
			ref_quad(&m3, &q);
			cmpres a4, a3;
			ref_compare(&gpu, &m, 0, 0, w, h, 4, &a4);
			ref_compare(&gpu, &m3, 0, 0, w, h, 4, &a3);
			char d[300];
			snprintf(d, sizeof d, "GPU matches the 4-colour rule (engine Bcn.cs) with max diff %d, the 3-colour+transparent rule with max diff %d -> %s", a4.maxd, a3.maxd,
			         a4.maxd <= 4 ? "same as the engine" : a3.maxd <= 4 ? "GPU uses 3-colour: the pack's BC3 encoder must emit c0 > c1" : "neither");
			verdict(V_INFO, a4.maxd <= 4 ? L_PASS : L_WARN, cases[i].name, d);
			img_free(&gpu); img_free(&m3); free(alt);
		} else cell_end(cls, cases[i].name, cell, &m, 4, 8);
		img_free(&m);
		free(blk); free(rgba);
	}
}

static void test_sdf(progset *ps)
{
	LOG("BEGIN sdf (sprite+SDF program on a GL_ALPHA atlas, UNPACK_ALIGNMENT 1, odd width)");
	int w = 62, h = 46;
	uint8_t *a8 = (uint8_t *)malloc((size_t)w * h);
	synth_sdf(a8, w, h, 16);
	rtex rt = { w, h, RT_A8, a8 };
	tex *tn = up_alpha(w, h, a8, 0, "sdf62x46-nearest"), *tl = up_alpha(w, h, a8, 1, "sdf62x46-linear");
	float c[4][4];
	img m = img_new(128, 128);
	cell_begin(cell128, &m, 0, 0, 0, 0);
	solid4(c, 1.0f, 0.85f, 0.3f, 1.0f);
	dq(&m, (texpair){ &rt, tn }, BL_NONE, &ps->sdf, SH_SDF, 0, 0, w, h, 0, 0, 1, 1, c, NOCLIP);
	cell_end(g_cls, "sdf.nearest-1:1", cell128, &m, 2, 6);
	cell_begin(cell128, &m, 0.2f, 0.2f, 0.4f, 1);   /* exact x255: no rounding ambiguity in the clear */
	solid4(c, 0.95f, 0.9f, 0.8f, 0.9f);
	dq(&m, (texpair){ &rt, tl }, BL_BLEND, &ps->sdf, SH_SDF, 2, 4, 2 + 2 * w, 4 + 2 * h, 0, 0, 1, 1, c, NOCLIP);
	cell_end(g_cls, "sdf.linear-2x-blend", cell128, &m, 8, 24);
	img_free(&m);
	free(a8);
}

static void test_copy(progset *ps)
{
	LOG("BEGIN copy (copy program: FBO -> FBO 1:1, and the present-shaped NDC quad)");
	float c[4][4];
	solid4(c, 1, 1, 1, 1);
	begin(fA, 0, 0, 0, 0);
	quad(t_src, BL_NONE, &ps->copy, 0, 0, SW, SH, 0, 0, 1, 1, c, NOCLIP);
	img a = readback(fA);
	check_img(g_cls, "copy.texture-to-fbo 960x544", &a, &g_src, 0, 1);
	/* GlBackend.PresentCopy's quad (bottom-left of the window = last row of a top-down target), drawn into an FBO here */
	present_to(ps, fA, fD->fbo);
	img d = img_new(SW, SH);
	readback_fb(fD->fbo, SW, SH, &d, 1);
	img e = img_new(SW, SH);
	for (int y = 0; y < SH; y++) memcpy(img_px(&e, 0, y), img_px(&g_src, 0, SH - 1 - y), (size_t)SW * 4);
	check_img(g_cls, "copy.present-quad (flips into a bottom-up framebuffer)", &d, &e, 0, 1);
	img_free(&a); img_free(&d); img_free(&e);
}

#if GLTEST_HAVE_STYLIZER
static void test_stylizer(progset *ps)
{
	LOG("BEGIN stylizer (GPU Stylizer pass vs the CPU reference applied to the pass's own input)");
	float c[4][4];
	solid4(c, 1, 1, 1, 1);
	struct { const char *name; int two, block; float intensity, lerp; } cases[] = {
		{ "stylizer 1 palette, block 1, intensity 1", 0, 1, 1.0f, 0 },
		{ "stylizer 1 palette, pixelate block 3, intensity 0.75", 0, 3, 0.75f, 0 },
		{ "stylizer+TWO_PALETTES lerp 0.5, block 2, LUT2 256x48", 1, 2, 1.0f, 0.5f },
	};
	for (unsigned i = 0; i < sizeof cases / sizeof cases[0]; i++) {
		begin(fA, 0, 0, 0, 0);
		quad(t_src, BL_NONE, &ps->copy, 0, 0, SW, SH, 0, 0, 1, 1, c, NOCLIP);
		flush();
		img in = readback(fA);
		sty_args sa = { t_lut1, cases[i].two ? t_lut2 : NULL, cases[i].two ? t_lm2 : t_lm1, cases[i].block, cases[i].intensity, cases[i].lerp };
		stylize(ps, fA, fB, &sa);
		img out = readback(fB), exp = img_new(SW, SH);
		rsty rs = { &in, cases[i].two ? &g_lm2 : &g_lm1, &g_lut1, cases[i].two ? &g_lut2 : NULL, cases[i].block, cases[i].intensity, cases[i].lerp };
		ref_stylize(&exp, &rs);
		check_img(g_cls, cases[i].name, &out, &exp, 1, 1);
		img_free(&in); img_free(&out); img_free(&exp);
	}
}
#endif

static void run_correctness(progset *ps)
{
	if (!ps->sprite.ok || !ps->copy.ok) { skip("correctness", "sprite or copy program missing"); return; }
	test_sprite(ps);
	test_dxt(ps);
	if (ps->sdf.ok) test_sdf(ps); else skip("sdf", "program failed");
	test_copy(ps);
#if GLTEST_HAVE_STYLIZER
	if (ps->sty1.ok && ps->sty2.ok) test_stylizer(ps); else skip("stylizer", "program failed");
#else
	skip("stylizer", "the stylizer shader and reference are owner-local (engine/Derived) and not built in");
#endif
}

/* ================================================================== API pre-flight: GlBackend's own calls, error by error */
static void test_api(void)
{
	LOG("BEGIN api (GlBackend's init calls one by one through the table; GL errors per call)");
	errs();
	int n = 0, bad = 0;
#define TRY(expr) do { expr; const char *e_ = errs(); if (*e_) { LOG("API %s -> GL errors%s", #expr, e_); bad++; } n++; } while (0)
	TRY(G.Disable(GL_DEPTH_TEST));
	TRY(G.Disable(GL_CULL_FACE));
	TRY(G.Disable(GL_DITHER));
	TRY(G.Disable(GL_STENCIL_TEST));
	TRY(G.PixelStorei(GL_UNPACK_ALIGNMENT, 4));
	TRY(G.PixelStorei(GL_PACK_ALIGNMENT, 4));
	TRY(G.PixelStorei(GL_UNPACK_ALIGNMENT, 1));
	TRY(G.PixelStorei(GL_UNPACK_ALIGNMENT, 4));
	GLint v = 0;
	TRY(G.GetIntegerv(GL_MAX_TEXTURE_SIZE, &v));
	TRY(G.Disable(GL_BLEND));
	TRY(G.Disable(GL_SCISSOR_TEST));
#undef TRY
	char d[200];
	snprintf(d, sizeof d, "%d calls, %d set a GL error (GlBackend logs such errors as '[gl] init errors' and goes on)", n, bad);
	verdict(V_CORE, bad ? L_WARN : L_PASS, "api.init-sequence", d);
	void (*raw)(GLenum, GLint) = (void (*)(GLenum, GLint))shepgl_raw("glPixelStorei");
	if (raw) {
		raw(GL_UNPACK_ALIGNMENT, 1);
		const char *e = errs();
		snprintf(d, sizeof d, "vitaGL's own glPixelStorei(GL_UNPACK_ALIGNMENT, 1) -> GL errors%s (the shepgl wrapper accepts it: vitaGL reads tight rows)", *e ? e : " none");
		verdict(V_INFO, *e ? L_WARN : L_PASS, "api.raw-pixelstorei", d);
		raw(GL_UNPACK_ALIGNMENT, 4);
		errs();
	}
}

static void log_gl_info(void)
{
	LOG("BEGIN glinfo");
	LOG("GL vendor '%s' renderer '%s' version '%s' GLSL '%s'", (const char *)G.GetString(GL_VENDOR), (const char *)G.GetString(GL_RENDERER),
	    (const char *)G.GetString(GL_VERSION), (const char *)G.GetString(GL_SHADING_LANGUAGE_VERSION));
	GLint mts = 0, mtu = 0, mvv = 0, mfu = 0, ua = 0, pa = 0;
	G.GetIntegerv(GL_MAX_TEXTURE_SIZE, &mts);
	G.GetIntegerv(GL_MAX_TEXTURE_IMAGE_UNITS, &mtu);
	G.GetIntegerv(GL_MAX_VARYING_VECTORS, &mvv);
	G.GetIntegerv(GL_MAX_FRAGMENT_UNIFORM_VECTORS, &mfu);
	G.GetIntegerv(GL_UNPACK_ALIGNMENT, &ua);
	G.GetIntegerv(GL_PACK_ALIGNMENT, &pa);
	const char *e = errs();
	LOG("GL max texture %d, texture units %d, varying vectors %d, fragment uniform vectors %d, unpack/pack alignment %d/%d (errors:%s)", mts, mtu, mvv, mfu, ua, pa, *e ? e : " none");
	int s3 = shepgl_has_extension("GL_EXT_texture_compression_s3tc");
	char d[200];
	snprintf(d, sizeof d, "GL_EXT_texture_compression_s3tc %s (GlBackend enables DXT upload only when this is advertised); max texture %d (GlBackend cap 4096)",
	         s3 ? "advertised" : "NOT advertised", mts);
	verdict(V_CORE, s3 && mts >= 4096 ? L_PASS : L_WARN, "glinfo", d);
	const char *ext = (const char *)G.GetString(GL_EXTENSIONS);
	LOG_LINES("GL extensions: ", ext ? ext : "(null)");
}

/* ================================================================== performance */
typedef struct { double submit, serial, smin, smax; int frames; } perfres;
typedef void (*workload)(progset *ps, void *ctx);
static void perf_run(const char *name, progset *ps, workload fn, void *ctx, tex *show, int warm, int frames, perfres *out)
{
	perfres r = { 0, 0, 1e9, 0, 0 };
	long d0 = 0;
	for (int f = 0; f < warm + frames; f++) {
		if (f == warm) d0 = stt.draws;
		uint64_t t0 = plat_us();
		fn(ps, ctx);
		flush();
		uint64_t t1 = plat_us();
		G.Finish();
		uint64_t t2 = plat_us();
		if (f >= warm) {
			double s = (t2 - t0) / 1000.0;
			r.submit += (t1 - t0) / 1000.0; r.serial += s;
			if (s < r.smin) r.smin = s;
			if (s > r.smax) r.smax = s;
			r.frames++;
		}
		if (show) { present(ps, show); shepgl_swap(); G.Finish(); }   /* live view, not timed */
	}
	r.submit /= r.frames; r.serial /= r.frames;
	LOG("PERF [%s] %-34s submit %6.2f ms | serialized (GPU-synced) %6.2f ms (min %.2f max %.2f) | %ld draws/frame | %d frames after %d warm-up",
	    ps->tag, name, r.submit, r.serial, r.smin, r.smax, (stt.draws - d0) / (frames > 0 ? frames : 1) - (show ? 1 : 0), r.frames, warm);
	if (out) *out = r;
}

typedef struct { tex *t; prog *p; int n, size; int blend; } fillctx;
static void wl_fill(progset *ps, void *vc)
{
	fillctx *c = (fillctx *)vc;
	(void)ps;
	begin(fC, 0.1f, 0.1f, 0.12f, 1);
	float col[4][4];
	solid4(col, 1, 1, 1, 0.9f);
	quad(c->t, c->blend, c->p, 0, 0, SW, SH, 0, 0, 1, 1, col, NOCLIP);   /* one full-screen layer (scaled, LINEAR) */
	uint32_t s = 0x1234u;
	for (int i = 0; i < c->n; i++) {
		float x = (float)(xs32(&s) % (SW - c->size)), y = (float)(xs32(&s) % (SH - c->size));
		quad(c->t, c->blend, c->p, x, y, x + c->size, y + c->size, 0, 0, 0.125f * c->size / 64.0f, 0.125f * c->size / 64.0f, col, NOCLIP);
	}
}
typedef struct { tex *t; prog *p; int n; } textctx;
static void wl_text(progset *ps, void *vc)
{
	textctx *c = (textctx *)vc;
	(void)ps;
	begin(fC, 0.1f, 0.1f, 0.12f, 1);
	float col[4][4];
	solid4(col, 1, 0.95f, 0.85f, 1);
	for (int i = 0; i < c->n; i++) {
		int gx = i % 38, gy = (i / 38) % 21, cell = i % 128;
		float u0 = (cell % 16) * 32 / 512.0f, v0 = (cell / 16) * 32 / 256.0f;
		quad(c->t, BL_BLEND, c->p, gx * 25.0f, gy * 25.0f, gx * 25.0f + 24, gy * 25.0f + 24, u0, v0, u0 + 24 / 512.0f, v0 + 24 / 256.0f, col, NOCLIP);
	}
}
static void wl_copy(progset *ps, void *vc) { (void)vc; present_to(ps, fA, fD->fbo); target = NULL; }
#if GLTEST_HAVE_STYLIZER
static void wl_sty(progset *ps, void *vc) { stylize(ps, fA, fB, (sty_args *)vc); }
#endif
typedef struct { tex **t; int nt; prog *p; int n; } drawsctx;
static void wl_draws(progset *ps, void *vc)
{
	drawsctx *c = (drawsctx *)vc;
	(void)ps;
	begin(fC, 0.1f, 0.1f, 0.12f, 1);
	float col[4][4];
	solid4(col, 1, 1, 1, 1);
	for (int i = 0; i < c->n; i++) {
		float x = (float)((i % 25) * 38), y = (float)((i / 25) * 38);
		quad(c->t[i % c->nt], BL_BLEND, c->p, x, y, x + 32, y + 32, 0, 0, 0.125f, 0.125f, col, NOCLIP);   /* texture changes every draw: 1 batch each */
	}
}

/* ================================================================== the menu-like frame (needs the owner-local stylizer) */
static double g_menu_serial = -1, g_menu_pipe = -1;
#if GLTEST_HAVE_STYLIZER
typedef struct { int ti, blend, sdf, x0, y0, x1, y1; double u0, v0, u1, v1; float c[4]; clipr cl; } mq;
#define NWT 10
typedef struct {
	tex *t[NWT + 2];
	rtex rt[NWT + 2];
	uint8_t *mem[NWT + 2];
	mq *world, *ui;
	int nworld, nui;
	sty_args sa;
} menu;
static menu M;

static void menu_build(void)
{
	/* textures: 0 = background 960x544 RGBA8 (linear), 1..5 DXT5 256x256, 6..8 DXT1 256x256, 9 DXT5 200x120 (NPOT),
	 * 10 = UI RGBA8 256x256, 11 = SDF ALPHA8 512x256 */
	uint8_t *bg = (uint8_t *)malloc((size_t)SW * SH * 4);
	for (int y = 0; y < SH; y++) for (int x = 0; x < SW; x++) { uint8_t *p = bg + ((size_t)y * SW + x) * 4; p[0] = (uint8_t)(40 + y / 6); p[1] = (uint8_t)(30 + (x ^ y) % 50); p[2] = (uint8_t)(60 + x / 12); p[3] = 255; }
	M.mem[0] = bg; M.rt[0] = (rtex){ SW, SH, RT_RGBA8, bg }; M.t[0] = up_rgba(SW, SH, bg, 1, "menu-bg");
	for (int i = 1; i < NWT; i++) {
		int w = i == 9 ? 200 : 256, h = i == 9 ? 120 : 256, dxt5 = i <= 5 || i == 9, nblk = (w / 4) * (h / 4);
		uint8_t *blk = (uint8_t *)malloc((size_t)nblk * 16), *rgba = (uint8_t *)malloc((size_t)w * h * 4);
		if (dxt5) { synth_bc3(blk, nblk, 0x500u + i, 0); ref_bc3_decode(blk, w, h, rgba); }
		else { synth_bc1(blk, nblk, 0x100u + i, 0); ref_bc1_decode(blk, w, h, rgba); }
		char nm[32];
		snprintf(nm, sizeof nm, "menu-world-%d", i);
		M.t[i] = up_dxt(w, h, dxt5, blk, 1, nm);
		M.rt[i] = (rtex){ w, h, RT_RGBA8, rgba };
		M.mem[i] = rgba;
		free(blk);
	}
	uint8_t *ui = (uint8_t *)malloc(256 * 256 * 4);
	synth_rgba(ui, 256, 256, 0x41u, 1);
	M.mem[10] = ui; M.rt[10] = (rtex){ 256, 256, RT_RGBA8, ui }; M.t[10] = up_rgba(256, 256, ui, 1, "menu-ui");
	uint8_t *sd = (uint8_t *)malloc(512 * 256);
	synth_sdf(sd, 512, 256, 32);
	M.mem[11] = sd; M.rt[11] = (rtex){ 512, 256, RT_A8, sd }; M.t[11] = up_alpha(512, 256, sd, 1, "menu-sdf");
	/* world: batch 0 = the background, then 23 batches x 23 quads cycling textures 1..9 (every break = texture, like the menu) */
	M.world = (mq *)calloc(600, sizeof(mq));
	M.ui = (mq *)calloc(400, sizeof(mq));
	uint32_t s = 0xC0FFEEu;
	int n = 0;
	M.world[n++] = (mq){ 0, BL_NONE, 0, 0, 0, SW, SH, 0, 0, 1, 1, { 1, 1, 1, 1 }, { 0 } };
	for (int b = 0; b < 23; b++) {
		int ti = 1 + b % 9, tw = M.rt[ti].w, th = M.rt[ti].h, blend = (ti <= 5 && (b & 1)) ? BL_PREMUL : BL_BLEND;
		for (int k = 0; k < 23; k++) {
			int w = 32 + (int)(xs32(&s) % 97), h = 32 + (int)(xs32(&s) % 97);
			if (w > tw) w = tw;
			if (h > th) h = th;
			int x = (int)(xs32(&s) % (SW - w)), y = (int)(xs32(&s) % (SH - h)), tx = (int)(xs32(&s) % (tw - w + 1)), ty = (int)(xs32(&s) % (th - h + 1));
			float a = 0.55f + (xs32(&s) % 46) / 100.0f, r = 0.6f + (xs32(&s) % 41) / 100.0f, g = 0.6f + (xs32(&s) % 41) / 100.0f, bl = 0.6f + (xs32(&s) % 41) / 100.0f;
			mq q = { ti, blend, 0, x, y, x + w, y + h, (double)tx / tw, (double)ty / th, (double)(tx + w) / tw, (double)(ty + h) / th, { r, g, bl, a }, { 0 } };
			if (blend == BL_PREMUL) { q.c[0] *= a; q.c[1] *= a; q.c[2] *= a; }
			M.world[n++] = q;
		}
	}
	M.nworld = n;
	/* UI: 42 batches alternating SDF text runs (6 glyphs) and RGBA sprites (1-2 quads); every 7th batch clipped to a panel */
	n = 0;
	for (int b = 0; b < 42; b++) {
		clipr cl = (b % 7 == 3) ? (clipr){ 1, 300, 100 + (b / 7) * 60, 360, 40 } : NOCLIP;
		int row = b / 2, x = 40 + (b % 4) * 220, y = 30 + row * 23;
		if (b % 7 == 3) { x = 290 + (b % 3) * 30; y = 96 + (b / 7) * 60; }
		if ((b & 1) == 0) {
			for (int k = 0; k < 6; k++) {
				int cell = (b * 6 + k) % 128, gu = (cell % 16) * 32 + 4, gv = (cell / 16) * 32 + 4;
				M.ui[n++] = (mq){ 11, BL_BLEND, 1, x + k * 26, y, x + k * 26 + 24, y + 24, gu / 512.0, gv / 256.0, (gu + 24) / 512.0, (gv + 24) / 256.0, { 1, 0.95f, 0.85f, 1 }, cl };
			}
		} else {
			int cnt = 1 + (b % 3 == 0);
			for (int k = 0; k < cnt; k++) {
				int w = 64 + (int)(xs32(&s) % 97), h = 24 + (int)(xs32(&s) % 25), tx = (int)(xs32(&s) % (256 - w)), ty = (int)(xs32(&s) % (256 - h)), xx = x + 160 + k * 8, yy = y + k * 4;
				if (xx + w > SW) xx = SW - w;
				M.ui[n++] = (mq){ 10, BL_BLEND, 0, xx, yy, xx + w, yy + h, tx / 256.0, ty / 256.0, (tx + w) / 256.0, (ty + h) / 256.0, { 1, 1, 1, 0.85f }, cl };
			}
		}
	}
	M.nui = n;
	M.sa = (sty_args){ t_lut1, NULL, t_lm1, 1, 1.0f, 0 };
}
static void mq_draw(progset *ps, img *model, const mq *q)
{
	float c[4][4];
	solid4(c, q->c[0], q->c[1], q->c[2], q->c[3]);
	dq(model, (texpair){ &M.rt[q->ti], M.t[q->ti] }, q->blend, q->sdf ? &ps->sdf : &ps->sprite, q->sdf ? SH_SDF : SH_SPRITE, q->x0, q->y0, q->x1, q->y1,
	   q->u0, q->v0, q->u1, q->v1, c, q->cl);
}
static void menu_world(progset *ps, img *model)
{
	begin(fA, 0, 0, 0, 1);
	for (int i = 0; i < M.nworld; i++) mq_draw(ps, model, &M.world[i]);
	flush();
}
static void menu_ui(progset *ps, img *model)
{
	bind_target(fB);
	for (int i = 0; i < M.nui; i++) mq_draw(ps, model, &M.ui[i]);
	flush();
	set_clip(0, 0, 0, 0, 0);
}

static double g_menu_submit = -1;
static void test_menu(progset *ps)
{
	LOG("BEGIN menu-frame (Main-Menu-shaped: world pass into FBO 0, GPU Stylizer into FBO 1, UI on top, present, swap)");
	menu_build();
	/* verification frame: chain of custody, each stage against the CPU applied to the GPU's own input */
	img wm = img_new(SW, SH);
	model_clear(&wm, 0, 0, 0, 1);
	long d0 = stt.draws;
	menu_world(ps, &wm);
	long dw = stt.draws - d0;
	img A = readback(fA);
	check_img(g_cls, "menu.world-pass (24 batches, DXT1/DXT5/RGBA8, BLEND+PREMULTIPLIED)", &A, &wm, 4, 8);
	d0 = stt.draws;
	stylize(ps, fA, fB, &M.sa);
	img Bs = readback(fB), be = img_new(SW, SH);
	rsty rs = { &A, &g_lm1, &g_lut1, NULL, 1, 1.0, 0 };
	ref_stylize(&be, &rs);
	check_img(g_cls, "menu.stylizer-pass (on the GPU's world pass)", &Bs, &be, 1, 1);
	img um = img_new(SW, SH);
	memcpy(um.p, Bs.p, (size_t)SW * SH * 4);
	menu_ui(ps, &um);
	long du = stt.draws - d0 - 1;
	img Bu = readback(fB);
	check_img(g_cls, "menu.ui-pass (42 batches, SDF text + RGBA, clipped panels)", &Bu, &um, 4, 8);
	present(ps, fB);
	shepgl_swap();
	G.Finish();
	LOG("INFO menu frame: world %d quads -> %ld draws, Stylizer 1, UI %d quads -> %ld draws, present 1 = %ld draw calls (the real menu: 24 + 1 + 42 + present)",
	    M.nworld, dw, M.nui, du, dw + 1 + du + 1);
	img_free(&wm); img_free(&A); img_free(&Bs); img_free(&be); img_free(&um); img_free(&Bu);
	/* timing 1: serialized (glFinish after the swap: CPU + GPU, no overlap) */
	double sub = 0, ser = 0, swp = 0, mn = 1e9, mx = 0;
	int warm = 10, frames = 60;
	for (int f = 0; f < warm + frames; f++) {
		uint64_t t0 = plat_us();
		menu_world(ps, NULL);
		stylize(ps, fA, fB, &M.sa);
		menu_ui(ps, NULL);
		present(ps, fB);
		uint64_t t1 = plat_us();
		shepgl_swap();
		uint64_t t2 = plat_us();
		G.Finish();
		uint64_t t3 = plat_us();
		if (f >= warm) {
			double s = (t3 - t0) / 1000.0;
			sub += (t1 - t0) / 1000.0; swp += (t2 - t1) / 1000.0; ser += s;
			if (s < mn) mn = s;
			if (s > mx) mx = s;
		}
	}
	g_menu_submit = sub / frames;
	g_menu_serial = ser / frames;
	LOG("PERF [%s] menu frame serialized: submit %.2f ms, swap %.2f ms, frame %.2f ms (min %.2f max %.2f) = %.1f fps if nothing overlapped (%d frames, vsync off)",
	    ps->tag, sub / frames, swp / frames, g_menu_serial, mn, mx, 1000.0 / g_menu_serial, frames);
	/* timing 2: pipelined (no glFinish: CPU records frame N+1 while the GPU draws frame N; what a game loop gets) */
	for (int f = 0; f < warm; f++) { menu_world(ps, NULL); stylize(ps, fA, fB, &M.sa); menu_ui(ps, NULL); present(ps, fB); shepgl_swap(); }
	G.Finish();
	int pf = 120;
	uint64_t t0 = plat_us();
	for (int f = 0; f < pf; f++) { menu_world(ps, NULL); stylize(ps, fA, fB, &M.sa); menu_ui(ps, NULL); present(ps, fB); shepgl_swap(); }
	G.Finish();
	g_menu_pipe = (plat_us() - t0) / 1000.0 / pf;
	LOG("PERF [%s] menu frame pipelined: %.2f ms/frame = %.1f fps over %d frames (vsync off; the GPU-side bound of the real menu's draw shape)",
	    ps->tag, g_menu_pipe, 1000.0 / g_menu_pipe, pf);
}
#endif

static void test_perf(progset *ps)
{
	LOG("BEGIN perf (per program; GPU-synced with glFinish; first 10 frames excluded; screen shows each workload)");
	shepgl_set_vsync(0);
	int nblk = (512 / 4) * (512 / 4);
	uint8_t *blk = (uint8_t *)malloc((size_t)nblk * 16), *rgba = (uint8_t *)malloc(512 * 512 * 4);
	synth_bc3(blk, nblk, 0xFEEDu, 0);
	tex *dxt = up_dxt(512, 512, 1, blk, 1, "perf-dxt5-512");
	synth_rgba(rgba, 512, 512, 0xFADEu, 1);
	tex *rgb8 = up_rgba(512, 512, rgba, 1, "perf-rgba8-512");
	uint8_t *sd = (uint8_t *)malloc(512 * 256);
	synth_sdf(sd, 512, 256, 32);
	tex *sdf = up_alpha(512, 256, sd, 1, "perf-sdf-512x256");
	fillctx f1 = { dxt, &ps->sprite, 200, 64, BL_BLEND }, f2 = { rgb8, &ps->sprite, 200, 64, BL_BLEND };
	perf_run("sprite DXT5: 1 screen + 200 quads", ps, wl_fill, &f1, fC, 10, 60, NULL);
	perf_run("sprite RGBA8: 1 screen + 200 quads", ps, wl_fill, &f2, fC, 10, 60, NULL);
	textctx tx = { sdf, &ps->sdf, 798 };
	perf_run("sprite+SDF: 798 glyphs 24x24", ps, wl_text, &tx, fC, 10, 60, NULL);
	float c[4][4];
	solid4(c, 1, 1, 1, 1);
	begin(fA, 0, 0, 0, 0);
	quad(t_src, BL_NONE, &ps->copy, 0, 0, SW, SH, 0, 0, 1, 1, c, NOCLIP);
	flush();
	perf_run("copy: present-shaped full screen", ps, wl_copy, NULL, fD, 10, 60, NULL);
#if GLTEST_HAVE_STYLIZER
	sty_args s1 = { t_lut1, NULL, t_lm1, 1, 1.0f, 0 }, s2 = { t_lut1, t_lut2, t_lm2, 1, 1.0f, 0.5f };
	perf_run("stylizer: full screen", ps, wl_sty, &s1, fB, 10, 60, NULL);
	perf_run("stylizer+TWO_PALETTES: full screen", ps, wl_sty, &s2, fB, 10, 60, NULL);
#endif
	tex *ts[8];
	for (int i = 0; i < 8; i++) { synth_bc3(blk, (256 / 4) * (256 / 4), 0x700u + i, 0); char nm[24]; snprintf(nm, sizeof nm, "perf-draws-%d", i); ts[i] = up_dxt(256, 256, 1, blk, 1, nm); }
	drawsctx dc = { ts, 8, &ps->sprite, 67 };
	perfres pr;
	perf_run("67 separate draws (texture per draw)", ps, wl_draws, &dc, fC, 10, 60, &pr);
	LOG("PERF [%s] per-draw CPU cost in vitaGL ~ %.1f us (submit / 67, includes the 2 buffer uploads)", ps->tag, pr.submit * 1000.0 / 67);
	free(blk); free(rgba); free(sd);
}

/* ================================================================== candidates (not in GlBackend) */
static void test_candidates(progset *ps)
{
	g_tag = "candidate";
	g_cls = V_CAND;
	LOG("BEGIN candidates (shaders NOT in GlBackend; reported as CAND, not counted in the verdict)");
	prog tb;
	int built = build_program(&tb, "spine-tintblack (candidate)", CAND_tintblack_vert, CAND_tintblack_frag, 1, NULL);
	g_cls = V_CORE;
	if (!built) { g_tag = "primary"; return; }
	uint8_t *px = (uint8_t *)malloc(64 * 64 * 4);
	synth_rgba(px, 64, 64, 0x7Bu, 1);
	premultiply(px, 64 * 64);
	rtex rt = { 64, 64, RT_RGBA8, px };
	tex *t = up_rgba(64, 64, px, 0, "tintblack-pma");
	img m = img_new(64, 64);
	cell_begin(cell64, &m, 0.4f, 0.4f, 0.4f, 1);
	flush();
	/* its own 48-byte vertex: pos 2f, light 4f, dark 4f, uv 2f; attribute 3 = dark */
	float a = 0.9f, light[4] = { 0.8f * a, 0.9f * a, 1.0f * a, a }, dark[4] = { 0.2f, 0.1f, 0.3f, 1.0f };
	float vb[4][12];
	float xs[4] = { 0, 64, 64, 0 }, ys[4] = { 0, 0, 64, 64 }, us[4] = { 0, 1, 1, 0 }, vs[4] = { 0, 0, 1, 1 };
	for (int k = 0; k < 4; k++) {
		float v[12] = { xs[k], ys[k], light[0], light[1], light[2], light[3], dark[0], dark[1], dark[2], dark[3], us[k], vs[k] };
		memcpy(vb[k], v, sizeof v);
	}
	GLuint b;
	G.GenBuffers(1, &b);
	G.BindBuffer(GL_ARRAY_BUFFER, b);
	G.BufferData(GL_ARRAY_BUFFER, sizeof vb, vb, GL_STREAM_DRAW);
	G.BindBuffer(GL_ELEMENT_ARRAY_BUFFER, quad_idx);
	set_blend(BL_PREMUL);
	set_clip(0, 0, 0, 0, 0);
	use_prog(&tb);
	bind_tex0(t);
	for (int k = 0; k < 4; k++) G.EnableVertexAttribArray((GLuint)k);
	G.VertexAttribPointer(0, 2, GL_FLOAT, 0, 48, (void *)0);
	G.VertexAttribPointer(1, 4, GL_FLOAT, 0, 48, (void *)8);
	G.VertexAttribPointer(3, 4, GL_FLOAT, 0, 48, (void *)24);
	G.VertexAttribPointer(2, 2, GL_FLOAT, 0, 48, (void *)40);
	G.DrawElements(GL_TRIANGLES, 6, GL_UNSIGNED_SHORT, NULL);
	glDisableVertexAttribArray(3);   /* not one of GlBackend's 51; only this candidate ever enables attribute 3 */
	attr_base = INT_MIN;
	rquad q;
	memset(&q, 0, sizeof q);
	q.x1 = 64; q.y1 = 64; q.u1 = 1; q.v1 = 1;
	for (int k = 0; k < 4; k++) { q.tl[k] = q.tr[k] = q.bl[k] = light[k]; q.dark[k] = dark[k]; }
	q.tex = &rt; q.shader = SH_TINTBLACK; q.blend = BL_PREMUL;
	ref_quad(&m, &q);
	cell_end(V_CAND, "spine-tintblack (PMA two-colour tint) draw", cell64, &m, 2, 4);
	img_free(&m);
	free(px);
	g_tag = "primary";
}

/* mediump experiment: the sprite programs with a mediump default (SHEPHERD_GL_FRAG_PRECISION=mediump in the engine) */
static void test_mediump(progset *ps)
{
	g_tag = "mediump";
	LOG("BEGIN mediump (experiment: sprite programs with 'precision mediump float' in the fragment shader; ALT, not the engine default)");
	progset mp = *ps;
	mp.tag = "mediump";
	mp.copy.tw = mp.copy.th = mp.sty1.tw = mp.sty1.th = mp.sty2.tw = mp.sty2.th = -1;   /* the copies' uniform caches mean nothing */
	g_cls = V_ALT;
	int ok = build_program(&mp.sprite, "sprite (mediump)", k_backend_programs[0].vs, k_backend_programs[0].fs, 0, "mediump");
	ok &= build_program(&mp.sdf, "sprite+SDF (mediump)", k_backend_programs[1].vs, k_backend_programs[1].fs, 0, "mediump");
	if (ok) {
		test_sprite(&mp);
		test_sdf(&mp);
		int nblk = (512 / 4) * (512 / 4);
		uint8_t *blk = (uint8_t *)malloc((size_t)nblk * 16);
		synth_bc3(blk, nblk, 0xFEEDu, 0);
		tex *dxt = up_dxt(512, 512, 1, blk, 1, "perf-dxt5-512b");
		fillctx f1 = { dxt, &mp.sprite, 200, 64, BL_BLEND };
		perf_run("sprite DXT5 (mediump): 1 screen + 200", &mp, wl_fill, &f1, fC, 10, 60, NULL);
		uint8_t *sd = (uint8_t *)malloc(512 * 256);
		synth_sdf(sd, 512, 256, 32);
		tex *sdf = up_alpha(512, 256, sd, 1, "perf-sdf-b");
		textctx tx = { sdf, &mp.sdf, 798 };
		perf_run("sprite+SDF (mediump): 798 glyphs", &mp, wl_text, &tx, fC, 10, 60, NULL);
		free(blk); free(sd);
	}
	g_cls = V_CORE;
	g_tag = "primary";
}

/* ================================================================== pools */
static shepgl_meminfo g_mem0, g_memmin;
static void mem_sample(const char *when)
{
	shepgl_meminfo m;
	shepgl_mem(&m);
	char d[400];
	int o = 0;
	for (int i = 0; i < 5; i++) {
		o += snprintf(d + o, sizeof d - o, "%s%s %.1f/%.1f", i ? ", " : "", shepgl_pool_name(i), m.free_[i] / MiB, m.total[i] / MiB);
		if (m.free_[i] < g_memmin.free_[i] || g_memmin.total[i] == 0) { g_memmin.free_[i] = m.free_[i]; g_memmin.total[i] = m.total[i]; }
	}
	LOG("VGL pools [%s] free/total MiB: %s", when, d);
}

/* ================================================================== final screen */
static void final_screen(progset *ps, int ok)
{
	tex *F = fC;
	float c[4][4];
	begin(F, ok ? 0.0f : 0.6f, ok ? 0.55f : 0.0f, ok ? 0.15f : 0.0f, 1);
	solid4(c, 1, 1, 1, 1);
	quad(white, BL_NONE, &ps->sprite, 0, 0, SW, 24, 0, 0, 1, 1, c, NOCLIP);   /* must show at the TOP of the screen */
	int nf = n_fail > 40 ? 40 : n_fail;
	for (int i = 0; i < nf; i++) quad(white, BL_NONE, &ps->sprite, 20.0f + i * 22, 60, 36.0f + i * 22, 76, 0, 0, 1, 1, c, NOCLIP);
	flush();
	shepgl_set_vsync(1);   /* probe pattern: vsync on before the exit loop */
	for (int i = 0; i < plat_exit_wait_frames(); i++) {
		present(ps, F);
		shepgl_swap();
		if (i > 30 && plat_button()) break;
	}
}

static void diag_line(const char *l) { LOG("SHACCCG %s", l); }

/* a dark-blue frame on the display between phases, so the screen never sits on stale content while the CPU reference runs */
static void show_working(void)
{
	flush();
	for (int i = 0; i < 2; i++) {
		G.BindFramebuffer(GL_FRAMEBUFFER, display_fbo());
		G.Viewport(0, 0, SW, SH);
		if (scissor_on) { G.Disable(GL_SCISSOR_TEST); scissor_on = 0; }
		G.ClearColor(0.0f, 0.0f, 0.3f, 1.0f);
		G.Clear(GL_COLOR_BUFFER_BIT);
		shepgl_swap();
	}
	target = NULL;
	clip_dirty = 1;
}

int main(void)
{
	plat_log_open();
	LOG("=== %s (build %s) ===", GLTEST_VERSION, GLTEST_BUILD);
	plat_screen(1);
	LOG("Shepherd GL Test: GlBackend's shaders + GL calls on vitaGL, checked pixel by pixel against a CPU reference. Synthetic data only.");
	LOG("Do not close the app; it ends on its own (about 1-2 minutes). Log: %s", plat_log_path());
	LOG("platform %s, shader dialect %s", plat_name(), plat_dialect());
	LOG("shaders: sha256 %s; vita/v4/shaders-essl100 dump %s; %s", SHADERS_SHA256, SHADERS_DUMP_AGREES ? "agrees with the engine text" : "STALE: " SHADERS_DUMP_STALE, SHADERS_GLSLANG);
	plat_clocks_set();
	char cl[200];
	plat_clocks_str(cl, sizeof cl);
	LOG("CLK %s", cl);

	LOG("BEGIN presence");
	char where[300];
	if (!plat_shacccg(where, sizeof where)) {
		LOG("SKIP gltest: libshacccg.suprx not found (%s). vitaGL needs it to compile GLSL; nothing was tested.", where);
		LOG("SKIP to fix: on THIS console install PSM Runtime 1.00, then 2.00, then 2.01, then run ShaRKF00D:");
		LOG("SKIP it extracts ur0:data/libshacccg.suprx from your own PSM runtime. Never download the file. Then run this test again.");
		LOG("DONE SKIP -- press X, O or START to exit.");
		for (int i = 0; i < 60 * 600 && !plat_button(); i++) plat_delay_ms(16);
		plat_exit(0);
		return 0;
	}
	LOG("PASS presence: libshacccg %s", where);

	LOG("BEGIN init (handing the display to vitaGL: the log continues in the file only; the screen shows the workloads, then GREEN = all passed, RED = a FAIL)");
	plat_screen(0);
	shepgl_config cfg;
	shepgl_default_config(&cfg);
	cfg.vsync = 0;   /* measurements; the exit loop turns it on */
	int ir = shepgl_init(&cfg);
	LOG("%s %s", ir > 0 ? "PASS init:" : "FAIL init:", shepgl_status());
	if (ir == 0) { plat_screen(1); LOG("FAIL init: vglInitExtended failed -- nothing else can run. DONE -- press X, O or START."); for (int i = 0; i < 60 * 600 && !plat_button(); i++) plat_delay_ms(16); plat_exit(1); return 1; }
	shepgl_set_log(diag_line);
	mem_sample("after init");
	g_mem0 = g_memmin;
	if (!load_table()) { LOG("FAIL table incomplete -- stopping"); plat_exit(1); return 1; }
	if (ir < 0) {
		LOG("FAIL init: libshacccg.suprx is present but vitaGL could not load it (is_shark_online = 0): wrong file or a damaged copy. Re-extract it with ShaRKF00D.");
		G.ClearColor(0.6f, 0, 0, 1);
		for (int i = 0; i < 60 * 600; i++) { G.BindFramebuffer(GL_FRAMEBUFFER, 0); G.Clear(GL_COLOR_BUFFER_BIT); shepgl_swap(); if (i > 30 && plat_button()) break; }
		plat_exit(1);
		return 1;
	}
	log_gl_info();
	test_api();

	/* renderer state, as GlBackend's constructor leaves it */
	V = (vtx *)malloc(sizeof(vtx) * MAXV);
	I = (uint16_t *)malloc(sizeof(uint16_t) * MAXI);
	Bt = (batch *)malloc(sizeof(batch) * MAXB);
	G.GenBuffers(RING, vbo);
	G.GenBuffers(RING, ibo);
	build_quads();
	static const uint8_t W1[4] = { 255, 255, 255, 255 };
	white = up_rgba(1, 1, W1, 0, "white");
	rt_white_1x1 = (rtex){ 1, 1, RT_RGBA8, W1 };
	WHITE = (texpair){ &rt_white_1x1, white };

	LOG("BEGIN compile (GlBackend's 5 programs, SHADER_PAIR mode, compiler FAST/fastmath/fastint = vitaGL defaults)");
	progset ps;
	memset(&ps, 0, sizeof ps);
	g_cls = V_CORE;
	build_progset(&ps, "primary");
	if (!ps.sprite.ok || !ps.copy.ok) {
		LOG("FAIL compile: the sprite or copy program is unusable -- the rest of the test needs them");
		G.ClearColor(0.6f, 0, 0, 1);
		for (int i = 0; i < 60 * 600; i++) { G.BindFramebuffer(GL_FRAMEBUFFER, 0); G.Clear(GL_COLOR_BUFFER_BIT); shepgl_swap(); if (i > 30 && plat_button()) break; }
		plat_exit(1);
		return 1;
	}
	G.Disable(GL_DEPTH_TEST); G.Disable(GL_CULL_FACE); G.Disable(GL_DITHER); G.Disable(GL_STENCIL_TEST);
	G.PixelStorei(GL_UNPACK_ALIGNMENT, 4); G.PixelStorei(GL_PACK_ALIGNMENT, 4);
	errs();
	show_working();
	make_fixtures();
	mem_sample("after fixtures");

	test_orientation(&ps);
	run_correctness(&ps);
	mem_sample("after correctness");
	test_perf(&ps);
#if GLTEST_HAVE_STYLIZER
	if (ps.sty1.ok && ps.sdf.ok) test_menu(&ps); else skip("menu-frame", "a program failed");
#else
	skip("menu-frame", "the Main-Menu-shaped frame needs the owner-local stylizer (engine/Derived), not built in");
#endif
	mem_sample("after menu frame");

	/* alternatives the glue could pick, ALT verdicts (do not change the main verdict) */
	show_working();
	{
		LOG("BEGIN alt-strict (same suite, compiler SHARK_OPT_DEFAULT, fastmath 0, fastprecision 0, fastint 0)");
		shepgl_set_compiler(2, 0, 0, 0);
		progset st;
		memset(&st, 0, sizeof st);
		g_cls = V_ALT;
		build_progset(&st, "strict");
		run_correctness(&st);
		shepgl_set_compiler(cfg.opt, cfg.fastmath, cfg.fastprecision, cfg.fastint);
		LOG("BEGIN alt-postponed (same suite, VGL_MODE_POSTPONED = vitaGL's default mode: the compile happens in glLinkProgram)");
		shepgl_set_semantic_mode(SHEPGL_SEMA_POSTPONED);
		progset pp;
		memset(&pp, 0, sizeof pp);
		build_progset(&pp, "postponed");
		run_correctness(&pp);
		shepgl_set_semantic_mode(cfg.semantic_mode);
		g_cls = V_CORE;
		g_tag = "primary";
	}
	test_mediump(&ps);
	test_candidates(&ps);

	/* the real display: present + read framebuffer 0 back (INFO: the owner's eyes are the check -- the white bar must be at the TOP) */
	{
		g_tag = "primary";
		float c[4][4];
		solid4(c, 1, 1, 1, 1);
		begin(fA, 0, 0, 0, 1);
		quad(white, BL_NONE, &ps.sprite, 0, 0, SW, 24, 0, 0, 1, 1, c, NOCLIP);
		present(&ps, fA);
		img d = img_new(SW, SH);
		readback_fb(display_fbo(), SW, SH, &d, 0);
		shepgl_swap();
		static const uint8_t WH[4] = { 255, 255, 255, 255 };
		int glconv = rows_are(&d, SH - 24, SH, WH, 2), topfirst = rows_are(&d, 0, 24, WH, 2);
		char dd[300];
		snprintf(dd, sizeof dd, "white bar drawn at target rows 0..23 reads back from the display at %s",
		         glconv ? "the last rows (GL: bottom-up default framebuffer = bar at the top of the screen)" : topfirst ? "the first rows (display readback is top-first)" : "neither end");
		verdict(V_INFO, glconv ? L_PASS : L_WARN, "present.display-readback", dd);
		img_free(&d);
	}
	mem_sample("end");
	char pk[400];
	int o = 0;
	for (int i = 0; i < 5; i++) o += snprintf(pk + o, sizeof pk - o, "%s%s %.1f", i ? ", " : "", shepgl_pool_name(i), (g_memmin.total[i] - g_memmin.free_[i]) / MiB);
	LOG("VGL pools peak used (total - lowest free seen) MiB: %s", pk);
	int ok = n_fail == 0;
	LOG("SUMMARY pass=%d warn=%d fail=%d skip=%d | alt pass=%d fail=%d | candidates pass=%d fail=%d | info diffs=%d | readback_flipped=%d | compile ms sprite %.0f+%.0f+%.0f sty2 %.0f+%.0f+%.0f | menu frame serialized %.2f ms, pipelined %.2f ms (%.1f fps)",
	    n_pass, n_warn, n_fail, n_skip, n_alt_pass, n_alt_fail, n_cand_pass, n_cand_fail, n_info_diff, g_rb_flip, ps.sprite.vs_ms, ps.sprite.fs_ms, ps.sprite.link_ms,
	    ps.sty2.vs_ms, ps.sty2.fs_ms, ps.sty2.link_ms, g_menu_serial, g_menu_pipe, g_menu_pipe > 0 ? 1000.0 / g_menu_pipe : 0);
	LOG("DONE %s -- send %s back. The screen is now %s with a WHITE BAR that must be at the TOP edge (%d small squares = FAIL lines). Press X, O or START to exit.",
	    ok ? "PASS" : "FAIL", plat_log_path(), ok ? "GREEN" : "RED", n_fail > 40 ? 40 : n_fail);
	final_screen(&ps, ok);
	plat_exit(n_fail);
	return n_fail ? 1 : 0;
}
