using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk;
using Kazo.Os.AppSdk.Keyring;
using Kazo.Os.AppSdk.Settings;
using Kazo.Os.Platform.Core.Execution;
using Kazo.Os.Simulation.Abstractions.Diagnostics;
using Kazo.Os.Simulation.Abstractions.FileSystem;
using Kazo.Os.Simulation.Abstractions.Gateways;
using Kazo.Os.Simulation.Abstractions.Processes;
using Kazo.Os.Simulation.Abstractions.Sessions;
using Kazo.Os.Simulation.Abstractions.Settings;

namespace Kazo.Os.Platform.Core.Tests.Settings;

/// <summary>
/// ADR 0100 section 1: sensitive keys live in <c>&lt;doc&gt;.private.config</c> (0600), are masked for
/// a caller who may not read that file, move out of the wrong file on the next write, and a
/// <c>SecretReference</c> keeps only a keyring entry id.
/// </summary>
public sealed class SensitiveSettingsTests
{
    private const string AppId = "org.example.mail";
    private const string UserFile = "/home/alice/.config/org.example.mail/settings.config";
    private const string UserPrivateFile = "/home/alice/.config/org.example.mail/settings.private.config";
    private const string SystemPrivateFile = "/etc/org.example.mail/settings.private.config";

    [Fact]
    public async Task Private_keys_are_written_to_a_0600_private_file_and_public_keys_stay_public()
    {
        (SettingsFilesHarness harness, TestAppContext app) = await CreateAsync();
        await using KazoSettingsDocument document = await KazoSettings.OpenAsync(app);

        await document.SetAsync("server", "imap.example.org");
        await document.SetAsync("signature", "Alice, sent from Kazo OS");

        Assert.Equal("server=imap.example.org\n", await harness.ReadAsync(UserFile));
        Assert.Equal("signature=Alice, sent from Kazo OS\n", await harness.ReadAsync(UserPrivateFile));
        FileSystemEntryMetadata metadata = (await harness.StatAsync(UserPrivateFile))!;
        Assert.Equal(SettingsPrivateFiles.PrivateFileMode, metadata.Permissions.Mode);
        Assert.Equal("Alice, sent from Kazo OS", document.Get<string>("signature"));
        Assert.False(document.IsMasked("signature"));
    }

    [Fact]
    public async Task A_system_private_value_is_masked_for_a_user_who_cannot_read_the_file()
    {
        (SettingsFilesHarness harness, TestAppContext app) = await CreateAsync();
        await WritePrivateAsRootAsync(harness, SystemPrivateFile, "signature=Corporate footer\nlicense-key=ABCD-1234\n");

        await using KazoSettingsDocument document = await KazoSettings.OpenAsync(app);

        KazoSettingValue signature = document.GetValue("signature");
        Assert.True(signature.IsMasked);
        Assert.Null(signature.Value);
        Assert.Equal(KazoSettingValue.MaskText, signature.DisplayText);
        Assert.Equal(SettingLayer.System, signature.Source);
        KazoSettingMaskedException masked = Assert.Throws<KazoSettingMaskedException>(() => document.Get<string>("signature"));
        Assert.Equal(SystemPrivateFile, masked.PrivatePath);
        Assert.True(document.IsMasked("license-key"));
        // Public keys are unaffected, and the file itself is refused like /etc/shadow.
        Assert.Equal("localhost", document.Get<string>("server"));
        SettingsFileReadResult cat = await SettingsTextFile.ReadAsync(app.FileSystem, VirtualPath.Parse(SystemPrivateFile));
        Assert.True(cat.IsDenied);
    }

    [Fact]
    public async Task The_users_own_private_value_wins_over_a_masked_system_one()
    {
        (SettingsFilesHarness harness, TestAppContext app) = await CreateAsync();
        await WritePrivateAsRootAsync(harness, SystemPrivateFile, "signature=Corporate footer\n");
        await using KazoSettingsDocument document = await KazoSettings.OpenAsync(app);

        await document.SetAsync("signature", "Mine");

        Assert.False(document.IsMasked("signature"));
        Assert.Equal("Mine", document.Get<string>("signature"));
        Assert.Equal(SettingLayer.User, document.GetSource("signature"));
    }

    [Fact]
    public async Task A_sensitive_key_left_in_the_public_file_is_ignored_then_moved_by_the_next_write()
    {
        (SettingsFilesHarness harness, TestAppContext app) = await CreateAsync();
        await harness.WriteAsUserAsync(UserFile, "server=mail.local\nsignature=Leaked\n");
        await using KazoSettingsDocument document = await KazoSettings.OpenAsync(app);

        Assert.Equal(string.Empty, document.Get<string>("signature"));
        Assert.Contains(
            $"settings: {UserFile}:2: sensitive key 'signature' belongs in {UserPrivateFile}, ignored",
            app.LoggedWarnings);

        await document.SetAsync("server", "mail.example.org");

        Assert.Equal("server=mail.example.org\n", await harness.ReadAsync(UserFile));
        Assert.Equal("signature=Leaked\n", await harness.ReadAsync(UserPrivateFile));
        Assert.Equal("Leaked", document.Get<string>("signature"));
    }

