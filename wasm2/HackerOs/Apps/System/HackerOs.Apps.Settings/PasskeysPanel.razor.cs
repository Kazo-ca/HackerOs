using HackerOs.App.Abstractions;
using HackerOs.AppSdk;
using HackerOs.AppSdk.Administration;
using HackerOs.AppSdk.Globalization;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;

namespace HackerOs.Apps.Settings;

/// <summary>
/// Settings &gt; Users &gt; Passkeys (ADR 0168, section 5): the session user's passkeys, whether each
/// one opens the keyring, and the form that adds one. The browser asks for the user's presence
/// (fingerprint, face, PIN or security key); the OS verifies the answer.
/// </summary>
/// <remarks>
/// Uses only the public SDK (ADR 0055): <see cref="HackUserAccounts"/>. Managing one's own passkeys
/// needs no capability. A browser without WebAuthn (or a page that is not a secure context) shows a
/// note instead of the form.
/// </remarks>
public partial class PasskeysPanel
{
    private IReadOnlyList<HackPasskey> _passkeys = [];
    private string _name = string.Empty;
    private string? _message;
    private bool _failed;
    private bool _busy;
    private bool _isSupported = true;

    private IStringLocalizer<Strings>? _localizer;

    /// <summary>Gets or sets what runs after a passkey is added or removed (the account list refreshes its count).</summary>
    [Parameter]
    public EventCallback Changed { get; set; }

    private IAppExecutionContext Context => AppContext
        ?? throw new InvalidOperationException("This panel must be rendered inside the Settings window.");

    /// <summary>The panel's texts in the language of the Settings process (ADR 0104).</summary>
    private IStringLocalizer<Strings> L => _localizer ??= AppContext.GetLocalizer<Strings>();

    /// <inheritdoc />
    protected override Task OnInitializedAsync() => LoadAsync();

    private async Task LoadAsync()
    {
        try
        {
            _passkeys = await HackUserAccounts.ListPasskeysAsync(Context, Context.CancellationToken);
        }
        catch (NotSupportedException)
        {
            (_passkeys, _isSupported) = ([], false);
        }
        catch (UnauthorizedAccessException exception)
        {
            (_message, _failed) = (L["Users.Error.Failed", exception.Message], true);
        }
    }

    private async Task AddAsync()
    {
        if (_busy)
        {
            return;
        }

        string name = _name.Trim();
        if (name.Length == 0)
        {
            (_message, _failed) = (L["Passkeys.Error.NameRequired"], true);
            return;
        }

        _busy = true;
        try
        {
            HackPasskey added = await HackUserAccounts.AddPasskeyAsync(Context, name, Context.CancellationToken);
            (_message, _failed) = (L[added.UnlocksKeyring ? "Passkeys.Added" : "Passkeys.AddedNoKeyring", added.Name], false);
            _name = string.Empty;
            await LoadAsync();
            await Changed.InvokeAsync();
        }
        catch (OperationCanceledException)
        {
            (_message, _failed) = (L["Passkeys.Error.Cancelled"], true);
        }
        catch (NotSupportedException)
        {
            (_message, _failed, _isSupported) = (L["Passkeys.Unsupported"], true, false);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or UnauthorizedAccessException)
        {
            (_message, _failed) = (L["Users.Error.Failed", exception.Message], true);
        }
        finally
        {
            _busy = false;
        }
    }

    private async Task RemoveAsync(HackPasskey passkey)
    {
        if (_busy)
        {
            return;
        }

        _busy = true;
        try
        {
            bool removed = await HackUserAccounts.RemovePasskeyAsync(Context, passkey.Id, Context.CancellationToken);
            (_message, _failed) = removed
                ? (L["Passkeys.Removed", passkey.Name].Value, false)
                : (L["Passkeys.Error.NotFound", passkey.Name].Value, true);
            await LoadAsync();
            await Changed.InvokeAsync();
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or UnauthorizedAccessException or NotSupportedException)
        {
            (_message, _failed) = (L["Users.Error.Failed", exception.Message], true);
        }
        finally
        {
            _busy = false;
        }
    }
}
