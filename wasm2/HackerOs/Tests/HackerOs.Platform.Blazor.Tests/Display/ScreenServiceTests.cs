using System.Text.Json;
using HackerOs.AppSdk.Blazor.Display;
using HackerOs.Platform.Blazor.Display;
using HackerOs.Platform.Core.Settings;
using HackerOs.Platform.Core.Shell;
using HackerOs.Simulation.Abstractions;
using HackerOs.Simulation.Abstractions.Gateways;
using HackerOs.Simulation.Abstractions.Settings;
using HackerOs.Windowing.Abstractions;
using HackerOs.Windowing.Core;
using Microsoft.JSInterop;

namespace HackerOs.Platform.Blazor.Tests.Display;

/// <summary>
/// <see cref="ScreenService"/> (ADR 0178 §4, ADR 0180): secondary screens registered in the window
/// manager, remembered in <c>screens.config</c>, against scripted modules. The windows themselves
/// are covered by the UI E2E tests.
/// </summary>
public sealed class ScreenServiceTests
{
    [Fact]
    public async Task A_new_screen_is_a_surface_registered_as_display_2_with_its_size()
    {
        Harness harness = new();
        harness.Surfaces.OpenReply = """{"status":"opened","width":1900,"height":1000,"monitor":"DELL U2720Q"}""";
        await harness.Service.StartAsync();

        ScreenOpenResult result = await harness.Service.OpenScreenAsync();

        Assert.Equal(ScreenOpenStatus.Opened, result.Status);
        SecondaryScreen screen = Assert.IsType<SecondaryScreen>(result.Screen);
        Assert.Equal(DisplayId.FromNumber(2), screen.Id);
        Assert.Equal("DELL U2720Q", screen.MonitorLabel);
        Assert.Same(screen, Assert.Single(harness.Service.Screens));
        Assert.Equal([DisplayId.Primary, DisplayId.FromNumber(2)], harness.Runtime.Displays);
        Assert.Equal(new WindowBounds(0, 0, 1900, 1000), harness.Runtime.GetWorkArea(screen.Id));

        (string identifier, object?[] arguments) = Assert.Single(harness.Surfaces.Calls, call => call.Identifier == "open");
        Assert.Equal("HackerOS — Screen 2", arguments[2]);
        Assert.Equal(6, arguments.Length);
        Assert.Equal("open = 2", harness.Settings.Line("screens.open"));
        Assert.Equal(ScreenPermission.Granted, harness.Service.Permission);
    }

    [Fact]
    public async Task A_resized_window_leaves_the_work_area_to_the_screen_desktop()
    {
        Harness harness = new();
        SecondaryScreen screen = (await harness.Service.OpenScreenAsync()).Screen!;
        WindowBounds opened = harness.Runtime.GetWorkArea(screen.Id);

        harness.SurfaceService.OnSurfaceEvent(screen.Surface.Id, "resize", 800, 600);

        // ADR 0178 §1: the screen's DesktopArea measures its work area, without the screen's bars.
        Assert.Equal(opened, harness.Runtime.GetWorkArea(screen.Id));
        Assert.Equal(new WindowBounds(0, 0, 1280, 720), harness.Runtime.WorkArea);
    }

