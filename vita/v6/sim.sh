#!/bin/zsh
# V6 Stage A (vita/notes/v6-mac-sim.md): the exact V6 boot, on the Mac, on the Mono INTERPRETER (osx-arm64 Mono 8.0.29 pack, coop).
#   SHEPHERD_PROFILE=vita SHEPHERD_RENDER=null (draws nothing, no SDL) + the menu pack, strict, from a Data folder that holds only
#   Managed/ (runtime/ManagedVita laid over the game's Managed) and the pack's data/ JSON; pad events from vita/v6/pad-menu-dlc.txt.
# usage: sim.sh [all]            publish + stage + 3 timing rounds + 1 census run + summary  (~15 min)
#        sim.sh publish          dotnet publish the engine on the Mono pack -> $PUB (offline: --source ~/.nuget/packages)
#        sim.sh stage            (re)build the sim folder $SIM (Data/ + root/)
#        sim.sh run <name> [ENV=VAL ...]   one run -> runtime/logs/v6/<name>/
#        sim.sh census <name>    one run with Mono's DllImport + GC log, the interp-census profiler and --stats (native census, GC pauses,
#                                interpreted methods + IR bytes, assembly-load stacks)
#        sim.sh prof <name>      one run with the call-timing profiler over the SimStats window (PROF_MONO= : the JIT) -> <run>/interp-prof.tsv
#        sim.sh threads <name>   per-thread CPU over the window (e.g. the game's Dispatch Thread polling on Thread.Yield), untimed-free
#        sim.sh clr <name>       the same boot on CoreCLR (Release, invariant), with crypto/zlib/System.Native call stacks
#        sim.sh mem <name>       holds 15 s at frame 1300 (after the forced GC) and takes vmmap --summary + footprint of the process
#        sim.sh summary <name...> python summary of the given runs
#        sim.sh cap <name> <MiB> [ENV=VAL ...]   one run with max-heap-size=<MiB>m (own root, parallel-safe) -> one PASS/OOM/CRASH line
#        sim.sh capgrid <name> <MiB...>   cap runs at several sizes, up to $SIM_JOBS (4) at a time -> runtime/logs/v6/<name>-capgrid.txt
#        sim.sh heapwalk <name> [ENV=VAL ...]  a census of the live sgen heap at frame 1300 (after the measurement's forced GCs:
#                                one more full GC with the heap-walk profiler armed; vita/v6/tools/heap-walk) -> <run>/heapwalk-{classes.tsv,big.txt,summary.txt}
#        sim.sh msl <name> [ENV=VAL ...]  MallocStackLogging run: holds at frame 1300 and attributes every live malloc block by Mono
#                                subsystem (vita/v6/tools/malloc_attr.py) -> <run>/malloc-attr.txt
#   SIM_ROOT=<dir> moves a run's root (logs, persistent/) off $SIM/root; SIM_PACK=<dir> boots another pack (default runtime/vitapack/menu);
#   SIM_PERSISTENT=<dir> starts the run from another sandbox-saves copy (default runtime/persistent; never the real CotL save dir).
#   SHEPHERD_TMPDIR=<dir> (kit #6): the temp dir (default ${SHEPHERD_WORK:-$HOME/shepherd-work}/tmp), so a track keeps its temp files in its own scratch dir.
# Derived game data (Data/Managed = game + rewritten Unity DLLs + the owner-local Shepherd.Engine.Derived.dll) lives only in $SIM on
# the build volume: never copy it into git, a VPK or anywhere shared. The real CotL save dir is never touched (SHEPHERD_ROOT=$SIM/root, persistent/ copied fresh per run).
setopt pipe_fail
zmodload zsh/datetime
V6="${0:A:h}"; REPO="${V6:h:h}"; R="$REPO/runtime"; ENG="$REPO/engine/Shepherd.Engine"
export TMPDIR=${SHEPHERD_TMPDIR:-${SHEPHERD_WORK:-$HOME/shepherd-work}/tmp} DOTNET_ROOT=$HOME/.dotnet PATH="$HOME/.dotnet:$PATH" DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
mkdir -p $TMPDIR
SIM=${SHEPHERD_SIM:-${SHEPHERD_WORK:-$HOME/shepherd-work}/v6/sim}
PUB=${SHEPHERD_V6_PUB:-${SHEPHERD_WORK:-$HOME/shepherd-work}/v6/pubmono}
ART=${SHEPHERD_V6_ART:-${SHEPHERD_WORK:-$HOME/shepherd-work}/v6/art-mono}
CLR=${SHEPHERD_V6_CLR:-${SHEPHERD_WORK:-$HOME/shepherd-work}/v6/clrrel}
GAME_DATA="${SHEPHERD_GAME_DATA}"
PACK="${SIM_PACK:-$R/vitapack/menu}"
FRAMES=${SIM_FRAMES:-1460}
# The Vita runtime's GC shape (vita/hello/host.c) minus the hello-sized 40 MiB cap: serial mark + serial sweep, 4 MiB nursery.
GC_PARAMS=${SIM_GC_PARAMS:-major=marksweep,no-concurrent-sweep,nursery-size=4m}
# The pad script, menu probes and measurement window. Absolute (the V6 default): the Main Menu scene lands at frame ~814 on the
# pre-stage-2 interpreter. SIM_REL=1 (stage 2): the same events relative to the Main Menu scene's arrival (Diag/MenuClock.cs),
# because faster frames land the menu later (the boot's loads wait on wall time); the run stops 646 frames after it (= 1460).
if [[ -n "$SIM_REL" ]]; then
  TIMING=(SHEPHERD_PAD_FILE="$V6/pad-menu-dlc-rel.txt" SHEPHERD_MENU_PROBE=m+226,m+486,m+636 SHEPHERD_SIMSTATS=m+487:m+586:m+486 SHEPHERD_STOP_AT=m+646)
  FRAMES=${SIM_FRAMES:-4000}
