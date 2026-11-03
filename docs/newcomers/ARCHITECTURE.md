# How Shepherd fits together: a plain-English map

There is a denser version in [docs/ARCHITECTURE.md](../ARCHITECTURE.md). This page explains the same thing for someone who has
never opened the code.

## The idea in one paragraph

A Unity game is two things: the game's own C# code (compiled to .NET assemblies), and Unity's engine underneath it (native code
that draws, plays sound, runs physics, loads assets). Unity's engine does not run on a PS Vita. Shepherd keeps the game's C# code
exactly as shipped, and replaces everything underneath it:

- a .NET runtime that runs on the Vita;
- a C# re-implementation of the parts of Unity the game calls;
- the native pieces (graphics, physics, audio, input, video) to go with it.

The rule over everything is **fidelity first**: the game should behave and look exactly like the original. Anything that would
differ is an opt-in switch, off by default.

## The layers, top to bottom

```
  The game's own assemblies          unchanged, read from your install
  Unity's managed assemblies         from your install; their native calls rewritten by tools/Rewriter
 ─────────────────────────────────────────────────────────────────────────────────────────────
  Shepherd.Hooks                     the one doorway: every rewritten call goes through Icalls.Call(id, args)
  Shepherd.Engine (C#)               what Unity's native engine did: objects, scenes, rendering, physics, audio, input
  Shepherd.Assets (C#)               reads Unity's file formats: serialized files, bundles, textures; the Vita pack
 ─────────────────────────────────────────────────────────────────────────────────────────────
  .NET 8 Mono runtime, forked        runs all of the above on the Vita: interpreter, AOT, LLVM AOT, a small JIT
 ─────────────────────────────────────────────────────────────────────────────────────────────
  Vita host (C)                      boots the runtime; memory, clocks, threads, files, pad and touch, video, logs
  native libraries (C/C++)           vitaGL (graphics), libshepphys (Box2D physics), shepaudio (sound)
 ─────────────────────────────────────────────────────────────────────────────────────────────
  PS Vita with HENkaku / Enso        ux0:data/shepherd/ holds your staged game files
```

## The main parts

### 1. The doorway: rewriting Unity's internal calls (`tools/Rewriter`, `engine/Shepherd.Hooks`)
Unity's managed assemblies declare thousands of methods marked `InternalCall`. Their bodies live in Unity's native player, which
is not here. `tools/Rewriter` (built on Mono.Cecil) gives each of them a small managed body that forwards to
`Shepherd.Hooks.Icalls.Call(id, args)`. It also writes `icalls.json`, the list of every call with its id and signature.
`tools/IcallBake` turns that list into `icalls.bin`, a compact copy the engine loads quickly.

### 2. The engine (`engine/Shepherd.Engine`)
This is the stand-in for Unity's native engine, written in C#. Its folders:

| Folder | What it does |
|---|---|
| `Icalls/` | The implementations of Unity's internal calls, grouped by area (application, camera, input, animator, bundles, ...). A call with no implementation is logged to `runtime/logs/missing-icalls.txt`. |
| `Runtime/` | The game loop and its rules: Unity's order of Awake / Start / Update / coroutines, the play clock (`PlayClock.cs`), loading, caches, video, the strict-original switch (`StrictOriginal.cs`). |
| `Native/` | The "native side" of every live Unity object: the record Unity's native code would hold, and fast lists the per-frame passes use. |
| `Assets/` | Loads scenes and objects from the game's files or from the Vita pack, and records which objects a boot touches (for the pack builder). |
| `Render/` | Collects sprites, meshes, particles, UI and text, sorts them like Unity, and draws them through a backend: SDL_Renderer (the reference), a GL backend (desktop GL on the Mac, vitaGL on the Vita), or null (headless). It can draw on its own render thread. |
| `Physics/` | Unity's 2D physics on `libshepphys` (see below), plus check suites that compare it with Unity. `PhysicsOff/` stands in when physics is compiled out. |
| `Audio/` | In the Vita profile the game's FMOD calls land here (`Fmod.cs`): they play the baked audio through shepaudio (`ShepAudio.cs`) when `SHEPHERD_AUDIO_BAKE` points at a bake, and stay silent otherwise. The Mac engine keeps the game's real FMOD. |
| `Anim/` | Unity's Animator: state machines and curve binding. |
| `Diag/` | Measurement and test hooks: probes, profilers, memory reports, scripted routes. Hooks meant only for Mac test runs are left out of Vita builds. |
| `MonoCompat/` | Small fixes needed only when the engine runs on Mono's interpreter in a 64-bit process (the Mac). |

`Profile.cs` picks the profile. With no `SHEPHERD_PROFILE` set it is the Mac engine; `SHEPHERD_PROFILE=vita` runs the Vita's
configuration (on the Mac too, for testing). Builds made for the Vita define `SHEPHERD_EBOOT`, which leaves the Mac-only test
hooks out.

