#!/usr/bin/env python3
"""Device kit #11, track device (vita/notes/kit11-device.md): the boot screen's EMBLEM SET -- one recording of the game's own loading screen
per emblem, and the SET.txt the host reads to pick one at random each boot (vita/shepherd/k10_boot.inc.c, kit #11 part).

Why: kit #10's boot screen was ONE recording (data/video/BootLoading.shb) made from the New Game route's load into 'Game Biome Intro', so it
always showed the emblem the game happened to draw there (one of the 'eyes' symbols). The game itself picks a RANDOM emblem for every load
(MMTools.MMTransition: list[UnityEngine.Random.Range(0, list.Count)] over its pentagram sprite lists). The owner's decision (2026-10-30,
vita/notes/roadmap-kits.md "The emblems"): every emblem the player HAS is equally likely, one recording per emblem, each recorded as the game
draws its loading screen; the base emblems + the Sins of the Flesh pentagram always; Woolhaven's set only if owned; the co-op (purple) versions
only once co-op was played. The staging decides which recordings go on the card; the host draws among what is there.

Commands (standard library only; reads .shb files, never writes into them):
  check <file.shb> ...                 header + index + every frame decoded (zlib) and the RINGS check (below); exit 1 if any file fails
  make <dir> <file.shb>=<name>[:slots] ... [--allow-white] [--allow-empty] [--default-on]
                                       copies nothing: writes <dir>/SET.txt for the .shb files already in <dir> -- one 'emblem' line per file
                                       (file, name, the game's list slots, the header checksum the host verifies) + the 'rings' line from
                                       the check ('rings drawn' only when no file shows the white-square rings; --allow-white records
                                       'rings white' and the set stays opt-in: SHEPHERD_K10_BOOTSCREEN=1) + with --default-on the 'default on'
                                       line: THE OWNER'S YES to exactly this set (roadmap question 3) -- written only for a set with drawn rings
                                       and at least the owner's pool of 6 emblems (2026-10-30: the base emblems + the Sins of the Flesh pentagram);
                                       the host shows the boot screen by default only with BOTH lines. Pass it only after the owner has seen the
                                       set and said yes: every re-made set drops it until he says yes again
  key <file.shb>                       the emblem's key: FNV-1a 32 of the screen's centre (x 320..639, y 112..431: the emblem; the crown and its
                                       rings sit in the bottom-left corner) in the file's last frame -- two recordings of the same emblem
                                       give the same key (bootscreen_record.sh keeps one recording per key)
  synth <dir> <n> [--default-on]       TEST ONLY: n small synthetic recordings (flat colour + a moving square; no game content) + SET.txt with
                                       'rings drawn' (+ 'default on' with the flag, any n) -- the host's pick / check / timing paths under vlx
                                       without any owner data
THE RINGS CHECK: Shepherd drew the loading screen's crown rings as WHITE SQUARES until the ring images' UI_UberShader texture is bound (kit #10
review finding 6: the GPU work's white-panels row): rotating squares whose edges show as grey-to-white gradients around the red crown. The
game's own loading screen is coloured on black (red; blue in Woolhaven's screens, purple in co-op) and has no bright NEUTRAL pixel at all, so
the check counts, in every frame of the loop, the pixels whose red, green and blue are all >= 96 and within 16 of each other: more than 32 in
any frame = the white-square rings (kit #10's recording: hundreds a frame). A set with such a frame is recorded as 'rings white': the host
then keeps the boot screen opt-in (kit #10's rule) instead of on by default.
THE EMBLEM CHECK (kit #11 finding: some of the game's emblems draw as NOTHING in Shepherd today -- seeds whose run drew one of them gave a
loading screen with the crown and its rings but an empty centre): the emblem's area (the screen's centre, x 320..639, y 112..431) of the
file's last frame must hold at least 500 pixels brighter than 32 in some channel; an empty one is refused by 'make' (--allow-empty keeps it)
and dropped by bootscreen_record.sh -- the boot screen never shows a loading screen without its emblem.
SET.txt (one line each, '#' comments):
  shb-set 1
  rings drawn|white
  default on                           (optional: the owner's yes, --default-on; the host: on by default = 'rings drawn' + 'default on')
  emblem <file> <name> <slots> <header checksum, 8 hex>
The recordings are GAME-DERIVED (the owner's own copy, drawn): the owner folder only, never in git, never in a VPK, never shared."""
import os, struct, sys, zlib

W, H = 960, 544
MAGIC = 0x31424853


def fnv32(b, h=2166136261):
    for x in b:
        h = ((h ^ x) * 16777619) & 0xffffffff
    return h


