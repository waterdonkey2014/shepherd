#!/bin/zsh
# Kit #13 looks: the Mac shots for the PC-capture picture check (vita/tools/piccheck.py, vita/tools/piccheck-screens.tsv) -- one arm = one engine
# tree + one env set; the routes are the manifest's: title (no input, shot at frame 1040) and menu (X at the title, shot at frame 1300), Vita
# profile, GL + DXT, the strict menu pack, frame clock, silent (output muted; refuses to run unmuted), a sandbox save copy per run.
# usage: piccheck-mac.sh <tree> <out dir> [ENV=VAL ...]      (the tree: a worktree with its Debug engine and runtime/ as the Mac snapshots have)
# then:  piccheck.py run vita/tools/piccheck-screens.tsv --pc-dir <the owner's PC captures> --arm <name>=<out dir> [...] --out <dir> --report <file>
# env:   PICCHECK_TMP (temp files; default <out dir>/tmp -- never the system disk's /tmp)
S=${1:?tree}; O=${2:?out dir}; shift 2
export TMPDIR=${PICCHECK_TMP:-$O/tmp} TMPPREFIX=${PICCHECK_TMP:-$O/tmp}/zsh SHEPHERD_TMPDIR=${PICCHECK_TMP:-$O/tmp}; mkdir -p $TMPDIR
export PATH="$HOME/.dotnet:$PATH" DOTNET_ROOT=$HOME/.dotnet DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
osascript -e 'set volume output muted true' 2>/dev/null
[[ "$(osascript -e 'output muted of (get volume settings)' 2>/dev/null)" == true ]] || { echo "NOT MUTED - refusing"; exit 9; }
bin=$S/engine/Shepherd.Engine/bin/Debug/net8.0; R=$S/runtime
[[ -f $bin/Shepherd.Engine.dll && -d $R/vitapack/menu ]] || { echo "no Debug engine / menu pack in $S"; exit 2; }
run() {   # run <name> <frames> <shot frames> <pad lines or ->
  local n=$1 frames=$2 shots=$3 pad=$4 o=$O/$1; local root=$o/root; shift 4   # (the rest: the arm's env)
  rm -rf $o; mkdir -p $o/shots $root/runtime/logs
  for d in Managed ManagedVita; do ln -s $R/$d $root/runtime/$d; done
  ln -s $R/derived $root/runtime/derived; cp -R $R/persistent $root/runtime/persistent
  local envs=(SHEPHERD_ROOT=$root SHEPHERD_PROFILE=vita SHEPHERD_AUDIO=off SHEPHERD_RENDER=gl SHEPHERD_GL_DXT=1 SHEPHERD_PACK=$R/vitapack/menu SHEPHERD_PACK_STRICT=1
    SHEPHERD_SHOT_DIR=$o/shots SHEPHERD_SHOT_FRAMES=$shots "$@")
  if [[ $pad != - ]]; then print -r -- $pad > $o/pad.txt; envs+=(SHEPHERD_PAD_FILE=$o/pad.txt); fi
  print -l -- $envs > $o/env.txt
  (cd $root && env $envs nice dotnet $bin/Shepherd.Engine.dll boot $frames 100000 > $o/console.txt 2>&1); local rc=$?
  cp $root/runtime/logs/engine.log $o/ 2>/dev/null; rm -rf $root
  echo "$n: rc=$rc, shots $(ls $o/shots | wc -l | tr -d ' ')"
}
run title 1041 1040 - "$@"
run menu 1301 1300 'm+244:cross' "$@"
osascript -e 'set volume output muted true' 2>/dev/null
