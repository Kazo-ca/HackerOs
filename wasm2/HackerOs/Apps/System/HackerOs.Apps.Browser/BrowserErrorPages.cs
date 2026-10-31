using System.Globalization;
using HackerOs.Apps.Browser.Net;
using Microsoft.Extensions.Localization;

namespace HackerOs.Apps.Browser;

/// <summary>What the browser's own error page says.</summary>
/// <param name="Title">Headline ("This site can't be reached").</param>
/// <param name="Message">What happened, naming the host or file.</param>
/// <param name="Code">The <c>ERR_*</c> code, or <c>HTTP ERROR 404</c>.</param>
/// <param name="Hints">What the user can try.</param>
public sealed record BrowserErrorView(string Title, string Message, string Code, IReadOnlyList<string> Hints);

/// <summary>
/// The texts of the browser's error pages, modeled on Chromium's: one per <c>net::ERR_*</c> code a
/// request can end with, and the "HTTP ERROR" page of an error status sent with no body
/// (ADR 0075, "erreurs réseau" of chantier <c>web/4-browser-html</c>). Each builder takes the language
/// of the browser's process (ADR 0104); <see langword="null"/> gives English. The codes are never
/// translated.
/// </summary>
public static class BrowserErrorPages
{
    /// <summary>Describes a network error of a navigation to <paramref name="url"/>.</summary>
    /// <param name="code">The <c>ERR_*</c> code the request ended with.</param>
    /// <param name="url">The address, or <see langword="null"/> when it could not be parsed.</param>
    /// <param name="culture">The language of the page; <see langword="null"/> gives English.</param>
    public static BrowserErrorView ForNetworkError(string code, Uri? url, CultureInfo? culture = null)
    {
        IStringLocalizer<Strings> text = Strings.For(culture);
        string host = url is null ? text["Error.ThisSite"] : url.Scheme == "file" ? BrowserUrl.ToLocalPath(url) : url.Host;
        string display = url is null ? string.Empty : BrowserUrl.ToDisplayString(url);
        string unreachable = text["Error.Unreachable.Title"];
        return code switch
        {
            "ERR_NAME_NOT_RESOLVED" => new(unreachable,
                text["Error.NameNotResolved.Message", host], code,
                [text["Error.NameNotResolved.Hint.Typo", host], text["Error.NameNotResolved.Hint.Hosts"], text["Error.Hint.HostedInstalled"],
                 text["Error.NameNotResolved.Hint.Server"]]),
            // ADR 0210 §6: the relay's operator refused the address, the port or the app.
            "ERR_BLOCKED_BY_ADMINISTRATOR" => new(text["Error.Blocked.Title"],
                text["Error.Blocked.Message", host], code,
                [text["Error.Blocked.Hint"]]),
            "ERR_CONNECTION_REFUSED" => new(unreachable,
                text["Error.ConnectionRefused.Message", host], code,
                [text["Error.ConnectionRefused.Hint.Listening"], text["Error.ConnectionRefused.Hint.Start"]]),
            "ERR_HOST_UNREACHABLE" or "ERR_ADDRESS_UNREACHABLE" => new(unreachable,
                text["Error.HostUnreachable.Message", host], code,
                [text["Error.HostUnreachable.Hint"]]),
            "ERR_CONNECTION_TIMED_OUT" or "ERR_TIMED_OUT" => new(unreachable,
                text["Error.TimedOut.Message", host], code,
                [text["Error.Hint.CheckConnection"], text["Error.Hint.TryLater"]]),
            "ERR_TOO_MANY_REDIRECTS" => new(text["Error.NotWorking.Title"],
                text["Error.TooManyRedirects.Message", host], code,
                [text["Error.TooManyRedirects.Hint"]]),
            BrowserErrorCodes.FileNotFound => new(text["Error.FileNotFound.Title"],
                text["Error.FileNotFound.Message", host], code,
                [text["Error.FileNotFound.Hint.Path"], text["Error.FileNotFound.Hint.Folder"]]),
            BrowserErrorCodes.AccessDenied => new(text["Error.AccessDenied.Title"],
                text["Error.AccessDenied.Message", host], code,
                [text["Error.AccessDenied.Hint"]]),
            BrowserErrorCodes.InvalidUrl => new(unreachable,
                text["Error.InvalidUrl.Message", display], code, []),
            BrowserErrorCodes.UnknownUrlScheme => new(text["Error.UnknownScheme.Title"],
                text["Error.UnknownScheme.Message", display], code, []),
            BrowserErrorCodes.BlockedLocalResource => new(text["Error.BlockedLocal.Title"],
                text["Error.BlockedLocal.Message", display], code, []),
            _ => new(unreachable,
                text["Error.Failed.Message", display], code,
                [text["Error.Hint.CheckConnection"], text["Error.Hint.TryLater"]]),
        };
    }

