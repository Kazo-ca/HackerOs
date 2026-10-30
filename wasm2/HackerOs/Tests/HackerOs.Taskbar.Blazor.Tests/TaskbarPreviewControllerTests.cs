using System.Net;
using HackerOs.AppSdk.Icons;
using HackerOs.Windowing.Abstractions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace HackerOs.Taskbar.Blazor.Tests;

/// <summary>
/// ADR 0199 §2-3: when the hover preview of a window button opens, moves, closes and peeks, and what
/// a click on it does.
/// </summary>
public sealed class TaskbarPreviewControllerTests
{
    private static readonly WindowId Notes = WindowId.FromGuid(Guid.Parse("92000000-0000-0000-0000-000000000001"));
    private static readonly WindowId Terminal = WindowId.FromGuid(Guid.Parse("92000000-0000-0000-0000-000000000002"));

    [Fact]
    public void The_preview_opens_only_after_the_pointer_rests_on_the_button()
    {
        Fixture fixture = new();

        fixture.Controller.EnterItem(Notes, "mouse");

        Assert.Null(fixture.Controller.Shown);
        Assert.Equal(TaskbarPreviewController.OpenDelay, fixture.Scheduler.NextDelay);

        fixture.Scheduler.RunNext();

        Assert.Equal(Notes, fixture.Controller.Shown);
        Assert.Equal(1, fixture.Changes);
    }

    [Fact]
    public void Leaving_the_button_before_the_delay_opens_nothing()
    {
        Fixture fixture = new();

        fixture.Controller.EnterItem(Notes, "mouse");
        fixture.Controller.LeaveItem();
        fixture.Scheduler.RunAll();

        Assert.Null(fixture.Controller.Shown);
        Assert.Equal(0, fixture.Changes);
    }

    [Fact]
    public void A_touch_pointer_never_opens_a_preview()
    {
        Fixture fixture = new();

        fixture.Controller.EnterItem(Notes, "touch");

        Assert.False(fixture.Scheduler.HasPending);
        Assert.Null(fixture.Controller.Shown);
    }

    [Fact]
    public void An_open_preview_moves_to_the_next_button_at_once()
    {
        Fixture fixture = Fixture.Showing(Notes);

        fixture.Controller.LeaveItem();
        fixture.Controller.EnterItem(Terminal, "mouse");

        Assert.Equal(Terminal, fixture.Controller.Shown);
        Assert.False(fixture.Scheduler.HasPending);
    }

    [Fact]
    public void The_preview_stays_while_the_pointer_crosses_from_the_button_to_it()
    {
        Fixture fixture = Fixture.Showing(Notes);

        fixture.Controller.LeaveItem();
        fixture.Controller.EnterPreview();

        Assert.Equal(TaskbarPreviewController.PeekDelay, fixture.Scheduler.NextDelay);
        Assert.Equal(Notes, fixture.Controller.Shown);
    }

    [Fact]
    public void Resting_on_the_preview_peeks_and_leaving_it_ends_the_peek_then_closes_it()
    {
        Fixture fixture = Fixture.Showing(Notes);

        fixture.Controller.EnterPreview();
        fixture.Scheduler.RunNext();

        Assert.Equal([Notes], fixture.Previews.Peeks);

        fixture.Controller.LeavePreview();

        Assert.Equal([Notes, null], fixture.Previews.Peeks);
        Assert.Equal(TaskbarPreviewController.HideDelay, fixture.Scheduler.NextDelay);

        fixture.Scheduler.RunNext();

        Assert.Null(fixture.Controller.Shown);
    }

    [Fact]
    public void Dismissing_closes_the_preview_and_ends_the_peek_at_once()
    {
        Fixture fixture = Fixture.Showing(Notes);
        fixture.Controller.EnterPreview();
        fixture.Scheduler.RunNext();

        fixture.Controller.Dismiss();

        Assert.Null(fixture.Controller.Shown);
        Assert.Equal([Notes, null], fixture.Previews.Peeks);
        Assert.False(fixture.Scheduler.HasPending);
    }

    [Fact]
    public void The_preview_closes_when_its_window_leaves_the_taskbar()
    {
        Fixture fixture = Fixture.Showing(Notes);

        fixture.Controller.Sync([Item(Terminal)]);

        Assert.Null(fixture.Controller.Shown);
    }

    [Fact]
    public void The_preview_stays_while_its_window_is_listed()
    {
        Fixture fixture = Fixture.Showing(Notes);

        fixture.Controller.Sync([Item(Notes), Item(Terminal)]);

        Assert.Equal(Notes, fixture.Controller.Shown);
    }

