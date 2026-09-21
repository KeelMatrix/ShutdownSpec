using KeelMatrix.ShutdownSpec;
using Microsoft.Extensions.Hosting;

namespace PackageSmokeNetStandard;

public static class Consumer
{
    public static ShutdownHarness Create(IHostedService service) => ShutdownHarness.For(service);
}
