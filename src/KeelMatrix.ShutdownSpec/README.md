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

The package targets `net8.0` and `netstandard2.0` and is validated against `Microsoft.Extensions.Hosting` 10.0.12. It is OS-neutral and intended for supported .NET runtimes on Windows, Linux, and macOS; the current repository evidence covers Windows and Linux, while macOS requires external validation. The package makes no network requests.

## Limitations

ShutdownSpec does not infer queue or database drain state, make network requests, collect telemetry, control processes, or replace the host. It cannot safely regain control from an arbitrary synchronous infinite loop in the same process; the bounded result reports a deadline or noncompletion outcome, and the blocked thread may remain.

See the [canonical lifecycle contract](https://github.com/KeelMatrix/ShutdownSpec/blob/main/docs/contract.md) for outcome codes and failure diagnostics.

## Troubleshooting

- `StartupNoncompletion` means `StartAsync` or the readiness probe exceeded the startup deadline. Check the readiness probe and startup path before increasing the deadline.
- `FactoryNoncompletion` means the synchronous service factory did not return before the outer deadline. The harness bounds its wait, but it cannot interrupt a synchronous block in the same process.
- `ServiceNoncompletion` means stop or execution did not finish before the applicable shutdown/outer deadline. Inspect the diagnostic report for the exact deadline provenance.
- `CleanupNoncompletion` means disposal did not return before the cleanup deadline. Use process isolation when a hard interruption boundary is required.
