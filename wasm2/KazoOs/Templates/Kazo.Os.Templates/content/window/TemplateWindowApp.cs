using Kazo.Os.App.Abstractions;

namespace Kazo.Os.Template.Window;

/// <summary>
/// The app's manifest. The build generates <c>manifest.json</c> from this class (Kazo.Os.App.Build);
/// it is never instantiated at run time.
/// </summary>
[AppManifest]
public sealed class TemplateWindowApp : AppDefinition
{
    /// <inheritdoc />
    public override string Id => "com.example.template-app";

    /// <inheritdoc />
    public override string Name => "Kazo.Os.Template.Window";

    /// <inheritdoc />
    public override AppKind Kind => AppKind.Window;

    /// <inheritdoc />
    public override Type EntryPoint => typeof(MainWindow);

    /// <inheritdoc />
    public override string PublisherId => "pub.template";

    /// <inheritdoc />
    public override string Description => "A Kazo OS window app.";

    /// <inheritdoc />
    public override IReadOnlyList<string> Capabilities => [AppCapabilities.FileSystemUserHomeRead];
}
