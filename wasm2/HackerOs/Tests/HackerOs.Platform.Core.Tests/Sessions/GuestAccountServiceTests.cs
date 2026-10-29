using HackerOs.App.Abstractions;
using HackerOs.Platform.Core.Sessions;
using HackerOs.Platform.Core.Tests.Settings;
using HackerOs.Simulation.Abstractions.Diagnostics;
using HackerOs.Simulation.Abstractions.Sessions;

namespace HackerOs.Platform.Core.Tests.Sessions;

/// <summary>
/// ADR 0187 sections 1 to 3: the guest account exists only while no account exists, has no
/// password, is an administrator, keeps its home, and is never created again once an account exists.
/// </summary>
public sealed class GuestAccountServiceTests
{
    private readonly SettingsFilesHarness _harness = new(withAccounts: false);
    private readonly GuestAccountService _guests;

    public GuestAccountServiceTests()
    {
        _guests = new GuestAccountService(_harness.Users, _harness.Groups, _harness.Audit);
    }

    [Fact]
    public async Task The_guest_is_a_marked_passwordless_administrator_with_its_own_group()
    {
        Assert.True(await _guests.CanCreateAsync());

        LocalUser guest = await _guests.CreateAsync(" Invité ");

        Assert.True(guest.IsGuest);
        Assert.Equal("guest", guest.LoginName.Value);
        Assert.Equal("Invité", guest.DisplayName);
        Assert.Null(guest.Credential);
        Assert.Equal(AppAuthority.Administrator, guest.Authority);
        Assert.Equal("guest", (await _harness.Groups.FindByIdAsync(guest.PrimaryGroupId))!.Name.Value);
        LocalGroup administrators = (await _harness.Groups.FindByNameAsync(LocalLoginName.Parse(LocalAccountService.AdministratorsGroupName)))!;
        Assert.Contains(administrators.Id, guest.AdditionalGroupIds);
        Assert.Contains(_harness.Audit.Entries, entry => entry.Action == "users.guest-create" && entry.Outcome == AuditOutcome.Success);
    }

    [Fact]
    public async Task The_guest_signs_in_without_a_password_and_gets_a_home()
    {
        LocalUser guest = await _guests.CreateAsync("Guest");

        AuthenticatedPrincipal principal = await _harness.Session.LoginAsync(guest.LoginName, password: null);

        Assert.Equal(guest.Id, principal.UserId);
        Assert.Equal("/home/guest", principal.HomePath);
        Assert.NotNull(await _harness.StatAsync("/home/guest"));
    }

    [Fact]
    public async Task A_second_guest_is_refused_and_logged()
    {
        await _guests.CreateAsync("Guest");

        Assert.False(await _guests.CanCreateAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => _guests.CreateAsync("Guest").AsTask());
        Assert.Contains(_harness.Audit.Entries, entry => entry.Action == "users.guest-create" && entry.Outcome == AuditOutcome.Denied);
    }

    [Fact]
    public async Task No_guest_once_a_registered_account_exists_even_a_disabled_one()
    {
        LocalGroup group = _harness.Groups.CreateGroup(LocalLoginName.Parse("carol"));
        LocalUser carol = _harness.Users.CreateUser(
            LocalLoginName.Parse("carol"), "Carol", AppAuthority.User, group.Id,
            credential: LocalPasswordHasher.Create(SettingsFilesHarness.Password, iterations: 10));
        _harness.Users.SetEnabled(carol.Id, enabled: false);

        Assert.False(await _guests.CanCreateAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => _guests.CreateAsync("Guest").AsTask());
        Assert.DoesNotContain(await _harness.Users.GetAllAsync(), user => user.IsGuest);
    }

    [Fact]
    public async Task The_guest_is_no_longer_alone_when_another_account_exists()
    {
        LocalUser guest = await _guests.CreateAsync("Guest");
        Assert.Equal(guest.Id, (await _guests.FindLoneGuestAsync())!.Id);

        LocalGroup group = _harness.Groups.CreateGroup(LocalLoginName.Parse("carol"));
        _harness.Users.CreateUser(LocalLoginName.Parse("carol"), "Carol", AppAuthority.User, group.Id);

        Assert.Null(await _guests.FindLoneGuestAsync());
    }

    [Fact]
    public async Task A_change_to_the_guest_account_keeps_its_mark()
    {
        LocalUser guest = await _guests.CreateAsync("Guest");

        LocalUser updated = await _harness.Users.SetPasskeysAsync(guest.Id, []);

        Assert.True(updated.IsGuest);
    }

    [Fact]
    public async Task A_guest_who_set_a_password_signs_in_with_it_like_any_account()
    {
        LocalUser guest = await _guests.CreateAsync("Guest");

        await _harness.Users.SetCredentialAsync(guest.Id, LocalPasswordHasher.Create(SettingsFilesHarness.Password, iterations: 10));

        Assert.Null(await _guests.FindLoneGuestAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => _harness.Session.LoginAsync(guest.LoginName, password: null));
    }
}
