#!/bin/zsh
# SHEPHERD-VITA V6 Stage B: builds vita/shepherd/out/shepherd_menu.vpk -- Shepherd's headless menu boot for the PS Vita.
#   engine IL (dotnet build) + the linux-arm 8.0.31 BCL -> static full+interp AOT of CoreLib + a BCL subset + the engine's
#   own Shepherd.Assets/Hooks (mono-aot-cross, armv7-vita-eabihf) -> GUID gate -> the native shim (System.Native + crypto +
#   network refusal + zlib + generated stubs) -> link with libmonosgen-2.0.a (HOST_VITA) + host.c -> velf -> fself -> VPK.
# The VPK holds the engine + runtime + BCL ONLY. Game DLLs, rewritten Unity modules (runtime/ManagedVita) and the pack are
# derived/owner assets: never in the VPK, never AOT-compiled; the eboot loads them from ux0:data/shepherd/ (stage-owner.sh).
# Prerequisites (vita/notes/v5-runtime.md, v6-vita-build.md): build volume attached, runtime fork configured + built
# (vita/toolchain/build-runtime-vita.sh), mono-aot-cross built (vita/toolchain/build-cross.sh). Nothing is downloaded.
# env: SHEPHERD_NEWLIB_MB (newlib heap, MiB; default 128, engine-AOT builds 116), MONO_AGGRESSIVE_INLINELIMIT (default 256), SHEPHERD_V6_WORK, SHEPHERD_V6_OUT
# Every VPK (every recipe below) carries the LiveArea set (vita/probe's template.xml + 8-bit indexed bg/startup PNGs) and passes the
#   VPK gate (vita/hello/diag/check_diag_vpk.py: LiveArea set, 8-bit PNGs, TITLE, eboot.bin == this build, managed/ == work/bcl).
# Device status of the opt-in builds (2026-10-13, vita/notes/llvm-aot.md §14):
#   - SHEPHERD_AOT_ENGINE=1 (SHPD00011/09/90/91): the vlx crash (SIGSEGV in compute_class_bitmap 2 s after 'H10 entering Main') is
#     fixed in the engine (Diag/NativeCensus.cs: its AssemblyLoad hook re-entered its own AOT init while Mono was still setting up a
#     class's fields). SHPD00011 and SHPD00090 now reach PASS END frame 1460 under vlx with the interpreter's draw stream, frame for frame.
#     Kit 3 (2026-10-13; ${SHEPHERD_WORK:-$HOME/shepherd-work}/kit3/gl-aot.md): the GL ones too -- SHPD00009 / 00091 / 00096 under vlx with a recording
#     GL stand-in (vita/vlx/glrec_vlx.c) hand the GPU exactly the interpreted SHPD00008's GL calls and bytes, frame for frame (boot +
#     menu tour; 00096 with the render thread submitting); their newlib heap is 116 MiB (see NEWLIB_MB below).
#   - SHEPHERD_AOT_LLVM (any): the SIGBUS (an LDRD that llc paired at a 2-byte-aligned address in CoreLib) is fixed by the llc flags
#     below, and the LLVM menu passes vlx frame-identical. The float-argument ABI mismatch (the Mono LLVM 16 tools' Mono calling
#     convention passes float / double arguments of managed calls in core registers on ARM; Mono's armhf ABI -- its own backend, the
#     interpreter's entry trampolines, gsharedvt -- in VFP registers) is fixed in the compiler by kit #5's vita/llvm/fork-llvm-armhf-cc.patch
#     (vita/notes/kit5-llvm.md: float-argument signatures use LLVM's C convention = AAPCS-VFP, their rgctx / IMT argument goes in r8 as a
#     swifterror argument). Without that compiler (or with SHEPHERD_LLVM_ARMHF_CC=0) every LLVM build writes $OUT/NOT-FOR-DEVICE.txt;
#     with it, only the LLVM builds that take a default app's TITLE_ID do: the device A/B apps are SHPD00191 (the 00099 recipe + LLVM)
#     and SHPD00192 (the 00098 recipe + LLVM), EXTRA owner-only apps with their own log names.
#     Kit #5 review: an exception caught above an LLVM frame handed that frame back corrupted callee-saved d8-d15 (three stock ARM
#     bugs no Mono-backend code exposes, vita/llvm/fork-eh-vfp.patch). The A/B apps are device apps only with that fix too: fix A in
#     the compiler (build-cross-llvm-armhf.sh applies both patches) and fixes B + C in the runtime they link (SHEPHERD_VITA_RUNTIME_OBJ =
#     vita/llvm/build-runtime-eh-vfp.sh's output: the shared runtime with 2 members rebuilt). Without either (or with
#     SHEPHERD_EH_VFP_FIX=0) every LLVM build writes NOT-FOR-DEVICE.txt. The default (non-LLVM) apps never link that runtime.
# SHEPHERD_AOT_ENGINE=1 (V6 stage 2, vita/notes/engine-pass-speed.md; OPT-IN, owner-local): also AOT-compile Shepherd.Engine. The
#   engine references the rewritten Unity / game assemblies, so it is compiled against the owner's staged managed folder
#   (SHEPHERD_OWNER_MANAGED, default the stage-owner.sh output) with mono-aot-cross told to inline NO method of those assemblies
#   (SHEPHERD_AOT_INLINE_IMAGES = the engine + BCL only; a gate checks that every compiled method is engine, BCL or a wrapper).
#   The image still records their MVIDs and type layouts, so it is bound to the owner's DLLs: it is written to the build volume
#   only ($B/shepherd-v6-engineaot/out: TITLE_ID SHPD00011, log ux0:data/shepherd/menu-aot.txt), never to vita/shepherd/out.
# SHEPHERD_AOT_DERIVED=1 (with SHEPHERD_AOT_ENGINE=1; fid1-fixup D1, OPT-IN, OWNER-LOCAL AND NEVER SHAREABLE): also AOT-compile the
#   owner-local Shepherd.Engine.Derived (engine/Derived: the derived render code, vita/shepherd/build-derived.sh) as its OWN module:
#   built against this build's Shepherd.Engine.dll, compiled with the engine + BCL inlinable, gated so that only its image may hold
#   Shepherd_Engine_Derived_* code (the engine image may not), linked into the eboot. Its IL is never packaged: the eboot loads it from
#   ux0:data/shepherd/managed, so the exact DLL the image was compiled from is written to $OUT/owner-managed/ for the owner to copy
#   there (over stage-owner.sh's). The eboot then CONTAINS derived code: work dir $B/shepherd-v6-engineaot-derived (GL:
#   shepherd-gl-engineaot-derived), TITLE_ID SHPD00090 / SHPD00091 (GL; SHPD0009x = owner-only builds, see check_vpk.py), logs
#   menu-aotd.txt / menu_gl-aotd.txt; check_vpk.py runs with --owner-derived. Install it only on the owner's own Vita; never share it.
# KIT #6: LLVM Thumb-2 is the DEFAULT for every engine-AOT recipe except the game-AOT app (owner decision; see "device kit #6" at
#   AOT_LLVM below): unset = LLVM under the recipe's own app, SHEPHERD_AOT_LLVM=0 = the non-LLVM build (kit #5's).
# SHEPHERD_AOT_LLVM=arm|thumb (vita/notes/llvm-aot.md; OPT-IN until kit #5): AOT with Mono's LLVM backend instead of Mono's own A32 backend, via
#   the LLVM-enabled cross compiler (vita/llvm/build-cross-llvm.sh -> obj/cross-vita-llvm, the official Mono LLVM 16 tools).
#   thumb = Thumb-2 LLVM code (Mono's thumb_mixed mode: ~29% less text than today's A32, the recommended setting), arm = A32 LLVM
#   code. Each module gets a second object (<M>-llvm.o: LLVM methods + the AOT data tables + the Mono EH table) next to <M>.dll.o.
#   The runtime (libmonosgen) loads LLVM AOT images without ENABLE_LLVM; a device LLVM app links the EH-VFP runtime (above). Unset =
#   today's build, byte for byte. An LLVM build's default work dir is the recipe's + "-llvm" (never the non-LLVM build's), and the
#   headless / GL non-engine LLVM builds write their output under that work dir (not vita/shepherd/out).
#   With SHEPHERD_AOT_DERIVED=1 (kit #5): only with the float-argument fix (the derived module is compiled with LLVM too; its GUID and
#   foreign-code gates read both objects, vita/llvm/foreign-gate.sh); without the fix the build refuses the combination.
#   SHEPHERD_CROSS_LLVM_OBJ picks the LLVM compiler (default obj/cross-vita-llvm; kit #5's fixed one: vita/llvm/build-cross-llvm-armhf.sh).
# SHEPHERD_GL=1 (Stage B, the milestone app; vita/notes/m1-gl-app.md): shepherd_menu_gl.vpk -- the same eboot recipe plus the GL
#   backend's host layer: host.c built with SHEPHERD_GL, vita/gltest/shepgl (shepgl_vita.c: vitaGL context/swap + the 51 GL entry
#   points, every shepgl_* export in the static P/Invoke table), vitaGL + vitaShaRK linked, SceCtrl/ScePower/SceGxm imports.
#   TITLE_ID SHPD00008 "Shepherd Menu (GL)", logs ux0:data/shepherd/menu_gl.txt, env_gl.txt / args_gl.txt; work dir
#   $B/shepherd-gl, outputs vita/shepherd/out/gl/. With SHEPHERD_AOT_ENGINE=1 as well: SHPD00009, menu_gl-aot.txt, build volume only.
# SHEPHERD_GL=1 SHEPHERD_AUDIO=1 (Stage C; vita/notes/m1-audio.md): shepherd_menu_gl_audio.vpk -- the GL app with sound: host.c built
#   with SHEPHERD_AUDIO too, libshepaudio_vita.a (vita/audio/build.sh vita) + the audio pool (audio_pool.c) linked, SceAudio/
#   SceAudiodec imports; the engine plays the owner-copied bake (ux0:data/shepherd/audiobake/menu) through shepaudio_proc.
#   TITLE_ID SHPD00010 "Shepherd Menu (GL + audio)", logs menu_gl_audio.txt, env_gl_audio.txt / args_gl_audio.txt; work dir
#   $B/shepherd-gl-audio, outputs vita/shepherd/out/gl-audio/. Device kit #2: ran on the Vita (title + menu with sound, frame clock).
# SHEPHERD_MT_RENDER=1 (kit 3, with SHEPHERD_GL=1 SHEPHERD_AOT_ENGINE=1 SHEPHERD_AOT_DERIVED=1 only; OWNER-ONLY like SHPD00091): the MT
#   variant of the GL derived-AOT app for the device A/B of the multicore render pipeline (engine Render/MtRender.cs; vita/notes/
#   engine-pass-speed.md section 14): the same eboot recipe and AOT objects, host.c built with SHP_MT_DEFAULT, so the engine's environment
#   defaults to SHEPHERD_MT_RENDER=1 (render prep + the GL submit on a second thread) and with it the core pinning (main -> USER_0,
#   shepherd-render -> USER_1, vitaGL's garbage collector -> USER_2; SHEPHERD_VITA_PIN overrides). TITLE_ID SHPD00096 "Shepherd Menu
#   (GL, derived AOT, MT)", log menu_gl-aotd-mt.txt (env_gl.txt / args_gl.txt as every GL app), work dir shepherd-gl-engineaot-derived-mt.
#   (Any GL app can run the pipeline from env_gl.txt; this variant exists so the A/B needs no env file, which every GL app shares.)
# SHEPHERD_GL=1 SHEPHERD_AUDIO=1 SHEPHERD_AOT_ENGINE=1 SHEPHERD_AOT_DERIVED=1 (device kit #3, OWNER-ONLY): THE PLAY APP -- the GL derived-AOT
#   app (SHPD00091's AOT objects and engine IL) with SOUND (the Stage C audio host, shepaudio, the audio pool): TITLE_ID SHPD00097 "Cult of
#   the Lamb (play)", log play.txt, env_play.txt / args_play.txt (its own files: an env file left for a test app never changes it), work
#   dir shepherd-gl-audio-engineaot-derived. It is a GL app, so the play clock is its default (no pad.txt; the owner drives). With
#   SHEPHERD_MT_RENDER=1 as well: SHPD00098 "Cult of the Lamb (play MT)" (SHP_MT_DEFAULT, as 00096), log play-mt.txt, the same
#   env_play.txt / args_play.txt. SHEPHERD_AUDIO=1 with SHEPHERD_AOT_ENGINE=1 but without SHEPHERD_AOT_DERIVED=1 is refused (no ID; the
#   derived module would run interpreted).
# SHEPHERD_TEST_CLOCK=1 (device kit #3; with SHEPHERD_GL=1 SHEPHERD_AOT_ENGINE=1 SHEPHERD_AOT_DERIVED=1 only, OWNER-ONLY): the MEASUREMENT
#   variant of the GL derived-AOT app: host.c built with SHP_TEST_CLOCK -- the frame clock + pad.txt by default (the deterministic
#   scripted run: the idle-menu [sim] / [render] window 1301-1400 and the heap at 1300 land on the same screens as every frame-clock run)
#   and "boot 1460 0" (it ends by itself: nobody presses anything). TITLE_ID SHPD00099 "Shepherd Measure (GL AOT)", log
#   menu_gl-measure.txt, env_gl_measure.txt / args_gl_measure.txt, work dir shepherd-gl-engineaot-derived-measure. Same engine IL and
#   AOT objects as 00091.
# SHEPHERD_AOT_OWNER=1 (device kit #4, OWNER-ONLY AND NEVER SHAREABLE; with SHEPHERD_AOT_ENGINE=1 SHEPHERD_AOT_DERIVED=1; vita/notes/game-aot.md):
#   OWNER-LOCAL GAME-CODE AOT, variant P of the game-aot study (vita/gameaot/README.md): the owner's rewritten Unity modules and game /
#   third-party assemblies the profile names, each compiled as ITS OWN AOT module from the owner managed folder (never packaged), PROFILE-
#   GUIDED PARTIAL (profile-only + SHEPHERD_AOT_PROFILE_STRICT: only the profile's hot methods and generic class instances; the rest of
#   each module stays interpreted). Lists: SHEPHERD_AOT_OWNER_PART (default vita/gameaot/owner-modules-p.txt), SHEPHERD_AOT_OWNER_FULL
#   (whole modules; default none), SHEPHERD_AOT_OWNER_PROFILE (the .aotprofile, owner-local; default $B/gameaot/profiles/owner-p.aotprofile).
#   Toolchain: the gameaot compiler (fork + vita/gameaot/mechanics/fork-partial-aot.patch B-D + fork-gsharedvt-unop.patch;
#   SHEPHERD_CROSS_OBJ, default $B/gameaot/proto/obj/cross-vita) and the runtime with the patch's fix A (uncompiled struct methods of a
#   partial image enter the interpreter instead of another method's unbox trampoline; SHEPHERD_VITA_RUNTIME_OBJ, default
#   $B/gameaot/obj/vita-runtime-fix): both checked below. CoreLib's trampoline pools x4 / x2 / x2 (game code AOT'd takes more; running
#   out is fatal). Owner objects in $W/aot-owner, the owner GUID gate (vita/gameaot/check_owner_guids.py: every owner image against the
#   staged owner DLL it was compiled from, its dependencies against the packaged / owner DLLs), out/owner-modules.tsv (the manifest).
#   They register through their own table (shepherd_register_owner_aot_modules, host.c SHP_GAME_AOT): SHEPHERD_GAME_AOT=0 in the env
#   file skips them, and the game code runs interpreted as in the app without them (the per-case switch back). Two recipes only:
#   the play app's MT variant (SHEPHERD_GL=1 SHEPHERD_AUDIO=1 SHEPHERD_MT_RENDER=1): SHPD00095 "Cult of the Lamb (play, game AOT)", log
#   play-gaot.txt, env_play.txt / args_play.txt (the play apps' own), work dir shepherd-gl-audio-engineaot-derived-mt-gameaot,
#   cotl_play-gameaot-OWNER-ONLY.vpk; and headless (no GL): SHPD00190 "Shepherd Menu (V6, game AOT)", log menu-gaot.txt, work dir
#   shepherd-v6-engineaot-derived-gameaot (the vlx / scripted check of the same objects; not a kit app). The engine, derived and BCL
#   images are compiled exactly as without it (their gates unchanged).
# SHEPHERD_DIAG=1 (devfail1, DIAG-ONLY; headless only): "Shepherd Menu Diag" (SHPD00016, shepherd_menu_diag.vpk) -- the same recipe plus
#   host.c built with SHP_DIAG (diag/host_diag.inc.c: heartbeat + counters + phases), the engine built from a COPY of engine/ with
#   diag/engine-diag.patch applied (boot-phase markers, engine.log appended+closed per line), the counting ld --wrap thunks
#   (diag/gen_diag_wraps.py), the DIAGNOSTIC runtime (SHEPHERD_VITA_RUNTIME_DIAG_OBJ, vita/toolchain/build-runtime-diag.sh build),
#   its own icon (the LiveArea set and the VPK gate are every recipe's now). Logs menu-diag.txt, env_diag.txt / args_diag.txt; work/out default $B/shepherd-v6-diag(/out).
#   Without SHEPHERD_DIAG nothing below changes.
# SHEPHERD_ENGINE_TOOLS=1 (kit #5 review, vita/notes/kit5-boot.md "Review fixes"): keep the engine's Mac-only tests and diagnostics in the
#   eboot (boot gxpcachetest, SHEPHERD_JSONBAKE_DUMP / SHEPHERD_JSONBAKE_PROBE). Unset, every recipe builds the engine with
#   -p:ShepherdEboot=1 (compile symbol SHEPHERD_EBOOT: that code is left out -- AOT code is eboot span, and the device's free user memory
#   drops 1 MiB at a time as the span grows); nothing the apps do with their env-file switches changes.
# SHEPHERD_PHYS_CODE=0 (kit #8 review fix, vita/notes/kit8-physics.md "Review fixes"): the build switch back to kit #7's engine image -- the
#   engine is built with -p:ShepherdPhysCode=0 (Shepherd.Engine.csproj: the physics runtime Physics/ left out but the owner-local provider's
#   contract Physics/DerivedPhysics.cs, PhysicsOff/PhysOff.cs in: the SHEPHERD_PHYS=none answers -- kit #7's engine paths, SHEPHERD_PHYS not
#   read) and libshepphys is not linked (SHEPHERD_PHYS_LINK=0). Gives back the physics runtime's ~1.1 MiB of engine AOT text (every app's
#   eboot span); the Derived module keeps its owner-local provider (~0.17 MiB: the card's one Shepherd.Engine.Derived.dll is shared by every
#   app and stays byte for byte the same). Default 1 (the physics runtime in every engine image).
set -euo pipefail
HERE=${0:a:h}
V=${HERE:h}                                         # vita/
REPO=${V:h}
export VITASDK=${VITASDK:-$HOME/vitasdk} PATH="$HOME/vitasdk/bin:$HOME/.dotnet:$PATH"
export DOTNET_ROOT=$HOME/.dotnet DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
export TMPDIR=${TMPDIR:-${SHEPHERD_WORK:-$HOME/shepherd-work}/tmp}
B=${SHEPHERD_WORK:-$HOME/shepherd-work}
R=${SHEPHERD_RUNTIME:-$B/runtime}
RT=${SHEPHERD_VITA_RUNTIME_OBJ:-$B/obj/vita-runtime}
X=${SHEPHERD_CROSS_OBJ:-$B/obj/cross-vita}/out/bin/mono-aot-cross
# device kit #6 (owner decision 2026-10-19, vita/notes/roadmap-kits.md: "LLVM is the default runtime" -- kit #5's device A/B, 00191 / 00192,
# was the fastest everywhere): a recipe that leaves SHEPHERD_AOT_LLVM unset is built with LLVM Thumb-2 when it can be -- engine AOT, not
# the game-AOT app (SHEPHERD_AOT_OWNER has no LLVM variant) and not the diagnostic build -- with kit #5's fixed LLVM compiler
# ($K5RT/llvm-compiler, both patches) and the EH-VFP runtime ($K5RT/llvm), under the recipe's OWN title id, log and VPK names (a device
# app, no NOT-FOR-DEVICE.txt). A missing compiler or runtime stops the build (a kit must never lose LLVM silently).
# SHEPHERD_AOT_LLVM=0 (or empty) = kit #5's non-LLVM build of the recipe; SHEPHERD_AOT_LLVM=thumb|arm given explicitly = kit #5's rule
# (the 00099 / 00098 recipes -> the A/B apps SHPD00191 / 00192). SHEPHERD_K6_LLVM=0 = no default (kit #5's build.sh).
K6_LLVM_DEFAULT=0
if [ -z "${SHEPHERD_AOT_LLVM+x}" ] && [ "${SHEPHERD_K6_LLVM:-1}" != 0 ] && [ "${SHEPHERD_AOT_ENGINE:-0}" = 1 ] && [ "${SHEPHERD_AOT_OWNER:-0}" != 1 ] \
   && [ "${SHEPHERD_DIAG:-0}" != 1 ] && [ "${SHEPHERD_K5_RUNTIMES:-}" != 0 ]; then
  K6_LLVM_DEFAULT=1; SHEPHERD_AOT_LLVM=thumb
  SHEPHERD_CROSS_LLVM_OBJ=${SHEPHERD_CROSS_LLVM_OBJ:-${SHEPHERD_K5_RUNTIMES:-$B/kit5/runtimes}/llvm-compiler/obj/cross-llvm-armhf}
  [ -x "$SHEPHERD_CROSS_LLVM_OBJ/out/bin/mono-aot-cross" ] || { echo "kit #6: LLVM is the default and the fixed LLVM compiler $SHEPHERD_CROSS_LLVM_OBJ is missing (DEST=<it> vita/llvm/build-cross-llvm-armhf.sh) -- or SHEPHERD_AOT_LLVM=0 for kit #5's non-LLVM build"; exit 1; }
