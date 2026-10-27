# Shepherd devlog wire protocol (shpdlog/1, revision 3)

Status 2026-10-07: **revision 3**, implemented by the client (`vita/devlog/`) and the standalone receiver
(`tools/devlog-receiver/`, which also speaks the new binding with `--base`), covered by their tests
(`vita/devlog/mac/test.sh` 80 checks, `test_receiver.py` 42 tests, `e2e_test.sh` 32 checks, all on 127.0.0.1).
Revision 3 adds the **Archive Codex server binding** (§10: the owner's decision that the logs go to the Codex server,
the standalone receiver stays as the fallback), an optional `base=` in the discovery answer (§3), and the client's
`mode=` / `clear=` / retention rules (§7). It changes nothing a revision-2 receiver sees, except `client=devlog-c/3`.
Revision 2 (same day, §9) replaced revision 1 after a security / protocol / build review: HMAC per request instead of
a bearer token, a proof in the ping answer, no stable key id in discovery, tighter timing / meta / copy rules.
This file is the single statement of the protocol; the receiver's first proposal
(`$SHEPHERD_WORK/devlog/PROTOCOL-receiver.md`) is history.

Scope: LAN only, a developer receiver on the owner's Mac. Logs only: never save data, game files or anything from the
owner folder (`ux0:data/shepherd/{managed,pack,data,persistent}`). The client never sends a console or account
identifier (no PSID/OpenPSID/IDPS/CID, account id, user name, MAC address, SSID/BSSID, IP address), and nothing it
sends is stable across runs (no key id, no install id).

## 1. Token and MACs
- **token**: 64 lowercase hex chars (32 random bytes). Mac: `$SHEPHERD_WORK/devlog/token` (0600).
  Vita: the `token=<64 hex>` line of `ux0:data/shepherd/devlog.txt` (§7). **It is never sent anywhere.**
- **MAC(msg)**: HMAC-SHA256(key = token as its 64 ASCII bytes, msg as ASCII), first 32 lowercase hex chars.
- **kid**: first 16 hex chars of SHA-256(token). For people only (`receiver.py kid`, the receiver's start line, to
  check which token a machine has); not on the wire.
- The client implements SHA-256 and HMAC itself (`devlog_sha256.c`, FIPS 180-4 / RFC 2104, tested against NIST and
  RFC 4231 vectors and against Python's `hashlib`/`hmac` for every MAC below); nothing comes from a crypto library.

## 2. Ports (defaults)
- **TCP 8770**: the Archive Codex server (revision 3, §10): the preferred target. It is also reachable through its
  cloudflared tunnel and `tailscale serve`; its devlog routes refuse everything that did not come straight from the LAN.
- **TCP 47710**: the standalone receiver's HTTP/1.1 upload API (the fallback), bound to the Mac's private LAN IPv4 only
  (127.0.0.1 in tests). Never behind the Archive Codex tunnel, never on 0.0.0.0, never a Tailscale (100.64/10) or other
  non-RFC-1918 address.
- **UDP 47711**: discovery responder (either server, or both).
(`lsof` 2026-10-07: both free; outside 8765/8770/8080/8090/47984-48010.)

## 3. Discovery (UDP; one ASCII line, <= 200 bytes, optional trailing "\n")
Client -> `255.255.255.255:47711` **and** the subnet's directed broadcast (from the Vita's IP/netmask), from an
ephemeral source port:

    SHPDLOG1 DISCOVER nonce=<16hex> mac=<MAC("discover|" + nonce)>

Server -> unicast back to the datagram's source ip:port:

    SHPDLOG1 HERE host=<dotted ipv4> port=<tcp port> nonce=<same 16hex> proof=<MAC("here|" + nonce + "|" + host + "|" + port)>

or, from the Archive Codex server (revision 3, §10), with the base path its routes live under, covered by the proof:

    SHPDLOG1 HERE host=<ipv4> port=8770 nonce=<16hex> base=/vita/devlog proof=<MAC("here|" + nonce + "|" + host + "|" + port + "|" + base)>

