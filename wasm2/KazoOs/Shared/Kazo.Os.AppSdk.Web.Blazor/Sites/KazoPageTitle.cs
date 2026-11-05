using Kazo.Os.AppSdk.Blazor;
using Microsoft.AspNetCore.Components;

namespace Kazo.Os.AppSdk.Web.Blazor.Sites;

/// <summary>
/// Sets the browser tab's title from a page of a Blazor site (Blazor's <c>PageTitle</c>, ADR 0084,
/// section 5): <c>&lt;KazoPageTitle Title="Notes" /&gt;</c>. Renders nothing.
/// </summary>
public sealed class KazoPageTitle : KazoComponentBase
{
    /// <summary>Gets the site services of the tab.</summary>
    [CascadingParameter]
    private KazoWebSite? Site { get; set; }

    /// <summary>Gets or sets the title.</summary>
    [Parameter, EditorRequired]
    public string Title { get; set; } = string.Empty;

    /// <inheritdoc />
    protected override void OnParametersSet() => Site?.SetTitle(Title);
}
