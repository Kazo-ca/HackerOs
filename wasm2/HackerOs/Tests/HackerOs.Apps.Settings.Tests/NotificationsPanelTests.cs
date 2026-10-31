using System.Net;
using HackerOs.App.Abstractions;
using HackerOs.AppSdk;
using HackerOs.AppSdk.Settings;
using HackerOs.Platform.Core.Notifications;
using HackerOs.Platform.Core.Settings;
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
/// Settings &gt; Notifications (ADR 0216 §2): the "Do not disturb" switch written to the user layer
/// of <c>notifications.config</c>. Rendered with the real <see cref="HtmlRenderer"/> over an in-memory
/// OS settings gateway that carries the OS's own schema.
/// </summary>
public sealed class NotificationsPanelTests
{
    [Fact]
    public void The_panel_keys_are_the_keys_the_OS_declares()
    {
        Assert.Equal(NotificationSettingsDocuments.DocumentId, NotificationsPanel.DocumentId);
        Assert.Equal(NotificationSettingsDocuments.DoNotDisturbKey, NotificationsPanel.DoNotDisturbKey);
        Assert.True(OsSettingsDocumentRegistry.TryGet(NotificationsPanel.DocumentId, out _));
        SettingsPage page = Assert.Single(SettingsWindow.AllPages, candidate => candidate.Id == "notifications");
        Assert.Equal("Nav.Group.Personalization", page.GroupKey);
    }

    [Fact]
    public async Task The_mode_is_off_by_default()
    {
        await using PanelHarness harness = await PanelHarness.RenderAsync(new FakeSystemSettings(), new FakeAppFileSystemGateway());

        Assert.False(harness.Panel.DoNotDisturb);
        string html = harness.Html();
        Assert.Contains("Do not disturb", html, StringComparison.Ordinal);
        Assert.Contains("~/.config/hackeros/notifications.config", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_panel_speaks_the_language_of_the_session()
    {
        await using PanelHarness harness = await PanelHarness.RenderAsync(new FakeSystemSettings(), new FakeAppFileSystemGateway(), lang: "fr_CA.UTF-8");

        Assert.Contains("Ne pas déranger", harness.Html(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_switch_is_written_to_the_user_layer()
    {
        FakeSystemSettings settings = new();
        await using PanelHarness harness = await PanelHarness.RenderAsync(settings, new FakeAppFileSystemGateway());

        await harness.RunAsync(panel => panel.SetDoNotDisturbAsync(true));

        Assert.Equal("true", settings.User["doNotDisturb"]);
        Assert.True(harness.Panel.DoNotDisturb);
        Assert.False(harness.Panel.MessageIsError);
        Assert.Contains("Do not disturb is on.", harness.Html(), StringComparison.Ordinal);

        await harness.RunAsync(panel => panel.SetDoNotDisturbAsync(false));

        Assert.Equal("false", settings.User["doNotDisturb"]);
        Assert.False(harness.Panel.DoNotDisturb);
    }

    /// <summary>The OS settings gateway over an in-memory user layer, with the OS's schema of <c>notifications.config</c>.</summary>
    private sealed class FakeSystemSettings : IAppSystemSettingsGateway
    {
        public Dictionary<string, string> User { get; } = new(StringComparer.Ordinal);

        public AppSystemSettingsDocumentInfo Describe(string documentId) =>
            documentId == NotificationSettingsDocuments.DocumentId
                ? new(documentId, NotificationSettingsDocuments.Schema, "/etc/hackeros/notifications.config", "/home/alice/.config/hackeros/notifications.config", false)
                : throw new ArgumentException($"Unknown document '{documentId}'.", nameof(documentId));

        public ValueTask<LayeredConfigSnapshot> ReadAsync(string documentId, CancellationToken cancellationToken = default)
        {
            Dictionary<string, string> values = new(StringComparer.Ordinal);
            Dictionary<string, SettingLayer> sources = new(StringComparer.Ordinal);
            foreach (SettingFieldDeclaration field in NotificationSettingsDocuments.Schema.Fields.Values)
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

    /// <summary>Hosts one <see cref="NotificationsPanel"/> in a static renderer, under a cascading app context.</summary>
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

        public NotificationsPanel Panel { get; private set; } = default!;

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
                // The panel reads the ambient app, as in the Settings window.
                using (HackExecutionContext.Enter(context))
                {
                    return harness._renderer.RenderComponentAsync<PanelHost>(
                        ParameterView.FromDictionary(new Dictionary<string, object?>
                        {
                            [nameof(PanelHost.Context)] = context,
                            [nameof(PanelHost.Captured)] = (Action<NotificationsPanel>)(panel => harness.Panel = panel)
                        }));
                }
            });
            await harness._root.QuiescenceTask;
            return harness;
        }

        /// <summary>Runs a panel action on the renderer's dispatcher, inside the app's ambient scope (as a click does).</summary>
        public Task RunAsync(Func<NotificationsPanel, Task> action) =>
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
        public Action<NotificationsPanel> Captured { get; set; } = default!;

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<CascadingValue<IAppExecutionContext>>(0);
            builder.AddComponentParameter(1, nameof(CascadingValue<IAppExecutionContext>.Value), Context);
            builder.AddComponentParameter(2, nameof(CascadingValue<IAppExecutionContext>.ChildContent), (RenderFragment)(child =>
            {
                child.OpenComponent<NotificationsPanel>(0);
                child.AddComponentReferenceCapture(1, reference => Captured((NotificationsPanel)reference));
                child.CloseComponent();
            }));
            builder.CloseComponent();
        }
    }
}
