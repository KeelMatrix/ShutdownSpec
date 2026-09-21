# Developer Validation

This guide describes the repository-local validation path for KeelMatrix.ShutdownSpec.

## Prerequisites

- .NET SDK 8.0.425 or a compatible patch release selected by `global.json`.
- Access to `https://api.nuget.org/v3/index.json` for restore and dependency audit.

## Focused validation

```powershell
dotnet test tests/KeelMatrix.ShutdownSpec.Tests/KeelMatrix.ShutdownSpec.Tests.csproj -c Release --no-restore
```

## Full local validation

```powershell
dotnet restore KeelMatrix.ShutdownSpec.sln --configfile NuGet.config
dotnet test KeelMatrix.ShutdownSpec.sln -c Release --no-restore
pwsh -NoProfile -File scripts/Invoke-PackageGate.ps1
dotnet list KeelMatrix.ShutdownSpec.sln package --vulnerable --include-transitive --configfile NuGet.config
```

The package gate builds the shipping project, validates the exact package payload and metadata, and restores the non-solution consumer from an isolated local feed with a fresh global-packages folder.

## API baseline

The shipping project references `Microsoft.CodeAnalysis.PublicApiAnalyzers` unconditionally. Every supported public entry is recorded in `src/KeelMatrix.ShutdownSpec/PublicAPI.Shipped.txt`; `PublicAPI.Unshipped.txt` is header-only after release preparation.

## Package smoke

The smoke project deliberately has no project reference to the library. It runs a clean direct scenario and an ignored-cancellation scenario from the packed `.nupkg`, and checks that the latter is classified as service noncompletion rather than a false clean result.
