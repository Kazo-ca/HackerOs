using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk;

namespace ManifestGenerator.Fixtures.SingleApp;

/// <summary>Fixture command.</summary>
public sealed class HelloCommand(AppManifest manifest) : TerminalAppBase(manifest)
{
    /// <inheritdoc />
    public override ValueTask<int> ExecuteAsync(TerminalExecutionContext context, CancellationToken cancellationToken) =>
        ValueTask.FromResult(0);
}

/// <summary>Fixture definition: one app in the project produces manifest.json.</summary>
[AppManifest]
public sealed class HelloApp : AppDefinition
{
    public override string Id => "org.hackeros.fixtures.hello";
    public override string Name => "hello";
    public override AppKind Kind => AppKind.Terminal;
    public override Type EntryPoint => typeof(HelloCommand);
    public override AppLaunchVisibility LaunchVisibility => AppLaunchVisibility.Hidden;
    public override TerminalCommand? Terminal => new("hello", "hello");
}
