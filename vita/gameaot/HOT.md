# game-aot study: HOT METHODS (method-level execution profiles)

Which methods of which assemblies the Vita build runs, how often, for how long, and where execution would cross between AOT code
and the interpreter under each candidate AOT split (default VPK, engine AOT, + rewritten Unity modules, + game, profile-guided
partial sets). Results: `$SHEPHERD_WORK/gameaot/profile.md` and `$SHEPHERD_WORK/gameaot/profiles/` (owner-local:
the method lists name the owner's game / Unity-derived methods; never in git or a VPK).

## Tools (this directory)

| file | what |
|---|---|
| `tools/hotprof/hotprof.c` + `build.sh` | Mac-only Mono profiler module (public profiler API, call instrumentation): every managed call of **every thread** inside the SimStats window (`Diag/ProfMarks`), per method calls / inclusive / exclusive time, every caller->callee edge, optional frame segments (`SHEPHERD_HOT_SEGMENTS=806,1786,2935` with an absolute window), and per-method metadata for AOT decisions (IL size, flags, `mono_signature_full_name` = the key mono-aot-cross's `profile=` loader matches). Derived from `vita/v6/tools/interp-prof` |
| `hot.sh` | the harness: `build` (publish this worktree), `run <idle|tour|intro> <plain|prof|profni> <name>`, `rounds <n> "<scenarios>" "<modes>" <prefix>` (interleaved). Vita profile, null renderer, interpreter, coop, the Vita GC shape, SHEPHERD_AUDIO=off, output-muted check per run; private snapshots under `$G/hot/snap`; every output under `$G/hot` |
| `hotan.py` | per scenario / segment: per-assembly and per-group tables, per-method lists (`methods/<asm>.tsv`), the hot list outside the engine-AOT set with its coverage curve, AOT<->interpreter transitions per configuration (C0..C5, P/PH partial sets) with call sites |
| `hotunion.py` | union of the partial sets over scenarios, per assembly, plus `.aotprofile` files (non-generic methods) for `mono-aot-cross --aot=...,profile=<f>,profile-only` |
| `hotsum.py`, `hotasm.py` | cross-scenario tables (`profiles/overview.md`) and the loaded-assembly inventory with executed counts (`profiles/assemblies-loaded.*`) |
| `analyze.sh` | runs all of them over the rounds -> `$G/profiles` |
| `hotcheck.sh <set dir>` | compiles every `.aotprofile` of a set with the Vita cross compiler (`profile=…,profile-only`): resolved methods, compiled count, A32 text -> `$G/hot/aotcheck/` (owner-local compiled code) |
| `pads/` | `pad-tour-rel.txt` (polish round 1's menu tour relative to the menu, = menuspeed's), `pad-main-joy.txt` (polish round 1's intro run) |

## Reproduce

```sh
export TMPDIR=$SHEPHERD_WORK/tmp
zsh vita/gameaot/hot.sh build                                   # ~5 s
zsh vita/gameaot/hot.sh rounds 6 "idle tour intro" "plain prof" r   # ~40 min (intro: 110 s plain, 190 s profiled)
zsh vita/gameaot/hot.sh rounds 2 "idle tour intro" "profni" n       # exact counts (interpreter inlining off)
zsh vita/gameaot/analyze.sh                                     # -> $SHEPHERD_WORK/gameaot/profiles
```
Inputs: `$G/hot/snap/{Data,pack,persistent}` (clones of menuspeed's 2026-10-07 snapshots: game Managed + runtime/ManagedVita +
the owner-local Shepherd.Engine.Derived.dll; the strict menu pack; sandbox saves) and `$G/hot/snap/GameData` (symlinks to the
game's Data files, read only, for the pack-less intro run; Managed -> the snapshot).

## Model notes

- Interpreter inlining: the interpreter inlines small non-virtual callees (IL < 20 bytes, no clauses...) whatever their execution
  mode (`transform.c: interp_method_check_inlining` has no AOT test), so interp->AOT transitions are counted from the inline-ON
  profile; AOT->interp from the inline-OFF one, minus callees the caller's AOT compile would inline (IL < 20, or AggressiveInlining
  <= 256, non-virtual, inline allowlist of the caller's image).
- Every non-inlined call between an interpreted method and an AOT method crosses: direct calls via MINT_JIT_CALL, virtual and
  delegate calls via `do_jit_call` (interp.c MINT_CALLVIRT_FAST / MINT_CALL_DELEGATE check `mono_interp_jit_call_supported`);
  AOT code calls an interpreted method through an interp-entry trampoline.
- `(wrapper dynamic-method) InvokeStub_*` (the engine's reflection behaviour messages) is always interpreted.
