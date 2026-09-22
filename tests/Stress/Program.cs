using KeelMatrix.ShutdownSpec;
using Microsoft.Extensions.Hosting;

var iterations = GetInt("--iterations", 1000);
var maxSeconds = GetInt("--max-seconds", 300);
var stopwatch = System.Diagnostics.Stopwatch.StartNew();
var cases = new[]
{
    new StressCase("direct", RunDirectAsync, ExpectClean),
    new StressCase("background", RunBackgroundAsync, ExpectClean),
    new StressCase("immediate-stop", RunImmediateStopAsync, ExpectClean),
    new StressCase("ignored-cancellation", RunIgnoredCancellationAsync, ExpectServiceNoncompletion),
    new StressCase("delayed-completion", RunDelayedCompletionAsync, ExpectClean),
    new StressCase("synchronous-blocking", RunSynchronousBlockingAsync, ExpectStartupNoncompletion),
    new StressCase("execution-fault", RunExecutionFaultAsync, ExpectExecutionFault),
    new StressCase("shutdown-deadline", RunShutdownDeadlineAsync, ExpectServiceNoncompletion),
    new StressCase("unrelated-cancellation", RunUnrelatedCancellationAsync, ExpectUnverifiedCancellation)
};

for (var iteration = 0; iteration < iterations; iteration++)
{
    foreach (var stressCase in cases)
    {
        var result = await stressCase.Run();
        stressCase.Summary.Add(result);
        stressCase.Validate(result, iteration);
    }

    if (stopwatch.Elapsed > TimeSpan.FromSeconds(maxSeconds))
    {
        throw new TimeoutException($"Stress run exceeded {maxSeconds}s at iteration {iteration}.");
    }
}

stopwatch.Stop();
Console.WriteLine($"os={System.Runtime.InteropServices.RuntimeInformation.OSDescription}");
Console.WriteLine($"runtime={System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}");
Console.WriteLine($"hostingAssembly={typeof(IHostedService).Assembly.GetName().Version}");
Console.WriteLine($"hostingInformationalVersion={typeof(IHostedService).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false).OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion ?? "unknown"}");
Console.WriteLine($"iterations={iterations}; cases={cases.Length}; durationMs={stopwatch.Elapsed.TotalMilliseconds:0.###}");
foreach (var stressCase in cases)
{
    Console.WriteLine($"case={stressCase.Name}; outcomes={stressCase.Summary.FormatOutcomes()}; cleanupCompleted={stressCase.Summary.CleanupCompleted}; cleanupIncomplete={stressCase.Summary.CleanupIncomplete}; elapsedMs={stressCase.Summary.TotalElapsed.TotalMilliseconds:0.###}; minMs={stressCase.Summary.MinElapsed.TotalMilliseconds:0.###}; maxMs={stressCase.Summary.MaxElapsed.TotalMilliseconds:0.###}");
}

static int GetInt(string name, int fallback)
{
    var args = Environment.GetCommandLineArgs();
    var index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length && int.TryParse(args[index + 1], out var value) && value > 0 ? value : fallback;
}

static async Task<ShutdownResult> RunDirectAsync()
{
    var start = new ShutdownProbe("direct-start");
    var stop = new ShutdownProbe("direct-stop");
    return await ShutdownHarness
        .For(() => new DirectStressService(start, stop))
        .WithProbe(start)
        .WithProbe(stop)
        .WithStartupDeadline(TimeSpan.FromMilliseconds(50))
        .WithShutdownDeadline(TimeSpan.FromMilliseconds(50))
        .WithHarnessDeadline(TimeSpan.FromMilliseconds(250))
        .RunAsync();
}

static async Task<ShutdownResult> RunBackgroundAsync()
{
    var entry = new ShutdownProbe("background-entry");
    var stopping = new ShutdownProbe("background-stopping");
    return await ShutdownHarness
        .For(() => new CleanStressService(entry, stopping))
        .WithReadinessProbe(entry)
        .WithExecutionProbe(entry)
        .WithStoppingProbe(stopping)
        .WithProbe(stopping)
        .WithShutdownDeadline(TimeSpan.FromMilliseconds(50))
        .WithHarnessDeadline(TimeSpan.FromMilliseconds(250))
        .RunAsync();
}

static async Task<ShutdownResult> RunImmediateStopAsync()
{
    var stop = new ShutdownProbe("immediate-stop");
    return await ShutdownHarness
        .For(() => new ImmediateStopStressService(stop))
        .WithProbe(stop)
        .WithStartupDeadline(TimeSpan.FromMilliseconds(50))
        .WithShutdownDeadline(TimeSpan.FromMilliseconds(50))
        .WithHarnessDeadline(TimeSpan.FromMilliseconds(250))
        .RunAsync();
}

