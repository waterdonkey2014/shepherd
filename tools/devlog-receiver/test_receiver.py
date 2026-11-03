#!/usr/bin/env python3
"""Protocol + hardening tests for receiver.py. Standard library only.

Every receiver these tests start binds 127.0.0.1 on ephemeral ports, writes under a throw-away root
(default $SHEPHERD_WORK/devlog/e2e/py-<stamp>/, or $SHPD_TEST_ROOT), and is stopped at the end.

  python3 tools/devlog-receiver/test_receiver.py [-v]
"""

from __future__ import annotations

import datetime as dt
import hashlib
import http.client
import json
import os
import re
import secrets
import signal
import socket
import stat
import subprocess
import sys
import time
import unittest

HERE = os.path.dirname(os.path.abspath(__file__))
sys.dont_write_bytecode = True   # no __pycache__ next to the sources
sys.path.insert(0, HERE)
import shpdlog_client as C  # noqa: E402

RECEIVER = os.path.join(HERE, "receiver.py")
PY = sys.executable
ROOT = os.environ.get("SHPD_TEST_ROOT") or os.path.join(os.environ.get("SHEPHERD_WORK") or os.path.expanduser("~/shepherd-work"), "devlog/e2e/py-%s-%d") % (
    time.strftime("%Y%m%d-%H%M%S"), os.getpid())
TODAY = dt.date.today().isoformat()
INSTANCES = []


class Instance:
    """One receiver process on 127.0.0.1 with its own root."""

    def __init__(self, name, *extra):
        self.name = name
        self.root = os.path.join(ROOT, name)
        os.makedirs(self.root, exist_ok=True)
        self.token = secrets.token_hex(32)
        tf = os.path.join(self.root, "token")
        fd = os.open(tf, os.O_WRONLY | os.O_CREAT | os.O_TRUNC, 0o600)
        os.write(fd, (self.token + "\n").encode())
        os.close(fd)
        os.chmod(tf, 0o600)
        self.ports_file = os.path.join(self.root, "ports")
        if os.path.exists(self.ports_file):
            os.unlink(self.ports_file)
        self.pidfile = os.path.join(self.root, "receiver.pid")
        self.logf = open(os.path.join(self.root, "receiver.log"), "ab")
        extra = [os.path.join(self.root, "rx.log") if x == "LOGFILE" else x for x in extra]
        args = [PY, RECEIVER, "--root", self.root, "serve", "--bind", "127.0.0.1", "--port", "0",
                "--udp-port", "0", "--ports-file", self.ports_file, "--pidfile", self.pidfile,
                "--min-free", "1M"] + list(extra)
        self.proc = subprocess.Popen(args, stdout=self.logf, stderr=subprocess.STDOUT)
        INSTANCES.append(self)
        end = time.monotonic() + 10
        while not os.path.exists(self.ports_file):
            if self.proc.poll() is not None or time.monotonic() > end:
                raise RuntimeError("receiver %s did not start:\n%s" % (name, self.log_text()))
            time.sleep(0.05)
        self.port, self.udp = (int(x) for x in slurp(self.ports_file).split())
        self.client = C.Client("127.0.0.1", self.port, self.token, timeout=5)
        self.inbox = os.path.join(self.root, "inbox")

    def log_text(self):
        if not self.logf.closed:
            self.logf.flush()
        with open(os.path.join(self.root, "receiver.log"), "rb") as f:
            return f.read().decode("utf-8", "replace")[-4000:]

    def stop(self):
        if self.proc.poll() is None:
            self.proc.send_signal(signal.SIGTERM)
            try:
                self.proc.wait(10)
            except subprocess.TimeoutExpired:
                self.proc.kill()
                self.proc.wait(5)
        self.logf.close()
        return self.proc.returncode

    def run_dir(self, app, run):
        return os.path.join(self.inbox, TODAY, app, run)

    def raw(self, data, read=True, timeout=5.0):
        """Send raw bytes on a fresh connection, return the response bytes (until close)."""
        s = socket.create_connection(("127.0.0.1", self.port), timeout=timeout)
        try:
            s.sendall(data)
            if not read:
                return b""
            out = b""
            while True:
                try:
                    b = s.recv(65536)
                except (ConnectionResetError, socket.timeout):
                    break
                if not b:
                    break
                out += b
            return out
        finally:
            s.close()

    def status_of_raw(self, data):
        resp = self.raw(data)
        try:
            return int(resp.split(b" ", 2)[1])
        except (IndexError, ValueError):
            return None


def slurp(path, mode="r"):
    with open(path, mode) as f:
        return f.read()


def rid(tag=""):
    return C.new_run_id() + tag


def req(method, path, token, headers=b"", body=b"", auth_body=None, nonce=None):
    """A raw request authenticated the shpdlog way (HMAC over method, target, nonce, body hash); auth_body: the
    bytes the auth hash is computed over when they differ from what is sent (framing tests)."""
    auth, _ = C.auth_header(token, method, path, body if auth_body is None else auth_body, nonce)
    h = b"%s %s HTTP/1.1\r\nHost: x\r\nAuthorization: %s\r\n" % (method.encode(), path.encode(), auth.encode())
    return h + headers + b"Connection: close\r\n\r\n" + body


MAIN = None


def setUpModule():
    global MAIN
    os.makedirs(ROOT, exist_ok=True)
    MAIN = Instance("main", "--max-file", "64K", "--max-run", "160K", "--max-files", "4",
                    "--idle-timeout", "3", "--request-deadline", "20", "--rate", "100000", "--ban-after", "100000")


def tearDownModule():
    for inst in INSTANCES:
        inst.stop()


