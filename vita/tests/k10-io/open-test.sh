#!/bin/zsh
# device kit #10, track io (vita/notes/kit10-io.md "Review fixes"): the chunked pack's open-time checks and read-time checks, as the engine
# runs them (tools/VitaPack --open / --read-all use Shepherd.Assets' PackReader exactly as K10Io opens it):
#   * a matched shepherd.pk2 + shepherd.pack opens the chunked pack (made-from: header + sampled chunks; --made-from: every chunk);
#   * a shepherd.pack with the same TOC and size but other bytes (a re-bake without a new pk2): a changed SAMPLED chunk is refused at open,
#     a changed unsampled chunk is not (the sampled check's limit for packs baked before kit #10) -- --made-from (stage-owner.sh) refuses both;
#   * a shepherd.pk2 cut short (an interrupted push) is refused at open (the engine reads the version-1 file), not mid-session;
#   * a damaged LZ4 literal (decodes to the right length, wrong bytes): hash checks "all" end in an exception naming the chunk, "off" (the
#     first build) gives wrong bytes, the default (background) ends in the exception too when the worker decoded it first;
#   * a sequential walk after one read near the tail: the decode-ahead worker follows (runs on) vs stays at the tail (runs off, the first build).
#   open-test.sh <dir: matched shepherd.pack + shepherd.pk2 (+ shepherd.pack.xxh)>   (copies go to $K10_TEST_OUT; owner-local data)
setopt pipe_fail
T="${0:A:h}"; REPO="${T:h:h:h}"; O=${K10_TEST_OUT:-/tmp/k10-io-test}/open; rm -rf $O; mkdir -p $O
D=${1:A}; [[ -f $D/shepherd.pk2 && -f $D/shepherd.pack ]] || { echo "usage: open-test.sh <dir with shepherd.pack + shepherd.pk2>"; exit 2; }
export DOTNET_ROOT=$HOME/.dotnet PATH="$HOME/.dotnet:$PATH"
V=(dotnet $REPO/tools/VitaPack/bin/VitaPack.dll)
fail=0
want() { local w=$1; shift; local out; out=$("$@" 2>&1); local rc=$?; print -r -- "$out" | sed "s|$O/||g" | cut -c1-240
  case $w in ok) (( rc == 0 )) || { echo "  ^ expected exit 0"; fail=1; } ;; bad) (( rc != 0 )) || { echo "  ^ expected a refusal"; fail=1; } ;; esac; }
grep1() { [[ $1 == *$2* ]] || { echo "  ^ expected '$2'"; fail=1; }; }
flip() {   # flip one byte of the version-1 file in the middle of chunk <i> (its logical range from the pk2's chunk table)
python3 - "$1" "$D/shepherd.pk2" "$2" <<'PY'
import sys, struct
v1, pk2, ci = sys.argv[1], sys.argv[2], int(sys.argv[3])
h = open(pk2, 'rb').read(128); t, = struct.unpack_from('<Q', h, 72); payoff, = struct.unpack_from('<Q', h, 32)
e = open(pk2, 'rb'); e.seek(t + ci * 40); lo, ll = struct.unpack_from('<QI', e.read(12), 0)
f = open(v1, 'r+b'); at = payoff + lo + ll // 2; f.seek(at); x = f.read(1); f.seek(at); f.write(bytes([x[0] ^ 1])); f.close()
PY
}
echo "== matched pair"; want ok $V --open $D; want ok $V --made-from $D/shepherd.pk2 $D/shepherd.pack
mkdir -p $O/same; ln -s $D/shepherd.pk2 $O/same/shepherd.pk2; cp $D/shepherd.pack $O/same/shepherd.pack
echo "== same TOC and size, chunk 0 changed (sampled)"; flip $O/same/shepherd.pack 0; want bad $V --open $O/same; want bad $V --made-from $O/same/shepherd.pk2 $O/same/shepherd.pack
flip $O/same/shepherd.pack 0; flip $O/same/shepherd.pack 100
echo "== same TOC and size, chunk 100 changed (not sampled: opens -- stage-owner's --made-from refuses it)"; want ok $V --open $O/same; want bad $V --made-from $O/same/shepherd.pk2 $O/same/shepherd.pack
rm -rf $O/same
mkdir -p $O/short; ln -s $D/shepherd.pack $O/short/shepherd.pack
head -c $(( $(stat -L -f %z $D/shepherd.pk2) - 70000 )) $D/shepherd.pk2 > $O/short/shepherd.pk2
echo "== shepherd.pk2 cut short"; out=$($V --open $O/short 2>&1); print -r -- "$out" | sed "s|$O/||g" | cut -c1-240; grep1 "$out" "past the file's end"
rm -rf $O/short
mkdir -p $O/lit; cp $D/shepherd.pk2 $O/lit/shepherd.pk2; ln -s $D/shepherd.pack $O/lit/shepherd.pack
python3 - $O/lit/shepherd.pk2 <<'PY'
import sys, struct
p = sys.argv[1]; b = bytearray(open(p, 'rb').read())
n, = struct.unpack_from('<I', b, 64); t, = struct.unpack_from('<Q', b, 72)
for ci in range(100, n):
    lo, ll, sl, at = struct.unpack_from('<QIIQ', b, t + ci * 40); codec = b[t + ci * 40 + 24]; fl, = struct.unpack_from('<H', b, t + ci * 40 + 26)
    if codec == 1 and 2 <= (b[at] >> 4) < 15 and not (fl & 2):
        b[at + 1] ^= 1; open(p, 'wb').write(b); print("   (the first literal byte of LZ4 chunk %d flipped)" % ci); break
PY
echo "== a damaged LZ4 literal: hash checks all / off (the first build) / background"
out=$($V --read-all $O/lit --hash 2 --compare 2>&1); print -r -- "$out" | sed "s|$O/||g" | cut -c1-240; grep1 "$out" "differ from its hash"
out=$($V --read-all $O/lit --hash 0 --compare 2>&1); print -r -- "$out" | sed "s|$O/||g" | cut -c1-240; [[ $out == *" 0 differ"* ]] && { echo "  ^ expected wrong bytes with the checks off"; fail=1; }
want any $V --read-all $O/lit --hash 1 --compare
rm -rf $O/lit
echo "== a sequential walk after one read near the tail: the worker follows (runs on) / stays (runs off, the first build); no jump for scale"
for a in "--runs 1 --jump" "--runs 0 --jump" "--runs 1"; do want ok $V --read-all $D ${=a} --compare; done
rm -rf $O
exit $fail
