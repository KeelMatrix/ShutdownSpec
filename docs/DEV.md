# Developer Validation

This guide describes the repository-local validation path for KeelMatrix.ShutdownSpec.

## Supported platform evidence

The repository's public CI matrix validates the Release test suite and package gate on Windows, Linux, and macOS. The canonical supported-platform and dependency-boundary statement is maintained in [the lifecycle contract](contract.md#supported-platforms-and-dependency-boundary).

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
# Both package-gate modes require the repository-root `icon.png`; release-readiness mode also performs the release-specific checks.
pwsh -NoProfile -File scripts/Invoke-PackageGate.ps1 -ReleaseReadiness
pwsh -NoProfile -File scripts/Invoke-VulnerabilityAudit.ps1
```

The package-gate PNG regression control can be run without building a package:

```powershell
pwsh -NoProfile -File scripts/Invoke-PackageGate.ps1 -PngValidationSelfTest
```

It checks dimensions that exceed a byte and confirms that non-512x512 and oversized inputs fail closed.

The package gate restores the solution, runs the same fail-closed direct-and-transitive vulnerability audit used by release verification, builds the shipping project, validates the exact package payload and metadata, and restores the non-solution consumer from an isolated local feed with a fresh global-packages folder. The audit runs `dotnet list KeelMatrix.ShutdownSpec.sln package --vulnerable --include-transitive --configfile NuGet.config` and fails on a vulnerable package, an advisory-service error, a command failure, or an incomplete report. The shipping pack target requires the repository-root `icon.png` in ordinary development and release-readiness mode; release-readiness mode also fails closed when that icon is absent or invalid before the package is built.

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

## Stress and compatibility

The bounded several-thousand-iteration fixture and the isolated hosting-version comparison are documented in [stress-and-compatibility.md](stress-and-compatibility.md). The `net8.0` commands use the repository SDK; the `net10.0` commands run from `tests/Stress` so the comparison uses the nested SDK selection. The compatibility fixtures do not reference the shipping project and therefore do not change its dependency boundary.
