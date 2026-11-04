using Kazo.Os.AppSdk.Blazor;
using Microsoft.AspNetCore.Components;

namespace Kazo.Os.AppSdk.Web.Blazor.Sites;

/// <summary>
/// Base class of a Blazor site's layout (Blazor's <c>LayoutComponentBase</c>, ADR 0084, section 5):
/// <see cref="HackSiteRouter"/> renders the page in <see cref="Body"/>. It derives from
/// <see cref="HackComponentBase"/>, so the layout's event handlers run as the site's renderer process.
/// </summary>
public abstract class HackSiteLayoutBase : HackComponentBase
{
    /// <summary>Gets or sets the page rendered inside the layout.</summary>
    [Parameter]
    public RenderFragment? Body { get; set; }
}
