#!/usr/bin/env python3
"""Shepherd devlog receiver (protocol shpdlog/1 revision 3, see vita/devlog/PROTOCOL.md).

A small LAN-only service on the owner's Mac that accepts log files uploaded by the Shepherd Vita apps
(vita/devlog/ client) and files them under <root>/inbox/<yyyy-mm-dd>/<app>/<run>/.

It is deliberately separate from everything else on this Mac: its own process, its own port, bound to the LAN
IPv4 (or 127.0.0.1 for tests), never to 0.0.0.0 for TCP, never behind any tunnel. Python 3 standard library only.
Runs on /usr/bin/python3 (3.9) and newer.

The token never travels: every request carries an HMAC over (method, target, a fresh nonce, the body's SHA-256),
and the ping answer carries an HMAC proof the client checks before it sends anything else.

Subcommands:
  serve       run the HTTP receiver + the UDP discovery responder (foreground; devlogctl.sh wraps it)
  gen-token   create the per-install secret (<root>/token, 0600) and a ready devlog.txt for the Vita
  kid         print the token's fingerprint (safe to show; never sent on the network)
  list        list recently received runs
"""

from __future__ import annotations

import argparse
import codecs
import collections
import datetime as dt
import hashlib
import hmac
import http.server
import ipaddress
import json
import logging
import logging.handlers
import os
import re
import secrets
import shutil
import signal
import socket
import socketserver
import stat
import subprocess
import sys
import threading
import time

PROTO = "shpdlog/1"
MAGIC = "SHPDLOG1"
DEFAULT_ROOT = os.path.join(os.environ.get("SHEPHERD_WORK") or os.path.expanduser("~/shepherd-work"), "devlog")
REQUIRED_MOUNT = os.environ.get("SHEPHERD_DEVLOG_MOUNT", "")   # a volume the state dir must be on
DEFAULT_TCP_PORT = 47710
DEFAULT_UDP_PORT = 47711
# ports owned by other services on this Mac (Archive Codex server/tunnel, SideJITServer, wrapper, Sunshine)
RESERVED_PORTS = {8765, 8770, 8080, 8090} | set(range(47984, 48011))
EXIT_NET_CHANGED = 75  # EX_TEMPFAIL: the LAN address went away/changed; launchd (KeepAlive) restarts us
O_NOFOLLOW = getattr(os, "O_NOFOLLOW", 0)

TOKEN_RE = re.compile(r"[0-9a-f]{64}")
SHA_RE = re.compile(r"[0-9a-f]{64}")
EMPTY_SHA = hashlib.sha256(b"").hexdigest()
# Authorization: SHPD1 nonce=<16hex> body=<sha256 of the body, 64hex> mac=<MAC("req|M|target|nonce|body")>
AUTH_RE = re.compile(r"SHPD1 nonce=([0-9a-f]{16}) body=([0-9a-f]{64}) mac=([0-9a-f]{32})")
APP_RE = r"[a-z][a-z0-9_-]{0,31}"
RUN_RE = r"[A-Za-z0-9][A-Za-z0-9_-]{5,63}"
NAME_RE = r"[A-Za-z0-9][A-Za-z0-9._-]{0,63}"
# matched against the RAW request target: no '%', no '?', no absolute-form URL, so nothing can be decoded into '..'
ROUTE_RE = re.compile(r"/v1/runs/(%s)/(%s)/(meta|done|files/(%s))" % (APP_RE, RUN_RE, NAME_RE))
# revision 3, the Codex binding (PROTOCOL.md §10): <base>/v1/ping and ONE exact upload path, the run in a fixed-order
# query (still the raw target: no '%' decoding, every value matches its own regex). --base makes this receiver speak
# it (tests; the Archive Codex server's vita_devlog_routes.py speaks it for real).
BASE_RE = re.compile(r"(?:/[a-z0-9_-]{1,32}){1,4}")


def codex_route_re(base):
    return re.compile(re.escape(base) + r"/v1/upload\?op=(meta|done|file)&app=(%s)&run=(%s)(?:&name=(%s))?"
                      % (APP_RE, RUN_RE, NAME_RE))
ALLOWED_EXT = (".txt", ".log", ".csv", ".tsv", ".json")
META_KEY_RE = re.compile(r"[a-z][a-z0-9_]{0,31}")
META_MAX_KEYS = 64
META_MAX_VALUE = 256
# never accepted as metadata: console/account/network identifiers (PROTOCOL.md: "logs only, no identifiers")
FORBIDDEN_KEYS = {
    "psid", "openpsid", "idps", "cid", "account", "account_id", "aid", "user", "username", "user_name", "nick",
    "nickname", "email", "mac", "mac_address", "macaddr", "bssid", "ssid", "ip", "ip_addr", "ipaddr", "serial",
    "console_id", "device_id", "np_id", "npid", "online_id", "login",
}
FORBIDDEN_KEY_PARTS = ("psid", "idps", "account", "username", "email", "macaddr", "mac_addr", "ssid", "serial",
                       "nickname", "online_id", "npid", "np_id")
MACADDR_RE = re.compile(r"(?<![0-9A-Fa-f])(?:[0-9A-Fa-f]{2}[:-]){5}[0-9A-Fa-f]{2}(?![0-9A-Fa-f])")
# control bytes that count against the "looks like text" rule (TAB LF CR ESC are fine)
CTRL_BYTES = bytes(b for b in range(32) if b not in (9, 10, 13, 27)) + b"\x7f"
# the only networks the receiver treats as "the LAN" (RFC 1918); everything else is refused for --bind
LAN_NETS = tuple(ipaddress.ip_network(n) for n in ("10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16"))
CGNAT = ipaddress.ip_network("100.64.0.0/10")  # Tailscale lives here: never a LAN
LOOPBACK_NET = ipaddress.ip_network("127.0.0.0/8")
INFO_HEADERS = (("X-Shpd-Sanitized", "sanitized"), ("X-Shpd-File-Size", "file_size"), ("X-Shpd-Offset", "offset"))
RUN_OVERHEAD = 1024    # bytes charged against the daily / total caps for each new run (dirs, manifest, index line)
EVENT_OVERHEAD = 256   # ... and for every manifest rewrite + index line (file stored, meta, done)
LOG_WINDOW = 60.0      # unauthenticated noise: at most one log line per source IP per this many seconds

log = logging.getLogger("shpdlog")


# ---------------------------------------------------------------------------------------------------- caps / config

class Caps:
    def __init__(self, a):
        self.file = a.max_file
        self.run = a.max_run
        self.files_per_run = a.max_files
        self.meta = a.max_meta
        self.day = a.max_day
        self.total = a.max_total
        self.new_runs = a.max_new_runs
        self.free_floor = a.min_free
        self.conns = a.max_conns
        self.per_ip = a.max_per_ip
        self.rate = a.rate
        self.idle = a.idle_timeout
        self.header_timeout = a.header_timeout
        self.request_deadline = a.request_deadline
        self.ban_after = a.ban_after
        self.lookback_days = 14


def mib(s):
    """'8M' / '256M' / '4G' / '65536' -> bytes."""
    s = str(s).strip().upper()
    mult = 1
    if s.endswith("K"):
        mult, s = 1024, s[:-1]
    elif s.endswith("M"):
        mult, s = 1024 ** 2, s[:-1]
    elif s.endswith("G"):
        mult, s = 1024 ** 3, s[:-1]
    if not s.isdigit():
        raise argparse.ArgumentTypeError("bad size %r" % s)
    return int(s) * mult


def _safe(text, limit=200):
    """For the receiver's own log: at most `limit` characters, every non-printable one escaped (no terminal escape
    sequences, no line breaks ever reach the log from a request)."""
    if isinstance(text, bytes):
        text = text.decode("latin-1")
    text = str(text)
    cut = len(text) > limit
    out = []
    for c in text[:limit]:
        o = ord(c)
        if 0x20 <= o < 0x7f and c != "\\":
            out.append(c)
        elif c == "\\":
            out.append("\\\\")
        elif o < 0x100:
            out.append("\\x%02x" % o)
        else:
            out.append("\\u%04x" % o if o < 0x10000 else "\\U%08x" % o)
    return "".join(out) + ("...(+%d)" % (len(text) - limit) if cut else "")


# ---------------------------------------------------------------------------------------------------- token

