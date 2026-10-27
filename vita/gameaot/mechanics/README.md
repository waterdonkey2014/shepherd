# GAME-AOT study: partial-AOT mechanics

How the Mono fork AOT-compiles a subset of an assembly's methods (profile-only) while the rest stays interpreted, in the Vita's
full AOT + interpreter mode with static modules; the four defects stock Mono 8 has in that mode and the fork patch that fixes
them. Report (owner-local, build volume): `$SHEPHERD_WORK/gameaot/mechanics.md`. Everything here is synthetic test code
or tooling; outputs go to `$SHEPHERD_WORK/gameaot/` only.

| path | what |
|---|---|
| `fork-partial-aot.patch` | against the fork tree (`git apply --check` passes): A runtime (unbox trampoline of an uncompiled value-type method -> interpreter), B compiler (keep the gsharedvt_out_sig / interp_in wrappers of compiled methods in profile-only images), C compiler opt-in `SHEPHERD_AOT_NO_GSHAREDVT=1`, D compiler opt-in `SHEPHERD_AOT_PROFILE_STRICT=1`. B-D leave every non-profile build byte-identical |
| `aotprof/gameaot_aotprof.c`, `build.sh` | Mac-only Mono profiler module: counts interpreted calls of selected images (whole run or the vita-sim window) and writes a `.aotprofile` (+ TSV) of the hot methods, generic class instances and the value-type closure; `--profile=gameaotprof:out=<p>,images=<A;B>,min=<n>[,window][,vt=0][,closure=<A;B>]` |
| `aotprof/aotprofile.py` | `dump`, `empty`, `select`, `merge` for `.aotprofile` files |
| `exp/` | the two-assembly experiment: `Lib` (stand-in for an owner module) and `App` (stand-in for the engine, AOT'd with the inline allowlist), `host_exp.c` (embeds the osx-arm64 Mono pack like the Vita host: static modules via `mono_aot_register_module`), `Loader/` (loads assemblies for `closure=`), `mech.sh` |

Rerun (about 5 minutes; needs the fork's Mac compiler `obj/mono-osx-llvm`, and for `forkfix` / the working partial images a
patched build: clone the fork, apply the patch, build `mono-sgen` into `$SHEPHERD_WORK/gameaot/obj/mono-osx-fix` as
`vita/llvm/build-mac-llvm.sh` does without `-DLLVM_PREFIX`):

```
export TMPDIR=$SHEPHERD_WORK/tmp; X=vita/gameaot/mechanics/exp/mech.sh
$X build && $X bcl && $X aot-base && $X profile && $X aot-part && $X hosts
$X checks          # every configuration vs the interpreter's 39 results
$X forkfix         # unpatched / patched fork runtime x compiler matrix, inertness
$X guid            # GUID / version gate behaviour
$X rounds 8        # interleaved timing rounds -> bench-summary.md
```
