using Microsoft.AspNetCore.Components;

namespace HackerOs.AppSdk.Web.Blazor.Sites;

/// <summary>
/// The <see cref="NavigationManager"/> of a Blazor site (ADR 0084, section 5): the address of the
/// browser tab, not of Kazo OS. <see cref="NavigationManager.NavigateTo(string, bool)"/> within the site
/// changes the tab's address without a request (like <c>history.pushState</c>); outside it, or with
/// <c>forceLoad</c>, the browser navigates. A site gets it from the cascading <see cref="HackWebSite"/>
/// (<c>Site.Navigation</c>) and must not inject Blazor's own <see cref="NavigationManager"/>, which is
/// the OS's.
/// </summary>
public sealed class HackSiteNavigationManager : NavigationManager
{
    /// <summary>Initializes the manager on the site's origin and the tab's current address.</summary>
    /// <param name="baseUri">The site's origin with a trailing slash (<c>http://intranet.hack/</c>).</param>
    /// <param name="uri">The current absolute address.</param>
    public HackSiteNavigationManager(string baseUri, string uri)
    {
        Initialize(baseUri, uri);
    }

    /// <summary>Raised by <see cref="NavigationManager.NavigateTo(string, bool)"/> and its overloads: the view decides whether the tab changes address or document.</summary>
    public event Action<string, NavigationOptions>? NavigationRequested;

    /// <inheritdoc />
    protected override void NavigateToCore(string uri, NavigationOptions options)
    {
        ArgumentNullException.ThrowIfNull(uri);
        NavigationRequested?.Invoke(ToAbsoluteUri(uri).AbsoluteUri, options);
    }

    /// <summary>Records a new address of the tab and tells the site (<see cref="NavigationManager.LocationChanged"/>).</summary>
    /// <param name="uri">The new absolute address, within the site.</param>
    /// <param name="isInterceptedLink">Whether a link click caused it.</param>
    public void SetLocation(string uri, bool isInterceptedLink)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (string.Equals(Uri, uri, StringComparison.Ordinal))
        {
            return;
        }

        Uri = uri;
        NotifyLocationChanged(isInterceptedLink);
    }

    /// <summary>Tells whether <paramref name="uri"/> is within the site (same origin as <see cref="NavigationManager.BaseUri"/>).</summary>
    /// <param name="uri">An absolute URI.</param>
    public bool IsWithinSite(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        Uri site = new(BaseUri);
        return uri.IsAbsoluteUri
            && string.Equals(uri.Scheme, site.Scheme, StringComparison.OrdinalIgnoreCase)
            && string.Equals(uri.Host, site.Host, StringComparison.OrdinalIgnoreCase)
            && uri.Port == site.Port;
    }
}
