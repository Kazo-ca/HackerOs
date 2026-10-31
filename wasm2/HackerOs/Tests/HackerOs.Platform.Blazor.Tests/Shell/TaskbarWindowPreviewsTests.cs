using System.Net;
using HackerOs.Platform.Blazor.Shell;
using HackerOs.Windowing.Abstractions;
using HackerOs.Windowing.Blazor;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;

namespace HackerOs.Platform.Blazor.Tests.Shell;

/// <summary>
/// ADR 0199 §4-5: the desktop's previews copy a window from its element, and the peek shows the
/// peeked window (and its dialogs) alone on its screen, the other windows of that screen see-through.
/// </summary>
public sealed class TaskbarWindowPreviewsTests
{
    private static readonly WindowId Notes = WindowId.FromGuid(Guid.Parse("93000000-0000-0000-0000-000000000001"));
    private static readonly WindowId NotesDialog = WindowId.FromGuid(Guid.Parse("93000000-0000-0000-0000-000000000002"));
    private static readonly WindowId Terminal = WindowId.FromGuid(Guid.Parse("93000000-0000-0000-0000-000000000003"));
    private static readonly WindowId OtherScreen = WindowId.FromGuid(Guid.Parse("93000000-0000-0000-0000-000000000004"));

    [Fact]
    public void The_live_copy_is_taken_from_the_window_element()
    {
        Assert.Equal($"[data-window-id=\"{Notes}\"]", new TaskbarWindowPreviews().LiveCopySelector(Notes));
    }

    [Fact]
    public void A_peek_is_announced_once_per_change()
    {
        TaskbarWindowPreviews previews = new();
        int changes = 0;
        previews.Changed += () => changes++;

        previews.Peek(Notes);
        previews.Peek(Notes);
        previews.Peek(null);

        Assert.Null(previews.PeekWindowId);
        Assert.Equal(2, changes);
    }

    [Fact]
    public async Task Without_a_peek_no_window_changes()
    {
        string html = await RenderAsync(peek: null);

        Assert.DoesNotContain("is-peek-", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_peeked_window_and_its_dialog_show_and_the_others_of_its_screen_turn_see_through()
    {
        string html = await RenderAsync(Notes);

        Assert.Equal("is-peek-shown", PeekClassOf(html, Notes));
        Assert.Equal("is-peek-shown", PeekClassOf(html, NotesDialog));
        Assert.Equal("is-peek-see-through", PeekClassOf(html, Terminal));
        Assert.Null(PeekClassOf(html, OtherScreen));
    }

    private static string? PeekClassOf(string html, WindowId id)
    {
        int window = html.IndexOf($"data-window-id=\"{id}\"", StringComparison.Ordinal);
        int start = html.LastIndexOf("<article class=\"", window, StringComparison.Ordinal);
        string classes = html[(start + "<article class=\"".Length)..html.IndexOf('"', start + "<article class=\"".Length)];
        return classes.Split(' ').SingleOrDefault(name => name.StartsWith("is-peek-", StringComparison.Ordinal));
    }

    private static async Task<string> RenderAsync(WindowId? peek)
    {
        ServiceCollection services = new();
        services.AddSingleton<IJSRuntime, NoJsRuntime>();
        await using ServiceProvider provider = services.BuildServiceProvider();
        await using HtmlRenderer renderer = new(provider, NullLoggerFactory.Instance);
        IReadOnlyList<WindowRuntimeState> windows =
        [
            Window(Notes, 1),
            Window(NotesDialog, 2, owner: Notes),
            Window(Terminal, 3),
            Window(OtherScreen, 4) with { DisplayId = DisplayId.FromNumber(2) },
        ];

        string html = await renderer.Dispatcher.InvokeAsync(async () =>
            (await renderer.RenderComponentAsync<DesktopArea>(ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(DesktopArea.Windows)] = windows,
                // With shared layers, the area of screen 1 renders the windows of every screen.
                [nameof(DesktopArea.Layers)] = new WindowLayers(),
                [nameof(DesktopArea.PeekWindowId)] = peek,
            }))).ToHtmlString());
        return WebUtility.HtmlDecode(html);
    }

    private static WindowRuntimeState Window(WindowId id, int zOrder, WindowId? owner = null) => new(
        id, "org.hackeros.test", WindowOwnerId.FromGuid(Guid.NewGuid()),
        "Test window", null, new WindowBounds(40, 40, 640, 480), null, zOrder,
        WindowVisualState.Normal, new WindowConstraints(true, 320, 240), ownerId: owner);

    private sealed class NoJsRuntime : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => throw new NotSupportedException();

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) =>
            throw new NotSupportedException();
    }
}
