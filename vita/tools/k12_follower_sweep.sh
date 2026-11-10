#!/bin/zsh
# Kit #12 follower sweep harness -- PREPARED in kit #11 by the measure track (vita/notes/kit11-measure.md "Follower sweep"), not run yet:
# frame times at 0 / 25 / 50 / 75 / 100 followers on a FRESH-BUILT sandbox base, on the Mac. The device leg is kit #12's: this script only
# writes the env file the device run would use; it never touches a Vita and sends nothing anywhere (results stay in <out dir>).
#   k12_follower_sweep.sh <out dir> <saves dir>
#     <saves dir>/<N>/   for N in K12_COUNTS: a sandbox persistent/ folder (the engine's save root: saves/, settings ...) holding the
#                        fresh-built base with N followers -- made by kit #12 from ONE fresh-built sandbox base (New Game on the engine,
#                        played to the base and saved; never a player's own save), each count derived from it the same way
# env: K12_COUNTS   follower counts (default "0 25 50 75 100")
#      K12_PAD      the pad file that takes the boot to the base (Main Menu -> Play -> the slot), REQUIRED (vita/tests/fixtures/pads/ style)
#      K12_FRAMES   run length (default 4000)       K12_WINDOW  the timed window <from>:<to> (default 3000:4000: after the base is up)
#      K12_PACK     the pack with the base scene (SHEPHERD_PACK; required: the base is not in the menu pack)
#      K12_ENV      extra switches for every run ("K=V ..."; default: the kit #11 counters + the stream heat map)
#      K12_RUNS     runs per count (default 3: the median is reported)      MACBOOT_BIN  the engine build (default the repo's Debug build)
# Safety: a saves dir (or a count folder in it) whose real path -- symlinks resolved, letter case ignored -- lies under a system
# application-support folder, a Steam library, the game's own save folders or the like is REFUSED; the engine always runs on a COPY of
# <saves dir>/<N> (a fresh root per run), silent (the Mac muted and checked), headless, the frame clock.
# Kit #13 basec -- HOOKS MODE (<saves dir> = the word "hooks"): no saves; every run starts from a fresh copy of the repo's sandbox persistent/
# and takes the game's own Quick Start to the base and spawns the followers with the Mac test hooks (engine/Shepherd.Engine/Diag/TestWarp.cs,
# Mac / tools builds only): count N = the Quick Start base's own follower + N-1 more (FollowerManager.CreateNewFollower at the base); N = 0
# runs the Quick Start base as it is (its one follower). env (hooks mode): K12_QS the Quick Start frame (default 1300), K12_SPAWN the spawn frame
# (default 1700), K12_PAD (required: the title press), K12_PACK optional (default: the game's own files), K12_PHYS the physics level (default
# 6, the play apps' device default; needs SHEPHERD_PHYS_LIB for levels >= 1), K12_RENDER (default null), K12_CENSUS=<frame> a heap census
# (SHEPHERD_MEMREPORT=<frame>:1:census, after the timed window) in round 1 of every count -- its 32-bit object graph and total go in the table.
# Output: <out>/<N>-r<k>/ (engine.log, simstats.txt, simstats-frames.tsv, k11/), <out>/sweep.md (the table), <out>/device-env-<N>.txt
# (the device leg's env lines: its saves in a sweep folder of their own, SHEPHERD_PERSISTENT -- never the Vita build's save folder).
setopt pipe_fail
T="${0:A:h}"; REPO="${T:h:h}"; R="$REPO/runtime"
O=${1:?out dir}; SV=${2:?saves dir}; O=${O:a}; HOOKS=0; [[ $SV == hooks ]] && HOOKS=1 || SV=${SV:A}   # (:A = absolute with symlinks resolved)
COUNTS=(${=${K12_COUNTS:-0 25 50 75 100}}); FR=${K12_FRAMES:-4000}; WIN=${K12_WINDOW:-3000:4000}; RUNS=${K12_RUNS:-3}
PAD=${K12_PAD:?K12_PAD: the pad file that takes the boot to the base}
if (( HOOKS )); then PACK=${K12_PACK:-}; else PACK=${K12_PACK:?K12_PACK: the pack with the base scene}; fi
XENV=(${=${K12_ENV:-SHEPHERD_K11_COUNTERS=1 SHEPHERD_K11_HEAT=stream}})
BIN=${MACBOOT_BIN:-$REPO/engine/Shepherd.Engine/bin/Debug/net8.0}
real_save() {   # real_save <path>: the path, resolved and lower-cased, looks like a real save location
  case ${(L)${1:A}} in
    *"application support"*|*steamapps*|*steamlibrary*|*library/containers*|*"/saved games"*|*appdata*|*"massive monster"*|*"/ux0:"*|*"/volumes/ux0"*) return 0;;
  esac; return 1; }
