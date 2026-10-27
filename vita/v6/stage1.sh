#!/bin/zsh
# V6 stage 1 (vita/notes/engine-pass-memory.md): the measurement matrix of the memory cuts on the vita-sim boot (vita/v6/sim.sh).
# Every config is the full stage-1 engine with ONE part switched off (leave-one-out), plus the all-off configuration on the
# pre-stage-1 pack ("base"); each gets a memory run (live heap after the forced GC at f1300 + vmmap malloc), and the configs that
# move the GC heap get a heap-cap grid. Runs go 5 at a time in their own roots (sim.sh SIM_ROOT); the timing rounds are separate
# (sim.sh run, alone). Results: runtime/logs/v6/s1m-<config>-{mem,census,capgrid.txt}; summary: python3 vita/v6/tools/stage1_table.py
# usage: stage1.sh [mem|census|cap|all] [config]   (S1_SKIP_DONE=1: skip runs whose logs exist; SIM_JOBS: parallel runs, default 5)
setopt pipe_fail
V6="${0:A:h}"; REPO="${V6:h:h}"; R="$REPO/runtime"
export TMPDIR=${SHEPHERD_WORK:-$HOME/shepherd-work}/tmp
SIM=${SHEPHERD_SIM:-${SHEPHERD_WORK:-$HOME/shepherd-work}/v6/sim}
PRE_PACK=${S1_PRE_PACK:-${SHEPHERD_WORK:-$HOME/shepherd-work}/s1/pack-pre}     # copy of runtime/vitapack/menu before stage 1 (derived data: build volume only)
NEW_PACK="$R/vitapack/menu"; LANG_PACK="$R/vitapack/menu-langonly"; JS_PACK="$R/vitapack/menu-jsonly"   # (--keep-joystick-maps) / (--language all)
ALL_OFF=nosaves,nonewtonsoft,noscans,nobuiltin,noi2,noboxes,nokeys,noatlas,nolazytext,noalpha8
# name | pack | SHEPHERD_VITA_MEM | caps to probe (MiB; empty = none) | census (Mono --stats + interp census)? | extra env
# (base's caps: the earlier s1-base grid, runtime/logs/v6/s1-base-capgrid.txt: 192 crash, 194 pass, 196 one OOM, 200 pass)
CONFIGS=(
  "base|$PRE_PACK|$ALL_OFF||census"
  "all|$NEW_PACK||105 110 115 120 125 130 135|census"
  "-saves|$NEW_PACK|nosaves|125 130 135 140|census"
  "-language|$JS_PACK|noi2|115 120 125 130 135|"
  "-languagepack|$JS_PACK||115 120 125 130 135|||SHEPHERD_LANGUAGE=en"
  "-packtrims|$PRE_PACK||115 120 125 130 135 140|"
  "-joystickmaps|$LANG_PACK||115 120 125|"
  "-newtonsoft|$NEW_PACK|nonewtonsoft||census"
  "-scans|$NEW_PACK|noscans||census"
  "-builtin|$NEW_PACK|nobuiltin||census"
  "-boxes|$NEW_PACK|noboxes|115 120 125|"
  "-keys|$NEW_PACK|nokeys|115 120 125|"
  "-atlas|$NEW_PACK|noatlas|115 120 125 130|"
  "-lazytext|$NEW_PACK|nolazytext|115 120 125 130|"
  "-alpha8|$NEW_PACK|noalpha8|115 120 125|"
)
JOBS=${SIM_JOBS:-5}
zmodload zsh/parameter
pool() { while (( ${#jobstates} >= JOBS )); do sleep 1; done; (eval "$1" > /dev/null 2>&1) & }
mode=${1:-all}; only=${2:-}   # stage1.sh <mem|census|cap|all> [config name]: one config only
for c in $CONFIGS; do
  IFS='|' read name pack off caps cen _ env <<< "$c"
  [[ -n $only && $name != $only ]] && continue
  tag="s1m${name}"
  [[ ($mode == mem || $mode == all) && ( -z $S1_SKIP_DONE || ! -f $R/logs/v6/$tag-mem/vmmap-summary.txt ) ]] &&
    pool "SIM_ROOT=$SIM/root-$tag-mem SIM_PACK='$pack' zsh '$V6/sim.sh' mem $tag-mem SHEPHERD_VITA_MEM=$off $env"
  [[ ($mode == census || $mode == all) && $cen == census && ( -z $S1_SKIP_DONE || ! -f $R/logs/v6/$tag-census/interp-methods.tsv ) ]] &&
    pool "SIM_ROOT=$SIM/root-$tag-cen SIM_PACK='$pack' zsh '$V6/sim.sh' census $tag-census SHEPHERD_VITA_MEM=$off $env"
  if [[ $mode == cap || $mode == all ]]; then
    for m in ${=caps}; do
      [[ -n $S1_SKIP_DONE && -f $R/logs/v6/$tag-cap$m/wall.txt ]] && continue
      pool "SIM_PACK='$pack' zsh '$V6/sim.sh' cap $tag-cap$m $m SHEPHERD_VITA_MEM=$off $env"
    done
  fi
done
wait
# cap tables from the per-run logs (sim.sh cap_verdict re-reads them)
for c in $CONFIGS; do
  IFS='|' read name pack off caps cen _ env <<< "$c"; tag="s1m${name}"; t="$R/logs/v6/$tag-capgrid.txt"; : > "$t"
  for d in "$R"/logs/v6/$tag-cap<->(N/); do zsh "$V6/sim.sh" verdict "$d" ${${d:t}#$tag-cap} >> "$t"; done
  sort -k3 -n "$t" -o "$t"
done
python3 "$V6/tools/stage1_table.py"
