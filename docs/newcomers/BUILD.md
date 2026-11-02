# Building Shepherd, step by step

This is the newcomer's walk-through. The reference is the top-level [BUILD.md](../../BUILD.md), and every script has a header
that explains it in detail: read the header before you run a script.

Shepherd's build grew on one Apple-silicon Mac. Most of it still assumes macOS. This page says, for each step, what it needs:

| Step | Linux or macOS | Needs a Mac | Needs a PS Vita | Needs your own copy of the game |
|---|---|---|---|---|
| [0. Linux checks](#0-the-linux-checks-no-mac-no-vita-no-game) | yes | no | no | no |
| [1. Toolchain](#1-toolchain) | vitasdk: yes | the runtime fork's cross compiler (as written) | no | no |
| [2. Your game's files](#2-your-games-files) | the C# tools build anywhere | no (not checked on Linux) | no | **yes** |
| [3. The Mac engine](#3-the-mac-engine) | no | **yes** | no | **yes** |
| [4. The Vita build](#4-the-vita-build) | no | **yes** (as written) | no | **yes**, for staging |
| [5. Off-device: vlx](#5-off-device-vlx) | no | **yes** (OrbStack / Docker) | no | yes, for the menu app |
| [6. On the Vita](#6-on-the-vita) | – | – | **yes** | **yes** |

"As written" means the script itself is hard-wired to macOS. For example, `vita/toolchain/build-cross.sh` builds the host side of
the cross compiler with `CMAKE_OSX_ARCHITECTURES=arm64`. Making these steps work on other machines is a welcome contribution.

Shepherd never ships game files. Every step that reads the game reads **your own install**, and everything built from it stays on
your machine: under `runtime/` in your checkout (git-ignored), under `$SHEPHERD_WORK`, or on your Vita.

## 0. The Linux checks (no Mac, no Vita, no game)

This is what CI runs on every pull request. It is the quickest way to see that your checkout and your changes build.

On Ubuntu 24.04 (other Linux distributions need the same tools under their own package names):
```bash
sudo apt-get install -y build-essential cmake ninja-build zsh python3 dotnet-sdk-8.0
```

Then, from the checkout:
```bash
.github/ci/linux-checks.sh
```

You can also run only some sections, e.g. `.github/ci/linux-checks.sh physics c-units`:

| Section | What it does |
|---|---|
| `leaks` | `vita/shepherd/derived_leaks.py` (without a local `engine/**/Derived/` it reports VACUOUS and passes), and checks that no tracked file matches `.gitignore` |
| `python` | every tracked `.py` file compiles |
| `receiver` | the devlog receiver's unit tests (`tools/devlog-receiver/test_receiver.py`, standard library only, 127.0.0.1 only) |
| `shell` | every tracked shell script parses with the shell its `#!` line names (most are zsh) |
| `c-units` | the Vita host's helper tests (`vita/shepherd/mac-check/test_*.c`), the System.Native shim's pure blocks (`vita/runtime/tests/test_{paths,redirect,readahead}.c`), the devlog client self-test under ASan + UBSan |
| `physics` | builds `vita/physics` (Box2D 2.3.1 + the Unity layer) and runs the T1 scenarios against `tests/golden`, self-tests S0-S7 and the trace replay |
| `audio` | builds the portable audio library (`vita/audio`) and its tools (build only) |
| `dotnet` | builds every C# project that needs no game DLLs and runs `tools/K10CacheTest` |

The script's header lists what it skips and why: anything that needs the game, vitasdk, the runtime fork, or macOS.

## 1. Toolchain

Set the environment variables first. The full table is in [BUILD.md](../../BUILD.md#environment-variables). The ones you
always need:

```bash
export SHEPHERD_REPO=$HOME/src/shepherd        # this checkout
export SHEPHERD_WORK=$HOME/shepherd-work       # build trees and toolchain checkouts: needs tens of GB
export VITASDK=$HOME/vitasdk
```
Paths must not contain spaces (CMake and the Vita toolchain break on them).

1. **vitasdk** ([vitasdk.org](https://vitasdk.org)), then **vitaGL** and **vitaShaRK** from its package manager (`vdpm`).
2. **.NET 8 SDK**, **Python 3**, **CMake**, **Ninja**. Add **Docker or OrbStack** if you want the off-device harness (step 5).
3. **The Mono runtime fork.** Clone dotnet/runtime at tag `v8.0.31` into `$SHEPHERD_WORK/runtime`, then:
   ```bash
   vita/toolchain/setup-fork.sh "$SHEPHERD_WORK/runtime"
   ```
   This applies `vita/patches/dotnet-runtime-v8.0.31-shepherd-vita.patch`, with no network access.
   ```bash
   vita/toolchain/build-runtime-vita.sh
   ```
   This builds `libmonosgen-2.0.a` for the Vita (interpreter + sgen GC).
   ```bash
   vita/toolchain/gen-offsets.sh && vita/toolchain/build-cross.sh
   ```
   This builds `mono-aot-cross`, the AOT compiler that runs on the Mac and emits ARMv7 code for the Vita.
4. Optional, **LLVM AOT**: `vita/llvm/fetch-llvm.sh` (downloads the official Mono LLVM 16 packages, sha256-pinned), then
   `vita/llvm/build-cross-llvm-armhf.sh`. Read `vita/llvm/README.md` first.
5. **Unity's crunch fork** into `engine/Shepherd.Assets/native/crunch-unity`. The commit is in
   [THIRD-PARTY-NOTICES.md](../../THIRD-PARTY-NOTICES.md).

## 2. Your game's files

Everything here needs **your own installed copy** of the game. Point Shepherd at it:
```bash
export SHEPHERD_GAME_DIR=<the folder of your installed game>
export SHEPHERD_GAME_DATA=<the game's Unity Data folder>     # the one that holds Managed/
```

1. **Rewrite Unity's internal calls.** This gives every Unity internal call a managed body that calls into the engine:
   ```bash
   dotnet run --project tools/Rewriter -- "$SHEPHERD_GAME_DATA/Managed" runtime/Managed
   ```
   For the Vita profile, run it again with `--vita`, writing to `runtime/ManagedVita`:
   ```bash
   dotnet run --project tools/Rewriter -- "$SHEPHERD_GAME_DATA/Managed" runtime/ManagedVita --vita
   ```
   Each run also writes `icalls.json`. Turn it into the engine's flat copy (`icalls.bin`):
   ```bash
   dotnet run --project tools/IcallBake -- runtime/ManagedVita/icalls.json
   ```
2. **The Vita asset pack.** `tools/VitaPack` builds it from a recorded engine boot (`SHEPHERD_PACK_RECORD`). See the header of
   its `Program.cs`.
3. **Audio.** `tools/FmodBake` converts the game's FMOD banks for Shepherd's audio engine and writes under `runtime/audiobake/`.
   The bank key is read in memory and never stored. See `tools/FmodBake/README.md`.

Everything these tools write is derived from the game. It stays under `runtime/`, which git ignores. Never commit it.

## 3. The Mac engine

Most work and testing happens here. The Mac engine runs the same engine code as the Vita, with real input, and writes its logs to
`runtime/logs/`. As written it is macOS-only: it loads `libshepphys.dylib`, `libshepaudio.dylib` and macOS system calls.

Build the two native libraries it loads:
```bash
zsh vita/physics/build.sh mac
```
```bash
vita/audio/build.sh mac
```
The engine looks for them under `vita/{physics,audio}/build/mac/`, or wherever `SHEPHERD_PHYS_LIB` and `SHEPHERD_AUDIO_LIB` point.

Then build the engine (it needs `runtime/Managed` from step 2) and run the game in a window:
```bash
dotnet build engine/Shepherd.Engine
```
```bash
SHEPHERD_ROOT="$SHEPHERD_REPO" ./play.sh
```

Every Unity internal call the engine does not implement yet is logged to `runtime/logs/missing-icalls.txt`, with its signature.
For a new game, that file is your work list.

## 4. The Vita build

```bash
vita/shepherd/build.sh
```
This builds a VPK. Its recipes and switches are documented in its header, and it ends with the gates in `check_vpk.py`.
The VPK holds the engine, the runtime and the .NET base class library only, never game files.
```bash
vita/shepherd/stage-owner.sh
```
This assembles `ux0:data/shepherd/` (the game's managed DLLs, the pack, the saves sandbox) from **your** files, for you to copy to
the memory card.

A launcher build loads the engine as a module. Its imports are checked against `vita/shepherd/module-load-libs.txt`: a module that
imports a library that is not loaded yet is refused with `0x8002D003`.

## 5. Off-device: vlx

`vita/vlx/` relinks the Vita build's own objects into an ARM Linux program and runs it under QEMU (Docker or OrbStack, image
`alpine:3.20`, no network). Use it before every device run, so a crash costs seconds instead of a memory-card round trip.
Read `vita/vlx/README.md`.

## 6. On the Vita

You need a PS Vita with HENkaku / Enso (taiHEN), and `libshacccg.suprx` (the runtime shader compiler) extracted with ShaRKF00D.

1. Copy the staged `shepherd/` folder (step 4) to `ux0:data/shepherd/` on the memory card.
2. Install the VPK with VitaShell, then run it.
3. The logs land in `ux0:data/shepherd/`. With the devlog client they can also come back over Wi-Fi (`vita/devlog/`,
   `tools/devlog-receiver/`).

## When something goes wrong

- **A script stops at its first lines.** Read its header: most scripts check their prerequisites first (the runtime checkout, the
  cross build, the work volume) and say which one is missing.
- **The Vita refuses to load a module with `0x8002D003`.** Compare its imports with `vita/shepherd/module-load-libs.txt`.
- **The Mac engine stops in a scene.** Look in `runtime/logs/missing-icalls.txt` and the engine log.
- **Something about the layout is unclear.** See [ARCHITECTURE.md](ARCHITECTURE.md) and the [GLOSSARY](GLOSSARY.md).
