using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Kazo.Os.Infrastructure.Browser.Backup;
using Kazo.Os.Infrastructure.Browser.Rename;
using Kazo.Os.Infrastructure.Browser.Schema;

namespace Kazo.Os.Infrastructure.Browser.Tests;

/// <summary>The boot migration of a browser that holds HackerOS data (ADR 0230 §1, §2, §4).</summary>
public sealed class RenamedStorageMigrationTests
{
    private const string Current = "3.0.0";

    [Fact]
    public async Task Old_databases_keys_and_caches_move_to_the_new_names_and_the_version_is_recorded()
    {
        FakeBrowser browser = new(["hackeros", "hackeros-host-folders", "hackweb.org.hackeros.intranet", "other"]);
        browser.Local["hackeros.backup.restore-record"] = "{\"path\":\"/etc/hackeros\"}";
        browser.Session["hackeros.bootRetries"] = "1";
        browser.Caches.Add("hackeros-cache-abc");

        RenamedStorageMigrationResult result = await RenamedStorageMigration.RunAsync(browser, Current, CancellationToken.None);

        Assert.True(result.Migrated);
        Assert.Equal(["kazoos", "kazoos-host-folders", "kazoweb.org.kazoos.intranet", "other"], browser.Databases.Order(StringComparer.Ordinal));
        Assert.Equal(3, result.CopiedDatabases.Count);
        Assert.Equal("{\"path\":\"/etc/kazoos\"}", browser.Local["kazoos.backup.restore-record"]);
        Assert.Equal("1", browser.Session["kazoos.bootRetries"]);
        Assert.Equal(2, result.RenamedKeys);
        Assert.Empty(browser.Caches);
        Assert.Equal(1, browser.Rewrites);
        Assert.Equal(Current, browser.Local[RenamedStorageMigration.VersionKey]);
        Assert.False(browser.Local.ContainsKey(RenamedStorageMigration.ProgressKey));
        Assert.DoesNotContain(browser.Local.Keys.Concat(browser.Session.Keys), RenamedNames.ContainsOldName);
    }

    [Fact]
    public async Task A_recorded_version_from_3_0_0_on_stops_the_migration_at_once()
    {
        FakeBrowser browser = new(["hackeros"]);
        browser.Local[RenamedStorageMigration.VersionKey] = "3.0.0";

        RenamedStorageMigrationResult result = await RenamedStorageMigration.RunAsync(browser, "3.1.0", CancellationToken.None);

        Assert.False(result.Migrated);
        Assert.Contains("hackeros", browser.Databases);
        Assert.Equal(0, browser.ListCalls);
        Assert.Equal("3.1.0", browser.Local[RenamedStorageMigration.VersionKey]);
    }

    [Fact]
    public async Task A_fresh_browser_only_records_the_version()
    {
        FakeBrowser browser = new([]);

        RenamedStorageMigrationResult result = await RenamedStorageMigration.RunAsync(browser, Current, CancellationToken.None);

        Assert.False(result.Migrated);
        Assert.Equal(0, browser.Rewrites);
        Assert.Equal(Current, browser.Local[RenamedStorageMigration.VersionKey]);
    }

    [Fact]
    public async Task An_interrupted_migration_resumes_without_copying_a_database_again()
    {
        // The previous boot copied hackeros to kazoos, recorded it, and stopped before deleting it.
        FakeBrowser browser = new(["hackeros", "kazoos", "hackweb.site"]);
        browser.Local[RenamedStorageMigration.ProgressKey] = "{\"copied\":[\"hackeros\"]}";
        browser.Local[RenamedStorageMigration.VersionKey] = "3.0.0";

        RenamedStorageMigrationResult result = await RenamedStorageMigration.RunAsync(browser, Current, CancellationToken.None);

        Assert.Equal(["kazoweb.site"], browser.CopyTargets);
        Assert.Equal(["kazoos", "kazoweb.site"], browser.Databases.Order(StringComparer.Ordinal));
        Assert.Equal(["hackeros", "hackweb.site"], result.CopiedDatabases);
        Assert.False(browser.Local.ContainsKey(RenamedStorageMigration.ProgressKey));
    }

    [Fact]
    public async Task A_failure_leaves_the_progress_so_the_next_boot_finishes_the_work()
    {
        FakeBrowser browser = new(["hackeros", "hackweb.site"]) { FailCopyOf = "hackweb.site" };

        await Assert.ThrowsAsync<InvalidOperationException>(() => RenamedStorageMigration.RunAsync(browser, Current, CancellationToken.None));
        Assert.False(browser.Local.ContainsKey(RenamedStorageMigration.VersionKey));
        Assert.Contains("hackeros", browser.Local[RenamedStorageMigration.ProgressKey]);

        browser.FailCopyOf = null;
        RenamedStorageMigrationResult result = await RenamedStorageMigration.RunAsync(browser, Current, CancellationToken.None);

        Assert.True(result.Migrated);
        Assert.Equal(["kazoos", "kazoweb.site"], browser.CopyTargets);
        Assert.Equal(["kazoos", "kazoweb.site"], browser.Databases.Order(StringComparer.Ordinal));
        Assert.Equal(Current, browser.Local[RenamedStorageMigration.VersionKey]);
    }

