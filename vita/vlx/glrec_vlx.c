/* vita/vlx/glrec_vlx.c (kit 3): a RECORDING stand-in for vita/gltest/shepgl + vitaGL, so the GL menu hosts (SHPD00008 /
 * 00009 / 00091 / 00096) run their managed side under vlx AND leave proof of what they handed the GPU. Derived from the kit #2
 * stand-in (kit2fix/vlxgl/fakegl_vlx.c: no-op GL with plausible answers); in addition every GL call is folded into a 64-bit FNV-1a
 * hash -- the call, every argument, and every byte it hands over (texture pixels / DXT blocks, vertex / index / uniform data, shader
 * sources, attribute and uniform names) -- and each shepgl_swap (a frame handed to the display) writes one line to
 * ux0:data/shepherd/runtime/logs/glrec.txt:
 *   <swap> n=<calls> d=<draws> idx=<indices drawn> up=<uploads> h=<frame hash> | thr=<thread switches> tid=<last caller> n2=.. h2=..
 * Two runs whose lines agree left of '|' made the same GL calls with the same data in every frame: the same GPU input, so the same
 * pictures. n2 / h2 (kit #5, informational, right of '|') count and hash the same calls WITHOUT the ones that give a shader object its
 * program -- glShaderSource / glCompileShader / glGetShaderiv / glGetShaderInfoLog and the GXP cache's shepgl_gxp_load -- so a start
 * that loads its shaders from the cache (vita/notes/kit5-boot.md) can be compared with one that compiled them: equal h2 in a frame =
 * the same GL input apart from how the shaders got their programs. The GXP cache's stand-in "binary" is the shader's GLSL text.
 * (thr / tid are informational: the multicore pipeline moves the calls to the render thread.) Queries answer as kit #2's did.
 * Kit #11 gpu2 review (opt-in, VLX_GLREC_CANON=1 in the app's env file): h3 (right of '|') hashes the same calls and data as h but names
 * every GL object by WHAT it is instead of the number the stand-in's single counter gave it -- a texture by its first image (size, format,
 * the bytes or "no data") and how many textures got that image before it, a framebuffer by its attachment, a buffer by its first data, a
 * shader by its text, a program by its shaders and attribute bindings (+ the same count), a uniform location by its program and name; an
 * object not yet given its content counts as (its kind, frame, n-th name of that kind made in that frame). So a row that makes a few objects of its own (a new
 * program, two targets) moves h / h2 of every later frame -- every later name is renumbered -- but h3 only where its own objects are made
 * or used. Off (the default): no h3, no allocation, every line and record as before.
 * Nothing here models vitaGL's memory (pools, newlib use). Never in a VPK. Build + link (vita/vlx/README.md "GL apps"):
 *   arm-vita-eabi-gcc -O2 -I vita/gltest/shepgl -c vita/vlx/glrec_vlx.c -o $VLX_OBJ/glrec_vlx.o
 *   VLX_EXTRA_OBJS=$VLX_OBJ/glrec_vlx.o [VLX_AOT_EXTRA="Shepherd.Engine Shepherd.Engine.Derived"] vita/vlx/link.sh menu <work>/obj/host.o <out.elf> */
#include <vitaGL.h>
#include <psp2/kernel/threadmgr.h>
#include <psp2/kernel/sysmem.h>
#include <psp2/kernel/clib.h>
#include <psp2/io/fcntl.h>
#include <psp2/ctrl.h>
#include <string.h>
#include <stdio.h>
#include <stdint.h>
#include <stdlib.h>
#include "shepgl.h"

