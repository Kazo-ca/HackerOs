using System.Text;
using Kazo.Os.AppSdk.IO;

namespace Kazo.Os.AppSdk.Tests.IO;

public sealed class HackTextContentTests
{
    [Fact]
    public void Empty_content_is_text()
    {
        // "New File" creates an empty file tagged binary: editors must still open it.
        Assert.True(HackTextContent.TryDecode([], out string text));
        Assert.Equal(string.Empty, text);
    }

    [Fact]
    public void Utf8_with_bom_is_text_without_the_bom()
    {
        byte[] bytes = [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("héllo")];

        Assert.True(HackTextContent.TryDecode(bytes, out string text));
        Assert.Equal("héllo", text);
    }

    [Theory]
    [InlineData(new byte[] { 0x48, 0x00, 0x49 })]
    [InlineData(new byte[] { 0xFF, 0xFE, 0xFD })]
    [InlineData(new byte[] { 0x01, 0x02, 0x03 })]
    public void Control_bytes_or_invalid_utf8_are_binary(byte[] bytes)
    {
        Assert.False(HackTextContent.TryDecode(bytes, out string text));
        Assert.Equal(string.Empty, text);
    }

    [Fact]
    public async Task TryReadAsync_reads_the_stream_to_its_end()
    {
        using MemoryStream stream = new(Encoding.UTF8.GetBytes("line 1\n\tline 2\r\n"));

        Assert.Equal("line 1\n\tline 2\r\n", await HackTextContent.TryReadAsync(stream));
    }
}
