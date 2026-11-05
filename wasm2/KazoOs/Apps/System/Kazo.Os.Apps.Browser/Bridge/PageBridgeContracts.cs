using System.Text.Json.Serialization;

namespace Kazo.Os.Apps.Browser.Bridge;

/// <summary>A <c>&lt;meta http-equiv="refresh"&gt;</c> the bridge carries out (the frame cannot navigate itself).</summary>
/// <param name="Seconds">Delay before the navigation.</param>
/// <param name="Url">Target URL, or <see langword="null"/> to reload the page.</param>
public sealed record RefreshDirective(double Seconds, string? Url);

/// <summary>
/// State handed to the page bridge when a document is written in the frame (ADR 0075 §3): what
/// the page must find already there, because the bridge answers some APIs synchronously.
/// </summary>
/// <param name="Token">Random value tying the frame's messages to this document.</param>
/// <param name="Url">The page's address, returned by <c>document.URL</c>.</param>
/// <param name="Referrer">The referring page, returned by <c>document.referrer</c>.</param>
/// <param name="Cookies">Value of <c>document.cookie</c> at load.</param>
/// <param name="LocalStorage">Content of the site's <c>localStorage</c>.</param>
/// <param name="SessionStorage">Content of the tab's <c>sessionStorage</c> for the site.</param>
/// <param name="StorageQuota">Characters a storage area may hold (5 Mi, like Chromium).</param>
/// <param name="IsFile">Whether the page is a <c>file://</c> page (no cookies, opaque origin).</param>
/// <param name="Refresh">A pending meta refresh, or <see langword="null"/>.</param>
public sealed record PageBootstrap(
    string Token,
    string Url,
    string Referrer,
    string Cookies,
    IReadOnlyDictionary<string, string> LocalStorage,
    IReadOnlyDictionary<string, string> SessionStorage,
    int StorageQuota,
    bool IsFile,
    RefreshDirective? Refresh = null);

/// <summary>A <c>fetch()</c> or <c>XMLHttpRequest</c> of the page, as the bridge serialized it.</summary>
/// <param name="Method">HTTP method, upper case.</param>
/// <param name="Url">Absolute URL, resolved by the frame against the page's base.</param>
/// <param name="Headers">Request headers as <c>[name, value]</c> pairs.</param>
/// <param name="Body">Body bytes in base64, or <see langword="null"/>.</param>
/// <param name="Kind"><c>fetch</c> or <c>xhr</c>.</param>
/// <param name="Credentials">Fetch credentials mode: <c>omit</c>, <c>same-origin</c> or <c>include</c>.</param>
/// <param name="Mode">Fetch request mode: <c>cors</c>, <c>no-cors</c>, <c>same-origin</c>…</param>
public sealed record BridgeFetchRequest(
    string Method,
    string Url,
    IReadOnlyList<string[]>? Headers,
    string? Body,
    string? Kind,
    string? Credentials,
    string? Mode);

/// <summary>The answer to a <see cref="BridgeFetchRequest"/>.</summary>
/// <param name="Error">Whether the request failed (the page sees a <c>TypeError</c>).</param>
/// <param name="Message">Error message for the page.</param>
/// <param name="Status">Status code.</param>
/// <param name="StatusText">Reason phrase.</param>
/// <param name="Url">Final URL after redirects.</param>
/// <param name="Redirected">Whether a redirect was followed.</param>
/// <param name="Type">Response type: <c>basic</c> (same origin) or <c>cors</c>.</param>
/// <param name="Opaque">An opaque <c>no-cors</c> response: no status, headers nor body for the page.</param>
/// <param name="Headers">Response headers the page may read.</param>
/// <param name="Body">Body bytes in base64.</param>
/// <param name="Cookies">New value of <c>document.cookie</c> after the exchange.</param>
public sealed record BridgeFetchReply(
    bool Error,
    string? Message,
    int Status,
    string? StatusText,
    string? Url,
    bool Redirected,
    string? Type,
    bool Opaque,
    IReadOnlyList<string[]>? Headers,
    string? Body,
    string? Cookies)
{
    /// <summary>Creates a failed reply.</summary>
    public static BridgeFetchReply Fail(string message, string? cookies = null) =>
        new(true, message, 0, null, null, false, null, false, null, null, cookies);
}

/// <summary>A resource added by the page's script after the load (an <c>&lt;img&gt;</c>, a style sheet…).</summary>
/// <param name="Url">Absolute URL.</param>
/// <param name="Kind"><c>image</c>, <c>script</c>, <c>stylesheet</c> or <c>media</c>.</param>
public sealed record BridgeResourceRequest(string Url, string? Kind);

/// <summary>The answer to a <see cref="BridgeResourceRequest"/>.</summary>
/// <param name="DataUrl">The resource as a <c>data:</c> URL; <c>data:,</c> when it failed.</param>
public sealed record BridgeResourceReply(string DataUrl);

/// <summary>A navigation asked by the page: a link, a form, a meta refresh.</summary>
/// <param name="Url">Target URL.</param>
/// <param name="Method"><c>GET</c> or <c>POST</c>.</param>
/// <param name="Body">Form body in base64 for a <c>POST</c>.</param>
/// <param name="ContentType">Form encoding of <paramref name="Body"/>.</param>
/// <param name="Target">The link's <c>target</c>, or empty.</param>
/// <param name="Download">The link has a <c>download</c> attribute.</param>
/// <param name="Replace">Replace the history entry (meta refresh) instead of adding one.</param>
public sealed record BridgeNavigateEvent(string Url, string? Method, string? Body, string? ContentType, string? Target, bool Download, bool Replace);

