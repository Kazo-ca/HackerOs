using Kazo.Os.AppSdk.Net;

namespace Kazo.Os.AppSdk.Web.Blazor.Sites;

/// <summary>
/// What a Blazor site's components get from the browser tab that draws them (ADR 0084, sections
/// 4 to 6), as a cascading parameter: the tab's address and navigation, an HTTP client on the site's
/// own address with the tab's cookies, and the tab's title.
/// </summary>
/// <example>
/// <code>
/// [CascadingParameter] HackWebSite Site { get; set; } = default!;
/// ...
/// Note[] notes = await Site.Http.GetFromJsonAsync("api/notes", NotesJson.Default.NoteArray);
/// Site.Navigation.NavigateTo("/notes/1");
/// </code>
/// </example>
public sealed class HackWebSite
{
    private string _title;

    /// <summary>Initializes the site services of a tab.</summary>
    /// <param name="navigation">The tab's navigation manager.</param>
    /// <param name="http">The site's HTTP client.</param>
    /// <param name="appId">The web app's id.</param>
    /// <param name="name">The site's display name, its initial title.</param>
    public HackWebSite(HackSiteNavigationManager navigation, HackHttpClient http, string appId, string name)
    {
        Navigation = navigation ?? throw new ArgumentNullException(nameof(navigation));
        Http = http ?? throw new ArgumentNullException(nameof(http));
        AppId = appId ?? throw new ArgumentNullException(nameof(appId));
        Name = name ?? string.Empty;
        _title = Name;
    }

    /// <summary>Gets the web app's id (<c>com.example.intranet</c>).</summary>
    public string AppId { get; }

    /// <summary>Gets the site's display name.</summary>
    public string Name { get; }

    /// <summary>Gets the tab's navigation: current address, <c>NavigateTo</c>, <c>LocationChanged</c>.</summary>
    public HackSiteNavigationManager Navigation { get; }

    /// <summary>
    /// Gets the site's HTTP client: <see cref="HackHttpClient.BaseAddress"/> is the site's address and
    /// the cookies are the tab's, so <c>GetAsync("api/notes")</c> reaches the site's own routes with
    /// the session its pages opened. Requests run as the site's renderer process.
    /// </summary>
    public HackHttpClient Http { get; }

    /// <summary>Gets the tab's title (<see cref="HackPageTitle"/> sets it).</summary>
    public string Title => _title;

    /// <summary>Raised when the title changes.</summary>
    public event Action<string>? TitleChanged;

    /// <summary>Sets the tab's title.</summary>
    /// <param name="title">The new title; empty falls back to the site's name.</param>
    public void SetTitle(string? title)
    {
        string value = string.IsNullOrWhiteSpace(title) ? Name : title.Trim();
        if (string.Equals(_title, value, StringComparison.Ordinal))
        {
            return;
        }

        _title = value;
        TitleChanged?.Invoke(value);
    }
}
