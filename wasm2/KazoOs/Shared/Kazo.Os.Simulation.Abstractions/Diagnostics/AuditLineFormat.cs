using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;

namespace Kazo.Os.Simulation.Abstractions.Diagnostics;

/// <summary>
/// One protected filesystem write recorded in <c>/var/log/audit.log</c> (ADR 0100 section 3).
/// Never the content: only who did what to which path, and whether it was allowed.
/// </summary>
/// <param name="TimestampUtc">When the operation ended.</param>
/// <param name="Serial">Event serial number, increasing for the life of the machine's boot.</param>
/// <param name="Operation"><c>create</c>, <c>write</c>, <c>delete</c>, <c>rename</c>, <c>copy</c> or <c>chmod</c>.</param>
/// <param name="Path">Path the operation targeted (the source of a rename).</param>
/// <param name="NewPath">Destination of a rename or copy, otherwise <see langword="null"/>.</param>
/// <param name="Uid">Identity that performed the write: <c>root</c> under <c>sudo</c> or for the OS itself, else the login name.</param>
/// <param name="Auid">Human at the origin (the login user, even after <c>sudo</c>), or <c>unset</c> for the OS itself.</param>
/// <param name="ProcessId">PID of the process, or 0 for the kernel.</param>
/// <param name="Command">Short process name (<c>comm</c>), the last segment of the app id.</param>
/// <param name="AppId">App id of the process.</param>
/// <param name="Succeeded">Whether the operation was allowed and committed.</param>
/// <param name="Error">Engine error code of a refused or failed operation.</param>
public sealed record AuditRecord(
    DateTimeOffset TimestampUtc,
    long Serial,
    string Operation,
    string Path,
    string? NewPath,
    string Uid,
    string Auid,
    long ProcessId,
    string Command,
    string AppId,
    bool Succeeded,
    string? Error);

/// <summary>
/// Line format of <c>/var/log/audit.log</c>, modelled on <c>auditd</c> with <c>log_format = ENRICHED</c>
/// (ADR 0100 section 3):
/// <code>
/// type=PATH msg=audit(1792218000.123:42): op=write path="/etc/x/settings.config" uid=0 auid=1000 pid=231 comm="settings" app="org.x.settings" res=success UID="root" AUID="benoit"
/// </code>
/// Kazo OS has no numeric uids, so <c>uid</c>/<c>auid</c> carry 0 for root, 1000 for a user and
/// 4294967295 for "unset", as <c>/proc/net/tcp</c> does; the enriched <c>UID</c>/<c>AUID</c> fields
/// name the account.
/// </summary>
public static class AuditLineFormat
{
    /// <summary>Numeric <c>auid</c> of an operation with no human at its origin.</summary>
    public const string UnsetAuid = "unset";

    private const string Prefix = "type=PATH msg=audit(";

    /// <summary>Formats one record as one line, without the terminator.</summary>
    /// <param name="record">Record to format.</param>
    public static string Format(AuditRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        StringBuilder line = new(256);
        long millis = record.TimestampUtc.ToUnixTimeMilliseconds();
        line.Append(Prefix)
            .Append((millis / 1000).ToString(CultureInfo.InvariantCulture)).Append('.')
            .Append((millis % 1000).ToString("000", CultureInfo.InvariantCulture)).Append(':')
            .Append(record.Serial.ToString(CultureInfo.InvariantCulture)).Append("): ")
            .Append("op=").Append(record.Operation)
            .Append(" path=").Append(Quote(record.Path));
        if (record.NewPath is not null)
        {
            line.Append(" new=").Append(Quote(record.NewPath));
        }

        line.Append(" uid=").Append(NumericId(record.Uid))
            .Append(" auid=").Append(NumericId(record.Auid))
            .Append(" pid=").Append(record.ProcessId.ToString(CultureInfo.InvariantCulture))
            .Append(" comm=").Append(Quote(record.Command))
            .Append(" app=").Append(Quote(record.AppId))
            .Append(" res=").Append(record.Succeeded ? "success" : "failed");
        if (record.Error is not null)
        {
            line.Append(" err=").Append(record.Error);
        }

        line.Append(" UID=").Append(Quote(record.Uid))
            .Append(" AUID=").Append(Quote(record.Auid));
        return line.ToString();
    }

    /// <summary>Parses one line written by <see cref="Format"/>.</summary>
    /// <param name="line">One line, without its terminator.</param>
    /// <param name="record">The parsed record.</param>
    /// <returns><see langword="false"/> for any other line.</returns>
    public static bool TryParse(string line, [NotNullWhen(true)] out AuditRecord? record)
    {
        record = null;
        if (line is null || !line.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        int close = line.IndexOf("): ", Prefix.Length, StringComparison.Ordinal);
        if (close < 0)
        {
            return false;
        }

        string[] stamp = line[Prefix.Length..close].Split(':');
        string[] seconds = stamp[0].Split('.');
        if (stamp.Length != 2
            || seconds.Length != 2
            || !long.TryParse(seconds[0], NumberStyles.None, CultureInfo.InvariantCulture, out long unixSeconds)
            || !int.TryParse(seconds[1], NumberStyles.None, CultureInfo.InvariantCulture, out int millis)
            || !long.TryParse(stamp[1], NumberStyles.None, CultureInfo.InvariantCulture, out long serial))
        {
            return false;
        }

        Dictionary<string, string> fields = ReadFields(line, close + 3);
        if (!fields.TryGetValue("op", out string? op)
            || !fields.TryGetValue("path", out string? path)
            || !fields.TryGetValue("res", out string? result)
            || !fields.TryGetValue("UID", out string? uid)
            || !fields.TryGetValue("AUID", out string? auid))
        {
            return false;
        }

        record = new AuditRecord(
            DateTimeOffset.FromUnixTimeMilliseconds((unixSeconds * 1000) + millis),
            serial,
            op,
            path,
            fields.GetValueOrDefault("new"),
            uid,
            auid,
            long.TryParse(fields.GetValueOrDefault("pid"), NumberStyles.None, CultureInfo.InvariantCulture, out long pid) ? pid : 0,
            fields.GetValueOrDefault("comm") ?? string.Empty,
            fields.GetValueOrDefault("app") ?? string.Empty,
            result == "success",
            fields.GetValueOrDefault("err"));
        return true;
    }

    private static string NumericId(string name) => name switch
    {
        "root" or "system" => "0",
        UnsetAuid => "4294967295",
        _ => "1000"
    };

    private static string Quote(string value) =>
        "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal) + "\"";

    private static Dictionary<string, string> ReadFields(string line, int start)
    {
        Dictionary<string, string> fields = new(StringComparer.Ordinal);
        int index = start;
        while (index < line.Length)
        {
            int equals = line.IndexOf('=', index);
            if (equals < 0)
            {
                break;
            }

            string name = line[index..equals].Trim();
            index = equals + 1;
            StringBuilder value = new();
            if (index < line.Length && line[index] == '"')
            {
                for (index++; index < line.Length && line[index] != '"'; index++)
                {
                    if (line[index] == '\\' && index + 1 < line.Length)
                    {
                        index++;
                        value.Append(line[index] == 'n' ? '\n' : line[index]);
                    }
                    else
                    {
                        value.Append(line[index]);
                    }
                }

                index++;
            }
            else
            {
                for (; index < line.Length && line[index] != ' '; index++)
                {
                    value.Append(line[index]);
                }
            }

            fields[name] = value.ToString();
            while (index < line.Length && line[index] == ' ')
            {
                index++;
            }
        }

        return fields;
    }
}
