using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk;
using Kazo.Os.Commands.Common;
using Kazo.Os.Simulation.Abstractions.FileSystem;
using Kazo.Os.AppSdk.Globalization;
using Microsoft.Extensions.Localization;

namespace Kazo.Os.Commands.Chmod;

/// <summary>
/// Implements the <c>chmod</c> terminal command (`P4-W5-CMD-002`, symbolic modes since ADR 0064).
/// Changes the permission bits of filesystem entries, like coreutils:
/// octal modes (<c>755</c>) and symbolic modes (<c>+x</c>, <c>u+x</c>, <c>go-w</c>,
/// <c>a=r</c>, <c>u=rwx,g=rx,o=</c>, <c>g+u</c>), <c>-R</c> to recurse, <c>-v</c>/<c>-c</c> to
/// report changes, <c>-f</c> to stay quiet. <c>+x</c> without a "who" letter applies to all
/// classes, as the default umask <c>022</c> lets it (write bits keep the umask: <c>+w</c> only
/// adds the owner's).
/// </summary>
public sealed class ChmodCommand : TerminalAppBase
{
    /// <summary>The default file creation mask, applied to symbolic modes without a "who" letter.</summary>
    private const ushort Umask = 0b000_010_010;

    /// <summary>Initializes the command with its manifest.</summary>
    public ChmodCommand(AppManifest manifest) : base(manifest) { }

    /// <inheritdoc />
    public override async ValueTask<int> ExecuteAsync(
        TerminalExecutionContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        IStringLocalizer<Strings> text = context.App.GetLocalizer<Strings>();

        bool recursive = false, verbose = false, changesOnly = false, quiet = false;
        List<string> positional = [];
        foreach (string argument in context.Arguments)
        {
            // A mode such as -w or -x is a mode, not an option (coreutils accepts both).
            if (argument.Length > 1 && argument[0] == '-' && argument[1..].All(ch => ch is 'R' or 'v' or 'c' or 'f'))
            {
                recursive |= argument.Contains('R');
                verbose |= argument.Contains('v');
                changesOnly |= argument.Contains('c');
                quiet |= argument.Contains('f');
                continue;
            }

            if (argument is "--recursive" or "--verbose" or "--changes" or "--silent" or "--quiet")
            {
                recursive |= argument == "--recursive";
                verbose |= argument == "--verbose";
                changesOnly |= argument == "--changes";
                quiet |= argument is "--silent" or "--quiet";
                continue;
            }

            positional.Add(argument);
        }

        if (positional.Count < 2)
        {
            context.StandardError.WriteLine(positional.Count == 0 ? text["MissingOperand"] : text["MissingOperandAfter", positional[0]]);
            return 1;
        }

        string modeText = positional[0];
        if (ParseMode(modeText, 0) is null)
        {
            context.StandardError.WriteLine(text["InvalidMode", modeText]);
            return 1;
        }

        int status = 0;
        foreach (string target in positional.Skip(1))
        {
            string resolved = ResolvePath(context.WorkingDirectory, target);
            status |= await ChangeAsync(context, target, resolved, modeText, recursive, verbose, changesOnly, quiet, cancellationToken);
        }

        return status;
    }

    /// <summary>Changes one entry (and its children with <c>-R</c>); returns 1 on any failure.</summary>
    private static async Task<int> ChangeAsync(
        TerminalExecutionContext context,
        string display,
        string path,
        string modeText,
        bool recursive,
        bool verbose,
        bool changesOnly,
        bool quiet,
        CancellationToken cancellationToken)
    {
        IStringLocalizer<Strings> text = context.App.GetLocalizer<Strings>();
        FileSystemResult<FileSystemEntrySnapshot> entryStat = await context.App.FileSystem.StatAsync(
            new FileSystemStatRequest(VirtualPath.Parse(path)), cancellationToken);
        if (!entryStat.Succeeded || entryStat.Value is null)
        {
            if (!quiet)
            {
                context.StandardError.WriteLine(text["CannotAccess", display, CommandText.Reason(context, Errno.NotFound)]);
            }

            return 1;
        }

        FileSystemEntryMetadata metadata = entryStat.Value.Metadata;
        ushort current = metadata.Permissions.Mode;
        bool isDirectory = metadata.Kind == FileSystemEntryKind.Directory;
        ushort updated = ParseMode(modeText, current, isDirectory)!.Value;
        int status = 0;
        if (updated != current)
        {
            FileSystemSetPermissionsRequest request = new(
                path: VirtualPath.Parse(path),
                permissions: FileSystemPermissions.FromMode(updated),
                expectedRevision: metadata.Revision);
            FileSystemMutationResult result = await context.App.FileSystem.SetPermissionsAsync(request, cancellationToken);
            if (!result.Succeeded)
            {
                if (!quiet)
                {
                    context.StandardError.WriteLine(text["ChangeFailed", display, Describe(context, text, result.Transaction.Error?.Code)]);
                }

                return 1;
            }
        }

        if (verbose || (changesOnly && updated != current))
        {
            context.StandardOutput.WriteLine(updated == current
                ? text["Retained", display, Octal(current), Symbolic(current, isDirectory)]
                : text["Changed", display, Octal(current), Symbolic(current, isDirectory), Octal(updated), Symbolic(updated, isDirectory)]);
        }

        if (recursive && isDirectory)
        {
            FileSystemResult<FileSystemDirectorySnapshot> listing = await context.App.FileSystem.EnumerateAsync(
                new FileSystemEnumerateRequest(VirtualPath.Parse(path)), cancellationToken);
            if (!listing.Succeeded || listing.Value is null)
            {
                context.StandardError.WriteLine(text["CannotReadDirectory", display, CommandText.Reason(context, Errno.PermissionDenied)]);
                return 1;
            }

            foreach (FileSystemDirectoryItem child in listing.Value.Entries)
            {
                if (child.Metadata.Kind == FileSystemEntryKind.SymbolicLink)
                {
                    // chmod -R never follows symbolic links met during the walk.
                    continue;
                }

                string childName = child.Name.Value;
                status |= await ChangeAsync(
                    context,
                    display.TrimEnd('/') + "/" + childName,
                    path.TrimEnd('/') + "/" + childName,
                    modeText, recursive, verbose, changesOnly, quiet, cancellationToken);
            }
        }

        return status;
    }

