#!/bin/bash
# Linux checks: everything in this repository that builds and runs WITHOUT a Mac, a PS Vita or a copy of a game.
# The CI workflow (.github/workflows/linux-checks.yml) runs one section per step; you can run the same thing locally:
#   .github/ci/linux-checks.sh [all | leaks | python | receiver | shell | c-units | physics | audio | dotnet] ...
# Needs: bash, git, python3, gcc/g++ (with ASan/UBSan), cmake (+ ninja if present), zsh (shell parse), the .NET 8 SDK.
# Writes build products only under $CI_OUT (default: a fresh temp dir) and into the git-ignored bin/ and obj/ folders of the
# C# projects; it changes no tracked file. Exit status 0 = every section that ran passed.
#
# What is NOT run here, and why (each section repeats this where it applies):
#   - anything that needs the user's game: the engine itself (engine/Shepherd.Engine and vita/tests/TestHost reference the
#     game's UnityEngine.*.dll / FMODUnity.dll), tools/FmodBake (FMODUnity.dll), every boot / fixture / oracle / physcheck run;
#   - anything that needs vitasdk or the forked Mono runtime: the VPK builds, vita/runtime/tests/run-tests.sh's runtime parts
#     (test_arena, test_yield, test_gsharedvt_arm need a dotnet/runtime v8.0.31 fork checkout + cross build), the LLVM / JIT work;
#   - anything macOS-only: CommonCrypto (test_crypto), afconvert / AudioToolbox (running sa_test), OpenGL on macOS (gltest),
#     OrbStack / QEMU user mode (vlx), Apple's linker flags (the physics shared library's link map);
#   - timing-based checks that assume one Mac (vita/devlog/mac/test.sh's end-to-end part);
#   - tools/VitaBudget: known broken (CHANGELOG 0.4.0: written against an older Shepherd.Assets API).
set -euo pipefail

REPO=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
cd "$REPO"
CI_OUT=${CI_OUT:-$(mktemp -d "${TMPDIR:-/tmp}/shepherd-ci.XXXXXX")}
mkdir -p "$CI_OUT"
export PYTHONDONTWRITEBYTECODE=1
CC=${CC:-gcc}
CXX=${CXX:-g++}

say() { printf '\n== %s\n' "$*"; }
run() { echo "+ $*"; "$@"; }

# --- the repo's own leak / hygiene checks -------------------------------------------------------------------------------------
leaks() {
  say "derived-code leak check (vita/shepherd/derived_leaks.py)"
  # It compares the owner-local, git-ignored engine/**/Derived/ files with everything git carries. A fresh checkout has no
  # Derived/ folder, so here it reports VACUOUS and passes; the real check runs on the owner's machine (build-derived.sh,
  # check_vpk.py) and must pass with --require before a pull request (CONTRIBUTING.md rule 2).
  run python3 vita/shepherd/derived_leaks.py

  say "no tracked file matches .gitignore (game data, build outputs, Derived/ must never be committed)"
  local bad
  bad=$(git ls-files -ci --exclude-standard)
  if [ -n "$bad" ]; then echo "tracked files that .gitignore says must never be committed:"; echo "$bad"; return 1; fi
  echo "ok: none"
}

# --- Python: every file compiles --------------------------------------------------------------------------------------------
python_compile() {
  say "every tracked Python file compiles (no bytecode written)"
  git ls-files -z '*.py' | python3 -c '
import sys
files = [f for f in sys.stdin.read().split("\0") if f]
bad = 0
for f in files:
    try:
        compile(open(f, encoding="utf-8").read(), f, "exec")
    except Exception as e:
        bad += 1
        print(f"FAIL {f}: {e}")
print(f"{len(files)} Python files, {bad} failed to compile")
sys.exit(1 if bad else 0)'
}

