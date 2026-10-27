#!/bin/zsh
# kit #7 tests track (vita/notes/kit7-tests.md "Seeded thread-schedule testing"): real Mac boots with the engine's own threads under the PCT
# scheduler (vita/tests/TestHost Pct.cs: the multicore render pipeline's render thread + the pack read-ahead worker + the main thread),
# one seed per boot, each compared frame by frame (state + render stream hashes) with the single-threaded reference boot
# (SHEPHERD_MT_RENDER=0, read-ahead off). Required: every seed = the reference on every frame, no deadlock, no stall.
#   boot-seeds.sh <route: idle|newgame> <first seed> <count> [depth]   -> $TW/pct/boot-<route>/summary.txt
setopt pipe_fail
T="${0:A:h}"; TESTS="${T:h}"; TW=${TESTS_WORK:-${SHEPHERD_WORK:-$HOME/shepherd-work}/kit7/tests-work}
route=${1:-idle}; first=${2:-1}; n=${3:-16}; d=${4:-3}; P=${P:-4}
O=$TW/pct/boot-$route; mkdir -p $O
case $route in
  idle) FR=1500; ARGS=() ;;
  newgame) FR=3000; ARGS=(PAD=$TESTS/fixtures/pads/vlx-intro.txt SHEPHERD_PACK=${SHEPHERD_WORK:-$HOME/shepherd-work}/kit6/kit-build/owner/shepherd/pack) ;;
esac
DET=(SHEPHERD_TEST_ASTAR=join SHEPHERD_TEST_SPINE=join)
[[ -f $O/ref/state-hash.txt ]] || zsh $TESTS/macboot.sh $O/ref $FR $ARGS $DET SHEPHERD_MT_RENDER=0 > /dev/null
typeset -a pids
for s in $(seq $first $((first + n - 1))); do
  while (( ${#pids} >= P )); do a=(); for p in $pids; do kill -0 $p 2>/dev/null && a+=($p); done; pids=($a); (( ${#pids} >= P )) && sleep 1; done
  zsh $TESTS/macboot.sh $O/s$s $FR HOST=1 $ARGS $DET SHEPHERD_MT_RENDER=1 SHEPHERD_PACK_READAHEAD=1 SHEPHERD_TESTHOST_PCT=$s:$d:$((FR * 3)) SHEPHERD_TESTHOST_PCT_OUT=$O/s$s.pct > /dev/null 2>&1 & pids+=($!)
done
wait
ok=0; bad=0; : > $O/summary.txt
for s in $(seq $first $((first + n - 1))); do
  same=1; cmp -s $O/ref/state-hash.txt $O/s$s/state-hash.txt || same=0; cmp -s $O/ref/stream-hash.txt $O/s$s/stream-hash.txt || same=0
  pct=$(cat $O/s$s.pct 2>/dev/null | head -1)
  [[ $pct == *FAIL* ]] && same=0
  (( same )) && ok=$((ok+1)) || bad=$((bad+1))
  echo "seed $s: $( ((same)) && echo "= the single-threaded run on every frame" || echo "DIFFERS") | $pct" >> $O/summary.txt
  (( same )) && rm -rf $O/s$s
done
echo "RESULT: $ok of $n seeds identical to the single-threaded reference, $bad differ / fail (route $route, d $d)" >> $O/summary.txt
tail -1 $O/summary.txt
