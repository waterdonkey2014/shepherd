#!/bin/zsh
# game-aot PROTOTYPE: the owner-local Vita variant (eboot + owner-local VPK, TITLE_ID SHPD00012), built by vita/shepherd/build.sh
# WITH vita/gameaot/build-gameaot.patch applied to a COPY of it (the repo's build.sh is never modified): a private tree
# $O/tree = this worktree's vita/ and engine/ by symlink, vita/shepherd copied and patched, runtime/ -> the main checkout's
# git-ignored runtime/ (read only: build.sh's --forbid checks). Everything it writes is under $O = ${SHEPHERD_WORK:-$HOME/shepherd-work}/gameaot/
# vita-out (compiled game / Unity code: never in git, never a shareable VPK). Nothing runs on a Vita.
#   vita.sh tree                     (re)create the private tree
#   vita.sh owner                    the owner managed folder (stage-owner.sh's layout, from the prototype's snapshot)
#   vita.sh build <variant>          variant P: every owner module partial; C: the 27 rewritten Unity modules whole + the game partial;
#                                    B: the 27 rewritten Unity modules whole only;
#                                    CORE: UnityEngine.CoreModule whole + the rest partial; NONE: no owner module (inertness check)
#   vita.sh gates <variant>          the checks build.sh does not do (see below)
# Partial modules: the prototype's profile WITHOUT the value-type closure (nocl.aotprofile) + the runtime with mechanics' fix A
# (obj/vita-runtime-fix): owner-local only. VITA_CLOSURE=1: the closure profile (part.aotprofile) + the stock runtime instead.
setopt err_exit pipe_fail
D=${0:A:h}; WT=${D:h:h:h}; G=${SHEPHERD_WORK:-$HOME/shepherd-work}/gameaot; P=$G/proto; O=$G/vita-out
export TMPDIR=${SHEPHERD_WORK:-$HOME/shepherd-work}/tmp
MAIN=${SHEPHERD_REPO:-$HOME/src/shepherd}
UNITY=(${(f)"$(<$D/modules-rewritten.txt)"}); GAME=(${(f)"$(<$D/modules-game.txt)"}); OWNER=($UNITY $GAME)

