using Kazo.Os.App.Abstractions;
using Kazo.Os.App.Abstractions.Policy;
using Kazo.Os.Simulation.Abstractions.FileSystem;
using Kazo.Os.Simulation.Abstractions.Sessions;

namespace Kazo.Os.Platform.Core.Intents;

/// <summary>
/// Reads and writes the user's "always use" choices for app-defined intents (ADR 0096, section 5):
/// <c>~/.config/hackeros/intents.config</c>, one <c>&lt;intentId&gt;=&lt;appId&gt;</c> line per intent,
/// like the <c>[Default Applications]</c> section of freedesktop's <c>mimeapps.list</c>.
/// </summary>
/// <remarks>
/// Every access runs as the acting user (POSIX permissions of their home apply): the file belongs
/// to them and they may edit it with <c>nano</c>. Lines that are blank, comments (<c>#</c>) or
/// malformed are ignored on read and kept on write.
/// </remarks>
internal static class IntentChoiceStore
{
    /// <summary>Gets the file of one user.</summary>
    /// <param name="loginName">Login name; the home is <c>/home/&lt;login&gt;</c>.</param>
    public static VirtualPath GetPath(string loginName) =>
        VirtualPath.Parse($"/home/{loginName}/.config/hackeros/{FileName}");

    private const string FileName = "intents.config";

    /// <summary>Reads the app remembered for <paramref name="intentId"/>, or <see langword="null"/>.</summary>
    public static async ValueTask<string?> ReadAsync(
        IFileSystemService fileSystem, AuthenticatedPrincipal principal, string intentId, CancellationToken cancellationToken)
    {
        string? content = await ReadContentAsync(fileSystem, principal, cancellationToken).ConfigureAwait(false);
        if (content is null)
        {
            return null;
        }

        foreach (string rawLine in content.Split('\n'))
        {
            if (TryParse(rawLine, out string key, out string value) && string.Equals(key, intentId, StringComparison.Ordinal))
            {
                return value;
            }
        }

        return null;
    }

    /// <summary>
    /// Remembers <paramref name="appId"/> for <paramref name="intentId"/>, replacing an earlier
    /// choice and keeping every other line. Creates <c>~/.config/hackeros</c> and the file (0600) if needed.
    /// </summary>
    /// <returns><see langword="false"/> when the user's permissions refuse the write.</returns>
    public static async ValueTask<bool> WriteAsync(
        IFileSystemService fileSystem,
        AuthenticatedPrincipal principal,
        string intentId,
        string appId,
        CancellationToken cancellationToken)
    {
        string existing = await ReadContentAsync(fileSystem, principal, cancellationToken).ConfigureAwait(false)
            ?? "# Apps chosen with \"Always use\" for app-defined intents (ADR 0096): <intentId>=<appId>\n";
        List<string> lines = [.. existing.Split('\n')];
        if (lines.Count > 0 && lines[^1].Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        int index = lines.FindIndex(line => TryParse(line, out string key, out _) && string.Equals(key, intentId, StringComparison.Ordinal));
        string entry = $"{intentId}={appId}";
        if (index >= 0)
        {
            lines[index] = entry;
        }
        else
        {
            lines.Add(entry);
        }

        return await PrincipalTextFile.WriteConfigAsync(
            fileSystem, principal, FileName, string.Join('\n', lines) + "\n", 0b110_000_000, cancellationToken).ConfigureAwait(false);
    }

    private static ValueTask<string?> ReadContentAsync(
        IFileSystemService fileSystem, AuthenticatedPrincipal principal, CancellationToken cancellationToken) =>
        PrincipalTextFile.ReadAsync(fileSystem, principal, GetPath(principal.LoginName.Value), cancellationToken);

    private static bool TryParse(string rawLine, out string key, out string value)
    {
        string line = rawLine.Trim();
        int equals = line.IndexOf('=');
        if (line.Length == 0 || line.StartsWith('#') || equals <= 0 || equals == line.Length - 1)
        {
            key = value = string.Empty;
            return false;
        }

        key = line[..equals].Trim();
        value = line[(equals + 1)..].Trim();
        return true;
    }
}

/// <summary>
/// Filesystem authorization of the acting user for intents the OS checks on their behalf (ADR 0096):
/// POSIX permissions and groups of the principal, with the broad read rights a file manager holds,
/// so the answer is "may this user read it", as a file manager running as them would find.
/// </summary>
internal static class PrincipalFileAccess
{
    /// <summary>Builds the authorization context of <paramref name="principal"/>.</summary>
    /// <param name="principal">Acting user.</param>
    /// <param name="appId">App id recorded on the operation (audit, private-path rules).</param>
    public static FileSystemAuthorizationContext ForUser(AuthenticatedPrincipal principal, string appId)
    {
        AppOperationContext operation = new()
        {
            AppId = appId,
            UserId = principal.UserId.ToString(),
            UserName = principal.LoginName.Value,
            UserAuthority = principal.Authority,
            GrantedCapabilities = new HashSet<string>(StringComparer.Ordinal)
            {
                AppCapabilities.FileSystemUserHomeRead,
                AppCapabilities.FileSystemUserHomeWrite,
                AppCapabilities.FileSystemSystemRead
            },
            IsSystemOperation = false
        };

        return new FileSystemAuthorizationContext(
            operation, [.. principal.GroupIds.Select(static group => group.ToString())], DateTimeOffset.UtcNow);
    }
}
