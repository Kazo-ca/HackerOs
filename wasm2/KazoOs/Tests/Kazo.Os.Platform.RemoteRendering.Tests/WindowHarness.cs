using System.Globalization;
using System.Text.Json;
using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk;
using Kazo.Os.Platform.RemoteRendering.Worker;
using Kazo.Os.Platform.WorkerHost.Worker;
using Kazo.Os.Simulation.Abstractions.Gateways;
using Kazo.Os.Simulation.Abstractions.Processes;
using Kazo.Os.Simulation.Abstractions.Sessions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

// BL0006: the harness disposes the worker's Renderer, a RenderTree type (see DomPatchWriter.cs).
#pragma warning disable BL0006

namespace Kazo.Os.Platform.RemoteRendering.Tests;

/// <summary>
/// A window rendered by the worker's renderer, as in a worker but without one: the <c>ui</c> messages
/// the run posts are applied to a <see cref="FakeDom"/> at once, and DOM events go back through
/// <see cref="WorkerWindowRenderer.DispatchDomEventAsync"/> with the JSON the page would send.
/// </summary>
internal sealed class WindowHarness : IAsyncDisposable
{
    private WindowHarness(Type window)
    {
        Run = new WorkerWindowRun(42, new TestAppContext(), window, Post);
        Renderer = new WorkerWindowRenderer(new ServiceCollection().BuildServiceProvider(), NullLoggerFactory.Instance, Run);
    }

    public WorkerWindowRun Run { get; }

    public WorkerWindowRenderer Renderer { get; }

    public FakeDom Dom { get; } = new();

    /// <summary>Gets the lines the run wrote to the OS's syslog.</summary>
    public List<string> Logs { get; } = [];

    /// <summary>Gets the other <c>ui</c> outputs (dialogs), in order.</summary>
    public List<JsonElement> Outputs { get; } = [];

    /// <summary>Gets the number of batches applied.</summary>
    public int Batches { get; private set; }

    public static async Task<WindowHarness> MountAsync<TWindow>()
    {
        WindowHarness harness = new(typeof(TWindow));
        await harness.Renderer.Dispatcher.InvokeAsync(() => harness.Renderer.MountAsync(harness.Run.Context, typeof(TWindow)));
        return harness;
    }

    /// <summary>Sends a DOM event for the listener of <paramref name="elementId"/>, as the page's module does.</summary>
    public Task FireAsync(string elementId, string eventName = "click", string? arguments = null) =>
        FireAsync(Dom.Find(elementId).Listeners[eventName], eventName, arguments);

    /// <summary>Sends a DOM event for a handler id, even one the worker disposed since.</summary>
    public Task FireAsync(ulong handler, string eventName = "click", string? arguments = null)
    {
        string json = $$"""{"h":{{handler}},"n":"{{eventName}}","a":{{arguments ?? $$"""{"type":"{{eventName}}"}"""}}}""";
        return Renderer.DispatchDomEventAsync(JsonDocument.Parse(json).RootElement);
    }

    public async ValueTask DisposeAsync() => await Renderer.DisposeAsync();

    private void Post(string message)
    {
        using JsonDocument document = JsonDocument.Parse(message);
        JsonElement root = document.RootElement;
        switch (root.GetProperty("t").GetString())
        {
            case "ui":
                Assert.Equal(42, root.GetProperty("p").GetInt64());
                JsonElement output = root.GetProperty("d");
                if (output.GetProperty("k").GetString() == "b")
                {
                    Batches++;
                    Assert.Equal(Batches, output.GetProperty("n").GetInt64());
                    Dom.Apply(output);
                    // The worker's count of page nodes stays in step with the page's.
                    Assert.Equal(Dom.NodeCount, output.GetProperty("nodes").GetInt32());
                }
                else
                {
                    Outputs.Add(output.Clone());
                }

                break;
            case "log":
                Logs.Add(root.GetProperty("m").GetString()!);
                break;
        }
    }
}

/// <summary>An execution context with no gateway: the test windows never call the SDK.</summary>
internal sealed class TestAppContext : IAppExecutionContext
{
    public AppManifest Manifest => throw new NotSupportedException();
    public Guid InstanceId { get; } = Guid.NewGuid();
    public string UserId => "user-1";
    public AppAuthority UserAuthority => AppAuthority.User;
    public IReadOnlySet<string> GrantedCapabilities { get; } = new HashSet<string>(StringComparer.Ordinal);
    public SessionId SessionId { get; } = SessionId.FromGuid(Guid.NewGuid());
    public ProcessId ProcessId { get; } = ProcessId.FromInt64(42);
    public CancellationToken CancellationToken => CancellationToken.None;
    public ICapabilityChecker Capabilities => throw new NotSupportedException();
    public IAppFileSystemGateway FileSystem => throw new NotSupportedException();
    public IAppEventGateway Events => throw new NotSupportedException();
    public IAppNotificationGateway Notifications => throw new NotSupportedException();
    public IAppLoggingGateway Logging => throw new NotSupportedException();
    public IAppClockGateway Clock => throw new NotSupportedException();
    public IAppProcessGateway Processes => throw new NotSupportedException();
    public CultureInfo Culture => CultureInfo.InvariantCulture;
    public CultureInfo FormatCulture => CultureInfo.InvariantCulture;
}
