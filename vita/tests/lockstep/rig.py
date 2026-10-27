#!/usr/bin/env python3
"""Kit #7 tests track (vita/notes/kit7-tests.md "P2 lockstep hash rig"; Enhanced groundwork): two runs of Shepherd compared frame by frame
WHILE they run, through their per-frame state checksums (Diag/StateHash.cs, SHEPHERD_STATE_HASH lines:
frame live active transforms objects behaviours rng time total). The rig stops both at the first frame that differs, names the parts that
moved, and (--dump) replays both to that frame with SHEPHERD_STATE_DUMP=<frame> and diffs the two dumps record by record.

Sources (--a / --b), each one of:
  boot:<out dir>[;ENV=VAL;...]   a Mac boot through vita/tests/macboot.sh (the shared runner: null backend, frame clock, silent), its state
                                 hashes written to a FIFO the rig reads
  file:<path>                    a file another process is writing (followed like tail -f; ends when the writer stops for --idle s)
  tcp:<port>                     listen on 127.0.0.1:<port> for state-hash lines (the device leg's shape: a Vita host streaming its lines
                                 over the network to the Mac; nothing on the Vita yet -- the host side is a later kit's step)
Common options: --frames N (the boot sources' frame count), --pad <file>, --env K=V (both boot sources), --dump, --log <file> (the rig's
output is also written there; default for two boot sources: <A's out dir>.rig.txt -- the evidence of an A/A claim).
A/A = the same source twice: the rig's own determinism check (and the device leg's acceptance later: device vs Mac at the same input).
Review fixes (kit7-tests.md "Review fixes"): when both streams end, each macboot run is waited for (it copies engine.log and removes its
root/) and its rc and the engine's END line ('done: ...') are checked -- two runs that fail the same way at the same frame are not 'in step';
when the rig stops runs early (a divergence, one side ended first) it stops only the engine processes, so macboot still copies the log and
cleans its root (a leftover root is removed by the rig).
Exit 0 = every frame both sides reported agreed, both reported the same number of frames, and every boot source ended cleanly (rc 0 + END);
1 = in step but the frame counts differ; 2 = diverged; 3 = in step but a boot source did not end cleanly."""
import argparse, os, sys, subprocess, tempfile, threading, queue, time, socket, shutil

HERE = os.path.dirname(os.path.abspath(__file__)); MACBOOT = os.path.join(os.path.dirname(HERE), "macboot.sh")
COLS = ["frame", "live", "active", "transforms", "objects", "behaviours", "rng", "time", "total"]

class Source:
    def __init__(self, spec, name, args):
        self.spec, self.name, self.args = spec, name, args
        self.q = queue.Queue(maxsize=100000); self.proc = None; self.done = False
    def start(self):
        kind, _, rest = self.spec.partition(":")
        if kind == "boot":
            parts = rest.split(";"); out = os.path.abspath(parts[0]); envs = [p for p in parts[1:] if p] + list(self.args.env or [])
            self.tmp = tempfile.mkdtemp(prefix="rig-", dir=os.environ.get("TMPDIR")); fifo = os.path.join(self.tmp, "state.fifo"); os.mkfifo(fifo)
            cmd = ["zsh", MACBOOT, out, str(self.args.frames)] + ([f"PAD={self.args.pad}"] if self.args.pad else []) + envs + [f"SHEPHERD_STATE_HASH={fifo}"]
            if self.args.dump_frame: cmd.append(f"SHEPHERD_STATE_DUMP={self.args.dump_frame}")
            self.proc = subprocess.Popen(cmd, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, start_new_session=True)
            threading.Thread(target=self._read_fifo, args=(fifo,), daemon=True).start()
            self.out = out
        elif kind == "file":
            threading.Thread(target=self._follow, args=(rest,), daemon=True).start()
        elif kind == "tcp":
            threading.Thread(target=self._tcp, args=(int(rest),), daemon=True).start()
        else: raise SystemExit(f"unknown source {self.spec}")
    def _read_fifo(self, fifo):
        with open(fifo, "r") as f:
            for line in f: self.q.put(line.rstrip("\n"))
        self.q.put(None)
    def _follow(self, path):
        while not os.path.exists(path): time.sleep(0.2)
        last = time.time()
        with open(path, "r") as f:
            buf = ""
            while True:
                s = f.readline()
                if s:
                    buf += s
                    if buf.endswith("\n"): self.q.put(buf.rstrip("\n")); buf = ""; last = time.time()
                elif time.time() - last > self.args.idle: break
                else: time.sleep(0.05)
        self.q.put(None)
    def _tcp(self, port):
        srv = socket.socket(); srv.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1); srv.bind(("127.0.0.1", port)); srv.listen(1)
        conn, _ = srv.accept()
        with conn, conn.makefile("r") as f:
            for line in f: self.q.put(line.rstrip("\n"))
        self.q.put(None)
    def finish(self, early, wait):
        """Waits for a boot source's macboot run. early: the rig stopped comparing before the run's end -- only its engine processes are
        stopped, so macboot still copies engine.log and removes root/. -> (rc, macboot's verdict line) or (None, '') for other sources."""
        if not self.proc: return None, ""
        if early and self.proc.poll() is None:
            for pid in self._engine_pids():
                try: os.kill(pid, 15)
                except Exception: pass
        try: out, _ = self.proc.communicate(timeout=wait)
        except subprocess.TimeoutExpired:
            try: os.killpg(self.proc.pid, 15)
            except Exception: pass
            try: out, _ = self.proc.communicate(timeout=30)
            except Exception: out = b""
        line = (out or b"").decode("utf-8", "replace").strip().splitlines()
        root = os.path.join(self.out, "root")
        if os.path.isdir(root) and os.path.realpath(root).startswith(os.path.realpath(self.out) + os.sep):
            shutil.rmtree(root, ignore_errors=True)   # (macboot's own cleanup did not run: a run killed whole)
        return self.proc.returncode, (line[-1] if line else "")
    def _engine_pids(self):
        try: r = subprocess.run(["pgrep", "-g", str(self.proc.pid), "-x", "dotnet"], capture_output=True, text=True).stdout
        except Exception: return []
        return [int(x) for x in r.split() if x.isdigit()]

