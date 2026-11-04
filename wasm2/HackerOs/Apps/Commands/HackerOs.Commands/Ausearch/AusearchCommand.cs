using System.Globalization;
using HackerOs.App.Abstractions;
using HackerOs.AppSdk;
using HackerOs.Commands.Common;
using HackerOs.Simulation.Abstractions.Diagnostics;
using HackerOs.Simulation.Abstractions.FileSystem;
using HackerOs.AppSdk.Globalization;
using Microsoft.Extensions.Localization;

namespace HackerOs.Commands.Ausearch;

/// <summary>
/// <c>ausearch</c>: searches the filesystem audit log <c>/var/log/audit.log</c> and its rotated
/// archives (ADR 0100 section 3), like the <c>auditd</c> tool of the same name. The log is
/// <c>0600 root</c>, so it is run with <c>sudo</c>.
/// </summary>
/// <remarks>
/// Filters (all must match): <c>-f path</c> (the path, a file below it, or a file of that name),
/// <c>-ua user</c> (uid or auid), <c>-ui user</c> (uid), <c>-ul user</c> (auid, the human at the origin),
/// <c>-c comm</c>, <c>--success yes|no</c> (<c>-sv</c>), <c>-if file</c> to read another file.
/// <c>-i</c> is accepted: names are already interpreted in KazoOS's enriched records.
/// </remarks>
public sealed class AusearchCommand(AppManifest manifest) : TerminalAppBase(manifest)
{
    /// <summary>Default audit log.</summary>
    public const string AuditLogPath = "/var/log/audit.log";

    private const int MaxArchives = 5;

    /// <inheritdoc />
    public override async ValueTask<int> ExecuteAsync(TerminalExecutionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        IStringLocalizer<Strings> text = context.App.GetLocalizer<Strings>();
        string? file = null, user = null, uid = null, auid = null, command = null;
        bool? success = null;
        string? inputFile = null;
        IReadOnlyList<string> args = context.Arguments;
        for (int i = 0; i < args.Count; i++)
        {
            string option = args[i];
            if (option is "--help" or "-h")
            {
                await context.StandardOutput.WriteLineAsync(text["Usage"]);
                return 0;
            }

            if (option is "-i" or "--interpret")
            {
                continue;
            }

            if (i + 1 >= args.Count
                || option is not ("-f" or "--file" or "-ua" or "-ui" or "-ul" or "-c" or "--comm" or "--success" or "-sv" or "-if" or "--input"))
            {
                await context.StandardError.WriteLineAsync(text["UnrecognizedOption", option]);
                return 1;
            }

            string value = args[++i];
            switch (option)
            {
                case "-f" or "--file":
                    file = value.StartsWith('/') ? value.TrimEnd('/') : value;
                    break;
                case "-ua":
                    user = value;
                    break;
                case "-ui":
                    uid = value;
                    break;
                case "-ul":
                    auid = value;
                    break;
                case "-c" or "--comm":
                    command = value;
                    break;
                case "-if" or "--input":
                    inputFile = CommandIO.ResolvePath(context.WorkingDirectory, value);
                    break;
                default:
                    if (value is not ("yes" or "no"))
                    {
                        await context.StandardError.WriteLineAsync(text["InvalidSuccess", value]);
                        return 1;
                    }

                    success = value == "yes";
                    break;
            }
        }

        // Without -if: the rotated archives, oldest first, then the active log, as auditd keeps them.
        List<string> files = inputFile is not null
            ? [inputFile]
            : [.. Enumerable.Range(1, MaxArchives).Reverse().Select(index => $"{AuditLogPath}.{index}"), AuditLogPath];
        int matches = 0;
        foreach (string path in files)
        {
            string? content = await ReadAsync(context, text, path, required: inputFile is not null || path == AuditLogPath, cancellationToken);
            if (content is null)
            {
                if (path == AuditLogPath || inputFile is not null)
                {
                    return 1;
                }

                continue;
            }

            foreach (string line in CommandIO.SplitLines(content))
            {
                if (!AuditLineFormat.TryParse(line, out AuditRecord? record) || !Matches(record))
                {
                    continue;
                }

                matches++;
                await context.StandardOutput.WriteLineAsync("----");
                await context.StandardOutput.WriteLineAsync(
                    "time->" + record.TimestampUtc.ToString("ddd MMM d HH:mm:ss yyyy", CultureInfo.InvariantCulture));
                await context.StandardOutput.WriteLineAsync(line);
            }
        }

        if (matches == 0)
        {
            await context.StandardError.WriteLineAsync(text["NoMatches"]);
            return 1;
        }

        return 0;

        bool Matches(AuditRecord record) =>
            (file is null || MatchesPath(record.Path, file) || (record.NewPath is { } target && MatchesPath(target, file)))
            && (user is null || record.Uid == user || record.Auid == user)
            && (uid is null || record.Uid == uid)
            && (auid is null || record.Auid == auid)
            && (command is null || record.Command == command)
            && (success is null || record.Succeeded == success);
    }

    private static bool MatchesPath(string recorded, string wanted) =>
        wanted.StartsWith('/')
            ? recorded == wanted || recorded.StartsWith(wanted + "/", StringComparison.Ordinal)
            : recorded[(recorded.LastIndexOf('/') + 1)..] == wanted;

    private static async Task<string?> ReadAsync(TerminalExecutionContext context, IStringLocalizer<Strings> text, string path, bool required, CancellationToken cancellationToken)
    {
        FileSystemResult<FileSystemContentReadHandle> read = await context.App.FileSystem
            .ReadAsync(new FileSystemReadRequest(VirtualPath.Parse(path)), cancellationToken);
        if (read.Succeeded && read.Value is not null)
        {
            await using FileSystemContentReadHandle handle = read.Value;
            using StreamReader reader = new(handle.Content);
            return await reader.ReadToEndAsync(cancellationToken);
        }

        if (required || read.Error?.Code != FileSystemErrorCode.NotFound)
        {
            string reason = read.Error?.Code switch
            {
                FileSystemErrorCode.PermissionDenied or FileSystemErrorCode.CapabilityDenied or FileSystemErrorCode.AuthorityDenied => CommandText.Reason(context, Errno.PermissionDenied),
                FileSystemErrorCode.NotFound => CommandText.Reason(context, Errno.NotFound),
                _ => read.Error?.Code.ToString() ?? "error"
            };
            await context.StandardError.WriteLineAsync(text["OpenFailed", path, reason]);
        }

        return null;
    }
}
