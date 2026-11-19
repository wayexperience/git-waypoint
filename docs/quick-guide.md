# Git Waypoint: quick guide

Git Waypoint brings Git into the Unity Editor, with Perforce-style Git LFS
auto-locking so artists and developers can work on the same project without
overwriting each other's binary assets.

## Install

In Unity: **Window ▸ Package Manager ▸ + ▸ Add package from git URL…** and paste:

```
https://github.com/wayexperience/git-waypoint.git#upm
```

Open the window from **Window ▸ Git Waypoint**.

## First run

1. **Git installs itself.** On a machine with no Git, Git Waypoint downloads a
   portable Git + Git LFS the first time it runs. To use your own Git instead
   (e.g. Homebrew), go to **Settings ▸ Find system git**.
2. **Set your identity.** If your Git name and email aren't set, a banner sends
   you to Settings. Git actions stay disabled until both are filled in.
3. **Initialize the repository** if the project isn't under Git yet.
4. **Recommended:** in Settings, **Set up .gitattributes** (LFS and lock rules for
   Unity binaries) and **Set up Unity merge** (smart merge for scenes and prefabs).

## Daily use

### Changes
Lists everything you've modified. Tick the files to include (a file's `.meta`
follows it), write a message, then **Commit** or **Commit & Push**. Committing a
file that has newer changes on the server is blocked until you pull.
**Discard all** throws away every local change (after a confirmation) and
releases your locks on those files.

### Locks
Files marked `lockable` in `.gitattributes` (scenes, prefabs, textures, …) are
locked automatically when you open or save them, so two people can't edit the
same file at once. Files locked by someone else can't be opened or saved.

The **Locks** tab lists every lock: green is yours, red is someone else's. Your
locks are released when you push. **Release all mine** releases every lock you
hold in one click. You can also lock and unlock from the right-click menu in the
Project window.

### History and Branches
**History** shows incoming commits (on the server, not pulled yet) above your
own. **Branches** lists local and remote branches with what's left to push and
pull. Checking out a remote branch creates a local branch that tracks it.

### Sync
**Fetch** checks for news (also done automatically), **Pull** brings server
changes in, **Push** sends your commits. The header shows how far ahead or
behind you are.

## Settings

- **Git user**: name and email, required to commit.
- **Repository**: remote URL, `.gitattributes` and Unity merge setup.
- **Git installation**: paths to git and git-lfs with their versions,
  **Find system git** and **Use bundled git**.
- **Sync**: automatic fetch and its interval; block editing or committing files
  that are out of date.
- **Locking**: automatic locking, lock on open, lock on save, release your locks
  when Unity closes, block files locked by others, a popup (or only a Console
  warning) when a locked file isn't saved, and **Only Git Waypoint makes files
  read-only**, which keeps read-only only the files someone else has actually locked.
- **Advanced**: timeouts and trace logging.

## Troubleshooting

- **Locks, fetch and push use your SSH key.** If your key lives in an agent such
  as 1Password, keep it unlocked, otherwise these operations fail.
- **Logs**: `~/Library/Logs/GitWaypoint/git-waypoint.log` on macOS. Turn on
  **trace logging** in Settings for more detail.
