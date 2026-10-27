#!/bin/zsh
# trampcount.sh <config> <scenario> <out tsv>: one bench.sh-shaped run of the engine under lldb, counting CoreLib trampoline pool
# entries per type (trampcount.py). Slow (a breakpoint per entry); for pool sizing only, never for timing.
D=${0:A:h}; P=${SHEPHERD_WORK:-$HOME/shepherd-work}/gameaot/proto
export TMPDIR=${SHEPHERD_WORK:-$HOME/shepherd-work}/tmp
c=$1 sc=$2 out=$3
cmds=$TMPDIR/trampcount-$$.lldb
cat > $cmds <<EOC
command script import $D/trampcount.py
settings set target.process.stop-on-exec false
process handle -s false -n false -p true SIGUSR1 SIGUSR2 SIGXCPU SIGPWR SIGSEGV SIGBUS SIGPIPE SIGALRM SIGVTALRM SIGPROF SIGCHLD SIGURG
breakpoint set -s libcoreclr.dylib -n get_numerous_trampoline
breakpoint command add -F trampcount.hit 1
run
trampdump
quit
EOC
TRAMPCOUNT_OUT=$out BENCH_WRAP="lldb -b -s $cmds --" zsh $D/../bench.sh run $c $sc tc-$c-$sc
rm -f $cmds; cat $out
