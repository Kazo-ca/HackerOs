using HackerOs.App.Abstractions;
using HackerOs.Simulation.Abstractions.FileSystem;
using HackerOs.Simulation.Abstractions.Gateways;
using HackerOs.Simulation.Abstractions.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace HackerOs.Platform.Core.Apps.Packages;

/// <summary>
/// Protects a user's data when an installed app changes version (ADR 0214 §4.2, Q5 A), the way
/// Android keeps an app's data across <c>onUpgrade</c>: before the first launch of a new version,
/// the OS copies the app's <c>~/.config/&lt;appId&gt;/</c> and <c>~/.local/share/&lt;appId&gt;/</c>
/// to <c>~/.local/share/hackeros/app-backups/&lt;appId&gt;/&lt;old version&gt;/</c>. An update then
/// hands <see cref="PreviousVersionVariable"/> to the process, which migrates its own data; a
/// rollback puts back the backup of the version it returns to.
/// </summary>
/// <remarks>
/// Everything runs with the user's own rights, in their home: no other user's data is read. The
/// version that used the data last is in <c>~/.config/hackeros/app-data-versions.config</c>,
/// section <c>[versions]</c>. A failed backup is logged and does not stop the launch.
/// </remarks>
public sealed class AppDataGuard
{
    /// <summary>Variable handed to the first process of a new version: the version whose data it finds.</summary>
    public const string PreviousVersionVariable = "HACKEROS_PREVIOUS_VERSION";

    /// <summary>File of the versions that used each app's data last, under the home.</summary>
    public const string VersionsConfigUnderHome = ".config/hackeros/app-data-versions.config";

    /// <summary>Folder of the data backups, under the home.</summary>
    public const string BackupsUnderHome = ".local/share/hackeros/app-backups";

    /// <summary>Section of <see cref="VersionsConfigUnderHome"/>.</summary>
    public const string VersionsSection = "versions";

    /// <summary>Backups kept per app: the version left and the version now used.</summary>
    public const int MaximumBackups = 2;

    private const string VersionsDefaultContent =
        "# Version of each installed app that used its data last (ADR 0214 §4.2).\n"
        + "# Written by the OS at each launch; a backup of the data is made when it changes.\n"
        + "[versions]\n";

    private static readonly string[] DataFolders = [".config", ".local/share"];

    private readonly Func<InstalledPackageCatalog?> _packages;
    private readonly ILogger _logger;

    /// <summary>Creates the guard.</summary>
    /// <param name="packages">The apps installed from a package in the running OS; only they are guarded.</param>
    /// <param name="logger">Log of the backups (<c>/var/log/syslog</c>).</param>
    public AppDataGuard(Func<InstalledPackageCatalog?> packages, ILogger<AppDataGuard>? logger = null)
    {
        _packages = packages ?? throw new ArgumentNullException(nameof(packages));
        _logger = (ILogger?)logger ?? NullLogger.Instance;
    }

    /// <summary>Gets the versions file of a home.</summary>
    /// <param name="home">The user's home directory.</param>
    public static VirtualPath GetVersionsPath(string home) => VirtualPath.Parse($"{Trim(home)}/{VersionsConfigUnderHome}");

    /// <summary>Gets the backup folder of one version of an app's data.</summary>
    /// <param name="home">The user's home directory.</param>
    /// <param name="appId">The app.</param>
    /// <param name="version">The version that used the data.</param>
    public static VirtualPath GetBackupPath(string home, string appId, SemanticVersion version) =>
        VirtualPath.Parse($"{Trim(home)}/{BackupsUnderHome}/{appId}/{version}");

