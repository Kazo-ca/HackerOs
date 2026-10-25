using System.Text.Json;
using HackerOs.Infrastructure.Browser.Schema;

namespace HackerOs.Infrastructure.Browser.Tests;

/// <summary>
/// Enforces the ADR 0099 section 4 rule without a browser: every released schema version older
/// than <see cref="HackerOsIndexedDbSchema.CurrentVersion"/> has a database dump under
/// <c>Tests/fixtures/indexeddb/</c>, and each dump is consistent with the schema it claims.
/// </summary>
/// <remarks>
/// The upgrade itself is exercised by <c>HackerOs.UI.E2E.Tests.IndexedDbUpgradeFixtureTests</c>;
/// this suite runs in every routine (non-E2E) test pass, so a chantier that raises
/// <c>CurrentVersion</c> without adding the dump of the previous version fails here at once.
/// </remarks>
public sealed class IndexedDbUpgradeFixtureCatalogTests
{
    /// <summary>
    /// The oldest version a dump exists for. Version 1 was never in any commit of this repository
    /// (its root commit <c>fdca68e</c> already declares version 2), so no build can produce it;
    /// see <c>doc/wasm/dev/building-and-testing.md</c>.
    /// </summary>
    private const int FirstReleasedVersion = 2;

    /// <summary>Every released version below the current one has its dump.</summary>
    [Fact]
    public void Every_previous_released_schema_version_has_a_fixture()
    {
        string directory = FixtureDirectory();
        for (int version = FirstReleasedVersion; version < HackerOsIndexedDbSchema.CurrentVersion; version++)
        {
            string path = Path.Combine(directory, $"hackeros-v{version}.json");
            Assert.True(
                File.Exists(path),
                $"Missing IndexedDB fixture for schema v{version} ({path}). A chantier that raises " +
                "HackerOsIndexedDbSchema.CurrentVersion adds the dump of the previous version " +
                "(procedure in doc/wasm/dev/building-and-testing.md).");
        }
    }

    /// <summary>Each dump names the canonical database, its own version and only known stores.</summary>
    /// <param name="version">The dumped schema version.</param>
    [Theory]
    [MemberData(nameof(FixtureVersions))]
    public void Fixture_matches_the_schema_of_its_version(int version)
    {
        using JsonDocument document = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(FixtureDirectory(), $"hackeros-v{version}.json")));
        JsonElement root = document.RootElement;

        Assert.Equal(HackerOsIndexedDbSchema.DatabaseName, root.GetProperty("databaseName").GetString());
        Assert.Equal(version, root.GetProperty("version").GetInt32());
        Assert.True(root.GetProperty("producedFrom").GetProperty("commit").GetString() is { Length: > 0 });

        JsonElement expectations = root.GetProperty("expectations");
        Assert.True(expectations.GetProperty("admin").GetProperty("password").GetString() is { Length: >= 8 });
        Assert.StartsWith("/", expectations.GetProperty("file").GetProperty("path").GetString());

        Dictionary<string, IndexedDbObjectStoreDefinition> current = HackerOsIndexedDbSchema.ObjectStores
            .ToDictionary(store => store.Name, StringComparer.Ordinal);
        foreach (JsonElement store in root.GetProperty("objectStores").EnumerateArray())
        {
            string name = store.GetProperty("name").GetString()!;
            // A store an older version had and the current one dropped would need a delete step;
            // none exists yet, so every dumped store must still be declared.
            Assert.True(current.TryGetValue(name, out IndexedDbObjectStoreDefinition? definition), $"Unknown store '{name}' in v{version}.");
            Assert.Equal(definition.AutoIncrement, store.GetProperty("autoIncrement").GetBoolean());
            Assert.Equal(definition.KeyPath, ReadKeyPath(store.GetProperty("keyPath")));
            Assert.Equal(JsonValueKind.Array, store.GetProperty("records").ValueKind);
        }

        // The users store must hold the fixture's administrator: the E2E test signs in with it.
        JsonElement users = root.GetProperty("objectStores").EnumerateArray()
            .Single(store => store.GetProperty("name").GetString() == HackerOsIndexedDbSchema.UserStoreName);
        string loginName = expectations.GetProperty("admin").GetProperty("loginName").GetString()!;
        Assert.Contains(users.GetProperty("records").EnumerateArray(), user => user.GetProperty("loginName").GetString() == loginName);
    }

    /// <summary>The versions that have a dump on disk.</summary>
    public static TheoryData<int> FixtureVersions()
    {
        TheoryData<int> data = [];
        for (int version = FirstReleasedVersion; version < HackerOsIndexedDbSchema.CurrentVersion; version++)
        {
            data.Add(version);
        }

        return data;
    }

    private static IReadOnlyList<string> ReadKeyPath(JsonElement keyPath) => keyPath.ValueKind switch
    {
        JsonValueKind.String => [keyPath.GetString()!],
        JsonValueKind.Array => [.. keyPath.EnumerateArray().Select(segment => segment.GetString()!)],
        _ => []
    };

    private static string FixtureDirectory()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "HackerOs.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return Path.Combine(directory.FullName, "Tests", "fixtures", "indexeddb");
    }
}
