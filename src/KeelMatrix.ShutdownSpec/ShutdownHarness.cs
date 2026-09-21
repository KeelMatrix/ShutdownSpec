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
    private ShutdownProbe? _executionProbe;
    private ShutdownProbe? _stoppingProbe;
    private TimeSpan _startupDeadline = DefaultStartupDeadline;
    private TimeSpan _shutdownDeadline = DefaultShutdownDeadline;
    private TimeSpan _harnessDeadline = DefaultHarnessDeadline;
    private TimeSpan _cleanupDeadline = DefaultCleanupDeadline;

    private ShutdownHarness(Func<IHostedService> factory) => _factory = factory;

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

    /// <summary>Registers an independently marked checkpoint proving that execution entered its body.</summary>
    /// <param name="probe">The application-owned execution-entry probe.</param>
    /// <returns>This harness.</returns>
    public ShutdownHarness WithExecutionProbe(ShutdownProbe probe)
    {
        WithProbe(probe);
        _executionProbe = probe;
        return this;
    }

    /// <summary>Registers the application-owned probe for the BackgroundService stopping token.</summary>
    /// <param name="probe">A probe registered with the service stopping token.</param>
    /// <returns>This harness.</returns>
    public ShutdownHarness WithStoppingProbe(ShutdownProbe probe)
    {
        WithProbe(probe);
        _stoppingProbe = probe;
        return this;
    }

    /// <summary>Runs the scenario and returns a structured result instead of a framework-specific assertion.</summary>
    /// <param name="cancellationToken">A caller-owned cancellation token for the harness wait.</param>
    /// <returns>The bounded lifecycle result.</returns>
    public async Task<ShutdownResult> RunAsync(CancellationToken cancellationToken = default)
    {
        foreach (var probe in _probes) probe.Reset();

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
        var startupCancellationRequested = false;
        var startupDeadlineFired = false;
        var shutdownDeadlineFired = false;
        var hostShutdownCancellationRequested = false;
        var executionState = ShutdownExecutionState.NotObserved;
        var exceptionTypeName = (string?)null;
        var exceptionMessage = (string?)null;
        var cleanupExceptionTypeName = (string?)null;
        var cleanupExceptionMessage = (string?)null;
        var service = (IHostedService?)null;
        var executionTask = (Task?)null;
        var cleanupCompleted = false;
        var cleanupTimedOut = false;
        var startupDuration = TimeSpan.Zero;
        var shutdownDuration = TimeSpan.Zero;
        var cleanupDuration = TimeSpan.Zero;
        var cleanupOutcome = (ShutdownOutcome?)null;
        var factoryTask = (Task<IHostedService>?)null;
        var startTask = (Task?)null;
        var cancellationNotifications = new List<CancellationNotification>();
        var resources = new OwnedResources(_hostBuilderFactory is not null);
        ShutdownResult? result = null;
        var stoppingTokenObserved = false;

        try
        {
            phase = ShutdownPhase.Creating;
            factoryTask = Task.Run(_factory, CancellationToken.None);
            resources.TrackFactory(factoryTask);
            var factoryWait = await WaitForOperationAsync(factoryTask, Timeout.InfiniteTimeSpan, stopwatch, cancellationToken).ConfigureAwait(false);
            if (factoryWait != WaitReason.Completed)
            {
                harnessDeadlineFired = factoryWait == WaitReason.HarnessDeadline;
                outcome = factoryWait == WaitReason.CallerCancellation ? ShutdownOutcome.CallerCancellation : ShutdownOutcome.FactoryNoncompletion;
                resources.MarkHostConstructionCompleted();
                ObserveFault(factoryTask);
            }
            else
            {
                try
                {
                    service = await factoryTask.ConfigureAwait(false);
                    if (service is null) throw new InvalidOperationException("The service factory returned null.");
                    resources.RegisterService(service);
                }
                catch (Exception exception)
                {
                    outcome = ShutdownOutcome.FactoryFailure;
                    exceptionTypeName = exception.GetType().FullName;
                    exceptionMessage = exception.Message;
                    resources.MarkHostConstructionCompleted();
                }
            }

            if (outcome == ShutdownOutcome.CleanCompletion && service is not null)
            {
                phase = ShutdownPhase.Starting;
                var startupClock = Stopwatch.StartNew();
                using var startupCancellation = new CancellationTokenSource();
                startTask = Task.Run(async () =>
                {
                    if (_hostBuilderFactory is not null)
                    {
                        var builder = _hostBuilderFactory();
                        if (builder is null) throw new InvalidOperationException("The host builder factory returned null.");
                        builder.ConfigureServices((_, services) => services.AddSingleton<IHostedService>(service));
                        var builtHost = builder.Build();
                        resources.RegisterHost(builtHost);
                        await builtHost.StartAsync(startupCancellation.Token).ConfigureAwait(false);
                    }
                    else
                    {
                        await service.StartAsync(startupCancellation.Token).ConfigureAwait(false);
                    }
                }, CancellationToken.None);
                resources.TrackHostConstruction(startTask);

                var startWait = await WaitForOperationAsync(startTask, _startupDeadline, stopwatch, cancellationToken).ConfigureAwait(false);
                if (startWait != WaitReason.Completed)
                {
                    startupDeadlineFired = startWait == WaitReason.PhaseDeadline;
                    harnessDeadlineFired = startWait == WaitReason.HarnessDeadline;
                    outcome = startWait switch
                    {
                        WaitReason.CallerCancellation => ShutdownOutcome.CallerCancellation,
                        WaitReason.HarnessDeadline => ShutdownOutcome.HarnessDeadline,
                        _ => ShutdownOutcome.StartupNoncompletion
                    };
                    startupCancellationRequested = true;
                    cancellationNotifications.Add(RequestCancellation(startupCancellation));
                    ObserveFault(startTask);
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

                var startupClockElapsed = startupClock.Elapsed;
                if (outcome == ShutdownOutcome.CleanCompletion && startupCompleted)
                {
                    executionTask = GetExecutionTask(service);
                    RefreshExecutionState(executionTask, ref executionState, ref executionCompleted, ref executionCanceled, ref exceptionTypeName, ref exceptionMessage);
                    executionEntered = IsExecutionEntered();
                    if (_readinessProbe is not null)
                    {
                        var remainingStartup = _startupDeadline - startupClockElapsed;
                        var readinessWait = remainingStartup <= TimeSpan.Zero ? WaitReason.PhaseDeadline : await WaitForProbeAsync(_readinessProbe, remainingStartup, stopwatch, cancellationToken).ConfigureAwait(false);
                        startupDeadlineFired = readinessWait == WaitReason.PhaseDeadline;
                        harnessDeadlineFired = readinessWait == WaitReason.HarnessDeadline;
                        if (readinessWait != WaitReason.Completed)
                        {
                            outcome = readinessWait switch
                            {
                                WaitReason.CallerCancellation => ShutdownOutcome.CallerCancellation,
                                WaitReason.HarnessDeadline => ShutdownOutcome.HarnessDeadline,
                                _ => ShutdownOutcome.StartupNoncompletion
                            };
                            startupCancellationRequested = true;
                            cancellationNotifications.Add(RequestCancellation(startupCancellation));
                        }
                    }
                }
                startupDuration = startupClock.Elapsed;

                if (outcome == ShutdownOutcome.CleanCompletion && startupCompleted)
                {
                    executionTask ??= GetExecutionTask(service);
                    RefreshExecutionState(executionTask, ref executionState, ref executionCompleted, ref executionCanceled, ref exceptionTypeName, ref exceptionMessage);
                    executionEntered = IsExecutionEntered();
                    var executionCanceledBeforeStop = executionState == ShutdownExecutionState.Canceled;
                    phase = ShutdownPhase.Stopping;
                    stopInitiated = true;
                    var shutdownClock = Stopwatch.StartNew();
                    using var shutdownCancellation = new CancellationTokenSource();
                    var stopTask = Task.Run(async () =>
                    {
                        var host = resources.GetHost();
                        if (host is not null) await host.StopAsync(shutdownCancellation.Token).ConfigureAwait(false);
                        else await service.StopAsync(shutdownCancellation.Token).ConfigureAwait(false);
                    }, CancellationToken.None);

                    var stopWait = await WaitForOperationAsync(stopTask, _shutdownDeadline, stopwatch, cancellationToken).ConfigureAwait(false);
                    if (stopWait != WaitReason.Completed)
                    {
                        shutdownDeadlineFired = stopWait == WaitReason.PhaseDeadline;
                        harnessDeadlineFired = stopWait == WaitReason.HarnessDeadline;
                        outcome = stopWait == WaitReason.CallerCancellation ? ShutdownOutcome.CallerCancellation : ShutdownOutcome.ServiceNoncompletion;
                        hostShutdownCancellationRequested = true;
                        cancellationNotifications.Add(RequestCancellation(shutdownCancellation));
                        ObserveFault(stopTask);
                        resources.TrackAbandonedOperation(stopTask);
                    }
                    else
                    {
                        stopCompleted = true;
                        try { await stopTask.ConfigureAwait(false); }
                        catch (OperationCanceledException exception)
                        {
                            outcome = ShutdownOutcome.StopFault;
                            exceptionTypeName = exception.GetType().FullName;
                            exceptionMessage = exception.Message;
                        }
                        catch (Exception exception)
                        {
                            outcome = ShutdownOutcome.StopFault;
                            exceptionTypeName = exception.GetType().FullName;
                            exceptionMessage = exception.Message;
                        }
                    }

                    executionTask ??= GetExecutionTask(service);
                    RefreshExecutionState(executionTask, ref executionState, ref executionCompleted, ref executionCanceled, ref exceptionTypeName, ref exceptionMessage);
                    executionEntered = IsExecutionEntered();
                    if (outcome == ShutdownOutcome.CleanCompletion && executionState == ShutdownExecutionState.Running && executionTask is not null)
                    {
                        var remainingShutdown = _shutdownDeadline - shutdownClock.Elapsed;
                        var executionWait = await WaitForOperationAsync(executionTask, remainingShutdown > TimeSpan.Zero ? remainingShutdown : TimeSpan.Zero, stopwatch, cancellationToken).ConfigureAwait(false);
                        if (executionWait != WaitReason.Completed)
                        {
                            shutdownDeadlineFired = executionWait == WaitReason.PhaseDeadline;
                            harnessDeadlineFired = executionWait == WaitReason.HarnessDeadline;
                            outcome = executionWait == WaitReason.CallerCancellation ? ShutdownOutcome.CallerCancellation : ShutdownOutcome.ServiceNoncompletion;
                            hostShutdownCancellationRequested = true;
                            cancellationNotifications.Add(RequestCancellation(shutdownCancellation));
                            resources.TrackAbandonedOperation(executionTask);
                        }
                    }

                    RefreshExecutionState(executionTask, ref executionState, ref executionCompleted, ref executionCanceled, ref exceptionTypeName, ref exceptionMessage);
                    executionEntered = IsExecutionEntered();
                    stoppingTokenObserved = _stoppingProbe?.IsCancellationObserved == true;
                    if (outcome == ShutdownOutcome.CleanCompletion && executionState == ShutdownExecutionState.Faulted)
                    {
                        outcome = ShutdownOutcome.ExecutionFault;
                    }
                    else if (outcome == ShutdownOutcome.CleanCompletion && executionState == ShutdownExecutionState.Canceled)
                    {
                        if (_stoppingProbe is not null && !_stoppingProbe.IsCancellationObserved)
                        {
                            await WaitForProbeAsync(_stoppingProbe, TimeSpan.FromMilliseconds(10), stopwatch, cancellationToken).ConfigureAwait(false);
                        }
                        stoppingTokenObserved = _stoppingProbe?.IsCancellationObserved == true;
                        if (executionCanceledBeforeStop) outcome = ShutdownOutcome.ExecutionFault;
                        else if (!executionEntered) outcome = ShutdownOutcome.ExecutionNotStarted;
                        else if (!stoppingTokenObserved) outcome = ShutdownOutcome.ExecutionCancellationUnverified;
                        else outcome = ShutdownOutcome.ExpectedCancellation;
                    }
                    else if (outcome == ShutdownOutcome.CleanCompletion && executionState == ShutdownExecutionState.Completed && !executionEntered)
                    {
                        outcome = ShutdownOutcome.ExecutionNotStarted;
                    }
                    else if (outcome == ShutdownOutcome.CleanCompletion && service is BackgroundService && executionTask is null)
                    {
                        outcome = ShutdownOutcome.ExecutionNotStarted;
                    }
                    shutdownDuration = shutdownClock.Elapsed;
                }
            }
        }
        catch (Exception exception)
        {
            if (outcome == ShutdownOutcome.CleanCompletion) outcome = startupCompleted ? ShutdownOutcome.StopFault : ShutdownOutcome.StartupFailure;
            exceptionTypeName ??= exception.GetType().FullName;
            exceptionMessage ??= exception.Message;
        }
        finally
        {
            var primaryOutcome = outcome;
            var primaryPhase = phase;
            phase = ShutdownPhase.CleaningUp;
            var cleanupClock = Stopwatch.StartNew();
            var cleanupSucceeded = true;

            if (!stopInitiated && service is not null)
            {
                var remaining = _cleanupDeadline - cleanupClock.Elapsed;
                if (remaining <= TimeSpan.Zero)
                {
                    cleanupSucceeded = false;
                    cleanupOutcome = ShutdownOutcome.CleanupNoncompletion;
                }
                else
                {
                    using var cleanupCancellation = new CancellationTokenSource();
                    var cleanupStopTask = Task.Run(async () =>
                    {
                        var host = resources.GetHost();
                        if (host is not null) await host.StopAsync(cleanupCancellation.Token).ConfigureAwait(false);
                        else await service.StopAsync(cleanupCancellation.Token).ConfigureAwait(false);
                    }, CancellationToken.None);
                    var cleanupStopWait = await WaitForOperationAsync(cleanupStopTask, remaining, stopwatch, CancellationToken.None, includeHarnessDeadline: false).ConfigureAwait(false);
                    if (cleanupStopWait != WaitReason.Completed)
                    {
                        cleanupSucceeded = false;
                        cleanupOutcome = ShutdownOutcome.CleanupNoncompletion;
                        ObserveFault(cleanupStopTask);
                        _ = RequestCancellation(cleanupCancellation);
                        resources.TrackAbandonedOperation(cleanupStopTask);
                    }
                    else
                    {
                        try { await cleanupStopTask.ConfigureAwait(false); }
                        catch (Exception exception)
                        {
                            cleanupSucceeded = false;
                            cleanupOutcome = ShutdownOutcome.CleanupFailure;
                            cleanupExceptionTypeName = exception.GetType().FullName;
                            cleanupExceptionMessage = exception.Message;
                        }
                    }
                }
            }

            var remainingForDispose = _cleanupDeadline - cleanupClock.Elapsed;
            if (remainingForDispose > TimeSpan.Zero)
            {
                var disposeTask = resources.RequestCleanupAsync();
                var disposeWait = await WaitForOperationAsync(disposeTask, remainingForDispose, stopwatch, CancellationToken.None, includeHarnessDeadline: false).ConfigureAwait(false);
                if (disposeWait != WaitReason.Completed)
                {
                    cleanupSucceeded = false;
                    cleanupOutcome ??= ShutdownOutcome.CleanupNoncompletion;
                    ObserveFault(disposeTask);
                }
                else
                {
                    try { await disposeTask.ConfigureAwait(false); }
                    catch (Exception exception)
                    {
                        cleanupSucceeded = false;
                        cleanupOutcome ??= ShutdownOutcome.CleanupFailure;
                        cleanupExceptionTypeName ??= exception.GetType().FullName;
                        cleanupExceptionMessage ??= exception.Message;
                    }
                }
            }
            else
            {
                cleanupSucceeded = false;
                cleanupOutcome ??= ShutdownOutcome.CleanupNoncompletion;
            }

            cleanupDuration = cleanupClock.Elapsed;
            cleanupCompleted = cleanupSucceeded;
            cleanupTimedOut = cleanupOutcome == ShutdownOutcome.CleanupNoncompletion;
            if (cleanupOutcome is not null && (primaryOutcome == ShutdownOutcome.CleanCompletion || primaryOutcome == ShutdownOutcome.ExpectedCancellation))
            {
                outcome = cleanupOutcome.Value;
                phase = ShutdownPhase.CleaningUp;
            }
            else
            {
                outcome = primaryOutcome;
                phase = primaryOutcome == ShutdownOutcome.CleanCompletion || primaryOutcome == ShutdownOutcome.ExpectedCancellation ? ShutdownPhase.Completed : primaryPhase;
            }

            CaptureNotificationState(cancellationNotifications, out var cancellationNotificationFaulted, out var cancellationNotificationPending, out var notificationTypeName, out var notificationMessage);
            stopwatch.Stop();
            result = new ShutdownResult(
                outcome,
                phase,
                stopwatch.Elapsed,
                startupDuration,
                shutdownDuration,
                cleanupDuration,
                startupCompleted,
                _readinessProbe?.IsObserved == true,
                executionEntered,
                stopInitiated,
                stopCompleted,
                executionCompleted,
                executionCanceled,
                harnessDeadlineFired,
                startupCancellationRequested,
                startupDeadlineFired,
                shutdownDeadlineFired,
                cancellationToken.IsCancellationRequested,
                hostShutdownCancellationRequested,
                stoppingTokenObserved,
                cancellationNotificationFaulted,
                cancellationNotificationPending,
                executionState,
                exceptionTypeName ?? notificationTypeName,
                exceptionMessage ?? notificationMessage,
                primaryOutcome,
                primaryPhase,
                cleanupOutcome,
                cleanupExceptionTypeName,
                cleanupExceptionMessage,
                cleanupCompleted,
                cleanupTimedOut,
                _probes.Where(probe => probe.IsObserved));
        }

        return result!;
    }

    private bool IsExecutionEntered() => _executionProbe?.IsObserved == true;

    private static Task? GetExecutionTask(IHostedService service) => (service as BackgroundService)?.ExecuteTask;

    private static void RefreshExecutionState(Task? executionTask, ref ShutdownExecutionState state, ref bool executionCompleted, ref bool executionCanceled, ref string? exceptionTypeName, ref string? exceptionMessage)
    {
        if (executionTask is null)
        {
            state = ShutdownExecutionState.NotObserved;
            return;
        }

        executionCompleted = executionTask.Status == TaskStatus.RanToCompletion;
        executionCanceled = executionTask.IsCanceled;
        if (executionTask.IsFaulted)
        {
            state = ShutdownExecutionState.Faulted;
            var exception = executionTask.Exception?.GetBaseException();
            exceptionTypeName ??= exception?.GetType().FullName;
            exceptionMessage ??= exception?.Message;
        }
        else if (executionTask.IsCanceled) state = ShutdownExecutionState.Canceled;
        else if (executionTask.IsCompleted) state = ShutdownExecutionState.Completed;
        else state = ShutdownExecutionState.Running;
    }

    private async Task<WaitReason> WaitForProbeAsync(ShutdownProbe probe, TimeSpan phaseDeadline, Stopwatch stopwatch, CancellationToken callerCancellationToken)
    {
        var phaseEnd = stopwatch.Elapsed + phaseDeadline;
        while (!probe.IsObserved)
        {
            var remainingPhase = phaseEnd - stopwatch.Elapsed;
            var remainingHarness = _harnessDeadline - stopwatch.Elapsed;
            if (callerCancellationToken.IsCancellationRequested) return WaitReason.CallerCancellation;
            if (remainingHarness <= TimeSpan.Zero) return WaitReason.HarnessDeadline;
            if (remainingPhase <= TimeSpan.Zero) return WaitReason.PhaseDeadline;
            var delay = TimeSpan.FromMilliseconds(Math.Min(10, Math.Min(remainingPhase.TotalMilliseconds, remainingHarness.TotalMilliseconds)));
            await Task.Delay(delay, CancellationToken.None).ConfigureAwait(false);
        }
        return WaitReason.Completed;
    }

    private async Task<WaitReason> WaitForOperationAsync(Task operation, TimeSpan phaseDeadline, Stopwatch stopwatch, CancellationToken callerCancellationToken, bool includeHarnessDeadline = true)
    {
        if (operation.IsCompleted) return WaitReason.Completed;
        var remainingHarness = _harnessDeadline - stopwatch.Elapsed;
        if (includeHarnessDeadline && remainingHarness <= TimeSpan.Zero) return WaitReason.HarnessDeadline;
        using var phaseCancellation = new CancellationTokenSource();
        using var harnessCancellation = includeHarnessDeadline ? new CancellationTokenSource() : null;
        var callerSignal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var callerRegistration = callerCancellationToken.Register(static state => ((TaskCompletionSource<bool>)state!).TrySetResult(true), callerSignal);
        var phaseTask = Task.Delay(phaseDeadline, phaseCancellation.Token);
        var harnessTask = includeHarnessDeadline ? Task.Delay(remainingHarness, harnessCancellation!.Token) : Task.Delay(Timeout.InfiniteTimeSpan, CancellationToken.None);
        try
        {
            var winner = await Task.WhenAny(operation, phaseTask, harnessTask, callerSignal.Task).ConfigureAwait(false);
            if (winner == operation) return WaitReason.Completed;
            if (winner == callerSignal.Task) return WaitReason.CallerCancellation;
            if (winner == harnessTask) return WaitReason.HarnessDeadline;
            return WaitReason.PhaseDeadline;
        }
        finally
        {
            phaseCancellation.Cancel();
            harnessCancellation?.Cancel();
            callerSignal.TrySetResult(false);
        }
    }

    private static CancellationNotification RequestCancellation(CancellationTokenSource source)
    {
        var task = Task.Run(() => source.Cancel(throwOnFirstException: false), CancellationToken.None);
        ObserveFault(task);
        return new CancellationNotification(task);
    }

    private static void CaptureNotificationState(IEnumerable<CancellationNotification> notifications, out bool faulted, out bool pending, out string? exceptionTypeName, out string? exceptionMessage)
    {
        faulted = false;
        pending = false;
        exceptionTypeName = null;
        exceptionMessage = null;
        foreach (var notification in notifications)
        {
            if (!notification.Task.IsCompleted)
            {
                pending = true;
                continue;
            }
            if (!notification.Task.IsFaulted) continue;
            faulted = true;
            var exception = notification.Task.Exception?.GetBaseException();
            exceptionTypeName ??= exception?.GetType().FullName;
            exceptionMessage ??= exception?.Message;
        }
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

    private sealed class CancellationNotification
    {
        public CancellationNotification(Task task) => Task = task;
        public Task Task { get; }
    }

    private sealed class OwnedResources
    {
        private readonly object _sync = new();
        private readonly bool _hostBacked;
        private IHostedService? _service;
        private IHost? _host;
        private bool _hostConstructionCompleted;
        private bool _cleanupRequested;
        private bool _serviceDisposed;
        private bool _hostDisposed;
        private int _pendingOperations;
        private TaskCompletionSource<bool>? _cleanupCompletion;
        private bool _cleanupActionsRunning;

        public OwnedResources(bool hostBacked)
        {
            _hostBacked = hostBacked;
            _hostConstructionCompleted = !hostBacked;
        }

        public void TrackFactory(Task<IHostedService> factoryTask)
        {
            _ = factoryTask.ContinueWith(completed =>
            {
                if (completed.Status == TaskStatus.RanToCompletion && completed.Result is not null) RegisterService(completed.Result);
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }

        public void RegisterService(IHostedService service)
        {
            List<Action>? actions;
            lock (_sync)
            {
                _service ??= service;
                actions = _cleanupRequested ? TakeDisposalsLocked() : null;
            }
            if (_cleanupRequested) RunCleanupActions(actions); else RunDetached(actions);
        }

        public void RegisterHost(IHost host)
        {
            List<Action>? actions;
            lock (_sync)
            {
                _host = host;
                actions = _cleanupRequested ? TakeDisposalsLocked() : null;
            }
            if (_cleanupRequested) RunCleanupActions(actions); else RunDetached(actions);
        }

        public void TrackHostConstruction(Task startTask)
        {
            _ = startTask.ContinueWith(_ => MarkHostConstructionCompleted(), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }

        public void TrackAbandonedOperation(Task operation)
        {
            if (operation.IsCompleted) return;
            lock (_sync) _pendingOperations++;
            _ = operation.ContinueWith(_ => CompleteAbandonedOperation(), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }

        private void CompleteAbandonedOperation()
        {
            List<Action>? actions;
            lock (_sync)
            {
                _pendingOperations--;
                actions = _cleanupRequested ? TakeDisposalsLocked() : null;
            }
            RunCleanupActions(actions);
        }

        public void MarkHostConstructionCompleted()
        {
            List<Action>? actions;
            lock (_sync)
            {
                _hostConstructionCompleted = true;
                actions = _cleanupRequested ? TakeDisposalsLocked() : null;
            }
            if (_cleanupRequested) RunCleanupActions(actions); else RunDetached(actions);
        }

        public IHost? GetHost()
        {
            lock (_sync) return _host;
        }

        public Task<bool> RequestCleanupAsync()
        {
            List<Action>? actions;
            lock (_sync)
            {
                _cleanupRequested = true;
                _cleanupCompletion ??= new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                actions = TakeDisposalsLocked();
                if (_pendingOperations == 0 && actions is null) _cleanupCompletion.TrySetResult(true);
            }
            RunCleanupActions(actions);
            return _cleanupCompletion.Task;
        }

        private List<Action>? TakeDisposalsLocked()
        {
            var actions = new List<Action>();
            if (_host is not null && !_hostDisposed)
            {
                _hostDisposed = true;
                _serviceDisposed = true;
                var host = _host;
                var service = _service;
                actions.Add(() => DisposeHostAndService(host, service));
            }
            else if (_service is not null && !_serviceDisposed && (!_hostBacked || _hostConstructionCompleted))
            {
                _serviceDisposed = true;
                var service = _service;
                if (service is IDisposable disposable) actions.Add(disposable.Dispose);
            }
            return actions.Count == 0 ? null : actions;
        }

        private void RunCleanupActions(List<Action>? actions)
        {
            if (actions is null || actions.Count == 0)
            {
                lock (_sync)
                {
                    if (_cleanupRequested && _pendingOperations == 0 && !_cleanupActionsRunning) _cleanupCompletion?.TrySetResult(true);
                }
                return;
            }

            lock (_sync) _cleanupActionsRunning = true;
            var task = RunActionsAsync(actions);
            _ = task.ContinueWith(completed =>
            {
                lock (_sync)
                {
                    _cleanupActionsRunning = false;
                    if (completed.IsFaulted) _cleanupCompletion?.TrySetException(completed.Exception!.InnerExceptions);
                    else if (_cleanupRequested && _pendingOperations == 0) _cleanupCompletion?.TrySetResult(true);
                }
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }

        private static Task RunActionsAsync(List<Action>? actions)
        {
            if (actions is null || actions.Count == 0) return Task.CompletedTask;
            return Task.Run(() =>
            {
                foreach (var action in actions) action();
            }, CancellationToken.None);
        }

        private static void DisposeHostAndService(IHost host, IHostedService? service)
        {
            var exceptions = new List<Exception>();
            try { host.Dispose(); }
            catch (Exception exception) { exceptions.Add(exception); }
            try { (service as IDisposable)?.Dispose(); }
            catch (Exception exception) { exceptions.Add(exception); }
            if (exceptions.Count == 1) throw exceptions[0];
            if (exceptions.Count > 1) throw new AggregateException(exceptions);
        }

        private static void RunDetached(List<Action>? actions)
        {
            var task = RunActionsAsync(actions);
            if (!task.IsCompleted) ObserveFault(task);
            else if (task.IsFaulted) _ = task.Exception;
        }
    }

    private enum WaitReason
    {
        Completed,
        PhaseDeadline,
        HarnessDeadline,
        CallerCancellation
    }
}
