# vita/devlog: the Vita apps send their logs to the Mac

A small C client that every Shepherd Vita app can include. It keeps a record of each run and sends the run's **log
files** to the owner's Mac over the same Wi-Fi, so a device session needs no FTP copying afterwards. Where they go
(revision 3, owner decision 2026-10-07): the **Archive Codex server** (port 8770, `vita_devlog_routes.py`, viewer
`/vita/logs` for the owner); the standalone receiver `tools/devlog-receiver/` (port 47710) is the fallback. Wire
protocol: [`PROTOCOL.md`](PROTOCOL.md) (shpdlog/1 revision 3; the Codex binding is §10). Design, device sessions and
the release plan: [`vita/notes/devlog.md`](../notes/devlog.md).

| file | what |
|---|---|
| `devlog.h` | the API (below) + the result struct |
| `devlog.c` | the client: config, modes, run markers, crash pickup, the background worker, discovery, authenticated HTTP upload (both bindings), the logs-only rule, clear, the log-file writer |
| `devlog_sha256.c` | SHA-256 + HMAC-SHA256 (FIPS 180-4 / RFC 2104, written here; NIST + RFC 4231 vectors in the self-test) |
| `devlog_plat.h`, `devlog_plat_vita.c`, `devlog_plat_posix.c` | files, clock, sockets, the worker thread: sceIo / sceNet / sceNetCtl / sceKernelCreateThread on the Vita, POSIX + pthreads on the Mac (non-blocking sockets, deadlines, a stop flag) |
| `devlog_unity.c` | the whole client as one include (`#include "../devlog/devlog_unity.c"`); everything but `devlog_*` is static |
| `devlog_pad.h` | `devlog_pad_answer`: the Vita-button answer for `devlog_ask_console` (X / CIRCLE / START) |
| `test/` | **Shepherd Log Send Test** VPK, TITLE_ID **SHPD00014** (full LiveArea set, 8-bit indexed PNGs from `make_assets.py`) |
| `mac/` | Mac build of the same sources: `selftest.c`, `devlog_cli.c`, `fakes.py`, `test.sh` (against the real receiver on 127.0.0.1, both bindings) |
| `integrate.patch` | ready patch for `vita/hello/host.c` + `vita/probe/main.c` (revision-3 wiring, against afdb105). **Not applied.** |

## On the device (owner)

1. On the Mac, once: `tools/devlog-receiver/devlogctl.sh token` (makes the secret and
   `$SHEPHERD_WORK/devlog/vita-devlog.txt`; without `--rotate` it keeps the token and only rewrites that file).
   The Archive Codex server takes the logs (its devlog routes are live since 2026-10-07; read them at `/vita/logs`
   through the front door, owner login). `devlogctl.sh start` runs the standalone receiver only for when the Codex
   server is down (both answer discovery on UDP 47711; only one can hold it).
2. Copy `vita-devlog.txt` to the Vita as **`ux0:data/shepherd/devlog.txt`** (VitaShell, SELECT = FTP). Without this file
   every app behaves exactly as before: no network, no extra files. In it:
   - `mode=ask` (also when the line is missing): after a run the app asks **X send / CIRCLE delete / START later**;
     nothing leaves the Vita without that yes. `mode=auto`: sends at the end of a run, and runs kept from earlier
     launches at the next start (in the background). `mode=off`: never.
   - `clear=1` (one-shot): the next start of any Shepherd app deletes the kept runs and its previous log, then turns
     the line into `clear=0`.
3. Install `vita/devlog/out/shepherd_logtest.vpk` (built by `vita/devlog/test/build.sh`) and start **Shepherd Log Send
   Test**. It writes a sample log, then: **X** (or 20 s) ends the run: in `ask` mode it asks first; then it sends in the
   background while the screen keeps counting, and ends with `PASS sent to 192.168.x.y:8770 in N ms` (Codex server) or
   `...:47710` (receiver), or `NOT SENT (...)` with the reason and the raw network error. The screen also shows how
   long `devlog_begin` / `devlog_end_async` took **in the app's thread** (microseconds).
