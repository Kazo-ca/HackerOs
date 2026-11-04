using System.Globalization;
using Kazo.Os.AppSdk;
using Kazo.Os.AppSdk.IO;
using Kazo.Os.App.Abstractions;
using Kazo.Os.Simulation.Abstractions.FileSystem;

namespace Kazo.Os.Commands.Common;

/// <summary>
/// Helpers shared by the text commands (ADR 0064): read the named files or standard input like
/// coreutils (<c>-</c> is standard input), report errors as <c>cmd: file: reason</c>, and print
/// sizes the way <c>df -h</c> and <c>du -h</c> do. Paths go through the <c>Hack*</c> API, so they
/// resolve against the command's working directory with its own permissions.
/// </summary>
internal static class CommandIO
{
    /// <summary>
    /// Reads every line of the inputs: standard input when <paramref name="files"/> is empty, else
    /// each file in turn (<c>-</c> meaning standard input). A file that cannot be read is reported
    /// and skipped; <paramref name="failed"/> then becomes <see langword="true"/>.
    /// </summary>
    public static async IAsyncEnumerable<string> ReadLinesAsync(
        TerminalExecutionContext context,
        IReadOnlyList<string> files,
        string command,
        StrongBox<bool> failed,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (files.Count == 0)
        {
            files = ["-"];
        }

        foreach (string file in files)
        {
            TextReader? reader = await OpenAsync(context, file, command, cancellationToken);
            if (reader is null)
            {
                failed.Value = true;
                continue;
            }

            try
            {
                while (await reader.ReadLineAsync(cancellationToken) is { } line)
                {
                    yield return line;
                }
            }
            finally
            {
                if (file != "-")
                {
                    reader.Dispose();
                }
            }
        }
    }

    /// <summary>Opens one input (<c>-</c> is standard input); prints <c>cmd: file: reason</c> and returns null when it cannot.</summary>
    public static async Task<TextReader?> OpenAsync(TerminalExecutionContext context, string file, string command, CancellationToken cancellationToken)
    {
        if (file == "-")
        {
            return context.StandardInput;
        }

        try
        {
            if (await KazoDirectory.ExistsAsync(file, cancellationToken))
            {
                await context.StandardError.WriteLineAsync(CommandText.FileError(context, command, file, CommandText.Reason(context, Errno.IsDirectory)));
                return null;
            }

            KazoFileStream stream = await KazoFile.OpenReadAsync(file, cancellationToken);
            return new StreamReader(stream);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            await context.StandardError.WriteLineAsync(CommandText.FileError(context, command, file, CommandText.Describe(context, exception)));
            return null;
        }
    }

    /// <summary>
    /// Reads one input through the instance's own filesystem gateway, resolved against
    /// <see cref="TerminalExecutionContext.WorkingDirectory"/> (<c>-</c> is standard input): the
    /// path older commands (<c>head</c>, <c>tail</c>, <c>sort</c>) take, which works without an
    /// ambient <c>Hack*</c> context. Prints <c>cmd: file: reason</c> and returns null on failure.
    /// </summary>
    public static async Task<string?> ReadTextAsync(TerminalExecutionContext context, string file, string command, CancellationToken cancellationToken)
    {
        if (file == "-")
        {
            return await context.StandardInput.ReadToEndAsync(cancellationToken);
        }

        string resolved = ResolvePath(context.WorkingDirectory, file);
        FileSystemResult<FileSystemContentReadHandle> result;
        try
        {
            result = await context.App.FileSystem.ReadAsync(new FileSystemReadRequest(VirtualPath.Parse(resolved)), cancellationToken);
        }
        catch (FormatException)
        {
            await context.StandardError.WriteLineAsync(CommandText.FileError(context, command, file, CommandText.Reason(context, Errno.NotFound)));
            return null;
        }

        if (!result.Succeeded || result.Value is null)
        {
            await context.StandardError.WriteLineAsync(CommandText.FileError(context, command, file, CommandText.Reason(context, ReadErrno(result.Error))));
            return null;
        }

        using StreamReader reader = new(result.Value.Content);
        return await reader.ReadToEndAsync(cancellationToken);
    }

    /// <summary>Resolves a path against a working directory, as the shell's commands always did.</summary>
    public static string ResolvePath(string cwd, string path) =>
        path.StartsWith('/') ? path : (cwd.TrimEnd('/') + "/" + path).Replace("//", "/", StringComparison.Ordinal);

    /// <summary>Splits text into lines like <c>ReadLine</c>: no trailing empty line for a final newline.</summary>
    public static List<string> SplitLines(string text)
    {
        List<string> lines = [.. text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n')];
        if (lines.Count > 0 && lines[^1].Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        return lines;
    }

    /// <summary>
    /// Formats a size like <c>-h</c> in coreutils: powers of 1024 with one decimal below 10
    /// (<c>4.0K</c>, <c>12M</c>, <c>1.5G</c>), bytes as a plain number.
    /// </summary>
    public static string HumanSize(long bytes)
    {
        string[] units = ["", "K", "M", "G", "T", "P"];
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        if (unit == 0)
        {
            return bytes.ToString(CultureInfo.InvariantCulture);
        }

        // coreutils rounds up, so 1025 bytes is 1.1K, never 1.0K.
        double rounded = value < 10 ? Math.Ceiling(value * 10) / 10 : Math.Ceiling(value);
        return (value < 10 ? rounded.ToString("0.0", CultureInfo.InvariantCulture) : rounded.ToString("0", CultureInfo.InvariantCulture)) + units[unit];
    }

    /// <summary>Splits arguments into option clusters and operands; <c>--</c> ends options.</summary>
    public static (List<string> Options, List<string> Operands) SplitArguments(IReadOnlyList<string> arguments)
    {
        List<string> options = [];
        List<string> operands = [];
        bool endOfOptions = false;
        foreach (string argument in arguments)
        {
            if (!endOfOptions && argument == "--")
            {
                endOfOptions = true;
                continue;
            }

            if (!endOfOptions && argument.Length > 1 && argument[0] == '-')
            {
                options.Add(argument);
            }
            else
            {
                operands.Add(argument);
            }
        }

        return (options, operands);
    }

    /// <summary>
    /// The <c>errno</c> a failed <c>open(2)</c> for reading reports: <c>EACCES</c>, <c>EISDIR</c>,
    /// <c>EMFILE</c> at the <c>nofile</c> limit (ADR 0125), otherwise <c>ENOENT</c>.
    /// </summary>
    internal static Errno ReadErrno(FileSystemError? error) => error?.Code switch
    {
        FileSystemErrorCode.PermissionDenied or FileSystemErrorCode.CapabilityDenied or FileSystemErrorCode.AuthorityDenied => Errno.PermissionDenied,
        FileSystemErrorCode.NotFile => Errno.IsDirectory,
        FileSystemErrorCode.TooManyOpenFiles => Errno.TooManyOpenFiles,
        _ => Errno.NotFound
    };
}

/// <summary>A mutable box, so an async iterator can report a failure to its caller.</summary>
internal sealed class StrongBox<T>(T value)
{
    public T Value { get; set; } = value;
}
