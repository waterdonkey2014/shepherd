/* SHEPHERD-VITA V6 Stage B: native host of Shepherd's headless menu boot on the PS Vita (shepherd_menu.vpk).
 *
 * Runs Shepherd.Engine's Main ("boot 1460 0": 1460 frames, no screenshots) on the forked .NET 8 Mono (interpreter +
 * static AOT, coop GC), with the Vita profile, the null renderer and the strict menu pack, from:
 *   app0:managed/                    (VPK) the linux-arm 8.0.31 BCL IL, the engine (Shepherd.Engine/Assets/Hooks) and
 *                                    Microsoft.Bcl.AsyncInterfaces; CoreLib + a BCL subset + Shepherd.Assets/Hooks are
 *                                    statically AOT-compiled into this eboot
 *   ux0:data/shepherd/managed/       (owner-copied) the game's Managed DLLs with runtime/ManagedVita laid over them
 *   ux0:data/shepherd/pack/          (owner-copied) shepherd.pack
 *   ux0:data/shepherd/data/          (owner-copied) RuntimeInitializeOnLoads.json + StreamingAssets/aa/{settings,catalog}.json
 *   ux0:data/shepherd/persistent/    the sandbox saves (Application.persistentDataPath; never the real CotL saves)
 *   ux0:data/shepherd/pad.txt        pad script, e.g. "1050:cross,1400:down"
 *   ux0:data/shepherd/env.txt        optional KEY=VALUE overrides (setenv before anything else)
 *   ux0:data/shepherd/args.txt       optional engine arguments (default "boot 1460 0")
 * Log: ux0:data/shepherd/menu.txt, one open/append/close per line (a crash or hang keeps every line before it); the
 *   previous run's log is kept as menu.prev.txt (renamed at start, before the fresh menu.txt is created).
 * The engine's own logs (engine.log, simstats*, native-resolve.txt, assemblies.txt, missing-icalls.txt, audio-events.tsv) go to
 *   ux0:data/shepherd/runtime/logs/<app>/, <app> = this build's log name without ".txt" (menu, menu-diag, menu-aot, menu_gl,
 *   menu_gl_audio): SHEPHERD_LOG_DIR, set for the engine (Program.LogDir), so one app no longer truncates another app's engine.log
 *   (device kit #2 review; before, every app wrote runtime/logs/engine.log). The mac-check build keeps runtime/logs.
 *
 * Memory partition (vita/notes/v6-vita-build.md): ATTRIBUTE2=12 gives the process ~365 MiB of main memory.
 *   eboot (text+data+bss)        ~40 MiB, loaded by the kernel        AOT code + runtime + shim
 *   newlib heap                  SHP_NEWLIB_HEAP_MB (link time)        malloc: the IL images (read into RAM, no mmap),
 *                                                                      Mono metadata/mempools/interpreter code
 *   thread stacks                SHP_K6_STACK_RESERVE_MB kept free     Mono thread 2 MiB, Mono's others 256 KiB, the MT render thread 512 KiB
 *                                (kit #5: SHP_K5_STACK_RESERVE_MB 16,  (kernel-allocated; kit #6 from kit #5's device STK lines; kit #5: 8 MiB /
 *                                kit #4: SHP_STACK_RESERVE_MB 20)      256 KiB / 1 MiB -- SHEPHERD_STACKS=kit5; kit #4: 8 MiB / 1 MiB -- kit4)
 *   IL pool                      SHP_K6_IL_POOL_MB                     IL images read here before the newlib heap (kit #6 10 MiB; kit #5 4; kit #4: none)
 *   Mono arena (one USER_RW)     everything else (env SHEPHERD_ARENA_MB overrides)
 *                                = sgen card table 8 MiB + nursery/major/LOS (max-heap-size) + 1 MiB interpreter stack
 *                                  per thread + internal/hazard slack
 *   max-heap-size                arena - 8 - SHP_INTERP_STACKS_MB - SHP_ARENA_SLACK_MB (env MONO_GC_PARAMS overrides)
 *   soft-heap-limit              max-heap-size - SHP_SOFT_MARGIN_MB: sgen collects the major heap above it (device kit #2)
 * A full GC heap throws OutOfMemoryException (logged as FAIL by the profiler hook); an empty arena logs ARENA FAIL + FAIL;
 * an empty newlib heap is a Mono fatal error (FAIL). Every checkpoint line has the numbers of all four pools.
 * Reaching max-heap-size is NOT a failure by itself (device kit #2 review): sgen refuses, collects and retries, so the first refusal
 * is a "WARN LIMIT" line and the refusal count is on every checkpoint and on the END line; only an OutOfMemoryException, an arena or
 * newlib exhaustion, a fatal error or an unhandled exception are FAIL lines. A STALL? line (every build but the diag one, which has
 * its heartbeat) says nothing was logged for 120 s, with counters that show whether the runtime is still working.
 * No signal/exception/abort handlers, no executable memory, no kubridge (same rules as the V5 hello).
 *
 * SHEPHERD_GL (Stage B, the milestone app shepherd_menu_gl.vpk, SHPD00008; vita/notes/m1-gl-app.md): the same host, but the engine
 * DRAWS -- its GL backend (SHEPHERD_RENDER=gl, SHEPHERD_GL_HOST=shepgl) on vitaGL through vita/gltest/shepgl (the 51 entry points +
 * context/swap, all in the static P/Invoke table), the GPU Stylizer, DXT textures from the pack, 960x544 -- and reads the Vita's
 * controls (shepherd_vita_pad_read -> the engine's VitaPad mapping) and, since kit #5, the front touch screen (shepherd_vita_touch_read ->
 * the engine's VitaTouch: the game's mouse, as on the PC; touch_latch.h; SHEPHERD_TOUCH=0 in the env file: never read). Differences from
 * the headless build:
 *   - files: menu_gl.txt / menu_gl.prev.txt (log), env_gl.txt, args_gl.txt (default "boot 0 0": run until START+SELECT is held
 *     1.5 s), screenshots on request into ux0:data/shepherd/shots/ (SHEPHERD_SHOT_FRAMES=... in env_gl.txt)
 *   - libshacccg.suprx presence check before vitaGL is touched (a clear on-screen + log message if it is missing)
 *   - memory: the arena leaves vitaGL a RAM pool (SHP_GL_RAM_MB) on top of the stack/safety reserve, and vglInitExtended runs
 *     AFTER the arena exists with ram_threshold = that reserve, so vitaGL's pools (RAM pool, all free CDRAM + PHYCONT) can never
 *     take the Mono arena's or the thread stacks' memory (partition below, at main)
 *   - clocks: like every build since 2026-10-13, set at start (before vitaGL's init; see CLOCKS below)
 *   - PASS = the Main Menu is drawn (frames reached the display) and responds to the pad: the selected button changes after a
 *     pad press (the engine's menu watch, Diag/VitaHost.cs kind 3)
 *
 * CLOCKS (every build: headless, GL, GL+audio, diag; 2026-10-13): main sets 444/222/222/166 MHz (ARM/bus/GPU/crossbar) right after
 * the env file is applied, before the arena, vitaGL or the runtime, and logs the clocks read back on a CLK line at start, again
 * right before the END line and at exit (launch values restored there). SHEPHERD_CLOCKS=0 (env file) keeps the launch clocks.
 * Before this only the GL build set them (gl_start, after vitaGL's init), so the headless device runs ran at the 333 MHz launch
 * clock (devicelogs/analysis-2026-10-12/device-findings.md correction 1).
 *
 * PLAY CLOCK (kit3 play mode, vita/notes/play-mode.md): SHEPHERD_TIME picks the engine's game clock (Runtime/PlayClock.cs). The GL
 * apps (00008/00009/00010/00091/00096 and kit #3's play app 00097/00098: played by the owner) default to "wall" = Unity's clock: deltaTime = the real frame time clamped to
 * maximumDeltaTime (1/3 s), FixedUpdate at 50 Hz of game time, realtimeSinceStartup = the wall clock -- the game moves at real speed at any
 * frame rate. The headless apps (scripted test runs) default to "frame" (1/60 s of game time per frame, deterministic). The env file wins
 * either way; a T0 line says which clock and why. Kit #3's measurement app (SHP_TEST_CLOCK, 00099) is a GL app on the frame clock + pad.txt
 * that ends at frame 1460. With the play clock the [sim] window keeps its frames (1301-1400) but drops the forced full GC at 1300 (a ~1.7 s
 * freeze on the device: a measurement for scripted runs only). The sound never follows either clock: shepaudio's sceAudioOut thread runs at the
 * hardware's pace on the spare core USER_2 (Audio/ShepAudio.cs).
 *
 * KIT #3 REVIEW (vita/notes/play-mode.md section 7): an unhandled managed exception -- and a runtime fatal error after
 * OutOfMemoryExceptions -- ends the run the host's way (end_run_now: FAIL END line, the red screen when safe, exit) instead of Mono's
 * abort and the system's crash dialog; the play apps' MEMORY GUARD (SHP_PLAY_APP) ends a session cleanly when the GC heap meets its cap
 * during play, on an OutOfMemoryException, or when newlib is nearly out (flag 4 of shepherd_vita_pad_read); newlib is checked on every
 * heartbeat (WARN lines, the P line's KiB/1000f slope); a short press between two engine frames is latched (pad_latch.h); a render
 * thread that did not stop at the end (checkpoint kind 4) keeps the host off vitaGL after Main; the play apps' 1 MiB of stream rings
 * is 4 x 65536 frames.
 *
 * SHP_DIAG (devfail1: "Shepherd Menu Diag", SHPD00016, vita/shepherd/build.sh SHEPHERD_DIAG=1; headless only): the same host plus
 * vita/shepherd/diag/host_diag.inc.c -- a heartbeat thread with progress counters every 5 s (STALL lines when nothing moves), the
 * engine's boot-phase markers (engine-diag.patch), the preload hook's nesting, Mono's fatal messages in full, a REPRO mode (L at
 * start: the 2026-10-07 settings) -- linked with the diagnostic runtime and the counting ld --wrap thunks (diag/gen_diag_wraps.py).
 * Files: menu-diag.txt (+ .prev), env_diag.txt, args_diag.txt. Without SHP_DIAG none of it is compiled.
 *
 * CORE PINNING (kit 3, every build; vita/notes/engine-pass-speed.md section 14): on when SHEPHERD_VITA_PIN=1, or, unset, when
 * SHEPHERD_MT_RENDER=1 (the multicore render pipeline): the engine pins the main thread to USER_0 and the pipeline's render thread to
 * USER_1 through the shepherd_vita_pin export (one PIN line each, rc + read-back), and the GL host pins vitaGL's garbage-collector
 * thread to USER_2 (vglSetupGarbageCollector before vglInit). Off, no thread is touched. SHP_MT_DEFAULT (build.sh SHEPHERD_MT_RENDER=1:
 * SHPD00096, the MT variant of the GL derived-AOT app; since k4 also the play app SHPD00097) makes SHEPHERD_MT_RENDER=1 the default
 * (SHEPHERD_MT_RENDER=0 in the env file turns it and the pinning off). The H1 multicore line says what is in force.
 * k4-display, the 4-CORE SPLIT (with pinning on): + shepaudio's output and IO threads on USER_2 (their PIN lines come from
 * shepherd_vita_thread_note), + the latency-tolerant workers -- the engine's pack read-ahead thread and the host's power/watchdog thread
 * -- on CPU3 (mask 0x80000) when the boot probe finds it open (the owner's core-unlock plugin), else on USER_2 (C3 line; see the
 * core3_probe comment); the P heartbeat lines carry the clocks the kernel reports at that moment (a PSVshell profile shows there).
 * KIT #6 SETTINGS ROWS (track toggles, vita/notes/kit6-toggles.md): the engine adds "PS Vita Settings" rows to the game's Settings > Graphics
 * tab (Runtime/VitaToggles.cs) and reaches the host through one export, shepherd_vita_k6: "4th CPU Core" Off moves the latency-tolerant
 * workers pinned so far to USER_2 (and every later one: shepherd_vita_worker_mask) -- a C3 k6 line + a PIN line per moved thread, with the
 * read-back --, On moves them back to CPU3 (only when the C3 probe found it open); "CPU Speed" asks for 444 or 500 MHz and answers with the
 * clock READ BACK (a CLK k6 line per call; 500 counts only when it reads back 500); the readout row reads the clock about once a second
 * (no line). The choices live in the owner folder's vita-settings.txt (the engine reads and writes it, never the game's settings.json).
 * SHEPHERD_K6_TOGGLES=0 in the env file: the engine adds no row and never calls the export -- the host is kit #5's.
 *
 * SHEPHERD_AUDIO (Stage C, with SHEPHERD_GL: shepherd_menu_gl_audio.vpk, SHPD00010; vita/notes/m1-audio.md): the same GL app with
 * SOUND. The engine's FMOD stubs play the owner-copied audio bake (ux0:data/shepherd/audiobake/menu: manifest.json + ev/) through
 * shepaudio (vita/audio, libshepaudio_vita.a linked in): sceAudioOut on its own thread, sceAudiodec (AAC) on the IO thread. The
 * engine binds every sa_* entry point through shepaudio_proc (this file; in the static P/Invoke table). Memory: an audio pool
 * (SHP_AUDIO_POOL_MB, one USER_RW memblock carved from the arena's share, allocated right after the arena) serves the take cache
 * and the stream rings (vita/shepherd/audio_pool.c); shepaudio's tables (~0.3 MiB for the menu set) and decoder buffers are
 * newlib. Without the bake folder the app runs exactly like the silent GL app (an A0 line says so). Logs menu_gl_audio.txt,
 * env_gl_audio.txt, args_gl_audio.txt. (Built without SHEPHERD_AUDIO, the GL host exports a shepaudio_proc that finds nothing.)
 */
#include <psp2/kernel/processmgr.h>
#include <psp2/kernel/sysmem.h>
#include <psp2/kernel/threadmgr.h>
#include <psp2/kernel/clib.h>
#include <psp2/io/fcntl.h>
#include <psp2/io/stat.h>
#include <psp2/ctrl.h>
#include <pthread.h>
#include <malloc.h>
#include <stdarg.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <strings.h>

#include <mono/jit/jit.h>
#include <mono/jit/mono-private-unstable.h>
#include <mono/metadata/appdomain.h>   /* (kit #7: mono_jit_info_get_code_start, the first-call trace) */
#include <mono/metadata/assembly.h>
#include <mono/metadata/class.h>
#include <mono/metadata/exception.h>
#include <mono/metadata/image.h>
#include <mono/metadata/loader.h>
#include <mono/metadata/mono-gc.h>
#include <mono/metadata/object.h>
#include <mono/metadata/profiler.h>
#include <mono/utils/mono-logger.h>
#include <mono/utils/mono-vita.h>   /* from the forked runtime (src/mono/mono/utils) */

#include <psp2/power.h>
#include "debugScreen.h"
#ifdef SHEPHERD_GL
#include "shepgl.h"   /* vita/gltest/shepgl: vitaGL context + swap + the 51 GL entry points (exported through the static table) */
#endif
#ifdef SHEPHERD_AUDIO
#ifndef SHEPHERD_GL
#error "SHEPHERD_AUDIO is a variant of the GL app: build with SHEPHERD_GL too"
#endif
#include "shepaudio.h"    /* vita/audio: the mixer that plays the bake */
#include "audio_pool.h"   /* vita/shepherd: its cache/stream-ring pool */
#endif

#ifndef SHP_BUILD_ID
#define SHP_BUILD_ID "unknown"
#endif
#ifndef SHP_NEWLIB_HEAP_MB
#define SHP_NEWLIB_HEAP_MB 128   /* v6-vita-build.md section 4: IL + Mono metadata, projected peak 105-115 MiB */
#endif

#define MiB (1024u * 1024u)
#define SHP_MONO_STACK (8u * MiB)  /* kit #4 (SHEPHERD_STACKS=kit4): the Mono thread's stack */
#define SHP_STACK_RESERVE_MB 20u   /* kit #4: not given to the arena: the Mono thread (8) + ~11 more threads x 1 MiB (finalizer,
                                       DispatchThread, 4-6 thread-pool workers + gate + timer, power) + kernel */
/* kit #5 (track mem, M3 stack right-sizing; vita/notes/kit5-mem.md): every thread stack measured under vlx (VLX_STACK_REPORT: painted
 * stacks, deepest word written) on the heaviest routes of every kit app -- the Mono thread's deepest use is the boot GC's own 256 KiB
 * clearing plus a few KiB (318 KiB on the Settings routes), every other thread (finalizer, the game's dispatch thread, thread-pool
 * workers, audio) <= 10 KiB -- so Mono's other threads get SHP_K5_THREAD_STACK_KB (25x the deepest use) and the reserve kept for stacks
 * shrinks by what that saves (SHP_K5_STACK_RESERVE_MB). Review fix: the Mono thread KEEPS kit #4's 8 MiB (SHP_K5_MONO_STACK_KB): the
 * engine builds its GL programs on it (GlBackend's constructor, before the MT render thread exists) and vitaGL's glCompileShader /
 * glLinkProgram run SceShaccCg there -- a stack depth vlx cannot see (its GL recorder never compiles) and no device run has measured
 * (kits #2-#4 gave it 8 MiB); the Vita has no guard page. The STK lines (stack_watch below) measure it on the device: kit #6 sizes it
 * from them. The MT apps' render thread asks for kit #4's 1 MiB itself (MtRender.cs, SHEPHERD_RENDER_STACK_KB). What M3 frees (4 MiB
 * of the reserve) goes to the IL pool (SHP_K5_IL_POOL_MB: IL images read there instead of the newlib heap = newlib headroom); the
 * Mono arena (GC heap cap) is kit #4's. Env file knobs: SHEPHERD_STACKS=kit4 (all four back to kit #4: 8 MiB / 1 MiB / 20 MiB / no
 * pool), SHEPHERD_MONO_STACK_KB, SHEPHERD_THREAD_STACK_KB, SHEPHERD_STACK_RESERVE_MB, SHEPHERD_IL_POOL_MB; SHEPHERD_STACK_WATCH=0 =
 * no STK lines (no paint). */
#ifndef SHP_K5_MONO_STACK_KB
#define SHP_K5_MONO_STACK_KB 8192u
#endif
#ifndef SHP_K5_THREAD_STACK_KB
#define SHP_K5_THREAD_STACK_KB 256u
#endif
#ifndef SHP_K5_STACK_RESERVE_MB
#define SHP_K5_STACK_RESERVE_MB 16u
#endif
#ifndef SHP_K5_IL_POOL_MB
#define SHP_K5_IL_POOL_MB 4u
#endif
/* device kit #6 (integration; the roadmap's "M3 stack shrink from the device STK lines"): kit #5's device run measured every Mono
 * thread's deepest stack use on the owner's PCH-2000 (devicelogs/20261015-kit5-phone, 8 starts of the 7 apps, STK lines): the Mono
 * thread 260 KiB (the boot GC's 256 KiB clearing; SceShaccCg compiling the 8 GL programs on it: 53 KiB, STK gl-programs), Mono's other
 * threads <= 10 KiB of their 256, the MT render thread (real vitaGL / GXM) <= 9 KiB of its 1024; under vlx (kit #6, every track's runs
 * incl. the intro measurement builds) the Mono thread's deepest is 318 KiB (Settings -> New Game, route A), the others <= 10 KiB. Kit #6:
 * the Mono thread 2048 KiB (6.4x the deepest seen), the render thread 512 KiB (MtRender.cs; 9.7x the compiler's 53 KiB, should a GL
 * program ever compile there), Mono's other threads KEEP kit #5's 256 KiB (kit #6 review fix: the integration had 128 -- but the game's
 * save writer, MMJsonDataReadWriter's "Write Thread" (new Thread without a size: MessagePack-serializes the whole DataManager, then LZ4 +
 * AES), has never run on any measured route: saves start past the scene stop, where no device or vlx run went; the other threads' <= 10
 * KiB says nothing about it, and the Vita has no guard page; shrink only after an STK line for that thread), and the reserve kept for
 * stacks 16 -> 10 MiB (VLX peak of live stacks 5.4 MiB with 128 KiB threads, ~7 MiB with 256: 13 threads x +128 KiB); the 6 MiB go to the IL
 * pool (4 -> 10 MiB: IL images read there instead of the newlib heap = newlib headroom), so the Mono arena, the GC heap cap and vitaGL's
 * RAM pool stay kit #5's to the MiB. SHEPHERD_STACKS=kit5 = kit #5's sizes exactly (8192 / 256 / 1024 KiB, 16 MiB, IL pool 4 MiB);
 * SHEPHERD_STACKS=kit4 = kit #4's; the single knobs override either. The STK lines keep measuring on the device. */
#ifndef SHP_K6_MONO_STACK_KB
#define SHP_K6_MONO_STACK_KB 2048u
#endif
#ifndef SHP_K6_THREAD_STACK_KB
#define SHP_K6_THREAD_STACK_KB 256u   /* (= kit #5's: the save writer is unmeasured -- above) */
#endif
#ifndef SHP_K6_STACK_RESERVE_MB
#define SHP_K6_STACK_RESERVE_MB 10u
#endif
#ifndef SHP_K6_IL_POOL_MB
#define SHP_K6_IL_POOL_MB 10u
#endif
#define SHP_SAFETY_MB 6u            /* not given to the arena: later kernel/sceIo allocations, pthread-embedded bookkeeping */
#define SHP_ARENA_MAX_MB 240u
#define SHP_CARD_TABLE_MB 8u        /* sgen 32-bit card table: covers the whole 4 GiB address space */
#define SHP_INTERP_STACKS_MB 10u    /* INTERP_STACK_SIZE = 1 MiB per thread that runs interpreted code (~8 expected) */
#define SHP_ARENA_SLACK_MB 4u       /* sgen internal allocator, hazard pointers, section alignment (the V5 arena rule) */
#define SHP_SOFT_MARGIN_MB 16u      /* MONO_GC_PARAMS soft-heap-limit = max-heap-size - this (major collections before the cap) */

unsigned int _newlib_heap_size_user = SHP_NEWLIB_HEAP_MB * MiB;
unsigned int _pthread_stack_default_user = 1u * MiB;
#ifndef SHP_K8_MODULE   /* (kit #8 launcher build: the launcher eboot owns the main thread's stack size -- the same 512 KiB, launcher.c) */
__attribute__((used)) const SceSize sceUserMainThreadStackSize = 512u * 1024u;
#endif

#define DIR_ "ux0:data/shepherd"
#define ROOTED "/ux0:data/shepherd"   /* the same folder as managed code sees it (rooted: see pal_vita.c "Paths") */
#ifndef SHP_LOG_NAME
#define SHP_LOG_NAME "menu.txt"   /* build.sh: menu-aot.txt for the opt-in engine-AOT build (SHPD00011) */
#endif
#ifndef SHP_LOG_PREV_NAME
#define SHP_LOG_PREV_NAME "menu.prev.txt"   /* build.sh: menu-aot.prev.txt for SHPD00011 */
#endif
#ifndef SHP_ENV_NAME
#define SHP_ENV_NAME "env.txt"   /* build.sh: env_gl.txt for the GL app (its partition knobs differ: keep the files apart) */
#endif
#ifndef SHP_ARGS_NAME
#define SHP_ARGS_NAME "args.txt"   /* build.sh: args_gl.txt for the GL app */
#endif
#ifdef SHEPHERD_GL
#ifdef SHP_TEST_CLOCK
/* device kit #3, the measurement variant of the GL derived-AOT app (build.sh SHEPHERD_TEST_CLOCK=1, SHPD00099): a scripted run that ends
 * by itself -- the frame clock (pad.txt applies: cross at 1050, down at 1400) and 1460 frames, like every frame-clock reference run */
#define SHP_DEFAULT_ARGS "boot 1460 0"
#define SHP_TIME_DEFAULT "frame"
#else
#define SHP_DEFAULT_ARGS "boot 0 0"   /* run until START+SELECT (the V9 soak); args_gl.txt "boot 1460 0" bounds it */
#define SHP_TIME_DEFAULT "wall"      /* kit3 play mode: the GL apps are played -- Unity's clock (see PLAY CLOCK in the header) */
#endif
#define SHP_RENDER "gl"
#define SHP_GL_RAM_MB 12u            /* vitaGL's RAM (USER_RW) pool, left free by the arena (env SHEPHERD_GL_RAM_MB); see main */
#else
#define SHP_DEFAULT_ARGS "boot 1460 0"
#define SHP_RENDER "null"
#define SHP_TIME_DEFAULT "frame"     /* the headless apps are scripted test runs: the deterministic frame clock */
#endif
#ifdef SHEPHERD_AUDIO
#ifndef SHP_AUDIO_POOL_MB
#define SHP_AUDIO_POOL_MB 8u          /* the audio pool (env SHEPHERD_AUDIO_POOL_MB): cache + 8 stream rings (1 MiB) + slack */
#endif                                /* (build.sh: 4 for kit #3's play app -- the menu's sounds peak at 2.1 MiB of it -- so the
                                       *  GC heap gets 4 MiB more: max-heap-size 124 instead of 120; vita/notes/play-mode.md section 6) */
#define SHP_AUDIO_BAKE DIR_ "/audiobake/menu"   /* a device path: shepaudio opens its files with newlib (sceIo) */
#endif
#ifdef SHP_APP_TITLE   /* device kit #3's variants: their BEGIN line */
#ifdef SHEPHERD_AUDIO
#define SHP_APP_SOUND "SOUND: the audio bake through shepaudio on sceAudioOut"
#else
#define SHP_APP_SOUND "silent"
#endif
#if defined (SHP_TEST_CLOCK)
#define SHP_APP_MODE "; MEASUREMENT: frame clock + pad.txt, 1460 frames, then it ends by itself"
#elif defined (SHP_MT_DEFAULT)
#define SHP_APP_MODE "; PLAY clock; MT: the multicore render pipeline on by default, threads pinned"
#else
#define SHP_APP_MODE "; PLAY clock"
#endif
#if defined (SHEPHERD_GL) && !defined (SHP_TEST_CLOCK)
/* kit #3's PLAY apps (00097 / 00098), played by the owner for as long as he likes (kit #3 review): the MEMORY GUARD ends a session
 * cleanly -- a FAIL MEM line, the engine's END after the current frame, the red screen, every log kept -- when the GC heap meets its cap
 * after the Main Menu is up (closed sub-menus stay in the heap as in Unity: each new opening adds to it, submenus.md section 4), when an
 * OutOfMemoryException is thrown, or when the newlib heap is about to run out. Without it those end in an unhandled
 * OutOfMemoryException or a newlib fatal error: the runtime's abort and the system's crash dialog. SHEPHERD_MEM_GUARD=0 (env file)
 * turns it off (vlx margin runs). */
#define SHP_PLAY_APP 1
#endif
#endif
#define LOG_PATH DIR_ "/" SHP_LOG_NAME
#define LOG_PREV_PATH DIR_ "/" SHP_LOG_PREV_NAME
#ifdef SHP_DIAG
static char g_log_path [96] = LOG_PATH;   /* repro mode: menu-diag-repro.txt (+ its .prev) */
#define LOG_FILE g_log_path
#else
#define LOG_FILE LOG_PATH
#endif
#define GAME_MANAGED DIR_ "/managed"
#define VPK_MANAGED "app0:managed"

/* generated by build.sh */
extern void shepherd_register_aot_modules (void);
extern const char *const shepherd_aot_module_names [];
/* kit #9 fidelity, second review: the card assemblies the static AOT images bind (the "(owner)" entries of their image tables in the GUID
 * gate's report): SHEPHERD_K9_FLOATPC never takes one of these from its folder (k9_fpc_refuse below) */
extern const char *const shepherd_aot_bound_names [];
#ifdef SHP_GAME_AOT
/* device kit #4 (build.sh SHEPHERD_AOT_OWNER=1: SHPD00095 / SHPD00190): the owner-local game modules (compiled game / rewritten Unity code,
 * profile-guided partial) have their own table; SHEPHERD_GAME_AOT=0 in the env file leaves them unregistered, so Mono finds no image for
 * those assemblies and the interpreter runs all their code, as in the apps without them (the per-case switch back) */
extern void shepherd_register_owner_aot_modules (void);
extern const char *const shepherd_owner_aot_module_names [];
extern const char *const shepherd_owner_aot_bound_names [];   /* kit #9 second review: the card assemblies the owner modules bind (their own too) */
#endif
extern const MonoVitaStaticSymbol shepherd_static_symbols [];
extern const int shepherd_static_symbols_count;

static pthread_mutex_t log_lock = PTHREAD_MUTEX_INITIALIZER;
static int g_screen;
static volatile int g_unhandled_printed, g_fail_count, g_oom_count;
static SceUInt64 g_t0;   /* process time at main (us) */
#ifdef SHEPHERD_GL
static int g_gl_state, g_interactive;   /* vitaGL is up (gl_start); menu responses to the pad (kind 3, + kit #9's title press: kind 9) */
static int g_title_resp;                /* kit #9 review round 2 (finding 2): of them, the title press's answer (kind 9) -- counted for the END
					 * line / PASS M1 (the GREEN rule) but NOT for the memory guard, which kit #8 armed at the first navigation */
static volatile int g_gl_unsafe;        /* the engine said its render thread may still be inside vitaGL (kind 4): no GL after Main */
#endif
#ifdef SHEPHERD_GL
/* kit #3 review: the host's own request to end the session (the memory guard, SHP_PLAY_APP): shepherd_vita_pad_read hands it to the
 * engine (flag 4), which ends its boot loop after the current frame like START+SELECT does -- the END line, the red screen, the logs */
static volatile int g_host_quit;
#endif
#ifdef SHP_PLAY_APP
static int g_mem_guard;   /* 1 unless SHEPHERD_MEM_GUARD=0 (main) */
#endif

static double wall_s (void) { return (double) (sceKernelGetProcessTimeWide () - g_t0) / 1e6; }

static volatile SceUInt64 g_activity_t;   /* the last line logged by anything but the watchdog (stall_check), or echoed by the engine */
/* kit #7 measure (k7_measure.h, included below logs_dir): the black box's line rings, its event dump, the Chrome trace's GC ring */
static void k7_ring_put (int which, const char *line);
static void k7_bb_event (const char *why);
static void k7_gc_note (SceUInt64 start, unsigned us);
static void k7_calls_write (const char *tag);
static void k10a_ilz_path (const char *name, char *path, size_t cap, unsigned *size);   /* device kit #10 aot (k10_aot.inc.c): the preload hook */
static void k12_vpk_ilz_path (const char *name, char *path, size_t cap, unsigned *size);   /* kit #12 walkb (k12_walkb.inc.c): the VPK's DLLs in ILZ form */
static int k10a_ilz_on_now (void);   /* kit #10 integration (boot x aot): compressed IL on this start? (k10_aot.inc.c; k10_ilb.inc.c asks) */

static void log_line_ex (const char *line, int activity)
{
	pthread_mutex_lock (&log_lock);
	if (activity)
		g_activity_t = sceKernelGetProcessTimeWide ();
	k7_ring_put (0, line);   /* kit #7: the black box's host-line ring (nothing before k7_init) */
	SceUID fd = sceIoOpen (LOG_FILE, SCE_O_WRONLY | SCE_O_CREAT | SCE_O_APPEND, 0777);
	if (fd >= 0) {
		sceIoWrite (fd, line, strlen (line));
		sceIoWrite (fd, "\n", 1);
		sceIoClose (fd);
	}
	sceClibPrintf ("%s\n", line);
	if (g_screen) {
		psvDebugScreenPuts (line);
		psvDebugScreenPuts ("\n");
	}
	pthread_mutex_unlock (&log_lock);
}
static void log_line (const char *line) { log_line_ex (line, 1); }

static char g_k8_last_fail [192];   /* kit #8 boot track: the FIRST FAIL line of the latest failure, for the clean boot screen's message (k8_fail_screen) */
static char g_k8_fail_tag [16];     /* its tag ("FAIL G0 "), and how many FAIL lines with the same tag came after it (review fix: it was the last line) */
static unsigned g_k8_fail_more;
static void logf_ (const char *fmt, ...)
{
	char buf [1024];
	va_list ap;
	va_start (ap, fmt);
	vsnprintf (buf, sizeof (buf), fmt, ap);
	va_end (ap);
	if (!strncmp (buf, "FAIL", 4)) {
		g_fail_count++;
		size_t tl = 5 + strcspn (buf + (buf [4] ? 5 : 4), " ") + 1;   /* "FAIL " + the tag + ' ' */
		if (tl < sizeof g_k8_fail_tag && g_k8_last_fail [0] && strlen (g_k8_fail_tag) == tl && !strncmp (g_k8_fail_tag, buf, tl))
			g_k8_fail_more++;
		else {
			snprintf (g_k8_last_fail, sizeof g_k8_last_fail, "%s", buf);
			snprintf (g_k8_fail_tag, sizeof g_k8_fail_tag, "%.*s", (int) (tl < sizeof g_k8_fail_tag ? tl : sizeof g_k8_fail_tag - 1), buf);
			g_k8_fail_more = 0;
		}
	}
	log_line (buf);
}
#define LOG(...) logf_ (__VA_ARGS__)
static void end_run_now (const char *what);   /* (below, at the Mono thread) */

/* ---------------------------------------------------------------- memory numbers */

