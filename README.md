# Git Waypoint

Git Waypoint is a Git client for the Unity Editor with **Perforce-style Git LFS auto-locking** — built so artists *and* developers can work on the same project without stepping on each other's binary assets. It installs a portable Git + Git LFS automatically, so users don't need Git pre-installed.

It is a hard fork of [Git for Unity](https://github.com/spoiledcat/git-for-unity) (itself descended from GitHub for Unity), diverged with: automatic LFS locking, a rebuilt native UI, a modern bundled Git (2.53+, Apple Silicon native), and many fixes. It does **not** use the upstream package registry.

## Install (single URL)

In Unity open **Window ▸ Package Manager ▸ + ▸ Add package from git URL…** and paste:

```
https://github.com/wayexperience/git-waypoint.git#upm
```

That's it — one package (`it.wayexperience.unity.git-waypoint`) bundling everything (API, editor UI, native file watcher). Then open it via **Window ▸ Git Waypoint**.

On a machine with no Git installed, the first run downloads a portable Git + Git LFS automatically. To use your own instead: Settings ▸ Git installation ▸ **Find system git**.

### First run
- Open **Window ▸ Git Waypoint**. If your Git identity isn't set, a banner prompts for name + email (also editable in the Settings tab). Git actions stay disabled until it's set.
- The bundled Git/LFS install automatically; the detected versions show next to the paths in Settings.

### For maintainers
The `#upm` branch is a generated snapshot of the unified package. To cut a new one:

```
packaging/make-unified-package.sh      # assembles build/upm-unified/it.wayexperience.unity.git-waypoint
packaging/publish-unified-package.sh   # force-pushes it to the orphan `upm` branch
```

## What's all this then?

Git Waypoint is split into two parts: The API part is a .NET Git Client library, without any dependencies on Unity itself; The UI part is Unity-specific. In source they live as two packages — `it.wayexperience.unity.git-waypoint.api` (the Git client library) and `it.wayexperience.unity.git-waypoint.ui` (the Git UI for the Unity Editor) — which are merged into the single `it.wayexperience.unity.git-waypoint` package for distribution.

Even though this project is currently a fork, since neither GitHub nor Unity seem very interested in supporting developer tooling, this is probably going to become the main implementation of this - this is why this repository is not a GitHub(tm) fork, but a completely separate repository, inheriting the history of both GitHub for Unity and Git for Unity.

## How to Build

This repository is LFS-enabled. To clone it, you should use a git client that supports git LFS 2.x.

Check [How to Build](https://github.com/wayexperience/git-waypoint/blob/main/BUILD.md) for all the build, packaging and versioning details.

### Release build 

`build[.sh|cmd] -r`

### Release build and package

`pack[.sh|cmd] -r -b`

### Release build and test

`test[.sh|cmd] -r -b`


### Where are the build artifacts?

Packages sources are in `build/packages`.

Nuget packages are in `build/nuget`.

Packman (npm) packages are in `upm-ci~/packages`.

Binaries for each project are in `build/bin` for the main projects, `build/Samples/bin` for the samples, and `build/bin/tests` for the tests.

### How to bump the major or minor parts of the version

The `version.json` file in the root of the repo controls the version for all packages.
Set the major and/or minor number in it and **commit the change** so that the next build uses the new version.
The patch part of the version is the height of the commit tree since the last manual change of the `version.json`
file, so once you commit a change to the major or minor parts, the patch will reset back to 0.

## License

**[MIT](LICENSE)**

Copyright 2020-2024 Andreia Gaita

Copyright 2019 Unity

The MIT license grant is not for Unity Technologies's trademarks, which include the Unity logo designs. Unity Technologies reserves all trademark and copyright rights in and to all Unity Technologies trademarks.

Copyright 2015 - 2018 GitHub, Inc.

The MIT license grant is not for GitHub's trademarks, which include the GitHub logo designs. GitHub reserves all trademark and copyright rights in and to all GitHub trademarks.
