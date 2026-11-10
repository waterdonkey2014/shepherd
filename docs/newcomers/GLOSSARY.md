# Glossary

The words you will meet in Shepherd's code, scripts and notes, in alphabetical order. A "→" points to another entry.

**0x8002D003**: The error the Vita gives when it refuses to load a module that imports a library not loaded yet. Launcher builds
check the engine module's imports against `vita/shepherd/module-load-libs.txt` to avoid it.

**AOT (ahead-of-time compilation)**: Compiling .NET code to machine code before the program runs, instead of interpreting it.
Shepherd uses `mono-aot-cross` to compile the → BCL and engine code to ARM code for the Vita. "Full+interp" means AOT code and the
→ interpreter in the same process. See also → LLVM AOT, → game AOT.

**app0: / ux0:**: Vita drive names. `app0:` is the installed app's own (read-only) files from the → VPK. `ux0:` is the memory card.
Shepherd keeps your staged game files in `ux0:data/shepherd/`.

**arena**: The fixed block of memory the Vita build gives the Mono runtime (`SHEPHERD_ARENA_MB`); see `vita/runtime/tests/test_arena*.c`.

**bake**: Converting something once, ahead of time, into a form the Vita can use cheaply. The **audio bake** (`tools/FmodBake`)
renders the game's → FMOD events into takes plus a `manifest.json` under `runtime/audiobake/`. The **icall bake**
(`tools/IcallBake`) turns `icalls.json` into `icalls.bin`. Bakes are built from the game, so they are never committed.

**BCL (base class library)**: .NET's standard library (`System.*`). On the Vita, Shepherd uses CoreLib (`System.Private.CoreLib`)
plus a trimmed subset, compiled → AOT.

**baseline JIT**: Shepherd's small just-in-time compiler for the Vita (`vita/jit`). It is **copy-and-patch**: it copies
pre-compiled machine-code snippets (→ stencils) and patches in the operands. It is used only where the system allows executable
memory.

**black box**: The host's record of recent log lines and events, kept in memory rings and dumped to `blackbox.txt` on the memory card,
so a run that fails still leaves a trail (`vita/shepherd/k7_measure.h`).

**Box2D**: The 2D physics library Unity uses underneath. Shepherd vendors Box2D 2.3.1 in `vita/physics/box2d` (zlib licence) and
adds Unity's behaviours behind flags. See → libshepphys.

**companion plugin**: A Claude Code plugin, "Shepherd for PS Vita" (marketplace `shepherd-vita`, plugin `shepherd`). It holds the
project's knowledge: measured hardware facts, gotchas, proven and failed techniques, the idea catalog.

**coop (cooperative suspend)**: The Mono threading mode the Vita build uses. Threads stop for the garbage collector at safe points
they reach themselves.

**Derived / derived code**: A port of a game's or a third party's code (for example a shader rewritten for vitaGL). It may only
live in the local, git-ignored `engine/**/Derived/` folder. `vita/shepherd/derived_leaks.py` fails if any of it shows up in a file
git would carry. "Derived" also describes any data built from the game (packs, bakes, rewritten DLLs): owner-local, never
committed.

**devlog**: The small C client (`vita/devlog`) that lets a Vita app send its log files to a computer over Wi-Fi. HMACs prove the
sender knows a secret token; the token itself never crosses the network. `tools/devlog-receiver` is a Python receiver for it.

**eboot (`eboot.bin`)**: The main executable of a Vita app, inside its → VPK. Built from an ELF with `vita-elf-create` and
`vita-make-fself`.

**EMULATED / MEASURED / DERIVED / ESTIMATE**: The labels every number in this project must carry: run under → vlx / QEMU, measured on
a device (with model, firmware and clock), computed from other numbers, or a guess.

**fidelity first**: The project's top rule. The port must behave and look exactly like the original game; anything else is
opt-in. See → strict original.

**fixtures**: Recorded replays (idle, menu tour, routes) that are re-run after every build and must match frame for frame
(`vita/tests/fixtures`).

**FMOD**: The audio middleware many Unity games use, the first game included. There is no FMOD runtime for the Vita, so Shepherd
→ bakes the game's FMOD banks and plays them with → shepaudio. In the → Vita profile, the game's FMOD calls are rewritten to land in
the engine (`engine/Shepherd.Engine/Audio/Fmod.cs`).

