#!/bin/zsh
# kit #4 boot (B-1): icalls.bin next to the icalls.json in <managed dir> (engine/Shepherd.Engine/IcallManifest.cs: the engine then
# skips parsing the 3.7 MB json at boot). Builds this tool on the build volume, offline. The json and the bake are derived from the
# game's DLLs: run it only on folders that never go into git or a VPK (runtime/ManagedVita, the sim's Data/Managed, the owner folder).
# usage: bake.sh <managed dir> [--check]
set -euo pipefail
HERE=${0:a:h}; REPO=${HERE:h:h}
D=${1:?usage: bake.sh <managed dir> [--check]}
export TMPDIR=${TMPDIR:-${SHEPHERD_WORK:-$HOME/shepherd-work}/tmp} DOTNET_ROOT=$HOME/.dotnet PATH="$HOME/.dotnet:$PATH" DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
ART=${SHEPHERD_ICALLBAKE_ART:-${SHEPHERD_WORK:-$HOME/shepherd-work}/tmp/icallbake-$(print -r -- $REPO | shasum | cut -c1-8)}
[[ -f "$D/icalls.json" ]] || { echo "bake.sh: no icalls.json in $D" >&2; exit 1; }
dotnet build "$HERE/IcallBake.csproj" -c Release -p:DisableTransitiveFrameworkReferenceDownloads=true --source ~/.nuget/packages \
  --artifacts-path "$ART" -nologo -v q > "$ART.build.log" 2>&1 || { echo "bake.sh: IcallBake did not build ($ART.build.log):" >&2; grep -E " error " "$ART.build.log" | head >&2; exit 1; }
if [[ ${2:-} == --check ]]; then dotnet "$ART/bin/IcallBake/release/IcallBake.dll" --check "$D/icalls.json"
else dotnet "$ART/bin/IcallBake/release/IcallBake.dll" "$D/icalls.json"; fi
