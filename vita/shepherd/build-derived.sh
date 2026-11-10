#!/bin/zsh
# Builds the owner-local Shepherd.Engine.Derived.dll -- the engine's ports of decompiled game / third-party code and of the game's /
# third-party compiled shaders (the Febucci TextAnimator database replay, Runtime/VitaMemory.cs FebucciReplay; the render features behind
# Render/DerivedRender.cs: the lit-shader model, the light mask + LUT grading, the Stylizer, with their GLSL as embedded resources) --
# from engine/Derived/Shepherd.Engine.Derived (git-ignored) into runtime/derived/ (git-ignored). Derived code: never in git, never in a
# VPK (build.sh -> check_vpk.py --derived-src greps the packaged DLLs for its symbols and its shaders' lines, refuses a packaged
# Shepherd.Engine.Derived.dll, and forbids runtime/derived by content). stage-owner.sh copies it into the owner's managed/ folder
# (ux0:data/shepherd/managed), sim.sh stage into the sim's Data/Managed; the engine loads it by name like a game DLL (on the Mac from
# <root>/runtime/derived). It references Shepherd.Engine (the render contract), so it is built after it; only the one DLL is copied out.
# Prints the DLL's path, or "absent" (exit 0) when the source is not on this machine: the engine then lets the game run its own code
# and draws without the owner-local render features.
# Refuses (exit 1) when derived content sits in a file git carries (derived_leaks.py) or when the engine lacks the render contract.
# env: SHEPHERD_DERIVED_OUT (default <repo>/runtime/derived), SHEPHERD_DERIVED_ART (MSBuild artifacts, default ${SHEPHERD_WORK:-$HOME/shepherd-work}/
#      derived-art), SHEPHERD_DERIVED_ENGINE=<Shepherd.Engine.dll> (compile against that exact engine build instead of the project: the
#      derived-AOT build, whose image records the engine's MVID).
set -euo pipefail
HERE=${0:a:h}; REPO=${HERE:h:h}
export TMPDIR=${TMPDIR:-${SHEPHERD_WORK:-$HOME/shepherd-work}/tmp} DOTNET_ROOT=$HOME/.dotnet PATH="$HOME/.dotnet:$PATH" DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
SRC=$REPO/engine/Derived/Shepherd.Engine.Derived
P=$SRC/Shepherd.Engine.Derived.csproj
OUT=${SHEPHERD_DERIVED_OUT:-$REPO/runtime/derived}
ART=${SHEPHERD_DERIVED_ART:-${SHEPHERD_WORK:-$HOME/shepherd-work}/derived-art}
[[ -f $P ]] || { echo absent; exit 0; }
git -C "$REPO" check-ignore -q "$P" || { echo "refusing: $P is not git-ignored" >&2; exit 1; }
# fid1-fixup round 2: the render provider needs the engine's side of the contract (Render/DerivedRender.cs, RenderKit.cs: fid1-fixup,
# on main since the integration; DerivedRender.Api 3 since integ step 3). A checkout without it (a branch cut before the integration)
# cannot build it: say so instead of a wall of CS0246.
EXPORT=${SHEPHERD_WORK:-$HOME/shepherd-work}/fid1fix/derived-export/engine/Derived   # the owner's Api-3 copy (sha256s in ../derived-export-manifest.txt)
if [[ -f $SRC/Render/RenderProvider.cs && ! -f $REPO/engine/Shepherd.Engine/Render/DerivedRender.cs ]]; then
  echo "refusing: engine/Derived holds the render provider (fid1-fixup), but this engine has no DerivedRender contract" >&2
  echo "  (engine/Shepherd.Engine/Render/DerivedRender.cs): this checkout predates the integration -- build from main (contract Api 3)" >&2
  echo "  with an Api-3 engine/Derived (copy $EXPORT over engine/Derived)" >&2
  exit 1
fi
API=$(sed -nE 's/^ *public const int Api = ([0-9]+);.*/\1/p' "$REPO/engine/Shepherd.Engine/Render/DerivedRender.cs" 2>/dev/null | head -1 || true)
# fid1-fixup round 2: derived content must not sit in any file git carries (vita/shepherd/derived_leaks.py: shader lines, string
# literals, >= 7-significant-digit numbers, shader file names from engine/**/Derived, searched in the tracked + untracked-not-ignored
# files). A leak is fixed by moving it (here: refused before anything is built).
python3 "$HERE/derived_leaks.py" --repo "$REPO" --quiet >&2 || { echo "refusing: derived content in files git carries (listed above) -- move it to engine/Derived" >&2; exit 1; }
# the V3 diet keeps only the property-bag keys Shepherd.Engine's own literals name (Assets/Diet.cs BuildReadable): derived code reads
# bag keys through Render/RenderKit.cs, never with a literal of its own
if grep -rnE '\.(Get|Set)\("' --include='*.cs' "$SRC" > "$TMPDIR/derived-bagreads.txt"; then
  echo "refusing: derived code reads property-bag keys directly (use Shepherd.Engine.Render.RenderKit):" >&2; cat "$TMPDIR/derived-bagreads.txt" >&2; exit 1
fi
ENG=(); [[ -n ${SHEPHERD_DERIVED_ENGINE:-} ]] && ENG=(-p:ShepherdEngineDll="$SHEPHERD_DERIVED_ENGINE")
# device kit #14: SHEPHERD_K14_SMALL_CODE=0 = kit #13's engine image, which has no catalog contract: the owner-local catalog reader stays out of this DLL too
[[ ${SHEPHERD_K14_SMALL_CODE:-1} == 0 ]] && ENG+=(-p:ShepherdK14Small=0)
mkdir -p "$OUT"
rm -rf "$ART/out"
dotnet build "$P" -c Release -p:DisableTransitiveFrameworkReferenceDownloads=true --source ~/.nuget/packages $ENG \
  --artifacts-path "$ART" -o "$ART/out" -nologo -v q > "$OUT/build.log" 2>&1 \
  || { grep -E " error " "$OUT/build.log" | head -20 >&2 || true
       # the usual cause after a contract bump: engine/Derived is older than the engine (an Api-2 copy against Api 3 fails here)
       echo "build failed: if engine/Derived predates the engine's contract (DerivedRender.Api ${API:-?}), copy $EXPORT over engine/Derived" >&2
       exit 1; }
[[ -f $ART/out/Shepherd.Engine.Derived.dll ]] || { echo "build produced no DLL (see $OUT/build.log)" >&2; exit 1; }
cp "$ART/out/Shepherd.Engine.Derived.dll" "$OUT/Shepherd.Engine.Derived.dll"
echo "$OUT/Shepherd.Engine.Derived.dll"
