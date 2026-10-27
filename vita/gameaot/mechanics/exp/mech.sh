#!/bin/zsh
# GAME-AOT mechanics experiment (vita/gameaot/mechanics/exp; ${SHEPHERD_WORK:-$HOME/shepherd-work}/gameaot/mechanics.md): two assemblies -- App (stand-in for
# Shepherd.Engine: always AOT, with the fork's inline allowlist) and Lib (stand-in for an owner-local Unity/game module:
# interpreted, fully AOT'd, or PARTIALLY AOT'd from a training profile with profile=,profile-only) -- on the Mac proxy:
# Microsoft's osx-arm64 Mono 8.0.29 runtime in full AOT + interpreter mode (--full-aot-interp = MONO_AOT_MODE_INTERP, the
# Vita's mode), the fork's osx-arm64 mono-sgen (8.0.31 + the Shepherd patches, Mono backend) as the AOT compiler, AOT images
# either dynamic (<assembly>.dylib) or STATIC (linked into the host and registered with mono_aot_register_module, as the
# Vita host does). Silent: console output only.
# usage:
#   mech.sh build                IL (App + Lib, and Lib variant 2 = same API, new MVID) -> $E/il, $E/il-v2
#   mech.sh bcl                  the pack's BCL -> $E/bcl, CoreLib AOT (full,interp + the Vita's trampoline pools) next to it
#   mech.sh profile              train run (App AOT, Lib interpreted, --interp=-inline) with the gameaotprof module -> $E/prof/lib.*
#   mech.sh aot-base             App (allowlist; and the Mac-only inlining probe) + Lib full -> $E/aot/<variant>/ (dylib + static .o)
#   mech.sh aot-part             Lib partial (profile-only) after `profile`
#   mech.sh hosts                one host per static configuration -> $E/hosts/<cfg>/host
#   mech.sh run <cfg> <args...>  one run (cfg: interp app app+lib app+libpart appinl s-app+lib s-app+libpart ...)
#   mech.sh checks               the check mode in every configuration, compared with the interpreter's output
#   mech.sh rounds <n>           n interleaved bench rounds over the timing configurations, then the summary
#   mech.sh guid                 the GUID / version-gate experiments
setopt pipe_fail
zmodload zsh/datetime
HERE=${0:A:h}
export TMPDIR=${SHEPHERD_WORK:-$HOME/shepherd-work}/tmp DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 PATH=$HOME/.dotnet:$PATH DOTNET_ROOT=$HOME/.dotnet
E=${GAMEAOT_MECH:-${SHEPHERD_WORK:-$HOME/shepherd-work}/gameaot/mech}
MS=${SHEPHERD_MAC_AOT:-${SHEPHERD_WORK:-$HOME/shepherd-work}/obj/mono-osx-llvm/mono/mini/mono-sgen}   # the fork's osx-arm64 AOT compiler
PK=$HOME/.nuget/packages/microsoft.netcore.app.runtime.mono.osx-arm64/8.0.29/runtimes/osx-arm64
PROFDIR=${GAMEAOT_OUT:-${SHEPHERD_WORK:-$HOME/shepherd-work}/gameaot}/aotprof
CORELIB_TRAMPS=",ntrampolines=8192,nrgctx-trampolines=8192,nimt-trampolines=2048,ngsharedvt-trampolines=4096,nftnptr-arg-trampolines=4096,nunbox-arbitrary-trampolines=1024"
ALLOW="App,System.*,Microsoft.*,mscorlib,netstandard"      # App may inline itself + the BCL, never Lib (build.sh's rule)
FIXC=${GAMEAOT_FIX_MONO:-${SHEPHERD_WORK:-$HOME/shepherd-work}/gameaot/obj/mono-osx-fix/mono/mini/mono-sgen}   # the patched fork build (forkfix)
TIMING_CFGS=(interp app app+lib app+libpart app+libstrict appinl s-app+lib s-app+libpart s-app+libstrict)