extern unsigned int __malloc_max_sbrked_mem;   /* newlib mallocr.c: high-water mark of the sbrk'd heap */
static unsigned g_arena_bytes, g_max_heap_mb;
static unsigned long long g_il_bytes;
static unsigned g_il_files;

/* kit #5 M3 (see SHP_K5_* above): the stack sizes in force and the IL pool. g_il_pool: one USER_RW block allocated right after the
 * Mono arena (before vitaGL, so it is not part of vitaGL's ram_threshold); the preload hook reads IL images into it first-fit in load
 * order, bump-allocated (Mono keeps every image buffer for the life of the process), and into the newlib heap once it is full. */
static unsigned g_mono_stack_req = SHP_MONO_STACK, g_thread_stack = 1024u * 1024u, g_stack_reserve_mb = SHP_STACK_RESERVE_MB, g_il_pool_mb;
static int g_sw_on;   /* the stack watch (stack_watch_* below): SHEPHERD_STACK_WATCH, default on */
static void stack_watch_mono_line (const char *tag, const char *why);
static char *g_il_pool; static unsigned g_il_pool_size, g_il_pool_used, g_il_pool_files;
static void *il_pool_alloc (unsigned n)
{
	unsigned need = (n + 16u) & ~15u;   /* + the terminating 0 read_file writes, 16-byte aligned */
	if (!g_il_pool)
		return NULL;
	/* kit #10 (boot track): lock-free -- the IL bundle's reader thread (k10_ilb.inc.c) allocates too, without load_lock */
	unsigned used = __atomic_load_n (&g_il_pool_used, __ATOMIC_RELAXED);
	do {
		if (g_il_pool_size - used < need)
			return NULL;
	} while (!__atomic_compare_exchange_n (&g_il_pool_used, &used, used + need, 1, __ATOMIC_ACQ_REL, __ATOMIC_RELAXED));
	return g_il_pool + used;
}
/* kit #10: give back the pool's LAST allocation [p, p + n) (a failed read); anything allocated after it keeps the space used */
static void il_pool_rewind (const char *p, unsigned n)
{
	unsigned end = (unsigned) (p - g_il_pool) + ((n + 16u) & ~15u), start = (unsigned) (p - g_il_pool);
	__atomic_compare_exchange_n (&g_il_pool_used, &end, start, 0, __ATOMIC_ACQ_REL, __ATOMIC_RELAXED);
}
static int in_il_pool (const void *p) { return g_il_pool && (const char *) p >= g_il_pool && (const char *) p < g_il_pool + g_il_pool_size; }
static const char *il_pool_h2 (void)
{
	static char b [64];
	if (!g_il_pool_mb)
		return "";
	snprintf (b, sizeof (b), " + %u MiB IL pool", g_il_pool_mb);
	return b;
}
static const char *il_pool_note (void)
{
	static char b [120];
	if (!g_il_pool)
		return "";
	snprintf (b, sizeof (b), " (%u of them in the IL pool: %u of %u KiB used; the rest in the newlib heap)", g_il_pool_files, g_il_pool_used / 1024,
		  g_il_pool_size / 1024);
	return b;
}

typedef struct {
	unsigned free_user_k, free_cdram_k, free_phycont_k;
	unsigned newlib_used_k, newlib_sbrk_k, newlib_peak_k;
	int arena_ok;
	MonoVitaArenaStats arena;
} MemSnap;
/* device kit #10, track memory -- DEFAULTS (vita/notes/kit10-memory.md, the gates): in the PLAY apps (00097 / 00098 / 00095 and the launcher apps
 * 00193 / 00194) the memory-pressure ladder and the twelve extra slot sizes are ON (identical records on every route; the walk's GC heap
 * -5.1 MiB from the slot sizes); the measurement app 00099 and the headless 00011 keep kit #9's memory (their kit-to-kit history). The SHARED
 * POOL is opt-in since the review (R1/R2: every play app is a GL app, and vitaGL registers only the newlib block of its malloc (1) at vglInit --
 * k10_vglmem.c makes the split heap safe, but no gate models vitaGL: the VLX stand-in has no pools; SHEPHERD_K10_POOL=1 + a device run decide).
 * Evacuation and the IL pool in video memory are opt-in. SHEPHERD_K10_LADDER=0 / SHEPHERD_K10_SIZES=0 / SHEPHERD_K10_MEM=0 (env file) = kit #9. */
/* device kit #11, track walk -- DEFAULTS (vita/notes/kit11-walk.md): in the PLAY apps the SHARED POOL is ON by default (the owner's device run
 * of kit #10's X2: PASS END, 0 OOM, vitaGL's range + GPU mapping ok, the GC heap's cap +13 MiB at its tightest; boot +2.8 s in that run, to
 * watch). SHEPHERD_K11_WALK=0 (env file; read before main too, like the pool) = kit #10's defaults for every kit #11 walk switch;
 * SHEPHERD_K10_POOL=0 alone = the split. Review fix: sgen's major-block EVACUATION at 90 % (the walk's arena peak -5 MiB, VLX) is OPT-IN
 * (SHEPHERD_K10_EVAC=90) until the owner's device A/B of its pauses (the roadmap's condition); the default is kit #10's: sgen's own 66 %. */
#ifdef SHP_PLAY_APP
#ifndef SHP_K11_POOL_DEFAULT
#define SHP_K11_POOL_DEFAULT 1
#endif
#endif
#ifdef SHP_PLAY_APP
#ifndef SHP_K10_POOL_DEFAULT
#define SHP_K10_POOL_DEFAULT 0   /* (k10b: 1 -- review fix R1/R2: off until the owner's device run of SHEPHERD_K10_POOL=1) -- kit #11: SHP_K11_POOL_DEFAULT */
#endif
#ifndef SHP_K10_LADDER_DEFAULT
#define SHP_K10_LADDER_DEFAULT 1
#endif
#ifndef SHP_K10_SIZES_DEFAULT
#define SHP_K10_SIZES_DEFAULT "536,1600,112,208,176,3208,368,56,1144,472,144,232"   /* the walk census's greedy pick (block packing counted) */
#endif
#endif
static const char *k10_pool_note (void);   /* kit #10 memory (k10_mem.inc.c): the shared pool's ends for the MEM lines (empty without the pool) */
#include "k10_newlib.inc.c"   /* device kit #10 memory: newlib's heap (vitasdk's, or the shared pool: SHEPHERD_K10_POOL) -- vita/notes/kit10-memory.md */

static void mem_snap (MemSnap *m, int nonblocking)
{
	SceKernelFreeMemorySizeInfo fi;
	memset (&fi, 0, sizeof (fi));
	fi.size = sizeof (fi);
	sceKernelGetFreeMemorySize (&fi);
	struct mallinfo mi = mallinfo ();
	m->free_user_k = fi.size_user / 1024;
	m->free_cdram_k = fi.size_cdram / 1024;
	m->free_phycont_k = fi.size_phycont / 1024;
	m->newlib_used_k = k10_nl_fix_k ((unsigned long long) (unsigned) mi.uordblks);   /* kit #10: mallocr books the shared pool's gap as in use too */
	m->newlib_sbrk_k = k10_nl_fix_k ((unsigned long long) (unsigned) mi.arena);   /* kit #10: without the shared pool's gap (k10_newlib.inc.c); = kit #9 otherwise */
	m->newlib_peak_k = k10_nl_fix_k (__malloc_max_sbrked_mem);
	if (nonblocking)
		m->arena_ok = mono_vita_vm_arena_try_get_stats (&m->arena) == 0;
	else {
		mono_vita_vm_arena_get_stats (&m->arena);
		m->arena_ok = 1;
	}
}

static void log_mem_ex (const char *when, int nonblocking)
{
	MemSnap m;
	mem_snap (&m, nonblocking);
	if (!m.arena_ok) {
		LOG ("MEM %s: free user=%u KiB cdram=%u phycont=%u | newlib used=%u KiB (sbrk %u, peak %u) of %u KiB | mono arena busy (its lock is held)",
		     when, m.free_user_k, m.free_cdram_k, m.free_phycont_k, m.newlib_used_k, m.newlib_sbrk_k, m.newlib_peak_k, k10_newlib_cap_k ());
		return;
	}
	LOG ("MEM %s: free user=%u KiB cdram=%u phycont=%u | newlib used=%u KiB (sbrk %u, peak %u) of %u KiB | arena used=%u KiB peak=%u of %u, %u extents, largest free %u KiB, exec-refused=%u%s",
	     when, m.free_user_k, m.free_cdram_k, m.free_phycont_k, m.newlib_used_k, m.newlib_sbrk_k, m.newlib_peak_k, k10_newlib_cap_k (),
	     (unsigned) (m.arena.used / 1024), (unsigned) (m.arena.peak / 1024), (unsigned) (m.arena.arena_size / 1024),
	     m.arena.free_extents, (unsigned) (m.arena.largest_free / 1024), m.arena.exec_requests, nonblocking ? "" : k10_pool_note ());
}
#define log_mem(when) log_mem_ex ((when), 0)

/* ---------------------------------------------------------------- GC pauses (profiler gc_event; no handler of any kind) */

#define PAUSE_RING 4096
static struct {
	SceUInt64 stop_t;          /* PRE_STOP_WORLD time */
	int gen;                   /* generation of the collection in progress (START event) */
	unsigned count [2];        /* minor / major collections seen */
	unsigned long long total_us [2];
	unsigned max_us [2];
	unsigned ring [PAUSE_RING];   /* recent pauses (us), major ones with the top bit set */
	unsigned ring_n;
	unsigned window_start;     /* ring_n at the previous checkpoint */
	int conc;                  /* kit #9 gc: this pause's major kind -- 0 serial (sgen's own), 1 a concurrent major's start, 2 its finish,
	                              3 an explicit GC.Collect (a full serial major; a concurrent mark still running is finished in the same pause) */
	int explicit_;             /* kit #9 gc review: this pause runs on the thread inside an explicit GC.Collect (__wrap_ves_icall_System_GC_InternalCollect) */
	unsigned kind_n [4], kind_max [4];   /* kit #9 gc: major pauses by that kind (count, max us) */
	/* kit #9 gc review: the majors in order (us | kind << 30) for the K9 GC majors lines -- the first K9_MAJSEQ_HEAD and, after them, the
	 * last K9_MAJSEQ_RING (a ring): a device session's gameplay majors are kept, not only the boot's and the menu's (the first 48 before) */
#define K9_MAJSEQ_HEAD 64
#define K9_MAJSEQ_RING 192
	unsigned majseq [K9_MAJSEQ_HEAD + K9_MAJSEQ_RING], majseq_n;
} gcs;
/* kit #9 gc review: the thread inside an explicit full GC.Collect (the M2 wrapper sets it around the real call; 0 = none), so its pause is
 * booked as kind 3 'x' -- sgen's own serial majors (soft-heap-limit, overflow, the cap rule) stay 's' */
static volatile SceUID g_gc_explicit_tid;
/* kit #9 gc review: the runtime's own answer (vita/patches/k9-gc.patch: sgen_gc_collect with a generation > 0 is running, whichever way the
 * icall was reached -- the M2 wrapper is NOT entered in every run: vlx g5mf1, g4tpfk1, g5pfo ran every GC.Collect past it); weak: a kit #8
 * runtime has none, and with SHEPHERD_GC_STATS off it answers 0 -- then the wrapper's thread mark alone decides */
int mono_vita_gc_explicit_running (void) __attribute__((weak));
static unsigned g_gcgov_full_calls;   /* kit #9 gc review: full GC.Collect calls (generation > 0) the M2 wrapper saw */
static inline void k9_gc_after_collection (long long heap);   /* kit #9 gc (k9_gc.inc.c): the GC heap's peak at collections */
static inline int k9_gc_base_update (int major, int kind, long long heap, volatile long long *base);   /* kit #9 gc review (k9_gc.inc.c): M2's base under lever 32 */
static void k9_gc_scene_note (int frame, const char *note, int64_t alloc_main);   /* kit #9 gc: checkpoint kind 8 (k9_gc.inc.c) */
static void k9_gc_census_walk (void);   /* kit #9 gc: the object-header census (research; SHEPHERD_GC_CENSUS=1) */
static void k10_mem_major_walk (void);   /* kit #10 memory (k10_mem.inc.c): the size-class census (test only; SHEPHERD_K10_CENSUS=1) */
static void k12_hdr_walk (void);   /* kit #12 walkb (k12_walkb.inc.c): the object-header census (test only; SHEPHERD_K12_HDRCENSUS=1) */
static unsigned k13_il_vram_type (void);   /* kit #13 walkc (k13_walkc.inc.c): the IL pool's video-memory type (SHEPHERD_K13_IL_CACHED) */
static unsigned k14_cap_over (void);       /* kit #14 newlib (k14_newlib.inc.c): the MiB the GC heap's cap leaves for the arena's other tenants */
static int k14_refusals_named (void);      /* kit #14 integration (k14_newlib.inc.c): the runtime names the refusals sgen halves */
static int k14_pool_more (void);           /* kit #14 newlib: the MiB the stack reserve in force moves the device pools by */
static void k13_il_vram_used (int t);
static int k13_il_coherence (void *base);
static void k10_mem_after_major (int conc);  /* kit #10 memory: the memory timeline's sample after a major (test only; SHEPHERD_K10_MEMLOG); the pool's cap-drop flag (R3) */
static int k10_guard_spare (void);       /* kit #10 memory review fix R3: a heap-cap refusal right after the shared pool lowered the cap (no major since) */
static void k10_mem_heartbeat (int frame, const MemSnap *m);   /* kit #10 memory: the pressure ladder's level, the memory timeline */

struct _MonoProfiler { int unused; };
static struct _MonoProfiler g_prof;

#if !defined (SHEPHERD_MAC_CHECK)
/* sgen's own figures, as GC.GetGCMemoryInfo reads them (metadata/gc-internals.h): plain loads of the struct sgen fills at the end of
 * every collection -- no lock, no allocation -- so callable from gc_event_cb */
void mono_gc_get_gcmemoryinfo (int64_t *high_memory_load_threshold_bytes, int64_t *memory_load_bytes, int64_t *total_available_memory_bytes,
			       int64_t *total_committed_bytes, int64_t *heap_size_bytes, int64_t *fragmented_bytes);
static long long gc_heap_bytes (void)
{
	int64_t a, b, c, d, h, f;
	mono_gc_get_gcmemoryinfo (&a, &b, &c, &d, &h, &f);
	return (long long) h;   /* GCMemoryInfo.HeapSizeBytes: the major blocks + large objects at the last collection */
}
static volatile long long g_gc_major_base = -1;   /* the heap right after the last major collection (M2's growth gate); -1 = none yet */
#endif

/* device kit #6 (integration; the roadmap's "GC pause timer"): kit #5's heavy menu frames that held a collection averaged ~2 s on the
 * device, but they are also the frames that open Settings, so the log could not split the GC from the loading. Every collection's
 * stop-the-world time (PRE_STOP_WORLD -> POST_START_WORLD, below) is summed here in microseconds; the engine reads the sum before and
 * after each frame (Diag/SimStats.cs) and writes the frame's own GC pause into simstats-frames.tsv (gc_pause_ms) and the [sim] window
 * lines; read-logs.sh shows it next to every long frame. 32 bits (wraps after ~71 min; the engine takes differences): one aligned load,
 * safe while a collection runs on another thread. SHEPHERD_K6_GC_PAUSE=0 (env file): the engine never calls it (kit #5's TSV). */
static volatile unsigned g_gc_pause_total_us;
unsigned shepherd_vita_gc_pause_us (void);
unsigned shepherd_vita_gc_pause_us (void) { return g_gc_pause_total_us; }

/* kit #7 spine track, "native clipping" (vita/shepherd/spine_clip_abi.h, Runtime/SpineClipNative.cs): the port of Spine's clipping is
 * owner-local (engine/Derived/native) -- build.sh compiles it in for the owner-only derived-AOT apps (SHP_SPINE_CLIP_DERIVED) and this stub
 * everywhere else: shepherd_spine_clip_api answers 0, the engine then never calls the other two and the game's managed clipping runs. */
#include "spine_clip_abi.h"
#ifdef SHP_SPINE_CLIP_DERIVED
#include "spine_clip_impl.h"
#else
int shepherd_spine_clip_api (void) { return 0; }
int shepherd_spine_clip_start (float *a, int b, float *c, int d, int *e, int f, int *g, int h) { (void) a; (void) b; (void) c; (void) d; (void) e; (void) f; (void) g; (void) h; return -1; }
int shepherd_spine_clip_tris (const float *a, int b, const int *c, int d, const float *e, const float *f, const int *g, int h, float *i, float *j, int k, int *l, int m, float *n, int o, int *p)
{ (void) a; (void) b; (void) c; (void) d; (void) e; (void) f; (void) g; (void) h; (void) i; (void) j; (void) k; (void) l; (void) m; (void) n; (void) o; (void) p; return -1; }
#endif

static void gc_event_cb (MonoProfiler *prof, MonoProfilerGCEvent ev, uint32_t generation, mono_bool serial)
{
	/* runs on the collecting thread with the world stopped: arithmetic into static storage only */
	switch (ev) {
	case MONO_GC_EVENT_PRE_STOP_WORLD:
		gcs.stop_t = sceKernelGetProcessTimeWide ();
		gcs.gen = 0;
		gcs.conc = 0;
		gcs.explicit_ = (mono_vita_gc_explicit_running && mono_vita_gc_explicit_running ())
			|| (g_gc_explicit_tid && g_gc_explicit_tid == sceKernelGetThreadId ());   /* kit #9 gc review: kind 3 'x' */
		break;
	case MONO_GC_EVENT_START:
		gcs.gen = generation ? 1 : 0;
		if (generation && serial && !gcs.conc)
			gcs.conc = 1;   /* kit #9 gc: (the profiler's last argument is "a concurrent major") the start pause of a concurrent major */
		else if (generation && !serial && gcs.conc == 2)
			gcs.conc = 0;   /* kit #9 gc review: a concurrent finish FOLLOWED by a full serial major in the same pause (sgen runs that for a
					   forced collection during a mark) -- serial, not a finishing pause */
		break;
	case MONO_GC_EVENT_END:
		/* kit #9 gc (lever 32): a concurrent major's FINISHING pause raises END (old generation) without a START -- before, that pause was
		 * counted as a minor one. A serial major raised START (old) already, a minor END (nursery): their classes are unchanged. */
		if (generation) {
			gcs.gen = 1;
			if (serial)
				gcs.conc = 2;
		}
		break;
	case MONO_GC_EVENT_PRE_START_WORLD:
		if (gcs.gen) {
			k9_gc_census_walk ();   /* kit #9 gc (research, test only): nothing unless SHEPHERD_GC_CENSUS=1 */
			k10_mem_major_walk ();  /* kit #10 memory (test only): nothing unless SHEPHERD_K10_CENSUS=1 */
			k12_hdr_walk ();        /* kit #12 walkb (test only): nothing unless SHEPHERD_K12_HDRCENSUS=1 */
		}
		break;
	case MONO_GC_EVENT_POST_START_WORLD: {
		if (!gcs.stop_t)
			break;
		unsigned us = (unsigned) (sceKernelGetProcessTimeWide () - gcs.stop_t);
		int g = gcs.gen;
		gcs.count [g]++;
		gcs.total_us [g] += us;
		if (us > gcs.max_us [g])
			gcs.max_us [g] = us;
		gcs.ring [gcs.ring_n++ % PAUSE_RING] = us | (g ? 0x80000000u : 0);
		if (g) {   /* kit #9 gc: major pauses by kind (serial / concurrent start / concurrent finish / explicit GC.Collect) */
			if (gcs.explicit_)
				gcs.conc = 3;
			gcs.kind_n [gcs.conc]++;
			if (us > gcs.kind_max [gcs.conc])
				gcs.kind_max [gcs.conc] = us;
			unsigned v = (us & 0x3fffffffu) | ((unsigned) gcs.conc << 30), i = gcs.majseq_n++;
			gcs.majseq [i < K9_MAJSEQ_HEAD ? i : K9_MAJSEQ_HEAD + (i - K9_MAJSEQ_HEAD) % K9_MAJSEQ_RING] = v;
		}
		k7_gc_note (gcs.stop_t, us);   /* kit #7: the Chrome trace's GC rows (only with the profiler block) */
		gcs.stop_t = 0;
		if (g)
			k10_mem_after_major (gcs.conc);   /* kit #10 memory: the memory timeline's sample (test only: SHEPHERD_K10_MEMLOG); a completed major clears the pool's cap-drop flag (R3) */
		g_gc_pause_total_us += us;   /* kit #6: the engine's per-frame GC time (shepherd_vita_gc_pause_us) */
#if !defined (SHEPHERD_MAC_CHECK)
		{
			long long heap = gc_heap_bytes ();
			/* M2's growth gate: what this major left (sgen filled its info before restarting the world). kit #9 gc review: under lever 32
			 * k9_gc_base_update keeps it right (a concurrent START pause frees nothing; a finishing pause's sweep runs on the worker
			 * afterwards) -- with lever 32 off it answers 0 and this is kit #8's line exactly */
			if (!k9_gc_base_update (g, gcs.conc, heap, &g_gc_major_base) && g)
				g_gc_major_base = heap;
			k9_gc_after_collection (heap);   /* kit #9 gc: the GC heap's peak at collections (plain loads) */
		}
#endif
		break;
	}
	default:
		break;
	}
}

static int cmp_u (const void *a, const void *b)
{
	unsigned x = *(const unsigned *) a & 0x7fffffffu, y = *(const unsigned *) b & 0x7fffffffu;
	return x < y ? -1 : x > y;
}

/* p50/p99/max (ms) of the pauses since the previous call (or of the last PAUSE_RING ones) */
static void pause_window (char *out, size_t n)
{
	static unsigned tmp [PAUSE_RING];
	unsigned end = gcs.ring_n, start = gcs.window_start;
	if (end - start > PAUSE_RING)
		start = end - PAUSE_RING;
	unsigned k = 0, majors = 0;
	for (unsigned i = start; i < end; ++i) {
		tmp [k] = gcs.ring [i % PAUSE_RING];
		majors += tmp [k] >> 31;
		k++;
	}
	gcs.window_start = end;
	if (!k) {
		snprintf (out, n, "no GC pause since the previous checkpoint");
		return;
	}
	qsort (tmp, k, sizeof (unsigned), cmp_u);
	unsigned p50 = tmp [k / 2] & 0x7fffffffu, p99 = tmp [(k * 99) / 100 < k ? (k * 99) / 100 : k - 1] & 0x7fffffffu, mx = tmp [k - 1] & 0x7fffffffu;
	snprintf (out, n, "%u pauses (%u major) p50 %.1f p99 %.1f max %.1f ms", k, majors, p50 / 1000.0, p99 / 1000.0, mx / 1000.0);
}

/* ---------------------------------------------------------------- OutOfMemoryException = the heap limit was hit */

static void exception_throw_cb (MonoProfiler *prof, MonoObject *exc)
{
	MonoClass *k = exc ? mono_object_get_class (exc) : NULL;
	if (!k || strcmp (mono_class_get_name (k), "OutOfMemoryException") || strcmp (mono_class_get_namespace (k), "System"))
		return;
	int n = ++g_oom_count;
	if (n <= 5) {
		LOG ("FAIL OutOfMemoryException thrown (#%d): the GC heap limit was hit (max-heap-size=%um; arena %u MiB). It may be caught, the run goes on:",
		     n, g_max_heap_mb, g_arena_bytes / MiB);
		log_mem_ex ("oom", 1);
	}
#ifdef SHP_PLAY_APP
	if (g_mem_guard && !g_host_quit) {
		g_host_quit = 1;
		LOG ("FAIL MEM memory guard: an OutOfMemoryException -- the app ends this session after the current frame (red END, every log kept) "
		     "instead of running on with a broken menu into a crash. Start it again; open each big sub-menu (Settings, Credits, Roadmap, "
		     "Achievements) at most once per start");
	}
#endif
}

/* sgen's SgenMemgovSpace (sgen-gc.h): 0 nursery, 1 major heap (16 KiB marksweep blocks), 2 large-object space. (mono-vita.h's
 * comment on the callback has 0/1 the wrong way round.) */
static const char *sgen_space_name (int space) { return space == 2 ? "large objects" : space == 1 ? "major heap" : "nursery"; }

/* sgen refused an allocation because of max-heap-size (it collects and retries next; called with the GC lock held, so
 * this only logs). Reaching the cap is not a failure by itself (device kit #2 review: the GL apps' smaller arenas meet it during
 * the boot's peak and go on): the first refusal is a WARN LIMIT line, the count is on every checkpoint and the END line. A refusal
 * that survives the collection becomes an OutOfMemoryException (a FAIL line, exception_throw_cb) or, on the interpreter's box paths
 * that do not check for NULL, a crash (seen on the Mac with a 64-bit cap of 175/168 MiB) -- so this line is written before that can
 * happen. */
static void heap_limit_cb (size_t request, int space, size_t allocated, size_t max_heap, unsigned count)
{
	if (count == 1)
		LOG ("WARN LIMIT GC heap cap reached: sgen refused %u KiB (%s) at %u of %u KiB max-heap-size; it collects and retries. Not a "
		     "failure by itself: only a refusal the collection cannot satisfy becomes an OutOfMemoryException (FAIL), or a crash right "
		     "after this (the interpreter's box paths do not check for NULL). Refusals are counted on every checkpoint and on the END line",
		     (unsigned) (request / 1024), sgen_space_name (space), (unsigned) (allocated / 1024), (unsigned) (max_heap / 1024));
	else if (count <= 3 || count % 100 == 0)
		LOG ("LIMIT #%u: sgen refused %u KiB (%s) at %u of %u KiB max-heap-size; collecting%s", count, (unsigned) (request / 1024),
		     sgen_space_name (space), (unsigned) (allocated / 1024), (unsigned) (max_heap / 1024),
		     count == 3 ? " (further refusals: every 100th, and the count in each checkpoint)" : "");
#ifdef SHP_PLAY_APP
	/* the memory guard (kit #3 review): during play (the Main Menu answered the pad) a refusal means the heap is full to the cap even
	 * after the soft limit's major collections -- the next sub-menu opening ends in OutOfMemoryExceptions and an unhandled one (vlx vr5:
	 * Settings opened a third time). End the session cleanly now; the boot's own peak (before the menu) is left alone. Only a flag and
	 * a log line here: the GC lock is held. */
	/* (kit #10 memory review fix R3: not while the shared pool's cap has just fallen below what sgen holds and no major has run since --
	 * k10_guard_spare counts it; never set outside the pool) */
	if (g_mem_guard && !g_host_quit && g_interactive - g_title_resp > 0 && !k10_guard_spare ()) {   /* (kit #9 review round 2: the title press does not arm it) */
		g_host_quit = 1;
		LOG ("FAIL MEM memory guard: the GC heap reached its %u MiB cap during play -- the app ends this session after the current frame "
		     "(red END, every log kept) before the Vita runs out of memory and crashes. Start it again; open each big sub-menu (Settings, "
		     "Credits, Roadmap, Achievements) at most once per start (SHEPHERD_MEM_GUARD=0 in " SHP_ENV_NAME " turns the guard off)",
		     (unsigned) (max_heap / MiB));
	}
#endif
}

/* ---------------------------------------------------------------- hooks used by the runtime and the shim */

static unsigned g_console_lines, g_console_extra;
static int g_console_cont;               /* the previous kept line was an EXC line: keep its indented stack lines too */
static char g_last_engine_line [128];    /* for the STALL? line */
#define SHP_CONSOLE_EXTRA_MAX 400u
/* an engine.log line ("[frame] TAG message") worth keeping after the first 40: WRN / ERR / EXC (and an EXC line's stack lines),
 * [gl] (the GL backend: host, context, programs, FALLBACK), [audio], [vitamem] (the memory parts), the final "done:" line */
static int console_important (const char *l)
{
	const char *b = strstr (l, "] ");
	if (!b || l [0] != '[')
		return 0;
	b += 2;
	if (!strncmp (b, "WRN", 3) || !strncmp (b, "ERR", 3))
		return 1;
	if (!strncmp (b, "EXC", 3))
		return 2;
	return strstr (b, "[gl]") || strstr (b, "[audio]") || strstr (b, "[vitamem]") || !strncmp (b, "    done: ", 10);
}
void shepherd_vita_console_write (int fd, const char *buf, int len)
{
	/* Console.Out/Error via SystemNative_Write. The engine echoes every engine.log line to stdout: keep the first 40 here
	 * (early path/startup problems), then only the important ones (console_important, up to SHP_CONSOLE_EXTRA_MAX: engine.log has
	 * the rest, and before device kit #2 a later app overwrote it) and count the others; stderr is always kept. */
	static char pending [2][512];
	static int npending [2];
	static int gl_programs_seen;
	int gl_programs_now = 0;
	int s = fd == 2 ? 1 : 0;
	pthread_mutex_lock (&log_lock);
	for (int i = 0; i < len; ++i) {
		char c = buf [i];
		if (c == '\n' || npending [s] == (int) sizeof (pending [s]) - 1) {
			pending [s][npending [s]] = 0;
			npending [s] = 0;
#ifdef SHP_DIAG
			int keep = s == 1 || ++g_console_lines <= 120;   /* diag: more of the engine's echo (engine.log is the full copy) */
#else
			int keep = s == 1 || ++g_console_lines <= 40;
#endif
			g_activity_t = sceKernelGetProcessTimeWide ();
			if (s == 0) {
				if (!gl_programs_seen && strstr (pending [s], "[gl] ") && strstr (pending [s], " programs compiled+linked in "))
					gl_programs_seen = gl_programs_now = 1;   /* kit #5 review: GlBackend built its programs (the STK gl-programs line) */
				k7_ring_put (1, pending [s]);   /* kit #7: every engine line, kept here or not */
				size_t ll = strlen (pending [s]) < sizeof (g_last_engine_line) - 1 ? strlen (pending [s]) : sizeof (g_last_engine_line) - 1;
				memcpy (g_last_engine_line, pending [s], ll);   /* (truncated on purpose) */
				g_last_engine_line [ll] = 0;
				int imp = console_important (pending [s]);
				if (!imp && g_console_cont && !strncmp (pending [s], "      ", 6))
					imp = 3;   /* a stack line of the EXC line before it */
				g_console_cont = imp == 2 || imp == 3;
				if (!keep && imp && g_console_extra < SHP_CONSOLE_EXTRA_MAX) {
					keep = 1;
					if (++g_console_extra == SHP_CONSOLE_EXTRA_MAX) {
						pthread_mutex_unlock (&log_lock);
						log_line ("C> (the host log keeps no more engine lines from here; engine.log in this app's runtime/logs folder has them all)");
						pthread_mutex_lock (&log_lock);
					}
				}
			}
			if (keep) {
				char line [600];
				snprintf (line, sizeof (line), "C%d> %s", fd, pending [s]);
				pthread_mutex_unlock (&log_lock);
				log_line (line);
				pthread_mutex_lock (&log_lock);
			}
			if (c == '\n')
				continue;
		}
		if (c != '\r')
			pending [s][npending [s]++] = c;
	}
	pthread_mutex_unlock (&log_lock);
	if (gl_programs_now)
		stack_watch_mono_line ("gl-programs", "GlBackend's programs built on this thread: vitaGL's glCompileShader / glLinkProgram -> SceShaccCg "
				       "(under vlx a GL recorder: no compiler), before the boot GC");
}

void shepherd_vita_native_log (const char *msg)
{
	LOG ("N> %s", msg);
}

static void mono_log_cb (const char *domain, const char *level, const char *message, mono_bool fatal, void *user_data)
{
#ifdef SHP_DIAG
	/* the whole message, in 800-byte pieces (LOG's buffer would cut it at ~1000) */
	size_t mlen = message ? strlen (message) : 0;
	for (size_t off = 0; off < mlen || off == 0; off += 800) {
		LOG ("R> [%s %s]%s %.800s", domain ? domain : "mono", level ? level : "", off ? " (cont.)" : "", message ? message + off : "(null)");
		if (!mlen)
			break;
	}
	if (fatal)
		LOG ("FAIL (diag) the runtime stops here; the N> EXC / EXC-FAIL / SUSPEND-STATE lines and the last HB / PHASE lines above are the details");
#else
	LOG ("R> [%s %s] %s", domain ? domain : "mono", level ? level : "", message);
#endif
	if (fatal) {
		log_mem_ex ("fatal", 1);
		LOG ("FAIL runtime fatal error (see the R> line above)");
		k7_bb_event ("a runtime fatal error (the R> line)");   /* kit #7: before the abort */
		/* kit #3 review: after OutOfMemoryExceptions a fatal error is the full heap's (e.g. mini-exceptions.c "Could not allocate" while
		 * throwing the next one): its cause is in the log already, so the host ends the run cleanly (end_run_now: END line, red screen)
		 * instead of the abort's crash dialog. Any other fatal error keeps the abort and its core dump (a bug to look at). */
		if (g_oom_count > 0)
			end_run_now ("a runtime fatal error after OutOfMemoryExceptions (the GC heap was full) ended the run");
		if (message && strstr (message, "K14 interpreter stack refused"))   /* kit #14 integration: the same fullness, seen by a new thread first (vita/patches/k14-stack-refused.patch) */
			end_run_now ("the shared pool has no interpreter stack for a new thread (the memory is full) ended the run");
		if (g_screen)
			sceKernelDelayThread (10 * 1000 * 1000);
		abort ();   /* newlib's abort traps: system error dialog + core dump. Not a handler. */
	}
}

