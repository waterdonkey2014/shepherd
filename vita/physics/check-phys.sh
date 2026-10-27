#!/bin/zsh
# check-phys.sh -- the physics bar's runner (physics-plan-v2 section 4, "The bar after every stage", item 3). E1 version (Stage 0 made it; E1 =
# the Unity 2022.3 oracle's decisions applied: the losing hypothesis variants are deleted, so no variant loops are left but H-NONALLOC's, which E1
# left undecided; patch P2 joins P1 in the box2d step):
# the steps the later stages need exist and run; the ones that need a later stage say so ("n/a until Stage N") instead of passing silently.
#   1 native   vita/physics/build.sh all: Mac dylib (T1 8/8 against the goldens, FMA scan, B3's link map, the Stage 0 self-test, every T1
#              scenario traced and replayed bit for bit, the prof library), Vita archive + T1 eboot (symbol check, FMA / NEON scan),
#              QEMU's Cortex-A9 (T1 8/8, the self-test's hash = the Mac's), the determinism variants and risk probes, the ASan/UBSan build
#   2 box2d    diff -r of vita/physics/box2d/Box2D against the approved archive (SHEPHERD_BOX2D_ARCHIVE; skipped when absent): exactly the five
#              files of patch P1 (Stage 2), the two of patch P2 (E1: the per-step speed clamps) and the two of patch P5 (Stage 4b: Unity's
#              polygon weld) differ, and their unified diffs equal the committed box2d/P1.diff, box2d/P2.diff and box2d/P5.diff
#              Stage 1 adds: the Unity layer's self-test (S1, Mac = QEMU), B2's link map; Stage 2: S2 (P1's probes, serials, queries; Mac = QEMU);
#              Stage 3: S3 (forces, impulses, H-DAMP, restitution, the clamp, frozen axes, one-sided chains, contact snapshots; Mac = QEMU)
#              Stage 6: S6 (the 3D island-ground ray: the tilted Unity plane's hit bits, culling, primitives, filters, modes, 20,000 +z rays fast =
#              general = brute; Mac = QEMU)
#   3 physcheck  the engine (Release) in `physcheck all` with SHEPHERD_CHECK_PHYS=all, at SHEPHERD_PHYS=0 (the groundwork: settings self-test,
#              ABI, pool, trace writer, unit scenes, the composite oracle), at SHEPHERD_PHYS=1 (Stage 1: + the walk / slide / ghost /
#              door / interpolation / C6 / C15 scenes, render60, the composite oracle: 4 non-stale composites, 12 paths) and at SHEPHERD_PHYS=2
#              (Stage 2: + unit2 -- Stays, the C1 entries, kinematic triggers, C4, exits, two-collider bodies, C9, and E1's block: simulated's
#              deferred Exit, Destroy's Exit inside the call, a re-enable's Exit + Enter, layer changes, contactCaptureLayers -- and the query
#              contract, with E1's TOI casts); unit2 with the listener masks off (events byte-identical);
#              `physcheck room:Base Biome 1` (the owner's install, read only): the AABB-level counts 1,122 / 694 / 629 / 67, the shape level, the bells' fields;
#              Stage 3: SHEPHERD_PHYS=3 (+ unit3: drag 2.5 u, AddForce, restitution, the clamp, the Collision2D, Enter / Stay / Exit, IgnoreCollision,
#              rotation, FreezePositionX, the child hitbox C7, the mirrored swipe C16, H-CHAIN, the force rule over 0-step ticks; E1: the per-step
#              clamps of patch P2, a MovePosition step keeping gravity)
#              Stage 4 step 4a: SHEPHERD_PHYS=4 (`physcheck all` + unit4: the PolygonCollider2D / EdgeCollider2D / CompositeCollider2D path icalls
#              against the Unity 2022.3 oracle's numbers on primitives -- the setters' and getters' contracts, exceptions, raw storage, the shapes
#              queries see, Unity's chain vertex filter (S1's WELD natively), the composite's path space and offset, Synchronous composites, the
#              refused usedByComposite); level 6 runs unit4 too (the levels are cumulative)
#              Stage 4 step 4b: + unit4b at levels 4 and 6 (the oracle's composite paths -- every vertex, start, order -- and polygon shapes --
#              libtess2's pieces with the owner-local decomposer, Unity's weld / collinear removal / snap -- bit for bit) and `physcheck
#              composite` bit for bit at level 4 (the game's 4 non-stale composites: Unity's own m_CompositePaths); the 4b review's unit4br at
#              levels 4 and 6 (pieces / outlines on Y-flipped, tilted, mirrored bodies at the collider's localToWorld image; a Synchronous
#              composite on a moving body not regenerated; the generation memo bounded; useDelaunayMesh counted)
#              Stage 6: SHEPHERD_PHYS=6 (`physcheck all` + unit6: the ground ray through the Unity API -- the GroundCollisionPlane's shape and its
#              float tilt, the mask, ElevatedGround, a BoxCollider, triggers, inside starts, OnGround's early-out, RaycastNonAlloc, the bool form,
#              the lazy sync, sharedMesh cooked at assignment, a mirrored plane, gravity and the default scene, the project's 3D settings, bounds,
#              ClosestPoint, 100,000 rays fast = general = brute), unit6 under H-NONALLOC=found (open after E1), and `physcheck room3d` on
#              Game Biome Intro (the ground plane's pose and hit heights) and Base Biome 1 (stairs / ElevatedGround = the analytic height within
#              1e-6; 10^6 random +z rays fast = general = brute)
#   4 intro    (optional: INTRO_PACK=<the owner-local strict intro pack folder>, INTRO_PAD=<pad file, e.g. walk-wall>) the intro drive at
#              SHEPHERD_PHYS=$INTRO_PHYS (default 2) three times -- with SHEPHERD_CHECK_PHYS=all (0 dirty-hook misses, 0 interest mismatches;
#              level 2: the brute oracle -- trigger pairs and queries without the broadphase -- 0 differences), plain, and with
#              SHEPHERD_PHYS_TRACE -- whose per-50-step pose hashes (SHEPHERD_PHYS_HASH) must be identical; the trace replayed bit for bit on
#              the Mac and on QEMU's Cortex-A9 against the Vita archive (build.sh qemu-replay); level 2 adds the swap A/B (Triggers2D's pair
#              search beside the core, shadow and parity: 0 UNCLASSIFIED differences) and, with INTRO1_GOLDEN=<Stage 1's hash file>, the
#              same drive at level 1 against Stage 1's golden pose hashes
#              with INTRO6=1 the walk-wall drive also at SHEPHERD_PHYS=4 (Stage 4b: Unity's composite outline -- its corner cuts and start -- moves
#              the poses from level 3's; the first differing hash is reported, and with INTRO4_GOLDEN=<file> the hashes must equal it) and at
#              SHEPHERD_PHYS=6 twice (the 2D pose hashes = level 4's: Stage 6 adds only the 3D ray; every ground ray checked fast = general = brute
#              and the scene's sync against the transforms, SHEPHERD_CHECK_PHYS=ray3d) and traced: the trace, 3D scene and rays included, replayed
#              on the Mac and on QEMU's Cortex-A9
#   5 combat   (optional: COMBAT_PAD=<the combat pad: the Mac intro route through the rebirth into the first fight>) the drive at SHEPHERD_PHYS=3
#              (bundles from the owner's install; the Vita profile's pad) twice -- identical per-50-step pose hashes and event logs -- and a third
#              time traced, the trace replayed bit for bit on the Mac and on QEMU's Cortex-A9 (forces, velocity sets, collisions through the traced
#              core API); with COMBAT_GOLDEN=<file> the pose hashes must equal it.
#              Kit #8 (k8-physics = k6-physics merged onto kit #7): physics' two test knobs are gone -- kit #6's engine track has the models
#              (Renderer.material's default instance is on by default; the VideoPlayer keeps Unity's timing with each clip's length from
#              SHEPHERD_VIDEO_LENGTHS): the drive gets COMBAT_VIDEO_LENGTHS (default: a generated test table giving the intro's clip the old
#              knob's 2 s -- a test input, not the clip's real 13 s). And the tests track's deterministic schedule (kit7-tests.md "For the physics
#              workflow"; TEST-ONLY, Mac builds only, never in an eboot): SHEPHERD_TEST_ASTAR=join SHEPHERD_TEST_SPINE=join -- the game's A* path
#              thread and Spine's dispatch thread finish within the frame, the one schedule both switches pin (the combat flake was the
#              original game's own thread timing under the Mac's load); COMBAT_TEST_SWITCHES=0 runs the plain drive (the flaky schedule).
#              COMBAT6=1: also two runs at SHEPHERD_PHYS=6 (the play apps' level since kit #8) with identical pose hashes and events.
#   -          save-booted drives (after D15): n/a until then
# Outputs: vita/physics/out/ (native), runtime/logs/physics/check-phys-*/ (engine runs) -- both git-ignored; build trees under
# SHEPHERD_PHYS_WORK. Silent: no audio anywhere (SHEPHERD_AUDIO=off, SHEPHERD_AUDIO_OUT=null), the Mac's mute state is logged at start
# and end. Nothing is downloaded. Exit code = the number of failed steps.
# env: SHEPHERD_PHYS_WORK (build trees, default ${SHEPHERD_WORK:-$HOME/shepherd-work}/physics1/build -- give a worktree its own), SHEPHERD_BOX2D_ARCHIVE,
#      INTRO_PACK, INTRO_PAD, INTRO_FRAMES (default 4400), CHECK_PHYS_ONLY=<step names> (e.g. "physcheck intro"); INTRO_PHYS, INTRO6,
#      INTRO1_GOLDEN, INTRO4_GOLDEN (Stage 4b: the level-4 pose hashes of the drive), COMBAT_PAD, COMBAT_GOLDEN, COMBAT_VIDEO_LENGTHS,
#      COMBAT_TEST_SWITCHES, COMBAT6 as described above.
set -u
setopt pipe_fail
HERE=${0:A:h}; REPO=${HERE:h:h}
export TMPDIR=${SHEPHERD_PHYS_TMPDIR:-${SHEPHERD_WORK:-$HOME/shepherd-work}/tmp} DOTNET_ROOT=$HOME/.dotnet PATH="$HOME/.dotnet:$PATH" DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
export MSBUILDDISABLENODEREUSE=1 DOTNET_CLI_USE_MSBUILD_SERVER=0
mkdir -p $TMPDIR
OUT=$HERE/out; LOGS=$REPO/runtime/logs/physics; mkdir -p $OUT $LOGS
S=$OUT/check-phys-summary.txt; : > $S
fails=0
say() { print -r -- "$*" | tee -a $S; }
want() { [[ -z "${CHECK_PHYS_ONLY:-}" || " $CHECK_PHYS_ONLY " == *" $1 "* ]]; }
step() {   # step <name> <command...>
  local name=$1; shift; local t=$(date +%s)
  "$@" > $OUT/check-phys-$name.log 2>&1; local rc=$?
  say "$name rc=$rc $(( $(date +%s) - t ))s"
  (( rc )) && { fails=$((fails+1)); say "  ^ FAILED (see $OUT/check-phys-$name.log)"; }
  return $rc
}
say "check-phys (physics stage 6 + E1 + stage 4a, 4b + review; kit #8: merged onto kit #7, libshepphys in the play apps) at $(git -C $REPO rev-parse --short HEAD 2>/dev/null) ($(git -C $REPO branch --show-current 2>/dev/null)); muted at start: $(osascript -e 'output muted of (get volume settings)' 2>/dev/null)"

