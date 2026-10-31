using System.Text.Json;
using HackerOs.App.Abstractions;
using HackerOs.Platform.Core.Lifecycle;
using HackerOs.Simulation.Abstractions.Sessions;

namespace HackerOs.Platform.Core.ContextMenus;

/// <summary>
/// Runs a registered file menu entry (ADR 0151 §7): launches its target app, with its own rights,
/// once for all the paths or once per path, handing it the entry's intent or, when the target does
/// not declare it, the launch arguments <c>["&lt;intent&gt;", "&lt;path&gt;", …]</c>.
/// </summary>
/// <remarks>
/// The launch goes through <see cref="AppLifecycleOrchestrator"/> like any other: the target receives
/// <c>AppContext.Intent</c>, or <c>IntentReceived</c> when a single instance is already open. The OS
/// is the sender (<see cref="ShellAppId"/>), not the app showing the menu: the entry gives the target
/// no access to the host's files, and the target reads the paths with its own capabilities.
/// </remarks>
public sealed class FileContextMenuLauncher
{
    /// <summary>The sender of the intents the OS hands for a menu entry.</summary>
    public const string ShellAppId = "org.hackeros.shell";

    private readonly AppCatalog _catalog;
    private readonly IAppEnablementRegistry _enablement;
    private readonly AppLifecycleOrchestrator? _orchestrator;

