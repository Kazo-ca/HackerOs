using System.Text;
using Kazo.Os.Apps.Browser;
using Xunit;

namespace Kazo.Os.Apps.Browser.Tests;

/// <summary>ADR 0074 section 1: the browser shows a non-page body according to its Content-Type.</summary>
public sealed class ResponseBodyPresenterTests
{
    [Fact]
    public void Json_is_pretty_printed()
    {
        ResponseBodyView view = ResponseBodyPresenter.Present("application/json; charset=utf-8", Encoding.UTF8.GetBytes("{\"a\":[1,2]}"));

        Assert.Equal(ResponseBodyKind.Json, view.Kind);
        Assert.Contains("\n", view.Text);
        Assert.Contains("\"a\": [", view.Text);
    }

    [Fact]
    public void Invalid_json_is_shown_unchanged()
    {
        ResponseBodyView view = ResponseBodyPresenter.Present("application/json", Encoding.UTF8.GetBytes("{oops"));

        Assert.Equal("{oops", view.Text);
    }

    [Fact]
    public void Html_shows_its_source_and_text_keeps_its_charset()
    {
        ResponseBodyView html = ResponseBodyPresenter.Present("text/html", Encoding.UTF8.GetBytes("<h1>Hi</h1>"));
        ResponseBodyView latin1 = ResponseBodyPresenter.Present("text/plain; charset=iso-8859-1", Encoding.Latin1.GetBytes("café"));

        Assert.Equal(ResponseBodyKind.Html, html.Kind);
        Assert.Equal("<h1>Hi</h1>", html.Text);
        Assert.Equal(ResponseBodyKind.Text, latin1.Kind);
        Assert.Equal("café", latin1.Text);
    }

    [Fact]
    public void Images_become_data_urls_and_other_bytes_a_download()
    {
        ResponseBodyView image = ResponseBodyPresenter.Present("image/png", [1, 2, 3]);
        ResponseBodyView binary = ResponseBodyPresenter.Present("application/zip", [1, 2, 3]);

        Assert.Equal(ResponseBodyKind.Image, image.Kind);
        Assert.Equal("data:image/png;base64,AQID", image.DataUrl);
        Assert.Equal(ResponseBodyKind.Binary, binary.Kind);
        Assert.Equal(3, binary.Length);
    }

    [Fact]
    public void Untyped_bodies_are_sniffed_and_empty_ones_reported()
    {
        Assert.Equal(ResponseBodyKind.Text, ResponseBodyPresenter.Present(null, Encoding.UTF8.GetBytes("plain words")).Kind);
        Assert.Equal(ResponseBodyKind.Binary, ResponseBodyPresenter.Present(null, [0, 159, 146, 150]).Kind);
        Assert.Equal(ResponseBodyKind.Empty, ResponseBodyPresenter.Present("text/plain", []).Kind);
    }
}
