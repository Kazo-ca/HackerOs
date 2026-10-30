using System.Globalization;
using System.Net;
using HackerOs.App.Abstractions;
using HackerOs.App.Abstractions.Policy;
using HackerOs.AppSdk;
using HackerOs.AppSdk.WhatsNew;
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
/// Settings &gt; What's new (ADR 0186 §4): one group per version, the next one first, New badges on
/// unseen changes, and those changes marked seen when the page closes.
/// </summary>
public sealed class WhatsNewPanelTests
{
    private static readonly HackChangeList List = new(
        [
            new HackChangeGroup(null, "2.1.0", null,
            [
                new HackChangeEntry("a", HackChangeCategory.New, "Drag files out", "Drag them to download.",
                    new Uri("https://github.com/Kazo-ca/HackerOs/pull/275"), [new Uri("https://example.com/adr")], IsNew: true),
            ]),
            new HackChangeGroup("2.1.0", null, new DateOnly(2026, 10, 26),
            [
                new HackChangeEntry("b", HackChangeCategory.Improved, "Settings side menu", null, null, [], IsNew: true),
                new HackChangeEntry("c", HackChangeCategory.Fixed, "Old fix", null, null, [], IsNew: false),
            ]),
        ],
        NewCount: 2);

    [Fact]
    public void The_page_is_in_the_menu_after_About_and_opens_with_its_argument()
    {
        List<string> ids = [.. SettingsWindow.AllPages.Select(static page => page.Id)];

        Assert.Equal(ids.IndexOf("about") - 1, ids.IndexOf(SettingsWindow.WhatsNewPageId));
        Assert.Equal("whats-new", SettingsWindow.ReadPageArgument(["--page=whats-new"]));
    }

    [Fact]
    public async Task Groups_badges_and_links_are_shown_in_English()
    {
        FakeWhatsNew gateway = new(List);
        await using PanelHarness harness = await PanelHarness.RenderAsync(gateway);

        string html = harness.Html();
        Assert.Contains("2 new changes since your last visit.", html, StringComparison.Ordinal);
        Assert.Contains("Since 2.1.0 (next version)", html, StringComparison.Ordinal);
        Assert.Contains("Version 2.1.0 — Monday, October 26, 2026", html, StringComparison.Ordinal);
        Assert.True(html.IndexOf("Since 2.1.0", StringComparison.Ordinal) < html.IndexOf("Version 2.1.0", StringComparison.Ordinal));
        Assert.Equal(2, CountOf(html, "class=\"wn-badge\""));
        Assert.Contains("href=\"https://github.com/Kazo-ca/HackerOs/pull/275\"", html, StringComparison.Ordinal);
        Assert.Contains("rel=\"noopener noreferrer\"", html, StringComparison.Ordinal);
        Assert.Contains(">Improved</span>", html, StringComparison.Ordinal);
        Assert.Empty(gateway.Marked);
    }

