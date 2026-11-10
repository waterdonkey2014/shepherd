#!/usr/bin/env python3
"""perf_history.py -- the Vita apps' device numbers, one row per app run, version by version (owner request 2026-10-13: "where can we
store a log of what changed from version to version ... so we can see with the numbers what has changed").

Reads device-log folders as read-logs.sh writes them ($SHEPHERD_WORK/devicelogs/<date-time>/) and keeps
  vita/perf-history.tsv   one row per app run (tracked; numbers, app names, title ids, dates, build stamps, commit ids only)
  vita/perf-history.md    the same as readable tables (generated from the TSV; never edit it by hand)
The plain-language story per version is vita/CHANGELOG.md; this tool only does the numbers.

What it reads (top-level host logs + the engine logs; nothing else, never the game's text):
  probe*.txt, gldiag.txt, hello*.txt, menu*.txt (menu.txt, menu-diag, menu-aot, menu_gl, menu_gl_audio, menu_gl-measure ...),
  play*.txt (+ their .prev.txt), and the engine side: runtime/logs/<app>/engine.log (+ simstats.txt), runtime/logs/engine.log,
  logs/engine.log, runtime/logs/simstats.txt, runtime/engine-<app>.log (a hand-copied pull). Lines used: BEGIN (title id, build
  stamp), CLK, PASS / FAIL / STALL / DONE, P and CP heartbeats, MEM, PHASE #1, T0, the probe's IO / GPU lines, gldiag's variants,
  and the engine's [sim] window / heap / startup lines, [pack], [time] and the scene-instantiate times. Game strings in those lines
  (menu labels, scene names, button names) are matched over and never stored.

Columns (empty = not in that log):
  version kit date time folder log title app build commit clock clock_src result frames window
  idle_ms idle_p50 idle_p99 render_ms logic_ms idle_fps idle_wall_ms avg_fps
  boot_menu_s main_at_s menu_frame menu_load_s menu_scene_s
  gc_minor gc_major gc_ms gc_major_max_ms gc_idle_minor_ms gc_full_ms live_mib
  newlib_peak_kib newlib_cap_kib arena_peak_kib arena_kib free_min_kib heap_cap_mib
  card_mibs card_4k_ms pack_mibs gpu_us_draw shown key ekey splash_at_s boot_total_s work_left_s clock_meas clock_note play_ms
  - clock_meas / clock_note (kit #8 fix, the kit #7 lesson: the extras' 500 MHz runs were filed as 444): the ARM clock the app MEASURED
    itself (host "CLK measured" lines, kit #7 on: a 64-add loop -- it sees a clock PSVshellPlus sets outside scePower, which the system's
    report and so "clock" miss): the values in order ("444", "500", "444>500" = a change during the run); clock_note "DIFFER" = the measured
    clock is not the system's report (a PSVshellPlus profile), "changed at 106 s" = the change's time. A comparison uses the MEASURED clock
    when there is one (the effective clock), never gives a percentage across two clocks or for a run whose clock changed, and looks for an
    earlier run at the same clock first. (vlx's "not one of the Vita's clock steps" figures are ignored.)
  - play_ms (kit #8 fix): the engine's [sim] window mean for a run whose window is NOT the idle Main Menu (the play apps: frames 1301-1400
    wherever the owner was; or a window at other frames than the idle menu's) -- such a run has NO idle_* numbers (before kit #8 its window
    mean sat in idle_ms, marked "play", and the tables showed it in the idle-menu column).
  - splash_at_s / boot_total_s / work_left_s (kit #7, the host log's "K7 BOOT" line, Diag/K7.cs; process time, i.e. from the app's
    start): time to the Devolver logo's first frame, to the Main Menu scene (the logo included), and the work left after the logo (the
    real time from its end to the menu beyond the game time the game's own sequence spends there); empty before kit #7.
  - idle_*: (only for a run whose window IS the idle menu: see play_ms) the engine's own idle-Main-Menu window, frames 1301-1400 ([sim] "frame wall", the frame timer; kit #6: + the host's frame-clock
    re-base for the Devolver splash, "H1 frame clock re-based by 376 frames" -> 1677-1776): mean / p50 / p99 and its render / logic
    split; idle_fps = 1000 / idle_ms; idle_wall_ms = the host's wall clock per frame from CP1300 to P f=1400 (100 frames, the last one
    the pad's down press; includes logging and the gaps between frames: 3-15 % above the frame timer, ~10-13 ms per frame -- 456 vs 443
    kit #1, 170 vs 159 GL, 75 vs 65.5 engine AOT). Kit #6 re-based logs have no host line at 1776 (heartbeats every 50 frames,
    checkpoints 0 / 500 / 1426 / 1676): CP1676 -> P f=1750, 74 idle frames before the press (kit #6 review: not the first heartbeat after
    1776, P f=1800, whose window took in 24 frames after the press). window = "menu" (frame clock: the idle Main Menu) or "play" (the
    play apps' play clock: frames 1301-1400 are wherever the owner was -- not comparable with "menu" rows; avg_fps = the [time]
    wall-clock average).
  - boot_menu_s: Main entered -> Main Menu scene present ([sim] startup); main_at_s: launch -> Main entered (diag builds' PHASE #1).
    menu_load_s: the host's wall clock across the heartbeat window that holds the menu's arrival (frames 800 -> 850 on the frame clock;
    kit #6 re-based: 1150 -> 1200), menu_scene_s: the menu scene's instantiate time alone. menu_frame comes from the engine's [sim]
    startup line; a run without it (an early FAIL END, a guard, a hang, a host log alone) on the frame clock gets the measured arrival,
    f806 -- f1182 on a re-based log (806 + the re-base; kit #6 review: was 806 always, inside the Devolver clip there).
  - gc_full_ms: the longer of the two forced full GCs at frame 1300 (idle menu); gc_idle_minor_ms: the minor-GC pause p50 between
    CP1300 and the end (the idle menu). gc_minor / gc_major: the counts at the log's last checkpoint or heartbeat (CPend, else the
    last P line's "gcs N/M"); gc_ms (all pauses) / gc_major_max_ms: from the last CP gc line, left empty when later heartbeats show
    more GCs than it (a log that stops without CPend).
  - Memory in KiB as the host logs it. newlib_peak_kib / arena_peak_kib = the highest use SEEN IN THE LOG: the CP / MEM (and for
    newlib STALL / WARN) peak values and every P heartbeat's in-use value. A log that stops without its end summary (no CPend /
    MEM end: a run stopped with the PS button, a freeze, a crash) gives a lower bound (perf-history.md marks it >=).
  - win_screen / win_sel / win_draws / win_events / win_clock / win_cfg / win_mtdraws / win_ms_x (kit #12 compare audit, 2026-11-04: kit
    #11's X2 window was the title screen and STEP 7's the menu buttons -- a "+11 % menu render" that was the screen, not the switch): WHICH
    SCREEN the [sim] window covered. win_screen = title (nothing selected, steady draws, no press) | menu (a button selected, steady, no press
    but one ON the window's last frame -- Measure's scripted down) | mixed (a pad press or menu response inside the window or up to 70
    frames before it, or the draws moved > 5 %) | other (not the menu's scenes, or a selection outside the button list: a save slot) | "" (not
    seen); a trailing "?" = from the engine's menu probes alone (no host log: a press just before the window end cannot be excluded).
    win_sel = the selected button's INDEX at the window's start > end ("none>none", "b1>b1"; the label itself is never kept); win_draws =
    draws in at each heartbeat in the window ("516/516/516"); win_events = the menu watch's presses / responses near it ("p1282,r1329,
    p1362" = press / response frames; "e1776" = a press on the last frame); win_clock = the MEASURED clock in force across the window ("500";
    "500>444" = a change within 10 s of it: the watchdog needs two 5 s readings; the system's clock when nothing was measured); win_cfg =
    the env file's switches in force (H1 env lines, SHEPHERD_LOG_DIR left out; "-" = none = as shipped; "" = no host log, unknown);
    win_mtdraws = the MT apps' captured world draws per frame over the window ([mt] window line; 510 title / 244 buttons on kit #11);
    win_ms_x = the window mean without the frames that hold a press or a GC (simstats-frames.tsv; Measure's scripted down on its last frame
    cost 50-363 ms on the device). A percentage between two windows needs the same win_screen (title or menu; a menu with the same button
    selected) and the same known win_clock -- comparable() below; otherwise both numbers are printed with the reason and no percentage.
  - clock: arm MHz. clock_src "logged" (CLK lines) or "inferred" (no CLK line: builds before 2026-10-13 ran at the 333 MHz launch
    clock; kit #2+ builds set 444 at start).
  - version / kit / commit: from the build stamp in the BEGIN line (VERSIONS below), else from the probe's own header
    (PROBE_BUILDS: shepherd-probe-2 = the 0.0 test kit's, -3 = kit #2's), else from when the run happened -- a guess (an older build
    re-run later gets the newer kit's version), so such rows get NO commit; nor does a build newer than the last known kit ("0.3+").
  - result: the host log's own (PASS / FAIL = its PASS END / FAIL END line, PASS M1, STALL, CRASH, HUNG, STOPPED = stopped without
    an end line, probe FAIL:<tests>, GL Diag PASS n/m); "PASS*" = only the engine's log was seen and it ends with its normal end
    summary -- the host's PASS END checks (FAIL lines, OOM, heap-cap refusals) were not seen.
  - key: a hash of the run's host log (or of its engine log when that came alone); ekey: the engine run's identity (a hash of its
    start time, else of the file). De-duplication: the same run copied into several folders, re-pulled, or seen once through its host
    log and once through its engine log alone is one row; a later copy with more data fills the empty fields. Two different host
    logs are never one run. An engine log pairs with the host log of its app (runtime/logs/<app>/ <-> <app>.txt or <app>.prev.txt)
    only when its run happened inside that host log's run (dated read-logs.sh folders keep the card's file times: host file time
    minus its last wall value <= the engine log's file time, or its Main entered, <= host file time). host.c does not clear
    runtime/logs/<app>/ and the engine opens its log only in managed Boot, so after a run that ends before Main the card holds the
    new host log next to the previous run's engine log -- that engine log then counts as its own, older run (no commit: its
    version is a run-time guess). In a hand-copied folder (not <date>-<time>: file times are the copy's) names alone pair them.

usage:
  perf_history.py add DIR...        parse folders (or a parent of dated folders), merge into the TSV, rewrite the MD, print the new
                                    rows and a "vs last version" comparison (read-logs.sh runs this after every pull; kit #8: on the
                                    devicelogs PARENT, so every pull folder of a kit -- main + extras -- is in; a run is never counted twice)
  perf_history.py rebuild DIR...    the same from an empty table (the back-fill)
  perf_history.py show              print the comparison of the last two versions
  perf_history.py window DIR...     (kit #12) print, for every host log in the folders, which screen its [sim] window covered (read-logs.sh)
  perf_history.py ab A B            (kit #12) compare two runs' windows (A / B = <pull folder>/<host log>, e.g. 20261104-173425/play-gaot.txt):
                                    the % only when comparable(); then the same-screen frames both runs share (simstats-frames.tsv), so
                                    a pair whose windows landed on different screens is still compared on frames that match
  "vs last version" compares each new main-line run with the latest earlier-version run of the same app (else of the app it grew
  out of) that passed or has an idle window; it prints both clocks and gives no percentage when they differ (333 vs 444 MHz alone
  is ~1.33x); the best idle menu per version is labelled "different apps" when the two best runs are not the same app.
  options: --tsv PATH --md PATH (default: vita/perf-history.tsv / .md next to this tools/ folder), --dry-run (print, write nothing)
Adding a version (a new kit): add its line to VERSIONS below (version, kit, date, main commit, first build stamp, its apps' build
stamps) and its entry to vita/CHANGELOG.md; bump vita/shepherd/VERSION before building it (build.sh puts it in APP_VER)."""
import argparse, calendar, hashlib, os, re, sys, time

