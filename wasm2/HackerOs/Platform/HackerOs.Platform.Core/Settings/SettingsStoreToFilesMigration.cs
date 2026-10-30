using System.Globalization;
using System.Text.Json;
using HackerOs.App.Abstractions;
using HackerOs.Platform.Core.Appearance;
using HackerOs.Platform.Core.Execution;
using HackerOs.Platform.Core.FileSystem;
using HackerOs.Platform.Core.Intents;
using HackerOs.Platform.Core.Policy;
using HackerOs.Platform.Core.Shell;
using HackerOs.Simulation.Abstractions.FileSystem;
using HackerOs.Simulation.Abstractions.Gateways;
using HackerOs.Simulation.Abstractions.Sessions;
using HackerOs.Simulation.Abstractions.Settings;
using HackerOs.Simulation.Abstractions.Time;
using HackerOs.Theming.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace HackerOs.Platform.Core.Settings;

/// <summary>Runs per-user preparation inside login, after the home directory is seeded.</summary>
public interface IUserProfileInitializer
{
    /// <summary>Prepares <paramref name="user"/>'s profile before the session becomes active.</summary>
    /// <param name="user">User logging in.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    ValueTask InitializeAsync(LocalUser user, CancellationToken cancellationToken = default);
}

/// <summary>
/// Copies the retired IndexedDB settings store into real files, once (ADR 0044 section 9).
/// </summary>
/// <remarks>
/// <list type="number">
/// <item><see cref="MigrateSystemAsync"/>, at boot before any login, with an audited system context:
/// every <c>OsAdmin</c> record goes to its <c>/etc/hackeros</c> file (flat JSON converted to
/// <c>.config</c>). Then the installation defaults are written where no file exists yet, so an
/// administrator always edits a real file. Marker: <c>/var/lib/hackeros/migrations/settings-to-files</c>.</item>
/// <item><see cref="InitializeAsync"/>, at each user's login after <see cref="FileSystemSeeder"/>:
/// that user's app documents, start-menu profile and window geometry, and the move of
/// <c>~/.config/apps/&lt;appId&gt;/</c> to <c>~/.config/&lt;appId&gt;/</c>. Marker:
/// <c>~/.local/state/hackeros/migrations/settings-to-files</c>.</item>
/// </list>
/// An existing target file is never overwritten.
/// </remarks>
public sealed class SettingsStoreToFilesMigration : IUserProfileInitializer
{
    /// <summary>System migration marker.</summary>
    public const string SystemMarkerPath = "/var/lib/hackeros/migrations/settings-to-files";

    /// <summary>Per-user marker, relative to the home directory.</summary>
    public const string UserMarkerRelativePath = ".local/state/hackeros/migrations/settings-to-files";

    private readonly IFileSystemService _fileSystem;
    private readonly ISimulationClock _clock;
    private readonly ILegacySettingsStore _store;
    private readonly ILogger _logger;
    private IReadOnlyList<LegacySettingsRecord>? _records;

    /// <summary>Creates the migration.</summary>
    /// <param name="fileSystem">Filesystem engine.</param>
    /// <param name="clock">Simulation clock.</param>
    /// <param name="store">Retired store to read.</param>
    /// <param name="logger">Optional logger.</param>
    public SettingsStoreToFilesMigration(
        IFileSystemService fileSystem,
        ISimulationClock clock,
        ILegacySettingsStore store,
        ILogger<SettingsStoreToFilesMigration>? logger = null)
    {
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _logger = (ILogger?)logger ?? NullLogger.Instance;
    }

