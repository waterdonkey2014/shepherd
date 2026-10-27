#!/bin/zsh
# game-aot HOT METHODS: run the analysis (hotan.py per scenario / segment, hotunion.py over all of them) on the rounds of hot.sh:
#   rounds r1..r<N>-<scenario>-{plain,prof} (interleaved) and n1..n2-<scenario>-profni.  Output: $G/profiles/ (owner-local).
setopt pipe_fail null_glob
G=${SHEPHERD_WORK:-$HOME/shepherd-work}/gameaot; H=$G/hot; D=${0:A:h}; O=${1:-$G/profiles}; R=$H/runs
export DOTNET_ROOT=$HOME/.dotnet PATH="$HOME/.dotnet:$PATH" TMPDIR=${SHEPHERD_WORK:-$HOME/shepherd-work}/tmp PYTHONDONTWRITEBYTECODE=1
mkdir -p $O
# whole-assembly IL sizes of everything the intro run loads (vita/v6/tools/AsmSize, metadata only)
[[ -f $O/asmsize.tsv ]] || dotnet ${SHEPHERD_WORK:-$HOME/shepherd-work}/v6/asmsize/AsmSize.dll sizes $R/r1-intro-plain/assemblies.txt $O/asmsize.tsv | tail -5
for sc in idle tour; do
  F=$([[ $sc == idle ]] && echo 100 || echo 1300)
  python3 $D/hotan.py $O $sc --frames $F --prof $R/r<->-$sc-prof --profni $R/n<->-$sc-profni --plain $R/r<->-$sc-plain --asmsize $O/asmsize.tsv || exit 1
done
python3 $D/hotan.py $O intro --frames 805,980,1149,2066 --segnames 0-boot,1-menu,2-quote,3-gameplay \
  --prof $R/r<->-intro-prof --profni $R/n<->-intro-profni --plain $R/r<->-intro-plain --asmsize $O/asmsize.tsv || exit 1
python3 $D/hotan.py $O intro-all --frames 5000 --merge \
  --prof $R/r<->-intro-prof --profni $R/n<->-intro-profni --plain $R/r<->-intro-plain --asmsize $O/asmsize.tsv || exit 1
python3 $D/hotunion.py $O/union $O/idle $O/tour $O/intro/0-boot $O/intro/1-menu $O/intro/2-quote $O/intro/3-gameplay > /dev/null || exit 1
python3 $D/hotunion.py $O/union-menu $O/idle $O/tour > /dev/null || exit 1
python3 $D/hotsum.py $O > $O/overview.md || exit 1
python3 $D/hotasm.py $O $R/r1-idle-prof $R/r1-intro-prof > /dev/null || exit 1
echo "analysis -> $O"
