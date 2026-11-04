using Kazo.Os.App.Abstractions;
using Kazo.Os.App.Abstractions.Policy;
using Kazo.Os.AppSdk.Administration;
using Kazo.Os.AppSdk.IO;
using Kazo.Os.Platform.Core.Administration;
using Kazo.Os.Platform.Core.ComponentProviders;
using Kazo.Os.Platform.Core.Diagnostics;
using Kazo.Os.Platform.Core.Elevation;
using Kazo.Os.Platform.Core.Lifecycle;
using Kazo.Os.Platform.Core.Policy;
using Kazo.Os.Platform.Core.Tests.Settings;
using Kazo.Os.Simulation.Abstractions.Gateways;
using Kazo.Os.Simulation.Abstractions.Sessions;

namespace Kazo.Os.Platform.Core.Tests.Administration;

/// <summary>ADR 0156 §3: <c>KazoComponentProviders</c> behind Settings &gt; OS components.</summary>
public sealed class ComponentProviderAdministrationGatewayTests
{
    private const string ExplorerId = "org.hackeros.file-explorer";
    private const string FilesId = "com.example.files";
    private const string SettingsId = "org.hackeros.settings";
    private const string ConfigPath = "/etc/hackeros/components.config";

    [Fact]
    public async Task Every_call_requires_apps_manage()
    {
        Harness harness = await Harness.CreateAsync();
        AppComponentProviderAdministrationGateway gateway = harness.CreateGateway(new CountingAgent(succeed: true));

        await Assert.ThrowsAsync<AppGatewayAccessDeniedException>(() => gateway.GetOverviewAsync().AsTask());
        await Assert.ThrowsAsync<AppGatewayAccessDeniedException>(
            () => gateway.SetProviderAsync([AppComponentContracts.FileView], null).AsTask());
    }

    [Fact]
    public async Task A_third_party_app_goes_through_the_approval_window_not_the_password_prompt()
    {
        Harness harness = await Harness.CreateAsync();
        CountingAgent agent = new(succeed: true);
        AppComponentProviderAdministrationGateway gateway = harness.CreateGateway(agent, AppCapabilities.AppsManage);

        KazoUnauthorizedAccessException denied = await Assert.ThrowsAsync<KazoUnauthorizedAccessException>(
            () => gateway.SetProviderAsync([AppComponentContracts.FileView, AppComponentContracts.FileDialogs], FilesId).AsTask());

        Assert.Equal(1, harness.Prompt.Calls);
        Assert.Equal(0, agent.Calls);
        Assert.Contains("Dismissed", denied.Message, StringComparison.Ordinal);
        Assert.Null(await harness.Files.ReadAsync(ConfigPath));
    }

    [Fact]
    public async Task Going_back_to_the_explorer_only_asks_an_administrator()
    {
        Harness harness = await Harness.CreateAsync();
        await harness.Files.WriteAsRootAsync(ConfigPath, "[providers]\nhackeros.file-view=com.example.files\n");
        CountingAgent agent = new(succeed: true);
        AppComponentProviderAdministrationGateway gateway = harness.CreateGateway(agent, AppCapabilities.AppsManage);

        await gateway.SetProviderAsync([AppComponentContracts.FileView], null);

        Assert.Equal(0, harness.Prompt.Calls);
        Assert.Equal(ComponentProviderService.AuditAction, agent.LastRequest!.Action);
        KazoComponentContractInfo view = (await gateway.GetOverviewAsync())[0];
        Assert.Null(view.SelectedAppId);
        Assert.Equal(ExplorerId, view.DefaultAppId);
        Assert.Equal([ExplorerId, FilesId], view.Providers.Select(provider => provider.AppId));
    }

    [Fact]
    public async Task A_refused_administrator_writes_nothing()
    {
        Harness harness = await Harness.CreateAsync();
        CountingAgent agent = new(succeed: false);
        AppComponentProviderAdministrationGateway gateway = harness.CreateGateway(agent, AppCapabilities.AppsManage);

        await Assert.ThrowsAsync<KazoUnauthorizedAccessException>(
            () => gateway.SetProviderAsync([AppComponentContracts.FileView], ExplorerId).AsTask());

        Assert.Equal(1, agent.Calls);
        Assert.Null(await harness.Files.ReadAsync(ConfigPath));
    }

