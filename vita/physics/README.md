# vita/physics: libshepphys (stage 0a: the native Box2D core; physics Stage 0: runtime, pool, settings check, trace; Stage 1: the Unity layer; Stages 2, 3, 6; E1; Stage 4a, 4b)

The native physics core of the Shepherd engine (plan: `physics-plan-v2.md`, Path A): **Box2D 2.3.1, vendored unmodified**
(`box2d/`, zlib), a minimal flat C API of ours on top (`shim/`), and the T1 determinism scenarios (`tests/`). Physics Stage 0 added
the runtime and its pool, the settings check against Box2D's constants, the trace writer, B3 (the Mac's no-op b2Timer) and the
engine binding (`engine/Shepherd.Engine/Physics/ShepPhys.cs`). Physics Stage 1 added the Unity layer (`shim/sp_unity.cpp`: Unity bodies
with a pose mirror in the pool, shapes under the collider-to-body affine, the transform sync, MovePosition, write-back flags,
interpolation, render poses), the Unity contact filter and mass data in the core, B2 (Box2D's allocations in a fixed arena) and the
replay of an engine drive's trace on the Mac and on QEMU's Cortex-A9. Zero Box2D alterations. Physics Stage 2 added patch P1
(`box2d/PATCHES.md`: Unity's trigger phase and kinematic triggers behind world flags), contact serials and the Unity queries
(`shim/sp_query.cpp`). Physics Stage 3 (ABI 5) added combat: the mirror's force accumulators, inverse mass and pre-step velocity (AddForce /
AddTorque with 0 native calls, impulses through `sp_u_apply`), frozen position axes, the H-DAMP variants (Box2D's damping mapped per step),
one-sided edges / chains for H-CHAIN (a PreSolve rule in the core's contact listener), and the contact snapshot a Collision2D is built from
(`sp_u_contacts_between`); still no Box2D file changed beyond P1. Physics Stage 6 (ABI 6) added the 3D island-ground ray
(`shim/sp_mesh3d.cpp`): 3D scenes of static colliders -- MeshCollider triangles with a BVH in the mesh's vertex space, Box / Sphere /
Capsule analytic --, rays with PhysX 4.1's arithmetic as remembered (the hit point's form is the hypothesis H-RAYPOINT), the +z fast path
(a per-collider XY grid, bit for bit the BVH's answer) and the layers-present early-out; traced and replayed like the 2D core.
E1 (ABI 7, shim 0.7.0; the Unity editor oracle's decisions, `vita/notes/physics-ledger.md`) added patch P2 (`box2d/PATCHES.md`: Unity's
per-step speed clamps, `sp_world_set_clamps`, traced), `SP_USTEP_MOVEGRAV_ON` (a move step keeps gravity), H-SYNC's tolerance 9.5e-5, and
Unity's shape casts (`shim/sp_query.cpp`: Box2D's b2TimeOfImpact with the skins, the centroid at the sweep's pose, the point and normal from
the contact manifold; a cast starting overlapped reports the manifold at the origin), with ClosestPoint including the skin and bounds leaving
a box's edgeRadius out.
Stage 4 step 4a (ABI 8, shim 0.8.0) added `SP_USF_CHAIN_WELD`: Unity 2022.3's chain vertex filter, measured on primitives in the E1 Unity
project (scenes Paths4a / Paths4a2) -- an edge / loop point within the threshold of the last point kept is dropped, in body space after the
affine (`b2_linearSlop^2` for a loop, i.e. a CompositeCollider2D outline; 2.01 x `b2_linearSlop^2` for an open chain, i.e. an EdgeCollider2D);
the engine sets it at level 4 and above; S1's WELD record checks it (Mac = QEMU). No Box2D file changed.
Stage 4 step 4b (ABI 9, shim 0.9.0) added patch P5 (`box2d/PATCHES.md`: Unity 2022.3's polygon weld, (0.5 `b2_linearSlop`)^2 squared,
behind the shape flag `SP_SHAPE_UNITY_WELD`, traced) and `SP_USF_UNITY_POLY`: Unity's build of one piece of its convex decomposition --
its weld, Box2D's gift-wrap hull, Unity's collinear removal (unit edges' dot > 0.99999, while more than 3 remain) -- measured on the E1
project (scenes Paths4b..Paths4b5); the pieces themselves (libtess2's, H-DECOMP) come from the engine's owner-local decomposer, not from
this library. `sp_shape_get_polygon` reads a fixture's polygon back (unit4b, Unity's GetShapes). S1's UPOL record checks it (Mac = QEMU).
Notes: `vita/notes/physics-stage0a.md`, `vita/notes/physics-stage0.md`, `vita/notes/physics-stage1.md`, `vita/notes/physics-stage2.md`,
`vita/notes/physics-stage3.md`, `vita/notes/physics-stage6.md`, `vita/notes/physics-ledger.md` (E1), `vita/notes/physics-stage4.md` (Stage 4),
`vita/notes/physics-k6.md`.

| path | what |
|---|---|
| `box2d/` | Box2D 2.3.1 core, the upstream archive with patches P1 (Stage 2), P2 (E1) and P5 (Stage 4b) in place (`P1.diff`, `P2.diff`, `P5.diff`); `README.md` (source, sha256, licence), `PATCHES.md` (every change, its switch and its flag-off proof; the build-level substitutions B1-B3) |
| `shim/shepphys.h`, `shepphys.cpp` | the flat C API: int32 handles, int32/uint32/float structs with pinned sizes, floats through memory, `shepphys_proc(name)` lookup |
| `shim/sp_trig.c`, `sp_trig_override.h` | deterministic `sp_sinf` / `sp_cosf`, force-included into Box2D's translation units (no Box2D file changed) |
| `shim/sp_core.cpp` | Stage 0: `sp_init` / `sp_shutdown`, the pool (one base, uint32 offsets, the header block at offset 16, deterministic segregated free lists; a Mac grow, a Vita failure), `sp_settings_check` (Physics2DSettings decoded at run time vs Box2D's compiled constants), `sp_nop` |
| `shim/sp_trace.{h,cpp}` | Stage 0: the trace writer -- every mutating API call (arguments, what they read, results; a pose hash per step; event drains) appended to a binary file |
| `shim/sp_timer_stub.cpp` | Stage 0, B3: the Mac library's no-op b2Timer (b2Timer.cpp's own `#else` bodies); `build.sh prof` keeps the stock timer |
| `shim/sp_unity.cpp`, `sp_internal.h` | Stage 1: the Unity layer (Unity worlds with a ground body; bodies + mirror records; shapes: circles, boxes, polygons with convex decomposition, capsules as rounded boxes, edges / composite loops as chains, negative scale re-wound; `sp_u_sync` (H-SYNC), `sp_u_step` (movement states, history, write-back flags), `sp_u_interp` / `sp_u_early` (slots C / A), `sp_u_snapshot` / `sp_render_poses` (section 1.6)); every change through the traced C API |
| `shim/sp_b2glue.cpp` | Stage 1, B2: `b2Alloc` / `b2Free` / `b2Log` / `b2_version` in place of `b2Settings.cpp` (Box2D's allocations in the fixed arena of `sp_core.cpp`) |
| `tests/sp_s1.cpp` | Stage 1 self-test of the Unity layer (MovePosition arrival, the 45-degree slide ratios, ghost vertices, a door box, interpolation, render poses, decomposition, the filter, Unity mass, Stage 4a's chain vertex filter (WELD), Stage 4b's piece build (UPOL), B2), hashed the T1 way: the Mac and QEMU must agree |
| `shim/sp_mesh3d.cpp` | Stage 6: the 3D scene (`sp_scene3d_*`, `sp_mesh3d_*`, `sp_col3d_*`, `sp_ray3d`): meshes + BVH, primitives, the rays (closest / per collider / any; triggers, backfaces, the layer mask), the +z fast path, the layers-present early-out, the trace ops |
| `tests/sp_s6.cpp` | Stage 6 self-test (the tilted Unity plane's hit bits, culling, primitives, filters, modes, a mirrored plane, 20,000 +z rays fast = general = brute), hashed the T1 way: the Mac and QEMU must agree |
| `tests/sp_s2.cpp`, `tests/sp_s3.cpp` | Stage 2 / Stage 3 self-tests (P1's probes, serials, queries / forces, impulses, H-DAMP, restitution, the clamp, frozen axes, one-sided chains, contact snapshots), hashed the T1 way: the Mac and QEMU must agree |
| `tests/sp_s0.c` | Stage 0 self-test (pool, settings check), hashed the T1 way: the Mac and QEMU must agree |
| `tests/sp_t1.*` | 8 scenarios (falling, stacking, chains, kinematic sweep, top-down slide, sensor sequences, queries, crowd bench) hashed record by record |
| `tests/golden/` | golden records (`<name>.txt`, float bit patterns + decimals for reading) and `t1_golden.h` (hashes compiled into both libraries) |
| `tests/t1_runner.c` | Mac driver: `info`, `record`, `check`, `time` (`--fz` for the flush-to-zero what-if); Stage 0: `s0`, `trace <dir>` (every scenario traced and replayed), `replay <file>`; Stage 1: `s1`, and `replay` of an engine drive's trace |
| `vita/t1_main.c` | the T1 test eboot (SHPD00020, "Shepherd Phys T1"): runs and self-checks the scenarios on the device, prints FPSCR |
| `qemu/` | bare-metal harness: the Vita archive's objects on QEMU's emulated Cortex-A9 (silent, offline); Stage 1: replays `replay.sptr` (an engine drive's trace, through semihosting) when present (`build.sh qemu-replay <trace>`) |
| `tools/check_syms.sh` | Vita link check: external symbols, ELF undefineds, FMA / NEON scan |
| `check-phys.sh` | the physics bar's runner (native `build.sh all`, Box2D vs the archive, the engine's `physcheck all` at levels 0 to 4 and 6 (since E1 only H-NONALLOC keeps a variant run; level 4 adds Stage 4a's unit4 and 4b's unit4b and `physcheck composite`), `room` / `room3d` on the owner's install, the optional intro drive (levels 1-3, and 4 and 6 with `INTRO6=1`) three times with the pose hashes, the check modes and the traces replayed on the Mac and QEMU, the optional combat drive) |

Build (`zsh vita/physics/build.sh [mac|vita|qemu|verify|golden|san|prof|all]`; CMake + Ninja from the `~/src` path, build trees on
`$SHEPHERD_WORK/physics1/build`):

- `build/mac/libshepphys.dylib` (+ `.a`, `t1_runner`): Mac arm64, `-O2 -ffp-contract=off -fno-exceptions -fno-rtti`, only the
  `sp_*` / `shepphys_*` entry points exported; Box2D's b2Timer replaced by the no-op (B3). `build/mac-prof/libshepphys.a`: the same
  with the stock timer (b2Profile phase splits; PhysBench's `physbench_mac_prof`).
- `build/vita/libshepphys_vita.a`: `arm-vita-eabi`, `-mcpu=cortex-a9 -mfpu=neon -mfloat-abi=hard -O2 -ffp-contract=off
  -fno-exceptions -fno-rtti`; needs only newlib (`libc`, `libm`), `libsupc++` (operator delete) and `libgcc` when linked.
- `build/vita/shepherd_phys_t1.vpk` / `eboot.bin`: the device self-check (writes `ux0:data/shepherd/physics-t1.txt`).

`build/` and `out/` are git-ignored. The goldens are synthetic scenes (no game data) and are tracked; re-record them only
after an intended behaviour change (`build.sh golden`), from the canonical build (arm64, own trig, `-ffp-contract=off`).

Kit #8 (Law of the Vita, part 1; `vita/notes/kit8-physics.md`): the play apps' eboots (00097 / 00098 / 00095) link
`libshepphys_vita.a` (`vita/shepherd/build.sh` `SHEPHERD_PHYS_LINK`, default on for GL + audio builds; the library's own
`shepphys_proc` answers the engine's binding). The host (`vita/shepherd/phys_host.inc.c`) sets the play apps' default level
(`SHEPHERD_PHYS_DEFAULT`, 6; the env file's `SHEPHERD_PHYS=0` / `none` = kit #7), hands the runtime a pool block from the arena's share
(`sp_host_memory`, `SHEPHERD_PHYS_BLOCK_MB`, default 1 MiB: the pool a quarter, B2's arena the rest; `t1_runner host` checks that the
self-tests' hashes are the same with it) and points the engine at the owner folder's `data/physics2d-settings.bin` (the project's
Physics2DSettings record, which the strict card pack lacks; `Shepherd.Assets.Cli phys2d`, `stage-owner.sh`).

Kit #9 (Law of the Vita, part 2 = Stage 7 item 2; `vita/notes/kit9-physics.md`): **patch P3** (`box2d/PATCHES.md`) and the worker pool.
While the pool runs (`sp_init_def.workers` / `sp_jobs_start`, 1..3 native threads, never attached to Mono; the Vita host gives its own
threads through `sp_host_jobs`, the Mac uses pthreads, the QEMU harness has none), every `b2World::Step` may fork Collide's narrowphase,
P1's final trigger pass and the island solve (`shim/sp_p3.cpp`; thresholds `sp_jobs_set`, default 16 / 16 / 4) and applies the results in
Box2D's own serial order -- bit-identical to the serial step, which runs unchanged below the thresholds and whenever the pool is off.
`shim/sp_jobs.cpp` is the pool (spin, then sleep; each fork carries the caller's FPSCR). Checks: `build.sh mac` runs T1 / S1 / S2 / S3 /
S7 / the traces with 1, 2 and 3 workers (`t1_runner ... --workers N --jcheck`) against the serial hashes; `tests/sp_s7.cpp` (S7) builds
worlds for the forked phases (Mac = QEMU); `build.sh tsan` = 0 ThreadSanitizer reports; `check-phys.sh` step 6 runs the engine's checks
and drives with the workers.