def read_shb(path):
    """-> (header dict, list of (rect, raw rgba bytes or None)); raises ValueError with a reason"""
    data = open(path, "rb").read()
    if len(data) < 64: raise ValueError("shorter than the header")
    f = struct.unpack("<16I", data[:64])
    hd = dict(zip(["magic", "version", "header_size", "width", "height", "frames", "loop_start", "rate_num", "rate_den", "index_off",
                   "data_off", "data_bytes", "max_raw", "index_fnv", "src_fnv", "checksum"], f))
    if hd["magic"] != MAGIC or hd["version"] != 1 or hd["header_size"] != 64: raise ValueError("not a boot screen file (magic / version)")
    if fnv32(data[:60]) != hd["checksum"]: raise ValueError("header checksum (damaged)")
    if (hd["width"], hd["height"]) != (W, H): raise ValueError("not 960 x 544")
    n = hd["frames"]
    if not n or hd["loop_start"] >= n: raise ValueError("frame count / loop")
    idx = data[hd["index_off"]:hd["index_off"] + 24 * n]
    if len(idx) != 24 * n or fnv32(idx) != hd["index_fnv"]: raise ValueError("index checksum (damaged)")
    out = []
    for k in range(n):
        x, y, w, h, off, ln, raw_fnv, frame_fnv = struct.unpack_from("<4H4I", idx, 24 * k)
        if not w or not h: out.append(((0, 0, 0, 0, frame_fnv), None)); continue
        raw = zlib.decompress(data[hd["data_off"] + off:hd["data_off"] + off + ln])
        if len(raw) != w * h * 4 or fnv32(raw) != raw_fnv: raise ValueError(f"frame {k}: rectangle does not decode to its stored hash")
        out.append(((x, y, w, h, frame_fnv), raw))
    return hd, out


def frames(hd, rects):
    """every frame of the file as shown (whole-screen RGBA), in file order"""
    buf = bytearray(W * H * 4)
    for (x, y, w, h, _ff), raw in rects:
        if raw is not None:
            for j in range(h):
                buf[((y + j) * W + x) * 4:((y + j) * W + x + w) * 4] = raw[j * w * 4:(j + 1) * w * 4]
        yield bytes(buf)


NEUTRAL_MIN, NEUTRAL_SPREAD, NEUTRAL_LIMIT = 96, 16, 32


def white_blocks(fr):
    """the bright neutral (grey / white) pixels of a frame: r, g, b all >= 96 and within 16 of each other (the white-square rings)"""
    n = 0
    for i in range(0, len(fr), 4):
        r, g, b = fr[i], fr[i + 1], fr[i + 2]
        if r >= NEUTRAL_MIN and g >= NEUTRAL_MIN and b >= NEUTRAL_MIN and max(r, g, b) - min(r, g, b) <= NEUTRAL_SPREAD:
            n += 1
    return n


EMBLEM_MIN = 500


def emblem_pixels(fr):
    """the pixels of the emblem's area (x 320..639, y 112..431) brighter than 32 in some channel"""
    n = 0
    for y in range(112, 432):
        row = fr[(y * W + 320) * 4:(y * W + 640) * 4]
        for i in range(0, len(row), 4):
            if row[i] > 32 or row[i + 1] > 32 or row[i + 2] > 32: n += 1
    return n


def check(path, emblem=None):
    """-> (header, white-square pixels in the worst loop frame (0 = rings drawn), that frame); emblem: a list that receives the emblem's
    pixel count in the last frame"""
    hd, rects = read_shb(path)
    worst, at, bad_hash, last = 0, -1, 0, None
    for k, fr in enumerate(frames(hd, rects)):
        last = fr
        if fnv32(fr) != rects[k][0][4]: bad_hash += 1
        if k >= hd["loop_start"]:
            wb = white_blocks(fr)
            if wb > worst: worst, at = wb, k
    if bad_hash: raise ValueError(f"{bad_hash} frame(s) differ from their stored whole-frame hash")
    if emblem is not None: emblem.append(emblem_pixels(last))
    return hd, (worst if worst > NEUTRAL_LIMIT else 0), at


def cmd_check(paths):
    rc = 0
    for p in paths:
        try:
            em = []
            hd, worst, at = check(p, em)
            print(f"{p}: OK -- {hd['frames']} frames (loop from {hd['loop_start']}), checksum {hd['checksum']:08x}; rings: "
                  + (f"WHITE SQUARES ({worst} bright neutral pixels in loop frame {at})" if worst else f"drawn (at most {NEUTRAL_LIMIT} bright neutral pixels a loop frame)")
                  + f"; emblem: " + (f"present ({em[0]} pixels)" if em[0] >= EMBLEM_MIN else f"MISSING (only {em[0]} pixels in its area: Shepherd drew no emblem)"))
            if worst or em[0] < EMBLEM_MIN: rc = 1
        except (ValueError, OSError, zlib.error) as e:
            print(f"{p}: FAIL -- {e}"); rc = 1
    return rc


