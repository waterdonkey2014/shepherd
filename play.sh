#!/bin/zsh
# Live, windowed run of Cult of the Lamb's real boot -> title -> Main Menu on the Shepherd engine.
# Keys (the game's own Rewired keyboard map): Space/E = press-any-button + accept, W/S = up/down, Esc = back. Close the window to quit.
# Saves are sandboxed to runtime/persistent; Steam is never contacted.
# fid1-fixup (defaults): the window uses the GL backend, so it shows the lit model, light mask and grading (with the owner-local
# Shepherd.Engine.Derived in runtime/derived: vita/shepherd/build-derived.sh; without it GL draws unlit, like SDL).
# SHEPHERD_RENDER=sdl ./play.sh gives the old SDL_Renderer window. Only this script changed: the engine's own default (no
# SHEPHERD_RENDER) is still SDL, so every regression script and its recorded baseline is as before. The engine is rooted in this
# checkout (its runtime/: saves sandbox, owner-local DLL), also when this is a worktree.
export DOTNET_ROOT=$HOME/.dotnet PATH="$HOME/.dotnet:$PATH" DOTNET_CLI_TELEMETRY_OPTOUT=1
export SHEPHERD_RENDER=${SHEPHERD_RENDER:-gl} SHEPHERD_ROOT=${SHEPHERD_ROOT:-${0:A:h}}
cd "${0:A:h}/engine/Shepherd.Engine" && exec dotnet bin/Shepherd.Engine.dll boot 0 0 window
