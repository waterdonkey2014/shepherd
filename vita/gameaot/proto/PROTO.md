# game-aot PROTOTYPE: tooling

Owner-local AOT of the rewritten Unity modules and the game / third-party assemblies, built and measured: Mac proxy timings
(macbench method: full AOT + interpreter, osx-arm64 images of the same module sets), hop counts, Vita (armv7) builds of the
owner-local variant with its gates. Report: `$SHEPHERD_WORK/gameaot/prototype.md` (owner-local, build volume).
Everything these scripts write is under `$SHEPHERD_WORK/gameaot/{proto,vita-out}`: compiled game / Unity code, profiles and
method lists of the owner's game. Never in git, never in a shareable VPK. Nothing here runs on a Vita.

| file | what |
|---|---|
| `build-compilers.sh` | the four AOT compilers (Mac osx-arm64 `mono-sgen` with / without LLVM, Vita `mono-aot-cross` with / without LLVM) from one private fork tree: `runtime-fix` (mechanics' `fork-partial-aot.patch` A-D) + the sizes study's `fork-gsharedvt-unop.patch` |
| `bench.sh` | the Mac proxy harness: `build`, `snap`, `closure`, `profiles`, `aot <backend> <full\|part> <set>`, `cfg <config>`, `run`, `rounds` (interleaved, rotating order). Configs A, B, C, P, D, DP, E, DA, H, HP, HC, I, HI (see its header) |
| `summary.py` | per window (idle, tour, intro boot / menu / quote / gameplay): median of window means [range], render / logic, paired per-round difference vs A, alloc, GCs, outcome, live heap, device projection (x43 / x65) |
| `profsel.py` | `.aotprofile` helpers that keep generic class instances (`ginst`, `merge`, `stat`) |
| `hopmodel.py` | AOT<->interpreter crossings per frame for these exact AOT sets (the hot study's model and profiles) |
| `hopfix-prof.sh` | the hot study's exact-count profiles of the menu-speed engine (config HI), for hopmodel's H / HP / HC rows |
| `trampcount/` | lldb counter of CoreLib trampoline pool use per type (pool sizing) |
| `llvmcheck/coldwrap.py` | defect E check: is each LLVM image's GC-poll cold wrapper inside the runtime's LLVM code range? |
| `vita-sizes.sh`, `vsize.py` | Vita object sizes of module sets with the prototype's compilers (through `../aot-module.sh`) |
| `vita.sh` | the owner-local Vita variant: a private tree with `../build-gameaot.patch` applied to a COPY of `vita/shepherd/build.sh` (`tree`, `owner`, `build <P\|C\|B\|CORE\|NONE>`, `gates`) |
| `modules-rewritten.txt`, `modules-game.txt` | the module split (27 rewritten Unity modules; 60 game / third-party incl. Unity packages and Shepherd.Engine.Derived) = the sizes study's census of the menu + intro run |
| `../build-gameaot.patch` | `SHEPHERD_AOT_OWNER=1` for `vita/shepherd/build.sh` (not applied in the repo; inert when unset: proven by building both) |

Rerun (about 2 h of machine time; `export TMPDIR=$SHEPHERD_WORK/tmp`):

```sh
D=vita/gameaot/proto
zsh $D/build-compilers.sh src && for c in mac mac-llvm cross cross-llvm; do zsh $D/build-compilers.sh $c; done
zsh $D/bench.sh build && zsh $D/bench.sh snap && zsh $D/bench.sh closure
# generic class instances: gameaotprof runs of config I (see prototype.md §2), then
zsh $D/bench.sh profiles
for b in nollvm llvm; do zsh $D/bench.sh aot $b full eng; zsh $D/bench.sh aot $b full unity; done
zsh $D/bench.sh aot nollvm full game; zsh $D/bench.sh aot nollvm part owner; zsh $D/bench.sh aot nollvm full engh
zsh $D/bench.sh cfg A B C P D DP E DA H HP HC
zsh $D/bench.sh rounds 8 "A B C P D DP E H HP HC DA" "idle tour intro" s
python3 $D/summary.py $SHEPHERD_WORK/gameaot/proto/runs/s[0-9]*
zsh $D/vita.sh tree && zsh $D/vita.sh owner && zsh $D/vita.sh build P && zsh $D/vita.sh gates P
```
