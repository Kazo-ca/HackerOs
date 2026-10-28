using System.Net;
using HackerOs.App.Abstractions;
using HackerOs.App.Abstractions.Policy;
using HackerOs.AppSdk;
using HackerOs.AppSdk.Administration;
using HackerOs.AppSdk.Settings;
using HackerOs.Platform.Core.Settings;
using HackerOs.Platform.Core.Shell;
using HackerOs.Simulation.Abstractions.Gateways;
using HackerOs.Simulation.Abstractions.Settings;
using HackerOs.Tests.Support;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.Web.HtmlRendering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace HackerOs.Apps.Settings.Tests;

/// <summary>
/// Settings &gt; Screens (ADR 0179 §7): <c>primary-taskbar</c> written to the user layer of
/// <c>screens.config</c>, the remembered screens, forgetting the place of a closed one, and a change
/// made elsewhere shown at once. Rendered with the real <see cref="HtmlRenderer"/> over an in-memory
/// OS settings gateway that carries the OS's own schema.
/// </summary>
public sealed class ScreensPanelTests
{
    [Fact]
    public void The_panel_keys_are_the_keys_the_OS_declares()
    {
        Assert.Equal(ScreensSettingsDocuments.DocumentId, ScreensPanel.DocumentId);
        Assert.Equal(ScreensPanel.PrimaryTaskbarKey, ScreensSettingsDocuments.PrimaryTaskbarQualifiedKey);
        Assert.Equal(ScreensPanel.OpenKey, ScreensSettingsDocuments.OpenQualifiedKey);
        Assert.Equal(ScreensSettingsDocuments.PrimaryTaskbarAll, ScreensPanel.PrimaryTaskbarAll);
        Assert.Equal(ScreensSettingsDocuments.PrimaryTaskbarOwn, ScreensPanel.PrimaryTaskbarOwn);
        Assert.Equal(ScreensSettingsDocuments.MaxScreenNumber, ScreensPanel.MaxScreenNumber);
        Assert.True(OsSettingsDocumentRegistry.TryGet(ScreensPanel.DocumentId, out _));
    }

