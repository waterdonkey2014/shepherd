#!/bin/bash
# Runs the Shepherd.Assets CLI (Release build) with the local .NET 8 SDK.
export DOTNET_ROOT=$HOME/.dotnet PATH=$HOME/.dotnet:$PATH DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
exec dotnet "$(dirname "$0")/Shepherd.Assets.Cli/bin/Release/net8.0/Shepherd.Assets.Cli.dll" "$@"
