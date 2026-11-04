using HackerOs.App.Abstractions;
using HackerOs.AppSdk.IO;
using HackerOs.Simulation.Abstractions.FileSystem;
using HackerOs.Simulation.Abstractions.Gateways;

namespace HackerOs.AppSdk.Apps;

/// <summary>
/// The catalog of installed apps, and launching them (ADR 0056, section 2). Replaces
/// <c>AppCatalog</c> and <c>IAppEnablementRegistry</c>, which apps may no longer inject (ADR 0055).
/// </summary>
/// <remarks>
/// Reading the catalog needs no capability; the default handlers need <c>file-associations.read</c>
/// (and <c>filesystem.user-home.write</c> to change the user's own, ADR 0121); <see cref="LaunchAsync"/>, <see cref="OpenFileAsync"/>,
/// <see cref="EditFileAsync"/>, <see cref="RevealFileAsync"/>, <see cref="OpenUriAsync(Uri, string?, CancellationToken)"/>
/// and <see cref="SendIntentAsync"/> need <c>apps.launch</c>; <see cref="ShowSettingsAsync"/> needs none
/// (ADR 0096). Every member targets the app running the current code (ambient context of
/// ADR 0043).
/// </remarks>
public static class HackApps
{
    /// <summary>
    /// Raised after an app was installed, uninstalled or reinstalled. Subscribing and unsubscribing
    /// both need the app's ambient scope; a component that unsubscribes in <c>Dispose</c> should use
    /// <c>AppContext.Catalog.Changed</c> instead.
    /// </summary>
    /// <exception cref="HackExecutionContextMissingException">No app context is active.</exception>
    public static event EventHandler? Changed
    {
        add => Gateway.Changed += value;
        remove => Gateway.Changed -= value;
    }

    /// <summary>Gets every app installed for the current user, sorted by name.</summary>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <exception cref="HackExecutionContextMissingException">No app context is active.</exception>
    public static async Task<IReadOnlyList<HackAppInfo>> GetInstalledAsync(CancellationToken cancellationToken = default) =>
        await Gateway.GetInstalledAsync(cancellationToken).ConfigureAwait(false);

    /// <summary>Gets the installed apps that can open <paramref name="path"/>, the configured default first.</summary>
    /// <param name="path">Absolute, relative or <c>~</c>-prefixed path; the file need not exist.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <exception cref="ArgumentException">The path is empty or malformed.</exception>
    /// <exception cref="HackExecutionContextMissingException">No app context is active.</exception>
    public static async Task<IReadOnlyList<HackAppInfo>> GetHandlersAsync(string path, CancellationToken cancellationToken = default)
    {
        VirtualPath target = HackPath.Resolve(path, nameof(path));
        return await Gateway.GetHandlersAsync(target, null, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Gets the configured default handlers (ADR 0121): the user's own first, then the machine's.
    /// The first row of a target and action is the one the OS uses.
    /// </summary>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <exception cref="HackUnauthorizedAccessException">This app lacks <c>file-associations.read</c>.</exception>
    /// <exception cref="HackExecutionContextMissingException">No app context is active.</exception>
    public static async Task<IReadOnlyList<HackFileAssociation>> GetDefaultHandlersAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await Gateway.GetDefaultHandlersAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (AppGatewayAccessDeniedException exception)
        {
            throw HackGatewayErrors.ToUnauthorized(exception);
        }
    }

    /// <summary>
    /// Makes <paramref name="appId"/> the user's default for a file type or URI scheme (ADR 0121),
    /// like <c>xdg-mime default</c>: written to <c>~/.config/hackeros/file-associations.json</c>,
    /// it comes before the machine's default for this user only.
    /// </summary>
    /// <param name="target">Extension with its dot (<c>.md</c>) or media type (<c>text/markdown</c>, <c>x-scheme-handler/https</c>).</param>
    /// <param name="appId">App that must declare the target and action in its manifest.</param>
    /// <param name="action"><c>open</c> (default) or <c>edit</c>.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns><see cref="HackDefaultHandlerStatus.Changed"/>, or why nothing changed.</returns>
    /// <exception cref="HackUnauthorizedAccessException">This app lacks <c>file-associations.read</c> or <c>filesystem.user-home.write</c>.</exception>
    /// <exception cref="HackExecutionContextMissingException">No app context is active.</exception>
    public static Task<HackDefaultHandlerStatus> SetDefaultHandlerAsync(
        string target, string appId, string action = "open", CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        ArgumentException.ThrowIfNullOrWhiteSpace(appId);
        return ChangeDefaultHandlerAsync(target, action, appId, cancellationToken);
    }

    /// <summary>
    /// Removes the user's own default for a file type or URI scheme (ADR 0121): the machine's
    /// default, or the chooser, applies again.
    /// </summary>
    /// <param name="target">Extension with its dot or media type, as given to <see cref="SetDefaultHandlerAsync"/>.</param>
    /// <param name="action"><c>open</c> (default) or <c>edit</c>.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <exception cref="HackUnauthorizedAccessException">This app lacks <c>file-associations.read</c> or <c>filesystem.user-home.write</c>.</exception>
    /// <exception cref="HackExecutionContextMissingException">No app context is active.</exception>
    public static Task<HackDefaultHandlerStatus> ResetDefaultHandlerAsync(
        string target, string action = "open", CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        return ChangeDefaultHandlerAsync(target, action, null, cancellationToken);
    }

    /// <summary>Starts an installed app with arguments, like running it from the launcher.</summary>
    /// <param name="appId">Exact app ID.</param>
    /// <param name="arguments">Arguments passed to the new process (file paths for a window app).</param>
    /// <param name="cancellationToken">Cancels the launch.</param>
    /// <exception cref="HackAppLaunchException">The app is unknown, uninstalled, or failed to start.</exception>
    /// <exception cref="HackUnauthorizedAccessException">This app lacks <c>apps.launch</c>.</exception>
    /// <exception cref="HackExecutionContextMissingException">No app context is active.</exception>
    public static async Task LaunchAsync(
        string appId, IReadOnlyList<string>? arguments = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appId);
        IAppIntentGateway intents = HackExecutionContext.Current.Intents;
        AppIntentLaunchResult result;
        try
        {
            result = await intents.LaunchAsync(appId, arguments ?? [], cancellationToken).ConfigureAwait(false);
        }
        catch (AppGatewayAccessDeniedException exception)
        {
            throw HackGatewayErrors.ToUnauthorized(exception);
        }

        switch (result.Outcome)
        {
            case AppIntentLaunchOutcome.Launched:
                return;
            case AppIntentLaunchOutcome.NotFound:
                throw new HackAppLaunchException($"No app '{appId}' is installed.", appId, result.ErrorCode);
            case AppIntentLaunchOutcome.Disabled:
                throw new HackAppLaunchException($"The app '{appId}' is uninstalled.", appId, result.ErrorCode);
            default:
                throw new HackAppLaunchException($"The app '{appId}' could not be started ({result.ErrorCode}).", appId, result.ErrorCode);
        }
    }