build() {
  dotnet build $HERE/App/App.csproj -c Release -o $E/il -p:DisableTransitiveFrameworkReferenceDownloads=true --source ~/.nuget/packages --artifacts-path $E/art -nologo -v q > $E/build.log 2>&1 || { tail -20 $E/build.log; return 1; }
  dotnet build $HERE/Lib/Lib.csproj -c Release -p:LibVariant=2 -o $E/il-v2 -p:DisableTransitiveFrameworkReferenceDownloads=true --source ~/.nuget/packages --artifacts-path $E/art-v2 -nologo -v q > $E/build-v2.log 2>&1 || { tail -20 $E/build-v2.log; return 1; }
  python3 $HERE/mvid.py $E/il/App.dll $E/il/Lib.dll $E/il-v2/Lib.dll
}

bcl() {
  rm -rf $E/bcl; mkdir -p $E/bcl $E/aot/bcl
  cp -c $PK/lib/net8.0/*.dll $PK/native/System.Private.CoreLib.dll $E/bcl/
  local t0=$EPOCHREALTIME
  MONO_PATH=$E/bcl $MS --aot=full,interp$CORELIB_TRAMPS,temp-path=$E/aot/bcl,outfile=$E/bcl/System.Private.CoreLib.dll.dylib $E/bcl/System.Private.CoreLib.dll > $E/aot/bcl/corelib.log 2>&1 || { tail $E/aot/bcl/corelib.log; return 1; }
  rm -rf $E/bcl/*.dSYM
  printf 'CoreLib AOT: %s (%.0f s)\n' "$(grep -o 'Compiled: [0-9/]*' $E/aot/bcl/corelib.log | tail -1)" $(( EPOCHREALTIME - t0 ))
}

# aot1 <variant> <assembly> <static 0|1> [extra opts] [env...]
aot1() {
  local v=$1 a=$2 st=$3 extra=$4; shift 4
  local W=$E/aot/$v; mkdir -p $W/tmp
  local opts="full,interp,temp-path=$W/tmp$extra" t0=$EPOCHREALTIME
  if (( st )); then
    env "$@" MONO_PATH=$E/bcl:$E/il $MS --aot=$opts,static,asmonly,outfile=$W/$a.dll.s $E/il/$a.dll > $W/$a.log 2>&1 || { tail -20 $W/$a.log; return 1; }
    clang -c -arch arm64 -o $W/$a.dll.o $W/$a.dll.s || return 1
  else
    env "$@" MONO_PATH=$E/bcl:$E/il $MS --aot=$opts,outfile=$W/$a.dll.dylib $E/il/$a.dll > $W/$a.log 2>&1 || { tail -20 $W/$a.log; return 1; }
    rm -rf $W/$a.dll.dylib.dSYM
  fi
  local f=$W/$a.dll.dylib; (( st )) && f=$W/$a.dll.o
  printf '   %-14s %-4s %s, %s text symbols, %s B file, %.1f s%s\n' $v $a "$(grep -o 'Compiled: [0-9/]*' $W/$a.log | tail -1)" \
    "$(nm $f | awk '$2 ~ /^[Tt]$/' | wc -l | tr -d ' ')" "$(stat -f %z $f)" $(( EPOCHREALTIME - t0 )) \
    "$(grep -o '[0-9]* inline(s) of methods outside SHEPHERD_AOT_INLINE_IMAGES refused ([0-9]* distinct methods)' $W/$a.log | sed 's/^/; /')"
}

aot_base() {   # everything that needs no profile
  for st in 0 1; do
    local s=; (( st )) && s=s-
    aot1 ${s}app-allow App $st "" SHEPHERD_AOT_INLINE_IMAGES=$ALLOW SHEPHERD_AOT_INLINE_LOG=1 || return 1
    aot1 ${s}lib-full Lib $st "" || return 1
  done
  aot1 app-inl App 0 "" || return 1                         # Mac-only probe: App may inline Lib (never a Vita configuration)
}
aot_part() {   # the partial images (after `profile`)
  [[ -f $E/prof/lib.aotprofile ]] || { echo "no $E/prof/lib.aotprofile: mech.sh profile"; return 1; }
  local P=",profile=$E/prof/lib.aotprofile,profile-only"
  # with the stock (unpatched) fork compiler: the failure demonstrations
  aot1 lib-part-unfixed Lib 0 "$P" || return 1
  aot1 s-lib-part-unfixed Lib 1 "$P" || return 1
  aot1 lib-part-novt Lib 0 ",profile=$E/prof/lib-novt.aotprofile,profile-only" || return 1
  aot1 lib-part-empty Lib 0 ",profile=$E/prof/empty.aotprofile,profile-only" || return 1   # profile-only with no method: the floor
  # the working partial images: the patched compiler (fix B: keeps the gsharedvt_out_sig wrappers of the compiled methods) +
  # the value-type closure in the profile (the stock 8.0.29 runtime has no fix A)
  [[ -x $FIXC ]] || { echo "no patched compiler $FIXC: partial images skipped"; return 0; }
  local save=$MS; MS=$FIXC
  aot1 lib-part Lib 0 "$P"; aot1 s-lib-part Lib 1 "$P"
  # the recommended form: + SHEPHERD_AOT_PROFILE_STRICT (only the profile's methods and instances) and no gsharedvt bodies
  aot1 lib-strict Lib 0 "$P" SHEPHERD_AOT_PROFILE_STRICT=1 SHEPHERD_AOT_NO_GSHAREDVT=1
  aot1 s-lib-strict Lib 1 "$P" SHEPHERD_AOT_PROFILE_STRICT=1 SHEPHERD_AOT_NO_GSHAREDVT=1
  MS=$save
}

hosts() {
  local o
  local cfg; for cfg in host-none s-app+lib s-app+libpart s-app+libpart-unfixed s-app+libstrict s-app; do
    local H=$E/hosts/$cfg objs=() names=(); mkdir -p $H
    case $cfg in
      s-app+lib) objs=($E/aot/s-app-allow/App.dll.o $E/aot/s-lib-full/Lib.dll.o); names=(App Lib) ;;
      s-app+libpart) objs=($E/aot/s-app-allow/App.dll.o $E/aot/s-lib-part/Lib.dll.o); names=(App Lib) ;;
      s-app+libpart-unfixed) objs=($E/aot/s-app-allow/App.dll.o $E/aot/s-lib-part-unfixed/Lib.dll.o); names=(App Lib) ;;
      s-app+libstrict) objs=($E/aot/s-app-allow/App.dll.o $E/aot/s-lib-strict/Lib.dll.o); names=(App Lib) ;;
      s-app) objs=($E/aot/s-app-allow/App.dll.o); names=(App) ;;
    esac
    { echo "/* GENERATED by mech.sh hosts */"
      for n in $names; do echo "extern void *mono_aot_module_${n}_info;"; done
      echo "extern void mono_aot_register_module (void **aot_info);"
      echo "const char *gameaot_static_names = \"${names[*]}\";"
      echo "void gameaot_register_static (void) {"; for n in $names; do echo "\tmono_aot_register_module (mono_aot_module_${n}_info);"; done; echo "}"
    } > $H/static_tables.c
    for o in $objs; do [[ -f $o ]] || { echo "   $cfg: skipped (no $o yet)"; continue 2; }; done
    clang -O1 -arch arm64 -o $H/host $HERE/host_exp.c $H/static_tables.c $objs -L$PK/native -lcoreclr -Wl,-rpath,$PK/native || return 1
    echo "   $cfg: $H/host ($(stat -f %z $H/host) B)"
  done
}