# 1 native
if want native; then
  step native env SHEPHERD_PHYS_SRC=$HERE zsh $HERE/build.sh all
  grep -hE "T1 SUMMARY arm64:|B3 OK|B3 FAILED|B2 OK|B2 FAILED|S0 RESULT|S1 RESULT|S2 RESULT|S3 RESULT|S6 RESULT|TRACE SUMMARY|check_syms:|QEMU RESULT|S0 qemu hash|S1 qemu hash|S2 qemu hash|S3 qemu hash|S6 qemu hash|san \(ASan" $OUT/check-phys-native.log | sed 's/^/    /' | tee -a $S
fi

# 2 box2d against the archive
if want box2d; then
  A=${SHEPHERD_BOX2D_ARCHIVE:-${SHEPHERD_WORK:-$HOME/shepherd-work}/physics1/box2d/box2d-v2.3.1.tar.gz}
  if [[ -f $A ]]; then
    b2() {
      local d=$TMPDIR/check-phys-b2.$$; rm -rf $d; mkdir -p $d; tar -xzf $A -C $d && shasum -a 256 $A || return 1
      local diffs=$(cd $d/box2d-2.3.1/Box2D && diff -rq Box2D $HERE/box2d/Box2D | sed -E 's|^Files Box2D/([^ ]+) and .*|\1|' | sort | tr '\n' ' ')
      local want="Collision/Shapes/b2PolygonShape.cpp Collision/Shapes/b2PolygonShape.h Dynamics/Contacts/b2Contact.cpp Dynamics/Contacts/b2Contact.h Dynamics/b2ContactManager.cpp Dynamics/b2ContactManager.h Dynamics/b2Island.cpp Dynamics/b2Island.h Dynamics/b2World.cpp "
      print "files differing from the archive: ${diffs:-none}"
      [[ "$diffs" == "$want" ]] || { print "expected exactly P1's five files, P2's two and P5's two"; rm -rf $d; return 1; }
      (cd $d/box2d-2.3.1/Box2D && for f in Dynamics/Contacts/b2Contact.h Dynamics/Contacts/b2Contact.cpp Dynamics/b2ContactManager.h Dynamics/b2ContactManager.cpp Dynamics/b2World.cpp; do
         diff -u --label a/Box2D/$f --label b/Box2D/$f Box2D/$f $HERE/box2d/Box2D/$f; done) > $OUT/p1.diff
      (cd $d/box2d-2.3.1/Box2D && for f in Dynamics/b2Island.h Dynamics/b2Island.cpp; do
         diff -u --label a/Box2D/$f --label b/Box2D/$f Box2D/$f $HERE/box2d/Box2D/$f; done) > $OUT/p2.diff
      (cd $d/box2d-2.3.1/Box2D && for f in Collision/Shapes/b2PolygonShape.h Collision/Shapes/b2PolygonShape.cpp; do
         diff -u --label a/Box2D/$f --label b/Box2D/$f Box2D/$f $HERE/box2d/Box2D/$f; done) > $OUT/p5.diff
      cmp -s $OUT/p1.diff $HERE/box2d/P1.diff; local r=$?; (( r )) && print "the P1 files differ from box2d/P1.diff"
      cmp -s $OUT/p2.diff $HERE/box2d/P2.diff; local r2=$?; (( r2 )) && print "the P2 files differ from box2d/P2.diff"
      cmp -s $OUT/p5.diff $HERE/box2d/P5.diff; local r5=$?; (( r5 )) && print "the P5 files differ from box2d/P5.diff"
      rm -rf $d; return $(( r || r2 || r5 ))
    }
    step box2d b2 && say "    exactly P1's five files, P2's two and P5's two differ from the approved archive ($(cut -c1-16 < <(shasum -a 256 $A))), as box2d/P1.diff, P2.diff and P5.diff record"
  else say "box2d: the archive $A is not on this machine -- skipped"; fi
