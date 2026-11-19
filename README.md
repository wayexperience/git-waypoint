<p align="center">
  <img src="media/logo.png" alt="Git Waypoint" width="160">
</p>

# Git Waypoint

Git Waypoint is a Git client for the Unity Editor with **Perforce-style Git LFS auto-locking**, built so artists *and* developers can work on the same project without stepping on each other's binary assets. It installs a portable Git + Git LFS automatically, so users don't need Git pre-installed.

It is a hard fork of [Git for Unity](https://github.com/spoiledcat/git-for-unity) (itself descended from GitHub for Unity), with automatic LFS locking, a rebuilt UI Toolkit window, a modern bundled Git (2.53+, Apple Silicon native) and many fixes.

## Install

In Unity open **Window ▸ Package Manager ▸ + ▸ Add package from git URL…** and paste:

```
https://github.com/wayexperience/git-waypoint.git#upm
```

Then open **Window ▸ Git Waypoint**.

## Documentation

- **[Quick guide](docs/quick-guide.md)**: first run, daily use, locks, settings.
- **[Contributing](docs/contributing.md)**: repo layout, dev loop, compile check, releasing.
- **[Changelog](CHANGELOG.md)**

## Acknowledgements

Git Waypoint stands on the shoulders of two open-source projects:

- **[Git for Unity](https://github.com/spoiledcat/git-for-unity)** by **Andreia Gaita**, the direct upstream.
- **[GitHub for Unity](https://github.com/github-for-unity/Unity)** by **GitHub**, the original project both descend from.

## License

**[MIT](LICENSE.md)**

Copyright 2026 WAY Experience

Copyright 2020-2024 Andreia Gaita

Copyright 2019 Unity

The MIT license grant is not for Unity Technologies's trademarks, which include the Unity logo designs. Unity Technologies reserves all trademark and copyright rights in and to all Unity Technologies trademarks.

Copyright 2015 - 2018 GitHub, Inc.

The MIT license grant is not for GitHub's trademarks, which include the GitHub logo designs. GitHub reserves all trademark and copyright rights in and to all GitHub trademarks.
