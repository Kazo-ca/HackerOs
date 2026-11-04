using System.Text.RegularExpressions;
using Kazo.Os.App.Abstractions.Policy;

namespace Kazo.Os.App.Abstractions;

/// <summary>
/// Identifies a versioned intent that crosses an application boundary.
/// </summary>
public interface IAppIntent
{
    /// <summary>Gets the stable namespaced intent identifier.</summary>
    string IntentId { get; }
}

/// <summary>
/// Defines stable identifiers for intents provided by the ecosystem.
/// </summary>
public static partial class AppIntentIds
{
    /// <summary>Launch an application directly.</summary>
    public const string LaunchApp = "org.hackeros.intent.launch-app.v1";

    /// <summary>Open or edit a virtual file through association resolution.</summary>
    public const string OpenFile = "org.hackeros.intent.open-file.v1";

    /// <summary>Reveal a virtual file in its containing folder.</summary>
    public const string RevealFile = "org.hackeros.intent.reveal-file.v1";

    /// <summary>Execute a command through an available terminal session.</summary>
    public const string ExecuteCommand = "org.hackeros.intent.execute-command.v1";

    /// <summary>Show an app's settings or status surface.</summary>
    public const string ShowAppSettings = "org.hackeros.intent.show-app-settings.v1";

    /// <summary>Edit a virtual file with a handler declaring the <c>edit</c> action (ADR 0096, section 2).</summary>
    public const string EditFile = "org.hackeros.intent.edit-file.v1";

    /// <summary>Open a URI with the handler of its scheme, <c>x-scheme-handler/&lt;scheme&gt;</c> (ADR 0096, section 3).</summary>
    public const string OpenUri = "org.hackeros.intent.open-uri.v1";

    /// <summary>
    /// Prefix of the media type that names the handler of one URI scheme, as under freedesktop
    /// (<c>x-scheme-handler/https</c>): an app declares it in its file handlers (ADR 0096, section 3).
    /// </summary>
    public const string SchemeHandlerMediaTypePrefix = "x-scheme-handler/";

    /// <summary>Largest payload of a <see cref="CustomIntent"/>, in UTF-8 bytes (64 KiB, ADR 0096 section 5).</summary>
    public const int MaxCustomPayloadBytes = 64 * 1024;

    /// <summary>
    /// Gets whether <paramref name="intentId"/> is a well-formed intent identifier: reverse-domain
    /// segments then a version, <c>domain.reversed.name.vN</c>, like an Android action
    /// (ADR 0096, section 5). Every built-in identifier matches.
    /// </summary>
    /// <param name="intentId">Candidate identifier.</param>
    public static bool IsWellFormed(string? intentId) =>
        intentId is not null && IntentIdPattern().IsMatch(intentId);

    /// <summary>Gets whether <paramref name="intentId"/> is one of the intents the OS itself handles.</summary>
    /// <param name="intentId">Candidate identifier.</param>
    public static bool IsBuiltIn(string intentId) => intentId is
        LaunchApp or OpenFile or RevealFile or ExecuteCommand or ShowAppSettings or EditFile or OpenUri;

    [GeneratedRegex("^[a-z][a-z0-9-]*(?:\\.[a-z][a-z0-9-]*)+\\.v[1-9][0-9]*$", RegexOptions.CultureInvariant)]
    private static partial Regex IntentIdPattern();
}

/// <summary>
/// Requests direct launch of an enabled application.
/// </summary>
/// <param name="TargetAppId">Immutable target application identifier.</param>
/// <param name="Arguments">Opaque command-line-style arguments supplied to the target.</param>
public sealed record LaunchAppIntent(string TargetAppId, IReadOnlyList<string> Arguments) : IAppIntent
{
    /// <inheritdoc />
    public string IntentId => AppIntentIds.LaunchApp;
}

/// <summary>
/// Identifies the operation requested for a virtual file.
/// </summary>
public enum FileIntentAction
{
    /// <summary>Open the file using its default read/view behavior.</summary>
    Open,

    /// <summary>Edit the file using a compatible application.</summary>
    Edit
}

/// <summary>
/// Requests association-based opening or editing of a virtual file.
/// </summary>
/// <param name="Path">Canonical virtual filesystem path.</param>
/// <param name="Action">Requested file operation.</param>
/// <param name="MediaType">Detected media type, when known.</param>
/// <param name="PreferredAppId">Explicit preferred handler, when supplied by the caller.</param>
public sealed record OpenFileIntent(
    VirtualPath Path,
    FileIntentAction Action,
    string? MediaType = null,
    string? PreferredAppId = null) : IAppIntent
{
    /// <inheritdoc />
    public string IntentId => AppIntentIds.OpenFile;
}

/// <summary>
/// Requests that a file manager reveal a virtual file.
/// </summary>
/// <param name="Path">Canonical path to reveal.</param>
public sealed record RevealFileIntent(VirtualPath Path) : IAppIntent
{
    /// <inheritdoc />
    public string IntentId => AppIntentIds.RevealFile;
}

