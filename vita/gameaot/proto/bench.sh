#!/bin/zsh
# game-aot PROTOTYPE (vita/gameaot/proto, report ${SHEPHERD_WORK:-$HOME/shepherd-work}/gameaot/prototype.md): owner-local AOT of the rewritten
# Unity modules and of the game / third-party assemblies, measured on the Mac proxy of the Vita (vita/llvm/macbench.sh's method:
# Mono osx-arm64 8.0.29, coop, the Vita GC shape, Vita profile, null renderer, FMOD stubs, SHEPHERD_AUDIO=off, --full-aot-interp
# with osx-arm64 AOT images of the same module sets the Vita build would hold, compiled by the fork's Mac compiler + the gameaot
# compiler patches (build-compilers.sh)). Isolated: engine from this worktree, private snapshots under $P/snap, every output under
# $P = ${SHEPHERD_WORK:-$HOME/shepherd-work}/gameaot/proto (owner-local compiled game / Unity code: never in git, never in a VPK).
#
#   bench.sh build                       publish the engine (this worktree) -> $P/builds/base/pub
#   bench.sh snap                        private snapshots (APFS clones of the hot study's: Data/Managed, pack, persistent, GameData)
#   bench.sh closure                     value-type closure profile of the partial owner modules (the stock 8.0.29 runtime has no fix A)
#   bench.sh profiles                    $P/prof/{hot,part}.aotprofile: the hot study's PH95 union (owner modules) [+ closure]
#   bench.sh aot <nollvm|llvm> <full|part> <set>    AOT a module set -> $P/mac/img/<backend>-<mode>/<asm>.dll.dylib (stamped)
#   bench.sh cfg <config>                $P/mac/cfg-<config>: pub + engine-set images + Managed (clone) + the owner images
#   bench.sh run <config> <idle|tour|intro> <name> [ENV=VAL ...]     one run -> $P/runs/<name>
#   bench.sh rounds <n> "<configs>" "<scenarios>" <prefix>             interleaved: for k, scenario, config
# configs: A  engine-AOT baseline (12 Vita modules + Shepherd.Engine with the inline allowlist) = macbench aot-nollvm-eng
#          B  A + the 27 rewritten Unity modules, whole, each its own module
#          C  B + profile-guided partial AOT of the 60 game / third-party modules (incl. Unity packages, Shepherd.Engine.Derived)
#          P  A + profile-guided partial AOT of all 87 owner modules (rewritten ones partial too)
#          D  C with LLVM for every whole image (engine set + Unity); the partial game modules stay on Mono's backend (defect E)
#          DP P with LLVM for the engine set; the partial owner modules on Mono's backend
#          DA A with LLVM (the LLVM study's aot-llvm-eng)
#          E  A + all 87 owner modules whole (Mac-only ceiling: does not fit the Vita, sizes.md)
#          I  interpreter only (the default VPK's model, for reference)
#          HI the hop-fix engine interpreted (its hot-method profiles for hopmodel.py)
#          H, HP, HC (HD, HDP): A, P, C (D, DP) with the menu-speed branch's engine (2d7b80b: UI cache + hop fix; `git archive`
#             into $P/hopfix/src, published to $P/builds/hopfix/pub with builds/base's Shepherd.Hooks/Assets, same source): does
#             owner AOT still pay once the engine-side hop fix lands?
# CoreLib's trampoline pools (CORELIB_TRAMPS): the Vita build's 8192 specific trampolines run out as soon as game modules are AOT'd
# ("Ran out of trampolines of type 0", measured in C, P and E); every config here uses the same bigger pools (pool size does not
# change speed), the Vita owner-local variant needs its own CoreLib image with pools sized from trampcount (vita.sh)
# Silent: the Vita profile has no audio library; the output-muted state is checked before and after every run.
setopt pipe_fail
zmodload zsh/datetime
G=${SHEPHERD_WORK:-$HOME/shepherd-work}/gameaot; P=$G/proto; D=${0:A:h}; WT=${D:h:h:h}
export TMPDIR=${SHEPHERD_WORK:-$HOME/shepherd-work}/tmp DOTNET_ROOT=$HOME/.dotnet PATH="$HOME/.dotnet:$PATH" DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
mkdir -p $TMPDIR $P/runs $P/roots $P/logs
GC_PARAMS=major=marksweep,no-concurrent-sweep,nursery-size=4m
PUB=$P/builds/base/pub
MAN=$P/snap/Data/Managed
SET=(System.Private.CoreLib System.Linq System.Collections System.Collections.Concurrent System.Console System.Memory
     System.ObjectModel System.Runtime.InteropServices System.Threading Shepherd.Assets Shepherd.Hooks Microsoft.Bcl.AsyncInterfaces)
