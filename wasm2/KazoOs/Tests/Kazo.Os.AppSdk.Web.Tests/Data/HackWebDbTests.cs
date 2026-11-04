using System.Text.Json;
using System.Text.Json.Serialization;
using Kazo.Os.AppSdk.Web.Data;
using Kazo.Os.AppSdk.Web.Hosting;
using Kazo.Os.AppSdk.WebData;
using Microsoft.Extensions.DependencyInjection;

namespace Kazo.Os.AppSdk.Web.Tests.Data;

/// <summary>
/// The site database of the web SDK (ADR 0088 §2-3) over the in-memory engine: the IndexedDB rules a
/// site relies on (key generator, in-line keys, indexes, key order, ranges, transactions, versions).
/// </summary>
public sealed class HackWebDbTests
{
    internal sealed class Contact
    {
        // As in a browser, a record that carries id 0 is stored under key 0: leaving the default out
        // lets the key generator number it.
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public int Id { get; set; }

        public string Name { get; set; } = string.Empty;

        public string Email { get; set; } = string.Empty;

        public string[] Tags { get; set; } = [];
    }

    private static HackWebDb Create(Action<HackWebDbSchemaBuilder> schema, IAppWebDatabaseGateway? gateway = null)
    {
        HackWebDbSchemaBuilder builder = new();
        schema(builder);
        return new HackWebDb(gateway ?? new HackWebDbInMemoryBackend("hackweb.test"), builder, Json());
    }

    /// <summary>The site's JSON options, with its source-generated context as a site registers it.</summary>
    private static HackWebJsonOptions Json()
    {
        HackWebJsonOptions json = new();
        json.SerializerOptions.TypeInfoResolverChain.Insert(0, HackWebDbTestJsonContext.Default);
        return json;
    }

    private static void Contacts(HackWebDbSchemaBuilder schema) => schema.Version(1, version => version
        .CreateStore("contacts", keyPath: "id", autoIncrement: true)
        .CreateIndex("contacts", "byEmail", "email", unique: true)
        .CreateIndex("contacts", "byTag", "tags", multiEntry: true));

    [Fact]
    public async Task The_key_generator_numbers_records_and_writes_the_key_into_them()
    {
        HackWebDb db = Create(Contacts);
        HackWebDbStore<Contact> contacts = db.Store<Contact>("contacts");

        HackWebDbKey first = await contacts.AddAsync(new Contact { Name = "Ada", Email = "ada@hack" });
        HackWebDbKey second = await contacts.AddAsync(new Contact { Name = "Linus", Email = "linus@hack" });

        Assert.Equal(1, first.AsInt64());
        Assert.Equal(2, second.AsInt64());
        Contact? stored = await contacts.GetAsync(2);
        Assert.NotNull(stored);
        Assert.Equal(2, stored.Id);
        Assert.Equal("Linus", stored.Name);
    }

    [Fact]
    public async Task A_unique_index_refuses_a_duplicate_and_add_refuses_an_existing_key()
    {
        HackWebDb db = Create(Contacts);
        HackWebDbStore<Contact> contacts = db.Store<Contact>("contacts");
        await contacts.AddAsync(new Contact { Name = "Ada", Email = "ada@hack" });

        HackWebDbException duplicate = await Assert.ThrowsAsync<HackWebDbException>(() =>
            contacts.AddAsync(new Contact { Name = "Other", Email = "ada@hack" }));
        HackWebDbException existing = await Assert.ThrowsAsync<HackWebDbException>(() =>
            contacts.AddAsync(new Contact { Id = 1, Name = "Again", Email = "again@hack" }));

        Assert.Equal(HackWebDbException.ConstraintError, duplicate.Name);
        Assert.Equal(HackWebDbException.ConstraintError, existing.Name);
        Assert.Equal(1, await contacts.CountAsync());
    }

    [Fact]
    public async Task Indexes_find_records_by_value_and_by_each_entry_of_an_array()
    {
        HackWebDb db = Create(Contacts);
        HackWebDbStore<Contact> contacts = db.Store<Contact>("contacts");
        await contacts.AddAsync(new Contact { Name = "Ada", Email = "ada@hack", Tags = ["admin", "dev"] });
        await contacts.AddAsync(new Contact { Name = "Linus", Email = "linus@hack", Tags = ["dev"] });

        Contact? byEmail = await contacts.Index("byEmail").GetAsync("linus@hack");
        IReadOnlyList<Contact> devs = await contacts.Index("byTag").GetAllAsync("dev");
        int admins = await contacts.Index("byTag").CountAsync(HackWebDbKeyRange.Only("admin"));

        Assert.Equal("Linus", byEmail?.Name);
        Assert.Equal(["Ada", "Linus"], devs.Select(contact => contact.Name));
        Assert.Equal(1, admins);
    }

    [Fact]
    public async Task Keys_sort_numbers_before_strings_before_arrays_and_ranges_select_them()
    {
        HackWebDb db = Create(schema => schema.Version(1, version => version.CreateStore("kv")));
        HackWebDbStore<string> kv = db.Store<string>("kv");
        await kv.PutAsync("array", HackWebDbKey.Array("a", 1));
        await kv.PutAsync("b", "b");
        await kv.PutAsync("ten", 10);
        await kv.PutAsync("two", 2);
        await kv.PutAsync("a", "a");

        IReadOnlyList<string> all = await kv.GetAllAsync();
        IReadOnlyList<string> numbers = await kv.GetAllAsync(HackWebDbKeyRange.UpperBound(100));
        IReadOnlyList<string> afterA = await kv.GetAllAsync(HackWebDbKeyRange.Bound("a", "z", lowerOpen: true));

        Assert.Equal(["two", "ten", "a", "b", "array"], all);
        Assert.Equal(["two", "ten"], numbers);
        Assert.Equal(["b"], afterA);
    }

