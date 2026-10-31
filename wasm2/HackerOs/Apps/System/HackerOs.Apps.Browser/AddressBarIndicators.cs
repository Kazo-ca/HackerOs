using System.Globalization;
using HackerOs.Apps.Browser.Net;
using Microsoft.Extensions.Localization;

namespace HackerOs.Apps.Browser;

/// <summary>What the start of the address bar shows for the current page (ADR 0210 §4).</summary>
/// <param name="SourceIcon">The icon of where the page came from; empty when there is nothing to show.</param>
/// <param name="SourceClass">The CSS class of that icon (<c>source-real</c>, <c>source-simulated</c>…).</param>
/// <param name="SourceTitle">Its tooltip and accessible name.</param>
/// <param name="NotSecureLabel">"Not secure" for an <c>http:</c> page, as Chromium writes it; otherwise <see langword="null"/>.</param>
public sealed record AddressBarIndicator(string SourceIcon, string SourceClass, string SourceTitle, string? NotSecureLabel);

/// <summary>
/// Builds the indicators at the start of the address bar (ADR 0210 §4): an icon telling where the
/// page came from (simulated network, a site of this machine, a local file, or the real Internet
/// through the HackerOS proxy) and, for <c>http:</c>, Chromium's "Not secure" label next to the
/// scheme icon. The server's address and token are never shown (ADR 0056 §6).
/// </summary>
public static class AddressBarIndicators
{
    /// <summary>Gets the indicator of a page loaded from <paramref name="url"/>.</summary>
    /// <param name="url">The page's URL, or <see langword="null"/> when no page is shown.</param>
    /// <param name="source">Where the page came from, or <see langword="null"/> for an error or an internal page.</param>
    /// <param name="culture">The language of the browser's process; <see langword="null"/> gives English.</param>
    public static AddressBarIndicator For(Uri? url, BrowserResponseSource? source, CultureInfo? culture = null)
    {
        IStringLocalizer<Strings> text = Strings.For(culture);
        string? notSecure = url?.Scheme == "http" ? text["Scheme.NotSecure"].Value : null;
        return source switch
        {
            BrowserResponseSource.RealNetwork => new("⇄", "source-real", text["Source.RealNetwork"], notSecure),
            BrowserResponseSource.LocalSite => new("⌂", "source-local", text["Source.LocalSite"], notSecure),
            BrowserResponseSource.Simulated => new("◎", "source-simulated", text["Source.Simulated"], notSecure),
            // A file already shows its own icon as its scheme.
            _ => new(string.Empty, "source-none", string.Empty, notSecure),
        };
    }
}
