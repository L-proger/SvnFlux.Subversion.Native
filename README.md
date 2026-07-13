# SvnFlux.Subversion.Native

Personal build infrastructure for experimenting with native Apache Subversion libraries from C#. It is public and can be useful to others, but makes no promises about support, compatibility, release cadence, or production readiness.

The repository produces two self-contained RID packages: `SvnFlux.Subversion.Native.win-x64` and `SvnFlux.Subversion.Native.win-arm64`. Each contains its matching native libraries and an independently generated public `SvnFlux.Subversion.dll` P/Invoke API. Subversion and most dependencies are built from source with MSVC; OpenSSL comes from the pinned `openssl-native` NuGet package. No DLLs are taken from an installed SVN client.

The Subversion source is pinned to trunk commit `6e6a9b0ddf0d745be7b56f6f1804fbc8216bd067`. It is a development snapshot rather than an official stable release; the modern CMake build is intentionally preferred over historical Windows build pipelines.

Pinned dependencies: APR 1.7.6, APR-util 1.6.3, Serf 1.3.10, zlib 1.3.2, SQLite 3.53.3, Expat 2.8.2, OpenSSL 3.5.5, SCons 4.10.1, and SharpCompress 0.49.1.

## Build

Prerequisites: Visual Studio 2022 with C++, CMake, Ninja and Python 3. SCons is installed into the local build cache automatically. OpenSSL headers, import libraries, and runtime DLLs come from the pinned `openssl-native` NuGet package, so Perl and NASM are not required.

```powershell
dotnet run --project build/SvnFlux.Subversion.Native.Build -c Release -- --no-pack
dotnet tool restore
./scripts/Generate-Bindings.ps1 -Rid win-x64
dotnet build src/SvnFlux.Subversion.Interop.win-x64 -c Release
dotnet pack src/SvnFlux.Subversion.Native.win-x64 -c Release --output artifacts/packages
```

Pass `--rid win-arm64` to cross-compile for Windows ARM64. `win-x64` is the default.

The result is `artifacts/packages/SvnFlux.Subversion.Native.<rid>.<version>.nupkg`. Pass `--rid win-arm64` to the native build and the matching later commands for ARM64. Use `--skip-dependencies` to reuse an existing dependency build.

Validate a completed build with:

```powershell
./scripts/Verify-NativeBuild.ps1 -Rid win-x64
./scripts/Verify-NativeBuild.ps1 -Rid win-arm64
```

## Generate Windows bindings

After building both native targets and restoring the local .NET tools, generate the two independent P/Invoke wrappers with:

```powershell
./scripts/Generate-Bindings.ps1
```

The generator parses the public SVN/APR headers separately for each RID and uses `dumpbin /exports` to assign every generated function to its exact DLL. Each interop project gets one function file per DLL plus its own platform-specific types. Generated files are build outputs and are not committed. CI regenerates them immediately after building each RID's native DLLs. Export diagnostics are written below `.build/<rid>/bindings`.

## Packages and tags

Release tags have the form `svn-<NuGetVersion>`. For example, tag `svn-1.16.0-dev.20260712.6e6a9b0.1` produces both RID packages with version `1.16.0-dev.20260712.6e6a9b0.1`. A tag build publishes them to GitHub Packages; a manually dispatched build publishes only when explicitly requested.

After committing a release-ready state, create and immediately push the next tag with:

```powershell
./scripts/New-ReleaseTag.ps1
```

The script reads the SVN version and pinned commit from `DependencyCatalog.cs`, uses the current UTC date, increments the package revision for matching existing tags, and pushes the annotated tag to `origin`. It refuses to tag a dirty working tree. Use `-WhatIf` to preview the generated tag without creating or pushing it.

Consumers add the GitHub NuGet source and reference the package matching their build RID. Each package contains `lib/net10.0/SvnFlux.Subversion.dll` and its matching `runtimes/<rid>/native` assets. There is no shared third package; platform-specific API differences remain visible to consumers and may be handled with conditional compilation.

Downloaded and compiled trees live below `.build` and are not committed.