    [Fact]
    public async Task An_impossible_choice_is_refused_before_any_prompt()
    {
        Harness harness = await Harness.CreateAsync();
        CountingAgent agent = new(succeed: true);
        AppComponentProviderAdministrationGateway gateway = harness.CreateGateway(agent, AppCapabilities.AppsManage);

        await Assert.ThrowsAsync<ArgumentException>(
            () => gateway.SetProviderAsync([AppComponentContracts.ContextMenu], FilesId).AsTask());
        await Assert.ThrowsAsync<ArgumentException>(
            () => gateway.SetProviderAsync([AppComponentContracts.FileView], SettingsId).AsTask());

        Assert.Equal(0, agent.Calls);
        Assert.Equal(0, harness.Prompt.Calls);
    }

    private static AppManifest Manifest(string id, string[] capabilities, params string[] contracts) => new()
    {
        Id = id,
        Name = id,
        Version = "1.0.0",
        PublisherId = id.StartsWith("org.hackeros", StringComparison.Ordinal) ? "pub.hackeros" : "com.example",
        Description = "Component provider fixture.",
        Kind = AppKind.Window,
        EntryPoint = new AppEntryPointManifest("Kazo.Os.Apps.Sample", "Kazo.Os.Apps.Sample.SampleApp"),
        SdkCompatibility = new AppSdkCompatibilityManifest(KazoOsSdk.Version),
        Presentation = new PresentationManifest("test", AppLaunchVisibility.Visible, []),
        Resources = AppResourceProfileManifest.None,
        Capabilities = capabilities,
        Components = [.. contracts.Select(contract => new AppComponentManifest(contract, "Kazo.Os.Apps.Sample", "Kazo.Os.Apps.Sample.Embed"))]
    };

    /// <summary>Settings (system app) calling the gateway on behalf of alice.</summary>
    private sealed class Harness
    {
        private Harness(SettingsFilesHarness files, AuthenticatedPrincipal principal)
        {
            Files = files;
            Principal = principal;
            AppCatalogBuildResult built = AppCatalog.Build(
                [
                    Manifest(ExplorerId, [], AppComponentContracts.FileView, AppComponentContracts.FileDialogs),
                    Manifest(FilesId, [], AppComponentContracts.FileView, AppComponentContracts.FileDialogs),
                    Manifest(SettingsId, [AppCapabilities.AppsManage])
                ],
                [ExplorerId, SettingsId]);
            Assert.True(built.IsSuccess, string.Join(", ", built.Errors.Select(error => error.Message)));
            Catalog = built.Catalog!;
            BoundedAuditLog audit = new(maxEntries: 200);
            Service = new ComponentProviderService(
                Catalog, new AppEnablementRegistry(Catalog), files.Settings, new AdminElevationVerifier(files.Users, audit), audit);
        }

        internal SettingsFilesHarness Files { get; }
        internal AuthenticatedPrincipal Principal { get; }
        internal AppCatalog Catalog { get; }
        internal ComponentProviderService Service { get; }
        internal CountingPrompt Prompt { get; } = new();

        internal static async Task<Harness> CreateAsync()
        {
            SettingsFilesHarness files = new();
            await files.BootAsync();
            AuthenticatedPrincipal alice = await files.LoginAsync(files.Alice);
            return new Harness(files, alice);
        }

        internal AppComponentProviderAdministrationGateway CreateGateway(IAdministratorAuthenticationAgent agent, params string[] capabilities) =>
            new(() => Service, () => Prompt, new FixedCapabilities(capabilities),
                new AdministratorGate(() => agent, Catalog.Manifests[SettingsId], Principal, isRootElevated: false));
    }

    /// <summary>Records requests and answers with a fixed outcome.</summary>
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

    /// <summary>Grants exactly the listed capabilities (the gateway only calls <see cref="Require"/>).</summary>
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

    /// <summary>An approval window the user closes without choosing.</summary>
    private sealed class CountingPrompt : IDangerousCapabilityApprovalPrompt
    {
        public int Calls { get; private set; }

        public Task<DangerousCapabilityApprovalOutcome> RequestAsync(
            DangerousCapabilityApprovalRequest request, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(DangerousCapabilityApprovalOutcome.Dismissed);
        }
    }
}
