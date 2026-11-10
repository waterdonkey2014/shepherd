/* shepgl on vitaGL -- see shepgl.h. Build: arm-vita-eabi, static library libshepgl.a (vita/gltest/CMakeLists.txt). */
#include "shepgl.h"
#include "shepgl_gxp_blob.h"
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <psp2/kernel/clib.h>
#include <psp2/kernel/sysmem.h>     /* (kit #8 review: sceKernelGetMemBlockInfoByAddr in shepgl_tex_data) */
#include <psp2/kernel/threadmgr.h>
#include <psp2/kernel/processmgr.h>
#include <psp2/io/fcntl.h>

extern unsigned char is_shark_online;   /* vitaGL (custom_shaders.o): 1 once libshacccg.suprx is loaded */
/* vitaGL (gxm.o): set by its init. vglInitExtended's return value is NOT a success flag in this vitaGL (libvitaGL.a of
 * ~/vitasdk, 2026-06-28): it is 1 only when the requested size exceeded sceDisplayGetMaximumFrameBufResolution and was
 * clamped, 0 otherwise -- including a complete, working 960x544 init on a handheld -- and 0 on a second call. None of the
 * init sub-steps report failure. So "is vitaGL up" is read from vitaGL's own state instead
 * (devicelogs/analysis-2026-10-12/vitagl-rootcause.md). */
extern void *gxm_context, *gxm_shader_patcher, *gxm_render_target;
/* vitaGL (splashscreen.o): its init starts a splash thread (invoke_splashscreen, every non-system app) that draws the animated
 * logo with its own GXM context and queues it to the display every frame until the FIRST GL SCENE: scene_reset (gxm.o
 * 0x94c-0x97e) signals splash_mutex[0], waits for splash_mutex[1], clears is_splashscreen_active, calls clear_splashscreen and
 * deletes both semaphores; vglSwapBuffers does nothing while the flag is set. A host that gives up after a failed init never
 * draws a scene, so the splash keeps the display and hides any fallback screen (2026-10-09 probe: the logo stayed). */
extern unsigned char is_splashscreen_active;
extern SceUID splash_mutex[2];   /* [0] "end now" (to the splash thread), [1] "ended" (from it) */
extern void clear_splashscreen(void);

/* Ends vitaGL's splash exactly the way scene_reset does, without a GL scene (for failure paths). 1 = ended, 0 = it was not
 * running, -1 = the thread did not answer within timeout_ms (left running: its semaphores are not deleted under it). */
static int end_splash(unsigned timeout_ms)
{
	if (!is_splashscreen_active)
		return 0;
	sceKernelSignalSema(splash_mutex[0], 1);
	SceUInt to = timeout_ms * 1000u;
	if (sceKernelWaitSema(splash_mutex[1], 1, &to) < 0)
		return -1;
	is_splashscreen_active = 0;
	if (gxm_shader_patcher)
		clear_splashscreen();   /* unregisters the splash programs from the shader patcher */
	sceKernelDeleteSema(splash_mutex[0]);
	sceKernelDeleteSema(splash_mutex[1]);
	sceKernelDelayThread(100 * 1000);   /* let a flip the splash queued last reach the display before anyone else sets it */
	return 1;
}

/* vitaGL's pool getters are only safe on a pool whose mspace exists. vgl_mem_init stores each requested pool size BEFORE it
 * maps the memblock (mem_utils.o 0xae-0xb2) and only skips the mapping when sceKernelGetMemBlockBase fails (0x15c-0x16c), so
 * vglMemTotal stays > 0 for a pool that never came up, and vglMemFree then calls sceClibMspaceMallocStats on a NULL mspace
 * (0x3c6). vglAlloc has vgl_malloc's own NULL-mspace guard (0x55e-0x568): a 4 KiB alloc + free proves the pool is real.
 * 1 = usable, 0 = absent (total 0, vglMemFree is safe and returns 0), -1 = sized but unusable (no mspace, or full). */
static int pool_alive(vglMemType t)
{
	if (t == VGL_MEM_EXTERNAL || vglMemTotal(t) == 0) return t == VGL_MEM_EXTERNAL;
	void *p = vglAlloc(4096, t);
	if (p) vglFree(p);
	return p ? 1 : -1;
}
static size_t pool_free(vglMemType t) { return pool_alive(t) >= 0 ? vglMemFree(t) : 0; }

/* The managed side passes C# int/uint/nint/byte/float/pointers; these are the sizes that makes ABI-equal to vitaGL's types. */
_Static_assert(sizeof(void *) == 4, "arm-vita-eabi is ILP32: C# nint == 32 bits");
_Static_assert(sizeof(GLsizei) == sizeof(intptr_t), "Gl.BufferData passes nint where vitaGL takes GLsizei");
_Static_assert(sizeof(GLboolean) == 1, "Gl.VertexAttribPointer passes byte for GLboolean");
_Static_assert(sizeof(GLenum) == 4 && sizeof(GLuint) == 4 && sizeof(GLint) == 4 && sizeof(GLbitfield) == 4, "32-bit GL scalars");
_Static_assert(sizeof(GLfloat) == 4, "GLfloat is float");

#ifndef GL_UNPACK_ALIGNMENT
#define GL_UNPACK_ALIGNMENT 0x0CF5
#endif
#ifndef GL_PACK_ALIGNMENT
#define GL_PACK_ALIGNMENT 0x0D05
#endif

/* ------------------------------------------------------------------ compiler diagnostics */
#define CAP 4096
static char g_cap[CAP];
static int g_caplen, g_capturing;
static char g_last[CAP];
static shepgl_line_fn g_logfn;

static void cap_append(const char *s)
{
	int n = (int)strlen(s);
	if (g_caplen + n + 2 >= CAP) n = CAP - g_caplen - 2;
	if (n <= 0) return;
	memcpy(g_cap + g_caplen, s, n);
	g_caplen += n;
	g_cap[g_caplen++] = '\n';
	g_cap[g_caplen] = 0;
}

static void shark_cb(const char *msg, shark_log_level level, int line)
{
	char buf[640];
	const char *lv = level == SHARK_LOG_ERROR ? "error" : level == SHARK_LOG_WARNING ? "warning" : "info";
	if (line >= 0) snprintf(buf, sizeof buf, "shacccg %s (Cg line %d): %s", lv, line, msg ? msg : "");
	else snprintf(buf, sizeof buf, "shacccg %s: %s", lv, msg ? msg : "");
	for (char *p = buf; *p; p++) if (*p == '\n' || *p == '\r') *p = ' ';
	if (g_capturing) cap_append(buf);
	if (g_logfn) g_logfn(buf);
}