HERE = os.path.dirname(os.path.abspath(__file__))
VITA = os.path.dirname(HERE)
DEF_TSV = os.path.join(VITA, "perf-history.tsv")
DEF_MD = os.path.join(VITA, "perf-history.md")

# version scheme (vita/CHANGELOG.md): 0.K = device kit #K; 0.0 = the first test kit. "since" = the first build stamp (UTC, as in
# the BEGIN lines) of that kit's apps; "stamps" = the kit's own builds (a later unknown build gets "<last>+", built after that kit).
VERSIONS = [
    dict(v="0.0", kit="test kit", date="2026-10-07", commit="", since="20261001-000000",
         stamps={"20261006-193949", "20261007-135045"}),
    dict(v="0.1", kit="kit #1", date="2026-10-08", commit="0bddbe5", since="20261008-020000",
         stamps={"20261008-060232", "20261008-060234"}),
    dict(v="0.2", kit="kit #2", date="2026-10-13", commit="37305f7", since="20261013-000000",
         stamps={"20261013-041803", "20261013-075624", "20261013-090621", "20261013-090702", "20261013-090833",
                 "20261013-090923", "20261013-091022"}),
    dict(v="0.3", kit="kit #3", date="2026-10-13", commit="7fa72e2", since="20261013-120000",
         stamps={"20261013-171017", "20261013-171105", "20261013-171202", "20261013-171301"}),
    # kit #4: revision 2 (the review rebuild of d5a3841 -- the kit) + revision 1's five stamps (5f73777; built, never on the card)
    dict(v="0.4", kit="kit #4", date="2026-10-14", commit="d5a3841", since="20261014-011900",
         stamps={"20261014-032823", "20261014-032916", "20261014-033018", "20261014-033121", "20261014-033229",
                 "20261014-011944", "20261014-012036", "20261014-012137", "20261014-012239", "20261014-012345"}),
    # kit #5: built from main f9fb3f3 (= the kit #5 merge c7874ad + VERSION 0.5) in a clean clone; 00099 00011 00097 00098 00095 00191 00192
    dict(v="0.5", kit="kit #5", date="2026-10-15", commit="f9fb3f3", since="20261015-015700",
         stamps={"20261015-015726", "20261015-015819", "20261015-015923", "20261015-020027", "20261015-020136",
                 "20261015-020454", "20261015-020829"}),
    # kit #6: built from main a05e41f (= the kit #6 merge df9e588 + VERSION 0.6) in a clean clone; 00099 00011 00097 00098 00095 (LLVM the
    # default runtime for the first four; 00191 / 00192 not part of kit #6)
    dict(v="0.6", kit="kit #6", date="2026-10-20", commit="a05e41f", since="20261020-091000",
         stamps={"20261020-091035", "20261020-091351", "20261020-091721", "20261020-092052", "20261020-092211"}),
    # kit #7: built from k7-integ a6c0c38 (= the gated kit #7 integration f9822b9 + VERSION 0.7; merged to main after the kit build) in a clean
    # clone, 2026-10-20 local = 2026-10-21 UTC stamps; 00099 00011 00097 00098 00095 (+ the optional JIT probe SHPD00040: no BEGIN line, no row)
    dict(v="0.7", kit="kit #7", date="2026-10-20", commit="a6c0c38", since="20261021-012000",
         stamps={"20261021-012001", "20261021-012355", "20261021-012806", "20261021-013207", "20261021-013329"}),
    # kit #8: built from k8-integ 494db8e (= the gated kit #8 integration a4e06f1 + the integration review's fixes 3244d3d + VERSION 0.8 / the kit
    # scripts; merged to main after the kit build) in a clean clone, 2026-10-21 local = 2026-10-21 22:00-22:17 UTC stamps; 00099 00011 00097
    # (the JIT app) 00098 00095 + the launcher apps 00193 00194
    dict(v="0.8", kit="kit #8", date="2026-10-21", commit="494db8e", since="20261021-215900",
         stamps={"20261021-215956", "20261021-220309", "20261021-220647", "20261021-221037", "20261021-221207",
                 "20261021-221534", "20261021-221703"}),
    # kit #9: built from k9-integ f3b5024 (= the gated kit #9 integration c074ca1 + the integration review's fixes a612f59 + VERSION 0.9 / the kit
    # scripts + main's notes; merged to main after the kit build) in a clean clone, 2026-10-27 local = 14:55-15:01 UTC stamps; 00099 00011
    # 00097 (the JIT app) 00098 00095 + the launcher apps 00193 00194; + the untrimmed alternates alt-1..7 (the same commit, built 15:04-15:12
    # UTC: the CoreLib trim's switch back, installed only if the owner is asked to)
    dict(v="0.9", kit="kit #9", date="2026-10-27", commit="f3b5024", since="20261027-145500",
         stamps={"20261027-145537", "20261027-145934", "20261027-145923", "20261027-145614", "20261027-150112",
                 "20261027-145636", "20261027-145833",
                 "20261027-150456", "20261027-151031", "20261027-151022", "20261027-150538", "20261027-151253",
                 "20261027-150606", "20261027-150855"}),
    # kit #10: built from k10-integ ecea164 (= the gated kit #10 integration c1 8576bc2 + the integration review's fixes f307cb6 (gated as c3) +
    # VERSION 0.10 / the kit scripts ee294ab + the build fix ecea164; merged to main after the kit build) in a clean clone, 2026-10-29 local =
    # 14:54-15:01 UTC stamps; 00011 00099 00097 (the JIT app) 00098 00095 + the launcher apps 00193 00194; + the no-cold alternates alt-1..7
    # (the same commit, 15:07-15:14 UTC: the cold recipe's switch back, only if asked) + the optional learned-AOT 00095 (extra X3, 15:07 UTC)
    dict(v="0.10", kit="kit #10", date="2026-10-29", commit="ecea164", since="20261029-145400",
         stamps={"20261029-145809", "20261029-145451", "20261029-150138", "20261029-145534", "20261029-145735",
                 "20261029-145556", "20261029-145759",
                 "20261029-151120", "20261029-150755", "20261029-151455", "20261029-150841", "20261029-151039",
                 "20261029-150905", "20261029-151105", "20261029-150735"}),
    # kit #11: built from k11-integ afdbbb8 (= the gated kit #11 integration c1 5f8be96 + the integration review's fixes 7783b4a + VERSION 0.11;
    # merged to main after the kit build) in a clean clone, 2026-11-03 local = 11:24-11:33 UTC stamps; 00099 00011 00097 (the JIT app) 00098 00095
    # + the launcher apps 00193 00194 (00095 / 00194 with learned AOT, recipe k10L)
    dict(v="0.11", kit="kit #11", date="2026-11-03", commit="afdbbb8", since="20261103-112400",
         stamps={"20261103-112457", "20261103-112819", "20261103-113222", "20261103-112535", "20261103-112738",
                 "20261103-112557", "20261103-112803"}),
    dict(v="0.12", kit="kit #12", date="2026-11-04", commit="0b72b82", since="20261105-024400",
         stamps={"20261105-024445", "20261105-024521", "20261105-024543", "20261105-033519", "20261105-033532", "20261105-024812", "20261105-025149"}),
    # kit #13: built from k13-integ 0c00e4d (= the gated kit #13 integration c2 b0c8415 + the fix + kit stage 6d20291 / 0c00e4d, VERSION 0.13;
    # merged to main after the kit build) in a clean clone, 2026-11-05 local = 2026-11-06 02:07-02:15 UTC stamps; 00099 00011 00097 (the JIT
    # app) 00098 00095 + the launcher apps 00193 00194 (recipe k12K; 00095 / 00194 with keep-k12gaot)
    dict(v="0.13", kit="kit #13", date="2026-11-05", commit="0c00e4d", since="20261106-020700",
         stamps={"20261106-020818", "20261106-021145", "20261106-021515", "20261106-020857", "20261106-020738", "20261106-021243", "20261106-020943"}),
    # kit 13.1: the hotfix of kit #13, VERSION stays 0.13, the same seven title ids: k13-1 7cbd204 (main + k13-fix + k13-fixlooks), recipe k12K, 2026-11-06 20:54-21:06 UTC stamps
    dict(v="0.13.1", kit="kit 13.1", date="2026-11-06", commit="7cbd204", since="20261106-205400",
         stamps={"20261106-205545", "20261106-205926", "20261106-210309", "20261106-205621", "20261106-205456", "20261106-210023", "20261106-205709"}),
    # kit #14: THE WALK. Built from k14-integ e935733 (= the integration's 22a35ee + VERSION 0.14) in a frozen tree on the integration's runtimes2, recipe k12K +
    # SHEPHERD_K14_DEDUP_SHARED=1 SHEPHERD_K14_LIBTRIM=1, 2026-11-10 09:15-09:22 UTC stamps; the same seven title ids (APP_VER 00.14)
    dict(v="0.14", kit="kit #14", date="2026-11-10", commit="e935733", since="20261110-091500",
         stamps={"20261110-091526", "20261110-091826", "20261110-092131", "20261110-091600", "20261110-091534", "20261110-091911", "20261110-091759"}),
]

# the probe logs no build stamp; its header names the probe's revision, and each revision shipped in one kit only
PROBE_BUILDS = {"shepherd-probe-2": "0.0", "shepherd-probe-3": "0.2", "shepherd-probe-4": "0.4"}   # -4: k4 review (every taiHEN config line)

APPS = {  # title id -> our app name (vita/notes/title-ids.md)
    "SHPD00001": "Probe", "SHPD00005": "Hello (V5)", "SHPD00006": "Menu (V6)", "SHPD00007": "GL test", "SHPD00008": "Menu (GL)",
    "SHPD00009": "Menu (GL engine AOT)", "SHPD00010": "Menu (GL + audio)", "SHPD00011": "Menu (V6 engine AOT)",
    "SHPD00012": "PhysBench", "SHPD00015": "Hello Diag", "SHPD00016": "Menu Diag", "SHPD00030": "GL Diag",
    "SHPD00090": "Menu (V6 derived AOT)", "SHPD00091": "Menu (GL derived AOT)", "SHPD00096": "Menu (GL derived AOT, MT)",
    "SHPD00097": "Play", "SHPD00098": "Play MT", "SHPD00099": "Measure (GL AOT)", "SHPD00095": "Play (game AOT)",
    "SHPD00190": "Menu (V6 game AOT)",
    "SHPD00191": "Measure (GL AOT, LLVM)", "SHPD00192": "Play MT (LLVM)",   # kit #5 llvm A/B (vita/notes/kit5-llvm.md)
    "SHPD00193": "Play MT (launcher)", "SHPD00194": "Play (game AOT, launcher)",   # kit #8 boot: the tiny launcher + early logo (kit8-boot.md)
}
LOG_TITLE = {  # log base name (no .prev / .txt) -> title id, for logs whose BEGIN line has no id
    "probe": "SHPD00001", "gldiag": "SHPD00030", "hello": "SHPD00005", "hello-diag": "SHPD00015", "hello-diag-repro": "SHPD00015",
    "menu": "SHPD00006", "menu-diag": "SHPD00016", "menu-aot": "SHPD00011", "menu-aotd": "SHPD00090", "menu_gl": "SHPD00008",
    "menu_gl-aot": "SHPD00009", "menu_gl_audio": "SHPD00010", "menu_gl-aotd": "SHPD00091", "menu_gl-aotd-mt": "SHPD00096",
    "menu_gl-measure": "SHPD00099", "play": "SHPD00097", "play-mt": "SHPD00098", "play-gaot": "SHPD00095", "menu-gaot": "SHPD00190",
    "menu_gl-measure-llvm": "SHPD00191", "play-mt-llvm": "SHPD00192",
    "play-mt-launcher": "SHPD00193", "play-gaot-launcher": "SHPD00194",
}
# k4 review: a single-threaded app's MT sibling (the same build with SHP_MT_DEFAULT) and the apps that are MT by default
MT_SIBLING = {"SHPD00091": "SHPD00096", "SHPD00097": "SHPD00098"}
MT_APPS = {"SHPD00096", "SHPD00098", "SHPD00095", "SHPD00192", "SHPD00193", "SHPD00194"}
PLAY_APPS = {"SHPD00097", "SHPD00098", "SHPD00095", "SHPD00192", "SHPD00193", "SHPD00194"}   # owner-driven sessions: their frames 1301-1400 are never "the idle menu"
MAIN_LINE = {"SHPD00006", "SHPD00008", "SHPD00009", "SHPD00010", "SHPD00011", "SHPD00016", "SHPD00090", "SHPD00091", "SHPD00096",
             "SHPD00097", "SHPD00098", "SHPD00099", "SHPD00095", "SHPD00191", "SHPD00192", "SHPD00193", "SHPD00194"}
