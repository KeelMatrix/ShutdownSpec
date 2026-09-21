using System.Collections.ObjectModel;
using System.Text;

namespace KeelMatrix.ShutdownSpec;

/// <summary>Contains lifecycle facts, timing, probes, and the terminal outcome of a scenario.</summary>
public sealed class ShutdownResult
{
    private readonly HashSet<ShutdownProbe> _observedProbeReferences;

    internal ShutdownResult(
        ShutdownOutcome outcome,
        ShutdownPhase phase,
        TimeSpan elapsed,
        TimeSpan startupDuration,
        TimeSpan shutdownDuration,
        TimeSpan cleanupDuration,
        bool startupCompleted,
        bool executionEntered,
        bool stopInitiated,
        bool stopCompleted,
        bool executionCompleted,
        bool executionCanceled,
        bool harnessDeadlineFired,
        bool startupCancellationRequested,
        bool startupDeadlineFired,
        bool shutdownDeadlineFired,
        bool callerCancellationRequested,
        bool hostShutdownCancellationRequested,
        bool stoppingTokenObserved,
        bool cancellationNotificationFaulted,
        bool cancellationNotificationPending,
        ShutdownExecutionState executionState,
        string? exceptionTypeName,
        string? exceptionMessage,
        ShutdownOutcome primaryOutcome,
        ShutdownPhase primaryPhase,
        ShutdownOutcome? cleanupOutcome,
        string? cleanupExceptionTypeName,
        string? cleanupExceptionMessage,
        bool cleanupCompleted,
        bool cleanupTimedOut,
        IEnumerable<ShutdownProbe> observedProbes)
    {
        Outcome = outcome;
        Phase = phase;
        Elapsed = elapsed;
        StartupDuration = startupDuration;
        ShutdownDuration = shutdownDuration;
        CleanupDuration = cleanupDuration;
        StartupCompleted = startupCompleted;
        ExecutionEntered = executionEntered;
        StopInitiated = stopInitiated;
        StopCompleted = stopCompleted;
        ExecutionCompleted = executionCompleted;
        ExecutionCanceled = executionCanceled;
        HarnessDeadlineFired = harnessDeadlineFired;
        StartupCancellationRequested = startupCancellationRequested;
        StartupDeadlineFired = startupDeadlineFired;
        ShutdownDeadlineFired = shutdownDeadlineFired;
        CallerCancellationRequested = callerCancellationRequested;
        HostShutdownCancellationRequested = hostShutdownCancellationRequested;
        StoppingTokenObserved = stoppingTokenObserved;
        CancellationNotificationFaulted = cancellationNotificationFaulted;
        CancellationNotificationPending = cancellationNotificationPending;
        ExecutionState = executionState;
        ExceptionTypeName = exceptionTypeName;
        ExceptionMessage = exceptionMessage;
        PrimaryOutcome = primaryOutcome;
        PrimaryPhase = primaryPhase;
        CleanupOutcome = cleanupOutcome;
        CleanupExceptionTypeName = cleanupExceptionTypeName;
        CleanupExceptionMessage = cleanupExceptionMessage;
        CleanupCompleted = cleanupCompleted;
        CleanupTimedOut = cleanupTimedOut;
        _observedProbeReferences = new HashSet<ShutdownProbe>(observedProbes);
        var names = _observedProbeReferences.Select(probe => probe.Name).Distinct(StringComparer.Ordinal).OrderBy(name => name, StringComparer.Ordinal).ToArray();
        ObservedProbeNames = new ReadOnlyCollection<string>(names);
    }

    /// <summary>Gets the terminal classification.</summary>
    public ShutdownOutcome Outcome { get; }

    /// <summary>Gets the lifecycle phase reached when the scenario completed.</summary>
    public ShutdownPhase Phase { get; }

    /// <summary>Gets the total scenario duration, including bounded cleanup.</summary>
    public TimeSpan Elapsed { get; }

    /// <summary>Gets the duration spent waiting for startup.</summary>
    public TimeSpan StartupDuration { get; }

    /// <summary>Gets the duration spent waiting for graceful shutdown.</summary>
    public TimeSpan ShutdownDuration { get; }

    /// <summary>Gets the duration spent in bounded cleanup.</summary>
    public TimeSpan CleanupDuration { get; }

    /// <summary>Gets whether startup completed successfully.</summary>
    public bool StartupCompleted { get; }

    /// <summary>Gets whether background execution became observable before it completed.</summary>
    public bool ExecutionEntered { get; }

    /// <summary>Gets whether graceful shutdown was initiated.</summary>
    public bool StopInitiated { get; }

    /// <summary>Gets whether the stop operation returned.</summary>
    public bool StopCompleted { get; }

    /// <summary>Gets whether an observed execution task completed successfully.</summary>
    public bool ExecutionCompleted { get; }

