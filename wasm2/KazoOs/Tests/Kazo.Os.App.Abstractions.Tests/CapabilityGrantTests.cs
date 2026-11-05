using Kazo.Os.App.Abstractions.Policy;

namespace Kazo.Os.App.Abstractions.Tests;

public sealed class CapabilityGrantTests
{
    [Fact]
    public void Default_evaluation_denies_as_missing()
    {
        CapabilityPolicyEvaluation evaluation = default;

        Assert.False(evaluation.Granted);
        Assert.Equal(CapabilityPolicyEvaluationReason.Missing, evaluation.Reason);
        Assert.Equal(0, evaluation.PolicyRevision);
        Assert.Null(evaluation.GrantId);
    }

    [Fact]
    public void Evaluation_reports_granted_and_each_explicit_denial_reason()
    {
        CapabilityGrant grant = CreateGrant();

        CapabilityPolicyEvaluation permitted = CapabilityPolicyEvaluation.Permit(grant);
        CapabilityPolicyEvaluation missing = CapabilityPolicyEvaluation.DenyMissing(8);
        CapabilityPolicyEvaluation revoked = CapabilityPolicyEvaluation.Deny(
            grant,
            CapabilityPolicyEvaluationReason.Revoked);
        CapabilityPolicyEvaluation constrained = CapabilityPolicyEvaluation.Deny(
            grant,
            CapabilityPolicyEvaluationReason.Constrained);
        CapabilityPolicyEvaluation authorityDenied = CapabilityPolicyEvaluation.Deny(
            grant,
            CapabilityPolicyEvaluationReason.AuthorityDenied);

        Assert.True(permitted.Granted);
        Assert.Equal(CapabilityPolicyEvaluationReason.Granted, permitted.Reason);
        Assert.Equal(grant.Id, permitted.GrantId);
        Assert.Equal(8, missing.PolicyRevision);
        Assert.Null(missing.GrantId);
        Assert.Equal(CapabilityPolicyEvaluationReason.Revoked, revoked.Reason);
        Assert.Equal(CapabilityPolicyEvaluationReason.Constrained, constrained.Reason);
        Assert.Equal(CapabilityPolicyEvaluationReason.AuthorityDenied, authorityDenied.Reason);
        Assert.All(
            [revoked, constrained, authorityDenied],
            evaluation =>
            {
                Assert.False(evaluation.Granted);
                Assert.Equal(grant.Id, evaluation.GrantId);
                Assert.Equal(grant.PolicyRevision, evaluation.PolicyRevision);
            });
    }

    [Fact]
    public void Grant_denial_rejects_non_denial_reasons()
    {
        CapabilityGrant grant = CreateGrant();

        Assert.Throws<ArgumentOutOfRangeException>(() => CapabilityPolicyEvaluation.Deny(
            grant,
            CapabilityPolicyEvaluationReason.Granted));
        Assert.Throws<ArgumentOutOfRangeException>(() => CapabilityPolicyEvaluation.Deny(
            grant,
            CapabilityPolicyEvaluationReason.Missing));
    }

    [Fact]
    public void Grant_copies_structured_constraints_and_preserves_exact_identity()
    {
        CapabilityConstraint[] constraints =
        [
            new VirtualPathCapabilityConstraint(VirtualPath.Parse("/home/user"), true),
            new NetworkHostCapabilityConstraint("EXAMPLE.COM."),
            new NetworkPortCapabilityConstraint(443, 443)
        ];
        CapabilityGrant grant = new(
            CapabilityGrantId.FromGuid(Guid.Parse("d9428888-122b-11e1-b85c-61cd3cbb3210")),
            "org.kazoos.browser",
            "user-1",
            AppCapabilities.FileSystemUserHomeRead,
            7,
            CapabilityGrantSource.UserApproval,
            constraints);
        constraints[0] = new VirtualPathCapabilityConstraint(VirtualPath.Parse("/"), true);

        Assert.Equal("org.kazoos.browser", grant.AppId);
        Assert.Equal("user-1", grant.UserId);
        Assert.Equal(7, grant.PolicyRevision);
        Assert.Equal("/home/user", Assert.IsType<VirtualPathCapabilityConstraint>(grant.Constraints[0]).Path.Value);
        Assert.Equal("example.com", Assert.IsType<NetworkHostCapabilityConstraint>(grant.Constraints[1]).Host);
    }

    [Fact]
    public void Path_constraint_uses_segment_boundary_and_optional_descendants()
    {
        VirtualPathCapabilityConstraint subtree = new(VirtualPath.Parse("/home/user"), true);
        VirtualPathCapabilityConstraint exact = new(VirtualPath.Parse("/home/user"), false);

        Assert.True(subtree.Allows(VirtualPath.Parse("/home/user/Documents/file.txt")));
        Assert.False(subtree.Allows(VirtualPath.Parse("/home/username/file.txt")));
        Assert.False(exact.Allows(VirtualPath.Parse("/home/user/file.txt")));
    }

    [Fact]
    public void Grant_rejects_unknown_or_wildcard_capability()
    {
        Assert.Throws<ArgumentException>(() => new CapabilityGrant(
            CapabilityGrantId.FromGuid(Guid.NewGuid()),
            "org.kazoos.test",
            "user",
            "filesystem.*",
            1,
            CapabilityGrantSource.UserApproval));
    }

    [Fact]
    public void Grant_rejects_duplicate_constraint_kinds()
    {
        Assert.Throws<ArgumentException>(() => new CapabilityGrant(
            CapabilityGrantId.FromGuid(Guid.NewGuid()),
            "org.kazoos.test",
            "user",
            AppCapabilities.FileSystemUserHomeRead,
            1,
            CapabilityGrantSource.UserApproval,
            [
                new VirtualPathCapabilityConstraint(VirtualPath.Parse("/home/user"), true),
                new VirtualPathCapabilityConstraint(VirtualPath.Parse("/tmp"), true)
            ]));
    }

    [Fact]
    public void Host_and_port_constraints_reject_wildcards_and_invalid_ranges()
    {
        Assert.Throws<ArgumentException>(() => new NetworkHostCapabilityConstraint("*.example.com"));
        Assert.Throws<ArgumentOutOfRangeException>(() => new NetworkPortCapabilityConstraint(443, 80));
        Assert.Throws<ArgumentOutOfRangeException>(() => new NetworkPortCapabilityConstraint(0, 80));
    }

    private static CapabilityGrant CreateGrant() => new(
        CapabilityGrantId.FromGuid(Guid.Parse("d9428888-122b-11e1-b85c-61cd3cbb3210")),
        "org.kazoos.browser",
        "user-1",
        AppCapabilities.FileSystemUserHomeRead,
        7,
        CapabilityGrantSource.UserApproval);
}