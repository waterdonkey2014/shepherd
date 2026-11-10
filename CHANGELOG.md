# Changelog

Numbers measured on one game are not listed here: they belong to that game's port, not to the engine. Names in the code still carry
the number of the test build ("kit") a change arrived in (`K10Store`, `SHEPHERD_K13_...`); see the glossary.

## Unreleased (after 0.4.0)
**Runtime**
- Fast thread-local storage, typed internal-call stubs, AOT de-duplication, shared generic code, trimmed library code (smaller
  executables), no IL sequence points.
- Baseline JIT round 2: recorded paths, calls into AOT code, getter intrinsics; learned lists of hot methods for AOT.
- The game's code DLLs in one file; never-run code stored compressed.

**Memory**
- Concurrent garbage collection, extra GC size classes, a memory-pressure ladder; a refused evacuation copy handled in place;
  a cleanup at every scene change.
- An optional shared memory pool; a smaller engine record footprint; the asset index as a view over the object table; a compact
  game catalog; stale records and finished async operations let go; a string pool; lazy skins; right-sized newlib and stack reserves;
  a C heap census.

**Loading**
- A chunked LZ4 pack read and unpacked ahead, with read-ahead windows; object creation by replayed field programs.
- Learning caches on the memory card, with Cache Size / Clear Cache / Verify Files settings rows.

**Rendering and UI**
- Perspective-correct world draws; support for a game's own custom shaders (their ports stay on the porter's machine).
- uGUI stencil masks; cheaper canvas layout (only what changed) and masks; exact text-mesh, colour and raycast caches.
- A render target's memory is held until the GPU has used it (fixes a crash when a target is drawn and released in one frame).
- Opt-in GPU rows: exact uniforms, skipped no-op draws, vertex reuse.
- A PS Vita tab in the game's settings; "Press X" means Cross only at the title.

**Video and boot**
- Video playback through the hardware decoder to the last frame; an optional loading screen and boot sequence on the launcher apps.

**Fidelity**
- Unity-exact random numbers; an optional switch for the PC's float rules.

**Tools**
- The off-device harness (vlx) models the VRAM pool and the GPU's late writes, and checks decoder pointers like the device.
- Measurement tools, hardware probes, a picture-check tool against reference frames; a quieter log and DMA copies by default.

## 0.4.0 -- early public release of the engine
The engine as first released, before polish:
- Mono (.NET 8) runtime fork for the Vita: interpreter, static AOT, LLVM AOT (ARMv7 Thumb-2), copy-and-patch baseline JIT.
- Shepherd.Engine: Unity internal calls in C#, lifecycle, scenes, GL renderer on vitaGL (single- and multi-threaded), Unity-exact 2D
  physics on Box2D, audio engine standing in for FMOD, input (pad, touch, rear-touch), video, sandboxed saves with backups.
- Vita host: launcher eboot + engine module, memory budgets, clocks (measured), suspend handling, black-box logs, VPK build with gates.
- Tools: IL rewriter, Vita pack builder, icall bake, FMOD bank bake, API-surface scanner, budget reports, devlog receiver.
- Off-device harness (vlx): the Vita build's own objects run on a Mac under QEMU.
Known gaps: gameplay on the device, whole games, DLCs, first-game assumptions in places, machine-specific build defaults.
Known broken: `tools/VitaBudget` (an early tool written against an older Shepherd.Assets API; does not build) -- a good first fix.
Checked for this release: every other C# project builds from a clean export (24 of 25), Python files compile, shell scripts parse.