# --- Python: the devlog receiver's unit tests (standard library only, binds to 127.0.0.1) -----------------------------------
receiver() {
  say "devlog receiver unit tests (tools/devlog-receiver/test_receiver.py)"
  (cd tools/devlog-receiver && run python3 -m unittest -v test_receiver)
}

# --- shell scripts parse with the shell their #! line names ------------------------------------------------------------------
shell_parse() {
  say "every tracked shell script parses (bash -n / zsh -n / sh -n, by its #! line)"
  command -v zsh > /dev/null || { echo "zsh is needed (most scripts here are zsh): install it, e.g. apt-get install zsh"; return 1; }
  local n=0 bad=0 f first sh
  while IFS= read -r -d '' f; do
    [ -f "$f" ] || continue
    IFS= read -r first < "$f" || true
    case "$first" in
      '#!/bin/zsh'*|'#!/usr/bin/env zsh'*) sh=zsh ;;
      '#!/bin/bash'*|'#!/usr/bin/env bash'*) sh=bash ;;
      '#!/bin/sh'*|'#!/usr/bin/env sh'*) sh=sh ;;
      *) continue ;;
    esac
    n=$((n + 1))
    if ! "$sh" -n "$f"; then bad=$((bad + 1)); echo "FAIL ($sh -n) $f"; fi
  done < <(git ls-files -z)
  echo "$n shell scripts, $bad failed to parse"
  [ "$bad" = 0 ]
}

# --- C unit tests of host-side code (each file's header gives the same build line) ---------------------------------------------
c_units() {
  local O="$CI_OUT/c-units"; mkdir -p "$O"
  local M=vita/shepherd/mac-check

  say "Vita host helpers (vita/shepherd/mac-check/test_*.c)"
  run $CC -O2 -Wall -o "$O/test_pad_latch" $M/test_pad_latch.c
  run "$O/test_pad_latch"
  run $CC -O2 -Wall -o "$O/test_touch_latch" $M/test_touch_latch.c
  run "$O/test_touch_latch"
  run $CC -O2 -Wall -I vita/shepherd -o "$O/test_splash_rows" $M/test_splash_rows.c
  run "$O/test_splash_rows"
  run $CC -O2 -Wall -I vita/shepherd -o "$O/test_shv" $M/test_shv.c
  run "$O/test_shv"   # synthetic clip (no argv[1])
  run $CC -O2 -Wall -I vita/gltest/shepgl -o "$O/test_gxp_blob" $M/test_gxp_blob.c
  run "$O/test_gxp_blob"
  run $CC -O2 -Wall -pthread -o "$O/test_audio_pool" $M/test_audio_pool.c vita/shepherd/audio_pool.c
  run "$O/test_audio_pool"

  say "System.Native shim's pure blocks (vita/runtime/tests: paths, redirect, read-ahead)"
  # Exactly as vita/runtime/tests/run-tests.sh does: cut the marked block out of pal_vita.c and test it alone.
  # Not run: test_arena / test_yield / test_gsharedvt_arm (need the dotnet/runtime fork checkout + its cross build) and
  # test_crypto (differential against macOS CommonCrypto).
  local spec name marker inc
  for spec in "paths:pure path helpers:path_helpers" "redirect:pure redirect:redirect" "readahead:pure read-ahead:readahead"; do
    IFS=: read -r name marker inc <<< "$spec"
    awk "/BEGIN $marker/{p=1} p{print} /END $marker/{exit}" vita/runtime/system-native/pal_vita.c > "$O/$inc.inc"
    grep -q "END $marker" "$O/$inc.inc" || { echo "'$marker' markers missing in pal_vita.c"; return 1; }
    run $CC -O1 -g -Wall -Wno-unused-function -I"$O" -o "$O/test_$name" vita/runtime/tests/test_$name.c
    run "$O/test_$name"
  done

  say "devlog client self-test under ASan + UBSan (vita/devlog/mac/selftest.c, devlog_cli.c)"
  # Section 0 + 1 of vita/devlog/mac/test.sh. Its later sections (client against the live receiver) are not run: they time
  # operations against limits measured on one Mac and read BSD stat output.
  local DF=(-std=gnu11 -O1 -g -Wall -Wextra -Werror -Wno-unused-parameter -fsanitize=address,undefined)
  run $CC "${DF[@]}" vita/devlog/mac/selftest.c -o "$O/devlog_selftest"
  run $CC "${DF[@]}" vita/devlog/mac/devlog_cli.c -o "$O/devlog_cli"
  ASAN_OPTIONS=detect_leaks=0 "$O/devlog_selftest" > "$O/devlog_selftest.txt" 2>&1 || { cat "$O/devlog_selftest.txt"; return 1; }
  tail -1 "$O/devlog_selftest.txt"
  grep -q ' 0 failed' "$O/devlog_selftest.txt"
}

