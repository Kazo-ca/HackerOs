using Kazo.Os.Platform.Core.Shell;
using Kazo.Os.Simulation.Abstractions.Sessions;

namespace Kazo.Os.Platform.Core.Tests.Shell;

public sealed class StartMenuSettingsValidatorTests
{
    private readonly StartMenuSettingsValidator _validator = new();
    private readonly LocalUserId _userId = LocalUserId.FromGuid(Guid.Parse("11111111-2222-3333-4444-555555555555"));

    [Fact]
    public void Validate_clean_profile_is_valid()
    {
        Assert.Empty(_validator.Validate(StartMenuSettingsDocuments.EmptyDocumentContent));
    }

    [Fact]
    public void Validate_duplicate_pin_is_rejected()
    {
        string content =
            """{"schemaVersion":1,"profiles":{"USER_ID":{"pinnedAppIds":["org.hackeros.browser","org.hackeros.browser"]}}}"""
                .Replace("USER_ID", _userId.ToString(), StringComparison.Ordinal);

        Assert.Contains("start-menu.app-id-duplicate", _validator.Validate(content));
    }

    [Fact]
    public void Validate_syntactically_valid_unknown_app_id_is_preserved_by_schema()
    {
        string content =
            """{"schemaVersion":1,"profiles":{"USER_ID":{"pinnedAppIds":["org.example.unmounted"]}}}"""
                .Replace("USER_ID", _userId.ToString(), StringComparison.Ordinal);

        Assert.Empty(_validator.Validate(content));
    }
}
