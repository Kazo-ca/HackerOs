using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk;

namespace ManifestGenerator.Fixtures.MultiApp;

/// <summary>Fixture command shared by both apps.</summary>
public sealed class NoopCommand(AppManifest manifest) : TerminalAppBase(manifest)
{
    /// <inheritdoc />
    public override ValueTask<int> ExecuteAsync(TerminalExecutionContext context, CancellationToken cancellationToken) =>
        ValueTask.FromResult(0);
}

/// <summary>First of two definitions: the project produces manifests/&lt;appId&gt;.json.</summary>
[AppManifest]
public sealed class AlphaApp : AppDefinition
{
    public override string Id => "org.kazoos.fixtures.alpha";
    public override string Name => "alpha";
    public override AppKind Kind => AppKind.Terminal;
    public override Type EntryPoint => typeof(NoopCommand);
    public override AppLaunchVisibility LaunchVisibility => AppLaunchVisibility.Hidden;
    public override TerminalCommand? Terminal => new("alpha", "alpha");
}

/// <summary>Second definition.</summary>
[AppManifest]
public sealed class BetaApp : AppDefinition
{
    public override string Id => "org.kazoos.fixtures.beta";
    public override string Name => "beta";
    public override AppKind Kind => AppKind.Terminal;
    public override Type EntryPoint => typeof(NoopCommand);
    public override AppLaunchVisibility LaunchVisibility => AppLaunchVisibility.Hidden;
    public override TerminalCommand? Terminal => new("beta", "beta", ["b"]);
}
