using System.Text;
using System.Text.RegularExpressions;
using Kazo.Os.Apps.Browser.DevTools;
using Kazo.Os.Apps.Browser.Net;

namespace Kazo.Os.Apps.Browser.Rendering;

/// <summary>
/// Makes a style sheet self-contained for the isolated frame (ADR 0075 §2): every <c>url()</c>
/// (backgrounds, fonts, cursors) is loaded through the simulated network and replaced by a
/// <c>data:</c> URL, and every <c>@import</c> is loaded, rewritten the same way against its own
/// URL, and inlined.
/// </summary>
public sealed partial class CssResourceRewriter
{
    /// <summary>How deep <c>@import</c> chains are followed (Chromium has no limit, but cycles must stop).</summary>
    public const int MaxImportDepth = 4;

    private readonly SubresourceFetcher _fetcher;

    /// <summary>Initializes a rewriter that loads through <paramref name="fetcher"/>.</summary>
    public CssResourceRewriter(SubresourceFetcher fetcher)
    {
        _fetcher = fetcher ?? throw new ArgumentNullException(nameof(fetcher));
    }

    /// <summary>Rewrites <paramref name="css"/>, whose relative URLs resolve against <paramref name="baseUrl"/>.</summary>
    public Task<string> RewriteAsync(string css, Uri baseUrl) => RewriteAsync(css, baseUrl, depth: 0);

    private async Task<string> RewriteAsync(string css, Uri baseUrl, int depth)
    {
        ArgumentNullException.ThrowIfNull(css);
        ArgumentNullException.ThrowIfNull(baseUrl);
        if (css.Length == 0 || (!css.Contains("url(", StringComparison.OrdinalIgnoreCase) && !css.Contains("@import", StringComparison.OrdinalIgnoreCase)))
        {
            return css;
        }

        // Comments may hold "url(" text that is not a reference: strip them first, as the CSS tokenizer does.
        css = CommentPattern().Replace(css, string.Empty);

        List<(Match Match, Task<string> Replacement)> replacements = [];
        foreach (Match match in TokenPattern().Matches(css))
        {
            replacements.Add((match, match.Groups["import"].Success
                ? ReplaceImportAsync(match, baseUrl, depth)
                : ReplaceUrlAsync(match, baseUrl)));
        }

        await Task.WhenAll(replacements.Select(item => item.Replacement)).ConfigureAwait(false);

        StringBuilder result = new(css.Length);
        int position = 0;
        foreach ((Match match, Task<string> replacement) in replacements)
        {
            result.Append(css, position, match.Index - position).Append(await replacement.ConfigureAwait(false));
            position = match.Index + match.Length;
        }

        return result.Append(css, position, css.Length - position).ToString();
    }

    private async Task<string> ReplaceUrlAsync(Match match, Uri baseUrl)
    {
        string reference = FirstValue(match, "dq", "sq", "bare");
        Uri? url = BrowserUrl.Resolve(baseUrl, reference);
        if (url is null)
        {
            // data:, #fragment and other local references stay as they are.
            return match.Value;
        }

        LoadedResource? resource = await _fetcher.GetAsync(url, GuessKind(url)).ConfigureAwait(false);
        string fallback = Simulation.Abstractions.Network.SimulatedFileTypes.GetContentType(url.AbsolutePath);
        return $"url(\"{(resource is null ? DataUrl.Empty : resource.ToDataUrl(Simulation.Abstractions.Network.SimulatedMediaTypes.GetMediaType(fallback)))}{Fragment(url)}\")";
    }

    private async Task<string> ReplaceImportAsync(Match match, Uri baseUrl, int depth)
    {
        string reference = FirstValue(match, "idq", "isq", "iudq", "iusq", "iubare");
        string media = match.Groups["media"].Value.Trim();
        Uri? url = BrowserUrl.Resolve(baseUrl, reference);
        if (url is null || depth >= MaxImportDepth)
        {
            return string.Empty;
        }

        LoadedResource? resource = await _fetcher.GetAsync(url, NetworkRequestKind.Stylesheet).ConfigureAwait(false);
        if (resource is null)
        {
            return string.Empty;
        }

        string imported = await RewriteAsync(resource.ReadAsString(), resource.Url, depth + 1).ConfigureAwait(false);
        string dataUrl = DataUrl.FromText("text/css", imported);
        return media.Length == 0 ? $"@import url(\"{dataUrl}\");" : $"@import url(\"{dataUrl}\") {media};";
    }

    private static string FirstValue(Match match, params string[] groups)
    {
        foreach (string group in groups)
        {
            if (match.Groups[group].Success)
            {
                return match.Groups[group].Value;
            }
        }

        return string.Empty;
    }

    private static string Fragment(Uri url) => url.Fragment;

    private static NetworkRequestKind GuessKind(Uri url)
    {
        string extension = Path.GetExtension(url.AbsolutePath).ToLowerInvariant();
        return extension is ".woff" or ".woff2" or ".ttf" or ".otf" or ".eot"
            ? NetworkRequestKind.Font
            : NetworkRequestKind.Image;
    }

    [GeneratedRegex(@"/\*.*?\*/", RegexOptions.Singleline)]
    private static partial Regex CommentPattern();

    // @import "a.css" media; | @import url(a.css) media; | url("x") | url('x') | url(x)
    [GeneratedRegex(
        @"(?<import>@import\s+(?:""(?<idq>[^""]*)""|'(?<isq>[^']*)'|url\(\s*(?:""(?<iudq>[^""]*)""|'(?<iusq>[^']*)'|(?<iubare>[^)""'\s]*))\s*\))(?<media>[^;]*);)"
        + @"|url\(\s*(?:""(?<dq>[^""]*)""|'(?<sq>[^']*)'|(?<bare>[^)""'\s]*))\s*\)",
        RegexOptions.IgnoreCase)]
    private static partial Regex TokenPattern();
}
