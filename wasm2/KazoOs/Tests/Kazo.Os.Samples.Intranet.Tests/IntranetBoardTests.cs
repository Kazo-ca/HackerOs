using System.Text.Json;
using Kazo.Os.AppSdk.Web.Http;
using Kazo.Os.AppSdk.Web.Hosting;
using Kazo.Os.Samples.Intranet.Board;
using Kazo.Os.Samples.Intranet.Board.Pages;
using Microsoft.AspNetCore.Components;

namespace Kazo.Os.Samples.Intranet.Tests;

/// <summary>
/// The Blazor part of the Intranet sample (ADR 0084): the board's addresses answer the host page, the MVC
/// routes keep theirs, and the boot manifest names the site's root component.
/// </summary>
public sealed class IntranetBoardTests
{
    [Theory]
    [InlineData("/board")]
    [InlineData("/board/tasks/2")]
    public async Task Board_addresses_answer_the_blazor_host_page(string path)
    {
        KazoWebResponseData page = await new IntranetClient().GetAsync(path);

        Assert.Equal(200, page.StatusCode);
        Assert.True(KazoBlazorSiteEndpoints.IsHostPage(page.ReadAsString()));
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/Notes")]
    [InlineData("/People")]
    public async Task Mvc_pages_come_before_the_blazor_fallback(string path)
    {
        KazoWebResponseData page = await new IntranetClient().GetAsync(path);

        Assert.Equal(200, page.StatusCode);
        Assert.False(KazoBlazorSiteEndpoints.IsHostPage(page.ReadAsString()));
    }

    [Fact]
    public async Task The_boot_manifest_names_the_board_root()
    {
        KazoWebResponseData boot = await new IntranetClient().GetAsync("/_framework/blazor.boot.json");

        Assert.Equal(200, boot.StatusCode);
        using JsonDocument json = JsonDocument.Parse(boot.ReadAsString());
        Assert.Equal("org.hackeros.samples.intranet", json.RootElement.GetProperty("appId").GetString());
        Assert.Equal(typeof(BoardApp).FullName, json.RootElement.GetProperty("rootComponent").GetString());
    }

    [Fact]
    public void Board_pages_are_routed_under_board_only()
    {
        // The board's router only knows these pages: every other address of the site is an MVC page.
        Assert.Equal("/board", Route<BoardPage>());
        Assert.Equal("/board/tasks/{Id:int}", Route<TaskPage>());
        Assert.Equal(
            [typeof(BoardPage), typeof(TaskPage)],
            typeof(BoardApp).Assembly.GetTypes().Where(type => type.GetCustomAttributes(typeof(RouteAttribute), false).Length > 0).OrderBy(type => type.Name));
    }

    private static string Route<T>() =>
        ((RouteAttribute)Assert.Single(typeof(T).GetCustomAttributes(typeof(RouteAttribute), false))).Template;
}
