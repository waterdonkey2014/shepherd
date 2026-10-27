#!/bin/zsh
# SHEPHERD-VITA Stage B Mac rehearsal of the GL app (vita/notes/m1-gl-app.md): the Vita host (vita/shepherd/host.c) built with
# SHEPHERD_GL, run on Microsoft's osx-arm64 Mono 8.0.29 (INTERPRETER, coop) with the device folder layout -- the closest Mac
# equivalent of shepherd_menu_gl.vpk:
#   Vita profile, strict menu pack, the staged owner folder, the pad script (pad.txt) AND the native pad path (MAC_CTRL below),
#   GlBackend bound through vita/gltest/shepgl's API (shepgl_mac.c: a CGL legacy context, a 960x544 stand-in display FBO) with
#   the GLSL ES 1.00 dialect (the Mac shim rewrites only the "#version 100" / default-precision prelude for Apple's compiler),
#   no SDL in the process, presents every frame, screenshots by glReadPixels into ux0:data/shepherd/shots/.
# Checks (exit 0 = all):
#   1. menu_gl.txt: "PASS M1" (the selected button changed after a pad press) and "PASS END" (frames shown > 0, no FAIL line);
#      at least 2 menu responses (the scripted down at 1400 and the native down at 1500); START+SELECT ended the boot
#   2. engine.log: the shepgl host line (float ABI probe 51.75, dialect essl100), DXT upload on, 8 programs compiled when the staged
#      owner folder holds the owner-local Shepherd.Engine.Derived.dll, else 5 (its stylizer, stylizer+TWO_PALETTES and lut shaders are
#      owner-local; fid1-fixup D1) (polish round 1 stage 2 added the optional grab program: was 5; fidelity round 1 the optional lut and
#      sprite+ADD: was 6); the shader text handed to shepgl == vita/v4/shaders-essl100 (+ engine/Derived/shaders-essl100 with it)
#   3. no SDL in the process (native census) and the previous log kept as menu_gl.prev.txt
#   4. frames f00001..f01450 vs two GL references through SDL's GL context and the glsl120 dialect (made here unless given):
#      A = the same boot on the same Mac Mono interpreter (MAC_GL_REF_MONO), B = on CoreCLR (MAC_GL_REF, runtime/logs/v4r/m1-gl-ref);
#      each frame's ">8 levels" share <= MAC_GL_TOL (0.5 %); for B outside the store banner (it diverges between runtimes)
#   5. a negative run without libshacccg.suprx: the G0 message and exit 1, before any runtime starts
# Clock: the GL apps default to the wall clock (kit3 play mode, host.c PLAY CLOCK); this rehearsal compares frames with frame-clock
#   references, so its env file sets SHEPHERD_TIME=frame (MAC_TIME=wall: the play clock, for a look -- the frame checks then fail).
# Silent: the Vita profile's FMOD is the engine's stub (no audio device is opened). Derived data stays on the build volume.
# MAC_AUDIO=1 (Stage C, vita/notes/m1-audio.md): the same rehearsal of the GL+AUDIO app (shepherd_menu_gl_audio.vpk): host.c with
#   SHEPHERD_AUDIO, libshepaudio.a (the Mac build: no device backend at all) + the audio pool linked in, the bake under
#   ux0:data/shepherd/audiobake/menu, the engine bound through the host's shepaudio_proc and the pool -- and, instead of the Vita's
#   sceAudioOut, SHEPHERD_AUDIO_OUT=file: (a float WAV under runtime/audiobake/_engine/; NEVER a device). Extra checks: the A0/A1/END
#   audio lines, the host binding + pool in engine.log, the WAV = the sim boot's WAV (vita/audio/engine-check.sh: a.wav) bit for bit
#   over the frames both rendered, engine_wav_check's envelope/clipping/STOPPED checks, no bridge leak.
set -euo pipefail
HERE=${0:a:h}
S=${HERE:h}
V=${S:h}
REPO=${V:h}
export TMPDIR=${TMPDIR:-${SHEPHERD_WORK:-$HOME/shepherd-work}/tmp}
export DOTNET_ROOT=$HOME/.dotnet PATH="$HOME/.dotnet:$PATH" DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
R=${SHEPHERD_RUNTIME:-${SHEPHERD_WORK:-$HOME/shepherd-work}/runtime}
PUB=${SHEPHERD_V6_PUB:-${SHEPHERD_WORK:-$HOME/shepherd-work}/v6/pubmono}
OWNER=${MAC_OWNER:-${SHEPHERD_WORK:-$HOME/shepherd-work}/v6b/owner/shepherd}
TOL=${MAC_GL_TOL:-0.5}
AUDIO=${MAC_AUDIO:-0}
SFX=gl; [[ $AUDIO = 1 ]] && SFX=gl_audio      # log/env/args names: menu_gl.txt ... or menu_gl_audio.txt ...
OUT=${MAC_OUT:-$REPO/runtime/logs/v6/mac-check-$SFX}
BAKE=$REPO/runtime/audiobake/menu
AUDIO_DEFS=(); AUDIO_LIBS=(); AUDIO_SRCS=()
if [[ $AUDIO = 1 ]]; then
  [[ -f $BAKE/manifest.json ]] || { echo "no audio bake at $BAKE"; exit 1; }
  [[ -f $V/audio/build/mac/libshepaudio.a ]] || bash "$V/audio/build.sh" mac > /dev/null
  AUDIO_DEFS=(-DSHEPHERD_AUDIO -I"$V/audio/include" -I"$S")
  AUDIO_SRCS=("$S/audio_pool.c")
  AUDIO_LIBS=("$V/audio/build/mac/libshepaudio.a" -framework AudioToolbox -framework CoreFoundation)
