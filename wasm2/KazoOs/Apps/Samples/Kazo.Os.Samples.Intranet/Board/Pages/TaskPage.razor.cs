using Kazo.Os.AppSdk.Net;
using Kazo.Os.AppSdk.Web.Blazor.Sites;
using Kazo.Os.Samples.Intranet.Models;
using Microsoft.AspNetCore.Components;

namespace Kazo.Os.Samples.Intranet.Board.Pages;

/// <summary>The page of one card (see <c>TaskPage.razor</c>).</summary>
public sealed partial class TaskPage
{
    private BoardTask? _task;
    private int? _loadedFor;
    private string? _error;
    private bool _busy;
    private bool _saved;

    /// <summary>Gets or sets the card identifier from the address.</summary>
    [Parameter]
    public int Id { get; set; }

    [CascadingParameter]
    private KazoWebSite? Site { get; set; }

    /// <inheritdoc />
    protected override async Task OnParametersSetAsync()
    {
        if (Site is null || _loadedFor == Id)
        {
            return;
        }

        _loadedFor = Id;
        _task = null;
        _error = null;
        _saved = false;
        KazoHttpResponse response = await Site.Http.GetAsync($"api/tasks/{Id}");
        if (response.StatusCode == 404)
        {
            _error = $"There is no card {Id}.";
            return;
        }

        try
        {
            _task = response.EnsureSuccessStatusCode().ReadFromJson(IntranetJsonContext.Default.BoardTask);
        }
        catch (HttpRequestException exception)
        {
            _error = exception.Message;
        }
    }

    private async Task SaveAsync()
    {
        if (Site is null || _task is null)
        {
            return;
        }

        _busy = true;
        _saved = false;
        _error = null;
        try
        {
            KazoHttpResponse response = await Site.Http.PutAsync($"api/tasks/{_task.Id}", KazoJsonContent.Create(_task, IntranetJsonContext.Default.BoardTask));
            if (response.StatusCode == 400)
            {
                _error = "The server refused the card: the title needs 3 to 80 characters.";
                return;
            }

            _task = response.EnsureSuccessStatusCode().ReadFromJson(IntranetJsonContext.Default.BoardTask);
            _saved = true;
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
}
