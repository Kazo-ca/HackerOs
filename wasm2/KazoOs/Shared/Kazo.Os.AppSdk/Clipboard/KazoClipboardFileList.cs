using System.Text;

namespace Kazo.Os.AppSdk.Clipboard;

/// <summary>Whether the files on the clipboard were copied or cut (ADR 0056, section 12).</summary>
public enum KazoClipboardFileOperation
{
    /// <summary>Paste duplicates the files; the clipboard keeps them for another paste.</summary>
    Copy = 0,

    /// <summary>Paste moves the files; nothing is removed before the paste, and the clipboard is emptied after it.</summary>
    Cut = 1
}

/// <summary>
/// A file list on the clipboard, modelled on Windows Forms
/// <c>Clipboard.SetFileDropList</c> and serialized like GNOME's <c>x-special/gnome-copied-files</c>:
/// a first line <c>copy</c> or <c>cut</c>, then one <c>file://</c> URI per line.
/// </summary>
/// <param name="Operation">Copy or cut.</param>
/// <param name="Paths">Absolute virtual paths, in the order they were selected.</param>
public sealed record KazoClipboardFileList(KazoClipboardFileOperation Operation, IReadOnlyList<string> Paths)
{
    /// <summary>The MIME type of the serialized form, as GNOME Files offers it.</summary>
    public const string GnomeCopiedFilesMediaType = "x-special/gnome-copied-files";

    /// <summary>Serializes the list in the <c>x-special/gnome-copied-files</c> format.</summary>
    public string ToGnomeCopiedFiles()
    {
        StringBuilder builder = new(Operation == KazoClipboardFileOperation.Cut ? "cut" : "copy");
        foreach (string path in Paths)
        {
            builder.Append('\n').Append(ToFileUri(path));
        }

        return builder.ToString();
    }

    /// <summary>Gets the paths one per line, the <c>text/plain</c> form an app reading text sees.</summary>
    public string ToText() => string.Join('\n', Paths);

    /// <summary>
    /// Parses the <c>x-special/gnome-copied-files</c> format. Returns <see langword="null"/> when the
    /// first line is neither <c>copy</c> nor <c>cut</c>, or a line is not an absolute <c>file://</c> URI.
    /// </summary>
    /// <param name="content">Serialized content.</param>
    public static KazoClipboardFileList? TryParseGnomeCopiedFiles(string? content)
    {
        if (string.IsNullOrEmpty(content))
        {
            return null;
        }

        string[] lines = content.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        KazoClipboardFileOperation operation;
        switch (lines[0])
        {
            case "copy":
                operation = KazoClipboardFileOperation.Copy;
                break;
            case "cut":
                operation = KazoClipboardFileOperation.Cut;
                break;
            default:
                return null;
        }

        List<string> paths = [];
        foreach (string line in lines.Skip(1))
        {
            if (!line.StartsWith("file:///", StringComparison.Ordinal))
            {
                return null;
            }

            paths.Add(Uri.UnescapeDataString(line["file://".Length..]));
        }

        return new KazoClipboardFileList(operation, paths);
    }

    /// <summary>Encodes one absolute path as a <c>file://</c> URI, escaping each segment like GLib.</summary>
    private static string ToFileUri(string path) =>
        "file://" + string.Join('/', path.Split('/').Select(Uri.EscapeDataString));
}
