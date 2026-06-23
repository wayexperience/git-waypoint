# Git Waypoint — Quick Guide

Git Waypoint brings Git into the Unity Editor, with Perforce-style Git LFS
auto-locking so artists and developers can work on the same project without
overwriting each other's binary assets. You don't need Git pre-installed — the
plugin installs a portable Git + Git LFS automatically on first run.

## Install

In Unity: **Window ▸ Package Manager ▸ + ▸ Add package from git URL…** and paste:

```
https://github.com/wayexperience/git-waypoint.git#upm
```

Open the window from **Window ▸ Git Waypoint**.

## First run

1. **Git installs itself.** On a machine with no Git, Git Waypoint downloads a
   portable Git + Git LFS the first time it runs. If you already have Git
   (e.g. via Homebrew) you can switch to it in **Settings ▸ Git installation ▸
   Find system git**.
2. **Set your identity.** A banner at the top asks for your name and email if
   they aren't set yet — these are stamped on every commit. Git actions stay
   disabled until both are filled in. (You can also edit them in the Settings tab.)
3. **Initialize the repository** if the project isn't under Git yet. This sets up
   `git init`, `git lfs install`, a `.gitignore`, and serializes meta files as text.

## Daily use

### Changes — save your work
The **Changes** tab lists everything you've modified. Tick the files to include
(a file's `.meta` follows it), write a short message, and click **Commit** (or
**Commit & Push** to also send it to the server). If a file you selected has
newer changes on the server, the commit is blocked until you update — pull first.

### Locks — avoid clashes on binary assets
Lockable files (scenes, prefabs, textures, …) are locked automatically as you
start editing them, so teammates can't change the same file at the same time.
The **Locks** tab shows every lock: green = yours, red = someone else's. Your
locks are released automatically when you push. You can also lock/unlock from the
right-click menu in the Project window.

### Sync — stay up to date
Use the toolbar:
- **Fetch** — check what's new on the server.
- **Pull** — bring server changes into your project.
- **Push** — send your commits to the server.

The header shows how far ahead/behind the server you are.

## Settings

- **Git user** — your name and email (required to commit).
- **Repository** — the remote URL, plus optional one-click setup for
  `.gitattributes` (LFS rules for Unity binaries) and Unity's smart merge tool.
- **Git installation** — paths to git and git-lfs, with **Find system git** and
  **Use bundled git**; the detected versions are shown next to each path.
- **Sync** — automatic fetch, and whether to block editing/committing files that
  are out of date.
- **Automatic locking** — enable auto-lock, lock-on-save / lock-on-open, which
  file types to lock, and whether to release your locks when Unity closes.

## Notes

- **Locks and push/pull use your SSH key.** Make sure your SSH agent (e.g. the
  1Password agent) is unlocked, otherwise these operations fail and lock polling
  pauses.
- **Logs** live in the plugin's cache folder; enable **trace logging** in
  Settings ▸ Advanced if you need to diagnose an issue.
