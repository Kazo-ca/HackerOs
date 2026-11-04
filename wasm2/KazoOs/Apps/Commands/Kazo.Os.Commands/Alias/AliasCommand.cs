using System.Text;
using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk;
using Kazo.Os.Simulation.Abstractions.FileSystem;
using Kazo.Os.AppSdk.Globalization;
using Microsoft.Extensions.Localization;

namespace Kazo.Os.Commands.Alias;

/// <summary>
/// Implements the <c>alias</c> / <c>addalias</c> / <c>rmalias</c> / <c>unalias</c> terminal
/// command (`P4-W5-CMD-008`). Manages terminal command aliases, persisted per-user at
/// <c>~/.kazoos/aliases</c> so they survive across terminal sessions.
/// </summary>
/// <remarks>
/// <c>rmalias</c> and <c>unalias</c> are static aliases declared by the manifest (ADR 0051,
/// section 4). Like a multi-call binary, the command reads the name it was invoked as from
/// <c>argv[0]</c> and behaves as <c>alias -r</c> for those two; the shell no longer rewrites them.
/// </remarks>
public sealed class AliasCommand : TerminalAppBase
{
    /// <summary>Initializes the command with its manifest.</summary>
    public AliasCommand(AppManifest manifest) : base(manifest) { }

    /// <inheritdoc />
    public override async ValueTask<int> ExecuteAsync(
        TerminalExecutionContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        IStringLocalizer<Strings> text = context.App.GetLocalizer<Strings>();
        bool remove = InvokedAs(context) is "unalias" or "rmalias";
        List<string> positional = [];
        foreach (string arg in context.Arguments)
        {
            if (arg is "-r" or "--remove")
            {
                remove = true;
            }
            else
            {
                positional.Add(arg);
            }
        }

        string home = context.Environment.TryGetValue("HOME", out string? envHome) ? envHome : "/home/user";
        VirtualPath aliasFilePath = VirtualPath.Parse($"{home.TrimEnd('/')}/.kazoos/aliases");

        (Dictionary<string, string> aliases, long? fileRevision) = await LoadAliasesAsync(
            context, aliasFilePath, cancellationToken);

        if (remove)
        {
            if (positional.Count == 0)
            {
                context.StandardError.WriteLine(text["Unalias.Usage"]);
                return 1;
            }

            int status = 0;
            bool changed = false;
            foreach (string name in positional)
            {
                if (aliases.Remove(name))
                {
                    changed = true;
                }
                else
                {
                    context.StandardError.WriteLine(text["Unalias.NotFound", name]);
                    status = 1;
                }
            }

            if (changed && !await SaveAliasesAsync(context, aliasFilePath, home, aliases, fileRevision, cancellationToken))
            {
                context.StandardError.WriteLine(text["Unalias.SaveFailed"]);
                status = 1;
            }

            return status;
        }

        if (positional.Count == 0)
        {
            foreach ((string name, string value) in aliases.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                context.StandardOutput.WriteLine($"alias {name}='{value}'");
            }
            return 0;
        }

        int exitCode = 0;
        bool wrote = false;
        foreach (string arg in positional)
        {
            int eqIdx = arg.IndexOf('=');
            if (eqIdx > 0)
            {
                string name = arg[..eqIdx];
                string val = arg[(eqIdx + 1)..].Trim('\'', '"');
                aliases[name] = val;
                wrote = true;
            }
            else if (aliases.TryGetValue(arg, out string? val))
            {
                context.StandardOutput.WriteLine($"alias {arg}='{val}'");
            }
            else
            {
                context.StandardError.WriteLine(text["NotFound", arg]);
                exitCode = 1;
            }
        }

        if (wrote && !await SaveAliasesAsync(context, aliasFilePath, home, aliases, fileRevision, cancellationToken))
        {
            context.StandardError.WriteLine(text["SaveFailed"]);
            exitCode = 1;
        }

        return exitCode;
    }

    /// <summary>The name this process was invoked as (<c>argv[0]</c>), or <c>alias</c> when unknown.</summary>
    private static string InvokedAs(TerminalExecutionContext context)
    {
        try
        {
            IReadOnlyList<string> argv = context.App.Environment.GetCommandLineArgs();
            return argv.Count > 0 ? argv[0] : "alias";
        }
        catch (NotSupportedException)
        {
            // A context with no live process (a host-less test double): plain `alias`.
            return "alias";
        }
    }

