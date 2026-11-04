using System.Net;
using System.Text;
using Kazo.Os.AppSdk.Web.Http;

namespace Kazo.Os.AppSdk.Web.Diagnostics;

/// <summary>
/// The detailed error page of <c>UseExceptionPage</c> (ASP.NET's developer exception page): the
/// exception chain with stacks, the request line, query, headers, cookies and route values, in the
/// Kazo OS console style. Content negotiation as ASP.NET: a client that does not accept HTML gets text.
/// </summary>
internal static class KazoExceptionPage
{
    /// <summary>Writes the page for <paramref name="exception"/> into the (cleared) response.</summary>
    public static void Write(KazoHttpContext context, Exception exception)
    {
        KazoHttpResponse response = context.Response;
        response.StatusCode = 500;
        string accept = context.Request.Headers["Accept"] ?? string.Empty;
        if (!accept.Contains("text/html", StringComparison.OrdinalIgnoreCase))
        {
            response.ContentType = "text/plain; charset=utf-8";
            response.Body.Write(KazoBodyEncoding.Utf8.GetBytes(
                $"{exception}\n\nHEADERS\n=======\n{string.Join('\n', context.Request.Headers.Select(header => $"{header.Key}: {string.Join(", ", header.Value)}"))}\n"));
            return;
        }

        response.ContentType = "text/html; charset=utf-8";
        StringBuilder html = new();
        html.Append("<!DOCTYPE html><html lang=\"en\"><head><meta charset=\"utf-8\"><title>Internal Server Error</title><style>")
            .Append("body{margin:0;background:#07090d;color:#c9d1d9;font:14px/1.5 'Cascadia Code','Fira Code',Consolas,monospace}")
            .Append("header{padding:24px 32px;border-bottom:1px solid #1f6f43;background:linear-gradient(90deg,#0d1f14,#07090d)}")
            .Append("h1{margin:0;color:#ff5f56;font-size:20px}h2{color:#39d353;font-size:15px;margin:24px 0 8px}")
            .Append("main{padding:8px 32px 32px}pre{white-space:pre-wrap;background:#0d1117;border:1px solid #21262d;padding:12px;border-radius:6px;color:#e6edf3}")
            .Append("table{border-collapse:collapse;width:100%}td{border-bottom:1px solid #21262d;padding:4px 8px;vertical-align:top}")
            .Append("td:first-child{color:#58a6ff;width:30%}.muted{color:#8b949e}")
            .Append("</style></head><body><header><h1>An unhandled exception occurred while processing the request.</h1>")
            .Append("<div class=\"muted\">").Append(Encode(context.Request.Method)).Append(' ').Append(Encode(context.Request.Url.ToString()))
            .Append(" &middot; ").Append(Encode(context.Endpoint?.DisplayName ?? "no endpoint"))
            .Append(" &middot; trace ").Append(Encode(context.TraceIdentifier)).Append("</div></header><main>");

        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            html.Append("<h2>").Append(Encode(current.GetType().FullName ?? current.GetType().Name)).Append(": ")
                .Append(Encode(current.Message)).Append("</h2><pre>").Append(Encode(current.StackTrace ?? "(no stack trace)")).Append("</pre>");
        }

        AppendTable(html, "Query", context.Request.Query.Select(field => (field.Key, string.Join(", ", field.Value))));
        AppendTable(html, "Cookies", context.Request.Cookies.Select(cookie => (cookie.Key, cookie.Value)));
        AppendTable(html, "Headers", context.Request.Headers.Select(header => (header.Key, string.Join(", ", header.Value))));
        AppendTable(html, "Routing", context.Request.RouteValues.Select(value => (value.Key, Routing.KazoRouteValueDictionary.FormatValue(value.Value) ?? "null")));
        html.Append("</main></body></html>");
        response.Body.Write(KazoBodyEncoding.Utf8.GetBytes(html.ToString()));
    }

    private static void AppendTable(StringBuilder html, string title, IEnumerable<(string Name, string Value)> rows)
    {
        html.Append("<h2>").Append(title).Append("</h2>");
        (string Name, string Value)[] items = [.. rows];
        if (items.Length == 0)
        {
            html.Append("<p class=\"muted\">none</p>");
            return;
        }

        html.Append("<table>");
        foreach ((string name, string value) in items)
        {
            html.Append("<tr><td>").Append(Encode(name)).Append("</td><td>").Append(Encode(value)).Append("</td></tr>");
        }

        html.Append("</table>");
    }

    private static string Encode(string text) => WebUtility.HtmlEncode(text);
}
