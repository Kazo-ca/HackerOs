using Kazo.Os.App.Abstractions;
using Kazo.Os.App.Abstractions.Policy;
using Kazo.Os.Platform.Core.Policy;

namespace Kazo.Os.Platform.Core.Tests.Policy;

public sealed class CleanProfileCapabilityGrantSeederTests
{
    [Fact]
    public void Seeding_grants_exactly_the_declared_capabilities_and_nothing_more()
    {
        CapabilityGrantRepository repository = new();
        AppManifest manifest = CreateManifest([
            AppCapabilities.FileSystemUserSelectedRead,
            AppCapabilities.FileSystemUserSelectedWrite
        ]);

        IReadOnlyList<CapabilityGrantMutationResult> results =
            CleanProfileCapabilityGrantSeeder.SeedDeclaredCapabilities(repository, manifest, "user-1", isSystemApp: false);

        Assert.All(results, result => Assert.Equal(CapabilityGrantMutationStatus.Granted, result.Status));

        CapabilityPolicyEvaluation declared = repository.Evaluate(
            manifest.Id,
            "user-1",
            AppCapabilities.FileSystemUserSelectedRead,
            AppAuthority.User,
            AppAuthority.User);
        CapabilityPolicyEvaluation undeclared = repository.Evaluate(
            manifest.Id,
            "user-1",
            AppCapabilities.FileSystemSystemWrite,
            AppAuthority.User,
            AppAuthority.User);

        Assert.True(declared.Granted);
        Assert.False(undeclared.Granted);
        Assert.Equal(CapabilityPolicyEvaluationReason.Missing, undeclared.Reason);
    }

    [Fact]
    public void System_authority_alone_does_not_imply_an_undeclared_capability()
    {
        CapabilityGrantRepository repository = new();
        AppManifest manifest = CreateManifest([AppCapabilities.FileSystemPrivateRead]);

        CleanProfileCapabilityGrantSeeder.SeedDeclaredCapabilities(repository, manifest, "user-1", isSystemApp: false);

        CapabilityPolicyEvaluation evaluation = repository.Evaluate(
            manifest.Id,
            "user-1",
            AppCapabilities.FileSystemSystemWrite,
            AppAuthority.System,
            AppAuthority.User);

        Assert.False(evaluation.Granted);
        Assert.Equal(CapabilityPolicyEvaluationReason.Missing, evaluation.Reason);
    }

    [Fact]
    public void Seeding_is_idempotent_when_called_multiple_times()
    {
        CapabilityGrantRepository repository = new();
        AppManifest manifest = CreateManifest([
            AppCapabilities.FileSystemUserSelectedRead,
            AppCapabilities.FileSystemUserSelectedWrite
        ]);

        IReadOnlyList<CapabilityGrantMutationResult> firstPass =
            CleanProfileCapabilityGrantSeeder.SeedDeclaredCapabilities(repository, manifest, "user-1", isSystemApp: false);
        IReadOnlyList<CapabilityGrantMutationResult> secondPass =
            CleanProfileCapabilityGrantSeeder.SeedDeclaredCapabilities(repository, manifest, "user-1", isSystemApp: false);

        Assert.Equal(2, firstPass.Count);
        Assert.Empty(secondPass);
    }

    [Fact]
    public void Normal_capabilities_are_granted_from_the_build_profile_to_any_app()
    {
        CapabilityGrantRepository repository = new();
        AppManifest manifest = CreateManifest([AppCapabilities.ProcessList, AppCapabilities.NotificationsPost]);

        IReadOnlyList<CapabilityGrantMutationResult> results =
            CleanProfileCapabilityGrantSeeder.SeedDeclaredCapabilities(repository, manifest, "user-1", isSystemApp: false);

        Assert.Equal(2, results.Count);
        Assert.All(results, result => Assert.Equal(CapabilityGrantSource.BuildProfile, result.Grant!.Source));
    }

    [Fact]
    public void System_app_receives_its_dangerous_capabilities_from_system_policy_without_approval()
    {
        CapabilityGrantRepository repository = new();
        AppManifest manifest = CreateManifest([AppCapabilities.ProcessList, AppCapabilities.ProcessManage]);

        IReadOnlyList<CapabilityGrantMutationResult> results =
            CleanProfileCapabilityGrantSeeder.SeedDeclaredCapabilities(repository, manifest, "user-1", isSystemApp: true);

        Assert.Equal(
            [CapabilityGrantSource.BuildProfile, CapabilityGrantSource.SystemPolicy],
            results.Select(result => result.Grant!.Source));
        Assert.True(Evaluate(repository, manifest, AppCapabilities.ProcessManage).Granted);
    }

    [Fact]
    public void Non_system_app_without_a_decision_does_not_receive_its_dangerous_capabilities()
    {
        CapabilityGrantRepository repository = new();
        AppManifest manifest = CreateManifest([
            AppCapabilities.FileSystemUserHomeRead,
            AppCapabilities.NetworkRealAccess,
            AppCapabilities.FileSystemSystemWrite
        ]);

        IReadOnlyList<CapabilityGrantMutationResult> results =
            CleanProfileCapabilityGrantSeeder.SeedDeclaredCapabilities(repository, manifest, "user-1", isSystemApp: false);

        CapabilityGrantMutationResult granted = Assert.Single(results);
        Assert.Equal(AppCapabilities.FileSystemUserHomeRead, granted.Grant!.Capability);
        Assert.True(Evaluate(repository, manifest, AppCapabilities.FileSystemUserHomeRead).Granted);
        CapabilityPolicyEvaluation network = Evaluate(repository, manifest, AppCapabilities.NetworkRealAccess);
        Assert.False(network.Granted);
        Assert.Equal(CapabilityPolicyEvaluationReason.Missing, network.Reason);
        Assert.False(Evaluate(repository, manifest, AppCapabilities.FileSystemSystemWrite).Granted);
    }

    private static CapabilityPolicyEvaluation Evaluate(
        CapabilityGrantRepository repository, AppManifest manifest, string capability) =>
        repository.Evaluate(manifest.Id, "user-1", capability, AppAuthority.User, AppAuthority.User);

    private static AppManifest CreateManifest(IReadOnlyList<string> capabilities) => new()
    {
        Id = "org.kazoos.sample-app",
        Name = "Sample App",
        Version = "1.0.0",
        PublisherId = "org.kazoos",
        Description = "A manifest fixture for clean-profile default grant tests.",
        Kind = AppKind.Window,
        EntryPoint = new AppEntryPointManifest("Kazo.Os.Apps.Sample", "Kazo.Os.Apps.Sample.SampleApp"),
        SdkCompatibility = new AppSdkCompatibilityManifest(KazoOsSdk.Version),
        Presentation = new PresentationManifest("test", AppLaunchVisibility.Visible, []),
        Resources = AppResourceProfileManifest.None,
        Capabilities = capabilities
    };
}
