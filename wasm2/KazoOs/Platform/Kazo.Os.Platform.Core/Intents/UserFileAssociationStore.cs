using Kazo.Os.App.Abstractions;
using Kazo.Os.Simulation.Abstractions.FileSystem;
using Kazo.Os.Simulation.Abstractions.Sessions;

namespace Kazo.Os.Platform.Core.Intents;

/// <summary>How a write of the user's association file ended (ADR 0121, section 3).</summary>
internal enum UserFileAssociationWriteStatus
{
    /// <summary>The file now holds the change.</summary>
    Changed,

    /// <summary>The existing file is not a valid association document; it was left untouched.</summary>
    FileInvalid,

    /// <summary>The user's permissions refused the write.</summary>
    WriteFailed
}

/// <summary>
/// The user layer of the file associations (ADR 0121, section 1):
/// <c>~/.config/kazoos/file-associations.json</c>, the same strict-JSON schema as
/// <c>/etc/kazoos/file-associations.json</c>, like freedesktop's <c>~/.config/mimeapps.list</c>
/// over <c>/etc/xdg/mimeapps.list</c>. Read and written as the acting user.
/// </summary>
/// <remarks>
/// An absent or invalid file contributes nothing: resolution falls through to the system layer.
/// A write never replaces an invalid file, so a typo made with <c>nano</c> is not silently lost.
/// </remarks>
internal static class UserFileAssociationStore
{
    /// <summary>File name inside <c>~/.config/kazoos</c>.</summary>
    public const string FileName = "file-associations.json";

    private const string EmptyContent = "{\"schemaVersion\":1,\"associations\":[]}";

    /// <summary>Gets the file of one user.</summary>
    /// <param name="loginName">Login name; the home is <c>/home/&lt;login&gt;</c>.</param>
    public static VirtualPath GetPath(string loginName) =>
        VirtualPath.Parse($"/home/{loginName}/.config/kazoos/{FileName}");

    /// <summary>Reads the user's layer, or <see langword="null"/> when the file is absent, unreadable or invalid.</summary>
    /// <param name="fileSystem">OS filesystem.</param>
    /// <param name="principal">User whose layer applies.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    public static async ValueTask<FileAssociationIndex?> ReadAsync(
        IFileSystemService fileSystem, AuthenticatedPrincipal principal, CancellationToken cancellationToken)
    {
        string? content = await PrincipalTextFile
            .ReadAsync(fileSystem, principal, GetPath(principal.LoginName.Value), cancellationToken)
            .ConfigureAwait(false);
        return content is not null && new FileAssociationSettingsValidator().Validate(content).Count == 0
            ? FileAssociationIndex.Build(content)
            : null;
    }

    /// <summary>
    /// Makes <paramref name="appId"/> the user's default for <paramref name="action"/> on
    /// <paramref name="target"/>, or removes the user's choice when <paramref name="appId"/> is
    /// <see langword="null"/>. Other entries, and other actions of the same target, are kept.
    /// </summary>
    /// <param name="fileSystem">OS filesystem.</param>
    /// <param name="principal">User whose file is written.</param>
    /// <param name="target">Normalized target (<see cref="TryNormalizeTarget"/>).</param>
    /// <param name="action">Normalized action (<c>open</c> or <c>edit</c>).</param>
    /// <param name="appId">New default, or <see langword="null"/> to fall back to the system layer.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    public static async ValueTask<UserFileAssociationWriteStatus> WriteAsync(
        IFileSystemService fileSystem,
        AuthenticatedPrincipal principal,
        string target,
        string action,
        string? appId,
        CancellationToken cancellationToken)
    {
        string? existing = await PrincipalTextFile
            .ReadAsync(fileSystem, principal, GetPath(principal.LoginName.Value), cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null && new FileAssociationSettingsValidator().Validate(existing).Count != 0)
        {
            return UserFileAssociationWriteStatus.FileInvalid;
        }

        bool isExtension = IsExtension(target);
        List<FileAssociationEntry> entries = [];
        foreach (FileAssociationEntry entry in FileAssociationIndex.Build(existing ?? EmptyContent).Entries)
        {
            bool sameTarget = isExtension
                ? string.Equals(entry.Extension, target, StringComparison.Ordinal)
                : string.Equals(entry.MediaType, target, StringComparison.Ordinal);
            if (!sameTarget)
            {
                entries.Add(entry);
                continue;
            }

            // An entry may name both an extension and a media type: only the action moves out of it.
            string[] remaining = [.. entry.Actions.Where(a => !string.Equals(a, action, StringComparison.Ordinal))];
            if (remaining.Length > 0)
            {
                entries.Add(entry with { Actions = remaining });
            }
        }

        if (appId is not null)
        {
            entries.Add(new FileAssociationEntry(
                isExtension ? target : null, isExtension ? null : target, appId, [action]));
        }

        bool written = await PrincipalTextFile.WriteConfigAsync(
            fileSystem, principal, FileName, FileAssociationIndex.Serialize(entries) + "\n", 0b110_100_100, cancellationToken)
            .ConfigureAwait(false);
        return written ? UserFileAssociationWriteStatus.Changed : UserFileAssociationWriteStatus.WriteFailed;
    }

    /// <summary>
    /// Normalizes an association target: an extension with its leading dot (<c>.md</c>) or a media
    /// type <c>type/subtype</c> (<c>text/markdown</c>, <c>x-scheme-handler/https</c>), lowercased.
    /// </summary>
    /// <param name="target">Raw target.</param>
    /// <param name="normalized">The lowercase target, when valid.</param>
    public static bool TryNormalizeTarget(string? target, out string normalized)
    {
        normalized = target?.Trim().ToLowerInvariant() ?? string.Empty;
        if (normalized.Length < 2 || normalized.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
        {
            return false;
        }

        if (normalized[0] == '.')
        {
            return normalized.IndexOf('/') < 0 && normalized.IndexOf('.', 1) < 0;
        }

        int slash = normalized.IndexOf('/');
        return slash > 0 && slash < normalized.Length - 1 && normalized.IndexOf('/', slash + 1) < 0;
    }

    /// <summary>Whether a normalized target is an extension rather than a media type.</summary>
    public static bool IsExtension(string target) => target.StartsWith('.');
}
