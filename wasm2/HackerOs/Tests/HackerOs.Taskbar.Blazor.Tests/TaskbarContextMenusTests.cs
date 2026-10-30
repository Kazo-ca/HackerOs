using System.Net;
using HackerOs.AppSdk.Icons;
using HackerOs.Windowing.Abstractions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace HackerOs.Taskbar.Blazor.Tests;

/// <summary>
/// ADR 0194 §1: the taskbar sets the host's context menu attributes on itself and on each window
/// button, without reading them, and sets none without a host.
/// </summary>
public sealed class TaskbarContextMenusTests
{
    private static readonly WindowId Notes = WindowId.FromGuid(Guid.Parse("91000000-0000-0000-0000-000000000001"));

    [Fact]
    public async Task The_host_attributes_go_on_the_taskbar_and_on_each_window_button()
    {
        string html = await RenderAsync(new Menus());

        Assert.Contains("<footer class=\"taskbar-container\" role=\"contentinfo\" aria-label=\"Desktop taskbar\" data-test-menu=\"taskbar\"", html, StringComparison.Ordinal);
        Assert.Contains($"data-test-menu=\"window:{Notes.Value:N}\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_a_host_the_taskbar_sets_no_menu_attribute()
    {
        string html = await RenderAsync(contextMenus: null);

        Assert.DoesNotContain("data-test-menu", html, StringComparison.Ordinal);
        Assert.Contains("aria-label=\"Notes (focused)\"", html, StringComparison.Ordinal);
    }

    private static async Task<string> RenderAsync(ITaskbarContextMenus? contextMenus)
    {
        ServiceCollection services = new();
        services.AddSingleton<IIconCatalog, IconCatalog>();
        await using ServiceProvider provider = services.BuildServiceProvider();
        await using HtmlRenderer renderer = new(provider, NullLoggerFactory.Instance);
        Dictionary<string, object?> parameters = new()
        {
            [nameof(Taskbar.WindowSource)] = new OneWindow(),
            [nameof(Taskbar.ContextMenus)] = contextMenus,
        };

        string html = await renderer.Dispatcher.InvokeAsync(async () =>
            (await renderer.RenderComponentAsync<Taskbar>(ParameterView.FromDictionary(parameters))).ToHtmlString());
        return WebUtility.HtmlDecode(html);
    }

    private sealed class Menus : ITaskbarContextMenus
    {
        public IReadOnlyDictionary<string, object>? TaskbarAttributes { get; } =
            new Dictionary<string, object> { ["data-test-menu"] = "taskbar" };

        public IReadOnlyDictionary<string, object>? WindowItemAttributes(WindowId id) =>
            new Dictionary<string, object> { ["data-test-menu"] = $"window:{id.Value:N}" };
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
}
