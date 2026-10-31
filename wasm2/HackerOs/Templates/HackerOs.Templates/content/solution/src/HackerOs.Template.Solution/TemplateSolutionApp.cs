using HackerOs.App.Abstractions;

namespace HackerOs.Template.Solution;

/// <summary>
/// The app's manifest. The build generates <c>manifest.json</c> from this class (HackerOs.App.Build);
/// it is never instantiated at run time.
/// </summary>
[AppManifest]
public sealed class TemplateSolutionApp : AppDefinition
{
    /// <inheritdoc />
    public override string Id => "com.example.template-app";

    /// <inheritdoc />
    public override string Name => "HackerOs.Template.Solution";

    /// <summary>
    /// The app's version. A tag v&lt;version&gt; publishes it to the Hack Store
    /// (.github/workflows/publish-to-hack-store.yml): change it here, then tag.
    /// </summary>
    public override string Version => "1.0.0";

    /// <inheritdoc />
    public override AppKind Kind => AppKind.Window;

    /// <inheritdoc />
    public override Type EntryPoint => typeof(MainWindow);

    /// <inheritdoc />
    public override string PublisherId => "pub.template";

    /// <inheritdoc />
    public override string Description => "A HackerOS window app.";

    /// <inheritdoc />
    public override IReadOnlyList<string> Capabilities => [AppCapabilities.FileSystemUserHomeRead];
}
