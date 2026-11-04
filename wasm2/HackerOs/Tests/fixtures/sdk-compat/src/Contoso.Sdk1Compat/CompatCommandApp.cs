using HackerOs.App.Abstractions;

namespace Contoso.Sdk1Compat;

/// <summary>Manifest of the <c>sdk1-compat</c> command (generated into its manifest file at build).</summary>
[AppManifest]
public sealed class CompatCommandApp : AppDefinition
{
    /// <inheritdoc />
    public override string Id => "com.contoso.sdk1-command";

    /// <inheritdoc />
    public override string Name => "SDK 1.0 Command";

    /// <inheritdoc />
    public override AppKind Kind => AppKind.Terminal;

    /// <inheritdoc />
    public override Type EntryPoint => typeof(CompatCommand);

    /// <inheritdoc />
    public override string PublisherId => "pub.contoso";

    /// <inheritdoc />
    public override string Description => "A command compiled against KazoOS SDK 1.0.0.";

    /// <inheritdoc />
    public override IReadOnlyList<string> Capabilities => [AppCapabilities.FileSystemUserHomeRead, AppCapabilities.FileSystemUserHomeWrite];

    /// <inheritdoc />
    public override TerminalCommand? Terminal => new("sdk1-compat", "Writes, reads back and prints a note", []);
}
