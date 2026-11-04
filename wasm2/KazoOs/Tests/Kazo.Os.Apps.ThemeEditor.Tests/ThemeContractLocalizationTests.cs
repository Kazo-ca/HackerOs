using System.Globalization;
using Kazo.Os.AppSdk.Globalization;
using Kazo.Os.Theming.Abstractions;
using Xunit;

namespace Kazo.Os.Apps.ThemeEditor.Tests;

/// <summary>
/// The texts of <c>Kazo.Os.Theming.Abstractions</c> shown by the Theme Editor (token labels and
/// descriptions, validation reasons, theme file diagnostics) read in the caller's language (ADR 0104),
/// while their English properties, token names and file content never change.
/// </summary>
public sealed class ThemeContractLocalizationTests
{
    private static readonly CultureInfo French = CultureInfo.GetCultureInfo("fr-FR");
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");

    // French labels that are spelled like the English ones.
    private static readonly HashSet<string> SameInFrench = new(StringComparer.Ordinal) { "Surface", "Information" };

    [Fact]
    public void EveryToken_HasAnEnglishResourceMatchingTheCatalog()
    {
        foreach (ThemeTokenDefinition token in ThemeTokenCatalog.All)
        {
            Assert.Equal(token.Label, token.GetLabel(English));
            Assert.Equal(token.Description, token.GetDescription(English));
            Assert.Equal(token.Label, token.GetLabel(null));
        }
    }

    [Fact]
    public void EveryToken_IsTranslatedToFrench()
    {
        foreach (ThemeTokenDefinition token in ThemeTokenCatalog.All)
        {
            string label = token.GetLabel(French);
            if (!SameInFrench.Contains(label))
            {
                Assert.NotEqual(token.Label, label);
            }

            Assert.NotEqual(token.Description, token.GetDescription(French));
        }

        Assert.True(ThemeTokenCatalog.TryGet("--hos-bg-base", out ThemeTokenDefinition? baseBackground));
        Assert.Equal("Arrière-plan de base", baseBackground.GetLabel(French));
        Assert.Equal("Base background", baseBackground.Label);
    }

    [Fact]
    public void ValidationReason_FollowsTheCulture()
    {
        Assert.False(ThemeTokenValidator.TryValidate(ThemeTokenType.Number, "bold", out _, out string? english));
        Assert.False(ThemeTokenValidator.TryValidate(ThemeTokenType.Number, "bold", French, out _, out string? french));
        Assert.False(ThemeTokenValidator.TryValidate(ThemeTokenType.Length, " ", French, out _, out string? empty));

        Assert.Equal("Expected a number such as 600 or 0.8.", english);
        Assert.Equal("Nombre attendu, par exemple 600 ou 0.8.", french);
        Assert.Equal("La valeur est vide.", empty);
        Assert.True(ThemeTokenValidator.TryValidate(ThemeTokenType.Number, "600", French, out string normalized, out string? none));
        Assert.Equal("600", normalized);
        Assert.Null(none);
    }

    [Fact]
    public void FileDiagnostics_ReadInTheReadersLanguage_AndLogInEnglish()
    {
        ThemeFileParseResult result = ThemeFile.Parse(
            "[theme]\nid = neon\nname = Neon\nbase = kazoos\n\n[tokens]\n--hos-accent = url(evil)\n",
            path: "/home/alice/.config/kazoos/themes/neon.theme");

        ThemeFileDiagnostic diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("Expected a color such as #39ff14, rgb(57 255 20) or a color name. Token '--hos-accent' ignored.", diagnostic.Message);
        Assert.Equal(diagnostic.Message, diagnostic.GetMessage(English));
        Assert.Equal(
            "Couleur attendue, par exemple #39ff14, rgb(57 255 20) ou un nom de couleur. Jeton « --hos-accent » ignoré.",
            diagnostic.GetMessage(French));
        Assert.Equal(
            "/home/alice/.config/kazoos/themes/neon.theme:7: avertissement: Couleur attendue, par exemple #39ff14, rgb(57 255 20) ou un nom de couleur. Jeton « --hos-accent » ignoré.",
            diagnostic.ToString(French));
        Assert.StartsWith("/home/alice/.config/kazoos/themes/neon.theme:7: warning: Expected a color", diagnostic.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void RefusedFile_AndLibraryConflicts_AreTranslated()
    {
        ThemeFileDiagnostic missingId = Assert.Single(ThemeFile.Parse("[theme]\nname = X\nbase = kazoos\n").Diagnostics);
        Assert.Equal("Missing 'id' in [theme].", missingId.Message);
        Assert.Equal("« id » manquant dans [theme].", missingId.GetMessage(French));
        Assert.Equal("thème: erreur: « id » manquant dans [theme].", missingId.ToString(French));

        UserThemeLibraryResult library = UserThemeLibrary.Build(
        [
            new ThemeFileSource("/etc/kazoos/themes/ubuntu.theme", ThemeOrigin.System, "[theme]\nid = ubuntu\nname = U\nbase = kazoos\n")
        ]);
        ThemeFileDiagnostic builtIn = Assert.Single(library.Diagnostics);
        Assert.Equal("The id 'ubuntu' is a built-in theme; this file is ignored.", builtIn.Message);
        Assert.Equal("L'identifiant « ubuntu » est celui d'un thème intégré ; ce fichier est ignoré.", builtIn.GetMessage(French));
    }

    [Fact]
    public void DiagnosticWithAPlainMessage_KeepsItInEveryLanguage()
    {
        ThemeFileDiagnostic custom = new(3, "Custom reason.", IsError: false, "a.theme");

        Assert.Equal("Custom reason.", custom.GetMessage(French));
        Assert.Equal("a.theme:3: avertissement: Custom reason.", custom.ToString(French));
    }

    [Fact]
    public void Document_ExplainsInvalidValues_InTheWindowLanguage()
    {
        ThemeEditorDocument document = new(new LocaleTestContext("fr_FR.UTF-8").GetLocalizer<Strings>());

        document.SetValue("--hos-radius-sm", "big");

        Assert.Equal("Longueur attendue, par exemple 0.5rem, 12px ou 50%.", document.GetError("--hos-radius-sm"));
    }
}