static void mono_print_cb (const char *string, mono_bool is_stdout)
{
	const char *prefix = is_stdout ? "R> " : "R!> ";
	if (!is_stdout && strstr (string, "Unhandled Exception:"))
		g_unhandled_printed = 1;
	for (const char *p = string; *p;) {
		const char *nl = strchr (p, '\n');
		size_t n = nl ? (size_t) (nl - p) : strlen (p);
		for (size_t off = 0; off < n; off += 900) {
			size_t chunk = n - off < 900 ? n - off : 900;
			LOG ("%s%.*s", prefix, (int) chunk, p + off);
		}
		p += n + (nl ? 1 : 0);
	}
}

static void *pinvoke_override (const char *library, const char *entry)
{
	/* every DllImport target the Vita can answer is linked into the eboot (shim, stubs, host exports) */
	return mono_vita_lookup_static_symbol (entry);
}

static void arena_log (const char *event, const MonoVitaArenaStats *s, size_t request)
{
	LOG ("ARENA %s: used=%u KiB peak=%u KiB of %u KiB, %u extents (largest %u KiB), request=%u KiB",
	     event, (unsigned) (s->used / 1024), (unsigned) (s->peak / 1024), (unsigned) (s->arena_size / 1024),
	     s->free_extents, (unsigned) (s->largest_free / 1024), (unsigned) (request / 1024));
	/* (kit #14 newlib: above one 16-KiB block sgen asks for half as many blocks, down to one, so that refusal is not the end. Kit #14 integration: the runtime names those requests
	 * ("FAIL blocks"), and any other refusal over a block keeps a FAIL line: an interpreter stack is the same size as a halved request, and a large object section ends the same way.
	 * SHEPHERD_K14_OFF=0x400 = by size alone; the track off = kit #13's line) */
	if (!strncmp (event, "FAIL", 4) && strcmp (event, "FAIL blocks")) {
		if (request <= 16384u || k14_cap_over () == 22u)
			LOG ("FAIL Mono arena exhausted (%u KiB requested): raise SHEPHERD_ARENA_MB or lower max-heap-size in env.txt; sgen exits next", (unsigned) (request / 1024));
		else if (k14_refusals_named ())
			LOG ("FAIL Mono arena refused %u KiB, not a block request sgen halves: the shared pool is full", (unsigned) (request / 1024));
	}
}

/* ---------------------------------------------------------------- files */

static int file_size (const char *path, unsigned *size)
{
	SceIoStat st;
	if (sceIoGetstat (path, &st) < 0 || SCE_S_ISDIR (st.st_mode))
		return -1;
	if (size)
		*size = (unsigned) st.st_size;
	return 0;
}

static int read_file_ex (const char *path, void **data, unsigned *size, int il)
{
	unsigned n;
	if (file_size (path, &n) != 0)
		return -1;
	SceUID fd = sceIoOpen (path, SCE_O_RDONLY, 0);
	if (fd < 0)
		return -1;
	char *buf = il ? (char *) il_pool_alloc (n + 1) : NULL;   /* kit #5: an IL image goes to the IL pool while it has room */
	if (!buf)
		buf = (char *) malloc (n + 1);
	if (!buf) {
		sceIoClose (fd);
		return -2;
	}
	unsigned done = 0;
	while (done < n) {
		int r = sceIoRead (fd, buf + done, n - done > 4 * MiB ? 4 * MiB : n - done);
		if (r <= 0)
			break;
		done += (unsigned) r;
	}
	sceIoClose (fd);
	if (done != n) {
		if (in_il_pool (buf))
			il_pool_rewind (buf, n + 1);   /* the last pool allocation: rewound (kit #10: unless another came after it) */
		else
			free (buf);
		return -3;
	}
	buf [n] = 0;
	*data = buf;
	*size = n;
	if (in_il_pool (buf))
		__atomic_add_fetch (&g_il_pool_files, 1, __ATOMIC_RELAXED);
	return 0;
}
#define read_file(path, data, size) read_file_ex ((path), (data), (size), 0)

static void apply_env_file (void)
{
	void *data;
	unsigned size;
	if (read_file (DIR_ "/" SHP_ENV_NAME, &data, &size) != 0)
		return;
	char *save = NULL;
	for (char *line = strtok_r ((char *) data, "\r\n", &save); line; line = strtok_r (NULL, "\r\n", &save)) {
		char *eq = strchr (line, '=');
		if (!eq || line [0] == '#')
			continue;
		*eq = 0;
		setenv (line, eq + 1, 1);
		LOG ("H1 env %s=%s (" SHP_ENV_NAME ")", line, eq + 1);
	}
	free (data);
}

/* kit #5 M3: the thread stacks and the IL pool in force (SHP_K5_* above); env file: SHEPHERD_STACKS=kit4 = kit #4's partition, then the
 * single knobs override (each within sane bounds). One H1 stacks line says what is in force. */
static unsigned env_uint (const char *k, unsigned lo, unsigned hi, unsigned def)
{
	const char *e = getenv (k);
	if (!e || !*e)
		return def;
	char *end;
	unsigned long v = strtoul (e, &end, 10);
	if (*end || v < lo || v > hi) {
		LOG ("WARN %s=%s ignored (%u..%u): %u", k, e, lo, hi, def);
		return def;
	}
	return (unsigned) v;
}
static void stacks_config (void)
{
	const char *st = getenv ("SHEPHERD_STACKS");
	int k4 = st && !strcmp (st, "kit4"), k5 = st && !strcmp (st, "kit5");
	unsigned mono_kb = env_uint ("SHEPHERD_MONO_STACK_KB", 512, 8192, k4 ? SHP_MONO_STACK / 1024u : k5 ? SHP_K5_MONO_STACK_KB : SHP_K6_MONO_STACK_KB);
	unsigned thr_kb = env_uint ("SHEPHERD_THREAD_STACK_KB", 64, 1024, k4 ? 1024u : k5 ? SHP_K5_THREAD_STACK_KB : SHP_K6_THREAD_STACK_KB);
	g_stack_reserve_mb = env_uint ("SHEPHERD_STACK_RESERVE_MB", 4, 32, k4 ? SHP_STACK_RESERVE_MB : k5 ? SHP_K5_STACK_RESERVE_MB : SHP_K6_STACK_RESERVE_MB);
	g_il_pool_mb = env_uint ("SHEPHERD_IL_POOL_MB", 0, 64, k4 ? 0u : k5 ? SHP_K5_IL_POOL_MB : SHP_K6_IL_POOL_MB);
	g_mono_stack_req = (mono_kb * 1024u + 4095u) & ~4095u;
	g_thread_stack = thr_kb * 1024u;
	const char *sw = getenv ("SHEPHERD_STACK_WATCH");
	g_sw_on = !(sw && !strcmp (sw, "0"));
#ifdef SHEPHERD_MAC_CHECK
	g_sw_on = 0;   /* mac-check: real Mac threads (guard pages, other bounds); the watch is a Vita / vlx measurement */
#endif
	LOG ("H1 stacks (kit #6: sized from kit #5's device STK lines): Mono thread %u KiB, Mono's other threads %u KiB (the MT render thread asks for its own: "
	     "SHEPHERD_RENDER_STACK_KB, default %u); %u MiB kept free for thread stacks (+ %u safety); IL pool %u MiB%s; stack watch %s "
	     "(kit #5: 8192 KiB, 256 KiB, render 1024, 16 MiB, IL pool 4 -- SHEPHERD_STACKS=kit5; kit #4: 8192, 1024, 20 MiB, none -- SHEPHERD_STACKS=kit4; "
	     "knobs SHEPHERD_MONO_STACK_KB / _THREAD_STACK_KB / _STACK_RESERVE_MB / _IL_POOL_MB, SHEPHERD_STACK_WATCH=0)", g_mono_stack_req / 1024, thr_kb,
	     k4 || k5 ? 1024u : 512u, g_stack_reserve_mb, SHP_SAFETY_MB, g_il_pool_mb,
	     k4 ? " -- SHEPHERD_STACKS=kit4" : k5 ? " -- SHEPHERD_STACKS=kit5" : "", g_sw_on ? "ON (STK lines: each Mono thread's deepest stack use)" : "off");
}

/* kit #5 review (track mem): the stack watch -- each Mono thread's deepest stack use ON THE DEVICE (the Vita has no guard page: a stack
 * that is too small corrupts memory silently, so kit #5 keeps sizes vlx alone cannot prove -- the Mono thread's 8 MiB, the render thread's
 * 1 MiB -- and kit #6 sizes them from these lines). A thread's stack is painted below its stack pointer when it starts (the Mono thread
 * first thing in mono_thread_body; every thread Mono starts or attaches from its profiler thread_started event, on that thread) and
 * scanned from the bottom for the first word that no longer holds the paint: deepest use = top of the stack - that word. Bounds from
 * sceKernelGetThreadInfo, read the way the runtime reads them (mono-threads-vita.c). The paint is vlx's own STACK_PAINT, so under
 * VLX_STACK_REPORT=1 vlx and these lines measure the same words (a cross-check), and it is no address in user memory (the device's is at
 * 0x81000000 and up): a stale paint word in a frame never pins a GC object. Lines: STK watch (the Mono thread painted), STK gl-programs
 * (right after the engine's "[gl] N programs compiled+linked" line: the shader compiler's depth, before the boot GC clears 256 KiB),
 * STK cp0 (the first checkpoint), STK end (every watched thread alive at the end, and the deepest of the ended ones by name). Cost
 * (ESTIMATE): painting the 8 MiB Mono stack once at start and scanning it 3 times, ~10-30 ms each on the device; 256 KiB / 1 MiB per
 * other thread. SHEPHERD_STACK_WATCH=0: none of it (kit #4). */
#define SW_PAINT 0x5A5AA5A5u   /* = vita/vlx/vlx.c STACK_PAINT */
#define SW_BOTTOM_GAP 4096u    /* never painted or scanned: the lowest 4 KiB of a stack (use up to there reads as "at the limit") */
#define SW_SP_GAP 512u         /* the painter's own frame */
#define SW_SLOTS 24
#define SW_ENDED 12
typedef struct { SceUID uid; uintptr_t tid; char *lo; unsigned size, painted_from; char name [32]; } SwSlot;
static SwSlot g_sw [SW_SLOTS];
static struct { char name [32]; unsigned size, count, deepest, deepest_data; } g_sw_ended [SW_ENDED];
static unsigned g_sw_dropped, g_sw_unbounded;
static pthread_mutex_t g_sw_lock = PTHREAD_MUTEX_INITIALIZER;
static SceUID g_sw_mono_uid;

#if !defined (SHEPHERD_MAC_CHECK)
/* the calling thread's stack: [*lo, *lo + *size); -1 if the kernel's answer does not contain this frame */
static int sw_bounds (char **lo, unsigned *size)
{
	SceKernelThreadInfo info;
	memset (&info, 0, sizeof (info));
	info.size = sizeof (info);
	if (sceKernelGetThreadInfo (sceKernelGetThreadId (), &info) < 0)
		return -1;
	char *st = (char *) info.stack, *sp = (char *) &info;
	unsigned sz = (unsigned) info.stackSize;
	if (!(sp >= st && sp < st + sz)) {
		if (sp < st && sp >= st - sz)
			st -= sz;   /* (the other reading: stack = its top; the runtime accepts both) */
		else
			return -1;
	}
	*lo = st;
	*size = sz;
	return 0;
}
/* paints [lo + SW_BOTTOM_GAP, this frame - SW_SP_GAP); returns the bytes from the top it leaves unpainted (the measure's floor) */
static unsigned __attribute__ ((noinline)) sw_paint (char *lo, unsigned size)
{
	volatile uint32_t here = 0;
	uint32_t *p = (uint32_t *) (lo + SW_BOTTOM_GAP), *e = (uint32_t *) (((uintptr_t) &here - SW_SP_GAP) & ~(uintptr_t) 3);
	for (; p < e; ++p)
		*p = SW_PAINT;
	return (unsigned) (lo + size - (char *) e);
}
/* deepest use (bytes from the top to the lowest word that is not the paint) and the deepest data word (neither the paint nor 0: a region
 * zeroed on purpose -- the boot GC's 256 KiB clearing -- counts as used above, not here); one pass from the bottom */
static void sw_scan (const SwSlot *w, unsigned *used, unsigned *data)
{
	const uint32_t *p = (const uint32_t *) (w->lo + SW_BOTTOM_GAP), *e = (const uint32_t *) (w->lo + w->size);
	while (p < e && *p == SW_PAINT)
		++p;
	*used = (unsigned) ((const char *) e - (const char *) p);
	while (p < e && (*p == SW_PAINT || *p == 0))
		++p;
	*data = (unsigned) ((const char *) e - (const char *) p);
}
static SwSlot *sw_find_uid (SceUID uid)
{
	for (int i = 0; i < SW_SLOTS; ++i)
		if (g_sw [i].uid == uid && g_sw [i].lo)
			return &g_sw [i];
	return NULL;
}
/* the calling thread: paint and register (once: a thread already watched keeps its paint -- the Mono thread's attach event) */
static void sw_register_self (uintptr_t tid, const char *name)
{
	SceUID me = sceKernelGetThreadId ();
	pthread_mutex_lock (&g_sw_lock);
	SwSlot *w = sw_find_uid (me);
	if (w) {
		if (tid)
			w->tid = tid;
		pthread_mutex_unlock (&g_sw_lock);
		return;
	}
	for (int i = 0; i < SW_SLOTS && !w; ++i)
		if (!g_sw [i].lo)
			w = &g_sw [i];
	if (!w) {
		g_sw_dropped++;
		pthread_mutex_unlock (&g_sw_lock);
		return;
	}
	char *lo;
	unsigned size;
	if (sw_bounds (&lo, &size) != 0 || size <= 2u * SW_BOTTOM_GAP) {
		g_sw_unbounded++;
		pthread_mutex_unlock (&g_sw_lock);
		return;
	}
	memset (w, 0, sizeof (*w));
	w->uid = me;
	w->tid = tid;
	w->size = size;
	snprintf (w->name, sizeof (w->name), "%s", name ? name : "");
	w->painted_from = sw_paint (lo, size);
	w->lo = lo;   /* (last: a slot with lo is complete) */
	pthread_mutex_unlock (&g_sw_lock);
}
static void sw_thread_started (MonoProfiler *prof, uintptr_t tid) { (void) prof; sw_register_self (tid, NULL); }
static void sw_thread_name (MonoProfiler *prof, uintptr_t tid, const char *name)
{
	(void) prof;
	pthread_mutex_lock (&g_sw_lock);
	for (int i = 0; i < SW_SLOTS; ++i)
		if (g_sw [i].lo && g_sw [i].tid == tid && tid)
			snprintf (g_sw [i].name, sizeof (g_sw [i].name), "%s", name ? name : "");
	pthread_mutex_unlock (&g_sw_lock);
}
/* raised on the stopping thread itself (mono_thread_detach_internal), its stack still there: scanned, kept by name, the slot freed */
static void sw_thread_stopped (MonoProfiler *prof, uintptr_t tid)
{
	(void) prof; (void) tid;
	pthread_mutex_lock (&g_sw_lock);
	SwSlot *w = sw_find_uid (sceKernelGetThreadId ());
	if (w && w->uid != g_sw_mono_uid) {   /* (the Mono thread keeps its slot for the end line) */
		unsigned u, d;
		sw_scan (w, &u, &d);
		const char *n = w->name [0] ? w->name : "(unnamed)";
		int k = -1;
		for (int i = 0; i < SW_ENDED && k < 0; ++i)
			if (g_sw_ended [i].count && g_sw_ended [i].size == w->size && !strcmp (g_sw_ended [i].name, n))
				k = i;
		for (int i = 0; i < SW_ENDED && k < 0; ++i)
			if (!g_sw_ended [i].count)
				k = i;
		if (k >= 0) {
			if (!g_sw_ended [k].count) {
				snprintf (g_sw_ended [k].name, sizeof (g_sw_ended [k].name), "%s", n);
				g_sw_ended [k].size = w->size;
			}
			g_sw_ended [k].count++;
			if (u > g_sw_ended [k].deepest)
				g_sw_ended [k].deepest = u;
			if (d > g_sw_ended [k].deepest_data)
				g_sw_ended [k].deepest_data = d;
		}
		w->lo = NULL;
		w->uid = 0;
	}
	pthread_mutex_unlock (&g_sw_lock);
}
static void sw_line (const char *tag, const SwSlot *w, const char *state, const char *why)
{
	unsigned u, d;
	sw_scan (w, &u, &d);
	int limit = u >= w->size - SW_BOTTOM_GAP;
	LOG ("STK %s: %s%s%s stack %u KiB, deepest use %u KiB (%u B; deepest non-zero word %u B; painted below %u B)%s%s%s", tag,
	     w->uid == g_sw_mono_uid ? "the Mono thread" : w->name [0] ? w->name : "(unnamed)", state [0] ? " " : "", state, w->size / 1024,
	     (u + 1023) / 1024, u, d, w->painted_from, limit ? " -- AT THE LIMIT: the stack was used to its last 4 KiB or beyond" : "",
	     why [0] ? " | " : "", why);
}
static void stack_watch_mono_line (const char *tag, const char *why)
{
	if (!g_sw_on)
		return;
	pthread_mutex_lock (&g_sw_lock);
	SwSlot *w = sw_find_uid (g_sw_mono_uid);
	if (w)
		sw_line (tag, w, "", why);
	pthread_mutex_unlock (&g_sw_lock);
}
static void stack_watch_end_lines (const char *tag)
{
	if (!g_sw_on)
		return;
	pthread_mutex_lock (&g_sw_lock);
	SwSlot *m = sw_find_uid (g_sw_mono_uid);
	if (m)
		sw_line (tag, m, "", "");
	for (int i = 0; i < SW_SLOTS; ++i)
		if (g_sw [i].lo && &g_sw [i] != m) {
			SceKernelThreadInfo info;   /* only a thread the kernel still has (its stack with it) is read */
			memset (&info, 0, sizeof (info));
			info.size = sizeof (info);
			if (sceKernelGetThreadInfo (g_sw [i].uid, &info) >= 0)
				sw_line (tag, &g_sw [i], "(alive)", "");
		}
	for (int i = 0; i < SW_ENDED; ++i)
		if (g_sw_ended [i].count)
			LOG ("STK %s: %s (ended, %u thread%s) stack %u KiB, deepest use %u KiB (%u B; deepest non-zero word %u B)", tag, g_sw_ended [i].name,
			     g_sw_ended [i].count, g_sw_ended [i].count == 1 ? "" : "s", g_sw_ended [i].size / 1024, (g_sw_ended [i].deepest + 1023) / 1024,
			     g_sw_ended [i].deepest, g_sw_ended [i].deepest_data);
	if (g_sw_dropped || g_sw_unbounded)
		LOG ("STK %s: %u thread(s) not watched (the table full), %u without usable stack bounds", tag, g_sw_dropped, g_sw_unbounded);
	pthread_mutex_unlock (&g_sw_lock);
}
/* first thing on the Mono thread: the whole stack below this frame */
static void stack_watch_mono_start (void)
{
	if (!g_sw_on)
		return;
	g_sw_mono_uid = sceKernelGetThreadId ();
	SceUInt64 t0 = sceKernelGetProcessTimeWide ();
	sw_register_self (0, "Mono thread");
	SwSlot *w = sw_find_uid (g_sw_mono_uid);
	if (w)
		LOG ("STK watch: the Mono thread's stack %u KiB at %p painted below %u B from its top in %.1f ms; Mono's threads are painted when they "
		     "start (profiler thread events); deepest use on the STK gl-programs / cp0 / end lines (SHEPHERD_STACK_WATCH=0 turns this off)",
		     w->size / 1024, (void *) w->lo, w->painted_from, (double) (sceKernelGetProcessTimeWide () - t0) / 1000.0);
	else
		LOG ("WARN STK watch: no usable stack bounds for the Mono thread (sceKernelGetThreadInfo): no STK lines");
}
static void stack_watch_profiler (MonoProfilerHandle ph)
{
	if (!g_sw_on)
		return;
	mono_profiler_set_thread_started_callback (ph, sw_thread_started);
	mono_profiler_set_thread_name_callback (ph, sw_thread_name);
	mono_profiler_set_thread_stopped_callback (ph, sw_thread_stopped);
}
#else
static void stack_watch_mono_line (const char *tag, const char *why) { (void) tag; (void) why; }
static void stack_watch_end_lines (const char *tag) { (void) tag; }
static void stack_watch_mono_start (void) { }
static void stack_watch_profiler (MonoProfilerHandle ph) { (void) ph; }
#endif

/* ---------------------------------------------------------------- assembly loading (no bundles, no mmap) */

/* Program.IsBcl: these names are the .NET 8 framework's, never the game's own (Unity's old) copies in managed/ */
static int is_bcl (const char *n)
{
	return !strcmp (n, "mscorlib") || !strcmp (n, "netstandard") || !strncmp (n, "System", 6) || !strncmp (n, "Microsoft.", 10) || !strncmp (n, "Mono.", 5);
}

/* RECURSIVE (device run 2026-10-07, devfail1/menu-hang.md): Mono re-enters this hook on the SAME thread while the outer load
 * holds the lock. load_aot_module is an assembly load hook, so it runs inside mono_assembly_load_from_full below, and unless
 * --aot-lazy-assembly-load it loads every image the AOT module references (aot-runtime.c load_aot_module -> load_image ->
 * netcore_load_reference -> this hook): System.Runtime.InteropServices -> System.Collections.Concurrent, Shepherd.Assets ->
 * System.Text.Json/Parallel/IO.Compression. A NORMAL pthread-embedded mutex (PTHREAD_MUTEX_INITIALIZER) has no owner check:
 * the relock blocks forever in sceKernelWaitSema, GC-safe, so nothing logs. Never seen on the Mac (no AOT images there) nor
 * in the V5 hello (bundled assemblies, no hook). Other threads still load one at a time. */
static pthread_mutex_t load_lock = PTHREAD_RECURSIVE_MUTEX_INITIALIZER;
static volatile int g_runtime_up;   /* the loading thread is attached (after mono_jit_init_version): GC-safe regions allowed */

/* exported by the runtime (mono-threads-api.h, MONO_API) but not in the public headers */
void *mono_threads_enter_gc_safe_region (void **stackdata);
void mono_threads_exit_gc_safe_region (void *cookie, void **stackdata);

/* blocking work (waiting for the lock, reading the card) in a GC-safe region, so a stop-the-world never waits for it */
#define SAFE_BEGIN() void *sd_; int on_ = g_runtime_up; void *ck_ = on_ ? mono_threads_enter_gc_safe_region (&sd_) : NULL
#define SAFE_END() do { if (on_) mono_threads_exit_gc_safe_region (ck_, &sd_); } while (0)

/* ---------------------------------------------------------------- GC.Collect governor (kit #4, M2; vita/notes/kit4-bootgc.md)
 * The game's pause menu calls GC.Collect() every 5 s of unscaled time while it is open (UIPauseMenuController.Update). In this Mono
 * that is a stop-the-world FULL collection: ~0.9 s on the device's menu heap at 444 MHz (the forced full GC of every kit's
 * measurement) and more on a gameplay heap -- a freeze every 5 s on a screen the player is looking at. On the platforms the game
 * shipped on, Unity's GC.Collect costs tens of ms and is never seen.
 * GC.Collect() -> GC.InternalCollect (MaxGeneration) -> ves_icall_System_GC_InternalCollect, which CoreLib's AOT code calls directly
 * (direct-icalls) and the interpreter through the icall table: the link wraps it (-Wl,--wrap=ves_icall_System_GC_InternalCollect,
 * build.sh; vita/vlx/link.sh when this host.o defines the wrapper). A full collection asked for from a GOVERNED SITE -- the first
 * managed frame below System.GC that is not a runtime wrapper (the interpreter calls CoreLib's AOT code through one) is one of
 * SHEPHERD_GC_GOVERN's "Namespace.Class::Method" names -- runs as a nursery collection
 * instead, which sgen follows with a major one by its own rule when the old generation is over its trigger (sgen_gc_collect:
 * "Minor allowance"). Nothing else changes: other callers (the engine's measurements, VitaMemory's pool drop) and any other game
 * code still get a full collection. What can differ from a full collection is only when old-generation objects are finalized and
 * their weak references cleared -- not deterministic in Unity either.
 * SHEPHERD_GC_GOVERN (env file): unset = the pause menu's site; 0 / off / none = every GC.Collect a full collection (pre-kit-4);
 * else a ';'-separated site list. GCG lines: the setting at start, the first 3 governed calls, a count at the end.
 * GROWTH GATE (k4 review; M1's rule, Runtime/BlackFrameGc.cs): a governed call still runs the FULL collection when the major heap has
 * grown by at least half of sgen's allowance (a third of the heap the last major left, at least 16 MiB) since that major. The first
 * GC.Collect of a pause after play -- garbage built up while playing -- is then the full collection kit 3 ran, behind the static pause
 * menu (Time.timeScale 0), instead of being left for a major of sgen's own during play (a 0.8-3 s freeze there); the repeats every 5 s
 * on the static menu, where the heap hardly grows, are nursery collections. SHEPHERD_GC_GOVERN_GATE=0: every governed call a nursery
 * collection (the first kit-4 rule). The heap after each major is sampled at its end (gc_event_cb); the "now" figure is sgen's at the
 * last collection (minor or major), as GC.GetGCMemoryInfo gives it. */
#if !defined (SHEPHERD_MAC_CHECK)
#define GCGOV_DEFAULT "Lamb.UI.PauseMenu.UIPauseMenuController::Update"
void __real_ves_icall_System_GC_InternalCollect (int generation);
void __wrap_ves_icall_System_GC_InternalCollect (int generation);
static char g_gcgov_sites [512];         /* ";site;site;" (empty: off) */
static int g_gcgov_ready, g_gcgov_gate = 1;
static unsigned g_gcgov_calls, g_gcgov_governed, g_gcgov_majors, g_gcgov_gated_full;
static unsigned long long g_gcgov_us;

static void gcgov_config (void)
{
	const char *e = getenv ("SHEPHERD_GC_GOVERN");
	if (!e)
		e = GCGOV_DEFAULT;
	if (!*e || !strcmp (e, "0") || !strcasecmp (e, "off") || !strcasecmp (e, "none"))
		g_gcgov_sites [0] = 0;
	else
		snprintf (g_gcgov_sites, sizeof (g_gcgov_sites), ";%s;", e);
	g_gcgov_ready = 1;
	const char *gate = getenv ("SHEPHERD_GC_GOVERN_GATE");
	g_gcgov_gate = !(gate && (!strcmp (gate, "0") || !strcasecmp (gate, "off")));
	if (g_gcgov_sites [0])
		LOG ("GCG GC.Collect governor on: a full collection asked for from %s runs as a nursery collection (sgen adds a major one when the "
		     "old generation is over its trigger)%s; SHEPHERD_GC_GOVERN=0 in " SHP_ENV_NAME " restores the full collection", e,
		     g_gcgov_gate ? ", except when the major heap grew by half of sgen's allowance since the last major (growth gate: then the full one, "
				    "as before; SHEPHERD_GC_GOVERN_GATE=0 = always the nursery one)" : " (growth gate off: SHEPHERD_GC_GOVERN_GATE=0)");
	else
		LOG ("GCG GC.Collect governor off (SHEPHERD_GC_GOVERN=%s): every GC.Collect is a full collection", e);
}

typedef struct { char site [256]; } GcCaller;
static mono_bool gcgov_frame (MonoMethod *method, int32_t native_offset, int32_t il_offset, mono_bool managed, void *data)
{
	if (!method)
		return 0;
	MonoClass *k = mono_method_get_class (method);
	const char *ns = k ? mono_class_get_namespace (k) : "", *cn = k ? mono_class_get_name (k) : "?";
	if (k && !strcmp (cn, "GC") && !strcmp (ns, "System"))
		return 0;   /* GC.Collect itself (and its icall wrapper) */
	if (!mono_method_get_token (method))
		return 0;   /* a runtime wrapper (no metadata token): the interpreter reaches CoreLib's AOT code through System.Object::gsharedvt_out_sig */
	snprintf (((GcCaller *) data)->site, sizeof (((GcCaller *) data)->site), "%s%s%s::%s", ns, *ns ? "." : "", cn, mono_method_get_name (method));
	return 1;
}

void __wrap_ves_icall_System_GC_InternalCollect (int generation)
{
	if (!g_gcgov_ready)
		gcgov_config ();
	g_gcgov_calls++;
	GcCaller c = { "" };
	char key [260];
	if (g_gcgov_sites [0] && generation > 0) {
		mono_stack_walk_no_il (gcgov_frame, &c);
		snprintf (key, sizeof (key), ";%s;", c.site);
	}
	if (!g_gcgov_sites [0] || generation <= 0 || !c.site [0] || !strstr (g_gcgov_sites, key)) {
		if (g_gcgov_sites [0] && generation > 0 && g_gcgov_calls - g_gcgov_governed <= 8) {   /* the first ungoverned full collections: from where */
			SAFE_BEGIN ();
			LOG ("GCG GC.Collect (generation %d) from %s: a full collection (not a governed site)", generation, c.site [0] ? c.site : "(no managed frame found)");
			SAFE_END ();
		}
		if (generation > 0) {
			g_gc_explicit_tid = sceKernelGetThreadId ();   /* kit #9 gc review: its pause is kind 3 'x' on the K9 GC lines */
			g_gcgov_full_calls++;
		}
		__real_ves_icall_System_GC_InternalCollect (generation);
		g_gc_explicit_tid = 0;
		return;
	}
	if (g_gcgov_gate) {   /* the growth gate: garbage built up since the last major (play before the pause) -> the full collection */
		long long now = gc_heap_bytes (), base = g_gc_major_base;
		k9_gc_base_update (0, -1, now, &base);   /* kit #9 gc review: lever 32 -- the worker's sweep may have lowered sgen's figure since (a local copy; no-op otherwise) */
		double allowance = base > 0 ? base / 3.0 : 0, grown = base < 0 ? -1 : (double) (now - base);
		if (allowance < 16.0 * 1048576)
			allowance = 16.0 * 1048576;
		if (base < 0 || grown >= 0.5 * allowance) {
			SceUInt64 tf = sceKernelGetProcessTimeWide ();
			g_gc_explicit_tid = sceKernelGetThreadId ();   /* kit #9 gc review: kind 3 'x' */
			g_gcgov_full_calls++;
			__real_ves_icall_System_GC_InternalCollect (generation);
			g_gc_explicit_tid = 0;
			unsigned usf = (unsigned) (sceKernelGetProcessTimeWide () - tf);
			g_gcgov_gated_full++;
			if (g_gcgov_gated_full <= 3) {
				SAFE_BEGIN ();
				if (base < 0)
					LOG ("GCG GC.Collect() from %s: a full collection, %.1f ms (growth gate: no major seen yet)", c.site, usf / 1000.0);
				else
					LOG ("GCG GC.Collect() from %s: a full collection, %.1f ms (growth gate: the major heap grew %.1f MiB since the last major, "
					     ">= half of sgen's allowance %.1f MiB)%s", c.site, usf / 1000.0, grown / 1048576.0, allowance / 1048576.0,
					     g_gcgov_gated_full == 3 ? " (further ones: counted on the END GCG line)" : "");
				SAFE_END ();
			}
			return;
		}
	}
	unsigned majors0 = gcs.count [1];
	SceUInt64 t0 = sceKernelGetProcessTimeWide ();
	__real_ves_icall_System_GC_InternalCollect (0);
	unsigned us = (unsigned) (sceKernelGetProcessTimeWide () - t0);
	int major = gcs.count [1] != majors0;
	g_gcgov_governed++;
	g_gcgov_us += us;
	g_gcgov_majors += major;
	if (g_gcgov_governed <= 3) {
		SAFE_BEGIN ();
		LOG ("GCG #%u GC.Collect() from %s: nursery collection instead of a full one, %.1f ms%s%s", g_gcgov_governed, c.site, us / 1000.0,
		     major ? " (sgen's trigger asked for a major one too)" : "", g_gcgov_governed == 3 ? " (further ones: counted on the END GCG line)" : "");
		SAFE_END ();
	}
}

static void gcgov_end_line (void)
{
	if (!g_gcgov_ready)
		return;   /* no GC.Collect at all in this run */
	LOG ("GCG end: %u GC.Collect calls, %u governed (%u with sgen's own major), %.1f ms in governed collections; %u governed-site calls kept "
	     "full by the growth gate", g_gcgov_calls, g_gcgov_governed, g_gcgov_majors, g_gcgov_us / 1000.0, g_gcgov_gated_full);
}
#else
static void gcgov_end_line (void) { }
#endif

/* B-3 (kit #4): the System.Native shim's read-ahead (vita/runtime/system-native/pal_vita.c, SHEPHERD_IO_READAHEAD_KB) */
#if !defined (SHEPHERD_MAC_CHECK)
void shepherd_vita_io_readahead_stats (unsigned *calls, unsigned *served, unsigned *fills, unsigned long long *fill_bytes, unsigned *direct,
				       int32_t *size_kib);
