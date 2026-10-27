#!/bin/zsh
# SHEPHERD-VITA LLVM: the LLVM-vs-non-LLVM speed proxy on the Mac (vita/notes/llvm-aot.md §5).
# The V6 vita-sim menu benchmark (vita/v6/sim.sh `SIM_REL=1 run`: Mono osx-arm64 8.0.29, coop, Vita profile, null renderer,
# strict pack, the pad script relative to the Main Menu's arrival, the SimStats window m+487..m+586 = 100 idle-menu frames with
# Play selected), copied here (sim.sh is not modified or run) with its own snapshots and roots, plus the Vita's execution model:
# full AOT + interpreter (--full-aot-interp) with the Vita's AOT set compiled by the fork's osx-arm64 Mono (build-mac-llvm.sh),
# once with Mono's own arm64 backend and once with LLVM. THE ARM64 RATIO IS A PROXY for armv7 (different ISA, register file,
# LLVM backend and Mono backend quality); it says how much of this codebase's AOT'd time LLVM removes, not the A9's number.
# usage:
#   macbench.sh aot <nollvm|llvm>                  AOT the set (12 Vita modules + Shepherd.Engine) -> $M/aot-<v>/<X>.dll.dylib
#   macbench.sh pub <config>                       configs: interp, jit, aot-nollvm, aot-llvm (the 12-module default VPK set),
#                                                  aot-nollvm-eng, aot-llvm-eng (+ Shepherd.Engine: the opt-in engine-AOT set),
#                                                  aot-nollvm-eng-inl, aot-llvm-eng-inl (Mac-only probe: the engine image may
#                                                  inline Unity methods; needs `aot nollvm-inl` / `aot llvm-inl`)
#   macbench.sh run <config> <name> [ENV=VAL ...]  one benchmark run -> $M/runs/<name>/ (engine.log, simstats*, console, wall)
#   macbench.sh rounds <n> <config...>             n interleaved rounds over the configs (r<k>-<config>), then the summary
#   macbench.sh summary <run...>                   window frame ms (mean/p50/p95), render/logic split, outcome
# Snapshots (2026-10-07, build volume only: derived game data never leaves it): $M/pub-base (the vita-sim publish),
# $M/Data (Managed = game + rewritten Unity + owner-local derived DLLs, + the pack's JSON), $M/snap/pack, $M/snap/persistent
# (a copy of the sandbox saves; never the real CotL save dir). The Mac stays silent: the null renderer, FMOD stubs, no audio.
setopt pipe_fail
zmodload zsh/datetime
HERE=${0:A:h}
export TMPDIR=${SHEPHERD_WORK:-$HOME/shepherd-work}/tmp DOTNET_CLI_TELEMETRY_OPTOUT=1
M=${SHEPHERD_LLVM_MAC:-${SHEPHERD_WORK:-$HOME/shepherd-work}/llvm/mac}
MS=${SHEPHERD_MAC_LLVM_OBJ:-${SHEPHERD_WORK:-$HOME/shepherd-work}/obj/mono-osx-llvm}/mono/mini/mono-sgen
LLVMBIN=${SHEPHERD_MAC_LLVM_OBJ:-${SHEPHERD_WORK:-$HOME/shepherd-work}/obj/mono-osx-llvm}/out/llvm-bin/
SET=(System.Private.CoreLib System.Linq System.Collections System.Collections.Concurrent System.Console System.Memory
     System.ObjectModel System.Runtime.InteropServices System.Threading Shepherd.Assets Shepherd.Hooks Microsoft.Bcl.AsyncInterfaces)
GC_PARAMS=major=marksweep,no-concurrent-sweep,nursery-size=4m
TIMING=(SHEPHERD_PAD_FILE="$HERE/pad-menu-dlc-rel.txt" SHEPHERD_MENU_PROBE=m+226,m+486,m+636 SHEPHERD_SIMSTATS=m+487:m+586:m+486 SHEPHERD_STOP_AT=m+646)
FRAMES=4000

aot() {
  local v=$1 W=$M/aot-$1; mkdir -p $W/tmp
  local base="full,interp,temp-path=$W/tmp"; [[ $v == llvm* ]] && base="$base,llvm,llvm-path=$LLVMBIN"
  # <v>-inl (Mac-only experiment, build volume only): Shepherd.Engine alone, WITHOUT the inline allowlist, i.e. allowed to inline
  # the rewritten Unity methods (Vector3/Color/Matrix4x4 operators...): measures what the AOT->interpreter transitions at the
  # 1,073 refused inline sites cost. Never a Vita configuration (it would compile Unity method bodies into the engine image).
  local -a mods=($SET Shepherd.Engine); [[ $v == *-inl ]] && mods=(Shepherd.Engine)
  local xs=$(shasum -a 256 $MS | cut -c1-16)
  for a in $mods; do
    local opts=$base envs=() mp=$M/pub-base
    [[ $a == System.Private.CoreLib ]] && opts="$opts,ntrampolines=8192,nrgctx-trampolines=8192,nimt-trampolines=2048,ngsharedvt-trampolines=4096,nftnptr-arg-trampolines=4096,nunbox-arbitrary-trampolines=1024"
    if [[ $a == Shepherd.Engine ]]; then
      envs=(SHEPHERD_AOT_INLINE_IMAGES="Shepherd.Engine,Shepherd.Assets,Shepherd.Hooks,System.*,Microsoft.*,mscorlib,netstandard"); mp=$M/pub-base:$M/Data/Managed
      [[ $v == *-inl ]] && envs=()
    fi
    local want="x=$xs opts=$opts in=$(shasum -a 256 $M/pub-base/$a.dll | cut -c1-16)"
    if [[ -f $W/$a.dll.dylib && "$(cat $W/$a.stamp 2>/dev/null)" == "$want" ]]; then echo "   mac-$v $a: up to date"; continue; fi
    local t0=$EPOCHREALTIME
    env $envs MONO_AGGRESSIVE_INLINELIMIT=256 MONO_THREADS_SUSPEND=coop MONO_PATH=$mp $MS --aot=$opts,outfile=$W/$a.dll.dylib $M/pub-base/$a.dll > $W/$a.log 2>&1 \
      || { tail -15 $W/$a.log; return 1; }
    rm -rf $W/$a.dll.dylib.dSYM
    echo $want > $W/$a.stamp
    printf '   mac-%s %s: %s (%.0f s, %s)\n' $v $a "$(grep -o 'Compiled: [0-9/]*' $W/$a.log | tail -1)" $(( EPOCHREALTIME - t0 )) "$(du -h $W/$a.dll.dylib | cut -f1)"
  done
}

