#!/bin/zsh
# device kit #10, track io: Verify files (Shepherd.Assets PackVerify) must say OK for a good pack and name the damage in a damaged copy:
# a flipped byte in a stored chunk, a flipped byte in the TOC, a truncated file; the version-1 pack with and without its shepherd.pack.xxh.
#   verify-test.sh <dir with shepherd.pk2 + shepherd.pack.xxh> <version-1 shepherd.pack>     (copies go to $K10_TEST_OUT; owner-local data)
setopt pipe_fail
T="${0:A:h}"; REPO="${T:h:h:h}"; O=${K10_TEST_OUT:-/tmp/k10-io-test}/verify; rm -rf $O; mkdir -p $O
D=${1:?pk2 dir}; V1=${2:?v1 pack}
V=(dotnet $REPO/tools/VitaPack/bin/VitaPack.dll --verify)
export DOTNET_ROOT=$HOME/.dotnet PATH="$HOME/.dotnet:$PATH"
fail=0
want() { local w=$1; shift; local out; out=$("$@" 2>&1); local rc=$?; print -r -- "$out" | cut -c1-260
  if [[ $w == ok ]]; then (( rc == 0 )) || { echo "  ^ expected OK"; fail=1; }; else (( rc != 0 )) || { echo "  ^ expected a FAILED verdict"; fail=1; }; fi; }
want ok $V $D/shepherd.pk2 deep
cp -c $D/shepherd.pk2 $O/flip.pk2 2>/dev/null || cp $D/shepherd.pk2 $O/flip.pk2
python3 - $O/flip.pk2 <<'PY'
import sys, struct
p = sys.argv[1]; b = bytearray(open(p, 'rb').read())
n, = struct.unpack_from('<I', b, 64); t, = struct.unpack_from('<Q', b, 72)
# the 100th chunk's first stored byte
at, = struct.unpack_from('<Q', b, t + 100 * 40 + 16); b[at] ^= 0x40
open(p, 'wb').write(b)
PY
want bad $V $O/flip.pk2
cp $D/shepherd.pk2 $O/toc.pk2
python3 - $O/toc.pk2 <<'PY'
import sys, struct
p = sys.argv[1]; b = bytearray(open(p, 'rb').read()); at, = struct.unpack_from('<Q', b, 88); b[at + 5] ^= 1; open(p, 'wb').write(b)
PY
want bad $V $O/toc.pk2
head -c $(( $(stat -f %z $D/shepherd.pk2) - 70000 )) $D/shepherd.pk2 > $O/short.pk2
want bad $V $O/short.pk2
cp $V1 $O/shepherd.pack; want ok $V $O/shepherd.pack
cp $D/shepherd.pack.xxh $O/; want ok $V $O/shepherd.pack
python3 - $O/shepherd.pack <<'PY'
import sys
p = sys.argv[1]; f = open(p, 'r+b'); f.seek(5 << 20); x = f.read(1); f.seek(5 << 20); f.write(bytes([x[0] ^ 1])); f.close()
PY
want bad $V $O/shepherd.pack
rm -rf $O
exit $fail
