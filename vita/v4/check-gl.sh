#!/bin/zsh
# V4 part 2 regression (vita/notes/v4-renderer.md): GL render backend. Builds the engine (Debug), then:
#   rendertest        SDL vs GL parity scene, GPU Stylizer vs CPU kernel, DXT block upload, present copy   (expect ALL PASS)
#   essl100           rendertest again on the core context with the GLSL ES 1.00 prelude, dumping every shader into $TMPDIR:
#                     the dump must equal vita/v4/shaders-essl100/ (tracked) + engine/Derived/shaders-essl100/ (owner-local, when the
#                     owner-local Shepherd.Engine.Derived is built here) byte for byte (the text vitaGL gets), and glslang's ES 1.00
#                     front end must accept every one of those files                       (expect ALL PASS, identical, 16/16 OK (10
#                     tracked + 6 owner-local); without the owner-local assembly 10/10, and rendertest skips the Stylizer checks;
#                     polish round 2 added the grab program: was 10/10; fidelity round 1 the lut and sprite+ADD programs: was 12/12;
#                     fid1-fixup D1 moved the stylizer and lut programs' shaders and dumps to engine/Derived)
#   gl-compare        SHEPHERD_RENDER=compare boot: GL and SDL from the same draw stream, f01350 diff       (expect >8 px ~0.2-0.3%)
#   gl-vita           SHEPHERD_PROFILE=vita + GL + DXT, pad-only boot to DLC at 960x544                     (expect 7 errors, 3 exceptions;
#                     with SHEPHERD_DIET_SCAN=both: the diet's file scan and reflection scan agree -- fid1-fixup round 2)
#   default           no env: 7 errors / 3 exceptions, diet literal scan 1467, f01350 vs the racecheck reference
#                     (polish round 1: was 10/4 and 1456 -> 1464; stage 2: 1464 -> 1472; vita/notes/polish-round1.md;
#                     fidelity round 1: 1472 -> 1499, vita/notes/fidelity-round1.md; fid1-fixup D1: 1499 -> 1468, the derived
#                     render code's literals left Shepherd.Engine; fid1-fixup D4: 1468 -> 1469, "bodyType" -- Triggers2D reads a
#                     Rigidbody2D's body type, an int the diet never drops; fid1-fixup round 2: 1469 -> 1467, "_StencilInfluence" and
#                     "DecalSpriteLighting" left Shepherd.Engine with the grab-tint rule (owner-local IShaderLooks; neither names a
#                     property-bag key: the dropped-property lists are unchanged), and the scan now reads the engine's assembly file
#                     instead of reflecting over it (Assets/IlLiterals.cs: the same strings, SHEPHERD_DIET_SCAN=both checks it); the
#                     reference carries the Stylizer: without the owner-local assembly f01350 differs)
# Logs + screenshots -> runtime/logs/v4r/check-*/. Headless, offline. ~75 s.
# Stale shaders-essl100/ after a shader edit: regenerate it with the essl100 env below + SHEPHERD_GL_DUMP_SHADERS=$V4/shaders-essl100.
V4="${0:A:h}"; REPO="${V4:h:h}"; R="$REPO/runtime"; ENG="$REPO/engine/Shepherd.Engine"
export TMPDIR=${SHEPHERD_TMPDIR:-${SHEPHERD_WORK:-$HOME/shepherd-work}/tmp} DOTNET_ROOT=$HOME/.dotnet PATH="$HOME/.dotnet:$PATH" DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
mkdir -p $TMPDIR
(cd "$ENG" && dotnet build -c Debug -o bin -p:DisableTransitiveFrameworkReferenceDownloads=true 2>&1 | grep -E " error |rror\(s\)") || exit 1
DER=$(zsh "$REPO/vita/shepherd/build-derived.sh") || exit 1   # the owner-local render code -> runtime/derived (or "absent")
DERESSL="$REPO/engine/Derived/shaders-essl100"; [[ -f "$DER" ]] || DERESSL=
echo "    owner-local Shepherd.Engine.Derived: $DER"
L="$R/logs/v4r"
"$V4/run1.sh" check-rendertest -- rendertest
grep -E "\[rendertest\] (ALL PASS|[0-9]+ FAILED)" "$L/check-rendertest/engine.log" | tail -1 | sed 's/^\[ *[0-9]*\] */    /'
ESD="$TMPDIR/check-gl-essl100"; rm -rf "$ESD"
"$V4/run1.sh" check-essl100 SHEPHERD_RENDER=gl SHEPHERD_GL_CORE=1 SHEPHERD_GL_DIALECT=essl100 "SHEPHERD_GL_DUMP_SHADERS=$ESD" -- rendertest
grep -E "\[rendertest\] (ALL PASS|[0-9]+ FAILED)" "$L/check-essl100/engine.log" | tail -1 | sed 's/^\[ *[0-9]*\] */    /'
EXP="$TMPDIR/check-gl-essl100-expected"; rm -rf "$EXP"; mkdir -p "$EXP"; cp "$V4"/shaders-essl100/* "$EXP/"; [[ -n "$DERESSL" ]] && cp "$DERESSL"/* "$EXP/"
if diff -r "$ESD" "$EXP" > "$L/check-essl100/dump.diff" 2>&1; then echo "    shaders-essl100: identical to a fresh dump ($(ls "$ESD" | wc -l | tr -d ' ') files: $(ls "$V4/shaders-essl100" | wc -l | tr -d ' ') tracked + $([[ -n "$DERESSL" ]] && ls "$DERESSL" | wc -l | tr -d ' ' || echo 0) owner-local)"
else echo "    shaders-essl100: STALE, differs from a fresh dump ($(grep -cE '^(diff -r|Only in)' "$L/check-essl100/dump.diff") files; $L/check-essl100/dump.diff)"; fi
rm -rf "$EXP"
GV=$(command -v glslangValidator || echo /opt/homebrew/bin/glslangValidator)
if [[ -x "$GV" ]]; then
  gn=0; gok=0
  DFILES=(); [[ -n "$DERESSL" ]] && DFILES=("$DERESSL"/*.(vert|frag)(N))
  for f in "$V4"/shaders-essl100/*.(vert|frag)(N) $DFILES; do
    gn=$((gn+1))
    if "$GV" -S ${f##*.} "$f" > "$L/check-essl100/glslang-${f:t}.txt" 2>&1; then gok=$((gok+1)); else echo "    glslang REJECTS ${f:t}: $(grep ERROR "$L/check-essl100/glslang-${f:t}.txt" | head -2 | tr '\n' ' ')"; fi
  done
  echo "    glslang ES 1.00 ($("$GV" --version | head -1 | sed 's/^Glslang Version: //')): $gok/$gn files OK"
else echo "    glslang: glslangValidator not found, skipped"; fi
"$V4/run1.sh" check-gl-compare SHEPHERD_RENDER=compare SHEPHERD_PRESS=1050:32 -- boot 1350 50
grep "\[compare\] f01350" "$L/check-gl-compare/engine.log" | sed 's/^\[ *[0-9]*\] */    /'
grep "\[render\] frame 1350" "$L/check-gl-compare/engine.log" | sed 's/^\[ *[0-9]*\] */    /' | cut -c1-160
"$V4/run1.sh" check-gl-vita SHEPHERD_PROFILE=vita SHEPHERD_RENDER=gl SHEPHERD_GL_DXT=1 "SHEPHERD_PAD=1050:cross,1400:down" SHEPHERD_DIET_SCAN=both -- boot 1460 50
python3 -c "from PIL import Image; im=Image.open('$L/check-gl-vita/f01450.png'); print('    f01450', im.size, '(expect (960, 544), DLC selected: look at it)')"
# fid1-fixup round 2: the diet's literal scan reads the assembly file (Assets/IlLiterals.cs); this run also does the old reflection scan
grep "\[diet\] literal scan check" "$L/check-gl-vita/engine.log" | sed 's/^\[ *[0-9]*\] */    /;s/$/ (expect: agree)/' | cut -c1-200
grep "resident textures" "$L/check-gl-vita/engine.log" | sed 's/^\[ *[0-9]*\] *\[render\] */    /' | cut -c1-200
"$V4/run1.sh" check-default SHEPHERD_PRESS=1050:32 -- boot 1400 50
grep -c " EXC " "$L/check-default/engine.log" | sed 's/^/    EXC lines: /;s/$/ (expect 2)/'
# 1470 since the integration branch (was 1467 on main + fid1-fixup): +1 devfail1 03f12c3 (MemReport's "JIT: n/a ..." line), +2
# menu-speed 1cce421 (MemReport's two "... skipped: SHEPHERD_MT_RENDER=1" lines) -- log text, not bag keys (the scan over-approximates)
grep "readable property keys" "$L/check-default/engine.log" | sed 's/^\[ *[0-9]*\] */    /;s/$/ (expect 1470)/'
python3 "$V4/imgcmp.py" "$L/check-default/f01350.png" "$R/logs/racecheck/f01350-run3.png" | sed 's/^/    /'
