#!/bin/sh
# Start / stop the Shepherd devlog receiver (LAN only). Nothing here installs anything persistent:
# `start` runs it in the background until `stop` (or a reboot). For a login item, see README.md (LaunchAgent).
#
#   devlogctl.sh token [--rotate]   create the secret token + a ready devlog.txt for the Vita (once)
#   devlogctl.sh kid                the token's fingerprint (compare with another machine; never on the wire)
#   devlogctl.sh start [serve opts] start in the background (default: the Mac's LAN IPv4, TCP 47710, UDP 47711)
#   devlogctl.sh stop | restart | status
#   devlogctl.sh run [serve opts]   run in the foreground, log on the terminal (Ctrl-C stops it)
#   devlogctl.sh list [--days N]    what came in
#   devlogctl.sh log                last 40 lines of the receiver's log (+ startup errors, if any)
#
# Env: SHPD_DEVLOG_ROOT (default ${SHEPHERD_WORK:-$HOME/shepherd-work}/devlog), PYTHON (default /usr/bin/python3),
#      SHPD_DEVLOG_TOKEN_FILE (default <root>/token; e.g. a 0600 file on the boot disk on a multi-account Mac).
# The receiver writes its log itself (receiver.log, rotating: 3 files of 5 MB at most); this script's own
# redirect (receiver.out) only ever gets startup errors.
set -eu

HERE=$(cd "$(dirname "$0")" && pwd)
RX="$HERE/receiver.py"
ROOT=${SHPD_DEVLOG_ROOT:-${SHEPHERD_WORK:-$HOME/shepherd-work}/devlog}
PY=${PYTHON:-/usr/bin/python3}
[ -x "$PY" ] || PY=$(command -v python3)
PIDFILE="$ROOT/receiver.pid"
LOG="$ROOT/receiver.log"
OUTF="$ROOT/receiver.out"
TOKEN_FILE=${SHPD_DEVLOG_TOKEN_FILE:-$ROOT/token}

running_pid() {
	[ -f "$PIDFILE" ] || return 1
	pid=$(awk '{print $1; exit}' "$PIDFILE" 2>/dev/null) || return 1
	case "$pid" in ''|*[!0-9]*) return 1 ;; esac
	kill -0 "$pid" 2>/dev/null || return 1
	# make sure the pid is still OUR receiver (pids get reused)
	ps -p "$pid" -o command= 2>/dev/null | grep -q "receiver.py.* serve" || return 1
	echo "$pid"
}

cmd=${1:-status}
[ $# -gt 0 ] && shift

case "$cmd" in
token)
	exec "$PY" "$RX" --root "$ROOT" --token-file "$TOKEN_FILE" gen-token "$@"
	;;
kid)
	exec "$PY" "$RX" --root "$ROOT" --token-file "$TOKEN_FILE" kid
	;;
start)
	if pid=$(running_pid); then echo "already running (pid $pid)"; exit 0; fi
	[ -f "$TOKEN_FILE" ] || { echo "no token yet: run  $0 token"; exit 1; }
	mkdir -p "$ROOT"
	if [ -f "$OUTF" ] && [ "$(wc -c < "$OUTF")" -gt 1000000 ]; then mv -f "$OUTF" "$OUTF.1"; fi
	nohup "$PY" "$RX" --root "$ROOT" --token-file "$TOKEN_FILE" serve --pidfile "$PIDFILE" --log-file "$LOG" "$@" \
		>> "$OUTF" 2>&1 < /dev/null &
	i=0
	while [ $i -lt 30 ]; do
		if pid=$(running_pid); then
			echo "started (pid $pid)"
			grep "shpdlog receiver:" "$LOG" 2>/dev/null | tail -1 | sed 's/^.*shpdlog receiver: /  /'
			exit 0
		fi
		sleep 0.1; i=$((i + 1))
	done
	echo "did not start; last lines:"; tail -n 8 "$OUTF" 2>/dev/null; tail -n 8 "$LOG" 2>/dev/null; exit 1
	;;
stop)
	if ! pid=$(running_pid); then echo "not running"; exit 0; fi
	kill -TERM "$pid"
	i=0
	while kill -0 "$pid" 2>/dev/null && [ $i -lt 50 ]; do sleep 0.1; i=$((i + 1)); done
	if kill -0 "$pid" 2>/dev/null; then echo "still running after 5 s (pid $pid); kill -KILL $pid if needed"; exit 1; fi
	echo "stopped"
	;;
restart)
	"$0" stop
	exec "$0" start "$@"
	;;
status)
	if pid=$(running_pid); then
		echo "running (pid $pid)"
		lsof -nP -a -p "$pid" -i 2>/dev/null | awk 'NR>1 {print "  " $8 " " $9 " " $10}'
	else
		echo "not running"
	fi
	;;
run)
	[ -f "$TOKEN_FILE" ] || { echo "no token yet: run  $0 token"; exit 1; }
	exec "$PY" "$RX" --root "$ROOT" --token-file "$TOKEN_FILE" serve "$@"
	;;
list)
	exec "$PY" "$RX" --root "$ROOT" list "$@"
	;;
log)
	# the receiver's own log (devlogctl start and the LaunchAgent both use it); request data in it is escaped
	if [ -f "$LOG" ]; then echo "== $LOG"; tail -n 40 "$LOG"; else echo "no log yet ($LOG)"; fi
	for f in "$OUTF" "$HOME/Library/Logs/shepherd-devlog-receiver.log"; do
		if [ -s "$f" ]; then echo "== startup errors: $f"; tail -n 5 "$f"; fi
	done
	;;
*)
	sed -n '2,17p' "$0"; exit 2
	;;
esac
