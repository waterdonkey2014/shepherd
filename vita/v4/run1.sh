#!/bin/zsh
# run1.sh <name> [ENV=VAL ...] -- <engine args...>   one engine run; logs + screenshots -> runtime/logs/v4r/<name>/
# BIN=binrel picks the Release build (default bin = Debug); EXE=<path> runs that executable instead (e.g. the Mono publish vita/v4/pubmono/Shepherd.Engine).
export TMPDIR=${SHEPHERD_TMPDIR:-${SHEPHERD_WORK:-$HOME/shepherd-work}/tmp} DOTNET_ROOT=$HOME/.dotnet PATH="$HOME/.dotnet:$PATH" DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
mkdir -p $TMPDIR
# the checkout this script lives in (the main one resolves to ${SHEPHERD_REPO:-$HOME/src/shepherd}, as before; a worktree to its own),
# and its runtime/ as the engine's root unless SHEPHERD_ROOT is given (the engine's default root is the main checkout's)
REPO="${0:A:h:h:h}"; R="$REPO/runtime"; ENG="$REPO/engine/Shepherd.Engine"; export SHEPHERD_ROOT="${SHEPHERD_ROOT:-$REPO}"
name=$1; shift; local -a envs; while [[ "$1" != "--" ]]; do envs+=$1; shift; done; shift
out="$R/logs/v4r/$name"; rm -rf "$out"; mkdir -p "$out"; rm -f "$R"/shots/boot/*.png(N)
if [[ -n "$EXE" ]]; then (cd "$ENG" && env $envs "$EXE" "$@" > "$out/console.txt" 2>&1); else (cd "$ENG" && env $envs dotnet ${BIN:-bin}/Shepherd.Engine.dll "$@" > "$out/console.txt" 2>&1); fi; rc=$?
mv "$R"/logs/memreport-1299.txt "$out/" 2>/dev/null   # only the window this tool uses (SHEPHERD_MEMREPORT=1299); older memreport files stay untouched
cp "$R/logs/engine.log" "$R/logs/missing-icalls.txt" "$out/" 2>/dev/null; cp "$R"/shots/boot/*.png(N) "$out/" 2>/dev/null
echo "$name: rc=$rc  $(tail -1 "$out/engine.log" | sed 's/^\[ *[0-9]*\] *//')  EXC=$(grep -c ' EXC ' "$out/engine.log")"