static void io_readahead_line (void)
{
	unsigned calls, served, fills, direct;
	unsigned long long bytes;
	int32_t kib;
	shepherd_vita_io_readahead_stats (&calls, &served, &fills, &bytes, &direct, &kib);
	if (kib < 0)
		LOG ("IO read-ahead: no read-only file was opened through the shim");
	else if (!kib)
		LOG ("IO read-ahead off (SHEPHERD_IO_READAHEAD_KB=0): every small read went to the card");
	else
		LOG ("IO read-ahead (%d KiB, SHEPHERD_IO_READAHEAD_KB; 0 = off): %u small reads of read-only files -> %u card reads (%u buffer fills, %.1f MiB, "
		     "+ %u direct); %u served from the buffers", kib, calls, fills + direct, fills, bytes / 1048576.0, direct, served);
}
#else
static void io_readahead_line (void) { }
#endif

#ifdef SHP_DIAG
#include "host_diag.inc.c"            /* vita/shepherd/diag: heartbeat, counters, phases, the preload hook wrapper */
#define preload_hook preload_hook_impl   /* the hook below becomes the body the diag wrapper calls */
#endif

/* Default-ALC by-name loads (netcore_load_reference step 6, before the managed Resolving event; CoreLib too): read the
 * DLL into the newlib heap and open the image from that memory (Mono keeps the buffer; nothing is mapped).
 *   BCL names        -> app0:managed/<name>.dll only (the framework + the engine's Microsoft.Bcl.AsyncInterfaces)
 *   everything else  -> app0:managed (Shepherd.*), then ux0:data/shepherd/managed (game + rewritten Unity)
 * The file is read in a GC-safe region (a 16 MB DLL takes ~1 s from the memory card; a stop-the-world must not wait on it). */
/* kit #9 fidelity, review fix (vita/notes/kit9-fidelity.md "Review fixes"): the env-file switch for row 2 (float precision like the PC's Mono).
 * SHEPHERD_K9_FLOATPC=1 takes the game's assemblies first from ux0:data/shepherd/managed-floatpc (=<name>: from ux0:data/shepherd/<name>, a
 * plain folder name), the DLLs tools/FloatPc rewrote, and from managed/ for every name that folder does not hold; unset / 0 = managed/ only
 * (kit #8). One card holds both sets, so a device A/B is one env-file line. Only the game's own DLLs belong there: an assembly an AOT image
 * binds (the engine image's table: UnityEngine.* modules, UnityEngine.UI, FMODUnity; 00095's owner modules) gets a new MVID
 * from FloatPc, and Mono (aot-only mode) stops with a fatal error at an image bound to the old one unless the eboot was built against the same
 * rewritten set; the host refuses those (below). The game-AOT apps (00095 / 00194) leave the owner modules unregistered when the folder holds
 * their assemblies (H7 below; SHEPHERD_GAME_AOT=1 keeps them for an owner AOT built against the rewritten set). */
static char g_k9_fpc_dir [64];   /* "" = off */
/* kit #9 fidelity, second review (vita/notes/kit9-fidelity.md 13.2): the switch acts on what the folder holds, not on the env line alone.
 * (a) A DLL an AOT image binds is never taken from the folder (Mono, aot-only, would stop at that image: "doesn't match assembly" / "a
 *     dependency ... is out of date") -- the engine image's card assemblies (shepherd_aot_bound_names: the UnityEngine.* modules it uses,
 *     UnityEngine.UI, FMODUnity, ...) always, the owner modules' (shepherd_owner_aot_bound_names) while they are registered unless
 *     SHEPHERD_GAME_AOT=1; a WARN line names each one, and managed/'s copy loads instead.
 * (b) A folder that is missing gives a WARN line and the switch stays off; in the game-AOT apps the owner modules are left unregistered
 *     only when the folder holds one of THEIR assemblies (H7). The end line counts what the folder supplied. */
static int g_k9_fpc_owner_bound;          /* SHP_GAME_AOT: the owner modules are registered and bind their assemblies (H7) */
static unsigned g_k9_fpc_taken, g_k9_fpc_refused;
static int k9_name_in (const char *const *list, const char *name)
{
	for (; list && *list; list++)
		if (!strcmp (*list, name))
			return 1;
	return 0;
}
static int k9_fpc_has (const char *name)   /* <folder>/<name>.dll exists */
{
	char p [300];
	snprintf (p, sizeof (p), "%s/%s.dll", g_k9_fpc_dir, name);
	return file_size (p, NULL) == 0;
}
static const char *k9_fpc_refuse (const char *name)   /* why <name> must not come from the folder (NULL: it may) */
{
	if (k9_name_in (shepherd_aot_bound_names, name))
		return "the eboot's own AOT images are bound to managed/'s copy";
#ifdef SHP_GAME_AOT
	if (g_k9_fpc_owner_bound && k9_name_in (shepherd_owner_aot_bound_names, name))
		return "the registered owner game-AOT modules are bound to managed/'s copy (SHEPHERD_GAME_AOT=1: an owner AOT built against the rewritten set)";
#endif
	return NULL;
}
static void k9_fpc_warn_bound (const char *const *list)   /* one WARN line per bound DLL the folder holds */
{
	for (; list && *list; list++) {
		const char *why = k9_fpc_has (*list) ? k9_fpc_refuse (*list) : NULL;
		if (why)
			LOG ("WARN K9 floatpc: %s/%s.dll is NOT used (%s; Mono would stop at it): " GAME_MANAGED "/%s.dll loads instead -- only the game's own "
			     "DLLs belong in the folder", g_k9_fpc_dir, *list, why, *list);
	}
}
static void k9_floatpc_end_line (void)
{
	if (g_k9_fpc_dir [0])
		LOG ("%sK9 floatpc: %u assemblies loaded from %s, %u bound one(s) refused%s", g_k9_fpc_taken ? "" : "WARN ", g_k9_fpc_taken, g_k9_fpc_dir,
		     g_k9_fpc_refused, g_k9_fpc_taken ? "" : " -- the switch had no effect (the folder supplied nothing the game loaded)");
}
static void k9_floatpc_config (void)
{
	const char *v = getenv ("SHEPHERD_K9_FLOATPC"), *so = getenv ("SHEPHERD_STRICT_ORIGINAL"), *ks = getenv ("SHEPHERD_K10_STRICT");
	/* kit #10 fidelity: the strict original switch asks for the PC's float evaluation too (Runtime/StrictOriginal.cs); no folder = WARN, off.
	 * Review fix: only where the env file has no SHEPHERD_K9_FLOATPC line -- an explicit one (0 included: e.g. strict on 00095 / 00194 with the
	 * game-AOT modules kept registered) wins -- and not under SHEPHERD_K10_STRICT=0 (kit #9's strict list) */
	if (so && !strcmp (so, "1") && !(ks && !strcmp (ks, "0")) && (!v || !*v)) {
		v = "1";
		LOG ("K9 floatpc: SHEPHERD_STRICT_ORIGINAL=1 asks for the PC's float evaluation (SHEPHERD_K9_FLOATPC=1; a SHEPHERD_K9_FLOATPC line in the env "
		     "file wins, SHEPHERD_K10_STRICT=0 = kit #9's strict list)");
	}
	if (!v || !*v || !strcmp (v, "0"))
		return;
	const char *name = !strcmp (v, "1") ? "managed-floatpc" : v;
	size_t n = strlen (name);
	int ok = n > 0 && n <= 40 && name [0] != '.';
	for (size_t i = 0; ok && i < n; i++) {
		char c = name [i];
		ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_' || c == '-' || c == '.';
	}
	if (!ok) {
		LOG ("WARN K9 floatpc: SHEPHERD_K9_FLOATPC=%s is not 1, 0 or a plain folder name -- off (" GAME_MANAGED " only, kit #8)", v);
		return;
	}
	snprintf (g_k9_fpc_dir, sizeof (g_k9_fpc_dir), DIR_ "/%s", name);
	SceIoStat st;
	if (sceIoGetstat (g_k9_fpc_dir, &st) < 0 || !SCE_S_ISDIR (st.st_mode)) {   /* kit #9 second review (b) */
		LOG ("WARN K9 floatpc: SHEPHERD_K9_FLOATPC=%s but %s is not a folder -- off (" GAME_MANAGED " only, kit #8)", v, g_k9_fpc_dir);
		g_k9_fpc_dir [0] = 0;
		return;
	}
	LOG ("K9 floatpc: the game's assemblies from %s first, then " GAME_MANAGED " (SHEPHERD_K9_FLOATPC unset / 0: " GAME_MANAGED " only, kit #8)", g_k9_fpc_dir);
	k9_fpc_warn_bound (shepherd_aot_bound_names);   /* kit #9 second review (a) */
}

/* device kit #10 (track boot): the game's DLLs in one bundle, read in one stream (vita/notes/kit10-boot.md; SHEPHERD_K10_ILBUNDLE=0: kit #9) */
int shepherd_vita_worker_mask (void);
#include "k10_ilb.inc.c"
static MonoAssembly *preload_hook (MonoAssemblyName *aname, char **assemblies_path, void *user_data)
{
	const char *name = mono_assembly_name_get_name (aname);
	const char *culture = mono_assembly_name_get_culture (aname);
	if (!name || (culture && *culture))
		return NULL;   /* no satellite assemblies (invariant globalization) */
	char path [300], rooted [320];
	unsigned size = 0;
	void *ilb_data = NULL;   /* kit #10: the DLL from the bundle (k10_ilb.inc.c) -- no file call at all */
	int ilb_ilz = 0, ilz_done = 0;
	if (k10_ilb_take (name, &ilb_data, &size, path, sizeof (path), &ilb_ilz)) {
		/* kit #10 integration (boot x aot): the bundle serves a DLL only in the form the loader reads without it THIS start -- the compressed-IL
		 * choice (k10a_ilz_path: SHEPHERD_K10_ILZ, the ILZ file present and verified against its original) is made here, once, with the
		 * original's own size; another form = the entry given back and the loose file read, exactly as without the bundle. (Before: a bundled
		 * original was taken and k10a_ilz_path then switched path and size to the ILZ file -- an image opened with the wrong length.) */
		char zp [300];
		unsigned osize = size, zs;
		snprintf (zp, sizeof (zp), "%s", path);   /* managed/<name>.dll */
		snprintf (rooted, sizeof (rooted), "/%s", path);   /* Assembly.Location: the original's path in every case */
		if (ilb_ilz && file_size (path, &osize) != 0)
			osize = 0;   /* (no original: the ILZ form cannot be checked -- given back below) */
		zs = osize;
		if (osize)
			k10a_ilz_path (name, zp, sizeof (zp), &zs);
		int is_ilz = strcmp (zp, path) != 0;
		if (osize && is_ilz == ilb_ilz && (!ilb_ilz || zs == size)) {
			ilz_done = 1;   /* served: path = the form's file (for messages), size = the entry's */
			if (is_ilz) { snprintf (path, sizeof (path), "%s", zp); k10_ilb_ilz_taken (); }
		} else {
			k10_ilb_release (ilb_data, size);
			ilb_data = NULL;
			k10_ilb_other_form ();
			if (is_ilz) { snprintf (path, sizeof (path), "%s", zp); size = zs; ilz_done = 1; }   /* the ILZ file, verified above */
			else if (osize) { size = osize; ilz_done = 1; }   /* the original (path unchanged; k10a_ilz_path did not take it) */
			/* else: neither -- the full lookup below, as without the bundle */
		}
	}
	if (!ilb_data && !ilz_done) {
	snprintf (path, sizeof (path), VPK_MANAGED "/%s.dll", name);
	if (file_size (path, &size) != 0) {
		if (is_bcl (name))
			return NULL;
		int alt = 0;
		if (g_k9_fpc_dir [0]) {   /* kit #9 review: SHEPHERD_K9_FLOATPC (above) */
			snprintf (path, sizeof (path), "%s/%s.dll", g_k9_fpc_dir, name);
			alt = file_size (path, &size) == 0;
			if (alt && k9_fpc_refuse (name)) {   /* kit #9 second review (a): a bound DLL is never taken (its WARN line came at start) */
				alt = 0;
				__atomic_add_fetch (&g_k9_fpc_refused, 1, __ATOMIC_RELAXED);
			}
			if (alt) {
				LOG ("K9 floatpc: %s", path);
				__atomic_add_fetch (&g_k9_fpc_taken, 1, __ATOMIC_RELAXED);
			}
		}
		if (!alt) {
			snprintf (path, sizeof (path), GAME_MANAGED "/%s.dll", name);
			if (file_size (path, &size) != 0)
				return NULL;   /* not ours: the Resolving event / FileNotFoundException decide, as on the Mac */
		}
	}
	}   /* (kit #10: not from the bundle) */
	if (!ilz_done) {   /* (kit #10 integration: a bundle entry's path / size / Location were settled above) */
	snprintf (rooted, sizeof (rooted), "/%s", path);   /* Assembly.Location, as managed code would spell it */
	k10a_ilz_path (name, path, sizeof (path), &size);   /* device kit #10 aot: read the ILZ form from managed-ilz/ when there is one (Location stays managed/; SHEPHERD_K10_ILZ=0: never) */
	k12_vpk_ilz_path (name, path, sizeof (path), &size);   /* kit #12 walkb: a VPK DLL from app0:managed-ilz/ when the VPK has one and SHEPHERD_K12_VPKILZ=1 (each pair CRC-checked; Location stays app0:managed/) */
	}

	{
		SAFE_BEGIN ();
#ifdef SHP_DIAG
		g_dg_preload_waiting = 1;
#endif
		pthread_mutex_lock (&load_lock);   /* one load at a time: two threads loading the same DLL would leak a buffer */
#ifdef SHP_DIAG
		g_dg_preload_waiting = 0;
#endif
		SAFE_END ();
	}
	MonoAssembly *already = mono_assembly_loaded (aname);
	if (already) {
		pthread_mutex_unlock (&load_lock);
		if (ilb_data) k10_ilb_release (ilb_data, size);   /* kit #10 */
		return already;
	}
	void *data = NULL;
	int r;
	if (ilb_data) {   /* kit #10: read (and checked) by the bundle's thread */
		data = ilb_data;
		r = 0;
	} else {
		SAFE_BEGIN ();
		r = read_file_ex (path, &data, &size, 1);
		SAFE_END ();
		if (r == 0) k10_ilb_verify (name, data, size, !strncmp (path, GAME_MANAGED "-ilz/", sizeof (GAME_MANAGED "-ilz/") - 1));   /* kit #10: VERIFY mode only (integration: + the form read) */
	}
	if (r != 0) {
		pthread_mutex_unlock (&load_lock);
		LOG ("FAIL cannot read %s (%d)%s", path, r, r == -2 ? ": newlib heap full" : "");
		return NULL;
	}
	MonoImageOpenStatus st = MONO_IMAGE_OK;
	MonoImage *img = mono_image_open_from_data_with_name ((char *) data, size, 0 /* keep our buffer */, &st, 0, rooted);
	MonoAssembly *a = img ? mono_assembly_load_from_full (img, rooted, &st, 0) : NULL;
	if (img)
		mono_image_close (img);   /* the assembly holds its own reference */
	if (!a) {
		/* the image (if any) is closed and never referenced the buffer as its own. kit #5 review: an IL pool buffer is rewound while
		 * load_lock is still held -- after the unlock another thread's load could take the pool space just behind it */
		if (in_il_pool (data)) {
			if ((char *) data + ((size + 16u) & ~15u) == g_il_pool + __atomic_load_n (&g_il_pool_used, __ATOMIC_RELAXED)) {
				il_pool_rewind ((char *) data, size);   /* the last pool allocation: rewound (kit #10: lock-free) */
				__atomic_sub_fetch (&g_il_pool_files, 1, __ATOMIC_RELAXED);
			}
		} else
			free (data);
		pthread_mutex_unlock (&load_lock);
		LOG ("FAIL cannot load %s (image status %d)", path, (int) st);
		return NULL;
	}
	g_il_bytes += size;
	g_il_files++;
	pthread_mutex_unlock (&load_lock);
	return a;
}
#ifdef SHP_DIAG
#undef preload_hook
#endif

/* ---------------------------------------------------------------- the engine's checkpoints (Diag/VitaHost.cs) */

static SceUInt64 g_cp_t, g_cp_frame_t;
static int g_cp_frame;
static volatile int g_last_frame = -1;   /* the frame of the engine's last checkpoint / P line (any kind) */
static char g_end_note [700];
static int g_end_frame = -1;

static void k11_cheap_line (const char *tag);   /* kit #11 walk (k11_walk.inc.c): the C heap's census after the mono: line */
static void mono_counters_line (const char *tag)
{
	MonoVitaRuntimeStats rs;
	mono_vita_get_runtime_stats (&rs);
	LOG ("%s mono: mempools %u KiB | interp %d methods (%lld transforms, %d inlined, %d instrs) | aot %d methods | %d methods, %d classes init (%d used), %d generic classes, %d inflated types, %d inflated methods | vtables %u KiB, statics %u KiB, imt %u KiB | %d delegates",
	     tag, (unsigned) (rs.mempool_bytes / 1024), rs.interp_methods, (long long) rs.interp_transforms, rs.interp_inlined, rs.interp_emitted_instructions,
	     rs.aot_methods, rs.methods, rs.initialized_classes, rs.used_classes, rs.generic_classes, rs.inflated_types, rs.inflated_methods,
	     (unsigned) rs.vtable_bytes / 1024, (unsigned) rs.static_data_bytes / 1024, (unsigned) rs.imt_bytes / 1024, rs.delegates_created);
	k11_cheap_line (tag);   /* kit #11 walk: the C heap's census (SHEPHERD_K11_CHEAP=0: none) */
	/* V6 stage 2: managed Thread.Yield's back-off (mono-vita-yield.c): the game's Dispatch Thread polls with it */
	uint64_t yc = 0, yd = 0, yb = 0, yn = 0;
	mono_vita_get_yield_stats (&yc, &yd, &yb, &yn);
	LOG ("%s yield: %llu managed Thread.Yield calls, %.1f s slept in them, %llu backed off (> 100 us), %llu without a state slot",
	     tag, (unsigned long long) yc, yd / 1e6, (unsigned long long) yb, (unsigned long long) yn);
}

#ifdef SHEPHERD_GL
/* vitaGL's pools (free/total KiB) + frames handed to the display. On the GL thread only (the engine's, or main after it ended). */
static void gl_pools_line (const char *tag)
{
	char d [256];
	int o = 0;
	for (int i = 0; i < 5 && o < (int) sizeof (d); ++i)
		o += snprintf (d + o, sizeof (d) - o, "%s%s %u/%u", i ? ", " : "", shepgl_pool_name (i), shepgl_pool_kib (i, 0), shepgl_pool_kib (i, 1));
	LOG ("%s vgl pools free/total KiB: %s | frames shown %u", tag, d, shepgl_swap_count ());
}
#endif

/* newlib (kit #3 review): sbrk-only (no mmap, nothing returned from the middle), so the sbrk'd size is what runs out; free chunks
 * inside it are reused only by requests they fit, and a malloc that none fits once the sbrk'd size is at the cap is a Mono fatal error.
 * Checked on EVERY heartbeat and checkpoint (before: checkpoints only -- frames 0/500/1050/1300 and the end -- so an endless play
 * session had no warning): WARN when the sbrk'd size is within 6 MiB of the cap or the bytes in use within 6 MiB, again only after
 * each further MiB; the play apps (SHP_PLAY_APP) end the session cleanly (FAIL MEM + the quit request) when the sbrk'd size is within
 * 4 MiB of the cap AND less than 8 MiB of the cap is unused. The P line's newlib slope (KiB per 1000 frames over the last ~1000
 * frames) shows a growth long before (device kit #2: the interpreted GL apps grew ~1.2-1.6 KiB per frame; the AOT apps are flat). */
static unsigned g_nl_warned_k;
static struct { int frame; unsigned used_k; } g_nl_ring [24];
static unsigned g_nl_n;
static void newlib_watch (const MemSnap *m, int frame, char *slope, size_t slope_n)
{
	unsigned cap_k = k10_newlib_cap_k ();   /* kit #10: = SHP_NEWLIB_HEAP_MB * 1024 (kit #9); in the shared pool what newlib may grow to now (its floor) */
	if (slope) {
		slope [0] = 0;
		unsigned i = g_nl_n % 24;
		g_nl_ring [i].frame = frame;
		g_nl_ring [i].used_k = m->newlib_used_k;
		g_nl_n++;
		/* the oldest entry at most ~1000 frames back (heartbeats every 50 frames: 21 entries) */
		unsigned have = g_nl_n < 24 ? g_nl_n : 24, j = (g_nl_n - have) % 24;
		for (unsigned k = 0; k < have; ++k, j = (j + 1) % 24)
			if (frame - g_nl_ring [j].frame <= 1000)
				break;
		int span = frame - g_nl_ring [j].frame;
		if (span >= 200)
			snprintf (slope, slope_n, " (%+.0f KiB/1000f)", ((double) m->newlib_used_k - (double) g_nl_ring [j].used_k) * 1000.0 / span);
	}
	int near = m->newlib_sbrk_k + 6144 > cap_k || m->newlib_used_k + 6144 > cap_k;
	unsigned lvl = m->newlib_sbrk_k > m->newlib_used_k ? m->newlib_sbrk_k : m->newlib_used_k;
	if (near && (!g_nl_warned_k || lvl >= g_nl_warned_k + 1024)) {
		g_nl_warned_k = lvl;
		LOG ("WARN newlib heap nearly full at f=%d: sbrk'd %u of %u KiB, %u in use (peak %u): once the sbrk'd size reaches the cap, a malloc "
		     "that no free chunk inside it fits is a fatal error (this line repeats after each further MiB)", frame, m->newlib_sbrk_k, cap_k,
		     m->newlib_used_k, m->newlib_peak_k);
	}
#ifdef SHP_PLAY_APP
	if (g_mem_guard && !g_host_quit && m->newlib_sbrk_k + 4096 > cap_k && m->newlib_used_k + 8192 > cap_k) {
		g_host_quit = 1;
		LOG ("FAIL MEM memory guard: the newlib heap is almost out (sbrk'd %u of %u KiB, %u in use) -- the app ends this session after the "
		     "current frame (red END, every log kept) instead of a fatal error. Start it again", m->newlib_sbrk_k, cap_k, m->newlib_used_k);
	}
#endif
}

/* device kit #5 (lever 20, vita/notes/kit5-runtime.md): the runtime's AOT -> interpreter call-info cache
 * (vita/patches/k5-interp-cicache.patch). Weak: a runtime without the patch leaves these at address 0 (and the host says so).
 * SHEPHERD_INTERP_CICACHE=count adds the entry counters to every heartbeat ("| ie ..."; statistics only). */
#if !defined (SHEPHERD_MAC_CHECK)
extern int mono_shepherd_interp_cicache_mode __attribute__ ((weak));
extern unsigned mono_shepherd_interp_entries __attribute__ ((weak));
extern unsigned mono_shepherd_interp_entries_dynamic __attribute__ ((weak));
extern unsigned mono_shepherd_interp_entry_methods __attribute__ ((weak));
extern unsigned mono_shepherd_interp_entry_infos __attribute__ ((weak));
extern unsigned mono_shepherd_interp_entry_info_bytes __attribute__ ((weak));
extern int mono_shepherd_interp_entry_dump (const char *path) __attribute__ ((weak));
static const char *logs_dir (void);

static void log_cicache (void)
{
	const char *e = getenv ("SHEPHERD_INTERP_CICACHE");
	if (!&mono_shepherd_interp_cicache_mode)
		LOG ("H8 runtime: no AOT->interpreter call-info cache in this runtime (each entry computes its calling convention; SHEPHERD_INTERP_CICACHE=%s ignored)", e ? e : "unset");
	else if (mono_shepherd_interp_cicache_mode == 3)   /* kit #5 integration: the runtime's fail-safe parse refused the value */
		LOG ("H8 runtime: AOT->interpreter call-info cache OFF: SHEPHERD_INTERP_CICACHE=%s is not a known value (unset / 1 / on = on, 0 / off = off, count = on + counters), so every entry computes its calling convention, as kit #4",
		     e ? e : "unset");
	else
		LOG ("H8 runtime: AOT->interpreter call-info cache %s (SHEPHERD_INTERP_CICACHE=%s; 0 = every entry computes its calling convention, as kit #4)",
		     mono_shepherd_interp_cicache_mode == 0 ? "OFF" : mono_shepherd_interp_cicache_mode == 2 ? "ON + counters" : "ON", e ? e : "unset");
}

static void cicache_note (char *buf, size_t n)
{
	buf [0] = 0;
	if (&mono_shepherd_interp_cicache_mode && mono_shepherd_interp_cicache_mode == 2 && &mono_shepherd_interp_entries)
		snprintf (buf, n, " | ie %u entries (%u dynamic), %u methods, %u call infos %u B", mono_shepherd_interp_entries,
			  &mono_shepherd_interp_entries_dynamic ? mono_shepherd_interp_entries_dynamic : 0u, mono_shepherd_interp_entry_methods,
			  mono_shepherd_interp_entry_infos, mono_shepherd_interp_entry_info_bytes);
}

/* =count, at the end: every method the AOT code entered, with its entry count (<logs>/interp-entries.tsv; diagnostics) */
static void cicache_dump (const char *tag)
{
	char path [160];
	if (!&mono_shepherd_interp_entry_dump)
		return;
	snprintf (path, sizeof (path), "%s/interp-entries.tsv", logs_dir ());
	int k = mono_shepherd_interp_entry_dump (path);
	if (k >= 0)
		LOG ("%s interp entries: %d methods entered from AOT code, counts in %s", tag, k, path);
}
#else
static void log_cicache (void) { }   /* the Mac's stock Mono: no Vita runtime patch */
static void cicache_note (char *buf, size_t n) { buf [0] = 0; }
static void cicache_dump (const char *tag) { }
#endif
#include "k9_runtime.inc.c"   /* device kit #9, track runtime: R1 fast TLS (the H8 line, the check mode's END line) */
#include "k10_aot.inc.c"       /* device kit #10, track aot: the learning record (aot-learn.tsv), compressed IL (managed-ilz/) */

/* kind: 0 heartbeat, 1 checkpoint, 2 end, 3 (GL app) the menu responded to a pad press, 4 (GL app) the engine's render thread did not
 * stop at the end (Render/MtRender.cs: it may still be inside vitaGL, so the host makes no GL call after Main returns), 5 (kit #6
 * gpulight review, Diag/VitaHost.cs Fail) the engine's own FAIL line: "FAIL <note>", counted like the host's (FAIL END, red screen) --
 * e.g. "FAIL derived: ..." when the card's Shepherd.Engine.Derived.dll is one the engine does not load (Render/DerivedRender.cs).
 * Reached through a P/Invoke (Diag/VitaHost.cs), so under coop this thread is GC-SAFE (STATE_BLOCKING) for the whole call.
 * Nothing here may take a runtime coop lock: mono_coop_mutex_lock enters GC-safe again when its trylock fails, and
 * "Blocking is not nestable" (mono-threads-state-machine.c) is a fatal error -- e.g. mono_gc_get_used_size -> LOCK_GC
 * whenever a collection or a LOS/TLAB slow path holds the GC lock. Allowed: plain pthread locks (the log, the arena's stats),
 * lock-free reads (mono_gc_get_heap_size, mono_vita_get_runtime_stats, mono_vita_heap_limit_count), static data. The GC heap
 * in use comes from the managed side (GC.GetTotalMemory(false), taken in GC-unsafe state). */
#ifdef SHEPHERD_JIT
extern void mono_shepjit_frame (int frame) __attribute__ ((weak));   /* kit #9 track jit (k8_jit_host.inc.c): the tier's heartbeat */
#endif
static void k10_fp_watch (int frame);   /* kit #10 fidelity (k10_fp.inc.c): the float rules re-checked at every heartbeat (SHEPHERD_K10_FPSCR) */
void shepherd_vita_engine_checkpoint (int frame, int kind, const char *note, int64_t gc_heap, int64_t alloc_main,
				      int minors, int majors, int live_objects)
{
	sceKernelPowerTick (SCE_KERNEL_POWER_TICK_DEFAULT);
	if (kind == 5) {   /* (not a frame report: g_last_frame and the checkpoint clock stay as they are) */
		LOG ("FAIL %s", note ? note : "(the engine gave no reason)");
		return;
	}
	if (kind == 6) {   /* kit #7 (Diag/K7.cs): one engine line for the host log, as it is -- the BOOT numbers (never a frame report) */
		LOG ("%s", note ? note : "");
		return;
	}
	if (kind == 8) {   /* kit #9 gc (Runtime/K9Gc.cs): a scene load starts -- the per-scene nursery sizing (k9_gc.inc.c; never a frame report) */
		k9_gc_scene_note (frame, note, alloc_main);
		return;
	}
	g_last_frame = frame;
	k10_fp_watch (frame);   /* kit #10 fidelity: off = nothing (kit #9) */
#ifdef SHEPHERD_GL
	/* kind 9 (kit #9 device review round 2, finding 2): the title press's answer -- the same lines, not the memory guard. (kit #9 integration:
	 * the device track sent it as kind 8, which the gc track had taken for its scene notes -- the branch above returned first and the title
	 * press was never counted; it is kind 9 now, in Diag/VitaHost.cs too) */
	if (kind == 3 || kind == 9) {
		g_interactive++;
		if (kind == 9)
			g_title_resp++;
		LOG ("M1 f=%d wall=%.1fs the menu responded to the pad: %s", frame, wall_s (), note ? note : "");
		if (g_interactive == 1)
			LOG ("PASS M1 f=%d: the Main Menu is on screen (%u frames shown) and interactive -- the selected button changed after a pad press (%s)",
			     frame, shepgl_swap_count (), note ? note : "");
		return;
	}
	if (kind == 4) {
		g_gl_unsafe = 1;
		LOG ("WARN MT f=%d the render thread did not stop at the end (%s): after Main the host makes no vitaGL call (no END pool line, no green/red screen)",
		     frame, note ? note : "");
		return;
	}
#endif
	SceUInt64 now = sceKernelGetProcessTimeWide ();
	double ms_per_frame = frame > g_cp_frame && g_cp_frame_t ? (double) (now - g_cp_frame_t) / 1000.0 / (frame - g_cp_frame) : 0;
	MemSnap m;
	mem_snap (&m, 0);
	if (kind == 0 || kind == 7) {   /* kit #8 speed (Diag/VitaHost.cs, SHEPHERD_K8_HEAP): kind 7 = a heartbeat whose heap is sgen's at the last collection ("gc heap@gc") */
		char slope [40], ie [128];
#ifdef SHEPHERD_JIT
		if (mono_shepjit_frame)
			mono_shepjit_frame (frame);   /* kit #9: every heartbeat (50 frames): frame-based counter decay, the count window (a plain pthread lock at most) */
#endif
		newlib_watch (&m, frame, slope, sizeof (slope));
		k10_mem_heartbeat (frame, &m);   /* kit #10 memory: the pressure ladder's level, the memory timeline (k10_mem.inc.c) */
		cicache_note (ie, sizeof (ie));
		/* k4-display: the clocks the kernel reports NOW, every heartbeat (a PSVshell profile -- e.g. 500 MHz -- set after our start shows here) */
		LOG ("P f=%d wall=%.1fs %.0f ms/frame | gc heap%s %u KiB, gcs %d/%d | arena %u KiB, newlib %u KiB%s, free user %u KiB | clk arm %d bus %d gpu %d MHz | %s%s",
		     frame, wall_s (), ms_per_frame, kind == 7 ? "@gc" : "", (unsigned) (gc_heap / 1024), minors, majors, (unsigned) (m.arena.used / 1024), m.newlib_used_k, slope, m.free_user_k,
		     scePowerGetArmClockFrequency (), scePowerGetBusClockFrequency (), scePowerGetGpuClockFrequency (), note ? note : "", ie);
	} else {
		char tag [24], pauses [160];
		snprintf (tag, sizeof (tag), kind == 2 ? "CPend" : "CP%d", frame);
		static int sw_cp;
		if (!sw_cp++)   /* kit #5 review: the Mono thread's stack at the first checkpoint (the GL programs and the boot GC behind it) */
			stack_watch_mono_line ("cp0", "the GL programs built and the boot GC's 256 KiB clearing done");
		pause_window (pauses, sizeof (pauses));
		LOG ("%s f=%d wall=%.1fs (%.0f ms/frame since f%d) | %s", tag, frame, wall_s (), ms_per_frame, g_cp_frame, note ? note : "");
		LOG ("%s ram: free main(user) %u KiB, cdram %u KiB, phycont %u KiB | newlib heap used %u KiB, sbrk %u, peak %u of %u KiB | arena used %u KiB, peak %u of %u KiB (%u extents, largest free %u KiB)",
		     tag, m.free_user_k, m.free_cdram_k, m.free_phycont_k, m.newlib_used_k, m.newlib_sbrk_k, m.newlib_peak_k, k10_newlib_cap_k (),
		     (unsigned) (m.arena.used / 1024), (unsigned) (m.arena.peak / 1024), (unsigned) (m.arena.arena_size / 1024), m.arena.free_extents, (unsigned) (m.arena.largest_free / 1024));
		LOG ("%s gc: heap in use %u KiB (GC.GetTotalMemory; max-heap-size %u MiB) | mono_gc heap %u KiB | %d minor + %d major GCs; pauses: minor %u (%.1f ms total, max %.1f), major %u (%.1f ms total, max %.1f); window: %s | main-thread allocation %u KiB total | %d live engine objects",
		     tag, (unsigned) (gc_heap / 1024), g_max_heap_mb, (unsigned) (mono_gc_get_heap_size () / 1024), minors, majors,
		     gcs.count [0], gcs.total_us [0] / 1000.0, gcs.max_us [0] / 1000.0, gcs.count [1], gcs.total_us [1] / 1000.0, gcs.max_us [1] / 1000.0,
		     pauses, (unsigned) (alloc_main / 1024), live_objects);
		LOG ("%s il: %u assemblies read into RAM, %llu KiB%s | max-heap-size refusals so far: %u | OutOfMemoryException: %d", tag, g_il_files, g_il_bytes / 1024,
		     il_pool_note (), mono_vita_heap_limit_count (), g_oom_count);
		mono_counters_line (tag);
#ifdef SHEPHERD_GL
		gl_pools_line (tag);
#endif
		newlib_watch (&m, frame, NULL, 0);   /* (the heartbeats do it too; see newlib_watch) */
		g_cp_frame = frame;
		g_cp_frame_t = now;
		if (kind == 2) {
			k9_gc_scene_note (frame, "end", alloc_main);   /* kit #9 gc: the last scene's window (per-scene nursery sizing) */
			g_end_frame = frame;
			snprintf (g_end_note, sizeof (g_end_note), "%s", note ? note : "");
			cicache_dump (tag);
			k9_fast_tls_end (tag);
			k10a_end (tag);   /* device kit #10 aot: the learning record written + its END line, the compressed-IL END line */
		}
	}
	if (!g_cp_frame_t) {
		g_cp_frame = frame;
		g_cp_frame_t = now;
	}
	g_cp_t = now;
}

