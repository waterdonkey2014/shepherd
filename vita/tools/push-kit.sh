#!/bin/zsh
# SHEPHERD DEVICE KIT #10 (2026-10-29: k10-integ = the kit #10 integration (memory, aot, io, load, cache, boot, fidelity) + its review fixes
# + VERSION 0.10; merged to main after the kit build) -- copies this kit onto the Vita's memory card.
# (Template: vita/tools/push-kit.sh in the repo = this script: device kit #5's (the memory-card check that refuses a game card
#  (cartridge), a card without the shepherd folder, never deletes anything) with kit #10's file list; copy it into each new kit and update
#  its kit lines, the file count and step 7's owner-folder path.)
# (Kit #10 additions: NEW owner files -- managed-ilz/ (the game's DLLs with their unused code compressed: 116 DLLs + ILZ-MANIFEST.txt; the
#  apps check each one against its original once, then use it), pack/shepherd.pk2 + pack/shepherd.pack.xxh (the chunked pack, read instead
#  of shepherd.pack, which STAYS on the card: SHEPHERD_K10_PACK=0 goes back to it), managed/boot.ilb (the game's DLLs in one file, read in one
#  stream; the first start compares it with the loose DLLs, the next ones use it), data/video/BootLoading.shb (the boot loading screen's
#  pictures; the screen is OFF unless an env file turns it on), kit10-env-files.zip (the README's ready-made env files: extracted once on the
#  Vita) -- and the Derived DLL REPLACED; step 3 prints one line for managed-ilz/; step 5d checks the new files on the card; step 7 checks
#  managed-ilz/ by sha256 too; step 9f reports runtime/k10-avc-refeed.off (the logos' drain switched off after a crash / hang).)
# (Kit #9 additions: the kit REPLACES three of the card's Unity modules (managed/UnityEngine.CoreModule.dll, UnityEngine.InputLegacyModule.dll,
#  UnityEngine.UIModule.dll: the runtime track's typed v2 rewrite -- every kit #9 app is bound to them by MVID, so kit #8's apps no longer
#  start with them; the card's old copies are saved on the Mac in push-logs/replaced-<time>/ like every replaced file) and the Derived DLL;
#  step 9e reports runtime/k9-avc-drain.off (the decoder's end-of-stream call switched off after a crash / hang, device track).)
# (Kit #8 additions: step 1 also REFUSES a kit without data/physics2d-settings.bin (the play apps' physics settings: physics review) and,
#  when the kit holds a launcher app (SHPD00193 / 00194), without the two .shv clips data/video/*.shv (integration review: without them the
#  launcher starts behind a black screen); step 5c checks the clips on the card; step 9d reports save-backups/ (read-only).)
# (Kit #7 template additions, track probes -- kit #6's device run had no Devolver splash time because data/video-lengths.tsv never reached
#  the card: step 1 now REFUSES a kit without data/shepherd/data/video-lengths.tsv holding a Devolver row the engine takes, and step 5b
#  checks the card's copy after the copy -- present, the kit's bytes, the row -- or says PROBLEM; step 9c reports (read-only) the device
#  profile the apps write at every start and the measurement app's storage results.)
#
# On the Vita: VitaShell -> START -> USB ("Mount ux0:"), cable in. Then on the Mac:
#     ${SHEPHERD_WORK:-$HOME/shepherd-work}/kit10/kit/push-kit.sh           (waits up to 30 min for the card; "push-kit.sh 60" waits 60)
# Kit #10 holds the 7 VPKs (vpk/k10-1 .. k10-7: the same seven apps / title ids as kit #9), managed/Shepherd.Engine.Derived.dll (REPLACE: the
# new apps were compiled with this exact file), NEW: managed-ilz/ (117 files), pack/shepherd.pk2 + pack/shepherd.pack.xxh, managed/boot.ilb,
# data/video/BootLoading.shb, kit10-env-files.zip; and "same" (already on the card since kit #7 / #8 / #9, carried so the kit can be checked
# whole): the typed v2 managed/UnityEngine.CoreModule.dll / InputLegacyModule.dll / UIModule.dll, managed/spine-unity.dll,
# data/video-lengths.tsv, data/physics2d-settings.bin, the two clips data/video/*.shv and cache/json/catalog.jbake. The pack
# (shepherd.pack: keep it -- the switch back), the other game DLLs, data, audio bake, pad script and sandbox saves stay as they are. The
# card's old copy of every replaced file is saved on the Mac first (see below): with it and kit #9's VPKs the card goes back to kit #9 (kit #9's
# apps ignore every new kit #10 file).
#
# What it does:
#   - waits for the MEMORY CARD: a volume under /Volumes with VitaShell/, app/ and data/ at its root and no gc/ (exactly one; two such
#     volumes -> it stops). A Vita GAME CARD (cartridge) mounted by mistake looks like app/<TITLEID> + gc/ + license/ WITHOUT VitaShell/:
#     it is never used (nothing is ever written to it); the script says so and keeps waiting (VitaShell -> START -> Settings -> USB device
#     -> Memory Card, then mount again). A volume with app/ and no VitaShell/ is named as a cartridge when it has gc/ or license/ OR an
#     app/<TITLE ID> folder (4 capitals + 5 digits, e.g. app/PCSE00089). A card without data/shepherd (kit #5's folder) is refused too.
#   - checks this kit against SHA256SUMS.txt first (a damaged kit is never copied)
#   - checks the free space on the card
#   - copies data/shepherd/ of this kit to ux0:data/shepherd/ with `rsync -rt` (new files are added, a kit file with the same name
#     replaces the card's copy -- the card's old copy of any replaced file is saved on the Mac first, in push-logs/replaced-<time>/)
#   - checks every copied file on the card against SHA256SUMS.txt (sha256)
#   - LISTS the macOS "._" side files (AppleDouble, magic 00051607) under ux0:data/shepherd/, if any (harmless junk; it never removes them)
#   - checks that the rest of ux0:data/shepherd/ (game DLLs, data, audio bake, pad.txt, the pack) is what the apps need: sizes, and for
#     managed/ (the game DLLs the engine-AOT apps are bound to by MVID) the sha256 of every file as well
#   - REPORTS (read-only) the env / args files in ux0:data/shepherd/ (an env file left on the card changes how an app starts), what
#     the card's cache/ folder holds (the shader cache and the hook list the apps write themselves) and (kit #6) vita-settings.txt, the
#     PS Vita Settings rows' saved choices
#   - logs everything it did to push-logs/push-<time>.txt next to this script
# It NEVER ejects the card and NEVER deletes or moves anything on the card. It writes only inside ux0:data/shepherd/ (the kit's files) and
# nothing anywhere else on the card; every file already in ux0:data/shepherd/ that is not in this kit stays exactly as it is. Eject in
# Finder yourself when it says DONE.
set -u
K=${0:a:h}
SRC=$K/data/shepherd
SUMS=$K/SHA256SUMS.txt
WAIT_MIN=30
for a in "$@"; do
  case $a in
    <->) WAIT_MIN=$a ;;
    *) echo "usage: push-kit.sh [minutes to wait for the card]"; exit 2 ;;
  esac