    /// <summary>The code of the page shown when a Blazor site's renderer crashed (ADR 0084 §4), Chromium's for a crashed renderer.</summary>
    public const string RendererCrashed = "STATUS_ACCESS_VIOLATION";

    /// <summary>The code of the page shown when the OS ended a Blazor site's renderer, Chromium's for a killed renderer.</summary>
    public const string RendererKilled = "RESULT_CODE_KILLED";

    /// <summary>The code of the page shown when a Blazor site exists but its renderer did not start.</summary>
    public const string SiteNotStarted = "ERR_SITE_NOT_STARTED";

    /// <summary>Chromium's "Aw, Snap!" page: the site's renderer crashed or was ended (ADR 0084 §4).</summary>
    /// <param name="url">The page's URL.</param>
    /// <param name="killed">Whether the OS ended the renderer rather than the site crashing.</param>
    /// <param name="culture">The language of the page; <see langword="null"/> gives English.</param>
    public static BrowserErrorView ForRendererCrash(Uri? url, bool killed, CultureInfo? culture = null)
    {
        IStringLocalizer<Strings> text = Strings.For(culture);
        return new(
            text["Error.Crash.Title"],
            text["Error.Crash.Message"],
            killed ? RendererKilled : RendererCrashed,
            [text["Error.Hint.Reload"], killed ? text["Error.Crash.Hint.Killed"] : text["Error.Crash.Hint.Console"]]);
    }

    /// <summary>The OS refused to start the site's renderer (ADR 0084 §3): the reason, and what to check.</summary>
    /// <param name="url">The page's URL.</param>
    /// <param name="reason">Why the renderer did not start.</param>
    /// <param name="culture">The language of the page; <see langword="null"/> gives English.</param>
    public static BrowserErrorView ForSiteNotStarted(Uri url, string reason, CultureInfo? culture = null)
    {
        ArgumentNullException.ThrowIfNull(url);
        IStringLocalizer<Strings> text = Strings.For(culture);
        return new(text["Error.SiteNotStarted.Title"], text["Error.SiteNotStarted.Message", url.Host, reason], SiteNotStarted,
            [text["Error.SiteNotStarted.Hint"], text["Error.Hint.Reload"]]);
    }

    /// <summary>Describes an error status that came with no body to show.</summary>
    /// <param name="statusCode">The HTTP status.</param>
    /// <param name="url">The page's URL.</param>
    /// <param name="culture">The language of the page; <see langword="null"/> gives English.</param>
    public static BrowserErrorView ForHttpStatus(int statusCode, Uri url, CultureInfo? culture = null)
    {
        ArgumentNullException.ThrowIfNull(url);
        IStringLocalizer<Strings> text = Strings.For(culture);
        // Chromium keeps "HTTP ERROR 404" in every language: it is a code, not a sentence.
        string code = $"HTTP ERROR {statusCode}";
        return statusCode switch
        {
            404 or 410 => new(text["Error.Http.NotFound.Title", url.Host],
                text["Error.Http.NotFound.Message", url.AbsoluteUri], code, [text["Error.Http.NotFound.Hint"]]),
            401 or 403 => new(text["Error.Http.Denied.Title", url.Host],
                text["Error.Http.Denied.Message"], code, [text["Error.Http.Denied.Hint"]]),
            >= 500 => new(text["Error.NotWorking.Title"],
                text["Error.Http.Server.Message", url.Host], code, [text["Error.Hint.TryLater"], text["Error.Http.Server.Hint"]]),
            _ => new(text["Error.NotWorking.Title"],
                text["Error.Http.Other.Message", url.Host, statusCode], code, []),
        };
    }

    /// <summary>
    /// The page shown when a page left its frame by script (<c>window.location</c>), which the
    /// browser does not support yet.
    /// </summary>
    /// <param name="culture">The language of the page; <see langword="null"/> gives English.</param>
    public static BrowserErrorView ForScriptNavigation(CultureInfo? culture = null)
    {
        IStringLocalizer<Strings> text = Strings.For(culture);
        return new(
            text["Error.ScriptNavigation.Title"],
            text["Error.ScriptNavigation.Message"],
            BrowserErrorCodes.BlockedByClient,
            [text["Error.Hint.Reload"], text["Error.ScriptNavigation.Hint"]]);
    }
}
