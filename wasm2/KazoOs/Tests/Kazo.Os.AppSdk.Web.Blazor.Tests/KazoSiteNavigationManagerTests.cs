using Kazo.Os.AppSdk.Web.Blazor.Sites;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;

namespace Kazo.Os.AppSdk.Web.Blazor.Tests;

/// <summary>The tab's navigation manager (ADR 0084 §5): NavigateTo asks the view, SetLocation tells the site.</summary>
public sealed class KazoSiteNavigationManagerTests
{
    private const string Site = "http://intranet.hack/";

    [Fact]
    public void NavigateTo_asks_the_view_with_an_absolute_address()
    {
        KazoSiteNavigationManager navigation = new(Site, Site + "notes");
        List<(string Uri, NavigationOptions Options)> requested = [];
        navigation.NavigationRequested += (uri, options) => requested.Add((uri, options));

        navigation.NavigateTo("notes/5");
        navigation.NavigateTo("/about", forceLoad: true);
        navigation.NavigateTo("https://elsewhere.hack/", replace: true);

        Assert.Equal(Site + "notes/5", requested[0].Uri);
        Assert.False(requested[0].Options.ForceLoad);
        Assert.Equal(Site + "about", requested[1].Uri);
        Assert.True(requested[1].Options.ForceLoad);
        Assert.Equal("https://elsewhere.hack/", requested[2].Uri);
        Assert.True(requested[2].Options.ReplaceHistoryEntry);
        // The address only changes once the view says so.
        Assert.Equal(Site + "notes", navigation.Uri);
    }

    [Fact]
    public void SetLocation_changes_the_address_and_raises_LocationChanged_once()
    {
        KazoSiteNavigationManager navigation = new(Site, Site);
        List<LocationChangedEventArgs> changes = [];
        navigation.LocationChanged += (_, e) => changes.Add(e);

        navigation.SetLocation(Site + "notes/5", isInterceptedLink: true);
        navigation.SetLocation(Site + "notes/5", isInterceptedLink: false);

        LocationChangedEventArgs change = Assert.Single(changes);
        Assert.Equal(Site + "notes/5", change.Location);
        Assert.True(change.IsNavigationIntercepted);
        Assert.Equal("notes/5", navigation.ToBaseRelativePath(navigation.Uri));
    }

    [Theory]
    [InlineData("http://intranet.hack/notes", true)]
    [InlineData("http://INTRANET.hack/", true)]
    [InlineData("http://intranet.hack:8080/notes", false)]
    [InlineData("https://intranet.hack/notes", false)]
    [InlineData("http://wiki.hack/", false)]
    public void IsWithinSite_compares_the_origin(string uri, bool expected)
    {
        KazoSiteNavigationManager navigation = new(Site, Site);

        Assert.Equal(expected, navigation.IsWithinSite(new Uri(uri)));
    }
}
