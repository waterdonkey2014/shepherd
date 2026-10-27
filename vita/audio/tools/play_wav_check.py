#!/usr/bin/env python3
"""kit3 play mode (vita/notes/play-mode.md): is the audio of a slow-frame-rate run at the right speed and pitch?

Input: a WAV from the engine's real-time output (SHEPHERD_AUDIO_OUT=rtfile:..., shepaudio's sa_rt_out thread = the Mac stand-in for the
Vita's sceAudioOut thread), its audio-events.tsv and engine.log. Numbers only -- nothing is ever played.

  play_wav_check.py --manifest <bake>/manifest.json --lib libshepaudio.dylib --wav run.wav --events audio-events.tsv --log engine.log
                    [--event event:/music/menu/menu_title] [--gain-db -2.50] [--json out.json]

Checks
  pace     the WAV's length vs the wall time the output thread ran (engine.log "[audio] rt output" line): the audio clock is the
           hardware's (1.000x), whatever the game's frame rate (the run's fps from the "[time] wall clock" line)
  music    the event's take (an isolated render through the same mixer, ctypes, memory only) is found in the WAV after its start():
           coarse alignment by FFT cross-correlation, then the lag measured again in every 0.5 s window over the whole rest of the run
           (+-256 samples, exact integer search) where no other event sounds (another start() .. its STOPPED + 1 s, from the event
           log). One lag for every such window, from the first to the last second of the run = the take plays contiguously at exactly
           1.0x: no stretch (a speed error e drifts e x 48000 samples per second), no gap, no repeat, no skipped grain -- so the pitch
           is the take's. Gain (least squares) vs the expected bus/snapshot gain, and the residual there (only the Mac's 1-LSB AAC
           decode difference between processes may remain: ~ -95 dB).
Exit 0 = every check passed."""
import argparse, json, math, re, sys
import numpy as np
sys.path.insert(0, __import__('os').path.dirname(__file__))
from engine_wav_check import read_wav, read_events, Shep, db   # same WAV reader, event log reader and isolated renderer

ap = argparse.ArgumentParser()
ap.add_argument('--manifest', required=True); ap.add_argument('--lib', required=True)
ap.add_argument('--wav', required=True); ap.add_argument('--events', required=True); ap.add_argument('--log', required=True)
ap.add_argument('--event', default='event:/music/menu/menu_title')
ap.add_argument('--gain-db', type=float, default=-2.50, help="the game's settings: music bus 0.75 = -2.50 dB")
ap.add_argument('--json')
a = ap.parse_args()
R = 48000
fails, res = [], {}
def ok(cond, msg):
    print(('ok   ' if cond else 'FAIL ') + msg)
    if not cond: fails.append(msg)

x, rate = read_wav(a.wav)
assert rate == R
log = open(a.log, errors='replace').read()
# ---- pace
m = re.search(r'rt output .*?: (\d+) frames = ([0-9.]+) s of audio in ([0-9.]+) s of wall time \(([0-9.]+)x\), worst lateness ([0-9.]+) ms, (\d+) late', log)
t = re.search(r'\[time\] wall clock: (\d+) frames over ([0-9.]+) s of wall time \(([0-9.]+) fps\); Time.unscaledTime ([0-9.]+) s, Time.time ([0-9.]+) s', log)
if not m: ok(False, 'no "[audio] rt output" line in the log'); sys.exit(1)
frames, secs, wall, ratio, late_ms, late_n = int(m[1]), float(m[2]), float(m[3]), float(m[4]), float(m[5]), int(m[6])
fps = float(t[3]) if t else float('nan')
res['pace'] = dict(frames=frames, audio_s=secs, wall_s=wall, ratio=ratio, late_ms=late_ms, late_grains=late_n, game_fps=fps,
                   game_time_s=float(t[5]) if t else None, unscaled_s=float(t[4]) if t else None)
ok(len(x) == frames, f'WAV = what the output thread mixed ({len(x)} frames)')
ok(abs(ratio - 1) < 0.002, f'the audio clock ran at the hardware pace: {secs:.3f} s of audio in {wall:.3f} s of wall time ({ratio:.4f}x) '
   f'while the game ran at {fps:.2f} fps')
ok(late_n == 0, f'the output thread never fell behind real time ({late_n} late grains, worst {late_ms:.2f} ms)')

