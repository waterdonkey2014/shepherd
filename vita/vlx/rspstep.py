#!/usr/bin/env python3
"""rspstep.py (kit #6 track fps): the instruction count of given functions (entry to return, callees included) in a vlx run, by
single-stepping the real objects through qemu-user's gdbstub with the GDB remote protocol (no lldb: VLX_GDB=<port> VLX_BREAK=1 KEEP=1).
  rspstep.py <elf> <port> <frame> <fn,fn> <samples> <out>
Runs to the engine checkpoint of <frame> (host.c shepherd_vita_engine_checkpoint, r0 = frame, every 50 frames; its breakpoint stepped
over by hand: qemu's TCG breakpoints trap again on a plain continue), then per function: break at its entry, single-step until the
return address with the stack back, count; the first sample also prints the instructions per symbol. Process signals (Mono's) are
passed on; qemu-user reads the debugger only when the program stops. Notes: vita/notes/kit6-fps.md."""
import socket, sys, time, subprocess, bisect, collections, os

elf, port, F0, FUNCS, NS, out = sys.argv[1], int(sys.argv[2]), int(sys.argv[3]), sys.argv[4].split(','), int(sys.argv[5]), sys.argv[6]
F1 = F0; DT = 0
NM = os.path.expanduser('~/vitasdk/bin/arm-vita-eabi-nm')
syms = []; cpkind = 4
for line in subprocess.run([NM, '-n', elf], capture_output=True, text=True).stdout.splitlines():
    p = line.split()
    if len(p) >= 3 and p[1] in 'tTwW':
        syms.append((int(p[0], 16) & ~1, p[2]))
        if p[2] == 'shepherd_vita_engine_checkpoint': cpkind = 2 if int(p[0], 16) & 1 else 4   # Thumb code: a 2-byte breakpoint
syms.sort(); addrs = [a for a, _ in syms]
cpaddr = next(a for a, n in syms if n == 'shepherd_vita_engine_checkpoint')
def sym(a):
    i = bisect.bisect_right(addrs, a) - 1
    return syms[i][1] if i >= 0 else '?%08x' % a

s = socket.create_connection(('127.0.0.1', port)); s.settimeout(None)
buf = b''
def rd():
    global buf
    while True:
        i = buf.find(b'$')
        if i >= 0:
            j = buf.find(b'#', i)
            if j >= 0 and len(buf) >= j + 3:
                pkt = buf[i + 1:j]; buf = buf[j + 3:]
                s.sendall(b'+')
                if DBG: sys.stderr.write('< %s\n' % pkt[:120]); sys.stderr.flush()
                return pkt.decode('latin1')
        d = s.recv(65536)
        if not d: raise EOFError
        buf += d
DBG = os.environ.get('RSP_DEBUG') == '1'
def send(p):
    if DBG: sys.stderr.write('> %s\n' % p[:80]); sys.stderr.flush()
    c = sum(p.encode('latin1')) & 0xff
    s.sendall(b'$' + p.encode('latin1') + b'#%02x' % c)
def cmd(p):
    send(p); r = rd()
    while r.startswith('O') and r != 'OK': r = rd()   # console output packets
    return r
def le32(h): return int.from_bytes(bytes.fromhex(h), 'little')
log = open(out, 'w')
def L(x): log.write(x + '\n'); log.flush()
cmd('qSupported:swbreak+;multiprocess-')
L('stop at attach: ' + cmd('?'))
cmd('QStartNoAckMode') if False else None
def threads():
    t = []; r = cmd('qfThreadInfo')
    while r.startswith('m'):
        t += [x for x in r[1:].split(',') if x]; r = cmd('qsThreadInfo')
    return t
_regs = {}
def regs(tid):
    cmd('Hg' + tid); g = cmd('g'); return [le32(g[8 * i:8 * i + 8]) for i in range(16)]
def reg(tid, n): return regs(tid)[n]
# skip the bkpt: pc += 4 on the stopped thread
st = cmd('?'); tid0 = None
for kv in st[3:].split(';'):
    if kv.startswith('thread:'): tid0 = kv[7:]
def skip_bkpt(tid):
    """at VLX_BREAK's bkpt in vlx_boot: pc += 4 (the shim's 'debugger attached' trap)"""
    cmd('Hg' + tid); g = cmd('g'); pc = le32(g[120:128])
    if sym(pc & ~1) != 'vlx_boot': return False
    cmd('G' + g[:120] + (pc + 4).to_bytes(4, 'little').hex() + g[128:]); return True
