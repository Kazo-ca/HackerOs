using System.Collections;
using System.Globalization;
using System.Resources;
using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk;
using Kazo.Os.Tests.Support;
using Xunit;

namespace Kazo.Os.Apps.CodeEditor.Tests;

/// <summary>
/// The Code Editor speaks the language of its process (ADR 0104): its services read the ambient
/// app's <c>LANG</c>, English outside any app.
/// </summary>
public sealed class CodeEditorLocalizationTests
{
    [Fact]
    public void Services_InAFrenchProcess_AnswerInFrench()
    {
        using IDisposable scope = HackExecutionContext.Enter(Process("fr_FR.UTF-8"));

        Assert.Equal("Sans titre-3", CodeEditorDocument.CreateNew(3).Title);
        Assert.Equal("Un nom de fichier ou de dossier est requis.", CodeEditorWorkspace.ValidateName(" "));
        Assert.Equal("Le nom « .. » n'est pas valide comme nom de fichier ou de dossier.", CodeEditorWorkspace.ValidateName(".."));
    }

    [Fact]
    public void Services_InAnEnglishProcess_OrOutsideAnyApp_AnswerInEnglish()
    {
        Assert.Equal("Untitled-1", CodeEditorDocument.CreateNew(1).Title);

        using IDisposable scope = HackExecutionContext.Enter(Process("en_US.UTF-8"));
        Assert.Equal("A file or folder name must be provided.", CodeEditorWorkspace.ValidateName(""));
    }

    [Fact]
    public void Resources_EnglishAndFrench_HaveTheSameKeys()
    {
        ResourceManager resources = new("Kazo.Os.Apps.CodeEditor.Resources.Strings", typeof(CodeEditorApp).Assembly);

        Assert.Equal(Keys(resources, CultureInfo.InvariantCulture), Keys(resources, CultureInfo.GetCultureInfo("fr")));
    }

    private static MinimalAppExecutionContext Process(string lang)
    {
        FakeAppEnvironmentGateway environment = new("/home/user");
        environment.SetVariable("LANG", lang);
        return new MinimalAppExecutionContext(AppDefinitionMapper.ToManifest(new CodeEditorApp()), environment: environment);
    }

    private static SortedSet<string> Keys(ResourceManager resources, CultureInfo culture)
    {
        ResourceSet set = resources.GetResourceSet(culture, createIfNotExists: true, tryParents: false)
            ?? throw new InvalidOperationException($"No {culture.Name} resources.");
        return new SortedSet<string>(set.Cast<DictionaryEntry>().Select(entry => (string)entry.Key), StringComparer.Ordinal);
    }
}
