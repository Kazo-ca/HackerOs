using Kazo.Os.AppSdk.IO;

namespace Kazo.Os.Apps.WebServer.Daemon;

/// <summary>
/// The rights of the <c>www-data</c> service account (ADR 0077, section 2; ADR 0080, section 2), with
/// which <c>kazowebd</c> serves an administrator's site: <c>www-data</c> owns no file and is in no
/// group of the OS, so only the "other" permission bits apply to it, exactly as on Debian when
/// Apache's worker reads <c>/var/www</c>.
/// </summary>
/// <remarks>
/// It must be able to cross every directory from <c>/</c> down to the file (<c>o+x</c>) and read the
/// file (<c>o+r</c>); listing a directory also needs <c>o+r</c> on it. A site rooted in a home
/// (<c>0700</c>) therefore answers <c>403 Forbidden</c>, the classic Apache "userdir" surprise, which
/// Web Server Manager explains when such a site is created.
/// </remarks>
public static class WwwDataAccess
{
    /// <summary>The service account's login name.</summary>
    public const string AccountName = "www-data";

    /// <summary>Gets whether <c>www-data</c> may read the file at <paramref name="path"/>.</summary>
    /// <param name="path">Absolute path of a regular file.</param>
    /// <param name="cancellationToken">Cancels the reads of the metadata.</param>
    public static Task<bool> CanReadFileAsync(string path, CancellationToken cancellationToken = default) =>
        CheckAsync(path, UnixFileMode.OtherRead, cancellationToken);

    /// <summary>Gets whether <c>www-data</c> may list the directory at <paramref name="path"/>.</summary>
    /// <param name="path">Absolute path of a directory.</param>
    /// <param name="cancellationToken">Cancels the reads of the metadata.</param>
    public static Task<bool> CanListDirectoryAsync(string path, CancellationToken cancellationToken = default) =>
        CheckAsync(path, UnixFileMode.OtherRead | UnixFileMode.OtherExecute, cancellationToken);

    /// <summary>Gets whether <c>www-data</c> may cross every directory down to, and including, <paramref name="path"/>.</summary>
    /// <param name="path">Absolute path of a directory.</param>
    /// <param name="cancellationToken">Cancels the reads of the metadata.</param>
    public static Task<bool> CanTraverseAsync(string path, CancellationToken cancellationToken = default) =>
        CheckAsync(path, UnixFileMode.OtherExecute, cancellationToken);

    private static async Task<bool> CheckAsync(string path, UnixFileMode required, CancellationToken cancellationToken)
    {
        try
        {
            foreach (string ancestor in Ancestors(path))
            {
                KazoDirectoryInfo directory = await KazoDirectoryInfo.GetAsync(ancestor, cancellationToken).ConfigureAwait(false);
                if (!directory.Exists || (directory.UnixFileMode & UnixFileMode.OtherExecute) == 0)
                {
                    return false;
                }
            }

            UnixFileMode mode = await KazoFile.GetUnixFileModeAsync(path, cancellationToken).ConfigureAwait(false);
            return (mode & required) == required;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // What the server's own user cannot even stat, www-data cannot read either.
            return false;
        }
    }

    /// <summary>Gets <c>/</c>, then each directory above <paramref name="path"/>, top-down.</summary>
    private static IEnumerable<string> Ancestors(string path)
    {
        yield return "/";
        string[] segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        string current = string.Empty;
        for (int index = 0; index < segments.Length - 1; index++)
        {
            current += "/" + segments[index];
            yield return current;
        }
    }
}
