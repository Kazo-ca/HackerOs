using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk;

namespace ManifestGenerator.Fixtures.ForbiddenReference;

/// <summary>Fixture command; valid on its own, the project reference is what breaks the rule.</summary>
public sealed class ForbiddenReferenceCommand(AppManifest manifest) : TerminalAppBase(manifest)
{
    /// <inheritdoc />
    public override ValueTask<int> ExecuteAsync(TerminalExecutionContext context, CancellationToken cancellationToken) =>
        ValueTask.FromResult(0);
}

/// <summary>Fixture definition.</summary>
[AppManifest]
public sealed class ForbiddenReferenceApp : AppDefinition
{
    public override string Id => "org.hackeros.fixtures.forbidden-reference";
    public override string Name => "forbidden-reference";
    public override AppKind Kind => AppKind.Terminal;
    public override Type EntryPoint => typeof(ForbiddenReferenceCommand);
    public override AppLaunchVisibility LaunchVisibility => AppLaunchVisibility.Hidden;
    public override TerminalCommand? Terminal => new("forbidden-reference", "forbidden-reference");
}
