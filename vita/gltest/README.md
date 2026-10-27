# Shepherd GL Test (vita/gltest)

A small homebrew app that checks, on your Vita, that vitaGL runs the Shepherd renderer's shaders and GL calls
correctly, and measures how fast they are. It renders each shader into an off-screen image, reads the pixels back and
compares every one with a reference computed on the CPU. Synthetic test images only: no game code or files are in the VPK.
It changes nothing on the console except the log file and vitaGL's own shader-cache folder (`ux0:data/shader_cache/`).
While it runs it sets the clocks to 444/222/222/166 (the same as the probe), and restores them on exit.

Also here: `libshepgl.a`, the small C layer the engine will link on the Vita to reach vitaGL (`shepgl/`, notes in
`vita/notes/gltest.md`).

The Stylizer programs, their CPU reference and the Main-Menu-shaped frame (which runs the Stylizer pass) are owner-local since
2026-10-07 (fid1-fixup D1: the shader and the reference port a third-party effect): `engine/Derived/` (git-ignored). By default
the build leaves them out, whatever `engine/Derived` holds: it builds and checks the tracked programs only and logs the Stylizer
and menu-frame checks as skipped (`out/shepherd_gltest.vpk`, SHPD00007). `GLTEST_OWNER_LOCAL=1 ./build.sh` (fid1-fixup round 2:
opt-in) builds them in -- that VPK holds derived code, so it is `out/shepherd_gltest-OWNER-ONLY.vpk` with the owner-only title ID
SHPD00092: install it on the owner's Vita only, never share it.

## Before you run it
- **libshacccg.suprx** must be on the console (vitaGL compiles shaders with it). If you ran the probe's GPU test you
  already have it. If not: on your own console install PSM Runtime 1.00, then 2.00, then 2.01, then run **ShaRKF00D**; it
  writes `ur0:data/libshacccg.suprx`. Never download the file. Without it the test logs `SKIP` with these steps and exits.

## Run
1. Copy `out/shepherd_gltest.vpk` to the Vita (VitaShell FTP or USB) and install it. It installs as **Shepherd GL Test**
   (title ID SHPD00007, next to the probe and the earlier Shepherd builds).
2. Launch it. The first lines show on screen; then vitaGL takes the screen. You will see dark blue, then a few seconds of
   moving test images (the speed tests), then dark blue again. The whole run takes about 1 to 2 minutes.
3. At the end the screen turns **GREEN** (everything passed) or **RED** (something failed). Either way there is a
   **white bar, and it must be at the TOP edge of the screen**. If the bar is at the bottom, tell me: the image would be
   upside down in the game. On red, the small white squares under the bar count the failed checks.
4. Press **X, O or START** to exit.
5. Send back `ux0:data/shepherd/gltest.txt` (and `gltest.prev.txt` if you ran it more than once — it keeps the previous run), and say whether the white bar was at the top.

If it freezes or crashes, the log still has every line up to the failing step (the last `STEP` or `BEGIN` line says
where). Hold the power button to restart if needed.

## Reading the log
| prefix | meaning |
|---|---|
| `PASS` / `WARN` / `FAIL` | the main checks: what the engine will use (counted in the final colour) |
| `ALT-*` | the same checks with other compiler settings (strict options, vitaGL's postponed mode, mediump); informational |
| `CAND-*` | shaders that are not in the engine yet (Spine tint-black); informational |
| `INFO-OK` / `INFO-DIFF` | behaviour worth knowing that the engine does not rely on |
| `PERF` | timings: `serialized` = CPU + GPU with a full wait each frame; `pipelined` = what a game loop gets |
| `SHACCCG` | the shader compiler's own messages (vitaGL drops them; the glue catches them) |
| `SUMMARY`, `DONE` | the totals and the final verdict |

## Build (Mac)
`./build.sh` (needs `~/vitasdk`, cmake, ninja, python3; the build volume attached). It generates the shader text from the
engine's shaders, checks the GL signatures against vitaGL, builds `out/libshepgl.a` and `out/shepherd_gltest.vpk`,
checks symbols with `arm-vita-eabi-nm`, then runs the same test program on the Mac's desktop GL as a self-check
(headless, silent). `./build.sh --no-mac` skips the self-check.