**game AOT**: Compiling a game's own assemblies → AOT for the Vita (`vita/gameaot`). The result is built from the game, so it is
→ owner-local only.

**gate**: A check a build must pass, or it is refused. Examples:
- the → GUID gate;
- the VPK gate (`vita/shepherd/check_vpk.py`: title id, memory budget, imports, contents, no game files);
- the module-import check (→ 0x8002D003);
- in `vita/llvm/gates.sh`, size and foreign-code gates.

**golden / goldens**: Recorded expected results that a test compares against, e.g. `vita/physics/tests/golden` for the → T1
scenarios.

**GUID gate**: `check_aot_guids.py`. It checks that every AOT image was compiled from exactly the assemblies that are packaged
next to it. A mismatch would crash at run time.

**GXM / GXP**: The Vita's native graphics API (GXM), and its compiled shader programs (GXP). → vitaGL sits on GXM.
`libshacccg.suprx` compiles shaders at run time; Shepherd caches the results (`Render/GxpCache.cs`).

**HENkaku / Enso / taiHEN**: The Vita homebrew stack Shepherd needs. HENkaku enables homebrew, Enso makes it permanent at boot,
and taiHEN is the plugin framework.

**HOST_VITA**: The preprocessor define the Mono fork uses for its Vita-specific code.

**icall (internal call)**: A Unity method marked `[MethodImpl(InternalCall)]`: declared in C#, implemented in Unity's native engine.
→ Rewriter replaces each one with a call through → Shepherd.Hooks. `Shepherd.Engine/Icalls/` implements them. Unimplemented ones
are logged to `runtime/logs/missing-icalls.txt`.

**`icalls.json` / `icalls.bin`**: The list of every rewritten icall (id, key, signature), written by → Rewriter, and its compact
→ baked copy that the engine loads.

**identity check**: Comparing hashes of what two builds do (per-frame render commands, audio streams) on the same scripted route.
For example, the Mac engine against the Vita build under → vlx: the Vita must hand the GPU the same draw stream.

**interpreter**: Mono's interpreter. It runs .NET code without compiling it: slow, but it runs anything. The game's own code runs
here unless → game AOT or the → baseline JIT takes it.

