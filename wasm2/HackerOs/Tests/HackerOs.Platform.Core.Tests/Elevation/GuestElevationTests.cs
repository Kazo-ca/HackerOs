using HackerOs.App.Abstractions;
using HackerOs.App.Abstractions.Policy;
using HackerOs.Platform.Core.Diagnostics;
using HackerOs.Platform.Core.Elevation;
using HackerOs.Platform.Core.Sessions;
using HackerOs.Simulation.Abstractions.Diagnostics;
using HackerOs.Simulation.Abstractions.Sessions;

namespace HackerOs.Platform.Core.Tests.Elevation;

/// <summary>
/// ADR 0187 section 2: the lone guest confirms an elevation without a password; no other
/// passwordless account does, and the guest loses it as soon as another account exists.
/// </summary>
public sealed class GuestElevationTests
{
    private static readonly AppManifest Settings = new()
    {
        Id = "org.test.settings",
        Name = "Settings",
        Version = "1.0.0",
        PublisherId = "org.test",
        Description = "Settings",
        Kind = AppKind.Window,
        SdkCompatibility = new AppSdkCompatibilityManifest(HackerOsSdk.Version),
        Presentation = new PresentationManifest("test", AppLaunchVisibility.Visible, []),
        Resources = AppResourceProfileManifest.None
    };

    private readonly InMemoryLocalUserRepository _users = new();
    private readonly InMemoryLocalGroupRepository _groups = new();
    private readonly BoundedAuditLog _audit = new(maxEntries: 50);
    private readonly AdminElevationVerifier _verifier;

    public GuestElevationTests()
    {
        _verifier = new AdminElevationVerifier(_users, _audit);
    }

    [Fact]
    public async Task The_lone_guest_confirms_with_an_empty_password_and_it_is_logged()
    {
        LocalUser guest = await CreateGuestAsync();

        Assert.True(await _verifier.CanConfirmWithoutPasswordAsync("guest"));
        AdminElevationVerificationResult result = await _verifier.VerifyAsync("guest", string.Empty);

        Assert.True(result.Success);
        Assert.Equal(guest.Id.ToString(), result.UserId);
        Assert.Contains(_audit.Entries, entry => entry.Action == "session.elevate" && entry.Outcome == AuditOutcome.Success);
    }

    [Fact]
    public async Task The_guest_typing_something_is_refused()
    {
        await CreateGuestAsync();

        AdminElevationVerificationResult result = await _verifier.VerifyAsync("guest", "anything");

        Assert.False(result.Success);
        Assert.Equal("invalid-credentials", result.FailureReason);
    }

    [Fact]
    public async Task Another_passwordless_administrator_is_still_refused()
    {
        LocalGroup group = _groups.CreateGroup(LocalLoginName.Parse("eve"));
        _users.CreateUser(LocalLoginName.Parse("eve"), "Eve", AppAuthority.Administrator, group.Id);

        Assert.False(await _verifier.CanConfirmWithoutPasswordAsync("eve"));
        AdminElevationVerificationResult result = await _verifier.VerifyAsync("eve", string.Empty);

        Assert.False(result.Success);
        Assert.Equal("invalid-credentials", result.FailureReason);
    }

    [Fact]
    public async Task An_unmarked_account_named_guest_is_refused()
    {
        LocalGroup group = _groups.CreateGroup(LocalLoginName.Parse("guest"));
        _users.CreateUser(LocalLoginName.Parse("guest"), "Guest", AppAuthority.Administrator, group.Id);

        Assert.False(await _verifier.CanConfirmWithoutPasswordAsync("guest"));
        Assert.False((await _verifier.VerifyAsync("guest", string.Empty)).Success);
    }

    [Fact]
    public async Task The_guest_loses_the_confirmation_once_another_account_exists()
    {
        await CreateGuestAsync();
        LocalGroup group = _groups.CreateGroup(LocalLoginName.Parse("carol"));
        _users.CreateUser(
            LocalLoginName.Parse("carol"), "Carol", AppAuthority.Administrator, group.Id,
            credential: LocalPasswordHasher.Create("correct-horse", iterations: 10));

        Assert.False(await _verifier.CanConfirmWithoutPasswordAsync("guest"));
        Assert.False((await _verifier.VerifyAsync("guest", string.Empty)).Success);
        Assert.True((await _verifier.VerifyAsync("carol", "correct-horse")).Success);
    }

    [Fact]
    public async Task The_guest_confirms_through_the_authentication_agent()
    {
        await CreateGuestAsync();
        LocalUser guest = (await _users.FindByLoginNameAsync(LocalLoginName.Parse("guest")))!;
        ConfirmingPrompt prompt = new();
        AdministratorAuthenticationAgent agent = new(_verifier, _audit, prompt);
        AuthenticatedPrincipal principal = new(
            SessionId.FromGuid(Guid.NewGuid()), guest.Id, guest.LoginName, guest.DisplayName, guest.Authority,
            guest.PrimaryGroupId, [guest.PrimaryGroupId, .. guest.AdditionalGroupIds],
            InstallationId.FromGuid(Guid.NewGuid()), DeviceId.FromGuid(Guid.NewGuid()), DateTimeOffset.UnixEpoch);

        AdministratorAuthenticationResult result = await agent.AuthenticateAsync(new AdministratorAuthenticationRequest(
            Settings, "Add an account", "users.add", principal));

        Assert.True(result.Succeeded);
        Assert.Equal("guest", result.AdministratorLogin);
        Assert.Equal("guest", prompt.SuggestedLogin);
    }

    private ValueTask<LocalUser> CreateGuestAsync() =>
        new GuestAccountService(_users, _groups, _audit).CreateAsync("Guest");

    /// <summary>Stands in for the overlay: confirms as the suggested administrator, without a password.</summary>
    private sealed class ConfirmingPrompt : IAdministratorAuthenticationPrompt
    {
        public string? SuggestedLogin { get; private set; }

        public async Task<AdministratorAuthenticationOutcome> RequestAsync(
            AdministratorAuthenticationPromptRequest request, CancellationToken cancellationToken = default)
        {
            SuggestedLogin = request.SuggestedAdministratorLogin;
            AdministratorAuthenticationAttempt attempt = await request.AuthenticateAsync(SuggestedLogin!, string.Empty, cancellationToken);
            return attempt.Succeeded ? AdministratorAuthenticationOutcome.Approved : AdministratorAuthenticationOutcome.Cancelled;
        }
    }
}
