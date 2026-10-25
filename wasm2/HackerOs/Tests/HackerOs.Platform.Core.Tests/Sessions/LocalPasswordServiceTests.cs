using HackerOs.App.Abstractions;
using HackerOs.App.Abstractions.Policy;
using HackerOs.AppSdk;
using HackerOs.AppSdk.Administration;
using HackerOs.AppSdk.IO;
using HackerOs.Platform.Core.Administration;
using HackerOs.Platform.Core.Elevation;
using HackerOs.Platform.Core.Sessions;
using HackerOs.Platform.Core.Tests.Settings;
using HackerOs.Simulation.Abstractions.Diagnostics;
using HackerOs.Simulation.Abstractions.Gateways;
using HackerOs.Simulation.Abstractions.Sessions;

namespace HackerOs.Platform.Core.Tests.Sessions;

/// <summary>
/// ADR 0120: <c>passwd</c> changes one's own password after checking the current one and re-seals the
/// keyring; an administrator reset sets the keyring aside; the last administrator keeps a password;
/// the gateway asks <c>users.manage</c> and an administrator only for another account.
/// </summary>
public sealed class LocalPasswordServiceTests
{
    private const string AliceKeyring = "/home/alice/.local/share/keyrings/login.keyring";
    private const string NewPassword = "correct horse battery";

    [Fact]
    public async Task Changing_ones_own_password_lets_the_new_one_log_in_and_keeps_the_keyring_readable()
    {
        Harness harness = await Harness.CreateAsync();
        await harness.Files.Keyring!.SetAsync(harness.Files.Alice.Id, "org.example.mail", "imap", "kept", null);

        await harness.Service.ChangeOwnPasswordAsync(harness.Alice, SettingsFilesHarness.Password, NewPassword);

        await harness.Files.Session.LogoutAsync();
        await Assert.ThrowsAnyAsync<Exception>(() => harness.Files.Session.LoginAsync(harness.Files.Alice.LoginName, SettingsFilesHarness.Password));
        await harness.Files.Session.LoginAsync(harness.Files.Alice.LoginName, NewPassword);
        Assert.Equal("kept", await harness.Files.Keyring.GetAsync(harness.Files.Alice.Id, "org.example.mail", "imap", CancellationToken.None));
        Assert.Null(await harness.Files.StatAsync(AliceKeyring + ".old"));
        AuditEntry entry = Assert.Single(harness.Files.Audit.Entries, audit => audit.Action == "passwd.change");
        Assert.Equal(AuditOutcome.Success, entry.Outcome);
    }

    [Fact]
    public async Task A_wrong_current_password_is_refused_and_audited()
    {
        Harness harness = await Harness.CreateAsync();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => harness.Service.ChangeOwnPasswordAsync(harness.Alice, "not it", NewPassword).AsTask());

