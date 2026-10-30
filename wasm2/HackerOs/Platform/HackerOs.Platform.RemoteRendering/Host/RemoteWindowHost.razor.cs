using System.Text.Json;
using HackerOs.AppSdk;
using HackerOs.AppSdk.Blazor;
using HackerOs.Platform.Core.Discovery;
using HackerOs.Platform.Core.Execution;
using HackerOs.Platform.RemoteRendering.Worker;
using HackerOs.Platform.WorkerHost.Host;
using HackerOs.Windowing.Abstractions;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace HackerOs.Platform.RemoteRendering.Host;

/// <summary>
/// The page side of a window rendered in a worker (ADR 0129 trial, ADR 0133): the X11 server of one
/// window. It starts the app's process in a worker of its own, applies the DOM operations the worker
/// sends to its content element (<c>RemoteWindowHost.razor.js</c>), sends the DOM events back, and
/// opens the SDK dialogs the app asks for. The window's chrome is the page's: a frozen worker never
/// stops the window from moving or closing.
/// </summary>
public sealed partial class RemoteWindowHost : ComponentBase, IAsyncDisposable
{
    private const string ModulePath = "./_content/HackerOs.Platform.RemoteRendering/Host/RemoteWindowHost.razor.js";

    private readonly Queue<string> _early = new();
    private ElementReference _surface;
    private IJSObjectReference? _module;
    private IJSObjectReference? _surfaceHandle;
    private DotNetObjectReference<RemoteWindowHost>? _self;
    private RemoteWindowProcess? _process;
    private bool _fallback;
    private string _state = "starting";
    private int? _workerId;
    private string? _failure;
    private bool _disposed;

    /// <summary>Gets or sets the window (from <c>WindowAppRenderer</c>).</summary>
    [Parameter, EditorRequired]
    public WindowRuntimeState Window { get; set; } = null!;

    /// <summary>Gets or sets the trusted catalog descriptor of the app.</summary>
    [Parameter, EditorRequired]
    public AppDescriptor Descriptor { get; set; } = null!;

    /// <summary>Gets or sets the app's execution context: the OS's own, through which the worker's calls are served.</summary>
    [Parameter, EditorRequired]
    public IAppExecutionContext AppContext { get; set; } = null!;

    [Inject]
    private IJSRuntime JS { get; set; } = null!;

    [Inject]
    private IServiceProvider Services { get; set; } = null!;

    /// <inheritdoc />
    protected override void OnInitialized()
    {
        if (Services.GetService(typeof(IRemoteProcessHost)) is not WorkerRemoteProcessHost)
        {
            _fallback = true;
        }
    }

