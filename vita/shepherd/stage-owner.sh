#!/bin/zsh
# SHEPHERD-VITA V6 Stage B: assembles the owner's ux0:data/shepherd/ folder on the external drive, for him to copy to the
# Vita over FTP (VitaShell: SELECT = FTP). These are derived/owner assets -- the game's DLLs, the rewritten Unity modules,
# the pack, the sandbox saves -- so they never go into a VPK, git or anything shared; this script only copies them from
# the owner's own install and build outputs into one folder on the build volume.
#   <out>/shepherd/managed/     the game's Managed DLLs with runtime/ManagedVita laid over them (rewritten Unity/game
#                               modules + icalls.json + its bake icalls.bin, tools/IcallBake), minus the game's old BCL copies (System*, mscorlib, netstandard,
#                               Microsoft.*, Mono.*: the eboot never loads those names from here, as on the Mac), plus
#                               Shepherd.Engine.Derived.dll (vita/shepherd/build-derived.sh: the engine's ports of decompiled
#                               third-party code -- the Febucci replay -- and of the game's / third-party compiled shaders -- the
#                               lit model, light mask + LUT grading, the Stylizer -- which must never be inside the VPKs' Shepherd.Engine.dll)
#   <out>/shepherd/pack/        shepherd.pack (runtime/vitapack/menu; STAGE_PACK=<dir with shepherd.pack + data/> for another pack,
#                               e.g. kit #4's newgame pack). Its size and sha256 are printed and kept in <out>/PACK.txt (k4 review: a
#                               worktree's runtime/ links the main checkout's pack, which can be older than the kit's)
#   <out>/shepherd/data/        RuntimeInitializeOnLoads.json + StreamingAssets/aa/{settings,catalog}.json + (kit #6) video-lengths.tsv
#                               + (kit #8) physics2d-settings.bin
#   <out>/shepherd/persistent/  a copy of runtime/persistent (the sandbox saves; never the real CotL save dir)
#   <out>/shepherd/pad.txt      1050:cross, 1400:down
#   <out>/shepherd/audiobake/menu/  (Stage C, the GL + audio app; STAGE_AUDIO=0 skips it) the menu audio bake: manifest.json + ev/
#                               (runtime/audiobake/menu: takes rendered from the owner's own FMOD banks by tools/FmodBake; derived
#                               game data, so like the pack it is copied by the owner and never put in a VPK)
#   <out>/shepherd/data/video/  (kit #8) the game's clips as .shv files for the hardware video decoder (STAGE_SPLASH below)
# usage: [STAGE_PACK=<pack dir>] [STAGE_CACHE=<cache dir>] [STAGE_VIDEO_LENGTHS=0|<file>] [STAGE_SPLASH=0|all|<dir>] [STAGE_SHADER_CULL=1|<file>] [STAGE_PHYS2D=0|<file>] stage-owner.sh [out dir]   (default ${SHEPHERD_WORK:-$HOME/shepherd-work}/v6b/owner)
set -euo pipefail
HERE=${0:a:h}
REPO=${HERE:h:h}
R=$REPO/runtime
GAME_MANAGED="${SHEPHERD_GAME_DATA}/Managed"
OUT=${1:-${SHEPHERD_WORK:-$HOME/shepherd-work}/v6b/owner}
D=$OUT/shepherd
case $OUT in ${SHEPHERD_WORK:-$HOME/shepherd-work}/*|${SHEPHERD_DATA:-$HOME}/*) ;; *) echo "refusing to stage outside the external drives: $OUT"; exit 1 ;; esac
[[ -f "$R/ManagedVita/icalls.json" ]] || { echo "runtime/ManagedVita missing: run vita/v4/check.sh --rewrite"; exit 1; }
PACK=${STAGE_PACK:-$R/vitapack/menu}
[[ -f "$PACK/shepherd.pack" && -d "$PACK/data" ]] || { echo "$PACK/shepherd.pack (+ data/) missing: run vita/v4/check-pack.sh"; exit 1; }
[[ -f "$GAME_MANAGED/Assembly-CSharp.dll" ]] || { echo "the game's Managed folder is missing: $GAME_MANAGED"; exit 1; }
AUDIO=${STAGE_AUDIO:-1}
[[ $AUDIO = 0 || -f "$R/audiobake/menu/manifest.json" ]] || { echo "runtime/audiobake/menu missing (tools/FmodBake): run with STAGE_AUDIO=0 to stage without sound"; exit 1; }
rm -rf "$D"; mkdir -p "$D/managed" "$D/pack" "$D/data" "$D/persistent"

is_bcl() { [[ $1 == mscorlib || $1 == netstandard || $1 == System* || $1 == Microsoft.* || $1 == Mono.* ]]; }
skipped=()
for f in "$GAME_MANAGED"/*.dll; do
  n=${${f:t}%.dll}
  if is_bcl $n; then skipped+=$n; continue; fi
  cp "$f" "$D/managed/"
done
cp "$R"/ManagedVita/* "$D/managed/"                    # the Vita set laid over (rewritten Unity/game DLLs + icalls.json)
zsh "$REPO/tools/IcallBake/bake.sh" "$D/managed" > /dev/null || { echo "stage-owner: IcallBake failed (tools/IcallBake/bake.sh $D/managed)" >&2; exit 1; }   # kit #4 B-1: icalls.bin, the flat copy of icalls.json the engine reads at boot (same rows; ~14-20 s less boot)
DER=$(zsh "$HERE/build-derived.sh")                    # the owner-local derived engine code (loaded by name from managed/)
[[ -f "$DER" ]] && cp "$DER" "$D/managed/" || echo "note: no engine/Derived source here: the game will run its own Febucci scan, and the apps draw without the Stylizer pass, lighting and grading"
cp "$PACK/shepherd.pack" "$D/pack/"
cp -R "$PACK/data/." "$D/data/"
pk="pack: ${PACK:A}/shepherd.pack, $(stat -f %z "$D/pack/shepherd.pack") bytes, sha256 $(shasum -a 256 "$D/pack/shepherd.pack" | cut -c1-16)"
print -r -- "$pk" > "$OUT/PACK.txt"; print -r -- "stage-owner: $pk"
cp -R "$R/persistent/." "$D/persistent/"
printf '# Shepherd V6 pad script: frame:button[:holdFrames]. 1050 engages "Press Any Button", 1400 moves Play -> DLC.\n1050:cross\n1400:down\n' > "$D/pad.txt"
if [[ $AUDIO = 1 ]]; then   # only the manifest and the takes it names (no reports/logs of the bake)
  mkdir -p "$D/audiobake/menu"
  cp "$R/audiobake/menu/manifest.json" "$D/audiobake/menu/"
  cp -R "$R/audiobake/menu/ev" "$D/audiobake/menu/"
fi
# kit #5 (vita/notes/kit5-boot.md): STAGE_CACHE=<dir> copies a prepared cache/ folder -- e.g. the catalog bake cache/json/catalog.jbake that
# the engine wrote in a Mac sim / vlx start from this same catalog.json (the bake is deterministic; the engine checks it against the card's
# catalog and remakes it when it does not match) -- so the very first start reads the catalog from it. Optional: without it the first
# start parses the catalog and writes the bake. Derived from the game's catalog: owner folder only, never git or a VPK.
if [[ -n ${STAGE_CACHE:-} ]]; then
  [[ -d $STAGE_CACHE ]] || { echo "STAGE_CACHE=$STAGE_CACHE is not a folder"; exit 1; }
  mkdir -p "$D/cache"; cp -R "$STAGE_CACHE/." "$D/cache/"
fi
# kit #6 (owner decision 2026-10-19, "Devolver splash: match the original"; vita/notes/kit6-integration.md): data/video-lengths.tsv, every clip
# of the owner's install (vita/tools/video_lengths.py: each mp4's own frame count and rate), which the engine reads by default under the
# Vita profile (Runtime/K6Engine.cs) -- the boot's Devolver logo then takes its 6.27 s as on the PC. Derived from the game's files: owner
# folder only. STAGE_VIDEO_LENGTHS=0 stages none (kit #5's 0-s clips); STAGE_VIDEO_LENGTHS=<file> copies that table instead.
VIDEOS="${GAME_MANAGED:h}/StreamingAssets/MMVideoPlayer/Videos"
if [[ ${STAGE_VIDEO_LENGTHS:-1} == 1 ]]; then
  [[ -d $VIDEOS ]] || { echo "the game's Videos folder is missing: $VIDEOS (STAGE_VIDEO_LENGTHS=0 stages without the clip lengths)"; exit 1; }
  python3 -I "$REPO/vita/tools/video_lengths.py" "$VIDEOS" -o "$D/data/video-lengths.tsv"
elif [[ ${STAGE_VIDEO_LENGTHS:-1} != 0 ]]; then
  cp "$STAGE_VIDEO_LENGTHS" "$D/data/video-lengths.tsv"
fi
# kit #8 (track boot; vita/notes/kit8-boot.md): data/video/<clip>.shv, the game's videos for the Vita's hardware H.264 decoder
# (vita/tools/splash_pack.py: the owner's clips scaled to the screen as the game shows them, BT.601, H.264 Main 3.1 without B-frames; the
# Devolver logo with the game's own FMOD splash sound from the audio bake) -- the launcher apps' early splash and the engine's real video
# playback read them. Derived from the game's files: owner folder only, never git or a VPK. STAGE_SPLASH=1: the Devolver logo and the
# intro (the kit #8 card: the launcher apps 00193 / 00194 need them); =all every clip; =<dir> copies that folder's .shv files; unset / 0
# (the default -- review fix: kit #7's staging, so other tracks' and kit #7-style runs neither transcode nor need ffmpeg / the Videos
# folder / the audio bake): none (the launcher then loads with a black screen, the engine draws the clips black as in kit #7).
if [[ ${STAGE_SPLASH:-0} == 1 || ${STAGE_SPLASH:-0} == all ]]; then
  [[ -d $VIDEOS ]] || { echo "the game's Videos folder is missing: $VIDEOS (STAGE_SPLASH=0 stages without the clips)"; exit 1; }
  CL=(); [[ ${STAGE_SPLASH:-0} == all ]] && CL=(--clips all)
  mkdir -p "$D/data/video"
  python3 -I "$REPO/vita/tools/splash_pack.py" "$VIDEOS" -o "$D/data/video" --audio-bake "$R/audiobake/menu" $CL | sed 's/^/   /'
elif [[ ${STAGE_SPLASH:-0} != 0 ]]; then
  mkdir -p "$D/data/video"; cp "$STAGE_SPLASH"/*.shv "$D/data/video/"
else
  echo "   data/video/: no .shv clips staged (STAGE_SPLASH=1 makes them: the kit #8 launcher apps' splash and real video; without them both stay black)"
fi
# kit #7 (track probes, G6 "honour Cull state"; vita/notes/kit7-probes.md): data/shader-cull.tsv, every Shader's main-pass Cull state read from
# the owner's install (engine/Shepherd.Assets/Shepherd.Assets.Cli shadercull) -- the engine takes a shader's Cull from it when the asset carries
# none (the pack's shader reduction drops the field), so a Cull Back / Front mesh (the decal cubes) is drawn as Unity draws it. Derived from the
# game's files: owner folder only. Opt-in for now (nothing on the kit routes changes with it): STAGE_SHADER_CULL=1 makes it, =<file> copies one.
if [[ ${STAGE_SHADER_CULL:-0} == 1 ]]; then
  CLI=$TMPDIR/shepherd-assets-cli; dotnet build "$REPO/engine/Shepherd.Assets/Shepherd.Assets.Cli/Shepherd.Assets.Cli.csproj" -c Release -o "$CLI" -nologo -v q > /dev/null \
    || { echo "Shepherd.Assets.Cli did not build (STAGE_SHADER_CULL=0 stages without the cull table)"; exit 1; }
  COTL_DATA="${GAME_MANAGED:h}" dotnet "$CLI/Shepherd.Assets.Cli.dll" shadercull "$D/data/shader-cull.tsv" | sed 's/^/   /'
elif [[ ${STAGE_SHADER_CULL:-0} != 0 ]]; then
  cp "$STAGE_SHADER_CULL" "$D/data/shader-cull.tsv"
fi
# kit #8 (track physics, Law of the Vita part 1; vita/notes/kit8-physics.md): data/physics2d-settings.bin, the project's Physics2DSettings record
# exactly as globalgamemanagers stores it (engine/Shepherd.Assets/Shepherd.Assets.Cli phys2d) -- the play apps' engine reads it at physics level
# 1+ (Runtime/Physics2DSettings.cs, SHEPHERD_PHYS2D_SETTINGS, set by phys_host.inc.c): the card's strict pack, recorded with physics off, has no
# copy, and the core needs the project's gravity / iterations / layer matrix / clamps. Derived from the game's files: owner folder only.
# STAGE_PHYS2D=0 stages none (the core then runs on Unity's defaults and logs the pack's MISSING line); =<file> copies one.
if [[ ${STAGE_PHYS2D:-1} == 1 ]]; then
  CLI=$TMPDIR/shepherd-assets-cli; dotnet build "$REPO/engine/Shepherd.Assets/Shepherd.Assets.Cli/Shepherd.Assets.Cli.csproj" -c Release -o "$CLI" -nologo -v q > /dev/null \
    || { echo "Shepherd.Assets.Cli did not build (STAGE_PHYS2D=0 stages without the physics settings)"; exit 1; }
  COTL_DATA="${GAME_MANAGED:h}" dotnet "$CLI/Shepherd.Assets.Cli.dll" phys2d "$D/data/physics2d-settings.bin" | sed 's/^/   /'
elif [[ ${STAGE_PHYS2D:-1} != 0 ]]; then
  cp "$STAGE_PHYS2D" "$D/data/physics2d-settings.bin"
fi
cat > "$OUT/COPY-TO-VITA.txt" <<EOF
Copy the folder "shepherd" (next to this file) to the Vita as ux0:data/shepherd/ (VitaShell -> SELECT starts FTP;
e.g. drag it into ux0:/data/ with an FTP client). Keep the names exactly. Then install shepherd_menu.vpk (VitaShell),
start "Shepherd Menu (V6)" and leave the Vita on its charger: the run takes roughly 30-60 minutes and ends with a
PASS/FAIL line on screen. Send back ux0:data/shepherd/menu.txt and the folder ux0:data/shepherd/runtime/logs/.
Each start keeps the previous run's log as ux0:data/shepherd/menu.prev.txt: after a crash or a re-run, send that one too.
Optional: ux0:data/shepherd/env.txt (KEY=VALUE lines), e.g. SHEPHERD_ARENA_MB=170 or MONO_LOG_LEVEL=debug.

managed/Shepherd.Engine.Derived.dll holds the engine's own replay of a third-party start-up scan and its versions of the game's look
(the menu's Stylizer dither, and in gameplay the lighting, light mask and colour grading) -- derived from the game's code and shaders,
so it is kept out of the apps and lives here with the game's DLLs. Copy it along with the rest; without it the apps still work: the
game runs its own full scan, which loads 14 more assemblies and costs more memory (~7 MiB on the Mac sim), and the picture has no
dither pass, lighting or grading (the log then says "no owner-local Shepherd.Engine.Derived").

The milestone app (Stage B), shepherd_menu_gl.vpk = "Shepherd Menu (GL)" (SHPD00008), uses the SAME folder: it draws the
title and the Main Menu with the GPU and reads the Vita's buttons (cross = Space+E, circle = Escape, d-pad/left stick =
W/A/S/D, ...). It needs Sony's shader compiler on the console: ur0:data/libshacccg.suprx, extracted from YOUR OWN Vita
(PSM Runtime 1.00 -> 2.00 -> 2.01, then ShaRKF00D). Never download it; without it the app shows how to get it and exits.
Its log is ux0:data/shepherd/menu_gl.txt (+ menu_gl.prev.txt); its own optional files are env_gl.txt and args_gl.txt.
It runs until you hold START+SELECT for 1.5 s (the screen turns green = PASS or red = FAIL, then it exits). The GL apps run on
the PLAY clock (line T0): the game moves at real speed whatever the frame rate, like the original (Unity's clock: animations,
menus and timers in real time, physics at 50 Hz), and the music and sounds play at their own real-time pace on the audio core.
In play mode pad.txt is NOT applied (your controls only): press a d-pad direction on the menu for the "PASS M1" line.
SHEPHERD_TIME=frame in env_gl.txt / env_gl_audio.txt switches to the deterministic test clock (1/60 s of game time per frame,
so at ~6 fps the game runs at a tenth of its speed) and brings pad.txt back (cross at frame 1050, down at 1400).
Screenshots on request: SHEPHERD_SHOT_FRAMES=1300,1450 in env_gl.txt -> ux0:data/shepherd/shots/.
Send back menu_gl.txt, menu_gl.prev.txt and ux0:data/shepherd/runtime/logs/ (vita/notes/m1-gl-app.md).

The same app WITH SOUND (Stage C), shepherd_menu_gl_audio.vpk = "Shepherd Menu (GL + audio)" (SHPD00010), also uses this folder,
plus audiobake/menu/ (the menu's sounds rendered from your own copy of the game's FMOD banks: copy it along, ~100 MB). It plays the
splash music, the title music and the menu sounds through the Vita's audio output (turn the volume down first if you like). Without
audiobake/menu it runs silent and says so (line A0). Everything else is as above; its files are menu_gl_audio.txt (+ .prev.txt),
env_gl_audio.txt, args_gl_audio.txt. Like the GL app it runs on the play clock (game and sound both in real time, in step);
SHEPHERD_TIME=frame in env_gl_audio.txt = the test clock (the game slows to fps/60 of real time, the sound does not).
Send back menu_gl_audio.txt and ux0:data/shepherd/runtime/logs/ (engine.log has the [audio] lines; vita/notes/m1-audio.md).
EOF
echo "staged $D:"
echo "   managed/    $(ls "$D/managed" | wc -l | tr -d ' ') files, $(du -sh "$D/managed" | cut -f1)  (skipped ${#skipped} BCL-named game DLLs: ${skipped[*]})$([[ -f "$D/managed/Shepherd.Engine.Derived.dll" ]] && echo '; + Shepherd.Engine.Derived.dll')"
echo "   pack/       $(du -sh "$D/pack" | cut -f1)"
echo "   data/       $(cd "$D/data" && find . -type f | sed 's|^\./||' | tr '\n' ' ')"
echo "   persistent/ $(cd "$D/persistent" && find . -type f | sed 's|^\./||' | tr '\n' ' ')"
echo "   pad.txt     $(grep -v '^#' "$D/pad.txt" | tr '\n' ' ')"
[[ $AUDIO = 1 ]] && echo "   audiobake/  $(find "$D/audiobake" -type f | wc -l | tr -d ' ') files, $(du -sh "$D/audiobake" | cut -f1) (the menu audio bake; Shepherd Menu (GL + audio) only)"
[[ -d "$D/cache" ]] && echo "   cache/      $(cd "$D/cache" && find . -type f | sed 's|^\./||' | tr '\n' ' ')(STAGE_CACHE; kit #5)"
echo "   total       $(du -sh "$D" | cut -f1)   -> copy to ux0:data/shepherd/ (instructions: $OUT/COPY-TO-VITA.txt)"