class T01Auth(unittest.TestCase):
    def test_ping_auth(self):
        c = C.Client("127.0.0.1", MAIN.port, "0" * 64)
        self.assertEqual(c.ping()[0], 401)
        self.assertEqual(MAIN.status_of_raw(b"GET /v1/ping HTTP/1.1\r\nHost: x\r\nConnection: close\r\n\r\n"), 401)
        for scheme in (b"Basic", b"Bearer"):   # the token itself is never a credential on the wire
            self.assertEqual(MAIN.status_of_raw(
                b"GET /v1/ping HTTP/1.1\r\nHost: x\r\nAuthorization: %s %s\r\nConnection: close\r\n\r\n"
                % (scheme, MAIN.token.encode())), 401, scheme)
        code, body, proof_ok = MAIN.client.ping_verified()
        self.assertEqual(code, 200)
        self.assertRegex(body, r"^ok shpdlog/1 max_file=65536 max_run=163840 proof=[0-9a-f]{32}\n$")
        self.assertTrue(proof_ok, "the ping answer proves the receiver knows the token")
        n = MAIN.client.last_nonce
        self.assertFalse(C.ping_proof_ok("1" * 64, n, "127.0.0.1", MAIN.port, body), "proof under another token")
        self.assertFalse(C.ping_proof_ok(MAIN.token, n, "127.0.0.1", MAIN.port + 1, body), "proof bound to host:port")
        self.assertFalse(C.ping_proof_ok(MAIN.token, "0" * 16, "127.0.0.1", MAIN.port, body), "proof bound to nonce")

    def test_mac_binds_method_target_nonce(self):
        tok = MAIN.token
        run = rid()
        # a MAC made for another target / method is refused
        auth, _ = C.auth_header(tok, "GET", "/v1/ping")
        self.assertEqual(MAIN.status_of_raw(
            b"POST /v1/runs/logtest/%s/done HTTP/1.1\r\nHost: x\r\nAuthorization: %s\r\nContent-Length: 0\r\n"
            b"Connection: close\r\n\r\n" % (run.encode(), auth.encode())), 401)
        auth, _ = C.auth_header(tok, "POST", "/v1/ping")
        self.assertEqual(MAIN.status_of_raw(
            b"GET /v1/ping HTTP/1.1\r\nHost: x\r\nAuthorization: %s\r\nConnection: close\r\n\r\n" % auth.encode()), 401)
        # replay: the same request (same nonce) is accepted once
        nonce = secrets.token_hex(8)
        r1 = req("GET", "/v1/ping", tok, nonce=nonce)
        self.assertEqual(MAIN.status_of_raw(r1), 200)
        self.assertEqual(MAIN.status_of_raw(r1), 401)
        # malformed auth values
        for v in (b"SHPD1 nonce=zz body=%s mac=%s" % (b"0" * 64, b"0" * 32), b"SHPD1", b"SHPD1 nonce=%s" % (b"a" * 16)):
            self.assertEqual(MAIN.status_of_raw(
                b"GET /v1/ping HTTP/1.1\r\nHost: x\r\nAuthorization: %s\r\nConnection: close\r\n\r\n" % v), 401)

    def test_body_hash_binds_the_body(self):
        run = rid()
        good = b"hello\n"
        self.assertEqual(MAIN.client.file("logtest", run, "s.txt", good)[0], 200)
        # authenticated for one body, another one sent: 409, nothing replaced
        self.assertEqual(MAIN.client.file("logtest", run, "s.txt", b"evil\n",
                                          body_sha=hashlib.sha256(good).hexdigest())[0], 409)
        self.assertEqual(slurp(os.path.join(MAIN.run_dir("logtest", run), "files", "s.txt"), "rb"), good)
        self.assertEqual(MAIN.status_of_raw(req("POST", "/v1/runs/logtest/%s/meta" % run, MAIN.token,
                                                b"Content-Length: 5\r\n", b"a=1\nb", auth_body=b"a=2\nb")), 409)
        self.assertEqual(MAIN.status_of_raw(req("GET", "/v1/ping", MAIN.token, auth_body=b"x")), 400)

    def test_token_never_on_the_wire(self):
        srv = socket.socket()
        srv.bind(("127.0.0.1", 0))
        srv.listen(1)
        got = []

        def take():
            c, _ = srv.accept()
            c.settimeout(2)
            data = b""
            try:
                while b"\r\n\r\n" not in data:
                    b = c.recv(4096)
                    if not b:
                        break
                    data += b
                c.sendall(b"HTTP/1.1 200 OK\r\nContent-Length: 16\r\nConnection: close\r\n\r\nok shpdlog/1 x\n\n")
            finally:
                c.close()
            got.append(data)
        import threading
        t = threading.Thread(target=take)
        t.start()
        code, body, proof_ok = C.Client("127.0.0.1", srv.getsockname()[1], MAIN.token).ping_verified()
        t.join(5)
        srv.close()
        self.assertEqual(code, 200)
        self.assertFalse(proof_ok, "an impostor cannot prove it knows the token")
        self.assertTrue(got and b"Authorization: SHPD1 " in got[0])
        self.assertNotIn(MAIN.token.encode(), got[0])

    def test_server_header_is_bland(self):
        resp = MAIN.raw(req("GET", "/v1/ping", MAIN.token))
        self.assertIn(b"\r\nServer: shpdlog\r\n", resp)
        self.assertNotIn(b"Python", resp)

    def test_keepalive(self):
        c = http.client.HTTPConnection("127.0.0.1", MAIN.port, timeout=5)
        for _ in range(3):
            c.request("GET", "/v1/ping", headers={"Authorization": C.auth_header(MAIN.token, "GET", "/v1/ping")[0]})
            r = c.getresponse()
            self.assertEqual(r.status, 200)
            r.read()
        c.close()