- The server silently drops anything else (wrong magic or MAC, replayed nonce, source outside the LAN subnet,
  > 5 datagrams/s per source). Revision 1 also sent `kid=`; it is gone, so two DISCOVERs share nothing but the magic.
- The client verifies `nonce` and `proof` before using `host:port`, and uses `host` from the reply. A `base=` that is
  not `^(/[a-z0-9_-]{1,32}){1,4}$` (< 40 chars) makes the answer invalid.
- Client timing: send, wait 300 ms, resend (new nonce), wait 300 ms, give up: **<= 700 ms**. Revision 3: the client
  keeps up to 2 verified answers, the Codex server's (with `base=`) first; once a standalone receiver answered it
  listens at most 150 ms more for a Codex answer, a Codex answer ends the listening at once.
- `discover=<ipv4>` in devlog.txt replaces the broadcast targets with that unicast address (the Mac tests use
  127.0.0.1). The responder binds `0.0.0.0:47711` pinned to the LAN interface (`IP_BOUND_IF`; macOS delivers
  broadcasts only to wildcard sockets) and filters by source subnet.

## 4. HTTP API (HTTP/1.1; every request authenticated, no bearer token)
Every request carries

    Authorization: SHPD1 nonce=<16hex> body=<SHA-256 of the body as sent, 64hex> mac=<MAC("req|" + METHOD + "|" + target + "|" + nonce + "|" + body)>

`target` is the request target exactly as sent (`/v1/ping`, `/v1/runs/<app>/<run>/meta`, ...); a GET's body hash is
the hash of nothing (`e3b0c442...b855`). The receiver accepts a nonce once (15-minute replay cache) and checks the
body against `body=` (mismatch -> **409**, nothing stored). Missing/invalid -> 401, empty body; 10 failures in a minute
from one address -> 429 for 10 minutes.

The client sends, on every request: `Host`, `Authorization`, `User-Agent: shepherd-devlog/3 (<app>)`, `Content-Length`
(always; never chunked), `Content-Type: text/plain; charset=utf-8` (files) / `text/plain` (meta, done),
`Connection: close`. One request per TCP connection.

Path segments (strict regexes on the raw target, anything else -> 400/404):
- **app** `^[a-z][a-z0-9_-]{0,31}$` (`probe`, `hello`, `gltest`, `shepherd`, `menu`, `physbench`, `logtest`, ...)
- **run** `^[A-Za-z0-9][A-Za-z0-9_-]{5,63}$`; the client makes `<yyyymmddThhmmssZ>-<12 random hex>` (UTC device clock).
- **name** `^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$`, no `..`, extension `.txt .log .csv .tsv .json`. The client uploads base
  names (`runtime/logs/engine.log` -> `engine.log`), a `-<n>` suffix on clashes.

What the client reads (the "logs only" rule, `dl_path_allowed`, whatever an app lists): a top-level `*.txt` / `*.log`
of `ux0:data/shepherd/`, files under `runtime/logs/` (<= 3 folders deep), its own copies in `devlog/pending/<run>/`.
Refused by name anywhere: config and input files -- the name (without extension and `.prev`) starts with `env`,
`args`, `argv`, `pad`, `devlog`, `token`, `config`, `settings`, `secret`, `passw`, or one of its words (split at
`- _ .`) is `env args arg argv pad devlog token(s) config cfg conf setting(s) stress secret(s) key(s) password passwd
save(s) savedata prefs options opts param(s)`. So `env.txt args.txt pad.txt devlog.txt hello-env.txt hello-stress.txt
env_gl.txt args_gl.txt env_gl_audio.txt args_gl_audio.txt` never leave the console, while `probe.txt menu_gl.txt
menu-aot.txt hello-managed.txt physics-t1-A-x.txt` do. Everything else (`persistent/` = saves, `managed/`, `pack/`,
`data/`, other devices, `..`) is refused.

