using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk;
using Kazo.Os.AppSdk.Globalization;
using Kazo.Os.Commands.Common;
using Kazo.Os.Simulation.Abstractions.FileSystem;
using Microsoft.Extensions.Localization;

namespace Kazo.Os.Commands.Mkdir;

/// <summary>
/// Implements the <c>mkdir</c> terminal command (`P4-W5-CMD-001`), like GNU coreutils
/// <c>mkdir(1)</c>: creates each directory operand, with <c>-p</c>/<c>--parents</c> to create the
/// missing parents too and accept a directory that already exists, and <c>-v</c>/<c>--verbose</c>
/// to print one line per directory created.
/// </summary>
public sealed class MkdirCommand : TerminalAppBase
{
    /// <summary>Mode of a new directory (<c>0755</c>, the result of <c>0777</c> under umask <c>022</c>).</summary>
    private static readonly FileSystemPermissions DirectoryMode = FileSystemPermissions.FromMode(0b111_101_101);

    /// <summary>Initializes the command with its manifest.</summary>
    public MkdirCommand(AppManifest manifest) : base(manifest) { }

    /// <summary>Parses the options, then creates every operand in order; exit status 1 if any failed.</summary>
    public override async ValueTask<int> ExecuteAsync(
        TerminalExecutionContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        IStringLocalizer<Strings> text = context.App.GetLocalizer<Strings>();

        (List<string> options, List<string> paths) = CommandIO.SplitArguments(context.Arguments);
        bool parents = false;
        bool verbose = false;
        foreach (string option in options)
        {
            switch (option)
            {
                case "--parents":
                    parents = true;
                    continue;
                case "--verbose":
                    verbose = true;
                    continue;
            }

            if (option.StartsWith("--", StringComparison.Ordinal))
            {
                context.StandardError.WriteLine(text["UnrecognizedOption", option]);
                return 1;
            }

            // A cluster of short flags, as in "-pv".
            foreach (char flag in option.AsSpan(1))
            {
                switch (flag)
                {
                    case 'p':
                        parents = true;
                        break;
                    case 'v':
                        verbose = true;
                        break;
                    default:
                        context.StandardError.WriteLine(text["InvalidOption", flag]);
                        return 1;
                }
            }
        }

        if (paths.Count == 0)
        {
            context.StandardError.WriteLine(text["MissingOperand"]);
            return 1;
        }

        int status = 0;
        foreach (string path in paths)
        {
            VirtualPath target;
            try
            {
                target = VirtualPath.Parse(CommandIO.ResolvePath(context.WorkingDirectory, path));
            }
            catch (FormatException)
            {
                context.StandardError.WriteLine(text["CannotCreate", path, CommandText.Reason(context, Errno.InvalidArgument)]);
                status = 1;
                continue;
            }

            bool created = parents
                ? await CreateWithParentsAsync(context, path, target, verbose, cancellationToken)
                : await CreateOneAsync(context, path, target, verbose, cancellationToken);
            if (!created)
            {
                status = 1;
            }
        }

        return status;
    }

    /// <summary>
    /// <c>mkdir path</c>: the parent must exist and the directory must not. The parent's revision is
    /// passed with the create, so a concurrent change to the parent fails instead of racing.
    /// </summary>
    private static async Task<bool> CreateOneAsync(
        TerminalExecutionContext context,
        string operand,
        VirtualPath target,
        bool verbose,
        CancellationToken cancellationToken)
    {
        string parentPath = GetParentPath(target.Value);
        var parentStat = await context.App.FileSystem.StatAsync(
            new FileSystemStatRequest(VirtualPath.Parse(parentPath)), cancellationToken);
        if (!parentStat.Succeeded || parentStat.Value is null)
        {
            context.StandardError.WriteLine(Text(context)["CannotCreate", operand, CommandText.Reason(context, Errno.NotFound)]);
            return false;
        }

        return await CreateDirectoryAsync(context, operand, operand, target, parentStat.Value.Metadata.Revision, verbose, cancellationToken);
    }