else
  TIMING=(SHEPHERD_PAD_FILE="$V6/pad-menu-dlc.txt" SHEPHERD_MENU_PROBE=1040,1300,1450 SHEPHERD_SIMSTATS=1301:1400:1300)
fi

publish() {
  # InvariantGlobalization=true: the Vita host passes System.Globalization.Invariant=true (an env var cannot override the
  # runtimeconfig's explicit false). PredefinedCulturesOnly=false: in invariant mode .NET 8 otherwise throws CultureNotFoundException
  # for any named culture, and Unify's UnifyManager..ctor calls CultureInfo.GetCultureInfo("en-us") (the boot then dies in
  # InputSource.Finalize, see the notes); the Vita host must pass the same property. Command-line only: the csproj and the
  # Mac default stay as they are.
  dotnet publish "$ENG/Shepherd.Engine.csproj" -c Release -r osx-arm64 --self-contained -p:UseMonoRuntime=true -p:InvariantGlobalization=true -p:PredefinedCulturesOnly=false \
    -p:DisableTransitiveFrameworkReferenceDownloads=true --source ~/.nuget/packages --artifacts-path "$ART" -o "$PUB" 2>&1 | grep -E " error |Shepherd.Engine ->" || return 1
  grep -q '"System.Globalization.Invariant": true' "$PUB/Shepherd.Engine.runtimeconfig.json" || { echo "publish: runtimeconfig is not invariant"; return 1; }
  grep -q '"System.Globalization.PredefinedCulturesOnly": false' "$PUB/Shepherd.Engine.runtimeconfig.json" || { echo "publish: runtimeconfig lacks PredefinedCulturesOnly=false"; return 1; }
}