/// <summary>
/// Requests command execution through the shell rather than a concrete terminal app.
/// </summary>
/// <param name="CommandLine">Command line to parse and execute.</param>
/// <param name="WorkingDirectory">Optional initial virtual working directory.</param>
/// <param name="ElevationToken">
/// Optional single-use elevation ticket token (`ADR 0042`), minted by <see cref="IElevationService"/>
/// after live Administrator re-authentication — e.g. by the terminal's <c>sudo</c> built-in, or by a
/// one-shot capability-approval retry. Consumed exactly once by the launch this intent triggers.
/// </param>
/// <param name="ElevationScope">The scope the ticket in <see cref="ElevationToken"/> authorizes.</param>
public sealed record ExecuteCommandIntent(
    string CommandLine,
    VirtualPath? WorkingDirectory = null,
    Guid? ElevationToken = null,
    ElevationScope? ElevationScope = null) : IAppIntent
{
    /// <inheritdoc />
    public string IntentId => AppIntentIds.ExecuteCommand;
}

/// <summary>
/// Requests an application's settings or service-status UI.
/// </summary>
/// <param name="TargetAppId">Application whose settings should be shown.</param>
public sealed record ShowAppSettingsIntent(string TargetAppId) : IAppIntent
{
    /// <inheritdoc />
    public string IntentId => AppIntentIds.ShowAppSettings;
}

/// <summary>
/// Requests that a virtual file be edited (ADR 0096, section 2): resolved among the handlers whose
/// actions include <c>edit</c>, with the same precedence as <see cref="OpenFileIntent"/>. Without
/// any <c>edit</c> handler it falls back to <c>open</c>, as <c>xdg-open</c> has no "edit" verb.
/// </summary>
/// <param name="Path">Canonical virtual filesystem path.</param>
/// <param name="MediaType">Detected media type, when known.</param>
/// <param name="PreferredAppId">Explicit preferred handler, when supplied by the caller.</param>
public sealed record EditFileIntent(
    VirtualPath Path,
    string? MediaType = null,
    string? PreferredAppId = null) : IAppIntent
{
    /// <inheritdoc />
    public string IntentId => AppIntentIds.EditFile;
}

/// <summary>
/// Requests that a URI be opened by the handler of its scheme (ADR 0096, section 3): the media type
/// <c>x-scheme-handler/&lt;scheme&gt;</c>, resolved like a file association. A <c>file://</c> URI is
/// an <see cref="OpenFileIntent"/> on its path.
/// </summary>
/// <param name="Uri">Absolute URI to open.</param>
/// <param name="PreferredAppId">Explicit preferred handler, when supplied by the caller (after a chooser).</param>
public sealed record OpenUriIntent(Uri Uri, string? PreferredAppId = null) : IAppIntent
{
    /// <inheritdoc />
    public string IntentId => AppIntentIds.OpenUri;
}

/// <summary>
/// An intent defined by apps rather than by the OS (ADR 0096, section 5): routed to the installed
/// apps that declare <paramref name="IntentId"/> in their manifest's <c>intents</c>.
/// </summary>
/// <param name="IntentId">Well-formed identifier (<see cref="AppIntentIds.IsWellFormed"/>).</param>
/// <param name="PayloadJson">Optional JSON object of at most <see cref="AppIntentIds.MaxCustomPayloadBytes"/>.</param>
/// <param name="TargetAppId">The app to deliver to, when the caller already chose one (after a chooser).</param>
/// <param name="RememberChoice">
/// With <paramref name="TargetAppId"/>: remember it as the handler of this intent in
/// <c>~/.config/hackeros/intents.config</c> ("always use", ADR 0096 section 5).
/// </param>
public sealed record CustomIntent(
    string IntentId,
    string? PayloadJson = null,
    string? TargetAppId = null,
    bool RememberChoice = false) : IAppIntent;

/// <summary>
/// What a process receives when the OS hands it an intent (ADR 0096, section 5): at launch through
/// <c>IAppExecutionContext.Intent</c>, and for an already running single instance through
/// <c>IAppExecutionContext.IntentReceived</c>, like a second <c>GApplication</c> activation.
/// </summary>
/// <param name="IntentId">Identifier of the delivered intent.</param>
/// <param name="PayloadJson">Payload of a <see cref="CustomIntent"/>, when it has one.</param>
/// <param name="SenderAppId">The app that sent it, when known.</param>
public sealed record AppIntentDelivery(string IntentId, string? PayloadJson = null, string? SenderAppId = null)
{
    /// <summary>
    /// Gets the arguments of this activation, without <c>argv[0]</c>: what the process would read
    /// from its command line had it been started for this intent (a file path, <c>--select=…</c>,
    /// <c>--app=…</c>, <c>--show-settings</c>).
    /// </summary>
    public IReadOnlyList<string> Arguments { get; init; } = [];
}

/// <summary>
/// Wraps an intent with authenticated caller and correlation information.
/// </summary>
/// <param name="RequestId">Unique request identifier used for tracing and results.</param>
/// <param name="CallerAppId">Application that created the request.</param>
/// <param name="UserId">Authenticated user on whose behalf it runs.</param>
/// <param name="Intent">Typed intent payload.</param>
public sealed record AppIntentRequest(
    Guid RequestId,
    string CallerAppId,
    string UserId,
    IAppIntent Intent);