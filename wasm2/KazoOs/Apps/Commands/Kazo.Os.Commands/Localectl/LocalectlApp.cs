using Kazo.Os.App.Abstractions;

namespace Kazo.Os.Commands.Localectl;

/// <summary>
/// Manifest of "localectl" (ADR 0104, section 2). The build generates
/// <c>manifests/&lt;appId&gt;.json</c> from this class; it is never instantiated at runtime.
/// </summary>
[AppManifest]
public sealed class LocalectlApp : AppDefinition
{
    /// <inheritdoc />
    public override string Id => "org.kazoos.commands.localectl";

    /// <inheritdoc />
    public override string Name => "localectl";

    /// <inheritdoc />
    public override AppKind Kind => AppKind.Terminal;

    /// <inheritdoc />
    public override Type EntryPoint => typeof(LocalectlCommand);

    /// <inheritdoc />
    public override string PublisherId => "pub.kazoos";

    /// <inheritdoc />
    public override string Description => "Query or change the system locale";

    /// <inheritdoc />
    public override AppLaunchVisibility LaunchVisibility => AppLaunchVisibility.Hidden;

    /// <inheritdoc />
    public override IReadOnlyList<string> Capabilities =>
        [AppCapabilities.FileSystemSystemRead, AppCapabilities.FileSystemSystemWrite];

    /// <inheritdoc />
    public override TerminalCommand? Terminal => new("localectl", "localectl [status | list-locales | set-locale VARIABLE=LOCALE...]", []);

    /// <inheritdoc />
    public override AppResourceProfileManifest Resources => new(
        BaselineCpuWeight: 0.0,
        BurstCpuWeight: 0.01,
        BaselineMemoryWeight: 0.0,
        BurstMemoryWeight: 0.01,
        BaselineStorageIoWeight: 0.0,
        BurstStorageIoWeight: 0.01,
        BaselineNetworkIoWeight: 0.0,
        BurstNetworkIoWeight: 0.0);
}
