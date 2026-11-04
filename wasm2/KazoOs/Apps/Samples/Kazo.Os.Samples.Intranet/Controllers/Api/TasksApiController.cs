using Kazo.Os.AppSdk.Web.Mvc;
using Kazo.Os.Samples.Intranet.Models;
using Kazo.Os.Samples.Intranet.Services;

namespace Kazo.Os.Samples.Intranet.Controllers.Api;

/// <summary>
/// The API of the Blazor task board (<c>/board</c>): its pages, drawn by a renderer process of the
/// site (ADR 0084), call these routes through <c>Site.Http</c> with the tab's cookies.
/// </summary>
[Route("api/tasks")]
public sealed class TasksApiController(TaskBoard board) : HackApiController
{
    /// <summary><c>GET /api/tasks</c>: every card.</summary>
    [HttpGet]
    public IReadOnlyList<BoardTask> List() => board.All();

    /// <summary><c>GET /api/tasks/{id}</c>.</summary>
    /// <param name="id">The identifier.</param>
    [HttpGet("{id:int}", Name = "GetTask")]
    public IHackActionResult Get(int id) => board.Find(id) is BoardTask task ? Ok(task) : NotFound();

    /// <summary><c>POST /api/tasks</c>: a new card, in <c>todo</c> unless the body names another column.</summary>
    /// <param name="task">The card (JSON body).</param>
    [HttpPost]
    public IHackActionResult Create(BoardTask task)
    {
        if (!BoardColumns.IsKnown(task.Column))
        {
            ModelState.AddModelError(nameof(BoardTask.Column), $"The column must be one of: {string.Join(", ", BoardColumns.All)}.");
            return ValidationProblem();
        }

        BoardTask created = board.Add(task);
        return CreatedAtAction(nameof(Get), new { id = created.Id }, created);
    }

    /// <summary><c>PUT /api/tasks/{id}</c>: replaces the card (the board moves a card by changing its column).</summary>
    /// <param name="id">The identifier.</param>
    /// <param name="task">The new content (JSON body).</param>
    [HttpPut("{id:int}")]
    public IHackActionResult Replace(int id, BoardTask task)
    {
        if (!BoardColumns.IsKnown(task.Column))
        {
            ModelState.AddModelError(nameof(BoardTask.Column), $"The column must be one of: {string.Join(", ", BoardColumns.All)}.");
            return ValidationProblem();
        }

        task.Id = id;
        return board.Update(task) is BoardTask saved ? Ok(saved) : NotFound();
    }

    /// <summary><c>DELETE /api/tasks/{id}</c>.</summary>
    /// <param name="id">The identifier.</param>
    [HttpDelete("{id:int}")]
    public IHackActionResult Delete(int id) => board.Remove(id) ? NoContent() : NotFound();
}
