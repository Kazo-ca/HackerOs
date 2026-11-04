using System.Text.Json;
using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk.Blazor.Display;
using Kazo.Os.Platform.Blazor.Display;
using Kazo.Os.Platform.Core.Notifications;
using Kazo.Os.Simulation.Abstractions.Notifications;
using Kazo.Os.Simulation.Abstractions.Sessions;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Kazo.Os.Platform.Blazor.Tests.Display;

/// <summary>
/// <see cref="DisplaySurfaceService"/> (ADR 0078 §2): the C# side of detached windows, against a
/// scripted module. The window itself is covered by the UI E2E test of the Browser.
/// </summary>
public sealed class DisplaySurfaceServiceTests
{
    [Fact]
    public async Task Opening_a_surface_reports_it_open_with_the_window_size()
    {
        FakeModule module = new() { OpenReply = """{"status":"opened","width":900,"height":600}""" };
        DisplaySurfaceService service = new(new FakeJsRuntime(module));
        int changes = 0;
        service.SurfacesChanged += () => changes++;

        DisplaySurfaceOpenResult result = await service.OpenAsync(new DisplaySurfaceOptions("Page", 1000, 700));

        Assert.Equal(DisplaySurfaceOpenStatus.Opened, result.Status);
        IDisplaySurface surface = Assert.IsAssignableFrom<IDisplaySurface>(result.Surface);
        Assert.Equal("surface-1", surface.Id);
        Assert.Equal(DisplaySurfaceState.Open, surface.State);
        Assert.Equal((900, 600), (surface.Width, surface.Height));
        Assert.Equal("Page", surface.Title);
        Assert.Same(surface, Assert.Single(service.Surfaces));
        Assert.Equal(1, changes);
        (string identifier, object?[] arguments) = Assert.Single(module.Calls);
        Assert.Equal("open", identifier);
        Assert.Equal(["surface-1", "Page", 1000, 700], arguments.Skip(1));
    }

