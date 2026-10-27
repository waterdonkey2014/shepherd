/* shepgl -- the GL host layer for Shepherd's GlBackend on the PS Vita (vitaGL), as a static library (libshepgl.a).
 *
 * What it gives the C# side:
 *   1. the 51 entry points of engine/Shepherd.Engine/Render/Gl.cs under stable names, resolved at LINK time (a missing one is
 *      a link error, not a run-time surprise). Gl.Load takes a Func<string, IntPtr>; on the Vita that is shepgl_proc:
 *        [DllImport("__Internal")] static extern IntPtr shepgl_proc(string name);      // UTF-8 marshalling (V5 checked it)
 *        var missing = Gl.Load(shepgl_proc, core: false);                              // 0 missing == every entry bound
 *      shepgl_proc returns the function itself (no per-call wrapper) for 44 of them and a thin fix-up wrapper for 7 whose
 *      vitaGL version is incomplete (see shepgl_entries.h and vita/notes/gltest.md): the shader/program info logs (stubs
 *      in vitaGL) and glPixelStorei (vitaGL knows only GL_UNPACK_ROW_LENGTH).
 *      The host must put every shepgl_* symbol into its static P/Invoke table (vita/shepherd/build.sh: add "shepgl_" to
 *      the static-symbols regex) and link libshepgl.a + vitaGL.
 *   2. the context (shepgl_init: vglInitExtended at 960x544, MSAA off), the swap (shepgl_swap), the vsync policy, and the
 *      queries GlBackend took from SDL (extension check, compiler presence, pool sizes).
 *
 * ABI: arm-vita-eabi is 32-bit AAPCS with hard float (VFP). C# int/uint/nint = 32-bit core registers, byte = GLboolean,
 * float = VFP s-registers. tools/check_abi.py checks every Gl.cs signature against vitaGL.h by ABI class.
 * shepgl_abi_probe is a test hook for the managed side: call it through the same delegate* unmanaged path Gl uses to prove
 * float arguments arrive in the right registers before any GL call depends on it.
 *
 * The same API is implemented for macOS (CGL, legacy 2.1) in shepgl_mac.c, so vita/gltest can check its own logic on the Mac
 * before a device run, and (Stage B) so the engine host's Mac rehearsal (vita/shepherd/mac-check/run-gl.sh) drives GlBackend
 * through exactly this API with the GLSL ES 1.00 dialect: the Mac table's glShaderSource rewrites a "#version 100" prelude
 * for Apple's legacy compiler (shepgl_mac.c). It is not a product path (the Mac engine keeps SDL3). */
#ifndef SHEPGL_H
#define SHEPGL_H

#include <stddef.h>
#include <stdint.h>

#ifdef __vita__
#include <vitaGL.h>
#else
#define GL_SILENCE_DEPRECATION 1
#include <OpenGL/gl3.h>
#endif