# --- physics: Box2D 2.3.1 + the Unity layer, against the recorded goldens ----------------------------------------------------
physics() {
  local B="$CI_OUT/physics"
  say "libshepphys (vita/physics): static library + t1_runner"
  local gen=(); command -v ninja > /dev/null && gen=(-G Ninja)
  run cmake -S vita/physics -B "$B" "${gen[@]}" -DCMAKE_BUILD_TYPE=Release -DCMAKE_C_COMPILER="$CC" -DCMAKE_CXX_COMPILER="$CXX" > "$B.cmake.log" 2>&1 \
    || { cat "$B.cmake.log"; return 1; }
  # Only the static library target: the shared one (and the t1_runner target linked to it) passes Apple's -Wl,-map, which
  # GNU ld does not understand. t1_runner is linked here against the static library instead -- same objects, same code.
  run cmake --build "$B" --target shepphys_s
  run $CC -O2 -Wall -Wextra -Wno-unused-parameter -I vita/physics/shim -I vita/physics/tests vita/physics/tests/t1_runner.c \
    "$B/libshepphys.a" -lstdc++ -lm -pthread -o "$B/t1_runner"

  say "physics checks (as vita/physics/build.sh mac / san): T1 goldens, self-tests S0-S7, trace + replay, worker pool"
  (
    cd vita/physics
    "$B/t1_runner" info | tail -3
    "$B/t1_runner" check tests/golden "$B/t1" | tee "$B/t1.txt" | tail -1
    grep -q "8/8 match the golden files, 8/8" "$B/t1.txt" || { echo "T1 FAILED"; cat "$B/t1.txt"; exit 1; }
    "$B/t1_runner" check tests/golden "$B/t1w2" --workers 2 --jcheck > "$B/t1w2.txt" 2>&1 || { cat "$B/t1w2.txt"; exit 1; }
    grep -q "8/8 match the golden files, 8/8" "$B/t1w2.txt" || { echo "T1 with 2 workers FAILED"; cat "$B/t1w2.txt"; exit 1; }
    echo "T1 with 2 workers: 8/8"
    for k in s0 s1 s2 s3 s6 s7; do
      "$B/t1_runner" $k > "$B/$k.txt" 2>&1 || { cat "$B/$k.txt"; exit 1; }
      tail -1 "$B/$k.txt"
      grep -q "RESULT PASS" "$B/$k.txt" || { echo "$k FAILED"; exit 1; }
    done
    "$B/t1_runner" trace "$B/trace" | tail -1
  )
  # Not run: the Vita archive, QEMU's Cortex-A9 replay (need vitasdk / qemu-system-arm), the engine's physcheck (needs the game).
}