    /// <summary>Gets whether an observed execution task was canceled.</summary>
    public bool ExecutionCanceled { get; }

    /// <summary>Gets whether the outer harness deadline fired.</summary>
    public bool HarnessDeadlineFired { get; }

    /// <summary>Gets whether the startup cancellation notification was requested.</summary>
    public bool StartupCancellationRequested { get; }

    /// <summary>Gets whether the startup phase deadline fired.</summary>
    public bool StartupDeadlineFired { get; }

    /// <summary>Gets whether the shutdown phase deadline fired.</summary>
    public bool ShutdownDeadlineFired { get; }

    /// <summary>Gets whether the caller cancellation token was requested.</summary>
    public bool CallerCancellationRequested { get; }

    /// <summary>Gets whether the token passed to graceful shutdown was requested.</summary>
    public bool HostShutdownCancellationRequested { get; }

    /// <summary>Gets whether the configured application-owned stopping-token probe was observed before result classification.</summary>
    public bool StoppingTokenObserved { get; }

    /// <summary>Gets whether a cancellation callback failed while notification was isolated.</summary>
    public bool CancellationNotificationFaulted { get; }

    /// <summary>Gets whether an isolated cancellation notification was still running when the result was returned.</summary>
    public bool CancellationNotificationPending { get; }

    /// <summary>Gets the latest observed execution-task state.</summary>
    public ShutdownExecutionState ExecutionState { get; }

    /// <summary>Gets the captured exception type name without exposing it in the default report.</summary>
    public string? ExceptionTypeName { get; }

    /// <summary>Gets the captured exception message for explicit caller inspection.</summary>
    public string? ExceptionMessage { get; }

    /// <summary>Gets the primary scenario outcome before cleanup ran.</summary>
    public ShutdownOutcome PrimaryOutcome { get; }

    /// <summary>Gets the phase where the primary scenario outcome was determined.</summary>
    public ShutdownPhase PrimaryPhase { get; }

    /// <summary>Gets a cleanup outcome, when cleanup failed independently of the primary scenario.</summary>
    public ShutdownOutcome? CleanupOutcome { get; }

    /// <summary>Gets the cleanup exception type without exposing it in the default report.</summary>
    public string? CleanupExceptionTypeName { get; }

    /// <summary>Gets the cleanup exception message for explicit caller inspection.</summary>
    public string? CleanupExceptionMessage { get; }

    /// <summary>Gets whether cleanup completed before its own bound.</summary>
    public bool CleanupCompleted { get; }

    /// <summary>Gets whether cleanup reached its bound without returning.</summary>
    public bool CleanupTimedOut { get; }

    /// <summary>Gets the names of application-owned probes observed during the scenario.</summary>
    public IReadOnlyList<string> ObservedProbeNames { get; }

    /// <summary>Gets whether the lifecycle and bounded cleanup both completed successfully.</summary>
    public bool Succeeded => (PrimaryOutcome == ShutdownOutcome.CleanCompletion || PrimaryOutcome == ShutdownOutcome.ExpectedCancellation)
        && CleanupCompleted
        && !CleanupTimedOut
        && CleanupOutcome is null
        && Outcome != ShutdownOutcome.ExecutionCancellationUnverified
        && ExecutionState != ShutdownExecutionState.Running;

    /// <summary>Gets whether an unexpected fault was observed.</summary>
    public bool HasUnexpectedFault => IsUnexpectedFault(PrimaryOutcome)
        || IsUnexpectedFault(Outcome)
        || (CleanupOutcome is not null && IsUnexpectedFault(CleanupOutcome.Value));

    /// <summary>Checks that graceful shutdown completed within a configured assertion bound.</summary>
    /// <param name="deadline">The maximum acceptable shutdown duration.</param>
    /// <exception cref="ShutdownAssertionException">Thrown when the result does not satisfy the assertion.</exception>
    public void ShouldStopWithin(TimeSpan deadline)
    {
        if (deadline < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(deadline));
        if (!Succeeded || !StopCompleted || ShutdownDuration > deadline)
        {
            throw new ShutdownAssertionException($"Shutdown did not complete within {deadline.TotalSeconds:0.###}s.\n{ToDiagnosticString()}");
        }
    }

    /// <summary>Checks that no unexpected startup, stop, execution, deadline, or cleanup outcome occurred.</summary>
    /// <exception cref="ShutdownAssertionException">Thrown when the result is not successful.</exception>
    public void ShouldCompleteWithoutFault()
    {
        if (!Succeeded || CleanupTimedOut || Outcome == ShutdownOutcome.CleanupFailure)
        {
            throw new ShutdownAssertionException($"Shutdown did not complete without fault.\n{ToDiagnosticString()}");
        }
    }

