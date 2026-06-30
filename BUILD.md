# How to build

## Requirements

- dotnet core 2.1+

## Command line build scripts

- `build.cmd`/`build.sh`: Build shell script for Windows command line or Windows/Mac/Linux bash. 
- `test.cmd`/`test.sh`: Test shell script for Windows command line or Windows/Mac/Linux bash. 

Both shell scripts take the same parameters:

- `-r|--release`: Release build (default)
- `-d|--debug`: Debug build
- `-p|--public`: Stamp a build with a public release version

### Visual Studio

To build with Visual Studio, open the solution file `GitWaypoint.sln`. Select `Build Solution` in the `Build` menu.

## Build artifacts

Before opening the Unity projects in `UnityProject/`, you should build at least once, so that required binaries are generated in the right places.

Note: If you build while having a Unity project open that points to the sources or build artifacts, some files might be locked by Unity if you have one of the build output projects open when you compile from VS or the command line. This is expected and shouldn't cause issues with your builds.

## Testing

### Windows

- Test release: `.\test`
- Test debug: `.\test -d`

### Mac, Linux, Windows Bash

- Test release: `./test.sh`
- Test debug: `./test.sh -d`

## Versioning and releasing

There's no separate packaging step and no auto-derived version. `release.sh` is the one-command
release flow: it bumps the `package.json` files and `FallbackVersion` to an explicit version you pass
it, commits, builds the unified UPM package, pushes it to the `upm` branch, tags, and cuts a GitHub
Release. See [RELEASING.md](RELEASING.md) for details.
