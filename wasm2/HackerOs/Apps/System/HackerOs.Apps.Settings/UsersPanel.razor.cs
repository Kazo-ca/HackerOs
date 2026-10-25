using HackerOs.App.Abstractions;
using HackerOs.AppSdk;
using HackerOs.AppSdk.Administration;
using HackerOs.AppSdk.Globalization;
using Microsoft.Extensions.Localization;

namespace HackerOs.Apps.Settings;

/// <summary>
/// Settings &gt; Users (ADR 0120, section 6): the local accounts, the form that changes one's own
/// password, and, for an administrator, the forms that reset or remove another account's password
/// and that add or delete an account (ADR 0169). The session user's passkeys are in
/// <see cref="PasskeysPanel"/> (ADR 0168).
/// </summary>
/// <remarks>
/// Uses only the public SDK (ADR 0055): <see cref="HackUserAccounts"/>. Changing one's own password
/// needs no capability (the current one is checked); a reset needs <c>users.manage</c>, and the OS
/// asks an administrator in its own window. Password fields are cleared after every attempt.
/// </remarks>
public partial class UsersPanel
{
    private IReadOnlyList<HackUserAccount> _accounts = [];

    private string? _deleteLogin;
    private bool _deleteHome;
    private string? _listMessage;
    private bool _listFailed;

    private string _newLogin = string.Empty;
    private string _newDisplayName = string.Empty;
    private bool _newIsAdministrator;
    private string _newPassword = string.Empty;
    private string _newConfirm = string.Empty;
    private string? _addMessage;
    private bool _addFailed;
    private string? _loadError;
    private bool _busy;

    private string _current = string.Empty;
    private string _ownNew = string.Empty;
    private string _ownConfirm = string.Empty;
    private string? _ownMessage;
    private bool _ownFailed;

    private string? _resetLogin;
    private string _resetNew = string.Empty;
    private string _resetConfirm = string.Empty;
    private string? _resetMessage;
    private bool _resetFailed;

    private IAppExecutionContext Context => AppContext
        ?? throw new InvalidOperationException("This panel must be rendered inside the Settings window.");

    private IStringLocalizer<Strings>? _localizer;

    /// <summary>The panel's texts in the language of the Settings process (ADR 0104).</summary>
    private IStringLocalizer<Strings> L => _localizer ??= AppContext.GetLocalizer<Strings>();

    private HackUserAccount? CurrentAccount => _accounts.FirstOrDefault(account => account.IsCurrentUser);

    /// <summary>An account created without a password changes it without giving a current one.</summary>
    private bool CurrentHasPassword => CurrentAccount?.HasPassword ?? true;

    private bool CurrentIsAdministrator => CurrentAccount?.IsAdministrator ?? false;

    private IReadOnlyList<HackUserAccount> OtherAccounts => _accounts.Where(account => !account.IsCurrentUser).ToArray();

    /// <inheritdoc />
    protected override Task OnInitializedAsync() => LoadAsync();

    /// <summary>Reads the account list again (after every change: the state column follows).</summary>
    internal async Task LoadAsync()
    {
        if (_deleteLogin is not null && _accounts.All(account => account.LoginName != _deleteLogin))
        {
            _deleteLogin = null;
        }

        try
        {
            _accounts = await HackUserAccounts.ListAsync(Context, Context.CancellationToken);
            _loadError = null;
            if (_resetLogin is null || OtherAccounts.All(account => account.LoginName != _resetLogin))
            {
                _resetLogin = OtherAccounts.FirstOrDefault()?.LoginName;
            }
        }
        catch (Exception exception) when (exception is NotSupportedException or UnauthorizedAccessException)
        {
            _loadError = L["Users.Error.CannotList", exception.Message];
        }
    }

    private async Task ChangeOwnAsync()
    {
        if (_busy)
        {
            return;
        }

        if (Check(_ownNew, _ownConfirm) is { } problem)
        {
            (_ownMessage, _ownFailed) = (problem, true);
            return;
        }

        _busy = true;
        try
        {
            await HackUserAccounts.ChangePasswordAsync(Context, CurrentHasPassword ? _current : string.Empty, _ownNew, Context.CancellationToken);
            (_ownMessage, _ownFailed) = (L["Users.Own.Done"], false);
            await LoadAsync();
        }
        catch (UnauthorizedAccessException)
        {
            (_ownMessage, _ownFailed) = (L["Users.Error.WrongCurrent"], true);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or NotSupportedException)
        {
            (_ownMessage, _ownFailed) = (L["Users.Error.Failed", exception.Message], true);
        }
        finally
        {
            (_current, _ownNew, _ownConfirm) = (string.Empty, string.Empty, string.Empty);
            _busy = false;
        }
    }

    private Task ResetAsync()
    {
        if (Check(_resetNew, _resetConfirm) is { } problem)
        {
            (_resetMessage, _resetFailed) = (problem, true);
            return Task.CompletedTask;
        }

        return RunResetAsync(_resetNew);
    }

