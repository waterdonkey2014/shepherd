# Building Shepherd

> Early release: these scripts grew on one Mac (Apple silicon, macOS) and were made path-neutral for this release with environment
> variables. Expect to adapt things; issues and fixes are welcome. Every script has a header that explains it in detail -- read it
> before running it.

## Environment variables
| Variable | What | Default |
|---|---|---|
| `SHEPHERD_REPO` | This checkout | `$HOME/src/shepherd` |
| `SHEPHERD_WORK` | Build trees, toolchain checkouts, temporary files (needs tens of GB) | `$HOME/shepherd-work` |
| `SHEPHERD_GAME_DIR` | The folder of **your own** installed game (for the first game: the Steam folder that holds the macOS app) | none -- set it |
| `SHEPHERD_GAME_DATA` | The game's Unity `Data` folder (read by the engine, the project files and most tools) | none -- set it |
| `VITASDK` | vitasdk | `$HOME/vitasdk` |
| `UNITY_APPS` | Where Unity editors live (only for the physics oracle tests) | `/Applications/Unity` |
| `NINJA` | The ninja binary | `ninja` on your PATH |
Paths must not contain spaces (CMake / the Vita toolchain).

## 1. Toolchain
- vitasdk, then vitaGL and vitaShaRK from its package manager (`vdpm`).
- .NET 8 SDK (`dotnet`), Python 3, CMake, Ninja; Docker or OrbStack for the off-device harness.
- The runtime fork: clone dotnet/runtime at tag `v8.0.31` into `$SHEPHERD_WORK/runtime`, then
  ```
  vita/toolchain/setup-fork.sh "$SHEPHERD_WORK/runtime"      # applies vita/patches/dotnet-runtime-v8.0.31-shepherd-vita.patch
  vita/toolchain/build-runtime-vita.sh                        # configure-vita.sh + libmonosgen-2.0.a for arm-vita-eabi
  vita/toolchain/gen-offsets.sh && vita/toolchain/build-cross.sh   # mono-aot-cross (host -> armv7-vita-eabihf)
  ```
  The patches in `vita/patches/` (baseline JIT, interpreter cache, diagnostics) and `vita/llvm/` are applied by the scripts that
  need them; their headers say which.
- Optional LLVM AOT: `vita/llvm/fetch-llvm.sh` (downloads the official Mono LLVM 16 packages, sha256-pinned), then
  `vita/llvm/build-cross-llvm-armhf.sh`.
- Unity's crunch fork into `engine/Shepherd.Assets/native/crunch-unity` (see THIRD-PARTY-NOTICES.md for the commit).

## 2. Your game's files (local only, never committed)
All of these read your install and write under `runtime/` in this checkout (git-ignored):
- `tools/Rewriter` -- rewrites Unity's internal calls in the game's managed folder into calls to the engine:
  `dotnet run --project tools/Rewriter -- "$SHEPHERD_GAME_DATA/Managed" runtime/Managed` (add `--vita` and `runtime/ManagedVita` for the
  Vita profile), then `tools/IcallBake` on the resulting `icalls.json`.
- `tools/VitaPack` -- builds the Vita asset pack from a recorded boot (`SHEPHERD_PACK_RECORD`), see its Program.cs header.
- `tools/FmodBake` -- converts the game's FMOD banks for the Shepherd audio engine (the key is read in memory, never stored).

## 3. The engine on the Mac
```
export SHEPHERD_GAME_DATA=<your game>/...app/Contents/Resources/Data     # the Unity Data folder
dotnet build engine/Shepherd.Engine                                   # also needs runtime/Managed from step 2
SHEPHERD_ROOT="$SHEPHERD_REPO" ./play.sh                              # a windowed run of the game on the Shepherd engine
```
The Mac engine is where most work and testing happens: same engine code as the Vita, real input, logs in `runtime/logs/`.

## 4. The Vita build
```
vita/shepherd/build.sh            # -> a VPK; recipes and switches are documented in its header; ends with check_vpk.py's gates
vita/shepherd/stage-owner.sh      # assembles ux0:data/shepherd/ (managed DLLs, pack, saves sandbox) from YOUR files
```
Copy the staged folder to the memory card, install the VPK with VitaShell, run. Logs land in `ux0:data/shepherd/`.
A launcher build loads the engine as a module: its imports are checked against `vita/shepherd/module-load-libs.txt`
(a module that imports a library not yet loaded is refused with `0x8002D003`).

## 5. Off-device: vlx
`vita/vlx/` relinks the Vita build's own objects into an ARM Linux ELF and runs it under QEMU (Docker / OrbStack, image
`alpine:3.20`, no network). Read `vita/vlx/README.md`. Use it before every device run; when the device fails in a way vlx did not,
teach vlx that behaviour.