**kit (device kit #N)**: One round of changes to the first port, built, tested and measured on a real Vita. Kits 1 to 14 so far
(see CHANGELOG and the → progress page). Many file and variable names carry their kit number (`k8_`, `K10Store`, ...).

**launcher**: A tiny eboot (`vita/shepherd/launcher.c`). It shows the splash video through the hardware decoder while it loads
the engine as a module (`shepherd.suprx`), so the screen is not black during the long load.

**learning cache**: Kit 10's caches on the memory card (`Runtime/K10Store.cs`). They remember work from earlier boots to make later
ones faster. The Settings menu has Cache Size / Clear Cache / Verify Files rows for them.

**libshepphys**: Shepherd's physics library: → Box2D plus Unity's behaviours and a flat C API (`vita/physics/shim`). It is built for
the Mac, the Vita and (in CI) Linux.

**LLVM AOT**: → AOT through LLVM (`vita/llvm`), producing ARMv7 Thumb-2 code. It uses Mono's LLVM 16 packages plus Shepherd's
patches.

**lockstep**: Running two builds side by side and comparing them frame by frame, stopping at the first difference
(`vita/tests/lockstep/rig.py`). Used to check a new execution tier against the → interpreter.

**Mac engine**: `engine/Shepherd.Engine` run on macOS (`play.sh`): the same engine code as the Vita, with a window, real input
and fast iteration.

**mono-aot-cross**: The cross compiler (built by `vita/toolchain/build-cross.sh`) that runs on the Mac and emits Vita code.

**Mono fork**: Mono from dotnet/runtime `v8.0.31`, patched for the Vita by `vita/patches/dotnet-runtime-v8.0.31-shepherd-vita.patch`.
The patch is applied by `vita/toolchain/setup-fork.sh`; the fork itself is not in this repository.

**newlib**: The C library of the Vita toolchain. Its heap size is a build setting (`SHEPHERD_NEWLIB_MB`).

**oracle**: The real Unity, used as the reference answer. `vita/tests/oracle` is a Unity 2022.3 project that runs the same probes
as Shepherd, and the results are compared.

**owner-local**: Built from, or describing, the owner's own copy of a game. It stays on that machine or that Vita, and is never
committed, uploaded or put in a shareable → VPK.

**pack (`shepherd.pack`, `shepherd.pk2`)**: The Vita asset pack, built by `tools/VitaPack`. It is one flat file holding just the
objects a recorded boot touched (`SHEPHERD_PACK_RECORD`). Version 2 (`.pk2`) is chunked and LZ4-compressed, so it can be read and
unpacked ahead of time.

**PCH-1000 / PCH-2000 / PS TV**: Vita models: the original OLED model, the slim model, and the TV box. The first port's numbers are
from one PCH-2000.

**PCT**: Seeded thread-schedule testing. It runs the engine's threads one at a time in a reproducible random order, to make rare
races repeatable (`vita/tests/pct`, `Shepherd.Assets/Sched.cs`).

**progress page** (Shepherd Progress): the first port's page, in its own repository (https://waterdonkey2014.github.io/shepherd-progress/): every kit, done and
planned, with each trick explained and its effect, measured or estimated.

**Rewriter**: `tools/Rewriter`. It rewrites Unity's managed assemblies from your install so their → icalls go to Shepherd. With
`--vita` it also rewrites → FMOD calls for the → Vita profile.

**`runtime/`**: The git-ignored folder in your checkout where everything built from your game goes (rewritten DLLs, packs, bakes,
logs, the saves sandbox). Not to be confused with the → Mono fork's checkout, `$SHEPHERD_WORK/runtime`.

**shepaudio**: Shepherd's portable C99 audio mixer (`vita/audio`). It plays the → baked FMOD events on the Vita.

**Shepherd.Hooks**: The tiny assembly every rewritten icall goes through (`Icalls.Call(id, args)`). It is the single doorway
between Unity's managed code and the engine.

**`SHEPHERD_EBOOT`**: The compile-time define for Vita builds of the engine. It leaves Mac-only test hooks out.

**`SHEPHERD_WORK`**: The folder for build trees, toolchain checkouts and temporary files. It needs tens of GB and lives outside the
checkout.

**SHPDxxxxx**: The title IDs of Shepherd's Vita apps. Each build recipe has its own (e.g. the hello app, the menu boot, test apps).
Some ranges are reserved for owner-only builds that contain → derived code.

**`.shv`**: The splash clip format the → launcher plays through the hardware video decoder, made by `vita/tools/splash_pack.py` from
your own game's video. It carries checksums, so a damaged clip is refused.

**stencils**: The pre-compiled machine-code snippets the → baseline JIT copies and patches (`vita/jit/stencils`).

**strict original**: `SHEPHERD_STRICT_ORIGINAL=1` turns every non-original behaviour off (`Runtime/StrictOriginal.cs`).

**System.Native shim**: Shepherd's Vita version of .NET's native helper library (`vita/runtime/system-native`, `pal_vita*.c`). It
provides file access through the Vita's own calls, crypto, zlib, and a refusal of network calls.

**T1 scenarios**: The physics determinism scenarios (`vita/physics/tests`). They must give bit-identical results to the goldens on
every platform.

**vitaGL / vitaShaRK**: vitaGL is an OpenGL-ES-style library on top of → GXM. vitaShaRK wraps the Vita's run-time shader compiler.
Both come from vitasdk's package manager (`vdpm`).

**vitasdk**: The open-source toolchain for Vita homebrew (`arm-vita-eabi-gcc` and friends).

**VitaShell**: The Vita file manager used to install VPKs and to copy files over FTP.

**Vita profile**: The engine run with `SHEPHERD_PROFILE=vita`: the Vita's configuration (rewritten FMOD, pack loading, budgets),
also usable on the Mac for testing.

**vlx**: "Vita-on-Linux eXecution" (`vita/vlx`). It takes the Vita build's own objects, relinks them into an ARM Linux program with
a small stand-in for the Vita kernel calls, and runs them under QEMU on a computer, so device crashes can be reproduced off the
device.

**VPK**: The Vita app package you install with VitaShell. Shepherd's VPKs hold the engine, runtime and base library: never game
files.
