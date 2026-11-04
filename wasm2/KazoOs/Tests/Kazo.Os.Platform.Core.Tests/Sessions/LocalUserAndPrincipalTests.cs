using Kazo.Os.App.Abstractions;
using Kazo.Os.Simulation.Abstractions.Sessions;

namespace Kazo.Os.Platform.Core.Tests.Sessions;

public sealed class LocalLoginNameTests
{
    [Theory]
    [InlineData("Alice", "alice")]
    [InlineData("BOB-2", "bob-2")]
    [InlineData("carol_", "carol_")]
    public void Parse_normalizes_to_lowercase(string input, string expected)
    {
        Assert.Equal(expected, LocalLoginName.Parse(input).Value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("1alice")]
    [InlineData("alice bob")]
    [InlineData("alice.bob")]
    public void Parse_rejects_invalid_names(string input)
    {
        Assert.Throws<FormatException>(() => LocalLoginName.Parse(input));
    }

    [Fact]
    public void Parse_rejects_names_over_the_maximum_length()
    {
        string tooLong = "a" + new string('b', LocalLoginName.MaximumLength);
        Assert.Throws<FormatException>(() => LocalLoginName.Parse(tooLong));
    }
}

public sealed class LocalUserTests
{
    private static LocalUser CreateUser(AppAuthority authority = AppAuthority.User) => new(
        LocalUserId.FromGuid(Guid.NewGuid()),
        LocalLoginName.Parse("alice"),
        "Alice",
        enabled: true,
        authority,
        LocalGroupId.FromGuid(Guid.NewGuid()),
        additionalGroupIds: [],
        credential: null,
        revision: 1,
        DateTimeOffset.UnixEpoch,
        DateTimeOffset.UnixEpoch);

    [Fact]
    public void HomePath_is_derived_from_the_login_name()
    {
        Assert.Equal("/home/alice", CreateUser().HomePath);
    }

    [Fact]
    public void System_authority_cannot_be_assigned_to_a_local_user()
    {
        Assert.Throws<ArgumentException>(() => CreateUser(AppAuthority.System));
    }

    [Fact]
    public void Administrator_authority_is_valid_for_a_local_user()
    {
        LocalUser user = CreateUser(AppAuthority.Administrator);
        Assert.Equal(AppAuthority.Administrator, user.Authority);
    }
}

public sealed class LocalPasswordCredentialTests
{
    [Fact]
    public void Construction_rejects_an_empty_salt()
    {
        Assert.Throws<ArgumentException>(() =>
            new LocalPasswordCredential("pbkdf2-sha256-v1", [], 100_000, [1, 2, 3]));
    }

    [Fact]
    public void Construction_rejects_non_positive_iterations()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new LocalPasswordCredential("pbkdf2-sha256-v1", [1], 0, [1, 2, 3]));
    }
}

public sealed class AuthenticatedPrincipalTests
{
    private static AuthenticatedPrincipal CreatePrincipal(AppAuthority authority = AppAuthority.User)
    {
        LocalGroupId primaryGroup = LocalGroupId.FromGuid(Guid.NewGuid());
        return new AuthenticatedPrincipal(
            SessionId.FromGuid(Guid.NewGuid()),
            LocalUserId.FromGuid(Guid.NewGuid()),
            LocalLoginName.Parse("alice"),
            "Alice",
            authority,
            primaryGroup,
            [primaryGroup],
            InstallationId.FromGuid(Guid.NewGuid()),
            DeviceId.FromGuid(Guid.NewGuid()),
            DateTimeOffset.UnixEpoch);
    }

    [Fact]
    public void System_authority_cannot_be_assigned_to_a_principal()
    {
        Assert.Throws<ArgumentException>(() => CreatePrincipal(AppAuthority.System));
    }

    [Fact]
    public void Group_memberships_must_include_the_primary_group()
    {
        LocalGroupId primaryGroup = LocalGroupId.FromGuid(Guid.NewGuid());
        LocalGroupId otherGroup = LocalGroupId.FromGuid(Guid.NewGuid());

        Assert.Throws<ArgumentException>(() => new AuthenticatedPrincipal(
            SessionId.FromGuid(Guid.NewGuid()),
            LocalUserId.FromGuid(Guid.NewGuid()),
            LocalLoginName.Parse("alice"),
            "Alice",
            AppAuthority.User,
            primaryGroup,
            [otherGroup],
            InstallationId.FromGuid(Guid.NewGuid()),
            DeviceId.FromGuid(Guid.NewGuid()),
            DateTimeOffset.UnixEpoch));
    }

    [Fact]
    public void HasAuthority_follows_the_ordered_authority_hierarchy()
    {
        AuthenticatedPrincipal principal = CreatePrincipal(AppAuthority.Administrator);

        Assert.True(principal.HasAuthority(AppAuthority.User));
        Assert.True(principal.HasAuthority(AppAuthority.Administrator));
        Assert.False(principal.HasAuthority(AppAuthority.System));
    }
}
