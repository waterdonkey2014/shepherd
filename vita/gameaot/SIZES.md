# Game / Unity AOT study: SIZES (2026-10-07, branch game-aot @ ff30827)

Owner-local AOT of the assemblies that today always run in the interpreter: the rewritten Unity modules (runtime/ManagedVita)
and the game's own and third-party assemblies, each compiled as ITS OWN AOT module for the Vita (armv7, coop, full + interp,
static), measured whole and profile-guided-partial, link-checked as one owner-local eboot with the engine-AOT set.
Nothing ran on a Vita; nothing was published; the shareable default VPK and `vita/shepherd/build.sh` were not touched (no VPK
was built at all). Tooling: `vita/gameaot/` (README: `vita/gameaot/SIZES.md` = this file). Outputs (owner-local, compiled
game / Unity code): `$SHEPHERD_WORK/gameaot/sizes/`.

## Headline

| | Mono A32 backend (build.sh recipe) | Mono LLVM Thumb-2 (vita/llvm recipe) |
|---|---:|---:|
| owner modules the menu + intro load | **87** (27 rewritten Unity modules, 59 game / third-party, 1 derived engine) | same |
| their IL | 12.75 MiB of IL, 189,889 methods with a body (32.07 MiB of DLL files) | |
| **full AOT, all 87: text + rodata** | **171.33 MiB** (Assembly-CSharp 42.57 does not assemble: see gates) | **136.39 MiB** (links) |
| full AOT, the 27 rewritten Unity modules only | 35.84 MiB | 30.52 MiB |
| full AOT, UnityEngine.CoreModule alone (needs a compiler fix) | 7.06 MiB | 5.49 MiB |
| **partial AOT, all 87** (`profile-only` from a menu + intro run, `no-instances`) | **18.09 MiB** | **15.51 MiB** |
| partial AOT, the 27 rewritten Unity modules | 3.06 MiB | 2.82 MiB |
| linked owner-local eboot (loaded = text+rodata+data+bss): engine-AOT set alone | 47.70 MiB | 38.22 MiB |
| … + 27 rewritten modules, full | 83.89 MiB | 69.44 MiB |
| … + all owner modules, full | 177.58 MiB (86: without Assembly-CSharp) | 177.87 MiB (87) |
| … + all 87, partial | **66.07 MiB** | **54.27 MiB** |
| GC heap cap left (partition of v6-vita-build.md §4: cap = 189 − eboot MiB; the menu needs ≈ 87) | engine-AOT 141.3; +rewritten 105.1; +all full ≈ 11 (**does not fit**); +partial **122.9** | 150.8; 119.6; ≈ 11 (**does not fit**); **134.7** |
| what fails to AOT | UnityEngine.CoreModule: **mono-aot-cross crashes** (SIGBUS, both backends) on `UnsafeUtility.EnumEquals<T>`'s gsharedvt body; fixed by a 1-line fork guard (patch file, not applied to the shared fork). Assembly-CSharp: compiles (98,154/98,155) but **92,960 `bl` out of range** at assembly (42.57 MiB > the ±32 MiB A32 reach). 2,366 methods not compiled, 2,249 of them gshared failures in 22 modules (interpreted at run time) | the same CoreModule crash (same fix). Assembly-CSharp builds: 30.73 MiB text+rodata (> the 28 MiB gate as written, but its Mono part is 2.23 MiB and the method tables decode correctly: 13,222 linker veneers in the all-87 eboot) |
| gates | per-module 28 MiB: only Assembly-CSharp fails (A32 42.57, Thumb 30.73); owner GUID gate 0 mismatches (87 modules, both variants, full and partial); **engine foreign-code gate unchanged: 0 foreign** (29,107 A32 / 36,766 Thumb symbols; 1,184 refused inline sites, 309 methods); LLVM EH / method-table check 100/100 modules OK (all-87 Thumb eboot and partial Thumb eboot); vita-elf-create accepts every eboot | |
| build time (M4 Pro, shared, niced; one run each, indicative) | all 87 full: 22 s compile + 24 s assemble; partial 7 + 2 s | full 775 s compile (Assembly-CSharp 231 s, **3.7 GB RSS**), partial 42 s; link 70 s + vita-elf-create 163 s for the all-87 eboot |

**Reading it.** Whole-module AOT of everything the menu + intro load is 136–171 MiB of code and leaves ≈ 11 MiB of GC heap
cap against a measured menu need of ≈ 87: it does not fit, and most of it is dead code (UIElementsModule: 11.27 MiB A32 for 19
executed methods; Unity.Mathematics 7.38 MiB for 1; MessagePack 4.88 MiB for 0). Whole-module AOT of the 27 rewritten Unity
modules fits (cap 105 / 120 MiB, margin 18 / 33 MiB) but spends 31–36 MiB mostly on UIElements. **Profile-guided partial AOT
of every owner module costs +16–18 MiB of eboot** (cap 123 / 135 MiB, margin 36 / 48), compiles every method the run executed
(the engine's hop callees Rect.get_x/y/width/height, Vector3.Min/Max, Vector4..ctor, Bounds..ctor, Color.white … are in it), and
of that ≈ 4 MiB is the gsharedvt bodies upstream `profile-only` still compiles for every generic method of an image. The menu's
need is the only measured one: the intro's heap is unknown and far larger (gameplay-survey.md: 547.6 MiB live at 64 bits before
any intro pack or diet), so every MiB here competes with that.

## 1. Which modules (derived from runs, not from the folder)

- Engine for the runs: this branch published exactly as `vita/v6/sim.sh publish` does (Release, osx-arm64, self-contained Mono,
  invariant globalization) into `sizes/build/pubmono`.
- Run: `vita/gameaot/census-run.sh run mono-intro 4200 census` (and `... aotprof`): the vita-sim shape (osx-arm64 Mono 8.0.29
  **interpreter**, coop, the Vita GC shape, `SHEPHERD_PROFILE=vita`, null renderer, `SHEPHERD_AUDIO=off`) of this branch's engine,
  the gameplay survey's pad (menu → Play → new game → QuoteScreen → **Game Biome Intro** at f2935, then move/attack to f4200),
  no pack (the intro is outside the menu pack: bundles read through symlinks into the owner's Data, read only; a private merged
  Managed/). 7 errors / 3 exceptions, scenes level0 → Main Menu (f806) → QuoteScreen → Game Biome Intro (5,605 GameObjects).
  `runtime/persistent` md5-identical before and after; the Mac output muted throughout.
- `assemblies.txt` at exit: 143 assemblies (45.24 MiB of DLLs): 53 BCL, 3 engine (VPK), **87 owner** = `census/owner-modules-all.txt`.
  76 of them are already loaded by the menu (the menu-speed base run `aa1-base-interp`, same commit); intro-only (11):
  Boxophobic.AtmosphericHeightFog.Runtime, Boxophobic.Utils.Scripts, Org.OpenAPITools, Pathfinding.ClipperLib,
  Pathfinding.Ionic.Zip.Reduced, SocketIOUnityAssembly, Unity.Timeline, UnityEngine.GridModule, UnityEngine.SpriteMaskModule,
  UnityEngine.SubsystemsModule, UnityEngine.UnityWebRequestAssetBundleModule.