# the app an app grew out of (for "vs last version" when an app is new): measure <- GL derived AOT <- GL engine AOT <- GL menu;
# play <- GL + audio menu; play MT <- play; engine AOT <- headless menu
PREDECESSORS = {
    "SHPD00099": ["SHPD00091", "SHPD00009", "SHPD00008"], "SHPD00091": ["SHPD00009", "SHPD00008"], "SHPD00096": ["SHPD00091", "SHPD00008"],
    "SHPD00009": ["SHPD00008"], "SHPD00097": ["SHPD00010"], "SHPD00098": ["SHPD00097", "SHPD00010"], "SHPD00011": ["SHPD00006"],
    "SHPD00090": ["SHPD00011", "SHPD00006"], "SHPD00010": ["SHPD00008"], "SHPD00095": ["SHPD00098", "SHPD00097", "SHPD00010"],
    "SHPD00191": ["SHPD00099"], "SHPD00192": ["SHPD00098"],   # kit #5: the LLVM A/B apps (their first version: compared with their partners' last)
    "SHPD00193": ["SHPD00098"], "SHPD00194": ["SHPD00095"],   # kit #8: the launcher builds of the play MT / game AOT recipes
}
# kit #4: an engine-log folder an env file moved (SHEPHERD_LOG_DIR) so that one start does not overwrite the app's previous engine log
# (kit #4 STEP 6: the read-ahead start of 00095) -> the app it belongs to (its host log is still <app>.txt / .prev.txt)
ENGINE_DIR_ALIAS = {"play-gaot-readahead": "play-gaot"}
# engine logs that came without their host log and cannot name their app themselves (a hand-copied pull): folder, path -> facts
# from that kit's README (kit #2: 00006 "BEGIN ... build 20261013-090621", 00016 "... 090702"; every kit #2 app sets 444 at start)
OVERRIDES = {
    ("20261013-kit2-phone", "runtime/engine-headless-a.log"): dict(title="SHPD00006", build="20261013-090621", clock="444",
                                                                   clock_src="inferred"),
    ("20261013-kit2-phone", "runtime/engine-headless-b.log"): dict(title="SHPD00016", build="20261013-090702", clock="444",
                                                                   clock_src="inferred"),
}

COLS = ("version kit date time folder log title app build commit clock clock_src result frames window "
        "idle_ms idle_p50 idle_p99 render_ms logic_ms idle_fps idle_wall_ms avg_fps "
        "boot_menu_s main_at_s menu_frame menu_load_s menu_scene_s "
        "gc_minor gc_major gc_ms gc_major_max_ms gc_idle_minor_ms gc_full_ms live_mib "
        "newlib_peak_kib newlib_cap_kib arena_peak_kib arena_kib free_min_kib heap_cap_mib "
        "card_mibs card_4k_ms pack_mibs gpu_us_draw shown key ekey "
        "splash_at_s boot_total_s work_left_s clock_meas clock_note play_ms "
        "win_screen win_sel win_draws win_events win_clock win_cfg win_mtdraws win_ms_x").split()   # (kit #12: appended; old TSVs load)

HOST_RE = re.compile(r"^(probe[\w-]*|gldiag|hello(?:-diag(?:-repro)?)?|menu[\w-]*|play[\w-]*)(\.prev)?\.txt$")
FOLDER_RE = re.compile(r"^(\d{8})-(\d{6})$")
STAMP_RE = re.compile(r"\bbuild (\d{8}-\d{6})(?:-[0-9a-f]+)?\b")

def num(x, nd=1):
    if x is None or x == "": return ""
    if isinstance(x, int): return str(x)
    s = f"{x:.{nd}f}"
    return s.rstrip("0").rstrip(".") if "." in s else s

def fnum(s):
    try: return float(s)
    except (TypeError, ValueError): return None

def sha(data): return hashlib.sha1(data).hexdigest()[:12]

def read_lines(path):
    with open(path, "rb") as f: data = f.read()
    return data, data.decode("utf-8", "replace").splitlines()

def vkey(v):
    m = re.match(r"^(\d+)\.(\d+)(\+?)$", v or "")
    return (int(m.group(1)), int(m.group(2)), 1 if m.group(3) else 0) if m else (-1, -1, 0)

def version_of(stamp=None, utc_stamp=None, probe_build=None):
    """stamp: a build stamp (UTC); probe_build: the probe's header ("shepherd-probe-2"); utc_stamp: when the run happened (UTC), for
    logs with neither. -> the VERSIONS entry; its commit only when the build itself names the kit (not for a run-time guess, nor for
    a build newer than the last known kit)."""
    if not stamp and probe_build in PROBE_BUILDS:
        return next(v for v in VERSIONS if v["v"] == PROBE_BUILDS[probe_build])
    s = stamp or utc_stamp
    if not s: return None
    cur = None
    for v in VERSIONS:
        if s >= v["since"]: cur = v
    if cur is None: return None
    if not stamp: return dict(cur, commit="")
    if cur is VERSIONS[-1] and stamp not in cur["stamps"] and stamp > max(cur["stamps"]):
        return dict(cur, v=cur["v"] + "+", commit="")
    return cur

# ---------------------------------------------------------------------------------------------------------------- engine side
def norm_engine(lines, simstats=False):
    """-> [(frame or None, tag, text)]: '[  806]     [pack] text' -> (806, 'pack', 'text'); simstats.txt lines are tag 'sim'."""
    out = []
    for l in lines:
        fr = None
        m = re.match(r"^\[\s*(\d+)\]\s?(.*)$", l)
        if m: fr, l = int(m.group(1)), m.group(2)
        l = l.strip()
        m = re.match(r"^\[([\w-]+)\]\s*(.*)$", l)
        if m and not simstats: out.append((fr, m.group(1), m.group(2).strip()))
        elif simstats: out.append((fr, "sim", l))
        else: out.append((fr, "", l))
    return out

def parse_engine(lines, simstats=False):
    e = {}
    scenes = []
    for fr, tag, t in norm_engine(lines, simstats):
        if tag == "sim":
            m = re.match(r"^window frames (\d+)\.\.(\d+) \(n=(\d+)\), (\w+)", t)
            if m: e["win"] = (int(m.group(1)), int(m.group(2))); e["renderer"] = m.group(4); continue
            m = re.match(r"^frame wall\s+mean ([\d.]+) p50 ([\d.]+) p95 ([\d.]+) p99 ([\d.]+)", t)
            if m: e["idle_ms"], e["idle_p50"], e["idle_p99"] = fnum(m.group(1)), fnum(m.group(2)), fnum(m.group(4)); continue
            m = re.match(r"^render part\s+mean ([\d.]+)", t)
            if m: e["render_ms"] = fnum(m.group(1)); continue
            m = re.match(r"^logic part\s+mean ([\d.]+)", t)
            if m: e["logic_ms"] = fnum(m.group(1)); continue
            m = re.match(r"^heap @ frame \d+ .*?forced full GC #1 ([\d.]+) ms, #2 ([\d.]+) ms.*?LIVE after = ([\d.]+) MiB", t)
            if m: e["gc_full_ms"] = max(fnum(m.group(1)), fnum(m.group(2))); e["live_mib"] = fnum(m.group(3)); continue
            if t.startswith("startup:"):
                m = re.search(r"unix ms (\d+)\)", t)
                if m: e["main_epoch"] = int(m.group(1)) / 1000.0
                m = re.search(r"present at frame (\d+) \(\+(\d+) ms\)", t)
                if m: e["menu_frame"], e["boot_menu_s"] = int(m.group(1)), int(m.group(2)) / 1000.0
                m = re.search(r"run total \+(\d+) ms over (\d+) frames", t)
                if m: e["run_s"], e["frames"] = int(m.group(1)) / 1000.0, int(m.group(2))
                m = re.search(r"GCs (\d+) minor \+ (\d+) major", t)
                if m: e["gc_minor"], e["gc_major"] = int(m.group(1)), int(m.group(2))
                continue
        elif tag == "menu":   # kit #12 compare audit: the engine's menu probes (SHEPHERD_MENU_PROBE 1040 / 1300 / 1450 + the re-base)
            m = re.match(r"^frame (\d+): selected='([^']*)'", t)
            if m: e.setdefault("probe", {})[int(m.group(1))] = "none" if m.group(2) == "(none)" else "sel"   # (the label is never kept)
        elif tag == "mt":
            m = re.match(r"^window \(\d+ frames\):.*?captured world draws ([\d.]+)/frame", t)
            if m: e["mtdraws"] = fnum(m.group(1))
        elif tag == "pack":
            m = re.match(r"^pack pack: open [\d.]+ ms .*?; (\d+) reads ([\d.]+) MiB in (\d+) ms", t)
            if m and int(m.group(3)) > 0: e["pack_mibs"] = fnum(m.group(2)) / (int(m.group(3)) / 1000.0)
        elif tag == "time":
            m = re.match(r"^wall clock: (\d+) frames over ([\d.]+) s of wall time \(([\d.]+) fps\)", t)
            if m: e["avg_fps"] = fnum(m.group(3))
            if re.match(r"^clock WALL\b", t): e["play"] = True
        elif tag == "":
            m = re.match(r"^scene '[^']*': \d+ GameObjects, \d+ objects created, \d+ roots \((\d+) ms\)$", t)
            if m and fr is not None: scenes.append((fr, int(m.group(1))))
    if "menu_frame" in e:
        for fr, ms in scenes:
            if fr == e["menu_frame"]: e["menu_scene_s"] = ms / 1000.0
    if "main_epoch" in e and "run_s" in e: e["end_epoch"] = e["main_epoch"] + e["run_s"]
    return e

