using System.Text;
using System.Text.RegularExpressions;
using Kazo.Os.AppSdk.Web.Generated;
using Kazo.Os.AppSdk.Web.Hosting;
using Kazo.Os.AppSdk.Web.Http;

namespace Kazo.Os.Samples.Intranet.Tests;

/// <summary>
/// A browser-like client of the Intranet site run in process (<see cref="KazoWebHost.CreateApplication"/>,
/// the site's generated route and view tables): it keeps the cookies the site sets, like the tab of the
/// Kazo OS browser, and finds the anti-forgery field of a form.
/// </summary>
internal sealed partial class IntranetClient
{
    private readonly Dictionary<string, string> _cookies = new(StringComparer.Ordinal);

    public IntranetClient()
    {
        Application = KazoWebHost.CreateApplication(new IntranetSite(), KazoWebGeneratedRoutes.Table, views: KazoWebGeneratedRoutes.Views);
    }

    public KazoWebApplication Application { get; }

    public IReadOnlyDictionary<string, string> Cookies => _cookies;

    public Task<KazoWebResponseData> GetAsync(string path) => SendAsync(new KazoWebRequestData("GET", Url(path), CookieHeader(), null, ReadOnlyMemory<byte>.Empty));

    public Task<KazoWebResponseData> SendAsync(string method, string path, string contentType, string body) =>
        SendAsync(new KazoWebRequestData(method, Url(path), CookieHeader(), contentType, Encoding.UTF8.GetBytes(body)));

    public Task<KazoWebResponseData> SendJsonAsync(string method, string path, string json) => SendAsync(method, path, "application/json", json);

    public Task<KazoWebResponseData> DeleteAsync(string path) => SendAsync(new KazoWebRequestData("DELETE", Url(path), CookieHeader(), null, ReadOnlyMemory<byte>.Empty));

    /// <summary>Posts an HTML form, with the anti-forgery token of <paramref name="formPage"/> unless <paramref name="formPage"/> is null.</summary>
    public Task<KazoWebResponseData> PostFormAsync(string path, string? formPage, params (string Name, string Value)[] fields)
    {
        List<(string Name, string Value)> all = [.. fields];
        if (formPage is not null)
        {
            all.Add(("__RequestVerificationToken", TokenOf(formPage)));
        }

        string body = string.Join('&', all.Select(field => $"{Uri.EscapeDataString(field.Name)}={Uri.EscapeDataString(field.Value)}"));
        return SendAsync("POST", path, "application/x-www-form-urlencoded", body);
    }

    public static string TokenOf(string html)
    {
        Match match = TokenField().Match(html);
        Assert.True(match.Success, "The page has no anti-forgery field.");
        return System.Net.WebUtility.HtmlDecode(match.Groups[1].Value);
    }

    public static string? Location(KazoWebResponseData response) =>
        response.Headers.FirstOrDefault(header => string.Equals(header.Key, "Location", StringComparison.OrdinalIgnoreCase)).Value;

    public static string? Header(KazoWebResponseData response, string name) =>
        response.Headers.FirstOrDefault(header => string.Equals(header.Key, name, StringComparison.OrdinalIgnoreCase)).Value;

    private async Task<KazoWebResponseData> SendAsync(KazoWebRequestData request)
    {
        KazoWebResponseData response = await Application.ProcessAsync(request);
        foreach (KeyValuePair<string, string> header in response.Headers.Where(header => string.Equals(header.Key, "Set-Cookie", StringComparison.OrdinalIgnoreCase)))
        {
            string pair = header.Value.Split(';')[0];
            int equals = pair.IndexOf('=');
            _cookies[pair[..equals]] = pair[(equals + 1)..];
        }

        return response;
    }

    private KeyValuePair<string, string>[] CookieHeader() =>
        _cookies.Count == 0 ? [] : [new("Cookie", string.Join("; ", _cookies.Select(cookie => $"{cookie.Key}={cookie.Value}")))];

    private static Uri Url(string path) => new(new Uri(IntranetSite.Address), path);

    [GeneratedRegex("name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"")]
    private static partial Regex TokenField();
}
