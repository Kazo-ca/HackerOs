using System.Text;

namespace HackerOs.AppSdk.IO;

/// <summary>
/// Decides whether a file's bytes are text, the way Linux tools do (<c>file</c>, <c>grep</c>,
/// gedit): from the content itself, never from how the file was tagged when written.
/// </summary>
/// <remarks>
/// A Kazo OS file records a content descriptor (text or binary) from its last write, but an empty
/// file made by "New File", or a text file copied with <see cref="HackFile.WriteAllBytesAsync(string, byte[], CancellationToken)"/>,
/// carries a binary descriptor. Editors call <see cref="TryDecode"/> on the bytes instead.
/// </remarks>
public static class HackTextContent
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>
    /// Decodes <paramref name="bytes"/> when they look like a text file: strict UTF-8 (a leading
    /// byte order mark is dropped) with no control byte other than tab, line feed, carriage return,
    /// form feed, vertical tab, backspace and escape (the bytes <c>file</c> accepts in text). An
    /// empty file is text.
    /// </summary>
    /// <param name="bytes">The whole file content.</param>
    /// <param name="text">The decoded text, or an empty string when the content is binary.</param>
    /// <returns><see langword="true"/> when the content is text.</returns>
    public static bool TryDecode(ReadOnlySpan<byte> bytes, out string text)
    {
        text = string.Empty;
        foreach (byte value in bytes)
        {
            if (value < 0x20 && value is not ((byte)'\t' or (byte)'\n' or (byte)'\r' or 0x0C or 0x0B or 0x08 or 0x1B))
            {
                return false;
            }
        }

        if (bytes.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
        {
            bytes = bytes[3..];
        }

        try
        {
            text = StrictUtf8.GetString(bytes);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    /// <summary>Reads <paramref name="content"/> to its end and decodes it with <see cref="TryDecode"/>.</summary>
    /// <param name="content">The file's content stream; it is not disposed.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The text, or <see langword="null"/> when the content is binary.</returns>
    public static async ValueTask<string?> TryReadAsync(Stream content, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        using MemoryStream buffer = new();
        await content.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        return TryDecode(buffer.GetBuffer().AsSpan(0, (int)buffer.Length), out string text) ? text : null;
    }
}
