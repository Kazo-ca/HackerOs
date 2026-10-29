using HackerOs.App.Abstractions;
using HackerOs.App.Abstractions.Policy;
using HackerOs.AppSdk;
using HackerOs.AppSdk.Diagnostics;
using HackerOs.Simulation.Abstractions.FileSystem;
using HackerOs.Simulation.Abstractions.Gateways;
using Microsoft.AspNetCore.Components;

namespace HackerOs.AppSdk.Blazor;

/// <summary>
/// Base component for a HackerOS window application.
/// </summary>
/// <remarks>
/// Component lifecycle overrides are sealed so framework setup cannot be skipped.
/// Apps use the corresponding <c>OnApp*</c> hooks instead.
/// </remarks>
public abstract partial class WindowAppBase : ComponentBase, IHandleEvent, IHandleAfterRender, IWindowAppEventTarget
{
    private Guid? _boundInstanceId;
    private bool _permissionErrorHandlerAttached;
    private bool _hasCalledOnAfterRender;

    [Inject]
    private IWindowAppFrameworkLifecycle? FrameworkLifecycle { get; set; }

    private IWindowAppFrameworkLifecycle EffectiveFrameworkLifecycle =>
        FrameworkLifecycle ?? NullWindowAppFrameworkLifecycle.Instance;

    /// <summary>Gets or sets the execution context assigned by the window host.</summary>
    [Parameter]
    [EditorRequired]
    public IAppExecutionContext AppContext { get; set; } = default!;

    /// <summary>
    /// Gets the SDK's own texts (the dialogs this base class opens) in the language of the app's
    /// process (ADR 0104); the app's own texts come from its own <c>Strings</c>.
    /// </summary>
    private Microsoft.Extensions.Localization.IStringLocalizer<Strings> SdkText => HackerOs.AppSdk.Globalization.HackLocalization.GetLocalizer<Strings>(AppContext);

    /// <summary>Gets or sets the ecosystem-owned file dialog service.</summary>
    [Inject]
    protected IFileDialogService FileDialogs { get; set; } = default!;

    /// <summary>Gets or sets the ecosystem-owned dialog service.</summary>
    [Inject]
    protected IDialogService Dialogs { get; set; } = default!;

    /// <summary>
    /// Gets or sets the admin-credential verifier used to offer an approval prompt on a denied
    /// permission (`ADR 0042`). Optional: when unavailable (e.g. in tests), a denial simply falls
    /// back to the plain "Permission Denied" message.
    /// </summary>
    [Inject]
    protected IAdminElevationVerifier? ElevationVerifier { get; set; }

    /// <summary>Gets the validated window app manifest.</summary>
    protected AppManifest Manifest => AppContext.Manifest;

    /// <summary>
    /// Gets the arguments this window was launched with, without <c>argv[0]</c> (the app id), like
    /// <c>Main(string[] args)</c>. Opening a file from the File Explorer or with <c>open</c> starts
    /// the handler app with the file's absolute path as its first argument
    /// (<c>OpenFileIntent</c>, ADR 0050), so a document app reads its initial file here.
    /// </summary>
    protected IReadOnlyList<string> LaunchArguments
    {
        get
        {
            IReadOnlyList<string> argv = AppContext.Environment.GetCommandLineArgs();
            return argv.Count > 1 ? [.. argv.Skip(1)] : [];
        }
    }

    /// <summary>
    /// Gets the file this window was asked to open: the first launch argument when it is an
    /// absolute virtual path, otherwise <see langword="null"/> (a plain launch from the menu).
    /// </summary>
    protected VirtualPath? LaunchFile
    {
        get
        {
            if (LaunchArguments is not [{ } first, ..] || !first.StartsWith('/'))
            {
                return null;
            }

            try
            {
                return VirtualPath.Parse(first);
            }
            catch (FormatException)
            {
                return null;
            }
        }
    }

    /// <summary>
    /// Prevents apps from replacing parameter assignment and bypassing lifecycle validation. The
    /// synchronous lifecycle (and the continuations of the asynchronous one) runs inside the app's
    /// <see cref="HackExecutionContext"/> scope (ADR 0043).
    /// </summary>
    public sealed override Task SetParametersAsync(ParameterView parameters)
    {
        IAppExecutionContext? context = parameters.TryGetValue(nameof(AppContext), out IAppExecutionContext? supplied)
            ? supplied
            : AppContext;
        using IDisposable? scope = AmbientComponentDispatch.Enter(context);
        return base.SetParametersAsync(parameters);
    }

