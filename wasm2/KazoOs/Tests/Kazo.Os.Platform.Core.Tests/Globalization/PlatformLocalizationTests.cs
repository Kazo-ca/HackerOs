using System.Globalization;
using Kazo.Os.App.Abstractions;
using Kazo.Os.Platform.Core.Globalization;
using Kazo.Os.Platform.Core.Processes;
using Kazo.Os.Platform.Core.Tests.FileSystem.Virtual;
using Kazo.Os.Simulation.Abstractions.FileSystem;
using Kazo.Os.Simulation.Abstractions.Sessions;

namespace Kazo.Os.Platform.Core.Tests.Globalization;

/// <summary>
/// The platform side of ADR 0104: the locale files of the login environment, the session
/// language, translated catalog texts and the platform's own resources.
/// </summary>
public sealed class PlatformLocalizationTests
{
    private static readonly CultureInfo French = CultureInfo.GetCultureInfo("fr");

    [Fact]
    public void Login_applies_system_locale_then_etc_environment_then_the_users_locale()
    {
        IReadOnlyDictionary<string, string> systemOnly = LoginEnvironment.Build(
            "alice", "/home/alice", environmentFile: null, systemLocaleFile: "LANG=fr_FR.UTF-8\nLC_TIME=en_GB.UTF-8\n");
        IReadOnlyDictionary<string, string> environmentWins = LoginEnvironment.Build(
            "alice", "/home/alice", "LANG=fr_CA.UTF-8\n", systemLocaleFile: "LANG=fr_FR.UTF-8\n");
        IReadOnlyDictionary<string, string> userWins = LoginEnvironment.Build(
            "alice", "/home/alice", "LANG=fr_CA.UTF-8\n", systemLocaleFile: "LANG=fr_FR.UTF-8\n", userLocaleFile: "LANG=en_GB.UTF-8\n");

        Assert.Equal(("fr_FR.UTF-8", "en_GB.UTF-8"), (systemOnly["LANG"], systemOnly["LC_TIME"]));
        Assert.Equal("fr_CA.UTF-8", environmentWins["LANG"]);
        Assert.Equal("en_GB.UTF-8", userWins["LANG"]);
    }

