using Kazo.Os.Samples.Intranet.Models;

namespace Kazo.Os.Samples.Intranet.Services;

/// <summary>
/// The cards of the Blazor task board, kept in the memory of the site process. The board's pages
/// (the renderer process, ADR 0084) reach them only through <c>/api/tasks</c>, like a Blazor
/// WebAssembly client calls its server: they never share this instance.
/// </summary>
public sealed class TaskBoard
{
    private readonly Lock _gate = new();
    private readonly List<BoardTask> _tasks = [];
    private readonly TimeProvider _time;
    private int _nextId = 1;

    /// <summary>Creates the board with the cards every new site process starts with.</summary>
    /// <param name="time">The clock of the site.</param>
    public TaskBoard(TimeProvider time)
    {
        _time = time;
        Add(new BoardTask { Title = "Rotate the SSH keys of the build farm", Assignee = "neo", Column = BoardColumns.Doing });
        Add(new BoardTask { Title = "Patch the mail gateway", Assignee = "trinity", Column = BoardColumns.Todo });
        Add(new BoardTask { Title = "Write the onboarding checklist", Column = BoardColumns.Todo });
        Add(new BoardTask { Title = "Move the wiki to intranet.hack", Assignee = "morpheus", Column = BoardColumns.Done });
    }

    /// <summary>Returns the number of cards.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _tasks.Count;
            }
        }
    }

    /// <summary>Returns every card in creation order.</summary>
    public IReadOnlyList<BoardTask> All()
    {
        lock (_gate)
        {
            return [.. _tasks.Select(Copy)];
        }
    }

    /// <summary>Returns a copy of the card <paramref name="id"/>, or <see langword="null"/>.</summary>
    /// <param name="id">The identifier.</param>
    public BoardTask? Find(int id)
    {
        lock (_gate)
        {
            return _tasks.Find(task => task.Id == id) is BoardTask task ? Copy(task) : null;
        }
    }

    /// <summary>Stores a new card and returns it with its identifier and date.</summary>
    /// <param name="task">The card; its <see cref="BoardTask.Id"/> is ignored.</param>
    public BoardTask Add(BoardTask task)
    {
        ArgumentNullException.ThrowIfNull(task);
        lock (_gate)
        {
            BoardTask stored = Copy(task);
            stored.Id = _nextId++;
            stored.CreatedAt = _time.GetUtcNow();
            _tasks.Add(stored);
            return Copy(stored);
        }
    }

    /// <summary>Replaces the card of the same identifier (its creation date is kept); <see langword="null"/> when there is none.</summary>
    /// <param name="task">The new content.</param>
    public BoardTask? Update(BoardTask task)
    {
        ArgumentNullException.ThrowIfNull(task);
        lock (_gate)
        {
            int index = _tasks.FindIndex(existing => existing.Id == task.Id);
            if (index < 0)
            {
                return null;
            }

            BoardTask stored = Copy(task);
            stored.CreatedAt = _tasks[index].CreatedAt;
            _tasks[index] = stored;
            return Copy(stored);
        }
    }

    /// <summary>Removes the card <paramref name="id"/>; returns whether it existed.</summary>
    /// <param name="id">The identifier.</param>
    public bool Remove(int id)
    {
        lock (_gate)
        {
            return _tasks.RemoveAll(task => task.Id == id) > 0;
        }
    }

    private static BoardTask Copy(BoardTask task) => new()
    {
        Id = task.Id,
        Title = task.Title,
        Assignee = task.Assignee,
        Column = task.Column,
        CreatedAt = task.CreatedAt,
    };
}
