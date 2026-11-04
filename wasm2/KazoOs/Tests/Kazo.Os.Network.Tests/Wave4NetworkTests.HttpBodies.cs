using System.Collections.Immutable;
using System.Text;
using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk;
using Kazo.Os.AppSdk.Net;
using Kazo.Os.Commands.Curl;
using Kazo.Os.Platform.Core.Network;
using Kazo.Os.Platform.Core.Network.Websites;
using Kazo.Os.Simulation.Abstractions.Gateways;
using Kazo.Os.Simulation.Abstractions.Network;
using Xunit;

namespace Kazo.Os.Network.Tests;

/// <summary>
/// ADR 0074 section 1 (chantier <c>web/2-http-bodies</c>): real HTTP bodies through the simulated
/// network, the gateway, the SDK and <c>curl</c>: every method, <c>Content-Type</c> and bytes,
/// the browser's redirect rules, 405/OPTIONS, HEAD and charsets.
/// </summary>
public sealed partial class Wave4NetworkTests
{
    private const string EchoBase = "https://api.test";

    /// <summary>Test site that echoes request bodies and serves a few typed responses.</summary>
    private sealed class BodyEchoController : SimulatedWebsiteControllerBase
    {
        public static readonly SimulatedHost Host = new("192.168.1.250", "api.test", true, 5, []);

        public static readonly byte[] Png = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0xFF];

        public BodyEchoController()
        {
            foreach (string method in new[] { "POST", "PUT", "PATCH" })
            {
                Map(method, "/echo", Echo);
            }

            Get("/echo-get", _ => SimulatedHttpResponse.Text("GET"));
            Delete("/items/{id}", _ => SimulatedHttpResponse.NoContent());
            Get("/image.png", _ => SimulatedHttpResponse.File(Png, "image/png"));
            Get("/latin1", _ => SimulatedHttpResponse.Content(
                new SimulatedHttpBody("text/plain; charset=iso-8859-1", Encoding.Latin1.GetBytes("café"))));
            Get("/missing", _ => SimulatedHttpResponse.Text("nothing here", SimulatedHttpStatus.NotFound));
            Put("/moved-307", _ => Redirect(SimulatedHttpStatus.TemporaryRedirect, "/echo"));
            Put("/moved-303", _ => Redirect(SimulatedHttpStatus.SeeOther, "/echo-get"));
        }

        public override string PrimaryHostname => "api.test";

        public override string Theme => "hacker";

        public override string SiteName => "Body echo (tests)";

        private static SimulatedHttpResponse Echo(SimulatedHttpRequest request)
        {
            ImmutableDictionary<string, string> headers = ImmutableDictionary<string, string>.Empty
                .Add("X-Echo-Method", request.Method)
                .Add("X-Echo-Form", request.FormBody is { } form ? string.Join(';', form.OrderBy(f => f.Key, StringComparer.Ordinal).Select(f => $"{f.Key}={f.Value}")) : "-")
                .Add("X-Echo-Agent", request.Headers.GetValueOrDefault("user-agent", "-"));
            return request.Body is { } body
                ? SimulatedHttpResponse.Content(body, headers: headers)
                : SimulatedHttpResponse.NoContent() with { Headers = headers };
        }