    private static async Task<(Dictionary<string, string> Aliases, long? FileRevision)> LoadAliasesAsync(
        TerminalExecutionContext context, VirtualPath filePath, CancellationToken cancellationToken)
    {
        Dictionary<string, string> aliases = new(StringComparer.Ordinal);

        var stat = await context.App.FileSystem.StatAsync(new FileSystemStatRequest(filePath), cancellationToken);
        if (!stat.Succeeded || stat.Value is null)
        {
            return (aliases, null);
        }

        var read = await context.App.FileSystem.ReadAsync(new FileSystemReadRequest(filePath), cancellationToken);
        if (!read.Succeeded || read.Value is null)
        {
            return (aliases, stat.Value.Metadata.Revision);
        }

        using StreamReader reader = new(read.Value.Content, Encoding.UTF8);
        string content = await reader.ReadToEndAsync(cancellationToken);
        foreach (string rawLine in content.Split('\n'))
        {
            string line = rawLine.TrimEnd('\r').Trim();
            int eqIdx = line.IndexOf('=');
            if (line.Length == 0 || eqIdx <= 0)
            {
                continue;
            }
            aliases[line[..eqIdx]] = line[(eqIdx + 1)..];
        }

        return (aliases, stat.Value.Metadata.Revision);
    }

    private static async Task<bool> SaveAliasesAsync(
        TerminalExecutionContext context,
        VirtualPath filePath,
        string home,
        Dictionary<string, string> aliases,
        long? fileRevision,
        CancellationToken cancellationToken)
    {
        string content = string.Join(
            '\n', aliases.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{kv.Key}={kv.Value}"));

        if (fileRevision is { } revision)
        {
            var writeResult = await context.App.FileSystem.WriteAsync(
                new FileSystemWriteRequest(filePath, revision), new Utf8TextSource(content), cancellationToken);
            return writeResult.Succeeded;
        }

        string dirPath = GetParentPath(filePath.Value);
        var dirStat = await context.App.FileSystem.StatAsync(new FileSystemStatRequest(VirtualPath.Parse(dirPath)), cancellationToken);
        if (!dirStat.Succeeded || dirStat.Value is null)
        {
            var homeStat = await context.App.FileSystem.StatAsync(new FileSystemStatRequest(VirtualPath.Parse(home)), cancellationToken);
            if (!homeStat.Succeeded || homeStat.Value is null)
            {
                return false;
            }

            var createDirResult = await context.App.FileSystem.CreateAsync(
                new FileSystemCreateRequest(
                    VirtualPath.Parse(dirPath), FileSystemEntryKind.Directory,
                    FileSystemPermissions.FromMode(0b111_101_101), homeStat.Value.Metadata.Revision),
                cancellationToken);
            if (!createDirResult.Succeeded || createDirResult.Entry is null)
            {
                return false;
            }
            dirStat = FileSystemResult<FileSystemEntrySnapshot>.Success(createDirResult.Entry);
        }

        var createFileResult = await context.App.FileSystem.CreateAsync(
            new FileSystemCreateRequest(
                filePath, FileSystemEntryKind.File,
                FileSystemPermissions.FromMode(0b110_100_100), dirStat.Value!.Metadata.Revision),
            cancellationToken);
        if (!createFileResult.Succeeded || createFileResult.Entry is null)
        {
            return false;
        }

        var writeAfterCreate = await context.App.FileSystem.WriteAsync(
            new FileSystemWriteRequest(filePath, createFileResult.Entry.Metadata.Revision),
            new Utf8TextSource(content), cancellationToken);
        return writeAfterCreate.Succeeded;
    }

    private static string GetParentPath(string canonicalPath)
    {
        int lastSlash = canonicalPath.LastIndexOf('/');
        return lastSlash <= 0 ? "/" : canonicalPath[..lastSlash];
    }

    private sealed class Utf8TextSource(string content) : IFileSystemContentSource
    {
        private readonly byte[] _bytes = Encoding.UTF8.GetBytes(content);
        public FileSystemContentDescriptor Descriptor { get; } = FileSystemContentDescriptor.Text();
        public long? Length => _bytes.LongLength;
        public ValueTask<Stream> OpenReadAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<Stream>(new MemoryStream(_bytes, writable: false));
        }
    }
}