/* ---------------- the recorder ---------------- */
static uint64_t g_h = 1469598103934665603ull, g_total = 1469598103934665603ull, g_h2 = 1469598103934665603ull;
static unsigned g_calls, g_draws, g_uploads, g_thr, g_calls2;
static int g_excl;   /* the current call gives a shader object its program: not in n2 / h2 */
static unsigned long long g_idx;
static SceUID g_last_tid;
static int g_canon;      /* kit #11 gpu2 review: VLX_GLREC_CANON=1 -> h3 (1 = on, 2 = on but out of memory: "h3=none") */
static int g_noh3;       /* the bytes being mixed are a name's / location's own number: h3 takes its canonical id instead */
static uint64_t g_h3 = 1469598103934665603ull;
static void mix(const void *p, size_t n)
{
	const unsigned char *b = (const unsigned char *)p;
	uint64_t h = g_h;
	for (size_t i = 0; i < n; i++) { h ^= b[i]; h *= 1099511628211ull; }
	g_h = h;
	if (!g_excl) { h = g_h2; for (size_t i = 0; i < n; i++) { h ^= b[i]; h *= 1099511628211ull; } g_h2 = h; }
	if (g_canon == 1 && !g_noh3) { h = g_h3; for (size_t i = 0; i < n; i++) { h ^= b[i]; h *= 1099511628211ull; } g_h3 = h; }
}
static void w32(uint32_t v) { mix(&v, 4); }
static void wf(float f) { uint32_t v; memcpy(&v, &f, 4); w32(v); }
static void call(uint32_t id)
{
	SceUID t = sceKernelGetThreadId();
	if (t != g_last_tid) { if (g_last_tid) g_thr++; g_last_tid = t; }
	g_calls++;
	g_excl = id == 33 || id == 34 || id == 35 || id == 36 || id == 60;
	if (!g_excl) g_calls2++;
	w32(0xC0DE0000u | id);
}
static void wstr(const char *s) { if (!s) { w32(0xFFFFFFFFu); return; } size_t n = strlen(s); w32((uint32_t)n); mix(s, n); }
static unsigned g_swaps;
#include "vramcheck.inc.c"   /* kit #13 fix: VLX_VRAMCHECK=1, the checked model of vitaGL's VRAM pool (off: no call does anything) */
/* ---------------- kit #11 gpu2 review: the canonical object ids of h3 (VLX_GLREC_CANON=1; see the header) ---------------- */
#define CID_PROV 0x8000000000000000ull   /* a name not yet given its content: (kind, frame made, n-th name of that kind made in that frame) */
static uint64_t *g_cid; static unsigned g_ncid;     /* per GL name: 0 = unknown (made before shepgl_init), CID_PROV|.., or its id */
static uint64_t *g_lcid; static unsigned g_nlcid;   /* per uniform location (the stand-in's counter): (program id, uniform name) */
enum { K_TEX, K_FB, K_BUF, K_SH, K_PG, K_VAO, K_RB, K_N };   /* (K_RB: kit #12 boot, the stencil renderbuffer) */
static unsigned g_born[K_N];   /* per kind: so a row's extra program does not renumber the buffers made after it (they are often bound before their first data) */
typedef struct { uint64_t k; uint32_t n; } dupe;
static dupe *g_dup; static unsigned g_capdup, g_ndup;   /* a content id -> how many objects were given it so far */
typedef struct { GLuint p; uint64_t acc; } pacc_t;
static pacc_t *g_pacc;   /* 256 slots (made on first use): a program's shaders + attribute bindings until it links */
enum { TAG_TX = 0x7478, TAG_CX = 0x6378, TAG_FB = 0x6662, TAG_BF = 0x6266, TAG_SH = 0x7368, TAG_PG = 0x7067, TAG_UL = 0x756c };
static uint64_t fnv(uint64_t h, const void *p, size_t n) { const unsigned char *b = (const unsigned char *)p; for (size_t i = 0; i < n; i++) { h ^= b[i]; h *= 1099511628211ull; } return h; }
static uint64_t fnv64(uint64_t h, uint64_t v) { return fnv(h, &v, 8); }
static uint64_t fnvs(uint64_t h, const char *s) { if (!s) return fnv64(h, ~0ull); size_t n = strlen(s); return fnv(fnv64(h, n), s, n); }
static uint64_t tag(unsigned t) { return fnv64(1469598103934665603ull, t); }
static int grow64(uint64_t **a, unsigned *n, unsigned need)
{
	if (need < *n) return 1;
	unsigned m = *n ? *n : 4096; while (m <= need) m *= 2;
	uint64_t *b = (uint64_t *)realloc(*a, (size_t)m * 8);
	if (!b) { g_canon = 2; return 0; }
	memset(b + *n, 0, (size_t)(m - *n) * 8); *a = b; *n = m; return 1;
}
static uint64_t dedup(uint64_t base)
{   /* the n-th object given this content: (content, n) -- an object of a row's own does not renumber the others' */
	base |= 1;
	if (g_ndup * 2 >= g_capdup) {
		unsigned m = g_capdup ? g_capdup * 2 : 4096; dupe *nb = (dupe *)calloc(m, sizeof *nb);
		if (!nb) { g_canon = 2; return base; }
		for (unsigned i = 0; i < g_capdup; i++) if (g_dup[i].k) {
			unsigned j = (unsigned)((g_dup[i].k * 0x9E3779B97F4A7C15ull) >> 40) & (m - 1);
			while (nb[j].k) j = (j + 1) & (m - 1);
			nb[j] = g_dup[i];
		}
		free(g_dup); g_dup = nb; g_capdup = m;
	}
	unsigned j = (unsigned)((base * 0x9E3779B97F4A7C15ull) >> 40) & (g_capdup - 1);
	while (g_dup[j].k && g_dup[j].k != base) j = (j + 1) & (g_capdup - 1);
	if (!g_dup[j].k) { g_dup[j].k = base; g_dup[j].n = 0; g_ndup++; }
	return (fnv64(base, g_dup[j].n++) & ~CID_PROV) | 1;
}
static uint64_t cid_of(GLuint id) { return !id ? 0 : id < g_ncid && g_cid[id] ? g_cid[id] : 0xA000000000000000ull ^ id; }
static void born(GLuint id, unsigned kind)
{
	if (g_canon != 1 || !grow64(&g_cid, &g_ncid, id)) return;
	g_cid[id] = CID_PROV | (uint64_t)kind << 56 | (uint64_t)(g_swaps & 0xFFFFFFFu) << 24 | (g_born[kind]++ & 0xFFFFFFu);
}
static void define(GLuint id, uint64_t content, int counted)
{   /* the object's content: its id from now on (the first content stays: a texture drawn into or re-uploaded keeps its id) */
	if (g_canon != 1 || !id || id >= g_ncid || !(g_cid[id] & CID_PROV)) return;
	g_cid[id] = counted ? dedup(content) : (content & ~CID_PROV) | 1;
}
static uint64_t lcid_of(GLint r) { return r >= 0 && (unsigned)r < g_nlcid && g_lcid[r] ? g_lcid[r] : 0xB000000000000000ull ^ (uint32_t)r; }
static void loc_set(GLint r, GLuint prog, const char *name)
{
	if (g_canon != 1 || r < 0 || !grow64(&g_lcid, &g_nlcid, (unsigned)r)) return;
	g_lcid[r] = (fnvs(fnv64(tag(TAG_UL), cid_of(prog)), name) & ~CID_PROV) | 1;
}
static uint64_t *pacc(GLuint p)
{
	if (!g_pacc && !(g_pacc = (pacc_t *)calloc(256, sizeof *g_pacc))) { g_canon = 2; return NULL; }
	for (int i = 0; i < 256; i++) if (g_pacc[i].p == p) return &g_pacc[i].acc;
	for (int i = 0; i < 256; i++) if (!g_pacc[i].p) { g_pacc[i].p = p; g_pacc[i].acc = tag(TAG_PG); return &g_pacc[i].acc; }
	return NULL;
}
static void w3(uint64_t v) { if (g_canon == 1) g_h3 = fnv64(g_h3, v); }
static void canon_frame(void) { memset(g_born, 0, sizeof g_born); }
static void flush_frame(void)
{
	char line[240], h3[32] = "";
	if (g_canon == 1) snprintf(h3, sizeof h3, " h3=%016llx", (unsigned long long)g_h3);
	else if (g_canon) snprintf(h3, sizeof h3, " h3=none");
	int n = snprintf(line, sizeof line, "%u n=%u d=%u idx=%llu up=%u h=%016llx | thr=%u tid=0x%x n2=%u h2=%016llx%s\n", g_swaps, g_calls, g_draws, g_idx, g_uploads,
	                 (unsigned long long)g_h, g_thr, (unsigned)g_last_tid, g_calls2, (unsigned long long)g_h2, h3);
	SceUID fd = sceIoOpen("ux0:data/shepherd/runtime/logs/glrec.txt", SCE_O_WRONLY | SCE_O_CREAT | SCE_O_APPEND, 0777);
	if (fd >= 0) { sceIoWrite(fd, line, n); sceIoClose(fd); }
	g_total ^= g_h; g_total *= 1099511628211ull;
	g_h = g_h2 = g_h3 = 1469598103934665603ull; g_calls = g_draws = g_uploads = g_thr = g_calls2 = 0; g_idx = 0;
	canon_frame();
}
static int bpp_of(GLenum fmt) { return fmt == GL_RGBA ? 4 : fmt == GL_RGB ? 3 : fmt == GL_LUMINANCE_ALPHA ? 2 : 1; }

/* ---------------- GL ---------------- */
static GLuint g_next_id = 1;
static GLint g_next_loc = 0;
static GLint g_vp[4] = { 0, 0, 960, 544 };
static GLuint g_fbo;
static GLint g_unpack = 4;