pub() {
  local c=$1 P=$M/pub-$1
  rm -rf $P; cp -cR $M/pub-base $P
  case $c in
    interp|jit) ;;
    aot-nollvm|aot-llvm) for a in $SET; do cp $M/aot-${c#aot-}/$a.dll.dylib $P/; done ;;
    aot-nollvm-eng|aot-llvm-eng|aot-nollvm-eng-inl|aot-llvm-eng-inl) local v=${${${c#aot-}%-inl}%-eng}
      for a in $SET Shepherd.Engine; do cp $M/aot-$v/$a.dll.dylib $P/; done
      [[ $c == *-inl ]] && cp $M/aot-$v-inl/Shepherd.Engine.dll.dylib $P/
      # The engine's AOT image names the Unity/game assemblies it was compiled against; decoding an AOT method loads them by name
      # (aot-runtime.c decode_klass_ref -> load_image). On the Vita the host's preload hook answers those loads from
      # ux0:data/shepherd/managed; the plain Mac apphost has no hook, Mono falls back to the managed Resolving event before the
      # engine has installed its handler, and the run dies (NRE -> "infinite recursion while looking up resource", measured).
      # APP_PATHS (monovm.c: probed by the default ALC) plays the preload hook's part here.
      python3 -c "import json,sys; p=sys.argv[1]; j=json.load(open(p)); j['runtimeOptions'].setdefault('configProperties',{})['APP_PATHS']=sys.argv[2]; json.dump(j,open(p,'w'),indent=2)" \
        $P/Shepherd.Engine.runtimeconfig.json $M/Data/Managed ;;
    *) echo "unknown config $c"; return 2 ;;
  esac
  echo "pub-$c: $(ls $P/*.dylib(N) | grep -c '\.dll\.dylib$') AOT images"
}

run() {   # run <config> <name> [ENV=VAL ...]
  local c=$1 name=$2; shift 2
  local P=$M/pub-$c out=$M/runs/$name root=$M/roots/$name
  [[ -x $P/Shepherd.Engine ]] || { echo "no $P: macbench.sh pub $c"; return 1; }
  rm -rf $out $root; mkdir -p $out $root/runtime/logs
  cp -R $M/snap/persistent $root/runtime/persistent
  local mo=--full-aot-interp; [[ $c == interp ]] && mo=--interpreter; [[ $c == jit ]] && mo=
  local -a envs=(SHEPHERD_ROOT=$root SHEPHERD_GAME_DATA=$M/Data SHEPHERD_MANAGED=$M/Data/Managed
    SHEPHERD_PROFILE=vita SHEPHERD_RENDER=null SHEPHERD_PACK=$M/snap/pack SHEPHERD_PACK_STRICT=1 $TIMING
    MONO_ENV_OPTIONS=$mo MONO_THREADS_SUSPEND=coop MONO_GC_PARAMS=$GC_PARAMS "$@")
  print -l -- config=$c $envs > $out/env.txt
  local t0=$EPOCHREALTIME
  (cd $root && env $envs $P/Shepherd.Engine boot $FRAMES 50 > $out/console.txt 2>&1); local rc=$?
  printf 'wall_s=%.3f\nrc=%d\nload1=%s\n' $(( EPOCHREALTIME - t0 )) $rc "$(sysctl -n vm.loadavg)" > $out/wall.txt
  cp $root/runtime/logs/*(N) $out/ 2>/dev/null; rm -rf $root
  [[ -f $out/engine.log ]] || { echo "$name: rc=$rc, no engine.log"; tail -20 $out/console.txt; return 1; }
  echo "$name: rc=$rc $(python3 $HERE/macbench_summary.py --line $out)"
}

case $1 in
  aot) aot $2 ;;
  pub) pub $2 ;;
  run) shift; run "$@" ;;
  rounds) local n=$2; shift 2; local -a cs=("$@") names=()
          for ((k = 1; k <= n; k++)); do for c in $cs; do run $c ${RPFX:-r}$k-$c || return 1; names+=($M/runs/${RPFX:-r}$k-$c); done; done
          python3 $HERE/macbench_summary.py $names ;;
  summary) shift; python3 $HERE/macbench_summary.py "$@" ;;
  *) sed -n '2,20p' $0; exit 2 ;;
esac
