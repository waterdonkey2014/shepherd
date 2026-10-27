#!/bin/sh
# End-to-end test of "the Vita sends its logs back" on the Mac, with no Vita:
#   1. the receiver's protocol + hardening tests (test_receiver.py)
#   2. devlogctl.sh token/start/status/stop (+ the Python reference client, discovery included)
#   3. the C client (vita/devlog/, POSIX platform layer) built into e2e/mac_driver.c, against the receiver:
#      normal run, failing run, crash + next launch, server down (2 s budget) then flush, server= without discovery,
#      wrong token, disabled config, and the "logs only" rule (config, saves, owner folders, outside paths)
# Every receiver here binds 127.0.0.1 on free ports, and is stopped at the end (also on Ctrl-C / failure); every
# devlog.txt names discover=127.0.0.1, so no datagram is broadcast on the LAN.
# Writes only under ${SHEPHERD_WORK:-$HOME/shepherd-work}/devlog/e2e/<stamp>/ (or $SHPD_E2E_ROOT). Exit 0 = all passed.
#
#   tools/devlog-receiver/e2e_test.sh            PYTHON=/opt/homebrew/bin/python3 CC=clang also work
set -u
[ -d ${SHEPHERD_WORK:-$HOME/shepherd-work}/tmp ] && export TMPDIR=${SHEPHERD_WORK:-$HOME/shepherd-work}/tmp
HERE=$(cd "$(dirname "$0")" && pwd)
REPO=$(cd "$HERE/../.." && pwd)
DEVLOG="$REPO/vita/devlog"
PY=${PYTHON:-/usr/bin/python3}
[ -x "$PY" ] || PY=$(command -v python3)
CC=${CC:-cc}
chk() { "$PY" "$HERE/e2e/check_inbox.py" "$@"; }
CHKQ="'$PY' '$HERE/e2e/check_inbox.py'"   # the same, for use inside sh -c "..." strings
OUT=${SHPD_E2E_ROOT:-${SHEPHERD_WORK:-$HOME/shepherd-work}/devlog/e2e/$(date +%Y%m%d-%H%M%S)}
mkdir -p "$OUT"
PASS=0 FAIL=0 SKIP=0
RX_PID=""

ok()   { PASS=$((PASS + 1)); echo "  ok    $*"; }
bad()  { FAIL=$((FAIL + 1)); echo "  FAIL  $*"; }
skip() { SKIP=$((SKIP + 1)); echo "  skip  $*"; }
check() { # check <description> <command...>
	d=$1; shift
	if "$@" > "$OUT/last-check.txt" 2>&1; then ok "$d"; else bad "$d"; sed 's/^/        /' "$OUT/last-check.txt"; fi
}
val() { sed -n "s/^$2=//p" "$1" | tail -1; }   # val <file> <key>

rx_stop() {
	if [ -n "$RX_PID" ] && kill -0 "$RX_PID" 2>/dev/null; then
		kill -TERM "$RX_PID" 2>/dev/null
		i=0; while kill -0 "$RX_PID" 2>/dev/null && [ $i -lt 50 ]; do sleep 0.1; i=$((i + 1)); done
		kill -0 "$RX_PID" 2>/dev/null && kill -KILL "$RX_PID" 2>/dev/null
	fi
	RX_PID=""
}
rx_start() { # rx_start <root> [tcp udp]: receiver on 127.0.0.1; sets TCP / UDP
	rm -f "$1/receiver.pid"
	"$PY" "$HERE/receiver.py" --root "$1" serve --bind 127.0.0.1 --port "${2:-0}" --udp-port "${3:-0}" \
		--pidfile "$1/receiver.pid" --min-free 1M --rate 1000 >> "$1/receiver.log" 2>&1 &
	RX_PID=$!
	i=0; while [ ! -s "$1/receiver.pid" ] && [ $i -lt 100 ]; do sleep 0.05; i=$((i + 1)); done
	read -r _ TCP UDP < "$1/receiver.pid" || return 1
}
cleanup() {
	rx_stop
	SHPD_DEVLOG_ROOT="$OUT/ctl" "$HERE/devlogctl.sh" stop > /dev/null 2>&1
}
trap 'cleanup' EXIT
trap 'cleanup; exit 130' INT TERM

echo "devlog e2e -> $OUT"

