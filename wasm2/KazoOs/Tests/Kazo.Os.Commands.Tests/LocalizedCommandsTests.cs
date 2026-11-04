using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Resources;
using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk;
using Kazo.Os.Commands.Basename;
using Kazo.Os.Commands.Cat;
using Kazo.Os.Commands.Chmod;
using Kazo.Os.Commands.Cp;
using Kazo.Os.Commands.Head;
using Kazo.Os.Commands.Ls;
using Kazo.Os.Tests.Support;
using Xunit;

namespace Kazo.Os.Commands.Tests;

/// <summary>
/// ADR 0104: the commands print their messages in the language of their own process
/// (<c>LANG=fr_FR.UTF-8</c>), English by default, with the same keys in both languages.
/// </summary>
public sealed class LocalizedCommandsTests
{
    [Fact]
    public async Task Cat_prints_the_errno_text_in_the_process_language()
    {
        CatCommand command = new(AppDefinitionMapper.ToManifest(new CatApp()));

        (int english, _, string englishError) = await RunAsync(command, null, "missing.txt");
        (int french, _, string frenchError) = await RunAsync(command, "fr_FR.UTF-8", "missing.txt");

        Assert.Equal([1, 1], [english, french]);
        Assert.Equal("cat: missing.txt: No such file or directory", englishError.TrimEnd());
        Assert.Equal("cat : missing.txt : Aucun fichier ou dossier de ce nom", frenchError.TrimEnd());
    }

    [Fact]
    public async Task Ls_reports_a_missing_directory_in_french()
    {
        LsCommand command = new(AppDefinitionMapper.ToManifest(new LsApp()));

        (int status, _, string error) = await RunAsync(command, "fr_FR.UTF-8", "/nope");

        Assert.Equal(1, status);
        Assert.Equal("ls : impossible d'accéder à « /nope » : Aucun fichier ou dossier de ce nom", error.TrimEnd());
    }

    [Fact]
    public async Task Usage_errors_follow_LANG_and_stay_english_by_default()
    {
        BasenameCommand basename = new(AppDefinitionMapper.ToManifest(new BasenameApp()));
        ChmodCommand chmod = new(AppDefinitionMapper.ToManifest(new ChmodApp()));
        HeadCommand head = new(AppDefinitionMapper.ToManifest(new HeadApp()));

        Assert.Equal("basename: missing operand", (await RunAsync(basename, null)).Error.TrimEnd());
        Assert.Equal("basename : opérande manquant", (await RunAsync(basename, "fr_FR.UTF-8")).Error.TrimEnd());
        Assert.Equal("chmod : opérande manquant après « 755 »", (await RunAsync(chmod, "fr_CA.UTF-8", "755")).Error.TrimEnd());
        Assert.Equal("head : nombre de lignes non valide : « x »", (await RunAsync(head, "fr_FR.UTF-8", "-n", "x")).Error.TrimEnd());
    }

    [Fact]
    public async Task Cp_translates_its_message_and_the_errno_text()
    {
        CpCommand command = new(AppDefinitionMapper.ToManifest(new CpApp()));

        (int status, _, string error) = await RunAsync(command, "fr_FR.UTF-8", "missing.txt", "copy.txt");

        Assert.Equal(1, status);
        Assert.Equal("cp : impossible d'évaluer « missing.txt » : Aucun fichier ou dossier de ce nom", error.TrimEnd());
    }

    [Fact]
    public void Every_command_resource_has_the_same_keys_in_english_and_french()
    {
        Assembly assembly = typeof(CatCommand).Assembly;
        List<Type> markers = [.. assembly.GetTypes().Where(type => type.Name == "Strings" && type.Namespace?.StartsWith("Kazo.Os.Commands.", StringComparison.Ordinal) == true)];
        Assert.NotEmpty(markers);
        foreach (Type marker in markers)
        {
            string baseName = assembly.GetName().Name + ".Resources." + marker.FullName![(assembly.GetName().Name!.Length + 1)..];
            ResourceManager resources = new(baseName, assembly);
            HashSet<string> english = Keys(resources.GetResourceSet(CultureInfo.InvariantCulture, createIfNotExists: true, tryParents: false));
            HashSet<string> french = Keys(resources.GetResourceSet(CultureInfo.GetCultureInfo("fr"), createIfNotExists: true, tryParents: false));
            Assert.True(english.Count > 0, $"{baseName} has no English texts.");
            Assert.True(english.SetEquals(french), $"{baseName}: keys differ ({string.Join(", ", english.Union(french).Except(english.Intersect(french)))}).");
        }
    }

    private static HashSet<string> Keys(ResourceSet? set) =>
        set is null ? [] : [.. set.Cast<DictionaryEntry>().Select(entry => (string)entry.Key)];

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
