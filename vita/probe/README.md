# Shepherd Probe v2 (gate V0)

A small homebrew app that measures your Vita for the CotL port plan (`vita/PLAN.md`). Synthetic data only: no game
code or files. It changes nothing on the console except its log (`ux0:data/shepherd/probe.txt`, + `probe.prev.txt`) and, through
vitaGL's init in the GPU test, vitaGL's own shader-cache folder `ux0:data/shader_cache/` (shared by every vitaGL app). It sets the
CPU/GPU clocks to 444/222/222/166 first thing (the same clocks GTA:SA and Max Payne ports use), logs them (`CLK` lines) at start
and at the end and restores the launch clocks at exit, and it briefly allocates then frees most free memory to measure it.

Version `shepherd-probe-3` (2026-10-13): the clocks are set before every other test, and the GPU test decides "vitaGL is up" from
vitaGL's own state (context, shader patcher, render target, a 4 KiB VRAM allocation) instead of `vglInitExtended`'s return value,
which in this vitaGL only says whether the size was clamped: the 2026-10-09 `FAIL gpu` was that misread (`vita/notes/clocks-vgl-init.md`).
If vitaGL really fails, the probe ends vitaGL's splash and brings its text screen back (it keeps 2 MiB of video memory for that).

Version `shepherd-probe-4` (kit #4): the `CFG` lines list every non-comment line of `ur0:tai/config.txt` and `ux0:tai/config.txt`
(sections and plugin paths), not only kubridge / PSVshell, so the log names a core-unlock plugin (the 4th CPU core) too.

## Before you run it
1. HENkaku/Ensō + VitaShell (you have these).
2. **libshacccg.suprx** (needed for the GPU test; vitaGL compiles shaders with it). Extract it on your own console:
   install PSM Runtime 1.00, then 2.00, then 2.01 (that order), then run **ShaRKF00D**. It writes `ur0:data/libshacccg.suprx`.
   Do not download the file from anywhere. Without it the probe still runs everything except the GPU test.
3. Optional, for the SD/memory-card speed test: copy a ~100 MB file to `ux0:data/shepherd/readtest.bin`.
   Make one on the Mac with `dd if=/dev/urandom of=readtest.bin bs=1m count=100`.
4. Optional: kubridge in `ur0:tai/config.txt` under `*KERNEL` (the probe only reports whether it is listed).

## Run
1. Copy `out/shepherd_probe.vpk` to the Vita (VitaShell FTP or USB) and install it.
2. Launch **Shepherd Probe**. It prints progress on screen for the first five tests (about 1–3 minutes).
3. For the GPU test the screen goes dark blue, then **green when finished** (the on-screen text stops then; the log continues).
4. Press **X, O or START** to exit.
5. Send back `ux0:data/shepherd/probe.txt`.

If it freezes or crashes, the log still has every line up to the step that failed (`BEGIN <test>` with no `PASS`).
Hold the power button to restart if needed; nothing is changed on the console.

## What it measures
| Test | Why the port needs it |
|---|---|
| memory | real free main RAM / CDRAM / PHYCONT and largest blocks with ATTRIBUTE2=12 (plan assumes 365 / 112 / 26 MiB) |
| clocks | whether 444 MHz sticks |
| presence | libshacccg (vitaGL shader compiler), kubridge/PSVshell listed in config.txt |
| storage | MiB/s sequential + 4 KiB random reads (asset streaming budget) |
| cpu | the Stylizer dither math, memory latency, float, memcpy, interpreter-style dispatch — **same C as the Mac** (`out/mac_ref.h`), so each line gives a "Vita is N× slower" ratio |
| gpu | cost of 550 separate textured draws vs 1 batched draw, and the GPU version of CotL's dither pass at 960×544 |

Executable-memory/JIT tests are not part of this build.

The Stylizer parts (the cpu kernel and the gpu dither pass) are owner-local since 2026-10-07 (fid1-fixup D1: a port of a
third-party effect): `engine/Derived/vita-probe/` (git-ignored). By default the probe leaves them out and skips those two
measurements (`out/shepherd_probe.vpk`, SHPD00001). `PROBE_OWNER_LOCAL=1 ./build.sh` (fid1-fixup round 2: opt-in) builds them in --
that probe holds derived code: `out/shepherd_probe-OWNER-ONLY.vpk`, owner-only title ID SHPD00093, never shared.

## Build (Mac)
`./build.sh` — needs `~/vitasdk`, cmake, ninja. Regenerates the Mac reference numbers and `out/shepherd_probe.vpk`.
