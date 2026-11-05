using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.JSInterop;

namespace Kazo.Os.Infrastructure.Browser.Rename;

/// <summary>What the rename migration did at boot (ADR 0230).</summary>
/// <param name="Migrated">Whether the browser held data of a version before 3.0.0 and it was migrated.</param>
/// <param name="CopiedDatabases">Old databases now under their new name (and deleted).</param>
/// <param name="KeptDatabases">Old databases left in place because a database of the new name already existed.</param>
/// <param name="RenamedKeys">The <c>localStorage</c> and <c>sessionStorage</c> keys renamed.</param>
/// <param name="RewrittenRecords">The records of the OS database written again.</param>
public sealed record RenamedStorageMigrationResult(
    bool Migrated,
    IReadOnlyList<string> CopiedDatabases,
    IReadOnlyList<string> KeptDatabases,
    int RenamedKeys,
    int RewrittenRecords)
{
    /// <summary>Nothing to migrate: the data is from 3.0.0 or later, or there is none.</summary>
    public static RenamedStorageMigrationResult NotNeeded { get; } = new(false, [], [], 0, 0);
}

/// <summary>
/// Moves what a HackerOS version (before 3.0.0) stored in the browser to the Kazo OS names
/// (ADR 0230): it runs once at boot, before the OS opens its database.
/// </summary>
/// <remarks>
/// <para>
/// <b>Version.</b> The version that wrote the data is kept in <c>localStorage</c> under
/// <see cref="VersionKey"/>. The versions before 3.0.0 recorded none: a missing version while an
/// old database (<c>hackeros</c>, <c>hackeros-*</c>, <c>hackweb.*</c>), an old key or an old cache
/// exists means data to migrate. Once done, or when there is nothing to migrate, the running
/// version is recorded and later boots stop at that key.
/// </para>
/// <para>
/// <b>Steps.</b> Each old database is copied to its new name (same version, stores, indexes and
/// records), then deleted; old <c>localStorage</c>/<c>sessionStorage</c> keys are renamed; old
/// caches (which the OS fills again) are deleted; then the OS database's records are rewritten by
/// <see cref="RenamedOsDatabaseRewriter"/>.
/// </para>
/// <para>
/// <b>Resuming.</b> <see cref="ProgressKey"/> holds the databases already copied. An interrupted
/// migration starts again at the next boot: a database recorded there is not copied again, only
/// deleted; the copy itself is atomic; every rewrite is idempotent. A database of the new name the
/// migration did not create (made by another build) is never overwritten: the old one is kept.
/// </para>
/// </remarks>
public static class RenamedStorageMigration
{
    /// <summary><c>localStorage</c> key of the version that last wrote the browser's data.</summary>
    public const string VersionKey = "kazoos.osVersion";

    /// <summary><c>localStorage</c> key of an unfinished migration.</summary>
    public const string ProgressKey = "kazoos.renameMigration";

    private const string Local = "local";
    private const string Session = "session";

