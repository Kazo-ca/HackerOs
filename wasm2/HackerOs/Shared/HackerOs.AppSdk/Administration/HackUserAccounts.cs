using HackerOs.Simulation.Abstractions.Gateways;

namespace HackerOs.AppSdk.Administration;

/// <summary>
/// Local accounts and their passwords (ADR 0120), what <c>passwd</c> does on Linux. Anyone changes
/// their own password by giving the current one; resetting or removing another account's password
/// needs the dangerous capability <c>users.manage</c> and an administrator, and sets that account's
/// keyring aside (its secrets stay sealed under the old password).
/// </summary>
public static class HackUserAccounts
{
    /// <summary>The shortest password accepted, as when the first account is created.</summary>
    public const int MinimumPasswordLength = 8;

    /// <summary>The longest password accepted.</summary>
    public const int MaximumPasswordLength = 256;

    /// <summary>Lists every local account (ambient context).</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    public static Task<IReadOnlyList<HackUserAccount>> ListAsync(CancellationToken cancellationToken = default) =>
        ListAsync(HackExecutionContext.Current, cancellationToken);

    /// <summary>Lists every local account.</summary>
    /// <param name="context">The calling app's context.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    public static Task<IReadOnlyList<HackUserAccount>> ListAsync(IAppExecutionContext context, CancellationToken cancellationToken = default) =>
        Call(context, gateway => gateway.ListAsync(cancellationToken));

    /// <summary>Changes the session user's password (ambient context).</summary>
    /// <param name="currentPassword">The current password; empty when the account has none.</param>
    /// <param name="newPassword">The new password.</param>
    /// <param name="cancellationToken">Cancels the change.</param>
    public static Task ChangePasswordAsync(string currentPassword, string newPassword, CancellationToken cancellationToken = default) =>
        ChangePasswordAsync(HackExecutionContext.Current, currentPassword, newPassword, cancellationToken);

    /// <summary>Changes the session user's password, after checking the current one.</summary>
    /// <param name="context">The calling app's context.</param>
    /// <param name="currentPassword">The current password; empty when the account has none.</param>
    /// <param name="newPassword">The new password.</param>
    /// <param name="cancellationToken">Cancels the change.</param>
    /// <exception cref="UnauthorizedAccessException">The current password is wrong.</exception>
    /// <exception cref="ArgumentException">The new password breaks the rules (see <see cref="ValidateNewPassword"/>).</exception>
    public static Task ChangePasswordAsync(
        IAppExecutionContext context,
        string currentPassword,
        string newPassword,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(currentPassword);
        ThrowIfInvalid(newPassword);
        return Call(context, async gateway =>
        {
            await gateway.ChangePasswordAsync(currentPassword, newPassword, cancellationToken).ConfigureAwait(false);
            return true;
        });
    }

    /// <summary>Sets another account's password (ambient context).</summary>
    /// <param name="loginName">The account to change.</param>
    /// <param name="newPassword">The new password.</param>
    /// <param name="cancellationToken">Cancels the change.</param>
    public static Task ResetPasswordAsync(string loginName, string newPassword, CancellationToken cancellationToken = default) =>
        ResetPasswordAsync(HackExecutionContext.Current, loginName, newPassword, cancellationToken);

    /// <summary>
    /// Sets another account's password, as <c>sudo passwd NAME</c>: needs <c>users.manage</c> and an
    /// administrator; the account's keyring is set aside.
    /// </summary>
    /// <param name="context">The calling app's context.</param>
    /// <param name="loginName">The account to change.</param>
    /// <param name="newPassword">The new password.</param>
    /// <param name="cancellationToken">Cancels the change.</param>
    /// <exception cref="UnauthorizedAccessException">No <c>users.manage</c>, or no administrator confirmed.</exception>
    /// <exception cref="ArgumentException">The new password breaks the rules, or the account does not exist.</exception>
    public static Task ResetPasswordAsync(
        IAppExecutionContext context,
        string loginName,
        string newPassword,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(loginName);
        ThrowIfInvalid(newPassword);
        return Call(context, async gateway =>
        {
            await gateway.ResetPasswordAsync(loginName, newPassword, cancellationToken).ConfigureAwait(false);
            return true;
        });
    }

    /// <summary>Removes another account's password (ambient context).</summary>
    /// <param name="loginName">The account to change.</param>
    /// <param name="cancellationToken">Cancels the change.</param>
    public static Task RemovePasswordAsync(string loginName, CancellationToken cancellationToken = default) =>
        RemovePasswordAsync(HackExecutionContext.Current, loginName, cancellationToken);

