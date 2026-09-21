# Stress and hosting compatibility evidence

The repository contains a bounded stress fixture at `tests/Stress`. It uses independent execution-entry and stopping-token probes; it does not use `ShutdownResult.ExecutionEntered` or `StoppingTokenObserved` as its oracle. The clean fixture must remain `CleanCompletion` with both checkpoints observed, while the broken fixture must remain `ExecutionCancellationUnverified` with the stopping checkpoint absent.

Run the several-thousand-iteration comparison on Windows and Linux with the repository SDK:

```powershell
dotnet run --project tests/Stress/ShutdownSpec.Stress.Net8.csproj -c Release -- --iterations 3000 --max-seconds 180
Push-Location tests/Stress
dotnet run --project ShutdownSpec.Stress.csproj -c Release -f net10.0 -- --iterations 3000 --max-seconds 180
Pop-Location
```

The first command compares the supported .NET 8 runtime; the second compares the targeted current .NET 10 hosting/runtime surface. A run fails if the complete loop exceeds 180 seconds or if any independent outcome/checkpoint differs. Record the exact runtime, duration, iteration count, and clean/broken outcome counts in the task handoff for each operating system.