OWNER_POOL = 6   # the owner's boot-screen pool (2026-10-30): the base emblems + the Sins of the Flesh Snake pentagram, 1 in 6 each


def cmd_make(d, specs, allow_white, allow_empty=False, default_on=False, min_pool=OWNER_POOL):
    lines, white = [], 0
    for s in specs:
        fn, _, rest = s.partition("=")
        name, _, slots = rest.partition(":")
        if not fn or not name or "/" in fn or " " in name: raise SystemExit(f"bad spec {s!r}: <file.shb>=<name>[:slots] (file inside {d}, no spaces)")
        em = []
        hd, worst, at = check(os.path.join(d, fn), em)
        if worst: white += 1; print(f"{fn}: rings WHITE SQUARES ({worst} bright neutral pixels, loop frame {at})")
        if em[0] < EMBLEM_MIN and not allow_empty: raise SystemExit(f"{fn}: the emblem is MISSING ({em[0]} pixels in its area): not put into the set (--allow-empty keeps it)")
        lines.append(f"emblem {fn} {name} {int(slots or 1)} {hd['checksum']:08x}")
    if white and not allow_white: raise SystemExit(f"{white} recording(s) show the white-square rings: re-record after the rings draw right, or --allow-white (the set stays opt-in)")
    yes = False
    if default_on:   # (kit #11 review fix: drawn rings alone never turn the boot screen on -- the owner's yes does, for this set only)
        if white: print("--default-on: NOT written -- the set shows the white-square rings (it stays opt-in: SHEPHERD_K10_BOOTSCREEN=1)")
        elif len(lines) < min_pool: print(f"--default-on: NOT written -- {len(lines)} emblem(s), fewer than the owner's pool of {min_pool} (the base emblems + the Sins of the Flesh pentagram; it stays opt-in: SHEPHERD_K10_BOOTSCREEN=1)")
        else: yes = True
    body = ["# the boot screen's emblem set (vita/tools/bootscreen_set.py): the host picks one recording at random each boot",
            "shb-set 1", "rings " + ("white" if white else "drawn")] + (["default on"] if yes else []) + lines
    tmp = os.path.join(d, "SET.txt.tmp")
    with open(tmp, "w") as f: f.write("\n".join(body) + "\n")
    os.replace(tmp, os.path.join(d, "SET.txt"))
    print(f"{os.path.join(d, 'SET.txt')}: {len(lines)} emblem(s) ({', '.join(l.split()[2] for l in lines)}), rings {'white' if white else 'drawn'}; "
          + ("'default on' (the owner's yes): the host shows it by default" if yes else "opt-in (no 'default on': SHEPHERD_K10_BOOTSCREEN=1 shows it)"))