    [Fact]
    public async Task A_database_of_the_new_name_the_migration_did_not_create_is_never_overwritten()
    {
        FakeBrowser browser = new(["hackeros", "kazoos"]);

        RenamedStorageMigrationResult result = await RenamedStorageMigration.RunAsync(browser, Current, CancellationToken.None);

        Assert.Equal(["hackeros"], result.KeptDatabases);
        Assert.Contains("hackeros", browser.Databases);
        Assert.Empty(browser.CopyTargets);
    }

    [Fact]
    public void A_backup_made_before_3_0_0_is_migrated_when_it_is_read()
    {
        Dictionary<string, IReadOnlyList<JsonObject>> fixture = RenamedOsDatabaseRewriterTests.ReadFixture(3);
        string backup = LegacyBackup(fixture);

        IndexedDbBackupSummary summary = IndexedDbBackupService.Inspect(backup);
        IReadOnlyDictionary<string, JsonElement> stores = IndexedDbBackupService.ReadStores(backup);

        Assert.Equal(fixture[KazoOsIndexedDbSchema.CatalogStoreName].Count, summary.StoreRecordCounts[KazoOsIndexedDbSchema.CatalogStoreName]);
        foreach ((string store, JsonElement records) in stores)
        {
            Assert.False(RenamedOsDatabaseRewriterTests.OldName().IsMatch(records.GetRawText()), store);
        }

        Assert.Contains(stores[KazoOsIndexedDbSchema.CatalogStoreName].EnumerateArray(), app => app.GetProperty("appId").GetString() == "org.kazoos.terminal");
    }

    [Fact]
    public void A_tampered_backup_made_before_3_0_0_is_still_refused()
    {
        string backup = LegacyBackup(RenamedOsDatabaseRewriterTests.ReadFixture(3)).Replace("org.hackeros.terminal", "org.hackeros.terminai", StringComparison.Ordinal);

        Assert.Throws<IndexedDbBackupValidationException>(() => IndexedDbBackupService.Inspect(backup));
    }

    /// <summary>A backup as HackerOS 2.x wrote it: database <c>hackeros</c>, no <c>osVersion</c>.</summary>
    private static string LegacyBackup(Dictionary<string, IReadOnlyList<JsonObject>> records)
    {
        JsonObject stores = [];
        foreach (IndexedDbObjectStoreDefinition store in KazoOsIndexedDbSchema.ObjectStores)
        {
            stores[store.Name] = new JsonArray([.. (records.GetValueOrDefault(store.Name) ?? []).Select(record => (JsonNode)record.DeepClone())]);
        }

        JsonObject payload = new()
        {
            ["formatVersion"] = 1,
            ["databaseName"] = "hackeros",
            ["databaseSchemaVersion"] = KazoOsIndexedDbSchema.CurrentVersion,
            ["createdAtUtc"] = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero).ToString("O"),
            ["stores"] = stores,
        };
        string hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(payload.ToJsonString())));
        payload["sha256"] = hash;
        return payload.ToJsonString();
    }

    private sealed class FakeBrowser(IEnumerable<string> databases) : IRenamedStorageBrowser
    {
        public HashSet<string> Databases { get; } = new(databases, StringComparer.Ordinal);
        public Dictionary<string, string> Local { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, string> Session { get; } = new(StringComparer.Ordinal);
        public List<string> Caches { get; } = [];
        public List<string> CopyTargets { get; } = [];
        public int Rewrites { get; private set; }
        public int ListCalls { get; private set; }
        public string? FailCopyOf { get; set; }

        public Task<IReadOnlyList<string>> ListDatabasesAsync(CancellationToken cancellationToken)
        {
            ListCalls++;
            return Task.FromResult<IReadOnlyList<string>>([.. Databases]);
        }

        public Task<bool> CopyDatabaseAsync(string source, string target, CancellationToken cancellationToken)
        {
            if (source == FailCopyOf)
            {
                throw new InvalidOperationException("The tab was closed.");
            }

            if (!Databases.Add(target))
            {
                return Task.FromResult(false);
            }

            CopyTargets.Add(target);
            return Task.FromResult(true);
        }

        public Task DeleteDatabaseAsync(string name, CancellationToken cancellationToken)
        {
            Databases.Remove(name);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<KeyValuePair<string, string>>> ReadStorageAsync(string area, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<KeyValuePair<string, string>>>([.. Area(area)]);

        public Task WriteStorageAsync(string area, string key, string value, CancellationToken cancellationToken)
        {
            Area(area)[key] = value;
            return Task.CompletedTask;
        }

        public Task RemoveStorageAsync(string area, string key, CancellationToken cancellationToken)
        {
            Area(area).Remove(key);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<string>> CacheNamesAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<string>>([.. Caches]);

        public Task DeleteCacheAsync(string name, CancellationToken cancellationToken)
        {
            Caches.Remove(name);
            return Task.CompletedTask;
        }

        public Task<int> RewriteOsDatabaseAsync(CancellationToken cancellationToken)
        {
            Rewrites++;
            return Task.FromResult(1);
        }

        private Dictionary<string, string> Area(string area) => area == "session" ? Session : Local;
    }
}
