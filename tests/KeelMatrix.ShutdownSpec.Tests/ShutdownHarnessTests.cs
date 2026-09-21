using Microsoft.Extensions.Hosting;
using Xunit;

namespace KeelMatrix.ShutdownSpec.Tests;

public sealed class ShutdownHarnessTests
{
    [Fact]
    public async Task DirectServiceCompletesCleanly()
    {
        var result = await ShutdownHarness
            .For(() => new DirectService())
            .WithStartupDeadline(TimeSpan.FromSeconds(1))
            .WithShutdownDeadline(TimeSpan.FromSeconds(1))
            .WithHarnessDeadline(TimeSpan.FromSeconds(2))
            .RunAsync();

        Assert.Equal(ShutdownOutcome.CleanCompletion, result.Outcome);
        result.ShouldStopWithin(TimeSpan.FromSeconds(1));
        result.ShouldCompleteWithoutFault();
    }

    [Fact]
    public async Task BackgroundServiceReadyProbeCompletesCleanly()
    {
        var ready = new ShutdownProbe("ready");
        var stopping = new ShutdownProbe("stopping-token");
        var result = await ShutdownHarness
            .For(() => new CleanBackgroundService(ready, stopping))
            .WithReadinessProbe(ready)
            .WithProbe(stopping)
            .RunAsync();

        Assert.Equal(ShutdownOutcome.CleanCompletion, result.Outcome);
        result.ShouldObserve(ready);
        result.ShouldObserve(stopping);
        Assert.True(result.ExecutionEntered);
    }

