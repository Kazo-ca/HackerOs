using System.Net;
using HackerOs.App.Abstractions;
using HackerOs.AppSdk;
using HackerOs.AppSdk.Administration;
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
/// Settings &gt; Server Connection &gt; Local server (ADR 0191 §6): the download list, the Test
/// button, and pairing with a one-time code. Nothing is called before a button is pressed (§3).
/// </summary>
public sealed class LocalServerPanelTests
{
    [Fact]
    public void Every_release_archive_is_listed_with_the_OS_version()
    {
        IReadOnlyList<ServerDownload> downloads = LocalServerPanel.Downloads("2.2.0");

        Assert.Equal(
            [
                "hackeros-server-2.2.0-win-x64.zip",
                "hackeros-server-2.2.0-win-arm64.zip",
                "hackeros-server-2.2.0-osx-arm64.tar.gz",
                "hackeros-server-2.2.0-osx-x64.tar.gz",
                "hackeros-server-2.2.0-linux-x64.tar.gz",
                "hackeros-server-2.2.0-linux-arm64.tar.gz",
            ],
            downloads.Select(static download => download.FileName));
    }

    [Fact]
    public async Task The_panel_offers_the_downloads_and_calls_nothing_by_itself()
    {
        FakeServerConnection gateway = new();
        await using PanelHarness harness = await PanelHarness.RenderAsync(gateway);

        string html = harness.Html();
        Assert.Contains($"hackeros-server-{HackerOsSdk.Version}-win-x64.zip", html, StringComparison.Ordinal);
        Assert.Contains("ghcr.io/kazo-ca/hackeros-server", html, StringComparison.Ordinal);
        Assert.Contains("href=\"https://github.com/Kazo-ca/HackerOs/releases/latest\"", html, StringComparison.Ordinal);
        Assert.Contains("value=\"http://localhost:5888\"", html, StringComparison.Ordinal);
        Assert.Equal(0, gateway.Calls);
    }

