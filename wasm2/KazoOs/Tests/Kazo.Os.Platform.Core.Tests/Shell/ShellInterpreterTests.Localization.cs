using System.Collections;
using System.Globalization;
using System.Resources;
using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk;
using Kazo.Os.AppSdk.Shell;
using Kazo.Os.AppSdk.Globalization;
using Kazo.Os.Platform.Core.Shell;

namespace Kazo.Os.Platform.Core.Tests.Shell;

/// <summary>
/// The shell's diagnostics follow its own locale (ADR 0104), as bash translates them through
/// gettext: <c>LANG=fr_FR.UTF-8</c> prints French, <c>LC_ALL</c> overrides it, and names, paths and
/// program prefixes (<c>hsh: </c>, <c>sudo: </c>) stay as they are.
/// </summary>
public sealed partial class ShellInterpreterTests
{
    [Fact]
    public async Task A_french_session_prints_command_not_found_and_syntax_errors_in_french()
    {
        Fixture fixture = await Fixture.CreateAsync();
        fixture.Session.SetVariable("LANG", "fr_FR.UTF-8");

        Output missing = await fixture.RunAsync("nope arg");
        Output syntax = await fixture.RunAsync("echo a |");
        Output quote = await fixture.RunAsync("echo 'open");

        Assert.Equal(127, missing.ExitCode);
        Assert.Equal("hsh: nope : commande introuvable\n", missing.Stderr);
        Assert.Equal(2, syntax.ExitCode);
        Assert.Equal("hsh: erreur de syntaxe : fin de ligne inattendue après « | »\n", syntax.Stderr);
        Assert.Equal("hsh: erreur de syntaxe : apostrophe non fermée\n", quote.Stderr);
        // The structured error keeps its English message for logs and callers.
        Assert.Equal("syntax error: unexpected end of line after `|'", syntax.Result.SyntaxError?.Message);
    }

    [Fact]
    public async Task Lc_all_overrides_lang_and_the_language_changes_between_two_lines()
    {
        Fixture fixture = await Fixture.CreateAsync();
        fixture.Session.SetVariable("LANG", "fr_CA.UTF-8");
        fixture.Session.SetVariable("LC_ALL", "en_US.UTF-8");

        Output english = await fixture.RunAsync("nope");
        await fixture.RunAsync("LC_ALL=");
        Output french = await fixture.RunAsync("nope");

        Assert.Equal("hsh: nope: command not found\n", english.Stderr);
        Assert.Equal("hsh: nope : commande introuvable\n", french.Stderr);
    }

    [Fact]
    public async Task Expansion_errors_and_type_descriptions_are_translated()
    {
        Fixture fixture = await Fixture.CreateAsync();
        fixture.Session.SetVariable("LANG", "fr_FR.UTF-8");

        Output division = await fixture.RunAsync("echo $((1/0))");
        Output keyword = await fixture.RunAsync("type if");
        Output unset = await fixture.RunAsync("echo ${MISSING?}");

        Assert.Equal("hsh: 1/0 : division par 0 (le symbole erroné est « 0 »)\n", division.Stderr);
        Assert.Equal("if est un mot-clé du shell\n", keyword.Stdout);
        Assert.Equal("hsh: MISSING : paramètre non défini\n", unset.Stderr);
    }

    [Fact]
    public async Task Sudo_speaks_the_language_of_the_shell()
    {
        Fixture fixture = await Fixture.CreateAsync();
        fixture.Session.SetVariable("LANG", "fr_FR.UTF-8");
        List<string> prompts = [];

        Output refused = await fixture.RunAsync("sudo amroot", credentialPrompt: (description, _) =>
        {
            prompts.Add(description);
            return Task.FromResult<ShellElevation?>(null);
        });
        Output builtin = await fixture.RunAsync("sudo cd /root");

        Assert.Equal(["Exécuter en tant que root : amroot"], prompts);
        Assert.Equal("sudo: échec de l'authentification\n", refused.Stderr);
        Assert.Equal("sudo: cd : primitive du shell\n", builtin.Stderr);
    }

    [Fact]
    public async Task A_hosted_french_shell_asks_for_sudo_credentials_in_french()
    {
        Fixture fixture = await Fixture.CreateAsync();
        fixture.Grant(AppCapabilities.ShellHost);
        RecordingPrompt prompt = new() { Credential = new HackCredential("admin", "wrong") };
        await using IHackShell shell = await HackShell.StartAsync(fixture.Host, new HackShellOptions { CredentialPrompt = prompt });

        await RunHostedAsync(shell, "LANG=fr_FR.UTF-8");
        (int status, _, string error) = await RunHostedAsync(shell, "sudo amroot");

        Assert.Equal(1, status);
        Assert.Equal("sudo: échec de l'authentification\n", error);
        HackCredentialRequest request = Assert.Single(prompt.Requests);
        Assert.Equal("Autorisation administrateur requise", request.Title);
        Assert.Equal("Exécuter en tant que root : amroot", request.Description);
        Assert.Equal(["Désolé, veuillez réessayer."], prompt.Lines);
    }

    [Fact]
    public void Every_catalog_capability_is_described_in_english_and_in_french()
    {
        CultureInfo french = CultureInfo.GetCultureInfo("fr");
        LocalizerProbe probe = new(Strings.For(HackLocale.DefaultLanguage));

        foreach (string capability in AppCapabilities.All)
        {
            Assert.True(probe.Has($"Capability.{capability}.Title"), capability);
            Assert.Equal(AppCapabilities.IsDangerous(capability), probe.Has($"Capability.{capability}.Risk"));
        }

        CapabilityDescription english = Kazo.Os.Platform.Core.Policy.CapabilityText.Describe(AppCapabilities.ShellHost, null);
        CapabilityDescription inFrench = Kazo.Os.Platform.Core.Policy.CapabilityText.Describe(AppCapabilities.ShellHost, french);
        Assert.Equal("Host a shell", english.Title);
        Assert.Equal("Run commands on your behalf, with the rights of those commands.", english.RiskDescription);
        Assert.Equal("Héberger un shell", inFrench.Title);
        Assert.Equal(CapabilityRisk.Dangerous, inFrench.Risk);
        Assert.Null(Kazo.Os.Platform.Core.Policy.CapabilityText.Describe(AppCapabilities.ClipboardRead, french).RiskDescription);
    }

    private sealed class LocalizerProbe(Microsoft.Extensions.Localization.IStringLocalizer text)
    {
        public bool Has(string key) => !text[key].ResourceNotFound;
    }

    [Fact]
    public void Every_core_text_exists_in_english_and_in_french()
    {
        ResourceManager resources = HackLocalization.GetResourceManager(typeof(Strings));
        HashSet<string> english = Keys(resources.GetResourceSet(CultureInfo.InvariantCulture, createIfNotExists: true, tryParents: false));
        HashSet<string> french = Keys(resources.GetResourceSet(CultureInfo.GetCultureInfo("fr"), createIfNotExists: true, tryParents: false));

        Assert.NotEmpty(english);
        Assert.Empty(english.Except(french));
        Assert.Empty(french.Except(english));
        Assert.Equal("{0} : commande introuvable", Strings.For(CultureInfo.GetCultureInfo("fr"))["Shell.CommandNotFound"].Value);
        Assert.Equal("{0}: command not found", Strings.For(HackLocale.DefaultLanguage)["Shell.CommandNotFound"].Value);

        static HashSet<string> Keys(ResourceSet? set) =>
            set is null ? [] : [.. set.Cast<DictionaryEntry>().Select(entry => (string)entry.Key)];
    }
}
