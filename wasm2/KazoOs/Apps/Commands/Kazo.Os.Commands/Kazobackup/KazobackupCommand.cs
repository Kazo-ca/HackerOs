using System.Globalization;
using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk;
using Kazo.Os.AppSdk.Administration;
using Kazo.Os.AppSdk.IO;
using Kazo.Os.Commands.Common;
using Kazo.Os.AppSdk.Globalization;
using Microsoft.Extensions.Localization;

namespace Kazo.Os.Commands.Kazobackup;

/// <summary>
/// <c>kazobackup create | restore [--merge|--replace]</c> (ADR 0099, section 1): the terminal side
/// of Settings &gt; Backup, through <see cref="KazoSystemBackup"/>.
/// </summary>
/// <remarks>
/// <para>
/// Like a system backup tool, it must run as root (<c>sudo kazobackup ...</c>): an archive holds
/// every user's files and the password hashes. Without <c>sudo</c> it stops at once instead of
/// opening the graphical administrator prompt, the way <c>apt</c> answers "are you root?".
/// </para>
/// <para>
/// <c>create</c> downloads the <c>.hosbak</c> archive to the host computer. <c>restore</c> opens
/// the browser's file picker (Enter counts as the user's gesture); the default mode is
/// <c>--merge</c>. If the browser refuses to open its picker, the command points to Settings.
/// </para>
/// </remarks>
public sealed class KazobackupCommand(AppManifest manifest) : TerminalAppBase(manifest)
{
    /// <inheritdoc />
    public override async ValueTask<int> ExecuteAsync(TerminalExecutionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        IStringLocalizer<Strings> text = context.App.GetLocalizer<Strings>();
        string usage = text["Usage"];
        IReadOnlyList<string> args = context.Arguments;
        if (args.Count == 0 || args[0] is "-h" or "--help" or "help")
        {
            await (args.Count == 0 ? context.StandardError : context.StandardOutput).WriteLineAsync(usage);
            return args.Count == 0 ? 2 : 0;
        }

        string verb = args[0];
        if (verb is not ("create" or "restore"))
        {
            await context.StandardError.WriteLineAsync(text["UnknownCommand", verb]);
            await context.StandardError.WriteLineAsync(usage);
            return 2;
        }

        KazoBackupRestoreMode mode = KazoBackupRestoreMode.Merge;
        foreach (string option in args.Skip(1))
        {
            switch (option)
            {
                case "--merge" when verb == "restore":
                    mode = KazoBackupRestoreMode.Merge;
                    break;
                case "--replace" when verb == "restore":
                    mode = KazoBackupRestoreMode.Replace;
                    break;
                default:
                    await context.StandardError.WriteLineAsync(text["InvalidOption", option]);
                    await context.StandardError.WriteLineAsync(usage);
                    return 2;
            }
        }

        if (!context.App.IsRootElevated)
        {
            await context.StandardError.WriteLineAsync(
                text["RootRequired", string.Join(' ', args)]);
            return 1;
        }

        if (!KazoSystemBackup.IsSupportedFor(context.App))
        {
            await context.StandardError.WriteLineAsync(text["Unsupported"]);
            return 1;
        }

        try
        {
            return verb == "create"
                ? await CreateAsync(context, cancellationToken)
                : await RestoreAsync(context, mode, cancellationToken);
        }
        catch (KazoBackupInvalidException exception)
        {
            await context.StandardError.WriteLineAsync(text["InvalidBackup", exception.Message]);
            return 1;
        }
        catch (InvalidOperationException exception) when (verb == "restore")
        {
            // The browser refused to open its file picker without a fresh user gesture.
            await context.StandardError.WriteLineAsync($"kazobackup: {exception.Message}");
            await context.StandardError.WriteLineAsync(text["UseSettings"]);
            return 1;
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or NotSupportedException or IOException)
        {
            await context.StandardError.WriteLineAsync($"kazobackup: {CommandText.Describe(context, exception)}");
            return 1;
        }
    }

    private static async Task<int> CreateAsync(TerminalExecutionContext context, CancellationToken cancellationToken)
    {
        IStringLocalizer<Strings> text = context.App.GetLocalizer<Strings>();
        await context.StandardOutput.WriteLineAsync(text["Exporting"]);
        KazoBackupInfo backup = await KazoSystemBackup.CreateAsync(context.App, cancellationToken);
        await context.StandardOutput.WriteLineAsync(text[
            "Downloaded", backup.FileName, CommandIO.HumanSize(backup.SizeBytes), backup.RecordCount.ToString(CultureInfo.InvariantCulture)]);
        return 0;
    }

    private static async Task<int> RestoreAsync(TerminalExecutionContext context, KazoBackupRestoreMode mode, CancellationToken cancellationToken)
    {
        IStringLocalizer<Strings> text = context.App.GetLocalizer<Strings>();
        string modeName = mode == KazoBackupRestoreMode.Replace ? "replace" : "merge";
        await context.StandardOutput.WriteLineAsync(text["ChooseFile", modeName]);
        KazoBackupInfo? restored = await KazoSystemBackup.RestoreAsync(context.App, mode, cancellationToken);
        if (restored is null)
        {
            await context.StandardError.WriteLineAsync(text["NoFileChosen"]);
            return 1;
        }

        // Normally unreachable: the session ends and the machine restarts during the restore.
        await context.StandardOutput.WriteLineAsync(text["Restored", restored.FileName, modeName]);
        return 0;
    }
}
