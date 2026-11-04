using Kazo.Os.App.Abstractions;
using Kazo.Os.App.Abstractions.Policy;
using Kazo.Os.AppSdk;
using Kazo.Os.AppSdk.Administration;
using Kazo.Os.Platform.Core.Administration;
using Kazo.Os.Platform.Core.Sessions;
using Kazo.Os.Platform.Core.Tests.Settings;
using Kazo.Os.Simulation.Abstractions.Gateways;
using Kazo.Os.Simulation.Abstractions.Sessions;

namespace Kazo.Os.Platform.Core.Tests.Sessions;

/// <summary>
/// ADR 0187 section 5: Settings ▸ Users asks the host for its "Create my account" form through
/// <see cref="KazoUserAccounts.RequestGuestAccountCreationAsync(CancellationToken)"/>; only the
/// guest's own session may ask, and the account list marks the guest.
/// </summary>
public sealed class GuestAccountRequestsTests
{
    private readonly SettingsFilesHarness _harness = new(withAccounts: false, withKeyring: true);
    private readonly GuestAccountService _guests;
    private readonly GuestAccountRequests _requests = new();
    private int _received;

    public GuestAccountRequestsTests()
    {
        _guests = new GuestAccountService(
            _harness.Users, _harness.Groups, _harness.Audit,
            fileSystem: _harness.FileSystem, session: _harness.Session, iterations: 10);
        _requests.CreateAccountRequested += () => _received++;
    }

    [Fact]
    public async Task The_guest_session_reaches_the_host_form()
    {
        LocalUser guest = await _guests.CreateAsync("Guest");
        AuthenticatedPrincipal principal = await _harness.Session.LoginAsync(guest.LoginName, password: null);
        AppUserAccountsGateway gateway = Gateway(principal);

        Assert.True(await gateway.RequestGuestAccountCreationAsync());
        Assert.Equal(1, _received);
        Assert.True(Assert.Single(await gateway.ListAsync()).IsGuest);
    }

    [Fact]
    public async Task Another_account_cannot_ask_for_the_guest_form()
    {
        LocalGroup group = await _harness.Groups.CreateGroupAsync(LocalLoginName.Parse("alice"));
        LocalUser alice = _harness.Users.CreateUser(
            LocalLoginName.Parse("alice"), "Alice", AppAuthority.Administrator, group.Id,
            credential: LocalPasswordHasher.Create(SettingsFilesHarness.Password, iterations: 10));
        AppUserAccountsGateway gateway = Gateway(await _harness.LoginAsync(alice));

        Assert.False(await gateway.RequestGuestAccountCreationAsync());
        Assert.Equal(0, _received);
        Assert.DoesNotContain(await gateway.ListAsync(), account => account.IsGuest);
    }

    [Fact]
    public async Task Without_a_host_listening_the_request_is_refused()
    {
        LocalUser guest = await _guests.CreateAsync("Guest");
        AuthenticatedPrincipal principal = await _harness.Session.LoginAsync(guest.LoginName, password: null);
        GuestAccountRequests unheard = new();

        Assert.False(unheard.HasListener);
        Assert.False(await Gateway(principal, () => unheard).RequestGuestAccountCreationAsync());
        Assert.False(await Gateway(principal, requests: null).RequestGuestAccountCreationAsync());
    }

    private AppUserAccountsGateway Gateway(AuthenticatedPrincipal principal) => Gateway(principal, () => _requests);

    private AppUserAccountsGateway Gateway(AuthenticatedPrincipal principal, Func<GuestAccountRequests>? requests)
    {
        AppManifest manifest = new()
        {
            Id = "org.hackeros.settings",
            Name = "Settings",
            Version = "1.0.0",
            PublisherId = "pub.hackeros",
            Description = "Guest fixture.",
            Kind = AppKind.Window,
            EntryPoint = new AppEntryPointManifest("Kazo.Os.Apps.Sample", "Kazo.Os.Apps.Sample.SampleApp"),
            SdkCompatibility = new AppSdkCompatibilityManifest(KazoOsSdk.Version),
            Presentation = new PresentationManifest("test", AppLaunchVisibility.Visible, []),
            Resources = AppResourceProfileManifest.None,
            Capabilities = []
        };
        LocalPasswordService passwords = new(_harness.Users, _harness.Audit, _harness.Keyring, iterations: 10);
        return new AppUserAccountsGateway(
            () => passwords,
            new NoCapabilities(),
            new AdministratorGate(agent: null, manifest, principal, isRootElevated: false))
        {
            GuestRequests = requests
        };
    }

    private sealed class NoCapabilities : ICapabilityChecker
    {
        public CapabilityPolicyEvaluation Evaluate(
            string capability, AppAuthority requiredAuthority = AppAuthority.User, CapabilityResourceCandidate? resourceCandidate = null) =>
            throw new NotSupportedException();

        public void Require(
            string capability, AppAuthority requiredAuthority = AppAuthority.User, CapabilityResourceCandidate? resourceCandidate = null) =>
            throw new AppGatewayAccessDeniedException(capability, CapabilityPolicyEvaluation.DenyMissing(1));
    }
}
