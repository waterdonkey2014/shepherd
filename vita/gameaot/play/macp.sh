#!/bin/zsh
# kit 4, game AOT in the play app (vita/notes/game-aot.md): the Mac arm64 proxy of the three execution models the play apps can run,
# built from ONE engine build of this tree and the card's owner folder (the managed DLLs the Vita images bind to):
#   interp  Mono 8.0.29 osx-arm64 INTERPRETER (--interpreter): every byte of managed code interpreted (the shareable apps' model)
#   ad      full AOT + interpreter (--full-aot-interp): the Vita's 12 AOT modules + Shepherd.Engine (build.sh's inline allowlist) +
#           Shepherd.Engine.Derived = the play apps 00097 / 00098 (the game code interpreted)
#   p       ad + the owner game modules of vita/gameaot/owner-modules-p.txt, PROFILE-GUIDED PARTIAL (profile-only + strict) = SHPD00095
#           (on the Mac with the value-type closure profile: the stock 8.0.29 runtime has no fix A; the Vita build uses fix A instead)
# compiled by the gameaot fork's osx-arm64 Mono (Mono's own arm64 backend, the partial-AOT patches), CoreLib with the bigger pools in
# every AOT config (pool size does not change speed). Two run kinds:
#   gl    real GL (SDL3's GL context, hidden window; GlBackend + the derived render provider; DXT; the card's strict pack): frames as PNG
#         + the per-frame draw-stream hash -> byte-for-byte comparison (macgl.sh's method, kit 3)
#   null  the null renderer (vita-sim timing: [sim] window, render / logic split) -> the idle-menu and menu-tour windows of the
#         game-aot prototype (idle m+487..m+586, tour m+494..m+1793)
# Everything it writes is owner-local, under $M (compiled game code, the card's DLLs): never in git. Silent: the Vita profile's FMOD
# stubs, SHEPHERD_AUDIO=off; the output-muted state is checked before every run and logged after it. Saves: copies of the card's.
#   macp.sh build                 publish this tree's engine -> $M/B/pub; the derived DLL against it; data-{interp,ad,p} (card folder copies)
#   macp.sh aot                   engine set + derived -> $M/B/img; owner partial images -> $M/B/img-own; cfg-{ad,p}
#   macp.sh run <cfg> <gl|null> <boot|tour|idle> <name> [ENV=VAL ...]   -> $M/runs/<name>
#   macp.sh cmp <runA> <runB>     frames byte for byte + the stream hash (gl runs)
#   macp.sh rounds <n> "<cfgs>" "<scenarios>" <prefix>   interleaved null-renderer timing rounds (config order rotates every round)
setopt pipe_fail
zmodload zsh/datetime
WT=${0:A:h:h:h:h}
M=${MACP_DIR:-${SHEPHERD_WORK:-$HOME/shepherd-work}/kit4/gameaot-work/mac}; OWN=${MACP_OWNER:-${SHEPHERD_WORK:-$HOME/shepherd-work}/kit3/kit-build2/owner/shepherd}
export TMPDIR=${SHEPHERD_WORK:-$HOME/shepherd-work}/tmp/k4ga-mac DOTNET_ROOT=$HOME/.dotnet PATH="$HOME/.dotnet:$PATH" DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
mkdir -p $TMPDIR $M
MS=${MACP_MONO:-${SHEPHERD_WORK:-$HOME/shepherd-work}/gameaot/proto/obj/mono-osx/mono/mini/mono-sgen}   # the gameaot fork's osx-arm64 Mono (AOT compiler)
PROFILE=${MACP_PROFILE:-${SHEPHERD_WORK:-$HOME/shepherd-work}/gameaot/profiles/owner-p-closure.aotprofile}
SET=(System.Private.CoreLib System.Linq System.Collections System.Collections.Concurrent System.Console System.Memory
     System.ObjectModel System.Runtime.InteropServices System.Threading Shepherd.Assets Shepherd.Hooks Microsoft.Bcl.AsyncInterfaces)
ENG_INL="Shepherd.Engine,Shepherd.Assets,Shepherd.Hooks,System.*,Microsoft.*,mscorlib,netstandard"
OWNER=($(grep -v '^#' $WT/vita/gameaot/owner-modules-p.txt))
# build.sh's owner inline set: the owner modules of the game-aot study's census (proto lists, minus Shepherd.*) + the BCL
INL_OWN=(${(u)OWNER} $(grep -hv '^#' $WT/vita/gameaot/proto/modules-rewritten.txt $WT/vita/gameaot/proto/modules-game.txt | grep -v '^Shepherd\.'))
INL_OWN=(${(u)INL_OWN}); OWN_INL="${(j:,:)INL_OWN},System.*,Microsoft.*,mscorlib,netstandard"
TRAMPS=ntrampolines=32768,nrgctx-trampolines=16384,nimt-trampolines=4096,ngsharedvt-trampolines=4096,nftnptr-arg-trampolines=4096,nunbox-arbitrary-trampolines=1024
GC_PARAMS=major=marksweep,no-concurrent-sweep,nursery-size=4m
B=$M/B