# run <cfg> <managed args...>   (env: RUN_ENV=(K=V ...), MONO_OPTS_EXTRA, LIB_DLL=<alternative Lib.dll>)
run() {
  local cfg=$1; shift
  local R=$E/runs/$cfg; rm -rf $R; mkdir -p $R
  cp $E/il/App.dll $E/il/App.runtimeconfig.json $R/; cp ${LIB_DLL:-$E/il/Lib.dll} $R/Lib.dll
  local host=$E/hosts/host-none/host mo=--full-aot-interp
  case $cfg in
    interp) mo=--interpreter ;;
    app) cp $E/aot/app-allow/App.dll.dylib $R/ ;;
    app+lib) cp $E/aot/app-allow/App.dll.dylib $E/aot/lib-full/Lib.dll.dylib $R/ ;;
    app+libpart) cp $E/aot/app-allow/App.dll.dylib $E/aot/lib-part/Lib.dll.dylib $R/ ;;
    app+libpart-novt) cp $E/aot/app-allow/App.dll.dylib $E/aot/lib-part-novt/Lib.dll.dylib $R/ ;;
    app+libpart-unfixed) cp $E/aot/app-allow/App.dll.dylib $E/aot/lib-part-unfixed/Lib.dll.dylib $R/ ;;
    app+libstrict) cp $E/aot/app-allow/App.dll.dylib $E/aot/lib-strict/Lib.dll.dylib $R/ ;;
    appinl) cp $E/aot/app-inl/App.dll.dylib $R/ ;;
    app+libstale) cp $E/aot/app-allow/App.dll.dylib $E/aot/lib-full/Lib.dll.dylib $R/ ;;     # use with LIB_DLL=il-v2
    s-app+lib|s-app+libpart|s-app+libpart-unfixed|s-app+libstrict|s-app) host=$E/hosts/$cfg/host ;;
    *) echo "unknown cfg $cfg"; return 2 ;;
  esac
  local tpa=$(print -l $E/bcl/*.dll | tr '\n' ':')$R/App.dll:$R/Lib.dll
  print -r -- "env ${RUN_ENV[*]} $host <tpa> $PK/native $mo ${MONO_OPTS_EXTRA} $R/App.dll $*" > $R/cmd.txt
  (cd $R && env ${RUN_ENV[@]} $host "$tpa" $PK/native $mo ${=MONO_OPTS_EXTRA} $R/App.dll "$@" > $R/out.txt 2> $R/err.txt); local rc=$?
  echo $rc > $R/rc.txt
  return 0
}

checks() {
  run interp check; grep -E '^CHECK' $E/runs/interp/out.txt > $E/check-interp.txt
  local cfg; for cfg in app app+lib app+libpart-novt app+libpart-unfixed s-app+libpart-unfixed app+libpart app+libstrict appinl s-app+lib s-app+libpart s-app+libstrict; do
    RUN_ENV=(MONO_LOG_LEVEL=debug MONO_LOG_MASK=aot) run $cfg check
    local R=$E/runs/$cfg
    grep -E '^CHECK' $R/out.txt > $E/check-$cfg.txt; grep -vE '^CHECK' $R/out.txt | cat - $R/err.txt > $E/check-$cfg.err.txt
    local same=DIFFERENT; cmp -s $E/check-interp.txt $E/check-$cfg.txt && same=identical
    printf '   %-14s rc=%s, %s CHECK lines %s to the interpreter'"'"'s; AOT NOT FOUND (Lib): %s; image found: %s\n' $cfg "$(cat $R/rc.txt)" \
      "$(grep -c '^CHECK ' $E/check-$cfg.txt)" $same "$(grep 'NOT FOUND' $E/check-$cfg.err.txt | grep -c 'UnityLike\|GlobalNs')" \
      "$(grep -oE "(image '[^']*' found|Found statically linked AOT module '[^']*')" $E/check-$cfg.err.txt | sed -E "s/.*'([^']*)'.*/\1/" | xargs -n1 basename 2>/dev/null | grep -v CoreLib | sort -u | tr '\n' ' ')"
  done
}

