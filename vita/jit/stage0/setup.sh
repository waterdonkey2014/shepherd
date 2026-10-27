#!/bin/zsh
# Kit #7 JIT stage 0 (vita/notes/kit7-jit.md): the Mac work area for the long-session JIT census and the normal-AOT (J1) bench.
# Everything lands under $JIT_WORK/mac (default ${SHEPHERD_WORK:-$HOME/shepherd-work}/kit7/jit-work/mac): derived game data (Managed = the game's DLLs,
# the rewritten Unity modules, the owner-local Derived DLL; the pack) never leaves the build volume and never enters git.
#   setup.sh pub                the vita-sim publish of THIS worktree's engine on the osx-arm64 Mono pack -> mac/pub-base
#                               (sim.sh's publish recipe, but only the Mono publish and only into the work dir: sim.sh publish also
#                               rebuilds the shared ${SHEPHERD_WORK:-$HOME/shepherd-work}/v6/clrrel)
#   setup.sh data               mac/Data (Managed = an APFS clone of the kit #6 owner folder's managed/ = the card's set; the pack's JSON),
#                               mac/GameData (the owner's install, read only, by symlink; Managed -> mac/Data/Managed), mac/snap/pack
#                               (a clone of the kit #6 card pack), mac/snap/persistent (the sandbox saves; never the real save dir)
#   setup.sh aot <full|norm>    the AOT set (vita/llvm/macbench.sh's 12 BCL modules + Shepherd.Engine) compiled by the fork's osx-arm64
#                               Mono: full = 'full,interp' (the Vita's model today: --full-aot-interp), norm = normal AOT (no full, no
#                               interp: Mono's default mode loads it and JIT-compiles everything else) -> mac/aot-<v>/<X>.dll.dylib
#   setup.sh cfg <name>         a run tree mac/cfg-<name>/pub: I / J (no AOT image), A (aot-full, 13), J1 (aot-norm, the 12 BCL modules:
#                               the shareable set), J1E (aot-norm, 13: + the owner-local engine image); Jnd / J1nd / J1End = the same with
#                               RuntimeFeature.IsDynamicCodeSupported=false in the runtimeconfig -- kept only to show that pin's effect: it
#                               is NOT today's Vita behaviour (kit #7 review: 31 / 66 extra 'Dynamic code generation is not supported'
#                               deserialization exceptions on idle / intro; vita/notes/kit7-jit.md "Review fixes")
setopt pipe_fail err_exit
zmodload zsh/datetime
HERE=${0:A:h}; REPO=${HERE:h:h:h}
W=${JIT_WORK:-${SHEPHERD_WORK:-$HOME/shepherd-work}/kit7/jit-work}/mac
OWNER=${JIT_OWNER:-${SHEPHERD_WORK:-$HOME/shepherd-work}/kit6/kit-build/owner/shepherd}
GAME_DATA="${SHEPHERD_GAME_DATA}"
MS=${SHEPHERD_MAC_LLVM_OBJ:-${SHEPHERD_WORK:-$HOME/shepherd-work}/obj/mono-osx-llvm}/mono/mini/mono-sgen
export TMPDIR=${JIT_WORK:-${SHEPHERD_WORK:-$HOME/shepherd-work}/kit7/jit-work}/tmp DOTNET_ROOT=$HOME/.dotnet PATH="$HOME/.dotnet:$PATH" DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
mkdir -p $W $TMPDIR
SET=(System.Private.CoreLib System.Linq System.Collections System.Collections.Concurrent System.Console System.Memory
     System.ObjectModel System.Runtime.InteropServices System.Threading Shepherd.Assets Shepherd.Hooks Microsoft.Bcl.AsyncInterfaces)

pub() {
  local ENG=$REPO/engine/Shepherd.Engine
  dotnet publish "$ENG/Shepherd.Engine.csproj" -c Release -r osx-arm64 --self-contained -p:UseMonoRuntime=true -p:InvariantGlobalization=true \
    -p:PredefinedCulturesOnly=false -p:DisableTransitiveFrameworkReferenceDownloads=true --source ~/.nuget/packages --artifacts-path $W/art-mono \
    -o $W/pub-base 2>&1 | grep -E " error |Shepherd.Engine ->"
  grep -q '"System.Globalization.Invariant": true' $W/pub-base/Shepherd.Engine.runtimeconfig.json
  echo "pub-base: $(git -C $REPO rev-parse --short HEAD), $(ls $W/pub-base | wc -l | tr -d ' ') files, Mono (libcoreclr.dylib) $(shasum -a 256 $W/pub-base/libcoreclr.dylib | cut -c1-16)"
}