    /// <summary>Runs every UI event handler of the app inside its ambient scope (ADR 0043).</summary>
    Task IHandleEvent.HandleEventAsync(EventCallbackWorkItem callback, object? arg) =>
        AmbientComponentDispatch.HandleEventAsync(AppContext, callback, arg, StateHasChanged);

    /// <summary>Runs the after-render lifecycle inside the app's ambient scope (ADR 0043).</summary>
    Task IHandleAfterRender.OnAfterRenderAsync()
    {
        bool firstRender = !_hasCalledOnAfterRender;
        _hasCalledOnAfterRender = true;

        using IDisposable? scope = AmbientComponentDispatch.Enter(AppContext);
        OnAfterRender(firstRender);
        return OnAfterRenderAsync(firstRender);
    }

    /// <inheritdoc />
    protected sealed override void OnInitialized()
    {
        ValidateContext();
        OnAppInitialized();
    }

    /// <inheritdoc />
    protected sealed override Task OnInitializedAsync() => OnAppInitializedAsync();

    /// <inheritdoc />
    protected sealed override void OnParametersSet()
    {
        ValidateContext();
        OnAppParametersSet();
    }

    /// <inheritdoc />
    protected sealed override Task OnParametersSetAsync() => OnAppParametersSetAsync();

    /// <inheritdoc />
    protected sealed override bool ShouldRender() => ShouldRenderApp();

    /// <inheritdoc />
    protected sealed override void OnAfterRender(bool firstRender) => OnAppAfterRender(firstRender);

    /// <inheritdoc />
    protected sealed override async Task OnAfterRenderAsync(bool firstRender)
    {
        await EffectiveFrameworkLifecycle.OnAfterRenderAsync(this, firstRender);
        await OnAppAfterRenderAsync(firstRender);
    }

    /// <summary>Displays the standard file-open dialog.</summary>
    protected ValueTask<OpenFileDialogResult> OpenFileAsync(
        OpenFileDialogRequest request,
        CancellationToken cancellationToken = default)
    {
        EnsureFileDialogService();
        return FileDialogs.OpenFileAsync(AppContext, request, cancellationToken);
    }

    /// <summary>Displays the standard file-save dialog.</summary>
    protected ValueTask<SaveFileDialogResult> SaveFileAsync(
        SaveFileDialogRequest request,
        CancellationToken cancellationToken = default)
    {
        EnsureFileDialogService();
        return FileDialogs.SaveFileAsync(AppContext, request, cancellationToken);
    }

    /// <summary>Displays the standard folder-selection dialog.</summary>
    protected ValueTask<SelectFolderDialogResult> SelectFolderAsync(
        SelectFolderDialogRequest request,
        CancellationToken cancellationToken = default)
    {
        EnsureFileDialogService();
        return FileDialogs.SelectFolderAsync(AppContext, request, cancellationToken);
    }

    /// <summary>Displays a message box dialog.</summary>
    protected ValueTask<MessageBoxDialogResult> MessageBox(
        string title,
        string content,
        MessageBoxType dialogType = MessageBoxType.Ok,
        CancellationToken cancellationToken = default)
    {
        EnsureBasicDialogService();
        return Dialogs.MessageBoxAsync(
            AppContext,
            new MessageBoxDialogRequest
            {
                Title = title,
                Content = content,
                DialogType = dialogType
            },
            cancellationToken);
    }

    /// <summary>Displays a message box dialog with an icon, such as <see cref="MessageBoxIcon.Error"/> for a failure.</summary>
    protected ValueTask<MessageBoxDialogResult> MessageBox(
        string title,
        string content,
        MessageBoxType dialogType,
        MessageBoxIcon icon,
        CancellationToken cancellationToken = default)
    {
        EnsureBasicDialogService();
        return Dialogs.MessageBoxAsync(
            AppContext,
            new MessageBoxDialogRequest
            {
                Title = title,
                Content = content,
                DialogType = dialogType,
                Icon = icon
            },
            cancellationToken);
    }

