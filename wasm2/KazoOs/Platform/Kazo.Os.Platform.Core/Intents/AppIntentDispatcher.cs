using Kazo.Os.App.Abstractions;
using Kazo.Os.Simulation.Abstractions.Gateways;
using Kazo.Os.App.Abstractions.Policy;
using Kazo.Os.AppSdk;
using Kazo.Os.Platform.Core.Discovery;
using Kazo.Os.Platform.Core.Lifecycle;
using Kazo.Os.Platform.Core.Policy;
using Kazo.Os.Platform.Core.Sessions;
using Kazo.Os.Platform.Core.Shell;
using Kazo.Os.Simulation.Abstractions.FileSystem;
using Kazo.Os.Simulation.Abstractions.Processes;
using Kazo.Os.Simulation.Abstractions.Sessions;

namespace Kazo.Os.Platform.Core.Intents;

/// <summary>Identifies the stable outcome of dispatching one app intent.</summary>
public enum AppIntentDispatchStatus
{
    /// <summary>The intent was authorized and carried out (or acknowledged, for deferred UI intents).</summary>
    Dispatched,

    /// <summary>The calling app lacks the capability required to carry out this intent.</summary>
    CapabilityDenied,

    /// <summary>No catalog app matches the intent's target.</summary>
    NotFound,

    /// <summary>The resolved target app is currently disabled.</summary>
    Disabled,

    /// <summary>Multiple candidate apps can handle an <see cref="OpenFileIntent"/>; the caller must choose.</summary>
    ChooserRequired,

    /// <summary>An <see cref="OpenFileIntent"/>'s explicit preferred app is invalid.</summary>
    TargetInvalid,

    /// <summary>The resolved target's entry point faulted while starting or executing.</summary>
    EntryPointFault,

    /// <summary>The supplied elevation token was missing, expired, wrong-scope, or already consumed (`ADR 0042`).</summary>
    ElevationDenied,

    /// <summary>The acting user may not read what the intent points at (<see cref="RevealFileIntent"/>, ADR 0096).</summary>
    AccessDenied,

    /// <summary>The request is malformed: intent identifier, URI or payload (ADR 0096).</summary>
    Invalid
}

/// <summary>Contains the result of dispatching one <see cref="IAppIntent"/>, per `P1-APP-007`.</summary>
/// <param name="Status">Stable dispatch outcome.</param>
/// <param name="Process">Process created for the resolved target, when one was launched.</param>
/// <param name="Context">Execution context for the resolved target, when one was launched.</param>
/// <param name="ExitCode">Exit code for a completed <see cref="ExecuteCommandIntent"/> or Terminal <see cref="OpenFileIntent"/>.</param>
/// <param name="StandardOutput">Captured standard output for a completed Terminal launch.</param>
/// <param name="StandardError">Captured standard error for a completed Terminal launch.</param>
/// <param name="CandidateAppIds">Candidate app IDs, populated for <see cref="AppIntentDispatchStatus.ChooserRequired"/>.</param>
/// <param name="ErrorCode">Stable machine-readable error code when dispatch did not succeed.</param>
/// <param name="PermissionDenials">
/// Filesystem permission-class errors raised while a completed Terminal launch ran (`ADR 0042`),
/// forwarded from <see cref="AppLaunchResult.PermissionDenials"/>.
/// </param>
/// <param name="FellBackToOpen">
/// An <see cref="EditFileIntent"/> found no <c>edit</c> handler and was resolved among the
/// <c>open</c> handlers (ADR 0096, section 2).
/// </param>
public sealed record AppIntentDispatchResult(
    AppIntentDispatchStatus Status,
    ProcessRecord? Process = null,
    IAppExecutionContext? Context = null,
    int? ExitCode = null,
    string? StandardOutput = null,
    string? StandardError = null,
    IReadOnlyList<string>? CandidateAppIds = null,
    string? ErrorCode = null,
    IReadOnlyList<FileSystemError>? PermissionDenials = null,
    bool FellBackToOpen = false);

/// <summary>Identifies the stable outcome of one service-control request (start/stop/get/set start mode).</summary>
public enum ServiceControlDispatchStatus
{
    /// <summary>The request completed: the service was started/stopped, or its start mode was read/set.</summary>
    Succeeded,

    /// <summary>No catalog app matches the requested target ID.</summary>
    NotFound,

    /// <summary>The resolved target app is not a <see cref="AppKind.Service"/> app.</summary>
    NotAService,

    /// <summary>
    /// The caller is neither in the same assembly as the target service nor holds
    /// <see cref="AppCapabilities.ServicesManage"/>.
    /// </summary>
    CapabilityDenied,

    /// <summary>The target service's start mode is <see cref="ServiceStartMode.Disabled"/>; it cannot be started.</summary>
    ServiceDisabled,

    /// <summary>The target's entry point faulted while starting.</summary>
    Faulted
}

/// <summary>Contains the result of one service-control request.</summary>
/// <param name="Status">Stable request outcome.</param>
/// <param name="ErrorCode">Stable machine-readable error code when the outcome did not succeed.</param>
public sealed record ServiceControlDispatchResult(ServiceControlDispatchStatus Status, string? ErrorCode = null);

