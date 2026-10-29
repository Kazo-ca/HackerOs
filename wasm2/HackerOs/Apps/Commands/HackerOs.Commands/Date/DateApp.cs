using HackerOs.App.Abstractions;

namespace HackerOs.Commands.Date;

/// <summary>
/// Manifest of "date" (ADR 0193). The build generates
/// <c>manifests/&lt;appId&gt;.json</c> from this class; it is never instantiated at runtime.
/// </summary>
[AppManifest]
public sealed class DateApp : AppDefinition
{
    /// <inheritdoc />
    public override string Id => "org.hackeros.commands.date";

    /// <inheritdoc />
    public override string Name => "date";

    /// <inheritdoc />
    public override AppKind Kind => AppKind.Terminal;

    /// <inheritdoc />
    public override Type EntryPoint => typeof(DateCommand);

    /// <inheritdoc />
    public override string PublisherId => "pub.hackeros";

    /// <inheritdoc />
    public override string Description => "Print the current date and time in the time zone of the process";

    /// <inheritdoc />
    public override AppLaunchVisibility LaunchVisibility => AppLaunchVisibility.Hidden;

    /// <inheritdoc />
    public override IReadOnlyList<string> Capabilities => [];

    /// <inheritdoc />
    public override TerminalCommand? Terminal => new("date", "date [-u] [-I|-R] [+FORMAT]");

    /// <inheritdoc />
    public override AppResourceProfileManifest Resources => new(
        BaselineCpuWeight: 0.0,
        BurstCpuWeight: 0.01,
        BaselineMemoryWeight: 0.01,
        BurstMemoryWeight: 0.02,
        BaselineStorageIoWeight: 0.0,
        BurstStorageIoWeight: 0.0,
        BaselineNetworkIoWeight: 0.0,
        BurstNetworkIoWeight: 0.0);
}
