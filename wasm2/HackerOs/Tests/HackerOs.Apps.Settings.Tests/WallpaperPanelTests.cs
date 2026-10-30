using System.Net;
using HackerOs.App.Abstractions;
using HackerOs.AppSdk;
using HackerOs.AppSdk.Administration;
using HackerOs.AppSdk.Settings;
using HackerOs.Platform.Core.Appearance;
using HackerOs.Simulation.Abstractions.Settings;
using HackerOs.Tests.Support;
using HackerOs.Theming.Abstractions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.Web.HtmlRendering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;
using Xunit;

namespace HackerOs.Apps.Settings.Tests;

/// <summary>
/// Settings &gt; Appearance &gt; Wallpaper (ADR 0202 §5): the built-in and library images, the choice
/// written for every screen or for one screen, the fit and « Browse… ». Rendered with the real
/// <see cref="HtmlRenderer"/> over an in-memory OS settings gateway that carries the OS's own schema.
/// </summary>
public sealed class WallpaperPanelTests
{
    private const string WebpHeader = "RIFF\u0010\0\0\0WEBPVP8 ";

    [Fact]
    public void The_panel_keys_are_the_keys_the_OS_declares()
    {
        Assert.Equal(AppearanceSettingsDocuments.WallpaperKey, WallpaperPanel.WallpaperKey);
        Assert.Equal(AppearanceSettingsDocuments.WallpaperFitKey, WallpaperPanel.FitKey);
        Assert.Equal(AppearanceSettingsDocuments.MaxWallpaperScreen, WallpaperPanel.MaxScreenNumber);
        Assert.Equal(AppearanceSettingsDocuments.WallpaperScreenKey(4), WallpaperPanel.ScreenKey(4));
    }