# ------------------------------------------------------------------------------------------------ 1
echo "== 1. receiver protocol + hardening tests"
if SHPD_TEST_ROOT="$OUT/py" "$PY" "$HERE/test_receiver.py" > "$OUT/test_receiver.txt" 2>&1; then
	ok "test_receiver.py: $(grep -o 'Ran [0-9]* tests' "$OUT/test_receiver.txt")"
else
	bad "test_receiver.py (log: $OUT/test_receiver.txt)"; tail -n 25 "$OUT/test_receiver.txt" | sed 's/^/        /'
fi

# ------------------------------------------------------------------------------------------------ 2
echo "== 2. devlogctl.sh + Python reference client (loopback)"
CTL="$OUT/ctl"
SHPD_DEVLOG_ROOT="$CTL" "$HERE/devlogctl.sh" token > "$OUT/ctl-token.txt" 2>&1
check "token file is 0600" test "$(stat -f %Lp "$CTL/token")" = 600
check "vita-devlog.txt is 0600" test "$(stat -f %Lp "$CTL/vita-devlog.txt")" = 600
check "token not printed" sh -c "! grep -q \"\$(cat '$CTL/token')\" '$OUT/ctl-token.txt'"
SHPD_DEVLOG_ROOT="$CTL" "$HERE/devlogctl.sh" start --bind 127.0.0.1 --port 0 --udp-port 0 > "$OUT/ctl-start.txt" 2>&1
check "devlogctl start" grep -q "^started" "$OUT/ctl-start.txt"
check "devlogctl status: running, loopback only" sh -c "SHPD_DEVLOG_ROOT='$CTL' '$HERE/devlogctl.sh' status | grep -q 'TCP 127.0.0.1:'"
if read -r _ CTCP CUDP < "$CTL/receiver.pid" 2>/dev/null; then
	"$PY" "$HERE/shpdlog_client.py" --token-file "$CTL/token" --discover 127.0.0.1 --discovery-port "$CUDP" \
		--app logtest --meta outcome=ok --meta title_id=SHPD00014 "$OUT/ctl-start.txt" > "$OUT/ctl-client.txt" 2>&1
	check "python client: discovery + ping + meta + file + done" sh -c "grep -q '^discovered 127.0.0.1:$CTCP' '$OUT/ctl-client.txt' && grep -q '^file ctl-start.txt 200' '$OUT/ctl-client.txt' && grep -q '^done 200' '$OUT/ctl-client.txt'"
	PRUN=$(sed -n 's/^run //p' "$OUT/ctl-client.txt")
	check "python client run filed in the inbox" chk run "$CTL/inbox" logtest "$PRUN" --file "ctl-start.txt=$OUT/ctl-start.txt" --outcome ok --done
else
	bad "devlogctl: no pid file"
fi
SHPD_DEVLOG_ROOT="$CTL" "$HERE/devlogctl.sh" stop > /dev/null 2>&1
check "devlogctl stop" sh -c "SHPD_DEVLOG_ROOT='$CTL' '$HERE/devlogctl.sh' status | grep -q 'not running'"

# ------------------------------------------------------------------------------------------------ 3
echo "== 3. C client (vita/devlog POSIX build) -> receiver"
if [ ! -f "$DEVLOG/devlog_unity.c" ]; then
	skip "vita/devlog/devlog_unity.c does not exist yet: the C client part cannot run"
else
	mkdir -p "$OUT/bin"
	SAN=${SAN:--fsanitize=address,undefined}
	if $CC -std=gnu11 -O1 -g -Wall -Wextra $SAN -I"$DEVLOG" "$HERE/e2e/mac_driver.c" -o "$OUT/bin/mac_driver" \
		> "$OUT/build.txt" 2>&1; then
		ok "built mac_driver from vita/devlog (CC=$CC ${SAN:-no sanitizers})"
		[ -s "$OUT/build.txt" ] && sed 's/^/        /' "$OUT/build.txt" | head -20
	else
		bad "build mac_driver (log: $OUT/build.txt)"; sed 's/^/        /' "$OUT/build.txt" | head -30
	fi
