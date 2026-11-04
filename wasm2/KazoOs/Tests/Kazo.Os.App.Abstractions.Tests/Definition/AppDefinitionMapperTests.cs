using Kazo.Os.App.Abstractions;

namespace Kazo.Os.App.Abstractions.Tests.Definition;

/// <summary>Contract tests for <see cref="AppDefinitionMapper"/> (ADR 0047 §7).</summary>
public sealed class AppDefinitionMapperTests
{
    [Fact]
    public void ToManifest_MinimalDefinition_AppliesEveryDocumentedDefault()
    {
        AppManifest manifest = AppDefinitionMapper.ToManifest(new MinimalWindowApp(), "1.0.0");

        Assert.Equal(1, manifest.SchemaVersion);
        Assert.Equal("org.hackeros.tests.minimal", manifest.Id);
        Assert.Equal("Minimal", manifest.Name);
        Assert.Equal(AppKind.Window, manifest.Kind);
        Assert.Equal("1.0.0", manifest.Version);
        Assert.Equal("Minimal", manifest.Description);
        Assert.Equal("pub.local", manifest.PublisherId);
        Assert.Equal("utilities", manifest.Presentation.Category);
        Assert.Equal(AppLaunchVisibility.Visible, manifest.Presentation.LaunchVisibility);
        Assert.Empty(manifest.Presentation.IconAssetPaths);
        Assert.Empty(manifest.Capabilities);
        Assert.Empty(manifest.FileHandlers);
        Assert.Null(manifest.Terminal);
        Assert.Null(manifest.Components);
        Assert.Null(manifest.Platform);
        Assert.False(manifest.SingleInstancePerUser);
        Assert.False(manifest.AutoStart);
        Assert.False(manifest.SupportsBack);
        Assert.Null(manifest.Window);
        Assert.Equal(AppExecutionHost.MainThread, manifest.Execution);
        Assert.Equal(AppResourceProfileManifest.None, manifest.Resources);
        Assert.Null(manifest.Settings);
        Assert.Empty(manifest.Dependencies);
        Assert.Empty(manifest.Intents);
        Assert.Empty(manifest.DeclaredTopicPermissions);
        Assert.Empty(manifest.Localizations);
        Assert.Empty(manifest.Assets);
        Assert.Null(manifest.OsCompatibility);
        Assert.Null(manifest.Update);
        Assert.Equal(new AppSdkCompatibilityManifest("1.0.0"), manifest.SdkCompatibility);
        Assert.True(AppManifestValidator.Validate(manifest).IsValid);
    }

    [Theory]
    [InlineData(typeof(UnmarkedServiceApp), AppExecutionHost.Dedicated)]
    [InlineData(typeof(DefaultCommandApp), AppExecutionHost.Pool)]
    [InlineData(typeof(MainThreadCommandApp), AppExecutionHost.MainThread)]
    [InlineData(typeof(DedicatedCommandApp), AppExecutionHost.Dedicated)]
    public void ToManifest_Execution_ResolvesTheAttributeOrTheKindDefault(Type definitionType, AppExecutionHost expected)
    {
        AppManifest manifest = AppDefinitionMapper.ToManifest((AppDefinition)Activator.CreateInstance(definitionType)!, "1.0.0");

        Assert.Equal(expected, manifest.Execution);
        Assert.True(AppManifestValidator.Validate(manifest).IsValid);
    }

    [Fact]
    public void ToManifest_Components_MapToTheirAssemblyAndType()
    {
        AppManifest manifest = AppDefinitionMapper.ToManifest(new ComponentApp(), "1.0.0");

        AppComponentManifest component = Assert.Single(manifest.Components!);
        Assert.Equal(new AppComponentManifest("hackeros.tests-view", "Kazo.Os.App.Abstractions.Tests.dll", typeof(FakeEmbed).FullName!), component);
        Assert.True(AppManifestValidator.Validate(manifest).IsValid);
    }

