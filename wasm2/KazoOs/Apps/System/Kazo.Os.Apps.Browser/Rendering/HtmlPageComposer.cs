using System.Text.Json;
using AngleSharp;
using AngleSharp.Dom;
using AngleSharp.Html;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;
using Kazo.Os.Apps.Browser.Bridge;
using Kazo.Os.Apps.Browser.DevTools;
using Kazo.Os.Apps.Browser.Net;

namespace Kazo.Os.Apps.Browser.Rendering;

/// <summary>A page ready for the isolated frame.</summary>
/// <param name="Html">The final document, written to the frame's <c>srcdoc</c>.</param>
/// <param name="Title">The document's <c>&lt;title&gt;</c>, or empty.</param>
/// <param name="BaseUrl">The URL relative references of the page resolve against.</param>
/// <param name="SubresourceCount">Number of sub-resource requests the page needed.</param>
public sealed record ComposedPage(string Html, string Title, Uri BaseUrl, int SubresourceCount);

/// <summary>
/// Turns an HTML response into the self-contained document the isolated frame shows (ADR 0075 §1
/// and §2), as a browser's loader would: the HTML is parsed in C# with AngleSharp, each style
/// sheet, script, image, media and font is loaded through the simulated network (or the file
/// system for <c>file://</c>) and inlined as a <c>data:</c> URL, and the page bridge, a
/// Content Security Policy and the document's base URL are put first in <c>&lt;head&gt;</c>.
/// </summary>
/// <remarks>
/// The policy (<see cref="ContentSecurityPolicy"/>) lets the frame load nothing but <c>data:</c>
/// and <c>blob:</c> URLs: whatever the page tries to reach on its own (a resource added by a
/// script the bridge did not see, a <c>WebSocket</c>…) is refused by the host browser, so a site's
/// JavaScript never reaches the real Internet nor Kazo OS.
/// </remarks>
public sealed class HtmlPageComposer
{
    /// <summary>The policy written in the frame. <c>connect-src 'none'</c>: requests go through the bridge only.</summary>
    public const string ContentSecurityPolicy =
        "default-src 'none'; script-src 'unsafe-inline' 'unsafe-eval' data: blob:; style-src 'unsafe-inline' data: blob:; "
        + "img-src data: blob:; font-src data: blob:; media-src data: blob:; connect-src 'none'; frame-src 'none'; "
        + "object-src 'none'; worker-src 'none'; manifest-src 'none'; form-action 'none'; base-uri 'none'";

    // Link relations that would fetch on their own and have no effect on the rendered page.
    private static readonly HashSet<string> IgnoredLinkRelations = new(StringComparer.OrdinalIgnoreCase)
    {
        "preload", "prefetch", "preconnect", "dns-prefetch", "modulepreload", "manifest", "prerender", "alternate",
    };

    private readonly BrowserResourceLoader _loader;

    /// <summary>Initializes a composer that loads sub-resources through <paramref name="loader"/>.</summary>
    public HtmlPageComposer(BrowserResourceLoader loader)
    {
        _loader = loader ?? throw new ArgumentNullException(nameof(loader));
    }