fi

# the engine (Release) for steps 3-4, into a git-ignored folder of this checkout
BIN=$REPO/engine/Shepherd.Engine/binrel
if want physcheck || want intro; then
  (cd $REPO/engine/Shepherd.Engine && nice dotnet build -c Release -o $BIN -p:DisableTransitiveFrameworkReferenceDownloads=true 2>&1 | grep -E " error |rror\(s\)") || { say "engine build FAILED"; fails=$((fails+1)); }
fi
engine() {   # engine <name> [ENV=VAL ...] -- <args...>: a sandbox root per run (symlinks + a fresh copy of the sandbox saves), silent
  local name=$1; shift; local -a envs; while [[ "$1" != "--" ]]; do envs+=$1; shift; done; shift
  local rt=$TMPDIR/check-phys-root-$name.$$ out=$LOGS/check-phys-$name; rm -rf $rt $out; mkdir -p $rt/runtime/logs $out
  for x in Managed ManagedVita shaders; do [[ -e $REPO/runtime/$x ]] && ln -s $REPO/runtime/$x $rt/runtime/$x; done
  [[ -d $REPO/runtime/derived ]] && cp -R $REPO/runtime/derived $rt/runtime/derived   # kit #8: a snapshot, never the live folder (kit7-tests.md: a Derived DLL rebuilt mid-run made a drive take another game path)
  ln -s $REPO/vita $rt/vita; cp -R $REPO/runtime/persistent $rt/runtime/persistent
  (cd $REPO/engine/Shepherd.Engine && env SHEPHERD_ROOT=$rt SHEPHERD_AUDIO=off SHEPHERD_AUDIO_OUT=null $envs nice dotnet $BIN/Shepherd.Engine.dll "$@" > $out/console.txt 2>&1); local rc=$?
  cp -R $rt/runtime/logs/. $out/; rm -rf $rt
  return $rc
}