class T02Upload(unittest.TestCase):
    def test_full_run(self):
        run = rid()
        cl = MAIN.client
        meta = {"proto": "1", "client": "devlog-c/1", "platform": "mac", "app": "logtest", "title_id": "SHPD00014",
                "build": "20261007-153000-ff30827", "commit": "ff30827", "fw": "3.65", "model": "PCH-2000",
                "free_mem_kb": "198656", "free_cdram_kb": "112000", "free_phycont_kb": "26000", "outcome": "fail",
                "detail": "FAIL gpu: FBO incomplete", "started": "2026-10-07T15:30:00Z", "uptime_ms": "61234",
                "attempt": "2", "late": "1", "files": "logtest.txt,logtest.prev.txt,frames.tsv"}
        self.assertEqual(cl.meta("logtest", run, meta), (200, "ok\n"))
        a = b"BEGIN logtest\n" + b"line \x1b[1mbold\x1b[0m\ttab\r\n" * 500 + b"END\n"
        b = os.urandom(3000).hex().encode() + b"\n"
        t = b"frame\tms\n" + b"".join(b"%d\t16.6\n" % i for i in range(2000))
        for name, data, chunked in (("logtest.txt", a, False), ("logtest.prev.txt", b, True), ("frames.tsv", t, False)):
            code, body = cl.file("logtest", run, name, data, chunked=chunked,
                                 headers={"X-Shpd-Sanitized": "0", "X-Shpd-File-Size": str(len(data)),
                                          "X-Shpd-Offset": "0"})
            self.assertEqual(code, 200, body)
            self.assertEqual(body, "ok bytes=%d sha256=%s\n" % (len(data), hashlib.sha256(data).hexdigest()))
        self.assertEqual(cl.done("logtest", run, "fail"), (200, "ok\n"))
        rd = MAIN.run_dir("logtest", run)
        self.assertEqual(slurp(os.path.join(rd, "files", "logtest.txt"), "rb"), a)
        self.assertEqual(slurp(os.path.join(rd, "files", "logtest.prev.txt"), "rb"), b)
        self.assertEqual(slurp(os.path.join(rd, "files", "frames.tsv"), "rb"), t)
        man = json.loads(slurp(os.path.join(rd, "manifest.json")))
        self.assertTrue(man["done"])
        self.assertEqual(man["outcome"], "fail")
        self.assertEqual(sorted(man["files"]), ["frames.tsv", "logtest.prev.txt", "logtest.txt"])
        self.assertEqual(man["files"]["logtest.txt"]["sha256"], hashlib.sha256(a).hexdigest())
        self.assertEqual(man["files"]["logtest.txt"]["file_size"], len(a))
        m = json.loads(slurp(os.path.join(rd, "meta.json")))
        self.assertEqual(m["meta"], meta)
        idx = [json.loads(l) for l in slurp(os.path.join(MAIN.inbox, "index.jsonl")).splitlines()]
        evs = [e["ev"] for e in idx if e.get("run") == run]
        self.assertEqual(evs, ["meta", "file", "file", "file", "done"])
        self.assertNotIn("127.0.0.1", slurp(os.path.join(MAIN.inbox, "index.jsonl")))
        self.assertEqual([x for x in os.listdir(os.path.join(rd, "files")) if x.startswith(".")], [])

    def test_reupload_replaces_and_meta_merges(self):
        run = rid()
        cl = MAIN.client
        self.assertEqual(cl.file("logtest", run, "x.log", b"one\n")[0], 200)
        self.assertEqual(cl.file("logtest", run, "x.log", b"two two\n")[0], 200)
        self.assertEqual(cl.meta("logtest", run, {"outcome": "pending", "fw": "3.60"})[0], 200)
        self.assertEqual(cl.meta("logtest", run, {"outcome": "ok"})[0], 200)
        rd = MAIN.run_dir("logtest", run)
        self.assertEqual(slurp(os.path.join(rd, "files", "x.log"), "rb"), b"two two\n")
        m = json.loads(slurp(os.path.join(rd, "meta.json")))["meta"]
        self.assertEqual(m, {"outcome": "ok", "fw": "3.60"})
        man = json.loads(slurp(os.path.join(rd, "manifest.json")))
        self.assertEqual(list(man["files"]), ["x.log"])

    def test_empty_file_and_done_without_body(self):
        run = rid()
        self.assertEqual(MAIN.client.file("logtest", run, "empty.txt", b"")[0], 200)
        self.assertEqual(MAIN.status_of_raw(req("POST", "/v1/runs/logtest/%s/done" % run, MAIN.token)), 200)

    def test_done_unknown_run(self):
        self.assertEqual(MAIN.client.done("logtest", rid("-nope"))[0], 404)


class T03Paths(unittest.TestCase):
    BAD = [
        "/v1/runs/logtest/{run}/files/../../x.txt",
        "/v1/runs/logtest/{run}/files/..%2f..%2fx.txt",
        "/v1/runs/logtest/{run}/files/%2e%2e%2fx.txt",
        "/v1/runs/logtest/{run}/files/.hidden.txt",
        "/v1/runs/logtest/{run}/files/x.exe",
        "/v1/runs/logtest/{run}/files/x.txt.sh",
        "/v1/runs/logtest/{run}/files/x.sfo",
        "/v1/runs/logtest/{run}/files/a/b.txt",
        "/v1/runs/logtest/{run}/files/x..txt",
        "/v1/runs/logtest/{run}/files/",
        "/v1/runs/logtest/{run}/files/" + "a" * 70 + ".txt",
        "/v1/runs/logtest/{run}/files/x.txt?y=1",
        "/v1/runs/logtest/{run}/files/x.txt/",
        "/v1/runs/logtest/{run}/files/x%00.txt",
        "/v1/runs/../etc/{run}/meta",
        "/v1/runs/Logtest/{run}/meta",
        "/v1/runs/log.test/{run}/meta",
        "/v1/runs/logtest/abc/meta",
        "/v1/runs/logtest/..{run}/meta",
        "/v1/runs/logtest/{run}/../meta",
        "/v1/runs/logtest/{run}/manifest",
        # ("//v1/..." is not here: http.server folds leading slashes to one, so it IS /v1/...; harmless)
        "http://127.0.0.1/v1/runs/logtest/{run}/meta",
        "/v1/../v1/runs/logtest/{run}/meta",
        "/",
        "/index.jsonl",
    ]

    def test_bad_paths(self):
        before = snapshot(MAIN.root)
        run = rid()
        for p in self.BAD:
            path = p.format(run=run)
            code = MAIN.status_of_raw(req("POST", path, MAIN.token, b"Content-Length: 4\r\n", b"abc\n"))
            self.assertIn(code, (400, 404), path)
        after = snapshot(MAIN.root)
        new = sorted(set(after) - set(before))
        self.assertEqual([n for n in new if not n.startswith("receiver.log")], [], "files created by bad paths")


