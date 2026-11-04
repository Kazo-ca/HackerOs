using Kazo.Os.App.Abstractions;

namespace Kazo.Os.Samples.TerminalApp;

/// <summary>
/// Manifest of "Sample Terminal App" (ADR 0047). The build generates <c>manifest.json</c> from this
/// class; it is never instantiated at runtime.
/// </summary>
[AppManifest]
public sealed class TerminalSampleApp : AppDefinition
{
    /// <inheritdoc />
    public override string Id => "org.kazoos.samples.terminal-app";

    /// <inheritdoc />
    public override string Name => "Sample Terminal App";

    /// <inheritdoc />
    public override AppKind Kind => AppKind.Terminal;

    /// <inheritdoc />
    public override Type EntryPoint => typeof(SampleTerminalApp);

    /// <summary>A command is invoked by name, like every built-in command: not listed in the launcher.</summary>
    public override AppLaunchVisibility LaunchVisibility => AppLaunchVisibility.Hidden;

    /// <inheritdoc />
    public override string PublisherId => "pub.kazoos";

    /// <inheritdoc />
    public override string Description =>
        "Complete sample Terminal command application demonstrating streams, arguments, VFS, cancellation, and exit codes";

    /// <inheritdoc />
    public override IReadOnlyList<string> Capabilities =>
        [AppCapabilities.FileSystemUserHomeRead, AppCapabilities.FileSystemUserHomeWrite];

    /// <inheritdoc />
    public override TerminalCommand? Terminal =>
        new("sample-cmd", "Executes sample SDK terminal command", ["sample"]);

    /// <inheritdoc />
    public override AppResourceProfileManifest Resources => new(
        BaselineCpuWeight: 0.01,
        BurstCpuWeight: 0.05,
        BaselineMemoryWeight: 0.01,
        BurstMemoryWeight: 0.02,
        BaselineStorageIoWeight: 0.0,
        BurstStorageIoWeight: 0.0,
        BaselineNetworkIoWeight: 0.0,
        BurstNetworkIoWeight: 0.0);
}
