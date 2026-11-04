using Kazo.Os.App.Abstractions;
using Kazo.Os.App.Abstractions.Policy;
using Kazo.Os.Platform.Core.Policy;

namespace Kazo.Os.Platform.Core.Tests.Policy;

public sealed class CapabilityGrantRepositoryTests
{
    [Fact]
    public void Grant_and_revoke_require_administrator_or_system_authority()
    {
        CapabilityGrantRepository repository = new();

        CapabilityGrantMutationResult deniedGrant = repository.Grant(
            "org.hackeros.browser",
            "user-1",
            AppCapabilities.FileSystemUserHomeRead,
            CapabilityGrantSource.UserApproval,
            AppAuthority.User);

        Assert.Equal(CapabilityGrantMutationStatus.AuthorityDenied, deniedGrant.Status);
        Assert.Equal(0, repository.CurrentPolicyRevision);

        CapabilityGrantMutationResult granted = repository.Grant(
            "org.hackeros.browser",
            "user-1",
            AppCapabilities.FileSystemUserHomeRead,
            CapabilityGrantSource.AdministratorApproval,
            AppAuthority.Administrator);

        Assert.Equal(CapabilityGrantMutationStatus.Granted, granted.Status);

        CapabilityGrantMutationResult deniedRevoke = repository.Revoke(granted.Grant!.Id, AppAuthority.User);
        Assert.Equal(CapabilityGrantMutationStatus.AuthorityDenied, deniedRevoke.Status);
    }

    [Fact]
    public void Evaluate_denies_missing_by_default_and_permits_exact_match()
    {
        CapabilityGrantRepository repository = new();
        repository.Grant(
            "org.hackeros.browser",
            "user-1",
            AppCapabilities.FileSystemUserHomeRead,
            CapabilityGrantSource.AdministratorApproval,
            AppAuthority.Administrator);

        CapabilityPolicyEvaluation missingApp = repository.Evaluate(
            "org.hackeros.other",
            "user-1",
            AppCapabilities.FileSystemUserHomeRead,
            AppAuthority.User,
            AppAuthority.User);
        CapabilityPolicyEvaluation permitted = repository.Evaluate(
            "org.hackeros.browser",
            "user-1",
            AppCapabilities.FileSystemUserHomeRead,
            AppAuthority.User,
            AppAuthority.User);

        Assert.Equal(CapabilityPolicyEvaluationReason.Missing, missingApp.Reason);
        Assert.True(permitted.Granted);
        Assert.Equal(CapabilityPolicyEvaluationReason.Granted, permitted.Reason);
    }

    [Fact]
    public void Evaluate_reports_revoked_reason_after_revocation()
    {
        CapabilityGrantRepository repository = new();
        CapabilityGrantMutationResult granted = repository.Grant(
            "org.hackeros.browser",
            "user-1",
            AppCapabilities.FileSystemUserHomeRead,
            CapabilityGrantSource.AdministratorApproval,
            AppAuthority.Administrator);
        repository.Revoke(granted.Grant!.Id, AppAuthority.Administrator);

        CapabilityPolicyEvaluation evaluation = repository.Evaluate(
            "org.hackeros.browser",
            "user-1",
            AppCapabilities.FileSystemUserHomeRead,
            AppAuthority.User,
            AppAuthority.User);

        Assert.False(evaluation.Granted);
        Assert.Equal(CapabilityPolicyEvaluationReason.Revoked, evaluation.Reason);
    }

    [Fact]
    public void Evaluate_reports_constrained_when_resource_falls_outside_grant()
    {
        CapabilityGrantRepository repository = new();
        repository.Grant(
            "org.hackeros.browser",
            "user-1",
            AppCapabilities.FileSystemUserHomeRead,
            CapabilityGrantSource.AdministratorApproval,
            AppAuthority.Administrator,
            [new VirtualPathCapabilityConstraint(VirtualPath.Parse("/home/user-1/Downloads"), true)]);

        CapabilityPolicyEvaluation outsideConstraint = repository.Evaluate(
            "org.hackeros.browser",
            "user-1",
            AppCapabilities.FileSystemUserHomeRead,
            AppAuthority.User,
            AppAuthority.User,
            new VirtualPathResourceCandidate(VirtualPath.Parse("/home/user-1/Documents/file.txt")));
        CapabilityPolicyEvaluation insideConstraint = repository.Evaluate(
            "org.hackeros.browser",
            "user-1",
            AppCapabilities.FileSystemUserHomeRead,
            AppAuthority.User,
            AppAuthority.User,
            new VirtualPathResourceCandidate(VirtualPath.Parse("/home/user-1/Downloads/file.txt")));

        Assert.Equal(CapabilityPolicyEvaluationReason.Constrained, outsideConstraint.Reason);
        Assert.True(insideConstraint.Granted);
    }

