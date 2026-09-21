# Contributing

## Before you begin

Read the [Code of Conduct](CODE_OF_CONDUCT.md). For security vulnerabilities, use the private reporting route in [SECURITY.md](SECURITY.md) instead of a public issue.

## Making changes

1. Create a branch from `main`.
2. Add or update focused tests for behavior changes.
3. Keep public API changes intentional and update the shipping API baseline.
4. Update consumer documentation when the public contract changes.

## Validation

```powershell
dotnet test KeelMatrix.ShutdownSpec.sln -c Release
pwsh -NoProfile -File scripts/Invoke-PackageGate.ps1
dotnet list KeelMatrix.ShutdownSpec.sln package --vulnerable --include-transitive --configfile NuGet.config
```

The package gate builds and inspects the actual package, then runs the isolated package-consumer smoke test. Tests and the consumer project are explicitly non-packable.

## Public API

The shipping project uses Public API Analyzers. New members must be reviewed in `src/KeelMatrix.ShutdownSpec/PublicAPI.Unshipped.txt` and promoted to `PublicAPI.Shipped.txt` only when the release contract is finalized.

## Pull requests

Describe the user-facing behavior, tests run, documentation changes, and any remaining platform or runtime limitation.
