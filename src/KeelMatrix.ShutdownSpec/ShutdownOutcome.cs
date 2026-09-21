namespace KeelMatrix.ShutdownSpec;

/// <summary>Identifies the terminal classification of a shutdown scenario.</summary>
public enum ShutdownOutcome
{
    /// <summary>The service started and stopped within the configured contract.</summary>
    CleanCompletion,

    /// <summary>The service completed through expected cancellation.</summary>
    ExpectedCancellation,

    /// <summary>The execution task was canceled before its body became observable.</summary>
    ExecutionNotStarted,

    /// <summary>The service start operation failed.</summary>
    StartupFailure,

    /// <summary>The stop operation failed unexpectedly.</summary>
    StopFault,

    /// <summary>The observed execution task failed unexpectedly.</summary>
    ExecutionFault,

    /// <summary>The harness outer deadline expired before graceful shutdown began.</summary>
    HarnessDeadline,

    /// <summary>The caller cancellation token ended the scenario.</summary>
    CallerCancellation,

    /// <summary>The service remained incomplete at a shutdown or harness deadline.</summary>
    ServiceNoncompletion,

    /// <summary>Bounded cleanup failed after the primary scenario outcome.</summary>
    CleanupFailure,

    /// <summary>The service factory failed before startup began.</summary>
    FactoryFailure,

    /// <summary>The service factory did not return before the outer harness deadline.</summary>
    FactoryNoncompletion,

    /// <summary>The service did not complete startup or readiness before the startup deadline.</summary>
    StartupNoncompletion,

    /// <summary>Bounded cleanup did not return before its cleanup deadline.</summary>
    CleanupNoncompletion
}

/// <summary>Identifies the lifecycle phase reached when a scenario finished.</summary>
public enum ShutdownPhase
{
    /// <summary>No lifecycle operation has started.</summary>
    NotStarted,

    /// <summary>The service is being created.</summary>
    Creating,

    /// <summary>The service is starting.</summary>
    Starting,

    /// <summary>The service has started and is being observed.</summary>
    Started,

    /// <summary>Graceful shutdown is in progress.</summary>
    Stopping,

    /// <summary>Cleanup is in progress.</summary>
    CleaningUp,

    /// <summary>The scenario has completed.</summary>
    Completed
}

/// <summary>Describes the latest observable state of a BackgroundService execution task.</summary>
public enum ShutdownExecutionState
{
    /// <summary>No execution task was available.</summary>
    NotObserved,

    /// <summary>The execution task was still running when it was observed.</summary>
    Running,

    /// <summary>The execution task completed successfully.</summary>
    Completed,

    /// <summary>The execution task was canceled.</summary>
    Canceled,

    /// <summary>The execution task faulted.</summary>
    Faulted
}
