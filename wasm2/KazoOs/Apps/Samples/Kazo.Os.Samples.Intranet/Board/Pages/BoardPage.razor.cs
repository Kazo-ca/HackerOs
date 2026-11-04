using Kazo.Os.AppSdk.Net;
using Kazo.Os.AppSdk.Web.Blazor.Sites;
using Kazo.Os.Samples.Intranet.Models;
using Microsoft.AspNetCore.Components;

namespace Kazo.Os.Samples.Intranet.Board.Pages;

/// <summary>The task board (see <c>BoardPage.razor</c>): a Blazor WebAssembly-style client of <c>/api/tasks</c>.</summary>
public sealed partial class BoardPage
{
    private List<BoardTask>? _tasks;
    private string _newTitle = string.Empty;
    private string? _error;
    private bool _busy;

    [CascadingParameter]
    private HackWebSite? Site { get; set; }

    [CascadingParameter]
    private BoardSession? Session { get; set; }

    /// <inheritdoc />
    protected override Task OnInitializedAsync() => RunAsync(LoadAsync);

    private async Task LoadAsync(HackWebSite site)
    {
        IReadOnlyList<BoardTask>? tasks = await site.Http.GetFromJsonAsync("api/tasks", IntranetJsonContext.Default.IReadOnlyListBoardTask);
        _tasks = [.. tasks ?? []];
    }

    private Task AddAsync() => RunAsync(async site =>
    {
        BoardTask draft = new() { Title = _newTitle.Trim(), Assignee = Session?.DisplayName ?? string.Empty, Column = BoardColumns.Todo };
        HackHttpResponse response = await site.Http.PostAsync("api/tasks", HackJsonContent.Create(draft, IntranetJsonContext.Default.BoardTask));
        _tasks?.Add(Read(response));
        _newTitle = string.Empty;
    });

    private Task MoveAsync(BoardTask task, int step) => RunAsync(async site =>
    {
        int index = BoardColumns.All.ToList().IndexOf(task.Column) + step;
        if (index < 0 || index >= BoardColumns.All.Count)
        {
            return;
        }

        BoardTask moved = new() { Id = task.Id, Title = task.Title, Assignee = task.Assignee, Column = BoardColumns.All[index], CreatedAt = task.CreatedAt };
        HackHttpResponse response = await site.Http.PutAsync($"api/tasks/{task.Id}", HackJsonContent.Create(moved, IntranetJsonContext.Default.BoardTask));
        Replace(Read(response));
    });

    private Task DeleteAsync(BoardTask task) => RunAsync(async site =>
    {
        HackHttpResponse response = await site.Http.DeleteAsync($"api/tasks/{task.Id}");
        response.EnsureSuccessStatusCode();
        _tasks?.RemoveAll(existing => existing.Id == task.Id);
    });

    // Every call goes through the simulated network: a failure (site stopped, 4xx) becomes a message, not a crash.
    private async Task RunAsync(Func<HackWebSite, Task> work)
    {
        if (Site is null)
        {
            return;
        }

        _busy = true;
        _error = null;
        try
        {
            await work(Site);
        }
        catch (HttpRequestException exception)
        {
            _error = exception.Message;
        }
        finally
        {
            _busy = false;
        }
    }

    private void Replace(BoardTask saved)
    {
        int index = _tasks?.FindIndex(existing => existing.Id == saved.Id) ?? -1;
        if (index >= 0)
        {
            _tasks![index] = saved;
        }
    }

    private static BoardTask Read(HackHttpResponse response) =>
        response.EnsureSuccessStatusCode().ReadFromJson(IntranetJsonContext.Default.BoardTask)
        ?? throw new HttpRequestException("The server answered an empty card.");
}
