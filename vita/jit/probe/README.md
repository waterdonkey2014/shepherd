# Shepherd JIT probe (kit #7 JIT stage 1) — BUILT, NOT YET RUN on a device

A small homebrew app that measures the device facts a JIT on the PS Vita would depend on: the unknowns the read-only JIT study left
open (`$SHEPHERD_WORK/jitstudy/verdict.md` §8 stage 1) and the questions kit #8's design is parameterised on
(`vita/notes/kit8-baseline-jit-design.md` §6). Synthetic code only: it generates and runs a handful of tiny routines it writes itself
(return a constant, add to the argument, a countdown loop, a call out to the eboot), in **A32 and Thumb-2** (T32 = the ISA kit #8's
stencils use). **No game code, no game assets, nothing derived.** Revision 2 (`shepherd-jitprobe-2`, VPK 01.01) = the kit #7 review
fixes (`vita/notes/kit7-jit.md` "Review fixes").

## The owner's confirmation gate (the hard rule)

On the console the probe does **nothing that touches executable memory until the owner presses X**. `main_vita.c` runs the harness
with `auto_confirm` off; `probe_core.c` allocates no ForVM block and generates no code before `plat_confirm`, which first waits until
**every button is released** (an X held from the launch, or a stuck button, never counts) and then needs a **fresh X press** (seen on
two polls in a row). O / START exits without allocating anything. **This app has not been run on a device.** Its first device run is
the owner's call (the standing rule: no JIT / run-time-generated code runs on a device without the owner's explicit go-ahead; if the
console's safety checks decline the exec-memory step, we do not work around them).

Off-device validation (both have been run):
- **Mac harness** (`build-mac.sh`): the same `probe_core.c` on real executable memory (`MAP_JIT` + `pthread_jit_write_protect_np` +
  `sys_icache_invalidate`) — thread handshakes, timing, timeouts, the crash-safe log, skip-on-relaunch. The Mac runs A64, its W^X is
  per thread (so the Vita-only questions print "n/a" or "harness check") and it has no 16 MiB quota: a Mac pass proves the harness,
  never the Vita's numbers.
- **qemu encoding check** (`qemu/run.sh`): `emit.c` + `native.c` bare-metal on an emulated Cortex-A9 (`qemu-system-arm -M vexpress-a9`,
  already installed, audio off): every A32 and T32 routine, the register call, the patched BL / BLX to Thumb-2 and ARM helpers at both
  edges of the reach and just past it — 42 checks. It proves the **encodings**; qemu keeps its code coherent by itself, so caches,
  ForVM and the Open / Close model stay the device's questions.

vlx is **not** used for the exec-memory path: it does not model ForVM (Open/Close are unimplemented on Vita3K, and normal memory
executes there), so a vlx run would prove nothing about the console — the study's own point.

## What it measures (one log line each → `ux0:data/shepherd/jitprobe.txt`, append-then-close, crash-safe)

Safest first; the two tests that may fault on some firmware come last.

1. **the block** — free user memory before/after a 4 MiB `sceKernelAllocMemBlockForVM` (does it come out of user RAM?); its distance
   to **this probe's** code against A32 BL (±32 MiB) and Thumb-2 BL (±16 MiB). The probe's eboot is tiny: Shepherd's (45–62 MiB, then
   newlib and the arena) lays memory out differently, so kit #8 re-measures the distance inside Shepherd.
2. **the basic path, A32 and T32** — Open, write one routine, Sync, Close, call (the `__clear_cache` no-op trap: Sync must do the flush).
3. **Sync cost** — 2,000 small routines written + synced under one Open, then all verified.
4. **Open / Close cost** — 1,000 bare Open+Close pairs, and 500 routines each in its own Open / write / Sync / Close bracket (the
   per-batch alternative to open-once), against test 3's per-write cost.
5. **call out of the block, A32 and T32** — through a register (MOVW/MOVT + BLX, always reachable) and by a patched direct BL to the
   Thumb-2 helper and BL / BLX to an ARM-mode one (reach-dependent; out of reach = a veneer is needed).
6. **exec ratio, A32 and T32** — the **same** two-instruction loop (SUBS #1 / BNE) in the code block and in the eboot's `.text`
   (`native.c`, inline asm: identical bytes), best of 3 interleaved: ~1.00× = code in the ForVM block runs at native speed.
7. **second block** — a 16 MiB ForVM alloc after the 4 MiB one: the per-process VM quota (`0x80024B0B` = reached, as the field reports).
8. **nested Open** — a second Open on one thread, then a second thread's Open while the main thread holds the domain (the azaharvita
   race: `0x80010058` = refused). No code is written between the calls, so nothing can fault.
9. **code run while open** *(may fault)* — write, Sync and call with the domain still open, and the countdown loop timed open vs closed:
   the open-once-never-close model needs this.
10. **a write by a thread that never called Open** *(may fault)* — while the main thread holds Open: can a compiler thread write
    without opening?
11. **cross-core** — code another core has **already run** is rewritten in place and synced by a writer on core 1; the caller on core 2
    runs it again, 200 rounds (fresh / stale counts). Each thread reports the core it actually ran on (`sceKernelGetCpuId`), so "both
    on one core" reads INCONCLUSIVE, never PASS. It runs in the open-once model when 9 and 10 passed this launch, else in per-write
    brackets, and says which.

Worker threads have timeouts: a worker that does not finish gives "TIMEOUT … no verdict" (never a FAIL or MISMATCH) and the probe stops
there without freeing the block.

**Restarts.** `jitprobe.txt` is **never rotated or truncated**: every launch appends under its own `=== … launch N ===` header, so a
crashed run's lines survive any number of relaunches. `jitprobe.state` holds the test last entered; a launch that finds a test name
there (the previous launch stopped inside it — a fault, a hang, a timeout) logs a NOTE, adds it to `jitprobe.faults` and **skips it from
then on**, so the rest still run. Delete `jitprobe.faults` to try a skipped test again. `jitprobe.count` is the launch counter.

## Build

- Device VPK: `JITP_OUT=<dir> vita/jit/probe/build.sh` → `<dir>/shepherd_jitprobe.vpk` (default dir
  `$SHEPHERD_WORK/kit7/jit-work/probe`). Needs `~/vitasdk`, cmake, ninja. UNSAFE eboot (ForVM needs an unsafe eboot, as every
  Vita dynarec homebrew). Title id **SHPD00040** (claimed in `vita/notes/title-ids.md`).
- Mac harness: `JITP_OUT=<dir> vita/jit/probe/build-mac.sh` → `<dir>/jitprobe` (ad-hoc signed with the JIT entitlement); run it with
  `JITP_OUT=<dir>`. Test-only switch: `JITP_TEST_HANG=cross-core:<ms>` stalls that test's writer to exercise the timeout and the
  skip-on-relaunch path (unset = no effect; never set on the Vita).
- qemu encoding check: `JITP_OUT=<dir> vita/jit/probe/qemu/run.sh` (refuses unless the Mac is muted; exit 0 = 42 / 42).

## When the owner runs it (later, not now)

Copy `shepherd_jitprobe.vpk` to the Vita and install; launch **Shepherd JIT Probe**; it prints the 4th-core result and waits —
**release every button, then press X** to allow the exec-memory tests (**O** to exit without allocating anything). If it stops or the
console faults, relaunching is safe (the stopped test is skipped, the rest run). When done, send back **the whole
`ux0:data/shepherd/jitprobe.txt`** (it holds every launch) **and `jitprobe.faults`** if it exists. It restores the launch clocks and
changes nothing else on the console.
