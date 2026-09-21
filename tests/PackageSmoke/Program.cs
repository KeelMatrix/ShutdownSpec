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

var earlyReturn = await ShutdownHarness
    .For(() => new EarlyReturningRunningService())
    .WithShutdownDeadline(TimeSpan.FromMilliseconds(20))
    .WithHarnessDeadline(TimeSpan.FromMilliseconds(200))
    .RunAsync();

if (earlyReturn.Outcome != ShutdownOutcome.ServiceNoncompletion
    || !earlyReturn.StopCompleted
    || earlyReturn.ExecutionState != ShutdownExecutionState.Running
    || !earlyReturn.ShutdownDeadlineFired
    || earlyReturn.HarnessDeadlineFired
    || earlyReturn.Succeeded)
{
    throw new InvalidOperationException($"Unexpected early-return classification: {earlyReturn.ToDiagnosticString()}");
}

try
{
    earlyReturn.ShouldStopWithin(TimeSpan.FromSeconds(1));
    throw new InvalidOperationException("ShouldStopWithin accepted an unfinished execution task.");
}
catch (ShutdownAssertionException)
{
}

var delayedEntry = new ShutdownProbe("execution-entered");
var delayedCompletion = await ShutdownHarness
    .For(() => new DelayedExecutionCompletionService(delayedEntry))
    .WithExecutionProbe(delayedEntry)
    .WithShutdownDeadline(TimeSpan.FromSeconds(1))
    .WithHarnessDeadline(TimeSpan.FromSeconds(2))
    .RunAsync();

if (delayedCompletion.Outcome != ShutdownOutcome.CleanCompletion || !delayedCompletion.ExecutionCompleted)
{
    throw new InvalidOperationException($"Unexpected delayed-completion classification: {delayedCompletion.ToDiagnosticString()}");
}

var delayedFault = await ShutdownHarness
    .For(() => new DelayedExecutionFaultService())
    .WithShutdownDeadline(TimeSpan.FromSeconds(1))
    .WithHarnessDeadline(TimeSpan.FromSeconds(2))
    .RunAsync();

if (delayedFault.Outcome != ShutdownOutcome.ExecutionFault || delayedFault.Succeeded)
{
    throw new InvalidOperationException($"Unexpected delayed-fault classification: {delayedFault.ToDiagnosticString()}");
}

var delayedEntryCancellation = new ShutdownProbe("execution-entered");
var delayedStoppingCancellation = new ShutdownProbe("stopping-token");
var delayedCancellation = await ShutdownHarness
    .For(() => new DelayedExecutionCancellationService(delayedEntryCancellation, delayedStoppingCancellation))
    .WithExecutionProbe(delayedEntryCancellation)
    .WithStoppingProbe(delayedStoppingCancellation)
    .WithProbe(delayedStoppingCancellation)
    .WithShutdownDeadline(TimeSpan.FromSeconds(1))
    .WithHarnessDeadline(TimeSpan.FromSeconds(2))
    .RunAsync();

if (delayedCancellation.Outcome != ShutdownOutcome.ExpectedCancellation || !delayedCancellation.Succeeded)
{
    throw new InvalidOperationException($"Unexpected delayed-cancellation classification: {delayedCancellation.ToDiagnosticString()}");
}

Console.WriteLine($"clean={clean.Outcome}; broken={broken.Outcome}; earlyReturn={earlyReturn.Outcome}; delayedCompletion={delayedCompletion.Outcome}; delayedFault={delayedFault.Outcome}; delayedCancellation={delayedCancellation.Outcome}");

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

sealed class EarlyReturningRunningService : BackgroundService
{
    public override Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.Delay(Timeout.InfiniteTimeSpan, CancellationToken.None);
}

sealed class DelayedExecutionCompletionService : BackgroundService
{
    private readonly TaskCompletionSource<bool> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ShutdownProbe _entry;

    public DelayedExecutionCompletionService(ShutdownProbe entry) => _entry = entry;

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        _ = CompleteLaterAsync();
        return Task.CompletedTask;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _entry.MarkObserved();
        return _completion.Task;
    }

    private async Task CompleteLaterAsync()
    {
        await Task.Delay(TimeSpan.FromMilliseconds(20));
        _completion.TrySetResult(true);
    }
}

sealed class DelayedExecutionFaultService : BackgroundService
{
    private readonly TaskCompletionSource<bool> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        _ = FaultLaterAsync();
        return Task.CompletedTask;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken) => _completion.Task;

    private async Task FaultLaterAsync()
    {
        await Task.Delay(TimeSpan.FromMilliseconds(20));
        _completion.TrySetException(new InvalidOperationException("synthetic delayed execution fault"));
    }
}

sealed class DelayedExecutionCancellationService : BackgroundService
{
    private readonly ShutdownProbe _entry;
    private readonly ShutdownProbe _stopping;

    public DelayedExecutionCancellationService(ShutdownProbe entry, ShutdownProbe stopping)
    {
        _entry = entry;
        _stopping = stopping;
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        _ = StopThroughBaseLaterAsync(cancellationToken);
        return Task.CompletedTask;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _entry.MarkObserved();
        _ = _stopping.ObserveCancellation(stoppingToken);
        return Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken);
    }

    private async Task StopThroughBaseLaterAsync(CancellationToken cancellationToken)
    {
        await Task.Delay(TimeSpan.FromMilliseconds(20), CancellationToken.None);
        await base.StopAsync(cancellationToken);
    }
}
