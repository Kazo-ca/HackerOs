using System.Text.Encodings.Web;
using System.Text.Unicode;

namespace Kazo.Os.AppSdk.Web.Views;

/// <summary>
/// HTML that a <c>.cshtml</c> page writes as is, without encoding (ASP.NET's <c>IHtmlContent</c>, ADR 0086 §3):
/// what <c>Html.Raw</c>, <c>RenderBody()</c>, <c>RenderSection</c> and the HTML helpers return.
/// </summary>
public interface IHackHtmlContent
{
    /// <summary>Writes the HTML.</summary>
    /// <param name="writer">The page output.</param>
    void WriteTo(TextWriter writer);
}

/// <summary>An HTML string written without encoding (ASP.NET's <c>HtmlString</c>).</summary>
/// <param name="value">The HTML; <see langword="null"/> writes nothing.</param>
public sealed class HackHtmlString(string? value) : IHackHtmlContent
{
    /// <summary>The empty HTML.</summary>
    public static HackHtmlString Empty { get; } = new(string.Empty);

    /// <summary>Gets the HTML.</summary>
    public string Value { get; } = value ?? string.Empty;

    /// <inheritdoc />
    public void WriteTo(TextWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.Write(Value);
    }

    /// <inheritdoc />
    public override string ToString() => Value;
}

/// <summary>
/// The result of an inline template (<c>@&lt;b&gt;@item&lt;/b&gt;</c>, ASP.NET's <c>HelperResult</c>): the
/// compiler generates a call to <c>HelperResult</c>, which the site's compatibility file derives from
/// this class (ADR 0086 §2).
/// </summary>
/// <param name="asyncAction">Writes the template to the given writer.</param>
public class HackHelperResult(Func<TextWriter, Task> asyncAction) : IHackHtmlContent
{
    private readonly Func<TextWriter, Task> _asyncAction = asyncAction ?? throw new ArgumentNullException(nameof(asyncAction));

    /// <inheritdoc />
    /// <remarks>Synchronous as in ASP.NET: a template only writes, it does not wait on anything.</remarks>
    public void WriteTo(TextWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        _asyncAction(writer).GetAwaiter().GetResult();
    }

    /// <inheritdoc />
    public override string ToString()
    {
        using StringWriter writer = new();
        WriteTo(writer);
        return writer.ToString();
    }
}

/// <summary>The HTML encoding of the <c>.cshtml</c> pages.</summary>
internal static class HackHtml
{
    /// <summary>
    /// Encodes <c>&lt; &gt; &amp; " '</c> and control characters but keeps every letter readable (an
    /// <c>é</c> stays <c>é</c>): pages are UTF-8, and the OS is used in French.
    /// </summary>
    public static HtmlEncoder Encoder { get; } = HtmlEncoder.Create(UnicodeRanges.All);

    /// <summary>Encodes a value for HTML text or an attribute value.</summary>
    public static string Encode(string? value) => string.IsNullOrEmpty(value) ? string.Empty : Encoder.Encode(value);

    /// <summary>Writes a value as a page writes it: HTML content as is, anything else encoded.</summary>
    public static void Write(TextWriter writer, object? value)
    {
        switch (value)
        {
            case null:
                return;
            case IHackHtmlContent html:
                html.WriteTo(writer);
                return;
            case Microsoft.AspNetCore.Components.MarkupString markup:
                writer.Write(markup.Value);
                return;
            default:
                writer.Write(Encode(Convert.ToString(value, System.Globalization.CultureInfo.CurrentCulture)));
                return;
        }
    }
}
