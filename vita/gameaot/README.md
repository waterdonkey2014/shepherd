# vita/gameaot: owner-local AOT of the Unity and game modules

Tooling for compiling the rewritten Unity modules (runtime/Managed*, derived from the owner's copy) and the game / third-party
assemblies as **their own owner-local AOT modules** for the Vita build (Mono fork, full AOT + interpreter, coop, static modules,
armv7 hard-float), plus the measurement tools used to decide whether to do it. Today these assemblies always run in the
interpreter; only the engine and the BCL set are AOT-compiled.

**Owner-local only.** Everything this tooling produces (AOT objects/images, eboots, VPKs, `.aotprofile` files, method lists,
profiles, run logs) is compiled from or describes the owner's game files: it lives on the build volume
(`$SHEPHERD_WORK/gameaot/`), never in git, never in a shareable VPK. Git holds only scripts, patches and module-name
lists. The shareable default VPK and `vita/shepherd/build.sh` are not changed by anything here.

## Status (2026-10-13, kit 4: in the play app)

- **Adopted as SHPD00095 "Cult of the Lamb (play, game AOT)"** (owner-only; `vita/notes/game-aot.md`): `vita/shepherd/build.sh`
  `SHEPHERD_AOT_OWNER=1` on the play MT recipe (GL + audio + engine AOT + derived AOT), 25 owner modules partial
  (`owner-modules-p.txt`), the gameaot compiler + the fix-A runtime (both checked by build.sh), the owner GUID gate,
  `SHEPHERD_GAME_AOT=0` as the per-case switch back. Headless check build SHPD00190. `build-gameaot.patch` is superseded (kept as
  the prototype's record; it targets the old build.sh). The open items below are closed: owner-only IDs 00095 / 00190, Derived
  through `SHEPHERD_AOT_DERIVED`, the fix-A build check, empty lists refused.
- First armv7 execution of partial images (vlx): PASS; Mac real-GL frames byte-identical to the interpreter's; Mac proxy idle logic
  -30 %, menu tour x1.5. Cost: +6.6 MiB of eboot = 7 MiB less GC heap than 00098 on the device (125 -> 118; k4 review).
- `play/macp.sh`: the Mac proxy of the three execution models (interp / ad / p) from one engine build and the card's owner folder.

## Status (2026-10-07)

- Built and measured on the Mac proxy (vita/llvm/llvm-aot.md §8 method); one owner-local Vita variant built and gated.
  **Nothing has run on a Vita.**
- Recommended shape ("P"): all owner modules the menu + intro load, each its own module, **profile-guided partial** in strict
  mode, Mono A32 backend, runtime fix A, CoreLib with larger trampoline pools. About +9 MiB of eboot; 1.6-1.7x the engine-AOT
  build at the idle menu on the proxy. After the menu-speed hop fix the per-frame gain is small (idle +7-15 %, steady gameplay
  ~0); the large remaining gain is shorter load / first-use hitches. LLVM for the engine set is the bigger per-frame lever.
- Adopt only after menu-speed is merged, as an opt-in owner-only build. Open before any install: owner-only TITLE_ID (the
  prototype's SHPD00012 collides with PhysBench), rebase `build-gameaot.patch` onto fid1-fixup's build.sh (Derived goes to
  `SHEPHERD_AOT_DERIVED`), a build-time check that the linked runtime carries fix A, and `grep ... || true` for empty lists.
- Full write-up (owner-local, build volume): `$SHEPHERD_WORK/gameaot/` (`mechanics.md`, `sizes.md`, `profile.md`,
  `prototype.md`, `verify-0/`, `verify-1/`).

## Layout

| path | what |
|---|---|
| `mechanics/` | partial-AOT mechanics on synthetic code: profile-only in full AOT + interp with static modules; `fork-partial-aot.patch` (fix A runtime: uncompiled struct methods via unbox trampolines; B: keep the interp-in / gsharedvt-out wrappers; C: opt-in gsharedvt guard; D: `SHEPHERD_AOT_PROFILE_STRICT`, no blanket generic instances). README inside |
| `fork-gsharedvt-unop.patch` | one-line compiler guard for the CoreModule gsharedvt crash (root fix for defect C) |
| `SIZES.md`, `aot-module.sh`, `aot-all.sh`, `link-gameaot.sh`, `sizes_report.py`, `census-run.sh` | size study: which modules the menu + intro load, per-module whole / partial AOT for A32 and Thumb, owner-local link checks |
| `HOT.md`, `tools/hotprof/`, `hot.sh`, `hotan.py`, `hotsum.py`, `hotunion.py`, `hotasm.py`, `hotcheck.sh`, `analyze.sh`, `pads/`, `pad-main.txt` | hot-method study: all-thread call profiler, interleaved vita-sim rounds, AOT/interp crossing model per AOT split, partial sets and `.aotprofile` writer |
| `aotprof/`, `mechanics/aotprof/` | `.aotprofile` writers (hot study / mechanics study) |
| `proto/` | prototype: Mac proxy configs and interleaved bench (`bench.sh`), compilers (`build-compilers.sh`), profile selection (`profsel.py`), hop model (`hopmodel.py`), trampoline counter (`trampcount/`), LLVM cold-wrapper check (`llvmcheck/`), Vita variant build (`vita.sh`, `vita-sizes.sh`), module lists `modules-rewritten.txt` (27) and `modules-game.txt` (60). `PROTO.md` explains how to rerun |
| `owner-modules-p.txt` | kit 4: the owner modules build.sh's `SHEPHERD_AOT_OWNER=1` compiles partial (25; selection in the file and `vita/notes/game-aot.md` §2) |
| `play/macp.sh` | kit 4: Mac proxy of the play app's execution models (interp / ad / p): build, AOT, real-GL identity runs and comparisons, interleaved null-renderer timing rounds |
| `build-gameaot.patch` | (superseded by build.sh `SHEPHERD_AOT_OWNER=1`, kit 4) adds `SHEPHERD_AOT_OWNER=1` to `vita/shepherd/build.sh` (with `SHEPHERD_AOT_ENGINE=1`): owner module lists + one profile, owner objects outside the packaged set, owner GUID gate, larger CoreLib pools, `owner-modules.tsv` manifest. Not applied in the repo; inert when unset (default VPK byte-identical) |
| `check_owner_guids.py` | owner GUID gate: every owner AOT image's MVID against the staged owner DLLs (a mismatch is fatal at load) |

## Module split (exact)

- Shareable default VPK: unchanged (12 BCL + Shepherd.Assets/Hooks).
- Engine-AOT build: + Shepherd.Engine (whole); engine foreign-code gate must stay 0. Owner modules never inline into it: owner
  images use `SHEPHERD_AOT_INLINE_IMAGES=<owner set>,System.*,Microsoft.*,mscorlib,netstandard` (no `Shepherd.*`).
- Owner-local: the 27 modules of `proto/modules-rewritten.txt` and the 60 of `proto/modules-game.txt`, each its own AOT module.
  Shepherd.Engine.Derived moves to fid1-fixup's `SHEPHERD_AOT_DERIVED` once that lands.

## Rules for anything built here

- Outputs only under `$SHEPHERD_WORK/gameaot/`; `TMPDIR=$SHEPHERD_WORK/tmp`.
- Runs are silent (null audio, output muted, checked before and after) and use copies of `runtime/` and saves, never the real
  game, Steam or save folders.
- Timing claims: >= 6 interleaved rounds, medians with ranges; separate steady frames from load / hitch frames.
- Owner builds use an owner-only TITLE_ID and an `*-OWNER-ONLY.vpk` name and are installed only on the owner's own Vita.
