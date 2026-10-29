using System.Globalization;
using System.Net;
using System.Resources;
using System.Text.RegularExpressions;
using HackerOs.AppSdk.Icons;
using HackerOs.Windowing.Abstractions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;

namespace HackerOs.Taskbar.Blazor.Tests;

/// <summary>
/// ADR 0104: the taskbar renders in English for a host that registers no localization, in the
/// host's language when it registers an <see cref="IStringLocalizer{T}"/>, and formats its clock
/// with the status source's culture.
/// </summary>
public sealed class TaskbarLocalizationTests
{
    private static readonly DateTimeOffset NineAm = new(2026, 10, 17, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task A_host_without_localization_gets_the_English_texts_and_a_24_hour_clock()
    {
        string html = await RenderAsync(localizer: null, new Status(CultureInfo.InvariantCulture));

        Assert.Contains("aria-label=\"Desktop taskbar\"", html, StringComparison.Ordinal);
        Assert.Contains("aria-label=\"App launcher\"", html, StringComparison.Ordinal);
        Assert.Contains("aria-label=\"Notes (focused)\"", html, StringComparison.Ordinal);
        Assert.Contains(">Applications</span>", html, StringComparison.Ordinal);
        Assert.Equal("09:00:00", ClockText(html));
    }

    [Fact]
    public async Task A_French_host_gets_the_French_texts()
    {
        string html = await RenderAsync(typeof(FrenchStringLocalizer<>), new Status(CultureInfo.GetCultureInfo("fr-FR")));

        Assert.Contains("aria-label=\"Barre des tâches du bureau\"", html, StringComparison.Ordinal);
        Assert.Contains("aria-label=\"Lanceur d'applications\"", html, StringComparison.Ordinal);
        Assert.Contains("aria-label=\"Notes (au premier plan)\"", html, StringComparison.Ordinal);
        Assert.Contains("aria-label=\"Applications en cours d'exécution\"", html, StringComparison.Ordinal);
        Assert.Equal("09:00:00", ClockText(html));
    }

    [Fact]
    public async Task The_clock_uses_the_long_time_pattern_of_the_source_culture()
    {
        string html = await RenderAsync(localizer: null, new Status(CultureInfo.GetCultureInfo("en-US")));

        Assert.Matches(@"^9:00:00\sAM$", ClockText(html));
    }

    [Fact]
    public async Task A_host_label_still_wins_over_the_package_text()
    {
        string html = await RenderAsync(
            typeof(FrenchStringLocalizer<>),
            new Status(CultureInfo.InvariantCulture),
            new TaskbarOptions { LauncherLabel = "Apps" });

        Assert.Contains(">Apps</span>", html, StringComparison.Ordinal);
    }

    private static string ClockText(string html) =>
        Regex.Match(html, "<span class=\"clock-text\"[^>]*>([^<]*)</span>").Groups[1].Value;

    [Fact]
    public async Task Unseen_changes_put_a_dot_on_the_launcher_and_say_so_in_its_name()
    {
        string plain = await RenderAsync(localizer: null, new Status(CultureInfo.InvariantCulture));
        string english = await RenderAsync(localizer: null, new Status(CultureInfo.InvariantCulture), launcher: new Launcher(hasAttention: true));
        string french = await RenderAsync(typeof(FrenchStringLocalizer<>), new Status(CultureInfo.InvariantCulture), launcher: new Launcher(hasAttention: true));

        Assert.DoesNotContain("launcher-attention", plain, StringComparison.Ordinal);
        Assert.Contains("class=\"launcher-attention\" aria-hidden=\"true\"", english, StringComparison.Ordinal);
        Assert.Contains("aria-label=\"App launcher, new changes to see\"", english, StringComparison.Ordinal);
        Assert.Contains("aria-label=\"Lanceur d'applications, nouveautés à voir\"", french, StringComparison.Ordinal);
    }

    private static async Task<string> RenderAsync(Type? localizer, ITaskbarStatusSource status, TaskbarOptions? options = null, Launcher? launcher = null)
    {
        ServiceCollection services = new();
        services.AddSingleton<IIconCatalog, IconCatalog>();
        if (localizer is not null)
        {
            services.AddSingleton(typeof(IStringLocalizer<>), localizer);
        }

        await using ServiceProvider provider = services.BuildServiceProvider();
        await using HtmlRenderer renderer = new(provider, NullLoggerFactory.Instance);
        Dictionary<string, object?> parameters = new()
        {
            [nameof(Taskbar.WindowSource)] = new OneWindow(),
            [nameof(Taskbar.Launcher)] = launcher ?? new Launcher(),
            [nameof(Taskbar.StatusSource)] = status,
        };
        if (options is not null)
        {
            parameters[nameof(Taskbar.Options)] = options;
        }

        string html = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var root = await renderer.RenderComponentAsync<Taskbar>(ParameterView.FromDictionary(parameters));
            return root.ToHtmlString();
        });
        return WebUtility.HtmlDecode(html);
    }

    /// <summary>A host localizer answering in French from the package's own <c>.resx</c>.</summary>
    private sealed class FrenchStringLocalizer<TResource> : IStringLocalizer<TResource>
    {
        private static readonly ResourceManager Resources = new(
            $"{typeof(TResource).Assembly.GetName().Name}.Resources.Strings", typeof(TResource).Assembly);

        private static readonly CultureInfo French = CultureInfo.GetCultureInfo("fr");

        public LocalizedString this[string name] => new(name, Resources.GetString(name, French) ?? name);

        public LocalizedString this[string name, params object[] arguments] =>
            new(name, string.Format(French, Resources.GetString(name, French) ?? name, arguments));

        public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures) => [];
    }

    private sealed class Status(CultureInfo culture) : ITaskbarStatusSource
    {
        public DateTimeOffset UtcNow => NineAm;

        public CultureInfo FormatCulture => culture;

        public event Action? Changed
        {
            add { }
            remove { }
        }
    }

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

    private sealed class Launcher(bool hasAttention = false) : ITaskbarLauncher
    {
        public bool IsOpen => false;

        public bool HasAttention => hasAttention;

        public event Action? Changed
        {
            add { }
            remove { }
        }

        public void Toggle()
        {
        }
    }
}
