#!/bin/zsh
# kit #10 fidelity (vita/notes/kit10-fidelity.md "2. Exact decoders"): the decoder pin -- every decoder Shepherd runs on game data checked
# integer-exact against its reference, on the Mac (MEASURED-M). Builds the reference dylibs (native/: Unity's crunch fork's crn_decomp.h, the
# Vita's own IMA decoder vita/audio/src/sa_codec.c) and DecoderPin, then runs:
#   crunch  every crunched Texture2D of the game's bundles, every mip level: Shepherd.Assets' managed crunch port vs crn_decomp.h
#   lz4     every LZ4 block of the game's bundles: Shepherd.Assets' Lz4.Decode vs the system liblz4
#   ima     every IMA ADPCM take of an audio bake set: the Vita's C decoder vs the bake's model (tools/FmodBake/Encode.cs)
# usage: run.sh <out dir> [crunch] [lz4] [ima]      (default all three)
# env: PIN_DATA (the game's Data folder; read only), PIN_AUDIO (the bake set, default runtime/audiobake/menu), PIN_CRN_INC (crunch-unity/inc;
#      default the main checkout's engine/Shepherd.Assets/native/crunch-unity/inc), PIN_LZ4 (liblz4.dylib), PIN_J (parallel jobs, default 4).
# Reports name game assets: they stay in <out dir> (owner-local). Nothing is downloaded; the game is never run (its files are read only).
setopt pipe_fail
T="${0:A:h}"; WT="${T:h:h:h}"
O=${1:?out dir}; O=${O:a}; shift; want=("$@"); (( ${#want} )) || want=(crunch lz4 ima)
DATA=${PIN_DATA:-"${SHEPHERD_GAME_DATA}"}
AUDIO=${PIN_AUDIO:-$WT/runtime/audiobake/menu}
CRN=${PIN_CRN_INC:-"${SHEPHERD_REPO:-$HOME/src/shepherd}/engine/Shepherd.Assets/native/crunch-unity/inc"}
LZ4=${PIN_LZ4:-/opt/homebrew/lib/liblz4.dylib}; J=${PIN_J:-4}
export DOTNET_ROOT=$HOME/.dotnet PATH="$HOME/.dotnet:$PATH" DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 MSBUILDDISABLENODEREUSE=1 DOTNET_CLI_USE_MSBUILD_SERVER=0
mkdir -p $O/native
[[ -f $CRN/crn_decomp.h ]] || { echo "no crn_decomp.h in $CRN"; exit 1; }
clang++ -O2 -std=c++14 -shared -fPIC -w -Dmalloc_usable_size=malloc_size -I$WT/engine/Shepherd.Assets/native/crntest/shim -I"$CRN" $T/native/crnref.cpp -o $O/native/crnref.dylib || exit 1
clang -O2 -shared -fPIC -Wall -I$WT/vita/audio/include -I$WT/vita/audio/src $T/native/imaref.c $WT/vita/audio/src/sa_codec.c -o $O/native/imaref.dylib || exit 1
dotnet build -c Release -nologo -v q $T/DecoderPin/DecoderPin.csproj -o $O/bin > $O/build.log 2>&1 || { tail -20 $O/build.log; exit 1; }
rc=0
for w in $want; do
  case $w in
    crunch) dotnet $O/bin/DecoderPin.dll crunch "$DATA" $O/crunch-report.txt $O/native/crnref.dylib -j $J > $O/crunch.console 2>&1 || rc=1; tail -2 $O/crunch-report.txt ;;
    lz4)    dotnet $O/bin/DecoderPin.dll lz4 "$DATA" $O/lz4-report.txt $LZ4 -j $J > $O/lz4.console 2>&1 || rc=1; tail -2 $O/lz4-report.txt ;;
    ima)    dotnet $O/bin/DecoderPin.dll ima "$AUDIO" $O/ima-report.txt $O/native/imaref.dylib > $O/ima.console 2>&1 || rc=1; tail -2 $O/ima-report.txt ;;
  esac
done
exit $rc
