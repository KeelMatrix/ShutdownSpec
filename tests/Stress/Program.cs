using KeelMatrix.ShutdownSpec;
using Microsoft.Extensions.Hosting;

var iterations = GetInt("--iterations", 3000);
var maxSeconds = GetInt("--max-seconds", 120);
var stopwatch = System.Diagnostics.Stopwatch.StartNew();
var cleanCounts = new Dictionary<ShutdownOutcome, int>();
var brokenCounts = new Dictionary<ShutdownOutcome, int>();

for (var i = 0; i < iterations; i++)
{
    var cleanEntry = new ShutdownProbe("clean-entry");
    var cleanStop = new ShutdownProbe("clean-stop-invoked");
    var cleanStopping = new ShutdownProbe("clean-stopping");
    var clean = await ShutdownHarness
        .For(() => new CleanStressService(cleanEntry, cleanStop, cleanStopping))
        .WithReadinessProbe(cleanEntry)
        .WithExecutionProbe(cleanEntry)
        .WithStoppingProbe(cleanStopping)
        .WithShutdownDeadline(TimeSpan.FromMilliseconds(250))
        .WithHarnessDeadline(TimeSpan.FromSeconds(1))
        .RunAsync();
    Increment(cleanCounts, clean.Outcome);
    if (clean.Outcome != ShutdownOutcome.CleanCompletion || !cleanEntry.IsObserved || !cleanStop.IsObserved)
    {
        throw new InvalidOperationException($"Clean stress iteration {i} was unstable: {clean.ToDiagnosticString()}");
    }

    var brokenEntry = new ShutdownProbe("broken-entry");
    var brokenStop = new ShutdownProbe("broken-stop-invoked");
    var brokenStopping = new ShutdownProbe("broken-stopping");
    var broken = await ShutdownHarness
        .For(() => new BrokenAfterStopStressService(brokenEntry, brokenStop, brokenStopping))
        .WithReadinessProbe(brokenEntry)
        .WithExecutionProbe(brokenEntry)
        .WithStoppingProbe(brokenStopping)
        .WithShutdownDeadline(TimeSpan.FromMilliseconds(250))
        .WithHarnessDeadline(TimeSpan.FromSeconds(1))
        .RunAsync();
    Increment(brokenCounts, broken.Outcome);
    if (broken.Outcome != ShutdownOutcome.ExecutionCancellationUnverified || !brokenEntry.IsObserved || !brokenStop.IsObserved || broken.StoppingTokenObserved)
    {
        throw new InvalidOperationException($"Broken stress iteration {i} was unstable: {broken.ToDiagnosticString()}");
    }

    if (stopwatch.Elapsed > TimeSpan.FromSeconds(maxSeconds))
    {
        throw new TimeoutException($"Stress run exceeded {maxSeconds}s at iteration {i}.");
    }
}

stopwatch.Stop();
Console.WriteLine($"runtime={System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}");
Console.WriteLine($"iterations={iterations}; durationMs={stopwatch.Elapsed.TotalMilliseconds:0.###}; clean={Format(cleanCounts)}; broken={Format(brokenCounts)}");

static int GetInt(string name, int fallback)
{
    var index = Array.IndexOf(Environment.GetCommandLineArgs(), name);
    return index >= 0 && index + 1 < Environment.GetCommandLineArgs().Length && int.TryParse(Environment.GetCommandLineArgs()[index + 1], out var value) && value > 0 ? value : fallback;
}

static void Increment(Dictionary<ShutdownOutcome, int> counts, ShutdownOutcome outcome)
{
    counts[outcome] = counts.TryGetValue(outcome, out var count) ? count + 1 : 1;
}

static string Format(Dictionary<ShutdownOutcome, int> counts) => string.Join(",", counts.OrderBy(pair => pair.Key).Select(pair => $"{pair.Key}:{pair.Value}"));

sealed class CleanStressService : BackgroundService
{
    private readonly ShutdownProbe _entry;
    private readonly ShutdownProbe _stop;
    private readonly ShutdownProbe _stopping;

    public CleanStressService(ShutdownProbe entry, ShutdownProbe stop, ShutdownProbe stopping)
    {
        _entry = entry;
        _stop = stop;
        _stopping = stopping;
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        _stop.MarkObserved();
        return base.StopAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _entry.MarkObserved();
        using var registration = _stopping.ObserveCancellation(stoppingToken);
        try { await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken); }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}

sealed class BrokenAfterStopStressService : BackgroundService
{
    private readonly ShutdownProbe _entry;
    private readonly ShutdownProbe _stop;
    private readonly ShutdownProbe _stopping;
    private readonly TaskCompletionSource<bool> _stopStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public BrokenAfterStopStressService(ShutdownProbe entry, ShutdownProbe stop, ShutdownProbe stopping)
    {
        _entry = entry;
        _stop = stop;
        _stopping = stopping;
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        _stop.MarkObserved();
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
