#!/bin/zsh
# SHEPHERD-VITA game-AOT study: aot-module.sh over a module list, a few at a time (the Mac is shared: niced, GA_JOBS default 2).
# usage: aot-all.sh <variant> <list file: one assembly name per line, '#' comments> [module ...]
# Output (owner-local): $GA_OUT/aot-<variant>/; a module that fails is reported and the rest continue.
set -uo pipefail
HERE=${0:A:h}; VAR=$1; LIST=$2; shift 2
MODS=($(grep -v '^#' $LIST | awk 'NF {print $1}'))
(( $# )) && MODS=("$@")
print -l -- $MODS | xargs -P ${GA_JOBS:-2} -I{} nice -n 10 zsh $HERE/aot-module.sh $VAR {}
true