    /// <summary>Displays a text input dialog.</summary>
    protected ValueTask<TextInputDialogResult> TextInput(
        string title,
        string content,
        string? defaultValue = null,
        string? placeholder = null,
        CancellationToken cancellationToken = default)
    {
        EnsureBasicDialogService();
        return Dialogs.TextInputAsync(
            AppContext,
            new TextInputDialogRequest
            {
                Title = title,
                Content = content,
                DefaultValue = defaultValue,
                Placeholder = placeholder
            },
            cancellationToken);
    }

    /// <summary>Runs app-specific synchronous initialization.</summary>
    protected virtual void OnAppInitialized()
    {
    }

    /// <summary>Runs app-specific asynchronous initialization.</summary>
    protected virtual Task OnAppInitializedAsync() => Task.CompletedTask;

    /// <summary>Runs after the host supplies or updates component parameters.</summary>
    protected virtual void OnAppParametersSet()
    {
    }

    /// <summary>Runs asynchronously after the host supplies or updates component parameters.</summary>
    protected virtual Task OnAppParametersSetAsync() => Task.CompletedTask;

    /// <summary>Lets an app suppress a render without replacing framework lifecycle code.</summary>
    protected virtual bool ShouldRenderApp() => true;

    /// <summary>Runs app-specific synchronous post-render work.</summary>
    protected virtual void OnAppAfterRender(bool firstRender)
    {
    }

    /// <summary>Runs app-specific asynchronous post-render work after framework setup.</summary>
    protected virtual Task OnAppAfterRenderAsync(bool firstRender) => Task.CompletedTask;

    private void ValidateContext()
    {
        if (AppContext is null)
        {
            throw new InvalidOperationException("The window host must provide AppContext.");
        }

        ManifestValidationResult validation = AppManifestValidator.Validate(AppContext.Manifest);
        if (!validation.IsValid)
        {
            throw new InvalidOperationException("The window host provided an invalid app manifest.");
        }

        if (AppContext.Manifest.Kind != AppKind.Window)
        {
            throw new InvalidOperationException(
                $"Manifest kind '{AppContext.Manifest.Kind}' cannot be hosted by WindowAppBase.");
        }

        if (_boundInstanceId is Guid instanceId && instanceId != AppContext.InstanceId)
        {
            throw new InvalidOperationException("A window component cannot be rebound to another app instance.");
        }

        _boundInstanceId = AppContext.InstanceId;

        if (!_permissionErrorHandlerAttached)
        {
            _permissionErrorHandlerAttached = true;
            AppContext.PermissionErrors.PermissionDenied += HandlePermissionDenied;
            AppContext.StopRequested += HandleStopRequested;
            AppContext.IntentReceived += HandleIntentReceived;
        }
    }

    /// <summary>
    /// Gets the intent this window was started for (ADR 0096, section 5): an app-defined intent with
    /// its payload, <c>reveal-file</c>, <c>show-app-settings</c>... or <see langword="null"/> for a
    /// plain launch. Its <see cref="AppIntentDelivery.Arguments"/> equal <see cref="LaunchArguments"/>.
    /// </summary>
    protected AppIntentDelivery? LaunchIntent => AppContext.Intent;

    /// <summary>
    /// Runs when the OS hands an intent to this already open window instead of starting a second
    /// instance (ADR 0096, section 5): only a <see cref="AppManifest.SingleInstancePerUser"/> app
    /// receives it. A second launch with arguments arrives as <see cref="AppIntentIds.LaunchApp"/>
    /// with those arguments, like the command line a <c>GApplication</c> forwards to its primary
    /// instance. Runs on the renderer's dispatcher, inside the app's ambient scope.
    /// </summary>
    /// <param name="intent">Identifier, payload, sender and arguments.</param>
    /// <returns>A task representing the handling.</returns>
    protected virtual Task OnIntentReceivedAsync(AppIntentDelivery intent) => Task.CompletedTask;

    private void HandleIntentReceived(object? sender, AppIntentDelivery intent)
    {
        try
        {
            _ = InvokeAsync(async () =>
            {
                using IDisposable? scope = AmbientComponentDispatch.Enter(AppContext);
                await OnIntentReceivedAsync(intent);
                StateHasChanged();
            });
        }
        catch (InvalidOperationException)
        {
            // Not attached to a renderer yet: the window shows the state its launch gave it.
            _ = OnIntentReceivedAsync(intent);
        }
    }

