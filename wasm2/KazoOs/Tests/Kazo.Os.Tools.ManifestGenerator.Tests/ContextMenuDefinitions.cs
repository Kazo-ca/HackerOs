using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk;
using Kazo.Os.AppSdk.Blazor.Files;
using Kazo.Os.AppSdk.Files;

namespace Kazo.Os.Tools.ManifestGenerator.Tests;

// Definitions used by ContextMenuRegistrationTests (ADR 0151 §2, ADR 0159). Not marked [AppManifest]:
// the tests hand the inspector an explicit type list.

/// <summary>A service entry point: the kinds that may add file menu entries are window and service.</summary>
public sealed class FixtureService : ServiceAppBase
{
    /// <summary>Initializes the service.</summary>
    public FixtureService(AppManifest manifest) : base(manifest)
    {
    }

    /// <inheritdoc />
    protected override Task RunCoreAsync(IAppExecutionContext context, CancellationToken sessionCancellationToken) => Task.CompletedTask;
}

/// <summary>A service registering two entries, one in a submenu.</summary>
public class MenuApp : AppDefinition
{
    public const string ExtractIntent = "org.hackeros.tests.menu.extract.v1";

    public override string Id => "org.hackeros.tests.menu";
    public override string Name => "menu";
    public override AppKind Kind => AppKind.Service;
    public override Type EntryPoint => typeof(FixtureService);
    public override AppLaunchVisibility LaunchVisibility => AppLaunchVisibility.Hidden;
    public override IReadOnlyList<string> Capabilities => [AppCapabilities.ShellContextMenu];
    public override IReadOnlyList<AppIntentDeclarationManifest> Intents => [new(ExtractIntent)];

    [RegisterFileContextMenu]
    public static void RegisterFileContextMenu(RegisterFileContextMenuContext context)
    {
        context.Register("Extract here", "extract", intent: ExtractIntent, types: [".zip"], after: HackFileMenuItems.Open);
        context.Register("Tools", "tools", scope: FileMenuScope.File | FileMenuScope.Directory);
        context.Register("Hash", "hash", intent: ExtractIntent, parent: "tools");
    }
}

/// <summary>The same entries without the capability (HOSAPP0027).</summary>
public sealed class MenuWithoutCapabilityApp : MenuApp
{
    public override string Id => "org.hackeros.tests.menu-no-capability";
    public override IReadOnlyList<string> Capabilities => [];

    // The generator reads the method declared on the definition class itself, not an inherited one.
    [RegisterFileContextMenu]
    public static void Register(RegisterFileContextMenuContext context) =>
        context.Register("Run", "run", intent: ExtractIntent);
}

/// <summary>A registration breaking a rule of ADR 0151 §5 (HOSAPP0026).</summary>
public sealed class BrokenMenuApp : MenuApp
{
    public override string Id => "org.hackeros.tests.menu-broken";

    [RegisterFileContextMenu]
    public static void Register(RegisterFileContextMenuContext context) =>
        context.Register("Nowhere", "nowhere", intent: ExtractIntent, after: "not-a-menu-entry");
}

/// <summary>A registration that throws (HOSAPP0026: nothing is registered).</summary>
public sealed class ThrowingMenuApp : MenuApp
{
    public override string Id => "org.hackeros.tests.menu-throws";

    [RegisterFileContextMenu]
    public static void Register(RegisterFileContextMenuContext context)
    {
        context.Register("First", "first", intent: ExtractIntent);
        throw new InvalidOperationException("no files at build time");
    }
}

/// <summary>A registration that changes between two runs (HOSAPP0026).</summary>
public sealed class UnstableMenuApp : MenuApp
{
    private static int s_runs;

    public override string Id => "org.hackeros.tests.menu-unstable";

    [RegisterFileContextMenu]
    public static void Register(RegisterFileContextMenuContext context) =>
        context.Register("Run", $"run-{Interlocked.Increment(ref s_runs)}", intent: ExtractIntent);
}

/// <summary>A registration method with the wrong signature (HOSAPP0026).</summary>
public sealed class WrongSignatureMenuApp : MenuApp
{
    public override string Id => "org.hackeros.tests.menu-signature";

    [RegisterFileContextMenu]
    public void Register(RegisterFileContextMenuContext context) =>
        context.Register("Run", "run", intent: ExtractIntent);
}
