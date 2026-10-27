#!/bin/sh
# SHEPHERD-VITA: turn a fresh dotnet/runtime v8.0.31 checkout into the shepherd-vita fork (no network access).
# Usage: setup-fork.sh <runtime-checkout>     e.g. setup-fork.sh ${SHEPHERD_WORK:-$HOME/shepherd-work}/runtime
#
# The patch is applied with --intent-to-add, so the fork's NEW files (mono-*-vita.c, mono-vita.h, mini-vita.c) are
# known to git right away: `git diff` and `git diff v8.0.31` show them, and a regenerated patch cannot drop them.
# Regenerating the patch after changing the fork (run inside the checkout):
#   git status --porcelain | grep '^??'          # must print NOTHING: an untracked file would be missing from the patch
#                                               # (if it does: git add -N <file>, or delete it if it is not part of the fork)
#   git diff v8.0.31 > ${SHEPHERD_REPO:-$HOME/src/shepherd}/vita/patches/dotnet-runtime-v8.0.31-shepherd-vita.patch
#   # then prove it applies to a clean tree, e.g. in a scratch worktree:
#   git worktree add --detach ${SHEPHERD_WORK:-$HOME/shepherd-work}/tmp/patchcheck v8.0.31
#   git -C ${SHEPHERD_WORK:-$HOME/shepherd-work}/tmp/patchcheck apply --check ${SHEPHERD_REPO:-$HOME/src/shepherd}/vita/patches/dotnet-runtime-v8.0.31-shepherd-vita.patch
#   git worktree remove ${SHEPHERD_WORK:-$HOME/shepherd-work}/tmp/patchcheck
set -e
HERE=$(cd "$(dirname "$0")" && pwd)
R=${1:?usage: setup-fork.sh <dotnet/runtime v8.0.31 checkout>}
PATCH="$HERE/../patches/dotnet-runtime-v8.0.31-shepherd-vita.patch"
cd "$R"
[ "$(git rev-parse HEAD)" = "1219a42122cf5190c7f512850557e38c422430dc" ] || { echo "not v8.0.31 (1219a421)"; exit 1; }
git apply --check "$PATCH"
git apply --intent-to-add "$PATCH"
# every file the patch creates must now be tracked (intent-to-add), none left as '??'
if git status --porcelain | grep '^??'; then echo "untracked files after applying the patch (see above)"; exit 1; fi
mkdir -p artifacts/obj
cp "$HERE/version-files/_version.c" "$HERE/version-files/runtime_version.h" artifacts/obj/
echo "fork ready: $R (now run gen-offsets after configure-vita, then build-cross.sh and build-runtime-vita.sh)"
