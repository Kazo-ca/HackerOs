using Kazo.Os.App.Abstractions;
using Kazo.Os.Platform.Core.ComponentProviders;
using Kazo.Os.Platform.Core.Diagnostics;
using Kazo.Os.Platform.Core.Elevation;
using Kazo.Os.Platform.Core.Lifecycle;
using Kazo.Os.Platform.Core.Policy;
using Kazo.Os.Platform.Core.Sessions;
using Kazo.Os.Platform.Core.Tests.Settings;
using Kazo.Os.Simulation.Abstractions.Diagnostics;
using Kazo.Os.Simulation.Abstractions.Sessions;

namespace Kazo.Os.Platform.Core.Tests.ComponentProviders;

/// <summary>ADR 0156: /etc/hackeros/components.config, its approval and its clean-up.</summary>
public sealed class ComponentProviderServiceTests
{
    private const string ExplorerId = "org.hackeros.file-explorer";
    private const string FilesId = "com.example.files";
    private const string ViewerId = "com.example.viewer";
    private const string ConfigPath = "/etc/hackeros/components.config";
    private const string AdminPassword = "s3cret-admin";

    private static readonly string[] Both = [AppComponentContracts.FileView, AppComponentContracts.FileDialogs];

    // ---- The file (§2) ----

    [Fact]
    public void Parse_reads_the_providers_section_and_ignores_other_contracts()
    {
        IReadOnlyDictionary<string, string> choices = ComponentProvidersDocument.Parse(
            """
            # comment
            [providers]
            hackeros.file-view=com.example.files
            hackeros.context-menu=com.example.menu
            hackeros.terminal=com.example.term
            [other]
            hackeros.file-dialogs=com.example.ignored
            """);

        Assert.Equal(FilesId, Assert.Single(choices).Value);
        Assert.Equal(AppComponentContracts.FileView, choices.Keys.Single());
        Assert.Empty(ComponentProvidersDocument.Parse(null));
    }

    [Fact]
    public void Edit_keeps_comments_and_removes_a_line_to_go_back_to_the_default()
    {
        string written = ComponentProvidersDocument.Edit(null, AppComponentContracts.FileView, FilesId)!;
        Assert.StartsWith("# /etc/hackeros/components.config", written, StringComparison.Ordinal);
        Assert.Contains("[providers]", written, StringComparison.Ordinal);
        Assert.Contains("hackeros.file-view=com.example.files", written, StringComparison.Ordinal);

        string removed = ComponentProvidersDocument.Edit(written, AppComponentContracts.FileView, null)!;
        Assert.Empty(ComponentProvidersDocument.Parse(removed));
        Assert.StartsWith("# /etc/hackeros/components.config", removed, StringComparison.Ordinal);
        Assert.Null(ComponentProvidersDocument.Edit(null, AppComponentContracts.FileView, null));
    }

    [Fact]
    public async Task A_fresh_installation_has_no_choice_and_the_explorer_is_the_default()
    {
        Harness harness = await Harness.CreateAsync();

        Assert.Null(await harness.Service.GetSelectedProviderAsync(AppComponentContracts.FileView));
        ComponentProviderOverview view = (await harness.Service.GetOverviewAsync())[0];
        Assert.Equal(AppComponentContracts.FileView, view.Contract);
        Assert.Null(view.SelectedAppId);
        Assert.Equal(ExplorerId, view.DefaultAppId);
        Assert.Equal([ExplorerId, FilesId, ViewerId], view.Providers.Select(provider => provider.AppId));
        Assert.True(view.Providers[0].IsSystemApp);
        Assert.False(view.Providers[1].IsSystemApp);
    }

    [Fact]
    public async Task A_hand_edited_file_applies_at_the_next_read()
    {
        Harness harness = await Harness.CreateAsync();
        await harness.Files.WriteAsRootAsync(ConfigPath, "[providers]\nhackeros.file-dialogs=com.example.files\n");

        Assert.Equal(FilesId, await harness.Service.GetSelectedProviderAsync(AppComponentContracts.FileDialogs));
        Assert.Null(await harness.Service.GetSelectedProviderAsync(AppComponentContracts.FileView));
        Assert.Null(await harness.Service.GetSelectedProviderAsync("hackeros.terminal"));
    }

    // ---- Validation (§1, §3) ----

    [Fact]
    public async Task A_choice_is_refused_before_any_password_when_it_cannot_apply()
    {
        Harness harness = await Harness.CreateAsync();

        Assert.Throws<ArgumentException>(() => harness.Service.ValidateChoice([AppComponentContracts.ContextMenu], FilesId));
        Assert.Throws<ArgumentException>(() => harness.Service.ValidateChoice(["hackeros.terminal"], FilesId));
        // The viewer provides the file view only.
        Assert.Throws<ArgumentException>(() => harness.Service.ValidateChoice(Both, ViewerId));
        Assert.Throws<ArgumentException>(() => harness.Service.ValidateChoice([], FilesId));
        harness.Enablement.MarkDisabled([FilesId]);
        Assert.Throws<ArgumentException>(() => harness.Service.ValidateChoice([AppComponentContracts.FileView], FilesId));
        harness.Service.ValidateChoice(Both, null);
    }