def die(msg, code=2):
    """A configuration error: one line on stderr, then exit. Under the LaunchAgent (SHPD_DEVLOG_UNDER_LAUNCHD=1) the
    exit status is 0, so launchd does not restart a receiver that cannot work every 30 s (README: Run it at login)."""
    sys.stderr.write("%s devlog-receiver: %s\n" % (time.strftime("%Y-%m-%d %H:%M:%S"), msg))
    sys.exit(0 if os.environ.get("SHPD_DEVLOG_UNDER_LAUNCHD") == "1" else code)


def load_token(path):
    try:
        st = os.lstat(path)
    except FileNotFoundError:
        die("no token at %s -- run: devlogctl.sh token" % path)
    if stat.S_ISLNK(st.st_mode) or not stat.S_ISREG(st.st_mode):
        die("%s must be a regular file (not a symlink)" % path)
    if st.st_uid != os.getuid():
        die("%s is not owned by this user" % path)
    if st.st_mode & 0o077:
        die("%s is readable by others (mode %o) -- chmod 600 it" % (path, st.st_mode & 0o777))
    with open(path, "r", encoding="ascii") as f:
        tok = f.read().strip()
    if not TOKEN_RE.fullmatch(tok):
        die("%s does not hold a 64-hex token" % path)
    return tok


def kid_of(token):
    """Operator-facing fingerprint of the token (shown by `kid`, in the start line). Never sent on the network."""
    return hashlib.sha256(token.encode("ascii")).hexdigest()[:16]


def mac_of(token, msg):
    return hmac.new(token.encode("ascii"), msg.encode("ascii"), hashlib.sha256).hexdigest()[:32]


def write_private(path, text):
    """Create/replace path with mode 0600 atomically (tmp + rename in the same dir)."""
    d = os.path.dirname(path) or "."
    tmp = os.path.join(d, ".%s.new-%s" % (os.path.basename(path), secrets.token_hex(4)))
    fd = os.open(tmp, os.O_WRONLY | os.O_CREAT | os.O_EXCL | O_NOFOLLOW, 0o600)
    try:
        os.fchmod(fd, 0o600)
        os.write(fd, text.encode("ascii"))
        os.fsync(fd)
    finally:
        os.close(fd)
    os.replace(tmp, path)


def _run(args):
    try:
        return subprocess.run(args, capture_output=True, text=True, timeout=5, check=False).stdout
    except (OSError, subprocess.SubprocessError):
        return ""


def ignores_ownership(path):
    """True when path lives on a volume mounted with 'noowners' (macOS "Ignore ownership on this volume"): there a
    0600 mode does not keep other local accounts out."""
    rp = os.path.realpath(path)
    best, opts = "", ""
    for line in _run(["/sbin/mount"]).splitlines():
        m = re.match(r"^.+? on (/.*?) \((.*)\)$", line)
        if m and (rp == m.group(1) or rp.startswith(m.group(1).rstrip("/") + "/")) and len(m.group(1)) > len(best):
            best, opts = m.group(1), m.group(2)
    return "noowners" in [o.strip() for o in opts.split(",")]


# ---------------------------------------------------------------------------------------------------- network

class NetUnavailable(ValueError):
    """The network is not (yet) there: transient, worth a restart later (unlike a bad --bind)."""


class BindInfo:
    def __init__(self, iface, ip, net, loopback):
        self.iface, self.ip, self.net, self.loopback = iface, ip, net, loopback

    def __repr__(self):
        return "%s %s (%s)" % (self.iface, self.ip, self.net)


def ifconfig_ipv4():
    """[(iface, ip, prefixlen)] from ifconfig -a (macOS)."""
    out = _run(["/sbin/ifconfig", "-a"])
    res, cur = [], None
    for line in out.splitlines():
        m = re.match(r"^([A-Za-z0-9]+): flags=", line)
        if m:
            cur = m.group(1)
            continue
        m = re.match(r"^\s+inet (\d+\.\d+\.\d+\.\d+) (?:--> \S+ )?netmask (0x[0-9a-fA-F]{8})", line)
        if m and cur:
            res.append((cur, m.group(1), bin(int(m.group(2), 16)).count("1")))
    return res


def default_iface():
    m = re.search(r"interface:\s*(\S+)", _run(["/sbin/route", "-n", "get", "default"]))
    return m.group(1) if m else None


def is_lan_ip(ip):
    """Only the three RFC 1918 networks count (not is_private: that also takes 198.18/15 -- fake-IP VPN tunnels --,
    192.0.0/24, 192.0.2/24, 0/8, 240/4, ...). Never loopback, link-local or CGNAT/Tailscale."""
    try:
        a = ipaddress.ip_address(ip)
    except ValueError:
        return False
    return (a.version == 4 and any(a in n for n in LAN_NETS) and not a.is_loopback and not a.is_link_local
            and a not in CGNAT)


def resolve_bind(bind, iface):
    """'auto' -> the private IPv4 of the default-route interface (or --iface); an IP -> that local address;
    '127.0.0.1' -> loopback test mode. Never 0.0.0.0, never a public/CGNAT/Tailscale address."""
    if bind in ("127.0.0.1", "loopback", "localhost"):
        return BindInfo("lo0", "127.0.0.1", LOOPBACK_NET, True)
    if bind in ("0.0.0.0", "::", ""):
        raise ValueError("refusing to bind all interfaces; use auto, a LAN IP or 127.0.0.1")
    addrs = ifconfig_ipv4()
    if bind == "auto":
        want = iface or default_iface()
        if not want:
            raise NetUnavailable("no default route (no network?); pass --bind <LAN ip>")
        cands = [a for a in addrs if a[0] == want and is_lan_ip(a[1])]
        if not cands:
            raise NetUnavailable("interface %s has no private LAN IPv4; pass --iface or --bind" % want)
    else:
        try:
            ipaddress.IPv4Address(bind)
        except ValueError:
            raise ValueError("--bind must be auto, 127.0.0.1 or an IPv4 address")
        if not is_lan_ip(bind):
            raise ValueError("refusing %s: not a private LAN address (10/8, 172.16/12, 192.168/16)" % bind)
        cands = [a for a in addrs if a[1] == bind and (not iface or a[0] == iface)]
        if not cands:
            raise NetUnavailable("%s is not an address of this Mac%s" % (bind, " on " + iface if iface else ""))
    name, ip, plen = cands[0]
    return BindInfo(name, ip, ipaddress.ip_network("%s/%d" % (ip, plen), strict=False), False)


# ---------------------------------------------------------------------------------------------------- limits

class RateLimiter:
    """Sliding 60 s window per source IP."""

    def __init__(self, per_min):
        self.per_min = per_min
        self.hits = {}
        self.lock = threading.Lock()

    def allow(self, key):
        now = time.monotonic()
        with self.lock:
            dq = self.hits.get(key)
            if dq is None:
                if len(self.hits) > 4096:
                    self.hits = {k: v for k, v in self.hits.items() if v and now - v[-1] < 60}
                dq = self.hits[key] = collections.deque()
            while dq and now - dq[0] > 60:
                dq.popleft()
            if len(dq) >= self.per_min:
                return False
            dq.append(now)
            return True


class AuthGuard:
    """10 bad authentications in 60 s from one IP -> that IP gets 429 for 10 minutes."""

    def __init__(self, fails=10, window=60.0, ban=600.0):
        self.fails, self.window, self.ban = fails, window, ban
        self.bad = {}
        self.banned_until = {}
        self.lock = threading.Lock()

    def banned(self, ip):
        with self.lock:
            t = self.banned_until.get(ip)
            if t and time.monotonic() < t:
                return True
            self.banned_until.pop(ip, None)
            return False

    def fail(self, ip):
        now = time.monotonic()
        with self.lock:
            if len(self.bad) > 4096:
                self.bad = {k: v for k, v in self.bad.items() if v and now - v[-1] < self.window}
            dq = self.bad.setdefault(ip, collections.deque())
            dq.append(now)
            while dq and now - dq[0] > self.window:
                dq.popleft()
            if len(dq) >= self.fails:
                self.banned_until[ip] = now + self.ban
                dq.clear()
                log.warning("%s banned for %ds after %d bad authentications", ip, self.ban, self.fails)