static void cap_begin(void) { g_caplen = 0; g_cap[0] = 0; g_capturing = 1; }
static void cap_end(void) { g_capturing = 0; memcpy(g_last, g_cap, (size_t)g_caplen + 1); }

/* per-object logs: shader and program names are small integers in vitaGL (index + 1) */
typedef struct { GLuint id; char kind; char *log; } objlog;
#define NOBJ 128
static objlog g_logs[NOBJ];
static int g_next;

static objlog *find_log(char kind, GLuint id)
{
	for (int i = 0; i < NOBJ; i++) if (g_logs[i].log && g_logs[i].kind == kind && g_logs[i].id == id) return &g_logs[i];
	return NULL;
}
static void set_log(char kind, GLuint id, const char *text)
{
	objlog *o = find_log(kind, id);
	if (!o) { o = &g_logs[g_next]; g_next = (g_next + 1) % NOBJ; }
	free(o->log);
	o->log = NULL;
	if (!text || !*text) return;
	size_t n = strlen(text);
	while (n > 0 && text[n - 1] == '\n') n--;
	o->log = (char *)malloc(n + 1);
	if (!o->log) return;
	memcpy(o->log, text, n);
	o->log[n] = 0;
	o->kind = kind;
	o->id = id;
}
static const char *get_log(char kind, GLuint id) { objlog *o = find_log(kind, id); return o ? o->log : NULL; }

static void copy_log(const char *l, GLsizei max, GLsizei *len, GLchar *out)
{
	GLsizei n = 0;
	if (out && max > 0) {
		if (l) { n = (GLsizei)strlen(l); if (n > max - 1) n = max - 1; memcpy(out, l, (size_t)n); }
		out[n] = 0;
	}
	if (len) *len = n;
}

/* ------------------------------------------------------------------ the 7 wrapped entry points */
static void shepgl_w_glCompileShader(GLuint s)
{
	cap_begin();
	glCompileShader(s);
	cap_end();
	GLint ok = 1;
	glGetShaderiv(s, GL_COMPILE_STATUS, &ok);
	if (!ok && g_caplen == 0)
		set_log('s', s, is_shark_online ? "(no libshacccg diagnostics: vitaGL's GLSL translator or the GXM shader patcher refused it)"
		                                : "(libshacccg is not loaded: vitaGL has no shader compiler)");
	else set_log('s', s, g_cap);
}
static void shepgl_w_glGetShaderiv(GLuint s, GLenum pname, GLint *params)
{
	if (pname == GL_INFO_LOG_LENGTH) { const char *l = get_log('s', s); if (params) *params = l ? (GLint)strlen(l) + 1 : 0; return; }
	glGetShaderiv(s, pname, params);
}
static void shepgl_w_glGetShaderInfoLog(GLuint s, GLsizei max, GLsizei *len, GLchar *out) { copy_log(get_log('s', s), max, len, out); }
static void shepgl_w_glLinkProgram(GLuint p)
{
	cap_begin();
	glLinkProgram(p);
	cap_end();
	GLint ok = 1;
	glGetProgramiv(p, GL_LINK_STATUS, &ok);
	if (!ok && g_caplen == 0)
		set_log('p', p, "(link failed without libshacccg diagnostics: attribute/varying binding or the GXM shader patcher)");
	else set_log('p', p, g_cap);
}
static void shepgl_w_glGetProgramiv(GLuint p, GLenum pname, GLint *params)
{
	if (pname == GL_INFO_LOG_LENGTH) { const char *l = get_log('p', p); if (params) *params = l ? (GLint)strlen(l) + 1 : 0; return; }
	glGetProgramiv(p, pname, params);
}
static void shepgl_w_glGetProgramInfoLog(GLuint p, GLsizei max, GLsizei *len, GLchar *out) { copy_log(get_log('p', p), max, len, out); }

/* vitaGL's glPixelStorei knows only GL_UNPACK_ROW_LENGTH and raises GL_INVALID_ENUM for the alignments. It reads and writes
 * tightly packed rows, which is what GL does for every transfer GlBackend makes (RGBA8 rows are 4w bytes; ALPHA8 uploads
 * set UNPACK_ALIGNMENT 1 first; readbacks are RGBA8). So the alignments are accepted and remembered, not emulated. */
static GLint g_unpack_align = 4, g_pack_align = 4;
static void shepgl_w_glPixelStorei(GLenum pname, GLint param)
{
	if ((pname == GL_UNPACK_ALIGNMENT || pname == GL_PACK_ALIGNMENT) && (param == 1 || param == 2 || param == 4 || param == 8)) {
		if (pname == GL_UNPACK_ALIGNMENT) g_unpack_align = param; else g_pack_align = param;
		return;
	}
	glPixelStorei(pname, param);
}

/* ------------------------------------------------------------------ table */
typedef struct { const char *name; void *fn; unsigned char wrapped; } entry;
#define SHEPGL_DIRECT(sym) (void *)(sym), 0
#define SHEPGL_WRAP(sym) (void *)(shepgl_w_##sym), 1
#define SHEPGL_E(idx, field, sym, how) { #sym, SHEPGL_##how(sym) },
static const entry k_entries[] = {
#include "shepgl_entries.h"
};
#undef SHEPGL_E
_Static_assert(sizeof k_entries / sizeof k_entries[0] == SHEPGL_COUNT, "Gl.cs has SHEPGL_COUNT entry points");

int shepgl_count(void) { return SHEPGL_COUNT; }
const char *shepgl_name(int i) { return i >= 0 && i < SHEPGL_COUNT ? k_entries[i].name : NULL; }
void *shepgl_fn(int i) { return i >= 0 && i < SHEPGL_COUNT ? k_entries[i].fn : NULL; }
int shepgl_is_wrapped(int i) { return i >= 0 && i < SHEPGL_COUNT ? k_entries[i].wrapped : 0; }
void *shepgl_proc(const char *name)
{
	if (!name) return NULL;
	for (int i = 0; i < SHEPGL_COUNT; i++) if (!strcmp(k_entries[i].name, name)) return k_entries[i].fn;
	if (!strcmp(name, "shepgl_abi_probe")) return (void *)shepgl_abi_probe;
	return NULL;
}
void *shepgl_raw(const char *name)
{
	static const struct { const char *n; void *f; } raw[] = {
		{ "glPixelStorei", (void *)glPixelStorei }, { "glCompileShader", (void *)glCompileShader },
		{ "glGetShaderiv", (void *)glGetShaderiv }, { "glGetShaderInfoLog", (void *)glGetShaderInfoLog },
		{ "glLinkProgram", (void *)glLinkProgram }, { "glGetProgramiv", (void *)glGetProgramiv },
		{ "glGetProgramInfoLog", (void *)glGetProgramInfoLog },
	};
	for (unsigned i = 0; i < sizeof raw / sizeof raw[0]; i++) if (!strcmp(raw[i].n, name)) return raw[i].f;
	return NULL;
}