    /// <summary>
    /// Opens a file with its handler, like double-clicking it: the configured default, or the only
    /// app that can open it. When several can and none is the default, nothing starts and the
    /// result lists them so the caller can ask the user.
    /// </summary>
    /// <param name="path">Absolute, relative or <c>~</c>-prefixed path of the file.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <exception cref="ArgumentException">The path is empty or malformed.</exception>
    /// <exception cref="HackAppLaunchException">The handler failed to start.</exception>
    /// <exception cref="HackUnauthorizedAccessException">This app lacks <c>apps.launch</c>.</exception>
    /// <exception cref="HackExecutionContextMissingException">No app context is active.</exception>
    public static async Task<HackOpenFileResult> OpenFileAsync(string path, CancellationToken cancellationToken = default)
    {
        VirtualPath target = HackPath.Resolve(path, nameof(path));
        IAppIntentGateway intents = HackExecutionContext.Current.Intents;
        AppIntentOpenFileResult result;
        try
        {
            result = await intents.OpenFileAsync(target, null, cancellationToken).ConfigureAwait(false);
        }
        catch (AppGatewayAccessDeniedException exception)
        {
            throw HackGatewayErrors.ToUnauthorized(exception);
        }

        return result.Outcome switch
        {
            AppIntentOpenFileOutcome.Opened => new HackOpenFileResult(HackOpenFileStatus.Opened, []),
            AppIntentOpenFileOutcome.ChooserRequired => new HackOpenFileResult(HackOpenFileStatus.ChooserRequired, result.CandidateAppIds ?? []),
            AppIntentOpenFileOutcome.NoHandler => new HackOpenFileResult(HackOpenFileStatus.NoHandler, []),
            _ => throw new HackAppLaunchException($"'{target.Value}' could not be opened ({result.ErrorCode}).", errorCode: result.ErrorCode)
        };
    }