class NonceCache:
    """Nonces of authenticated requests seen in the last `ttl` seconds: a replayed request is refused."""

    def __init__(self, ttl=900.0, cap=100000):
        self.ttl, self.cap = ttl, cap
        self.seen = collections.OrderedDict()
        self.lock = threading.Lock()

    def add(self, nonce):
        now = time.monotonic()
        with self.lock:
            while self.seen:
                k, t = next(iter(self.seen.items()))
                if now - t > self.ttl or len(self.seen) >= self.cap:
                    self.seen.popitem(last=False)
                else:
                    break
            if nonce in self.seen:
                return False
            self.seen[nonce] = now
            return True


class QuietLog:
    """Lines caused by requests that did not authenticate (401, 429, bans, malformed requests, refused or timed-out
    connections, bad discovery datagrams): at most one line per source IP per LOG_WINDOW seconds; the next line
    says how many were left out. So nobody without the token can grow the log faster than ~1 line/IP/minute."""

    def __init__(self, window=LOG_WINDOW, max_keys=2048):
        self.window, self.max_keys = window, max_keys
        self.state = {}
        self.lock = threading.Lock()

    def emit(self, ip, line, level=logging.INFO, exc_info=False):
        now = time.monotonic()
        with self.lock:
            st = self.state.get(ip)
            if st and now - st[0] < self.window:
                st[1] += 1
                return
            dropped = st[1] if st else 0
            if len(self.state) >= self.max_keys:
                self.state = {k: v for k, v in self.state.items() if now - v[0] < self.window}
                if len(self.state) >= self.max_keys:
                    return
            self.state[ip] = [now, 0]
        if dropped:
            line += "  (+%d similar line(s) from %s left out in the last %ds)" % (dropped, ip, self.window)
        log.log(level, "%s", line, exc_info=exc_info)


class Watchdog(threading.Thread):
    """One thread that closes connections whose deadline passed: the header deadline (accept -> end of headers, and
    the short reply that follows an unauthenticated request) and, after authentication, the request deadline."""

    def __init__(self):
        threading.Thread.__init__(self, name="watchdog", daemon=True)
        self.lock = threading.Lock()
        self.items = {}
        self.stop_ev = threading.Event()

    def arm(self, handler, seconds, why):
        with self.lock:
            self.items[handler] = (time.monotonic() + seconds, why)

    def disarm(self, handler):
        with self.lock:
            self.items.pop(handler, None)

    def run(self):
        while not self.stop_ev.wait(0.25):
            now = time.monotonic()
            expired = []
            with self.lock:
                for h, (t, why) in list(self.items.items()):
                    if now >= t:
                        expired.append((h, why))
                        del self.items[h]
            for h, why in expired:
                try:
                    h.connection.shutdown(socket.SHUT_RDWR)
                except OSError:
                    pass
                h._note("connection closed: %s" % why)

    def stop(self):
        self.stop_ev.set()


class HttpError(Exception):
    def __init__(self, code, msg=""):
        Exception.__init__(self, code, msg)
        self.code = code
        self.msg = msg


# ---------------------------------------------------------------------------------------------------- storage

def _now_iso():
    return dt.datetime.now().astimezone().isoformat(timespec="seconds")


def _read_json(path, default):
    try:
        st = os.lstat(path)
    except FileNotFoundError:
        return default
    except OSError as e:
        log.error("cannot stat %s (%s); starting it over", path, e)
        return default
    if not stat.S_ISREG(st.st_mode):
        log.error("%s is not a regular file (symlink?); ignored, starting it over", path)
        return default
    try:
        with open(path, "r", encoding="utf-8") as f:
            return json.load(f)
    except FileNotFoundError:
        return default
    except (ValueError, OSError) as e:
        log.error("unreadable %s (%s); starting it over", path, e)
        return default


def _write_json(path, obj):
    tmp = "%s.tmp-%s" % (path, secrets.token_hex(4))
    with open(tmp, "x", encoding="utf-8") as f:   # O_EXCL: never follows a planted symlink
        json.dump(obj, f, indent=1, sort_keys=True)
        f.write("\n")
    os.replace(tmp, path)                           # replaces a symlink at path itself, never its target


def _tree_size(path):
    total = 0
    for dirpath, dirs, files in os.walk(path):
        for fn in files:
            try:
                total += os.lstat(os.path.join(dirpath, fn)).st_size
            except OSError:
                pass
    return total


def _count_runs(day_dir):
    n = 0
    try:
        for app in os.listdir(day_dir):
            ad = os.path.join(day_dir, app)
            if os.path.isdir(ad) and not os.path.islink(ad):
                n += sum(1 for r in os.listdir(ad) if os.path.isdir(os.path.join(ad, r)))
    except OSError:
        pass
    return n


class Store:
    """Everything the receiver writes lives under self.inbox; paths are built only from regex-checked segments
    and checked again against the real inbox path before use."""

    def __init__(self, inbox, caps):
        os.makedirs(inbox, mode=0o750, exist_ok=True)
        self.inbox = os.path.realpath(inbox)
        self.caps = caps
        self.lock = threading.RLock()
        self.cache = {}
        self.inflight = {}    # (app, run.lower()) -> {file name: bytes reserved by an upload in progress}
        self.total = _tree_size(self.inbox)
        self.day = self._today()
        self.day_bytes = _tree_size(os.path.join(self.inbox, self.day))
        self.day_runs = _count_runs(os.path.join(self.inbox, self.day))
        self.index_path = os.path.join(self.inbox, "index.jsonl")
        try:
            st = os.lstat(self.index_path)
            if not stat.S_ISREG(st.st_mode):
                die("%s is not a regular file (a symlink?): move it away first" % self.index_path)
        except FileNotFoundError:
            pass

    @staticmethod
    def _today():
        return dt.date.today().isoformat()

    def _inside(self, path):
        rp = os.path.realpath(path)
        if rp != os.path.normpath(path) or not rp.startswith(self.inbox + os.sep):
            raise HttpError(400, "bad path\n")
        return rp

    def check_free(self):
        try:
            free = shutil.disk_usage(self.inbox).free
        except OSError:
            raise HttpError(507, "storage unavailable\n")
        if free < self.caps.free_floor:
            raise HttpError(507, "volume nearly full\n")

    def find_run(self, app, run):
        key = (app, run.lower())
        with self.lock:
            p = self.cache.get(key)
            if p and os.path.isdir(p):
                return p
            today = dt.date.today()
            for i in range(self.caps.lookback_days + 1):
                d = (today - dt.timedelta(days=i)).isoformat()
                p = os.path.join(self.inbox, d, app, run)
                if os.path.isdir(p) and not os.path.islink(p):
                    self.cache[key] = self._inside(p)
                    return self.cache[key]
            return None

    def run_dir(self, app, run):
        """Existing dir for app/run, or a new one under today's date (charged RUN_OVERHEAD, counted against the
        new-runs-per-day cap). Returns (path, created)."""
        with self.lock:
            p = self.find_run(app, run)
            if p:
                return p, False
            self._roll_day()
            if self.day_runs >= self.caps.new_runs:
                raise HttpError(429, "too many new runs today\n")
            p = os.path.join(self.inbox, self._today(), app, run)
            self._inside(p)
            self.charge(RUN_OVERHEAD)
            os.makedirs(os.path.join(p, "files"), mode=0o750, exist_ok=True)
            p = self._inside(p)
            self.day_runs += 1
            self.cache[(app, run.lower())] = p
            return p, True

    def _roll_day(self):
        t = self._today()
        if t != self.day:
            self.day, self.day_bytes, self.day_runs = t, 0, 0

    def charge(self, n):
        with self.lock:
            self._roll_day()
            if self.day_bytes + n > self.caps.day:
                raise HttpError(429, "daily cap reached\n")
            if self.total + n > self.caps.total:
                raise HttpError(507, "inbox cap reached\n")
            self.day_bytes += n
            self.total += n

    def refund(self, n, day=True):
        with self.lock:
            self.total = max(0, self.total - n)
            if day:
                self.day_bytes = max(0, self.day_bytes - n)

    def manifest(self, rd, app, run):
        m = _read_json(os.path.join(rd, "manifest.json"), None)
        if not isinstance(m, dict) or not isinstance(m.get("files"), dict):
            m = {"app": app, "run": run, "first_seen": _now_iso(), "files": {}, "done": False}
        return m

    def save_manifest(self, rd, m):
        m["updated"] = _now_iso()
        _write_json(os.path.join(rd, "manifest.json"), m)

    def index(self, ev, rd, **kw):
        rec = {"t": _now_iso(), "ev": ev, "path": os.path.relpath(rd, self.inbox)}
        rec.update((k, v) for k, v in kw.items() if v is not None)
        line = (json.dumps(rec, sort_keys=True) + "\n").encode("utf-8")
        with self.lock:
            try:   # O_NOFOLLOW: a symlink planted as index.jsonl is refused, never appended through
                fd = os.open(self.index_path, os.O_WRONLY | os.O_APPEND | os.O_CREAT | O_NOFOLLOW, 0o640)
            except OSError as e:
                log.error("index.jsonl: cannot open (%s); %s event not indexed", e, ev)
                return
            try:
                if not stat.S_ISREG(os.fstat(fd).st_mode):
                    log.error("index.jsonl is not a regular file; %s event not indexed", ev)
                    return
                os.write(fd, line)
            finally:
                os.close(fd)


