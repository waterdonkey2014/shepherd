# Shepherd architecture

```
 the game's own assemblies (unchanged)          your install
 Unity's managed assemblies (UnityEngine.*)     your install, internal calls rewritten by tools/Rewriter
 ------------------------------------------------------------------------------------------------
 Shepherd.Hooks.Icalls.Call(id, args)  -->  Shepherd.Engine (C#): icalls, lifecycle, scenes, assets,
                                            rendering, physics, audio, input, video, saves
 ------------------------------------------------------------------------------------------------
 .NET 8 Mono runtime fork: interpreter | static AOT | LLVM AOT | baseline JIT (copy-and-patch)
 BCL (CoreLib + subset), System.Native shim for the Vita
 ------------------------------------------------------------------------------------------------
 Vita host (C): launcher eboot -> engine module, memory, clocks, threads, input, video decoder, logs
 native libraries: vitaGL (GL ES on GXM), Box2D + Unity layer (libshepphys), audio engine
 ------------------------------------------------------------------------------------------------
 PS Vita (HENkaku / Enso, taiHEN), ux0:data/shepherd/ = the user's staged files
```

## How a Unity game runs on it
1. **Internal calls.** Unity's managed assemblies declare thousands of `[MethodImpl(InternalCall)]` methods implemented in Unity's
   native player. `tools/Rewriter` gives each one a managed body that forwards to `Shepherd.Hooks.Icalls.Call(id, args)`, and writes
   `icalls.json` (id, key, signature). `Shepherd.Engine` implements them (`engine/Shepherd.Engine/Icalls/`); a missing one is logged
   to `runtime/logs/missing-icalls.txt` with its signature -- the work list for a new game.
2. **Lifecycle.** The engine drives Awake / OnEnable / Start / Update / LateUpdate / FixedUpdate / coroutines in Unity's order,
   loads scenes from the game's own serialized files (`engine/Shepherd.Assets`), and keeps Unity's timing rules (`Runtime/PlayClock.cs`).
3. **Rendering.** The scene renderer collects sprites, meshes, particles, UI and text, sorts them the way Unity does, and draws through
   a GL backend (desktop GL on the Mac, vitaGL on the Vita; optionally on a render thread). The Vita build is checked to hand the GPU
   the same draw stream as the Mac engine, frame by frame.
4. **Physics.** Unity's 2D physics is Box2D underneath; Shepherd runs Box2D 2.3.1 with Unity's behaviours added behind flags, checked
   against the Unity editor as an oracle (`vita/physics`, `engine/Shepherd.Engine/Physics`).
5. **Audio.** Games that use FMOD get a compatible surface over Shepherd's own audio engine, playing the game's banks converted by
   `tools/FmodBake`.
6. **Execution tiers.** Engine and BCL are AOT-compiled into the eboot (LLVM Thumb-2 by default); game code runs interpreted, AOT
   (an owner-local build of the game's assemblies) or through the baseline JIT where the system allows executable memory.

## Fidelity tools
- Identity checks: per-frame render-command hashes and audio-stream hashes on scripted routes; the Mac engine vs the Vita build (vlx).
- Lockstep: a new execution tier compared against the interpreter.
- Oracles: Unity editor projects for physics and API behaviour (`vita/tests/oracle`).
- Strict-original switch: `SHEPHERD_STRICT_ORIGINAL=1` turns every non-original behaviour off (`Runtime/StrictOriginal.cs`).

## Where game data lives
Never in this repository and never in a VPK. The build reads your install; rewritten assemblies, packs, bakes and caches stay under
`runtime/` locally or in `ux0:data/shepherd/` on your Vita. Ports of decompiled code stay in your local `engine/**/Derived/`.
