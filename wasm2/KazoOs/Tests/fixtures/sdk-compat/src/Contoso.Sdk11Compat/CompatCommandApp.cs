using HackerOs.App.Abstractions;

namespace Contoso.Sdk11Compat;

/// <summary>Manifest of the <c>sdk11-compat</c> command (generated into its manifest file at build).</summary>
[AppManifest]
public sealed class CompatCommandApp : AppDefinition
{
    /// <inheritdoc />
    public override string Id => "com.contoso.sdk11-command";

    /// <inheritdoc />
    public override string Name => "SDK 1.1 Command";

    /// <inheritdoc />
    public override AppKind Kind => AppKind.Terminal;

    /// <inheritdoc />
    public override Type EntryPoint => typeof(CompatCommand);

    /// <inheritdoc />
    public override string PublisherId => "pub.contoso";

    /// <inheritdoc />
    public override string Description => "A command compiled against Kazo OS SDK 1.1.0.";

    /// <inheritdoc />
    public override IReadOnlyList<string> Capabilities => [AppCapabilities.FileSystemUserHomeRead, AppCapabilities.FileSystemUserHomeWrite];

    /// <inheritdoc />
    public override TerminalCommand? Terminal => new("sdk11-compat", "Writes, reads back and prints a note, and checks a password against the SDK 1.1 rule", []);
}