# ---------------------------------------------------------------------------------------------------- HTTP

def parse_meta(data, caps):
    """key=value lines -> dict; raises HttpError(422) on anything off-contract."""
    try:
        text = data.decode("utf-8")
    except UnicodeDecodeError:
        raise HttpError(422, "meta is not UTF-8\n")
    out = {}
    for raw in text.split("\n"):
        line = raw[:-1] if raw.endswith("\r") else raw
        if not line.strip() or line.lstrip().startswith("#"):
            continue
        if "=" not in line:
            raise HttpError(422, "meta line without '='\n")
        k, v = line.split("=", 1)
        k = k.strip()
        if not META_KEY_RE.fullmatch(k):
            raise HttpError(422, "bad meta key\n")
        if k in FORBIDDEN_KEYS or any(p in k for p in FORBIDDEN_KEY_PARTS):
            raise HttpError(422, "forbidden meta key: %s\n" % k)
        if len(v.encode("utf-8")) > META_MAX_VALUE:
            raise HttpError(422, "meta value too long: %s\n" % k)
        if any(ord(c) < 32 or ord(c) == 127 for c in v):
            raise HttpError(422, "control character in meta value: %s\n" % k)
        if MACADDR_RE.search(v):
            raise HttpError(422, "meta value looks like a MAC address: %s\n" % k)
        out[k] = v
        if len(out) > META_MAX_KEYS:
            raise HttpError(422, "too many meta keys\n")
    return out


def meta_size(d):
    return sum(len(k) + len(str(v).encode("utf-8")) + 2 for k, v in d.items())


