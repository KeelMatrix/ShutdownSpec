# KeelMatrix.ShutdownSpec

**KeelMatrix.ShutdownSpec** is a test-framework-neutral NuGet library for checking that an `IHostedService` or `BackgroundService` starts, observes application-owned lifecycle checkpoints, and completes graceful shutdown within a declared test contract.

## Install

```bash
dotnet add package KeelMatrix.ShutdownSpec
```

## Quick Start

```csharp
var result = await ShutdownHarness
    .For(() => new Worker())
    .RunAsync();

result.ShouldCompleteWithoutFault();
```

The returned `ShutdownResult` records lifecycle facts and a stable `ShutdownOutcome`. Assertion methods throw `ShutdownAssertionException`, so the same API works with xUnit, NUnit, MSTest, or plain code.

## Probes and deadlines

Create a `ShutdownProbe` in the test and mark it from application-owned code. Register it with `WithProbe` and assert it with `ShouldObserve`. Use `WithReadinessProbe` when shutdown must not begin until a readiness checkpoint has been observed.

`WithStartupDeadline`, `WithShutdownDeadline`, and `WithHarnessDeadline` are separate. The host shutdown token passed to `StartAsync`/`StopAsync`, the `BackgroundService` stopping token, the caller cancellation token, and the harness's outer safety deadline are recorded separately. A test deadline does not set or predict a production host timeout.

## Limitations

ShutdownSpec does not infer queue or database drain state, make network requests, collect telemetry, control processes, or replace the host. It cannot safely regain control from an arbitrary synchronous infinite loop in the same process; that limitation is surfaced in the result and diagnostic report.

See the [canonical lifecycle contract](https://github.com/KeelMatrix/ShutdownSpec/blob/main/docs/contract.md) for outcome codes and failure diagnostics.