const GLubyte *glGetString(GLenum name)
{
	call(0); w32(name);
	switch (name) {
	case GL_VENDOR: return (const GLubyte *)"vlx-rec";
	case GL_RENDERER: return (const GLubyte *)"vlx-rec (no GPU)";
	case GL_VERSION: return (const GLubyte *)"OpenGL ES 2.0 vlx-rec";
	case GL_SHADING_LANGUAGE_VERSION: return (const GLubyte *)"OpenGL ES GLSL ES 1.00";
	case GL_EXTENSIONS: return (const GLubyte *)"GL_EXT_texture_compression_s3tc GL_OES_vertex_array_object GL_OES_framebuffer_object";
	}
	return (const GLubyte *)"";
}
void glGetIntegerv(GLenum pname, GLint *data)
{
	call(1); w32(pname);
	if (!data) return;
	switch (pname) {
	case GL_MAX_TEXTURE_SIZE: *data = 4096; break;
	case GL_VIEWPORT: memcpy(data, g_vp, sizeof g_vp); break;
	case GL_FRAMEBUFFER_BINDING: *data = (GLint)g_fbo; break;
	default: *data = 0; break;
	}
}
GLenum glGetError(void) { call(2); return GL_NO_ERROR; }
void glViewport(GLint x, GLint y, GLsizei w, GLsizei h) { call(3); w32(x); w32(y); w32(w); w32(h); g_vp[0] = x; g_vp[1] = y; g_vp[2] = w; g_vp[3] = h; }
void glScissor(GLint x, GLint y, GLsizei w, GLsizei h) { call(4); w32(x); w32(y); w32(w); w32(h); }
void glClearColor(GLfloat r, GLfloat g, GLfloat b, GLfloat a) { call(5); wf(r); wf(g); wf(b); wf(a); }
void glClear(GLbitfield mask) { call(6); w32(mask); vc_gl_clear (g_fbo); }
void glEnable(GLenum cap) { call(7); w32(cap); }
void glDisable(GLenum cap) { call(8); w32(cap); }
void glBlendFuncSeparate(GLenum a, GLenum b, GLenum c, GLenum d) { call(9); w32(a); w32(b); w32(c); w32(d); }
static void wid(GLuint id);
static void gen(GLsizei n, GLuint *out, unsigned kind) { for (GLsizei i = 0; i < n && out; i++) { out[i] = g_next_id++; born(out[i], kind); wid(out[i]); } }
void glGenTextures(GLsizei n, GLuint *t) { call(10); w32(n); gen(n, t, K_TEX); }
/* kit #8 boot track (the engine's real video): a texture whose memory the hardware decoder writes (shepgl_tex_data) gets a stand-in buffer
 * here (2 MiB of the modelled CDRAM: 960 x 544 RGBA); vlx's decoder stand-in fills each picture with one word naming its access unit, and
 * every draw while such a texture is bound on unit 0 hashes that word too -- so the GL record says WHICH video frame each draw showed. No
 * other draw changes (kit #7's routes draw no such texture). */
#define K8_VTEX 8
static struct { GLuint name; SceUID uid; uint32_t *buf; } g_vtex[K8_VTEX];
static GLuint g_bound[4]; static unsigned g_active_unit;
/* every name ever made a video texture (shepgl_tex_data), in order: the record hashes GL object names through canon() so a video's three
 * textures (whose names the stand-in's single counter hands out like any other) do not renumber every texture / framebuffer / buffer /
 * shader made after them -- a later object's name is hashed as if the video textures had never taken a number, a video texture's as
 * 0x7E000000 + its index. Without video textures canon() is the identity (kit #7's records are unchanged). */
static GLuint g_vnames[64]; static unsigned g_nvnames;
static uint32_t canon(GLuint id)
{
	if (!id || !g_nvnames) return id;
	uint32_t below = 0;
	for (unsigned i = 0; i < g_nvnames; i++) { if (g_vnames[i] == id) return 0x7E000000u + i; if (g_vnames[i] < id) below++; }
	return id - below;
}
static void wid(GLuint id) { g_noh3 = 1; w32(canon(id)); g_noh3 = 0; w3(cid_of(id)); }   /* (h3: the object's canonical id, kit #11 gpu2 review) */
static void wloc(GLint r) { g_noh3 = 1; w32((uint32_t)r); g_noh3 = 0; w3(lcid_of(r)); }
static void vtex_forget(GLuint name)
{
	for (int i = 0; i < K8_VTEX; i++) if (g_vtex[i].name == name && name) { sceKernelFreeMemBlock(g_vtex[i].uid); memset(&g_vtex[i], 0, sizeof g_vtex[i]); }
}
static uint32_t *vtex_buf(GLuint name) { for (int i = 0; i < K8_VTEX; i++) if (g_vtex[i].name == name && name) return g_vtex[i].buf; return NULL; }
static int g_gllog = -1;
static int gllog(void) { if (g_gllog < 0) g_gllog = getenv("VLX_GLLOG") != NULL; return g_gllog; }
/* kit #13 fix: VLX_GLREC_NODEL=1 in the app's env file leaves glDeleteTextures / glDeleteFramebuffers out of the record: two runs that differ only by WHEN a texture is
 * deleted then record the same frames (SHEPHERD_K13_TARGETHOLD moves those calls and nothing else; the gate that says so). Off (the default): as before. */
