# ShutdownSpec Lifecycle Contract

This document defines the v1 result semantics for KeelMatrix.ShutdownSpec. The harness checks an in-process lifecycle contract; it does not replace the host or prove external system durability.

## Supported platforms and dependency boundary

The package is validated on Windows, Linux, and macOS by the repository's public CI matrix. Its dependency/runtime boundary is `Microsoft.Extensions.Hosting` 10.0.12 with `net8.0` and `netstandard2.0` target frameworks. This is the canonical supported-platform statement for the repository; other documentation surfaces should link here when they need the full claim.

## Scenario lifecycle

1. The factory creates one service instance within the outer harness deadline.
2. The harness starts the service with a startup token.
3. The harness records readiness and application-owned probes when the consumer marks them.
4. The harness starts graceful shutdown with a distinct host shutdown token.
5. The harness observes the service and, for `BackgroundService`, its public execution task when available from the targeted hosting package.
6. After `StopAsync` returns, a still-running observable execution task is awaited within the remaining shutdown and outer-harness bounds. A task that completes, faults, or cancels during that bounded observation is classified from its final state.
7. Bounded cleanup runs after failure, cancellation, or noncompletion.

The caller cancellation token and the harness outer deadline control the harness wait. They are not the host shutdown token, and neither is the `BackgroundService` stopping token passed by the hosting implementation to `ExecuteAsync`.

`WithStartupDeadline` and `WithShutdownDeadline` bound lifecycle phases. `WithHarnessDeadline` is the outer safety boundary. A default test deadline is not a production host shutdown timeout.

The defaults are 5 seconds for startup, 5 seconds for graceful shutdown, 15 seconds for the complete harness, and 1 second for bounded cleanup.

## Outcomes

| Outcome | Meaning |
| --- | --- |
| `CleanCompletion` | Startup, graceful shutdown, and any bounded post-stop observation completed without an unexpected fault or missed deadline. |
| `ExpectedCancellation` | The service completed through expected cancellation. |
| `ExecutionNotStarted` | Immediate stop completed before a background execution body became observable. This is not a clean result. |
| `StartupFailure` | `StartAsync` or host construction failed after factory creation. |
| `StopFault` | `StopAsync` or host shutdown failed unexpectedly. |
| `ExecutionFault` | A public `BackgroundService.ExecuteTask` or an observed execution task faulted. |
| `HarnessDeadline` | The outer harness deadline expired before the lifecycle could reach graceful shutdown. |
| `CallerCancellation` | The caller cancellation token ended the harness wait. |
| `ServiceNoncompletion` | The service or its observable execution task was still incomplete at the shutdown or outer harness deadline. `StopCompleted` may still be `true` when `ExecuteTask` remains running after an early-returning `StopAsync`. |
| `CleanupFailure` | Bounded cleanup failed after the primary outcome; the primary outcome remains available in the result. |
| `FactoryFailure` | The service factory failed before startup began. |
| `FactoryNoncompletion` | The service factory did not return before the outer harness deadline. |
| `StartupNoncompletion` | `StartAsync` or the readiness probe did not complete before the startup phase deadline. |
| `CleanupNoncompletion` | Disposal did not return before the cleanup deadline; this is never a successful result. |
| `ExecutionCancellationUnverified` | The observed execution task canceled after stop began, but no independently observed application stopping-token checkpoint proved that the cancellation came from the expected shutdown path. |

An unexpected fault or cancellation from an unrelated source never becomes a clean result. A cancellation already present before graceful stop is classified as an execution fault; cancellation observed after stop begins is classified as expected cancellation only when an independently configured stopping-token probe was observed and the execution-entry checkpoint was observed. Otherwise the result is `ExecutionCancellationUnverified` or `ExecutionNotStarted`. A service that ignores cancellation is classified as `ServiceNoncompletion`, even if a later cleanup task eventually finishes. A phase deadline and the outer deadline set only their corresponding provenance flag; a stop or post-stop observation that reaches the outer deadline remains `ServiceNoncompletion` with `HarnessDeadlineFired=true` and `ShutdownDeadlineFired=false`.

## Application-owned probes

ShutdownSpec cannot infer that a queue, database, or broker is drained. Create a `ShutdownProbe`, mark it from application-owned code, register it with `WithProbe`, and assert it with `ShouldObserve`. Register a readiness probe with `WithReadinessProbe` when the service must reach a known ready state before shutdown begins. A stopping-token probe can be marked from the service's own stopping-token registration.

Probe state is reset at the beginning of every `RunAsync` call, so a reusable harness cannot satisfy a later readiness or observation assertion from an earlier run. Use `WithExecutionProbe` for an independently marked execution-entry checkpoint and `WithStoppingProbe` for a probe registered with the service's stopping token.

Probe names are bounded and only their observed names are included in the default diagnostic report. Probe payloads are never transmitted or logged by the library.

## Diagnostics

`ShutdownResult.ToDiagnosticString()` reports a stable failure code, phase, elapsed startup/shutdown/cleanup durations, startup/stop/execution state, host-token and harness-deadline facts, primary and cleanup outcomes, cancellation-notification state, and observed probe names. It reports exception type names but does not append arbitrary exception messages or stacks by default. `PrimaryOutcome` and `PrimaryPhase` remain available when bounded cleanup reports a separate failure through `CleanupOutcome`.

The primary failure codes are:

- `KMSHUT001` startup failure
- `KMSHUT002` stop fault
- `KMSHUT003` execution fault
- `KMSHUT101` service noncompletion
- `KMSHUT102` harness deadline
- `KMSHUT103` caller cancellation
- `KMSHUT104` execution not started before immediate stop
- `KMSHUT106` factory failure
- `KMSHUT107` factory noncompletion
- `KMSHUT108` startup noncompletion
- `KMSHUT109` cleanup noncompletion
- `KMSHUT110` execution cancellation without verified shutdown provenance

## In-process limitation

The harness cannot safely interrupt arbitrary application code that blocks synchronously forever in the same process, including a service factory or disposal. It bounds its wait and reports `FactoryNoncompletion`, `CleanupNoncompletion`, or another truthful deadline outcome, but the blocked thread may remain. Use process isolation when a hard kill boundary is part of the test contract.
