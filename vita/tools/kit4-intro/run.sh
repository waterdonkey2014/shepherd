#!/bin/zsh
# kit4 intro-plan runner: one engine run in its own sandbox root (Managed/ManagedVita symlinks, the worktree's runtime/derived,
# a fresh copy of the worktree's sandbox saves); logs + shots -> intro-work/runs/<name>/. Silent: Vita profile (FMOD stubs), SHEPHERD_AUDIO=off.
# usage: run.sh <name> <frames> <shotEvery> [KEY=VAL ...]     BIN=<dir> (default worktree engine bin = Debug; binrel = Release)
set -u
name=$1 frames=$2 every=$3; shift 3
W=${SHEPHERD_WORK:-$HOME/shepherd-work}/kit4/intro; K=${SHEPHERD_WORK:-$HOME/shepherd-work}/kit4/intro-work
export TMPDIR=${SHEPHERD_WORK:-$HOME/shepherd-work}/tmp DOTNET_ROOT=$HOME/.dotnet PATH="$HOME/.dotnet:$PATH" DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
RT=$K/roots/$name; rm -rf $RT; mkdir -p $RT/runtime/logs $RT/runtime/shots
ln -s $W/runtime/Managed $RT/runtime/Managed; ln -s $W/runtime/ManagedVita $RT/runtime/ManagedVita; ln -s $W/runtime/derived $RT/runtime/derived
ln -s $W/runtime/shaders $RT/runtime/shaders
cp -R $W/runtime/persistent $RT/runtime/persistent
unset SHEPHERD_PACK SHEPHERD_PERSISTENT SHEPHERD_PROFILE SHEPHERD_RENDER
export SHEPHERD_ROOT=$RT SHEPHERD_AUDIO=off SHEPHERD_PROFILE=vita SHEPHERD_RENDER=${RENDER:-gl} SHEPHERD_GL_DXT=1
for kv in "$@"; do export "$kv"; done
out=$K/runs/$name; rm -rf $out; mkdir -p $out
BIN=${BIN:-$W/engine/Shepherd.Engine/bin}
{ echo "BIN=$BIN"; env | grep '^SHEPHERD_' | sort; } > $out/env.txt
osascript -e 'output muted of (get volume settings)' > $out/muted-before.txt 2>&1
( cd $W/engine/Shepherd.Engine && /usr/bin/time -l dotnet $BIN/Shepherd.Engine.dll boot $frames $every > $out/console.txt 2> $out/stderr.txt ); echo "rc=$?" >> $out/stderr.txt
osascript -e 'output muted of (get volume settings)' > $out/muted-after.txt 2>&1
mv $RT/runtime/logs $out/logs; mv $RT/runtime/shots $out/shots; cp -R $RT/runtime/persistent $out/persistent-after
rm -rf $RT
echo "$name: $(tail -1 $out/logs/engine.log | sed 's/^\[ *[0-9]*\] *//') EXC=$(grep -c ' EXC ' $out/logs/engine.log) ERR=$(grep -c ' ERR ' $out/logs/engine.log) MISSING=$(grep -c 'MISSING from pack' $out/logs/engine.log)"
