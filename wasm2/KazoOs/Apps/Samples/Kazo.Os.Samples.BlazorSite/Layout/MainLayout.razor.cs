using Microsoft.AspNetCore.Components.Routing;

namespace Kazo.Os.Samples.BlazorSite.Layout;

/// <summary>The site's layout (see <c>MainLayout.razor</c>).</summary>
public sealed partial class MainLayout
{
    // One per renderer process: pages come and go, the layout stays.
    private readonly SiteState _state = new();
}
