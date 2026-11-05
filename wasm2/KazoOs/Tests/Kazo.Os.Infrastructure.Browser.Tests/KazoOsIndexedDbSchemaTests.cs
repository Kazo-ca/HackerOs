using Kazo.Os.Infrastructure.Browser.Schema;

namespace Kazo.Os.Infrastructure.Browser.Tests;

/// <summary>
/// Verifies the <c>P2-IDB-002</c> IndexedDB schema declaration: identity constants, the expected
/// object store set, validation invariants, and cross-referential transaction boundaries.
/// </summary>
public sealed class KazoOsIndexedDbSchemaTests
{
    [Fact]
    public void DatabaseIdentity_MatchesAdr0015()
    {
        Assert.Equal("kazoos", KazoOsIndexedDbSchema.DatabaseName);
        Assert.Equal(4, KazoOsIndexedDbSchema.CurrentVersion);
    }

    [Fact]
    public void ObjectStores_ContainExactlyTheExpectedNamesWithNoDuplicates()
    {
        string[] expected =
        [
            "users", "groups", "sessions", "settings", "fsEntries", "fsLinks", "fsContent",
            "catalog", "grants", "audit", "diagnostics", "syncMetadata", "serverConnection",
            "syncCursors", "syncRecordState"
        ];

        string[] actual = [.. KazoOsIndexedDbSchema.ObjectStores.Select(store => store.Name)];

        Assert.Equal(expected.Length, actual.Length);
        Assert.Equal(expected.Distinct().Count(), actual.Distinct().Count());
        foreach (string name in expected)
        {
            Assert.Contains(name, actual);
        }
    }

    [Fact]
    public void ObjectStores_HaveNoEmptyKeyPathSegmentsAndUniqueIndexNames()
    {
        foreach (IndexedDbObjectStoreDefinition store in KazoOsIndexedDbSchema.ObjectStores)
        {
            Assert.All(store.KeyPath, segment => Assert.False(string.IsNullOrWhiteSpace(segment)));

            string[] indexNames = [.. store.Indexes.Select(index => index.Name)];
            Assert.Equal(indexNames.Distinct(StringComparer.Ordinal).Count(), indexNames.Length);
        }
    }

    [Fact]
    public void AutoIncrementStores_HaveAtMostOneKeyPathSegment()
    {
        foreach (IndexedDbObjectStoreDefinition store in KazoOsIndexedDbSchema.ObjectStores.Where(s => s.AutoIncrement))
        {
            Assert.True(store.KeyPath.Count <= 1);
        }
    }

    [Fact]
    public void FileSystemLinkStore_UsesCompoundParentAndNameKey()
    {
        IndexedDbObjectStoreDefinition fsLinks = KazoOsIndexedDbSchema.ObjectStores
            .Single(store => store.Name == KazoOsIndexedDbSchema.FileSystemLinkStoreName);

        Assert.Equal(["parentId", "name"], fsLinks.KeyPath);
        Assert.Contains(fsLinks.Indexes, index => index.Name == "parentId");
        Assert.Contains(fsLinks.Indexes, index => index.Name == "entryId");
    }

    [Fact]
    public void FileContentStore_UsesDeduplicatedContentHashAndChunkIndexKey()
    {
        IndexedDbObjectStoreDefinition fsContent = KazoOsIndexedDbSchema.ObjectStores
            .Single(store => store.Name == KazoOsIndexedDbSchema.FileContentStoreName);

        Assert.Equal(["contentHash", "chunkIndex"], fsContent.KeyPath);
        Assert.Contains(fsContent.Indexes, index => index.Name == "contentHash");
    }

    [Fact]
    public void TransactionBoundaries_OnlyReferenceDeclaredObjectStores()
    {
        HashSet<string> declaredStores = [.. KazoOsIndexedDbSchema.ObjectStores.Select(store => store.Name)];

        foreach (IndexedDbTransactionBoundary boundary in KazoOsIndexedDbSchema.TransactionBoundaries)
        {
            foreach (string storeName in boundary.ObjectStoreNames)
            {
                Assert.Contains(storeName, declaredStores);
            }
        }
    }

