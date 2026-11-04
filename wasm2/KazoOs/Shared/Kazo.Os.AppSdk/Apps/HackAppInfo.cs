using Kazo.Os.App.Abstractions;

namespace Kazo.Os.AppSdk.Apps;

/// <summary>
/// Public description of one app of the catalog (ADR 0056, section 2): what a launcher, an
/// "Open with" list or a task manager shows. Like a <c>.desktop</c> file under
/// <c>/usr/share/applications</c>, it carries no entry point and no .NET type.
/// </summary>
/// <param name="Id">Stable app identifier (<c>org.hackeros.text-editor</c>).</param>
/// <param name="Name">Display name.</param>
/// <param name="Description">One-line description.</param>
/// <param name="Kind">Window, terminal command, or service.</param>
/// <param name="Category">Lower-kebab-case category used for grouping.</param>
/// <param name="Icon">Package-relative path of the smallest declared icon, or <see langword="null"/>.</param>
/// <param name="Version">Semantic version.</param>
/// <param name="PublisherId">Publisher identifier.</param>
/// <param name="IsSystemApp">Whether the host ships it as a system app (ADR 0048): never uninstallable.</param>
/// <param name="IsHidden">Whether the app is hidden from launchers (reachable only through intents).</param>
/// <param name="FileHandlers">File types the app declares it can open or edit.</param>
public sealed record HackAppInfo(
    string Id,
    string Name,
    string Description,
    AppKind Kind,
    string Category,
    string? Icon,
    string Version,
    string PublisherId,
    bool IsSystemApp,
    bool IsHidden,
    IReadOnlyList<HackAppFileHandler> FileHandlers)
{
    /// <summary>
    /// Gets the URLs a web app declares (<c>web.urls</c>, ADR 0073), main address first; empty for
    /// every other kind. Web Server Manager lists them as read-only "external" sites (ADR 0077 §4).
    /// </summary>
    public IReadOnlyList<string> Urls { get; init; } = [];

    /// <summary>
    /// Gets the capabilities the app's manifest declares (ADR 0096, section 4: the app's page in
    /// Settings). Declared is not granted: dangerous ones still need an administrator's decision.
    /// </summary>
    public IReadOnlyList<string> Capabilities { get; init; } = [];

    /// <summary>Builds the public description of a manifest. Called by the platform's catalog gateway.</summary>
    /// <param name="manifest">Validated manifest.</param>
    /// <param name="isSystemApp">Whether the host declares the app as a system app.</param>
    public static HackAppInfo FromManifest(AppManifest manifest, bool isSystemApp)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return new HackAppInfo(
            manifest.Id,
            manifest.Name,
            manifest.Description,
            manifest.Kind,
            manifest.Presentation.Category,
            manifest.Presentation.IconAssetPaths.Count > 0 ? manifest.Presentation.IconAssetPaths[0] : null,
            manifest.Version,
            manifest.PublisherId,
            isSystemApp,
            manifest.Presentation.LaunchVisibility == AppLaunchVisibility.Hidden,
            [.. manifest.FileHandlers.Select(static handler =>
                new HackAppFileHandler(handler.MediaType, [.. handler.Extensions], [.. handler.Actions]))])
        {
            Urls = manifest.Web is { } web ? [.. web.Urls] : [],
            Capabilities = [.. manifest.Capabilities]
        };
    }
}

/// <summary>One file type an app declares it handles.</summary>
/// <param name="MediaType">MIME type (<c>text/markdown</c>), or <see langword="null"/>.</param>
/// <param name="Extensions">Extensions with their leading dot (<c>.md</c>).</param>
/// <param name="Actions">Supported actions (<c>open</c>, <c>edit</c>).</param>
public sealed record HackAppFileHandler(string? MediaType, IReadOnlyList<string> Extensions, IReadOnlyList<string> Actions);

/// <summary>How <see cref="HackApps.OpenFileAsync"/> ended.</summary>
public enum HackOpenFileStatus
{
    /// <summary>A handler app was started with the file.</summary>
    Opened = 0,

    /// <summary>Several installed apps can open the file and none is the default: the caller lets the user choose.</summary>
    ChooserRequired = 1,

    /// <summary>No installed app can open the file.</summary>
    NoHandler = 2
}

/// <summary>Result of <see cref="HackApps.OpenFileAsync"/>.</summary>
/// <param name="Status">How the request ended.</param>
/// <param name="CandidateAppIds">With <see cref="HackOpenFileStatus.ChooserRequired"/>, the apps to choose from.</param>
public sealed record HackOpenFileResult(HackOpenFileStatus Status, IReadOnlyList<string> CandidateAppIds)
{
    /// <summary>
    /// Gets whether <see cref="HackApps.EditFileAsync"/> found no app declaring <c>edit</c> and used
    /// the <c>open</c> handlers instead, like <c>xdg-open</c> (ADR 0096, section 2).
    /// </summary>
    public bool FellBackToOpen { get; init; }
}

/// <summary>What happened to an intent sent with <see cref="HackApps"/> (ADR 0096).</summary>
public enum HackIntentStatus
{
    /// <summary>The handler was started, or the running instance received the intent.</summary>
    Delivered = 0,

    /// <summary>
    /// Several apps can handle it and none is the default or the remembered choice: nothing
    /// started, ask the user with <see cref="HackIntentResult.CandidateAppIds"/> and send again
    /// with the chosen app.
    /// </summary>
    ChooserRequired = 1,

    /// <summary>No installed app handles it.</summary>
    NoHandler = 2
}

/// <summary>Result of <see cref="HackApps.OpenUriAsync(Uri, string?, CancellationToken)"/>, <see cref="HackApps.SendIntentAsync"/> and the other intents of ADR 0096.</summary>
/// <param name="Status">What happened.</param>
/// <param name="CandidateAppIds">The apps to choose from, for <see cref="HackIntentStatus.ChooserRequired"/>.</param>
public sealed record HackIntentResult(HackIntentStatus Status, IReadOnlyList<string> CandidateAppIds);

/// <summary>An app could not be started: unknown, uninstalled, or failed while starting.</summary>
public sealed class HackAppLaunchException : InvalidOperationException
{
    /// <summary>Initializes the exception.</summary>
    /// <param name="message">Human-readable message.</param>
    /// <param name="appId">The app that could not be started, when known.</param>
    /// <param name="errorCode">Stable error code from the OS, when there is one.</param>
    public HackAppLaunchException(string message, string? appId = null, string? errorCode = null)
        : base(message)
    {
        AppId = appId;
        ErrorCode = errorCode;
    }

    /// <summary>Gets the app that could not be started, when known.</summary>
    public string? AppId { get; }

    /// <summary>Gets the stable error code from the OS, when there is one.</summary>
    public string? ErrorCode { get; }
}
