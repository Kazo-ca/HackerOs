using HackerOs.Platform.Core.Keyring;
using HackerOs.Platform.Core.Sessions;
using HackerOs.Platform.Core.Tests.Settings;
using HackerOs.Simulation.Abstractions.Diagnostics;
using HackerOs.Simulation.Abstractions.Sessions;

namespace HackerOs.Platform.Core.Tests.Sessions;

/// <summary>
/// ADR 0168: a passkey is registered for the session user, signs them in after every check of
/// section 1, and opens their keyring through a PRF slot (section 3); a keyring in format 1 moves
/// to format 2 at the next password sign-in.
/// </summary>
public sealed class PasskeyServiceTests : IDisposable
{
    private const string KeyringFile = "/home/alice/.local/share/keyrings/login.keyring";

    private readonly SettingsFilesHarness _harness = new(withKeyring: true);
    private readonly FakePasskeyAuthenticator _authenticator = new();
    private readonly PasskeyService _passkeys;

    public PasskeyServiceTests()
    {
        _passkeys = new PasskeyService(_harness.Users, _authenticator, _harness.Audit, _harness.Keyring, () => _harness.Clock.UtcNow);
    }

    private UserKeyringService Keyring => _harness.Keyring!;

    public void Dispose() => _authenticator.Dispose();

    [Fact]
    public async Task Registering_stores_the_public_key_only_and_logs_it()
    {
        AuthenticatedPrincipal alice = await _harness.LoginAsync(_harness.Alice);

        LocalPasskeyCredential passkey = await _passkeys.RegisterAsync(alice, "  Laptop ");

        Assert.Equal("Laptop", passkey.Name);
        Assert.Equal(LocalPasskeyCredential.Es256, passkey.Algorithm);
        Assert.Equal(_authenticator.CredentialId, passkey.CredentialId);
        LocalUser stored = (await _harness.Users.FindByIdAsync(_harness.Alice.Id))!;
        Assert.Equal(passkey.Id, Assert.Single(stored.Passkeys).Id);
        Assert.Contains(_harness.Audit.Entries, entry => entry.Action == "passkey.add" && entry.Outcome == AuditOutcome.Success);
    }

    [Fact]
    public async Task A_passkey_sign_in_opens_the_session_and_the_keyring()
    {
        AuthenticatedPrincipal alice = await _harness.LoginAsync(_harness.Alice);
        await Keyring.SetAsync(_harness.Alice.Id, "org.example.mail", "imap", "s3cr3t", null);
        LocalPasskeyCredential passkey = await _passkeys.RegisterAsync(alice, "Phone");
        // No PRF output at creation: one assertion to get it and add the keyring slot.
        Assert.Equal(1, _authenticator.Assertions);
        Assert.Contains(passkey.Id, await _passkeys.GetKeyringPasskeyIdsAsync(alice));
        await _harness.Session.LogoutAsync();

        AuthenticatedPrincipal signedIn = await _harness.Session.LoginWithPasskeyAsync(_harness.Alice.LoginName, _passkeys);

        Assert.Equal(_harness.Alice.Id, signedIn.UserId);
        Assert.Equal(_harness.Alice.Id, Keyring.UnlockedUserId);
        Assert.Equal("s3cr3t", await Keyring.GetAsync(_harness.Alice.Id, "org.example.mail", "imap"));
        Assert.DoesNotContain("s3cr3t", (await _harness.ReadAsync(KeyringFile))!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_prf_output_given_at_creation_needs_no_second_confirmation()
    {
        _authenticator.PrfOnCreate = true;
        AuthenticatedPrincipal alice = await _harness.LoginAsync(_harness.Alice);

        LocalPasskeyCredential passkey = await _passkeys.RegisterAsync(alice, "YubiKey");

        Assert.Equal(0, _authenticator.Assertions);
        Assert.Contains(passkey.Id, await _passkeys.GetKeyringPasskeyIdsAsync(alice));
    }

    [Fact]
    public async Task Without_prf_the_sign_in_works_and_the_keyring_stays_locked()
    {
        _authenticator.SupportsPrf = false;
        AuthenticatedPrincipal alice = await _harness.LoginAsync(_harness.Alice);
        LocalPasskeyCredential passkey = await _passkeys.RegisterAsync(alice, "Old key");
        Assert.Null(passkey.PrfSalt);
        Assert.Empty(await _passkeys.GetKeyringPasskeyIdsAsync(alice));
        await _harness.Session.LogoutAsync();

        await _harness.Session.LoginWithPasskeyAsync(_harness.Alice.LoginName, _passkeys);

        Assert.NotNull(_harness.Session.CurrentPrincipal);
        Assert.Null(Keyring.UnlockedUserId);
    }

    [Fact]
    public async Task A_sign_in_stores_the_new_counter_and_the_date_of_use()
    {
        AuthenticatedPrincipal alice = await _harness.LoginAsync(_harness.Alice);
        await _passkeys.RegisterAsync(alice, "Phone");
        await _harness.Session.LogoutAsync();

        await _harness.Session.LoginWithPasskeyAsync(_harness.Alice.LoginName, _passkeys);

        LocalPasskeyCredential stored = Assert.Single((await _harness.Users.FindByIdAsync(_harness.Alice.Id))!.Passkeys);
        Assert.Equal(2u, stored.SignCount);
        Assert.Equal(_harness.Clock.UtcNow, stored.LastUsedAtUtc);
    }

    [Theory]
    [InlineData(PasskeyTamper.Challenge)]
    [InlineData(PasskeyTamper.Origin)]
    [InlineData(PasskeyTamper.RpId)]
    [InlineData(PasskeyTamper.Signature)]
    [InlineData(PasskeyTamper.ClonedCounter)]
    [InlineData(PasskeyTamper.NoUserPresence)]
    public async Task An_answer_failing_a_check_opens_no_session(PasskeyTamper tamper)
    {
        AuthenticatedPrincipal alice = await _harness.LoginAsync(_harness.Alice);
        await _passkeys.RegisterAsync(alice, "Phone");
        await _harness.Session.LogoutAsync();
        _authenticator.Tamper = tamper;

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _harness.Session.LoginWithPasskeyAsync(_harness.Alice.LoginName, _passkeys));

        Assert.Null(_harness.Session.CurrentPrincipal);
        Assert.Null(Keyring.UnlockedUserId);
        Assert.Contains(_harness.Audit.Entries, entry => entry.Action == "passkey.verify" && entry.Outcome == AuditOutcome.Denied);
    }

    [Fact]
    public async Task A_tampered_creation_is_not_stored()
    {
        AuthenticatedPrincipal alice = await _harness.LoginAsync(_harness.Alice);
        _authenticator.Tamper = PasskeyTamper.Origin;

        await Assert.ThrowsAsync<InvalidOperationException>(() => _passkeys.RegisterAsync(alice, "Phone").AsTask());

        Assert.Empty((await _harness.Users.FindByIdAsync(_harness.Alice.Id))!.Passkeys);
    }

    [Fact]
    public async Task Closing_the_browser_prompt_cancels_without_opening_a_session()
    {
        AuthenticatedPrincipal alice = await _harness.LoginAsync(_harness.Alice);
        await _passkeys.RegisterAsync(alice, "Phone");
        await _harness.Session.LogoutAsync();
        _authenticator.Cancels = true;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _harness.Session.LoginWithPasskeyAsync(_harness.Alice.LoginName, _passkeys));

        Assert.Null(_harness.Session.CurrentPrincipal);
    }