    /// <summary>Creates the launcher.</summary>
    /// <param name="catalog">The catalog: who declares which intent.</param>
    /// <param name="enablement">Which apps are installed for the session user.</param>
    /// <param name="orchestrator">Launches the apps; <see langword="null"/> in tests that only check availability.</param>
    public FileContextMenuLauncher(AppCatalog catalog, IAppEnablementRegistry enablement, AppLifecycleOrchestrator? orchestrator)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _enablement = enablement ?? throw new ArgumentNullException(nameof(enablement));
        _orchestrator = orchestrator;
    }

    /// <summary>Gets the app an entry of <paramref name="ownerAppId"/> launches.</summary>
    /// <param name="ownerAppId">The app that registered the entry.</param>
    /// <param name="entry">The entry.</param>
    public static string TargetOf(string ownerAppId, AppContextMenuEntryManifest entry) =>
        entry.Target == AppContextMenus.SelfTarget ? ownerAppId : entry.Target;

    /// <summary>
    /// Gets whether an entry can run now (ADR 0151 §3.5 and §7): its app is installed for the user, and
    /// its target too, declaring the intent when it is another app. An entry that cannot run is hidden.
    /// A submenu entry (no intent) only needs its app.
    /// </summary>
    /// <param name="ownerAppId">The app that registered the entry.</param>
    /// <param name="entry">The entry.</param>
    public bool IsAvailable(string ownerAppId, AppContextMenuEntryManifest entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (!_catalog.Manifests.ContainsKey(ownerAppId) || !_enablement.IsEnabled(ownerAppId))
        {
            return false;
        }

        if (entry.Intent is null)
        {
            return true;
        }

        string target = TargetOf(ownerAppId, entry);
        if (!_catalog.Manifests.TryGetValue(target, out AppManifest? manifest) || !_enablement.IsEnabled(target))
        {
            return false;
        }

        return target == ownerAppId || Declares(manifest, entry.Intent);
    }

    /// <summary>
    /// Builds the launches of one click, without running them: one per path for <c>each</c>, one with
    /// every path otherwise.
    /// </summary>
    /// <param name="ownerAppId">The app that registered the entry.</param>
    /// <param name="entry">The entry; it has an intent.</param>
    /// <param name="scope">The scope name of the payload: <c>File</c>, <c>Directory</c>, <c>Background</c> or <c>Desktop</c>.</param>
    /// <param name="paths">The paths: the items, or the folder shown for the empty space.</param>
    /// <param name="principal">The session user.</param>
    public IReadOnlyList<AppLaunchRequest> BuildLaunches(
        string ownerAppId,
        AppContextMenuEntryManifest entry,
        string scope,
        IReadOnlyList<string> paths,
        AuthenticatedPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(principal);
        string intent = entry.Intent ?? throw new ArgumentException("A submenu entry launches nothing.", nameof(entry));
        string target = TargetOf(ownerAppId, entry);
        bool declared = _catalog.Manifests.TryGetValue(target, out AppManifest? manifest) && Declares(manifest, intent);

        IEnumerable<IReadOnlyList<string>> batches = entry.Selection == AppContextMenus.SelectionEach
            ? paths.Select(static path => (IReadOnlyList<string>)[path])
            : [paths];
        return [.. batches.Select(batch => declared
            ? new AppLaunchRequest(target, principal, [], Intent: new AppIntentDelivery(intent, PayloadOf(batch, scope, entry.Name), ShellAppId))
            : new AppLaunchRequest(target, principal, [intent, .. batch]))];
    }

    /// <summary>Runs one click of an entry: every launch of <see cref="BuildLaunches"/>, in order.</summary>
    /// <param name="ownerAppId">The app that registered the entry.</param>
    /// <param name="entry">The entry.</param>
    /// <param name="scope">The scope name of the payload.</param>
    /// <param name="paths">The paths.</param>
    /// <param name="principal">The session user.</param>
    /// <returns>The launch results; a failed launch does not stop the next ones.</returns>
    public async Task<IReadOnlyList<AppLaunchResult>> LaunchAsync(
        string ownerAppId,
        AppContextMenuEntryManifest entry,
        string scope,
        IReadOnlyList<string> paths,
        AuthenticatedPrincipal principal)
    {
        if (_orchestrator is null)
        {
            throw new InvalidOperationException("This launcher cannot launch apps.");
        }

        // ADR 0151 §7: the target is checked again at the click; it may have gone since the menu opened.
        if (!IsAvailable(ownerAppId, entry))
        {
            return [new AppLaunchResult(AppLaunchStatus.NotFound, ErrorCode: "context-menu.target-unavailable")];
        }

        List<AppLaunchResult> results = [];
        foreach (AppLaunchRequest request in BuildLaunches(ownerAppId, entry, scope, paths, principal))
        {
            results.Add(await _orchestrator.LaunchAsync(request).ConfigureAwait(false));
        }

        return results;
    }

    /// <summary>The intent's payload without the entry's name, <c>{"paths":[…],"scope":"File"}</c> (ADR 0151 §7).</summary>
    /// <param name="paths">The paths.</param>
    /// <param name="scope">The scope name.</param>
    public static string PayloadOf(IReadOnlyList<string> paths, string scope) => PayloadOf(paths, scope, entry: null);

    /// <summary>
    /// The intent's payload, <c>{"paths":[…],"scope":"File","entry":"extract"}</c> (ADR 0151 §7): the
    /// entry's name lets one intent serve several entries of an app (ADR 0218 §3).
    /// </summary>
    /// <param name="paths">The paths.</param>
    /// <param name="scope">The scope name.</param>
    /// <param name="entry">The name of the chosen entry, or <see langword="null"/> to leave it out.</param>
    public static string PayloadOf(IReadOnlyList<string> paths, string scope, string? entry)
    {
        using MemoryStream stream = new();
        using (Utf8JsonWriter writer = new(stream))
        {
            writer.WriteStartObject();
            writer.WriteStartArray("paths");
            foreach (string path in paths)
            {
                writer.WriteStringValue(path);
            }

            writer.WriteEndArray();
            writer.WriteString("scope", scope);
            if (entry is not null)
            {
                writer.WriteString("entry", entry);
            }

            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    private static bool Declares(AppManifest manifest, string intent) =>
        manifest.Intents.Any(declaration => string.Equals(declaration.IntentId, intent, StringComparison.Ordinal));
}
