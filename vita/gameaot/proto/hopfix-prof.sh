#!/bin/zsh
# game-aot PROTOTYPE: the hot study's exact-count profiles (vita/gameaot/tools/hotprof + interp-census; hot.sh's prof / profni modes)
# of the hop-fix engine (config HI: the menu-speed branch's engine, interpreted), so hopmodel.py can count the crossings of the
# H / HP / HC configs. -> $P/hopfix-prof/{r1-<sc>-prof, n1-<sc>-profni} (owner-local: method names of the game).
D=${0:A:h}; G=${SHEPHERD_WORK:-$HOME/shepherd-work}/gameaot; P=$G/proto; H=$G/hot
zsh $D/bench.sh cfg HI || exit 1
mkdir -p $P/hopfix-prof
for sc in idle tour intro; do for m in prof profni; do
  local name=hfp-$sc-$m out=$P/runs/hfp-$sc-$m
  local mo="--profile=shepherdhot:$out/hotprof.tsv --profile=shepherd:$out/census.tsv"; [[ $m == profni ]] && mo="--interp=-inline $mo"
  local -a seg=(); [[ $sc == intro ]] && seg=(SHEPHERD_HOT_SEGMENTS=806,1786,2935)
  MONO_EXTRA="$mo" zsh $D/bench.sh run HI $sc $name DYLD_LIBRARY_PATH=$H/tools/hotprof:$H/tools/census $seg || exit 1
  ln -sfn $out $P/hopfix-prof/$([[ $m == prof ]] && echo r1 || echo n1)-$sc-$m
done; done
ls -la $P/hopfix-prof