    /// <summary>
    /// Runs before the process of <paramref name="manifest"/> exists: backs up, restores and records
    /// as §4.2 says.
    /// </summary>
    /// <param name="user">The user's own file-system writer.</param>
    /// <param name="home">The user's home directory.</param>
    /// <param name="manifest">The app being launched.</param>
    /// <param name="cancellationToken">Cancels the copies.</param>
    /// <returns>Variables to add to the process environment, or <see langword="null"/>.</returns>
    public async ValueTask<IReadOnlyDictionary<string, string>?> PrepareAsync(
        IAppFileSystemGateway user, string home, AppManifest manifest, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentException.ThrowIfNullOrWhiteSpace(home);
        ArgumentNullException.ThrowIfNull(manifest);
        if (_packages()?.Apps.ContainsKey(manifest.Id) != true || !SemanticVersion.TryParse(manifest.Version, out SemanticVersion version))
        {
            return null;
        }

        try
        {
            VirtualPath versionsPath = GetVersionsPath(home);
            SettingsFileReadResult read = await SettingsTextFile.ReadAsync(user, versionsPath, cancellationToken).ConfigureAwait(false);
            if (read.Error is not null)
            {
                throw new IOException($"{versionsPath.Value}: {read.Error.Code}.");
            }

            SemanticVersion? recorded = ReadVersion(read.Content, manifest.Id);
            if (recorded == version)
            {
                return null;
            }

            IReadOnlyDictionary<string, string>? environment = null;
            if (recorded is { } previous)
            {
                await CopyDataAsync(user, home, manifest.Id, GetBackupPath(home, manifest.Id, previous), toBackup: true, cancellationToken).ConfigureAwait(false);
                VirtualPath target = GetBackupPath(home, manifest.Id, version);
                if (version.CompareTo(previous) < 0 && await ExistsAsync(user, target, cancellationToken).ConfigureAwait(false))
                {
                    await CopyDataAsync(user, home, manifest.Id, target, toBackup: false, cancellationToken).ConfigureAwait(false);
                    _logger.LogInformation(
                        "apps: data of {AppId} for {Home} backed up ({Previous}) and restored from the backup of {Version}", manifest.Id, home, previous, version);
                }
                else
                {
                    environment = new Dictionary<string, string>(StringComparer.Ordinal) { [PreviousVersionVariable] = previous.ToString() };
                    _logger.LogInformation(
                        "apps: data of {AppId} for {Home} backed up before {Version} migrates it from {Previous}", manifest.Id, home, version, previous);
                }

                await PruneAsync(user, home, manifest.Id, version, previous, cancellationToken).ConfigureAwait(false);
            }

            Ensure(await SettingsTextFile.EnsureDirectoryAsync(user, SettingsTextFile.GetParent(versionsPath), cancellationToken).ConfigureAwait(false));
            Ensure(await SettingsTextFile.EditAsync(
                    user,
                    versionsPath,
                    content => ConfigDocumentEditor.SetValue(
                        string.IsNullOrEmpty(content) ? VersionsDefaultContent : content, VersionsSection, manifest.Id, version.ToString()),
                    createIfMissing: true,
                    cancellationToken)
                .ConfigureAwait(false));
            return environment;
        }
        catch (IOException exception)
        {
            // The launch goes on, like an app whose data could not be backed up on a full disk;
            // the version is not recorded, so the next launch tries again.
            _logger.LogWarning(exception, "apps: data of {AppId} for {Home} could not be backed up", manifest.Id, home);
            return null;
        }
    }

    /// <summary>Reads the version recorded for an app in a versions file.</summary>
    /// <param name="content">Content of the file, or <see langword="null"/>.</param>
    /// <param name="appId">The app.</param>
    public static SemanticVersion? ReadVersion(string? content, string appId)
    {
        if (string.IsNullOrEmpty(content))
        {
            return null;
        }

        ConfigLine? line = ConfigDocumentLines.Parse(content).LastOrDefault(line =>
            line.Kind == ConfigLineKind.KeyValue && !line.InMalformedSection && line.Group == VersionsSection && line.Key == appId);
        return SemanticVersion.TryParse(line?.Value, out SemanticVersion version) ? version : null;
    }

