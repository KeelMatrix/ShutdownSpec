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

## Host-backed mode

Use host-backed mode when the test needs actual `IHost` registration and orchestration:

```csharp
var ready = new ShutdownProbe("ready");
var result = await ShutdownHarness
    .For(() => new Worker(ready))
    .WithReadinessProbe(ready)
    .WithHost(() => Host.CreateDefaultBuilder())
    .RunAsync();
```

Direct mode remains the simplest option and does not require constructing a full host.

## Important limitations

- The harness proves the configured in-process test contract. It does not prove broker, database, container, or orchestrator durability.
- Application code must mark its own readiness, stopping-token, and drain checkpoints. ShutdownSpec does not infer domain state.
- The harness cannot safely interrupt an arbitrary synchronous infinite loop in the same process. Such a scenario is bounded and classified as a deadline or noncompletion outcome; use process isolation for a hard kill boundary.
- A test deadline is not a production host shutdown timeout. Configure both deliberately for their separate purposes.
- The package makes no product-owned network requests and emits no telemetry.

## Documentation

- [Lifecycle contract and outcome reference](https://github.com/KeelMatrix/ShutdownSpec/blob/main/docs/contract.md)
- [Developer validation guide](https://github.com/KeelMatrix/ShutdownSpec/blob/main/docs/DEV.md)
- [Privacy](https://github.com/KeelMatrix/ShutdownSpec/blob/main/PRIVACY.md)

## Supported frameworks

The package targets `net8.0` and `netstandard2.0` and uses `Microsoft.Extensions.Hosting` 10.0.12 for the hosted-service contract.

## License

MIT. See [LICENSE](https://github.com/KeelMatrix/ShutdownSpec/blob/main/LICENSE).
