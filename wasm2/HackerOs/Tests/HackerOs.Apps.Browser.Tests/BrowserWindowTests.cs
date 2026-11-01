using HackerOs.Apps.Browser;
using Xunit;

namespace HackerOs.Apps.Browser.Tests;

public sealed class BrowserWindowTests
{
    [Fact]
    public void ResolveSubmitUrl_RelativePostPath_ResolvesAgainstCurrentHost()
    {
        string result = BrowserWindow.ResolveSubmitUrl("https://hackersearch.net/results?q=x", "/login");

        Assert.Equal("https://hackersearch.net/login", result);
    }

    [Fact]
    public void ResolveSubmitUrl_AbsolutePostPath_IsUsedAsIs()
    {
        string result = BrowserWindow.ResolveSubmitUrl("https://hackersearch.net/", "https://other-host.example/submit");

        Assert.Equal("https://other-host.example/submit", result);
    }

    [Fact]
    public void ResolveSubmitUrl_NoCurrentPage_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, BrowserWindow.ResolveSubmitUrl(string.Empty, "/login"));
    }

    [Fact]
    public void ResolveSubmitUrl_MalformedCurrentUrl_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, BrowserWindow.ResolveSubmitUrl("not-a-valid-url", "/login"));
    }

    [Fact]
    public void ResolveSubmitUrl_PreservesHttpsScheme_NotOriginalPath()
    {
        string result = BrowserWindow.ResolveSubmitUrl("https://hackmail.com/inbox/message/42", "/submit");

        Assert.Equal("https://hackmail.com/submit", result);
    }
}

/// <summary>ADR 0220 §4: the mouse's Back and Forward buttons in the browser.</summary>
public sealed class BrowserHardwareNavigationTests
{
    [Fact]
    public void The_browser_window_answers_the_mouse_Back_and_Forward_buttons()
    {
        Assert.True(typeof(HackerOs.AppSdk.Blazor.IAppBackHandler).IsAssignableFrom(typeof(BrowserWindow)));
        Assert.True(typeof(HackerOs.AppSdk.Blazor.IAppForwardHandler).IsAssignableFrom(typeof(BrowserWindow)));
    }

    [Fact]
    public void The_page_bridge_relays_the_mouse_navigation_buttons_pressed_over_the_page()
    {
        string source = HackerOs.Apps.Browser.Bridge.PageBridgeScript.Source;

        Assert.Contains("event.button === 3 || event.button === 4", source, StringComparison.Ordinal);
        Assert.Contains("\"BrowserBack\" : \"BrowserForward\"", source, StringComparison.Ordinal);
    }
}
