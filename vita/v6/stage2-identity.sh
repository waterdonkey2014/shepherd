#!/bin/zsh
# V6 stage 2 (vita/notes/engine-pass-speed.md §6): output-identity checks -- the in-process RenderCheck + RegistryCheck, and pre- vs
# post-stage-2 screenshot identity on the same (headless frame-counting) clock for the default SDL path, the GL path and the Vita
# profile on GL + DXT + the strict pack. PRE = the pre-stage-2 engine snapshot ($S/pre-engine, copied before the first stage-2 edit)
# with only the headless clock patched in ($S/pre-engine-vrt: Runtime/Time.cs + Boot.cs, the same lines as the stage-2 engine), and
# (stage-2 review) the same two review hooks as the current engine ($S/pre-engine-vrt2, made here): Runtime/RewiredClock.cs (Rewired's
# realtime on the frame clock; without it Rewired's digital axes ran on the wall clock and a press could move the selection a frame
# later on the fast GL paths) and Diag/ShotPlan.cs (SHEPHERD_SHOT_FRAMES), so both engines walk the same frame timeline.
# Review additions: every frame from press-1 to press+10 is shot as well (dense windows: a press handled one frame late differs
# there and nowhere else); a 10-press navigation script per path (nav*); the current engine runs twice per config (repeatability);
# the nav runs also once with sparse shots only (every 5 frames: a shot slows its frame, which used to hide wall-clock effects).
# Results -> $S/identity/summary.txt. Never touches runtime/persistent (own roots).
export TMPDIR=${SHEPHERD_WORK:-$HOME/shepherd-work}/tmp DOTNET_ROOT=$HOME/.dotnet PATH="$HOME/.dotnet:$PATH" DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
REPO="${SHEPHERD_REPO:-$HOME/src/shepherd}"; S=${STAGE2_DIR:-${SHEPHERD_WORK:-$HOME/shepherd-work}/s2}; O=$S/identity; mkdir -p $O; : > $O/summary.txt
export MACRUN_ROOTS=$S/roots
CUR="$REPO/engine/Shepherd.Engine/bin"; PRE=$S/pre-engine-vrt2-bin; JOBS=${IDENT_JOBS:-4}
echo "muted at start: $(osascript -e 'output muted of (get volume settings)')" >> $O/summary.txt
# PRE: the clock snapshot + the review hooks (the same files / lines as the current engine)
rm -rf $S/pre-engine-vrt2 && rsync -a --exclude bin --exclude obj $S/pre-engine-vrt/ $S/pre-engine-vrt2/ || exit 1
cp "$REPO/engine/Shepherd.Engine/Runtime/RewiredClock.cs" $S/pre-engine-vrt2/Shepherd.Engine/Runtime/
cp "$REPO/engine/Shepherd.Engine/Diag/ShotPlan.cs" $S/pre-engine-vrt2/Shepherd.Engine/Diag/
python3 - "$S/pre-engine-vrt2/Shepherd.Engine" <<'EOF' >> $O/summary.txt || exit 1
import sys, os
d = sys.argv[1]
def patch(rel, old, new):
    p = os.path.join(d, rel); s = open(p).read()
    assert s.count(old) == 1, rel; open(p, 'w').write(s.replace(old, new))
patch('Runtime/Lifecycle.cs', 'Call(m, For(m.Managed.GetType()).Awake, "Awake"); }',
      'Call(m, For(m.Managed.GetType()).Awake, "Awake"); if (Time.VirtualRealtime) RewiredClock.AfterAwake(m.Managed); }')
patch('Boot.cs', 'if (every > 0 && (i % every == 0 || i == 1)) {', 'if ((every > 0 && (i % every == 0 || i == 1)) || Diag.ShotPlan.Extra(i)) {')
print("pre-engine-vrt2: RewiredClock + ShotPlan patched in")
EOF
(cd "$REPO/engine/Shepherd.Engine" && dotnet build -c Debug -o bin -p:DisableTransitiveFrameworkReferenceDownloads=true 2>&1 | grep -E " error |rror\(s\)") >> $O/summary.txt
(cd $S/pre-engine-vrt2/Shepherd.Engine && dotnet build -c Debug -o $PRE -p:DisableTransitiveFrameworkReferenceDownloads=true 2>&1 | grep -E " error |rror\(s\)") >> $O/summary.txt
cd "$REPO"