    [Fact]
    public async Task Only_a_third_party_app_needs_the_approval_window()
    {
        Harness harness = await Harness.CreateAsync();

        Assert.True(harness.Service.RequiresApproval(FilesId));
        Assert.False(harness.Service.RequiresApproval(ExplorerId));
        Assert.False(harness.Service.RequiresApproval(null));
    }

    // ---- Approval of a third-party app (§3) ----

    [Fact]
    public async Task An_approved_third_party_app_is_written_for_both_contracts_and_logged()
    {
        Harness harness = await Harness.CreateAsync();
        harness.Prompt.Script = async (request, ct) =>
        {
            DangerousCapabilityApprovalAttempt attempt = await request.ApproveAsync("admin", AdminPassword, ct);
            Assert.True(attempt.Succeeded);
            return DangerousCapabilityApprovalOutcome.Approved;
        };

        ComponentProviderChangeOutcome outcome = await harness.Service.ApproveAsync(
            Both, FilesId, harness.Principal, harness.Prompt, System.Globalization.CultureInfo.GetCultureInfo("fr"));

        Assert.Equal(ComponentProviderChangeOutcome.Applied, outcome);
        DangerousCapabilityApprovalRequest request = harness.Prompt.LastRequest!;
        Assert.Equal(FilesId, request.Manifest.Id);
        Assert.Equal(Both, request.Capabilities.Select(capability => capability.Capability));
        Assert.Equal(
            "Le code de cette app s'exécutera dans toutes les applications qui affichent des fichiers, avec leurs droits.",
            request.Capabilities[0].RiskDescription);
        Assert.Equal(FilesId, await harness.Service.GetSelectedProviderAsync(AppComponentContracts.FileView));
        Assert.Equal(FilesId, await harness.Service.GetSelectedProviderAsync(AppComponentContracts.FileDialogs));
        AuditEntry entry = Assert.Single(harness.Audit.Entries, item => item.Action == ComponentProviderService.AuditAction);
        Assert.Equal(AuditOutcome.Success, entry.Outcome);
        Assert.Equal(FilesId, entry.Properties["provider"]);
    }

    [Fact]
    public async Task A_refusal_or_a_wrong_password_writes_nothing()
    {
        Harness harness = await Harness.CreateAsync();
        harness.Prompt.Script = async (request, ct) =>
        {
            DangerousCapabilityApprovalAttempt attempt = await request.ApproveAsync("admin", "wrong", ct);
            Assert.False(attempt.Succeeded);
            return DangerousCapabilityApprovalOutcome.Refused;
        };

        ComponentProviderChangeOutcome outcome = await harness.Service.ApproveAsync(
            [AppComponentContracts.FileView], FilesId, harness.Principal, harness.Prompt, null);

        Assert.Equal(ComponentProviderChangeOutcome.Refused, outcome);
        Assert.Null(await harness.Files.ReadAsync(ConfigPath));
        Assert.All(
            harness.Audit.Entries.Where(item => item.Action == ComponentProviderService.AuditAction),
            item => Assert.Equal(AuditOutcome.Denied, item.Outcome));
    }

    [Fact]
    public async Task Without_an_approval_window_nothing_is_written()
    {
        Harness harness = await Harness.CreateAsync();

        ComponentProviderChangeOutcome outcome = await harness.Service.ApproveAsync(
            [AppComponentContracts.FileView], FilesId, harness.Principal, prompt: null, null);

        Assert.Equal(ComponentProviderChangeOutcome.Unavailable, outcome);
        Assert.Null(await harness.Files.ReadAsync(ConfigPath));
    }

    [Fact]
    public async Task Going_back_to_the_default_removes_the_line()
    {
        Harness harness = await Harness.CreateAsync();
        await harness.Files.WriteAsRootAsync(ConfigPath, "# kept\n[providers]\nhackeros.file-view=com.example.files\n");

        await harness.Service.ApplyAsync([AppComponentContracts.FileView], null, "admin", harness.Principal);

        string content = (await harness.Files.ReadAsync(ConfigPath))!;
        Assert.Contains("# kept", content, StringComparison.Ordinal);
        Assert.Null(await harness.Service.GetSelectedProviderAsync(AppComponentContracts.FileView));
        AuditEntry entry = Assert.Single(harness.Audit.Entries, item => item.Action == ComponentProviderService.AuditAction);
        Assert.Equal("default", entry.Properties["provider"]);
        Assert.Equal("admin", entry.Actor);
    }

