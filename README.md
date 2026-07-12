# SvnFlux.Subversion.Native

Personal build infrastructure for experimenting with native Apache Subversion libraries from C#. It is public and can be useful to others, but makes no promises about support, compatibility, release cadence, or production readiness.

The supported runtimes are `win-x64` and `win-arm64`. Subversion and most dependencies are built from source with MSVC; OpenSSL comes from the pinned `openssl-native` NuGet package. No DLLs are taken from an installed SVN client.

The Subversion source is pinned to trunk commit `6e6a9b0ddf0d745be7b56f6f1804fbc8216bd067`. It is a development snapshot rather than an official stable release; the modern CMake build is intentionally preferred over historical Windows build pipelines.

Pinned dependencies: APR 1.7.6, APR-util 1.6.3, Serf 1.3.10, zlib 1.3.2, SQLite 3.53.3, Expat 2.8.2, OpenSSL 3.5.5, SCons 4.10.1, and SharpCompress 0.49.1.

## Build

Prerequisites: Visual Studio 2022 with C++, CMake, Ninja and Python 3. SCons is installed into the local build cache automatically. OpenSSL headers, import libraries, and runtime DLLs come from the pinned `openssl-native` NuGet package, so Perl and NASM are not required.

```powershell
dotnet run --project build/SvnFlux.Subversion.Native.Build -c Release
```

Pass `--rid win-arm64` to cross-compile for Windows ARM64. `win-x64` is the default.

The result is `artifacts/packages/SvnFlux.Subversion.Native.<rid>.<version>.nupkg`. Use `--no-pack` to stop after producing `artifacts/native/<rid>`, or `--skip-dependencies` to reuse an existing dependency build.

Validate a completed build with:

```powershell
./scripts/Verify-NativeBuild.ps1 -Rid win-x64
./scripts/Verify-NativeBuild.ps1 -Rid win-arm64
```

## Packages and tags

Release tags have the form `svn-<NuGetVersion>`. For example, tag `svn-1.16.0-dev.20260712.6e6a9b0.1` produces both RID packages with version `1.16.0-dev.20260712.6e6a9b0.1`. A tag build publishes them to GitHub Packages; a manually dispatched build publishes only when explicitly requested.

After committing a release-ready state, create and immediately push the next tag with:

```powershell
./scripts/New-ReleaseTag.ps1
```

The script reads the SVN version and pinned commit from `DependencyCatalog.cs`, uses the current UTC date, increments the package revision for matching existing tags, and pushes the annotated tag to `origin`. It refuses to tag a dirty working tree. Use `-WhatIf` to preview the generated tag without creating or pushing it.

Consumers add the GitHub NuGet source and reference the RID packages from a managed wrapper. Each package contains only its matching `runtimes/<rid>/native` assets.

Downloaded and compiled trees live below `.build` and are not committed.
