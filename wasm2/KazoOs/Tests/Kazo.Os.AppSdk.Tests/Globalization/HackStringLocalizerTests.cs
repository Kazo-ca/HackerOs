using System.Globalization;
using Kazo.Os.AppSdk.Globalization;
using Kazo.Os.AppSdk.Tests.IO;
using Kazo.Os.Tests.Support;
using Microsoft.Extensions.Localization;

namespace Kazo.Os.AppSdk.Tests.Globalization;

/// <summary>Localizers bound to a process language instead of the thread culture (ADR 0104, section 3).</summary>
public sealed class HackStringLocalizerTests
{
    private static MinimalAppExecutionContext Process(string? lang)
    {
        FakeAppEnvironmentGateway environment = new(HackTestHost.Home);
        environment.SetVariable("LANG", lang);
        return new MinimalAppExecutionContext(HackTestHost.Manifest, homeDirectory: HackTestHost.Home, environment: environment);
    }

    [Fact]
    public void The_resource_name_follows_the_Microsoft_Extensions_Localization_convention() =>
        Assert.Equal("Kazo.Os.AppSdk.Tests.Resources.Globalization.TestStrings", HackLocalization.GetResourceBaseName(typeof(TestStrings)));

    [Fact]
    public void Each_process_reads_its_own_language_whatever_the_thread_culture()
    {
        CultureInfo previous = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en-US");
        try
        {
            Assert.Equal("Bonjour", Process("fr_CA.UTF-8").GetLocalizer<TestStrings>()["Hello"].Value);
            Assert.Equal("Hello", Process("en_US.UTF-8").GetLocalizer<TestStrings>()["Hello"].Value);
            Assert.Equal("Hello", ((IAppExecutionContext?)null).GetLocalizer<TestStrings>()["Hello"].Value);
        }
        finally
        {
            CultureInfo.CurrentUICulture = previous;
        }
    }

    [Fact]
    public void The_language_is_read_again_at_every_lookup()
    {
        MinimalAppExecutionContext process = Process("en_US.UTF-8");
        IStringLocalizer<TestStrings> text = process.GetLocalizer<TestStrings>();
        Assert.Equal("Hello", text["Hello"].Value);

        process.Environment.SetVariable("LANG", "fr_FR.UTF-8");

        Assert.Equal("Bonjour", text["Hello"].Value);
    }

    [Fact]
    public void Arguments_are_formatted_in_the_culture_of_the_text()
    {
        Assert.Equal("1,5 fichiers", Process("fr_FR.UTF-8").GetLocalizer<TestStrings>()["Count", 1.5].Value.Replace(' ', ' ').Replace(' ', ' '));
        Assert.Equal("1.5 files", Process("en_US.UTF-8").GetLocalizer<TestStrings>()["Count", 1.5].Value);
    }

    [Fact]
    public void A_missing_French_key_falls_back_to_English_and_an_unknown_key_returns_itself()
    {
        IStringLocalizer<TestStrings> text = Process("fr_FR.UTF-8").GetLocalizer<TestStrings>();

        LocalizedString english = text["EnglishOnly"];
        LocalizedString unknown = text["Nope"];

        Assert.Equal("Only in English", english.Value);
        Assert.False(english.ResourceNotFound);
        Assert.Equal("Nope", unknown.Value);
        Assert.True(unknown.ResourceNotFound);
    }

    [Fact]
    public void GetAllStrings_merges_the_culture_and_its_parents()
    {
        Dictionary<string, string> all = Process("fr_FR.UTF-8").GetLocalizer<TestStrings>()
            .GetAllStrings(includeParentCultures: true)
            .ToDictionary(s => s.Name, s => s.Value);

        Assert.Equal("Bonjour", all["Hello"]);
        Assert.Equal("Only in English", all["EnglishOnly"]);
    }

    [Fact]
    public void The_English_localizer_never_follows_a_French_process() =>
        Assert.Equal("Hello", new EnglishStringLocalizer<TestStrings>()["Hello"].Value);

    [Fact]
    public void The_execution_context_exposes_the_language_and_the_format_culture()
    {
        MinimalAppExecutionContext process = Process("fr_CA.UTF-8");
        process.Environment.SetVariable("LC_TIME", "en_GB.UTF-8");
        IAppExecutionContext context = process;

        Assert.Equal("fr", context.Culture.Name);
        Assert.Equal("en-GB", context.FormatCulture.Name);
        using (HackExecutionContext.Enter(process, HackTestHost.Home))
        {
            Assert.Equal("fr", HackEnvironment.Culture.Name);
            Assert.Equal("en-GB", HackEnvironment.FormatCulture.Name);
            Assert.Equal("Bonjour", HackLocalization.GetLocalizer<TestStrings>()["Hello"].Value);
        }
    }
}