    [Fact]
    public async Task A_screen_closed_by_the_user_brings_its_windows_back_and_frees_its_number()
    {
        Harness harness = new();
        SecondaryScreen second = (await harness.Service.OpenScreenAsync()).Screen!;
        SecondaryScreen third = (await harness.Service.OpenScreenAsync()).Screen!;
        Assert.Equal(3, third.Id.Number);
        WindowId window = WindowId.FromGuid(Guid.NewGuid());
        harness.Runtime.Apply(new CreateWindowCommand(new WindowRuntimeState(
            window,
            "org.hackeros.notes",
            WindowOwnerId.FromGuid(Guid.NewGuid()),
            "Notes",
            null,
            new WindowBounds(10, 10, 400, 300),
            null,
            0,
            WindowVisualState.Normal,
            new WindowConstraints(true, 320, 240, 2000, 1200),
            WindowModality.Modeless,
            null)));
        harness.Runtime.Apply(new MoveWindowToDisplayCommand(window, second.Id));

        harness.SurfaceService.OnSurfaceEvent(second.Surface.Id, "closed", 0, 0);

        Assert.Same(third, Assert.Single(harness.Service.Screens));
        Assert.Equal([DisplayId.Primary, third.Id], harness.Runtime.Displays);
        Assert.Equal(DisplayId.Primary, harness.Runtime.Windows.Single(state => state.Id == window).DisplayId);
        await harness.Settings.WaitForWritesAsync(3);
        Assert.Equal("open = 3", harness.Settings.Line("screens.open"));
        Assert.Equal(2, (await harness.Service.OpenScreenAsync()).Screen!.Id.Number);
    }

    [Fact]
    public async Task A_screen_closed_by_the_os_stays_offered_for_the_next_session()
    {
        Harness harness = new();
        await harness.Service.OpenScreenAsync();
        int writes = harness.Settings.Writes;

        await harness.SurfaceService.CloseAllAsync();

        Assert.Empty(harness.Service.Screens);
        Assert.Equal([DisplayId.Primary], harness.Runtime.Displays);
        Assert.Equal(writes, harness.Settings.Writes);
        Assert.Equal("open = 2", harness.Settings.Line("screens.open"));
    }

    [Fact]
    public async Task An_unplugged_monitor_closes_its_screen_and_forgets_it()
    {
        Harness harness = new();
        SecondaryScreen screen = (await harness.Service.OpenScreenAsync()).Screen!;

        await harness.Service.OnScreenMonitorLost(screen.Surface.Id);

        Assert.Empty(harness.Service.Screens);
        Assert.Equal(DisplaySurfaceState.Closed, screen.Surface.State);
        await harness.Settings.WaitForWritesAsync(2);
        Assert.Equal("open = ", harness.Settings.Line("screens.open"));
    }

    [Fact]
    public async Task Restoring_reopens_the_last_session_in_order_at_the_remembered_place()
    {
        Harness harness = new();
        harness.Settings.Values["screens.open"] = "3,2";
        harness.Settings.Values["screens.3"] = "-1280,0,1280,1024";
        await harness.Service.StartAsync();
        Assert.Equal([DisplayId.FromNumber(3), DisplayId.FromNumber(2)], harness.Service.RestorableScreens);

        int opened = await harness.Service.RestoreScreensAsync();

        Assert.Equal(2, opened);
        Assert.Empty(harness.Service.RestorableScreens);
        Assert.Equal([2, 3], harness.Service.Screens.Select(screen => screen.Id.Number));
        object?[] first = harness.Surfaces.Calls.First(call => call.Identifier == "open").Arguments;
        Assert.Equal(1280, first[3]);
        Assert.Equal(1024, first[4]);
        Assert.Equal(
            """{"remembered":{"left":-1280,"top":0,"width":1280,"height":1024}}""",
            JsonSerializer.Serialize(first[5]));
        Assert.Equal("open = 2,3", harness.Settings.Line("screens.open"));
    }

    [Fact]
    public async Task A_blocked_window_keeps_the_rest_offered_for_the_next_click()
    {
        Harness harness = new();
        harness.Settings.Values["screens.open"] = "2,3";
        await harness.Service.StartAsync();
        harness.Surfaces.OpenReplies.Enqueue("""{"status":"opened","width":1024,"height":768}""");
        harness.Surfaces.OpenReplies.Enqueue("""{"status":"blocked","width":0,"height":0}""");

        Assert.Equal(1, await harness.Service.RestoreScreensAsync());
        Assert.Equal([DisplayId.FromNumber(3)], harness.Service.RestorableScreens);

        Assert.Equal(1, await harness.Service.RestoreScreensAsync());
        Assert.Empty(harness.Service.RestorableScreens);
        Assert.Equal([2, 3], harness.Service.Screens.Select(screen => screen.Id.Number));
    }