    /// <summary>Migrates the OS documents and writes installation defaults (idempotent).</summary>
    /// <param name="cancellationToken">Cancels the work.</param>
    public async ValueTask MigrateSystemAsync(CancellationToken cancellationToken = default)
    {
        IAppFileSystemGateway system = CreateGateway(SystemOperation(), ["system"]);
        await EnsureOrThrowAsync(system, VirtualPath.Parse(OsSettingsDocument.SystemDirectory), cancellationToken).ConfigureAwait(false);

        if (!await ExistsAsync(system, VirtualPath.Parse(SystemMarkerPath), cancellationToken).ConfigureAwait(false))
        {
            foreach (LegacySettingsRecord record in (await GetRecordsAsync(cancellationToken).ConfigureAwait(false))
                         .Where(static record => record.Scope == SettingsScope.OsAdmin))
            {
                (VirtualPath Path, string Content)? converted = ConvertOsRecord(record);
                if (converted is { } file)
                {
                    await WriteIfAbsentAsync(system, file.Path, file.Content, cancellationToken).ConfigureAwait(false);
                }
            }

            await WriteIfAbsentAsync(system, VirtualPath.Parse(SystemMarkerPath), string.Empty, cancellationToken).ConfigureAwait(false);
        }

        // Installation defaults: a fresh machine ships real, commented /etc files like a distro.
        foreach (OsSettingsDocument document in new[]
                 {
                     AppearanceSettingsDocuments.Document,
                     // ADR 0105: high contrast, text size and reduced motion, all following the host.
                     AccessibilitySettingsDocuments.Document,
                     UiPlatformPreferenceSettingsDocuments.Document,
                     PolicySettingsDocuments.Document,
                     Clipboard.ClipboardSettingsDocuments.Document,
                     // ADR 0127 §5: the "not responding" dialog switch.
                     Shell.WindowManagerSettingsDocuments.Document,
                     // ADR 0193 §2: the machine's time zone, "auto" (the device's zone).
                     Globalization.TimeZoneSettingsDocuments.Document,
                     // ADR 0205 §3: the machine's cursor theme, "system" (the host's cursors).
                     CursorSettingsDocuments.Document
                 })
        {
            await WriteIfAbsentAsync(system, document.SystemPath, DefaultConfig(document), cancellationToken).ConfigureAwait(false);
        }

        await WriteIfAbsentAsync(
            system,
            FileAssociationSettingsDocuments.File.Resolve(null),
            FileAssociationSettingsDocuments.DefaultContent,
            cancellationToken).ConfigureAwait(false);

        // ADR 0095, section 3: the window manager's shortcuts, every line commented out.
        await WriteIfAbsentAsync(
            system,
            Shell.WindowKeyBindingsSettingsDocuments.Document.SystemPath,
            Shell.WindowKeyBindingsSettingsDocuments.DefaultSystemFileContent,
            cancellationToken).ConfigureAwait(false);

        // ADR 0049: the dangerous-capability policy and the (empty) machine decisions.
        await WriteIfAbsentAsync(
            system,
            SecuritySettingsDocuments.Document.SystemPath,
            SecuritySettingsDocuments.DefaultContent,
            cancellationToken).ConfigureAwait(false);
        // ADR 0126: the OOM killer's switch, off.
        await WriteIfAbsentAsync(
            system,
            Processes.MemorySettingsDocuments.Document.SystemPath,
            Processes.MemorySettingsDocuments.DefaultContent,
            cancellationToken).ConfigureAwait(false);
        await WriteIfAbsentAsync(
            system,
            AppPermissionsDocument.MachineFile.Resolve(null),
            AppPermissionsDocument.DefaultMachineContent,
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>Never fails the login: an unexpected error is logged and the migration retried at the next login.</remarks>
    public async ValueTask InitializeAsync(LocalUser user, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(user);
        try
        {
            await MigrateUserAsync(user, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(exception, "settings: migration of {User}'s settings failed, retried at next login", user.LoginName.Value);
        }
    }

    private async ValueTask MigrateUserAsync(LocalUser user, CancellationToken cancellationToken)
    {
        string home = $"/home/{user.LoginName.Value}";
        IAppFileSystemGateway gateway = CreateGateway(UserOperation(user), [user.PrimaryGroupId.ToString()]);
        VirtualPath marker = VirtualPath.Parse($"{home}/{UserMarkerRelativePath}");
        if (await ExistsAsync(gateway, marker, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        string userId = user.Id.ToString();
        foreach (LegacySettingsRecord record in await GetRecordsAsync(cancellationToken).ConfigureAwait(false))
        {
            if (record.Scope == SettingsScope.OsAdmin && record.DocumentId == "start-menu")
            {
                string? profile = ExtractStartMenuProfile(record.Content, user.Id);
                if (profile is not null)
                {
                    await WriteIfAbsentAsync(gateway, StartMenuSettingsDocuments.File.Resolve(home), profile, cancellationToken).ConfigureAwait(false);
                }

                continue;
            }

            if (record.Scope is SettingsScope.AppUser or SettingsScope.AppRoamingUser or SettingsScope.AppUserDevice
                && record.AppId is { Length: > 0 } appId
                && string.Equals(record.UserId, userId, StringComparison.OrdinalIgnoreCase))
            {
                (VirtualPath Path, string Content) target = record.DocumentId == "window-geometry"
                    ? (VirtualPath.Parse($"{home}/.local/state/hackeros/windows/{appId}.config"), ConvertWindowGeometry(record.Content))
                    : (VirtualPath.Parse($"{home}/.config/{appId}/{record.DocumentId}.config"), record.Content);
                await WriteIfAbsentAsync(gateway, target.Path, target.Content, cancellationToken).ConfigureAwait(false);
            }
        }

        await MoveLegacyAppDirectoriesAsync(gateway, home, cancellationToken).ConfigureAwait(false);
        await WriteIfAbsentAsync(gateway, marker, string.Empty, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Converts one OS record to its file, or <see langword="null"/> when it has no file of its own.</summary>
    internal static (VirtualPath Path, string Content)? ConvertOsRecord(LegacySettingsRecord record)
    {
        switch (record.DocumentId)
        {
            case "appearance":
                return AppearanceSettingsCodec.TryDecode(record.Content, out ThemePreferences preferences)
                    ? (AppearanceSettingsDocuments.Document.SystemPath, ToConfig(AppearanceSettingsDocuments.Schema, new Dictionary<string, string>
                    {
                        [AppearanceSettingsDocuments.DesktopThemeKey] = preferences.DesktopThemeId,
                        [AppearanceSettingsDocuments.MobileThemeKey] = preferences.MobileThemeId,
                        [AppearanceSettingsDocuments.AccentKey] = preferences.AccentId,
                        [AppearanceSettingsDocuments.AnimationsKey] = preferences.AnimationsEnabled ? "true" : "false"
                    }))
                    : null;
            case "ui-platform-preference":
                return (UiPlatformPreferenceSettingsDocuments.Document.SystemPath,
                    ToConfig(UiPlatformPreferenceSettingsDocuments.Schema, DecodeUiPlatform(record.Content)));
            case "policy":
                // Already a .config document (ADR 0011 grammar): copied verbatim.
                return (PolicySettingsDocuments.Document.SystemPath, record.Content);
            case "file-associations":
                return new FileAssociationSettingsValidator().Validate(record.Content).Count == 0
                    ? (FileAssociationSettingsDocuments.File.Resolve(null), record.Content)
                    : null;
            default:
                // start-menu is split per user at login; anything else has no successor file.
                return null;
        }
    }

    private static Dictionary<string, string> DecodeUiPlatform(string content)
    {
        Dictionary<string, string> values = new(StringComparer.Ordinal);
        try
        {
            using JsonDocument document = JsonDocument.Parse(content);
            JsonElement root = document.RootElement;
            if (root.TryGetProperty("selectionSource", out JsonElement source) && source.GetString() == "explicit"
                && root.TryGetProperty("explicitPlatformId", out JsonElement platform) && platform.ValueKind == JsonValueKind.String)
            {
                values[UiPlatformPreferenceSettingsDocuments.SelectionSourceKey] = "explicit";
                values[UiPlatformPreferenceSettingsDocuments.ExplicitPlatformKey] = platform.GetString()!;
            }
        }
        catch (JsonException)
        {
            // Invalid legacy content: defaults apply.
        }

        return values;
    }

    private static string? ExtractStartMenuProfile(string content, LocalUserId userId)
    {
        if (!StartMenuSettingsCodec.TryDecode(content, out Dictionary<LocalUserId, IReadOnlyList<string>> profiles)
            || !profiles.TryGetValue(userId, out IReadOnlyList<string>? pins))
        {
            return null;
        }

        return StartMenuSettingsCodec.Encode(new Dictionary<LocalUserId, IReadOnlyList<string>> { [userId] = pins });
    }

    private static string ConvertWindowGeometry(string content)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(content);
            JsonElement root = document.RootElement;
            if (!root.GetProperty("hasValue").GetBoolean())
            {
                return string.Empty;
            }

            return string.Create(
                CultureInfo.InvariantCulture,
                $"x={root.GetProperty("x").GetDouble()}\ny={root.GetProperty("y").GetDouble()}\nwidth={root.GetProperty("width").GetDouble()}\nheight={root.GetProperty("height").GetDouble()}\n");
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return string.Empty;
        }
    }

    /// <summary>Serializes explicit values as a commented <c>.config</c> file (missing keys keep their default).</summary>
    internal static string ToConfig(SettingsSchema schema, IReadOnlyDictionary<string, string> values) =>
        "# Migrated from the HackerOS settings store (ADR 0044).\n"
        + ConfigDocumentFormat.Serialize(schema, values);

    private static string DefaultConfig(OsSettingsDocument document) =>
        $"# HackerOS default for every user. Users override keys in ~/.config/hackeros/{document.DocumentId}.config.\n"
        + "# List a key under [locked] with the value true to stop users from overriding it.\n"
        + ConfigDocumentFormat.Serialize(document.Schema, new Dictionary<string, string>(StringComparer.Ordinal));

    private async ValueTask MoveLegacyAppDirectoriesAsync(IAppFileSystemGateway gateway, string home, CancellationToken cancellationToken)
    {
        VirtualPath legacyRoot = VirtualPath.Parse($"{home}/.config/apps");
        FileSystemResult<FileSystemDirectorySnapshot> children = await gateway
            .EnumerateAsync(new FileSystemEnumerateRequest(legacyRoot), cancellationToken)
            .ConfigureAwait(false);
        if (!children.Succeeded || children.Value is null)
        {
            return;
        }

        VirtualPath configRoot = VirtualPath.Parse($"{home}/.config");
        foreach (FileSystemDirectoryItem child in children.Value.Entries)
        {
            VirtualPath source = VirtualPath.Parse($"{legacyRoot.Value}/{child.Name.Value}");
            VirtualPath target = VirtualPath.Parse($"{home}/.config/{child.Name.Value}");
            if (await ExistsAsync(gateway, target, cancellationToken).ConfigureAwait(false))
            {
                _logger.LogWarning("settings: {Source} not moved, {Target} already exists", source.Value, target.Value);
                continue;
            }

            // Moves carry optimistic revisions: re-read both parents right before each move.
            FileSystemResult<FileSystemDirectorySnapshot> sourceParent = await gateway
                .EnumerateAsync(new FileSystemEnumerateRequest(legacyRoot), cancellationToken)
                .ConfigureAwait(false);
            FileSystemResult<FileSystemEntrySnapshot> targetParent = await gateway
                .StatAsync(new FileSystemStatRequest(configRoot), cancellationToken)
                .ConfigureAwait(false);
            if (!sourceParent.Succeeded || sourceParent.Value is null || !targetParent.Succeeded || targetParent.Value is null)
            {
                continue;
            }

            FileSystemMutationResult moved = await gateway
                .MoveAsync(
                    new FileSystemMoveRequest(
                        source,
                        target,
                        child.Metadata.Revision,
                        sourceParent.Value.Revision,
                        targetParent.Value.Metadata.Revision),
                    cancellationToken)
                .ConfigureAwait(false);
            if (!moved.Succeeded)
            {
                _logger.LogWarning("settings: cannot move {Source}: {Code}", source.Value, moved.Transaction.Error?.Code);
            }
        }
    }

    private async ValueTask<IReadOnlyList<LegacySettingsRecord>> GetRecordsAsync(CancellationToken cancellationToken)
    {
        if (_records is not null)
        {
            return _records;
        }

        try
        {
            _records = await _store.ReadAllAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The store is advisory history: an unreadable store must never block boot or login.
            _logger.LogWarning(exception, "settings: legacy settings store unreadable, nothing migrated");
            _records = [];
        }

        return _records;
    }

    private async ValueTask WriteIfAbsentAsync(IAppFileSystemGateway gateway, VirtualPath path, string content, CancellationToken cancellationToken)
    {
        if (await ExistsAsync(gateway, path, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        FileSystemError? error = await SettingsTextFile
            .EditAsync(gateway, path, current => current is { Length: > 0 } ? current : content, createIfMissing: true, cancellationToken)
            .ConfigureAwait(false);
        if (error is not null)
        {
            _logger.LogWarning("settings: cannot write {Path}: {Code}", path.Value, error.Code);
        }
    }

    private static async ValueTask<bool> ExistsAsync(IAppFileSystemGateway gateway, VirtualPath path, CancellationToken cancellationToken)
    {
        FileSystemResult<FileSystemEntrySnapshot> stat = await gateway
            .StatAsync(new FileSystemStatRequest(path), cancellationToken)
            .ConfigureAwait(false);
        return stat.Succeeded;
    }

    private static async ValueTask EnsureOrThrowAsync(IAppFileSystemGateway gateway, VirtualPath path, CancellationToken cancellationToken)
    {
        FileSystemError? error = await SettingsTextFile.EnsureDirectoryAsync(gateway, path, cancellationToken).ConfigureAwait(false);
        if (error is not null)
        {
            throw new InvalidOperationException($"Cannot create '{path}': {error.Code}.");
        }
    }

    private IAppFileSystemGateway CreateGateway(AppOperationContext operation, IReadOnlyList<string> groups) =>
        new AppFileSystemGateway(_fileSystem, _clock, operation, groups);

    private static AppOperationContext SystemOperation() => new()
    {
        AppId = OsSettingsService.ShellAppId,
        UserId = "system",
        UserName = "system",
        UserAuthority = AppAuthority.System,
        GrantedCapabilities = new HashSet<string>(AppCapabilities.All, StringComparer.Ordinal),
        IsSystemOperation = true
    };

    // Audited system operation acting *as* the user, so every created file belongs to that user.
    private static AppOperationContext UserOperation(LocalUser user) => new()
    {
        AppId = OsSettingsService.ShellAppId,
        UserId = user.Id.ToString(),
        UserName = user.LoginName.Value,
        UserAuthority = AppAuthority.System,
        GrantedCapabilities = new HashSet<string>(AppCapabilities.All, StringComparer.Ordinal),
        IsSystemOperation = true
    };
}
