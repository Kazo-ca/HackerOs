using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk;
using Kazo.Os.Commands.Common;
using Kazo.Os.AppSdk.IO;
using Kazo.Os.Simulation.Abstractions.FileSystem;
using Kazo.Os.AppSdk.Globalization;
using Microsoft.Extensions.Localization;

namespace Kazo.Os.Commands.Rm;

/// <summary>
/// Implements the <c>rm</c> terminal command (`P4-W5-CMD-001`, ADR 0052 section 4).
/// </summary>
/// <remarks>
/// By default every operand goes to the effective user's trash (<see cref="HackTrash"/>), so a
/// deletion can be undone with <c>trash restore</c>. <c>--permanent</c> deletes for good, as
/// <c>rm</c> always did before. A directory needs <c>-r</c> and then goes to the trash whole.
/// <c>-f</c> ignores missing operands. Output is silent on success, like GNU <c>rm</c>.
/// </remarks>
public sealed class RmCommand : TerminalAppBase
{
    /// <summary>Initializes the command with its manifest.</summary>
    public RmCommand(AppManifest manifest) : base(manifest) { }

    /// <inheritdoc />
    public override async ValueTask<int> ExecuteAsync(
        TerminalExecutionContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        IStringLocalizer<Strings> text = context.App.GetLocalizer<Strings>();

        if (!RmOptions.TryParse(context.Arguments, text, out RmOptions options, out string? usageError))
        {
            context.StandardError.WriteLine(text["Error", usageError!]);
            return 1;
        }

        if (options.Targets.Count == 0)
        {
            if (options.Force)
            {
                return 0;
            }

            context.StandardError.WriteLine(text["MissingOperand"]);
            return 1;
        }

        int status = 0;
        foreach (string target in options.Targets)
        {
            if (!await RemoveAsync(context, target, options, cancellationToken))
            {
                status = 1;
            }
        }

        return status;
    }

    /// <summary>Removes one operand; reports its own error and returns <see langword="false"/> on failure.</summary>
    internal static async Task<bool> RemoveAsync(
        TerminalExecutionContext context, string target, RmOptions options, CancellationToken cancellationToken)
    {
        IStringLocalizer<Strings> text = context.App.GetLocalizer<Strings>();
        string resolved = ResolvePath(context.WorkingDirectory, target);
        var entryStat = await context.App.FileSystem.StatAsync(
            new FileSystemStatRequest(VirtualPath.Parse(resolved), FileSystemLinkBehavior.NoFollow), cancellationToken);
        if (!entryStat.Succeeded || entryStat.Value is null)
        {
            if (entryStat.Error?.Code == FileSystemErrorCode.NotFound)
            {
                if (options.Force)
                {
                    return true;
                }

                context.StandardError.WriteLine(text["CannotRemove", target, CommandText.Reason(context, Errno.NotFound)]);
                return false;
            }

            context.StandardError.WriteLine(text["CannotRemove", target, entryStat.Error?.Code.ToString() ?? string.Empty]);
            return false;
        }

        if (entryStat.Value.Metadata.Kind == FileSystemEntryKind.Directory && !options.Recursive)
        {
            context.StandardError.WriteLine(text["CannotRemove", target, CommandText.Reason(context, Errno.IsDirectory)]);
            return false;
        }

        return options.Permanent
            ? await DeletePermanentlyAsync(context, target, resolved, entryStat.Value, cancellationToken)
            : await MoveToTrashAsync(context, target, resolved, cancellationToken);
    }

    private static async Task<bool> MoveToTrashAsync(
        TerminalExecutionContext context, string target, string resolved, CancellationToken cancellationToken)
    {
        IStringLocalizer<Strings> text = context.App.GetLocalizer<Strings>();
        try
        {
            await HackTrash.MoveToTrashAsync(resolved, cancellationToken);
            return true;
        }
        catch (HackFileNotFoundException)
        {
            // Vanished between our stat and the move: same outcome as a missing operand.
            context.StandardError.WriteLine(text["CannotRemove", target, CommandText.Reason(context, Errno.NotFound)]);
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            context.StandardError.WriteLine(text["CannotRemove", target, CommandText.Reason(context, Errno.PermissionDenied)]);
            return false;
        }
        catch (IOException exception)
        {
            context.StandardError.WriteLine(text["CannotRemove", target, exception.Message]);
            return false;
        }
    }

