namespace Kazo.Os.Samples.BlazorSite;

/// <summary>
/// State of the site that outlives its pages: the layout owns one instance and cascades it, so a
/// page recreated after a navigation within the site (back, a link) finds its values again. The
/// state lives as long as the renderer process: a reload starts a new one, and the values are gone.
/// </summary>
public sealed class SiteState
{
    /// <summary>Gets or sets how many times the home page's button was clicked.</summary>
    public int Clicks { get; set; }
}
