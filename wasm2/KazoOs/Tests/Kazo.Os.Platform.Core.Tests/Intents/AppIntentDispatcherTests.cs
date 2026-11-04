using Kazo.Os.Platform.Core.Settings;
using Kazo.Os.App.Abstractions;
using Kazo.Os.App.Abstractions.Policy;
using Kazo.Os.AppSdk;
using Kazo.Os.Platform.Core.Diagnostics;
using Kazo.Os.Platform.Core.Discovery;
using Kazo.Os.Platform.Core.Events;
using Kazo.Os.Platform.Core.Execution;
using Kazo.Os.Platform.Core.FileSystem;
using Kazo.Os.Platform.Core.Intents;
using Kazo.Os.Platform.Core.Lifecycle;
using Kazo.Os.Platform.Core.Notifications;
using Kazo.Os.Platform.Core.Policy;
using Kazo.Os.Platform.Core.Processes;
using Kazo.Os.Platform.Core.Sessions;
using Kazo.Os.Platform.Core.Time;
using Kazo.Os.Simulation.Abstractions;
using Kazo.Os.Simulation.Abstractions.Diagnostics;
using Kazo.Os.Simulation.Abstractions.Events;
using Kazo.Os.Simulation.Abstractions.FileSystem;
using Kazo.Os.Simulation.Abstractions.Gateways;
using Kazo.Os.Simulation.Abstractions.Processes;
using Kazo.Os.Simulation.Abstractions.Sessions;

namespace Kazo.Os.Platform.Core.Tests.Intents;

/// <summary>
/// Tests for `P1-APP-007`: capability-gated dispatch of launch, open-file, execute-command, and
/// deferred-UI intents.
/// </summary>
public sealed class AppIntentDispatcherTests
{
    [Fact]
    public async Task Launching_another_app_is_denied_without_the_apps_launch_capability()
    {
        Fixture fixture = new(EchoManifest());
        AuthenticatedPrincipal principal = await fixture.LoginAsync();
        AppIntentRequest request = new(Guid.NewGuid(), "org.hackeros.shell", principal.UserId.ToString(), new LaunchAppIntent("org.hackeros.echo", []));

        AppIntentDispatchResult result = await fixture.Dispatcher.DispatchAsync(request, principal);

        Assert.Equal(AppIntentDispatchStatus.CapabilityDenied, result.Status);
    }