    [Fact]
    public async Task Choosing_own_writes_primary_taskbar_to_the_user_layer()
    {
        FakeSystemSettings settings = new();
        await using PanelHarness harness = await PanelHarness.RenderAsync(settings);
        Assert.Equal("all", harness.Panel.PrimaryTaskbar);

        await harness.RunAsync(panel => panel.SetPrimaryTaskbarAsync("own"));

        Assert.Equal("own", settings.User[ScreensSettingsDocuments.PrimaryTaskbarQualifiedKey]);
        Assert.Equal("own", harness.Panel.PrimaryTaskbar);
        Assert.False(harness.Panel.MessageIsError);
        Assert.Contains("Saved.", harness.Html(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_list_shows_screen_1_and_each_remembered_screen_with_its_state_and_place()
    {
        FakeSystemSettings settings = new();
        settings.User["screens.open"] = "3, 2, 12";
        settings.User["screens.2"] = "1920,0,1920,1080";
        settings.User["screens.4"] = "-1280,0,1280,1024";
        settings.User["screens.5"] = "0,0,10,10";
        await using PanelHarness harness = await PanelHarness.RenderAsync(settings);

        Assert.Equal(
            [new RememberedScreen(2, true, new RememberedPlace(1920, 0, 1920, 1080)),
             new RememberedScreen(3, true, null),
             new RememberedScreen(4, false, new RememberedPlace(-1280, 0, 1280, 1024))],
            harness.Panel.Screens);
        string html = harness.Html();
        Assert.Contains("This browser tab", html, StringComparison.Ordinal);
        Assert.Contains("1920 × 1080 at (1920, 0)", html, StringComparison.Ordinal);
        Assert.Contains("Place not remembered yet", html, StringComparison.Ordinal);
        Assert.Contains("1280 × 1024 at (-1280, 0)", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Screen 5", html, StringComparison.Ordinal);
        // Only the closed screen 4 can be forgotten: the screen service owns the open ones.
        Assert.Single(harness.Panel.Screens, screen => screen.CanForget);
        Assert.Contains("Forget the place of screen 4", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Forget the place of screen 2", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Forgetting_a_closed_screen_removes_its_place_and_an_open_one_is_kept()
    {
        FakeSystemSettings settings = new();
        settings.User["screens.open"] = "2";
        settings.User["screens.2"] = "1920,0,1920,1080";
        settings.User["screens.3"] = "0,0,800,600";
        await using PanelHarness harness = await PanelHarness.RenderAsync(settings);

        await harness.RunAsync(panel => panel.ForgetAsync(2));
        await harness.RunAsync(panel => panel.ForgetAsync(3));

        Assert.Equal("1920,0,1920,1080", settings.User["screens.2"]);
        Assert.False(settings.User.ContainsKey("screens.3"));
        RememberedScreen only = Assert.Single(harness.Panel.Screens);
        Assert.Equal(2, only.Number);
    }

    [Fact]
    public async Task A_change_made_by_the_screen_service_shows_at_once()
    {
        FakeSystemSettings settings = new();
        await using PanelHarness harness = await PanelHarness.RenderAsync(settings);
        Assert.Contains("No secondary screen yet.", harness.Html(), StringComparison.Ordinal);

        settings.User["screens.open"] = "2";
        settings.User["taskbar.primary-taskbar"] = "OWN";
        await harness.RaiseChangedAsync(settings);

        Assert.Equal("own", harness.Panel.PrimaryTaskbar);
        Assert.Equal(2, Assert.Single(harness.Panel.Screens).Number);
        Assert.Contains("Open, or offered at the next sign-in", harness.Html(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task In_a_French_session_the_panel_speaks_French()
    {
        FakeSystemSettings settings = new();
        settings.User["screens.3"] = "0,0,800,600";
        await using PanelHarness harness = await PanelHarness.RenderAsync(settings, lang: "fr_CA.UTF-8");

        string html = harness.Html();
        Assert.Contains("Barre des tâches de l'écran 1", html, StringComparison.Ordinal);
        Assert.Contains("Seulement les fenêtres de l'écran 1", html, StringComparison.Ordinal);
        Assert.Contains("800 × 600 à (0, 0)", html, StringComparison.Ordinal);
        Assert.Contains("Autre écran ▸ Nouvel écran", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_screens_on_the_host_the_open_screens_block_is_hidden()
    {
        await using PanelHarness harness = await PanelHarness.RenderAsync(new FakeSystemSettings());

        Assert.Null(harness.Panel.Live);
        string html = harness.Html();
        Assert.DoesNotContain("Open screens", html, StringComparison.Ordinal);
        Assert.DoesNotContain("scr-identify", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_open_screens_are_listed_with_their_monitor_size_and_this_window()
    {
        FakeScreens screens = new()
        {
            State = new HackScreensState(
                [new HackScreenInfo(1, null, 1920, 1080, false), new HackScreenInfo(2, "DELL U2720Q", 2560, 1440, true)],
                HackScreenPermission.Granted, true, [], 2),
        };
        await using PanelHarness harness = await PanelHarness.RenderAsync(new FakeSystemSettings(), screens: screens);

        string html = harness.Html();
        Assert.Contains("Open screens", html, StringComparison.Ordinal);
        Assert.Contains("DELL U2720Q", html, StringComparison.Ordinal);
        Assert.Contains("2560 × 1440", html, StringComparison.Ordinal);
        Assert.Contains("This window", html, StringComparison.Ordinal);
        Assert.Contains("Close screen 2", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Close screen 1", html, StringComparison.Ordinal);
        // The caller's screen is in full screen, so the button takes it out.
        Assert.Contains("Leave full screen", html, StringComparison.Ordinal);
        Assert.Contains("Window management allowed", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Allow now", html, StringComparison.Ordinal);
        Assert.DoesNotContain("scr-restore", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Each_button_asks_the_screens_gateway_and_the_list_follows()
    {
        FakeScreens screens = new()
        {
            State = new HackScreensState([new HackScreenInfo(1, null, 1920, 1080, false)], HackScreenPermission.Prompt, true, [3], 1),
        };
        await using PanelHarness harness = await PanelHarness.RenderAsync(new FakeSystemSettings(), screens: screens);
        Assert.Contains("Restore screens (1)", harness.Html(), StringComparison.Ordinal);
        Assert.Contains("Allow now", harness.Html(), StringComparison.Ordinal);

        screens.OnOpen = () => screens.State = screens.State with
        {
            Screens = [.. screens.State.Screens, new HackScreenInfo(2, null, 1280, 1024, false)],
        };
        await harness.RunAsync(panel => panel.OpenScreenAsync());
        await harness.RunAsync(panel => panel.IdentifyAsync());
        await harness.RunAsync(panel => panel.CloseScreenAsync(2));
        await harness.RunAsync(panel => panel.RestoreScreensAsync());
        await harness.RunAsync(panel => panel.ToggleFullScreenAsync());
        await harness.RunAsync(panel => panel.RequestPermissionAsync());

        Assert.Equal(["open", "identify", "close 2", "restore", "fullscreen", "permission"], screens.Calls);
        Assert.Equal(2, harness.Panel.Live!.Screens.Count);
        Assert.Null(harness.Panel.Message);
    }

    [Fact]
    public async Task A_blocked_window_tells_how_to_allow_it()
    {
        FakeScreens screens = new()
        {
            State = new HackScreensState([new HackScreenInfo(1, null, 1920, 1080, false)], HackScreenPermission.Denied, true, [], 1),
            OpenStatus = HackScreenOpenStatus.Blocked,
            FullScreenResult = false,
        };
        await using PanelHarness harness = await PanelHarness.RenderAsync(new FakeSystemSettings(), screens: screens);

        await harness.RunAsync(panel => panel.OpenScreenAsync());
        Assert.True(harness.Panel.MessageIsError);
        Assert.Contains("Allow pop-ups for HackerOS", harness.Panel.Message, StringComparison.Ordinal);

        await harness.RunAsync(panel => panel.ToggleFullScreenAsync());
        Assert.Equal("The browser refused full screen.", harness.Panel.Message);
    }

    [Fact]
    public async Task A_screen_opened_elsewhere_shows_at_once()
    {
        FakeScreens screens = new()
        {
            State = new HackScreensState([new HackScreenInfo(1, null, 1920, 1080, false)], HackScreenPermission.Granted, true, [], 1),
        };
        await using PanelHarness harness = await PanelHarness.RenderAsync(new FakeSystemSettings(), screens: screens);
        Assert.DoesNotContain("Close screen 2", harness.Html(), StringComparison.Ordinal);

        screens.State = screens.State with { Screens = [.. screens.State.Screens, new HackScreenInfo(2, null, 800, 600, false)] };
        await harness.RunOnDispatcherAsync(screens.RaiseAsync);

        Assert.Contains("Close screen 2", harness.Html(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_screens_manage_the_page_keeps_the_file_part()
    {
        FakeScreens screens = new()
        {
            State = new HackScreensState([], HackScreenPermission.Unknown, false, [], 1),
            Denied = true,
        };
        await using PanelHarness harness = await PanelHarness.RenderAsync(new FakeSystemSettings(), screens: screens);

        Assert.Null(harness.Panel.Live);
        Assert.Contains("No secondary screen yet.", harness.Html(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task In_a_French_session_the_open_screens_speak_French()
    {
        FakeScreens screens = new()
        {
            State = new HackScreensState([new HackScreenInfo(1, null, 1920, 1080, false)], HackScreenPermission.Prompt, true, [], 1),
        };
        await using PanelHarness harness = await PanelHarness.RenderAsync(new FakeSystemSettings(), lang: "fr_CA.UTF-8", screens: screens);

        string html = harness.Html();
        Assert.Contains("Écrans ouverts", html, StringComparison.Ordinal);
        Assert.Contains("Nouvel écran", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Settings_lists_the_page_under_personalization()
    {
        SettingsPage page = Assert.Single(SettingsWindow.AllPages, candidate => candidate.Id == "screens");
        Assert.Equal("Nav.Group.Personalization", page.GroupKey);
        Assert.Equal("screens", SettingsWindow.ReadPageArgument(["--page=screens"]));
    }

    /// <summary>The OS settings gateway over two in-memory layers, with the OS's schema of <c>screens.config</c>.</summary>
    private sealed class FakeSystemSettings : IAppSystemSettingsGateway
    {
        private readonly List<Func<ValueTask>> _watchers = [];

        public Dictionary<string, string> User { get; } = new(StringComparer.Ordinal);

        public AppSystemSettingsDocumentInfo Describe(string documentId) =>
            documentId == ScreensSettingsDocuments.DocumentId
                ? new(documentId, ScreensSettingsDocuments.Schema, "/etc/hackeros/screens.config", "/home/alice/.config/hackeros/screens.config", false)
                : throw new ArgumentException($"Unknown document '{documentId}'.", nameof(documentId));

        public ValueTask<LayeredConfigSnapshot> ReadAsync(string documentId, CancellationToken cancellationToken = default)
        {
            Dictionary<string, string> values = new(StringComparer.Ordinal);
            Dictionary<string, SettingLayer> sources = new(StringComparer.Ordinal);
            foreach (SettingFieldDeclaration field in ScreensSettingsDocuments.Schema.Fields.Values)
            {
                bool set = User.TryGetValue(field.QualifiedKey, out string? value);
                values[field.QualifiedKey] = set ? value! : field.DefaultValue;
                sources[field.QualifiedKey] = set ? SettingLayer.User : SettingLayer.Default;
            }

            return ValueTask.FromResult(new LayeredConfigSnapshot(values, sources, new HashSet<string>(), []));
        }

        public ValueTask SetAsync(string documentId, IReadOnlyList<KeyValuePair<string, string>> values, SettingLayer target, CancellationToken cancellationToken = default)
        {
            Assert.Equal(SettingLayer.User, target);
            foreach ((string key, string value) in values)
            {
                User[key] = value;
            }

            return ValueTask.CompletedTask;
        }

        public ValueTask ResetAsync(string documentId, string qualifiedKey, SettingLayer target, CancellationToken cancellationToken = default)
        {
            Assert.Equal(SettingLayer.User, target);
            User.Remove(qualifiedKey);
            return ValueTask.CompletedTask;
        }

        public ValueTask<IAsyncDisposable> WatchAsync(string documentId, Func<ValueTask> onChanged, CancellationToken cancellationToken = default)
        {
            _watchers.Add(onChanged);
            return ValueTask.FromResult<IAsyncDisposable>(new NoWatch());
        }

        public async Task RaiseAsync()
        {
            foreach (Func<ValueTask> watcher in _watchers.ToList())
            {
                await watcher();
            }
        }

        private sealed class NoWatch : IAsyncDisposable
        {
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    /// <summary>The screens gateway of a host with screens: records each request and answers from <see cref="State"/>.</summary>
    private sealed class FakeScreens : IAppScreensGateway
    {
        private readonly List<Func<ValueTask>> _watchers = [];

        public HackScreensState State { get; set; } = new([], HackScreenPermission.Unknown, false, [], 1);

        public HackScreenOpenStatus OpenStatus { get; init; } = HackScreenOpenStatus.Opened;

        public bool FullScreenResult { get; init; } = true;

        public bool Denied { get; init; }

        public Action? OnOpen { get; set; }

        public List<string> Calls { get; } = [];

        public bool IsSupported => true;

        public HackScreensState GetState() =>
            Denied ? throw new AppGatewayAccessDeniedException(AppCapabilities.ScreensManage, CapabilityPolicyEvaluation.DenyMissing(1)) : State;

        public ValueTask<IAsyncDisposable> WatchAsync(Func<ValueTask> onChanged, CancellationToken cancellationToken = default)
        {
            _watchers.Add(onChanged);
            return ValueTask.FromResult<IAsyncDisposable>(new NoWatch());
        }

        public ValueTask<HackScreenOpenStatus> OpenScreenAsync(CancellationToken cancellationToken = default)
        {
            Calls.Add("open");
            OnOpen?.Invoke();
            return ValueTask.FromResult(OpenStatus);
        }

        public ValueTask<int> RestoreScreensAsync(CancellationToken cancellationToken = default)
        {
            Calls.Add("restore");
            return ValueTask.FromResult(1);
        }

        public ValueTask CloseScreenAsync(int number, CancellationToken cancellationToken = default)
        {
            Calls.Add($"close {number}");
            return ValueTask.CompletedTask;
        }

        public ValueTask IdentifyAsync(CancellationToken cancellationToken = default)
        {
            Calls.Add("identify");
            return ValueTask.CompletedTask;
        }

        public ValueTask<bool> ToggleFullScreenAsync(CancellationToken cancellationToken = default)
        {
            Calls.Add("fullscreen");
            return ValueTask.FromResult(FullScreenResult);
        }

        public ValueTask<HackScreenPermission> RequestPermissionAsync(CancellationToken cancellationToken = default)
        {
            Calls.Add("permission");
            return ValueTask.FromResult(HackScreenPermission.Granted);
        }

        public async Task RaiseAsync()
        {
            foreach (Func<ValueTask> watcher in _watchers.ToList())
            {
                await watcher();
            }
        }

        private sealed class NoWatch : IAsyncDisposable
        {
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    /// <summary>Hosts one <see cref="ScreensPanel"/> in a static renderer, under a cascading app context.</summary>
    private sealed class PanelHarness : IAsyncDisposable
    {
        private readonly ServiceProvider _services;
        private readonly HtmlRenderer _renderer;
        private readonly IAppExecutionContext _context;
        private HtmlRootComponent _root;

        private PanelHarness(ServiceProvider services, HtmlRenderer renderer, IAppExecutionContext context)
        {
            _services = services;
            _renderer = renderer;
            _context = context;
        }

        public ScreensPanel Panel { get; private set; } = default!;

        public static async Task<PanelHarness> RenderAsync(FakeSystemSettings settings, string lang = "en_US.UTF-8", IAppScreensGateway? screens = null)
        {
            FakeAppEnvironmentGateway environment = new("/home/alice", "alice");
            environment.SetVariable("LANG", lang);
            IAppExecutionContext context = new MinimalAppExecutionContext(
                AppDefinitionMapper.ToManifest(new SettingsApp()),
                userId: "alice",
                homeDirectory: "/home/alice",
                environment: environment,
                systemSettings: settings,
                screens: screens);
            ServiceProvider services = new ServiceCollection().BuildServiceProvider();
            PanelHarness harness = new(services, new HtmlRenderer(services, NullLoggerFactory.Instance), context);
            harness._root = await harness._renderer.Dispatcher.InvokeAsync(() => harness._renderer.RenderComponentAsync<PanelHost>(
                ParameterView.FromDictionary(new Dictionary<string, object?>
                {
                    [nameof(PanelHost.Context)] = context,
                    [nameof(PanelHost.Captured)] = (Action<ScreensPanel>)(panel => harness.Panel = panel)
                })));
            await harness._root.QuiescenceTask;
            return harness;
        }

        /// <summary>Runs a panel action on the renderer's dispatcher, inside the app's ambient scope (as a click does).</summary>
        public Task RunAsync(Func<ScreensPanel, Task> action) =>
            _renderer.Dispatcher.InvokeAsync(async () =>
            {
                using (HackExecutionContext.Enter(_context))
                {
                    await action(Panel);
                }
            });

        /// <summary>Tells the open document its file changed, then waits for the panel to render again.</summary>
        public async Task RaiseChangedAsync(FakeSystemSettings settings)
        {
            await _renderer.Dispatcher.InvokeAsync(settings.RaiseAsync);
            await _renderer.Dispatcher.InvokeAsync(() => Task.CompletedTask);
        }

        /// <summary>Runs a callback on the renderer's dispatcher, then waits for the panel to render again.</summary>
        public async Task RunOnDispatcherAsync(Func<Task> callback)
        {
            await _renderer.Dispatcher.InvokeAsync(callback);
            await _renderer.Dispatcher.InvokeAsync(() => Task.CompletedTask);
        }

        /// <summary>The current HTML, decoded so accents and quotes read as text.</summary>
        public string Html() => WebUtility.HtmlDecode(_renderer.Dispatcher.InvokeAsync(() => _root.ToHtmlString()).GetAwaiter().GetResult());

        public async ValueTask DisposeAsync()
        {
            await _renderer.DisposeAsync();
            await _services.DisposeAsync();
        }
    }

    /// <summary>Cascades the app context to the panel and hands the panel instance to the test.</summary>
    private sealed class PanelHost : ComponentBase
    {
        [Parameter]
        public IAppExecutionContext Context { get; set; } = default!;

        [Parameter]
        public Action<ScreensPanel> Captured { get; set; } = default!;

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<CascadingValue<IAppExecutionContext>>(0);
            builder.AddComponentParameter(1, nameof(CascadingValue<IAppExecutionContext>.Value), Context);
            builder.AddComponentParameter(2, nameof(CascadingValue<IAppExecutionContext>.ChildContent), (RenderFragment)(child =>
            {
                child.OpenComponent<ScreensPanel>(0);
                child.AddComponentReferenceCapture(1, reference => Captured((ScreensPanel)reference));
                child.CloseComponent();
            }));
            builder.CloseComponent();
        }
    }
}
