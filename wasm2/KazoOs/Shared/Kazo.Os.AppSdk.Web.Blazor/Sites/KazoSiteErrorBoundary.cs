using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Web;

namespace Kazo.Os.AppSdk.Web.Blazor.Sites;

/// <summary>
/// Stops an exception of a site's component at the site (ADR 0084, section 4): the browser shows its
/// "Aw, Snap!" page and ends the renderer; the OS is never touched. Renders nothing once an error
/// happened: the browser replaces the view.
/// </summary>
public sealed class KazoSiteErrorBoundary : ErrorBoundaryBase
{
    /// <summary>Gets or sets what to do with the exception (outside the component tree).</summary>
    [Parameter]
    public Func<Exception, Task>? OnError { get; set; }

    /// <inheritdoc />
    protected override Task OnErrorAsync(Exception exception) =>
        OnError?.Invoke(exception) ?? Task.CompletedTask;

    /// <inheritdoc />
    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        if (CurrentException is null)
        {
            builder.AddContent(0, ChildContent);
        }
    }
}