    [Fact]
    public void A_click_on_the_preview_of_a_minimized_window_restores_it_and_never_minimizes()
    {
        Commands commands = new();

        TaskbarWindowInteraction.Show(Item(Notes, isFocused: false, isMinimized: true), commands);
        TaskbarWindowInteraction.Show(Item(Terminal, isFocused: true, isMinimized: false), commands);

        Assert.Equal([$"restore:{Notes}", $"activate:{Notes}", $"activate:{Terminal}"], commands.Calls);
    }

    [Fact]
    public async Task Without_previews_the_buttons_carry_no_preview_anchor()
    {
        string html = await RenderAsync(previews: null);

        Assert.DoesNotContain("data-taskbar-window-id", html, StringComparison.Ordinal);
        Assert.DoesNotContain("taskbar-preview", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task With_previews_each_button_names_its_window_and_no_preview_is_open_yet()
    {
        string html = await RenderAsync(new RecordingPreviews());

        Assert.Contains($"data-taskbar-window-id=\"{Notes}\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("class=\"taskbar-preview\"", html, StringComparison.Ordinal);
    }

    private static TaskbarWindowItem Item(WindowId id, bool isFocused = true, bool isMinimized = false) =>
        new(id, "Window", null, isFocused, isMinimized);

    private static async Task<string> RenderAsync(ITaskbarWindowPreviews? previews)
    {
        ServiceCollection services = new();
        services.AddSingleton<IIconCatalog, IconCatalog>();
        services.AddSingleton<Microsoft.JSInterop.IJSRuntime, NoJsRuntime>();
        await using ServiceProvider provider = services.BuildServiceProvider();
        await using HtmlRenderer renderer = new(provider, NullLoggerFactory.Instance);
        Dictionary<string, object?> parameters = new()
        {
            [nameof(Taskbar.WindowSource)] = new OneWindow(),
            [nameof(Taskbar.Previews)] = previews,
        };

        string html = await renderer.Dispatcher.InvokeAsync(async () =>
            (await renderer.RenderComponentAsync<Taskbar>(ParameterView.FromDictionary(parameters))).ToHtmlString());
        return WebUtility.HtmlDecode(html);
    }

    private sealed class Fixture
    {
        public Fixture()
        {
            Controller = new TaskbarPreviewController(Previews, Scheduler.Schedule, () => Changes++);
        }

        public RecordingPreviews Previews { get; } = new();

        public ManualScheduler Scheduler { get; } = new();

        public TaskbarPreviewController Controller { get; }

        public int Changes { get; private set; }

        public static Fixture Showing(WindowId id)
        {
            Fixture fixture = new();
            fixture.Controller.EnterItem(id, "mouse");
            fixture.Scheduler.RunNext();
            return fixture;
        }
    }

    /// <summary>Runs the scheduled preview steps when the test says so, in order.</summary>
    private sealed class ManualScheduler
    {
        private readonly List<Step> _steps = [];

        public bool HasPending => _steps.Any(step => !step.IsCancelled);

        public TimeSpan NextDelay => _steps.First(step => !step.IsCancelled).Delay;

        public IDisposable Schedule(TimeSpan delay, Action action)
        {
            Step step = new(delay, action);
            _steps.Add(step);
            return step;
        }

        public void RunNext()
        {
            Step step = _steps.First(candidate => !candidate.IsCancelled);
            _steps.Remove(step);
            step.Action();
        }

        public void RunAll()
        {
            while (HasPending)
            {
                RunNext();
            }

            _steps.Clear();
        }

        private sealed class Step(TimeSpan delay, Action action) : IDisposable
        {
            public TimeSpan Delay { get; } = delay;

            public Action Action { get; } = action;

            public bool IsCancelled { get; private set; }

            public void Dispose() => IsCancelled = true;
        }
    }

    private sealed class RecordingPreviews : ITaskbarWindowPreviews
    {
        public List<WindowId?> Peeks { get; } = [];

        public string? LiveCopySelector(WindowId id) => $"[data-window-id=\"{id}\"]";

        public void Peek(WindowId? id) => Peeks.Add(id);
    }

    private sealed class Commands : ITaskbarCommandDispatcher
    {
        public List<string> Calls { get; } = [];

        public void Activate(WindowId id) => Calls.Add($"activate:{id}");

        public void Minimize(WindowId id) => Calls.Add($"minimize:{id}");

        public void Restore(WindowId id) => Calls.Add($"restore:{id}");

        public void Close(WindowId id) => Calls.Add($"close:{id}");
    }

    private sealed class OneWindow : ITaskbarWindowSource
    {
        public IReadOnlyList<TaskbarWindowItem> Windows { get; } =
            [new TaskbarWindowItem(Notes, "Notes", null, IsFocused: true, IsMinimized: false)];

        public event Action? Changed
        {
            add { }
            remove { }
        }
    }

    private sealed class NoJsRuntime : Microsoft.JSInterop.IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => throw new NotSupportedException();

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) =>
            throw new NotSupportedException();
    }
}