def write_shb(path, frames_raw, loop_start, rate=30):
    """a lossless .shb from whole frames (the packer's layout: frame 0 whole, then each frame's changed rectangle; the loop's first frame also
    covers what differs from the last)"""
    def bbox(a, b):
        if a == b: return None
        ys = [y for y in range(H) if a[y * W * 4:(y + 1) * W * 4] != b[y * W * 4:(y + 1) * W * 4]]
        xs = [x for x in range(W) for y in (ys[0], ys[-1]) if a[(y * W + x) * 4:(y * W + x + 1) * 4] != b[(y * W + x) * 4:(y * W + x + 1) * 4]]
        x0, x1 = W, -1
        for y in ys:
            ra, rb = a[y * W * 4:(y + 1) * W * 4], b[y * W * 4:(y + 1) * W * 4]
            i = 0
            while ra[i:i + 4] == rb[i:i + 4]: i += 4
            j = W * 4 - 4
            while ra[j:j + 4] == rb[j:j + 4]: j -= 4
            x0, x1 = min(x0, i // 4), max(x1, j // 4)
        return (x0, ys[0], x1 - x0 + 1, ys[-1] - ys[0] + 1)

    def union(r, s):
        if r is None: return s
        if s is None: return r
        x0, y0 = min(r[0], s[0]), min(r[1], s[1]); x1, y1 = max(r[0] + r[2], s[0] + s[2]), max(r[1] + r[3], s[1] + s[3])
        return (x0, y0, x1 - x0, y1 - y0)
    rects = [(0, 0, W, H)]
    for k in range(1, len(frames_raw)):
        r = bbox(frames_raw[k], frames_raw[k - 1])
        if k == loop_start: r = union(r, bbox(frames_raw[k], frames_raw[-1]))
        rects.append(r)
    idx, data, max_raw = b"", b"", 0
    for k, r in enumerate(rects):
        ff = fnv32(frames_raw[k])
        if r is None: idx += struct.pack("<4H4I", 0, 0, 0, 0, 0, 0, 0, ff); continue
        x, y, w, h = r
        raw = b"".join(frames_raw[k][((y + j) * W + x) * 4:((y + j) * W + x + w) * 4] for j in range(h))
        z = zlib.compress(raw, 9)
        idx += struct.pack("<4H4I", x, y, w, h, len(data), len(z), fnv32(raw), ff); data += z; max_raw = max(max_raw, len(raw))
    hdr = struct.pack("<15I", MAGIC, 1, 64, W, H, len(frames_raw), loop_start, rate, 1, 64, 64 + len(idx), len(data), max_raw, fnv32(idx),
                      fnv32(frames_raw[loop_start][:1 << 20]))
    hdr += struct.pack("<I", fnv32(hdr))
    with open(path + ".tmp", "wb") as f: f.write(hdr + idx + data)
    os.replace(path + ".tmp", path)


def cmd_synth(d, n, default_on=False):
    """TEST ONLY: n synthetic recordings -- a flat dark colour per emblem (no game content), a 64 x 64 'emblem' block in the centre, a 24 x 24
    square stepping round a small circuit
    (the 'loop', 12 frames) after a 2-frame fade-in; every frame distinct, so the host's CHECK mode tests the exact frames of the chosen file"""
    os.makedirs(d, exist_ok=True)
    specs = []
    for e in range(n):
        base = bytes((16 + 40 * e) & 0xff for _ in range(1)) + bytes([8, 24 + 20 * e & 0xff, 255])
        frs = []
        for k in range(14):
            buf = bytearray(base * (W * H)) if k else bytearray(b"\x00\x00\x00\xff" * (W * H))
            if k >= 1:   # the 'emblem': a 64 x 64 block in the emblem's area (the emblem check), from frame 1 on
                em = bytes([180, 40 + 50 * e & 0xff, 60, 255]) * 64
                for j in range(64): buf[((240 + j) * W + 448) * 4:((240 + j) * W + 512) * 4] = em
            if k >= 2:
                px, py = 100 + 30 * ((k - 2) % 12), 400
                sq = bytes([200, 60 + 10 * e & 0xff, 30, 255]) * 24
                for j in range(24): buf[((py + j) * W + px) * 4:((py + j) * W + px + 24) * 4] = sq
            frs.append(bytes(buf))
        fn = f"synth-{e}.shb"
        write_shb(os.path.join(d, fn), frs, 2)
        specs.append(f"{fn}=synth-{e}:1")
    cmd_make(d, specs, False, default_on=default_on, min_pool=1)   # (test data: any n)


def cmd_key(path):
    hd, rects = read_shb(path)
    last = None
    for fr in frames(hd, rects): last = fr
    centre = b"".join(last[(y * W + 320) * 4:(y * W + 640) * 4] for y in range(112, 432))
    print(f"{fnv32(centre):08x}")
    return 0


def main(argv):
    if len(argv) < 2 or argv[1] in ("-h", "--help"): print(__doc__); return 0
    if argv[1] == "check": return cmd_check(argv[2:])
    if argv[1] == "make":
        allow, empty, yes = "--allow-white" in argv, "--allow-empty" in argv, "--default-on" in argv
        args = [a for a in argv[2:] if a not in ("--allow-white", "--allow-empty", "--default-on")]
        if len(args) < 2: raise SystemExit("make <dir> <file.shb>=<name>[:slots] ... [--allow-white] [--allow-empty] [--default-on]")
        cmd_make(args[0], args[1:], allow, empty, yes); return 0
    if argv[1] == "key":
        if len(argv) != 3: raise SystemExit("key <file.shb>")
        return cmd_key(argv[2])
    if argv[1] == "synth":
        yes = "--default-on" in argv
        args = [a for a in argv[2:] if a != "--default-on"]
        if len(args) != 2: raise SystemExit("synth <dir> <n> [--default-on]")
        cmd_synth(args[0], int(args[1]), yes); return 0
    raise SystemExit(f"unknown command {argv[1]!r} (check | make | key | synth)")


if __name__ == "__main__":
    sys.exit(main(sys.argv))
