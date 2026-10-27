#!/bin/zsh
# macrun.sh <name> <bin dir> [ENV=VAL ...] -- <engine args>: the default Mac boot (CoreCLR) in its own root under $MACRUN_ROOTS/<name>
# (default ${SHEPHERD_WORK:-$HOME/shepherd-work}/s2/roots; V6 stage 2, vita/notes/engine-pass-speed.md)
# (runtime/{Managed,ManagedVita,vitapack} symlinked to the repo, persistent/ a fresh copy) -> logs + shots in that root.
export TMPDIR=${SHEPHERD_WORK:-$HOME/shepherd-work}/tmp DOTNET_ROOT=$HOME/.dotnet PATH="$HOME/.dotnet:$PATH" DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
R="${SHEPHERD_REPO:-$HOME/src/shepherd}/runtime"; name=$1 bin=$2; shift 2
root=${MACRUN_ROOTS:-${SHEPHERD_WORK:-$HOME/shepherd-work}/s2/roots}/$name; rm -rf $root; mkdir -p $root/runtime/logs $root/runtime/shots
for d in Managed ManagedVita vitapack shaders audiobake; do ln -s "$R/$d" $root/runtime/$d; done
cp -R "$R/persistent" $root/runtime/persistent
envs=(); while (( $# )) && [[ "$1" != "--" ]]; do envs+=("$1"); shift; done; shift
(cd $root && env SHEPHERD_ROOT=$root $envs dotnet "$bin/Shepherd.Engine.dll" "$@" > $root/console.txt 2>&1); rc=$?
echo "$name rc=$rc $(tail -1 $root/runtime/logs/engine.log | sed 's/^\[ *[0-9]*\] *//')"
