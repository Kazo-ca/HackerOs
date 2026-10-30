using System.Reflection;
using HackerOs.App.Abstractions;
using HackerOs.App.Abstractions.Policy;
using HackerOs.AppSdk;
using HackerOs.AppSdk.Blazor;
using HackerOs.Simulation.Abstractions.FileSystem;
using HackerOs.Simulation.Abstractions.Gateways;
using HackerOs.Simulation.Abstractions.Processes;
using HackerOs.Simulation.Abstractions.Sessions;

namespace HackerOs.AppSdk.Blazor.Tests;

public sealed class WindowAppBaseTests
{
    [Theory]
    [InlineData("OnInitialized")]
    [InlineData("OnInitializedAsync")]
    [InlineData("OnParametersSet")]
    [InlineData("OnParametersSetAsync")]
    [InlineData("ShouldRender")]
    [InlineData("OnAfterRender")]
    [InlineData("OnAfterRenderAsync")]
    public void Framework_lifecycle_methods_are_sealed(string methodName)
    {
        MethodInfo? method = typeof(WindowAppBase).GetMethod(
            methodName,
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(method);
        Assert.True(method.IsFinal);
    }

    [Fact]
    public async Task App_post_render_hook_runs_through_the_sealed_lifecycle()
    {
        TestWindowApp app = CreateWindowApp();

        await app.InvokeAfterRenderAsync(firstRender: true);

        Assert.True(app.AfterRenderCalled);
    }

    [Fact]
    public async Task Framework_post_render_setup_runs_before_the_app_hook()
    {
        List<string> calls = [];
        TestWindowApp app = CreateWindowApp();
        app.AfterRenderCallback = () => calls.Add("app");
        PropertyInfo lifecycleProperty = typeof(WindowAppBase).GetProperty(
            "FrameworkLifecycle",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        lifecycleProperty.SetValue(app, new TestFrameworkLifecycle(calls));

        await app.InvokeAfterRenderAsync(firstRender: true);

        Assert.Equal(["framework", "app"], calls);
    }

    [Fact]
    public async Task A_stop_request_reaches_the_app_hook_and_the_os_waits_for_its_work()
    {
        // ADR 0069: the window closes only after the app's draft is written.
        TestExecutionContext context = new(CreateManifest(AppKind.Window));
        TestWindowApp app = CreateWindowApp(context: context);
        app.InvokeInitialized();
        TaskCompletionSource draft = new(TaskCreationOptions.RunContinuationsAsynchronously);
        app.StopWork = draft.Task;
        HackerOs.AppSdk.Diagnostics.HackStopRequestedEventArgs request = new(
            ProcessExitReason.Terminated, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddSeconds(5));

        context.RaiseStopRequested(request);
        Task deferred = request.WhenDeferredWorkEndedAsync();

        Assert.Same(request, app.HeardRequest);
        Assert.False(deferred.IsCompleted);
        draft.SetResult();
        await deferred.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void Window_base_rejects_a_non_window_manifest()
    {
        TestWindowApp app = CreateWindowApp(AppKind.Service);

        Assert.Throws<InvalidOperationException>(app.InvokeInitialized);
    }

    [Fact]
    public async Task File_dialog_helpers_delegate_with_the_bound_app_context()
    {
        FakeDialogService dialogs = new();
        TestWindowApp app = CreateWindowApp(dialogs: dialogs);
        OpenFileDialogRequest request = new()
        {
            InitialDirectory = VirtualPath.Parse("/home/user"),
            AllowMultiple = true
        };

        OpenFileDialogResult result = await app.InvokeOpenFileAsync(request);

        Assert.Same(app.AppContext, dialogs.LastContext);
        Assert.Same(request, dialogs.LastOpenRequest);
        Assert.Equal(FileDialogStatus.Cancelled, result.Status);
    }

    [Fact]
    public async Task Message_box_helper_delegates_to_dialog_service()
    {
        FakeDialogService dialogs = new();
        TestWindowApp app = CreateWindowApp(dialogs: dialogs);

        MessageBoxDialogResult result = await app.InvokeMessageBoxAsync(
            "Confirmation", "Voulez-vous vraiment effacer ce fichier?", MessageboxType.YesNo);

        Assert.Same(app.AppContext, dialogs.LastContext);
        Assert.NotNull(dialogs.LastMessageBoxRequest);
        Assert.Equal("Confirmation", dialogs.LastMessageBoxRequest.Title);
        Assert.Equal("Voulez-vous vraiment effacer ce fichier?", dialogs.LastMessageBoxRequest.Content);
        Assert.Equal(MessageBoxType.YesNo, dialogs.LastMessageBoxRequest.DialogType);
        Assert.Equal(MessageBoxResult.Yes, result.Result);
        Assert.Equal(MessageboxResult.Yes.Value, result.Result);
    }

    [Fact]
    public async Task Message_box_helper_passes_the_icon_to_the_dialog_service()
    {
        FakeDialogService dialogs = new();
        TestWindowApp app = CreateWindowApp(dialogs: dialogs);

        await app.InvokeMessageBoxAsync("File Explorer", "No application is registered to open this file.", MessageBoxType.Ok, MessageBoxIcon.Error);

        Assert.NotNull(dialogs.LastMessageBoxRequest);
        Assert.Equal(MessageBoxIcon.Error, dialogs.LastMessageBoxRequest.Icon);
        Assert.Equal(MessageBoxType.Ok, dialogs.LastMessageBoxRequest.DialogType);
    }

    [Fact]
    public async Task Message_box_without_icon_shows_none()
    {
        FakeDialogService dialogs = new();
        TestWindowApp app = CreateWindowApp(dialogs: dialogs);

        await app.InvokeMessageBoxAsync("Title", "Body", MessageBoxType.Ok);

        Assert.Equal(MessageBoxIcon.None, dialogs.LastMessageBoxRequest!.Icon);
    }

    [Fact]
    public async Task Text_input_helper_delegates_to_dialog_service()
    {
        FakeDialogService dialogs = new();
        TestWindowApp app = CreateWindowApp(dialogs: dialogs);

        TextInputDialogResult result = await app.InvokeTextInputAsync(
            "Rename", "Enter new name:", "DefaultName", "Placeholder");

        Assert.Same(app.AppContext, dialogs.LastContext);
        Assert.NotNull(dialogs.LastTextInputRequest);
        Assert.Equal("Rename", dialogs.LastTextInputRequest.Title);
        Assert.Equal("Enter new name:", dialogs.LastTextInputRequest.Content);
        Assert.Equal("DefaultName", dialogs.LastTextInputRequest.DefaultValue);
        Assert.Equal("Placeholder", dialogs.LastTextInputRequest.Placeholder);
        Assert.Equal(TextInputStatus.Submitted, result.Status);
        Assert.Equal("SubmittedText", result.Value);
    }

    [Fact]
    public void A_denied_permission_falls_back_to_the_plain_message_when_no_elevation_verifier_is_configured()
    {
        FakeDialogService dialogs = new();
        TestExecutionContext context = new(TestManifest());
        TestWindowApp app = CreateWindowApp(dialogs: dialogs, context: context);
        app.InvokeInitialized();

        context.PermissionErrorsGateway.Raise(new AppPermissionErrorEventArgs(
            new FileSystemError(
                FileSystemOperation.Stat, FileSystemErrorCode.CapabilityDenied,
                VirtualPath.Parse("/etc"), requiredCapability: "filesystem.system.read")));

        Assert.NotNull(dialogs.LastMessageBoxRequest);
        Assert.Equal("Permission Denied", dialogs.LastMessageBoxRequest.Title);
        Assert.Null(dialogs.LastCredentialPromptRequest);
    }

    [Fact]
    public void An_approved_elevation_grants_a_one_shot_ticket_instead_of_showing_permission_denied()
    {
        FakeDialogService dialogs = new()
        {
            NextCredentialPromptResult = new CredentialPromptDialogResult(CredentialPromptStatus.Submitted, "admin", "hunter2")
        };
        FakeAdminElevationVerifier verifier = new()
        {
            NextResult = new AdminElevationVerificationResult(true, "admin-id", "admin", null)
        };
        TestExecutionContext context = new(TestManifest());
        TestWindowApp app = CreateWindowApp(dialogs: dialogs, elevationVerifier: verifier, context: context);
        app.InvokeInitialized();
        VirtualPath deniedPath = VirtualPath.Parse("/etc");

        context.PermissionErrorsGateway.Raise(new AppPermissionErrorEventArgs(
            new FileSystemError(
                FileSystemOperation.Stat, FileSystemErrorCode.CapabilityDenied,
                deniedPath, requiredCapability: "filesystem.system.read")));

        Assert.NotNull(dialogs.LastCredentialPromptRequest);
        Assert.Equal("admin", verifier.LastLoginName);
        Assert.Equal("hunter2", verifier.LastPassword);
        Assert.True(context.OneShotElevation.TryConsume("filesystem.system.read", deniedPath));
        Assert.Equal("Access Approved", dialogs.LastMessageBoxRequest?.Title);
    }

    [Fact]
    public void A_cancelled_or_denied_elevation_prompt_falls_back_to_the_plain_message()
    {
        FakeDialogService dialogs = new()
        {
            NextCredentialPromptResult = new CredentialPromptDialogResult(CredentialPromptStatus.Cancelled, null, null)
        };
        FakeAdminElevationVerifier verifier = new();
        TestExecutionContext context = new(TestManifest());
        TestWindowApp app = CreateWindowApp(dialogs: dialogs, elevationVerifier: verifier, context: context);
        app.InvokeInitialized();

        context.PermissionErrorsGateway.Raise(new AppPermissionErrorEventArgs(
            new FileSystemError(
                FileSystemOperation.Stat, FileSystemErrorCode.CapabilityDenied,
                VirtualPath.Parse("/etc"), requiredCapability: "filesystem.system.read")));

        Assert.Equal("Permission Denied", dialogs.LastMessageBoxRequest?.Title);
    }

    private static AppManifest TestManifest() => new()
    {
        Id = "org.hackeros.test-window",
        Name = "Test Window",
        Version = "1.0.0",
        PublisherId = "org.hackeros",
        Description = "Window App SDK test component.",
        Kind = AppKind.Window,
        EntryPoint = new AppEntryPointManifest("HackerOs.AppSdk.Blazor.Tests", "TestWindowApp"),
        SdkCompatibility = new AppSdkCompatibilityManifest(HackerOsSdk.Version),
        Presentation = new PresentationManifest("test", AppLaunchVisibility.Visible, []),
        Resources = AppResourceProfileManifest.None
    };

    [Theory]
    [InlineData(new[] { "org.hackeros.test-window", "/home/user/notes.txt" }, "/home/user/notes.txt")]
    [InlineData(new[] { "org.hackeros.test-window" }, null)]
    [InlineData(new[] { "org.hackeros.test-window", "--new-window" }, null)]
    public void LaunchFile_is_the_first_absolute_argument_after_argv0(string[] argv, string? expected)
    {
        // An open-file intent (File Explorer double-click) launches the handler with argv
        // [appId, path]; the document app reads its file from there (ADR 0050).
        TestWindowApp app = CreateWindowApp(context: new TestExecutionContext(CreateManifest())
        {
            Environment = new HackerOs.Tests.Support.FakeAppEnvironmentGateway("/home/user", arguments: argv)
        });

        Assert.Equal(argv.Skip(1), app.ExposedLaunchArguments);
        Assert.Equal(expected, app.ExposedLaunchFile?.Value);
    }

    private static AppManifest CreateManifest(AppKind kind = AppKind.Window) =>
        new()
        {
            Id = "org.hackeros.test-window",
            Name = "Test Window",
            Version = "1.0.0",
            PublisherId = "org.hackeros",
            Description = "Window App SDK test component.",
            Kind = kind,
            EntryPoint = new AppEntryPointManifest("HackerOs.AppSdk.Blazor.Tests", "TestWindowApp"),
            SdkCompatibility = new AppSdkCompatibilityManifest(HackerOsSdk.Version),
            Presentation = new PresentationManifest("test", AppLaunchVisibility.Visible, []),
            Resources = AppResourceProfileManifest.None
        };

    private static TestWindowApp CreateWindowApp(
        AppKind kind = AppKind.Window,
        IDialogService? dialogs = null,
        IAdminElevationVerifier? elevationVerifier = null,
        TestExecutionContext? context = null)
    {
        AppManifest manifest = CreateManifest(kind);

        FakeDialogService fakeService = dialogs as FakeDialogService ?? new FakeDialogService();
        TestWindowApp app = new(fakeService, elevationVerifier);
        app.BindContext(context ?? new TestExecutionContext(manifest));
        return app;
    }

    private sealed class TestWindowApp : WindowAppBase
    {
        public TestWindowApp(IDialogService dialogs, IAdminElevationVerifier? elevationVerifier = null)
        {
            Dialogs = dialogs;
            FileDialogs = dialogs;
            ElevationVerifier = elevationVerifier;
        }

        public bool AfterRenderCalled { get; private set; }

        public Action? AfterRenderCallback { get; set; }

        public void BindContext(IAppExecutionContext context) => AppContext = context;

        public IReadOnlyList<string> ExposedLaunchArguments => LaunchArguments;

        public VirtualPath? ExposedLaunchFile => LaunchFile;

        public void InvokeInitialized() => base.OnInitialized();

        public Task InvokeAfterRenderAsync(bool firstRender) => base.OnAfterRenderAsync(firstRender);

        public ValueTask<OpenFileDialogResult> InvokeOpenFileAsync(OpenFileDialogRequest request) =>
            base.OpenFileAsync(request);

        public ValueTask<MessageBoxDialogResult> InvokeMessageBoxAsync(
            string title, string content, MessageBoxType dialogType) =>
            base.MessageBox(title, content, dialogType);

        public ValueTask<MessageBoxDialogResult> InvokeMessageBoxAsync(
            string title, string content, MessageBoxType dialogType, MessageBoxIcon icon) =>
            base.MessageBox(title, content, dialogType, icon);

        public ValueTask<TextInputDialogResult> InvokeTextInputAsync(
            string title, string content, string? defaultValue = null, string? placeholder = null) =>
            base.TextInput(title, content, defaultValue, placeholder);

        public Task StopWork { get; set; } = Task.CompletedTask;

        public HackerOs.AppSdk.Diagnostics.HackStopRequestedEventArgs? HeardRequest { get; private set; }

        protected override Task OnAppStopRequestedAsync(HackerOs.AppSdk.Diagnostics.HackStopRequestedEventArgs e)
        {
            HeardRequest = e;
            return StopWork;
        }

        protected override Task OnAppAfterRenderAsync(bool firstRender)
        {
            AfterRenderCalled = firstRender;
            AfterRenderCallback?.Invoke();
            return Task.CompletedTask;
        }
    }

    private sealed class TestFrameworkLifecycle(List<string> calls) : IWindowAppFrameworkLifecycle
    {
        public Task OnAfterRenderAsync(WindowAppBase app, bool firstRender)
        {
            _ = app;
            _ = firstRender;
            calls.Add("framework");
            return Task.CompletedTask;
        }
    }

    private sealed class FakeDialogService : IDialogService
    {
        public IAppExecutionContext? LastContext { get; private set; }

        public OpenFileDialogRequest? LastOpenRequest { get; private set; }

        public MessageBoxDialogRequest? LastMessageBoxRequest { get; private set; }

        public TextInputDialogRequest? LastTextInputRequest { get; private set; }

        public ValueTask<OpenFileDialogResult> OpenFileAsync(
            IAppExecutionContext context,
            OpenFileDialogRequest request,
            CancellationToken cancellationToken = default)
        {
            LastContext = context;
            LastOpenRequest = request;
            return ValueTask.FromResult(new OpenFileDialogResult(FileDialogStatus.Cancelled, []));
        }

        public ValueTask<SaveFileDialogResult> SaveFileAsync(
            IAppExecutionContext context,
            SaveFileDialogRequest request,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new SaveFileDialogResult(FileDialogStatus.Cancelled, null));

        public ValueTask<SelectFolderDialogResult> SelectFolderAsync(
            IAppExecutionContext context,
            SelectFolderDialogRequest request,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new SelectFolderDialogResult(FileDialogStatus.Cancelled, null));

        public ValueTask<MessageBoxDialogResult> MessageBoxAsync(
            IAppExecutionContext context,
            MessageBoxDialogRequest request,
            CancellationToken cancellationToken = default)
        {
            LastContext = context;
            LastMessageBoxRequest = request;
            return ValueTask.FromResult(new MessageBoxDialogResult(MessageBoxResult.Yes));
        }

        public ValueTask<TextInputDialogResult> TextInputAsync(
            IAppExecutionContext context,
            TextInputDialogRequest request,
            CancellationToken cancellationToken = default)
        {
            LastContext = context;
            LastTextInputRequest = request;
            return ValueTask.FromResult(new TextInputDialogResult(TextInputStatus.Submitted, "SubmittedText"));
        }

        public CredentialPromptDialogRequest? LastCredentialPromptRequest { get; private set; }

        public CredentialPromptDialogResult NextCredentialPromptResult { get; set; } =
            new(CredentialPromptStatus.Cancelled, null, null);

        public ValueTask<CredentialPromptDialogResult> CredentialPromptAsync(
            IAppExecutionContext context,
            CredentialPromptDialogRequest request,
            CancellationToken cancellationToken = default)
        {
            LastContext = context;
            LastCredentialPromptRequest = request;
            return ValueTask.FromResult(NextCredentialPromptResult);
        }
    }

    private sealed class FakePermissionErrorGateway : IAppPermissionErrorGateway
    {
        public event EventHandler<AppPermissionErrorEventArgs>? PermissionDenied;

        public void Raise(AppPermissionErrorEventArgs e) => PermissionDenied?.Invoke(this, e);
    }

    private sealed class TestExecutionContext(AppManifest manifest) : IAppExecutionContext
    {
        private EventHandler<HackerOs.AppSdk.Diagnostics.HackStopRequestedEventArgs>? _stopRequested;

        public FakePermissionErrorGateway PermissionErrorsGateway { get; } = new();

        public event EventHandler<HackerOs.AppSdk.Diagnostics.HackStopRequestedEventArgs>? StopRequested
        {
            add => _stopRequested += value;
            remove => _stopRequested -= value;
        }

        public void RaiseStopRequested(HackerOs.AppSdk.Diagnostics.HackStopRequestedEventArgs request) =>
            _stopRequested?.Invoke(this, request);

        public AppManifest Manifest { get; } = manifest;

        public Guid InstanceId { get; } = Guid.NewGuid();

        public string UserId => "user";

        public AppAuthority UserAuthority => AppAuthority.User;

        public IReadOnlySet<string> GrantedCapabilities { get; } = new HashSet<string>();

        public SessionId SessionId { get; } = SessionId.FromGuid(Guid.NewGuid());

        public ProcessId ProcessId { get; } = ProcessId.FromInt64(1);

        public CancellationToken CancellationToken => CancellationToken.None;

        public ICapabilityChecker Capabilities => throw new NotSupportedException();

        public IAppFileSystemGateway FileSystem => throw new NotSupportedException();


        public IAppEventGateway Events => throw new NotSupportedException();

        public IAppNotificationGateway Notifications => throw new NotSupportedException();

        public IAppLoggingGateway Logging => throw new NotSupportedException();


        public IAppClockGateway Clock => throw new NotSupportedException();

        public IAppProcessGateway Processes => throw new NotSupportedException();

        public IAppPermissionErrorGateway PermissionErrors => PermissionErrorsGateway;

        public OneShotElevationGrantSet OneShotElevation { get; } = new();

        public IAppEnvironmentGateway Environment { get; init; } =
            new HackerOs.Tests.Support.FakeAppEnvironmentGateway("/home/user", arguments: [manifest.Id]);
    }

    private sealed class FakeAdminElevationVerifier : IAdminElevationVerifier
    {
        public AdminElevationVerificationResult NextResult { get; set; } =
            AdminElevationVerificationResult.Failed("not-configured");

        public string? LastLoginName { get; private set; }

        public string? LastPassword { get; private set; }

        public Task<AdminElevationVerificationResult> VerifyAsync(
            string loginName, string password, CancellationToken cancellationToken = default)
        {
            LastLoginName = loginName;
            LastPassword = password;
            return Task.FromResult(NextResult);
        }
    }
}