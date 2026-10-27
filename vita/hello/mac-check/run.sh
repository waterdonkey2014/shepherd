#!/bin/zsh
# Runs the Vita hello's managed checks on the Mac (Microsoft Mono 8.0.29 osx-arm64 pack from the NuGet cache, interpreter).
# SHEPHERD_HOST=mac tells Hello.dll it is not on the device (M3 then expects a 64-bit process; M9b uses the scratch
# cwd instead of ux0:). Exit status = managed Main's return value: 0 only when every check passed.
# env: TMPDIR (scratch; point it at the build volume, e.g. TMPDIR=${SHEPHERD_WORK:-$HOME/shepherd-work}/tmp, when the main disk is
# full), SHEPHERD_HELLO_OUT (hello build output, default ../out). Extra arguments go to Main (e.g. --stress-seconds=60).
set -euo pipefail
HERE=${0:a:h}
OUT=${SHEPHERD_HELLO_OUT:-$HERE/../out}
PKGS=($HOME/.nuget/packages/microsoft.netcore.app.runtime.mono.osx-arm64/*(N/))
(( ${#PKGS} )) || { echo "no Microsoft.NETCore.App.Runtime.Mono.osx-arm64 pack in the NuGet cache"; exit 1; }
P=${PKGS[-1]}/runtimes/osx-arm64
[ -f "$OUT/managed-il/Hello.dll" ] || { echo "no $OUT/managed-il/Hello.dll (run vita/hello/build.sh)"; exit 1; }
T=$(mktemp -d "${TMPDIR:-/tmp}/mac-check.XXXXXX")
trap 'rm -rf "$T"' EXIT
mkdir -p $T/managed && cp $HERE/../managed/hello-data.txt $T/managed/
clang -O1 -o $T/host_mac $HERE/host_mac.c -I$P/native/include/mono-2.0 -L$P/native -lcoreclr -Wl,-rpath,$P/native -Wl,-export_dynamic
TPA=$(ls $P/lib/net8.0/*.dll | tr '\n' ':')$P/native/System.Private.CoreLib.dll:$OUT/managed-il/Hello.dll:$OUT/managed-il/HelloInterp.dll
cd $T
rc=0
SHEPHERD_HOST=mac ./host_mac "$TPA" "$P/native" $OUT/managed-il/Hello.dll "$@" || rc=$?
if (( rc == 0 )); then echo "PASS mac-check (every managed check passed)"; else echo "FAIL mac-check (exit $rc)"; fi
exit $rc
