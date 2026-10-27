#!/bin/zsh
# kit #7 tests track: run the IL conformance suite (build.sh's IL, unchanged) on the Mac's tiers and tabulate.
#   run.sh [tiers]   tiers: unity-mono (the editor's Mono, arm64, its default), unity-mono-nof32 (the same with -O=-float32: double-precision
#   float intermediates), coreclr (.NET 8 CoreCLR: the Mac test host), player-arm64 / player-x64 (the Unity 2022.3 oracle players: the
#   game's own runtime -- OracleMain -ilsuite), vlx (Shepherd's Vita runtime under vlx, both tiers -- AOT and interpreter -- from one hello
#   build: vita/tests/il/vlx.sh; its results are hello.txt lines, compared by vita-compare.py, not in table.txt). -> $OUT/<tier>.tsv, table.txt
setopt pipe_fail
T="${0:A:h}"; BIN=${IL_OUT:-${SHEPHERD_WORK:-$HOME/shepherd-work}/kit7/tests-work/il/bin}; OUT=${IL_RUNS:-${SHEPHERD_WORK:-$HOME/shepherd-work}/kit7/tests-work/il/runs}; OR=${ORACLE_DIR:-${SHEPHERD_WORK:-$HOME/shepherd-work}/kit7/tests-work/oracle}
U="${UNITY_APPS:-/Applications/Unity}/Unity.app/Contents/MonoBleedingEdge"
export DOTNET_ROOT=$HOME/.dotnet PATH="$HOME/.dotnet:$PATH" DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
mkdir -p $OUT
tiers=(${@:-unity-mono unity-mono-nof32 coreclr player-arm64 player-x64})
print -r -- '{"runtimeOptions":{"tfm":"net8.0","framework":{"name":"Microsoft.NETCore.App","version":"8.0.0"},"rollForward":"LatestMinor"}}' > $BIN/conformance.runtimeconfig.json
for t in $tiers; do
  case $t in
    unity-mono) "$U/bin/mono" $BIN/conformance.exe $BIN $OUT/$t.tsv > $OUT/$t.log 2>&1 ;;
    unity-mono-nof32) "$U/bin/mono" --optimize=-float32 $BIN/conformance.exe $BIN $OUT/$t.tsv > $OUT/$t.log 2>&1 ;;
    coreclr) dotnet exec --runtimeconfig $BIN/conformance.runtimeconfig.json $BIN/conformance.exe $BIN $OUT/$t.tsv > $OUT/$t.log 2>&1 ;;
    player-arm64|player-x64)
      a=${t#player-}; pre=(); [[ $a == x64 ]] && pre=(arch -x86_64)
      [[ "$(osascript -e 'output muted of (get volume settings)')" == true ]] || osascript -e 'set volume output muted true'
      timeout 900 $pre $OR/build/$a/Oracle.app/Contents/MacOS/Oracle -batchmode -nographics -logFile $OUT/$t.player.log -ilsuite $BIN -out $OUT/$t > /dev/null 2>&1
      cp $OUT/$t/il.tsv $OUT/$t.tsv 2>/dev/null ;;
    vlx|vlx-aot|vlx-interp)   # (kit #7 review fix: the Vita tiers' step, committed; one run gives both tiers)
      zsh $T/vlx.sh $OUT > $OUT/vlx.log 2>&1 && python3 $T/vita-compare.py $OUT/vlx-hello.txt $OUT > $OUT/vlx-compare.txt 2>&1
      echo "$t: $(tail -1 $OUT/vlx.log) -- vita-compare: $OUT/vlx-compare.txt"; continue ;;
    *) echo "unknown tier $t" ;;
  esac
  echo "$t: $(grep -c . $OUT/$t.tsv 2>/dev/null) tests recorded"
done
python3 - $OUT ${tiers[@]} > $OUT/table.txt <<'PY'
import sys, os
out, tiers = sys.argv[1], sys.argv[2:]
rows = {}; order = []
for t in tiers:
    p = os.path.join(out, t + ".tsv")
    if not os.path.exists(p): continue
    for l in open(p):
        f = l.rstrip("\n").split("\t")
        if len(f) < 4: continue
        k = (f[0], f[1])
        if k not in rows: rows[k] = {}; order.append(k)
        rows[k][t] = (f[2], f[3])
have = [t for t in tiers if os.path.exists(os.path.join(out, t + ".tsv"))]
print("tiers: " + ", ".join(have)); print(f"tests: {len(order)}")
for t in have:
    fails = sum(1 for k in order if t in rows[k] and rows[k][t][1] not in ("-",) and rows[k][t][0] != rows[k][t][1])
    print(f"  {t}: {sum(1 for k in order if t in rows[k])} ran, {fails} return a value other than the one their name expects")
ref = have[0] if have else None
for t in have[1:]:
    diff = [k for k in order if t in rows[k] and ref in rows[k] and rows[k][t][0] != rows[k][ref][0]]
    print(f"  {t} vs {ref}: {len(diff)} tests return a different value" + (": " + ", ".join(f"{k[1]}={rows[k][ref][0]}/{rows[k][t][0]}" for k in diff[:12]) if diff else ""))
print("\nfpmode (raw bits per tier):")
for k in order:
    if k[0] == "conf-fpmode.dll": print(f"  {k[1].split('.')[-1]:16s} " + " ".join(f"{t}={rows[k].get(t, ('-',))[0]}" for t in have))
PY
cat $OUT/table.txt