# ---- the music
ev = read_events(a.events)
starts = [r for r in ev if r['what'] == 'start' and r['event'] == a.event]
if not starts: ok(False, f'no start of {a.event} in the event log'); sys.exit(1)
af = starts[0]['audio_frame']; f0 = starts[0]['frame']
span = len(x) - af
ref = Shep(a.lib, a.manifest).render(a.event, [0.0], span / R + 1.0)
mono = lambda y: y[:, 0] + y[:, 1]
xm, rm = mono(x), mono(ref)
# coarse: where does the take's sample 0 sit after start()'s audio frame? (FFT cross-correlation over 8 s)
T = min(8 * R, span - R)
seg, tmpl = xm[af:af + T], rm[:T - R // 2]
nfft = 1 << int(math.ceil(math.log2(len(seg) + len(tmpl))))
cc = np.fft.irfft(np.fft.rfft(seg, nfft) * np.conj(np.fft.rfft(tmpl, nfft)), nfft)[:len(seg) - len(tmpl) + 1]
L = int(np.argmax(cc))
lead = int(np.nonzero(np.abs(rm) > 0)[0][0])
ok(L < R // 2, f'{a.event}: start() at engine frame {f0}, audio frame {af}; the take begins {L} frames ({L / R * 1000:.1f} ms) later '
   f'(next Studio update + stream prefill + the queued grain), its first sound {lead} frames into the take')
# fine: the lag in every 0.5 s window over the rest of the run
# the other events' sounding spans: start() .. its STOPPED (same handle) + 1 s (tails), or the end of the run
stopped = {r['handle']: r['audio_frame'] for r in ev if r['what'] == 'STOPPED'}
other = [(r['audio_frame'], stopped.get(r['handle'], len(x)) + R) for r in ev
         if r['what'] == 'start' and r['event'] != a.event and not r['event'].startswith('snapshot:')]
W, S = R // 2, 256
lags, gains, clean_resid, busy, first, last = [], [], [], 0, None, None
p = R   # skip the first second (the take's lead-in)
while af + L + p + W + S < len(x) and p + W + S < len(rm):
    pos = af + L + p
    if any(lo < pos + W + S and pos - S < hi for lo, hi in other): busy += 1; p += W; continue   # another event sounds here
    xs = xm[pos - S: pos + W + S]
    rs = rm[p:p + W]
    sc = np.correlate(xs, rs, mode='valid')          # 2S + 1 lags
    k = int(np.argmax(sc)) - S
    lags.append(k)
    xw, rw = x[pos + k:pos + k + W], ref[p:p + W]
    g = float(np.sum(xw * rw) / max(np.sum(rw * rw), 1e-30))
    e = float(np.sqrt(np.mean((xw - g * rw) ** 2)) / max(np.sqrt(np.mean((g * rw) ** 2)), 1e-30))
    gains.append(g); clean_resid.append(e)
    first = pos if first is None else first; last = pos + W
    p += W
n = len(lags)
uniq = sorted(set(lags))
dur = n * W / R
spread = (last - first) / R if n else 0
g_med = float(np.median(gains)) if gains else 0
res['music'] = dict(event=a.event, start_frame=f0, start_audio_frame=af, take_offset=L, windows=n, busy_windows=busy, seconds=dur,
                    span_s=spread, lags=uniq, gain_db=db(g_med), resid_db_median=db(float(np.median(clean_resid))) if clean_resid else None,
                    resid_db_max=db(max(clean_resid)) if clean_resid else None)
ok(n >= 20, f'{n} windows of 0.5 s where only the music sounds = {dur:.1f} s of the take, spread over {spread:.1f} s of the run up to its end '
   f'({busy} windows skipped: other events sounding)')
ok(uniq == [0], f'one lag in every window ({uniq}): the take plays contiguously at exactly 1.0x speed for {spread:.1f} s '
   f'(drift 0 samples; a 0.01 % speed error would drift {spread * R * 1e-4:.0f})')
ok(abs(db(g_med) - a.gain_db) < 0.1, f'gain {db(g_med):.2f} dB (the music bus at the game\'s 0.75: expected {a.gain_db:.2f} dB)')
if clean_resid:
    ok(db(max(clean_resid)) < -80, f'residual after the gain-matched take: median {db(float(np.median(clean_resid))):.1f} dB, worst '
       f'{db(max(clean_resid)):.1f} dB (the same samples: same speed, same pitch; the Mac\'s AAC decode differs by 1 LSB between processes)')
print(f"{'PASS' if not fails else 'FAIL'} play_wav_check: {len(fails)} failed")
if a.json:
    res['fails'] = fails
    json.dump(res, open(a.json, 'w'), indent=1)
sys.exit(1 if fails else 0)
