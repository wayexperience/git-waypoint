#!/usr/bin/env bash
# One-command release for Git Waypoint.
#
#   ./release.sh <version> "<changelog summary>"
#   ./release.sh 0.1.12 "Fix file-descriptor leak in git lock polling"
#
# It bumps every version that must move, records a CHANGELOG entry, commits the source, then builds and
# pushes the unified package to the `upm` branch (what users install). No manual sed, no touching
# packages-lock.json. After it runs: in Unity, Package Manager -> Refresh to pull the new version.
#
# Versions kept in sync (this is the "what to update each release" list):
#   - src/it.wayexperience.unity.git-waypoint/package.json        (combined)
#   - src/it.wayexperience.unity.git-waypoint.api/package.json    (api)
#   - src/it.wayexperience.unity.git-waypoint.ui/package.json     (ui)
#   - src/.../Api/Application/ApplicationInfo.cs  FallbackVersion  (last-resort runtime version)
#   - CHANGELOG.md                                                (new entry)
#   - upm branch: package.json + the Editor/ and Api/ source trees + CHANGELOG.md

set -euo pipefail

VER="${1:-}"
NOTE="${2:-}"
if [ -z "$VER" ] || [ -z "$NOTE" ]; then
  echo "usage: ./release.sh <version> \"<changelog summary>\"" >&2
  exit 1
fi

ROOT="$(cd "$(dirname "$0")" && pwd)"
cd "$ROOT"
SED=(/usr/bin/sed -i '')

UIPKG="src/it.wayexperience.unity.git-waypoint.ui/package.json"
OLD=$(grep -m1 '"version"' "$UIPKG" | sed -E 's/.*"version": "([^"]+)".*/\1/')
if [ "$OLD" = "$VER" ]; then echo "version is already $VER" >&2; exit 1; fi
echo ">> $OLD -> $VER : $NOTE"

# 1) bump the three package.json + the FallbackVersion
for p in src/it.wayexperience.unity.git-waypoint src/it.wayexperience.unity.git-waypoint.api src/it.wayexperience.unity.git-waypoint.ui; do
  "${SED[@]}" "s/\"version\": \"$OLD\"/\"version\": \"$VER\"/" "$p/package.json"
done
"${SED[@]}" "s/FallbackVersion = \"[^\"]*\"/FallbackVersion = \"$VER\"/" \
  src/it.wayexperience.unity.git-waypoint.api/Api/Application/ApplicationInfo.cs

# 2) prepend a CHANGELOG entry under the header
DATE=$(date +%Y-%m-%d)
TMP=$(mktemp)
awk -v ver="$VER" -v date="$DATE" -v note="$NOTE" '
  NR==1 && !done { print; next }
  /^## \[/ && !done { print "## [" ver "] - " date "\n- " note "\n"; done=1 }
  { print }
  END { if (!done) print "\n## [" ver "] - " date "\n- " note }
' CHANGELOG.md > "$TMP" && mv "$TMP" CHANGELOG.md

# 3) commit the source
git add -A src CHANGELOG.md
git -c commit.gpgsign=false commit -q -m "$VER: $NOTE"
echo ">> committed source"

# 4) build + push the unified upm package
WT="$(mktemp -d)/upm"
git fetch origin upm -q
git worktree add -B upm "$WT" origin/upm -q
# The unified package is flat: ui Editor/ -> Editor/, api Api/ -> Api/. Mirror .cs AND .meta with --delete
# so upm is an EXACT mirror of the source - renames and deletions are handled, no stale/duplicate files
# left behind. The .meta GUIDs already match the published package (verified), so syncing them is a no-op
# for unchanged files and keeps references intact. Everything else (binaries, resources) is protected by
# --exclude='*' and never deleted; the .gitattributes resource is copied explicitly below.
# (The .csproj/.DotSettings are source-build files, not package content - excluded before the .meta rule.)
RS_FILTER=(--exclude='*.csproj' --exclude='*.csproj.meta' --exclude='*.csproj.DotSettings' --exclude='*.csproj.DotSettings.meta' --include='*/' --include='*.cs' --include='*.meta' --exclude='*')
rsync -rc --delete "${RS_FILTER[@]}" \
  src/it.wayexperience.unity.git-waypoint.ui/Editor/  "$WT/Editor/"
rsync -rc --delete "${RS_FILTER[@]}" \
  src/it.wayexperience.unity.git-waypoint.api/Api/    "$WT/Api/"
# The vendored task framework (process/task plumbing) ships in the package too - sync it with the same
# filter so fixes to ProcessWrapper/ProcessTask/ProcessManager actually reach installed users. Without this
# the framework in upm stays frozen at whatever was first published. --delete + --exclude='*' only prune
# stale .cs/.meta; the asmdef/package.json/Documentation~ in the package are protected and left intact.
rsync -rc --delete "${RS_FILTER[@]}" \
  src/it.wayexperience.unity.git-waypoint.api/com.unity.editor.tasks/  "$WT/com.unity.editor.tasks/"
# Also ship the .gitattributes resource (what "Set up .gitattributes" writes) - it's not a .cs.
cp src/it.wayexperience.unity.git-waypoint.api/Api/PlatformResources/gitattributes "$WT/Api/PlatformResources/gitattributes"
cp "src/it.wayexperience.unity.git-waypoint.ui/Editor/PlatformResources~/gitattributes" "$WT/Editor/PlatformResources~/gitattributes"
"${SED[@]}" "s/\"version\": \"$OLD\"/\"version\": \"$VER\"/" "$WT/package.json"
cp CHANGELOG.md "$WT/CHANGELOG.md"

echo ">> upm changes:"; git -C "$WT" status -s
git -C "$WT" add -A
git -C "$WT" -c commit.gpgsign=false commit -q -m "$VER: $NOTE"
git -C "$WT" push origin upm
HASH=$(git -C "$WT" rev-parse HEAD)
git worktree remove "$WT" --force
git worktree prune

echo ""
echo ">> Published $VER  (upm $HASH)"
echo ">> In Unity: Package Manager -> Git Waypoint -> Refresh to update test projects."