    /// <summary>Builds the frame document of <paramref name="html"/>.</summary>
    /// <param name="html">The HTML received.</param>
    /// <param name="session">The document: its final URL, sub-resource cache and cancellation.</param>
    /// <param name="bootstrap">State handed to the page bridge (cookies, storage, token).</param>
    public async Task<ComposedPage> ComposeAsync(string html, PageSession session, PageBootstrap bootstrap)
    {
        ArgumentNullException.ThrowIfNull(html);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(bootstrap);
        Uri documentUrl = session.DocumentUrl;
        CancellationToken cancellationToken = session.CancellationToken;

        // IsScripting: the frame runs scripts, so <noscript> content must stay inert text.
        HtmlParser parser = new(new HtmlParserOptions { IsScripting = true });
        IHtmlDocument document = await parser.ParseDocumentAsync(html, cancellationToken).ConfigureAwait(false);

        Uri baseUrl = ResolveBaseUrl(document, documentUrl);
        SubresourceFetcher fetcher = session.Fetcher;
        CssResourceRewriter css = session.Css;
        RefreshDirective? refresh = TakeRefreshDirective(document, baseUrl);

        // Loads run concurrently; each returns the DOM change to make, applied afterwards one by
        // one because the AngleSharp DOM is not thread-safe.
        List<Task<Action?>> loads = [];
        loads.AddRange(document.QuerySelectorAll("link[href]").Select(link => InlineLinkAsync(link, baseUrl, fetcher, css)));
        loads.AddRange(document.QuerySelectorAll("script[src]").Select(script => InlineScriptAsync(script, baseUrl, fetcher)));
        loads.AddRange(document.QuerySelectorAll("style").Select(style => RewriteStyleElementAsync(style, baseUrl, css)));
        loads.AddRange(document.QuerySelectorAll("[style]").Select(element => RewriteStyleAttributeAsync(element, baseUrl, css)));
        loads.AddRange(MediaAttributes(document).Select(target => InlineAttributeAsync(target.Element, target.Attribute, target.Kind, baseUrl, fetcher)));
        loads.AddRange(document.QuerySelectorAll("img[srcset], source[srcset]").Select(element => InlineSrcSetAsync(element, baseUrl, fetcher)));
        foreach (IElement frame in document.QuerySelectorAll("iframe[src], frame[src], object[data], embed[src]"))
        {
            // Nested browsing contexts would need a bridge of their own: not in v1 (ADR 0075 §3).
            _loader.Log.Add(new ConsoleLogEntry(ConsoleLevel.Warning,
                $"Nested <{frame.LocalName}> is not supported by Kazo OS Browser yet; it stays empty.", BrowserUrl.ToDisplayString(documentUrl)));
            frame.SetAttribute(frame.LocalName == "object" ? "data" : "src", "about:blank");
        }

        foreach (Action? apply in await Task.WhenAll(loads).ConfigureAwait(false))
        {
            apply?.Invoke();
        }

        InjectHead(document, baseUrl, bootstrap with { Refresh = refresh });
        return new ComposedPage(Serialize(document), document.Title ?? string.Empty, baseUrl, fetcher.RequestCount);
    }

    /// <summary>The page bridge script, injected first in <c>&lt;head&gt;</c>.</summary>
    public static string BridgeScript => PageBridgeScript.Source;

    private static Uri ResolveBaseUrl(IHtmlDocument document, Uri documentUrl)
    {
        Uri baseUrl = documentUrl;
        IElement? baseElement = document.QuerySelector("base[href]");
        if (baseElement is not null && BrowserUrl.Resolve(documentUrl, baseElement.GetAttribute("href")) is { } declared)
        {
            baseUrl = declared;
        }

        // The page's own <base> elements are replaced by the one the composer writes (CSP base-uri 'none').
        foreach (IElement element in document.QuerySelectorAll("base").ToList())
        {
            element.Remove();
        }

        return baseUrl;
    }

    private static RefreshDirective? TakeRefreshDirective(IHtmlDocument document, Uri baseUrl)
    {
        IElement? meta = document.QuerySelectorAll("meta[http-equiv]")
            .FirstOrDefault(element => string.Equals(element.GetAttribute("http-equiv"), "refresh", StringComparison.OrdinalIgnoreCase));
        if (meta is null)
        {
            return null;
        }

        // The frame cannot navigate itself: the bridge asks the browser when the delay ends.
        meta.Remove();
        string content = meta.GetAttribute("content") ?? string.Empty;
        string[] parts = content.Split([';', ','], 2);
        if (!double.TryParse(parts[0].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double seconds))
        {
            return null;
        }

        string target = parts.Length > 1 ? parts[1].Trim() : string.Empty;
        if (target.StartsWith("url", StringComparison.OrdinalIgnoreCase) && target.Contains('=', StringComparison.Ordinal))
        {
            target = target[(target.IndexOf('=', StringComparison.Ordinal) + 1)..].Trim().Trim('\'', '"');
        }

        Uri? url = target.Length == 0 ? null : BrowserUrl.Resolve(baseUrl, target);
        return new RefreshDirective(Math.Max(0, seconds), url?.AbsoluteUri);
    }

