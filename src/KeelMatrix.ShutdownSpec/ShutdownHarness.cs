using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace KeelMatrix.ShutdownSpec;

/// <summary>Builds and runs a bounded hosted-service shutdown scenario.</summary>
public sealed class ShutdownHarness
{
    private static readonly TimeSpan DefaultStartupDeadline = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan DefaultShutdownDeadline = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan DefaultHarnessDeadline = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan DefaultCleanupDeadline = TimeSpan.FromSeconds(1);

    private readonly Func<IHostedService> _factory;
    private readonly List<ShutdownProbe> _probes = new();
    private Func<IHostBuilder>? _hostBuilderFactory;
    private ShutdownProbe? _readinessProbe;
    private TimeSpan _startupDeadline = DefaultStartupDeadline;
    private TimeSpan _shutdownDeadline = DefaultShutdownDeadline;
    private TimeSpan _harnessDeadline = DefaultHarnessDeadline;
    private TimeSpan _cleanupDeadline = DefaultCleanupDeadline;

    private ShutdownHarness(Func<IHostedService> factory)
    {
        _factory = factory;
    }

    /// <summary>Creates a harness for one already-created service instance.</summary>
    /// <param name="service">The service instance to start and stop once.</param>
    /// <returns>A configurable shutdown harness.</returns>
    public static ShutdownHarness For(IHostedService service)
    {
        if (service is null) throw new ArgumentNullException(nameof(service));
        return new ShutdownHarness(() => service);
    }

    /// <summary>Creates a harness that creates a fresh service instance for each run.</summary>
    /// <param name="factory">The factory used to create the service.</param>
    /// <returns>A configurable shutdown harness.</returns>
    public static ShutdownHarness For(Func<IHostedService> factory)
    {
        if (factory is null) throw new ArgumentNullException(nameof(factory));
        return new ShutdownHarness(factory);
    }

    /// <summary>Uses a real host builder to run the service through host registration and orchestration.</summary>
    /// <param name="hostBuilderFactory">A factory that returns an unbuilt host builder.</param>
    /// <returns>This harness.</returns>
    public ShutdownHarness WithHost(Func<IHostBuilder> hostBuilderFactory)
    {
        if (hostBuilderFactory is null) throw new ArgumentNullException(nameof(hostBuilderFactory));
        _hostBuilderFactory = hostBuilderFactory;
        return this;
    }

    /// <summary>Sets the maximum startup phase duration.</summary>
    /// <param name="deadline">A positive startup bound.</param>
    /// <returns>This harness.</returns>
    public ShutdownHarness WithStartupDeadline(TimeSpan deadline)
    {
        _startupDeadline = ValidateDeadline(deadline, nameof(deadline));
        return this;
    }

    /// <summary>Sets the maximum graceful shutdown duration.</summary>
    /// <param name="deadline">A positive shutdown bound.</param>
    /// <returns>This harness.</returns>
    public ShutdownHarness WithShutdownDeadline(TimeSpan deadline)
    {
        _shutdownDeadline = ValidateDeadline(deadline, nameof(deadline));
        return this;
    }

    /// <summary>Sets the outer safety bound for the entire scenario.</summary>
    /// <param name="deadline">A positive outer bound.</param>
    /// <returns>This harness.</returns>
    public ShutdownHarness WithHarnessDeadline(TimeSpan deadline)
    {
        _harnessDeadline = ValidateDeadline(deadline, nameof(deadline));
        return this;
    }

    /// <summary>Sets the maximum cleanup wait after a failure or cancellation.</summary>
    /// <param name="deadline">A positive cleanup bound.</param>
    /// <returns>This harness.</returns>
    public ShutdownHarness WithCleanupDeadline(TimeSpan deadline)
    {
        _cleanupDeadline = ValidateDeadline(deadline, nameof(deadline));
        return this;
    }

    /// <summary>Registers an application-owned probe for result observation.</summary>
    /// <param name="probe">The probe to record.</param>
    /// <returns>This harness.</returns>
    public ShutdownHarness WithProbe(ShutdownProbe probe)
    {
        if (probe is null) throw new ArgumentNullException(nameof(probe));
        if (!_probes.Contains(probe)) _probes.Add(probe);
        return this;
    }

