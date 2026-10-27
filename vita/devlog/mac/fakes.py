#!/usr/bin/env python3
"""test.sh helper: hostile/broken peers on 127.0.0.1 (standard library only).

  fakes.py blackhole <statefile>            TCP listener that accepts and never answers (a hung receiver)
  fakes.py rogue <statefile>                UDP responder that answers every DISCOVER with a HERE pointing at its own
                                            TCP listener but with a WRONG proof (it does not know the token); the TCP
                                            listener records whatever connects. A correct client never connects.
  fakes.py stall <statefile> <tokenfile>    a receiver that knows the token: answers ping (valid proof) and meta,
                                            then stops reading file bodies (Wi-Fi gone / Mac asleep mid-upload)
  fakes.py impostor <statefile> <tokenfile> an HTTP server at a stale address that only LOOKS like a receiver: answers
                                            "ok shpdlog/1" without a proof and 200 to everything; records each request
                                            line and whether the token's bytes were in it (they never may be)
  fakes.py relay <statefile> <tokenfile> <tcpA> <tcpB> <base>
                                            revision 3: a discovery responder that answers every DISCOVER twice, with
                                            valid proofs: first a standalone receiver at 127.0.0.1:<tcpA>, 30 ms later
                                            a Codex server at 127.0.0.1:<tcpB> with base=<base> (the client must pick
                                            the Codex one, and fall back to the other when it is gone)
  fakes.py wall <statefile>                 an HTTP server that answers everything with 401 and a JSON body, like the
                                            Archive Codex server's login wall before its devlog routes exist
statefile gets "<tcp port> <udp port>" once listening; hits go to <statefile>.hits. Runs until killed.
"""
import hashlib, hmac, re, socket, sys, threading, time


def read_head(c):
    data = b""
    while b"\r\n\r\n" not in data:
        b = c.recv(1)
        if not b:
            return None
        data += b
    return data


def mac(token, msg):
    return hmac.new(token.encode(), msg.encode(), hashlib.sha256).hexdigest()[:32]


def relay(state, token, tcp_a, tcp_b, base):
    udp = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    udp.bind(("127.0.0.1", 0))
    open(state, "w").write("0 %d\n" % udp.getsockname()[1])
    while True:
        data, src = udp.recvfrom(512)
        m = re.search(rb"nonce=([0-9a-f]{16})", data)
        if not m:
            continue
        n = m.group(1).decode()
        a = "SHPDLOG1 HERE host=127.0.0.1 port=%s nonce=%s proof=%s\n" % (tcp_a, n, mac(token, "here|%s|127.0.0.1|%s" % (n, tcp_a)))
        b = "SHPDLOG1 HERE host=127.0.0.1 port=%s nonce=%s base=%s proof=%s\n" % (
            tcp_b, n, base, mac(token, "here|%s|127.0.0.1|%s|%s" % (n, tcp_b, base)))
        udp.sendto(a.encode(), src)
        time.sleep(0.03)
        udp.sendto(b.encode(), src)
        with open(state + ".hits", "ab") as f:
            f.write(b"UDP\n")


def main():
    mode, state = sys.argv[1], sys.argv[2]
    token = open(sys.argv[3]).read().strip() if len(sys.argv) > 3 else ""
    if mode == "relay":
        return relay(state, token, sys.argv[4], sys.argv[5], sys.argv[6])
    tcp = socket.socket(); tcp.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    if mode == "stall":
        tcp.setsockopt(socket.SOL_SOCKET, socket.SO_RCVBUF, 4096)
    tcp.bind(("127.0.0.1", 0)); tcp.listen(8)
    port = tcp.getsockname()[1]
    udp = None
    if mode == "rogue":
        udp = socket.socket(socket.AF_INET, socket.SOCK_DGRAM); udp.bind(("127.0.0.1", 0))
    held = []

    def hit(line):
        with open(state + ".hits", "ab") as f:
            f.write(line + b"\n")

    def serve(c):
        c.settimeout(5)
        try:
            head = read_head(c)
        except OSError:
            head = None
        if head is None:
            return
        line = head.split(b"\r\n", 1)[0]
        hit(b"TCP " + line + b" token_seen=%d" % int(bool(token) and token.encode() in head))
        if mode == "stall":
            m = re.search(rb"nonce=([0-9a-f]{16})", head)
            if line.startswith(b"GET /v1/ping"):
                proof = hmac.new(token.encode(), b"pong|%s|127.0.0.1|%d" % (m.group(1), port), hashlib.sha256).hexdigest()[:32]
                body = b"ok shpdlog/1 max_file=8388608 max_run=33554432 proof=%s\n" % proof.encode()
            elif b"/meta " in line:
                cl = int(re.search(rb"(?i)content-length: *(\d+)", head).group(1))
                while cl > 0:
                    cl -= len(c.recv(cl))
                body = b"ok\n"
            else:
                held.append(c)        # never read the file body, never answer
                return
        elif mode == "wall":
            body = b'{"error": "auth required", "login": "/auth/login"}'
            c.sendall(b"HTTP/1.1 401 UNAUTHORIZED\r\nContent-Type: application/json\r\nContent-Length: %d\r\n"
                      b"Connection: close\r\n\r\n" % len(body) + body)
            c.close()
            return
        else:  # impostor
            body = b"ok shpdlog/1 max_file=8388608 max_run=33554432\n" if line.startswith(b"GET /v1/ping") else b"ok\n"
        c.sendall(b"HTTP/1.1 200 OK\r\nContent-Length: %d\r\nConnection: close\r\n\r\n" % len(body) + body)
        c.close()

    def accept_loop():
        while True:
            c, _ = tcp.accept()
            if mode in ("stall", "impostor", "wall"):
                threading.Thread(target=serve, args=(c,), daemon=True).start()
                continue
            held.append(c)
            c.settimeout(0.5)
            try:
                data = c.recv(4096)
            except OSError:
                data = b""
            hit(b"TCP " + str(len(data)).encode() + b"\n" + data)

    open(state, "w").write("%d %d\n" % (port, udp.getsockname()[1] if udp else 0))
    threading.Thread(target=accept_loop, daemon=True).start()
    if udp:
        while True:
            data, src = udp.recvfrom(512)
            parts = dict(p.split("=", 1) for p in data.decode(errors="replace").split()[2:] if "=" in p)
            reply = "SHPDLOG1 HERE host=127.0.0.1 port=%d nonce=%s proof=%s\n" % (port, parts.get("nonce", ""), "0" * 32)
            udp.sendto(reply.encode(), src)
            hit(b"UDP")
    else:
        while True:
            time.sleep(1)


main()