/* ---------------------------------------------------------------- core pinning (kit 3; vita/notes/engine-pass-speed.md section 14)
 * On when SHEPHERD_VITA_PIN=1 (0 = off); unset = on exactly when SHEPHERD_MT_RENDER=1 (the multicore pipeline; the MT variant's
 * default). Then the engine (Diag/VitaHost.cs) pins the main thread to USER_0 at boot and the pipeline's render thread to USER_1 when
 * it starts, each through shepherd_vita_pin, and gl_start pins vitaGL's garbage-collector thread to USER_2 (before vitaGL's init).
 * Off: no thread is touched (every thread keeps the kernel's default affinity, as in every build before kit 3). */
static int pin_requested (void)
{
	const char *p = getenv ("SHEPHERD_VITA_PIN");
	if (p && *p)
		return !strcmp (p, "1");
	const char *m = getenv ("SHEPHERD_MT_RENDER");
	return m && !strcmp (m, "1");
}
/* The calling thread to `mask` (SCE_KERNEL_CPU_MASK_USER_* or CPU3), read back, one PIN line. Returns 0 when done, the error (< 0)
 * when the kernel refused the mask. k4-display: sceKernelChangeThreadCpuAffinityMask returns the thread's PREVIOUS mask on success
 * (the probes logged rc 0x70000 for a thread that had 0x70000; kit #3's pinned threads had 0, hence rc 0) and an error code < 0 on
 * refusal; before, any rc != 0 was reported as NOT PINNED, which would have been wrong for a thread that already had a mask.
 * k4 review: a LATENCY-TOLERANT WORKER (pinned to CPU3 or USER_2, the masks the 4-core split gives the pack read-ahead and the
 * power/watchdog thread) also runs at least at the main thread's priority: the main thread waits (bounded) for a window the read-ahead
 * is still reading, so a worker preempted by a lower-priority thread on its core would stall it. On USER_2 sceAudioOut's output thread
 * (priority 64) still comes first. The PIN line then shows both priorities.
 * Reached through a P/Invoke (GC-safe): only kernel calls and the log's plain pthread lock. */
#define SHP_CORE3_MASK 0x80000
#define SHP_USER2_MASK 0x40000
static SceUID g_mono_tid;   /* (the Mono main thread; set in mono_thread_body below) */
/* kit #6 track toggles (vita/notes/kit6-toggles.md): the latency-tolerant workers pinned so far (the power / watchdog thread, the pack
 * read-ahead), so Settings > Graphics > 4th CPU Core can move them between CPU3 and USER_2 while the game runs (k6_core3). Bookkeeping
 * only: nothing reads it unless the engine's Settings row asks (shepherd_vita_k6), so with SHEPHERD_K6_TOGGLES=0 the host is kit #5's. */
#define K6_WORKERS 8
static struct { SceUID tid; char who [24]; } g_k6_workers [K6_WORKERS];
static int g_k6_nworkers;
static pthread_mutex_t g_k6_mu = PTHREAD_MUTEX_INITIALIZER;
static void k6_worker_note (SceUID tid, const char *who)
{
	pthread_mutex_lock (&g_k6_mu);
	int i = 0;
	while (i < g_k6_nworkers && g_k6_workers [i].tid != tid) i++;
	if (i < K6_WORKERS) {
		g_k6_workers [i].tid = tid;
		snprintf (g_k6_workers [i].who, sizeof g_k6_workers [i].who, "%s", who ? who : "?");   /* (the engine's name buffer is freed after the call) */
		if (i == g_k6_nworkers) g_k6_nworkers++;
	}
	pthread_mutex_unlock (&g_k6_mu);
}
int shepherd_vita_pin (int mask, const char *who)
{
	SceUID me = sceKernelGetThreadId ();
	int before = sceKernelGetThreadCpuAffinityMask (me);
	int rc = sceKernelChangeThreadCpuAffinityMask (me, mask);
	int after = sceKernelGetThreadCpuAffinityMask (me);
	char pr [96] = "";
	if ((mask == SHP_CORE3_MASK || mask == SHP_USER2_MASK) && g_mono_tid && me != g_mono_tid) {
		SceKernelThreadInfo mi, wi;
		memset (&mi, 0, sizeof mi); mi.size = sizeof mi;
		memset (&wi, 0, sizeof wi); wi.size = sizeof wi;
		if (sceKernelGetThreadInfo (g_mono_tid, &mi) >= 0 && sceKernelGetThreadInfo (me, &wi) >= 0) {
			int prc = 0, now = wi.currentPriority;
			if (wi.currentPriority > mi.currentPriority) {   /* a larger number = a lower priority */
				prc = sceKernelChangeThreadPriority (me, mi.currentPriority);
				if (prc >= 0) now = mi.currentPriority;
			}
			snprintf (pr, sizeof pr, "; priority %d -> %d (main thread %d)%s", wi.currentPriority, now, mi.currentPriority,
				  prc < 0 ? " (change refused)" : "");
		}
	}
	LOG ("PIN %s (SceUID 0x%x): cpu affinity 0x%x -> 0x%x: rc 0x%08x, read back 0x%x%s%s", who ? who : "?", (unsigned) me, (unsigned) before,
	     (unsigned) mask, (unsigned) rc, (unsigned) after, rc < 0 ? " (NOT PINNED: the kernel refused the mask; the thread keeps its affinity)" : after != mask ? " (the read-back differs from the mask)" : "", pr);
	if (rc >= 0 && (mask == SHP_CORE3_MASK || mask == SHP_USER2_MASK))
		k6_worker_note (me, who);   /* kit #6 toggles: a worker the 4th CPU Core row may move later */
	return rc < 0 ? rc : 0;
}

/* ---------------------------------------------------------------- the 4-core split (k4-display)
 * USER_0 main (the game's scripts) | USER_1 the render thread (MT pipeline) | USER_2 shepaudio's output + IO threads and vitaGL's garbage
 * collector | CPU3 (mask 0x80000, SCE_KERNEL_CPU_MASK_SYSTEM: the system-reserved 4th core) the LATENCY-TOLERANT workers: the pack
 * read-ahead (Shepherd.Assets PackReader) and this host's power / watchdog thread. CPU3 is open to an app only with a core-unlock kernel
 * plugin (GrapheneCt's CapUnlocker; the owner's, never installed by us): at boot (with pinning on) a throwaway thread asks for the mask,
 * reads it back and must then run there (a delay = a reschedule onto the new core) within 2 s. Refused (no plugin) -> the workers run on
 * USER_2, silently (one C3 line says which). SHEPHERD_CORE3=0 (env file) skips the probe: USER_2, as without the plugin.
 * vlx refuses 0x80000 like a Vita without the plugin (VLX_CORE3=1 accepts it), so both paths run there. */
static int g_core3 = -1;           /* -1 not probed, 1 = the workers run on CPU3, 0 = USER_2 */
static int g_core3_use = 1;        /* kit #6 toggles: Settings > Graphics > 4th CPU Core (1 = On, the default = kit #5; 0 = the workers on USER_2) */
static volatile int g_core3_rc, g_core3_back, g_core3_ran;
static void *core3_probe_thread (void *arg)
{
	(void) arg;
	SceUID me = sceKernelGetThreadId ();
	g_core3_rc = sceKernelChangeThreadCpuAffinityMask (me, SHP_CORE3_MASK);
	g_core3_back = sceKernelGetThreadCpuAffinityMask (me);
	sceKernelDelayThread (1000);   /* off the CPU and back: with the new mask it can only come back on CPU3 */
	g_core3_ran = 1;
	return NULL;
}
static void core3_probe (void)
{
	const char *e = getenv ("SHEPHERD_CORE3");
	if (e && !strcmp (e, "0")) {
		g_core3 = 0;
		LOG ("C3 4th core (CPU3): not probed (SHEPHERD_CORE3=0): the latency-tolerant workers run on USER_2");
		return;
	}
	pthread_t th;
	pthread_attr_t a;
	pthread_attr_init (&a);
	pthread_attr_setstacksize (&a, 16 * 1024);
	pthread_attr_setdetachstate (&a, PTHREAD_CREATE_DETACHED);
	int cr = pthread_create (&th, &a, core3_probe_thread, NULL);
	pthread_attr_destroy (&a);
	int waited = 0;
	while (cr == 0 && !g_core3_ran && waited < 2000) {
		sceKernelDelayThread (1000);
		waited++;
	}
	g_core3 = cr == 0 && g_core3_rc >= 0 && g_core3_back == SHP_CORE3_MASK && g_core3_ran;
	LOG ("C3 4th core (CPU3, mask 0x%x): probe rc 0x%08x, read back 0x%x, %s -> %s", SHP_CORE3_MASK, (unsigned) g_core3_rc, (unsigned) g_core3_back,
	     cr ? "probe thread not created" : g_core3_rc < 0 ? "the kernel refused the mask" : g_core3_ran ? "the probe thread ran on it" : "the probe thread did not run again within 2 s",
	     g_core3 ? "AVAILABLE (a core-unlock plugin is active): the latency-tolerant workers (pack read-ahead, power/watchdog) run on CPU3"
		     : "not available (no core-unlock plugin): the latency-tolerant workers run on USER_2");
}
/* k4 review: which kernel/user plugins the owner's taiHEN loads (every non-comment line of ur0:tai/config.txt and ux0:tai/config.txt,
 * with its section), so the log names the core-unlock plugin next to the C3 line (kit #2's probe printed only kubridge / PSVshell lines).
 * Read-only, once at boot; a missing file is one line. */
static void tai_plugins_note (void)
{
	static const char *const cfgs [] = { "ur0:tai/config.txt", "ux0:tai/config.txt" };
	static char buf [16 * 1024];
	for (unsigned c = 0; c < sizeof cfgs / sizeof cfgs [0]; c++) {
		SceUID fd = sceIoOpen (cfgs [c], SCE_O_RDONLY, 0);
		if (fd < 0) { LOG ("TAI %s: not readable (0x%08x)", cfgs [c], (unsigned) fd); continue; }
		int n = sceIoRead (fd, buf, sizeof buf - 1);
		sceIoClose (fd);
		if (n < 0) n = 0;
		buf [n] = 0;
		char sect [48] = "?";
		int plugins = 0;
		for (char *line = buf; line && *line; ) {
			char *nl = strchr (line, '\n');
			if (nl) *nl = 0;
			size_t len = strlen (line);
			while (len && (line [len - 1] == '\r' || line [len - 1] == ' ' || line [len - 1] == '\t')) line [--len] = 0;
			while (*line == ' ' || *line == '\t') line++;
			if (*line == '*') snprintf (sect, sizeof sect, "%s", line);
			else if (*line && *line != '#') { LOG ("TAI %s [%s] %s", cfgs [c], sect, line); plugins++; }
			line = nl ? nl + 1 : NULL;
		}
		LOG ("TAI %s: %d bytes, %d plugin line(s)%s", cfgs [c], n, plugins, n == (int) sizeof buf - 1 ? " (first 16 KiB only)" : "");
	}
}
/* The mask for a latency-tolerant worker: CPU3 when the probe passed, else USER_2. Reached through a P/Invoke (GC-safe). */
int shepherd_vita_worker_mask (void)
{
	return g_core3 > 0 && g_core3_use ? SHP_CORE3_MASK : SHP_USER2_MASK;
}
/* kit #6 toggles: Settings > Graphics > 4th CPU Core On / Off (shepherd_vita_k6 op 1). Off = every latency-tolerant worker on USER_2, exactly
 * the fallback of a Vita without the core-unlock plugin; On = CPU3 again. Workers pinned before the change are moved now (a PIN line each,
 * rc + read-back), later ones get the new mask from shepherd_vita_worker_mask. Only when the boot probe found CPU3 (C3 line); otherwise
 * nothing changes (the row shows the core as not available and cannot turn it on). */
static void (*g_k6_core3_hook) (int use);   /* kit #9 review fix: the physics workers follow the row too (phys_host.inc.c sets it when they run) */
static void k6_core3 (int use)
{
	if (g_core3 <= 0) {
		LOG ("C3 k6 Settings > Graphics > 4th CPU Core %s asked: nothing changed (%s)", use ? "On" : "Off",
		     g_core3 < 0 ? "core pinning is off in this app: no thread is pinned, the 4th core is never used" : "CPU3 is not open to this app: see the C3 line (no core-unlock plugin, or SHEPHERD_CORE3=0)");
		return;
	}
	int was = g_core3_use;
	g_core3_use = use ? 1 : 0;
	int mask = shepherd_vita_worker_mask ();
	pthread_mutex_lock (&g_k6_mu);
	LOG ("C3 k6 Settings > Graphics > 4th CPU Core %s (was %s): the latency-tolerant workers -> %s (mask 0x%x); %d worker thread(s) pinned so far, moved now",
	     use ? "On" : "Off", was ? "On" : "Off", use ? "CPU3" : "USER_2 (as without the core-unlock plugin)", (unsigned) mask, g_k6_nworkers);
	for (int i = 0; i < g_k6_nworkers; i++) {
		SceUID t = g_k6_workers [i].tid;
		int before = sceKernelGetThreadCpuAffinityMask (t);
		int rc = sceKernelChangeThreadCpuAffinityMask (t, mask);
		int after = sceKernelGetThreadCpuAffinityMask (t);
		LOG ("PIN %s (SceUID 0x%x): cpu affinity 0x%x -> 0x%x: rc 0x%08x, read back 0x%x (k6: 4th CPU Core %s)%s", g_k6_workers [i].who, (unsigned) t,
		     (unsigned) before, (unsigned) mask, (unsigned) rc, (unsigned) after, use ? "On" : "Off",
		     rc < 0 ? " (NOT MOVED: the kernel refused; the thread may have ended)" : after != mask ? " (the read-back differs from the mask)" : "");
	}
	pthread_mutex_unlock (&g_k6_mu);
	if (g_k6_core3_hook)
		g_k6_core3_hook (g_core3_use);   /* kit #9 review fix: the physics worker on CPU3 (never in the list above: it is restarted, not moved) */
}
/* A PIN line for a native thread created with its affinity (shepaudio's output and IO threads: vita/audio sa_plat_vita.c calls this,
 * a weak no-op in shepaudio's own builds): the mask asked for at creation and the kernel's read-back. */
void shepherd_vita_thread_note (int thid, const char *name, int mask)
{
	int back = sceKernelGetThreadCpuAffinityMask ((SceUID) thid);
	LOG ("PIN %s (SceUID 0x%x): created with cpu affinity 0x%x, read back 0x%x%s", name ? name : "?", (unsigned) thid, (unsigned) mask, (unsigned) back,
	     back < 0 ? " (read-back failed)" : mask && back != mask ? " (the read-back differs from the mask)" : "");
}

#ifdef SHEPHERD_GL
/* ---------------------------------------------------------------- the Vita's controls (Icalls/VitaPad.cs, once per frame) */

static SceUInt64 g_quit_hold_t;
/* SHORT PRESSES (kit #3 review): the engine reads the pad once per frame, and at the Vita's frame rates (Credits ~0.5 s a frame) a tap
 * can start and end between two reads. SceCtrl keeps the last 64 samples (one per vblank, ~1 s): the host reads them all and a button
 * that went down and up since the previous read is reported as down for this one read (pad_latch.h) -- the game sees one press frame,
 * as Unity at 60 fps would have. Holds, the stick and START+SELECT use the newest sample. SHEPHERD_PAD_LATCH=0 (env file): the newest
 * sample only, as before. The first reads log a PAD line with what the device returns (sample count, time span, order). */
#include "pad_latch.h"
#define SHP_PAD_SAMPLES 64
static SceCtrlData g_pads [SHP_PAD_SAMPLES];   /* one reader: the engine's main thread */
static pad_latch g_latch;
static int g_latch_on = -1;
static int32_t g_k8_rx, g_k8_ry;   /* kit #8: the newest sample's right stick, for shepherd_vita_k8_pad (k8_play.inc.c) */
static unsigned g_pad_reads;
/* One SceCtrl read (analog mode, set in main): the button bits, the left stick centred at 0 (-128..127, y down). Flags: 2 = a
 * sample was read, 1 = START+SELECT have been held together for SHEPHERD_QUIT_HOLD_MS (default 1500): the engine then ends its
 * boot loop cleanly (END line); 4 = the host asks the engine to end the session (the memory guard, SHP_PLAY_APP: a FAIL MEM line says
 * why), the same clean end. Reached through a P/Invoke (GC-safe): no runtime locks, nothing that blocks. */
int shepherd_vita_pad_read (uint32_t *buttons, int32_t *lx, int32_t *ly)
{
	*buttons = 0;
	*lx = *ly = 0;
	int quit = g_host_quit ? 4 : 0;
	if (g_latch_on < 0) {
		const char *e = getenv ("SHEPHERD_PAD_LATCH");
		g_latch_on = !(e && !strcmp (e, "0"));
	}
	memset (g_pads, 0, sizeof (g_pads));
	int n = sceCtrlPeekBufferPositive (0, g_pads, g_latch_on ? SHP_PAD_SAMPLES : 1);
	if (n < 1)
		return quit;
	if (n > SHP_PAD_SAMPLES)
		n = SHP_PAD_SAMPLES;
	int newest = 0;
	unsigned tap = 0, held = g_latch_on ? pad_latch_step (&g_latch, g_pads, n, &newest, &tap) : g_pads [0].buttons;
	const SceCtrlData *pad = &g_pads [newest];
	g_pad_reads++;
	if (g_latch_on && (g_pad_reads == 2 || g_pad_reads == 500)) {
		/* what this firmware gives (device check: n = 64 and ~1 s of span expected; vlx / the Mac give equal or single samples) */
		uint64_t lo = pad->timeStamp, hi = pad->timeStamp;
		int asc = 1;
		for (int i = 0; i < n; ++i) {
			if (g_pads [i].timeStamp < lo) lo = g_pads [i].timeStamp;
			if (g_pads [i].timeStamp > hi) hi = g_pads [i].timeStamp;
			if (i && g_pads [i].timeStamp < g_pads [i - 1].timeStamp) asc = 0;
		}
		LOG ("PAD read #%u: sceCtrlPeekBufferPositive(64) gave %d sample(s) over %.1f ms (%s, newest #%d); short presses between frames are "
		     "latched (SHEPHERD_PAD_LATCH=0 turns it off); latched so far: %u", g_pad_reads, n, (double) (hi - lo) / 1000.0,
		     asc ? "oldest first" : "not oldest first", newest, g_latch.taps);
	}
	if (tap && g_latch.taps <= 20)
		LOG ("PAD tap #%u latched: buttons 0x%x went down and up between two engine frames; reported down for one frame%s", g_latch.taps, tap,
		     g_latch.taps == 20 ? " (further taps are counted on the END line only)" : "");
	*buttons = held;
	*lx = (int32_t) pad->lx - 128;
	*ly = (int32_t) pad->ly - 128;
	g_k8_rx = (int32_t) pad->rx - 128;   /* kit #8: the right stick (shepherd_vita_k8_pad) */
	g_k8_ry = (int32_t) pad->ry - 128;
	int flags = 2 | quit;
	const unsigned both = SCE_CTRL_START | SCE_CTRL_SELECT;
	if ((pad->buttons & both) == both) {
		static unsigned hold_us;
		if (!hold_us) {
			const char *h = getenv ("SHEPHERD_QUIT_HOLD_MS");
			hold_us = (h && atoi (h) > 0 ? (unsigned) atoi (h) : 1500u) * 1000u;
		}
		SceUInt64 now = sceKernelGetProcessTimeWide ();
		if (!g_quit_hold_t)
			g_quit_hold_t = now;
		else if (now - g_quit_hold_t >= hold_us)
			flags |= 1;
	} else
		g_quit_hold_t = 0;
	return flags;
}
#endif

#ifdef SHEPHERD_GL
/* ---------------------------------------------------------------- the front touch screen (Icalls/VitaTouch.cs, once per frame; kit #5) */
/* kit #5 track touch (vita/notes/kit5-touch.md): the engine reads the FRONT panel once per frame next to the pad and hands the game a
 * mouse (move, left button down / up) -- the PC game's pointer input. Like the pad (pad_latch.h), sceTouchPeek(front, 64) returns the
 * buffered samples and a finger that touched and left the glass between two engine frames is reported once as a tap at the point where
 * it left (touch_latch.h). Set up at the first read (after the env file): SHEPHERD_TOUCH=0 there = no SceTouch call at all (kit #4; the
 * engine then never calls this either). The rear panel is never sampled. TOUCH lines: the panel's set-up, what the firmware returns (read
 * #2 and #500), the first 20 contacts and latched taps, and a TOUCH end line before the END line. Reached through a P/Invoke (GC-safe):
 * no runtime locks, nothing that blocks. */
#include <psp2/touch.h>
#include "touch_latch.h"
#define SHP_TOUCH_SAMPLES 64
static SceTouchData g_touch [SHP_TOUCH_SAMPLES];   /* one reader: the engine's main thread */
static touch_latch g_tlatch = TOUCH_LATCH_INIT;
static int g_touch_state;   /* 0 = not set up yet, 1 = on, -1 = off (SHEPHERD_TOUCH=0, or the panel refused sampling) */
static int g_touch_bufs = SHP_TOUCH_SAMPLES, g_touch_w = 1920, g_touch_h = 1088, g_touch_x0, g_touch_y0;
static unsigned g_touch_reads, g_touch_logged_contacts;

static void touch_setup (void)
{
	const char *e = getenv ("SHEPHERD_TOUCH");
	if (e && !strcmp (e, "0")) {
		g_touch_state = -1;
		LOG ("TOUCH off (SHEPHERD_TOUCH=0): the front panel is not read");
		return;
	}
	int rs = sceTouchSetSamplingState (SCE_TOUCH_PORT_FRONT, SCE_TOUCH_SAMPLING_STATE_START);
	SceTouchPanelInfo pi;
	memset (&pi, 0, sizeof (pi));
	int rp = sceTouchGetPanelInfo (SCE_TOUCH_PORT_FRONT, &pi);
	if (rp >= 0 && pi.maxDispX > pi.minDispX && pi.maxDispY > pi.minDispY) {
		g_touch_x0 = pi.minDispX;
		g_touch_y0 = pi.minDispY;
		g_touch_w = pi.maxDispX - pi.minDispX + 1;
		g_touch_h = pi.maxDispY - pi.minDispY + 1;
	}
	g_touch_state = rs < 0 ? -1 : 1;
	LOG ("TOUCH front panel: sampling %s (rc 0x%08x); panel info rc 0x%08x: active area %d,%d..%d,%d, display %d,%d..%d,%d -> report space %dx%d "
	     "onto the 960x544 screen; the game gets a mouse (Icalls/VitaTouch.cs); rear panel not read; SHEPHERD_TOUCH=0 turns it off",
	     rs < 0 ? "REFUSED: no touch input" : "started", (unsigned) rs, (unsigned) rp, pi.minAaX, pi.minAaY, pi.maxAaX, pi.maxAaY, pi.minDispX,
	     pi.minDispY, pi.maxDispX, pi.maxDispY, g_touch_w, g_touch_h);
}

/* One front-panel read: flags 4 = a sample was read, 1 = a finger on the glass at *x, *y (the newest sample), 2 = a tap latched since the
 * previous read at *x, *y (where it left the glass); *x, *y relative to the panel's display origin, *w, *h = its report space (1920 x 1088
 * on the PCH-1000 / 2000 front panel). 0 = touch off or nothing read. */
int shepherd_vita_touch_read (int32_t *x, int32_t *y, int32_t *w, int32_t *h)
{
	*x = *y = 0;
	if (!g_touch_state)
		touch_setup ();
	*w = g_touch_w;
	*h = g_touch_h;
	if (g_touch_state < 0)
		return 0;
	int n = sceTouchPeek (SCE_TOUCH_PORT_FRONT, g_touch, (SceUInt32) g_touch_bufs);
	if (n < 0 && g_touch_bufs > 1) {
		LOG ("TOUCH sceTouchPeek(front, %d) refused (0x%08x): one sample per read from now on (no taps latched between frames)", g_touch_bufs, (unsigned) n);
		g_touch_bufs = 1;
		n = sceTouchPeek (SCE_TOUCH_PORT_FRONT, g_touch, 1);
	}
	if (n < 1)
		return 0;
	if (n > SHP_TOUCH_SAMPLES)
		n = SHP_TOUCH_SAMPLES;
	int newest = 0, tx = 0, ty = 0;
	unsigned contacts = g_tlatch.contacts;
	int r = touch_latch_step (&g_tlatch, g_touch, n, &newest, &tx, &ty);
	g_touch_reads++;
	if (g_touch_reads == 2 || g_touch_reads == 500) {
		/* what this firmware gives (device check: up to 64 samples over ~1 s expected, like the pad; vlx: a script or equal samples) */
		uint64_t lo = g_touch [newest].timeStamp, hi = lo;
		int asc = 1;
		for (int i = 0; i < n; ++i) {
			if (g_touch [i].timeStamp < lo) lo = g_touch [i].timeStamp;
			if (g_touch [i].timeStamp > hi) hi = g_touch [i].timeStamp;
			if (i && g_touch [i].timeStamp < g_touch [i - 1].timeStamp) asc = 0;
		}
		LOG ("TOUCH read #%u: sceTouchPeek(front, %d) gave %d sample(s) over %.1f ms (%s, newest #%d); contacts so far %u, taps latched %u",
		     g_touch_reads, g_touch_bufs, n, (double) (hi - lo) / 1000.0, asc ? "oldest first" : "not oldest first", newest, g_tlatch.contacts, g_tlatch.taps);
	}
	if (r == TOUCH_CONTACT && g_tlatch.contacts != contacts && ++g_touch_logged_contacts <= 20)
		LOG ("TOUCH contact #%u at (%d,%d) = screen (%d,%d)%s", g_tlatch.contacts, tx, ty, (tx - g_touch_x0) * 960 / g_touch_w, (ty - g_touch_y0) * 544 / g_touch_h,
		     g_touch_logged_contacts == 20 ? " (further contacts are counted on the TOUCH end line only)" : "");
	if (r == TOUCH_TAP && g_tlatch.taps <= 20)
		LOG ("TOUCH tap #%u latched at (%d,%d) = screen (%d,%d): a finger touched and left the glass between two engine frames; the game gets a click there%s",
		     g_tlatch.taps, tx, ty, (tx - g_touch_x0) * 960 / g_touch_w, (ty - g_touch_y0) * 544 / g_touch_h,
		     g_tlatch.taps == 20 ? " (further taps are counted on the TOUCH end line only)" : "");
	*x = tx - g_touch_x0;
	*y = ty - g_touch_y0;
	return 4 | (r == TOUCH_CONTACT ? 1 : r == TOUCH_TAP ? 2 : 0);
}

/* before the END line: what the panel did this session (nothing when touch was never set up: SHEPHERD_TOUCH=0 in the engine too) */
static void touch_end_line (void)
{
	if (g_touch_state > 0)
		LOG ("TOUCH end: %u panel read(s), %u contact(s), %u tap(s) latched between frames%s", g_touch_reads, g_tlatch.contacts, g_tlatch.taps,
		     g_touch_bufs == 1 ? " (one sample per read: no latching)" : "");
}
#endif

#ifdef SHEPHERD_GL
/* ---------------------------------------------------------------- audio (Stage C): shepaudio's entry points for the engine */

#ifdef SHEPHERD_AUDIO
static ap_pool g_apool;
static unsigned g_audio_pool_mb;
static int g_audio_bake;   /* 1 = ux0:data/shepherd/audiobake/menu/manifest.json is there: the engine plays it */
#ifdef SHEPHERD_MAC_CHECK
static pthread_mutex_t g_apool_mu = PTHREAD_MUTEX_INITIALIZER;
static void apool_lock (void *a) { pthread_mutex_lock ((pthread_mutex_t *) a); }
static void apool_unlock (void *a) { pthread_mutex_unlock ((pthread_mutex_t *) a); }
#else
/* shepaudio's IO thread is a SceKernel thread (not a pthread): an LwMutex, not pthread-embedded */
static SceKernelLwMutexWork g_apool_mu;
static void apool_lock (void *a) { sceKernelLockLwMutex ((SceKernelLwMutexWork *) a, 1, NULL); }
static void apool_unlock (void *a) { sceKernelUnlockLwMutex ((SceKernelLwMutexWork *) a, 1); }
#endif
/* the hooks shepaudio's cache and stream rings allocate through (sa_config alloc/free, passed by the engine) */
void *shepherd_audio_alloc (size_t bytes, size_t align, void *user) { return ap_alloc (&g_apool, bytes, align); }
void shepherd_audio_free (void *ptr, void *user) { ap_free (&g_apool, ptr); }
/* out: total KiB, used KiB, peak KiB, allocs, frees, failures (the engine's heartbeat; lock-free reads, approximate) */
int shepherd_audio_pool_stats (unsigned *out, int n)
{
	unsigned v [6] = { g_apool.size / 1024, g_apool.used / 1024, g_apool.peak / 1024, g_apool.allocs, g_apool.frees, g_apool.failures };
	for (int i = 0; i < n && i < 6; ++i)
		out [i] = v [i];
	return 6;
}
#define SA_FN(f) { #f, (void *) f }
static const struct { const char *name; void *fn; } g_audio_procs [] = {
	SA_FN (sa_init_opts), SA_FN (sa_last_error), SA_FN (sa_shutdown), SA_FN (sa_update), SA_FN (sa_set_timing), SA_FN (sa_set_poll_mask),
	SA_FN (sa_poll), SA_FN (sa_get_stats_v), SA_FN (sa_event_count), SA_FN (sa_event_get_info), SA_FN (sa_event_get_param),
	SA_FN (sa_event_preload), SA_FN (sa_event_unload), SA_FN (sa_event_create_index), SA_FN (sa_instance_start), SA_FN (sa_instance_release),
	SA_FN (sa_instance_get_playback_state), SA_FN (sa_instance_stop), SA_FN (sa_instance_set_paused), SA_FN (sa_instance_set_timeline_ms),
	SA_FN (sa_instance_set_param_index), SA_FN (sa_instance_set_volume), SA_FN (sa_instance_set_pitch), SA_FN (sa_instance_set_3d),
	SA_FN (sa_instance_get_timeline_ms), SA_FN (sa_set_listener), SA_FN (sa_set_global_param), SA_FN (sa_bus_find), SA_FN (sa_bus_set_volume),
	SA_FN (sa_bus_set_mute), SA_FN (sa_bus_set_paused), SA_FN (sa_bus_stop_all), SA_FN (sa_file_open), SA_FN (sa_file_write),
	SA_FN (sa_file_close), SA_FN (sa_vita_out_start), SA_FN (sa_vita_out_stop), SA_FN (sa_mix),
	SA_FN (shepherd_audio_alloc), SA_FN (shepherd_audio_free), SA_FN (shepherd_audio_pool_stats),
};
#endif
/* The engine's Audio/ShepAudio.cs resolves every shepaudio entry point here (SHEPHERD_AUDIO_HOST=1): plain C function pointers,
 * called through the same unmanaged-function-pointer path as the GL entry points. NULL = not in this eboot (the silent GL app). */
void *shepaudio_proc (const char *name)
{
#ifdef SHEPHERD_AUDIO
	for (size_t i = 0; name && i < sizeof (g_audio_procs) / sizeof (g_audio_procs [0]); ++i)
		if (!strcmp (g_audio_procs [i].name, name))
			return g_audio_procs [i].fn;
#endif
	return NULL;
}
/* Physics (kit #8, Law of the Vita part 1): shepphys_proc, the level, the pool block, the Mono thread's pin, the END line -- libshepphys's
 * table when build.sh links it (SHP_PHYS_LINK), else kit #7's NULL stub (phys_host.inc.c) */
