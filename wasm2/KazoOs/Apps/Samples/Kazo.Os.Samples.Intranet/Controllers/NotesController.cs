using Kazo.Os.AppSdk.Web.Antiforgery;
using Kazo.Os.AppSdk.Web.Mvc;
using Kazo.Os.Samples.Intranet.Models;
using Kazo.Os.Samples.Intranet.Services;
using Microsoft.Extensions.Logging;

namespace Kazo.Os.Samples.Intranet.Controllers;

/// <summary>
/// The pages of the notes wiki: a classic MVC CRUD over HTML forms with anti-forgery tokens,
/// validation messages and post/redirect/get. Its views are <c>.razor</c> components
/// (<c>Views/Notes/</c>, ADR 0083) rendered to static HTML. The notes live in the site's browser
/// database (<see cref="NoteStore"/>, ADR 0088), and each change is written to the site's debug log
/// (<c>/var/log/kazoweb/org.kazoos.samples.intranet.log</c>) through <see cref="ILogger{TCategoryName}"/>.
/// </summary>
public sealed class NotesController(NoteStore store, ILogger<NotesController> logger) : KazoController
{
    /// <summary><c>GET /Notes</c>: the list.</summary>
    /// <param name="cancellationToken">Aborted with the request.</param>
    public async Task<IKazoActionResult> Index(CancellationToken cancellationToken) => View(await store.AllAsync(cancellationToken));

    /// <summary><c>GET /Notes/Details/{id}</c>.</summary>
    /// <param name="id">The identifier.</param>
    /// <param name="cancellationToken">Aborted with the request.</param>
    public async Task<IKazoActionResult> Details(int id, CancellationToken cancellationToken) =>
        await store.FindAsync(id, cancellationToken) is Note note ? View(note) : NotFound();

    /// <summary><c>GET /Notes/Create</c>: the empty form (the <c>Edit</c> view).</summary>
    [HttpGet]
    public IKazoActionResult Create() => View("Edit", new Note());

    /// <summary><c>POST /Notes/Create</c>.</summary>
    /// <param name="note">The posted form.</param>
    [HttpPost]
    [ValidateAntiForgeryToken]
    /// <param name="cancellationToken">Aborted with the request.</param>
    public async Task<IKazoActionResult> Create(Note note, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            logger.LogDebug("Note form refused: {ErrorCount} error(s)", ModelState.ErrorCount);
            return View("Edit", note);
        }

        note.Author = IntranetSession.DisplayName(HttpContext) ?? IntranetSession.Anonymous;
        Note created = await store.AddAsync(note, cancellationToken);
        logger.LogInformation("Note {NoteId} created by {Author}", created.Id, created.Author);
        return RedirectToAction(nameof(Details), new { id = created.Id });
    }

    /// <summary><c>GET /Notes/Edit/{id}</c>.</summary>
    /// <param name="id">The identifier.</param>
    [HttpGet]
    /// <param name="cancellationToken">Aborted with the request.</param>
    public async Task<IKazoActionResult> Edit(int id, CancellationToken cancellationToken) =>
        await store.FindAsync(id, cancellationToken) is Note note ? View(note) : NotFound();

    /// <summary><c>POST /Notes/Edit/{id}</c>: the form shown again with its errors, or the saved note.</summary>
    /// <param name="id">The identifier (route).</param>
    /// <param name="note">The posted form.</param>
    /// <param name="cancellationToken">Aborted with the request.</param>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IKazoActionResult> Edit(int id, Note note, CancellationToken cancellationToken)
    {
        note.Id = id;
        if (!ModelState.IsValid)
        {
            return View(note);
        }

        note.Author = IntranetSession.DisplayName(HttpContext) ?? IntranetSession.Anonymous;
        if (await store.UpdateAsync(note, cancellationToken) is null)
        {
            return NotFound();
        }

        logger.LogInformation("Note {NoteId} updated by {Author}", id, note.Author);
        return RedirectToAction(nameof(Details), new { id });
    }

    /// <summary><c>POST /Notes/Delete/{id}</c>.</summary>
    /// <param name="id">The identifier.</param>
    /// <param name="cancellationToken">Aborted with the request.</param>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IKazoActionResult> Delete(int id, CancellationToken cancellationToken)
    {
        if (await store.RemoveAsync(id, cancellationToken))
        {
            logger.LogInformation("Note {NoteId} deleted", id);
        }

        return RedirectToAction(nameof(Index));
    }
}
