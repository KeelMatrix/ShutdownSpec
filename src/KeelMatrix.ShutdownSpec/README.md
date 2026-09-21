# KeelMatrix.ShutdownSpec

**KeelMatrix.ShutdownSpec** is a test-framework-neutral NuGet library for checking that an `IHostedService` or `BackgroundService` starts, observes application-owned lifecycle checkpoints, and completes graceful shutdown within a declared test contract.

## Install

```bash
dotnet add package KeelMatrix.ShutdownSpec
```

## Quick Start

```csharp
using KeelMatrix.ShutdownSpec;
using Microsoft.Extensions.Hosting;

var ready = new ShutdownProbe("ready");
var result = await ShutdownHarness
    .For(() => new Worker(ready))
    .WithReadinessProbe(ready)
    .RunAsync();

result.ShouldCompleteWithoutFault();

sealed class Worker : BackgroundService
{
    private readonly ShutdownProbe _ready;

    public Worker(ShutdownProbe ready) => _ready = ready;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _ready.MarkObserved();
        try { await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken); }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}
```

The returned `ShutdownResult` records lifecycle facts and a stable `ShutdownOutcome`. Assertion methods throw `ShutdownAssertionException`, so the same API works with xUnit, NUnit, MSTest, or plain code.

After `StopAsync` returns, an observable `BackgroundService.ExecuteTask` is still awaited within the remaining configured bounds. A task that remains running is reported as `ServiceNoncompletion`; completion, fault, and cancellation are classified from the final observed task state.

Representative failure output for a worker that ignores cancellation:

```text
KMSHUT101: ServiceNoncompletion.
Phase: Completed
Startup completed: True; stop initiated: True; stop completed: False
Execution: Running; entered: True; completed: False; canceled: False
Harness deadline: False; startup deadline: False; shutdown deadline: True
Cleanup completed: True; cleanup timed out: False
```

## Probes and deadlines

Create a `ShutdownProbe` in the test and mark it from application-owned code. Register it with `WithProbe` and assert it with `ShouldObserve`. Use `WithReadinessProbe` when shutdown must not begin until a readiness checkpoint has been observed.

`WithStartupDeadline`, `WithShutdownDeadline`, and `WithHarnessDeadline` are separate. The host shutdown token passed to `StartAsync`/`StopAsync`, the `BackgroundService` stopping token, the caller cancellation token, and the harness's outer safety deadline are recorded separately. A test deadline does not set or predict a production host timeout.

The package targets `net8.0` and `netstandard2.0` and is validated against `Microsoft.Extensions.Hosting` 10.0.12. The repository's public CI matrix validates it on Windows, Linux, and macOS. See the [canonical supported-platform statement](https://github.com/KeelMatrix/ShutdownSpec/blob/main/docs/contract.md#supported-platforms-and-dependency-boundary) for the full compatibility boundary. The package makes no network requests.

## Limitations

ShutdownSpec does not infer queue or database drain state, make network requests, collect telemetry, control processes, or replace the host. It can bound asynchronous operations, but it cannot safely regain control from an arbitrary synchronous infinite loop on the calling process's thread; the truthful result is returned while the blocked thread may remain.

See the [canonical lifecycle contract](https://github.com/KeelMatrix/ShutdownSpec/blob/main/docs/contract.md) for outcome codes and failure diagnostics.

## Troubleshooting

- `StartupNoncompletion` means `StartAsync` or the readiness probe exceeded the startup deadline. Check the readiness probe and startup path before increasing the deadline.
- `FactoryNoncompletion` means the synchronous service factory did not return before the outer deadline. The harness bounds its wait, but it cannot interrupt a synchronous block in the same process.
- `ServiceNoncompletion` means stop or an observable execution task did not finish before the applicable shutdown/outer deadline. `StopCompleted` can be true when a custom `StopAsync` returns early; inspect the execution state and exact deadline provenance.
- `CleanupNoncompletion` means disposal did not return before the cleanup deadline. Use process isolation when a hard interruption boundary is required.