    // Copies each data folder to (or back from) a backup; the destination is replaced whole.
    private static async ValueTask CopyDataAsync(
        IAppFileSystemGateway user, string home, string appId, VirtualPath backup, bool toBackup, CancellationToken cancellationToken)
    {
        foreach (string folder in DataFolders)
        {
            VirtualPath data = VirtualPath.Parse($"{Trim(home)}/{folder}/{appId}");
            VirtualPath saved = VirtualPath.Parse($"{backup.Value}/{folder.Replace('/', '-')}");
            (VirtualPath from, VirtualPath to) = toBackup ? (data, saved) : (saved, data);
            await DeleteIfExistsAsync(user, to, cancellationToken).ConfigureAwait(false);
            FileSystemResult<FileSystemEntrySnapshot> source = await user.StatAsync(new FileSystemStatRequest(from), cancellationToken).ConfigureAwait(false);
            if (!source.Succeeded)
            {
                Ensure(source.Error?.Code == FileSystemErrorCode.NotFound ? null : source.Error);
                continue;
            }

            VirtualPath parent = SettingsTextFile.GetParent(to);
            Ensure(await SettingsTextFile.EnsureDirectoryAsync(user, parent, cancellationToken).ConfigureAwait(false));
            FileSystemResult<FileSystemEntrySnapshot> parentEntry = await user.StatAsync(new FileSystemStatRequest(parent), cancellationToken).ConfigureAwait(false);
            Ensure(parentEntry.Succeeded ? null : parentEntry.Error);
            FileSystemMutationResult copied = await user.CopyAsync(
                    new FileSystemCopyRequest(from, to, source.Value!.Metadata.Revision, parentEntry.Value!.Metadata.Revision), cancellationToken)
                .ConfigureAwait(false);
            Ensure(copied.Succeeded ? null : copied.Transaction.Error);
        }

        if (toBackup)
        {
            // An app with no data still gets its (empty) backup: a later rollback restores "nothing".
            Ensure(await SettingsTextFile.EnsureDirectoryAsync(user, backup, cancellationToken).ConfigureAwait(false));
        }
    }

    // Keeps the backups of the version left and the version now used (MaximumBackups).
    private static async ValueTask PruneAsync(
        IAppFileSystemGateway user, string home, string appId, SemanticVersion current, SemanticVersion previous, CancellationToken cancellationToken)
    {
        VirtualPath root = VirtualPath.Parse($"{Trim(home)}/{BackupsUnderHome}/{appId}");
        FileSystemResult<FileSystemDirectorySnapshot> listed = await user.EnumerateAsync(new FileSystemEnumerateRequest(root), cancellationToken).ConfigureAwait(false);
        if (!listed.Succeeded)
        {
            return;
        }

        foreach (FileSystemDirectoryItem entry in listed.Value!.Entries)
        {
            bool kept = SemanticVersion.TryParse(entry.Name.Value, out SemanticVersion version) && (version == current || version == previous);
            if (!kept)
            {
                await DeleteIfExistsAsync(user, VirtualPath.Parse($"{root.Value}/{entry.Name.Value}"), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static async ValueTask<bool> ExistsAsync(IAppFileSystemGateway user, VirtualPath path, CancellationToken cancellationToken)
    {
        FileSystemResult<FileSystemEntrySnapshot> entry = await user.StatAsync(new FileSystemStatRequest(path), cancellationToken).ConfigureAwait(false);
        Ensure(entry.Succeeded || entry.Error?.Code == FileSystemErrorCode.NotFound ? null : entry.Error);
        return entry.Succeeded;
    }

    private static async ValueTask DeleteIfExistsAsync(IAppFileSystemGateway user, VirtualPath path, CancellationToken cancellationToken)
    {
        FileSystemResult<FileSystemEntrySnapshot> entry = await user.StatAsync(new FileSystemStatRequest(path), cancellationToken).ConfigureAwait(false);
        if (!entry.Succeeded)
        {
            Ensure(entry.Error?.Code == FileSystemErrorCode.NotFound ? null : entry.Error);
            return;
        }

        VirtualPath parent = SettingsTextFile.GetParent(path);
        FileSystemResult<FileSystemEntrySnapshot> parentEntry = await user.StatAsync(new FileSystemStatRequest(parent), cancellationToken).ConfigureAwait(false);
        Ensure(parentEntry.Succeeded ? null : parentEntry.Error);
        FileSystemMutationResult deleted = await user.DeleteAsync(
                new FileSystemDeleteRequest(path, entry.Value!.Metadata.Revision, parentEntry.Value!.Metadata.Revision, recursive: true),
                cancellationToken)
            .ConfigureAwait(false);
        Ensure(deleted.Succeeded ? null : deleted.Transaction.Error);
    }

    private static void Ensure(FileSystemError? error)
    {
        if (error is not null)
        {
            throw new IOException($"{error.Path?.Value}: {error.Code}.");
        }
    }

    private static string Trim(string home) => home.Length > 1 ? home.TrimEnd('/') : home;
}