    /// <summary>
    /// Edits a file (ADR 0096, section 2): with the app declaring <c>edit</c> for it, or, when none
    /// does, with the one that opens it (<see cref="HackOpenFileResult.FellBackToOpen"/>).
    /// </summary>
    /// <param name="path">Absolute, relative or <c>~</c>-prefixed path of the file.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <exception cref="ArgumentException">The path is empty or malformed.</exception>
    /// <exception cref="HackAppLaunchException">The handler failed to start.</exception>
    /// <exception cref="HackUnauthorizedAccessException">This app lacks <c>apps.launch</c>.</exception>
    /// <exception cref="HackExecutionContextMissingException">No app context is active.</exception>
    public static async Task<HackOpenFileResult> EditFileAsync(string path, CancellationToken cancellationToken = default)
    {
        VirtualPath target = HackPath.Resolve(path, nameof(path));
        AppIntentOpenFileResult result = await Call(intents => intents.EditFileAsync(target, null, cancellationToken)).ConfigureAwait(false);
        return result.Outcome switch
        {
            AppIntentOpenFileOutcome.Opened => new HackOpenFileResult(HackOpenFileStatus.Opened, []) { FellBackToOpen = result.FellBackToOpen },
            AppIntentOpenFileOutcome.ChooserRequired => new HackOpenFileResult(HackOpenFileStatus.ChooserRequired, result.CandidateAppIds ?? []) { FellBackToOpen = result.FellBackToOpen },
            AppIntentOpenFileOutcome.NoHandler => new HackOpenFileResult(HackOpenFileStatus.NoHandler, []),
            _ => throw new HackAppLaunchException($"'{target.Value}' could not be edited ({result.ErrorCode}).", errorCode: result.ErrorCode)
        };
    }

    /// <summary>
    /// Shows a file in its folder, selected (ADR 0096, section 1), like "Show in folder": the default
    /// file manager opens the parent folder and selects the item.
    /// </summary>
    /// <param name="path">Absolute, relative or <c>~</c>-prefixed path of the file or folder.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <exception cref="ArgumentException">The path is empty or malformed.</exception>
    /// <exception cref="HackFileNotFoundException">Nothing exists at <paramref name="path"/>.</exception>
    /// <exception cref="HackUnauthorizedAccessException">The user may not read the parent folder, or this app lacks <c>apps.launch</c>.</exception>
    /// <exception cref="HackAppLaunchException">No file manager is installed, or it failed to start.</exception>
    /// <exception cref="HackExecutionContextMissingException">No app context is active.</exception>
    public static async Task RevealFileAsync(string path, CancellationToken cancellationToken = default)
    {
        VirtualPath target = HackPath.Resolve(path, nameof(path));
        AppIntentResult result = await Call(intents => intents.RevealFileAsync(target, cancellationToken)).ConfigureAwait(false);
        switch (result.Outcome)
        {
            case AppIntentOutcome.Dispatched:
                return;
            case AppIntentOutcome.NotFound:
                throw new HackFileNotFoundException(target.Value);
            case AppIntentOutcome.AccessDenied:
                throw new HackUnauthorizedAccessException(
                    $"Access to the folder of '{target.Value}' is denied.", FileSystemErrorCode.PermissionDenied, target.Value);
            default:
                throw new HackAppLaunchException($"'{target.Value}' could not be shown ({result.ErrorCode}).", errorCode: result.ErrorCode);
        }
    }

    /// <summary>
    /// Opens a URI with the app that handles its scheme (ADR 0096, section 3), like <c>xdg-open</c>:
    /// the default web browser for <c>http</c>/<c>https</c>, the handler of the file for <c>file://</c>.
    /// Use <see cref="HackBrowser.OpenAsync"/> to open precisely the Kazo OS browser.
    /// </summary>
    /// <param name="uri">Absolute URI.</param>
    /// <param name="appId">The app to use, when the user already chose one in a chooser.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <exception cref="ArgumentException">The URI is not absolute.</exception>
    /// <exception cref="HackAppLaunchException">The handler failed to start.</exception>
    /// <exception cref="HackUnauthorizedAccessException">This app lacks <c>apps.launch</c>.</exception>
    /// <exception cref="HackExecutionContextMissingException">No app context is active.</exception>
    public static async Task<HackIntentResult> OpenUriAsync(Uri uri, string? appId = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (!uri.IsAbsoluteUri)
        {
            throw new ArgumentException("The URI must be absolute.", nameof(uri));
        }

        AppIntentResult result = await Call(intents => intents.OpenUriAsync(uri, appId, cancellationToken)).ConfigureAwait(false);
        return ToHackResult(result, uri.OriginalString);
    }

    /// <summary>Opens a URI written as text; see <see cref="OpenUriAsync(Uri, string?, CancellationToken)"/>.</summary>
    /// <param name="uri">Absolute URI such as <c>https://example.hack/</c>.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <exception cref="ArgumentException">The text is not an absolute URI.</exception>
    public static Task<HackIntentResult> OpenUriAsync(string uri, CancellationToken cancellationToken = default) =>
        Uri.TryCreate(uri, UriKind.Absolute, out Uri? parsed)
            ? OpenUriAsync(parsed, null, cancellationToken)
            : throw new ArgumentException($"'{uri}' is not an absolute URI.", nameof(uri));

