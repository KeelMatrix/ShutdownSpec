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
            .WithExecutionProbe(ready)
            .WithStoppingProbe(stopping)
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
        var entry = new ShutdownProbe("execution-entered");
        var result = await ShutdownHarness
            .For(() => new DelayedExecutionCompletionService(entry))
            .WithExecutionProbe(entry)
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
        var entry = new ShutdownProbe("execution-entered");
        var stopping = new ShutdownProbe("stopping-token");
        var result = await ShutdownHarness
            .For(() => new DelayedExecutionCancellationService(entry, stopping))
            .WithExecutionProbe(entry)
            .WithStoppingProbe(stopping)
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
                Thread.Sleep(TimeSpan.FromMilliseconds(500));
                return new DirectService();
            })
            .WithHarnessDeadline(TimeSpan.FromMilliseconds(20))
            .RunAsync();
        stopwatch.Stop();

        Assert.Equal(ShutdownOutcome.FactoryNoncompletion, result.Outcome);
        Assert.True(result.HarnessDeadlineFired);
        Assert.False(result.StartupCompleted);
        Assert.False(result.Succeeded);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromMilliseconds(300), result.ToDiagnosticString());
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

    [Fact(Timeout = 3000)]
    public async Task BlockingStartupCancellationCallbackCannotTrapRunAsync()
    {
        var service = new BlockingStartupCancellationService();
        var resultTask = ShutdownHarness
            .For(service)
            .WithStartupDeadline(TimeSpan.FromMilliseconds(10))
            .WithHarnessDeadline(TimeSpan.FromMilliseconds(100))
            .WithCleanupDeadline(TimeSpan.FromMilliseconds(10))
            .RunAsync();

        var completed = await Task.WhenAny(resultTask, Task.Delay(TimeSpan.FromSeconds(1)));
        Assert.Same(resultTask, completed);
        var result = await resultTask;
        service.Release();
        Assert.Equal(ShutdownOutcome.StartupNoncompletion, result.Outcome);
        Assert.True(result.StartupCancellationRequested);
    }

    [Fact(Timeout = 3000)]
    public async Task ThrowingStartupCancellationCallbackCannotChangeDeadlineClassification()
    {
        var result = await ShutdownHarness
            .For(new ThrowingStartupCancellationService())
            .WithStartupDeadline(TimeSpan.FromMilliseconds(10))
            .WithHarnessDeadline(TimeSpan.FromMilliseconds(100))
            .RunAsync();

        Assert.Equal(ShutdownOutcome.StartupNoncompletion, result.Outcome);
        Assert.True(result.StartupCancellationRequested);
        Assert.True(result.CancellationNotificationFaulted || result.CancellationNotificationPending);
    }

    [Fact(Timeout = 3000)]
    public async Task BlockingShutdownCancellationCallbackCannotTrapRunAsync()
    {
        var service = new BlockingShutdownCancellationService();
        var resultTask = ShutdownHarness
            .For(service)
            .WithShutdownDeadline(TimeSpan.FromMilliseconds(10))
            .WithHarnessDeadline(TimeSpan.FromMilliseconds(100))
            .WithCleanupDeadline(TimeSpan.FromMilliseconds(10))
            .RunAsync();

        var completed = await Task.WhenAny(resultTask, Task.Delay(TimeSpan.FromSeconds(1)));
        Assert.Same(resultTask, completed);
        var result = await resultTask;
        service.Release();
        Assert.Equal(ShutdownOutcome.ServiceNoncompletion, result.Outcome);
        Assert.True(result.HostShutdownCancellationRequested);
    }

    [Fact(Timeout = 3000)]
    public async Task ThrowingShutdownCancellationCallbackCannotChangeDeadlineClassification()
    {
        var result = await ShutdownHarness
            .For(new ThrowingShutdownCancellationService())
            .WithShutdownDeadline(TimeSpan.FromMilliseconds(10))
            .WithHarnessDeadline(TimeSpan.FromMilliseconds(100))
            .WithCleanupDeadline(TimeSpan.FromMilliseconds(20))
            .RunAsync();

        Assert.Equal(ShutdownOutcome.ServiceNoncompletion, result.Outcome);
        Assert.True(result.HostShutdownCancellationRequested);
        Assert.True(result.CancellationNotificationFaulted || result.CancellationNotificationPending);
    }

    [Fact]
    public async Task UnrelatedCancellationAfterStopIsNotExpectedCancellation()
    {
        var entry = new ShutdownProbe("execution-entered");
        var stopping = new ShutdownProbe("stopping-token");
        var result = await ShutdownHarness
            .For(() => new UnrelatedCancellationAfterStopService(entry, stopping))
            .WithExecutionProbe(entry)
            .WithStoppingProbe(stopping)
            .WithShutdownDeadline(TimeSpan.FromMilliseconds(200))
            .RunAsync();

        Assert.Equal(ShutdownOutcome.ExecutionCancellationUnverified, result.Outcome);
        Assert.True(result.ExecutionEntered);
        Assert.False(result.StoppingTokenObserved);
        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task CancellationBeforeExecutionCheckpointIsNotExpectedCancellation()
    {
        var entry = new ShutdownProbe("execution-entered");
        var result = await ShutdownHarness
            .For(() => new CanceledBeforeExecutionService(entry))
            .WithExecutionProbe(entry)
            .WithShutdownDeadline(TimeSpan.FromMilliseconds(200))
            .RunAsync();

        Assert.Equal(ShutdownOutcome.ExecutionNotStarted, result.Outcome);
        Assert.False(result.ExecutionEntered);
        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task ShouldStopWithinRejectsAnUnfinishedExecution()
    {
        var result = await ShutdownHarness
            .For(() => new EarlyReturningRunningService())
            .WithShutdownDeadline(TimeSpan.FromMilliseconds(20))
            .WithHarnessDeadline(TimeSpan.FromMilliseconds(200))
            .RunAsync();

        Assert.Throws<ShutdownAssertionException>(() => result.ShouldStopWithin(TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task HostModeDisposesSuppliedSubjectExactlyOnce()
    {
        var service = new DisposableHostService();
        var result = await ShutdownHarness
            .For(service)
            .WithHost(() => Host.CreateDefaultBuilder())
            .WithShutdownDeadline(TimeSpan.FromSeconds(1))
            .RunAsync();

        Assert.Equal(ShutdownOutcome.CleanCompletion, result.Outcome);
        Assert.Equal(1, service.DisposeCount);
    }

    [Fact]
    public async Task ReadinessFailureMakesBoundedBestEffortStopAttempt()
    {
        var service = new PartiallyStartedService();
        var result = await ShutdownHarness
            .For(service)
            .WithReadinessProbe(new ShutdownProbe("never-ready"))
            .WithStartupDeadline(TimeSpan.FromMilliseconds(20))
            .WithCleanupDeadline(TimeSpan.FromMilliseconds(200))
            .RunAsync();

        Assert.Equal(ShutdownOutcome.StartupNoncompletion, result.Outcome);
        Assert.Equal(1, service.StopCount);
    }

    [Fact(Timeout = 3000)]
    public async Task LateFactoryResultIsDisposedAfterBoundedReturn()
    {
        var service = new DisposableHostService();
        var releaseFactory = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var result = await ShutdownHarness
            .For(() =>
            {
                releaseFactory.Task.GetAwaiter().GetResult();
                return service;
            })
            .WithHarnessDeadline(TimeSpan.FromMilliseconds(10))
            .WithCleanupDeadline(TimeSpan.FromMilliseconds(10))
            .RunAsync();

        Assert.Equal(ShutdownOutcome.FactoryNoncompletion, result.Outcome);
        releaseFactory.SetResult(true);
        var disposed = await Task.WhenAny(service.Disposed, Task.Delay(TimeSpan.FromSeconds(2)));
        Assert.Same(service.Disposed, disposed);
        Assert.Equal(1, service.DisposeCount);
    }

    [Fact(Timeout = 3000)]
    public async Task LateHostConstructionDisposesSubjectEventually()
    {
        var service = new DisposableHostService();
        var result = await ShutdownHarness
            .For(service)
            .WithHost(() =>
            {
                Thread.Sleep(100);
                return Host.CreateDefaultBuilder();
            })
            .WithStartupDeadline(TimeSpan.FromMilliseconds(10))
            .WithCleanupDeadline(TimeSpan.FromMilliseconds(10))
            .RunAsync();

        Assert.Equal(ShutdownOutcome.StartupNoncompletion, result.Outcome);
        await Task.Delay(1000);
        Assert.Equal(1, service.DisposeCount);
    }

    [Fact]
    public async Task PrimaryFailureAndCleanupFailureRemainSeparate()
    {
        var result = await ShutdownHarness
            .For(new StopAndDisposeFaultService())
            .RunAsync();

        Assert.Equal(ShutdownOutcome.StopFault, result.Outcome);
        Assert.Equal(ShutdownOutcome.StopFault, result.PrimaryOutcome);
        Assert.Equal(ShutdownOutcome.CleanupFailure, result.CleanupOutcome);
        Assert.Equal(typeof(InvalidOperationException).FullName, result.ExceptionTypeName);
        Assert.Equal(typeof(CleanupException).FullName, result.CleanupExceptionTypeName);
        Assert.Equal(ShutdownPhase.Stopping, result.Phase);
        Assert.True(result.HasUnexpectedFault);
    }

    [Fact]
    public async Task ReadinessWaitIsIncludedInStartupDurationAndDiagnostics()
    {
        var result = await ShutdownHarness
            .For(new DirectService())
            .WithReadinessProbe(new ShutdownProbe("never-ready"))
            .WithStartupDeadline(TimeSpan.FromMilliseconds(30))
            .WithHarnessDeadline(TimeSpan.FromMilliseconds(200))
            .RunAsync();

        Assert.Equal(ShutdownOutcome.StartupNoncompletion, result.Outcome);
        Assert.True(result.StartupDuration >= TimeSpan.FromMilliseconds(20), result.ToDiagnosticString());
        Assert.Contains("Startup duration:", result.ToDiagnosticString(), StringComparison.Ordinal);
        Assert.Equal(ShutdownPhase.Started, result.Phase);
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
            var entry = new ShutdownProbe("execution-entered");
            results.Add(await ShutdownHarness.For(() => new ImmediateBackgroundService(entry)).WithExecutionProbe(entry).RunAsync());
        }

        Assert.All(results, result => Assert.True(result.ExecutionEntered || result.Outcome == ShutdownOutcome.ExecutionNotStarted, result.ToDiagnosticString()));
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
        private readonly ShutdownProbe _entry;

        public ImmediateBackgroundService(ShutdownProbe entry) => _entry = entry;

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _entry.MarkObserved();
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

    private sealed class BlockingStartupCancellationService : IHostedService
    {
        private readonly TaskCompletionSource<bool> _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task StartAsync(CancellationToken cancellationToken)
        {
            cancellationToken.Register(() => _gate.Task.GetAwaiter().GetResult());
            return Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public void Release() => _gate.TrySetResult(true);
    }

    private sealed class ThrowingStartupCancellationService : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken)
        {
            cancellationToken.Register(() => throw new InvalidOperationException("startup callback failure"));
            return Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class BlockingShutdownCancellationService : IHostedService
    {
        private readonly TaskCompletionSource<bool> _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task StartAsync(CancellationToken cancellationToken)
        {
            cancellationToken.Register(() => _gate.Task.GetAwaiter().GetResult());
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.Delay(Timeout.InfiniteTimeSpan, CancellationToken.None);

        public void Release() => _gate.TrySetResult(true);
    }

    private sealed class ThrowingShutdownCancellationService : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken)
        {
            cancellationToken.Register(() => throw new InvalidOperationException("shutdown callback failure"));
            return Task.Delay(Timeout.InfiniteTimeSpan, CancellationToken.None);
        }
    }

    private sealed class UnrelatedCancellationAfterStopService : BackgroundService
    {
        private readonly ShutdownProbe _entry;
        private readonly ShutdownProbe _stopping;
        private readonly TaskCompletionSource<bool> _stopStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public UnrelatedCancellationAfterStopService(ShutdownProbe entry, ShutdownProbe stopping)
        {
            _entry = entry;
            _stopping = stopping;
        }

        public override Task StopAsync(CancellationToken cancellationToken)
        {
            _stopStarted.TrySetResult(true);
            return Task.CompletedTask;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _entry.MarkObserved();
            _ = _stopping.ObserveCancellation(stoppingToken);
            await _stopStarted.Task;
            using var unrelated = new CancellationTokenSource();
            unrelated.Cancel();
            await Task.FromCanceled(unrelated.Token);
        }
    }

    private sealed class CanceledBeforeExecutionService : BackgroundService
    {
        public CanceledBeforeExecutionService(ShutdownProbe entry) { }

        protected override Task ExecuteAsync(CancellationToken stoppingToken)
        {
            return Task.FromCanceled(new CancellationToken(true));
        }
    }

    private sealed class DisposableHostService : IHostedService, IDisposable
    {
        private readonly TaskCompletionSource<bool> _disposed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int DisposeCount { get; private set; }
        public Task Disposed => _disposed.Task;
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public void Dispose()
        {
            DisposeCount++;
            _disposed.TrySetResult(true);
        }
    }

    private sealed class PartiallyStartedService : IHostedService
    {
        public int StopCount { get; private set; }
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken)
        {
            StopCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class StopAndDisposeFaultService : IHostedService, IDisposable
    {
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken) => Task.FromException(new InvalidOperationException("stop failure"));
        public void Dispose() => throw new CleanupException();
    }

    private sealed class CleanupException : Exception
    {
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
