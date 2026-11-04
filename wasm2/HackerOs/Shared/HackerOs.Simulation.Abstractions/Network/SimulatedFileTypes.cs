using System.Collections.Frozen;

namespace HackerOs.Simulation.Abstractions.Network;

/// <summary>
/// Maps a file name to the <c>Content-Type</c> a web server or a browser gives it, like the
/// <c>mime.types</c> table of a Linux system or ASP.NET Core's <c>FileExtensionContentTypeProvider</c>
/// (ADR 0075 §4 for <c>file://</c> pages; ADR 0077 for the static sites of <c>hackwebd</c>).
/// </summary>
public static class SimulatedFileTypes
{
    // Text types carry "charset=utf-8": Kazo OS writes text files as UTF-8.
    private static readonly FrozenDictionary<string, string> ContentTypes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        [".html"] = "text/html; charset=utf-8",
        [".htm"] = "text/html; charset=utf-8",
        [".css"] = "text/css; charset=utf-8",
        [".js"] = "text/javascript; charset=utf-8",
        [".mjs"] = "text/javascript; charset=utf-8",
        [".json"] = "application/json; charset=utf-8",
        [".map"] = "application/json; charset=utf-8",
        [".txt"] = "text/plain; charset=utf-8",
        [".log"] = "text/plain; charset=utf-8",
        [".md"] = "text/markdown; charset=utf-8",
        [".csv"] = "text/csv; charset=utf-8",
        [".xml"] = "application/xml; charset=utf-8",
        [".svg"] = "image/svg+xml",
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".gif"] = "image/gif",
        [".webp"] = "image/webp",
        [".bmp"] = "image/bmp",
        [".ico"] = "image/x-icon",
        [".avif"] = "image/avif",
        [".woff"] = "font/woff",
        [".woff2"] = "font/woff2",
        [".ttf"] = "font/ttf",
        [".otf"] = "font/otf",
        [".mp3"] = "audio/mpeg",
        [".wav"] = "audio/wav",
        [".ogg"] = "audio/ogg",
        [".mp4"] = "video/mp4",
        [".webm"] = "video/webm",
        [".pdf"] = "application/pdf",
        [".wasm"] = "application/wasm",
        [".zip"] = "application/zip",
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets the <c>Content-Type</c> of a file from its extension; <see cref="SimulatedMediaTypes.OctetStream"/>
    /// when the extension is unknown or missing, as Apache and IIS answer.
    /// </summary>
    /// <param name="fileName">A file name or path; only its extension is read.</param>
    public static string GetContentType(string? fileName)
    {
        if (string.IsNullOrEmpty(fileName))
        {
            return SimulatedMediaTypes.OctetStream;
        }

        int slash = fileName.LastIndexOf('/');
        int dot = fileName.LastIndexOf('.');
        if (dot <= slash + 1)
        {
            // No extension, or a dot file such as ".bashrc".
            return SimulatedMediaTypes.OctetStream;
        }

        return ContentTypes.TryGetValue(fileName[dot..], out string? contentType)
            ? contentType
            : SimulatedMediaTypes.OctetStream;
    }
}
