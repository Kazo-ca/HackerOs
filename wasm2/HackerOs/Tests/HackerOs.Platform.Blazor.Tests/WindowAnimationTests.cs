using HackerOs.Windowing.Abstractions;
using HackerOs.Windowing.Blazor;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;

namespace HackerOs.Platform.Blazor.Tests;

/// <summary>
/// ADR 0198: <see cref="WindowHost"/> picks the animation of each render from the window's state
/// change, keeps a minimized window drawn until its animation ends, and plays the close animation
/// when it goes away.
/// </summary>
public sealed class WindowAnimationTests
{
    private static readonly WindowId Id = WindowId.FromGuid(Guid.NewGuid());
    private static readonly WindowOwnerId Owner = WindowOwnerId.FromGuid(Guid.NewGuid());

    [Fact]
    public async Task A_new_window_is_drawn_transparent_then_plays_its_open_animation()
    {
        await using Harness harness = new();

        await harness.RenderAsync(Window());

        Assert.Equal(["open"], harness.Js.Transitions);
        Assert.False(harness.IsHidden());
        Assert.Contains("is-opening", harness.HostClass(), StringComparison.Ordinal);

        await harness.RenderAsync(Window(zOrder: 2));

        Assert.DoesNotContain("is-opening", harness.HostClass(), StringComparison.Ordinal);
        Assert.Equal(["open", "none"], harness.Js.Transitions);
    }

    [Fact]
    public async Task A_window_that_starts_minimized_is_hidden_and_not_animated()
    {
        await using Harness harness = new();

        await harness.RenderAsync(Window(WindowVisualState.Minimized));

        Assert.Equal(["none"], harness.Js.Transitions);
        Assert.True(harness.IsHidden());
        Assert.DoesNotContain("is-opening", harness.HostClass(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_minimized_window_stays_drawn_until_its_animation_ends()
    {
        await using Harness harness = new();
        await harness.RenderAsync(Window());
        TaskCompletionSource animation = harness.Js.HoldMinimize();

        await harness.RenderAsync(Window(WindowVisualState.Minimized));

        Assert.Equal("minimize", harness.Js.Transitions[^1]);
        Assert.Equal(Id.ToString(), harness.Js.LastWindowId);
        Assert.False(harness.IsHidden());

        await harness.Dispatch(() => animation.SetResult());

        Assert.True(harness.IsHidden());
    }

    [Fact]
    public async Task A_restore_during_the_minimize_animation_keeps_the_window_shown()
    {
        await using Harness harness = new();
        await harness.RenderAsync(Window());
        TaskCompletionSource animation = harness.Js.HoldMinimize();
        await harness.RenderAsync(Window(WindowVisualState.Minimized));

        await harness.RenderAsync(Window());
        await harness.Dispatch(() => animation.SetResult());

        Assert.Equal("restore", harness.Js.Transitions[^1]);
        Assert.False(harness.IsHidden());
    }

    [Fact]
    public async Task Maximize_restore_and_snap_play_the_geometry_animation_and_a_move_does_not()
    {
        await using Harness harness = new();
        await harness.RenderAsync(Window());

        await harness.RenderAsync(Window(bounds: new WindowBounds(10, 10, 640, 480)));
        await harness.RenderAsync(Window(WindowVisualState.Maximized));
        await harness.RenderAsync(Window());
        await harness.RenderAsync(Window(snapped: true));

        Assert.Equal(["open", "none", "geometry", "geometry", "geometry"], harness.Js.Transitions);
    }

    [Fact]
    public async Task A_closed_window_plays_its_close_animation()
    {
        await using Harness harness = new();
        await harness.RenderAsync(Window());

        await harness.RemoveAsync();

        Assert.Equal(1, harness.Js.CloseCount);
    }

    [Fact]
    public async Task A_chromeless_window_is_never_animated()
    {
        await using Harness harness = new();

        await harness.RenderAsync(Window(), showChrome: false);
        await harness.RenderAsync(Window(WindowVisualState.Maximized), showChrome: false);
        await harness.RemoveAsync();

        Assert.Equal(["none", "none"], harness.Js.Transitions);
        Assert.Equal(0, harness.Js.CloseCount);
    }

    private static WindowRuntimeState Window(
        WindowVisualState state = WindowVisualState.Normal,
        WindowBounds? bounds = null,
        int zOrder = 1,
        bool snapped = false) => new(
            Id, "org.hackeros.test", Owner, "Test window", null, bounds ?? new WindowBounds(48, 42, 640, 480), null, zOrder,
            state, new WindowConstraints(true, 320, 240))
        {
            IsSnapped = snapped
        };

    /// <summary>Renders one <see cref="WindowHost"/> with a renderer that runs its after-render code.</summary>
    private sealed class Harness : IAsyncDisposable
    {
        private readonly ServiceProvider _services;
        private readonly HostRenderer _renderer;
        private int _rootId = -1;

        public Harness()
        {
            ServiceCollection services = new();
            services.AddSingleton<IJSRuntime>(Js);
            _services = services.BuildServiceProvider();
            _renderer = new HostRenderer(_services);
        }

        public RecordingJsRuntime Js { get; } = new();

        public Task RenderAsync(WindowRuntimeState window, bool showChrome = true) => Dispatch(async () =>
        {
            ParameterView parameters = ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(WindowHost.Window)] = window,
                [nameof(WindowHost.ShowChrome)] = showChrome
            });
            if (_rootId < 0)
            {
                _rootId = _renderer.Attach();
            }

            await _renderer.RenderAsync(_rootId, parameters);
        });

        public Task RemoveAsync() => Dispatch(() =>
        {
            _renderer.Remove(_rootId);
            return Task.CompletedTask;
        });

        public Task Dispatch(Action action) => Dispatch(() =>
        {
            action();
            return Task.CompletedTask;
        });

        public async Task Dispatch(Func<Task> action)
        {
            await _renderer.Dispatcher.InvokeAsync(action);
            // Let the after-render continuations and the re-render they ask for run.
            for (int i = 0; i < 5; i++)
            {
                await _renderer.Dispatcher.InvokeAsync(async () => await Task.Yield());
            }

            _renderer.ThrowIfFailed();
        }

        public bool IsHidden() => Attribute("hidden") is not null;

        public string HostClass() => Attribute("class") as string ?? string.Empty;

        // The attributes of the window's <article>, the first element of the host's frames.
        private object? Attribute(string name)
        {
            ArrayRange<RenderTreeFrame> frames = _renderer.Frames(_rootId);
            bool inArticle = false;
            for (int i = 0; i < frames.Count; i++)
            {
                RenderTreeFrame frame = frames.Array[i];
                if (frame.FrameType == RenderTreeFrameType.Element)
                {
                    if (inArticle)
                    {
                        return null;
                    }

                    inArticle = frame.ElementName == "article";
                }
                else if (inArticle && frame.FrameType == RenderTreeFrameType.Attribute && frame.AttributeName == name)
                {
                    return frame.AttributeValue;
                }
            }

            return null;
        }

        public async ValueTask DisposeAsync()
        {
            await _renderer.DisposeAsync();
            await _services.DisposeAsync();
        }
    }

