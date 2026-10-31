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
static void mix(const void *p, size_t n)
{
	const unsigned char *b = (const unsigned char *)p;
	uint64_t h = g_h;
	for (size_t i = 0; i < n; i++) { h ^= b[i]; h *= 1099511628211ull; }
	g_h = h;
	if (!g_excl) { h = g_h2; for (size_t i = 0; i < n; i++) { h ^= b[i]; h *= 1099511628211ull; } g_h2 = h; }
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
static void flush_frame(void)
{
	char line[240];
	int n = snprintf(line, sizeof line, "%u n=%u d=%u idx=%llu up=%u h=%016llx | thr=%u tid=0x%x n2=%u h2=%016llx\n", g_swaps, g_calls, g_draws, g_idx, g_uploads,
	                 (unsigned long long)g_h, g_thr, (unsigned)g_last_tid, g_calls2, (unsigned long long)g_h2);
	SceUID fd = sceIoOpen("ux0:data/shepherd/runtime/logs/glrec.txt", SCE_O_WRONLY | SCE_O_CREAT | SCE_O_APPEND, 0777);
	if (fd >= 0) { sceIoWrite(fd, line, n); sceIoClose(fd); }
	g_total ^= g_h; g_total *= 1099511628211ull;
	g_h = g_h2 = 1469598103934665603ull; g_calls = g_draws = g_uploads = g_thr = g_calls2 = 0; g_idx = 0;
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
void glClear(GLbitfield mask) { call(6); w32(mask); }
void glEnable(GLenum cap) { call(7); w32(cap); }
void glDisable(GLenum cap) { call(8); w32(cap); }
void glBlendFuncSeparate(GLenum a, GLenum b, GLenum c, GLenum d) { call(9); w32(a); w32(b); w32(c); w32(d); }
static void wid(GLuint id);
static void gen(GLsizei n, GLuint *out) { for (GLsizei i = 0; i < n && out; i++) { out[i] = g_next_id++; wid(out[i]); } }
void glGenTextures(GLsizei n, GLuint *t) { call(10); w32(n); gen(n, t); }
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
static void wid(GLuint id) { w32(canon(id)); }
static void vtex_forget(GLuint name)
{
	for (int i = 0; i < K8_VTEX; i++) if (g_vtex[i].name == name && name) { sceKernelFreeMemBlock(g_vtex[i].uid); memset(&g_vtex[i], 0, sizeof g_vtex[i]); }
}
static uint32_t *vtex_buf(GLuint name) { for (int i = 0; i < K8_VTEX; i++) if (g_vtex[i].name == name && name) return g_vtex[i].buf; return NULL; }
void glDeleteTextures(GLsizei n, const GLuint *t) { call(11); w32(n); for (GLsizei i = 0; i < n && t; i++) { wid(t[i]); vtex_forget(t[i]); } }
void glBindTexture(GLenum target, GLuint texture) { call(12); w32(target); wid(texture); if (target == GL_TEXTURE_2D) g_bound[g_active_unit & 3] = texture; }
void glActiveTexture(GLenum texture) { call(13); w32(texture); g_active_unit = (unsigned)(texture - GL_TEXTURE0) & 3; }
void glTexImage2D(GLenum target, GLint level, GLint ifmt, GLsizei w, GLsizei h, GLint border, GLenum fmt, GLenum type, const GLvoid *data)
{
	call(14); w32(target); w32(level); w32(ifmt); w32(w); w32(h); w32(border); w32(fmt); w32(type); w32(data != NULL);
	if (data && w > 0 && h > 0) {
		size_t row = (size_t)w * bpp_of(fmt), stride = (row + g_unpack - 1) / g_unpack * g_unpack;
		for (GLsizei y = 0; y < h; y++) mix((const char *)data + (size_t)y * stride, row);
	}
	g_uploads++;
}
void glCompressedTexImage2D(GLenum target, GLint level, GLenum ifmt, GLsizei w, GLsizei h, GLint border, GLsizei size, const void *data)
{ call(15); w32(target); w32(level); w32(ifmt); w32(w); w32(h); w32(border); w32(size); if (data && size > 0) mix(data, (size_t)size); g_uploads++; }
void glTexParameteri(GLenum target, GLenum pname, GLint param) { call(16); w32(target); w32(pname); w32(param); }
void glPixelStorei(GLenum pname, GLint param) { call(17); w32(pname); w32(param); if (pname == GL_UNPACK_ALIGNMENT && param > 0) g_unpack = param; }
void glReadPixels(GLint x, GLint y, GLsizei w, GLsizei h, GLenum fmt, GLenum type, GLvoid *data)
{ call(18); w32(x); w32(y); w32(w); w32(h); w32(fmt); w32(type); if (data && w > 0 && h > 0) memset(data, 0, (size_t)w * h * 4); }
void glGenFramebuffers(GLsizei n, GLuint *f) { call(19); w32(n); gen(n, f); }
void glDeleteFramebuffers(GLsizei n, const GLuint *f) { call(20); w32(n); for (GLsizei i = 0; i < n && f; i++) wid(f[i]); }
void glBindFramebuffer(GLenum target, GLuint fb) { call(21); w32(target); wid(fb); g_fbo = fb; }
void glFramebufferTexture2D(GLenum target, GLenum att, GLenum tt, GLuint tex, GLint level) { call(22); w32(target); w32(att); w32(tt); wid(tex); w32(level); }
GLenum glCheckFramebufferStatus(GLenum target) { call(23); w32(target); return GL_FRAMEBUFFER_COMPLETE; }
void glGenBuffers(GLsizei n, GLuint *b) { call(24); w32(n); gen(n, b); }
void glDeleteBuffers(GLsizei n, const GLuint *b) { call(25); w32(n); for (GLsizei i = 0; i < n && b; i++) wid(b[i]); }
void glBindBuffer(GLenum target, GLuint buffer) { call(26); w32(target); wid(buffer); }
void glBufferData(GLenum target, GLsizei size, const GLvoid *data, GLenum usage)
{ call(27); w32(target); w32(size); w32(usage); w32(data != NULL); if (data && size > 0) mix(data, (size_t)size); }
void glVertexAttribPointer(GLuint index, GLint size, GLenum type, GLboolean norm, GLsizei stride, const void *ptr)
{ call(28); w32(index); w32(size); w32(type); w32(norm); w32(stride); w32((uint32_t)(uintptr_t)ptr); }
void glEnableVertexAttribArray(GLuint index) { call(29); w32(index); }
void glDrawElements(GLenum mode, GLsizei count, GLenum type, const GLvoid *indices)
{
	call(30); w32(mode); w32(count); w32(type); w32((uint32_t)(uintptr_t)indices); g_draws++; g_idx += (unsigned)count;
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
	call(31); w32(t); GLuint r = g_next_id++; wid(r);
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
	free(all);
}
void glCompileShader(GLuint s) { call(34); wid(s); }
void glGetShaderiv(GLuint h, GLenum pname, GLint *p) { call(35); wid(h); w32(pname); if (p) *p = pname == GL_COMPILE_STATUS ? GL_TRUE : 0; }
void glGetShaderInfoLog(GLuint h, GLsizei max, GLsizei *len, GLchar *log) { call(36); wid(h); w32(max); if (len) *len = 0; if (log && max > 0) log[0] = 0; }
GLuint glCreateProgram(void) { call(37); GLuint r = g_next_id++; wid(r); return r; }
void glAttachShader(GLuint p, GLuint s) { call(38); wid(p); wid(s); }
void glBindAttribLocation(GLuint p, GLuint index, const GLchar *name) { call(39); wid(p); w32(index); wstr(name); }
void glLinkProgram(GLuint p) { call(40); wid(p); }
void glGetProgramiv(GLuint p, GLenum pname, GLint *v) { call(41); wid(p); w32(pname); if (v) *v = pname == GL_LINK_STATUS ? GL_TRUE : 0; }
void glGetProgramInfoLog(GLuint p, GLsizei max, GLsizei *len, GLchar *log) { call(42); wid(p); w32(max); if (len) *len = 0; if (log && max > 0) log[0] = 0; }
void glUseProgram(GLuint program) { call(43); wid(program); }
GLint glGetUniformLocation(GLuint prog, const GLchar *name) { call(44); wid(prog); wstr(name); GLint r = g_next_loc++; w32((uint32_t)r); return r; }
void glUniform1i(GLint location, GLint v0) { call(45); w32((uint32_t)location); w32((uint32_t)v0); }
void glUniform4f(GLint location, GLfloat a, GLfloat b, GLfloat c, GLfloat d) { call(46); w32((uint32_t)location); wf(a); wf(b); wf(c); wf(d); }
void glFinish(void) { call(47); }
void glFlush(void) { call(48); }
void glGenVertexArrays(GLsizei n, GLuint *res) { call(49); w32(n); gen(n, res); }
void glBindVertexArray(GLuint array) { call(50); wid(array); }

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
	g_swaps++;
	flush_frame();
	if (g_swaps % 200 == 0)
		sceClibPrintf("[glrec] swaps %u, running hash %016llx\n", g_swaps, (unsigned long long)g_total);
}
unsigned shepgl_swap_count(void) { return g_swaps; }
/* kit #8 boot track (the clean boot screen's black frame, shepgl_vita.c): NOT recorded and not counted -- on the device it presents one
 * cleared frame before the engine starts and leaves no GL state changed, so the engine's record is the same with and without it */
static unsigned g_boot_frames;
int shepgl_boot_frame(void) { g_boot_frames++; if (g_gl_display) gl_display_flip(); return 1; }
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
		g_vram[i].p = p; g_vram[i].uid = b; g_vram[i].kib = (bytes + 1023u) / 1024u; g_vram_held_kib += g_vram[i].kib; return p;
	}
	return NULL;
}
void shepgl_vram_free(void *p) { for (int i = 0; i < 8; i++) if (p && g_vram[i].p == p) { sceKernelFreeMemBlock(g_vram[i].uid); g_vram[i].p = NULL; g_vram_held_kib -= g_vram[i].kib; } }
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
	}
	snprintf(g_status, sizeof g_status, "vlx RECORDING stand-in for vitaGL (no GPU, no pools; every GL call hashed into runtime/logs/glrec.txt): %dx%d, ram_threshold %d MiB (not applied), gc thread affinity 0x%x (not applied)%s%s",
	         c->width, c->height, c->ram_threshold >> 20, (unsigned)c->gc_affinity, g_usermem_note, g_phycont_note);
	{   /* kit #8 boot review (failure-path test only): VLX_GL_INIT_FAIL=1 in the app's env file = vitaGL did not come up (shepgl_init 0) */
		const char *f = getenv("VLX_GL_INIT_FAIL");
		if (f && !strcmp(f, "1")) { size_t l = strlen(g_status); snprintf(g_status + l, sizeof g_status - l, "; VLX_GL_INIT_FAIL=1: init failure modelled"); return 0; }
	}
	g_state = 1;
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
 * Unset = kit #7's stand-in (0: no pool -- the preload stays off under vlx, and the host's pool line reads 0). Not recorded. */
unsigned shepgl_pool_kib(int pool, int total)
{
	const char *e = pool == 0 ? getenv("VLX_VRAM_FREE_KIB") : NULL;
	if (e && !strcmp(e, "device")) return total ? 98304 : 1733632;   /* kit #9 review round 2 (finding 4): the play apps' reading on the owner's PCH-2000 */
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
