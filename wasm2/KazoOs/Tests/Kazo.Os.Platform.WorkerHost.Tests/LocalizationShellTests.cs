namespace Kazo.Os.Platform.WorkerHost.Tests;

/// <summary>
/// ADR 0104 through the real shell, on the main thread and in Web Workers: each command answers in
/// the language of its own process, and <c>localectl</c> guards <c>/etc/locale.conf</c>.
/// </summary>
public sealed class LocalizationShellTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_command_speaks_the_LANG_of_its_own_process(bool workers)
    {
        await using OsFixture os = await OsFixture.CreateAsync(workers);

        ShellRun english = await os.RunAsync("help nope");
        ShellRun french = await os.RunAsync("LANG=fr_FR.UTF-8 help nope");
        ShellRun frenchError = await os.RunAsync("LANG=fr_CA.UTF-8 localectl bogus");
        ShellRun back = await os.RunAsync("help nope");

        Assert.Contains("help: no help topics match 'nope'", english.Stderr, StringComparison.Ordinal);
        Assert.Contains("help : aucune rubrique d'aide ne correspond à « nope »", french.Stderr, StringComparison.Ordinal);
        Assert.Contains("verbe de commande inconnu « bogus »", frenchError.Stderr, StringComparison.Ordinal);
        Assert.Equal(english.Stderr, back.Stderr);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_exported_LANG_translates_help_and_the_manifest_texts(bool workers)
    {
        await using OsFixture os = await OsFixture.CreateAsync(workers);

        ShellRun run = await os.RunAsync("export LANG=fr_FR.UTF-8 && help && help localectl");

        Assert.Equal(0, run.ExitCode);
        Assert.Contains("Commandes installées :", run.Stdout, StringComparison.Ordinal);
        // Command names never change; the builtins' descriptions come from Platform.Core's resources.
        Assert.Contains("localectl", run.Stdout, StringComparison.Ordinal);
        Assert.Contains("utilisation : ", run.Stdout, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Commands_m_to_z_follow_LANG(bool workers)
    {
        await using OsFixture os = await OsFixture.CreateAsync(workers);

        ShellRun mkdir = await os.RunAsync("LANG=fr_FR.UTF-8 mkdir");
        ShellRun rm = await os.RunAsync("LANG=fr_FR.UTF-8 rm ghost.txt");
        ShellRun english = await os.RunAsync("rm ghost.txt");

        Assert.Contains("mkdir : opérande manquant", mkdir.Stderr, StringComparison.Ordinal);
        Assert.Contains("rm : impossible de supprimer « ghost.txt » : Aucun fichier ou dossier de ce nom", rm.Stderr, StringComparison.Ordinal);
        Assert.Contains("rm: cannot remove 'ghost.txt': No such file or directory", english.Stderr, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Localectl_shows_the_system_locale_and_only_root_may_change_it(bool workers)
    {
        await using OsFixture os = await OsFixture.CreateAsync(workers);

        ShellRun status = await os.RunAsync("localectl");
        ShellRun list = await os.RunAsync("localectl list-locales");
        ShellRun unknown = await os.RunAsync("localectl set-locale LANG=xx_YY.UTF-8");
        ShellRun denied = await os.RunAsync("localectl set-locale LANG=fr_FR.UTF-8");

        Assert.Equal("System Locale: LANG=en_US.UTF-8\n", status.Stdout);
        Assert.Contains("fr_CA.UTF-8\n", list.Stdout, StringComparison.Ordinal);
        Assert.Equal(8, list.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
        Assert.Equal(1, unknown.ExitCode);
        Assert.Contains("locale 'xx_YY.UTF-8' is not installed", unknown.Stderr, StringComparison.Ordinal);
        Assert.Equal(1, denied.ExitCode);
        Assert.True(denied.Stderr.Contains("Access denied (try sudo)", StringComparison.Ordinal), denied.Stderr);
        Assert.Equal("System Locale: LANG=en_US.UTF-8\n", (await os.RunAsync("localectl status")).Stdout);
    }
}
