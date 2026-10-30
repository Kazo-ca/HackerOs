using System.Globalization;
using System.Text;

namespace HackerOs.AppSdk.IO;

/// <summary>
/// Reads and writes the <c>.trashinfo</c> format of the freedesktop.org Trash specification 1.0
/// (ADR 0052, section 1), and derives the <c>name.N.ext</c> candidates used on name collisions.
/// </summary>
/// <param name="OriginalPath">Decoded absolute original path, or <see langword="null"/> when missing or invalid.</param>
/// <param name="DeletedAt">Deletion time in the local time zone, or <see langword="null"/> when missing or invalid.</param>
/// <param name="DeletedOnAnotherDevice">
/// The synchronization put the element here because it was deleted on another device (ADR 0101
/// section 1): <c>X-HackerOS-Origin=deleted-on-another-device</c>, an extension key other
/// implementations ignore.
/// </param>
internal sealed record HackTrashInfo(string? OriginalPath, DateTimeOffset? DeletedAt, bool DeletedOnAnotherDevice = false)
{
    private const string Header = "[Trash Info]";
    private const string DateFormat = "yyyy-MM-ddTHH:mm:ss";

    /// <summary>
    /// Builds the content of a <c>.trashinfo</c> file: <c>Path</c> is URL-encoded (RFC 2396) with
    /// <c>/</c> kept as is; <c>DeletionDate</c> is the local time, without offset, to the second.
    /// </summary>
    public static string Format(string originalPath, DateTimeOffset deletedAt)
    {
        StringBuilder builder = new();
        builder.Append(Header).Append('\n');
        builder.Append("Path=").Append(EncodePath(originalPath)).Append('\n');
        builder.Append("DeletionDate=").Append(deletedAt.ToString(DateFormat, CultureInfo.InvariantCulture)).Append('\n');
        return builder.ToString();
    }

    /// <summary>
    /// Parses a <c>.trashinfo</c> file. Tolerant like desktop implementations: a missing header or
    /// an unreadable key yields <see langword="null"/> for that value instead of failing.
    /// </summary>
    public static HackTrashInfo? Parse(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        bool inSection = false;
        string? path = null;
        DateTimeOffset? deletedAt = null;
        bool deletedOnAnotherDevice = false;

        foreach (string rawLine in content.Split('\n'))
        {
            string line = rawLine.TrimEnd('\r');
            if (line.StartsWith('['))
            {
                inSection = line == Header;
                continue;
            }

            if (!inSection)
            {
                continue;
            }

            int separator = line.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            string key = line[..separator].Trim();
            string value = line[(separator + 1)..].Trim();
            if (key == "Path" && path is null)
            {
                path = DecodePath(value);
            }
            else if (key == "DeletionDate" && deletedAt is null)
            {
                deletedAt = ParseDate(value);
            }
            else if (key == "X-HackerOS-Origin")
            {
                deletedOnAnotherDevice = value == "deleted-on-another-device";
            }
        }

        return inSection || path is not null ? new HackTrashInfo(path, deletedAt, deletedOnAnotherDevice) : null;
    }

    /// <summary>
    /// Returns the name to try at <paramref name="attempt"/> (1-based): the original name first,
    /// then <c>report.2.txt</c>, <c>report.3.txt</c>... A dotfile without extension
    /// (<c>.bashrc</c>) becomes <c>.bashrc.2</c>.
    /// </summary>
    public static string CandidateName(string name, int attempt)
    {
        if (attempt <= 1)
        {
            return name;
        }

        int dot = name.LastIndexOf('.');
        return dot > 0
            ? $"{name[..dot]}.{attempt.ToString(CultureInfo.InvariantCulture)}{name[dot..]}"
            : $"{name}.{attempt.ToString(CultureInfo.InvariantCulture)}";
    }

    /// <summary>Percent-encodes every segment of an absolute path, keeping the separators.</summary>
    public static string EncodePath(string path) =>
        string.Join('/', path.Split('/').Select(Uri.EscapeDataString));

    private static string? DecodePath(string value)
    {
        if (value.Length == 0)
        {
            return null;
        }

        try
        {
            string decoded = Uri.UnescapeDataString(value);

            // The home trash always stores absolute paths; a relative one is relative to the topdir (/).
            string absolute = decoded.StartsWith('/') ? decoded : "/" + decoded;
            return HackerOs.App.Abstractions.VirtualPath.Parse(absolute).Value;
        }
        catch (Exception exception) when (exception is FormatException or ArgumentException or UriFormatException)
        {
            return null;
        }
    }

    private static DateTimeOffset? ParseDate(string value)
    {
        if (!DateTime.TryParseExact(value, DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime local))
        {
            return null;
        }

        // ADR 0193: DeletionDate is written in the app's time zone (HackTrash), so it is read back in it.
        DateTime unspecified = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        TimeZoneInfo zone = HackExecutionContext.CurrentOrNull?.TimeZone ?? TimeZoneInfo.Local;
        return new DateTimeOffset(unspecified, zone.GetUtcOffset(unspecified));
    }
}