        private static SimulatedHttpResponse Redirect(int status, string location) =>
            new(status, null, location, ImmutableDictionary<string, string>.Empty, ImmutableDictionary<string, string>.Empty);
    }

    [Fact]
    public async Task Put_with_json_reaches_the_site_with_its_type_and_bytes_and_comes_back_typed()
    {
        EnterApp(new GrantAllCapabilityChecker());
        using HackHttpClient client = new();

        HackHttpResponse response = await client.PutAsync($"{EchoBase}/echo", HackJsonContent.FromJson("{\"name\":\"neo\"}"));

        Assert.Equal(200, response.StatusCode);
        Assert.Equal("PUT", response.GetHeader("X-Echo-Method"));
        Assert.Equal("application/json; charset=utf-8", response.ContentType);
        Assert.Equal("application/json", response.MediaType);
        Assert.Equal("{\"name\":\"neo\"}", response.Content);
        Assert.Equal("14", response.GetHeader("Content-Length"));
        Assert.Null(response.Page);
    }

    [Fact]
    public async Task Form_content_is_parsed_for_the_site_whatever_the_method()
    {
        EnterApp(new GrantAllCapabilityChecker());
        using HackHttpClient client = new();

        HackHttpResponse response = await client.PatchAsync(
            $"{EchoBase}/echo", new HackFormUrlEncodedContent([new("user", "neo anderson"), new("role", "a&b")]));

        Assert.Equal("user=neo+anderson&role=a%26b", response.Content);
        Assert.Equal("role=a&b;user=neo anderson", response.GetHeader("X-Echo-Form"));
        Assert.Equal("PATCH", response.GetHeader("X-Echo-Method"));
    }

    [Fact]
    public async Task Delete_answered_204_has_no_body_and_no_content_type()
    {
        EnterApp(new GrantAllCapabilityChecker());
        using HackHttpClient client = new();

        HackHttpResponse response = await client.DeleteAsync($"{EchoBase}/items/42");

        Assert.Equal(204, response.StatusCode);
        Assert.Equal("No Content", response.ReasonPhrase);
        Assert.Empty(response.ContentBytes);
        Assert.Null(response.ContentType);
        Assert.Equal("0", response.GetHeader("Content-Length"));
    }

    [Fact]
    public async Task Redirect_307_keeps_method_and_body_while_303_continues_as_a_bodyless_get()
    {
        EnterApp(new GrantAllCapabilityChecker());
        using HackHttpClient client = new();

        HackHttpResponse kept = await client.PutAsync($"{EchoBase}/moved-307", new HackStringContent("payload"));
        HackHttpResponse switched = await client.PutAsync($"{EchoBase}/moved-303", new HackStringContent("payload"));

        Assert.Equal(1, kept.RedirectCount);
        Assert.Equal("PUT", kept.GetHeader("X-Echo-Method"));
        Assert.Equal("payload", kept.Content);
        Assert.Equal($"{EchoBase}/echo", kept.RequestUri.AbsoluteUri);
        Assert.Equal(1, switched.RedirectCount);
        Assert.Equal("GET", switched.Content);
    }

    [Theory]
    [InlineData(303, "PUT", true)]
    [InlineData(303, "HEAD", false)]
    [InlineData(302, "POST", true)]
    [InlineData(302, "PUT", false)]
    [InlineData(301, "POST", true)]
    [InlineData(307, "POST", false)]
    [InlineData(308, "POST", false)]
    public void Redirect_rules_follow_the_browsers(int status, string method, bool becomesGet) =>
        Assert.Equal(becomesGet, AppNetworkGateway.RedirectBecomesGet(status, method));

    [Fact]
    public async Task Known_path_with_unknown_method_answers_405_with_allow_and_options_lists_it()
    {
        EnterApp(new GrantAllCapabilityChecker());
        using HackHttpClient client = new();

        HackHttpResponse notAllowed = await client.DeleteAsync($"{EchoBase}/echo-get");
        HackHttpResponse options = await client.SendAsync(System.Net.Http.HttpMethod.Options, $"{EchoBase}/echo-get");
        HackHttpResponse unknown = await client.DeleteAsync($"{EchoBase}/nowhere");

        Assert.Equal(405, notAllowed.StatusCode);
        Assert.Equal("GET, HEAD, OPTIONS", notAllowed.GetHeader("Allow"));
        Assert.Equal(204, options.StatusCode);
        Assert.Equal("GET, HEAD, OPTIONS", options.GetHeader("Allow"));
        Assert.Equal(404, unknown.StatusCode);
    }

    [Fact]
    public async Task Head_is_served_by_the_get_route_announcing_the_length_without_the_body()
    {
        EnterApp(new GrantAllCapabilityChecker());
        using HackHttpClient client = new();

        HackHttpResponse head = await client.SendAsync(System.Net.Http.HttpMethod.Head, $"{EchoBase}/image.png");

        Assert.Equal(200, head.StatusCode);
        Assert.Empty(head.ContentBytes);
        Assert.Equal("image/png", head.ContentType);
        Assert.Equal(BodyEchoController.Png.Length.ToString(), head.GetHeader("Content-Length"));
    }

    [Fact]
    public async Task Binary_bodies_round_trip_and_text_is_decoded_with_its_charset()
    {
        EnterApp(new GrantAllCapabilityChecker());
        using HackHttpClient client = new();

        byte[] png = await client.GetByteArrayAsync($"{EchoBase}/image.png");
        HackHttpResponse latin1 = await client.GetAsync($"{EchoBase}/latin1");

        Assert.Equal(BodyEchoController.Png, png);
        Assert.Equal(4, latin1.ContentBytes.Length);
        Assert.Equal("café", latin1.Content);
    }

    [Fact]
    public async Task Historical_pages_still_travel_as_text_with_the_structured_page_attached()
    {
        EnterApp(new GrantAllCapabilityChecker());
        using HackHttpClient client = new();

        HackHttpResponse page = await client.GetAsync("https://hackersearch.net/");

        Assert.Equal("text/plain; charset=utf-8", page.ContentType);
        Assert.Equal("HackerSearch", page.Page?.Title);
        Assert.Contains("Title: HackerSearch", page.Content);
    }

    [Fact]
    public async Task HackerSearch_serves_a_json_api_robots_txt_and_redirects_its_form_post()
    {
        EnterApp(new GrantAllCapabilityChecker());
        using HackHttpClient client = new();

        HackHttpResponse api = await client.GetAsync("https://hackersearch.net/api/search?q=zero%20day");
        HackHttpResponse robots = await client.GetAsync("https://hackersearch.net/robots.txt");
        HackHttpResponse search = await client.PostAsync("https://hackersearch.net/search", [new("q", "exploit")]);

        Assert.Equal("application/json", api.MediaType);
        using (System.Text.Json.JsonDocument json = System.Text.Json.JsonDocument.Parse(api.Content))
        {
            Assert.Equal("zero day", json.RootElement.GetProperty("query").GetString());
            Assert.Equal(3, json.RootElement.GetProperty("featured").GetArrayLength());
        }

        Assert.Equal("text/plain", robots.MediaType);
        Assert.StartsWith("User-agent: *", robots.Content);
        Assert.Equal(1, search.RedirectCount);
        Assert.Equal("exploit - HackerSearch", search.Page?.Title);
    }

    [Fact]
    public async Task Put_needs_network_simulated_write_but_options_only_read()
    {
        EnterApp(new GrantAllCapabilityChecker(AppCapabilities.NetworkSimulatedWrite));
        using HackHttpClient client = new();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => client.PutAsync($"{EchoBase}/echo", new HackStringContent("x")));
        HackHttpResponse options = await client.SendAsync(System.Net.Http.HttpMethod.Options, $"{EchoBase}/echo");

        Assert.Equal(204, options.StatusCode);
    }

    [Fact]
    public void Simulated_body_parses_forms_media_types_and_charsets()
    {
        SimulatedHttpBody form = SimulatedHttpBody.FormUrlEncoded([new("a b", "1+1=2"), new("c", "")]);

        Assert.Equal("a+b=1%2B1%3D2&c=", form.ReadAsString());
        Assert.True(form.IsFormUrlEncoded);
        Assert.Equal(ImmutableDictionary<string, string>.Empty.Add("a b", "1+1=2").Add("c", ""), form.ReadAsForm());
        Assert.Equal("text/html", SimulatedMediaTypes.GetMediaType("Text/HTML ; charset=UTF-8"));
        Assert.Equal("UTF-8", SimulatedMediaTypes.GetCharset("text/html; charset=\"UTF-8\""));
        Assert.True(SimulatedMediaTypes.IsText("image/svg+xml"));
        Assert.True(SimulatedMediaTypes.IsJson("application/problem+json"));
        Assert.False(SimulatedMediaTypes.IsText("image/png"));
        Assert.Equal(Encoding.UTF8, SimulatedMediaTypes.GetEncoding("text/plain; charset=no-such-charset"));
    }

    // ── curl ────────────────────────────────────────────────────────────────

    private async Task<(int ExitCode, string Output, string Error)> RunCurlAsync(params string[] args)
    {
        UseServer(new NeverConnectedServerConnectionService(), new UnusedProxyClient());
        CurlCommand command = new(AppDefinitionMapper.ToManifest(new CurlApp()));
        using StringWriter output = new();
        using StringWriter error = new();
        int exitCode = await command.ExecuteAsync(CreateContext(args, output, error), CancellationToken.None);
        return (exitCode, output.ToString(), error.ToString());
    }

    [Fact]
    public async Task Curl_json_sends_put_with_json_type_and_prints_the_raw_body()
    {
        (int exitCode, string output, _) = await RunCurlAsync("-X", "PUT", "--json", "{\"id\":7}", $"{EchoBase}/echo");

        Assert.Equal(0, exitCode);
        Assert.Equal("{\"id\":7}\n", output);
    }

    [Fact]
    public async Task Curl_data_is_sent_raw_as_a_form_post_and_include_prints_headers_first()
    {
        (int exitCode, string output, _) = await RunCurlAsync("-i", "-d", "user=neo", "-d", "pass=x", $"{EchoBase}/echo");

        Assert.Equal(0, exitCode);
        Assert.StartsWith("HTTP/1.1 200 OK\n", output.ReplaceLineEndings("\n"));
        Assert.Contains("X-Echo-Method: POST", output);
        Assert.Contains("X-Echo-Form: pass=x;user=neo", output);
        Assert.Contains("Content-Type: application/x-www-form-urlencoded", output);
        Assert.EndsWith("\n\nuser=neo&pass=x\n", output.ReplaceLineEndings("\n"));
    }

    [Fact]
    public async Task Curl_header_overrides_the_content_type_and_data_urlencode_encodes_the_value()
    {
        (_, string typed, _) = await RunCurlAsync("-i", "-H", "Content-Type: text/csv", "--data-raw", "a,b", $"{EchoBase}/echo");
        (_, string encoded, _) = await RunCurlAsync("--data-urlencode", "q=two words&more", $"{EchoBase}/echo");

        Assert.Contains("Content-Type: text/csv", typed);
        Assert.Equal("q=two+words%26more\n", encoded);
    }

    [Fact]
    public async Task Curl_prints_an_error_body_without_fail_and_refuses_binary_on_the_terminal()
    {
        (int missingExit, string missing, _) = await RunCurlAsync($"{EchoBase}/missing");
        (int failExit, string failed, _) = await RunCurlAsync("-f", $"{EchoBase}/missing");
        (int binaryExit, string binary, string warning) = await RunCurlAsync($"{EchoBase}/image.png");

        Assert.Equal(0, missingExit);
        Assert.Equal("nothing here\n", missing);
        Assert.Equal(22, failExit);
        Assert.Empty(failed);
        Assert.Equal(23, binaryExit);
        Assert.Empty(binary);
        Assert.Contains("Binary output can mess up your terminal", warning);
    }
}