        LocalUser alice = (await harness.Files.Users.FindByIdAsync(harness.Files.Alice.Id))!;
        Assert.True(await LocalPasswordHasher.VerifyAsync(SettingsFilesHarness.Password, alice.Credential!, null, CancellationToken.None));
        AuditEntry entry = Assert.Single(harness.Files.Audit.Entries, audit => audit.Action == "passwd.change");
        Assert.Equal(AuditOutcome.Denied, entry.Outcome);
    }

    [Theory]
    [InlineData("short")]
    [InlineData("")]
    [InlineData("tab\there!")]
    [InlineData(SettingsFilesHarness.Password + "-is-not-changed")]
    public async Task A_new_password_breaking_the_rules_is_refused(string candidate)
    {
        Harness harness = await Harness.CreateAsync();
        // The last case: a valid password, then the same one again, is "Password unchanged".
        string current = SettingsFilesHarness.Password;
        if (candidate.EndsWith("-is-not-changed", StringComparison.Ordinal))
        {
            await harness.Service.ChangeOwnPasswordAsync(harness.Alice, current, candidate);
            current = candidate;
        }

        await Assert.ThrowsAsync<ArgumentException>(
            () => harness.Service.ChangeOwnPasswordAsync(harness.Alice, current, candidate).AsTask());
    }

    [Fact]
    public async Task An_administrator_reset_sets_the_other_accounts_keyring_aside()
    {
        Harness harness = await Harness.CreateAsync();
        await harness.Files.Session.LogoutAsync();
        AuthenticatedPrincipal bob = await harness.Files.LoginAsync(harness.Files.Bob);

        await harness.Service.ResetPasswordAsync(bob, "admin", "alice", NewPassword);

        Assert.Null(await harness.Files.StatAsync(AliceKeyring));
        Assert.NotNull(await harness.Files.StatAsync(AliceKeyring + ".old"));
        await harness.Files.Session.LogoutAsync();
        await harness.Files.Session.LoginAsync(harness.Files.Alice.LoginName, NewPassword);
        AuditEntry entry = Assert.Single(harness.Files.Audit.Entries, audit => audit.Action == "passwd.reset");
        Assert.Equal("admin", entry.Actor);
        Assert.Equal("alice", entry.Subject);
    }

    [Fact]
    public async Task Removing_a_password_leaves_the_account_without_one_but_never_the_last_administrator()
    {
        Harness harness = await Harness.CreateAsync();

        await harness.Service.ResetPasswordAsync(harness.Alice, "admin", "bob", null);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.Service.ResetPasswordAsync(harness.Alice, "admin", "admin", null).AsTask());

        IReadOnlyList<LocalUser> users = await harness.Service.ListAsync();
        Assert.Null(users.Single(user => user.LoginName.Value == "bob").Credential);
        Assert.NotNull(users.Single(user => user.LoginName.Value == "admin").Credential);
        Assert.Contains(harness.Files.Audit.Entries, audit => audit.Action == "passwd.delete" && audit.Outcome == AuditOutcome.Denied);
        await Assert.ThrowsAsync<ArgumentException>(
            () => harness.Service.ResetPasswordAsync(harness.Alice, "admin", "nobody", NewPassword).AsTask());
    }

    [Fact]
    public async Task The_gateway_lists_accounts_without_hashes_and_marks_the_caller()
    {
        Harness harness = await Harness.CreateAsync();

        IReadOnlyList<HackUserAccount> accounts = await harness.Gateway(new CountingAgent(true)).ListAsync();

        Assert.Equal(["admin", "alice", "bob"], accounts.Select(account => account.LoginName));
        HackUserAccount alice = accounts.Single(account => account.IsCurrentUser);
        Assert.Equal("alice", alice.LoginName);
        Assert.True(alice.HasPassword);
        Assert.True(accounts.Single(account => account.LoginName == "admin").IsAdministrator);
    }

    [Fact]
    public async Task The_gateway_changes_ones_own_password_without_capability_or_administrator()
    {
        Harness harness = await Harness.CreateAsync();
        CountingAgent agent = new(true);

        await harness.Gateway(agent).ChangePasswordAsync(SettingsFilesHarness.Password, NewPassword);

        Assert.Equal(0, agent.Calls);
    }

    [Fact]
    public async Task A_reset_through_the_gateway_needs_users_manage_and_an_administrator()
    {
        Harness harness = await Harness.CreateAsync();
        CountingAgent refusing = new(false);
        CountingAgent agreeing = new(true);

        await Assert.ThrowsAsync<AppGatewayAccessDeniedException>(
            () => harness.Gateway(agreeing).ResetPasswordAsync("bob", NewPassword).AsTask());
        await Assert.ThrowsAsync<HackUnauthorizedAccessException>(
            () => harness.Gateway(refusing, AppCapabilities.UsersManage).ResetPasswordAsync("bob", NewPassword).AsTask());
        Assert.Equal("users.password-reset", refusing.LastRequest!.Action);
        Assert.Contains("bob", refusing.LastRequest.Operation, StringComparison.Ordinal);

        await harness.Gateway(agreeing, AppCapabilities.UsersManage).ResetPasswordAsync("bob", null);

        Assert.Equal("users.password-delete", agreeing.LastRequest!.Action);
        Assert.Null((await harness.Files.Users.FindByIdAsync(harness.Files.Bob.Id))!.Credential);
    }

    [Fact]
    public async Task The_in_memory_store_replaces_the_credential_and_bumps_the_revision()
    {
        SettingsFilesHarness files = new();
        LocalPasswordCredential credential = LocalPasswordHasher.Create(NewPassword, iterations: 10);

        LocalUser updated = await files.Users.SetCredentialAsync(files.Alice.Id, credential);

        Assert.Equal(credential, updated.Credential);
        Assert.True(updated.Revision > files.Alice.Revision);
        Assert.Null((await files.Users.SetCredentialAsync(files.Alice.Id, null)).Credential);
    }

    [Fact]
    public async Task Adding_and_deleting_through_the_gateway_need_users_manage_and_an_administrator()
    {
        Harness harness = await Harness.CreateAsync();
        CountingAgent refusing = new(false);
        CountingAgent agreeing = new(true);
        HackNewUserAccount carol = new("carol", "Carol", AppAuthority.User, NewPassword);

        await Assert.ThrowsAsync<AppGatewayAccessDeniedException>(() => harness.Gateway(agreeing).CreateAsync(carol).AsTask());
        await Assert.ThrowsAsync<HackUnauthorizedAccessException>(
            () => harness.Gateway(refusing, AppCapabilities.UsersManage).CreateAsync(carol).AsTask());
        Assert.Equal("users.add", refusing.LastRequest!.Action);

        HackUserAccount created = await harness.Gateway(agreeing, AppCapabilities.UsersManage).CreateAsync(carol);
        Assert.Equal("carol", created.LoginName);
        Assert.False(created.IsAdministrator);

        await Assert.ThrowsAsync<AppGatewayAccessDeniedException>(() => harness.Gateway(agreeing).DeleteAsync("carol", removeHome: false).AsTask());
        await harness.Gateway(agreeing, AppCapabilities.UsersManage).DeleteAsync("carol", removeHome: true);

        Assert.Equal("users.delete", agreeing.LastRequest!.Action);
        Assert.DoesNotContain(await harness.Gateway(agreeing).ListAsync(), account => account.LoginName == "carol");
    }

    [Fact]
    public async Task The_session_user_manages_their_passkeys_without_capability_or_administrator()
    {
        Harness harness = await Harness.CreateAsync();
        CountingAgent agent = new(true);
        AppUserAccountsGateway gateway = harness.Gateway(agent);

        HackPasskey added = await gateway.AddPasskeyAsync("Phone");

        Assert.True(added.UnlocksKeyring);
        Assert.Equal(added.Id, Assert.Single(await gateway.ListPasskeysAsync()).Id);
        Assert.Equal(1, (await gateway.ListAsync()).Single(account => account.IsCurrentUser).PasskeyCount);
        Assert.True(await gateway.RemovePasskeyAsync(added.Id));
        Assert.Empty(await gateway.ListPasskeysAsync());
        Assert.Equal(0, agent.Calls);
    }

    /// <summary>The files harness with its keyring, an administrator account and alice logged in.</summary>
    private sealed class Harness
    {
        private Harness(SettingsFilesHarness files, AuthenticatedPrincipal alice)
        {
            Files = files;
            Alice = alice;
            Service = new LocalPasswordService(files.Users, files.Audit, files.Keyring, iterations: 10);
            Accounts = new LocalAccountService(files.Users, new InMemoryLocalGroupRepository(), files.Audit, files.FileSystem, iterations: 10);
            Passkeys = new PasskeyService(files.Users, new FakePasskeyAuthenticator { PrfOnCreate = true }, files.Audit, files.Keyring);
        }

        internal SettingsFilesHarness Files { get; }

        internal AuthenticatedPrincipal Alice { get; }

        internal LocalPasswordService Service { get; }

        internal LocalAccountService Accounts { get; }

        internal PasskeyService Passkeys { get; }

        internal static async Task<Harness> CreateAsync()
        {
            SettingsFilesHarness files = new(withKeyring: true);
            files.Users.CreateUser(
                LocalLoginName.Parse("admin"), "Admin", AppAuthority.Administrator, files.Alice.PrimaryGroupId,
                credential: LocalPasswordHasher.Create(SettingsFilesHarness.Password, iterations: 100));
            AuthenticatedPrincipal alice = await files.LoginAsync(files.Alice);
            return new Harness(files, alice);
        }

        internal AppUserAccountsGateway Gateway(IAdministratorAuthenticationAgent agent, params string[] capabilities)
        {
            AppManifest manifest = new()
            {
                Id = "org.hackeros.settings",
                Name = "Settings",
                Version = "1.0.0",
                PublisherId = "pub.hackeros",
                Description = "Password fixture.",
                Kind = AppKind.Window,
                EntryPoint = new AppEntryPointManifest("HackerOs.Apps.Sample", "HackerOs.Apps.Sample.SampleApp"),
                SdkCompatibility = new AppSdkCompatibilityManifest(HackerOsSdk.Version),
                Presentation = new PresentationManifest("test", AppLaunchVisibility.Visible, []),
                Resources = AppResourceProfileManifest.None,
                Capabilities = capabilities
            };
            return new AppUserAccountsGateway(
                () => Service,
                new FixedCapabilities(capabilities),
                new AdministratorGate(() => agent, manifest, Alice, isRootElevated: false))
            {
                Accounts = () => Accounts,
                Passkeys = () => Passkeys
            };
        }
    }

    private sealed class CountingAgent(bool succeed) : IAdministratorAuthenticationAgent
    {
        public int Calls { get; private set; }

        public AdministratorAuthenticationRequest? LastRequest { get; private set; }

        public Task<AdministratorAuthenticationResult> AuthenticateAsync(
            AdministratorAuthenticationRequest request, CancellationToken cancellationToken = default)
        {
            Calls++;
            LastRequest = request;
            return Task.FromResult(succeed
                ? new AdministratorAuthenticationResult(true, "admin", null)
                : AdministratorAuthenticationResult.Failed("cancelled"));
        }
    }

    private sealed class FixedCapabilities(params string[] granted) : ICapabilityChecker
    {
        public CapabilityPolicyEvaluation Evaluate(
            string capability, AppAuthority requiredAuthority = AppAuthority.User, CapabilityResourceCandidate? resourceCandidate = null) =>
            throw new NotSupportedException();

        public void Require(
            string capability, AppAuthority requiredAuthority = AppAuthority.User, CapabilityResourceCandidate? resourceCandidate = null)
        {
            if (!granted.Contains(capability, StringComparer.Ordinal))
            {
                throw new AppGatewayAccessDeniedException(capability, CapabilityPolicyEvaluation.DenyMissing(1));
            }
        }
    }
}