/// <summary>
/// Capability-gates and dispatches every typed <see cref="IAppIntent"/> to the lifecycle
/// orchestrator or file-association resolver, per `P1-APP-007`. This is the policy layer:
/// it decides whether a request is allowed before <see cref="AppLifecycleOrchestrator"/> (the
/// mechanism layer) carries it out.
/// </summary>
public sealed class AppIntentDispatcher
{
    private readonly AppLifecycleOrchestrator _orchestrator;
    private readonly AppCatalog _catalog;
    private readonly IAppEnablementRegistry _enablement;
    private readonly FileAssociationResolver _associations;
    private readonly ICapabilityGrantRepository _grants;
    private readonly IFileSystemService _fileSystem;
    private readonly ShellInterpreter _shell;

    /// <summary>Initializes the dispatcher over the orchestrator, catalog, resolver, grant repository, and filesystem.</summary>
    public AppIntentDispatcher(
        AppLifecycleOrchestrator orchestrator,
        AppCatalog catalog,
        IAppEnablementRegistry enablement,
        FileAssociationResolver associations,
        ICapabilityGrantRepository grants,
        IFileSystemService fileSystem)
    {
        _orchestrator = orchestrator ?? throw new ArgumentNullException(nameof(orchestrator));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _enablement = enablement ?? throw new ArgumentNullException(nameof(enablement));
        _associations = associations ?? throw new ArgumentNullException(nameof(associations));
        _grants = grants ?? throw new ArgumentNullException(nameof(grants));
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _shell = new ShellInterpreter(orchestrator, catalog, enablement);
    }

    /// <summary>Dispatches one intent request on behalf of an authenticated principal.</summary>
    /// <param name="request">Caller app, acting user, and typed intent.</param>
    /// <param name="principal">Authenticated principal the request runs as. Must match <paramref name="request"/>'s user.</param>
    /// <param name="fullScreen">Optional alternate-screen renderer supplied to an executed Terminal command.</param>
    /// <param name="cancellationToken">Cancels command execution without affecting unrelated intents.</param>
    /// <param name="approvalPrompt">
    /// Optional prompt for an executed command's pending dangerous capabilities (ADR 0049 section 5):
    /// the terminal's text mode. Other intents use the host's overlay.
    /// </param>
    /// <param name="callerContext">
    /// The calling app's own context, for <see cref="ExecuteCommandIntent"/> (ADR 0050, section 7):
    /// its process becomes the shell's process (parent of the commands, starting directory) and its
    /// file gateway opens redirection targets. Without it the line runs in a detached shell: no
    /// parent, the login environment, and no redirections.
    /// </param>
    public async ValueTask<AppIntentDispatchResult> DispatchAsync(
        AppIntentRequest request,
        AuthenticatedPrincipal principal,
        IFullScreenTerminalSession? fullScreen = null,
        CancellationToken cancellationToken = default,
        IDangerousCapabilityApprovalPrompt? approvalPrompt = null,
        IAppExecutionContext? callerContext = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(principal);
        if (!string.Equals(request.UserId, principal.UserId.ToString(), StringComparison.Ordinal))
        {
            throw new ArgumentException("The intent request's user must match the acting principal.", nameof(request));
        }

        return request.Intent switch
        {
            LaunchAppIntent launch => await DispatchLaunchAsync(request, launch, principal),
            OpenFileIntent openFile => await DispatchOpenFileAsync(request, openFile, principal),
            ExecuteCommandIntent execute => await DispatchExecuteCommandAsync(
                request, execute, principal, fullScreen, approvalPrompt, callerContext, cancellationToken),
            RevealFileIntent reveal => await DispatchRevealAsync(request, reveal, principal, cancellationToken),
            EditFileIntent edit => await DispatchEditFileAsync(request, edit, principal),
            OpenUriIntent openUri => await DispatchOpenUriAsync(request, openUri, principal, cancellationToken),
            ShowAppSettingsIntent showSettings => await DispatchShowSettingsAsync(request, showSettings, principal),
            CustomIntent custom => await DispatchCustomAsync(request, custom, principal, cancellationToken),
            _ => new AppIntentDispatchResult(AppIntentDispatchStatus.NotFound, ErrorCode: "intent.unknown")
        };
    }

    /// <summary>App id the OS records on the file operations it makes for an intent (audit, private-path rules).</summary>
    internal const string OsAppId = "org.hackeros.intents";

    /// <summary>App id of the Settings app, which shows the page of an app with no settings of its own (ADR 0096, section 4).</summary>
    public const string SettingsAppId = "org.hackeros.settings";

    /// <summary>Launch argument asking an app for its own settings page (ADR 0096, section 4).</summary>
    public const string ShowSettingsArgument = "--show-settings";

    /// <summary>Prefix of the Settings argument naming the app whose page to show, <c>--app=&lt;id&gt;</c>.</summary>
    public const string SettingsAppArgumentPrefix = "--app=";

    /// <summary>Prefix of the folder-handler argument naming the item to select, <c>--select=&lt;path&gt;</c> (ADR 0096, section 1).</summary>
    public const string SelectArgumentPrefix = "--select=";

    /// <summary>Media type of a directory, whose default handler is the file manager (ADR 0039).</summary>
    public const string DirectoryMediaType = "inode/directory";

    private bool HasAppsLaunch(string callerAppId, string userId, AppAuthority actingAuthority) =>
        _grants.Evaluate(callerAppId, userId, AppCapabilities.AppsLaunch, actingAuthority, AppAuthority.User).Granted;

