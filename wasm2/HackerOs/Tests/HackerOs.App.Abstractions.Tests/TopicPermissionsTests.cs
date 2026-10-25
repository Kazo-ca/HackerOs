using HackerOs.App.Abstractions;
using HackerOs.App.Abstractions.Policy;

namespace HackerOs.App.Abstractions.Tests;

/// <summary>
/// Tests for <see cref="TopicPermissions"/> and its integration with <see cref="CapabilityGrant"/>, per
/// doc/en/wasm/dev/adr/0040-declared-topic-permissions.md.
/// </summary>
public sealed class TopicPermissionsTests
{
    [Theory]
    [InlineData("topic-publish:app/org.hackeros.file-explorer/change-directory")]
    [InlineData("topic-subscribe:app/org.hackeros.file-explorer/change-directory")]
    [InlineData("topic-publish:app/org.hackeros.file-explorer/change-directory/nested")]
    public void IsWellFormed_accepts_valid_identifiers(string capability)
    {
        Assert.True(TopicPermissions.IsWellFormed(capability));
    }

    [Theory]
    [InlineData("")]
    [InlineData("filesystem.private.read")]
    [InlineData("topic-publish:shared/filesystem/changed")]
    [InlineData("topic-publish:app/org.hackeros.file-explorer")]
    [InlineData("topic-publish:app/NotLowercase/change-directory")]
    [InlineData("topic-publish:app/org.hackeros.file-explorer/Change-Directory")]
    public void IsWellFormed_rejects_invalid_shapes(string capability)
    {
        Assert.False(TopicPermissions.IsWellFormed(capability));
    }

    [Fact]
    public void IsOwnedByApp_matches_only_the_declaring_apps_own_namespace()
    {
        const string capability = "topic-publish:app/org.hackeros.file-explorer/change-directory";

        Assert.True(TopicPermissions.IsOwnedByApp(capability, "org.hackeros.file-explorer"));
        Assert.False(TopicPermissions.IsOwnedByApp(capability, "org.hackeros.other-app"));
    }

    [Fact]
    public void CapabilityGrant_accepts_a_well_formed_topic_permission()
    {
        CapabilityGrant grant = new(
            CapabilityGrantId.FromGuid(Guid.NewGuid()),
            appId: "org.hackeros.other-app",
            userId: "user-1",
            capability: "topic-publish:app/org.hackeros.file-explorer/change-directory",
            policyRevision: 1,
            source: CapabilityGrantSource.UserApproval);

        Assert.Equal("topic-publish:app/org.hackeros.file-explorer/change-directory", grant.Capability);
    }

    [Fact]
    public void CapabilityGrant_rejects_a_malformed_capability()
    {
        Assert.Throws<ArgumentException>(() => new CapabilityGrant(
            CapabilityGrantId.FromGuid(Guid.NewGuid()),
            appId: "org.hackeros.other-app",
            userId: "user-1",
            capability: "not-a-real-capability",
            policyRevision: 1,
            source: CapabilityGrantSource.UserApproval));
    }
}
