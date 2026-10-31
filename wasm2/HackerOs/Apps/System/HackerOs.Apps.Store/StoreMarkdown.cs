using System.Text;

namespace HackerOs.Apps.Store;

/// <summary>Kind of a block of an app's description.</summary>
internal enum StoreMarkdownBlockKind
{
    /// <summary>A paragraph.</summary>
    Paragraph,

    /// <summary>A heading; <see cref="StoreMarkdownBlock.Level"/> is 1 to 3.</summary>
    Heading,

    /// <summary>A bullet list; each item is one entry of <see cref="StoreMarkdownBlock.Items"/>.</summary>
    BulletList,

    /// <summary>A numbered list.</summary>
    NumberedList,

    /// <summary>A fenced code block, shown as it is.</summary>
    Code
}

/// <summary>Kind of an inline piece of text.</summary>
internal enum StoreMarkdownSpanKind
{
    /// <summary>Plain text.</summary>
    Text,

    /// <summary><c>**bold**</c>.</summary>
    Strong,

    /// <summary><c>*italic*</c> or <c>_italic_</c>.</summary>
    Emphasis,

    /// <summary><c>`code`</c>.</summary>
    Code,

    /// <summary><c>[text](https://…)</c>; <see cref="StoreMarkdownSpan.Href"/> is an <c>https</c> address.</summary>
    Link
}

/// <summary>An inline piece of text.</summary>
/// <param name="Kind">Its kind.</param>
/// <param name="Text">Its text, never HTML.</param>
/// <param name="Href">The address of a link.</param>
internal sealed record StoreMarkdownSpan(StoreMarkdownSpanKind Kind, string Text, string? Href = null);

/// <summary>A block of an app's description.</summary>
/// <param name="Kind">Its kind.</param>
/// <param name="Items">Its lines of spans: one for a paragraph or a heading, one per list item.</param>
/// <param name="Level">Heading level.</param>
/// <param name="Code">Text of a code block.</param>
internal sealed record StoreMarkdownBlock(
    StoreMarkdownBlockKind Kind, IReadOnlyList<IReadOnlyList<StoreMarkdownSpan>> Items, int Level = 0, string? Code = null);

