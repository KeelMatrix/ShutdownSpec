# Changelog

Changes to KeelMatrix.ShutdownSpec are documented here.

## [Unreleased]

- Fixed false `CleanCompletion` results when `StopAsync` returns before an observable `BackgroundService.ExecuteTask` completes. The harness now performs bounded post-stop observation and preserves truthful deadline provenance.
- Corrected platform-evidence documentation and added fail-closed direct-and-transitive vulnerability auditing to local package-gate and release verification.

## [0.1.0] - 2026-09-23

### Added

- Provides deterministic direct and host-backed lifecycle contracts for `IHostedService` and `BackgroundService` implementations.
- Supports configurable startup, shutdown, harness, and cleanup deadlines with bounded in-process test cleanup.
- Records application-owned readiness, execution, stopping-token, and drain probes alongside structured cancellation, fault, deadline, and noncompletion outcomes.
- Supplies framework-neutral assertion helpers and deterministic lifecycle diagnostics for xUnit, NUnit, MSTest, or plain code.
- Targets `net8.0` and `netstandard2.0` with `Microsoft.Extensions.Hosting` 10.0.12.
