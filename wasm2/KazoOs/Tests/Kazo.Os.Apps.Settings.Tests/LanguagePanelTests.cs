using System.Net;
using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk;
using Kazo.Os.AppSdk.Globalization;
using Kazo.Os.Tests.Support;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.Web.HtmlRendering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Kazo.Os.Apps.Settings.Tests;

/// <summary>
/// Settings &gt; Language (ADR 0104): the panel reads the session's locale from the Settings process,
/// writes or removes <c>~/.config/locale.conf</c>, and tells the user the change waits for the next
/// sign-in. Rendered with the real <see cref="HtmlRenderer"/> inside a cascading app context, as the
/// window host does.
/// </summary>
public sealed class LanguagePanelTests
{
    private const string Home = "/home/alice";
    private const string UserFile = Home + "/.config/locale.conf";

    [Fact]
    public async Task Choosing_a_locale_writes_LANG_to_the_user_locale_file_and_says_it_applies_at_next_sign_in()
    {
        FakeAppFileSystemGateway files = new FakeAppFileSystemGateway()
            .WithDirectory(Home)
            .WithFile("/etc/locale.conf", "LANG=en_US.UTF-8\n");
        await using PanelHarness harness = await PanelHarness.RenderAsync(files, lang: "en_US.UTF-8");

        await harness.RunAsync(panel => panel.ChooseAsync("fr_CA.UTF-8"));

        string content = await harness.ReadAsync(UserFile);
        Assert.Equal("fr_CA.UTF-8", HackLocale.ParseLocaleConf(content)["LANG"]);
        Assert.StartsWith("# " + LanguagePanel.FileHeader, content, StringComparison.Ordinal);
        Assert.Equal("fr_CA.UTF-8", harness.Panel.ChosenLocale);
        Assert.False(harness.Panel.MessageIsError);

        string html = harness.Html();
        Assert.Contains("Saved: Français (Canada) — fr_CA.UTF-8.", html, StringComparison.Ordinal);
        Assert.Contains("The change takes effect at your next sign-in.", html, StringComparison.Ordinal);
        Assert.Contains("At your next sign-in: Français (Canada) — fr_CA.UTF-8", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Following_the_system_language_deletes_the_user_locale_file()
    {
        FakeAppFileSystemGateway files = new FakeAppFileSystemGateway()
            .WithFile("/etc/locale.conf", "LANG=en_GB.UTF-8\n")
            .WithFile(UserFile, "LANG=fr_FR.UTF-8\n");
        await using PanelHarness harness = await PanelHarness.RenderAsync(files, lang: "fr_FR.UTF-8");
        Assert.Equal("fr_FR.UTF-8", harness.Panel.ChosenLocale);

        await harness.RunAsync(panel => panel.ChooseAsync(null));

        Assert.False(files.Exists(UserFile));
        Assert.Null(harness.Panel.ChosenLocale);
        string html = harness.Html();
        Assert.Contains("Enregistré : vous suivez la langue du système (English (United Kingdom) — en_GB.UTF-8).", html, StringComparison.Ordinal);
        Assert.Contains("Le changement prendra effet à votre prochaine connexion.", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Following_the_system_language_keeps_the_other_variables_of_the_file()
    {
        FakeAppFileSystemGateway files = new FakeAppFileSystemGateway()
            .WithFile(UserFile, "LANG=fr_FR.UTF-8\nLC_TIME=en_GB.UTF-8\n");
        await using PanelHarness harness = await PanelHarness.RenderAsync(files, lang: "fr_FR.UTF-8");

        await harness.RunAsync(panel => panel.ChooseAsync(null));

        IReadOnlyDictionary<string, string> variables = HackLocale.ParseLocaleConf(await harness.ReadAsync(UserFile));
        Assert.False(variables.ContainsKey("LANG"));
        Assert.Equal("en_GB.UTF-8", variables["LC_TIME"]);
    }

    [Fact]
    public async Task An_unknown_locale_is_refused_and_nothing_is_written()
    {
        FakeAppFileSystemGateway files = new FakeAppFileSystemGateway().WithDirectory(Home);
        await using PanelHarness harness = await PanelHarness.RenderAsync(files, lang: "en_US.UTF-8");

        await harness.RunAsync(panel => panel.ChooseAsync("xx_YY.UTF-8"));

        Assert.False(files.Exists(UserFile));
        Assert.True(harness.Panel.MessageIsError);
        Assert.Contains("Unknown locale: xx_YY.UTF-8", harness.Html(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_panel_shows_the_session_locale_the_choices_and_the_read_only_system_locale()
    {
        FakeAppFileSystemGateway files = new FakeAppFileSystemGateway()
            .WithDirectory(Home)
            .WithFile("/etc/locale.conf", "# system\nLANG=en_CA.UTF-8\n");
        await using PanelHarness harness = await PanelHarness.RenderAsync(files, lang: "en_US.UTF-8");

        string html = harness.Html();
        Assert.Contains("This session", html, StringComparison.Ordinal);
        // Scoped CSS adds a b-xxxx attribute to opening tags: match from the end of the tag.
        Assert.Contains(">en_US.UTF-8</code>", html, StringComparison.Ordinal);
        Assert.Contains("Follow the system language", html, StringComparison.Ordinal);
        Assert.Contains("Currently English (Canada) — en_CA.UTF-8", html, StringComparison.Ordinal);
        foreach (string locale in HackLocale.AvailableLocales)
        {
            Assert.Contains($">{locale}</span>", html, StringComparison.Ordinal);
        }

        Assert.Contains("Français (France)", html, StringComparison.Ordinal);
        Assert.Contains(">en_CA.UTF-8</code>", html, StringComparison.Ordinal);
        Assert.Contains("<code>sudo localectl set-locale LANG=fr_CA.UTF-8</code>", html, StringComparison.Ordinal);
        // No user file: "follow the system" is the checked choice.
        Assert.Null(harness.Panel.ChosenLocale);
    }

    [Fact]
    public async Task In_a_French_session_the_panel_speaks_French()
    {
        FakeAppFileSystemGateway files = new FakeAppFileSystemGateway()
            .WithDirectory(Home)
            .WithFile("/etc/locale.conf", "LANG=en_US.UTF-8\n");
        await using PanelHarness harness = await PanelHarness.RenderAsync(files, lang: "fr_CA.UTF-8");

        string html = harness.Html();
        Assert.Contains("Cette session", html, StringComparison.Ordinal);
        Assert.Contains("Votre langue", html, StringComparison.Ordinal);
        Assert.Contains("Suivre la langue du système", html, StringComparison.Ordinal);
        Assert.Contains("Langue du système", html, StringComparison.Ordinal);
        Assert.Contains("Seul un administrateur peut la changer", html, StringComparison.Ordinal);
        // Scoped CSS adds a b-xxxx attribute to the opening tag, so only the closing tag is matched.
        Assert.Contains(">Français</dd>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Follow the system language", html, StringComparison.Ordinal);
    }

    /// <summary>Hosts one <see cref="LanguagePanel"/> in a static renderer, under a cascading app context.</summary>
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

        public LanguagePanel Panel { get; private set; } = default!;

        public static async Task<PanelHarness> RenderAsync(FakeAppFileSystemGateway files, string lang)
        {
            FakeAppEnvironmentGateway environment = new(Home, "alice");
            environment.SetVariable("LANG", lang);
            IAppExecutionContext context = new MinimalAppExecutionContext(
                AppDefinitionMapper.ToManifest(new SettingsApp()),
                fileSystem: files,
                userId: "alice",
                homeDirectory: Home,
                environment: environment);
            ServiceProvider services = new ServiceCollection().BuildServiceProvider();
            PanelHarness harness = new(services, new HtmlRenderer(services, NullLoggerFactory.Instance), context);
            harness._root = await harness._renderer.Dispatcher.InvokeAsync(() => harness._renderer.RenderComponentAsync<PanelHost>(
                ParameterView.FromDictionary(new Dictionary<string, object?>
                {
                    [nameof(PanelHost.Context)] = context,
                    [nameof(PanelHost.Captured)] = (Action<LanguagePanel>)(panel => harness.Panel = panel)
                })));
            await harness._root.QuiescenceTask;
            return harness;
        }

        /// <summary>Runs a panel action on the renderer's dispatcher, inside the app's ambient scope (as a click does).</summary>
        public Task RunAsync(Func<LanguagePanel, Task> action) =>
            _renderer.Dispatcher.InvokeAsync(async () =>
            {
                using (HackExecutionContext.Enter(_context))
                {
                    await action(Panel);
                }

            });

        /// <summary>The current HTML, decoded so accents and quotes read as text.</summary>
        public string Html() => WebUtility.HtmlDecode(_renderer.Dispatcher.InvokeAsync(() => _root.ToHtmlString()).GetAwaiter().GetResult());

        public async Task<string> ReadAsync(string path)
        {
            using (HackExecutionContext.Enter(_context))
            {
                return await Kazo.Os.AppSdk.IO.HackFile.ReadAllTextAsync(path);
            }
        }

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
        public Action<LanguagePanel> Captured { get; set; } = default!;

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<CascadingValue<IAppExecutionContext>>(0);
            builder.AddComponentParameter(1, nameof(CascadingValue<IAppExecutionContext>.Value), Context);
            builder.AddComponentParameter(2, nameof(CascadingValue<IAppExecutionContext>.ChildContent), (RenderFragment)(child =>
            {
                child.OpenComponent<LanguagePanel>(0);
                child.AddComponentReferenceCapture(1, reference => Captured((LanguagePanel)reference));
                child.CloseComponent();
            }));
            builder.CloseComponent();
        }
    }
}
