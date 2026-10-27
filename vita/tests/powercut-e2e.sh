#!/bin/zsh
# kit #7 tests track (vita/notes/kit7-tests.md "Power-cut tests"): the end-to-end half -- the real engine on the Mac (Vita profile, null
# backend) writes its card caches (the icall hook list cache/icalls, the catalog bake cache/json) in a first boot; then each cache file is
# damaged the ways a power cut can leave it (cut to half, cut to 1 byte, a flipped bit, the tail zeroed, the file gone with only a stray
# .tmp left, a .tmp next to a good file) and fed to a new boot. Required: every damaged start runs exactly the clean start's frames (state
# and render stream hashes equal on every frame) -- a damaged cache only ever costs a rebuild -- and logs the refusal. -> <out>/e2e.txt
setopt pipe_fail
T="${0:A:h}"; O=${1:-${SHEPHERD_WORK:-$HOME/shepherd-work}/kit7/tests-work/powercut/e2e}; FR=${FRAMES:-400}
rm -rf $O; mkdir -p $O
zsh $T/macboot.sh $O/clean $FR > /dev/null || { echo "clean boot failed"; exit 1; }
zsh $T/macboot.sh $O/warm $FR CACHE_IN=$O/clean/cache > /dev/null   # (a start with the good caches: the reference for "hit")
files=($(cd $O/clean/cache && find . -type f ! -name '*.tmp' | sort))
echo "caches written by the clean start: ${files[*]}" > $O/e2e.txt
v=PASS
for f in $files; do
  for dmg in half one flip zero gone straytmp; do
    d=$O/in-${f:t}-$dmg; rm -rf $d; cp -R $O/clean/cache $d; p=$d/$f; n=$(stat -f %z $p)
    case $dmg in
      half) python3 -c "import sys; p=sys.argv[1]; b=open(p,'rb').read(); open(p,'wb').write(b[:len(b)//2])" $p ;;
      one) python3 -c "import sys; p=sys.argv[1]; b=open(p,'rb').read(); open(p,'wb').write(b[:1])" $p ;;
      flip) python3 -c "import sys; p=sys.argv[1]; b=bytearray(open(p,'rb').read()); b[len(b)//3]^=4; open(p,'wb').write(b)" $p ;;
      zero) python3 -c "import sys; p=sys.argv[1]; b=bytearray(open(p,'rb').read()); k=len(b)*2//3; b[k:]=bytes(len(b)-k); open(p,'wb').write(b)" $p ;;
      gone) mv $p $p.tmp ;;
      straytmp) head -c 100 $p > $p.tmp ;;
    esac
    r=$O/run-${f:t}-$dmg
    zsh $T/macboot.sh $r $FR CACHE_IN=$d > /dev/null
    same=1; cmp -s $O/clean/state-hash.txt $r/state-hash.txt || same=0; cmp -s $O/clean/stream-hash.txt $r/stream-hash.txt || same=0
    note=$(grep -h "refused\|rewritten\|damaged\|scanned\|\[jsonbake\]" $r/engine.log | sed 's/^\[ *[0-9]*\] *//' | head -2 | cut -c1-160 | tr '\n' ' ')
    echo "$f $dmg ($n B): frames $( ((same)) && echo identical || echo DIFFER) to the clean start; $note" >> $O/e2e.txt
    (( same )) || v=FAIL
  done
done
same=1; cmp -s $O/clean/state-hash.txt $O/warm/state-hash.txt || same=0; cmp -s $O/clean/stream-hash.txt $O/warm/stream-hash.txt || same=0
echo "warm start (good caches): frames $( ((same)) && echo identical || echo DIFFER) to the clean start" >> $O/e2e.txt; (( same )) || v=FAIL
echo "RESULT: $v" >> $O/e2e.txt
cat $O/e2e.txt; [[ $v == PASS ]]