class T04Content(unittest.TestCase):
    def test_binary_rejected(self):
        run = rid()
        for data in (b"text\x00more\n", b"\x7fELF" + b"\x01\x02\x03" * 100, b"PK\x03\x04" + b"\x00" * 50,
                     b"\x00PSF\x01\x01"):
            self.assertEqual(MAIN.client.file("logtest", run, "bin.txt", data)[0], 415)
        # under 0.5 % control bytes is fine, over is not
        ok = b"a" * 1000 + b"\x01"
        self.assertEqual(MAIN.client.file("logtest", run, "ctl.txt", ok)[0], 200)
        bad = b"a" * 100 + b"\x01\x02"
        self.assertEqual(MAIN.client.file("logtest", run, "ctl2.txt", bad)[0], 415)
        files = os.listdir(os.path.join(MAIN.run_dir("logtest", run), "files"))
        self.assertEqual(files, ["ctl.txt"])

    def test_size_caps(self):
        run = rid()
        # Content-Length over the cap: refused from the headers alone
        self.assertEqual(MAIN.status_of_raw(req("POST", "/v1/runs/logtest/%s/files/big.txt" % run, MAIN.token,
                                                b"Content-Length: 70000\r\n")), 413)
        # chunked over the cap: refused mid-body, no partial file left
        self.assertEqual(MAIN.client.file("logtest", run, "big2.txt", b"x" * 70000, chunked=True)[0], 413)
        self.assertFalse(os.path.exists(os.path.join(MAIN.run_dir("logtest", run), "files", "big2.txt")))
        leftovers = [f for f in os.listdir(os.path.join(MAIN.run_dir("logtest", run), "files")) if f.startswith(".")] \
            if os.path.isdir(MAIN.run_dir("logtest", run)) else []
        self.assertEqual(leftovers, [])
        # run cap 160K: 60K + 60K fit, a third 60K does not (only 40K left); a 30K one does
        run2 = rid()
        for n in ("a.txt", "b.txt"):
            self.assertEqual(MAIN.client.file("logtest", run2, n, b"y" * 60000)[0], 200)
        self.assertEqual(MAIN.client.file("logtest", run2, "c.txt", b"y" * 60000)[0], 413)
        self.assertEqual(MAIN.client.file("logtest", run2, "c.txt", b"y" * 30000)[0], 200)
        # replacing a.txt with the same size still fits (its own old bytes do not count)
        self.assertEqual(MAIN.client.file("logtest", run2, "a.txt", b"z" * 60000)[0], 200)
        # files per run: 4
        self.assertEqual(MAIN.client.file("logtest", run2, "d.txt", b"1\n")[0], 200)
        self.assertEqual(MAIN.client.file("logtest", run2, "e.txt", b"1\n")[0], 413)
        # meta cap 8K
        self.assertEqual(MAIN.client.meta("logtest", run2, {"k%d" % i: "v" * 200 for i in range(50)})[0], 413)

    def test_framing(self):
        run = rid()
        path = "/v1/runs/logtest/%s/files/f.txt" % run
        self.assertEqual(MAIN.status_of_raw(req("POST", path, MAIN.token,
                                                b"Content-Length: 4\r\nTransfer-Encoding: chunked\r\n",
                                                b"4\r\nabc\n\r\n0\r\n\r\n")), 400)
        self.assertEqual(MAIN.status_of_raw(req("POST", path, MAIN.token, b"Transfer-Encoding: gzip\r\n")), 400)
        self.assertEqual(MAIN.status_of_raw(req("POST", path, MAIN.token)), 411)
        self.assertEqual(MAIN.status_of_raw(req("POST", path, MAIN.token, b"Content-Length: abc\r\n")), 400)
        self.assertEqual(MAIN.status_of_raw(req("POST", path, MAIN.token, b"Content-Length: -1\r\n")), 400)
        self.assertEqual(MAIN.status_of_raw(req("POST", path, MAIN.token,
                                                b"Content-Length: 4\r\nContent-Length: 4\r\n", b"abc\n")), 400)
        self.assertEqual(MAIN.status_of_raw(req("POST", path, MAIN.token, b"Transfer-Encoding: chunked\r\n",
                                                b"zz\r\nabc\r\n0\r\n\r\n")), 400)
        # truncated body: client closes early -> nothing stored
        MAIN.raw(req("POST", path, MAIN.token, b"Content-Length: 100\r\n", b"only ten.\n"), read=False)
        time.sleep(0.5)
        self.assertFalse(os.path.exists(os.path.join(MAIN.run_dir("logtest", run), "files", "f.txt")))

    def test_methods(self):
        for m in ("PUT", "DELETE", "OPTIONS", "PATCH", "TRACE"):
            self.assertEqual(MAIN.status_of_raw(req(m, "/v1/ping", MAIN.token)), 405, m)
        self.assertEqual(MAIN.status_of_raw(req("FOO", "/v1/ping", MAIN.token)), 501)
        self.assertEqual(MAIN.status_of_raw(req("GET", "/v1/runs/logtest/%s/meta" % rid(), MAIN.token)), 404)
        self.assertEqual(MAIN.status_of_raw(req("GET", "/", MAIN.token)), 404)

    def test_expect_100(self):
        path = "/v1/runs/logtest/%s/files/e.txt" % rid()
        auth, _ = C.auth_header(MAIN.token, "POST", path, b"x" * 70000)
        resp = MAIN.raw(b"POST %s HTTP/1.1\r\nHost: x\r\nAuthorization: %s\r\nContent-Length: 70000\r\n"
                        b"Expect: 100-continue\r\n\r\n" % (path.encode(), auth.encode()))
        self.assertTrue(resp.startswith(b"HTTP/1.1 413"), resp[:40])
        s = socket.create_connection(("127.0.0.1", MAIN.port), timeout=5)
        auth, _ = C.auth_header(MAIN.token, "POST", path, b"ok\n")
        s.sendall(b"POST %s HTTP/1.1\r\nHost: x\r\nAuthorization: %s\r\nContent-Length: 3\r\n"
                  b"Expect: 100-continue\r\nConnection: close\r\n\r\n" % (path.encode(), auth.encode()))
        self.assertTrue(s.recv(100).startswith(b"HTTP/1.1 100"))
        s.sendall(b"ok\n")
        rest = b""
        while True:
            b = s.recv(4096)
            if not b:
                break
            rest += b
        s.close()
        self.assertIn(b"HTTP/1.1 200", rest)


class T05Meta(unittest.TestCase):
    def test_forbidden_and_malformed(self):
        run = rid()
        cl = MAIN.client
        for kv in ({"psid": "0" * 32}, {"openpsid": "1"}, {"username": "x"}, {"account_id": "1"}, {"wifi_ssid": "x"},
                   {"mac": "x"}, {"ip": "1"}, {"serial": "1"}, {"user": "x"},
                   {"note": "aa:bb:cc:dd:ee:ff"}, {"note": "AA-BB-CC-DD-EE-FF"}, {"Bad": "x"}, {"bad-key": "x"},
                   {"k": "v" * 257}, {"k": "tab\there"}):
            self.assertEqual(cl.meta("logtest", run, kv)[0], 422, kv)
        self.assertEqual(cl.request("POST", "/v1/runs/logtest/%s/meta" % run, b"\xff\xfe=x\n")[0], 422)
        self.assertEqual(cl.request("POST", "/v1/runs/logtest/%s/meta" % run, b"novalue\n")[0], 422)
        self.assertEqual(cl.meta("logtest", run, {"k%d" % i: "v" for i in range(65)})[0], 422)
        # timestamps / builds that contain separators are not mistaken for MAC addresses
        self.assertEqual(cl.meta("logtest", run, {"started": "2026-10-07T15:30:00Z", "build": "20261007-153000-ff30827",
                                                  "detail": "PASS END frame 1460, 49 s"})[0], 200)


