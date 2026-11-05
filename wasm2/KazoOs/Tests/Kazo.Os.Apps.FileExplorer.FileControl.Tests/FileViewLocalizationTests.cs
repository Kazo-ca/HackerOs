using System.Collections;
using System.Globalization;
using System.Resources;
using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk.IO;
using Kazo.Os.AppSdk.Globalization;
using Kazo.Os.Tests.Support;

namespace Kazo.Os.Apps.FileExplorer.FileControl.Tests;

/// <summary>ADR 0104: the file view and its transfer summaries speak the language of the hosting app.</summary>
public sealed class FileViewLocalizationTests
{
    [Fact]
    public void Transfer_failures_are_summarised_in_the_language_of_the_app()
    {
        KazoFileTransferResult result = new(
            [],
            [],
            [new KazoFileTransferFailure("/home/alice/a.txt", "permission refusée"), new KazoFileTransferFailure("/home/alice/b.txt", "n'existe plus")]);

        string? french = result.DescribeFailures("copy", Context("fr_FR.UTF-8"));
        string? english = result.DescribeFailures("copy", Context("en_US.UTF-8"));
        string? custom = new KazoFileTransferResult([], [], [new KazoFileTransferFailure("/x", "busy")]).DescribeFailures("frobnicate", Context("fr_FR.UTF-8"));

        Assert.Equal("Impossible de copier 2 éléments : « a.txt » : permission refusée; « b.txt » : n'existe plus.", french);
        Assert.StartsWith("Could not copy 2 items", english, StringComparison.Ordinal);
        Assert.Equal("Impossible de frobnicate « x » : busy.", custom);
    }

    [Fact]
    public void A_french_app_names_columns_and_sizes_in_french()
    {
        var text = KazoLocalization.GetLocalizer<Strings>(Context("fr_FR.UTF-8"));

        Assert.Equal("Dossier", text["Type.Folder"].Value);
        Assert.Equal("1,5 Ko", text["Size.Kilobytes", 1.5].Value);
    }

    [Fact]
    public void Every_file_view_text_exists_in_english_and_in_french()
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
        return new MinimalAppExecutionContext(Manifest(), environment: environment);
    }

    private static AppManifest Manifest() => new()
    {
        Id = "org.kazoos.test-fileview-localization",
        Name = "File View Localization",
        Version = "1.0.0",
        PublisherId = "org.kazoos",
        Description = "File view localization test.",
        Kind = AppKind.Window,
        EntryPoint = new AppEntryPointManifest("Kazo.Os.Apps.FileExplorer.FileControl.Tests", "Probe"),
        SdkCompatibility = new AppSdkCompatibilityManifest("1.0.0"),
        Presentation = new PresentationManifest("test", AppLaunchVisibility.Visible, []),
        Resources = AppResourceProfileManifest.None
    };
}
