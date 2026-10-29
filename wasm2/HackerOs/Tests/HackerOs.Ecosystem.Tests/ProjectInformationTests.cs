using System.Globalization;
using System.Text.RegularExpressions;
using HackerOs.App.Abstractions;
using HackerOs.AppSdk.Globalization;
using HackerOs.Ecosystem;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;

namespace HackerOs.Ecosystem.Tests;

/// <summary>Checks the actual rendered product information, including exact public-host matching.</summary>
public sealed class ProjectInformationTests
{
    [Theory]
    [InlineData("https://hackos.kazo.ca/", true, true)]
    [InlineData("https://HACKOS.KAZO.CA/login", true, true)]
    [InlineData("https://hackos.kazo.ca/", false, false)]
    [InlineData("https://hackos.kazo.ca.example.com/", true, false)]
    [InlineData("http://localhost:7240/?host=hackos.kazo.ca", true, false)]
    [InlineData("https://kazo-ca.github.io/HackerOs/", true, false)]
    public async Task Migration_notice_is_only_shown_on_the_public_authentication_pages(string url, bool enabled, bool visible)
    {
        ServiceCollection services = new();
        services.AddLogging();
        services.AddSingleton<NavigationManager>(new TestNavigation(url));
        services.AddLocalization();
        await using ServiceProvider provider = services.BuildServiceProvider();
        await using HtmlRenderer renderer = new(provider, provider.GetRequiredService<ILoggerFactory>());
        string html = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var result = await renderer.RenderComponentAsync<ProjectInformation>(ParameterView.FromDictionary(
                new Dictionary<string, object?> { [nameof(ProjectInformation.ShowLegacyNotice)] = enabled }));
            return result.ToHtmlString();
        });
        Assert.Equal(visible, html.Contains("https://hackos.kazo.ca/legacy/", StringComparison.Ordinal));
        Assert.Contains("https://github.com/Kazo-ca/HackerOs", html);
        Assert.Contains($"HackerOS {HackerOsSdk.Version}", html);
    }

    [Theory]
    [InlineData("en", "Author:", "GitHub project:")]
    [InlineData("fr", "Auteur :", "Projet GitHub :")]
    public async Task Credits_link_the_author_and_the_repository_with_localized_labels(string culture, string author, string project)
    {
        string html = await RenderAsync("https://kazo-ca.github.io/HackerOs/", CultureInfo.GetCultureInfo(culture));

        // Scoped-CSS attributes (b-…) follow the authored ones, so match each link up to its closing bracket.
        Assert.Matches($@"{Regex.Escape(author)} <a class=""project-author"" href=""https://kazo\.ca"" target=""_blank"" rel=""noopener noreferrer""[^>]*>Benoit Robin</a>", html);
        Assert.Contains(project, html);
        Assert.Matches(@"<a class=""project-repository"" href=""https://github\.com/Kazo-ca/HackerOs"" target=""_blank"" rel=""noopener noreferrer""[^>]*>", html);
        Assert.Contains("Kazo-ca/HackerOs", html);
        Assert.Contains("aria-hidden=\"true\"", html);
        // The "GitHub project" line replaces the raw URL as link text (ADR 0187, section 7).
        Assert.DoesNotContain(">https://github.com/Kazo-ca/HackerOs<", html);
    }

    private static async Task<string> RenderAsync(string url, CultureInfo culture)
    {
        ServiceCollection services = new();
        services.AddLogging();
        services.AddSingleton<NavigationManager>(new TestNavigation(url));
        // The host binds its texts to the system language before sign-in, as this fixed culture does.
        services.AddSingleton(new TestCulture(culture));
        services.AddSingleton(typeof(IStringLocalizer<>), typeof(TestCultureLocalizer<>));
        await using ServiceProvider provider = services.BuildServiceProvider();
        await using HtmlRenderer renderer = new(provider, provider.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var result = await renderer.RenderComponentAsync<ProjectInformation>(ParameterView.Empty);
            return result.ToHtmlString();
        });
    }

    private sealed record TestCulture(CultureInfo Value);

    private sealed class TestCultureLocalizer<TResource>(TestCulture culture)
        : HackStringLocalizer(HackLocalization.GetResourceManager(typeof(TResource)), () => culture.Value), IStringLocalizer<TResource>;

    private sealed class TestNavigation : NavigationManager
    {
        internal TestNavigation(string url) => Initialize(new Uri(url).GetLeftPart(UriPartial.Authority) + "/", new Uri(url).AbsoluteUri);
        protected override void NavigateToCore(string uri, bool forceLoad) => throw new NotSupportedException();
    }

}