What a run sends: the app's log; its `*.prev` log only if that file carries no `DEVLOG run=` tag at all (written while
devlog was off; a tagged one belongs to a devlog run that was sent, declined, dropped, cleared or is still kept, and is
never sent as somebody's `*.prev`); the extra files the app lists. A run kept from an earlier launch sends the copies made then, with `late=1`.

Endpoints, in the order the client calls them:
1. `GET /v1/ping` -> 200 `ok shpdlog/1 max_file=<bytes> max_run=<bytes> proof=<MAC("pong|" + nonce + "|" + ip + "|" + port)>`
   where nonce is the request's and ip:port the receiver's bound address. **The client checks the proof against the
   address it connected to before it sends anything else**: an address that answers without a valid proof (a stale
   cached address after a DHCP change, another device) is ignored and discovery runs instead. The client lowers its
   caps to `max_file` / `max_run`.
2. `POST /v1/runs/<app>/<run>/meta` -- `key=value\n` lines, <= 8 KiB, keys `^[a-z][a-z0-9_]{0,31}$`, values printable
   ASCII <= 256 bytes, <= 64 keys. Re-sent meta merges (later wins); the key and size caps hold for the **merged** meta
   (422 / 413). Values shaped like a MAC address are refused (422), so the client masks them (`0a:1b:...` ->
   `xx:xx:...`) in every value. Keys:

   | key | example | meaning |
   |---|---|---|
   | `proto` | `1` | |
   | `client` | `devlog-c/3` | (revision 2 clients: `devlog-c/2`) |
   | `platform` | `vita` / `mac` | |
   | `app` | `probe` | same as the path |
   | `title_id` | `SHPD00001` | |
   | `build` | `20261007-153000-ff30827` | the app's build id string |
   | `commit` | `ff30827` | when the app passes one |
   | `fw` | `3.65` | `sceKernelGetSystemSwVersion` (a spoofed version shows as spoofed) |
   | `model` | `PCH-2000` / `PCH-1000` / `VTE-1000` / `mac` | family only: `sceKernelIsPSVitaTV`, and whether `imc0:` exists |
   | `free_mem_kb`, `free_cdram_kb`, `free_phycont_kb` | `198656` | free memory per pool when the meta was written |
   | `outcome` | `ok` / `fail` / `crash` / `abort` / `hang` / `pending` / `unknown` | |
   | `detail` | `FAIL gpu: FBO incomplete` | one line from the app, <= 200 chars, printable ASCII |
   | `started` | `2026-10-07T15:30:00Z` | device clock (may be wrong) |
   | `uptime_ms` | `61234` | run time when the run ended (or when the meta was written) |
   | `attempt` | `1` | upload attempt for this run (2+ = retried at a later launch) |
   | `late` | `0` / `1` | 1 = sent by a later launch (crash, or failed / timed-out upload) |
   | `files` | `probe.txt,probe.prev.txt` | names the client will upload |
   | `meta` | `reduced` | only in the reduced meta (below) |

   **Reduced meta**: if the receiver refuses the meta for good (400/404/405/411/413/415/422), the client retries once
   with only the keys it makes itself (no `title_id build commit detail files`, plus `meta=reduced`), so an odd app
   string never costs the run its logs. If that is refused too, the run is dropped and the call says so
   (`DEVLOG_REJECTED`, `why` = "run rejected by the receiver (meta HTTP <n> even reduced)").
3. `POST /v1/runs/<app>/<run>/files/<name>` -- body = the bytes, `Content-Length: N`. 200 `ok bytes=<n> sha256=<hex>`;
   the client compares the hash with its own. Re-uploading a name replaces it.
   - Two passes over the file range: the first counts what the text guarantee replaces and computes the SHA-256 the
     request is authenticated with; the second streams it in 8 KiB pieces. A file that changed in between gets 409
     (transient, retried later).
   - Text guarantee: NUL and control bytes other than TAB/LF/CR/ESC (and DEL) go as `?` (same length;
     `X-Shpd-Sanitized: <n>` says how many). The receiver also refuses content that is not mostly UTF-8 (more than 4
     invalid sequences and more than 1 % of the bytes: 415).
   - Size: over min(8 MiB, `max_file`) the client sends the **last** that many bytes (`X-Shpd-File-Size`,
     `X-Shpd-Offset` say so). Run cap min(32 MiB, `max_run`), <= 16 files.
