#!/bin/zsh
# kit #7 tests track (vita/notes/kit7-tests.md "AI-written tests behind strict filters", from Meta's TestGen-LLM / ACH): the filter every
# machine-drafted probe (drafted/candidates/D_*.cs, written by Claude) must pass to be kept. Mac only.
#   1 compile   both sides: the Mac test host (dotnet, against the game's own UnityEngine DLLs -- an API the game's DLLs lack cannot be
#               something the game calls) and the Unity oracle project (Unity 2022.3.62f2); a file that fails either is rejected whole
#   2 Unity     each drafted suite run 3x in fresh arm64 players + 1x x64: kept only if the three arm64 runs agree on every line and the
#               suite did not throw in every case (= "passes on real Unity")
#   3 coverage  run on Shepherd with the icall seam counting: kept only if it reaches an engine icall that no hand-written suite
#               (vita/tests/probes) reaches and no earlier kept probe claimed (greedy, most new icalls first)
#   4 mutation  for each claimed icall (up to 3): Shepherd re-run with that icall mutated (Mutate.cs: one ulp / +1 / negated, then the
#               default value, then not called): kept only if at least one mutation changes the probe's output (it catches a fault)
#   5 verdict   the kept probes' Shepherd output vs Unity (arm64, x64 as the other reference): their differences are fidelity findings
# Outputs (owner-local, derived): $DW/{compile,unity,coverage,mutation,verdict}.txt + kept.txt; candidates that fail stay in candidates/
# with the reason in $DW/rejected.txt (drafted/active holds the kept ones afterwards). Silent (muted, batchmode, -nographics).
setopt pipe_fail
T="${0:A:h}"; TESTS="${T:h}"; WT="${TESTS:h:h}"; TW=${TESTS_WORK:-${SHEPHERD_WORK:-$HOME/shepherd-work}/kit7/tests-work}; DW=$TW/drafted; OR=$TW/oracle
export TMPDIR=$TW/tmp DOTNET_ROOT=$HOME/.dotnet PATH="$HOME/.dotnet:$PATH" DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 MSBUILDDISABLENODEREUSE=1 DOTNET_CLI_USE_MSBUILD_SERVER=0
SEED=${SEED:-7}; N=${N:-120}; BASE=${BASE_ICALLS:-$TW/probes/shep-s1-inv}
mkdir -p $DW; : > $DW/rejected.txt
rm -f $T/active/*.cs(N); cp $T/candidates/D_*.cs $T/active/
# ---- 1 compile (Mac host, then Unity)
for round in 1 2 3 4 5; do
  out=$(cd $WT && nice dotnet build vita/tests/TestHost -c Debug 2>&1 | grep -E " error " | sed -E 's|^.*/drafted/active/([^(]+)\(.*error (CS[0-9]+): (.*) \[.*$|\1\t\2 \3|' | sort -u)
  [[ -z $out ]] && break
  for f in $(print -r -- "$out" | cut -f1 | sort -u | grep -v '^D_Common.cs$'); do echo "$f	compile (Mac host): $(print -r -- "$out" | grep "^$f" | head -2 | cut -f2 | tr '\n' ' ')" >> $DW/rejected.txt; rm -f $T/active/$f; done
done
zsh $TESTS/oracle/setup.sh build > $DW/unity-build.log 2>&1
for round in 1 2 3; do
  L=$(ls -t $OR/logs/build*.log | head -1)
  bad=($(grep -oE "Assets/Oracle/Drafted/[A-Za-z_]+\.cs\([0-9]+,[0-9]+\): error" $L | sed -E 's|Assets/Oracle/Drafted/([^(]+)\(.*|\1|' | sort -u))
  (( ${#bad} )) || break
  for f in ${bad:#D_Common.cs}; do echo "$f	compile (Unity): $(grep -E "Drafted/$f.*error" $L | head -1 | sed 's/.*error //' | cut -c1-160)" >> $DW/rejected.txt; rm -f $T/active/$f; done
  zsh $TESTS/oracle/setup.sh build >> $DW/unity-build.log 2>&1
done
(cd $WT && nice dotnet build vita/tests/TestHost -c Debug 2>&1 | grep -E " error |rror\(s\)") > $DW/compile.txt
suites=($(env SHEPHERD_ROOT=$TW/probe-root SHEPHERD_PROFILE=vita dotnet $WT/vita/tests/TestHost/bin/Debug/net8.0/Shepherd.TestHost.dll list 2>/dev/null | grep -E '^d-'))
echo "compiled on both sides: ${#suites} drafted suites (${suites[*]})" >> $DW/compile.txt
S=${(j:,:)suites}
# ---- 2 Unity: 3 arm64 runs + 1 x64
for i in 1 2 3; do zsh $TESTS/oracle/run.sh unity arm64 $DW/u$i $S $SEED $N > /dev/null; done; zsh $TESTS/oracle/run.sh unity x64 $DW/ux $S $SEED $N > /dev/null
: > $DW/unity.txt; stable=()
for s in $suites; do
  if ! cmp -s $DW/u1/$s.tsv $DW/u2/$s.tsv || ! cmp -s $DW/u1/$s.tsv $DW/u3/$s.tsv; then echo "$s	unstable on Unity (the three arm64 runs differ)" | tee -a $DW/unity.txt >> $DW/rejected.txt; continue; fi
  cases=$(grep -c "^[0-9]*	in\.\|^[0-9]*	[a-zA-Z]" $DW/u1/$s.tsv); fails=$(grep -c "	!case	" $DW/u1/$s.tsv)
  ncase=$(cut -f1 $DW/u1/$s.tsv | grep -E '^[0-9]+$' | sort -u | wc -l | tr -d ' ')
  if (( fails >= ncase )); then echo "$s	fails on real Unity (every case threw)" | tee -a $DW/unity.txt >> $DW/rejected.txt; continue; fi
  echo "$s	stable on Unity: 3 arm64 runs identical ($(grep -c . $DW/u1/$s.tsv) lines, $fails of $ncase cases threw); x64 $(cmp -s $DW/u1/$s.tsv $DW/ux/$s.tsv && echo identical || echo differs)" >> $DW/unity.txt; stable+=($s)
done
# ---- 3 coverage on Shepherd (Vita settings: invariant globalization)
zsh $TESTS/oracle/run.sh shepherd $DW/shep ${(j:,:)stable} $SEED $N DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1 > $DW/shep.out 2>&1
python3 - $BASE $DW/shep $DW "${stable[@]}" > $DW/coverage.txt <<'PY'
import sys, os, glob
base, shep, dw, suites = sys.argv[1], sys.argv[2], sys.argv[3], sys.argv[4:]
def ic(p):
    s = set()
    if os.path.exists(p):
        for l in open(p):
            f = l.rstrip("\n").split("\t")
            if len(f) >= 3 and f[2] == "impl": s.add(f[0])
    return s
basecov = set().union(*[ic(p) for p in glob.glob(base + "/*.icalls")]) if glob.glob(base + "/*.icalls") else set()
reach = {s: ic(f"{shep}/{s}.icalls") for s in suites}
claimed = set(); kept = []
for s in sorted(suites, key=lambda s: -len(reach[s] - basecov)):
    new = reach[s] - basecov - claimed
    if new: kept.append(s); claimed |= new; print(f"{s}\tKEEP\t{len(reach[s])} icalls reached, {len(new)} new: {' '.join(sorted(new))}")
    else: print(f"{s}\tDROP\t{len(reach[s])} icalls reached, none new (all reached by the hand-written suites or an earlier kept probe)")
open(dw + "/cov-kept.txt", "w").write("\n".join(f"{s}\t{' '.join(sorted(reach[s] - basecov))}" for s in kept) + "\n")
print(f"baseline: {len(basecov)} icalls reached by the hand-written suites; the kept drafted probes add {len(claimed)}")
PY
grep "	DROP	" $DW/coverage.txt | awk -F'\t' '{print $1"\tcoverage: "$3}' >> $DW/rejected.txt
# ---- 4 mutation
: > $DW/mutation.txt; : > $DW/kept.txt
while IFS=$'\t' read -r s icalls; do
  [[ -z $s ]] && continue
  caught=0; tried=0; detail=""
  for ic in ${${=icalls}[1,3]}; do
    for op in ulp zero skip; do
      tried=$((tried+1)); md=$DW/mut/$s-${ic//[^A-Za-z0-9]/_}-$op; mkdir -p $md
      env SHEPHERD_ROOT=$TW/probe-root SHEPHERD_PROFILE=vita SHEPHERD_AUDIO=off DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1 PROBE_HANG_S=6 nice dotnet $WT/vita/tests/TestHost/bin/Debug/net8.0/Shepherd.TestHost.dll mutate "$ic=$op" probe $s $SEED $N $md > $md/out.txt 2>&1
      if ! cmp -s <(grep -v '^#' $DW/shep/$s.tsv) <(grep -v '^#' $md/$s.tsv 2>/dev/null); then caught=$((caught+1)); detail+=" ${ic}=${op}:caught"; break; else detail+=" ${ic}=${op}:missed"; fi
    done
  done
  echo "$s	mutations caught $caught of the icalls tried:$detail" >> $DW/mutation.txt
  if (( caught > 0 )); then echo "$s" >> $DW/kept.txt; else echo "$s	mutation: no seeded fault in its icalls changed its output" >> $DW/rejected.txt; fi
done < $DW/cov-kept.txt
# ---- 5 verdict vs Unity
python3 $TESTS/oracle/classify.py $DW/shep $DW/u1 --either $DW/ux --suites ${(j:,:)$(cat $DW/kept.txt)} --examples 1 > $DW/verdict.txt 2>&1
# drafted/active = the kept probes' files only
keepfiles=($(for s in $(cat $DW/kept.txt); do grep -l "\"$s\"" $T/active/*.cs; done | sort -u))
keepfiles+=($T/active/D_Common.cs)   # (the shared helpers stay with the kept probes)
for f in $T/active/*.cs(N); do [[ ${keepfiles[(Ie)$f]} == 0 ]] && rm -f $f; done
echo "kept: $(wc -l < $DW/kept.txt | tr -d ' ') probes in $(ls $T/active/*.cs(N) | wc -l | tr -d ' ') files; rejected: $(cut -f1 $DW/rejected.txt | sort -u | wc -l | tr -d ' ') (see $DW/rejected.txt)"
