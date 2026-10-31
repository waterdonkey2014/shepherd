#!/usr/bin/env python3
"""Kit #8 boot track (vita/notes/kit8-boot.md): the game's videos as .shv clips for the Vita's hardware H.264 decoder.

Reads the owner's own copy of the game's clips (StreamingAssets/MMVideoPlayer/Videos/*.mp4) and writes one .shv per clip (the format is
vita/shepherd/k8_shv.h): the clip decoded with the local ffmpeg, scaled to the Vita's screen the way the game shows it -- the MMVideoPlayer
prefab draws the video at its camera's near plane with Fit Inside, so a 16:9 clip fills 960 x 540 of the 960 x 544 screen with 2-px black
bars above and below; 1920 x 1080 -> 960 x 540 is an exact 2:1, done as the 2x2 average a GPU's bilinear sampling of the full-size
texture gives at that ratio (ffmpeg's area scaler) -- converted to BT.601 limited range (the decoder's colour conversion), and encoded
with the local x264 as H.264 Main profile level 3.1 without B-frames (one access unit in, one picture out), near-transparent quality
(CRF, default 12) inside the level's 14 Mbit/s. Every frame of the clip is kept (no frame-rate change: the .shv's frame count and rate
are the clip's -- the engine's length table and the clip agree). For the Devolver logo the .shv also carries its sound: the game's own
FMOD event (event:/music/devolver_pre_roll/DevolverSplash) from the owner's audio bake (--audio-bake), the sound the PC game plays with
the logo, as 16-bit stereo PCM (the bake's encoder delay removed, its frame count kept) -- the launcher plays it with the splash.

Device kit #9 (track device, vita/notes/kit9-device.md): the owner's Vita gave NO picture for the first access unit of kit #8's clips
(the hardware decoder returns pictures some access units after it gets them). Kit #8's clips already had no B-frames and said
max_num_reorder_frames 0 (x264 writes it for bframes=0); what was left is the decoded picture buffer the SPS allows the decoder,
max_dec_frame_buffering = the reference count (3). --ref sets it: the default stays 3 = kit #8's clips, byte for byte from the same source
and x264 (kit #9 review round 2, finding 3: the pictures are the ones kit #8's records hold, and no device run has shown yet that the
decoder's delay follows max_dec_frame_buffering -- the launcher and the engine tolerate the delay whatever the clip, vita/shepherd/k8_avc.h);
--ref 1 (opt-in, owner-local: `STAGE_SPLASH_ONLY=1 STAGE_SPLASH_REF=1 stage-owner.sh <out>` writes ONLY data/video/*.shv into the existing
card <out>/shepherd -- kit #9 integration review fix: a plain `stage-owner.sh` rebuilds the whole card, managed/ included, from the install +
runtime/ManagedVita, which no kit #9 eboot boots on (the typed modules' MVIDs); or copy the gated
$SHEPHERD_WORK/kit9/device-work/clips-ref1/*.shv into the card's data/video/) gives max_dec_frame_buffering 1 -- the smallest buffer a decoder may hold
back for, i.e. at most one held picture at the clip's end -- a slightly different encode (PSNR min 48.60 / mean 54.31 dB vs 48.65 / 54.33
for the Devolver logo); the .shv's flags carry F_DPB1 for it. Once on the card the engine's clip path is fixed (data/video/<clip>.shv):
re-staging with the default gives kit #8's clips back.

The output is GAME-DERIVED (the owner's videos): owner folder only (ux0:data/shepherd/data/video/), never in git, never in a VPK.
--check decodes each .shv's pictures back with ffmpeg and compares them with the scaled source frames (PSNR per frame; the minimum and the
mean are printed), and checks the frame count against the source's.

usage: splash_pack.py <Videos dir | clip.mp4> -o <out dir> [--clips Name,Name] [--audio-bake <audiobake/menu>] [--crf 12] [--ref 3] [--check]
Only reads its inputs; standard library + the local ffmpeg / ffprobe (no download)."""
import argparse, json, os, struct, subprocess, sys, tempfile

HDR = 192
MAGIC = 0x31564853
F_NO_BFRAMES, F_BT601, F_AUDIO_BAKE, F_DPB1 = 1, 2, 4, 8   # F_DPB1 (kit #9): one reference picture, max_dec_frame_buffering 1
W, H = 960, 544
EVENT_FOR_CLIP = {"Devolver_Animated_Logo_v001_4k_Audio-_HQ_HBR_MP4": "event:/music/devolver_pre_roll/DevolverSplash"}
DEFAULT_CLIPS = ["Devolver_Animated_Logo_v001_4k_Audio-_HQ_HBR_MP4", "Intro"]   # the two clips the kit routes reach

def fnv32(b, h=2166136261):
    for x in b:
        h = ((h ^ x) * 16777619) & 0xffffffff
    return h

def run(cmd, **kw):
    r = subprocess.run(cmd, stdout=subprocess.PIPE, stderr=subprocess.PIPE, **kw)
    if r.returncode:
        sys.stderr.write(r.stderr.decode("utf-8", "replace")[-2000:])
        raise SystemExit(f"failed: {' '.join(cmd[:3])} ...")
    return r.stdout

