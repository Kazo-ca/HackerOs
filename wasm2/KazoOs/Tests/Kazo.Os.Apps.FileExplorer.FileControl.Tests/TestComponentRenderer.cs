using System.Runtime.ExceptionServices;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kazo.Os.Apps.FileExplorer.FileControl.Tests;

/// <summary>
/// Minimal <see cref="Renderer"/> that attaches a component to a real <see cref="RenderHandle"/> (so
/// <c>StateHasChanged</c>/<c>InvokeAsync</c> work exactly as in production) while discarding the produced
/// render batches — no bUnit in this solution; this is the same technique ASP.NET Core's own component
/// tests use. See <c>FileViewTests.cs</c>.
/// </summary>
internal sealed class TestComponentRenderer(IServiceProvider services) : Renderer(services, NullLoggerFactory.Instance)
{
    public override Dispatcher Dispatcher { get; } = Dispatcher.CreateDefault();

    protected override void HandleException(Exception exception) =>
        ExceptionDispatchInfo.Capture(exception).Throw();

    protected override Task UpdateDisplayAsync(in RenderBatch renderBatch) => Task.CompletedTask;

    /// <summary>Attaches <paramref name="component"/> as a root component and runs its initial render.</summary>
    public Task<TComponent> RenderAsync<TComponent>(TComponent component) where TComponent : IComponent =>
        Dispatcher.InvokeAsync(async () =>
        {
            int componentId = AssignRootComponentId(component);
            await RenderRootComponentAsync(componentId);
            return component;
        });

    /// <summary>Runs <paramref name="action"/> on the renderer's dispatcher, matching production call context.</summary>
    public Task RunAsync(Func<Task> action) => Dispatcher.InvokeAsync(action);
}
