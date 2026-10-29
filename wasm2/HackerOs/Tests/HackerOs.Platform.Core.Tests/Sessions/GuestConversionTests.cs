using HackerOs.App.Abstractions;
using HackerOs.AppSdk.Administration;
using HackerOs.Platform.Core.Sessions;
using HackerOs.Platform.Core.Tests.Settings;
using HackerOs.Simulation.Abstractions.Diagnostics;
using HackerOs.Simulation.Abstractions.Sessions;

namespace HackerOs.Platform.Core.Tests.Sessions;

/// <summary>
/// ADR 0187 section 4: "Create my account" from the guest. <em>Migrate</em> keeps the account
/// (same id, files moved to the new home, settings paths rewritten, password set, guest mark
/// removed); <em>Delete the guest</em> creates a new administrator and removes the guest with its
/// home. Both need the guest's session closed and are written to <c>auth.log</c>.
/// </summary>
public sealed class GuestConversionTests
{
    private const string NewPassword = "correct horse 42";

    private readonly SettingsFilesHarness _harness = new(withAccounts: false, withKeyring: true);
    private readonly GuestAccountService _guests;

    public GuestConversionTests()
    {
        _guests = new GuestAccountService(
            _harness.Users, _harness.Groups, _harness.Audit,
            fileSystem: _harness.FileSystem, session: _harness.Session, iterations: 10);
    }

    [Fact]
    public async Task Migrating_keeps_the_account_and_moves_its_files_to_the_new_home()
    {
        LocalUser guest = await StartGuestWithFilesAsync();

        LocalUser alice = await _guests.MigrateAsync(new HackNewUserAccount(" alice ", " Alice ", AppAuthority.User, NewPassword));

        Assert.Equal(guest.Id, alice.Id);
        Assert.False(alice.IsGuest);
        Assert.Equal("alice", alice.LoginName.Value);
        Assert.Equal("Alice", alice.DisplayName);
        Assert.Equal(AppAuthority.Administrator, alice.Authority);
        Assert.True(LocalPasswordHasher.Verify(NewPassword, alice.Credential!));
        Assert.Equal("alice", (await _harness.Groups.FindByIdAsync(alice.PrimaryGroupId))!.Name.Value);
        Assert.Null(await _harness.Groups.FindByNameAsync(LocalLoginName.Parse("guest")));

        Assert.Null(await _harness.StatAsync("/home/guest"));
        Assert.Equal("my notes", await _harness.ReadAsync("/home/alice/Documents/notes.txt"));
        Assert.Equal("alice", (await _harness.StatAsync("/home/alice/Documents/notes.txt"))!.OwnerId);
        Assert.Equal(
            "wallpaper=/home/alice/Pictures/sky.png\nother=/home/guests/x\n",
            await _harness.ReadAsync("/home/alice/.config/hackeros/desktop.conf"));
        Assert.Contains(_harness.Audit.Entries, entry => entry.Action == "users.guest-migrate" && entry.Outcome == AuditOutcome.Success);

        // The new account signs in with its password, and the keyring opens with it.
        AuthenticatedPrincipal principal = await _harness.Session.LoginAsync(alice.LoginName, NewPassword);
        Assert.Equal("/home/alice", principal.HomePath);
        Assert.Equal(alice.Id, _harness.Keyring!.UnlockedUserId);
        await _harness.WriteAsUserAsync("/home/alice/Documents/notes.txt", "still mine");
        await _harness.WriteAsUserAsync("/home/alice/Documents/new.txt", "new");
    }