# ------------------------------------------------------------------------------------------------------------------ host side
def parse_host(name, lines):
    h = dict(fails=[], walls={}, frees=[], newlib_peaks=[], arena_peaks=[], span=0.0)
    base = name[:-4]
    if base.endswith(".prev"): base = base[:-5]
    h["base"] = base
    gl_pass = gl_fail = gl_runs = 0
    for l in lines:
        if not l or l.startswith("C1>"): continue
        if l.startswith("BEGIN ") and "stamp" not in h:
            m = STAMP_RE.search(l)
            if m: h["stamp"] = m.group(1)
            m = re.search(r"\b(SHPD\d{5})\b", l)
            if m: h["title"] = m.group(1)
            continue
        # kit #10: an env file's SHEPHERD_LOG_DIR moves this run's engine logs out of runtime/logs/<app>/ -- the host names the folder
        # folder (every host prints the line; only a folder other than the app's own runtime/logs/<app>/ counts)
        m = re.match(r"^H1 this app's engine logs \([^)]*\): ux0:data/shepherd/(runtime/logs/([^/\s]+))/", l)
        if m:
            if ENGINE_DIR_ALIAS.get(m.group(2), m.group(2)) != base: h["logdir"] = m.group(1)
            continue
        m = re.match(r"^=== shepherd-gldiag-\d+ build (\d{8}-\d{6})", l)
        if m:
            h.setdefault("stamp", m.group(1)); h["kind"] = "gldiag"; continue
        m = re.match(r"^=== (shepherd-probe-\d+) ===", l)
        if m: h["kind"] = "probe"; h["probe_build"] = m.group(1); continue
        m = re.match(r"^CLK start: arm (\d+)", l) or re.match(r"^CLK after arm=(\d+)", l)
        if m: h["clock"] = m.group(1); continue
        # kit #8 fix: the clock the app MEASURED (kit #7 on) -- at start, at the end, and a change seen during the run ("CLK measured at 106.0 s:
        # the ARM cores now run at ~500 MHz (was ~444 ...)"); "they DIFFER" = not the system's report (set outside scePower: PSVshellPlus)
        if l.startswith("CLK measured ") and "not one of the Vita's clock steps" not in l:
            m = re.match(r"^CLK measured at ([\d.]+) s: the ARM cores now run at ~(\d+) MHz", l)
            if m: h.setdefault("cmeas", []).append((int(m.group(2)), fnum(m.group(1))))
            else:
                m = re.match(r"^CLK measured at (start|the end): ~(\d+) MHz", l)
                if m: h.setdefault("cmeas", []).append((int(m.group(2)), None))
            if "they DIFFER" in l: h["cdiffer"] = True
            continue
        # how long the run had lasted at its last timed line (P / CP / M1 wall=, PHASE / STALL "wall N s", "END frame N, N s")
        for m in re.finditer(r"\bwall[= ]([\d.]+) ?s\b|\bEND frame \d+, ([\d.]+) s\b", l):
            h["span"] = max(h["span"], fnum(m.group(1) or m.group(2)) or 0.0)
        m = re.match(r"^(?:P|CP\w*) f=(\d+) wall=([\d.]+)s", l)
        if m:
            f, w = int(m.group(1)), fnum(m.group(2))
            h["walls"].setdefault(f, w)
            # kit #12 compare audit: the screen at this heartbeat -- the selection as the starred entry's INDEX in the buttons list ("b1";
            # "none"; "x" = selected but not a menu button), draws in, and a hash of the scene list (the game's names are never kept)
            sel = re.search(r"\bselected='([^']*)'", l)
            s = None
            if sel:
                bl = re.search(r"\bbuttons=\(\d+\) (.*?) \| scenes=", l)
                k = next((i + 1 for i, x in enumerate(bl.group(1).split(" | ")) if x.startswith("*") and x.endswith("*")), 0) if bl else 0
                s = "none" if sel.group(1) == "(none)" else (f"b{k}" if k else "x")
            dr = re.search(r"\bdraws (\d+)->\d+", l)
            sc = re.search(r"\bscenes=\[([^\]]*)\]", l)
            h.setdefault("hb", {}).setdefault(f, (w, s, int(dr.group(1)) if dr else None, sha(sc.group(1).encode())[:8] if sc else None))
            if l.startswith("CP"): h.setdefault("cpw", {}).setdefault(f, w)   # (kit #6: CP1300 + the frame-clock re-base)
            h["last_frame"] = max(h.get("last_frame", 0), f)
            if l.startswith("CP"): h["cp_frame"] = f
        # every heartbeat: GC counts and the newlib heap in use (a log without CPend still shows how far they got)
        m = re.match(r"^P f=(\d+) .*?\bgcs (\d+)/(\d+) \| arena (\d+) KiB, newlib (\d+) KiB", l)
        if m:
            h["p_gcs"] = (int(m.group(1)), int(m.group(2)), int(m.group(3)))
            h["arena_peaks"].append(int(m.group(4))); h["newlib_peaks"].append(int(m.group(5)))
        for rx in (r"^STALL\b.*\bnewlib peak (\d+) KiB", r"^WARN newlib heap .*\(peak (\d+)\)"):
            m = re.search(rx, l)
            if m: h["newlib_peaks"].append(int(m.group(1)))
        m = re.search(r" scene at f(\d+)\)", l) if l.startswith("K7 BOOT ") else None   # kit #12: the menu's arrival frame (K7 BOOT)
        if m: h["menu_f"] = int(m.group(1))
        m1 = re.match(r"^M1 f=(\d+) wall=[\d.]+s .*?after pad (\w+) at f(\d+)", l)   # kit #12: the menu watch (first 20 responses)
        if m1: h.setdefault("m1ev", []).append((int(m1.group(3)), int(m1.group(1)), m1.group(2)))   # (press frame, response frame, VitaPad.B)
        m = re.match(r"^H1 env (SHEPHERD_[A-Z0-9_]+)=(\S+) \(", l)
        if m and m.group(1) != "SHEPHERD_LOG_DIR": h.setdefault("envs", []).append(f"{m.group(1)}={m.group(2)}")
        m = re.match(r"^H1 frame clock re-based by (\d+) frames", l)   # kit #6: the Devolver splash (frame clock): the scripted frames + N
        if m: h["rebase"] = int(m.group(1))
        elif l.startswith("H1 frame clock not re-based"): h.setdefault("rebase", 0)
        if l.startswith("K7 BOOT "):   # kit #7 (Diag/K7.cs): the boot numbers every boot -- time to splash, total, work left after the splash
            m = re.search(r"time to splash ([\d.]+) s", l)
            if m: h["splash_at_s"] = fnum(m.group(1))
            m = re.search(r"WORK LEFT AFTER THE SPLASH ([\d.]+) s", l)
            if m: h["work_left_s"] = fnum(m.group(1))
            m = re.search(r"\| TOTAL ([\d.]+) s", l)
            if m: h["boot_total_s"] = fnum(m.group(1))
        m = re.match(r"^PHASE #1 Main: entered.*\(frame 0, wall ([\d.]+) s\)", l)
        if m: h["main_at_s"] = fnum(m.group(1))
        if re.match(r"^T0 game clock .*: PLAY\b", l): h["play"] = True
        m = re.match(r"^H1 multicore render pipeline (ON|off)\b", l)
        if m: h["mt"] = m.group(1) == "ON"
        if re.match(r"^H1 diag mode repro\b", l): h["repro"] = True
        for rx in (r"free user[= ](\d+) KiB", r"free main\(user\) (\d+) KiB"):
            for m in re.finditer(rx, l): h["frees"].append(int(m.group(1)))
        for rx in (r"newlib used=\d+ KiB \(sbrk \d+, peak (\d+)\) of (\d+) KiB", r"newlib heap used \d+ KiB, sbrk \d+, peak (\d+) of (\d+) KiB"):
            m = re.search(rx, l)
            if m: h["newlib_peaks"].append(int(m.group(1))); h["newlib_cap"] = int(m.group(2))
        for rx in (r"arena used=\d+ KiB peak=(\d+)(?: KiB)? of (\d+)", r"arena used \d+ KiB, peak (\d+) of (\d+) KiB"):
            m = re.search(rx, l)
            if m and int(m.group(2)) > 0: h["arena_peaks"].append(int(m.group(1))); h["arena"] = int(m.group(2))
        for m in re.finditer(r"(?:\((\d+) frames shown\)|frames shown (\d+)|, (\d+) frames shown)", l):
            v = int(next(g for g in m.groups() if g))
            h["shown"] = max(h.get("shown", 0), v)
        m = re.match(r"^CP(\w+) gc: .*?max-heap-size (\d+) MiB.*?\| (\d+) minor \+ (\d+) major GCs; pauses: minor \d+ \(([\d.]+) ms total, "
                     r"max [\d.]+\), major \d+ \(([\d.]+) ms total, max ([\d.]+)\); window: (?:(\d+) pauses \((\d+) major\) p50 ([\d.]+) p99 "
                     r"[\d.]+ max ([\d.]+))?", l)
        if m:
            cp = m.group(1)
            h["heap_cap_mib"] = int(m.group(2))
            h["gc"] = (int(m.group(3)), int(m.group(4)), fnum(m.group(5)) + fnum(m.group(6)), fnum(m.group(7)))
            h["gc_end"] = cp == "end"
            if cp.isdigit() and m.group(8) and int(m.group(9)) >= 2: h.setdefault("cp_full", {})[int(cp)] = fnum(m.group(11))   # (CP1300 + the re-base: below)
            if cp == "end" and m.group(8) and int(m.group(9)) == 0 and int(m.group(8)) > 0: h["gc_idle_minor_ms"] = fnum(m.group(10))
        # results
        m = re.match(r"^PASS END frame (\d+)", l)
        if m: h["end"] = "PASS"; h["last_frame"] = max(h.get("last_frame", 0), int(m.group(1)))
        m = re.match(r"^FAIL END frame (\d+)", l)
        if m: h["end"] = "FAIL"; h["last_frame"] = max(h.get("last_frame", 0), int(m.group(1)))
        if re.match(r"^PASS M1 f=\d+", l): h["m1"] = True
        if re.match(r"^FAIL runtime fatal", l): h["crash"] = True
        if re.match(r"^STALL\b", l): h["stall"] = True
        if l.startswith("DONE"): h["done"] = True
        if re.match(r"^PASS shepherd-hello\b", l): h["hello_pass"] = True
        m = re.match(r"^FAIL (\w+)", l)
        if m and m.group(1) not in ("END", "runtime"): h["fails"].append(m.group(1))
        if re.match(r"^RUN variant \d+ of \d+", l): gl_runs += 1
        if re.match(r"^PASS variant \d+", l): gl_pass += 1
        if re.match(r"^FAIL variant \d+", l): gl_fail += 1
        # probe numbers
        m = re.match(r"^IO seq 1MiB chunks.*= ([\d.]+) MiB/s", l)
        if m: h["card_mibs"] = fnum(m.group(1))
        m = re.match(r"^IO random 4KiB.*\(([\d.]+) ms each\)", l)
        if m: h["card_4k_ms"] = fnum(m.group(1))
        m = re.match(r"^GPU individual: .*\(([\d.]+) us/draw\)", l)
        if m: h["gpu_us_draw"] = fnum(m.group(1))
    if gl_runs: h["gldiag"] = (gl_pass, gl_runs, gl_fail)
    return h

def host_result(h):
    if "gldiag" in h:
        p, n, f = h["gldiag"]
        return f"PASS {p}/{n}" if not f else f"FAIL {f}/{n}"
    if h.get("kind") == "probe":
        if not h.get("done"): return "HUNG"
        return "PASS" if not h["fails"] else "FAIL:" + ",".join(sorted(set(h["fails"])))
    if h.get("end"): return h["end"]
    if h.get("crash"): return "REPRO-CRASH" if h.get("repro") else "CRASH"
    if h.get("hello_pass"): return "PASS"
    if h.get("stall"): return "STALL"
    if h.get("m1"): return "PASS M1"
    if not h.get("walls"): return "HUNG"
    return "STOPPED"

# ---------------------------------------------------------------------------------------------------------------- the folder
def local_dt(epoch):
    t = time.localtime(epoch)
    return time.strftime("%Y-%m-%d", t), time.strftime("%H:%M", t)

def utc_stamp(epoch): return time.strftime("%Y%m%d-%H%M%S", time.gmtime(epoch))

def folder_epoch(folder):
    m = FOLDER_RE.match(folder) or re.match(r"^(\d{8})", folder)
    if not m: return None
    s = m.group(1) + (m.group(2) if m.lastindex and m.lastindex >= 2 else "000000")
    return time.mktime(time.strptime(s, "%Y%m%d%H%M%S"))

