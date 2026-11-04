using System.Text;
using System.Text.RegularExpressions;
using HackerOs.AppSdk.Web.Generated;
using HackerOs.AppSdk.Web.Hosting;
using HackerOs.AppSdk.Web.Http;

namespace HackerOs.Samples.Intranet.Tests;

/// <summary>
/// A browser-like client of the Intranet site run in process (<see cref="HackWebHost.CreateApplication"/>,
/// the site's generated route and view tables): it keeps the cookies the site sets, like the tab of the
/// Kazo OS browser, and finds the anti-forgery field of a form.
/// </summary>
internal sealed partial class IntranetClient
{
    private readonly Dictionary<string, string> _cookies = new(StringComparer.Ordinal);

    public IntranetClient()
    {
        Application = HackWebHost.CreateApplication(new IntranetSite(), HackWebGeneratedRoutes.Table, views: HackWebGeneratedRoutes.Views);
    }

    public HackWebApplication Application { get; }

    public IReadOnlyDictionary<string, string> Cookies => _cookies;

    public Task<HackWebResponseData> GetAsync(string path) => SendAsync(new HackWebRequestData("GET", Url(path), CookieHeader(), null, ReadOnlyMemory<byte>.Empty));

    public Task<HackWebResponseData> SendAsync(string method, string path, string contentType, string body) =>
        SendAsync(new HackWebRequestData(method, Url(path), CookieHeader(), contentType, Encoding.UTF8.GetBytes(body)));

    public Task<HackWebResponseData> SendJsonAsync(string method, string path, string json) => SendAsync(method, path, "application/json", json);

    public Task<HackWebResponseData> DeleteAsync(string path) => SendAsync(new HackWebRequestData("DELETE", Url(path), CookieHeader(), null, ReadOnlyMemory<byte>.Empty));

    /// <summary>Posts an HTML form, with the anti-forgery token of <paramref name="formPage"/> unless <paramref name="formPage"/> is null.</summary>
    public Task<HackWebResponseData> PostFormAsync(string path, string? formPage, params (string Name, string Value)[] fields)
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

    public static string? Location(HackWebResponseData response) =>
        response.Headers.FirstOrDefault(header => string.Equals(header.Key, "Location", StringComparison.OrdinalIgnoreCase)).Value;

    public static string? Header(HackWebResponseData response, string name) =>
        response.Headers.FirstOrDefault(header => string.Equals(header.Key, name, StringComparison.OrdinalIgnoreCase)).Value;

    private async Task<HackWebResponseData> SendAsync(HackWebRequestData request)
    {
        HackWebResponseData response = await Application.ProcessAsync(request);
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