(( HOOKS )) || { real_save $SV && { echo "REFUSED: $SV (${SV:A}) looks like a real save location -- the sweep runs on fresh-built sandbox saves only"; exit 3; } }
(( HOOKS )) || for n in $COUNTS; do
  [[ -d $SV/$n ]] || { echo "missing $SV/$n (one sandbox persistent/ folder per follower count)"; exit 2; }
  real_save $SV/$n && { echo "REFUSED: $SV/$n resolves to ${${:-$SV/$n}:A}, a real save location"; exit 3; }
done
export TMPDIR=${SHEPHERD_TMPDIR:-$O/tmp} DOTNET_ROOT=$HOME/.dotnet PATH="$HOME/.dotnet:$PATH" DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
export TMPPREFIX=${TMPDIR%/}/zsh   # (kit #11 integration review: zsh's here-documents next to the temp files, never /tmp/zsh on the system disk)
mkdir -p $O $TMPDIR
mute() { [[ "$(osascript -e 'output muted of (get volume settings)' 2>/dev/null)" == true ]] || osascript -e 'set volume output muted true' 2>/dev/null
         [[ "$(osascript -e 'output muted of (get volume settings)' 2>/dev/null)" == true ]] || { echo "NOT MUTED - refusing"; exit 9; }; }
run() {   # run <count> <k>
  local n=$1 k=$2 o=$O/$1-r$2; mute
  rm -rf $o; mkdir -p $o/root/runtime/logs
  for d in Managed ManagedVita derived; do [[ -e $R/$d ]] && ln -s $R/$d $o/root/runtime/$d; done
  local -a envs
  if (( HOOKS )); then   # kit #13 basec: the repo's sandbox persistent/ (a copy), Quick Start + the follower hook
    cp -R $R/persistent $o/root/runtime/persistent
    envs=(SHEPHERD_ROOT=$o/root SHEPHERD_PROFILE=vita SHEPHERD_AUDIO=off SHEPHERD_RENDER=${K12_RENDER:-null} SHEPHERD_STOP_SCENE=0
      SHEPHERD_PAD_FILE=$PAD SHEPHERD_SIMSTATS=$WIN SHEPHERD_K11_OUT=$o/k11 SHEPHERD_TEST_QUICKSTART=${K12_QS:-1300} SHEPHERD_PHYS=${K12_PHYS:-6})
    [[ -n $PACK ]] && envs+=(SHEPHERD_PACK=$PACK SHEPHERD_PACK_STRICT=1)
    (( n > 1 )) && envs+=(SHEPHERD_TEST_FOLLOWERS=${K12_SPAWN:-1700}:$((n - 1)),base)
    [[ -n $K12_CENSUS && $k == 1 ]] && envs+=(SHEPHERD_MEMREPORT=$K12_CENSUS:1:census)
    envs+=($XENV)
  else
  cp -R $SV/$n $o/root/runtime/persistent   # (a copy: the sandbox folder itself is never written)
  envs=(SHEPHERD_ROOT=$o/root SHEPHERD_PROFILE=vita SHEPHERD_AUDIO=off SHEPHERD_RENDER=null SHEPHERD_PACK=$PACK SHEPHERD_PACK_STRICT=1
    SHEPHERD_PAD_FILE=$PAD SHEPHERD_SIMSTATS=$WIN SHEPHERD_TEST_ASTAR=join SHEPHERD_TEST_SPINE=join SHEPHERD_K11_OUT=$o/k11 $XENV)
  fi
  print -l -- $envs > $o/env.txt
  if (( HOOKS )); then (cd $o/root && env -u SHEPHERD_PACK -u SHEPHERD_PERSISTENT $envs nice dotnet $BIN/Shepherd.Engine.dll boot $FR 100000 > $o/console.txt 2>&1); else
  (cd $o/root && env $envs nice dotnet $BIN/Shepherd.Engine.dll boot $FR 100000 > $o/console.txt 2>&1); fi; local rc=$?
  cp $o/root/runtime/logs/{engine.log,simstats.txt,simstats-frames.tsv} $o/ 2>/dev/null
  rm -rf $o/root
  echo "$n r$k rc=$rc $(tail -1 $o/engine.log 2>/dev/null | sed 's/^\[ *[0-9]*\] *//')"
}
for k in $(seq 1 $RUNS); do for n in $COUNTS; do run $n $k; done; done   # (interleaved: count order inside each round)
for n in $COUNTS; do   # the env lines a device run of this count would use (kit #12 writes its README around them)
  # its saves in a folder of their own: NEVER ux0:data/shepherd/persistent/ (the Vita build's own save folder, where the owner's real
  # slots live); the kit's README copies the count's sandbox folder to the sweep folder before the run and deletes it after
  print -l "SHEPHERD_PERSISTENT=/ux0:data/shepherd/k12-sweep/persistent-$n" "SHEPHERD_SIMSTATS=$WIN" "SHEPHERD_K7_PROF=time" \
    "# followers: $n -- the sandbox base's persistent/ folder for this count goes to ux0:data/shepherd/k12-sweep/persistent-$n/ (a test input," \
    "# deleted after the run); never to ux0:data/shepherd/persistent/ (the Vita build's real saves)" > $O/device-env-$n.txt