    /// <summary>Registers a probe that must be observed before graceful shutdown begins.</summary>
    /// <param name="probe">The application-owned readiness probe.</param>
    /// <returns>This harness.</returns>
    public ShutdownHarness WithReadinessProbe(ShutdownProbe probe)
    {
        WithProbe(probe);
        _readinessProbe = probe;
        return this;
    }

    /// <summary>Runs the scenario and returns a structured result instead of a framework-specific assertion.</summary>
    /// <param name="cancellationToken">A caller-owned cancellation token for the harness wait.</param>
    /// <returns>The bounded lifecycle result.</returns>
    public async Task<ShutdownResult> RunAsync(CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var phase = ShutdownPhase.NotStarted;
        var outcome = ShutdownOutcome.CleanCompletion;
        var startupCompleted = false;
        var executionEntered = false;
        var stopInitiated = false;
        var stopCompleted = false;
        var executionCompleted = false;
        var executionCanceled = false;
        var harnessDeadlineFired = false;
        var startupDeadlineFired = false;
        var shutdownDeadlineFired = false;
        var hostShutdownCancellationRequested = false;
        var executionState = ShutdownExecutionState.NotObserved;
        var exceptionTypeName = (string?)null;
        var exceptionMessage = (string?)null;
        var service = (IHostedService?)null;
        var host = (IHost?)null;
        var executionTask = (Task?)null;
        var cleanupCompleted = false;
        var cleanupTimedOut = false;
        var startupDuration = TimeSpan.Zero;
        var shutdownDuration = TimeSpan.Zero;
        var cleanupDuration = TimeSpan.Zero;

        try
        {
            phase = ShutdownPhase.Creating;
            service = _factory();
            if (service is null) throw new InvalidOperationException("The service factory returned null.");

            phase = ShutdownPhase.Starting;
            var startupClock = Stopwatch.StartNew();
            using var startupCancellation = new CancellationTokenSource();
            var startTask = Task.Run(async () =>
            {
                if (_hostBuilderFactory is not null)
                {
                    var builder = _hostBuilderFactory();
                    if (builder is null) throw new InvalidOperationException("The host builder factory returned null.");
                    builder.ConfigureServices((_, services) => services.AddSingleton(typeof(IHostedService), service));
                    host = builder.Build();
                    await host.StartAsync(startupCancellation.Token).ConfigureAwait(false);
                }
                else
                {
                    await service.StartAsync(startupCancellation.Token).ConfigureAwait(false);
                }
            }, CancellationToken.None);

            var startWait = await WaitForOperationAsync(startTask, _startupDeadline, stopwatch, cancellationToken).ConfigureAwait(false);
            startupDuration = startupClock.Elapsed;
            if (startWait != WaitReason.Completed)
            {
                startupDeadlineFired = startWait == WaitReason.PhaseDeadline;
                harnessDeadlineFired = startWait == WaitReason.HarnessDeadline;
                startupCancellation.Cancel();
                outcome = startWait == WaitReason.CallerCancellation ? ShutdownOutcome.CallerCancellation : ShutdownOutcome.HarnessDeadline;
                if (startWait != WaitReason.Completed) ObserveFault(startTask);
            }
            else
            {
                try
                {
                    await startTask.ConfigureAwait(false);
                    startupCompleted = true;
                    phase = ShutdownPhase.Started;
                }
                catch (Exception exception)
                {
                    outcome = ShutdownOutcome.StartupFailure;
                    exceptionTypeName = exception.GetType().FullName;
                    exceptionMessage = exception.Message;
                }
            }

            if (outcome == ShutdownOutcome.CleanCompletion && startupCompleted)
            {
                executionTask = GetExecutionTask(service);
                RefreshExecutionState(executionTask, ref executionState, ref executionEntered, ref executionCompleted, ref executionCanceled, ref exceptionTypeName, ref exceptionMessage);

                if (_readinessProbe is not null)
                {
                    var remainingStartup = _startupDeadline - startupDuration;
                    if (remainingStartup <= TimeSpan.Zero || !await WaitForProbeAsync(_readinessProbe, remainingStartup, stopwatch, cancellationToken).ConfigureAwait(false))
                    {
                        startupDeadlineFired = true;
                        startupCancellation.Cancel();
                        outcome = cancellationToken.IsCancellationRequested ? ShutdownOutcome.CallerCancellation : ShutdownOutcome.HarnessDeadline;
                    }
                }
            }

            if (outcome == ShutdownOutcome.CleanCompletion && startupCompleted)
            {
                phase = ShutdownPhase.Stopping;
                stopInitiated = true;
                var shutdownClock = Stopwatch.StartNew();
                using var shutdownCancellation = new CancellationTokenSource();
                var stopTask = Task.Run(async () =>
                {
                    if (host is not null)
                    {
                        await host.StopAsync(shutdownCancellation.Token).ConfigureAwait(false);
                    }
                    else
                    {
                        await service.StopAsync(shutdownCancellation.Token).ConfigureAwait(false);
                    }
                }, CancellationToken.None);

                var stopWait = await WaitForOperationAsync(stopTask, _shutdownDeadline, stopwatch, cancellationToken).ConfigureAwait(false);
                shutdownDuration = shutdownClock.Elapsed;
                hostShutdownCancellationRequested = shutdownCancellation.IsCancellationRequested;
                shutdownDeadlineFired = hostShutdownCancellationRequested;
                if (stopWait != WaitReason.Completed)
                {
                    shutdownCancellation.Cancel();
                    hostShutdownCancellationRequested = shutdownCancellation.IsCancellationRequested;
                    shutdownDeadlineFired = stopWait == WaitReason.PhaseDeadline || hostShutdownCancellationRequested;
                    harnessDeadlineFired = stopWait == WaitReason.HarnessDeadline;
                    outcome = stopWait == WaitReason.CallerCancellation ? ShutdownOutcome.CallerCancellation : ShutdownOutcome.ServiceNoncompletion;
                    ObserveFault(stopTask);
                }
                else
                {
                    stopCompleted = true;
                    try
                    {
                        await stopTask.ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        outcome = ShutdownOutcome.ExpectedCancellation;
                    }
                    catch (Exception exception)
                    {
                        outcome = ShutdownOutcome.StopFault;
                        exceptionTypeName = exception.GetType().FullName;
                        exceptionMessage = exception.Message;
                    }
                }

                RefreshExecutionState(executionTask ?? GetExecutionTask(service), ref executionState, ref executionEntered, ref executionCompleted, ref executionCanceled, ref exceptionTypeName, ref exceptionMessage);
                if (outcome == ShutdownOutcome.CleanCompletion)
                {
                    if (executionState == ShutdownExecutionState.Faulted)
                    {
                        outcome = ShutdownOutcome.ExecutionFault;
                    }
                    else if (executionState == ShutdownExecutionState.Canceled)
                    {
                        outcome = executionEntered ? ShutdownOutcome.ExpectedCancellation : ShutdownOutcome.ExecutionNotStarted;
                    }
                    else if (shutdownDeadlineFired && (!stopCompleted || executionState == ShutdownExecutionState.Running))
                    {
                        outcome = ShutdownOutcome.ServiceNoncompletion;
                    }
                }
            }
        }
        catch (Exception exception)
        {
            if (outcome == ShutdownOutcome.CleanCompletion)
            {
                outcome = startupCompleted ? ShutdownOutcome.StopFault : ShutdownOutcome.StartupFailure;
            }
            exceptionTypeName ??= exception.GetType().FullName;
            exceptionMessage ??= exception.Message;
        }
        finally
        {
            phase = ShutdownPhase.CleaningUp;
            var cleanupClock = Stopwatch.StartNew();
            var cleanupTask = Task.Run(() =>
            {
                if (host is not null)
                {
                    host.Dispose();
                }
                else
                {
                    (service as IDisposable)?.Dispose();
                }
            }, CancellationToken.None);
            var cleanupWait = await WaitForOperationAsync(cleanupTask, _cleanupDeadline, stopwatch, CancellationToken.None).ConfigureAwait(false);
            cleanupDuration = cleanupClock.Elapsed;
            cleanupCompleted = cleanupWait == WaitReason.Completed;
            cleanupTimedOut = !cleanupCompleted;
            if (!cleanupCompleted) ObserveFault(cleanupTask);
            if (cleanupCompleted)
            {
                try { await cleanupTask.ConfigureAwait(false); }
                catch (Exception exception)
                {
                    if (outcome == ShutdownOutcome.CleanCompletion || outcome == ShutdownOutcome.ExpectedCancellation)
                    {
                        outcome = ShutdownOutcome.CleanupFailure;
                    }
                    exceptionTypeName ??= exception.GetType().FullName;
                    exceptionMessage ??= exception.Message;
                }
            }
        }

        phase = ShutdownPhase.Completed;
        stopwatch.Stop();
        return new ShutdownResult(
            outcome,
            phase,
            stopwatch.Elapsed,
            startupDuration,
            shutdownDuration,
            cleanupDuration,
            startupCompleted,
            executionEntered,
            stopInitiated,
            stopCompleted,
            executionCompleted,
            executionCanceled,
            harnessDeadlineFired,
            startupDeadlineFired,
            shutdownDeadlineFired,
            cancellationToken.IsCancellationRequested,
            hostShutdownCancellationRequested,
            executionState,
            exceptionTypeName,
            exceptionMessage,
            cleanupCompleted,
            cleanupTimedOut,
            _probes.Where(probe => probe.IsObserved));
    }

