using HackerOs.App.Abstractions;
using HackerOs.AppSdk;
using HackerOs.Commands.Mkdir;
using HackerOs.Commands.Printf;
using HackerOs.Commands.Rm;
using HackerOs.Commands.Seq;
using HackerOs.Commands.Sleep;
using HackerOs.Commands.Sudo;
using HackerOs.Commands.Test;
using HackerOs.Commands.XdgOpen;
using HackerOs.Tests.Support;
using Xunit;

namespace HackerOs.Commands.Tests;

/// <summary>
/// ADR 0104 for the commands M to Z: their messages follow <c>LANG</c> of their own process,
/// French typography included, and stay byte-identical in English by default.
/// </summary>
public sealed class LocalizedCommandsMzTests
{
    private const string French = "fr_FR.UTF-8";

    [Fact]
    public async Task Mkdir_translates_its_usage_error_and_the_errno_text()
    {
        MkdirCommand command = new(AppDefinitionMapper.ToManifest(new MkdirApp()));

        Assert.Equal("mkdir: missing operand", (await RunAsync(command, null)).Error.TrimEnd());
        Assert.Equal("mkdir : opérande manquant", (await RunAsync(command, French)).Error.TrimEnd());

        (int status, _, string error) = await RunAsync(command, French, "missing/child");
        Assert.Equal(1, status);
        Assert.Equal("mkdir : impossible de créer le dossier « missing/child » : Aucun fichier ou dossier de ce nom", error.TrimEnd());
    }

    [Fact]
    public async Task Rm_reports_a_missing_file_in_the_process_language()
    {
        RmCommand command = new(AppDefinitionMapper.ToManifest(new RmApp()));

        Assert.Equal("rm: cannot remove 'ghost.txt': No such file or directory", (await RunAsync(command, null, "ghost.txt")).Error.TrimEnd());
        Assert.Equal("rm : impossible de supprimer « ghost.txt » : Aucun fichier ou dossier de ce nom", (await RunAsync(command, French, "ghost.txt")).Error.TrimEnd());
        Assert.Equal("rm : option invalide -- « z »", (await RunAsync(command, French, "-z", "x")).Error.TrimEnd());
    }

    [Fact]
    public async Task Text_utilities_translate_their_errors()
    {
        SeqCommand seq = new(AppDefinitionMapper.ToManifest(new SeqApp()));
        SleepCommand sleep = new(AppDefinitionMapper.ToManifest(new SleepApp()));
        PrintfCommand printf = new(AppDefinitionMapper.ToManifest(new PrintfApp()));
        TestCommand test = new(AppDefinitionMapper.ToManifest(new TestApp()));

        Assert.Equal("seq : argument à virgule flottante invalide : « abc »", (await RunAsync(seq, French, "abc")).Error.TrimEnd());
        Assert.Equal("sleep : intervalle de temps invalide « soon »", (await RunAsync(sleep, French, "soon")).Error.TrimEnd());
        Assert.Equal("printf : utilisation : printf format [arguments]", (await RunAsync(printf, French)).Error.TrimEnd());
        Assert.Equal("printf: usage: printf format [arguments]", (await RunAsync(printf, null)).Error.TrimEnd());
        Assert.Equal("test : x : nombre entier attendu", (await RunAsync(test, French, "1", "-eq", "x")).Error.TrimEnd());
        Assert.Equal("test: x: integer expression expected", (await RunAsync(test, null, "1", "-eq", "x")).Error.TrimEnd());
    }

    [Fact]
    public async Task Sudo_and_xdg_open_print_their_usage_in_french()
    {
        SudoCommand sudo = new(AppDefinitionMapper.ToManifest(new SudoApp()));
        XdgOpenCommand xdgOpen = new(AppDefinitionMapper.ToManifest(new XdgOpenApp()));

        Assert.Equal("utilisation : sudo <commande> [arguments...]", (await RunAsync(sudo, French)).Error.TrimEnd());
        Assert.Equal("utilisation : xdg-open { fichier | URL }", (await RunAsync(xdgOpen, French, "--help")).Output.TrimEnd());
        Assert.Equal("usage: xdg-open { file | URL }", (await RunAsync(xdgOpen, null, "--help")).Output.TrimEnd());
    }

    [Fact]
    public void Every_command_ships_a_french_description_and_usage()
    {
        // The build turns Resources/AppManifest.fr.resx into localization/<appId>/fr.json and
        // references it from manifests/<appId>.json (ADR 0104, section 4): read what was committed.
        string project = FindCommandsProject();
        string[] manifests = Directory.GetFiles(Path.Combine(project, "manifests"), "*.json");
        Assert.Equal(76, manifests.Length);
        foreach (string file in manifests)
        {
            AppManifest manifest = AppManifestJsonSerializer.DeserializeStrict(File.ReadAllText(file));
            LocalizationManifest localization = Assert.Single(manifest.Localizations, static entry => entry.Culture == "fr");
            AppManifestText french = AppManifestTextJsonSerializer.DeserializeStrict(
                File.ReadAllText(Path.Combine(project, localization.ResourcePath)));

            // Command names never change (ADR 0104); description and usage are French.
            Assert.Equal(manifest.Name, french.Name);
            Assert.NotEqual(manifest.Description, french.Description);
            Assert.False(string.IsNullOrWhiteSpace(french.TerminalUsage), $"{manifest.Id} has no French usage.");
        }

        AppManifestText mkdir = AppManifestTextJsonSerializer.DeserializeStrict(
            File.ReadAllText(Path.Combine(project, "localization", "org.hackeros.commands.mkdir", "fr.json")));
        Assert.Equal("mkdir [-pv] <dossier...>", mkdir.TerminalUsage);
    }

    private static string FindCommandsProject()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            string candidate = Path.Combine(directory.FullName, "Apps", "Commands", "HackerOs.Commands");
            if (Directory.Exists(Path.Combine(candidate, "manifests")))
            {
                return candidate;
            }
        }

        throw new DirectoryNotFoundException("Apps/Commands/HackerOs.Commands not found above the test output.");
    }

    /// <summary>Runs <paramref name="command"/> in a process whose <c>LANG</c> is <paramref name="lang"/> (unset when null).</summary>
    private static async Task<(int Status, string Output, string Error)> RunAsync(TerminalAppBase command, string? lang, params string[] arguments)
    {
        FakeAppEnvironmentGateway environment = new("/home/user", "user");
        if (lang is not null)
        {
            environment.SetVariable("LANG", lang);
        }

        FakeAppFileSystemGateway fileSystem = new FakeAppFileSystemGateway().WithDirectory("/home/user");
        using StringWriter stdout = new();
        using StringWriter stderr = new();
        TerminalExecutionContext context = new(
            new MinimalAppExecutionContext(command.Manifest, fileSystem: fileSystem, environment: environment),
            arguments,
            new StringReader(string.Empty),
            stdout,
            stderr,
            "/home/user",
            new Dictionary<string, string>());
        int status = await command.ExecuteAsync(context, CancellationToken.None);
        return (status, stdout.ToString(), stderr.ToString());
    }
}