class T06Discovery(unittest.TestCase):
    def udp(self, payload, wait=0.4):
        s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        s.settimeout(wait)
        try:
            s.sendto(payload, ("127.0.0.1", MAIN.udp))
            try:
                return s.recvfrom(512)[0]
            except socket.timeout:
                return None
        finally:
            s.close()

    def test_good(self):
        nonce, msg = C.discover_request(MAIN.token)
        rep = self.udp(msg)
        self.assertIsNotNone(rep)
        self.assertEqual(C.check_reply(MAIN.token, nonce, rep), ("127.0.0.1", MAIN.port))
        self.assertIsNone(C.check_reply("1" * 64, nonce, rep), "proof must not verify under another token")
        self.assertEqual(C.discover(MAIN.token, "127.0.0.1", MAIN.udp), ("127.0.0.1", MAIN.port))

    def test_drops(self):
        tok = MAIN.token
        nonce = secrets.token_hex(8)
        mac = C.mac_of(tok, "discover|" + nonce)
        other = "2" * 64
        cases = [
            b"SHPDLOG1 DISCOVER nonce=%s mac=%s\n" % (nonce.encode(), C.mac_of(other, "discover|" + nonce).encode()),
            b"SHPDLOG1 DISCOVER nonce=%s mac=%s\n" % (nonce.encode(), b"0" * 32),
            b"SHPDLOG1 DISCOVER nonce=%s mac=%s\n" % (secrets.token_hex(8).encode(), mac.encode()),
            b"SHPDLOG1 DISCOVER nonce=%s\n" % nonce.encode(),
            b"SHPDLOG1 DISCOVER nonce=%s mac=%s junk\n" % (nonce.encode(), mac.encode()),
            b"SHPDLOG1 DISCOVER kid=%s nonce=%s mac=%s\n" % (C.kid_of(tok).encode(), nonce.encode(), mac.encode()),
            b"SHPDLOG2 DISCOVER nonce=%s mac=%s\n" % (nonce.encode(), mac.encode()),
            b"\xff" * 50,
            b"A" * 300,
            b"",
        ]
        for c in cases:
            self.assertIsNone(self.udp(c), c[:60])
        # replay: the first use of a nonce is answered, the second is not
        n2, msg = C.discover_request(tok)
        self.assertIsNotNone(self.udp(msg))
        self.assertIsNone(self.udp(msg))
        # nothing stable about the install is in a DISCOVER datagram: two requests share no field but the magic
        a, b = C.discover_request(tok)[1].split(), C.discover_request(tok)[1].split()
        self.assertEqual([x for x in a if x in b], [b"SHPDLOG1", b"DISCOVER"])
        self.assertNotIn(C.kid_of(tok).encode(), C.discover_request(tok)[1])


class T07Instances(unittest.TestCase):
    """Behaviour that needs its own receiver (caps that would disturb the shared one)."""

    def test_daily_cap(self):
        inst = Instance("daycap", "--max-day", "100K", "--max-total", "10M", "--rate", "100000")
        try:
            run = rid()
            self.assertEqual(inst.client.file("logtest", run, "a.txt", b"x" * 60000)[0], 200)
            self.assertEqual(inst.client.file("logtest", run, "b.txt", b"x" * 60000)[0], 429)
            self.assertFalse(os.path.exists(os.path.join(inst.run_dir("logtest", run), "files", "b.txt")))
            self.assertEqual(inst.client.file("logtest", run, "c.txt", b"x" * 30000)[0], 200)
        finally:
            self.assertEqual(inst.stop(), 0)

    def test_total_cap(self):
        inst = Instance("totalcap", "--max-day", "10M", "--max-total", "100K", "--rate", "100000")
        try:
            run = rid()
            self.assertEqual(inst.client.file("logtest", run, "a.txt", b"x" * 60000)[0], 200)
            self.assertEqual(inst.client.file("logtest", run, "b.txt", b"x" * 60000)[0], 507)
        finally:
            inst.stop()
        # the cap survives a restart: usage is re-read from disk
        inst2 = Instance("totalcap", "--max-day", "10M", "--max-total", "100K", "--rate", "100000")
        try:
            self.assertEqual(inst2.client.file("logtest", rid(), "b.txt", b"x" * 60000)[0], 507)
        finally:
            inst2.stop()

    def test_bad_token_ban(self):
        inst = Instance("ban", "--rate", "100000")
        try:
            bad = C.Client("127.0.0.1", inst.port, "3" * 64)
            for _ in range(10):
                self.assertEqual(bad.ping()[0], 401)
            self.assertEqual(inst.client.ping()[0], 429)
        finally:
            inst.stop()

    def test_rate_limit(self):
        inst = Instance("rate", "--rate", "5")
        try:
            codes = [inst.client.ping()[0] for _ in range(7)]
            self.assertEqual(codes, [200] * 5 + [429] * 2)
        finally:
            inst.stop()

    def test_connection_cap(self):
        inst = Instance("conns", "--max-conns", "2", "--idle-timeout", "5")
        socks = []
        try:
            for _ in range(2):
                socks.append(socket.create_connection(("127.0.0.1", inst.port), timeout=5))
            time.sleep(0.3)
            resp = inst.raw(req("GET", "/v1/ping", inst.token))
            self.assertTrue(resp.startswith(b"HTTP/1.1 503"), resp[:40])
            for s in socks:
                s.close()
            socks = []
            time.sleep(0.5)
            self.assertEqual(inst.client.ping()[0], 200)
        finally:
            for s in socks:
                s.close()
            inst.stop()

    def test_stop_cleans_up(self):
        inst = Instance("stop")
        self.assertTrue(os.path.exists(inst.pidfile))
        port = inst.port
        self.assertEqual(inst.stop(), 0)
        self.assertFalse(os.path.exists(inst.pidfile))
        with self.assertRaises(OSError):
            socket.create_connection(("127.0.0.1", port), timeout=1).close()