/* ------------------------------------------------------------------ context */
static char g_status[768];
static int g_state;
const char *shepgl_status(void) { return g_status; }
int shepgl_state(void) { return g_state; }
int shepgl_attach(void) { return g_state != 0; }   /* vitaGL: one global context, usable from any thread (one at a time) */
int shepgl_compiler_online(void) { return is_shark_online; }
const char *shepgl_last_diagnostics(void) { return g_last; }
void shepgl_set_log(shepgl_line_fn fn) { g_logfn = fn; }
GLuint shepgl_display_fbo(void) { return 0; }
static unsigned g_swaps;
void shepgl_swap(void) { vglSwapBuffers(GL_FALSE); g_swaps++; }
int shepgl_boot_frame(void)
{
	if (g_state == 0) return 0;
	glBindFramebuffer(GL_FRAMEBUFFER, 0);
	glViewport(0, 0, 960, 544);
	glDisable(GL_SCISSOR_TEST);
	glClearColor(0.0f, 0.0f, 0.0f, 0.0f);
	glClear(GL_COLOR_BUFFER_BIT);
	vglSwapBuffers(GL_FALSE);   /* (not counted: shepgl_swap_count is the engine's frames) */
	return 1;
}

/* kit #8 boot track (the engine's real video): VRAM blocks for the clip and the decoder (vglAlloc keeps no alignment promise beyond its own:
 * the block is asked `align` bytes larger and the pointer rounded up; a small table maps it back to vglAlloc's for the free) */
#define K8_VRAM_SLOTS 8
static struct { void *aligned, *raw; } g_k8_vram[K8_VRAM_SLOTS];
void *shepgl_vram_alloc(unsigned bytes, unsigned align)
{
	if (g_state == 0 || !bytes) return NULL;
	if (align < 16) align = 16;
	int slot = -1;
	for (int i = 0; i < K8_VRAM_SLOTS; i++) if (!g_k8_vram[i].raw) { slot = i; break; }
	if (slot < 0) return NULL;
	void *raw = vglAlloc(bytes + align, VGL_MEM_VRAM);
	if (!raw) return NULL;
	void *a = (void *)(((uintptr_t)raw + align - 1) & ~(uintptr_t)(align - 1));
	g_k8_vram[slot].raw = raw; g_k8_vram[slot].aligned = a;
	return a;
}
void shepgl_vram_free(void *p)
{
	for (int i = 0; i < K8_VRAM_SLOTS; i++)
		if (p && g_k8_vram[i].aligned == p) { vglFree(g_k8_vram[i].raw); g_k8_vram[i].raw = g_k8_vram[i].aligned = NULL; return; }
}
void *shepgl_tex_data(GLuint name)
{
	if (g_state == 0) return NULL;
	GLint old = 0;
	glGetIntegerv(GL_TEXTURE_BINDING_2D, &old);
	glBindTexture(GL_TEXTURE_2D, name);
	void *p = vglGetTexDataPointer(GL_TEXTURE_2D);
	glBindTexture(GL_TEXTURE_2D, (GLuint)old);
	/* kit #8 review fix: the hardware decoder writes the picture by DMA, so the texture's memory must be physically contiguous. vitaGL puts a
	 * texture in its VRAM pool (CDRAM) and, when that is full, falls back to its other pools -- among them the USER_RW RAM pool and newlib's
	 * heap, ordinary main memory (4 KiB pages, not contiguous): a decode there would write to the wrong physical pages. Such a texture is
	 * refused (NULL: the engine draws the clip black, kit #7's picture, with one line). Only positive evidence refuses: a block of the
	 * USER main-memory family (0x0C2xxxxx: USER_RW, MAIN_*; 0x0E2xxxxx: the CDialog budget); CDRAM (0x094xxxxx) and PHYCONT (0x0C8xxxxx) pass,
	 * and so does an address the kernel does not describe (the check then decides nothing). */
	if (p) {
		SceKernelMemBlockInfo mi;
		memset(&mi, 0, sizeof mi);
		mi.size = sizeof mi;
		int r = sceKernelGetMemBlockInfoByAddr(p, &mi);
		unsigned fam = (unsigned)mi.type & 0xFFF00000u;
		if (r >= 0 && (fam == 0x0C200000u || fam == 0x0E200000u)) {
			char b[200];
			snprintf(b, sizeof b, "WARN shepgl_tex_data: texture %u's memory %p is not physically contiguous (memblock type 0x%08x): no hardware video into it",
			         (unsigned)name, p, (unsigned)mi.type);
			if (g_logfn) g_logfn(b);
			return NULL;
		}
	}
	return p;
}

/* kit #8 boot track (the clean boot screen): vitaGL's init calls invoke_splashscreen (every non-system app) -- an animated vitaGL logo on
 * its own GXM context until the first GL scene, ~60 s on the device before the engine's first frame. The GL link wraps that call
 * (-Wl,--wrap=invoke_splashscreen, vita/shepherd/build.sh): with shepgl_config.no_vgl_splash the splash is never started --
 * is_splashscreen_active stays 0, so vitaGL's first scene skips its splash teardown and vglSwapBuffers presents from the first call;
 * vitaGL's pools are sized before the call (vgl.o: vgl_mem_init comes first), so they are kit #7's. Without the flag, or in a link
 * without the wrap (vita/gltest), vitaGL's own splash runs as before. */
extern void __real_invoke_splashscreen(void) __attribute__((weak));
static int g_no_vgl_splash;
void __wrap_invoke_splashscreen(void);
void __wrap_invoke_splashscreen(void) { if (!g_no_vgl_splash && __real_invoke_splashscreen) __real_invoke_splashscreen(); }
unsigned shepgl_swap_count(void) { return g_swaps; }
void shepgl_set_vsync(int on) { vglWaitVblankStart(on ? GL_TRUE : GL_FALSE); }
void shepgl_set_compiler(int opt, int fastmath, int fastprecision, int fastint)
{
	vglSetupRuntimeShaderCompiler((shark_opt)opt, fastmath, fastprecision, fastint);
}
void shepgl_set_semantic_mode(int mode)
{
	vglSetSemanticBindingMode(mode == SHEPGL_SEMA_SHADER_PAIR ? VGL_MODE_SHADER_PAIR : mode == SHEPGL_SEMA_GLOBAL ? VGL_MODE_GLOBAL : VGL_MODE_POSTPONED);
}

