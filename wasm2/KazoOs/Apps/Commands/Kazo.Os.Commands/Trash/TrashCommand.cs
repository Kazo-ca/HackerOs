using System.Globalization;
using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk;
using Kazo.Os.AppSdk.IO;
using Kazo.Os.Commands.Rm;
using Kazo.Os.AppSdk.Globalization;
using Microsoft.Extensions.Localization;

namespace Kazo.Os.Commands.Trash;

/// <summary>
/// Implements the <c>trash</c> terminal command (ADR 0052 section 4), modelled on <c>trash-cli</c>:
/// <list type="bullet">
/// <item><c>trash put [-f] &lt;file...&gt;</c> — moves files or whole directories to the trash (no <c>-r</c> needed, like <c>trash-put</c>);</item>
/// <item><c>trash list</c> — one line per element, <c>DeletionDate  Path</c>, oldest first;</item>
/// <item><c>trash restore &lt;name|original path&gt; [destination]</c> — the most recent match when a path was trashed several times;</item>
/// <item><c>trash rm &lt;name...&gt;</c> — deletes elements permanently;</item>
/// <item><c>trash empty</c> — empties the trash.</item>
/// </list>
/// Every subcommand goes through <see cref="KazoTrash"/>, so it acts on the effective user's trash
/// (<c>/root/.local/share/Trash</c> under <c>sudo</c>).
/// </summary>
public sealed class TrashCommand : TerminalAppBase
{
    /// <summary>Initializes the command with its manifest.</summary>
    public TrashCommand(AppManifest manifest) : base(manifest) { }

    /// <inheritdoc />
    public override async ValueTask<int> ExecuteAsync(TerminalExecutionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        IStringLocalizer<Strings> text = Text(context);
        if (context.Arguments.Count == 0)
        {
            context.StandardError.WriteLine(text["Usage"]);
            return 1;
        }

        string[] rest = [.. context.Arguments.Skip(1)];
        try
        {
            return context.Arguments[0] switch
            {
                "put" => await PutAsync(context, rest, cancellationToken),
                "list" or "ls" => await ListAsync(context, rest, cancellationToken),
                "restore" => await RestoreAsync(context, rest, cancellationToken),
                "rm" => await RemoveAsync(context, rest, cancellationToken),
                "empty" => await EmptyAsync(context, rest, cancellationToken),
                "--help" or "-h" or "help" => PrintUsage(context),
                string other => Fail(context, text["UnknownSubcommand", other] + "\n" + text["Usage"])
            };
        }
        catch (UnauthorizedAccessException)
        {
            return Fail(context, text["PermissionDenied"]);
        }
        catch (IOException exception)
        {
            return Fail(context, exception.Message);
        }
    }

    private static async Task<int> PutAsync(TerminalExecutionContext context, string[] arguments, CancellationToken cancellationToken)
    {
        IStringLocalizer<Strings> text = Text(context);
        if (!RmOptions.TryParse(arguments, context.App.GetLocalizer<Rm.Strings>(), out RmOptions options, out string? error))
        {
            return Fail(context, text["Put.Error", error!]);
        }

        if (options.Permanent)
        {
            return Fail(context, text["Put.Permanent"]);
        }

        if (options.Targets.Count == 0)
        {
            return options.Force ? 0 : Fail(context, text["Put.MissingOperand"]);
        }

        // trash-put sends directories without asking for -r.
        RmOptions trashOptions = options with { Recursive = true };
        int status = 0;
        foreach (string target in options.Targets)
        {
            if (!await RmCommand.RemoveAsync(context, target, trashOptions, cancellationToken))
            {
                status = 1;
            }
        }

        return status;
    }

