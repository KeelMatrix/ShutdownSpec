using KeelMatrix.ShutdownSpec;
using Microsoft.Extensions.Hosting;

var ready = new ShutdownProbe("ready");
var clean = await ShutdownHarness
    .For(() => new Worker(ready))
    .WithReadinessProbe(ready)
    .WithStartupDeadline(TimeSpan.FromSeconds(1))
    .WithShutdownDeadline(TimeSpan.FromSeconds(1))
    .WithHarnessDeadline(TimeSpan.FromSeconds(2))
    .RunAsync();

clean.ShouldStopWithin(TimeSpan.FromSeconds(1));
clean.ShouldCompleteWithoutFault();

var broken = await ShutdownHarness
    .For(() => new IgnoredCancellationService())
    .WithStartupDeadline(TimeSpan.FromMilliseconds(100))
    .WithShutdownDeadline(TimeSpan.FromMilliseconds(20))
    .WithHarnessDeadline(TimeSpan.FromMilliseconds(200))
    .WithCleanupDeadline(TimeSpan.FromMilliseconds(50))
    .RunAsync();

if (broken.Outcome != ShutdownOutcome.ServiceNoncompletion || !broken.ShutdownDeadlineFired || broken.Succeeded)
{
    throw new InvalidOperationException($"Unexpected broken-service classification: {broken.ToDiagnosticString()}");
}

Console.WriteLine($"clean={clean.Outcome}; broken={broken.Outcome}; diagnostic={broken.ToDiagnosticString()}");

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

sealed class IgnoredCancellationService : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.Delay(TimeSpan.FromSeconds(5), CancellationToken.None);
}