    private Task RemoveAsync() => RunResetAsync(null);

    /// <summary>Sets (or, with <see langword="null"/>, removes) the selected account's password.</summary>
    private async Task RunResetAsync(string? newPassword)
    {
        if (_busy || _resetLogin is not { } login)
        {
            return;
        }

        _busy = true;
        try
        {
            if (newPassword is null)
            {
                await HackUserAccounts.RemovePasswordAsync(Context, login, Context.CancellationToken);
                (_resetMessage, _resetFailed) = (L["Users.Reset.Removed", login], false);
            }
            else
            {
                await HackUserAccounts.ResetPasswordAsync(Context, login, newPassword, Context.CancellationToken);
                (_resetMessage, _resetFailed) = (L["Users.Reset.Done", login], false);
            }

            await LoadAsync();
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or ArgumentException or InvalidOperationException or NotSupportedException)
        {
            (_resetMessage, _resetFailed) = (L["Users.Error.Failed", exception.Message], true);
        }
        finally
        {
            (_resetNew, _resetConfirm) = (string.Empty, string.Empty);
            _busy = false;
        }
    }

    /// <summary>ADR 0169 §3: asks for the confirmation and the home folder choice before deleting.</summary>
    private void AskDelete(string login) => (_deleteLogin, _deleteHome, _listMessage) = (login, false, null);

    private void CancelDelete() => _deleteLogin = null;

    /// <summary>Deletes the account chosen in the list; the OS asks an administrator first.</summary>
    private async Task DeleteAsync()
    {
        if (_busy || _deleteLogin is not { } login)
        {
            return;
        }

        _busy = true;
        try
        {
            await HackUserAccounts.DeleteAsync(Context, login, _deleteHome, Context.CancellationToken);
            (_listMessage, _listFailed) = (L[_deleteHome ? "Users.Delete.DoneWithHome" : "Users.Delete.Done", login], false);
            _deleteLogin = null;
            await LoadAsync();
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or ArgumentException or InvalidOperationException or NotSupportedException or IOException)
        {
            (_listMessage, _listFailed) = (L["Users.Error.Failed", exception.Message], true);
        }
        finally
        {
            _busy = false;
        }
    }

    /// <summary>ADR 0169 §1: adds an account; the OS asks an administrator first.</summary>
    private async Task AddAsync()
    {
        if (_busy)
        {
            return;
        }

        HackNewUserAccount account = new(
            _newLogin.Trim().ToLowerInvariant(),
            _newDisplayName.Trim(),
            _newIsAdministrator ? AppAuthority.Administrator : AppAuthority.User,
            _newPassword);
        if (Check(_newPassword, _newConfirm) is { } problem)
        {
            (_addMessage, _addFailed) = (problem, true);
            return;
        }

        if (HackUserAccounts.ValidateNewAccount(account) is { } reason)
        {
            (_addMessage, _addFailed) = (L["Users.Error.Failed", reason], true);
            return;
        }

        _busy = true;
        try
        {
            HackUserAccount created = await HackUserAccounts.CreateAsync(Context, account, Context.CancellationToken);
            (_addMessage, _addFailed) = (L["Users.Add.Done", created.LoginName], false);
            (_newLogin, _newDisplayName, _newIsAdministrator) = (string.Empty, string.Empty, false);
            await LoadAsync();
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or ArgumentException or InvalidOperationException or NotSupportedException)
        {
            (_addMessage, _addFailed) = (L["Users.Error.Failed", exception.Message], true);
        }
        finally
        {
            (_newPassword, _newConfirm) = (string.Empty, string.Empty);
            _busy = false;
        }
    }

    /// <summary>The rules of ADR 0120 and the confirmation, checked before anything is sent.</summary>
    private string? Check(string newPassword, string confirmation)
    {
        if (newPassword.Length < HackUserAccounts.MinimumPasswordLength)
        {
            return L["Users.Error.TooShort", HackUserAccounts.MinimumPasswordLength];
        }

        if (HackUserAccounts.ValidateNewPassword(newPassword) is { } reason)
        {
            return L["Users.Error.Failed", reason];
        }

        return string.Equals(newPassword, confirmation, StringComparison.Ordinal) ? null : L["Users.Error.Mismatch"].Value;
    }

    private static string StateCode(HackUserAccount account) => !account.IsEnabled ? "L" : account.HasPassword ? "P" : "NP";

    private string StateTitle(HackUserAccount account) =>
        L[!account.IsEnabled ? "Users.State.Locked" : account.HasPassword ? "Users.State.Password" : "Users.State.NoPassword"];

    private static string StateClass(HackUserAccount account) =>
        !account.IsEnabled || !account.HasPassword ? "badge badge-warning" : "badge badge-ok";
}