stage() {
  [[ -f "$R/ManagedVita/icalls.json" ]] || { echo "stage: runtime/ManagedVita missing (vita/v4/check.sh --rewrite)"; return 1; }
  [[ -f "$PACK/shepherd.pack" ]] || { echo "stage: $PACK/shepherd.pack missing (vita/v4/check-pack.sh)"; return 1; }
  rm -rf "$SIM/Data" "$SIM/root" "$SIM"/root-*(N); mkdir -p "$SIM/Data/Managed" "$SIM/root/runtime/logs"
  cp "$GAME_DATA"/Managed/*.dll "$SIM/Data/Managed/"            # the game's Managed folder ...
  cp "$R"/ManagedVita/* "$SIM/Data/Managed/"                    # ... with the Vita set (rewritten Unity/game DLLs + icalls.json) laid over it
  zsh "$REPO/tools/IcallBake/bake.sh" "$SIM/Data/Managed" > /dev/null || { echo "stage: tools/IcallBake failed"; return 1; }   # kit #4 B-1: icalls.bin (the engine's flat copy of icalls.json)
  # ... and the owner-local derived engine code (Shepherd.Engine.Derived: the Febucci replay, the derived render code), as on the Vita (ux0:data/shepherd/managed)
  local der; der=$(zsh "$REPO/vita/shepherd/build-derived.sh") || { echo "stage: build-derived.sh failed"; return 1; }
  if [[ -f "$der" ]]; then cp "$der" "$SIM/Data/Managed/"; else echo "stage: no engine/Derived source here: the game runs its own Febucci scan"; fi
  cp -R "$PACK/data/." "$SIM/Data/"                             # RuntimeInitializeOnLoads.json + StreamingAssets/aa/{settings,catalog}.json
  echo "stage: $SIM/Data: $(ls "$SIM/Data/Managed" | wc -l | tr -d ' ') files in Managed/ ($(du -sh "$SIM/Data/Managed" | cut -f1)), $(cd "$SIM/Data" && find . -type f -not -path './Managed/*' | tr '\n' ' ')"
}

run() {   # run <name> [ENV=VAL ...]
  local name=$1; shift
  local out="$R/logs/v6/$name"; rm -rf "$out"; mkdir -p "$out"
  local root=${SIM_ROOT:-$SIM/root}
  rm -rf "$root/runtime/persistent" "$root/runtime/logs"; mkdir -p "$root/runtime/logs"
  cp -R "${SIM_PERSISTENT:-$R/persistent}" "$root/runtime/persistent"   # a fresh copy of the sandbox saves for every run (SIM_PERSISTENT: another sandbox copy)
  local -a envs=(
    SHEPHERD_ROOT="$root" SHEPHERD_GAME_DATA="$SIM/Data" SHEPHERD_MANAGED="$SIM/Data/Managed"
    SHEPHERD_PROFILE=vita SHEPHERD_RENDER=null SHEPHERD_PACK="$PACK" SHEPHERD_PACK_STRICT=1
    $TIMING
    MONO_ENV_OPTIONS=--interpreter MONO_THREADS_SUSPEND=coop MONO_GC_PARAMS="$GC_PARAMS"
    "$@")
  # SIM_YIELD=vita: sched_yield (= Mono's Thread.Yield on the Mac) gets the Vita's managed-yield back-off (tools/yield-interpose)
  if [[ "$SIM_YIELD" == vita ]]; then
    [[ -f "$V6/tools/yield-interpose/out/libyield-vita.dylib" ]] || "$V6/tools/yield-interpose/build.sh" >/dev/null || return 1
    envs+=(DYLD_INSERT_LIBRARIES="$V6/tools/yield-interpose/out/libyield-vita.dylib")
  fi
  print -l -- $envs > "$out/env.txt"
  local t0=$EPOCHREALTIME
  local -a exe; if (( ${#PUBEXE} )); then exe=($PUBEXE); else exe=("$PUB/Shepherd.Engine"); fi
  (cd "$root" && env $envs $exe boot $FRAMES 50 > "$out/console.txt" 2>&1); local rc=$?
  local t1=$EPOCHREALTIME
  printf 'launch_unix_s=%s\nexit_unix_s=%s\nwall_s=%.3f\nrc=%d\n' $t0 $t1 $(( t1 - t0 )) $rc > "$out/wall.txt"
  cp "$root/runtime/logs/"*(N) "$out/" 2>/dev/null
  [[ -n "$SIM_ROOT" ]] && rm -rf "$root"
  [[ -f "$out/engine.log" ]] || { echo "$name: rc=$rc, no engine.log"; tail -20 "$out/console.txt"; return 1; }
  echo "$name: rc=$rc wall=$(printf %.1f $(( t1 - t0 )))s  $(tail -1 "$out/engine.log" | sed 's/^\[ *[0-9]*\] *//')"
  grep -E "\[menu\] frame|\[menu\]   buttons" "$out/engine.log" | sed 's/^\[ *[0-9]*\] *\[menu\] */    /' | cut -c1-200
}

