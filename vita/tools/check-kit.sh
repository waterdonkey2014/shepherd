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
#   - (kit #11) the env-files zip of ANY kit (data/shepherd/kit<N>-env-files.zip = extras/card-env/kit<N>-env/, file for file) and every env
#     file the README names is inside it (CHECK_KIT_README=<the README>: each "ENV: paste <folder>/<file>" line); the boot screen's EMBLEM
#     SET data/shepherd/data/video/boot/ when the kit carries it (SET.txt: 'shb-set 1', a 'rings' line, every 'emblem' file there with the
#     header checksum SET.txt gives, read by vita/tools/bootscreen_set.py check: every recording whole, its emblem present, its rings as
#     SET.txt says -- 'rings white' must show the white squares, 'rings drawn' must not -- and 'default on' only with 'rings drawn');
#     probes/ (the hardware probe VPKs: probes/SHA256SUMS.txt lists every probe VPK and each matches); the play apps' APP_VER (param.sfo) --
#     one version for all five play apps, = CHECK_KIT_APPVER when given (e.g. 00.11)
#   - (kit #12) EVERY VPK's sce_sys PNGs are 8-bit palette PNGs (the only kind the Vita's installer takes: kit #11's P2 / P6 failed to install
#     with 2-bit palette / RGB icons) -- data/shepherd/vpk/, alternates/, probes/; CHECK_KIT_NEGCTL=1 also runs the check on a made-up RGB PNG
#     (it must FAIL); the boot sequence's owner files when the kit carries them: data/video/boot-seq/ (the same SET.txt + recordings check as
#     data/video/boot/) and data/video/MassiveMonster.shv (a whole .shv clip); the A/B pairs extras/card-env/kit<N>-env/ab-<name>-<a|b>[n]/:
#     every arm names the same pad file (one the kit carries, every press frame:button[:hold], the title press first), the same window lines
#   - (kit #13) the boot screen's EMBLEM CROSSFADE when a set folder (data/video/boot/, boot-seq/) carries XFADE.txt: vita/tools/
#     bootscreen_xfade.py check re-reads it (the stream = one of the set's recordings by name and header checksum, every xf-<emblem>.shx box =
#     its recording's frames, xf-check.bin = the stream's frames with the box left out) -- a set with XFADE.txt that does not check FAILs;
#     CHECK_KIT_NEGCTL=1 also runs the check on a copy of the set with one byte of a still changed (it must FAIL)
#   - (kit 13.1) data/video/MassiveMonster-luma.shv when the kit carries it: a whole clip, flagged K8SHV_F_LUMA_RED, its rectangle inside the frame, as many pictures as the RGB clip;
#     CHECK_KIT_NEGCTL=1 also runs that check on the RGB clip (it must FAIL); owner-manifest.txt must list every file of the kit's data/video/ (not only managed/)
#   - (kit 14) THE WALK's env rules (vita/notes/kit14-integration.md R8: nothing in the kit may end the owner's session before the fight): no env file carries a test-route
#     line (SHEPHERD_TIME, SHEPHERD_VIDEO_LENGTHS, SHEPHERD_PHYS_HASH, SHEPHERD_K13_SHORT_POOLS, SHEPHERD_PHYS_EVENTLOG, VLX_*) and no kit ships an args file; every env
#     folder named walk* / off* / x*-il* (the walk steps) holds SHEPHERD_STOP_SCENE=0 and no SHEPHERD_STOP_AT / SHEPHERD_PAD_FILE / SHEPHERD_PAD (the owner plays);
#     when the kit ships pack/shepherd.pack without a pk2, every env file must say SHEPHERD_K10_PACK=0 (the card's old pk2, made from another pack, ends a
#     session at boot); CHECK_KIT_NEGCTL=1 also runs the rule on made-up walk folders without the stop line, with a test line and without the pack line
#     (each must FAIL); when the kit carries
#     data/shepherd/pack/shepherd.pack, owner-manifest.txt must list it and it must be a version-1 pack (magic SHPK); a pk2 beside it: the kit #10 rule above
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
extra=$(cd "$K" && find data extras alternates probes -type f ! -name '._*' ! -name '.DS_Store' 2>/dev/null | sort | while read -r f; do (( ${+listed[$f]} )) || echo "$f"; done)
[[ -z $extra ]] || fail "in data/ / extras/ / alternates/ / probes/ but not in SHA256SUMS.txt: $(print -r -- $extra | tr '\n' ' ')"
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
    elif [[ $rel == managed/* || $rel == data/video/* ]]; then mm=$((mm + 1)); fail "the kit carries $rel but owner-manifest.txt does not list it (push-kit step 7 would not check it)"; fi   # (kit 13.1: + data/video/, the clips and the sets)
  done
  (( mm )) || say "ok   owner-manifest.txt: the kit's $mc owner file(s) carry the manifest's sha256 ($(grep -vc '^#' "$OM") files listed, $(grep -v '^#' "$OM" | grep -c $'\tmanaged/') in managed/)"
fi
# (kit #9) the extras' env files: KEY=VALUE lines only (a stray line is a typo the app would ignore, silently changing a step)
for e in $K/extras/**/env*.txt(N.) $K/extras/**/args*.txt(N.); do
  badl=$(grep -vE '^[[:space:]]*(#.*)?$|^[A-Z][A-Z0-9_]*=' "$e" | head -2)
  [[ -n $badl ]] && fail "${e#$K/}: a line that is neither a comment nor KEY=VALUE: $badl"
done
ne=($K/extras/**/env*.txt(N.)); (( ${#ne} )) && say "ok   ${#ne} extras env file(s): KEY=VALUE lines and comments only ($(for e in $ne; do echo -n "${${e#$K/extras/}%/env*.txt}=$(grep -cE '^[A-Z]' $e) "; done))"
# (kit #12 compare audit) A/B pairs: extras/card-env/kit<N>-env/ab-<name>-a[n]/ and ab-<name>-b[n]/ -- every arm must script the SAME presses
# (the same SHEPHERD_PAD_FILE line, a pad file the kit carries, every item frame:button[:hold]) so both see the same screens at the same
# frames: the title press at m+N with N <= 200, nothing else between m+201 and m+599 (after the [sim] window 1301-1400 even for a menu at
# f800), no absolute frame in 1231-1400; the same SHEPHERD_SIMSTATS / SHEPHERD_STOP_AT / SHEPHERD_TIME; the arms differ only in the switches
# under test and SHEPHERD_LOG_DIR (each its own folder). Kit #13: a screen-state wait "@Screen[+N]:button" (due N frames after the game showed
# that screen) only after a press at m+600 or later -- the press that opens the screen, after the window.
typeset -A abgrp; abgrp=()
for A in $K/extras/card-env/kit*-env/ab-*-[ab](N/) $K/extras/card-env/kit*-env/ab-*-[ab][0-9](N/); do nm=${A:t}; nm=${nm%-[ab]*}; abgrp[${A:h}/$nm]=1; done
abn=0
for G in ${(k)abgrp}; do
  nm=${G:t}; abn=$((abn + 1)); arms=($G-[ab](N/) $G-[ab][0-9](N/))
  na=0; nb=0; for x in $arms; do [[ ${x:t} == $nm-a* ]] && na=$((na + 1)) || nb=$((nb + 1)); done
  (( na >= 1 && nb >= 1 )) || { fail "A/B ${nm}: needs at least one a and one b arm (${(j: :)${arms:t}})"; continue; }
  ref=; refpad=; abok=1; typeset -A logdirs; logdirs=()
  for x in $arms; do
    ex=($x/env_*.txt(N.)); (( ${#ex} == 1 )) || { fail "A/B ${nm}: ${x:t}/ needs exactly one env file"; abok=0; continue; }
    if [[ -z $ref ]]; then ref=$ex[1]
    else
      [[ ${ex[1]:t} == ${ref:t} ]] || { fail "A/B ${nm}: ${x:t}/${ex[1]:t} is not ${ref:t}"; abok=0; }
      for k in SHEPHERD_PAD_FILE SHEPHERD_SIMSTATS SHEPHERD_STOP_AT SHEPHERD_TIME; do
        [[ "$(grep -E "^$k=" $ex[1])" == "$(grep -E "^$k=" $ref)" ]] || { fail "A/B ${nm}: $k differs between ${ref:h:t} and ${x:t}"; abok=0; }
      done
    fi
    ld=$(grep -E '^SHEPHERD_LOG_DIR=' $ex[1]); [[ -n $ld ]] || { fail "A/B ${nm}: ${x:t} has no SHEPHERD_LOG_DIR (its logs would overwrite another start's)"; abok=0; }
    (( ${+logdirs[$ld]} )) && { fail "A/B ${nm}: ${x:t} shares its SHEPHERD_LOG_DIR with ${logdirs[$ld]}"; abok=0; } || logdirs[$ld]=${x:t}
  done
  (( abok )) || continue
  pa=$(grep -E '^SHEPHERD_PAD_FILE=' $ref); [[ -n $pa ]] || { fail "A/B ${nm}: no SHEPHERD_PAD_FILE (the owner's own presses differ from start to start)"; continue; }
  pf=${pa#SHEPHERD_PAD_FILE=}; rel=${pf#/ux0:data/shepherd/}
  [[ $rel != $pf ]] || { fail "A/B ${nm}: the pad file '$pf' is not under /ux0:data/shepherd/"; continue; }
  src=; for c in $K/data/shepherd/$rel $K/extras/card-env/$rel; do [[ -f $c ]] && { src=$c; break; }; done
  [[ -n $src ]] || { fail "A/B ${nm}: the kit does not carry the pad file $rel"; continue; }
  r=$(python3 -I - "$src" <<'PYEOF'
import re, sys
bad, first = [], None
btn = "cross circle square triangle up down left right l r start select lup ldown lleft lright l2 r2 l3 r3 rup rdown rleft rright".split()
items = [x for l in open(sys.argv[1]) for x in re.split(r"[,\s]+", l.split("#")[0]) if x]
late = False   # (kit #13 bootc) a press at m+600 or later came before: a screen it opens cannot appear inside the window
for it in items:
    sw = re.fullmatch(r"@([a-z0-9]+)(?:\+(\d+))?:([a-z0-9]+)(?::(\d+))?", it.lower())
    if sw:   # (kit #13 bootc) a screen-state wait, "@Screen[+N]:button": due N frames after the game showed that screen (Diag/MenuClock.cs)
        if sw.group(3) not in btn: bad.append(f"cannot parse '{it}'")
        elif first is None or not late: bad.append(f"'{it}': a screen wait before the title press and a press at m+600 or later (the screen could appear inside the window)")
        continue
    m = re.fullmatch(r"(m\+)?(\d+):([a-z0-9]+)(?::(\d+))?", it.lower())
    if not m or m.group(3) not in btn: bad.append(f"cannot parse '{it}'"); continue
    if m.group(1) and int(m.group(2)) >= 600: late = True
    rel, n = bool(m.group(1)), int(m.group(2))
    if not rel and 1231 <= n <= 1400: bad.append(f"'{it}': an absolute press inside / just before the window 1301-1400")
    if rel and first is None:
        first = n
        if m.group(3) != "cross" or n > 200: bad.append(f"'{it}': the first press must be the title press, cross at m+N with N <= 200")
    elif rel and 200 < n < 600: bad.append(f"'{it}': a press between m+201 and m+599 can land inside the window")
if first is None: bad.append("no m+N title press")
print("; ".join(bad) if bad else f"ok {len(items)}")
PYEOF
)
  [[ $r == ok* ]] && say "ok   A/B ${nm}: ${#arms} arms (${na} a / ${nb} b) script ${rel} (${r#ok } presses), the same window lines, each its own log folder" || fail "A/B ${nm}: ${rel}: $r"
done
(( abn )) || say "ok   no A/B pair folders (ab-<name>-a / -b) in extras/card-env/"
# (kit #12, the kit #11 device lesson) every VPK's sce_sys PNGs are 8-bit palette (the Vita's installer refuses anything else: kit #11's P2 / P6)
png_check() { python3 -I - "$@" <<'PYEOF'
import sys, struct, zipfile, os
KIND = {0: "grey", 2: "RGB", 3: "palette", 4: "grey+alpha", 6: "RGBA"}
def verdict(name, b):
    if b[:8] != b"\x89PNG\r\n\x1a\n": return False, f"{name}: not a PNG"
    w, h, depth, ctype = struct.unpack(">IIBB", b[16:26])
    return (depth, ctype) == (8, 3), f"{name}: {w}x{h} {depth}-bit {KIND.get(ctype, ctype)}"
bad, n = [], 0
for a in sys.argv[1:]:
    if a.endswith(".png"):
        ok, why = verdict(os.path.basename(a), open(a, "rb").read()); n += 1
        if not ok: bad.append(why)
        continue
    with zipfile.ZipFile(a) as z:
        pngs = [m for m in z.namelist() if m.startswith("sce_sys/") and m.lower().endswith(".png")]
        if not pngs: bad.append(f"{os.path.basename(a)}: no sce_sys PNG at all")
        for m in pngs:
            ok, why = verdict(f"{os.path.basename(a)}:{m}", z.read(m)); n += 1
            if not ok: bad.append(why + " (needs 8-bit palette)")
print(f"{n} PNG(s)" + (": " + "; ".join(bad[:6]) if bad else ", every one 8-bit palette"))
sys.exit(1 if bad else 0)
PYEOF
}
allv=($K/data/shepherd/vpk/*.vpk(N) $K/data/shepherd/vpk/**/*.vpk(N) $K/alternates/*.vpk(N) $K/probes/*.vpk(N)); allv=(${(u)allv})
if (( ${#allv} )); then
  r=$(png_check $allv); (( $? == 0 )) && say "ok   sce_sys PNGs of ${#allv} VPK(s): $r" || fail "sce_sys PNGs: $r"
fi
if [[ ${CHECK_KIT_NEGCTL:-0} == 1 ]]; then   # the checker's own negative control: a made-up 8-bit RGB PNG header must FAIL
  NP=${TMPDIR:-/tmp}/check-kit-negctl-$$.png
  python3 -I -c 'import struct,zlib,sys; c=lambda t,d: struct.pack(">I",len(d))+t+d+struct.pack(">I",zlib.crc32(t+d)); open(sys.argv[1],"wb").write(bytes([137,80,78,71,13,10,26,10])+c(b"IHDR",struct.pack(">IIBBBBB",4,4,8,2,0,0,0))+c(b"IDAT",zlib.compress(bytes(13*4)))+c(b"IEND",b""))' $NP
  r=$(png_check $NP); rc=$?; rm -f $NP
  (( rc != 0 )) && say "ok   negative control: the PNG check refuses an RGB PNG ($r)" || fail "negative control: the PNG check PASSED an RGB PNG"
fi
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
for EZ in $K/data/shepherd/kit*-env-files.zip(N); do   # (kit #11: any kit's zip, = extras/card-env/kit<N>-env/)
  ED=${${EZ:t}%-files.zip}
  zl=$(unzip -Z1 "$EZ" 2>/dev/null | grep -v '/$' | LC_ALL=C sort); el=$(cd $K/extras/card-env 2>/dev/null && find $ED -type f ! -name '._*' ! -name '.DS_Store' | LC_ALL=C sort)
  if [[ -n $zl && $zl == $el ]]; then
    zbad=0; for f in ${(f)zl}; do cmp -s <(unzip -p "$EZ" "$f") "$K/extras/card-env/$f" || { zbad=1; fail "${EZ:t}: $f differs from extras/card-env/$f"; }; done
    (( zbad )) || say "ok   data/shepherd/${EZ:t} = extras/card-env/$ED/ ($(print -r -- $zl | wc -l | tr -d ' ') files)"
  else fail "data/shepherd/${EZ:t} does not hold exactly extras/card-env/$ED/'s files"; fi
  if [[ -n ${CHECK_KIT_README:-} ]]; then   # every env file the README names must be inside the zip (no loose-only folder)
    rn=0; for want in ${(f)"$(grep -oE 'ENV: paste [A-Za-z0-9_.-]+/env[A-Za-z0-9_]*\.txt' "$CHECK_KIT_README" | sed 's/^ENV: paste //' | LC_ALL=C sort -u)"}; do
      rn=$((rn + 1)); print -r -- "$zl" | grep -qx "$ED/$want" || fail "${CHECK_KIT_README:t} names $want, which ${EZ:t} does not hold"; done
    (( rn )) && say "ok   ${CHECK_KIT_README:t}: the $rn env file(s) its ENV lines name are all inside ${EZ:t}" || fail "${CHECK_KIT_README:t} has no 'ENV: paste <folder>/<file>' line"
  fi
done
# (kit #11) the boot screen's emblem set (data/video/boot/): SET.txt + every recording it names, checked by bootscreen_set.py check
for BSD in $K/data/shepherd/data/video/boot $K/data/shepherd/data/video/boot-seq; do   # (kit #12: + boot-seq/, the boot sequence's set: the same check)
if [[ -d $BSD ]]; then
  BSP=$HERE/bootscreen_set.py
  if [[ ! -f $BSD/SET.txt ]]; then fail "data/shepherd/data/video/${BSD:t}/ has no SET.txt"
  elif [[ ! -f $BSP ]]; then fail "the emblem-set check needs $BSP (run check-kit.sh from the repo's vita/tools)"
  elif r=$(python3 -I - "$BSD" "$BSP" <<'PYEOF'
import sys, os, re, subprocess
d, tool = sys.argv[1], sys.argv[2]
lines = [l.split("#", 1)[0].strip() for l in open(os.path.join(d, "SET.txt"), encoding="utf-8", errors="replace")]
lines = [l for l in lines if l]
bad = []
if "shb-set 1" not in lines: bad.append("no 'shb-set 1' line")
rings = [l.split()[1] for l in lines if l.startswith("rings ") and len(l.split()) > 1]
if len(rings) != 1 or rings[0] not in ("drawn", "white"): bad.append("no single 'rings drawn|white' line")
dflt = "default on" in lines
em = [l.split() for l in lines if l.startswith("emblem ")]
if not em: bad.append("no emblem line")
if dflt and rings[:1] != ["drawn"]: bad.append("'default on' without 'rings drawn'")
files = sorted(f for f in os.listdir(d) if f.endswith(".shb") and not f.startswith("._"))
named = [e[1] for e in em if len(e) >= 5]
for f in files:
    if f not in named: bad.append(f + " is not in SET.txt")
for e in em:
    if len(e) < 5: bad.append("short emblem line: " + " ".join(e)); continue
    f, cs = e[1], e[4].lower(); p = os.path.join(d, f)
    if not os.path.isfile(p): bad.append(f + " (missing)"); continue
    out = subprocess.run([sys.executable, "-I", tool, "check", p], capture_output=True, text=True).stdout
    m = re.search(r"OK -- .*checksum ([0-9a-f]{8}); rings: (\S+ ?\S*).*emblem: (\w+)", out)
    if not m: bad.append(f"{f}: not a whole recording ({out.strip()[:120]})"); continue
    if m.group(1) != cs: bad.append(f"{f}: checksum {m.group(1)} != SET.txt's {cs}")
    white = m.group(2).startswith("WHITE")
    if rings[:1] == ["white"] and not white: bad.append(f"{f}: SET.txt says 'rings white' but its rings pass the check (re-make SET.txt)")
    if rings[:1] == ["drawn"] and white: bad.append(f"{f}: SET.txt says 'rings drawn' but it shows the white-square rings")
    if m.group(3) != "present": bad.append(f"{f}: no emblem drawn")
print(f"{len(em)} recording(s), rings {rings[0] if rings else '?'}, {'default on' if dflt else 'opt-in (no default on)'}" + (": " + "; ".join(bad[:6]) if bad else " -- each whole, its emblem present, its checksum = SET.txt's"))
sys.exit(1 if bad else 0)
PYEOF
); then say "ok   data/shepherd/data/video/${BSD:t}/ (the boot screen's emblem set): $r"
  else fail "data/shepherd/data/video/${BSD:t}/: $r"; fi
fi
done
# (kit #13) the emblem crossfade files (XFADE.txt + xf-*.shx + xf-check.bin) of each set folder that carries them: bootscreen_xfade.py check
XFT=$HERE/bootscreen_xfade.py; xfn=0
for BSD in $K/data/shepherd/data/video/boot $K/data/shepherd/data/video/boot-seq; do
  [[ -f $BSD/XFADE.txt ]] || continue; xfn=$((xfn + 1))
  if [[ ! -f $XFT ]]; then fail "the crossfade check needs $XFT (run check-kit.sh from the repo's vita/tools)"; continue; fi
  r=$(python3 -I "$XFT" check "$BSD" 2>&1); rc=$?
  if (( rc == 0 )) && [[ $r == *"CHECK OK"* ]]; then say "ok   data/shepherd/data/video/${BSD:t}/ crossfade: $(print -r -- "$r" | grep -m1 '^XFADE' | cut -c1-170) -- CHECK OK"
  else fail "data/shepherd/data/video/${BSD:t}/ crossfade files do not check: $(print -r -- "$r" | tail -2 | tr '\n' ' ' | cut -c1-220)"; fi
  if [[ ${CHECK_KIT_NEGCTL:-0} == 1 ]]; then   # the checker's own negative control: one byte of one still changed in a copy must FAIL
    ND=${TMPDIR:-/tmp}/check-kit-xfneg-$$; rm -rf $ND; cp -R "$BSD" $ND
    sx=($ND/xf-*.shx(N)); if (( ${#sx} )); then
      python3 -I -c 'import sys; p=sys.argv[1]; b=bytearray(open(p,"rb").read()); b[len(b)//2]^=0x5a; open(p,"wb").write(bytes(b))' $sx[1]
      r=$(python3 -I "$XFT" check $ND 2>&1); rc=$?
      (( rc != 0 )) && say "ok   negative control: the crossfade check refuses ${BSD:t}/ with one byte of ${sx[1]:t} changed ($(print -r -- "$r" | tail -1 | cut -c1-120))" || fail "negative control: the crossfade check PASSED a changed ${sx[1]:t}"
    else fail "negative control: ${BSD:t}/ has XFADE.txt but no xf-*.shx"; fi
    rm -rf $ND
  fi
done
(( xfn )) || say "note no crossfade set (XFADE.txt) in the kit's data/video/boot*/"
# (kit #12) the boot sequence's second clip (launcher apps): a whole .shv clip when the kit carries it or the boot-seq set
MMC=$K/data/shepherd/data/video/MassiveMonster.shv
if [[ -f $MMC ]]; then
  if r=$(shv_check "$MMC"); then say "ok   data/shepherd/data/video/MassiveMonster.shv (the boot sequence's clip): $r"
  else fail "data/shepherd/data/video/MassiveMonster.shv is not a whole .shv clip: $r"; fi
elif [[ -d $K/data/shepherd/data/video/boot-seq ]]; then fail "data/video/boot-seq/ without data/video/MassiveMonster.shv (the boot sequence falls back to kit #11's boot without its clip)"; fi
# (kit 13.1, vita/notes/kit13-fixlooks.md section 3) the sharper second clip, when the kit carries it: whole, and flagged K8SHV_F_LUMA_RED with its content rectangle inside the frame -- without the
# flag the launcher, which prefers this file, would show the logo grey -- and as many pictures as the RGB clip it stands in for. CHECK_KIT_NEGCTL=1: the RGB clip must be refused by this check.
shv_luma() { python3 -I - "$1" ${2:-} <<'PYEOF'
import sys, struct
b = open(sys.argv[1], "rb").read(192); f = struct.unpack_from("<25I", b, 0)
w, h, fr, fl, cw, ch, cx, cy = f[3], f[4], f[7], f[20], f[21], f[22], f[23], f[24]
if not fl & 16: print(f"no K8SHV_F_LUMA_RED flag (flags {fl:#x}): the launcher would show it grey"); sys.exit(1)
if not cw or not ch or cx + cw > w or cy + ch > h: print(f"content rectangle {cw} x {ch} at {cx},{cy} outside the {w} x {h} frame"); sys.exit(1)
if len(sys.argv) > 2:
    r = open(sys.argv[2], "rb").read(192); n = struct.unpack_from("<8I", r, 0)[7]
    if n != fr: print(f"{fr} pictures, the RGB clip {n}"); sys.exit(1)
print(f"flag set, content {cw} x {ch} at {cx},{cy}, {fr} pictures"); sys.exit(0)
PYEOF
}
MML=$K/data/shepherd/data/video/MassiveMonster-luma.shv
if [[ -f $MML ]]; then
  if r=$(shv_check "$MML") && r2=$(shv_luma "$MML" "$MMC"); then say "ok   data/shepherd/data/video/MassiveMonster-luma.shv (the boot sequence's sharper clip): $r; $r2"
  else fail "data/shepherd/data/video/MassiveMonster-luma.shv: $r ${r2:-}"; fi
  if [[ ${CHECK_KIT_NEGCTL:-0} == 1 && -f $MMC ]]; then
    r=$(shv_luma "$MMC"); rc=$?
    (( rc != 0 )) && say "ok   negative control: the luma check refuses the RGB clip ($r)" || fail "negative control: the luma check PASSED the RGB clip"
  fi
fi
# (kit #11) probes/: the hardware probe VPKs and their own SHA256SUMS.txt
if [[ -d $K/probes ]]; then
  pv=($K/probes/*.vpk(N))
  if [[ ! -f $K/probes/SHA256SUMS.txt ]]; then fail "probes/ has no SHA256SUMS.txt"
  else
    pb=0; for v in $pv; do grep -q "  ${v:t}\$" $K/probes/SHA256SUMS.txt || { pb=1; fail "probes/${v:t} is not in probes/SHA256SUMS.txt"; }; done
    ( cd $K/probes && shasum -a 256 -c SHA256SUMS.txt > /dev/null 2>&1 ) || { pb=1; fail "probes/SHA256SUMS.txt does not match the probe VPKs"; }
    (( pb )) || say "ok   probes/: ${#pv} probe VPK(s), each = probes/SHA256SUMS.txt ($(for v in $pv; do echo -n "${v:t} "; done))"
  fi
fi
# (kit #11) the play apps' version (param.sfo APP_VER): one version for the five play apps, = CHECK_KIT_APPVER when given
typeset -A av; av=()
for v in $vp; do
  t=$(unzip -p "$v" sce_sys/param.sfo 2>/dev/null | LC_ALL=C grep -aoE 'SHPD[0-9]{5}' | head -1)
  [[ $t == SHPD000(97|98|95) || $t == SHPD0019[34] ]] || continue
  a=$(unzip -p "$v" sce_sys/param.sfo 2>/dev/null | LC_ALL=C grep -aoE '[0-9]{2}\.[0-9]{2}' | head -1); av[${v:t}]=$a
done
avs=(${(u)av}); if (( ${#av} )); then
  if (( ${#avs} != 1 )); then fail "the play apps carry different APP_VERs: $(for k in ${(k)av}; do echo -n "$k=${av[$k]} "; done)"
  elif [[ -n ${CHECK_KIT_APPVER:-} && ${avs[1]} != $CHECK_KIT_APPVER ]]; then fail "the play apps carry APP_VER ${avs[1]}, not $CHECK_KIT_APPVER"
  else say "ok   the ${#av} play app(s) carry APP_VER ${avs[1]}${CHECK_KIT_APPVER:+ (= CHECK_KIT_APPVER)}"; fi
fi
# (kit 14) THE WALK's env rules: no test-route line anywhere, no args file; the walk steps' folders end nothing before the fight
walk_env_check() {   # <env file> <folder name> [v1]; prints the problem, exit 1 = a problem (v1: the kit ships a version-1 pack without its pk2)
  local e=$1 d=$2 v1=${3:-0} l
  if [[ $v1 == 1 ]] && ! grep -qE '^SHEPHERD_K10_PACK=0[[:space:]]*$' "$e"; then
    echo "the kit ships a version-1 pack without its pk2 and this env file does not say SHEPHERD_K10_PACK=0 (the card's old pk2 ends the session at boot)"; return 1; fi
  l=$(grep -nE '^(SHEPHERD_TIME|SHEPHERD_VIDEO_LENGTHS|SHEPHERD_PHYS_HASH|SHEPHERD_K13_SHORT_POOLS|SHEPHERD_PHYS_EVENTLOG|VLX_[A-Z0-9_]*)=' "$e" | head -1)
  [[ -n $l ]] && { echo "a test-route line (the VLX drives' own, never the owner's): $l"; return 1; }
  if [[ $d == walk* || $d == off* || $d == x*-il* ]]; then
    grep -qE '^SHEPHERD_STOP_SCENE=0[[:space:]]*$' "$e" || { echo "a walk step without SHEPHERD_STOP_SCENE=0 (the Vita profile would end the session at the intro's load)"; return 1; }
    l=$(grep -nE '^(SHEPHERD_STOP_AT|SHEPHERD_PAD_FILE|SHEPHERD_PAD)=' "$e" | head -1)
    [[ -n $l ]] && { echo "a walk step that ends or plays itself (the owner plays): $l"; return 1; }
  fi
  return 0
}
wn=0; ww=0; wv1=0; [[ -f $K/data/shepherd/pack/shepherd.pack && ! -f $K/data/shepherd/pack/shepherd.pk2 ]] && wv1=1
for e in $K/extras/card-env/kit*-env/**/env*.txt(N.); do
  d=${${e:h}:t}; wn=$((wn + 1)); [[ $d == walk* || $d == off* || $d == x*-il* ]] && ww=$((ww + 1))
  r=$(walk_env_check "$e" "$d" $wv1) || fail "${e#$K/}: $r"
done
for a in $K/extras/**/args*.txt(N.) $K/data/shepherd/args*.txt(N.); do fail "${a#$K/}: an args file in the kit (a frame count in it ends the session at that frame)"; done
(( wn )) && say "ok   kit 14 walk rules: $wn env file(s) without a test-route line, $ww walk-step file(s) with SHEPHERD_STOP_SCENE=0 and nothing that ends or plays the step$( (( wv1 )) && echo "; every one says SHEPHERD_K10_PACK=0 (a version-1 pack ships without its pk2)")"
if [[ ${CHECK_KIT_NEGCTL:-0} == 1 ]]; then   # the rule's own negative controls: a walk folder without the stop line, and one with a test line, must FAIL
  NT=$(mktemp -d "${TMPDIR:-/tmp}/ckwalk.XXXXXX"); mkdir -p $NT/walk9-neg
  print -r -- 'SHEPHERD_CHECKPOINTS=0,500' > $NT/walk9-neg/env_play.txt
  if walk_env_check $NT/walk9-neg/env_play.txt walk9-neg > /dev/null; then fail "kit 14 walk rule: its negative control (a walk folder without SHEPHERD_STOP_SCENE=0) PASSED"; else say "ok   kit 14 walk rule: negative control 1 (no stop line) refused"; fi
  print -l 'SHEPHERD_STOP_SCENE=0' 'SHEPHERD_TIME=frame' > $NT/walk9-neg/env_play.txt
  if walk_env_check $NT/walk9-neg/env_play.txt walk9-neg > /dev/null; then fail "kit 14 walk rule: its negative control (a test-route line) PASSED"; else say "ok   kit 14 walk rule: negative control 2 (a test-route line) refused"; fi
  print -l 'SHEPHERD_STOP_SCENE=0' > $NT/walk9-neg/env_play.txt
  if walk_env_check $NT/walk9-neg/env_play.txt walk9-neg 1 > /dev/null; then fail "kit 14 walk rule: its negative control (no SHEPHERD_K10_PACK=0 beside a pk2-less version-1 pack) PASSED"; else say "ok   kit 14 walk rule: negative control 3 (no SHEPHERD_K10_PACK=0) refused"; fi
  rm -rf $NT
fi
# (kit 14) the walk's pack, when the kit carries it: listed in owner-manifest.txt (its sha256 checked above), a version-1 pack; a chunked pack beside it was made from it
PK=$K/data/shepherd/pack/shepherd.pack
if [[ -f $PK ]]; then
  [[ -f $K/owner-manifest.txt ]] && grep -q $'\tpack/shepherd.pack$' $K/owner-manifest.txt || fail "the kit carries pack/shepherd.pack but owner-manifest.txt does not list it"
  pm=$(head -c 4 "$PK" | LC_ALL=C tr -cd '[:print:]')
  [[ $pm == SHPK ]] && say "ok   pack/shepherd.pack: $(stat -f %z "$PK") B, a version-1 pack (magic SHPK), sha256 $(shasum -a 256 "$PK" | cut -c1-16) (the manifest's row checked above)" \
    || fail "pack/shepherd.pack: magic '$pm', not a version-1 pack (SHPK)"
fi
for s in push-kit.sh read-logs.sh; do [[ -f $K/$s ]] || fail "no $s"; done
grep -q 'splash_row' "$K/push-kit.sh" 2>/dev/null && say "ok   push-kit.sh checks the splash table (kit #7 template)" || fail "push-kit.sh is not the kit #7 template (no splash table check)"
SC=$K/data/shepherd/data/shader-cull.tsv
[[ -f $SC ]] && say "note data/shepherd/data/shader-cull.tsv: $(grep -cE $'^[^#].*\t[012](\t|$)' "$SC") shader row(s) (kit #7 G6: the Cull state of the shaders the Vita pack carries without one)"
(( bad )) && { say "FAIL check-kit: $bad problem(s)"; exit 1; } || { say "PASS check-kit $K"; exit 0; }