4. `POST /v1/runs/<app>/<run>/done` -- `outcome=<..>\n` -> 200 `ok`. After it the client deletes the run's marker and
   copies. `done` costs 256 bytes against the receiver's daily cap (429 once it is used up).

Status codes and the client's reaction:
- 200 success.
- 401/403: bad token -> stop, keep everything, the `why` line says "receiver refused the token".
- 400/404/405/411/413/415/422 on a **file**: that file is dropped (logged), the run goes on. On **meta**: the reduced
  retry, then the run is dropped (above).
- 409, 429, 503, 507, other 5xx, connect / stall / timeout errors, short or unparsable answers: transient -> stop, keep
  the run (copy it if it is the current one), retry at the next launch.

## 5. Storage (receiver only)

    $SHEPHERD_WORK/devlog/inbox/<yyyy-mm-dd>/<app>/<run>/{meta.json,manifest.json,files/<name>}
    $SHEPHERD_WORK/devlog/inbox/index.jsonl

No client address is stored in the inbox. Caps: 8 MiB/file, 32 MiB and 32 files per run, 8 KiB merged meta,
256 MiB/day, 1000 new runs/day, 4 GiB total, 2 GiB free-space floor; each new run costs 1 KiB and each stored event
256 B against the day/total caps (so empty runs and repeated `done` cannot fill the disk); parallel uploads reserve
their file slot and bytes before the body is read.

## 6. Client timing (the "a missing server never blocks the app" rule)
- **Revision 3: none of the below runs on the app's thread.** Every reach, transfer and keep copy is a job on one
  background worker (Vita: `sceKernelCreateThread` priority 191 = the lowest user priority, CPU mask
  `SCE_KERNEL_CPU_MASK_USER_2` = core 2, which the Shepherd apps leave spare; an app passes another spare core in
  `devlog_app.thread_affinity`; POSIX: a pthread at QoS "utility"). The app's thread reads the config, writes the
  marker and the tag, starts or collects jobs: `devlog_begin` 0.45-0.65 ms, `devlog_end_async` ~0.1 ms on the Mac
  (`mac/test.sh` §11, -O2). Network happens only at run end (`devlog_end`, `devlog_send_now`) and at the next launch
  (`devlog_begin`, kept runs only); `devlog_cancel` (and `devlog_end` while a launch send runs) stops a job within a
  few ms (every wait polls a stop flag), its runs stay kept and the stop is not a failed attempt. The budgets below
  are per job.
- **Reach budget 2000 ms per call**, counted from the call's start (copies included): sceNet init, the Wi-Fi state,
  the configured / cached address (connect <= 600 ms, ping <= 800 ms), discovery <= 700 ms, backoff 100 / 250 / 500 ms,
  all stop at 1700 ms; 300 ms are kept for what follows a failed reach (sceNet term, the keep copies, the marker).
  No Wi-Fi returns at once. `devlog_begin` with nothing kept does not touch the network.
- **Transfer budget** once the receiver answered: `begin_ms` (default **2500 ms**) for `devlog_begin` (it runs at app
  start), `max_total_ms` (default **20000 ms**) for `devlog_end` / flush. When it runs out, the rest waits for a later
  launch; that does not count as a failed attempt.
- **Stall rule**: a request whose send makes no progress for **1500 ms** is abandoned (a receiver or Wi-Fi that dies
  mid-upload), and an answer must come within 3000 ms of the last byte; both also stop at the call's budget. On both
  platforms every socket is non-blocking and every wait is against one deadline.
- Kept runs: each keeps an attempt count (a stall or error with the receiver reachable counts; running out of time
  does not); after 8 attempts, 14 days, or beyond `keep_pending` (revision 3: 2) runs the marker and copies are
  deleted (logged).
