# vita/physics/physbench: PhysBench (the physics plan's Stage 0 device micro-benchmark, D4)

This is a Vita eboot (**SHPD00012, "Shepherd PhysBench"**) with a Mac counterpart. Both run the same C/C++ bench source
against libshepphys (Box2D 2.3.1, unaltered, plus our shim). The plan's Vita budget depends on what they measure. The
owner's instructions are in `HOWTO.md`; the results and findings are in `vita/notes/physics-stage0a.md` §PhysBench. The
scenes are synthetic, built from scene-data.md's counts only, so the VPK holds no game data.

| file | what |
|---|---|
| `pb_scene.{h,c}` | the five scenes: `intro_fight`, `intro_fight_stock`, `dungeon_room`, `base_never`, `base_sleep`. Each has its per-step script (MovePosition velocities, hitbox teleports, swipes, knockbacks, NPC wander) and a pose hash. Plain C with no libc formatting, so it also runs bare-metal under QEMU |
| `pb_b2.{h,cpp}` | the same scenes built straight in Box2D, plus prototypes of the plan's parallel phases (Evaluate / TestOverlap per contact into slots) and of a child-aware A* broadphase query |
| `pb_bench.{h,c}` | the cases: T1 vs goldens (exact, or ulp + first difference), STEP, B2, CALL, ASTAR, FJ, NP, MW, SUMMARY |
| `pb_pool.{h,c}`, `pb_plat.h`, `pb_plat_{vita,mac}.c` | a fork-join pool (spin / block / hybrid), with workers on USER_1 / USER_2 on the Vita |
| `vita_main.c` | the eboot: logging (`ux0:data/shepherd/physbench.txt` + `.prev`), clocks (444, 333, and 500 only with PSVshell), FPSCR, the Mac-reference ratios |
| `mac_main.c` | the Mac host: 5 passes, best of, writing `out/mac_ref.h` (keyed by the sources' sha) and `out/physbench-mac.txt` |
| `pb_timer_stub.cpp` | the Mac reference links Box2D's no-op `b2Timer`, as the Vita build has it (gettimeofday is not counted). `physbench_mac_prof` links without it |
| `qemu/pb_qemu.c` | the eboot's scene objects on QEMU's Cortex-A9: every scene hash must equal the Mac's |
| `tools/mk_golden.py` | `../tests/golden/*.txt` → `out/pb_t1_golden.h` (record-by-record T1 comparison) |
| `build.sh` | `lib`, `mac`, `vita`, `qemu`, `tsan`, `selftest`, `all`. Products and reports go to `out/` (git-ignored) and build trees to `$SHEPHERD_WORK/physics1/build/physbench` |

Run `zsh vita/physics/physbench/build.sh` (everything, about 20 s from clean). It writes:
- `out/shepherd_physbench.vpk`, `out/eboot.bin`
- `out/mac_ref.h`, `out/physbench-mac.txt`, `out/physbench-mac-prof.txt`
- `out/vita-check.txt`, `out/qemu-scenes.txt`, `out/tsan-summary.txt`, `out/selftest.txt`