    [Fact]
    public async Task The_theme_background_and_the_built_ins_are_offered_and_a_choice_is_written_for_every_screen()
    {
        FakeAppearance settings = new();
        await using PanelHarness harness = await PanelHarness.RenderAsync(settings, new FakeAppFileSystemGateway());

        string html = harness.Html();
        Assert.Contains("Theme background", html, StringComparison.Ordinal);
        Assert.Contains("Aurora", html, StringComparison.Ordinal);
        Assert.Contains("_content/HackerOs.Theming.Blazor/wallpapers/nebula.webp", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Screen 1 and the others", html, StringComparison.Ordinal);

        await harness.RunAsync(panel => panel.ChooseAsync("builtin:aurora"));

        Assert.Equal("builtin:aurora", settings.User["wallpaper"]);
        Assert.Equal("builtin:aurora", harness.Panel.SelectedValue);
        Assert.Contains("Wallpaper saved.", harness.Html(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_library_lists_the_system_user_and_pack_images_and_skips_what_is_not_an_image()
    {
        FakeAppFileSystemGateway files = new FakeAppFileSystemGateway()
            .WithFile("/usr/share/hackeros/wallpapers/city.webp", WebpHeader)
            .WithFile("/usr/share/hackeros/wallpapers/readme.txt", "hello")
            .WithFile("/home/alice/.local/share/hackeros/wallpapers/cat.png", "not really a png")
            .WithFile("/home/alice/.local/share/hackeros/wallpapers/dog.webp", WebpHeader)
            .WithFile("/home/alice/.local/share/hackeros/theme-packs/neon/wallpapers/neon.webp", WebpHeader);
        await using PanelHarness harness = await PanelHarness.RenderAsync(new FakeAppearance(), files);

        Assert.Equal(
            [("/usr/share/hackeros/wallpapers/city.webp", "system"),
             ("/home/alice/.local/share/hackeros/wallpapers/dog.webp", "user"),
             ("/home/alice/.local/share/hackeros/theme-packs/neon/wallpapers/neon.webp", "pack")],
            harness.Panel.Library.Select(static image => (image.Path, image.Origin)));
        Assert.All(harness.Panel.Library, static image => Assert.Equal("image/webp", image.MediaType));
    }

    [Fact]
    public async Task With_several_screens_a_choice_can_be_for_one_screen_only()
    {
        FakeAppearance settings = new();
        FakeScreens screens = new(new HackScreensState(
            [new HackScreenInfo(1, null, 1920, 1080, false), new HackScreenInfo(2, null, 1280, 1024, false)],
            HackScreenPermission.Granted, true, [], 1));
        await using PanelHarness harness = await PanelHarness.RenderAsync(settings, new FakeAppFileSystemGateway(), screens);

        Assert.Equal([1, 2], harness.Panel.Screens);
        string html = harness.Html();
        Assert.Contains("Screen 1 and the others", html, StringComparison.Ordinal);
        Assert.Contains("Screen 2", html, StringComparison.Ordinal);

        await harness.RunAsync(panel =>
        {
            panel.SelectScreen(2);
            return panel.ChooseAsync("builtin:circuit");
        });

        Assert.Equal("builtin:circuit", settings.User["wallpaperScreen2"]);
        Assert.False(settings.User.ContainsKey("wallpaper"));
        Assert.Contains("Same as screen 1", harness.Html(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Browse_keeps_the_path_of_an_image_and_refuses_another_file()
    {
        FakeAppearance settings = new();
        FakeAppFileSystemGateway files = new FakeAppFileSystemGateway().WithFile("/home/alice/Pictures/beach.webp", WebpHeader);
        string? picked = "/home/alice/Pictures/notes.txt";
        await using PanelHarness harness = await PanelHarness.RenderAsync(settings, files, browse: () => Task.FromResult<string?>(picked));

        await harness.RunAsync(panel => panel.BrowseAsync());
        Assert.False(settings.User.ContainsKey("wallpaper"));
        Assert.Contains("is not a PNG, JPEG or WebP image", harness.Panel.Message, StringComparison.Ordinal);

        picked = "/home/alice/Pictures/beach.webp";
        await harness.RunAsync(panel => panel.BrowseAsync());
        Assert.Equal("/home/alice/Pictures/beach.webp", settings.User["wallpaper"]);
        Assert.Equal(("/home/alice/Pictures/beach.webp", "file"), harness.Panel.Library.Select(static image => (image.Path, image.Origin)).Single());

        picked = null;
        await harness.RunAsync(panel => panel.BrowseAsync());
        Assert.Equal("/home/alice/Pictures/beach.webp", settings.User["wallpaper"]);
    }

    [Fact]
    public async Task The_fit_is_written_to_wallpaperFit()
    {
        FakeAppearance settings = new();
        await using PanelHarness harness = await PanelHarness.RenderAsync(settings, new FakeAppFileSystemGateway());

        await harness.RunAsync(panel => panel.ChangeFitAsync("center"));

        Assert.Equal("center", settings.User["wallpaperFit"]);
    }

    [Fact]
    public async Task In_a_French_session_the_panel_speaks_French()
    {
        await using PanelHarness harness = await PanelHarness.RenderAsync(new FakeAppearance(), new FakeAppFileSystemGateway(), lang: "fr_CA.UTF-8");

        string html = harness.Html();
        Assert.Contains("Fond d'écran", html, StringComparison.Ordinal);
        Assert.Contains("Fond du thème", html, StringComparison.Ordinal);
        Assert.Contains("Nébuleuse", html, StringComparison.Ordinal);
        Assert.Contains("Mosaïque", html, StringComparison.Ordinal);
    }

    /// <summary>The OS settings gateway over one in-memory user layer, with the OS's schema of <c>appearance.config</c>.</summary>
    private sealed class FakeAppearance : IAppSystemSettingsGateway
    {
        public Dictionary<string, string> User { get; } = new(StringComparer.Ordinal);

        public AppSystemSettingsDocumentInfo Describe(string documentId) =>
            documentId == AppearanceSettingsDocuments.Document.DocumentId
                ? new(documentId, AppearanceSettingsDocuments.Schema, "/etc/hackeros/appearance.config", "/home/alice/.config/hackeros/appearance.config", false)
                : throw new ArgumentException($"Unknown document '{documentId}'.", nameof(documentId));

        public ValueTask<LayeredConfigSnapshot> ReadAsync(string documentId, CancellationToken cancellationToken = default)
        {
            Dictionary<string, string> values = new(StringComparer.Ordinal);
            Dictionary<string, SettingLayer> sources = new(StringComparer.Ordinal);
            foreach (SettingFieldDeclaration field in AppearanceSettingsDocuments.Schema.Fields.Values)
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
            User.Remove(qualifiedKey);
            return ValueTask.CompletedTask;
        }

        public ValueTask<IAsyncDisposable> WatchAsync(string documentId, Func<ValueTask> onChanged, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IAsyncDisposable>(new NoWatch());
    }

    /// <summary>The screens gateway of a host with screens, answering from a fixed state.</summary>
    private sealed class FakeScreens(HackScreensState state) : IAppScreensGateway
    {
        public bool IsSupported => true;

        public HackScreensState GetState() => state;

        public ValueTask<IAsyncDisposable> WatchAsync(Func<ValueTask> onChanged, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IAsyncDisposable>(new NoWatch());

        public ValueTask<HackScreenOpenStatus> OpenScreenAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public ValueTask<int> RestoreScreensAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public ValueTask CloseScreenAsync(int number, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public ValueTask IdentifyAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public ValueTask<bool> ToggleFullScreenAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public ValueTask<HackScreenPermission> RequestPermissionAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class NoWatch : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>The static renderer has no JavaScript: the thumbnails are never drawn.</summary>
    private sealed class NoJavaScript : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => throw new JSException("No JavaScript in tests.");

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) => throw new JSException("No JavaScript in tests.");
    }

    /// <summary>Hosts one <see cref="WallpaperPanel"/> in a static renderer, under a cascading app context.</summary>
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

        public WallpaperPanel Panel { get; private set; } = default!;

        public static async Task<PanelHarness> RenderAsync(
            FakeAppearance settings,
            FakeAppFileSystemGateway files,
            IAppScreensGateway? screens = null,
            Func<Task<string?>>? browse = null,
            string lang = "en_US.UTF-8")
        {
            FakeAppEnvironmentGateway environment = new("/home/alice", "alice");
            environment.SetVariable("LANG", lang);
            IAppExecutionContext context = new MinimalAppExecutionContext(
                AppDefinitionMapper.ToManifest(new SettingsApp()),
                fileSystem: files,
                userId: "alice",
                homeDirectory: "/home/alice",
                environment: environment,
                systemSettings: settings,
                screens: screens);
            ServiceProvider services = new ServiceCollection().AddSingleton<IJSRuntime, NoJavaScript>().BuildServiceProvider();
            PanelHarness harness = new(services, new HtmlRenderer(services, NullLoggerFactory.Instance), context);
            harness._root = await harness._renderer.Dispatcher.InvokeAsync(() => harness._renderer.RenderComponentAsync<PanelHost>(
                ParameterView.FromDictionary(new Dictionary<string, object?>
                {
                    [nameof(PanelHost.Context)] = context,
                    [nameof(PanelHost.Browse)] = browse,
                    [nameof(PanelHost.Captured)] = (Action<WallpaperPanel>)(panel => harness.Panel = panel)
                })));
            await harness._root.QuiescenceTask;
            return harness;
        }

        /// <summary>Runs a panel action on the renderer's dispatcher, inside the app's ambient scope (as a click does).</summary>
        public Task RunAsync(Func<WallpaperPanel, Task> action) =>
            _renderer.Dispatcher.InvokeAsync(async () =>
            {
                using (HackExecutionContext.Enter(_context))
                {
                    await action(Panel);
                }
            });

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
        public Func<Task<string?>>? Browse { get; set; }

        [Parameter]
        public Action<WallpaperPanel> Captured { get; set; } = default!;

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<CascadingValue<IAppExecutionContext>>(0);
            builder.AddComponentParameter(1, nameof(CascadingValue<IAppExecutionContext>.Value), Context);
            builder.AddComponentParameter(2, nameof(CascadingValue<IAppExecutionContext>.ChildContent), (RenderFragment)(child =>
            {
                child.OpenComponent<WallpaperPanel>(0);
                child.AddComponentParameter(1, nameof(WallpaperPanel.BrowseImage), Browse);
                child.AddComponentReferenceCapture(2, reference => Captured((WallpaperPanel)reference));
                child.CloseComponent();
            }));
            builder.CloseComponent();
        }
    }
}
