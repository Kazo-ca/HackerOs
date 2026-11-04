using Kazo.Os.AppSdk.IO;

namespace Kazo.Os.Apps.Browser;

/// <summary>
/// Downloads of the browser (ADR 0075 §5: a type the browser cannot show goes to <c>~/Downloads</c>):
/// the file name a real browser would pick, made unique the way Chromium does (<c>file (1).zip</c>).
/// </summary>
public static class BrowserDownloads
{
    /// <summary>Gets the download folder of the current user, <c>~/Downloads</c>.</summary>
    public static string GetFolder(string homeDirectory) => $"{homeDirectory.TrimEnd('/')}/Downloads";

    /// <summary>
    /// Picks the file name: the <c>filename</c> of <c>Content-Disposition</c>, else the last segment
    /// of the URL path, else <c>download</c>. Only the last path segment is kept, without control characters.
    /// </summary>
    public static string SuggestFileName(Uri url, string? contentDisposition)
    {
        ArgumentNullException.ThrowIfNull(url);
        string? name = null;
        foreach (string parameter in (contentDisposition ?? string.Empty).Split(';').Skip(1))
        {
            int equals = parameter.IndexOf('=');
            if (equals > 0 && parameter[..equals].Trim().Equals("filename", StringComparison.OrdinalIgnoreCase))
            {
                name = parameter[(equals + 1)..].Trim().Trim('"');
            }
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            string segment = Uri.UnescapeDataString(url.AbsolutePath.TrimEnd('/'));
            name = segment[(segment.LastIndexOf('/') + 1)..];
        }

        // Only the last segment counts: a server cannot write outside the download folder.
        name ??= string.Empty;
        name = name[(name.LastIndexOfAny(['/', '\\']) + 1)..];
        string cleaned = new([.. name.Where(character => character is not ('/' or '\\') && !char.IsControl(character))]);
        cleaned = cleaned.Trim().Trim('.');
        return cleaned.Length == 0 ? "download" : cleaned;
    }

    /// <summary>Returns <paramref name="fileName"/>, or <c>name (n).ext</c> for the first free <c>n</c>.</summary>
    public static async Task<string> GetUniquePathAsync(string folder, string fileName, Func<string, Task<bool>> exists)
    {
        ArgumentNullException.ThrowIfNull(exists);
        string candidate = $"{folder.TrimEnd('/')}/{fileName}";
        if (!await exists(candidate).ConfigureAwait(false))
        {
            return candidate;
        }

        int dot = fileName.LastIndexOf('.');
        string stem = dot > 0 ? fileName[..dot] : fileName;
        string extension = dot > 0 ? fileName[dot..] : string.Empty;
        for (int index = 1; ; index++)
        {
            candidate = $"{folder.TrimEnd('/')}/{stem} ({index}){extension}";
            if (!await exists(candidate).ConfigureAwait(false))
            {
                return candidate;
            }
        }
    }

    /// <summary>Saves <paramref name="bytes"/> in the download folder and returns the path written.</summary>
    public static async Task<string> SaveAsync(string homeDirectory, string fileName, byte[] bytes, CancellationToken cancellationToken = default)
    {
        string folder = GetFolder(homeDirectory);
        await KazoDirectory.CreateDirectoryAsync(folder, cancellationToken).ConfigureAwait(false);
        string path = await GetUniquePathAsync(folder, fileName, candidate => KazoFile.ExistsAsync(candidate, cancellationToken)).ConfigureAwait(false);
        await KazoFile.WriteAllBytesAsync(path, bytes, cancellationToken).ConfigureAwait(false);
        return path;
    }
}
