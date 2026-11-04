using Kazo.Os.App.Abstractions;
using Kazo.Os.App.Abstractions.Policy;
using Kazo.Os.AppSdk.Keyring;
using Kazo.Os.Platform.Core.Keyring;
using Kazo.Os.Platform.Core.Tests.Settings;
using Kazo.Os.Simulation.Abstractions.FileSystem;
using Kazo.Os.Simulation.Abstractions.Gateways;
using Kazo.Os.Simulation.Abstractions.Sessions;

namespace Kazo.Os.Platform.Core.Tests.Keyring;

/// <summary>
/// ADR 0100 section 2: the login keyring is created and unlocked at login, sealed with AES-GCM under
/// the password, locked at logout, re-sealed on a password change and set aside when it cannot be
/// opened; apps see only their own entries unless they hold <c>keyring.read-all</c>.
/// </summary>
public sealed class UserKeyringServiceTests
{
    private const string KeyringFile = "/home/alice/.local/share/keyrings/login.keyring";

    [Fact]
    public async Task Login_creates_an_owner_only_keyring_that_never_contains_a_secret_in_clear()
    {
        SettingsFilesHarness harness = new(withKeyring: true);
        await harness.LoginAsync(harness.Alice);
        UserKeyringService keyring = harness.Keyring!;

        await keyring.SetAsync(harness.Alice.Id, "org.example.mail", "imap", "s3cr3t-imap", "IMAP");

        Assert.Equal(harness.Alice.Id, keyring.UnlockedUserId);
        string text = (await harness.ReadAsync(KeyringFile))!;
        Assert.Contains("\"cipher\": \"aes-256-gcm\"", text, StringComparison.Ordinal);
        Assert.DoesNotContain("s3cr3t", text, StringComparison.Ordinal);
        Assert.Equal(0b110_000_000, (await harness.StatAsync(KeyringFile))!.Permissions.Mode);
        Assert.Equal(0b111_000_000, (await harness.StatAsync("/home/alice/.local/share/keyrings"))!.Permissions.Mode);
    }

    [Fact]
    public async Task Logout_locks_the_keyring_and_the_next_login_reads_the_entries_back()
    {
        SettingsFilesHarness harness = new(withKeyring: true);
        await harness.LoginAsync(harness.Alice);
        UserKeyringService keyring = harness.Keyring!;
        await keyring.SetAsync(harness.Alice.Id, "org.example.mail", "imap", "s3cr3t-imap", null);

        await harness.Session.LogoutAsync();

        Assert.Null(keyring.UnlockedUserId);
        await Assert.ThrowsAsync<KazoKeyringLockedException>(
            () => keyring.GetAsync(harness.Alice.Id, "org.example.mail", "imap", CancellationToken.None).AsTask());

        await harness.LoginAsync(harness.Alice);

        Assert.Equal("s3cr3t-imap", await keyring.GetAsync(harness.Alice.Id, "org.example.mail", "imap", CancellationToken.None));
    }

    [Fact]
    public async Task A_keyring_sealed_under_another_password_is_set_aside_and_replaced_by_an_empty_one()
    {
        SettingsFilesHarness harness = new(withKeyring: true);
        AuthenticatedPrincipal alice = await harness.LoginAsync(harness.Alice);
        UserKeyringService keyring = harness.Keyring!;
        await keyring.SetAsync(harness.Alice.Id, "org.example.mail", "imap", "old-secret", null);
        string sealedBefore = (await harness.ReadAsync(KeyringFile))!;

        // As after an administrator reset the password: the typed password no longer opens it.
        await keyring.UnlockAsync(alice, "a brand new password");

        Assert.Equal(sealedBefore, await harness.ReadAsync(KeyringFile + ".old"));
        Assert.Null(await keyring.GetAsync(harness.Alice.Id, "org.example.mail", "imap", CancellationToken.None));
        Assert.Contains(harness.Audit.Entries, entry => entry.Action == "keyring.reset");
    }

    [Fact]
    public async Task Changing_the_password_reseals_the_keyring_under_the_new_one()
    {
        SettingsFilesHarness harness = new(withKeyring: true);
        AuthenticatedPrincipal alice = await harness.LoginAsync(harness.Alice);
        UserKeyringService keyring = harness.Keyring!;
        await keyring.SetAsync(harness.Alice.Id, "org.example.mail", "imap", "kept", null);

        await keyring.ChangePasswordAsync(harness.Alice.Id, "new password");
        keyring.Lock();
        await keyring.UnlockAsync(alice, "new password");

        Assert.Equal("kept", await keyring.GetAsync(harness.Alice.Id, "org.example.mail", "imap", CancellationToken.None));
        Assert.Null(await harness.ReadAsync(KeyringFile + ".old"));
    }

