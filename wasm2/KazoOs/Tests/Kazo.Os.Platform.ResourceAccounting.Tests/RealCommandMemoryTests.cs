using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk;
using Kazo.Os.Commands.Ls;
using Kazo.Os.Platform.ResourceAccounting.Memory;
using Kazo.Os.Simulation.Abstractions.Processes;
using Kazo.Os.Tests.Support;

namespace Kazo.Os.Platform.ResourceAccounting.Tests;

/// <summary>
/// ADR 0068 on a real command, unmodified (execution-isolation plan, section 5.1): <c>ls</c> runs
/// in its process frame, as the OS starts it, under the real context-switch hook.
/// </summary>
public sealed class RealCommandMemoryTests
{
    [Fact]
    public async Task Ls_is_measured_with_its_image_and_what_it_allocated()
    {
        AppManifest manifest = AppDefinitionMapper.ToManifest(new LsApp());
        ProcessId pid = ProcessId.FromInt64(4242);
        FakeAppFileSystemGateway files = new FakeAppFileSystemGateway().WithDirectory("/home/user");
        for (int i = 0; i < 50; i++)
        {
            files.WithFile($"/home/user/file-{i:D2}.txt", new string('x', i));
        }

        MinimalAppExecutionContext app = new(manifest, files, userId: "user", processId: pid);
        AppImage image = new([new("Kazo.Os.Commands.dll", 470_000, false), new("Kazo.Os.AppSdk.dll", 400_000, true)]);
        AllocationTracker allocations = new(GcManagedHeapProbe.Instance);
        MeasuredMemoryAccountant accountant = new(
            allocations, new KernelMemoryLedger(), new Dictionary<string, AppImage> { [manifest.Id] = image }, GcManagedHeapProbe.Instance);
        using StringWriter stdout = new();

        int exitCode;
        using (ContextSwitchHook.Install(allocations))
        {
            using (KazoExecutionContext.Enter(app, "/home/user"))
            {
                TerminalExecutionContext terminal = new(
                    app, ["-l"], TextReader.Null, stdout, TextWriter.Null, "/home/user", new Dictionary<string, string>());
                exitCode = await new LsCommand(manifest).ExecuteAsync(terminal, CancellationToken.None);
            }
        }

        ProcessRecord process = MemoryRecords.Process(pid.Value, manifest.Id);
        Assert.Equal(0, exitCode);
        Assert.Contains("file-49.txt", stdout.ToString(), StringComparison.Ordinal);
        Assert.True(accountant.TryGetProcessMemory(process, [process], out ProcessMemoryUsage usage));
        Assert.Equal(470_000, usage.ImageBytes);
        Assert.Equal(870_000, usage.FileBytes);
        Assert.True(usage.AllocatedBytes > 0, "KazoAllocBytes");
        Assert.Equal(0, usage.KernelBytes); // a command has no window
    }
}