#ifdef __cplusplus
extern "C" {
#endif

#define SHEPGL_ABI_VERSION 1
#define SHEPGL_COUNT 51

/* ---- entry-point table (Gl.cs order) ---- */
int shepgl_count(void);                    /* 51 */
const char *shepgl_name(int index);        /* "glGetString" ... ; NULL out of range */
void *shepgl_fn(int index);                /* the function to call; NULL out of range */
int shepgl_is_wrapped(int index);          /* 1 = the table hands out a shepgl fix-up wrapper for this name */
/* Gl.Load's getProc: the table entry for a GL name, plus "shepgl_abi_probe". NULL for anything else, including the "...EXT"
 * FBO names Gl.Load tries only when the plain name is missing (it never is here). */
void *shepgl_proc(const char *name);

/* ---- context ---- */
typedef struct {
	int width, height;          /* 960 x 544 */
	int ram_threshold;          /* bytes of free USER_RW main RAM vitaGL must leave untouched at init (see below) */
	int msaa;                   /* 0 = SCE_GXM_MULTISAMPLE_NONE (the only value Shepherd uses) */
	int vsync;                  /* initial vglWaitVblankStart state */
	int semantic_mode;          /* SHEPGL_SEMA_* */
	int opt, fastmath, fastprecision, fastint;   /* vglSetupRuntimeShaderCompiler (shark_opt 0..4, flags 0/1) */
	int gc_affinity;            /* 0 = vitaGL's own (default affinity); else a SCE_KERNEL_CPU_MASK_USER_* for vitaGL's garbage-collector
	                               thread, set by vglSetupGarbageCollector(SHEPGL_GC_PRIORITY, mask) before vglInit (kit 3 core pinning) */
	int param_buffer_kib;       /* kit5 mem G7: 0 = vitaGL's own GXM parameter buffer (16 MiB of CDRAM, outside its VRAM pool); else
	                               vglSetParamBufferSize(KiB * 1024) before vglInit: the CDRAM it no longer takes joins the VRAM pool */
	int no_vgl_splash;          /* kit #8 boot track (the clean boot screen): 1 = vitaGL's own animated splash is left out (the GL link
	                               wraps vitaGL's invoke_splashscreen: -Wl,--wrap=invoke_splashscreen); 0 = vitaGL's splash (kit #7) */
} shepgl_config;
/* vitaGL's own garbage-collector thread priority (gxm.o .data gc_thread_priority of this libvitaGL.a), kept when only the affinity changes */
#define SHEPGL_GC_PRIORITY 0x10000100

/* vglInitExtended(legacy_pool=0, w, h, ram_threshold, msaa) == vglInitWithCustomThreshold(.., ram_threshold, cdram 0,
 * phycont 0, cdlg 0x8C6000, msaa): vitaGL's RAM pool = free USER_RW at that moment - ram_threshold (0 if the threshold is
 * larger), its CDRAM and PHYCONT pools take ALL that is free, and the common-dialog pool is 0 (disassembled from this
 * libvitaGL.a; vita/notes/gltest.md section 3). Nothing can allocate CDRAM/PHYCONT memblocks after shepgl_init. So ram_threshold must cover every
 * USER_RW memblock the process allocates AFTER shepgl_init: thread stacks, sceIo buffers, a Mono arena if it is not
 * allocated yet, a safety margin. Default 16 MiB (vita/gltest: no later memblocks). Shepherd's engine host: allocate the
 * Mono arena first, then threshold = 26 MiB (V6 partition: 20 MiB stacks + 6 safety), and shrink the arena by the GL RAM
 * budget measured by gltest ("VGL pools" lines). */
#define SHEPGL_DEFAULT_RAM_THRESHOLD (16 * 1024 * 1024)

#define SHEPGL_SEMA_SHADER_PAIR 0   /* VGL_MODE_SHADER_PAIR: shacccg runs inside glCompileShader (errors surface there) */
#define SHEPGL_SEMA_GLOBAL      1
#define SHEPGL_SEMA_POSTPONED   2   /* vitaGL's default: glCompileShader does nothing, shacccg runs in glLinkProgram */

void shepgl_default_config(shepgl_config *c);   /* 960x544, 16 MiB, MSAA off, vsync on, SHADER_PAIR, FAST/1/0/1 */
int shepgl_init(const shepgl_config *c);        /* 1 = context + shader compiler online; -1 = context up, no libshacccg; 0 = failed (shepgl_status) */
const char *shepgl_status(void);                /* one line about the last init (for the host log) */
/* ---- the engine host (vita/shepherd GL variant, Stage B; vita/notes/m1-gl-app.md) ----
 * The native host calls shepgl_init once, after it has sized the Mono arena; the engine's GlBackend (SHEPHERD_GL_HOST=shepgl)
 * then binds through shepgl_proc on the Mono thread and never initializes anything itself. */
int shepgl_state(void);                         /* what the last shepgl_init returned (1 / -1 / 0); 0 before any init */
/* Make the context usable on the calling thread: a no-op on the Vita (vitaGL has one global context, any thread may use it,
 * one at a time); CGLSetCurrentContext on the Mac rehearsal host. 1 = ok, 0 = no context (shepgl_init not called / failed). */
int shepgl_attach(void);
/* vitaGL pool numbers in KiB (pool = 0 vram/CDRAM, 1 ram/USER_RW, 2 phycont, 3 cdlg, 4 newlib), for log lines that must not
 * depend on size_t's width (the managed side reads them as 32-bit). total = 0 -> free, 1 -> total. 0 on the Mac. */
unsigned shepgl_pool_kib(int pool, int total);
unsigned shepgl_swap_count(void);               /* shepgl_swap calls so far (frames handed to the display) */
int shepgl_compiler_online(void);               /* vitaGL's is_shark_online (libshacccg loaded) */
void shepgl_set_compiler(int opt, int fastmath, int fastprecision, int fastint);   /* affects later compiles */
void shepgl_set_semantic_mode(int mode);
/* Vsync policy: on (default) for play -- vglSwapBuffers then waits for vblank, triple buffering keeps the CPU up to two frames
 * ahead; off only for measurements (a frame time is then not quantised to 16.7 ms). */
void shepgl_set_vsync(int on);
void shepgl_swap(void);                         /* vglSwapBuffers(GL_FALSE): no common dialog in Shepherd */
/* kit #8 boot track (the clean boot screen): the display cleared to vitaGL's default clear colour (black) and presented once -- one
 * vglSwapBuffers OUTSIDE shepgl_swap's count and with no GL state left changed (the default framebuffer, the full viewport, no scissor and
 * the clear colour (0,0,0,0) are the GL defaults the engine starts from). 1 = presented, 0 = no context. vlx's GL record does not see it. */
int shepgl_boot_frame(void);
/* kit #8 boot track (the engine's real video, Runtime/K8Video.cs + host.c's shepherd_vita_video_*): memory from vitaGL's VRAM pool for the
 * clip and the hardware decoder's work area (vglAlloc(VGL_MEM_VRAM), aligned up to `align` inside the block: CDRAM, physically contiguous);
 * NULL when the pool has no room. shepgl_vram_free takes the pointer shepgl_vram_alloc returned. */
void *shepgl_vram_alloc(unsigned bytes, unsigned align);
void shepgl_vram_free(void *p);
/* the data pointer of texture `name` (vglGetTexDataPointer; the GL_TEXTURE_2D binding is restored): where the decoder writes its pictures. */
void *shepgl_tex_data(GLuint name);
int shepgl_has_extension(const char *name);     /* exact token match in GL_EXTENSIONS (replaces SDL_GL_ExtensionSupported) */
GLuint shepgl_display_fbo(void);                /* 0 on the Vita (the display); an offscreen FBO on the Mac test host */

typedef struct { size_t free_[5], total[5]; } shepgl_meminfo;   /* VRAM(CDRAM), RAM(USER_RW), PHYCONT, CDLG, newlib */
void shepgl_mem(shepgl_meminfo *m);
const char *shepgl_pool_name(int pool);

/* ---- shader diagnostics ----
 * vitaGL's glGetShaderInfoLog / glGetProgramInfoLog are empty stubs and INFO_LOG_LENGTH reads 0. shepgl installs a libshacccg
 * log callback (shark_install_log_cb) and keeps what the compiler said during each glCompileShader / glLinkProgram; the
 * wrapped GetShaderiv/GetShaderInfoLog/GetProgramiv/GetProgramInfoLog answer from it, so GlBackend's existing logging works
 * unchanged. Line numbers refer to vitaGL's translated Cg text, which starts with its own ~200-line header. */
const char *shepgl_last_diagnostics(void);      /* everything the compiler said during the last compile/link call */
typedef void (*shepgl_line_fn)(const char *line);
void shepgl_set_log(shepgl_line_fn fn);         /* optional: every compiler diagnostic line also goes here */
/* Size of the compiled GXP of a shader object (vglGetShaderBinary), 0 if none (not compiled yet / Mac). */
int shepgl_shader_binary_size(GLuint shader);

/* ---- GXP cache support (device kit #5, track boot; engine/Shepherd.Engine/Render/GxpCache.cs keeps the file) ----
 * vitaGL compiles GlBackend's 8 programs with libshacccg at EVERY start (device kit #4: 7.0-7.7 s). These calls let the engine keep
 * the compiled shaders in the owner folder and hand them back on the next start, through vitaGL's own shader binary path:
 *   vglGetShaderBinary (serialize_shader) writes  u32 N | N x u32 uniform parameter index | the GXP program (sceGxm's format)
 *   glShaderBinary (unserialize_shader) reads the same layout back into a fresh shader object: it copies the GXP, registers it with
 *   the shader patcher and rebuilds the uniform list -- the exact state glCompileShader leaves (custom_shaders.o of this
 *   libvitaGL.a, disassembled: vita/notes/kit5-boot.md), with two differences handled here: the reader pushes the indices in the
 *   opposite order (so a blob from shepgl_gxp_save carries them reversed and the rebuilt list has the compile's order), and the
 *   uniform-BUFFER name list the compile keeps is not in the format (a shader with a uniform-buffer parameter is refused).
 * The blobs never leave the owner's card (owner-local, like the pack). */
/* One text line naming everything a compiled GXP depends on besides the GLSL text: vitaGL's and vitaShaRK's builds (sha256 of the
 * linked libvitaGL.a / libvitashark.a, from build.sh), libshacccg (each file vitaGL may load, in its order: size + FNV-1a 64 of its
 * bytes, or "absent"), vitaGL's live compiler options (opt / fastmath / fastprecision / fastint), the semantic mode, the low-precision
 * flag and the blob layout version. Returns its length, or -1 (unavailable: shepgl_gxp_why). ms = the time it took (file hashing). */
int shepgl_gxp_identity(char *out, int cap, int *ms);
/* The serialized compiled shader (vertex 0 / fragment 1 = type) of a shader object that is attached to `program` and linked:
 * written to buf in the load order (see above), checked like shepgl_gxp_check. Returns the length, -n when cap is smaller than the
 * n bytes it may need, 0 when refused (shepgl_gxp_why). Reads vitaGL's state only. */
int shepgl_gxp_save(GLuint program, GLuint shader, int type, void *buf, int cap);
/* 1 when buf[0..len) is a blob shepgl_gxp_save could have written for a shader of that type: N and the length agree, the GXP passes
 * sceGxmProgramCheck, its size field is the rest of the blob, its type is the one asked for, every index names a parameter of it,
 * and it has no uniform-buffer parameter. Pure parsing (no GL state). 0 = refused (shepgl_gxp_why). */
int shepgl_gxp_check(const void *buf, int len, int type);
/* shepgl_gxp_check, then glShaderBinary into `shader` (a glCreateShader'd object of that type, nothing else done to it): 1 = loaded
 * (vitaGL then reports COMPILE_STATUS true and the shader links like a compiled one), 0 = refused (shepgl_gxp_why). */
int shepgl_gxp_load(GLuint shader, int type, const void *buf, int len);
const char *shepgl_gxp_why(void);               /* why the last shepgl_gxp_* call refused (empty when it did not) */

/* ---- test hooks ---- */
float shepgl_abi_probe(int32_t i, float a, float b, float c, float d, uint8_t flag);   /* = i + 2a + 3b + 5c + 7d + 11*flag */
/* vita/gltest only: vitaGL's own glPixelStorei, to log the gap the wrapper hides (NULL on the Mac). */
void *shepgl_raw(const char *name);

#ifdef __cplusplus
}
#endif
#endif