    [Fact]
    public void ToManifest_BlazorRoot_MapsToWebBlazor()
    {
        AppManifest manifest = AppDefinitionMapper.ToManifest(new BlazorSiteApp(), "1.0.0");
        AppManifest mvcOnly = AppDefinitionMapper.ToManifest(new MvcSiteApp(), "1.0.0");

        Assert.Equal(new WebBlazorManifest("Kazo.Os.App.Abstractions.Tests.dll", typeof(FakeEmbed).FullName!), manifest.Web!.Blazor);
        Assert.Equal(["http://blazor.hack/"], manifest.Web.Urls);
        Assert.True(AppManifestValidator.Validate(manifest).IsValid);
        Assert.Null(mvcOnly.Web!.Blazor);
        Assert.DoesNotContain("\"blazor\":", AppManifestJsonSerializer.SerializeCanonical(mvcOnly), StringComparison.Ordinal);
    }

    [Fact]
    public void ToManifest_DesktopOnly_ProducesEntryPointForm()
    {
        AppManifest manifest = AppDefinitionMapper.ToManifest(new MinimalWindowApp());

        Assert.Null(manifest.Platform);
        Assert.Equal(
            new AppEntryPointManifest("Kazo.Os.App.Abstractions.Tests.dll", typeof(FakeWindow).FullName!),
            manifest.EntryPoint);
    }

    [Fact]
    public void ToManifest_SharedMobileEntryPoint_ProducesOneEntryCoveringBothPlatforms()
    {
        AppManifest manifest = AppDefinitionMapper.ToManifest(new SharedMobileApp(), "1.0.0");

        Assert.Null(manifest.EntryPoint);
        Assert.NotNull(manifest.Platform);
        Assert.Equal(["desktop", "mobile"], manifest.Platform!.Supported);
        AppPlatformEntryPointManifest entry = Assert.Single(manifest.Platform.EntryPoints);
        Assert.Equal(["desktop", "mobile"], entry.Platforms);
        Assert.Equal(typeof(FakeWindow).FullName, entry.Type);
        Assert.True(AppManifestValidator.Validate(manifest).IsValid);
    }

    [Fact]
    public void ToManifest_DistinctMobileEntryPoint_ProducesOneEntryPerPlatform()
    {
        AppManifest manifest = AppDefinitionMapper.ToManifest(new DistinctMobileApp(), "1.0.0");

        Assert.Null(manifest.EntryPoint);
        Assert.Collection(
            manifest.Platform!.EntryPoints,
            desktop =>
            {
                Assert.Equal(["desktop"], desktop.Platforms);
                Assert.Equal(typeof(FakeWindow).FullName, desktop.Type);
            },
            mobile =>
            {
                Assert.Equal(["mobile"], mobile.Platforms);
                Assert.Equal(typeof(FakeMobileWindow).FullName, mobile.Type);
            });
        Assert.True(AppManifestValidator.Validate(manifest).IsValid);
    }

    [Fact]
    public void ToManifest_ShortForms_MapToManifestRecords()
    {
        AppManifest manifest = AppDefinitionMapper.ToManifest(new FullWindowApp(), "1.0.0");

        FileHandlerManifest handler = Assert.Single(manifest.FileHandlers);
        Assert.Equal("text/plain", handler.MediaType);
        Assert.Equal([".txt", ".md"], handler.Extensions);
        Assert.Equal(["open", "edit"], handler.Actions);
        Assert.Equal(5, handler.Priority);
        Assert.Equal("1.2.3", manifest.Version);
        Assert.Equal("pub.hackeros", manifest.PublisherId);
        Assert.Equal("Edits text.", manifest.Description);
        Assert.Equal("productivity", manifest.Presentation.Category);
        Assert.Equal(AppLaunchVisibility.Hidden, manifest.Presentation.LaunchVisibility);
        Assert.Equal([AppCapabilities.FileSystemUserHomeRead], manifest.Capabilities);
        Assert.True(manifest.SingleInstancePerUser);
        Assert.True(AppManifestValidator.Validate(manifest).IsValid);
    }

