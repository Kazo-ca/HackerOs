using HackerOs.App.Abstractions;
using HackerOs.App.Abstractions.Policy;
using HackerOs.Platform.Core.Diagnostics;
using HackerOs.Platform.Core.Elevation;
using HackerOs.Simulation.Abstractions.Diagnostics;

namespace HackerOs.Platform.Core.Tests.Elevation;

/// <summary>Contract tests of the OS authentication agent (ADR 0055, section 5).</summary>
public sealed class AdministratorAuthenticationAgentTests
{
    private static readonly AppManifest App = new()
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

    [Fact]
    public async Task A_process_running_as_root_is_not_asked_and_the_pass_is_logged()
    {
        ScriptedPrompt prompt = new();
        BoundedAuditLog audit = new(maxEntries: 20);
        AdministratorAuthenticationAgent agent = new(new FakeVerifier(), audit, prompt);

        AdministratorAuthenticationResult result = await agent.AuthenticateAsync(Request(isRootElevated: true));

        Assert.True(result.Succeeded);
        Assert.Equal("root", result.AdministratorLogin);
        Assert.Equal(0, prompt.Shown);
        AuditEntry entry = Assert.Single(audit.Entries);
        Assert.Equal("sudo", entry.Properties["via"]);
    }

    [Fact]
    public async Task Without_a_prompt_surface_the_operation_is_refused()
    {
        BoundedAuditLog audit = new(maxEntries: 20);
        AdministratorAuthenticationAgent agent = new(new FakeVerifier(), audit);

        AdministratorAuthenticationResult result = await agent.AuthenticateAsync(Request());

        Assert.False(result.Succeeded);
        Assert.Equal("unavailable", result.FailureReason);
        Assert.Equal(AuditOutcome.Denied, Assert.Single(audit.Entries).Outcome);
    }

    [Fact]
    public async Task A_wrong_password_then_the_right_one_succeeds_and_every_attempt_is_logged_without_the_password()
    {
        ScriptedPrompt prompt = new(("admin", "wrong"), ("admin", FakeVerifier.Password));
        BoundedAuditLog audit = new(maxEntries: 20);
        AdministratorAuthenticationAgent agent = new(new FakeVerifier(), audit, prompt);

        AdministratorAuthenticationResult result = await agent.AuthenticateAsync(Request());

        Assert.True(result.Succeeded);
        Assert.Equal("admin", result.AdministratorLogin);
        Assert.Equal(2, audit.Entries.Count);
        Assert.All(audit.Entries, entry =>
        {
            Assert.Equal(AdministratorAuthenticationAgent.AuditAction, entry.Action);
            Assert.Equal("apps.uninstall.machine", entry.Properties["operation"]);
            Assert.DoesNotContain(entry.Properties.Values, value => value.Contains(FakeVerifier.Password, StringComparison.Ordinal));
        });
        Assert.Equal("Uninstall Calc for all users", prompt.LastOperation);
    }

    [Fact]
    public async Task Three_failures_end_the_request_and_further_attempts_are_not_verified()
    {
        ScriptedPrompt prompt = new(("admin", "a"), ("admin", "b"), ("admin", "c"), ("admin", FakeVerifier.Password));
        FakeVerifier verifier = new();
        AdministratorAuthenticationAgent agent = new(verifier, new BoundedAuditLog(maxEntries: 20), prompt);

        AdministratorAuthenticationResult result = await agent.AuthenticateAsync(Request());

        Assert.False(result.Succeeded);
        Assert.Equal("too-many-attempts", result.FailureReason);
        Assert.Equal(3, verifier.Calls);
    }

    [Fact]
    public async Task A_prompt_closing_without_a_verified_attempt_cannot_forge_an_approval()
    {
        ForgingPrompt prompt = new();
        AdministratorAuthenticationAgent agent = new(new FakeVerifier(), new BoundedAuditLog(maxEntries: 20), prompt);

        AdministratorAuthenticationResult result = await agent.AuthenticateAsync(Request());

        Assert.False(result.Succeeded);
        Assert.Equal("cancelled", result.FailureReason);
    }

