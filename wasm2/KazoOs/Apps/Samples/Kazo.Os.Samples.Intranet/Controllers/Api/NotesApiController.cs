using Kazo.Os.AppSdk.Web.Mvc;
using Kazo.Os.Samples.Intranet.Models;
using Kazo.Os.Samples.Intranet.Services;

namespace Kazo.Os.Samples.Intranet.Controllers.Api;

/// <summary>
/// The REST API of the notes: every HTTP verb of a resource, as an ASP.NET Core <c>[ApiController]</c>.
/// JSON in and out, <c>400</c> with a problem document when the body breaks the <see cref="Note"/>
/// rules, <c>404</c> problem for an unknown identifier. The <c>/Notes</c> pages call the same store;
/// the script of the notes list (<c>wwwroot/js/notes.js</c>) calls <c>PATCH</c> to pin a note.
/// </summary>
[Route("api/notes")]
public sealed class NotesApiController(NoteStore store) : HackApiController
{
    /// <summary>
    /// <c>GET /api/notes?q=&amp;author=</c>: every note, those of one author (read through the
    /// database's <c>byAuthor</c> index), and of those, the ones whose title or text contains <paramref name="q"/>.
    /// </summary>
    /// <param name="q">The search text.</param>
    /// <param name="author">The author's display name.</param>
    /// <param name="cancellationToken">Aborted with the request.</param>
    [HttpGet]
    public async Task<IReadOnlyList<Note>> List([FromQuery] string? q = null, [FromQuery] string? author = null, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<Note> notes = string.IsNullOrWhiteSpace(author)
            ? await store.AllAsync(cancellationToken)
            : await store.ByAuthorAsync(author, cancellationToken);
        return string.IsNullOrWhiteSpace(q)
            ? notes
            : [.. notes.Where(note => note.Title.Contains(q, StringComparison.OrdinalIgnoreCase) || note.Body.Contains(q, StringComparison.OrdinalIgnoreCase))];
    }

    /// <summary><c>GET /api/notes/{id}</c>.</summary>
    /// <param name="id">The identifier.</param>
    /// <param name="cancellationToken">Aborted with the request.</param>
    [HttpGet("{id:int}", Name = "GetNote")]
    public async Task<IHackActionResult> Get(int id, CancellationToken cancellationToken) =>
        await store.FindAsync(id, cancellationToken) is Note note ? Ok(note) : NotFound();

    /// <summary><c>POST /api/notes</c>: <c>201 Created</c> with the <c>Location</c> of the new note.</summary>
    /// <param name="note">The note (JSON body); the identifier is given by the store.</param>
    /// <param name="cancellationToken">Aborted with the request.</param>
    [HttpPost]
    public async Task<IHackActionResult> Create(Note note, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(note.Author))
        {
            note.Author = IntranetSession.DisplayName(HttpContext) ?? IntranetSession.Anonymous;
        }

        Note created = await store.AddAsync(note, cancellationToken);
        return CreatedAtAction(nameof(Get), new { id = created.Id }, created);
    }

    /// <summary><c>PUT /api/notes/{id}</c>: replaces the whole note.</summary>
    /// <param name="id">The identifier.</param>
    /// <param name="note">The new content (JSON body).</param>
    /// <param name="cancellationToken">Aborted with the request.</param>
    [HttpPut("{id:int}")]
    public async Task<IHackActionResult> Replace(int id, Note note, CancellationToken cancellationToken)
    {
        note.Id = id;
        return await store.UpdateAsync(note, cancellationToken) is Note saved ? Ok(saved) : NotFound();
    }

    /// <summary><c>PATCH /api/notes/{id}</c>: changes only the fields given, then validates the result.</summary>
    /// <param name="id">The identifier.</param>
    /// <param name="patch">The fields to change (JSON body).</param>
    /// <param name="cancellationToken">Aborted with the request.</param>
    [HttpPatch("{id:int}")]
    public async Task<IHackActionResult> Patch(int id, NotePatch patch, CancellationToken cancellationToken)
    {
        if (await store.FindAsync(id, cancellationToken) is not Note note)
        {
            return NotFound();
        }

        note.Title = patch.Title ?? note.Title;
        note.Body = patch.Body ?? note.Body;
        note.Pinned = patch.Pinned ?? note.Pinned;

        // The patched note must still follow the rules a full body is checked against.
        if (!TryValidateModel(note))
        {
            return ValidationProblem();
        }

        return Ok(await store.UpdateAsync(note, cancellationToken));
    }

    /// <summary><c>DELETE /api/notes/{id}</c>: <c>204 No Content</c>, or <c>404</c>.</summary>
    /// <param name="id">The identifier.</param>
    /// <param name="cancellationToken">Aborted with the request.</param>
    [HttpDelete("{id:int}")]
    public async Task<IHackActionResult> Delete(int id, CancellationToken cancellationToken) =>
        await store.RemoveAsync(id, cancellationToken) ? NoContent() : NotFound();
}