    /// <summary>The pre-trash behavior: one engine delete at the revisions just observed.</summary>
    private static async Task<bool> DeletePermanentlyAsync(
        TerminalExecutionContext context,
        string target,
        string resolved,
        FileSystemEntrySnapshot entry,
        CancellationToken cancellationToken)
    {
        IStringLocalizer<Strings> text = context.App.GetLocalizer<Strings>();
        var parentStat = await context.App.FileSystem.StatAsync(
            new FileSystemStatRequest(VirtualPath.Parse(GetParentPath(resolved))), cancellationToken);
        if (!parentStat.Succeeded || parentStat.Value is null)
        {
            context.StandardError.WriteLine(text["CannotRemove", target, CommandText.Reason(context, Errno.NotFound)]);
            return false;
        }

        var result = await context.App.FileSystem.DeleteAsync(
            new FileSystemDeleteRequest(
                path: VirtualPath.Parse(resolved),
                expectedEntryRevision: entry.Metadata.Revision,
                expectedParentRevision: parentStat.Value.Metadata.Revision,
                recursive: true),
            cancellationToken);
        if (result.Succeeded)
        {
            return true;
        }

        FileSystemErrorCode? code = result.Transaction.Error?.Code;
        string reason = code is FileSystemErrorCode.PermissionDenied or FileSystemErrorCode.CapabilityDenied or FileSystemErrorCode.AuthorityDenied
            ? CommandText.Reason(context, Errno.PermissionDenied)
            : code?.ToString() ?? text["Reason.Unknown"];
        context.StandardError.WriteLine(text["CannotRemove", target, reason]);
        return false;
    }

    private static string ResolvePath(string cwd, string path) =>
        VirtualPath.Parse(path.StartsWith('/') ? path : cwd.TrimEnd('/') + "/" + path).Value;

    private static string GetParentPath(string canonicalPath)
    {
        int lastSlash = canonicalPath.LastIndexOf('/');
        return lastSlash <= 0 ? "/" : canonicalPath[..lastSlash];
    }
}

/// <summary>Parsed <c>rm</c> command line; also used by <c>trash put</c>.</summary>
/// <param name="Recursive"><c>-r</c>, <c>-R</c> or <c>--recursive</c>.</param>
/// <param name="Force"><c>-f</c> or <c>--force</c>: ignore missing operands, never fail on zero operands.</param>
/// <param name="Permanent"><c>--permanent</c>: bypass the trash.</param>
/// <param name="Targets">Operands, in order.</param>
internal sealed record RmOptions(bool Recursive, bool Force, bool Permanent, IReadOnlyList<string> Targets)
{
    /// <summary>
    /// Parses GNU-style options: grouped short flags (<c>-rf</c>) and <c>--</c> to end options.
    /// <paramref name="text"/> words <paramref name="error"/> in the language of the calling process.
    /// </summary>
    public static bool TryParse(IReadOnlyList<string> arguments, IStringLocalizer<Strings> text, out RmOptions options, out string? error)
    {
        bool recursive = false, force = false, permanent = false, endOfOptions = false;
        List<string> targets = [];
        options = new RmOptions(false, false, false, []);
        error = null;

        foreach (string argument in arguments)
        {
            if (endOfOptions || argument == "-" || !argument.StartsWith('-'))
            {
                targets.Add(argument);
                continue;
            }

            switch (argument)
            {
                case "--":
                    endOfOptions = true;
                    continue;
                case "--recursive":
                    recursive = true;
                    continue;
                case "--force":
                    force = true;
                    continue;
                case "--permanent":
                    permanent = true;
                    continue;
            }

            if (argument.StartsWith("--", StringComparison.Ordinal))
            {
                error = text["UnrecognizedOption", argument];
                return false;
            }

            foreach (char flag in argument.AsSpan(1))
            {
                switch (flag)
                {
                    case 'r' or 'R':
                        recursive = true;
                        break;
                    case 'f':
                        force = true;
                        break;
                    default:
                        error = text["InvalidOption", flag];
                        return false;
                }
            }
        }

        options = new RmOptions(recursive, force, permanent, targets);
        return true;
    }
}