def engine_sources(d):
    """-> [(relpath, app or None, is_simstats)] for one folder, engine.log preferred over simstats.txt in the same directory."""
    out = []
    def add_dir(rel, app):
        p = os.path.join(d, rel)
        if os.path.isfile(os.path.join(p, "engine.log")): out.append((os.path.join(rel, "engine.log"), app, False))
        elif os.path.isfile(os.path.join(p, "simstats.txt")): out.append((os.path.join(rel, "simstats.txt"), app, True))
    rl = os.path.join(d, "runtime", "logs")
    if os.path.isdir(rl):
        for a in sorted(os.listdir(rl)):
            if not a.startswith(".") and os.path.isdir(os.path.join(rl, a)): add_dir(os.path.join("runtime", "logs", a), ENGINE_DIR_ALIAS.get(a, a))
        add_dir(os.path.join("runtime", "logs"), None)
    if os.path.isdir(os.path.join(d, "logs")): add_dir("logs", None)
    rt = os.path.join(d, "runtime")
    if os.path.isdir(rt):
        for f in sorted(os.listdir(rt)):
            m = re.match(r"^engine-([\w-]+)\.log$", f)
            if m: out.append((os.path.join("runtime", f), m.group(1), False))
    return out

def parse_folder(d):
    d = os.path.abspath(d)
    folder = os.path.basename(d)
    mtime_ok = bool(FOLDER_RE.match(folder))   # read-logs.sh copies with cp -p: file times = when the run wrote its log
    fe = folder_epoch(folder)
    hosts = []
    for f in sorted(os.listdir(d)):
        if f.startswith("._") or not HOST_RE.match(f): continue
        p = os.path.join(d, f)
        if not os.path.isfile(p): continue
        data, lines = read_lines(p)
        h = parse_host(f, lines)
        h.update(name=f, key=sha(data), mtime=os.path.getmtime(p))
        hosts.append(h)
    engines, seen = [], set()
    for rel, app, simst in engine_sources(d):
        data, lines = read_lines(os.path.join(d, rel))
        k = sha(data)
        if k in seen: continue
        seen.add(k)
        e = parse_engine(lines, simstats=simst)
        if "idle_ms" not in e and "boot_menu_s" not in e: continue
        e["sf_path"] = os.path.join(d, os.path.dirname(rel), "simstats-frames.tsv")   # (kit #12 compare audit)
        e.update(rel=rel, app=app, key=k, simstats=simst, mtime=os.path.getmtime(os.path.join(d, rel)),
                 ekey=sha(f"run@{e['main_epoch']:.3f}".encode()) if e.get("main_epoch") else k)
        # one run seen twice (logs/engine.log and runtime/logs/simstats.txt of the same run): keep the engine log, it has more
        twin = next((x for x in engines if e.get("main_epoch") and x.get("main_epoch") == e["main_epoch"]), None)
        if twin is not None:
            if twin["simstats"] and not simst: engines[engines.index(twin)] = e
            continue
        engines.append(e)
    # pair engine logs with host logs: by app name -- in a dated folder only when the engine's run lies inside the host log's run
    # (the card does not clear runtime/logs/<app>/: after a run that ends before Main its engine.log is the previous run's) --,
    # else by time (the engine's run end vs the host log's file time)
    def inside(x, e):
        # the host's file time is its last write (up to ~60 s after its last timed line: the "press START or wait 60 s" end); its
        # start = file time - its last wall value, give or take that wait. The engine's run: its log's file time (the same card
        # clock as the host's) or its Main-entered time (UTC from the engine; the same unless the Vita's time zone is not the Mac's)
        lo, hi = x["mtime"] - x["span"] - 150, x["mtime"] + 60
        return any(t is not None and lo <= t <= hi for t in (e["mtime"], e.get("main_epoch")))
    # kit #10 (device run 2026-10-29): a host log that names its engine-log folder (H1 line) pairs with that folder's engine log first;
    # by app name a host is paired only with runtime/logs/<app>/ when it named no other folder (before: STEP 8's host log, its engine
    # log in runtime/logs/k10-step8-measure/, took STEP 3's runtime/logs/menu_gl-measure/ engine log by name)
    # (a dated folder: only a host whose run holds the engine's -- a later start with the same env file left on the card, cut short,
    # names the same folder and must not take the earlier run's engine log)
    for e in engines:
        h = [x for x in hosts if x.get("logdir") == os.path.dirname(e["rel"]) and "eng" not in x and (not mtime_ok or inside(x, e))]
        h = max(h, key=lambda x: x["mtime"]) if h else None
        if h is not None: h["eng"] = e; e["paired"] = True
    for e in engines:
        if e.get("paired"): continue
        h = None
        if e["app"]:
            named = [x for x in hosts if x["base"] == e["app"] and "eng" not in x
                     and x.get("logdir", os.path.dirname(e["rel"])) == os.path.dirname(e["rel"])]
            if mtime_ok:
                fits = [x for x in named if inside(x, e)]
                h = max(fits, key=lambda x: x["mtime"]) if fits else None
            else:   # a hand-copied folder: the file times are the copy's, only the names say which belongs to which
                h = next((x for x in named if x["name"] == e["app"] + ".txt"), None)
                # kit #10: the .txt's run kept its engine logs elsewhere (H1) -> the folder's engine log is the .prev.txt run's
                if h is None and any(x["name"] == e["app"] + ".txt" and x.get("logdir") for x in hosts):
                    h = next((x for x in named if x["name"] == e["app"] + ".prev.txt"), None)
        elif mtime_ok and "end_epoch" in e:
            cands = [x for x in hosts if x["base"].startswith(("menu", "play")) and abs(x["mtime"] - e["end_epoch"]) < 600]
            h = min(cands, key=lambda x: abs(x["mtime"] - e["end_epoch"])) if cands else None
        if h is not None and "eng" not in h: h["eng"] = e; e["paired"] = True
    rows = []
    for h in hosts: rows.append(make_row(folder, fe, mtime_ok, h, h.get("eng")))
    for e in engines:
        if not e.get("paired"): rows.append(make_row(folder, fe, mtime_ok, None, e))
    return rows

def make_row(folder, fe, mtime_ok, h, e):
    r = dict.fromkeys(COLS, "")
    e = e or {}
    r["folder"] = folder
    ov = {}
    if h:
        r["log"] = h["name"]; r["key"] = h["key"]
        title = h.get("title") or LOG_TITLE.get(h["base"]) or ""
        stamp = h.get("stamp")
        r["clock"] = h.get("clock", ""); r["clock_src"] = "logged" if h.get("clock") else ""
        r["result"] = host_result(h)
    else:
        r["log"] = e["rel"]; r["key"] = e["key"]
        ov = OVERRIDES.get((folder, e["rel"]), {})
        title = ov.get("title") or LOG_TITLE.get(e.get("app") or "", "")
        stamp = ov.get("build")
        r["clock"], r["clock_src"] = ov.get("clock", ""), ov.get("clock_src", "")
        # an engine log ends with its summary only when the run ended normally; the host's PASS END checks were not seen: "PASS*"
        r["result"] = "PASS*" if e.get("frames") else ""
    r["title"] = title; r["app"] = APPS.get(title, "")
    # k4 review: the multicore pipeline as the run had it (host H1 line), when it is not the app's own default -- a single-threaded
    # app run MT is compared with its MT sibling's runs (prior), so an MT switch is never counted as a version's speed-up
    mt = (h or {}).get("mt")
    if mt is True and title in MT_SIBLING: r["app"] += " (MT)"
    if mt is False and title in MT_APPS: r["app"] += " (MT off)"
    r["build"] = stamp or ""
    # when: the engine's run end, else the host log's file time (read-logs folders), else the folder's date
    when = e.get("end_epoch") or (h["mtime"] if h and mtime_ok else None)
    if when: r["date"], r["time"] = local_dt(when)
    elif fe: r["date"] = time.strftime("%Y-%m-%d", time.localtime(fe))
    v = version_of(stamp, utc_stamp(when or fe) if (when or fe) else None, (h or {}).get("probe_build"))
    if v: r["version"], r["kit"], r["commit"] = v["v"], v["kit"], v["commit"]
    # clock without a CLK line: builds before 2026-10-13 never set it (the 333 MHz launch clock); later builds set 444 at start
    if not r["clock"] and title and title != "SHPD00030" and (stamp or (h and h.get("kind") == "probe")):
        if stamp and stamp < "20261013-000000": r["clock"], r["clock_src"] = "333", "inferred"
    # frames, window, idle numbers
    frames = (h or {}).get("last_frame") or e.get("frames")
    r["frames"] = num(frames) if frames else ""
    play = bool((h or {}).get("play") or e.get("play") or title in PLAY_APPS)
    # kit #8 fix: the measured clock (the values in order, a change during the run, DIFFER = not the system's report)
    seq, notes = [], []
    for mhz, at in (h or {}).get("cmeas", []):
        if not seq or seq[-1] != mhz:
            if seq and at is not None: notes.append(f"changed at {num(at, 0)} s")
            elif seq: notes.append("changed")
            seq.append(mhz)
    r["clock_meas"] = ">".join(str(x) for x in seq)
    if (h or {}).get("cdiffer"): notes.insert(0, "DIFFER")
    r["clock_note"] = "; ".join(notes)
    if "idle_ms" in e:
        # kit #8 fix: an idle-menu number only for a run whose window IS the idle menu -- a menu app (not a play session) whose [sim] window is
        # the idle frames (1301-1400 + the frame-clock re-base); otherwise its mean goes to play_ms and no idle_* number is written
        rb0 = (h or {}).get("rebase")
        win = e.get("win")
        idle_win = win is None or (win[0] == 1301 + rb0 if rb0 is not None else win[0] in (1301, 1677))
        if play or not idle_win:
            r["window"] = "play" if play else "other"
            r["play_ms"] = num(e.get("idle_ms"), 2)
        else:
            r["window"] = "menu"
            for k in ("idle_ms", "idle_p50", "idle_p99", "render_ms", "logic_ms"): r[k] = num(e.get(k), 2)
            r["idle_fps"] = num(1000.0 / e["idle_ms"], 1) if e.get("idle_ms") else ""
    if h:   # kit #6: the idle window's host frames move with the frame-clock re-base (H1 frame clock re-based by N frames; 0 before kit #6)
        rb = h.get("rebase", 0)
        h["cp1300"] = h.get("cpw", {}).get(1300 + rb)
        # the window's end: the host line at 1400 + rb (kits #3-#5: P f=1400, the pad's down press); a re-based log has none there (P lines
        # every 50 frames): the last heartbeat at or before it, inside the window (kit #6 review: not the first one after it -- P f=1800
        # took in 24 frames after the press)
        walls = h.get("walls", {}); end = 1400 + rb
        if end not in walls and rb:
            ends = [f for f in walls if 1300 + rb < f < end]
            end = max(ends) if ends else None
        h["p1400"] = walls.get(end) if end is not None else None; h["p1400_frames"] = (end - (1300 + rb)) if end is not None else 100
        if (h.get("cp_full") or {}).get(1300 + rb) is not None: h["gc_full_ms"] = h["cp_full"][1300 + rb]
    if h and not play and r["window"] != "other" and h.get("cp1300") is not None and h.get("p1400") is not None:
        r["idle_wall_ms"] = num((h["p1400"] - h["cp1300"]) * 1000.0 / h.get("p1400_frames", 100), 0)   # (s over the frames) -> ms per frame
        if not r["window"]: r["window"] = "menu"
    r["avg_fps"] = num(e.get("avg_fps"), 1)
    r["boot_menu_s"] = num(e.get("boot_menu_s"), 1)
    for k in ("splash_at_s", "boot_total_s", "work_left_s"): r[k] = num((h or {}).get(k), 1)   # kit #7: the host's K7 BOOT line
    r["main_at_s"] = num((h or {}).get("main_at_s"), 1)
    # no [sim] startup line (an early FAIL END, a guard, a hang, a host log alone): the frame clock's measured arrival, f806 + the re-base
    # (kit #6: f1182; review fix -- 806 alone lies inside the Devolver clip there), when the host log reached the heartbeat after it
    # (kits #3-#5: P f=850, as before; re-based: any line at or after 850 + 376, i.e. P f=1250)
    rb = (h or {}).get("rebase", 0); mf0 = 806 + rb
    mf = e.get("menu_frame") or (mf0 if h and not play and any(f >= 850 + rb for f in h.get("walls", {})) else None)
    if mf:
        r["menu_frame"] = str(mf)
        walls = (h or {}).get("walls", {})
        before = [f for f in walls if f <= mf]; after = [f for f in walls if f > mf]
        if before and after:
            r["menu_load_s"] = num(walls[min(after)] - walls[max(before)], 1)
    r["menu_scene_s"] = num(e.get("menu_scene_s"), 1)
    # GC
    if h and (h.get("gc") or h.get("p_gcs")):
        mi = ma = tot = mx = None
        if h.get("gc"): mi, ma, tot, mx = h["gc"]
        pg = h.get("p_gcs")
        # no CPend: the last heartbeat's counts are the furthest the log got; the pause totals of an earlier CP are then not the run's
        if pg and not h.get("gc_end") and pg[0] > h.get("cp_frame", -1) and (mi is None or (pg[1], pg[2]) != (mi, ma)):
            if ma is None or pg[2] != ma: mx = None
            mi, ma, tot = pg[1], pg[2], None
        r["gc_minor"], r["gc_major"], r["gc_ms"], r["gc_major_max_ms"] = str(mi), str(ma), num(tot, 0), num(mx, 1)
    elif e.get("gc_minor") is not None:
        r["gc_minor"], r["gc_major"] = str(e["gc_minor"]), str(e["gc_major"])
    r["gc_idle_minor_ms"] = num((h or {}).get("gc_idle_minor_ms"), 1)
    r["gc_full_ms"] = num(e.get("gc_full_ms") or (h or {}).get("gc_full_ms"), 1)
    r["live_mib"] = num(e.get("live_mib"), 1)
    # memory
    if h:
        if h["newlib_peaks"]: r["newlib_peak_kib"], r["newlib_cap_kib"] = str(max(h["newlib_peaks"])), str(h.get("newlib_cap", ""))
        if h["arena_peaks"]: r["arena_peak_kib"], r["arena_kib"] = str(max(h["arena_peaks"])), str(h.get("arena", ""))
        if h["frees"] and h.get("kind") != "probe": r["free_min_kib"] = str(min(h["frees"]))
        r["heap_cap_mib"] = num(h.get("heap_cap_mib"))
        r["card_mibs"], r["card_4k_ms"], r["gpu_us_draw"] = num(h.get("card_mibs"), 1), num(h.get("card_4k_ms"), 2), num(h.get("gpu_us_draw"), 1)
        r["shown"] = num(h.get("shown")) if h.get("shown") else ""
    r["pack_mibs"] = num(e.get("pack_mibs"), 1)
    r["ekey"] = e.get("ekey", "")
    wrb = (e["win"][0] - 1301) if e.get("win") else (h or {}).get("rebase", 0)   # (an engine log alone: its own [sim] window says the re-base)
    if "idle_ms" in e or (h and (h.get("hb") or {}).get(1300 + wrb)):   # kit #12: which screen the window covered
        (r["win_screen"], r["win_sel"], r["win_draws"], r["win_events"], r["win_clock"], r["win_cfg"], r["win_mtdraws"],
         r["win_ms_x"]) = window_info(h, e, wrb)
    return r