build() {
  mkdir -p $B
  print -r -- "src=$WT commit=$(git -C $WT rev-parse HEAD) dirty=$(git -C $WT status --porcelain --untracked-files=no | wc -l | tr -d ' ')" > $B/source.txt
  dotnet publish $WT/engine/Shepherd.Engine/Shepherd.Engine.csproj -c Release -r osx-arm64 --self-contained -p:UseMonoRuntime=true -p:InvariantGlobalization=true \
    -p:PredefinedCulturesOnly=false -p:DisableTransitiveFrameworkReferenceDownloads=true --source ~/.nuget/packages --disable-build-servers -p:UseSharedCompilation=false \
    --artifacts-path $B/art -o $B/pub 2>&1 | grep -E " error |Shepherd.Engine -> " || return 1
  grep -q '"System.Globalization.Invariant": true' $B/pub/Shepherd.Engine.runtimeconfig.json || { echo "publish: runtimeconfig not invariant"; return 1; }
  rm -rf $M/derived $M/data-interp $M/data-ad $M/data-p; mkdir -p $M/derived
  SHEPHERD_DERIVED_OUT=$M/derived SHEPHERD_DERIVED_ART=$M/derived-art SHEPHERD_DERIVED_ENGINE=$B/pub/Shepherd.Engine.dll zsh $WT/vita/shepherd/build-derived.sh > $M/derived/build.txt 2>&1 \
    || { cat $M/derived/build.txt; return 1; }
  for d in data-interp data-ad data-p; do
    mkdir -p $M/$d; cp -Rc $OWN/managed $M/$d/Managed; cp -Rc $OWN/data/. $M/$d/; cp $M/derived/Shepherd.Engine.Derived.dll $M/$d/Managed/
  done
  echo "build: pub $(ls $B/pub/*.dll | wc -l | tr -d ' ') DLLs; derived $(shasum -a 256 $M/derived/Shepherd.Engine.Derived.dll | cut -c1-16); owner DLLs = the card's ($OWN/managed)"
}

