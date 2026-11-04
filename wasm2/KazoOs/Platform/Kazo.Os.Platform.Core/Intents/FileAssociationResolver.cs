using Kazo.Os.App.Abstractions;
using Kazo.Os.Platform.Core.Lifecycle;
using Kazo.Os.Platform.Core.Settings;
using Kazo.Os.Simulation.Abstractions.FileSystem;
using Kazo.Os.Simulation.Abstractions.Sessions;

namespace Kazo.Os.Platform.Core.Intents;

/// <summary>Identifies the stable outcome of resolving a file-open request to a handler app.</summary>
public enum FileHandlerResolutionStatus
{
    /// <summary>The intent's explicit preferred app was valid and will be used.</summary>
    ExplicitTarget,

    /// <summary>
    /// A configured default will be used: the acting user's own choice in
    /// <c>~/.config/hackeros/file-associations.json</c> (ADR 0121), else the administrator's in
    /// <c>/etc/hackeros/file-associations.json</c>.
    /// </summary>
    ConfiguredDefault,

    /// <summary>Exactly one enabled app declares handling this file; it will be used.</summary>
    SoleCandidate,

    /// <summary>Multiple enabled apps can handle this file; the caller must choose.</summary>
    ChooserRequired,

    /// <summary>No enabled app declares handling this file.</summary>
    NoHandler,

    /// <summary>The intent's explicit preferred app cannot handle this file or is disabled/unknown.</summary>
    TargetInvalid
}

/// <summary>Contains the result of resolving one <see cref="OpenFileIntent"/>, per `P1-APP-008`.</summary>
/// <param name="Status">Stable resolution outcome.</param>
/// <param name="AppId">Resolved target app ID, when resolution produced exactly one.</param>
/// <param name="CandidateAppIds">Every candidate app ID considered, ordinal-sorted for determinism.</param>
public sealed record FileHandlerResolution(
    FileHandlerResolutionStatus Status,
    string? AppId,
    IReadOnlyList<string> CandidateAppIds);

/// <summary>Every app that can open a file, for "Open with" (ADR 0056, section 2).</summary>
/// <param name="AppIds">Candidate app IDs, the configured default first.</param>
/// <param name="DefaultAppId">The configured default, when one applies.</param>
public sealed record FileHandlerCandidates(IReadOnlyList<string> AppIds, string? DefaultAppId);

/// <summary>Which file holds a configured default (ADR 0121, section 1).</summary>
public enum FileAssociationLayer
{
    /// <summary><c>/etc/hackeros/file-associations.json</c> (or its built-in content), set by an administrator.</summary>
    System = 0,

    /// <summary>The acting user's <c>~/.config/hackeros/file-associations.json</c>.</summary>
    User = 1
}

/// <summary>One configured default and the file it comes from (ADR 0121, section 3).</summary>
/// <param name="Target">Extension with its dot (<c>.md</c>) or media type (<c>x-scheme-handler/https</c>), lowercase.</param>
/// <param name="Action">Action (<c>open</c> or <c>edit</c>).</param>
/// <param name="AppId">Default app.</param>
/// <param name="Layer">File that names it.</param>
public sealed record ConfiguredFileAssociation(string Target, string Action, string AppId, FileAssociationLayer Layer);

/// <summary>How a change of the user's default handler ended (ADR 0121, section 3).</summary>
public enum UserDefaultHandlerChangeStatus
{
    /// <summary>The user's file now holds the change.</summary>
    Changed,

    /// <summary>The target is neither <c>.ext</c> nor <c>type/subtype</c>.</summary>
    InvalidTarget,

    /// <summary>The action is neither <c>open</c> nor <c>edit</c>.</summary>
    InvalidAction,

    /// <summary>No installed, enabled window app has this ID.</summary>
    AppNotFound,

    /// <summary>The app's manifest does not declare this target and action.</summary>
    AppCannotHandle,

    /// <summary>The user's existing file is invalid; it was left untouched.</summary>
    FileInvalid,

    /// <summary>The user's permissions refused the write, or no user layer is available.</summary>
    WriteFailed
}

