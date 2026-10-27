/* shepgl on macOS (CGL, legacy 2.1 context, headless) -- so vita/gltest can check its own logic and its CPU reference on
 * desktop GL before a device run, and so the engine host's Mac rehearsal (vita/shepherd/mac-check/run-gl.sh, Stage B) drives
 * GlBackend through the same API. Same API as shepgl_vita.c; every entry is the framework's own function except
 * glShaderSource, which accepts the GLSL ES 1.00 text vitaGL gets (see shepgl_mac_ShaderSource). Not a product path. */
#include "shepgl.h"
#include <OpenGL/OpenGL.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

typedef struct { const char *name; void *fn; unsigned char wrapped; } entry;
#define SHEPGL_E(idx, field, sym, how) { #sym, (void *)(sym), 0 },
static const entry k_entries[] = {
#include "shepgl_entries.h"
};
#undef SHEPGL_E
_Static_assert(sizeof k_entries / sizeof k_entries[0] == SHEPGL_COUNT, "Gl.cs has 51 entry points");

/* The Mac rehearsal feeds GlBackend's essl100 dialect: "#version 100" + "precision <p> float;" + the body. Apple's legacy (2.1)
 * compiler accepts neither line (it knows GLSL 1.20 only), so a source whose first line is "#version 100" gets "#version 120"
 * there and every whole-line default-precision statement becomes an empty line (line numbers kept); the body -- the part
 * vitaGL translates -- is handed over untouched. GLSL 1.20 sources (the gltest self-check, the SDL path) pass through. */
static void shepgl_mac_ShaderSource(GLuint sh, GLsizei count, const GLchar *const *str, const GLint *len)
{
	size_t n = 0;
	for (GLsizei i = 0; i < count; i++) n += len && len[i] >= 0 ? (size_t)len[i] : strlen(str[i]);
	char *src = (char *)malloc(n + 1);
	if (!src) { glShaderSource(sh, count, str, len); return; }
	size_t o = 0;
	for (GLsizei i = 0; i < count; i++) { size_t k = len && len[i] >= 0 ? (size_t)len[i] : strlen(str[i]); memcpy(src + o, str[i], k); o += k; }
	src[o] = 0;
	if (!strncmp(src, "#version 100", 12) && (src[12] == '\n' || src[12] == '\r' || src[12] == ' ')) {
		memcpy(src, "#version 120", 12);
		for (char *line = src; line && *line;) {
			char *end = strchr(line, '\n');
			char *p = line;
			while (*p == ' ' || *p == '\t') p++;
			if (!strncmp(p, "precision", 9)) {
				char *semi = memchr(p, ';', (size_t)((end ? end : p + strlen(p)) - p));
				int whole = semi != NULL;
				for (char *q = semi ? semi + 1 : p; whole && q < (end ? end : p + strlen(p)); q++) if (*q != ' ' && *q != '\t' && *q != '\r') whole = 0;
				if (whole) for (char *q = line; q < (end ? end : line + strlen(line)); q++) *q = ' ';
			}
			line = end ? end + 1 : NULL;
		}
	}
	const GLchar *one = src;
	glShaderSource(sh, 1, &one, NULL);
	free(src);
}
static void *mac_impl(const char *name, void *fn) { return !strcmp(name, "glShaderSource") ? (void *)shepgl_mac_ShaderSource : fn; }

int shepgl_count(void) { return SHEPGL_COUNT; }
const char *shepgl_name(int i) { return i >= 0 && i < SHEPGL_COUNT ? k_entries[i].name : NULL; }
void *shepgl_fn(int i) { return i >= 0 && i < SHEPGL_COUNT ? mac_impl(k_entries[i].name, k_entries[i].fn) : NULL; }
int shepgl_is_wrapped(int i) { return i >= 0 && i < SHEPGL_COUNT && mac_impl(k_entries[i].name, k_entries[i].fn) != k_entries[i].fn; }
void *shepgl_proc(const char *name)
{
	if (!name) return NULL;
	for (int i = 0; i < SHEPGL_COUNT; i++) if (!strcmp(k_entries[i].name, name)) return mac_impl(name, k_entries[i].fn);
	if (!strcmp(name, "shepgl_abi_probe")) return (void *)shepgl_abi_probe;
	return NULL;
}
void *shepgl_raw(const char *name) { (void)name; return NULL; }

