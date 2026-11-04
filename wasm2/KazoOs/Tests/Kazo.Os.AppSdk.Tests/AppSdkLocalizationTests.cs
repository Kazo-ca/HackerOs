using System.Collections;
using System.Globalization;
using System.Resources;
using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk.Globalization;
using Kazo.Os.AppSdk.IO;
using Kazo.Os.AppSdk.Net.Hosting;
using Kazo.Os.Tests.Support;

namespace Kazo.Os.AppSdk.Tests;

/// <summary>
/// ADR 0104: the core SDK's user-facing texts (<c>errno</c> reasons, <c>.site</c> validation) follow
/// the calling app's language, and their English stays byte-identical without a context.
/// </summary>
public sealed class AppSdkLocalizationTests
{
    [Fact]
    public void Errno_reasons_follow_the_language_of_the_app()
    {
        KazoFileNotFoundException missing = new("/tmp/x");

        Assert.Equal("No such file or directory", ShellErrorText.Describe(missing));
        Assert.Equal("No such file or directory", ShellErrorText.Describe(missing, Context("en_US.UTF-8")));
        Assert.Equal("Aucun fichier ou dossier de ce type", ShellErrorText.Describe(missing, Context("fr_FR.UTF-8")));
        Assert.Equal("boom", ShellErrorText.Describe(new InvalidOperationException("boom"), Context("fr_FR.UTF-8")));
    }

    [Fact]
    public void Site_validation_speaks_the_language_of_the_app()
    {
        KazoWebSite site = new() { Name = "docs", Root = "/home/alice/www", Bindings = [] };
        KazoWebSite https = site with { Bindings = ["https://docs.hack/"] };
        KazoWebSite malformed = site with { Bindings = ["ftp://docs.hack/"] };

        Assert.Equal(
            "a site needs at least one binding, such as 'http://localhost:8080/'",
            Assert.Single(KazoWebSiteFile.Validate(site, KazoWebSiteScope.User)));
        Assert.Equal(
            "un site a besoin d'au moins une liaison, comme « http://localhost:8080/ »",
            Assert.Single(KazoWebSiteFile.Validate(site, KazoWebSiteScope.User, Context("fr_FR.UTF-8"))));
        Assert.Equal(
            "« https://docs.hack/ » : une liaison https a besoin d'un certificat de serveur, que hackwebd n'a pas encore",
            Assert.Single(KazoWebSiteFile.Validate(https, KazoWebSiteScope.User, Context("fr_FR.UTF-8"))));
        Assert.Equal(
            KazoWebSiteFile.Validate(https, KazoWebSiteScope.User),
            KazoWebSiteFile.Validate(https, KazoWebSiteScope.User, Context("en_US.UTF-8")));
        Assert.StartsWith(
            "« ftp://docs.hack/ » n'est pas une liaison valable : ",
            Assert.Single(KazoWebSiteFile.Validate(malformed, KazoWebSiteScope.User, Context("fr_FR.UTF-8"))),
            StringComparison.Ordinal);
    }

    [Fact]
    public void Every_sdk_text_exists_in_english_and_in_french()
    {
        ResourceManager resources = KazoLocalization.GetResourceManager(typeof(Strings));

        HashSet<string> english = Keys(resources.GetResourceSet(CultureInfo.InvariantCulture, createIfNotExists: true, tryParents: false));
        HashSet<string> french = Keys(resources.GetResourceSet(CultureInfo.GetCultureInfo("fr"), createIfNotExists: true, tryParents: false));

        Assert.NotEmpty(english);
        Assert.Empty(english.Except(french));
        Assert.Empty(french.Except(english));

        static HashSet<string> Keys(ResourceSet? set) =>
            set is null ? [] : [.. set.Cast<DictionaryEntry>().Select(entry => (string)entry.Key)];
    }

    private static MinimalAppExecutionContext Context(string lang)
    {
        FakeAppEnvironmentGateway environment = new("/home/alice", "alice");
        environment.SetVariable("LANG", lang);
        return new MinimalAppExecutionContext(new AppManifest
        {
            Id = "org.hackeros.test-sdk-localization",
            Name = "SDK Localization",
            Version = "1.0.0",
            PublisherId = "org.hackeros",
            Description = "SDK localization test.",
            Kind = AppKind.Window,
            EntryPoint = new AppEntryPointManifest("Kazo.Os.AppSdk.Tests", "Probe"),
            SdkCompatibility = new AppSdkCompatibilityManifest("1.0.0"),
            Presentation = new PresentationManifest("test", AppLaunchVisibility.Visible, []),
            Resources = AppResourceProfileManifest.None
        }, environment: environment);
    }
}