/// <summary>
/// Resolves which app should open or edit a file using the documented precedence order, per
/// `P1-APP-008`, `P1-APP-009` and ADR 0121: an explicit request target first, then the acting
/// user's own default, then the administrator-configured default, then any manifest-declared
/// candidates. Never guesses or silently assigns a new default.
/// </summary>
public sealed class FileAssociationResolver
{
    private readonly AppCatalog _catalog;
    private readonly IAppEnablementRegistry _enablement;
    private readonly IOsSettingsService _settings;
    private readonly IFileSystemService? _fileSystem;

    /// <summary>Initializes the resolver over a catalog, enablement registry, and the OS settings files (system layer only).</summary>
    public FileAssociationResolver(AppCatalog catalog, IAppEnablementRegistry enablement, IOsSettingsService settings)
        : this(catalog, enablement, settings, fileSystem: null)
    {
    }

    /// <summary>Initializes the resolver over a catalog, enablement registry, the OS settings files and the filesystem.</summary>
    /// <param name="catalog">Installed apps.</param>
    /// <param name="enablement">Launch checkpoint (uninstalled apps are skipped).</param>
    /// <param name="settings">OS settings files (the system layer).</param>
    /// <param name="fileSystem">
    /// Filesystem the user layer is read from as the acting user (ADR 0121). Without it, and for
    /// calls that name no user, only the system layer applies.
    /// </param>
    public FileAssociationResolver(
        AppCatalog catalog, IAppEnablementRegistry enablement, IOsSettingsService settings, IFileSystemService? fileSystem)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _enablement = enablement ?? throw new ArgumentNullException(nameof(enablement));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _fileSystem = fileSystem;
    }

    /// <summary>Resolves an <see cref="OpenFileIntent"/> with the system layer only.</summary>
    /// <param name="intent">Intent describing the target file, action, and any explicit preference.</param>
    /// <param name="cancellationToken">Cancels the read of the association document.</param>
    public ValueTask<FileHandlerResolution> ResolveAsync(OpenFileIntent intent, CancellationToken cancellationToken = default) =>
        ResolveAsync(intent, user: null, cancellationToken);

    /// <summary>Resolves an <see cref="OpenFileIntent"/> to a target app, or to a chooser/failure outcome.</summary>
    /// <param name="intent">Intent describing the target file, action, and any explicit preference.</param>
    /// <param name="user">User the intent runs as; their own defaults come before the system's (ADR 0121).</param>
    /// <param name="cancellationToken">Cancels the read of the association documents.</param>
    public ValueTask<FileHandlerResolution> ResolveAsync(
        OpenFileIntent intent, AuthenticatedPrincipal? user, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(intent);
        return ResolveCoreAsync(
            GetExtension(intent.Path),
            intent.MediaType,
            intent.Action == FileIntentAction.Edit ? "edit" : "open",
            intent.PreferredAppId,
            user,
            cancellationToken);
    }

    /// <summary>
    /// Resolves the handler of one URI scheme (ADR 0096, section 3): the media type
    /// <c>x-scheme-handler/&lt;scheme&gt;</c> with the <c>open</c> action, with the same precedence as a
    /// file (explicit preference, configured default, sole candidate, chooser).
    /// </summary>
    /// <param name="scheme">URI scheme, e.g. <c>https</c>; compared case-insensitively.</param>
    /// <param name="preferredAppId">Explicit preferred handler, when the caller already chose one.</param>
    /// <param name="cancellationToken">Cancels the read of the association document.</param>
    public ValueTask<FileHandlerResolution> ResolveSchemeAsync(
        string scheme, string? preferredAppId = null, CancellationToken cancellationToken = default) =>
        ResolveSchemeAsync(scheme, user: null, preferredAppId, cancellationToken);

    /// <summary>Resolves the handler of one URI scheme for <paramref name="user"/>: their own default first (ADR 0121).</summary>
    /// <param name="scheme">URI scheme, e.g. <c>https</c>; compared case-insensitively.</param>
    /// <param name="user">User the intent runs as; <see langword="null"/> reads the system layer only.</param>
    /// <param name="preferredAppId">Explicit preferred handler, when the caller already chose one.</param>
    /// <param name="cancellationToken">Cancels the read of the association documents.</param>
    public ValueTask<FileHandlerResolution> ResolveSchemeAsync(
        string scheme, AuthenticatedPrincipal? user, string? preferredAppId = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scheme);
        return ResolveCoreAsync(
            extension: null,
            AppIntentIds.SchemeHandlerMediaTypePrefix + scheme.ToLowerInvariant(),
            "open",
            preferredAppId,
            user,
            cancellationToken);
    }

    private async ValueTask<FileHandlerResolution> ResolveCoreAsync(
        string? extension,
        string? mediaType,
        string action,
        string? preferredAppId,
        AuthenticatedPrincipal? user,
        CancellationToken cancellationToken)
    {
        if (preferredAppId is not null)
        {
            bool valid = _enablement.IsEnabled(preferredAppId)
                && _catalog.Manifests.TryGetValue(preferredAppId, out AppManifest? preferredManifest)
                && HandlesFile(preferredManifest, extension, mediaType, action);
            return valid
                ? new FileHandlerResolution(FileHandlerResolutionStatus.ExplicitTarget, preferredAppId, [preferredAppId])
                : new FileHandlerResolution(FileHandlerResolutionStatus.TargetInvalid, null, []);
        }

        if (await FindDefaultAsync(extension, mediaType, action, user, cancellationToken).ConfigureAwait(false) is { } defaultAppId)
        {
            return new FileHandlerResolution(FileHandlerResolutionStatus.ConfiguredDefault, defaultAppId, [defaultAppId]);
        }

        string[] candidates = [.. _catalog.Manifests.Values
            .Where(manifest => manifest.Kind == AppKind.Window
                && _enablement.IsEnabled(manifest.Id)
                && HandlesFile(manifest, extension, mediaType, action))
            .Select(manifest => manifest.Id)
            .OrderBy(id => id, StringComparer.Ordinal)];

        return candidates.Length switch
        {
            0 => new FileHandlerResolution(FileHandlerResolutionStatus.NoHandler, null, []),
            1 => new FileHandlerResolution(FileHandlerResolutionStatus.SoleCandidate, candidates[0], candidates),
            _ => new FileHandlerResolution(FileHandlerResolutionStatus.ChooserRequired, null, candidates)
        };
    }

    /// <summary>
    /// Lists every enabled window app that can open or edit <paramref name="path"/> ("Open with",
    /// ADR 0056 section 2): the configured default first when there is one, then the other
    /// candidates in ordinal id order.
    /// </summary>
    /// <returns>The candidate ids and, when one applies, the configured default (also first in the list).</returns>
    /// <param name="path">File to open.</param>
    /// <param name="mediaType">MIME type when known, matched besides the extension.</param>
    /// <param name="cancellationToken">Cancels the read of the association document.</param>
    public ValueTask<FileHandlerCandidates> GetHandlerAppIdsAsync(
        VirtualPath path, string? mediaType = null, CancellationToken cancellationToken = default) =>
        GetHandlerAppIdsAsync(path, user: null, mediaType, cancellationToken);

    /// <summary>Lists the "Open with" candidates for <paramref name="user"/>: their own default first (ADR 0121).</summary>
    /// <param name="path">File to open.</param>
    /// <param name="user">User asking; <see langword="null"/> reads the system layer only.</param>
    /// <param name="mediaType">MIME type when known, matched besides the extension.</param>
    /// <param name="cancellationToken">Cancels the read of the association documents.</param>
    public async ValueTask<FileHandlerCandidates> GetHandlerAppIdsAsync(
        VirtualPath path, AuthenticatedPrincipal? user, string? mediaType = null, CancellationToken cancellationToken = default)
    {
        string? extension = GetExtension(path);
        List<string> handlers = [.. _catalog.Manifests.Values
            .Where(manifest => manifest.Kind == AppKind.Window
                && _enablement.IsEnabled(manifest.Id)
                && (HandlesFile(manifest, extension, mediaType, "open") || HandlesFile(manifest, extension, mediaType, "edit")))
            .Select(manifest => manifest.Id)
            .OrderBy(id => id, StringComparer.Ordinal)];

        if (await FindDefaultAsync(extension, mediaType, "open", user, cancellationToken).ConfigureAwait(false) is { } defaultAppId)
        {
            handlers.Remove(defaultAppId);
            handlers.Insert(0, defaultAppId);
            return new FileHandlerCandidates(handlers, defaultAppId);
        }

        return new FileHandlerCandidates(handlers, null);
    }

    /// <summary>
    /// Lists every configured default of both layers (ADR 0121, section 3): the user's entries first,
    /// then the system's, one row per target and action, in document order. A user entry hides the
    /// system entry of the same target and action, so the first row of a pair is the effective one.
    /// </summary>
    /// <param name="user">User whose layer is read; <see langword="null"/> reads the system layer only.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    public async ValueTask<IReadOnlyList<ConfiguredFileAssociation>> GetConfiguredDefaultsAsync(
        AuthenticatedPrincipal? user, CancellationToken cancellationToken = default)
    {
        List<ConfiguredFileAssociation> rows = [];
        if (await ReadUserLayerAsync(user, cancellationToken).ConfigureAwait(false) is { } userIndex)
        {
            AddRows(rows, userIndex, FileAssociationLayer.User);
        }

        AddRows(rows, await ReadSystemLayerAsync(cancellationToken).ConfigureAwait(false), FileAssociationLayer.System);
        return rows;
    }

    /// <summary>
    /// Sets or removes <paramref name="user"/>'s own default for one target and action (ADR 0121,
    /// section 3). The app must be an installed window app whose manifest declares the target and
    /// action; removing a choice (<paramref name="appId"/> <see langword="null"/>) is always allowed.
    /// </summary>
    /// <param name="user">User whose <c>~/.config/hackeros/file-associations.json</c> is written, as them.</param>
    /// <param name="target">Extension with its dot (<c>.md</c>) or media type (<c>x-scheme-handler/https</c>).</param>
    /// <param name="action"><c>open</c> or <c>edit</c>.</param>
    /// <param name="appId">New default, or <see langword="null"/> to fall back to the system's.</param>
    /// <param name="cancellationToken">Cancels the read and the write.</param>
    public async ValueTask<UserDefaultHandlerChangeStatus> SetUserDefaultAsync(
        AuthenticatedPrincipal user, string target, string action, string? appId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(user);
        if (!UserFileAssociationStore.TryNormalizeTarget(target, out string normalizedTarget))
        {
            return UserDefaultHandlerChangeStatus.InvalidTarget;
        }

        string normalizedAction = action?.Trim().ToLowerInvariant() ?? string.Empty;
        if (normalizedAction is not ("open" or "edit"))
        {
            return UserDefaultHandlerChangeStatus.InvalidAction;
        }

        if (appId is not null)
        {
            if (!_enablement.IsEnabled(appId)
                || !_catalog.Manifests.TryGetValue(appId, out AppManifest? manifest)
                || manifest.Kind != AppKind.Window)
            {
                return UserDefaultHandlerChangeStatus.AppNotFound;
            }

            bool isExtension = UserFileAssociationStore.IsExtension(normalizedTarget);
            if (!HandlesFile(manifest, isExtension ? normalizedTarget : null, isExtension ? null : normalizedTarget, normalizedAction))
            {
                return UserDefaultHandlerChangeStatus.AppCannotHandle;
            }
        }

        if (_fileSystem is null)
        {
            return UserDefaultHandlerChangeStatus.WriteFailed;
        }

        UserFileAssociationWriteStatus written = await UserFileAssociationStore
            .WriteAsync(_fileSystem, user, normalizedTarget, normalizedAction, appId, cancellationToken)
            .ConfigureAwait(false);
        return written switch
        {
            UserFileAssociationWriteStatus.Changed => UserDefaultHandlerChangeStatus.Changed,
            UserFileAssociationWriteStatus.FileInvalid => UserDefaultHandlerChangeStatus.FileInvalid,
            _ => UserDefaultHandlerChangeStatus.WriteFailed
        };
    }

    /// <summary>
    /// Finds the configured default of one target and action: the user's layer first, then the
    /// system's (ADR 0121, section 2). An entry naming an unknown or uninstalled app is skipped, so
    /// a stale user choice falls back to the administrator's.
    /// </summary>
    private async ValueTask<string?> FindDefaultAsync(
        string? extension, string? mediaType, string action, AuthenticatedPrincipal? user, CancellationToken cancellationToken)
    {
        if (await ReadUserLayerAsync(user, cancellationToken).ConfigureAwait(false) is { } userIndex
            && userIndex.TryGetDefault(extension, mediaType, action, out FileAssociationEntry userEntry)
            && IsUsable(userEntry.AppId))
        {
            return userEntry.AppId;
        }

        // /etc/hackeros/file-associations.json is world-readable (0644): resolving a handler is a
        // routine user operation, so the OS reads it as itself.
        FileAssociationIndex systemIndex = await ReadSystemLayerAsync(cancellationToken).ConfigureAwait(false);
        return systemIndex.TryGetDefault(extension, mediaType, action, out FileAssociationEntry entry) && IsUsable(entry.AppId)
            ? entry.AppId
            : null;
    }

    private bool IsUsable(string appId) => _enablement.IsEnabled(appId) && _catalog.Manifests.ContainsKey(appId);

    private async ValueTask<FileAssociationIndex> ReadSystemLayerAsync(CancellationToken cancellationToken) =>
        FileAssociationIndex.Build(await FileAssociationSettingsDocuments.ReadAsync(_settings, cancellationToken).ConfigureAwait(false));

    private ValueTask<FileAssociationIndex?> ReadUserLayerAsync(AuthenticatedPrincipal? user, CancellationToken cancellationToken) =>
        user is null || _fileSystem is null
            ? ValueTask.FromResult<FileAssociationIndex?>(null)
            : UserFileAssociationStore.ReadAsync(_fileSystem, user, cancellationToken);

    private static void AddRows(List<ConfiguredFileAssociation> rows, FileAssociationIndex index, FileAssociationLayer layer)
    {
        HashSet<string> seen = [];
        foreach (FileAssociationEntry entry in index.Entries)
        {
            foreach (string action in entry.Actions)
            {
                foreach (string? target in new[] { entry.Extension, entry.MediaType })
                {
                    // Within one file the first entry of a target and action wins, as in the index.
                    if (target is not null && seen.Add(target + "\n" + action))
                    {
                        rows.Add(new ConfiguredFileAssociation(target, action, entry.AppId, layer));
                    }
                }
            }
        }
    }

    private static bool HandlesFile(AppManifest manifest, string? extension, string? mediaType, string action) =>
        manifest.FileHandlers.Any(handler =>
            MatchesTarget(handler, extension, mediaType)
            && handler.Actions.Any(a => string.Equals(a, action, StringComparison.OrdinalIgnoreCase)));

    private static bool MatchesTarget(FileHandlerManifest handler, string? extension, string? mediaType) =>
        (extension is not null && handler.Extensions.Any(e => string.Equals(e, extension, StringComparison.OrdinalIgnoreCase)))
        || (mediaType is not null && string.Equals(handler.MediaType, mediaType, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Extracts a file's extension (including the leading dot) from its virtual path. There is no
    /// shared <c>VirtualPath</c> filename helper, so the last path segment and its last dot are
    /// located manually. A dotfile with no further dot (e.g. <c>.bashrc</c>) has no extension.
    /// </summary>
    private static string? GetExtension(VirtualPath path)
    {
        string value = path.Value;
        int lastSlash = value.LastIndexOf('/');
        string fileName = lastSlash >= 0 ? value[(lastSlash + 1)..] : value;
        int lastDot = fileName.LastIndexOf('.');
        return lastDot > 0 ? fileName[lastDot..] : null;
    }
}
