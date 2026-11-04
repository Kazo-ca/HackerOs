using Kazo.Os.AppSdk.Web.Data;
using Kazo.Os.Samples.Intranet.Models;

namespace Kazo.Os.Samples.Intranet.Services;

/// <summary>
/// The notes of the site, kept in the site's browser database <c>hackweb.org.hackeros.samples.intranet</c>
/// (<see cref="HackWebDb"/>, ADR 0088): an IndexedDB database of the host browser, outside the OS files.
/// </summary>
/// <remarks>
/// A site has no <c>filesystem.*</c> capability (ADR 0073 §3). Its notes survive the OS stopping the
/// site and the machine restarting; they go away when the site is uninstalled or when the Diagnostic
/// app clears its database. The schema (<see cref="Configure"/>) is an IndexedDB one: a <c>notes</c> store
/// keyed by <c>id</c> with a key generator and a <c>byAuthor</c> index, and a <c>meta</c> store that
/// remembers the welcome notes were written once.
/// </remarks>
public sealed class NoteStore(HackWebDb database, TimeProvider time)
{
    /// <summary>The object store of the notes.</summary>
    public const string StoreName = "notes";

    /// <summary>The index of the notes by author.</summary>
    public const string ByAuthorIndex = "byAuthor";

    private const string MetaStore = "meta";
    private const string SeededKey = "seeded";

    private readonly SemaphoreSlim _seedGate = new(1, 1);
    private bool _seeded;

    private HackWebDbStore<Note> Notes => database.Store<Note>(StoreName);

    /// <summary>Declares the database versions, in the site's <c>Configure</c>.</summary>
    /// <param name="schema">The schema builder of <c>AddHackWebDb</c>.</param>
    public static void Configure(HackWebDbSchemaBuilder schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        schema.Version(1, version => version
            .CreateStore(StoreName, keyPath: "id", autoIncrement: true)
            .CreateIndex(StoreName, ByAuthorIndex, "author")
            .CreateStore(MetaStore));
    }

    /// <summary>Returns every note, pinned ones first, then the most recent.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    public async Task<IReadOnlyList<Note>> AllAsync(CancellationToken cancellationToken = default)
    {
        await SeedAsync(cancellationToken).ConfigureAwait(false);
        return Sort(await Notes.GetAllAsync(cancellationToken: cancellationToken).ConfigureAwait(false));
    }

    /// <summary>Returns the notes of one author, through the <c>byAuthor</c> index.</summary>
    /// <param name="author">The author's display name.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    public async Task<IReadOnlyList<Note>> ByAuthorAsync(string author, CancellationToken cancellationToken = default)
    {
        await SeedAsync(cancellationToken).ConfigureAwait(false);
        return Sort(await Notes.Index(ByAuthorIndex).GetAllAsync(author, cancellationToken: cancellationToken).ConfigureAwait(false));
    }

    /// <summary>Returns the number of notes.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    public async Task<int> CountAsync(CancellationToken cancellationToken = default)
    {
        await SeedAsync(cancellationToken).ConfigureAwait(false);
        return await Notes.CountAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Returns the note <paramref name="id"/>, or <see langword="null"/>.</summary>
    /// <param name="id">The identifier.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    public async Task<Note?> FindAsync(int id, CancellationToken cancellationToken = default)
    {
        await SeedAsync(cancellationToken).ConfigureAwait(false);
        return await Notes.GetAsync(id, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Stores a new note and returns it with the identifier the key generator gave and its date.</summary>
    /// <param name="note">The note to add; its <see cref="Note.Id"/> is ignored.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    public async Task<Note> AddAsync(Note note, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(note);
        await SeedAsync(cancellationToken).ConfigureAwait(false);
        Note stored = Copy(note);
        stored.Id = 0; // Not written (JsonIgnore on default): the store's key generator numbers it.
        stored.UpdatedAt = time.GetUtcNow();
        stored.Id = (int)(await Notes.AddAsync(stored, cancellationToken: cancellationToken).ConfigureAwait(false)).AsInt64();
        return stored;
    }

    /// <summary>Replaces the note of the same identifier; returns <see langword="null"/> when there is none.</summary>
    /// <param name="note">The new content.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    public async Task<Note?> UpdateAsync(Note note, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(note);
        if (note.Id <= 0 || await FindAsync(note.Id, cancellationToken).ConfigureAwait(false) is null)
        {
            return null;
        }

        Note stored = Copy(note);
        stored.UpdatedAt = time.GetUtcNow();
        await Notes.PutAsync(stored, cancellationToken: cancellationToken).ConfigureAwait(false);
        return stored;
    }

    /// <summary>Removes the note <paramref name="id"/>; returns whether it existed.</summary>
    /// <param name="id">The identifier.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    public async Task<bool> RemoveAsync(int id, CancellationToken cancellationToken = default)
    {
        if (await FindAsync(id, cancellationToken).ConfigureAwait(false) is null)
        {
            return false;
        }

        await Notes.DeleteAsync(id, cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Writes the welcome notes the first time the database is used, with the marker that says so, in one
    /// transaction: a database whose notes were all deleted stays empty.
    /// </summary>
    private async Task SeedAsync(CancellationToken cancellationToken)
    {
        if (_seeded)
        {
            return;
        }

        await _seedGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_seeded)
            {
                return;
            }

            if (await database.Store<string>(MetaStore).GetAsync(SeededKey, cancellationToken).ConfigureAwait(false) is null)
            {
                DateTimeOffset now = time.GetUtcNow();
                HackWebDbTransaction seed = database.BeginTransaction(StoreName, MetaStore);
                HackWebDbTransactionStore<Note> notes = seed.Store<Note>(StoreName);
                notes.Add(new Note { Title = "Welcome to the intranet", Body = "Notes are kept in the site's browser database (hackweb.org.hackeros.samples.intranet): they survive restarts.", Pinned = true, Author = "it-ops", UpdatedAt = now });
                notes.Add(new Note { Title = "VPN maintenance", Body = "The gateway at 10.0.0.1 reboots Friday at 22:00.", Author = "it-ops", UpdatedAt = now });
                notes.Add(new Note { Title = "Coffee machine", Body = "Second floor machine fixed. Do not feed it tea bags.", Author = "facilities", UpdatedAt = now });
                seed.Store<string>(MetaStore).Put(now.ToString("O", System.Globalization.CultureInfo.InvariantCulture), SeededKey);
                await seed.CommitAsync(cancellationToken).ConfigureAwait(false);
            }

            _seeded = true;
        }
        finally
        {
            _seedGate.Release();
        }
    }

    private static IReadOnlyList<Note> Sort(IReadOnlyList<Note> notes) =>
        [.. notes.OrderByDescending(note => note.Pinned).ThenByDescending(note => note.UpdatedAt).ThenByDescending(note => note.Id)];

    private static Note Copy(Note note) => new()
    {
        Id = note.Id,
        Title = note.Title,
        Body = note.Body,
        Pinned = note.Pinned,
        Author = note.Author,
        UpdatedAt = note.UpdatedAt,
    };
}
