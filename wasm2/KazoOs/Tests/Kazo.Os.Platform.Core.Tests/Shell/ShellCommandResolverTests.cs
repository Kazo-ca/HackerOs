using Kazo.Os.App.Abstractions;
using Kazo.Os.Platform.Core;
using Kazo.Os.Platform.Core.Lifecycle;
using Kazo.Os.Platform.Core.Shell;
using Xunit;

namespace Kazo.Os.Platform.Core.Tests.Shell;

public sealed class ShellCommandResolverTests
{
    [Fact]
    public void GetTerminalCommands_ReturnsRegisteredManifests_OrderedByName_NotHardcoded()
    {
        AppCatalog catalog = BuildCatalog(
            Manifest("org.example.zeta", "zeta", "does zeta things", "zeta [--flag]"),
            Manifest("org.example.alpha", "alpha", "does alpha things", "alpha <arg>"));
        ShellCommandResolver resolver = new(catalog);

        IReadOnlyList<AppManifest> commands = resolver.GetTerminalCommands();

        Assert.Equal(["alpha", "zeta"], commands.Select(m => m.Terminal!.Name));
        Assert.Equal("does alpha things", commands[0].Description);
        Assert.Equal("alpha <arg>", commands[0].Terminal!.Usage);
    }

    [Fact]
    public void GetTerminalCommands_ExcludesNonTerminalApps()
    {
        AppManifest windowApp = Manifest("org.example.window", "window-app", "a window app", "");
        windowApp = windowApp with { Kind = AppKind.Window, Terminal = null };
        AppCatalog catalog = BuildCatalog(
            Manifest("org.example.cmd", "cmd", "a real command", "cmd"),
            windowApp);
        ShellCommandResolver resolver = new(catalog);

        IReadOnlyList<AppManifest> commands = resolver.GetTerminalCommands();

        Assert.Single(commands);
        Assert.Equal("cmd", commands[0].Terminal!.Name);
    }

    [Fact]
    public void Uninstalled_commands_are_neither_resolved_nor_listed()
    {
        // ADR 0048: an uninstalled command answers "command not found", like a removed binary.
        AppCatalog catalog = BuildCatalog(
            Manifest("org.example.cmd", "cmd", "a real command", "cmd"),
            Manifest("org.example.gone", "gone", "an uninstalled command", "gone"));
        AppEnablementRegistry enablement = new(catalog);
        enablement.MarkDisabled(["org.example.gone"]);
        ShellCommandResolver resolver = new(catalog, enablement);

        Assert.Null(resolver.Resolve("gone", []));
        Assert.NotNull(resolver.Resolve("cmd", []));
        Assert.Equal(["cmd"], resolver.GetTerminalCommands().Select(m => m.Terminal!.Name));
    }

    private static AppCatalog BuildCatalog(params AppManifest[] manifests)
    {
        AppCatalogBuildResult result = AppCatalog.Build(manifests);
        Assert.True(result.IsSuccess, string.Join(", ", result.Errors.Select(e => e.Message)));
        return result.Catalog!;
    }

    [Fact]
    public void Resolve_finds_terminal_apps_by_name_or_alias()
    {
        AppCatalog catalog = BuildCatalog(Manifest("org.hackeros.echo", "echo", "Echoes", "echo [text]", aliases: ["print"]));
        ShellCommandResolver resolver = new(catalog);

        Assert.Equal("org.hackeros.echo", resolver.Resolve("echo", ["hello"])?.AppId);
        Assert.Equal("org.hackeros.echo", resolver.Resolve("print", ["hello"])?.AppId);
        Assert.Null(resolver.Resolve("nonexistent", []));
    }

    [Fact]
    public void A_duplicate_command_name_resolves_to_the_app_the_catalog_gave_it_to()
    {
        // ADR 0098 §4: the first in activation order keeps "tool"; the other app keeps its own alias.
        AppCatalog catalog = BuildCatalog(
            Manifest("org.example.a", "tool", "first", "tool"),
            Manifest("org.example.b", "tool", "second", "tool", aliases: ["btool"]),
            Manifest("org.example.c", "tool", "third", "tool"));
        ShellCommandResolver resolver = new(catalog);

        Assert.Equal("org.example.a", resolver.Resolve("tool", [])?.AppId);
        Assert.Equal("org.example.b", resolver.Resolve("btool", [])?.AppId);
        // An app left with no command name registers no command at all; it stays in the catalog.
        Assert.Equal(["org.example.a", "org.example.b"], resolver.GetTerminalCommands().Select(m => m.Id).Order());
        Assert.Null(resolver.Resolve("org.example.c", []));
        Assert.True(catalog.Manifests.ContainsKey("org.example.c"));
    }

    private static AppManifest Manifest(string id, string name, string description, string usage, IReadOnlyList<string>? aliases = null) => new()
    {
        SchemaVersion = 1,
        Id = id,
        Name = name,
        Version = "1.0.0",
        PublisherId = "pub.hackeros",
        Description = description,
        Kind = AppKind.Terminal,
        EntryPoint = new AppEntryPointManifest($"{name}.dll", $"{name}.Command"),
        SdkCompatibility = new AppSdkCompatibilityManifest(KazoOsSdk.Version),
        Presentation = new PresentationManifest("utilities", AppLaunchVisibility.Hidden, []),
        Capabilities = [],
        Resources = AppResourceProfileManifest.None,
        Terminal = new TerminalCommandManifest(name, aliases ?? [], usage),
        SingleInstancePerUser = false
    };
}