# ---------------------------------------------------------------------------------------- kit #12 compare audit: the window's screen
WIN_LEAD = 70   # a press up to ~60 frames before the window changes the screen inside it: the title press's menu response came 44-58 frames
                # after the press on the kit #11 device runs (STEP 8: press f1282 -> response f1329, inside 1301-1400)

def frames_tsv(path):
    """simstats-frames.tsv -> {frame: (ms, render_ms, gc)}; gc = a minor / major count changed at that frame"""
    out, prev = {}, None
    try:
        with open(path) as f:
            for l in f:
                p = l.rstrip("\n").split("\t")
                if len(p) < 6 or not p[0].isdigit(): continue
                fr, mi, ma = int(p[0]), p[4], p[5]
                out[fr] = (float(p[1]), float(p[2]), prev is not None and prev[0] == fr - 1 and (prev[1], prev[2]) != (mi, ma))
                prev = (fr, mi, ma)
    except (OSError, ValueError): return {}
    return out

def clock_at(h, t0, t1):
    """the measured clock across host wall times t0..t1: '500', '500>444' (a settled change within 10 s of the window), '' (none measured)"""
    cm = h.get("cmeas") or []
    if not cm: return h.get("clock", "")
    cur, seq = cm[0][0], []
    for mhz, at in cm[1:]:
        if at is None: continue   # (the end reading)
        if at < t0 - 10: cur = mhz
        elif at <= t1 + 10: seq.append(mhz)
    return ">".join(str(x) for x in [cur] + [x for i, x in enumerate(seq) if x != ([cur] + seq)[i]])

def window_info(h, e, rb):
    h, e = h or {}, e or {}
    w0, w1 = 1301 + rb, 1400 + rb
    hb, probes = h.get("hb", {}), e.get("probe", {})
    beats = [hb[f] for f in sorted(hb) if w0 - 1 <= f <= w1]
    s0 = hb.get(w0 - 1, (None, None))[1]
    q = ""
    if s0 is None and (w0 - 1) in probes: s0, q = probes[w0 - 1], "?"
    draws = [b[2] for b in beats if b[2] is not None]
    mf = h.get("menu_f") or e.get("menu_frame")   # the scenes at the first heartbeat after the menu arrived = the menu's scenes
    ref = next((hb[f][3] for f in sorted(hb) if mf and f > mf and hb[f][3]), None)
    mm = [b[3] == ref for b in beats if b[3] is not None] if ref else []
    ev, contaminated = [], False
    for p, rsp, btn in h.get("m1ev", []):
        if w0 - WIN_LEAD <= p < w1 or w0 <= rsp < w1:
            ev += [f"p{p}", f"r{rsp}"]; contaminated = True
        elif p == w1: ev.append(f"e{p}")   # a press ON the last frame (Measure's scripted down): its frame is in, its effect is not
    s1 = s0 if not contaminated else "chg"
    if not h and (w0 - 1) in probes and (1450 + rb) in probes and probes[w0 - 1] != probes[1450 + rb]: s1, contaminated = "chg", True
    if not beats and not probes: screen = ""
    elif mm and not all(mm): screen = "other"
    elif contaminated or (draws and max(draws) > 1.05 * max(1, min(draws))): screen = "mixed"
    elif s0 == "none": screen = "title" + q
    elif s0 in ("sel",) or (s0 or "").startswith("b"): screen = "menu" + q
    elif s0 == "x": screen = "other"
    else: screen = ""
    sel = f"{s0 or '?'}{q}>{s1 or '?'}"
    t0 = hb.get(w0 - 1, (None,))[0]
    t1 = beats[-1][0] if beats else None
    clk = clock_at(h, t0, t1) if (h and t0 is not None and t1 is not None) else (h.get("clock", "") if h else "")
    cfg = (";".join(sorted(set(h.get("envs", [])))) or "-") if h else ""
    sf = frames_tsv(e["sf_path"]) if e.get("sf_path") else {}
    bad = {p for p, rsp, btn in h.get("m1ev", []) for p in (p, rsp)}
    keep = [sf[f][0] for f in range(w0, w1 + 1) if f in sf and not sf[f][2] and f not in bad]
    allw = [sf[f][0] for f in range(w0, w1 + 1) if f in sf]
    same = e.get("idle_ms") is None or (allw and abs(sum(allw) / len(allw) - e["idle_ms"]) < 0.05)   # (the per-frame file is this run's)
    msx = num(sum(keep) / len(keep), 2) if len(keep) >= 50 and same else ""
    return (screen, sel, "/".join(str(x) for x in draws), ",".join(ev), clk, cfg, num(e.get("mtdraws"), 1), msx)

def comparable(a, b):
    """(True, '') when a percentage between two windows is fair: the same screen (title or menu; for a menu the same button selected) and
    the same known clock across both windows; else (False, why). Rows written before the window columns are never comparable (perf_history.py
    add on the devicelogs parent fills the columns of the rows already in the table)."""
    sa, sb = (a.get("win_screen") or "").rstrip("?"), (b.get("win_screen") or "").rstrip("?")
    if not sa or not sb: return False, "the screen of a window is not recorded"
    if sa != sb: return False, f"different screens ({a['win_screen']} vs {b['win_screen']})"
    if sa not in ("title", "menu"): return False, f"both windows {sa}: not one steady screen"
    ka, kb = (a.get("win_sel") or "?").split(">")[0], (b.get("win_sel") or "?").split(">")[0]
    if sa == "menu" and "?" not in ka + kb and "sel" not in ka + kb and ka != kb: return False, f"different buttons selected ({ka} vs {kb})"
    ca, cb = a.get("win_clock") or "", b.get("win_clock") or ""
    if not ca or not cb: return False, f"the clock at a window is unknown ({ca or '?'} vs {cb or '?'} MHz)"
    if ">" in ca + cb or ca != cb: return False, f"clock differs at the windows ({ca} vs {cb} MHz)"
    note = " (engine log only: screen from the menu probes)" if "?" in (a.get("win_screen") or "") + (b.get("win_screen") or "") else ""
    return True, note

def win_label(r):
    return (f"{r.get('win_screen') or '?'} window [sel {r.get('win_sel') or '?'}, draws {r.get('win_draws') or '?'}"
            + (f", events {r['win_events']}" if r.get("win_events") else "") + f", {r.get('win_clock') or '?'} MHz"
            + (f", env {r['win_cfg']}" if r.get("win_cfg") not in ("", "-", None) else "") + "]")

# ------------------------------------------------------------------------------------------------------------- table + output
def load_tsv(path):
    rows = []
    if not os.path.exists(path): return rows
    with open(path, encoding="utf-8") as f:
        hdr = None
        for l in f:
            l = l.rstrip("\n")
            if not l or l.startswith("#"): continue
            parts = l.split("\t")
            if hdr is None: hdr = parts; continue
            r = dict.fromkeys(COLS, "")
            r.update({k: v for k, v in zip(hdr, parts) if k in r})
            rows.append(r)
    return rows

def sort_rows(rows):
    return sorted(rows, key=lambda r: (vkey(r["version"]), r["date"], r["time"] or "99:99", r["folder"], r["log"], r["key"]))

def same_run(x, r):
    if x["key"] == r["key"]: return True
    if "/" not in x["log"] and "/" not in r["log"]: return False   # two different host logs are two runs, whatever engine log they met
    return bool(r["ekey"] and r["ekey"] in (x["ekey"], x["key"])) or bool(x["ekey"] and x["ekey"] == r["key"])

def merge(old, new):
    rows, added = list(old), []
    for r in new:
        o = next((x for x in rows if same_run(x, r)), None)
        if o is None:
            rows.append(r); added.append(r); continue
        if "/" in o["log"] and "/" not in r["log"]:   # an engine-log-only row met its host log: the host log's values win
            o.update({k: r[k] or o[k] for k in COLS})
            continue
        for k in COLS:
            if not o[k] and r[k]: o[k] = r[k]
    return sort_rows(rows), added

def write_tsv(path, rows):
    with open(path, "w", encoding="utf-8") as f:
        f.write("# Shepherd on the Vita: one row per device app run. Written by vita/tools/perf_history.py (numbers, app names, title\n"
                "# ids, dates, build stamps and commit ids only); the columns are explained in its header, the story in vita/CHANGELOG.md.\n")
        f.write("\t".join(COLS) + "\n")
        for r in rows: f.write("\t".join(r[k] for k in COLS) + "\n")

def clockcell(r):
    """the system's clock (* = not logged) and, when the app measured another one (kit #7 on), the measured: '444 (measured 500, DIFFER)'"""
    if not r["clock"] and not r.get("clock_meas"): return ""
    c = (r["clock"] + ("" if r["clock_src"] == "logged" else "*")) if r["clock"] else "?"
    cm, cn = r.get("clock_meas", ""), r.get("clock_note", "")
    if cm and (cm != r["clock"] or cn):
        c += f" (measured {cm}" + (f", {cn}" if cn else "") + ")"
    return c