fi
[ -f "$PUB/libcoreclr.dylib" ] || { echo "no Mono publish at $PUB: run vita/v6/sim.sh publish"; exit 1; }
[[ -f "$OWNER/managed/icalls.json" && -f "$OWNER/pack/shepherd.pack" ]] || { echo "no staged owner folder at $OWNER: run vita/shepherd/stage-owner.sh"; exit 1; }
LC_ALL=C grep -aq "shepgl_proc" "$PUB/Shepherd.Engine.dll" \
  || { echo "the Mono publish at $PUB predates the shepgl host: run vita/v6/sim.sh publish"; exit 1; }
T=$(mktemp -d "$TMPDIR/shepherd-maccheck-gl.XXXXXX")
[[ ${KEEP:-0} = 1 ]] || trap 'rm -rf "$T"' EXIT
mkdir -p "$T/include/mono/utils" "$T/app0/managed" "$T/ux0/data/shepherd" "$T/ur0/data"
cp "$R/src/mono/mono/utils/mono-vita.h" "$T/include/mono/utils/"

clang -O1 -g -Wall -Wno-unused-function -Wno-unused-variable -Wno-deprecated-declarations -DSHEPHERD_MAC_CHECK -DSHEPHERD_GL $AUDIO_DEFS \
  -DSHP_BUILD_ID="\"mac-check-$SFX\"" -DSHP_LOG_NAME="\"menu_$SFX.txt\"" -DSHP_LOG_PREV_NAME="\"menu_$SFX.prev.txt\"" -DSHP_ENV_NAME="\"env_$SFX.txt\"" -DSHP_ARGS_NAME="\"args_$SFX.txt\"" \
  -I"$HERE/include" -I"$T/include" -I"$R/src/native/public" -I"$V/gltest/shepgl" -o "$T/shepherd-mac-gl" \
  "$S/host.c" "$HERE/vita_mac_compat.c" "$V/gltest/shepgl/shepgl_mac.c" $AUDIO_SRCS -framework OpenGL $AUDIO_LIBS \
  -L"$PUB" -lcoreclr -Wl,-rpath,"$PUB" -Wl,-export_dynamic

