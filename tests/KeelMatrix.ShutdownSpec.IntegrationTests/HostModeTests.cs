using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace KeelMatrix.ShutdownSpec.IntegrationTests;

public sealed class HostModeTests
{
    [Fact]
    public async Task HostModeRunsRealHostLifecycle()
    {
        var ready = new ShutdownProbe("host-ready");
        var entry = new ShutdownProbe("host-entry");
        var result = await ShutdownHarness
            .For(() => new HostWorker(ready, entry))
            .WithHost(() => Host.CreateDefaultBuilder().ConfigureLogging(logging => logging.ClearProviders()))
            .WithReadinessProbe(ready)
            .WithExecutionProbe(entry)
            .WithStartupDeadline(TimeSpan.FromSeconds(2))
            .WithShutdownDeadline(TimeSpan.FromSeconds(2))
            .WithHarnessDeadline(TimeSpan.FromSeconds(4))
            .RunAsync();

        Assert.Equal(ShutdownOutcome.CleanCompletion, result.Outcome);
        result.ShouldObserve(ready);
        Assert.True(result.StartupCompleted);
        Assert.True(result.StopCompleted);
    }

    [Fact]
    public async Task HostModePreservesStartupFailureCategory()
    {
        var result = await ShutdownHarness
            .For(() => new FailingStartService())
            .WithHost(() => Host.CreateDefaultBuilder().ConfigureLogging(logging => logging.ClearProviders()))
            .WithStartupDeadline(TimeSpan.FromSeconds(1))
            .WithHarnessDeadline(TimeSpan.FromSeconds(2))
            .RunAsync();

        Assert.Equal(ShutdownOutcome.StartupFailure, result.Outcome);
        Assert.Equal(typeof(InvalidOperationException).FullName, result.ExceptionTypeName);
        Assert.False(result.StopInitiated);
    }

    [Fact]
    public async Task HostModeCanStartMultipleRegisteredServices()
    {
        var ready = new ShutdownProbe("primary-ready");
        var entry = new ShutdownProbe("primary-entry");
        var secondary = new ShutdownProbe("secondary-ready");
        var secondaryEntry = new ShutdownProbe("secondary-entry");
        var result = await ShutdownHarness
            .For(() => new HostWorker(ready, entry))
            .WithHost(() => Host.CreateDefaultBuilder()
                .ConfigureLogging(logging => logging.ClearProviders())
                .ConfigureServices(services => services.AddSingleton<IHostedService>(new HostWorker(secondary, secondaryEntry))))
            .WithReadinessProbe(ready)
            .WithExecutionProbe(entry)
            .WithProbe(secondary)
            .WithProbe(secondaryEntry)
            .WithStartupDeadline(TimeSpan.FromSeconds(2))
            .WithShutdownDeadline(TimeSpan.FromSeconds(2))
            .WithHarnessDeadline(TimeSpan.FromSeconds(4))
            .RunAsync();

        Assert.Equal(ShutdownOutcome.CleanCompletion, result.Outcome);
        result.ShouldObserve(ready);
        result.ShouldObserve(secondary);
    }

    private sealed class HostWorker : BackgroundService
    {
        private readonly ShutdownProbe _ready;
        private readonly ShutdownProbe _entry;

        public HostWorker(ShutdownProbe ready, ShutdownProbe entry)
        {
            _ready = ready;
            _entry = entry;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _ready.MarkObserved();
            _entry.MarkObserved();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        }
    }

    private sealed class FailingStartService : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken) => Task.FromException(new InvalidOperationException("synthetic startup failure"));
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
