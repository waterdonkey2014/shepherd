# Changelog

## Unreleased (after 0.4.0) -- device kits 9 and 10 of the first port
- Kit 9, runtime + memory: fast thread-local storage, typed internal-call stubs, AOT de-duplication, concurrent garbage collection,
  JIT round 2 (recorded paths, calls into AOT code, getter intrinsics), Unity-exact random numbers, the logos' decoder fixes.
- Kit 10, loading: a chunked LZ4 pack read and unpacked ahead, the game's code DLLs in one file, never-run code stored compressed,
  object creation by replayed field programs, learning caches with Cache Size / Clear Cache / Verify Files rows, an optional boot
  loading screen, both logos to their last frame, extra GC size classes + a memory-pressure ladder, an optional shared memory pool,
  an optional switch for the PC's float rules.
Device numbers (one PCH-2000): idle Main Menu ~25 fps at 444 MHz / ~27 fps at ~500 MHz; boot to the menu 104.5-136.8 s depending on
the app (-17 to -24 % vs kit 9).

## 0.4.0 -- early public release of the engine
The engine as it stands after device kit 8 of the first port (Cult of the Lamb), released before polish:
- Mono (.NET 8) runtime fork for the Vita: interpreter, static AOT, LLVM AOT (ARMv7 Thumb-2), copy-and-patch baseline JIT.
- Shepherd.Engine: Unity internal calls in C#, lifecycle, scenes, GL renderer on vitaGL (single- and multi-threaded), Unity-exact 2D
  physics on Box2D, audio engine standing in for FMOD, input (pad, touch, rear-touch), video, sandboxed saves with backups.
- Vita host: launcher eboot + engine module, memory budgets, clocks (measured), suspend handling, black-box logs, VPK build with gates.
- Tools: IL rewriter, Vita pack builder, icall bake, FMOD bank bake, API-surface scanner, budget reports, devlog receiver.
- Off-device harness (vlx): the Vita build's own objects run on a Mac under QEMU.
Device numbers (one PCH-2000): idle Main Menu 2.3 fps (kit 1) -> ~22 fps at 444 MHz / ~25 fps at ~500 MHz; boot to menu ~6 min -> ~2.3 min.
Known gaps: gameplay frame rates, the full game, DLCs, first-game assumptions in places, machine-specific build defaults.
Known broken: `tools/VitaBudget` (an early tool written against an older Shepherd.Assets API; does not build) -- a good first fix.
Checked for this release: every other C# project builds from a clean export (24 of 25), Python files compile, shell scripts parse.