ln -s "$PUB"/*.dll "$T/app0/managed/"
D=$T/ux0/data/shepherd
echo "mac-check-gl: ux0:data/shepherd = the staged owner folder $OWNER"
ln -s "$OWNER/managed" "$D/managed"
mkdir -p "$D/pack"; ln -s "$OWNER/pack/shepherd.pack" "$D/pack/shepherd.pack"
cp -R "$OWNER/data" "$D/data"
cp -R "$OWNER/persistent" "$D/persistent"   # a copy: the run may rewrite the sandbox saves
cp "$OWNER/pad.txt" "$D/pad.txt"
if [[ $AUDIO = 1 ]]; then
  mkdir -p "$D/audiobake"; ln -s "$BAKE" "$D/audiobake/menu"
  WAV=$REPO/runtime/audiobake/_engine/mac-check-gl-audio.wav; rm -f "$WAV"
fi
cat > "$D/env_$SFX.txt" <<EOF
SHEPHERD_ROOT=$D
SHEPHERD_GAME_DATA=$D/data
SHEPHERD_MANAGED=$D/managed
SHEPHERD_PACK=$D/pack
SHEPHERD_PERSISTENT=$D/persistent
SHEPHERD_PAD_FILE=$D/pad.txt
SHEPHERD_SHOT_DIR=$D/shots
SHEPHERD_ARENA_MB=${MAC_ARENA_MB:-272}
SHEPHERD_QUIT_HOLD_MS=100
SHEPHERD_GL_DUMP_SHADERS=$T/shader-dump
MONO_THREADS_SUSPEND=coop
SHEPHERD_IL_DIR=$PUB
SHEPHERD_TIME=${MAC_TIME:-frame}
EOF
# the audio app on the Mac: the bake under the mapped ux0:, the FILE output (paths may hold spaces: one line each, not MAC_EXTRA_ENV)
[[ $AUDIO = 1 ]] && printf 'SHEPHERD_AUDIO_BAKE=%s\nSHEPHERD_AUDIO_OUT=file:%s\n' "$D/audiobake/menu" "$WAV" >> "$D/env_$SFX.txt"
[[ -n ${MAC_EXTRA_ENV:-} ]] && print -l -- ${(s: :)MAC_EXTRA_ENV} >> "$D/env_$SFX.txt"
printf 'boot %s 50\n' ${MAC_FRAMES:-2000} > "$D/args_$SFX.txt"
printf 'mac-check-gl previous-run marker\n' > "$D/menu_$SFX.txt"; printf 'stale\n' > "$D/menu_$SFX.prev.txt"
# the native controls (vita_mac_compat.c MAC_CTRL, one call per engine frame): down at 1500 (after the script's down at 1400),
# then START+SELECT held from 1600 (the host's quit hold is 100 ms here)
CTRL=${MAC_CTRL_SCRIPT:-1500:40:6,1600:9:400}

echo "mac-check-gl: negative run (no libshacccg.suprx)"
(cd "$T" && MAC_APP0="$T/app0" MAC_UX0="$T/ux0" MAC_UR0="$T/ur0" MAC_NATIVE_DIR="$PUB" MAC_CTRL="1:8:50" ./shepherd-mac-gl > "$T/console-neg.txt" 2>&1) && neg=0 || neg=$?
cp "$D/menu_$SFX.txt" "$T/menu_$SFX-neg.txt"
grep -q "^FAIL G0 libshacccg.suprx is missing" "$T/menu_$SFX-neg.txt" && [[ $neg == 1 ]] && ! grep -q "^H3 " "$T/menu_$SFX-neg.txt" \
  && echo "    negative: G0 message, exit 1, no runtime started" || { echo "FAIL mac-check-gl: the negative run did not stop at G0 (exit $neg)"; cat "$T/menu_$SFX-neg.txt"; exit 1; }
# restore the marker for the real run's menu_gl.prev.txt check
printf 'mac-check-gl previous-run marker\n' > "$D/menu_$SFX.txt"; printf 'stale\n' > "$D/menu_$SFX.prev.txt"
printf 'mac-check stand-in (desktop GL compiles GLSL itself; this file only satisfies the presence check)\n' > "$T/ur0/data/libshacccg.suprx"

echo "mac-check-gl: running the GL host on Mac Mono (interpreter), ~2 min"
s0=$(date +%s)
(cd "$T" && MAC_APP0="$T/app0" MAC_UX0="$T/ux0" MAC_UR0="$T/ur0" MAC_NATIVE_DIR="$PUB" MAC_CTRL="$CTRL" ./shepherd-mac-gl > "$T/console.txt" 2>&1) && rc=0 || rc=$?
echo "    host exit $rc after $(( $(date +%s) - s0 )) s"
rm -rf "$OUT"; mkdir -p "$OUT/shots"
cp "$D/menu_$SFX.txt" "$D/menu_$SFX.prev.txt" "$OUT/" 2>/dev/null || true
cp "$T/menu_$SFX-neg.txt" "$OUT/"
cp "$D"/runtime/logs/*(N) "$OUT/" 2>/dev/null || true
cp "$D"/shots/*.png(N) "$OUT/shots/" 2>/dev/null || true
cp "$T/console.txt" "$OUT/host-console.txt"
grep -E "^(BEGIN|H[0-9]+|G[0-9]|A[0-9]|CP[0-9a-z]* f=|CP[0-9a-z]* vgl|M1|PASS|FAIL|WARN|LIMIT|END)" "$OUT/menu_$SFX.txt" | cut -c1-250 || true

fail=0
chk() { if eval "$2"; then echo "ok   $1"; else echo "FAIL $1"; fail=$((fail+1)); fi; }
M=$OUT/menu_$SFX.txt; E=$OUT/engine.log
chk "PASS M1 line (menu on screen + selection changed after a pad press)" "grep -q '^PASS M1 ' \"$M\""
chk "PASS END line" "grep -q '^PASS END ' \"$M\""
chk "2+ menu responses (scripted down f1400 + native down f1500): $(grep -c '^M1 ' "$M") seen" "(( \$(grep -c '^M1 ' \"$M\") >= 2 ))"
chk "a native press reached the menu (pad Down at f1500)" "grep -q '^M1 .*after pad Down at f150' \"$M\""
chk "START+SELECT ended the boot" "grep -q 'START+SELECT held' \"$E\""
chk "previous log kept as menu_$SFX.prev.txt" "grep -qx 'mac-check-gl previous-run marker' \"$OUT/menu_$SFX.prev.txt\""
chk "engine bound GL through shepgl (float ABI probe 51.75)" "grep -q '\[gl\] host shepgl: .*float ABI probe 51.75 ok' \"$E\""
chk "dialect essl100, DXT upload on, 960x544 on the shepgl display" "grep -q '\[gl\] context: .*dialect essl100.*DXT upload ON.*960x544 | host shepgl' \"$E\""
NPROG=5; ESSL=("$V"/v4/shaders-essl100/*)   # the owner-local render code staged with the game's DLLs: its three programs, its dumps
[[ -f "$OWNER/managed/Shepherd.Engine.Derived.dll" ]] && { NPROG=8; ESSL+=("$REPO"/engine/Derived/shaders-essl100/*(N)); }
mkdir -p "$T/essl-expected"; cp $ESSL "$T/essl-expected/"
chk "$NPROG programs compiled and linked (owner-local Derived $([[ $NPROG = 8 ]] && echo staged || echo absent))" "grep -q '\[gl\] $NPROG programs compiled+linked' \"$E\""
chk "the shader text handed to shepgl == vita/v4/shaders-essl100 (+ owner-local dumps; what vita/gltest checks): $(ls "$T/shader-dump" 2>/dev/null | wc -l | tr -d ' ') files" "diff -r \"$T/shader-dump\" \"$T/essl-expected\" > /dev/null"
chk "no SDL in the process (native census: $(grep -c . "$OUT/native-resolve.txt" 2>/dev/null) lines)" "[[ -s \"$OUT/native-resolve.txt\" ]] && ! grep -q 'libSDL' \"$OUT/native-resolve.txt\""
chk "no FAIL line in menu_$SFX.txt" "! grep -q '^FAIL' \"$M\""
if [[ $AUDIO = 1 ]]; then
  cp "$WAV" "$OUT/" 2>/dev/null || true
  chk "A0: the bake is found, A1: the audio pool is allocated" "grep -q '^A0 audio bake present' \"$M\" && grep -q '^A1 audio pool: 8 MiB' \"$M\""
  chk "the engine bound shepaudio through the HOST (shepaudio_proc) with the host's pool, file output" "grep -q '\[audio\] on: .*output file .*binding host shepaudio_proc' \"$E\" && grep -q 'host pool' \"$E\""
  chk "END audio pool line, no failed pool allocation" "grep -qE '^END audio pool: .* 0 failed' \"$M\""
  chk "heartbeats carry the audio numbers" "grep -q '| audio: .* voices' \"$M\""
  chk "no bridge leak at the end" "grep -q 'bridge leaks (released + STOPPED but not destroyed): 0' \"$E\""
  SIMWAV=$REPO/runtime/audiobake/_engine/check/a.wav
  SIMTSV=$REPO/runtime/logs/v6/c-audio-a/audio-events.tsv
  if [[ -f $SIMWAV && -f $SIMTSV ]]; then
    # the sim boot (vita/audio/engine-check.sh: Mono interpreter, null renderer, dylib binding, malloc) vs this host (GL, host
    # binding, the pool): the same audio calls at the same frames, and the same audio up to 1 LSB of int16 -- AudioToolbox's AAC
    # decode (the Mac stand-in for sceAudiodec) differs by 1 LSB on a few samples between the two processes; RAM takes are exact
    cmpres=$(python3 - "$WAV" "$SIMWAV" "$OUT/audio-events.tsv" "$SIMTSV" <<'PYEOF'
import sys, numpy as np
def rd(p):
    b = open(p, 'rb').read(); i = b.index(b'data'); return np.frombuffer(b[i + 8:], dtype=np.float32).reshape(-1, 2)
a, b = rd(sys.argv[1]), rd(sys.argv[2]); n = min(len(a), len(b)) - 800   # the sim stops at f1500; this run presses Down there
d = np.abs(a[:n].astype(np.float64) - b[:n])
rows = lambda p: [l for l in open(p).read().splitlines()[1:] if int(l.split('\t')[0]) < 1500]
same_log = rows(sys.argv[3]) == rows(sys.argv[4])
print(f"{n} {int(np.count_nonzero(d))} {d.max() * 32768:.2f} {1 if same_log else 0} {len(rows(sys.argv[3]))}")
PYEOF
)
    set -- ${=cmpres}
    chk "the audio calls = the sim boot's (event log up to f1499: $5 rows identical: $4)" "[[ $4 == 1 ]]"
    chk "the WAV = the sim boot's within 1 LSB (int16) over $1 frames: $2 samples differ, max $3 LSB (AudioToolbox AAC)" "[[ $1 -gt 1000000 ]] && (( $3 <= 1.0 )) && (( $2 < $1 / 100 ))"
  else echo "note no sim WAV/event log ($SIMWAV): run vita/audio/engine-check.sh first for the comparison"; fi
  python3 "$V/audio/tools/engine_wav_check.py" --manifest "$BAKE/manifest.json" --lib "$V/audio/build/mac/libshepaudio.dylib" --full "$WAV" \
    --events "$OUT/audio-events.tsv" > "$OUT/engine_wav_check.txt" 2>&1; wc=$?
  grep -E "^(ok|FAIL)" "$OUT/engine_wav_check.txt" | sed 's/^/    /'
  chk "engine_wav_check on the rehearsal's WAV (clipping, silence before f121, STOPPED timing)" "[[ $wc == 0 ]]"
fi
# 4. frames vs two GL references, both the same boot (Vita profile, GL + DXT, strict pack, pad 1050:cross 1400:down, frame clock)
#    through SDL's GL context and the glsl120 dialect:
#    A. on the SAME runtime (Mac Mono interpreter, vita/v6/sim.sh run): isolates the new path (host + shepgl + ES dialect): every
#       frame within MAC_GL_TOL (expected byte-identical)
#    B. on CoreCLR (the V4/V3b GL reference, check-pack-strict's configuration): within MAC_GL_TOL outside the main menu's store
#       banner (610,70)-(940,240), whose item carousel diverges between the two runtimes from ~f1250 (the Mono-vs-CoreCLR runs
#       are each deterministic; the CoreCLR reference is byte-identical run to run); the unexcluded numbers are printed too
REFA=${MAC_GL_REF_MONO:-$TMPDIR/m1-gl-ref-mono-shots}
if [[ -z ${MAC_GL_REF_MONO:-} ]]; then
  echo "mac-check-gl: reference A (Mono interpreter, SDL GL context, glsl120) -> $REFA"
  rm -rf "$REFA"
  ro=$(zsh "$V/v6/sim.sh" run m1-gl-ref-mono SHEPHERD_RENDER=gl SHEPHERD_GL_DXT=1 SHEPHERD_SHOT_DIR="$REFA" 2>&1) || true
  print -r -- "${ro%%$'\n'*}" | sed 's/^/    /'
fi
REFB=${MAC_GL_REF:-$REPO/runtime/logs/v4r/m1-gl-ref}
if [[ -z ${MAC_GL_REF:-} ]]; then
  echo "mac-check-gl: reference B (CoreCLR, SDL GL context, glsl120) -> $REFB"
  bo=$(cd "$REPO/engine/Shepherd.Engine" && dotnet build -c Debug -o bin -p:DisableTransitiveFrameworkReferenceDownloads=true 2>&1) || true
  print -r -- "$bo" | grep -q " 0 Error(s)" || { print -r -- "$bo" | grep -E " error " | head -10; echo "FAIL mac-check-gl: engine build"; exit 1; }
  "$V/v4/run1.sh" m1-gl-ref SHEPHERD_PROFILE=vita SHEPHERD_RENDER=gl SHEPHERD_GL_DXT=1 "SHEPHERD_PAD=1050:cross,1400:down" \
    "SHEPHERD_PACK=$REPO/runtime/vitapack/menu" SHEPHERD_PACK_STRICT=1 -- boot 1460 50 | sed 's/^/    /'
fi
cmpset() {   # cmpset <ref dir> <report> [imgcmp option]: frames over TOL -> $over, compared -> $n, identical -> $same, worst -> $worst
  : > "$2"; over=0; n=0; same=0; worst=0
  for f in "$1"/f0*.png(N); do
    local b=${f:t}; [[ -f "$OUT/shots/$b" ]] || continue
    local line=$(python3 "$V/v4/imgcmp.py" "$OUT/shots/$b" "$f" ${3:-}); echo "$line" >> "$2"; n=$((n+1))
    [[ $line == *"meanAbs=0.000 >8=0.000% >24=0.000% max=0 "* ]] && same=$((same+1))
    local p=$(print -r -- "$line" | sed -E 's/.*>8=([0-9.]+)%.*/\1/')
    (( p > worst )) && worst=$p
    (( p > TOL )) && over=$((over+1))
  done
}
cmpset "$REFA" "$OUT/imgcmp-mono.txt"
echo "    A (Mono, SDL GL): $n frames, $same byte-identical, worst >8-level share $worst %, over $TOL %: $over"
chk "frames vs reference A (same runtime, SDL GL context, glsl120) within $TOL %: $n frames" "(( n >= 25 && over == 0 ))"
cmpset "$REFB" "$OUT/imgcmp-coreclr-all.txt"
echo "    B (CoreCLR, SDL GL), whole frame: $n frames, $same byte-identical, worst $worst %, over $TOL %: $over (the store banner carousel)"
cmpset "$REFB" "$OUT/imgcmp-coreclr.txt" --exclude=610,70,940,240
echo "    B (CoreCLR, SDL GL), store banner excluded: $n frames, $same identical, worst $worst %, over $TOL %: $over"
chk "frames vs reference B (CoreCLR) within $TOL % outside the store banner: $n frames" "(( n >= 25 && over == 0 ))"
if (( fail )); then echo "FAIL mac-check-gl ($fail check(s); see $OUT)"; exit 1; fi
echo "PASS mac-check-gl (menu_gl.txt: $M)"