windows() {   # windows <frame...>: "p-1..p+10" for every press frame
  local -a w; for p in "$@"; do w+=("$((p-1))-$((p+10))"); done; print -r -- "SHEPHERD_SHOT_FRAMES=${(j:,:)w}"
}
VITA=(SHEPHERD_PROFILE=vita SHEPHERD_RENDER=gl SHEPHERD_GL_DXT=1 SHEPHERD_PACK="$REPO/runtime/vitapack/menu" SHEPHERD_PACK_STRICT=1)
NAVF=(1050 1150 1250 1350 1400 1450 1650 1800 1900 2000)
NAVKEYS="SHEPHERD_PRESS=1050:32,1150:274,1250:274,1350:273,1400:274,1450:13,1650:27,1800:274,1900:274,2000:273"
NAVPAD="SHEPHERD_PAD=1050:cross,1150:down,1250:down,1350:up,1400:down,1450:cross,1650:circle,1800:down,1900:down,2000:up"
NP=(); for p in $NAVF; do NP+=($p $((p+1)) $((p+2))); done; NAVPROBE="SHEPHERD_MENU_PROBE=${(j:,:)NP}"

pending=0
job() { "$REPO/vita/v6/macrun.sh" "$@" >> $O/summary.txt & pending=$((pending+1)); (( pending % JOBS == 0 )) && wait; }
cmpshots() {   # cmpshots <tag>: pre vs cur, cur vs cur2 (and sparse cur vs pre when present)
  local tag=$1 a b
  for a b in pre cur cur cur2 pre sparse; do
    [[ -d $S/roots/id-$tag-$a && -d $S/roots/id-$tag-$b ]] || continue
    local n=0 d=0 list=""
    for f in $S/roots/id-$tag-$a/runtime/shots/boot/*.png(N); do
      [[ -f $S/roots/id-$tag-$b/runtime/shots/boot/${f:t} ]] || continue
      n=$((n+1)); cmp -s $f $S/roots/id-$tag-$b/runtime/shots/boot/${f:t} || { d=$((d+1)); list+=" ${${f:t:r}#f}"; }
    done
    echo "$tag $a vs $b: byte-identical screenshots $((n-d))/$n${list:+ (differ:$list)}" >> $O/summary.txt
  done
}

job id-rc "$CUR" SHEPHERD_RENDER_CHECK=1 SHEPHERD_CHECK_REG=1 SHEPHERD_PRESS=1050:32 -- boot 1460 100
for v in pre cur cur2; do bin=$CUR; [[ $v == pre ]] && bin=$PRE
  job id-sdl-$v $bin SHEPHERD_PRESS=1050:32 "$(windows 1050)" -- boot 1400 50
  job id-gl-$v $bin SHEPHERD_RENDER=gl SHEPHERD_PRESS=1050:32 "$(windows 1050)" -- boot 1400 50
  job id-vitagl-$v $bin $VITA "SHEPHERD_PAD=1050:cross,1400:down" "$(windows 1050 1400)" -- boot 1460 50
  job id-navsdl-$v $bin $NAVKEYS "$(windows $NAVF)" -- boot 2150 5
  job id-navgl-$v $bin SHEPHERD_RENDER=gl $NAVKEYS "$(windows $NAVF)" -- boot 2150 5
  job id-navvitagl-$v $bin $VITA $NAVPAD $NAVPROBE "$(windows $NAVF)" -- boot 2150 5
done
job id-navgl-sparse $CUR SHEPHERD_RENDER=gl $NAVKEYS -- boot 2150 5
job id-navvitagl-sparse $CUR $VITA $NAVPAD $NAVPROBE -- boot 2150 5
wait
grep -E "\[regcheck\]|\[rendercheck\]|\[rewired-clock\]" $S/roots/id-rc/runtime/logs/engine.log | sed 's/^\[ *[0-9]*\] *//' >> $O/summary.txt
for t in sdl gl vitagl navsdl navgl navvitagl; do cmpshots $t; done
for r in id-navvitagl-pre id-navvitagl-cur id-navvitagl-cur2 id-navvitagl-sparse; do   # the selection on each press frame and the 2 after it
  echo "$r: $(grep -h '\[menu\] frame' $S/roots/$r/runtime/logs/engine.log | sed -E "s/.*frame ([0-9]+): selected='([^']*)'.*/\1:\2/" | tr '\n' ' ')" >> $O/summary.txt
done
diff <(md5 -r $(find runtime/persistent -type f) | sort) $S/persistent-orig.md5 > /dev/null && echo "runtime/persistent unchanged" >> $O/summary.txt || echo "runtime/persistent CHANGED" >> $O/summary.txt
echo "muted at end: $(osascript -e 'output muted of (get volume settings)')" >> $O/summary.txt
echo done >> $O/summary.txt