static int g_nodel = -1;
static int nodel(void) { if (g_nodel < 0) g_nodel = getenv("VLX_GLREC_NODEL") != NULL; return g_nodel; }
void glDeleteTextures(GLsizei n, const GLuint *t) { if (nodel()) { for (GLsizei i = 0; i < n && t; i++) { vtex_forget(t[i]); vc_gl_del_tex(t[i]); } return; } call(11); w32(n); for (GLsizei i = 0; i < n && t; i++) { wid(t[i]); vtex_forget(t[i]); vc_gl_del_tex(t[i]); if (gllog()) sceClibPrintf("[gllog] D f%u t%x id%u\n", g_swaps, (unsigned)sceKernelGetThreadId(), t[i]); } }
void glBindTexture(GLenum target, GLuint texture) { call(12); w32(target); wid(texture); if (target == GL_TEXTURE_2D) g_bound[g_active_unit & 3] = texture; }
void glActiveTexture(GLenum texture) { call(13); w32(texture); g_active_unit = (unsigned)(texture - GL_TEXTURE0) & 3; }
void glTexImage2D(GLenum target, GLint level, GLint ifmt, GLsizei w, GLsizei h, GLint border, GLenum fmt, GLenum type, const GLvoid *data)
{
	call(14); w32(target); w32(level); w32(ifmt); w32(w); w32(h); w32(border); w32(fmt); w32(type); w32(data != NULL);
	if (gllog()) sceClibPrintf("[gllog] T f%u t%x id%u %dx%d fmt %x h %08x data %d\n", g_swaps, (unsigned)sceKernelGetThreadId(), g_bound[g_active_unit & 3], w, h, fmt, data && w * h * 4 > 0 ? (unsigned)fnv(1469598103934665603ull, data, (size_t)w * h * (bpp_of(fmt))) : 0, data != NULL);
	if (level == 0) vc_gl_tex_image(g_bound[g_active_unit & 3], w, h, bpp_of(fmt), 0);
	uint64_t c = g_canon == 1 && level == 0 ? fnv64(fnv64(fnv64(fnv64(fnv64(tag(TAG_TX), (uint32_t)ifmt), (uint32_t)w), (uint32_t)h), fmt), type) : 0;
	if (data && w > 0 && h > 0) {
		size_t row = (size_t)w * bpp_of(fmt), stride = (row + g_unpack - 1) / g_unpack * g_unpack;
		for (GLsizei y = 0; y < h; y++) { mix((const char *)data + (size_t)y * stride, row); if (c) c = fnv(c, (const char *)data + (size_t)y * stride, row); }
	}
	if (c) define(g_bound[g_active_unit & 3], fnv64(c, data != NULL), 1);
	g_uploads++;
}
void glCompressedTexImage2D(GLenum target, GLint level, GLenum ifmt, GLsizei w, GLsizei h, GLint border, GLsizei size, const void *data)
{
	call(15); w32(target); w32(level); w32(ifmt); w32(w); w32(h); w32(border); w32(size); if (data && size > 0) mix(data, (size_t)size);
	if (level == 0) vc_gl_tex_image(g_bound[g_active_unit & 3], w, h, 0, (unsigned)size);
	if (gllog()) { sceClibPrintf("[gllog] C f%u t%x id%u %dx%d fmt %x size %d\n", g_swaps, (unsigned)sceKernelGetThreadId(), g_bound[g_active_unit & 3], w, h, ifmt, size);
		if (w <= 64 && h <= 64 && data) { char l[100]; const unsigned char *d = data; for (int i = 0; i < size; i += 32) { int n = 0; for (int k = 0; k < 32 && i + k < size; k++) n += snprintf(l + n, sizeof l - n, "%02x", d[i + k]); sceClibPrintf("[gldata] %u %04x %s\n", g_bound[g_active_unit & 3], i, l); } } }
	if (g_canon == 1 && level == 0) define(g_bound[g_active_unit & 3], fnv(fnv64(fnv64(fnv64(fnv64(tag(TAG_CX), ifmt), (uint32_t)w), (uint32_t)h), (uint32_t)size), data, data && size > 0 ? (size_t)size : 0), 1);
	g_uploads++;
}
void glTexParameteri(GLenum target, GLenum pname, GLint param) { call(16); w32(target); w32(pname); w32(param); }
void glPixelStorei(GLenum pname, GLint param) { call(17); w32(pname); w32(param); if (pname == GL_UNPACK_ALIGNMENT && param > 0) g_unpack = param; }
void glReadPixels(GLint x, GLint y, GLsizei w, GLsizei h, GLenum fmt, GLenum type, GLvoid *data)
{ call(18); w32(x); w32(y); w32(w); w32(h); w32(fmt); w32(type); if (data && w > 0 && h > 0) memset(data, 0, (size_t)w * h * 4); }
void glGenFramebuffers(GLsizei n, GLuint *f) { call(19); w32(n); gen(n, f, K_FB); }
void glDeleteFramebuffers(GLsizei n, const GLuint *f) { if (nodel()) { for (GLsizei i = 0; i < n && f; i++) vc_gl_del_fb(f[i]); return; } call(20); w32(n); for (GLsizei i = 0; i < n && f; i++) { wid(f[i]); vc_gl_del_fb(f[i]); if (gllog()) sceClibPrintf("[gllog] DF f%u fb%u\n", g_swaps, f[i]); } }
void glBindFramebuffer(GLenum target, GLuint fb) { call(21); w32(target); wid(fb); g_fbo = fb; if (gllog() && g_swaps < 8) sceClibPrintf("[gllog] BF f%u fb%u\n", g_swaps, fb); }
void glFramebufferTexture2D(GLenum target, GLenum att, GLenum tt, GLuint tex, GLint level) { call(22); w32(target); w32(att); w32(tt); wid(tex); w32(level); define(g_fbo, fnv64(fnv64(tag(TAG_FB), att), cid_of(tex)), 1); vc_gl_attach(g_fbo, tex); if (gllog()) sceClibPrintf("[gllog] FT f%u fb%u tex%u\n", g_swaps, g_fbo, tex); }
GLenum glCheckFramebufferStatus(GLenum target) { call(23); w32(target); return GL_FRAMEBUFFER_COMPLETE; }
void glGenBuffers(GLsizei n, GLuint *b) { call(24); w32(n); gen(n, b, K_BUF); }
void glDeleteBuffers(GLsizei n, const GLuint *b) { call(25); w32(n); for (GLsizei i = 0; i < n && b; i++) { wid(b[i]); vc_gl_del_buffer(b[i]); } }
static GLuint g_bbuf[2];   /* (h3) the buffers bound to GL_ARRAY_BUFFER / GL_ELEMENT_ARRAY_BUFFER */
void glBindBuffer(GLenum target, GLuint buffer) { call(26); w32(target); wid(buffer); g_bbuf[target == GL_ELEMENT_ARRAY_BUFFER] = buffer; }
void glBufferData(GLenum target, GLsizei size, const GLvoid *data, GLenum usage)
{
	call(27); w32(target); w32(size); w32(usage); w32(data != NULL); if (data && size > 0) mix(data, (size_t)size);
	vc_gl_buffer_data(g_bbuf[target == GL_ELEMENT_ARRAY_BUFFER], size);
	if (gllog() && (usage == GL_STATIC_DRAW || size >= 0x400)) sceClibPrintf("[gllog] B f%u t%x %s size %x usage %x\n", g_swaps, (unsigned)sceKernelGetThreadId(), target == GL_ELEMENT_ARRAY_BUFFER ? "ibo" : "vbo", size, usage);
	if (g_canon == 1) define(g_bbuf[target == GL_ELEMENT_ARRAY_BUFFER], fnv(fnv64(fnv64(fnv64(fnv64(tag(TAG_BF), target), (uint32_t)size), usage), data != NULL), data, data && size > 0 ? (size_t)size : 0), 1);
}
void glVertexAttribPointer(GLuint index, GLint size, GLenum type, GLboolean norm, GLsizei stride, const void *ptr)
{ call(28); w32(index); w32(size); w32(type); w32(norm); w32(stride); w32((uint32_t)(uintptr_t)ptr); vc_gl_attr(index, g_bbuf[0]); }
void glEnableVertexAttribArray(GLuint index) { call(29); w32(index); }
void glDrawElements(GLenum mode, GLsizei count, GLenum type, const GLvoid *indices)
{
	call(30); w32(mode); w32(count); w32(type); w32((uint32_t)(uintptr_t)indices); g_draws++; g_idx += (unsigned)count;
	vc_gl_draw(g_fbo, g_bound, g_bbuf[1]);
	if (gllog() && g_swaps < 8) sceClibPrintf("[gllog] DR f%u fb%u tex%u n%d\n", g_swaps, g_fbo, g_bound[0], count);
	uint32_t *vb = vtex_buf(g_bound[0]);
	if (vb) { w32(0x71DE0000u); w32(vb[0]); }   /* kit #8: the video frame this draw shows (see g_vtex) */
}
/* the shader objects' types and texts, for the GXP cache's stand-in (its "binary" of a shader is the text it was given) */
typedef struct { GLuint id; GLenum type; char *src; size_t n; } shobj;
#define NSH 256
static shobj g_sh[NSH];
static shobj *sh_find(GLuint id) { for (int i = 0; i < NSH; i++) if (g_sh[i].id == id && id) return &g_sh[i]; return NULL; }
static void sh_set(shobj *o, const char *src, size_t n) { free(o->src); o->src = (char *)malloc(n + 1); if (o->src) { memcpy(o->src, src, n); o->src[n] = 0; o->n = n; } else o->n = 0; }
GLuint glCreateShader(GLenum t)
{
	call(31); w32(t); GLuint r = g_next_id++; born(r, K_SH); wid(r);
	shobj *o = NULL; for (int i = 0; i < NSH && !o; i++) if (!g_sh[i].id) o = &g_sh[i];
	if (o) { o->id = r; o->type = t; o->src = NULL; o->n = 0; }
	return r;
}
void glDeleteShader(GLuint s) { call(32); wid(s); shobj *o = sh_find(s); if (o) { free(o->src); memset(o, 0, sizeof *o); } }
void glShaderSource(GLuint h, GLsizei count, const GLchar *const *string, const GLint *length)
{
	call(33); wid(h); w32(count);
	shobj *o = sh_find(h); size_t tot = 0; char *all = NULL;
	for (GLsizei i = 0; i < count && string; i++) {
		size_t n = length && length[i] >= 0 ? (size_t)length[i] : strlen(string[i]);
		w32((uint32_t)n); mix(string[i], n);
		char *g = (char *)realloc(all, tot + n + 1); if (g) { all = g; memcpy(all + tot, string[i], n); tot += n; }
	}
	if (o && all) sh_set(o, all, tot);
	if (g_canon == 1) define(h, fnv(fnv64(tag(TAG_SH), o ? o->type : 0), all, all ? tot : 0), 0);   /* (h3) a shader by its text */
	free(all);
}
void glCompileShader(GLuint s) { call(34); wid(s); }
void glGetShaderiv(GLuint h, GLenum pname, GLint *p) { call(35); wid(h); w32(pname); if (p) *p = pname == GL_COMPILE_STATUS ? GL_TRUE : 0; }
void glGetShaderInfoLog(GLuint h, GLsizei max, GLsizei *len, GLchar *log) { call(36); wid(h); w32(max); if (len) *len = 0; if (log && max > 0) log[0] = 0; }
GLuint glCreateProgram(void) { call(37); GLuint r = g_next_id++; born(r, K_PG); wid(r); return r; }
void glAttachShader(GLuint p, GLuint s) { call(38); wid(p); wid(s); uint64_t *a = g_canon == 1 ? pacc(p) : NULL; if (a) *a = fnv64(*a, cid_of(s)); }
void glBindAttribLocation(GLuint p, GLuint index, const GLchar *name) { call(39); wid(p); w32(index); wstr(name); uint64_t *a = g_canon == 1 ? pacc(p) : NULL; if (a) *a = fnvs(fnv64(*a, index), name); }
void glLinkProgram(GLuint p)
{
	call(40); wid(p);
	uint64_t *a = g_canon == 1 ? pacc(p) : NULL;   /* (h3) a program by its shaders and bindings, counted */
	if (a) { define(p, *a, 1); for (int i = 0; i < 256; i++) if (g_pacc[i].p == p) g_pacc[i].p = 0; }
}
void glGetProgramiv(GLuint p, GLenum pname, GLint *v) { call(41); wid(p); w32(pname); if (v) *v = pname == GL_LINK_STATUS ? GL_TRUE : 0; }
void glGetProgramInfoLog(GLuint p, GLsizei max, GLsizei *len, GLchar *log) { call(42); wid(p); w32(max); if (len) *len = 0; if (log && max > 0) log[0] = 0; }
void glUseProgram(GLuint program) { call(43); wid(program); }
GLint glGetUniformLocation(GLuint prog, const GLchar *name) { call(44); wid(prog); wstr(name); GLint r = g_next_loc++; loc_set(r, prog, name); wloc(r); return r; }
void glUniform1i(GLint location, GLint v0) { call(45); wloc(location); w32((uint32_t)v0); }
void glUniform4f(GLint location, GLfloat a, GLfloat b, GLfloat c, GLfloat d) { call(46); wloc(location); wf(a); wf(b); wf(c); wf(d); }
void glFinish(void) { call(47); vc_gl_finish(); if (gllog()) sceClibPrintf("[gllog] FIN f%u\n", g_swaps); }
void glFlush(void) { call(48); }
void glGenVertexArrays(GLsizei n, GLuint *res) { call(49); w32(n); gen(n, res, K_VAO); }
void glBindVertexArray(GLuint array) { call(50); wid(array); }
/* kit #12 boot: the stencil entry points (Gl.cs 51..58, Render/K12Stencil.cs; called only while SHEPHERD_K12_STENCIL=1 draws a uGUI Mask):
 * recorded like the others -- the call, every argument; a renderbuffer gets a name from the same counter (born as K_RB for h3) */