def compare(a, b, args):
    n = 0; t0 = time.time()
    while True:
        la = a.q.get(); lb = b.q.get()
        if la is None or lb is None:
            if la is None and lb is None: return n, None, "both ended"
            return n, None, f"{'A' if la is None else 'B'} ended first after {n} frames"
        n += 1
        if la != lb:
            pa, pb = la.split("\t"), lb.split("\t")
            parts = [COLS[i] for i in range(1, min(len(pa), len(pb), len(COLS))) if pa[i] != pb[i] and COLS[i] != "total"]
            return n, (pa[0], parts, la, lb), None
        if args.progress and n % args.progress == 0: print(f"  [rig] frame {la.split(chr(9))[0]}: in step ({time.time() - t0:.0f} s)", flush=True)

def diff_dumps(fa, fb, limit=12):
    def rd(p):
        d = {}
        for line in open(p):
            if line.startswith("#"): continue
            f = line.rstrip("\n").split("\t"); d[(f[0], f[1])] = line.rstrip("\n")
        return d
    A, B = rd(fa), rd(fb); out = []
    # (records compared by their hashed fields: the line's last column is the record's hash; names are printed, not hashed)
    for k in sorted(set(A) | set(B), key=lambda k: (k[0], int(k[1]))):
        ha = A.get(k, "-").split("\t")[-1]; hb = B.get(k, "-").split("\t")[-1]
        if ha != hb: out.append((A.get(k, "-"), B.get(k, "-")))
    return out

def main():
    ap = argparse.ArgumentParser(); ap.add_argument("--a", required=True); ap.add_argument("--b", required=True)
    ap.add_argument("--frames", type=int, default=1500); ap.add_argument("--pad"); ap.add_argument("--env", action="append")
    ap.add_argument("--dump", action="store_true"); ap.add_argument("--idle", type=float, default=20); ap.add_argument("--progress", type=int, default=0)
    ap.add_argument("--dump-frame", type=int, default=0, help=argparse.SUPPRESS)
    ap.add_argument("--log"); ap.add_argument("--wait", type=float, default=900, help="seconds a boot source may take to exit after its stream ended")
    args = ap.parse_args()
    log = args.log or (os.path.abspath(args.a[5:].split(";")[0]) + ".rig.txt" if args.a.startswith("boot:") and args.b.startswith("boot:") else None)
    logf = open(log, "w") if log else None
    def say(msg):
        print(msg, flush=True)
        if logf: logf.write(msg + "\n"); logf.flush()
    say(f"[rig] {time.strftime('%Y-%m-%d %H:%M:%S')} A={args.a} B={args.b} frames={args.frames} pad={args.pad} env={args.env}")
    A, B = Source(args.a, "A", args), Source(args.b, "B", args)
    A.start(); B.start()
    n, diff, end = None, None, "rig error"
    try: n, diff, end = compare(A, B, args)
    finally:
        early = diff is not None or end != "both ended"
        clean = True
        for s in (A, B):
            rc, line = s.finish(early, args.wait)
            if rc is None: continue
            ok = rc == 0 and " done: " in f" {line}"
            if not early and not ok: clean = False
            say(f"[rig] {s.name}: macboot rc={rc}{'' if ok or early else '  NOT A CLEAN END'}: {line}")
    if diff is None:
        if end == "both ended" and not clean:
            say(f"[rig] IN STEP over {n} frames, but a boot source did not end cleanly (rc / END above): not an A/A pass"); return 3
        say(f"[rig] IN STEP: {n} frames, every state checksum equal ({end})")
        return 0 if end == "both ended" else 1
    frame, parts, la, lb = diff
    say(f"[rig] DIVERGED at frame {frame} (after {n - 1} equal frames): {', '.join(parts) or 'total only'} moved\n   A {la}\n   B {lb}")
    if args.dump and args.a.startswith("boot:") and args.b.startswith("boot:"):
        # both replayed to that frame (it stops there) with SHEPHERD_STATE_DUMP: every record's hashed fields, then diffed record by record
        files = []
        for spec in (args.a, args.b):
            parts = spec[5:].split(";"); out = os.path.abspath(parts[0]) + "-dump"
            cmd = ["zsh", MACBOOT, out, str(frame)] + ([f"PAD={args.pad}"] if args.pad else []) + [p for p in parts[1:] if p] + list(args.env or []) + [f"SHEPHERD_STATE_DUMP={frame}"]
            subprocess.run(cmd, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
            files.append(os.path.join(out, f"state-hash.txt.f{frame}.tsv"))
        if all(os.path.exists(f) for f in files):
            d = diff_dumps(*files)
            say(f"[rig] state dumps at frame {frame}: {len(d)} records differ ({files[0]} vs {files[1]}); the first:")
            for x, y in d[:12]: say(f"   A {x[:220]}\n   B {y[:220]}")
        else: say(f"[rig] dumps missing: {files}")
    return 2

if __name__ == "__main__": sys.exit(main())
