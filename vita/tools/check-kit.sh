#!/bin/zsh
# SHEPHERD DEVICE KIT (kit #7 template, track probes; kit #8: + the physics settings file and the launcher apps' clips): checks a KIT FOLDER on the Mac before it is handed to the owner -- the kit stage's
# own gate, the same rules push-kit.sh applies at its step 1 (it never touches a card):
#   - SHA256SUMS.txt lists every file under data/ and every listed file matches (no unlisted file in data/)
#   - data/shepherd/data/video-lengths.tsv is there and holds a Devolver row the ENGINE takes (vita/shepherd/splash_rows.h's rules) --
#     kit #6's device run had none on the card, so the Devolver logo took 0 s and the frame clock was never re-based (vita/notes/kit7-probes.md)
#   - (kit #8, physics review fix) data/shepherd/data/physics2d-settings.bin is there and is the 280-B record: the project's Physics2DSettings
#     the play apps' physics core needs (the strict card pack has none) -- without it they run physics level 0 (WARN PHYS in the log)
#   - (kit #8, integration review) when the kit holds a launcher app (SHPD00193 / SHPD00194, by the VPK's param.sfo title id or its file
#     name), the two owner-local clips data/shepherd/data/video/Devolver_Animated_Logo_v001_4k_Audio-_HQ_HBR_MP4.shv and Intro.shv are there
#     and are whole .shv files (magic SHV1, version 1, the header checksum, the pictures inside the file) -- without them the launcher starts
#     behind a black screen and the engine draws the logo black (stage-owner.sh STAGE_SPLASH=1 makes them)
#   - at least one VPK in data/shepherd/vpk/, push-kit.sh and read-logs.sh present
#   - (optional, G6) data/shepherd/data/shader-cull.tsv: name TAB mode 0..2 [TAB property] rows (reported, not required)
# usage: check-kit.sh <kit dir>     exit 0 = PASS
set -u
K=${1:?usage: check-kit.sh <kit dir>}; K=${K:a}; bad=0
say() { echo "$@"; }
fail() { say "FAIL $*"; bad=$((bad + 1)); }
# (kit #8) a whole .shv clip (vita/shepherd/k8_shv.h): magic SHV1, version 1, header 192 B, the FNV-1a 32 header checksum, the index and the
# pictures (es) inside the file; prints "<frames> frames <w>x<h>, <bytes> B" or the reason; exit 0 = whole
shv_check() { python3 -I - "$1" <<'PYEOF'
import sys, struct, os
p = sys.argv[1]; b = open(p, "rb").read(192); n = os.path.getsize(p)
def fnv(d):
    h = 2166136261
    for c in d: h = ((h ^ c) * 16777619) & 0xffffffff
    return h
if len(b) < 192: print("shorter than the header"); sys.exit(1)
magic, ver, hs, w, h, rn, rd, fr, io, eo, eb = struct.unpack_from("<11I", b, 0)
if magic != 0x31564853: print("not a .shv file (magic)"); sys.exit(1)
if ver != 1 or hs != 192: print("another .shv version"); sys.exit(1)
if fnv(b[:188]) != struct.unpack_from("<I", b, 188)[0]: print("header checksum (damaged)"); sys.exit(1)
if not fr or io + fr * 8 > n or eo + eb > n: print("index / pictures outside the file (cut short?)"); sys.exit(1)
print(f"{fr} frames {w}x{h}, {n:,} B"); sys.exit(0)
PYEOF
}
[[ -f $K/SHA256SUMS.txt ]] || { say "FAIL no SHA256SUMS.txt in $K"; exit 1; }
n=0; typeset -A listed
while read -r sum rel; do
  [[ -z $rel ]] && continue; listed[$rel]=1; [[ $rel == data/* ]] || continue; n=$((n + 1))
  [[ -f $K/$rel ]] || { fail "listed, missing: $rel"; continue; }
  [[ $(shasum -a 256 "$K/$rel" | cut -c1-64) == $sum ]] || fail "checksum differs: $rel"
done < "$K/SHA256SUMS.txt"
extra=$(cd "$K" && find data -type f ! -name '._*' ! -name '.DS_Store' 2>/dev/null | sort | while read -r f; do (( ${+listed[$f]} )) || echo "$f"; done)
[[ -z $extra ]] || fail "in data/ but not in SHA256SUMS.txt: $(print -r -- $extra | tr '\n' ' ')"
say "ok   $n data file(s) listed in SHA256SUMS.txt checked"
VL=$K/data/shepherd/data/video-lengths.tsv
if [[ ! -f $VL ]]; then fail "no data/shepherd/data/video-lengths.tsv (the Devolver splash table; vita/shepherd/stage-owner.sh writes it)"
elif python3 -I - "$VL" <<'PYEOF'
import sys, re
def num(s):
    s = s.strip()
    return float(s) if re.fullmatch(r"[+-]?(\d+\.?\d*|\.\d+)([eE][+-]?\d+)?", s) else None
ok = False
for line in open(sys.argv[1], encoding="utf-8", errors="replace").read().split("\n"):
    f = line.split("#", 1)[0].split("\t")
    if len(f) < 3 or not f[0].strip().lower().startswith("devolver_animated_logo"): continue
    fr = f[1].strip()
    if not re.fullmatch(r"[+-]?\d+", fr) or int(fr) <= 0: continue
    r = f[2].split("/", 1); n = num(r[0]); d = num(r[1]) if len(r) > 1 else None
    if n is None: continue
    if d is None or d <= 0: d = 1.0
    if n / d > 0: ok = True; break
sys.exit(0 if ok else 1)
PYEOF
then say "ok   data/shepherd/data/video-lengths.tsv holds the Devolver row (the logo's 6.27 s; the frame clock re-based by 376)"
else fail "data/shepherd/data/video-lengths.tsv has no Devolver row the engine takes"; fi
PS=$K/data/shepherd/data/physics2d-settings.bin
if [[ ! -f $PS ]]; then fail "no data/shepherd/data/physics2d-settings.bin (the project's Physics2DSettings the play apps' physics core needs -- without it they run physics level 0; vita/shepherd/stage-owner.sh writes it)"
elif [[ $(stat -f %z "$PS") != 280 ]]; then fail "data/shepherd/data/physics2d-settings.bin is $(stat -f %z "$PS") B, not the 280-B Physics2DSettings record"
else say "ok   data/shepherd/data/physics2d-settings.bin (280 B: the play apps' physics settings)"; fi
vp=($K/data/shepherd/vpk/*.vpk(N)); (( ${#vp} )) && say "ok   ${#vp} VPK(s): $(for v in $vp; do echo -n "${v:t} "; done)" || fail "no VPK in data/shepherd/vpk/"
# (kit #8, integration review) the launcher apps' clips: required when a launcher VPK is in the kit
launchers=(); for v in $vp; do
  t=$(unzip -p "$v" sce_sys/param.sfo 2>/dev/null | LC_ALL=C grep -aoE 'SHPD0019[34]' | head -1)
  [[ -n $t || ${v:t} == *launcher* ]] && launchers+=("${v:t}${t:+ ($t)}")
done
if (( ${#launchers} )); then
  for c in Devolver_Animated_Logo_v001_4k_Audio-_HQ_HBR_MP4 Intro; do
    f=$K/data/shepherd/data/video/$c.shv
    if [[ ! -f $f ]]; then fail "no data/shepherd/data/video/$c.shv, but the kit holds launcher app(s) ${launchers[*]}: they would start behind a black screen (vita/shepherd/stage-owner.sh STAGE_SPLASH=1 makes the clip)"
    elif r=$(shv_check "$f"); then say "ok   data/shepherd/data/video/$c.shv ($r)"
    else fail "data/shepherd/data/video/$c.shv is not a whole .shv clip: $r (stage-owner.sh STAGE_SPLASH=1 makes it again)"; fi
  done
else
  say "note no launcher app in the kit: the .shv clips are not required"
fi
for s in push-kit.sh read-logs.sh; do [[ -f $K/$s ]] || fail "no $s"; done
grep -q 'splash_row' "$K/push-kit.sh" 2>/dev/null && say "ok   push-kit.sh checks the splash table (kit #7 template)" || fail "push-kit.sh is not the kit #7 template (no splash table check)"
SC=$K/data/shepherd/data/shader-cull.tsv
[[ -f $SC ]] && say "note data/shepherd/data/shader-cull.tsv: $(grep -cE $'^[^#].*\t[012](\t|$)' "$SC") shader row(s) (kit #7 G6: the Cull state of the shaders the Vita pack carries without one)"
(( bad )) && { say "FAIL check-kit: $bad problem(s)"; exit 1; } || { say "PASS check-kit $K"; exit 0; }