    private async ValueTask<AppIntentDispatchResult> DispatchLaunchAsync(
        AppIntentRequest request, LaunchAppIntent intent, AuthenticatedPrincipal principal)
    {
        if (!string.Equals(request.CallerAppId, intent.TargetAppId, StringComparison.Ordinal)
            && !HasAppsLaunch(request.CallerAppId, request.UserId, principal.Authority))
        {
            return new AppIntentDispatchResult(AppIntentDispatchStatus.CapabilityDenied, ErrorCode: "intent.capability-denied");
        }

        AppLaunchResult launch = await _orchestrator.LaunchAsync(new AppLaunchRequest(intent.TargetAppId, principal, intent.Arguments));
        return ToDispatchResult(launch);
    }

    private async ValueTask<AppIntentDispatchResult> DispatchOpenFileAsync(
        AppIntentRequest request, OpenFileIntent intent, AuthenticatedPrincipal principal)
    {
        if (!HasAppsLaunch(request.CallerAppId, request.UserId, principal.Authority))
        {
            return new AppIntentDispatchResult(AppIntentDispatchStatus.CapabilityDenied, ErrorCode: "intent.capability-denied");
        }

        FileHandlerResolution resolution = await _associations.ResolveAsync(intent, principal);
        switch (resolution.Status)
        {
            case FileHandlerResolutionStatus.ChooserRequired:
                return new AppIntentDispatchResult(AppIntentDispatchStatus.ChooserRequired, CandidateAppIds: resolution.CandidateAppIds, ErrorCode: "intent.open-file.chooser-required");
            case FileHandlerResolutionStatus.NoHandler:
                return new AppIntentDispatchResult(AppIntentDispatchStatus.NotFound, ErrorCode: "intent.open-file.no-handler");
            case FileHandlerResolutionStatus.TargetInvalid:
                return new AppIntentDispatchResult(AppIntentDispatchStatus.TargetInvalid, ErrorCode: "intent.open-file.target-invalid");
        }

        AppLaunchResult launch = await _orchestrator.LaunchAsync(
            new AppLaunchRequest(resolution.AppId!, principal, [intent.Path.Value]));
        return ToDispatchResult(launch);
    }

    /// <summary>
    /// Runs <see cref="ExecuteCommandIntent.CommandLine"/> in a non-interactive shell (ADR 0050,
    /// section 7): same grammar as the terminal, no history, output buffered in the result. The
    /// caller's <c>apps.launch</c> is checked once for the whole line.
    /// </summary>
    private async ValueTask<AppIntentDispatchResult> DispatchExecuteCommandAsync(
        AppIntentRequest request,
        ExecuteCommandIntent intent,
        AuthenticatedPrincipal principal,
        IFullScreenTerminalSession? fullScreen,
        IDangerousCapabilityApprovalPrompt? approvalPrompt,
        IAppExecutionContext? callerContext,
        CancellationToken cancellationToken)
    {
        if (!HasAppsLaunch(request.CallerAppId, request.UserId, principal.Authority))
        {
            return new AppIntentDispatchResult(AppIntentDispatchStatus.CapabilityDenied, ErrorCode: "intent.capability-denied");
        }

        if (string.IsNullOrWhiteSpace(intent.CommandLine))
        {
            return new AppIntentDispatchResult(AppIntentDispatchStatus.NotFound, ErrorCode: "intent.execute-command.empty");
        }

        ShellSession session;
        if (callerContext is not null)
        {
            session = new ShellSession(callerContext, principal, isInteractive: false);
            if (intent.WorkingDirectory is { } directory)
            {
                // The shell is the caller's own process: starting elsewhere changes its directory,
                // as `cd dir && …` would.
                callerContext.Environment.SetCurrentDirectory(directory);
            }
        }
        else
        {
            session = ShellSession.CreateDetached(principal, intent.WorkingDirectory, Processes.LoginEnvironment.DefaultHostName);
        }

        using StringWriter standardOutput = new();
        using StringWriter standardError = new();
        ShellExecutionResult result = await _shell.ExecuteAsync(
            session,
            intent.CommandLine,
            new ShellExecutionOptions
            {
                StandardOutput = standardOutput,
                StandardError = standardError,
                FullScreen = fullScreen,
                ApprovalPrompt = approvalPrompt,
                Elevation = intent.ElevationToken is Guid token && intent.ElevationScope is ElevationScope scope
                    ? new ShellElevation(token, scope)
                    : null
            },
            cancellationToken);

        AppIntentDispatchStatus status = result.Failure switch
        {
            ShellFailure.CommandNotFound => AppIntentDispatchStatus.NotFound,
            ShellFailure.Disabled => AppIntentDispatchStatus.Disabled,
            ShellFailure.ElevationDenied => AppIntentDispatchStatus.ElevationDenied,
            ShellFailure.Fault => AppIntentDispatchStatus.EntryPointFault,
            _ => AppIntentDispatchStatus.Dispatched
        };
        string? errorCode = result.Failure switch
        {
            ShellFailure.CommandNotFound => "intent.execute-command.not-found",
            ShellFailure.Disabled => "intent.execute-command.disabled",
            ShellFailure.ElevationDenied => "lifecycle.elevation.denied",
            ShellFailure.Fault => "lifecycle.entry-point.fault",
            ShellFailure.Redirection => "shell.redirection.failed",
            ShellFailure.Syntax => result.SyntaxError!.Code,
            _ => null
        };

        return new AppIntentDispatchResult(
            status,
            ExitCode: result.ExitCode,
            StandardOutput: standardOutput.ToString(),
            StandardError: standardError.ToString(),
            ErrorCode: errorCode,
            PermissionDenials: result.PermissionDenials);
    }