- A CoreCLR run of the same boot loaded 139 with a different owner set (no Shepherd.Engine.Derived replay on CoreCLR: the game's
  own Febucci all-assembly scan runs), so the Mono interpreter run is the one used.
- **Module split** (exactly):
  - rewritten Unity modules (runtime/ManagedVita, 27): FMODUnity, GalaxyCSharp, com.steamworks.net, UnityEngine.{AnimationModule,
    AssetBundleModule, AudioModule, CoreModule, GridModule, IMGUIModule, InputLegacyModule, InputModule, JSONSerializeModule,
    ParticleSystemModule, Physics2DModule, PhysicsModule, SpriteMaskModule, SpriteShapeModule, SubsystemsModule,
    TextCoreFontEngineModule, TextRenderingModule, TilemapModule, UIElementsModule, UIModule, UnityWebRequestAssetBundleModule,
    UnityWebRequestModule, VideoModule, XRModule};
  - game / third-party (59, the game's own unmodified DLLs; includes Unity packages and the unrewritten UnityEngine.dll,
    UnityEngine.SharedInternalsModule, UnityEngine.TextCoreTextEngineModule, UnityEngine.UI): Assembly-CSharp,
    Assembly-CSharp-firstpass, Rewired_Core, Unity.TextMeshPro, spine-unity, DOTween (+ .Modules, DOTweenPro, DOTweenPro.Scripts),
    AstarPathfindingProject, Newtonsoft.Json, MessagePack, Sirenix.*, Unity.InputSystem, Unity.Mathematics, Unity.Collections, … (all in the appendix);
  - derived engine (1): Shepherd.Engine.Derived (owner-local by construction: build-derived.sh).

## 2. How it was compiled

- Inputs (snapshots, 14:48): `inputs/bcl` = the Vita linux-arm 8.0.31 BCL + CoreLib + this branch's Shepherd.Engine / Assets / Hooks /
  Microsoft.Bcl.AsyncInterfaces (dotnet build Release); `inputs/owner-managed` = stage-owner.sh's layout (the game's Managed minus
  BCL names + runtime/ManagedVita + Shepherd.Engine.Derived; ManagedVita byte-identical to the menu-speed and LLVM studies' snapshots).
- Compilers: snapshots of `obj/cross-vita` (sha256 14b6280a…, = build.sh's, = the LLVM study's nollvm) and `obj/cross-vita-llvm`
  (+ its opt/llc). `vita/gameaot/aot-module.sh <nollvm|thumb> <module>`: build.sh's options
  `full,interp,static,asmonly,direct-icalls,nodebug,mtriple=armv7-vita-eabihf`, `MONO_AGGRESSIVE_INLINELIMIT=256`, MONO_PATH = bcl : owner;
  Thumb adds vita/llvm's `llvm,mtriple=thumbv7-vita-eabihf,llvmllc=-mcpu=cortex-a9 -exception-model=dwarf,mattr=+neon,mattr=+d32,llvm-outfile=`.
  Deliberate differences from the engine module: **no `direct-pinvoke`** (an owner module's P/Invokes would become undefined
  symbols; late-bound they fail at call time exactly as in the interpreter today — the census found none left in the 87 anyway:
  the rewriter turned FMOD / Steam / Galaxy into stubs), **no inline allowlist** (an owner module may inline the BCL, the engine and
  other owner modules: all of it is owner-local or ours), and the print-only `print-skipped,stats`.
- Partial: `GA_EXTRA="profile=<menu-intro.aotprofile>,profile-only,no-instances"`. The profile comes from a new Mac-only profiler
  module, `vita/gameaot/aotprof/shepherd_aotprof.c` (the official AOT profiler is not in the osx-arm64 pack): every method the
  interpreter transformed in the menu + intro run, 32,507 methods (5,208 wrappers skipped), 5,884 classes, 83 images, in the
  `.aotprofile` format aot-compiler.c reads. Generic instances are written as their definitions (see §6).
- Sizes: ELF section sizes of the objects (`.text*` / `.rodata*` / `.data*` / `.bss*`, Mono part + LLVM part); "gate" = text +
  rodata = build.sh's 28 MiB measure. Assembly-CSharp's A32 size comes from a measure-only copy of its `.s` with every `bl <label>`
  replaced by a 4-byte `.long 0` (same layout, not linkable).
