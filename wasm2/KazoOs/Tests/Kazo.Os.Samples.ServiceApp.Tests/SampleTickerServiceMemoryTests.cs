using Kazo.Os.App.Abstractions;
using Kazo.Os.Platform.Core.Events;
using Kazo.Os.Platform.Core.Policy;
using Kazo.Os.Platform.ResourceAccounting;
using Kazo.Os.Platform.ResourceAccounting.Memory;
using Kazo.Os.Simulation.Abstractions.Processes;
using Kazo.Os.Simulation.Abstractions.Sessions;
using Xunit;

namespace Kazo.Os.Samples.ServiceApp.Tests;

/// <summary>Runs alone: the context-switch hook is process-wide and refuses to install while a scope is open.</summary>
[CollectionDefinition(nameof(ProcessWideHookCollection), DisableParallelization = true)]
public sealed class ProcessWideHookCollection;

/// <summary>
/// ADR 0068 on a real service, unmodified (execution-isolation plan, section 5.1): the sample ticker
/// runs under the real context-switch hook and its memory is measured.
/// </summary>
[Collection(nameof(ProcessWideHookCollection))]
public sealed class SampleTickerServiceMemoryTests
{
    [Fact]
    public async Task A_running_service_is_measured_without_any_change_to_its_code()
    {
        AppManifest manifest = AppDefinitionMapper.ToManifest(new ServiceSampleApp());
        SampleTickerService service = new(manifest, TimeSpan.FromMilliseconds(5));
        SampleTickerServiceTests.TestExecutionContext context = new(
            manifest, new InMemoryEventBus(), new InMemoryTopicMessageBus(new CapabilityGrantRepository()));
        AllocationTracker allocations = new(GcManagedHeapProbe.Instance);
        MeasuredMemoryAccountant accountant = new(
            allocations, new KernelMemoryLedger(), new Dictionary<string, AppImage>(), GcManagedHeapProbe.Instance);

        using (ContextSwitchHook.Install(allocations))
        {
            using CancellationTokenSource cancellation = new();
            Task run = service.RunAsync(context, cancellation.Token);
            await Task.Delay(100);
            cancellation.Cancel();
            await run;
        }

        GC.Collect();
        ProcessRecord process = Record(context.ProcessId, manifest.Id);
        Assert.True(service.TickCount > 0);
        Assert.True(accountant.TryGetProcessMemory(process, [process], out ProcessMemoryUsage usage));
        // Every tick builds a status string, an event and a log entry: all charged to the service.
        Assert.True(usage.AllocatedBytes > 0, "KazoAllocBytes");
        Assert.True(usage.AnonymousBytes > 0, "RssAnon");
        Assert.Equal(0, usage.KernelBytes); // a service has no window
        Assert.Equal(usage.AnonymousBytes, usage.ResidentBytes); // no image known for a sample outside the host
    }

    private static ProcessRecord Record(ProcessId pid, string appId)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        return new ProcessRecord(
            pid, null, appId, AppInstanceId.FromGuid(Guid.NewGuid()), AppKind.Service,
            LocalUserId.FromGuid(Guid.NewGuid()), SessionId.FromGuid(Guid.NewGuid()), ProcessState.Running,
            ResourceProfile.None, ServiceHealth.Healthy, now, now, null, null, null);
    }
}
