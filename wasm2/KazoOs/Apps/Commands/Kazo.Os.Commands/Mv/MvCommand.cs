using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk;
using Kazo.Os.Commands.Common;
using Kazo.Os.Simulation.Abstractions.FileSystem;
using Kazo.Os.AppSdk.Globalization;
using Microsoft.Extensions.Localization;

namespace Kazo.Os.Commands.Mv;

/// <summary>
/// Implements the <c>mv</c> terminal command (`P4-W5-CMD-001`).
/// Moves or renames virtual files and directories.
/// </summary>
public sealed class MvCommand : TerminalAppBase
{
    /// <summary>Initializes the command with its manifest.</summary>
    public MvCommand(AppManifest manifest) : base(manifest) { }

    /// <inheritdoc />
    public override async ValueTask<int> ExecuteAsync(
        TerminalExecutionContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        IStringLocalizer<Strings> text = context.App.GetLocalizer<Strings>();

        var pos = context.Arguments.Where(a => !a.StartsWith('-')).ToList();
        if (pos.Count < 2)
        {
            context.StandardError.WriteLine(text["MissingDestination", pos.FirstOrDefault() ?? ""]);
            return 1;
        }

        string dest = pos[^1];
        var sources = pos.Take(pos.Count - 1).ToList();

        int status = 0;
        foreach (var src in sources)
        {
            var srcPath = ResolvePath(context.WorkingDirectory, src);
            var destPath = ResolvePath(context.WorkingDirectory, dest);

            var srcStat = await context.App.FileSystem.StatAsync(
                new FileSystemStatRequest(VirtualPath.Parse(srcPath)), cancellationToken);
            var srcParentStat = await context.App.FileSystem.StatAsync(
                new FileSystemStatRequest(VirtualPath.Parse(GetParentPath(srcPath))), cancellationToken);
            if (!srcStat.Succeeded || srcStat.Value is null || !srcParentStat.Succeeded || srcParentStat.Value is null)
            {
                context.StandardError.WriteLine(text["CannotStat", src, CommandText.Reason(context, Errno.NotFound)]);
                status = 1;
                continue;
            }

            var destParentStat = await context.App.FileSystem.StatAsync(
                new FileSystemStatRequest(VirtualPath.Parse(GetParentPath(destPath))), cancellationToken);
            if (!destParentStat.Succeeded || destParentStat.Value is null)
            {
                context.StandardError.WriteLine(text["CannotMove", src, dest, CommandText.Reason(context, Errno.NotFound)]);
                status = 1;
                continue;
            }

            var req = new FileSystemMoveRequest(
                sourcePath: VirtualPath.Parse(srcPath),
                destinationPath: VirtualPath.Parse(destPath),
                expectedEntryRevision: srcStat.Value.Metadata.Revision,
                expectedSourceParentRevision: srcParentStat.Value.Metadata.Revision,
                expectedDestinationParentRevision: destParentStat.Value.Metadata.Revision);

            var result = await context.App.FileSystem.MoveAsync(req, cancellationToken);
            if (!result.Succeeded)
            {
                context.StandardError.WriteLine(text["CannotMove", src, dest, result.Transaction.Error?.Code.ToString() ?? text["Reason.Failed"]]);
                status = 1;
            }
        }

        return status;
    }

    private static string ResolvePath(string cwd, string path) =>
        path.StartsWith('/') ? path : (cwd.TrimEnd('/') + "/" + path).Replace("//", "/");

    private static string GetParentPath(string canonicalPath)
    {
        int lastSlash = canonicalPath.LastIndexOf('/');
        return lastSlash <= 0 ? "/" : canonicalPath[..lastSlash];
    }
}
