# devlog receiver (Mac side of "the Vita sends its logs back over Wi-Fi")

The Shepherd Vita apps (probe, hello, gltest, shepherd, physbench, the log-send test) keep their logs in
`ux0:data/shepherd/<app>.txt` (+ `<app>.prev.txt`). With the devlog client (`vita/devlog/`) they can also send those
logs to this small receiver on the Mac, over the same Wi-Fi, so a device run needs no FTP copying afterwards.

It is its own little program. It has nothing to do with the Archive Codex server or its tunnel, uses its own ports,
only listens on the home network, and only takes log files from a Vita that knows the secret token (the token itself
never goes over the network: requests carry HMACs). Design and release plan: `vita/notes/devlog.md`.

**Revision 3 (2026-10-07):** the owner decided the logs go to the **Archive Codex server** (port 8770, its
`vita_devlog_routes.py`, viewer `/vita/logs`), with the same token and inbox. The Vita client prefers the Codex
server's discovery answer and falls back to this receiver when the Codex server does not answer, so this receiver is
now the fallback (and the tests' stand-in). `serve --base /vita/devlog` makes it speak the Codex binding
(PROTOCOL.md §10: `GET /vita/devlog/v1/ping`, `POST /vita/devlog/v1/upload?op=meta|file|done&app=..&run=..[&name=..]`,
`base=` in the discovery answer under the proof); without `--base` it speaks the revision-2 paths as before.

| | |
|---|---|
| program | `tools/devlog-receiver/receiver.py` (Python 3 standard library only; `/usr/bin/python3` 3.9 or newer) |
| start/stop | `tools/devlog-receiver/devlogctl.sh` |
| listens on | TCP **47710** on the Mac's LAN address only (e.g. `192.0.2.125`), UDP **47711** for "where is the Mac?" |
| secret | `$SHEPHERD_WORK/devlog/token` (mode 0600; see "Token" below) |
| logs arrive in | `$SHEPHERD_WORK/devlog/inbox/<yyyy-mm-dd>/<app>/<run>/files/` |
| its own log | `$SHEPHERD_WORK/devlog/receiver.log` (rotating, 3 files of 5 MB at most) |
| protocol | `vita/devlog/PROTOCOL.md` (shpdlog/1 revision 3; this receiver's paths: §4, the Codex binding: §10) |

## First time (5 minutes)

1. **Make the token** (once):

       tools/devlog-receiver/devlogctl.sh token

   It writes `$SHEPHERD_WORK/devlog/token` and a ready config for the Vita,
   `$SHEPHERD_WORK/devlog/vita-devlog.txt` (both readable only by you). The token is not printed.

2. **Put the config on the Vita.** In VitaShell press SELECT (FTP), then copy `vita-devlog.txt` from the Mac to the
   Vita as **`ux0:data/shepherd/devlog.txt`** (rename it on the way). It contains:

       enabled=1
       token=<64 hex characters>
       mode=ask
       clear=0
       # server=192.0.2.125:8770
       # server=192.0.2.125:47710

   `mode=ask` (also without the line): after a run the app asks before anything is sent (X send, CIRCLE delete, START
   later); `mode=auto` sends on its own; `mode=off` never. `clear=1` once: the next start deletes the kept runs and
   that app's previous log, then sets `clear=0`.
   Without a `server=` line the Vita finds the Mac by itself (a UDP broadcast that only the Codex server or this
   receiver answers, and only if the Vita knows the token). If that does not work on your network, remove the `# ` in
   front of one `server=` (the address must be the Mac's current LAN IP: `ipconfig getifaddr en0`; 8770 = the Codex
   server, 47710 = this receiver).
   No `devlog.txt` on the Vita, `enabled=0` or `mode=off` = the apps never touch the network.

3. **Start the receiver:**

       tools/devlog-receiver/devlogctl.sh start

   It prints where it listens, e.g. `http://192.0.2.125:47710  discovery udp 0.0.0.0%en0:47711`.
   It keeps running in the background until `devlogctl.sh stop` or a restart of the Mac.

4. **Run the test VPK** ("Shepherd Log Send Test", **SHPD00014**, from `vita/devlog/test/`) on the Vita, then on the
   Mac:

       tools/devlog-receiver/devlogctl.sh list

   shows one line per run: date, app, run id, outcome, model, firmware and the files received.

### What macOS may ask

- **Firewall.** The macOS firewall is **off** on this Mac (checked 2026-10-07), so there is no prompt. If you turn it on,
  the first start shows *"Do you want the application “Python” to accept incoming network connections?"* → **Allow**.
  (Denied by mistake: System Settings → Network → Firewall → Options → set Python to "Allow incoming connections".)
- **Local Network.** Newer macOS versions may ask *"“Python” (or “Terminal”) would like to find and connect to
  devices on your local network"* → **Allow**. The receiver needs it to answer the Vita's discovery broadcast. If it
  was denied, discovery silently fails while uploads with a `server=` line still work; fix it in System Settings →
  Privacy & Security → Local Network.
- When started at login (LaunchAgent, below) macOS may also ask once whether Python may access files on a removable
  volume (`$SHEPHERD_WORK`) → Allow.

## Everyday commands

    devlogctl.sh start | stop | restart | status
    devlogctl.sh list [--days 7]       # runs received in the last N day folders
    devlogctl.sh log                   # the receiver's own log (+ startup errors, if any)
    devlogctl.sh run --bind 127.0.0.1  # foreground, log on the terminal, Ctrl-C to stop (loopback only: local tests)
    devlogctl.sh token --rotate        # new token: copy the new vita-devlog.txt to the Vita again

`start` / `run` take the receiver's options, e.g. `--iface en1` (use Wi-Fi instead of the default-route interface),
`--bind 192.0.2.32`, `--no-discovery`, `--max-file 16M`.

## What arrives, and where

    $SHEPHERD_WORK/devlog/inbox/
      index.jsonl                                one line per event (meta / file / done)
      2026-10-07/probe/20261007T153000Z-3fa94c0be1d2/
        meta.json        app, TITLE_ID, build, commit, firmware, model, free memory, outcome, times
        manifest.json    files with size + SHA-256, done yes/no, outcome
        files/probe.txt  files/probe.prev.txt  ...

The date folder is the Mac's date when the run first arrived; a crashed run sent by the next launch lands in the day it
was sent, with `late=1` in its meta. The run id is made on the Vita from the time and random digits: nothing in it, or
anywhere in what is stored, identifies the console or an account. The receiver does not store the Vita's IP address
in the inbox (its own log, `receiver.log`, shows it per request for debugging).

## Safety rules it enforces

- **LAN only.** TCP binds the Mac's private LAN address (exactly 10/8, 172.16/12, 192.168/16) of the default-route
  interface, never `0.0.0.0`, never a public, Tailscale (100.64/10), VPN fake-IP (198.18/15) or other special address,
  never behind the Archive Codex tunnel. Connections from outside that subnet are closed at once. The UDP responder has
  to listen on the wildcard address (macOS delivers broadcasts only there) but is pinned to the LAN interface
  (`IP_BOUND_IF`) and drops datagrams from outside the subnet. Do not forward ports 47710/47711 on the router.
- **Authentication without sending the token.** Every request carries
  `Authorization: SHPD1 nonce=… body=<sha256> mac=<HMAC>` over the method, the exact path, a fresh nonce and the body's
  hash; a nonce is accepted once (15 minutes), and a body that does not match its hash is refused (409). Wrong/missing
  → 401; 10 failures in a minute → that address gets 429 for 10 minutes. The ping answer carries a proof (an HMAC
  bound to the request's nonce and this receiver's address), and the Vita sends nothing to an address that cannot
  give it. Discovery: the Vita proves it knows the token with an HMAC over a fresh random number (nothing stable is in
  the datagram), and the answer carries an HMAC proof the Vita checks. Replays are ignored.
- **Logs only.** File names must match `[A-Za-z0-9][A-Za-z0-9._-]{0,63}` with a `.txt .log .csv .tsv .json` ending;
  no `/`, no `..`, no `%`-escapes, no hidden files. The content must be text: no NUL byte, ≤ 0.5 % control bytes, and
  mostly UTF-8 (more than 4 invalid sequences and over 1 % of the bytes → 415), so save files, eboots, SFOs, archives,
  images or other binaries are refused. Meta keys that would carry an identifier (psid, openpsid, account, username,
  MAC address, SSID, IP, serial, ...) are refused (422), as is any value shaped like a MAC address.
- **Caps** (options of `serve`): 8 MiB per file, 32 MiB and 32 files per run (parallel uploads reserve their slot and
  bytes first, so they cannot overshoot), 8 KiB of meta per run (the merged meta, not just one request; ≤ 64 keys),
  256 MiB and 1000 new runs per day, 4 GiB for the whole inbox; every new run costs 1 KiB and every stored event
  256 B against those caps (empty runs and repeated `done` cannot fill the disk); no uploads when the volume has
  < 2 GiB free. Connections: 8 at a time, **2 per address**, the request headers must arrive within **8 s** of the
  connect (a slow-drip client is closed then), 120 requests a minute per address, 15 s idle timeout, 120 s per
  authenticated request. Over a cap → 413 / 429 / 503 / 507, and the Vita keeps the run for a later try.
- **Its own log cannot be flooded or abused.** Lines caused by requests that did not authenticate (401, 429, bans,
  malformed requests, refused / timed-out connections, bad discovery datagrams) are limited to one per address per
  minute (the next line says how many were left out). Request data in the log is cut to 300 characters and every
  non-printable byte is escaped, so no terminal escape sequence reaches `devlogctl.sh log`. The file rotates at 5 MB
  (3 files at most).
- **Writes only** under `$SHEPHERD_WORK/devlog/inbox/` (plus its own `receiver.pid`, `receiver.log*` and
  devlogctl's `receiver.out` for startup errors, next to it). `index.jsonl` is opened with `O_NOFOLLOW` (a planted
  symlink is refused, never written through; the receiver does not start on an inbox whose `index.jsonl` is not a
  regular file). If `$SHEPHERD_WORK` is not mounted it does not start. Nothing is ever deleted by the
  receiver; delete old date folders by hand when you like.

### Token

The token file is 0600, but `$SHEPHERD_WORK` is mounted with **"ignore ownership"** (`noowners`): on such a
volume file modes do not keep other local accounts out (the receiver says so at start). On this Mac (one account)
that is fine. On a Mac with more accounts, keep the token on the boot disk and point everything at it:

    export SHPD_DEVLOG_TOKEN_FILE="$HOME/Library/Application Support/shepherd-devlog/token"
    devlogctl.sh token && devlogctl.sh start          # receiver.py also takes --token-file

## Run it at login (optional, your call)

`com.shepherd.devlog-receiver.plist` is a ready LaunchAgent. Nothing installs it: a LaunchAgent is a standing piece of
configuration, so it is your decision. To install:

    cp tools/devlog-receiver/com.shepherd.devlog-receiver.plist ~/Library/LaunchAgents/
    launchctl bootstrap gui/$(id -u) ~/Library/LaunchAgents/com.shepherd.devlog-receiver.plist

To remove:

    launchctl bootout gui/$(id -u)/com.shepherd.devlog-receiver
    rm ~/Library/LaunchAgents/com.shepherd.devlog-receiver.plist

It runs `/usr/bin/python3` with the script at `$SHEPHERD_REPO/...` and needs **both drives**: the script is on
"DATA" (`$SHEPHERD_REPO` links there), the inbox and the log on $SHEPHERD_WORK. If either is missing it writes
one line and exits 0 (no restart loop) and starts again when a volume mounts. It restarts itself if the Mac's LAN
address changes. A configuration error (no token, port taken, ...) also stops it after one line instead of retrying
every 30 s; fix it, then `launchctl kickstart gui/$(id -u)/com.shepherd.devlog-receiver`. Its request log is the same
rotating `$SHEPHERD_WORK/devlog/receiver.log`; `~/Library/Logs/shepherd-devlog-receiver.log` on the boot disk
only gets those startup lines (one per mount event at most). Use either the LaunchAgent or `devlogctl.sh start`, not
both (the second one refuses to start: one receiver per pid file). `devlogctl.sh status/list/log` work with either.

## Tests

    tools/devlog-receiver/test_receiver.py     # 42 protocol + hardening tests, receivers on 127.0.0.1 only
    tools/devlog-receiver/e2e_test.sh          # those + devlogctl + the C client's Mac build against the receiver

Both write under `$SHEPHERD_WORK/devlog/e2e/<stamp>/`, bind only `127.0.0.1` on random free ports and stop
every receiver they start. The hardening tests cover the 2026-10-07 review's attacks: escape sequences and 64 KB
request lines (log stays clean and small, rotation works), slow-drip clients (a real client gets through within 10 s
with the defaults), 8 parallel uploads into one run (caps hold), empty runs / repeated `done` / growing meta (charged,
capped), a symlinked `index.jsonl` (never followed), binary content of high bytes (refused; Latin-1 sprinkles and
split UTF-8 accepted), the RFC-1918 rule, MAC binding (method, path, nonce, body), replays, and a fake receiver
(the token is never on the wire). T10 (revision 3) runs a receiver with `--base /vita/devlog`: `base=` in the
discovery answer under the proof (a changed or removed base fails), ping + a full run through the exact upload path,
the MAC covering the query, and 15 malformed targets (wrong order, extra keys, `%`-encoding, a name on meta, the
receiver's own paths) that create nothing. `shpdlog_client.py` is a small Python client for the same protocol (handy
to check a running receiver from the Mac: `shpdlog_client.py --server 192.0.2.125:47710 --app logtest some.log`;
`--base /vita/devlog` for the Codex binding; with discovery it takes the base from the answer).

## Troubleshooting

| symptom | look at |
|---|---|
| Vita says `no receiver within 2000 ms` / run stays pending | `devlogctl.sh status` (running?), same Wi-Fi?, Local Network permission (above), try `server=` in devlog.txt; the `(last network error: …)` part names the failing call |
| `401` in `devlogctl.sh log` | the token on the Vita differs from the Mac's: copy `vita-devlog.txt` again (`devlogctl.sh kid` shows the Mac's fingerprint) |
| Vita log: `answered without a valid proof` | something else answers at the cached / configured address (the Mac's IP changed?): fix `server=`, or delete it and let discovery find the Mac |
| `refused connection from ... (outside ...)` | the Vita is on another subnet (guest Wi-Fi?) |
| `busy (...): 503` | more than 2 connections from one address, or all 8 slots taken; the Vita retries at its next launch |
| `$SHEPHERD_WORK is not mounted; not starting` | mount the drive, then `devlogctl.sh start` |
| `LAN address changed ... exiting` | the Mac changed network; `devlogctl.sh start` again (the LaunchAgent does it itself) |