    /// <summary>
    /// Reveals a file in its folder (ADR 0096, section 1; <c>org.freedesktop.FileManager1.ShowItems</c>):
    /// the default handler of <c>inode/directory</c> starts on the parent with <c>--select=&lt;path&gt;</c>.
    /// Like opening the folder, it needs <c>apps.launch</c>; the acting user must be able to read the
    /// parent, as a file manager running as them would.
    /// </summary>
    private async ValueTask<AppIntentDispatchResult> DispatchRevealAsync(
        AppIntentRequest request, RevealFileIntent intent, AuthenticatedPrincipal principal, CancellationToken cancellationToken)
    {
        if (!HasAppsLaunch(request.CallerAppId, request.UserId, principal.Authority))
        {
            return new AppIntentDispatchResult(AppIntentDispatchStatus.CapabilityDenied, ErrorCode: "intent.capability-denied");
        }

        FileSystemAuthorizationContext access = PrincipalFileAccess.ForUser(principal, request.CallerAppId);
        FileSystemResult<FileSystemEntrySnapshot> stat = await _fileSystem
            .StatAsync(new FileSystemStatRequest(intent.Path, FileSystemLinkBehavior.NoFollow), access, cancellationToken)
            .ConfigureAwait(false);
        if (!stat.Succeeded)
        {
            return stat.Error?.Code == FileSystemErrorCode.PermissionDenied
                ? new AppIntentDispatchResult(AppIntentDispatchStatus.AccessDenied, ErrorCode: "intent.reveal-file.denied")
                : new AppIntentDispatchResult(AppIntentDispatchStatus.NotFound, ErrorCode: "intent.reveal-file.not-found");
        }

        VirtualPath parent = GetParent(intent.Path);
        FileSystemResult<FileSystemDirectorySnapshot> listing = await _fileSystem
            .EnumerateAsync(new FileSystemEnumerateRequest(parent), access, cancellationToken)
            .ConfigureAwait(false);
        if (!listing.Succeeded)
        {
            return listing.Error?.Code == FileSystemErrorCode.PermissionDenied
                ? new AppIntentDispatchResult(AppIntentDispatchStatus.AccessDenied, ErrorCode: "intent.reveal-file.denied")
                : new AppIntentDispatchResult(AppIntentDispatchStatus.NotFound, ErrorCode: "intent.reveal-file.not-found");
        }

        FileHandlerResolution resolution = await _associations.ResolveAsync(
            new OpenFileIntent(parent, FileIntentAction.Open, DirectoryMediaType), principal, cancellationToken).ConfigureAwait(false);
        if (resolution.Status == FileHandlerResolutionStatus.ChooserRequired)
        {
            return new AppIntentDispatchResult(AppIntentDispatchStatus.ChooserRequired, CandidateAppIds: resolution.CandidateAppIds, ErrorCode: "intent.reveal-file.chooser-required");
        }

        if (resolution.AppId is not { } handler)
        {
            return new AppIntentDispatchResult(AppIntentDispatchStatus.NotFound, ErrorCode: "intent.reveal-file.no-handler");
        }

        string[] arguments = [parent.Value, SelectArgumentPrefix + intent.Path.Value];
        AppLaunchResult launch = await _orchestrator.LaunchAsync(new AppLaunchRequest(
            handler, principal, arguments,
            Intent: new AppIntentDelivery(AppIntentIds.RevealFile, SenderAppId: request.CallerAppId) { Arguments = arguments }));
        return ToDispatchResult(launch);
    }

    /// <summary>
    /// Edits a file (ADR 0096, section 2): the <c>edit</c> handlers first, with the precedence of
    /// <see cref="OpenFileIntent"/>, then the <c>open</c> handlers when no app declares <c>edit</c>.
    /// </summary>
    private async ValueTask<AppIntentDispatchResult> DispatchEditFileAsync(
        AppIntentRequest request, EditFileIntent intent, AuthenticatedPrincipal principal)
    {
        if (!HasAppsLaunch(request.CallerAppId, request.UserId, principal.Authority))
        {
            return new AppIntentDispatchResult(AppIntentDispatchStatus.CapabilityDenied, ErrorCode: "intent.capability-denied");
        }

        FileHandlerResolution resolution = await _associations.ResolveAsync(
            new OpenFileIntent(intent.Path, FileIntentAction.Edit, intent.MediaType, intent.PreferredAppId), principal);
        bool fellBack = false;
        if (resolution.Status == FileHandlerResolutionStatus.NoHandler)
        {
            resolution = await _associations.ResolveAsync(
                new OpenFileIntent(intent.Path, FileIntentAction.Open, intent.MediaType), principal);
            fellBack = true;
        }

        switch (resolution.Status)
        {
            case FileHandlerResolutionStatus.ChooserRequired:
                return new AppIntentDispatchResult(AppIntentDispatchStatus.ChooserRequired, CandidateAppIds: resolution.CandidateAppIds, ErrorCode: "intent.edit-file.chooser-required", FellBackToOpen: fellBack);
            case FileHandlerResolutionStatus.NoHandler:
                return new AppIntentDispatchResult(AppIntentDispatchStatus.NotFound, ErrorCode: "intent.edit-file.no-handler");
            case FileHandlerResolutionStatus.TargetInvalid:
                return new AppIntentDispatchResult(AppIntentDispatchStatus.TargetInvalid, ErrorCode: "intent.edit-file.target-invalid");
        }

        string[] arguments = [intent.Path.Value];
        AppLaunchResult launch = await _orchestrator.LaunchAsync(new AppLaunchRequest(
            resolution.AppId!, principal, arguments,
            Intent: new AppIntentDelivery(AppIntentIds.EditFile, SenderAppId: request.CallerAppId) { Arguments = arguments }));
        return ToDispatchResult(launch) with { FellBackToOpen = fellBack };
    }