    /// <summary>
    /// Leaves an account without a password, as <c>passwd -d NAME</c>: needs <c>users.manage</c> and an
    /// administrator. Refused for the last enabled administrator.
    /// </summary>
    /// <param name="context">The calling app's context.</param>
    /// <param name="loginName">The account to change.</param>
    /// <param name="cancellationToken">Cancels the change.</param>
    /// <exception cref="UnauthorizedAccessException">No <c>users.manage</c>, or no administrator confirmed.</exception>
    /// <exception cref="InvalidOperationException">The account is the last enabled administrator.</exception>
    public static Task RemovePasswordAsync(IAppExecutionContext context, string loginName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(loginName);
        return Call(context, async gateway =>
        {
            await gateway.ResetPasswordAsync(loginName, null, cancellationToken).ConfigureAwait(false);
            return true;
        });
    }

    /// <summary>Longest display name of a new account (ADR 0169).</summary>
    public const int MaximumDisplayNameLength = 128;

    /// <summary>Creates an account (ambient context).</summary>
    /// <param name="account">The new account.</param>
    /// <param name="cancellationToken">Cancels the creation.</param>
    /// <returns>The created account.</returns>
    public static Task<HackUserAccount> CreateAsync(HackNewUserAccount account, CancellationToken cancellationToken = default) =>
        CreateAsync(HackExecutionContext.Current, account, cancellationToken);

    /// <summary>
    /// Creates an account, as <c>useradd -m</c> then <c>passwd</c> (ADR 0169): needs
    /// <c>users.manage</c> and an administrator. The home directory is created at its first login.
    /// </summary>
    /// <param name="context">The calling app's context.</param>
    /// <param name="account">The new account.</param>
    /// <param name="cancellationToken">Cancels the creation.</param>
    /// <returns>The created account.</returns>
    /// <exception cref="UnauthorizedAccessException">No <c>users.manage</c>, or no administrator confirmed.</exception>
    /// <exception cref="ArgumentException">A field breaks the rules (see <see cref="ValidateNewAccount"/>), or the name is taken or reserved.</exception>
    public static Task<HackUserAccount> CreateAsync(IAppExecutionContext context, HackNewUserAccount account, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        if (ValidateNewAccount(account) is { } reason)
        {
            throw new ArgumentException($"useradd: {reason}", nameof(account));
        }

        return Call(context, gateway => gateway.CreateAsync(account, cancellationToken));
    }

    /// <summary>Deletes an account (ambient context).</summary>
    /// <param name="loginName">The account to delete.</param>
    /// <param name="removeHome">Whether to delete its home directory too.</param>
    /// <param name="cancellationToken">Cancels the deletion.</param>
    public static Task DeleteAsync(string loginName, bool removeHome, CancellationToken cancellationToken = default) =>
        DeleteAsync(HackExecutionContext.Current, loginName, removeHome, cancellationToken);

    /// <summary>
    /// Deletes an account, as <c>userdel</c> (or <c>userdel -r</c> with <paramref name="removeHome"/>)
    /// (ADR 0169): needs <c>users.manage</c> and an administrator. Refused for the caller's own
    /// account and for the last enabled administrator.
    /// </summary>
    /// <param name="context">The calling app's context.</param>
    /// <param name="loginName">The account to delete.</param>
    /// <param name="removeHome">Whether to delete its home directory too.</param>
    /// <param name="cancellationToken">Cancels the deletion.</param>
    /// <exception cref="UnauthorizedAccessException">No <c>users.manage</c>, or no administrator confirmed.</exception>
    /// <exception cref="ArgumentException">The account does not exist.</exception>
    /// <exception cref="InvalidOperationException">The caller's own account, or the last enabled administrator.</exception>
    public static Task DeleteAsync(IAppExecutionContext context, string loginName, bool removeHome, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(loginName);
        return Call(context, async gateway =>
        {
            await gateway.DeleteAsync(loginName, removeHome, cancellationToken).ConfigureAwait(false);
            return true;
        });
    }

    /// <summary>Lists the session user's passkeys (ambient context).</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The passkeys, oldest first.</returns>
    public static Task<IReadOnlyList<HackPasskey>> ListPasskeysAsync(CancellationToken cancellationToken = default) =>
        ListPasskeysAsync(HackExecutionContext.Current, cancellationToken);

    /// <summary>Lists the session user's passkeys (ADR 0168).</summary>
    /// <param name="context">The calling app's context.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The passkeys, oldest first.</returns>
    public static Task<IReadOnlyList<HackPasskey>> ListPasskeysAsync(IAppExecutionContext context, CancellationToken cancellationToken = default) =>
        Call(context, gateway => gateway.ListPasskeysAsync(cancellationToken));

    /// <summary>Creates a passkey for the session user (ambient context).</summary>
    /// <param name="name">Name of the passkey.</param>
    /// <param name="cancellationToken">Cancels the ceremony.</param>
    /// <returns>The new passkey.</returns>
    public static Task<HackPasskey> AddPasskeyAsync(string name, CancellationToken cancellationToken = default) =>
        AddPasskeyAsync(HackExecutionContext.Current, name, cancellationToken);