- Keep copies: the last `keep_kb` (1 MiB) of each file, at most 2 x `keep_kb` per call, in 32 KiB card I/O. A stop
  is checked per piece (copies, tag scans, the upload hash pass): a copy cut short is deleted and its run left as it
  was, to be copied by the run-end job or the next launch.

## 7. Client config: `ux0:data/shepherd/devlog.txt` (Mac tests: any path)
`key=value` lines, `#` comments, unknown keys ignored. **No file, `enabled` != 1, no valid token or `mode=off` -> the
client does nothing at all** (no sceNet, no files). `receiver.py gen-token` writes a ready one.

    enabled=1
    token=<64 hex>                 # required
    mode=ask                       # revision 3: ask (also: no mode= line, or any other value) | auto | off
    clear=0                        # revision 3: clear=1 = a one-shot wipe at the next devlog_begin (below)
    server=192.0.2.20:8770       # optional: the Codex server (base /vita/devlog, §10); :47710 = the standalone
                                   #   receiver; ip:port/base = another base. Without it: discovery (§3)
    discover=192.0.2.20          # optional; unicast discovery target instead of broadcast
    discovery_port=47711           # optional
    max_total_ms=20000             # optional; transfer budget of devlog_end / flush (1000..120000)
    begin_ms=2500                  # optional; transfer budget of devlog_begin (1000..20000)
    reach_ms=2000                  # optional; reach budget (600..2000)
    keep_kb=1024                   # optional; tail kept per file for a later launch (64..8192)
    keep_pending=2                 # optional; max kept runs (1..32); revision 3 default 2 = the current run + the
                                   #   previous one (with a run active, devlog_begin keeps 1 older run)

Revision 3:
- **mode=ask** (the default): runs are recorded, crashes picked up and copied exactly as before, but nothing is sent
  until the user answers yes (`devlog_should_ask` / `devlog_send_now(1)` / the console prompt `devlog_ask_console`:
  X send, CIRCLE delete, START later). `devlog_begin` then copies kept runs without sending (`DEVLOG_ASK`), `devlog_end`
  keeps the ended run without network (`DEVLOG_ASK`). A "no" deletes the kept runs; their logs stay on the card and
  are not sent later as somebody's `*.prev` either (they carry their tag). **mode=auto** = the revision-2 behaviour, on the
  worker. **mode=off** = no file.
- **clear=1**: the next `devlog_begin` of any Shepherd app deletes every kept run (markers, notes, copies), the
  cached address and that app's previous log (the current one holds only the new launch's first lines), then
  overwrites the `1` with `0` in place (one byte; nothing else in the file moves, the token is not rewritten). Works
  with any `mode` and `enabled`. `devlog_clear` does the same on request, and empties the current log too.

Client state: `ux0:data/shepherd/devlog/` (`pending/<run>.run` markers with the tag offset, `pending/<run>.note`
written by `devlog_note` (merged into the marker by the next load), `pending/<run>/` copies, `server.txt` =
`ip:port[/base]`; a `sent.txt` from the first revision-3 build is deleted by the next clear). Copies of logs only.

