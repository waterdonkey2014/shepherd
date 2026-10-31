#!/bin/zsh
# kit #9 fidelity, second review (vita/notes/kit9-fidelity.md 13.1): Unity's answers for the ease-in-out curve behind the tour fixture's f2240
# (Probe.cs) in both oracle players, compiled with the editor's mcs against the oracle player's UnityEngine.CoreModule as conformance.exe and run
# through OracleMain's -ilsuite hook (vita/tests/oracle/setup.sh build), silent. Output: <out>/<arch>/il.tsv; compare.py <out> scores the models.
#   run.sh <out dir>
setopt pipe_fail
T="${0:A:h}"; OUT=${1:?usage: run.sh <out dir>}; OUT=${OUT:a}; OR=${ORACLE_DIR:-${SHEPHERD_WORK:-$HOME/shepherd-work}/kit7/tests-work/oracle}
U="${UNITY_APPS:-/Applications/Unity}/Unity.app/Contents/MonoBleedingEdge"; M=$OR/build/arm64/Oracle.app/Contents/Resources/Data/Managed
[[ "$(osascript -e 'output muted of (get volume settings)' 2>/dev/null)" == true ]] || osascript -e 'set volume output muted true'
[[ "$(osascript -e 'output muted of (get volume settings)' 2>/dev/null)" == true ]] || { echo "NOT MUTED - refusing"; exit 9; }
mkdir -p $OUT/bin
"$U/bin/mono" "$U/lib/mono/4.5/mcs.exe" -nologo -target:library -out:$OUT/bin/conformance.exe -r:$M/UnityEngine.CoreModule.dll -r:$M/netstandard.dll $T/Probe.cs || exit 1
for a in arm64 x64; do
  pre=(); [[ $a == x64 ]] && pre=(arch -x86_64); mkdir -p $OUT/$a
  timeout 600 nice $pre $OR/build/$a/Oracle.app/Contents/MacOS/Oracle -batchmode -nographics -logFile $OUT/$a/player.log -ilsuite $OUT/bin -out $OUT/$a > /dev/null 2>&1
  echo "== $a rc=$?"; cat $OUT/$a/il.tsv
done
