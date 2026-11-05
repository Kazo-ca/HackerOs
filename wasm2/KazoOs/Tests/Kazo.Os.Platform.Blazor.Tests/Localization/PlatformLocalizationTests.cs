using System.Net;
using Kazo.Os.AppSdk.Icons;
using Kazo.Os.Ecosystem;
using Kazo.Os.Platform.Blazor.Shell;
using Kazo.Os.Platform.Core.Globalization;
using Kazo.Os.Platform.Core.Time;
using Kazo.Os.Simulation.Abstractions.Sessions;
using Kazo.Os.Taskbar.Blazor;
using Kazo.Os.Windowing.Abstractions;
using Kazo.Os.Windowing.Blazor;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kazo.Os.Platform.Blazor.Tests.Localization;

/// <summary>
/// ADR 0104: the platform's surfaces take the session language of <see cref="SessionLocaleService"/>
/// (English by default, French once <c>LANG=fr_*</c> is applied) and its <c>LC_TIME</c> culture.
/// </summary>
public sealed class PlatformLocalizationTests
{
    private static readonly DateTimeOffset NineAm = new(2026, 10, 17, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task The_taskbar_and_its_clock_follow_an_English_session()
    {
        SessionLocaleService locale = TestLocalization.CreateSessionLocale("en_US.UTF-8");

        string text = await RenderTaskbarAsync(locale);

        Assert.Contains("aria-label=\"Desktop taskbar\"", text, StringComparison.Ordinal);
        Assert.Contains("aria-label=\"System clock, notifications, and calendar\"", text, StringComparison.Ordinal);
        Assert.Contains("aria-label=\"Notes (focused)\"", text, StringComparison.Ordinal);
        Assert.Contains(">Applications</span>", text, StringComparison.Ordinal);
        // en-US long time: 12-hour clock (ICU may put a narrow no-break space before AM).
        Assert.Matches(@"^9:00:00\sAM$", ClockText(text));
    }

    [Fact]
    public async Task The_taskbar_and_its_clock_follow_a_French_session()
    {
        SessionLocaleService locale = TestLocalization.CreateSessionLocale("fr_FR.UTF-8");

        string text = await RenderTaskbarAsync(locale);

        Assert.Contains("aria-label=\"Barre des tâches du bureau\"", text, StringComparison.Ordinal);
        Assert.Contains("aria-label=\"Horloge système, notifications et calendrier\"", text, StringComparison.Ordinal);
        Assert.Contains("aria-label=\"Notes (au premier plan)\"", text, StringComparison.Ordinal);
        Assert.Contains("aria-label=\"Session utilisateur et déconnexion\"", text, StringComparison.Ordinal);
        Assert.Equal("09:00:00", ClockText(text));
    }

    [Fact]
    public async Task The_window_chrome_follows_the_session_language()
    {
        SessionLocaleService locale = TestLocalization.CreateSessionLocale("fr_FR.UTF-8");

        string text = await RenderAsync<DesktopArea>(locale, new Dictionary<string, object?>
        {
            [nameof(DesktopArea.Windows)] = new[] { Window() },
        });

        Assert.Contains("aria-label=\"Espace de travail du bureau\"", text, StringComparison.Ordinal);
        Assert.Contains("aria-label=\"Réduire\"", text, StringComparison.Ordinal);
        Assert.Contains("aria-label=\"Agrandir\"", text, StringComparison.Ordinal);
        Assert.Contains("aria-label=\"Fermer\"", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Changing_the_session_language_changes_the_texts_read_next()
    {
        SessionLocaleService locale = TestLocalization.CreateSessionLocale("en_US.UTF-8");
        bool changed = false;
        locale.Changed += () => changed = true;

        string english = await RenderAsync<LoginProgressScreen>(locale, new Dictionary<string, object?>(), services =>
            services.AddSingleton<ILoginProgressTracker>(new FixedProgress()));
        locale.Apply(new Dictionary<string, string> { ["LANG"] = "fr_FR.UTF-8" });
        string french = await RenderAsync<LoginProgressScreen>(locale, new Dictionary<string, object?>(), services =>
            services.AddSingleton<ILoginProgressTracker>(new FixedProgress()));

        Assert.True(changed);
        Assert.Contains("Session initialization in progress", english, StringComparison.Ordinal);
        Assert.Contains("Loading Profile...", english, StringComparison.Ordinal);
        Assert.Contains("Initialisation de la session en cours", french, StringComparison.Ordinal);
        Assert.Contains("Chargement du profil...", french, StringComparison.Ordinal);
        Assert.Contains("Vérification de l'intégrité des données", french, StringComparison.Ordinal);
        // A step the screen does not know is shown as the session service named it.
        Assert.Contains("Custom step", french, StringComparison.Ordinal);
    }

    private static string ClockText(string html) =>
        System.Text.RegularExpressions.Regex.Match(html, "<span class=\"clock-text\"[^>]*>([^<]*)</span>").Groups[1].Value;

    private static async Task<string> RenderTaskbarAsync(SessionLocaleService locale)
    {
        ManualSimulationClock clock = new(NineAm, TimeSpan.FromSeconds(1));
        using TaskbarStatusSourceAdapter status = new(clock, locale);
        return await RenderAsync<Kazo.Os.Taskbar.Blazor.Taskbar>(locale, new Dictionary<string, object?>
        {
            [nameof(Kazo.Os.Taskbar.Blazor.Taskbar.WindowSource)] = new OneWindow(),
            [nameof(Kazo.Os.Taskbar.Blazor.Taskbar.Launcher)] = new TaskbarLauncherAdapter(),
            [nameof(Kazo.Os.Taskbar.Blazor.Taskbar.StatusSource)] = status,
            [nameof(Kazo.Os.Taskbar.Blazor.Taskbar.SessionCommands)] = new TaskbarSessionCommandsAdapter(() => { }),
        });
    }

    private static async Task<string> RenderAsync<TComponent>(
        SessionLocaleService locale,
        IDictionary<string, object?> parameters,
        Action<IServiceCollection>? configure = null)
        where TComponent : IComponent
    {
        ServiceCollection services = new();
        services.AddSessionLocalizer(locale);
        services.AddSingleton<IIconCatalog, IconCatalog>();
        services.AddSingleton<Microsoft.JSInterop.IJSRuntime, NoJsRuntime>();
        configure?.Invoke(services);
        await using ServiceProvider provider = services.BuildServiceProvider();
        await using HtmlRenderer renderer = new(provider, NullLoggerFactory.Instance);
        string html = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var root = await renderer.RenderComponentAsync<TComponent>(ParameterView.FromDictionary(parameters));
            return root.ToHtmlString();
        });
        return WebUtility.HtmlDecode(html);
    }

    private static WindowRuntimeState Window() =>
        new(
            WindowId.FromGuid(Guid.Parse("70000000-0000-0000-0000-000000000001")),
            "org.kazoos.notes",
            WindowOwnerId.FromGuid(Guid.Parse("80000000-0000-0000-0000-000000000001")),
            "Notes",
            null,
            new WindowBounds(20, 30, 640, 480),
            null,
            0,
            WindowVisualState.Normal,
            new WindowConstraints(true, 320, 240));

    private sealed class OneWindow : ITaskbarWindowSource
    {
        public IReadOnlyList<TaskbarWindowItem> Windows { get; } =
            [new TaskbarWindowItem(WindowId.FromGuid(Guid.NewGuid()), "Notes", null, IsFocused: true, IsMinimized: false)];

        public event Action? Changed
        {
            add { }
            remove { }
        }
    }

    private sealed class FixedProgress : ILoginProgressTracker
    {
        public LoginProgressSnapshot CurrentSnapshot { get; } =
            new("Loading Profile", 2, 4, 50, ["Ensure data integrity", "Custom step"]);

        public event Action<LoginProgressSnapshot>? ProgressChanged
        {
            add { }
            remove { }
        }

        public void SetTotalSteps(int totalSteps)
        {
        }

        public IDisposable BeginStep(string stepName) => throw new NotSupportedException();

        public void Reset()
        {
        }
    }

    /// <summary>The window chrome imports its pointer module after render; the HTML renderer never runs that.</summary>
    private sealed class NoJsRuntime : Microsoft.JSInterop.IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => throw new NotSupportedException();

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) =>
            throw new NotSupportedException();
    }
}