#include "phys_host.inc.c"
#endif
/* device kit #9 (track fidelity): the FPSCR logged -- the main thread (at start, after vglInit), the engine thread (before / after Main) and, since
 * the second review, every Mono thread at its start and stop (FZ / DN / rounding: the PC keeps denormals; vita/notes/kit9-fidelity.md 4.5, 13.3) */
#include "k9_fp.inc.c"
/* device kit #10 (track fidelity): SHEPHERD_K10_FPSCR=pc -- the PC's float rules (FZ 0, DN 0, nearest) written on every thread the game's code runs on
 * (vita/notes/kit10-fidelity.md section 1; unset / 0 = kit #9: nothing written) */
#include "k10_fp.inc.c"

/* ---------------------------------------------------------------- the Mono thread */

static int g_exit_code = -1;
static volatile int g_host_exiting;
static unsigned g_mono_stack;
static SceUID g_done_sema = -1;
static SceUID g_mono_tid;   /* the Mono main thread (the engine's frames; the GL thread unless the MT pipeline is on); declared above shepherd_vita_pin */
static char g_args_buf [256] = SHP_DEFAULT_ARGS;
static int g_args_from_file;   /* kit #6: the args file was read (its frames are the owner's: never re-based) */

static void clocks_line (const char *when);
static void exit_process (int code);
static const char *logs_dir (void);
#ifdef SHEPHERD_GL
static void gl_fill (float r, float g, float b);
#endif

/* The host's own end of a run the runtime cannot finish (kit #3 review): the FAIL lines, an END line with the run's counters (frame = the
 * engine's last checkpoint / heartbeat), the launch clocks back, and -- when this is the engine's own thread and no render thread can be
 * inside vitaGL -- the red screen for 5 s (the headless apps wait for START or 60 s as at their normal end); then the process exits.
 * Never returns. */
static void end_run_now (const char *what)
{
	static volatile int ending;
	if (ending++) {   /* a second thread at the same time: the first one ends the process */
		for (;;)
			sceKernelDelayThread (1000 * 1000);
	}
	log_mem_ex ("end-now", 1);
	stack_watch_end_lines ("FAIL END");   /* kit #5 review (the threads are all still there: the process exits below) */
	k9_fp_threads_line ("FAIL END");      /* kit #9 second review */
	k10_fp_end_line ("FAIL END");         /* kit #10 fidelity (SHEPHERD_K10_FPSCR on only) */
	int on_main = g_mono_tid && sceKernelGetThreadId () == g_mono_tid;
#ifdef SHEPHERD_GL
	const char *mt = getenv ("SHEPHERD_MT_RENDER");
	int fill = on_main && g_gl_state > 0 && !g_gl_unsafe && !(mt && !strcmp (mt, "1"));
	LOG ("FAIL END frame %d (the engine's last heartbeat), %.1f s: %s -- the app ends itself here instead of the runtime's abort (no crash "
	     "dialog) | %d FAIL line(s), %d OOM, %u heap-cap refusal(s), %u frames shown, %d menu response(s) to the pad, %u short press(es) latched",
	     g_last_frame, wall_s (), what, g_fail_count, g_oom_count, mono_vita_heap_limit_count (), shepgl_swap_count (), g_interactive, g_latch.taps);
	k7_bb_event (what);   /* kit #7: the black box (blackbox.txt) */
	k7_calls_write ("FAIL END");
	k10a_learn_flush ("FAIL END");   /* device kit #10 aot: the learning record so far */
	LOG ("(the screen is %s for 5 s, then the app exits; send back " LOG_PATH ", " LOG_PREV_PATH " and the folder %s/)",
	     fill ? "red" : "left as it is (not on the engine's GL thread)", logs_dir ());
	clocks_line ("end");
	if (fill)
		gl_fill (0.8f, 0.05f, 0.05f);
	sceKernelDelayThread (5 * 1000 * 1000);
#else
	(void) on_main;
	LOG ("FAIL END frame %d (the engine's last heartbeat), %.1f s: %s -- the app ends itself here instead of the runtime's abort (no crash "
	     "dialog) | %d FAIL line(s), %d OOM, %u heap-cap refusal(s)", g_last_frame, wall_s (), what, g_fail_count, g_oom_count,
	     mono_vita_heap_limit_count ());
	k7_bb_event (what);   /* kit #7: the black box (blackbox.txt) */
	k7_calls_write ("FAIL END");
	k10a_learn_flush ("FAIL END");   /* device kit #10 aot: the learning record so far */
	LOG ("(press START to exit, or wait 60 s; send back " LOG_PATH " and the folder %s/)", logs_dir ());
	clocks_line ("end");
	for (int i = 0; i < 600; ++i) {
		SceCtrlData pad;
		memset (&pad, 0, sizeof (pad));
		sceCtrlPeekBufferPositive (0, &pad, 1);
		if (pad.buttons & SCE_CTRL_START)
			break;
		sceKernelDelayThread (100 * 1000);
	}
#endif
	exit_process (3);
}

/* Mono's contract for this hook: it does not return -- after it, exception.c asserts ("should not be reached"), i.e. an abort: the
 * system's crash dialog on the Vita and no verdict line (kit #3 review: vlx vr5, an unhandled OutOfMemoryException after a third Settings
 * opening, raised while the engine's catch formatted the first one). The host ends the run its own way instead (end_run_now). */
static void unhandled_exception (MonoObject *exc, void *user_data)
{
	MonoClass *k = mono_object_get_class (exc);
	if (!g_unhandled_printed)
		mono_print_unhandled_exception (exc);
	const char *ns = mono_class_get_namespace (k), *name = mono_class_get_name (k);
	LOG ("FAIL unhandled managed exception %s.%s (details in the R!> lines above)", ns, name);
	char what [160];
	snprintf (what, sizeof (what), "an unhandled %s.%s ended the run", ns, name);
	end_run_now (what);
}

static void *mono_thread_body (void *arg);

static void *mono_thread (void *arg)
{
	int old;
	pthread_setcancelstate (PTHREAD_CANCEL_DISABLE, &old);
	void *r = mono_thread_body (arg);
	sceKernelSignalSema (g_done_sema, 1);
	return r;
}

/* this app's engine-log folder: runtime/logs/<log name without .txt> (see the header); "" in the mac-check build (runtime/logs) */
static char g_logs_dir [96];
static void logs_dir_init (void)
{
#ifndef SHEPHERD_MAC_CHECK
	char tag [48];
	snprintf (tag, sizeof (tag), "%s", SHP_LOG_NAME);
	char *dot = strrchr (tag, '.');
	if (dot)
		*dot = 0;
	snprintf (g_logs_dir, sizeof (g_logs_dir), DIR_ "/runtime/logs/%s", tag);
	sceIoMkdir (g_logs_dir, 0777);
#endif
}
/* the folder the engine really writes its logs to: an env file's SHEPHERD_LOG_DIR (applied before the host's default, engine_env) wins
 * over this app's own runtime/logs/<app> -- kit #4 STEP 6's env_play.txt moves play-gaot's to play-gaot-readahead (kit #4 review: the H1
 * and END lines named the default folder) */
static const char *logs_dir (void)
{
	const char *e = getenv ("SHEPHERD_LOG_DIR");
	if (e && *e)
		return *e == '/' ? e + 1 : e;   /* rooted "/ux0:..." (pal_vita.c "Paths") -> "ux0:..." as the other lines print it */
	return g_logs_dir [0] ? g_logs_dir : DIR_ "/runtime/logs";
}

#include "k7_measure.h"   /* kit #7 track measure: the black box, the first-call trace, the sampler, the BOOT export */

/* device kit #6 (integration; owner decision 2026-10-19, "Devolver splash: match the original ... frame-clock references re-based once"):
 * with the owner folder's data/video-lengths.tsv (staged by stage-owner.sh) the engine plays the boot's Devolver logo for its 6.27 s
 * (Runtime/K6Engine.cs), and on the frame clock that is SHP_K6_SPLASH_FRAMES more frames before the Main Menu (VLX, the measurement app:
 * the clip plays f535 -> f911, the Main Menu arrives at f1182 instead of f806). So on the frame clock every frame-numbered default of a
 * scripted run moves by it, once: pad.txt's absolute frames (the engine's SHEPHERD_PAD_SHIFT: 1050 / 1400 -> 1426 / 1776), the [sim] and
 * [render] windows (1301-1400 -> 1677-1776), the heap frame, the menu probes, the checkpoints and the run's 1460 frames (-> 1836). Without
 * the table, with SHEPHERD_VIDEO_LENGTHS=0 / SHEPHERD_K6_VIDEO=0 / SHEPHERD_K6_ENGINE=0 in the env file, or on the play clock: 0 = kit #5's
 * timeline. SHEPHERD_FRAME_REBASE=<n> forces n (0 = none). One H1 line says what is in force. */
#ifndef SHP_K6_SPLASH_FRAMES
#define SHP_K6_SPLASH_FRAMES 376u
#endif
static unsigned g_rebase;
/* kit #6 review fix: the host decides with the engine's own rules (Runtime/K6Engine.cs LoadLengths; splash_rows.h), not by a text search
 * of the whole file -- a '#' comment or a malformed row naming the clip made the host re-base by 376 while the engine listed no clip. */
#include "splash_rows.h"
static int file_names_splash (const char *path)
{
	void *data;
	unsigned size;
	if (read_file (path, &data, &size) != 0)
		return 0;
	int named = 0, yes = splash_rows_scan ((char *) data, &named);   /* (read_file NUL-terminates) */
	free (data);
	if (!yes && named)
		LOG ("WARN %s names the Devolver clip but has no row the engine takes (name TAB frames > 0 TAB rate > 0, outside a '#' comment): "
		     "the logo takes 0 s, the frame clock is not re-based", path);
	return yes;
}
static unsigned splash_rebase (const char **why)
{
	const char *r = getenv ("SHEPHERD_FRAME_REBASE"), *v = getenv ("SHEPHERD_VIDEO_LENGTHS"), *k = getenv ("SHEPHERD_K6_VIDEO"), *e = getenv ("SHEPHERD_K6_ENGINE");
	if (r && *r) { *why = "SHEPHERD_FRAME_REBASE in " SHP_ENV_NAME; return (unsigned) strtoul (r, NULL, 10); }
	if ((k && !strcmp (k, "0")) || (e && !strcmp (e, "0"))) { *why = "the engine's video item off"; return 0; }
	if (v && !strcmp (v, "0")) { *why = "SHEPHERD_VIDEO_LENGTHS=0"; return 0; }
	if (v && *v) {   /* the tester's own table (a rooted or ux0: path) */
		*why = "SHEPHERD_VIDEO_LENGTHS's table";
		return file_names_splash (v [0] == '/' ? v + 1 : v) ? SHP_K6_SPLASH_FRAMES : 0;
	}
	*why = DIR_ "/data/video-lengths.tsv";
	return file_names_splash (DIR_ "/data/video-lengths.tsv") ? SHP_K6_SPLASH_FRAMES : 0;
}

/* the managed side's environment: the Vita layout, rooted paths, the V6 run shape; env.txt (already applied) wins */
static void engine_env (void)
{
	static const char *const kv [][2] = {
		{ "SHEPHERD_VITA_HOST", "1" },
		{ "SHEPHERD_PROFILE", "vita" },
		{ "SHEPHERD_RENDER", SHP_RENDER },
#ifdef SHEPHERD_GL
		/* Stage B: GlBackend on the host's vitaGL context (Render/ShepGl.cs), DXT textures from the pack, the Vita's controls,
		 * the menu watch (kind 3 = PASS), screenshots on request only, a bounded [render] stats window (the run is endless) */
		{ "SHEPHERD_GL_HOST", "shepgl" },
		{ "SHEPHERD_GL_DXT", "1" },
		{ "SHEPHERD_VITA_PAD", "1" },
		{ "SHEPHERD_MENU_WATCH", "1" },
		{ "SHEPHERD_SHOT_DIR", ROOTED "/shots" },
		{ "SHEPHERD_RENDER_STATS", "1301:1400" },
#endif
#ifdef SHP_MT_DEFAULT
		/* the MT variant (build.sh SHEPHERD_MT_RENDER=1, SHPD00096): the multicore render pipeline (Render/MtRender.cs) on, and with it the
		 * core pinning (pin_requested); SHEPHERD_MT_RENDER=0 in the env file turns both off */
		{ "SHEPHERD_MT_RENDER", "1" },
#endif
#ifdef SHEPHERD_AUDIO
		/* Stage C: set only when the bake is there (audio_env below); the engine's default without them is the silent stubs */
#endif
		{ "SHEPHERD_TIME", SHP_TIME_DEFAULT },   /* kit3 play mode: wall (GL apps: played) | frame (headless: scripted); env file wins */
		{ "SHEPHERD_ROOT", ROOTED },
		{ "SHEPHERD_GAME_DATA", ROOTED "/data" },
		{ "SHEPHERD_MANAGED", ROOTED "/managed" },
		{ "SHEPHERD_PACK", ROOTED "/pack" },
		{ "SHEPHERD_PACK_STRICT", "1" },
		{ "SHEPHERD_PERSISTENT", ROOTED "/persistent" },
		{ "SHEPHERD_MENU_PROBE", "1040,1300,1450" },
		{ "SHEPHERD_CHECKPOINTS", "0,500,1050,1300" },
		{ "SHEPHERD_PROGRESS_EVERY", "50" },
		{ "DOTNET_SYSTEM_GLOBALIZATION_INVARIANT", "1" },
		{ "DOTNET_SYSTEM_GLOBALIZATION_PREDEFINED_CULTURES_ONLY", "0" },
		/* a coop stop-the-world that waits > 30 s for a thread is a FAIL, not a silent hang (game threads are slower
		 * to reach a safepoint than the hello's; MONO_SLEEP_ABORT_LIMIT in env.txt overrides) */
		{ "MONO_SLEEP_ABORT_LIMIT", "30000" },
	};
	{
		const char *ck = getenv ("SHEPHERD_CLOCK");   /* an older env file's Stage B switch: SHEPHERD_CLOCK=wall = SHEPHERD_TIME=wall */
		if (!getenv ("SHEPHERD_TIME") && ck && !strcmp (ck, "wall"))
			setenv ("SHEPHERD_TIME", "wall", 1);
	}
	{
		/* kit #6: the frame-clock re-base (above), before the defaults below take their kit #5 values */
		const char *tm0 = getenv ("SHEPHERD_TIME");
		const char *why = "";
		if (!(tm0 ? !strcmp (tm0, "wall") : !strcmp (SHP_TIME_DEFAULT, "wall")))
			g_rebase = splash_rebase (&why);
		if (g_rebase) {
			char b [64];
			snprintf (b, sizeof (b), "%u,%u,%u", 1040u + g_rebase, 1300u + g_rebase, 1450u + g_rebase);
			setenv ("SHEPHERD_MENU_PROBE", b, 0);
			snprintf (b, sizeof (b), "0,500,%u,%u", 1050u + g_rebase, 1300u + g_rebase);
			setenv ("SHEPHERD_CHECKPOINTS", b, 0);
#ifdef SHEPHERD_GL
			snprintf (b, sizeof (b), "%u:%u", 1301u + g_rebase, 1400u + g_rebase);
			setenv ("SHEPHERD_RENDER_STATS", b, 0);
#endif
			snprintf (b, sizeof (b), "%u", g_rebase);
			setenv ("SHEPHERD_PAD_SHIFT", b, 0);
		}
		LOG ("H1 frame clock %s%u frames (%s): the Devolver splash %s; SHEPHERD_VIDEO_LENGTHS=0 = kit #5's timeline", g_rebase ? "re-based by " : "not re-based: ",
		     g_rebase, why, g_rebase ? "plays its 6.27 s, so pad.txt's absolute frames, the [sim] / [render] windows, the menu probes, the checkpoints and a "
		     "default run's 1460 frames move by that much (kit #6 owner decision)" : "takes 0 s as in kits #3-#5 (or the play clock: frames are not scripted)");
	}
	for (size_t i = 0; i < sizeof (kv) / sizeof (kv [0]); ++i)
		setenv (kv [i][0], kv [i][1], 0);
#ifdef SHP_BCL_AOT_TRIM
	/* device kit #8 speed (review fix): the build-time BCL AOT trim of this eboot (build.sh SHEPHERD_BCL_AOT_TRIM: the modules, or "0") for the
	 * engine's [k8] start line -- no env switch undoes it, so SHEPHERD_K8_SPEED=0 says so; overwrites any env-file value (a build fact) */
	setenv ("SHEPHERD_BUILD_BCL_AOT_TRIM", SHP_BCL_AOT_TRIM, 1);
#endif
	{
		/* the scripted presses of pad.txt (absolute frames, made for the frame clock): applied with the frame clock only; with the
		 * play clock the frames would land on whatever screen the owner is on (SHEPHERD_PAD_FILE in the env file still scripts them) */
		const char *tm = getenv ("SHEPHERD_TIME");
		int wall = tm && !strcmp (tm, "wall");
		if (!wall)
			setenv ("SHEPHERD_PAD_FILE", ROOTED "/pad.txt", 0);
		/* the [sim] window (frames 1301-1400: the idle Main Menu of a scripted run) and, with the frame clock only, the heap measurement
		 * at frame 1300 -- a forced full GC (~1.7 s on the device): a measurement of the scripted run, never a freeze in the middle of
		 * the owner's play (device kit #3; the env file still sets SHEPHERD_SIMSTATS=1301:1400:1300). Kit #6: + g_rebase on the frame clock */
		char sim [48];
		snprintf (sim, sizeof (sim), "%u:%u:%u", 1301u + g_rebase, 1400u + g_rebase, 1300u + g_rebase);
		setenv ("SHEPHERD_SIMSTATS", wall ? "1301:1400" : sim, 0);
	}
	if (g_logs_dir [0]) {
		char rooted [112];
		snprintf (rooted, sizeof (rooted), "/%s", g_logs_dir);   /* rooted, like SHEPHERD_ROOT (pal_vita.c "Paths") */
		setenv ("SHEPHERD_LOG_DIR", rooted, 0);
	}
#ifdef SHP_DIAG
	dg_engine_env ();   /* SHEPHERD_DIAG_PHASE_BUF: where the engine writes its boot phases */
#endif
#ifdef SHEPHERD_AUDIO
	if (g_audio_bake) {
		/* the engine plays the bake through this eboot's shepaudio (shepaudio_proc), on sceAudioOut; the cache gets the pool minus
		 * the stream rings (1 MiB) and 1 MiB of slack for fragmentation and the transient compressed copy of a decoding take.
		 * The play apps (kit #3 review): the same 1 MiB of rings as 4 streams x 65536 frames (1.37 s each) instead of 8 x 32768 (0.68 s):
		 * the music's ring outlasts a large pack read on the main thread (the card: ~10 MiB/s; the menu plays at most 2 streams at once
		 * under vlx) */
		char opts [96];
#ifdef SHP_PLAY_APP
		snprintf (opts, sizeof (opts), "cache_kib=%u,streams=4,stream_frames=65536", g_audio_pool_mb > 3 ? (g_audio_pool_mb - 2) * 1024 : 1024);
#else
		snprintf (opts, sizeof (opts), "cache_kib=%u", g_audio_pool_mb > 3 ? (g_audio_pool_mb - 2) * 1024 : 1024);
#endif
		setenv ("SHEPHERD_AUDIO_HOST", "1", 0);
		setenv ("SHEPHERD_AUDIO_BAKE", SHP_AUDIO_BAKE, 0);
		setenv ("SHEPHERD_AUDIO_OUT", "vita", 0);
		setenv ("SHEPHERD_AUDIO_OPTS", opts, 0);
	}
#endif
}

#include "k9_gc.inc.c"   /* kit #9 track gc: lever 32, M6, per-scene nursery sizing, the used-size fix, M8 (vita/notes/kit9-gc.md) */
#include "k10_mem.inc.c"  /* kit #10 track memory: the shared pool's partition + GC limits, evacuation, size classes, the pressure ladder, MemReport */
#include "k11_walk.inc.c"  /* kit #11 track walk: the C heap's census (MemReport); the walk defaults are the DEFAULTS block -- vita/notes/kit11-walk.md */
#include "k12_walka.inc.c"  /* kit #12 track walka: the walk, GC side (a refused evacuation copy marked in place, ...) -- vita/notes/kit12-walka.md */
#include "k12_walkb.inc.c"  /* kit #12 track walkb: interpreter tiering, the VPK's DLLs in ILZ form, the object-header census -- vita/notes/kit12-walkb.md */
#include "k13_walkc.inc.c"  /* kit #13 track walkc: the IL pool in cached video memory, the "MiB short" meter -- vita/notes/kit13-walkc.md */
#include "k14_newlib.inc.c" /* kit #14 track newlib: the runtime's rows off the C heap (interface bitmaps on first read, ...) -- vita/notes/kit14-newlib.md */

static void *mono_thread_body (void *arg)
{
	int tmp;
	g_mono_tid = sceKernelGetThreadId ();
	k10_fp_apply ("engine thread at start", 0);   /* kit #10 fidelity: a new thread has the Vita's FPSCR again (SHEPHERD_K10_FPSCR; off = nothing) */
	LOG ("H3 mono thread running (sp~%p, stack %u KiB, SceUID 0x%x)", (void *) &tmp, g_mono_stack / 1024, (unsigned) g_mono_tid);
#ifdef SHEPHERD_GL
	phys_pin_mono ();   /* kit #8: the physics thread on USER_0 (level >= 1, kit 3's pinning off; phys_host.inc.c) */
#endif
	stack_watch_mono_start ();   /* kit #5 review: the STK lines (SHEPHERD_STACK_WATCH) */
#ifdef SHP_DIAG
	g_dg_mono_tid = sceKernelGetThreadId ();
#endif

	char gcp [256];
	/* soft-heap-limit = the cap - SHP_SOFT_MARGIN_MB (device kit #2 review): above it sgen runs MAJOR collections in time, so a minor
	 * collection no longer meets a major heap that is full to the cap -- its promotions refused block by block, the survivors pinned in
	 * the nursery (2,900 refusals at frame 1 of the GL apps' 128 / 120 MiB caps under vlx; 0 with the soft limit, same GC counts).
	 * kit #9 gc (k9_gc.inc.c): the collector / nursery parts follow the env file's GC switches; with them off the string is kit #8's
	 * "major=marksweep,no-concurrent-sweep,nursery-size=4m,max-heap-size=<cap>m[,soft-heap-limit=<cap - 16>m]" exactly. */
	k10_gc_params (gcp, sizeof (gcp));   /* kit #10 memory: in the shared pool the cap is the floor's (k10_mem.inc.c); else nothing */
	k9_gc_params (gcp, sizeof (gcp), g_max_heap_mb, g_max_heap_mb >= 3 * SHP_SOFT_MARGIN_MB);
	k10_gc_params_tail (gcp, sizeof (gcp));   /* kit #10 memory: evacuation-threshold= (SHEPHERD_K10_EVAC); else nothing */
	setenv ("MONO_GC_PARAMS", gcp, 0);
	{
		/* env.txt may have set its own MONO_GC_PARAMS: report the cap that is really in force */
		const char *mh = strstr (getenv ("MONO_GC_PARAMS"), "max-heap-size=");
		if (mh) {
			char *end;
			unsigned long v = strtoul (mh + 14, &end, 10);
			g_max_heap_mb = (unsigned) (*end == 'g' || *end == 'G' ? v * 1024 : *end == 'k' || *end == 'K' ? v / 1024 : *end == 'm' || *end == 'M' ? v : v / MiB);
		} else {
			g_max_heap_mb = 0;
			LOG ("WARN MONO_GC_PARAMS has no max-heap-size: a full arena ends the process (sgen exit) instead of an OutOfMemoryException");
		}
	}
	LOG ("H4 MONO_GC_PARAMS=%s (heap cap %u MiB)", getenv ("MONO_GC_PARAMS"), g_max_heap_mb);
	k10_gc_params_in_force (gcp);   /* kit #10 memory review fix R7: in the shared pool an env-file MONO_GC_PARAMS max-heap-size stays the cap's ceiling (a WARN line); else nothing */
	k9_gc_line ();   /* kit #9 gc: the K9 GC line (the switches in force) */
	k12_walka_before_runtime ();   /* kit #12 walka: the K12 WALKA line; the runtime's flags (SHEPHERD_K12_WALKA=0: kit #11) */
	k12_walka_scrub (gcp, sizeof (gcp));   /* kit #12 walka: the GC parameters' text off the Mono thread's stack (a conservative false pin; SHEPHERD_K12_SCRUB=0: kept) */
#if !defined (SHEPHERD_MAC_CHECK)
	gcgov_config ();   /* M2: the GCG line (the GC.Collect governor's sites) */
#endif
	mono_trace_set_log_handler (mono_log_cb, NULL);
	mono_trace_set_print_handler (mono_print_cb);
	mono_trace_set_printerr_handler (mono_print_cb);

	mono_vita_register_static_symbols (shepherd_static_symbols, shepherd_static_symbols_count);
	LOG ("H5 static symbol table: %d entries", shepherd_static_symbols_count);
	if (g_thread_stack != 1024u * 1024u)   /* kit #5 M3: Mono's threads without an explicit size (the runtime's default: 1 MiB = kit #4) */
		mono_vita_set_default_thread_stack_size (g_thread_stack);

	char override_ptr [32];
	snprintf (override_ptr, sizeof (override_ptr), "%p", (void *) &pinvoke_override);
	/* the engine's runtimeconfig.json knobs (InvariantGlobalization + PredefinedCulturesOnly=false: Unify's
	 * GetCultureInfo ("en-us") throws without it and the boot dies at frame ~2, see v6-mac-sim.md) */
	const char *keys [] = { "APP_CONTEXT_BASE_DIRECTORY", "RUNTIME_IDENTIFIER", "PINVOKE_OVERRIDE",
		"System.Globalization.Invariant", "System.Globalization.PredefinedCulturesOnly",
		"System.Reflection.Metadata.MetadataUpdater.IsSupported", "System.Runtime.Serialization.EnableUnsafeBinaryFormatterSerialization",
		"System.GC.Concurrent",
		/* the Vita runtime has no EventPipe (DISABLE_EVENTPIPE); without this switch the first ArrayPool use runs
		 * ArrayPoolEventSource's cctor -> NotImplementedException -> a NullReferenceException inside an exception's ctor ->
		 * exception.c:137 assert (the V5 hello's device fatal, devfail1/hello-nre.md; WASI gets it from the SDK) */
		"System.Diagnostics.Tracing.EventSource.IsSupported" };
	const char *values [] = { "/app0:/managed/", "linux-arm", override_ptr, "true", "false", "false", "false", "false", "false" };
#ifdef SHEPHERD_MAC_CHECK
	/* mac-check: Microsoft's Mono finds the Mac BCL natives by path (there is no static table) */
	keys [0] = "NATIVE_DLL_SEARCH_DIRECTORIES";
	values [0] = getenv ("MAC_NATIVE_DIR");
#endif
#ifdef SHP_DIAG
	/* repro mode: the 2026-10-07 property set (the EventSource switch, the last property, left out) */
	int nprops = (int) (sizeof (keys) / sizeof (keys [0])) - (g_dg_mode == 1 ? 1 : 0);
	int r = monovm_initialize (nprops, keys, values);
	LOG ("H6 monovm_initialize -> %d (%d properties; EventSource.IsSupported %s)", r, nprops, g_dg_mode == 1 ? "NOT set (repro)" : "= false");
	mono_install_assembly_load_hook (dg_assembly_loaded, NULL);
#else
	k10_before_runtime ();   /* kit #10 memory: the extra slot sizes (SHEPHERD_K10_SIZES) before sgen's init; else nothing */
	int r = monovm_initialize ((int) (sizeof (keys) / sizeof (keys [0])), keys, values);
	LOG ("H6 monovm_initialize -> %d", r);
#endif

	/* (k9_floatpc_config, SHEPHERD_K9_FLOATPC -- the row 2 switch, off = kit #8 -- now runs in main before k10_ilb_start: kit #10 integration review) */
	mono_install_assembly_preload_hook (preload_hook, NULL);
	shepherd_register_aot_modules ();
	int nmods = 0;
	while (shepherd_aot_module_names [nmods])
		nmods++;
#ifdef SHP_GAME_AOT
	{
		int ngame = 0;
		while (shepherd_owner_aot_module_names [ngame])
			ngame++;
		const char *ga = getenv ("SHEPHERD_GAME_AOT");
		int on = !(ga && !strcmp (ga, "0"));
		int ga1 = ga && !strcmp (ga, "1");
		/* kit #9 review: with SHEPHERD_K9_FLOATPC on, the owner modules (compiled against the original DLLs) would meet FloatPc's new MVIDs --
		 * in aot-only mode Mono stops with a fatal error at the first module whose assembly or a referenced one changed (vlx, review run
		 * f3fpc95: "doesn't match assembly") -- so they stay unregistered (all game code interpreted, as SHEPHERD_GAME_AOT=0) unless
		 * SHEPHERD_GAME_AOT=1 says the owner AOT was rebuilt against the rewritten set. Second review: only when the folder really holds one of
		 * THEIR assemblies (their image tables, shepherd_owner_aot_bound_names; the engine-bound ones never load from it) -- a missing / empty
		 * folder, or one with other DLLs only, keeps them registered (and their assemblies refused from the folder) */
		const char *hit = NULL;
		int nhit = 0;
		if (on && !ga1 && g_k9_fpc_dir [0])
			for (const char *const *p = shepherd_owner_aot_bound_names; *p; p++)
				if (!k9_name_in (shepherd_aot_bound_names, *p) && k9_fpc_has (*p)) {
					if (!hit)
						hit = *p;
					nhit++;
				}
		int fpc = nhit > 0;
		if (fpc)
			on = 0;
		g_k9_fpc_owner_bound = on && !ga1;
		if (on)
			shepherd_register_owner_aot_modules ();
		LOG ("H7 game AOT %s (SHEPHERD_GAME_AOT=%s): %d owner-local game modules (profile-guided partial: their hot methods compiled in, "
		     "the rest interpreted)%s%s%s%s", on ? "ON" : "off", ga ? ga : "unset", ngame,
		     on ? " registered" : fpc ? " NOT registered: SHEPHERD_K9_FLOATPC is on and its folder holds " : " NOT registered: all game code runs "
		     "in the interpreter (the play app's execution model without them)", fpc ? (nhit == 1 ? "one of their assemblies, " : "their assemblies, e.g. ") : "",
		     fpc ? hit : "", fpc ? " (the modules were built against the original DLLs; SHEPHERD_GAME_AOT=1 = an owner AOT built against the rewritten "
		     "set): all game code runs in the interpreter" : on && g_k9_fpc_dir [0] && !ga1 ? " (SHEPHERD_K9_FLOATPC is on, but its folder holds none of "
		     "their assemblies)" : "");
		if (g_k9_fpc_owner_bound && g_k9_fpc_dir [0])
			k9_fpc_warn_bound (shepherd_owner_aot_bound_names);
	}
#endif
#ifdef SHEPHERD_MAC_CHECK
	mono_jit_set_aot_mode (MONO_AOT_MODE_INTERP_ONLY);   /* mac-check: no AOT images; the interpreter runs everything (stage A's mode) */
#else
	mono_jit_set_aot_mode (MONO_AOT_MODE_INTERP);
#endif
#ifdef SHEPHERD_MAC_CHECK
	LOG ("H7 mac-check: no AOT modules, interpreter only; assemblies: preload hook (app0:managed, then " GAME_MANAGED ")");
#else
	LOG ("H7 %d static AOT modules, aot mode INTERP (no JIT, no run-time code); assemblies: preload hook (app0:managed, then " GAME_MANAGED ")", nmods);
#endif
	mono_install_unhandled_exception_hook (unhandled_exception, NULL);
	MonoProfilerHandle ph = mono_profiler_create (&g_prof);
	mono_profiler_set_gc_event_callback (ph, gc_event_cb);
	mono_profiler_set_exception_throw_callback (ph, exception_throw_cb);
	stack_watch_profiler (ph);   /* kit #5 review: every thread Mono starts or attaches is painted on start, scanned when it stops */
	k7_profiler (ph);            /* kit #7: the first-call trace (SHEPHERD_K7_PROF=calls; off: no callback) */
	k9_fp_profiler ();           /* kit #9 fidelity, second review: every Mono thread's FPSCR at its start / stop (SHEPHERD_K9_FP=0: no hook) */
	k10_fp_profiler ();          /* kit #10 fidelity: SHEPHERD_K10_FPSCR's rules on every Mono thread at its start (created after kit #9's: runs first) */

	k12_walkb_pre_init ();       /* kit #12 walkb: the interpreter's options (SHEPHERD_K12_TIERING) before mini_init reads them; its K12 WALKB line */
	k14_newlib_pre_init ();      /* kit #14 newlib: the runtime's rows, before any class is set up */
	MonoDomain *domain = mono_jit_init_version ("shepherd", "v4.0.30319");
	if (!domain) {
		LOG ("FAIL H8 mono_jit_init_version returned NULL");
		return NULL;
	}
	g_runtime_up = 1;
	LOG ("H8 mono_jit_init_version ok (CoreLib loaded from app0:managed, its AOT image accepted)");
	k10_after_runtime ();   /* kit #10 memory: the shared pool's live GC limits, the slot sizes in force; else nothing */
	log_cicache ();
	k9_log_fast_tls ();
	k10a_log ();   /* device kit #10 aot: the learning record / compressed IL H8 lines */
	log_mem ("after-init");
	mono_gc_init_finalizer_thread ();
	LOG ("H9 finalizer thread started");

	MonoImageOpenStatus st = MONO_IMAGE_OK;
	MonoAssemblyName *an = mono_assembly_name_new ("Shepherd.Engine");
	MonoAssembly *engine = an ? mono_assembly_load (an, NULL, &st) : NULL;
	if (!engine) {
		LOG ("FAIL H10 cannot load Shepherd.Engine from " VPK_MANAGED " (status %d)", (int) st);
		return NULL;
	}
	char *argv [8] = { (char *) "Shepherd.Engine.dll" };
	int argc = 1;
	char *save = NULL;
	for (char *t = strtok_r (g_args_buf, " \t\r\n", &save); t && argc < 8; t = strtok_r (NULL, " \t\r\n", &save))
		argv [argc++] = t;
	LOG ("H10 Shepherd.Engine loaded; entering Main (%s %s %s)%s", argc > 1 ? argv [1] : "", argc > 2 ? argv [2] : "", argc > 3 ? argv [3] : "", argc > 4 ? " ..." : "");
	log_mem ("before-main");
	k9_fp_line ("engine thread before Main");   /* kit #9 fidelity: the FPSCR Mono's main managed thread runs the game with */
	g_exit_code = mono_jit_exec (domain, engine, argc, argv);
	LOG ("H11 Main returned %d at %.1f s", g_exit_code, wall_s ());
	k9_fp_line ("engine thread after Main");
	k9_fp_threads_line ("H11");   /* kit #9 second review: every Mono thread's FPSCR (k9_fp.inc.c) */
	k10_fp_end_line ("H11");      /* kit #10 fidelity: what SHEPHERD_K10_FPSCR did (on only) */
	k9_floatpc_end_line ();   /* kit #9 second review: what SHEPHERD_K9_FLOATPC's folder supplied */
	k7_calls_write ("H11");   /* kit #7: prof-calls.tsv (SHEPHERD_K7_PROF=calls) */
	k10a_learn_flush ("H11");  /* device kit #10 aot: the learning record's last lines */
	k7_cpu_line ("H11");      /* kit #7: CPU time per Mono thread (the render thread, the game's threads: the A* path threads ...) */
	log_mem ("end");
	mono_counters_line ("H11");
	stack_watch_end_lines ("end");   /* kit #5 review: the deepest stack use of every watched thread (this one still alive) */
	return NULL;
}