    [Fact]
    public async Task A_restricted_key_is_never_written_to_the_user_layer()
    {
        (_, TestAppContext app) = await CreateAsync();
        await using KazoSettingsDocument document = await KazoSettings.OpenAsync(app);

        await Assert.ThrowsAsync<InvalidOperationException>(() => document.SetAsync("license-key", "X").AsTask());
    }

    [Fact]
    public async Task A_secret_reference_stores_the_secret_in_the_keyring_and_only_its_id_in_the_file()
    {
        (SettingsFilesHarness harness, TestAppContext app) = await CreateAsync();
        await using KazoSettingsDocument document = await KazoSettings.OpenAsync(app);

        await document.SetSecretAsync("password", "correct horse battery staple");

        string privateText = (await harness.ReadAsync(UserPrivateFile))!;
        Assert.Equal("password=settings:settings:password\n", privateText);
        Assert.DoesNotContain("horse", privateText, StringComparison.Ordinal);
        Assert.Equal("correct horse battery staple", app.FakeKeyring.Secrets["settings:settings:password"]);
        Assert.Equal("correct horse battery staple", await document.GetSecretAsync("password"));
        Assert.Equal(KazoSettingValue.MaskText, document.GetValue("password").DisplayText);

        await document.SetSecretAsync("password", null);

        Assert.Empty(app.FakeKeyring.Secrets);
        Assert.Null(await document.GetSecretAsync("password"));
    }

    [Fact]
    public void Resolver_reads_restricted_keys_from_the_system_private_file_only()
    {
        SettingsSchema schema = SettingsSchema.FromManifest(Manifest.Settings!);
        LayeredConfigSnapshot snapshot = LayeredConfigResolver.Resolve(
            schema,
            new LayeredConfigFiles(null, null, "/etc/x/settings.config", "/home/a/.config/x/settings.config")
            {
                SystemPrivateContent = "license-key=SYSTEM\n",
                UserPrivateContent = "license-key=USER\n"
            });

        Assert.Equal("SYSTEM", snapshot.Values["license-key"]);
        Assert.Equal(SettingLayer.System, snapshot.Sources["license-key"]);
        LayeredConfigWarning warning = Assert.Single(snapshot.Warnings);
        Assert.Equal("restricted key 'license-key' is only honored in the system private file, ignored", warning.Message);
    }

    [Fact]
    public void Resolver_ignores_a_public_key_found_in_a_private_file()
    {
        SettingsSchema schema = SettingsSchema.FromManifest(Manifest.Settings!);
        LayeredConfigSnapshot snapshot = LayeredConfigResolver.Resolve(
            schema,
            new LayeredConfigFiles(null, null, "/etc/x/settings.config", "/home/a/.config/x/settings.config")
            {
                UserPrivateContent = "server=hidden\n"
            });

        Assert.Equal("localhost", snapshot.Values["server"]);
        Assert.Equal(
            "public key 'server' belongs in /home/a/.config/x/settings.config, ignored",
            Assert.Single(snapshot.Warnings).Message);
        Assert.Empty(snapshot.MaskedKeys);
    }

    [Fact]
    public void Apply_moves_misplaced_keys_both_ways_and_routes_edits_by_sensitivity()
    {
        SettingsSchema schema = SettingsSchema.FromManifest(Manifest.Settings!);
        SettingFieldDeclaration signature = schema.Fields["signature"];
        SettingFieldDeclaration server = schema.Fields["server"];

        (string? publicText, string? privateText) = SettingsPrivateFiles.Apply(
            schema,
            "server=a\nsignature=moved\n",
            "server=b\n",
            [(signature, null), (server, "c")]);

        // signature was moved to the private file, then removed by the edit; server came back public.
        Assert.Equal("server=c\n", publicText);
        Assert.Equal(string.Empty, privateText);
    }

    private static async Task WritePrivateAsRootAsync(SettingsFilesHarness harness, string path, string content)
    {
        FileSystemError? error = await SettingsTextFile.EditAsync(
            harness.CreateRootGateway(), VirtualPath.Parse(path), _ => content, createIfMissing: true, SettingsPrivateFiles.PrivateFileMode);
        Assert.Null(error);
    }

    private static async Task<(SettingsFilesHarness Harness, TestAppContext App)> CreateAsync()
    {
        SettingsFilesHarness harness = new();
        AuthenticatedPrincipal alice = await harness.LoginAsync(harness.Alice);
        string[] capabilities = [AppCapabilities.FileSystemPrivateRead, AppCapabilities.FileSystemPrivateWrite];
        return (harness, new TestAppContext(harness, alice, Manifest, capabilities));
    }