census() {   # census <name>: + Mono's DllImport/GC log (file, with wall-clock seconds), the interpreter census profiler, --stats,
             # and the stack behind every assembly load. Its timings are not the headline numbers (logging + profiler).
  local name=$1; shift
  [[ -f "$V6/tools/interp-census/out/libmono-profiler-shepherd.dylib" ]] || "$V6/tools/interp-census/build.sh" >/dev/null || return 1
  local root=${SIM_ROOT:-$SIM/root}
  run $name MONO_ENV_OPTIONS="--interpreter --stats --profile=shepherd:$root/runtime/logs/interp-methods.tsv" \
    DYLD_LIBRARY_PATH="$V6/tools/interp-census/out" MONO_LOG_LEVEL=debug MONO_LOG_MASK=dll,gc MONO_LOG_HEADER=1 MONO_LOG_DEST="$root/runtime/logs/mono.log" \
    SHEPHERD_ASMLOAD_STACKS=1 "$@"
}

prof() {     # prof <name> [ENV=VAL ...]: the call-timing profiler (vita/v6/tools/interp-prof) over the SimStats window: every managed call
             # of the main thread between frames 1301 and 1400, exclusive/inclusive ms per method and image -> <run>/interp-prof.tsv.
             # PROF_MONO= (empty) profiles the Mono JIT instead of the interpreter (the interpreter-vs-JIT split per image). Its timings
             # are inflated by the instrumentation (two callbacks per call): use it for the split, sim.sh run for the frame time.
  local name=$1; shift
  [[ -f "$V6/tools/interp-prof/out/libmono-profiler-shepherdprof.dylib" ]] || "$V6/tools/interp-prof/build.sh" >/dev/null || return 1
  local root=${SIM_ROOT:-$SIM/root}
  run $name MONO_ENV_OPTIONS="${PROF_MONO---interpreter} --profile=shepherdprof:$root/runtime/logs/interp-prof.tsv" DYLD_LIBRARY_PATH="$V6/tools/interp-prof/out" "$@"
}

heapwalk() { # heapwalk <name> [ENV=VAL ...]: the heap-census profiler module, armed by SimStats after the live-heap measurement
  local name=$1; shift
  [[ -f "$V6/tools/heap-walk/out/libmono-profiler-shepherdheap.dylib" ]] || "$V6/tools/heap-walk/build.sh" >/dev/null || return 1
  local root=${SIM_ROOT:-$SIM/root}
  run $name MONO_ENV_OPTIONS="--interpreter --profile=shepherdheap:$root/runtime/logs/heapwalk" DYLD_LIBRARY_PATH="$V6/tools/heap-walk/out" \
    SHEPHERD_HEAPWALK="$root/runtime/logs/heapwalk.arm" "$@"
}

threads() {  # threads <name> [ENV=VAL ...]: CPU time of every thread over the SimStats window (the interp-prof module with no call
             # instrumentation: SHEPHERD_PROF_CALLS=0, so the run is timed as usual) -> the "## threads" section of <run>/interp-prof.tsv
  local name=$1; shift
  prof $name SHEPHERD_PROF_CALLS=0 SHEPHERD_PROF_THREADS=1 "$@" || return 1
  grep -E "^## threads|^t	" "$R/logs/v6/$name/interp-prof.tsv" | sed 's/^t	/  /'
}