UNITY=(${(f)"$(<$D/modules-rewritten.txt)"})
GAME=(${(f)"$(<$D/modules-game.txt)"})
OWNER=($UNITY $GAME)
ENG_INLINE="Shepherd.Engine,Shepherd.Assets,Shepherd.Hooks,System.*,Microsoft.*,mscorlib,netstandard"
OWN_INLINE="${(j:,:)OWNER},System.*,Microsoft.*,mscorlib,netstandard"   # owner images: owner + BCL, no Shepherd.* (mechanics.md §5)

msgen() { [[ $1 == llvm ]] && print $P/obj/mono-osx-llvm/mono/mini/mono-sgen || print $P/obj/mono-osx/mono/mini/mono-sgen; }

build() {
  local O=$P/builds/base; mkdir -p $O
  print -r -- "src=$WT commit=$(git -C $WT rev-parse HEAD) dirty=$(git -C $WT status --porcelain | grep -vc '^??')" > $O/source.txt
  dotnet publish $WT/engine/Shepherd.Engine/Shepherd.Engine.csproj -c Release -r osx-arm64 --self-contained -p:UseMonoRuntime=true \
    -p:InvariantGlobalization=true -p:PredefinedCulturesOnly=false -p:DisableTransitiveFrameworkReferenceDownloads=true \
    --source ~/.nuget/packages --disable-build-servers -p:UseSharedCompilation=false --artifacts-path $O/art -o $O/pub 2>&1 \
    | grep -E " error |Shepherd.Engine -> " || return 1
  grep -q '"System.Globalization.Invariant": true' $O/pub/Shepherd.Engine.runtimeconfig.json || { echo "publish: runtimeconfig not invariant"; return 1; }
}