tree() {
  local T=$O/tree; rm -rf $T; mkdir -p $T/vita
  for e in $WT/vita/*(N); do [[ ${e:t} == shepherd ]] && continue; ln -s $e $T/vita/${e:t}; done
  cp -R $WT/vita/shepherd $T/vita/shepherd; rm -rf $T/vita/shepherd/out
  (cd $T && patch -p1 --quiet < $WT/vita/gameaot/build-gameaot.patch)
  ln -s $WT/engine $T/engine; ln -s "$MAIN/runtime" $T/runtime
  cmp -s $WT/vita/shepherd/build.sh $T/vita/shepherd/build.sh && { echo "patch not applied?"; return 1; }
  echo "tree: $T (build.sh patched: $(diff $WT/vita/shepherd/build.sh $T/vita/shepherd/build.sh | grep -c '^>') lines added)"
}

owner() {   # stage-owner.sh's managed layout: the game's Managed minus the BCL names + runtime/ManagedVita + Shepherd.Engine.Derived
  local M=$O/owner/managed REF=$G/sizes/inputs/owner-managed; rm -rf $M; mkdir -p $M
  for f in $REF/*.dll; do cp -c $P/snap/Data/Managed/${f:t} $M/ || { echo "missing ${f:t} in the snapshot"; return 1; }; done
  (cd $M && shasum -a 256 *.dll) > $O/owner/managed.sha256
  echo "owner managed: $(ls $M/*.dll | wc -l | tr -d ' ') DLLs (the prototype's Mac snapshot; same bytes the Mac proxy ran)"
}

build() {
  local v=$1 W=$O/work-$1 OUT=$O/out-$1; local -a full part
  case $v in
    P) part=($OWNER) ;;
    C) full=($UNITY); part=($GAME) ;;
    B) full=($UNITY) ;;
    CORE) full=(UnityEngine.CoreModule); part=(${OWNER:#UnityEngine.CoreModule}) ;;
    NONE) ;;
    *) echo "variant P|C|B|CORE|NONE"; return 2 ;;
  esac
  mkdir -p $O/lists; print -l $full > $O/lists/$v-full.txt; print -l $part > $O/lists/$v-part.txt
  local prof=$P/prof/nocl.aotprofile rt=$G/obj/vita-runtime-fix
  [[ -n ${VITA_CLOSURE:-} ]] && { prof=$P/prof/part.aotprofile rt=${SHEPHERD_WORK:-$HOME/shepherd-work}/obj/vita-runtime; }
  local -a envs=(SHEPHERD_AOT_ENGINE=1 SHEPHERD_OWNER_MANAGED=$O/owner/managed SHEPHERD_CROSS_OBJ=$P/obj/cross-vita
                 SHEPHERD_VITA_RUNTIME_OBJ=$rt SHEPHERD_V6_WORK=$W SHEPHERD_V6_OUT=$OUT)
  [[ $v != NONE ]] && envs+=(SHEPHERD_AOT_OWNER=1 SHEPHERD_AOT_OWNER_FULL=$O/lists/$v-full.txt SHEPHERD_AOT_OWNER_PART=$O/lists/$v-part.txt
                             SHEPHERD_AOT_OWNER_PROFILE=$prof)
  print -l $envs > $O/build-$v.env
  local t0=$(date +%s)
  env $envs nice -n 10 zsh $O/tree/vita/shepherd/build.sh > $O/build-$v.log 2>&1; local rc=$?
  echo "build $v: rc=$rc, $(( $(date +%s) - t0 )) s (log $O/build-$v.log)"; tail -12 $O/build-$v.log
  return $rc
}

gates() {   # gates <variant>
  setopt local_options no_err_exit no_pipe_fail
  local v=$1 W=$O/work-$1 OUT=$O/out-$1 R=$O/gates-$1.txt
  {
    echo "# gates for the owner-local game-AOT variant $v ($(date -u +%Y-%m-%dT%H:%MZ))"
    echo "## build.sh's own checks: $(grep -c '^PASS\|PASS' $OUT/checks.txt) PASS lines; $(grep -ciE 'FAIL' $OUT/checks.txt) FAIL"
    echo "## packaged-set AOT GUID gate: $(tail -1 $OUT/aot-guids.txt)"
    [[ -f $OUT/aot-guids-owner.txt ]] && echo "## owner AOT GUID gate: $(tail -1 $OUT/aot-guids-owner.txt)"
    echo "## engine foreign-code gate (build.sh, A32): $(cat $OUT/engine-aot.txt)"
    echo "## engine foreign-code gate (vita/llvm/foreign-gate.sh, self-test first):"
    (cd $WT && zsh vita/llvm/foreign-gate.sh --self-test 2>&1 | tail -2)
    (cd $WT && zsh vita/llvm/foreign-gate.sh Shepherd.Engine $W/aot/Shepherd.Engine.dll.o - $W/obj/foreign-gate 2>&1 | tail -3)
    echo "## the VPK holds no owner file / no owner AOT image (owner modules are compiled INTO the eboot only):"
    unzip -l $OUT/*.vpk | awk 'NR>3 {print $4}' | grep -E '\.dll$' | sed 's#.*/##; s#\.dll$##' > $W/obj/vpk-dlls.txt
    echo "   packaged DLLs: $(wc -l < $W/obj/vpk-dlls.txt | tr -d ' '); owner modules among them: $(grep -cxF -f <(print -l $OWNER) $W/obj/vpk-dlls.txt || true)"
    echo "## registered AOT modules (shepherd_tables.c): $(grep -c 'mono_aot_register_module (mono_aot_module' $W/obj/shepherd_tables.c) = 13 engine-AOT set + $(( $(grep -c . $O/lists/$v-full.txt 2>/dev/null) + $(grep -c . $O/lists/$v-part.txt 2>/dev/null) )) owner"
    echo "## the shareable default (SHPD00006) set, for comparison: vita/shepherd/build.sh AOT=(12 modules), unchanged in the repo: git diff --stat vita/shepherd/build.sh = '$(git -C $WT diff --stat -- vita/shepherd/build.sh | tail -1)'"
    echo "## CoreLib trampoline pools: $(grep -o 'ntrampolines=[0-9]*[^ ]*' $W/aot/System.Private.CoreLib.stamp | head -1)"
    echo "## sizes"; cat $OUT/elf-size.txt; cat $OUT/build-info.txt
  } > $R 2>&1
  cat $R
}

case $1 in
  tree) tree ;;
  owner) owner ;;
  build) build $2 ;;
  gates) gates $2 ;;
  *) sed -n '2,16p' $0; exit 2 ;;
esac
