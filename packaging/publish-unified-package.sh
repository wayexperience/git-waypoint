#!/usr/bin/env bash
# Publish the assembled unified package (build/upm-unified/it.wayexperience.unity.git-waypoint) to the
# orphan branch `upm` so it can be installed from a single git URL:
#
#   https://github.com/wayexperience/git-waypoint.git#upm
#
# The package sits at the branch ROOT, so no ?path= is needed. Uses a throwaway git
# worktree so the main working tree (and any other agent editing it) is never touched.
# Force-pushes the branch: it is a generated snapshot, not hand-edited history.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PKG="$ROOT/build/upm-unified/it.wayexperience.unity.git-waypoint"
BRANCH="upm"
WT="$(mktemp -d)/upm-publish"

[ -d "$PKG" ] || { echo "Run make-unified-package.sh first ($PKG missing)"; exit 1; }

cleanup() { git -C "$ROOT" worktree remove --force "$WT" 2>/dev/null || true; rm -rf "$(dirname "$WT")"; }
trap cleanup EXIT

echo "==> Creating orphan worktree for branch '$BRANCH'"
git -C "$ROOT" worktree add --detach "$WT" >/dev/null
git -C "$WT" checkout --orphan "$BRANCH"
git -C "$WT" rm -rf . >/dev/null 2>&1 || true
find "$WT" -mindepth 1 -maxdepth 1 ! -name '.git' -exec rm -rf {} +

echo "==> Copying package to branch root"
cp -R "$PKG"/. "$WT"/

git -C "$WT" add -A
git -C "$WT" commit -q -m "Unified WAY Git UPM package (single git-URL install)"
echo "==> Pushing $BRANCH to origin (force)"
git -C "$WT" push -f origin "$BRANCH"

echo "==> Published. Install URL:"
echo "   https://github.com/wayexperience/git-waypoint.git#$BRANCH"