    private static Task? GetExecutionTask(IHostedService service)
    {
        return (service as BackgroundService)?.ExecuteTask;
    }

    private static void RefreshExecutionState(
        Task? executionTask,
        ref ShutdownExecutionState state,
        ref bool executionEntered,
        ref bool executionCompleted,
        ref bool executionCanceled,
        ref string? exceptionTypeName,
        ref string? exceptionMessage)
    {
        if (executionTask is null)
        {
            state = ShutdownExecutionState.NotObserved;
            return;
        }

        executionEntered = !executionTask.IsCanceled;
        executionCompleted = executionTask.Status == TaskStatus.RanToCompletion;
        executionCanceled = executionTask.IsCanceled;
        if (executionTask.IsFaulted)
        {
            state = ShutdownExecutionState.Faulted;
            var exception = executionTask.Exception?.GetBaseException();
            exceptionTypeName ??= exception?.GetType().FullName;
            exceptionMessage ??= exception?.Message;
        }
        else if (executionTask.IsCanceled)
        {
            state = ShutdownExecutionState.Canceled;
        }
        else if (executionTask.IsCompleted)
        {
            state = ShutdownExecutionState.Completed;
        }
        else
        {
            state = ShutdownExecutionState.Running;
        }
    }

    private async Task<bool> WaitForProbeAsync(ShutdownProbe probe, TimeSpan phaseDeadline, Stopwatch stopwatch, CancellationToken callerCancellationToken)
    {
        var phaseEnd = stopwatch.Elapsed + phaseDeadline;
        while (!probe.IsObserved)
        {
            var remainingPhase = phaseEnd - stopwatch.Elapsed;
            var remainingHarness = _harnessDeadline - stopwatch.Elapsed;
            if (remainingHarness <= TimeSpan.Zero || remainingPhase <= TimeSpan.Zero || callerCancellationToken.IsCancellationRequested) return false;
            var delay = TimeSpan.FromMilliseconds(Math.Min(10, Math.Min(remainingPhase.TotalMilliseconds, remainingHarness.TotalMilliseconds)));
            await Task.Delay(delay, CancellationToken.None).ConfigureAwait(false);
        }
        return true;
    }