data() {
  [[ -d $OWNER/managed ]] || { echo "no owner folder $OWNER"; return 1; }
  rm -rf $W/Data $W/GameData $W/snap; mkdir -p $W/Data $W/GameData $W/snap
  cp -cR $OWNER/managed $W/Data/Managed
  cp -cR $OWNER/data/. $W/Data/
  rm -f $W/Data/video-lengths.tsv   # the census keeps kit #5's frame timeline (no splash re-base on the Mac)
  for e in "$GAME_DATA"/*(N); do [[ ${e:t} == Managed ]] || ln -s "$e" $W/GameData/${e:t}; done
  ln -s $W/Data/Managed $W/GameData/Managed
  mkdir -p $W/snap/pack; cp -c $OWNER/pack/shepherd.pack $W/snap/pack/shepherd.pack; cp -cR $OWNER/data $W/snap/pack/data
  cp -R $REPO/runtime/persistent $W/snap/persistent
  echo "data: $(ls $W/Data/Managed | wc -l | tr -d ' ') managed files ($(du -sh $W/Data/Managed | cut -f1)); pack $(shasum -a 256 $W/snap/pack/shepherd.pack | cut -c1-8)"
}

aot() {
  local v=$1 A=$W/aot-$1; mkdir -p $A/tmp
  local base="temp-path=$A/tmp"; [[ $v == full ]] && base="full,interp,$base"
  [[ $v == full || $v == norm ]] || { echo "aot full|norm"; return 2; }
  local xs=$(shasum -a 256 $MS | cut -c1-16)
  for a in $SET Shepherd.Engine; do
    local opts=$base envs=() mp=$W/pub-base
    [[ $a == System.Private.CoreLib && $v == full ]] && opts="$opts,ntrampolines=8192,nrgctx-trampolines=8192,nimt-trampolines=2048,ngsharedvt-trampolines=4096,nftnptr-arg-trampolines=4096,nunbox-arbitrary-trampolines=1024"
    if [[ $a == Shepherd.Engine ]]; then
      envs=(SHEPHERD_AOT_INLINE_IMAGES="Shepherd.Engine,Shepherd.Assets,Shepherd.Hooks,System.*,Microsoft.*,mscorlib,netstandard"); mp=$W/pub-base:$W/Data/Managed
    fi
    local want="x=$xs opts=$opts in=$(shasum -a 256 $W/pub-base/$a.dll | cut -c1-16)"
    if [[ -f $A/$a.dll.dylib && "$(cat $A/$a.stamp 2>/dev/null)" == "$want" ]]; then echo "   aot-$v $a: up to date"; continue; fi
    local t0=$EPOCHREALTIME
    env $envs MONO_AGGRESSIVE_INLINELIMIT=256 MONO_THREADS_SUSPEND=coop MONO_PATH=$mp nice -n 10 $MS --aot=$opts,outfile=$A/$a.dll.dylib $W/pub-base/$a.dll > $A/$a.log 2>&1 \
      || { tail -15 $A/$a.log; return 1; }
    rm -rf $A/$a.dll.dylib.dSYM
    echo $want > $A/$a.stamp
    printf '   aot-%s %s: %s (%.0f s, %s)\n' $v $a "$(grep -o 'Compiled: [0-9/]*' $A/$a.log | tail -1)" $(( EPOCHREALTIME - t0 )) "$(du -h $A/$a.dll.dylib | cut -f1)"
  done
}

cfg() {
  local c=$1 P=$W/cfg-$1/pub
  rm -rf $W/cfg-$c; mkdir -p $W/cfg-$c; cp -cR $W/pub-base $P
  local -a imgs=()
  case $c in
    I|J|Jnd) ;;
    A)   imgs=($W/aot-full/${^SET}.dll.dylib $W/aot-full/Shepherd.Engine.dll.dylib) ;;
    J1|J1nd)   imgs=($W/aot-norm/${^SET}.dll.dylib) ;;
    J1E|J1End) imgs=($W/aot-norm/${^SET}.dll.dylib $W/aot-norm/Shepherd.Engine.dll.dylib) ;;
    *) echo "cfg I|J|Jnd|A|J1|J1nd|J1E|J1End"; return 2 ;;
  esac
  if [[ $c == *nd ]]; then
    # RuntimeFeature.IsDynamicCodeSupported=false (the AppContext switch). Kit #7 review fix: this does NOT reproduce the Vita's runtime.
    # With the pin the game's serializer fails to emit its formatters ("Dynamic code generation is not supported on this platform") for
    # 29 UI types by the menu, 63 by the intro; config A (--full-aot-interp, the Vita's model) and J throw none of those (under A the
    # emitted code works: the interpreter runs it). The stream hashes still match A on the measured windows (those menus are not opened
    # there), but the pin is a behaviour change: do not use it.
    python3 -c "import json,sys; p=sys.argv[1]; j=json.load(open(p)); j['runtimeOptions'].setdefault('configProperties',{})['System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported']=False; json.dump(j,open(p,'w'),indent=2)" \
      $P/Shepherd.Engine.runtimeconfig.json
  fi
  for i in $imgs; do cp $i $P/; done
  if (( ${#imgs} )) && [[ -n ${imgs[(r)*Shepherd.Engine.dll.dylib]} ]]; then
    # the engine image names the Unity / game assemblies it was compiled against; decoding an AOT method loads them by name: APP_PATHS (probed
    # by the default ALC) plays the Vita host's preload hook (vita/llvm/macbench.sh pub)
    python3 -c "import json,sys; p=sys.argv[1]; j=json.load(open(p)); j['runtimeOptions'].setdefault('configProperties',{})['APP_PATHS']=sys.argv[2]; json.dump(j,open(p,'w'),indent=2)" \
      $P/Shepherd.Engine.runtimeconfig.json $W/Data/Managed
  fi
  print "config=$c images=${#imgs} $(date -u +%Y%m%d-%H%M%S)" > $W/cfg-$c/cfg.txt
  echo "cfg-$c: ${#imgs} AOT images"
}

case $1 in
  pub) pub ;;
  data) data ;;
  aot) aot $2 ;;
  cfg) shift; for c in "$@"; do cfg $c; done ;;
  *) sed -n '2,18p' $0; exit 2 ;;
esac
