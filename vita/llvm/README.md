# vita/llvm — Mono LLVM AOT for the PS Vita (build, gates, speed proxy)

Everything here is opt-in and lives beside the current Vita build: nothing in `vita/shepherd/`, `vita/hello/`, `engine/` or the
existing `obj/cross-vita` / `obj/vita-runtime` is changed. Results, decisions and risks: `vita/notes/llvm-aot.md`.

Status: **built and link-checked, not run on a device.**

## Quick start

```sh
hdiutil attach "$HOME/shepherd-build.sparsebundle"     # $SHEPHERD_WORK
export TMPDIR=$SHEPHERD_WORK/tmp
L=$SHEPHERD_REPO/vita/llvm
$L/fetch-llvm.sh             # the two owner-approved Mono LLVM 16 packages (sha256-pinned) -> $SHEPHERD_WORK/llvm/sdk/arm64
$L/build-cross-llvm.sh       # LLVM-enabled mono-aot-cross -> $SHEPHERD_WORK/obj/cross-vita-llvm (~70 s)
$L/aot-vita.sh hello thumb   # AOT a module set: hello|engine x nollvm|arm|thumb -> $SHEPHERD_WORK/llvm/aot/<set>-<variant>
$L/link-vita.sh hello thumb  # link + vita-elf-create + vita-make-fself -> $SHEPHERD_WORK/llvm/out/<set>-<variant>/eboot.bin
$L/gates.sh hello thumb      # GUID gate, 28 MiB module gate, foreign-code gate (engine set), LLVM EH/call-table checks
python3 $L/sizes.py $SHEPHERD_WORK/llvm/aot/engine-{nollvm,arm,thumb}             # per-module size tables
python3 $L/instcount.py $SHEPHERD_WORK/llvm/aot/engine-{nollvm,arm,thumb}          # hot-method static instruction counts
```

The fork needs `fork-llvm.patch` (one gated Thumb-2 fix, already applied in `$SHEPHERD_WORK/runtime`; apply it after
`vita/toolchain/setup-fork.sh` on a fresh checkout). It changes nothing in any non-LLVM build (proof in the notes, §11).

**Kit #5: the armhf float-argument fix** (`vita/notes/kit5-llvm.md`). Without it every LLVM build passes float / double arguments in
the wrong registers (`llvm-aot.md` §14) and stays NOT-FOR-DEVICE; the review's exception fix (`fork-eh-vfp.patch`: compiler + the LLVM
apps' runtime) is needed too, or a caught exception corrupts LLVM frames' d8-d15 (`kit5-llvm.md` "Review fixes"). The fixed compiler is built from a private copy of the fork:

```sh
DEST=$SHEPHERD_WORK/<your dir> $L/build-cross-llvm-armhf.sh     # fork copy + fork-llvm-armhf-cc.patch + fork-eh-vfp.patch -> $DEST/obj/cross-llvm-armhf
SHEPHERD_VITA_RUNTIME_EH_VFP_OBJ=$DEST/obj/vita-runtime-eh-vfp $L/build-runtime-eh-vfp.sh   # the LLVM apps' runtime (review fix B + C)
SHEPHERD_AOT_LLVM=thumb SHEPHERD_CROSS_LLVM_OBJ=$DEST/obj/cross-llvm-armhf SHEPHERD_VITA_RUNTIME_OBJ=$DEST/obj/vita-runtime-eh-vfp \
  SHEPHERD_GL=1 SHEPHERD_AOT_ENGINE=1 SHEPHERD_AOT_DERIVED=1 SHEPHERD_TEST_CLOCK=1 vita/shepherd/build.sh   # SHPD00191 (00099 recipe + LLVM)
ABIT_DIR=$SHEPHERD_WORK/<your dir>/abitest $L/abitest/abitest.sh base     # the ABI + exception test (then: llvm <v> <compiler>; run <v> <runtime>)
python3 $L/check_eh_tramps.py <work>/aot/System.Private.CoreLib.dll.o              # the throw trampolines' fp_regs offsets (fix A)
$L/check-derived-identity.sh <out dir> ...                                         # one Derived DLL for every derived-AOT app of a kit
python3 $L/cc_alloc_test.py <llc> thumbv7-vita-eabihf 1500 "" 21 agg,se  # the ABI test: C convention + r8 vs Mono's add_float
llvm-dis <M>.dll.s.bc -o - | python3 $L/cc_consistency.py -            # the IR check (conventions + swifterror at every call)
```

## Speed proxy on the Mac (no device)

