#!/bin/zsh
# device kit #10, track io (vita/notes/kit10-io.md): the device's LZ4 decoder (vita/shepherd/k10_io.inc.c) built for the Mac twice -- -O2 and
# under ASan + UBSan -- and run over every chunk of the chunked packs named (shepherd.pk2: owner-local derived data, only counts printed).
#   run.sh <pack.pk2 or dir> [...]          (exit 0 = every chunk decoded to its hash, every damaged chunk refused in bounds)
setopt pipe_fail
T="${0:A:h}"; O=${K10_TEST_OUT:-/tmp/k10-io-test}; mkdir -p $O
cc -O2 -Wall -Wno-unused-function -o $O/lz4check $T/lz4check.c || exit 1
cc -O1 -g -fsanitize=address,undefined -fno-omit-frame-pointer -Wall -Wno-unused-function -o $O/lz4check-asan $T/lz4check.c || exit 1
rc=0
for p in "$@"; do
  [[ -d $p ]] && p=$p/shepherd.pk2
  $O/lz4check $p 0 || rc=1
  ASAN_OPTIONS=detect_leaks=0 $O/lz4check-asan $p 3 || rc=1
done
exit $rc