    /// <summary>
    /// Opens a URI with the handler of its scheme (ADR 0096, section 3): <c>file://</c> becomes an
    /// <see cref="OpenFileIntent"/> on its path; any other scheme resolves the media type
    /// <c>x-scheme-handler/&lt;scheme&gt;</c>, and the handler starts with the URI as its argument.
    /// </summary>
    private async ValueTask<AppIntentDispatchResult> DispatchOpenUriAsync(
        AppIntentRequest request, OpenUriIntent intent, AuthenticatedPrincipal principal, CancellationToken cancellationToken)
    {
        if (!HasAppsLaunch(request.CallerAppId, request.UserId, principal.Authority))
        {
            return new AppIntentDispatchResult(AppIntentDispatchStatus.CapabilityDenied, ErrorCode: "intent.capability-denied");
        }

        if (intent.Uri is null || !intent.Uri.IsAbsoluteUri || string.IsNullOrEmpty(intent.Uri.Scheme))
        {
            return new AppIntentDispatchResult(AppIntentDispatchStatus.Invalid, ErrorCode: "intent.open-uri.invalid");
        }

        if (intent.Uri.IsFile || string.Equals(intent.Uri.Scheme, "file", StringComparison.OrdinalIgnoreCase))
        {
            VirtualPath path;
            try
            {
                path = VirtualPath.Parse(Uri.UnescapeDataString(intent.Uri.AbsolutePath));
            }
            catch (Exception exception) when (exception is FormatException or ArgumentException)
            {
                return new AppIntentDispatchResult(AppIntentDispatchStatus.Invalid, ErrorCode: "intent.open-uri.invalid");
            }

            return await DispatchOpenFileAsync(
                request, new OpenFileIntent(path, FileIntentAction.Open, PreferredAppId: intent.PreferredAppId), principal);
        }

        FileHandlerResolution resolution = await _associations
            .ResolveSchemeAsync(intent.Uri.Scheme, principal, intent.PreferredAppId, cancellationToken)
            .ConfigureAwait(false);
        switch (resolution.Status)
        {
            case FileHandlerResolutionStatus.ChooserRequired:
                return new AppIntentDispatchResult(AppIntentDispatchStatus.ChooserRequired, CandidateAppIds: resolution.CandidateAppIds, ErrorCode: "intent.open-uri.chooser-required");
            case FileHandlerResolutionStatus.NoHandler:
                return new AppIntentDispatchResult(AppIntentDispatchStatus.NotFound, ErrorCode: "intent.open-uri.no-handler");
            case FileHandlerResolutionStatus.TargetInvalid:
                return new AppIntentDispatchResult(AppIntentDispatchStatus.TargetInvalid, ErrorCode: "intent.open-uri.target-invalid");
        }

        string[] arguments = [intent.Uri.OriginalString];
        AppLaunchResult launch = await _orchestrator.LaunchAsync(new AppLaunchRequest(
            resolution.AppId!, principal, arguments,
            Intent: new AppIntentDelivery(AppIntentIds.OpenUri, SenderAppId: request.CallerAppId) { Arguments = arguments }));
        return ToDispatchResult(launch);
    }

    /// <summary>
    /// Shows an app's settings (ADR 0096, section 4): the app itself with <c>--show-settings</c>
    /// when it declares the intent in its manifest, otherwise Settings on the app's page
    /// (<c>--app=&lt;id&gt;</c>). It opens a system surface and carries no caller data, so like the
    /// freedesktop settings panels it needs no capability.
    /// </summary>
    private async ValueTask<AppIntentDispatchResult> DispatchShowSettingsAsync(
        AppIntentRequest request, ShowAppSettingsIntent intent, AuthenticatedPrincipal principal)
    {
        if (!_catalog.Manifests.TryGetValue(intent.TargetAppId, out AppManifest? target))
        {
            return new AppIntentDispatchResult(AppIntentDispatchStatus.NotFound, ErrorCode: "intent.show-settings.not-found");
        }

        if (!_enablement.IsEnabled(intent.TargetAppId))
        {
            return new AppIntentDispatchResult(AppIntentDispatchStatus.Disabled, ErrorCode: "intent.show-settings.disabled");
        }

        bool ownPage = target.Intents.Any(static declaration =>
            string.Equals(declaration.IntentId, AppIntentIds.ShowAppSettings, StringComparison.Ordinal));
        (string handler, string[] arguments) = ownPage
            ? (intent.TargetAppId, [ShowSettingsArgument])
            : (SettingsAppId, new[] { SettingsAppArgumentPrefix + intent.TargetAppId });
        AppLaunchResult launch = await _orchestrator.LaunchAsync(new AppLaunchRequest(
            handler, principal, arguments,
            Intent: new AppIntentDelivery(AppIntentIds.ShowAppSettings, SenderAppId: request.CallerAppId) { Arguments = arguments }));
        return launch.Status == AppLaunchStatus.NotFound && !ownPage
            ? new AppIntentDispatchResult(AppIntentDispatchStatus.NotFound, ErrorCode: "intent.show-settings.no-settings-app")
            : ToDispatchResult(launch);
    }