    [Fact]
    public async Task Test_shows_what_the_address_answered()
    {
        FakeServerConnection gateway = new() { Probe = new HackServerProbe(true, "2.1.0", "1.0", null) };
        await using PanelHarness harness = await PanelHarness.RenderAsync(gateway);

        await harness.InvokeAsync(() => harness.Panel.TestAsync());

        Assert.Equal(new Uri("http://localhost:5888/"), gateway.TestedUrl);
        Assert.Contains("A HackerOS server answers (version 2.1.0).", harness.Html(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Pairing_sends_the_code_and_reports_the_connection()
    {
        FakeServerConnection gateway = new();
        await using PanelHarness harness = await PanelHarness.RenderAsync(gateway);
        harness.Panel.Address = "https://192.168.1.20:5889";
        harness.Panel.Code = "H9R4-JYYG";

        await harness.InvokeAsync(() => harness.Panel.PairAsync());

        Assert.Equal((new Uri("https://192.168.1.20:5889/"), "H9R4-JYYG"), gateway.Paired);
        HackServerConnectionState state = Assert.IsType<HackServerConnectionState>(harness.PairedState);
        Assert.True(state.IsConnected);
        Assert.Equal(string.Empty, harness.Panel.Code);
    }

    [Fact]
    public async Task A_refused_code_is_shown_in_French()
    {
        FakeServerConnection gateway = new() { RefusePairing = true };
        await using PanelHarness harness = await PanelHarness.RenderAsync(gateway, "fr_CA.UTF-8");
        harness.Panel.Code = "AAAA-AAAA";

        await harness.InvokeAsync(() => harness.Panel.PairAsync());

        Assert.Null(harness.PairedState);
        Assert.Contains("L'appairage a échoué : Could not pair: 401", harness.Html(), StringComparison.Ordinal);
        Assert.Contains("Appairer cet appareil", harness.Html(), StringComparison.Ordinal);
    }

    private sealed class FakeServerConnection : IAppServerConnectionGateway
    {
        public int Calls { get; private set; }

        public HackServerProbe Probe { get; init; } = new(false, null, null, "refused");

        public bool RefusePairing { get; init; }

        public Uri? TestedUrl { get; private set; }

        public (Uri Url, string Code)? Paired { get; private set; }

        public ValueTask<HackServerProbe> TestAsync(Uri serverUrl, CancellationToken cancellationToken = default)
        {
            Calls++;
            TestedUrl = serverUrl;
            return ValueTask.FromResult(Probe);
        }

        public ValueTask<HackServerConnectionState> PairAsync(Uri serverUrl, string code, CancellationToken cancellationToken = default)
        {
            Calls++;
            if (RefusePairing)
            {
                throw new HackServerConnectionException("Could not pair: 401");
            }

            Paired = (serverUrl, code);
            return ValueTask.FromResult(new HackServerConnectionState(true, serverUrl, Guid.NewGuid(), Guid.NewGuid()));
        }

        public ValueTask<HackServerConnectionState> GetStateAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(HackServerConnectionState.NotConnected);

        public ValueTask<HackServerConnectionState> ConnectAsync(Uri serverUrl, string login, string password, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<HackServerConnectionState> CreateAccountAsync(Uri serverUrl, string login, string password, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask DisconnectAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public ValueTask SyncAsync(HackSyncDomain domain, HackSyncDirection direction, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<IReadOnlyList<HackSyncConflict>> GetConflictsAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask ShowConflictsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public ValueTask<HackSyncStatus> GetSyncStatusAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask SetSyncPausedAsync(bool paused, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<HackSyncStatus> SyncNowAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class PanelHarness : IAsyncDisposable
    {
        private readonly ServiceProvider _services;
        private readonly HtmlRenderer _renderer;
        private HtmlRootComponent _root;

        private PanelHarness(ServiceProvider services, HtmlRenderer renderer)
        {
            _services = services;
            _renderer = renderer;
        }

        public LocalServerPanel Panel { get; private set; } = default!;

        public HackServerConnectionState? PairedState { get; private set; }

        public static async Task<PanelHarness> RenderAsync(IAppServerConnectionGateway gateway, string lang = "en_US.UTF-8")
        {
            FakeAppEnvironmentGateway environment = new("/home/alice", "alice");
            environment.SetVariable("LANG", lang);
            IAppExecutionContext context = new MinimalAppExecutionContext(
                AppDefinitionMapper.ToManifest(new SettingsApp()),
                userId: "alice",
                homeDirectory: "/home/alice",
                environment: environment,
                serverConnection: gateway);
            ServiceProvider services = new ServiceCollection().BuildServiceProvider();
            PanelHarness harness = new(services, new HtmlRenderer(services, NullLoggerFactory.Instance));
            harness._root = await harness._renderer.Dispatcher.InvokeAsync(() => harness._renderer.RenderComponentAsync<PanelHost>(
                ParameterView.FromDictionary(new Dictionary<string, object?>
                {
                    [nameof(PanelHost.Context)] = context,
                    [nameof(PanelHost.Captured)] = (Action<LocalServerPanel>)(panel => harness.Panel = panel),
                    [nameof(PanelHost.Paired)] = (Action<HackServerConnectionState>)(state => harness.PairedState = state)
                })));
            await harness._root.QuiescenceTask;
            return harness;
        }

        public Task InvokeAsync(Func<Task> action) => _renderer.Dispatcher.InvokeAsync(action);

        public string Html() => WebUtility.HtmlDecode(_renderer.Dispatcher.InvokeAsync(() => _root.ToHtmlString()).GetAwaiter().GetResult());

        public async ValueTask DisposeAsync()
        {
            await _renderer.DisposeAsync();
            await _services.DisposeAsync();
        }
    }

    private sealed class PanelHost : ComponentBase
    {
        [Parameter]
        public IAppExecutionContext Context { get; set; } = default!;

        [Parameter]
        public Action<LocalServerPanel> Captured { get; set; } = default!;

        [Parameter]
        public Action<HackServerConnectionState> Paired { get; set; } = default!;

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<CascadingValue<IAppExecutionContext>>(0);
            builder.AddComponentParameter(1, nameof(CascadingValue<IAppExecutionContext>.Value), Context);
            builder.AddComponentParameter(2, nameof(CascadingValue<IAppExecutionContext>.ChildContent), (RenderFragment)(child =>
            {
                child.OpenComponent<LocalServerPanel>(0);
                child.AddComponentParameter(1, nameof(LocalServerPanel.OnPaired),
                    EventCallback.Factory.Create<HackServerConnectionState>(this, Paired));
                child.AddComponentReferenceCapture(2, reference => Captured((LocalServerPanel)reference));
                child.CloseComponent();
            }));
            builder.CloseComponent();
        }
    }
}
