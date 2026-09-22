using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var entry = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
var stopping = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
var stopwatch = System.Diagnostics.Stopwatch.StartNew();
using var host = Host.CreateDefaultBuilder()
    .ConfigureServices(services => services.AddSingleton<IHostedService>(new CompatibilityService(entry, stopping)))
    .Build();

await host.StartAsync();
await entry.Task.WaitAsync(TimeSpan.FromSeconds(2));
await host.StopAsync();
await stopping.Task.WaitAsync(TimeSpan.FromSeconds(2));
stopwatch.Stop();

var hostingAssembly = typeof(IHostedService).Assembly;
var hostingVersion = hostingAssembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
    ?? hostingAssembly.GetName().Version?.ToString()
    ?? "unknown";
Console.WriteLine($"os={System.Runtime.InteropServices.RuntimeInformation.OSDescription}");
Console.WriteLine($"runtime={System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}");
Console.WriteLine($"hostingAssembly={hostingAssembly.GetName().FullName}");
Console.WriteLine($"hostingInformationalVersion={hostingVersion}; outcome=CleanCompletion; entry=True; stopping=True; cleanup=True; durationMs={stopwatch.Elapsed.TotalMilliseconds:0.###}");

sealed class CompatibilityService : BackgroundService
{
    private readonly TaskCompletionSource<bool> _entry;
    private readonly TaskCompletionSource<bool> _stopping;

    public CompatibilityService(TaskCompletionSource<bool> entry, TaskCompletionSource<bool> stopping)
    {
        _entry = entry;
        _stopping = stopping;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _entry.TrySetResult(true);
        using var registration = stoppingToken.Register(static state => ((TaskCompletionSource<bool>)state!).TrySetResult(true), _stopping);
        try { await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken); }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}
