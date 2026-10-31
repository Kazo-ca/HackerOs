using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using HackerOs.App.Abstractions;
using HackerOs.Simulation.Abstractions.FileSystem;
using HackerOs.Simulation.Abstractions.Gateways;
using HackerOs.Simulation.Abstractions.Settings;
using Microsoft.Extensions.Logging;

namespace HackerOs.Platform.Core.Apps.Packages;

/// <summary>
/// Transactional writes of the installer (ADR 0214 §3 and §4), the way <c>dpkg</c> keeps its
/// <c>updates/</c> journal: a <c>.transaction</c> file in the app's folder says what is being
/// done, the files go to <c>&lt;version&gt;.partial/</c> and are renamed in one move, and the
/// <c>[installed]</c> line is the commit point. One previous version is kept for
/// <see cref="RollbackAsync"/>.
/// </summary>
public sealed partial class AppPackageInstaller
{
    /// <summary>Name of the journal written in <c>&lt;root&gt;/&lt;appId&gt;/</c> while an operation runs.</summary>
    public const string TransactionFileName = ".transaction";

    /// <summary>Suffix of the folder a version is written to before it is renamed.</summary>
    public const string PartialSuffix = ".partial";

    private const string InstallOperation = "install";
    private const string RemoveOperation = "remove";
    private const string RollbackOperation = "rollback";

    /// <summary>
    /// Finishes or undoes an operation an interruption left on <paramref name="appId"/> (ADR 0214
    /// §3.5): when <c>[installed]</c> already names the new version, the clean-up is finished;
    /// otherwise the partly written version is deleted and the previous one stays.
    /// </summary>
    /// <param name="writer">A writer of the scope (the system writer for <c>/usr/lib/hackeros/apps</c>).</param>
    /// <param name="appId">The app whose journal is read.</param>
    /// <param name="cancellationToken">Cancels the writes.</param>
    /// <param name="home">The user's home for a personal app, or <see langword="null"/> for the system.</param>
    /// <returns>What was done, or <see langword="null"/> when no operation was left.</returns>
    public async ValueTask<string?> RecoverAsync(
        IAppFileSystemGateway writer, string appId, CancellationToken cancellationToken = default, string? home = null)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentException.ThrowIfNullOrWhiteSpace(appId);
        VirtualPath appDirectory = GetAppRoot(home, appId);
        VirtualPath journalPath = GetJournalPath(appDirectory);
        SettingsFileReadResult read = await SettingsTextFile.ReadAsync(writer, journalPath, cancellationToken).ConfigureAwait(false);
        Ensure(read.Error);
        if (!read.Exists)
        {
            return null;
        }

        Transaction? journal = Transaction.Parse(read.Content);
        SemanticVersion? current = await ReadInstalledVersionAsync(writer, appId, home, cancellationToken).ConfigureAwait(false);
        string result;
        if (journal is null)
        {
            result = "unreadable journal deleted";
        }
        else if (journal.Operation == RemoveOperation)
        {
            if (current is null)
            {
                await DeleteIfExistsAsync(writer, appDirectory, cancellationToken).ConfigureAwait(false);
                return Report(appId, home, "removal finished");
            }

            result = "removal undone, the app stays installed";
        }
        else if (current == journal.Version)
        {
            await PruneAsync(writer, appDirectory, journal.Version, journal.Previous, cancellationToken).ConfigureAwait(false);
            result = $"{journal.Operation} of {journal.Version} finished";
        }
        else
        {
            await DeleteIfExistsAsync(writer, GetPartialDirectory(appDirectory, journal.Version), cancellationToken).ConfigureAwait(false);
            if (journal.Operation == InstallOperation && journal.Version != journal.Previous)
            {
                await DeleteIfExistsAsync(writer, GetVersionDirectory(appDirectory, journal.Version), cancellationToken).ConfigureAwait(false);
            }

            result = $"{journal.Operation} of {journal.Version} undone, {current?.ToString() ?? "nothing"} stays installed";
        }