    /// <summary>
    /// Routes an app-defined intent (ADR 0096, section 5) to the installed apps declaring its id: the
    /// caller's chosen target, the only declaring app, or the user's "always use" choice in
    /// <c>~/.config/hackeros/intents.config</c>; otherwise the caller gets the candidates to show the
    /// "Open with" chooser. The handler receives the id and payload as its
    /// <see cref="IAppExecutionContext.Intent"/>, or through <see cref="IAppExecutionContext.IntentReceived"/>
    /// when it is a running single instance.
    /// </summary>
    private async ValueTask<AppIntentDispatchResult> DispatchCustomAsync(
        AppIntentRequest request, CustomIntent intent, AuthenticatedPrincipal principal, CancellationToken cancellationToken)
    {
        if (!AppIntentIds.IsWellFormed(intent.IntentId))
        {
            return new AppIntentDispatchResult(AppIntentDispatchStatus.Invalid, ErrorCode: "intent.custom.invalid-id");
        }

        if (AppIntentIds.IsBuiltIn(intent.IntentId))
        {
            // A built-in id has a typed record with its own rules: sending it untyped would bypass them.
            return new AppIntentDispatchResult(AppIntentDispatchStatus.Invalid, ErrorCode: "intent.custom.reserved-id");
        }

        if (!HasAppsLaunch(request.CallerAppId, request.UserId, principal.Authority))
        {
            return new AppIntentDispatchResult(AppIntentDispatchStatus.CapabilityDenied, ErrorCode: "intent.capability-denied");
        }

        if (ValidatePayload(intent.PayloadJson) is { } payloadError)
        {
            return new AppIntentDispatchResult(AppIntentDispatchStatus.Invalid, ErrorCode: payloadError);
        }

        string[] candidates = [.. _catalog.Manifests.Values
            .Where(manifest => _enablement.IsEnabled(manifest.Id)
                && manifest.Intents.Any(declaration => string.Equals(declaration.IntentId, intent.IntentId, StringComparison.Ordinal)))
            .Select(static manifest => manifest.Id)
            .OrderBy(static id => id, StringComparer.Ordinal)];
        if (candidates.Length == 0)
        {
            return new AppIntentDispatchResult(AppIntentDispatchStatus.NotFound, ErrorCode: "intent.unknown");
        }

        string? handler;
        if (intent.TargetAppId is { } target)
        {
            if (!candidates.Contains(target, StringComparer.Ordinal))
            {
                return new AppIntentDispatchResult(AppIntentDispatchStatus.TargetInvalid, ErrorCode: "intent.custom.target-invalid");
            }

            handler = target;
            if (intent.RememberChoice)
            {
                await IntentChoiceStore.WriteAsync(_fileSystem, principal, intent.IntentId, target, cancellationToken).ConfigureAwait(false);
            }
        }
        else if (candidates.Length == 1)
        {
            handler = candidates[0];
        }
        else
        {
            string? remembered = await IntentChoiceStore.ReadAsync(_fileSystem, principal, intent.IntentId, cancellationToken).ConfigureAwait(false);
            handler = remembered is not null && candidates.Contains(remembered, StringComparer.Ordinal) ? remembered : null;
            if (handler is null)
            {
                return new AppIntentDispatchResult(AppIntentDispatchStatus.ChooserRequired, CandidateAppIds: candidates, ErrorCode: "intent.custom.chooser-required");
            }
        }

        AppLaunchResult launch = await _orchestrator.LaunchAsync(new AppLaunchRequest(
            handler, principal, [],
            Intent: new AppIntentDelivery(intent.IntentId, intent.PayloadJson, request.CallerAppId)));
        return ToDispatchResult(launch);
    }

    /// <summary>
    /// Checks an app-defined intent's payload (ADR 0096, section 5): a JSON object of at most 64 KiB.
    /// No JSON Schema validation: <c>PayloadSchemaAssetPath</c> documents the contract for callers.
    /// </summary>
    /// <returns>The error code, or <see langword="null"/> when the payload is acceptable.</returns>
    private static string? ValidatePayload(string? payloadJson)
    {
        if (payloadJson is null)
        {
            return null;
        }

        if (System.Text.Encoding.UTF8.GetByteCount(payloadJson) > AppIntentIds.MaxCustomPayloadBytes)
        {
            return "intent.custom.payload-too-large";
        }

        try
        {
            using System.Text.Json.JsonDocument document = System.Text.Json.JsonDocument.Parse(payloadJson);
            return document.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object ? null : "intent.custom.payload-invalid";
        }
        catch (System.Text.Json.JsonException)
        {
            return "intent.custom.payload-invalid";
        }
    }

