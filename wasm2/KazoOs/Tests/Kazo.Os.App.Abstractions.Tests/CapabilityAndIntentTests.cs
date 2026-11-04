using Kazo.Os.App.Abstractions;

namespace Kazo.Os.App.Abstractions.Tests;

public sealed class CapabilityAndIntentTests
{
    [Fact]
    public void Capability_grants_use_exact_case_sensitive_matching()
    {
        string[] grants = [AppCapabilities.FileSystemSystemWrite];

        Assert.True(AppCapabilityPolicy.IsGranted(grants, AppCapabilities.FileSystemSystemWrite));
        Assert.False(AppCapabilityPolicy.IsGranted(grants, "FILESYSTEM.SYSTEM.WRITE"));
        Assert.False(AppCapabilityPolicy.IsGranted(grants, "filesystem.system.*"));
    }

    [Fact]
    public void Manifest_validation_rejects_unknown_capabilities()
    {
        AppManifest manifest = CreateValidManifest() with
        {
            Capabilities = ["filesystem.everything"]
        };

        ManifestValidationResult result = AppManifestValidator.Validate(manifest);

        Assert.Contains(result.Errors, error => error.Code == "manifest.capability.unknown");
    }

    [Fact]
    public void Virtual_path_normalizes_redundant_and_relative_segments()
    {
        VirtualPath path = VirtualPath.Parse("/home//user/Documents/../notes.txt");

        Assert.Equal("/home/user/notes.txt", path.Value);
    }

    [Theory]
    [InlineData("relative/file.txt")]
    [InlineData("/../../etc/passwd")]
    [InlineData("/home\\user")]
    public void Virtual_path_rejects_unsafe_or_non_virtual_paths(string value)
    {
        Assert.Throws<FormatException>(() => VirtualPath.Parse(value));
    }

    [Fact]
    public void Open_file_intent_has_a_stable_versioned_identifier()
    {
        OpenFileIntent intent = new(
            VirtualPath.Parse("/home/user/notes.txt"),
            FileIntentAction.Edit,
            "text/plain");

        Assert.Equal("org.hackeros.intent.open-file.v1", intent.IntentId);
        Assert.Equal("/home/user/notes.txt", intent.Path.Value);
    }

    private static AppManifest CreateValidManifest() => new()
    {
        Id = "org.hackeros.test-app",
        Name = "Test App",
        Version = "1.0.0",
        PublisherId = "org.hackeros",
        Description = "Valid manifest used by capability tests.",
        Kind = AppKind.Window,
        EntryPoint = new AppEntryPointManifest("Kazo.Os.TestApp", "Kazo.Os.TestApp.TestApp"),
        SdkCompatibility = new AppSdkCompatibilityManifest(HackerOsSdk.Version),
        Presentation = new PresentationManifest("test", AppLaunchVisibility.Visible, []),
        Resources = AppResourceProfileManifest.None
    };
}