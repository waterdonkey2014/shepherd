#!/usr/bin/env python3
"""Stage C (vita/notes/m1-audio.md): objective checks of the engine's boot audio, rendered by the Vita profile's FMOD bridge
(engine/Shepherd.Engine/Audio) into float WAVs. Numbers only -- nothing is ever played.

  engine_wav_check.py --manifest <bake>/manifest.json --lib libshepaudio.dylib --full A.wav --events A-audio-events.tsv
                      [--repeat A2.wav] [--mute <event path>=<B.wav> ...] [--json out.json]

What it checks
  determinism   A == A2 bit for bit (same boot, same seed, SYNC_IO file output)
  clipping      peak of A, samples at |x| >= 0.999 (must be 0; the master limiter keeps -0.1 dBFS)
  envelope      per-frame RMS (dBFS) of A around the boot's audio events (frame = 800 samples at 60 fps)
  per event E   (B = the same boot with E at volume 0 -- same instances, same take draws, same game logic):
                d = A - B is E's contribution, plus the AAC noise below. Isolation: before E's first start |d| is at most the
                AAC noise bound (exactly 0 while no AAC stream plays). Onset: E's take is aligned on d by least squares (shift
                L between start()'s audio frame and the voice's sample 0, searched over [-64, allowed + 64] output frames, every
                variant of an isolated shepaudio render of E) -- the voice's sample 0 lands at start's audio frame + L, its first
                sound at that + the take's own lead (first non-zero sample of the isolated render); L must be 0 for a RAM take,
                0..800 for a stream (prefill). Gain vs that isolated render at the best alignment (bus + snapshot gains the game
                set: the expected value is printed next to it) and the residual
  STOPPED       every one-shot start in the event log is followed by STOPPED + DESTROYED (or is still in flight at the end); the
                number of updates from start to STOPPED vs the bake's stopFrame of the event's takes
AAC noise (review of 2026-10-07): on the Mac, AudioToolbox's AAC decode (the stand-in for sceAudiodec) differs by 1 LSB of int16
on a few hundred samples between two processes whose call sequences differ (A vs a mute run; A and A2 are bit-identical). The
noise is at most 1 LSB x the voice's gain (<= 1 here) per AAC stream playing at that sample; RAM takes are exact. So the isolation
bound is 1 LSB per playing AAC stream (from the event log; 0 when none plays), and the onset is found by alignment instead of the
first non-zero sample (a lone 1-LSB difference before the real onset used to be taken for it; a stream's own first samples are
1-LSB quanta too, so a threshold would move every stream onset). run-gl.sh MAC_AUDIO=1 applies the same 1-LSB rule.
Exit code 0 = every check passed.
"""
import argparse, ctypes, json, math, struct, sys
import numpy as np

FPS_FRAMES = 800   # output frames per 60 fps engine frame
LSB = 1.0 / 32768  # one int16 step: the AudioToolbox AAC decode difference between processes
AAC_TOL = LSB * 1.001   # per playing AAC stream (+ float32 rounding of the mix: ~1e-8)
SEARCH = 64        # alignment search margin (output frames) on each side of the allowed lateness


def read_wav(path):
    with open(path, 'rb') as f:
        b = f.read()
    assert b[:4] == b'RIFF' and b[8:12] == b'WAVE', path
    pos, fmt, data = 12, None, None
    while pos + 8 <= len(b):
        cid, n = b[pos:pos + 4], struct.unpack('<I', b[pos + 4:pos + 8])[0]
        if cid == b'fmt ':
            fmt = struct.unpack('<HHIIHH', b[pos + 8:pos + 24])
        elif cid == b'data':
            data = b[pos + 8:pos + 8 + n]
        pos += 8 + n + (n & 1)
    tag, ch, rate, _, _, bits = fmt
    x = np.frombuffer(data, dtype=np.float32 if tag == 3 else np.int16).astype(np.float64)
    if tag != 3:
        x /= 32768.0
    return x.reshape(-1, ch), rate


def db(v):
    return 20 * math.log10(max(v, 1e-12))