    [Fact]
    public async Task A_page_without_webauthn_cannot_register()
    {
        AuthenticatedPrincipal alice = await _harness.LoginAsync(_harness.Alice);
        _authenticator.IsSupported = false;

        Assert.False(await _passkeys.IsSupportedAsync());
        await Assert.ThrowsAsync<NotSupportedException>(() => _passkeys.RegisterAsync(alice, "Phone").AsTask());
    }

    [Fact]
    public async Task An_account_without_a_passkey_cannot_sign_in_with_one()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _harness.Session.LoginWithPasskeyAsync(_harness.Bob.LoginName, _passkeys));

        Assert.Null(_harness.Session.CurrentPrincipal);
    }

    [Fact]
    public async Task Removing_a_passkey_removes_its_keyring_slot_too()
    {
        AuthenticatedPrincipal alice = await _harness.LoginAsync(_harness.Alice);
        LocalPasskeyCredential passkey = await _passkeys.RegisterAsync(alice, "Phone");

        Assert.True(await _passkeys.RemoveAsync(alice, passkey.Id));
        Assert.False(await _passkeys.RemoveAsync(alice, passkey.Id));

        Assert.Empty(await _passkeys.ListAsync(_harness.Alice.Id));
        Assert.Empty(await _passkeys.GetKeyringPasskeyIdsAsync(alice));
        Assert.DoesNotContain(passkey.Id, (await _harness.ReadAsync(KeyringFile))!, StringComparison.Ordinal);
        Assert.Contains(_harness.Audit.Entries, entry => entry.Action == "passkey.remove");
    }

    [Fact]
    public async Task Changing_the_password_keeps_the_passkey_slots()
    {
        AuthenticatedPrincipal alice = await _harness.LoginAsync(_harness.Alice);
        await Keyring.SetAsync(_harness.Alice.Id, "org.example.mail", "imap", "kept", null);
        await _passkeys.RegisterAsync(alice, "Phone");

        await Keyring.ChangePasswordAsync(_harness.Alice.Id, "a new password");
        await _harness.Session.LogoutAsync();
        await _harness.Session.LoginWithPasskeyAsync(_harness.Alice.LoginName, _passkeys);

        Assert.Equal("kept", await Keyring.GetAsync(_harness.Alice.Id, "org.example.mail", "imap"));
    }

    [Fact]
    public async Task A_format_1_keyring_moves_to_format_2_at_the_next_password_sign_in()
    {
        await _harness.LoginAsync(_harness.Alice);
        await _harness.Session.LogoutAsync();
        ManagedKeyringCipher cipher = new();
        byte[] salt = new byte[16];
        byte[] nonce = new byte[12];
        byte[] key = await cipher.DeriveKeyAsync(SettingsFilesHarness.Password, salt, 10);
        byte[] sealedData = await cipher.EncryptAsync(
            key,
            nonce,
            KeyringFileFormat.WriteEntries([new KeyringEntryRecord("org.example.mail", "imap", null, "legacy", _harness.Clock.UtcNow)]));
        await _harness.WriteAsRootAsync(KeyringFile, KeyringFileFormat.WriteEnvelope(new KeyringEnvelope(10, salt, nonce, sealedData)));

        await _harness.LoginAsync(_harness.Alice);

        Assert.Equal("legacy", await Keyring.GetAsync(_harness.Alice.Id, "org.example.mail", "imap"));
        string text = (await _harness.ReadAsync(KeyringFile))!;
        Assert.Equal(KeyringFileFormat.VersionWithSlots, KeyringFileFormat.ReadVersion(text));
        Assert.Contains(_harness.Audit.Entries, entry => entry.Action == "keyring.upgrade");
    }
}
