# How to build

## Build Requirements

This repository is LFS-enabled. To clone it, you should use a git client that supports git LFS 2.x and submodules.

### Windows

- dotnet core 2.1+, Visual Studio 2017+ or Mono 5.x.
- `UnityEngine.dll` and `UnityEditor.dll`.
  - If you've installed Unity in the default location of `C:\Program Files\Unity` or `C:\Program Files (x86)\Unity`, the build will be able to reference these DLLs automatically. Otherwise, you'll need to copy these DLLs from `[Unity installation path]\Unity\Editor\Data\Managed` into the `lib` directory in order for the build to work

### MacOS

- dotnet core 2.1+ or Mono 5.x. You can install it via brew.
- `UnityEngine.dll` and `UnityEditor.dll`.
  - If you've installed Unity in the default location of `/Applications/Unity`, the build will be able to reference these DLLs automatically. Otherwise, you'll need to copy these DLLs from `[Unity installation path]/Unity.app/Contents/Managed` into the `lib` directory in order for the build to work

## How to Build

Clone the repository and its submodules in a git GUI client that supports Git LFS, or via the command line with the following command:

```
git lfs clone https://github.com/wayexperience/git-waypoint

```

### Windows Command Line


- Release builds: `.\build`
- Debug builds: `.\build -d`

### Mac, Linux and Windows bash

- Release builds: `./build.sh`
- Debug builds: `./build.sh -d`

### Visual Studio

To build with Visual Studio, open the solution file `GitWaypoint.sln`. Select `Build Solution` in the `Build` menu.


## Build artifacts

Once you've built the solution for the first time, you can open one of the test projects under `UnityProject/` (e.g. `GitWaypoint-Full`) in Unity. These reference the API and UI sources as packages via symlinks.

Note: some files might be locked by Unity if have one of the build output projects open when you compile from VS or the command line. This is expected and shouldn't cause issues with your builds.

To publish a release (build the unified UPM package and push it), see [RELEASING.md](../../RELEASING.md) — `release.sh` is the one-command release flow; there's no separate packaging step to run by hand.

## Testing

### Windows

- Test release: `.\test`
- Test debug: `.\test -d`
- Build and test: `.\test -b`

### Mac, Linux, Windows Bash

- Test release: `./test.sh`
- Test debug: `./test.sh -d`
- Build and test: `./test.sh -b`
