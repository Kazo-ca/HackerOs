using HackerOs.App.Abstractions;
using HackerOs.Ecosystem;
using HackerOs.Simulation.Abstractions.Recovery;
using HackerOs.Simulation.Abstractions.Sessions;

namespace HackerOs.Ecosystem.Tests;

public sealed class EcosystemHostStateTests
{
    [Fact]
    public void Identity_load_selects_first_run_or_sorted_login()
    {
        EcosystemHostState state = new();

        state.CompleteIdentityLoad([]);
        Assert.Equal(EcosystemHostView.FirstRun, state.View);

        state.BeginInitialization();
        state.CompleteIdentityLoad([CreateUser("zed"), CreateUser("admin")]);

        Assert.Equal(EcosystemHostView.Login, state.View);
        Assert.Equal(["admin", "zed"], state.Users.Select(user => user.LoginName.Value));
    }

    [Fact]
    public void The_lone_guest_is_offered_without_a_password_form()
    {
        EcosystemHostState state = new();
        LocalUser guest = CreateUser("guest") with { IsGuest = true };

        state.CompleteIdentityLoad([guest]);
        Assert.Equal(EcosystemHostView.Login, state.View);
        Assert.Same(guest, state.LoneGuest);

        state.CompleteIdentityLoad([guest, CreateUser("alice")]);
        Assert.Null(state.LoneGuest);

        // The mark identifies the guest, not the login name.
        state.CompleteIdentityLoad([CreateUser("guest")]);
        Assert.Null(state.LoneGuest);
    }

    [Fact]
    public void Recovery_fatal_desktop_and_update_states_are_explicit()
    {
        EcosystemHostState state = new();
        Guid recoveryCorrelation = Guid.NewGuid();
        StorageRecoveryPresentation recovery = new(
            StorageRecoveryState.StorageUnavailable,
            "storage.unavailable",
            BlocksBoot: true,
            CanExport: false,
            StorageRecoveryActions.Retry,
            recoveryCorrelation);

        state.ShowRecovery(recovery);
        Assert.Equal(EcosystemHostView.Recovery, state.View);
        Assert.Same(recovery, state.Recovery);

        state.SetUpdateAvailable(true);
        Assert.True(state.UpdateAvailable);
        Assert.Null(state.AvailableVersion);

        // ADR 0174: the served version.json names the release that waits.
        state.SetAvailableVersion("2.2.0");
        Assert.True(state.UpdateAvailable);
        Assert.Equal("2.2.0", state.AvailableVersion);
        Assert.Equal(EcosystemHostView.Recovery, state.View);

        state.CompleteLogin();
        Assert.Equal(EcosystemHostView.Desktop, state.View);

        Guid fatalCorrelation = Guid.NewGuid();
        state.ShowFatal(fatalCorrelation);
        Assert.Equal(EcosystemHostView.Fatal, state.View);
        Assert.Equal(fatalCorrelation, state.FatalCorrelationId);
    }

    private static LocalUser CreateUser(string loginName)
    {
        DateTimeOffset now = DateTimeOffset.UnixEpoch;
        return new LocalUser(
            LocalUserId.FromGuid(Guid.NewGuid()),
            LocalLoginName.Parse(loginName),
            loginName,
            enabled: true,
            AppAuthority.User,
            LocalGroupId.FromGuid(Guid.NewGuid()),
            [],
            credential: null,
            revision: 1,
            now,
            now);
    }
}