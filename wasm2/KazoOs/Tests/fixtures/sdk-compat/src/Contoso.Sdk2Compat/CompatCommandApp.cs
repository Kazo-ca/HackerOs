using Kazo.Os.App.Abstractions;

namespace Contoso.Sdk2Compat;

/// <summary>Manifest of the <c>sdk2-compat</c> command (generated into its manifest file at build).</summary>
[AppManifest]
public sealed class CompatCommandApp : AppDefinition
{
    /// <inheritdoc />
    public override string Id => "com.contoso.sdk2-command";

    /// <inheritdoc />
    public override string Name => "SDK 2.0 Command";

    /// <inheritdoc />
    public override AppKind Kind => AppKind.Terminal;

    /// <inheritdoc />
    public override Type EntryPoint => typeof(CompatCommand);

    /// <inheritdoc />
    public override string PublisherId => "pub.contoso";

    /// <inheritdoc />
    public override string Description => "A command compiled against Kazo OS SDK 2.0.0.";

    /// <inheritdoc />
    public override IReadOnlyList<string> Capabilities => [AppCapabilities.FileSystemUserHomeRead, AppCapabilities.FileSystemUserHomeWrite];

    /// <inheritdoc />
    public override TerminalCommand? Terminal => new("sdk2-compat", "Writes a note, copies it with the SDK 2.0 copy rules and prints the copy", []);
}