    /// <summary>
    /// Shows an app's settings (ADR 0096, section 4): its own settings page when it declares one,
    /// otherwise its page in Settings (capabilities, file types, service, uninstall). Needs no capability.
    /// </summary>
    /// <param name="appId">Exact app ID; the current app's own when <see langword="null"/>.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <exception cref="HackAppLaunchException">The app is unknown or uninstalled, or the settings surface failed to start.</exception>
    /// <exception cref="HackExecutionContextMissingException">No app context is active.</exception>
    public static async Task ShowSettingsAsync(string? appId = null, CancellationToken cancellationToken = default)
    {
        string target = appId ?? HackExecutionContext.Current.Manifest.Id;
        AppIntentResult result = await Call(intents => intents.ShowAppSettingsAsync(target, cancellationToken)).ConfigureAwait(false);
        if (result.Outcome != AppIntentOutcome.Dispatched)
        {
            throw new HackAppLaunchException($"The settings of '{target}' could not be shown ({result.ErrorCode}).", target, result.ErrorCode);
        }
    }

    /// <summary>
    /// Sends an app-defined intent (ADR 0096, section 5) to the installed apps that declare it in
    /// their manifest: the only one, the one the user chose to "always use", or, when several can
    /// and none was chosen, nothing yet: the result lists them so the caller asks the user, then
    /// sends again with <paramref name="targetAppId"/> (a window app can use
    /// <c>WindowAppBase.SendIntentAsync</c>, which shows the chooser). The receiving app reads it as
    /// <c>AppContext.Intent</c> or, already open, in <c>OnIntentReceivedAsync</c>.
    /// </summary>
    /// <param name="intentId">Identifier <c>domain.reversed.name.vN</c>, e.g. <c>com.example.notes.share.v1</c>.</param>
    /// <param name="payloadJson">Optional JSON object of at most 64 KiB.</param>
    /// <param name="targetAppId">The app to deliver to, after the user chose one.</param>
    /// <param name="rememberChoice">With <paramref name="targetAppId"/>: always use it for this intent (<c>~/.config/hackeros/intents.config</c>).</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <exception cref="ArgumentException">The identifier is malformed or reserved, the payload is not a JSON object or is too large, or the target does not declare the intent.</exception>
    /// <exception cref="HackAppLaunchException">The handler failed to start.</exception>
    /// <exception cref="HackUnauthorizedAccessException">This app lacks <c>apps.launch</c>.</exception>
    /// <exception cref="HackExecutionContextMissingException">No app context is active.</exception>
    public static async Task<HackIntentResult> SendIntentAsync(
        string intentId,
        string? payloadJson = null,
        string? targetAppId = null,
        bool rememberChoice = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(intentId);
        AppIntentResult result = await Call(intents =>
            intents.SendIntentAsync(intentId, payloadJson, targetAppId, rememberChoice, cancellationToken)).ConfigureAwait(false);
        if (result.Outcome == AppIntentOutcome.Invalid)
        {
            throw new ArgumentException($"The intent '{intentId}' was refused ({result.ErrorCode}).", nameof(intentId));
        }

        return ToHackResult(result, intentId);
    }

    private static HackIntentResult ToHackResult(AppIntentResult result, string what) => result.Outcome switch
    {
        AppIntentOutcome.Dispatched => new HackIntentResult(HackIntentStatus.Delivered, []),
        AppIntentOutcome.ChooserRequired => new HackIntentResult(HackIntentStatus.ChooserRequired, result.CandidateAppIds ?? []),
        AppIntentOutcome.NoHandler => new HackIntentResult(HackIntentStatus.NoHandler, []),
        AppIntentOutcome.Invalid => throw new ArgumentException($"'{what}' was refused ({result.ErrorCode})."),
        _ => throw new HackAppLaunchException($"'{what}' could not be opened ({result.ErrorCode}).", errorCode: result.ErrorCode)
    };

    /// <summary>Calls the intents gateway of the current app, turning a missing <c>apps.launch</c> into the SDK's exception.</summary>
    private static async Task<T> Call<T>(Func<IAppIntentGateway, ValueTask<T>> call)
    {
        IAppIntentGateway intents = HackExecutionContext.Current.Intents;
        try
        {
            return await call(intents).ConfigureAwait(false);
        }
        catch (AppGatewayAccessDeniedException exception)
        {
            throw HackGatewayErrors.ToUnauthorized(exception);
        }
    }

    private static async Task<HackDefaultHandlerStatus> ChangeDefaultHandlerAsync(
        string target, string action, string? appId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(action);
        try
        {
            return await Gateway.SetUserDefaultHandlerAsync(target, action, appId, cancellationToken).ConfigureAwait(false);
        }
        catch (AppGatewayAccessDeniedException exception)
        {
            throw HackGatewayErrors.ToUnauthorized(exception);
        }
    }

    private static IAppCatalogGateway Gateway => HackExecutionContext.Current.Catalog;
}