    private static VirtualPath GetParent(VirtualPath path)
    {
        int separator = path.Value.LastIndexOf('/');
        return separator <= 0 ? VirtualPath.Parse("/") : VirtualPath.Parse(path.Value[..separator]);
    }

    /// <summary>
    /// Starts one <see cref="AppKind.Service"/> app on behalf of a caller, authorized either
    /// because the caller's entry point lives in the same assembly as the target service (a
    /// companion Window/Terminal app controlling its own bundled service) or because the caller
    /// holds <see cref="AppCapabilities.ServicesManage"/> (a "service manager" role, e.g. System
    /// Monitor). Refuses when the target's effective start mode is <see cref="ServiceStartMode.Disabled"/>.
    /// </summary>
    public async ValueTask<ServiceControlDispatchResult> StartServiceAsync(
        string callerAppId, string userId, string targetAppId, AuthenticatedPrincipal principal)
    {
        ServiceControlDispatchResult? denial = AuthorizeServiceControl(callerAppId, userId, targetAppId, principal, out AppManifest? target);
        if (denial is not null)
        {
            return denial;
        }

        bool system = IsBootService(target!);
        if (system && !principal.HasAuthority(AppAuthority.Administrator))
        {
            return AdministratorRequired();
        }

        ServiceStartMode effectiveMode = await ReadStartModeAsync(target!, principal, system);
        if (effectiveMode == ServiceStartMode.Disabled)
        {
            return new ServiceControlDispatchResult(ServiceControlDispatchStatus.ServiceDisabled, ErrorCode: "service.start.disabled");
        }

        // A system service runs as hackeros-svc in the system scope whoever starts it, like
        // `sudo systemctl start` of a system unit: the administrator's session does not own it.
        AppLaunchRequest request = system
            ? new AppLaunchRequest(
                targetAppId,
                ServiceAccount.CreatePrincipal(principal.InstallationId, principal.DeviceId, DateTimeOffset.UtcNow),
                [],
                Interactive: false,
                SystemScope: true)
            : new AppLaunchRequest(targetAppId, principal, []);
        AppLaunchResult launch = await _orchestrator.LaunchAsync(request);
        return launch.Status switch
        {
            AppLaunchStatus.Launched or AppLaunchStatus.FocusedExisting =>
                new ServiceControlDispatchResult(ServiceControlDispatchStatus.Succeeded),
            _ => new ServiceControlDispatchResult(ServiceControlDispatchStatus.Faulted, ErrorCode: launch.ErrorCode)
        };
    }

    /// <summary>Stops one <see cref="AppKind.Service"/> app on behalf of a caller; see <see cref="StartServiceAsync"/> for the authorization rule.</summary>
    public async ValueTask<ServiceControlDispatchResult> StopServiceAsync(
        string callerAppId, string userId, string targetAppId, AuthenticatedPrincipal principal)
    {
        ServiceControlDispatchResult? denial = AuthorizeServiceControl(callerAppId, userId, targetAppId, principal, out AppManifest? target);
        if (denial is not null)
        {
            return denial;
        }

        if (IsBootService(target!) && !principal.HasAuthority(AppAuthority.Administrator))
        {
            return AdministratorRequired();
        }

        await _orchestrator.StopServiceAsync(targetAppId);
        return new ServiceControlDispatchResult(ServiceControlDispatchStatus.Succeeded);
    }

    /// <summary>Reads one <see cref="AppKind.Service"/> app's effective start mode on behalf of a caller; see <see cref="StartServiceAsync"/> for the authorization rule.</summary>
    public async ValueTask<(ServiceControlDispatchResult Result, ServiceStartMode Mode)> GetServiceStartModeAsync(
        string callerAppId, string userId, string targetAppId, AuthenticatedPrincipal principal)
    {
        ServiceControlDispatchResult? denial = AuthorizeServiceControl(callerAppId, userId, targetAppId, principal, out AppManifest? target);
        if (denial is not null)
        {
            return (denial, ServiceStartMode.Manual);
        }

        ServiceStartMode mode = await ReadStartModeAsync(target!, principal, IsBootService(target!));
        return (new ServiceControlDispatchResult(ServiceControlDispatchStatus.Succeeded), mode);
    }

    /// <summary>Sets one <see cref="AppKind.Service"/> app's effective start mode on behalf of a caller; see <see cref="StartServiceAsync"/> for the authorization rule.</summary>
    public async ValueTask<ServiceControlDispatchResult> SetServiceStartModeAsync(
        string callerAppId, string userId, string targetAppId, AuthenticatedPrincipal principal, ServiceStartMode mode)
    {
        ServiceControlDispatchResult? denial = AuthorizeServiceControl(callerAppId, userId, targetAppId, principal, out AppManifest? target);
        if (denial is not null)
        {
            return denial;
        }

        if (IsBootService(target!))
        {
            // A system service's start mode lives in /etc, for every user: `sudo systemctl enable`.
            if (!principal.HasAuthority(AppAuthority.Administrator))
            {
                return AdministratorRequired();
            }

            await ServiceStartModeStore.WriteSystemAsync(_fileSystem, targetAppId, mode, CancellationToken.None);
        }
        else
        {
            await ServiceStartModeStore.WriteAsync(_fileSystem, principal.LoginName.Value, targetAppId, mode, CancellationToken.None);
        }

        return new ServiceControlDispatchResult(ServiceControlDispatchStatus.Succeeded);
    }

