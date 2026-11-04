using System.Globalization;
using Kazo.Os.AppSdk;
using Kazo.Os.AppSdk.Administration;
using Kazo.Os.AppSdk.Globalization;
using Kazo.Os.AppSdk.IO;
using Microsoft.Extensions.Localization;

namespace Kazo.Os.Apps.Settings;

/// <summary>
/// Settings &gt; Backup (ADR 0099, section 1): download a backup of the whole machine to the host
/// computer, or restore one picked there, merging or replacing.
/// </summary>
/// <remarks>
/// Uses only the public SDK (ADR 0055): <see cref="HackSystemBackup"/> (capability
/// <c>system.backup</c>). The OS asks an administrator in its own window, opens the host's file
/// picker and restarts after a restore; this panel never sees a password or the archive. The
/// restore call starts from the button's click so the browser accepts to open its file picker.
/// </remarks>
public partial class BackupPanel
{
    private enum Operation
    {
        None,
        Create,
        Restore
    }

    private Operation _operation;
    private HackBackupRestoreMode _mode = HackBackupRestoreMode.Merge;
    private HackBackupInfo? _lastBackup;
    private string? _error;

    private bool IsBusy => _operation != Operation.None;

    private IAppExecutionContext Context => AppContext
        ?? throw new InvalidOperationException("This panel must be rendered inside the Settings window.");

    private IStringLocalizer<Strings>? _localizer;

    /// <summary>The panel's texts in the language of the Settings process (ADR 0104).</summary>
    private IStringLocalizer<Strings> L => _localizer ??= AppContext.GetLocalizer<Strings>();

    /// <summary>The culture of the numbers shown (the process's locale, ADR 0104).</summary>
    private CultureInfo Formats => AppContext?.FormatCulture ?? CultureInfo.InvariantCulture;

    private string ModeClass(HackBackupRestoreMode mode) =>
        mode == _mode ? "restore-mode is-selected" : "restore-mode";

    private async Task CreateAsync()
    {
        if (IsBusy)
        {
            return;
        }

        _operation = Operation.Create;
        _error = null;
        try
        {
            _lastBackup = await HackSystemBackup.CreateAsync(Context, Context.CancellationToken);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or NotSupportedException or IOException or InvalidOperationException)
        {
            _error = L["Backup.Error.Failed", exception.Message];
        }
        finally
        {
            _operation = Operation.None;
        }
    }

    private async Task RestoreAsync()
    {
        if (IsBusy)
        {
            return;
        }

        _operation = Operation.Restore;
        _error = null;
        try
        {
            // Returns null when the picker was closed; otherwise the machine restarts.
            await HackSystemBackup.RestoreAsync(Context, _mode, Context.CancellationToken);
        }
        catch (HackBackupInvalidException exception)
        {
            _error = L["Backup.Error.Invalid", exception.Message];
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or NotSupportedException or IOException or InvalidOperationException)
        {
            _error = L["Backup.Error.RestoreFailed", exception.Message];
        }
        catch (OperationCanceledException) when (Context.CancellationToken.IsCancellationRequested)
        {
            // The session is ending for the restart.
        }
        finally
        {
            _operation = Operation.None;
        }
    }

    /// <summary>A size in binary units, with the unit symbols and decimal separator of the process's locale.</summary>
    private string FormatSize(long bytes) => bytes switch
    {
        < 1024 => L["Backup.Size.Bytes", bytes.ToString(Formats)],
        < 1024 * 1024 => L["Backup.Size.KiB", (bytes / 1024.0).ToString("0.#", Formats)],
        _ => L["Backup.Size.MiB", (bytes / (1024.0 * 1024.0)).ToString("0.#", Formats)]
    };
}