#ifndef SHP_DIAG
/* STALL? watchdog (every build but the diag one, whose heartbeat thread does more; device kit #2 review): when nothing has been
 * logged -- no host line, no engine echo line, kept or not -- for SHP_STALL_S, one line with counters that keep moving while the
 * runtime works (interpreter methods/transforms, arena, GC heap) and the last engine line. Slow loading can do this too: two STALL?
 * lines with different counters = still working; the same counters for 10 minutes = frozen. The GL apps' screen belongs to vitaGL
 * (its logo stays up until the engine's first frame), so for them this line is the only record of a hang before the first frame.
 * Reads only lock-free numbers (no mallinfo: newlib's malloc lock may be what is stuck). */
#define SHP_STALL_S 120u
static void stall_check (void)
{
	static SceUInt64 last_stall;
	SceUInt64 now = sceKernelGetProcessTimeWide (), act = g_activity_t;
	if (!act || now - act < SHP_STALL_S * 1000000ull || (last_stall && now - last_stall < SHP_STALL_S * 1000000ull))
		return;
	last_stall = now;
	MonoVitaRuntimeStats rs;
	memset (&rs, 0, sizeof (rs));
	if (g_runtime_up)
		mono_vita_get_runtime_stats (&rs);
	MonoVitaArenaStats as;
	int aok = mono_vita_vm_arena_try_get_stats (&as) == 0;
	char line [600];
	snprintf (line, sizeof (line), "STALL? nothing logged for %.0f s (wall %.1f s, last engine frame %d) | interp %d methods, %lld transforms; aot %d methods | arena %s%u KiB used | gc heap %u KiB, heap-cap refusals %u, OOM %d | newlib peak %u KiB | last engine line: %.110s",
		  (double) (now - act) / 1e6, wall_s (), g_last_frame, rs.interp_methods, (long long) rs.interp_transforms, rs.aot_methods,
		  aok ? "" : "~", aok ? (unsigned) (as.used / 1024) : 0u, g_runtime_up ? (unsigned) (mono_gc_get_heap_size () / 1024) : 0u,
		  mono_vita_heap_limit_count (), g_oom_count, k10_nl_fix_k (__malloc_max_sbrked_mem), g_last_engine_line [0] ? g_last_engine_line : "(none yet)");   /* (kit #10 review fix R8: without the pool's gap; = kit #9's figure outside the pool) */
	log_line_ex (line, 0);
	k7_bb_event ("STALL? (the watchdog: nothing logged for 120 s)");   /* kit #7 */
}
#endif

/* device kit #8 (track boot): the hardware model + real firmware, the clean boot screen, the launcher handshake (vita/notes/kit8-boot.md) */
#include "k8_boot.inc.c"
/* device kit #11 (track device): kit #10's device results as defaults, the player build, the boot screen's emblem set (vita/notes/kit11-device.md) */
#include "k11_device.inc.c"
#include "k12_defaults.inc.c"   /* device kit #12 integration: the kit #12 defaults table (the owner's 2026-11-04 answer) -- vita/notes/kit12-integration.md section 6b */
#include "k13_defaults.inc.c"   /* device kit #13 integration: the kit #13 defaults table (FloatPc + looks' rows; speed's own table folded in) -- vita/notes/kit13-integration.md section 6 */
#include "k12_boot.inc.c"   /* device kit #12, track boot: the owner's boot sequence (host half), the recommended plugins on the loading screen -- vita/notes/kit12-boot.md */
/* device kit #10 (track boot): the boot that always shows something -- the game's own loading screen during the engine's boot (vita/notes/kit10-boot.md) */
#include "k10_boot.inc.c"
/* device kit #7 (track probes): the clocks kept / logged / measured, the device profile, P3 suspend / resume (vita/notes/kit7-probes.md) */
#include "k7_host.inc.c"
/* device kit #8 (track play): the measured clock shown, the right stick + rear touch, G3 frame pacing (vita/notes/kit8-play.md) */
#include "k8_play.inc.c"

#include "k10_io.inc.c"   /* device kit #10, track io: the chunked pack's LZ4 (+ zstd) decoders, the DMA copy (vita/notes/kit10-io.md) */
/* device kit #10 (track cache): the card's free space for the learning cache's free-space margin (vita/notes/kit10-cache.md) */
#include "k10_cache.inc.c"
#include "k13_bootc.inc.c"   /* device kit #13, track bootc: Verify Files over the compressed code files (vita/notes/kit13-bootc.md) */
#ifdef SHP_TEST_CLOCK
#include "k7_probes.inc.c"   /* the measurement app's device probes (memory, big pages, storage, NGS, GPU transfer) */
#endif

/* keeps the Vita awake (no auto-suspend / screen off) during the ~30-60 min run; a plain native thread, never attached.
 * Kit #7: also the clock watchdog every 5 s (k7_clk_watch: CLK change / measured lines; SHEPHERD_K7_CLK=0: none) and, in the GL apps, the
 * P3 suspension watch -- a 250 ms tick that books a gap of more than 1.5 s as a suspension (k7_susp_book) and sets the clocks again after
 * one; the 5 s work (power tick, STALL? check, clock watch, the live black box) keeps its 5 s pace, outside the watched time (k7_susp_busy:
 * the gap before it is booked, nothing during it, the tick restarts after it). SHEPHERD_K7_SUSPEND=0 (or SHEPHERD_STRICT_ORIGINAL=1): kit
 * #6's 5 s loop. */
/* kit #9 gc (found by the track's vlx runs, a kit #8 race): pin_requested () read the environment on THIS thread (getenv) while the Mono
 * thread was adding the engine's env defaults (setenv: newlib reallocates environ) -- a SIGSEGV in strncmp under getenv, ~1 start in 10
 * under vlx with concurrent-GC timing. main reads it once before this thread exists (the same value: the env file and the build's
 * defaults are in place by then -- the H1 line shows the same answer). */
static int g_power_pin = -1;
static void *power_thread (void *arg)
{
	int old;
	pthread_setcancelstate (PTHREAD_CANCEL_DISABLE, &old);
	if (g_power_pin > 0)
		shepherd_vita_pin (shepherd_vita_worker_mask (), "power");   /* k4-display: a latency-tolerant worker (CPU3, else USER_2) */
#ifdef SHEPHERD_GL
	if (g_k7_susp) {
		for (unsigned i = 0;; i++) {
			k7_susp_book (0);
			if (i % 20 == 0) {   /* every 5 s (20 ticks of 250 ms): kit #6's loop body */
				k7_susp_busy (1);   /* (integration review: its own time -- a slow card write -- is never booked as a suspension) */
				sceKernelPowerTick (SCE_KERNEL_POWER_TICK_DEFAULT);
				if (i) {
#ifndef SHP_DIAG
					stall_check ();
#endif
					k7_clk_watch ();
					k7_bb_live ();   /* kit #7 integration: the measure track's live black box keeps its 5 s pace in P3's 250 ms loop too */
					k10a_learn_flush (NULL);   /* device kit #10 aot: new learning records appended (none new: nothing written) */
				}
				k7_susp_busy (0);
			}
			if (g_susp_reapply) {
				g_susp_reapply = 0;
				k7_clk_reapply ();
			}
			sceKernelDelayThread (K7_SUSP_TICK_US);
		}
	}
#endif
	for (;;) {
		sceKernelPowerTick (SCE_KERNEL_POWER_TICK_DEFAULT);
		sceKernelDelayThread (5 * 1000 * 1000);
#ifndef SHP_DIAG
		stall_check ();
#endif
		k7_clk_watch ();   /* (kit #7; a no-op with SHEPHERD_K7_CLK=0) */
		k7_bb_live ();   /* kit #7: blackbox-live.txt when something new arrived */
		k10a_learn_flush (NULL);   /* device kit #10 aot: new learning records appended (none new: nothing written) */
	}
	return NULL;
}

static void log_exit (void)
{
	if (!g_host_exiting) {
		log_mem_ex ("exit", 1);
		LOG ("FAIL process exit() called outside the host (e.g. sgen could not get arena memory: see the ARENA / R> lines above)");
		k7_bb_event ("exit() outside the host");   /* kit #7 */
	}
}

/* ---------------------------------------------------------------- clocks (every build) */

/* (g_clock_launch / g_clock_set: k7_host.inc.c, above -- the kit #7 watchdog needs them before this point) */

static void clocks_line (const char *when)
{
	LOG ("CLK %s: arm %d bus %d gpu %d xbar %d MHz (launch %d/%d/%d/%d%s)", when, scePowerGetArmClockFrequency (), scePowerGetBusClockFrequency (),
	     scePowerGetGpuClockFrequency (), scePowerGetGpuXbarClockFrequency (), g_clock_launch [0], g_clock_launch [1], g_clock_launch [2],
	     g_clock_launch [3], g_clock_set ? "" : "; SHEPHERD_CLOCKS=0: left at launch");
}

/* 444/222/222/166 MHz, the V8 clocks, for the whole run (also the headless builds: their device times are only comparable with the
 * projections at 444); SHEPHERD_CLOCKS=0 in the env file keeps the launch clocks. Every rc and the read-back are logged. */
static void clocks_start (void)
{
	g_clock_launch [0] = scePowerGetArmClockFrequency ();
	g_clock_launch [1] = scePowerGetBusClockFrequency ();
	g_clock_launch [2] = scePowerGetGpuClockFrequency ();
	g_clock_launch [3] = scePowerGetGpuXbarClockFrequency ();
	const char *ck = getenv ("SHEPHERD_CLOCKS");
	if (ck && !strcmp (ck, "0")) {
		clocks_line ("start");
		return;
	}
	/* kit #7 (roadmap "500 MHz with PSVshellPlus"): a launch clock ABOVE the app's is kept -- a PSVshellPlus profile saved for the title sets
	 * the clocks as the app starts, and forcing 444 here overrode it (kit #6's device run). Only higher ones: a launch clock below the app's
	 * (the stock 333 MHz) is raised as before, so without such a plugin this is kit #6's code path and log line, byte for byte.
	 * SHEPHERD_K7_CLK=0: kit #6 (444/222/222/166 forced) */
	static const int want [4] = { 444, 222, 222, 166 };
	static const char *const dom [4] = { "arm", "bus", "gpu", "xbar" };
	int keep [4] = { 0, 0, 0, 0 }, kept = 0;
	for (int i = 0; i < 4; i++) {
		g_clock_target [i] = want [i];
		if (g_k7_clk && g_clock_launch [i] > want [i]) {
			keep [i] = 1;
			g_clock_target [i] = g_clock_launch [i];
			kept++;
		}
	}
	g_clock_base_arm = g_clock_target [0];
	int ra = keep [0] ? 0 : scePowerSetArmClockFrequency (444), rb = keep [1] ? 0 : scePowerSetBusClockFrequency (222);
	int rg = keep [2] ? 0 : scePowerSetGpuClockFrequency (222), rx = keep [3] ? 0 : scePowerSetGpuXbarClockFrequency (166);
	g_clock_set = 1;
	if (!kept)
		LOG ("CLK set 444/222/222/166: rc arm 0x%08x bus 0x%08x gpu 0x%08x xbar 0x%08x (restored at exit; SHEPHERD_CLOCKS=0 keeps the launch clocks)",
		     (unsigned) ra, (unsigned) rb, (unsigned) rg, (unsigned) rx);
	else {
		char k [96] = "";
		for (int i = 0; i < 4; i++)
			if (keep [i]) {
				size_t kn = strlen (k);
				snprintf (k + kn, sizeof k - kn, "%s%s %d", kn ? ", " : "", dom [i], g_clock_launch [i]);
			}
		LOG ("CLK keep: launch clock above the app's (%s MHz): kept, never lowered (a plugin's, e.g. a PSVshellPlus profile; CPU Speed '444' = it; "
		     "SHEPHERD_K7_CLK=0: 444 as kit #6)", k);
		LOG ("CLK set %d/%d/%d/%d: rc arm 0x%08x bus 0x%08x gpu 0x%08x xbar 0x%08x (kept = not set, rc 0)", g_clock_target [0], g_clock_target [1],
		     g_clock_target [2], g_clock_target [3], (unsigned) ra, (unsigned) rb, (unsigned) rg, (unsigned) rx);
	}
	clocks_line ("start");
	k7_clk_start ();   /* kit #7: the CLK measured line (SHEPHERD_K7_CLK=0: none) */
}

/* kit #6 toggles: Settings > Graphics > CPU Speed (shepherd_vita_k6 op 2) -- the ARM clock the app asks for, 444 (the default, every build
 * since 2026-10-13) or 500 MHz. Only 444 is a stock clock: 500 takes effect only when a plugin on the device lets
 * scePowerSetArmClockFrequency(500) through, so the answer is the clock READ BACK right after the call, never the request (the engine
 * then decides: a 500 request that reads back anything else is "not available", and it asks for 444 again). Bus / GPU / crossbar stay
 * 222 / 222 / 166. One CLK line per call; the CLK end / exit lines and every P heartbeat line carry the clock as it is then. With
 * SHEPHERD_CLOCKS=0 the launch clocks are kept and nothing is set. */
static int k6_arm (int mhz)
{
	if (!g_clock_set) {
		LOG ("CLK k6 Settings > Graphics > CPU Speed %d MHz asked: not set (SHEPHERD_CLOCKS=0 keeps the launch clocks); arm %d MHz", mhz,
		     scePowerGetArmClockFrequency ());
		return scePowerGetArmClockFrequency ();
	}
	/* kit #7: "444" (the row's normal) is the app's normal clock -- a higher launch clock a plugin set (CLK keep line) is kept, not lowered */
	int ask = g_k7_clk && mhz <= 444 && g_clock_base_arm > mhz ? g_clock_base_arm : mhz;
	int before = scePowerGetArmClockFrequency ();
	int rc = scePowerSetArmClockFrequency (ask);
	int after = scePowerGetArmClockFrequency ();
	g_clock_target [0] = ask;
	LOG ("CLK k6 set arm %d MHz (Settings > Graphics > CPU Speed%s): rc 0x%08x; read back arm %d -> %d bus %d gpu %d xbar %d MHz%s", ask,
	     ask != mhz ? "; 444 asked = the kept launch clock" : "", (unsigned) rc,
	     before, after, scePowerGetBusClockFrequency (), scePowerGetGpuClockFrequency (), scePowerGetGpuXbarClockFrequency (),
	     after == ask ? "" : " (NOT TAKEN: the clock read back is not the one asked for)");
	if (g_k7_clk)
		k7_clk_note_set ();
	return after;
}

/* kit #6 toggles: the one export the engine's Settings rows use (Runtime/VitaToggles.cs; reached through a P/Invoke, GC-safe: kernel calls
 * and the log's lock only). op 0: state; 1: 4th CPU Core use (arg 0 / 1), returns the state after; 2: set the ARM clock to arg MHz, returns
 * the clock read back; 3: the ARM clock read back now (the readout row, about once a second; kit #7: a CLK readout line when it changes);
 * 4 (kit #7 P3, GL apps): the suspended time booked so far in ms (-1 = SHEPHERD_K7_SUSPEND=0 or no GL app). State bits: 1 = core pinning
 * is on in this app (the boot's C3 probe ran unless SHEPHERD_CORE3=0), 2 = CPU3 is open (probe passed), 4 = the workers are on CPU3 now, 8 = the host sets the clocks
 * (SHEPHERD_CLOCKS != 0); bits 16-27 = the ARM clock at launch. Never called with SHEPHERD_K6_TOGGLES=0 (the engine's switch). */
int shepherd_vita_k6 (int op, int arg)
{
	switch (op) {
	case 0:
	case 1:
		if (op == 1)
			k6_core3 (arg);
		return (pin_requested () ? 1 : 0) | (g_core3 > 0 ? 2 : 0) | (shepherd_vita_worker_mask () == SHP_CORE3_MASK ? 4 : 0) | (g_clock_set ? 8 : 0)
		       | ((g_clock_launch [0] > 0 ? g_clock_launch [0] & 0xfff : 0) << 16) | k9_state_bits ();   /* kit #9: bit 28 = 500 MHz by default (k7_host.inc.c) */
	case 2:
		return k6_arm (arg);
	case 3:
		return k8_clk_readout ();   /* kit #7: + a CLK readout line whenever it changes (SHEPHERD_K7_CLK=0: kit #6's plain read); kit #8: the MEASURED clock when it
		                             * settled on a Vita step (SHEPHERD_K8_CLK_MEASURED=0: the report, kit #7) */
#ifdef SHEPHERD_GL
	case 4:   /* kit #7 P3 (Runtime/Time.cs, the play clock, once a frame): the suspended time booked so far, ms; -1 = P3 off here */
		return g_k7_susp ? (int) k7_susp_book (1) : -1;
#endif
	}
	return -1;
}

static void exit_process (int code)
{
	g_host_exiting = 1;
	if (g_clock_set) {
		clocks_line ("exit (restoring the launch clocks)");
		scePowerSetArmClockFrequency (g_clock_launch [0]);
		scePowerSetBusClockFrequency (g_clock_launch [1]);
		scePowerSetGpuClockFrequency (g_clock_launch [2]);
		scePowerSetGpuXbarClockFrequency (g_clock_launch [3]);
	} else
		clocks_line ("exit");
	sceKernelExitProcess (code);
}

static void fail_and_wait (void)
{
	k10_bootscreen_stop ("a failed start (the host's fail path)", -1);   /* kit #10 review fix (finding 9): never the loading screen over a failure */
	k8_fail_screen (0);   /* kit #8: the clean boot screen's plain message (release apps; SHEPHERD_K8_BOOTSCREEN=0: nothing new); no START here */
	sceKernelDelayThread (15 * 1000 * 1000);
	exit_process (1);
}

#ifdef SHEPHERD_GL
/* ---------------------------------------------------------------- vitaGL (shepgl), after the arena */

/* this vitaGL/vitaShaRK build tries these two paths, in this order (vita/notes/gltest.md section 3) */
static int shacccg_present (char *where, size_t n)
{
	static const char *const paths [2] = { "ur0:data/external/libshacccg.suprx", "ur0:data/libshacccg.suprx" };
	for (int i = 0; i < 2; ++i) {
		unsigned sz = 0;
		if (file_size (paths [i], &sz) == 0 && sz > 0) {
			snprintf (where, n, "%s (%u bytes)", paths [i], sz);
			return 1;
		}
	}
	snprintf (where, n, "neither ur0:data/libshacccg.suprx nor ur0:data/external/libshacccg.suprx exists");
	return 0;
}

/* the whole display in one colour (vitaGL owns it once shepgl_init ran): green = PASS, red = FAIL; three swaps (triple buffering) */
static void gl_fill (float r, float g, float b)
{
	if (g_gl_state == 0 || !shepgl_attach ())
		return;
	glBindFramebuffer (GL_FRAMEBUFFER, shepgl_display_fbo ());
	glViewport (0, 0, 960, 544);
	glDisable (GL_SCISSOR_TEST);
	for (int i = 0; i < 3; ++i) {
		glClearColor (r, g, b, 1.0f);
		glClear (GL_COLOR_BUFFER_BIT);
		shepgl_swap ();
	}
}

/* The partition step that belongs to vitaGL (main has already allocated the Mono arena): the shader compiler must exist, the
 * debug screen gives its CDRAM framebuffer back, then vglInitExtended(0, 960, 544, ram_threshold = stacks + safety, MSAA off):
 * vitaGL's RAM pool = what is free beyond that threshold (the SHP_GL_RAM_MB the arena left, minus libshacccg's module and
 * vitaGL's GC thread, which load inside the init), its CDRAM and PHYCONT pools = all that is free. 1 = up, 0 = failed (logged). */
static int gl_start (void)
{
	char where [160];
	if (!shacccg_present (where, sizeof (where))) {
		LOG ("FAIL G0 libshacccg.suprx is missing: %s.", where);
		LOG ("FAIL G0 vitaGL compiles the engine's GLSL shader programs at run time with Sony's shader compiler (libshacccg.suprx). It is not in this VPK and must never be downloaded.");
#ifdef SHP_APP_TITLE
		LOG ("FAIL G0 Extract it from your own Vita: install PSM Runtime 1.00, update it to 2.00 and then 2.01, run ShaRKF00D; it writes ur0:data/libshacccg.suprx. Then start " SHP_APP_TITLE " again.");
#else
		LOG ("FAIL G0 Extract it from your own Vita: install PSM Runtime 1.00, update it to 2.00 and then 2.01, run ShaRKF00D; it writes ur0:data/libshacccg.suprx. Then start this app again.");
#endif
		return 0;
	}
	LOG ("G0 shader compiler present: %s", where);
	shepgl_config cfg;
	shepgl_default_config (&cfg);
	cfg.width = 960;
	cfg.height = 544;
	cfg.ram_threshold = (int) ((g_stack_reserve_mb + SHP_SAFETY_MB) * MiB);
	{
		/* kit #5 G7: the GXM parameter buffer (CDRAM, outside vitaGL's VRAM pool): unset / 0 = vitaGL's own 16 MiB (kit #4); 4..16 MiB =
		 * vglSetParamBufferSize before vglInit, the rest of the 16 MiB joins the VRAM pool. Too small costs GPU time (partial renders), never
		 * pixels: a device A/B (frame times at the menu and in Credits) decides; the G1 line names the size */
		unsigned gp = env_uint ("SHEPHERD_GXM_PARAM_MB", 0, 16, 0);
		if (gp && gp < 4) {
			LOG ("WARN SHEPHERD_GXM_PARAM_MB=%u ignored (4..16, or 0 = vitaGL's own 16 MiB)", gp);
			gp = 0;
		}
		cfg.param_buffer_kib = (int) gp * 1024;
	}
	const char *vs = getenv ("SHEPHERD_VSYNC");
	cfg.vsync = vs && !strcmp (vs, "0") ? 0 : 1;
	cfg.no_vgl_splash = g_k8_bootscreen;   /* kit #8: the clean boot screen leaves vitaGL's own splash out (shepgl_vita.c's wrap) */
	if (pin_requested ())
		cfg.gc_affinity = 0x40000;   /* SCE_KERNEL_CPU_MASK_USER_2: vitaGL's garbage collector off the main and render threads' cores */
	k9_video_reserve ();   /* kit #9 device track: the real video's decoder library before vitaGL takes every PHYCONT page (k8_boot.inc.c; launcher apps) */
	log_mem ("before-gl");
	LOG ("G1 vitaGL takes over the display now: from here on the log is only in " LOG_PATH " (the screen shows the game)");
	if (g_screen) {
		psvDebugScreenFinish ();   /* frees the debug screen's 2 MiB CDRAM framebuffer before vitaGL takes all free CDRAM */
		g_screen = 0;
	}
	SceUInt64 t = sceKernelGetProcessTimeWide ();
	g_gl_state = shepgl_init (&cfg);
	LOG ("G1 %s (%.0f ms)", shepgl_status (), (double) (sceKernelGetProcessTimeWide () - t) / 1000.0);
	k10_fp_after ("main thread after vglInit");   /* kit #10 fidelity: vitaGL's init reset nothing? (re-applied + logged; off = nothing) */
	k9_fp_line ("main thread after vglInit");   /* kit #9 second review: vitaGL (+ SceShaccCg) ran on this thread (k9_fp.inc.c) */
	log_mem ("after-gl");
	if (g_gl_state == 0) {
		/* shepgl_init has ended vitaGL's splash (the G1 line says so); the debug screen needs 2 MiB CDRAM, which vitaGL's VRAM
		 * pool may hold: then the log file is the only record */
		if (!g_k8_bootscreen)   /* kit #8 review fix: the clean boot screen shows its plain message instead (gl_fail_wait -> k8_fail_screen) */
			g_screen = psvDebugScreenInit () >= 0;
		LOG ("FAIL G1 vitaGL is not up (the G1 line has its state: context, shader patcher, render target, VRAM pool; clamped=N is not a failure; see the MEM lines: CDRAM/PHYCONT/RAM left after the arena); debug screen %s",
		     g_k8_bootscreen ? "not used (the clean boot screen: the plain message follows when the CDRAM allows)" : g_screen ? "back" : "could not start (vitaGL holds the CDRAM: log file only)");
		return 0;
	}
	k10_gl_after_init ();   /* kit #10 memory review fix R1: newlib split over two blocks (the shared pool / its fallback) -> GPU-map the other block (k10_vglmem.c); else nothing */
	gl_pools_line ("G1");
	if (g_k8_bootscreen) {
		int bf = shepgl_boot_frame ();
		LOG ("K8 boot screen: vitaGL's splash left out; %s (outside the frame count) -- the screen stays black until the engine's first frame (SHEPHERD_K8_BOOTSCREEN=0 = kit #7: the debug text, vitaGL's splash)",
		     bf ? "one black frame presented" : "no black frame (vitaGL is not up)");
	}
	if (cfg.gc_affinity)   /* k4-display: a PIN line per thread; vitaGL keeps its collector's thread id, so this one has no read-back */
		LOG ("PIN vitaGL-gc: created by vitaGL's init with cpu affinity 0x%x (vglSetupGarbageCollector before vglInit; no read-back: vitaGL keeps the thread's id)",
		     (unsigned) cfg.gc_affinity);
	if (g_gl_state < 0) {
		LOG ("FAIL G1 libshacccg.suprx is present but vitaGL could not load it (is_shark_online = 0): a wrong or damaged copy. Re-extract it with ShaRKF00D. (The screen is red.)");
		gl_fill (0.8f, 0.05f, 0.05f);
		return 0;
	}
	LOG ("G2 clocks arm %d bus %d gpu %d xbar %d MHz after vitaGL's init (set at start since 2026-10-13: see the CLK lines)",
	     scePowerGetArmClockFrequency (), scePowerGetBusClockFrequency (), scePowerGetGpuClockFrequency (), scePowerGetGpuXbarClockFrequency ());
	return 1;
}

/* a failure the owner must read: on the debug screen when it is up, else (vitaGL owns the display) the screen is red */
static void gl_fail_wait (void)
{
	LOG ("(press START to exit, or wait 60 s)");
	k8_fail_screen (1);   /* kit #8: before vitaGL has the display only (else kit #7's red screen); this loop reads START */
	SceCtrlData pad;
	for (int i = 0; i < 600; ++i) {
		memset (&pad, 0, sizeof (pad));
		sceCtrlPeekBufferPositive (0, &pad, 1);
		if (pad.buttons & SCE_CTRL_START)
			break;
		sceKernelDelayThread (100 * 1000);
	}
	exit_process (1);
}
#endif

/* the owner-copied folders must be there before the runtime starts (vita/shepherd/stage-owner.sh) */
static int check_layout (void)
{
	static const char *const need [] = {
		GAME_MANAGED "/icalls.json", GAME_MANAGED "/Assembly-CSharp.dll", GAME_MANAGED "/UnityEngine.CoreModule.dll",
		DIR_ "/pack/shepherd.pack", DIR_ "/data/RuntimeInitializeOnLoads.json",
		DIR_ "/data/StreamingAssets/aa/settings.json", DIR_ "/data/StreamingAssets/aa/catalog.json",
		VPK_MANAGED "/System.Private.CoreLib.dll", VPK_MANAGED "/Shepherd.Engine.dll",
	};
	int bad = 0;
	for (size_t i = 0; i < sizeof (need) / sizeof (need [0]); ++i) {
		unsigned sz;
		if (file_size (need [i], &sz) != 0) {
			LOG ("FAIL H0 missing %s", need [i]);
			bad++;
		}
	}
	if (bad)
		LOG ("FAIL H0 %d file(s) missing: copy the staged folder (vita/shepherd/stage-owner.sh output) to ux0:data/shepherd/ over FTP (see vita/notes/v6-vita-build.md)", bad);
	unsigned pk = 0;
	file_size (DIR_ "/pack/shepherd.pack", &pk);
	LOG ("H0 layout: pack %u KiB; pad.txt %s; persistent/ %s", pk / 1024, file_size (DIR_ "/pad.txt", NULL) == 0 ? "present" : "MISSING (no pad events: the boot stops at 'Press Any Button')",
	     file_size (DIR_ "/persistent/saves/settings.json", NULL) == 0 ? "has saves/settings.json" : "empty (the game creates defaults)");
	return bad == 0;
}