clr() {      # clr <name>: the same boot on CoreCLR 8.0.29 (Release, invariant like the Mono build: $CLR), for comparison and for the
             # per-entry-point census with call stacks (CoreCLR binds per entry point): crypto / zlib / System.Native callers
  local name=$1; shift
  local -a PUBEXE=(dotnet "$CLR/Shepherd.Engine.dll")
  run $name MONO_ENV_OPTIONS= MONO_THREADS_SUSPEND= MONO_GC_PARAMS= SHEPHERD_NATIVE_CENSUS_STACKS=Cryptography,Compression,libSystem.Native "$@"
}
mem() {      # mem <name>: a timing-free run that holds at frame 1300 (after the forced GC) for a vmmap snapshot of the whole process
  local name=$1; shift
  hold $name 15000 snap_vmmap "$@"
}
msl() {      # msl <name>: the same hold with MallocStackLogging; every live malloc block at frame 1300 attributed by Mono subsystem
  local name=$1; shift
  hold $name 600000 snap_msl MallocStackLogging=1 "$@"
}
snap_vmmap() {   # snap_vmmap <pid> <out>
  vmmap --summary $1 > "$2/vmmap-summary.txt" 2>&1; footprint $1 > "$2/footprint.txt" 2>&1
  grep -E 'Physical footprint:' "$2/vmmap-summary.txt" | head -1
}
snap_msl() {     # snap_msl <pid> <out>: vmmap + malloc_history -allBySize (live blocks, full stacks) -> malloc_attr.py
  snap_vmmap $1 $2 >/dev/null
  malloc_history $1 -allBySize > "$2/malloc-allbysize.txt" 2>"$2/malloc-history.err"
  python3 "$V6/tools/malloc_attr.py" "$2/malloc-allbysize.txt" > "$2/malloc-attr.txt"; gzip -f "$2/malloc-allbysize.txt"
  head -3 "$2/malloc-attr.txt" | tail -2
}
hold() {     # hold <name> <ms> <snapshot fn> [ENV=VAL ...]: run to frame 1300 (forced GC), hold, call <fn> <pid> <out>, let the run finish
  local name=$1 ms=$2 fn=$3; shift 3
  local out="$R/logs/v6/$name"; rm -rf "$out"; mkdir -p "$out"
  local root=${SIM_ROOT:-$SIM/root}
  rm -rf "$root/runtime/persistent" "$root/runtime/logs"; mkdir -p "$root/runtime/logs"; cp -R "$R/persistent" "$root/runtime/persistent"
  local -a envs=(
    SHEPHERD_ROOT="$root" SHEPHERD_GAME_DATA="$SIM/Data" SHEPHERD_MANAGED="$SIM/Data/Managed"
    SHEPHERD_PROFILE=vita SHEPHERD_RENDER=null SHEPHERD_PACK="$PACK" SHEPHERD_PACK_STRICT=1
    $TIMING SHEPHERD_SIM_HOLD_MS=$ms
    MONO_ENV_OPTIONS=--interpreter MONO_THREADS_SUSPEND=coop MONO_GC_PARAMS="$GC_PARAMS" "$@")
  print -l -- $envs > "$out/env.txt"
  (cd "$root" && exec env $envs "$PUB/Shepherd.Engine" boot $FRAMES 50 > "$out/console.txt" 2>&1) &
  local pid=$! n=0
  while [[ ! -f "$root/runtime/logs/sim-hold" ]] && kill -0 $pid 2>/dev/null && (( n < 2400 )); do sleep 0.25; n=$((n+1)); done
  if [[ -f "$root/runtime/logs/sim-hold" ]]; then
    local f=$(cat "$root/runtime/logs/sim-hold" 2>/dev/null); local res=$($fn $pid "$out")
    echo "$name: snapshot at frame $f: $res"
    rm -f "$root/runtime/logs/sim-hold.wait"; touch "$root/runtime/logs/sim-hold.done"
  else echo "$name: no hold marker (process ended?)"; fi
  wait $pid
  cp "$root/runtime/logs/"*(N) "$out/" 2>/dev/null
  [[ -n "$SIM_ROOT" ]] && rm -rf "$root"
}

