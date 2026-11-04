using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk;
using Kazo.Os.Simulation.Abstractions.Processes;
using Kazo.Os.Tests.Support;

namespace Kazo.Os.Platform.ResourceAccounting.Tests;

internal static class TestContexts
{
    public static IAppExecutionContext Process(long pid) => new MinimalAppExecutionContext(
        new AppManifest
        {
            Id = $"org.kazoos.test-{pid}",
            Name = $"Test {pid}",
            Version = "1.0.0",
            PublisherId = "org.kazoos",
            Description = "Resource accounting test process.",
            Kind = AppKind.Terminal,
            EntryPoint = new AppEntryPointManifest("Kazo.Os.Platform.ResourceAccounting.Tests", "Test"),
            SdkCompatibility = new AppSdkCompatibilityManifest(KazoOsSdk.Version),
            Presentation = new PresentationManifest("test", AppLaunchVisibility.Visible, []),
            Resources = AppResourceProfileManifest.None
        },
        processId: ProcessId.FromInt64(pid));
}
