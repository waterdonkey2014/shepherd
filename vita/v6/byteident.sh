#!/bin/zsh
# V6: the Mac default boot, pre-V6 engine (snapshot ${SHEPHERD_WORK:-$HOME/shepherd-work}/v6/pre-engine) vs the current engine: frames 1-1000
# screenshots must be byte-identical (the default path must not change). Writes runtime/logs/v6/byteident-{pre,cur}/.
# BYTEIDENT_PRE=<snapshot dir holding Shepherd.Engine/ Shepherd.Assets/ Shepherd.Hooks/> compares against another snapshot
# (V6 stage 1: ${SHEPHERD_WORK:-$HOME/shepherd-work}/s1/pre-engine); BYTEIDENT_TAG names the output dirs (default byteident).
export TMPDIR=${SHEPHERD_WORK:-$HOME/shepherd-work}/tmp DOTNET_ROOT=$HOME/.dotnet PATH="$HOME/.dotnet:$PATH" DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
REPO="${SHEPHERD_REPO:-$HOME/src/shepherd}"; R="$REPO/runtime"; ENG="$REPO/engine/Shepherd.Engine"
SNAP=${BYTEIDENT_PRE:-${SHEPHERD_WORK:-$HOME/shepherd-work}/v6/pre-engine}; PRE=$SNAP/Shepherd.Engine; PREBIN=$SNAP-bin; TAG=${BYTEIDENT_TAG:-byteident}
(cd $PRE && dotnet build -c Debug -o $PREBIN -p:DisableTransitiveFrameworkReferenceDownloads=true 2>&1 | grep -E " error |rror\(s\)") || exit 1
(cd "$ENG" && dotnet build -c Debug -o bin -p:DisableTransitiveFrameworkReferenceDownloads=true 2>&1 | grep -E " error |rror\(s\)") || exit 1
stamp=$(stat -f %m "$R/persistent/saves/settings.json")
for which in pre cur; do
  out="$R/logs/v6/$TAG-$which"; rm -rf "$out"; mkdir -p "$out"; rm -f "$R"/shots/boot/*.png(N)
  bin=$([[ $which == pre ]] && echo $PREBIN || echo "$ENG/bin")
  (cd "$ENG" && SHEPHERD_PRESS=1050:32 dotnet "$bin/Shepherd.Engine.dll" boot 1000 50 > "$out/console.txt" 2>&1)
  cp "$R/logs/engine.log" "$out/"; cp "$R"/shots/boot/*.png(N) "$out/"
  echo "$which: $(tail -1 "$out/engine.log" | sed 's/^\[ *[0-9]*\] *//')  $(ls "$out"/*.png | wc -l | tr -d ' ') shots  $(grep 'readable property keys' "$out/engine.log" | sed 's/.*auto-getter names + //')"
done
n=0; d=0; for f in "$R"/logs/v6/$TAG-pre/*.png; do n=$((n+1)); cmp -s "$f" "$R/logs/v6/$TAG-cur/${f:t}" || { d=$((d+1)); echo "  DIFFERS: ${f:t}"; }; done
echo "byte-identical screenshots: $((n-d))/$n"
diff <(sed 's/^\[ *[0-9]*\]//' "$R/logs/v6/$TAG-pre/engine.log" | grep -vE "asset layer up|in [0-9.]+ ms|\([0-9]+ ms\)") <(sed 's/^\[ *[0-9]*\]//' "$R/logs/v6/$TAG-cur/engine.log" | grep -vE "asset layer up|in [0-9.]+ ms|\([0-9]+ ms\)") > "$R/logs/v6/$TAG-log.diff" && echo "normalised engine.log: identical" || echo "normalised engine.log: $(grep -c '^[<>]' "$R/logs/v6/$TAG-log.diff") lines differ ($R/logs/v6/$TAG-log.diff)"
[[ "$(stat -f %m "$R/persistent/saves/settings.json")" == "$stamp" ]] || echo "WARNING: runtime/persistent/saves/settings.json changed"
