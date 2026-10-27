# kit #4 intro-plan tools

The measurement tools behind `vita/notes/kit4-intro-plan.md` (New Game / intro feasibility, 2026-10-13). They assume the track's
worktree `$SHEPHERD_WORK/kit4/intro` (W) and work area `$SHEPHERD_WORK/kit4/intro-work` (K, scripts copied there).
Everything they produce (packs, recordings, logs, card copies) stays on the build volume; packs are derived game data.

| file | what |
|---|---|
| `run.sh <name> <frames> <shotEvery> [ENV=VAL ...]` | one engine run (CoreCLR; `BIN=` picks Debug `bin` or Release `binrel`) in its own sandbox root: Managed/ManagedVita/derived symlinked, a fresh copy of the sandbox saves; Vita profile, GL + DXT, `SHEPHERD_AUDIO=off`; logs + shots -> `K/runs/<name>/`; mute state logged |
| `bench.sh <interp\|aoteng\|aotd> <name> <from:to:heap> <stop> [ENV=VAL ...]` | the Mono proxies of the Vita execution models on the intro (from `vita/llvm/macbench.sh`): interpreted engine, engine AOT, engine + derived-module AOT (kit #3's play apps); null renderer, strict intro pack, absolute pad frames -> `K/mac/runs/<name>/` |
| `sampinc.py <sample.txt> <symbol...>` | inclusive main-thread share of symbols in a macOS `sample` call graph (outermost occurrence) |
| `samptree.py <sample.txt> <root> <depth> <min %>` | the main thread's call tree below a symbol |
| `pads/intro-explore.txt` | menu -> Play -> empty slot -> New Game -> quotes -> Game Biome Intro -> dialogue presses, stick/attack/dodge loop to f4800 -> pause menu (look around, close) |
| `pads/intro-quit.txt` | the same to the intro, then pause -> Main Menu -> confirm (Accept): the scene re-load defect |
| `pads/vlx-intro.txt` | the device pad.txt form of intro-explore up to f4400 (vlx runs of the play eboot on the frame clock) |
| `pads/menu-idle.txt` | press any button at f1050, then idle (sampling the idle menu) |

vlx runs used kit #3's driver unchanged (`$SHEPHERD_WORK/kit3/kit-build/scripts/vlx-run.sh`, copied to K/scripts) with
`VLX_SRC_TREE`/`VLX_VB` = kit3/kit-build2 (the kit's own objects), `VLX_OWNER` = a card copy holding the new pack, `VLX_USER_KIB` for
the extra-RAM run. Diag switches used (engine, all off unless set): `SHEPHERD_CENSUS_NAMED`, `SHEPHERD_CENSUS_DUPARRAYS`,
`SHEPHERD_FIXED_TIMING`, `SHEPHERD_EXP_ARRAYSHARE`, `SHEPHERD_EXP_ARRAYSHARE_CHECK`.
