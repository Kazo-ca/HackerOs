using Kazo.Os.App.Abstractions;

namespace Kazo.Os.App.Abstractions.Tests;

public sealed class WebAppUrlsTests
{
    [Theory]
    [InlineData("http://intranet.hack/", "http", "intranet.hack", 80, "/")]
    [InlineData("HTTPS://Bank.HACK/", "https", "bank.hack", 443, "/")]
    [InlineData("http://localhost:8080/api/", "http", "localhost", 8080, "/api/")]
    [InlineData("http://10.0.0.5:81/", "http", "10.0.0.5", 81, "/")]
    public void TryParse_reads_scheme_host_port_and_path(string value, string scheme, string host, int port, string path)
    {
        Assert.True(WebAppUrls.TryParse(value, allowWildcardHost: false, out WebBinding binding, out string? error), error);
        Assert.Equal(new WebBinding(scheme, host, port, path), binding);
    }

    [Theory]
    [InlineData("ftp://intranet.hack/")]
    [InlineData("http://intranet.hack")]
    [InlineData("http://intranet.hack/a")]
    [InlineData("http://intranet.hack//")]
    [InlineData("http://user@intranet.hack/")]
    [InlineData("http://intranet.hack:0/")]
    [InlineData("http://intranet.hack:70000/")]
    [InlineData("http://-bad.hack/")]
    [InlineData("http://under_score.hack/")]
    [InlineData(" http://intranet.hack/")]
    public void TryParse_rejects_malformed_urls(string value)
    {
        Assert.False(WebAppUrls.TryParse(value, allowWildcardHost: true, out _, out string? error));
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Fact]
    public void Wildcards_are_listener_prefixes_only()
    {
        Assert.False(WebAppUrls.TryParse("http://+:8080/", allowWildcardHost: false, out _, out _));
        Assert.True(WebAppUrls.TryParse("http://+:8080/", allowWildcardHost: true, out WebBinding strong, out _));
        Assert.True(WebAppUrls.TryParse("http://*:80/", allowWildcardHost: true, out WebBinding weak, out _));
        Assert.True(strong.IsWildcard);
        Assert.True(weak.IsWildcard);
        Assert.True(weak.IsPrivilegedPort);
        Assert.False(strong.IsPrivilegedPort);
    }

    [Theory]
    [InlineData("HTTP://Intranet.HACK:80/", "http://intranet.hack/")]
    [InlineData("https://bank.hack:443/", "https://bank.hack/")]
    [InlineData("http://bank.hack:443/", "http://bank.hack:443/")]
    [InlineData("not a url", "not a url")]
    public void Normalize_lowercases_and_drops_the_default_port(string value, string expected) =>
        Assert.Equal(expected, WebAppUrls.Normalize(value));

    [Theory]
    [InlineData("localhost", true)]
    [InlineData("api.localhost", true)]
    [InlineData("127.0.0.1", true)]
    [InlineData("127.8.0.3", true)]
    [InlineData("intranet.hack", false)]
    public void IsLoopbackName_matches_localhost_and_127_slash_8(string host, bool expected) =>
        Assert.Equal(expected, WebAppUrls.IsLoopbackName(host));

    [Theory]
    [InlineData("hack", true)]
    [InlineData("intranet.hack", true)]
    [InlineData("INTRANET.HACK", true)]
    [InlineData("hack.example.com", false)]
    [InlineData("kazoos.net", false)]
    public void IsInternalDomain_matches_the_hack_tld(string host, bool expected) =>
        Assert.Equal(expected, WebAppUrls.IsInternalDomain(host));
}