done
TS=$(date +%Y%m%d-%H%M%S)
LOGDIR=$K/push-logs; mkdir -p "$LOGDIR"
LOG=$LOGDIR/push-$TS.txt
exec > >(tee -a "$LOG") 2>&1
export COPYFILE_DISABLE=1    # no ._ files from the Mac's own copy routines
# (kit #7) does a video-length table hold a Devolver splash row the ENGINE takes? The engine's rules exactly (vita/shepherd/splash_rows.h =
# Runtime/K6Engine.cs LoadLengths): per line cut at '#', TAB-separated, >= 3 fields, field 0 starts with Devolver_Animated_Logo (any case),
# field 1 a whole number > 0, field 2 "num" or "num/den" with num / den > 0 (a bad den counts as 1). Exit 0 = yes.
splash_row() { python3 -I - "$1" <<'PYEOF'
import sys, re
def num(s):
    s = s.strip()
    return float(s) if re.fullmatch(r"[+-]?(\d+\.?\d*|\.\d+)([eE][+-]?\d+)?", s) else None
ok = False
for line in open(sys.argv[1], encoding="utf-8", errors="replace").read().split("\n"):
    line = line.split("#", 1)[0]
    f = line.split("\t")
    if len(f) < 3 or not f[0].strip().lower().startswith("devolver_animated_logo"): continue
    fr = f[1].strip()
    if not re.fullmatch(r"[+-]?\d+", fr) or int(fr) <= 0: continue
    r = f[2].split("/", 1); n = num(r[0])
    d = num(r[1]) if len(r) > 1 else None
    if n is None: continue
    if d is None or d <= 0: d = 1.0
    if n / d > 0: ok = True; break
sys.exit(0 if ok else 1)
PYEOF
}
# (kit #8) a whole .shv clip (vita/shepherd/k8_shv.h): magic SHV1, version 1, header 192 B, the header checksum, the index and the pictures
# inside the file. Prints "<frames> frames <w>x<h>, <bytes> B" or the reason; exit 0 = whole. (= vita/tools/check-kit.sh's)
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