    /// <summary>
    /// Sends an app-defined intent (ADR 0096, section 5) and, when several apps can handle it and
    /// the user chose none yet, shows the OS's "Open with" chooser and sends it to the picked app,
    /// remembering it when the user ticks "Always use this app".
    /// </summary>
    /// <param name="intentId">Identifier <c>domain.reversed.name.vN</c>.</param>
    /// <param name="payloadJson">Optional JSON object of at most 64 KiB.</param>
    /// <param name="cancellationToken">Cancels the request and the chooser.</param>
    /// <returns>
    /// <see langword="true"/> when an app received it; <see langword="false"/> when nobody handles it or
    /// the user cancelled the chooser.
    /// </returns>
    /// <exception cref="ArgumentException">The identifier or payload is refused.</exception>
    /// <exception cref="HackerOs.AppSdk.Apps.HackAppLaunchException">The handler failed to start.</exception>
    /// <exception cref="HackerOs.AppSdk.IO.HackUnauthorizedAccessException">This app lacks <c>apps.launch</c>.</exception>
    protected async Task<bool> SendIntentAsync(string intentId, string? payloadJson = null, CancellationToken cancellationToken = default)
    {
        using IDisposable? scope = AmbientComponentDispatch.Enter(AppContext);
        HackerOs.AppSdk.Apps.HackIntentResult result =
            await HackerOs.AppSdk.Apps.HackApps.SendIntentAsync(intentId, payloadJson, cancellationToken: cancellationToken);
        if (result.Status != HackerOs.AppSdk.Apps.HackIntentStatus.ChooserRequired)
        {
            return result.Status == HackerOs.AppSdk.Apps.HackIntentStatus.Delivered;
        }

        AppChooserDialogResult choice = await ChooseAppAsync(
            result.CandidateAppIds, SdkText["Intent.ChooseApp", intentId], cancellationToken);
        if (choice.Status != AppChooserStatus.Selected || choice.AppId is null)
        {
            return false;
        }

        HackerOs.AppSdk.Apps.HackIntentResult sent = await HackerOs.AppSdk.Apps.HackApps.SendIntentAsync(
            intentId, payloadJson, choice.AppId, choice.AlwaysUse, cancellationToken);
        return sent.Status == HackerOs.AppSdk.Apps.HackIntentStatus.Delivered;
    }

    /// <summary>
    /// Shows the OS's "Open with" chooser over <paramref name="appIds"/> (ADR 0096, section 5), with
    /// each app's installed name and description.
    /// </summary>
    /// <param name="appIds">The candidate apps, e.g. <see cref="HackerOs.AppSdk.Apps.HackIntentResult.CandidateAppIds"/>.</param>
    /// <param name="content">Sentence above the list.</param>
    /// <param name="cancellationToken">Cancels the dialog.</param>
    protected async Task<AppChooserDialogResult> ChooseAppAsync(
        IReadOnlyList<string> appIds, string content, CancellationToken cancellationToken = default)
    {
        EnsureBasicDialogService();
        IReadOnlyList<HackerOs.AppSdk.Apps.HackAppInfo> installed = await AppContext.Catalog.GetInstalledAsync(cancellationToken);
        List<AppChooserOption> options = [.. appIds.Select(id =>
            installed.FirstOrDefault(app => app.Id == id) is { } app
                ? new AppChooserOption(app.Id, app.Name, app.Description)
                : new AppChooserOption(id, id))];
        return await Dialogs.ChooseAppAsync(
            AppContext,
            new AppChooserDialogRequest { Title = SdkText["Intent.OpenWith"], Content = content, Candidates = options },
            cancellationToken);
    }

    /// <summary>
    /// Runs when the OS asks the app's process to stop (ADR 0069): <c>kill</c>, "End task", logout,
    /// shutdown. The window closes once the returned task completes, or at
    /// <see cref="HackStopRequestedEventArgs.Deadline"/> at the latest, when the OS kills the process.
    /// An app that holds state finishes or cancels its current operation here, and keeps a recovery
    /// draft of unsaved work instead of losing it silently. It is not called when the user closes the
    /// window: <see cref="IWindowCloseGuard"/> asks the user then.
    /// </summary>
    /// <param name="e">Reason and deadline of the request.</param>
    /// <returns>The work the OS waits for before closing the window.</returns>
    protected virtual Task OnAppStopRequestedAsync(HackStopRequestedEventArgs e) => Task.CompletedTask;

