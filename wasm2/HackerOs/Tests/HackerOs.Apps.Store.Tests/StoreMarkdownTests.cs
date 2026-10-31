using Xunit;

namespace HackerOs.Apps.Store.Tests;

public sealed class StoreMarkdownTests
{
    [Fact]
    public void Blocks_are_headings_paragraphs_lists_and_code()
    {
        IReadOnlyList<StoreMarkdownBlock> blocks = StoreMarkdown.Parse(
            "# Notes\n\nA quick\nnote app.\n\n- one\n- two\n\n1. first\n2. second\n\n```\n<b>raw</b>\n```");

        Assert.Equal(
            [StoreMarkdownBlockKind.Heading, StoreMarkdownBlockKind.Paragraph, StoreMarkdownBlockKind.BulletList,
             StoreMarkdownBlockKind.NumberedList, StoreMarkdownBlockKind.Code],
            blocks.Select(block => block.Kind));
        Assert.Equal("A quick note app.", Assert.Single(blocks[1].Items[0]).Text);
        Assert.Equal(2, blocks[2].Items.Count);
        Assert.Equal("<b>raw</b>", blocks[4].Code);
    }

    [Fact]
    public void Inline_marks_are_read()
    {
        IReadOnlyList<StoreMarkdownSpan> spans = StoreMarkdown.ParseInline("a **b** *c* `d`");

        Assert.Equal(
            [StoreMarkdownSpanKind.Text, StoreMarkdownSpanKind.Strong, StoreMarkdownSpanKind.Text,
             StoreMarkdownSpanKind.Emphasis, StoreMarkdownSpanKind.Text, StoreMarkdownSpanKind.Code],
            spans.Select(span => span.Kind));
    }

    [Fact]
    public void Only_https_links_are_kept()
    {
        IReadOnlyList<StoreMarkdownSpan> spans = StoreMarkdown.ParseInline(
            "[site](https://example.org/a) [bad](javascript:alert(1)) [plain](http://example.org)");

        StoreMarkdownSpan link = Assert.Single(spans, span => span.Kind == StoreMarkdownSpanKind.Link);
        Assert.Equal("https://example.org/a", link.Href);
        Assert.DoesNotContain(spans, span => span.Href?.StartsWith("javascript", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void Raw_html_stays_text()
    {
        StoreMarkdownSpan span = Assert.Single(StoreMarkdown.ParseInline("<script>x</script>"));

        Assert.Equal(StoreMarkdownSpanKind.Text, span.Kind);
        Assert.Equal("<script>x</script>", span.Text);
    }
}