class T08Config(unittest.TestCase):
    def run_rx(self, *args):
        return subprocess.run([PY, RECEIVER] + list(args), capture_output=True, text=True, timeout=20)

    def test_refuses_wildcard_and_public(self):
        root = os.path.join(ROOT, "cfg")
        os.makedirs(root, exist_ok=True)
        r = self.run_rx("--root", root, "gen-token")
        self.assertEqual(r.returncode, 0, r.stderr)
        for b in ("0.0.0.0", "8.8.8.8", "100.100.1.1"):
            r = self.run_rx("--root", root, "serve", "--bind", b, "--port", "0", "--udp-port", "0")
            self.assertEqual(r.returncode, 2, (b, r.stdout, r.stderr))
        r = self.run_rx("--root", root, "serve", "--bind", "127.0.0.1", "--port", "8765")
        self.assertEqual(r.returncode, 2)
        self.assertIn("another service", r.stderr)

    def test_gen_token_and_perms(self):
        root = os.path.join(ROOT, "gen")
        r = self.run_rx("--root", root, "gen-token")
        self.assertEqual(r.returncode, 0, r.stderr)
        tok_path = os.path.join(root, "token")
        tok = slurp(tok_path).strip()
        self.assertRegex(tok, r"^[0-9a-f]{64}$")
        self.assertNotIn(tok, r.stdout, "the token is not printed without --show")
        self.assertEqual(stat.S_IMODE(os.stat(tok_path).st_mode), 0o600)
        vc = os.path.join(root, "vita-devlog.txt")
        self.assertEqual(stat.S_IMODE(os.stat(vc).st_mode), 0o600)
        lines = slurp(vc).splitlines()
        self.assertIn("enabled=1", lines)
        self.assertIn("token=" + tok, lines)
        self.assertEqual(self.run_rx("--root", root, "kid").stdout.strip(), C.kid_of(tok))
        # a second gen-token keeps the token; --rotate replaces it
        self.run_rx("--root", root, "gen-token")
        self.assertEqual(slurp(tok_path).strip(), tok)
        self.run_rx("--root", root, "gen-token", "--rotate")
        self.assertNotEqual(slurp(tok_path).strip(), tok)
        # a token readable by others is refused
        os.chmod(tok_path, 0o644)
        r = self.run_rx("--root", root, "serve", "--bind", "127.0.0.1", "--port", "0", "--udp-port", "0")
        self.assertEqual(r.returncode, 2)
        self.assertIn("chmod 600", r.stderr)
        os.chmod(tok_path, 0o600)

    def test_list(self):
        run = rid()
        MAIN.client.meta("logtest", run, {"model": "PCH-2000", "fw": "3.65", "outcome": "ok"})
        MAIN.client.file("logtest", run, "l.txt", b"x\n")
        MAIN.client.done("logtest", run, "ok")
        r = self.run_rx("--root", MAIN.root, "list")
        self.assertIn(run, r.stdout)
        self.assertIn("PCH-2000", r.stdout)


