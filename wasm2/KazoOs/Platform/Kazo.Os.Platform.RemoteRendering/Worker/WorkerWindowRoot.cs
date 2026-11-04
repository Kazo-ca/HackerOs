using System.Diagnostics.CodeAnalysis;
using Kazo.Os.AppSdk;
using Kazo.Os.AppSdk.Blazor;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;

namespace Kazo.Os.Platform.RemoteRendering.Worker;

/// <summary>
/// The root of a window rendered in a worker: the same tree as the page's <c>WindowAppRenderer</c>
/// (the app's context cascaded, fixed, around the app's component with its <c>AppContext</c>), so the
/// app cannot tell where it renders (ADR 0129, criterion "no change to the app").
/// </summary>
internal sealed class WorkerWindowRoot : ComponentBase
{
    /// <summary>Gets or sets the process's remote execution context.</summary>
    [Parameter, EditorRequired]
    public IAppExecutionContext AppContext { get; set; } = null!;

    /// <summary>Gets or sets the app's window component.</summary>
    [Parameter, EditorRequired]
    public Type EntryPointType { get; set; } = null!;

    /// <inheritdoc />
    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        builder.OpenComponent<CascadingValue<IAppExecutionContext>>(0);
        builder.AddComponentParameter(1, nameof(CascadingValue<IAppExecutionContext>.Value), AppContext);
        builder.AddComponentParameter(2, nameof(CascadingValue<IAppExecutionContext>.IsFixed), true);
        builder.AddComponentParameter(3, nameof(CascadingValue<IAppExecutionContext>.ChildContent), (RenderFragment)RenderApp);
        builder.CloseComponent();
    }

    [UnconditionalSuppressMessage(
        "Trimming", "IL2072",
        Justification = "The entry point comes from the OS's validated, allowlisted catalog; a trimmed host keeps app assemblies whole (ADR 0113).")]
    private void RenderApp(RenderTreeBuilder builder)
    {
        builder.OpenComponent(0, EntryPointType);
        builder.AddComponentParameter(1, nameof(WindowAppBase.AppContext), AppContext);
        builder.CloseComponent();
    }
}
