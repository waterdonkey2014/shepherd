#!/bin/sh
# SHEPHERD-VITA LLVM: fetch + unpack the official Mono LLVM packages that dotnet/runtime v8.0.31 restores when LLVM is enabled
# (src/mono/llvm/llvm-init.proj: runtime.<host>.Microsoft.NETCore.Runtime.Mono.LLVM.Sdk + .Tools; versions from eng/Versions.props,
# runtimeosxarm64MicrosoftNETCoreRuntimeMonoLLVM{Sdk,Tools}Version = 16.0.5-alpha.1.25311.1; feed: NuGet.config's dotnet8).
# Owner-approved download (2026-10-07): these two packages only. Layout = llvm-init.proj's CopyLLVMToTree:
# tools/osx-arm64/** of both packages -> $L/sdk/arm64/ (bin/{llc,opt,...}, include/, lib/*.a).
# The pinned sha256 values are those of the packages fetched on 2026-10-07; a mismatch stops the script.
set -e
export TMPDIR=${TMPDIR:-${SHEPHERD_WORK:-$HOME/shepherd-work}/tmp}
L=${SHEPHERD_LLVM_DIR:-${SHEPHERD_WORK:-$HOME/shepherd-work}/llvm}
V=16.0.5-alpha.1.25311.1
FEED=https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet8/nuget/v3/flat2
SHA_sdk=e755f62b34243543c4d3aea6ea8e3c10382b60587475b88355b97328cd7907f6
SHA_tools=c4fdcbb2342a8eedbd8f0dd7f823831fc005d774582e83392d81aaff984d684f
mkdir -p "$L/pkgs" "$L/sdk/arm64"
for p in sdk tools; do
  n=runtime.osx-arm64.microsoft.netcore.runtime.mono.llvm.$p
  f="$L/pkgs/$n.$V.nupkg"
  [ -f "$f" ] || curl -sfL --max-time 900 -o "$f" "$FEED/$n/$V/$n.$V.nupkg"
  eval want=\$SHA_$p
  got=$(shasum -a 256 "$f" | cut -d' ' -f1)
  [ "$got" = "$want" ] || { echo "$f: sha256 $got, expected $want"; exit 1; }
  rm -rf "$L/pkgs/x-$p" && mkdir -p "$L/pkgs/x-$p" && (cd "$L/pkgs/x-$p" && unzip -q "$f")
  cp -R "$L/pkgs/x-$p/tools/osx-arm64/." "$L/sdk/arm64/"
done
chmod +x "$L/sdk/arm64/bin/"*
grep MONO_API_VERSION "$L/sdk/arm64/include/llvm/Config/llvm-config.h"
"$L/sdk/arm64/bin/llc" --version | sed -n '1,3p'