class T09Hardening(unittest.TestCase):
    """The review's attacks (2026-10-07): log injection + growth, slowloris, upload races, uncharged writes,
    a planted index.jsonl symlink, binary content with high bytes, the LAN address rule."""

    def test_log_escapes_and_growth(self):
        inst = Instance("logs", "--log-file", "LOGFILE", "--log-max-bytes", "20K", "--rate", "100000",
                        "--ban-after", "100000")
        lf = os.path.join(inst.root, "rx.log")
        try:
            def logbytes():
                return b"".join(slurp(p, "rb") for p in (lf, lf + ".1", lf + ".2") if os.path.exists(p))
            # terminal escape sequences in the request line / method (no token): never reach the log raw
            inst.raw(b"GET /\x1b]0;PWNED\x07\x1b[2J\x1b[31mFAKE token=deadbeef\x1b[0m HTTP/1.1\r\n\r\n")
            inst.raw(b"\x1b[31mEVIL /x HTTP/1.1\r\n\r\n")
            time.sleep(0.3)
            lg = logbytes()
            self.assertNotIn(b"\x1b", lg)
            self.assertNotIn(b"\x07", lg)
            # 64 KB request lines and unauthenticated requests: about one short line per IP per minute
            before = len(logbytes())
            for _ in range(50):
                inst.raw(b"GET /" + b"B" * 65000 + b" HTTP/1.1\r\n\r\n", timeout=3)
            for _ in range(200):
                inst.status_of_raw(b"GET /v1/ping HTTP/1.1\r\nHost: x\r\nConnection: close\r\n\r\n")
            time.sleep(0.3)
            self.assertLess(len(logbytes()) - before, 2000, "unauthenticated requests grew the log")
            # authenticated traffic is logged per request, and the file rotates (<= 3 files of ~20 KB)
            for _ in range(400):
                inst.client.ping()
            time.sleep(0.3)
            self.assertTrue(os.path.exists(lf + ".1"))
            self.assertFalse(os.path.exists(lf + ".3"))
            for p in (lf, lf + ".1", lf + ".2"):
                if os.path.exists(p):
                    self.assertLess(os.path.getsize(p), 21000, p)
        finally:
            inst.stop()

    def _slow(self, inst, n, drip_s):
        """n connections that send a partial request and then one header byte every drip_s seconds."""
        import threading
        socks = []
        for _ in range(n):
            s = socket.create_connection(("127.0.0.1", inst.port), timeout=5)
            try:
                s.sendall(b"GET /v1/ping HTTP/1.1\r\n")
            except OSError:
                pass
            socks.append(s)
        stop = threading.Event()

        def drip():
            while not stop.wait(drip_s):
                for s in socks:
                    try:
                        s.sendall(b"X: a\r\n")
                    except OSError:
                        pass
        threading.Thread(target=drip, daemon=True).start()
        return socks, stop

    def _until_ok(self, inst, limit_s):
        t0 = time.monotonic()
        while time.monotonic() - t0 < limit_s:
            try:
                if inst.client.ping()[0] == 200:
                    return time.monotonic() - t0
            except OSError:
                pass
            time.sleep(0.25)
        return None

    def test_slowloris_defaults(self):
        # defaults: 8 slots, 2 per address, 8 s header deadline. A dripping client holds at most 2 slots, for 8 s.
        inst = Instance("slow1", "--rate", "100000")
        try:
            socks, stop = self._slow(inst, 8, 2.0)
            try:
                took = self._until_ok(inst, 12.0)
            finally:
                stop.set()
                for s in socks:
                    s.close()
            self.assertIsNotNone(took, "a legitimate client never got through")
            self.assertLessEqual(took, 10.0)
        finally:
            inst.stop()

    def test_slowloris_all_slots(self):
        # every slot taken (per-address limit lifted): the header deadline frees them all
        inst = Instance("slow2", "--rate", "100000", "--max-per-ip", "64", "--header-timeout", "3")
        try:
            socks, stop = self._slow(inst, 8, 1.0)
            try:
                time.sleep(0.3)
                self.assertTrue(inst.raw(req("GET", "/v1/ping", inst.token)).startswith(b"HTTP/1.1 503"))
                took = self._until_ok(inst, 8.0)
                closed = 0
                for s in socks:
                    s.settimeout(0.5)
                    try:
                        closed += s.recv(100) == b""
                    except OSError:
                        closed += 1
            finally:
                stop.set()
                for s in socks:
                    s.close()
            self.assertIsNotNone(took)
            self.assertLessEqual(took, 4.5)
            self.assertEqual(closed, 8, "the dripping connections were closed by the receiver")
        finally:
            inst.stop()

    def test_parallel_uploads_respect_run_caps(self):
        import threading
        inst = Instance("race", "--max-file", "512K", "--max-run", "1M", "--max-files", "4", "--rate", "100000",
                        "--max-conns", "16", "--max-per-ip", "16")
        try:
            run = rid()
            self.assertEqual(inst.client.meta("app", run, {"outcome": "ok"})[0], 200)
            body = (b"y" * 1023 + b"\n") * 400   # 400 KiB
            codes = {}

            def up(i):
                codes[i] = C.Client("127.0.0.1", inst.port, inst.token, 10).file("app", run, "f%d.txt" % i, body)[0]
            ts = [threading.Thread(target=up, args=(i,)) for i in range(8)]
            for t in ts:
                t.start()
            for t in ts:
                t.join()
            man = json.loads(slurp(os.path.join(inst.run_dir("app", run), "manifest.json")))
            total = sum(f["bytes"] for f in man["files"].values())
            self.assertLessEqual(total, 1024 * 1024, codes)
            self.assertLessEqual(len(man["files"]), 4)
            self.assertEqual(sorted(codes.values()).count(200), len(man["files"]))
            self.assertGreaterEqual(len(man["files"]), 1)
            self.assertEqual([f for f in os.listdir(os.path.join(inst.run_dir("app", run), "files"))
                              if f.startswith(".")], [])
        finally:
            inst.stop()

    def test_every_write_is_charged(self):
        inst = Instance("charged", "--max-day", "64K", "--rate", "100000")
        try:
            codes = [inst.client.file("logtest", rid(), "e.txt", b"")[0] for _ in range(120)]
            self.assertIn(429, codes, "empty files into new runs were never charged")
            self.assertLessEqual(codes.count(200), 64 * 1024 // (1024 + 256) + 1)
            runs = _count_run_dirs(inst.inbox)
            self.assertLessEqual(runs, 52)
            # /done once the daily cap is used up: refused, the index does not grow
            ok_run = [r for r in os.listdir(os.path.join(inst.inbox, TODAY, "logtest"))][0]
            idx = os.path.getsize(os.path.join(inst.inbox, "index.jsonl"))
            done = [inst.client.done("logtest", ok_run, "ok")[0] for _ in range(20)]
            self.assertLessEqual(done.count(200), 4, done)     # < 1280 bytes were left: at most 4 x 256
            self.assertEqual(done[-1], 429)
            self.assertLess(os.path.getsize(os.path.join(inst.inbox, "index.jsonl")) - idx, 4 * 300)
        finally:
            inst.stop()
        inst = Instance("newruns", "--max-new-runs", "5", "--rate", "100000")
        try:
            codes = [inst.client.file("logtest", rid(), "e.txt", b"x\n")[0] for _ in range(7)]
            self.assertEqual(codes, [200] * 5 + [429] * 2)
        finally:
            inst.stop()

    def test_meta_caps_hold_for_the_merged_meta(self):
        run = rid()
        cl = MAIN.client
        self.assertEqual(cl.meta("logtest", run, {"a%d" % i: "v" for i in range(40)})[0], 200)
        self.assertEqual(cl.meta("logtest", run, {"b%d" % i: "v" for i in range(30)})[0], 422)
        m = json.loads(slurp(os.path.join(MAIN.run_dir("logtest", run), "meta.json")))["meta"]
        self.assertEqual(len(m), 40)
        run2 = rid()
        self.assertEqual(cl.meta("logtest", run2, {"a%d" % i: "v" * 200 for i in range(30)})[0], 200)
        self.assertEqual(cl.meta("logtest", run2, {"b%d" % i: "v" * 200 for i in range(30)})[0], 413)
        self.assertLess(os.path.getsize(os.path.join(MAIN.run_dir("logtest", run2), "meta.json")), 9000)

    def test_index_symlink_is_never_followed(self):
        inst = Instance("idxlink", "--rate", "100000")
        outside = os.path.join(ROOT, "outside-idxlink.txt")
        with open(outside, "w") as f:
            f.write("OUTSIDE\n")
        idx = os.path.join(inst.inbox, "index.jsonl")
        try:
            if os.path.lexists(idx):
                os.unlink(idx)
            os.symlink(outside, idx)
            self.assertEqual(inst.client.file("logtest", rid(), "a.txt", b"hello\n")[0], 200)
            self.assertEqual(slurp(outside), "OUTSIDE\n")
        finally:
            inst.stop()
        self.assertIn("index.jsonl: cannot open", inst.log_text())
        # and a receiver does not start on an inbox whose index.jsonl is a symlink
        r = subprocess.run([PY, RECEIVER, "--root", inst.root, "serve", "--bind", "127.0.0.1", "--port", "0",
                            "--udp-port", "0", "--min-free", "1M"], capture_output=True, text=True, timeout=20)
        self.assertEqual(r.returncode, 2, r.stderr)
        self.assertIn("not a regular file", r.stderr)
        os.unlink(idx)

    def test_text_rule_high_bytes(self):
        run = rid()
        cl = MAIN.client
        self.assertEqual(cl.file("logtest", run, "hi.txt", bytes(range(0x80, 0x100)) * 25)[0], 415)
        latin1 = b"a" * 2000 + b" caf\xe9 " * 3 + b"\n"   # a few stray Latin-1 bytes: still a log
        self.assertEqual(cl.file("logtest", run, "l1.txt", latin1)[0], 200)
        utf8 = ("\u00e9\u2713 line\n" * 2000).encode()
        self.assertEqual(cl.file("logtest", run, "u8.txt", utf8)[0], 200)
        # a multi-byte character split across chunks / reads is not "invalid"
        split = b"a" * 4095 + "\u2713".encode() + b"b" * 5000 + b"\n"
        self.assertEqual(cl.file("logtest", run, "split.txt", split, chunked=True)[0], 200)
        m = json.loads(slurp(os.path.join(MAIN.run_dir("logtest", run), "manifest.json")))["files"]
        self.assertEqual(sorted(m), ["l1.txt", "split.txt", "u8.txt"])
        self.assertEqual(m["l1.txt"].get("invalid_utf8"), 3)
        self.assertNotIn("invalid_utf8", m["split.txt"])

    def test_lan_rule(self):
        sys.path.insert(0, HERE)
        import receiver as R
        # the 192.168 case is written split so the public export's LAN-address scrub leaves it alone (a whole literal would be
        # rewritten to the documentation range, which is_lan_ip correctly rejects)
        for ip in ("10.1.2.3", "172.16.0.1", "172.31.255.254", "192.168." + "200.7"):
            self.assertTrue(R.is_lan_ip(ip), ip)
        for ip in ("198.18.0.1", "198.19.255.1", "192.0.0.8", "192.0.2.1", "0.0.0.0", "0.1.2.3", "240.0.0.1",
                   "100.64.1.1", "100.100.100.100", "127.0.0.1", "169.254.1.1", "172.15.0.1", "172.32.0.1",
                   "8.8.8.8", "255.255.255.255", "::1", "fd00::1", "nonsense"):
            self.assertFalse(R.is_lan_ip(ip), ip)
        out = R._safe("a\x1b[2Jb\x07\n\\" + "z" * 300, 20)
        self.assertTrue(out.startswith("a\\x1b[2Jb\\x07\\x0a\\\\z"), out)
        self.assertTrue(out.endswith("...(+289)"), out)
        self.assertTrue(all(0x20 <= ord(c) < 0x7f for c in out), out)


def _count_run_dirs(inbox):
    n = 0
    for d in os.listdir(inbox):
        dd = os.path.join(inbox, d)
        if re.fullmatch(r"\d{4}-\d\d-\d\d", d) and os.path.isdir(dd):
            for app in os.listdir(dd):
                n += len(os.listdir(os.path.join(dd, app)))
    return n


class T10CodexBinding(unittest.TestCase):
    """Revision 3 (PROTOCOL.md §10): --base speaks the Archive Codex server's binding: <base>/v1/ping and ONE exact
    upload path with the run in a fixed-order query; HERE carries base= under the proof."""
    BASE = "/vita/devlog"

    @classmethod
    def setUpClass(cls):
        cls.inst = Instance("codex", "--base", cls.BASE, "--rate", "100000", "--ban-after", "100000")
        cls.cl = C.Client("127.0.0.1", cls.inst.port, cls.inst.token, base=cls.BASE)

    def test_discovery_carries_the_base_under_the_proof(self):
        nonce, msg = C.discover_request(self.inst.token)
        s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        s.settimeout(0.5)
        try:
            s.sendto(msg, ("127.0.0.1", self.inst.udp))
            rep = s.recvfrom(512)[0]
        finally:
            s.close()
        self.assertIn(b" base=/vita/devlog proof=", rep)
        self.assertEqual(C.check_reply(self.inst.token, nonce, rep, True), ("127.0.0.1", self.inst.port, self.BASE))
        forged = rep.replace(b"base=/vita/devlog", b"base=/vita/devlox")
        self.assertIsNone(C.check_reply(self.inst.token, nonce, forged, True), "a changed base must not verify")
        stripped = rep.replace(b" base=/vita/devlog", b"")
        self.assertIsNone(C.check_reply(self.inst.token, nonce, stripped, True), "a removed base must not verify")

    def test_ping_and_full_run(self):
        code, body, ok = self.cl.ping_verified()
        self.assertEqual((code, ok), (200, True), body)
        self.assertEqual(C.Client("127.0.0.1", self.inst.port, self.inst.token).ping()[0], 404, "/v1/ping is not served")
        run = rid()
        self.assertEqual(self.cl.meta("logtest", run, {"outcome": "ok", "app": "logtest"}), (200, "ok\n"))
        data = b"codex binding line\n" * 50
        code, body = self.cl.file("logtest", run, "logtest.txt", data)
        self.assertEqual(body, "ok bytes=%d sha256=%s\n" % (len(data), hashlib.sha256(data).hexdigest()))
        self.assertEqual(self.cl.done("logtest", run, "ok"), (200, "ok\n"))
        rd = self.inst.run_dir("logtest", run)
        self.assertEqual(slurp(os.path.join(rd, "files", "logtest.txt"), "rb"), data)
        self.assertTrue(json.loads(slurp(os.path.join(rd, "manifest.json")))["done"])

    def test_the_mac_covers_the_query(self):
        run = rid()
        good = "%s/v1/upload?op=meta&app=logtest&run=%s" % (self.BASE, run)
        auth, _ = C.auth_header(self.inst.token, "POST", good, b"outcome=ok\n")
        bad = good.replace("app=logtest", "app=probe")
        raw = b"POST %s HTTP/1.1\r\nHost: x\r\nAuthorization: %s\r\nContent-Length: 11\r\nConnection: close\r\n\r\noutcome=ok\n" \
            % (bad.encode(), auth.encode())
        self.assertEqual(self.inst.status_of_raw(raw), 401)

    def test_bad_targets(self):
        before = snapshot(self.inst.root)
        run = rid()
        b = self.BASE
        bad = [
            b + "/v1/upload?op=file&app=logtest&run={run}",                      # file without a name
            b + "/v1/upload?op=meta&app=logtest&run={run}&name=x.txt",           # a name on meta
            b + "/v1/upload?app=logtest&op=meta&run={run}",                      # another order
            b + "/v1/upload?op=meta&app=logtest&run={run}&x=1",                  # an extra parameter
            b + "/v1/upload?op=file&app=logtest&run={run}&name=..%2fx.txt",      # percent-encoding
            b + "/v1/upload?op=file&app=logtest&run={run}&name=../x.txt",
            b + "/v1/upload?op=file&app=logtest&run={run}&name=x.exe",
            b + "/v1/upload?op=list&app=logtest&run={run}",
            b + "/v1/upload?op=meta&app=Logtest&run={run}",
            b + "/v1/upload?op=meta&app=logtest&run=abc",
            b + "/v1/upload",
            b + "/v1/runs/logtest/{run}/meta",                                   # the receiver's paths: not here
            "/v1/runs/logtest/{run}/meta",
            b + "/../v1/upload?op=meta&app=logtest&run={run}",
            b + "/v1/upload/?op=meta&app=logtest&run={run}",
        ]
        for p in bad:
            path = p.format(run=run)
            code = self.inst.status_of_raw(req("POST", path, self.inst.token, b"Content-Length: 4\r\n", b"abc\n"))
            self.assertIn(code, (400, 404), path)
        after = snapshot(self.inst.root)
        self.assertEqual([n for n in sorted(set(after) - set(before)) if not n.startswith("receiver.log")], [])


class T99Confinement(unittest.TestCase):
    def test_only_inbox_written(self):
        """Everything the receivers wrote is under inbox/<date>/<app>/<run>/ or inbox/index.jsonl."""
        for inst in INSTANCES:
            for dirpath, dirs, files in os.walk(inst.inbox):
                rel = os.path.relpath(dirpath, inst.inbox)
                parts = [] if rel == "." else rel.split(os.sep)
                for fn in files:
                    if not parts:
                        self.assertEqual(fn, "index.jsonl", inst.name)
                    else:
                        self.assertRegex(parts[0], r"^\d{4}-\d\d-\d\d$")
                        self.assertTrue(len(parts) in (3, 4), (inst.name, rel, fn))
                        if len(parts) == 4:
                            self.assertEqual(parts[3], "files")
                            self.assertFalse(fn.startswith("."), "leftover temp file %s/%s" % (rel, fn))
                        else:
                            self.assertIn(fn, ("meta.json", "manifest.json"))


def snapshot(root):
    out = []
    for dirpath, dirs, files in os.walk(root):
        for fn in files:
            out.append(os.path.relpath(os.path.join(dirpath, fn), root))
    return out


if __name__ == "__main__":
    print("test root:", ROOT)
    unittest.main(verbosity=2 if "-v" in sys.argv else 1)