- Link checks: `vita/gameaot/link-gameaot.sh <variant> <name> <module list>`: the 13-module engine-AOT set compiled from the same
  inputs (vita/llvm/aot-vita.sh engine, SHEPHERD_LLVM_DIR = this study) + the owner modules, each registered as its own module in a
  regenerated `shepherd_tables.c` (build.sh's, owner modules appended), the LLVM study's host / shim / runtime-archive snapshots,
  build.sh's link line → ELF checks, vita-elf-create, vita-make-fself.

## 3. Results (totals; every module in the appendix)

| set | A32 compiled | A32 text | Thumb compiled | Thumb text Mono / LLVM | Thumb rodata | Thumb gate | bss A32 / Thumb |
|---|---:|---:|---:|---:|---:|---:|---:|
| rewritten Unity modules (27) | 103,615/104,139 | 35.84 | 113,605/114,129 | 2.96 / 19.15 | 8.41 | 30.52 | 0.32 / 0.67 MiB |
| game / third-party (59) | 302,884/304,726 | 134.85 | 332,791/334,633 | 9.77 / 70.41 | 25.00 | 105.19 | 1.09 / 2.22 |
| derived engine (1) | 1,229/1,229 | 0.64 | 1,780/1,780 | 0.03 / 0.41 | 0.25 | 0.68 | 0.01 / 0.02 |
| **all 87** | 407,728/410,094 | **171.33** | 448,176/450,542 | 12.76 / 89.97 | 33.66 | **136.39** | 1.41 / 2.90 |
| all minus Assembly-CSharp (86) | 309,574/311,939 | 128.76 | 349,344/351,709 | 10.53 / 65.87 | 29.26 | 105.65 | 1.06 / 2.22 |
| menu set (76) | 395,044/397,304 | 165.76 | 431,604/433,864 | 12.39 / 86.56 | 31.92 | 130.87 | |
| **partial, all 87** | 26,942/27,004 | **18.09** | 26,942/27,004 | 5.87 / 4.54 | 5.10 | **15.51** | 0.23 / 0.48 |
| partial, rewritten (27) | 5,557/5,559 | 3.06 | | 1.21 / 0.60 | 1.01 | 2.82 | |
| partial, game / third-party (59) | 21,372/21,432 | 15.01 | | 4.66 / 3.93 | 4.09 | 12.68 | |

The ten largest full modules (A32 / Thumb gate MiB; executed methods): Assembly-CSharp 42.57 / 30.73 (3,180 of 80,015),
UIElementsModule 11.27 / 8.67 (19), Rewired_Core 7.52 / 5.77 (2,087), Unity.Mathematics 7.38 / 1.89 (1), CoreModule 7.06 / 5.49
(1,599), Unity.InputSystem 6.69 / 4.83 (177), MessagePack 4.88 / 3.73 (0), Newtonsoft.Json 4.84 / 3.64 (576), Unity.ProBuilder
4.79 / 3.40 (8), Assembly-CSharp-firstpass 4.22 / 3.30 (138). A32 code is ~13 bytes per IL byte; Thumb-2 text is 0.60× the A32 text (0.80×
counting the tables LLVM moves to `.rodata`; Unity.Mathematics 0.26×: its struct math).

### What fails to AOT, and why

1. **UnityEngine.CoreModule (rewritten): the compiler crashes** — `mono-aot-cross` dies with SIGBUS 0.2 s in, A32 and LLVM alike.
   lldb: `mono_local_cprop` (local-propagation.c:543) writing `defs[sreg]` with a garbage negative vreg while compiling
   `Unity.Collections.LowLevel.Unsafe.UnsafeUtility:EnumEquals<T_GSHAREDVT>`; with `-O=-consprop,-copyprop` it becomes the
   assertion `ins->opcode >= MONO_CEE_LAST` (method-to-ir.c:12758, mono_handle_global_vregs). Cause: Unity's IL for
   `EnumEquals<T>(T, T) where T : struct, IConvertible` is `(long)lhs == (long)rhs` on a generic T (valid only for enums);
   in the gsharedvt body T is a STACK_VTYPE, `type_from_op` maps `conv.i8` of a vtype to opcode offset 0, the raw CEE opcode stays
   in the IR, and the later passes read garbage. Full AOT always compiles the gsharedvt version (MONO_OPT_GSHAREDVT is forced
   on for full AOT), so the module cannot be AOT-compiled at all with today's compiler. **Fix**: `vita/gameaot/fork-gsharedvt-unop.patch`
   — one guard in `ADD_UNOP`: if the opcode is still a raw CEE opcode, `UNVERIFIED` (in a gsharedvt method that is
   MONO_EXCEPTION_GENERIC_SHARING_FAILED: "falling back to instantiation"; valid IL never reaches it). Built privately (a shared
   clone of the fork + its working-tree diff + the patch → `sizes/fork-work/obj-cross-vita{,-llvm}`; the shared fork and obj/ are
   untouched; `git apply --check` against the shared fork passes). Proof it is inert elsewhere: DOTween, Unity.TextMeshPro,
   UnityEngine.UI A32 `.s` byte-identical except the 16-byte AOT id; DOTween Thumb `-llvm.o` disassembly identical. With it:
   CoreModule 23,997/24,397 (A32) / 24,534/24,934 (Thumb), 399 gshared failures, EnumEquals<T> among them.
2. **Assembly-CSharp, A32: does not assemble.** Compiles in 7 s (98,154/98,155, 602 MiB RSS), the `.s` is 217 MB, and gas
   reports 92,960 "branch out of range": 71,486 `bl .Lp_N` (calls to the module's PLT, emitted after all methods), 21,462 `bl .Lm_N`
   (direct calls to methods, and the method-address table) and 11 `bl ut_N` (the unbox-trampoline table) — the two tables are
   tables of `bl` instructions the runtime decodes. The module's text is 42.57 MiB; A32 `bl` reaches ±32 MiB. Splitting the assembly would mean modifying the game
   DLL (ruled out). It assembles and links as LLVM Thumb-2 (§4) or as a partial module (4.46 MiB A32).
3. **Not compiled**: 2,366 methods of the 410,094 (A32), 2,249 of them "Skip (gshared failure)" in 22 modules: Unity.Collections
   1,384 (unsafe generic native containers), CoreModule 399, Unity.InputSystem 120, UIElementsModule 93, MessagePack 67,
   Unity.Profiling.Core 31, Unity.Burst 23, Sirenix.Serialization 22, ParticleSystemModule 20, Rewired_Core 16, UnityEngine.UI 12,
   Unity.TextMeshPro 12, …; the other 117 are skipped without a printed reason. They run in the interpreter, as today.
   No "Unable to compile" in any module; no owner module declares a P/Invoke.

## 4. Gates and branch reach

| gate | result |
|---|---|
| 28 MiB per module (text + rodata, build.sh) | A32: every module passes except Assembly-CSharp (42.57). Thumb: except Assembly-CSharp (30.73). Next largest: UIElementsModule 11.27 / 8.67 |
| A32 branch reach (Mono backend: PLT calls and the bl tables inside one module, ±32 MiB, resolved by gas) | the real limit behind the 28 MiB gate: Assembly-CSharp fails (above); all other modules < 12 MiB |
| Thumb-2 / LLVM | Assembly-CSharp's Mono part (fallback methods, PLT, tables) is 2.23 MiB and its A32 tables reach the whole 24.10 MiB LLVM part (A32 BL/BLX ±32 MiB); the LLVM code's own calls are relocations: ld added **13,222 long-branch veneers** in the all-87 eboot (149 in the engine-only Thumb eboot, 170 with the partial owner set). `vita/llvm/check_llvm_eh.py` on the all-87 eboot: **100/100 modules OK** (every method-table entry decodes to its method, no veneer in a table, EH table order = address order, 532,464 FDEs), likewise on the partial Thumb eboot (112,634 FDEs). So for LLVM the binding limit is the Mono part's A32 table reach (±32 MiB to the module's LLVM methods), not 28 MiB; the 28 MiB gate as written would reject Assembly-CSharp |
| owner GUID gate (`vita/gameaot/check_owner_guids.py`: self vs the owner DLL, image_table vs the packaged DLL else the owner DLL) | **0 mismatches**: A32 86 modules (+ CoreModule fixed), Thumb 87, both partial sets 87. The packaged-set gates (`check_aot_guids*.py`) report every owner module as "self MISMATCH" (they look only in the VPK set) and the `.s` reader trips on a non-UTF-8 byte in an image_table blob (UnicodeDecodeError): the new script handles both |
| binding | 25 rewritten modules record **Shepherd.Hooks**' MVID (their icall stubs): a VPK whose Shepherd.Hooks changes invalidates them; no owner module references Shepherd.Engine; Shepherd.Engine.Derived references CoreLib, Linq, CoreModule |
| engine foreign-code gate (`vita/llvm/foreign-gate.sh`, self-test first) | **0 foreign**: A32 29,107 code symbols, Thumb 36,766 (17,736 prefixed LLVM names checked after stripping); 1,184 refused inline sites (309 methods). The engine image is compiled exactly as before; with the Unity modules AOT'd as their own modules those refused sites become engine → PLT → CoreModule AOT calls instead of interpreter entries, without any Unity code in the engine image |
| link | every eboot links with 0 strong undefined (9 weak), every module info symbol defined (13 engine-set + the owner modules), vita-elf-create accepts it (A32: 98–99 long-branch veneers in every variant) |

## 5. Memory budget (365 MiB, ATTRIBUTE2 = 12)

Partition as built (v6-vita-build.md §4): newlib 128 MiB fixed at link, 20 MiB stacks + 6 MiB safety kept free, Mono arena = the
rest, GC cap = arena − 22 (8 card table + 10 + 4) ⇒ **cap = 189 − eboot (MiB)**. Today's need (engine-pass-speed.md §8): GC ≈ 87 MiB
(0.74 × the 117 MiB clean cap), newlib peak ≈ 100–110 MiB (projected use 187–197 MiB). Owner AOT code lives in the eboot.

| eboot (loaded, measured link) | MiB | cap | margin over the ≈ 87 MiB GC need | free RAM after the projected use (365 − eboot − 187…197 − 8 − 26) |
|---|---:|---:|---:|---:|
| default VPK (engine interpreted; v6 note) | 38.8 | 150.2 | 63 | 95–105 |
| engine-AOT A32 (SHPD00011 today) | 47.70 | 141.3 | 54 | 86–96 |
| engine-AOT Thumb-2 (LLVM) | 38.22 | 150.8 | 64 | 96–106 |
| A32 + 27 rewritten Unity modules, full | 83.89 | 105.1 | 18 | 50–60 |
| Thumb + 27 rewritten, full | 69.44 | 119.6 | 33 | 65–75 |
| A32 + 86 owner modules, full (no Assembly-CSharp) | 177.58 | 11.4 | **−76** | −44…−34 |
| Thumb + all 87, full | 177.87 | 11.1 | **−76** | −44…−34 |
| **A32 + all 87, partial** | **66.07** | **122.9** | **36** | 68–78 |
| **Thumb + all 87, partial** | **54.27** | **134.7** | **48** | 80–90 |

- What AOT gives back: the interpreter no longer transforms an AOT'd method. In the menu + intro run the owner modules' methods
  used **2.76 MiB of interpreter IR (3.46 MiB with the untiered copies)** at 64 bits, plus their InterpMethod records (12,865
  methods): roughly 3–5 MiB of newlib on the Vita, the same for full and partial AOT (both cover every executed method). It only
  becomes GC cap if `SHEPHERD_NEWLIB_MB` is lowered by as much. IL is still read whole (metadata), unchanged.
- Data/bss of the owner modules (GOT etc.): full 1.4 / 2.9 MiB, partial 0.23 / 0.48 MiB (included above).
- The margins are against the **menu's** need only.

## 6. Partial AOT: what the 15.5–18 MiB is, and caveats

- Per module the partial build keeps every profiled method of the image (the menu + intro executed 12,865 owner methods; e.g.
  Assembly-CSharp 3,174 → 2,766 compiled "Normal"), the profiled generic instances the compiler can resolve, and — what upstream
  `profile-only` does not filter — the **gsharedvt body of every generic method of the image** (9,475 of the 26,942 compiled
  methods; UIElementsModule: 19 profiled methods, 1,241 gsharedvt bodies → 1.49 MiB; Unity.Collections 1,425 → 1.15 MiB) plus
  per-image tables sized by the whole image (the method-address table has one entry per method of the image).
  Without `no-instances` upstream also keeps every inflated method it enumerates: **85.24 MiB** A32 for all 87 — unusable.
  The sibling "mechanics" study in this worktree (`vita/gameaot/mechanics/fork-partial-aot.patch`, SHEPHERD_AOT_PROFILE_STRICT) is
  working on that filter in the fork; its note that `profile-only` also drops the gsharedvt_out_sig / interp_in wrappers the
  interpreter needs to call AOT code applies here: the measured partial modules hold 141 wrappers, a runnable one needs the
  wrappers of its compiled signatures back — in the full builds those wrappers are 41,627 = 5.96 MiB for 410k methods, i.e.
  ≈ 0.4–0.6 MiB at the partial set's 27k (estimate). So: runnable partial ≈ 16–19 MiB with today's compiler, ≈ 12–15 MiB if the
  gsharedvt bodies of unprofiled generic methods are dropped too (an estimate from the 3.9 MiB of gsharedvt symbols).
- The profile names generic-class methods by their generic definition (the compiler matches name + parameter count on a gtd) and
  generic-method instances not at all (their signature differs from the definition's); executed methods the profile cannot name
  stay interpreted. The hop callees the engine needs (CoreModule Rect getters, Vector3.Min/Max, Vector4..ctor, Bounds..ctor,
  Color.white, Vector3 −, sqrMagnitude) are in the CoreModule partial module (A32 0.92 / Thumb 0.82 MiB).
- A hotter cut is possible: the menu-speed base profile of the idle window (`p-idle-noinl`, interpreter inliner off) calls only
  1,209 owner methods (CoreModule 317, Rewired 305, Assembly-CSharp 131 …); not built here.
- An executed-bodies estimate from the full builds' symbol sizes (sizes.tsv `exec_*`, the census names mangled like
  aot-compiler.c `get_debug_sym`, 77 % of the executed methods matched) gives ≈ 6 MiB for all 87 modules: the floor a perfect profile-only
  filter approaches.

## 7. Build time and host memory (one run each, niced, on a Mac shared with two other workflows: indicative, not a speed claim)

| | compile (sum over modules, CPU-serial) | largest | peak RSS |
|---|---:|---|---:|
| full A32, 87 | 22 s + 24 s assembling | Assembly-CSharp 7 s + 6 s | 602 MiB (Assembly-CSharp), 188 (UIElements) |
| full Thumb, 87 (2 jobs) | 775 s | Assembly-CSharp 231 s | **3.7 GB** (Assembly-CSharp), 2.4 GB (UIElements) |
| partial A32 / Thumb, 87 | 7 s / 42 s | Assembly-CSharp 1 s / 10 s | 118 MiB / 541 MiB |
| link + vita-elf-create | A32 all-86: 4 s + 61 s; Thumb all-87: 70 s + 163 s; partial: 1–5 s + 24–53 s | | |
| eboot.bin (compressed fself) | engine-AOT A32 15.6 MB → +partial 21.9 MB, +all 56.7 MB; Thumb 19.5 → 26.8 / 100.6 MB | | |

## 8. Hygiene

- Every owner AOT output (compiled game / Unity code, and the eboots that hold it) is under `$SHEPHERD_WORK/gameaot/sizes/`
  (`aot-*`, `link/*`, `engset/`); `aot-module.sh` and `link-gameaot.sh` refuse an output dir inside the repo. No VPK was built;
  `vita/shepherd/build.sh`, `vita/shepherd/out` and the shareable default VPK (SHPD00006, 12 AOT modules: CoreLib, System.Linq,
  System.Collections, System.Collections.Concurrent, System.Console, System.Memory, System.ObjectModel,
  System.Runtime.InteropServices, System.Threading, Shepherd.Assets, Shepherd.Hooks, Microsoft.Bcl.AsyncInterfaces) are exactly as
  at ff30827. A game-AOT eboot would be a third owner-local variant beside SHPD00011: engine-AOT set + owner modules, registered
  by name in its own generated table, never in `vita/shepherd/out`, never packed with the shareable eboot (its tables name game
  assemblies). A build.sh variant would need: the owner module list, no direct-pinvoke / no allowlist for them, the owner GUID
  gate, the fork fix for CoreModule, LLVM for Assembly-CSharp (or partial) — not written as a patch here (the link script shows
  the recipe).
- Git (branch game-aot only): scripts, the profiler source, the fork patch and this note; no DLL, object, profile, method list or
  log. The fork fix is a patch file; the shared fork `$SHEPHERD_WORK/runtime` and `obj/cross-vita*` are untouched
  (a `git clone --shared` + the fork's working-tree diff in `sizes/fork-work/`).
- The real game was only read (Data through symlinks, Managed copied); the real CotL save dir was never touched (runs copy
  `runtime/persistent`, md5-identical at the end); the Mac output was muted at every run start and at the end; nothing was
  downloaded.

## 9. Files

- `vita/gameaot/`: `aot-module.sh` (one module, nollvm / thumb / arm, GA_EXTRA for partial), `aot-all.sh`, `link-gameaot.sh`,
  `check_owner_guids.py`, `sizes_report.py` (→ `sizes/sizes.tsv`, `sizes/sizes-tables.md`), `census-run.sh` + `pad-main.txt`
  (the menu + intro runs), `aotprof/` (the AOT-profile profiler module), `fork-gsharedvt-unop.patch`, `SIZES.md`.
- `$SHEPHERD_WORK/gameaot/sizes/`: `census/` (assembly lists, IL sizes, the census, `menu-intro.aotprofile` + its method
  list, module lists), `aot-{nollvm,thumb}[-gsvtfix|-partialni|-partial|-measure]/`, `engset/aot/engine-{nollvm,thumb}/`,
  `link/<variant>-<name>/checks.txt` (+ `llvm-eh.txt`), `logs/` (batch logs, GUID gates, foreign gate), `fork-work/` (private
  fork clone + compiler builds), `tools/` (compiler snapshots, profiler dylibs).

Rerun: `vita/gameaot/census-run.sh stage && census-run.sh run mono-intro 4200 census && census-run.sh run aotprof-intro 4200 aotprof`;
`aot-all.sh nollvm|thumb <list>` (CoreModule: `GA_X_NOLLVM=… / GA_X_LLVM=…` = the fixed compilers, `GA_TAG=gsvtfix`); partial:
`GA_EXTRA="profile=<p>,profile-only,no-instances" GA_TAG=partialni aot-all.sh …`; `link-gameaot.sh …`; `sizes_report.py`.

## Appendix: per module

Generated by `vita/gameaot/sizes_report.py` (full per-variant tables with totals: `$SHEPHERD_WORK/gameaot/sizes/sizes-tables.md`; machine-readable: `$SHEPHERD_WORK/gameaot/sizes/sizes.tsv`). Executed = methods of that image the menu + intro interpreter census transformed.

| module | kind | first loaded | IL KiB | methods with IL | A32 compiled | A32 text | A32 data/bss | Thumb text Mono / LLVM | Thumb rodata | Thumb gate | Thumb data/bss | partial A32 | partial Thumb gate | executed (menu+intro) | not compiled (A32) |
|---|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|
| Assembly-CSharp | game/third-party | menu | 6197 | 80015 | 98154/98155 | 42.57 **>28** | 0.4 / 364 | 2.23 / 24.10 | 4.40 | 30.73 **>28** | 3.7 / 691 | 4.46 | 3.60 | 3180 | as: branch out of range (size from the measure-only object); 1 |
| UnityEngine.UIElementsModule | rewritten | menu | 658 | 9050 | 29232/29325 | 11.27 | 0.4 / 122 | 1.25 / 5.36 | 2.06 | 8.67 | 0.6 / 213 | 1.49 | 1.39 | 19 | 93 (gshared failure 93) |
| Rewired_Core | game/third-party | menu | 749 | 13238 | 20758/20774 | 7.52 | 0.4 / 75 | 0.88 / 3.70 | 1.18 | 5.77 | 0.8 / 130 | 1.99 | 1.67 | 2087 | 16 (gshared failure 16) |
| Unity.Mathematics | game/third-party | menu | 404 | 9338 | 11485/11485 | 7.38 | 0.4 / 3 | 0.16 / 1.53 | 0.19 | 1.89 | 0.4 / 12 | 0.07 | 0.07 | 1 |  |
| UnityEngine.CoreModule | rewritten | menu | 577 | 11126 | 23997/24397 | 7.06 | 0.4 / 60 | 0.64 / 3.54 | 1.31 | 5.49 | 0.4 / 115 | 0.92 | 0.82 | 1599 | compiler crash (SIGBUS); built with the gsharedvt-unop fix; 400 (gshared failure 399) |
| Unity.InputSystem | game/third-party | menu | 404 | 5751 | 16059/16179 | 6.69 | 0.4 / 50 | 0.51 / 3.23 | 1.08 | 4.83 | 0.5 / 96 | 0.57 | 0.52 | 177 | 120 (gshared failure 120) |
| MessagePack | game/third-party | menu | 147 | 2392 | 10427/10498 | 4.88 | 0.4 / 47 | 0.57 / 2.08 | 1.08 | 3.73 | 0.5 / 83 | 0.73 | 0.67 | 0 | 71 (gshared failure 67) |
| Newtonsoft.Json | game/third-party | menu | 275 | 4060 | 11018/11022 | 4.84 | 0.4 / 46 | 0.36 / 2.40 | 0.89 | 3.64 | 0.6 / 83 | 0.60 | 0.50 | 576 | 4 (gshared failure 4) |
| Unity.ProBuilder | game/third-party | menu | 209 | 1509 | 10032/10032 | 4.79 | 0.4 / 37 | 0.27 / 2.27 | 0.86 | 3.40 | 0.5 / 74 | 0.10 | 0.09 | 8 |  |
| Assembly-CSharp-firstpass | game/third-party | menu | 321 | 4761 | 10231/10245 | 4.22 | 0.4 / 41 | 0.46 / 2.00 | 0.84 | 3.30 | 0.6 / 75 | 0.77 | 0.72 | 138 | 14 (gshared failure 12) |
| Unity.Collections | game/third-party | menu | 131 | 2795 | 6993/8379 | 3.89 | 0.4 / 23 | 0.80 / 2.11 | 0.94 | 3.85 | 0.4 / 46 | 1.15 | 1.14 | 19 | 1386 (gshared failure 1384) |
| AstarPathfindingProject | game/third-party | menu | 272 | 2668 | 7132/7134 | 3.64 | 0.4 / 27 | 0.16 / 1.84 | 0.58 | 2.58 | 0.5 / 52 | 0.26 | 0.20 | 288 | 2 (gshared failure 2) |
| Sirenix.Serialization | game/third-party | menu | 172 | 1682 | 6077/6100 | 3.06 | 0.4 / 29 | 0.55 / 1.32 | 0.68 | 2.55 | 0.7 / 55 | 0.70 | 0.63 | 218 | 23 (gshared failure 22) |
| Unity.2D.SpriteShape.Runtime | game/third-party | menu | 52 | 449 | 7327/7332 | 2.64 | 0.4 / 21 | 0.15 / 1.33 | 0.64 | 2.12 | 0.4 / 46 | 0.05 | 0.04 | 44 | 5 (gshared failure 5) |
| Unity.TextMeshPro | game/third-party | menu | 220 | 1607 | 4692/4704 | 2.53 | 0.4 / 19 | 0.12 / 1.27 | 0.47 | 1.86 | 0.4 / 36 | 0.47 | 0.28 | 332 | 12 (gshared failure 12) |
| spine-unity | game/third-party | menu | 139 | 1651 | 5877/5877 | 2.47 | 0.4 / 20 | 0.18 / 1.25 | 0.53 | 1.95 | 0.5 / 40 | 0.40 | 0.27 | 668 |  |
| UnityEngine.TextCoreTextEngineModule | game/third-party | menu | 140 | 626 | 4842/4842 | 2.37 | 0.4 / 18 | 0.11 / 1.17 | 0.52 | 1.80 | 0.4 / 37 | 0.04 | 0.04 | 0 |  |
| Unity.ResourceManager | game/third-party | menu | 44 | 860 | 5577/5587 | 2.29 | 0.4 / 21 | 0.22 / 1.09 | 0.59 | 1.90 | 0.5 / 42 | 0.37 | 0.32 | 875 | 10 (gshared failure 10) |
| UnityEngine.PhysicsModule | rewritten | menu | 71 | 1354 | 6510/6510 | 2.19 | 0.4 / 16 | 0.12 / 1.15 | 0.52 | 1.79 | 0.4 / 37 | 0.02 | 0.02 | 8 |  |
| UnityEngine.UI | game/third-party | menu | 107 | 1804 | 4322/4334 | 1.82 | 0.4 / 18 | 0.10 / 0.96 | 0.42 | 1.48 | 0.5 / 35 | 0.27 | 0.19 | 546 | 12 (gshared failure 12) |
| DOTween | game/third-party | menu | 75 | 1122 | 3670/3670 | 1.81 | 0.4 / 16 | 0.14 / 0.93 | 0.42 | 1.48 | 0.4 / 34 | 0.24 | 0.19 | 216 |  |
| Unity.Addressables | game/third-party | menu | 33 | 649 | 3795/3795 | 1.68 | 0.4 / 16 | 0.12 / 0.87 | 0.46 | 1.45 | 0.4 / 33 | 0.14 | 0.12 | 144 |  |
| FMODUnity | rewritten | menu | 111 | 1790 | 4363/4363 | 1.62 | 0.4 / 13 | 0.10 / 0.93 | 0.39 | 1.42 | 0.4 / 29 | 0.16 | 0.13 | 331 |  |
| UnityEngine.ParticleSystemModule | rewritten | menu | 108 | 1912 | 4367/4387 | 1.58 | 0.4 / 13 | 0.11 / 0.88 | 0.40 | 1.39 | 0.4 / 28 | 0.05 | 0.05 | 49 | 20 (gshared failure 20) |
| Sirenix.Utilities | game/third-party | menu | 64 | 948 | 3034/3038 | 1.51 | 0.4 / 15 | 0.27 / 0.65 | 0.43 | 1.36 | 0.5 / 29 | 0.53 | 0.47 | 33 | 4 (gshared failure 4) |
| websocket-sharp | game/third-party | menu | 123 | 1565 | 3471/3473 | 1.51 | 0.4 / 17 | 0.13 / 0.81 | 0.37 | 1.32 | 0.5 / 34 | 0.06 | 0.06 | 2 | 2 (gshared failure 2) |
| Org.OpenAPITools | game/third-party | intro | 39 | 726 | 3195/3195 | 1.44 | 0.4 / 15 | 0.09 / 0.78 | 0.40 | 1.27 | 0.5 / 31 | 0.08 | 0.07 | 0 |  |
| com.steamworks.net | rewritten | menu | 82 | 2896 | 5691/5691 | 1.43 | 0.4 / 8 | 0.11 / 0.74 | 0.40 | 1.25 | 0.4 / 24 | 0.09 | 0.09 | 31 |  |
| Unity.Postprocessing.Runtime | game/third-party | menu | 61 | 597 | 2776/2776 | 1.37 | 0.4 / 13 | 0.08 / 0.73 | 0.37 | 1.18 | 0.5 / 27 | 0.11 | 0.08 | 197 |  |
| UnityEngine.AnimationModule | rewritten | menu | 87 | 1603 | 3890/3898 | 1.32 | 0.4 / 11 | 0.08 / 0.74 | 0.36 | 1.19 | 0.4 / 25 | 0.03 | 0.03 | 13 | 8 (gshared failure 8) |
| Pathfinding.Ionic.Zip.Reduced | game/third-party | intro | 117 | 1176 | 2863/2863 | 1.31 | 0.4 / 11 | 0.08 / 0.74 | 0.33 | 1.15 | 0.5 / 24 | 0.12 | 0.08 | 128 |  |
| Coffee.SoftMaskForUGUI | game/third-party | menu | 22 | 412 | 3022/3022 | 1.28 | 0.4 / 12 | 0.09 / 0.68 | 0.39 | 1.16 | 0.4 / 26 | 0.07 | 0.07 | 28 |  |
| UnityEngine.Physics2DModule | rewritten | menu | 74 | 1271 | 3444/3444 | 1.27 | 0.4 / 9 | 0.06 / 0.73 | 0.33 | 1.12 | 0.4 / 22 | 0.02 | 0.02 | 31 |  |
| SocketIOUnityAssembly | game/third-party | intro | 30 | 590 | 2851/2957 | 1.25 | 0.4 / 11 | 0.07 / 0.68 | 0.39 | 1.14 | 0.4 / 25 | 0.01 | 0.02 | 0 | 106 |
| UnityEngine.IMGUIModule | rewritten | menu | 96 | 1295 | 2978/2978 | 1.19 | 0.4 / 11 | 0.06 / 0.68 | 0.32 | 1.06 | 0.5 / 23 | 0.01 | 0.02 | 3 |  |
| Unity.Timeline | game/third-party | intro | 39 | 843 | 2854/2854 | 1.17 | 0.4 / 11 | 0.07 / 0.64 | 0.33 | 1.04 | 0.4 / 24 | 0.04 | 0.04 | 0 |  |
| Unity.Services.Core.Internal | game/third-party | menu | 13 | 362 | 2481/2481 | 1.12 | 0.4 / 10 | 0.09 / 0.61 | 0.36 | 1.06 | 0.4 / 24 | 0.09 | 0.08 | 77 |  |
| UnityEngine.SpriteShapeModule | rewritten | menu | 2 | 41 | 2902/2904 | 1.09 | 0.4 / 9 | 0.06 / 0.61 | 0.35 | 1.03 | 0.4 / 23 | 0.00 | 0.00 | 2 | 2 (gshared failure 2) |
| UnityEngine.XRModule | rewritten | menu | 22 | 384 | 2559/2559 | 1.06 | 0.4 / 10 | 0.05 / 0.60 | 0.33 | 0.99 | 0.4 / 23 | 0.02 | 0.02 | 0 |  |
| Unity.Services.Analytics | game/third-party | menu | 19 | 319 | 2315/2315 | 1.06 | 0.4 / 11 | 0.06 / 0.60 | 0.33 | 0.99 | 0.5 / 25 | 0.01 | 0.01 | 2 |  |
| UnifyLibrary | game/third-party | menu | 22 | 507 | 2299/2299 | 0.99 | 0.4 / 10 | 0.05 / 0.58 | 0.32 | 0.94 | 0.4 / 23 | 0.06 | 0.04 | 112 |  |
| Unity.Burst | game/third-party | menu | 86 | 3636 | 4359/4382 | 0.97 | 0.4 / 4 | 0.05 / 0.37 | 0.07 | 0.48 | 0.4 / 10 | 0.06 | 0.06 | 20 | 23 (gshared failure 23) |
| DOTween.Modules | game/third-party | menu | 14 | 336 | 2018/2018 | 0.95 | 0.4 / 9 | 0.04 / 0.56 | 0.30 | 0.90 | 0.4 / 23 | 0.01 | 0.01 | 19 |  |
| UnityEngine.TextCoreFontEngineModule | rewritten | menu | 22 | 380 | 2000/2000 | 0.87 | 0.4 / 7 | 0.04 / 0.52 | 0.30 | 0.86 | 0.4 / 18 | 0.01 | 0.01 | 31 |  |
| UnityEngine.TilemapModule | rewritten | menu | 14 | 294 | 2127/2127 | 0.87 | 0.4 / 7 | 0.04 / 0.51 | 0.30 | 0.85 | 0.4 / 19 | 0.01 | 0.01 | 0 |  |
| Coffee.UIParticle | game/third-party | menu | 8 | 128 | 1654/1654 | 0.84 | 0.4 / 7 | 0.04 / 0.50 | 0.29 | 0.83 | 0.4 / 18 | 0.04 | 0.03 | 63 |  |
| DOTweenPro.Scripts | game/third-party | menu | 14 | 188 | 1603/1603 | 0.83 | 0.4 / 7 | 0.03 / 0.50 | 0.28 | 0.81 | 0.4 / 18 | 0.03 | 0.02 | 20 |  |
| GalaxyCSharp | rewritten | menu | 120 | 2955 | 3616/3616 | 0.82 | 0.4 / 12 | 0.08 / 0.49 | 0.11 | 0.67 | 0.4 / 21 | 0.08 | 0.08 | 0 |  |
| Unity.Services.Core.Registration | game/third-party | menu | 3 | 57 | 1710/1710 | 0.82 | 0.4 / 8 | 0.04 / 0.50 | 0.28 | 0.82 | 0.4 / 19 | 0.00 | 0.00 | 3 |  |
| RTLTMPro | game/third-party | menu | 9 | 107 | 1627/1627 | 0.80 | 0.4 / 6 | 0.04 / 0.48 | 0.29 | 0.81 | 0.4 / 17 | 0.00 | 0.00 | 1 |  |
| UnityEngine.UnityWebRequestModule | rewritten | menu | 16 | 286 | 1795/1795 | 0.79 | 0.4 / 7 | 0.04 / 0.48 | 0.29 | 0.81 | 0.4 / 18 | 0.02 | 0.01 | 44 |  |
| UnityEngine.UIModule | rewritten | menu | 9 | 150 | 1663/1663 | 0.78 | 0.4 / 6 | 0.03 / 0.48 | 0.29 | 0.79 | 0.4 / 17 | 0.02 | 0.01 | 57 |  |
| Elringus.BlendModes.Runtime | game/third-party | menu | 9 | 247 | 1651/1651 | 0.77 | 0.4 / 7 | 0.04 / 0.46 | 0.28 | 0.78 | 0.4 / 18 | 0.05 | 0.04 | 139 |  |
| DOTweenPro | game/third-party | menu | 3 | 45 | 1448/1448 | 0.73 | 0.4 / 6 | 0.03 / 0.45 | 0.27 | 0.75 | 0.4 / 16 | 0.00 | 0.00 | 1 |  |
| Unity.Services.Core.Configuration | game/third-party | menu | 1 | 44 | 1507/1507 | 0.73 | 0.4 / 6 | 0.03 / 0.45 | 0.28 | 0.76 | 0.4 / 17 | 0.00 | 0.00 | 0 |  |
| Unity.Services.Core | game/third-party | menu | 2 | 63 | 1485/1485 | 0.71 | 0.4 / 6 | 0.03 / 0.44 | 0.27 | 0.74 | 0.4 / 17 | 0.00 | 0.01 | 10 |  |
| UnityFx.Outline | game/third-party | menu | 10 | 224 | 1481/1481 | 0.71 | 0.4 / 6 | 0.03 / 0.44 | 0.25 | 0.72 | 0.4 / 16 | 0.01 | 0.01 | 14 |  |
| Shepherd.Engine.Derived | derived-engine | menu | 3 | 24 | 1229/1229 | 0.64 | 0.4 / 6 | 0.03 / 0.41 | 0.25 | 0.68 | 0.4 / 16 | 0.02 | 0.01 | 14 |  |
| Pathfinding.ClipperLib | game/third-party | intro | 38 | 220 | 448/448 | 0.24 | 0.4 / 1 | 0.03 / 0.49 | 0.26 | 0.78 | 0.4 / 16 | 0.00 | 0.00 | 0 |  |
| UnityEngine.AudioModule | rewritten | menu | 29 | 523 | 876/876 | 0.21 | 0.4 / 2 | 0.01 / 0.11 | 0.03 | 0.15 | 0.4 / 4 | 0.01 | 0.01 | 0 |  |
| AFPSCounter.Runtime | game/third-party | menu | 29 | 280 | 372/372 | 0.16 | 0.4 / 6 | 0.03 / 0.47 | 0.26 | 0.75 | 0.4 / 24 | 0.10 | 0.07 | 100 |  |
| Sirenix.OdinInspector.Attributes | game/third-party | menu | 10 | 475 | 711/711 | 0.14 | 0.4 / 1 | 0.04 / 0.44 | 0.28 | 0.75 | 0.4 / 15 | 0.02 | 0.02 | 0 |  |
| Unity.Services.Core.Scheduler | game/third-party | menu | 2 | 37 | 286/286 | 0.12 | 0.4 / 1 | 0.05 / 0.42 | 0.27 | 0.74 | 0.4 / 16 | 0.03 | 0.02 | 0 |  |
| UnityEngine.TextRenderingModule | rewritten | menu | 9 | 162 | 391/391 | 0.12 | 0.4 / 1 | 0.03 / 0.44 | 0.26 | 0.74 | 0.4 / 16 | 0.02 | 0.01 | 25 |  |
| LeTai.TranslucentImage | game/third-party | menu | 4 | 68 | 204/204 | 0.08 | 0.4 / 1 | 0.03 / 0.42 | 0.25 | 0.69 | 0.4 / 15 | 0.01 | 0.01 | 27 |  |
| UnityEngine.VideoModule | rewritten | menu | 13 | 204 | 306/306 | 0.07 | 0.4 / 1 | 0.00 / 0.04 | 0.01 | 0.06 | 0.4 / 1 | 0.01 | 0.01 | 17 |  |
| UnityEngine.SubsystemsModule | rewritten | intro | 5 | 126 | 218/218 | 0.07 | 0.4 / 1 | 0.02 / 0.02 | 0.02 | 0.06 | 0.4 / 2 | 0.04 | 0.04 | 0 |  |
| UnityEngine.InputLegacyModule | rewritten | menu | 10 | 194 | 288/288 | 0.06 | 0.4 / 1 | 0.00 / 0.03 | 0.01 | 0.05 | 0.4 / 2 | 0.01 | 0.01 | 20 |  |
| MessagePack.Annotations | game/third-party | menu | 2 | 122 | 198/198 | 0.04 | 0.4 / 0 | 0.00 / 0.02 | 0.01 | 0.03 | 0.4 / 1 | 0.00 | 0.00 | 1 |  |
| UnityEngine.AssetBundleModule | rewritten | menu | 5 | 102 | 158/158 | 0.04 | 0.4 / 1 | 0.00 / 0.02 | 0.01 | 0.03 | 0.4 / 1 | 0.01 | 0.01 | 8 |  |
| Boxophobic.AtmosphericHeightFog.Runtime | game/third-party | intro | 6 | 22 | 24/24 | 0.03 | 0.4 / 1 | 0.00 / 0.01 | 0.00 | 0.02 | 0.4 / 1 | 0.00 | 0.00 | 1 |  |
| Boxophobic.Utils.Scripts | game/third-party | intro | 3 | 46 | 63/63 | 0.02 | 0.4 / 0 | 0.00 / 0.01 | 0.00 | 0.01 | 0.4 / 0 | 0.00 | 0.00 | 1 |  |
| UnityEngine.GridModule | rewritten | intro | 3 | 50 | 73/73 | 0.02 | 0.4 / 0 | 0.00 / 0.01 | 0.00 | 0.01 | 0.4 / 0 | 0.00 | 0.00 | 0 |  |
| Unity.Services.Core.Threading | game/third-party | menu | 0 | 20 | 63/63 | 0.02 | 0.4 / 0 | 0.00 / 0.01 | 0.01 | 0.02 | 0.4 / 1 | 0.01 | 0.01 | 0 |  |
| UnityEngine.SharedInternalsModule | game/third-party | menu | 2 | 130 | 148/148 | 0.02 | 0.4 / 0 | 0.00 / 0.01 | 0.00 | 0.01 | 0.4 / 0 | 0.01 | 0.01 | 14 |  |
| Unity.Profiling.Core | game/third-party | menu | 1 | 43 | 67/98 | 0.02 | 0.4 / 0 | 0.01 / 0.00 | 0.01 | 0.01 | 0.4 / 0 | 0.01 | 0.01 | 3 | 31 (gshared failure 31) |
| UnityEngine.InputModule | rewritten | menu | 1 | 22 | 63/64 | 0.02 | 0.4 / 0 | 0.00 / 0.01 | 0.00 | 0.01 | 0.4 / 0 | 0.00 | 0.00 | 6 | 1 (gshared failure 1) |
| UnityEngine.UnityWebRequestAssetBundleModule | rewritten | intro | 1 | 27 | 51/51 | 0.01 | 0.4 / 0 | 0.00 / 0.01 | 0.00 | 0.01 | 0.4 / 0 | 0.00 | 0.00 | 0 |  |
| UnityEngine.SpriteMaskModule | rewritten | intro | 1 | 21 | 44/44 | 0.01 | 0.4 / 0 | 0.00 / 0.01 | 0.00 | 0.01 | 0.4 / 0 | 0.00 | 0.00 | 0 |  |
| Sirenix.Serialization.Config | game/third-party | menu | 1 | 19 | 28/28 | 0.01 | 0.4 / 0 | 0.00 / 0.00 | 0.00 | 0.01 | 0.4 / 0 | 0.00 | 0.00 | 8 |  |
| DOGames.Threading | game/third-party | menu | 1 | 15 | 24/24 | 0.01 | 0.4 / 0 | 0.00 / 0.00 | 0.00 | 0.01 | 0.4 / 0 | 0.00 | 0.00 | 7 |  |
| Plawius.ControllerIcons | game/third-party | menu | 0 | 8 | 18/18 | 0.01 | 0.4 / 0 | 0.00 / 0.00 | 0.00 | 0.01 | 0.4 / 0 | 0.00 | 0.00 | 1 |  |
| Unity.Services.Core.Device | game/third-party | menu | 0 | 14 | 20/20 | 0.01 | 0.4 / 0 | 0.00 / 0.00 | 0.00 | 0.00 | 0.4 / 0 | 0.00 | 0.00 | 0 |  |
| Unity.MemoryProfiler | game/third-party | menu | 0 | 8 | 13/13 | 0.00 | 0.4 / 0 | 0.00 / 0.00 | 0.00 | 0.00 | 0.4 / 0 | 0.00 | 0.00 | 4 |  |
| UnityEngine.JSONSerializeModule | rewritten | menu | 1 | 7 | 13/13 | 0.00 | 0.4 / 0 | 0.00 / 0.00 | 0.00 | 0.00 | 0.4 / 0 | 0.00 | 0.00 | 4 |  |
| UnityEngine | game/third-party | menu | 0 | 11 | 16/16 | 0.00 | 0.4 / 0 | 0.00 / 0.00 | 0.00 | 0.00 | 0.4 / 0 | 0.00 | 0.00 | 0 |  |
| Unity.Services.Core.Environments.Internal | game/third-party | menu | 0 | 5 | 9/9 | 0.00 | 0.4 / 0 | 0.00 / 0.00 | 0.00 | 0.00 | 0.4 / 0 | 0.00 | 0.00 | 0 |  |