# 3 physcheck (level 0: the groundwork; level 1: Stage 1's scenes)
if want physcheck; then
  step physcheck engine physcheck SHEPHERD_CHECK_PHYS=all -- physcheck all
  grep -hE "\[physcheck\] (FAIL|RESULT)" $LOGS/check-phys-physcheck/engine.log | sed 's/^\[ *[0-9]*\] */    level 0: /' | tee -a $S
  step physcheck1 engine physcheck1 SHEPHERD_PHYS=1 SHEPHERD_CHECK_PHYS=all -- physcheck all
  grep -hE "\[physcheck\] (FAIL|RESULT)" $LOGS/check-phys-physcheck1/engine.log | sed 's/^\[ *[0-9]*\] */    level 1: /' | tee -a $S
  step physcheck2 engine physcheck2 SHEPHERD_PHYS=2 SHEPHERD_CHECK_PHYS=all SHEPHERD_PHYS_EVENTLOG=$LOGS/check-phys-u2-events.txt -- physcheck all
  grep -hE "\[physcheck\] (FAIL|RESULT)" $LOGS/check-phys-physcheck2/engine.log | sed 's/^\[ *[0-9]*\] */    level 2: /' | tee -a $S
  # (since E1 the Stage 2 switches have Unity's variant only: no variant runs)
  masks() {
    engine u2masks SHEPHERD_PHYS=2 SHEPHERD_PHYS_EVENTLOG=$LOGS/check-phys-u2-masks-on.txt -- physcheck unit2 || return 1
    engine u2nomasks SHEPHERD_PHYS=2 SHEPHERD_PHYS_NOMASKS=1 SHEPHERD_PHYS_EVENTLOG=$LOGS/check-phys-u2-masks-off.txt -- physcheck unit2 || return 1
    cmp $LOGS/check-phys-u2-masks-on.txt $LOGS/check-phys-u2-masks-off.txt && cmp $LOGS/check-phys-u2masks/physcheck-unit2-events.txt $LOGS/check-phys-u2nomasks/physcheck-unit2-events.txt &&
      print "listener masks on / off: the dispatcher's events and the handlers' log byte-identical"
  }
  step masks masks && say "    listener masks on / off: byte-identical events"
  # Stage 3: level 3 (+ unit3; since E1 the Stage 3 switches have Unity's variant only, and MAXTRANS / MAXROT=per-step run through patch P2)
  step physcheck3 engine physcheck3 SHEPHERD_PHYS=3 SHEPHERD_CHECK_PHYS=all -- physcheck all
  grep -hE "\[physcheck\] (FAIL|RESULT)" $LOGS/check-phys-physcheck3/engine.log | sed 's/^\[ *[0-9]*\] */    level 3: /' | tee -a $S
  # Stage 4 step 4a: level 4 (+ unit4)
  step physcheck4 engine physcheck4 SHEPHERD_PHYS=4 SHEPHERD_CHECK_PHYS=all -- physcheck all
  grep -hE "\[physcheck\] (FAIL|RESULT)" $LOGS/check-phys-physcheck4/engine.log | sed 's/^\[ *[0-9]*\] */    level 4: /' | tee -a $S
  grep -hE "unit4b: (the owner|composites)|unit4br: the body frame" $LOGS/check-phys-physcheck4/engine.log | sed 's/^\[ *[0-9]*\] *\[physcheck\] */    level 4: /' | tee -a $S
  # Stage 4 step 4b: the game's 4 non-stale composites through Unity's pipeline, bit for bit
  if [[ -d "${SHEPHERD_INSTALL_DATA:-${SHEPHERD_GAME_DATA}}" ]]; then
    step composite4 engine composite4 SHEPHERD_PHYS=4 SHEPHERD_CHECK_PHYS=all -- physcheck composite
    grep -hE "\[physcheck\] (FAIL|RESULT|PASS composite oracle)" $LOGS/check-phys-composite4/engine.log | sed 's/^\[ *[0-9]*\] */    level 4: /' | cut -c1-300 | tee -a $S
  else say "composite4: the owner's install is not on this machine -- skipped"; fi
  # Stage 6: level 6 (+ unit6) and unit6 under H-NONALLOC=found (E1 left H-NONALLOC open; H-RAYPOINT=bary is decided)
  step physcheck6 engine physcheck6 SHEPHERD_PHYS=6 SHEPHERD_CHECK_PHYS=all -- physcheck all
  grep -hE "\[physcheck\] (FAIL|RESULT)" $LOGS/check-phys-physcheck6/engine.log | sed 's/^\[ *[0-9]*\] */    level 6: /' | tee -a $S
  for v in NONALLOC=found; do
    step physcheck6-$v engine physcheck6-$v SHEPHERD_PHYS=6 SHEPHERD_CHECK_PHYS=ray3d SHEPHERD_PHYS_H=$v -- physcheck unit6
    grep -hE "\[physcheck\] (FAIL|RESULT)" $LOGS/check-phys-physcheck6-$v/engine.log | sed "s/^\[ *[0-9]*\] */    level 6 H-$v: /" | tee -a $S
  done
  if [[ -d "${SHEPHERD_INSTALL_DATA:-${SHEPHERD_GAME_DATA}}" ]]; then
    for sc in "Game Biome Intro" "Base Biome 1"; do
      n=room3d-${${sc// /}:l}
      step $n engine $n SHEPHERD_PHYS=6 -- physcheck room3d:$sc
      grep -hE "\[physcheck\] (FAIL|RESULT|PASS room3d)" $LOGS/check-phys-$n/engine.log | sed 's/^\[ *[0-9]*\] */    /' | cut -c1-330 | tee -a $S
    done
  else say "room3d: the owner's install is not on this machine -- skipped"; fi
  if [[ -d "${SHEPHERD_INSTALL_DATA:-${SHEPHERD_GAME_DATA}}" ]]; then
    step room engine room SHEPHERD_PHYS=2 -- physcheck room:Base Biome 1
    grep -hE "\[physcheck\] (FAIL|RESULT)|room AABB level \(ser|room shape level|room bells|Stay deliveries" $LOGS/check-phys-room/engine.log | sed 's/^\[ *[0-9]*\] */    room: /' | cut -c1-300 | tee -a $S
  else say "room: the owner's install is not on this machine -- skipped"; fi
fi

# 4 intro (optional, owner-local inputs)
if want intro; then
  if [[ -n "${INTRO_PACK:-}" && -f ${INTRO_PACK}/shepherd.pack && -f ${INTRO_PAD:-/nonexistent} ]]; then
    IP=${INTRO_PHYS:-2}
    IV=(SHEPHERD_PROFILE=vita SHEPHERD_RENDER=gl SHEPHERD_GL_DXT=1 SHEPHERD_PACK=$INTRO_PACK SHEPHERD_PACK_STRICT=1 SHEPHERD_STOP_SCENE= SHEPHERD_PAD_FILE=$INTRO_PAD SHEPHERD_PHYS=$IP)
    intro() {
      local H=$LOGS/check-phys-intro-hash; mkdir -p $H; rm -f $H/*(N)
      engine intro "${IV[@]}" SHEPHERD_CHECK_PHYS=all SHEPHERD_PHYS_HASH=$H/check.txt -- boot ${INTRO_FRAMES:-6100} 0 || return 1
      local L=$LOGS/check-phys-intro/engine.log
      grep -q "sync check: .* 0 MISSED by the dirty hook" $L && grep -q "PhysInterest mismatches 0, PhysDesc mismatches 0" $L || { print "check mode: misses / mismatches"; return 1; }
      if (( IP >= 2 )); then grep -q "brute check: .* 0 differences; queries: .* 0 differences" $L || { print "brute oracle: differences"; return 1; }; fi
      engine intro2 "${IV[@]}" SHEPHERD_PHYS_HASH=$H/plain.txt -- boot ${INTRO_FRAMES:-6100} 0 || return 1
      engine intro3 "${IV[@]}" SHEPHERD_PHYS_HASH=$H/traced.txt SHEPHERD_PHYS_TRACE=$H/drive.sptr -- boot ${INTRO_FRAMES:-6100} 0 || return 1
      cmp $H/check.txt $H/plain.txt && cmp $H/check.txt $H/traced.txt || { print "pose hashes differ between the runs"; return 1; }
      print "pose hashes: $(wc -l < $H/plain.txt) per-50-step hashes identical in 3 runs; the last: $(tail -1 $H/plain.txt)"
      SHEPHERD_PHYS_SRC=$HERE zsh $HERE/build.sh qemu-replay $H/drive.sptr || return 1
      rm -f $H/drive.sptr
      if (( IP >= 2 )); then   # the swap A/B: Triggers2D's pair search beside the core (shadow, then parity): no unclassified difference
        engine introab "${IV[@]}" SHEPHERD_TRIGGERS2D=shadow -- boot ${INTRO_FRAMES:-6100} 0 || return 1   # (E1 deleted KINSLEEP=never / SLEEPSTAY=continue: sleeping kinematic pairs are class 8)
        engine introabp "${IV[@]}" SHEPHERD_TRIGGERS2D=parity -- boot ${INTRO_FRAMES:-6100} 0 || return 1
        grep -h "swap A/B" $LOGS/check-phys-introab/engine.log $LOGS/check-phys-introabp/engine.log | cut -c1-600
        grep -q "UNCLASSIFIED 0" $LOGS/check-phys-introab/engine.log && grep -q "UNCLASSIFIED 0" $LOGS/check-phys-introabp/engine.log || { print "swap A/B: unclassified differences"; return 1; }
        grep -q "4 polygon skin (H-SLOP) 0, 5 Static-type bodies 0" $LOGS/check-phys-introabp/engine.log && grep -q "3 child bodies (H-CHILDSYNC) 0" $LOGS/check-phys-introabp/engine.log || { print "parity: classes 3-5 left"; return 1; }
        if [[ -n "${INTRO6:-}" ]]; then   # Stage 6: the ground ray on the same drive -- the 2D poses unchanged, every ray checked, the trace replayed
          # Stage 4b: levels 4 and 6 run Unity's composite pipeline (the outline's corner cuts and start): level 4 is the reference of level 6
          engine intro4 "${IV[@]}" SHEPHERD_PHYS=4 SHEPHERD_PHYS_HASH=$H/level4.txt -- boot ${INTRO_FRAMES:-6100} 0 || return 1
          if cmp -s $H/level4.txt $H/check.txt; then print "level 4: the pose hashes = level $IP's"
          else print "level 4: the pose hashes = level $IP's through $(( $(cmp $H/level4.txt $H/check.txt | sed -E 's/.* line ([0-9]+).*/\1/') - 1 )) hashes, then differ (Stage 4b: Unity's composite outline); the last: $(tail -1 $H/level4.txt)"; fi
          if [[ -f "${INTRO4_GOLDEN:-}" ]]; then cmp $H/level4.txt $INTRO4_GOLDEN && print "level 4: = the level-4 golden ($(tail -1 $INTRO4_GOLDEN))" || { print "level 4 pose hashes differ from the level-4 golden"; return 1; }; fi
          engine intro6 "${IV[@]}" SHEPHERD_PHYS=6 SHEPHERD_CHECK_PHYS=ray3d SHEPHERD_PHYS_HASH=$H/level6.txt -- boot ${INTRO_FRAMES:-6100} 0 || return 1
          engine intro6t "${IV[@]}" SHEPHERD_PHYS=6 SHEPHERD_PHYS_HASH=$H/level6t.txt SHEPHERD_PHYS_TRACE=$H/drive6.sptr -- boot ${INTRO_FRAMES:-6100} 0 || return 1
          cmp $H/level6.txt $H/level4.txt && cmp $H/level6t.txt $H/level4.txt || { print "level 6 pose hashes differ from level 4's"; return 1; }
          local L6=$LOGS/check-phys-intro6/engine.log
          grep -q "check ray3d: [0-9]* rays re-run on the general path and by brute force: 0 differences; synced colliders against their transforms: 0 misses" $L6 || { print "level 6: ray3d check differences"; return 1; }
          print "level 6: the pose hashes = level 4's; $(grep -ho 'check ray3d: [^;]*; [^;]*' $L6)"
          SHEPHERD_PHYS_SRC=$HERE zsh $HERE/build.sh qemu-replay $H/drive6.sptr || return 1
          rm -f $H/drive6.sptr
        fi
        if [[ -f "${INTRO1_GOLDEN:-}" ]]; then   # level 1 unchanged: Stage 1's golden pose hashes
          engine intro1 "${IV[@]}" SHEPHERD_PHYS=1 SHEPHERD_PHYS_HASH=$H/level1.txt -- boot ${INTRO_FRAMES:-6100} 0 || return 1
          cmp $H/level1.txt $INTRO1_GOLDEN && print "level 1: the per-50-step pose hashes = Stage 1's golden ($(tail -1 $INTRO1_GOLDEN))" || { print "level 1 pose hashes differ from Stage 1's"; return 1; }
        fi
      fi
    }
    step intro intro
    grep -hE "pose hashes:|REPLAY|swap A/B|level 1:|level 4:|level 6:" $OUT/check-phys-intro.log | sed 's/^/    /' | cut -c1-500 | tee -a $S
    grep -hE "\[phys\] (stage 1 world|stage 2 dispatch|brute check)" $LOGS/check-phys-intro2/engine.log $LOGS/check-phys-intro/engine.log | sed 's/^\[ *[0-9]*\] */    /' | cut -c1-400 | tee -a $S
  else say "intro: INTRO_PACK / INTRO_PAD not given (owner-local inputs) -- skipped"; fi
fi

# 5 combat (optional, owner-local pad; the owner's install's bundles)
if want combat; then
  if [[ -f ${COMBAT_PAD:-/nonexistent} ]]; then
    CVL=${COMBAT_VIDEO_LENGTHS:-}
    if [[ -z $CVL ]]; then   # the old knob's 2-s clip as a length table (a test input; the game's own table gives the intro's clip 13 s)
      CVL=$OUT/combat-video-lengths.tsv
      printf '%s\n' "# check-phys combat drive: the intro clip's TEST length (2 s = physics' former SHEPHERD_VIDEO_STANDIN=2 knob), not the game's 13 s" "Intro	60	30/1" > $CVL
    fi
    CTS=(SHEPHERD_TEST_ASTAR=join SHEPHERD_TEST_SPINE=join); [[ ${COMBAT_TEST_SWITCHES:-1} == 0 ]] && CTS=()
    CV=(SHEPHERD_PROFILE=vita SHEPHERD_RENDER=gl SHEPHERD_GL_DXT=1 SHEPHERD_STOP_SCENE= SHEPHERD_PAD_FILE=$COMBAT_PAD SHEPHERD_PHYS=3 SHEPHERD_VIDEO_LENGTHS=$CVL $CTS)
    say "    combat env: ${CV[*]}"
    combat() {
      local H=$LOGS/check-phys-combat-hash; mkdir -p $H; rm -f $H/*(N)
      engine combat "${CV[@]}" SHEPHERD_PHYS_HASH=$H/a.txt SHEPHERD_PHYS_EVENTLOG=$H/a.events -- boot ${COMBAT_FRAMES:-14700} 0 || return 1
      engine combat2 "${CV[@]}" SHEPHERD_PHYS_HASH=$H/b.txt SHEPHERD_PHYS_EVENTLOG=$H/b.events -- boot ${COMBAT_FRAMES:-14700} 0 || return 1
      engine combat3 "${CV[@]}" SHEPHERD_PHYS_HASH=$H/traced.txt SHEPHERD_PHYS_TRACE=$H/drive.sptr -- boot ${COMBAT_FRAMES:-14700} 0 || return 1
      cmp $H/a.txt $H/b.txt && cmp $H/a.txt $H/traced.txt && cmp $H/a.events $H/b.events || { print "the combat drive's pose hashes / events differ between runs"; return 1; }
      print "combat pose hashes: $(wc -l < $H/a.txt) per-50-step hashes identical in 3 runs, events identical in 2; the last: $(tail -1 $H/a.txt)"
      grep -h "\[phys\] stage 3:" $LOGS/check-phys-combat/engine.log | sed 's/^\[ *[0-9]*\] */    /' | cut -c1-400
      grep -hE "\[test-(astar|spine)\]" $LOGS/check-phys-combat/engine.log | sed 's/^\[ *[0-9]*\] */    /' | cut -c1-300
      ! grep -q "Could not load type" $LOGS/check-phys-combat/engine.log $LOGS/check-phys-combat2/engine.log $LOGS/check-phys-combat3/engine.log || { print "SETUP MISMATCH: a type-load failure (a Derived DLL of another engine?)"; return 1; }
      if [[ -f "${COMBAT_GOLDEN:-}" ]]; then cmp $H/a.txt $COMBAT_GOLDEN && print "combat: = the golden ($(tail -1 $COMBAT_GOLDEN))" || { print "combat pose hashes differ from the golden ($(tail -1 $COMBAT_GOLDEN))"; return 1; }; fi
      SHEPHERD_PHYS_SRC=$HERE zsh $HERE/build.sh qemu-replay $H/drive.sptr || return 1
      rm -f $H/drive.sptr
      if [[ -n "${COMBAT6:-}" ]]; then   # kit #8: the play apps' level on the same drive -- deterministic (two runs identical)
        engine combat6 "${CV[@]}" SHEPHERD_PHYS=6 SHEPHERD_PHYS_HASH=$H/l6a.txt SHEPHERD_PHYS_EVENTLOG=$H/l6a.events -- boot ${COMBAT_FRAMES:-14700} 0 || return 1
        engine combat6b "${CV[@]}" SHEPHERD_PHYS=6 SHEPHERD_PHYS_HASH=$H/l6b.txt SHEPHERD_PHYS_EVENTLOG=$H/l6b.events -- boot ${COMBAT_FRAMES:-14700} 0 || return 1
        cmp $H/l6a.txt $H/l6b.txt && cmp $H/l6a.events $H/l6b.events || { print "level 6: the combat drive's pose hashes / events differ between runs"; return 1; }
        print "combat level 6: $(wc -l < $H/l6a.txt) per-50-step hashes identical in 2 runs, events identical; the last: $(tail -1 $H/l6a.txt); vs level 3 $(cmp -s $H/l6a.txt $H/a.txt && echo 'identical' || echo "first differs at hash $(cmp $H/l6a.txt $H/a.txt | sed -E 's/.* line ([0-9]+).*/\1/')")"
      fi
    }
    step combat combat
    grep -hE "combat pose hashes:|combat:|combat level 6:|REPLAY|stage 3:|\[test-(astar|spine)\]|SETUP MISMATCH" $OUT/check-phys-combat.log | sed 's/^/    /' | cut -c1-500 | tee -a $S
  else say "combat: COMBAT_PAD not given (owner-local) -- skipped"; fi
fi

say "n/a: the save-booted drives (they need the owner's save export, D15)"
say "muted at end: $(osascript -e 'output muted of (get volume settings)' 2>/dev/null)"
(( fails )) && say "RESULT: FAILED ($fails step(s))" || say "RESULT: PASS (every step rc=0)"
exit $fails
