using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk;
using Kazo.Os.AppSdk.Threading;
using Kazo.Os.Tests.Support;
using Xunit;

namespace Kazo.Os.Samples.WindowApp.Tests;

/// <summary>The sample's background task (ADR 0128), run inline as an app's unit test runs it.</summary>
public sealed class SampleJobsTests
{
    [Fact]
    public async Task CountPrimes_counts_and_reports_its_pid()
    {
        MinimalAppExecutionContext context = new(AppDefinitionMapper.ToManifest(new WindowSampleApp()), processId: Kazo.Os.Simulation.Abstractions.Processes.ProcessId.FromInt64(9));
        using IDisposable scope = HackExecutionContext.Enter(context);
        List<HackBackgroundProgress> reports = [];

        PrimeCountResult result = await HackBackground.RunAsync<PrimeCountInput, PrimeCountResult>(
            SampleJobs.CountPrimes, new PrimeCountInput(100_000), new Collect(reports));

        Assert.Equal(new PrimeCountResult(9592, 99991), result);
        Assert.StartsWith("pid 9 · ", reports[^1].Message, StringComparison.Ordinal);
        Assert.Equal(1d, reports[^1].Fraction);
    }

    private sealed class Collect(List<HackBackgroundProgress> reports) : IProgress<HackBackgroundProgress>
    {
        public void Report(HackBackgroundProgress value) => reports.Add(value);
    }
}