static async Task<ShutdownResult> RunIgnoredCancellationAsync()
{
    var service = new IgnoredCancellationStressService();
    var result = await ShutdownHarness
        .For(service)
        .WithShutdownDeadline(TimeSpan.FromMilliseconds(2))
        .WithHarnessDeadline(TimeSpan.FromMilliseconds(100))
        .WithCleanupDeadline(TimeSpan.FromMilliseconds(2))
        .RunAsync();
    service.Release();
    await service.Completed;
    return result;
}

static async Task<ShutdownResult> RunDelayedCompletionAsync()
{
    var entry = new ShutdownProbe("delayed-entry");
    return await ShutdownHarness
        .For(() => new DelayedCompletionStressService(entry))
        .WithExecutionProbe(entry)
        .WithShutdownDeadline(TimeSpan.FromMilliseconds(100))
        .WithHarnessDeadline(TimeSpan.FromMilliseconds(250))
        .RunAsync();
}

static async Task<ShutdownResult> RunSynchronousBlockingAsync()
{
    var service = new SynchronousBlockingStressService();
    var result = await ShutdownHarness
        .For(service)
        .WithStartupDeadline(TimeSpan.FromMilliseconds(2))
        .WithHarnessDeadline(TimeSpan.FromMilliseconds(100))
        .WithCleanupDeadline(TimeSpan.FromMilliseconds(2))
        .RunAsync();
    service.Release();
    await service.Completed;
    return result;
}

static async Task<ShutdownResult> RunExecutionFaultAsync()
{
    var entry = new ShutdownProbe("fault-entry");
    return await ShutdownHarness
        .For(() => new ExecutionFaultStressService(entry))
        .WithReadinessProbe(entry)
        .WithStartupDeadline(TimeSpan.FromMilliseconds(100))
        .WithShutdownDeadline(TimeSpan.FromMilliseconds(100))
        .WithHarnessDeadline(TimeSpan.FromMilliseconds(250))
        .RunAsync();
}

static async Task<ShutdownResult> RunShutdownDeadlineAsync()
{
    var entry = new ShutdownProbe("deadline-entry");
    var service = new ShutdownDeadlineStressService(entry);
    var result = await ShutdownHarness
        .For(service)
        .WithReadinessProbe(entry)
        .WithExecutionProbe(entry)
        .WithShutdownDeadline(TimeSpan.FromMilliseconds(2))
        .WithHarnessDeadline(TimeSpan.FromMilliseconds(100))
        .WithCleanupDeadline(TimeSpan.FromMilliseconds(2))
        .RunAsync();
    service.Release();
    await service.Completed;
    return result;
}

static async Task<ShutdownResult> RunUnrelatedCancellationAsync()
{
    var entry = new ShutdownProbe("unrelated-entry");
    var stopping = new ShutdownProbe("unrelated-stopping");
    return await ShutdownHarness
        .For(() => new UnrelatedCancellationStressService(entry, stopping))
        .WithReadinessProbe(entry)
        .WithExecutionProbe(entry)
        .WithStoppingProbe(stopping)
        .WithShutdownDeadline(TimeSpan.FromMilliseconds(100))
        .WithHarnessDeadline(TimeSpan.FromMilliseconds(250))
        .RunAsync();
}

static void ExpectClean(ShutdownResult result, int iteration)
{
    if (result.Outcome != ShutdownOutcome.CleanCompletion || !result.Succeeded)
    {
        throw new InvalidOperationException($"Clean stress case failed at iteration {iteration}: {result.ToDiagnosticString()}");
    }
}

static void ExpectServiceNoncompletion(ShutdownResult result, int iteration)
{
    if (result.Outcome != ShutdownOutcome.ServiceNoncompletion || result.Succeeded)
    {
        throw new InvalidOperationException($"Noncompletion stress case failed at iteration {iteration}: {result.ToDiagnosticString()}");
    }
}

static void ExpectStartupNoncompletion(ShutdownResult result, int iteration)
{
    if (result.Outcome != ShutdownOutcome.StartupNoncompletion || result.Succeeded)
    {
        throw new InvalidOperationException($"Blocking-start stress case failed at iteration {iteration}: {result.ToDiagnosticString()}");
    }
}

static void ExpectExecutionFault(ShutdownResult result, int iteration)
{
    if (result.Outcome != ShutdownOutcome.ExecutionFault || result.Succeeded)
    {
        throw new InvalidOperationException($"Execution-fault stress case failed at iteration {iteration}: {result.ToDiagnosticString()}");
    }
}

static void ExpectUnverifiedCancellation(ShutdownResult result, int iteration)
{
    if (result.Outcome != ShutdownOutcome.ExecutionCancellationUnverified || result.Succeeded)
    {
        throw new InvalidOperationException($"Unrelated-cancellation stress case failed at iteration {iteration}: {result.ToDiagnosticString()}");
    }
}