/// <summary>
/// Reads the small part of Markdown that an app's <c>description.&lt;lang&gt;.md</c> needs
/// (headings, paragraphs, lists, code, bold, italic, links) into blocks the window draws itself.
/// </summary>
/// <remarks>
/// The description comes from the catalog, written by the app's author: no HTML is ever produced
/// from it. Text is drawn by Blazor, which escapes it; raw HTML in the file shows as text; a link is
/// kept only when it is an absolute <c>https</c> address, and the window opens it outside the OS
/// with <c>rel="noopener noreferrer"</c>.
/// </remarks>
internal static class StoreMarkdown
{
    /// <summary>Reads a description.</summary>
    /// <param name="markdown">The Markdown text.</param>
    public static IReadOnlyList<StoreMarkdownBlock> Parse(string? markdown)
    {
        List<StoreMarkdownBlock> blocks = [];
        string[] lines = (markdown ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        List<string> paragraph = [];
        int index = 0;
        while (index < lines.Length)
        {
            string line = lines[index];
            string trimmed = line.Trim();
            if (trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                Flush(paragraph, blocks);
                StringBuilder code = new();
                index++;
                while (index < lines.Length && !lines[index].Trim().StartsWith("```", StringComparison.Ordinal))
                {
                    code.Append(code.Length == 0 ? string.Empty : "\n").Append(lines[index]);
                    index++;
                }

                blocks.Add(new StoreMarkdownBlock(StoreMarkdownBlockKind.Code, [], Code: code.ToString()));
                index++;
                continue;
            }

            if (trimmed.Length == 0)
            {
                Flush(paragraph, blocks);
                index++;
                continue;
            }

            if (TryHeading(trimmed, out int level, out string heading))
            {
                Flush(paragraph, blocks);
                blocks.Add(new StoreMarkdownBlock(StoreMarkdownBlockKind.Heading, [ParseInline(heading)], level));
                index++;
                continue;
            }

            if (TryListItem(trimmed, out bool numbered, out _))
            {
                Flush(paragraph, blocks);
                List<IReadOnlyList<StoreMarkdownSpan>> items = [];
                while (index < lines.Length && TryListItem(lines[index].Trim(), out bool sameKind, out string item) && sameKind == numbered)
                {
                    items.Add(ParseInline(item));
                    index++;
                }

                blocks.Add(new StoreMarkdownBlock(numbered ? StoreMarkdownBlockKind.NumberedList : StoreMarkdownBlockKind.BulletList, items));
                continue;
            }

            paragraph.Add(trimmed);
            index++;
        }

        Flush(paragraph, blocks);
        return blocks;
    }

    /// <summary>Reads the inline marks of one line.</summary>
    /// <param name="text">The text.</param>
    public static IReadOnlyList<StoreMarkdownSpan> ParseInline(string text)
    {
        List<StoreMarkdownSpan> spans = [];
        StringBuilder plain = new();
        int i = 0;
        while (i < text.Length)
        {
            if (text[i] == '`' && text.IndexOf('`', i + 1) is int codeEnd and > 0)
            {
                Add(spans, plain, new StoreMarkdownSpan(StoreMarkdownSpanKind.Code, text[(i + 1)..codeEnd]));
                i = codeEnd + 1;
            }
            else if (i + 1 < text.Length && text[i] == '*' && text[i + 1] == '*' && text.IndexOf("**", i + 2, StringComparison.Ordinal) is int strongEnd and > 0)
            {
                Add(spans, plain, new StoreMarkdownSpan(StoreMarkdownSpanKind.Strong, text[(i + 2)..strongEnd]));
                i = strongEnd + 2;
            }
            else if (text[i] is '*' or '_'
                && (i == 0 || !char.IsLetterOrDigit(text[i - 1]))
                && text.IndexOf(text[i], i + 1) is int emEnd and > 0
                && emEnd > i + 1)
            {
                Add(spans, plain, new StoreMarkdownSpan(StoreMarkdownSpanKind.Emphasis, text[(i + 1)..emEnd]));
                i = emEnd + 1;
            }
            else if (text[i] == '[' && TryLink(text, i, out string label, out string href, out int linkEnd))
            {
                Add(spans, plain, Uri.TryCreate(href, UriKind.Absolute, out Uri? uri) && uri.Scheme == Uri.UriSchemeHttps
                    ? new StoreMarkdownSpan(StoreMarkdownSpanKind.Link, label, uri.AbsoluteUri)
                    : new StoreMarkdownSpan(StoreMarkdownSpanKind.Text, label));
                i = linkEnd;
            }
            else
            {
                plain.Append(text[i]);
                i++;
            }
        }

        if (plain.Length > 0)
        {
            spans.Add(new StoreMarkdownSpan(StoreMarkdownSpanKind.Text, plain.ToString()));
        }

        return spans;
    }

    private static void Add(List<StoreMarkdownSpan> spans, StringBuilder plain, StoreMarkdownSpan span)
    {
        if (plain.Length > 0)
        {
            spans.Add(new StoreMarkdownSpan(StoreMarkdownSpanKind.Text, plain.ToString()));
            plain.Clear();
        }

        spans.Add(span);
    }

    private static bool TryLink(string text, int start, out string label, out string href, out int end)
    {
        label = href = string.Empty;
        end = start;
        int close = text.IndexOf("](", start + 1, StringComparison.Ordinal);
        int closeParen = close < 0 ? -1 : text.IndexOf(')', close + 2);
        if (close < 0 || closeParen < 0)
        {
            return false;
        }

        label = text[(start + 1)..close];
        href = text[(close + 2)..closeParen].Trim();
        end = closeParen + 1;
        return true;
    }

    private static bool TryHeading(string line, out int level, out string text)
    {
        level = line.TakeWhile(static c => c == '#').Count();
        text = level is >= 1 and <= 6 && line.Length > level && line[level] == ' ' ? line[(level + 1)..].Trim() : string.Empty;
        // h1 and h2 are drawn as the page's level 3; the page has its own title.
        level = Math.Min(level, 3);
        return text.Length > 0;
    }

    private static bool TryListItem(string line, out bool numbered, out string text)
    {
        numbered = false;
        text = string.Empty;
        if (line.Length > 2 && line[0] is '-' or '*' or '+' && line[1] == ' ')
        {
            text = line[2..].Trim();
            return true;
        }

        int digits = line.TakeWhile(char.IsAsciiDigit).Count();
        if (digits is > 0 and < 4 && line.Length > digits + 2 && line[digits] is '.' or ')' && line[digits + 1] == ' ')
        {
            numbered = true;
            text = line[(digits + 2)..].Trim();
            return true;
        }

        return false;
    }

    private static void Flush(List<string> paragraph, List<StoreMarkdownBlock> blocks)
    {
        if (paragraph.Count > 0)
        {
            blocks.Add(new StoreMarkdownBlock(StoreMarkdownBlockKind.Paragraph, [ParseInline(string.Join(' ', paragraph))]));
            paragraph.Clear();
        }
    }
}
