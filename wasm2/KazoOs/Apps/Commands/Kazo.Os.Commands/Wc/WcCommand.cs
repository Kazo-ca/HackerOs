using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk;
using Kazo.Os.Commands.Common;
using Kazo.Os.Simulation.Abstractions.FileSystem;

namespace Kazo.Os.Commands.Wc;

/// <summary>
/// Implements the <c>wc</c> terminal command (`P4-W5-CMD-003`).
/// Prints line, word, and byte counts for stdin or virtual files.
/// </summary>
public sealed class WcCommand : TerminalAppBase
{
    /// <summary>Initializes the command with its manifest.</summary>
    public WcCommand(AppManifest manifest) : base(manifest) { }

    /// <inheritdoc />
    public override async ValueTask<int> ExecuteAsync(
        TerminalExecutionContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        bool countLines = false;
        bool countWords = false;
        bool countBytes = false;
        List<string> files = [];

        foreach (var a in context.Arguments)
        {
            if (a is "-l" or "--lines") countLines = true;
            else if (a is "-w" or "--words") countWords = true;
            else if (a is "-c" or "--bytes") countBytes = true;
            else if (!a.StartsWith('-')) files.Add(a);
        }

        if (!countLines && !countWords && !countBytes)
            countLines = countWords = countBytes = true;

        if (files.Count == 0)
        {
            string content = await context.StandardInput.ReadToEndAsync(cancellationToken);
            PrintCounts(context.StandardOutput, content, null, countLines, countWords, countBytes);
        }
        else
        {
            long totL = 0, totW = 0, totC = 0;
            foreach (var file in files)
            {
                var resolved = ResolvePath(context.WorkingDirectory, file);
                var req = new FileSystemReadRequest(VirtualPath.Parse(resolved));
                var res = await context.App.FileSystem.ReadAsync(req, cancellationToken);
                if (!res.Succeeded || res.Value is null)
                {
                    context.StandardError.WriteLine(CommandText.FileError(context, "wc", file, CommandText.Reason(context, CommandIO.ReadErrno(res.Error))));
                    continue;
                }

                using var reader = new StreamReader(res.Value.Content);
                string content = await reader.ReadToEndAsync(cancellationToken);
                var (l, w, c) = Count(content);
                totL += l; totW += w; totC += c;
                PrintCounts(context.StandardOutput, content, file, countLines, countWords, countBytes);
            }

            if (files.Count > 1)
            {
                string lineStr = countLines ? $"{totL,8} " : "";
                string wordStr = countWords ? $"{totW,8} " : "";
                string byteStr = countBytes ? $"{totC,8} " : "";
                context.StandardOutput.WriteLine($"{lineStr}{wordStr}{byteStr}total");
            }
        }

        return 0;
    }

    private static (long Lines, long Words, long Bytes) Count(string text)
    {
        long lines = text.Split('\n').Length - (text.EndsWith('\n') ? 1 : 0);
        long words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
        long bytes = System.Text.Encoding.UTF8.GetByteCount(text);
        return (lines, words, bytes);
    }

    private static void PrintCounts(TextWriter outWriter, string text, string? label, bool lines, bool words, bool bytes)
    {
        var (l, w, c) = Count(text);
        string lineStr = lines ? $"{l,8} " : "";
        string wordStr = words ? $"{w,8} " : "";
        string byteStr = bytes ? $"{c,8} " : "";
        string lbl = label is not null ? $" {label}" : "";
        outWriter.WriteLine($"{lineStr}{wordStr}{byteStr}{lbl}");
    }

    private static string ResolvePath(string cwd, string path) =>
        path.StartsWith('/') ? path : (cwd.TrimEnd('/') + "/" + path).Replace("//", "/");
}
