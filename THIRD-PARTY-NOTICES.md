# Third-party notices

Shepherd's own code is MIT (LICENSE). This file lists third-party code that is **in this repository** and third-party software the
build **uses but does not include**.

## In this repository
| Component | Where | Licence | Notes |
|---|---|---|---|
| Box2D 2.3.1 (Erin Catto) | `vita/physics/box2d/` | zlib (`vita/physics/box2d/License.txt`) | Altered: patches P1, P2, P5 are listed and marked in `vita/physics/box2d/PATCHES.md` (with `P*.diff`), as the zlib licence requires |
| Patches to dotnet/runtime v8.0.31 (.NET Foundation and Contributors) | `vita/patches/`, `vita/llvm/fork-*.patch`, `vita/gameaot/fork-*.patch`, `vita/gameaot/mechanics/*.patch`, `vita/toolchain/version-files/`, `vita/toolchain/offsets-arm-vita.h` | MIT (dotnet/runtime) | All target `src/mono` / the runtime's build files (the `vita/llvm` ones Mono's LLVM backend in dotnet/runtime, not LLVM itself); they contain upstream context lines; our changes are MIT as well |
| System.Native prototypes | `vita/runtime/system-native/` | MIT (dotnet/runtime `src/native/libs/System.Native/pal_*.h`) | Signatures and data layouts follow upstream headers; the implementations are ours |

Our own code written from public standards (no third-party code): the AES / SHA / MD5 / HMAC / PBKDF2 primitives
(`vita/runtime/system-native/crypto/`, tables generated from their definitions), the devlog SHA-256 / HMAC
(`vita/devlog/devlog_sha256.c`), and the corrected `sem_post` that replaces one function of vitasdk's pthread-embedded at link time
(`vita/runtime/system-native/vita_pthread_fix.c`, written from the observed behaviour; no pthread-embedded code).

## Used by the build, not included
| Software | Licence (upstream) | How it is obtained |
|---|---|---|
| dotnet/runtime v8.0.31 (Mono, CoreLib, BCL) | MIT | You clone it; `vita/toolchain/setup-fork.sh` applies our patches |
| Mono LLVM 16 tools (runtime.osx-arm64.Microsoft.NETCore.Runtime.Mono.LLVM.*) | Apache-2.0 WITH LLVM-exception | `vita/llvm/fetch-llvm.sh` downloads them from the dotnet8 feed (sha256-pinned) |
| vitasdk, newlib, pthread-embedded | various (BSD / LGPL / ...) | vitasdk installer |
| vitaGL, vitaShaRK | see their repositories | vitasdk package manager (vdpm) |
| Unity's crunch fork (Unity-Technologies/crunch) | zlib | Clone into `engine/Shepherd.Assets/native/crunch-unity` (commit 8708900eca8ec609d279270e72936258f81ddfb7) |
| Mono.Cecil, Microsoft.Bcl.AsyncInterfaces | MIT | NuGet (restored by `dotnet build`) |
| QEMU (via Docker / OrbStack, image alpine:3.20) | GPL-2.0 | Only for the off-device harness; runs as a separate program |

## Never included, never redistributed
- **The game you port**: its assemblies, Unity's managed assemblies that ship with it (UnityEngine.*.dll), its assets, audio banks
  and keys. The build reads them from your own install; packs, rewritten assemblies and bakes made from them stay on your machine.
- **libshacccg.suprx** (Sony's runtime shader compiler): each user extracts it from their own Vita with ShaRKF00D.
- **FMOD** and **Spine** runtimes: the game brings its own; Shepherd implements a compatible surface for FMOD and calls the game's
  Spine assemblies, and contains no code from either.