def eff_clock(r):
    """the clock a comparison uses: the MEASURED one when the run has it (kit #8 fix), '' when it changed during the run (no comparison at a
    clock), else the system's"""
    cm = r.get("clock_meas", "")
    if cm: return "" if ">" in cm else cm
    return r["clock"]

def kib_mib(x): return num(int(x) / 1024.0, 1) if x else ""

def ended(r):
    """the host log has its end line (PASS END / FAIL END: the end-of-run summaries with the true peaks)"""
    return r["result"] in ("PASS", "FAIL")

def passed(r):
    """usable as a 'before' in the comparison: it passed (PASS, PASS M1, PASS*) or has an idle window"""
    return r["result"].startswith("PASS") or bool(r["idle_ms"] or r.get("play_ms"))

def write_md(path, rows):
    L = ["# Shepherd on the Vita: the numbers, run by run", "",
         "Generated by `vita/tools/perf_history.py` from `vita/perf-history.tsv` -- do not edit by hand (read-logs.sh appends every new",
         "pull). What changed between versions, in words: `vita/CHANGELOG.md`.", "",
         "- **clock** = CPU MHz; `*` = not logged (the builds before 2026-10-13 never raised it: 333 = the launch clock; kit #2 apps set 444 at start). "
         "`(measured N)` = the clock the app timed itself (kit #7 on), shown when it is not the system's report: `DIFFER` = set outside the "
         "system (a PSVshellPlus profile), `444>500, changed at 106 s` = changed during the run; comparisons use the measured clock.",
         "- **idle menu** = the engine's own timer over frames 1301-1400 with the Main Menu idle (mean ms per frame, fps = 1000 / ms); "
         "**wall** = the same frames by the host's clock (includes the logging and the gaps between frames: 3-15 %, ~10-13 ms per "
         "frame, above the engine's timer). Only runs whose window IS the idle menu have it; **play window** = the play apps' frames "
         "1301-1400 wherever the owner was (and *other frames* = a menu app whose window was not the idle frames): never an idle-menu "
         "number; session fps = the [time] wall-clock average.",
         "- **boot** = Main entered -> Main Menu present; **load** = the heartbeat window holding the menu's arrival (frames 800 -> 850); "
         "**splash / total / left** (kit #7, the K7 BOOT line, process time): time to the Devolver logo's first frame / to the Main Menu "
         "(the logo included) / work left after the logo (the real time from its end to the menu beyond the game time the game's own "
         "sequence spends there: 0 = as fast as the original's splash allows); "
         "**GC full** = the longer forced full GC at the idle menu; **GCs** = minor / major collections by the end of the log; "
         "**newlib / arena** = the highest use seen in the log, in MiB (newlib of 128); `>=` = the log stops without its end summary "
         "(stopped, froze, or the PS button), so the run went at least that high.",
         "- **result**: PASS / FAIL = the host log's end line; PASS M1 = the menu answered the pad (the log has no end line); "
         "`PASS*` = only the engine's log was seen and it ended normally (the host's end checks -- FAIL lines, OOM, heap-cap "
         "refusals -- not seen).",
         "- **version** = the device kit (vita/CHANGELOG.md), from the build stamp or the probe's header; a row whose log names "
         "neither gets the kit current when it ran (a guess; the TSV gives it no commit).", ""]
    main = [r for r in rows if r["title"] in MAIN_LINE]
    L += ["## The main app line (menu -> play)", "",
          "| version | date | app | title | clock | result | idle menu ms (fps) | render / logic ms | wall ms | play window ms / session fps | boot s | splash / total / left s | load s | GC full s | idle minor GC ms | GCs | newlib MiB | arena MiB | shown |",
          "|---|---|---|---|---:|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|"]
    for r in main:
        idle = f"{r['idle_ms']} ({r['idle_fps']})" if r["idle_ms"] and r["window"] == "menu" else ""   # (kit #8 fix: idle menu windows only)
        pw = " / ".join(x for x in ((r.get("play_ms") or "") + (" *other frames*" if r["window"] == "other" else ""), (f"{r['avg_fps']} fps" if r["avg_fps"] else "")) if x)
        rl = f"{r['render_ms']} / {r['logic_ms']}" if r["render_ms"] else ""
        gcf = num(fnum(r["gc_full_ms"]) / 1000.0, 2) if r["gc_full_ms"] else ""
        gcs = f"{r['gc_minor']} / {r['gc_major']}" if r["gc_minor"] else ""
        lb = "" if ended(r) else ">= "
        nl = (lb + kib_mib(r["newlib_peak_kib"])) if r["newlib_peak_kib"] else ""
        ar = (lb + kib_mib(r["arena_peak_kib"])) if r["arena_peak_kib"] else ""
        k7b = f"{r['splash_at_s'] or '-'} / {r['boot_total_s']} / {r['work_left_s'] or '-'}" if r["boot_total_s"] else ""
        L.append(f"| {r['version']} | {r['date']} | {r['app']} | {r['title']} | {clockcell(r)} | {r['result']} | {idle} | {rl} | "
                 f"{r['idle_wall_ms']} | {pw} | {r['boot_menu_s']} | {k7b} | {r['menu_load_s']} | {gcf} | {r['gc_idle_minor_ms']} | {gcs} | "
                 f"{nl} | {ar} | {r['shown']} |")
    other = [r for r in rows if r["title"] not in MAIN_LINE]
    L += ["", "## Probe, GL Diag, hello", "",
          "| version | date | app | title | clock | result | card seq MiB/s | card 4 KiB ms | GPU us/draw |",
          "|---|---|---|---|---:|---|---:|---:|---:|"]
    for r in other:
        L.append(f"| {r['version']} | {r['date']} | {r['app']} | {r['title']} | {clockcell(r)} | {r['result']} | {r['card_mibs']} | "
                 f"{r['card_4k_ms']} | {r['gpu_us_draw']} |")
    L += ["", f"{len(rows)} runs. Source folders: " + ", ".join(sorted({r['folder'] for r in rows})) + "."]
    with open(path, "w", encoding="utf-8") as f: f.write("\n".join(L) + "\n")

def best_idle(rows, version, clock=None):
    """the fastest idle-menu run of a version (kit #8 fix: at one clock -- the given one, else the version's most common measured / logged clock,
    so a 500 MHz extras run is never the 'best' of a 444 MHz kit)"""
    c = [r for r in rows if r["version"] == version and r["title"] in MAIN_LINE and r["window"] == "menu" and r["idle_ms"] and eff_clock(r)
         and r.get("win_cfg", "") in ("", "-")]   # (kit #12: as shipped only -- kit #10's "best" was STEP 8 / X5a with the PC float rules)
    if not c: return None
    if clock is None:
        counts = {}
        for r in c: counts[eff_clock(r)] = counts.get(eff_clock(r), 0) + 1
        clock = max(counts, key=lambda k: (counts[k], k == "444"))
    c = [r for r in c if eff_clock(r) == clock]
    return min(c, key=lambda r: fnum(r["idle_ms"])) if c else None

def describe(r):
    return f"{r['idle_ms']} ms ({r['idle_fps']} fps) [{r['title']} {r['app']}, {clockcell(r) or '?'} MHz]"

def same_clock(a, b): return bool(eff_clock(a)) and eff_clock(a) == eff_clock(b)

def clock_note(a, b):
    """'' when both ran at the same (known) clock -- the measured one where there is one; else the two clocks -- 333 vs 444 MHz alone is ~1.33x,
    500 vs 444 ~1.13x, so no percentage then (nor for a run whose clock changed during it)"""
    if same_clock(a, b): return ""
    ca, cb = eff_clock(a) or (a.get("clock_meas") or "?"), eff_clock(b) or (b.get("clock_meas") or "?")
    return f"clock differs ({ca} vs {cb} MHz): no %"

def pct(a, b):
    a, b = fnum(a), fnum(b)
    return f" ({(b - a) / a * 100:+.0f} %)" if a and b is not None else ""

def prior(rows, r):
    """the latest run of this app (else of the app it grew out of) from an earlier version that passed or has an idle window (never a
    froze-before-frame-0 run): one with a comparable window first"""
    line = [r["title"]] + PREDECESSORS.get(r["title"], [])
    if r["app"].endswith(" (MT)") and r["title"] in MT_SIBLING: line = [MT_SIBLING[r["title"]]] + line   # an MT run: its MT sibling first
    older = {t: [o for o in rows if o["title"] == t and vkey(o["version"]) < vkey(r["version"]) and passed(o)] for t in line}
    # kit #12 compare audit: a run with the same env switches and a comparable window first (kit #11's Measure took kit #10's X6-B, the
    # logging diet's arm, as its 'before'; the as-shipped X6-A is the like-for-like)
    c = [o for o in older[r["title"]] if o.get("win_cfg", "") == r.get("win_cfg", "") and comparable(o, r)[0]]   # (the app itself only)
    if c: return c[-1]
    # kit #8 fix: the same (measured) clock first -- kit #7's 500 MHz extras runs are not the 'before' of a 444 MHz run
    for t in line:
        c = [o for o in older[t] if (o["idle_ms"] or o.get("play_ms")) and o["window"] == r["window"] and same_clock(o, r)]
        if c: return c[-1]
    for t in line:
        c = [o for o in older[t] if same_clock(o, r)]
        if c: return c[-1]
    for t in line:
        c = [o for o in older[t] if (o["idle_ms"] or o.get("play_ms")) and o["window"] == r["window"]]
        if c: return c[-1]
    for t in line:
        if older[t]: return older[t][-1]
    return None

def change(o, r, k, unit, conv=lambda x: x, with_pct=False, mark=lambda row: ""):
    """'before -> now unit' for column k (just 'now unit' without a before); the percentage only at the same clock"""
    now = mark(r) + conv(r[k]) + unit
    if not (o and o[k]): return now
    return f"{mark(o)}{conv(o[k])} -> {now}" + (pct(o[k], r[k]) if with_pct and same_clock(o, r) else "")