fi
if [ -x "$OUT/bin/mac_driver" ]; then
	C="$OUT/c"; D="$C/device"; INBOX="$C/inbox"; DRV="$OUT/bin/mac_driver"
	mkdir -p "$D/runtime/logs" "$D/persistent/saves" "$D/managed"
	"$PY" "$HERE/receiver.py" --root "$C" gen-token > /dev/null
	TOKEN=$(cat "$C/token")
	export ASAN_OPTIONS=detect_leaks=0
	# files the client may send, and files it must never send
	printf 'engine log line 1\nengine log line 2\n' > "$D/runtime/logs/engine.log"
	printf 'SAVE-DATA-MARKER do not upload\n' > "$D/persistent/saves/slot_0.txt"
	printf 'OWNER-FOLDER-MARKER do not upload\n' > "$D/managed/notes.txt"
	printf 'ENV-MARKER=1\n' > "$D/env.txt"
	printf 'OUTSIDE-MARKER do not upload\n' > "$C/outside.txt"
	conf() { # conf <extra lines...>: writes the device's devlog.txt (mode=auto: these runs send without a prompt).
		# discover=127.0.0.1 + port 9 first (a later line of the call wins): no DISCOVER datagram is broadcast on the LAN
		{ echo "enabled=1"; echo "token=$TOKEN"; echo "mode=auto"; echo "discover=127.0.0.1"; echo "discovery_port=9"
		  for l in "$@"; do echo "$l"; done; } > "$D/devlog.txt"
	}
	drive() { # drive <tag> <mode>: run the driver, output in $OUT/<tag>.txt
		chk timed "$OUT/$1.txt" -- "$DRV" "$D" "$2" > /dev/null 2>&1
		return 0
	}

	rx_start "$C" || bad "receiver did not start"
	conf "discover=127.0.0.1" "discovery_port=$UDP"

	# 3a. a normal run, found by discovery
	drive c-run run
	R=$(val "$OUT/c-run.txt" run)
	check "3a run: end sent everything (rc=1, reached by discovery)" sh -c "[ '$(val "$OUT/c-run.txt" end.rc)' = 1 ] && [ '$(val "$OUT/c-run.txt" end.reached)' = 1 ] && [ '$(val "$OUT/c-run.txt" end.server)' = '127.0.0.1:$TCP' ]"
	check "3a run: the log (as it was at the upload) + engine.log arrive byte-identical, meta right, done" \
		chk run "$INBOX" logtest "$R" --prefix "logtest.txt=$D/logtest.txt" --file "engine.log=$D/runtime/logs/engine.log" \
		--outcome ok --meta title_id=SHPD00014 --meta platform=mac --meta app=logtest --meta build=e2e-mac-driver --done

	# 3b. a failing run; also lists extra files that must never leave the device
	drive c-unsafe unsafe
	R=$(val "$OUT/c-unsafe.txt" run)
	check "3b logs only: just the app's log and runtime/logs/engine.log arrive" \
		chk run "$INBOX" logtest "$R" --only logtest.txt,engine.log --done
	check "3b logs only: no token, config, save data, owner-folder or outside file anywhere in the inbox" sh -c \
		"! $CHKQ grep '$INBOX' '$TOKEN' && ! $CHKQ grep '$INBOX' SAVE-DATA-MARKER && ! $CHKQ grep '$INBOX' OWNER-FOLDER-MARKER && ! $CHKQ grep '$INBOX' ENV-MARKER && ! $CHKQ grep '$INBOX' OUTSIDE-MARKER"
	drive c-fail fail
	R=$(val "$OUT/c-fail.txt" run)
	check "3b fail: outcome=fail + detail" chk run "$INBOX" logtest "$R" --outcome fail --meta "detail=FAIL test: deliberate" --done

	# 3c. a crash (no devlog_end), then the next launch sends it
	drive c-crash crash
	RC=$(val "$OUT/c-crash.txt" run)
	check "3c crash: the crashed process exited 3 without sending its run" sh -c "[ '$(val "$OUT/c-crash.txt" exit)' = 3 ] && ! $CHKQ run '$INBOX' logtest '$RC' > /dev/null"
	drive c-next run
	RN=$(val "$OUT/c-next.txt" run)
	check "3c next launch: the crashed run arrives (outcome=crash, late=1)" \
		chk run "$INBOX" logtest "$RC" --outcome crash --meta late=1 --done
	check "3c next launch: it carries the log the crash left ('about to crash')" sh -c \
		"$CHKQ grep \"\$(ls -d '$INBOX'/*/logtest/'$RC')/files\" 'about to crash' > /dev/null"
	check "3c next launch: its own run arrives too" chk run "$INBOX" logtest "$RN" --outcome ok --done

	# 3d. receiver down: the app is not held up (2 s reach budget), the run stays pending, a later flush sends it
	rx_stop
	drive c-down run
	RD=$(val "$OUT/c-down.txt" run)
	W=$(val "$OUT/c-down.txt" wall_ms)
	check "3d server down: end returns PENDING (rc=2), the run is kept" sh -c "[ '$(val "$OUT/c-down.txt" end.rc)' = 2 ] && [ '$(val "$OUT/c-down.txt" end.runs_pending)' -ge 1 ]"
	check "3d server down: each call <= 2000 ms (begin $(val "$OUT/c-down.txt" begin.ms) ms, end $(val "$OUT/c-down.txt" end.ms) ms; process $W ms)" \
		sh -c "[ '$(val "$OUT/c-down.txt" begin.ms)' -le 2000 ] && [ '$(val "$OUT/c-down.txt" end.ms)' -le 2000 ]"
	rx_start "$C" "$TCP" "$UDP" || bad "receiver did not restart on the same ports"
	drive c-flush flush
	check "3d next launch (begin + flush + end): the pending run is sent by begin" sh -c "[ '$(val "$OUT/c-flush.txt" begin.rc)' = 1 ] && [ '$(val "$OUT/c-flush.txt" begin.runs_sent)' -ge 1 ] && [ '$(val "$OUT/c-flush.txt" flush.rc)' = 1 ] && [ '$(val "$OUT/c-flush.txt" end.rc)' = 1 ]"
	check "3d it arrives (late=1)" chk run "$INBOX" logtest "$RD" --has logtest.txt --meta late=1 --done

	# 3e. server= instead of discovery
	conf "server=127.0.0.1:$TCP"
	drive c-server run
	R=$(val "$OUT/c-server.txt" run)
	check "3e server= (no discovery): sent" chk run "$INBOX" logtest "$R" --outcome ok --done

	# 3f. wrong token: refused (401), nothing filed, run kept pending
	GOOD=$TOKEN
	TOKEN=$(printf '%064d' 7)
	conf "server=127.0.0.1:$TCP"
	drive c-badtok run
	R=$(val "$OUT/c-badtok.txt" run)
	check "3f wrong token: PENDING, nothing filed" sh -c "[ '$(val "$OUT/c-badtok.txt" end.rc)' = 2 ] && ! $CHKQ run '$INBOX' logtest '$R' > /dev/null"
	check "3f wrong token: the receiver logged 401s" grep -q " 401 " "$C/receiver.log"
	TOKEN=$GOOD
	conf "discover=127.0.0.1" "discovery_port=$UDP"
	drive c-recover run
	check "3f fixed token: the pending run goes out" chk run "$INBOX" logtest "$R" --done

	# 3g. disabled
	rm -rf "$OUT/off"; mkdir -p "$OUT/off"
	"$DRV" "$OUT/off" run > "$OUT/c-off.txt" 2>&1
	check "3g no devlog.txt: DISABLED, no network, no devlog/ folder" sh -c "[ '$(val "$OUT/c-off.txt" end.rc)' = 0 ] && [ ! -e '$OUT/off/devlog' ]"
	printf 'enabled=0\ntoken=%s\n' "$TOKEN" > "$OUT/off/devlog.txt"
	"$DRV" "$OUT/off" run > "$OUT/c-off0.txt" 2>&1
	check "3g enabled=0: DISABLED, no devlog/ folder" sh -c "[ '$(val "$OUT/c-off0.txt" end.rc)' = 0 ] && [ ! -e '$OUT/off/devlog' ]"

	rx_stop
	check "receiver log: no token in it" sh -c "! grep -q '$TOKEN' '$C/receiver.log'"
	check "receiver log: no handler errors" sh -c "! grep -q 'Traceback' '$C/receiver.log'"
fi

# ------------------------------------------------------------------------------------------------ 4
echo "== 4. nothing left running"
cleanup
sleep 0.3
if pgrep -f "receiver.py --root $OUT" > /dev/null 2>&1; then bad "a receiver is still running"; else ok "no receiver left"; fi

echo "== $PASS passed, $FAIL failed, $SKIP skipped   ($OUT)"
[ "$FAIL" -eq 0 ]