    [Fact]
    public void ToManifest_TerminalWithoutAliases_WritesEmptyAliasList()
    {
        AppManifest manifest = AppDefinitionMapper.ToManifest(new EchoLikeCommand(), "1.0.0");

        Assert.Equal(new TerminalCommandManifest("echo", [], "echo [string ...]").Name, manifest.Terminal!.Name);
        Assert.Empty(manifest.Terminal.Aliases);
        Assert.Equal("echo [string ...]", manifest.Terminal.Usage);
        Assert.True(AppManifestValidator.Validate(manifest).IsValid);
    }

    [Fact]
    public void ToManifest_IsDeterministic_ForPureGetters()
    {
        string first = AppManifestJsonSerializer.SerializeCanonical(AppDefinitionMapper.ToManifest(new FullWindowApp(), "1.0.0"));
        string second = AppManifestJsonSerializer.SerializeCanonical(AppDefinitionMapper.ToManifest(new FullWindowApp(), "1.0.0"));

        Assert.Equal(first, second);
    }

    [Fact]
    public void ToManifest_WithoutExplicitVersion_UsesTheProcessSdkVersion() =>
        Assert.Equal(new AppSdkCompatibilityManifest(HackerOsSdk.Version), AppDefinitionMapper.ToManifest(new MinimalWindowApp()).SdkCompatibility);

    [Fact]
    public void ToManifest_KeepsARangeTheDefinitionDeclares() =>
        Assert.Equal(
            new AppSdkCompatibilityManifest("0.9.0-preview.1", "0.9.0"),
            AppDefinitionMapper.ToManifest(new RangedApp(), "0.9.0-preview.4").SdkCompatibility);

    [Fact]
    public void AppManifestAttribute_IsSingleUseAndNotInherited()
    {
        AttributeUsageAttribute usage = Assert.Single(
            typeof(AppManifestAttribute).GetCustomAttributes(typeof(AttributeUsageAttribute), false).Cast<AttributeUsageAttribute>());
        Assert.Equal(AttributeTargets.Class, usage.ValidOn);
        Assert.False(usage.AllowMultiple);
        Assert.False(usage.Inherited);
    }

    [Fact]
    public void ToManifest_copies_the_declared_window_sizes()
    {
        AppManifest manifest = AppDefinitionMapper.ToManifest(new SizedWindowApp(), "1.0.0");

        Assert.Equal(new WindowOptions { DefaultWidth = 360, DefaultHeight = 520, MinWidth = 360, MinHeight = 520, Resizable = false }, manifest.Window);
        Assert.True(AppManifestValidator.Validate(manifest).IsValid);
    }

    private sealed class SizedWindowApp : AppDefinition
    {
        public override string Id => "org.hackeros.tests.sized";
        public override string Name => "Sized";
        public override AppKind Kind => AppKind.Window;
        public override Type EntryPoint => typeof(FakeWindow);
        public override WindowOptions Window => new() { DefaultWidth = 360, DefaultHeight = 520, MinWidth = 360, MinHeight = 520, Resizable = false };
    }

    private sealed class FakeWindow;

    private sealed class FakeMobileWindow;

    private sealed class FakeCommand;

    private sealed class FakeEmbed;

    private sealed class ComponentApp : AppDefinition
    {
        public override string Id => "org.hackeros.tests.component";
        public override string Name => "Component";
        public override AppKind Kind => AppKind.Window;
        public override Type EntryPoint => typeof(FakeWindow);
        public override IReadOnlyList<AppComponent> Components => [new("hackeros.tests-view", typeof(FakeEmbed))];
    }

    private class MvcSiteApp : AppDefinition
    {
        public override string Id => "org.hackeros.tests.site";
        public override string Name => "Site";
        public override AppKind Kind => AppKind.Web;
        public override Type EntryPoint => typeof(FakeCommand);
        public override IReadOnlyList<string> Urls => ["http://blazor.hack/"];
    }

    private sealed class BlazorSiteApp : MvcSiteApp
    {
        public override Type? BlazorRoot => typeof(FakeEmbed);
    }

    [AppManifest]
    private sealed class UnmarkedServiceApp : AppDefinition
    {
        public override string Id => "org.hackeros.tests.service";
        public override string Name => "Service";
        public override AppKind Kind => AppKind.Service;
        public override Type EntryPoint => typeof(FakeCommand);
    }