    /// <summary>Checks that an application-owned probe was observed.</summary>
    /// <param name="probe">The probe to require.</param>
    /// <exception cref="ShutdownAssertionException">Thrown when the probe was not observed.</exception>
    public void ShouldObserve(ShutdownProbe probe)
    {
        if (probe is null) throw new ArgumentNullException(nameof(probe));
        if (!_observedProbeReferences.Contains(probe))
        {
            throw new ShutdownAssertionException($"Probe '{probe.Name}' was not observed.\n{ToDiagnosticString()}");
        }
    }

    /// <summary>Gets a bounded, privacy-conscious diagnostic report for this result.</summary>
    /// <returns>A report containing lifecycle facts, stable outcome code, and exception type only.</returns>
    public string ToDiagnosticString()
    {
        var builder = new StringBuilder();
        builder.Append(GetFailureCode()).Append(": ").Append(Outcome).Append('.');
        builder.AppendLine();
        builder.Append("Phase: ").Append(Phase).AppendLine();
        builder.Append("Elapsed: ").Append(Elapsed.TotalMilliseconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)).Append("ms").AppendLine();
        builder.Append("Startup duration: ").Append(StartupDuration.TotalMilliseconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)).Append("ms; shutdown duration: ").Append(ShutdownDuration.TotalMilliseconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)).Append("ms; cleanup duration: ").Append(CleanupDuration.TotalMilliseconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)).Append("ms").AppendLine();
        builder.Append("Startup completed: ").Append(StartupCompleted).Append("; stop initiated: ").Append(StopInitiated).Append("; stop completed: ").Append(StopCompleted).AppendLine();
        builder.Append("Execution: ").Append(ExecutionState).Append("; entered: ").Append(ExecutionEntered).Append("; completed: ").Append(ExecutionCompleted).Append("; canceled: ").Append(ExecutionCanceled).AppendLine();
        builder.Append("Harness deadline: ").Append(HarnessDeadlineFired).Append("; startup deadline: ").Append(StartupDeadlineFired).Append("; shutdown deadline: ").Append(ShutdownDeadlineFired).Append("; startup cancellation requested: ").Append(StartupCancellationRequested).AppendLine();
        builder.Append("Caller cancellation: ").Append(CallerCancellationRequested).Append("; host shutdown cancellation: ").Append(HostShutdownCancellationRequested).AppendLine();
        builder.Append("Primary outcome: ").Append(PrimaryOutcome).Append("; cleanup outcome: ").Append(CleanupOutcome?.ToString() ?? "none").Append("; cancellation notification faulted: ").Append(CancellationNotificationFaulted).Append("; notification pending: ").Append(CancellationNotificationPending).AppendLine();
        builder.Append("Observed probes: ").Append(ObservedProbeNames.Count == 0 ? "none" : string.Join(", ", ObservedProbeNames)).AppendLine();
        if (ExceptionTypeName is not null) builder.Append("Exception type: ").Append(ExceptionTypeName).AppendLine();
        if (CleanupExceptionTypeName is not null) builder.Append("Cleanup exception type: ").Append(CleanupExceptionTypeName).AppendLine();
        builder.Append("Cleanup completed: ").Append(CleanupCompleted).Append("; cleanup timed out: ").Append(CleanupTimedOut);
        return builder.ToString();
    }

    /// <summary>Returns the privacy-conscious diagnostic report.</summary>
    public override string ToString() => ToDiagnosticString();

    private string GetFailureCode()
    {
        switch (Outcome)
        {
            case ShutdownOutcome.StartupFailure: return "KMSHUT001";
            case ShutdownOutcome.StopFault: return "KMSHUT002";
            case ShutdownOutcome.ExecutionFault: return "KMSHUT003";
            case ShutdownOutcome.ServiceNoncompletion: return "KMSHUT101";
            case ShutdownOutcome.HarnessDeadline: return "KMSHUT102";
            case ShutdownOutcome.CallerCancellation: return "KMSHUT103";
            case ShutdownOutcome.ExecutionNotStarted: return "KMSHUT104";
            case ShutdownOutcome.CleanupFailure: return "KMSHUT105";
            case ShutdownOutcome.FactoryFailure: return "KMSHUT106";
            case ShutdownOutcome.FactoryNoncompletion: return "KMSHUT107";
            case ShutdownOutcome.StartupNoncompletion: return "KMSHUT108";
            case ShutdownOutcome.CleanupNoncompletion: return "KMSHUT109";
            case ShutdownOutcome.ExecutionCancellationUnverified: return "KMSHUT110";
            default: return "KMSHUT000";
        }
    }

    private static bool IsUnexpectedFault(ShutdownOutcome outcome) => outcome == ShutdownOutcome.FactoryFailure
        || outcome == ShutdownOutcome.StartupFailure
        || outcome == ShutdownOutcome.StopFault
        || outcome == ShutdownOutcome.ExecutionFault
        || outcome == ShutdownOutcome.ExecutionCancellationUnverified
        || outcome == ShutdownOutcome.CleanupFailure;
}