    [Fact]
    public async Task Launching_another_app_succeeds_once_the_caller_holds_apps_launch()
    {
        Fixture fixture = new(EchoManifest());
        AuthenticatedPrincipal principal = await fixture.LoginAsync();
        fixture.Grant("org.hackeros.shell", principal, AppCapabilities.AppsLaunch);
        AppIntentRequest request = new(Guid.NewGuid(), "org.hackeros.shell", principal.UserId.ToString(), new LaunchAppIntent("org.hackeros.echo", ["a"]));

        AppIntentDispatchResult result = await fixture.Dispatcher.DispatchAsync(request, principal);

        Assert.Equal(AppIntentDispatchStatus.Dispatched, result.Status);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Starting_a_service_is_allowed_for_a_caller_in_the_same_assembly_without_any_capability()
    {
        // EchoManifest and WaiterServiceManifest both resolve their entry point in this test
        // assembly, so they count as "the same package" for service-control purposes.
        Fixture fixture = new(EchoManifest(), WaiterServiceManifest());
        AuthenticatedPrincipal principal = await fixture.LoginAsync();

        ServiceControlDispatchResult result = await fixture.Dispatcher.StartServiceAsync(
            "org.hackeros.echo", principal.UserId.ToString(), "org.hackeros.waiter", principal);

        Assert.Equal(ServiceControlDispatchStatus.Succeeded, result.Status);
        Assert.True(fixture.Manager.TryGetSingleton("org.hackeros.waiter", out _));
    }

    [Fact]
    public async Task Starting_a_service_is_denied_for_a_different_assembly_caller_without_services_manage()
    {
        // NotepadManifest resolves to a placeholder Window descriptor outside this test assembly,
        // so it is not "the same package" as the service.
        Fixture fixture = new(NotepadManifest(), WaiterServiceManifest());
        AuthenticatedPrincipal principal = await fixture.LoginAsync();

        ServiceControlDispatchResult result = await fixture.Dispatcher.StartServiceAsync(
            "org.hackeros.notepad", principal.UserId.ToString(), "org.hackeros.waiter", principal);

        Assert.Equal(ServiceControlDispatchStatus.CapabilityDenied, result.Status);
        Assert.False(fixture.Manager.TryGetSingleton("org.hackeros.waiter", out _));
    }

    [Fact]
    public async Task Starting_a_service_succeeds_for_a_different_assembly_caller_holding_services_manage()
    {
        Fixture fixture = new(NotepadManifest(), WaiterServiceManifest());
        AuthenticatedPrincipal principal = await fixture.LoginAsync();
        fixture.Grant("org.hackeros.notepad", principal, AppCapabilities.ServicesManage);

        ServiceControlDispatchResult result = await fixture.Dispatcher.StartServiceAsync(
            "org.hackeros.notepad", principal.UserId.ToString(), "org.hackeros.waiter", principal);

        Assert.Equal(ServiceControlDispatchStatus.Succeeded, result.Status);
    }

    [Fact]
    public async Task Controlling_a_non_service_target_is_rejected_even_from_the_same_assembly()
    {
        Fixture fixture = new(EchoManifest(), TextEditorManifest());
        AuthenticatedPrincipal principal = await fixture.LoginAsync();

        ServiceControlDispatchResult result = await fixture.Dispatcher.StartServiceAsync(
            "org.hackeros.echo", principal.UserId.ToString(), "org.hackeros.texteditor", principal);

        Assert.Equal(ServiceControlDispatchStatus.NotAService, result.Status);
    }

    [Fact]
    public async Task Controlling_an_unknown_app_id_is_not_found()
    {
        Fixture fixture = new(EchoManifest());
        AuthenticatedPrincipal principal = await fixture.LoginAsync();

        ServiceControlDispatchResult result = await fixture.Dispatcher.StartServiceAsync(
            "org.hackeros.echo", principal.UserId.ToString(), "org.hackeros.missing", principal);

        Assert.Equal(ServiceControlDispatchStatus.NotFound, result.Status);
    }

    [Fact]
    public async Task Stopping_a_service_from_the_same_assembly_stops_its_running_instance()
    {
        Fixture fixture = new(EchoManifest(), WaiterServiceManifest());
        AuthenticatedPrincipal principal = await fixture.LoginAsync();
        await fixture.Dispatcher.StartServiceAsync("org.hackeros.echo", principal.UserId.ToString(), "org.hackeros.waiter", principal);
        Assert.True(fixture.Manager.TryGetSingleton("org.hackeros.waiter", out _));

        ServiceControlDispatchResult result = await fixture.Dispatcher.StopServiceAsync(
            "org.hackeros.echo", principal.UserId.ToString(), "org.hackeros.waiter", principal);

        Assert.Equal(ServiceControlDispatchStatus.Succeeded, result.Status);
        Assert.False(fixture.Manager.TryGetSingleton("org.hackeros.waiter", out _));
    }

    [Fact]
    public async Task Setting_and_reading_back_a_services_start_mode_round_trips_for_a_same_assembly_caller()
    {
        Fixture fixture = new(EchoManifest(), WaiterServiceManifest());
        AuthenticatedPrincipal principal = await fixture.LoginAsync();

        ServiceControlDispatchResult setResult = await fixture.Dispatcher.SetServiceStartModeAsync(
            "org.hackeros.echo", principal.UserId.ToString(), "org.hackeros.waiter", principal, ServiceStartMode.Disabled);
        (ServiceControlDispatchResult getResult, ServiceStartMode mode) = await fixture.Dispatcher.GetServiceStartModeAsync(
            "org.hackeros.echo", principal.UserId.ToString(), "org.hackeros.waiter", principal);

        Assert.Equal(ServiceControlDispatchStatus.Succeeded, setResult.Status);
        Assert.Equal(ServiceControlDispatchStatus.Succeeded, getResult.Status);
        Assert.Equal(ServiceStartMode.Disabled, mode);
    }

    [Fact]
    public async Task Starting_a_disabled_service_is_refused()
    {
        Fixture fixture = new(EchoManifest(), WaiterServiceManifest());
        AuthenticatedPrincipal principal = await fixture.LoginAsync();
        await fixture.Dispatcher.SetServiceStartModeAsync(
            "org.hackeros.echo", principal.UserId.ToString(), "org.hackeros.waiter", principal, ServiceStartMode.Disabled);

        ServiceControlDispatchResult result = await fixture.Dispatcher.StartServiceAsync(
            "org.hackeros.echo", principal.UserId.ToString(), "org.hackeros.waiter", principal);

        Assert.Equal(ServiceControlDispatchStatus.ServiceDisabled, result.Status);
        Assert.False(fixture.Manager.TryGetSingleton("org.hackeros.waiter", out _));
    }

    [Fact]
    public async Task A_system_service_is_controlled_by_an_administrator_only_and_runs_as_the_service_account()
    {
        AppManifest boot = WaiterServiceManifest() with { ServiceStartup = AppServiceStartup.Boot };
        Fixture user = new(["org.hackeros.waiter"], EchoManifest(), boot);
        AuthenticatedPrincipal alice = await user.LoginAsync();
        ServiceControlDispatchResult refusedStart = await user.Dispatcher.StartServiceAsync(
            "org.hackeros.echo", alice.UserId.ToString(), "org.hackeros.waiter", alice);
        ServiceControlDispatchResult refusedMode = await user.Dispatcher.SetServiceStartModeAsync(
            "org.hackeros.echo", alice.UserId.ToString(), "org.hackeros.waiter", alice, ServiceStartMode.Disabled);

        Fixture admin = new(["org.hackeros.waiter"], EchoManifest(), boot);
        AuthenticatedPrincipal root = await admin.LoginAsAdministratorAsync();
        ServiceControlDispatchResult started = await admin.Dispatcher.StartServiceAsync(
            "org.hackeros.echo", root.UserId.ToString(), "org.hackeros.waiter", root);
        ServiceControlDispatchResult setMode = await admin.Dispatcher.SetServiceStartModeAsync(
            "org.hackeros.echo", root.UserId.ToString(), "org.hackeros.waiter", root, ServiceStartMode.Manual);
        (_, ServiceStartMode mode) = await admin.Dispatcher.GetServiceStartModeAsync(
            "org.hackeros.echo", root.UserId.ToString(), "org.hackeros.waiter", root);

        Assert.Equal("service.system.administrator-required", refusedStart.ErrorCode);
        Assert.Equal("service.system.administrator-required", refusedMode.ErrorCode);
        Assert.Equal(ServiceControlDispatchStatus.Succeeded, started.Status);
        Assert.True(admin.Manager.TryGetSingleton("org.hackeros.waiter", out ProcessRecord process));
        Assert.Equal(Kazo.Os.Platform.Core.Sessions.ServiceAccount.UserId, process.UserId);
        Assert.Equal(ServiceControlDispatchStatus.Succeeded, setMode.Status);
        Assert.Equal(ServiceStartMode.Manual, mode);
    }

    [Fact]
    public async Task Execute_command_intent_resolves_a_terminal_app_by_its_command_name_and_runs_it()
    {
        Fixture fixture = new(EchoManifest());
        AuthenticatedPrincipal principal = await fixture.LoginAsync();
        fixture.Grant("org.hackeros.shell", principal, AppCapabilities.AppsLaunch);
        AppIntentRequest request = new(Guid.NewGuid(), "org.hackeros.shell", principal.UserId.ToString(), new ExecuteCommandIntent("echo hi there"));

        AppIntentDispatchResult result = await fixture.Dispatcher.DispatchAsync(request, principal);

        Assert.Equal(AppIntentDispatchStatus.Dispatched, result.Status);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal($"hi there{Environment.NewLine}", result.StandardOutput);
    }

    [Fact]
    public async Task Execute_command_intent_resolves_a_terminal_app_by_its_alias()
    {
        Fixture fixture = new(EchoManifest());
        AuthenticatedPrincipal principal = await fixture.LoginAsync();
        fixture.Grant("org.hackeros.shell", principal, AppCapabilities.AppsLaunch);
        AppIntentRequest request = new(Guid.NewGuid(), "org.hackeros.shell", principal.UserId.ToString(), new ExecuteCommandIntent("say hello"));

        AppIntentDispatchResult result = await fixture.Dispatcher.DispatchAsync(request, principal);

        Assert.Equal(AppIntentDispatchStatus.Dispatched, result.Status);
        Assert.Equal($"hello{Environment.NewLine}", result.StandardOutput);
    }

    [Fact]
    public async Task Execute_command_passes_full_screen_session_through_dispatch_and_lifecycle()
    {
        Fixture fixture = new(FullScreenManifest());
        AuthenticatedPrincipal principal = await fixture.LoginAsync();
        fixture.Grant("org.hackeros.shell", principal, AppCapabilities.AppsLaunch);
        TestFullScreenTerminal screen = new(new TerminalKeyEvent(TerminalKey.Character, "k"));
        AppIntentRequest request = new(
            Guid.NewGuid(), "org.hackeros.shell", principal.UserId.ToString(),
            new ExecuteCommandIntent("screen"));

        AppIntentDispatchResult result = await fixture.Dispatcher.DispatchAsync(
            request, principal, screen, CancellationToken.None);

        Assert.Equal(AppIntentDispatchStatus.Dispatched, result.Status);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal($"k{Environment.NewLine}", result.StandardOutput);
        Assert.True(screen.Entered);
        Assert.True(screen.Left);
        Assert.NotNull(screen.Frame);
    }

    [Fact]
    public async Task Cancelling_full_screen_command_returns_shell_exit_130_and_restores_screen()
    {
        Fixture fixture = new(FullScreenManifest());
        AuthenticatedPrincipal principal = await fixture.LoginAsync();
        fixture.Grant("org.hackeros.shell", principal, AppCapabilities.AppsLaunch);
        TestFullScreenTerminal screen = new();
        using CancellationTokenSource cancellation = new();
        AppIntentRequest request = new(
            Guid.NewGuid(), "org.hackeros.shell", principal.UserId.ToString(),
            new ExecuteCommandIntent("screen wait"));

        ValueTask<AppIntentDispatchResult> pending = fixture.Dispatcher.DispatchAsync(
            request, principal, screen, cancellation.Token);
        await screen.EnteredSignal.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();
        AppIntentDispatchResult result = await pending;

        Assert.Equal(AppIntentDispatchStatus.Dispatched, result.Status);
        Assert.Equal(130, result.ExitCode);
        Assert.True(screen.Left);
    }

    [Fact]
    public async Task An_unknown_command_name_returns_not_found()
    {
        Fixture fixture = new(EchoManifest());
        AuthenticatedPrincipal principal = await fixture.LoginAsync();
        fixture.Grant("org.hackeros.shell", principal, AppCapabilities.AppsLaunch);
        AppIntentRequest request = new(Guid.NewGuid(), "org.hackeros.shell", principal.UserId.ToString(), new ExecuteCommandIntent("nope"));

        AppIntentDispatchResult result = await fixture.Dispatcher.DispatchAsync(request, principal);

        Assert.Equal(AppIntentDispatchStatus.NotFound, result.Status);
    }

    [Fact]
    public async Task Open_file_intent_resolves_the_sole_candidate_and_launches_it()
    {
        Fixture fixture = new(NotepadManifest());
        AuthenticatedPrincipal principal = await fixture.LoginAsync();
        fixture.Grant("org.hackeros.shell", principal, AppCapabilities.AppsLaunch);
        AppIntentRequest request = new(
            Guid.NewGuid(), "org.hackeros.shell", principal.UserId.ToString(),
            new OpenFileIntent(VirtualPath.Parse("/home/alice/notes.txt"), FileIntentAction.Open));

        AppIntentDispatchResult result = await fixture.Dispatcher.DispatchAsync(request, principal);

        Assert.Equal(AppIntentDispatchStatus.Dispatched, result.Status);
        Assert.NotNull(result.Process);
    }

    [Fact]
    public async Task Open_file_intent_with_multiple_candidates_requires_a_chooser()
    {
        Fixture fixture = new(NotepadManifest(), TextEditorManifest());
        AuthenticatedPrincipal principal = await fixture.LoginAsync();
        fixture.Grant("org.hackeros.shell", principal, AppCapabilities.AppsLaunch);
        AppIntentRequest request = new(
            Guid.NewGuid(), "org.hackeros.shell", principal.UserId.ToString(),
            new OpenFileIntent(VirtualPath.Parse("/home/alice/notes.txt"), FileIntentAction.Open));

        AppIntentDispatchResult result = await fixture.Dispatcher.DispatchAsync(request, principal);

        Assert.Equal(AppIntentDispatchStatus.ChooserRequired, result.Status);
        Assert.Equal(2, result.CandidateAppIds!.Count);
    }

    [Fact]
    public async Task Open_file_intent_for_a_directory_resolves_org_hackeros_file_explorer_as_the_seeded_default_and_launches_it()
    {
        // Phase 5 (INT-006/INT-009): FileAssociationSettingsDocuments.EmptyDocumentContent seeds
        // org.hackeros.file-explorer as the protected default handler for inode/directory, so with the
        // real seeded association document (this fixture always uses CreateDefinition(), never a
        // synthetic one) an unpreferenced directory-open intent resolves as ConfiguredDefault — matching
        // the same end-to-end path FV-009's FileView.ActivateItemAsync(NewWindow) exercises when it calls
        // IAppIntentGateway.OpenFileAsync(path, mediaType: "inode/directory").
        Fixture fixture = new(DirectoryHandlerManifest("org.hackeros.file-explorer"));
        AuthenticatedPrincipal principal = await fixture.LoginAsync();
        fixture.Grant("org.hackeros.shell", principal, AppCapabilities.AppsLaunch);
        AppIntentRequest request = new(
            Guid.NewGuid(), "org.hackeros.shell", principal.UserId.ToString(),
            new OpenFileIntent(VirtualPath.Parse("/home/alice/Documents"), FileIntentAction.Open, MediaType: "inode/directory"));

        AppIntentDispatchResult result = await fixture.Dispatcher.DispatchAsync(request, principal);

        Assert.Equal(AppIntentDispatchStatus.Dispatched, result.Status);
        Assert.NotNull(result.Process);
    }

    // ---- ADR 0096, section 1: RevealFileIntent ----

    [Fact]
    public async Task Reveal_file_requires_apps_launch()
    {
        Fixture fixture = new(DirectoryHandlerManifest("org.hackeros.file-explorer"));
        AuthenticatedPrincipal principal = await fixture.LoginAsync();

        AppIntentDispatchResult result = await fixture.DispatchAsync(principal, new RevealFileIntent(VirtualPath.Parse("/home/alice/Documents")));

        Assert.Equal(AppIntentDispatchStatus.CapabilityDenied, result.Status);
        Assert.Equal("intent.capability-denied", result.ErrorCode);
    }

    [Fact]
    public async Task Reveal_file_opens_the_parent_folder_in_the_folder_handler_with_select()
    {
        Fixture fixture = new(DirectoryHandlerManifest("org.hackeros.file-explorer"));
        AuthenticatedPrincipal principal = await fixture.LoginAsync();
        fixture.Grant("org.hackeros.shell", principal, AppCapabilities.AppsLaunch);

        AppIntentDispatchResult result = await fixture.DispatchAsync(principal, new RevealFileIntent(VirtualPath.Parse("/home/alice/Documents")));

        Assert.Equal(AppIntentDispatchStatus.Dispatched, result.Status);
        Assert.Equal("org.hackeros.file-explorer", result.Process!.AppId);
        Assert.Equal(["/home/alice", "--select=/home/alice/Documents"], Arguments(result));
        Assert.Equal(AppIntentIds.RevealFile, result.Context!.Intent!.IntentId);
        Assert.Equal("org.hackeros.shell", result.Context.Intent.SenderAppId);
    }

    [Fact]
    public async Task Reveal_file_of_a_missing_path_is_not_found()
    {
        Fixture fixture = new(DirectoryHandlerManifest("org.hackeros.file-explorer"));
        AuthenticatedPrincipal principal = await fixture.LoginAsync();
        fixture.Grant("org.hackeros.shell", principal, AppCapabilities.AppsLaunch);

        AppIntentDispatchResult result = await fixture.DispatchAsync(principal, new RevealFileIntent(VirtualPath.Parse("/home/alice/missing.txt")));

        Assert.Equal(AppIntentDispatchStatus.NotFound, result.Status);
        Assert.Equal("intent.reveal-file.not-found", result.ErrorCode);
    }

    [Fact]
    public async Task Reveal_file_in_a_folder_the_user_cannot_read_is_denied()
    {
        Fixture fixture = new(DirectoryHandlerManifest("org.hackeros.file-explorer"));
        AuthenticatedPrincipal principal = await fixture.LoginAsync();
        fixture.Grant("org.hackeros.shell", principal, AppCapabilities.AppsLaunch);
        // Bob's home is 0700: Alice cannot traverse it, like on Linux.
        await fixture.SeedHomeAsync("bob");

        AppIntentDispatchResult result = await fixture.DispatchAsync(principal, new RevealFileIntent(VirtualPath.Parse("/home/bob/Documents")));

        Assert.Equal(AppIntentDispatchStatus.AccessDenied, result.Status);
        Assert.Equal("intent.reveal-file.denied", result.ErrorCode);
    }

    [Fact]
    public async Task Reveal_file_with_several_folder_handlers_and_no_default_requires_a_chooser()
    {
        Fixture fixture = new(DirectoryHandlerManifest("org.test.files-a"), DirectoryHandlerManifest("org.test.files-b"));
        AuthenticatedPrincipal principal = await fixture.LoginAsync();
        fixture.Grant("org.hackeros.shell", principal, AppCapabilities.AppsLaunch);

        AppIntentDispatchResult result = await fixture.DispatchAsync(principal, new RevealFileIntent(VirtualPath.Parse("/home/alice/Documents")));

        Assert.Equal(AppIntentDispatchStatus.ChooserRequired, result.Status);
        Assert.Equal("intent.reveal-file.chooser-required", result.ErrorCode);
        Assert.Equal(["org.test.files-a", "org.test.files-b"], result.CandidateAppIds!.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Reveal_file_without_a_folder_handler_has_no_handler()
    {
        Fixture fixture = new(NotepadManifest());
        AuthenticatedPrincipal principal = await fixture.LoginAsync();
        fixture.Grant("org.hackeros.shell", principal, AppCapabilities.AppsLaunch);

        AppIntentDispatchResult result = await fixture.DispatchAsync(principal, new RevealFileIntent(VirtualPath.Parse("/home/alice/Documents")));

        Assert.Equal(AppIntentDispatchStatus.NotFound, result.Status);
        Assert.Equal("intent.reveal-file.no-handler", result.ErrorCode);
    }

    // ---- ADR 0096, section 2: EditFileIntent ----

    [Fact]
    public async Task Edit_file_requires_apps_launch()
    {
        Fixture fixture = new(NotepadManifest());
        AuthenticatedPrincipal principal = await fixture.LoginAsync();

        AppIntentDispatchResult result = await fixture.DispatchAsync(principal, new EditFileIntent(VirtualPath.Parse("/home/alice/notes.txt")));

        Assert.Equal(AppIntentDispatchStatus.CapabilityDenied, result.Status);
    }

    [Fact]
    public async Task Edit_file_launches_the_edit_handler_with_the_path()
    {
        Fixture fixture = new(NotepadManifest(), ViewerManifest());
        AuthenticatedPrincipal principal = await fixture.LoginAsync();
        fixture.Grant("org.hackeros.shell", principal, AppCapabilities.AppsLaunch);

        AppIntentDispatchResult result = await fixture.DispatchAsync(principal, new EditFileIntent(VirtualPath.Parse("/home/alice/notes.txt")));

        // The viewer only opens .txt: the only edit handler wins without a chooser.
        Assert.Equal(AppIntentDispatchStatus.Dispatched, result.Status);
        Assert.Equal("org.hackeros.notepad", result.Process!.AppId);
        Assert.False(result.FellBackToOpen);
        Assert.Equal(["/home/alice/notes.txt"], Arguments(result));
        Assert.Equal(AppIntentIds.EditFile, result.Context!.Intent!.IntentId);
    }

    [Fact]
    public async Task Edit_file_falls_back_to_open_when_no_app_declares_edit()
    {
        Fixture fixture = new(ViewerManifest());
        AuthenticatedPrincipal principal = await fixture.LoginAsync();
        fixture.Grant("org.hackeros.shell", principal, AppCapabilities.AppsLaunch);

        AppIntentDispatchResult result = await fixture.DispatchAsync(principal, new EditFileIntent(VirtualPath.Parse("/home/alice/notes.txt")));

        Assert.Equal(AppIntentDispatchStatus.Dispatched, result.Status);
        Assert.Equal("org.test.viewer", result.Process!.AppId);
        Assert.True(result.FellBackToOpen);
    }

    [Fact]
    public async Task Edit_file_with_several_edit_handlers_requires_a_chooser()
    {
        Fixture fixture = new(NotepadManifest(), TextEditorManifest());
        AuthenticatedPrincipal principal = await fixture.LoginAsync();
        fixture.Grant("org.hackeros.shell", principal, AppCapabilities.AppsLaunch);

        AppIntentDispatchResult result = await fixture.DispatchAsync(principal, new EditFileIntent(VirtualPath.Parse("/home/alice/notes.txt")));

        Assert.Equal(AppIntentDispatchStatus.ChooserRequired, result.Status);
        Assert.Equal("intent.edit-file.chooser-required", result.ErrorCode);
        Assert.Equal(2, result.CandidateAppIds!.Count);
    }

    [Fact]
    public async Task Edit_file_honors_a_preferred_handler()
    {
        Fixture fixture = new(NotepadManifest(), TextEditorManifest());
        AuthenticatedPrincipal principal = await fixture.LoginAsync();
        fixture.Grant("org.hackeros.shell", principal, AppCapabilities.AppsLaunch);

        AppIntentDispatchResult result = await fixture.DispatchAsync(
            principal, new EditFileIntent(VirtualPath.Parse("/home/alice/notes.txt"), PreferredAppId: "org.hackeros.texteditor"));

        Assert.Equal(AppIntentDispatchStatus.Dispatched, result.Status);
        Assert.Equal("org.hackeros.texteditor", result.Process!.AppId);
    }

    [Fact]
    public async Task Edit_file_with_a_preferred_app_that_cannot_handle_it_is_target_invalid()
    {
        Fixture fixture = new(NotepadManifest(), EchoManifest());
        AuthenticatedPrincipal principal = await fixture.LoginAsync();
        fixture.Grant("org.hackeros.shell", principal, AppCapabilities.AppsLaunch);

        AppIntentDispatchResult result = await fixture.DispatchAsync(
            principal, new EditFileIntent(VirtualPath.Parse("/home/alice/notes.txt"), PreferredAppId: "org.hackeros.echo"));

        Assert.Equal(AppIntentDispatchStatus.TargetInvalid, result.Status);
        Assert.Equal("intent.edit-file.target-invalid", result.ErrorCode);
    }

    [Fact]
    public async Task Edit_file_without_any_handler_has_no_handler()
    {
        Fixture fixture = new(EchoManifest());
        AuthenticatedPrincipal principal = await fixture.LoginAsync();
        fixture.Grant("org.hackeros.shell", principal, AppCapabilities.AppsLaunch);

        AppIntentDispatchResult result = await fixture.DispatchAsync(principal, new EditFileIntent(VirtualPath.Parse("/home/alice/notes.txt")));

        Assert.Equal(AppIntentDispatchStatus.NotFound, result.Status);
        Assert.Equal("intent.edit-file.no-handler", result.ErrorCode);
    }

    // ---- ADR 0096, section 3: OpenUriIntent ----

    [Fact]
    public async Task Open_uri_requires_apps_launch()
    {
        Fixture fixture = new(BrowserManifest());
        AuthenticatedPrincipal principal = await fixture.LoginAsync();

        AppIntentDispatchResult result = await fixture.DispatchAsync(principal, new OpenUriIntent(new Uri("https://example.hackeros/")));

        Assert.Equal(AppIntentDispatchStatus.CapabilityDenied, result.Status);
    }

    [Fact]
    public async Task Open_uri_launches_the_default_scheme_handler_with_the_uri()
    {
        // The seeded file-associations.json names the browser for x-scheme-handler/http and https.
        Fixture fixture = new(BrowserManifest(), SchemeHandlerManifest("org.test.other-browser", "https"));
        AuthenticatedPrincipal principal = await fixture.LoginAsync();
        fixture.Grant("org.hackeros.shell", principal, AppCapabilities.AppsLaunch);

        AppIntentDispatchResult result = await fixture.DispatchAsync(principal, new OpenUriIntent(new Uri("https://example.hackeros/page?q=1")));

        Assert.Equal(AppIntentDispatchStatus.Dispatched, result.Status);
        Assert.Equal("org.hackeros.browser", result.Process!.AppId);
        Assert.Equal(["https://example.hackeros/page?q=1"], Arguments(result));
        Assert.Equal(AppIntentIds.OpenUri, result.Context!.Intent!.IntentId);
    }

    [Fact]
    public async Task Open_uri_and_open_file_use_the_acting_users_own_default()
    {
        // ADR 0121: ~/.config/hackeros/file-associations.json comes before the machine's file.
        Fixture fixture = new(BrowserManifest(), SchemeHandlerManifest("org.test.other-browser", "https"), NotepadManifest());
        AuthenticatedPrincipal principal = await fixture.LoginAsync();
        fixture.Grant("org.hackeros.shell", principal, AppCapabilities.AppsLaunch);
        Assert.Equal(UserDefaultHandlerChangeStatus.Changed,
            await fixture.Associations.SetUserDefaultAsync(principal, "x-scheme-handler/https", "open", "org.test.other-browser"));

        AppIntentDispatchResult uri = await fixture.DispatchAsync(principal, new OpenUriIntent(new Uri("https://example.hackeros/")));
        AppIntentDispatchResult http = await fixture.DispatchAsync(principal, new OpenUriIntent(new Uri("http://example.hackeros/")));

        Assert.Equal("org.test.other-browser", uri.Process!.AppId);
        Assert.Equal("org.hackeros.browser", http.Process!.AppId);
    }

    [Fact]
    public async Task Open_uri_honors_a_preferred_handler()
    {
        Fixture fixture = new(BrowserManifest(), SchemeHandlerManifest("org.test.other-browser", "https"));
        AuthenticatedPrincipal principal = await fixture.LoginAsync();
        fixture.Grant("org.hackeros.shell", principal, AppCapabilities.AppsLaunch);

        AppIntentDispatchResult result = await fixture.DispatchAsync(
            principal, new OpenUriIntent(new Uri("https://example.hackeros/"), PreferredAppId: "org.test.other-browser"));

        Assert.Equal(AppIntentDispatchStatus.Dispatched, result.Status);
        Assert.Equal("org.test.other-browser", result.Process!.AppId);
    }

    [Fact]
    public async Task Open_uri_with_a_preferred_app_that_does_not_handle_the_scheme_is_target_invalid()
    {
        Fixture fixture = new(BrowserManifest(), NotepadManifest());
        AuthenticatedPrincipal principal = await fixture.LoginAsync();
        fixture.Grant("org.hackeros.shell", principal, AppCapabilities.AppsLaunch);

        AppIntentDispatchResult result = await fixture.DispatchAsync(
            principal, new OpenUriIntent(new Uri("https://example.hackeros/"), PreferredAppId: "org.hackeros.notepad"));

        Assert.Equal(AppIntentDispatchStatus.TargetInvalid, result.Status);
        Assert.Equal("intent.open-uri.target-invalid", result.ErrorCode);
    }

    [Fact]
    public async Task Open_uri_with_several_scheme_handlers_and_no_default_requires_a_chooser()
    {
        Fixture fixture = new(SchemeHandlerManifest("org.test.gopher-a", "gopher"), SchemeHandlerManifest("org.test.gopher-b", "gopher"));
        AuthenticatedPrincipal principal = await fixture.LoginAsync();
        fixture.Grant("org.hackeros.shell", principal, AppCapabilities.AppsLaunch);

        AppIntentDispatchResult result = await fixture.DispatchAsync(principal, new OpenUriIntent(new Uri("gopher://hole.hackeros/")));

        Assert.Equal(AppIntentDispatchStatus.ChooserRequired, result.Status);
        Assert.Equal("intent.open-uri.chooser-required", result.ErrorCode);
        Assert.Equal(2, result.CandidateAppIds!.Count);
    }

    [Fact]
    public async Task Open_uri_of_a_scheme_nobody_handles_has_no_handler()
    {
        Fixture fixture = new(BrowserManifest());
        AuthenticatedPrincipal principal = await fixture.LoginAsync();
        fixture.Grant("org.hackeros.shell", principal, AppCapabilities.AppsLaunch);

        AppIntentDispatchResult result = await fixture.DispatchAsync(principal, new OpenUriIntent(new Uri("nope://thing")));

        Assert.Equal(AppIntentDispatchStatus.NotFound, result.Status);
        Assert.Equal("intent.open-uri.no-handler", result.ErrorCode);
    }

    [Fact]
    public async Task Open_uri_of_a_relative_uri_is_invalid()
    {
        Fixture fixture = new(BrowserManifest());
        AuthenticatedPrincipal principal = await fixture.LoginAsync();
        fixture.Grant("org.hackeros.shell", principal, AppCapabilities.AppsLaunch);

        AppIntentDispatchResult result = await fixture.DispatchAsync(principal, new OpenUriIntent(new Uri("docs/page.html", UriKind.Relative)));

        Assert.Equal(AppIntentDispatchStatus.Invalid, result.Status);
        Assert.Equal("intent.open-uri.invalid", result.ErrorCode);
    }

    [Fact]
    public async Task Open_uri_of_a_file_uri_opens_the_file()
    {
        Fixture fixture = new(NotepadManifest(), BrowserManifest());
        AuthenticatedPrincipal principal = await fixture.LoginAsync();
        fixture.Grant("org.hackeros.shell", principal, AppCapabilities.AppsLaunch);

        AppIntentDispatchResult result = await fixture.DispatchAsync(principal, new OpenUriIntent(new Uri("file:///home/alice/my%20notes.txt")));

        Assert.Equal(AppIntentDispatchStatus.Dispatched, result.Status);
        Assert.Equal("org.hackeros.notepad", result.Process!.AppId);
        Assert.Equal(["/home/alice/my notes.txt"], Arguments(result));
    }

    // ---- ADR 0096, section 4: ShowAppSettingsIntent ----

    [Fact]
    public async Task Show_settings_opens_the_settings_app_on_the_app_page_without_any_capability()
    {
        Fixture fixture = new(NotepadManifest(), SettingsManifest());
        AuthenticatedPrincipal principal = await fixture.LoginAsync();

        AppIntentDispatchResult result = await fixture.DispatchAsync(principal, new ShowAppSettingsIntent("org.hackeros.notepad"));

        Assert.Equal(AppIntentDispatchStatus.Dispatched, result.Status);
        Assert.Equal(AppIntentDispatcher.SettingsAppId, result.Process!.AppId);
        Assert.Equal(["--app=org.hackeros.notepad"], Arguments(result));
    }

    [Fact]
    public async Task Show_settings_launches_an_app_that_declares_its_own_settings_page()
    {
        AppManifest notepad = NotepadManifest() with { Intents = [new AppIntentDeclarationManifest(AppIntentIds.ShowAppSettings)] };
        Fixture fixture = new(notepad, SettingsManifest());
        AuthenticatedPrincipal principal = await fixture.LoginAsync();

        AppIntentDispatchResult result = await fixture.DispatchAsync(principal, new ShowAppSettingsIntent("org.hackeros.notepad"));

        Assert.Equal(AppIntentDispatchStatus.Dispatched, result.Status);
        Assert.Equal("org.hackeros.notepad", result.Process!.AppId);
        Assert.Equal(["--show-settings"], Arguments(result));
    }

    [Fact]
    public async Task Show_settings_of_an_unknown_app_is_not_found()
    {
        Fixture fixture = new(SettingsManifest());
        AuthenticatedPrincipal principal = await fixture.LoginAsync();

        AppIntentDispatchResult result = await fixture.DispatchAsync(principal, new ShowAppSettingsIntent("org.nope.app"));

        Assert.Equal(AppIntentDispatchStatus.NotFound, result.Status);
        Assert.Equal("intent.show-settings.not-found", result.ErrorCode);
    }

    [Fact]
    public async Task Show_settings_of_a_disabled_app_is_refused()
    {
        Fixture fixture = new(NotepadManifest(), SettingsManifest());
        fixture.Enablement.MarkDisabled(["org.hackeros.notepad"]);
        AuthenticatedPrincipal principal = await fixture.LoginAsync();

        AppIntentDispatchResult result = await fixture.DispatchAsync(principal, new ShowAppSettingsIntent("org.hackeros.notepad"));

        Assert.Equal(AppIntentDispatchStatus.Disabled, result.Status);
        Assert.Equal("intent.show-settings.disabled", result.ErrorCode);
    }

    [Fact]
    public async Task Show_settings_without_the_settings_app_reports_it()
    {
        Fixture fixture = new(NotepadManifest());
        AuthenticatedPrincipal principal = await fixture.LoginAsync();

        AppIntentDispatchResult result = await fixture.DispatchAsync(principal, new ShowAppSettingsIntent("org.hackeros.notepad"));

        Assert.Equal(AppIntentDispatchStatus.NotFound, result.Status);
        Assert.Equal("intent.show-settings.no-settings-app", result.ErrorCode);
    }

    // ---- ADR 0096, section 5: app-defined intents ----

    private const string ShareIntent = "org.test.intent.share.v1";

    [Fact]
    public async Task Custom_intent_with_a_malformed_id_is_invalid()
    {
        Fixture fixture = new(ReceiverManifest("org.test.receiver-a"));
        AuthenticatedPrincipal principal = await fixture.LoginAsync();
        fixture.Grant("org.hackeros.shell", principal, AppCapabilities.AppsLaunch);

        AppIntentDispatchResult result = await fixture.DispatchAsync(principal, new CustomIntent("Share"));

        Assert.Equal(AppIntentDispatchStatus.Invalid, result.Status);
        Assert.Equal("intent.custom.invalid-id", result.ErrorCode);
    }

    [Fact]
    public async Task Custom_intent_cannot_carry_a_built_in_id()
    {
        Fixture fixture = new(ReceiverManifest("org.test.receiver-a"));
        AuthenticatedPrincipal principal = await fixture.LoginAsync();
        fixture.Grant("org.hackeros.shell", principal, AppCapabilities.AppsLaunch);

        AppIntentDispatchResult result = await fixture.DispatchAsync(principal, new CustomIntent(AppIntentIds.OpenFile));

        Assert.Equal(AppIntentDispatchStatus.Invalid, result.Status);
        Assert.Equal("intent.custom.reserved-id", result.ErrorCode);
    }

    [Fact]
    public async Task Custom_intent_requires_apps_launch()
    {
        Fixture fixture = new(ReceiverManifest("org.test.receiver-a"));
        AuthenticatedPrincipal principal = await fixture.LoginAsync();

        AppIntentDispatchResult result = await fixture.DispatchAsync(principal, new CustomIntent(ShareIntent));

        Assert.Equal(AppIntentDispatchStatus.CapabilityDenied, result.Status);
        Assert.Equal("intent.capability-denied", result.ErrorCode);
    }

    [Theory]
    [InlineData("[1, 2]")]
    [InlineData("\"text\"")]
    [InlineData("{ not json")]
    public async Task Custom_intent_payload_must_be_a_json_object(string payload)
    {
        Fixture fixture = new(ReceiverManifest("org.test.receiver-a"));
        AuthenticatedPrincipal principal = await fixture.LoginAsync();
        fixture.Grant("org.hackeros.shell", principal, AppCapabilities.AppsLaunch);

        AppIntentDispatchResult result = await fixture.DispatchAsync(principal, new CustomIntent(ShareIntent, payload));

        Assert.Equal(AppIntentDispatchStatus.Invalid, result.Status);
        Assert.Equal("intent.custom.payload-invalid", result.ErrorCode);
    }

    [Fact]
    public async Task Custom_intent_payload_is_limited_to_64_KiB()
    {
        Fixture fixture = new(ReceiverManifest("org.test.receiver-a"));
        AuthenticatedPrincipal principal = await fixture.LoginAsync();
        fixture.Grant("org.hackeros.shell", principal, AppCapabilities.AppsLaunch);
        string payload = "{\"text\":\"" + new string('x', AppIntentIds.MaxCustomPayloadBytes) + "\"}";

        AppIntentDispatchResult result = await fixture.DispatchAsync(principal, new CustomIntent(ShareIntent, payload));

        Assert.Equal(AppIntentDispatchStatus.Invalid, result.Status);
        Assert.Equal("intent.custom.payload-too-large", result.ErrorCode);
    }

    [Fact]
    public async Task Custom_intent_nobody_declares_is_unknown()
    {
        Fixture fixture = new(ReceiverManifest("org.test.receiver-a"));
        AuthenticatedPrincipal principal = await fixture.LoginAsync();
        fixture.Grant("org.hackeros.shell", principal, AppCapabilities.AppsLaunch);

        AppIntentDispatchResult result = await fixture.DispatchAsync(principal, new CustomIntent("org.test.intent.nobody.v1"));

        Assert.Equal(AppIntentDispatchStatus.NotFound, result.Status);
        Assert.Equal("intent.unknown", result.ErrorCode);
    }

    [Fact]
    public async Task Custom_intent_declared_by_one_app_launches_it_with_the_payload()
    {
        Fixture fixture = new(ReceiverManifest("org.test.receiver-a"));
        AuthenticatedPrincipal principal = await fixture.LoginAsync();
        fixture.Grant("org.hackeros.shell", principal, AppCapabilities.AppsLaunch);

        AppIntentDispatchResult result = await fixture.DispatchAsync(principal, new CustomIntent(ShareIntent, "{\"text\":\"hi\"}"));

        Assert.Equal(AppIntentDispatchStatus.Dispatched, result.Status);
        Assert.Equal("org.test.receiver-a", result.Process!.AppId);
        AppIntentDelivery delivery = result.Context!.Intent!;
        Assert.Equal(ShareIntent, delivery.IntentId);
        Assert.Equal("{\"text\":\"hi\"}", delivery.PayloadJson);
        Assert.Equal("org.hackeros.shell", delivery.SenderAppId);
    }

    [Fact]
    public async Task Custom_intent_of_a_disabled_app_is_not_routed_to_it()
    {
        Fixture fixture = new(ReceiverManifest("org.test.receiver-a"));
        fixture.Enablement.MarkDisabled(["org.test.receiver-a"]);
        AuthenticatedPrincipal principal = await fixture.LoginAsync();
        fixture.Grant("org.hackeros.shell", principal, AppCapabilities.AppsLaunch);

        AppIntentDispatchResult result = await fixture.DispatchAsync(principal, new CustomIntent(ShareIntent));

        Assert.Equal("intent.unknown", result.ErrorCode);
    }

    [Fact]
    public async Task Custom_intent_declared_by_several_apps_requires_a_chooser()
    {
        Fixture fixture = new(ReceiverManifest("org.test.receiver-b"), ReceiverManifest("org.test.receiver-a"));
        AuthenticatedPrincipal principal = await fixture.LoginAsync();
        fixture.Grant("org.hackeros.shell", principal, AppCapabilities.AppsLaunch);

        AppIntentDispatchResult result = await fixture.DispatchAsync(principal, new CustomIntent(ShareIntent));

        Assert.Equal(AppIntentDispatchStatus.ChooserRequired, result.Status);
        Assert.Equal("intent.custom.chooser-required", result.ErrorCode);
        Assert.Equal(["org.test.receiver-a", "org.test.receiver-b"], result.CandidateAppIds);
    }

    [Fact]
    public async Task Custom_intent_to_an_app_that_does_not_declare_it_is_target_invalid()
    {
        Fixture fixture = new(ReceiverManifest("org.test.receiver-a"), NotepadManifest());
        AuthenticatedPrincipal principal = await fixture.LoginAsync();
        fixture.Grant("org.hackeros.shell", principal, AppCapabilities.AppsLaunch);

        AppIntentDispatchResult result = await fixture.DispatchAsync(principal, new CustomIntent(ShareIntent, TargetAppId: "org.hackeros.notepad"));

        Assert.Equal(AppIntentDispatchStatus.TargetInvalid, result.Status);
        Assert.Equal("intent.custom.target-invalid", result.ErrorCode);
    }

    [Fact]
    public async Task Custom_intent_always_use_is_remembered_in_intents_config()
    {
        Fixture fixture = new(ReceiverManifest("org.test.receiver-a"), ReceiverManifest("org.test.receiver-b"));
        AuthenticatedPrincipal principal = await fixture.LoginAsync();
        fixture.Grant("org.hackeros.shell", principal, AppCapabilities.AppsLaunch);

        AppIntentDispatchResult chosen = await fixture.DispatchAsync(
            principal, new CustomIntent(ShareIntent, TargetAppId: "org.test.receiver-b", RememberChoice: true));
        AppIntentDispatchResult next = await fixture.DispatchAsync(principal, new CustomIntent(ShareIntent));

        Assert.Equal(AppIntentDispatchStatus.Dispatched, chosen.Status);
        Assert.Equal(AppIntentDispatchStatus.Dispatched, next.Status);
        Assert.Equal("org.test.receiver-b", next.Process!.AppId);
        Assert.Equal("org.test.receiver-b", await IntentChoiceStore.ReadAsync(fixture.FileSystem, principal, ShareIntent, CancellationToken.None));
    }

    [Fact]
    public async Task Custom_intent_to_a_running_single_instance_is_delivered_through_intent_received()
    {
        Fixture fixture = new(ReceiverServiceManifest());
        AuthenticatedPrincipal principal = await fixture.LoginAsync();
        fixture.Grant("org.hackeros.shell", principal, AppCapabilities.AppsLaunch);
        AppIntentDispatchResult first = await fixture.DispatchAsync(principal, new CustomIntent(ShareIntent, "{\"n\":1}"));
        List<AppIntentDelivery> received = [];
        first.Context!.IntentReceived += (_, delivery) => received.Add(delivery);

        AppIntentDispatchResult second = await fixture.DispatchAsync(principal, new CustomIntent(ShareIntent, "{\"n\":2}"));

        Assert.Equal(AppIntentDispatchStatus.Dispatched, second.Status);
        Assert.Equal(first.Process!.Pid, second.Process!.Pid);
        AppIntentDelivery delivery = Assert.Single(received);
        Assert.Equal("{\"n\":2}", delivery.PayloadJson);
        await fixture.Orchestrator.StopAllAsync(ProcessExitReason.Shutdown);
    }

    // ---- ADR 0096: the app-facing gateway's outcomes ----

    [Fact]
    public async Task The_gateway_maps_each_new_intent_outcome()
    {
        Fixture fixture = new(DirectoryHandlerManifest("org.hackeros.file-explorer"), BrowserManifest(),
            ReceiverManifest("org.test.receiver-a"), ReceiverManifest("org.test.receiver-b"));
        AuthenticatedPrincipal principal = await fixture.LoginAsync();
        fixture.Grant("org.hackeros.shell", principal, AppCapabilities.AppsLaunch);
        await fixture.SeedHomeAsync("bob");
        IAppIntentGateway gateway = fixture.Gateway(principal);

        Assert.Equal(AppIntentOutcome.Dispatched, (await gateway.RevealFileAsync(VirtualPath.Parse("/home/alice/Documents"))).Outcome);
        Assert.Equal(AppIntentOutcome.NotFound, (await gateway.RevealFileAsync(VirtualPath.Parse("/home/alice/nope"))).Outcome);
        Assert.Equal(AppIntentOutcome.AccessDenied, (await gateway.RevealFileAsync(VirtualPath.Parse("/home/bob/Documents"))).Outcome);
        Assert.Equal(AppIntentOutcome.Dispatched, (await gateway.OpenUriAsync(new Uri("https://example.hackeros/"))).Outcome);
        Assert.Equal(AppIntentOutcome.NoHandler, (await gateway.OpenUriAsync(new Uri("nope://x"))).Outcome);
        Assert.Equal(AppIntentOutcome.NotFound, (await gateway.ShowAppSettingsAsync("org.nope.app")).Outcome);
        Assert.Equal(AppIntentOutcome.NoHandler, (await gateway.SendIntentAsync("org.test.intent.nobody.v1", null)).Outcome);
        Assert.Equal(AppIntentOutcome.Invalid, (await gateway.SendIntentAsync(ShareIntent, "[]")).Outcome);
        AppIntentResult chooser = await gateway.SendIntentAsync(ShareIntent, null);
        Assert.Equal(AppIntentOutcome.ChooserRequired, chooser.Outcome);
        Assert.Equal(["org.test.receiver-a", "org.test.receiver-b"], chooser.CandidateAppIds);
    }

    [Fact]
    public async Task The_gateway_throws_when_the_caller_lacks_apps_launch()
    {
        Fixture fixture = new(BrowserManifest(), NotepadManifest());
        AuthenticatedPrincipal principal = await fixture.LoginAsync();
        IAppIntentGateway gateway = fixture.Gateway(principal);

        await Assert.ThrowsAsync<AppGatewayAccessDeniedException>(() => gateway.OpenUriAsync(new Uri("https://example.hackeros/")).AsTask());
        await Assert.ThrowsAsync<AppGatewayAccessDeniedException>(() => gateway.EditFileAsync(VirtualPath.Parse("/home/alice/notes.txt")).AsTask());
    }

    [Fact]
    public async Task The_gateway_reports_an_edit_that_fell_back_to_open()
    {
        Fixture fixture = new(ViewerManifest());
        AuthenticatedPrincipal principal = await fixture.LoginAsync();
        fixture.Grant("org.hackeros.shell", principal, AppCapabilities.AppsLaunch);

        AppIntentOpenFileResult result = await fixture.Gateway(principal).EditFileAsync(VirtualPath.Parse("/home/alice/notes.txt"));

        Assert.Equal(AppIntentOpenFileOutcome.Opened, result.Outcome);
        Assert.True(result.FellBackToOpen);
    }

    private static string[] Arguments(AppIntentDispatchResult result) =>
        [.. result.Context!.Environment.GetCommandLineArgs().Skip(1)];

    private static AppManifest ViewerManifest() => WindowManifest("org.test.viewer") with
    {
        FileHandlers = [new FileHandlerManifest("text/plain", [".txt"], ["open"])]
    };

    private static AppManifest BrowserManifest() => WindowManifest("org.hackeros.browser") with
    {
        FileHandlers =
        [
            new FileHandlerManifest("x-scheme-handler/http", [], ["open"]),
            new FileHandlerManifest("x-scheme-handler/https", [], ["open"])
        ]
    };

    private static AppManifest SchemeHandlerManifest(string appId, string scheme) => WindowManifest(appId) with
    {
        FileHandlers = [new FileHandlerManifest($"x-scheme-handler/{scheme}", [], ["open"])]
    };

    private static AppManifest SettingsManifest() => WindowManifest(AppIntentDispatcher.SettingsAppId);

    private static AppManifest ReceiverManifest(string appId) => WindowManifest(appId) with
    {
        Intents = [new AppIntentDeclarationManifest(ShareIntent)]
    };

    private static AppManifest ReceiverServiceManifest() => WaiterServiceManifest() with
    {
        Intents = [new AppIntentDeclarationManifest(ShareIntent)]
    };

    private static AppManifest WindowManifest(string appId) => new()
    {
        Id = appId,
        Name = appId,
        Version = "1.0.0",
        PublisherId = "org.hackeros",
        Description = "A Window app for ADR 0096 tests.",
        Kind = AppKind.Window,
        EntryPoint = new AppEntryPointManifest("Kazo.Os.Platform.Core.Tests", $"{appId}.EntryPoint"),
        SdkCompatibility = new AppSdkCompatibilityManifest(KazoOsSdk.Version),
        Presentation = new PresentationManifest("test", AppLaunchVisibility.Visible, []),
        Resources = AppResourceProfileManifest.None
    };

    private static AppManifest EchoManifest() => new()
    {
        Id = "org.hackeros.echo",
        Name = "Echo",
        Version = "1.0.0",
        PublisherId = "org.hackeros",
        Description = "Echoes its arguments.",
        Kind = AppKind.Terminal,
        EntryPoint = new AppEntryPointManifest("Kazo.Os.Platform.Core.Tests", typeof(EchoTerminalApp).FullName!),
        SdkCompatibility = new AppSdkCompatibilityManifest(KazoOsSdk.Version),
        Presentation = new PresentationManifest("test", AppLaunchVisibility.Visible, []),
        Resources = AppResourceProfileManifest.None,
        Terminal = new TerminalCommandManifest("echo", ["say"], "echo [text]")
    };

    private static AppManifest NotepadManifest() => new()
    {
        Id = "org.hackeros.notepad",
        Name = "Notepad",
        Version = "1.0.0",
        PublisherId = "org.hackeros",
        Description = "A simple text editor.",
        Kind = AppKind.Window,
        EntryPoint = new AppEntryPointManifest("Kazo.Os.Platform.Core.Tests", "org.hackeros.notepad.EntryPoint"),
        SdkCompatibility = new AppSdkCompatibilityManifest(KazoOsSdk.Version),
        Presentation = new PresentationManifest("test", AppLaunchVisibility.Visible, []),
        Resources = AppResourceProfileManifest.None,
        FileHandlers = [new FileHandlerManifest("text/plain", [".txt"], ["open", "edit"])]
    };

    private static AppManifest DirectoryHandlerManifest(string appId) => new()
    {
        Id = appId,
        Name = appId,
        Version = "1.0.0",
        PublisherId = "org.hackeros",
        Description = "A directory-capable Window app for Phase 5 tests.",
        Kind = AppKind.Window,
        EntryPoint = new AppEntryPointManifest("Kazo.Os.Platform.Core.Tests", $"{appId}.EntryPoint"),
        SdkCompatibility = new AppSdkCompatibilityManifest(KazoOsSdk.Version),
        Presentation = new PresentationManifest("test", AppLaunchVisibility.Visible, []),
        Resources = AppResourceProfileManifest.None,
        FileHandlers = [new FileHandlerManifest("inode/directory", [], ["open"])]
    };

    private static AppManifest FullScreenManifest() => new()
    {
        Id = "org.hackeros.screen",
        Name = "Screen",
        Version = "1.0.0",
        PublisherId = "org.hackeros",
        Description = "Exercises full-screen command dispatch.",
        Kind = AppKind.Terminal,
        EntryPoint = new AppEntryPointManifest(
            "Kazo.Os.Platform.Core.Tests", typeof(FullScreenTerminalApp).FullName!),
        SdkCompatibility = new AppSdkCompatibilityManifest(KazoOsSdk.Version),
        Presentation = new PresentationManifest("test", AppLaunchVisibility.Hidden, []),
        Resources = AppResourceProfileManifest.None,
        Terminal = new TerminalCommandManifest("screen", [], "screen [wait]")
    };

    private static AppManifest TextEditorManifest() => new()
    {
        Id = "org.hackeros.texteditor",
        Name = "Text Editor",
        Version = "1.0.0",
        PublisherId = "org.hackeros",
        Description = "An advanced text editor.",
        Kind = AppKind.Window,
        EntryPoint = new AppEntryPointManifest("Kazo.Os.Platform.Core.Tests", "org.hackeros.texteditor.EntryPoint"),
        SdkCompatibility = new AppSdkCompatibilityManifest(KazoOsSdk.Version),
        Presentation = new PresentationManifest("test", AppLaunchVisibility.Visible, []),
        Resources = AppResourceProfileManifest.None,
        FileHandlers = [new FileHandlerManifest("text/plain", [".txt"], ["open", "edit"])]
    };

    private static AppManifest WaiterServiceManifest() => new()
    {
        Id = "org.hackeros.waiter",
        Name = "Waiter",
        Version = "1.0.0",
        PublisherId = "org.hackeros",
        Description = "Waits for cancellation.",
        Kind = AppKind.Service,
        EntryPoint = new AppEntryPointManifest("Kazo.Os.Platform.Core.Tests", typeof(WaiterServiceApp).FullName!),
        SdkCompatibility = new AppSdkCompatibilityManifest(KazoOsSdk.Version),
        Presentation = new PresentationManifest("test", AppLaunchVisibility.Visible, []),
        Resources = AppResourceProfileManifest.None
    };

    private sealed class WaiterServiceApp(AppManifest manifest) : ServiceAppBase(manifest)
    {
        protected override Task RunCoreAsync(IAppExecutionContext context, CancellationToken sessionCancellationToken) =>
            Task.Delay(Timeout.InfiniteTimeSpan, sessionCancellationToken);
    }

    private sealed class EchoTerminalApp(AppManifest manifest) : TerminalAppBase(manifest)
    {
        public override async ValueTask<int> ExecuteAsync(TerminalExecutionContext context, CancellationToken cancellationToken)
        {
            await context.StandardOutput.WriteLineAsync(string.Join(' ', context.Arguments).AsMemory(), cancellationToken);
            return 0;
        }
    }

    private sealed class FullScreenTerminalApp(AppManifest manifest) : TerminalAppBase(manifest)
    {
        public override async ValueTask<int> ExecuteAsync(
            TerminalExecutionContext context, CancellationToken cancellationToken)
        {
            IFullScreenTerminalSession screen = context.FullScreen
                ?? throw new InvalidOperationException("A full-screen session is required.");
            await screen.EnterAlternateScreenAsync(CancellationToken.None);
            try
            {
                await screen.RenderAsync(
                    new TerminalScreenFrame(["screen"], new TerminalCursor(0, 0)),
                    cancellationToken);
                if (context.Arguments.Contains("wait", StringComparer.Ordinal))
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    return 0;
                }

                TerminalKeyEvent key = await screen.ReadKeyAsync(cancellationToken);
                await context.StandardOutput.WriteLineAsync(key.Text);
                return 0;
            }
            finally
            {
                await screen.LeaveAlternateScreenAsync(CancellationToken.None);
            }
        }
    }

    private sealed class TestFullScreenTerminal(params TerminalKeyEvent[] keys) : IFullScreenTerminalSession
    {
        private readonly Queue<TerminalKeyEvent> _keys = new(keys);
        public TerminalViewport Viewport { get; } = TerminalViewport.Create(80, 24);
        public bool Entered { get; private set; }
        public bool Left { get; private set; }
        public TerminalScreenFrame? Frame { get; private set; }
        public TaskCompletionSource EnteredSignal { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask EnterAlternateScreenAsync(CancellationToken cancellationToken = default)
        {
            Entered = true;
            EnteredSignal.TrySetResult();
            return ValueTask.CompletedTask;
        }

        public ValueTask RenderAsync(
            TerminalScreenFrame frame, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Frame = frame;
            return ValueTask.CompletedTask;
        }

        public ValueTask<TerminalKeyEvent> ReadKeyAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(_keys.Dequeue());
        }

        public ValueTask LeaveAlternateScreenAsync(CancellationToken cancellationToken = default)
        {
            Left = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class Fixture
    {
        private int _entryId = 1;
        private int _transactionId = 100;
        private readonly DateTimeOffset _now = new(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);
        private readonly LocalLoginName _aliceLoginName;

        internal Fixture(params AppManifest[] manifests)
            : this([], manifests)
        {
        }

        /// <summary>A kernel whose host declared <paramref name="systemAppIds"/> system apps (ADR 0048).</summary>
        internal Fixture(IReadOnlyCollection<string> systemAppIds, params AppManifest[] manifests)
        {
            FixedTimeProvider timeProvider = new(_now);
            InMemoryFileSystemRepository repository = new(
                () => FileSystemEntryId.FromGuid(new Guid(_entryId++, 0, 0, new byte[8])),
                () => new Guid(_transactionId++, 0, 0, new byte[8]),
                timeProvider);
            FileSystemMountRouter router = new(repository);
            Grants = new CapabilityGrantRepository(() => _now);
            InMemoryTopicMessageBus topicBus = new(Grants);
            FileSystemService fileSystem = new(
                router, new FileSystemPathResolver(router), new FileSystemAuthorizer(), topicBus,
                () => new Guid(_transactionId++, 0, 0, new byte[8]));
            FileSystemSeeder seeder = new(fileSystem, timeProvider);
            FileSystem = fileSystem;
            _seeder = seeder;

            InMemoryLocalUserRepository users = new(() => _now);
            InMemoryLocalGroupRepository groups = new();
            InMemoryEventBus eventBus = new();
            BoundedAuditLog auditLog = new(maxEntries: 100);

            LocalGroup group = groups.CreateGroup(LocalLoginName.Parse("users"));
            _aliceLoginName = LocalLoginName.Parse("alice");
            users.CreateUser(
                _aliceLoginName, "Alice", AppAuthority.User, group.Id,
                credential: LocalPasswordHasher.Create("hunter2", iterations: 100));
            users.CreateUser(
                LocalLoginName.Parse("root"), "Root", AppAuthority.Administrator, group.Id,
                credential: LocalPasswordHasher.Create("hunter2", iterations: 100));

            Session = new LocalSessionService(
                users, seeder, eventBus, auditLog,
                InstallationId.FromGuid(Guid.NewGuid()), DeviceId.FromGuid(Guid.NewGuid()), () => _now);

            ManualSimulationClock clock = new(_now, TimeSpan.FromSeconds(1));
            Manager = new InMemoryProcessManager(clock, Session, eventBus);
            InMemoryNotificationQueue notifications = new(maxEntriesPerUser: 20);
            BoundedDiagnosticSink diagnostics = new(maxEntries: 100);
            Settings = new OsSettingsService(fileSystem, Session, clock);
            AppExecutionContextFactory contextFactory = new(
                Grants, fileSystem, eventBus, topicBus, notifications, diagnostics, clock, Manager);

            AppCatalogBuildResult catalogResult = AppCatalog.Build(manifests, systemAppIds);
            Assert.True(catalogResult.IsSuccess, string.Join(", ", catalogResult.Errors.Select(e => e.Message)));
            AppCatalog catalog = catalogResult.Catalog!;

            Dictionary<string, System.Reflection.Assembly> hostAssemblies = new(StringComparer.Ordinal)
            {
                ["Kazo.Os.Platform.Core.Tests"] = typeof(Fixture).Assembly
            };

            // Window apps in these tests always use a placeholder entry point (this project has no
            // reference to the Blazor App SDK, so no real WindowAppBase-derived type can ever exist
            // here) -- discover only the non-Window manifests so an intentionally-unresolvable
            // Window entry never poisons AppDiscoveryResult.Descriptors (all-or-nothing per catalog)
            // for the manifests that ARE meant to resolve for real.
            AppCatalogBuildResult discoveryCatalogResult = AppCatalog.Build(manifests.Where(m => m.Kind != AppKind.Window));
            AppDiscoveryResult discovery = AppEntryPointDiscovery.Discover(discoveryCatalogResult.Catalog!, hostAssemblies);

            Dictionary<string, AppDescriptor> descriptors = new(StringComparer.Ordinal);
            foreach (AppManifest manifest in manifests)
            {
                descriptors[manifest.Id] = manifest.Kind == AppKind.Window
                    ? new AppDescriptor(manifest, typeof(object), typeof(object).Assembly)
                    : discovery.Descriptors![manifest.Id];
            }

            AppEnablementRegistry enablement = new(catalog);
            Enablement = enablement;
            Orchestrator = new AppLifecycleOrchestrator(catalog, descriptors, enablement, Manager, Grants, contextFactory);
            FileAssociationResolver resolver = new(catalog, enablement, Settings, fileSystem);
            Associations = resolver;
            Dispatcher = new AppIntentDispatcher(Orchestrator, catalog, enablement, resolver, Grants, fileSystem);
        }

        private readonly FileSystemSeeder _seeder;

        internal LocalSessionService Session { get; }
        internal FileSystemService FileSystem { get; }
        internal AppEnablementRegistry Enablement { get; }
        internal InMemoryProcessManager Manager { get; }
        internal CapabilityGrantRepository Grants { get; }
        internal IOsSettingsService Settings { get; }
        internal AppLifecycleOrchestrator Orchestrator { get; }
        internal AppIntentDispatcher Dispatcher { get; }

        /// <summary>The resolver the dispatcher uses, to set a user's defaults (ADR 0121).</summary>
        internal FileAssociationResolver Associations { get; }

        internal Task<AuthenticatedPrincipal> LoginAsync() => Session.LoginAsync(_aliceLoginName, "hunter2");

        internal Task<AuthenticatedPrincipal> LoginAsAdministratorAsync() => Session.LoginAsync(LocalLoginName.Parse("root"), "hunter2");

        /// <summary>Creates another user's 0700 home, as the login seeding does.</summary>
        internal ValueTask SeedHomeAsync(string login) => _seeder.SeedAsync(login, Guid.NewGuid().ToString());

        /// <summary>The intents gateway the shell's context would get.</summary>
        internal IAppIntentGateway Gateway(AuthenticatedPrincipal principal) =>
            new AppIntentGateway(() => Dispatcher, "org.hackeros.shell", principal.UserId.ToString(), principal);

        /// <summary>Dispatches <paramref name="intent"/> on behalf of the shell for <paramref name="principal"/>.</summary>
        internal Task<AppIntentDispatchResult> DispatchAsync(AuthenticatedPrincipal principal, IAppIntent intent) =>
            Dispatcher.DispatchAsync(new AppIntentRequest(Guid.NewGuid(), "org.hackeros.shell", principal.UserId.ToString(), intent), principal).AsTask();

        internal void Grant(string appId, AuthenticatedPrincipal principal, string capability) =>
            Grants.Grant(appId, principal.UserId.ToString(), capability, CapabilityGrantSource.UserApproval, AppAuthority.Administrator);

        private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
        {
            public override DateTimeOffset GetUtcNow() => now;
        }
    }
}
