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
# Release-readiness mode additionally requires the repository-root icon.png.
pwsh -NoProfile -File scripts/Invoke-PackageGate.ps1 -ReleaseReadiness
dotnet list KeelMatrix.ShutdownSpec.sln package --vulnerable --include-transitive --configfile NuGet.config
```

The package-gate PNG regression control can be run without building a package:

```powershell
pwsh -NoProfile -File scripts/Invoke-PackageGate.ps1 -PngValidationSelfTest
```

It checks dimensions that exceed a byte and confirms that non-512x512 and oversized inputs fail closed.

The package gate builds the shipping project, validates the exact package payload and metadata, and restores the non-solution consumer from an isolated local feed with a fresh global-packages folder. Ordinary development mode permits the repository-root icon to be absent. Release-readiness mode fails closed when that icon is absent or invalid.

## Release validation before tag creation

After the intended `CHANGELOG.md` entry has a final version and date, validate the exact tag contract before creating or pushing that tag:

```powershell
pwsh -NoProfile -File scripts/Validate-Release.ps1 -Tag v0.1.0 -FirstPublicRelease
```

The validator fails closed when the tag, changelog, package version, dependency versions, or README install commands disagree. It also rejects planned/unreleased entries, missing dates, and first-release entries that contain categories other than `Added` or remediation-history wording. The same command runs as the first gate in the tag-scoped release workflow.

## API baseline

The shipping project references `Microsoft.CodeAnalysis.PublicApiAnalyzers` unconditionally. Every supported public entry is recorded in `src/KeelMatrix.ShutdownSpec/PublicAPI.Shipped.txt`; `PublicAPI.Unshipped.txt` is header-only after release preparation.

## Package smoke

The smoke project deliberately has no project reference to the library. It runs the documented `BackgroundService` quick start plus packed-artifact controls for an ignored-cancellation service, an early-returning stop with a still-running execution task, delayed completion, delayed fault, and delayed cancellation.