# --- audio engine: the portable C library and its tools build ---------------------------------------------------------------
audio() {
  local O="$CI_OUT/audio"; mkdir -p "$O/obj"
  say "shepaudio (vita/audio): portable library + sa_test / sa_render / sa_check (build only)"
  # The portable platform layer (sa_plat_posix.c) and no platform AAC decoder (sa_aac_none.c) instead of the macOS / Vita ones.
  # -D_GNU_SOURCE: glibc hides some POSIX calls under -std=c99 (macOS does not).
  # Not run: sa_test makes its reference files with macOS afconvert and checks AAC against Apple's decoder.
  local CF=(-std=c99 -D_GNU_SOURCE -O2 -g -Wall -Wextra -Wno-unused-parameter -I vita/audio/include -I vita/audio/src)
  local objs=() f
  for f in sa_json sa_codec sa_manifest sa_cache sa_stream sa_mixer sa_system sa_out_file sa_plat_posix sa_aac_none sa_out_rt; do
    run $CC "${CF[@]}" -c vita/audio/src/$f.c -o "$O/obj/$f.o"; objs+=("$O/obj/$f.o")
  done
  rm -f "$O/libshepaudio.a"; ar rcs "$O/libshepaudio.a" "${objs[@]}"
  for f in sa_test sa_render sa_check; do
    run $CC "${CF[@]}" vita/audio/tools/$f.c "$O/libshepaudio.a" -pthread -lm -o "$O/$f"
  done
}

# --- C#: every project that needs no game DLLs builds; the managed unit test runs ---------------------------------------------
# Projects that cannot build here, with the reason. Every other tracked .csproj is built, so a new project is covered at once.
DOTNET_SKIP=(
  "engine/Shepherd.Engine/Shepherd.Engine.csproj"     # references the game's UnityEngine.*.dll / FMODUnity.dll (runtime/Managed, SHEPHERD_GAME_DATA)
  "vita/tests/TestHost/Shepherd.TestHost.csproj"      # references Shepherd.Engine and the game's UnityEngine.*.dll
  "tools/FmodBake/FmodBake.csproj"                    # references the game's FMODUnity.dll
  "tools/VitaBudget/VitaBudget.csproj"                # known broken (CHANGELOG 0.4.0): needs a copied lib/Shepherd.Assets.dll of an older API
)
dotnet_build() {
  say "C# projects (dotnet build -c Release)"
  command -v dotnet > /dev/null || { echo "the .NET 8 SDK (dotnet) is needed"; return 1; }
  local projects=() p s skip
  while IFS= read -r -d '' p; do
    skip=0; for s in "${DOTNET_SKIP[@]}"; do [ "$p" = "$s" ] && skip=1; done
    if [ $skip = 1 ]; then echo "skipped (needs game files or known broken): $p"; else projects+=("$p"); fi
  done < <(git ls-files -z '*.csproj')
  # One `dotnet build` per project (a single solution is refused: two projects are named Hello.csproj). The build server stays
  # up between them, so each one after the first takes a second or two.
  echo "building ${#projects[@]} projects"
  for p in "${projects[@]}"; do
    run dotnet build "$p" -c Release -nologo -v q -clp:ErrorsOnly
  done

  say "managed unit test: the learning cache's store (tools/K10CacheTest, engine Runtime/K10Store.cs linked as source)"
  run dotnet tools/K10CacheTest/bin/Release/net8.0/K10CacheTest.dll | tail -3   # exit 0 only when every case passes
}

main() {
  local sections=("$@"); [ ${#sections[@]} = 0 ] && sections=(all)
  [ "${sections[0]}" = all ] && sections=(leaks python receiver shell c-units physics audio dotnet)
  echo "Shepherd Linux checks: ${sections[*]} (build products in $CI_OUT)"
  local s
  for s in "${sections[@]}"; do
    case "$s" in
      leaks) leaks ;;
      python) python_compile ;;
      receiver) receiver ;;
      shell) shell_parse ;;
      c-units) c_units ;;
      physics) physics ;;
      audio) audio ;;
      dotnet) dotnet_build ;;
      *) echo "unknown section: $s (all | leaks | python | receiver | shell | c-units | physics | audio | dotnet)"; exit 2 ;;
    esac
  done
  say "passed: ${sections[*]}"
}

main "$@"