4. More buttons: **R** big-log test (8 MiB more log, then as X), **TRIANGLE** crash test (quits without ending; the next
   start finds it as `crash`), **SQUARE** abort test (`devlog_note(ABORT)`), **CIRCLE** quit and keep the run,
   **SELECT** clear (`devlog_clear`), **L** card timing (2,000 log lines line by line vs buffered). The checklist for a
   session is in `vita/notes/devlog.md`.

Silent (no audio). It reads and writes only `ux0:data/shepherd/logtest*.txt`, `devlog.txt` (read; one byte written
for `clear=1`) and `ux0:data/shepherd/devlog/` (the client's own state).

## In an app

```c
#include "../devlog/devlog_unity.c"          /* the whole client; no build-file change */
#include "../devlog/devlog_pad.h"            /* only for the ask prompt (needs SceCtrl_stub) */

static const devlog_app DL = { .app = "probe", .title_id = "SHPD00001", .build = BUILD_ID,
                               .log_path = "ux0:data/shepherd/probe.txt",
                               .prev_path = "ux0:data/shepherd/probe.prev.txt",
                               .extra = NULL,          /* or e.g. { ".../runtime/logs/engine.log", NULL } */
                               .log = my_log_line };   /* devlog reports through the app's own log */
/* main(): rotate the log (probe.txt -> probe.prev.txt), write the first lines, then */
devlog_begin(&DL, NULL);                  /* < 1 ms here; kept runs: copied / sent on the worker */
/* ... the run (no devlog network during play) ... */
int rc = devlog_end(ok ? DEVLOG_OK : DEVLOG_FAIL, "short reason", &res);   /* auto: SENT / PENDING / REJECTED */
if (rc == DEVLOG_ASK) rc = devlog_ask_console(devlog_pad_answer, 30000, &res);    /* ask: the prompt */
/* fatal paths (no network there, any thread): */
devlog_note(DEVLOG_ABORT, "runtime fatal error");
```

`devlog_begin` may come after any amount of logging (the tag is found by the offset recorded in the marker), but it
should come before anything that can crash. Call the API from the app's main thread (`devlog_note`, `devlog_busy`,
`devlog_cancel`: any thread). An app that keeps core 2 busy sets `.thread_affinity` to the mask of its spare core;
hello (big allocations right after start) sets `.defer_launch_send = 1` and calls `devlog_send_now(1, NULL)` once its
Mono arena and thread are in place, so the sceNet pool (up to 1 MiB) is not taken in the middle of its start -- **only
when `devlog_begin` returned `DEVLOG_PENDING`** (mode=auto): in mode=ask (`DEVLOG_ASK`) `devlog_send_now(1)` is the
user's yes, so an app must never call it on its own there. An app that measures (the probe) sets
`.defer_launch_send = 1` and lets `devlog_end` send the kept runs after its own.

