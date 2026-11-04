using HackerOs.App.Abstractions;

namespace HackerOs.App.Abstractions.Tests;

public sealed class AppManifestValidatorTests
{
    [Fact]
    public void Validate_accepts_a_complete_window_app_manifest()
    {
        AppManifest manifest = CreateValidManifest() with
        {
            FileHandlers =
            [
                new FileHandlerManifest("text/plain", [".txt"], ["open", "edit"])
            ]
        };

        ManifestValidationResult result = AppManifestValidator.Validate(manifest);

        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Validate_requires_command_metadata_for_terminal_apps()
    {
        AppManifest manifest = CreateValidManifest() with { Kind = AppKind.Terminal };

        ManifestValidationResult result = AppManifestValidator.Validate(manifest);

        Assert.Contains(result.Errors, error => error.Code == "manifest.terminal.required");
    }

    [Fact]
    public void Validate_rejects_file_handlers_for_non_window_apps()
    {
        AppManifest manifest = CreateValidManifest() with
        {
            Kind = AppKind.Service,
            FileHandlers = [new FileHandlerManifest("text/plain", [".txt"], ["open"])]
        };

        ManifestValidationResult result = AppManifestValidator.Validate(manifest);

        Assert.Contains(result.Errors, error => error.Code == "manifest.fileHandlers.forbidden");
    }

    [Fact]
    public void Validate_accepts_serviceStartup_only_on_a_service()
    {
        AppManifest window = CreateValidManifest() with { ServiceStartup = AppServiceStartup.Boot };
        AppManifest invalid = CreateValidManifest() with { Kind = AppKind.Service, ServiceStartup = (AppServiceStartup)42 };

        Assert.Contains(AppManifestValidator.Validate(window).Errors, error => error.Code == "manifest.serviceStartup.forbidden");
        Assert.Contains(AppManifestValidator.Validate(invalid).Errors, error => error.Code == "manifest.serviceStartup.invalid");
    }

    [Theory]
    [InlineData(-1000, true)]
    [InlineData(500, true)]
    [InlineData(-1001, false)]
    [InlineData(1001, false)]
    public void Validate_bounds_oomScoreAdj_to_the_linux_range(int value, bool valid)
    {
        AppManifest manifest = CreateValidManifest() with { OomScoreAdj = value };

        ManifestValidationResult result = AppManifestValidator.Validate(manifest);

        Assert.Equal(valid, !result.Errors.Any(static error => error.Code == "manifest.oomScoreAdj.range"));
    }

    [Fact]
    public void OomScoreAdj_is_omitted_when_undeclared_and_round_trips_when_declared()
    {
        AppManifest plain = CreateValidManifest();
        Assert.DoesNotContain("oomScoreAdj", AppManifestJsonSerializer.SerializeCanonical(plain), StringComparison.Ordinal);

        string json = AppManifestJsonSerializer.SerializeCanonical(plain with { OomScoreAdj = -500 });
        Assert.Contains("\"oomScoreAdj\":-500", json, StringComparison.Ordinal);
        Assert.Equal(-500, AppManifestJsonSerializer.DeserializeStrict(json).OomScoreAdj);
    }

    [Fact]
    public void Validate_accepts_supportsBack_for_window_apps()
    {
        AppManifest manifest = CreateValidManifest() with { SupportsBack = true };

        ManifestValidationResult result = AppManifestValidator.Validate(manifest);

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Validate_rejects_supportsBack_for_non_window_apps()
    {
        AppManifest manifest = CreateValidManifest() with
        {
            Kind = AppKind.Service,
            SupportsBack = true
        };

        ManifestValidationResult result = AppManifestValidator.Validate(manifest);

        Assert.Contains(result.Errors, error => error.Code == "manifest.supportsBack.forbidden");
    }

    [Fact]
    public void Validate_accepts_declared_window_sizes_on_a_window_app()
    {
        AppManifest manifest = CreateValidManifest() with
        {
            Window = new WindowOptions { DefaultWidth = 420, DefaultHeight = 600, MinWidth = 300, MinHeight = 400, MaxWidth = 640 }
        };

        Assert.True(AppManifestValidator.Validate(manifest).IsValid);
    }

    [Theory]
    [InlineData(AppKind.Service, 800, 560, 360, 240, null, "manifest.window.forbidden")]
    [InlineData(AppKind.Window, 0, 560, 360, 240, null, "manifest.window.size.invalid")]
    [InlineData(AppKind.Window, 800, 560, -1, 240, null, "manifest.window.size.invalid")]
    [InlineData(AppKind.Window, 800, 560, 900, 240, null, "manifest.window.minimum.exceedsDefault")]
    [InlineData(AppKind.Window, 800, 560, 360, 240, 700, "manifest.window.default.exceedsMaximum")]
    public void Validate_rejects_inconsistent_window_sizes(
        AppKind kind, int defaultWidth, int defaultHeight, int minWidth, int minHeight, int? maxWidth, string code)
    {
        AppManifest manifest = CreateValidManifest() with
        {
            Kind = kind,
            Window = new WindowOptions
            {
                DefaultWidth = defaultWidth,
                DefaultHeight = defaultHeight,
                MinWidth = minWidth,
                MinHeight = minHeight,
                MaxWidth = maxWidth
            }
        };

        Assert.Contains(AppManifestValidator.Validate(manifest).Errors, error => error.Code == code);
    }

    [Fact]
    public void Validate_rejects_an_inverted_sdk_version_range()
    {
        AppManifest manifest = CreateValidManifest() with
        {
            SdkCompatibility = new AppSdkCompatibilityManifest("2.0.0", "1.9.0")
        };

        ManifestValidationResult result = AppManifestValidator.Validate(manifest);

        Assert.Contains(result.Errors, error => error.Code == "manifest.version.range.invalid");
    }

    [Fact]
    public void Validate_rejects_duplicate_capabilities_case_insensitively()
    {
        AppManifest manifest = CreateValidManifest() with
        {
            Capabilities = ["filesystem.private.read", "FILESYSTEM.PRIVATE.READ"]
        };

        ManifestValidationResult result = AppManifestValidator.Validate(manifest);

        Assert.Contains(result.Errors, error => error.Code == "manifest.value.duplicate");
    }

    [Fact]
    public void Validate_rejects_unknown_capabilities()
    {
        AppManifest manifest = CreateValidManifest() with
        {
            Capabilities = ["not-a-real-capability"]
        };

        ManifestValidationResult result = AppManifestValidator.Validate(manifest);

        Assert.Contains(result.Errors, error => error.Code == "manifest.capability.unknown");
    }

    [Fact]
    public void Validate_rejects_dialog_capabilities_for_non_window_apps()
    {
        AppManifest manifest = CreateValidManifest() with
        {
            Kind = AppKind.Service,
            Capabilities = [AppCapabilities.DialogFileOpen]
        };

        ManifestValidationResult result = AppManifestValidator.Validate(manifest);

        Assert.Contains(result.Errors, error => error.Code == "manifest.capability.incompatible");
    }

    [Fact]
    public void Validate_accepts_dialog_capabilities_for_window_apps()
    {
        AppManifest manifest = CreateValidManifest() with
        {
            Capabilities = [AppCapabilities.DialogFileOpen, AppCapabilities.DialogFileSave, AppCapabilities.DialogFolderSelect]
        };

        ManifestValidationResult result = AppManifestValidator.Validate(manifest);

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Validate_accepts_a_well_formed_topic_permission_as_a_requested_capability()
    {
        AppManifest manifest = CreateValidManifest() with
        {
            Capabilities = ["topic-publish:app/org.hackeros.file-explorer/change-directory"]
        };

        ManifestValidationResult result = AppManifestValidator.Validate(manifest);

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Validate_accepts_a_topic_permission_the_manifest_declares_and_owns()
    {
        AppManifest manifest = CreateValidManifest() with
        {
            DeclaredTopicPermissions =
            [
                new TopicPermissionDeclarationManifest(
                    "topic-publish:app/org.hackeros.text-editor/change-directory",
                    "Allows another app to change this window's current directory.")
            ]
        };

        ManifestValidationResult result = AppManifestValidator.Validate(manifest);

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Validate_rejects_a_declared_topic_permission_not_owned_by_this_app()
    {
        AppManifest manifest = CreateValidManifest() with
        {
            DeclaredTopicPermissions =
            [
                new TopicPermissionDeclarationManifest(
                    "topic-publish:app/org.hackeros.some-other-app/change-directory",
                    "Attempts to declare a permission for a different app's namespace.")
            ]
        };

        ManifestValidationResult result = AppManifestValidator.Validate(manifest);

        Assert.Contains(result.Errors, error => error.Code == "manifest.topicPermission.notOwned");
    }

    [Fact]
    public void Validate_rejects_a_malformed_declared_topic_permission()
    {
        AppManifest manifest = CreateValidManifest() with
        {
            DeclaredTopicPermissions =
            [
                new TopicPermissionDeclarationManifest("not-a-topic-permission", "Malformed identifier.")
            ]
        };

        ManifestValidationResult result = AppManifestValidator.Validate(manifest);

        Assert.Contains(result.Errors, error => error.Code == "manifest.topicPermission.malformed");
    }

    [Fact]
    public void Validate_accepts_components_in_the_entry_point_assembly()
    {
        AppManifest manifest = CreateValidManifest() with
        {
            Components = [new AppComponentManifest("hackeros.terminal", "HackerOs.Apps.TextEditor", "HackerOs.Apps.TextEditor.Embed")]
        };

        Assert.True(AppManifestValidator.Validate(manifest).IsValid);
    }

    [Theory]
    [InlineData(AppKind.Service, "hackeros.terminal", "HackerOs.Apps.TextEditor", "manifest.components.forbidden")]
    [InlineData(AppKind.Window, "Terminal", "HackerOs.Apps.TextEditor", "manifest.component.contract.invalid")]
    [InlineData(AppKind.Window, "hackeros.terminal", "Other.Assembly", "manifest.component.assembly.invalid")]
    public void Validate_rejects_invalid_components(AppKind kind, string contract, string assembly, string code)
    {
        AppManifest manifest = CreateValidManifest() with
        {
            Kind = kind,
            Components = [new AppComponentManifest(contract, assembly, "HackerOs.Apps.TextEditor.Embed")]
        };

        Assert.Contains(AppManifestValidator.Validate(manifest).Errors, error => error.Code == code);
    }

    [Fact]
    public void Validate_rejects_an_app_registering_the_platform_context_menu()
    {
        AppManifest manifest = CreateValidManifest() with
        {
            Components = [new AppComponentManifest(AppComponentContracts.ContextMenu, "HackerOs.Apps.TextEditor", "HackerOs.Apps.TextEditor.Menu")]
        };

        Assert.Contains(AppManifestValidator.Validate(manifest).Errors, error => error.Code == "manifest.component.contract.platform");
    }

    [Theory]
    [InlineData(AppComponentContracts.FileView)]
    [InlineData(AppComponentContracts.FileDialogs)]
    public void Validate_accepts_the_file_contracts_from_any_window_app(string contract)
    {
        // ADR 0156 §1: the file view and the file dialogs are open; an administrator picks the provider.
        AppManifest manifest = CreateValidManifest() with
        {
            Components = [new AppComponentManifest(contract, "HackerOs.Apps.TextEditor", "HackerOs.Apps.TextEditor.Embed")]
        };

        Assert.True(AppManifestValidator.Validate(manifest).IsValid);
        Assert.Equal(AppComponentProviderScope.AdministratorSelected, AppComponentContracts.GetProviderScope(contract));
    }

    [Fact]
    public void Validate_rejects_a_contract_registered_twice()
    {
        var component = new AppComponentManifest("hackeros.terminal", "HackerOs.Apps.TextEditor", "HackerOs.Apps.TextEditor.Embed");
        AppManifest manifest = CreateValidManifest() with { Components = [component, component with { Type = "Other" }] };

        Assert.False(AppManifestValidator.Validate(manifest).IsValid);
    }

    [Theory]
    [InlineData(AppKind.Window, AppExecutionHost.MainThread, true)]
    [InlineData(AppKind.Window, AppExecutionHost.Pool, false)]
    [InlineData(AppKind.Window, AppExecutionHost.Dedicated, false)]
    [InlineData(AppKind.Service, AppExecutionHost.Dedicated, true)]
    [InlineData(AppKind.Service, AppExecutionHost.MainThread, true)]
    [InlineData(AppKind.Service, AppExecutionHost.Default, false)]
    public void Validate_checks_the_execution_host_against_the_kind(AppKind kind, AppExecutionHost execution, bool valid)
    {
        AppManifest manifest = CreateValidManifest() with { Kind = kind, Execution = execution, FileHandlers = [], Components = null };

        ManifestValidationResult result = AppManifestValidator.Validate(manifest);

        Assert.Equal(valid, !result.Errors.Any(error => error.Code.StartsWith("manifest.execution.", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData(AppKind.Window, null, AppExecutionHost.MainThread)]
    [InlineData(AppKind.Terminal, null, AppExecutionHost.Pool)]
    [InlineData(AppKind.Service, null, AppExecutionHost.Dedicated)]
    [InlineData(AppKind.Terminal, AppExecutionHost.MainThread, AppExecutionHost.MainThread)]
    public void Effective_execution_host_falls_back_to_the_kind_default(AppKind kind, AppExecutionHost? declared, AppExecutionHost expected)
    {
        AppManifest manifest = CreateValidManifest() with { Kind = kind, Execution = declared };

        Assert.Equal(expected, AppExecutionHosts.Of(manifest));
        Assert.Equal(expected != AppExecutionHost.MainThread, AppExecutionHosts.IsWorker(expected));
    }

    [Fact]
    public void Validate_accepts_a_web_app_with_urls()
    {
        ManifestValidationResult result = AppManifestValidator.Validate(CreateWebManifest());

        Assert.True(result.IsValid, string.Join("; ", result.Errors.Select(error => error.Code)));
        Assert.Equal(AppExecutionHost.Web, AppExecutionHosts.Of(CreateWebManifest() with { Execution = null }));
    }

    [Theory]
    [InlineData("intranet.hack/", "manifest.web.url.invalid")]
    [InlineData("http://intranet.hack", "manifest.web.url.invalid")]
    [InlineData("http://+:8080/", "manifest.web.url.invalid")]
    [InlineData("http://intranet.hack/?q=1", "manifest.web.url.invalid")]
    [InlineData("http://Intranet.hack:80/", "manifest.web.url.duplicate")]
    public void Validate_rejects_bad_web_urls(string url, string code)
    {
        AppManifest manifest = CreateWebManifest() with { Web = new WebAppManifest(["http://intranet.hack/", url]) };

        ManifestValidationResult result = AppManifestValidator.Validate(manifest);

        Assert.Contains(result.Errors, error => error.Code == code);
    }

    [Fact]
    public void Validate_requires_urls_for_web_apps_and_forbids_them_elsewhere()
    {
        ManifestValidationResult noUrls = AppManifestValidator.Validate(CreateWebManifest() with { Web = new WebAppManifest([]) });
        ManifestValidationResult onWindow = AppManifestValidator.Validate(CreateValidManifest() with { Web = new WebAppManifest(["http://x.hack/"]) });

        Assert.Contains(noUrls.Errors, error => error.Code == "manifest.web.urls.required");
        Assert.Contains(onWindow.Errors, error => error.Code == "manifest.web.forbidden");
    }

    [Fact]
    public void Validate_keeps_the_blazor_root_in_the_entry_point_assembly()
    {
        AppManifest manifest = CreateWebManifest();
        string assembly = manifest.EntryPoint!.Assembly;
        AppManifest inEntry = manifest with { Web = manifest.Web! with { Blazor = new WebBlazorManifest(assembly, "Intranet.App") } };
        AppManifest elsewhere = manifest with { Web = manifest.Web! with { Blazor = new WebBlazorManifest("Other.dll", "Intranet.App") } };
        AppManifest noType = manifest with { Web = manifest.Web! with { Blazor = new WebBlazorManifest(assembly, "") } };

        Assert.True(AppManifestValidator.Validate(inEntry).IsValid);
        Assert.Contains(AppManifestValidator.Validate(elsewhere).Errors, error => error.Code == "manifest.web.blazor.assembly");
        Assert.False(AppManifestValidator.Validate(noType).IsValid);
    }

    [Fact]
    public void Validate_forbids_file_system_capabilities_for_web_apps()
    {
        AppManifest manifest = CreateWebManifest() with { Capabilities = [AppCapabilities.FileSystemUserSelectedRead] };

        ManifestValidationResult result = AppManifestValidator.Validate(manifest);

        Assert.Contains(result.Errors, error => error.Code == "manifest.web.filesystem");
    }

    [Theory]
    [InlineData(AppKind.Web, AppExecutionHost.Pool, "manifest.execution.web")]
    [InlineData(AppKind.Service, AppExecutionHost.Web, "manifest.execution.web-only")]
    public void Validate_keeps_the_web_worker_for_web_apps(AppKind kind, AppExecutionHost execution, string code)
    {
        AppManifest manifest = (kind == AppKind.Web ? CreateWebManifest() : CreateValidManifest() with { Kind = kind, Capabilities = [] })
            with { Execution = execution };

        ManifestValidationResult result = AppManifestValidator.Validate(manifest);

        Assert.Contains(result.Errors, error => error.Code == code);
    }

    private static AppManifest CreateWebManifest() => CreateValidManifest() with
    {
        Id = "org.hackeros.intranet",
        Name = "Intranet",
        Kind = AppKind.Web,
        Execution = AppExecutionHost.Web,
        Capabilities = [],
        Presentation = new PresentationManifest("system", AppLaunchVisibility.Hidden, []),
        Web = new WebAppManifest(["http://intranet.hack/"])
    };

    private static AppManifest CreateValidManifest() => new()
    {
        Id = "org.hackeros.text-editor",
        Name = "Text Editor",
        Version = "1.0.0",
        PublisherId = "org.hackeros",
        Description = "Edits text files in the KazoOS virtual filesystem.",
        Kind = AppKind.Window,
        EntryPoint = new AppEntryPointManifest("HackerOs.Apps.TextEditor", "HackerOs.Apps.TextEditor.TextEditorApp"),
        SdkCompatibility = new AppSdkCompatibilityManifest(HackerOsSdk.Version),
        Presentation = new PresentationManifest("editors", AppLaunchVisibility.Visible, []),
        Resources = AppResourceProfileManifest.None,
        Capabilities = [AppCapabilities.FileSystemUserSelectedRead, AppCapabilities.FileSystemUserSelectedWrite]
    };
}