rounds() {
  local n=$1 k cfg; mkdir -p $E/bench
  for ((k = 1; k <= n; k++)); do
    for cfg in $TIMING_CFGS; do
      run $cfg bench 200000; cp $E/runs/$cfg/out.txt $E/bench/r$k-$cfg.txt
      [[ $(cat $E/runs/$cfg/rc.txt) == 0 ]] || { echo "r$k $cfg rc=$(cat $E/runs/$cfg/rc.txt)"; tail -5 $E/runs/$cfg/err.txt; }
    done
    echo "   round $k done ($(sysctl -n vm.loadavg))"
  done
  python3 $HERE/bench_summary.py $E/bench $TIMING_CFGS | tee $E/bench-summary.md
}

profile() {
  [[ -f $PROFDIR/libmono-profiler-gameaotprof.dylib ]] || zsh $HERE/../aotprof/build.sh || return 1
  mkdir -p $E/prof
  # training run: App AOT'd (the engine-AOT configuration), Lib interpreted with the interpreter's inliner off, so that every
  # Lib method App calls is its own interpreted frame and is counted; methods called >= 100 times go into the profile
  RUN_ENV=(DYLD_LIBRARY_PATH=$PROFDIR) MONO_OPTS_EXTRA="--interp=-inline --profile=gameaotprof:out=$E/prof/lib,images=Lib,min=100" run app train
  cat $E/runs/app/out.txt; grep gameaotprof $E/runs/app/err.txt
  # the same without the value-type closure (vt=0): the unbox-trampoline hazard experiment
  RUN_ENV=(DYLD_LIBRARY_PATH=$PROFDIR) MONO_OPTS_EXTRA="--interp=-inline --profile=gameaotprof:out=$E/prof/lib-novt,images=Lib,min=100,vt=0" run app train
  grep gameaotprof $E/runs/app/err.txt
  python3 $HERE/../aotprof/aotprofile.py dump $E/prof/lib.aotprofile > $E/prof/lib.dump.txt; cat $E/prof/lib.dump.txt
  python3 $HERE/../aotprof/aotprofile.py empty $E/prof/empty.aotprofile
}