    [Fact]
    public void A_locale_file_cannot_set_other_variables_and_reports_its_bad_lines()
    {
        List<string> warnings = [];

        IReadOnlyDictionary<string, string> variables = LoginEnvironment.Build(
            "alice", "/home/alice", environmentFile: null, warnings, userLocaleFile: "LANG=fr_FR.UTF-8\nPATH=/evil\n");

        Assert.Equal("/bin:/usr/bin", variables["PATH"]);
        Assert.Equal("fr_FR.UTF-8", variables["LANG"]);
        Assert.Contains(warnings, warning => warning.Contains("/home/alice/.config/locale.conf:2", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_seeder_creates_etc_locale_conf_in_English_root_owned_0644()
    {
        VirtualFileSystemFixture fixture = await VirtualFileSystemFixture.CreateSeededAsync();

        string content = await fixture.ReadTextAsync("/etc/locale.conf", fixture.AppContext(null, AppCapabilities.FileSystemSystemRead));
        FileSystemResult<FileSystemEntrySnapshot> stat = await fixture.Service.StatAsync(
            new FileSystemStatRequest(VirtualPath.Parse("/etc/locale.conf")), fixture.SystemContext());

        Assert.Contains("LANG=en_US.UTF-8", content, StringComparison.Ordinal);
        Assert.Equal(0x1A4, stat.Value!.Metadata.Permissions.Mode);
        Assert.Equal("system", stat.Value.Metadata.OwnerId);
    }

    [Fact]
    public async Task The_session_speaks_the_system_language_before_sign_in_and_the_users_after()
    {
        VirtualFileSystemFixture fixture = await VirtualFileSystemFixture.CreateSeededAsync();
        await WriteAsync(fixture, "/etc/locale.conf", "LANG=fr_FR.UTF-8\n");
        await WriteAsync(fixture, "/home/alice/.config/locale.conf", "LANG=en_GB.UTF-8\nLC_TIME=fr_CA.UTF-8\n");
        SessionLocaleService locale = new(new LoginEnvironmentProvider(fixture.Service, fixture.Clock), fixture.Service, fixture.Clock);
        int changes = 0;
        locale.Changed += () => changes++;

        await locale.UseSystemLocaleAsync();
        Assert.Equal(("fr", "fr-FR", "fr_FR.UTF-8"), (locale.Culture.Name, locale.FormatCulture.Name, locale.LocaleName));

        await locale.UseSessionLocaleAsync(Principal("alice"));
        Assert.Equal(("en", "fr-CA"), (locale.Culture.Name, locale.FormatCulture.Name));
        Assert.Equal(2, changes);
    }

    [Fact]
    public async Task Without_a_LANG_the_session_is_English()
    {
        VirtualFileSystemFixture fixture = await VirtualFileSystemFixture.CreateSeededAsync();
        await WriteAsync(fixture, "/etc/locale.conf", "# no locale set\n");
        SessionLocaleService locale = new(new LoginEnvironmentProvider(fixture.Service, fixture.Clock), fixture.Service, fixture.Clock);

        await locale.UseSystemLocaleAsync();

        Assert.Equal(("en", "en-US", "en_US.UTF-8"), (locale.Culture.Name, locale.FormatCulture.Name, locale.LocaleName));
    }

    [Fact]
    public void The_catalog_shows_translated_names_but_keeps_ids_and_command_names()
    {
        AppManifest ls = new()
        {
            Id = "org.tests.ls",
            Name = "ls",
            Version = "1.0.0",
            PublisherId = "pub.tests",
            Description = "List directory contents",
            Kind = AppKind.Terminal,
            EntryPoint = new AppEntryPointManifest("Tests.dll", "Tests.Ls"),
            SdkCompatibility = new AppSdkCompatibilityManifest("1.0.0"),
            Presentation = new PresentationManifest("utilities", AppLaunchVisibility.Hidden, []),
            Resources = AppResourceProfileManifest.None,
            Terminal = new TerminalCommandManifest("ls", [], "ls [path]")
        };
        AppCatalog catalog = AppCatalog.BuildFromBuiltInApplications(
        [
            new BuiltInApplication(true, ls, Localized: new Dictionary<string, AppManifestText>
            {
                ["fr"] = new("ls", "Lister le contenu d'un dossier", "ls [chemin]")
            })
        ]).Catalog!;

        AppManifest french = catalog.Localize(ls, CultureInfo.GetCultureInfo("fr-CA"));
        AppManifest english = catalog.Localize(ls, CultureInfo.GetCultureInfo("en"));

        Assert.Equal(("org.tests.ls", "Lister le contenu d'un dossier", "ls", "ls [chemin]"), (french.Id, french.Description, french.Terminal!.Name, french.Terminal.Usage));
        Assert.Same(ls, english);
    }

    [Fact]
    public void The_platform_resources_exist_in_both_languages()
    {
        Assert.Equal("Start Calculator", Strings.For(CultureInfo.GetCultureInfo("en"))["Shell.StartApp", "Calculator"].Value);
        Assert.Equal("Démarrer Calculatrice", Strings.For(French)["Shell.StartApp", "Calculatrice"].Value);
    }

    private static async Task WriteAsync(VirtualFileSystemFixture fixture, string path, string content)
    {
        VirtualPath file = VirtualPath.Parse(path);
        VirtualPath parent = VirtualPath.Parse(path[..path.LastIndexOf('/')]);
        FileSystemResult<FileSystemEntrySnapshot> parentStat = await fixture.Service.StatAsync(new FileSystemStatRequest(parent), fixture.SystemContext());
        if (!parentStat.Succeeded)
        {
            await fixture.Service.CreateAsync(new FileSystemCreateRequest(parent, FileSystemEntryKind.Directory, FileSystemPermissions.FromMode(0x1ED)), fixture.SystemContext());
        }

        FileSystemResult<FileSystemEntrySnapshot> stat = await fixture.Service.StatAsync(new FileSystemStatRequest(file), fixture.SystemContext());
        if (!stat.Succeeded)
        {
            await fixture.Service.CreateAsync(new FileSystemCreateRequest(file, FileSystemEntryKind.File, FileSystemPermissions.FromMode(0x1A4)), fixture.SystemContext());
        }

        FileSystemMutationResult write = await fixture.Service.WriteAsync(
            new FileSystemWriteRequest(file),
            new VirtualFileSystemFixture.BytesSource(System.Text.Encoding.UTF8.GetBytes(content)),
            fixture.SystemContext());
        Assert.True(write.Succeeded, write.Transaction.Error?.Code.ToString());
    }

    private static AuthenticatedPrincipal Principal(string login)
    {
        LocalGroupId group = LocalGroupId.FromGuid(Guid.NewGuid());
        return new AuthenticatedPrincipal(
            SessionId.FromGuid(Guid.NewGuid()),
            LocalUserId.FromGuid(Guid.NewGuid()),
            LocalLoginName.Parse(login),
            login,
            AppAuthority.User,
            group,
            [group],
            InstallationId.FromGuid(Guid.NewGuid()),
            DeviceId.FromGuid(Guid.NewGuid()),
            DateTimeOffset.UnixEpoch);
    }
}