def compare(rows, new=None):
    out = []
    versions = sorted({r["version"] for r in rows if r["version"]}, key=vkey)
    if new:
        nv = sorted({r["version"] for r in new if r["version"]}, key=vkey)
        cur = nv[-1] if nv else None
    else:
        cur = versions[-1] if versions else None
    if not cur: return out
    prev = [v for v in versions if vkey(v) < vkey(cur)]
    b = best_idle(rows, cur)
    a = None
    for v in reversed(prev):   # (kit #8 fix: the earlier version's best at the same clock as this one's, else its own best)
        a = (best_idle(rows, v, eff_clock(b)) if b else None) or best_idle(rows, v)
        if a: break
    out.append("vs last version -- idle Main Menu (frames 1301-1400, the engine's frame timer):")
    if a and b:
        notes = ([] if a["title"] == b["title"] else ["different apps: each version's best"]) + ([clock_note(a, b)] if clock_note(a, b) else [])
        ok, why = comparable(a, b)
        notes += [why.strip()] if why.strip() else []
        out.append(f"  best idle menu {a['version']} -> {b['version']}: {describe(a)} -> {describe(b)}"
                   + (pct(a["idle_ms"], b["idle_ms"]) if ok else "") + (f" -- {'; '.join(notes)}" if notes else ""))
    elif b: out.append(f"  best idle menu {b['version']}: {describe(b)} (no earlier version with an idle-menu window)")
    else: out.append(f"  {cur}: no idle-menu window yet in this version's runs")
    # same app (else the app it grew out of), previous version
    lb = lambda row: "" if ended(row) else ">="
    for r in (new or [r for r in rows if r["version"] == cur]):
        if r["title"] not in MAIN_LINE: continue
        o = prior(rows, r)
        if r.get("win_cfg") not in ("", "-", None):   # kit #12: an A/B arm (env switches) -> this version's as-shipped run of the app
            arm = [x for x in rows if x["version"] == r["version"] and x["title"] == r["title"] and x.get("win_cfg") in ("-", "")
                   and x is not r and (x["idle_ms"] or x["play_ms"])]   # ("" = an engine log alone: its switches unknown)
            o = ([x for x in arm if comparable(x, r)[0]] or arm or [o])[-1]
        bits = []
        if r["window"] == "menu" and r["idle_ms"]:
            ok, why = comparable(o, r) if o else (False, "")
            bits.append(f"idle {change(o, r, 'idle_ms', ' ms', with_pct=ok)}, {change(o, r, 'idle_fps', ' fps')}" + (f" (no %: {why})" if o and not ok else ""))
        if r["window"] in ("play", "other"):
            bits.append(f"{r['window']} window {r['play_ms']} ms ({'where the owner was' if r['window'] == 'play' else 'not the idle frames'}, not an idle-menu number)" if r.get("play_ms") else r["window"])
            if r.get("play_ms"):   # kit #12: say which screen it was, and compare only with the same screen at the same clock
                bits.append(win_label(r))
                if o and o.get("play_ms"):
                    ok, why = comparable(o, r)
                    bits.append(f"vs {o['version']} {o['play_ms']} ms on a {o.get('win_screen') or '?'} window" + (pct(o["play_ms"], r["play_ms"]) + why if ok else f": no % -- {why}"))
            if r["avg_fps"]: bits.append(f"session average {r['avg_fps']} fps")
        if r.get("clock_note") or (r.get("clock_meas") and r["clock_meas"] != r["clock"]):
            bits.append(f"clock measured {r['clock_meas'] or '?'} MHz" + (f" ({r['clock_note']})" if r.get("clock_note") else ""))
        if r["boot_menu_s"]: bits.append(f"boot to menu {change(o, r, 'boot_menu_s', ' s', with_pct=True)}")
        if r["boot_total_s"]: bits.append(f"boot (kit #7 K7 BOOT): time to splash {r['splash_at_s'] or '-'} s, total {r['boot_total_s']} s, work left after the splash {r['work_left_s'] or '-'} s")
        if r["newlib_peak_kib"]: bits.append(f"newlib peak {change(o, r, 'newlib_peak_kib', ' MiB', kib_mib, mark=lb)}")
        if r["gc_full_ms"]: bits.append(f"full GC {change(o, r, 'gc_full_ms', ' ms')}")
        if o:
            tag = (f"vs {o['version']}" + ("" if o["title"] == r["title"] else f" {o['title']} {o['app']}")
                   + f" {o['result']} at {clockcell(o) or '?'} MHz" + (f", {clock_note(o, r)}" if clock_note(o, r) else ""))
        else:
            tag = "first run of this app"
        out.append(f"  {r['title']} {r['app']} {r['version']} ({clockcell(r) or '?'} MHz, {r['result'] or '?'}; {tag}): "
                   + ("; ".join(bits) or "no numbers"))
    return out

def timeline(h, e, sf):
    """kit #12 compare audit, per frame: 't' = the title screen settled, 'm<k>' = the menu with the k-th button selected, settled; frames
    within 20 of a press or response, transitions, other screens and GC frames are left out. From the menu's arrival and the menu watch."""
    mf = e.get("menu_frame")
    if not mf or not sf: return {}
    marks = sorted(h.get("m1ev", []))
    out, state, k, until = {}, "t", 0, mf + 60
    # a title press the menu watch did not log (the first heartbeat with a button selected comes before the first logged press): the
    # frames before that heartbeat are left out, the menu starts there
    fb = next((f for f in sorted(h.get("hb", {})) if f >= mf and (h["hb"][f][1] or "").startswith("b")), None)
    if fb is not None and (not marks or marks[0][0] > fb): state, k, until = "m", int(h["hb"][fb][1][1:]), fb + 1
    # frames within 10 s of a measured clock change are left out (the watchdog settles a change on two 5 s readings)
    walls = sorted((f, v[0]) for f, v in h.get("hb", {}).items())
    chg = [at for mhz, at in (h.get("cmeas") or [])[1:] if at is not None]
    def wall_of(f):
        lo = max((x for x in walls if x[0] <= f), default=None); hi = min((x for x in walls if x[0] >= f), default=None)
        if lo is None or hi is None: return None
        return lo[1] if hi[0] == lo[0] else lo[1] + (hi[1] - lo[1]) * (f - lo[0]) / (hi[0] - lo[0])
    i, last = 0, max(sf)
    for f in range(mf, last + 1):
        if chg:
            w = wall_of(f)
            if w is None or any(abs(w - t) <= 10 for t in chg): continue
        while i < len(marks) and f >= marks[i][0]:
            p, rsp, btn = marks[i]
            if state == "t": state, k = "m", 1
            elif btn == "Down" and state == "m": k += 1
            elif btn == "Up" and state == "m": k -= 1
            elif btn == "Circle" and state == "o": state = "m"
            else: state = "o"   # a cross on a button opens another screen; anything else: not tracked
            until = rsp + 20; i += 1
        if f < until or f not in sf or sf[f][2] or state == "o": continue
        out[f] = "t" if state == "t" else f"m{k}"
    return out

def ab(specs):
    """kit #12: compare two runs (A B as <pull folder>/<host log>) -- their windows (the % only when comparable), then the frames on which
    both show the same settled screen (simstats-frames.tsv + the menu watch): the same frame numbers first, else each run's own nearest"""
    import statistics as st
    if len(specs) != 2: print("ab: give two runs, e.g. 20261104-163746/play-gaot.txt 20261104-173425/play-gaot.txt"); return 2
    got = []
    for s in specs:
        s = s if os.path.isabs(s) else os.path.join(os.environ.get("SHEPHERD_DEVICELOGS", "$SHEPHERD_WORK/devicelogs"), s)
        d, log = os.path.dirname(s), os.path.basename(s)
        rows = parse_folder(d)
        r = next((x for x in rows if x["log"] == log), None)
        if r is None: print(f"ab: {s}: no such run"); return 2
        data, lines = read_lines(s); h = parse_host(log, lines)
        e = {}
        rel = h.get("logdir") or os.path.join("runtime", "logs", h["base"])   # (H1: an env file's SHEPHERD_LOG_DIR, else the app's own)
        ep = os.path.join(d, rel, "engine.log")
        if os.path.isfile(ep): e = parse_engine(read_lines(ep)[1])
        sf = frames_tsv(os.path.join(d, rel, "simstats-frames.tsv"))
        got.append((s, r, h, e, sf))
    (sa, ra, ha, ea, fa), (sb, rb_, hb_, eb, fb) = got
    va, vb = ra["idle_ms"] or ra["play_ms"], rb_["idle_ms"] or rb_["play_ms"]
    ok, why = comparable(ra, rb_)
    print(f"A {sa}: {va} ms on a {win_label(ra)}"); print(f"B {sb}: {vb} ms on a {win_label(rb_)}")
    print(f"  the [sim] windows: " + (f"{va} -> {vb} ms{pct(va, vb)}{why}" if ok else f"no % -- {why}"))
    ta, tb = timeline(ha, ea, fa), timeline(hb_, eb, fb)
    for state in sorted(set(ta.values()) & set(tb.values())):
        common = sorted(f for f in ta if ta[f] == state and tb.get(f) == state)
        if len(common) >= 30:
            fs_a, fs_b, how = common, common, f"the same {len(common)} frames f{common[0]}-f{common[-1]}"
        else:
            near = lambda t: sorted((f for f in t if t[f] == state), key=lambda f: abs(f - 1350))[:100]
            fs_a, fs_b = sorted(near(ta)), sorted(near(tb))
            if min(len(fs_a), len(fs_b)) < 30: continue
            how = f"NOT the same frames: A {len(fs_a)} frames f{fs_a[0]}-f{fs_a[-1]}, B {len(fs_b)} frames f{fs_b[0]}-f{fs_b[-1]}"
        ma, mb = st.mean(fa[f][0] for f in fs_a), st.mean(fb[f][0] for f in fs_b)
        rA, rB = st.mean(fa[f][1] for f in fs_a), st.mean(fb[f][1] for f in fs_b)
        same_clk = (ra.get("win_clock") or "") == (rb_.get("win_clock") or "") and ra.get("win_clock") and ">" not in ra["win_clock"]
        name = "title" if state == "t" else f"menu, button {state[1:]} selected"
        print(f"  same screen ({name}), {how}: frame {ma:.2f} -> {mb:.2f} ms" + (pct(ma, mb) if same_clk else " (no %: clock)")
              + f", render {rA:.2f} -> {rB:.2f} ms, logic {ma - rA:.2f} -> {mb - rB:.2f} ms")
    return 0

def expand(dirs):
    out = []
    for d in dirs:
        d = os.path.abspath(d)
        if not os.path.isdir(d): print(f"perf_history: not a folder: {d}", file=sys.stderr); continue
        subs = [s for s in os.listdir(d) if re.match(r"^20\d{6}-", s) and os.path.isdir(os.path.join(d, s))]
        def pulled(s):   # (kit #8: a kit's main pull before its extras pull -- the first copy of a run keeps its folder)
            ts = [os.path.getmtime(os.path.join(d, s, f)) for f in os.listdir(os.path.join(d, s)) if HOST_RE.match(f)]
            return (s[:8], max(ts) if ts else 0.0, s)
        subs.sort(key=pulled)
        has_logs = any(HOST_RE.match(f) for f in os.listdir(d)) or os.path.isdir(os.path.join(d, "runtime"))
        out += [d] if has_logs or not subs else [os.path.join(d, s) for s in subs]
    return out

def main(argv):
    ap = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    ap.add_argument("cmd", choices=("add", "rebuild", "show", "window", "ab"))
    ap.add_argument("dirs", nargs="*")
    ap.add_argument("--tsv", default=DEF_TSV); ap.add_argument("--md", default=DEF_MD)
    ap.add_argument("--dry-run", action="store_true")
    a = ap.parse_args(argv)
    old = load_tsv(a.tsv) if a.cmd != "rebuild" else []
    if a.cmd == "show":
        print("\n".join(compare(old))); return 0
    if a.cmd == "window":   # kit #12: read-logs.sh prints this per pull (reads the folders only)
        for d in expand(a.dirs):
            for r in parse_folder(d):
                if r["win_screen"] or r["idle_ms"] or r["play_ms"]:
                    print(f"  {r['log']}: {r['title']} {r['app']}: frames 1301-1400{' (+re-base)' if r['menu_frame'] and int(r['menu_frame']) > 1100 else ''}: "
                          f"{r['idle_ms'] or r['play_ms'] or '?'} ms (without press / GC frames {r['win_ms_x'] or '-'}) on a {win_label(r)}")
        return 0
    if a.cmd == "ab":
        return ab(a.dirs)
    new = []
    for d in expand(a.dirs): new += parse_folder(d)
    rows, added = merge(old, new)
    if a.dry_run:
        print("\t".join(COLS))
        for r in rows: print("\t".join(r[k] for k in COLS))
    else:
        write_tsv(a.tsv, rows); write_md(a.md, rows)
    shown = os.path.relpath(a.tsv, os.path.dirname(VITA)) if os.path.abspath(a.tsv).startswith(VITA + os.sep) else os.path.abspath(a.tsv)
    print(f"perf history: {len(added)} new run(s), {len(rows)} in all -> {shown} + {os.path.basename(a.md)}"
          + (" (dry run: nothing written)" if a.dry_run else ""))
    for r in added:
        print(f"  + {r['version'] or '?'} {r['date']} {r['time']} {r['title']} {r['app']} {r['log']}: {r['result']}"
              + (f", idle {r['idle_ms']} ms ({r['idle_fps']} fps)" if r['idle_ms'] and r['window'] == 'menu' else ""))
    if added or a.cmd == "rebuild":
        print("\n".join(compare(rows, added if a.cmd == "add" else None)))
    return 0

if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
