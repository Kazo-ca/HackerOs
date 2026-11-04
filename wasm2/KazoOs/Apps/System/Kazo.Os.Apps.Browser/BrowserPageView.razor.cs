using Kazo.Os.AppSdk;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Kazo.Os.Apps.Browser;

/// <summary>A document for <see cref="BrowserPageView"/>.</summary>
/// <param name="Html">The composed document (<c>srcdoc</c>).</param>
/// <param name="Token">The bridge token of the document.</param>
public sealed record PageFrameDocument(string Html, string Token);

/// <summary>
/// Hosts the isolated <c>&lt;iframe&gt;</c> of an HTML page (ADR 0075 §1) and relays the page
/// bridge's messages to the browser window. Messages arrive from JavaScript outside any Blazor
/// event, so each one runs inside the window app's ambient scope: the handlers may use the SDK.
/// </summary>
public sealed partial class BrowserPageView : IAsyncDisposable
{
    private ElementReference _host;
    private IJSObjectReference? _module;
    private IJSObjectReference? _frame;
    private DotNetObjectReference<BrowserPageView>? _self;
    private PageFrameDocument? _shown;

    [Inject]
    private IJSRuntime JavaScript { get; set; } = default!;

    /// <summary>The document to show; a new instance writes a new document.</summary>
    [Parameter, EditorRequired]
    public PageFrameDocument Document { get; set; } = default!;

    /// <summary>Answers a bridge request (<c>fetch</c>, <c>resource</c>): operation and JSON payload in, JSON reply out.</summary>
    [Parameter, EditorRequired]
    public Func<string, string, Task<string>> OnRequest { get; set; } = default!;

    /// <summary>Receives a bridge event (<c>navigate</c>, <c>console</c>, <c>storage</c>…): type and JSON payload.</summary>
    [Parameter, EditorRequired]
    public Func<string, string, Task> OnEvent { get; set; } = default!;

    /// <summary>Called when the page navigated the frame by script, out of the bridge's reach.</summary>
    [Parameter]
    public Func<Task>? OnEscaped { get; set; }

    /// <inheritdoc />
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            _self = DotNetObjectReference.Create(this);
            _module = await JavaScript.InvokeAsync<IJSObjectReference>(
                "import", "./_content/Kazo.Os.Apps.Browser/BrowserPageView.razor.js");
            _frame = await _module.InvokeAsync<IJSObjectReference>("attach", _host, _self);
        }

        if (_frame is not null && !ReferenceEquals(_shown, Document))
        {
            _shown = Document;
            await _frame.InvokeVoidAsync("show", Document.Html, Document.Token);
        }
    }

    /// <summary>Gives the keyboard focus to the page.</summary>
    public ValueTask FocusAsync() => _frame?.InvokeVoidAsync("focus") ?? ValueTask.CompletedTask;

    /// <summary>Called by the frame script for a bridge request.</summary>
    [JSInvokable]
    public async Task<string> OnBridgeRequest(string operation, string payload)
    {
        using IDisposable scope = HackExecutionContext.Enter(RequireAppContext());
        return await OnRequest(operation, payload);
    }

    /// <summary>Called by the frame script for a bridge event.</summary>
    [JSInvokable]
    public async Task OnBridgeEvent(string type, string payload)
    {
        using IDisposable scope = HackExecutionContext.Enter(RequireAppContext());
        await OnEvent(type, payload);
    }

    /// <summary>Called by the frame script when the frame loaded a document the browser did not write.</summary>
    [JSInvokable]
    public async Task OnFrameEscaped()
    {
        using IDisposable scope = HackExecutionContext.Enter(RequireAppContext());
        if (OnEscaped is not null)
        {
            await OnEscaped();
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_frame is not null)
            {
                await _frame.InvokeVoidAsync("dispose");
                await _frame.DisposeAsync();
            }

            if (_module is not null)
            {
                await _module.DisposeAsync();
            }
        }
        catch (JSDisconnectedException)
        {
            // The circuit or the page is gone: nothing left to clean up on the JavaScript side.
        }

        _self?.Dispose();
    }

    // The window cascades its execution context to every component it hosts; a frame message
    // without one would run outside any app, which the SDK must never allow.
    private IAppExecutionContext RequireAppContext() =>
        AppContext ?? throw new InvalidOperationException("BrowserPageView must be hosted inside the Browser window.");
}