    private sealed class HostRenderer(IServiceProvider services) : Renderer(services, NullLoggerFactory.Instance)
    {
        private Exception? _failure;

        public override Dispatcher Dispatcher { get; } = Dispatcher.CreateDefault();

        // Instantiated by the renderer, so its [Inject] properties are set.
        public int Attach() => AssignRootComponentId(InstantiateComponent(typeof(WindowHost)));

        public Task RenderAsync(int id, ParameterView parameters) => RenderRootComponentAsync(id, parameters);

        public void Remove(int id) => RemoveRootComponent(id);

        public ArrayRange<RenderTreeFrame> Frames(int id) => GetCurrentRenderTreeFrames(id);

        public void ThrowIfFailed()
        {
            if (_failure is not null)
            {
                throw new InvalidOperationException("The window host failed.", _failure);
            }
        }

        protected override void HandleException(Exception exception) => _failure ??= exception;

        protected override Task UpdateDisplayAsync(in RenderBatch renderBatch) => Task.CompletedTask;
    }

    /// <summary>Records the window animations asked of WindowHost.razor.js.</summary>
    private sealed class RecordingJsRuntime : IJSRuntime, IJSObjectReference
    {
        private TaskCompletionSource? _heldMinimize;

        public List<string> Transitions { get; } = [];

        public string? LastWindowId { get; private set; }

        public int CloseCount { get; private set; }

        /// <summary>The next minimize animation ends when the returned source completes.</summary>
        public TaskCompletionSource HoldMinimize() => _heldMinimize = new TaskCompletionSource();

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            if (identifier == "import")
            {
                return ValueTask.FromResult((TValue)(object)this);
            }

            if (identifier == "playClose")
            {
                CloseCount++;
            }

            if (identifier == "projectWindowGeometry" && args is { Length: >= 8 })
            {
                string transition = (string)args[6]!;
                Transitions.Add(transition);
                LastWindowId = args[7] as string;
                if (transition == "minimize" && _heldMinimize is { } held)
                {
                    _heldMinimize = null;
                    return Await<TValue>(held.Task);
                }
            }

            return ValueTask.FromResult(default(TValue)!);
        }

        private static async ValueTask<TValue> Await<TValue>(Task task)
        {
            await task;
            return default!;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
