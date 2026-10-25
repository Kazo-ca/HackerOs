using HackerOs.Platform.Core.Elevation;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.Localization;

namespace HackerOs.Platform.Blazor.Security;

/// <summary>
/// Secure-desktop window of the OS authentication agent (ADR 0055, section 5), in the same visual
/// family as <see cref="DangerousCapabilityApprovalOverlay"/>: the operation in plain words, the
/// app and the requesting user, an administrator name (prefilled with the session user when they
/// are an administrator) and a password.
/// </summary>
/// <remarks>
/// The overlay decides nothing: <em>Authenticate</em> submits the credentials to
/// <see cref="AdministratorAuthenticationPromptRequest.AuthenticateAsync"/>, which verifies and logs.
/// After the third failure the request closes as cancelled. <em>Cancel</em> and Escape cancel.
/// </remarks>
public sealed partial class AdministratorAuthenticationOverlay
{
    private AdministratorAuthenticationBroker.PendingAuthentication? _current;
    private IDisposable? _attachment;
    private ElementReference _loginInput;
    private ElementReference _passwordInput;
    private string _login = string.Empty;
    private string _password = string.Empty;
    private string? _error;
    private bool _isVerifying;
    private bool _focusPending;

    /// <summary>Gets or sets the overlay's texts, in the session language (ADR 0104).</summary>
    [Inject]
    private IStringLocalizer<Strings> L { get; set; } = default!;

    /// <summary>Gets or sets the broker queuing authentication requests.</summary>
    [Inject]
    public AdministratorAuthenticationBroker Broker { get; set; } = default!;

    private bool CanAuthenticate => !_isVerifying && !string.IsNullOrWhiteSpace(_login) && !string.IsNullOrEmpty(_password);

    private bool CanUsePasskey => !_isVerifying && !string.IsNullOrWhiteSpace(_login);

    /// <inheritdoc />
    protected override void OnInitialized()
    {
        _attachment = Broker.Attach();
        Broker.Changed += OnBrokerChanged;
        ShowCurrent();
    }

    /// <inheritdoc />
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!_focusPending || _current is null)
        {
            return;
        }

        _focusPending = false;
        try
        {
            await (string.IsNullOrEmpty(_login) ? _loginInput : _passwordInput).FocusAsync();
        }
        catch (InvalidOperationException)
        {
            // Element not attached (test renderer): focus is a convenience only.
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Broker.Changed -= OnBrokerChanged;
        _attachment?.Dispose();
    }

    private void OnBrokerChanged() => _ = InvokeAsync(() =>
    {
        ShowCurrent();
        StateHasChanged();
    });

    private void ShowCurrent()
    {
        AdministratorAuthenticationBroker.PendingAuthentication? next = Broker.Current;
        if (ReferenceEquals(next, _current))
        {
            return;
        }

        _current = next;
        _login = next?.Request.SuggestedAdministratorLogin ?? string.Empty;
        _password = string.Empty;
        _error = null;
        _isVerifying = false;
        _focusPending = next is not null;
    }

    private async Task AuthenticateAsync()
    {
        if (_current is not { } pending || !CanAuthenticate)
        {
            return;
        }

        _isVerifying = true;
        _error = null;
        string password = _password;
        _password = string.Empty;
        AdministratorAuthenticationAttempt attempt = await pending.Request.AuthenticateAsync(_login.Trim(), password, CancellationToken.None);
        _isVerifying = false;
        ShowAttempt(pending, attempt);
    }

    /// <summary>ADR 0168 section 4: the administrator confirms with a passkey; the browser asks for their presence.</summary>
    private async Task AuthenticateWithPasskeyAsync()
    {
        if (_current is not { } pending || pending.Request.AuthenticateWithPasskeyAsync is not { } authenticate || !CanUsePasskey)
        {
            return;
        }

        _isVerifying = true;
        _error = null;
        _password = string.Empty;
        AdministratorAuthenticationAttempt attempt = await authenticate(_login.Trim(), CancellationToken.None);
        _isVerifying = false;
        ShowAttempt(pending, attempt);
    }

    private void ShowAttempt(AdministratorAuthenticationBroker.PendingAuthentication pending, AdministratorAuthenticationAttempt attempt)
    {
        if (attempt.Succeeded)
        {
            pending.Complete(AdministratorAuthenticationOutcome.Approved);
            return;
        }

        if (attempt.RemainingAttempts <= 0)
        {
            pending.Complete(AdministratorAuthenticationOutcome.Cancelled);
            return;
        }

        _error = L[attempt.FailureReason switch
        {
            "not-administrator" => "Security.NotAdministrator",
            "user-disabled" => "Security.UserDisabled",
            "no-passkey" => "Security.NoPasskey",
            "passkey-cancelled" => "Security.PasskeyCancelled",
            "passkey-unsupported" => "Security.PasskeyUnsupported",
            _ => "Security.InvalidCredentials"
        }, attempt.RemainingAttempts];
        _focusPending = true;
    }

    private void Cancel() => _current?.Complete(AdministratorAuthenticationOutcome.Cancelled);

    private void HandleKeyDown(KeyboardEventArgs args)
    {
        if (args.Key == "Escape" && !_isVerifying)
        {
            _current?.Complete(AdministratorAuthenticationOutcome.Cancelled);
        }
    }
}
