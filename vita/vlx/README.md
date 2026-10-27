# vlx: the Vita apps' own objects, run on this Mac

vlx ("Vita-on-Linux eXecution") reproduces device-only runtime failures off the device. It takes **the same objects
that went into an eboot** and relinks them into a static ARM32 Linux ELF. The objects are newlib, pthread-embedded, the
HOST_VITA Mono runtime, the static AOT images, host.c and the System.Native shim. Only the SceKernel NID stubs are
replaced, by `vlx.c`, which implements the ~80 imported functions on top of Linux syscalls. The ELF then runs under
OrbStack's ARM32 user-mode emulation (qemu-user behind binfmt, in the local `alpine:3.20` image). Runs are silent and
offline, take 11 s for the hello, and print a register dump on any fault.

First use (2026-10-07): the device hello (SHPD00005) reproduced its fatal **line for line** (M1-M5, then the
exception.c:137 assert). The menu (SHPD00006) reproduced its hang (log stops at `MEM before-main`, `runtime/logs/`
empty, 0 % CPU). A thread dump showed the cause, and both fixes were validated in vlx before any device run. See
`$SHEPHERD_WORK/devfail1/repro.md`.

```
vita/vlx/build.sh                                   # shim -> $VLX_OBJ (default $SHEPHERD_WORK/obj/vlx; devfail1 refused)
vita/vlx/link.sh hello [host.o] [out.elf]           # objects of vita/hello/build.sh  (SHEPHERD_HELLO_WORK)
vita/vlx/link.sh menu  [host.o] [out.elf]           # objects of vita/shepherd/build.sh, headless (SHEPHERD_V6_WORK)
vita/vlx/stage.sh hello|menu <root>                 # app0/ = the VPK's files, ux0/data/ = the card (menu: owner data clone)
vita/vlx/run.sh <elf> <root>                        # docker run --network none ... alpine:3.20 (never pulled)
```

To test a host change, compile the patched host.c with the app's own flags, then pass that `host.o` to `link.sh`.
Compiling the unmodified host.c that way gives a byte-identical `host.o`, for both hello and menu.