    [Fact]
    public async Task A_blocked_window_posts_a_notification_to_the_user()
    {
        FakeModule module = new() { OpenReply = """{"status":"blocked","width":0,"height":0}""" };
        InMemoryNotificationQueue notifications = new(10);
        AuthenticatedPrincipal principal = CreatePrincipal();
        DisplaySurfaceService service = new(new FakeJsRuntime(module), notifications, new StubSessionService(principal));

        DisplaySurfaceOpenResult result = await service.OpenAsync(new DisplaySurfaceOptions("Page"));

        Assert.Equal(DisplaySurfaceOpenStatus.Blocked, result.Status);
        Assert.Null(result.Surface);
        Assert.Empty(service.Surfaces);
        Notification notification = Assert.Single(notifications.GetActive(principal.UserId, DateTimeOffset.UtcNow));
        Assert.Equal(NotificationSeverity.Warning, notification.Severity);
        Assert.Equal(DisplaySurfaceService.NotificationSource, notification.SourceAppId);
        Assert.Contains("pop-ups", notification.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_host_without_javascript_cannot_open_surfaces()
    {
        DisplaySurfaceService service = new(new FakeJsRuntime(null));

        DisplaySurfaceOpenResult result = await service.OpenAsync(new DisplaySurfaceOptions("Page"));

        Assert.Equal(DisplaySurfaceOpenStatus.Unavailable, result.Status);
    }

    [Fact]
    public async Task Window_events_update_the_surface_and_a_user_close_ends_it()
    {
        FakeModule module = new();
        DisplaySurfaceService service = new(new FakeJsRuntime(module));
        IDisplaySurface surface = (await service.OpenAsync(new DisplaySurfaceOptions("Page"))).Surface!;
        List<DisplaySurfaceState> seen = [];
        surface.Changed += changed => seen.Add(changed.State);

        service.OnSurfaceEvent(surface.Id, "resize", 640, 480);
        service.OnSurfaceEvent(surface.Id, "blur", 640, 480);
        Assert.Equal((640, 480, false), (surface.Width, surface.Height, surface.HasFocus));

        service.OnSurfaceEvent(surface.Id, "closed", 0, 0);

        Assert.Equal(DisplaySurfaceState.ClosedByUser, surface.State);
        Assert.Empty(service.Surfaces);
        Assert.Equal([DisplaySurfaceState.Open, DisplaySurfaceState.Open, DisplaySurfaceState.ClosedByUser], seen);

        // A late event for an ended surface changes nothing.
        service.OnSurfaceEvent(surface.Id, "lost", 0, 0);
        Assert.Equal(DisplaySurfaceState.ClosedByUser, surface.State);
    }

    [Fact]
    public async Task Closing_from_the_os_ends_the_surface_before_closing_the_window()
    {
        FakeModule module = new();
        DisplaySurfaceService service = new(new FakeJsRuntime(module));
        IDisplaySurface first = (await service.OpenAsync(new DisplaySurfaceOptions("One"))).Surface!;
        IDisplaySurface second = (await service.OpenAsync(new DisplaySurfaceOptions("Two"))).Surface!;
        Assert.Equal("surface-2", second.Id);

        await first.CloseAsync();
        Assert.Equal(DisplaySurfaceState.Closed, first.State);
        Assert.Same(second, Assert.Single(service.Surfaces));

        await service.CloseAllAsync();
        Assert.Equal(DisplaySurfaceState.Closed, second.State);
        Assert.Equal(["surface-1", "surface-2"], module.Calls.Where(call => call.Identifier == "close").Select(call => call.Arguments[0]));

        // Calls on an ended surface reach no window.
        int calls = module.Calls.Count;
        await first.SetTitleAsync("Ignored");
        await first.MountAsync(default);
        await first.CloseAsync();
        Assert.Equal(calls, module.Calls.Count);
    }

    [Fact]
    public async Task Title_mount_and_focus_are_sent_to_the_window()
    {
        FakeModule module = new();
        DisplaySurfaceService service = new(new FakeJsRuntime(module));
        IDisplaySurface surface = (await service.OpenAsync(new DisplaySurfaceOptions("Page"))).Surface!;
        int changes = 0;
        surface.Changed += _ => changes++;

        await surface.SetTitleAsync("New title");
        await surface.SetTitleAsync("New title");
        await surface.MountAsync(default(ElementReference));
        await surface.UnmountAsync(default(ElementReference));
        await surface.FocusAsync();

        Assert.Equal("New title", surface.Title);
        Assert.Equal(1, changes);
        Assert.Equal(["open", "setTitle", "mount", "unmount", "focus"], module.Calls.Select(call => call.Identifier));
    }

    private static AuthenticatedPrincipal CreatePrincipal()
    {
        LocalGroupId primaryGroup = LocalGroupId.FromGuid(Guid.NewGuid());
        return new AuthenticatedPrincipal(
            SessionId.FromGuid(Guid.NewGuid()),
            LocalUserId.FromGuid(Guid.NewGuid()),
            LocalLoginName.Parse("alice"),
            "Alice",
            AppAuthority.User,
            primaryGroup,
            [primaryGroup],
            InstallationId.FromGuid(Guid.NewGuid()),
            DeviceId.FromGuid(Guid.NewGuid()),
            DateTimeOffset.UnixEpoch);
    }

    /// <summary>A runtime whose only module is <paramref name="module"/>; <see langword="null"/> mimics prerendering.</summary>
    private sealed class FakeJsRuntime(FakeModule? module) : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            if (module is null)
            {
                throw new InvalidOperationException("JavaScript interop calls cannot be issued at this time.");
            }

            Assert.Equal("import", identifier);
            return ValueTask.FromResult((TValue)(object)module);
        }
    }

    private sealed class FakeModule : IJSObjectReference
    {
        public string OpenReply { get; init; } = """{"status":"opened","width":1024,"height":768}""";

        public List<(string Identifier, object?[] Arguments)> Calls { get; } = [];

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            Calls.Add((identifier, args ?? []));
            string json = identifier == "open" ? OpenReply : "null";
            return ValueTask.FromResult(JsonSerializer.Deserialize<TValue>(json)!);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class StubSessionService(AuthenticatedPrincipal principal) : ISessionService
    {
        public SessionState State => SessionState.Active;

        public AuthenticatedPrincipal? CurrentPrincipal => principal;

        public Task<AuthenticatedPrincipal> LoginAsync(
            LocalLoginName loginName, string? password, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task LogoutAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task ShutdownAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public CancellationTokenSource CreateLinkedCancellationSource() => new();
    }
}
