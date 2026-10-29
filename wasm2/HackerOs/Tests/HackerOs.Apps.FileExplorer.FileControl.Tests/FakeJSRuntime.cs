using Microsoft.JSInterop;

namespace HackerOs.Apps.FileExplorer.FileControl.Tests;

/// <summary>
/// Scriptable <see cref="IJSRuntime"/>/<see cref="IJSObjectReference"/> doubles for
/// <c>FileViewIcons</c>'s marquee-select JS interop (<c>FileViewIcons.razor.js</c>) — there is no real
/// browser/JS host in this xunit process. Each call is dispatched to a registered handler by identifier.
/// </summary>
internal sealed class FakeJSRuntime : IJSRuntime
{
    public Dictionary<string, Func<object?[]?, object?>> Handlers { get; } = [];

    public List<string> CalledIdentifiers { get; } = [];

    public List<(string Identifier, object?[]? Args)> Calls { get; } = [];

    public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
        InvokeAsync<TValue>(identifier, CancellationToken.None, args);

    public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
    {
        // FileView imports its module after its first render to install the native drop listeners
        // (ADR 0058): tests that script no JS still get a module that accepts that call.
        if (identifier == "import" && !Handlers.ContainsKey(identifier))
        {
            return ValueTask.FromResult((TValue)(object)(DefaultModule ??= new FakeJSObjectReference()));
        }

        CalledIdentifiers.Add(identifier);
        Calls.Add((identifier, args));
        if (!Handlers.TryGetValue(identifier, out Func<object?[]?, object?>? handler))
        {

            throw new InvalidOperationException($"No handler registered for '{identifier}'.");
        }

        return ValueTask.FromResult((TValue)handler(args)!);
    }

    /// <summary>Module returned by an unscripted <c>import</c>.</summary>
    public FakeJSObjectReference? DefaultModule { get; private set; }
}

internal sealed class FakeJSObjectReference : IJSObjectReference
{
    public Dictionary<string, Func<object?[]?, object?>> Handlers { get; } = [];

    public List<string> CalledIdentifiers { get; } = [];

    public List<(string Identifier, object?[]? Args)> Calls { get; } = [];

    public bool Disposed { get; private set; }

    /// <summary>Number of unscripted <c>attachDropZone</c> calls (not recorded in <see cref="Calls"/>).</summary>
    public int AttachDropZoneCalls { get; private set; }

    public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
        InvokeAsync<TValue>(identifier, CancellationToken.None, args);

    public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
    {
        // The native drop listeners FileView installs after its first render: a no-op, not recorded,
        // so tests asserting "no JS call" keep testing the interaction they drive.
        if (identifier == "attachDropZone" && !Handlers.ContainsKey(identifier))
        {
            AttachDropZoneCalls++;
            return ValueTask.FromResult((TValue)(object)true);
        }

        // The hover bubbles' listeners (ADR 0185 §16), the folder previews' observer (§14) and the press
        // listener of the drag out of the browser (§4-5), installed after the first render too: same no-op.
        if (identifier is "attachTooltips" or "attachFolderPreviews" or "attachExternalDrag" && !Handlers.ContainsKey(identifier))
        {
            return ValueTask.FromResult((TValue)(object)true);
        }

        CalledIdentifiers.Add(identifier);
        Calls.Add((identifier, args));
        if (!Handlers.TryGetValue(identifier, out Func<object?[]?, object?>? handler))
        {

            throw new InvalidOperationException($"No handler registered for '{identifier}'.");
        }

        return ValueTask.FromResult((TValue)handler(args)!);
    }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }
}
