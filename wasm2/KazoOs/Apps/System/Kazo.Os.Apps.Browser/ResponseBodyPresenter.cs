using System.Text;
using System.Text.Json;
using System.Text.Unicode;
using Kazo.Os.Simulation.Abstractions.Network;

namespace Kazo.Os.Apps.Browser;

/// <summary>How the browser shows a response body that is not a structured simulated page.</summary>
public enum ResponseBodyKind
{
    /// <summary>No bytes: nothing to show but the status.</summary>
    Empty,

    /// <summary>Text shown as is in a monospace view (<c>text/plain</c>, CSS, JavaScript, XML…).</summary>
    Text,

    /// <summary>JSON, pretty-printed as Chromium's JSON viewer does.</summary>
    Json,

    /// <summary>
    /// HTML. The browser renders it in its isolated frame (ADR 0075); this view is its source, shown
    /// for <c>view-source:</c> and the "source" button.
    /// </summary>
    Html,

    /// <summary>An image the browser can draw (<c>image/*</c>).</summary>
    Image,

    /// <summary>A PDF document, shown by the host browser's PDF viewer (ADR 0075 §5).</summary>
    Pdf,

    /// <summary>Anything else: a file the browser offers no view for, saved to <c>~/Downloads</c>.</summary>
    Binary
}

/// <summary>
/// A response body ready for display (ADR 0074, section 1): what kind it is and the text or data
/// URL to show. Built by <see cref="ResponseBodyPresenter.Present"/>, pure so it is unit-tested
/// without rendering.
/// </summary>
/// <param name="Kind">How to show it.</param>
/// <param name="MediaType">The <c>type/subtype</c> of the body, lower case.</param>
/// <param name="Length">Size in bytes.</param>
/// <param name="Text">Decoded (and for JSON, indented) text for the text kinds; empty otherwise.</param>
/// <param name="DataUrl">A <c>data:</c> URL of the bytes for <see cref="ResponseBodyKind.Image"/> and <see cref="ResponseBodyKind.Pdf"/>; empty otherwise.</param>
public sealed record ResponseBodyView(ResponseBodyKind Kind, string MediaType, int Length, string Text, string DataUrl);

/// <summary>Chooses how the browser shows a body from its <c>Content-Type</c>, like a real browser.</summary>
public static class ResponseBodyPresenter
{
    /// <summary>Builds the view of a body.</summary>
    /// <param name="contentType">The response's <c>Content-Type</c>, or <see langword="null"/>.</param>
    /// <param name="content">The body bytes.</param>
    public static ResponseBodyView Present(string? contentType, byte[] content)
    {
        ArgumentNullException.ThrowIfNull(content);
        string mediaType = SimulatedMediaTypes.GetMediaType(contentType);
        if (content.Length == 0)
        {
            return new(ResponseBodyKind.Empty, mediaType, 0, string.Empty, string.Empty);
        }

        // A body with no type is sniffed as text when it decodes cleanly, as browsers do.
        if (mediaType.Length == 0)
        {
            mediaType = LooksLikeText(content) ? SimulatedMediaTypes.TextPlain : SimulatedMediaTypes.OctetStream;
        }

        if (mediaType.StartsWith("image/", StringComparison.Ordinal))
        {
            return new(ResponseBodyKind.Image, mediaType, content.Length, string.Empty,
                $"data:{mediaType};base64,{Convert.ToBase64String(content)}");
        }

        if (mediaType == "application/pdf")
        {
            return new(ResponseBodyKind.Pdf, mediaType, content.Length, string.Empty,
                $"data:application/pdf;base64,{Convert.ToBase64String(content)}");
        }

        if (!SimulatedMediaTypes.IsText(mediaType))
        {
            return new(ResponseBodyKind.Binary, mediaType, content.Length, string.Empty, string.Empty);
        }

        string text = SimulatedMediaTypes.GetEncoding(contentType).GetString(content);
        if (SimulatedMediaTypes.IsJson(mediaType))
        {
            return new(ResponseBodyKind.Json, mediaType, content.Length, Indent(text), string.Empty);
        }

        ResponseBodyKind kind = mediaType == SimulatedMediaTypes.TextHtml ? ResponseBodyKind.Html : ResponseBodyKind.Text;
        return new(kind, mediaType, content.Length, text, string.Empty);
    }

    /// <summary>Indents JSON; text that is not valid JSON is shown unchanged.</summary>
    private static string Indent(string json)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            using MemoryStream buffer = new();
            using (Utf8JsonWriter writer = new(buffer, new JsonWriterOptions { Indented = true }))
            {
                document.WriteTo(writer);
            }

            return Encoding.UTF8.GetString(buffer.ToArray());
        }
        catch (JsonException)
        {
            return json;
        }
    }

    private static bool LooksLikeText(byte[] content)
    {
        ReadOnlySpan<byte> sample = content.AsSpan(0, Math.Min(content.Length, 512));
        return !sample.Contains((byte)0) && Utf8.IsValid(sample);
    }
}