def rms(x):
    return float(np.sqrt(np.mean(x * x))) if x.size else 0.0


def read_events(path):
    rows = []
    with open(path) as f:
        head = f.readline().rstrip('\n').split('\t')
        for line in f:
            p = line.rstrip('\n').split('\t')
            rows.append(dict(zip(head, p)))
    for r in rows:
        r['frame'] = int(r['frame']); r['audio_frame'] = int(r['audio_frame'])
    return rows


class Shep:
    """Isolated renders through the same mixer (libshepaudio, ctypes; memory only)."""
    def __init__(self, lib, manifest):
        L = ctypes.CDLL(lib)
        L.sa_init_opts.restype = ctypes.c_void_p
        L.sa_init_opts.argtypes = [ctypes.c_char_p, ctypes.c_char_p, ctypes.c_void_p, ctypes.c_void_p, ctypes.c_void_p]
        for n in ('sa_update', 'sa_shutdown'):
            getattr(L, n).argtypes = [ctypes.c_void_p]
        L.sa_event_create.restype = ctypes.c_uint32
        L.sa_event_create.argtypes = [ctypes.c_void_p, ctypes.c_char_p]
        L.sa_instance_start.argtypes = [ctypes.c_void_p, ctypes.c_uint32]
        L.sa_instance_release.argtypes = [ctypes.c_void_p, ctypes.c_uint32]
        L.sa_mix.argtypes = [ctypes.c_void_p, ctypes.POINTER(ctypes.c_float), ctypes.c_int]
        self.L, self.manifest = L, manifest.encode()

    def render(self, path, starts, seconds, seed=24301):
        """starts: list of start times (s). Returns (frames x 2) float64."""
        L = self.L
        s = L.sa_init_opts(self.manifest, f'sync_io=1,seed={seed}'.encode(), None, None, None)
        assert s, 'sa_init_opts failed'
        n = int(seconds * 48000)
        out = np.zeros((n, 2), dtype=np.float32)
        buf = (ctypes.c_float * (2 * FPS_FRAMES))()
        pending = sorted(int(t * 48000) for t in starts)
        pos = 0
        while pos < n:
            while pending and pending[0] <= pos:
                h = L.sa_event_create(s, path.encode()); L.sa_instance_start(s, h); L.sa_instance_release(s, h); pending.pop(0)
            L.sa_update(s)
            k = min(FPS_FRAMES, n - pos)
            L.sa_mix(s, buf, k)
            out[pos:pos + k] = np.frombuffer(buf, dtype=np.float32, count=2 * k).reshape(-1, 2)
            pos += k
        L.sa_shutdown(s)
        return out.astype(np.float64)


