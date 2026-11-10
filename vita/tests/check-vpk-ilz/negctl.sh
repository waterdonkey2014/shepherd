#!/bin/zsh
# Kit #12 fix + kit (review finding): check_vpk.py's managed-ilz verify loop rebound the script's global failure list, so with a VPK that
# carries managed-ilz/ (SHEPHERD_K12_VPKILZ=1 builds) every FAIL recorded before it was dropped and the gate printed PASS. This test runs
# check_vpk.py with a play-MT build's own arguments on
#   (1) the VPK as built                                  -> must PASS (exit 0)
#   (2) the same VPK + one stray file at its root         -> must FAIL (exit 1) and name the stray
#   (3) the check_vpk.py of <old ref> (default 8b31c8e, before the fix) on (2) -> exits 0: the bug this test exists for (the test's own
#       negative control -- if the old script also fails, this test cannot tell the fix from no fix, and it says so)
# usage: negctl.sh <vb build dir of a play-MT build with managed-ilz/ (e.g. integ-work/vb/k12-play-mt)> [old ref]
set -u; export PATH="$HOME/vitasdk/bin:$PATH"
D=${1:?build dir}; OLD=${2:-8b31c8e}; HERE=${0:a:h}; REPO=${HERE:h:h:h}; W=$D/work; O=$D/out
T=${TMPDIR:-/tmp}/check-vpk-ilz-$$; mkdir -p $T; trap 'rm -rf $T' EXIT
V=($O/*.vpk(N)); (( ${#V} == 1 )) || { echo "FAIL: not one VPK in $O"; exit 1; }
unzip -l $V[1] | grep -q ' managed-ilz/' || { echo "FAIL: $V[1] carries no managed-ilz/ (build with SHEPHERD_K12_VPKILZ=1) -- NOT COMPARED"; exit 1; }
PACK=${SHEPHERD_WORK:-$HOME/shepherd-work}/packs/mono.linux-arm.8.0.31/runtimes/linux-arm
ALLOW=SceLibKernel,SceThreadmgr,SceSysmem,SceIofilemgr,SceRtcUser,SceProcessmgr,SceLibRng,SceCtrl,SceDisplayUser,SceThreadmgrCoredumpTime,SceNet,ScePower,SceGxm,SceGxmInternalForVsh,SceSharedFb,SceDisplay,SceShaccCg,SceDmacmgr,SceAppMgrUser,SceCommonDialog,SceModulemgr,taihen,SceTouch,SceVshBridge,SceVideodecUser,SceSysmodule,SceAudio,SceAudiodecUser
TID=$(grep -aoE 'SHPD[0-9]{5}' $W/obj/param.sfo | head -1)
run() { python3 "$1" --allow-livearea --vpk "$2" --sfo $W/obj/param.sfo --elf $W/obj/shepherd_menu.elf --map $W/obj/shepherd_menu.map \
  --bcl $W/bcl --pack $PACK --engine $W/engine --unanswered $T/unanswered.txt --title-id $TID --allow-libs $ALLOW --owner-derived --require-derived \
  --allow-shacccg-ext --vpk-ilz > $T/out.txt 2>&1; }
cp $V[1] $T/stray.vpk; ( cd $T && echo "not part of the app" > stray-file.txt && zip -q stray.vpk stray-file.txt )
run $REPO/vita/shepherd/check_vpk.py $V[1]; r1=$?
run $REPO/vita/shepherd/check_vpk.py $T/stray.vpk; r2=$?; f2=$(grep -c '^FAIL' $T/out.txt); s2=$(grep '^FAIL' $T/out.txt | grep -c 'stray-file')
git -C $REPO show $OLD:vita/shepherd/check_vpk.py > $T/check_vpk_old.py 2>/dev/null
mkdir -p $T/shepherd; mv $T/check_vpk_old.py $T/shepherd/check_vpk.py; ln -s $REPO/vita/tools $T/tools
run $T/shepherd/check_vpk.py $T/stray.vpk; r3=$?
echo "(1) as built: exit $r1 (want 0)   (2) + a stray file: exit $r2 (want 1), $f2 FAIL line(s), the stray named: $s2   (3) $OLD's check_vpk.py on (2): exit $r3 (the bug: 0)"
(( r1 == 0 && r2 == 1 && s2 >= 1 )) || { echo "FAIL check-vpk-ilz"; exit 1; }
(( r3 == 0 )) && echo "PASS check-vpk-ilz (and the old script shows the bug: the test tells the fix from no fix)" || echo "PASS check-vpk-ilz (note: the old script did not show the bug here -- this run cannot tell the fix from no fix)"