    /// <summary>Gets whether a service is a system service, started at boot as <c>hackeros-svc</c> (ADR 0097, section 4).</summary>
    private bool IsBootService(AppManifest target) =>
        _orchestrator.GetEffectiveStartup(target) == AppServiceStartup.Boot;

    /// <summary>Reads a service's start mode from <c>/etc</c> (system service) or the user's <c>~/.config</c>.</summary>
    private ValueTask<ServiceStartMode> ReadStartModeAsync(AppManifest target, AuthenticatedPrincipal principal, bool system)
    {
        ServiceStartMode fallback = target.AutoStart ? ServiceStartMode.Automatic : ServiceStartMode.Manual;
        return system
            ? ServiceStartModeStore.ReadSystemAsync(_fileSystem, target.Id, fallback, CancellationToken.None)
            : ServiceStartModeStore.ReadAsync(_fileSystem, principal.LoginName.Value, target.Id, fallback, CancellationToken.None);
    }

    private static ServiceControlDispatchResult AdministratorRequired() =>
        new(ServiceControlDispatchStatus.CapabilityDenied, ErrorCode: "service.system.administrator-required");

    /// <summary>
    /// Reads one <see cref="AppKind.Service"/> app's state (ADR 0097). Needs no capability, like
    /// <c>systemctl status</c>; an unknown app or one that is not a service reads as never started.
    /// </summary>
    public ServiceStatusSnapshot GetServiceStatus(string targetAppId) =>
        _catalog.Manifests.TryGetValue(targetAppId, out AppManifest? manifest) && manifest.Kind == AppKind.Service
            ? _orchestrator.GetServiceStatus(targetAppId)
            : ServiceStatusSnapshot.NeverStarted;

    /// <summary>Clears one <see cref="AppKind.Service"/> app's failed state on behalf of a caller; see <see cref="StartServiceAsync"/> for the authorization rule.</summary>
    public ServiceControlDispatchResult ResetFailedService(
        string callerAppId, string userId, string targetAppId, AuthenticatedPrincipal principal)
    {
        ServiceControlDispatchResult? denial = AuthorizeServiceControl(callerAppId, userId, targetAppId, principal, out _);
        if (denial is not null)
        {
            return denial;
        }

        _orchestrator.ResetFailedService(targetAppId);
        return new ServiceControlDispatchResult(ServiceControlDispatchStatus.Succeeded);
    }

    /// <summary>
    /// Resolves and authorizes a service-control target: it must be a catalog <see cref="AppKind.Service"/>
    /// app, and the caller must either share its resolved entry-point assembly or hold
    /// <see cref="AppCapabilities.ServicesManage"/>. Returns <see langword="null"/> and the resolved
    /// manifest when authorized; otherwise returns the denial result to return to the caller.
    /// </summary>
    private ServiceControlDispatchResult? AuthorizeServiceControl(
        string callerAppId, string userId, string targetAppId, AuthenticatedPrincipal principal, out AppManifest? target)
    {
        target = null;
        if (!_catalog.Manifests.TryGetValue(targetAppId, out AppManifest? manifest))
        {
            return new ServiceControlDispatchResult(ServiceControlDispatchStatus.NotFound, ErrorCode: "service.not-found");
        }

        if (manifest.Kind != AppKind.Service)
        {
            return new ServiceControlDispatchResult(ServiceControlDispatchStatus.NotAService, ErrorCode: "service.not-a-service");
        }

        bool sameAssembly =
            _orchestrator.TryGetDescriptor(callerAppId, out AppDescriptor? caller)
            && _orchestrator.TryGetDescriptor(targetAppId, out AppDescriptor? targetDescriptor)
            && ReferenceEquals(caller.Assembly, targetDescriptor.Assembly);

        bool hasServicesManage = _grants.Evaluate(
            callerAppId, userId, AppCapabilities.ServicesManage, principal.Authority, AppAuthority.User).Granted;

        if (!sameAssembly && !hasServicesManage)
        {
            return new ServiceControlDispatchResult(ServiceControlDispatchStatus.CapabilityDenied, ErrorCode: "service.capability-denied");
        }

        target = manifest;
        return null;
    }

    private static AppIntentDispatchResult ToDispatchResult(AppLaunchResult launch) => launch.Status switch
    {
        AppLaunchStatus.Launched or AppLaunchStatus.FocusedExisting => new AppIntentDispatchResult(
            AppIntentDispatchStatus.Dispatched, launch.Process, launch.Context, launch.ExitCode, launch.StandardOutput,
            launch.StandardError, PermissionDenials: launch.PermissionDenials),
        AppLaunchStatus.NotFound => new AppIntentDispatchResult(AppIntentDispatchStatus.NotFound, ErrorCode: launch.ErrorCode),
        AppLaunchStatus.Disabled => new AppIntentDispatchResult(AppIntentDispatchStatus.Disabled, ErrorCode: launch.ErrorCode),
        AppLaunchStatus.ElevationDenied => new AppIntentDispatchResult(AppIntentDispatchStatus.ElevationDenied, ErrorCode: launch.ErrorCode),
        _ => new AppIntentDispatchResult(AppIntentDispatchStatus.EntryPointFault, launch.Process, launch.Context, ErrorCode: launch.ErrorCode)
    };
}