# ---- heap-cap probes (v6-vita-build.md §4): the lowest max-heap-size the boot survives cleanly to DLC ----
CAP_ERR=${CAP_ERR:-7}; CAP_EXC=${CAP_EXC:-3}   # polish round 1: 7/2 -> 7/3 (the SkeletonAnimationLODGlobalManager NRE: 3 frames, vita/notes/polish-round1.md §2)
cap_verdict() {  # cap_verdict <out> <MiB>: PASS = rc 0, 0 OutOfMemoryException, DLC selected at 1450, errors/exceptions = CAP_ERR/CAP_EXC
  local out=$1 mib=$2 rc=$(grep -o 'rc=[0-9]*' "$1/wall.txt" 2>/dev/null | cut -d= -f2)
  local done=$(grep -E "\] +done: " "$out/engine.log" 2>/dev/null | tail -1 | sed -E 's/.*done: ([0-9]+) errors, ([0-9]+) exceptions.*/\1\/\2/')
  local oom=$(cat "$out/engine.log" "$out/console.txt" 2>/dev/null | grep -c "OutOfMemory")
  local dlc=$(grep -c "frame 1450: selected='DLC'" "$out/engine.log" 2>/dev/null)
  local last=$(grep -oE "^\[ *[0-9]+\]" "$out/engine.log" 2>/dev/null | tail -1 | tr -d '[] ')
  local v=PASS
  if [[ "$rc" != 0 || -z "$done" ]]; then v=CRASH; elif (( oom > 0 )); then v=OOM; elif [[ "$dlc" != 1 || "$done" != "$CAP_ERR/$CAP_EXC" ]]; then v=FAIL; fi
  printf '%-5s cap %4dm  rc=%s  errors/exc=%s  OOM lines=%s  DLC@1450=%s  last frame=%s\n' $v $mib "${rc:-?}" "${done:-?}" $oom $dlc "${last:-?}"
}
cap() {      # cap <name> <MiB> [ENV=VAL ...]
  local name=$1 mib=$2; shift 2
  SIM_ROOT="$SIM/root-$name" run $name MONO_GC_PARAMS="$GC_PARAMS,max-heap-size=${mib}m" "$@" > /dev/null 2>&1
  cap_verdict "$R/logs/v6/$name" $mib
}
capgrid() {  # capgrid <name> <MiB...>: parallel cap runs (SIM_JOBS at a time; wall-clock pacing shifts a little under load: confirm the edge alone)
  local name=$1; shift; local -a caps=("$@"); local jobs=${SIM_JOBS:-4} i=0
  local tab="$R/logs/v6/$name-capgrid.txt"; : > "$tab"
  for c in $caps; do
    (cap $name-cap$c $c ${=CAP_ENV} >> "$tab") &
    i=$((i+1)); (( i % jobs == 0 )) && wait
  done; wait
  sort -k3 -n "$tab" | tee "$tab.sorted"; mv "$tab.sorted" "$tab"
}

publish_clr() {
  dotnet build "$ENG/Shepherd.Engine.csproj" -c Release -p:InvariantGlobalization=true -p:PredefinedCulturesOnly=false -p:DisableTransitiveFrameworkReferenceDownloads=true \
    --artifacts-path "$ART-clr" -o "$CLR" 2>&1 | grep -E " error |Shepherd.Engine ->" || return 1
}

summary() { python3 "$V6/tools/sim_summary.py" "$@"; }

case ${1:-all} in
  publish) publish && publish_clr ;;
  stage) stage ;;
  run) shift; run "$@" ;;
  census) shift; census "$@" ;;
  prof) shift; prof "$@" ;;
  threads) shift; threads "$@" ;;
  heapwalk) shift; heapwalk "$@" ;;
  clr) shift; clr "$@" ;;
  mem) shift; mem "$@" ;;
  msl) shift; msl "$@" ;;
  cap) shift; cap "$@" ;;
  capgrid) shift; capgrid "$@" ;;
  verdict) shift; cap_verdict "$@" ;;
  summary) shift; summary "$@" ;;
  all)
    publish && publish_clr && stage
    for r in r1 r2 r3; do run sim-$r; done
    census sim-census
    summary sim-r1 sim-r2 sim-r3 sim-census ;;
  *) echo "unknown mode $1"; return 2 ;;
esac
