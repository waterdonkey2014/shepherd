#!/usr/bin/env python3
"""Reference client for the Shepherd devlog receiver (shpdlog/1 revision 3). Standard library only.

The receiver's tests use it, and it is handy on the Mac:

  shpdlog_client.py --token-file $SHEPHERD_WORK/devlog/token --discover 192.0.2.125 \\
      --app logtest --meta title_id=SHPD00014 --meta outcome=ok some.log other.txt

It is not the Vita client (that is vita/devlog/devlog.c); it speaks the same protocol: the token never goes on the
wire (each request carries an HMAC over method, target, a fresh nonce and the body's SHA-256), and the ping answer's
proof is checked before anything else is sent.
"""

from __future__ import annotations

import argparse
import hashlib
import hmac
import http.client
import os
import re
import secrets
import socket
import sys
import time

MAGIC = "SHPDLOG1"
HERE_RE = re.compile(r"%s HERE host=(\d+\.\d+\.\d+\.\d+) port=(\d{1,5}) nonce=([0-9a-f]{16})"
                     r"(?: base=((?:/[a-z0-9_-]{1,32}){1,4}))? proof=([0-9a-f]{32})\n?" % MAGIC)
PROOF_RE = re.compile(r"proof=([0-9a-f]{32})")


def kid_of(token):
    """The token's fingerprint, for people (never sent)."""
    return hashlib.sha256(token.encode("ascii")).hexdigest()[:16]


def mac_of(token, msg):
    return hmac.new(token.encode("ascii"), msg.encode("ascii"), hashlib.sha256).hexdigest()[:32]


def auth_header(token, method, target, body=b"", nonce=None, body_sha=None):
    """-> (Authorization value, nonce). body_sha overrides the hash (tests: a body that does not match it)."""
    nonce = nonce or secrets.token_hex(8)
    sha = body_sha or hashlib.sha256(body or b"").hexdigest()
    mac = mac_of(token, "req|%s|%s|%s|%s" % (method, target, nonce, sha))
    return "SHPD1 nonce=%s body=%s mac=%s" % (nonce, sha, mac), nonce


def ping_proof_ok(token, nonce, host, port, body):
    m = PROOF_RE.search(body or "")
    return bool(m) and hmac.compare_digest(m.group(1), mac_of(token, "pong|%s|%s|%d" % (nonce, host, port)))


def discover_request(token, nonce=None):
    nonce = nonce or secrets.token_hex(8)
    msg = "%s DISCOVER nonce=%s mac=%s\n" % (MAGIC, nonce, mac_of(token, "discover|" + nonce))
    return nonce, msg.encode("ascii")


def check_reply(token, nonce, data, with_base=False):
    """-> (host, port) (with_base: (host, port, base), base "" for a standalone receiver) if data is a valid HERE
    for nonce, else None. With base= (the Codex binding, PROTOCOL.md §10) the proof covers the base too."""
    try:
        m = HERE_RE.fullmatch(data.decode("ascii"))
    except UnicodeDecodeError:
        return None
    if not m:
        return None
    host, port, rnonce, base, proof = m.groups()
    if rnonce != nonce:
        return None
    msg = "here|%s|%s|%s" % (nonce, host, port) + ("|" + base if base else "")
    if not hmac.compare_digest(proof, mac_of(token, msg)):
        return None
    return (host, int(port), base or "") if with_base else (host, int(port))


def discover(token, target="255.255.255.255", port=47711, tries=2, wait=0.3, with_base=False):
    s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    try:
        s.setsockopt(socket.SOL_SOCKET, socket.SO_BROADCAST, 1)
        s.settimeout(wait)
        for _ in range(tries):
            nonce, msg = discover_request(token)
            s.sendto(msg, (target, port))
            end = time.monotonic() + wait
            while time.monotonic() < end:
                try:
                    data, _ = s.recvfrom(512)
                except socket.timeout:
                    break
                hp = check_reply(token, nonce, data, with_base)
                if hp:
                    return hp
        return None
    finally:
        s.close()