    [Fact]
    public async Task A_failed_transaction_writes_nothing()
    {
        HackWebDb db = Create(Contacts);
        HackWebDbStore<Contact> contacts = db.Store<Contact>("contacts");
        await contacts.AddAsync(new Contact { Name = "Ada", Email = "ada@hack" });

        HackWebDbTransaction transaction = db.BeginTransaction("contacts");
        transaction.Store<Contact>("contacts")
            .Add(new Contact { Name = "Grace", Email = "grace@hack" })
            .Add(new Contact { Name = "Copy", Email = "ada@hack" });
        await Assert.ThrowsAsync<HackWebDbException>(() => transaction.CommitAsync());

        Assert.Equal(1, await contacts.CountAsync());
        Assert.Null(await contacts.Index("byEmail").GetAsync("grace@hack"));
    }

    [Fact]
    public async Task A_transaction_returns_the_keys_of_its_writes()
    {
        HackWebDb db = Create(Contacts);
        HackWebDbTransaction transaction = db.BeginTransaction("contacts");
        transaction.Store<Contact>("contacts")
            .Add(new Contact { Name = "Ada", Email = "ada@hack" })
            .Put(new Contact { Name = "Grace", Email = "grace@hack" })
            .Delete(1);

        IReadOnlyList<HackWebDbKey?> keys = await transaction.CommitAsync();

        Assert.Equal(3, keys.Count);
        Assert.Equal(1, keys[0]?.AsInt64());
        Assert.Equal(2, keys[1]?.AsInt64());
        Assert.Equal(1, await db.Store<Contact>("contacts").CountAsync());
    }

    [Fact]
    public async Task A_new_version_migrates_the_data_kept_by_the_previous_one()
    {
        HackWebDbInMemoryBackend backend = new("hackweb.test");
        HackWebDb v1 = Create(schema => schema.Version(1, version => version.CreateStore("notes", keyPath: "id", autoIncrement: true)), backend);
        await v1.Store<Contact>("notes").AddAsync(new Contact { Name = "kept", Email = "kept@hack" });

        HackWebDb v2 = Create(schema => schema
            .Version(1, version => version.CreateStore("notes", keyPath: "id", autoIncrement: true))
            .Version(2, version => version.CreateIndex("notes", "byName", "name").CreateStore("settings")), backend);

        Contact? kept = await v2.Store<Contact>("notes").Index("byName").GetAsync("kept");
        Assert.Equal(2, v2.Version);
        Assert.Equal("kept@hack", kept?.Email);

        // The database is now at version 2: a site going back to version 1 gets a VersionError.
        HackWebDb old = Create(schema => schema.Version(1, version => version.CreateStore("notes", keyPath: "id", autoIncrement: true)), backend);
        HackWebDbException error = await Assert.ThrowsAsync<HackWebDbException>(() => old.Store<Contact>("notes").CountAsync());
        Assert.Equal(HackWebDbException.VersionError, error.Name);
    }

    [Fact]
    public void Versions_must_follow_each_other()
    {
        // AddHackWebDb checks the versions at start-up, not at the first request.
        Assert.ThrowsAny<Exception>(() => new ServiceCollection().AddHackWebDb(builder =>
            builder.Version(1, version => version.CreateStore("a")).Version(3, version => version.CreateStore("b"))));
    }

    [Fact]
    public async Task An_unknown_store_is_a_NotFoundError()
    {
        HackWebDb db = Create(Contacts);

        HackWebDbException error = await Assert.ThrowsAsync<HackWebDbException>(() => db.Store<string>("missing").GetAllAsync());

        Assert.Equal(HackWebDbException.NotFoundError, error.Name);
    }

    [Fact]
    public async Task A_site_built_outside_HackerOS_gets_an_in_memory_database()
    {
        ServiceCollection services = new();
        services.AddSingleton(Json());
        services.AddHackWebDb(Contacts);
        await using ServiceProvider provider = services.BuildServiceProvider();

        HackWebDb db = provider.GetRequiredService<HackWebDb>();
        await db.Store<Contact>("contacts").AddAsync(new Contact { Name = "Ada", Email = "ada@hack" });

        Assert.Equal(HackWebDbServiceCollectionExtensions.InMemoryName, db.Name);
        Assert.Equal(1, await provider.GetRequiredService<HackWebDb>().Store<Contact>("contacts").CountAsync());
    }

    [Fact]
    public async Task A_registered_gateway_wins_over_the_in_memory_fallback()
    {
        ServiceCollection services = new();
        HackWebDbInMemoryBackend backend = new(HackWebDbNames.ForApp("com.example.site"));
        services.AddSingleton<IAppWebDatabaseGateway>(backend);
        services.AddSingleton(Json());
        services.AddHackWebDb(Contacts);
        await using ServiceProvider provider = services.BuildServiceProvider();

        HackWebDb db = provider.GetRequiredService<HackWebDb>();

        Assert.Equal("hackweb.com.example.site", db.Name);
        Assert.Equal("com.example.site", HackWebDbNames.AppIdOf(db.Name));
    }
}

/// <summary>The test site's JSON context (strings come from the SDK's fallback).</summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(HackWebDbTests.Contact))]
internal sealed partial class HackWebDbTestJsonContext : JsonSerializerContext;
