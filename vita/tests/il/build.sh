#!/bin/zsh
# kit #7 tests track (vita/notes/kit7-tests.md "Compiler + instruction differential tests"): the IL conformance suite, compiled ONCE to IL
# with Unity's own compiler (the editor's MonoBleedingEdge mcs, .NET 4.x profile -- the same IL then runs on every tier). Sources: Mono's
# mini regression tests from the local dotnet/runtime tree the Vita runtime is built from (MIT, third-party: copied into the work dir at
# build time, never committed) + this track's FpMode.cs; the recording driver Driver.cs. -> $OUT/{conformance.exe, conf-*.dll, build.txt}
setopt pipe_fail
T="${0:A:h}"; OUT=${IL_OUT:-${SHEPHERD_WORK:-$HOME/shepherd-work}/kit7/tests-work/il/bin}
MINI=${MONO_MINI:-${SHEPHERD_WORK:-$HOME/shepherd-work}/repro6/runtime/src/mono/mono/mini}
U="${UNITY_APPS:-/Applications/Unity}/Unity.app/Contents/MonoBleedingEdge"; MCS=("$U/bin/mono" "$U/lib/mono/4.5/mcs.exe")
rm -rf $OUT; mkdir -p $OUT/src; : > $OUT/build.txt
echo "mini tests from $MINI ($(git -C ${MINI:h:h:h:h} log --oneline -1 2>/dev/null | cut -c1-60))" >> $OUT/build.txt
$MCS -nologo -out:$OUT/conformance.exe $T/Driver.cs >> $OUT/build.txt 2>&1 || { echo "driver did not compile"; cat $OUT/build.txt; exit 1; }
$MCS -nologo -target:library -out:$OUT/conf-fpmode.dll $T/FpMode.cs >> $OUT/build.txt 2>&1
$MCS -nologo -target:library -out:$OUT/conf-f2imode.dll $T/F2iMode.cs >> $OUT/build.txt 2>&1   # kit #9 fidelity: float -> integer edge probes
for f in basic basic-float basic-long basic-math basic-calls objects arrays generics exceptions devirtualization gshared aot-tests mixed ratests unaligned basic-vectors; do
  [[ -f $MINI/$f.cs ]] || continue
  cp $MINI/$f.cs $OUT/src/; sha=$(shasum -a 256 $MINI/$f.cs | cut -c1-16)
  if $MCS -nologo -target:library -unsafe -nowarn:162,168,169,219,414,649,659,660,661,1718 -out:$OUT/conf-$f.dll $MINI/$f.cs $MINI/TestDriver.cs $MINI/TestHelpers.cs > $OUT/src/$f.log 2>&1; then
    echo "conf-$f.dll: compiled ($sha)" >> $OUT/build.txt
  else echo "$f.cs: does not compile with mcs ($sha): $(grep -m2 'error' $OUT/src/$f.log | tr '\n' ' ' | cut -c1-200)" >> $OUT/build.txt; fi
done
cp $MINI/TestDriver.cs $MINI/TestHelpers.cs $OUT/src/
cat $OUT/build.txt