    [Fact]
    public async Task A_cancelled_passkey_prompt_costs_no_attempt_and_a_verified_passkey_approves()
    {
        int calls = 0;
        PasskeyPrompt prompt = new(times: 4);
        AdministratorAuthenticationAgent agent = new(new FakeVerifier(), new BoundedAuditLog(maxEntries: 20), prompt)
        {
            // The browser prompt is closed three times (NotAllowedError), then the passkey is verified.
            PasskeyVerifier = (_, _) => ++calls <= 3
                ? throw new OperationCanceledException()
                : Task.FromResult(new AdminElevationVerificationResult(true, "uid-admin", "admin", null))
        };

        AdministratorAuthenticationResult result = await agent.AuthenticateAsync(Request());

        Assert.True(result.Succeeded);
        Assert.Equal("admin", result.AdministratorLogin);
        Assert.All(prompt.Remaining.Take(3), remaining => Assert.Equal(3, remaining));
    }

    [Fact]
    public async Task Without_a_passkey_verifier_the_prompt_offers_no_passkey()
    {
        PasskeyPrompt prompt = new(times: 0);
        AdministratorAuthenticationAgent agent = new(new FakeVerifier(), new BoundedAuditLog(maxEntries: 20), prompt);

        await agent.AuthenticateAsync(Request());

        Assert.False(prompt.OfferedPasskey);
    }

    private static AdministratorAuthenticationRequest Request(bool isRootElevated = false) =>
        new(App, "Uninstall Calc for all users", "apps.uninstall.machine", null, isRootElevated);

    /// <summary>Accepts only <c>admin</c> with <see cref="Password"/>.</summary>
    private sealed class FakeVerifier : IAdminElevationVerifier
    {
        public const string Password = "s3cret";

        public int Calls { get; private set; }

        public Task<AdminElevationVerificationResult> VerifyAsync(string loginName, string password, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(loginName == "admin" && password == Password
                ? new AdminElevationVerificationResult(true, "uid-admin", "admin", null)
                : AdminElevationVerificationResult.Failed("invalid-credentials"));
        }
    }

    /// <summary>Submits a fixed list of credentials, like a user typing them, until one works or the agent says stop.</summary>
    private sealed class ScriptedPrompt(params (string Login, string Password)[] attempts) : IAdministratorAuthenticationPrompt
    {
        public int Shown { get; private set; }

        public string? LastOperation { get; private set; }

        public async Task<AdministratorAuthenticationOutcome> RequestAsync(
            AdministratorAuthenticationPromptRequest request, CancellationToken cancellationToken = default)
        {
            Shown++;
            LastOperation = request.Operation;
            foreach ((string login, string password) in attempts)
            {
                AdministratorAuthenticationAttempt attempt = await request.AuthenticateAsync(login, password, cancellationToken);
                if (attempt.Succeeded)
                {
                    return AdministratorAuthenticationOutcome.Approved;
                }

                if (attempt.RemainingAttempts <= 0)
                {
                    // A real overlay closes here; keep submitting to prove the agent refuses.
                    continue;
                }
            }

            return AdministratorAuthenticationOutcome.Cancelled;
        }
    }

    /// <summary>Confirms with a passkey up to <paramref name="times"/> times, recording the attempts left.</summary>
    private sealed class PasskeyPrompt(int times) : IAdministratorAuthenticationPrompt
    {
        public List<int> Remaining { get; } = [];

        public bool OfferedPasskey { get; private set; }

        public async Task<AdministratorAuthenticationOutcome> RequestAsync(
            AdministratorAuthenticationPromptRequest request, CancellationToken cancellationToken = default)
        {
            OfferedPasskey = request.AuthenticateWithPasskeyAsync is not null;
            for (int index = 0; index < times && request.AuthenticateWithPasskeyAsync is { } passkey; index++)
            {
                AdministratorAuthenticationAttempt attempt = await passkey("admin", cancellationToken);
                if (attempt.Succeeded)
                {
                    return AdministratorAuthenticationOutcome.Approved;
                }

                Remaining.Add(attempt.RemainingAttempts);
            }

            return AdministratorAuthenticationOutcome.Cancelled;
        }
    }

    /// <summary>A hostile surface that claims approval without submitting anything.</summary>
    private sealed class ForgingPrompt : IAdministratorAuthenticationPrompt
    {
        public Task<AdministratorAuthenticationOutcome> RequestAsync(
            AdministratorAuthenticationPromptRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(AdministratorAuthenticationOutcome.Approved);
    }
}
