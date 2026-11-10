# Changelog

## Unreleased (after 0.4.0) -- device kits 9 to 14 of the first port
- Kit 9, runtime + memory: fast thread-local storage, typed internal-call stubs, AOT de-duplication, concurrent garbage collection,
  JIT round 2 (recorded paths, calls into AOT code, getter intrinsics), Unity-exact random numbers, the logos' decoder fixes.
- Kit 10, loading: a chunked LZ4 pack read and unpacked ahead, the game's code DLLs in one file, never-run code stored compressed,
  object creation by replayed field programs, learning caches with Cache Size / Clear Cache / Verify Files rows, an optional boot
  loading screen, both logos to their last frame, extra GC size classes + a memory-pressure ladder, an optional shared memory pool,
  an optional switch for the PC's float rules.
- Kit 11, memory for gameplay: a shared memory pool by default in the play apps, a smaller engine record footprint, a C heap census;
  opt-in GPU rows (exact uniforms, skipped no-op draws, vertex reuse); a quieter log, DMA copies and learned AOT lists by default;
  measurement tools and hardware probes.
- Kit 12, memory part 2 + menus: a refused GC evacuation copy handled in place, evacuation at 90 %, a cleanup at every scene change,
  the asset index as a view over the object table, compressed app code, no IL sequence points; exact text-mesh / colour / raycast
  caches, uGUI stencil masks, a PS Vita Settings tab, a boot sequence with a loading screen on the launcher apps.
- Kit 13, the Main Menu as on the PC: perspective-correct world draws, the game's own crowd / shrine / logo shaders, the Settings mask
  fix, Press X = Cross only, the launchers' boot sequence fixed, an emblem crossfade, cheaper canvas layout and masks, more memory
  groundwork. Kit 13.1: on the device kit 13 crashed just after the title -- a render target drawn and released in one frame was
  freed before the GPU had used it; its delete now waits 5 frames, and the off-device harness models the VRAM pool and the GPU's
  late writes.
- Kit 14, the walk: memory rows (a compact game catalog, the pack's read-ahead windows, smaller newlib / stack reserves, shared generic
  code, trimmed library code: the eboots 17-19 MiB smaller) so that New Game through the opening walk and the first fight fits the
  device's memory partitions.
Device numbers (one PCH-2000): idle Main Menu ~25 fps at 444 MHz (kit 12, 39.5 ms) / 26.5 fps at ~500 MHz (kit 11); boot to the menu
~101-107 s (kit 12, warm; kit 10: 104.5-136.8 s); room for the game (GC heap cap) 146-158 MiB (kit 12). Kit 13 crashed after the
title (fixed in 13.1, not yet run on the device). Kit 14 is built and not yet run on the device: on the off-device harness at the
device's memory partitions the walk and the first fight finish in 8 of 12 runs of the multithreaded play app, the others run out of
memory late in the fight; video memory in the fight is an ESTIMATE (~1.5-3 MiB short).

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
