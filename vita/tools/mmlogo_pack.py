#!/usr/bin/env python3
"""Kit #12 boot track (vita/notes/kit12-boot.md section 2): the Massive Monster logo as a .shv clip for the launcher's boot sequence.

The game's own splash (LoadMainMenu.RunSplashScreens) plays, after a 2 s black, the Massive Monster logo -- a Spine animation (SkeletonGraphic
'animation') with its pre-roll sound (event:/music/mm_pre_roll/mm_pre_roll) -- and then the Devolver video. The owner's boot sequence (owner
2026-10-29 / 30) plays the Devolver video first (the launcher's splash), then this logo, then the loading screen until the Main Menu, so nothing
plays twice: the logo has to play before the engine is up, so it is RECORDED -- the engine's own screenshots of level0 on the Mac from the owner's
copy (vita/tools/mmlogo_record.sh: the Vita profile's 960 x 544, the GL backend, the frame clock), every second frame (the animation is timed by
real time: 60 frames a second on the frame clock, 30 pictures a second here, the boot screen's rate), from the first frame the logo shows to the
last before the screen goes black again -- and encoded like the Devolver splash (vita/tools/splash_pack.py: H.264 Main 3.1, no B-frames, BT.601
limited range for the decoder's own colour conversion, the clip's sound as 16-bit stereo PCM from the owner's audio bake, its encoder delay
removed), so the launcher plays it on the same decoder path, with its sound.
The output is GAME-DERIVED (the owner's copy, drawn): the owner folder only (data/video/MassiveMonster.shv), never in git, never in a VPK.
--check decodes the clip back with ffmpeg and gives each picture's PSNR against its screenshot.
Kit #13 fixlooks (vita/notes/kit13-fixlooks.md section 3): the logo is pure red on black (G = B = 0 in all 200 pictures), so nearly all of its edges live
in Cr, which 4:2:0 keeps at half resolution: with NO encoder at all (the screenshot through 4:2:0 and back) the picture stops at 37.0 dB, what the
kit #12 clip measures -- a higher bit rate cannot help. --luma-red encodes the RED CHANNEL AS A GREY PICTURE (every edge in full-resolution luma, the
chroma neutral), flags the clip K8SHV_F_LUMA_RED and stores the rectangle the logo ever touches as the content rectangle: a launcher that knows the
flag clears G and B there before each picture is shown. A launcher that does NOT know the flag (kit #13 as shipped) shows it GREY: keep the two
together. --chroma-qp-offset passes x264's chroma QP offset (the plain RGB clip at more chroma bits, for the launcher as shipped).
usage: mmlogo_pack.py <shots dir> -o <out dir> --audio-bake <audiobake/menu> [--from N --to M] [--crf 12] [--ref 3] [--check] [--luma-red] [--chroma-qp-offset N] [--black-first N]
Only reads its inputs; standard library + the local ffmpeg (no download)."""
import argparse, os, re, struct, subprocess, sys, tempfile
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import splash_pack as sp

W, H, NAME, EVENT = 960, 544, "MassiveMonster", "event:/music/mm_pre_roll/mm_pre_roll"

def frames_of(shots):
    return sorted(int(m.group(1)) for f in os.listdir(shots) for m in [re.match(r"f(\d+)\.png$", f)] if m)

def lit(path):
    """the screenshot has something on it (any channel above 16 in more than 64 pixels)"""
    raw = sp.run(["ffmpeg", "-v", "error", "-i", path, "-vf", "scale=240:136:flags=area", "-f", "rawvideo", "-pix_fmt", "rgb24", "-"])
    return sum(1 for i in range(0, len(raw), 3) if max(raw[i], raw[i + 1], raw[i + 2]) > 16) > 4

