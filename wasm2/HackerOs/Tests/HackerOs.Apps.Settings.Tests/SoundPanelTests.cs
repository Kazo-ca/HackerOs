using System.Net;
using HackerOs.App.Abstractions;
using HackerOs.AppSdk;
using HackerOs.AppSdk.Settings;
using HackerOs.Platform.Core.Settings;
using HackerOs.Platform.Core.Sounds;
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
/// Settings &gt; Sound (ADR 0204 §6): the switch, the volume and the theme written to the user layer
/// of <c>sound.config</c>, and the themes listed from <c>/usr/share/sounds</c> and
/// <c>~/.local/share/sounds</c>. Rendered with the real <see cref="HtmlRenderer"/> over an in-memory
/// OS settings gateway that carries the OS's own schema.
/// </summary>
public sealed class SoundPanelTests
{
    [Fact]
    public void The_panel_keys_are_the_keys_the_OS_declares()
    {
        Assert.Equal(SoundSettingsDocuments.DocumentId, SoundPanel.DocumentId);
        Assert.Equal(SoundSettingsDocuments.EnabledKey, SoundPanel.EnabledKey);
        Assert.Equal(SoundSettingsDocuments.ThemeKey, SoundPanel.ThemeKey);
        Assert.Equal(SoundSettingsDocuments.VolumeKey, SoundPanel.VolumeKey);
        Assert.True(OsSettingsDocumentRegistry.TryGet(SoundPanel.DocumentId, out _));
        SettingsPage page = Assert.Single(SettingsWindow.AllPages, candidate => candidate.Id == "sound");
        Assert.Equal("Nav.Group.Personalization", page.GroupKey);
    }

