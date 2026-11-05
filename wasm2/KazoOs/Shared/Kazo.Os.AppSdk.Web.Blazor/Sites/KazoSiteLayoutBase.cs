using Kazo.Os.AppSdk.Blazor;
using Microsoft.AspNetCore.Components;

namespace Kazo.Os.AppSdk.Web.Blazor.Sites;

/// <summary>
/// Base class of a Blazor site's layout (Blazor's <c>LayoutComponentBase</c>, ADR 0084, section 5):
/// <see cref="KazoSiteRouter"/> renders the page in <see cref="Body"/>. It derives from
/// <see cref="KazoComponentBase"/>, so the layout's event handlers run as the site's renderer process.
/// </summary>
public abstract class KazoSiteLayoutBase : KazoComponentBase
{
    /// <summary>Gets or sets the page rendered inside the layout.</summary>
    [Parameter]
    public RenderFragment? Body { get; set; }
}