void glStencilFunc(GLenum func, GLint ref, GLuint mask) { call(51); w32(func); w32((uint32_t)ref); w32(mask); }
void glStencilOp(GLenum sfail, GLenum dpfail, GLenum dppass) { call(52); w32(sfail); w32(dpfail); w32(dppass); }
void glStencilMask(GLuint mask) { call(53); w32(mask); }
void glColorMask(GLboolean r, GLboolean g, GLboolean b, GLboolean a) { call(54); w32(r); w32(g); w32(b); w32(a); }
void glGenRenderbuffers(GLsizei n, GLuint *rb) { call(55); w32(n); gen(n, rb, K_RB); }
void glBindRenderbuffer(GLenum target, GLuint rb) { call(56); w32(target); wid(rb); }
void glRenderbufferStorage(GLenum target, GLenum ifmt, GLsizei w, GLsizei h) { call(57); w32(target); w32(ifmt); w32(w); w32(h); }
void glFramebufferRenderbuffer(GLenum target, GLenum att, GLenum rbtarget, GLuint rb) { call(58); w32(target); w32(att); w32(rbtarget); wid(rb); }

/* ---------------- shepgl (same API as shepgl_vita.c) ---------------- */
typedef struct { const char *name; void *fn; unsigned char wrapped; } entry;
#define SHEPGL_E(idx, field, sym, how) { #sym, (void *)(sym), 0 },
static const entry k_entries[] = {
#include "shepgl_entries.h"
};
#undef SHEPGL_E
int shepgl_count(void) { return SHEPGL_COUNT; }
const char *shepgl_name(int i) { return i >= 0 && i < SHEPGL_COUNT ? k_entries[i].name : NULL; }
void *shepgl_fn(int i) { return i >= 0 && i < SHEPGL_COUNT ? k_entries[i].fn : NULL; }
int shepgl_is_wrapped(int i) { return 0; }
float shepgl_abi_probe(int32_t i, float a, float b, float c, float d, uint8_t flag)
{
	return (float)i + 2.0f * a + 3.0f * b + 5.0f * c + 7.0f * d + 11.0f * (float)flag;
}
void *shepgl_proc(const char *name)
{
	if (!name) return NULL;
	for (int i = 0; i < SHEPGL_COUNT; i++) if (!strcmp(k_entries[i].name, name)) return k_entries[i].fn;
	if (!strcmp(name, "shepgl_abi_probe")) return (void *)shepgl_abi_probe;
	return NULL;
}
void *shepgl_raw(const char *name) { return NULL; }
static char g_status[768];   /* (kit #9 review: 512 -> 768, the PHYCONT + spill note) */
static int g_state;
const char *shepgl_status(void) { return g_status; }
int shepgl_state(void) { return g_state; }
int shepgl_attach(void) { return g_state != 0; }
int shepgl_compiler_online(void) { return 1; }
const char *shepgl_last_diagnostics(void) { return ""; }
void shepgl_set_log(shepgl_line_fn fn) {}
GLuint shepgl_display_fbo(void) { return 0; }
/* kit #7 measure review (the overlay's CPU / RT / GPU split): VLX_SWAP_DELAY_US=<us> in the app's env file makes each swap wait that long,
 * standing in for the device's swap (vitaGL returns when the GPU and the display are ready: ~15-20 ms at the idle menu), which the
 * recording stand-in otherwise returns at once. Timing only: the calls, the record and the frames are the same. Off unless set. */
