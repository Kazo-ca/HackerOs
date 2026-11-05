using System.ComponentModel.DataAnnotations;

namespace Kazo.Os.Samples.Intranet.Models;

/// <summary>The columns of the task board, in display order (sent as <c>todo</c>, <c>doing</c>, <c>done</c>).</summary>
public static class BoardColumns
{
    /// <summary>Not started.</summary>
    public const string Todo = "todo";

    /// <summary>In progress.</summary>
    public const string Doing = "doing";

    /// <summary>Finished.</summary>
    public const string Done = "done";

    /// <summary>Every column, left to right.</summary>
    public static IReadOnlyList<string> All { get; } = [Todo, Doing, Done];

    /// <summary>Returns whether <paramref name="column"/> names a column.</summary>
    /// <param name="column">The value received by the API.</param>
    public static bool IsKnown(string? column) => column is Todo or Doing or Done;

    /// <summary>Returns the title shown above a column.</summary>
    /// <param name="column">The column.</param>
    public static string Title(string column) => column switch
    {
        Todo => "To do",
        Doing => "In progress",
        Done => "Done",
        _ => column,
    };
}

/// <summary>A card of the Blazor task board (<c>/board</c>), stored by the site and served by <c>/api/tasks</c>.</summary>
public sealed class BoardTask
{
    /// <summary>Gets or sets the identifier given by <see cref="Services.TaskBoard"/>.</summary>
    public int Id { get; set; }

    /// <summary>Gets or sets what has to be done (3 to 80 characters).</summary>
    [Required]
    [StringLength(80, MinimumLength = 3)]
    public string Title { get; set; } = string.Empty;

    /// <summary>Gets or sets who works on it (empty: nobody yet).</summary>
    [StringLength(40)]
    public string Assignee { get; set; } = string.Empty;

    /// <summary>Gets or sets the column: one of <see cref="BoardColumns.All"/>.</summary>
    [Required]
    public string Column { get; set; } = BoardColumns.Todo;

    /// <summary>Gets or sets when the card was created (UTC).</summary>
    public DateTimeOffset CreatedAt { get; set; }
}