    /// <summary>Runs the migration in the page's browser.</summary>
    /// <param name="runtime">The page's JavaScript runtime.</param>
    /// <param name="currentVersion">The running Kazo OS version, recorded once done.</param>
    /// <param name="cancellationToken">Cancels the migration; it resumes at the next boot.</param>
    /// <returns>What was migrated.</returns>
    public static async Task<RenamedStorageMigrationResult> RunAsync(
        IJSRuntime runtime, string currentVersion, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        await using BrowserRenamedStorage browser = new(runtime);
        return await RunAsync(browser, currentVersion, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Runs the migration over <paramref name="browser"/>.</summary>
    internal static async Task<RenamedStorageMigrationResult> RunAsync(
        IRenamedStorageBrowser browser, string currentVersion, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(browser);
        ArgumentException.ThrowIfNullOrWhiteSpace(currentVersion);

        Dictionary<string, string> local = await ReadAsync(browser, Local, cancellationToken).ConfigureAwait(false);
        local.TryGetValue(VersionKey, out string? recorded);
        Progress? progress = local.TryGetValue(ProgressKey, out string? json) ? Progress.Read(json) : null;
        if (progress is null && !RenamedNames.PredatesRename(recorded))
        {
            await RecordVersionAsync(browser, recorded, currentVersion, cancellationToken).ConfigureAwait(false);
            return RenamedStorageMigrationResult.NotNeeded;
        }

        IReadOnlyList<string> databases = await browser.ListDatabasesAsync(cancellationToken).ConfigureAwait(false);
        string[] legacyDatabases = [.. databases.Where(RenamedNames.IsLegacyDatabaseName).Order(StringComparer.Ordinal)];
        Dictionary<string, string> session = await ReadAsync(browser, Session, cancellationToken).ConfigureAwait(false);
        string[] legacyCaches = [.. (await browser.CacheNamesAsync(cancellationToken).ConfigureAwait(false)).Where(RenamedNames.ContainsOldName)];
        bool legacyKeys = local.Keys.Concat(session.Keys).Any(RenamedNames.ContainsOldName);
        if (progress is null && legacyDatabases.Length == 0 && legacyCaches.Length == 0 && !legacyKeys)
        {
            await RecordVersionAsync(browser, recorded, currentVersion, cancellationToken).ConfigureAwait(false);
            return RenamedStorageMigrationResult.NotNeeded;
        }

        progress ??= new Progress([]);
        await browser.WriteStorageAsync(Local, ProgressKey, progress.Write(), cancellationToken).ConfigureAwait(false);

        List<string> copied = [];
        List<string> kept = [];
        foreach (string database in legacyDatabases)
        {
            if (!progress.Copied.Contains(database, StringComparer.Ordinal))
            {
                if (!await browser.CopyDatabaseAsync(database, RenamedNames.Rewrite(database), cancellationToken).ConfigureAwait(false))
                {
                    kept.Add(database);
                    continue;
                }

                progress = new Progress([.. progress.Copied, database]);
                await browser.WriteStorageAsync(Local, ProgressKey, progress.Write(), cancellationToken).ConfigureAwait(false);
            }

            await browser.DeleteDatabaseAsync(database, cancellationToken).ConfigureAwait(false);
            copied.Add(database);
        }

        int renamedKeys = await RenameKeysAsync(browser, Local, local, cancellationToken).ConfigureAwait(false)
            + await RenameKeysAsync(browser, Session, session, cancellationToken).ConfigureAwait(false);
        foreach (string cache in legacyCaches)
        {
            await browser.DeleteCacheAsync(cache, cancellationToken).ConfigureAwait(false);
        }

        int rewritten = 0;
        if (databases.Contains(Schema.KazoOsIndexedDbSchema.DatabaseName, StringComparer.Ordinal)
            || progress.Copied.Contains(RenamedNames.LegacyOsDatabaseName, StringComparer.Ordinal))
        {
            rewritten = await browser.RewriteOsDatabaseAsync(cancellationToken).ConfigureAwait(false);
        }

        await browser.WriteStorageAsync(Local, VersionKey, currentVersion, cancellationToken).ConfigureAwait(false);
        await browser.RemoveStorageAsync(Local, ProgressKey, cancellationToken).ConfigureAwait(false);
        return new RenamedStorageMigrationResult(true, copied, kept, renamedKeys, rewritten);
    }

    private static async Task<Dictionary<string, string>> ReadAsync(IRenamedStorageBrowser browser, string area, CancellationToken cancellationToken)
    {
        Dictionary<string, string> entries = new(StringComparer.Ordinal);
        foreach (KeyValuePair<string, string> entry in await browser.ReadStorageAsync(area, cancellationToken).ConfigureAwait(false))
        {
            entries[entry.Key] = entry.Value;
        }

        return entries;
    }

    /// <summary>Renames each old key; a key whose new name is already taken is dropped, the new one wins.</summary>
    private static async Task<int> RenameKeysAsync(
        IRenamedStorageBrowser browser, string area, Dictionary<string, string> entries, CancellationToken cancellationToken)
    {
        int renamed = 0;
        foreach ((string key, string value) in entries.Where(entry => RenamedNames.ContainsOldName(entry.Key)).ToList())
        {
            string newKey = RenamedNames.Rewrite(key);
            if (newKey == key)
            {
                continue;
            }

            if (!entries.ContainsKey(newKey))
            {
                await browser.WriteStorageAsync(area, newKey, RenamedNames.Rewrite(value), cancellationToken).ConfigureAwait(false);
            }

            await browser.RemoveStorageAsync(area, key, cancellationToken).ConfigureAwait(false);
            renamed++;
        }

        return renamed;
    }

    private static async Task RecordVersionAsync(IRenamedStorageBrowser browser, string? recorded, string currentVersion, CancellationToken cancellationToken)
    {
        if (recorded != currentVersion)
        {
            await browser.WriteStorageAsync(Local, VersionKey, currentVersion, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>The state of an unfinished migration: the old databases already copied.</summary>
    internal sealed record Progress(IReadOnlyList<string> Copied)
    {
        public static Progress Read(string json)
        {
            try
            {
                return JsonSerializer.Deserialize(json, RenamedStorageJsonContext.Default.Progress) is { Copied: not null } progress
                    ? progress
                    : new Progress([]);
            }
            catch (JsonException)
            {
                // A damaged record restarts the migration from the beginning: every step can run again.
                return new Progress([]);
            }
        }

        public string Write() => JsonSerializer.Serialize(this, RenamedStorageJsonContext.Default.Progress);
    }
}

/// <summary>Trim-safe JSON of the migration's progress record.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(RenamedStorageMigration.Progress))]
internal sealed partial class RenamedStorageJsonContext : JsonSerializerContext;
