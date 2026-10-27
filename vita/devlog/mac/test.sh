#!/bin/zsh
# Mac test of the devlog client (vita/devlog/, POSIX platform layer = the same sources the Vita apps compile)
# against the real receiver (tools/devlog-receiver/receiver.py) bound to 127.0.0.1 only, plus hostile peers.
#   vita/devlog/mac/test.sh        exit 0 = all passed
# Builds into vita/devlog/out/mac/ (git-ignored); test data under ${SHEPHERD_WORK:-$HOME/shepherd-work}/devlog/client-test/<stamp>/.
# Starts receivers / fakes on 127.0.0.1 with random ports and stops every one of them (trap), leaves nothing running.
# No datagram leaves this Mac: every devlog.txt written here names discover=127.0.0.1 (no broadcast), with the
# section's UDP port or port 9 (discard: nothing answers) where a test only wants "no receiver found".
set -u
HERE=${0:a:h}
DEVLOG=${HERE:h}
REPO=${DEVLOG:h:h}
RX=$REPO/tools/devlog-receiver/receiver.py
PY=${PY:-/usr/bin/python3}
CC=${CC:-clang}
export TMPDIR=${TMPDIR:-${SHEPHERD_WORK:-$HOME/shepherd-work}/tmp}
mkdir -p "$TMPDIR"
OUT=${DEVLOG_TEST_ROOT:-${SHEPHERD_WORK:-$HOME/shepherd-work}/devlog/client-test/$(date +%Y%m%d-%H%M%S)}
BIN=$DEVLOG/out/mac
mkdir -p "$OUT" "$BIN"
PASS=0 FAIL=0
PIDS=()
ok()  { PASS=$((PASS + 1)); echo "  ok    $*"; }
bad() { FAIL=$((FAIL + 1)); echo "  FAIL  $*"; }
check() { local d=$1; shift; if "$@" > "$OUT/last.txt" 2>&1; then ok "$d"; else bad "$d"; sed 's/^/        /' "$OUT/last.txt" | head -20; fi; }
val() { sed -n "s/^$2=//p" "$1" | tail -1; }
chk() { "$PY" "$HERE/check.py" "$@"; }
cleanup() { for p in $PIDS; do kill -TERM $p 2>/dev/null; done; sleep 0.2; for p in $PIDS; do kill -KILL $p 2>/dev/null; done; PIDS=(); }
trap cleanup EXIT INT TERM

echo "devlog client test -> $OUT"
echo "== 0. build (clang, ASan + UBSan) and the arm-vita-eabi compile of the same unity file"
SAN=(-fsanitize=address,undefined)
check "selftest builds"   $CC -std=gnu11 -O1 -g -Wall -Wextra -Werror -Wno-unused-parameter $SAN "$HERE/selftest.c" -o "$BIN/selftest"
check "devlog_cli builds" $CC -std=gnu11 -O1 -g -Wall -Wextra -Werror -Wno-unused-parameter $SAN "$HERE/devlog_cli.c" -o "$BIN/devlog_cli"
check "devlog_cli builds -O2 without sanitizers (the timing checks of section 11)" \
	$CC -std=gnu11 -O2 -Wall -Wextra -Werror -Wno-unused-parameter "$HERE/devlog_cli.c" -o "$BIN/devlog_cli_o2"
if [ -x "$HOME/vitasdk/bin/arm-vita-eabi-gcc" ]; then
	check "arm-vita-eabi-gcc: devlog_unity.c compiles warning-free (-Wall -Wextra -Werror)" \
		"$HOME/vitasdk/bin/arm-vita-eabi-gcc" -std=gnu11 -O2 -Wall -Wextra -Werror -Wno-unused-parameter -c "$DEVLOG/devlog_unity.c" -o "$BIN/devlog_vita.o"
fi
export ASAN_OPTIONS=detect_leaks=0

