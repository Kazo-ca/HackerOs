using System.Text;
using System.Text.Json;
using Kazo.Os.AppSdk.Net;
using Kazo.Os.Platform.WorkerHost.Protocol;
using Xunit;

namespace Kazo.Os.Platform.WorkerHost.Tests;

/// <summary>
/// ADR 0074 section 1: an HTTP request body and a typed response cross the worker boundary
/// (ADR 0070) unchanged, so a command in a worker sends and receives the same bytes as on the main
/// thread.
/// </summary>
public sealed class WireCodecHttpTests
{
    [Fact]
    public void Request_body_and_content_type_round_trip()
    {
        HackHttpRequest request = new(
            "PUT",
            new Uri("https://api.test/items/7"),
            [new("User-Agent", "curl/7.88.1")],
            new HackByteArrayContent([0, 1, 2, 255], "application/x-custom"),
            [new HackCookie("api.test", "sid", "42")]);

        HackHttpRequest copy = RoundTrip(writer => WireCodec.WriteHttpRequest(writer, request), WireCodec.ReadHttpRequest);

        Assert.Equal("PUT", copy.Method);
        Assert.Equal("application/x-custom", copy.Content?.ContentType);
        Assert.Equal(new byte[] { 0, 1, 2, 255 }, copy.Content?.ReadAsByteArray());
        Assert.Equal(request.Cookies, copy.Cookies);
    }

    [Fact]
    public void Request_without_body_stays_without_body()
    {
        HackHttpRequest request = new("GET", new Uri("https://api.test/"), [], null, []);

        HackHttpRequest copy = RoundTrip(writer => WireCodec.WriteHttpRequest(writer, request), WireCodec.ReadHttpRequest);

        Assert.Null(copy.Content);
    }

    [Fact]
    public void Response_keeps_its_type_and_bytes()
    {
        HackHttpResponse response = new(
            200, "OK", new Uri("https://api.test/"),
            [new("Content-Type", "application/json; charset=utf-8")],
            Encoding.UTF8.GetBytes("{\"ok\":true}"), null, 0, []);

        HackHttpResponse copy = RoundTrip(writer => WireCodec.WriteHttpResponse(writer, response), WireCodec.ReadHttpResponse);

        Assert.Equal("application/json", copy.MediaType);
        Assert.Equal("{\"ok\":true}", copy.Content);
    }

    private static T RoundTrip<T>(Action<Utf8JsonWriter> write, Func<JsonElement, T> read)
    {
        using MemoryStream buffer = new();
        using (Utf8JsonWriter writer = new(buffer))
        {
            writer.WriteStartObject();
            write(writer);
            writer.WriteEndObject();
        }

        using JsonDocument document = JsonDocument.Parse(buffer.ToArray());
        return read(document.RootElement);
    }
}
