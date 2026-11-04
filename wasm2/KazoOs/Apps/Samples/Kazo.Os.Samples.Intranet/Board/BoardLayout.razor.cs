using Kazo.Os.AppSdk.Web.Blazor.Sites;
using Kazo.Os.Samples.Intranet.Models;
using Microsoft.AspNetCore.Components;

namespace Kazo.Os.Samples.Intranet.Board;

/// <summary>The board's layout (see <c>BoardLayout.razor</c>): asks the server who the visitor is, once per renderer process.</summary>
public sealed partial class BoardLayout
{
    // One per renderer process: the pages come and go, the layout and its session stay.
    private readonly BoardSession _session = new();

    [CascadingParameter]
    private KazoWebSite? Site { get; set; }

    private string? Visitor => _session.DisplayName;

    /// <inheritdoc />
    protected override async Task OnInitializedAsync()
    {
        if (Site is null)
        {
            return;
        }

        try
        {
            // The tab's cookies go with the request: the session of the MVC pages answers.
            Visitor? me = await Site.Http.GetFromJsonAsync("api/me", IntranetJsonContext.Default.Visitor);
            _session.DisplayName = me?.DisplayName;
        }
        catch (HttpRequestException)
        {
            _session.DisplayName = null;
        }
    }
}

/// <summary>What the board's pages share for the life of the renderer process.</summary>
public sealed class BoardSession
{
    /// <summary>Gets or sets the visitor's display name, from <c>/api/me</c>.</summary>
    public string? DisplayName { get; set; }
}
