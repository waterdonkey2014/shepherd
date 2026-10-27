#!/bin/zsh
# vita/llvm/check-derived-identity.sh (kit #5 llvm review): every derived-AOT app of one kit loads the SAME card file
# ux0:data/shepherd/managed/Shepherd.Engine.Derived.dll, and each eboot's derived image is bound to the exact DLL it was compiled from
# (its MVID, checked at load). So the Derived DLL each build wrote to <out>/owner-managed/ must be byte-identical across ALL of them:
# 00099 / 00098 / 00097 / 00095 (the default derived-AOT apps) and 00191 / 00192 (the LLVM A/B apps). Run it before staging a kit.
# usage: check-derived-identity.sh <build out dir> ...     exit 0 = all identical (one line per build), 1 = a mismatch or a missing DLL
set -u
(( $# >= 2 )) || { echo "usage: check-derived-identity.sh <out dir> <out dir> ... (every derived-AOT app of the kit)"; exit 2; }
ref=; rc=0
for o in "$@"; do
  f=$o/owner-managed/Shepherd.Engine.Derived.dll
  if [[ ! -f $f ]]; then echo "MISSING  $f"; rc=1; continue; fi
  h=$(shasum -a 256 "$f" | cut -c1-16)
  id=$(grep -ho 'SHPD[0-9]*' "$o/build-info.txt" 2>/dev/null | head -1)
  [[ -z $ref ]] && ref=$h
  if [[ $h == "$ref" ]]; then echo "same     $h ${id:-?} $o"; else echo "DIFFERS  $h ${id:-?} $o (first: $ref)"; rc=1; fi
done
(( rc == 0 )) && echo "PASS: one Shepherd.Engine.Derived.dll for all $# builds" || echo "FAIL: the card can hold only one Derived DLL; rebuild these apps from one clean clone + commit"
exit $rc