echo "push-kit.sh (Shepherd device kit #10) started $(date '+%Y-%m-%d %H:%M:%S'); log: $LOG"
[[ -d $SRC && -f $SUMS ]] || { echo "FAIL: $SRC or $SUMS missing -- is this the kit folder?"; exit 1; }

# 1. the kit itself must match SHA256SUMS.txt (only the data/ lines are copied to the card)
echo "== 1. checking the kit against SHA256SUMS.txt"
bad=0; n=0
while read -r sum rel; do
  [[ $rel == data/* ]] || continue
  n=$((n + 1))
  [[ -f $K/$rel ]] || { echo "FAIL kit file missing: $rel"; bad=$((bad + 1)); continue; }
  [[ $(shasum -a 256 "$K/$rel" | cut -c1-64) == $sum ]] || { echo "FAIL kit file damaged: $rel"; bad=$((bad + 1)); }
done < "$SUMS"
(( bad == 0 )) || { echo "STOP: $bad kit file(s) do not match SHA256SUMS.txt; nothing was copied"; exit 1; }
# (kit #7) the Devolver splash table is part of every kit since kit #6: without it the logo takes 0 s and the frame-clock references
# do not line up (kit #6's device run). A kit stage that forgot it is stopped here, before anything is copied.
VL=data/shepherd/data/video-lengths.tsv
[[ -f $K/$VL ]] || { echo "STOP: the kit has no $VL (the Devolver splash table: vita/shepherd/stage-owner.sh writes it) -- the kit stage must add it; nothing was copied"; exit 1; }
splash_row "$K/$VL" || { echo "STOP: the kit's $VL has no Devolver row the engine takes (name TAB frames TAB rate, outside a '#'); nothing was copied"; exit 1; }
echo "   ok: the kit carries $VL with the Devolver row (the logo's 6.27 s)"
# (kit #8, physics review fix) the play apps' physics core needs the project's Physics2DSettings (the strict card pack has none): without
# data/physics2d-settings.bin they run physics level 0 (a WARN PHYS line). A kit stage that forgot it is stopped here too.
PS=data/shepherd/data/physics2d-settings.bin
[[ -f $K/$PS ]] || { echo "STOP: the kit has no $PS (the project's Physics2DSettings, 280 B: vita/shepherd/stage-owner.sh writes it) -- the play apps would run physics level 0; nothing was copied"; exit 1; }
echo "   ok: the kit carries $PS (the play apps' physics settings)"
# (kit #8, integration review) a kit with a launcher app (SHPD00193 / 00194: the VPK's param.sfo, or its name) must carry the two clips: without
# them the launcher starts behind a black screen and the engine draws the logo black. A kit stage that forgot them (stage-owner.sh
# STAGE_SPLASH=1 makes them) is stopped here too.
launchers=(); for v in $K/data/shepherd/vpk/*.vpk(N); do
  t=$(unzip -p "$v" sce_sys/param.sfo 2>/dev/null | LC_ALL=C grep -aoE 'SHPD0019[34]' | head -1)
  [[ -n $t || ${v:t} == *launcher* ]] && launchers+=("${v:t}")
done
CLIPS=(data/shepherd/data/video/Devolver_Animated_Logo_v001_4k_Audio-_HQ_HBR_MP4.shv data/shepherd/data/video/Intro.shv)
if (( ${#launchers} )); then
  for c in $CLIPS; do
    [[ -f $K/$c ]] || { echo "STOP: the kit holds launcher app(s) (${launchers[*]}) but not $c (stage-owner.sh STAGE_SPLASH=1 makes it) -- they would start behind a black screen; nothing was copied"; exit 1; }
    r=$(shv_check "$K/$c") || { echo "STOP: the kit's $c is not a whole .shv clip ($r); nothing was copied"; exit 1; }
  done
  echo "   ok: the kit carries the launcher apps' two video clips (data/video/*.shv)"
fi
typeset -A listed; while read -r sum rel; do listed[$rel]=1; done < "$SUMS"
extra=$(cd "$K" && find data -type f ! -name '._*' ! -name '.DS_Store' | sort | while read -r f; do (( ${+listed[$f]} )) || echo "$f"; done)
[[ -z $extra ]] || { echo "STOP: files in data/ that SHA256SUMS.txt does not list (not copied):"; echo "$extra"; exit 1; }
echo "   ok: $n kit files, all match"

# 2. wait for the MEMORY CARD (ux0:). Only a volume with VitaShell/ + app/ + data/ and no gc/ is the card. A game card (gro0:, the
#    cartridge: app/<TITLEID> + gc/ + license/, no VitaShell/) is reported and never used -- VitaShell's USB device is set to "Game Card".
#    (revision 2: app/ without VitaShell/, plus gc/ or license/ or an app/<TITLE ID> folder -- a cartridge showing only app/ is named too)
is_card() { [[ -d $1/VitaShell && -d $1/app && -d $1/data && ! -e $1/gc ]]; }
is_cart() { [[ ! -d $1/VitaShell && -d $1/app ]] || return 1; [[ -d $1/gc || -d $1/license ]] && return 0
  local t=($1/app/[A-Z][A-Z][A-Z][A-Z][0-9][0-9][0-9][0-9][0-9](N/)); (( ${#t} )); }
echo "== 2. waiting up to $WAIT_MIN min for the Vita's MEMORY CARD (VitaShell: START -> USB)"
end=$(( $(date +%s) + WAIT_MIN * 60 )); V=; typeset -A told
while [[ $(date +%s) -lt $end ]]; do
  cands=()
  for d in /Volumes/*(N/); do
    if is_card "$d"; then cands+=("$d")
    elif is_cart "$d"; then
      (( ${+told[$d]} )) || { told[$d]=1
        echo "   NOT the memory card: $d looks like a Vita GAME CARD (cartridge: $(ls "$d/app" 2>/dev/null | head -3 | tr '\n' ' ')in app/, no VitaShell/)."
        echo "   Nothing is written to it. On the Vita: VitaShell -> START -> Settings -> USB device -> \"Memory Card\", then START -> USB again."
        echo "   (Still waiting for the memory card.)"; }
    elif [[ -d $d/VitaShell && -e $d/gc ]]; then
      (( ${+told[$d]} )) || { told[$d]=1; echo "   NOT used: $d has VitaShell/ AND gc/ -- it is not clear that this is the memory card. Tell Claude."; }
    fi
  done
  if (( ${#cands} == 1 )); then V=${cands[1]}; break; fi
  if (( ${#cands} > 1 )); then echo "STOP: more than one volume looks like a Vita memory card: ${cands[*]} -- unplug the extra one and run again"; exit 1; fi
  sleep 2
done
[[ -n $V ]] || { echo "STOP: no Vita memory card within $WAIT_MIN min (VitaShell: START -> USB, with Settings -> USB device = Memory Card; then run this again)"; exit 1; }
echo "   card at $V"
df -h "$V" | tail -1 | awk '{print "   card: " $2 " size, " $4 " free"}'
D=$V/data/shepherd
[[ -d $D/managed && -d $D/pack ]] || { echo "STOP: $V has no data/shepherd folder with managed/ and pack/ (kit #5's). This kit only updates the folder kits #5-#9 made: see README-KIT10.txt, IF STEP 7 WARNS. Nothing was copied."; exit 1; }

# 3. plan: what is new, what replaces a different file, what is already there
echo "== 3. comparing the kit with ux0:data/shepherd/"
need_kb=0; todo=(); zs=0; zr=0; zn=0
while read -r sum rel; do
  [[ $rel == data/shepherd/* ]] || continue
  sub=${rel#data/shepherd/}; dst=$D/$sub; q=0; [[ $sub == managed-ilz/* ]] && q=1   # (kit #10: managed-ilz/'s 117 files summed up in one line)
  if [[ -f $dst ]]; then
    if [[ $(stat -f %z "$dst") == $(stat -f %z "$K/$rel") && $(shasum -a 256 "$dst" | cut -c1-64) == $sum ]]; then
      (( q )) && zs=$((zs + 1)) || echo "   same     $sub (already on the card)"; continue
    fi
    (( q )) && zr=$((zr + 1)) || echo "   REPLACE  $sub (card: $(stat -f %z "$dst") bytes -> kit: $(stat -f %z "$K/$rel") bytes)"
    mkdir -p "$LOGDIR/replaced-$TS/${sub:h}"; cp -p "$dst" "$LOGDIR/replaced-$TS/$sub" && { (( q )) || echo "            the card's old copy saved to push-logs/replaced-$TS/$sub"; }
  else
    (( q )) && zn=$((zn + 1)) || echo "   NEW      $sub ($(stat -f %z "$K/$rel") bytes)"
  fi
  todo+=("$sub"); need_kb=$(( need_kb + $(stat -f %z "$K/$rel") / 1024 + 1 ))
done < "$SUMS"
(( zs + zr + zn )) && echo "   managed-ilz/ (kit #10, the compressed-IL DLLs): $zn NEW, $zr REPLACE$( (( zr )) && echo " (the old copies saved to push-logs/replaced-$TS/managed-ilz/)"), $zs same"
free_kb=$(df -k "$V" | tail -1 | awk '{print $4}')
echo "   to copy: ${#todo} file(s), $(( need_kb / 1024 )) MiB; card free: $(( free_kb / 1024 )) MiB"
(( free_kb > need_kb + 512 * 1024 )) || { echo "STOP: not enough free space on the card (need the kit + 512 MiB for installing); nothing copied"; exit 1; }

# 4. copy (rsync -rt: recursive + modification times; never --delete)
if (( ${#todo} )); then
  echo "== 4. copying data/shepherd/ -> ux0:data/shepherd/ (rsync -rt, nothing deleted)"
  t0=$(date +%s)
  rsync -rt --exclude='.*' "$SRC/" "$D/" || { echo "FAIL: rsync reported an error (see above); run push-kit.sh again"; exit 1; }
  sync
  echo "   copied in $(( $(date +%s) - t0 )) s"
else
  echo "== 4. nothing to copy: every kit file is already on the card"
fi

# 5. verify every kit file on the card
echo "== 5. verifying on the card (sha256)"
okc=0; badc=0
while read -r sum rel; do
  [[ $rel == data/shepherd/* ]] || continue
  sub=${rel#data/shepherd/}
  if [[ -f $D/$sub && $(shasum -a 256 "$D/$sub" | cut -c1-64) == $sum ]]; then okc=$((okc + 1)); [[ $sub == managed-ilz/* ]] || echo "   ok   $sub  ${sum:0:16}"
  else
    echo "   retry $sub (checksum differs on the card)"
    mkdir -p "$D/${sub:h}"; cp "$K/$rel" "$D/$sub" && sync
    if [[ $(shasum -a 256 "$D/$sub" | cut -c1-64) == $sum ]]; then okc=$((okc + 1)); echo "   ok   $sub  ${sum:0:16} (after one retry)"
    else badc=$((badc + 1)); echo "   FAIL $sub: still differs after a retry"; fi
  fi
done < "$SUMS"

# 5b. (kit #7) the Devolver splash table on the CARD: present, the kit's bytes, a row the engine takes. Kit #6's device run had none on the
#     card (VIDEO(0 clip lengths) in every engine log, the H1 line "not re-based"), so the logo's 6.27 s were never exercised.
echo "== 5b. the Devolver splash table on the card (data/video-lengths.tsv)"
vsum=$(awk -v r="$VL" '$2 == r {print $1}' "$SUMS")
if [[ ! -f $D/data/video-lengths.tsv ]]; then
  badc=$((badc + 1)); echo "   PROBLEM: ux0:data/shepherd/data/video-lengths.tsv is NOT on the card: the Devolver logo would take 0 s (copy $K/$VL to ux0:data/shepherd/data/)"
elif [[ -n $vsum && $(shasum -a 256 "$D/data/video-lengths.tsv" | cut -c1-64) != $vsum ]]; then
  badc=$((badc + 1)); echo "   PROBLEM: the card's data/video-lengths.tsv differs from the kit's (run push-kit.sh again)"
elif ! splash_row "$D/data/video-lengths.tsv"; then
  badc=$((badc + 1)); echo "   PROBLEM: the card's data/video-lengths.tsv has no Devolver row the engine takes"
else
  echo "   ok: on the card with the Devolver row -- in the logs: the measurement apps' 'H1 frame clock re-based by 376 frames', every engine log's"
  echo "       'VideoPlayer.Play 'Devolver_...' (Unity timing: 6.267 s from the length table ...)' (read-logs.sh checks both)"
fi

# 5c. (kit #8) the launcher apps' two clips on the CARD (data/video/*.shv): present, the kit's bytes (step 5 checked the sha256), whole clips
if (( ${#launchers} )); then
  echo "== 5c. the launcher apps' video clips on the card (data/video/*.shv)"
  for c in $CLIPS; do sub=${c#data/shepherd/}
    if [[ ! -f $D/$sub ]]; then badc=$((badc + 1)); echo "   PROBLEM: ux0:data/shepherd/$sub is NOT on the card (the launcher apps would start behind a black screen)"
    elif r=$(shv_check "$D/$sub"); then echo "   ok   $sub ($r)"
    else badc=$((badc + 1)); echo "   PROBLEM: the card's $sub is not a whole clip ($r): run push-kit.sh again"; fi
  done
fi

# 5d. (kit #10) the new owner files on the CARD: managed-ilz/ (count), the chunked pack beside the card's shepherd.pack (kept: the switch
#     back), the IL bundle, the boot screen's recording, the env-files zip. Step 5 checked their bytes; this says what they are for. Report only.
echo "== 5d. kit #10's new files on the card (step 5 checked their bytes)"
nz=$(find "$D/managed-ilz" -name '*.dll' ! -name '._*' -type f 2>/dev/null | wc -l | tr -d ' ')
echo "   managed-ilz/: $nz compressed-IL DLL(s) (the first start checks each against managed/'s original once: cache/k10-ilz-verified.tsv)"
if [[ -f $D/pack/shepherd.pk2 ]]; then
  if [[ -f $D/pack/shepherd.pack ]]; then echo "   pack/shepherd.pk2 ($(stat -f %z "$D/pack/shepherd.pk2") B) next to pack/shepherd.pack ($(stat -f %z "$D/pack/shepherd.pack") B: keep it -- SHEPHERD_K10_PACK=0 reads it again; the apps check at every open that the .pk2 was made from it)"
  else badc=$((badc + 1)); echo "   PROBLEM: pack/shepherd.pack is NOT on the card: the .pk2 can only be used next to the pack it was made from (copy it back: IF STEP 7 WARNS)"; fi
fi
[[ -f $D/managed/boot.ilb ]] && echo "   managed/boot.ilb ($(stat -f %z "$D/managed/boot.ilb") B): the first start compares it with the loose DLLs and writes cache/il/k10-ilb.ok, the next starts read it$( [[ -f $D/cache/il/k10-ilb.ok ]] && echo " (an .ok from an earlier start is on the card: the managed/ listing decides whether it still counts)")"
[[ -f $D/data/video/BootLoading.shb ]] && echo "   data/video/BootLoading.shb ($(stat -f %z "$D/data/video/BootLoading.shb") B): the boot loading screen's pictures (shown only with SHEPHERD_K10_BOOTSCREEN=1: the README's STEP 1 file)"
[[ -f $D/kit10-env-files.zip ]] && echo "   kit10-env-files.zip: the README's ready-made env files -- on the Vita, extract it ONCE in ux0:data/shepherd/ (README-KIT10.txt, INSTALL)$( [[ -d $D/kit10-env ]] && echo "; ux0:data/shepherd/kit10-env/ is already there")"

# 6. macOS ._ side files (AppleDouble) under ux0:data/shepherd/: the Mac writes them next to files it copies (this script's own copy does
#    not: COPYFILE_DISABLE, rsync without xattrs). They are harmless junk (the kit #3 / #4 runs passed with them). Report only: this
#    script never removes anything.
echo "== 6. looking for macOS ._ side files (AppleDouble) under ux0:data/shepherd/ (report only; nothing is removed)"
ad_n=0
while IFS= read -r -d '' ad; do
  [[ $(head -c 4 "$ad" | xxd -p) == 00051607 ]] || continue
  ad_n=$((ad_n + 1)); (( ad_n <= 20 )) && echo "   found   ${ad#$V/}"
done < <(find "$D" -name '._*' -type f -print0)
(( ad_n > 20 )) && echo "   ... and $((ad_n - 20)) more"
if (( ad_n )); then echo "   $ad_n found and left as they are (harmless)"; else echo "   none"; fi

# 7. the rest of the owner folder (game DLLs, data, audio bake, the pad script, the pack): this kit copies only what changed since kit #5,
#    so check that the card still has everything else the apps load -- by size, and managed/ (the engine-AOT apps are bound to these DLLs by
#    MVID, so a same-size different copy would stop them) also by sha256. Nothing is written. Report only.
echo "== 7. checking the card's owner folder against owner-manifest.txt (managed/ + managed-ilz/: sha256; pack/ data/ audiobake/ cache/json/ pad.txt: sizes)"
miss=0; diffs=0; seen=0; hashed=0
while IFS=$'\t' read -r size sum rel; do
  [[ $size == \#* ]] && continue
  [[ $rel == ((managed|managed-ilz|pack|data|audiobake|cache/json)/*|pad.txt) ]] || continue
  seen=$((seen + 1))
  if [[ ! -f $D/$rel ]]; then miss=$((miss + 1)); (( miss <= 10 )) && echo "   MISSING   $rel"
  elif [[ $(stat -f %z "$D/$rel") != $size ]]; then diffs=$((diffs + 1)); (( diffs <= 10 )) && echo "   DIFFERENT $rel (card $(stat -f %z "$D/$rel") bytes, expected $size)"
  elif [[ $rel == managed/* || $rel == managed-ilz/* ]]; then
    hashed=$((hashed + 1))
    [[ $(shasum -a 256 "$D/$rel" | cut -c1-64) == $sum ]] || { diffs=$((diffs + 1)); (( diffs <= 10 )) && echo "   DIFFERENT $rel (same size, other sha256)"; }
  fi
done < "$K/owner-manifest.txt"
if (( miss + diffs == 0 )); then echo "   ok: all $seen owner files are on the card with the expected sizes ($hashed in managed/ + managed-ilz/ also by sha256)"
else
  echo "   WARNING: $miss missing, $diffs different (another size, or in managed/ / managed-ilz/ other contents) (of $seen). The apps need the whole folder: copy"
  echo "   ${SHEPHERD_WORK:-$HOME/shepherd-work}/kit10/integ-work/kit-build/owner/shepherd/{managed,managed-ilz,pack,data,audiobake,cache} and pad.txt to ux0:data/shepherd/ (README-KIT10.txt, IF STEP 7 WARNS)"
fi

# 8. the env / args files in ux0:data/shepherd/: each app reads its own (the play apps 00097 / 00098 / 00095 and the launcher apps 00193 /
#    00194 share env_play.txt and args_play.txt; the measurement app 00099 env_gl_measure.txt, 00011 env.txt). One left on the card from an earlier test (kit #4's STEP 6
#    copy of env_play.txt) changes how an app starts. Report only: nothing is written, moved or deleted.
echo "== 8. env / args files in ux0:data/shepherd/ (report only)"
envs=("$D"/env*.txt(N) "$D"/args*.txt(N))
if (( ${#envs} )); then
  for f in $envs; do
    [[ ${f:t} == ._* ]] && continue
    echo "   FOUND ${f:t} ($(stat -f %z "$f") bytes): $(grep -v '^#' "$f" | tr -d '\r' | tr '\n' ' ' | cut -c1-160)"
  done
  [[ -f $D/env_play.txt ]] && echo "   NOTE: env_play.txt is on the card: the five play apps (00097 / 00098 / 00095 / 00193 / 00194) read it at every start. Kit #10 starts with NONE there: delete it before STEP 1 (README-KIT10.txt: STEP 1 copies the kit's own file from kit10-env/step1/)."
  [[ -f $D/env_gl_measure.txt ]] && echo "   NOTE: env_gl_measure.txt is on the card: the Measure app reads it. For kit #10's main steps it must not be there before STEP 8 (README-KIT10.txt)."
else
  echo "   none (every app starts with its own defaults)"
fi

# 9. (kit #5) the card's cache/ folder: the catalog bake (kit #5's), and what the apps write there themselves (the shader cache
#    cache/gxp/: 1st GL start writes it, 2nd verifies it, 3rd on loads it; the hook list cache/icalls/). Report only.
echo "== 9. ux0:data/shepherd/cache/ (report only)"
if [[ -d $D/cache ]]; then
  (cd "$D/cache" && find . -type f ! -name '._*' | sed 's#^\./##' | LC_ALL=C sort | while read -r f; do echo "   $f ($(stat -f %z "$f") bytes)"; done)
else echo "   (no cache/ folder)"; fi

# 9b. (kit #6) the PS Vita rows' own file (Settings > Graphics > PS Vita Settings), written by the apps when a row changes. Report only.
echo "== 9b. ux0:data/shepherd/vita-settings.txt (the PS Vita Settings rows; report only)"
if [[ -f $D/vita-settings.txt ]]; then echo "   $(grep -v '^#' "$D/vita-settings.txt" | tr -d '\r' | tr '\n' ' ')"; else echo "   (none yet: the rows' defaults, 4th CPU Core On, CPU Speed 444 MHz -- (kit #9) 500 MHz in the play apps, which only a PSVshellPlus profile can give --, (kit #7) Performance Overlay Off, (kit #8) Rear Touch On, Frame Pacing Off, Restore Save Backup No)"; fi

# 9c. (kit #7) the device profile every GL app writes at start (model, firmware, stores, free space, speeds) and the measurement app's storage
#     read test results. Report only.
echo "== 9c. ux0:data/shepherd/device-profile.txt and probes/storage.txt (written by the apps; report only)"
if [[ -f $D/device-profile.txt ]]; then grep -E '^(model|firmware|store\.|speed|written)' "$D/device-profile.txt" | tr -d '\r' | sed 's/^/   /' | head -14
else echo "   (no device profile yet: the first start of a picture app writes it)"; fi
[[ -f $D/probes/storage.txt ]] && grep -E '^speed\.' "$D/probes/storage.txt" | tr -d '\r' | sed 's/^/   /' | head -6

# 9d. (kit #8, track play) save-backups/: the copies the play apps keep of a save file before the game replaces it (the 5 newest per file) and
#     pending-restore/ (a backup chosen in Settings > Graphics > Restore Save Backup, kept until the next start restores it). Report only.
echo "== 9d. ux0:data/shepherd/save-backups/ (kit #8: written by the play apps; report only)"
if [[ -d $D/save-backups ]]; then
  echo "   $(find "$D/save-backups" -maxdepth 1 -type f ! -name '._*' | wc -l | tr -d ' ') backup(s); pending-restore/: $(find "$D/save-backups/pending-restore" -type f ! -name '._*' 2>/dev/null | sed 's#.*/##' | tr '\n' ' ')"
else echo "   (none yet: the first real save in a play app makes it)"; fi

# 9e. (kit #9, track device) runtime/k9-avc-drain.off: the hardware decoder's end-of-stream call (sceAvcdecDecodeStop, never run on a Vita
#     before kit #9) crashed or hung once, so the launcher and the engine skip it from then on (the last 1-3 pictures of each logo are not
#     shown). Delete the file to try the call again (the README says when). Report only.
echo "== 9e. ux0:data/shepherd/runtime/k9-avc-drain.off / .marker (kit #9: the decoder's end-of-stream call; report only)"
if [[ -f $D/runtime/k9-avc-drain.off ]]; then echo "   FOUND runtime/k9-avc-drain.off: the end-of-stream call is switched OFF on this card ($(head -c 200 "$D/runtime/k9-avc-drain.off" | tr '\n' ' '))"
elif [[ -f $D/runtime/k9-avc-drain.marker ]]; then echo "   FOUND runtime/k9-avc-drain.marker: a start stopped inside the call (the next start writes the .off file)"
else echo "   none (the call has never failed on this card)"; fi

# 9f. (kit #10, track boot) runtime/k10-avc-refeed.off: the logos' drain (the clip's own pictures fed again after its last one, so the decoder
#     hands back the last 4 pictures) did not come back once (a crash or a hang), so the launcher and the engine skip it from then on (the
#     logos end 4 pictures early, as in kit #9). Delete the file to try it again (README-KIT10.txt says when). Report only.
echo "== 9f. ux0:data/shepherd/runtime/k10-avc-refeed.off / .marker (kit #10: the logos' drain; report only)"
if [[ -f $D/runtime/k10-avc-refeed.off ]]; then echo "   FOUND runtime/k10-avc-refeed.off: the logos' drain is switched OFF on this card ($(head -c 200 "$D/runtime/k10-avc-refeed.off" | tr '\n' ' '))"
elif [[ -f $D/runtime/k10-avc-refeed.marker ]]; then echo "   FOUND runtime/k10-avc-refeed.marker: a start stopped inside the drain (the next start writes the .off file)"
else echo "   none (the drain has never failed on this card)"; fi

echo
if (( badc == 0 )); then
  echo "DONE: $okc file(s) on the card match the kit. The card is still MOUNTED: eject it in Finder, unplug,"
  echo "      then install the seven k10 VPKs in VitaShell from ux0:data/shepherd/vpk/, k10-1 first and k10-7 last, and extract"
  echo "      kit10-env-files.zip once (README-KIT10.txt, INSTALL)."
else
  echo "PROBLEM: $badc file(s) do not match on the card (listed above). Run push-kit.sh again; if it repeats, send this log."
fi
echo "log: $LOG"
exit $(( badc > 0 ))