fi
[ "${SHEPHERD_AOT_LLVM:-}" = 0 ] && SHEPHERD_AOT_LLVM=
AOT_LLVM=${SHEPHERD_AOT_LLVM:-}
case "$AOT_LLVM" in ""|arm|thumb) ;; *) echo "SHEPHERD_AOT_LLVM must be arm or thumb (or 0: no LLVM)"; exit 1 ;; esac
if [ -n "$AOT_LLVM" ]; then
  XL=${SHEPHERD_CROSS_LLVM_OBJ:-$B/obj/cross-vita-llvm}/out
  X=$XL/bin/mono-aot-cross; LLVMBIN=$XL/llvm-bin/
  [ -x "$X" ] && [ -x "$LLVMBIN/llc" ] || { echo "LLVM cross compiler missing: run vita/llvm/build-cross-llvm.sh"; exit 1; }
fi
# kit #5 (vita/notes/kit5-llvm.md): the armhf float-argument fix (vita/llvm/fork-llvm-armhf-cc.patch) in the LLVM compiler -- a managed
# signature with a float/double argument is passed in s0-s15 / d0-d7 like Mono's own backend, the interpreter and gsharedvt (LLVM's C
# convention), or LLVM refuses the method. LLVM_ARMHF_CC=1 when the compiler has it and SHEPHERD_LLVM_ARMHF_CC is not 0 (the compiler
# reads the same variable: 0 = CC 21 everywhere, the kit #4 compiler's output byte for byte).
LLVM_ARMHF_CC=0
if [ -n "$AOT_LLVM" ] && [ "${SHEPHERD_LLVM_ARMHF_CC:-1}" != 0 ]; then
  strings "$X" | grep SHEPHERD_LLVM_ARMHF_CC > /dev/null && LLVM_ARMHF_CC=1 || true   # (no grep -q: pipefail)
fi
PACK=$B/packs/mono.linux-arm.8.0.31/runtimes/linux-arm
ENGINE_AOT=${SHEPHERD_AOT_ENGINE:-0}
DERIVED_AOT=${SHEPHERD_AOT_DERIVED:-0}
[ "$DERIVED_AOT" = 1 ] && [ "$ENGINE_AOT" != 1 ] && { echo "SHEPHERD_AOT_DERIVED=1 needs SHEPHERD_AOT_ENGINE=1 (the derived module calls the engine everywhere; both are owner-local)"; exit 1; }
[ "$DERIVED_AOT" = 1 ] && [ ! -f "$REPO/engine/Derived/Shepherd.Engine.Derived/Shepherd.Engine.Derived.csproj" ] && { echo "SHEPHERD_AOT_DERIVED=1: no owner-local engine/Derived source here"; exit 1; }
# kit #5: wired (the derived module is compiled with LLVM too; its GUID / foreign-code gates read both objects), but only with the
# float-argument fix: the derived module calls the engine with float arguments everywhere (UnityEngine.Rect / Vector3 / Color maths)
[ "$DERIVED_AOT" = 1 ] && [ -n "$AOT_LLVM" ] && [ "$LLVM_ARMHF_CC" != 1 ] && { echo "SHEPHERD_AOT_DERIVED=1 with SHEPHERD_AOT_LLVM needs the LLVM compiler with the armhf float-argument fix (vita/llvm/fork-llvm-armhf-cc.patch) and SHEPHERD_LLVM_ARMHF_CC unset or 1"; exit 1; }
GL=${SHEPHERD_GL:-0}
AUDIO=${SHEPHERD_AUDIO:-0}
DIAG=${SHEPHERD_DIAG:-0}
MT=${SHEPHERD_MT_RENDER:-0}
TEST_CLOCK=${SHEPHERD_TEST_CLOCK:-0}
[ "$MT" = 1 ] && { [ "$GL" != 1 ] || [ "$ENGINE_AOT" != 1 ] || [ "$DERIVED_AOT" != 1 ]; } && { echo "SHEPHERD_MT_RENDER=1 builds the MT variant of the GL derived-AOT app only (SHEPHERD_GL=1 SHEPHERD_AOT_ENGINE=1 SHEPHERD_AOT_DERIVED=1: SHPD00096, with SHEPHERD_AUDIO=1 the play app's SHPD00098); any other app runs the pipeline from its env file (unset SHEPHERD_MT_RENDER in this shell)"; exit 1; }
[ "$TEST_CLOCK" = 1 ] && { [ "$GL" != 1 ] || [ "$ENGINE_AOT" != 1 ] || [ "$DERIVED_AOT" != 1 ] || [ "$AUDIO" = 1 ] || [ "$MT" = 1 ]; } && { echo "SHEPHERD_TEST_CLOCK=1 builds the measurement variant of the GL derived-AOT app only (SHEPHERD_GL=1 SHEPHERD_AOT_ENGINE=1 SHEPHERD_AOT_DERIVED=1, no audio, no MT: SHPD00099); any other GL app takes SHEPHERD_TIME=frame from its env file"; exit 1; }
[ "$DIAG" = 1 ] && { [ "$GL" = 1 ] || [ "$ENGINE_AOT" = 1 ]; } && { echo "SHEPHERD_DIAG=1 is the headless menu only (no SHEPHERD_GL / SHEPHERD_AOT_ENGINE)"; exit 1; }
[ "$AUDIO" = 1 ] && [ "$GL" != 1 ] && { echo "SHEPHERD_AUDIO=1 is a variant of the GL app: set SHEPHERD_GL=1 too"; exit 1; }
[ "$AUDIO" = 1 ] && [ "$ENGINE_AOT" = 1 ] && [ "$DERIVED_AOT" != 1 ] && { echo "SHEPHERD_AUDIO=1 with SHEPHERD_AOT_ENGINE=1 builds the play app, whose derived module is AOT-compiled too: set SHEPHERD_AOT_DERIVED=1 (SHPD00097; there is no GL + audio + engine-AOT app with the derived module interpreted)"; exit 1; }
OWNER_AOT=${SHEPHERD_AOT_OWNER:-0}
# device kit #8, track boot (vita/notes/kit8-boot.md): SHEPHERD_K8_LAUNCHER=1 builds the play app as the TINY LAUNCHER (vita/shepherd/launcher.c,
# the ~0.1 MiB eboot.bin: the Devolver splash through the hardware video decoder at once) + the ENGINE MODULE app0:module/shepherd.suprx
# (host.c built with SHP_K8_MODULE + vita/shepherd/k8_module.c + the runtime + every AOT image: the objects of the recipe's eboot, linked as a
# user module). The device A/B apps, next to the default apps (never replacing them): the MT play recipe -> SHPD00193 "Cult of the Lamb
# (play MT, launcher)", the game-AOT recipe -> SHPD00194 "Cult of the Lamb (play, game AOT, launcher)"; their own log names, the play apps'
# env_play.txt / args_play.txt. Unset / 0 = the kit #7 eboot (everything in eboot.bin).
LAUNCHER=${SHEPHERD_K8_LAUNCHER:-0}
# device kit #8, track boot: SHEPHERD_K8_ENGINE -- the ENGINE side of the boot rows (Runtime/K8Video.cs the real video, Runtime/K8Preload.cs the
# idle-window preload / Main Menu head start, and their backend / pack-reader hooks). 1 = compiled in: the launcher apps' default (and the
# Mac build's). 0 = left out (-p:ShepherdK8=0, compile symbol SHEPHERD_NO_K8: kit #7's engine code): the default of every eboot app, whose
# AOT code is eboot span -- compiled in, it costs 00098 +37 KB of AOT text, one 64 KiB text step, and with it the 315 MiB step of free user
# memory (00097 / 00098 have 0.003-0.129 MiB of room). The host side (model check, clean boot screen) is in every app either way.
K8_ENGINE=${SHEPHERD_K8_ENGINE:-$LAUNCHER}
if [ "$LAUNCHER" = 1 ]; then
  { [ "$GL" = 1 ] && [ "$AUDIO" = 1 ] && [ "$MT" = 1 ] && [ "$ENGINE_AOT" = 1 ] && [ "$DERIVED_AOT" = 1 ]; } || { echo "SHEPHERD_K8_LAUNCHER=1 builds the MT play app (SHEPHERD_GL=1 SHEPHERD_AUDIO=1 SHEPHERD_MT_RENDER=1 SHEPHERD_AOT_ENGINE=1 SHEPHERD_AOT_DERIVED=1: SHPD00193; + SHEPHERD_AOT_OWNER=1: SHPD00194) only"; exit 1; }
fi
if [ "$OWNER_AOT" = 1 ]; then
  { [ "$ENGINE_AOT" = 1 ] && [ "$DERIVED_AOT" = 1 ]; } || { echo "SHEPHERD_AOT_OWNER=1 (owner-local game AOT) needs SHEPHERD_AOT_ENGINE=1 SHEPHERD_AOT_DERIVED=1"; exit 1; }
  [ -z "$AOT_LLVM" ] || { echo "SHEPHERD_AOT_OWNER=1 has no LLVM variant (SHEPHERD_AOT_LLVM: partial LLVM images hit defect E of the game-aot study, and the derived module is not LLVM-wired either)"; exit 1; }
  [ "$TEST_CLOCK" != 1 ] || { echo "SHEPHERD_AOT_OWNER=1 has no measurement variant (a play app takes SHEPHERD_TIME=frame + SHEPHERD_PAD_FILE from env_play.txt)"; exit 1; }
  [ "$GL" != 1 ] || { [ "$AUDIO" = 1 ] && [ "$MT" = 1 ]; } || { echo "SHEPHERD_AOT_OWNER=1 builds the play app's MT variant (SHEPHERD_GL=1 SHEPHERD_AUDIO=1 SHEPHERD_MT_RENDER=1: SHPD00095; SHEPHERD_MT_RENDER=0 in env_play.txt turns MT off) or the headless menu (no GL: SHPD00190) only"; exit 1; }
  # the gameaot toolchain (checked below): the compiler with the partial-AOT patches, the runtime with fix A
  X=${SHEPHERD_CROSS_OBJ:-$B/gameaot/proto/obj/cross-vita}/out/bin/mono-aot-cross
  RT=${SHEPHERD_VITA_RUNTIME_OBJ:-$B/gameaot/obj/vita-runtime-fix}
fi
# device kit #5 (vita/notes/kit5-integration.md): lever 20, the AOT -> interpreter call-info cache, lives in the Mono runtime, so it is on
# only in an eboot that links a kit #5 runtime (vita/toolchain/build-runtime-k5.sh). Every recipe links one by default:
#   K5RT/base/obj  every app (the shared fork + vita/patches/k5-interp-cicache.patch)
#   K5RT/fix/obj   SHEPHERD_AOT_OWNER=1 (the gameaot fork with its fix A + the patch: SHPD00095 / 00190)
#   K5RT/llvm/obj  SHEPHERD_AOT_LLVM (base + vita/llvm/fork-eh-vfp.patch fixes B / C: SHPD00191 / 00192 need both)
# K5RT = SHEPHERD_K5_RUNTIMES (default $B/kit5/runtimes; build them with build-runtime-k5.sh base|fix|llvm $K5RT/<kind>).
# SHEPHERD_VITA_RUNTIME_OBJ (an explicit runtime) wins. SHEPHERD_K5_RUNTIMES=0 = the kit #4 shared runtimes (no cache; the host's H8
# line says so). A missing kit #5 runtime stops the build: a kit must never lose lever 20 silently. (SHEPHERD_DIAG=1 links its own.)
K5RT=${SHEPHERD_K5_RUNTIMES:-$B/kit5/runtimes}
if [ -z "${SHEPHERD_VITA_RUNTIME_OBJ:-}" ] && [ "$K5RT" != 0 ] && [ "$DIAG" != 1 ]; then
  K5KIND=base; [ "$OWNER_AOT" = 1 ] && K5KIND=fix; [ -n "$AOT_LLVM" ] && K5KIND=llvm
  RT=$K5RT/$K5KIND/obj
  [ -f "$RT/mono/mini/libmonosgen-2.0.a" ] && [ -f "$K5RT/$K5KIND/K5-RUNTIME.txt" ] || { echo "the kit #5 runtime $RT is missing (or failed its member guard): vita/toolchain/build-runtime-k5.sh $K5KIND $K5RT/$K5KIND -- or SHEPHERD_K5_RUNTIMES=0 for kit #4's shared runtime (no lever 20)"; exit 1; }
fi
# device kit #8 (track jit, vita/notes/kit8-jit.md): SHEPHERD_JIT_APP=1 builds the recipe as a JIT APP -- it links the kit #8 JIT runtime
# (kit #5's LLVM runtime + the baseline JIT tier: vita/toolchain/build-runtime-k8.sh jit $K8RT/llvm-jit; K8RT = SHEPHERD_K8_RUNTIMES, default
# $B/kit8/runtimes; SHEPHERD_VITA_RUNTIME_OBJ still wins) and host.c with SHEPHERD_JIT (vita/shepherd/k8_jit_host.inc.c: the ForVM code block
# before the partition, the J1 / J2 lines). The app's SHEPHERD_JIT default is 1; SHEPHERD_JIT=0 in its env file = the kit #7 interpreter + AOT
# path. LLVM engine-AOT recipes only (the play apps 00097 / 00098); the recipe keeps its title id, names and files. Unset = no JIT (kit #7).
JIT_APP=${SHEPHERD_JIT_APP:-0}
if [ "$JIT_APP" = 1 ]; then
  { [ -n "$AOT_LLVM" ] && [ "$OWNER_AOT" != 1 ] && [ "$DIAG" != 1 ]; } || { echo "SHEPHERD_JIT_APP=1 needs an LLVM engine-AOT recipe (the play apps 00097 / 00098: the kit #8 JIT runtime is kit #5's LLVM runtime + the tier)"; exit 1; }
  K8RT=${SHEPHERD_K8_RUNTIMES:-$B/kit8/runtimes}
  RT=${SHEPHERD_VITA_RUNTIME_OBJ:-$K8RT/llvm-jit/obj}
  { [ -f "$RT/mono/mini/libmonosgen-2.0.a" ] && [ -f "${RT%/obj}/K8-RUNTIME.txt" ]; } || { echo "SHEPHERD_JIT_APP=1: the kit #8 JIT runtime $RT is missing (or failed its member guard): vita/toolchain/build-runtime-k8.sh jit ${RT%/obj}"; exit 1; }
  arm-vita-eabi-nm "$RT/mono/mini/libmonosgen-2.0.a" 2>/dev/null | grep " T mono_shepjit_set_block$" > /dev/null || { echo "SHEPHERD_JIT_APP=1: $RT has no baseline JIT tier"; exit 1; }
