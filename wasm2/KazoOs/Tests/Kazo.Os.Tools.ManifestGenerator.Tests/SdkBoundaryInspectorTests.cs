using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk;
using Kazo.Os.Simulation.Abstractions.Sessions;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Kazo.Os.Tools.ManifestGenerator.Tests;

/// <summary>Injection rule of the SDK boundary (ADR 0055 §3, HOSAPP0031 per ADR 0059).</summary>
public sealed class SdkBoundaryInspectorTests
{
    [Fact]
    public void Sdk_types_and_IJSRuntime_may_be_injected()
    {
        IReadOnlyList<GeneratorDiagnostic> diagnostics = SdkBoundaryInspector.Inspect([typeof(AllowedComponent)], []);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public void Os_service_injected_by_property_is_reported()
    {
        IReadOnlyList<GeneratorDiagnostic> diagnostics = SdkBoundaryInspector.Inspect([typeof(ForbiddenComponent)], []);

        GeneratorDiagnostic diagnostic = Assert.Single(diagnostics);
        Assert.Equal("HOSAPP0031", diagnostic.Code);
        Assert.Equal(typeof(ForbiddenComponent).FullName, diagnostic.Subject);
        Assert.Contains("'Sessions'", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains(typeof(ISessionService).FullName!, diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Property_without_Inject_is_data_not_a_service()
    {
        Assert.Empty(SdkBoundaryInspector.Inspect([typeof(DataHolder)], []));
    }

    [Fact]
    public void Inherited_inject_property_is_reported_once_on_its_declaring_type()
    {
        IReadOnlyList<GeneratorDiagnostic> diagnostics = SdkBoundaryInspector.Inspect([typeof(ForbiddenComponent), typeof(DerivedComponent)], []);

        Assert.Equal(typeof(ForbiddenComponent).FullName, Assert.Single(diagnostics).Subject);
    }

    [Fact]
    public void Entry_point_constructor_service_is_reported_but_manifest_and_values_are_not()
    {
        IReadOnlyList<GeneratorDiagnostic> diagnostics = SdkBoundaryInspector.Inspect([], [typeof(ForbiddenCommand), typeof(FixtureCommand)]);

        GeneratorDiagnostic diagnostic = Assert.Single(diagnostics);
        Assert.Equal(typeof(ForbiddenCommand).FullName, diagnostic.Subject);
        Assert.Contains("constructor parameter 'sessions'", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Constructors_of_non_entry_types_are_not_inspected()
    {
        Assert.Empty(SdkBoundaryInspector.Inspect([typeof(ForbiddenCommand)], []));
    }

    [Theory]
    [InlineData("Kazo.Os.AppSdk", true)]
    [InlineData("Kazo.Os.AppSdk.Blazor", true)]
    [InlineData("Kazo.Os.AppSdkExtras", false)]
    [InlineData("Kazo.Os.Platform.Core", false)]
    [InlineData(null, false)]
    public void Sdk_assemblies_are_recognised_by_name(string? name, bool expected)
    {
        Assert.Equal(expected, SdkBoundaryInspector.IsSdkAssembly(name));
    }

    /// <summary>Injects only what ADR 0055 §3 allows.</summary>
    public sealed class AllowedComponent
    {
        [Inject] public IAppExecutionContext Context { get; set; } = default!;

        [Inject] private IJSRuntime Runtime { get; set; } = default!;
    }

    /// <summary>Injects a raw OS service.</summary>
    public class ForbiddenComponent
    {
        [Inject] protected ISessionService Sessions { get; set; } = default!;
    }

    /// <summary>Inherits the forbidden injection; must not be reported twice.</summary>
    public sealed class DerivedComponent : ForbiddenComponent;

    /// <summary>Uses OS types as data only.</summary>
    public sealed class DataHolder
    {
        public ISessionService? NotInjected { get; set; }
    }

    /// <summary>Entry point asking the activator for a raw OS service.</summary>
    public sealed class ForbiddenCommand : TerminalAppBase
    {
        public ForbiddenCommand(AppManifest manifest, ISessionService sessions, TimeSpan delay, string label) : base(manifest)
        {
            _ = sessions;
            _ = delay;
            _ = label;
        }

        public override ValueTask<int> ExecuteAsync(TerminalExecutionContext context, CancellationToken cancellationToken) =>
            ValueTask.FromResult(0);
    }
}