    [Fact]
    public void TransactionBoundaries_HaveUniqueNames()
    {
        string[] names = [.. KazoOsIndexedDbSchema.TransactionBoundaries.Select(boundary => boundary.Name)];
        Assert.Equal(names.Distinct(StringComparer.Ordinal).Count(), names.Length);
    }

    /// <summary>
    /// Every repository names its boundary through a private <c>TransactionBoundary</c> constant;
    /// an undeclared name only fails at runtime inside the browser (it once crashed the boot via
    /// the settings-to-files migration), so this checks them all against the schema.
    /// </summary>
    [Fact]
    public void RepositoryTransactionBoundaryConstants_AreAllDeclared()
    {
        HashSet<string> declared = [.. KazoOsIndexedDbSchema.TransactionBoundaries.Select(b => b.Name)];
        var constants = typeof(KazoOsIndexedDbSchema).Assembly.GetTypes()
            .SelectMany(type => type.GetFields(
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.Static))
            .Where(field => field.IsLiteral && field.Name == "TransactionBoundary" && field.FieldType == typeof(string))
            .Select(field => (Type: field.DeclaringType!.Name, Name: (string)field.GetRawConstantValue()!))
            .ToList();

        Assert.NotEmpty(constants);
        Assert.Contains(constants, constant => constant.Type == "IndexedDbLegacySettingsStore");
        foreach (var constant in constants)
        {
            Assert.True(declared.Contains(constant.Name), $"{constant.Type} uses undeclared boundary '{constant.Name}'.");
        }
    }

    [Fact]
    public void PolicyGrantMutation_CoversGrantAuditAndRevisionStoresTogether()
    {
        IndexedDbTransactionBoundary boundary = KazoOsIndexedDbSchema.TransactionBoundaries
            .Single(b => b.Name == "PolicyGrantMutation");

        Assert.Equal(
            [
                KazoOsIndexedDbSchema.GrantStoreName,
                KazoOsIndexedDbSchema.AuditStoreName,
                KazoOsIndexedDbSchema.LocalBookkeepingStoreName
            ],
            boundary.ObjectStoreNames);
    }

    [Fact]
    public void IndexedDbIndexDefinition_RejectsEmptyKeyPath()
    {
        Assert.Throws<ArgumentException>(() => new IndexedDbIndexDefinition("bad", []));
    }

    [Fact]
    public void IndexedDbIndexDefinition_RejectsEmptyKeyPathSegment()
    {
        Assert.Throws<ArgumentException>(() => new IndexedDbIndexDefinition("bad", [""]));
    }

    [Fact]
    public void IndexedDbObjectStoreDefinition_RejectsDuplicateIndexNames()
    {
        Assert.Throws<ArgumentException>(() => new IndexedDbObjectStoreDefinition(
            "dup",
            keyPath: ["id"],
            autoIncrement: false,
            indexes:
            [
                new IndexedDbIndexDefinition("same", ["a"]),
                new IndexedDbIndexDefinition("same", ["b"])
            ],
            purpose: "test"));
    }

    [Fact]
    public void IndexedDbObjectStoreDefinition_RejectsAutoIncrementWithCompoundKey()
    {
        Assert.Throws<ArgumentException>(() => new IndexedDbObjectStoreDefinition(
            "bad",
            keyPath: ["a", "b"],
            autoIncrement: true,
            indexes: [],
            purpose: "test"));
    }

    [Fact]
    public void IndexedDbTransactionBoundary_RequiresAtLeastOneObjectStore()
    {
        Assert.Throws<ArgumentException>(() => new IndexedDbTransactionBoundary("empty", [], "test"));
    }
}