def probe(path):
    out = json.loads(run(["ffprobe", "-v", "error", "-select_streams", "v:0", "-count_frames", "-show_entries",
                          "stream=width,height,r_frame_rate,nb_read_frames", "-of", "json", path]))
    s = out["streams"][0]
    num, den = (int(x) for x in s["r_frame_rate"].split("/"))
    return int(s["width"]), int(s["height"]), num, den, int(s["nb_read_frames"])

def fit_inside(sw, sh):
    """Unity's VideoAspectRatio.FitInside on a 960 x 544 screen: the largest size with the clip's aspect that fits, centred."""
    if sw * H >= sh * W:
        cw, ch = W, (sh * W + sw // 2) // sw
    else:
        cw, ch = (sw * H + sh // 2) // sh, H
    cw -= cw & 1; ch -= ch & 1
    return cw, ch, (W - cw) // 2, (H - ch) // 2

def vfilter(sw, sh):
    cw, ch, cx, cy = fit_inside(sw, sh)
    return (f"scale={cw}:{ch}:flags=area:in_color_matrix=bt709:out_color_matrix=bt601:in_range=tv:out_range=tv,"
            f"pad={W}:{H}:{cx}:{cy}:color=black,format=yuv420p"), (cw, ch, cx, cy)

def split_aus(es):
    """Annex-B stream -> access units, split at each access unit delimiter (x264 aud=1); AUD and SEI NAL units dropped."""
    pos, n, starts = 0, len(es), []
    while True:
        i = es.find(b"\x00\x00\x01", pos)
        if i < 0: break
        z = i - 1 if i > 0 and es[i - 1] == 0 else i      # a 4-byte start code begins one byte earlier
        starts.append((z, i + 3)); pos = i + 3
    nals = []
    for k, (z, p) in enumerate(starts):
        end = starts[k + 1][0] if k + 1 < len(starts) else n
        nals.append((es[p] & 0x1f, es[p:end]))
    aus, cur = [], []
    for t, body in nals:
        if t == 9:
            if cur: aus.append(cur)
            cur = []
            continue
        if t == 6: continue
        cur.append(b"\x00\x00\x00\x01" + body)
    if cur: aus.append(cur)
    return [b"".join(a) for a in aus]

def bake_audio(bake_dir, event):
    m = json.load(open(os.path.join(bake_dir, "manifest.json")))
    evs = m["events"] if isinstance(m["events"], list) else list(m["events"].values())
    ev = next((e for e in evs if e.get("path") == event), None)
    if not ev or not ev.get("takes"): raise SystemExit(f"{event}: not in the audio bake {bake_dir}")
    t = ev["takes"][0]
    if t.get("channels") != 2 or t.get("rate") != 48000: raise SystemExit(f"{event}: not 48 kHz stereo")
    pcm = run(["ffmpeg", "-v", "error", "-i", os.path.join(bake_dir, t["file"]), "-f", "s16le", "-acodec", "pcm_s16le", "-ar", "48000", "-ac", "2", "-"])
    skip, frames = int(t.get("encoderDelay") or 0), int(t["frames"])
    pcm = pcm[skip * 4: (skip + frames) * 4]
    if len(pcm) < frames * 4: pcm += b"\x00" * (frames * 4 - len(pcm))
    return pcm, frames, f"{event} take 0 ({t['file']}, {frames} frames, encoder delay {skip} removed)"

def pack(src, out_dir, crf, bake_dir, tmp, ref=3):
    name = os.path.splitext(os.path.basename(src))[0]
    sw, sh, num, den, frames = probe(src)
    vf, (cw, ch, cx, cy) = vfilter(sw, sh)
    h264 = os.path.join(tmp, name + ".h264")
    run(["ffmpeg", "-v", "error", "-y", "-i", src, "-an", "-vf", vf, "-fps_mode", "passthrough", "-c:v", "libx264", "-preset", "slow",
         "-crf", str(crf), "-profile:v", "main", "-level:v", "3.1", "-x264-params",
         "bframes=0:ref=%d:keyint=600:min-keyint=600:scenecut=0:aud=1:vbv-maxrate=14000:vbv-bufsize=14000:colorprim=smpte170m:transfer=smpte170m:colormatrix=smpte170m" % ref,
         "-f", "h264", h264])
    aus = split_aus(open(h264, "rb").read())
    if len(aus) != frames: raise SystemExit(f"{name}: {len(aus)} access units for {frames} frames")
    pcm, aframes, aw = b"", 0, "none"
    if bake_dir and name in EVENT_FOR_CLIP:
        pcm, aframes, aw = bake_audio(bake_dir, EVENT_FOR_CLIP[name])
    with open(src, "rb") as f: head = f.read(1 << 20)
    index_off = HDR
    es_off = index_off + 8 * frames
    es = b"".join(aus)
    audio_off = (es_off + len(es) + 15) & ~15
    flags = F_NO_BFRAMES | F_BT601 | (F_AUDIO_BAKE if pcm else 0) | (F_DPB1 if ref == 1 else 0)
    fields = [MAGIC, 1, HDR, W, H, num, den, frames, index_off, es_off, len(es), max(len(a) for a in aus),
              audio_off if pcm else 0, len(pcm), 48000 if pcm else 0, 2 if pcm else 0, aframes,
              frames, num, den, flags, cw, ch, cx, cy, os.path.getsize(src), fnv32(head)]
    idx, off = b"", 0
    for a in aus: idx += struct.pack("<II", off, len(a)); off += len(a)
    hdr = struct.pack("<27I", *fields) + name.encode("ascii")[:63].ljust(64, b"\x00") + struct.pack("<I", fnv32(idx)) + b"\x00" * 12
    hdr += struct.pack("<I", fnv32(hdr))
    assert len(hdr) == HDR
    body = hdr + idx + es
    body += b"\x00" * ((audio_off - len(body)) if pcm else 0) + pcm
    out = os.path.join(out_dir, name + ".shv")
    with open(out + ".tmp", "wb") as f: f.write(body)
    os.replace(out + ".tmp", out)
    note = (f"{name}.shv: {frames} frames at {num}/{den} ({frames * den / num:.3f} s) from {os.path.basename(src)} ({sw}x{sh}, {os.path.getsize(src)} B); "
            f"picture {cw}x{ch} at {cx},{cy} in {W}x{H}; H.264 Main 3.1, no B-frames, {ref} reference picture(s) (max_dec_frame_buffering {ref}), CRF {crf}: {len(es)} B ({len(es) * 8 * num / den / frames / 1e6:.2f} Mbit/s), "
            f"largest picture {max(len(a) for a in aus)} B; audio: {aw}; file {len(body)} B")
    return out, note, h264, vf

def check(src, shv, h264, vf, tmp):
    """PSNR of each decoded picture against the scaled source frame (both through the same filter)."""
    d = open(shv, "rb").read()
    frames, index_off, es_off = struct.unpack_from("<I", d, 28)[0], struct.unpack_from("<I", d, 32)[0], struct.unpack_from("<I", d, 36)[0]
    es = b"".join(d[es_off + o: es_off + o + s] for o, s in (struct.unpack_from("<II", d, index_off + 8 * i) for i in range(frames)))
    back = os.path.join(tmp, "back.h264"); open(back, "wb").write(es)
    log = os.path.join(tmp, "psnr.log")
    run(["ffmpeg", "-v", "error", "-y", "-i", back, "-i", src, "-filter_complex", f"[1:v]{vf}[ref];[0:v][ref]psnr=stats_file={log}", "-f", "null", "-"])
    vals = []
    for line in open(log):
        for kv in line.split():
            if kv.startswith("psnr_avg:"):
                v = kv.split(":")[1]; vals.append(float("inf") if v == "inf" else float(v))
    finite = [v for v in vals if v != float("inf")]
    return len(vals), (min(finite) if finite else float("inf")), (sum(finite) / len(finite) if finite else float("inf"))

def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("src"); ap.add_argument("-o", "--out", required=True)
    ap.add_argument("--clips", default=",".join(DEFAULT_CLIPS), help="clip names (default: the Devolver logo and the intro); 'all' = every .mp4")
    ap.add_argument("--audio-bake", default=""); ap.add_argument("--crf", type=int, default=12); ap.add_argument("--check", action="store_true")
    ap.add_argument("--ref", type=int, default=3, choices=[1, 2, 3], help="reference pictures = the SPS's max_dec_frame_buffering (default 3 = kit #8's clips byte for byte; 1 = kit #9's opt-in)")
    a = ap.parse_args()
    os.makedirs(a.out, exist_ok=True)
    if os.path.isfile(a.src): srcs = [a.src]
    else:
        names = sorted(f[:-4] for f in os.listdir(a.src) if f.lower().endswith(".mp4"))
        want = names if a.clips == "all" else [c.strip() for c in a.clips.split(",") if c.strip()]
        missing = [c for c in want if c not in names]
        if missing: raise SystemExit(f"not in {a.src}: {missing}")
        srcs = [os.path.join(a.src, c + ".mp4") for c in want]
    rc = 0
    with tempfile.TemporaryDirectory(dir=os.environ.get("TMPDIR")) as tmp:
        for s in srcs:
            out, note, h264, vf = pack(s, a.out, a.crf, a.audio_bake, tmp, a.ref)
            print(note)
            if a.check:
                n, mn, mean = check(s, out, h264, vf, tmp)
                ok = n == struct.unpack_from("<I", open(out, "rb").read(), 28)[0]
                print(f"  check: {n} pictures decoded back (frame count {'= the clip' if ok else 'DIFFERS'}); PSNR vs the scaled source min {mn:.2f} dB, mean {mean:.2f} dB")
                rc |= 0 if ok else 1
    return rc

if __name__ == "__main__":
    sys.exit(main())
