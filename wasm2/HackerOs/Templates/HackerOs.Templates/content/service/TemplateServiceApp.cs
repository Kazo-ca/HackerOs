using HackerOs.App.Abstractions;

namespace HackerOs.Template.Service;

/// <summary>
/// The service's manifest. The build generates <c>manifest.json</c> from this class
/// (HackerOs.App.Build); it is never instantiated at run time. <see cref="AutoStart"/> starts it at
/// login, like a systemd user unit.
/// </summary>
[AppManifest]
public sealed class TemplateServiceApp : AppDefinition
{
    /// <inheritdoc />
    public override string Id => "com.example.template-app";

    /// <inheritdoc />
    public override string Name => "HackerOs.Template.Service";

    /// <inheritdoc />
    public override AppKind Kind => AppKind.Service;

    /// <inheritdoc />
    public override Type EntryPoint => typeof(Service);

    /// <inheritdoc />
    public override string PublisherId => "pub.template";

    /// <inheritdoc />
    public override string Description => "A Kazo OS background service.";

    /// <inheritdoc />
    public override AppLaunchVisibility LaunchVisibility => AppLaunchVisibility.Hidden;

    /// <inheritdoc />
    public override bool AutoStart => true;
}