    // ---- Uninstall (§4) ----

    [Fact]
    public async Task An_app_uninstalled_for_the_machine_loses_its_line()
    {
        Harness harness = await Harness.CreateAsync();
        List<string> syslog = [];
        harness.Service = harness.CreateService(syslog.Add);
        await harness.Files.WriteAsRootAsync(
            ConfigPath, "[providers]\nhackeros.file-view=com.example.viewer\nhackeros.file-dialogs=com.example.files\n");

        IReadOnlyList<string> untouched = await harness.Service.RemoveUninstalledAsync(appId => appId == "org.example.other");
        IReadOnlyList<string> removed = await harness.Service.RemoveUninstalledAsync(appId => appId == ViewerId);

        Assert.Empty(untouched);
        Assert.Equal([AppComponentContracts.FileView], removed);
        Assert.Null(await harness.Service.GetSelectedProviderAsync(AppComponentContracts.FileView));
        Assert.Equal(FilesId, await harness.Service.GetSelectedProviderAsync(AppComponentContracts.FileDialogs));
        Assert.Contains("component.provider.removed", Assert.Single(syslog), StringComparison.Ordinal);
    }

    private static AppManifest Manifest(string id, string name, params string[] contracts) => new()
    {
        Id = id,
        Name = name,
        Version = "1.0.0",
        PublisherId = id.StartsWith("org.hackeros", StringComparison.Ordinal) ? "pub.hackeros" : "com.example",
        Description = "Component provider fixture.",
        Kind = AppKind.Window,
        EntryPoint = new AppEntryPointManifest("Kazo.Os.Apps.Sample", "Kazo.Os.Apps.Sample.SampleApp"),
        SdkCompatibility = new AppSdkCompatibilityManifest(KazoOsSdk.Version),
        Presentation = new PresentationManifest("test", AppLaunchVisibility.Visible, []),
        Resources = AppResourceProfileManifest.None,
        Components = [.. contracts.Select(contract => new AppComponentManifest(contract, "Kazo.Os.Apps.Sample", "Kazo.Os.Apps.Sample.Embed"))]
    };

    /// <summary>Real files, settings and administrator verification around the service.</summary>
    private sealed class Harness
    {
        private Harness(SettingsFilesHarness files, AuthenticatedPrincipal principal)
        {
            Files = files;
            Principal = principal;
            AppCatalogBuildResult built = AppCatalog.Build(
                [
                    Manifest(ExplorerId, "Files", Both),
                    Manifest(FilesId, "Example Files", Both),
                    Manifest(ViewerId, "Example Viewer", AppComponentContracts.FileView)
                ],
                [ExplorerId]);
            Assert.True(built.IsSuccess, string.Join(", ", built.Errors.Select(error => error.Message)));
            Catalog = built.Catalog!;
            Enablement = new AppEnablementRegistry(Catalog);
            Audit = new BoundedAuditLog(maxEntries: 200);
            Verifier = new AdminElevationVerifier(files.Users, Audit);
            Service = CreateService(null);
        }

        internal SettingsFilesHarness Files { get; }
        internal AuthenticatedPrincipal Principal { get; }
        internal AppCatalog Catalog { get; }
        internal AppEnablementRegistry Enablement { get; }
        internal BoundedAuditLog Audit { get; }
        internal AdminElevationVerifier Verifier { get; }
        internal ComponentProviderService Service { get; set; }
        internal FakePrompt Prompt { get; } = new();

        internal ComponentProviderService CreateService(Action<string>? reportInformation) =>
            new(Catalog, Enablement, Files.Settings, Verifier, Audit, reportInformation);

        internal static async Task<Harness> CreateAsync()
        {
            SettingsFilesHarness files = new();
            files.Users.CreateUser(
                LocalLoginName.Parse("admin"), "Admin", AppAuthority.Administrator, files.Alice.PrimaryGroupId,
                credential: LocalPasswordHasher.Create(AdminPassword, iterations: 100));
            await files.BootAsync();
            AuthenticatedPrincipal alice = await files.LoginAsync(files.Alice);
            return new Harness(files, alice);
        }
    }

    private sealed class FakePrompt : IDangerousCapabilityApprovalPrompt
    {
        internal Func<DangerousCapabilityApprovalRequest, CancellationToken, Task<DangerousCapabilityApprovalOutcome>> Script { get; set; } =
            (_, _) => Task.FromResult(DangerousCapabilityApprovalOutcome.Dismissed);

        internal DangerousCapabilityApprovalRequest? LastRequest { get; private set; }

        public Task<DangerousCapabilityApprovalOutcome> RequestAsync(
            DangerousCapabilityApprovalRequest request, CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            return Script(request, cancellationToken);
        }
    }
}