pc0 = reg(tid0, 15); skip_bkpt(tid0)
L('bkpt at %08x (%s), thread %s' % (pc0, sym(pc0), tid0))
assert cmd('Z0,%x,%d' % (cpaddr, cpkind)) == 'OK', 'Z0 failed'
def stop_thread(r):
    for kv in r[3:].split(';'):
        if kv.startswith('thread:'): return kv[7:]
    return None
_sig = 0
TICK = 3   # (unused here: rspsamp's sampling tick)
OURS = (0, 2, 5, TICK)   # none, SIGINT, SIGTRAP (breakpoints), the sampling ticks (never delivered to the program)
def cont():
    """continue, passing on the signal of the last stop unless it was ours (SIGTRAP 5 = breakpoint / bkpt, SIGINT 2 = the interrupt):
    qemu-user reports every signal to the debugger first -- Mono's GC suspend signals among them"""
    send('C%02x' % _sig if _sig not in OURS else 'c')
def wait_stop():
    global _sig
    r = rd()
    while True:
        while r.startswith('O') and r != 'OK': r = rd()
        if r[0] in 'TS': _sig = int(r[1:3], 16)
        if r[0] in 'TS' and _sig not in OURS:   # a signal of the process itself: pass it on and keep going
            send('C%02x' % _sig); _sig = 0; r = rd(); continue
        return r
def drain(t=0.3):
    global buf, _sig
    s.settimeout(t)
    try:
        while True:
            r = rd()
            if DBG: sys.stderr.write('(drained %s)\n' % r[:40])
            if r[:1] in ('T', 'S') and int(r[1:3], 16) not in OURS: _sig = int(r[1:3], 16)   # a process signal: passed on by the next cont()
    except (socket.timeout, TimeoutError):
        pass
    finally:
        s.settimeout(None)
def step_over(tid):
    """the checkpoint breakpoint (qemu's own TCG breakpoint) traps again on a plain continue: remove it, single-step this thread, re-arm"""
    cmd('z0,%x,%d' % (cpaddr, cpkind)); cmd('Hc' + tid); send('s'); wait_stop(); cmd('Hc0')
    assert cmd('Z0,%x,%d' % (cpaddr, cpkind)) == 'OK'

cur = -1; t0 = time.time()
while cur < F0:
    cont(); r = wait_stop()
    if r[0] in 'WX': L('process ended: ' + r); sys.exit(1)
    th = stop_thread(r)
    if skip_bkpt(th): continue
    if (reg(th, 15) & ~1) == cpaddr: cur = reg(th, 0); step_over(th)
L('frame %d reached after %.0f s' % (cur, time.time() - t0))
cmd('z0,%x,%d' % (cpaddr, cpkind))
addr = {n: a for a, n in syms}
for fn in FUNCS:
    fa = addr[fn]; kind = 4
    assert cmd('Z0,%x,%d' % (fa, kind)) == 'OK'
    counts = []
    for k in range(NS):
        while True:
            cont(); r = wait_stop(); th = stop_thread(r)
            if r[0] in 'WX': L('ended'); sys.exit(1)
            rr = regs(th)
            if (rr[15] & ~1) == fa: break
        lr = rr[14] & ~1; sp0 = rr[13]
        cmd('z0,%x,%d' % (fa, kind)); cmd('Hc' + th)
        n = 0; hist = collections.Counter()
        while True:
            send('s'); r = wait_stop(); n += 1
            rr = regs(th)
            if k == 0: hist[sym(rr[15] & ~1)] += 1
            if (rr[15] & ~1) == lr and rr[13] >= sp0: break
            if n > 300000: L('runaway'); break
        cmd('Hc0'); assert cmd('Z0,%x,%d' % (fa, kind)) == 'OK'
        counts.append(n); L('%s sample %d: %d instructions' % (fn, k, n))
        if k == 0:
            for nm, c in hist.most_common(40): L('    %6d  %s' % (c, nm))
    cmd('z0,%x,%d' % (fa, kind))
    c = sorted(counts); L('%s: n=%d min %d median %d mean %.1f max %d' % (fn, len(c), c[0], c[len(c) // 2], sum(c) / len(c), c[-1]))
L('done')
try: send('k')
except Exception: pass
