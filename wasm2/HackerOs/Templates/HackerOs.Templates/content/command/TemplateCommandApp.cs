using HackerOs.App.Abstractions;

namespace HackerOs.Template.Command;

/// <summary>
/// The command's manifest. The build generates <c>manifest.json</c> from this class
/// (HackerOs.App.Build); it is never instantiated at run time. The command name must be unique in
/// the host and must not be a shell built-in (HOSAPP0022).
/// </summary>
[AppManifest]
public sealed class TemplateCommandApp : AppDefinition
{
    /// <inheritdoc />
    public override string Id => "com.example.template-app";

    /// <inheritdoc />
    public override string Name => "HackerOs.Template.Command";

    /// <inheritdoc />
    public override AppKind Kind => AppKind.Terminal;

    /// <inheritdoc />
    public override Type EntryPoint => typeof(Command);

    /// <inheritdoc />
    public override string PublisherId => "pub.template";

    /// <inheritdoc />
    public override string Description => "A KazoOS terminal command.";

    /// <inheritdoc />
    public override TerminalCommand? Terminal => new("hello", "Prints a greeting", []);
}