aot1() {   # aot1 <out dir> <dll> <MONO_PATH> <opts> [ENV=VAL ...]
  local W=$1 dll=$2 mp=$3 opts=$4; shift 4; local a=${${dll:t}%.dll} t0=$EPOCHREALTIME
  env "$@" MONO_AGGRESSIVE_INLINELIMIT=256 MONO_THREADS_SUSPEND=coop MONO_PATH=$mp nice -n 5 $MS --aot=$opts,outfile=$W/$a.dll.dylib $dll > $W/$a.log 2>&1 \
    || { tail -15 $W/$a.log; return 1; }
  rm -rf $W/$a.dll.dylib.dSYM $W/tmp/*(N)
  printf '   %s: %s (%.0f s, %s)\n' $a "$(grep -o 'Compiled: [0-9/]*' $W/$a.log | tail -1)" $(( EPOCHREALTIME - t0 )) "$(du -h $W/$a.dll.dylib | cut -f1)"
}

aot() {
  local W=$B/img O=$B/img-own; rm -rf $W $O; mkdir -p $W/tmp $O/tmp
  strings $MS | grep SHEPHERD_AOT_PROFILE_STRICT > /dev/null || { echo "$MS lacks the gameaot compiler patches"; return 1; }
  for a in $SET; do
    local opts="full,interp,temp-path=$W/tmp"; [[ $a == System.Private.CoreLib ]] && opts="$opts,$TRAMPS"
    aot1 $W $B/pub/$a.dll $B/pub $opts || return 1
  done
  aot1 $W $B/pub/Shepherd.Engine.dll $B/pub:$M/data-ad/Managed "full,interp,temp-path=$W/tmp" SHEPHERD_AOT_INLINE_IMAGES=$ENG_INL || return 1
  aot1 $W $M/data-ad/Managed/Shepherd.Engine.Derived.dll $B/pub:$M/data-ad/Managed "full,interp,temp-path=$W/tmp" SHEPHERD_AOT_INLINE_IMAGES="Shepherd.Engine.Derived,$ENG_INL" || return 1
  for a in $OWNER; do
    aot1 $O $M/data-p/Managed/$a.dll $B/pub:$M/data-p/Managed "full,interp,temp-path=$O/tmp,profile=$PROFILE,profile-only" \
      SHEPHERD_AOT_INLINE_IMAGES=$OWN_INL SHEPHERD_AOT_PROFILE_STRICT=1 || return 1
  done
  for c in ad p; do
    rm -f $M/data-$c/Managed/*.dll.dylib(N)   # no image of an earlier owner list stays next to its DLL (Mono would load it)
    local P=$B/cfg-$c; rm -rf $P; cp -cR $B/pub $P
    for a in $SET Shepherd.Engine; do cp -c $W/$a.dll.dylib $P/ || return 1; done
    cp -c $W/Shepherd.Engine.Derived.dll.dylib $M/data-$c/Managed/   # Mono looks for <assembly path>.dylib next to the DLL it loads
    python3 -c "import json,sys; p=sys.argv[1]; j=json.load(open(p)); j['runtimeOptions'].setdefault('configProperties',{})['APP_PATHS']=sys.argv[2]; json.dump(j,open(p,'w'),indent=2)" \
      $P/Shepherd.Engine.runtimeconfig.json $M/data-$c/Managed
  done
  for a in $OWNER; do cp -c $O/$a.dll.dylib $M/data-p/Managed/ || return 1; done
  echo "cfg-ad: $(ls $B/cfg-ad/*.dll.dylib | wc -l | tr -d ' ') engine-set images + derived; cfg-p: the same + ${#OWNER} owner images ($(du -ch $O/*.dylib | tail -1 | cut -f1))"
}

win() { local -a w; for p in "$@"; do w+=("$((p-1))-$((p+10))"); done; print -r -- "${(j:,:)w}"; }
run() {   # run <interp|ad|p> <gl|null> <boot|tour|idle> <name> [ENV=VAL ...]
  local c=$1 kind=$2 sc=$3 name=$4; shift 4
  local exe mo data
  case $c in
    interp) exe=$B/pub/Shepherd.Engine; mo=--interpreter; data=$M/data-interp ;;
    ad|p) exe=$B/cfg-$c/Shepherd.Engine; mo=--full-aot-interp; data=$M/data-$c ;;
    *) echo "config interp|ad|p"; return 2 ;;
  esac
  local frames every=50 dense probe; local -a pad timing rend
  case $kind/$sc in
    gl/boot) frames=1460; pad=(SHEPHERD_PAD=1050:cross,1400:down); dense="800-812,$(win 1050 1400)"; probe=1040,1300,1450 ;;
    gl/tour) frames=2600; every=10; pad=(SHEPHERD_PAD_FILE=$WT/vita/gameaot/pads/pad-tour.txt)
             dense=$(win 1050 1300 1330 1500 1600 1630 1660 1900 2000 2030 2200 2300 2330 2500); probe=1299,1336,1506,1666,1906,2036,2206,2336,2506 ;;
    null/idle) frames=4000; pad=(SHEPHERD_PAD_FILE=$WT/vita/v6/pad-menu-dlc-rel.txt)
               timing=(SHEPHERD_MENU_PROBE=m+226,m+486,m+636 SHEPHERD_SIMSTATS=m+487:m+586:m+486 SHEPHERD_STOP_AT=m+646) ;;
    null/tour) frames=4000; pad=(SHEPHERD_PAD_FILE=$WT/vita/gameaot/pads/pad-tour-rel.txt)
               timing=(SHEPHERD_MENU_PROBE=m+226,m+486,m+636 SHEPHERD_SIMSTATS=m+494:m+1793:m+486 SHEPHERD_STOP_AT=m+1800) ;;
    *) echo "kind/scenario: gl/boot gl/tour null/idle null/tour"; return 2 ;;
  esac
  local out=$M/runs/$name root=$M/roots/$name
  rm -rf $out $root; mkdir -p $out $root/runtime/logs; cp -R $OWN/persistent $root/runtime/persistent
  if [[ $kind == gl ]]; then
    mkdir -p $out/shots
    rend=(SHEPHERD_RENDER=gl SHEPHERD_GL_DXT=1 SHEPHERD_SHOT_DIR=$out/shots SHEPHERD_SHOT_FRAMES=$dense SHEPHERD_MENU_PROBE=$probe SHEPHERD_STREAM_HASH=$out/stream-hash.txt)
  else
    rend=(SHEPHERD_RENDER=null $timing)
  fi
  local -a envs=(SHEPHERD_ROOT=$root SHEPHERD_GAME_DATA=$data SHEPHERD_MANAGED=$data/Managed SHEPHERD_AUDIO=off SHEPHERD_PROFILE=vita
    SHEPHERD_PACK=$OWN/pack SHEPHERD_PACK_STRICT=1 $rend $pad
    MONO_ENV_OPTIONS="$mo ${MACP_MONO_EXTRA:-}" MONO_THREADS_SUSPEND=coop MONO_GC_PARAMS=$GC_PARAMS "$@")
  local muted=$(osascript -e 'output muted of (get volume settings)' 2>/dev/null)
  print -l -- config=$c kind=$kind scenario=$sc muted=$muted $envs > $out/env.txt
  [[ $muted == true ]] || { echo "$name: output NOT muted - refusing"; return 9; }
  local t0=$EPOCHREALTIME
  (cd $root && env $envs $exe boot $frames $every > $out/console.txt 2>&1); local rc=$?
  cp $root/runtime/logs/*(N) $out/ 2>/dev/null
  (cd $root/runtime/persistent && find . -type f | sort | xargs md5 -r) > $out/persistent.md5 2>/dev/null   # the saves after the run (state)
  rm -rf $root
  printf 'wall_s=%.3f\nrc=%d\nload1=%s\nmuted_after=%s\n' $(( EPOCHREALTIME - t0 )) $rc "$(sysctl -n vm.loadavg)" "$(osascript -e 'output muted of (get volume settings)' 2>/dev/null)" > $out/wall.txt
  [[ -f $out/engine.log ]] || { echo "$name: rc=$rc, no engine.log"; tail -20 $out/console.txt; return 1; }
  # silence proof per run: the engine's native-resolution census binds no audio / FMOD / SDL-audio library and no game plugin (our own
  # Mono AOT images <asm>.dll.dylib are named like game assemblies; they are not plugins)
  local nb=$(sed -n '/^## 1\./,/^## 1b/p' $out/native-resolve.txt 2>/dev/null | grep -ciE 'fmod|audio|openal')
  print "audio_bindings=$nb" >> $out/wall.txt
  if [[ $kind == gl ]]; then
    echo "$name: rc=$rc $(ls $out/shots/f*.png(N) | wc -l | tr -d ' ') shots, $(wc -l < $out/stream-hash.txt 2>/dev/null | tr -d ' ') hash lines; $(grep -oE 'done: [0-9]+ errors, [0-9]+ exceptions' $out/engine.log | tail -1); audio bindings $nb; $(grep -h '\[mt\] multicore\|\[mtcheck\]' $out/engine.log | sed 's/^\[ *[0-9]*\] *//' | cut -c1-160 | tr '\n' ' ')"
  else
    echo "$name: rc=$rc audio bindings $nb $(python3 $WT/vita/gameaot/proto/summary.py --line $out)"
  fi
}