    [Fact]
    public async Task IgnoredCancellationIsServiceNoncompletion()
    {
        var result = await ShutdownHarness
            .For(() => new IgnoredCancellationService())
            .WithStartupDeadline(TimeSpan.FromMilliseconds(100))
            .WithShutdownDeadline(TimeSpan.FromMilliseconds(5))
            .WithHarnessDeadline(TimeSpan.FromMilliseconds(100))
            .WithCleanupDeadline(TimeSpan.FromMilliseconds(20))
            .RunAsync();

        Assert.Equal(ShutdownOutcome.ServiceNoncompletion, result.Outcome);
        Assert.True(result.ShutdownDeadlineFired);
        Assert.False(result.StopCompleted);
        Assert.Contains("KMSHUT101", result.ToDiagnosticString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task EarlyReturningStopWithRunningExecutionIsServiceNoncompletion()
    {
        var result = await ShutdownHarness
            .For(() => new EarlyReturningRunningService())
            .WithShutdownDeadline(TimeSpan.FromMilliseconds(30))
            .WithHarnessDeadline(TimeSpan.FromMilliseconds(200))
            .RunAsync();

        Assert.Equal(ShutdownOutcome.ServiceNoncompletion, result.Outcome);
        Assert.True(result.StopCompleted);
        Assert.Equal(ShutdownExecutionState.Running, result.ExecutionState);
        Assert.True(result.ShutdownDeadlineFired);
        Assert.False(result.HarnessDeadlineFired);
        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task EarlyReturningStopObservationUsesOuterDeadlineProvenance()
    {
        var result = await ShutdownHarness
            .For(() => new EarlyReturningRunningService())
            .WithShutdownDeadline(TimeSpan.FromSeconds(1))
            .WithHarnessDeadline(TimeSpan.FromMilliseconds(100))
            .RunAsync();

        Assert.Equal(ShutdownOutcome.ServiceNoncompletion, result.Outcome);
        Assert.True(result.HarnessDeadlineFired);
        Assert.False(result.ShutdownDeadlineFired);
        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task ExecutionCompletingAfterEarlyStopIsObservedAsClean()
    {
        var result = await ShutdownHarness
            .For(() => new DelayedExecutionCompletionService())
            .WithShutdownDeadline(TimeSpan.FromMilliseconds(200))
            .WithHarnessDeadline(TimeSpan.FromSeconds(1))
            .RunAsync();

        Assert.Equal(ShutdownOutcome.CleanCompletion, result.Outcome);
        Assert.True(result.StopCompleted);
        Assert.Equal(ShutdownExecutionState.Completed, result.ExecutionState);
        Assert.True(result.ExecutionCompleted);
        Assert.True(result.Succeeded);
    }

    [Fact]
    public async Task ExecutionFaultAfterEarlyStopIsReported()
    {
        var result = await ShutdownHarness
            .For(() => new DelayedExecutionFaultService())
            .WithShutdownDeadline(TimeSpan.FromMilliseconds(200))
            .WithHarnessDeadline(TimeSpan.FromSeconds(1))
            .RunAsync();

        Assert.Equal(ShutdownOutcome.ExecutionFault, result.Outcome);
        Assert.True(result.StopCompleted);
        Assert.Equal(ShutdownExecutionState.Faulted, result.ExecutionState);
        Assert.Equal(typeof(InvalidOperationException).FullName, result.ExceptionTypeName);
        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task ExecutionCancellationAfterEarlyStopIsExpectedCancellation()
    {
        var result = await ShutdownHarness
            .For(() => new DelayedExecutionCancellationService())
            .WithShutdownDeadline(TimeSpan.FromMilliseconds(200))
            .WithHarnessDeadline(TimeSpan.FromSeconds(1))
            .RunAsync();

        Assert.Equal(ShutdownOutcome.ExpectedCancellation, result.Outcome);
        Assert.True(result.StopCompleted);
        Assert.Equal(ShutdownExecutionState.Canceled, result.ExecutionState);
        Assert.True(result.ExecutionCanceled);
        Assert.True(result.Succeeded);
    }

    [Fact]
    public async Task StopFaultIsNotClean()
    {
        var result = await ShutdownHarness.For(() => new StopFaultService()).RunAsync();

        Assert.Equal(ShutdownOutcome.StopFault, result.Outcome);
        Assert.False(result.Succeeded);
        Assert.Equal(typeof(InvalidOperationException).FullName, result.ExceptionTypeName);
    }

    [Fact]
    public async Task ExecutionFaultIsNotClean()
    {
        var ready = new ShutdownProbe("faulted");
        var result = await ShutdownHarness
            .For(() => new ExecutionFaultService(ready))
            .WithReadinessProbe(ready)
            .WithStartupDeadline(TimeSpan.FromSeconds(1))
            .WithShutdownDeadline(TimeSpan.FromSeconds(1))
            .RunAsync();

        Assert.Equal(ShutdownOutcome.ExecutionFault, result.Outcome);
        Assert.False(result.Succeeded);
        Assert.Equal(typeof(InvalidOperationException).FullName, result.ExceptionTypeName);
    }

    [Fact]
    public async Task CallerCancellationIsDistinctFromServiceNoncompletion()
    {
        using var callerCancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(10));
        var result = await ShutdownHarness
            .For(() => new HangingStartService())
            .WithStartupDeadline(TimeSpan.FromSeconds(1))
            .WithHarnessDeadline(TimeSpan.FromSeconds(2))
            .RunAsync(callerCancellation.Token);

        Assert.Equal(ShutdownOutcome.CallerCancellation, result.Outcome);
        Assert.True(result.CallerCancellationRequested);
        Assert.False(result.HarnessDeadlineFired);
    }

    [Fact]
    public async Task SynchronousBlockingStartIsBoundedAndNotClean()
    {
        var result = await ShutdownHarness
            .For(() => new SynchronousBlockingStartService())
            .WithStartupDeadline(TimeSpan.FromMilliseconds(10))
            .WithHarnessDeadline(TimeSpan.FromMilliseconds(100))
            .WithCleanupDeadline(TimeSpan.FromMilliseconds(20))
            .RunAsync();

        Assert.Equal(ShutdownOutcome.StartupNoncompletion, result.Outcome);
        Assert.True(result.StartupDeadlineFired);
        Assert.False(result.HarnessDeadlineFired);
        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task ReadinessDeadlineProvenanceIsExclusive()
    {
        var phaseResult = await ShutdownHarness
            .For(new DirectService())
            .WithReadinessProbe(new ShutdownProbe("never-ready"))
            .WithStartupDeadline(TimeSpan.FromMilliseconds(20))
            .WithHarnessDeadline(TimeSpan.FromMilliseconds(200))
            .RunAsync();

        Assert.Equal(ShutdownOutcome.StartupNoncompletion, phaseResult.Outcome);
        Assert.True(phaseResult.StartupDeadlineFired);
        Assert.False(phaseResult.HarnessDeadlineFired);
        Assert.False(phaseResult.ShutdownDeadlineFired);

        var outerResult = await ShutdownHarness
            .For(new DirectService())
            .WithReadinessProbe(new ShutdownProbe("never-ready"))
            .WithStartupDeadline(TimeSpan.FromSeconds(1))
            .WithHarnessDeadline(TimeSpan.FromMilliseconds(20))
            .RunAsync();

        Assert.Equal(ShutdownOutcome.HarnessDeadline, outerResult.Outcome);
        Assert.False(outerResult.StartupDeadlineFired);
        Assert.True(outerResult.HarnessDeadlineFired);
        Assert.False(outerResult.ShutdownDeadlineFired);
    }

    [Fact]
    public async Task OuterDeadlineDuringStopDoesNotClaimShutdownDeadline()
    {
        var result = await ShutdownHarness
            .For(new DelayedStopService())
            .WithStartupDeadline(TimeSpan.FromSeconds(1))
            .WithShutdownDeadline(TimeSpan.FromSeconds(1))
            .WithHarnessDeadline(TimeSpan.FromMilliseconds(20))
            .WithCleanupDeadline(TimeSpan.FromMilliseconds(20))
            .RunAsync();

        Assert.Equal(ShutdownOutcome.ServiceNoncompletion, result.Outcome);
        Assert.True(result.HarnessDeadlineFired);
        Assert.False(result.ShutdownDeadlineFired);
        Assert.True(result.HostShutdownCancellationRequested);
    }

    [Fact]
    public async Task ReusedProbeIsResetBetweenRuns()
    {
        var ready = new ShutdownProbe("ready");
        var first = true;
        var harness = ShutdownHarness
            .For(() => new DirectService(() =>
            {
                if (first) ready.MarkObserved();
                first = false;
            }))
            .WithReadinessProbe(ready)
            .WithStartupDeadline(TimeSpan.FromMilliseconds(50))
            .WithHarnessDeadline(TimeSpan.FromMilliseconds(200));

        var firstResult = await harness.RunAsync();
        var secondResult = await harness.RunAsync();

        Assert.Equal(ShutdownOutcome.CleanCompletion, firstResult.Outcome);
        Assert.Equal(ShutdownOutcome.StartupNoncompletion, secondResult.Outcome);
        Assert.Empty(secondResult.ObservedProbeNames);
        Assert.False(secondResult.Succeeded);
    }

    [Fact]
    public async Task FactoryIsBoundedByOuterDeadlineAndHasDistinctOutcome()
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var result = await ShutdownHarness
            .For(() =>
            {
                Thread.Sleep(TimeSpan.FromMilliseconds(150));
                return new DirectService();
            })
            .WithHarnessDeadline(TimeSpan.FromMilliseconds(20))
            .RunAsync();
        stopwatch.Stop();

        Assert.Equal(ShutdownOutcome.FactoryNoncompletion, result.Outcome);
        Assert.True(result.HarnessDeadlineFired);
        Assert.False(result.StartupCompleted);
        Assert.False(result.Succeeded);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromMilliseconds(120), result.ToDiagnosticString());
    }

    [Fact]
    public async Task CleanupTimeoutIsPrimaryFailure()
    {
        var result = await ShutdownHarness
            .For(new SlowDisposeService())
            .WithCleanupDeadline(TimeSpan.FromMilliseconds(5))
            .RunAsync();

        Assert.Equal(ShutdownOutcome.CleanupNoncompletion, result.Outcome);
        Assert.True(result.CleanupTimedOut);
        Assert.False(result.CleanupCompleted);
        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task UnrelatedStopCancellationIsNotExpectedSuccess()
    {
        var result = await ShutdownHarness.For(new UnrelatedCancellationStopService()).RunAsync();

        Assert.Equal(ShutdownOutcome.StopFault, result.Outcome);
        Assert.False(result.Succeeded);
        Assert.False(result.HostShutdownCancellationRequested);
    }

    [Fact]
    public async Task UnrelatedExecutionCancellationIsNotExpectedSuccess()
    {
        var result = await ShutdownHarness.For(new UnrelatedExecutionCancellationService()).RunAsync();

        Assert.Equal(ShutdownOutcome.ExecutionFault, result.Outcome);
        Assert.True(result.ExecutionCanceled);
        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task ImmediateStartStopNeverReportsUnenteredExecutionAsClean()
    {
        var results = new List<ShutdownResult>();
        for (var i = 0; i < 100; i++)
        {
            results.Add(await ShutdownHarness.For(() => new ImmediateBackgroundService()).RunAsync());
        }

        Assert.All(results, result => Assert.True(result.ExecutionEntered || result.Outcome != ShutdownOutcome.CleanCompletion));
    }

    [Fact]
    public async Task ApplicationProbeIsReportedAndAssertable()
    {
        var drained = new ShutdownProbe("queue-drained");
        var result = await ShutdownHarness
            .For(() => new DirectService(() => drained.MarkObserved()))
            .WithProbe(drained)
            .RunAsync();

        result.ShouldObserve(drained);
        Assert.Contains("queue-drained", result.ObservedProbeNames);
    }

    [Fact]
    public async Task DiagnosticReportDoesNotDumpExceptionMessage()
    {
        const string secret = "test-secret-value";
        var result = await ShutdownHarness.For(() => new StopFaultService(secret)).RunAsync();

        Assert.Equal(ShutdownOutcome.StopFault, result.Outcome);
        Assert.Equal(secret, result.ExceptionMessage);
        Assert.DoesNotContain(secret, result.ToDiagnosticString(), StringComparison.Ordinal);
    }

    [Fact]
    public void InvalidDeadlinesAreRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ShutdownHarness.For(new DirectService()).WithShutdownDeadline(TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => ShutdownHarness.For(new DirectService()).WithHarnessDeadline(Timeout.InfiniteTimeSpan));
    }

    [Fact]
    public void ShippingAssemblyHasNoNetworkOrTelemetryReference()
    {
        var references = typeof(ShutdownHarness).Assembly.GetReferencedAssemblies().Select(assembly => assembly.Name).ToArray();

        Assert.DoesNotContain("System.Net.Http", references);
        Assert.DoesNotContain(references, name => name is not null && name.Contains("Telemetry", StringComparison.OrdinalIgnoreCase));
    }

    private sealed class DirectService : IHostedService
    {
        private readonly Action? _onStart;

        public DirectService(Action? onStart = null) => _onStart = onStart;

        public Task StartAsync(CancellationToken cancellationToken)
        {
            _onStart?.Invoke();
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class CleanBackgroundService : BackgroundService
    {
        private readonly ShutdownProbe _ready;
        private readonly ShutdownProbe _stopping;

        public CleanBackgroundService(ShutdownProbe ready, ShutdownProbe stopping)
        {
            _ready = ready;
            _stopping = stopping;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _ready.MarkObserved();
            using var registration = _stopping.ObserveCancellation(stoppingToken);
            try { await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        }
    }

    private sealed class ImmediateBackgroundService : BackgroundService
    {
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            try { await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        }
    }

    private sealed class IgnoredCancellationService : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken) => Task.Delay(TimeSpan.FromSeconds(1), CancellationToken.None);
    }

    private sealed class EarlyReturningRunningService : BackgroundService
    {
        public override Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.Delay(Timeout.InfiniteTimeSpan, CancellationToken.None);
    }

    private sealed class DelayedExecutionCompletionService : BackgroundService
    {
        private readonly TaskCompletionSource<bool> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override Task StopAsync(CancellationToken cancellationToken)
        {
            _ = CompleteLaterAsync();
            return Task.CompletedTask;
        }

        protected override Task ExecuteAsync(CancellationToken stoppingToken) => _completion.Task;

        private async Task CompleteLaterAsync()
        {
            await Task.Delay(TimeSpan.FromMilliseconds(20));
            _completion.TrySetResult(true);
        }
    }

    private sealed class DelayedExecutionFaultService : BackgroundService
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

    private sealed class DelayedExecutionCancellationService : BackgroundService
    {
        public override Task StopAsync(CancellationToken cancellationToken)
        {
            _ = StopThroughBaseLaterAsync(cancellationToken);
            return Task.CompletedTask;
        }

        protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken);

        private async Task StopThroughBaseLaterAsync(CancellationToken cancellationToken)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(20), CancellationToken.None);
            await base.StopAsync(cancellationToken);
        }
    }

    private sealed class StopFaultService : IHostedService
    {
        private readonly string _message;

        public StopFaultService(string message = "synthetic stop fault") => _message = message;
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken) => Task.FromException(new InvalidOperationException(_message));
    }

    private sealed class ExecutionFaultService : BackgroundService
    {
        private readonly ShutdownProbe _ready;
        private readonly TaskCompletionSource<bool> _faulted = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ExecutionFaultService(ShutdownProbe ready) => _ready = ready;

        public override async Task StartAsync(CancellationToken cancellationToken)
        {
            await base.StartAsync(cancellationToken);
            await _faulted.Task;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _ready.MarkObserved();
            await Task.Yield();
            _faulted.TrySetResult(true);
            throw new InvalidOperationException("synthetic execution fault");
        }
    }

    private sealed class DelayedStopService : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken) => Task.Delay(TimeSpan.FromSeconds(1), CancellationToken.None);
    }

    private sealed class SlowDisposeService : IHostedService, IDisposable
    {
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public void Dispose() => Thread.Sleep(TimeSpan.FromMilliseconds(100));
    }

    private sealed class UnrelatedCancellationStopService : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken)
        {
            using var unrelated = new CancellationTokenSource();
            unrelated.Cancel();
            return Task.FromCanceled(unrelated.Token);
        }
    }

    private sealed class UnrelatedExecutionCancellationService : BackgroundService
    {
        public override async Task StartAsync(CancellationToken cancellationToken)
        {
            await base.StartAsync(cancellationToken);
            try { await ExecuteTask!; }
            catch (OperationCanceledException) { }
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            await Task.Yield();
            using var unrelated = new CancellationTokenSource();
            unrelated.Cancel();
            await Task.FromCanceled(unrelated.Token);
        }
    }

    private sealed class HangingStartService : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken) => Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class SynchronousBlockingStartService : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken)
        {
            Thread.Sleep(TimeSpan.FromMilliseconds(100));
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
