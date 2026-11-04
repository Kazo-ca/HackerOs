using System.Text.Json;
using Json.Schema;

namespace Kazo.Os.App.Abstractions.Tests.Schema;

/// <summary>
/// Verifies the published manifest JSON Schema (Draft 2020-12) accepts every valid fixture
/// (one per <see cref="AppKind"/>) and rejects every invalid fixture with a schema-level violation.
/// These tests exercise the schema document itself, independent of <see cref="AppManifestValidator"/>,
/// per ADR 0010's validation order: JSON structure/schema comes before semantic validation.
/// </summary>
public sealed class ManifestSchemaConformanceTests
{
    private static readonly Json.Schema.JsonSchema Schema = Json.Schema.JsonSchema.FromText(
        ManifestSchemaResource.LoadCurrentSchemaJson());

    public static TheoryData<string> ValidFixtureNames => new()
    {
        "window.valid.json",
        "terminal.valid.json",
        "service.valid.json",
        "window-multi-platform.valid.json",
        "web.valid.json",
        "web-blazor.valid.json",
        "window-sized.valid.json"
    };

    public static TheoryData<string> InvalidFixtureNames => new()
    {
        "invalid-schema-version.json",
        "invalid-app-id.json",
        "invalid-missing-resources.json",
        "invalid-unknown-property.json",
        "invalid-terminal-missing-terminal-block.json",
        "invalid-filehandlers-on-service.json",
        "invalid-asset-hash.json",
        "invalid-resource-weight-out-of-range.json",
        "invalid-settings-enum-missing-allowed-values.json",
        "invalid-platform-ambiguous.json",
        "invalid-web-missing-web-block.json",
        "invalid-web-on-service.json",
        "invalid-window-on-service.json"
    };

    [Theory]
    [MemberData(nameof(ValidFixtureNames))]
    public void Schema_accepts_every_valid_fixture(string fixtureName)
    {
        JsonElement instance = LoadFixture(fixtureName);

        EvaluationResults result = Schema.Evaluate(instance, new EvaluationOptions
        {
            OutputFormat = OutputFormat.List
        });

        Assert.True(result.IsValid, DescribeFailure(fixtureName, result));
    }

    [Theory]
    [MemberData(nameof(InvalidFixtureNames))]
    public void Schema_rejects_every_invalid_fixture(string fixtureName)
    {
        JsonElement instance = LoadFixture(fixtureName);

        EvaluationResults result = Schema.Evaluate(instance, new EvaluationOptions
        {
            OutputFormat = OutputFormat.List
        });

        Assert.False(result.IsValid, $"Expected fixture '{fixtureName}' to be rejected by the schema.");
    }

    private static JsonElement LoadFixture(string fixtureName)
    {
        string path = Path.Combine(FixturesDirectory.Value, fixtureName);
        string json = File.ReadAllText(path);
        // JsonSchema.Net 8+ evaluates JsonElement instances; Clone() detaches it from the disposed document.
        using JsonDocument document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static string DescribeFailure(string fixtureName, EvaluationResults result)
    {
        IEnumerable<string> errors = (result.Details ?? [])
            .Where(detail => detail.Errors is { Count: > 0 })
            .SelectMany(detail => detail.Errors!.Select(error => $"{detail.InstanceLocation}: {error.Value}"));
        return $"Fixture '{fixtureName}' failed schema validation: {string.Join("; ", errors)}";
    }

    private static readonly Lazy<string> FixturesDirectory = new(() =>
    {
        string? directory = AppContext.BaseDirectory;
        while (directory is not null)
        {
            string candidate = Path.Combine(directory, "Kazo.Os.sln");
            if (File.Exists(candidate))
            {
                return Path.Combine(directory, "Shared", "Kazo.Os.App.Abstractions", "Schema", "Fixtures");
            }

            directory = Path.GetDirectoryName(directory);
        }

        throw new InvalidOperationException("Could not locate Kazo.Os.sln by walking up from the test output directory.");
    });
}
