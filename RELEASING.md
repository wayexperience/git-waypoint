# Releasing Git Waypoint

One command:

```sh
./release.sh <version> "<changelog summary>"
# e.g.
./release.sh 0.1.12 "Fix file-descriptor leak in git lock polling"
```

`release.sh` does everything and keeps the versions in sync:

1. Bumps the three `src/.../package.json` (combined, api, ui) and the `FallbackVersion` in `ApplicationInfo.cs`.
2. Prepends an entry to `CHANGELOG.md`.
3. Commits the source.
4. Builds the unified package and pushes it to the `upm` branch (mirrors the `ui/Editor/` and `api/Api/` trees, bumps the package version, copies the changelog).

Then, to update a test project that installs `…git-waypoint.git#upm`: in Unity open **Package Manager → Git Waypoint → Refresh** (or restart). Do **not** hand-edit `packages-lock.json`.

## Notes / gotchas
- Commits use `commit.gpgsign=false` (1Password SSH signing fails in non-interactive shells).
- The real runtime version comes from `package.json` via `PackageInfo.FindForAssembly`; `FallbackVersion` is only a last resort (e.g. if the Package Manager isn't ready yet), but `release.sh` bumps it anyway so it's never stale.
- The unified `upm` package is a flattened build artifact; `release.sh` mirrors only the source trees, so for code-only releases it matches what the full packaging pipeline would produce.