/// <summary>A <c>console.*</c> call or an uncaught error of the page.</summary>
/// <param name="Level"><c>debug</c>, <c>log</c>, <c>warning</c> or <c>error</c>.</param>
/// <param name="Message">Formatted text.</param>
/// <param name="Source">Script location, or empty.</param>
public sealed record BridgeConsoleEvent(string? Level, string? Message, string? Source);

/// <summary>A change to <c>localStorage</c> or <c>sessionStorage</c>.</summary>
/// <param name="Area"><c>local</c> or <c>session</c>.</param>
/// <param name="Op"><c>set</c>, <c>remove</c> or <c>clear</c>.</param>
/// <param name="Key">Item key.</param>
/// <param name="Value">Item value for <c>set</c>.</param>
public sealed record BridgeStorageEvent(string? Area, string? Op, string? Key, string? Value);

/// <summary>An assignment to <c>document.cookie</c>.</summary>
/// <param name="Value">The assigned string (<c>name=value; Max-Age=0</c>…).</param>
public sealed record BridgeCookieEvent(string? Value);

/// <summary>The document's title changed.</summary>
/// <param name="Title">New title.</param>
public sealed record BridgeTitleEvent(string? Title);

/// <summary>An <c>alert()</c> of the page.</summary>
/// <param name="Kind">Dialog kind (<c>alert</c>).</param>
/// <param name="Message">Text.</param>
public sealed record BridgeDialogEvent(string? Kind, string? Message);

/// <summary>A <c>history.pushState</c>/<c>replaceState</c> with a URL, or a fragment link.</summary>
/// <param name="Url">The new address.</param>
/// <param name="Replace">Whether the current history entry is replaced.</param>
public sealed record BridgeHistoryEvent(string Url, bool Replace);

/// <summary>A browser shortcut typed while the page had the focus.</summary>
/// <param name="Key">The <c>KeyboardEvent.key</c>.</param>
/// <param name="Ctrl">Control held.</param>
/// <param name="Shift">Shift held.</param>
/// <param name="Alt">Alt held.</param>
public sealed record BridgeKeyEvent(string? Key, bool Ctrl, bool Shift, bool Alt);

/// <summary>The page asks the values remembered for a form field (ADR 0088 §6).</summary>
/// <param name="Name">The field's <c>name</c>.</param>
public sealed record BridgeFormHistoryRequest(string? Name);

/// <summary>The values offered in the field's <c>&lt;datalist&gt;</c>, most used first.</summary>
/// <param name="Values">The values.</param>
public sealed record BridgeFormHistoryReply(IReadOnlyList<string> Values);

/// <summary>A form was submitted: the fields the browser may remember (text-like, named, not <c>autocomplete="off"</c>).</summary>
/// <param name="Fields">Name and value pairs.</param>
public sealed record BridgeFormSubmitEvent(IReadOnlyList<string[]>? Fields);

/// <summary>Source-generated JSON for the bridge messages (trim-safe, camelCase like the JavaScript side).</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(PageBootstrap))]
[JsonSerializable(typeof(BridgeFetchRequest))]
[JsonSerializable(typeof(BridgeFetchReply))]
[JsonSerializable(typeof(BridgeResourceRequest))]
[JsonSerializable(typeof(BridgeResourceReply))]
[JsonSerializable(typeof(BridgeNavigateEvent))]
[JsonSerializable(typeof(BridgeConsoleEvent))]
[JsonSerializable(typeof(BridgeStorageEvent))]
[JsonSerializable(typeof(BridgeCookieEvent))]
[JsonSerializable(typeof(BridgeTitleEvent))]
[JsonSerializable(typeof(BridgeDialogEvent))]
[JsonSerializable(typeof(BridgeHistoryEvent))]
[JsonSerializable(typeof(BridgeKeyEvent))]
[JsonSerializable(typeof(BridgeFormHistoryRequest))]
[JsonSerializable(typeof(BridgeFormHistoryReply))]
[JsonSerializable(typeof(BridgeFormSubmitEvent))]
[JsonSerializable(typeof(Dictionary<string, string>))]
public sealed partial class PageBridgeJsonContext : JsonSerializerContext;

/// <summary>The page bridge script (<c>PageBridge.js</c>), embedded in the assembly.</summary>
public static class PageBridgeScript
{
    private static readonly Lazy<string> Cached = new(Load);

    /// <summary>Gets the script: a function expression taking the <see cref="PageBootstrap"/>.</summary>
    public static string Source => Cached.Value;

    private static string Load()
    {
        using Stream stream = typeof(PageBridgeScript).Assembly.GetManifestResourceStream("Kazo.Os.Apps.Browser.Bridge.PageBridge.js")
            ?? throw new InvalidOperationException("The page bridge script is not embedded in Kazo.Os.Apps.Browser.");
        using StreamReader reader = new(stream);

        // "</script" inside the text would end the inline <script> early.
        return reader.ReadToEnd().Replace("</script", "<\\/script", StringComparison.OrdinalIgnoreCase);
    }
}
