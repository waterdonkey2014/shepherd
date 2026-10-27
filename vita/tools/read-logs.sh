#!/bin/zsh
# SHEPHERD DEVICE KIT #8 (2026-10-21) -- copies every Shepherd log from the Vita's memory card to the Mac.
#
# On the Vita: VitaShell -> START -> USB, cable in. Then on the Mac:
#     ${SHEPHERD_WORK:-$HOME/shepherd-work}/kit8/kit/read-logs.sh           (waits up to 60 min for the card; "read-logs.sh 120" waits 120)
#
# It copies into ${SHEPHERD_WORK:-$HOME/shepherd-work}/devicelogs/<date-time>/:
#   - every .txt file in ux0:data/shepherd/ (kit #8: menu_gl-measure.txt, menu-aot.txt, play.txt, play-mt.txt, play-gaot.txt and the launcher
#     apps' play-mt-launcher.txt / play-gaot-launcher.txt; kit #7's too; older
#     kits' logs too, e.g. kit #5's menu_gl-measure-llvm.txt / play-mt-llvm.txt; their .prev.txt, pad.txt, env files, kit #6's
#     vita-settings.txt, kit #7's device-profile.txt and the optional JIT probe's jitprobe.txt + its jitprobe.faults / .state / .count)
#   - the folder ux0:data/shepherd/runtime/logs/ with its sub-folders: each app keeps its engine logs in its own runtime/logs/<app>/
#     (kit #6: menu_gl-measure, menu-aot, play, play-mt, play-gaot: engine.log, simstats, the TSVs,
#     audio-events.tsv ...; kit #8: the JIT app's runtime/logs/play/shepjit-traces.tsv / shepjit-aborts.tsv), so one run of read-logs.sh at
#     the end of the session collects every app's; plus ux0:data/shepherd/shots/ if any
#   - (kit #8) ux0:data/shepherd/save-backups/ (the play apps' copies of each save file before the game replaced it, + pending-restore/: a
#     backup chosen in the Restore row, kept until the next start restores it) and cache/jit-learn.tsv (the JIT app's learning cache)
#   - recent crash dumps: ux0:data/psp2core-*.psp2dmp changed in the last 3 days (into coredumps/)
#   - card-manifest.txt: every file under ux0:data/shepherd/ with its size and time (a listing only, nothing read; it includes cache/: the
#     shader cache and the hook list the apps write) + the free space
# then prints the result lines of each app's log (kit #6: the Derived DLL each app loaded, flagged when not loaded or not the kit's,
# step 5b; kit #5: the H8 runtime line, H1 stacks / H2 IL pool, the STK stack-watch lines, the TOUCH
# lines; engine: [gl] gxp cache, [jsonbake] catalog bake, [Icall] hook list, [blackgc], the boot GC, [sim] startup, the long frames after
# the Main Menu = Settings open / close, Credits, New Game; revision 2: Credits from the HOST log -- its opening time and what happened
# after circle closed it -- because STEP 4's second start overwrites the Credits start's engine log), and adds this pull's runs to the
# version-by-version numbers in the repo
# (vita/tools/perf_history.py add -> vita/perf-history.tsv + .md; it reads the copied folder on the Mac, never the card) with a short
# "vs last version" comparison. It NEVER ejects the card and NEVER writes, deletes or changes anything on it: it only reads.
# Like push-kit.sh it uses only the MEMORY CARD (VitaShell/ + app/ + data/ at its root, no gc/); a mounted game card (cartridge) is named
# and ignored (revision 2: a cartridge is any volume with app/ and no VitaShell/ that also has gc/ or license/ OR an app/<TITLE ID>
# folder such as app/PCSE00089 -- before, one showing only app/ was ignored without a word).
# (Template: vita/tools/read-logs.sh in the repo = device kit #8's script (kit #5's + kit #6's + kit #7's + kit #8's run order and lines); copy
#  it into each new kit and update its kit lines. SHEPHERD_REPO = the repo checkout, default ${SHEPHERD_REPO:-$HOME/src/shepherd}.)
# (kit #8: the perf-history step adds EVERY pull folder of the devicelogs parent, not only this one -- the same run is never counted twice --
#  so a pull that was copied by hand, renamed or whose perf step failed (kit #7's extras pull) is counted at the next pull.)
set -u
WAIT_MIN=${1:-60}
O=${SHEPHERD_DEVICELOGS:-${SHEPHERD_WORK:-$HOME/shepherd-work}/devicelogs}/$(date +%Y%m%d-%H%M%S)
is_card() { [[ -d $1/VitaShell && -d $1/app && -d $1/data && ! -e $1/gc ]]; }
is_cart() { [[ ! -d $1/VitaShell && -d $1/app ]] || return 1; [[ -d $1/gc || -d $1/license ]] && return 0
  local t=($1/app/[A-Z][A-Z][A-Z][A-Z][0-9][0-9][0-9][0-9][0-9](N/)); (( ${#t} )); }
echo "read-logs.sh (Shepherd device kit #8): waiting up to $WAIT_MIN min for the Vita's MEMORY CARD (VitaShell: START -> USB)"
end=$(( $(date +%s) + WAIT_MIN * 60 )); V=; typeset -A told
while [[ $(date +%s) -lt $end ]]; do
  cands=()
  for d in /Volumes/*(N/); do
    if is_card "$d"; then cands+=("$d")
    elif is_cart "$d"; then
      (( ${+told[$d]} )) || { told[$d]=1
        echo "   NOT the memory card: $d looks like a Vita GAME CARD (cartridge: $(ls "$d/app" 2>/dev/null | head -3 | tr '\n' ' ')in app/, no VitaShell/). Nothing is read from it."
        echo "   On the Vita: VitaShell -> START -> Settings -> USB device -> \"Memory Card\", then START -> USB again. (Still waiting.)"; }
    fi
  done
  if (( ${#cands} == 1 )); then V=${cands[1]}; break; fi
  if (( ${#cands} > 1 )); then echo "STOP: more than one volume looks like a Vita memory card: ${cands[*]}"; exit 1; fi
  sleep 2
done
[[ -n $V ]] || { echo "STOP: no Vita memory card within $WAIT_MIN min"; exit 1; }
D=$V/data/shepherd
[[ -d $D ]] || { echo "STOP: $V has no data/shepherd folder"; exit 1; }
mkdir -p "$O"
echo "card at $V -> $O"

# 1. every top-level .txt of ux0:data/shepherd (the apps' logs, .prev logs, state and env files)
n=0; for f in "$D"/*.txt(N); do [[ ${f:t} == ._* ]] && continue; cp -p "$f" "$O/" && n=$((n + 1)); done
echo "   $n .txt file(s) from ux0:data/shepherd/"
# (kit #7) the optional JIT probe's side files (jitprobe.txt is one of the .txt above): the tests a launch died in (faults), the last test
#  entered (state), the launch counter (count)
for f in "$D"/jitprobe.{faults,state,count}(N); do cp -p "$f" "$O/" && echo "   ${f:t} (kit #7: the JIT probe's)"; done
# 2. runtime/logs (engine.log, simstats, TSVs, icall lists ...) and screenshots if any were asked for
[[ -d $D/runtime/logs ]] && { mkdir -p "$O/runtime"; rsync -rt --exclude='._*' "$D/runtime/logs" "$O/runtime/" && echo "   runtime/logs/: $(find "$O/runtime/logs" -type f | wc -l | tr -d ' ') file(s) (per app: $(cd "$O/runtime/logs" && for a in *(N/); do echo -n "$a "; done))"; }
[[ -d $D/shots ]] && { rsync -rt --exclude='._*' "$D/shots" "$O/" && echo "   shots/: $(find "$O/shots" -type f | wc -l | tr -d ' ') file(s)"; }
# (kit #7) the measurement app's device probes write their storage results here (the device profile, device-profile.txt, is one of the .txt above)
[[ -d $D/probes ]] && { rsync -rt --exclude='._*' "$D/probes" "$O/" && echo "   probes/: $(find "$O/probes" -type f | wc -l | tr -d ' ') file(s) (kit #7: the storage read test)"; }
# (kit #8, integration review) save-backups/: the play apps' backups of each save file (read only, like probes/; before, the listing in 5e
#  looked for a folder nothing copied); cache/jit-learn.tsv: the JIT app's learning cache (which hot loops it compiled at once on the 2nd start)
[[ -d $D/save-backups ]] && { rsync -rt --exclude='._*' "$D/save-backups" "$O/" && echo "   save-backups/: $(find "$O/save-backups" -type f | wc -l | tr -d ' ') file(s) (kit #8: the 5 newest per save file + pending-restore/)"; }
[[ -f $D/cache/jit-learn.tsv ]] && { mkdir -p "$O/cache"; cp -p "$D/cache/jit-learn.tsv" "$O/cache/" && echo "   cache/jit-learn.tsv (kit #8: the JIT app's learning cache, $(grep -vc '^#' "$O/cache/jit-learn.tsv") row(s))"; }
# 3. recent crash dumps (the Vita writes them to ux0:data/)
cores=("$V"/data/psp2core-*.psp2dmp(N.m-3))
if (( ${#cores} )); then mkdir -p "$O/coredumps"; for c in $cores; do cp -p "$c" "$O/coredumps/"; done; echo "   coredumps/: ${#cores} crash dump(s) from the last 3 days"
else echo "   no crash dumps from the last 3 days"; fi
# 4. what is on the card now (listing only)
{ echo "# ux0:data/shepherd on $(date '+%Y-%m-%d %H:%M:%S'): size<TAB>mtime<TAB>path"; df -k "$V" | tail -1 | awk '{print "# card free: " int($4/1024) " MiB of " int($2/1024) " MiB"}'
  (cd "$D" && find . -type f ! -name '._*' -print0 | xargs -0 stat -f '%z%t%Sm%t%N' -t '%Y-%m-%d %H:%M:%S' | sed 's#\./##' | sort -t$'\t' -k3); } > "$O/card-manifest.txt"
echo "   card-manifest.txt: $(grep -vc '^#' "$O/card-manifest.txt") files listed; $(sed -n 2p "$O/card-manifest.txt" | sed 's/^# //')"
echo "   cache/ on the card (kit #5: the catalog bake, the shader cache, the hook list): $(awk -F'\t' '$3 ~ /^cache\// {print $3 " (" $1 " B)"}' "$O/card-manifest.txt" | tr '\n' ' ')"

[[ -f $O/vita-settings.txt ]] && echo "   vita-settings.txt (kit #6: the PS Vita Settings rows' saved choices): $(grep -v '^#' "$O/vita-settings.txt" | tr -d '\r' | tr '\n' ' ')"

# 5. the result lines
echo
echo "== result lines (full logs in $O)"
show() { local f=$O/$1; shift; [[ -f $f ]] || return; echo "--- ${f:t} ($(wc -l < "$f" | tr -d ' ') lines, $(stat -f '%Sm' -t '%m-%d %H:%M' "$f"))"; grep -E "$@" "$f" | cut -c1-260 | tail -${SHOW_N:-12}; }
show probe.txt '^(CLK|GPU vglInit|GPU individual|GPU batched|SUMMARY|DONE|FAIL)'
# (kit #7, OPTIONAL X3) the JIT probe, SHPD00040: every launch's header, the owner's go / decline, each test's verdict line, done / STOPPED
SHOW_N=60 show jitprobe.txt '^(=== |WAITING|owner |console:|4th core|block:|second block|nested open|exec while open|\[|NOTE|STOPPED|TIMEOUT)'
show gldiag.txt '^(===|RUN variant|vglInitExtended ->|PASS variant|FAIL|STALL|END variant)'
show hello-diag.txt '^(BEGIN|CLK|M99|PASS|FAIL|STALL nothing)'
# (kit #5: H8 runtime = the AOT->interpreter call-info cache (lever 20) ON / OFF; H1 stacks + H2 IL pool = the memory work (thread stacks,
#  the 4 MiB IL pool = newlib room); STK = the stack watch: the Mono thread's deepest use at the GL programs / CP0 / the end;
#  kit #6: H1 stacks = the sizes from kit #5's STK lines (IL pool 10 MiB), H1 frame clock = the Devolver splash's re-base of the scripted
#  frames (+376 on the frame clock, the measurement apps), H1 engine arguments (the default, re-based) = boot 1836)
K5H='H8 runtime|H1 stacks|H1 frame clock|H1 engine arguments \(the default|H2 partition|H2 IL pool|STK (watch|gl-programs|cp0)|STK end: (the Mono|shepherd-render)|K7 (BOOT|black box (NOT )?written|first-call trace|sampler|TEST)'
# (kit #7 measure: K7 BOOT = the boot numbers of that start -- time to splash, the splash, work left after the splash, total; K7 black
#  box written = a FAIL END / STALL / fatal wrote runtime/logs/<app>/blackbox.txt (NOT written = that dump was skipped: the line says why);
#  K7 first-call trace (H11 / FAIL END) / sampler = the opt-in profiler)
SHOW_N=16; for x in menu.txt menu-aot.txt menu-diag.txt; do show $x "^(BEGIN|CLK|PHASE #17|PASS|FAIL|WARN LIMIT|STALL|OOM|$K5H)"; done; SHOW_N=12
for x in menu_gl.txt menu_gl_audio.txt; do show $x '^(BEGIN|CLK|G0|G1 |FAIL|WARN LIMIT|STALL|PASS M1|PASS END|FAIL END|A0|A1)'; done
# the measurement app (kit #6: LLVM by default; kit #5's A/B partner SHPD00191 is not part of kit #6, its log is shown if present)
SHOW_N=20; for x in menu_gl-measure.txt menu_gl-measure-llvm.txt; do show $x "^(BEGIN|CLK start|T0|G1 vgl|FAIL|WARN LIMIT|STALL|PASS M1|PASS END|FAIL END|$K5H)"; done; SHOW_N=12
# (kit #3 revision 2: FAIL MEM = the memory guard ended the session cleanly; PAD = what the firmware's pad buffer gives + latched taps;
#  WARN newlib / WARN MT = the newlib watch and the MT end guard)
# (kit #4: the game-AOT play app SHPD00095, play-gaot.txt: its H7 line says whether the game modules were registered; H1 env = what
#  env_play.txt set (kit #5: should be none); C3 = the 4th core open or not; TAI = the owner's taiHEN plugins by name; GCG = the pause-menu
#  GC governor)
# (kit #5: TOUCH = the front touch panel: its set-up, what sceTouchPeek returns, the first contacts / latched taps, the TOUCH end line;
#  play-mt-llvm.txt = kit #5's LLVM A/B app SHPD00192 (not in kit #6); play-mt.prev.txt = STEP 4's first start (Credits); kit #6:
#  play-gaot.prev.txt = STEP 5's first start (the PS Vita rows changed))
# (kit #6 toggles: CLK k6 = Settings > Graphics > CPU Speed asked for a clock, with the clock read back; C3 k6 + PIN ... (k6: 4th CPU Core
#  On|Off) = the 4th CPU Core row moved the latency-tolerant workers)
PLAYLOGS=(play.prev.txt play.txt play-mt.prev.txt play-mt.txt play-gaot.prev.txt play-gaot.txt play-mt-launcher.prev.txt play-mt-launcher.txt play-gaot-launcher.prev.txt play-gaot-launcher.txt play-mt-llvm.txt)
SHOW_N=64; for x in $PLAYLOGS; do show $x "^(BEGIN|CLK start|CLK k6|T0|H1 multicore|H1 memory guard|H1 env|H7 game AOT|C3|TAI|PIN|GCG|G1 vgl|A0|A1|PAD read|TOUCH (front|off|read|end|sceTouchPeek|tap)|FAIL|WARN LIMIT|WARN newlib|WARN MT|STALL|PASS M1|PASS END|FAIL END|END audio|$K5H)"; done; SHOW_N=12
for x in $PLAYLOGS; do [[ -f $O/$x ]] && { echo "--- ${x}: the last heartbeat (frame time, newlib slope, fixed steps, audio clock)"; grep '^P f=' "$O/$x" | tail -1 | cut -c1-760; }; done
# (kit #5 revision 2) Credits, from the HOST log of every play start that opened it (STEP 4's first start = play-mt.prev.txt, or play-mt.txt
#  when read-logs.sh ran between STEP 4's two starts): the engine log of that start is overwritten by the next start of the same app, so the
#  host log is where its numbers are. "opening" = the host clock from the last host line before the press to the menu's answer
#  ('Credits' -> 'Scrollbar'): the Credits load plus the few normal frames before the press (the frames are printed); the frames right
#  after the answer can still be slow (vlx 00098: 3 frames, 2.1 s), as in kit #4's ~28-29 s, which were taken the same way. "after circle" = the
#  menu's answer to circle ('Scrollbar' -> 'Credits'), then where the log goes: on to its END line, or it just stops (kit #4 on the device:
#  both Play starts that opened Credits stopped there, between the circle press and ~1 s after the menu came back; cause not known).
credits() { local f=$O/$1; [[ -f $f ]] && grep -q "'Credits' -> 'Scrollbar'" "$f" || return 0
  echo "--- $1: Credits (host clock)"
  awk -v op="'Credits' -> 'Scrollbar'" -v cl="'Scrollbar' -> 'Credits'" '
    function after(s, key) { if (!match(s, key "[0-9.]+")) return -1; return substr(s, RSTART + length(key), RLENGTH - length(key)) + 0 }
    /^(P|M1|CP[0-9a-z]+) f=[0-9]+ wall=[0-9.]+s/ { f = after($0, "f="); w = after($0, "wall="); clocked = 1 }
    !/^(P|M1|CP[0-9a-z]+) f=[0-9]+ wall=[0-9.]+s/ { clocked = 0 }
    $1 == "M1" && index($0, op) { printf "   opening (press -> the menu answer, taken as kit #4 ~28-29 s were): about %.1f s (host line f%d %.1f s -> answer f%d %.1f s, %d frame(s))\n", w - lw, lf, lw, f, w, f - lf; opened++ }
    $1 == "M1" && index($0, cl) { printf "   after circle: the menu answered at f%d, %.1f s\n", f, w; closed++; cw = w }
    /^(PASS|FAIL) END/ { endl = substr($0, 1, 120) }
    clocked { lf = f; lw = w }
    END {
      if (!opened) exit
      if (!closed) printf "   after circle: no answer to circle in the host log (Credits not closed, or the log stopped first); last host clock f%d, %.1f s\n", lf, lw
      else printf "   the log goes on to f%d, %.1f s (%.1f s after the answer to circle)\n", lf, lw, lw - cw
      if (endl != "") print "   END line: " endl
      else print "   NO END line: the log just stops (the app closed by itself, a freeze, or the PS button -- the owner knows which)"
    }' "$f"
}
for x in play.txt play-mt.prev.txt play-mt.txt play-gaot.txt play-mt-launcher.txt play-mt-llvm.txt; do credits $x; done
# the engine logs: kit #8's run order (README-KIT8.txt) names each folder's STEP (the engine log is the app's LAST start since the last pull:
# an extra, when the owner ran one, replaces the main step's -- its host log then is the .prev.txt). In the PLAY apps the [sim] window is frames 1301-1400
# of the session, wherever the owner was then (the README's 2 idle minutes cover it); compare their ms/frame with 00099's only together
# with the fixed steps per frame ([time] wall clock line, P lines' "fixed:"), the play clock runs ~4x more FixedUpdates.
# (kit #5: [gl] gxp cache = the shader cache: STEP 1 "miss ... written", STEP 3 "verify -- 8 of 8 ... byte-identical", from STEP 4 "hit";
#  [jsonbake] = the catalog bake ("the catalog came from the bake"); [Icall] hook list = the hook-list cache; [blackgc] = the black-frame
#  GC at the Main Menu transition (mode scene); the long frames = every frame over 1 s after the Main Menu arrived (simstats-frames.tsv):
#  Settings open / close, Credits open, the New Game load)
for a in menu_gl-measure menu-aot play play-mt play-gaot play-mt-launcher play-gaot-launcher menu_gl-measure-llvm play-mt-llvm play-gaot-readahead; do
  e=$O/runtime/logs/$a/engine.log; [[ -f $e ]] || continue
  case $a in
    menu_gl-measure) echo "--- runtime/logs/$a/engine.log = STEP 1, Measure (00099, LLVM; the frame clock re-based by 376: the Main Menu at f1182, [sim] = frames 1677-1776; then the kit #7 probes) -- or X2's start (the built-in storage test)";;
    menu-aot) echo "--- runtime/logs/$a/engine.log = STEP 2, Menu engine AOT (00011, LLVM; headless; re-based as STEP 1)";;
    play) echo "--- runtime/logs/$a/engine.log = Play (00097, THE JIT APP): STEP 4's start with SHEPHERD_JIT=0 (STEP 3's JIT-on start was pulled before it, in the previous folder) -- or X5's lockstep start";;
    play-mt) echo "--- runtime/logs/$a/engine.log = STEP 5, Play MT (00098, LLVM, kit #7's runtime: the same-kit control): idle 2 min + Settings (look) + rear corners + New Game ([sim] = frames 1301-1400) -- or X3's Frame Pacing start";;
    play-gaot) echo "--- runtime/logs/$a/engine.log = STEP 9, Play game AOT (00095): the PS button / standby start (STEP 6's idle + Settings start: play-gaot.prev.txt's host log) -- or X1's 500 MHz start";;
    play-mt-launcher) echo "--- runtime/logs/$a/engine.log = STEP 7, Play MT launcher (00193): the early logo, then New Game -- or X4's (b) start";;
    play-gaot-launcher) echo "--- runtime/logs/$a/engine.log = STEP 8, Play game AOT launcher (00194): the early logo, idle -- or X4's (c) start";;
    menu_gl-measure-llvm|play-mt-llvm) echo "--- runtime/logs/$a/engine.log = kit #5's LLVM A/B app (not part of kit #6 / #7 / #8: never start it with the new DLLs on the card)";;
    play-gaot-readahead) echo "--- runtime/logs/$a/engine.log = kit #4's STEP 6 (the card read-ahead start; not part of kit #8)";;
  esac
  grep -E '\[vitamem\] boot GC' "$e" | sed 's/^\[ *[0-9]*\] *//' | cut -c1-260 | head -1   # (kit #4 r2: the boot GC -- "-> ~42 MiB" in every start)
  grep -E '\[gl\] gxp cache|\[gl\] [0-9]+ programs|\[jsonbake\]|\[Icall\] hook list|\[gl\] leaf state|icalls: rows from' "$e" | sed 's/^\[ *[0-9]*\] *//' | cut -c1-260 | head -6
  grep -E '\[blackgc\] (f[0-9]+|UnloadUnusedAssets:)' "$e" | sed 's/^\[ *[0-9]*\] *//' | cut -c1-200 | head -3
  grep -E '\[k6\]' "$e" | sed 's/^\[ *\([0-9]*\)\] */f\1 /' | cut -c1-240 | head -16   # (kit #6: Settings > Graphics > PS Vita Settings -- the saved choices at boot, the one 500 MHz check, rows added, changes, the first fps readout; FullScreen Mode greyed / a saved Windowed shown as FullScreen / a refused change; the video lengths table)
  grep -E '\[k7\] (BOOT|P3|performance overlay)|\[spineclip\] totals|\[spinelazy\] (lazy Spine skins|.*totals)|\[strict\]' "$e" | sed 's/^\[ *\([0-9]*\)\] */f\1 /' | cut -c1-260 | head -12   # (kit #7: the boot numbers, P3's suspensions left out, the overlay's end line, Spine's clipping / lazy-skin totals)
  grep -E '\[sim\] +(window|frame wall|render part|logic part|GC pauses|startup)|\[scenestop\]|\[display\]|\[time\] (clock|wall clock)|\[audio\] .*real time|\[audio\] output|\[audio\] sa_vita_out_start|\[audio\] sa_init_opts|cannot start IO thread|\[mt\] (multicore|the render thread)|\[pad\] the host ends' "$e" | sed 's/^\[ *[0-9]*\] *//' | cut -c1-260 | tail -12
  t=${e:h}/simstats-frames.tsv; mf=$(grep -oE 'Main Menu scene present at frame [0-9]+' "$e" | head -1 | grep -oE '[0-9]+$')
  [[ -f $t && -n $mf ]] && echo "   long frames after the Main Menu (f$mf), > 1 s (kit #6: the GC's own stop-the-world pause in it): $(awk -F'\t' -v m=$mf 'NR == 1 {for (i = 1; i <= NF; i++) if ($i == "gc_pause_ms") g = i} NR > 1 && $1 + 0 > m && $2 + 0 > 1000 {printf "f%d %.1f s%s; ", $1, $2 / 1000, g ? sprintf(" (GC %.1f s)", $g / 1000) : ""}' "$t" | cut -c1-400)"
done

# 5a. (kit #7 measure) the black box, the profiler's files and the boot history of each app's runtime/logs/<app>/ folder: blackbox.txt =
#     the last seconds before a FAIL END / STALL / runtime fatal error (written then), blackbox-live.txt = the last state written every
#     5 s (after a crash: the crash's evidence; .prev = the start before), prof.tsv / prof-calls.tsv / trace-*.json = SHEPHERD_K7_PROF /
#     _TRACE runs (rank: python3 <repo>/vita/tools/prof_rank.py prof.tsv; open the trace in chrome://tracing), boot-history.tsv = one
#     row per boot (time to splash, total, work left after the splash)
echo "--- kit #7: black box / profiler / boot history per app (runtime/logs/<app>/)"
for d in $O/runtime/logs/*(N/); do
  # (blackbox-live.txt.tmp: the periodic write is a temp file renamed over the old one; a .tmp left = the app died during that write -- with a
  #  blackbox-live.txt next to it the .txt is the last complete one, alone it is complete; the next start of that app sorts it out)
  fs=(); for f in blackbox.txt blackbox.prev.txt blackbox-live.txt blackbox-live.txt.tmp blackbox-live.prev.txt prof.tsv prof-calls.tsv boot-history.tsv; do [[ -f $d/$f ]] && fs+=($f); done
  fs+=($d/trace-*.json(N:t)); (( ${#fs} )) || continue
  echo "   ${d:t}: ${fs[*]}"
  [[ -f $d/blackbox.txt ]] && echo "      blackbox.txt: $(head -1 $d/blackbox.txt | cut -c1-200)"
  [[ -f $d/boot-history.tsv ]] && echo "      the last boot: $(tail -1 $d/boot-history.tsv | awk -F'\t' '{printf "time to splash %s s, total %s s, work left after the splash %s s (%s clock, %s)", $2, $8, $7, $10, $1}')"
done

# 5b. (kit #6, gpulight review) the owner-local render code: managed/Shepherd.Engine.Derived.dll on the card. Every app logs one
#     '[derived] render' line when it first draws: 'provider api N' = loaded (N = the DLL's render contract; the kit's own DLL reports
#     DERIVED_API below), any other line (WRN / ERR, no DLL, SHEPHERD_DERIVED=off) = NOT loaded: that app ran unlit, ungraded and without
#     the Stylizer pass (a WRN / ERR one: since kit #6 also a
#     host line 'FAIL derived: ...' and a red FAIL END). An older api that the engine still loads (3 = kit #5's DLL) runs, but means the
#     card's DLL is not the kit's (push-kit.sh step 5 checks it). The derived-AOT apps refuse a foreign DLL at start instead: 'Failed to
#     load AOT module 'Shepherd.Engine.Derived' ... doesn't match assembly' + 'FAIL runtime fatal error' in their host log.
DERIVED_API=${SHEPHERD_DERIVED_API:-4}   # kit #6: the render contract of the kit's Derived DLL (Render/DerivedRender.cs Api)
echo "--- the owner-local Derived DLL each app loaded ('[derived] render'; the kit's DLL: api $DERIVED_API)"
dbad=0
for e in $O/runtime/logs/*/engine.log(N); do
  a=${e:h:t}; l=$(grep -E '\[derived\] render' "$e" | sed 's/^\[ *[0-9]*\] *//' | head -1 | cut -c1-220)
  if [[ -z $l ]]; then echo "   $a: (no [derived] render line)"
  elif [[ $l =~ '^\[derived\] render: provider api ([0-9]+) ' ]]; then
    if [[ ${match[1]} != $DERIVED_API ]]; then dbad=$((dbad + 1)); echo "   !! $a: an api-${match[1]} Derived DLL, not the kit's (api $DERIVED_API) -- $l"
    else echo "   $a: $l"; fi
  else dbad=$((dbad + 1)); echo "   !! $a: NOT LOADED (unlit, ungraded, no Stylizer pass) -- $l"; fi   # WRN / ERR, absent, SHEPHERD_DERIVED=off
done
for f in $O/*.txt(N); do
  [[ ${f:t} == card-manifest.txt ]] && continue
  l=$(grep -hE '^FAIL derived|AOT module .?Shepherd\.Engine\.Derived|(WRN|ERR) \[derived\] render' "$f" | head -1 | cut -c1-220)
  [[ -n $l ]] && { dbad=$((dbad + 1)); echo "   !! ${f:t}: $l"; }
done
(( dbad )) && echo "   !! $dbad Derived DLL problem line(s) above: the apps named did not run the owner-local lighting / grading / Stylizer as the kit's DLL" \
  || echo "   ok: no Derived DLL problem in any log"

# 5c. (kit #7, track probes) the Devolver splash: kit #6's device run had no splash time (data/video-lengths.tsv was not on the card:
#     every engine log said VIDEO(0 clip lengths), the measurement apps' H1 line "not re-based"). Per app: the host's H1 frame-clock line
#     (frame-clock apps: "re-based by 376" = the table was read), the engine's Devolver clip line (its length: 6.267 s from the table, or 0 s),
#     and the dark screen it gave: the frames from the clip's Play to its end and their wall time from simstats-frames.tsv (on the play clock
#     the logo's 6.27 s of real time; on the frame clock 376 frames at the measurement app's rate).
echo "--- the Devolver splash (kit #7 check: the table on the card, the H1 re-base line, the dark-screen time)"
sbad=0
for f in $O/*.txt(N); do
  l=$(grep -h '^H1 frame clock' "$f" 2>/dev/null | head -1 | cut -c1-120); [[ -z $l ]] && continue
  w=$(grep -h '^WARN DP .*video-lengths' "$f" | head -1 | cut -c1-140)
  case $l in *"re-based by 376"*) echo "   ${f:t}: $l";; *"play clock"*|*"not re-based: 0 frames ()"*) echo "   ${f:t}: (play clock: no frame re-base) ${w:+-- !! $w}"; [[ -n $w ]] && sbad=$((sbad + 1));;
    *) [[ ${f:t} == *.prev.txt ]] && echo "   ${f:t}: $l" || { sbad=$((sbad + 1)); echo "   !! ${f:t}: $l -- the frame clock was NOT re-based: data/video-lengths.tsv missing or without the Devolver row"; };; esac
done
for e in $O/runtime/logs/*/engine.log(N); do
  a=${e:h:t}; pl=$(grep -h "VideoPlayer.Play 'Devolver" "$e" | head -1); [[ -z $pl ]] && continue
  pf=$(print -r -- "$pl" | sed -E 's/^\[ *([0-9]+)\].*/\1/'); len=$(print -r -- "$pl" | grep -oE 'Unity timing: [0-9.]+ s[^)]*' | head -1)
  ef=$(grep -h "'Devolver[^']*' reached its end" "$e" | head -1 | sed -E 's/^\[ *([0-9]+)\].*/\1/')
  t=${e:h}/simstats-frames.tsv; dark=
  [[ -n $ef && -f $t ]] && dark=$(awk -F'\t' -v a=$pf -v b=$ef 'NR > 1 && $1 + 0 > a && $1 + 0 <= b {s += $2} END {printf "%.2f", s / 1000}' "$t")
  case $len in *"from the length table"*) echo "   $a: Devolver logo f$pf -> f${ef:-?} ($(( ${ef:-$pf} - pf )) frames)${dark:+, the dark screen ${dark} s of wall time}; $len";;
    *) sbad=$((sbad + 1)); echo "   !! $a: Devolver logo $len -- NOT from the table (0 s): data/video-lengths.tsv was not read (the [k6] video lengths line below)"; grep -h '\[k6\] video lengths\|VIDEO(' "$e" | sed 's/^\[ *[0-9]*\] */      /' | cut -c1-200 | head -2;; esac
done
(( sbad )) && echo "   !! $sbad splash problem line(s): copy the kit's data/shepherd/data/video-lengths.tsv to ux0:data/shepherd/data/ (push-kit.sh does it and checks it, step 5b)" \
  || echo "   ok: the splash table was read wherever a log says"

# 5d. (kit #7, track probes) the host's kit #7 lines: the device profile (DP), the clocks (CLK keep = a plugin's launch clock above 444 kept;
#     CLK change = a clock the app did not set, seen by the watchdog; CLK readout = the CPU Clock row's value changed; CLK measured = the ARM
#     clock timed by a 64-add loop -- it shows a clock PSVshell set outside scePower; CLK resume = after a suspension), P3 (SUSP = a PS-button
#     suspend / standby left out of the play clock) and the measurement app's device probes (PRB: memory budget, big pages, storage, NGS, GPU)
echo "--- kit #7 host lines (device profile, clocks, suspend / resume, the measurement app's probes)"
for f in $O/*.txt(N); do
  [[ ${f:t} == (card-manifest|device-profile|vita-settings|pad).txt ]] && continue
  n=$(grep -cE '^(DP|WARN DP|CLK (keep|change|measured|readout|resume)|SUSP|PRB)' "$f" 2>/dev/null); (( n )) || continue
  echo "   ${f:t}:"; grep -hE '^(DP|WARN DP|CLK (keep|change|measured|readout|resume)|SUSP|PRB|WARN PRB)' "$f" | cut -c1-330 | sed 's/^/      /' | head -${SHOW_PRB:-40}
done
[[ -f $O/device-profile.txt ]] && { echo "   device-profile.txt (written at every start of a GL app):"; grep -vE '^#' "$O/device-profile.txt" | tr -d '\r' | cut -c1-200 | sed 's/^/      /' | head -24; }
[[ -f $O/probes/storage.txt ]] && { echo "   probes/storage.txt:"; grep -E '^(speed|measured)' "$O/probes/storage.txt" | tr -d '\r' | cut -c1-240 | sed 's/^/      /'; }

# 5e. (kit #8, track play; vita/notes/kit8-play.md) the host's lines: TOUCH rear = the rear panel's corners as L2 / R2 / L3 / R3 (set-up, the
#     first presses, the end line); SAVE backup / SAVE verify = a save file kept in save-backups/ before the game replaced it, and every save
#     write read back + checked; CLK readout ... MEASURED = the CPU Clock row showed the measured clock (a PSVshellPlus profile). The engine's
#     [k8] lines: the PC-only links hidden, the game's graphics settings, the late input latch (how much fresher the controls were read), the
#     frame-time histogram (play clock; with Frame Pacing on also the locked areas), the kit #8 rows' changes, a restore at the start.
echo "--- kit #8 lines (rear touch, save backups, the measured clock; engine: links, latch, frame times, rows)"
for f in $O/*.txt(N); do
  [[ ${f:t} == (card-manifest|device-profile|vita-settings|pad).txt ]] && continue
  n=$(grep -cE '^(TOUCH rear|N> SAVE|SAVE (backup|verify))|CLK readout .*MEASURED' "$f" 2>/dev/null); (( n )) || continue
  echo "   ${f:t}:"; grep -hE '^(TOUCH rear|N> SAVE|SAVE (backup|verify))|CLK readout .*MEASURED' "$f" | cut -c1-300 | sed 's/^/      /' | head -${SHOW_K8:-30}
done
for e in $O/runtime/logs/*/engine.log(N); do
  n=$(grep -cE '\[k8\]|\[pad\] native (L2|R2|L3|R3)' "$e" 2>/dev/null); (( n )) || continue
  echo "   ${e:h:t}/engine.log:"; grep -hE '\[k8\]|\[pad\] native (L2|R2|L3|R3)' "$e" | sed 's/^\[ *\([0-9]*\)\] */f\1 /' | cut -c1-300 | sed 's/^/      /' | head -${SHOW_K8:-30}
done
[[ -d $O/save-backups ]] && { echo "   save-backups/ (the 5 newest per save file; pending-restore/ = a backup chosen for the next start):"; (cd "$O/save-backups" && find . -type f ! -name '._*' | sed 's#^\./##' | LC_ALL=C sort | while read -r f; do echo "      $(stat -f %z "$f") B  $f"; done) | head -40; }

# 5f. (kit #8, tracks jit / boot / physics) the host's lines -- JIT (the JIT app 00097): J1 = the 2 MiB code block taken before the partition
#     and the ForVM probe (the largest ADDITIONAL block: how much code memory the Vita grants), WARN J1 = the block refused / the probe's memory
#     not all back, C3 / PIN jit-compiler = the compiler thread on the 4th core (or USER_2, and why), J2 = the end (entry points, traces, code
#     KiB used of 2,048, publishing windows and their time), LOCKSTEP RACE = the optional lockstep start's logged races (must be 0 on a first run);
#     boot (the launcher apps 00193 / 00194; device-profile.txt in every app): K8 L / K8 BOOT = the launcher's splash (TIME TO SPLASH, the
#     module read behind the logo), MEM start / MEM k8-released / H2 partition = the memory the engine got (the 314 / 315 question), K8 video =
#     the game's own clips played by the video decoder, K8 boot = the model check / clean boot screen; physics (the play apps): PHYS = the level
#     and the pool block, WARN PHYS = the settings file missing (level 0), END physics = the pool / Box2D high water
echo "--- kit #8 lines (JIT, boot / launcher / real video, physics)"
K8X='^(J1|WARN J1|J2|C3 jit-compiler|PIN jit-compiler|K8 |MEM (start|k8-released)|H2 partition|PHYS|WARN PHYS|END physics|WARN K8)|LOCKSTEP RACE|shepgl_tex_data'
for f in $O/*.txt(N); do
  [[ ${f:t} == (card-manifest|device-profile|vita-settings|pad).txt ]] && continue
  n=$(grep -cE "$K8X" "$f" 2>/dev/null); (( n )) || continue
  echo "   ${f:t}:"; grep -hE "$K8X" "$f" | cut -c1-330 | sed 's/^/      /' | head -${SHOW_K8X:-40}
done
for e in $O/runtime/logs/*/engine.log(N); do
  n=$(grep -cE '\[phys\] bound|\[physics2d\]|\[k8\] (preload|fixed steps)|\[shepjit\]|LOCKSTEP RACE' "$e" 2>/dev/null); (( n )) || continue
  echo "   ${e:h:t}/engine.log:"; grep -hE '\[phys\] bound|\[physics2d\]|\[k8\] (preload|fixed steps)|\[shepjit\]|LOCKSTEP RACE' "$e" | sed 's/^\[ *\([0-9]*\)\] */f\1 /' | cut -c1-300 | sed 's/^/      /' | head -${SHOW_K8X:-24}
done
for t in $O/runtime/logs/*/shepjit-traces.tsv(N) $O/runtime/logs/*/shepjit-aborts.tsv(N) $O/cache/jit-learn.tsv(N); do echo "   ${t#$O/}: $(grep -vc '^#' "$t") row(s) (the JIT's traces / aborts / learning cache)"; done
[[ -f $O/device-profile.txt ]] && { echo "   device-profile.txt (kit #8: the hardware model check and the real firmware):"; grep -E '^(model|firmware)' "$O/device-profile.txt" | tr -d '\r' | cut -c1-220 | sed 's/^/      /'; }

# 6. the numbers, version by version (owner request 2026-10-13): this pull's runs -> vita/perf-history.tsv + .md in the repo, then
#    "vs last version" (idle Main Menu ms / fps, boot, memory, GC). Reads $O only. The two files are left for a commit after a look.
#    (kit #8: every pull folder of the devicelogs parent -- this one and any earlier pull of the session, e.g. the main session's when this is
#    the extras' -- is added; a run already in the table is never counted twice.)
PH=${SHEPHERD_REPO:-${SHEPHERD_REPO:-$HOME/src/shepherd}}/vita/tools/perf_history.py
echo
if [[ -f $PH ]]; then
  echo "== perf history (vita/perf-history.md; the story per version: vita/CHANGELOG.md)"
  python3 "$PH" add "${O:h}" || echo "   (perf history: the tool stopped; the logs are all copied -- later: python3 $PH add ${O:h})"
else
  echo "== perf history: $PH not found (set SHEPHERD_REPO); later: python3 <repo>/vita/tools/perf_history.py add ${O:h}"
fi
echo
echo "DONE: logs in $O (the card is still mounted; nothing on it was changed)"