    [Fact]
    public async Task An_administrator_reset_sets_the_keyring_aside()
    {
        SettingsFilesHarness harness = new(withKeyring: true);
        await harness.LoginAsync(harness.Alice);
        await harness.Session.LogoutAsync();

        Assert.True(await harness.Keyring!.SetAsideAsync("/home/alice"));

        Assert.Null(await harness.StatAsync(KeyringFile));
        Assert.NotNull(await harness.StatAsync(KeyringFile + ".old"));
    }

    [Fact]
    public async Task A_passwordless_unlock_keeps_the_keyring_locked()
    {
        SettingsFilesHarness harness = new(withKeyring: true);
        AuthenticatedPrincipal alice = await harness.LoginAsync(harness.Alice);

        await harness.Keyring!.UnlockAsync(alice, null);

        Assert.Null(harness.Keyring.UnlockedUserId);
    }

    [Fact]
    public async Task An_app_sees_only_its_own_entries_unless_it_holds_keyring_read_all()
    {
        SettingsFilesHarness harness = new(withKeyring: true);
        await harness.LoginAsync(harness.Alice);
        UserKeyringService keyring = harness.Keyring!;
        AppKeyringGateway mail = new(keyring, harness.Alice.Id, "org.example.mail", new FixedCapabilities());
        AppKeyringGateway other = new(keyring, harness.Alice.Id, "org.example.other", new FixedCapabilities());
        AppKeyringGateway manager = new(keyring, harness.Alice.Id, "org.example.passwords", new FixedCapabilities(AppCapabilities.KeyringReadAll));
        await mail.SetAsync("imap", "mail-secret", "IMAP");

        Assert.True(mail.IsUnlocked);
        Assert.Equal("mail-secret", await mail.GetAsync(null, "imap"));
        Assert.Null(await other.GetAsync(null, "imap"));
        await Assert.ThrowsAnyAsync<UnauthorizedAccessException>(() => other.GetAsync("org.example.mail", "imap").AsTask());
        await Assert.ThrowsAnyAsync<UnauthorizedAccessException>(() => other.ListAsync(allApps: true).AsTask());
        Assert.Empty(await other.ListAsync(allApps: false));
        Assert.Equal("mail-secret", await manager.GetAsync("org.example.mail", "imap"));
        KazoKeyringEntry entry = Assert.Single(await manager.ListAsync(allApps: true));
        Assert.Equal(("org.example.mail", "imap", "IMAP"), (entry.AppId, entry.Id, entry.Label));
    }

    [Fact]
    public async Task Another_users_keyring_is_never_reachable()
    {
        SettingsFilesHarness harness = new(withKeyring: true);
        await harness.LoginAsync(harness.Alice);
        AppKeyringGateway bobsApp = new(harness.Keyring!, harness.Bob.Id, "org.example.mail", new FixedCapabilities(AppCapabilities.KeyringReadAll));

        Assert.False(bobsApp.IsUnlocked);
        await Assert.ThrowsAsync<KazoKeyringLockedException>(() => bobsApp.GetAsync(null, "imap").AsTask());
    }

    [Fact]
    public async Task Managed_cipher_rejects_a_wrong_key_or_altered_data()
    {
        ManagedKeyringCipher cipher = new();
        byte[] key = await cipher.DeriveKeyAsync("pw", new byte[16], 10);
        byte[] otherKey = await cipher.DeriveKeyAsync("other", new byte[16], 10);
        byte[] nonce = new byte[12];
        byte[] sealedData = await cipher.EncryptAsync(key, nonce, "hello"u8.ToArray());

        Assert.Equal("hello"u8.ToArray(), await cipher.DecryptAsync(key, nonce, sealedData));
        Assert.Null(await cipher.DecryptAsync(otherKey, nonce, sealedData));
        sealedData[0] ^= 0xFF;
        Assert.Null(await cipher.DecryptAsync(key, nonce, sealedData));
    }

    private sealed class FixedCapabilities(params string[] granted) : ICapabilityChecker
    {
        public CapabilityPolicyEvaluation Evaluate(
            string capability, AppAuthority requiredAuthority = AppAuthority.User, CapabilityResourceCandidate? resourceCandidate = null) =>
            throw new NotSupportedException();

        public void Require(
            string capability, AppAuthority requiredAuthority = AppAuthority.User, CapabilityResourceCandidate? resourceCandidate = null)
        {
            if (!granted.Contains(capability, StringComparer.Ordinal))
            {
                throw new AppGatewayAccessDeniedException(capability, CapabilityPolicyEvaluation.DenyMissing(1));
            }
        }
    }
}
