using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.Components;

namespace Kazo.Os.Apps.Diagnostic;

/// <summary>
/// Builds a sentence of translated text with inline markup (<c>&lt;code&gt;</c>, <c>&lt;strong&gt;</c>)
/// at its <c>{0}</c>, <c>{1}</c> placeholders, so a translation can move a path or a command inside
/// the sentence while the resource itself stays plain text (ADR 0104).
/// </summary>
/// <remarks>
/// The template and every plain argument are HTML-encoded; only the fragments built by
/// <see cref="Code"/> and <see cref="Strong"/> (themselves encoded) are inserted as markup. Non-ASCII
/// letters are kept as they are, so a French sentence stays readable in the rendered HTML.
/// </remarks>
internal static class LocalizedMarkup
{
    /// <summary>Formats <paramref name="template"/> with <paramref name="args"/>, encoding everything but markup fragments.</summary>
    /// <param name="template">A translated text with <c>{n}</c> placeholders.</param>
    /// <param name="args">Plain values (encoded) or <see cref="MarkupString"/> fragments (kept).</param>
    public static MarkupString Format(string template, params object?[] args)
    {
        object[] parts = [.. args.Select(static arg => arg is MarkupString markup
            ? markup.Value
            : Encode(Convert.ToString(arg, CultureInfo.InvariantCulture) ?? string.Empty))];
        return new MarkupString(string.Format(CultureInfo.InvariantCulture, Encode(template), parts));
    }

    /// <summary>Wraps <paramref name="text"/> (a path, a command) in <c>&lt;code&gt;</c>.</summary>
    public static MarkupString Code(string text) => new($"<code>{Encode(text)}</code>");

    /// <summary>Wraps <paramref name="text"/> in <c>&lt;strong&gt;</c>.</summary>
    public static MarkupString Strong(string text) => new($"<strong>{Encode(text)}</strong>");

    /// <summary>Encodes the five characters that matter in HTML text and attributes.</summary>
    private static string Encode(string text)
    {
        StringBuilder builder = new(text.Length);
        foreach (char character in text)
        {
            switch (character)
            {
                case '&': builder.Append("&amp;"); break;
                case '<': builder.Append("&lt;"); break;
                case '>': builder.Append("&gt;"); break;
                case '"': builder.Append("&quot;"); break;
                case '\'': builder.Append("&#39;"); break;
                default: builder.Append(character); break;
            }
        }

        return builder.ToString();
    }
}