```sh
$L/build-mac-llvm.sh                     # native osx-arm64 mono-sgen with LLVM (the AOT compiler for the Mac images)
$L/macbench.sh aot nollvm; $L/macbench.sh aot llvm
for c in interp jit aot-nollvm aot-llvm aot-nollvm-eng aot-llvm-eng; do $L/macbench.sh pub $c; done
RPFX=b $L/macbench.sh rounds 6 interp jit aot-nollvm aot-llvm aot-nollvm-eng aot-llvm-eng
python3 $L/instcount_arm64.py $SHEPHERD_WORK/llvm/mac/aot-{nollvm,llvm}/Shepherd.Engine.dll.dylib   # arm64 calibration
```

`macbench.sh` is the V6 vita-sim menu benchmark (`vita/v6/sim.sh`, `SIM_REL=1 run`) copied, with its own snapshots, roots and
output dirs under `$SHEPHERD_WORK/llvm/mac/`, and the Vita's execution model (`--full-aot-interp`). The arm64 numbers are
a proxy for armv7. The Mac stays silent (null renderer, FMOD stubs).

## Files

| file | what |
|---|---|
| `fetch-llvm.sh` | downloads + verifies + unpacks `runtime.osx-arm64.Microsoft.NETCore.Runtime.Mono.LLVM.{Sdk,Tools}` 16.0.5-alpha.1.25311.1 (dotnet8 feed) the way `src/mono/llvm/llvm-init.proj` does |
| `build-cross-llvm.sh` | `vita/toolchain/build-cross.sh` + `-DLLVM_PREFIX` (what `mono.proj` adds for `MonoAOTEnableLLVM`); copies `opt`/`llc` next to the compiler (`out/llvm-bin`, the `llvm-path=`) |
| `aot-vita.sh` | static full+interp AOT of the hello set (11 modules) or the engine-AOT set (13), in three variants: `nollvm` (a snapshot of today's compiler), `arm` (LLVM A32), `thumb` (LLVM Thumb-2); the Vita build's knobs (inline cap 256, CoreLib trampoline pools, direct icalls/P/Invokes, the engine's inline allowlist) |
| `link-vita.sh` | links the eboot from snapshot host objects/tables/shim/runtime archives (`$SHEPHERD_WORK/llvm/inputs/`), runs vita-elf-create / vita-make-fself, writes `checks.txt` (undefined symbols, process params, AOT module symbols, globals, veneers, relocation types, sections) |
| `gates.sh` | the Vita build's gates for these outputs |
| `foreign-gate.sh` | the engine-AOT foreign-code gate for both objects of an LLVM image (LLVM method names are checked after stripping the `Shepherd_Engine_` prefix every one carries in a static build); used by `gates.sh` and `shepherd-build-llvm.patch`; `--self-test`. fid1-fixup D1: the owner-local `Shepherd.Engine.Derived` module's prefix is accepted only in its own image |
| `check_aot_guids_llvm.py` | the GUID gate for LLVM output (the tables live in `<M>-llvm.o`) |
| `check_llvm_eh.py` | static check of what the runtime will read: the Mono EH table vs the linked ELF (table order = address order, LLVM code range, Thumb bits, CIE constants, CFA ops/registers Mono's unwinder decodes) and the call table decoded like `get_call_table_entry` |
| `sizes.py`, `instcount.py`, `instcount_arm64.py` | size tables; static per-method instruction counts (armv7 objects; arm64 Mac dylibs) |
| `macbench.sh`, `macbench_summary.py`, `pad-menu-dlc-rel.txt` | the Mac speed proxy (copied from `vita/v6/sim.sh` / `sim_summary.py` / `pad-menu-dlc-rel.txt`) |
| `build-mac-llvm.sh` | native osx-arm64 mono-sgen with LLVM (`obj/mono-osx-llvm`) |
| `fork-llvm.patch` | the fork change (2 files, gated `TARGET_VITA && ENABLE_LLVM`) |
| `shepherd-build-llvm.patch` | what changes in `vita/shepherd/build.sh` to build the Vita eboots with LLVM (`SHEPHERD_AOT_LLVM=thumb|arm`); APPLIED on branch menu-speed (2026-10-08, `vita/notes/menu-speed.md` §4), not on main |

Build-volume layout (`$SHEPHERD_WORK/llvm/`): `pkgs/` + `sdk/arm64/` (LLVM), `inputs/` (snapshots of the DLLs, the
owner folder, the link objects and the runtime archives — owner/derived data, never shared), `aot/`, `out/`, `mac/`, `logs/`,
`proof-tree/` (the non-LLVM identity proof). The engine-AOT eboots are bound to the owner's DLL MVIDs: build volume only.
