using HackerOs.App.Abstractions;

namespace HackerOs.Template.Window;

/// <summary>
/// The app's manifest. The build generates <c>manifest.json</c> from this class (HackerOs.App.Build);
/// it is never instantiated at run time.
/// </summary>
[AppManifest]
public sealed class TemplateWindowApp : AppDefinition
{
    /// <inheritdoc />
    public override string Id => "com.example.template-app";

    /// <inheritdoc />
    public override string Name => "HackerOs.Template.Window";

    /// <inheritdoc />
    public override AppKind Kind => AppKind.Window;

    /// <inheritdoc />
    public override Type EntryPoint => typeof(MainWindow);

    /// <inheritdoc />
    public override string PublisherId => "pub.template";

    /// <inheritdoc />
    public override string Description => "A KazoOS window app.";

    /// <inheritdoc />
    public override IReadOnlyList<string> Capabilities => [AppCapabilities.FileSystemUserHomeRead];
}
