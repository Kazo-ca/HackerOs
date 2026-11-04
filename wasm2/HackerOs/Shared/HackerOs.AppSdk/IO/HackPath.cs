using System.Diagnostics.CodeAnalysis;
using System.Text;
using HackerOs.App.Abstractions;

namespace HackerOs.AppSdk.IO;

/// <summary>
/// Path helpers for the KazoOS virtual filesystem, shaped like <see cref="System.IO.Path"/>
/// but always Linux-style (<c>/</c> separator, case-sensitive, no drive letters) whatever the
/// host OS running the WebAssembly runtime (ADR 0043).
/// </summary>
/// <remarks>
/// Every member is synchronous and pure except <see cref="GetFullPath(string)"/>, which reads the
/// ambient <see cref="HackExecutionContext"/> to expand <c>~</c> and resolve relative paths.
/// </remarks>
public static class HackPath
{
    /// <summary>The only directory separator of the virtual filesystem.</summary>
    public const char DirectorySeparatorChar = '/';

    /// <summary>Same as <see cref="DirectorySeparatorChar"/>: the virtual filesystem has no alternate separator.</summary>
    public const char AltDirectorySeparatorChar = '/';

    /// <summary>Separator used between entries of a <c>PATH</c>-like list.</summary>
    public const char PathSeparator = ':';

    /// <summary>Combines path segments; a rooted segment discards everything before it (like <see cref="Path.Combine(string[])"/>).</summary>
    /// <param name="paths">Segments to combine.</param>
    /// <returns>The combined path, not normalized.</returns>
    public static string Combine(params string[] paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        StringBuilder builder = new();
        foreach (string segment in paths)
        {
            ArgumentNullException.ThrowIfNull(segment, nameof(paths));
            if (segment.Length == 0)
            {
                continue;
            }

            if (IsPathRooted(segment))
            {
                builder.Clear();
            }
            else if (builder.Length > 0 && builder[^1] != DirectorySeparatorChar)
            {
                builder.Append(DirectorySeparatorChar);
            }

            builder.Append(segment);
        }

        return builder.ToString();
    }

    /// <summary>Concatenates segments with one separator between them; rooted segments do not reset (like <see cref="Path.Join(string?, string?)"/>).</summary>
    /// <param name="paths">Segments to join; <see langword="null"/> or empty segments are skipped.</param>
    /// <returns>The joined path, not normalized.</returns>
    public static string Join(params string?[] paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        StringBuilder builder = new();
        foreach (string? segment in paths)
        {
            if (string.IsNullOrEmpty(segment))
            {
                continue;
            }

            if (builder.Length > 0 && builder[^1] != DirectorySeparatorChar && segment[0] != DirectorySeparatorChar)
            {
                builder.Append(DirectorySeparatorChar);
            }

            builder.Append(segment);
        }

        return builder.ToString();
    }

    /// <summary>Returns the last segment of <paramref name="path"/> (everything after the last <c>/</c>).</summary>
    [return: NotNullIfNotNull(nameof(path))]
    public static string? GetFileName(string? path)
    {
        if (path is null)
        {
            return null;
        }

        int separator = path.LastIndexOf(DirectorySeparatorChar);
        return separator < 0 ? path : path[(separator + 1)..];
    }

    /// <summary>Returns the file name without its extension.</summary>
    [return: NotNullIfNotNull(nameof(path))]
    public static string? GetFileNameWithoutExtension(string? path)
    {
        string? fileName = GetFileName(path);
        if (fileName is null)
        {
            return null;
        }

        int dot = fileName.LastIndexOf('.');
        return dot < 0 ? fileName : fileName[..dot];
    }

    /// <summary>Returns the extension, including its leading dot, or an empty string when there is none.</summary>
    [return: NotNullIfNotNull(nameof(path))]
    public static string? GetExtension(string? path)
    {
        string? fileName = GetFileName(path);
        if (fileName is null)
        {
            return null;
        }

        int dot = fileName.LastIndexOf('.');
        return dot < 0 || dot == fileName.Length - 1 ? string.Empty : fileName[dot..];
    }

    /// <summary>Returns whether the file name has a non-empty extension.</summary>
    public static bool HasExtension(string? path) => !string.IsNullOrEmpty(GetExtension(path));

