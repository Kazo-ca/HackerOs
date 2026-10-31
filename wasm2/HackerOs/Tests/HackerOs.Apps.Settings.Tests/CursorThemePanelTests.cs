using System.Net;
using HackerOs.App.Abstractions;
using HackerOs.AppSdk;
using HackerOs.AppSdk.Settings;
using HackerOs.Platform.Core.Appearance;
using HackerOs.Platform.Core.Settings;
using HackerOs.Simulation.Abstractions.Gateways;
using HackerOs.Simulation.Abstractions.Settings;
using HackerOs.Tests.Support;
using HackerOs.Theming.Abstractions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.Web.HtmlRendering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace HackerOs.Apps.Settings.Tests;

/// <summary>
/// Settings &gt; Appearance &gt; Cursors (ADR 0205 §5): the built-in themes are offered with a preview,
/// choosing one writes <c>theme</c> to the user layer of <c>cursors.config</c>, an administrator's
/// lock disables the choice, and an edit made elsewhere shows at once.
/// </summary>
public sealed class CursorThemePanelTests
{
    [Fact]
    public void The_panel_keys_are_the_keys_the_OS_declares()
    {
        Assert.Equal(CursorThemePanel.DocumentId, CursorSettingsDocuments.Document.DocumentId);
        Assert.Equal(CursorThemePanel.ThemeKey, CursorSettingsDocuments.ThemeKey);
        Assert.True(OsSettingsDocumentRegistry.TryGet(CursorThemePanel.DocumentId, out _));
    }