    /// <summary>
    /// Creates a passkey for the session user (ADR 0168): the browser asks for their presence
    /// (fingerprint, device PIN, security key). No capability: the passkey only signs in this account.
    /// </summary>
    /// <param name="context">The calling app's context.</param>
    /// <param name="name">Name of the passkey, e.g. "Laptop" (64 characters at most).</param>
    /// <param name="cancellationToken">Cancels the ceremony.</param>
    /// <returns>The new passkey.</returns>
    /// <exception cref="NotSupportedException">No WebAuthn in this page or process.</exception>
    /// <exception cref="OperationCanceledException">The user closed the browser's prompt.</exception>
    /// <exception cref="InvalidOperationException">The browser's answer failed a check.</exception>
    public static Task<HackPasskey> AddPasskeyAsync(IAppExecutionContext context, string name, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return Call(context, gateway => gateway.AddPasskeyAsync(name.Trim(), cancellationToken));
    }

    /// <summary>Removes one of the session user's passkeys (ambient context).</summary>
    /// <param name="passkeyId">Its base64url id.</param>
    /// <param name="cancellationToken">Cancels the change.</param>
    /// <returns><see langword="true"/> when a passkey was removed.</returns>
    public static Task<bool> RemovePasskeyAsync(string passkeyId, CancellationToken cancellationToken = default) =>
        RemovePasskeyAsync(HackExecutionContext.Current, passkeyId, cancellationToken);

    /// <summary>Removes one of the session user's passkeys, and the keyring slot it had.</summary>
    /// <param name="context">The calling app's context.</param>
    /// <param name="passkeyId">Its base64url id.</param>
    /// <param name="cancellationToken">Cancels the change.</param>
    /// <returns><see langword="true"/> when a passkey was removed.</returns>
    public static Task<bool> RemovePasskeyAsync(IAppExecutionContext context, string passkeyId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(passkeyId);
        return Call(context, gateway => gateway.RemovePasskeyAsync(passkeyId, cancellationToken));
    }

    /// <summary>
    /// Checks a new account against the rules of ADR 0169: a login name of lowercase letters,
    /// digits, <c>-</c> and <c>_</c> starting with a letter (32 characters at most), a display name,
    /// a user or administrator authority, and a password that passes <see cref="ValidateNewPassword"/>.
    /// </summary>
    /// <param name="account">The candidate account.</param>
    /// <returns><see langword="null"/> when valid; otherwise the reason.</returns>
    public static string? ValidateNewAccount(HackNewUserAccount? account)
    {
        if (account is null)
        {
            return "No account has been supplied.";
        }

        string login = account.LoginName?.Trim() ?? string.Empty;
        if (login.Length is 0 or > 32
            || !char.IsAsciiLetterLower(login[0])
            || !login.All(static c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '-' or '_'))
        {
            return "The login name must start with a lowercase letter and use only lowercase letters, digits, '-' and '_' (32 at most).";
        }

        if (string.IsNullOrWhiteSpace(account.DisplayName) || account.DisplayName.Trim().Length > MaximumDisplayNameLength)
        {
            return $"A display name of 1 to {MaximumDisplayNameLength} characters is required.";
        }

        if (account.Authority is not (App.Abstractions.AppAuthority.User or App.Abstractions.AppAuthority.Administrator))
        {
            return "An account is a user or an administrator.";
        }

        return ValidateNewPassword(account.Password);
    }

    /// <summary>
    /// Checks a new password against the rules of ADR 0120: between <see cref="MinimumPasswordLength"/>
    /// and <see cref="MaximumPasswordLength"/> characters, with no control character.
    /// </summary>
    /// <param name="newPassword">The candidate password.</param>
    /// <returns><see langword="null"/> when valid; otherwise the reason, in the words of <c>passwd</c>.</returns>
    public static string? ValidateNewPassword(string? newPassword)
    {
        if (string.IsNullOrEmpty(newPassword))
        {
            return "No password has been supplied.";
        }

        if (newPassword.Length < MinimumPasswordLength)
        {
            return $"The password is shorter than {MinimumPasswordLength} characters.";
        }

        if (newPassword.Length > MaximumPasswordLength)
        {
            return $"The password is longer than {MaximumPasswordLength} characters.";
        }

        return newPassword.Any(char.IsControl) ? "The password contains a control character." : null;
    }

    private static void ThrowIfInvalid(string newPassword)
    {
        if (ValidateNewPassword(newPassword) is { } reason)
        {
            throw new ArgumentException($"BAD PASSWORD: {reason}", nameof(newPassword));
        }
    }

    private static async Task<T> Call<T>(IAppExecutionContext context, Func<IAppUserAccountsGateway, ValueTask<T>> call)
    {
        ArgumentNullException.ThrowIfNull(context);
        try
        {
            return await call(context.UserAccounts).ConfigureAwait(false);
        }
        catch (AppGatewayAccessDeniedException exception)
        {
            throw HackGatewayErrors.ToUnauthorized(exception);
        }
    }
}