guid() {
  local R
  gmsg() { grep -h -m1 -E 'Failed to load AOT module|is unusable' $1/out.txt $1/err.txt | sed -E "s|${SHEPHERD_WORK:-$HOME/shepherd-work}/gameaot/mech/||g" | cut -c1-260; }
  echo "-- G1: Lib.dll replaced by variant 2 (new MVID), Lib's AOT image still the variant-1 one (dynamic)"
  LIB_DLL=$E/il-v2/Lib.dll run app+libstale check; R=$E/runs/app+libstale
  echo "   rc=$(cat $R/rc.txt); $(grep -c '^CHECK ' $R/out.txt) CHECK lines; $(gmsg $R)"
  cat $R/out.txt $R/err.txt > $E/guid-g1.log
  echo "-- G2: Lib.dll variant 2, no Lib AOT image; App's AOT image compiled against variant 1 (dynamic)"
  LIB_DLL=$E/il-v2/Lib.dll run app check; R=$E/runs/app
  echo "   rc=$(cat $R/rc.txt); $(grep -c '^CHECK ' $R/out.txt) CHECK lines; $(gmsg $R)"
  cat $R/out.txt $R/err.txt > $E/guid-g2.log
  echo "-- G3: as G2 with --aot-lazy-assembly-load"
  LIB_DLL=$E/il-v2/Lib.dll MONO_OPTS_EXTRA=--aot-lazy-assembly-load RUN_ENV=(MONO_LOG_LEVEL=debug MONO_LOG_MASK=aot) run app check; R=$E/runs/app
  echo "   rc=$(cat $R/rc.txt); $(grep -c '^CHECK ' $R/out.txt) CHECK lines; $(gmsg $R); CHECK diff vs interp (variant 1): $(diff <(grep '^CHECK' $R/out.txt) $E/check-interp.txt | grep -c '^<') line(s)"
  cat $R/out.txt $R/err.txt > $E/guid-g3.log
  echo "-- G4: static App + Lib images, Lib.dll variant 2"
  LIB_DLL=$E/il-v2/Lib.dll run s-app+lib check; R=$E/runs/s-app+lib
  echo "   rc=$(cat $R/rc.txt); $(grep -c '^CHECK ' $R/out.txt) CHECK lines; $(gmsg $R)"
  cat $R/out.txt $R/err.txt > $E/guid-g4.log
}

