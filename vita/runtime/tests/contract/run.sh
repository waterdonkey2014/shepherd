#!/bin/zsh
# SHEPHERD-VITA: managed contract test of the Vita native shim (called by ../run-tests.sh).
#   1. P/Invoke census of the linux-arm BCL (vita/v6/tools/AsmSize pinvokes)
#   2. libvitapal.dylib = the shim's crypto / network / zlib files + the generated stubs, compiled for the Mac, re-exporting
#      the Mac libSystem.Native for everything the Vita file shim (pal_vita.c, sceIo) does on the device
#   3. Contract.dll (CoreCLR) loads the linux-arm System.Security.Cryptography / System.IO.Compression[.Brotli] / System.Net.*
#      into their own AssemblyLoadContext bound to libvitapal, and compares with the Mac BCL. Exit 0 = all pass.
# env: SHEPHERD_RUNTIME (fork checkout), SHEPHERD_LINUX_ARM_PACK, TMPDIR (scratch)
set -euo pipefail
HERE=${0:a:h}
S=${HERE:h:h}/system-native
V=${HERE:h:h:h}
RT=${SHEPHERD_RUNTIME:-${SHEPHERD_WORK:-$HOME/shepherd-work}/runtime}
PACK=${SHEPHERD_LINUX_ARM_PACK:-${SHEPHERD_WORK:-$HOME/shepherd-work}/packs/mono.linux-arm.8.0.31/runtimes/linux-arm}
export DOTNET_ROOT=$HOME/.dotnet PATH="$HOME/.dotnet:$PATH" DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
NETCORE=$(ls -d $DOTNET_ROOT/shared/Microsoft.NETCore.App/8.0.* | sort -V | tail -1)
[ -f "$PACK/lib/net8.0/System.Security.Cryptography.dll" ] || { echo "no linux-arm pack at $PACK"; exit 1; }
O=$(mktemp -d "${TMPDIR:-/tmp}/vita-contract.XXXXXX")
trap 'rm -rf "$O"' EXIT

dotnet build "$V/v6/tools/AsmSize/AsmSize.csproj" -c Release -o "$O/asmsize" --artifacts-path "$O/asmsize-art" -p:DisableTransitiveFrameworkReferenceDownloads=true -nologo -v q > "$O/asmsize.log" 2>&1 || { tail -20 "$O/asmsize.log"; exit 1; }
dotnet "$O/asmsize/AsmSize.dll" pinvokes "$O/pinvokes.tsv" "$PACK/lib/net8.0" "$PACK/native/System.Private.CoreLib.dll" > /dev/null

sed -e 's/^#cmakedefine01 \([A-Z0-9_]*\)/#define \1 0/' "$RT/src/native/libs/Common/pal_config.h.in" > "$O/pal_config.h"
CF=(-O1 -g -Wall -Wno-unused-function -I"$O" -I"$RT/src/native/libs/System.Native" -I"$RT/src/native/libs/Common" -I"$RT/src/native" -I"$S/crypto")
for f in "$S/pal_vita_crypto.c" "$S/crypto/vita_crypto.c" "$S/pal_vita_net.c" "$RT/src/native/libs/System.IO.Compression.Native/pal_zlib.c" "$HERE/mac_glue.c"; do
  clang $CF -c "$f" -o "$O/$(basename "$f" .c).o"
done
# defined = this shim's objects + the Mac libSystem.Native (which stands in for pal_vita.c here)
{ nm -g -U "$O"/*.o | awk 'NF==3{sub(/^_/,"",$3); print $3}'; nm -gU "$NETCORE/libSystem.Native.dylib" | awk 'NF==3{sub(/^_/,"",$3); print $3}'; } | sort -u > "$O/defined.txt"
python3 "$S/gen_stubs.py" "$O/pinvokes.tsv" "$RT" "$O/pal_vita_stubs.c" "$O/defined.txt" > /dev/null
clang -O1 -c "$O/pal_vita_stubs.c" -o "$O/pal_vita_stubs.o"
clang -dynamiclib -o "$O/libvitapal.dylib" "$O"/*.o -lz -Wl,-reexport_library,"$NETCORE/libSystem.Native.dylib"

dotnet build "$HERE/Contract.csproj" -c Release -o "$O/bin" --artifacts-path "$O/art" -p:DisableTransitiveFrameworkReferenceDownloads=true -nologo -v q > "$O/build.log" 2>&1 || { tail -20 "$O/build.log"; exit 1; }
dotnet "$O/bin/Contract.dll" "$PACK/lib/net8.0" "$O/libvitapal.dylib"
