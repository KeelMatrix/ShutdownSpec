# Changelog

Changes to KeelMatrix.ShutdownSpec are documented here.

## [Unreleased]

- Fixed false `CleanCompletion` results when `StopAsync` returns before an observable `BackgroundService.ExecuteTask` completes. The harness now performs bounded post-stop observation and preserves truthful deadline provenance.
- Corrected platform-evidence documentation and added fail-closed direct-and-transitive vulnerability auditing to local package-gate and release verification.

## [0.1.0] - Planned

### Added

- Deterministic direct and host-backed lifecycle contracts for `IHostedService` and `BackgroundService` implementations.
- Structured cancellation, fault, deadline, noncompletion, and application-owned probe results without a test-framework dependency.
- Bounded cleanup and diagnostics for in-process hosted-service tests.