    [AppManifest]
    private sealed class DefaultCommandApp : AppDefinition
    {
        public override string Id => "org.hackeros.tests.default-cmd";
        public override string Name => "default-cmd";
        public override AppKind Kind => AppKind.Terminal;
        public override Type EntryPoint => typeof(FakeCommand);
        public override TerminalCommand? Terminal => new("default-cmd", "default-cmd");
    }

    [AppManifest(Execution = AppExecutionHost.MainThread)]
    private sealed class MainThreadCommandApp : AppDefinition
    {
        public override string Id => "org.hackeros.tests.main-cmd";
        public override string Name => "main-cmd";
        public override AppKind Kind => AppKind.Terminal;
        public override Type EntryPoint => typeof(FakeCommand);
        public override TerminalCommand? Terminal => new("main-cmd", "main-cmd");
    }

    [AppManifest(Execution = AppExecutionHost.Dedicated)]
    private sealed class DedicatedCommandApp : AppDefinition
    {
        public override string Id => "org.hackeros.tests.dedicated-cmd";
        public override string Name => "dedicated-cmd";
        public override AppKind Kind => AppKind.Terminal;
        public override Type EntryPoint => typeof(FakeCommand);
        public override TerminalCommand? Terminal => new("dedicated-cmd", "dedicated-cmd");
    }

    private sealed class MinimalWindowApp : AppDefinition
    {
        public override string Id => "org.hackeros.tests.minimal";
        public override string Name => "Minimal";
        public override AppKind Kind => AppKind.Window;
        public override Type EntryPoint => typeof(FakeWindow);
    }

    private sealed class RangedApp : AppDefinition
    {
        public override string Id => "org.hackeros.tests.ranged";
        public override string Name => "Ranged";
        public override AppKind Kind => AppKind.Window;
        public override Type EntryPoint => typeof(FakeWindow);
        public override AppSdkCompatibilityManifest? SdkCompatibility => new("0.9.0-preview.1", "0.9.0");
    }

    private sealed class SharedMobileApp : AppDefinition
    {
        public override string Id => "org.hackeros.tests.shared-mobile";
        public override string Name => "Shared";
        public override AppKind Kind => AppKind.Window;
        public override Type EntryPoint => typeof(FakeWindow);
        public override Type? MobileEntryPoint => typeof(FakeWindow);
    }

    private sealed class DistinctMobileApp : AppDefinition
    {
        public override string Id => "org.hackeros.tests.distinct-mobile";
        public override string Name => "Distinct";
        public override AppKind Kind => AppKind.Window;
        public override Type EntryPoint => typeof(FakeWindow);
        public override Type? MobileEntryPoint => typeof(FakeMobileWindow);
    }

    private sealed class FullWindowApp : AppDefinition
    {
        public override string Id => "org.hackeros.tests.full";
        public override string Name => "Full";
        public override AppKind Kind => AppKind.Window;
        public override Type EntryPoint => typeof(FakeWindow);
        public override string Version => "1.2.3";
        public override string Description => "Edits text.";
        public override string PublisherId => "pub.hackeros";
        public override string Category => "productivity";
        public override AppLaunchVisibility LaunchVisibility => AppLaunchVisibility.Hidden;
        public override IReadOnlyList<string> Capabilities => [AppCapabilities.FileSystemUserHomeRead];
        public override IReadOnlyList<FileHandler> FileHandlers => [new("text/plain", [".txt", ".md"], ["open", "edit"], 5)];
        public override bool SingleInstancePerUser => true;
    }

    private sealed class EchoLikeCommand : AppDefinition
    {
        public override string Id => "org.hackeros.tests.echo";
        public override string Name => "echo";
        public override AppKind Kind => AppKind.Terminal;
        public override Type EntryPoint => typeof(FakeCommand);
        public override AppLaunchVisibility LaunchVisibility => AppLaunchVisibility.Hidden;
        public override TerminalCommand? Terminal => new("echo", "echo [string ...]");
    }
}
