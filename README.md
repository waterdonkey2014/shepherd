# Shepherd

**A stand-in for the Unity runtime that runs a Unity game's own compiled C# on the PS Vita.**

Shepherd keeps the game's managed code exactly as shipped and replaces what sits under it: a .NET 8 Mono runtime ported to the Vita
(interpreter, ahead-of-time compilation, LLVM AOT and a baseline JIT), Unity's internal calls re-implemented in C#, a renderer on
vitaGL, a Unity-exact 2D physics layer on Box2D, an audio engine that stands in for FMOD, an asset pack builder, and an off-device
harness that runs the Vita build on a Mac under QEMU. The rule over everything: **fidelity first** -- the game should behave and look
exactly like the original; anything that deviates is opt-in.

> **Status: early release (v0.4.0; the code here is newer -- see CHANGELOG).** This is the engine as it is, released before it is polished so that people porting other games
> -- with Claude or by hand -- can fill its gaps and send findings back. Expect rough edges, hard-wired assumptions from the first game,
> and build scripts that grew on one machine.

## Where it stands
Shepherd is for any Unity game; its first port is **Cult of the Lamb** (Unity, macOS build as the source), on one PS Vita (PCH-2000,
firmware 3.65 + Enso). On the device today: boot, title, Main Menu (~25 fps idle at 444 MHz, from 2.3 fps at kit 1), Settings,
Credits, New Game to the opening, touch and pad input, audio, video, sandboxed saves, 2D physics matching Unity on the tested scenes;
boot to the Main Menu ~1.7 minutes. Not yet: gameplay on the device, gameplay frame rates, whole games. The tricks, gotchas and ideas
behind it, for any game, are in the companion Claude plugin below.

That port's kit-by-kit page, **Shepherd Progress**, lives in its own repository: [https://waterdonkey2014.github.io/shepherd-progress/](https://waterdonkey2014.github.io/shepherd-progress/).

## You need
- **Your own copy of the game** you port. Shepherd never ships game files, and its build reads them only from your own install.
- A PS Vita with HENkaku / Enso (taiHEN), and `libshacccg.suprx` extracted with ShaRKF00D (the runtime shader compiler).
- A Mac with Apple silicon for the full build: the toolchain scripts, the Mac engine (Homebrew SDL3) and FmodBake assume macOS today.
  On Linux the C# tools build; the rest still needs porting.
- zsh (most scripts), [vitasdk](https://vitasdk.org) (with vitaGL from its package manager), the .NET 8 SDK, Python 3, CMake / Ninja.
- Docker or OrbStack (only for the off-device harness).

See [BUILD.md](BUILD.md) for the steps.

## Layout
| Path | What |
|---|---|
| `engine/Shepherd.Engine` | The Unity stand-in: icalls, lifecycle, scenes, rendering, physics, audio, input, Vita-specific runtime pieces |
| `engine/Shepherd.Assets` | Unity asset reader (serialized files, bundles, textures; crunch through Unity's crunch fork) |
| `engine/Shepherd.Hooks` | Small helpers the IL rewriter wires into game assemblies |
| `tools/` | Mac-side tools: IL rewriter, Vita pack builder, icall registry bake, FMOD bank bake, API-surface scanner, budget reports |
| `vita/shepherd` | The Vita host (C): boot, memory, clocks, input, video, launcher, the VPK build and its gates |
| `vita/toolchain`, `vita/patches` | Building the Mono runtime fork for the Vita from dotnet/runtime v8.0.31 + our patches |
| `vita/llvm`, `vita/jit` | LLVM AOT for ARMv7 Thumb-2, the copy-and-patch baseline JIT |
| `vita/physics` | Box2D 2.3.1 (vendored, zlib) + a flat C API with Unity's behaviours behind flags |
| `vita/audio` | The native audio engine |
| `vita/vlx` | Off-device harness: the Vita build's own objects relinked for ARM Linux and run under QEMU |
| `vita/tests`, `vita/gameaot`, ... | Tests, the game-AOT pipeline, probes, devlog, smaller experiments |
| `docs/` | [Architecture](docs/ARCHITECTURE.md), [porting another game](docs/PORTING-ANOTHER-GAME.md), [newcomers](docs/newcomers/) |

## The companion Claude plugin
The knowledge behind this engine -- measured Vita hardware facts, gotchas that cost device runs, proven and failed techniques, a
labelled idea catalog, and portable tools (import checker for `0x8002D003` module loads, VPK checker, run history, log puller) -- is a
Claude Code plugin: **Shepherd for PS Vita** (marketplace `shepherd-vita`, plugin `shepherd`). In Claude Code:
```
/plugin marketplace add waterdonkey2014/shepherd-vita-plugin
/plugin install shepherd@shepherd-vita
```

## Contributing
Read [CONTRIBUTING.md](CONTRIBUTING.md). The two hard rules: **no game files and no decompiled code in this repository** (ports of a
game's or third party's code stay local -- see `vita/shepherd/derived_leaks.py`), and **label every number** (device-measured,
emulated, estimate).

## Licence
MIT for Shepherd's own code -- see [LICENSE](LICENSE). Third-party parts keep their own licences: [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
Shepherd is not affiliated with Unity Technologies, Sony Interactive Entertainment or any game's developer or publisher.