    private static async Task<int> ListAsync(TerminalExecutionContext context, string[] arguments, CancellationToken cancellationToken)
    {
        IStringLocalizer<Strings> text = Text(context);
        if (arguments.Length > 0)
        {
            return Fail(context, text["List.TooManyArguments"]);
        }

        foreach (KazoTrashItem item in await KazoTrash.GetItemsAsync(cancellationToken))
        {
            string date = item.DeletedAt?.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) ?? "????-??-?? ??:??:??";
            string path = item.OriginalPath ?? text["List.UnknownOrigin"];

            // Show the trash name when it is not obvious from the path, so `trash rm <name>` is usable.
            string suffix = item.OriginalPath is null || KazoPath.GetFileName(item.OriginalPath) != item.Name ? $"  [{item.Name}]" : string.Empty;
            string origin = item.DeletedOnAnotherDevice ? text["List.OtherDevice"] : string.Empty;
            context.StandardOutput.WriteLine($"{date}  {path}{suffix}{origin}");
        }

        return 0;
    }

    private static async Task<int> RestoreAsync(TerminalExecutionContext context, string[] arguments, CancellationToken cancellationToken)
    {
        IStringLocalizer<Strings> text = Text(context);
        if (arguments.Length is 0 or > 2)
        {
            return Fail(context, text["Restore.Expected"]);
        }

        IReadOnlyList<KazoTrashItem> items = await KazoTrash.GetItemsAsync(cancellationToken);
        KazoTrashItem? item = Find(items, context, arguments[0]);
        if (item is null)
        {
            return Fail(context, text["Restore.NotInTrash", arguments[0]]);
        }

        string? destination = arguments.Length == 2 ? Resolve(context, arguments[1]) : null;
        await KazoTrash.RestoreAsync(item, destination, cancellationToken);
        return 0;
    }

    private static async Task<int> RemoveAsync(TerminalExecutionContext context, string[] arguments, CancellationToken cancellationToken)
    {
        IStringLocalizer<Strings> text = Text(context);
        if (arguments.Length == 0)
        {
            return Fail(context, text["Rm.MissingName"]);
        }

        IReadOnlyList<KazoTrashItem> items = await KazoTrash.GetItemsAsync(cancellationToken);
        int status = 0;
        foreach (string name in arguments)
        {
            KazoTrashItem? item = items.FirstOrDefault(candidate => candidate.Name == name);
            if (item is null)
            {
                context.StandardError.WriteLine(text["Rm.NotInTrash", name]);
                status = 1;
                continue;
            }

            await KazoTrash.DeleteAsync(item, cancellationToken);
        }

        return status;
    }

    private static async Task<int> EmptyAsync(TerminalExecutionContext context, string[] arguments, CancellationToken cancellationToken)
    {
        if (arguments.Length > 0)
        {
            return Fail(context, Text(context)["Empty.TooManyArguments"]);
        }

        await KazoTrash.EmptyAsync(cancellationToken);
        return 0;
    }

    /// <summary>Finds an element by trash name first, then by original path (most recent deletion wins).</summary>
    private static KazoTrashItem? Find(IReadOnlyList<KazoTrashItem> items, TerminalExecutionContext context, string key)
    {
        KazoTrashItem? byName = items.FirstOrDefault(item => item.Name == key);
        if (byName is not null)
        {
            return byName;
        }

        string? path = Resolve(context, key);
        return items
            .Where(item => item.OriginalPath == path)
            .OrderByDescending(item => item.DeletedAt)
            .FirstOrDefault();
    }

    /// <summary>Resolves a user-typed path against the shell's working directory; <see langword="null"/> when invalid.</summary>
    private static string? Resolve(TerminalExecutionContext context, string path)
    {
        try
        {
            return path.StartsWith('~') ? KazoPath.GetFullPath(path) : KazoPath.GetFullPath(path, context.WorkingDirectory);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static int PrintUsage(TerminalExecutionContext context)
    {
        context.StandardOutput.WriteLine(Text(context)["Usage"]);
        return 0;
    }

    private static int Fail(TerminalExecutionContext context, string message)
    {
        context.StandardError.WriteLine(Text(context)["Error", message]);
        return 1;
    }

    /// <summary>The texts of the command, in the language of its process (ADR 0104).</summary>
    private static IStringLocalizer<Strings> Text(TerminalExecutionContext context) => context.App.GetLocalizer<Strings>();
}