sealed class StressCase
{
    public StressCase(string name, Func<Task<ShutdownResult>> run, Action<ShutdownResult, int> validate)
    {
        Name = name;
        Run = run;
        Validate = validate;
        Summary = new StressSummary();
    }

    public string Name { get; }
    public Func<Task<ShutdownResult>> Run { get; }
    public Action<ShutdownResult, int> Validate { get; }
    public StressSummary Summary { get; }
}

sealed class StressSummary
{
    private readonly Dictionary<ShutdownOutcome, int> _outcomes = new();

    public int CleanupCompleted { get; private set; }
    public int CleanupIncomplete { get; private set; }
    public TimeSpan TotalElapsed { get; private set; }
    public TimeSpan MinElapsed { get; private set; } = TimeSpan.MaxValue;
    public TimeSpan MaxElapsed { get; private set; }

    public void Add(ShutdownResult result)
    {
        _outcomes[result.Outcome] = _outcomes.TryGetValue(result.Outcome, out var count) ? count + 1 : 1;
        if (result.CleanupCompleted) CleanupCompleted++; else CleanupIncomplete++;
        TotalElapsed += result.Elapsed;
        if (result.Elapsed < MinElapsed) MinElapsed = result.Elapsed;
        if (result.Elapsed > MaxElapsed) MaxElapsed = result.Elapsed;
    }

    public string FormatOutcomes() => string.Join(",", _outcomes.OrderBy(pair => pair.Key).Select(pair => $"{pair.Key}:{pair.Value}"));
}

sealed class DirectStressService : IHostedService
{
    private readonly ShutdownProbe _start;
    private readonly ShutdownProbe _stop;

    public DirectStressService(ShutdownProbe start, ShutdownProbe stop)
    {
        _start = start;
        _stop = stop;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _start.MarkObserved();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _stop.MarkObserved();
        return Task.CompletedTask;
    }
}

sealed class CleanStressService : BackgroundService
{
    private readonly ShutdownProbe _entry;
    private readonly ShutdownProbe _stopping;

    public CleanStressService(ShutdownProbe entry, ShutdownProbe stopping)
    {
        _entry = entry;
        _stopping = stopping;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _entry.MarkObserved();
        using var registration = _stopping.ObserveCancellation(stoppingToken);
        try { await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken); }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}

sealed class ImmediateStopStressService : IHostedService
{
    private readonly ShutdownProbe _stop;

    public ImmediateStopStressService(ShutdownProbe stop) => _stop = stop;
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _stop.MarkObserved();
        return Task.CompletedTask;
    }
}

sealed class IgnoredCancellationStressService : IHostedService
{
    private readonly TaskCompletionSource<bool> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StopAsync(CancellationToken cancellationToken) => _completion.Task;
    public Task Completed => _completion.Task;
    public void Release() => _completion.TrySetResult(true);
}

sealed class DelayedCompletionStressService : BackgroundService
{
    private readonly ShutdownProbe _entry;
    private readonly TaskCompletionSource<bool> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public DelayedCompletionStressService(ShutdownProbe entry) => _entry = entry;

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
        await Task.Delay(TimeSpan.FromMilliseconds(2));
        _completion.TrySetResult(true);
    }
}

sealed class SynchronousBlockingStressService : IHostedService
{
    private readonly TaskCompletionSource<bool> _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<bool> _completed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _release.Task.GetAwaiter().GetResult();
        _completed.TrySetResult(true);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task Completed => _completed.Task;
    public void Release() => _release.TrySetResult(true);
}

sealed class ExecutionFaultStressService : BackgroundService
{
    private readonly ShutdownProbe _entry;

    public ExecutionFaultStressService(ShutdownProbe entry) => _entry = entry;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _entry.MarkObserved();
        await Task.Yield();
        throw new InvalidOperationException("synthetic execution fault");
    }
}

sealed class ShutdownDeadlineStressService : BackgroundService
{
    private readonly ShutdownProbe _entry;
    private readonly TaskCompletionSource<bool> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ShutdownDeadlineStressService(ShutdownProbe entry) => _entry = entry;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _entry.MarkObserved();
        return _completion.Task;
    }
    public override Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task Completed => _completion.Task;
    public void Release() => _completion.TrySetResult(true);
}

sealed class UnrelatedCancellationStressService : BackgroundService
{
    private readonly ShutdownProbe _entry;
    private readonly ShutdownProbe _stopping;

    public UnrelatedCancellationStressService(ShutdownProbe entry, ShutdownProbe stopping)
    {
        _entry = entry;
        _stopping = stopping;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _entry.MarkObserved();
        using var registration = _stopping.ObserveCancellation(stoppingToken);
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            using var unrelated = new CancellationTokenSource();
            unrelated.Cancel();
            throw new OperationCanceledException(unrelated.Token);
        }
    }
}