    /// <summary>
    /// Hands the stop request to the app on the renderer's dispatcher (component state is touched)
    /// and asks the OS to wait for it.
    /// </summary>
    private void HandleStopRequested(object? sender, HackStopRequestedEventArgs e)
    {
        Task work;
        try
        {
            work = InvokeAsync(() =>
            {
                using IDisposable? scope = AmbientComponentDispatch.Enter(AppContext);
                return OnAppStopRequestedAsync(e);
            });
        }
        catch (InvalidOperationException)
        {
            // Not attached to a renderer (never rendered): nothing on screen to save from.
            work = OnAppStopRequestedAsync(e);
        }

        e.Defer(work);
    }

    /// <summary>
    /// Runs when a permission-class filesystem error (missing capability, missing authority, or a
    /// denied selected-handle/mode check) is raised for this app instance. Override and set
    /// <see cref="AppPermissionErrorEventArgs.ErrorHandled"/> to suppress the host's default error
    /// notification when the app already surfaces this failure through its own UI.
    /// </summary>
    protected virtual void OnPermissionErrorRaised(AppPermissionErrorEventArgs e)
    {
    }

    private async void HandlePermissionDenied(object? sender, AppPermissionErrorEventArgs e)
    {
        OnPermissionErrorRaised(e);
        if (e.ErrorHandled)
        {
            return;
        }

        e.ErrorHandled = true;

        // ADR 0049 section 3: under dangerousCapabilities=block, a dangerous capability of a
        // non-system app cannot be approved once either, so no prompt is offered.
        if (ElevationVerifier is not null
            && e.Error.RequiredCapability is not null
            && !AppContext.OneShotElevation.IsBlocked(e.Error.RequiredCapability))
        {
            EnsureBasicDialogService();
            CredentialPromptDialogResult prompt = await Dialogs.CredentialPromptAsync(AppContext, new CredentialPromptDialogRequest
            {
                Title = SdkText["Permission.Approval.Title"],
                Description = SdkText["Permission.Approval.Description", Manifest.Name, e.Error.Path?.ToString() ?? string.Empty]
            });

            if (prompt.Status == CredentialPromptStatus.Submitted)
            {
                AdminElevationVerificationResult verification = await ElevationVerifier.VerifyAsync(
                    prompt.LoginName!, prompt.Password!);
                if (verification.Success)
                {
                    AppContext.OneShotElevation.Grant(e.Error.RequiredCapability, e.Error.Path);
                    await MessageBox(
                        SdkText["Permission.Approved.Title"],
                        SdkText["Permission.Approved.Message"],
                        MessageBoxType.Ok);
                    return;
                }
            }
        }

        await MessageBox(SdkText["Permission.Denied.Title"], DescribePermissionError(e.Error), MessageBoxType.Ok);
    }

    private string DescribePermissionError(FileSystemError error) => SdkText[error.Code switch
    {
        FileSystemErrorCode.PermissionDenied => "Permission.Denied.User",
        FileSystemErrorCode.CapabilityDenied => "Permission.Denied.Capability",
        FileSystemErrorCode.AuthorityDenied => "Permission.Denied.Authority",
        _ => "Permission.Denied.Other"
    }, error.Path?.ToString() ?? string.Empty];

    private void EnsureFileDialogService()
    {
        ValidateContext();
        if (FileDialogs is null && Dialogs is not null)
        {
            FileDialogs = Dialogs;
        }

        if (FileDialogs is null)
        {
            throw new InvalidOperationException("The window host must provide IFileDialogService.");
        }
    }

    private void EnsureBasicDialogService()
    {
        ValidateContext();
        if (Dialogs is null && FileDialogs is IDialogService dialogs)
        {
            Dialogs = dialogs;
        }

        if (Dialogs is null)
        {
            throw new InvalidOperationException("The window host must provide IDialogService.");
        }
    }
}