    private async Task<Action?> InlineLinkAsync(IElement link, Uri baseUrl, SubresourceFetcher fetcher, CssResourceRewriter css)
    {
        string[] relations = (link.GetAttribute("rel") ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Uri? url = BrowserUrl.Resolve(baseUrl, link.GetAttribute("href"));
        if (relations.Contains("stylesheet", StringComparer.OrdinalIgnoreCase))
        {
            if (url is null || link.HasAttribute("disabled"))
            {
                return null;
            }

            LoadedResource? sheet = await fetcher.GetAsync(url, NetworkRequestKind.Stylesheet).ConfigureAwait(false);
            string text = sheet is null ? string.Empty : await css.RewriteAsync(sheet.ReadAsString(), sheet.Url).ConfigureAwait(false);

            return () =>
            {
                // A <style> in the same place keeps the cascade order of the original <link>.
                IElement style = link.Owner!.CreateElement("style");
                if (link.GetAttribute("media") is { } media)
                {
                    style.SetAttribute("media", media);
                }

                style.SetAttribute("data-href", url.AbsoluteUri);
                style.TextContent = text;
                link.Replace(style);
            };
        }

        if (relations.Any(relation => relation.Contains("icon", StringComparison.OrdinalIgnoreCase)) && url is not null)
        {
            LoadedResource? icon = await fetcher.GetAsync(url, NetworkRequestKind.Image).ConfigureAwait(false);
            return () => link.SetAttribute("href", icon?.ToDataUrl("image/x-icon") ?? DataUrl.Empty);
        }

        return relations.Any(IgnoredLinkRelations.Contains) || url is not null ? () => link.Remove() : null;
    }

    private static async Task<Action?> InlineScriptAsync(IElement script, Uri baseUrl, SubresourceFetcher fetcher)
    {
        Uri? url = BrowserUrl.Resolve(baseUrl, script.GetAttribute("src"));
        if (url is null)
        {
            return null;
        }

        LoadedResource? source = await fetcher.GetAsync(url, NetworkRequestKind.Script).ConfigureAwait(false);
        string dataUrl = source is null ? "data:text/javascript," : DataUrl.FromText("text/javascript", source.ReadAsString());
        return () =>
        {
            // A data: src keeps async/defer/module ordering; data-src keeps the original URL for the source view.
            script.SetAttribute("data-src", url.AbsoluteUri);
            script.SetAttribute("src", dataUrl);
            script.RemoveAttribute("integrity");
            script.RemoveAttribute("crossorigin");
        };
    }

    private static async Task<Action?> RewriteStyleElementAsync(IElement style, Uri baseUrl, CssResourceRewriter css)
    {
        string text = style.TextContent;
        string rewritten = await css.RewriteAsync(text, baseUrl).ConfigureAwait(false);
        return text == rewritten ? null : () => style.TextContent = rewritten;
    }

    private static async Task<Action?> RewriteStyleAttributeAsync(IElement element, Uri baseUrl, CssResourceRewriter css)
    {
        string text = element.GetAttribute("style") ?? string.Empty;
        if (!text.Contains("url(", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string rewritten = await css.RewriteAsync(text, baseUrl).ConfigureAwait(false);
        return () => element.SetAttribute("style", rewritten);
    }

    private static IEnumerable<(IElement Element, string Attribute, NetworkRequestKind Kind)> MediaAttributes(IHtmlDocument document)
    {
        foreach (IElement element in document.QuerySelectorAll("img[src], input[type=image][src]"))
        {
            yield return (element, "src", NetworkRequestKind.Image);
        }

        foreach (IElement element in document.QuerySelectorAll("video[poster]"))
        {
            yield return (element, "poster", NetworkRequestKind.Image);
        }

        foreach (IElement element in document.QuerySelectorAll("video[src], audio[src], source[src], track[src]"))
        {
            NetworkRequestKind kind = element.LocalName == "source" && element.ParentElement?.LocalName == "picture"
                ? NetworkRequestKind.Image
                : NetworkRequestKind.Media;
            yield return (element, "src", kind);
        }

        // SVG <image href> / <image xlink:href>.
        foreach (IElement element in document.QuerySelectorAll("image"))
        {
            if (element.HasAttribute("href"))
            {
                yield return (element, "href", NetworkRequestKind.Image);
            }
            else if (element.HasAttribute("xlink:href"))
            {
                yield return (element, "xlink:href", NetworkRequestKind.Image);
            }
        }
    }

    private static async Task<Action?> InlineAttributeAsync(IElement element, string attribute, NetworkRequestKind kind, Uri baseUrl, SubresourceFetcher fetcher)
    {
        Uri? url = BrowserUrl.Resolve(baseUrl, element.GetAttribute(attribute));
        if (url is null)
        {
            return null;
        }

        LoadedResource? resource = await fetcher.GetAsync(url, kind).ConfigureAwait(false);
        string fallback = Simulation.Abstractions.Network.SimulatedMediaTypes.GetMediaType(
            Simulation.Abstractions.Network.SimulatedFileTypes.GetContentType(url.AbsolutePath));
        string value = resource is null ? DataUrl.Empty : resource.ToDataUrl(fallback);
        return () => element.SetAttribute(attribute, value);
    }

    private static async Task<Action?> InlineSrcSetAsync(IElement element, Uri baseUrl, SubresourceFetcher fetcher)
    {
        // "a.png 1x, b.png 2x": each candidate is a URL then an optional descriptor.
        string[] candidates = (element.GetAttribute("srcset") ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        List<Task<string>> rewritten = [];
        foreach (string candidate in candidates)
        {
            rewritten.Add(RewriteCandidateAsync(candidate));
        }

        string[] values = await Task.WhenAll(rewritten).ConfigureAwait(false);
        string srcset = string.Join(", ", values.Where(value => value.Length > 0));
        return () => element.SetAttribute("srcset", srcset);

        async Task<string> RewriteCandidateAsync(string candidate)
        {
            int space = candidate.IndexOfAny([' ', '\t', '\n']);
            string reference = space < 0 ? candidate : candidate[..space];
            string descriptor = space < 0 ? string.Empty : candidate[space..];
            Uri? url = BrowserUrl.Resolve(baseUrl, reference);
            if (url is null)
            {
                return candidate;
            }

            LoadedResource? image = await fetcher.GetAsync(url, NetworkRequestKind.Image).ConfigureAwait(false);
            return image is null ? string.Empty : image.ToDataUrl("image/png") + descriptor;
        }
    }

    private static void InjectHead(IHtmlDocument document, Uri baseUrl, PageBootstrap bootstrap)
    {
        IElement head = document.Head ?? (IElement)document.DocumentElement.InsertBefore(document.CreateElement("head"), document.Body);

        IElement baseElement = document.CreateElement("base");
        baseElement.SetAttribute("href", baseUrl.AbsoluteUri);

        IElement policy = document.CreateElement("meta");
        policy.SetAttribute("http-equiv", "Content-Security-Policy");
        policy.SetAttribute("content", ContentSecurityPolicy);

        IElement charset = document.CreateElement("meta");
        charset.SetAttribute("charset", "utf-8");

        IElement bridge = document.CreateElement("script");
        bridge.SetAttribute("data-kazoos-bridge", string.Empty);
        bridge.TextContent = $"({PageBridgeScript.Source})({JsonSerializer.Serialize(bootstrap, PageBridgeJsonContext.Default.PageBootstrap)});";

        // Order matters: <base> before the policy (base-uri 'none' only applies after it), the
        // policy before anything that loads, the bridge before any page script.
        head.Prepend(charset, baseElement, policy, bridge);
    }

    private static string Serialize(IHtmlDocument document)
    {
        using StringWriter writer = new();
        document.ToHtml(writer, HtmlMarkupFormatter.Instance);
        return writer.ToString();
    }
}
