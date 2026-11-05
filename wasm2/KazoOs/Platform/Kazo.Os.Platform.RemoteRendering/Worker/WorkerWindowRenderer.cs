using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Kazo.Os.AppSdk;
using Kazo.Os.Platform.WorkerHost.Worker;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.Extensions.Logging;

// BL0006: RenderBatch, RenderTreeFrame and RenderTreeEdit are public, but Blazor warns they may change
// in a future release. Reading them is the whole point of the trial (ADR 0129, section 1); ADR 0133
// records the risk: each .NET upgrade re-runs the trial's tests before shipping.
#pragma warning disable BL0006

namespace Kazo.Os.Platform.RemoteRendering.Worker;

/// <summary>
/// The Blazor renderer of a window rendered in a worker (ADR 0129 trial, ADR 0133): derived from the
/// public <see cref="Renderer"/>, it renders the app's window component exactly as the page's renderer
/// would, but each <see cref="RenderBatch"/> leaves as DOM operations (<see cref="DomPatchWriter"/>)
/// in a <c>ui</c> message, and DOM events come back through <see cref="DispatchDomEventAsync"/>.
/// </summary>
internal sealed class WorkerWindowRenderer : Renderer
{
    private readonly WorkerWindowRun _run;
    private readonly DomPatchWriter _patch = new();
    private readonly Dispatcher _dispatcher = Dispatcher.CreateDefault();
    private readonly TaskCompletionSource<Exception?> _ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _batches;

    public WorkerWindowRenderer(IServiceProvider services, ILoggerFactory loggerFactory, WorkerWindowRun run)
        : base(services, loggerFactory)
    {
        _run = run;
    }

    /// <inheritdoc />
    public override Dispatcher Dispatcher => _dispatcher;

    /// <summary>Gets the task that ends when rendering failed (with the exception) or the window closed (<see langword="null"/>).</summary>
    public Task<Exception?> Ended => _ended.Task;

    /// <summary>Renders the window's root (the app component inside its context cascade) for the first time.</summary>
    public Task MountAsync(IAppExecutionContext context, Type entryPointType)
    {
        IComponent root = InstantiateComponent(typeof(WorkerWindowRoot));
        int rootId = AssignRootComponentId(root);
        _patch.AttachRoot(rootId);
        return RenderRootComponentAsync(rootId, ParameterView.FromDictionary(new Dictionary<string, object?>
        {
            [nameof(WorkerWindowRoot.AppContext)] = context,
            [nameof(WorkerWindowRoot.EntryPointType)] = entryPointType
        }));
    }

    /// <summary>
    /// Dispatches a DOM event the page sent: <c>h</c> the handler id, <c>n</c> the event name, <c>a</c>
    /// its arguments. An <c>input</c> or <c>change</c> event carries its field value, so <c>@bind</c>
    /// does not re-render the value the user is typing (Blazor's <see cref="EventFieldInfo"/>).
    /// </summary>
    public Task DispatchDomEventAsync(JsonElement input)
    {
        ulong handlerId = input.GetProperty("h").GetUInt64();
        string eventName = input.GetProperty("n").GetString() ?? string.Empty;
        JsonElement arguments = input.GetProperty("a");
        EventArgs eventArgs = DomEventArgsReader.Read(eventName, arguments);
        EventFieldInfo? field = null;
        if (eventName is "input" or "change"
            && DomEventArgsReader.ReadValue(arguments) is { } value
            && _patch.TryGetHandler(handlerId, out DomPatchWriter.HandlerInfo handler))
        {
            field = new EventFieldInfo { ComponentId = handler.ComponentId, FieldValue = value };
        }

        return Dispatcher.InvokeAsync(async () =>
        {
            try
            {
                await DispatchEventAsync(handlerId, field, eventArgs);
            }
            catch (ArgumentException exception)
            {
                // A handler id disposed by a render the page had not applied yet: the click raced a
                // re-render, as on the page's own renderer. Nothing to run.
                _run.Log($"event '{eventName}' for a disposed handler: {exception.Message}");
            }
        });
    }

    /// <summary>Ends the window: the next <see cref="Ended"/> completes with no error.</summary>
    public void Close() => _ended.TrySetResult(null);

    /// <inheritdoc />
    protected override void HandleException(Exception exception)
    {
        _run.Log($"window render failed: {exception.GetType().Name}: {exception.Message}");
        _ended.TrySetResult(exception);
    }

    /// <inheritdoc />
    /// <remarks>
    /// The batch is only valid during this call: it is written to the message here. The page applies
    /// batches in order; nothing waits for its acknowledgement, a window's batches are small.
    /// </remarks>
    protected override Task UpdateDisplayAsync(in RenderBatch renderBatch)
    {
        long started = Stopwatch.GetTimestamp();
        RenderBatch batch = renderBatch;
        long number = ++_batches;
        _run.Send(writer =>
        {
            writer.WriteString("k", "b");
            writer.WriteNumber("n", number);
            writer.WriteStartArray("o");
            _patch.Write(batch, writer);
            writer.WriteEndArray();
            // Diagnostics for the trial's measurements (ADR 0133): time spent encoding, nodes on the page.
            writer.WriteNumber("us", Stopwatch.GetElapsedTime(started).TotalMicroseconds);
            writer.WriteNumber("nodes", _patch.NodeCount);
        });
        return Task.CompletedTask;
    }
}