### 3. The asset reader (`engine/Shepherd.Assets`)
This reads Unity's formats directly: UnityFS bundles, serialized files and their type trees, Addressables catalogs, textures
(including crunch, through Unity's crunch fork), and compression. It also reads the **Vita pack**: one flat file holding just the
objects a recorded boot needs (`VitaPack.cs`), and its chunked, LZ4-compressed version 2 (`VitaPack2.cs`, `shepherd.pk2`).

### 4. Tools that run on your computer (`tools/`)
These are C# command-line tools:

| Tool | What it does |
|---|---|
| `Rewriter` | Rewrites Unity's internal calls (above). |
| `IcallBake` | Turns `icalls.json` into `icalls.bin`. |
| `VitaPack` | Builds the Vita pack from a recorded boot. |
| `FmodBake` | Converts the game's FMOD banks into takes and a manifest that shepaudio can play. |
| `ApiSurface` | Reports which engine, middleware and base-library APIs the game's code actually uses. |
| `FloatPc` | An optional IL rewrite that gives float maths the PC's precision (behind a switch). |
| `K10CacheTest` | A self-test of the learning cache's store. |
| `VitaBudget`, `IcallCount`, `GcCensus`, `FloatCensus`, `PhysIlScan`, `LoadProbe` | Survey and measurement tools: they read the game's assemblies or the engine's data and write reports. |

Besides the C# tools, `tools/devlog-receiver` is a small Python server that collects logs a Vita sends over Wi-Fi.

### 5. The runtime and its compilers (`vita/toolchain`, `vita/patches`, `vita/llvm`, `vita/jit`, `vita/gameaot`)
Shepherd runs on **Mono from .NET 8** (dotnet/runtime tag `v8.0.31`), forked for the Vita. The fork is not copied into this
repository. It is a patch (`vita/patches/dotnet-runtime-v8.0.31-shepherd-vita.patch`) that `vita/toolchain/setup-fork.sh` applies
to a fresh checkout. Code runs in up to four ways:

- **Interpreter.** It runs anything, slowly. The game's own code runs here by default.
- **Static AOT** (ahead-of-time). `mono-aot-cross` compiles the base class library and engine code to ARM code inside the Vita
  build.
- **LLVM AOT** (`vita/llvm`). The same, through LLVM, producing Thumb-2 code.
- **Baseline JIT** (`vita/jit`). A copy-and-patch JIT: it copies pre-compiled snippets ("stencils") and patches in the operands,
  where the system allows executable memory.

`vita/gameaot` is the owner-local pipeline that AOT-compiles a game's own assemblies. Its output is built from the game, so it
never leaves your machine.

### 6. The Vita host (`vita/shepherd`)
This is C code that turns all of the above into a Vita app. `host.c` boots the Mono runtime and calls the engine's `Main`. It also
handles memory budgets, CPU/GPU clocks, threads, file access, pad and touch input, suspend/resume and black-box logs. A **launcher**
build (`launcher.c`) is a tiny executable that shows the splash video through the hardware decoder while it loads the engine as a
module. `build.sh` produces the VPK and runs its gates. `stage-owner.sh` assembles the folder of your own files for the memory
card.

The System.Native shim (`vita/runtime/system-native`) gives .NET's base library its file, crypto and network calls on the Vita.

### 7. Native libraries
- **Physics** (`vita/physics`): Box2D 2.3.1, vendored, with Unity's behaviours added behind flags and a flat C API (`shim/`). The
  T1 scenarios (`tests/`) must match recorded golden hashes on the Mac, on the Vita's CPU under QEMU, and on Linux (CI).
- **Audio** (`vita/audio`): shepaudio, a portable C99 mixer that plays the baked FMOD events. It builds for the Mac and the Vita,
  and (without an AAC decoder) on Linux.
- **Graphics**: vitaGL from vitasdk. `vita/gltest` checks Shepherd's shaders and GL calls on the device against CPU references.

### 8. Testing off the device
- **The Mac engine** runs the same engine code with real input: the fastest loop (`play.sh`).
- **vlx** (`vita/vlx`) relinks the Vita build's own objects into an ARM Linux program and runs it under QEMU, so device-only
  crashes can be reproduced on a computer.
- **Identity checks**: per-frame render-command hashes and audio hashes on scripted routes, the Mac engine against the Vita build.
- **Oracles** (`vita/tests/oracle`): a Unity editor project whose results Shepherd must match (maths, formatting, layout, physics).
- **Unit tests**: the host's helpers, the shim, the devlog client, physics and the cache store. CI runs those that need no Mac,
  Vita or game (see [BUILD.md](BUILD.md#0-the-linux-checks-no-mac-no-vita-no-game)).

## How one run flows

1. On your computer, the Rewriter rewrites Unity's managed assemblies from your install, and the pack and audio tools build their
   files from your install. Everything goes under `runtime/`, which is git-ignored.
2. `vita/shepherd/build.sh` compiles the base library and engine code ahead of time (which parts depends on the recipe; see its
   header) and links them with the runtime and `host.c` into a VPK. The VPK has **no game files** in it.
3. `stage-owner.sh` puts your rewritten assemblies, the pack and the saves sandbox into one folder. You copy it to
   `ux0:data/shepherd/` on the Vita.
4. On the Vita, the host boots the runtime and calls the engine's `Main`. The engine loads the game's assemblies from
   `ux0:data/shepherd/` and plays the game. Each Unity native call the game makes lands in `Shepherd.Engine` through the doorway.

## Where game data lives

Never in this repository and never in a VPK. Rewritten assemblies, packs, audio bakes and caches stay under `runtime/` on your
computer or in `ux0:data/shepherd/` on your Vita. Ports of a game's or a third party's code (for example shaders) stay in your
local, git-ignored `engine/**/Derived/` folder. `vita/shepherd/derived_leaks.py` checks that none of it ends up in a file git
would carry.