    [Fact]
    public async Task Dismissing_or_opening_by_hand_drops_the_offer()
    {
        Harness harness = new();
        harness.Settings.Values["screens.open"] = "2";
        await harness.Service.StartAsync();

        await harness.Service.DismissRestoreAsync();

        Assert.Empty(harness.Service.RestorableScreens);
        Assert.Equal("open = ", harness.Settings.Line("screens.open"));

        Harness other = new();
        other.Settings.Values["screens.open"] = "2";
        await other.Service.StartAsync();
        await other.Service.OpenScreenAsync();
        Assert.Empty(other.Service.RestorableScreens);
    }

    [Fact]
    public async Task A_moved_window_is_remembered_after_a_pause()
    {
        Harness harness = new();
        harness.Service.GeometrySaveDelay = TimeSpan.Zero;
        SecondaryScreen screen = (await harness.Service.OpenScreenAsync()).Screen!;
        int writes = harness.Settings.Writes;

        harness.Service.OnScreenGeometry(screen.Surface.Id, 1920, 0, 1920, 1080);
        harness.Service.OnScreenGeometry(screen.Surface.Id, 1920, 0, 100, 100);

        await harness.Settings.WaitForWritesAsync(writes + 1);
        Assert.Equal(new ScreenGeometry(1920, 0, 1920, 1080), screen.Geometry);
        Assert.Equal("1920,0,1920,1080", harness.Settings.Values["screens.2"]);
    }

    [Fact]
    public async Task Visibility_full_screen_and_placement_reports_update_the_screen()
    {
        Harness harness = new();
        await harness.Service.StartAsync();
        SecondaryScreen screen = (await harness.Service.OpenScreenAsync()).Screen!;
        int changes = 0;
        harness.Service.Changed += () => changes++;

        harness.Service.OnPrimaryVisibility(true);
        harness.Service.OnScreenFullScreen(screen.Surface.Id, true);
        harness.Service.OnScreenPlaced(screen.Surface.Id, "LG");
        harness.Service.OnPermission("denied");
        harness.Service.OnPrimaryVisibility(true);

        Assert.True(harness.Service.IsPrimaryHidden);
        Assert.True(screen.IsFullScreen);
        Assert.Equal("LG", screen.MonitorLabel);
        Assert.Equal(ScreenPermission.Denied, harness.Service.Permission);
        Assert.Equal(4, changes);
        Assert.True(await harness.Service.ToggleFullScreenAsync(screen.Id));
        Assert.Contains(harness.Screens.Calls, call => call.Identifier == "toggleFullScreen" && Equals(call.Arguments[0], screen.Surface.Id));
    }

    [Fact]
    public async Task Without_javascript_no_screen_opens()
    {
        WindowRuntime runtime = new(new WindowBounds(0, 0, 1280, 720));
        FakeJsRuntime js = new(null, null);
        ScreenService service = new(new DisplaySurfaceService(js), js, runtime);

        await service.StartAsync();
        ScreenOpenResult result = await service.OpenScreenAsync();

        Assert.Equal(ScreenPermission.Unsupported, service.Permission);
        Assert.Equal(ScreenOpenStatus.Unavailable, result.Status);
        Assert.Equal([DisplayId.Primary], runtime.Displays);
    }

    private sealed class Harness
    {
        public Harness()
        {
            Runtime = new WindowRuntime(new WindowBounds(0, 0, 1280, 720));
            FakeJsRuntime js = new(Surfaces, Screens);
            SurfaceService = new DisplaySurfaceService(js);
            Service = new ScreenService(SurfaceService, js, Runtime, Settings);
        }

        public FakeModule Surfaces { get; } = new();

        public FakeModule Screens { get; } = new();

        public FakeSettings Settings { get; } = new();

        public WindowRuntime Runtime { get; }

        public DisplaySurfaceService SurfaceService { get; }