    /// <summary>
    /// Applies an octal or symbolic mode to <paramref name="current"/>; <see langword="null"/>
    /// when the text is not a valid mode. <c>X</c> sets execute only on directories or on entries
    /// that already have an execute bit.
    /// </summary>
    internal static ushort? ParseMode(string text, ushort current, bool isDirectory = false)
    {
        if (text.Length > 0 && text.All(ch => ch is >= '0' and <= '7'))
        {
            if (text.Length > 4)
            {
                return null;
            }

            // Set-id and sticky bits (a fourth digit) are accepted and ignored: the filesystem has none.
            return (ushort)(Convert.ToUInt16(text, 8) & 0x1FF);
        }

        ushort mode = current;
        foreach (string clause in text.Split(','))
        {
            int index = 0;
            int who = 0;
            while (index < clause.Length && clause[index] is 'u' or 'g' or 'o' or 'a')
            {
                who |= clause[index] switch
                {
                    'u' => 0b111_000_000,
                    'g' => 0b000_111_000,
                    'o' => 0b000_000_111,
                    _ => 0b111_111_111
                };
                index++;
            }

            bool implicitWho = who == 0;
            if (implicitWho)
            {
                who = 0b111_111_111;
            }

            if (index >= clause.Length)
            {
                return null;
            }

            while (index < clause.Length)
            {
                char op = clause[index++];
                if (op is not ('+' or '-' or '='))
                {
                    return null;
                }

                int bits = 0;
                while (index < clause.Length && clause[index] is not ('+' or '-' or '='))
                {
                    char permission = clause[index++];
                    bits |= permission switch
                    {
                        'r' => 0b100_100_100,
                        'w' => 0b010_010_010,
                        'x' => 0b001_001_001,
                        'X' => isDirectory || (mode & 0b001_001_001) != 0 ? 0b001_001_001 : 0,
                        'u' => Spread((mode >> 6) & 7),
                        'g' => Spread((mode >> 3) & 7),
                        'o' => Spread(mode & 7),
                        's' or 't' => 0,
                        _ => -1
                    };
                    if (bits < 0)
                    {
                        return null;
                    }
                }

                int mask = who & bits;
                if (implicitWho && op != '-')
                {
                    // Without u/g/o/a, the umask protects the bits it hides (+w is the owner's only).
                    mask &= ~Umask;
                }

                mode = op switch
                {
                    '+' => (ushort)(mode | mask),
                    '-' => (ushort)(mode & ~mask),
                    _ => (ushort)((mode & ~who) | mask)
                };
            }
        }

        return (ushort)(mode & 0x1FF);
    }

    /// <summary>Copies one class's three bits into all three classes (for <c>g+u</c>).</summary>
    private static int Spread(int bits) => (bits << 6) | (bits << 3) | bits;

    private static string Octal(ushort mode) => Convert.ToString(mode, 8).PadLeft(4, '0');

    private static string Symbolic(ushort mode, bool isDirectory)
    {
        char[] text = new char[10];
        text[0] = isDirectory ? 'd' : '-';
        string letters = "rwxrwxrwx";
        for (int i = 0; i < 9; i++)
        {
            text[i + 1] = (mode & (1 << (8 - i))) != 0 ? letters[i] : '-';
        }

        return new string(text);
    }

    private static string Describe(TerminalExecutionContext context, IStringLocalizer<Strings> text, FileSystemErrorCode? code) => code switch
    {
        FileSystemErrorCode.PermissionDenied => CommandText.Reason(context, Errno.NotPermitted),
        FileSystemErrorCode.NotFound => CommandText.Reason(context, Errno.NotFound),
        FileSystemErrorCode.NoSpace => CommandText.Reason(context, Errno.NoSpace),
        null => text["OperationFailed"],
        _ => code.Value.ToString()
    };

    private static string ResolvePath(string cwd, string path) =>
        path.StartsWith('/') ? path : (cwd.TrimEnd('/') + "/" + path).Replace("//", "/");
}