# The fork fixes (vita/gameaot/fork-partial-aot.patch) on the Mac: a clone of the fork with the patch applied, built as an
# osx-arm64 mono-sgen (no LLVM) -- the AOT compiler AND, as the classic mono driver, the runtime. The unpatched fork build
# (obj/mono-osx-llvm, the macbench compiler) is the baseline for both roles. Dynamic images only (the static path differs
# only in how the module is found; the experiments above show the same outcomes static and dynamic).
forkrun() {   # forkrun <runtime U|P> <app image dir> <lib image dir|-> <name>
  local rt=$MS; [[ $1 == P ]] && rt=$FIXC
  local R=$E/runs/fork-$4; rm -rf $R; mkdir -p $R
  cp $E/il/App.dll $E/il/Lib.dll $R/; cp $2/App.dll.dylib $R/; [[ $3 != - ]] && cp $3/Lib.dll.dylib $R/
  (cd $R && DYLD_LIBRARY_PATH=$PK/native MONO_PATH=$E/bcl:$R MONO_LOG_LEVEL=debug MONO_LOG_MASK=aot $rt --full-aot-interp $R/App.dll check > $R/out.txt 2> $R/err.txt); echo $? > $R/rc.txt
  grep -E '^CHECK' $R/out.txt > $R/check.txt
  local same=DIFFERENT; cmp -s $E/check-interp.txt $R/check.txt && same=identical
  printf '   %-34s rc=%s, %2s CHECK lines %s; struct-hash-disp=%s; %s\n' "$4" "$(cat $R/rc.txt)" "$(grep -c . $R/check.txt)" $same \
    "$(grep -m1 struct-hash-disp $R/check.txt | awk '{print $3}')" "$(grep -m1 -oE 'Attempting to JIT compile method .\(wrapper other\) [^(]*gsharedvt_out_sig' $R/err.txt $R/out.txt | head -1 | sed 's/.*Attempting/abort: Attempting/')"
}
forkfix() {
  [[ -x $FIXC ]] || { echo "no $FIXC (build the patched clone, see mechanics.md)"; return 1; }
  local W=$E/aot-fix; mkdir -p $W
  local save=$MS; MS=$FIXC
  local sE=$E; E=$E   # aot1 writes $E/aot/<variant>: use variant names with a fix- prefix
  aot1 fix-app-allow App 0 "" SHEPHERD_AOT_INLINE_IMAGES=$ALLOW || { MS=$save; return 1; }
  aot1 fix-lib-full Lib 0 "" || { MS=$save; return 1; }
  aot1 fix-lib-part-novt Lib 0 ",profile=$E/prof/lib-novt.aotprofile,profile-only" || { MS=$save; return 1; }
  aot1 fix-lib-part Lib 0 ",profile=$E/prof/lib.aotprofile,profile-only" || { MS=$save; return 1; }
  aot1 fix-lib-part-empty Lib 0 ",profile=$E/prof/empty.aotprofile,profile-only" || { MS=$save; return 1; }
  MS=$save
  local A=$E/aot
  echo "-- inertness: the patched compiler's FULL images vs the unpatched compiler's (text symbols)"
  for a in app-allow:App lib-full:Lib; do local v=${a%%:*} n=${a##*:}
    nm $A/$v/$n.dll.dylib | awk '$2 ~ /^[Tt]$/ {print $3}' | sort > $W/$v.unpatched.syms
    nm $A/fix-$v/$n.dll.dylib | awk '$2 ~ /^[Tt]$/ {print $3}' | sort > $W/$v.patched.syms
    printf '   %-10s symbols %s; __text %s vs %s B\n' $v "$(cmp -s $W/$v.unpatched.syms $W/$v.patched.syms && echo identical || echo DIFFERENT)" \
      "$(size -m $A/$v/$n.dll.dylib | awk '/Section __text/ {print $3; exit}')" "$(size -m $A/fix-$v/$n.dll.dylib | awk '/Section __text/ {print $3; exit}')"
  done
  echo "-- runs (U = unpatched fork runtime, P = patched; compiler: unpatched / patched)"
  forkrun U $A/app-allow $A/lib-full          "U, full Lib (baseline)"
  forkrun U $A/app-allow $A/lib-part-novt     "U, partial, unpatched compiler"
  forkrun P $A/app-allow $A/lib-part-novt     "P, partial, unpatched compiler"
  forkrun U $A/fix-app-allow $A/fix-lib-part-novt "U, partial, patched compiler"
  forkrun P $A/fix-app-allow $A/fix-lib-part-novt "P, partial, patched compiler"
  forkrun U $A/fix-app-allow $A/fix-lib-part  "U, partial+vt closure, patched compiler"
  forkrun P $A/fix-app-allow $A/fix-lib-full  "P, full Lib, patched compiler"
  forkrun P $A/fix-app-allow -                "P, Lib interpreted"
  echo "-- SHEPHERD_AOT_NO_GSHAREDVT=1 (fix C: no gsharedvt bodies in the owner-local module; patched compiler)"
  MS=$FIXC
  aot1 fix-lib-full-nogsvt Lib 0 "" SHEPHERD_AOT_NO_GSHAREDVT=1 || { MS=$save; return 1; }
  aot1 fix-lib-part-nogsvt Lib 0 ",profile=$E/prof/lib-novt.aotprofile,profile-only" SHEPHERD_AOT_NO_GSHAREDVT=1 || { MS=$save; return 1; }
  aot1 fix-lib-part-strict Lib 0 ",profile=$E/prof/lib-novt.aotprofile,profile-only" SHEPHERD_AOT_NO_GSHAREDVT=1 SHEPHERD_AOT_PROFILE_STRICT=1 || { MS=$save; return 1; }
  aot1 fix-lib-empty-strict Lib 0 ",profile=$E/prof/empty.aotprofile,profile-only" SHEPHERD_AOT_NO_GSHAREDVT=1 SHEPHERD_AOT_PROFILE_STRICT=1 || { MS=$save; return 1; }
  MS=$save
  forkrun P $A/fix-app-allow $A/fix-lib-part-strict "P, partial Lib, strict + no gsharedvt"
  forkrun P $A/fix-app-allow $A/fix-lib-empty-strict "P, empty profile, strict + no gsharedvt"
  forkrun P $A/fix-app-allow $A/fix-lib-full-nogsvt "P, full Lib, no gsharedvt"
  forkrun P $A/fix-app-allow $A/fix-lib-part-nogsvt "P, partial Lib, no gsharedvt"
  forkrun U $A/fix-app-allow $A/fix-lib-part-nogsvt "U, partial Lib (no vt closure), no gsharedvt"
}

case $1 in
  build) build ;; bcl) bcl ;; profile) profile ;; aot-base) aot_base ;; aot-part) aot_part ;; hosts) hosts ;;
  run) shift; run "$@"; cat $E/runs/$1/out.txt; echo "rc=$(cat $E/runs/$1/rc.txt)" ;;
  checks) checks ;; rounds) rounds ${2:-6} ;; guid) guid ;; forkfix) forkfix ;;
  *) sed -n '2,20p' $0; exit 2 ;;
esac