cmpr() {
  local a=$M/runs/$1 b=$M/runs/$2 n=0 d=0 list=""
  for f in $a/shots/f*.png(N); do [[ -f $b/shots/${f:t} ]] || { d=$((d+1)); list+=" ${${f:t:r}#f}(missing)"; continue; }; n=$((n+1)); cmp -s $f $b/shots/${f:t} || { d=$((d+1)); list+=" ${${f:t:r}#f}"; }; done
  local na=$(ls $a/shots/f*.png(N) | wc -l | tr -d ' ') nb=$(ls $b/shots/f*.png(N) | wc -l | tr -d ' ')
  local sh; cmp -s $a/stream-hash.txt $b/stream-hash.txt && sh="stream hash identical ($(wc -l < $a/stream-hash.txt | tr -d ' ') frames)" || sh="stream hash DIFFERS"
  # state: the menu probes (selected item + label at each probe frame), the run's error / exception count, the saves after the run
  local pa=$(grep -h '\[menu\] frame' $a/engine.log | sed -E 's/^\[ *[0-9]*\] *//' | md5) pb=$(grep -h '\[menu\] frame' $b/engine.log | sed -E 's/^\[ *[0-9]*\] *//' | md5)
  local ea=$(grep -oE 'done: [0-9]+ errors, [0-9]+ exceptions' $a/engine.log | tail -1) eb=$(grep -oE 'done: [0-9]+ errors, [0-9]+ exceptions' $b/engine.log | tail -1)
  local st=same; [[ $pa == $pb && $ea == $eb ]] && cmp -s $a/persistent.md5 $b/persistent.md5 || st=DIFFERS
  echo "$1 vs $2: frames identical $((n-d+0))/$na (B has $nb)${list:+ -- differ:$list}; $sh; state (menu probes $(grep -c '\[menu\] frame' $a/engine.log), '$ea', saves): $st"
  (( d == 0 )) && cmp -s $a/stream-hash.txt $b/stream-hash.txt && (( na == nb && na > 0 )) && [[ $st == same ]]
}

case $1 in
  build) build ;;
  aot) aot ;;
  run) shift; run "$@" ;;
  cmp) cmpr $2 $3 ;;
  rounds) n=$2; cs=(${=3}); scs=(${=4}); pf=$5
          for ((k = 1; k <= n; k++)); do r=$(( (k - 1) % ${#cs} )); ord=(${cs[r+1,-1]} ${cs[1,r]})
            for s in $scs; do for c in $ord; do run $c null $s $pf$k-$s-$c || echo "RUN FAILED $pf$k-$s-$c"; done; done; done ;;
  *) sed -n '2,24p' $0; exit 2 ;;
esac
