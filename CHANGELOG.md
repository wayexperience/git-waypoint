# Changelog

All notable changes to Git Waypoint are listed here. Newest first.
Managed by `release.sh` — do not hand-edit the version headers.

## [0.1.20] - 2026-06-26
- Fix protezione .meta di file bloccati, identità lock per account/remote, badge Hierarchy sempre aggiornati, argomenti git senza quoting manuale, meno handle di processo trattenuti

## [0.1.19] - 2026-06-25
- Fix fd leak: disable git-lfs SSH multiplexing (lfs.ssh.automultiplex=false) so the failed pure-SSH attempt against servers like Forgejo no longer leaks orphaned ssh ControlMaster processes; remove diagnostic tracing

## [0.1.18] - 2026-06-25
- TEMP diagnostic build: verbose git/ssh tracing to pin the bundled-git lock-check hang

## [0.1.17] - 2026-06-25
- Internal rename to Git Waypoint: GitForUnityWindow/Theme -> GitWaypointWindow/Theme, folder and solution renamed (no behavior change)

## [0.1.16] - 2026-06-25
- Rename app folders to Git Waypoint: logs now in ~/Library/Logs/GitWaypoint/git-waypoint.log and cache in ~/Library/Application Support/GitWaypoint

## [0.1.15] - 2026-06-25
- Lockable files now come solely from .gitattributes (committed, team-shared); plugin no longer has a per-user lockable list; Set up .gitattributes writes the full recommended template

## [0.1.14] - 2026-06-25
- Lock poller no longer false-warns 'not responding' when locks are unchanged: a new LocksRefreshed event fires on every successful poll, not only on change

## [0.1.13] - 2026-06-25
- History: keep ref pills (HEAD/branch) whole and ellipsize the commit summary when space is tight

## [0.1.12] - 2026-06-25
- Fix file-descriptor leak: drain stderr and kill timed-out git children; stop re-running 'lfs locks --verify' once identity is confirmed

## [0.1.11] - 2026-06-25
- Runtime version is now read from the installed package.json (`PackageInfo.FindForAssembly`), so the reported version always matches what's running.

## [0.1.10] - 2026-06-25
- Single, server-derived lock identity (`git lfs locks --verify`): the file badges and the edit-block now use the same "who am I", so they can't disagree. Zero config — it's learned from the server.
- Added a read-only "Lock identity" field in Settings.

## [0.1.9] - 2026-06-25
- Lock identity is re-read live when locks change, instead of only at startup.

## [0.1.8] - 2026-06-24
- Project/Hierarchy overlays are now all square badges, including outdated (↓) and lock.
- "Outdated" banner in the Changes tab, status filter dropdown, blue LFS chip next to the file name, even row spacing (also for files in the repo root).
- Fixed the window tab icon disappearing after domain reloads.

## [0.1.7] - 2026-06-24
- Overlay badges are square, smaller and vertically centered.

## [0.1.6] - 2026-06-24
- Letter status badges (M/A/D/R) on the Project and Hierarchy file overlays, matching the Changes list.