#ifdef SHEPHERD_JIT
#include "k8_jit_host.inc.c"   /* kit #8 track jit: the baseline JIT tier's host side (JIT apps only) */
#endif
int main (int argc, char *argv [])
{
	g_t0 = sceKernelGetProcessTimeWide ();
	sceIoMkdir ("ux0:data", 0777);
	sceIoMkdir (DIR_, 0777);
	/* keep the previous run's log (it may be the only record of a crash or hang) before starting a fresh one */
#ifdef SHP_DIAG
	/* the mode first (it picks the log file: menu-diag.txt or menu-diag-repro.txt, each with its .prev) */
	g_screen = psvDebugScreenInit () >= 0;
	dg_choose_mode_early ();
	{
		char prev [112];
		snprintf (prev, sizeof (prev), "%.*s.prev.txt", (int) (strlen (g_log_path) - 4), g_log_path);
		sceIoRemove (prev);
		sceIoRename (g_log_path, prev);
		SceUID fd = sceIoOpen (g_log_path, SCE_O_WRONLY | SCE_O_CREAT | SCE_O_TRUNC, 0777);
		if (fd >= 0)
			sceIoClose (fd);
	}
#else
	sceIoRemove (LOG_PREV_PATH);
	sceIoRename (LOG_PATH, LOG_PREV_PATH);
	{
		SceUID fd = sceIoOpen (LOG_PATH, SCE_O_WRONLY | SCE_O_CREAT | SCE_O_TRUNC, 0777);
		if (fd >= 0)
			sceIoClose (fd);
	}
	k8_bootscreen_decide ();   /* kit #8 boot track: the clean boot screen (release apps) is decided before a line could reach the screen */
	if (!g_k8_bootscreen && !k8_splash_holds_display ())
		g_screen = psvDebugScreenInit () >= 0;
#endif
	atexit (log_exit);
#if defined (SHP_APP_TITLE) && defined (SHEPHERD_GL)
	/* device kit #3's variants (build.sh: the play app SHPD00097 / its MT variant 00098, the measurement app 00099) say what they are */
	LOG ("BEGIN %s [%s] build %s (.NET 8.0.31 Mono fork: interpreter + static AOT incl. the engine and the derived render module, coop GC; GL "
	     "backend on vitaGL 960x544, GPU Stylizer, Vita controls; " SHP_APP_SOUND SHP_APP_MODE ")", SHP_APP_TITLE, SHP_APP_ID, SHP_BUILD_ID);
	sceCtrlSetSamplingMode (SCE_CTRL_MODE_ANALOG);   /* the left stick is the d-pad too (VitaPad) */
#elif defined (SHEPHERD_AUDIO)
	LOG ("BEGIN shepherd-menu-gl-audio M1 build %s (.NET 8.0.31 Mono fork: interpreter + static AOT, coop GC; GL backend on vitaGL 960x544, GPU Stylizer, Vita controls; SOUND: the audio bake through shepaudio on sceAudioOut)", SHP_BUILD_ID);
	sceCtrlSetSamplingMode (SCE_CTRL_MODE_ANALOG);
#elif defined (SHEPHERD_GL) && defined (SHP_MT_DEFAULT)
	LOG ("BEGIN shepherd-menu-gl M1 build %s (.NET 8.0.31 Mono fork: interpreter + static AOT, coop GC; GL backend on vitaGL 960x544, GPU Stylizer, Vita controls; silent; MT variant: the multicore render pipeline on by default, threads pinned)", SHP_BUILD_ID);
	sceCtrlSetSamplingMode (SCE_CTRL_MODE_ANALOG);   /* the left stick is the d-pad too (VitaPad) */
#elif defined (SHEPHERD_GL)
	LOG ("BEGIN shepherd-menu-gl M1 build %s (.NET 8.0.31 Mono fork: interpreter + static AOT, coop GC; GL backend on vitaGL 960x544, GPU Stylizer, Vita controls; silent)", SHP_BUILD_ID);
	sceCtrlSetSamplingMode (SCE_CTRL_MODE_ANALOG);   /* the left stick is the d-pad too (VitaPad) */
#elif defined (SHP_DIAG)
	LOG ("BEGIN shepherd-menu-diag (SHPD00016) build %s (.NET 8.0.31 Mono fork + the DIAGNOSTIC runtime hooks: interpreter + static AOT, coop GC; headless, null renderer)", SHP_BUILD_ID);
#else
	LOG ("BEGIN shepherd-menu V6 build %s (.NET 8.0.31 Mono fork: interpreter + static AOT, coop GC; headless, null renderer)", SHP_BUILD_ID);
#endif
	log_mem ("start");
	k8_launcher_lines ();   /* kit #8: a launcher build's own lines (K8 L ...); nothing in a kit #7-style eboot */

	apply_env_file ();
	k11_device_config ();   /* kit #11 device: the defaults table (the logging diet, DMA copies, the player build) -- env file lines win */
	k12_defaults_config (); /* kit #12 integration: the kit #12 defaults table (k12_defaults.inc.c; SHEPHERD_K12_DEFAULTS=0 = the c2 defaults) -- env file lines win */
	k13_defaults_config ();  /* kit #13 integration: the kit #13 defaults table (k13_defaults.inc.c: FloatPc + looks' rows; SHEPHERD_K13_DEFAULTS=0 = none) -- before any kit #13 row and k10_fp_config read their switches */
	k14_newlib_config ();   /* kit #14 newlib: the GC heap's cap's overhead (k14_newlib.inc.c), before the partition reads it */
	k12_boot_config ();     /* kit #12 boot: the boot sequence / plugin names switches (k12_boot.inc.c; SHEPHERD_K12_BOOT=0 = kit #11) */
	k7_config ();          /* kit #7: SHEPHERD_K7_CLK / _PROFILE / _SUSPEND (k7_host.inc.c) */
	k8_play_config ();     /* kit #8 play: SHEPHERD_K8_PLAY / _CLK_MEASURED (k8_play.inc.c) */
	k8_boot_config ();     /* kit #8 boot: SHEPHERD_K8_MODEL / ... (k8_boot.inc.c) -- kit #8 integration: the tracks' two k8_config () renamed */
	k10_fp_config ();      /* kit #10 fidelity: SHEPHERD_K10_FPSCR (k10_fp.inc.c; one line either way) */
	k10_fp_bench ();       /* kit #10 fidelity, test: SHEPHERD_K10_FP_BENCH=1 -- the denormal cost, FZ 1 vs FZ 0 (unset: nothing) */
	k10_fp_apply ("main thread at start", 1);   /* kit #10 fidelity: the rules on this thread + the self-test (off: nothing) */
	k9_fp_line ("main thread at start");   /* kit #9 fidelity: the Vita's default FPSCR (k9_fp.inc.c; SHEPHERD_K9_FP=0: no line) */
	stacks_config ();
#ifdef SHEPHERD_GL
	phys_config ();        /* kit #8: the physics level, SHEPHERD_PHYS_HOST, the pool block's size (phys_host.inc.c) */
#endif
	clocks_start ();
#ifdef SHP_DIAG
	dg_apply_mode ();
	dg_start_heartbeat ();
#endif
	{
		void *data;
		unsigned size;
		if (read_file (DIR_ "/" SHP_ARGS_NAME, &data, &size) == 0) {
			snprintf (g_args_buf, sizeof (g_args_buf), "%s", (char *) data);
			free (data);
			g_args_from_file = 1;
			LOG ("H1 engine arguments from " SHP_ARGS_NAME ": %s", g_args_buf);
		}
	}
	/* the engine creates these itself too; pre-creating them keeps Directory.CreateDirectory off the device roots */
	static const char *const dirs [] = { DIR_ "/runtime", DIR_ "/runtime/logs", DIR_ "/runtime/shots", DIR_ "/runtime/shots/boot",
		DIR_ "/runtime/cache", DIR_ "/persistent", DIR_ "/persistent/saves",
#ifdef SHEPHERD_GL
		DIR_ "/shots",
#endif
	};
	for (size_t i = 0; i < sizeof (dirs) / sizeof (dirs [0]); ++i)
		sceIoMkdir (dirs [i], 0777);
	logs_dir_init ();
	if (g_logs_dir [0]) {
		const char *ld = logs_dir ();
		if (strcmp (ld, g_logs_dir))
			LOG ("H1 this app's engine logs (engine.log, simstats, native-resolve.txt, assemblies.txt, missing-icalls.txt): %s/ -- moved there by "
			     SHP_ENV_NAME "'s SHEPHERD_LOG_DIR (this app's own folder %s/ is left as it is)", ld, g_logs_dir);
		else
			LOG ("H1 this app's engine logs (engine.log, simstats, native-resolve.txt, assemblies.txt, missing-icalls.txt): %s/", g_logs_dir);
	}
	k7_init ();   /* kit #7: the black box's rings (the previous start's files kept as .prev), the profiler block (SHEPHERD_K7=0: none) */
	if (!check_layout ()) {
		fail_and_wait ();
		return 1;
	}
#ifdef SHEPHERD_AUDIO
	{
		unsigned msz = 0;
		g_audio_bake = file_size (SHP_AUDIO_BAKE "/manifest.json", &msz) == 0 && msz > 0;
		g_audio_pool_mb = SHP_AUDIO_POOL_MB;
		const char *pe = getenv ("SHEPHERD_AUDIO_POOL_MB");
		/* >= 3 (k4-gameaot review): 3 MiB = the 1 MiB of stream rings + a 1 MiB take cache + 1 MiB slack -- 1 MiB more GC heap for the
		 * game-AOT app (Credits from a fresh start needs 119 MiB, its default cap is 118); the menu tour's take cache peaks at 1.06 MiB,
		 * Credits' and New Game's at ~0.5 MiB (vlx: pool peak 1.5 of 4 MiB), so only the tour may re-load an evicted take (a sound starting
		 * a card read later). Opt-in per app in its env file; the default stays the build's (play apps 4). */
		if (pe && atoi (pe) >= 3 && atoi (pe) <= 64)
			g_audio_pool_mb = (unsigned) atoi (pe);
		if (g_audio_bake)
			LOG ("A0 audio bake present: " SHP_AUDIO_BAKE "/manifest.json (%u bytes); the engine plays it through shepaudio on sceAudioOut, pool %u MiB", msz, g_audio_pool_mb);
		else
			LOG ("A0 no audio bake (" SHP_AUDIO_BAKE "/manifest.json is missing): the app runs SILENT, like Shepherd Menu (GL). Copy the staged audiobake/menu folder to " SHP_AUDIO_BAKE " for sound");
	}
#endif
	engine_env ();
	k8_engine_env ();   /* kit #8 boot track: SHEPHERD_K8_VIDEO's default (launcher builds), the K8 switches line */
	if (g_rebase && !g_args_from_file && !strcmp (g_args_buf, "boot 1460 0")) {   /* kit #6: a default run's 1460 frames move with the frame-clock re-base (no args file) */
		snprintf (g_args_buf, sizeof (g_args_buf), "boot %u 0", 1460u + g_rebase);
		LOG ("H1 engine arguments (the default, re-based): %s", g_args_buf);
	}
	{
		/* kit3 play mode: which clock drives the game (the engine logs the same choice as "[time] clock ...") */
		const char *tm = getenv ("SHEPHERD_TIME");
		int wall = tm && !strcmp (tm, "wall");
		LOG ("T0 game clock SHEPHERD_TIME=%s (%s): %s", tm ? tm : "?", tm && strcmp (tm, SHP_TIME_DEFAULT) ? "set in " SHP_ENV_NAME : "this app's default",
		     wall ? "PLAY -- game time = real time like Unity (deltaTime = real frame time, at most 1/3 s), physics 50 Hz, music and sounds at "
			    "their own real-time pace; pad.txt is not applied (your controls only; SHEPHERD_PAD_FILE=" ROOTED "/pad.txt in "
			    SHP_ENV_NAME " scripts it, m+N frames = relative to the Main Menu); SHEPHERD_TIME=frame for the deterministic test clock"
			  : "TEST -- every frame is 1/60 s of game time (deterministic; at a low frame rate the game runs slower than real time while "
			    "the sound keeps real time); SHEPHERD_TIME=wall in " SHP_ENV_NAME " plays in real time");
	}
	{
		const char *mt = getenv ("SHEPHERD_MT_RENDER");
#ifdef SHEPHERD_GL
		const char *pins = ": main -> USER_0, shepherd-render -> USER_1, shepaudio out + IO and vitaGL's garbage collector -> USER_2, pack read-ahead "
				   "+ power/watchdog -> CPU3 if the C3 probe passes, else USER_2 (a PIN line per thread)";
#else
		const char *pins = ": main -> USER_0, shepherd-render -> USER_1, pack read-ahead + power/watchdog -> CPU3 if the C3 probe passes, else USER_2 "
				   "(a PIN line per thread)";
#endif
		LOG ("H1 multicore render pipeline %s (SHEPHERD_MT_RENDER=%s); core pinning %s (SHEPHERD_VITA_PIN=%s)%s", mt && !strcmp (mt, "1") ? "ON" : "off",
		     mt ? mt : "unset", pin_requested () ? "ON" : "off", getenv ("SHEPHERD_VITA_PIN") ? getenv ("SHEPHERD_VITA_PIN") : "unset",
		     pin_requested () ? pins : "");
		tai_plugins_note ();   /* k4 review: the owner's plugins by name (TAI lines; the core-unlock one decides the C3 line) */
		if (pin_requested ())
			core3_probe ();   /* k4-display: the 4th core for the latency-tolerant workers (C3 line) */
	}
	k7_profile ();   /* kit #7: ux0:data/shepherd/device-profile.txt + the DP line (SHEPHERD_K7_PROFILE=0: none) */
#ifdef SHP_TEST_CLOCK
	k7_probes_start ();   /* kit #7: memory budget + big pages, before the partition (every block freed again; SHEPHERD_K7_PROBES=0: none) */
#endif
#ifdef SHP_PLAY_APP
	{
		const char *mg = getenv ("SHEPHERD_MEM_GUARD");
		g_mem_guard = !(mg && !strcmp (mg, "0"));
		LOG ("H1 memory guard %s (SHEPHERD_MEM_GUARD=%s): %s", g_mem_guard ? "ON" : "off", mg ? mg : "unset",
		     g_mem_guard ? "the session ends cleanly (FAIL MEM, red END) when the GC heap meets its cap after the Main Menu is up, on an "
				   "OutOfMemoryException, or when the newlib heap is about to run out" : "a full heap ends as it may (vlx margin runs)");
	}
#endif

	/* kit #8 (a launcher build): the splash ends and the launcher gives its memory back BEFORE the partition, so this host sizes exactly the
	 * memory kit #7's host saw (only the launcher's ~0.1 MiB eboot more); then the boot numbers; kit #7's debug text (SHEPHERD_K8_BOOTSCREEN=0)
	 * can start only now */
	k8_release_launcher ("before the partition");
	k8_boot_line ();
	if (g_k8_launch && !g_k8_bootscreen && !g_screen)
		g_screen = psvDebugScreenInit () >= 0;

#ifdef SHEPHERD_JIT
	k8_jit_setup ();   /* kit #8: the code block comes out of user memory BEFORE the partition reads it */
#endif
	/* ---- the partition: everything not reserved goes to the Mono arena */
	k10_mem_config ();   /* kit #10 memory: the K10 MEM line (the shared pool was decided before main: k10_newlib.inc.c) */
	unsigned k10_extra = k10_partition_prepare ();   /* kit #10: in the shared pool the IL pool and newlib's share join the arena's block; 0 = kit #9 */
	if (k10m.fatal) {   /* kit #10 memory review fix R5: newlib has only its 2 MiB boot block (the K10 POOL FAIL line above): Mono's init needs ~6 MiB */
		fail_and_wait ();
		return 1;
	}
k10_repartition:;
	SceKernelFreeMemorySizeInfo fi;
	memset (&fi, 0, sizeof (fi));
	fi.size = sizeof (fi);
	sceKernelGetFreeMemorySize (&fi);
	unsigned free_mb = fi.size_user / MiB;
	unsigned reserve_mb = g_stack_reserve_mb + SHP_SAFETY_MB + g_il_pool_mb;
#ifdef SHEPHERD_GL
	/* GL partition (vita/notes/m1-gl-app.md section 4): free user = arena + vitaGL's RAM pool + stacks + safety. The arena is
	 * allocated first; vglInitExtended then runs with ram_threshold = stacks + safety, so its RAM pool can only be what the arena
	 * left beyond that (SHP_GL_RAM_MB, minus libshacccg's module, which loads inside the init). vitaGL's CPU-written data (uniform
	 * and per-frame circular pools, USSE) is RAM-first and overflows to PHYCONT then CDRAM, never into the arena; textures and
	 * render targets are CDRAM-first. vitaGL's own small heap (vglMalloc: shader sources, compiler work) is newlib-first. */
	unsigned gl_ram_mb = SHP_GL_RAM_MB;
	const char *ge = getenv ("SHEPHERD_GL_RAM_MB");
	if (ge && *ge && atoi (ge) >= 0)
		gl_ram_mb = (unsigned) atoi (ge);
	reserve_mb += gl_ram_mb;
#endif
#ifdef SHEPHERD_AUDIO
	if (g_audio_bake)
		reserve_mb += g_audio_pool_mb;   /* the audio pool comes out of the arena's share (allocated right after it, before vitaGL) */
#endif
#ifdef SHEPHERD_GL
	reserve_mb += phys_reserve_mb ();   /* kit #8: the physics pool block too (level >= 1 in a build that links libshepphys; else 0) */
#endif
	unsigned arena_mb = free_mb > reserve_mb ? free_mb - reserve_mb : 0;
	if (arena_mb > SHP_ARENA_MAX_MB + k10_extra)   /* (kit #10: in the shared pool the sanity cap is the arena's + newlib's share + the IL pool's) */
		arena_mb = SHP_ARENA_MAX_MB + k10_extra;
	const char *ae = getenv ("SHEPHERD_ARENA_MB");
	if (ae && atoi (ae) > 0)
		arena_mb = (unsigned) atoi (ae) + k10_extra;   /* (kit #10: SHEPHERD_ARENA_MB is the arena's share; the pool adds newlib's) */
	unsigned overhead = SHP_CARD_TABLE_MB + SHP_INTERP_STACKS_MB + SHP_ARENA_SLACK_MB;
	g_max_heap_mb = arena_mb > overhead + 16 ? arena_mb - overhead : 16;
	g_arena_bytes = arena_mb * MiB;
	if (k10_extra)   /* kit #10 memory: the shared pool's own H2 line (the kit #9 lines below otherwise, unchanged) */
		LOG ("H2 partition (kit #10 shared pool): free user %u MiB after the eboot and newlib's %u KiB boot block -> ONE block of %u MiB = the arena's "
		     "share %u + newlib's %u + the IL pool's %u MiB (kit #9's split; keeping %u MiB for thread stacks + %u MiB safety and the rest as kit #9: "
		     "vitaGL's RAM pool, the audio / physics pools); max-heap-size follows newlib's floor (k10_mem.inc.c)%s",
		     free_mb, SHP_K10_BOOT_KB, arena_mb, arena_mb - k10_extra, k10_extra - k10m.il_kit9_mb, k10m.il_kit9_mb, g_stack_reserve_mb, SHP_SAFETY_MB,
		     ae ? " (SHEPHERD_ARENA_MB = the arena's share)" : "");
	else {
#ifdef SHEPHERD_GL
	LOG ("H2 partition: free user %u MiB after the eboot and the %u MiB newlib heap -> arena %u MiB (keeping %u MiB for vitaGL's RAM pool + %u MiB for thread stacks + %u MiB safety%s); max-heap-size %u MiB = arena - card table %u - interpreter stacks %u - slack %u%s",
	     free_mb, SHP_NEWLIB_HEAP_MB, arena_mb, gl_ram_mb, g_stack_reserve_mb, SHP_SAFETY_MB, il_pool_h2 (), g_max_heap_mb, SHP_CARD_TABLE_MB, SHP_INTERP_STACKS_MB, SHP_ARENA_SLACK_MB,
	     ae ? " (SHEPHERD_ARENA_MB)" : "");
#else
	LOG ("H2 partition: free user %u MiB after the eboot and the %u MiB newlib heap -> arena %u MiB (keeping %u MiB for thread stacks + %u MiB safety%s); max-heap-size %u MiB = arena - card table %u - interpreter stacks %u - slack %u%s",
	     free_mb, SHP_NEWLIB_HEAP_MB, arena_mb, g_stack_reserve_mb, SHP_SAFETY_MB, il_pool_h2 (), g_max_heap_mb, SHP_CARD_TABLE_MB, SHP_INTERP_STACKS_MB, SHP_ARENA_SLACK_MB,
	     ae ? " (SHEPHERD_ARENA_MB)" : "");
#endif
	}
	SceUID arena = arena_mb ? sceKernelAllocMemBlock ("shepherd-mono-arena", SCE_KERNEL_MEMBLOCK_TYPE_USER_RW, g_arena_bytes, NULL) : -1;
	void *arena_base = NULL;
	/* kit #10 memory, the shared pool: newlib's break must grow upward -- a block below its boot block cannot take it: given back, newlib gets
	 * its kit #9 share in a second block, and the partition runs again as kit #9's (the K10 POOL line says so) */
	if (k10_extra && arena >= 0 && sceKernelGetMemBlockBase (arena, &arena_base) >= 0 && k10_pool_order_ok (arena_base) != 0) {
		sceKernelFreeMemBlock (arena);
		k10m.pool = 0;
		k10m.pool_why = "the pool's block lay below newlib's boot block (newlib's break grows upward only)";
		g_il_pool_mb = k10m.il_kit9_mb;
		k10_extra = 0;
		int fb = k10_newlib_fallback ();
		LOG ("%s K10 POOL not used: %s -- newlib %s", fb ? "FAIL" : "WARN", k10m.pool_why, fb ? "has only its boot block: set SHEPHERD_K10_POOL=0" : "gets its kit #9 share in a second block; the partition is kit #9's");
		if (fb) {   /* review fix R5: a clean FAIL screen, not g_malloc's abort in Mono's init (it needs ~6 MiB of newlib) */
			fail_and_wait ();
			return 1;
		}
		goto k10_repartition;
	}
	if (arena < 0 || sceKernelGetMemBlockBase (arena, &arena_base) < 0 || mono_vita_vm_arena_init (arena_base, g_arena_bytes) != 0) {
		LOG ("FAIL H2 cannot allocate the %u MiB Mono arena (0x%08x): set a smaller SHEPHERD_ARENA_MB in env.txt", arena_mb, (unsigned) arena);
		fail_and_wait ();
		return 1;
	}
	mono_vita_vm_arena_set_log (arena_log, 16u * MiB);
	mono_vita_set_heap_limit_callback (heap_limit_cb);
	k9_gc_config ();   /* kit #9 gc: the GC switches; M8's arena policy before the runtime takes any arena memory */
	if (k10_extra && k10_pool_start (g_arena_bytes) != 0) {   /* kit #10: policy 3 (top-down above the floor) and newlib moves into the block */
		LOG ("FAIL K10 POOL newlib could not take its first pages in the pool: set SHEPHERD_K10_POOL=0 in " SHP_ENV_NAME);
		fail_and_wait ();
		return 1;
	}
	LOG ("H2 Mono arena: %u MiB USER_RW at %p%s", arena_mb, arena_base, k10_extra ? " (kit #10: the shared pool -- newlib from its low end, the arena from its high end)" : "");
	if (g_il_pool_mb) {
		void *ip = NULL;
#ifdef SHEPHERD_MAC_CHECK
		ip = malloc ((size_t) g_il_pool_mb * MiB);   /* the Mac compat layer has no real memblocks */
#else
		SceUID ib = sceKernelAllocMemBlock ("shepherd-il-pool", SCE_KERNEL_MEMBLOCK_TYPE_USER_RW, g_il_pool_mb * MiB, NULL);
		if (ib < 0 || sceKernelGetMemBlockBase (ib, &ip) < 0)
			ip = NULL;
#endif
		if (ip) {
			g_il_pool = (char *) ip;
			g_il_pool_size = g_il_pool_mb * MiB;
			LOG ("H2 IL pool: %u MiB USER_RW at %p (IL images read here first, then the newlib heap: newlib headroom; SHEPHERD_IL_POOL_MB=0 = none, kit #4)", g_il_pool_mb, ip);
		} else
			LOG ("WARN H2 cannot allocate the %u MiB IL pool: the IL images go to the newlib heap (kit #4)", g_il_pool_mb);
	} else if (k10_il_vram_alloc (&g_il_pool, &g_il_pool_size) == 0) {
		/* kit #10 memory (STRETCH): the IL pool in video memory (k10_mem.inc.c; SHEPHERD_K10_IL_VRAM_MB) */
	}
	k13_walkc_line ();   /* kit #13 walkc: the K13 WALKC line (the IL pool's video-memory type) */
	log_mem ("arena");
#ifdef SHEPHERD_AUDIO
	if (g_audio_bake) {
		void *pool = NULL;
		int lk = 0;
#ifdef SHEPHERD_MAC_CHECK
		pool = malloc ((size_t) g_audio_pool_mb * MiB);   /* the Mac compat layer has no real memblocks */
#else
		SceUID pb = sceKernelAllocMemBlock ("shepherd-audio-pool", SCE_KERNEL_MEMBLOCK_TYPE_USER_RW, g_audio_pool_mb * MiB, NULL);
		if (pb < 0 || sceKernelGetMemBlockBase (pb, &pool) < 0)
			pool = NULL;
		lk = sceKernelCreateLwMutex (&g_apool_mu, "shepherd-audio-pool", 0, 0, NULL);
#endif
		if (!pool || lk < 0 || ap_init (&g_apool, pool, (size_t) g_audio_pool_mb * MiB, apool_lock, apool_unlock, &g_apool_mu) != 0) {
			LOG ("FAIL A1 cannot allocate the %u MiB audio pool (lock %d): set SHEPHERD_AUDIO_POOL_MB lower in " SHP_ENV_NAME, g_audio_pool_mb, lk);
			fail_and_wait ();
			return 1;
		}
		LOG ("A1 audio pool: %u MiB USER_RW at %p (shepaudio's take cache + stream rings; tables and decoder buffers are newlib)", g_audio_pool_mb, pool);
		log_mem ("audio-pool");
	}
#endif
#ifdef SHEPHERD_GL
	phys_block_alloc ();   /* kit #8: the physics pool block (phys_host.inc.c; nothing at level 0 / none or without libshepphys) */
#endif
#ifdef SHEPHERD_GL
	if (!gl_start ()) {
		gl_fail_wait ();
		return 1;
	}
	k10_bootscreen_start ();   /* kit #10 boot track: the game's own loading screen until its first content (k10_boot.inc.c; SHEPHERD_K10_BOOTSCREEN=0: kit #9) */
#ifdef SHEPHERD_GL
	k12_deccheck ();          /* kit #12 boot: the decoder checks' device side (SHEPHERD_K12_DECCHECK=video, test-only; off: nothing) -- before the Mono thread */
#endif
#endif
	/* kit #10 integration review (finding: the IL bundle x FloatPc): SHEPHERD_K9_FLOATPC -- and the strict original switch, which asks for it since
	 * kit #10 -- is decided HERE, before the bundle's reader starts, not in the Mono thread: k10_ilb_start turns the bundle off when another
	 * DLL folder is named (g_k9_fpc_dir), and preload_hook asks the bundle first, so a bundle started without knowing served managed/'s
	 * ORIGINAL DLLs in place of the FloatPc rewrites (a verified .ok on the card = USE mode). k9_floatpc_config needs no Mono (getenv,
	 * sceIoGetstat, LOG, the static shepherd_aot_bound_names table); H7 reads the same g_k9_fpc_dir later, as before */
	k9_floatpc_config ();
	k10_ilb_start ();   /* kit #10 boot track: the game's DLLs in one stream (k10_ilb.inc.c; SHEPHERD_K10_ILBUNDLE=0: kit #9) */

	pthread_t pt;
	pthread_attr_t pa;
	pthread_attr_init (&pa);
	pthread_attr_setstacksize (&pa, 64 * 1024);
	g_power_pin = pin_requested ();   /* kit #9 gc: read here, before the thread exists (power_thread) */
	k10a_init ();   /* device kit #10 aot: its env switches read here, before the power thread exists (no getenv there: kit #9 gc's race) */
	pthread_create (&pt, &pa, power_thread, NULL);
	pthread_attr_destroy (&pa);

	g_done_sema = sceKernelCreateSema ("shepherd-mono-done", 0, 0, 1, NULL);
	k9_fast_tls_survey ();   /* kit #9 runtime review fix: the main thread's TLS slots 0x8a-0x99 already in use are kept from the runtime */
	pthread_t th;
	int created = 0;
	unsigned stack_floor = g_mono_stack_req < 4u * MiB ? g_mono_stack_req : 4u * MiB;   /* kit #4: 8 then 4 MiB; a smaller request: that size only */
	for (unsigned stack = g_mono_stack_req; stack >= stack_floor && !created; stack /= 2) {
		pthread_attr_t attr;
		pthread_attr_init (&attr);
		pthread_attr_setstacksize (&attr, stack);
		g_mono_stack = stack;
		int rc = pthread_create (&th, &attr, mono_thread, NULL);
		pthread_attr_destroy (&attr);
		if (rc == 0)
			created = 1;
		else
			LOG ("H3 pthread_create with a %u KiB stack failed (%d)", stack / 1024, rc);
	}
	if (!created) {
		LOG ("FAIL H3 cannot create the Mono thread");
		fail_and_wait ();
		return 1;
	}
	sceKernelWaitSema (g_done_sema, 1, NULL);
	k10_bootscreen_stop ("the end of the run (the host; the engine never said)", -1);   /* kit #10: a boot screen still up gives the display back before the end screen */
	k10_ilb_end ();   /* kit #10: the IL bundle's line (VERIFY: the .ok) */
	clocks_line ("end");   /* the clocks the whole run had (444 unless SHEPHERD_CLOCKS=0 or the set failed) */
	if (g_k7_clk)
		k7_clk_measured_line ("at the end", scePowerGetArmClockFrequency ());   /* kit #7 */

#ifdef SHEPHERD_JIT
	k8_jit_end ();         /* kit #8: the J2 line + the trace tables */
#endif
	gcgov_end_line ();     /* M2: how many GC.Collect calls the governor turned into nursery collections */
	k9_gc_end_line ();     /* kit #9 gc: pauses by kind, the worst pause, the GC heap's peak, the arena's ends, the used-size counts */
	k10_mem_end_line ();   /* kit #10 memory: the shared pool, the holes (test), the ladder, the size-class census (test) */
	k12_walka_end_line ();   /* kit #12 walka: objects marked in place after a refused evacuation copy */
	k12_walkb_end ();      /* kit #12 walkb: the VPK's compressed IL, the object-header census (test) */
	k13_short_line ();     /* kit #13 walkc: the "MiB short" meter (the GC side's peak against the device partitions' room) */
	io_readahead_line ();  /* B-3: the shim's read-ahead for small sequential reads */

	/* the final line: the selected menu button, and the verdict */
	int fails = g_fail_count;
#ifdef SHEPHERD_GL
	touch_end_line ();     /* kit #5: the front touch panel's session */
	k8_end_line ();        /* kit #8: the rear panel's session (nothing when it was never read) */
	int gl_ok = 0;
	if (g_end_frame < 0)
		LOG ("FAIL END no end checkpoint from the engine (Main returned %d before the boot loop ended, or the boot never started)", g_exit_code);
	else {
		gl_ok = g_exit_code == 0 && fails == 0 && g_oom_count == 0 && g_interactive > 0 && shepgl_swap_count () > 0;
		LOG ("%s END frame %d, %.1f s, exit %d, %d FAIL line(s), %d OOM, %u heap-cap refusal(s), %u frames shown, %d menu response(s) to the pad%s, %u short press(es) latched%s | menu %s",
		     gl_ok ? "PASS" : "FAIL", g_end_frame, wall_s (), g_exit_code, fails, g_oom_count, mono_vita_heap_limit_count (), shepgl_swap_count (), g_interactive,
		     g_interactive ? "" : " (none seen: no PASS M1 line)", g_latch.taps, g_host_quit ? "; ENDED BY THE MEMORY GUARD (the FAIL MEM line above)" : "", g_end_note);
	}
	if (!gl_ok)
		k7_bb_event ("FAIL END (the END line above)");   /* kit #7: the black box */
	if (g_gl_unsafe)
		LOG ("END no vitaGL call after Main (the engine's render thread did not stop: the WARN MT line above): no pool line, the screen stays as it is");
	else
		gl_pools_line ("END");
#ifdef SHEPHERD_AUDIO
	if (g_audio_bake)
		LOG ("END audio pool: %u of %u KiB in use, peak %u KiB, %u allocations / %u frees, %u failed (the [audio] end lines -- mixer, cache, limiter, instances -- are above and in %s/engine.log)",
		     g_apool.used / 1024, g_apool.size / 1024, g_apool.peak / 1024, g_apool.allocs, g_apool.frees, g_apool.failures, logs_dir ());
#endif
#ifdef SHEPHERD_GL
	phys_end_line ();   /* kit #8: the physics pool block's use (level >= 1 with libshepphys linked) */
	k8_late_end_line ();   /* kit #8.1: the engine module's late libraries (launcher builds only) */
#endif
#ifdef SHP_TEST_CLOCK
	k7_probes_end (!g_gl_unsafe && g_gl_state > 0);   /* kit #7: storage, NGS, GPU transfer -- after the verdict (SHEPHERD_K7_PROBES=0: none) */
#endif
	LOG ("(the screen is %s for 5 s, then the app exits; send back " LOG_PATH ", " LOG_PREV_PATH " and the folder %s/)", g_gl_unsafe ? "left as it is" : gl_ok ? "green" : "red", logs_dir ());
	if (!g_gl_unsafe)
		gl_fill (gl_ok ? 0.05f : 0.8f, gl_ok ? 0.6f : 0.05f, 0.05f);
	sceKernelDelayThread (5 * 1000 * 1000);
	exit_process (g_exit_code < 0 ? 2 : g_exit_code);
	return 0;
#else
	if (g_end_frame < 0) {
		LOG ("FAIL END no end checkpoint from the engine (Main returned %d before the last frame, or the boot never started)", g_exit_code);
		k7_bb_event ("FAIL END (no end checkpoint)");   /* kit #7 */
	} else {
		const char *sel = strstr (g_end_note, "label='");
		int has_sel = sel && sel [7] != '\'';
		int ok = g_exit_code == 0 && fails == 0 && g_oom_count == 0 && has_sel;
		LOG ("%s END frame %d, %.1f s, exit %d, %d FAIL line(s), %d OOM, %u heap-cap refusal(s) | menu %s", ok ? "PASS" : "FAIL", g_end_frame, wall_s (), g_exit_code, fails,
		     g_oom_count, mono_vita_heap_limit_count (), g_end_note);
		if (!ok)
			k7_bb_event ("FAIL END (the END line above)");   /* kit #7: the black box */
	}
#ifdef SHP_DIAG
	LOG ("(press START to exit, or wait 60 s; send back every ux0:data/shepherd/menu-diag*.txt and the folder %s/)", logs_dir ());
#else
	LOG ("(press START to exit, or wait 60 s; send back " LOG_PATH " and the folder %s/)", logs_dir ());
#endif
#endif
	SceCtrlData pad;
	for (int i = 0; i < 600; ++i) {
		memset (&pad, 0, sizeof (pad));
		sceCtrlPeekBufferPositive (0, &pad, 1);
		if (pad.buttons & SCE_CTRL_START)
			break;
		sceKernelDelayThread (100 * 1000);
	}
	exit_process (g_exit_code < 0 ? 2 : g_exit_code);
	return 0;
}
