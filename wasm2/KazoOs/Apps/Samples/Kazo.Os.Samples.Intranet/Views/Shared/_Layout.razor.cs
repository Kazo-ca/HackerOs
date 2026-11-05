using Kazo.Os.Samples.Intranet.Services;

namespace Kazo.Os.Samples.Intranet.Views.Shared;

/// <summary>The site's layout (see <c>_Layout.razor</c>).</summary>
public sealed partial class _Layout
{
    /// <summary>Gets the tab title: the view's title followed by the site name.</summary>
    private string PageTitle => ViewData.Title is { Length: > 0 } title ? $"{title} - Intranet" : "Intranet";

    /// <summary>Gets the visitor's name from the cookie session, or <see langword="null"/>.</summary>
    private string? DisplayName => IntranetSession.DisplayName(HttpContext);

    /// <summary>Marks the link of the controller that rendered the page.</summary>
    /// <param name="controller">The controller of the link.</param>
    private string NavClass(string controller) =>
        string.Equals(Request.RouteValues["controller"]?.ToString(), controller, StringComparison.OrdinalIgnoreCase) ? "active" : string.Empty;
}
