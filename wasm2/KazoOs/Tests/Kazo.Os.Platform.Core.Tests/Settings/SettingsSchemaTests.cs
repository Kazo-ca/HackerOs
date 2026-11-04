using Kazo.Os.Simulation.Abstractions.Settings;

namespace Kazo.Os.Platform.Core.Tests.Settings;

public sealed class SettingsSchemaTests
{
    [Fact]
    public void Validate_reports_unknown_keys()
    {
        SettingsSchema schema = new(1, [new SettingFieldDeclaration("theme", SettingValueType.String, "dark", SettingSensitivity.Public)]);

        IReadOnlyList<string> errors = schema.Validate(new Dictionary<string, string> { ["unknown"] = "x" });

        Assert.Contains("settings.unknown-key:unknown", errors);
    }

    [Fact]
    public void Validate_uses_default_when_value_missing_and_accepts_valid_types()
    {
        SettingsSchema schema = new(
            1,
            [
                new SettingFieldDeclaration("count", SettingValueType.Integer, "0", SettingSensitivity.Public),
                new SettingFieldDeclaration("enabled", SettingValueType.Boolean, "false", SettingSensitivity.Public),
                new SettingFieldDeclaration(
                    "mode",
                    SettingValueType.Enum,
                    "auto",
                    SettingSensitivity.Public,
                    AllowedValues: ["auto", "manual"])
            ]);

        IReadOnlyList<string> errors = schema.Validate(new Dictionary<string, string>());

        Assert.Empty(errors);
    }

    [Theory]
    [InlineData("count", "not-a-number")]
    [InlineData("enabled", "yes")]
    [InlineData("mode", "invalid-mode")]
    public void Validate_rejects_malformed_typed_values(string key, string value)
    {
        SettingsSchema schema = new(
            1,
            [
                new SettingFieldDeclaration("count", SettingValueType.Integer, "0", SettingSensitivity.Public),
                new SettingFieldDeclaration("enabled", SettingValueType.Boolean, "false", SettingSensitivity.Public),
                new SettingFieldDeclaration(
                    "mode",
                    SettingValueType.Enum,
                    "auto",
                    SettingSensitivity.Public,
                    AllowedValues: ["auto", "manual"])
            ]);

        IReadOnlyList<string> errors = schema.Validate(new Dictionary<string, string> { [key] = value });

        Assert.Contains(errors, error => error.StartsWith($"settings.invalid", StringComparison.Ordinal) && error.EndsWith(key, StringComparison.Ordinal));
    }

    [Fact]
    public void Grouped_fields_use_qualified_key_for_lookup_and_duplicates()
    {
        SettingFieldDeclaration field = new("width", SettingValueType.Integer, "800", SettingSensitivity.Public, Group: "Window");

        Assert.Equal("Window.width", field.QualifiedKey);
        Assert.Throws<ArgumentException>(() => new SettingsSchema(
            1,
            [field, new SettingFieldDeclaration("width", SettingValueType.Integer, "800", SettingSensitivity.Public, Group: "Window")]));
    }

    [Fact]
    public void Enum_field_without_allowed_values_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => new SettingsSchema(
            1,
            [new SettingFieldDeclaration("mode", SettingValueType.Enum, "auto", SettingSensitivity.Public)]));
    }
}
