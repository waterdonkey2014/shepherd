#!/usr/bin/env python3
"""kit #10 fidelity (vita/notes/kit10-fidelity.md "2. Exact decoders", video): how far the Vita's video picture is from the PC's, measured on the
Mac with the local ffmpeg (MEASURED-M; nothing downloaded, the game never run -- its clip files are read only).
  video.py <clip.shv> <the game's original .mp4> <out dir>
- the .shv's elementary stream (vita/shepherd/k8_shv.h: header, index, Annex-B access units) is extracted and decoded (H.264 decoding is
  bit-exact by the standard: any conformant decoder -- ffmpeg here, sceAvcdec on the Vita -- gives these YUV pictures); framemd5 of them =
  the reference for a device conformance check (<out>/shv-yuv.framemd5);
- the PC's picture: the original stream decoded (the same bits the PC's player decodes) and reduced to the .shv's content rectangle with a
  bilinear scale (the GPU's minification stand-in); both converted to RGB with their own matrices (the .shv says BT.601 in its flags, the
  source is BT.709) -> PSNR per frame (ffmpeg psnr) = what the re-encode costs against the PC's picture;
- the original's own decode: framemd5 (what the Vita would show bit for bit, up to the colour conversion and the scale, if it decoded the
  original stream itself -- High profile level 4.0, 1920x1080, within sceAvcdec's documented 1920x1088).
Writes <out>/video-report.txt (owner-local: hashes of game output)."""
import os, struct, subprocess, sys
shv, mp4, out = sys.argv[1], sys.argv[2], sys.argv[3]
os.makedirs(out, exist_ok=True)
b = open(shv, 'rb').read()
names = ('magic version header_size width height rate_num rate_den frames index_off es_off es_bytes max_au audio_off audio_bytes audio_rate '
         'audio_channels audio_frames src_frames src_rate_num src_rate_den flags content_w content_h content_x content_y src_bytes src_fnv').split()
h = dict(zip(names, struct.unpack('<' + 'I' * len(names), b[:4 * len(names)])))
assert h['magic'] == 0x31564853, 'not a .shv'
es = os.path.join(out, 'shv.h264')
open(es, 'wb').write(b[h['es_off']:h['es_off'] + h['es_bytes']])
ff = ['ffmpeg', '-v', 'error', '-nostdin', '-y']
rep = []
def run(args): return subprocess.run(args, capture_output=True, text=True)
run(ff + ['-f', 'h264', '-i', es, '-f', 'framemd5', '-pix_fmt', 'yuv420p', os.path.join(out, 'shv-yuv.framemd5')])
run(ff + ['-i', mp4, '-an', '-f', 'framemd5', '-pix_fmt', 'yuv420p', os.path.join(out, 'src-yuv.framemd5')])
cw, ch, cx, cy = h['content_w'], h['content_h'], h['content_x'], h['content_y']
mat = 'bt601' if h['flags'] & 2 else 'bt709'
flt = (f"[0:v]crop={cw}:{ch}:{cx}:{cy},scale=in_color_matrix={mat}:in_range=tv,format=rgb24[a];"
       f"[1:v]scale={cw}:{ch}:flags=bilinear:in_color_matrix=bt709:in_range=tv,format=rgb24[b];[a][b]psnr=stats_file={os.path.join(out, 'psnr.log')}")
r = run(ff[:-1] + ['-f', 'h264', '-i', es, '-i', mp4, '-an', '-lavfi', flt, '-f', 'null', '-'])
psnr = [l for l in (r.stderr or '').splitlines() if 'PSNR' in l]
nshv = sum(1 for l in open(os.path.join(out, 'shv-yuv.framemd5')) if not l.startswith('#'))
nsrc = sum(1 for l in open(os.path.join(out, 'src-yuv.framemd5')) if not l.startswith('#'))
vals = []
if os.path.exists(os.path.join(out, 'psnr.log')):
    for l in open(os.path.join(out, 'psnr.log')):
        d = dict(kv.split(':') for kv in l.split() if ':' in kv)
        try: vals.append(float(d['psnr_avg']))
        except (KeyError, ValueError): pass
fin = [v for v in vals if v != float('inf')]
rep.append(f"{os.path.basename(shv)}: {h['width']}x{h['height']} (content {cw}x{ch}+{cx}+{cy}), {h['frames']} frames at {h['rate_num']}/{h['rate_den']}, "
           f"flags {h['flags']} ({mat}); ES {h['es_bytes']} B decoded to {nshv} pictures (framemd5: shv-yuv.framemd5)")
rep.append(f"source {os.path.basename(mp4)}: {nsrc} pictures decoded (framemd5: src-yuv.framemd5)")
if fin:
    s = sorted(fin)
    rep.append(f"PSNR (RGB, the .shv's content vs the source scaled bilinear to {cw}x{ch}) over {len(vals)} frames: mean {sum(fin) / len(fin):.2f} dB, "
               f"min {s[0]:.2f}, median {s[len(s) // 2]:.2f}, max {s[-1]:.2f} ({len(vals) - len(fin)} identical frames); ffmpeg: {psnr[-1] if psnr else ''}")
else:
    rep.append('PSNR: none computed (' + (r.stderr or '')[-300:] + ')')
open(os.path.join(out, 'video-report.txt'), 'w').write('\n'.join(rep) + '\n')
print('\n'.join(rep))