static unsigned g_swap_delay_us;
/* kit #10 boot track: VLX_GL_DISPLAY=1 in the app's env file -- each swap also does what vitaGL's display callback does, a
 * sceDisplaySetFrameBuf of the frame (a buffer whose first word is "GLRG": vlx's display record names it), through the host's
 * --wrap=sceDisplaySetFrameBuf when the build has the boot screen (k10_boot.inc.c holds vitaGL's flips off the display while it shows).
 * Not recorded in glrec.txt (display only); off by default (kit #9's display records). */
#include <psp2/display.h>
static int g_gl_display;
static uint32_t g_gl_display_word[16] = { 0x47524c47u };
static void gl_display_flip(void)
{
	SceDisplayFrameBuf f; memset(&f, 0, sizeof f);
	f.size = sizeof f; f.base = g_gl_display_word; f.pitch = 960; f.pixelformat = SCE_DISPLAY_PIXELFORMAT_A8B8G8R8; f.width = 960; f.height = 544;
	sceDisplaySetFrameBuf(&f, SCE_DISPLAY_SETBUF_NEXTFRAME);
}
void shepgl_swap(void)
{
	call(100);
	if (g_gl_display)
		gl_display_flip();
	if (g_swap_delay_us)
		sceKernelDelayThread(g_swap_delay_us);
	vc_swap();
	g_swaps++;
	flush_frame();
	if (g_swaps % 200 == 0)
		sceClibPrintf("[glrec] swaps %u, running hash %016llx\n", g_swaps, (unsigned long long)g_total);
}
unsigned shepgl_swap_count(void) { return g_swaps; }
/* kit #8 boot track (the clean boot screen's black frame, shepgl_vita.c): NOT recorded and not counted -- on the device it presents one
 * cleared frame before the engine starts and leaves no GL state changed, so the engine's record is the same with and without it */
static unsigned g_boot_frames;
int shepgl_boot_frame(void) { g_boot_frames++; vc_boot_frame(); if (g_gl_display) gl_display_flip(); return 1; }
/* kit #8 boot track (the engine's real video): VRAM stands in as the modelled CDRAM (not recorded: the host calls these on the main thread,
 * which makes no GL calls in the MT apps); shepgl_tex_data is a GL-thread call and is recorded (call 121) */
static struct { void *p; SceUID uid; unsigned kib; } g_vram[8];
static unsigned g_vram_held_kib;   /* what shepgl_vram_alloc holds (the pool model below) */
void *shepgl_vram_alloc(unsigned bytes, unsigned align)
{
	for (int i = 0; i < 8; i++) if (!g_vram[i].p) {
		SceKernelAllocMemBlockOpt opt; memset(&opt, 0, sizeof opt); opt.size = sizeof opt;
		if (align > 0x1000) { opt.attr = SCE_KERNEL_ALLOC_MEMBLOCK_ATTR_HAS_ALIGNMENT; opt.alignment = align; }
		SceUID b = sceKernelAllocMemBlock("vlx-vram", SCE_KERNEL_MEMBLOCK_TYPE_USER_CDRAM_RW, (bytes + 0x3FFFFu) & ~0x3FFFFu, align > 0x1000 ? &opt : NULL);
		void *p = NULL;
		if (b < 0 || sceKernelGetMemBlockBase(b, &p) < 0) return NULL;
		g_vram[i].p = p; g_vram[i].uid = b; g_vram[i].kib = (bytes + 1023u) / 1024u; g_vram_held_kib += g_vram[i].kib; vc_host_alloc(p, bytes, align); return p;
	}
	return NULL;
}
void shepgl_vram_free(void *p) { for (int i = 0; i < 8; i++) if (p && g_vram[i].p == p) { sceKernelFreeMemBlock(g_vram[i].uid); g_vram[i].p = NULL; g_vram_held_kib -= g_vram[i].kib; vc_host_free(p); } }
void *shepgl_tex_data(GLuint name)
{
	unsigned known = 0; for (unsigned i = 0; i < g_nvnames; i++) known |= g_vnames[i] == name;
	if (!known && g_nvnames < 64) g_vnames[g_nvnames++] = name;
	call(121); wid(name);
	uint32_t *b = vtex_buf(name);
	if (b) return b;
	for (int i = 0; i < K8_VTEX; i++) if (!g_vtex[i].name) {
		void *p = NULL; SceUID u = sceKernelAllocMemBlock("vlx-vtex", SCE_KERNEL_MEMBLOCK_TYPE_USER_CDRAM_RW, 2u << 20, NULL);
		if (u < 0 || sceKernelGetMemBlockBase(u, &p) < 0) return NULL;
		memset(p, 0, 2u << 20);
		g_vtex[i].name = name; g_vtex[i].uid = u; g_vtex[i].buf = (uint32_t *)p; return p;
	}
	return NULL;
}
void shepgl_set_vsync(int on) {}
void shepgl_set_compiler(int opt, int fastmath, int fastprecision, int fastint) {}
void shepgl_set_semantic_mode(int mode) {}
void shepgl_default_config(shepgl_config *c)
{
	memset(c, 0, sizeof *c);
	c->width = 960; c->height = 544; c->ram_threshold = SHEPGL_DEFAULT_RAM_THRESHOLD; c->vsync = 1;
	c->semantic_mode = SHEPGL_SEMA_SHADER_PAIR; c->opt = 2; c->fastmath = 1; c->fastprecision = 0; c->fastint = 1;
}
/* kit5 mem: VLX_GL_USERMEM=1 in the app's env file models what vitaGL's init takes from the USER memory on the device, so a vlx run's
 * "free user" follows the device's: everything beyond ram_threshold (the libshacccg module + vitaGL's RAM pool: vglInitExtended sizes its
 * RAM pool as the free user memory above the threshold) as one memblock, and vitaGL's garbage-collector thread with a 1 MiB stack
 * (calibration: the device's MEM after-gl line is 1 MiB below the threshold, kit #4 play / play-mt / play-gaot: 25600 KiB for 26 MiB).
 * Only the user-memory take is modelled -- vitaGL's own pools (what fills them, CDRAM / PHYCONT) still are not. Off unless set. */
