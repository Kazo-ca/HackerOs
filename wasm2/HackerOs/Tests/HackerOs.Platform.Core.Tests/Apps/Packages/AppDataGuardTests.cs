using HackerOs.App.Abstractions;
using HackerOs.App.Abstractions.Policy;
using HackerOs.Platform.Core.Apps.Packages;
using HackerOs.Platform.Core.Tests.Settings;
using HackerOs.Simulation.Abstractions.Gateways;
using HackerOs.Simulation.Abstractions.Sessions;

namespace HackerOs.Platform.Core.Tests.Apps.Packages;

/// <summary>ADR 0214 §4.2: the OS backs up an installed app's data when its version changes; the app migrates it.</summary>
public sealed class AppDataGuardTests
{
    private const string Home = "/home/alice";
    private const string Settings = Home + "/.config/" + AppPackageTests.AppId + "/settings.config";
    private const string Data = Home + "/.local/share/" + AppPackageTests.AppId + "/notes.txt";
    private const string Backups = Home + "/" + AppDataGuard.BackupsUnderHome + "/" + AppPackageTests.AppId;

    [Fact]
    public async Task The_first_launch_records_the_version_without_a_backup()
    {
        (SettingsFilesHarness harness, IAppFileSystemGateway user, AppDataGuard guard) = await CreateAsync();

        IReadOnlyDictionary<string, string>? environment = await guard.PrepareAsync(user, Home, Manifest("1.0.0"));

        Assert.Null(environment);
        Assert.Equal(SemanticVersion.Parse("1.0.0"), AppDataGuard.ReadVersion(await harness.ReadAsync(AppDataGuard.GetVersionsPath(Home).Value), AppPackageTests.AppId));
        Assert.Null(await harness.StatAsync(Backups));
    }

    [Fact]
    public async Task An_update_backs_up_the_data_and_hands_the_previous_version_to_the_app()
    {
        (SettingsFilesHarness harness, IAppFileSystemGateway user, AppDataGuard guard) = await CreateAsync();
        await guard.PrepareAsync(user, Home, Manifest("1.0.0"));
        await harness.WriteAsUserAsync(Settings, "theme=dark\n");
        await harness.WriteAsUserAsync(Data, "v1 notes");

        IReadOnlyDictionary<string, string>? environment = await guard.PrepareAsync(user, Home, Manifest("1.1.0"));
        IReadOnlyDictionary<string, string>? second = await guard.PrepareAsync(user, Home, Manifest("1.1.0"));

        Assert.Equal("1.0.0", Assert.Contains(AppDataGuard.PreviousVersionVariable, environment!));
        Assert.Null(second);
        Assert.Equal("theme=dark\n", await harness.ReadAsync($"{Backups}/1.0.0/.config/settings.config"));
        Assert.Equal("v1 notes", await harness.ReadAsync($"{Backups}/1.0.0/.local-share/notes.txt"));
        Assert.Equal("theme=dark\n", await harness.ReadAsync(Settings));
    }

    [Fact]
    public async Task A_rollback_puts_back_the_data_of_the_version_it_returns_to()
    {
        (SettingsFilesHarness harness, IAppFileSystemGateway user, AppDataGuard guard) = await CreateAsync();
        await guard.PrepareAsync(user, Home, Manifest("1.0.0"));
        await harness.WriteAsUserAsync(Settings, "format=1\n");
        await guard.PrepareAsync(user, Home, Manifest("1.1.0"));
        // 1.1.0 migrated the data to its own format.
        await harness.WriteAsUserAsync(Settings, "format=2\n");

        IReadOnlyDictionary<string, string>? environment = await guard.PrepareAsync(user, Home, Manifest("1.0.0"));

        Assert.Null(environment);
        Assert.Equal("format=1\n", await harness.ReadAsync(Settings));
        Assert.Equal("format=2\n", await harness.ReadAsync($"{Backups}/1.1.0/.config/settings.config"));
    }

    [Fact]
    public async Task At_most_two_backups_are_kept()
    {
        (SettingsFilesHarness harness, IAppFileSystemGateway user, AppDataGuard guard) = await CreateAsync();
        await harness.WriteAsUserAsync(Settings, "x=1\n");
        foreach (string version in new[] { "1.0.0", "1.1.0", "1.2.0", "1.3.0" })
        {
            await guard.PrepareAsync(user, Home, Manifest(version));
        }

        Assert.Null(await harness.StatAsync($"{Backups}/1.0.0"));
        Assert.Null(await harness.StatAsync($"{Backups}/1.1.0"));
        Assert.NotNull(await harness.StatAsync($"{Backups}/1.2.0"));
    }

    [Fact]
    public async Task An_app_that_was_not_installed_from_a_package_is_left_alone()
    {
        (SettingsFilesHarness harness, IAppFileSystemGateway user, _) = await CreateAsync();
        AppDataGuard guard = new(() => new InstalledPackageCatalog());

        Assert.Null(await guard.PrepareAsync(user, Home, Manifest("1.0.0")));
        Assert.Null(await harness.ReadAsync(AppDataGuard.GetVersionsPath(Home).Value));
    }

    private static async Task<(SettingsFilesHarness Harness, IAppFileSystemGateway User, AppDataGuard Guard)> CreateAsync()
    {
        SettingsFilesHarness harness = new();
        await harness.BootAsync();
        AuthenticatedPrincipal alice = await harness.LoginAsync(harness.Alice);
        IAppFileSystemGateway user = harness.CreateAppGateway(
            alice, "org.hackeros.shell", AppCapabilities.FileSystemUserHomeRead, AppCapabilities.FileSystemUserHomeWrite);
        InstalledPackageCatalog packages = new();
        packages.Apply([new InstalledApp(AppPackageSource.ForCatalog("official", AppPackageTests.AppId, SemanticVersion.Parse("1.0.0")), Manifest("1.0.0"))]);
        return (harness, user, new AppDataGuard(() => packages));
    }

    private static AppManifest Manifest(string version) => new()
    {
        Id = AppPackageTests.AppId,
        Name = "Sample",
        Version = version,
        PublisherId = "pub.kazo",
        Description = "Sample",
        Kind = AppKind.Window,
        EntryPoint = new AppEntryPointManifest("Kazo.Sample", "Kazo.Sample.SampleWindow"),
        SdkCompatibility = new AppSdkCompatibilityManifest(HackerOsSdk.Version),
        Presentation = new PresentationManifest("utilities", AppLaunchVisibility.Visible, []),
        Resources = AppResourceProfileManifest.None
    };
}