echo "== 1. selftest (SHA-256 / HMAC vectors, path rule, masking, meta, tag search, HERE proof, dates)"
"$BIN/selftest" > "$OUT/selftest.txt" 2>&1
check "selftest: $(tail -1 "$OUT/selftest.txt")" grep -q ' 0 failed' "$OUT/selftest.txt"
TOK0=00112233445566778899aabbccddeeff00112233445566778899aabbccddeeff
PYK=$("$PY" -c "
import hashlib,hmac;t=b'$TOK0';m=lambda s:hmac.new(t,s,hashlib.sha256).hexdigest()[:32]
print(hashlib.sha256(t).hexdigest()[:16], m(b'discover|0123456789abcdef'), m(b'req|GET|/v1/ping|0123456789abcdef|'+hashlib.sha256(b'').hexdigest().encode()), m(b'pong|0123456789abcdef|192.0.2.125|47710'), m(b'here|0123456789abcdef|192.0.2.125|8770|/vita/devlog'))")
check "kid, discovery / request / pong / Codex HERE MACs equal Python's hashlib + hmac" \
	test "$(val "$OUT/selftest.txt" kid) $(val "$OUT/selftest.txt" mac) $(val "$OUT/selftest.txt" reqmac) $(val "$OUT/selftest.txt" pongmac) $(val "$OUT/selftest.txt" heremac)" = "$PYK"
LFT=$(val "$OUT/selftest.txt" lf_through_us); LFB=$(val "$OUT/selftest.txt" lf_buffered_us)
check "log-file writer, 20,000 lines on this Mac: write-through $LFT us, buffered $LFB us (buffered is cheaper)" test "$LFB" -lt "$LFT"

[ -f "$RX" ] || { bad "receiver missing: $RX"; echo "== $PASS passed, $FAIL failed"; exit 1; }
C=$OUT/rx; D=$OUT/device; INBOX=$C/inbox
mkdir -p "$C" "$D/runtime/logs" "$D/persistent/saves" "$D/managed"
"$PY" "$RX" --root "$C" gen-token > /dev/null
TOKEN=$(cat "$C/token")
printf 'engine log line 1\nengine log line 2\n' > "$D/runtime/logs/engine.log"
printf 'SAVE-DATA-MARKER\n' > "$D/persistent/saves/slot_0.txt"
printf 'OWNER-FOLDER-MARKER\n' > "$D/managed/notes.txt"
printf 'ENV-MARKER=1\n' > "$D/env.txt"
printf 'ENVGL-MARKER=1\n' > "$D/env_gl.txt"
printf 'ARGSGL-MARKER\n' > "$D/args_gl.txt"
printf 'OUTSIDE-MARKER\n' > "$OUT/outside.txt"
RX_PID=""
rx_start() { # rx_start [tcp udp] [extra serve options...]
	local tp=${1:-0} up=${2:-0}; shift 2 2>/dev/null
	rm -f "$C/receiver.pid"
	"$PY" "$RX" --root "$C" serve --bind 127.0.0.1 --port $tp --udp-port $up --pidfile "$C/receiver.pid" \
		--min-free 1M --rate 1000 "$@" >> "$C/receiver.log" 2>&1 &
	RX_PID=$!; PIDS+=($RX_PID)
	local i=0; while [ ! -s "$C/receiver.pid" ] && [ $i -lt 100 ]; do sleep 0.05; i=$((i + 1)); done
	read -r _ TCP UDP < "$C/receiver.pid"
}
rx_stop() { [ -n "$RX_PID" ] && { kill -TERM $RX_PID 2>/dev/null; wait $RX_PID 2>/dev/null; }; RX_PID=""; }
# sections 2-9 are the revision-2 behaviour: mode=auto, and room for 8 kept runs (section 6 keeps 7); section 11 tests
# the revision-3 defaults (no mode= = ask, keep_pending 2)
# discover=127.0.0.1 + discovery_port=9 first: a later discover= / discovery_port= line of the call wins
conf() { { echo "enabled=1"; echo "token=$TOKEN"; echo "mode=auto"; echo "keep_pending=8"; echo "discover=127.0.0.1"; echo "discovery_port=9"; for l in "$@"; do echo "$l"; done; } > "$D/devlog.txt"; }
drive() { local tag=$1; shift; "$BIN/devlog_cli" "$D" "$@" > "$OUT/$tag.txt" 2> "$OUT/$tag.err"; echo "exit=$?" >> "$OUT/$tag.txt"; }
pending_count() { ls "$D/devlog/pending/" 2>/dev/null | grep -c '\.run$'; }

echo "== 2. off"
rm -f "$D/devlog.txt"
drive off run
check "no devlog.txt: begin/end DISABLED (0), nothing created, no network" \
	sh -c "[ '$(val $OUT/off.txt begin.rc)' = 0 ] && [ '$(val $OUT/off.txt end.rc)' = 0 ] && [ '$(val $OUT/off.txt end.net)' = -100 ] && [ ! -e '$D/devlog' ]"
printf 'enabled=1\ntoken=1234\n' > "$D/devlog.txt"; drive off2 run
check "enabled=1 with a malformed token: DISABLED, nothing created" sh -c "[ '$(val $OUT/off2.txt end.rc)' = 0 ] && [ ! -e '$D/devlog' ]"

echo "== 3. receiver up (127.0.0.1), discovery by unicast"
rx_start 0 0 --max-file 262144
conf "discover=127.0.0.1" "discovery_port=$UDP"
drive run1 run --extra runtime/logs/engine.log
R=$(val $OUT/run1.txt run)
check "run: end SENT (1), reached by discovery at 127.0.0.1:$TCP" \
	sh -c "[ '$(val $OUT/run1.txt end.rc)' = 1 ] && [ '$(val $OUT/run1.txt end.server)' = '127.0.0.1:$TCP' ]"
check "run: log (prefix: devlog reports into it afterwards) + engine.log byte-identical, meta, done" \
	chk "$INBOX" logtest "$R" --prefix "logtest.txt=$D/logtest.txt" --same "engine.log=$D/runtime/logs/engine.log" \
	--meta outcome=ok --meta platform=mac --meta model=mac --meta title_id=SHPD00014 --meta build=mac-cli --meta late=0 \
	--meta attempt=1 --meta client=devlog-c/3 --contains "logtest.txt=DEVLOG run=$R" --done
check "run: the run left no marker, no copies" test "$(pending_count)" = 0
check "server address cached for the next launch" grep -q "^127.0.0.1:$TCP\$" "$D/devlog/server.txt"

drive run2 run --extra devlog.txt --extra persistent/saves/slot_0.txt --extra managed/notes.txt --extra env.txt \
	--extra ../outside.txt --extra runtime/logs/engine.log --extra runtime/../devlog.txt --extra env_gl.txt --extra args_gl.txt
R=$(val $OUT/run2.txt run)
check "logs only: exactly the log and runtime/logs/engine.log arrive (the .prev was run1's log, already sent)" \
	chk "$INBOX" logtest "$R" --only logtest.txt,engine.log --done
check "logs only: no token, config (env, env_gl, args_gl), save, owner-folder or outside text anywhere in the inbox" sh -c \
	"'$PY' '$HERE/check.py' '$INBOX' grep '$TOKEN' && '$PY' '$HERE/check.py' '$INBOX' grep SAVE-DATA-MARKER && '$PY' '$HERE/check.py' '$INBOX' grep OWNER-FOLDER-MARKER && '$PY' '$HERE/check.py' '$INBOX' grep ENV-MARKER && '$PY' '$HERE/check.py' '$INBOX' grep ENVGL-MARKER && '$PY' '$HERE/check.py' '$INBOX' grep ARGSGL-MARKER && '$PY' '$HERE/check.py' '$INBOX' grep OUTSIDE-MARKER"
check "logs only: each refusal is in the app's own log" sh -c "[ \$(grep -c 'DEVLOG refused extra file' '$D/logtest.txt') = 8 ]"

drive fail1 run --outcome fail
check "fail: outcome=fail + detail" chk "$INBOX" logtest "$(val $OUT/fail1.txt run)" --meta outcome=fail --meta "detail=FAIL END (test)" --done
drive macd run --outcome fail --detail "FAIL crc 0a:1b:2c:3d:4e:5f mismatch"
check "a detail shaped like a MAC address: masked by the client, the run arrives with its log (end SENT)" \
	sh -c "[ '$(val $OUT/macd.txt end.rc)' = 1 ] && '$PY' '$HERE/check.py' '$INBOX' logtest '$(val $OUT/macd.txt run)' --meta 'detail=FAIL crc xx:xx:xx:xx:xx:xx mismatch' --contains 'logtest.txt=FAIL END (test)' --done"

printf 'an old log written while devlog was off (no tag)\n' > "$D/logtest.txt"
drive prev1 run --lines 3
check "an untagged previous log (devlog was off then) is sent with the run as logtest.prev.txt" \
	chk "$INBOX" logtest "$(val $OUT/prev1.txt run)" --only logtest.txt,logtest.prev.txt --contains "logtest.prev.txt=written while devlog was off" --done

echo "== 4. text guarantee and the size cap"
drive nul1 run --nul --lines 5
R=$(val $OUT/nul1.txt run)
check "NUL / control bytes: sent as '?', receiver accepts (sanitized=18 recorded)" \
	chk "$INBOX" logtest "$R" --contains "logtest.txt=?????????????????? after a zero-filled gap" --info logtest.txt.sanitized=18 --done
drive big1 run --big 400000 --lines 3
R=$(val $OUT/big1.txt run)
SZ=$(stat -f %z "$D/logtest.txt")
check "over max_file (256 KiB): the LAST 262144 bytes go, offset/file_size recorded" \
	chk "$INBOX" logtest "$R" --info logtest.txt.bytes=262144 --contains "logtest.txt=BIG-END" --lacks "logtest.txt=BEGIN devlog_cli" --done
check "  (offset = file_size - 262144 at upload time)" sh -c "'$PY' -c \"
import glob,json,sys; m=json.load(open(glob.glob('$INBOX/*/logtest/$R/manifest.json')[0]))['files']['logtest.txt']
sys.exit(0 if int(m['offset'])==int(m['file_size'])-262144 and int(m['file_size'])>262144 else 1)\""

echo "== 5. crash, abort note, next launch"
drive crash1 crash --lines 30
RC=$(val $OUT/crash1.txt run)
check "crash: process exited 3, run not filed, marker kept" sh -c "[ '$(val $OUT/crash1.txt exit)' = 3 ] && [ -f '$D/devlog/pending/$RC.run' ] && ! ls -d $INBOX/*/logtest/$RC >/dev/null 2>&1"
drive next1 run --lines 5
RN=$(val $OUT/next1.txt run)
check "next launch (log renamed to .prev): the crashed run arrives, outcome=crash, late=1, its own log" \
	chk "$INBOX" logtest "$RC" --meta outcome=crash --meta late=1 --contains "logtest.txt=about to crash" \
	--contains "logtest.txt=DEVLOG run=$RC" --lacks "logtest.txt=DEVLOG run=$RN" --done
check "next launch: its own run too, and nothing left pending" sh -c "'$PY' '$HERE/check.py' '$INBOX' logtest '$RN' --meta outcome=ok --done && [ $(pending_count) = 0 ]"
drive note1 note --lines 5
RA=$(val $OUT/note1.txt run)
drive next2 run --lines 5
check "devlog_note(ABORT) before dying: next launch files outcome=abort + its detail" \
	chk "$INBOX" logtest "$RA" --meta outcome=abort --meta "detail=runtime fatal error (test)" --meta late=1 --done
drive crash2 crash --app probe --lines 5
RP=$(val $OUT/crash2.txt run)
drive next3 run --lines 5
check "another app's crash (probe) is sent by the next app that starts (logtest), with probe's log" \
	chk "$INBOX" probe "$RP" --meta outcome=crash --contains "probe.txt=DEVLOG run=$RP" --done

drive crash3 crash --lines 5
RL=$(val $OUT/crash3.txt run)
printf 'enabled=0\ntoken=%s\n' "$TOKEN" > "$D/devlog.txt"
drive off3 run --lines 2; drive off4 run --lines 2        # two launches with devlog off: both log names now newer
conf "discover=127.0.0.1" "discovery_port=$UDP"
drive next4 run --lines 2
check "a crashed run whose log was rewritten meanwhile (devlog off): sent without a wrong log, the detail says so" \
	chk "$INBOX" logtest "$RL" --only "" --meta outcome=crash --meta "detail=no devlog_end: crash, closed from the LiveArea, or power loss; logtest.txt: this run's tag is not in the log (rewritten while devlog was off?)" --done
drive pre1 crash --pre 40000 --lines 5
RQ=$(val $OUT/pre1.txt run)
drive pre2 run --lines 2
check "40 KB logged before devlog_begin, then a crash: the next launch finds the log by the tag's offset" \
	chk "$INBOX" logtest "$RQ" --meta outcome=crash --contains "logtest.txt=DEVLOG run=$RQ" --contains "logtest.txt=about to crash" --done

echo "== 6. no receiver: the app is never held more than 2 s"
rx_stop
drive down1 run
RD1=$(val $OUT/down1.txt run)
check "receiver down (discovery): end PENDING (2) in <= 2000 ms (begin $(val $OUT/down1.txt begin.ms), end $(val $OUT/down1.txt end.ms) ms)" \
	sh -c "[ '$(val $OUT/down1.txt end.rc)' = 2 ] && [ $(val $OUT/down1.txt end.ms) -le 2000 ] && [ $(val $OUT/down1.txt begin.ms) -le 2000 ]"
check "receiver down: run kept (marker + copy of its log)" sh -c "[ -f '$D/devlog/pending/$RD1.run' ] && [ -f '$D/devlog/pending/$RD1/logtest.txt' ] && grep -q '^state=copied' '$D/devlog/pending/$RD1.run'"
conf "server=127.0.0.1:$TCP"
drive down2 run
check "receiver down (server= closed port, backoff + discovery): begin $(val $OUT/down2.txt begin.ms) / end $(val $OUT/down2.txt end.ms) ms <= 2000" \
	sh -c "[ '$(val $OUT/down2.txt end.rc)' = 2 ] && [ $(val $OUT/down2.txt end.ms) -le 2000 ] && [ $(val $OUT/down2.txt begin.ms) -le 2000 ]"
"$PY" "$HERE/fakes.py" blackhole "$OUT/bh" & PIDS+=($!)
i=0; while [ ! -s "$OUT/bh" ] && [ $i -lt 100 ]; do sleep 0.05; i=$((i + 1)); done
read -r BHT _ < "$OUT/bh"
conf "server=127.0.0.1:$BHT"
drive down3 run
check "hung receiver (accepts, never answers): begin $(val $OUT/down3.txt begin.ms) / end $(val $OUT/down3.txt end.ms) ms <= 2000, PENDING" \
	sh -c "[ '$(val $OUT/down3.txt end.rc)' = 2 ] && [ $(val $OUT/down3.txt end.ms) -le 2000 ] && [ $(val $OUT/down3.txt begin.ms) -le 2000 ]"
"$PY" "$HERE/fakes.py" rogue "$OUT/rogue" & PIDS+=($!)
i=0; while [ ! -s "$OUT/rogue" ] && [ $i -lt 100 ]; do sleep 0.05; i=$((i + 1)); done
read -r RGT RGU < "$OUT/rogue"
rm -f "$D/devlog/server.txt"
conf "discover=127.0.0.1" "discovery_port=$RGU"
drive rogue1 run
NUDP=$(grep -c '^UDP' "$OUT/rogue.hits" 2>/dev/null); NTCP=$(grep -c '^TCP' "$OUT/rogue.hits" 2>/dev/null)
check "impostor answering discovery with a bad proof: ignored (it got ${NUDP:-0} DISCOVERs and ${NTCP:-0} TCP connections)" \
	sh -c "[ '$(val $OUT/rogue1.txt end.reached)' = 0 ] && [ '${NUDP:-0}' -ge 1 ] && [ '${NTCP:-0}' = 0 ]"
check "  and the run stayed within 2 s (end $(val $OUT/rogue1.txt end.ms) ms)" test "$(val $OUT/rogue1.txt end.ms)" -le 2000
conf "server=127.0.0.1:$TCP"
drive down4 run --big 8000000 --lines 2
RD4=$(val $OUT/down4.txt run)
KSZ=$(stat -f %z "$D/devlog/pending/$RD4/logtest.txt" 2>/dev/null || echo 0)
check "receiver down, 8 MB log: end $(val $OUT/down4.txt end.ms) ms (wall $(val $OUT/down4.txt end_wall_ms)) <= 2000 incl. the copy; kept tail $KSZ <= 1 MiB" \
	sh -c "[ $(val $OUT/down4.txt end.ms) -le 2000 ] && [ $KSZ -gt 0 ] && [ $KSZ -le 1048576 ] && [ $(val $OUT/down4.txt end.ms) -ge \$(( $(val $OUT/down4.txt end_wall_ms) - 5 )) ]"
drive bigc crash --big 8000000 --lines 2
drive bign run --lines 2
check "next launch adopts a crashed 8 MB log: begin $(val $OUT/bign.txt begin.ms) ms (wall $(val $OUT/bign.txt begin_wall_ms)) counts the copy, <= 2000" \
	sh -c "[ $(val $OUT/bign.txt begin.ms) -le 2000 ] && [ $(val $OUT/bign.txt begin.ms) -ge \$(( $(val $OUT/bign.txt begin_wall_ms) - 5 )) ] && [ \$(stat -f %z '$D/devlog/pending/$(val $OUT/bigc.txt run)/logtest.prev.txt' 2>/dev/null || stat -f %z '$D/devlog/pending/$(val $OUT/bigc.txt run)/logtest.txt') -le 1048576 ]"
NPEND=$(pending_count)
check "the 7 runs made while no receiver answered are kept on the device ($NPEND)" test "$NPEND" = 7

echo "== 7. receiver back: the kept runs go out"
rx_start $TCP $UDP --max-file 262144
conf "discover=127.0.0.1" "discovery_port=$UDP"
drive flush1 flush
check "flush_app: SENT (1), $(val $OUT/flush1.txt flush.runs_sent) kept runs sent, nothing left" \
	sh -c "[ '$(val $OUT/flush1.txt flush.rc)' = 1 ] && [ $(pending_count) = 0 ]"
check "the run kept while the receiver was down arrives (late=1, attempt counted from the marker)" \
	chk "$INBOX" logtest "$RD1" --meta late=1 --meta outcome=ok --contains "logtest.txt=DEVLOG run=$RD1" --done

echo "== 7b. a receiver that stalls mid-upload; an impostor at the cached address"
"$PY" "$HERE/fakes.py" stall "$OUT/stall" "$C/token" & PIDS+=($!)
i=0; while [ ! -s "$OUT/stall" ] && [ $i -lt 100 ]; do sleep 0.05; i=$((i + 1)); done
read -r STT _ < "$OUT/stall"
conf "server=127.0.0.1:$STT"
drive stall1 run --big 8000000 --lines 2
RS1=$(val $OUT/stall1.txt run)
check "stalled upload: end gives up after 1.5 s without progress: $(val $OUT/stall1.txt end.ms) ms <= 4000, PENDING, kept" \
	sh -c "[ '$(val $OUT/stall1.txt end.rc)' = 2 ] && [ $(val $OUT/stall1.txt end.ms) -le 4000 ] && [ -f '$D/devlog/pending/$RS1.run' ]"
check "  a stall with the receiver reached counts as a failed attempt (attempt=1 in the marker)" grep -q '^attempt=1$' "$D/devlog/pending/$RS1.run"
drive stall2 run --lines 2
check "next launch: begin sends the kept run into the stall and gives up within its budget: $(val $OUT/stall2.txt begin.ms) ms <= 3000 (end $(val $OUT/stall2.txt end.ms) ms)" \
	sh -c "[ $(val $OUT/stall2.txt begin.ms) -le 3000 ] && [ $(val $OUT/stall2.txt end.ms) -le 4000 ]"
"$PY" "$HERE/fakes.py" impostor "$OUT/imp" "$C/token" & PIDS+=($!)
i=0; while [ ! -s "$OUT/imp" ] && [ $i -lt 100 ]; do sleep 0.05; i=$((i + 1)); done
read -r IMT _ < "$OUT/imp"
echo "127.0.0.1:$IMT" > "$D/devlog/server.txt"
conf "discover=127.0.0.1" "discovery_port=$UDP"
drive imp1 run --lines 2
NIMP=$(grep -c '^TCP' "$OUT/imp.hits" 2>/dev/null)
check "impostor at the cached address: answered ping without a proof -> ignored, rediscovered, everything went to the real receiver" \
	sh -c "[ '$(val $OUT/imp1.txt end.rc)' = 1 ] && [ '$(val $OUT/imp1.txt end.server)' = '127.0.0.1:$TCP' ] && [ $(pending_count) = 0 ] && grep -q '^127.0.0.1:$TCP\$' '$D/devlog/server.txt'"
check "  the impostor saw only ping requests ($NIMP), never a token, never a log" \
	sh -c "[ '${NIMP:-0}' -ge 1 ] && ! grep -q 'token_seen=1' '$OUT/imp.hits' && ! grep -q 'POST' '$OUT/imp.hits'"
check "  the stalled runs arrived later (late=1)" chk "$INBOX" logtest "$RS1" --meta late=1 --contains "logtest.txt=BIG-END" --done

echo "== 8. wrong token"
GOOD=$TOKEN; TOKEN=$(printf '%064d' 7)
conf "server=127.0.0.1:$TCP"
drive badtok run
RB=$(val $OUT/badtok.txt run)
check "wrong token: PENDING, 401 seen, nothing filed, the why line says so" \
	sh -c "[ '$(val $OUT/badtok.txt end.rc)' = 2 ] && grep -q ' 401 ' '$C/receiver.log' && ! ls -d $INBOX/*/logtest/$RB >/dev/null 2>&1 && grep -q 'refused the token' '$OUT/badtok.txt'"
TOKEN=$GOOD; conf "server=127.0.0.1:$TCP"
drive fixed flush
check "token fixed: the kept run goes out" chk "$INBOX" logtest "$RB" --done
rx_stop
echo "== 9. the receiver refuses the meta: reduced retry, then a clean rejection"
rx_start $TCP $UDP --max-file 262144 --max-meta 220
conf "server=127.0.0.1:$TCP"
drive red1 run --lines 3
RR=$(val $OUT/red1.txt run)
check "full meta refused (413): retried with the client's own keys, run SENT with its log (meta=reduced, no detail)" \
	sh -c "[ '$(val $OUT/red1.txt end.rc)' = 1 ] && '$PY' '$HERE/check.py' '$INBOX' logtest '$RR' --meta meta=reduced --meta detail=None --meta outcome=ok --contains 'logtest.txt=PASS END' --done && grep -q 'retrying with the client' '$D/logtest.txt'"
rx_stop
rx_start $TCP $UDP --max-file 262144 --max-meta 100
drive rej1 run --lines 3
check "even the reduced meta refused: end REJECTED (3), why says so, the run is not kept" \
	sh -c "[ '$(val $OUT/rej1.txt end.rc)' = 3 ] && [ '$(val $OUT/rej1.txt end.runs_rejected)' = 1 ] && grep -q 'end.why=run rejected by the receiver' '$OUT/rej1.txt' && [ ! -f '$D/devlog/pending/$(val $OUT/rej1.txt run).run' ]"
rx_stop
check "receiver log: no token in it, no tracebacks, no escape bytes" sh -c "! grep -q '$TOKEN' '$C/receiver.log' && ! grep -q Traceback '$C/receiver.log' && ! LC_ALL=C grep -q \"\$(printf '\\033')\" '$C/receiver.log'"

echo "== 11. revision 3: modes, the background worker, clear, retention, buffered logs, the Codex binding"
# a fresh device folder; the -O2 build (no sanitizers) so the app-visible times are what an app would see.
# Every devlog.txt here names discover=127.0.0.1 + a local port: no test datagram ever leaves this Mac.
D=$OUT/device3; mkdir -p "$D"
CLI=$BIN/devlog_cli_o2
drive3() { local tag=$1; shift; "$CLI" "$D" "$@" > "$OUT/$tag.txt" 2> "$OUT/$tag.err"; echo "exit=$?" >> "$OUT/$tag.txt"; }
conf3() { { echo "enabled=1"; echo "token=$TOKEN"; for l in "$@"; do echo "$l"; done; } > "$D/devlog.txt"; }
v() { val "$OUT/$1.txt" "$2"; }
rx_start $TCP $UDP
conf3 "mode=off" "discover=127.0.0.1" "discovery_port=$UDP"
drive3 m_off run --lines 3
check "mode=off: DISABLED (0), the why line says mode=off, nothing created" \
	sh -c "[ '$(v m_off end.rc)' = 0 ] && grep -q '^begin.call_why=devlog.txt: mode=off' '$OUT/m_off.txt' && [ ! -e '$D/devlog' ]"
printf '\357\273\277enabled=1\ntoken=%s\ndiscover=127.0.0.1\ndiscovery_port=%s\n' "$TOKEN" "$UDP" > "$D/devlog.txt"
drive3 bom run --lines 2 --answer no
check "devlog.txt saved with a UTF-8 BOM (some editors add one): still read (end ASK, not DISABLED); answered no, nothing kept" \
	sh -c "[ '$(v bom end.rc)' = 5 ] && [ '$(v bom ask.rc)' = 6 ] && [ \$(ls '$D/devlog/pending' | grep -c '\.run\$') = 0 ]"

conf3 "discover=127.0.0.1" "discovery_port=$UDP"
drive3 ask1 run --lines 5
RA1=$(v ask1 run)
check "no mode= line = ask: end ASK (5), mode 1, no network (net -100), the run kept as 'ended', nothing in the inbox" \
	sh -c "[ '$(v ask1 end.rc)' = 5 ] && [ '$(v ask1 end.mode)' = 1 ] && [ '$(v ask1 end.net)' = -100 ] && grep -q '^state=ended' '$D/devlog/pending/$RA1.run' && ! ls -d $INBOX/*/logtest/$RA1 >/dev/null 2>&1"
check "  the prompt went through the app's log; not answered: ASK, the run stays" \
	sh -c "[ '$(v ask1 ask.should)' = 1 ] && [ '$(v ask1 ask.rc)' = 5 ] && grep -q 'DEVLOG ask: send the logs of 1 run' '$D/logtest.txt' && [ -f '$D/devlog/pending/$RA1.run' ]"
drive3 ask2 run --lines 5 --answer yes
RA2=$(v ask2 run)
check "next launch: begin copies the unanswered run without sending (ASK); the end asks about 2 runs; yes: both arrive" \
	sh -c "[ '$(v ask2 begin.call_rc)' = 5 ] && [ '$(v ask2 ask.should)' = 2 ] && [ '$(v ask2 ask.rc)' = 1 ] && '$PY' '$HERE/check.py' '$INBOX' logtest '$RA1' --meta outcome=ok --meta late=1 --contains 'logtest.txt=DEVLOG run=$RA1' --done && '$PY' '$HERE/check.py' '$INBOX' logtest '$RA2' --meta late=0 --only logtest.txt --done && [ \$(ls '$D/devlog/pending' | grep -c '\.run\$') = 0 ]"
drive3 ask3 run --lines 5 --answer no
RA3=$(v ask3 run)
check "answer no: DROPPED (6), nothing sent, nothing kept" \
	sh -c "[ '$(v ask3 ask.rc)' = 6 ] && ! ls -d $INBOX/*/logtest/$RA3 >/dev/null 2>&1 && [ ! -f '$D/devlog/pending/$RA3.run' ]"
drive3 ask4 run --lines 5 --answer yes
check "  and the declined run's log is not sent later as the next run's logtest.prev.txt either" chk "$INBOX" logtest "$(v ask4 run)" --only logtest.txt --done
drive3 ask5 crash --lines 5
RA5=$(v ask5 run)
drive3 ask6 run --lines 3 --ask-launch --answer yes
check "asking at launch about a crashed run: should_ask 1, yes: it arrives as crash; the launch's own run later too" \
	sh -c "[ '$(v ask6 asklaunch.should)' = 1 ] && [ '$(v ask6 asklaunch.rc)' = 1 ] && '$PY' '$HERE/check.py' '$INBOX' logtest '$RA5' --meta outcome=crash --contains 'logtest.txt=about to crash' --done && '$PY' '$HERE/check.py' '$INBOX' logtest '$(v ask6 run)' --done"
# declined in one app, then the logs cleared from another app (that clear removes only its own logs), then the first
# app again with a yes: its *.prev is the declined run's log and must stay on the console
drive3 dk1 run --app probe --lines 3 --answer no
RDK=$(v dk1 run)
drive3 dk2 run --lines 2 --clear-mid --answer yes
drive3 dk3 run --app probe --lines 3 --answer yes
check "declined in probe, logs cleared from logtest, probe again + yes: only probe.txt goes, never the declined run's log (probe.prev.txt)" \
	sh -c "[ '$(v dk1 ask.rc)' = 6 ] && [ '$(v dk2 ask.rc)' = 1 ] && [ '$(v dk3 ask.rc)' = 1 ] && grep -q 'DEVLOG run=$RDK' '$D/probe.prev.txt' && '$PY' '$HERE/check.py' '$INBOX' probe '$(v dk3 run)' --only probe.txt --done && '$PY' '$HERE/check.py' '$INBOX' grep 'DEVLOG run=$RDK'"

rx_stop
conf3 "mode=auto" "discover=127.0.0.1" "discovery_port=$UDP"
drive3 bg1 run --lines 3
drive3 bg2 run --lines 3 --async
check "app-visible cost: devlog_begin with a kept run and no receiver returns STARTED in $(v bg2 begin_call_us) us (< 1000); the worker gave up in $(v bg2 begin.ms) ms (<= 2000), PENDING" \
	sh -c "[ '$(v bg2 begin.call_rc)' = 4 ] && [ $(v bg2 begin_call_us) -lt 1000 ] && [ '$(v bg2 begin.rc)' = 2 ] && [ $(v bg2 begin.ms) -le 2000 ]"
check "app-visible cost: devlog_end_async returns STARTED in $(v bg2 end_call_us) us (< 1000); devlog_wait: PENDING after $(v bg2 end.ms) ms (<= 2000)" \
	sh -c "[ '$(v bg2 end.call_rc)' = 4 ] && [ $(v bg2 end_call_us) -lt 1000 ] && [ '$(v bg2 end.rc)' = 2 ] && [ $(v bg2 end.ms) -le 2000 ]"
check "the worker's lines reached the app's log on the app's thread (launch send + end)" \
	sh -c "grep -q '^DEVLOG launch send: no receiver' '$D/logtest.txt' && grep -q '^DEVLOG end (ok): no receiver' '$D/logtest.txt'"
check "retention: 2 runs kept (current + previous), not more" test "$(ls "$D/devlog/pending" | grep -c '\.run$')" = 2
rx_start $TCP $UDP
drive3 bg3 run --lines 3
check "receiver back: begin STARTED in $(v bg3 begin_call_us) us; at launch the older kept run was dropped (retention) and the newer one sent in the background" \
	sh -c "[ '$(v bg3 begin.call_rc)' = 4 ] && [ $(v bg3 begin_call_us) -lt 1000 ] && [ '$(v bg3 begin.rc)' = 1 ] && [ '$(v bg3 begin.runs_sent)' = 1 ] && grep -q 'dropped pending run $(v bg1 run) (too many pending runs)' '$D/logtest.txt' && '$PY' '$HERE/check.py' '$INBOX' logtest '$(v bg2 run)' --meta late=1 --done"
conf3 "mode=auto" "server=127.0.0.1:$BHT" "discover=127.0.0.1" "discovery_port=9"
drive3 cx1 run --lines 2
drive3 cx2 run --lines 2 --no-wait --async
check "devlog_end_async while the launch send hangs on a dead receiver: cancels it, returns in $(v cx2 end_call_us) us (< 100 ms); end PENDING in $(v cx2 end.ms) ms" \
	sh -c "[ '$(v cx2 begin.call_rc)' = 4 ] && [ '$(v cx2 end.call_rc)' = 4 ] && [ $(v cx2 end_call_us) -lt 100000 ] && [ '$(v cx2 end.rc)' = 2 ] && [ $(v cx2 end.ms) -le 2200 ]"

drive3 clr clear
check "devlog_clear (before begin): DROPPED (6): 2 kept runs, sent list, cached address gone, logtest.prev.txt gone, logtest.txt emptied (only the clear's own line left)" \
	sh -c "[ '$(v clr clear.rc)' = 6 ] && grep -q '^clear.why=cleared: 2 kept run(s), 2 log(s)' '$OUT/clr.txt' && [ ! -e '$D/devlog' ] && [ \$(wc -l < '$D/logtest.txt') = 1 ] && grep -q '^DEVLOG cleared: 2 kept run' '$D/logtest.txt' && [ ! -e '$D/logtest.prev.txt' ]"
drive3 cl1 crash --lines 2
conf3 "mode=auto" "server=127.0.0.1:$BHT" "discover=127.0.0.1" "discovery_port=9" "clear=1"
cp "$D/devlog.txt" "$OUT/devlog-before-clear.txt"
drive3 cl2 crash --lines 2
check "clear=1: that start wiped the crashed run and the previous log first (nothing kept: begin SENT), and logged it" \
	sh -c "[ '$(v cl2 begin.call_rc)' = 1 ] && [ ! -f '$D/devlog/pending/$(v cl1 run).run' ] && [ ! -e '$D/logtest.prev.txt' ] && grep -q 'DEVLOG clear=1: removed 1 kept run(s), 1 log(s)' '$D/logtest.txt'"
check "  devlog.txt now says clear=0 and is otherwise byte-identical (token kept)" \
	sh -c "sed 's/^clear=1\$/clear=0/' '$OUT/devlog-before-clear.txt' | cmp -s - '$D/devlog.txt' && grep -q '^clear=0\$' '$D/devlog.txt'"
conf3 "mode=auto" "discover=127.0.0.1" "discovery_port=$UDP"
drive3 cm run --lines 10 --clear-mid
check "devlog_clear during a run: the log restarts with the run's tag; the run is sent without the lines before the clear" \
	chk "$INBOX" logtest "$(v cm run)" --contains "logtest.txt=log cleared" --contains "logtest.txt=AFTER CLEAR" --lacks "logtest.txt=P line 0:" --only logtest.txt --done

drive3 bf1 crash --lines 100 --buffered
drive3 bf2 run --lines 2
check "buffered log + crash: the tag was flushed at devlog_begin, so the crash is picked up; lines still in the buffer are lost (the release trade-off)" \
	chk "$INBOX" logtest "$(v bf1 run)" --meta outcome=crash --contains "logtest.txt=DEVLOG run=$(v bf1 run)" --contains "logtest.txt=P line 99" --lacks "logtest.txt=UNFLUSHED" --done
drive3 bf3 note --lines 5 --buffered
drive3 bf4 run --lines 2 --logfile
check "buffered log + devlog_note: the note flushed the buffer (the fatal line arrives); write-through log run sent whole" \
	sh -c "'$PY' '$HERE/check.py' '$INBOX' logtest '$(v bf3 run)' --meta outcome=abort --contains 'logtest.txt=FAIL fatal error (test)' --done && '$PY' '$HERE/check.py' '$INBOX' logtest '$(v bf4 run)' --contains 'logtest.txt=PASS END' --done"

# a launch copy cut short: the run ends while the launch job copies a crashed run's 8 MB log + 8 MB extra
mkdir -p "$D/runtime/logs"
"$PY" -c "import sys; sys.stdout.write(('EXTRA filler filler filler filler filler filler filler filler\n' * 130000) + 'EXTRA-END\n')" > "$D/runtime/logs/big.log"
drive3 cc1 crash --big 8000000 --extra runtime/logs/big.log --lines 2
RCC=$(v cc1 run)
drive3 cc2 run --lines 2 --no-wait --async
CUT=$(grep -c 'DEVLOG launch send: stopped' "$D/logtest.txt")
check "devlog_end_async while the launch job copies a crashed run (8 MB log + 8 MB extra): returns in $(v cc2 end_call_us) us (< 10000; was 24-27 ms when the copy had to finish; launch copy cut short: $CUT)" \
	sh -c "[ '$(v cc2 end.call_rc)' = 4 ] && [ $(v cc2 end_call_us) -lt 10000 ]"
check "  the cut-short copy is made by the run-end job instead: the crashed run arrives (crash, both tails), nothing left pending" \
	sh -c "[ '$(v cc2 end.rc)' = 1 ] && '$PY' '$HERE/check.py' '$INBOX' logtest '$RCC' --meta outcome=crash --only logtest.txt,big.log --contains 'logtest.txt=BIG-END' --contains 'big.log=EXTRA-END' --done && [ \$(ls '$D/devlog/pending' | grep -c '\.run\$') = 0 ]"
rm -f "$D/runtime/logs/big.log"

# the Codex binding (PROTOCOL.md §10), played by a second receiver started with --base /vita/devlog
C2=$OUT/rx2; mkdir -p "$C2"; cp "$C/token" "$C2/token"; chmod 600 "$C2/token"; INBOX2=$C2/inbox
rm -f "$C2/receiver.pid"
"$PY" "$RX" --root "$C2" serve --bind 127.0.0.1 --port 0 --udp-port 0 --pidfile "$C2/receiver.pid" --min-free 1M \
	--rate 1000 --base /vita/devlog >> "$C2/receiver.log" 2>&1 &
RX2_PID=$!; PIDS+=($RX2_PID)
i=0; while [ ! -s "$C2/receiver.pid" ] && [ $i -lt 100 ]; do sleep 0.05; i=$((i + 1)); done
read -r _ TCP2 UDP2 < "$C2/receiver.pid"
rm -f "$D/devlog/server.txt"
conf3 "mode=auto" "discover=127.0.0.1" "discovery_port=$UDP2"
drive3 cdx run --lines 3
check "Codex binding: found by discovery (HERE base=/vita/devlog), sent to 127.0.0.1:$TCP2 through /vita/devlog/v1/upload?op=..." \
	sh -c "[ '$(v cdx end.rc)' = 1 ] && [ '$(v cdx end.server)' = '127.0.0.1:$TCP2' ] && '$PY' '$HERE/check.py' '$INBOX2' logtest '$(v cdx run)' --meta client=devlog-c/3 --contains 'logtest.txt=PASS END' --done && grep -q 'POST /vita/devlog/v1/upload?op=file&app=logtest&run=$(v cdx run)&name=logtest.txt -> 200' '$C2/receiver.log'"
check "  the cached address carries the base (server.txt = 127.0.0.1:$TCP2/vita/devlog)" grep -qx "127.0.0.1:$TCP2/vita/devlog" "$D/devlog/server.txt"
"$PY" "$HERE/fakes.py" relay "$OUT/relay" "$C/token" "$TCP" "$TCP2" /vita/devlog & PIDS+=($!)
i=0; while [ ! -s "$OUT/relay" ] && [ $i -lt 100 ]; do sleep 0.05; i=$((i + 1)); done
read -r _ RLU < "$OUT/relay"
rm -f "$D/devlog/server.txt"
conf3 "mode=auto" "discover=127.0.0.1" "discovery_port=$RLU"
drive3 pref run --lines 3
check "both answer discovery (the standalone receiver first, the Codex server 30 ms later): the Codex server gets the run" \
	sh -c "[ '$(v pref end.rc)' = 1 ] && [ '$(v pref end.server)' = '127.0.0.1:$TCP2' ] && '$PY' '$HERE/check.py' '$INBOX2' logtest '$(v pref run)' --done"
kill -TERM $RX2_PID 2>/dev/null; wait $RX2_PID 2>/dev/null
drive3 fb run --lines 3
check "Codex server gone: the cached Codex address fails, discovery offers both, the standalone receiver (fallback) gets the run" \
	sh -c "[ '$(v fb end.rc)' = 1 ] && [ '$(v fb end.server)' = '127.0.0.1:$TCP' ] && '$PY' '$HERE/check.py' '$INBOX' logtest '$(v fb run)' --done"
check "  the fallback is now the cached address (server.txt = 127.0.0.1:$TCP, no base)" grep -qx "127.0.0.1:$TCP" "$D/devlog/server.txt"
rm -f "$C2/receiver.pid"
"$PY" "$RX" --root "$C2" serve --bind 127.0.0.1 --port $TCP2 --udp-port 0 --pidfile "$C2/receiver.pid" --min-free 1M \
	--rate 1000 --base /vita/devlog >> "$C2/receiver.log" 2>&1 &
RX2_PID=$!; PIDS+=($RX2_PID)
i=0; while [ ! -s "$C2/receiver.pid" ] && [ $i -lt 100 ]; do sleep 0.05; i=$((i + 1)); done
drive3 back run --lines 3
check "Codex server back while the fallback is cached: the short discovery prefers it, it gets the run ($(v back end.ms) ms), the cache names it again" \
	sh -c "[ '$(v back end.rc)' = 1 ] && [ '$(v back end.server)' = '127.0.0.1:$TCP2' ] && '$PY' '$HERE/check.py' '$INBOX2' logtest '$(v back run)' --done && grep -qx '127.0.0.1:$TCP2/vita/devlog' '$D/devlog/server.txt'"
kill -TERM $RX2_PID 2>/dev/null; wait $RX2_PID 2>/dev/null
"$PY" "$HERE/fakes.py" wall "$OUT/wall" & PIDS+=($!)
i=0; while [ ! -s "$OUT/wall" ] && [ $i -lt 100 ]; do sleep 0.05; i=$((i + 1)); done
read -r WLT _ < "$OUT/wall"
conf3 "mode=auto" "server=127.0.0.1:$WLT/vita/devlog" "discover=127.0.0.1" "discovery_port=$UDP"
drive3 wl run --lines 3
check "a login wall (401 + JSON) at the configured address is not taken for a refused token: next address, sent" \
	sh -c "[ '$(v wl end.rc)' = 1 ] && [ '$(v wl end.server)' = '127.0.0.1:$TCP' ] && ! grep -q 'refused the token' '$OUT/wl.txt'"
rx_stop
check "receiver logs: no token in them" sh -c "! grep -q '$TOKEN' '$C/receiver.log' && ! grep -q '$TOKEN' '$C2/receiver.log'"

echo "== 12. ThreadSanitizer: the worker and the app's thread at the same time"
if $CC -std=gnu11 -O1 -g -Wall -Wextra -Werror -Wno-unused-parameter -fsanitize=thread "$HERE/devlog_cli.c" -o "$BIN/devlog_cli_tsan" > "$OUT/tsan-build.txt" 2>&1; then
	ok "devlog_cli builds with -fsanitize=thread"
	D=$OUT/device4; mkdir -p "$D"; CLI=$BIN/devlog_cli_tsan
	rx_start $TCP $UDP
	conf3 "mode=auto" "discover=127.0.0.1" "discovery_port=$UDP"
	drive3 ts1 crash --lines 20
	drive3 ts2 run --lines 400 --no-wait --async --logfile      # the launch send runs while the app logs 400 lines
	drive3 ts3 crash --lines 5
	conf3 "mode=auto" "server=127.0.0.1:$BHT" "discover=127.0.0.1" "discovery_port=9"
	drive3 ts4 run --lines 50 --no-wait --async                  # cancelled launch send, end on the worker
	conf3 "discover=127.0.0.1" "discovery_port=$UDP"
	drive3 ts5 run --lines 20 --ask-launch --answer yes          # ask at launch + at the end, sends on the worker
	rx_stop
	check "TSan: no data race reported in 5 runs (launch send during logging, cancel, ask + send)" \
		sh -c "! grep -l 'ThreadSanitizer' $OUT/ts1.err $OUT/ts2.err $OUT/ts3.err $OUT/ts4.err $OUT/ts5.err"
	check "  and the runs behaved: ts1 sent by ts2's launch, ts2 sent, ts5 sent after the yes" \
		sh -c "[ '$(v ts2 end.rc)' = 1 ] && '$PY' '$HERE/check.py' '$INBOX' logtest '$(v ts1 run)' --meta outcome=crash --done && [ '$(v ts5 ask.rc)' = 1 ] && '$PY' '$HERE/check.py' '$INBOX' logtest '$(v ts5 run)' --done"
else
	bad "devlog_cli does not build with -fsanitize=thread"; sed 's/^/        /' "$OUT/tsan-build.txt" | head
fi

echo "== 13. nothing left running"
cleanup
sleep 0.3
if pgrep -f "$OUT" > /dev/null 2>&1; then bad "a process of this test is still running"; pgrep -fl "$OUT"; else ok "no receiver / fake left"; fi
echo "== $PASS passed, $FAIL failed   ($OUT)"
[ "$FAIL" -eq 0 ]