static int vgl_gc_standin(SceSize args, void *argp) { (void)args; (void)argp; for (;;) sceKernelDelayThread(1000 * 1000); return 0; }
static char g_usermem_note[160];
static void model_vitagl_usermem(const shepgl_config *c)
{
	const char *um = getenv("VLX_GL_USERMEM");
	if (!um || um[0] != '1') return;
	SceKernelFreeMemorySizeInfo fi; memset(&fi, 0, sizeof fi); fi.size = sizeof fi; sceKernelGetFreeMemorySize(&fi);
	unsigned take = fi.size_user > c->ram_threshold ? ((unsigned)fi.size_user - (unsigned)c->ram_threshold) & ~0xfffu : 0;
	SceUID b = take ? sceKernelAllocMemBlock("vlx-vitagl-usermem", SCE_KERNEL_MEMBLOCK_TYPE_USER_RW, take, NULL) : -1;
	SceUID t = sceKernelCreateThread("vlx-vgl-gc-standin", vgl_gc_standin, 0x10000100, 1024 * 1024, 0, 0, NULL);
	if (t >= 0) sceKernelStartThread(t, 0, NULL);
	snprintf(g_usermem_note, sizeof g_usermem_note, "; VLX_GL_USERMEM=1: took %u KiB of user memory (block 0x%x) + a 1 MiB-stack gc thread stand-in (0x%x)",
	         take / 1024, (unsigned)b, (unsigned)t);
}
/* kit #9 device track: vitaGL's init takes EVERY free PHYCONT page (its PHYCONT pool: the device's MEM after-gl line says phycont=0) -- the
 * reason the engine's real video was refused on the owner's Vita (sceVideodecInitLibrary 0x80024302; vlx.c models the library's own 6,144
 * KiB). Modelled as one memblock of all free PHYCONT; VLX_GL_PHYCONT=0 in the app's env file = kit #8's stand-in (no PHYCONT taken). */
/* kit #9 review (finding 1): the VRAM it costs -- vitaGL's two per-frame circular pools took 21,846 KiB of its PHYCONT pool at frame 0 on every
 * kit #8 device log ("phycont 4778/26624" free/total); a pool smaller than that puts one of them (10,923 KiB; both below 10,923) into its
 * VRAM pool (CDRAM) for the whole run. The VRAM pool model below (VLX_VRAM_FREE_KIB=<KiB>) subtracts that spill, so the engine's preload
 * (Runtime/K8Preload.cs: ShepGl.PoolKiB(0)) sees what a SANE reading would show; VLX_GL_SPILL=0 in the env file = no spill modelled.
 * Review round 2 (finding 4): the owner's play apps (00097 / 00098 / 00095 / 00193 / 00194) do NOT read a sane figure -- from CP500 on every
 * kit #8 log says "vram 1733632/98304" (more free than the total) and both preloads said "up to 16 MiB of video memory (1692 MiB free)", so
 * on the device the preload's clamp never limits and it always takes its whole budget; VLX_VRAM_FREE_KIB=device models that reading (the
 * gate of the device's configuration). Neither models the spill's own cost: this stand-in has no texture VRAM pool, so a scene running out
 * of VRAM because of the ~10.7 MiB spill cannot show in any vlx run (the cost is an ESTIMATE, unverified until a device run). */
static char g_phycont_note[200];
static unsigned g_spill_kib;
static void model_vitagl_phycont(void)
{
	const char *pc = getenv("VLX_GL_PHYCONT");
	if (pc && pc[0] == '0') { snprintf(g_phycont_note, sizeof g_phycont_note, "; VLX_GL_PHYCONT=0: no PHYCONT taken (kit #8's stand-in)"); return; }
	SceKernelFreeMemorySizeInfo fi; memset(&fi, 0, sizeof fi); fi.size = sizeof fi; sceKernelGetFreeMemorySize(&fi);
	unsigned take = (unsigned)fi.size_phycont & ~0xfffffu;
	SceUID b = take ? sceKernelAllocMemBlock("vlx-vitagl-phycont", SCE_KERNEL_MEMBLOCK_TYPE_USER_MAIN_PHYCONT_NC_RW, take, NULL) : -1;
	const char *sp = getenv("VLX_GL_SPILL");
	unsigned tk = take / 1024u;
	g_spill_kib = (sp && sp[0] == '0') ? 0 : tk >= 2u * 10923u ? 0 : tk >= 10923u ? 10923u : 2u * 10923u;
	snprintf(g_phycont_note, sizeof g_phycont_note, "; vitaGL's PHYCONT pool modelled: took %u KiB (block 0x%x; VLX_GL_PHYCONT=0: none)%s", tk, (unsigned)b,
	         g_spill_kib ? (g_spill_kib > 10923u ? "; both per-frame circular pools (21,846 KiB) spill to VRAM (VLX_GL_SPILL=0: none)" : "; one per-frame circular pool (10,923 KiB) spills to VRAM (VLX_GL_SPILL=0: none)") : "");
}
int shepgl_init(const shepgl_config *c)
{
	model_vitagl_usermem(c);
	model_vitagl_phycont();
	{
		const char *sd = getenv("VLX_SWAP_DELAY_US");
		g_swap_delay_us = sd && atoi(sd) > 0 ? (unsigned)atoi(sd) : 0;
		const char *gd = getenv("VLX_GL_DISPLAY");   /* kit #10 */
		g_gl_display = gd && gd[0] == '1';
		const char *cn = getenv("VLX_GLREC_CANON");   /* kit #11 gpu2 review: h3 */
		g_canon = cn && cn[0] == '1';
	}
	snprintf(g_status, sizeof g_status, "vlx RECORDING stand-in for vitaGL (no GPU, no pools; every GL call hashed into runtime/logs/glrec.txt): %dx%d, ram_threshold %d MiB (not applied), gc thread affinity 0x%x (not applied)%s%s%s",
	         c->width, c->height, c->ram_threshold >> 20, (unsigned)c->gc_affinity, g_usermem_note, g_phycont_note, g_canon ? "; VLX_GLREC_CANON=1: h3 (objects by content)" : "");
	{   /* kit #8 boot review (failure-path test only): VLX_GL_INIT_FAIL=1 in the app's env file = vitaGL did not come up (shepgl_init 0) */
		const char *f = getenv("VLX_GL_INIT_FAIL");
		if (f && !strcmp(f, "1")) { size_t l = strlen(g_status); snprintf(g_status + l, sizeof g_status - l, "; VLX_GL_INIT_FAIL=1: init failure modelled"); return 0; }
	}
	g_state = 1;
	g_vc_ready = 1; vc_on();   /* kit #13 fix: VLX_VRAMCHECK=1 */
	return 1;
}
int shepgl_has_extension(const char *name) { return name && !strcmp(name, "GL_EXT_texture_compression_s3tc"); }
void shepgl_mem(shepgl_meminfo *m) { memset(m, 0, sizeof *m); }
const char *shepgl_pool_name(int pool)
{
	static const char *const n[5] = { "vram", "ram", "phycont", "cdlg", "newlib" };
	return pool >= 0 && pool < 5 ? n[pool] : "?";
}
/* kit #8 boot track: vitaGL's VRAM pool as the engine's preload sees it (Runtime/K8Preload.cs: ShepGl.PoolKiB(0) gates its store) --
 * VLX_VRAM_FREE_KIB=<KiB> in the app's env file models the pool's free size (e.g. 56564: the owner's PCH-2000 in level0 before the logo,
 * kit #7's device run, play-mt.txt CP500 "vram 56564/98304"), less what shepgl_vram_alloc holds; the total is then the device's 98304.
 * Unset = kit #7's stand-in (0: no pool -- the preload stays off under vlx, and the host's pool line reads 0). Not recorded.
 * Kit #13 fix (vita/notes/kit13-fix.md section 6): "device" is the reading of a pool the GPU corrupted at f3: kit #13 as shipped and SHEPHERD_K13_TARGETHOLD=0. With the hold the device should
 * read sane (ESTIMATE), so its gate is the sane arm: VLX_VRAM_FREE_KIB=model:<KiB> with VLX_VRAMCHECK=1 moves with the checked pool, <KiB> being what the model does not hold (vramcheck.inc.c). */
