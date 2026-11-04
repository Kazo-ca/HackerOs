using System.Text.Json;
using Kazo.Os.AppSdk.Diagnostics;
using Kazo.Os.Platform.Core.Isolation;
using Kazo.Os.Platform.WorkerHost.Protocol;

namespace Kazo.Os.Platform.WorkerHost.Tests;

/// <summary>
/// <c>KazoCrossOriginIsolation.GetStatusAsync</c> (ADR 0146): the status the boot probe recorded, with
/// no capability, the same on the main thread and in a worker.
/// </summary>
public sealed class CrossOriginIsolationStatusTests
{
    private static readonly CrossOriginIsolationStatus Isolated = new(
        CrossOriginIsolationMode.Auto, true, CrossOriginIsolationReason.None, "credentialless",
        SharedPipes: true, SynchronousCalls: true, MemoryMeasurement: true, TimeSpan.FromTicks(50));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_app_reads_the_isolation_without_any_capability(bool workers)
    {
        await using OsFixture os = await OsFixture.CreateAsync(workers: workers);
        os.Startup.MarkStarted();
        os.Isolation.Status = Isolated;

        ShellRun run = await os.RunAsync("sample-cmd --isolation").WaitAsync(TimeSpan.FromSeconds(20));

        Assert.True(run.ExitCode == 0, run.Stderr);
        Assert.Equal("isolation: yes None", run.Stdout.Trim());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Outside_a_browser_the_reason_is_not_browser(bool workers)
    {
        await using OsFixture os = await OsFixture.CreateAsync(workers: workers);
        os.Startup.MarkStarted();

        ShellRun run = await os.RunAsync("sample-cmd --isolation").WaitAsync(TimeSpan.FromSeconds(20));

        Assert.True(run.ExitCode == 0, run.Stderr);
        Assert.Equal("isolation: no NotBrowser", run.Stdout.Trim());
    }

    [Fact]
    public void Every_field_crosses_the_worker_boundary()
    {
        KazoCrossOriginIsolationStatus status = AppCrossOriginIsolationGateway.ToSdk(Isolated with { Mode = CrossOriginIsolationMode.On });
        KazoCrossOriginIsolationStatus missing = AppCrossOriginIsolationGateway.ToSdk(CrossOriginIsolationStatus.NotBrowser with
        {
            Mode = CrossOriginIsolationMode.Off,
            Reason = CrossOriginIsolationReason.Setting
        });

        Assert.Equal(status, RoundTrip(status));
        Assert.Equal(missing, RoundTrip(missing));
        Assert.Null(RoundTrip(missing).EmbedderPolicy);
    }

    private static KazoCrossOriginIsolationStatus RoundTrip(KazoCrossOriginIsolationStatus status)
    {
        using MemoryStream buffer = new();
        using (Utf8JsonWriter writer = new(buffer))
        {
            writer.WriteStartObject();
            WireCodec.WriteCrossOriginIsolation(writer, status);
            writer.WriteEndObject();
        }

        using JsonDocument document = JsonDocument.Parse(buffer.ToArray());
        return WireCodec.ReadCrossOriginIsolation(document.RootElement);
    }
}