def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("shots"); ap.add_argument("-o", "--out", required=True); ap.add_argument("--audio-bake", required=True)
    ap.add_argument("--from", dest="f0", type=int, default=0); ap.add_argument("--to", dest="f1", type=int, default=0)
    ap.add_argument("--crf", type=int, default=12); ap.add_argument("--ref", type=int, default=3, choices=[1, 2, 3]); ap.add_argument("--check", action="store_true")
    ap.add_argument("--luma-red", action="store_true"); ap.add_argument("--chroma-qp-offset", type=int, default=0)
    ap.add_argument("--black-first", type=int, default=0, help="the first N pictures replaced by the picture after them (kit #13 fixlooks: the flash, below)")
    a = ap.parse_args()
    fs = frames_of(a.shots)
    if not fs: raise SystemExit(f"no fNNNNN.png in {a.shots}")
    f0, f1 = a.f0, a.f1
    if not f0:   # the first lit frame, then the run of lit frames from there
        f0 = next((f for f in fs if lit(os.path.join(a.shots, f"f{f:05d}.png"))), 0)
        if not f0: raise SystemExit("no frame shows anything")
    if not f1:   # the last lit frame before the screen stays black for 10 frames, at least a second after the first (the logo's own animation
        f1, dark = f0, 0   # starts with a lit frame, then builds up from black: the dark frames inside it are part of the clip)
        for f in fs:
            if f <= f0: continue
            if lit(os.path.join(a.shots, f"f{f:05d}.png")): f1, dark = f, 0
            else:
                dark += 1
                if dark >= 10 and f1 >= f0 + 60: break
    keep = list(range(f0, f1 + 1, 2))
    missing = [f for f in keep if not os.path.exists(os.path.join(a.shots, f"f{f:05d}.png"))]
    if missing: raise SystemExit(f"screenshots missing: {missing[:5]} ...")
    rect = (0, 0, W, H)
    if a.luma_red:   # the clip must really be red only, and the rectangle covers every lit pixel of every kept picture (rounded out to 16 px)
        x0, y0, x1, y1 = W, H, 0, 0
        for k in keep:
            raw = sp.run(["ffmpeg", "-v", "error", "-i", os.path.join(a.shots, f"f{k:05d}.png"), "-f", "rawvideo", "-pix_fmt", "rgb24", "-"])
            if any(raw[1::3]) or any(raw[2::3]): raise SystemExit(f"f{k}: not red only: --luma-red would lose G / B")
            for y in range(H):
                row = raw[y * W * 3:(y + 1) * W * 3:3]
                if any(row):
                    y0, y1 = min(y0, y), max(y1, y)
                    x0 = min(x0, next(i for i, v in enumerate(row) if v)); x1 = max(x1, W - 1 - next(i for i, v in enumerate(reversed(row)) if v))
        x0 &= ~15; y0 &= ~15; x1 = min(W, (x1 + 16) & ~15); y1 = min(H, (y1 + 16) & ~15)
        rect = (x0, y0, x1 - x0, y1 - y0)
    vf = "scale=960:544:flags=area:out_color_matrix=bt601:out_range=tv,format=yuv420p"
    if a.luma_red: vf = "colorchannelmixer=rr=1:rg=0:rb=0:gr=1:gg=0:gb=0:br=1:bg=0:bb=0," + vf   # R into all three channels: a grey picture
    x264 = "bframes=0:ref=%d:keyint=600:min-keyint=600:scenecut=0:aud=1:vbv-maxrate=14000:vbv-bufsize=14000:colorprim=smpte170m:transfer=smpte170m:colormatrix=smpte170m" % a.ref
    if a.chroma_qp_offset: x264 += ":chroma-qp-offset=%d" % a.chroma_qp_offset
    with tempfile.TemporaryDirectory(dir=os.environ.get("TMPDIR")) as tmp:
        # (kit #13 fixlooks: an image sequence, one input picture = one output picture. The concat list + -r 30 this tool used before rounded its 1/30 s
        # durations and repeated / dropped pictures: the kit #12 clip's pictures lag their screenshots by 1-3 pictures, up to 0.1 s behind the sound)
        seq = os.path.join(tmp, "seq"); os.makedirs(seq)
        # (--black-first: the logo's FIRST screenshot is its whole setup pose for one frame, black again on the next: on the Vita a one-picture red flash right after
        # the Devolver clip's fade. N = 1 shows the black picture instead: the same 200 pictures, the same sound)
        for i, k in enumerate(keep): os.symlink(os.path.join(os.path.abspath(a.shots), f"f{keep[max(i, a.black_first)]:05d}.png"), os.path.join(seq, f"{i:05d}.png"))
        inp = ["-framerate", "30", "-start_number", "0", "-i", os.path.join(seq, "%05d.png")]
        h264 = os.path.join(tmp, NAME + ".h264")
        sp.run(["ffmpeg", "-v", "error", "-y", *inp, "-an", "-vf",
                vf, "-r", "30", "-fps_mode", "cfr", "-frames:v", str(len(keep)),
                "-c:v", "libx264", "-preset", "slow", "-crf", str(a.crf), "-profile:v", "main", "-level:v", "3.1", "-x264-params",
                x264,
                "-f", "h264", h264])
        aus = sp.split_aus(open(h264, "rb").read())
        if len(aus) != len(keep): raise SystemExit(f"{len(aus)} access units for {len(keep)} pictures")
        pcm, aframes, aw = sp.bake_audio(a.audio_bake, EVENT)
        frames, num, den = len(keep), 30, 1
        index_off = sp.HDR; es_off = index_off + 8 * frames; es = b"".join(aus); audio_off = (es_off + len(es) + 15) & ~15
        flags = sp.F_NO_BFRAMES | sp.F_BT601 | sp.F_AUDIO_BAKE | (sp.F_DPB1 if a.ref == 1 else 0) | (sp.F_LUMA_RED if a.luma_red else 0)
        first = open(os.path.join(a.shots, f"f{keep[0]:05d}.png"), "rb").read(1 << 20)
        fields = [sp.MAGIC, 1, sp.HDR, W, H, num, den, frames, index_off, es_off, len(es), max(len(x) for x in aus),
                  audio_off, len(pcm), 48000, 2, aframes, (f1 - f0 + 1), 60, 1, flags, rect[2], rect[3], rect[0], rect[1], sum(os.path.getsize(os.path.join(a.shots, f"f{k:05d}.png")) for k in keep) & 0xffffffff, sp.fnv32(first)]
        idx, off = b"", 0
        for x in aus: idx += struct.pack("<II", off, len(x)); off += len(x)
        hdr = struct.pack("<27I", *fields) + NAME.encode("ascii").ljust(64, b"\x00") + struct.pack("<I", sp.fnv32(idx)) + b"\x00" * 12
        hdr += struct.pack("<I", sp.fnv32(hdr))
        assert len(hdr) == sp.HDR
        body = hdr + idx + es
        body += b"\x00" * (audio_off - len(body)) + pcm
        os.makedirs(a.out, exist_ok=True)
        out = os.path.join(a.out, NAME + ("-luma" if a.luma_red else "") + ".shv")   # (the RGB clip stays where a launcher without the luma path finds it)
        with open(out + ".tmp", "wb") as f: f.write(body)
        os.replace(out + ".tmp", out)
        print(f"{os.path.basename(out)}: the logo from the engine's screenshots f{f0}..f{f1} (every 2nd: {frames} pictures at 30/1 = {frames / 30:.2f} s); H.264 Main 3.1, no B-frames, "
              f"{a.ref} reference picture(s), CRF {a.crf}: {len(es)} B, largest picture {max(len(x) for x in aus)} B; audio: {aw}; file {len(body)} B"
              + (f"; LUMA-RED clip: the launcher clears G, B in {rect[2]} x {rect[3]} at {rect[0]},{rect[1]}" if a.luma_red else ""))
        if a.check:
            back = os.path.join(tmp, "back.h264"); open(back, "wb").write(es)
            log = os.path.join(tmp, "psnr.log")
            # (the same conversion as the encode: a luma-red clip is compared with the red channel as grey, i.e. luma with luma)
            sp.run(["ffmpeg", "-v", "error", "-y", "-i", back, *inp, "-filter_complex",
                    f"[1:v]{vf}[ref];[0:v][ref]psnr=stats_file={log}", "-f", "null", "-"])
            vals = [float(kv.split(":")[1]) for line in open(log) for kv in line.split() if kv.startswith("psnr_avg:") and kv.split(":")[1] != "inf"]
            print(f"check: {len(vals)} pictures decoded back, PSNR min {min(vals):.2f} / mean {sum(vals) / len(vals):.2f} dB (finite only) against the 4:2:0 screenshots")

if __name__ == "__main__":
    main()
