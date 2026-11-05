using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk;

namespace Kazo.Os.Tools.ManifestGenerator.Tests;

// Definitions used by AppDefinitionInspectorTests. They are deliberately NOT marked [AppManifest]
// (except where the attribute itself is under test) so this assembly as a whole stays inspectable;
// tests hand the inspector an explicit type list instead.

/// <summary>A valid terminal entry point living in this test assembly.</summary>
public sealed class FixtureCommand : TerminalAppBase
{
    /// <summary>Initializes the command.</summary>
    public FixtureCommand(AppManifest manifest) : base(manifest)
    {
    }

    /// <inheritdoc />
    public override ValueTask<int> ExecuteAsync(TerminalExecutionContext context, CancellationToken cancellationToken) =>
        ValueTask.FromResult(0);
}

/// <summary>A class deriving from nothing the window kind accepts.</summary>
public sealed class NotAWindow;

/// <summary>Valid terminal app.</summary>
public class ValidCommandApp : AppDefinition
{
    public override string Id => "org.kazoos.tests.valid";
    public override string Name => "valid";
    public override AppKind Kind => AppKind.Terminal;
    public override Type EntryPoint => typeof(FixtureCommand);
    public override AppLaunchVisibility LaunchVisibility => AppLaunchVisibility.Hidden;
    public override TerminalCommand? Terminal => new("valid", "valid");
}

/// <summary>Second valid app, distinct id.</summary>
public sealed class SecondCommandApp : ValidCommandApp
{
    public override string Id => "org.kazoos.tests.second";
    public override TerminalCommand? Terminal => new("second", "second");
}

/// <summary>Another app declaring the command name of <see cref="ValidCommandApp"/> as an alias (ADR 0098 §4).</summary>
public sealed class SameCommandApp : ValidCommandApp
{
    public override string Id => "org.kazoos.tests.same-command";
    public override TerminalCommand? Terminal => new("same", "same", ["valid"]);
}

/// <summary>An app declaring a name the shell runs itself (ADR 0098 §4).</summary>
public sealed class BuiltinNameApp : ValidCommandApp
{
    public override string Id => "org.kazoos.tests.builtin-name";
    public override TerminalCommand? Terminal => new("eval", "eval");
}

/// <summary>An app shielding itself from the OOM killer (ADR 0126 §4): allowed only as a system app.</summary>
public sealed class ShieldedApp : ValidCommandApp
{
    public override string Id => "org.kazoos.tests.shielded";
    public override TerminalCommand? Terminal => new("shielded", "shielded");
    public override int? OomScoreAdjust => -300;
}

/// <summary>An app declaring a wwwroot asset (ADR 0098 §5).</summary>
public sealed class AssetApp : ValidCommandApp
{
    public override string Id => "org.kazoos.tests.asset";
    public override TerminalCommand? Terminal => new("asset", "asset");
    public override IReadOnlyList<AssetManifest> Assets => [new("data/table.json", AssetKind.Data, "")];
}

/// <summary>Same id as <see cref="ValidCommandApp"/>.</summary>
public sealed class DuplicateIdApp : ValidCommandApp;

internal sealed class InternalApp : ValidCommandApp;

public abstract class AbstractApp : ValidCommandApp;

public sealed class NotADefinition;

public sealed class NoDefaultConstructorApp(string id) : ValidCommandApp
{
    public override string Id => id;
}

public sealed class ThrowingApp : ValidCommandApp
{
    public override string Description => throw new InvalidOperationException("boom");
}

public sealed class ForeignEntryPointApp : ValidCommandApp
{
    public override Type EntryPoint => typeof(string);
}

public sealed class ClockApp : ValidCommandApp
{
    private static int _counter;

    public override string Version => $"1.0.{Interlocked.Increment(ref _counter)}";
}

public sealed class InvalidIdApp : ValidCommandApp
{
    public override string Id => "Not A Valid Id";
}

public sealed class WrongKindApp : ValidCommandApp
{
    public override string Id => "org.kazoos.tests.wrong-kind";
    public override AppKind Kind => AppKind.Window;
    public override AppLaunchVisibility LaunchVisibility => AppLaunchVisibility.Visible;
    public override TerminalCommand? Terminal => null;
    public override Type EntryPoint => typeof(NotAWindow);
}

/// <summary>Marked definition, used to check attribute discovery by name.</summary>
[AppManifest]
public sealed class MarkedApp : ValidCommandApp
{
    public override string Id => "org.kazoos.tests.marked";
    public override TerminalCommand? Terminal => new("marked", "marked");
}
