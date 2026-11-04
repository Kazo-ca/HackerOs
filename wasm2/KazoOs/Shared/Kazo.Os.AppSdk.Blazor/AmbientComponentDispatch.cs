using Kazo.Os.AppSdk;
using Microsoft.AspNetCore.Components;

namespace Kazo.Os.AppSdk.Blazor;

/// <summary>
/// Component plumbing shared by <see cref="WindowAppBase"/> and <see cref="KazoComponentBase"/>: runs
/// Blazor's event and after-render entry points inside the app's <see cref="KazoExecutionContext"/>
/// scope (ADR 0043, section 2). The renderer invokes these entry points from its own flow, so without
/// this an <c>@onclick</c> handler would see no ambient app.
/// </summary>
internal static class AmbientComponentDispatch
{
    /// <summary>Opens an ambient scope for <paramref name="context"/>, or nothing when there is no context yet.</summary>
    public static IDisposable? Enter(IAppExecutionContext? context) =>
        context is null ? null : KazoExecutionContext.Enter(context);

    /// <summary>
    /// Same behavior as <see cref="ComponentBase"/>'s <c>IHandleEvent.HandleEventAsync</c> (invoke,
    /// re-render, re-render again once an asynchronous handler completes), wrapped in the ambient scope.
    /// </summary>
    public static Task HandleEventAsync(
        IAppExecutionContext? context, EventCallbackWorkItem callback, object? arg, Action stateHasChanged)
    {
        using IDisposable? scope = Enter(context);
        Task task = callback.InvokeAsync(arg);
        bool shouldAwaitTask = task.Status != TaskStatus.RanToCompletion && task.Status != TaskStatus.Canceled;

        // After each event, re-render (as ComponentBase does), then again once async work completes.
        stateHasChanged();
        return shouldAwaitTask ? RenderOnCompletionAsync(task, stateHasChanged) : Task.CompletedTask;
    }

    private static async Task RenderOnCompletionAsync(Task task, Action stateHasChanged)
    {
        try
        {
            await task;
        }
        catch
        {
            // A cancelled handler is not an error, matching ComponentBase.
            if (task.IsCanceled)
            {
                return;
            }

            throw;
        }

        stateHasChanged();
    }
}