    private static AppManifest Manifest { get; } = new()
    {
        Id = AppId,
        Name = "Mail",
        Version = "1.0.0",
        PublisherId = "org.example",
        Description = "Sensitive settings contract test app.",
        Kind = AppKind.Window,
        EntryPoint = new AppEntryPointManifest("Kazo.Os.Platform.Core.Tests", "Mail"),
        SdkCompatibility = new AppSdkCompatibilityManifest("1.0.0"),
        Presentation = new PresentationManifest("test", AppLaunchVisibility.Visible, []),
        Resources = AppResourceProfileManifest.None,
        Settings = new AppSettingsSchemaManifest(
            1,
            [
                new AppSettingFieldManifest("server", AppSettingValueType.String, "localhost", AppSettingScope.AppUser, AppSettingSensitivity.Public),
                new AppSettingFieldManifest("signature", AppSettingValueType.String, "", AppSettingScope.AppUser, AppSettingSensitivity.Private),
                new AppSettingFieldManifest("password", AppSettingValueType.String, "", AppSettingScope.AppUser, AppSettingSensitivity.SecretReference),
                new AppSettingFieldManifest("license-key", AppSettingValueType.String, "", AppSettingScope.AppUser, AppSettingSensitivity.Restricted)
            ])
    };

    /// <summary>In-memory keyring standing in for the session keyring.</summary>
    private sealed class FakeKeyringGateway : IAppKeyringGateway
    {
        public Dictionary<string, string> Secrets { get; } = new(StringComparer.Ordinal);

        public bool IsUnlocked => true;

        public ValueTask<string?> GetAsync(string? appId, string id, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Secrets.GetValueOrDefault(id));

        public ValueTask SetAsync(string id, string secret, string? label, CancellationToken cancellationToken = default)
        {
            Secrets[id] = secret;
            return ValueTask.CompletedTask;
        }

        public ValueTask<bool> DeleteAsync(string id, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Secrets.Remove(id));

        public ValueTask<IReadOnlyList<KazoKeyringEntry>> ListAsync(bool allApps, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    /// <summary>Execution context wired to the real filesystem, a fake keyring and a capturing log.</summary>
    private sealed class TestAppContext : IAppExecutionContext, IAppLoggingGateway
    {
        internal TestAppContext(SettingsFilesHarness harness, AuthenticatedPrincipal principal, AppManifest manifest, string[] capabilities)
        {
            Manifest = manifest;
            UserId = principal.UserId.ToString();
            HomeDirectory = principal.HomePath;
            AppOperationContext operation = new()
            {
                AppId = manifest.Id,
                UserId = UserId,
                UserName = principal.LoginName.Value,
                UserAuthority = principal.Authority,
                GrantedCapabilities = new HashSet<string>(capabilities, StringComparer.Ordinal)
            };
            string[] groups = [.. principal.GroupIds.Select(group => group.ToString())];
            FileSystem = new AppFileSystemGateway(harness.FileSystem, harness.Clock, operation, groups);
            Watch = new AppFileSystemWatchGateway(harness.FileSystem, harness.TopicBus, harness.Clock, operation, groups, "1");
            GrantedCapabilities = operation.GrantedCapabilities;
        }

        public FakeKeyringGateway FakeKeyring { get; } = new();
        public IAppKeyringGateway Keyring => FakeKeyring;
        public List<string> LoggedWarnings { get; } = [];
        public AppManifest Manifest { get; }
        public Guid InstanceId { get; } = Guid.NewGuid();
        public string UserId { get; }
        public string HomeDirectory { get; }
        public AppAuthority UserAuthority => AppAuthority.User;
        public IReadOnlySet<string> GrantedCapabilities { get; }
        public SessionId SessionId { get; } = SessionId.FromGuid(Guid.NewGuid());
        public ProcessId ProcessId { get; } = ProcessId.FromInt64(1);
        public CancellationToken CancellationToken => CancellationToken.None;
        public IAppFileSystemGateway FileSystem { get; }
        public IAppFileSystemWatchGateway Watch { get; }
        public IAppLoggingGateway Logging => this;
        public ICapabilityChecker Capabilities => throw new NotSupportedException();
        public IAppEventGateway Events => throw new NotSupportedException();
        public IAppNotificationGateway Notifications => throw new NotSupportedException();
        public IAppClockGateway Clock => throw new NotSupportedException();
        public IAppProcessGateway Processes => throw new NotSupportedException();

        public void Log(DiagnosticSeverity severity, string message, IReadOnlyDictionary<string, string>? properties = null)
        {
            if (severity == DiagnosticSeverity.Warning)
            {
                LoggedWarnings.Add(message);
            }
        }
    }
}
