#!/bin/zsh
# SHEPHERD DEVICE KIT (kit #7 template, track probes; kit #8: + the physics settings file and the launcher apps' clips; kit #9: + the module-load
# check on the shipped launcher modules, every listed file, one VPK per title id, owner-manifest agreement, the extras' env lines; kit #10: + the
# compressed-IL DLLs, the chunked pack, the IL bundle, the boot screen's recording, the env-files zip): checks a KIT FOLDER on the Mac before it is handed to the owner -- the kit stage's
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
#   - (kit #9 kit stage) EVERY file SHA256SUMS.txt lists matches (data/, extras/, alternates/, the scripts and texts), and no file under
#     data/, extras/ or alternates/ is unlisted
#   - (kit #9, the kit #8.1 lesson) THE MODULE-LOAD CHECK on the shipped bytes: every launcher VPK's module/shepherd.suprx (the SELF the Vita
#     loads, read by vita/tools/self_imports.py) imports only the libraries in vita/shepherd/module-load-libs.txt -- anything else and the
#     Vita refuses the engine module with 0x8002D003 (kit #8's launchers on the owner's Vita); a launcher VPK without the module FAILs too
#   - (kit #9) one VPK per title id in data/shepherd/vpk/ (two would install over each other), and the same for alternates/
#   - (kit #9) owner-manifest.txt (when the kit has one) agrees with the kit's owner files: every data/shepherd/<file> the manifest lists has
#     the manifest's sha256 (the typed v2 Unity modules and the Derived DLL the eboots are bound to by MVID), and every DLL the kit carries in
#     managed/ is in the manifest -- else push-kit.sh's step 7 would warn on a correctly pushed card
#   - (kit #9) the extras' env files: every line a comment, empty, or KEY=VALUE with a SHEPHERD_ / MONO_ / VLX-free name (reported)
#   - (kit #10) the new owner files, when the kit carries them: managed-ilz/ (the compressed-IL DLLs: every file ILZ-MANIFEST.txt names as
#     "ilz" is there with the manifest's sha256 prefix, no other DLL), pack/shepherd.pk2 + pack/shepherd.pack.xxh (the chunked pack: deep
#     verified by tools/VitaPack --verify when a VitaPack build is found -- CHECK_KIT_VITAPACK=<VitaPack.dll> -- and --made-from against
#     CHECK_KIT_PACK=<the card's shepherd.pack> when given), managed/boot.ilb (the IL bundle: header + index checksums, every entry inside
#     the file, each ILZ-form entry byte for byte the kit's managed-ilz/ copy, each original-form entry its own FNV-1a), data/video/
#     BootLoading.shb (the boot loading screen's recording: header + index checksums, the sizes); and the env-files zip
#     data/shepherd/kit10-env-files.zip (README: extracted once on the card) holds exactly the files of extras/card-env/kit10-env/
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
n=0; na=0; typeset -A listed
while read -r sum rel; do
  [[ -z $rel ]] && continue; listed[$rel]=1; na=$((na + 1)); [[ $rel == data/* ]] && n=$((n + 1))
  [[ -f $K/$rel ]] || { fail "listed, missing: $rel"; continue; }
  [[ $(shasum -a 256 "$K/$rel" | cut -c1-64) == $sum ]] || fail "checksum differs: $rel"
done < "$K/SHA256SUMS.txt"
extra=$(cd "$K" && find data extras alternates -type f ! -name '._*' ! -name '.DS_Store' 2>/dev/null | sort | while read -r f; do (( ${+listed[$f]} )) || echo "$f"; done)
[[ -z $extra ]] || fail "in data/ / extras/ / alternates/ but not in SHA256SUMS.txt: $(print -r -- $extra | tr '\n' ' ')"
say "ok   $na file(s) listed in SHA256SUMS.txt checked ($n under data/)"
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
# (kit #9, the kit #8.1 lesson) the module-load check on the SHIPPED module, and one VPK per title id
HERE=${0:a:h}; MLL=$HERE/../shepherd/module-load-libs.txt; SI=$HERE/self_imports.py
for grp in data/shepherd/vpk alternates; do
  vs=($K/$grp/*.vpk(N)); (( ${#vs} )) || continue
  typeset -A tid_of; tid_of=()
  for v in $vs; do
    t=$(unzip -p "$v" sce_sys/param.sfo 2>/dev/null | LC_ALL=C grep -aoE 'SHPD[0-9]{5}' | head -1)
    [[ -z $t ]] && { fail "${v:t}: no SHPD title id in its param.sfo"; continue; }
    (( ${+tid_of[$t]} )) && fail "$grp: ${v:t} and ${tid_of[$t]} have the same title id $t (they would install over each other)" || tid_of[$t]=${v:t}
    has_mod=0; unzip -l "$v" module/shepherd.suprx > /dev/null 2>&1 && has_mod=1
    if [[ $t == SHPD0019[34] || ${v:t} == *launcher* ]]; then
      (( has_mod )) || { fail "${v:t} ($t) is a launcher app but holds no module/shepherd.suprx"; continue; }
      if [[ ! -f $MLL || ! -f $SI ]]; then fail "the module-load check needs $MLL and $SI (run check-kit.sh from the repo's vita/tools)"; continue; fi
      libs=$(python3 -I "$SI" --zip "$v" --member module/shepherd.suprx --allow "$MLL" 2>&1); rc=$?
      if (( rc == 0 )); then say "ok   ${v:t} ($t): the engine module imports only libraries loaded before it ($(print -r -- $libs | wc -l | tr -d ' ') libraries, all in module-load-libs.txt)"
      else fail "${v:t} ($t): the engine module imports a library that is not loaded when the launcher loads it -> the Vita refuses it with 0x8002D003: $(print -r -- $libs | grep 'NOT LOADED' | cut -c1-200)"; fi
    fi
  done
  say "ok   $grp: ${#vs} VPK(s), one per title id ($(for t in ${(ok)tid_of}; do echo -n "$t "; done))"
done
# (kit #9) owner-manifest.txt agrees with the kit's owner files (the bound DLLs: typed Unity modules, Derived)
OM=$K/owner-manifest.txt
if [[ -f $OM ]]; then
  typeset -A msum; msum=(); while IFS=$'\t' read -r size sum rel; do [[ $size == \#* || -z ${rel:-} ]] && continue; msum[$rel]=$sum; done < "$OM"
  mm=0; mc=0
  for f in $K/data/shepherd/**/*(N.); do
    rel=${f#$K/data/shepherd/}; [[ ${rel:t} == ._* || ${rel:t} == .DS_Store || $rel == vpk/* ]] && continue
    if (( ${+msum[$rel]} )); then mc=$((mc + 1)); [[ $(shasum -a 256 "$f" | cut -c1-64) == ${msum[$rel]} ]] || { mm=$((mm + 1)); fail "owner-manifest.txt lists $rel with another sha256 than the kit's copy"; }
    elif [[ $rel == managed/* ]]; then mm=$((mm + 1)); fail "the kit carries $rel but owner-manifest.txt does not list it (push-kit step 7 would not check it)"; fi
  done
  (( mm )) || say "ok   owner-manifest.txt: the kit's $mc owner file(s) carry the manifest's sha256 ($(grep -vc '^#' "$OM") files listed, $(grep -v '^#' "$OM" | grep -c $'\tmanaged/') in managed/)"
fi
# (kit #9) the extras' env files: KEY=VALUE lines only (a stray line is a typo the app would ignore, silently changing a step)
for e in $K/extras/**/env*.txt(N.) $K/extras/**/args*.txt(N.); do
  badl=$(grep -vE '^[[:space:]]*(#.*)?$|^[A-Z][A-Z0-9_]*=' "$e" | head -2)
  [[ -n $badl ]] && fail "${e#$K/}: a line that is neither a comment nor KEY=VALUE: $badl"
done
ne=($K/extras/**/env*.txt(N.)); (( ${#ne} )) && say "ok   ${#ne} extras env file(s): KEY=VALUE lines and comments only ($(for e in $ne; do echo -n "${${e#$K/extras/}%/env*.txt}=$(grep -cE '^[A-Z]' $e) "; done))"
# (kit #10) the new owner files (each check only when the kit carries the file)
ZD=$K/data/shepherd/managed-ilz
if [[ -d $ZD ]]; then
  if [[ ! -f $ZD/ILZ-MANIFEST.txt ]]; then fail "data/shepherd/managed-ilz/ has no ILZ-MANIFEST.txt"
  elif r=$(python3 -I - "$ZD" <<'PYEOF'
import sys, os, re, hashlib
d = sys.argv[1]; want = {}
for line in open(os.path.join(d, "ILZ-MANIFEST.txt"), encoding="utf-8", errors="replace"):
    m = re.match(r"ilz (\S+\.dll): .* sha256 ([0-9a-f]{16}) orig ([0-9a-f]{16})", line)
    if m: want[m.group(1)] = m.group(2)
have = sorted(f for f in os.listdir(d) if f.endswith(".dll") and not f.startswith("._"))
bad = [f for f in have if f not in want] + [f + " (missing)" for f in want if f not in have]
for f in have:
    if f in want and hashlib.sha256(open(os.path.join(d, f), "rb").read()).hexdigest()[:16] != want[f]: bad.append(f + " (sha256)")
for f in have:
    if open(os.path.join(d, f), "rb").read(2) != b"MZ": bad.append(f + " (not a PE image)")
print(f"{len(have)} ILZ DLL(s), {len(want)} in the manifest" + (": " + ", ".join(bad[:8]) if bad else ""))
sys.exit(1 if bad else 0)
PYEOF
); then say "ok   data/shepherd/managed-ilz/: $r (each = its manifest sha256)"
  else fail "data/shepherd/managed-ilz/: $r"; fi
fi
PK2=$K/data/shepherd/pack/shepherd.pk2
if [[ -f $PK2 ]]; then
  [[ -f $K/data/shepherd/pack/shepherd.pack.xxh ]] || fail "data/shepherd/pack/shepherd.pk2 without pack/shepherd.pack.xxh (the made-from samples the engine checks at open)"
  VPD=${CHECK_KIT_VITAPACK:-}; [[ -z $VPD ]] && for c in $HERE/../../tools/VitaPack/bin/{Release,Debug}/*/VitaPack.dll(N); do VPD=$c; break; done
  if [[ -n $VPD && -f $VPD ]] && command -v dotnet > /dev/null 2>&1; then
    vr=$(dotnet "$VPD" --verify "$PK2" deep 2>&1); vrc=$?; vr=$(print -r -- "$vr" | tail -1)
    (( vrc == 0 )) && [[ $vr == *OK* ]] && say "ok   pack/shepherd.pk2 (tools/VitaPack --verify deep): $(print -r -- $vr | cut -c1-160)" || fail "pack/shepherd.pk2 --verify deep: $(print -r -- $vr | cut -c1-200)"
    if [[ -n ${CHECK_KIT_PACK:-} ]]; then
      mf=$(dotnet "$VPD" --made-from "$PK2" "$CHECK_KIT_PACK" 2>&1); mfrc=$?; mf=$(print -r -- "$mf" | tail -1)
      (( mfrc == 0 )) && say "ok   pack/shepherd.pk2 made from $CHECK_KIT_PACK: $(print -r -- $mf | cut -c1-160)" || fail "pack/shepherd.pk2 NOT made from $CHECK_KIT_PACK: $(print -r -- $mf | cut -c1-200)"
    else say "note pack/shepherd.pk2: --made-from not run (CHECK_KIT_PACK=<the card's shepherd.pack> runs it; the engine also samples it at every open)"; fi
  else
    hdr=$(head -c 8 "$PK2" | xxd -p)
    say "note pack/shepherd.pk2: $(stat -f %z "$PK2") B, header $hdr -- not deep-verified (no VitaPack build: CHECK_KIT_VITAPACK=<VitaPack.dll>)"
  fi
fi
ILB=$K/data/shepherd/managed/boot.ilb
if [[ -f $ILB ]]; then
  if r=$(python3 -I - "$ILB" "$ZD" <<'PYEOF'
import sys, os, struct
p, zd = sys.argv[1], sys.argv[2]
def fnv(b, h=2166136261):
    for c in b: h = ((h ^ c) * 16777619) & 0xffffffff
    return h
data = open(p, "rb").read(); n = len(data)
if n < 32: print("shorter than the header"); sys.exit(1)
magic, ver, count, io, do, db, ifnv, cs = struct.unpack_from("<8I", data, 0)
if magic != 0x31424c49 or ver != 1: print("not an IL bundle (magic / version)"); sys.exit(1)
if fnv(data[:28]) != cs: print("header checksum (damaged)"); sys.exit(1)
idx = data[io:io + count * 64]
if len(idx) != count * 64 or fnv(idx) != ifnv: print("index checksum (damaged)"); sys.exit(1)
if do + db > n: print("data outside the file (cut short?)"); sys.exit(1)
nilz = 0; bad = []
for i in range(count):
    e = idx[i * 64:(i + 1) * 64]; name = e[:48].split(b"\0")[0].decode(); off, size, h, fl = struct.unpack_from("<4I", e, 48)
    b = data[do + off:do + off + size]
    if len(b) != size: bad.append(name + " (outside the file)"); continue
    if fl & 1:
        nilz += 1; z = os.path.join(zd, name + ".dll")
        if not os.path.isfile(z) or open(z, "rb").read() != b: bad.append(name + " (ILZ entry != the kit's managed-ilz copy)")
    elif fnv(b) != h: bad.append(name + " (FNV)")
print(f"{count} DLLs ({nilz} in their compressed-IL form), {n:,} B" + (": " + ", ".join(bad[:6]) if bad else ""))
sys.exit(1 if bad else 0)
PYEOF
); then say "ok   data/shepherd/managed/boot.ilb (the IL bundle): $r"
  else fail "data/shepherd/managed/boot.ilb: $r"; fi
fi
SHB=$K/data/shepherd/data/video/BootLoading.shb
if [[ -f $SHB ]]; then
  if r=$(python3 -I - "$SHB" <<'PYEOF'
import sys, os, struct
p = sys.argv[1]; data = open(p, "rb").read(); n = len(data)
def fnv(b, h=2166136261):
    for c in b: h = ((h ^ c) * 16777619) & 0xffffffff
    return h
if n < 64: print("shorter than the header"); sys.exit(1)
w = struct.unpack_from("<16I", data, 0)
if w[0] != 0x31424853 or w[1] != 1 or w[2] != 64: print("not a boot-screen recording (magic / version)"); sys.exit(1)
if fnv(data[:60]) != w[15]: print("header checksum (damaged)"); sys.exit(1)
io, do, db = w[9], w[10], w[11]
if fnv(data[io:do]) != w[13]: print("index checksum (damaged)"); sys.exit(1)
if do + db != n: print(f"size {n} != header {do + db} (cut short?)"); sys.exit(1)
print(f"{w[5]} pictures {w[3]}x{w[4]}, {n:,} B"); sys.exit(0)
PYEOF
); then say "ok   data/shepherd/data/video/BootLoading.shb (the boot loading screen): $r"
  else fail "data/shepherd/data/video/BootLoading.shb: $r"; fi
fi
EZ=$K/data/shepherd/kit10-env-files.zip
if [[ -f $EZ ]]; then
  zl=$(unzip -Z1 "$EZ" 2>/dev/null | grep -v '/$' | LC_ALL=C sort); el=$(cd $K/extras/card-env 2>/dev/null && find kit10-env -type f ! -name '._*' ! -name '.DS_Store' | LC_ALL=C sort)
  if [[ -n $zl && $zl == $el ]]; then
    zbad=0; for f in ${(f)zl}; do cmp -s <(unzip -p "$EZ" "$f") "$K/extras/card-env/$f" || { zbad=1; fail "kit10-env-files.zip: $f differs from extras/card-env/$f"; }; done
    (( zbad )) || say "ok   data/shepherd/kit10-env-files.zip = extras/card-env/kit10-env/ ($(print -r -- $zl | wc -l | tr -d ' ') files)"
  else fail "data/shepherd/kit10-env-files.zip does not hold exactly extras/card-env/kit10-env/'s files"; fi
fi
for s in push-kit.sh read-logs.sh; do [[ -f $K/$s ]] || fail "no $s"; done
grep -q 'splash_row' "$K/push-kit.sh" 2>/dev/null && say "ok   push-kit.sh checks the splash table (kit #7 template)" || fail "push-kit.sh is not the kit #7 template (no splash table check)"
SC=$K/data/shepherd/data/shader-cull.tsv
[[ -f $SC ]] && say "note data/shepherd/data/shader-cull.tsv: $(grep -cE $'^[^#].*\t[012](\t|$)' "$SC") shader row(s) (kit #7 G6: the Cull state of the shaders the Vita pack carries without one)"
(( bad )) && { say "FAIL check-kit: $bad problem(s)"; exit 1; } || { say "PASS check-kit $K"; exit 0; }
