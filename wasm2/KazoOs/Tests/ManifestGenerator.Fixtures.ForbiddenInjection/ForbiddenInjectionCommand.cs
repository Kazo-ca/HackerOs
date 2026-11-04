using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk;
using Kazo.Os.Simulation.Abstractions.Sessions;

namespace ManifestGenerator.Fixtures.ForbiddenInjection;

/// <summary>Fixture command asking the activator for a raw OS service (forbidden).</summary>
public sealed class ForbiddenInjectionCommand(AppManifest manifest, ISessionService sessions) : TerminalAppBase(manifest)
{
    /// <inheritdoc />
    public override ValueTask<int> ExecuteAsync(TerminalExecutionContext context, CancellationToken cancellationToken) =>
        ValueTask.FromResult(sessions.CurrentPrincipal is null ? 1 : 0);
}

/// <summary>Fixture definition.</summary>
[AppManifest]
public sealed class ForbiddenInjectionApp : AppDefinition
{
    public override string Id => "org.hackeros.fixtures.forbidden-injection";
    public override string Name => "forbidden-injection";
    public override AppKind Kind => AppKind.Terminal;
    public override Type EntryPoint => typeof(ForbiddenInjectionCommand);
    public override AppLaunchVisibility LaunchVisibility => AppLaunchVisibility.Hidden;
    public override TerminalCommand? Terminal => new("forbidden-injection", "forbidden-injection");
}