    private async Task<WaitReason> WaitForOperationAsync(Task operation, TimeSpan phaseDeadline, Stopwatch stopwatch, CancellationToken callerCancellationToken)
    {
        if (operation.IsCompleted) return WaitReason.Completed;
        var remainingHarness = _harnessDeadline - stopwatch.Elapsed;
        if (remainingHarness <= TimeSpan.Zero) return WaitReason.HarnessDeadline;
        var phaseTask = Task.Delay(phaseDeadline, CancellationToken.None);
        var harnessTask = Task.Delay(remainingHarness, CancellationToken.None);
        var callerTask = Task.Delay(Timeout.Infinite, callerCancellationToken);
        var winner = await Task.WhenAny(operation, phaseTask, harnessTask, callerTask).ConfigureAwait(false);
        if (winner == operation) return WaitReason.Completed;
        if (winner == callerTask) return WaitReason.CallerCancellation;
        if (winner == harnessTask) return WaitReason.HarnessDeadline;
        return WaitReason.PhaseDeadline;
    }

    private static void ObserveFault(Task task)
    {
        _ = task.ContinueWith(completed => _ = completed.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private static TimeSpan ValidateDeadline(TimeSpan deadline, string parameterName)
    {
        if (deadline <= TimeSpan.Zero || deadline == Timeout.InfiniteTimeSpan) throw new ArgumentOutOfRangeException(parameterName, "The deadline must be positive and finite.");
        return deadline;
    }

    private enum WaitReason
    {
        Completed,
        PhaseDeadline,
        HarnessDeadline,
        CallerCancellation
    }
}