unsigned shepgl_pool_kib(int pool, int total)
{
	const char *e = pool == 0 ? getenv("VLX_VRAM_FREE_KIB") : NULL;
	if (e && !strcmp(e, "device")) return total ? 98304 : 1733632;   /* kit #9 review round 2 (finding 4): the play apps' reading on the owner's PCH-2000 */
	if (e && !strncmp(e, "model", 5)) {
		static int said;
		if (!vc_on() && !said) { said = 1; sceClibPrintf("VRAMCHECK: VLX_VRAM_FREE_KIB=model without VLX_VRAMCHECK: no pool to read, the reading stays 0 (this run tests no sane reading)\n"); }
		return total ? 98304 : vc_free_kib(e[5] == ':' ? (unsigned)strtoul(e + 6, NULL, 10) : 0);
	}
	unsigned f = e ? (unsigned)strtoul(e, NULL, 10) : 0;
	if (!f) return 0;
	if (total) return 98304;
	f = f > g_spill_kib ? f - g_spill_kib : 0;   /* kit #9 review (finding 1): vitaGL's circular pool(s) that went to VRAM */
	return f > g_vram_held_kib ? f - g_vram_held_kib : 0;
}
int shepgl_shader_binary_size(GLuint shader) { return -1; }

/* GXP cache (kit #5; vita/gltest/shepgl/shepgl.h): the stand-in's "serialized shader" is  u32 0 | "VLXGXP\0\0" | u32 type | u32 n | the
 * GLSL text the shader object was given (the same as the real blob's role: what the shader object's program is made from). Identity,
 * save and check only read (not recorded: no GL input); load is recorded (call 60: the type and the text it hands the shader). */
static char g_gxp_why[160];
const char *shepgl_gxp_why(void) { return g_gxp_why; }
int shepgl_gxp_identity(char *out, int cap, int *ms)
{
	if (ms) *ms = 0;
	g_gxp_why[0] = 0;
	int n = snprintf(out, (size_t)cap, "vlx RECORDING stand-in (no GPU, no compiler): the cached shader 'binary' is its GLSL text | blob: vlx v1");
	return n > 0 && n < cap ? n : -1;
}
int shepgl_gxp_check(const void *buf, int len, int type)
{
	const unsigned char *b = (const unsigned char *)buf; uint32_t z, t, n;
	g_gxp_why[0] = 0;
	if (!buf || len < 20) { snprintf(g_gxp_why, sizeof g_gxp_why, "a blob of %d bytes", len); return 0; }
	memcpy(&z, b, 4); memcpy(&t, b + 12, 4); memcpy(&n, b + 16, 4);
	if (z != 0 || memcmp(b + 4, "VLXGXP\0\0", 8) != 0) { snprintf(g_gxp_why, sizeof g_gxp_why, "no stand-in header"); return 0; }
	if ((int)t != type || n != (uint32_t)len - 20) { snprintf(g_gxp_why, sizeof g_gxp_why, "type %u / length %u do not match (%d, %d)", t, n, type, len - 20); return 0; }
	return 1;
}
int shepgl_gxp_save(GLuint program, GLuint shader, int type, void *buf, int cap)
{
	shobj *o = sh_find(shader);
	g_gxp_why[0] = 0;
	if (!program || !o || !o->src) { snprintf(g_gxp_why, sizeof g_gxp_why, "shader %u has no text", shader); return 0; }
	int len = (int)(20 + o->n);
	if (cap < len) return -len;
	unsigned char *b = (unsigned char *)buf; uint32_t z = 0, t = (uint32_t)type, n = (uint32_t)o->n;
	memcpy(b, &z, 4); memcpy(b + 4, "VLXGXP\0\0", 8); memcpy(b + 12, &t, 4); memcpy(b + 16, &n, 4); memcpy(b + 20, o->src, o->n);
	return len;
}
int shepgl_gxp_load(GLuint shader, int type, const void *buf, int len)
{
	if (!shepgl_gxp_check(buf, len, type)) return 0;
	shobj *o = sh_find(shader);
	if (!o || o->type != (type ? GL_FRAGMENT_SHADER : GL_VERTEX_SHADER)) { snprintf(g_gxp_why, sizeof g_gxp_why, "shader %u is not a fresh %s shader", shader, type ? "fragment" : "vertex"); return 0; }
	call(60); wid(shader); w32((uint32_t)type); w32((uint32_t)(len - 20)); mix((const unsigned char *)buf + 20, (size_t)(len - 20));
	sh_set(o, (const char *)buf + 20, (size_t)(len - 20));
	if (g_canon == 1) define(shader, fnv(fnv64(tag(TAG_SH), o->type), (const unsigned char *)buf + 20, (size_t)(len - 20)), 0);   /* (h3) = its text, as compiled */
	return 1;
}

/* ---------------- imports vlx.c does not answer ---------------- */
int sceCtrlSetSamplingMode(SceCtrlPadInputMode mode) { return 0; }
int sceKernelWaitThreadEndCB(SceUID thid, int *stat, SceUInt *timeout);
int sceKernelWaitThreadEnd(SceUID thid, int *stat, SceUInt *timeout) { return sceKernelWaitThreadEndCB(thid, stat, timeout); }

/* device kit #7 (track probes): the measurement app's GPU transfer unit probe (shepgl_vita.c sgl_k7_transfer_probe) needs the real GXM:
 * there is none here, so it answers 0 = not run (one line; the host logs its PRB gpu verdict "not run"). Records nothing (no GL call). */
int sgl_k7_transfer_probe(void (*line)(const char *))
{
	line("PRB gpu: vlx has no GPU (the recording GL stand-in): the transfer unit probe runs on the device only");
	return 0;
}