class Handler(http.server.BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"
    server_version = "shpdlog"
    sys_version = ""

    # ------------------------------------------------------------------ plumbing
    def setup(self):
        self.timeout = self.server.caps.idle
        self._authed = False
        http.server.BaseHTTPRequestHandler.setup(self)

    def handle_one_request(self):
        # every request (also the next one on a kept-alive connection) gets the short header deadline first
        self._authed = False
        self._nonce = self._body_sha = None
        self.server.watch.arm(self, self.server.caps.header_timeout, "header deadline (%gs) passed"
                              % self.server.caps.header_timeout)
        http.server.BaseHTTPRequestHandler.handle_one_request(self)

    def parse_request(self):
        try:
            return http.server.BaseHTTPRequestHandler.parse_request(self)
        finally:
            # headers are in (or refused). An unauthenticated request only gets a short reply: the same short
            # deadline covers it. (An authenticated one -- Expect: 100-continue -- already has its own.)
            if not self._authed:
                self.server.watch.arm(self, self.server.caps.header_timeout, "reply deadline passed")

    def finish(self):
        self.server.watch.disarm(self)
        try:
            http.server.BaseHTTPRequestHandler.finish(self)
        except OSError:
            pass

    def version_string(self):
        return "shpdlog"

    def _note(self, text, level=logging.INFO, exc_info=False):
        """One line in the receiver's log: escaped, length-capped, and throttled per IP unless authenticated."""
        ip = self.client_address[0]
        line = "%s %s" % (ip, _safe(text, 300))
        if getattr(self, "_authed", False):
            log.log(level, "%s", line, exc_info=exc_info)
        else:
            self.server.quiet.emit(ip, line, level, exc_info)

    def log_request(self, code="-", size="-"):
        pass  # _dispatch / send_error write the one line per request

    def log_message(self, fmt, *args):
        self._note(fmt % args)

    def _req_text(self):
        return "%s %s" % (_safe(getattr(self, "command", "") or "-", 16), _safe(getattr(self, "path", "") or "-", 160))

    def _reply(self, code, body=b"", close=False):
        if isinstance(body, str):
            body = body.encode("utf-8")
        try:
            self.send_response(code)
            self.send_header("Content-Type", "text/plain; charset=utf-8")
            self.send_header("Content-Length", str(len(body)))
            self.send_header("Cache-Control", "no-store")
            if close:
                self.send_header("Connection", "close")
                self.close_connection = True
            self.end_headers()
            if body and self.command != "HEAD":
                self.wfile.write(body)
        except OSError:
            self.close_connection = True

    def send_error(self, code, message=None, explain=None):
        # http.server's own errors (bad request line, 414, 431, 501): short plain text, no HTML, then close
        self._note("%s -> %d %s" % (_safe(getattr(self, "requestline", "") or "-", 160), code, message or ""))
        self._reply(code, "%d\n" % code, close=True)

    def handle_expect_100(self):
        # refuse before the client sends a body we would not take
        try:
            self._gate()
            cl = self.headers.get("Content-Length", "")
            if cl.isdigit() and int(cl) > max(self.server.caps.file, self.server.caps.meta):
                raise HttpError(413, "too large\n")
        except HttpError as e:
            self._note("%s -> %d %s (expect 100)" % (self._req_text(), e.code, e.msg.strip()))
            self._reply(e.code, e.msg, close=True)
            return False
        return http.server.BaseHTTPRequestHandler.handle_expect_100(self)

    def _not_allowed(self):
        self._note("%s -> 405" % self._req_text())
        self._reply(405, "405\n", close=True)

    do_PUT = do_DELETE = do_PATCH = do_OPTIONS = do_HEAD = do_TRACE = do_CONNECT = _not_allowed

    def do_GET(self):
        self._dispatch("GET")

    def do_POST(self):
        self._dispatch("POST")

    # ------------------------------------------------------------------ auth + routing
    def _gate(self):
        """Ban / rate limit / authentication. The token never travels: the request carries a fresh nonce, the SHA-256
        of its body and MAC("req|METHOD|target|nonce|body"); a nonce is accepted once."""
        if self._authed:
            return
        srv = self.server
        ip = self.client_address[0]
        if srv.guard.banned(ip):
            raise HttpError(429, "")
        if not srv.rate.allow(ip):
            raise HttpError(429, "rate limited\n")
        m = AUTH_RE.fullmatch(self.headers.get("Authorization", "").strip())
        if not m:
            srv.guard.fail(ip)
            raise HttpError(401, "")
        nonce, body_sha, mac = m.groups()
        want = mac_of(srv.token, "req|%s|%s|%s|%s" % (self.command, self.path, nonce, body_sha))
        if not hmac.compare_digest(mac, want):
            srv.guard.fail(ip)
            raise HttpError(401, "")
        if not srv.nonces.add(nonce):
            srv.guard.fail(ip)
            raise HttpError(401, "replayed request\n")
        self._authed = True
        self._nonce, self._body_sha = nonce, body_sha
        srv.watch.arm(self, srv.caps.request_deadline + srv.caps.idle + 5.0, "request deadline passed")

    def _dispatch(self, method):
        srv = self.server
        self._deadline = time.monotonic() + srv.caps.request_deadline
        try:
            self._gate()
            if method == "GET":
                if self.path != srv.base + "/v1/ping":
                    raise HttpError(404, "")
                if self.headers.get("Content-Length", "0").strip() not in ("", "0") \
                        or self.headers.get("Transfer-Encoding") or self._body_sha != EMPTY_SHA:
                    raise HttpError(400, "GET with a body\n")
                host, port = srv.server_address[:2]
                proof = mac_of(srv.token, "pong|%s|%s|%d" % (self._nonce, host, port))
                self._note("%s -> 200" % self._req_text())
                return self._reply(200, "ok %s max_file=%d max_run=%d proof=%s\n"
                                   % (PROTO, srv.caps.file, srv.caps.run, proof))
            if srv.base:
                m = srv.codex_re.fullmatch(self.path)
                if not m:
                    raise HttpError(400 if self.path.startswith(srv.base + "/v1/") else 404, "")
                op, app, run, name = m.groups()
                if (op == "file") != (name is not None):
                    raise HttpError(400, "")
                what = "files/" if op == "file" else op
            else:
                m = ROUTE_RE.fullmatch(self.path)
                if not m:
                    raise HttpError(400 if self.path.startswith("/v1/runs/") else 404, "")
                app, run, what, name = m.groups()
            if what == "meta":
                self._meta(app, run)
            elif what == "done":
                self._done(app, run)
            else:
                self._file(app, run, name)
            self._note("%s -> 200" % self._req_text())
        except HttpError as e:
            self._note("%s -> %d %s" % (self._req_text(), e.code, e.msg.strip()))
            self._reply(e.code, e.msg, close=True)
        except (socket.timeout, TimeoutError):
            self._note("timeout on %s" % self._req_text())
            self.close_connection = True
        except (ConnectionError, BrokenPipeError):
            self.close_connection = True
        except OSError:
            self._note("%s: I/O error" % self._req_text(), logging.ERROR, exc_info=True)
            self._reply(500, "server I/O error\n", close=True)

    # ------------------------------------------------------------------ bodies
    def _body(self, limit, allow_empty=False):
        """Validate framing; return (iterator over the body, declared length or None for chunked). No I/O yet."""
        te = self.headers.get_all("Transfer-Encoding") or []
        cls = self.headers.get_all("Content-Length") or []
        if te:
            if cls or len(te) != 1 or te[0].strip().lower() != "chunked":
                raise HttpError(400, "unsupported transfer-encoding\n")
            return self._chunked(limit), None
        if not cls:
            if allow_empty:
                return iter(()), 0
            raise HttpError(411, "length required\n")
        if len(cls) != 1 or not re.fullmatch(r"[0-9]{1,15}", cls[0].strip()):
            raise HttpError(400, "bad content-length\n")
        n = int(cls[0])
        if n > limit:
            raise HttpError(413, "too large (limit %d)\n" % max(limit, 0))
        return self._fixed(n), n

    def _small_body(self, limit, allow_empty=False):
        """A meta / done body, read whole, checked against the body hash the request was authenticated with."""
        it, _ = self._body(limit, allow_empty)
        data = b"".join(it)
        if hashlib.sha256(data).hexdigest() != self._body_sha:
            raise HttpError(409, "body does not match its hash\n")
        return data

    def _tick(self):
        if time.monotonic() > self._deadline:
            raise HttpError(408, "request too slow\n")

    def _fixed(self, n):
        left = n
        while left:
            self._tick()
            piece = self.rfile.read(min(left, 65536))
            if not piece:
                raise HttpError(400, "body truncated\n")
            left -= len(piece)
            yield piece

    def _chunked(self, limit):
        total = 0
        while True:
            self._tick()
            line = self.rfile.readline(1026)
            if not line.endswith(b"\n"):
                raise HttpError(400, "bad chunk header\n")
            size_s = line.split(b";", 1)[0].strip()
            if not re.fullmatch(rb"[0-9A-Fa-f]{1,8}", size_s):
                raise HttpError(400, "bad chunk size\n")
            size = int(size_s, 16)
            if size == 0:
                for _ in range(32):  # trailers: read and drop
                    t = self.rfile.readline(1026)
                    if t in (b"\r\n", b"\n"):
                        return
                    if not t.endswith(b"\n"):
                        break
                raise HttpError(400, "bad chunk trailer\n")
            total += size
            if total > limit:
                raise HttpError(413, "too large (limit %d)\n" % max(limit, 0))
            left = size
            while left:
                self._tick()
                piece = self.rfile.read(min(left, 65536))
                if not piece:
                    raise HttpError(400, "body truncated\n")
                left -= len(piece)
                yield piece
            if self.rfile.readline(3) not in (b"\r\n", b"\n"):
                raise HttpError(400, "bad chunk end\n")

    # ------------------------------------------------------------------ endpoints
    def _meta(self, app, run):
        st = self.server.store
        st.check_free()
        data = self._small_body(self.server.caps.meta)
        kv = parse_meta(data, self.server.caps)
        with st.lock:
            rd = st.find_run(app, run)
            cur = _read_json(os.path.join(rd, "meta.json"), None) if rd else None
            if not isinstance(cur, dict) or not isinstance(cur.get("meta"), dict):
                cur = {"app": app, "run": run, "first_received": _now_iso(), "meta": {}}
            merged = dict(cur["meta"])
            merged.update(kv)
            # the caps hold for what is STORED (re-sent meta merges), not only per request
            if len(merged) > META_MAX_KEYS:
                raise HttpError(422, "too many meta keys in this run\n")
            if meta_size(merged) > self.server.caps.meta:
                raise HttpError(413, "meta of this run too large\n")
            st.charge(len(data) + EVENT_OVERHEAD)
            rd, _ = st.run_dir(app, run)
            cur["meta"] = merged
            cur["received"] = _now_iso()
            _write_json(os.path.join(rd, "meta.json"), cur)
            man = st.manifest(rd, app, run)
            if "outcome" in kv and not man.get("done"):
                man["outcome"] = kv["outcome"]
            st.save_manifest(rd, man)
            st.index("meta", rd, app=app, run=run, keys=len(kv), outcome=kv.get("outcome"),
                     title_id=kv.get("title_id"), model=kv.get("model"), fw=kv.get("fw"), build=kv.get("build"))
        self._reply(200, "ok\n")

    def _done(self, app, run):
        st = self.server.store
        data = self._small_body(1024, allow_empty=True)
        kv = parse_meta(data, self.server.caps) if data else {}
        with st.lock:
            rd = st.find_run(app, run)
            if not rd:
                raise HttpError(404, "unknown run\n")
            st.charge(EVENT_OVERHEAD)   # 429 once the daily cap is used up: /done cannot grow the index forever
            man = st.manifest(rd, app, run)
            man["done"] = True
            man["done_at"] = _now_iso()
            if kv.get("outcome"):
                man["outcome"] = kv["outcome"]
            st.save_manifest(rd, man)
            st.index("done", rd, app=app, run=run, outcome=man.get("outcome"),
                     files=len(man["files"]), bytes=sum(f.get("bytes", 0) for f in man["files"].values()))
        log.info("run complete: %s/%s outcome=%s files=%d", app, run, _safe(man.get("outcome"), 16), len(man["files"]))
        self._reply(200, "ok\n")

    def _file(self, app, run, name):
        st, caps = self.server.store, self.server.caps
        key = name.lower()
        if ".." in name or not key.endswith(ALLOWED_EXT):
            raise HttpError(400, "bad file name\n")
        info = {}
        for hdr, field in INFO_HEADERS:
            v = self.headers.get(hdr)
            if v is not None:
                v = v.strip()
                if not re.fullmatch(r"[0-9]{1,15}", v):
                    raise HttpError(400, "bad %s\n" % hdr)
                info[field] = int(v)
        st.check_free()
        rkey = (app, run.lower())
        # reserve the file slot and its bytes BEFORE the body is read, so parallel uploads cannot pass max-files /
        # max-run between the check and the store
        with st.lock:
            rd = st.find_run(app, run)
            files = st.manifest(rd, app, run)["files"] if rd else {}
            fl = st.inflight.setdefault(rkey, {})
            if key in fl:
                raise HttpError(409, "this file is being uploaded already\n")
            names = set(files) | set(fl)
            if key not in names and len(names) >= caps.files_per_run:
                if not fl:
                    st.inflight.pop(rkey, None)
                raise HttpError(413, "too many files in this run\n")
            others = sum(f.get("bytes", 0) for k, f in files.items() if k != key) + sum(fl.values())
            limit = min(caps.file, caps.run - others)
            try:
                if limit < 0:
                    raise HttpError(413, "run is full\n")
                body, declared = self._body(limit)   # framing/size errors here, before anything is created
            except HttpError:
                if not fl:
                    st.inflight.pop(rkey, None)
                raise
            fl[key] = declared if declared is not None else limit
        try:
            self._store_file(app, run, name, key, body, info)
        finally:
            with st.lock:
                fl.pop(key, None)
                if not fl and st.inflight.get(rkey) is fl:
                    st.inflight.pop(rkey, None)

    def _store_file(self, app, run, name, key, body, info):
        st = self.server.store
        with st.lock:
            rd, _ = st.run_dir(app, run)
        files_dir = st._inside(os.path.join(rd, "files"))
        os.makedirs(files_dir, mode=0o750, exist_ok=True)
        tmp = os.path.join(files_dir, ".%s.part-%s" % (key, secrets.token_hex(4)))
        final = os.path.join(files_dir, key)
        st._inside(final)
        h = hashlib.sha256()
        dec = codecs.getincrementaldecoder("utf-8")("replace")
        n = ctrl = bad = charged = 0
        ok = False
        try:
            try:
                fd = os.open(tmp, os.O_WRONLY | os.O_CREAT | os.O_EXCL | O_NOFOLLOW, 0o640)
            except OSError as e:
                log.error("cannot create %s: %s", tmp, e)
                raise HttpError(507, "cannot write\n")
            with os.fdopen(fd, "wb") as f:
                for piece in body:
                    if b"\0" in piece:
                        raise HttpError(415, "binary content (NUL byte)\n")
                    ctrl += len(piece) - len(piece.translate(None, CTRL_BYTES))
                    # invalid UTF-8 (a literal U+FFFD in the input is not counted as invalid)
                    bad += dec.decode(piece).count("�") - piece.count(b"\xef\xbf\xbd")
                    st.charge(len(piece))
                    charged += len(piece)
                    h.update(piece)
                    try:
                        f.write(piece)
                    except OSError as e:
                        log.error("write failed %s: %s", tmp, e)
                        raise HttpError(507, "cannot write\n")
                    n += len(piece)
            bad += dec.decode(b"", final=True).count("�")
            if n and ctrl * 200 > n:
                raise HttpError(415, "not text (%d control bytes)\n" % ctrl)
            if bad > 4 and bad * 100 > n:
                raise HttpError(415, "not text (%d invalid UTF-8 sequences)\n" % bad)
            digest = h.hexdigest()
            if digest != self._body_sha:
                raise HttpError(409, "body does not match its hash (the file changed while it was sent?)\n")
            with st.lock:
                st.charge(EVENT_OVERHEAD)
                charged += EVENT_OVERHEAD
                man = st.manifest(rd, app, run)
                old = man["files"].get(key)
                os.replace(tmp, final)
                ok = True
                if old:
                    st.refund(old.get("bytes", 0), day=False)
                ent = {"bytes": n, "sha256": digest, "received": _now_iso(), "sent_name": name}
                ent.update(info)
                if ctrl:
                    ent["control_bytes"] = ctrl
                if bad:
                    ent["invalid_utf8"] = bad
                man["files"][key] = ent
                st.save_manifest(rd, man)
                st.index("file", rd, app=app, run=run, name=key, bytes=n, sha256=digest, **info)
        finally:
            if not ok:
                try:
                    os.unlink(tmp)
                except OSError:
                    pass
                if charged:
                    st.refund(charged)
        self._reply(200, "ok bytes=%d sha256=%s\n" % (n, digest))


class Receiver(socketserver.ThreadingMixIn, http.server.HTTPServer):
    daemon_threads = True
    allow_reuse_address = True
    request_queue_size = 16

    def __init__(self, addr, token, store, caps, nets, base=""):
        self.token = token
        self.base = base
        self.codex_re = codex_route_re(base) if base else None
        self.store = store
        self.caps = caps
        self.nets = nets
        self.rate = RateLimiter(caps.rate)
        self.guard = AuthGuard(fails=caps.ban_after)
        self.nonces = NonceCache()
        self.quiet = QuietLog()
        self.watch = Watchdog()
        self.slots = threading.BoundedSemaphore(caps.conns)
        self.per_ip = {}
        self.ip_lock = threading.Lock()
        self.handled = set()   # ids of accepted sockets a handler thread owns (only those get the lingering close)
        http.server.HTTPServer.__init__(self, addr, Handler)
        self.watch.start()

    def server_bind(self):
        # skip HTTPServer's getfqdn() (a reverse DNS lookup); just bind
        socketserver.TCPServer.server_bind(self)
        host, port = self.server_address[:2]
        self.server_name, self.server_port = host, port

    def verify_request(self, request, client_address):
        try:
            ip = ipaddress.ip_address(client_address[0])
        except ValueError:
            return False
        if not any(ip in n for n in self.nets):
            self.quiet.emit(client_address[0], "refused connection from %s (outside %s)"
                            % (client_address[0], ", ".join(str(n) for n in self.nets)), logging.WARNING)
            return False
        return True

    def _busy(self, request, ip, why):
        self.quiet.emit(ip, "%s busy (%s): 503" % (ip, why))
        try:
            request.settimeout(0.5)
            request.sendall(b"HTTP/1.1 503 Service Unavailable\r\nContent-Length: 5\r\n"
                            b"Connection: close\r\n\r\nbusy\n")
        except OSError:
            pass
        self.close_request(request)

    def _ip_release(self, ip):
        with self.ip_lock:
            n = self.per_ip.get(ip, 0) - 1
            if n > 0:
                self.per_ip[ip] = n
            else:
                self.per_ip.pop(ip, None)

    def process_request(self, request, client_address):
        ip = client_address[0]
        with self.ip_lock:
            n = self.per_ip.get(ip, 0)
            if n >= self.caps.per_ip:
                over = "%d connections from this address" % n
            else:
                over = None
                self.per_ip[ip] = n + 1
        if over:
            return self._busy(request, ip, over)
        if not self.slots.acquire(blocking=False):
            self._ip_release(ip)
            return self._busy(request, ip, "all %d connection slots in use" % self.caps.conns)
        with self.ip_lock:
            self.handled.add(id(request))
        try:
            socketserver.ThreadingMixIn.process_request(self, request, client_address)
        except Exception:
            with self.ip_lock:
                self.handled.discard(id(request))
            self.slots.release()
            self._ip_release(ip)
            raise

    def process_request_thread(self, request, client_address):
        try:
            socketserver.ThreadingMixIn.process_request_thread(self, request, client_address)
        finally:
            self.slots.release()
            self._ip_release(client_address[0])

    def shutdown_request(self, request):
        # lingering close: after our response, read (and drop) what the client is still sending for a moment, so
        # closing with unread bytes does not turn into a TCP reset that destroys the response before it is read.
        # Only for connections a handler served: a refused one (outside the subnet) is closed at once, so it can
        # never hold the accept loop.
        with self.ip_lock:
            mine = id(request) in self.handled
            self.handled.discard(id(request))
        if not mine:
            self.close_request(request)
            return
        try:
            request.shutdown(socket.SHUT_WR)
            request.settimeout(0.5)
            end, got = time.monotonic() + 1.0, 0
            while got < 262144 and time.monotonic() < end:
                b = request.recv(65536)
                if not b:
                    break
                got += len(b)
        except OSError:
            pass
        self.close_request(request)

    def handle_error(self, request, client_address):
        ip = client_address[0] if client_address else "?"
        self.quiet.emit(ip, "handler error for %s" % ip, logging.ERROR, exc_info=True)

    def server_close(self):
        self.watch.stop()
        http.server.HTTPServer.server_close(self)


# ---------------------------------------------------------------------------------------------------- discovery

DISCOVER_RE = re.compile(r"%s DISCOVER nonce=([0-9a-f]{16}) mac=([0-9a-f]{32})" % MAGIC)
IP_BOUND_IF = getattr(socket, "IP_BOUND_IF", 25)  # macOS <netinet/in.h>


class Discovery(threading.Thread):
    """UDP responder. Answers only datagrams that prove knowledge of the token (an HMAC over a fresh nonce; nothing
    stable about the install is in the datagram), replies unicast with host/port + an HMAC proof the client checks.
    Everything else is dropped silently."""

    def __init__(self, token, bind, udp_port, tcp_port, quiet, base=""):
        threading.Thread.__init__(self, name="discovery", daemon=True)
        self.token = token
        self.base = base
        self.bind = bind
        self.tcp_port = tcp_port
        self.quiet = quiet
        self.stop_ev = threading.Event()
        self.seen = collections.OrderedDict()
        self.rate = {}
        s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        s.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        if bind.loopback:
            s.bind(("127.0.0.1", udp_port))
        else:
            # broadcasts are only delivered to a wildcard-bound socket on macOS, so: wildcard address, but pinned to
            # the LAN interface (IP_BOUND_IF), and every datagram's source must be inside that interface's subnet.
            s.setsockopt(socket.IPPROTO_IP, IP_BOUND_IF, socket.if_nametoindex(bind.iface))
            s.bind(("0.0.0.0", udp_port))
        s.settimeout(0.5)
        self.sock = s
        self.udp_port = s.getsockname()[1]

    def _allow(self, ip):
        now = time.monotonic()
        if len(self.rate) > 1024:
            self.rate = {k: v for k, v in self.rate.items() if v and now - v[-1] <= 1.0}
        dq = self.rate.setdefault(ip, collections.deque())
        while dq and now - dq[0] > 1.0:
            dq.popleft()
        if len(dq) >= 5:
            return False
        dq.append(now)
        return True

    def handle(self, data, addr):
        ip = addr[0]
        try:
            if ipaddress.ip_address(ip) not in self.bind.net:
                return None
        except ValueError:
            return None
        if len(data) > 200 or not self._allow(ip):
            return None
        try:
            text = data.decode("ascii")
        except UnicodeDecodeError:
            return None
        if text.endswith("\n"):
            text = text[:-1]
        m = DISCOVER_RE.fullmatch(text)
        if not m:
            return None
        nonce, mac = m.groups()
        if not hmac.compare_digest(mac, mac_of(self.token, "discover|" + nonce)):
            self.quiet.emit(ip, "discovery: bad mac from %s" % ip)
            return None
        now = time.monotonic()
        while self.seen:
            k, t = next(iter(self.seen.items()))
            if now - t > 600 or len(self.seen) > 4096:
                self.seen.popitem(last=False)
            else:
                break
        if nonce in self.seen:
            self.quiet.emit(ip, "discovery: replayed nonce from %s" % ip)
            return None
        self.seen[nonce] = now
        host, port = self.bind.ip, str(self.tcp_port)
        log.info("discovery: answered %s", ip)
        if self.base:   # revision 3: where the Codex binding's routes live; the proof covers it
            proof = mac_of(self.token, "here|%s|%s|%s|%s" % (nonce, host, port, self.base))
            return ("%s HERE host=%s port=%s nonce=%s base=%s proof=%s\n"
                    % (MAGIC, host, port, nonce, self.base, proof)).encode("ascii")
        proof = mac_of(self.token, "here|%s|%s|%s" % (nonce, host, port))
        return ("%s HERE host=%s port=%s nonce=%s proof=%s\n" % (MAGIC, host, port, nonce, proof)).encode("ascii")

    def run(self):
        while not self.stop_ev.is_set():
            try:
                data, addr = self.sock.recvfrom(512)
            except socket.timeout:
                continue
            except OSError:
                if self.stop_ev.is_set():
                    break
                time.sleep(0.2)
                continue
            reply = self.handle(data, addr)
            if reply:
                try:
                    self.sock.sendto(reply, addr)
                except OSError as e:
                    self.quiet.emit(addr[0], "discovery: reply to %s failed: %s" % (addr[0], e), logging.WARNING)

    def stop(self):
        self.stop_ev.set()
        try:
            self.sock.close()
        except OSError:
            pass


# ---------------------------------------------------------------------------------------------------- serve

def _pid_alive(pid):
    try:
        os.kill(pid, 0)
        return True
    except ProcessLookupError:
        return False
    except PermissionError:
        return True


def setup_logging(a):
    fmt = logging.Formatter("%(asctime)s %(levelname)s %(message)s")
    if a.log_file:
        lf = os.path.abspath(a.log_file)
        try:
            st = os.lstat(lf)
            if not stat.S_ISREG(st.st_mode):
                die("%s is not a regular file (a symlink?)" % lf)
        except FileNotFoundError:
            pass
        # rotates by itself: log-max-bytes x 3 files at most, whatever arrives
        h = logging.handlers.RotatingFileHandler(lf, maxBytes=a.log_max_bytes, backupCount=2, encoding="utf-8")
    else:
        h = logging.StreamHandler(sys.stderr)
    h.setFormatter(fmt)
    root = logging.getLogger()
    for old in list(root.handlers):
        root.removeHandler(old)
    root.addHandler(h)
    root.setLevel(logging.INFO)


def cmd_serve(a):
    os.umask(0o027)  # inbox files/dirs: owner rw, group r, others nothing
    root = os.path.abspath(a.root)
    if REQUIRED_MOUNT and (root + os.sep).startswith(REQUIRED_MOUNT + os.sep) and not os.path.ismount(REQUIRED_MOUNT):
        sys.stderr.write("%s devlog-receiver: %s is not mounted; not starting (would write to the boot disk)\n"
                         % (time.strftime("%Y-%m-%d %H:%M:%S"), REQUIRED_MOUNT))
        return 0  # clean exit: launchd's StartOnMount brings us back when a volume mounts
    for p in (a.port, a.udp_port):
        if p in RESERVED_PORTS:
            die("port %d belongs to another service on this Mac" % p)
    if a.base and not BASE_RE.fullmatch(a.base):
        die("--base must look like /vita/devlog (1-4 segments of [a-z0-9_-])")
    caps = Caps(a)
    token_file = a.token_file or os.path.join(root, "token")
    token = load_token(token_file)
    setup_logging(a)
    try:
        bind = resolve_bind(a.bind, a.iface)
    except NetUnavailable as e:
        log.error("%s", e)
        return EXIT_NET_CHANGED
    except ValueError as e:
        die(str(e))
    store = Store(os.path.join(root, "inbox"), caps)
    nets = [bind.net]
    if bind.loopback:
        nets = [LOOPBACK_NET]
    httpd = Receiver((bind.ip, a.port), token, store, caps, nets, a.base)
    tcp_port = httpd.server_address[1]
    disc = None
    if not a.no_discovery:
        disc = Discovery(token, bind, a.udp_port, tcp_port, httpd.quiet, a.base)
        disc.start()
    if a.pidfile:
        if os.path.exists(a.pidfile):
            try:
                old = int(open(a.pidfile).read().split()[0])
            except (ValueError, IndexError, OSError):
                old = 0
            if old and old != os.getpid() and _pid_alive(old):
                die("another receiver runs (pid %d, %s)" % (old, a.pidfile))
        with open(a.pidfile, "w") as f:
            f.write("%d %d %d\n" % (os.getpid(), tcp_port, disc.udp_port if disc else 0))
    if a.ports_file:  # tests: where the ephemeral ports landed
        with open(a.ports_file + ".tmp", "w") as f:
            f.write("%d %d\n" % (tcp_port, disc.udp_port if disc else 0))
        os.replace(a.ports_file + ".tmp", a.ports_file)

    state = {"code": 0}

    def stop(code=0):
        state["code"] = code
        threading.Thread(target=httpd.shutdown, daemon=True).start()

    signal.signal(signal.SIGTERM, lambda *_: stop(0))
    signal.signal(signal.SIGINT, lambda *_: stop(0))

    if not bind.loopback and a.bind == "auto":
        def netwatch():
            while True:
                time.sleep(30)
                try:
                    nb = resolve_bind("auto", a.iface or bind.iface)
                except ValueError:
                    nb = None
                if nb is None or nb.ip != bind.ip:
                    log.warning("LAN address changed (%s -> %s); exiting for a restart", bind.ip, nb and nb.ip)
                    stop(EXIT_NET_CHANGED)
                    return
        threading.Thread(target=netwatch, name="netwatch", daemon=True).start()

    log.info("shpdlog receiver: http://%s:%d%s  discovery udp %s:%s  iface=%s net=%s  kid=%s  inbox=%s",
             bind.ip, tcp_port, (" binding=codex base=" + a.base) if a.base else "",
             "127.0.0.1" if bind.loopback else "0.0.0.0%" + bind.iface,
             disc.udp_port if disc else "off", bind.iface, bind.net, kid_of(token), store.inbox)
    log.info("caps: file=%d run=%d files/run=%d day=%d total=%d (in use %d, today %d) new runs/day=%d conns=%d "
             "per-ip=%d rate=%d/min header-timeout=%gs", caps.file, caps.run, caps.files_per_run, caps.day,
             caps.total, store.total, store.day_bytes, caps.new_runs, caps.conns, caps.per_ip, caps.rate,
             caps.header_timeout)
    if ignores_ownership(token_file):
        log.warning("%s is on a volume that ignores ownership (noowners): its 0600 mode does not keep other local "
                    "accounts out. Single-account Mac: fine. Otherwise keep the token on the boot disk and pass "
                    "--token-file (README: Token).", token_file)
    try:
        httpd.serve_forever(poll_interval=0.5)
    finally:
        if disc:
            disc.stop()
        httpd.server_close()
        if a.pidfile:
            try:
                if open(a.pidfile).read().split()[0] == str(os.getpid()):
                    os.unlink(a.pidfile)
            except (OSError, IndexError):
                pass
        log.info("stopped (exit %d)", state["code"])
    return state["code"]


# ---------------------------------------------------------------------------------------------------- other commands

def vita_config_text(token, host_hint):
    return (
        "# Shepherd devlog config -> copy to the Vita as ux0:data/shepherd/devlog.txt\n"
        "# (generated by tools/devlog-receiver/receiver.py gen-token; keep it private: it holds the token)\n"
        "enabled=1\n"
        "token=%s\n"
        "# mode=ask: after a run the app asks before anything is sent (X send, CIRCLE delete, START later).\n"
        "# mode=auto: sends at the end of a run, and runs kept from earlier launches at the next start.\n"
        "# mode=off: never (like no file). Without a mode= line: ask.\n"
        "mode=ask\n"
        "# clear=1 once: the next Shepherd app start deletes the kept runs and its previous log, then sets clear=0.\n"
        "clear=0\n"
        "# Without server= the Vita finds the Mac by UDP discovery on the Wi-Fi (the Archive Codex server first, this\n"
        "# receiver second). If discovery does not work on your network, uncomment one (the Mac's LAN IP):\n"
        "# server=%s:8770\n"
        "# server=%s:%d\n" % (token, host_hint or "192.168.x.y", host_hint or "192.168.x.y", DEFAULT_TCP_PORT))


def cmd_gen_token(a):
    root = os.path.abspath(a.root)
    path = a.token_file or os.path.join(root, "token")
    os.makedirs(os.path.dirname(path), mode=0o750, exist_ok=True)
    if os.path.lexists(path) and not a.rotate:
        tok = load_token(path)
        print("token exists: %s (kid %s); --rotate to replace it" % (path, kid_of(tok)))
    else:
        tok = secrets.token_hex(32)
        write_private(path, tok + "\n")
        print("wrote %s (0600), kid %s" % (path, kid_of(tok)))
    if ignores_ownership(path):
        print("note: %s is on a volume that ignores ownership: fine on a one-account Mac (README: Token)" % path)
    hint = None
    try:
        hint = resolve_bind("auto", None).ip
    except ValueError:
        pass
    vpath = a.vita_config or os.path.join(root, "vita-devlog.txt")
    write_private(vpath, vita_config_text(tok, hint))
    print("wrote %s (0600): copy it to the Vita as ux0:data/shepherd/devlog.txt" % vpath)
    if a.show:
        print(tok)
    return 0


def cmd_kid(a):
    print(kid_of(load_token(a.token_file or os.path.join(os.path.abspath(a.root), "token"))))
    return 0


def cmd_list(a):
    inbox = os.path.join(os.path.abspath(a.root), "inbox")
    if not os.path.isdir(inbox):
        print("no inbox at %s" % inbox)
        return 0
    days = sorted((d for d in os.listdir(inbox) if re.fullmatch(r"\d{4}-\d\d-\d\d", d)), reverse=True)[:a.days]
    rows = 0
    for d in days:
        for app in sorted(os.listdir(os.path.join(inbox, d))):
            ad = os.path.join(inbox, d, app)
            if not os.path.isdir(ad):
                continue
            for run in sorted(os.listdir(ad)):
                rd = os.path.join(ad, run)
                man = _read_json(os.path.join(rd, "manifest.json"), {}) or {}
                meta = (_read_json(os.path.join(rd, "meta.json"), {}) or {}).get("meta", {})
                files = man.get("files", {})
                print(_safe("%s  %-10s %-34s %-8s %-9s %-9s %-6s %s" % (
                    d, app, run, man.get("outcome") or meta.get("outcome") or "?",
                    "done" if man.get("done") else "partial", meta.get("model", ""), meta.get("fw", ""),
                    ", ".join("%s(%d)" % (k, v.get("bytes", 0)) for k, v in sorted(files.items()))), 400))
                rows += 1
    if not rows:
        print("no runs in the last %d day folders" % a.days)
    return 0


def main(argv=None):
    p = argparse.ArgumentParser(prog="receiver.py", description="Shepherd devlog receiver (LAN only)")
    p.add_argument("--root", default=DEFAULT_ROOT, help="state dir (token, inbox/); default %(default)s")
    p.add_argument("--token-file", default=None, help="default <root>/token")
    sub = p.add_subparsers(dest="cmd")

    s = sub.add_parser("serve", help="run the receiver in the foreground")
    s.add_argument("--bind", default="auto", help="auto (LAN IPv4 of the default route), a LAN IP, or 127.0.0.1")
    s.add_argument("--iface", default=None, help="interface for --bind auto (default: the default route's)")
    s.add_argument("--port", type=int, default=DEFAULT_TCP_PORT)
    s.add_argument("--udp-port", type=int, default=DEFAULT_UDP_PORT)
    s.add_argument("--no-discovery", action="store_true")
    s.add_argument("--base", default="", help="speak the Codex binding under this base path (PROTOCOL.md §10), "
                   "e.g. /vita/devlog: for tests; the Archive Codex server does this for real")
    s.add_argument("--pidfile", default=None)
    s.add_argument("--log-file", default=None, help="rotating log file (default: stderr)")
    s.add_argument("--log-max-bytes", type=mib, default=mib("5M"), help=argparse.SUPPRESS)
    s.add_argument("--ports-file", default=None, help=argparse.SUPPRESS)
    s.add_argument("--max-file", type=mib, default=mib("8M"))
    s.add_argument("--max-run", type=mib, default=mib("32M"))
    s.add_argument("--max-files", type=int, default=32)
    s.add_argument("--max-meta", type=mib, default=mib("8K"))
    s.add_argument("--max-day", type=mib, default=mib("256M"))
    s.add_argument("--max-total", type=mib, default=mib("4G"))
    s.add_argument("--max-new-runs", type=int, default=1000, help="new runs per day")
    s.add_argument("--min-free", type=mib, default=mib("2G"), help="refuse uploads below this much free space")
    s.add_argument("--max-conns", type=int, default=8)
    s.add_argument("--max-per-ip", type=int, default=2, help="connections at a time from one address")
    s.add_argument("--rate", type=int, default=120, help="requests per minute per source IP")
    s.add_argument("--idle-timeout", type=float, default=15.0)
    s.add_argument("--header-timeout", type=float, default=8.0, help="accept -> end of headers")
    s.add_argument("--request-deadline", type=float, default=120.0)
    s.add_argument("--ban-after", type=int, default=10, help=argparse.SUPPRESS)  # bad auths/min -> 10 min ban

    g = sub.add_parser("gen-token", help="create the token (0600) and a devlog.txt for the Vita")
    g.add_argument("--rotate", action="store_true", help="replace an existing token")
    g.add_argument("--vita-config", default=None, help="default <root>/vita-devlog.txt")
    g.add_argument("--show", action="store_true", help="also print the token")

    sub.add_parser("kid", help="print the token's fingerprint")
    li = sub.add_parser("list", help="list received runs")
    li.add_argument("--days", type=int, default=3)

    a = p.parse_args(argv)
    if a.cmd == "serve":
        return cmd_serve(a)
    if a.cmd == "gen-token":
        return cmd_gen_token(a)
    if a.cmd == "kid":
        return cmd_kid(a)
    if a.cmd == "list":
        return cmd_list(a)
    p.print_help()
    return 2


if __name__ == "__main__":
    sys.exit(main())