`integrate.patch` does the revision-3 wiring for the V0 probe and the V5 hello (regenerated 2026-10-07 against main
afdb105; applies, both patched files compile warning-free): `devlog_begin` early with `.defer_launch_send = 1` (no
network during the probe's measurements or hello's start; the probe waits up to 5 s for the launch copies so the
worker is gone before `test_memory`), hello's `devlog_send_now(1)` after its arena + Mono thread only on
`DEVLOG_PENDING`, `devlog_ask_console` after `devlog_end` in mode=ask (the probe only while its debug screen is visible,
not under vitaGL), `devlog_note` in hello's fatal-error, unhandled-exception and `exit()` paths, `devlog_end(FAIL)`
before its H2/H3 early exits, and hello.txt lines for `DEVLOG_PENDING` / `DEVLOG_REJECTED` / no answer. The sceNet
stubs need no `-l`: arm-vita-eabi-gcc's default `*lib` spec already links `SceNet_stub SceNetCtl_stub
SceSysmodule_stub`.

| call | does | app's thread | network (on the worker) |
|---|---|---|---|
| `devlog_begin(app, res)` | reads `devlog.txt` (+ `clear=1`); makes the run id; writes the run marker (with the log's size); puts `DEVLOG run=<id> app=<app> …` in the log; starts the job for older runs: copy the logs of runs that never ended (crash) or were not sent, and in `auto` send them | 0.45-0.65 ms (Mac) | `auto` + something kept: ≤ 2 s without a receiver, ≤ `begin_ms` (2.5 s) with one; `ask`: none |
| `devlog_end(outcome, detail, res)` | `auto`: sends this run (meta, files, done) then other kept runs, and waits; if that fails, copies its files' tails for a later launch. `ask`: keeps the run, returns `DEVLOG_ASK` | waits for the job | ≤ 2 s without a receiver; ≤ `max_total_ms` (20 s) with one |
| `devlog_end_async(…)` | the same, returns at once (`DEVLOG_STARTED`) | ~0.1 ms; ~3 ms when it first stops a launch job (Mac) | as above |
| `devlog_wait(timeout_ms, res)` | the running job's result (`0` = poll, `~0u` = until done); hands the worker's log lines to the app's log | ≤ timeout | |
| `devlog_busy()`, `devlog_cancel()` | is a job running; stop it now (within a few ms, also mid-copy or mid-hash: every loop checks the stop per 8-32 KiB piece; its runs stay kept) | µs | |
| `devlog_should_ask()` | `ask`: how many runs wait for an answer (0 while the launch copies are still being made) | µs-ms | never |
| `devlog_send_now(yes, res)` | the answer: yes = send them all on the worker (`DEVLOG_STARTED`); no = delete them (`DEVLOG_DROPPED`; their logs stay on the card and never leave it) | < 1 ms / the deletes | yes: ≤ 20 s |
| `devlog_ask_console(answer, timeout_ms, res)` | the whole prompt for debugScreen apps (two lines through the app's log = its screen), then send + wait | the prompt | yes: ≤ 20 s |
| `devlog_clear(app, res)` | deletes every kept run, the cached address, the app's previous log; empties the current one (an active run's tag is written again) | ms | never |
| `devlog_note(outcome, detail)` | records an outcome for a run about to die (`<run>.note`) and flushes a buffered log | one small write | never |
| `devlog_flush(res)` / `devlog_flush_app(app, res)` | sends kept runs now and waits (after begin / standalone); `ask`: only copies | waits | ≤ 2 s / ≤ 20 s |
| `devlog_logfile_open / _write / _flush / _close` | optional log-file writer for the app's log function: write-through (dev default) or buffered (release) | µs per line | never |
| `devlog_run_id()`, `devlog_outcome_name()` | | | |

Return values: `DEVLOG_DISABLED` 0 (no config / `mode=off`: nothing done), `DEVLOG_SENT` 1 (everything sent, or nothing
to send), `DEVLOG_PENDING` 2 (something kept for later), `DEVLOG_REJECTED` 3 (`devlog_end`: the receiver refused this
run for good; dropped), `DEVLOG_STARTED` 4 (a background send runs: `devlog_wait`), `DEVLOG_ASK` 5 (runs wait for the
user's answer), `DEVLOG_DROPPED` 6 (deleted on request). `devlog_result.why` is one readable line for the screen /
log, with the raw network error when there was one; `.ms` is the whole call or job, copies included; `.mode` is the
config's mode.

## Threads: zero speed impact

- **One worker, one job at a time.** Every reach, transfer and keep copy runs on a background thread: Vita
  `sceKernelCreateThread` at priority **191** (the lowest user priority), CPU mask **`SCE_KERNEL_CPU_MASK_USER_2`**
  (core 2: the core the Shepherd apps leave spare; an app that uses it passes another mask in `.thread_affinity`),
  32 KiB stack; Mac: a pthread at QoS "utility". If the kernel refuses that priority / core, priority 191 on any
  user core is tried next (`worker on priority 191, any core (asked one refused 0x…)` in the next `DEVLOG …` line),
  and only then, as the last resort, the default priority (`worker on the DEFAULT priority 0x10000100, any core …`);
  if no thread can be made at all, the job runs on the app's thread (and says so).
- **No network during play.** Jobs start only in `devlog_begin` (runs kept from *earlier* launches; `ask`: copies
  only), `devlog_end*` / `devlog_send_now` (run end) and the flush calls. `devlog_cancel` stops one at once.
- **The app's thread never waits** unless it asks to (`devlog_end`, `devlog_wait`, the flush calls, the prompt).
  Measured on the Mac (`mac/test.sh` §11, -O2): `devlog_begin` with a kept run and no receiver **0.45-0.65 ms**
  (revision 2: 1.5 s there), `devlog_end_async` **~0.1 ms**, `devlog_end_async` cancelling a launch send stuck on a
  dead receiver **3 ms**, and cancelling a launch job in the middle of its keep copies (a crashed run's 8 MB log +
  8 MB extra) **3 ms** (before the copy loop checked the stop: 24-27 ms). The stop wait is the job's 2 ms poll. A copy
  cut short is deleted and the run left as it was: the run-end job (mode=auto, or the user's yes) copies and sends it,
  else the next launch.
- **Log lines from the worker** never call the app's log function on the worker: they wait in a 32-line queue and are
  written by the app's thread at its next `devlog_*` call (`devlog_wait(0, NULL)` = poll).
- **ThreadSanitizer**: `mac/test.sh` §12 runs five scenarios (a launch send while the app logs 400 lines, a cancel, the
  ask flow) under `-fsanitize=thread`: no report.

## Buffered logs (release builds)

`devlog_logfile_open(path, append, buffered)` + `devlog_logfile_write(line)` can back the app's log function.
**Write-through** (`buffered=0`, the default for dev apps): every line is on the card when the call returns, so a crash
log is complete. **Buffered** (`buffered=1`, for release builds): lines collect in a 16 KiB buffer and go to the card
every 64 lines, when the oldest waiting line is 2 s old (checked as lines arrive), on `devlog_logfile_flush` / `_close`,
and from `devlog_begin` (before the log size is recorded and right after the tag, so crash pickup still works),
`devlog_note` (the crash marker) and `devlog_end`. A crash loses at most the lines still in the buffer (`mac/test.sh`
§11 checks both). Mac, 20,000 lines of 80 bytes: write-through ~37 ms, buffered ~2.5 ms; the test VPK's **L** button
measures the memory card.

## What is sent, and what never is

- **Per run**: the app's log; its `*.prev` log **only if it carries no `DEVLOG run=` tag at all** (it was written
  while devlog was off); the extra files the app lists. A tagged `*.prev` is the log of a devlog run that was dealt
  with on its own (sent, declined, dropped, cleared, or still kept and sent under that run if allowed), so it is never
  sent as somebody's `*.prev`: a run the user declined cannot leave later, also not after a `devlog_clear` from
  another app (revision 3 used a list of the last 32 sent ids for this, which a clear emptied). The meta (PROTOCOL.md §4): app, TITLE_ID, build, commit, firmware version, model
  family (`PCH-1000` / `PCH-2000` / `VTE-1000`), free memory (main / CDRAM / phycont), outcome + one-line detail, start
  time (device clock), run time, attempt, `late`. App strings are cleaned to printable ASCII and anything shaped like a
  MAC address is masked (`xx:xx:…`); if the receiver still refuses the meta, a reduced meta (client-made keys only) goes.
- **Never**: the token (requests carry HMACs; nothing goes to an address whose ping answer lacks a valid proof), a
  console or account identifier (no PSID/OpenPSID/IDPS/CID, account id, user name, MAC address, SSID, IP address;
  none of these is read, except the Vita's own IPv4 address + netmask, read only to address the subnet broadcast and
  never sent; the VPK imports no identifier API), anything stable across runs, save data, game files, the owner
  folders. In `mode=ask`: nothing at all before the user's yes.
- **Logs only, by path and name** (`dl_path_allowed`, whatever an app lists): a top-level `*.txt` / `*.log` of
  `ux0:data/shepherd/`, files under `runtime/logs/` (≤ 3 folders deep; .txt .log .tsv .csv .json), the client's own
  copies; never a name that looks like a config file (`devlog*`, `env*`, `args*`, `pad*`, `*-env`, `*_gl` env/args
  variants, `*-stress`, `settings`, `token`, … PROTOCOL.md §4). `persistent/` (saves), `managed/`, `pack/`, `data/`,
  `..`, other devices: refused, with a `DEVLOG refused …` line in the app's log.
- **Text**: NUL and control bytes other than TAB/LF/CR/ESC go as `?` (same length; count in `X-Shpd-Sanitized`).
- **Size**: a file over 8 MiB (or the receiver's `max_file`) is sent as its last 8 MiB; a run is capped at 32 MiB,
  16 files; a kept run keeps the last 1 MiB (`keep_kb`) of each file.

## Where it sends (revision 3)

`server=` in devlog.txt or the cached address first, then UDP discovery: the Archive Codex server answers with
`base=/vita/devlog` and is preferred; a standalone receiver's answer waits at most 150 ms for it. When the known
address is a standalone receiver (a `server=…:47710`, or the cache after a run went to the fallback), a short
discovery (≤ 200 ms) looks for the Codex server first and puts it in front, so a run goes back to the Codex server as
soon as it answers again (`mac/test.sh` §11: 48 ms). If the Codex server
does not answer, answers without a proof, or answers with its login wall (a 401 that is not shpdlog's), the next
address is tried within the same 2 s: the standalone receiver gets the run. `server=a.b.c.d:8770` means the Codex
server (base `/vita/devlog`), `server=a.b.c.d:47710` the receiver, `server=a.b.c.d:<port>/<base>` any other base.
The cache (`devlog/server.txt`) keeps `ip:port[/base]`. Codex targets: `GET /vita/devlog/v1/ping`,
`POST /vita/devlog/v1/upload?op=meta|file|done&app=…&run=…[&name=…]` (PROTOCOL.md §10).

## Timing, crashes, state on the card

- **Off** (no `devlog.txt`, `enabled` ≠ 1, no valid 64-hex token, or `mode=off`): every call returns at once and
  creates nothing (`clear=1` still clears).
- **A missing receiver never holds a job more than 2 s**, copies included (and the app not at all, see Threads).
  Measured on the Mac (`mac/test.sh`): receiver down 1.6-1.7 s, closed `server=` port 1.6 s, a receiver that accepts
  and never answers 1.7 s, a rogue discovery answer 0.7 s, an 8 MiB log with the receiver down 1.7 s (1 MiB kept), the
  next launch adopting a crashed 8 MiB log 1.7 s.
- **A receiver that stops taking data** (Wi-Fi gone, Mac asleep mid-upload) is given up after 1.5 s without progress:
  `devlog_end` 2.1 s, the next launch send 2.0 s.
- **On the Vita every socket is non-blocking** and every wait is a 2 ms sleep loop against
  `sceKernelGetProcessTimeWide` that also watches the stop flag. sceNet is brought up only for a send and taken down
  after it (each of sysmodule / `sceNetInit` / `sceNetCtlInit` only if the app had not done it itself). Its pool is a
  USER_RW memblock sized from free memory: 1 MiB from 16 MiB free, 512 KiB from 4 MiB, else 256 KiB (a smaller size
  is tried if the allocation fails); the size is in the app's log line (`sceNet pool 1024 KiB`).
- **Crash pickup**: `devlog_begin` writes `ux0:data/shepherd/devlog/pending/<run>.run` (state `running`, with the
  log's size); `devlog_note` adds `<run>.note`. A run that dies keeps them; the next launch of **any** Shepherd app finds
  it, looks for the run's `DEVLOG run=<id>` line at that offset in the current or `*.prev` log, copies its tail into
  `pending/<run>/`, and sends it (`auto`) or asks about it (`ask`) with `outcome=crash` (or what `devlog_note` recorded)
  and `late=1`. If the log is gone or was overwritten, the run goes without it and its detail says which.
- **Kept runs and retention**: **the current run + the previous one** (`keep_pending=2`, 1..32 in devlog.txt); retried
  at every later launch (`auto`) or asked about (`ask`). Dropped after 8 attempts that failed with the receiver
  reachable (running out of time or a cancel does not count), 14 days, or beyond `keep_pending`; each drop is logged.
  `devlog_clear` / `clear=1` delete them all.
- **Memory**: no malloc. 111,864 B of static buffers (.bss: the 16 KiB log-file buffer, the worker's 9 KiB log queue
  and the revision-2 85,600 B), ~44 KiB of code (arm, -O2); the worker's stack is 32 KiB (deepest chain ~6 KiB,
  `-fstack-usage`: job → reach / send_run → http → mac); the sceNet pool only while sending.

## Building and testing

    vita/devlog/test/build.sh      # SHPD00014 VPK -> vita/devlog/out/shepherd_logtest.vpk (+ logtest-imports.txt)
    vita/devlog/mac/test.sh        # Mac: build (ASan+UBSan, -O2, TSan) + arm-vita-eabi compile + 86 checks
    tools/devlog-receiver/e2e_test.sh   # the receiver's 42 tests + devlogctl + this client again (32 checks)

`build.sh` stamps the build id with the HEAD commit, plus `-dirty` when `vita/devlog/` has uncommitted changes. It
deletes the last build's products first (the CMake VPK step can otherwise reuse a stale `.vpk.out`) and then checks
the VPK itself: newer than the build start, its `eboot.bin` = the one just built, `sce_sys/livearea/contents/
{template.xml,bg.png,startup.png}` + `icon0.png` + `param.sfo` inside, every PNG bit depth 8 / colour type 3
(VitaShell: 0x80AF5001 without the LiveArea set, 0x8010113D for other PNGs). Build from `~/src`, not the
`$SHEPHERD_DATA` path.

`mac/test.sh` binds everything to 127.0.0.1 on random ports, writes under
`$SHEPHERD_WORK/devlog/client-test/<stamp>/`, and stops every receiver / fake it started; every devlog.txt it
writes names `discover=127.0.0.1` (port 9 where no answer is wanted), so no test datagram is broadcast on the LAN. Sections 2-9 (the
revision-2 behaviour, `mode=auto`): off, discovery, `server=`, the logs-only rule, `fail` / `crash` / `abort`, a
MAC-shaped detail, a crash picked up by another app, a tag 40 KB into the log, the text rule, the tail upload, the 2 s
budget, a stalled receiver, an impostor at the cached address, an impostor answering discovery, the flush once the
receiver is back, a wrong token and its recovery, the reduced meta and a clean rejection. Section 11 (revision 3):
`mode=off`, the default `ask` (no network before the answer; later / yes / no; the declined log never sent as a
`*.prev`; asking at launch about a crash), the app-visible costs (< 1 ms), worker log lines, retention, a cancel,
`devlog_clear` before and during a run, `clear=1` (one byte changed), buffered logs + crash / note, a devlog.txt with a
UTF-8 BOM, a run declined in one app and the logs cleared from another (the declined log never leaves), a launch copy
cut short by `devlog_end_async` (3 ms, then copied and sent by the run-end job), the Codex binding (discovery with
`base=`, the cached base, the Codex server preferred over the receiver, the fallback when it is gone, back to the Codex
server when it returns, a login wall). Section 12: TSan.

## Not verified yet (needs the device)

The Vita platform layer has been compiled, linked and accepted by vita-elf-create, and its logic is the same as the
POSIX layer's; the network path and the worker thread have not run on hardware. The next SHPD00014 session (checklist
in `vita/notes/devlog.md`) tests: the worker at priority 191 on core 2 (or the logged fallback), sysmodule +
sceNetInit with the sized pool from the worker, the non-blocking connect loop, the UDP broadcasts, the Codex server's
discovery answer and routes (live since 2026-10-07), `sceNetCtlInetGetState` with Wi-Fi off, the keep-copy time and the
line-by-line vs buffered log cost on the memory card (L), and the macOS Local Network permission for discovery.
