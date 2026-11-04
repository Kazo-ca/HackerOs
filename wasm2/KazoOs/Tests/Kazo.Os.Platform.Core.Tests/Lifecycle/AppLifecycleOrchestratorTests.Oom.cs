using Kazo.Os.App.Abstractions;
using Kazo.Os.Platform.Core.Lifecycle;
using Kazo.Os.Simulation.Abstractions.Sessions;

namespace Kazo.Os.Platform.Core.Tests.Lifecycle;

/// <summary>ADR 0126, section 4: the <c>oom_score_adj</c> a launched app starts with.</summary>
public sealed partial class AppLifecycleOrchestratorTests
{
    [Fact]
    public async Task A_system_service_starts_at_minus_500_and_honors_its_declared_value()
    {
        AppManifest service = WaiterManifest();
        Fixture fixture = new([service.Id], service);
        AuthenticatedPrincipal principal = await fixture.LoginAsync();

        AppLaunchResult launch = await fixture.Orchestrator.LaunchAsync(new AppLaunchRequest(service.Id, principal, []));

        Assert.Equal(-500, fixture.Manager.OomScores.GetOrDefault(launch.Process!.Pid));

        AppManifest shielded = WaiterManifest() with { Id = "org.hackeros.shielded", OomScoreAdj = -1000 };
        Fixture declared = new([shielded.Id], shielded);
        AuthenticatedPrincipal other = await declared.LoginAsync();
        AppLaunchResult exempt = await declared.Orchestrator.LaunchAsync(new AppLaunchRequest(shielded.Id, other, []));
        Assert.Equal(-1000, declared.Manager.OomScores.GetOrDefault(exempt.Process!.Pid));
    }

    [Fact]
    public async Task A_non_system_app_cannot_shield_itself_but_may_volunteer()
    {
        AppManifest shielded = WaiterManifest() with { OomScoreAdj = -300 };
        Fixture fixture = new(shielded);
        AuthenticatedPrincipal principal = await fixture.LoginAsync();
        AppLaunchResult launch = await fixture.Orchestrator.LaunchAsync(new AppLaunchRequest(shielded.Id, principal, []));
        // A manifest the build did not check: the negative value is ignored.
        Assert.Equal(0, fixture.Manager.OomScores.GetOrDefault(launch.Process!.Pid));

        AppManifest volunteer = WaiterManifest() with { Id = "org.hackeros.volunteer", OomScoreAdj = 300 };
        Fixture second = new(volunteer);
        AuthenticatedPrincipal user = await second.LoginAsync();
        AppLaunchResult first = await second.Orchestrator.LaunchAsync(new AppLaunchRequest(volunteer.Id, user, []));
        Assert.Equal(300, second.Manager.OomScores.GetOrDefault(first.Process!.Pid));
    }
}