    /// <summary>Replaces (or removes, when <paramref name="extension"/> is <see langword="null"/>) the extension.</summary>
    [return: NotNullIfNotNull(nameof(path))]
    public static string? ChangeExtension(string? path, string? extension)
    {
        if (path is null)
        {
            return null;
        }

        int separator = path.LastIndexOf(DirectorySeparatorChar);
        int dot = path.LastIndexOf('.');
        string stem = dot > separator ? path[..dot] : path;
        if (extension is null)
        {
            return stem;
        }

        if (extension.Length == 0)
        {
            return stem + ".";
        }

        return extension[0] == '.' ? stem + extension : stem + "." + extension;
    }

    /// <summary>
    /// Returns the parent directory of <paramref name="path"/> like <see cref="Path.GetDirectoryName(string?)"/>:
    /// <c>/a</c> for <c>/a/b</c>, <c>/</c> for <c>/a</c>, <see langword="null"/> for <c>/</c> or an
    /// empty path, and an empty string for a bare name.
    /// </summary>
    public static string? GetDirectoryName(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        int separator = path.LastIndexOf(DirectorySeparatorChar);
        if (separator < 0)
        {
            return string.Empty;
        }

        if (separator == 0)
        {
            return path.Length == 1 ? null : "/";
        }

        string parent = path[..separator].TrimEnd(DirectorySeparatorChar);
        return parent.Length == 0 ? "/" : parent;
    }

    /// <summary>Returns whether <paramref name="path"/> starts at the root (<c>/</c>).</summary>
    public static bool IsPathRooted([NotNullWhen(true)] string? path) =>
        !string.IsNullOrEmpty(path) && path[0] == DirectorySeparatorChar;

    /// <summary>
    /// Resolves <paramref name="path"/> to a canonical absolute path: <c>~</c> and <c>~/…</c> expand
    /// to the user's home directory, relative paths are resolved against
    /// <see cref="HackExecutionContext.WorkingDirectory"/>, and <c>.</c>/<c>..</c> are collapsed.
    /// </summary>
    /// <exception cref="ArgumentException">The path is empty or malformed.</exception>
    /// <exception cref="HackExecutionContextMissingException">The path is relative or uses <c>~</c> and no app context is active.</exception>
    public static string GetFullPath(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        ThrowIfEmpty(path, nameof(path));

        if (path == "~" || path.StartsWith("~/", StringComparison.Ordinal))
        {
            string home = HackExecutionContext.Current.HomeDirectory;
            return Normalize(home + path[1..], nameof(path));
        }

        if (IsPathRooted(path))
        {
            return Normalize(path, nameof(path));
        }

        return Normalize(HackExecutionContext.WorkingDirectory + DirectorySeparatorChar + path, nameof(path));
    }

    /// <summary>
    /// Resolves <paramref name="path"/> against the absolute <paramref name="basePath"/> without
    /// reading any ambient state (<c>~</c> is not expanded by this overload).
    /// </summary>
    /// <exception cref="ArgumentException">A path is empty or malformed, or <paramref name="basePath"/> is relative.</exception>
    public static string GetFullPath(string path, string basePath)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(basePath);
        ThrowIfEmpty(path, nameof(path));
        if (!IsPathRooted(basePath))
        {
            throw new ArgumentException($"Base path '{basePath}' must be absolute.", nameof(basePath));
        }

        return IsPathRooted(path)
            ? Normalize(path, nameof(path))
            : Normalize(basePath + DirectorySeparatorChar + path, nameof(path));
    }

    /// <summary>Resolves a user-supplied path to the engine's canonical <see cref="VirtualPath"/>.</summary>
    internal static VirtualPath Resolve(string path, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(path, parameterName);
        try
        {
            return VirtualPath.Parse(GetFullPath(path));
        }
        catch (ArgumentException exception) when (exception.ParamName != parameterName)
        {
            throw new ArgumentException(exception.Message, parameterName, exception);
        }
    }

    private static string Normalize(string absolutePath, string parameterName)
    {
        try
        {
            return VirtualPath.Parse(absolutePath).Value;
        }
        catch (FormatException exception)
        {
            throw new ArgumentException($"'{absolutePath}' is not a valid virtual path: {exception.Message}", parameterName, exception);
        }
    }

    private static void ThrowIfEmpty(string path, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("The path is empty.", parameterName);
        }
    }
}
