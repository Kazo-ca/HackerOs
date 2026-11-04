using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk;
using Kazo.Os.Commands.Common;
using Kazo.Os.Simulation.Abstractions.FileSystem;
using Kazo.Os.AppSdk.Globalization;
using Microsoft.Extensions.Localization;

namespace Kazo.Os.Commands.Diff;

/// <summary>
/// Implements the <c>diff</c> terminal command (`P4-W5-CMD-003`).
/// Compares two virtual text files line by line. Supports -u (unified diff).
/// </summary>
public sealed class DiffCommand : TerminalAppBase
{
    /// <summary>Initializes the command with its manifest.</summary>
    public DiffCommand(AppManifest manifest) : base(manifest) { }

    /// <inheritdoc />
    public override async ValueTask<int> ExecuteAsync(
        TerminalExecutionContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        bool unified = false;
        List<string> files = [];

        foreach (var a in context.Arguments)
        {
            if (a is "-u" or "--unified") unified = true;
            else if (!a.StartsWith('-')) files.Add(a);
        }

        if (files.Count < 2)
        {
            context.StandardError.WriteLine(context.App.GetLocalizer<Strings>()["MissingOperandAfter", files.FirstOrDefault() ?? ""]);
            return 2;
        }

        var path1 = ResolvePath(context.WorkingDirectory, files[0]);
        var path2 = ResolvePath(context.WorkingDirectory, files[1]);

        var res1 = await context.App.FileSystem.ReadAsync(new FileSystemReadRequest(VirtualPath.Parse(path1)), cancellationToken);
        var res2 = await context.App.FileSystem.ReadAsync(new FileSystemReadRequest(VirtualPath.Parse(path2)), cancellationToken);

        if (!res1.Succeeded || res1.Value is null)
        {
            context.StandardError.WriteLine(CommandText.FileError(context, "diff", files[0], CommandText.Reason(context, Errno.NotFound)));
            return 2;
        }
        if (!res2.Succeeded || res2.Value is null)
        {
            context.StandardError.WriteLine(CommandText.FileError(context, "diff", files[1], CommandText.Reason(context, Errno.NotFound)));
            return 2;
        }

        using var reader1 = new StreamReader(res1.Value.Content);
        using var reader2 = new StreamReader(res2.Value.Content);

        string[] lines1 = TrimTrailingEmptyLine(
            (await reader1.ReadToEndAsync(cancellationToken)).Split('\n').Select(l => l.TrimEnd('\r')).ToArray());
        string[] lines2 = TrimTrailingEmptyLine(
            (await reader2.ReadToEndAsync(cancellationToken)).Split('\n').Select(l => l.TrimEnd('\r')).ToArray());

        List<DiffHunk> hunks = ComputeHunks(lines1, lines2);
        if (hunks.Count == 0)
        {
            return 0;
        }

        if (unified)
        {
            context.StandardOutput.WriteLine($"--- {files[0]}");
            context.StandardOutput.WriteLine($"+++ {files[1]}");
        }

        foreach (DiffHunk hunk in hunks)
        {
            if (unified)
            {
                int a1 = hunk.RemovedCount > 0 ? hunk.StartA + 1 : hunk.StartA;
                int b1 = hunk.AddedCount > 0 ? hunk.StartB + 1 : hunk.StartB;
                context.StandardOutput.WriteLine(
                    $"@@ -{FormatUnifiedRange(a1, hunk.RemovedCount)} +{FormatUnifiedRange(b1, hunk.AddedCount)} @@");
                foreach (string removed in hunk.Removed)
                {
                    context.StandardOutput.WriteLine($"-{removed}");
                }
                foreach (string added in hunk.Added)
                {
                    context.StandardOutput.WriteLine($"+{added}");
                }
            }
            else if (hunk.RemovedCount > 0 && hunk.AddedCount > 0)
            {
                context.StandardOutput.WriteLine(
                    $"{FormatRange(hunk.StartA + 1, hunk.StartA + hunk.RemovedCount)}c{FormatRange(hunk.StartB + 1, hunk.StartB + hunk.AddedCount)}");
                foreach (string removed in hunk.Removed)
                {
                    context.StandardOutput.WriteLine($"< {removed}");
                }
                context.StandardOutput.WriteLine("---");
                foreach (string added in hunk.Added)
                {
                    context.StandardOutput.WriteLine($"> {added}");
                }
            }
            else if (hunk.RemovedCount > 0)
            {
                context.StandardOutput.WriteLine(
                    $"{FormatRange(hunk.StartA + 1, hunk.StartA + hunk.RemovedCount)}d{hunk.StartB}");
                foreach (string removed in hunk.Removed)
                {
                    context.StandardOutput.WriteLine($"< {removed}");
                }
            }
            else
            {
                context.StandardOutput.WriteLine(
                    $"{hunk.StartA}a{FormatRange(hunk.StartB + 1, hunk.StartB + hunk.AddedCount)}");
                foreach (string added in hunk.Added)
                {
                    context.StandardOutput.WriteLine($"> {added}");
                }
            }
        }

        return 1;
    }