def first_nonzero(x, thr=0.0):
    idx = np.nonzero(np.max(np.abs(x), axis=1) > thr)[0]
    return int(idx[0]) if idx.size else -1


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--manifest', required=True); ap.add_argument('--lib', required=True)
    ap.add_argument('--full', required=True); ap.add_argument('--events', required=True)
    ap.add_argument('--repeat'); ap.add_argument('--mute', action='append', default=[])
    ap.add_argument('--json')
    a = ap.parse_args()
    fails, out = [], {}

    def check(ok, msg):
        print(('ok   ' if ok else 'FAIL ') + msg)
        if not ok:
            fails.append(msg)

    man = json.load(open(a.manifest))
    evs = {e['path']: e for e in man['events']}
    A, rate = read_wav(a.full)
    assert rate == 48000
    rows = read_events(a.events)
    nfr = A.shape[0] // FPS_FRAMES
    print(f'full mix: {a.full}: {A.shape[0]} frames ({A.shape[0] / 48000:.2f} s = {nfr} engine frames), {len(rows)} event-log rows')

    # determinism
    if a.repeat:
        A2, _ = read_wav(a.repeat)
        same = A2.shape == A.shape and np.array_equal(A, A2)
        check(same, f'determinism: a second boot renders the same WAV bit for bit ({a.repeat})')
        out['deterministic'] = bool(same)

    # clipping
    peak = float(np.max(np.abs(A)))
    clipped = int(np.sum(np.abs(A) >= 0.999))
    check(clipped == 0, f'clipping: peak {peak:.4f} ({db(peak):.2f} dBFS), {clipped} samples at |x| >= 0.999')
    out['peak_dbfs'] = db(peak); out['clipped_samples'] = clipped

    # envelope: per-frame RMS
    fr = A[:nfr * FPS_FRAMES].reshape(nfr, FPS_FRAMES, 2)
    env = 20 * np.log10(np.maximum(np.sqrt(np.mean(fr * fr, axis=(1, 2))), 1e-12))
    starts = [r for r in rows if r['what'] == 'start' and not r['event'].startswith('snapshot:')]
    print('envelope (RMS dBFS per 60 fps frame) around each start the game requested:')
    for r in starts:
        f = r['frame']
        lo, hi = max(0, f - 3), min(nfr, f + 6)
        print(f"  f{f:5d} {r['event']:<50s} " + ' '.join(f'{env[k]:6.1f}' for k in range(lo, hi)) + f'   (frames {lo}..{hi - 1})')
    silent_lead = env[:min(nfr, 120)]
    check(np.all(silent_lead < -100), f'frames 0..119 (before the first sound is requested, f121) are silent: max {silent_lead.max():.1f} dBFS')
    out['envelope_dbfs'] = [round(float(v), 2) for v in env]

    shep = Shep(a.lib, a.manifest)

    # the gain the game's volume snapshots put on an event's bus at a given frame: for each snapshot with a parameter curve
    # (master/music/sfx/vo_bus), its newest started instance's parameter value (event log 'param' rows), interpolated on the
    # bake's measured curve (default = gain 1), multiplied over the event bus's ancestors the curve names
    curves = {}
    for sn in man['snapshots']:
        if not sn.get('curve'):
            continue
        p = sn['params'][0]
        pts = {}
        for c in sn['curve']:
            v = c['paramValues'][p['name']]
            for b, g in c['busVolumes'].items():
                pts.setdefault(b, []).append((v, g))
        for b in pts:
            pts[b] = sorted(pts[b] + [(p['default'], 1.0)])
        curves[sn['path']] = (p['name'], p['default'], pts)

    def snapshot_gain(bus, frame):
        g = 1.0
        anc, b = [], bus
        while True:
            anc.append(b)
            if b in ('bus:/', 'bus:'):
                break
            b = b.rsplit('/', 1)[0]
            if b == 'bus:':
                b = 'bus:/'
        for sp, (pname, pdef, pts) in curves.items():
            ss = [r for r in rows if r['event'] == sp and r['what'] == 'start' and r['frame'] <= frame]
            if not ss:
                continue
            h = ss[-1]['handle']
            vals = [float(r['value'].split('=')[1]) for r in rows if r['handle'] == h and r['what'] == 'param' and r['frame'] <= frame]
            v = vals[-1] if vals else pdef
            for b in anc:
                if b in pts:
                    xs, ys = zip(*pts[b])
                    g *= float(np.interp(v, xs, ys))
        return g

    # the AAC noise bound per output frame: 1 LSB for every AAC stream instance playing (its start's audio frame up to its
    # STOPPED; a stream with no STOPPED plays to the end). 0 where none plays: there A - B must be exactly 0.
    def is_stream(ev):
        return ev in evs and any(t['codec'] == 'aac-adts' for t in evs[ev]['takes'])
    stopped_at = {r['handle']: r['audio_frame'] for r in rows if r['what'] == 'STOPPED'}
    stream_spans = [(r['audio_frame'], stopped_at.get(r['handle'], A.shape[0]), r['event'])
                    for r in rows if r['what'] == 'start' and is_stream(r['event'])]

    def aac_bound(n):
        tol = np.zeros(n)
        for b, e_, _ in stream_spans:
            tol[min(b, n):min(e_, n)] += AAC_TOL
        return tol

    # per muted event
    out['events'] = {}
    for spec in a.mute:
        path, wav = spec.split('=', 1)
        B, _ = read_wav(wav)
        n = min(A.shape[0], B.shape[0])
        d = A[:n] - B[:n]
        st = [r for r in rows if r['what'] == 'start' and r['event'] == path]
        if not st:
            check(False, f'{path}: no start in the event log'); continue
        e = evs[path]
        first = st[0]['audio_frame']
        # isolation: before E's first start, A - B is only the AAC noise (<= 1 LSB per playing stream; exactly 0 where none plays)
        pre = np.max(np.abs(d[:first]), axis=1) if first > 0 else np.zeros(0)
        tol = aac_bound(n)[:first]
        over = int(np.count_nonzero(pre > tol))
        nz = int(np.count_nonzero(pre))
        quiet = int(np.count_nonzero(pre[tol == 0]))   # differences where no AAC stream plays (must be none)
        iso = float(pre.max()) if pre.size else 0.0
        check(over == 0,
              f'{path}: A - B before its first start (audio frame {first}) is within the AAC noise bound (1 LSB per playing AAC '
              f'stream, 0 where none plays): {nz} non-zero sample(s), max {iso * 32768:.2f} LSB; {quiet} where no stream plays, {over} over the bound')
        # the take's variants: isolated renders through the same mixer, many starts so random takes show their variants
        takes = e['takes']
        nst = 1 if len(takes) == 1 else 24
        span = max(1.0, min(4.0, max(t['frames'] for t in takes) / 48000 + 0.3))
        spanf = int(math.ceil(span * 48000 / FPS_FRAMES)) * FPS_FRAMES   # starts on update boundaries, as in the engine
        ref = shep.render(path, [k * spanf / 48000 for k in range(nst)], (nst * spanf + 9600) / 48000)
        leads, refs, seen = set(), [], set()
        for k in range(nst):
            seg = ref[k * spanf:(k + 1) * spanf]
            l = first_nonzero(seg)
            if l >= 0:
                leads.add(l)
                key = hash(seg.tobytes())
                if key not in seen:
                    seen.add(key); refs.append((l, seg))
        info = {'starts': [], 'take_leads': sorted(leads), 'variants': len(refs), 'stream': is_stream(path),
                'pre_start_nonzero': nz, 'pre_start_max_lsb': iso * 32768}
        print(f"{path}: {len(st)} start(s); takes {len(takes)} ({', '.join(sorted(set(t['codec'] for t in takes)))}), "
              f"{len(refs)} distinct variant(s) in {nst} isolated render(s), leads (first non-zero sample) {sorted(leads)}")
        # RAM takes start at the update that follows start(): 0 frames late; a stream (AAC) has its prefill first: one update
        allow = FPS_FRAMES if info['stream'] else 0
        for i, r in enumerate(st):
            s0 = r['audio_frame']
            s1 = st[i + 1]['audio_frame'] if i + 1 < len(st) else n
            if not np.any(np.abs(d[s0:s1]) > LSB * 1.5):
                check(False, f'{path} start #{i + 1} (f{r["frame"]}): no audio in A - B'); continue
            # the alignment: the variant and the shift L (start's audio frame -> the voice's sample 0) with the least residual
            # over 1 s from the take's lead; d[s0 + L + j] = g x variant[j]
            best = None
            for l, rs in refs:
                for L in range(-SEARCH, allow + SEARCH + 1):
                    o = s0 + L + l
                    m = min(len(rs) - l, s1 - o, 48000)
                    if o < 0 or m <= 0:
                        continue
                    x, y = d[o:o + m].ravel(), rs[l:l + m].ravel()
                    g = float(np.dot(x, y) / max(np.dot(y, y), 1e-20))
                    res = rms(x - g * y) / max(rms(x), 1e-20)
                    if best is None or res < best[3]:
                        best = (l, L, g, res)
            l, late, g, res = best
            on = late + l   # the first sound, relative to start's audio frame
            lvl = rms(d[s0 + on:s0 + on + 24000])
            info['starts'].append({'frame': r['frame'], 'audio_frame': s0, 'onset': s0 + on, 'lead': l, 'late_frames': late,
                                   'gain_db': db(abs(g)), 'residual_db': db(res), 'rms_dbfs_500ms': db(lvl)})
            check(0 <= late <= allow,
                  f"{path} start #{i + 1} at f{r['frame']}: sound at audio frame {s0 + on} = start's {s0} + the take's lead {l} + {late} "
                  f"({late / 48:.1f} ms, allowed {allow / 48:.1f}: {'stream prefill' if info['stream'] else 'RAM take'}; best alignment "
                  f"of {len(refs)} variant(s) over shifts {-SEARCH}..{allow + SEARCH}), engine frame {(s0 + on) // FPS_FRAMES}")
            eg = db(snapshot_gain(e['bus'], r['frame']))
            info['starts'][-1]['expected_gain_db'] = eg
            check(abs(db(abs(g)) - eg) < 0.1 and db(res) < -60,
                  f"{path} start #{i + 1}: gain vs the isolated render {db(abs(g)):+.2f} dB (expect {eg:+.2f}: the volume snapshots on {e['bus']}), "
                  f"residual {db(res):.1f} dB, level {db(lvl):.1f} dBFS RMS over 0.5 s")
        out['events'][path] = info

    # STOPPED / DESTROYED for every one-shot start
    print('one-shot lifecycle (event log):')
    byh = {}
    for r in rows:
        byh.setdefault(r['handle'], []).append(r)
    last = nfr - 1   # the run's last engine frame
    lc = []
    for h, rs in byh.items():
        ev = rs[0]['event']
        if ev.startswith('snapshot:') or ev not in evs or not evs[ev]['oneshot']:
            continue
        stt = next((r for r in rs if r['what'] == 'start'), None)
        sto = next((r for r in rs if r['what'] == 'STOPPED'), None)
        des = next((r for r in rs if r['what'] == 'DESTROYED'), None)
        if stt is None:
            continue
        sfs = sorted({t['stopFrame'] for t in evs[ev]['takes'] if t.get('stopFrame') is not None})
        stream = any(t['codec'] == 'aac-adts' for t in evs[ev]['takes'])
        if sto is None:
            # still in flight: fine if its shortest stopFrame has not passed yet
            ok = not sfs or (last - stt['frame']) * FPS_FRAMES < sfs[0] + 2 * FPS_FRAMES
            check(ok, f"{ev} #{h}: started f{stt['frame']}, still playing at the end f{last} (stopFrame(s) {[round(s / FPS_FRAMES, 1) for s in sfs]} frames)")
            lc.append({'event': ev, 'start': stt['frame'], 'stopped': None})
            continue
        # the voice starts at the update whose audio clock is the start's audio frame (u0: the same frame if start() came before
        # that frame's Studio update, the next one if after); STOPPED is reported at the first update whose clock has passed the
        # take's stopFrame: u0 + ceil(stopFrame / 800), exactly, for one of the event's takes (random takes: whichever played)
        u0 = (stt['audio_frame'] + FPS_FRAMES - 1) // FPS_FRAMES
        cand = sorted({u0 + (s + FPS_FRAMES - 1) // FPS_FRAMES for s in sfs})
        ok = des is not None and des['frame'] == sto['frame'] and sto['frame'] in cand
        check(ok, f"{ev} #{h}: start() f{stt['frame']} (voice from update {u0}) -> STOPPED f{sto['frame']} (expected f{' or f'.join(map(str, cand))}: "
                  f"the bake's stopFrame {', '.join(f'{s / FPS_FRAMES:.2f}' for s in sfs)} frames) -> DESTROYED f{des['frame'] if des else None}")
        lc.append({'event': ev, 'start': stt['frame'], 'stopped': sto['frame'], 'destroyed': des['frame'] if des else None})
    out['oneshots'] = lc
    if a.json:
        json.dump(out, open(a.json, 'w'), indent=1)
    print(f"{'PASS' if not fails else 'FAIL'} engine_wav_check: {len(fails)} failed check(s)")
    return 0 if not fails else 1


if __name__ == '__main__':
    sys.exit(main())
