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
        var result = await ShutdownHarness
            .For(() => new ExecutionFaultService())
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

        Assert.Equal(ShutdownOutcome.HarnessDeadline, result.Outcome);
        Assert.True(result.StartupDeadlineFired);
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

    private sealed class StopFaultService : IHostedService
    {
        private readonly string _message;

        public StopFaultService(string message = "synthetic stop fault") => _message = message;
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken) => Task.FromException(new InvalidOperationException(_message));
    }

    private sealed class ExecutionFaultService : BackgroundService
    {
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            await Task.Yield();
            throw new InvalidOperationException("synthetic execution fault");
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
