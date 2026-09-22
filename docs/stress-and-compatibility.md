# Stress and hosting compatibility evidence

The Phase 0 fixture runs nine bounded cases through the shipping harness: direct `IHostedService`, clean `BackgroundService`, immediate stop, ignored cancellation, delayed execution completion, synchronous blocking startup, execution fault, shutdown deadline, and execution cancellation with an unrelated cancellation token. Each case runs independently and records outcome counts, cleanup completion/incompletion, elapsed totals, minimum/maximum duration, operating system, runtime, and hosting assembly version.

The CI command runs each case for 1,000 repetitions (9,000 bounded scenario executions per runtime comparison), which is the several-thousand-repetition Phase 0 gate.

The clean cases assert application-owned probes directly. The unrelated-cancellation case requires both the execution-entry and stopping-token probes to be observed, then requires `ExecutionCancellationUnverified`; it does not use the result's convenience flags as its probe oracle.

Run the several-thousand-iteration shipping-harness comparison on Windows and Linux:

```powershell
dotnet run --project tests/Stress/ShutdownSpec.Stress.Net8.csproj -c Release -- --iterations 1000 --max-seconds 300
Push-Location tests/Stress
dotnet run --project ShutdownSpec.Stress.csproj -c Release -f net10.0 -- --iterations 1000 --max-seconds 300
Pop-Location
```

The `net8.0` shipping-harness project intentionally consumes the package's supported `Microsoft.Extensions.Hosting` dependency. That package/runtime combination is not evidence of .NET 8 hosting-package semantics by itself. Run the isolated hosting fixtures for that comparison:

```powershell
dotnet run --project tests/Stress/HostingCompatibility/Net8/ShutdownSpec.HostingCompatibility.Net8.csproj -c Release
Push-Location tests/Stress
dotnet run --project HostingCompatibility/Net10/ShutdownSpec.HostingCompatibility.Net10.csproj -c Release
Pop-Location
```

The isolated fixtures do not reference ShutdownSpec. The .NET 8 fixture resolves `Microsoft.Extensions.Hosting` 8.0.1 and the .NET 10 fixture resolves 10.0.12, so the shipping package dependency boundary is unchanged. Each fixture proves host startup, background execution entry, stopping-token delivery, host stop completion, and cleanup, and prints the resolved assembly/informational version.

The Windows and Linux CI jobs run both shipping-harness comparisons and both isolated compatibility fixtures. Record the exact command, iteration count, operating system, runtime, hosting version, duration, per-case outcome counts, and cleanup counts for each run. The macOS job runs the Release suite and package gate but does not claim this Phase 0 matrix.