done
rm -f $O/sweep.md $O/sweep.md.tmp   # (a stale table from an earlier sweep must never be shown as this one's)
python3 - $O $WIN ${COUNTS[@]} <<'PY' > $O/sweep.md.tmp
import sys, os, glob, statistics, csv
o, win, counts = sys.argv[1], sys.argv[2], sys.argv[3:]
f0, f1 = (int(x) for x in win.split(":"))
import re
print(f"| followers | runs | frame ms mean (median of runs) | p50 | p95 | max | brains seen | flagged frames | task changes | random draws / frame | exceptions | census: object graph 32-bit MiB | census: total 32-bit MiB |")
print("|---|---|---|---|---|---|---|---|---|---|---|---|---|")
for n in counts:
    ms, p50, p95, mx, br, fl, tc, rng, ex = [], [], [], [], [], [], [], [], []
    for d in sorted(glob.glob(os.path.join(o, f"{n}-r*"))):
        fr = [r for r in csv.DictReader(open(os.path.join(d, "simstats-frames.tsv")), delimiter="\t")] if os.path.exists(os.path.join(d, "simstats-frames.tsv")) else []
        v = sorted(float(r["ms"]) for r in fr if f0 <= int(r["frame"]) <= f1)
        if v: ms.append(statistics.mean(v)); p50.append(v[len(v) // 2]); p95.append(v[min(len(v) - 1, int(len(v) * 0.95))]); mx.append(v[-1])
        cp = os.path.join(d, "k11", "k11-counters.tsv")
        if os.path.exists(cp):
            c = [r for r in csv.DictReader((l for l in open(cp) if not l.startswith("#")), delimiter="\t") if f0 <= int(r["frame"]) <= f1]
            if c:
                br.append(max(int(r["brains"]) for r in c)); fl.append(sum(1 for r in c if int(r["flagged_at_frame_end"]) > 0)); tc.append(sum(int(r["task_changes"]) for r in c))
                rng.append(statistics.mean(int(r["rng_draws"]) for r in c)); ex.append(sum(int(r["exc_thrown_main"]) for r in c))
    og, tot = "-", "-"   # kit #13 basec: the census of round 1 (K12_CENSUS), when one ran
    el = os.path.join(o, f"{n}-r1", "engine.log")
    if os.path.exists(el):
        for line in open(el, errors="replace"):
            m = re.search(r"object graph proper\): 64-bit [\d.]+ MiB -> 32-bit ([\d.]+) MiB", line)
            if m: og = m.group(1)
            m = re.search(r"TOTAL projected 32-bit Mono \(align 8\) += ([\d.]+) MiB", line)
            if m: tot = m.group(1)
    med = lambda a, f="{:.2f}": f.format(statistics.median(a)) if a else "-"
    print(f"| {n} | {len(ms)} | {med(ms)} | {med(p50)} | {med(p95)} | {med(mx)} | {med(br, '{:.0f}')} | {med(fl, '{:.0f}')} | {med(tc, '{:.0f}')} | {med(rng)} | {med(ex, '{:.0f}')} | {og} | {tot} |")
PY
rc=$?
(( rc == 0 )) && [[ -s $O/sweep.md.tmp ]] || { echo "NOT WRITTEN: the sweep table (python rc=$rc; a here-document that cannot be written, or the script failed)"; rm -f $O/sweep.md.tmp; exit 1; }
mv $O/sweep.md.tmp $O/sweep.md
cat $O/sweep.md