The devfail1 diag apps (SHPD00015/16) link extra pieces: `VLX_AOT_EXTRA` (more AOT modules, e.g. `HelloDiag`),
`VLX_EXTRA_OBJS` (objects after host.o: the hello's renamed stock `sem_post`, the menu's `diag_wraps_{S,c}.o`) and
`VLX_LDFLAGS` (`-Wl,--wrap=sceKernelCreateThread`, or the menu's `@<work>/obj/diag/wraps.rsp`), with
`SHEPHERD_VITA_RUNTIME_OBJ` pointing at the diagnostic runtime. `link.sh` links the whole libpthread.a with
`--allow-multiple-definition`: the shim archive's corrected `sem_post` (vita/runtime/system-native/vita_pthread_fix.c)
comes first and wins, as in the eboot. OrbStack can show a stale copy of a root directory that was deleted and recreated
at the same path: give every run its own root.

2026-10-08: `sceKernelDeleteThread` waited for the dead thread's CLONE_CHILD_CLEARTID with a PRIVATE futex wait, which the
kernel's SHARED wake never reaches: a rare vlx-only deadlock in `pthread_join` (hello diag G5, about 1 run in 12). It now
waits shared and re-checks every millisecond; the device never had this code.

2026-10-13: every host now sets the clocks at start (444/222/222/166) and logs them on CLK lines. `vlx.c` answers the eight
`scePower{Get,Set}{Arm,Bus,Gpu,GpuXbar}ClockFrequency` imports with stand-ins that remember what was set (launch values 333/222/0/111,
the PCH-2000's); nothing on the host changes speed.

2026-10-13 (device kit #2 review): the user-memory budget follows the eboot. `run.sh` used one figure for every `menu*` ELF
(`VLX_USER_KIB=327680`, the 2026-10-09 device's 192 MiB free + the 128 MiB newlib heap), which overstated the bigger GL and
engine-AOT eboots' arenas. `link.sh` now writes `<elf>.eboot-span` (the build's own velf: lowest LOAD address to the end of the
highest segment) and `run.sh` sets `VLX_USER_KIB = floor(359.0 MiB - span)` in whole MiB, calibrated on that device run (menu-diag
span 38.945 MiB -> 192 MiB free with 128 MiB newlib; hello-diag 32.677 -> 278 with 48: T in [358.95, 359.68)). The vlx ELF's own size
is no guide (it carries the shim, and for the GL apps a stand-in GL library). Kit #2 figures: headless menu / diag 320 MiB (192 free),
GL and GL + audio 316 (188), engine AOT 310 (182). An explicit `VLX_USER_KIB` still wins.

2026-10-13 (k4-gameaot review): T = **359.104** MiB. Device kit #3 pinned it down: 00011 (span 49.1034 MiB) had 194 MiB free with the
116 MiB newlib heap, so T >= 359.1034; the play apps (span 54.04) had 189. With 359.0, vlx gave every eboot whose span's fraction is
at most .1034 one MiB less than the device (kit #3's play apps: 188 instead of 189, a GC heap cap of 124 instead of the device's 125).

**Debugging.** `VLX_GDB=12345 VLX_BREAK=1 run.sh ...` starts qemu's gdbstub on 127.0.0.1:12345 and stops at a
`bkpt` in the shim's boot. Then run `lldb`, `target create --arch armv7-unknown-linux-gnueabihf <elf>`,
`gdb-remote 127.0.0.1:12345` and ``register write pc `$pc+4` ``, and continue. Breakpoints, `frame variable` and
DWARF source lines work for all C code (the runtime archives carry debug info). AOT frames show as symbols only.
lldb's own interrupt times out when every thread is blocked in a futex. `VLX_TRAP_AFTER_S=N` works around that: a
watchdog thread hits `bkpt` every N s, and `thread backtrace all` then shows every thread. `QEMU_STRACE=1` traces
syscalls. `VLX_TRACE=1` logs every kernel call that returns an error.

**Fidelity.** These match the device:
- the instruction stream: the same objects;
- A32/Thumb-2, VFPv3 and NEON;
- unaligned LDRD faults with SIGBUS, as on the A9;
- the memory budgets that `sceKernelGetFreeMemorySize` reports;
- coop suspend, threads, semaphores, mutexes and lightweight mutexes, with Vita semantics and non-reused UIDs.

These do not match:
- the CPU is qemu's ARMv8 AArch32 model, not a Cortex-A9;
- FPSCR starts at 0 (IEEE), not the Vita default;
- timing and scheduling differ, the host has more cores, and there are no caches or bus limits;
- sceIo is a host directory, and newlib's stdout goes to stderr;
- there is no GPU, audio or network (`sceNet*` fails) and no firmware modules;
- `link.sh` uses the current `obj/vita-runtime` archives, which may be newer than the ones in an older eboot.

A pass in vlx is therefore evidence, not proof. Performance numbers from vlx mean nothing for the device.

**Output identity.** A menu run can write the engine's per-frame draw-stream hash: put
`SHEPHERD_STREAM_HASH=ux0:data/shepherd/runtime/logs/stream-hash.txt` in the root's `ux0/data/shepherd/env.txt`. Two builds whose files
are identical handed the (null) backend bit-identical input in every frame. That is how the engine-AOT and LLVM builds were compared
with the interpreted build (`vita/notes/llvm-aot.md` §14).

**GL apps (kit 3).** The GL menu hosts (SHPD00008 / 00009 / 00091 / 00096) run under vlx with `glrec_vlx.c` linked in place of
shepgl + vitaGL (`VLX_EXTRA_OBJS`; the engine-AOT builds also need `VLX_AOT_EXTRA="Shepherd.Engine"` or `"Shepherd.Engine
Shepherd.Engine.Derived"`, the eboot's module order). It answers GL as kit #2's no-op stand-in did, but hashes every GL call, its
arguments and every byte it hands over (pixels, DXT blocks, vertices, indices, uniforms, shader text) and writes one line per displayed
frame to `ux0:data/shepherd/runtime/logs/glrec.txt`. Two builds whose lines agree (left of `|`) gave the GPU the same input in every
frame. Put a text placeholder at `ur0:data/libshacccg.suprx` (the host only checks it exists) and `boot N 0` in `args_gl.txt`. Nothing of
vitaGL's own memory is modelled. Kit 3 compared SHPD00008 with 00091 and 00096 this way (`$SHEPHERD_WORK/kit3/gl-aot.md`). Since the kit 3 merge the GL hosts
default to the play clock (`vita/notes/play-mode.md`: real elapsed time, `pad.txt` not applied), so a vlx comparison of GL apps puts
`SHEPHERD_TIME=frame` in `env_gl.txt` (the deterministic frame clock; with it the host applies `pad.txt` again).

**GL + audio apps (device kit #3).** The GL + audio hosts (SHPD00010, the play app SHPD00097 and its MT variant 00098) also link
`audio_vlx.c` (silent sceAudioOut / sceAudiodec stand-ins: the port takes one grain at a time at 48 kHz on a deadline, so shepaudio's
output thread keeps real time; the decoder returns zeros), the build's `<work>/obj/audio_pool.o` and the source tree's
`vita/audio/build/vita/libshepaudio_vita.a`, and need the owner's audio bake at `ux0:data/shepherd/audiobake` (the host's A0 line). Their
env / args files are the app's own (`env_gl_audio.txt` / `env_play.txt`, `args_...`). Kit #3's driver:
`$SHEPHERD_WORK/kit3/kit-build/scripts/vlx-run.sh glaudio <build> <tag>`. `VLX_AUDIO_FAIL=<first grain>:<count>` in the app's env
file makes that many `sceAudioOutOutput` calls fail at once (0x80260003), the kit #3 review's test of shepaudio's refused-grain path
(`vita/audio/src/sa_out_vita.c`: the audio clock must stay at 1.00x real time, the heartbeats show `OUTPUT ERRORS`). The stand-in hands
`sceCtrlPeekBufferPositive` equal samples (one timestamp), so the host's short-press latch (`vita/shepherd/pad_latch.h`) is inert under
vlx; its test is `vita/shepherd/mac-check/test_pad_latch.c`.

**Game-AOT apps (kit 4, `vita/notes/game-aot.md`).** `build.sh SHEPHERD_AOT_OWNER=1` (SHPD00095 / 00190) puts the owner game modules'
objects in `<work>/aot-owner` and lists them in `<work>/obj/aot-owner-modules.txt`; `link.sh` links them after the other AOT objects in
that order (`VLX_AOT_OWNER=0` leaves them out). Every build now writes the runtime it linked to `<work>/obj/vita-runtime-obj.txt`, and
`link.sh` relinks against that runtime (`SHEPHERD_VITA_RUNTIME_OBJ` still wins): a game-AOT eboot links the gameaot runtime with fix A,
and the first vlx runs, relinked against the shared runtime, died at frame ~1050 in the stock unbox-trampoline lookup (an assertion in
`get_call_table_entry` on Rewired_Core's empty unbox table, found with `VLX_GDB` + lldb) -- a failure the eboot itself cannot have.

**Front touch panel (kit 5, `vita/notes/kit5-touch.md`).** The GL hosts read the front panel once per frame (host.c
`shepherd_vita_touch_read`, `vita/shepherd/touch_latch.h`). `vlx.c` answers `sceTouchSetSamplingState`, `sceTouchGetPanelInfo` (a
1920 x 1088 report space, as the PCH-1000 / 2000 front panel) and `sceTouchPeek`: without a script every peek is one sample with no
finger (the latch stays inert, the stream is the no-touch one). `VLX_TOUCH="R:tap:x,y;R:hold:x,y:n"` (run.sh passes it through) scripts
the host's reads, R = the R-th peek of the front port = the engine frame (the engine reads once per frame from frame 1), x,y in report
space (screen pixels x 2): `tap` = a finger touched and left the glass between reads R-1 and R (the peek returns 8 samples spread over
the time since the previous one, the 3rd..5th with the finger: the host's latch must report the tap, a `TOUCH tap #n latched` line);
`hold` = the finger in the one sample of reads R..R+n-1 (a contact). The engine's own script (`SHEPHERD_TOUCH_FILE` in the env file,
`m+N` frames, screen pixels) drives the same pointer without the host's latch.

No executable memory is created (every mmap is PROT_READ|PROT_WRITE), and there is no code generation of any kind.
