using HackerOs.App.Abstractions;
using HackerOs.AppSdk;
using HackerOs.AppSdk.Globalization;
using HackerOs.AppSdk.IO;
using Microsoft.Extensions.Localization;

namespace HackerOs.Commands.Mount;

/// <summary>
/// Implements <c>mount</c> (ADR 0181, section 12), after util-linux:
/// <list type="bullet">
/// <item><c>mount</c> or <c>mount -l</c> lists <c>/proc/mounts</c> as <c>source on point type type (options)</c>;</item>
/// <item><c>mount -t hostfs [/mnt/name]</c> opens the browser's folder dialog and mounts the chosen
/// folder of the computer as a network drive (the Enter that ran the command is the user gesture);</item>
/// <item><c>mount -o remount /mnt/name</c> asks the browser for its permission again after a reload.</item>
/// </list>
/// Exit codes follow util-linux: 0 on success, 1 for a usage error, 32 when the mount fails.
/// </summary>
public sealed class MountCommand(AppManifest manifest) : TerminalAppBase(manifest)
{
    private const int UsageError = 1;
    private const int MountFailure = 32;

    /// <inheritdoc />
    public override async ValueTask<int> ExecuteAsync(TerminalExecutionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        IStringLocalizer<Strings> text = context.App.GetLocalizer<Strings>();
        string? type = null;
        string? options = null;
        List<string> operands = [];
        IReadOnlyList<string> args = context.Arguments;
        for (int index = 0; index < args.Count; index++)
        {
            switch (args[index])
            {
                case "-l" or "--show-labels":
                    break;
                case "-t" or "--types" when index + 1 < args.Count:
                    type = args[++index];
                    break;
                case "-o" or "--options" when index + 1 < args.Count:
                    options = args[++index];
                    break;
                case { } arg when arg.StartsWith('-'):
                    await context.StandardError.WriteLineAsync(text["InvalidOption", arg]);
                    await context.StandardError.WriteLineAsync(text["Usage"]);
                    return UsageError;
                case { } operand:
                    operands.Add(operand);
                    break;
            }
        }

        if (type is null && options is null && operands.Count == 0)
        {
            return await ListAsync(context, text, cancellationToken);
        }

        if (options is not null)
        {
            if (options != "remount" || operands.Count != 1)
            {
                await context.StandardError.WriteLineAsync(text["Usage"]);
                return UsageError;
            }

            return await RemountAsync(context, text, Absolute(context, operands[0]), cancellationToken);
        }

        if (type != HostFolderType || operands.Count > 1)
        {
            await context.StandardError.WriteLineAsync(type is null || type == HostFolderType
                ? text["Usage"]
                : text["UnknownType", type]);
            return type is null || type == HostFolderType ? UsageError : MountFailure;
        }

        return await MountHostFolderAsync(context, text, operands.Count == 1 ? Absolute(context, operands[0]) : null, cancellationToken);
    }

    /// <summary>Filesystem type of a folder of the computer.</summary>
    internal const string HostFolderType = "hostfs";

    private static async ValueTask<int> MountHostFolderAsync(
        TerminalExecutionContext context,
        IStringLocalizer<Strings> text,
        string? mountPoint,
        CancellationToken cancellationToken)
    {
        try
        {
            HackDriveInfo? drive = await HackDriveInfo.MountHostFolderAsync(mountPoint, cancellationToken);
            if (drive is null)
            {
                await context.StandardError.WriteLineAsync(text["Cancelled"]);
                return MountFailure;
            }

            await context.StandardOutput.WriteLineAsync(text["Mounted", drive.VolumeLabel, drive.Name]);
            return 0;
        }
        catch (Exception exception) when (exception is NotSupportedException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            await context.StandardError.WriteLineAsync(text["Failed", exception.Message]);
            return MountFailure;
        }
    }

    private static async ValueTask<int> RemountAsync(
        TerminalExecutionContext context,
        IStringLocalizer<Strings> text,
        string mountPoint,
        CancellationToken cancellationToken)
    {
        try
        {
            if (await HackDriveInfo.ReconnectAsync(mountPoint, cancellationToken))
            {
                return 0;
            }

            await context.StandardError.WriteLineAsync(text["PermissionRefused", mountPoint]);
            return MountFailure;
        }
        catch (Exception exception) when (exception is NotSupportedException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            await context.StandardError.WriteLineAsync(text["Failed", exception.Message]);
            return MountFailure;
        }
    }

    /// <summary>Prints <c>/proc/mounts</c> the way <c>mount</c> does: <c>source on point type type (options)</c>.</summary>
    private static async ValueTask<int> ListAsync(TerminalExecutionContext context, IStringLocalizer<Strings> text, CancellationToken cancellationToken)
    {
        string[] lines;
        try
        {
            lines = await HackFile.ReadAllLinesAsync("/proc/mounts", cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            await context.StandardError.WriteLineAsync(text["Failed", exception.Message]);
            return MountFailure;
        }

        foreach (string line in lines)
        {
            string[] fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length >= 4)
            {
                await context.StandardOutput.WriteLineAsync(
                    $"{Unescape(fields[0])} on {Unescape(fields[1])} type {fields[2]} ({fields[3]})");
            }
        }

        return 0;
    }

    private static string Unescape(string field) => field.Replace("\\040", " ", StringComparison.Ordinal);

    /// <summary>The absolute mount point, with <c>.</c> and <c>..</c> resolved like the shell's <c>cd</c>.</summary>
    internal static string Absolute(TerminalExecutionContext context, string path)
    {
        Stack<string> segments = new();
        foreach (string segment in Common.CommandIO.ResolvePath(context.WorkingDirectory, path).Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == "..")
            {
                segments.TryPop(out _);
            }
            else if (segment != ".")
            {
                segments.Push(segment);
            }
        }

        return "/" + string.Join('/', segments.Reverse());
    }
}