    /// <inheritdoc />
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender || _fallback)
        {
            return;
        }

        _self = DotNetObjectReference.Create(this);
        _module = await JS.InvokeAsync<IJSObjectReference>("import", ModulePath);
        _surfaceHandle = await _module.InvokeAsync<IJSObjectReference>("attach", _surface, _self);
        WorkerRemoteProcessHost host = (WorkerRemoteProcessHost)Services.GetService(typeof(IRemoteProcessHost))!;
        try
        {
            _process = await host.StartWindowAsync(
                new RemoteProcessLaunch(Descriptor, AppContext, Control: null, PlacementGroup: null),
                typeof(WorkerWindowRunner),
                OnWorkerOutput,
                AppContext.CancellationToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        _workerId = _process.WorkerId;
        _state = "running";
        StateHasChanged();
        _ = WatchExitAsync(_process);
        while (_early.TryDequeue(out string? batch))
        {
            await ApplyAsync(batch);
        }
    }

    /// <summary>The app's component on the page, when this host has no worker.</summary>
    private void RenderOnPage(Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder builder)
    {
        builder.OpenComponent(0, Descriptor.EntryPointType);
        builder.AddComponentParameter(1, nameof(WindowAppBase.AppContext), AppContext);
        builder.CloseComponent();
    }

    /// <summary>A DOM event from the module: sent to the worker as <c>{k:"e", h, n, a}</c>.</summary>
    /// <param name="handlerId">The handler id the worker gave the listener.</param>
    /// <param name="eventName">The DOM event's name.</param>
    /// <param name="argumentsJson">The event's plain properties as JSON.</param>
    [JSInvokable]
    public void DispatchEvent(ulong handlerId, string eventName, string argumentsJson)
    {
        _process?.Send(writer =>
        {
            writer.WriteString("k", "e");
            writer.WriteNumber("h", handlerId);
            writer.WriteString("n", eventName);
            writer.WritePropertyName("a");
            writer.WriteRawValue(argumentsJson);
        });
    }

    /// <summary>A <c>ui</c> output of the worker, on the page's thread.</summary>
    private void OnWorkerOutput(JsonElement output)
    {
        switch (output.GetProperty("k").GetString())
        {
            case "b":
                string batch = output.GetRawText();
                if (_surfaceHandle is null || _process is null)
                {
                    _early.Enqueue(batch);
                }
                else
                {
                    _ = ApplyAsync(batch);
                }

                break;
            case "dlg":
                _ = OpenDialogAsync(output.Clone());
                break;
        }
    }

    private async Task ApplyAsync(string batch)
    {
        if (_disposed || _surfaceHandle is null)
        {
            return;
        }

        try
        {
            // In WebAssembly the module is in-process: applying synchronously saves a turn of the
            // event loop per batch, which is most of the latency budget (ADR 0133, section 5).
            if (_surfaceHandle is IJSInProcessObjectReference inProcess)
            {
                inProcess.InvokeVoid("apply", batch);
            }
            else
            {
                await _surfaceHandle.InvokeVoidAsync("apply", batch);
            }
        }
        catch (JSDisconnectedException)
        {
            // The page is going away.
        }
    }

    /// <summary>Opens the OS's own dialog for the app's process and sends the answer back (ADR 0133, section 3).</summary>
    private async Task OpenDialogAsync(JsonElement request)
    {
        IDialogService? dialogs = Services.GetService(typeof(IDialogService)) as IDialogService;
        int id = request.GetProperty("id").GetInt32();
        string title = request.GetProperty("title").GetString() ?? string.Empty;
        string content = request.GetProperty("content").GetString() ?? string.Empty;
        try
        {
            if (dialogs is null)
            {
                throw new InvalidOperationException("This host has no dialog service.");
            }

            if (request.GetProperty("ty").GetString() == "txt")
            {
                TextInputDialogResult result = await dialogs.TextInputAsync(AppContext, new TextInputDialogRequest
                {
                    Title = title,
                    Content = content,
                    DefaultValue = request.TryGetProperty("default", out JsonElement value) ? value.GetString() : null,
                    Placeholder = request.TryGetProperty("placeholder", out JsonElement placeholder) ? placeholder.GetString() : null
                }, AppContext.CancellationToken);
                Answer(id, (int)result.Status, result.Value);
            }
            else
            {
                MessageBoxDialogResult result = await dialogs.MessageBoxAsync(AppContext, new MessageBoxDialogRequest
                {
                    Title = title,
                    Content = content,
                    DialogType = (MessageBoxType)request.GetProperty("type").GetInt32(),
                    Icon = request.TryGetProperty("icon", out JsonElement icon) ? (MessageBoxIcon)icon.GetInt32() : MessageBoxIcon.None
                }, AppContext.CancellationToken);
                Answer(id, (int)result.Result, null);
            }
        }
        catch (Exception exception) when (exception is OperationCanceledException or InvalidOperationException)
        {
            // Closed with the window, or no dialog here: the app sees a cancelled choice.
            Answer(id, (int)MessageBoxResult.Cancel, null);
        }
    }

    private void Answer(int id, int result, string? value) =>
        _process?.Send(writer =>
        {
            writer.WriteString("k", "dlgr");
            writer.WriteNumber("id", id);
            writer.WriteNumber("r", result);
            if (value is not null)
            {
                writer.WriteString("v", value);
            }
        });

    /// <summary>Shows why the window's process ended while the window is still open (a fault in the worker).</summary>
    private async Task WatchExitAsync(RemoteWindowProcess process)
    {
        RemoteProcessExit exit = await process.Exit;
        if (_disposed)
        {
            return;
        }

        await InvokeAsync(() =>
        {
            _state = "ended";
            _failure = exit is { Status: RemoteProcessExitStatus.Exited, ExitCode: 0 }
                ? null
                : $"{Descriptor.Manifest.Name}: {exit.FaultMessage ?? $"{exit.Status} ({exit.ExitCode})"}";
            StateHasChanged();
        });
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        if (_process is { } process)
        {
            await process.CloseAsync(static writer => writer.WriteString("k", "close"));
        }

        try
        {
            if (_surfaceHandle is not null)
            {
                await _surfaceHandle.InvokeVoidAsync("dispose");
                await _surfaceHandle.DisposeAsync();
            }

            if (_module is not null)
            {
                await _module.DisposeAsync();
            }
        }
        catch (JSDisconnectedException)
        {
            // The page is going away.
        }

        _self?.Dispose();
    }
}