void shepgl_default_config(shepgl_config *c)
{
	memset(c, 0, sizeof *c);
	c->width = 960; c->height = 544;
	c->ram_threshold = SHEPGL_DEFAULT_RAM_THRESHOLD;
	c->msaa = 0;
	c->vsync = 1;
	c->semantic_mode = SHEPGL_SEMA_SHADER_PAIR;
	/* vitaGL's own defaults (custom_shaders.o .data): SHARK_OPT_FAST, fastmath 1, fastprecision 0, fastint 1 -- set explicitly
	 * so a vitaGL update cannot change them silently */
	c->opt = SHARK_OPT_FAST; c->fastmath = 1; c->fastprecision = 0; c->fastint = 1;
}

int shepgl_init(const shepgl_config *c)
{
	shepgl_set_compiler(c->opt, c->fastmath, c->fastprecision, c->fastint);
	if (c->gc_affinity)
		vglSetupGarbageCollector(SHEPGL_GC_PRIORITY, c->gc_affinity);   /* must precede vglInit*: vitaGL creates the thread there */
	if (c->param_buffer_kib > 0)
		vglSetParamBufferSize((uint32_t)c->param_buffer_kib * 1024u);   /* kit5 mem G7: before vglInit* (init_gxm hands it to sceGxmVshInitialize) */
	g_no_vgl_splash = c->no_vgl_splash;   /* kit #8: read by __wrap_invoke_splashscreen inside vglInitExtended */
	GLboolean clamped = vglInitExtended(0, c->width, c->height, c->ram_threshold, c->msaa ? (SceGxmMultisampleMode)c->msaa : SCE_GXM_MULTISAMPLE_NONE);
	/* "vitaGL up" = its own state: GXM context + shader patcher + display render target exist and a 4 KiB vglAlloc from the
	 * VRAM pool succeeds; the compiler (is_shark_online) decides 1 vs -1. The return value is logged only as clamped=N. */
	int vram = pool_alive(VGL_MEM_VRAM);
	int up = gxm_context && gxm_shader_patcher && gxm_render_target && vram == 1;
	char state[300];
	snprintf(state, sizeof state, "clamped=%d; context %s (%p), shader patcher %s (%p), render target %s (%p), VRAM pool %u KiB %s, libshacccg %s",
	         clamped, gxm_context ? "up" : "MISSING", gxm_context, gxm_shader_patcher ? "up" : "MISSING", gxm_shader_patcher,
	         gxm_render_target ? "up" : "MISSING", gxm_render_target, (unsigned)(vglMemTotal(VGL_MEM_VRAM) >> 10),
	         vram == 1 ? "(4 KiB vglAlloc ok)" : vram == 0 ? "ABSENT" : "SIZED BUT UNUSABLE (4 KiB vglAlloc failed)",
	         is_shark_online ? "online" : "NOT LOADED");
	if (!up) {
		int sp = end_splash(5000);   /* or vitaGL's logo keeps the display and the host's fallback screen never shows */
		snprintf(g_status, sizeof g_status, "vglInitExtended(0, %d, %d, %d MiB, msaa %d): vitaGL is NOT up: %s; splash %s",
		         c->width, c->height, c->ram_threshold >> 20, c->msaa, state,
		         sp > 0 ? "ended" : sp == 0 ? "was not running" : "did NOT end within 5 s (it may keep the display)");
		g_state = 0;
		return 0;
	}
	shark_install_log_cb(shark_cb);           /* vitaGL installs none: without it, compiler errors are lost */
	shark_set_warnings_level(SHARK_WARN_LOW);
	shepgl_set_semantic_mode(c->semantic_mode);
	shepgl_set_vsync(c->vsync);
	snprintf(g_status, sizeof g_status,
	         "vglInitExtended(legacy 0, %dx%d, ram_threshold %d MiB, MSAA %s): vitaGL up: %s%s; semantic mode %s; compiler opt %d fastmath %d fastprecision %d fastint %d; vsync %s%s",
	         c->width, c->height, c->ram_threshold >> 20, c->msaa ? "on" : "off", state, clamped ? " (size clamped to the display maximum)" : "",
	         c->semantic_mode == SHEPGL_SEMA_SHADER_PAIR ? "SHADER_PAIR" : c->semantic_mode == SHEPGL_SEMA_GLOBAL ? "GLOBAL" : "POSTPONED",
	         c->opt, c->fastmath, c->fastprecision, c->fastint, c->vsync ? "on" : "off",
	         c->gc_affinity == 0x40000 ? "; gc thread USER_2" : c->gc_affinity ? "; gc thread affinity set" : "");
	if (c->no_vgl_splash) {
		size_t l = strlen(g_status);
		snprintf(g_status + l, sizeof g_status - l, "; vitaGL's splash %s (kit #8 boot screen)", is_splashscreen_active ? "RUNNING (the wrap is not linked)" : "left out");
	}
	if (c->param_buffer_kib > 0) {
		size_t l = strlen(g_status);
		snprintf(g_status + l, sizeof g_status - l, "; GXM parameter buffer %d KiB (vitaGL's default 16384)", c->param_buffer_kib);
	}
	g_state = is_shark_online ? 1 : -1;
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

void shepgl_mem(shepgl_meminfo *m)
{
	static const vglMemType t[5] = { VGL_MEM_VRAM, VGL_MEM_RAM, VGL_MEM_SLOW, VGL_MEM_BUDGET, VGL_MEM_EXTERNAL };
	for (int i = 0; i < 5; i++) { m->free_[i] = pool_free(t[i]); m->total[i] = vglMemTotal(t[i]); }
}
const char *shepgl_pool_name(int i)
{
	static const char *const n[5] = { "vram", "ram", "phycont", "cdlg", "newlib" };
	return i >= 0 && i < 5 ? n[i] : "?";
}
unsigned shepgl_pool_kib(int pool, int total)
{
	static const vglMemType t[5] = { VGL_MEM_VRAM, VGL_MEM_RAM, VGL_MEM_SLOW, VGL_MEM_BUDGET, VGL_MEM_EXTERNAL };
	if (pool < 0 || pool > 4 || g_state == 0) return 0;
	return (unsigned)((total ? vglMemTotal(t[pool]) : pool_free(t[pool])) / 1024);
}

int shepgl_shader_binary_size(GLuint shader)
{
	if (!shader) return 0;
	/* Not supported yet: vitaGL's vglGetShaderBinary serializes into a buffer it allocates itself when given NULL, and never
	   frees it, so asking only for the size leaks the whole serialized shader. Revisit with the precompiled-shader cache work. */
	(void)shader;
	return -1;
}

float shepgl_abi_probe(int32_t i, float a, float b, float c, float d, uint8_t flag)
{
	return (float)i + 2.0f * a + 3.0f * b + 5.0f * c + 7.0f * d + 11.0f * (float)flag;
}

/* ------------------------------------------------------------------ GXP cache support (device kit #5, track boot; shepgl.h) */
/* vitaGL's live compiler settings (custom_shaders.o / glsl_utils.o of this libvitaGL.a): what its glCompileShader hands
 * shark_compile_shader_extended (opt, fastmath, fastprecision, fastint) and how its GLSL translator binds semantics. */
extern unsigned char compiler_opts;
extern int compiler_fastmath, compiler_fastprecision, compiler_fastint;
extern int glsl_sema_mode;
extern unsigned char glsl_precision_low;
#ifndef GL_PROGRAM_BINARY_LENGTH
#define GL_PROGRAM_BINARY_LENGTH 0x8741
#endif
#ifndef GL_SHADER_TYPE
#define GL_SHADER_TYPE 0x8B4F
#endif
/* the GL stack this file is linked with: vita/shepherd/build.sh passes the sha256 of libvitaGL.a, libvitashark.a and
 * libSceShaccCgExt.a. Without it (another build, e.g. vita/gltest's) there is no identity and the engine keeps no cache. */
#ifndef SHEPGL_GLSTACK_ID
#define SHEPGL_GLSTACK_ID ""
#endif
#define SHEPGL_GXP_LAYOUT "vglGetShaderBinary u32 N, N x u32 uniform index (reversed for glShaderBinary), GXP; v1"

static char g_gxp_why[256];
const char *shepgl_gxp_why(void) { return g_gxp_why; }
#define GXP_REFUSE(...) do { snprintf(g_gxp_why, sizeof g_gxp_why, __VA_ARGS__); return 0; } while (0)

/* FNV-1a 64 over little-endian 32-bit words (the icalls.bin v2 scheme), a tail of 1-3 bytes zero-padded into one more word */
static uint64_t fnv_words(uint64_t h, const unsigned char *p, size_t n)
{
	size_t i = 0;
	for (; i + 4 <= n; i += 4) { uint32_t w; memcpy(&w, p + i, 4); h ^= w; h *= 1099511628211ull; }
	if (i < n) { uint32_t w = 0; memcpy(&w, p + i, n - i); h ^= w; h *= 1099511628211ull; }
	return h;
}
/* size + hash of one file, or -1 when it cannot be opened (absent) */
static long long hash_file(const char *path, uint64_t *hash)
{
	SceUID fd = sceIoOpen(path, SCE_O_RDONLY, 0);
	if (fd < 0) return -1;
	enum { CH = 64 * 1024 };   /* a multiple of 4: the word stream is the same whatever the chunking */
	unsigned char *b = (unsigned char *)malloc(CH);
	if (!b) { sceIoClose(fd); return -2; }
	uint64_t h = 1469598103934665603ull; long long size = 0; int r;
	while ((r = sceIoRead(fd, b, CH)) > 0) { h = fnv_words(h, b, (size_t)r); size += r; if (r < CH) break; }
	free(b);
	sceIoClose(fd);
	if (r < 0) return -2;
	*hash = h;
	return size;
}

int shepgl_gxp_identity(char *out, int cap, int *ms)
{
	uint64_t t0 = sceKernelGetProcessTimeWide();
	g_gxp_why[0] = 0;
	if (ms) *ms = 0;
	if (!out || cap < 64) { snprintf(g_gxp_why, sizeof g_gxp_why, "no buffer"); return -1; }
	if (!SHEPGL_GLSTACK_ID[0]) { snprintf(g_gxp_why, sizeof g_gxp_why, "this shepgl was built without the GL stack's build ids (vita/shepherd/build.sh passes them)"); return -1; }
	/* vitaGL's start_shader_compiler (gxm.o): shark_init(NULL) = vitaShaRK's default path first, then ur0:data/external/ */
	static const char *const paths[2] = { "ur0:data/libshacccg.suprx", "ur0:data/external/libshacccg.suprx" };
	char lib[2][96]; int found = 0;
	for (int i = 0; i < 2; i++) {
		uint64_t h = 0; long long sz = hash_file(paths[i], &h);
		if (sz == -2) { snprintf(g_gxp_why, sizeof g_gxp_why, "%s could not be read", paths[i]); return -1; }
		if (sz < 0) snprintf(lib[i], sizeof lib[i], "%s absent", paths[i]);
		else { found++; snprintf(lib[i], sizeof lib[i], "%s %lld bytes fnv64 %016llx", paths[i], sz, (unsigned long long)h); }
	}
	if (!found) { snprintf(g_gxp_why, sizeof g_gxp_why, "no libshacccg.suprx in ur0:data/ or ur0:data/external/"); return -1; }
	int n = snprintf(out, (size_t)cap, "GL stack %s | libshacccg: %s; %s | vitaGL compiler opt %d fastmath %d fastprecision %d fastint %d, semantic mode %d, low precision %d | blob: %s",
	                 SHEPGL_GLSTACK_ID, lib[0], lib[1], (int)compiler_opts, compiler_fastmath, compiler_fastprecision, compiler_fastint,
	                 glsl_sema_mode, (int)glsl_precision_low, SHEPGL_GXP_LAYOUT);
	if (ms) *ms = (int)((sceKernelGetProcessTimeWide() - t0) / 1000);
	if (n <= 0 || n >= cap) { snprintf(g_gxp_why, sizeof g_gxp_why, "identity longer than %d bytes", cap); return -1; }
	return n;
}

/* kit #6 integration: the blob's GXP part may be the GXP padded to 4 bytes (shepgl_gxp_blob.h gxpb_gxp_fits; this vitaGL does that on the
 * device). SHEPHERD_K6_GXP_PAD=0 (env file) = kit #5's exact-size check (every device blob refused: no cache). g_gxp_size: the last checked
 * blob's GXP size (shepgl_gxp_save zeroes the padding after it). */
static int g_gxp_pad = -1;
static uint32_t g_gxp_size;
static int gxp_pad_on(void)
{
	if (g_gxp_pad < 0) {
		const char *e = getenv("SHEPHERD_K6_GXP_PAD");
		g_gxp_pad = !(e && !strcmp(e, "0"));
	}
	return g_gxp_pad;
}

int shepgl_gxp_check(const void *buf, int len, int type)
{
	g_gxp_why[0] = 0;
	g_gxp_size = 0;
	uint32_t n, glen;
	const char *bad = gxpb_split(buf, len, &n, &glen);
	if (bad) GXP_REFUSE("%s (%d bytes)", bad, len);
	/* sceGxm reads the program in place: check an aligned copy (the caller's bytes can sit anywhere) */
	void *g = malloc(glen);
	if (!g) GXP_REFUSE("out of memory (%u bytes)", (unsigned)glen);
	memcpy(g, (const char *)buf + 4 + 4 * n, glen);
	const SceGxmProgram *pg = (const SceGxmProgram *)g;
	int ok = 0;
	if (memcmp(g, "GXP", 4) != 0) snprintf(g_gxp_why, sizeof g_gxp_why, "no GXP header");
	else if (!gxpb_gxp_fits(sceGxmProgramGetSize(pg), glen, gxp_pad_on()))
		snprintf(g_gxp_why, sizeof g_gxp_why, "the GXP says %u bytes, the blob holds %u%s", sceGxmProgramGetSize(pg), (unsigned)glen,
		         gxp_pad_on() ? "" : " (SHEPHERD_K6_GXP_PAD=0: exact sizes only, as kit #5)");
	else if (sceGxmProgramCheck(pg) != 0) snprintf(g_gxp_why, sizeof g_gxp_why, "sceGxmProgramCheck refused it (0x%08x)", (unsigned)sceGxmProgramCheck(pg));
	else if ((int)sceGxmProgramGetType(pg) != (type ? SCE_GXM_FRAGMENT_PROGRAM : SCE_GXM_VERTEX_PROGRAM))
		snprintf(g_gxp_why, sizeof g_gxp_why, "a %s program where a %s one was expected", sceGxmProgramGetType(pg) == SCE_GXM_FRAGMENT_PROGRAM ? "fragment" : "vertex", type ? "fragment" : "vertex");
	else {
		unsigned pc = sceGxmProgramGetParameterCount(pg); ok = 1;
		for (uint32_t i = 0; i < n && ok; i++) {
			uint32_t ix; memcpy(&ix, (const char *)buf + 4 + 4 * i, 4);
			if (ix >= pc) { ok = 0; snprintf(g_gxp_why, sizeof g_gxp_why, "uniform index %u of a program with %u parameters", (unsigned)ix, pc); }
		}
		/* the compile keeps the names of uniform-BUFFER parameters in a list vitaGL's binary format does not carry (glLinkProgram binds
		   them by that name): such a shader would link differently from a binary, so it is never cached */
		for (unsigned i = 0; i < pc && ok; i++) {
			const SceGxmProgramParameter *pp = sceGxmProgramGetParameter(pg, i);
			if (!pp) { ok = 0; snprintf(g_gxp_why, sizeof g_gxp_why, "parameter %u unreadable", i); }
			else if (sceGxmProgramParameterGetCategory(pp) == SCE_GXM_PARAMETER_CATEGORY_UNIFORM_BUFFER) { ok = 0; snprintf(g_gxp_why, sizeof g_gxp_why, "a uniform-buffer parameter (not kept by vitaGL's binary format)"); }
		}
		if (ok) g_gxp_size = sceGxmProgramGetSize(pg);
	}
	free(g);
	return ok;
}

int shepgl_gxp_save(GLuint program, GLuint shader, int type, void *buf, int cap)
{
	g_gxp_why[0] = 0;
	if (!program || !shader || !buf) GXP_REFUSE("no program, shader or buffer");
	/* vglGetShaderBinary ignores its bufSize; GL_PROGRAM_BINARY_LENGTH (140 + both GXPs + 4 x (both index counts + 2)) is more than
	   either shader's blob (4 + 4 x its index count + its GXP), so a buffer that large always holds it */
	GLint bound = 0;
	glGetProgramiv(program, GL_PROGRAM_BINARY_LENGTH, &bound);
	if (bound < 140 + 64) GXP_REFUSE("GL_PROGRAM_BINARY_LENGTH %d: the program has no linked shaders", (int)bound);
	if (cap < bound) return -bound;
	GLsizei len = 0;
	vglGetShaderBinary(shader, cap, &len, buf);
	if (len <= 0 || len > bound) GXP_REFUSE("vglGetShaderBinary gave %d bytes (at most %d expected)", (int)len, (int)bound);
	uint32_t n, glen;
	const char *bad = gxpb_split(buf, (int)len, &n, &glen);
	if (bad) GXP_REFUSE("%s (%d bytes from vglGetShaderBinary)", bad, (int)len);
	gxpb_reverse(buf, n);   /* so glShaderBinary rebuilds the compiled uniform list in its order (shepgl_gxp_blob.h) */
	if (!shepgl_gxp_check(buf, (int)len, type)) return 0;
	gxpb_zero_pad(buf, n, g_gxp_size, glen);   /* kit #6 integration: the 0..3 padding bytes canonical (zero) in the stored / compared copy */
	return (int)len;
}

int shepgl_gxp_load(GLuint shader, int type, const void *buf, int len)
{
	if (!shepgl_gxp_check(buf, len, type)) return 0;
	GLint st = 0;
	glGetShaderiv(shader, GL_SHADER_TYPE, &st);
	if (st != (GLint)(type ? GL_FRAGMENT_SHADER : GL_VERTEX_SHADER)) GXP_REFUSE("shader %u is of type 0x%x, not a %s shader", shader, (unsigned)st, type ? "fragment" : "vertex");
	glShaderBinary(1, &shader, 0, buf, len);   /* vitaGL: unserialize_shader(buf, len, shader): the GXP copied + registered, the list rebuilt */
	GLint ok = 0;
	glGetShaderiv(shader, GL_COMPILE_STATUS, &ok);   /* vitaGL (SHADER_PAIR): the shader has a program */
	if (!ok) GXP_REFUSE("vitaGL took the blob but its shader has no program");
	objlog *o = find_log('s', shader);   /* no compiler output belongs to it: a log kept for an earlier object of that name goes */
	if (o) { free(o->log); o->log = NULL; }
	return 1;
}

/* ------------------------------------------------------------------ device kit #7 (track probes): the GPU transfer unit probe
 * (roadmap "GPU transfer unit probe": the Vita's GPU has a 2D transfer unit -- sceGxmTransferCopy / Fill / Downscale -- that moves and
 * re-lays-out image data without the CPU; does it swizzle textures and copy buffers faster than the CPU or sceDmacMemcpy on 3.65?).
 * Called by the measurement app's host (vita/shepherd/k7_probes.inc.c) after its run, with vitaGL up and no frame in flight. Measurement
 * only: 2 x 1 MiB from vitaGL's VRAM pool (vglAlloc) + one 1 MiB uncached main-memory block mapped for the GPU, all freed again; no GL
 * call, no frame shown. 512 x 512 RGBA8 images; each GPU figure = 16 transfers submitted, then sceGxmTransferFinish (no sync object,
 * no notification: the transfer queue's own order). The swizzled / downscaled results are compared byte for byte with the CPU's (both
 * Morton orders tried; both downscale roundings tried). Not under vlx (its GL stand-in answers 0). Returns 1 = ran, -1 = stopped. */
#include <psp2/kernel/sysmem.h>
#include <psp2/kernel/dmac.h>
#include <stdarg.h>
#include <malloc.h>
#define K7W 512u
#define K7H 512u
#define K7SZ (K7W * K7H * 4u)
static void k7_line(void (*line)(const char *), const char *fmt, ...)
{
	char b[400];
	va_list ap;
	va_start(ap, fmt);
	vsnprintf(b, sizeof b, fmt, ap);
	va_end(ap);
	line(b);
}
static uint32_t k7_morton(uint32_t x, uint32_t y, int ylow)
{
	uint32_t r = 0;
	for (int i = 0; i < 10; i++) {
		uint32_t xb = (x >> i) & 1u, yb = (y >> i) & 1u;
		r |= ylow ? (yb << (2 * i)) | (xb << (2 * i + 1)) : (xb << (2 * i)) | (yb << (2 * i + 1));
	}
	return r;
}
static double k7_mibs(SceUInt64 t0, unsigned bytes)
{
	double s = (double)(sceKernelGetProcessTimeWide() - t0) / 1e6;
	return s > 0 ? bytes / 1048576.0 / s : 0;
}
int sgl_k7_transfer_probe(void (*line)(const char *))
{
	if (!gxm_context) { line("PRB gpu: vitaGL's GXM context is not up: skipped"); return -1; }
	uint8_t *v0 = (uint8_t *)vglAlloc(K7SZ, VGL_MEM_VRAM), *v1 = (uint8_t *)vglAlloc(K7SZ, VGL_MEM_VRAM);
	uint8_t *ram = (uint8_t *)memalign(64, K7SZ), *ref = (uint8_t *)memalign(64, K7SZ);
	SceUID nb = sceKernelAllocMemBlock("shepherd-probe-gxm", SCE_KERNEL_MEMBLOCK_TYPE_USER_MAIN_NC_RW, K7SZ, NULL);
	void *nm = NULL;
	int mapped = -1;
	if (nb >= 0 && sceKernelGetMemBlockBase(nb, &nm) >= 0) mapped = sceGxmMapMemory(nm, K7SZ, SCE_GXM_MEMORY_ATTRIB_RW);
	if (!v0 || !v1 || !ram || !ref || mapped < 0) {
		k7_line(line, "PRB gpu: no buffers (VRAM %p %p, RAM %p %p, uncached block 0x%08x mapped 0x%08x): skipped", (void *)v0, (void *)v1, (void *)ram,
		        (void *)ref, (unsigned)nb, (unsigned)mapped);
		goto out;
	}
	uint8_t *nc = (uint8_t *)nm;
	for (unsigned i = 0; i < K7SZ; i++) ram[i] = (uint8_t)(i * 2654435761u >> 13);
	memcpy(nc, ram, K7SZ);
	const unsigned N = 16, ST = K7W * 4u;
	SceUInt64 t0;
	/* the CPU and the DMA engine */
	t0 = sceKernelGetProcessTimeWide(); for (unsigned i = 0; i < N; i++) memcpy(ref, ram, K7SZ); double cpu_rr = k7_mibs(t0, N * K7SZ);
	t0 = sceKernelGetProcessTimeWide(); for (unsigned i = 0; i < N; i++) memcpy(v0, ram, K7SZ); double cpu_rv = k7_mibs(t0, N * K7SZ);
	t0 = sceKernelGetProcessTimeWide(); for (unsigned i = 0; i < 4; i++) memcpy(ref, v0, K7SZ); double cpu_vr = k7_mibs(t0, 4 * K7SZ);
	t0 = sceKernelGetProcessTimeWide(); for (unsigned i = 0; i < N; i++) memcpy(v0, nc, K7SZ); double cpu_nv = k7_mibs(t0, N * K7SZ);
	t0 = sceKernelGetProcessTimeWide(); int dr = 0; for (unsigned i = 0; i < N; i++) dr |= sceDmacMemcpy(v0, ram, K7SZ); double dma_rv = k7_mibs(t0, N * K7SZ);
	t0 = sceKernelGetProcessTimeWide(); for (unsigned i = 0; i < N; i++) memset(v0, (int)i, K7SZ); double cpu_fill = k7_mibs(t0, N * K7SZ);
	k7_line(line, "PRB gpu CPU / DMA (1 MiB, x%u): memcpy RAM->RAM %.0f MiB/s, RAM->VRAM %.0f, uncached RAM->VRAM %.0f, VRAM->RAM %.0f (CPU reads of video "
	        "memory); sceDmacMemcpy RAM->VRAM %.0f MiB/s%s; memset VRAM %.0f MiB/s", N, cpu_rr, cpu_rv, cpu_nv, cpu_vr, dma_rv, dr < 0 ? " (refused)" : "", cpu_fill);
	/* the transfer unit */
	const SceGxmTransferFormat F = SCE_GXM_TRANSFER_FORMAT_U8U8U8U8_ABGR;
	int rc = 0;
#define K7COPY(dt, src, dst) sceGxmTransferCopy(K7W, K7H, 0, 0, SCE_GXM_TRANSFER_COLORKEY_NONE, F, SCE_GXM_TRANSFER_LINEAR, (src), 0, 0, (int32_t)ST, F, (dt), (dst), 0, 0, (int32_t)ST, NULL, 0, NULL)
	t0 = sceKernelGetProcessTimeWide(); for (unsigned i = 0; i < N && rc >= 0; i++) rc = K7COPY(SCE_GXM_TRANSFER_LINEAR, nc, v0); sceGxmTransferFinish();
	double g_lin_nv = k7_mibs(t0, N * K7SZ);
	if (rc < 0) { k7_line(line, "PRB gpu: sceGxmTransferCopy refused (0x%08x): the transfer unit is not usable this way; stopped", (unsigned)rc); goto out; }
	t0 = sceKernelGetProcessTimeWide(); for (unsigned i = 0; i < N && rc >= 0; i++) rc = K7COPY(SCE_GXM_TRANSFER_LINEAR, v0, v1); sceGxmTransferFinish();
	double g_lin_vv = k7_mibs(t0, N * K7SZ);
	t0 = sceKernelGetProcessTimeWide(); for (unsigned i = 0; i < N && rc >= 0; i++) rc = K7COPY(SCE_GXM_TRANSFER_SWIZZLED, nc, v1); sceGxmTransferFinish();
	double g_swz = k7_mibs(t0, N * K7SZ);
	int rc_swz = rc;
	/* which Morton order the GPU wrote (texel = 4 bytes) */
	int match = -1;
	memcpy(ref, v1, K7SZ);   /* the GPU's swizzled image, into RAM */
	for (int ylow = 0; ylow < 2 && match < 0; ylow++) {
		int ok = 1;
		for (uint32_t y = 0; y < K7H && ok; y++)
			for (uint32_t x = 0; x < K7W; x++)
				if (memcmp(ref + 4u * k7_morton(x, y, ylow), ram + 4u * (y * K7W + x), 4)) { ok = 0; break; }
		if (ok) match = ylow;
	}
	/* the CPU's swizzle into VRAM (the order the GPU used, else x-low) */
	t0 = sceKernelGetProcessTimeWide();
	for (unsigned r = 0; r < 4; r++)
		for (uint32_t y = 0; y < K7H; y++)
			for (uint32_t x = 0; x < K7W; x++)
				memcpy(v0 + 4u * k7_morton(x, y, match == 1), ram + 4u * (y * K7W + x), 4);
	double cpu_swz = k7_mibs(t0, 4 * K7SZ);
	rc = rc_swz >= 0 ? 0 : rc_swz;
	t0 = sceKernelGetProcessTimeWide(); for (unsigned i = 0; i < N && rc >= 0; i++) rc = K7COPY(SCE_GXM_TRANSFER_TILED, nc, v1); sceGxmTransferFinish();
	double g_tile = k7_mibs(t0, N * K7SZ);
	int rc_tile = rc;
	rc = 0;
	t0 = sceKernelGetProcessTimeWide(); for (unsigned i = 0; i < N && rc >= 0; i++) rc = sceGxmTransferFill(0xff336699u, F, v1, 0, 0, K7W, K7H, (int32_t)ST, NULL, 0, NULL); sceGxmTransferFinish();
	double g_fill = k7_mibs(t0, N * K7SZ);
	int rc_fill = rc;
	k7_line(line, "PRB gpu transfer unit (512 x 512 RGBA8, x%u): copy uncached RAM->VRAM %.0f MiB/s, VRAM->VRAM %.0f; linear->SWIZZLED %.0f MiB/s%s vs the "
	        "CPU's swizzle %.0f MiB/s; linear->TILED %.0f MiB/s%s; fill %.0f MiB/s%s (CPU memset %.0f) | the GPU's swizzled layout %s",
	        N, g_lin_nv, g_lin_vv, g_swz, rc_swz < 0 ? " (refused)" : "", cpu_swz, g_tile, rc_tile < 0 ? " (refused)" : "", g_fill, rc_fill < 0 ? " (refused)" : "",
	        cpu_fill, rc_swz < 0 ? "not tested" : match == 0 ? "= Morton order, x in the low bit: identical to the CPU's byte for byte"
	        : match == 1 ? "= Morton order, y in the low bit: identical to the CPU's byte for byte" : "matches neither Morton order (another layout: not exact with this CPU code)");
	/* 2x downscale (box): 512 x 512 -> 256 x 256 */
	rc = 0;
	t0 = sceKernelGetProcessTimeWide();
	for (unsigned i = 0; i < N && rc >= 0; i++) rc = sceGxmTransferDownscale(F, nc, 0, 0, K7W, K7H, (int)ST, F, v1, 0, 0, (int)(ST / 2), NULL, 0, NULL);
	sceGxmTransferFinish();
	double g_down = k7_mibs(t0, N * K7SZ);
	if (rc < 0) k7_line(line, "PRB gpu downscale: refused (0x%08x)", (unsigned)rc);
	else {
		memcpy(ref, v1, K7SZ / 4);
		unsigned maxd[2] = { 0, 0 }, diff[2] = { 0, 0 };
		t0 = sceKernelGetProcessTimeWide();
		for (uint32_t y = 0; y < K7H / 2; y++)
			for (uint32_t x = 0; x < K7W / 2; x++)
				for (int c = 0; c < 4; c++) {
					const uint8_t *s = ram + 4u * ((2 * y) * K7W + 2 * x) + c;
					unsigned sum = s[0] + s[4] + s[ST] + s[ST + 4], g = ref[4u * (y * (K7W / 2) + x) + c];
					for (int m = 0; m < 2; m++) {
						unsigned cpu = m ? (sum + 2) / 4 : sum / 4, d = cpu > g ? cpu - g : g - cpu;
						if (d) diff[m]++;
						if (d > maxd[m]) maxd[m] = d;
					}
				}
		double cpu_down = k7_mibs(t0, K7SZ);
		k7_line(line, "PRB gpu downscale 2x (box) 512 x 512 -> 256 x 256: %.0f MiB/s of source (x%u) vs the CPU's %.0f MiB/s | vs the CPU's (a+b+c+d)/4: %u "
		        "channel values differ (max %u); vs (a+b+c+d+2)/4: %u differ (max %u)%s", g_down, N, cpu_down, diff[0], maxd[0], diff[1], maxd[1],
		        !diff[0] || !diff[1] ? " -- identical with one of them" : "");
	}
	k7_line(line, "PRB gpu verdict: the transfer unit swizzles at %.0f MiB/s vs the CPU's %.0f (%.1fx) and copies RAM->VRAM at %.0f vs the CPU's %.0f and "
	        "sceDmacMemcpy's %.0f MiB/s", g_swz, cpu_swz, cpu_swz > 0 ? g_swz / cpu_swz : 0, g_lin_nv, cpu_nv, dma_rv);
	if (mapped >= 0) sceGxmUnmapMemory(nm);
	sceKernelFreeMemBlock(nb);
	vglFree(v0); vglFree(v1); free(ram); free(ref);
	return 1;
out:
	if (mapped >= 0) sceGxmUnmapMemory(nm);
	if (nb >= 0) sceKernelFreeMemBlock(nb);
	if (v0) vglFree(v0);
	if (v1) vglFree(v1);
	free(ram); free(ref);
	return -1;
}
