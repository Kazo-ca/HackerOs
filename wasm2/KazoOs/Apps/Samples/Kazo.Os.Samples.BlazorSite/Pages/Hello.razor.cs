using Kazo.Os.AppSdk.Web.Blazor.Sites;
using Microsoft.AspNetCore.Components;

namespace Kazo.Os.Samples.BlazorSite.Pages;

/// <summary>The greeting page: one API call per name (see <c>Hello.razor</c>).</summary>
public sealed partial class Hello
{
    private Greeting? _greeting;
    private string? _error;
    private string? _loadedFor;

    /// <summary>Gets or sets the name from the address.</summary>
    [Parameter]
    public string Name { get; set; } = string.Empty;

    [CascadingParameter]
    private KazoWebSite? Site { get; set; }

    /// <inheritdoc />
    protected override async Task OnParametersSetAsync()
    {
        if (Site is null || string.Equals(_loadedFor, Name, StringComparison.Ordinal))
        {
            return;
        }

        _loadedFor = Name;
        _greeting = null;
        _error = null;
        try
        {
            _greeting = await Site.Http.GetFromJsonAsync("api/greeting/" + Uri.EscapeDataString(Name), BlazorSampleJsonContext.Default.Greeting);
        }
        catch (HttpRequestException exception)
        {
            _error = exception.Message;
        }
    }
}