        public ScreenService Service { get; }
    }

    private sealed class FakeJsRuntime(FakeModule? surfaces, FakeModule? screens) : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            if (surfaces is null || screens is null)
            {
                throw new InvalidOperationException("JavaScript interop calls cannot be issued at this time.");
            }

            Assert.Equal("import", identifier);
            string path = (string)args![0]!;
            return ValueTask.FromResult((TValue)(object)(path.EndsWith("screens.js", StringComparison.Ordinal) ? screens : surfaces));
        }
    }

    private sealed class FakeModule : IJSObjectReference
    {
        public string OpenReply { get; set; } = """{"status":"opened","width":1024,"height":768}""";

        public Queue<string> OpenReplies { get; } = new();

        public List<(string Identifier, object?[] Arguments)> Calls { get; } = [];

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            Calls.Add((identifier, args ?? []));
            string json = identifier switch
            {
                "open" => OpenReplies.Count > 0 ? OpenReplies.Dequeue() : OpenReply,
                "start" => """{"permission":"granted","hidden":false}""",
                "toggleFullScreen" => "true",
                _ => "null",
            };
            return ValueTask.FromResult(JsonSerializer.Deserialize<TValue>(json)!);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>The layered part of <see cref="IOsSettingsService"/> over one dictionary of qualified keys.</summary>
    private sealed class FakeSettings : IOsSettingsService
    {
        public Dictionary<string, string> Values { get; } = new(StringComparer.Ordinal);

        public int Writes { get; private set; }

        public event EventHandler<OsSettingsChangedEventArgs>? DocumentChanged { add { } remove { } }

        public string? CurrentHomeDirectory => "/home/alice";

        public string Line(string key) => $"{key.Split('.')[1]} = {Values.GetValueOrDefault(key)}";

        public async Task WaitForWritesAsync(int count)
        {
            for (int attempt = 0; attempt < 200 && Writes < count; attempt++)
            {
                await Task.Delay(10);
            }

            Assert.True(Writes >= count, $"Expected {count} writes, saw {Writes}.");
        }

        public ValueTask<LayeredConfigSnapshot> ReadAsync(OsSettingsDocument document, CancellationToken cancellationToken = default)
        {
            Assert.Equal(ScreensSettingsDocuments.DocumentId, document.DocumentId);
            return ValueTask.FromResult(new LayeredConfigSnapshot(
                new Dictionary<string, string>(Values, StringComparer.Ordinal),
                new Dictionary<string, SettingLayer>(StringComparer.Ordinal),
                new HashSet<string>(StringComparer.Ordinal),
                []));
        }

        public ValueTask SetManyAsync(OsSettingsDocument document, IReadOnlyList<KeyValuePair<string, string>> values, SettingLayer target = SettingLayer.User, IAppFileSystemGateway? writer = null, CancellationToken cancellationToken = default)
        {
            Assert.Equal(SettingLayer.User, target);
            foreach ((string key, string value) in values)
            {
                Assert.True(document.Schema.Fields.ContainsKey(key), key);
                Values[key] = value;
            }

            Writes++;
            return ValueTask.CompletedTask;
        }

        public ValueTask SetAsync(OsSettingsDocument document, string qualifiedKey, string value, SettingLayer target = SettingLayer.User, IAppFileSystemGateway? writer = null, CancellationToken cancellationToken = default) =>
            SetManyAsync(document, [new(qualifiedKey, value)], target, writer, cancellationToken);

        public ValueTask ResetAsync(OsSettingsDocument document, string qualifiedKey, SettingLayer target = SettingLayer.User, IAppFileSystemGateway? writer = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<string?> ReadFileAsync(OsSettingsFile file, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<string> ReadValidatedFileAsync(OsSettingsFile file, ISettingsDocumentValidator validator, string defaultContent, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask WriteFileAsync(OsSettingsFile file, string content, IAppFileSystemGateway? writer = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask WatchAsync(OsSettingsDocument document, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