    [Fact]
    public async Task The_built_in_themes_are_offered_with_system_chosen_and_a_preview_strip()
    {
        await using PanelHarness harness = await PanelHarness.RenderAsync(new FakeCursorSettings());

        Assert.Equal(CursorThemeCatalog.SystemId, harness.Panel.SelectedId);
        Assert.Equal(
            [CursorThemeCatalog.SystemId, CursorThemeCatalog.NeonId, CursorThemeCatalog.CrimsonId],
            harness.Panel.Themes.Select(theme => theme.Id));
        string html = harness.Html();
        Assert.Contains("id=\"cursor-theme-system\" aria-checked=\"true\"", html, StringComparison.Ordinal);
        Assert.Contains("HackerOS Neon", html, StringComparison.Ordinal);
        Assert.Contains("_content/HackerOs.Theming.Blazor/cursors/gothic-crimson/pointer.png", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Choosing_a_theme_writes_it_to_the_user_layer()
    {
        FakeCursorSettings settings = new();
        await using PanelHarness harness = await PanelHarness.RenderAsync(settings);

        await harness.RunAsync(panel => panel.ChooseAsync(CursorThemeCatalog.NeonId));

        Assert.Equal(CursorThemeCatalog.NeonId, settings.User[CursorSettingsDocuments.ThemeKey]);
        Assert.Equal(CursorThemeCatalog.NeonId, harness.Panel.SelectedId);
        Assert.Contains("Saved. The desktop uses these cursors now.", harness.Html(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_administrator_lock_disables_every_choice()
    {
        FakeCursorSettings settings = new() { Locked = true };
        await using PanelHarness harness = await PanelHarness.RenderAsync(settings);

        string html = harness.Html();
        Assert.Contains("id=\"cursor-theme-hackeros-neon\" aria-checked=\"false\" disabled", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_edit_made_elsewhere_shows_at_once_in_french()
    {
        FakeCursorSettings settings = new();
        await using PanelHarness harness = await PanelHarness.RenderAsync(settings, "fr_FR.UTF-8");
        Assert.Contains("Curseurs", harness.Html(), StringComparison.Ordinal);

        settings.User[CursorSettingsDocuments.ThemeKey] = CursorThemeCatalog.CrimsonId;
        await harness.RaiseChangedAsync(settings);

        Assert.Equal(CursorThemeCatalog.CrimsonId, harness.Panel.SelectedId);
        Assert.Contains("Gothique Pourpre", harness.Html(), StringComparison.Ordinal);
    }

    /// <summary>The cursors document of the OS, in memory, with an optional administrator lock.</summary>
    private sealed class FakeCursorSettings : IAppSystemSettingsGateway
    {
        private readonly List<Func<ValueTask>> _watchers = [];

        public Dictionary<string, string> User { get; } = new(StringComparer.Ordinal);

        public bool Locked { get; init; }

        public AppSystemSettingsDocumentInfo Describe(string documentId) =>
            documentId == CursorSettingsDocuments.Document.DocumentId
                ? new(documentId, CursorSettingsDocuments.Schema, "/etc/hackeros/cursors.config", "/home/alice/.config/hackeros/cursors.config", false)
                : throw new ArgumentException($"Unknown document '{documentId}'.", nameof(documentId));

        public ValueTask<LayeredConfigSnapshot> ReadAsync(string documentId, CancellationToken cancellationToken = default)
        {
            Dictionary<string, string> values = new(StringComparer.Ordinal);
            Dictionary<string, SettingLayer> sources = new(StringComparer.Ordinal);
            foreach (SettingFieldDeclaration field in CursorSettingsDocuments.Schema.Fields.Values)
            {
                bool set = User.TryGetValue(field.QualifiedKey, out string? value);
                values[field.QualifiedKey] = set ? value! : field.DefaultValue;
                sources[field.QualifiedKey] = Locked ? SettingLayer.System : set ? SettingLayer.User : SettingLayer.Default;
            }

            HashSet<string> locked = Locked ? [CursorSettingsDocuments.ThemeKey] : [];
            return ValueTask.FromResult(new LayeredConfigSnapshot(values, sources, locked, []));
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

        public CursorThemePanel Panel { get; private set; } = default!;

        public static async Task<PanelHarness> RenderAsync(FakeCursorSettings settings, string lang = "en_US.UTF-8")
        {
            FakeAppEnvironmentGateway environment = new("/home/alice", "alice");
            environment.SetVariable("LANG", lang);
            IAppExecutionContext context = new MinimalAppExecutionContext(
                AppDefinitionMapper.ToManifest(new SettingsApp()),
                userId: "alice",
                homeDirectory: "/home/alice",
                environment: environment,
                systemSettings: settings);
            ServiceProvider services = new ServiceCollection().BuildServiceProvider();
            PanelHarness harness = new(services, new HtmlRenderer(services, NullLoggerFactory.Instance), context);
            harness._root = await harness._renderer.Dispatcher.InvokeAsync(() =>
            {
                using (HackExecutionContext.Enter(context))
                {
                    return harness._renderer.RenderComponentAsync<PanelHost>(ParameterView.FromDictionary(new Dictionary<string, object?>
                    {
                        [nameof(PanelHost.Context)] = context,
                        [nameof(PanelHost.Captured)] = (Action<CursorThemePanel>)(panel => harness.Panel = panel)
                    }));
                }
            });
            await harness._root.QuiescenceTask;
            return harness;
        }

        /// <summary>Runs a panel action on the renderer's dispatcher, inside the app's ambient scope (as a click does).</summary>
        public Task RunAsync(Func<CursorThemePanel, Task> action) =>
            _renderer.Dispatcher.InvokeAsync(async () =>
            {
                using (HackExecutionContext.Enter(_context))
                {
                    await action(Panel);
                }
            });

        /// <summary>Tells the open document its file changed, then waits for the panel to render again.</summary>
        public async Task RaiseChangedAsync(FakeCursorSettings settings)
        {
            await _renderer.Dispatcher.InvokeAsync(settings.RaiseAsync);
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
        public Action<CursorThemePanel> Captured { get; set; } = default!;

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<CascadingValue<IAppExecutionContext>>(0);
            builder.AddComponentParameter(1, nameof(CascadingValue<IAppExecutionContext>.Value), Context);
            builder.AddComponentParameter(2, nameof(CascadingValue<IAppExecutionContext>.ChildContent), (RenderFragment)(child =>
            {
                child.OpenComponent<CursorThemePanel>(0);
                child.AddComponentReferenceCapture(1, reference => Captured((CursorThemePanel)reference));
                child.CloseComponent();
            }));
            builder.CloseComponent();
        }
    }
}