    /// <summary>
    /// <c>mkdir -p path</c>: walks the path from the root, creating each missing component like
    /// coreutils does (one <c>mkdir(2)</c> per component). An existing directory anywhere on the
    /// path, the target included, is not an error; an existing file is ("File exists" for the
    /// target, "Not a directory" for a component in the middle).
    /// </summary>
    private static async Task<bool> CreateWithParentsAsync(
        TerminalExecutionContext context,
        string operand,
        VirtualPath target,
        bool verbose,
        CancellationToken cancellationToken)
    {
        string[] segments = target.Value.Split('/', StringSplitOptions.RemoveEmptyEntries);
        string current = string.Empty;
        string parent = "/";
        long? parentRevision = null;
        for (int i = 0; i < segments.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            current += "/" + segments[i];
            bool isTarget = i == segments.Length - 1;

            var stat = await context.App.FileSystem.StatAsync(
                new FileSystemStatRequest(VirtualPath.Parse(current)), cancellationToken);
            if (stat.Succeeded && stat.Value is not null)
            {
                if (stat.Value.Metadata.Kind != FileSystemEntryKind.Directory)
                {
                    IStringLocalizer<Strings> text = Text(context);
                    string reason = isTarget ? CommandText.Reason(context, Errno.FileExists) : CommandText.Reason(context, Errno.NotDirectory);
                    context.StandardError.WriteLine(text["CannotCreate", operand, reason]);
                    return false;
                }

                parent = current;
                parentRevision = stat.Value.Metadata.Revision;
                continue;
            }

            if (parentRevision is null)
            {
                // The root always exists; only reached when the walk starts on a missing component.
                var parentStat = await context.App.FileSystem.StatAsync(
                    new FileSystemStatRequest(VirtualPath.Parse(parent)), cancellationToken);
                if (!parentStat.Succeeded || parentStat.Value is null)
                {
                    context.StandardError.WriteLine(Text(context)["CannotCreate", operand, CommandText.Reason(context, Errno.NotFound)]);
                    return false;
                }

                parentRevision = parentStat.Value.Metadata.Revision;
            }

            // Messages name the component, as GNU mkdir -pv prints each directory it creates.
            string display = DisplayName(operand, target.Value, current);
            if (!await CreateDirectoryAsync(context, operand, display, VirtualPath.Parse(current), parentRevision.Value, verbose, cancellationToken))
            {
                return false;
            }

            // The new directory is the next parent; read its revision for the next create.
            var created = await context.App.FileSystem.StatAsync(
                new FileSystemStatRequest(VirtualPath.Parse(current)), cancellationToken);
            parent = current;
            parentRevision = created.Succeeded && created.Value is not null ? created.Value.Metadata.Revision : null;
        }

        return true;
    }

    /// <summary>Creates one directory and reports the outcome like coreutils.</summary>
    private static async Task<bool> CreateDirectoryAsync(
        TerminalExecutionContext context,
        string operand,
        string display,
        VirtualPath path,
        long expectedParentRevision,
        bool verbose,
        CancellationToken cancellationToken)
    {
        var request = new FileSystemCreateRequest(
            path: path,
            kind: FileSystemEntryKind.Directory,
            permissions: DirectoryMode,
            expectedParentRevision: expectedParentRevision);

        var result = await context.App.FileSystem.CreateAsync(request, cancellationToken);
        if (!result.Succeeded)
        {
            IStringLocalizer<Strings> text = Text(context);
            context.StandardError.WriteLine(text["CannotCreate", operand, Describe(context, text, result.Transaction.Error?.Code)]);
            return false;
        }

        if (verbose)
        {
            context.StandardOutput.WriteLine(Text(context)["Created", display]);
        }

        return true;
    }

    /// <summary>The texts of the command, in the language of its process (ADR 0104).</summary>
    private static IStringLocalizer<Strings> Text(TerminalExecutionContext context) => context.App.GetLocalizer<Strings>();

    /// <summary>The <c>strerror</c> text for the usual failures, translated like glibc's; other codes keep their name.</summary>
    private static string Describe(TerminalExecutionContext context, IStringLocalizer<Strings> text, FileSystemErrorCode? code) => code switch
    {
        FileSystemErrorCode.AlreadyExists => CommandText.Reason(context, Errno.FileExists),
        FileSystemErrorCode.NotFound => CommandText.Reason(context, Errno.NotFound),
        FileSystemErrorCode.NoSpace => CommandText.Reason(context, Errno.NoSpace),
        FileSystemErrorCode.NotDirectory => CommandText.Reason(context, Errno.NotDirectory),
        FileSystemErrorCode.PermissionDenied or FileSystemErrorCode.CapabilityDenied => CommandText.Reason(context, Errno.PermissionDenied),
        null => text["Reason.Failed"],
        _ => code.Value.ToString(),
    };

    /// <summary>
    /// Shows a created component the way the user typed the operand: the operand itself for the
    /// target, its matching prefix for a parent (<c>a</c>, then <c>a/b</c> for <c>mkdir -pv a/b</c>).
    /// </summary>
    private static string DisplayName(string operand, string target, string component)
    {
        if (component == target)
        {
            return operand;
        }

        // Count the components still to go and drop as many from the end of the operand.
        int remaining = target[component.Length..].Split('/', StringSplitOptions.RemoveEmptyEntries).Length;
        string[] typed = operand.TrimEnd('/').Split('/');
        if (remaining >= typed.Length || operand.Contains("..", StringComparison.Ordinal))
        {
            return component;
        }

        string prefix = string.Join('/', typed[..^remaining]);
        return prefix.Length == 0 ? component : prefix;
    }

    private static string GetParentPath(string canonicalPath)
    {
        int lastSlash = canonicalPath.LastIndexOf('/');
        return lastSlash <= 0 ? "/" : canonicalPath[..lastSlash];
    }
}
