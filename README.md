# KeelMatrix.ShutdownSpec

**ShutdownSpec turns .NET hosted-service shutdown into a deterministic test contract.** Verify cancellation, bounded completion, faults, and application-owned drain checkpoints for `IHostedService` and `BackgroundService` without building a custom timeout harness in every project.

## Install

```bash
dotnet add package KeelMatrix.ShutdownSpec
```

## Quick Start

Use the direct mode for the shortest path. The package does not require xUnit, NUnit, MSTest, or another test framework.

```csharp
using KeelMatrix.ShutdownSpec;
using Microsoft.Extensions.Hosting;

var ready = new ShutdownProbe("ready");
var executionEntered = new ShutdownProbe("execution-entered");
var result = await ShutdownHarness
    .For(() => new Worker(ready, executionEntered))
    .WithReadinessProbe(ready)
    .WithExecutionProbe(executionEntered)
    .RunAsync();

result.ShouldCompleteWithoutFault();

sealed class Worker : BackgroundService
{
    private readonly ShutdownProbe _ready;
    private readonly ShutdownProbe _executionEntered;

    public Worker(ShutdownProbe ready, ShutdownProbe executionEntered)
    {
        _ready = ready;
        _executionEntered = executionEntered;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _ready.MarkObserved();
        _executionEntered.MarkObserved();
        try { await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken); }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}
```

The returned `ShutdownResult` records lifecycle facts and a stable `ShutdownOutcome`. Readiness is reported separately from the independently observed execution-entry checkpoint; a readiness probe never proves that the execution body entered. Assertion methods throw `ShutdownAssertionException`, so the same API works with xUnit, NUnit, MSTest, or plain code.

After `StopAsync` returns, an observable `BackgroundService.ExecuteTask` is still awaited within the remaining configured bounds. A task that remains running is reported as `ServiceNoncompletion`; completion, fault, and cancellation are classified from the final observed task state.

## Host-backed mode

Use host-backed mode when the test needs actual `IHost` registration and orchestration:

```csharp
var ready = new ShutdownProbe("ready");
var executionEntered = new ShutdownProbe("execution-entered");
var result = await ShutdownHarness
    .For(() => new Worker(ready, executionEntered))
    .WithReadinessProbe(ready)
    .WithExecutionProbe(executionEntered)
    .WithHost(() => Host.CreateDefaultBuilder())
    .RunAsync();
```

Direct mode remains the simplest option and does not require constructing a full host.

## Important limitations

- The harness proves the configured in-process test contract. It does not prove broker, database, container, or orchestrator durability.
- Application code must mark its own readiness, execution-entry, stopping-token, and drain checkpoints. ShutdownSpec does not infer execution entry or domain state from readiness or task state.
- The harness can bound asynchronous operations, but it cannot safely regain control from an arbitrary synchronous infinite loop on the calling process's thread. The blocked thread may remain after the truthful result is returned; use process isolation for a hard kill boundary.
- A test deadline is not a production host shutdown timeout. Configure both deliberately for their separate purposes.
- The package makes no product-owned network requests and emits no telemetry.

## Documentation

- [Lifecycle contract and outcome reference](https://github.com/KeelMatrix/ShutdownSpec/blob/main/docs/contract.md)
- [Developer validation guide](https://github.com/KeelMatrix/ShutdownSpec/blob/main/docs/DEV.md)
- [Privacy](https://github.com/KeelMatrix/ShutdownSpec/blob/main/PRIVACY.md)

## Supported frameworks

The package targets `net8.0` and `netstandard2.0` and uses `Microsoft.Extensions.Hosting` 10.0.12 for the hosted-service contract. The repository's public CI matrix validates it on Windows, Linux, and macOS. See the [canonical supported-platform statement](https://github.com/KeelMatrix/ShutdownSpec/blob/main/docs/contract.md#supported-platforms-and-dependency-boundary) for the full compatibility boundary.

## License

MIT. See [LICENSE](https://github.com/KeelMatrix/ShutdownSpec/blob/main/LICENSE).
