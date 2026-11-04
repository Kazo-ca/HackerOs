using Kazo.Os.App.Abstractions;
using Kazo.Os.Platform.ResourceAccounting.Memory;
using Kazo.Os.Simulation.Abstractions.Processes;
using Kazo.Os.Simulation.Abstractions.Sessions;

namespace Kazo.Os.Ecosystem.Tests;

/// <summary>
/// ADR 0068, image layer, on the host's real catalog: every built-in app has its image, sized from
/// the very assemblies the host ships, and a real window app is measured with it.
/// </summary>
public sealed class MemoryAccountingImageTests
{
    [Fact]
    public void Every_built_in_app_has_an_image_whose_own_assembly_is_its_entry_point()
    {
        Assert.NotEmpty(BuildKnownLazyApps.Catalog.Manifests);
        foreach (AppManifest manifest in BuildKnownLazyApps.Catalog.Manifests.Values)
        {
            Assert.True(BuildKnownLazyApps.Images.TryGetValue(manifest.Id, out AppImage? image), manifest.Id);
            AppImageAssembly own = Assert.Single(image!.Assemblies, static assembly => !assembly.Shared);
            Assert.Equal(EntryAssemblyOf(manifest), own.Name, ignoreCase: true);
            Assert.True(own.Bytes > 0, manifest.Id);
            // The runtime and framework are the OS's own: never counted in an app's image.
            Assert.DoesNotContain(image.Assemblies, static assembly => assembly.Name.StartsWith("System.", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void The_text_editor_window_maps_its_real_assembly_and_the_sdk_and_pays_for_its_window()
    {
        AppImage image = BuildKnownLazyApps.Images["org.kazoos.text-editor"];
        string own = Path.Combine(AppContext.BaseDirectory, "Kazo.Os.Apps.TextEditor.dll");
        Assert.Equal(new FileInfo(own).Length, image.OwnBytes);
        Assert.Contains(image.Assemblies, static assembly => assembly.Shared && assembly.Name == "Kazo.Os.AppSdk.dll");

        FakeProbe probe = new();
        AllocationTracker allocations = new(probe);
        MeasuredMemoryAccountant accountant = new(allocations, new KernelMemoryLedger(), BuildKnownLazyApps.Images, probe);
        ProcessRecord editor = Record(31, "org.kazoos.text-editor", AppKind.Window);
        ProcessRecord ls = Record(32, "org.kazoos.cmd.ls", AppKind.Terminal);
        ProcessRecord cat = Record(33, "org.kazoos.cmd.cat", AppKind.Terminal);

        Assert.True(accountant.TryGetProcessMemory(editor, [editor, ls, cat], out ProcessMemoryUsage usage));
        Assert.Equal(image.OwnBytes, usage.ImageBytes);
        Assert.Equal(image.TotalBytes, usage.FileBytes);
        Assert.Equal(MemoryAccountingConstants.WindowBytes, usage.KernelBytes);

        // ls and cat are applets of one assembly: mapped once machine-wide, like BusyBox.
        Assert.True(accountant.TryGetSystemMemory([editor, ls, cat], out SystemMemoryUsage system));
        long expected = new[] { editor, ls, cat }
            .SelectMany(process => BuildKnownLazyApps.Images[process.AppId].Assemblies)
            .DistinctBy(static assembly => assembly.Name)
            .Sum(static assembly => assembly.Bytes);
        Assert.Equal(expected, system.MappedBytes);
    }

    private static string EntryAssemblyOf(AppManifest manifest)
    {
        string assembly = manifest.EntryPoint?.Assembly ?? manifest.Platform!.EntryPoints[0].Assembly;
        return assembly.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? assembly : assembly + ".dll";
    }

    private static ProcessRecord Record(long pid, string appId, AppKind kind)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        return new ProcessRecord(
            ProcessId.FromInt64(pid), null, appId, AppInstanceId.FromGuid(Guid.NewGuid()), kind,
            LocalUserId.FromGuid(Guid.NewGuid()), SessionId.FromGuid(Guid.NewGuid()), ProcessState.Running,
            ResourceProfile.None, null, now, now, null, null, null);
    }

    private sealed class FakeProbe : IManagedHeapProbe
    {
        public int CollectionCount => 0;

        public long HeapSizeBytes => 0;

        public long GetAllocatedBytesForCurrentThread() => 0;
    }
}
