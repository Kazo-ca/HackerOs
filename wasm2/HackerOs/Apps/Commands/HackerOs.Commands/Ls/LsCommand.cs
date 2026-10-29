using System.Globalization;
using HackerOs.App.Abstractions;
using HackerOs.AppSdk;
using HackerOs.Commands.Common;
using HackerOs.Simulation.Abstractions.FileSystem;
using HackerOs.AppSdk.Globalization;
using Microsoft.Extensions.Localization;

namespace HackerOs.Commands.Ls;

/// <summary>
/// Implements the <c>ls</c> terminal command with <c>-a</c> and <c>-l</c> support (`P2-CMD-002`).
/// </summary>
public sealed class LsCommand : TerminalAppBase
{
    /// <summary>Initializes the command with its manifest.</summary>
    public LsCommand(AppManifest manifest) : base(manifest)
    {
    }

    /// <inheritdoc />
    public override async ValueTask<int> ExecuteAsync(TerminalExecutionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        bool showAll = false;
        bool longFormat = false;
        List<string> targetPaths = [];

        foreach (string arg in context.Arguments)
        {
            if (arg.StartsWith('-') && arg.Length > 1)
            {
                foreach (char ch in arg[1..])
                {
                    if (ch == 'a') showAll = true;
                    else if (ch == 'l') longFormat = true;
                }
            }
            else
            {
                targetPaths.Add(arg);
            }
        }

        if (targetPaths.Count == 0)
        {
            targetPaths.Add(context.WorkingDirectory);
        }

        int exitCode = 0;
        foreach (string rawPath in targetPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string resolvedPath = ResolvePath(context.WorkingDirectory, rawPath);
            FileSystemResult<FileSystemDirectorySnapshot> listResult = await context.App.FileSystem.EnumerateAsync(
                new FileSystemEnumerateRequest(VirtualPath.Parse(resolvedPath)), cancellationToken);

            if (!listResult.Succeeded || listResult.Value is null)
            {
                if (listResult.Error?.Code == FileSystemErrorCode.PermissionDenied || listResult.Error?.Code == FileSystemErrorCode.CapabilityDenied)
                {
                    context.StandardError.WriteLine(context.App.GetLocalizer<Strings>()["CannotAccess", rawPath, CommandText.Reason(context, Errno.PermissionDenied)]);
                }
                else
                {
                    context.StandardError.WriteLine(context.App.GetLocalizer<Strings>()["CannotAccess", rawPath, CommandText.Reason(context, Errno.NotFound)]);
                }
                exitCode = 1;
                continue;
            }

            IEnumerable<FileSystemDirectoryItem> entries = listResult.Value.Entries;
            if (!showAll)
            {
                entries = entries.Where(e => !e.Name.Value.StartsWith('.'));
            }

            List<FileSystemDirectoryItem> sorted = [.. entries.OrderBy(e => e.Name.Value, StringComparer.OrdinalIgnoreCase)];

            if (longFormat)
            {
                foreach (FileSystemDirectoryItem item in sorted)
                {
                    string kindChar = item.Metadata.Kind switch
                    {
                        FileSystemEntryKind.Directory => "d",
                        FileSystemEntryKind.SymbolicLink => "l",
                        FileSystemEntryKind.CharacterDevice => "c",
                        _ => "-"
                    };
                    string mode = item.Metadata.Permissions.Owner.HasFlag(FileSystemAccess.Read) ? "r" : "-";
                    mode += item.Metadata.Permissions.Owner.HasFlag(FileSystemAccess.Write) ? "w" : "-";
                    mode += item.Metadata.Permissions.Owner.HasFlag(FileSystemAccess.Execute) ? "x" : "-";
                    long length = item.Metadata is FileMetadata fm ? fm.Length : 0;
                    string size = length.ToString(CultureInfo.InvariantCulture).PadLeft(8);
                    // ADR 0193: in the process's time zone (its TZ, else the session's), like GNU ls.
                    string time = TimeZoneInfo.ConvertTime(item.Metadata.Timestamps.ContentModifiedAtUtc, context.App.TimeZone)
                        .ToString("MMM dd HH:mm", CultureInfo.InvariantCulture);
                    string ownerStr = item.Metadata.OwnerId.PadRight(8);
                    context.StandardOutput.WriteLine($"{kindChar}{mode}  {ownerStr}  {size}  {time}  {item.Name.Value}");
                }
            }
            else
            {
                context.StandardOutput.WriteLine(string.Join("  ", sorted.Select(e => e.Name.Value)));
            }
        }

        return exitCode;
    }

    private static string ResolvePath(string cwd, string target)
    {
        if (target.StartsWith('/'))
        {
            return NormalizePath(target);
        }
        return NormalizePath($"{cwd.TrimEnd('/')}/{target}");
    }

    private static string NormalizePath(string rawPath)
    {
        string[] segments = rawPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        Stack<string> stack = new();

        foreach (string segment in segments)
        {
            if (segment == ".")
            {
                continue;
            }
            if (segment == "..")
            {
                if (stack.Count > 0)
                {
                    stack.Pop();
                }
            }
            else
            {
                stack.Push(segment);
            }
        }

        return "/" + string.Join('/', stack.ToArray().Reverse());
    }
}
