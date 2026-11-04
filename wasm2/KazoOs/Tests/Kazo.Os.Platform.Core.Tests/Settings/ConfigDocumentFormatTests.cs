using Kazo.Os.Simulation.Abstractions.Settings;

namespace Kazo.Os.Platform.Core.Tests.Settings;

public sealed class ConfigDocumentFormatTests
{
    [Fact]
    public void Parse_ignores_comments_and_blank_lines()
    {
        ConfigDocumentParseResult result = ConfigDocumentFormat.Parse("""
            # a comment
            schemaVersion=1

            # another comment
            theme=dark
            """);

        Assert.True(result.Success);
        Assert.Equal("1", result.Values["schemaVersion"]);
        Assert.Equal("dark", result.Values["theme"]);
    }

    [Fact]
    public void Parse_supports_grouped_sections()
    {
        ConfigDocumentParseResult result = ConfigDocumentFormat.Parse("""
            schemaVersion=1

            [Window]
            width=800
            height=600
            """);

        Assert.True(result.Success);
        Assert.Equal("800", result.Values["Window.width"]);
        Assert.Equal("600", result.Values["Window.height"]);
    }

    [Fact]
    public void Parse_unescapes_supported_value_escapes()
    {
        ConfigDocumentParseResult result = ConfigDocumentFormat.Parse(
            """label=a\#b\=c\\d\ne\rf\tg""");

        Assert.True(result.Success);
        Assert.Equal("a#b=c\\d\ne\rf\tg", result.Values["label"]);
    }

    [Fact]
    public void Parse_reports_duplicate_key_and_duplicate_section()
    {
        ConfigDocumentParseResult duplicateKey = ConfigDocumentFormat.Parse("""
            key=one
            key=two
            """);
        ConfigDocumentParseResult duplicateSection = ConfigDocumentFormat.Parse("""
            [Group]
            a=1
            [Group]
            b=2
            """);

        Assert.False(duplicateKey.Success);
        Assert.Contains(duplicateKey.Errors, error => error.Code == "config.duplicate-key");
        Assert.False(duplicateSection.Success);
        Assert.Contains(duplicateSection.Errors, error => error.Code == "config.duplicate-section");
    }

    [Fact]
    public void Parse_reports_malformed_section_and_key_value_lines()
    {
        ConfigDocumentParseResult malformedSection = ConfigDocumentFormat.Parse("[unterminated");
        ConfigDocumentParseResult malformedPair = ConfigDocumentFormat.Parse("no-separator-here");

        Assert.Contains(malformedSection.Errors, error => error.Code == "config.malformed-section");
        Assert.Contains(malformedPair.Errors, error => error.Code == "config.malformed-key-value");
    }

    [Fact]
    public void Serialize_produces_deterministic_root_then_grouped_order()
    {
        SettingsSchema schema = new(
            1,
            [
                new SettingFieldDeclaration("height", SettingValueType.Integer, "600", SettingSensitivity.Public, Group: "Window"),
                new SettingFieldDeclaration("width", SettingValueType.Integer, "800", SettingSensitivity.Public, Group: "Window"),
                new SettingFieldDeclaration("theme", SettingValueType.String, "dark", SettingSensitivity.Public)
            ]);

        string serialized = ConfigDocumentFormat.Serialize(schema, new Dictionary<string, string>());
        ConfigDocumentParseResult reparsed = ConfigDocumentFormat.Parse(serialized);

        Assert.True(reparsed.Success);
        Assert.Equal("dark", reparsed.Values["theme"]);
        Assert.Equal("800", reparsed.Values["Window.width"]);
        Assert.Equal("600", reparsed.Values["Window.height"]);
        Assert.True(serialized.IndexOf("theme=", StringComparison.Ordinal)
            < serialized.IndexOf("[Window]", StringComparison.Ordinal));
    }
}