    private static string ResolvePath(string cwd, string path) =>
        path.StartsWith('/') ? path : (cwd.TrimEnd('/') + "/" + path).Replace("//", "/");

    private static string[] TrimTrailingEmptyLine(string[] lines) =>
        lines.Length > 0 && lines[^1].Length == 0 ? lines[..^1] : lines;

    private static string FormatRange(int start, int end) => start == end ? $"{start}" : $"{start},{end}";

    private static string FormatUnifiedRange(int start, int count) =>
        count == 1 ? $"{start}" : $"{start},{count}";

    private enum DiffOpKind { Equal, Delete, Insert }

    private readonly record struct DiffOp(DiffOpKind Kind);

    private sealed record DiffHunk(int StartA, int StartB, List<string> Removed, List<string> Added)
    {
        public int RemovedCount => Removed.Count;
        public int AddedCount => Added.Count;
    }

    /// <summary>
    /// Computes a minimal edit script via longest-common-subsequence backtracking, then groups
    /// consecutive insert/delete operations into hunks, matching classic <c>diff</c> semantics
    /// instead of comparing files by raw line index.
    /// </summary>
    private static List<DiffHunk> ComputeHunks(string[] a, string[] b)
    {
        int n = a.Length, m = b.Length;
        int[,] lcs = new int[n + 1, m + 1];
        for (int i = n - 1; i >= 0; i--)
        {
            for (int j = m - 1; j >= 0; j--)
            {
                lcs[i, j] = a[i] == b[j] ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
            }
        }

        List<DiffOp> ops = [];
        int x = 0, y = 0;
        while (x < n && y < m)
        {
            if (a[x] == b[y])
            {
                ops.Add(new DiffOp(DiffOpKind.Equal));
                x++;
                y++;
            }
            else if (lcs[x + 1, y] >= lcs[x, y + 1])
            {
                ops.Add(new DiffOp(DiffOpKind.Delete));
                x++;
            }
            else
            {
                ops.Add(new DiffOp(DiffOpKind.Insert));
                y++;
            }
        }
        while (x < n) { ops.Add(new DiffOp(DiffOpKind.Delete)); x++; }
        while (y < m) { ops.Add(new DiffOp(DiffOpKind.Insert)); y++; }

        List<DiffHunk> hunks = [];
        int aPos = 0, bPos = 0, opIndex = 0;
        while (opIndex < ops.Count)
        {
            if (ops[opIndex].Kind == DiffOpKind.Equal)
            {
                aPos++;
                bPos++;
                opIndex++;
                continue;
            }

            int startA = aPos, startB = bPos;
            List<string> removed = [];
            List<string> added = [];
            while (opIndex < ops.Count && ops[opIndex].Kind != DiffOpKind.Equal)
            {
                if (ops[opIndex].Kind == DiffOpKind.Delete)
                {
                    removed.Add(a[aPos]);
                    aPos++;
                }
                else
                {
                    added.Add(b[bPos]);
                    bPos++;
                }
                opIndex++;
            }
            hunks.Add(new DiffHunk(startA, startB, removed, added));
        }

        return hunks;
    }
}