        await DeleteIfExistsAsync(writer, journalPath, cancellationToken).ConfigureAwait(false);
        return Report(appId, home, result);
    }

    /// <summary>
    /// <c>hackpkg repair</c> (ADR 0214 §3.5): runs <see cref="RecoverAsync"/> on every app of the
    /// scope, then deletes the <c>.partial</c> folders and the folders of apps no
    /// <c>[installed]</c> line names any more.
    /// </summary>
    /// <param name="writer">A writer of the scope.</param>
    /// <param name="cancellationToken">Cancels the writes.</param>
    /// <param name="home">The user's home, or <see langword="null"/> for the system.</param>
    /// <returns>One line per repaired app; empty when nothing needed repair.</returns>
    public async ValueTask<IReadOnlyList<string>> RepairAsync(
        IAppFileSystemGateway writer, CancellationToken cancellationToken = default, string? home = null)
    {
        ArgumentNullException.ThrowIfNull(writer);
        VirtualPath root = home is null ? VirtualPath.Parse(InstalledAppsLayout.Root) : InstalledAppsLayout.GetPersonalRoot(home);
        List<string> repaired = [];
        foreach (string appId in await ListDirectoriesAsync(writer, root, cancellationToken).ConfigureAwait(false))
        {
            if (await RecoverAsync(writer, appId, cancellationToken, home).ConfigureAwait(false) is { } recovered)
            {
                repaired.Add(recovered);
            }
        }

        InstalledAppsReadResult installed = home is null
            ? await ReadInstalledAsync(writer, cancellationToken).ConfigureAwait(false)
            : await ReadPersonalInstalledAsync(writer, home, cancellationToken).ConfigureAwait(false);
        foreach (string appId in await ListDirectoriesAsync(writer, root, cancellationToken).ConfigureAwait(false))
        {
            VirtualPath appDirectory = GetAppRoot(home, appId);
            if (!installed.Sources.ContainsKey(appId))
            {
                await DeleteIfExistsAsync(writer, appDirectory, cancellationToken).ConfigureAwait(false);
                repaired.Add(Report(appId, home, "folder of an app that is not installed deleted"));
                continue;
            }

            foreach (string name in await ListDirectoriesAsync(writer, appDirectory, cancellationToken).ConfigureAwait(false))
            {
                if (name.EndsWith(PartialSuffix, StringComparison.Ordinal))
                {
                    await DeleteIfExistsAsync(writer, VirtualPath.Parse($"{appDirectory.Value}/{name}"), cancellationToken).ConfigureAwait(false);
                    repaired.Add(Report(appId, home, $"unfinished folder {name} deleted"));
                }
            }
        }

        return repaired;
    }

    /// <summary>
    /// Finds the version an update kept (ADR 0214 §4.1): the highest version folder of the app
    /// other than <paramref name="current"/>.
    /// </summary>
    /// <param name="fileSystem">A reader of the scope.</param>
    /// <param name="appId">The installed app.</param>
    /// <param name="current">The version <c>[installed]</c> names.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <param name="home">The user's home for a personal app, or <see langword="null"/> for the system.</param>
    public async ValueTask<SemanticVersion?> FindPreviousVersionAsync(
        IAppFileSystemGateway fileSystem, string appId, SemanticVersion current, CancellationToken cancellationToken = default, string? home = null)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentException.ThrowIfNullOrWhiteSpace(appId);
        SemanticVersion? best = null;
        foreach (string name in await ListDirectoriesAsync(fileSystem, GetAppRoot(home, appId), cancellationToken).ConfigureAwait(false))
        {
            if (SemanticVersion.TryParse(name, out SemanticVersion version)
                && version != current
                && (best is null || version.CompareTo(best.Value) > 0))
            {
                best = version;
            }
        }

        return best;
    }

    /// <summary>
    /// <c>hackpkg rollback</c> (ADR 0214 §4.1): puts the kept version back in <c>[installed]</c>
    /// and keeps the version it leaves, so a second rollback undoes the first.
    /// </summary>
    /// <param name="writer">The system writer after the two-step approval, or the user's own writer for a personal app.</param>
    /// <param name="appId">The installed app.</param>
    /// <param name="by">Login name of the user or administrator, for the log.</param>
    /// <param name="cancellationToken">Cancels the writes.</param>
    /// <param name="home">The user's home for a personal app, or <see langword="null"/> for the system.</param>
    /// <returns>The source left and the source now installed.</returns>
    /// <exception cref="AppPackageException"><c>apps.package.not-installed</c> or <c>apps.package.no-previous</c>.</exception>
    public async ValueTask<(AppPackageSource From, AppPackageSource To)> RollbackAsync(
        IAppFileSystemGateway writer, string appId, string by, CancellationToken cancellationToken = default, string? home = null)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentException.ThrowIfNullOrWhiteSpace(appId);
        await RecoverAsync(writer, appId, cancellationToken, home).ConfigureAwait(false);
        AppPackageSource from = await GetInstalledSourceAsync(writer, appId, home, cancellationToken).ConfigureAwait(false);
        SemanticVersion current = from.Version ?? throw new AppPackageException("apps.package.no-previous", $"{appId} has no version to go back to.");
        SemanticVersion previous = await FindPreviousVersionAsync(writer, appId, current, cancellationToken, home).ConfigureAwait(false)
            ?? throw new AppPackageException("apps.package.no-previous", $"{appId} {current} has no previous version kept.");
        AppPackageSource to = from.WithVersion(previous);
        VirtualPath appDirectory = GetAppRoot(home, appId);
        await WriteJournalAsync(writer, appDirectory, new Transaction(RollbackOperation, previous, current, _clock()), cancellationToken).ConfigureAwait(false);
        await SetInstalledAsync(writer, home, appId, to, cancellationToken).ConfigureAwait(false);
        await DeleteIfExistsAsync(writer, GetJournalPath(appDirectory), cancellationToken).ConfigureAwait(false);
        _logger.LogInformation(
            "apps: {AppId} rolled back from {From} to {To} for {Scope} (by={User})", appId, current, previous, home ?? "the system", by);
        return (from, to);
    }

    private async ValueTask WritePackageAsync(IAppFileSystemGateway writer, AppPackagePlan plan, CancellationToken cancellationToken)
    {
        AppPackage package = plan.Package;
        VirtualPath appDirectory = GetAppRoot(plan.Home, package.AppId);
        await RecoverAsync(writer, package.AppId, cancellationToken, plan.Home).ConfigureAwait(false);
        await WriteJournalAsync(
                writer, appDirectory, new Transaction(InstallOperation, package.Version, plan.PreviousVersion, _clock()), cancellationToken)
            .ConfigureAwait(false);
        VirtualPath partial = GetPartialDirectory(appDirectory, package.Version);
        VirtualPath directory = GetVersionDirectory(appDirectory, package.Version);
        await DeleteIfExistsAsync(writer, partial, cancellationToken).ConfigureAwait(false);
        Ensure(await SettingsTextFile.EnsureDirectoryAsync(writer, partial, cancellationToken).ConfigureAwait(false));
        foreach ((string relativePath, byte[] content) in package.Files.OrderBy(static file => file.Key, StringComparer.Ordinal))
        {
            await WriteFileAsync(writer, VirtualPath.Parse($"{partial.Value}/{relativePath}"), content, cancellationToken).ConfigureAwait(false);
        }

        // A folder of the same version (kept earlier, or left by an old install) is replaced whole.
        await DeleteIfExistsAsync(writer, directory, cancellationToken).ConfigureAwait(false);
        await MoveAsync(writer, partial, directory, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask FinishPackageAsync(IAppFileSystemGateway writer, AppPackagePlan plan, CancellationToken cancellationToken)
    {
        VirtualPath appDirectory = GetAppRoot(plan.Home, plan.Package.AppId);
        await PruneAsync(writer, appDirectory, plan.Package.Version, plan.PreviousVersion, cancellationToken).ConfigureAwait(false);
        await DeleteIfExistsAsync(writer, GetJournalPath(appDirectory), cancellationToken).ConfigureAwait(false);
    }

    private ValueTask BeginRemoveAsync(IAppFileSystemGateway writer, string appId, SemanticVersion? version, string? home, CancellationToken cancellationToken) =>
        WriteJournalAsync(writer, GetAppRoot(home, appId), new Transaction(RemoveOperation, version ?? default, null, _clock()), cancellationToken);

    private async ValueTask<SemanticVersion?> ReadInstalledVersionAsync(
        IAppFileSystemGateway fileSystem, string appId, string? home, CancellationToken cancellationToken)
    {
        InstalledAppsReadResult installed = home is null
            ? await ReadInstalledAsync(fileSystem, cancellationToken).ConfigureAwait(false)
            : await ReadPersonalInstalledAsync(fileSystem, home, cancellationToken).ConfigureAwait(false);
        return installed.Sources.TryGetValue(appId, out AppPackageSource? source) ? source.Version : null;
    }

    private async ValueTask<AppPackageSource> GetInstalledSourceAsync(
        IAppFileSystemGateway fileSystem, string appId, string? home, CancellationToken cancellationToken)
    {
        InstalledAppsReadResult installed = home is null
            ? await ReadInstalledAsync(fileSystem, cancellationToken).ConfigureAwait(false)
            : await ReadPersonalInstalledAsync(fileSystem, home, cancellationToken).ConfigureAwait(false);
        return installed.Sources.TryGetValue(appId, out AppPackageSource? source)
            ? source
            : throw new AppPackageException(
                "apps.package.not-installed",
                home is null ? $"{appId} was not installed for the system from a package." : $"{appId} was not installed for you from a package.");
    }

    private static async ValueTask SetInstalledAsync(
        IAppFileSystemGateway writer, string? home, string appId, AppPackageSource source, CancellationToken cancellationToken)
    {
        VirtualPath config = home is null ? AppsConfigDocument.MachinePath : InstalledAppsLayout.GetPersonalConfigPath(home);
        string defaultContent = home is null ? AppsConfigDocument.MachineDefaultContent : InstalledAppsLayout.PersonalConfigDefaultContent;
        if (home is not null)
        {
            Ensure(await SettingsTextFile.EnsureDirectoryAsync(writer, SettingsTextFile.GetParent(config), cancellationToken).ConfigureAwait(false));
        }

        Ensure(await SettingsTextFile.EditAsync(
                writer,
                config,
                content => AppsConfigDocument.SetInstalled(content, appId, source, defaultContent),
                createIfMissing: true,
                cancellationToken)
            .ConfigureAwait(false));
    }

    // Keeps the installed version and the one it replaced (ADR 0214 §4.1, Q4); deletes the others.
    private static async ValueTask PruneAsync(
        IAppFileSystemGateway writer, VirtualPath appDirectory, SemanticVersion keep, SemanticVersion? previous, CancellationToken cancellationToken)
    {
        foreach (string name in await ListDirectoriesAsync(writer, appDirectory, cancellationToken).ConfigureAwait(false))
        {
            bool kept = SemanticVersion.TryParse(name, out SemanticVersion version) && (version == keep || version == previous);
            if (!kept)
            {
                await DeleteIfExistsAsync(writer, VirtualPath.Parse($"{appDirectory.Value}/{name}"), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static async ValueTask<IReadOnlyList<string>> ListDirectoriesAsync(
        IAppFileSystemGateway fileSystem, VirtualPath directory, CancellationToken cancellationToken)
    {
        FileSystemResult<FileSystemDirectorySnapshot> listed = await fileSystem
            .EnumerateAsync(new FileSystemEnumerateRequest(directory), cancellationToken)
            .ConfigureAwait(false);
        if (!listed.Succeeded)
        {
            Ensure(listed.Error?.Code is FileSystemErrorCode.NotFound or FileSystemErrorCode.NotDirectory ? null : listed.Error);
            return [];
        }

        return [.. listed.Value!.Entries
            .Where(static entry => entry.Metadata.Kind == FileSystemEntryKind.Directory)
            .Select(static entry => entry.Name.Value)];
    }

    private static async ValueTask MoveAsync(IAppFileSystemGateway writer, VirtualPath source, VirtualPath destination, CancellationToken cancellationToken)
    {
        FileSystemResult<FileSystemEntrySnapshot> entry = await writer.StatAsync(new FileSystemStatRequest(source), cancellationToken).ConfigureAwait(false);
        Ensure(entry.Succeeded ? null : entry.Error);
        VirtualPath parent = SettingsTextFile.GetParent(source);
        FileSystemResult<FileSystemEntrySnapshot> parentEntry = await writer.StatAsync(new FileSystemStatRequest(parent), cancellationToken).ConfigureAwait(false);
        Ensure(parentEntry.Succeeded ? null : parentEntry.Error);
        long parentRevision = parentEntry.Value!.Metadata.Revision;
        FileSystemMutationResult moved = await writer.MoveAsync(
                new FileSystemMoveRequest(source, destination, entry.Value!.Metadata.Revision, parentRevision, parentRevision),
                cancellationToken)
            .ConfigureAwait(false);
        Ensure(moved.Succeeded ? null : moved.Transaction.Error);
    }

    private static async ValueTask WriteJournalAsync(
        IAppFileSystemGateway writer, VirtualPath appDirectory, Transaction transaction, CancellationToken cancellationToken)
    {
        Ensure(await SettingsTextFile.EnsureDirectoryAsync(writer, appDirectory, cancellationToken).ConfigureAwait(false));
        Ensure(await SettingsTextFile.WriteAllAsync(writer, GetJournalPath(appDirectory), transaction.Format(), cancellationToken).ConfigureAwait(false));
    }

    private string Report(string appId, string? home, string what)
    {
        _logger.LogWarning("apps: repair of {AppId} for {Scope}: {What}", appId, home ?? "the system", what);
        return $"{appId}: {what}";
    }

    private static VirtualPath GetAppRoot(string? home, string appId) =>
        home is null ? InstalledAppsLayout.GetAppDirectory(appId) : InstalledAppsLayout.GetPersonalAppDirectory(home, appId);

    private static VirtualPath GetJournalPath(VirtualPath appDirectory) => VirtualPath.Parse($"{appDirectory.Value}/{TransactionFileName}");

    private static VirtualPath GetVersionDirectory(VirtualPath appDirectory, SemanticVersion version) =>
        VirtualPath.Parse($"{appDirectory.Value}/{version}");

    private static VirtualPath GetPartialDirectory(VirtualPath appDirectory, SemanticVersion version) =>
        VirtualPath.Parse($"{appDirectory.Value}/{version}{PartialSuffix}");

    /// <summary>The journal: <c>{"operation":…,"version":…,"previous":…,"startedAt":…}</c>, written without reflection (trimmed publish).</summary>
    private sealed record Transaction(string Operation, SemanticVersion Version, SemanticVersion? Previous, DateTimeOffset StartedAt)
    {
        public string Format()
        {
            ArrayBufferWriter<byte> buffer = new();
            using (Utf8JsonWriter json = new(buffer, new JsonWriterOptions { Indented = true }))
            {
                json.WriteStartObject();
                json.WriteString("operation", Operation);
                json.WriteString("version", Version.ToString());
                if (Previous is { } previous)
                {
                    json.WriteString("previous", previous.ToString());
                }

                json.WriteString("startedAt", StartedAt.ToString("O", CultureInfo.InvariantCulture));
                json.WriteEndObject();
            }

            return Encoding.UTF8.GetString(buffer.WrittenSpan) + "\n";
        }

        public static Transaction? Parse(string? content)
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(content ?? string.Empty);
                JsonElement root = document.RootElement;
                string? operation = root.TryGetProperty("operation", out JsonElement op) ? op.GetString() : null;
                if (operation is not (InstallOperation or RemoveOperation or RollbackOperation)
                    || !root.TryGetProperty("version", out JsonElement versionText)
                    || !SemanticVersion.TryParse(versionText.GetString(), out SemanticVersion version))
                {
                    return null;
                }

                SemanticVersion? previous = root.TryGetProperty("previous", out JsonElement previousText)
                    && SemanticVersion.TryParse(previousText.GetString(), out SemanticVersion parsed)
                        ? parsed
                        : null;
                DateTimeOffset startedAt = root.TryGetProperty("startedAt", out JsonElement at) && at.TryGetDateTimeOffset(out DateTimeOffset when)
                    ? when
                    : DateTimeOffset.MinValue;
                return new Transaction(operation, version, previous, startedAt);
            }
            catch (JsonException)
            {
                return null;
            }
        }
    }
}