class Client:
    """base="" speaks the standalone receiver's paths; base="/vita/devlog" the Codex binding (PROTOCOL.md §10)."""

    def __init__(self, host, port, token, timeout=5.0, base=""):
        self.host, self.port, self.token, self.timeout, self.base = host, port, token, timeout, base
        self.last_nonce = None

    def target(self, op, app=None, run=None, name=None):
        if not self.base:
            if op == "ping":
                return "/v1/ping"
            if op == "file":
                return "/v1/runs/%s/%s/files/%s" % (app, run, name)
            return "/v1/runs/%s/%s/%s" % (app, run, op)
        if op == "ping":
            return self.base + "/v1/ping"
        return "%s/v1/upload?op=%s&app=%s&run=%s%s" % (self.base, op, app, run, "&name=" + name if name else "")

    def request(self, method, path, body=None, headers=None, chunked=False, nonce=None, body_sha=None):
        c = http.client.HTTPConnection(self.host, self.port, timeout=self.timeout)
        try:
            auth, self.last_nonce = auth_header(self.token, method, path, body or b"", nonce, body_sha)
            h = {"Authorization": auth, "User-Agent": "shpdlog-client-py/1",
                 "Content-Type": "text/plain; charset=utf-8", "Connection": "close"}
            h.update(headers or {})
            if chunked:
                c.putrequest(method, path)
                for k, v in h.items():
                    c.putheader(k, v)
                c.putheader("Transfer-Encoding", "chunked")
                c.endheaders()
                data = body or b""
                for i in range(0, len(data), 4096):
                    part = data[i:i + 4096]
                    c.send(b"%x\r\n%s\r\n" % (len(part), part))
                c.send(b"0\r\n\r\n")
            else:
                c.request(method, path, body=body, headers=h)
            r = c.getresponse()
            return r.status, r.read(4096).decode("utf-8", "replace")
        finally:
            c.close()

    def ping(self):
        return self.request("GET", self.target("ping"))

    def ping_verified(self):
        """-> (status, body, proof_ok): proof_ok says the answer came from a receiver that knows the token."""
        code, body = self.ping()
        return code, body, code == 200 and ping_proof_ok(self.token, self.last_nonce, self.host, self.port, body)

    def meta(self, app, run, kv):
        body = "".join("%s=%s\n" % (k, v) for k, v in kv.items()).encode("utf-8")
        return self.request("POST", self.target("meta", app, run), body)

    def file(self, app, run, name, data, chunked=False, headers=None, body_sha=None):
        return self.request("POST", self.target("file", app, run, name), data, headers, chunked,
                            body_sha=body_sha)

    def done(self, app, run, outcome=None):
        return self.request("POST", self.target("done", app, run),
                            ("outcome=%s\n" % outcome).encode() if outcome else b"")


def new_run_id():
    return time.strftime("%Y%m%dT%H%M%SZ", time.gmtime()) + "-" + secrets.token_hex(6)


def main(argv=None):
    p = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    p.add_argument("--token-file", default=os.path.join(os.environ.get("SHEPHERD_WORK") or os.path.expanduser("~/shepherd-work"), "devlog", "token"))
    p.add_argument("--server", help="host:port (skips discovery)")
    p.add_argument("--base", default=None, help="Codex binding base path (default: from discovery; with --server: none)")
    p.add_argument("--discover", default="255.255.255.255", help="discovery target (broadcast or unicast IP)")
    p.add_argument("--discovery-port", type=int, default=47711)
    p.add_argument("--app", default="logtest")
    p.add_argument("--run", default=None)
    p.add_argument("--meta", action="append", default=[], help="key=value (repeatable)")
    p.add_argument("--chunked", action="store_true")
    p.add_argument("files", nargs="*")
    a = p.parse_args(argv)
    token = open(a.token_file).read().strip()
    if a.server:
        host, port = a.server.rsplit(":", 1)
        hp = (host, int(port), a.base or "")
    else:
        hp = discover(token, a.discover, a.discovery_port, with_base=True)
        if not hp:
            print("discovery: no (valid) answer from %s:%d" % (a.discover, a.discovery_port))
            return 1
        print("discovered %s:%d%s" % (hp[0], hp[1], (" base " + hp[2]) if hp[2] else ""))
        if a.base is not None:
            hp = (hp[0], hp[1], a.base)
    c = Client(hp[0], hp[1], token, base=hp[2])
    code, body, proof_ok = c.ping_verified()
    print("ping", code, body.strip(), "proof ok" if proof_ok else "PROOF BAD")
    if not proof_ok:
        print("not a receiver that knows this token: nothing sent")
        return 1
    run = a.run or new_run_id()
    kv = {"proto": "1", "client": "shpdlog-client-py/1", "platform": "mac", "app": a.app}
    kv.update(dict(m.split("=", 1) for m in a.meta))
    kv["files"] = ",".join(os.path.basename(f) for f in a.files)
    print("meta", *c.meta(a.app, run, kv))
    for f in a.files:
        with open(f, "rb") as fh:
            print("file", os.path.basename(f), *c.file(a.app, run, os.path.basename(f), fh.read(), a.chunked))
    print("done", *c.done(a.app, run, kv.get("outcome")))
    print("run", run)
    return 0


if __name__ == "__main__":
    sys.exit(main())