    [Fact]
    public async Task The_page_speaks_French()
    {
        await using PanelHarness harness = await PanelHarness.RenderAsync(new FakeWhatsNew(List), "fr_CA.UTF-8");

        string html = harness.Html();
        Assert.Contains("2 nouveautés depuis votre dernière visite.", html, StringComparison.Ordinal);
        Assert.Contains("Depuis 2.1.0 (prochaine version)", html, StringComparison.Ordinal);
        Assert.Contains(">Nouveau</span>", html, StringComparison.Ordinal);
        Assert.Contains(">Corrigé</span>", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Closing_the_page_marks_the_changes_that_were_new_as_seen_once()
    {
        FakeWhatsNew gateway = new(List);
        PanelHarness harness = await PanelHarness.RenderAsync(gateway);
        Assert.Equal(["a", "b"], harness.Panel.NewIds);

        await harness.DisposeAsync();
        await harness.Panel.MarkShownAsSeenAsync();

        Assert.Equal([["a", "b"]], gateway.Marked);
    }

    [Fact]
    public async Task An_empty_list_says_so_and_marks_nothing()
    {
        FakeWhatsNew gateway = new(HackChangeList.Empty);
        PanelHarness harness = await PanelHarness.RenderAsync(gateway);

        Assert.Contains("No changes to show.", harness.Html(), StringComparison.Ordinal);
        await harness.DisposeAsync();
        Assert.Empty(gateway.Marked);
    }

    [Fact]
    public async Task The_list_is_read_in_the_language_of_the_Settings_process()
    {
        FakeWhatsNew gateway = new(List);
        await using PanelHarness harness = await PanelHarness.RenderAsync(gateway, "fr_CA.UTF-8");

        Assert.Equal("fr", gateway.Culture!.TwoLetterISOLanguageName);
    }

    private static int CountOf(string text, string value) =>
        (text.Length - text.Replace(value, string.Empty, StringComparison.Ordinal).Length) / value.Length;

    private sealed class FakeWhatsNew(HackChangeList list) : IAppWhatsNewGateway
    {
        public List<IReadOnlyCollection<string>> Marked { get; } = [];

        public CultureInfo? Culture { get; private set; }

        public bool IsSupported => true;

        public ValueTask<HackChangeList> GetAsync(CultureInfo culture, CancellationToken cancellationToken = default)
        {
            Culture = culture;
            return ValueTask.FromResult(list);
        }

        public ValueTask MarkSeenAsync(IReadOnlyCollection<string> ids, CancellationToken cancellationToken = default)
        {
            Marked.Add([.. ids]);
            return ValueTask.CompletedTask;
        }

        public ValueTask<IAsyncDisposable> WatchAsync(Func<ValueTask> onChanged, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class PanelHarness : IAsyncDisposable
    {
        private readonly ServiceProvider _services;
        private readonly HtmlRenderer _renderer;
        private HtmlRootComponent _root;
        private bool _disposed;

        private PanelHarness(ServiceProvider services, HtmlRenderer renderer)
        {
            _services = services;
            _renderer = renderer;
        }

        public WhatsNewPanel Panel { get; private set; } = default!;

        public static async Task<PanelHarness> RenderAsync(IAppWhatsNewGateway gateway, string lang = "en_US.UTF-8")
        {
            FakeAppEnvironmentGateway environment = new("/home/alice", "alice");
            environment.SetVariable("LANG", lang);
            IAppExecutionContext context = new MinimalAppExecutionContext(
                AppDefinitionMapper.ToManifest(new SettingsApp()),
                userId: "alice",
                homeDirectory: "/home/alice",
                environment: environment,
                whatsNew: gateway);
            ServiceProvider services = new ServiceCollection().BuildServiceProvider();
            PanelHarness harness = new(services, new HtmlRenderer(services, NullLoggerFactory.Instance));
            harness._root = await harness._renderer.Dispatcher.InvokeAsync(() => harness._renderer.RenderComponentAsync<PanelHost>(
                ParameterView.FromDictionary(new Dictionary<string, object?>
                {
                    [nameof(PanelHost.Context)] = context,
                    [nameof(PanelHost.Captured)] = (Action<WhatsNewPanel>)(panel => harness.Panel = panel)
                })));
            await harness._root.QuiescenceTask;
            return harness;
        }

        public string Html() => WebUtility.HtmlDecode(_renderer.Dispatcher.InvokeAsync(() => _root.ToHtmlString()).GetAwaiter().GetResult());

        public async ValueTask DisposeAsync()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            await _renderer.DisposeAsync();
            await _services.DisposeAsync();
        }
    }

    private sealed class PanelHost : ComponentBase
    {
        [Parameter]
        public IAppExecutionContext Context { get; set; } = default!;

        [Parameter]
        public Action<WhatsNewPanel> Captured { get; set; } = default!;

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<CascadingValue<IAppExecutionContext>>(0);
            builder.AddComponentParameter(1, nameof(CascadingValue<IAppExecutionContext>.Value), Context);
            builder.AddComponentParameter(2, nameof(CascadingValue<IAppExecutionContext>.ChildContent), (RenderFragment)(child =>
            {
                child.OpenComponent<WhatsNewPanel>(0);
                child.AddComponentReferenceCapture(1, reference => Captured((WhatsNewPanel)reference));
                child.CloseComponent();
            }));
            builder.CloseComponent();
        }
    }
}