static CGLContextObj g_ctx;
static GLuint g_disp_fbo, g_disp_tex;
static char g_status[512];
static int g_state;
const char *shepgl_status(void) { return g_status; }
int shepgl_state(void) { return g_state; }
/* a CGL context is current per thread: the engine binds it on the Mono thread, the host on its main thread */
int shepgl_attach(void) { return g_ctx != NULL && CGLSetCurrentContext(g_ctx) == kCGLNoError; }
unsigned shepgl_pool_kib(int pool, int total) { (void)pool; (void)total; return 0; }
int shepgl_compiler_online(void) { return g_ctx != NULL; }
const char *shepgl_last_diagnostics(void) { return ""; }
void shepgl_set_log(shepgl_line_fn fn) { (void)fn; }
GLuint shepgl_display_fbo(void) { return g_disp_fbo; }
static unsigned g_swaps;
void shepgl_swap(void) { glFlush(); g_swaps++; }
int shepgl_boot_frame(void) { return 0; }   /* kit #8: the Mac rehearsal host has no boot screen */
void *shepgl_vram_alloc(unsigned bytes, unsigned align) { (void)bytes; (void)align; return NULL; }   /* kit #8: no hardware video on the Mac */
void shepgl_vram_free(void *p) { (void)p; }
void *shepgl_tex_data(GLuint name) { (void)name; return NULL; }
unsigned shepgl_swap_count(void) { return g_swaps; }
void shepgl_set_vsync(int on) { (void)on; }
void shepgl_set_compiler(int opt, int fastmath, int fastprecision, int fastint) { (void)opt; (void)fastmath; (void)fastprecision; (void)fastint; }
void shepgl_set_semantic_mode(int mode) { (void)mode; }
int shepgl_shader_binary_size(GLuint shader) { (void)shader; return 0; }
/* GXP cache (kit #5): no GXP on the Mac test host -- the engine's cache reports itself unavailable and compiles as before */
int shepgl_gxp_identity(char *out, int cap, int *ms) { (void)out; (void)cap; if (ms) *ms = 0; return -1; }
int shepgl_gxp_save(GLuint program, GLuint shader, int type, void *buf, int cap) { (void)program; (void)shader; (void)type; (void)buf; (void)cap; return 0; }
int shepgl_gxp_check(const void *buf, int len, int type) { (void)buf; (void)len; (void)type; return 0; }
int shepgl_gxp_load(GLuint shader, int type, const void *buf, int len) { (void)shader; (void)type; (void)buf; (void)len; return 0; }
const char *shepgl_gxp_why(void) { return "the Mac test host (CGL) has no GXP shader binaries"; }

void shepgl_default_config(shepgl_config *c)
{
	memset(c, 0, sizeof *c);
	c->width = 960; c->height = 544; c->ram_threshold = SHEPGL_DEFAULT_RAM_THRESHOLD; c->vsync = 1;
	c->semantic_mode = SHEPGL_SEMA_SHADER_PAIR; c->opt = 3; c->fastmath = 1; c->fastint = 1;
}

int shepgl_init(const shepgl_config *c)
{
	CGLPixelFormatAttribute at[] = { kCGLPFAOpenGLProfile, (CGLPixelFormatAttribute)kCGLOGLPVersion_Legacy, kCGLPFAAccelerated,
	                                 kCGLPFAColorSize, (CGLPixelFormatAttribute)24, kCGLPFAAlphaSize, (CGLPixelFormatAttribute)8,
	                                 (CGLPixelFormatAttribute)0 };
	CGLPixelFormatObj pf = NULL; GLint npf = 0;
	if (CGLChoosePixelFormat(at, &pf, &npf) != kCGLNoError || !pf) { snprintf(g_status, sizeof g_status, "CGLChoosePixelFormat failed"); return 0; }
	CGLError e = CGLCreateContext(pf, NULL, &g_ctx);
	CGLDestroyPixelFormat(pf);
	if (e != kCGLNoError || !g_ctx) { snprintf(g_status, sizeof g_status, "CGLCreateContext failed (%d)", (int)e); return 0; }
	CGLSetCurrentContext(g_ctx);
	/* the "display": an offscreen RGBA8 target standing in for the Vita's framebuffer 0 (bottom-up like any default FB) */
	glGenTextures(1, &g_disp_tex);
	glBindTexture(GL_TEXTURE_2D, g_disp_tex);
	glTexImage2D(GL_TEXTURE_2D, 0, GL_RGBA, c->width, c->height, 0, GL_RGBA, GL_UNSIGNED_BYTE, NULL);
	glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MIN_FILTER, GL_NEAREST);
	glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MAG_FILTER, GL_NEAREST);
	glGenFramebuffers(1, &g_disp_fbo);
	glBindFramebuffer(GL_FRAMEBUFFER, g_disp_fbo);
	glFramebufferTexture2D(GL_FRAMEBUFFER, GL_COLOR_ATTACHMENT0, GL_TEXTURE_2D, g_disp_tex, 0);
	GLenum st = glCheckFramebufferStatus(GL_FRAMEBUFFER);
	glBindFramebuffer(GL_FRAMEBUFFER, 0);
	glBindTexture(GL_TEXTURE_2D, 0);
	snprintf(g_status, sizeof g_status, "macOS CGL legacy context (%s); display stand-in FBO %ux%u status 0x%04X", (const char *)glGetString(GL_VERSION),
	         (unsigned)c->width, (unsigned)c->height, (unsigned)st);
	g_state = st == GL_FRAMEBUFFER_COMPLETE ? 1 : 0;
	return g_state;
}

int shepgl_has_extension(const char *name)
{
	const char *e = (const char *)glGetString(GL_EXTENSIONS);
	size_t n = strlen(name);
	while (e && *e) {
		while (*e == ' ') e++;
		const char *end = strchr(e, ' ');
		size_t len = end ? (size_t)(end - e) : strlen(e);
		if (len == n && !strncmp(e, name, n)) return 1;
		e += len;
	}
	return 0;
}

void shepgl_mem(shepgl_meminfo *m) { memset(m, 0, sizeof *m); }
const char *shepgl_pool_name(int i)
{
	static const char *const n[5] = { "vram", "ram", "phycont", "cdlg", "newlib" };
	return i >= 0 && i < 5 ? n[i] : "?";
}

float shepgl_abi_probe(int32_t i, float a, float b, float c, float d, uint8_t flag)
{
	return (float)i + 2.0f * a + 3.0f * b + 5.0f * c + 7.0f * d + 11.0f * (float)flag;
}