    [Fact]
    public async Task Deleting_the_guest_creates_a_new_administrator_and_removes_the_guest_home()
    {
        LocalUser guest = await StartGuestWithFilesAsync();

        LocalUser bob = await _guests.ReplaceAsync(new HackNewUserAccount("bob", "Bob", AppAuthority.User, NewPassword));

        Assert.NotEqual(guest.Id, bob.Id);
        Assert.Equal(AppAuthority.Administrator, bob.Authority);
        Assert.False(bob.IsGuest);
        Assert.Null(await _harness.Users.FindByIdAsync(guest.Id));
        Assert.Null(await _harness.Groups.FindByNameAsync(LocalLoginName.Parse("guest")));
        Assert.Null(await _harness.StatAsync("/home/guest"));
        LocalGroup administrators = (await _harness.Groups.FindByNameAsync(LocalLoginName.Parse(LocalAccountService.AdministratorsGroupName)))!;
        Assert.Contains(administrators.Id, bob.AdditionalGroupIds);
        Assert.Contains(_harness.Audit.Entries, entry => entry.Action == "users.guest-delete" && entry.Outcome == AuditOutcome.Success);

        await _harness.Session.LoginAsync(bob.LoginName, NewPassword);
        Assert.Equal(bob.Id, _harness.Keyring!.UnlockedUserId);
    }

    [Fact]
    public async Task The_guest_can_never_come_back_after_a_conversion()
    {
        await StartGuestWithFilesAsync();
        await _guests.ReplaceAsync(new HackNewUserAccount("bob", "Bob", AppAuthority.Administrator, NewPassword));

        Assert.False(await _guests.CanCreateAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => _guests.CreateAsync("Guest").AsTask());
    }

    [Fact]
    public async Task A_conversion_is_refused_while_the_guest_is_signed_in()
    {
        LocalUser guest = await _guests.CreateAsync("Guest");
        await _harness.Session.LoginAsync(guest.LoginName, password: null);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _guests.MigrateAsync(new HackNewUserAccount("alice", "Alice", AppAuthority.Administrator, NewPassword)).AsTask());
        Assert.True((await _harness.Users.FindByIdAsync(guest.Id))!.IsGuest);
        Assert.Contains(_harness.Audit.Entries, entry => entry.Action == "users.guest-migrate" && entry.Outcome == AuditOutcome.Denied);
    }

    [Theory]
    [InlineData("guest")]
    [InlineData("root")]
    [InlineData("Not Valid")]
    public async Task A_reserved_or_malformed_name_changes_nothing(string login)
    {
        LocalUser guest = await StartGuestWithFilesAsync();

        await Assert.ThrowsAsync<ArgumentException>(
            () => _guests.MigrateAsync(new HackNewUserAccount(login, "Someone", AppAuthority.Administrator, NewPassword)).AsTask());

        Assert.True((await _harness.Users.FindByIdAsync(guest.Id))!.IsGuest);
        Assert.NotNull(await _harness.StatAsync("/home/guest"));
    }

    [Fact]
    public async Task A_short_password_is_refused()
    {
        await StartGuestWithFilesAsync();

        await Assert.ThrowsAsync<ArgumentException>(
            () => _guests.ReplaceAsync(new HackNewUserAccount("bob", "Bob", AppAuthority.Administrator, "short")).AsTask());
        Assert.NotNull(await _guests.FindLoneGuestAsync());
    }

    [Fact]
    public async Task Nothing_to_convert_without_a_guest()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _guests.MigrateAsync(new HackNewUserAccount("alice", "Alice", AppAuthority.Administrator, NewPassword)).AsTask());
    }

    /// <summary>Creates the guest, signs it in to write a document and a setting, then signs it out.</summary>
    private async Task<LocalUser> StartGuestWithFilesAsync()
    {
        LocalUser guest = await _guests.CreateAsync("Guest");
        await _harness.Session.LoginAsync(guest.LoginName, password: null);
        await _harness.WriteAsUserAsync("/home/guest/Documents/notes.txt", "my notes");
        await _harness.WriteAsUserAsync("/home/guest/.config/hackeros/desktop.conf", "wallpaper=/home/guest/Pictures/sky.png\nother=/home/guests/x\n");
        await _harness.Session.LogoutAsync();
        return guest;
    }
}
