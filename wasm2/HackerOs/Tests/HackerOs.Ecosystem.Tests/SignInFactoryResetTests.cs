using System.Globalization;
using HackerOs.AppSdk.Globalization;
using HackerOs.Ecosystem;
using HackerOs.Platform.Core.Power;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;

namespace HackerOs.Ecosystem.Tests;

/// <summary>ADR 0226: « Forgot password? » on the sign-in screen, its deployment switch and its confirmation.</summary>
public sealed class SignInFactoryResetTests
{
    [Fact]
    public void A_missing_section_offers_the_reset()
    {
        Assert.True(SignInFactoryResetSettings.Read(Configuration([])).Enabled);
        Assert.True(SignInFactoryResetSettings.Read(null).Enabled);
    }

    [Theory]
    [InlineData("false", false)]
    [InlineData("False", false)]
    [InlineData("true", true)]
    [InlineData("maybe", true)]
    public void The_deployment_switch_is_read_from_the_sign_in_section(string value, bool expected) =>
        Assert.Equal(expected, SignInFactoryResetSettings.Read(Configuration(new()
        {
            ["HackerOs:SignIn:FactoryReset:Enabled"] = value
        })).Enabled);

    [Fact]
    public void The_published_appsettings_declares_the_switch_on()
    {
        string file = Path.Combine(SolutionDirectory(), "OS", "HackerOs.Ecosystem", "wwwroot", "appsettings.json");
        IConfiguration configuration = new ConfigurationBuilder().AddJsonFile(file).Build();

        Assert.Equal("True", configuration["HackerOs:SignIn:FactoryReset:Enabled"], ignoreCase: true);
    }

    [Theory]
    [InlineData("ERASE", "ERASE", true)]
    [InlineData("  erase ", "ERASE", true)]
    [InlineData("effacer", "EFFACER", true)]
    [InlineData("ERAS", "ERASE", false)]
    [InlineData("", "ERASE", false)]
    [InlineData(null, "ERASE", false)]
    [InlineData("ERASE NOW", "ERASE", false)]
    public void Only_the_confirmation_word_unlocks_the_reset(string? typed, string word, bool expected) =>
        Assert.Equal(expected, SignInFactoryResetPrompt.IsConfirmationTyped(typed, word));

    [Fact]
    public async Task A_disabled_deployment_never_erases()
    {
        RecordingDevice device = new();
        ServiceCollection services = new();
        services.AddSingleton(new SignInFactoryResetSettings(false));
        services.AddSingleton<ISystemPowerDevice>(device);
        await using ServiceProvider provider = services.BuildServiceProvider();

        Assert.False(SignInFactoryReset.IsOffered(provider));
        await SignInFactoryReset.RunAsync(provider);

        Assert.Empty(device.Calls);
    }

    [Fact]
    public async Task An_enabled_deployment_erases_then_restarts()
    {
        RecordingDevice device = new();
        ServiceCollection services = new();
        services.AddSingleton<ISystemPowerDevice>(device);
        await using ServiceProvider provider = services.BuildServiceProvider();

        Assert.True(SignInFactoryReset.IsOffered(provider));
        await SignInFactoryReset.RunAsync(provider);

        Assert.Equal(["erase", "restart"], device.Calls);
    }

    [Fact]
    public async Task The_closed_prompt_is_only_the_link()
    {
        string html = await RenderAsync(CultureInfo.GetCultureInfo("en"), open: false);

        Assert.Contains("Forgot password?", html);
        Assert.DoesNotContain("alertdialog", html);
    }

    [Theory]
    [InlineData("en", "Reset this machine?", "Type ERASE to confirm", "Erase and restart")]
    [InlineData("fr", "Réinitialiser cette machine ?", "Tapez EFFACER pour confirmer", "Effacer et redémarrer")]
    public async Task The_open_prompt_lists_the_losses_and_keeps_the_reset_disabled(string culture, string title, string typeWord, string confirm)
    {
        string html = await RenderAsync(CultureInfo.GetCultureInfo(culture), open: true);

        Assert.Contains("role=\"alertdialog\"", html);
        Assert.Contains("aria-modal=\"true\"", html);
        Assert.Contains(title, html);
        Assert.Contains(typeWord, html);
        Assert.Equal(4, html.Split("<li", StringSplitOptions.None).Length - 1);
        // Nothing typed yet: the destructive button is disabled.
        Assert.Matches($"class=\"factory-reset-confirm\" disabled[^>]*>{confirm}<", html);
    }

    private static async Task<string> RenderAsync(CultureInfo culture, bool open)
    {
        ServiceCollection services = new();
        services.AddLogging();
        services.AddSingleton(new TestCulture(culture));
        services.AddSingleton(typeof(IStringLocalizer<>), typeof(TestCultureLocalizer<>));
        await using ServiceProvider provider = services.BuildServiceProvider();
        await using HtmlRenderer renderer = new(provider, provider.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var result = await renderer.RenderComponentAsync<SignInFactoryResetPrompt>(ParameterView.FromDictionary(
                new Dictionary<string, object?> { [nameof(SignInFactoryResetPrompt.InitiallyOpen)] = open }));
            // Accented letters are written as entities; the assertions read the text.
            return System.Net.WebUtility.HtmlDecode(result.ToHtmlString());
        });
    }

    private static IConfiguration Configuration(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    private static string SolutionDirectory()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "HackerOs.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("HackerOs.sln not found.");
    }

    private sealed record TestCulture(CultureInfo Value);

    private sealed class TestCultureLocalizer<TResource>(TestCulture culture)
        : HackStringLocalizer(HackLocalization.GetResourceManager(typeof(TResource)), () => culture.Value), IStringLocalizer<TResource>;

    private sealed class RecordingDevice : ISystemPowerDevice
    {
        public List<string> Calls { get; } = [];

        public Task RestartAsync(CancellationToken cancellationToken = default)
        {
            Calls.Add("restart");
            return Task.CompletedTask;
        }

        public Task EraseStorageAsync(CancellationToken cancellationToken = default)
        {
            Calls.Add("erase");
            return Task.CompletedTask;
        }
    }
}