    [Fact]
    public async Task System_themes_come_first_and_a_user_folder_cannot_hide_one()
    {
        await using PanelHarness harness = await PanelHarness.RenderAsync(new FakeSystemSettings(), Themes());

        Assert.Equal(["hackeros", "soft", "retro"], harness.Panel.Themes.Select(static theme => theme.Id));
        Assert.Equal([false, false, true], harness.Panel.Themes.Select(static theme => theme.IsUserTheme));
        Assert.Equal("Soft", harness.Panel.Themes[1].Name);
        Assert.True(harness.Panel.Enabled);
        Assert.Equal("hackeros", harness.Panel.Theme);
        Assert.Equal(60, harness.Panel.Volume);
        string html = harness.Html();
        Assert.Contains("Play system sounds", html, StringComparison.Ordinal);
        Assert.Contains("dialog-error.oga", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Theme_names_follow_the_language_of_the_session()
    {
        await using PanelHarness harness = await PanelHarness.RenderAsync(new FakeSystemSettings(), Themes(), lang: "fr_CA.UTF-8");

        Assert.Equal("Doux", harness.Panel.Themes[1].Name);
        Assert.Contains("Jouer les sons du système", harness.Html(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Choices_are_written_to_the_user_layer()
    {
        FakeSystemSettings settings = new();
        await using PanelHarness harness = await PanelHarness.RenderAsync(settings, Themes());

        await harness.RunAsync(panel => panel.SetThemeAsync("retro"));
        await harness.RunAsync(panel => panel.SetVolumeAsync(135));
        await harness.RunAsync(panel => panel.SetEnabledAsync(false));

        Assert.Equal("retro", settings.User["theme"]);
        Assert.Equal("100", settings.User["volume"]);
        Assert.Equal("false", settings.User["enabled"]);
        Assert.Equal(100, harness.Panel.Volume);
        Assert.False(harness.Panel.Enabled);
        Assert.False(harness.Panel.MessageIsError);
        Assert.Contains("System sounds are off.", harness.Html(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unknown_theme_is_refused_and_nothing_is_written()
    {
        FakeSystemSettings settings = new();
        await using PanelHarness harness = await PanelHarness.RenderAsync(settings, Themes());

        await harness.RunAsync(panel => panel.SetThemeAsync("missing"));

        Assert.Empty(settings.User);
        Assert.True(harness.Panel.MessageIsError);
    }

    private static FakeAppFileSystemGateway Themes() => new FakeAppFileSystemGateway()
        .WithFile("/usr/share/sounds/hackeros/index.theme", BuiltInSoundThemes.GetIndexContent(BuiltInSoundThemes.All[0]))
        .WithFile("/usr/share/sounds/soft/index.theme", BuiltInSoundThemes.GetIndexContent(BuiltInSoundThemes.All[1]))
        .WithFile("/usr/share/sounds/empty/readme.txt", "no index.theme here")
        .WithFile("/home/alice/.local/share/sounds/retro/index.theme", "[Sound Theme]\nName=Retro\n")
        .WithFile("/home/alice/.local/share/sounds/soft/index.theme", "[Sound Theme]\nName=Fake soft\n");

    /// <summary>The OS settings gateway over an in-memory user layer, with the OS's schema of <c>sound.config</c>.</summary>
    private sealed class FakeSystemSettings : IAppSystemSettingsGateway
    {
        public Dictionary<string, string> User { get; } = new(StringComparer.Ordinal);

        public AppSystemSettingsDocumentInfo Describe(string documentId) =>
            documentId == SoundSettingsDocuments.DocumentId
                ? new(documentId, SoundSettingsDocuments.Schema, "/etc/hackeros/sound.config", "/home/alice/.config/hackeros/sound.config", false)
                : throw new ArgumentException($"Unknown document '{documentId}'.", nameof(documentId));

        public ValueTask<LayeredConfigSnapshot> ReadAsync(string documentId, CancellationToken cancellationToken = default)
        {
            Dictionary<string, string> values = new(StringComparer.Ordinal);
            Dictionary<string, SettingLayer> sources = new(StringComparer.Ordinal);
            foreach (SettingFieldDeclaration field in SoundSettingsDocuments.Schema.Fields.Values)
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

        private sealed class NoWatch : IAsyncDisposable
        {
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    /// <summary>Hosts one <see cref="SoundPanel"/> in a static renderer, under a cascading app context.</summary>
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

        public SoundPanel Panel { get; private set; } = default!;

        public static async Task<PanelHarness> RenderAsync(
            FakeSystemSettings settings, FakeAppFileSystemGateway files, string lang = "en_US.UTF-8")
        {
            FakeAppEnvironmentGateway environment = new("/home/alice", "alice");
            environment.SetVariable("LANG", lang);
            IAppExecutionContext context = new MinimalAppExecutionContext(
                AppDefinitionMapper.ToManifest(new SettingsApp()),
                fileSystem: files,
                userId: "alice",
                homeDirectory: "/home/alice",
                environment: environment,
                systemSettings: settings);
            ServiceProvider services = new ServiceCollection().BuildServiceProvider();
            PanelHarness harness = new(services, new HtmlRenderer(services, NullLoggerFactory.Instance), context);
            harness._root = await harness._renderer.Dispatcher.InvokeAsync(() =>
            {
                // The panel lists the themes with HackDirectory and HackFile, which read the ambient app.
                using (HackExecutionContext.Enter(context))
                {
                    return harness._renderer.RenderComponentAsync<PanelHost>(
                        ParameterView.FromDictionary(new Dictionary<string, object?>
                        {
                            [nameof(PanelHost.Context)] = context,
                            [nameof(PanelHost.Captured)] = (Action<SoundPanel>)(panel => harness.Panel = panel)
                        }));
                }
            });
            await harness._root.QuiescenceTask;
            return harness;
        }

        /// <summary>Runs a panel action on the renderer's dispatcher, inside the app's ambient scope (as a click does).</summary>
        public Task RunAsync(Func<SoundPanel, Task> action) =>
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
        public Action<SoundPanel> Captured { get; set; } = default!;

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<CascadingValue<IAppExecutionContext>>(0);
            builder.AddComponentParameter(1, nameof(CascadingValue<IAppExecutionContext>.Value), Context);
            builder.AddComponentParameter(2, nameof(CascadingValue<IAppExecutionContext>.ChildContent), (RenderFragment)(child =>
            {
                child.OpenComponent<SoundPanel>(0);
                child.AddComponentReferenceCapture(1, reference => Captured((SoundPanel)reference));
                child.CloseComponent();
            }));
            builder.CloseComponent();
        }
    }
}
