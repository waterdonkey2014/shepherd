#!/bin/zsh
# kit #7 tests track (vita/notes/kit7-tests.md): the owner-local Unity 2022.3.62f2 oracle project -- E1's pattern (kit6/physics-work/e1),
# copied into this track's work dir: built-in modules + the editor's bundled com.unity.ugui only, the package registry pointed at
# 127.0.0.1:9 BEFORE the first editor launch (E1's one unintended fetch happened when an empty project was created: this project is never
# empty -- the manifest and the settings exist before Unity first opens it), audio disabled, batchmode, no downloads.
#   setup.sh sync    (re)create the project and copy the sources (vita/tests/probes + vita/tests/oracle) into it
#   setup.sh build   + build the macOS Mono players (arm64, x64) -> $OR/build/{arm64,x64}/Oracle.app; logs in $OR/logs
# The project's ProjectSettings come from E1 (owner-local, game-derived physics settings included; never in git).
setopt pipe_fail
T="${0:A:h}"; TESTS="${T:h}"; OR=${ORACLE_DIR:-${SHEPHERD_WORK:-$HOME/shepherd-work}/kit7/tests-work/oracle}; P=$OR/proj
U="${UNITY_APPS:-/Applications/Unity}/Unity.app/Contents/MacOS/Unity"
E1=${SHEPHERD_WORK:-$HOME/shepherd-work}/kit6/physics-work/e1/proj
mkdir -p $P/Assets/Oracle $P/Assets/Editor $P/Packages $P/ProjectSettings $OR/logs $OR/build
if [[ ! -f $P/ProjectSettings/ProjectVersion.txt ]]; then cp $E1/ProjectSettings/*.asset $E1/ProjectSettings/ProjectVersion.txt $P/ProjectSettings/; fi
cat > $P/Packages/manifest.json <<'JSON'
{
  "registry": "http://127.0.0.1:9",
  "dependencies": {
    "com.unity.modules.physics": "1.0.0",
    "com.unity.modules.physics2d": "1.0.0",
    "com.unity.modules.imageconversion": "1.0.0",
    "com.unity.modules.jsonserialize": "1.0.0",
    "com.unity.modules.ui": "1.0.0",
    "com.unity.modules.imgui": "1.0.0",
    "com.unity.modules.animation": "1.0.0",
    "com.unity.ugui": "1.0.0"
  }
}
JSON
rsync -a --delete --exclude '*.meta' $TESTS/probes/ $P/Assets/Oracle/Probes/
mkdir -p $TESTS/drafted/active; rsync -a --delete --exclude '*.meta' $TESTS/drafted/active/ $P/Assets/Oracle/Drafted/   # (the drafted probes still in the filter)
cp $T/OracleMain.cs $P/Assets/Oracle/; cp $T/Editor/*.cs $P/Assets/Editor/
grep -q "m_DisableAudio: 1" $P/ProjectSettings/AudioManager.asset || { echo "audio not disabled in the oracle project - refusing"; exit 9; }
echo "oracle project synced: $P ($(ls $P/Assets/Oracle/Probes | wc -l | tr -d ' ') probe files)"
if [[ $1 == build ]]; then
  osascript -e 'set volume output muted true'
  [[ "$(osascript -e 'output muted of (get volume settings)')" == true ]] || { echo "NOT MUTED - refusing"; exit 9; }
  n=$(ls $OR/logs/build*.log 2>/dev/null | wc -l | tr -d ' '); L=$OR/logs/build$((n + 1)).log
  timeout 3600 nice "$U" -batchmode -nographics -projectPath $P -executeMethod OracleBuild.BuildBoth -logFile $L; rc=$?
  echo "build rc=$rc ($L); downloads in the log: $(grep -ci 'downloading' $L)"; grep -E "Oracle: build|error CS" $L | head -20
  ls -d $OR/build/*/Oracle.app 2>/dev/null
  exit $rc
fi