## 8. Crash pickup
`devlog_begin` writes the marker (state `running`, the log's size = the offset the tag goes to) and then the
`DEVLOG run=<id> app=<app>` line into the log. A later launch of any Shepherd app that finds a `running` / `ended`
marker looks for that tag in the log's current and `*.prev` name, in a 32 KiB window starting 1 KiB before the
recorded offset (so an app may log as much as it likes before `devlog_begin`), copies the matching file and sends it
with `outcome=crash` (or what `devlog_note` recorded) and `late=1`. If neither file carries the tag, the run is sent
without a log and its `detail` says why: "no log file left", "overwritten by later launches" (another run's tag is
there), or "this run's tag is not in the log" (rewritten while devlog was off).

## 9. Revision 2 changes (2026-10-07 review)
1. Bearer token -> per-request HMAC (`Authorization: SHPD1 ...`), 15-minute nonce replay cache, body hash check (409).
2. Ping answer carries `proof=`; the client sends nothing to an address that cannot prove it knows the token.
3. DISCOVER without `kid=`.
4. Client: stall rule (1.5 s), `begin_ms` budget, budget exhaustion is not a failed attempt, POSIX layer
   non-blocking with deadlines (same timings as the Vita), raw network error in `why`, sceNet pool sized from free
   memory (1 MiB / 512 KiB / 256 KiB).
5. Client: MAC-shaped meta values masked, reduced-meta retry, `DEVLOG_REJECTED` + `runs_rejected` with a correct why.
6. Client: config files refused by name pattern (env_gl.txt, args_gl.txt, ...); tag found by recorded offset; keep
   copies bounded (1 MiB per file, 2 MiB per call, 32 KiB I/O) and counted in `res->ms`.
7. Receiver: merged-meta caps, new-run and event charges, new-runs/day cap, per-run reservations for parallel
   uploads, UTF-8 rule, RFC-1918-only bind rule, symlink-safe index, per-IP connection limit + header deadline,
   escaped/throttled/rotating log.

## 10. Server binding: the Archive Codex server (revision 3)

Owner decision 2026-10-07: the dev logs go to the **Archive Codex server** (`stream_server.py`, port 8770, routes in
`vita_devlog_routes.py`), not to a separate service. The standalone receiver (§2, TCP 47710) stays as the fallback and
for the tests; `receiver.py serve --base /vita/devlog` speaks this binding too (`test_receiver.py` T10, `mac/test.sh`
§11). Token, MACs, bodies, headers, status codes, caps, the content rules and the storage are those of §1, §4 and §5;
only the targets, the ping proof's address and the gates differ.

**Targets** (base `/vita/devlog`; two EXACT paths, so the server lists them as auth-free exact paths and opens no path
tree; everything that identifies the run is in a fixed-order query):

| op | request target |
|---|---|
| ping | `GET /vita/devlog/v1/ping` |
| meta | `POST /vita/devlog/v1/upload?op=meta&app=<app>&run=<run>` |
| file | `POST /vita/devlog/v1/upload?op=file&app=<app>&run=<run>&name=<name>` |
| done | `POST /vita/devlog/v1/upload?op=done&app=<app>&run=<run>` |

- The raw target must match, in full: `^/vita/devlog/v1/upload\?op=(meta|done|file)&app=(APP)&run=(RUN)(?:&name=(NAME))?$`
  with the §4 regexes, `name` present exactly when `op=file`; no other key, order or `%`-encoding (the client never
  encodes: every value is inside its regex). Anything else -> 400 (under `/vita/devlog/v1/`) or 404.
- **The MAC covers the target exactly as sent, query included** (`req|POST|/vita/devlog/v1/upload?op=...|nonce|sha`).
  A server that only sees the parsed URL rebuilds it as `path + "?" + raw query string` (werkzeug: the raw
  `QUERY_STRING`, never the decoded args).
- **Ping proof**: `proof=MAC("pong|" + nonce + "|" + ip + "|" + port)` with ip:port = the address the client
  connected to. The server takes it from the `Host` header (the client always sends `Host: <ip>:<port>`), and only
  when that ip is one of its own RFC-1918 interface addresses and the port is its own (8770); otherwise it answers 400.
- **Auth failures** (missing/bad MAC, replayed nonce): 401 with an **empty body** (or a body starting `shpdlog` /
  `replayed`), like the receiver. The client takes a 401/403 with any other body (the Codex login wall's JSON, before
  the routes are installed) for "not a receiver" and tries its next address instead of "token refused".
- **LAN only** (checked first, before the MAC): `remote_addr` in 10/8, 172.16/12 or 192.168/16 and not loopback; and no
  `Cf-Connecting-Ip`, `Cf-Ray`, `CF-Visitor`, `X-Forwarded-For`, `X-Forwarded-Host`, `Forwarded` or `Tailscale-*`
  header (the tunnel and `tailscale serve` deliver from 127.0.0.1, and add such headers). Refused: 404, empty body.
- **Discovery**: UDP 47711 as §3, on the LAN interface only, same drops / replay cache / rate limit / subnet filter;
  the HERE answer carries `base=/vita/devlog` and its proof covers it (§3). host = the server's LAN IPv4, port 8770.
  It never answers a loopback or 100.64/10 source and never contains the token. The Codex responder (0.0.0.0:47711
  pinned to the LAN interface with `IP_BOUND_IF`) and the standalone receiver's cannot share the port: whichever binds
  first answers, the Codex one retries every 30 s. So the standalone receiver runs only while the Codex server is down
  (`devlogctl.sh stop` otherwise); the client's preference for a Codex answer (§10 Client) needs it to be heard.
- **Storage**: `$SHEPHERD_WORK/devlog/inbox/` with the §5 layout and caps (outside the Codex project and media
  trees). The viewer (`/vita/logs`, `/vita/logs/<run>`) sits behind the server's normal login and is admin-only.

**Client** (revision 3): candidates = `server=` from devlog.txt, else the cached `devlog/server.txt`; then what
discovery finds, the Codex answer first. The least-tried live candidate goes next (a fresh one at once, without the
backoff); a candidate that answers without a valid proof, or with a foreign 401, is dropped. All within the 2 s reach
budget. So with both servers up the Codex server gets the logs; with it down (or its routes not installed yet) the
standalone receiver does. When the configured or cached address is a standalone receiver (no base), a short discovery
(<= 200 ms, one round) runs first and only a Codex answer from it is used, in front: a cached fallback never keeps the
runs away from a Codex server that answers again. `server=a.b.c.d:8770` (no path) means this binding with base `/vita/devlog`;
`server=a.b.c.d:<port>/<base>` another base; any other address without a path is a standalone receiver.

## 11. Revision 3 changes (2026-10-07)
1. The Archive Codex server binding (§10): two exact paths, the run in a fixed-order query, `base=` in HERE under the
   proof, the Codex answer preferred, the standalone receiver as the fallback, a foreign 401 is "not a receiver".
2. Client: one low-priority background worker for every network transfer and keep copy (§6); the app's thread
   costs < 1 ms per call; `devlog_end_async` / `devlog_wait` / `devlog_busy` / `devlog_cancel`; worker log lines are
   queued and handed to the app's log function on the app's thread.
3. Client: `mode=ask|auto|off` (default ask), `devlog_should_ask` / `devlog_send_now` / `devlog_ask_console`
   (+ `devlog_pad.h` for the Vita's buttons); a "no" deletes the runs (see §12 for the `*.prev` rule).
4. Client: `devlog_clear` and the one-shot `clear=1`; retention `keep_pending=2` (current + previous).
5. Client: `devlog_note` writes `<run>.note` (own buffer, any thread), merged into the marker on load.
6. Client: optional `devlog_logfile_*` writer: write-through (dev default: complete crash logs) or buffered (release:
   64 lines / 2 s / 16 KiB, flushed around the tag, by `devlog_note` and `devlog_end`).
7. `client=devlog-c/3`, `User-Agent: shepherd-devlog/3`. Nothing else on the wire changed for a standalone receiver.

## 12. Revision 3 client fixes (2026-10-07 verification; nothing on the wire changed)
1. `*.prev` rule: only an untagged `*.prev` goes with a run. The first revision-3 build skipped a tagged one only if
   its id was on a 32-entry sent list or still kept; `devlog_clear` from another app emptied that list, so a run the
   user had declined could leave later as that app's `*.prev`. The sent list (`devlog/sent.txt`) is gone.
2. Vita worker: when the asked priority / core is refused, priority 191 on any user core comes before the default
   priority (the last resort); the next DEVLOG line names which.
3. Stop requests reach every loop: keep copies, tag scans and the upload hash pass check the stop per piece; a copy cut
   short is deleted and its run adopted again by the run-end job (or the next launch). `devlog_end_async` stopping a
   launch job mid-copy: 3 ms on the Mac (was 24-27 ms).
4. A cached or configured standalone receiver no longer keeps the runs from the Codex server: a short discovery looks
   for it first (§10).
5. A UTF-8 BOM at the start of devlog.txt is skipped (it turned devlog off).
