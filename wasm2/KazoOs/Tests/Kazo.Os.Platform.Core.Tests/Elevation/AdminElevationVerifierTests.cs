using Kazo.Os.App.Abstractions;
using Kazo.Os.App.Abstractions.Policy;
using Kazo.Os.Platform.Core.Diagnostics;
using Kazo.Os.Platform.Core.Elevation;
using Kazo.Os.Platform.Core.Sessions;
using Kazo.Os.Simulation.Abstractions.Diagnostics;
using Kazo.Os.Simulation.Abstractions.Sessions;

namespace Kazo.Os.Platform.Core.Tests.Elevation;

/// <summary>Tests for `ADR 0042`'s admin re-authentication verifier, used by both `sudo` and the admin-approval prompt.</summary>
public sealed class AdminElevationVerifierTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task An_enabled_administrator_with_the_correct_password_is_verified()
    {
        (AdminElevationVerifier verifier, BoundedAuditLog audit, LocalUser admin) = await CreateFixtureAsync(
            AppAuthority.Administrator, enabled: true, password: "correct-horse");

        AdminElevationVerificationResult result = await verifier.VerifyAsync(admin.LoginName.Value, "correct-horse");

        Assert.True(result.Success);
        Assert.Equal(admin.Id.ToString(), result.UserId);
        Assert.Equal(admin.LoginName.Value, result.LoginName);
        Assert.Contains(audit.Entries, e => e.Action == "session.elevate" && e.Outcome == AuditOutcome.Success);
    }

    [Fact]
    public async Task A_disabled_administrator_is_rejected()
    {
        (AdminElevationVerifier verifier, BoundedAuditLog audit, LocalUser admin) = await CreateFixtureAsync(
            AppAuthority.Administrator, enabled: false, password: "correct-horse");

        AdminElevationVerificationResult result = await verifier.VerifyAsync(admin.LoginName.Value, "correct-horse");

        Assert.False(result.Success);
        Assert.Equal("user-disabled", result.FailureReason);
        Assert.Contains(audit.Entries, e => e.Action == "session.elevate" && e.Outcome == AuditOutcome.Denied);
    }

    [Fact]
    public async Task A_standard_user_is_rejected_even_with_the_correct_password()
    {
        (AdminElevationVerifier verifier, _, LocalUser user) = await CreateFixtureAsync(
            AppAuthority.User, enabled: true, password: "correct-horse");

        AdminElevationVerificationResult result = await verifier.VerifyAsync(user.LoginName.Value, "correct-horse");

        Assert.False(result.Success);
        Assert.Equal("not-administrator", result.FailureReason);
    }

    [Fact]
    public async Task An_incorrect_password_is_rejected()
    {
        (AdminElevationVerifier verifier, _, LocalUser admin) = await CreateFixtureAsync(
            AppAuthority.Administrator, enabled: true, password: "correct-horse");

        AdminElevationVerificationResult result = await verifier.VerifyAsync(admin.LoginName.Value, "wrong-password");

        Assert.False(result.Success);
        Assert.Equal("invalid-credentials", result.FailureReason);
    }

    [Fact]
    public async Task An_unknown_login_name_is_rejected()
    {
        (AdminElevationVerifier verifier, _, _) = await CreateFixtureAsync(
            AppAuthority.Administrator, enabled: true, password: "correct-horse");

        AdminElevationVerificationResult result = await verifier.VerifyAsync("nobody", "correct-horse");

        Assert.False(result.Success);
        Assert.Equal("user-not-found", result.FailureReason);
    }

    private static async Task<(AdminElevationVerifier Verifier, BoundedAuditLog Audit, LocalUser User)> CreateFixtureAsync(
        AppAuthority authority, bool enabled, string password)
    {
        InMemoryLocalUserRepository users = new(() => Now);
        InMemoryLocalGroupRepository groups = new();
        LocalGroup group = groups.CreateGroup(LocalLoginName.Parse("users"));
        LocalUser user = users.CreateUser(
            LocalLoginName.Parse("admin"), "Admin", authority, group.Id,
            credential: LocalPasswordHasher.Create(password, iterations: 100));
        if (!enabled)
        {
            // A second enabled Administrator keeps InMemoryLocalUserRepository's "last enabled
            // Administrator can never be disabled" guard from rejecting the test's own setup.
            users.CreateUser(
                LocalLoginName.Parse("keeper"), "Keeper", AppAuthority.Administrator, group.Id,
                credential: LocalPasswordHasher.Create("keeper-password", iterations: 100));
            user = await users.SetEnabledAsync(user.Id, false);
        }

        BoundedAuditLog audit = new(maxEntries: 100);
        AdminElevationVerifier verifier = new(users, audit, () => Now);
        return (verifier, audit, user);
    }
}