    [Fact]
    public void Evaluate_denies_authority_when_grant_matches_but_required_authority_is_not_met()
    {
        CapabilityGrantRepository repository = new();
        repository.Grant(
            "org.hackeros.settings",
            "user-1",
            AppCapabilities.FileSystemSystemWrite,
            CapabilityGrantSource.AdministratorApproval,
            AppAuthority.Administrator);

        CapabilityPolicyEvaluation evaluation = repository.Evaluate(
            "org.hackeros.settings",
            "user-1",
            AppCapabilities.FileSystemSystemWrite,
            AppAuthority.User,
            AppAuthority.Administrator);

        Assert.False(evaluation.Granted);
        Assert.Equal(CapabilityPolicyEvaluationReason.AuthorityDenied, evaluation.Reason);
    }

    [Fact]
    public void Re_granting_with_broader_constraints_is_recorded_as_expansion_and_audited()
    {
        CapabilityGrantRepository repository = new();
        repository.Grant(
            "org.hackeros.browser",
            "user-1",
            AppCapabilities.FileSystemUserHomeRead,
            CapabilityGrantSource.AdministratorApproval,
            AppAuthority.Administrator,
            [new VirtualPathCapabilityConstraint(VirtualPath.Parse("/home/user-1/Downloads"), false)]);

        CapabilityGrantMutationResult expanded = repository.Grant(
            "org.hackeros.browser",
            "user-1",
            AppCapabilities.FileSystemUserHomeRead,
            CapabilityGrantSource.AdministratorApproval,
            AppAuthority.Administrator,
            [new VirtualPathCapabilityConstraint(VirtualPath.Parse("/home/user-1"), true)]);

        Assert.Equal(CapabilityGrantMutationStatus.Expanded, expanded.Status);
        Assert.Contains(
            repository.AuditLog,
            entry => entry.Action == CapabilityGrantAuditAction.Expanded && entry.Grant.Id == expanded.Grant!.Id);
    }

    [Fact]
    public void Narrower_or_unrelated_regrant_is_not_recorded_as_expansion()
    {
        CapabilityGrantRepository repository = new();
        repository.Grant(
            "org.hackeros.browser",
            "user-1",
            AppCapabilities.FileSystemUserHomeRead,
            CapabilityGrantSource.AdministratorApproval,
            AppAuthority.Administrator,
            [new VirtualPathCapabilityConstraint(VirtualPath.Parse("/home/user-1"), true)]);

        CapabilityGrantMutationResult narrower = repository.Grant(
            "org.hackeros.browser",
            "user-1",
            AppCapabilities.FileSystemUserHomeRead,
            CapabilityGrantSource.AdministratorApproval,
            AppAuthority.Administrator,
            [new VirtualPathCapabilityConstraint(VirtualPath.Parse("/home/user-1/Downloads"), false)]);

        Assert.Equal(CapabilityGrantMutationStatus.Granted, narrower.Status);
    }

    [Fact]
    public void Audit_log_records_grant_and_revoke_with_policy_revision()
    {
        CapabilityGrantRepository repository = new();
        CapabilityGrantMutationResult granted = repository.Grant(
            "org.hackeros.browser",
            "user-1",
            AppCapabilities.FileSystemUserHomeRead,
            CapabilityGrantSource.AdministratorApproval,
            AppAuthority.Administrator);
        repository.Revoke(granted.Grant!.Id, AppAuthority.Administrator);

        Assert.Equal(2, repository.AuditLog.Count);
        Assert.Equal(CapabilityGrantAuditAction.Granted, repository.AuditLog[0].Action);
        Assert.Equal(1, repository.AuditLog[0].PolicyRevision);
        Assert.Equal(CapabilityGrantAuditAction.Revoked, repository.AuditLog[1].Action);
        Assert.Equal(2, repository.AuditLog[1].PolicyRevision);
        Assert.Equal(2, repository.CurrentPolicyRevision);
    }

    [Fact]
    public void Revoking_unknown_grant_reports_not_found()
    {
        CapabilityGrantRepository repository = new();

        CapabilityGrantMutationResult result = repository.Revoke(
            CapabilityGrantId.FromGuid(Guid.NewGuid()),
            AppAuthority.Administrator);

        Assert.Equal(CapabilityGrantMutationStatus.NotFound, result.Status);
    }
}
