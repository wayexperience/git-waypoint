#!/usr/bin/env bash
# Assemble a single, self-contained UPM package (API + UI + native libs + icons)
# so a colleague can install everything from ONE git URL with no scoped registry
# and no per-dependency wiring.
#
#   ./packaging/make-unified-package.sh            # build into build/upm-unified/
#   VERSION=1.0.0-way.3 ./packaging/make-unified-package.sh
#
# Result: build/upm-unified/it.wayexperience.unity.git-waypoint/  — a package whose only dependency
# is Unity itself. Publish its contents to a branch (see publish-unified-package.sh)
# and install via Package Manager > Add package from git URL.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SRC="$ROOT/src"
OUT="$ROOT/build/upm-unified/it.wayexperience.unity.git-waypoint"
VERSION="${VERSION:-0.1.0}"

API="$SRC/it.wayexperience.unity.git-waypoint.api"
UI="$SRC/it.wayexperience.unity.git-waypoint.ui"

# Things that must never ship in a distributable package.
EXCLUDES=(--exclude='Tests' --exclude='Tests.meta'
          --exclude='obj' --exclude='bin'
          --exclude='*.csproj' --exclude='*.csproj.meta'
          --exclude='package.json' --exclude='package.json.meta'
          --exclude='.npmignore' --exclude='.vs')

echo "==> Cleaning $OUT"
rm -rf "$OUT"
mkdir -p "$OUT"

# API package: bring its content (code, native sfw libs, localization, platform resources)
# to the package root, keeping every .meta so GUIDs/asmdef references stay intact.
echo "==> Copying API"
rsync -a "${EXCLUDES[@]}" "$API"/ "$OUT"/

# UI package: only the folders unique to UI (Editor code + icons, Shim). Docs/licence
# come from API to avoid root-level filename clashes.
echo "==> Copying UI"
for item in Editor Editor.meta Shim Shim.meta; do
  [ -e "$UI/$item" ] && rsync -a "${EXCLUDES[@]}" "$UI/$item" "$OUT"/
done

# Single root manifest. No it.wayexperience.unity.git-waypoint.api dependency: the API code is now in
# the same package, referenced at the asmdef level, so there is nothing for UPM to resolve.
echo "==> Writing package.json (version $VERSION)"
cat > "$OUT/package.json" <<JSON
{
  "name": "it.wayexperience.unity.git-waypoint",
  "displayName": "Git Waypoint",
  "description": "Git Waypoint: Git for Unity with Perforce-style LFS auto-locking for artists and developers. Bundles the API, editor UI and native file watcher in a single package, and installs a portable Git + Git LFS automatically.",
  "version": "$VERSION",
  "unity": "2021.3",
  "license": "MIT",
  "author": { "name": "WAY", "email": "mt@16bit.it" },
  "repository": {
    "type": "git",
    "url": "https://github.com/wayexperience/git-waypoint.git"
  }
}
JSON

echo "==> Done."
echo "Package: $OUT"
echo "Assemblies present:"; find "$OUT" -name '*.asmdef' -maxdepth 4 | sed "s#$OUT/##"
echo "Native libs present:"; find "$OUT/sfw" -name '*.dll' -o -name '*.so' -o -name '*.bundle' 2>/dev/null | sed "s#$OUT/##" | head
