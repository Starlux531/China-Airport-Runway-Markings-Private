# Private source repository

This repository must remain PRIVATE. The public repository contains only documentation, images and binary releases. Never push this source tree or its history to the public repository.

Requires .NET 10 SDK on Windows. Build and test:

```powershell
dotnet run --project tests/SmokeTests/SmokeTests.csproj -c Release
dotnet publish src/ChinaRunwayMarkings/ChinaRunwayMarkings.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=None -p:DebugSymbols=false -o artifacts/release-0.5.0
```

Read-only installation discovery:

```powershell
dotnet run --project tests/SmokeTests/SmokeTests.csproj -c Release -- --locate
```

Generate native UI images (local database required; not distributed):

```powershell
dotnet run --project tests/SmokeTests/SmokeTests.csproj -c Release -- --render apt.dat docs/images
```

Package only the release EXE, user guide, closed-source license and bundled .NET license notices. Do not package apt.dat, private source, PDB, build intermediates, user configuration, local installation paths or export manifests. Self-contained .NET binary distribution does not prevent reverse engineering.
