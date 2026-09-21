# KeelMatrix.ShutdownSpec

**ShutdownSpec turns .NET hosted-service shutdown into a deterministic test contract.** Verify cancellation, bounded completion, faults, and application-owned drain checkpoints for `IHostedService` and `BackgroundService` without building a custom timeout harness in every project.

## Install

```bash
dotnet add package KeelMatrix.ShutdownSpec
```

## Quick Start

Use the direct mode for the shortest path. The package does not require xUnit, NUnit, MSTest, or another test framework.

```csharp
var result = await ShutdownHarness
    .For(() => new Worker())
    .WithStartupDeadline(TimeSpan.FromSeconds(1))
    .WithShutdownDeadline(TimeSpan.FromSeconds(1))
    .RunAsync();

result.ShouldStopWithin(TimeSpan.FromSeconds(1));
result.ShouldCompleteWithoutFault();
```

Register an application-owned checkpoint when the scenario needs to prove a lifecycle event such as queue draining:

```csharp
var drained = new ShutdownProbe("queue-drained");
var result = await ShutdownHarness
    .For(() => new Worker(drained))
    .WithProbe(drained)
    .RunAsync();

result.ShouldObserve(drained);
```

The package reports structured outcomes for startup failure, `StopAsync` faults, execution faults, expected cancellation, caller cancellation, harness deadlines, execution that did not enter before immediate stop, and a service that remains incomplete at the shutdown or harness deadline.

## Host-backed mode

Use host-backed mode when the test needs actual `IHost` registration and orchestration:

```csharp
var result = await ShutdownHarness
    .For(() => new Worker())
    .WithHost(() => Host.CreateDefaultBuilder())
    .RunAsync();
```

Direct mode remains the simplest option and does not require constructing a full host.

## Important limitations

- The harness proves the configured in-process test contract. It does not prove broker, database, container, or orchestrator durability.
- Application code must mark its own readiness, stopping-token, and drain checkpoints. ShutdownSpec does not infer domain state.
- The harness cannot safely interrupt an arbitrary synchronous infinite loop in the same process. Such a scenario is reported as an in-process control limitation after the bounded harness wait; use process isolation for that requirement.
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
