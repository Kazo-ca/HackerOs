using HackerOs.App.Abstractions;
using HackerOs.AppSdk.Administration;
using HackerOs.Platform.Core.Sessions;
using HackerOs.Platform.Core.Tests.Settings;
using HackerOs.Simulation.Abstractions.Diagnostics;
using HackerOs.Simulation.Abstractions.Sessions;

namespace HackerOs.Platform.Core.Tests.Sessions;

/// <summary>
/// ADR 0169: an administrator adds an account (own group, <c>administrators</c> for an
/// administrator, a password) and deletes one, never their own nor the last administrator, with or
/// without its home folder; every attempt is in <c>auth.log</c>.
/// </summary>
public sealed class LocalAccountServiceTests
{
    private const string NewPassword = "correct horse 42";

    private readonly SettingsFilesHarness _harness = new();
    private readonly InMemoryLocalGroupRepository _groups = new();
    private readonly LocalAccountService _accounts;

    public LocalAccountServiceTests()
    {
        _accounts = new LocalAccountService(_harness.Users, _groups, _harness.Audit, _harness.FileSystem, iterations: 10);
    }

    [Fact]
    public async Task A_new_standard_account_has_its_own_group_and_signs_in_with_its_password()
    {
        LocalUser created = await _accounts.CreateAsync("admin", new HackNewUserAccount("carol", " Carol ", AppAuthority.User, NewPassword));

        Assert.Equal("Carol", created.DisplayName);
        Assert.Equal(AppAuthority.User, created.Authority);
        Assert.Equal("carol", (await _groups.FindByNameAsync(LocalLoginName.Parse("carol")))!.Name.Value);
        Assert.Empty(created.AdditionalGroupIds);
        Assert.True(LocalPasswordHasher.Verify(NewPassword, created.Credential!));

        AuthenticatedPrincipal carol = await _harness.Session.LoginAsync(created.LoginName, NewPassword);
        Assert.Equal("/home/carol", carol.HomePath);
        Assert.Contains(_harness.Audit.Entries, entry => entry.Action == "users.add" && entry.Outcome == AuditOutcome.Success);
    }

    [Fact]
    public async Task A_new_administrator_also_joins_the_administrators_group()
    {
        LocalUser created = await _accounts.CreateAsync("admin", new HackNewUserAccount("dave", "Dave", AppAuthority.Administrator, NewPassword));

        LocalGroup administrators = (await _groups.FindByNameAsync(LocalLoginName.Parse(LocalAccountService.AdministratorsGroupName)))!;
        Assert.Equal(AppAuthority.Administrator, created.Authority);
        Assert.Contains(administrators.Id, created.AdditionalGroupIds);
    }

    [Theory]
    [InlineData("root")]
    [InlineData("hackeros-svc")]
    [InlineData("administrators")]
    [InlineData("guest")]
    [InlineData("alice")]
    [InlineData("Not Valid")]
    public async Task A_reserved_taken_or_malformed_name_is_refused_and_logged(string login)
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => _accounts.CreateAsync("admin", new HackNewUserAccount(login, "Someone", AppAuthority.User, NewPassword)).AsTask());

        Assert.Contains(_harness.Audit.Entries, entry => entry.Action == "users.add" && entry.Outcome == AuditOutcome.Denied);
    }

    [Fact]
    public async Task Deleting_an_account_keeps_its_home_unless_asked()
    {
        await _harness.LoginAsync(_harness.Bob);
        await _harness.Session.LogoutAsync();
        AuthenticatedPrincipal alice = await _harness.LoginAsync(_harness.Alice);

        await _accounts.DeleteAsync(alice, "admin", "bob", removeHome: false);

        Assert.Null(await _harness.Users.FindByIdAsync(_harness.Bob.Id));
        Assert.NotNull(await _harness.StatAsync("/home/bob"));
        Assert.Contains(_harness.Audit.Entries, entry => entry.Action == "users.delete" && entry.Outcome == AuditOutcome.Success);
    }

    [Fact]
    public async Task Deleting_with_the_home_folder_removes_it()
    {
        await _harness.LoginAsync(_harness.Bob);
        await _harness.Session.LogoutAsync();
        AuthenticatedPrincipal alice = await _harness.LoginAsync(_harness.Alice);

        await _accounts.DeleteAsync(alice, "admin", "bob", removeHome: true);

        Assert.Null(await _harness.StatAsync("/home/bob"));
        Assert.NotNull(await _harness.StatAsync("/home/alice"));
    }

    [Fact]
    public async Task The_session_user_cannot_delete_their_own_account()
    {
        AuthenticatedPrincipal alice = await _harness.LoginAsync(_harness.Alice);

        await Assert.ThrowsAsync<InvalidOperationException>(() => _accounts.DeleteAsync(alice, "admin", "alice", removeHome: false).AsTask());

        Assert.NotNull(await _harness.Users.FindByIdAsync(_harness.Alice.Id));
    }

    [Fact]
    public async Task The_last_administrator_cannot_be_deleted()
    {
        LocalUser admin = await _accounts.CreateAsync("root", new HackNewUserAccount("admin", "Admin", AppAuthority.Administrator, NewPassword));
        AuthenticatedPrincipal alice = await _harness.LoginAsync(_harness.Alice);

        await Assert.ThrowsAsync<InvalidOperationException>(() => _accounts.DeleteAsync(alice, "root", "admin", removeHome: false).AsTask());

        Assert.NotNull(await _harness.Users.FindByIdAsync(admin.Id));
        Assert.Contains(_harness.Audit.Entries, entry => entry.Action == "users.delete" && entry.Outcome == AuditOutcome.Denied);
    }

    [Fact]
    public async Task An_unknown_account_cannot_be_deleted()
    {
        AuthenticatedPrincipal alice = await _harness.LoginAsync(_harness.Alice);

        await Assert.ThrowsAsync<ArgumentException>(() => _accounts.DeleteAsync(alice, "admin", "nobody", removeHome: false).AsTask());
    }
}