snap() {
  local S=$P/snap H=$G/hot/snap; mkdir -p $S
  for d in Data pack persistent; do [[ -d $S/$d ]] || cp -cR $H/$d $S/$d; done
  cp $H/persistent.md5 $S/
  if [[ ! -d $S/GameData ]]; then
    mkdir -p $S/GameData
    for l in $H/GameData/*(@); do ln -s "$(readlink $l)" $S/GameData/${l:t}; done
    rm $S/GameData/Managed; ln -s $MAN $S/GameData/Managed
  fi
  (cd $P && find snap/persistent -type f | sort | xargs md5 -r | sort) > $S/persistent.now.md5; diff -q <(sort $S/persistent.md5) $S/persistent.now.md5 && echo "persistent snapshot: md5-identical to the hot study's"
  ls -la $S $S/GameData | head -30
}

closure() {   # every instance method of every struct of the partial owner modules (mechanics.md defect A, without a runtime change)
  local L=$P/tools/loader; mkdir -p $L
  [[ -x $L/pub/Loader ]] || dotnet publish $WT/vita/gameaot/mechanics/exp/Loader/Loader.csproj -c Release -r osx-arm64 --self-contained \
      -p:UseMonoRuntime=true -p:UseAppHost=true -p:InvariantGlobalization=true -p:DisableTransitiveFrameworkReferenceDownloads=true --source ~/.nuget/packages --disable-build-servers \
      -p:UseSharedCompilation=false --artifacts-path $L/art -o $L/pub 2>&1 | grep -E " error |Loader -> " || return 1
  local PD=$G/aotprof; [[ -f $PD/libmono-profiler-gameaotprof.dylib ]] || GAMEAOT_OUT=$G zsh $WT/vita/gameaot/mechanics/aotprof/build.sh || return 1
  mkdir -p $P/prof; local -a paths=(); for a in $OWNER; do paths+=($MAN/$a.dll); done
  DYLD_LIBRARY_PATH=$PD MONO_ENV_OPTIONS="--interpreter --profile=gameaotprof:out=$P/prof/closure,images=NONE.NONE,min=1000000000,closure=${(j:;:)OWNER}" \
    $L/pub/Loader $paths > $P/prof/closure.log 2>&1; local rc=$?
  tail -3 $P/prof/closure.log; return $rc
}

profiles() {   # hot = the hot study's PH95 union (all six windows: idle, tour, intro boot/menu/quote/gameplay), owner modules only;
               # ginst = the generic CLASS instances those windows call >= 1/frame (gameaotprof runs gi-{idle,tour,intro}, config I,
               # --interp=-inline; the hot study's files hold no instances); part = hot + ginst + closure (Mac, stock runtime);
               # nocl = hot + ginst (a runtime with mechanics' fix A needs no closure)
  local U=$G/profiles/union/PH95/aotprofile PS=$D/profsel.py; local -a ins=()
  for a in $OWNER; do [[ -f $U/$a.aotprofile ]] && ins+=($U/$a.aotprofile); done
  python3 $PS merge $P/prof/hot.aotprofile $ins || return 1
  python3 $PS ginst $P/prof/ginst.aotprofile $P/prof/gi-{idle,tour,intro}.aotprofile || return 1
  python3 $PS merge $P/prof/nocl.aotprofile $P/prof/hot.aotprofile $P/prof/ginst.aotprofile || return 1
  python3 $PS merge $P/prof/part.aotprofile $P/prof/hot.aotprofile $P/prof/ginst.aotprofile $P/prof/closure.aotprofile || return 1
}

aot() {   # aot <nollvm|llvm> <full|part> <eng|unity|game|owner|LIST...>
  local be=$1 mode=$2; shift 2; local W=$P/mac/img/$be-$mode MS=$(msgen $be) PUB=$PUB
  [[ $1 == engh ]] && { W=$P/mac/img/$be-$mode-hopfix PUB=$P/builds/hopfix/pub; }
  mkdir -p $W/tmp $W/logs
  local -a mods
  case $1 in eng) mods=($SET Shepherd.Engine) ;; engh) mods=(Shepherd.Engine) ;; unity) mods=($UNITY) ;; game) mods=($GAME) ;; owner) mods=($OWNER) ;; *) mods=($@) ;; esac
  local base="full,interp,temp-path=$W/tmp" prof=""; [[ $be == llvm ]] && base="$base,llvm,llvm-path=$P/obj/mono-osx-llvm/out/llvm-bin/"
  [[ $mode == part ]] && { prof=",profile=$P/prof/part.aotprofile,profile-only"; [[ -f $P/prof/part.aotprofile ]] || { echo "no profile: bench.sh profiles"; return 1; }; }
  local xs=$(shasum -a 256 $MS | cut -c1-16) ps=$([[ -n $prof ]] && shasum -a 256 $P/prof/part.aotprofile | cut -c1-16)
  local a src; for a in $mods; do
    local opts=$base envs=(MONO_AGGRESSIVE_INLINELIMIT=256 MONO_THREADS_SUSPEND=coop) mp=$PUB:$MAN; src=""
    if (( ${SET[(Ie)$a]} )) || [[ $a == Shepherd.Engine ]]; then
      [[ $mode == full ]] || { echo "$a: engine-set modules are always whole"; return 1; }
      src=$PUB/$a.dll
      [[ $a == System.Private.CoreLib ]] && opts="$opts,${CORELIB_TRAMPS:-ntrampolines=65536,nrgctx-trampolines=16384,nimt-trampolines=4096,ngsharedvt-trampolines=8192,nftnptr-arg-trampolines=16384,nunbox-arbitrary-trampolines=4096}"
      [[ $a == Shepherd.Engine ]] && envs+=(SHEPHERD_AOT_INLINE_IMAGES=$ENG_INLINE)
      [[ $a != Shepherd.Engine ]] && mp=$PUB
    else
      src=$MAN/$a.dll; envs+=(SHEPHERD_AOT_INLINE_IMAGES=$OWN_INLINE)
      [[ $mode == part ]] && { opts="$opts$prof"; envs+=(SHEPHERD_AOT_PROFILE_STRICT=1); }
    fi
    local want="x=$xs opts=$opts p=$ps in=$(shasum -a 256 $src | cut -c1-16) env=${(j: :)envs}"
    if [[ -f $W/$a.dll.dylib && "$(cat $W/$a.stamp 2>/dev/null)" == "$want" ]]; then continue; fi
    local t0=$EPOCHREALTIME
    env $envs MONO_PATH=$mp nice -n 5 $MS --aot=$opts,outfile=$W/$a.dll.dylib $src > $W/logs/$a.log 2>&1 \
      || { echo "AOT FAILED $be-$mode $a"; tail -8 $W/logs/$a.log; rm -f $W/$a.dll.dylib; continue; }
    rm -rf $W/$a.dll.dylib.dSYM; echo $want > $W/$a.stamp
    printf '%s\t%s\t%s\t%.1f\t%s\n' $be-$mode $a "$(grep -o 'Compiled: [0-9/]*' $W/logs/$a.log | tail -1 | cut -d' ' -f2)" \
      $(( EPOCHREALTIME - t0 )) "$(stat -f %z $W/$a.dll.dylib)" | tee -a $W/compile.tsv
  done
}

cfg() {   # cfg <A|B|C|P|D|DP|E|I>
  local c=$1 C=$P/mac/cfg-$1 be=nollvm PUB=$PUB hop=; [[ $c == D* || $c == HD* ]] && be=llvm
  [[ $c == H* ]] && { PUB=$P/builds/hopfix/pub hop=1; }
  local -a own_full own_part
  case $c in
    A|I|DA) ;;
    B) own_full=($UNITY) ;;
    C|D) own_full=($UNITY); own_part=($GAME) ;;
    P|DP) own_part=($OWNER) ;;
    E) own_full=($OWNER) ;;
    H|HD|HI) ;;
    HP|HDP) own_part=($OWNER) ;;
    HC) own_full=($UNITY); own_part=($GAME) ;;
    *) echo "unknown config $c"; return 2 ;;
  esac
  rm -rf $C; mkdir -p $C; cp -cR $PUB $C/pub; cp -cR $MAN $C/Managed
  if [[ $c != I && $c != HI ]]; then
    for a in $SET Shepherd.Engine; do cp -c $P/mac/img/$be-full/$a.dll.dylib $C/pub/ || return 1; done
    [[ -n $hop ]] && { cp -c $P/mac/img/$be-full-hopfix/Shepherd.Engine.dll.dylib $C/pub/ || return 1; }
    python3 -c "import json,sys; p=sys.argv[1]; j=json.load(open(p)); j['runtimeOptions'].setdefault('configProperties',{})['APP_PATHS']=sys.argv[2]; json.dump(j,open(p,'w'),indent=2)" \
      $C/pub/Shepherd.Engine.runtimeconfig.json $C/Managed
  fi
  local a n=0 miss=0
  for a in $own_full; do [[ -f $P/mac/img/$be-full/$a.dll.dylib ]] && { cp -c $P/mac/img/$be-full/$a.dll.dylib $C/Managed/; n=$((n+1)); } || { echo "missing $be-full $a"; miss=$((miss+1)); }; done
  # partial modules always from the Mono backend: LLVM + profile-only images abort at their first GC poll (defect E,
  # llvmcheck/coldwrap.py: the module's GC-poll cold wrapper lies below its lowest method, outside the runtime's LLVM code range,
  # in 87/87 partial LLVM images and 0/41 whole ones); LLVM_PART=1 takes the LLVM partial images anyway (the failing form)
  local pbe=nollvm; [[ -n ${LLVM_PART:-} ]] && pbe=$be
  for a in $own_part; do [[ -f $P/mac/img/$pbe-part/$a.dll.dylib ]] && { cp -c $P/mac/img/$pbe-part/$a.dll.dylib $C/Managed/; n=$((n+1)); } || { echo "missing $pbe-part $a"; miss=$((miss+1)); }; done
  # the intro's game Data: the same symlinks, Managed -> this config's Managed
  mkdir -p $C/GameData; for l in $P/snap/GameData/*(@); do ln -s "$(readlink $l)" $C/GameData/${l:t}; done
  rm $C/GameData/Managed; ln -s $C/Managed $C/GameData/Managed
  print -l "config=$c backend=$be partial-backend=$pbe" "full=${(j: :)own_full}" "part=${(j: :)own_part}" "owner_images=$n missing=$miss" > $C/cfg.txt
  echo "cfg-$c: $(print -l $C/pub/*.dll.dylib(N) | grep -c .) engine-set images, $n owner images ($miss missing)"
}

run() {   # run <config> <scenario> <name> [ENV=VAL ...]
  local c=$1 sc=$2 name=$3; shift 3
  local C=$P/mac/cfg-$c data frames=4000 pad; local -a timing pack
  [[ -x $C/pub/Shepherd.Engine ]] || { echo "no $C: bench.sh cfg $c"; return 1; }
  data=$C/GameData
  case $sc in
    idle)  pad=$WT/vita/v6/pad-menu-dlc-rel.txt
           timing=(SHEPHERD_MENU_PROBE=m+226,m+486,m+636 SHEPHERD_SIMSTATS=m+487:m+586:m+486 SHEPHERD_STOP_AT=m+646) ;;
    tour)  pad=$WT/vita/gameaot/pads/pad-tour-rel.txt
           timing=(SHEPHERD_MENU_PROBE=m+226,m+486,m+636 SHEPHERD_SIMSTATS=m+494:m+1793:m+486 SHEPHERD_STOP_AT=m+1800) ;;
    intro) pad=$WT/vita/gameaot/pads/pad-main-joy.txt frames=${INTRO_FRAMES:-5000}
           timing=(SHEPHERD_MENU_PROBE=1040,1300,1450 SHEPHERD_SIMSTATS=1:$frames) ;;
    *) echo "scenario: idle|tour|intro"; return 2 ;;
  esac
  [[ $sc != intro ]] && pack=(SHEPHERD_PACK=$P/snap/pack SHEPHERD_PACK_STRICT=1)
  local out=$P/runs/$name root=$P/roots/$name; rm -rf $out $root; mkdir -p $out $root/runtime/logs
  # Silence: the Vita profile loads no audio library (FMOD stubs, SHEPHERD_AUDIO=off, null renderer: no SDL either), so a run is
  # silent by construction. The output-muted state is logged before and after every run; it is never changed here (it was
  # unmuted from outside during this study). Every run also checks the engine's native-resolution census: no audio / FMOD / SDL
  # binding, no game plugin (audio_bindings= / no_game_plugins= in wall.txt): the per-run proof that nothing could play.
  local muted=$(osascript -e 'output muted of (get volume settings)' 2>/dev/null)
  [[ $muted == true || -n ${ALLOW_UNMUTED:-} ]] || { echo "$name: output NOT muted - refusing (ALLOW_UNMUTED=1: rely on the per-run audio-library proof)"; return 9; }
  cp -R $P/snap/persistent $root/runtime/persistent
  local mo=--full-aot-interp; [[ $c == I || $c == HI ]] && mo=--interpreter
  local -a envs=(SHEPHERD_ROOT=$root SHEPHERD_GAME_DATA=$data SHEPHERD_MANAGED=$C/Managed SHEPHERD_AUDIO=off
    SHEPHERD_PROFILE=vita SHEPHERD_RENDER=null $pack SHEPHERD_PAD_FILE=$pad $timing
    MONO_ENV_OPTIONS="$mo ${MONO_EXTRA:-}" MONO_THREADS_SUSPEND=coop MONO_GC_PARAMS=${BENCH_GC_PARAMS:-$GC_PARAMS} "$@")
  print -l -- config=$c scenario=$sc muted=$muted $envs > $out/env.txt
  local t0=$EPOCHREALTIME
  (cd $root && env $envs ${=BENCH_WRAP} $C/pub/Shepherd.Engine boot $frames 50 > $out/console.txt 2>&1); local rc=$?
  printf 'wall_s=%.3f\nrc=%d\nload1=%s\nmuted_after=%s\n' $(( EPOCHREALTIME - t0 )) $rc "$(sysctl -n vm.loadavg)" \
    "$(osascript -e 'output muted of (get volume settings)' 2>/dev/null)" > $out/wall.txt
  cp $root/runtime/logs/*(N) $out/ 2>/dev/null; rm -rf $root
  [[ -f $out/engine.log ]] || { echo "$name: rc=$rc, no engine.log"; tail -20 $out/console.txt; return 1; }
  # the per-run silence proof: the engine's native-resolution census (every library managed code bound) holds no audio / FMOD /
  # SDL binding and no game plugin
  local nb=$(sed -n '/^## 1\./,/^## 1b/p' $out/native-resolve.txt 2>/dev/null | grep -ciE 'fmod|sdl|audio|openal')
  # (the census lists every dyld image named like a game assembly as a "game plugin": our own Mono AOT images <asm>.dll.dylib
  #  of FMODUnity / GalaxyCSharp are those, not FMOD's or Galaxy's native libraries; they are excluded here)
  local gp=$(grep -m1 -o 'game plugins mapped: \[[^]]*\]' $out/native-resolve.txt 2>/dev/null | sed 's/.*\[//; s/\]//' | tr ',' '\n' \
             | sed 's/^ *//' | grep -v '^$' | grep -vc '\.dll\.dylib$')
  gp=$(( gp == 0 ? 1 : 0 ))
  print "audio_bindings=$nb\nno_game_plugins=$gp" >> $out/wall.txt
  [[ $nb == 0 && $gp == 1 ]] || { echo "$name: audio/plugin binding found (or no census) - stopping"; return 8; }
  echo "$name: rc=$rc $(python3 $D/summary.py --line $out)"
}

case $1 in
  build) build ;;
  snap) snap ;;
  closure) closure ;;
  profiles) profiles ;;
  aot) shift; aot "$@" ;;
  cfg) shift; for c in "$@"; do cfg $c || exit 1; done ;;
  run) shift; run "$@" ;;
  rounds) local n=$2 cs=(${=3}) scs=(${=4}) pf=$5   # the config order rotates by one every round (no fixed position in a round)
          for ((k = 1; k <= n; k++)); do local r=$(( (k - 1) % ${#cs} )); local -a ord=(${cs[r+1,-1]} ${cs[1,r]})
            for s in $scs; do for c in $ord; do run $c $s $pf$k-$s-$c || echo "RUN FAILED $pf$k-$s-$c"; done; done; done ;;
  *) sed -n '2,28p' $0; exit 2 ;;
esac
