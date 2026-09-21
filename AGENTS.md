# Repository Guide

## Navigation

- `src/KeelMatrix.ShutdownSpec` contains the packable library and its package README.
- `tests/KeelMatrix.ShutdownSpec.Tests` contains direct-mode contract and regression tests.
- `tests/KeelMatrix.ShutdownSpec.IntegrationTests` contains real-host integration tests.
- `tests/PackageSmoke` is outside the solution and consumes the built package through `PackageReference`.
- `scripts/Invoke-PackageGate.ps1` is the deterministic pack, inspect, and consumer-smoke gate.
- `docs/contract.md` documents the lifecycle contract; `docs/DEV.md` documents repository validation.

## Commands

```text
dotnet test KeelMatrix.ShutdownSpec.sln -c Release
pwsh -NoProfile -File scripts/Invoke-PackageGate.ps1
dotnet list KeelMatrix.ShutdownSpec.sln package --vulnerable --include-transitive --configfile NuGet.config
```

## Invariants

- The shipping project is the only packable project.
- Tests and package smoke use a project reference or the built package respectively; the smoke project never uses a project reference.
- The package is test-framework-neutral and makes no product-owned network requests.
- The harness outer deadline, host shutdown token, caller cancellation, and service stopping token remain distinct in implementation, result state, and diagnostics.
- Do not add a project-local icon. The pack configuration resolves the package icon to the repository-root `icon.png` when that repository-root icon.png exists.

## Validation

Run the focused test first, then the Release solution tests, then the package gate. Keep package artifacts under the ignored `artifacts/` directory and do not commit generated output.