fi
GLW=; [ "$GL" = 1 ] && GLW=-gl                     # work/out dirs of the GL app are separate from the headless build's
[ "$AUDIO" = 1 ] && GLW=-gl-audio
if [ "$ENGINE_AOT" = 1 ]; then
  W=${SHEPHERD_V6_WORK:-$B/shepherd${GLW:-"-v6"}-engineaot${${DERIVED_AOT:#0}:+-derived}${${MT:#0}:+-mt}${${TEST_CLOCK:#0}:+-measure}${${OWNER_AOT:#0}:+-gameaot}${AOT_LLVM:+-llvm}${${LAUNCHER:#0}:+-launcher}}; OUT=${SHEPHERD_V6_OUT:-$W/out}
  OWNER_MANAGED=${SHEPHERD_OWNER_MANAGED:-$B/v6b/owner/shepherd/managed}
  [ -f "$OWNER_MANAGED/UnityEngine.CoreModule.dll" ] || { echo "SHEPHERD_AOT_ENGINE=1 needs the staged owner managed folder ($OWNER_MANAGED): run vita/shepherd/stage-owner.sh"; exit 1; }
  case "$OUT" in "$REPO"/*) echo "SHEPHERD_AOT_ENGINE=1: the output is bound to the owner's DLLs; it must not go under the repo ($OUT)"; exit 1 ;; esac
elif [ "$DIAG" = 1 ]; then
  W=${SHEPHERD_V6_WORK:-$B/shepherd-v6-diag}; OUT=${SHEPHERD_V6_OUT:-$W/out}
  case "$W" in "$B/shepherd-v6"|"$B/shepherd-v6/"*) echo "SHEPHERD_DIAG=1 must not use the normal work dir $W"; exit 1 ;; esac
  RT=${SHEPHERD_VITA_RUNTIME_DIAG_OBJ:?SHEPHERD_DIAG=1 links the diagnostic runtime: set SHEPHERD_VITA_RUNTIME_DIAG_OBJ (vita/toolchain/build-runtime-diag.sh build)}
  arm-vita-eabi-nm "$RT/mono/mini/libmonosgen-2.0.a" 2>/dev/null | grep -q " T shepherd_vita_diag_exc_fail$" || { echo "$RT is not the diagnostic runtime (no shepherd_vita_diag_* hooks)"; exit 1; }
else
  W=${SHEPHERD_V6_WORK:-$B/shepherd${GLW:-"-v6"}${AOT_LLVM:+-llvm}}  # big intermediates (.s files) stay on the build volume
  OUT=${SHEPHERD_V6_OUT:-$HERE/out${GLW:+/${GLW#-}}}; [ -n "$AOT_LLVM" ] && OUT=${SHEPHERD_V6_OUT:-$W/out}
fi
SHIM=$V/runtime/system-native
COMMON=$VITASDK/share/gcc-arm-vita-eabi/samples/common
TITLE_ID=SHPD00006; TITLE="Shepherd Menu (V6)"; LOG_NAME=menu.txt; LOG_PREV_NAME=menu.prev.txt; ENV_NAME=env.txt; ARGS_NAME=args.txt; VPK=shepherd_menu.vpk
[ "$DIAG" = 1 ] && { TITLE_ID=SHPD00016; TITLE="Shepherd Menu Diag"; LOG_NAME=menu-diag.txt; LOG_PREV_NAME=menu-diag.prev.txt; ENV_NAME=env_diag.txt; ARGS_NAME=args_diag.txt; VPK=shepherd_menu_diag.vpk; }
[ "$ENGINE_AOT" = 1 ] && { TITLE_ID=SHPD00011; TITLE="Shepherd Menu (V6 engine AOT)"; LOG_NAME=menu-aot.txt; LOG_PREV_NAME=menu-aot.prev.txt; }
[ "$DERIVED_AOT" = 1 ] && { TITLE_ID=SHPD00090; TITLE="Shepherd Menu (V6, owner-local derived AOT)"; LOG_NAME=menu-aotd.txt; LOG_PREV_NAME=menu-aotd.prev.txt; VPK=shepherd_menu-aotd-OWNER-ONLY.vpk; }
[ "$OWNER_AOT" = 1 ] && { TITLE_ID=SHPD00190; TITLE="Shepherd Menu (V6, game AOT)"; LOG_NAME=menu-gaot.txt; LOG_PREV_NAME=menu-gaot.prev.txt; VPK=shepherd_menu-gaot-OWNER-ONLY.vpk; }   # headless (the GL block below sets the play app's)
if [ "$GL" = 1 ]; then
  TITLE_ID=SHPD00008; TITLE="Shepherd Menu (GL)"; LOG_NAME=menu_gl.txt; LOG_PREV_NAME=menu_gl.prev.txt; ENV_NAME=env_gl.txt; ARGS_NAME=args_gl.txt; VPK=shepherd_menu_gl.vpk
  [ "$ENGINE_AOT" = 1 ] && { TITLE_ID=SHPD00009; TITLE="Shepherd Menu (GL engine AOT)"; LOG_NAME=menu_gl-aot.txt; LOG_PREV_NAME=menu_gl-aot.prev.txt; VPK=shepherd_menu_gl-aot.vpk; }
  [ "$DERIVED_AOT" = 1 ] && { TITLE_ID=SHPD00091; TITLE="Shepherd Menu (GL, owner-local derived AOT)"; LOG_NAME=menu_gl-aotd.txt; LOG_PREV_NAME=menu_gl-aotd.prev.txt; VPK=shepherd_menu_gl-aotd-OWNER-ONLY.vpk; }
  [ "$MT" = 1 ] && { TITLE_ID=SHPD00096; TITLE="Shepherd Menu (GL, derived AOT, MT)"; LOG_NAME=menu_gl-aotd-mt.txt; LOG_PREV_NAME=menu_gl-aotd-mt.prev.txt; VPK=shepherd_menu_gl-aotd-mt-OWNER-ONLY.vpk; }
  [ "$AUDIO" = 1 ] && { TITLE_ID=SHPD00010; TITLE="Shepherd Menu (GL + audio)"; LOG_NAME=menu_gl_audio.txt; LOG_PREV_NAME=menu_gl_audio.prev.txt; ENV_NAME=env_gl_audio.txt; ARGS_NAME=args_gl_audio.txt; VPK=shepherd_menu_gl_audio.vpk; }
  # device kit #3: the play app (GL + audio + engine AOT + derived AOT; its MT variant) and the measurement variant of 00091 (OWNER-ONLY)
  [ "$AUDIO" = 1 ] && [ "$ENGINE_AOT" = 1 ] && { TITLE_ID=SHPD00097; TITLE="Cult of the Lamb (play)"; LOG_NAME=play.txt; LOG_PREV_NAME=play.prev.txt; ENV_NAME=env_play.txt; ARGS_NAME=args_play.txt; VPK=cotl_play-OWNER-ONLY.vpk; }
  [ "$AUDIO" = 1 ] && [ "$MT" = 1 ] && { TITLE_ID=SHPD00098; TITLE="Cult of the Lamb (play MT)"; LOG_NAME=play-mt.txt; LOG_PREV_NAME=play-mt.prev.txt; VPK=cotl_play-mt-OWNER-ONLY.vpk; }
  [ "$TEST_CLOCK" = 1 ] && { TITLE_ID=SHPD00099; TITLE="Shepherd Measure (GL AOT)"; LOG_NAME=menu_gl-measure.txt; LOG_PREV_NAME=menu_gl-measure.prev.txt; ENV_NAME=env_gl_measure.txt; ARGS_NAME=args_gl_measure.txt; VPK=shepherd_menu_gl-measure-OWNER-ONLY.vpk; }
  # device kit #4: the play app (MT) with the owner-local game AOT (env_play.txt / args_play.txt, the play apps' own)
  [ "$OWNER_AOT" = 1 ] && { TITLE_ID=SHPD00095; TITLE="Cult of the Lamb (play, game AOT)"; LOG_NAME=play-gaot.txt; LOG_PREV_NAME=play-gaot.prev.txt; VPK=cotl_play-gameaot-OWNER-ONLY.vpk; }
  # device kit #8 (track boot): the launcher A/B apps (SHEPHERD_K8_LAUNCHER=1, above)
  [ "$LAUNCHER" = 1 ] && [ "$TITLE_ID" = SHPD00098 ] && { TITLE_ID=SHPD00193; TITLE="Cult of the Lamb (play MT, launcher)"; LOG_NAME=play-mt-launcher.txt; LOG_PREV_NAME=play-mt-launcher.prev.txt; VPK=cotl_play-mt-launcher-OWNER-ONLY.vpk; }
  [ "$LAUNCHER" = 1 ] && [ "$TITLE_ID" = SHPD00095 ] && { TITLE_ID=SHPD00194; TITLE="Cult of the Lamb (play, game AOT, launcher)"; LOG_NAME=play-gaot-launcher.txt; LOG_PREV_NAME=play-gaot-launcher.prev.txt; VPK=cotl_play-gameaot-launcher-OWNER-ONLY.vpk; }
  # device kit #5 (vita/notes/kit5-llvm.md): the LLVM A/B apps, EXTRA owner-only apps next to 00099 / 00098 (never replacing them): the same
  # recipes + SHEPHERD_AOT_LLVM=thumb with the float-argument fix; their own log names (and so engine log folders), the same env / args files
  if [ -n "$AOT_LLVM" ] && [ "$LLVM_ARMHF_CC" = 1 ] && [ "$K6_LLVM_DEFAULT" != 1 ]; then   # (kit #6: an LLVM build by default keeps the recipe's own app)
    [ "$TEST_CLOCK" = 1 ] && { TITLE_ID=SHPD00191; TITLE="Shepherd Measure (GL AOT, LLVM)"; LOG_NAME=menu_gl-measure-llvm.txt; LOG_PREV_NAME=menu_gl-measure-llvm.prev.txt; VPK=shepherd_menu_gl-measure-llvm-OWNER-ONLY.vpk; }
    [ "$AUDIO" = 1 ] && [ "$MT" = 1 ] && [ "$OWNER_AOT" != 1 ] && { TITLE_ID=SHPD00192; TITLE="Cult of the Lamb (play MT, LLVM)"; LOG_NAME=play-mt-llvm.txt; LOG_PREV_NAME=play-mt-llvm.prev.txt; VPK=cotl_play-mt-llvm-OWNER-ONLY.vpk; }
  fi
fi
# the kit-3 variants (and kit 4's game-AOT play app) name themselves on their BEGIN line (host.c SHP_APP_TITLE)
APP_DEFS=(); [ "$TITLE_ID" = SHPD00097 ] || [ "$TITLE_ID" = SHPD00098 ] || [ "$TITLE_ID" = SHPD00099 ] || [ "$TITLE_ID" = SHPD00095 ] || [ "$TITLE_ID" = SHPD00191 ] || [ "$TITLE_ID" = SHPD00192 ] || [ "$TITLE_ID" = SHPD00193 ] || [ "$TITLE_ID" = SHPD00194 ] && APP_DEFS=(-DSHP_APP_TITLE="\"$TITLE\"" -DSHP_APP_ID="\"$TITLE_ID\"")
[ "$LAUNCHER" = 1 ] && APP_DEFS+=(-DSHP_K8_MODULE)   # kit #8: host.c in the engine module (no process parameters: the launcher eboot has them)
# SHEPHERD_AOT_OWNER=1: the owner modules register through their own table; host.c reads SHEPHERD_GAME_AOT (env file; 0 = skip them)
[ "$OWNER_AOT" = 1 ] && APP_DEFS+=(-DSHP_GAME_AOT)
# kit #8: a JIT app's host (k8_jit_host.inc.c)
[ "$JIT_APP" = 1 ] && APP_DEFS+=(-DSHEPHERD_JIT)
# param.sfo APP_VER (the "Version" in the Vita's app info): the play apps (00097 / 00098 / 00095) carry the app version of vita/CHANGELOG.md
# from vita/shepherd/VERSION (0.K = device kit #K; bump it before building a new kit; SHEPHERD_APP_VERSION overrides), written the
# Vita's NN.NN way: 0.3 -> 00.03, 0.12 -> 00.12. Every other recipe keeps 01.00. (Kits #1-#3 were built before this: all 01.00.)
APP_VER=01.00
if [ "$TITLE_ID" = SHPD00097 ] || [ "$TITLE_ID" = SHPD00098 ] || [ "$TITLE_ID" = SHPD00095 ] || [ "$TITLE_ID" = SHPD00192 ] || [ "$TITLE_ID" = SHPD00193 ] || [ "$TITLE_ID" = SHPD00194 ]; then
  APP_VERSION=${SHEPHERD_APP_VERSION:-$(tr -d ' \t\r\n' < "$HERE/VERSION")}
  [[ $APP_VERSION =~ '^[0-9]{1,2}\.[0-9]{1,2}$' ]] || { echo "app version '$APP_VERSION' (vita/shepherd/VERSION or SHEPHERD_APP_VERSION) is not MAJOR.MINOR, 0-99 each"; exit 1; }
  APP_VER=$(printf '%02d.%02d' $((10#${APP_VERSION%%.*})) $((10#${APP_VERSION#*.})))
fi
# the play app's audio pool: 4 MiB (the whole menu tour peaks at 2.1 MiB: take cache 1.1 + the stream rings), so its GC heap cap is 124 MiB
# instead of 00010's 120 -- under vlx the tour through every sub-menu passes at 124 and dies of an unhandled OutOfMemoryException at 120
# (closed sub-menus stay in the heap, as in Unity; vita/notes/play-mode.md section 6). SHEPHERD_AUDIO_POOL_MB in env_play.txt overrides.
[ "$AUDIO" = 1 ] && [ "$ENGINE_AOT" = 1 ] && APP_DEFS+=(-DSHP_AUDIO_POOL_MB=4u)
AUDIOLIB=$V/audio/build/vita/libshepaudio_vita.a
# kit #8 (Law of the Vita part 1; vita/notes/kit8-physics.md): libshepphys (vita/physics: Box2D 2.3.1 + the Unity layer, the physics
# Laws so far) linked into the eboot, so the engine's physics levels have a core on the device. SHEPHERD_PHYS_LINK = auto (default: the
# play apps -- GL + audio: 00097 / 00098 / 00095) | 1 (any GL build) | 0 (kit #7's eboot: host.c's NULL shepphys_proc, no library).
# With it host.c (phys_host.inc.c) answers shepphys_proc from the library, sets the play apps' default level SHEPHERD_PHYS_DEFAULT
# (default 6 = Walking + Triggers + Combat + Shapes so far + Ground: the proven level that makes the intro walkable; the env file's
# SHEPHERD_PHYS wins, =0 / none = kit #7's behaviour), takes the pool block from the arena's share at level >= 1, pins the Mono thread
# to USER_0 and logs the PHYS lines. The library is built by vita/physics/build.sh vita (its own link check, symbol / FMA / NEON scan)
# into $W/physics; a copy goes to $W/obj/libshepphys_vita.a (vita/vlx/link.sh relinks it).
PHYS_LINK=${SHEPHERD_PHYS_LINK:-auto}
if [ "$PHYS_LINK" = auto ]; then PHYS_LINK=0; [ "$GL" = 1 ] && [ "$AUDIO" = 1 ] && PHYS_LINK=1; fi
PHYS_CODE=${SHEPHERD_PHYS_CODE:-1}   # kit #8 review fix: 0 = the physics runtime compiled out of the engine (header above)
case "$PHYS_CODE" in 0|1) ;; *) echo "SHEPHERD_PHYS_CODE=$PHYS_CODE: 0 or 1"; exit 1 ;; esac
if [ "$PHYS_CODE" = 0 ]; then
  [ "${SHEPHERD_PHYS_LINK:-auto}" = 1 ] && { echo "SHEPHERD_PHYS_CODE=0 has no physics runtime for libshepphys (SHEPHERD_PHYS_LINK=1)"; exit 1; }
  PHYS_LINK=0
fi
[ "$PHYS_LINK" = 1 ] && [ "$GL" != 1 ] && { echo "SHEPHERD_PHYS_LINK=1 links libshepphys into a GL app only (the headless host has no shepphys_proc)"; exit 1; }
PHYS_DEFAULT=${SHEPHERD_PHYS_DEFAULT:-6}
case "$PHYS_DEFAULT" in none|[0-7]) ;; *) echo "SHEPHERD_PHYS_DEFAULT=$PHYS_DEFAULT: none or 0..7"; exit 1 ;; esac
PHYSLIB=
SHEPGL=$V/gltest/shepgl
# newlib heap (link time). Engine-AOT builds default to 116 MiB (kit 3, vita/notes/m1-gl-app.md §4 "GL + engine AOT"): their eboot spans
# 11.2 MiB more (GL derived AOT 53.79 vs GL 42.56 MiB), which the device takes 1:1 from user memory, but they need ~13 MiB less newlib
# (fewer interpreter transforms: the vlx menu tour peaks at 101.3-103.7 MiB vs the interpreted GL app's 113.8-116.6). At 128 the GL
# derived-AOT app's arena was 139 MiB (max-heap-size 117): the tour's Credits screen hit the cap 95,477 times, exhausted the arena and
# threw an OutOfMemoryException under vlx. At 116: arena 151 / max-heap-size 129 -- the interpreted GL app's margins on both pools (it
# passes the tour with arena 150 / cap 128 and >= 11.4 MiB of newlib left; this app keeps >= 12.3, its MT variant >= 9.4).
NEWLIB_MB=${SHEPHERD_NEWLIB_MB:-128}; [ "$ENGINE_AOT" = 1 ] && NEWLIB_MB=${SHEPHERD_NEWLIB_MB:-116}
[ -d "$R/src/mono" ] || { echo "build volume not attached: hdiutil attach \"$HOME/shepherd-build.sparsebundle\""; exit 1; }
[ -x "$X" ] || { echo "mono-aot-cross missing: run vita/toolchain/build-cross.sh"; exit 1; }
[ -f "$RT/mono/mini/libmonosgen-2.0.a" ] || { echo "runtime missing: run vita/toolchain/build-runtime-vita.sh"; exit 1; }
# kit #5 review fix (vita/notes/kit5-llvm.md "Review fixes"; vita/llvm/fork-eh-vfp.patch): a caught exception must hand an LLVM frame
# its callee-saved d8-d15 back. Fix A is in the compiler (the throw trampolines it writes into CoreLib's image; it reads
# SHEPHERD_EH_VFP_FIX too: 0 = the stock trampolines), fixes B + C in the runtime (vita/llvm/build-runtime-eh-vfp.sh, recognised by
# its EH-VFP-RUNTIME.txt naming this archive's sha256). LLVM_EH_VFP=1 when both are there and SHEPHERD_EH_VFP_FIX is not 0.
LLVM_EH_VFP=0; LLVM_EH_VFP_WHY=
if [ -n "$AOT_LLVM" ]; then
  if [ "${SHEPHERD_EH_VFP_FIX:-1}" = 0 ]; then LLVM_EH_VFP_WHY="SHEPHERD_EH_VFP_FIX=0"
  elif ! strings "$X" | grep SHEPHERD_EH_VFP_FIX > /dev/null; then LLVM_EH_VFP_WHY="the compiler $X lacks fix A (vita/llvm/build-cross-llvm-armhf.sh)"
  elif [ ! -f "$RT/EH-VFP-RUNTIME.txt" ] || [ "$(sed -n 's/^libmonosgen-2.0.a sha256 //p' "$RT/EH-VFP-RUNTIME.txt")" != "$(shasum -a 256 "$RT/mono/mini/libmonosgen-2.0.a" | cut -d' ' -f1)" ]; then
    LLVM_EH_VFP_WHY="the runtime $RT lacks fixes B + C (set SHEPHERD_VITA_RUNTIME_OBJ to vita/llvm/build-runtime-eh-vfp.sh's output)"
  else LLVM_EH_VFP=1; fi
fi
if [ "$K6_LLVM_DEFAULT" = 1 ] && { [ "$LLVM_ARMHF_CC" != 1 ] || [ "$LLVM_EH_VFP" != 1 ]; }; then echo "kit #6: LLVM is the default, but this build would not be a device app (float-argument fix $LLVM_ARMHF_CC, exception fix $LLVM_EH_VFP: $LLVM_EH_VFP_WHY) -- fix the toolchain (vita/notes/kit5-llvm.md) or SHEPHERD_AOT_LLVM=0 for kit #5's non-LLVM build"; exit 1; fi
mkdir -p "$W/aot" "$W/obj" "$W/engine" "$OUT" "$TMPDIR"
print -r -- "$RT" > "$W/obj/vita-runtime-obj.txt"   # the runtime this eboot links (vita/vlx/link.sh relinks the same)
if [ "$OWNER_AOT" = 1 ]; then
  # the gameaot toolchain: the compiler must know SHEPHERD_AOT_PROFILE_STRICT (fork-partial-aot.patch D) and the runtime must carry fix A:
  # its mono_aot_get_unbox_trampoline calls mono_aot_get_unbox_arbitrary_trampoline for a method the table lacks (stock 8.0.31 never
  # does: it returns the last probed entry, ANOTHER method's trampoline -- a boxed struct's virtual call would run the wrong method)
  strings "$X" | grep SHEPHERD_AOT_PROFILE_STRICT > /dev/null \
    || { echo "SHEPHERD_AOT_OWNER=1: $X lacks the gameaot compiler patches (vita/gameaot/proto/build-compilers.sh cross)"; exit 1; }   # (no grep -q: pipefail)
  rm -rf "$W/obj/fixa"; mkdir -p "$W/obj/fixa"
  (cd "$W/obj/fixa" && arm-vita-eabi-ar x "$RT/mono/mini/libmonosgen-2.0.a" aot-runtime.c.obj)
  arm-vita-eabi-objdump -dr "$W/obj/fixa/aot-runtime.c.obj" | awk '/^[0-9a-f]+ <.*>:$/ {fn=$2} fn=="<mono_aot_get_unbox_trampoline>:" && /R_ARM_[A-Z_]*(CALL|JUMP24)[[:space:]]+mono_aot_get_unbox_arbitrary_trampoline$/ {n++} END {exit !(n > 0)}' \
    || { echo "SHEPHERD_AOT_OWNER=1: the runtime $RT lacks the gameaot fix A (partial images need it; vita/gameaot/mechanics/fork-partial-aot.patch)"; exit 1; }
  echo "   gameaot toolchain: $X (partial-AOT patches), runtime $RT (fix A)"
fi

# Statically AOT-compiled (full + interpreter entry wrappers): CoreLib and the BCL modules the menu boot executes a lot of
# (stage A's interp-methods.tsv), plus our own Shepherd.Assets (pack decode) and Shepherd.Hooks. Everything else -- the
# rest of the BCL, Shepherd.Engine (it references and would inline/instantiate the rewritten Unity modules), the game and
# the Unity modules -- runs in the interpreter. The rest of the BCL is packaged as IL and loaded only on demand.
AOT=(System.Private.CoreLib System.Linq System.Collections System.Collections.Concurrent System.Console System.Memory
     System.ObjectModel System.Runtime.InteropServices System.Threading Shepherd.Assets Shepherd.Hooks Microsoft.Bcl.AsyncInterfaces)
# Device kit #8 speed track (vita/notes/kit8-speed.md "CoreLib trimming", the module step): the kit #7 first-call traces (SHEPHERD_K7_PROF=calls,
# every vlx route) show four of these modules barely used -- System.Memory (2 methods: ReadOnlySequence's .cctor / .ctor), System.ObjectModel
# (6: attribute getters + SR strings at boot; its KeyedCollection instances already run interpreted), System.Runtime.InteropServices and
# System.Threading (none) -- for 2.71 MiB of LLVM text (kit #7's aot-sizes.txt: 1.12 + 0.75 + 0.80 + 0.04). Left out of the static AOT set
# they are packaged as IL like the rest of the BCL and their few methods run in the interpreter (the same IL, the same answers); the eboot
# span drops by their text. SHEPHERD_BCL_AOT_TRIM=0 = kit #7's AOT set (the build-time switch back: AOT code cannot come back from an env
# file; for kit #7's memory numbers in an env file see the note's SHEPHERD_ARENA_MB route).
BCL_TRIM=(System.Memory System.ObjectModel System.Runtime.InteropServices System.Threading)
if [ "${SHEPHERD_BCL_AOT_TRIM:-1}" != 0 ]; then AOT=(${AOT:|BCL_TRIM}); BCL_TRIM_NOTE="kit #8 BCL AOT trim: ${BCL_TRIM[*]} interpreted (SHEPHERD_BCL_AOT_TRIM=0: kit #7's set)"; BCL_TRIM_ENV="${(j:,:)BCL_TRIM}"
else BCL_TRIM_NOTE="kit #8 BCL AOT trim OFF (SHEPHERD_BCL_AOT_TRIM=0): kit #7's AOT set"; BCL_TRIM_ENV=0; fi
# (review fix: the host sets SHEPHERD_BUILD_BCL_AOT_TRIM=$BCL_TRIM_ENV for the engine, whose [k8] start line then says that SHEPHERD_K8_SPEED=0
# does not undo this build-time trim and how to get kit #7's partition)
echo "   $BCL_TRIM_NOTE"
AOT_BASE=($AOT)   # the base AOT set in the eboot's order (vita/vlx/link.sh reads it from $W/obj/aot-base-modules.txt)
[ "$ENGINE_AOT" = 1 ] && AOT+=(Shepherd.Engine)
[ "$DERIVED_AOT" = 1 ] && AOT+=(Shepherd.Engine.Derived)   # owner-local: its IL lives in $W/derived, never in $W/bcl (never packaged)
# SHEPHERD_AOT_OWNER=1: the owner modules (game / rewritten Unity code from the owner managed folder, each its own module; their objects go to
# $W/aot-owner, which the packaged-set GUID gate never reads, and register through their own table)
OWNER_FULL=(); OWNER_PART=(); OWNER_PROFILE=
if [ "$OWNER_AOT" = 1 ]; then
  OWNER_FULL_LIST=${SHEPHERD_AOT_OWNER_FULL:-}; OWNER_PART_LIST=${SHEPHERD_AOT_OWNER_PART:-$V/gameaot/owner-modules-p.txt}
  [ -n "$OWNER_FULL_LIST" ] && OWNER_FULL=($(grep -v '^#' "$OWNER_FULL_LIST" || true))
  [ -n "$OWNER_PART_LIST" ] && OWNER_PART=($(grep -v '^#' "$OWNER_PART_LIST" || true))
  OWNER_PROFILE=${SHEPHERD_AOT_OWNER_PROFILE:-$B/gameaot/profiles/owner-p.aotprofile}
  [ $(( ${#OWNER_FULL} + ${#OWNER_PART} )) -gt 0 ] || { echo "SHEPHERD_AOT_OWNER=1: no owner module listed"; exit 1; }
  [ ${#OWNER_PART} = 0 ] || [ -f "$OWNER_PROFILE" ] || { echo "SHEPHERD_AOT_OWNER=1: partial owner modules need the profile $OWNER_PROFILE (SHEPHERD_AOT_OWNER_PROFILE)"; exit 1; }
  case "$OWNER_PROFILE" in "$REPO"/*) echo "SHEPHERD_AOT_OWNER=1: the profile names the owner's game methods; it must not live in the repo ($OWNER_PROFILE)"; exit 1 ;; esac
  for a in $OWNER_FULL $OWNER_PART; do
    [ -f "$OWNER_MANAGED/$a.dll" ] || { echo "owner module $a: no $OWNER_MANAGED/$a.dll"; exit 1; }
    (( ${AOT[(Ie)$a]} )) && { echo "owner module $a is already in the AOT set"; exit 1; }
    case $a in Shepherd.*|System.*|Microsoft.*|mscorlib|netstandard) echo "owner module $a: not an owner assembly"; exit 1 ;; esac
  done
  AOT+=($OWNER_FULL $OWNER_PART)
  # what an owner image may inline: the owner modules -- the listed ones and every owner module of the game-aot study's census (proto lists:
  # a small getter of a module that is not compiled itself, as in the prototype) -- and the BCL; never Shepherd.* (no engine / derived code
  # baked into an owner image)
  OWNER_INL=(${(u)OWNER_FULL} ${(u)OWNER_PART} $(grep -hv '^#' "$V/gameaot/proto/modules-rewritten.txt" "$V/gameaot/proto/modules-game.txt" | grep -v '^Shepherd\.' || true))
  OWNER_INL=(${(u)OWNER_INL}); OWNER_INLINE_IMAGES="${(j:,:)OWNER_INL},System.*,Microsoft.*,mscorlib,netstandard"
  mkdir -p "$W/aot-owner"
fi
is_owner() { (( ${OWNER_FULL[(Ie)$1]} + ${OWNER_PART[(Ie)$1]} )); }
aotdir() { if is_owner "$1"; then echo "$W/aot-owner"; else echo "$W/aot"; fi; }
# what Shepherd.Engine's AOT may inline: itself, our other assemblies and the BCL -- never the owner's Unity / game assemblies
ENGINE_INLINE_IMAGES="Shepherd.Engine,Shepherd.Assets,Shepherd.Hooks,System.*,Microsoft.*,mscorlib,netstandard"
# what the derived module may inline: itself, the engine, our other assemblies and the BCL (never the owner's Unity / game assemblies)
DERIVED_INLINE_IMAGES="Shepherd.Engine.Derived,$ENGINE_INLINE_IMAGES"
ENGINE_DLLS=(Shepherd.Engine Shepherd.Assets Shepherd.Hooks Microsoft.Bcl.AsyncInterfaces)
export MONO_AGGRESSIVE_INLINELIMIT=${MONO_AGGRESSIVE_INLINELIMIT:-256}
MAX_MODULE_TEXT=$((28 * 1024 * 1024))

echo "== 1. engine IL (Release, portable)"
ENGINE_SRC=$REPO/engine
if [ "$DIAG" = 1 ]; then
  # DIAG: a copy of engine/ with diag/engine-diag.patch (the repo's engine sources are never touched)
  rm -rf "$W/engine-diag"; mkdir -p "$W/engine-diag"; ENGINE_SRC=$W/engine-diag/engine
  rsync -a --exclude bin --exclude obj --exclude Derived "$REPO/engine/" "$ENGINE_SRC/"
  patch -s -p1 -d "$W/engine-diag" < "$HERE/diag/engine-diag.patch" || { echo "diag/engine-diag.patch does not apply to engine/"; exit 1; }
  grep -q "BootPhase.Mark" "$ENGINE_SRC/Shepherd.Engine/Program.cs" || { echo "diag/engine-diag.patch did not apply"; exit 1; }
  echo "   diag: engine copy $ENGINE_SRC + engine-diag.patch"
fi
# PathMap (kit 3): the PDB path the PE records was the only work-dir-dependent part of the engine DLLs (Roslyn's deterministic MVID
# hashes it), so two builds of one source in two work dirs packaged different Shepherd.Engine.dll / MVIDs. Mapped, every recipe packages
# the same engine DLLs for the same source (e.g. 00091 and its MT variant 00096 differ only in host.o: same IL, same AOT objects).
# (Shepherd.Engine.Derived.dll does not depend on it: DebugType none, and it records the engine's name and version, not its MVID.)
EBOOT_PROP=-p:ShepherdEboot=1; [ "${SHEPHERD_ENGINE_TOOLS:-0}" = 1 ] && EBOOT_PROP=-p:ShepherdEboot=0
echo "   engine: $([ "$EBOOT_PROP" = -p:ShepherdEboot=1 ] && echo "SHEPHERD_EBOOT (Mac-only tests / diagnostics left out)" || echo "SHEPHERD_ENGINE_TOOLS=1: the Mac-only tests / diagnostics kept")"
PHYSCODE_PROP=(); [ "$PHYS_CODE" = 0 ] && { PHYSCODE_PROP=(-p:ShepherdPhysCode=0); echo "   engine: SHEPHERD_PHYS_CODE=0 -- the physics runtime compiled out (PhysicsOff/PhysOff.cs: kit #7's engine paths)"; }
K8_PROP=; [ "$K8_ENGINE" = 1 ] || K8_PROP=-p:ShepherdK8=0   # kit #8 boot track (SHEPHERD_K8_ENGINE, above)
echo "   engine: kit #8 boot rows' engine code $([ "$K8_ENGINE" = 1 ] && echo "compiled in (real video, preload; SHEPHERD_K8_ENGINE=1)" || echo "left out (SHEPHERD_NO_K8: kit #7's engine code; SHEPHERD_K8_ENGINE=1 adds it)")"
dotnet build "$ENGINE_SRC/Shepherd.Engine/Shepherd.Engine.csproj" -c Release -p:DisableTransitiveFrameworkReferenceDownloads=true $EBOOT_PROP $PHYSCODE_PROP $K8_PROP \
  -p:PathMap="$W/engine-art=/shepherd-art" \
  --source ~/.nuget/packages --artifacts-path "$W/engine-art" -o "$W/engine" -nologo -v q > "$W/obj/engine-build.log" 2>&1 \
  || { grep -E " error " "$W/obj/engine-build.log" | head -20; exit 1; }
rm -rf "$W/bcl"; mkdir -p "$W/bcl"                  # the packaged managed set; recreated every run (the GUID gate reads it)
cp "$PACK"/lib/net8.0/*.dll "$W/bcl/"
cp "$PACK/native/System.Private.CoreLib.dll" "$W/bcl/"
for a in $ENGINE_DLLS; do cp "$W/engine/$a.dll" "$W/bcl/"; done
if [ "$DERIVED_AOT" = 1 ]; then
  echo "== 1b. owner-local Shepherd.Engine.Derived IL, against this build's Shepherd.Engine.dll (never packaged)"
  rm -rf "$W/derived"; mkdir -p "$W/derived"
  SHEPHERD_DERIVED_OUT="$W/derived" SHEPHERD_DERIVED_ART="$W/derived-art" SHEPHERD_DERIVED_ENGINE="$W/engine/Shepherd.Engine.dll" \
    zsh "$HERE/build-derived.sh" > "$W/obj/derived-build.txt" 2>&1 || { cat "$W/obj/derived-build.txt"; exit 1; }
  [ -f "$W/derived/Shepherd.Engine.Derived.dll" ] || { cat "$W/obj/derived-build.txt"; exit 1; }
  mkdir -p "$OUT/owner-managed"; cp "$W/derived/Shepherd.Engine.Derived.dll" "$OUT/owner-managed/"
fi

echo "== 2. static AOT (full + interp entry wrappers, direct P/Invokes and icalls, no debug info)"
AOT_OPTS="full,interp,static,asmonly,direct-icalls,direct-pinvoke,nodebug,mtriple=armv7-vita-eabihf"
# device kit #5 (lever 30, vita/notes/kit5-runtime.md): SHEPHERD_AOT_O = extra Mono optimisation flags for every AOT image
# (mono-aot-cross -O=<flags>, added to driver.c's DEFAULT_OPTIMIZATIONS), e.g. "abcrem" (array bound-check removal on SSA form).
# Unset / empty = the kit #4 images. A build-time choice (the eboot holds the compiled code): there is no env-file switch; the
# switch back is a build without it. In this fork 'sched', 'leaf' and 'ssapre' do nothing (no pass reads them).
AOT_O=${SHEPHERD_AOT_O:-}
XO=(); [ -n "$AOT_O" ] && XO=("-O=$AOT_O")
case "$AOT_O" in *unsafe*|*all*) echo "SHEPHERD_AOT_O: 'unsafe' / 'all' change behaviour (no bound or overflow checks): refused"; exit 1 ;; esac
# LLVM: the triple picks ARM vs Thumb-2 (thumbv7 -> thumb_mixed); Mono adds -mattr=+vfp2,-neon,+d16 -float-abi=hard first, ours come
# last (+d32: the A9's VFPv3-D32); -exception-model=dwarf drops the unused ARM EHABI tables. opt runs Mono's default
# "default<O2>,place-safepoints" (coop polls on every backedge).
# Alignment (2026-10-13, llvm-aot.md §14): mini-llvm gives IL loads/stores through pointers their type's ABI alignment (8 for
# long/double), but managed code reads through pointers Mono only aligns to 2 or 4 (CoreLib's UnicodeEncoding.GetChars: ulongs through a
# 2-byte-aligned char*). Mono's A32 backend uses single LDR/STRs there, which the Vita allows unaligned; llc's load/store optimizer
# paired them into LDRD, which faults at any address that is not word-aligned (vlx: SIGBUS). -arm-assume-misaligned-load-store stops
# that pairing (no LDRD/STRD/LDM/STM made from separate accesses); NEON stays off (Mono's -neon) so no vector access carries an
# alignment the IR cannot guarantee and float maths keeps VFP semantics (NEON flushes denormals). Cost: +0.1 % (engine) to
# +0.5 % (CoreLib) static instructions.
LLVM_TRIPLE=armv7-vita-eabihf; [ "$AOT_LLVM" = thumb ] && LLVM_TRIPLE=thumbv7-vita-eabihf
[ -n "$AOT_LLVM" ] && AOT_OPTS="full,interp,static,asmonly,direct-icalls,direct-pinvoke,nodebug,llvm,llvm-path=$LLVMBIN,mtriple=$LLVM_TRIPLE,llvmllc=-mcpu=cortex-a9 -exception-model=dwarf -arm-assume-misaligned-load-store,mattr=+d32"
# all runtime trampolines come from CoreLib's image; running out of a pool is fatal (see vita/notes/v5-runtime.md)
CORELIB_TRAMPS=",ntrampolines=8192,nrgctx-trampolines=8192,nimt-trampolines=2048,ngsharedvt-trampolines=4096,nftnptr-arg-trampolines=4096,nunbox-arbitrary-trampolines=1024"
# game AOT: AOT'd game code takes far more specific / rgctx / IMT trampolines (the game-aot prototype's lldb counts, menu + intro to frame 5000:
# the engine-AOT set alone 7,129 specific of the 8,192; + the owner modules partial 10,084 specific, 5,146 rgctx, 1,697 IMT): x4 / x2 / x2
# (+0.8 MiB of CoreLib; SHEPHERD_CORELIB_TRAMPS overrides)
[ "$OWNER_AOT" = 1 ] && CORELIB_TRAMPS=",${SHEPHERD_CORELIB_TRAMPS:-ntrampolines=32768,nrgctx-trampolines=16384,nimt-trampolines=4096,ngsharedvt-trampolines=4096,nftnptr-arg-trampolines=4096,nunbox-arbitrary-trampolines=1024}"
# content stamps: the cross compiler + every DLL the module can depend on (CoreLib: itself; the BCL: the BCL; ours: all)
BCL_FILES=("$PACK"/lib/net8.0/*.dll "$PACK/native/System.Private.CoreLib.dll")
XTOOLS=("$X"); [ -n "$AOT_LLVM" ] && XTOOLS+=("$LLVMBIN/opt" "$LLVMBIN/llc")
DEPS_CORELIB=$(cat "$W/bcl/System.Private.CoreLib.dll" $XTOOLS | shasum -a 256 | cut -c1-16)
DEPS_BCL=$(cat $BCL_FILES $XTOOLS | shasum -a 256 | cut -c1-16)
DEPS_ALL=$(cat "$W"/bcl/*.dll $XTOOLS | shasum -a 256 | cut -c1-16)
[ "$ENGINE_AOT" = 1 ] && DEPS_ENGINE=$(cat "$W"/bcl/*.dll "$OWNER_MANAGED"/*.dll $XTOOLS | shasum -a 256 | cut -c1-16)
[ "$DERIVED_AOT" = 1 ] && DEPS_DERIVED=$(cat "$W"/bcl/*.dll "$W/derived/Shepherd.Engine.Derived.dll" "$OWNER_MANAGED"/*.dll $XTOOLS | shasum -a 256 | cut -c1-16)
[ "$OWNER_AOT" = 1 ] && DEPS_OWNER=$(cat "$W"/bcl/*.dll "$OWNER_MANAGED"/*.dll $XTOOLS ${OWNER_PROFILE:+"$OWNER_PROFILE"} | shasum -a 256 | cut -c1-16)
for f in "$W"/aot/*.dll.s(N) "$W"/aot-owner/*.dll.s(N); do a=${${f:t}%.dll.s}; (( ${AOT[(Ie)$a]} )) && [ "$(aotdir $a)" = "${f:h}" ] || { echo "   (dropping stale $a)"; rm -f "${f:h}/$a".*; }; done
for a in $AOT; do
  AD=$(aotdir $a); dll="$W/bcl/$a.dll"; s="$AD/$a.dll.s"; o="$AD/$a.dll.o"; lo="$AD/$a-llvm.o"
  [ "$a" = Shepherd.Engine.Derived ] && dll="$W/derived/$a.dll"
  is_owner $a && dll="$OWNER_MANAGED/$a.dll"
  # LLVM: llc must write an object (llvm-outfile=*.o): the Mono LLVM 16 llc for osx-arm64 crashes in its asm streamer on the
  # Mono EH table (vita/notes/llvm-aot.md)
  LOUT=; [ -n "$AOT_LLVM" ] && LOUT=",llvm-outfile=$lo"
  if is_owner $a; then EXTRA= DEPS=$DEPS_OWNER; (( ${OWNER_PART[(Ie)$a]} )) && EXTRA=",profile=$OWNER_PROFILE,profile-only"
  else
  case $a in
    System.Private.CoreLib) EXTRA=$CORELIB_TRAMPS DEPS=$DEPS_CORELIB ;;
    Shepherd.Engine) EXTRA= DEPS=$DEPS_ENGINE ;;
    Shepherd.Engine.Derived) EXTRA= DEPS=$DEPS_DERIVED ;;
    Shepherd.*|Microsoft.Bcl.*) EXTRA= DEPS=$DEPS_ALL ;;
    *) EXTRA= DEPS=$DEPS_BCL ;;
  esac
  fi
  stamp="$AD/$a.stamp"; want="$DEPS opts=$AOT_OPTS$EXTRA inl=$MONO_AGGRESSIVE_INLINELIMIT${GLW}${AOT_O:+ O=$AOT_O}${AOT_LLVM:+ armhf-cc=$LLVM_ARMHF_CC:${SHEPHERD_LLVM_ARMHF_CC:-} eh-vfp=${SHEPHERD_EH_VFP_FIX:-}}"
  if [ -f "$o" ] && { [ -z "$AOT_LLVM" ] || [ -f "$lo" ]; } && [ "$(cat "$stamp" 2>/dev/null)" = "$want" ]; then echo "   $a: up to date"; continue; fi
  rm -f "$stamp" "$lo"
  if is_owner $a; then
    # an owner module (game / rewritten Unity code, owner-local): compiled from the owner folder against the packaged set; inlines only the
    # owner modules and the BCL; no direct P/Invoke (late-bound, failing at call time as in the interpreter); partial = profile-only +
    # strict (only the profile's methods and generic class instances, their shared bodies and the interpreter's entry / gsharedvt
    # wrappers for their signatures; the rest of the module stays interpreted)
    OWNER_ENV=(SHEPHERD_AOT_INLINE_IMAGES=$OWNER_INLINE_IMAGES); (( ${OWNER_PART[(Ie)$a]} )) && OWNER_ENV+=(SHEPHERD_AOT_PROFILE_STRICT=1)
    env $OWNER_ENV MONO_PATH="$W/bcl:$OWNER_MANAGED" \
      "$X" $XO --aot=${AOT_OPTS/direct-pinvoke,/}$EXTRA,outfile="$s" "$dll" > "$AD/$a.log" 2>&1 || { tail -20 "$AD/$a.log"; exit 1; }
  elif [ "$a" = Shepherd.Engine ]; then
    # the owner's managed folder resolves the engine's Unity / game types; nothing of theirs is inlined; only the engine's own
    # __Internal export (the host checkpoint) is a direct P/Invoke (its Mac-only SDL / libc imports stay late-bound, never called)
    # the headless build links only the checkpoint export: its other __Internal imports (the GL app's shepgl_* and the pad
    # reader) stay late-bound (never called there); the GL build links them all
    DIRECT='direct-pinvokes=__Internal!shepherd_vita_engine_checkpoint'; [ "$GL" = 1 ] && DIRECT='direct-pinvokes=__Internal'
    SHEPHERD_AOT_INLINE_IMAGES=$ENGINE_INLINE_IMAGES MONO_PATH="$W/bcl:$OWNER_MANAGED" \
      "$X" $XO --aot=${AOT_OPTS/direct-pinvoke,/$DIRECT,}$LOUT,outfile="$s" "$dll" > "$W/aot/$a.log" 2>&1 || { tail -20 "$W/aot/$a.log"; exit 1; }
  elif [ "$a" = Shepherd.Engine.Derived ]; then
    # the owner-local derived module: resolves against the packaged set, its own folder and the owner's managed folder; inlines only
    # itself, the engine and the BCL; declares no P/Invoke of its own (any it reaches stays late-bound, as in the engine image)
    SHEPHERD_AOT_INLINE_IMAGES=$DERIVED_INLINE_IMAGES MONO_PATH="$W/bcl:$W/derived:$OWNER_MANAGED" \
      "$X" $XO --aot=${AOT_OPTS/direct-pinvoke,/}$LOUT,outfile="$s" "$dll" > "$W/aot/$a.log" 2>&1 || { tail -20 "$W/aot/$a.log"; exit 1; }
  else
  MONO_PATH="$W/bcl" "$X" $XO --aot=$AOT_OPTS$EXTRA$LOUT,outfile="$s" "$dll" > "$W/aot/$a.log" 2>&1 || { tail -20 "$W/aot/$a.log"; exit 1; }
  fi
  arm-vita-eabi-as -mfpu=neon -mfloat-abi=hard -o "$o" "$s"
  rm -f "$s.bc" "$s.opt.bc"                          # LLVM's intermediate bitcode (CoreLib: ~1 GB)
  echo "$want" > "$stamp"
  echo "   $a: $(grep -o 'Compiled: [0-9/]*' "$AD/$a.log" | tail -1)"
done
DER_DIR=; [ "$DERIVED_AOT" = 1 ] && DER_DIR=$W/derived   # (the derived module's own IL: an owner dir, it is not packaged)
if [ -n "$AOT_LLVM" ]; then
  # with LLVM the AOT data tables (assembly_guid, image_table) are in <M>-llvm.o, not in the .s
  python3 "$V/llvm/check_aot_guids_llvm.py" "$W/bcl" "$W/aot" ${DER_DIR:+$DER_DIR} ${OWNER_MANAGED:+$OWNER_MANAGED} > "$OUT/aot-guids.txt" || true
else
python3 "$V/hello/check_aot_guids.py" "$W/bcl" "$W/aot" ${DER_DIR:+$DER_DIR} ${OWNER_MANAGED:+$OWNER_MANAGED} > "$OUT/aot-guids.txt" || true
fi
grep -qx 'TOTAL MISMATCHES 0' "$OUT/aot-guids.txt" || { cat "$OUT/aot-guids.txt"; echo "AOT images do not match the packaged DLLs (see aot-guids.txt)"; exit 1; }
echo "   AOT GUID gate: $(tail -1 "$OUT/aot-guids.txt")"
if [ "$OWNER_AOT" = 1 ]; then
  # the owner modules: each image's own GUID against the owner DLL it was compiled from (the card's: a different DLL there is a fatal
  # "doesn't match assembly" at load), every image_table dependency against the packaged DLL of that name, else the owner DLL
  python3 "$V/gameaot/check_owner_guids.py" "$W/bcl" "$OWNER_MANAGED" "$W/aot-owner" > "$OUT/aot-guids-owner.txt" \
    || { tail -20 "$OUT/aot-guids-owner.txt"; echo "owner AOT images do not match the owner / packaged DLLs (see aot-guids-owner.txt)"; exit 1; }
  grep -qx "MODULES $(( ${#OWNER_FULL} + ${#OWNER_PART} ))" "$OUT/aot-guids-owner.txt" || { tail -3 "$OUT/aot-guids-owner.txt"; echo "owner GUID gate: module count is not the owner list's"; exit 1; }
  echo "   owner AOT GUID gate: $(tail -1 "$OUT/aot-guids-owner.txt") ($(( ${#OWNER_FULL} + ${#OWNER_PART} )) owner modules)"
fi
: > "$OUT/aot-sizes.txt"
for a in $AOT; do
  t=$(arm-vita-eabi-size "$(aotdir $a)/$a.dll.o" | tail -1 | awk '{print $1}')
  # LLVM: the module is both objects (Mono part + LLVM part); `size` text = .text + .rodata (+ .ARM.exidx/extab)
  [ -f "$W/aot/$a-llvm.o" ] && t=$((t + $(arm-vita-eabi-size "$W/aot/$a-llvm.o" | tail -1 | awk '{print $1}')))
  printf "%-36s text %10d (%6.2f MiB)\n" "$a" "$t" "$(echo "$t / 1048576" | bc -l)" >> "$OUT/aot-sizes.txt"
  [ "$t" -lt $MAX_MODULE_TEXT ] || { echo "AOT module $a text $t >= 28 MiB (bl reach margin)"; exit 1; }
done
cat "$OUT/aot-sizes.txt"
if [ "$ENGINE_AOT" = 1 ]; then
  # foreign-code gate: every method compiled into the engine image is the engine's, the BCL's (incl. its generics instantiated
  # over Unity value types: BCL code), or a runtime wrapper / trampoline -- no Unity or game method body
  if [ -n "$AOT_LLVM" ]; then
  # LLVM: vita/llvm/foreign-gate.sh checks both objects: the Mono part with the regex below (+ the p_<n>_plt_<callee>_llvm call
  # stubs it emits for LLVM callers); in the LLVM part every method is named "Shepherd_Engine_<name>" (static link:
  # mono_aot_get_method_name), BCL code and wrappers included, so it strips that prefix before testing (else a Unity body would
  # pass as Shepherd_*), and an unprefixed LLVM code symbol must be one of the module's own helpers. Its self-test also checks
  # that the regex below is still the one it copies. Exit 1 = foreign symbols (listed below), 2 = the gate could not run
  "$V/llvm/foreign-gate.sh" --self-test > "$OUT/engine-aot-gate.txt" || { cat "$OUT/engine-aot-gate.txt"; exit 1; }
  "$V/llvm/foreign-gate.sh" Shepherd.Engine "$W/aot/Shepherd.Engine.dll.o" "$W/aot/Shepherd.Engine-llvm.o" "$W/obj/engine-aot" >> "$OUT/engine-aot-gate.txt" \
    || [ $? = 1 ] || { cat "$OUT/engine-aot-gate.txt"; echo "Shepherd.Engine foreign-code gate could not run"; exit 1; }
  else
  arm-vita-eabi-nm "$W/aot/Shepherd.Engine.dll.o" | awk '$2 ~ /^[Tt]$/ {print $3}' | grep -v '^\.' > "$W/obj/engine-aot-methods.txt"
  # allowed: the engine's and the BCL's namespaces, the BCL's primitive types as Mono mangles them, runtime wrappers / PLT / unbox
  # trampolines, and the image's own data labels; anything else (a Unity / game namespace, a global-namespace game type) fails
  grep -vE '^(Shepherd_|System_|Microsoft_|Internal_|Interop_|wrapper_|plt_|ut_|unbox_|method_|mono_|jit_|got_|extra_|class_|(s?byte|u?int(16|32|64)?|u?long|u?short|single|double|bool|char|string|object|u?intptr|decimal)_)' "$W/obj/engine-aot-methods.txt" \
    | grep -vxE '(assembly_guid|assembly_name|blob|ex_info_offsets|image_table|mem_end|name_[0-9]+|plt|runtime_version|unwind_info|weak_field_indexes)' > "$W/obj/engine-aot-foreign.txt" || true
  fi
  # fid1-fixup D1: the owner-local derived code has its own module (SHEPHERD_AOT_DERIVED=1) and never sits in the engine's image; its
  # methods carry the Shepherd_Engine_Derived_ prefix, which the '^Shepherd_' test above lets through, so it is denied here (with LLVM:
  # the gate's method list covers both objects, prefixed names as Mono emits them)
  grep -E '^Shepherd_Engine_Derived_' "$W/obj/engine-aot-methods.txt" >> "$W/obj/engine-aot-foreign.txt" || true
  [ ! -s "$W/obj/engine-aot-foreign.txt" ] || { echo "Shepherd.Engine AOT image holds non-engine/BCL methods:"; head -20 "$W/obj/engine-aot-foreign.txt"; exit 1; }
  echo "   Shepherd.Engine AOT: $(wc -l < "$W/obj/engine-aot-methods.txt" | tr -d ' ') text symbols, all engine/BCL/wrappers; $(grep -o '[0-9]* inline(s) of methods outside SHEPHERD_AOT_INLINE_IMAGES refused ([0-9]* distinct methods)' "$W/aot/Shepherd.Engine.log")" | tee "$OUT/engine-aot.txt"
  for a in $AOT; do   # the BCL / Hooks / Assets (and owner) images hold no derived code either (only the derived module may)
    [ "$a" = Shepherd.Engine.Derived ] && continue
    ! arm-vita-eabi-nm "$(aotdir $a)/$a.dll.o" | awk '$2 ~ /^[Tt]$/ {print $3}' | grep -qE '^Shepherd_Engine_Derived_' || { echo "$a AOT image holds Shepherd.Engine.Derived code"; exit 1; }
    # kit #5: with LLVM, the LLVM part's method names after the module's own prefix (Shepherd.Engine's: foreign-gate.sh above)
    if [ -n "$AOT_LLVM" ] && [ "$a" != Shepherd.Engine ]; then
      pfx=${a//[^A-Za-z0-9_]/_}; [ "$a" = System.Private.CoreLib ] && pfx=corlib
      ! arm-vita-eabi-nm "$W/aot/$a-llvm.o" | awk '$2 ~ /^[Tt]$/ {print $3}' | sed "s/^${pfx}_//" | grep -qE '^Shepherd_Engine_Derived_' || { echo "$a AOT image (LLVM part) holds Shepherd.Engine.Derived code"; exit 1; }
    fi
  done
fi
if [ "$DERIVED_AOT" = 1 ]; then
  # the derived module's foreign-code gate: the same allow-list (engine, BCL, wrappers, data labels -- its own methods are Shepherd_Engine_Derived_*,
  # engine generics instantiated over its types are Shepherd_Engine_*), so no Unity / game method body is in it either
  if [ -n "$AOT_LLVM" ]; then
  # kit #5, LLVM: vita/llvm/foreign-gate.sh on both objects (the LLVM part's names checked after stripping the module's own prefix
  # Shepherd_Engine_Derived_, which every LLVM method of this module carries); exit 1 = foreign symbols, 2 = the gate could not run
  "$V/llvm/foreign-gate.sh" Shepherd.Engine.Derived "$W/aot/Shepherd.Engine.Derived.dll.o" "$W/aot/Shepherd.Engine.Derived-llvm.o" "$W/obj/derived-aot" > "$OUT/derived-aot-gate.txt" \
    || [ $? = 1 ] || { cat "$OUT/derived-aot-gate.txt"; echo "Shepherd.Engine.Derived foreign-code gate could not run"; exit 1; }
  else
  arm-vita-eabi-nm "$W/aot/Shepherd.Engine.Derived.dll.o" | awk '$2 ~ /^[Tt]$/ {print $3}' | grep -v '^\.' > "$W/obj/derived-aot-methods.txt"
  grep -vE '^(Shepherd_|System_|Microsoft_|Internal_|Interop_|wrapper_|plt_|ut_|unbox_|method_|mono_|jit_|got_|extra_|class_|(s?byte|u?int(16|32|64)?|u?long|u?short|single|double|bool|char|string|object|u?intptr|decimal)_)' "$W/obj/derived-aot-methods.txt" \
    | grep -vxE '(assembly_guid|assembly_name|blob|ex_info_offsets|image_table|mem_end|name_[0-9]+|plt|runtime_version|unwind_info|weak_field_indexes)' > "$W/obj/derived-aot-foreign.txt" || true
  fi
  [ ! -s "$W/obj/derived-aot-foreign.txt" ] || { echo "Shepherd.Engine.Derived AOT image holds non-engine/BCL methods:"; head -20 "$W/obj/derived-aot-foreign.txt"; exit 1; }
  echo "   Shepherd.Engine.Derived AOT (OWNER-LOCAL): $(wc -l < "$W/obj/derived-aot-methods.txt" | tr -d ' ') text symbols ($(grep -c '^Shepherd_Engine_Derived_' "$W/obj/derived-aot-methods.txt") its own), all engine/BCL/wrappers; $(grep -o '[0-9]* inline(s) of methods outside SHEPHERD_AOT_INLINE_IMAGES refused ([0-9]* distinct methods)' "$W/aot/Shepherd.Engine.Derived.log")" | tee "$OUT/derived-aot.txt"
fi

if [ "$OWNER_AOT" = 1 ]; then
  # the owner images hold no engine code (their inline set excludes Shepherd.*: an engine method is called, never copied in), and the
  # module manifest: what this eboot holds besides the packaged set and the exact owner DLLs it is bound to (a different DLL on the
  # card -- a restaged owner folder, other game files -- is a fatal "doesn't match assembly" at load: compare before installing)
  for a in $OWNER_FULL $OWNER_PART; do
    ! arm-vita-eabi-nm "$W/aot-owner/$a.dll.o" | awk '$2 ~ /^[Tt]$/ {print $3}' | grep -qE '^Shepherd_Engine_' || { echo "owner AOT image $a holds Shepherd.Engine code"; exit 1; }
  done
  { echo "# $TITLE_ID owner-local game AOT modules (compiled game / Unity code: never in git, never in a shareable VPK)"
    echo "# compiler $(shasum -a 256 "$X" | cut -c1-16)  profile ${OWNER_PROFILE:+$(shasum -a 256 "$OWNER_PROFILE" | cut -c1-16) $OWNER_PROFILE}  runtime $RT  owner managed $OWNER_MANAGED"
    printf "module\tmode\tassembly_guid\tdll_sha256\tcompiled\ttext_bytes\n"
    for a in $OWNER_FULL $OWNER_PART; do
      printf "%s\t%s\t%s\t%s\t%s\t%s\n" "$a" "$( (( ${OWNER_PART[(Ie)$a]} )) && echo partial || echo whole)" \
        "$(grep -A1 '^assembly_guid:' "$W/aot-owner/$a.dll.s" | sed -n 's/.*\.string "\(.*\)"/\1/p')" \
        "$(shasum -a 256 "$OWNER_MANAGED/$a.dll" | cut -c1-16)" "$(grep -o 'Compiled: [0-9/]*' "$W/aot-owner/$a.log" | tail -1 | cut -d' ' -f2)" \
        "$(arm-vita-eabi-size "$W/aot-owner/$a.dll.o" | tail -1 | awk '{print $1}')"
    done; } > "$OUT/owner-modules.tsv"
  print -l $OWNER_FULL $OWNER_PART > "$W/obj/aot-owner-modules.txt"   # the eboot's owner-module order (vita/vlx/link.sh links the same)
  echo "   owner modules: ${#OWNER_FULL} whole + ${#OWNER_PART} partial, text $(awk -F'\t' 'NR>3 {s+=$6} END {printf "%.2f MiB", s/1048576}' "$OUT/owner-modules.tsv") -> owner-modules.tsv"
else
  rm -f "$W/obj/aot-owner-modules.txt"
fi
print -l $AOT_BASE > "$W/obj/aot-base-modules.txt"   # kit #8: the base AOT set (without the engine / derived / owner modules), the eboot's order: vita/vlx/link.sh links the same

echo "== 3. P/Invoke census of the packaged managed set"
dotnet build "$V/v6/tools/AsmSize/AsmSize.csproj" -c Release -o "$W/asmsize" --artifacts-path "$W/asmsize-art" -p:DisableTransitiveFrameworkReferenceDownloads=true -nologo -v q > "$W/obj/asmsize.log" 2>&1 \
  || { tail -20 "$W/obj/asmsize.log"; exit 1; }
dotnet "$W/asmsize/AsmSize.dll" pinvokes "$W/obj/pinvokes.tsv" "$W/bcl" | sed 's/^/   /'

echo "== 4. native shim: System.Native + crypto + network refusal + zlib + generated stubs"
sed -e 's/^#cmakedefine01 \([A-Z0-9_]*\)/#define \1 0/' "$R/src/native/libs/Common/pal_config.h.in" > "$W/obj/pal_config.h"
printf '#undef HAVE_GNU_STRERROR_R\n#define HAVE_GNU_STRERROR_R 1\n#undef HAVE_STRLCPY\n#define HAVE_STRLCPY 1\n' >> "$W/obj/pal_config.h"
CFLAGS=(-O2 -g0 -Wall -Wno-unused-function -ffunction-sections -fdata-sections -D_GNU_SOURCE -DTARGET_UNIX -D__vita__
        -ffile-prefix-map="$R"=/shepherd-runtime -ffile-prefix-map="$W"=/shepherd-v6-work -ffile-prefix-map="$V"=/shepherd-vita)
NATIVE_INC=(-I"$W/obj" -I"$R/src/native/libs/System.Native" -I"$R/src/native/libs/Common" -I"$R/src/native" -I"$SHIM/crypto")
SHIM_OBJS=()
# vita_pthread_fix.c: the corrected sem_post for vitasdk's pthread-embedded (its own posts the kernel semaphore even with no waiter)
for f in "$SHIM/pal_vita.c" "$SHIM/pal_vita_misc.c" "$SHIM/vita_pthread_fix.c" "$SHIM/pal_vita_net.c" "$SHIM/pal_vita_crypto.c" "$SHIM/crypto/vita_crypto.c" \
         "$R/src/native/libs/System.IO.Compression.Native/pal_zlib.c"; do
  o="$W/obj/$(basename "$f" .c).o"
  arm-vita-eabi-gcc $CFLAGS $NATIVE_INC -I"$R/src/native/libs/System.IO.Compression.Native" -c "$f" -o "$o"
  SHIM_OBJS+=("$o")
done
arm-vita-eabi-nm -g --defined-only $SHIM_OBJS | awk 'NF==3 && $2=="T" {print $3}' | sort -u > "$W/obj/shim-defined.txt"
python3 "$SHIM/gen_stubs.py" "$W/obj/pinvokes.tsv" "$R" "$W/obj/pal_vita_stubs.c" "$W/obj/shim-defined.txt"
arm-vita-eabi-gcc $CFLAGS -c "$W/obj/pal_vita_stubs.c" -o "$W/obj/pal_vita_stubs.o"
rm -f "$W/obj/libSystem.Native.vita.a"
arm-vita-eabi-ar rcs "$W/obj/libSystem.Native.vita.a" $SHIM_OBJS "$W/obj/pal_vita_stubs.o"

GL_OBJS=()
if [ "$GL" = 1 ]; then
  echo "== 4b. GL host layer: vita/gltest/shepgl (vitaGL context/swap + the 51 GL entry points)"
  python3 "$V/gltest/tools/check_abi.py" "$REPO" "$VITASDK/arm-vita-eabi/include/vitaGL.h" "$W/obj/abi-table.md" > "$W/obj/check-abi.log" 2>&1 \
    || { tail -20 "$W/obj/check-abi.log"; echo "Gl.cs and vitaGL.h / shepgl_entries.h disagree (check_abi.py)"; exit 1; }
  # kit #5 (GXP cache): the GL stack a cached shader binary depends on, named in shepgl_gxp_identity (vita/notes/kit5-boot.md)
  GLSTACK_ID=""; for L in vitaGL vitashark SceShaccCgExt; do GLSTACK_ID+="$L $(shasum -a 256 "$VITASDK/arm-vita-eabi/lib/lib$L.a" | cut -d' ' -f1) "; done
  arm-vita-eabi-gcc -std=gnu11 -O2 -ffp-contract=off -Wall -Wextra -Wno-unused-parameter -ffunction-sections -fdata-sections \
    -DSHEPGL_GLSTACK_ID="\"${GLSTACK_ID% }\"" \
    -ffile-prefix-map="$V"=/shepherd-vita -I"$SHEPGL" -c "$SHEPGL/shepgl_vita.c" -o "$W/obj/shepgl_vita.o"
  GL_OBJS=("$W/obj/shepgl_vita.o")
fi
if [ "$AUDIO" = 1 ]; then
  echo "== 4c. audio: libshepaudio_vita.a (vita/audio/build.sh vita: its own link check) + the audio pool"
  TMPDIR=$TMPDIR bash "$V/audio/build.sh" vita > "$W/obj/shepaudio-build.log" 2>&1 || { tail -20 "$W/obj/shepaudio-build.log"; exit 1; }
  grep -E "^vita lib" "$W/obj/shepaudio-build.log" | sed 's/^/   /'
  arm-vita-eabi-gcc -std=gnu99 -O2 -Wall -Wextra -ffunction-sections -fdata-sections -ffile-prefix-map="$V"=/shepherd-vita -c "$HERE/audio_pool.c" -o "$W/obj/audio_pool.o"
  GL_OBJS+=("$W/obj/audio_pool.o")
fi
PHYS_STATE="not linked (SHEPHERD_PHYS_LINK=0 or not a play app: host.c's NULL shepphys_proc, the engine's physics levels have no core)"
[ "$PHYS_CODE" = 0 ] && PHYS_STATE="compiled out (SHEPHERD_PHYS_CODE=0: no physics runtime in the engine image -- kit #7's engine paths; libshepphys not linked; SHEPHERD_PHYS not read)"
if [ "$PHYS_LINK" = 1 ]; then
  echo "== 4d. physics: libshepphys_vita.a (vita/physics/build.sh vita: its own link check, symbol / FMA / NEON scan)"
  case "$V" in *" "*) echo "vita/physics/build.sh needs a path without a space (CMake / Ninja): build from a worktree or clone, not $V"; exit 1 ;; esac
  mkdir -p "$W/physics"
  SHEPHERD_PHYS_SRC="$V/physics" SHEPHERD_PHYS_WORK="$W/physics" SHEPHERD_PHYS_TMPDIR=$TMPDIR zsh "$V/physics/build.sh" vita > "$W/obj/shepphys-build.log" 2>&1 \
    || { tail -20 "$W/obj/shepphys-build.log"; exit 1; }
  grep -q "^check_syms: OK" "$W/obj/shepphys-build.log" || { grep -A3 "check_syms" "$W/obj/shepphys-build.log"; echo "libshepphys_vita.a failed its symbol / FMA / NEON check"; exit 1; }
  cp "$W/physics/vita/libshepphys_vita.a" "$W/obj/libshepphys_vita.a"
  PHYSLIB="$W/obj/libshepphys_vita.a"
  PHYS_STATE="libshepphys linked ($(sed -nE 's/^libshepphys_vita.a: (text [0-9]+) .*/\1 B/p' "$W/obj/shepphys-build.log"), sha256 $(shasum -a 256 "$PHYSLIB" | cut -c1-16)); the play apps' default level SHEPHERD_PHYS=$PHYS_DEFAULT (env file wins; 0 / none = kit #7), the pool block from the arena's share at level >= 1"
  echo "   $PHYS_STATE"
fi

echo "== 5. generated tables (AOT module registration, static P/Invoke symbol table)"
HOST_EXPORTS=(shepherd_vita_engine_checkpoint shepherd_vita_pin shepherd_vita_worker_mask shepherd_vita_k6 shepherd_vita_gc_pause_us shepherd_vita_k7 shepherd_vita_k7_ptr shepherd_spine_clip_api shepherd_spine_clip_start shepherd_spine_clip_tris shepherd_vita_k8)   # shepherd_vita_k8: kit #8 play track (Runtime/K8Play.cs: the measured clock, frame pacing; vita/shepherd/k8_play.inc.c); shepherd_vita_pin: the engine's core pinning (Diag/VitaHost.cs; kit 3), every build; shepherd_vita_worker_mask: k4 4-core split; shepherd_vita_k6: kit #6 Settings > Graphics rows (Runtime/VitaToggles.cs: 4th CPU Core, CPU Speed, the clock readout); shepherd_vita_gc_pause_us: kit #6 GC pause timer (Diag/SimStats.cs: each frame's stop-the-world time); shepherd_vita_k7 / _k7_ptr: kit #7 measure track (Diag/K7.cs: the black box's frame ring, the profiler block, the overlay's free memory, the BOOT numbers; vita/shepherd/k7_measure.h); shepherd_spine_clip_*: kit #7 spine track (Runtime/SpineClipNative.cs: the native clipping port, vita/shepherd/spine_clip_abi.h)
[ "$DIAG" = 1 ] && HOST_EXPORTS+=(shepherd_vita_diag_phase)   # DIAG: the engine's boot-phase marker (Diag/BootPhase.cs)
[ "$GL" = 1 ] && HOST_EXPORTS+=(shepherd_vita_pad_read shepherd_vita_touch_read shepaudio_proc shepherd_vita_k8_pad shepphys_proc)   # shepaudio_proc: the audio app's table, a NULL stub in the silent GL app; touch_read: kit #5 front touch (Icalls/VitaTouch.cs); shepherd_vita_k8_pad: kit #8 play (the right stick + rear touch); shepphys_proc: physics' binding (Physics/ShepPhys.cs) -- libshepphys's table when the eboot links it (kit #8, SHEPHERD_PHYS_LINK), else a NULL stub
[ "$GL" = 1 ] && [ "$K8_ENGINE" = 1 ] && HOST_EXPORTS+=(shepherd_vita_video_api shepherd_vita_video_open shepherd_vita_video_decode shepherd_vita_video_close)   # kit #8 boot track: the engine's real video (Runtime/K8Video.cs; host.c k8_boot.inc.c)
{ arm-vita-eabi-nm -g --defined-only "$W/obj/libSystem.Native.vita.a" $GL_OBJS | awk 'NF==3 && $2=="T" {print $3}'; printf '%s\n' $HOST_EXPORTS; } | sort -u > "$W/obj/static-candidates.txt"
# the GL app: every shepgl_* export (the engine binds shepgl_proc, _state, _status, _attach, _swap, _has_extension,
# _display_fbo, _pool_kib; the rest are harmless) and the pad reader
grep -E '^(SystemNative_|CryptoNative_|CompressionNative_|Brotli(Decoder|Encoder)|NetSecurityNative_|GlobalizationNative_|corehost_|shepherd_vita_engine_|shepherd_vita_pin$|shepherd_vita_worker_mask$|shepherd_vita_k6$|shepherd_vita_gc_pause_us$|shepherd_vita_k7$|shepherd_vita_k7_ptr$|shepherd_vita_video_(api|open|decode|close)$|shepherd_spine_clip_(api|start|tris)$|shepherd_vita_pad_read$|shepherd_vita_touch_read$|shepherd_vita_k8$|shepherd_vita_k8_pad$|shepgl_|shepaudio_proc$|shepphys_proc$|shepherd_vita_diag_phase$)' "$W/obj/static-candidates.txt" > "$W/obj/static-symbols.txt"
# every entry point the packaged BCL declares must be answerable (shim or stub)
# (the BCL's native libraries + "__Internal"; the engine's own Mac-only imports -- SDL3, libc, libSystem.B, CoreFoundation --
#  are deliberately unanswered: never bound under the Vita profile + null renderer, see checks.txt)
# (the headless build: the GL app's own __Internal imports -- shepgl_* and the pad reader, Render/ShepGl.cs and Icalls/VitaPad.cs --
#  are unanswered too: they are bound only under SHEPHERD_GL_HOST=shepgl / SHEPHERD_VITA_PAD=1, which only the GL host sets)
GLONLY='^(shepgl_|shepherd_vita_pad_read$|shepherd_vita_touch_read$|shepaudio_proc$|shepherd_vita_k8_pad$|shepphys_proc$|shepherd_vita_video_)'; [ "$GL" = 1 ] && GLONLY='^$'   # (Stage C: shepaudio_proc, Audio/ShepAudio.cs; kit #5: the touch reader, Icalls/VitaTouch.cs; kit #8 play: the rear touch reader; physics: shepphys_proc, Physics/ShepPhys.cs; kit #8 boot: the real video, Runtime/K8Video.cs)
awk -F'\t' -v g="$GLONLY" 'NR>1 && $2 ~ /^(libSystem\.(Native|Security\.Cryptography\.Native\.OpenSsl|IO\.Compression\.Native|Globalization\.Native|Net\.Security\.Native)|libhostpolicy|__Internal)$/ && !($2 == "__Internal" && $3 ~ g) {print $3}' "$W/obj/pinvokes.tsv" | sort -u > "$W/obj/declared.txt"
awk -F'\t' -v g="$GLONLY" 'NR>1 && ($2 !~ /^(libSystem\.(Native|Security\.Cryptography\.Native\.OpenSsl|IO\.Compression\.Native|Globalization\.Native|Net\.Security\.Native)|libhostpolicy|__Internal)$/ || ($2 == "__Internal" && $3 ~ g)) {print $1" -> "$2"!"$3}' "$W/obj/pinvokes.tsv" | sort -u > "$OUT/unanswered-imports.txt"
comm -23 "$W/obj/declared.txt" "$W/obj/static-symbols.txt" > "$W/obj/unanswered.txt"
[ ! -s "$W/obj/unanswered.txt" ] || { echo "declared but not in the static table:"; cat "$W/obj/unanswered.txt"; exit 1; }
{
  echo "/* GENERATED by vita/shepherd/build.sh */"
  echo "#include <stddef.h>"
  echo "#include <mono/utils/mono-vita.h>"
  for a in $AOT; do echo "extern void *mono_aot_module_${${a//./_}//-/_}_info;"; done
  echo "void mono_aot_register_module (void **aot_info);"
  echo "void shepherd_register_aot_modules (void) {"
  for a in $AOT; do is_owner $a || echo "\tmono_aot_register_module (mono_aot_module_${${a//./_}//-/_}_info);"; done
  echo "}"
  echo "const char *const shepherd_aot_module_names [] = {"; for a in $AOT; do is_owner $a || echo "\t\"$a\","; done; echo "\tNULL };"
  if [ "$OWNER_AOT" = 1 ]; then
    # the owner-local game modules: their own table, registered by host.c (SHP_GAME_AOT) unless SHEPHERD_GAME_AOT=0
    echo "void shepherd_register_owner_aot_modules (void) {"
    for a in $OWNER_FULL $OWNER_PART; do echo "\tmono_aot_register_module (mono_aot_module_${${a//./_}//-/_}_info);"; done
    echo "}"
    echo "const char *const shepherd_owner_aot_module_names [] = {"; for a in $OWNER_FULL $OWNER_PART; do echo "\t\"$a\","; done; echo "\tNULL };"
  fi
  while read -r s; do echo "extern void $s (void);"; done < "$W/obj/static-symbols.txt"
  echo "const MonoVitaStaticSymbol shepherd_static_symbols [] = {"
  while read -r s; do echo "\t{ \"$s\", (void *) $s },"; done < "$W/obj/static-symbols.txt"
  echo "};"
  echo "const int shepherd_static_symbols_count = sizeof (shepherd_static_symbols) / sizeof (shepherd_static_symbols [0]);"
} > "$W/obj/shepherd_tables.c"
mkdir -p "$W/obj/include/mono/utils"
cp "$R/src/mono/mono/utils/mono-vita.h" "$W/obj/include/mono/utils/"
MONO_INC=(-I"$W/obj/include" -I"$R/src/native/public")
arm-vita-eabi-gcc $CFLAGS $MONO_INC -c "$W/obj/shepherd_tables.c" -o "$W/obj/shepherd_tables.o"
BUILD_ID=${SHEPHERD_BUILD_ID:-"$(date -u +%Y%m%d-%H%M%S)-$(cd "$R" && git rev-parse --short HEAD)"}   # SHEPHERD_BUILD_ID: a fixed id for byte-for-byte build comparisons
GL_DEFS=(); [ "$GL" = 1 ] && GL_DEFS=(-DSHEPHERD_GL -I"$SHEPGL")
DIAG_OBJS=(); DIAG_LD=()
if [ "$DIAG" = 1 ]; then
  # DIAG: counting thunks for every P/Invoke target + AOT direct icall (ld --wrap), the file/thread C wrappers, the host's diag part
  GL_DEFS+=(-DSHP_DIAG -I"$HERE/diag")
  for a in $AOT; do arm-vita-eabi-nm -u "$W/aot/$a.dll.o"; done | awk '{print $2}' | sort -u > "$W/obj/aot-undefined.txt"
  python3 "$HERE/diag/gen_diag_wraps.py" "$W/obj/static-symbols.txt" "$W/obj/aot-undefined.txt" "$W/obj/diag" | sed 's/^/   /'
  arm-vita-eabi-gcc -c "$W/obj/diag/diag_wraps.S" -o "$W/obj/diag/diag_wraps_S.o"
  arm-vita-eabi-gcc $CFLAGS -I"$HERE/diag" -c "$W/obj/diag/diag_wraps.c" -o "$W/obj/diag/diag_wraps_c.o"
  DIAG_OBJS=("$W/obj/diag/diag_wraps_S.o" "$W/obj/diag/diag_wraps_c.o"); DIAG_LD=("@$W/obj/diag/wraps.rsp")
fi
[ "$AUDIO" = 1 ] && GL_DEFS+=(-DSHEPHERD_AUDIO -I"$V/audio/include" -I"$HERE")
[ "$PHYS_LINK" = 1 ] && GL_DEFS+=(-DSHP_PHYS_LINK -DSHP_PHYS_DEFAULT="\"$PHYS_DEFAULT\"")   # kit #8: libshepphys's table in shepphys_proc (phys_host.inc.c)
[ "$MT" = 1 ] && GL_DEFS+=(-DSHP_MT_DEFAULT)   # the MT variant: SHEPHERD_MT_RENDER=1 by default (engine_env)
[ "$TEST_CLOCK" = 1 ] && GL_DEFS+=(-DSHP_TEST_CLOCK)   # the measurement variant: the frame clock + pad.txt, "boot 1460 0" by default
# kit #7 spine track: Spine clipping's native port (owner-local engine/Derived/native/spine_clip_impl.h) only in the owner-only derived-AOT apps
# (their eboots already hold derived code); every other build compiles host.c's stub (the engine then runs the game's managed clipping).
# SHEPHERD_SPINE_CLIP_PORT=0 builds the stub everywhere.
SPCLIP_IMPL="$REPO/engine/Derived/native/spine_clip_impl.h"; SPCLIP_STATE="stub (the managed clipping)"
if [ "$DERIVED_AOT" = 1 ] && [ -f "$SPCLIP_IMPL" ] && [ "${SHEPHERD_SPINE_CLIP_PORT:-1}" != 0 ]; then
  GL_DEFS+=(-DSHP_SPINE_CLIP_DERIVED -ffp-contract=off -I"$HERE" -I"$REPO/engine/Derived/native"); SPCLIP_STATE="the owner-local native port (engine/Derived/native, sha256 $(shasum -a 256 "$SPCLIP_IMPL" | cut -c1-16))"
fi
echo "   spine clipping in host.c: $SPCLIP_STATE"
arm-vita-eabi-gcc $CFLAGS -Wextra -Wno-unused-parameter -Wno-sign-compare $MONO_INC -I"$COMMON" $GL_DEFS $APP_DEFS -DSHP_BUILD_ID="\"$BUILD_ID\"" -DSHP_BCL_AOT_TRIM="\"$BCL_TRIM_ENV\"" -DSHP_NEWLIB_HEAP_MB=$NEWLIB_MB -DSHP_LOG_NAME="\"$LOG_NAME\"" -DSHP_LOG_PREV_NAME="\"$LOG_PREV_NAME\"" -DSHP_ENV_NAME="\"$ENV_NAME\"" -DSHP_ARGS_NAME="\"$ARGS_NAME\"" -c "$HERE/host.c" -o "$W/obj/host.o"
arm-vita-eabi-gcc -O2 -I"$COMMON" -c "$COMMON/debugScreen.c" -o "$W/obj/debugScreen.o"

echo "== 6. link"
AOT_OBJS=(); for a in $AOT; do AOT_OBJS+=("$(aotdir $a)/$a.dll.o"); [ -n "$AOT_LLVM" ] && AOT_OBJS+=("$W/aot/$a-llvm.o"); done
MONO_LIBS=("$RT/mono/mini/libmonosgen-2.0.a" "$RT/mono/mini/libmono-component-marshal-ilgen-static.a"
           "$RT/mono/mini/libmono-component-debugger-stub-static.a" "$RT/mono/mini/libmono-component-hot_reload-stub-static.a"
           "$RT/mono/mini/libmono-component-diagnostics_tracing-stub-static.a")
STUB_LIBS=(-lSceLibKernel_stub -lSceKernelThreadMgr_stub -lSceKernelModulemgr_stub -lSceSysmem_stub -lSceIofilemgr_stub
           -lSceRtc_stub -lSceDisplay_stub -lSceCtrl_stub -lSceProcessmgr_stub -lScePower_stub)   # ScePower: every build sets the clocks
# the GL app: vitaGL + vitaShaRK (libshacccg is loaded at run time from ur0:data, never linked or shipped) and their stubs
# (the list vita/gltest/CMakeLists.txt links; SceShaccCg_stub resolves against the owner-extracted module)
GL_LIBS=(); [ "$GL" = 1 ] && GL_LIBS=(-lvitaGL -lvitashark -lSceShaccCgExt -lmathneon -ltaihen_stub -lSceShaccCg_stub -lSceKernelDmacMgr_stub
  -lSceGxm_stub -lSceAppMgr_stub -lSceCommonDialog_stub -lSceIme_stub -lSceSysmodule_stub -lSceAppUtil_stub -lstdc++ -lSceTouch_stub -lSceVshBridge_stub -lSceVideodec_stub)   # SceTouch: kit #5, the front touch screen (host.c)
# kit #8.1: the engine module defines the SceShaccCg functions itself (k8_latelibs.c): no stub, so a new compiler import fails the link
[ "$LAUNCHER" = 1 ] && GL_LIBS=(${GL_LIBS:#-lSceShaccCg_stub})
# the audio app: shepaudio (mixer, sceAudioOut thread, sceAudiodec AAC) and its imports
[ "$AUDIO" = 1 ] && GL_LIBS=("$AUDIOLIB" -lSceAudio_stub -lSceAudiodec_stub $GL_LIBS)
# kit #8: libshepphys (C++ without exceptions / RTTI: operator new / delete from libsupc++; libm, libc after it as for everything)
[ "$PHYS_LINK" = 1 ] && GL_LIBS=("$PHYSLIB" -lsupc++ $GL_LIBS)
# kit #7 (track probes): the measurement app's device probes (vita/shepherd/k7_probes.inc.c) -- the NGS check (SceNgsUser, loaded at run
# time through SceSysmodule, already in GL_LIBS) and the PMU counters of the big-pages probe (ScePerf, idem); no other app imports them
[ "$TEST_CLOCK" = 1 ] && GL_LIBS+=(-lSceNgsUser_stub -lScePerf_stub)
# kit #4 M2: GC.Collect's icall goes through host.c's governor (__wrap_ves_icall_System_GC_InternalCollect; SHEPHERD_GC_GOVERN)
GCGOV_LD=(-Wl,--wrap=ves_icall_System_GC_InternalCollect)
# kit #8 boot track (the clean boot screen): vitaGL's own splash goes through shepgl_vita.c's __wrap_invoke_splashscreen, which leaves it
# out when the host asks (shepgl_config.no_vgl_splash) -- every GL eboot; without the flag vitaGL's splash runs as in kit #7
SPLASH_LD=(); [ "$GL" = 1 ] && SPLASH_LD=(-Wl,--wrap=invoke_splashscreen)
# kit #8 boot track: a launcher build links the same objects as the ENGINE MODULE -- no crt0 (k8_module.c's module_start is the entry and
# k8_entry starts newlib), the toolchain's crti / crtbegin / crtend / crtn kept (newlib's __libc_init_array calls _init); the process
# parameters (sceUserMainThreadStackSize) live in the launcher eboot
ENTRY_LD=(-Wl,--undefined=sceUserMainThreadStackSize); CRT_PRE=(); CRT_POST=(); MOD_OBJS=()
if [ "$LAUNCHER" = 1 ]; then
  arm-vita-eabi-gcc $CFLAGS -Wextra -c "$HERE/k8_module.c" -o "$W/obj/k8_module.o"
  # kit #8.1: the module's late libraries (SceShaccCg, SceNet) as first-use trampolines -- a module loaded with sceKernelLoadStartModule
  # may import only libraries already loaded (module-load-libs.txt; the device refused kit #8's module with 0x8002D003)
  arm-vita-eabi-gcc $CFLAGS -Wextra -c "$HERE/k8_latelibs.c" -o "$W/obj/k8_latelibs.o" || exit 1
  MOD_OBJS=("$W/obj/k8_module.o" "$W/obj/k8_latelibs.o")
  ENTRY_LD=(-nostartfiles -Wl,-e,module_start -Wl,--undefined=module_start -Wl,--undefined=module_stop)
  CRT_PRE=("$(arm-vita-eabi-gcc -print-file-name=crti.o)" "$(arm-vita-eabi-gcc -print-file-name=crtbegin.o)")
  CRT_POST=("$(arm-vita-eabi-gcc -print-file-name=crtend.o)" "$(arm-vita-eabi-gcc -print-file-name=crtn.o)")
fi
arm-vita-eabi-gcc -Wl,-q -Wl,--gc-sections $ENTRY_LD -Wl,-Map="$W/obj/shepherd_menu.map" -o "$W/obj/shepherd_menu.elf" \
  $CRT_PRE "$W/obj/host.o" $MOD_OBJS $DIAG_OBJS "$W/obj/shepherd_tables.o" "$W/obj/debugScreen.o" $GL_OBJS $AOT_OBJS $DIAG_LD $GCGOV_LD $SPLASH_LD \
  -Wl,--start-group $MONO_LIBS "$W/obj/libSystem.Native.vita.a" -Wl,--end-group \
  $GL_LIBS -lz -lpthread -lm -lc $STUB_LIBS $CRT_POST 2> "$W/obj/link.log" \
  || { grep -E "undefined reference|error" "$W/obj/link.log" | sed 's/.*undefined reference to/undefined:/' | sort | uniq -c | sort -rn | head -60; exit 1; }
grep -i "warning" "$W/obj/link.log" | grep -v "has a LOAD segment with RWX permissions" | head -10 || true
arm-vita-eabi-nm "$W/obj/shepherd_menu.elf" > "$W/obj/elf-symbols.txt"
NEED_SYMS=(sceUserMainThreadStackSize _newlib_heap_size_user _pthread_stack_default_user)
[ "$LAUNCHER" = 1 ] && NEED_SYMS=(_newlib_heap_size_user _pthread_stack_default_user)   # (the launcher eboot holds sceUserMainThreadStackSize)
for sym in $NEED_SYMS; do
  grep -qE " [DdRr] $sym\$" "$W/obj/elf-symbols.txt" || { echo "$sym missing from the ELF (discarded by --gc-sections?)"; exit 1; }
done
if [ "$LAUNCHER" = 1 ]; then
  grep -qE " [Tt] module_start$" "$W/obj/elf-symbols.txt" || { echo "module_start missing from the engine module"; exit 1; }
  grep -qE " [DdRr] sceUserMainThreadStackSize$" "$W/obj/elf-symbols.txt" && { echo "the engine module must not carry sceUserMainThreadStackSize (the launcher eboot's)"; exit 1; }
fi
# 0 strong undefined symbols (weak toolchain references only)
arm-vita-eabi-nm -u "$W/obj/shepherd_menu.elf" > "$OUT/elf-undefined.txt" || true
STRONG=$(awk '$1=="U"' "$OUT/elf-undefined.txt" | wc -l | tr -d ' ')
[ "$STRONG" = 0 ] || { echo "strong undefined symbols:"; awk '$1=="U"' "$OUT/elf-undefined.txt"; exit 1; }
echo "   nm -u: $(wc -l < "$OUT/elf-undefined.txt" | tr -d ' ') undefined, all weak ($(awk '{print $2}' "$OUT/elf-undefined.txt" | tr '\n' ' '))"
NEWLIB_VAL=$(arm-vita-eabi-objdump -s --start-address=0x$(awk '$3=="_newlib_heap_size_user"{print $1}' "$W/obj/elf-symbols.txt") \
  --stop-address=$(printf '0x%x' $((0x$(awk '$3=="_newlib_heap_size_user"{print $1}' "$W/obj/elf-symbols.txt") + 4))) "$W/obj/shepherd_menu.elf" \
  | awk '/^ [0-9a-f]+ /{print $2; exit}' | sed -E 's/(..)(..)(..)(..)/\4\3\2\1/')
echo "   newlib heap (_newlib_heap_size_user): $((16#$NEWLIB_VAL / 1048576)) MiB"
arm-vita-eabi-size -A "$W/obj/shepherd_menu.elf" | grep -E "^\.(text|rodata|data|bss)|Total" > "$OUT/elf-sections.txt"
arm-vita-eabi-size "$W/obj/shepherd_menu.elf" | tee "$OUT/elf-size.txt"

echo "== 7. SELF + VPK"
if [ -n "$AOT_LLVM" ]; then
  # the runtime binary-searches each module's Mono EH table in table order and takes [first, last+len) as its LLVM code range:
  # llc puts every method in its own retained section, so check (in the linked ELF) that ld kept them in table order, the
  # Thumb bits, and that every CFA op/register is one Mono's unwinder decodes
  python3 "$V/llvm/check_llvm_eh.py" "$W/aot" "$W/obj/shepherd_menu.elf" > "$OUT/llvm-eh.txt" || { grep -E "FAIL|TOTAL" "$OUT/llvm-eh.txt"; exit 1; }
  echo "   LLVM EH tables: $(grep '^TOTAL' "$OUT/llvm-eh.txt")"
fi
if [ "$LAUNCHER" = 1 ]; then
  # kit #8: the engine module (a user module: module_start / module_stop, no exported libraries) and the tiny launcher eboot
  printf 'shepherd_engine:\n  attributes: 0\n  version:\n    major: 1\n    minor: 0\n  main:\n    start: module_start\n    stop: module_stop\n' > "$W/obj/shepherd_module.yml"
  vita-elf-create -v -e "$W/obj/shepherd_module.yml" "$W/obj/shepherd_menu.elf" "$W/obj/shepherd_menu.velf" > "$W/obj/velf.log" 2>&1 || { tail -20 "$W/obj/velf.log"; exit 1; }
  vita-make-fself -c "$W/obj/shepherd_menu.velf" "$W/obj/shepherd.suprx"
  # kit #8.1: the libraries the module imports (vlx's module loader and the checks compare them with module-load-libs.txt)
  grep -o 'library_nid=0x[0-9A-Fa-f]* ([A-Za-z0-9_]*)' "$W/obj/velf.log" | sed -E 's/.*\((.*)\)/\1/' | sort -u > "$OUT/module-imports.txt"
  cp "$OUT/module-imports.txt" "$W/obj/module-imports.txt"
  echo "   engine module: $(stat -f %z "$W/obj/shepherd.suprx") bytes (app0:module/shepherd.suprx)"
  LAUNCHER_LIBS=(-lSceLibKernel_stub -lSceKernelThreadMgr_stub -lSceSysmem_stub -lSceKernelModulemgr_stub -lSceIofilemgr_stub -lSceProcessmgr_stub
                 -lSceDisplay_stub -lSceCtrl_stub -lSceSysmodule_stub -lSceVideodec_stub -lSceAudio_stub -lgcc)
  # (launcher.o is kept: vita/vlx/link.sh links it with the module's objects into one vlx ELF)
  arm-vita-eabi-gcc -O2 -Wall -Wextra -Wno-unused-parameter -ffreestanding -fno-builtin -ffunction-sections -fdata-sections \
    -ffile-prefix-map="$V"=/shepherd-vita -I"$HERE" -I"$COMMON" $APP_DEFS -DSHP_BUILD_ID="\"$BUILD_ID\"" -DSHP_LOG_NAME="\"$LOG_NAME\"" -DSHP_LOG_PREV_NAME="\"$LOG_PREV_NAME\"" \
    -DSHP_ENV_NAME="\"$ENV_NAME\"" -c "$HERE/launcher.c" -o "$W/obj/launcher.o" || exit 1
  arm-vita-eabi-gcc -nostdlib -nostartfiles -Wl,-q -Wl,--gc-sections -Wl,-e,_start -Wl,--undefined=sceUserMainThreadStackSize -Wl,-Map="$W/obj/launcher.map" \
    "$W/obj/launcher.o" -o "$W/obj/launcher.elf" $LAUNCHER_LIBS 2> "$W/obj/launcher-link.log" || { cat "$W/obj/launcher-link.log" | head -40; exit 1; }
  arm-vita-eabi-nm "$W/obj/launcher.elf" | grep -qE " [DdRr] sceUserMainThreadStackSize$" || { echo "the launcher lost sceUserMainThreadStackSize"; exit 1; }
  arm-vita-eabi-nm "$W/obj/launcher.elf" | grep -qE " [TtDdBb] (malloc|_newlib_heap_size_user|_init_vita_newlib|mono_aot_module_.*_info)$" && { echo "the launcher must hold no newlib / runtime / AOT code"; exit 1; }
  vita-elf-create -v "$W/obj/launcher.elf" "$W/obj/launcher.velf" > "$W/obj/launcher-velf.log" 2>&1 || { tail -20 "$W/obj/launcher-velf.log"; exit 1; }
  vita-make-fself -c "$W/obj/launcher.velf" "$W/obj/eboot.bin"
  echo "   launcher eboot: $(stat -f %z "$W/obj/eboot.bin") bytes ($(arm-vita-eabi-size "$W/obj/launcher.elf" | tail -1 | awk '{print "text "$1", data "$2", bss "$3}'))"
else
vita-elf-create -v "$W/obj/shepherd_menu.elf" "$W/obj/shepherd_menu.velf" > "$W/obj/velf.log" 2>&1 || { tail -20 "$W/obj/velf.log"; exit 1; }
vita-make-fself -c "$W/obj/shepherd_menu.velf" "$W/obj/eboot.bin"
fi
vita-mksfoex -s TITLE_ID=$TITLE_ID -s APP_VER=$APP_VER -d ATTRIBUTE2=12 "$TITLE" "$W/obj/param.sfo"
echo "   param.sfo: $TITLE_ID APP_VER $APP_VER"
mkdir -p "$HERE/sce_sys"
[ -f "$HERE/sce_sys/icon0.png" ] || python3 "$V/hello/make_icon.py" "$HERE/sce_sys/icon0.png"
PACKED=(); for f in "$W"/bcl/*.dll; do PACKED+=(-a "$f=managed/${f:t}"); done
# every VPK carries the LiveArea set (8-bit indexed PNGs; vita/probe's known-good template/bg/startup) -- the diag apps had it first
# (devfail1); since the integration branch every recipe does, and step 8's VPK gate checks it
LA=$V/probe/sce_sys/livearea/contents
LIVEAREA=(-a "$LA/template.xml=sce_sys/livearea/contents/template.xml" -a "$LA/bg.png=sce_sys/livearea/contents/bg.png"
          -a "$LA/startup.png=sce_sys/livearea/contents/startup.png")
ICON=$HERE/sce_sys/icon0.png
[ "$DIAG" = 1 ] && { python3 "$V/hello/diag/make_diag_icon.py" "$W/obj/icon0.png" menu; ICON=$W/obj/icon0.png; }   # DIAG: own icon
# out/eboot.bin first, then the VPK (never a stale .vpk: removed, packed fresh, and newer than both eboot copies)
cp "$W/obj/eboot.bin" "$OUT/eboot.bin"
rm -f "$OUT/$VPK"
MODPACK=(); [ "$LAUNCHER" = 1 ] && { MODPACK=(-a "$W/obj/shepherd.suprx=module/shepherd.suprx"); cp "$W/obj/shepherd.suprx" "$OUT/shepherd.suprx"; }   # kit #8: the engine module
vita-pack-vpk -s "$W/obj/param.sfo" -b "$W/obj/eboot.bin" $PACKED $MODPACK -a "$ICON=sce_sys/icon0.png" $LIVEAREA "$OUT/$VPK" > /dev/null

echo "== 8. checks: param.sfo, imports, VPK contents"
# the import allow-list: exactly the libraries each build is meant to import (anything else fails the build)
# (headless: the 11 libraries of v6-vita-build.md + ScePower (the clocks, every build since 2026-10-13); GL adds vitaGL's
#  GXM/display/compiler/DMA/budget/common-dialog imports and taiHEN for vitaShaRK's SceShaccCgExt only -- see check_vpk.py --allow-shacccg-ext)
ALLOW=SceLibKernel,SceThreadmgr,SceSysmem,SceIofilemgr,SceRtcUser,SceProcessmgr,SceLibRng,SceCtrl,SceDisplayUser,SceThreadmgrCoredumpTime,SceNet,ScePower
GL_CHECK=()
[ "$DERIVED_AOT" = 1 ] && GL_CHECK+=(--owner-derived --require-derived)   # the eboot links the derived module: an owner-local VPK (3b may not be vacuous)
[ "$OWNER_AOT" = 1 ] && GL_CHECK+=(--owner-game-aot "$W/obj/aot-owner-modules.txt")   # ... and the owner's game modules: their images in the eboot, their DLLs not in the VPK
[ "$GL" = 1 ] && { ALLOW=$ALLOW,SceGxm,SceGxmInternalForVsh,SceSharedFb,SceDisplay,SceShaccCg,SceDmacmgr,SceAppMgrUser,SceCommonDialog,SceModulemgr,taihen,SceTouch,SceVshBridge,SceVideodecUser,SceSysmodule; GL_CHECK+=(--allow-shacccg-ext); }   # SceTouch: kit #5 (the front panel)
[ "$AUDIO" = 1 ] && ALLOW=$ALLOW,SceAudio,SceAudiodecUser   # sceAudioOut (the output thread) + sceAudiodec (AAC streams)
[ "$TEST_CLOCK" = 1 ] && ALLOW=$ALLOW,SceSysmodule,SceNgs,ScePerf   # kit #7: the measurement app's NGS check + PMU counters (loaded at run time)
[ "$JIT_APP" = 1 ] && GL_CHECK+=(--allow-forvm)   # kit #8: the JIT app's ForVM code memory (only the five functions, only wanted by the tier)
# kit #8: a launcher build -- the module's imports are the recipe's (above); the launcher's own list (no newlib: kernel, files, display, pad,
# the decoder module, the video decoder, audio, the module loader); the VPK's module/shepherd.suprx is the fself of the module ELF
[ "$LAUNCHER" = 1 ] && GL_CHECK+=(--launcher "$W/obj/launcher.elf" --module-yml "$W/obj/shepherd_module.yml" \
  --module-load-libs "$HERE/module-load-libs.txt" \
  --launcher-allow-libs SceLibKernel,SceThreadmgr,SceSysmem,SceIofilemgr,SceProcessmgr,SceDisplayUser,SceDisplay,SceCtrl,SceSysmodule,SceVideodecUser,SceAudio,SceModulemgr,SceThreadmgrCoredumpTime)
python3 "$HERE/check_vpk.py" --allow-livearea --vpk "$OUT/$VPK" --sfo "$W/obj/param.sfo" --elf "$W/obj/shepherd_menu.elf" --map "$W/obj/shepherd_menu.map" \
  --bcl "$W/bcl" --pack "$PACK" --engine "$W/engine" --unanswered "$OUT/unanswered-imports.txt" --title-id "$TITLE_ID" --allow-libs "$ALLOW" $GL_CHECK \
  --forbid "$REPO/runtime/ManagedVita" --forbid "${SHEPHERD_GAME_DATA}/Managed" \
  --forbid "$REPO/runtime/vitapack/menu" --forbid "$REPO/runtime/audiobake" --forbid "$REPO/runtime/derived" --derived-src "$REPO/engine" \
  > "$OUT/checks.txt" || { cat "$OUT/checks.txt"; exit 1; }
cat "$OUT/checks.txt"
# the VPK gate, every recipe: LiveArea set present, every PNG 8-bit indexed, TITLE_ID / TITLE, eboot.bin == this build, the .vpk
# newer than it, managed/ == this build's work/bcl in both directions (sha256)
MODNEED=(); [ "$LAUNCHER" = 1 ] && MODNEED=(module/shepherd.suprx)
python3 "$V/hello/diag/check_diag_vpk.py" --bcl="$W/bcl" --bcl-all "$OUT/$VPK" "$W/obj/eboot.bin" $TITLE_ID "$TITLE" \
  managed/Shepherd.Engine.dll managed/System.Private.CoreLib.dll $MODNEED > "$OUT/vpk-check.txt" || { cat "$OUT/vpk-check.txt"; exit 1; }
echo "   $(tail -1 "$OUT/vpk-check.txt")"
[ "$DIAG" = 1 ] && { grep -E "^[[:space:]]*(warning|error|Warning|Error)[: ]" "$W/obj/velf.log" | head -5 || true; }
IL=0; for f in "$W"/bcl/*.dll; do IL=$((IL + $(stat -f %z "$f"))); done
{
  echo "build $BUILD_ID"
  echo "elf text/data/bss: $(arm-vita-eabi-size "$W/obj/shepherd_menu.elf" | tail -1 | awk '{print $1" / "$2" / "$3}')"
  echo "eboot.bin (compressed fself): $(stat -f %z "$OUT/eboot.bin") bytes"
  [ "$LAUNCHER" = 1 ] && echo "kit #8 launcher build: eboot.bin = the tiny launcher (launcher.elf $(arm-vita-eabi-size "$W/obj/launcher.elf" | tail -1 | awk '{print "text "$1" / data "$2" / bss "$3}')); app0:module/shepherd.suprx = the engine module ($(stat -f %z "$W/obj/shepherd.suprx") bytes compressed)"
  echo "$VPK: $(stat -f %z "$OUT/$VPK") bytes ($TITLE_ID, \"$TITLE\", log $LOG_NAME)"
  echo "managed IL packaged ($(ls "$W"/bcl/*.dll | wc -l | tr -d ' ') assemblies: linux-arm BCL + engine; ${#AOT} AOT-compiled): $IL bytes"
  echo "AOT text total: $(awk '{s+=$3} END {printf "%d bytes (%.2f MiB)", s, s/1048576}' "$OUT/aot-sizes.txt")"
  echo "static P/Invoke table: $(wc -l < "$W/obj/static-symbols.txt" | tr -d ' ') symbols; packaged BCL declares $(wc -l < "$W/obj/declared.txt" | tr -d ' ') entry points, all answered"
  echo "newlib heap: $NEWLIB_MB MiB"
  echo "kit #8 engine code (real video, preload): $([ "$K8_ENGINE" = 1 ] && echo "compiled in (SHEPHERD_K8_ENGINE=1)" || echo "left out (SHEPHERD_K8_ENGINE=0: kit #7's engine code)")"
  echo "spine clipping (kit #7): $SPCLIP_STATE"
  echo "$BCL_TRIM_NOTE"
  echo "physics (kit #8): $PHYS_STATE"
  echo "runtime: $RT ($(arm-vita-eabi-nm "$RT/mono/mini/libmonosgen-2.0.a" 2>/dev/null | grep ' [DdBb] mono_shepherd_interp_cicache_mode$' > /dev/null && echo 'with the kit #5 AOT->interpreter call-info cache: vita/toolchain/build-runtime-k5.sh' || echo 'no call-info cache: a kit #4 runtime'))"
  [ -n "$AOT_O" ] && echo "AOT optimisation flags (SHEPHERD_AOT_O, kit #5 lever 30): -O=$AOT_O on every image"
  [ -n "$AOT_LLVM" ] && echo "LLVM ($AOT_LLVM$([ "$K6_LLVM_DEFAULT" = 1 ] && echo ', the kit #6 default: SHEPHERD_AOT_LLVM=0 builds without')): armhf float-argument fix $LLVM_ARMHF_CC; exception fix (compiler A + runtime B/C) $LLVM_EH_VFP${LLVM_EH_VFP_WHY:+ ($LLVM_EH_VFP_WHY)}; runtime $RT"
  [ "$JIT_APP" = 1 ] && echo "baseline JIT tier (kit #8, SHEPHERD_JIT_APP=1): runtime $RT ($(grep -h '^patch: k8-' "${RT%/obj}/K8-RUNTIME.txt" | tr '\n' ' ')); the app's SHEPHERD_JIT default 1, SHEPHERD_JIT=0 in $ENV_NAME = the kit #7 interpreter + AOT path"
  [ "$OWNER_AOT" = 1 ] && echo "owner game AOT (OWNER-LOCAL, not packaged): ${#OWNER_FULL} whole + ${#OWNER_PART} partial modules, text $(awk -F'\t' 'NR>3 {s+=$6} END {printf "%d bytes (%.2f MiB)", s, s/1048576}' "$OUT/owner-modules.tsv"); CoreLib pools$CORELIB_TRAMPS; SHEPHERD_GAME_AOT=0 in $ENV_NAME skips them"
} > "$OUT/build-info.txt"
cat "$OUT/build-info.txt"
NFD=()
[ -n "$AOT_LLVM" ] && [ "$LLVM_ARMHF_CC" = 1 ] && [ "$TITLE_ID" != SHPD00191 ] && [ "$TITLE_ID" != SHPD00192 ] && [ "$K6_LLVM_DEFAULT" != 1 ] && NFD+=("SHEPHERD_AOT_LLVM=$AOT_LLVM with the float-argument fix: this recipe's LLVM build takes the default app's TITLE_ID $TITLE_ID (installing it would replace that app) and was not vlx-checked as a device app. The owner A/B LLVM apps are SHPD00191 (the measurement recipe) and SHPD00192 (the play app MT recipe): vita/notes/kit5-llvm.md.")
[ -n "$AOT_LLVM" ] && [ "$LLVM_EH_VFP" != 1 ] && NFD+=("SHEPHERD_AOT_LLVM=$AOT_LLVM without the exception fix ($LLVM_EH_VFP_WHY): an exception caught above an LLVM frame hands that frame corrupted callee-saved d8-d15 (stock ARM Mono: the managed throw trampolines point ctx->fregs one double low, the unwinder restores d8-d15 on iOS only and truncates them to 32 bits). Under vlx the kit #5 review's ABI test fails 24 checks; the menu's SkeletonAnimationLOD NRE is caught through LLVM frames that keep floats in d8/d9. vita/notes/kit5-llvm.md \"Review fixes\".")
[ -n "$AOT_LLVM" ] && [ "$LLVM_ARMHF_CC" != 1 ] && NFD+=("SHEPHERD_AOT_LLVM=$AOT_LLVM: float/double arguments of managed calls cross the LLVM <-> Mono-backend / interpreter boundary in the wrong registers (the Mono LLVM 16 tools' Mono calling convention passes them in core registers on ARM; Mono's armhf ABI uses VFP registers). Under vlx the engine-AOT + LLVM menu fails at once (UnityEngine.Rect's constructor gets garbage, then a runtime assertion at frame 536); the BCL-only LLVM menu passes but is exposed the same way. vita/notes/llvm-aot.md §14.")
if [ ${#NFD} -gt 0 ]; then
  { echo "NOT FOR A DEVICE: $VPK ($TITLE_ID), build $BUILD_ID"; for x in $NFD; do echo "- $x"; done
    echo "Install it only after the cause is fixed and the vlx menu run (vita/vlx) reaches PASS END frame 1460 with the interpreted build's draw stream."; } > "$OUT/NOT-FOR-DEVICE.txt"
  echo "   WARNING: NOT FOR A DEVICE -- see $OUT/NOT-FOR-DEVICE.txt"
else rm -f "$OUT/NOT-FOR-DEVICE.txt"; fi
ls -la "$OUT/$VPK" "$OUT/eboot.bin"
